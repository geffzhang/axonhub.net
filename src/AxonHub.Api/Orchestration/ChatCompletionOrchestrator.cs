using AxonHub.Api.Persistence;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using Orleans;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of orchestrator.ChatCompletionOrchestrator.Process
/// (internal/server/orchestrator/orchestrator.go:182-260) for the chat slice:
/// select candidates from the model's pool, then run the pipeline with retry,
/// timeouts and per-channel metrics wired in.
/// </summary>
/// <remarks>
/// The reference selects candidates in an inbound middleware; the port selects
/// them up front because the candidate list is a grain call, then hands the list
/// to <see cref="PersistentOutboundTransformer"/>. Both paths put the same list
/// in front of the same pipeline.
/// </remarks>
public sealed class ChatCompletionOrchestrator
{
    private readonly IGrainFactory _grainFactory;
    private readonly IInboundTransformer _inbound;
    private readonly IExecutor _executor;
    private readonly IChannelMetricsRecorder _metrics;
    private readonly RetryPolicy _retryPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly IRequestChainRecorder? _requestChainRecorder;

    public ChatCompletionOrchestrator(
        IGrainFactory grainFactory,
        IInboundTransformer inbound,
        IExecutor executor,
        IChannelMetricsRecorder metrics,
        RetryPolicy retryPolicy,
        TimeProvider timeProvider,
        IRequestChainRecorder? requestChainRecorder = null)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _grainFactory = grainFactory;
        _inbound = inbound;
        _executor = executor;
        _metrics = metrics;
        _retryPolicy = retryPolicy;
        _timeProvider = timeProvider;
        _requestChainRecorder = requestChainRecorder;
    }

    /// <summary>The inbound transformer, used by the endpoint to map failures.</summary>
    public IInboundTransformer Inbound => _inbound;

    public async Task<ChatCompletionResult> ProcessAsync(
        HttpRequestData request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Read the requested model the same way the reference's selectCandidates
        // middleware does: after the inbound transform, before any retry.
        var llmRequest = _inbound.TransformRequest(request);
        var model = llmRequest.Model;

        var candidates = await _grainFactory
            .GetGrain<IChannelPoolGrain>(model)
            .SelectAsync(_timeProvider.GetUtcNow(), trackSelection: true, cancellationToken);

        if (candidates.Length == 0)
        {
            throw TransformerErrors.InvalidModel(model);
        }

        var outbound = new PersistentOutboundTransformer(candidates, _timeProvider, _metrics);

        var options = new PipelineOptions
        {
            MaxChannelRetries = _retryPolicy.Enabled ? _retryPolicy.MaxChannelRetries : 0,
            MaxSameChannelRetries = _retryPolicy.Enabled ? _retryPolicy.MaxSingleChannelRetries : 0,
            RetryDelay = _retryPolicy.Enabled ? _retryPolicy.RetryDelay : TimeSpan.Zero,
            EmptyResponseDetection = _retryPolicy.Enabled && _retryPolicy.EmptyResponseDetection,
            StreamFirstEventTimeout = _retryPolicy.Enabled ? _retryPolicy.StreamFirstEventTimeout : TimeSpan.Zero,
            NonStreamTimeout = _retryPolicy.Enabled ? _retryPolicy.NonStreamTimeout : TimeSpan.Zero,
        };
        options.Middlewares.Add(new ChannelMetricsMiddleware(
            outbound,
            _metrics,
            _timeProvider,
            request.Body,
            cancellationToken));

        // Registered after the metrics middleware so it runs first on the way
        // out, matching the reference order (persistRequestExecution is the last
        // outbound middleware).
        RequestChainPersistenceMiddleware? persistence = null;
        if (_requestChainRecorder is not null)
        {
            persistence = new RequestChainPersistenceMiddleware(
                _requestChainRecorder,
                outbound,
                request,
                _timeProvider,
                cancellationToken);
            options.Middlewares.Add(persistence);
        }

        var pipeline = new Pipeline(_executor, _inbound, outbound, options);

        PipelineResult result;
        try
        {
            result = pipeline.Process(request, cancellationToken);
        }
        catch (Exception ex)
        {
            // Port of orchestrator.Process's error branch: mark the last
            // execution and the request canceled (client cancellation) or failed.
            persistence?.FinalizeFailure(ex);
            throw;
        }

        return result.Stream
            ? new ChatCompletionResult { Stream = result.EventStream }
            : new ChatCompletionResult { Response = result.Response };
    }
}

/// <summary>Port of orchestrator.ChatCompletionResult (chat subset).</summary>
public sealed class ChatCompletionResult
{
    public HttpResponseData? Response { get; init; }

    public IStream<StreamEvent>? Stream { get; init; }
}
