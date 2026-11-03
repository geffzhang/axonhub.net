using AxonHub.Api.Auth;
using AxonHub.Api.Billing;
using AxonHub.Api.Persistence;
using AxonHub.Api.PromptProtection;
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
    private readonly IPassThroughSettingsProvider _passThroughSettings;
    private readonly IPromptProtectionRuleProvider? _promptProtectionRules;
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
        IUsageCostService? usageCostService = null,
        IPassThroughSettingsProvider? passThroughSettings = null,
        IPromptProtectionRuleProvider? promptProtectionRules = null)
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

        // The reference always mounts protectPrompts; without a rule service
        // the state holds no protector and the middleware is a no-op.
        _promptProtectionRules = promptProtectionRules;

        // Without a system store the reference falls back to the channel-level
        // setting only; both flags off reproduces that.
        _passThroughSettings = passThroughSettings
            ?? new StaticPassThroughSettingsProvider(PassThroughSettings.Disabled);

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
            _usageCostService,
            _passThroughSettings,
            _promptProtectionRules);
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

        // The reference reads the pass-through flags lazily inside each
        // middleware; the port's hooks are synchronous, so one snapshot per
        // request serves all of them (C091).
        var passThroughSettings = await _passThroughSettings
            .GetAsync(cancellationToken)
            .ConfigureAwait(false);

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

        return await RunPipelineAsync(
                _inbound,
                request,
                candidates,
                identity,
                onRequestCreated,
                retryPolicy,
                passThroughSettings,
                includeRateLimits: true,
                ensureUsage: false,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Port of the tester's per-request ChatCompletionOrchestrator.Process call
    /// (internal/server/orchestrator/tester.go): the same orchestrator with a
    /// <c>SpecifiedChannelSelector</c>, the tester's inbound transformer and
    /// <c>stream.EnsureUsage</c> as a global middleware.
    /// </summary>
    /// <remarks>
    /// The reference's selector is an inbound middleware, so its errors (channel
    /// lookup failures, unsupported models) surface through the same
    /// <c>inbound.TransformError</c> path as pipeline failures; the port runs the
    /// same factory before the pipeline and lets its exceptions propagate to the
    /// caller, which applies the same transform. The factory is asynchronous
    /// because the tester's selector re-reads the channel from the store on
    /// every attempt (candidates.go:SpecifiedChannelSelector.Select).
    /// </remarks>
    public async Task<ChatCompletionResult> ProcessSpecifiedAsync(
        IInboundTransformer inbound,
        HttpRequestData request,
        Func<LlmRequest, CancellationToken, Task<ChannelCandidate?>> candidateFactory,
        RequestIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidateFactory);
        ArgumentNullException.ThrowIfNull(identity);

        var llmRequest = inbound.TransformRequest(request);

        // Same reads as ProcessAsync: one policy and one pass-through snapshot
        // per request.
        var retryPolicy = await _retryPolicyProvider
            .GetRetryPolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        var passThroughSettings = await _passThroughSettings
            .GetAsync(cancellationToken)
            .ConfigureAwait(false);

        var candidate = await candidateFactory(llmRequest, cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null)
        {
            // select_candidates.go: the selector's empty list becomes
            // ErrInvalidModel wrapped with the request model.
            throw TransformerErrors.InvalidModel(llmRequest.Model);
        }

        // The tester's processor leaves the rate limit tracker nil, so
        // withRateLimitAdmission/withRateLimitTracking are no-ops there; the
        // concurrency limiter and the rest of the chain still run.
        return await RunPipelineAsync(
                inbound,
                request,
                [candidate],
                identity,
                onRequestCreated: null,
                retryPolicy,
                passThroughSettings,
                includeRateLimits: false,
                ensureUsage: true,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<ChatCompletionResult> RunPipelineAsync(
        IInboundTransformer inbound,
        HttpRequestData request,
        ChannelCandidate[] candidates,
        RequestIdentity identity,
        Action<long>? onRequestCreated,
        RetryPolicy retryPolicy,
        PassThroughSettings passThroughSettings,
        bool includeRateLimits,
        bool ensureUsage,
        CancellationToken cancellationToken)
    {
        // Codex OAuth channels refresh their token through the shared executor;
        // the reference uses the channel's proxy-aware HTTP client instead
        // (proxy configuration is not ported, C094).
        var outbound = new PersistentOutboundTransformer(
            candidates,
            _timeProvider,
            _metrics,
            (candidate, request, now) => ChannelOutboundFactory.Create(
                candidate,
                request,
                now,
                _executor));

        // orchestrator.go mounts injectPrompts then protectPrompts as inbound
        // middlewares; the reference reads the enabled rules from its live
        // cache there. The port takes the snapshot before building the pipeline
        // and ProtectPromptsMiddleware applies it inside the inbound phase, so
        // no provider body is built from unprotected prompts.
        var promptProtecter = await LoadPromptProtecterAsync(cancellationToken)
            .ConfigureAwait(false);

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

        // processor.Middlewares run before the orchestrator's inbound chain
        // (orchestrator.go:244); the tester's only global middleware is
        // stream.EnsureUsage.
        if (ensureUsage)
        {
            options.Middlewares.Add(new EnsureUsageMiddleware());
        }

        // Inbound-phase middleware: registered first so it runs before the
        // outbound chain, matching the reference's inbound/outbound split.
        options.Middlewares.Add(new ProtectPromptsMiddleware(
            outbound,
            inbound,
            promptProtecter));
        options.Middlewares.Add(new ChannelMetricsMiddleware(
            outbound,
            _metrics,
            performance,
            _timeProvider));

        // Channel request rewriting runs after the outbound transform (so the
        // body it patches is the provider body) and before persistence (so the
        // stored request execution sees the rewritten body and headers), the
        // same order as orchestrator.go: pass-through body first so overrides
        // can still modify the replayed body, the User-Agent policy before the
        // header overrides (which may still change the User-Agent).
        options.Middlewares.Add(new PassThroughRequestBodyMiddleware(outbound, passThroughSettings));
        options.Middlewares.Add(new PassThroughRequestHeadersMiddleware(outbound));
        options.Middlewares.Add(new ChannelOverrideBodyMiddleware(outbound));
        options.Middlewares.Add(new UserAgentPassThroughMiddleware(outbound, passThroughSettings));
        options.Middlewares.Add(new ChannelOverrideHeaderMiddleware(outbound));

        // Remove transport-incompatible fields after pass-through and
        // overrides, so persistence and execution observe the same provider
        // request (orchestrator.go's finalizeTransportRequest registration).
        options.Middlewares.Add(new TransportRequestFinalizerMiddleware(outbound));

        // Response pass-through middlewares run before persistence so the raw
        // provider response/stream is what the request row stores when
        // pass-through is enabled (orchestrator.go:258-263).
        options.Middlewares.Add(new PassThroughResponseMiddleware(outbound, passThroughSettings));
        options.Middlewares.Add(new PassThroughStreamMiddleware(
            outbound,
            passThroughSettings,
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
                inbound,
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
        if (includeRateLimits)
        {
            options.Middlewares.Add(new RateLimitAdmissionMiddleware(outbound, _rateLimitTracker));
            options.Middlewares.Add(new RateLimitTrackingMiddleware(outbound, _rateLimitTracker, _timeProvider));
        }

        // Response pass-through capture middlewares must be last so they run
        // first in reverse order, before any other outbound response/stream
        // handler sees the provider payload (orchestrator.go:306-309).
        options.Middlewares.Add(new CaptureRawProviderResponseMiddleware(outbound, passThroughSettings));
        options.Middlewares.Add(new CaptureRawProviderStreamMiddleware(
            outbound,
            passThroughSettings,
            cancellationToken));

        var pipeline = new Pipeline(_executor, inbound, outbound, options);

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

    /// <summary>
    /// Port of the rule load inside biz.PromptProtectionRuleService.ProtectWithResult:
    /// a load failure is logged there and the request continues unprotected
    /// (fail open), which an empty snapshot reproduces.
    /// </summary>
    private async Task<IPromptProtecter?> LoadPromptProtecterAsync(
        CancellationToken cancellationToken)
    {
        if (_promptProtectionRules is null)
        {
            return null;
        }

        try
        {
            var rules = await _promptProtectionRules
                .ListEnabledAsync(cancellationToken)
                .ConfigureAwait(false);
            return new PromptProtectionRuleProtector(rules);
        }
        catch (Exception)
        {
            return new PromptProtectionRuleProtector([]);
        }
    }
}

/// <summary>Port of orchestrator.ChatCompletionResult (chat subset).</summary>
public sealed class ChatCompletionResult
{
    public HttpResponseData? Response { get; init; }

    public IStream<StreamEvent>? Stream { get; init; }
}
