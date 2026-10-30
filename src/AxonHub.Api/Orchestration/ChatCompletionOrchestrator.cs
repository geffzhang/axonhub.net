using AxonHub.Api.Auth;
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
    private readonly IRetryPolicyProvider _retryPolicyProvider;
    private readonly TimeProvider _timeProvider;
    private readonly IRequestChainRecorder? _requestChainRecorder;

    public ChatCompletionOrchestrator(
        IGrainFactory grainFactory,
        IInboundTransformer inbound,
        IExecutor executor,
        IChannelMetricsRecorder metrics,
        IRetryPolicyProvider retryPolicyProvider,
        TimeProvider timeProvider,
        IRequestChainRecorder? requestChainRecorder = null)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(retryPolicyProvider);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _grainFactory = grainFactory;
        _inbound = inbound;
        _executor = executor;
        _metrics = metrics;
        _retryPolicyProvider = retryPolicyProvider;
        _timeProvider = timeProvider;
        _requestChainRecorder = requestChainRecorder;
    }

    /// <summary>The inbound transformer, used by the endpoint to map failures.</summary>
    public IInboundTransformer Inbound => _inbound;

    /// <summary>
    /// Port of middleware/response_headers.go: persists the masked downstream
    /// response headers once the handler has written the response. The reference
    /// captures them at the first write; ASP.NET freezes headers after that, so
    /// a snapshot taken after the handler carries the same value.
    /// </summary>
    public void RecordResponseHeaders(long requestId, IReadOnlyDictionary<string, List<string>>? headers)
    {
        if (_requestChainRecorder is null || requestId == 0 || headers is null || headers.Count == 0)
        {
            return;
        }

        try
        {
            _requestChainRecorder.UpdateRequestResponseHeaders(
                requestId,
                RequestChainPayloads.MaskedHeadersJson(headers),
                _timeProvider.GetUtcNow().UtcDateTime);
        }
        catch (Exception)
        {
            // A header write must never turn a delivered response into an error.
        }
    }

    public Task<ChatCompletionResult> ProcessAsync(
        HttpRequestData request,
        CancellationToken cancellationToken = default)
        => ProcessAsync(request, onRequestCreated: null, cancellationToken);

    /// <summary>
    /// Runs one client request; <paramref name="onRequestCreated"/> receives the
    /// request row id as soon as it exists, so the HTTP boundary can write the
    /// downstream response headers (middleware/response_headers.go).
    /// </summary>
    public Task<ChatCompletionResult> ProcessAsync(
        HttpRequestData request,
        Action<long>? onRequestCreated,
        CancellationToken cancellationToken = default)
        => ProcessAsync(request, RequestIdentity.Default, onRequestCreated, cancellationToken);

    /// <summary>
    /// Runs one authenticated client request. The identity carries the API key
    /// and project the reference reads back from the request context when it
    /// creates request, execution and usage-log rows.
    /// </summary>
    public async Task<ChatCompletionResult> ProcessAsync(
        HttpRequestData request,
        RequestIdentity identity,
        Action<long>? onRequestCreated,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(identity);

        // Read the requested model the same way the reference's selectCandidates
        // middleware does: after the inbound transform, before any retry.
        var llmRequest = _inbound.TransformRequest(request);
        var model = llmRequest.Model;

        // orchestrator.Process reads the policy once per request; the load
        // balancer derives top-K from the same values (1 + MaxChannelRetries,
        // or a single candidate when retries are disabled).
        var retryPolicy = await _retryPolicyProvider
            .GetRetryPolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        var topK = retryPolicy.Enabled ? 1 + retryPolicy.MaxChannelRetries : 1;

        var candidates = await _grainFactory
            .GetGrain<IChannelPoolGrain>(model)
            .SelectAsync(_timeProvider.GetUtcNow(), trackSelection: true, topK, cancellationToken);

        if (candidates.Length == 0)
        {
            throw TransformerErrors.InvalidModel(model);
        }

        var outbound = new PersistentOutboundTransformer(candidates, _timeProvider, _metrics);

        var options = new PipelineOptions
        {
            MaxChannelRetries = retryPolicy.Enabled ? retryPolicy.MaxChannelRetries : 0,
            MaxSameChannelRetries = retryPolicy.Enabled ? retryPolicy.MaxSingleChannelRetries : 0,
            RetryDelay = retryPolicy.Enabled ? retryPolicy.RetryDelay : TimeSpan.Zero,
            EmptyResponseDetection = retryPolicy.Enabled && retryPolicy.EmptyResponseDetection,
            StreamFirstEventTimeout = retryPolicy.Enabled ? retryPolicy.StreamFirstEventTimeout : TimeSpan.Zero,
            NonStreamTimeout = retryPolicy.Enabled ? retryPolicy.NonStreamTimeout : TimeSpan.Zero,
        };
        // One performance state per client request, shared by the metrics
        // middleware and the persistent-stream counterpart so both mark the
        // same per-attempt record (PersistenceState.Perf in the reference).
        var performance = new ChannelPerformanceState();
        options.Middlewares.Add(new ChannelMetricsMiddleware(
            outbound,
            _metrics,
            performance,
            _timeProvider));

        // Registered after the metrics middleware so it runs first on the way
        // out, matching the reference order (persistRequestExecution is the last
        // outbound middleware).
        RequestChainPersistenceMiddleware? persistence = null;
        if (_requestChainRecorder is not null)
        {
            persistence = new RequestChainPersistenceMiddleware(
                _requestChainRecorder,
                outbound,
                _inbound,
                request,
                _timeProvider,
                cancellationToken,
                onRequestCreated,
                performance,
                identity);
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
