using AxonHub.Api.Auth;
using AxonHub.Api.Billing;
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
    private readonly IUpstreamErrorPolicyApplier _upstreamErrorPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly IRequestChainRecorder? _requestChainRecorder;
    private readonly IUsageCostService? _usageCostService;
    private readonly ChannelRequestTracker _rateLimitTracker;
    private readonly ChannelLimiterManager _channelLimiterManager;

    public ChatCompletionOrchestrator(
        IGrainFactory grainFactory,
        IInboundTransformer inbound,
        IExecutor executor,
        IChannelMetricsRecorder metrics,
        IRetryPolicyProvider retryPolicyProvider,
        TimeProvider timeProvider,
        IRequestChainRecorder? requestChainRecorder = null,
        IUpstreamErrorPolicyApplier? upstreamErrorPolicy = null,
        ChannelRequestTracker? rateLimitTracker = null,
        ChannelLimiterManager? channelLimiterManager = null,
        IUsageCostService? usageCostService = null)
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
        _upstreamErrorPolicy = upstreamErrorPolicy ?? new UpstreamErrorPolicyApplier(retryPolicyProvider);
        _timeProvider = timeProvider;
        _requestChainRecorder = requestChainRecorder;
        _usageCostService = usageCostService;

        // The reference's orchestrator and load balancer share one tracker and
        // one limiter manager per process. Production registers both as
        // singletons (Program.cs) so the pool grain scores against the same
        // state the middlewares mutate; the defaults only serve direct
        // constructions in tests.
        _rateLimitTracker = rateLimitTracker ?? new ChannelRequestTracker(timeProvider);
        _channelLimiterManager = channelLimiterManager ?? new ChannelLimiterManager();
    }

    /// <summary>The inbound transformer, used by the endpoint to map failures.</summary>
    public IInboundTransformer Inbound => _inbound;

    /// <summary>
    /// Returns an orchestrator that speaks another client protocol (for example
    /// Anthropic Messages) while sharing this one's dependencies, mirroring the
    /// reference's per-route handler that swaps the inbound transformer
    /// (internal/server/api/anthropic.go: CreateMessage).
    /// </summary>
    public ChatCompletionOrchestrator WithInbound(IInboundTransformer inbound)
    {
        ArgumentNullException.ThrowIfNull(inbound);

        return new ChatCompletionOrchestrator(
            _grainFactory,
            inbound,
            _executor,
            _metrics,
            _retryPolicyProvider,
            _timeProvider,
            _requestChainRecorder,
            _upstreamErrorPolicy,
            _rateLimitTracker,
            _channelLimiterManager,
            _usageCostService);
    }

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

    /// <summary>
    /// Port of api.transformOrchestratorError (internal/server/api/upstream_error_policy.go:19-33):
    /// classify a transport failure, apply the upstream error policy, then let the
    /// inbound transformer build the client envelope. The reference's quota
    /// conversion has no port yet (quota is C014).
    /// </summary>
    public async Task<HttpResponseData> TransformErrorAsync(
        Exception error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);

        var classified = UpstreamTransportErrors.Classify(error);
        var rewritten = await _upstreamErrorPolicy
            .ApplyAsync(classified, cancellationToken)
            .ConfigureAwait(false);

        return _inbound.TransformError(rewritten);
    }

    /// <summary>
    /// Port of api.upstreamErrorStream.Err: what the SSE writer reports when the
    /// client-facing stream ends with an error.
    /// </summary>
    public Task<Exception?> TransformStreamErrorAsync(
        Exception error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        return _upstreamErrorPolicy.ApplyToStreamErrorAsync(error, cancellationToken);
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
        // or a single candidate when retries are disabled) and switches the
        // scoring chain by the configured strategy.
        var retryPolicy = await _retryPolicyProvider
            .GetRetryPolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        var topK = retryPolicy.Enabled ? 1 + retryPolicy.MaxChannelRetries : 1;

        var candidates = await _grainFactory
            .GetGrain<IChannelPoolGrain>(model)
            .SelectAsync(
                _timeProvider.GetUtcNow(),
                trackSelection: true,
                topK,
                retryPolicy.LoadBalancerStrategy,
                cancellationToken);

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

        // Channel request rewriting runs after the outbound transform (so the
        // body it patches is the provider body) and before persistence (so the
        // stored request execution sees the rewritten body and headers), the
        // same order as override.go's registration in orchestrator.go.
        options.Middlewares.Add(new ChannelOverrideBodyMiddleware(outbound));
        options.Middlewares.Add(new ChannelOverrideHeaderMiddleware(outbound));

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
                identity,
                _usageCostService);
            options.Middlewares.Add(persistence);
        }

        // Per-channel admission, then RPM admission, then TPM/cooldown
        // tracking — the reference registers them after the persistence
        // middleware (orchestrator.go:298-304). The concurrency limiter runs
        // first so a queue rejection never consumes an RPM slot.
        options.Middlewares.Add(new ChannelLimiterMiddleware(
            outbound,
            _channelLimiterManager,
            cancellationToken));
        options.Middlewares.Add(new RateLimitAdmissionMiddleware(outbound, _rateLimitTracker));
        options.Middlewares.Add(new RateLimitTrackingMiddleware(outbound, _rateLimitTracker, _timeProvider));

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
