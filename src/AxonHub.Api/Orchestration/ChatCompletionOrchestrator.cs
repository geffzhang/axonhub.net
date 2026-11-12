using AxonHub.Abstractions;
using AxonHub.Api.Auth;
using AxonHub.Api.Billing;
using AxonHub.Api.Management;
using AxonHub.Api.Persistence;
using AxonHub.Api.PromptProtection;
using AxonHub.Api.Prompts;
using AxonHub.Api.Settings;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using Orleans;
using IModelSettingsProvider = AxonHub.Api.Models.IModelSettingsProvider;
using ModelSettings = AxonHub.Api.Models.ModelSettings;
using ProfileFilters = AxonHub.Api.Models.ProfileFilters;

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
    private readonly IModelCandidateSelector _candidateSelector;
    private readonly IUpstreamErrorPolicyApplier _upstreamErrorPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly IRequestChainRecorder? _requestChainRecorder;
    private readonly IUsageCostService? _usageCostService;
    private readonly IPassThroughSettingsProvider _passThroughSettings;
    private readonly IPromptProtectionRuleProvider? _promptProtectionRules;
    private readonly IChannelRateLimitRecorder _rateLimitRecorder;
    private readonly ChannelLimiterManager _channelLimiterManager;
    private readonly ChannelLimiterMetrics? _channelLimiterMetrics;
    private readonly IPreviousChannelProvider? _previousChannels;
    private readonly IProviderQuotaStatusProvider? _quotaStatusProvider;
    private readonly IQuotaRoutingSettingsProvider? _quotaRoutingSettings;
    private readonly IModelSettingsProvider? _modelSettingsProvider;
    private readonly IApiKeyQuotaChecker? _quotaChecker;
    private readonly IPromptProvider? _promptProvider;
    private readonly ChannelApiKeySelectionRegistry? _apiKeySelection;
    private readonly ChannelAutoDisableService? _autoDisable;
    private readonly IChannelAutoDisableEvaluationQueue? _autoDisableQueue;
    private readonly ChannelProxyExecutors? _channelProxyExecutors;
    private readonly ChannelCopilotTokenCache? _copilotTokens;
    private readonly LiveStreamRegistry? _liveStreamRegistry;
    private readonly ISystemSettingsAdminService? _systemSettings;

    public ChatCompletionOrchestrator(
        IGrainFactory grainFactory,
        IInboundTransformer inbound,
        IExecutor executor,
        IChannelMetricsRecorder metrics,
        IRetryPolicyProvider retryPolicyProvider,
        IModelCandidateSelector candidateSelector,
        TimeProvider timeProvider,
        IRequestChainRecorder? requestChainRecorder = null,
        IUpstreamErrorPolicyApplier? upstreamErrorPolicy = null,
        ChannelRequestTracker? rateLimitTracker = null,
        ChannelLimiterManager? channelLimiterManager = null,
        IUsageCostService? usageCostService = null,
        IPassThroughSettingsProvider? passThroughSettings = null,
        IPromptProtectionRuleProvider? promptProtectionRules = null,
        IPreviousChannelProvider? previousChannels = null,
        IProviderQuotaStatusProvider? quotaStatusProvider = null,
        IQuotaRoutingSettingsProvider? quotaRoutingSettings = null,
        IModelSettingsProvider? modelSettings = null,
        IApiKeyQuotaChecker? quotaChecker = null,
        IPromptProvider? promptProvider = null,
        ChannelLimiterMetrics? channelLimiterMetrics = null,
        IChannelRateLimitRecorder? rateLimitRecorder = null,
        ChannelApiKeySelectionRegistry? apiKeySelection = null,
        ChannelAutoDisableService? autoDisable = null,
        IChannelAutoDisableEvaluationQueue? autoDisableQueue = null,
        ChannelProxyExecutors? channelProxyExecutors = null,
        LiveStreamRegistry? liveStreamRegistry = null,
        ISystemSettingsAdminService? systemSettings = null,
        ChannelCopilotTokenCache? copilotTokens = null)
    {
        ArgumentNullException.ThrowIfNull(grainFactory);
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(retryPolicyProvider);
        ArgumentNullException.ThrowIfNull(candidateSelector);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _grainFactory = grainFactory;
        _inbound = inbound;
        _executor = executor;
        _metrics = metrics;
        _retryPolicyProvider = retryPolicyProvider;
        _candidateSelector = candidateSelector;
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
        // one limiter manager per process. Production routes RPM/TPM/cooldown
        // through the channel grain (GrainChannelRateLimitRecorder) so the pool
        // grain scores from the same snapshot the middlewares mutate; direct
        // constructions keep the in-memory tracker, whose own clock governs.
        _rateLimitRecorder = rateLimitRecorder
            ?? new InMemoryChannelRateLimitRecorder(rateLimitTracker ?? new ChannelRequestTracker(timeProvider));
        _channelLimiterManager = channelLimiterManager ?? new ChannelLimiterManager();

        // Production registers the same metrics singleton the middlewares emit
        // to; direct constructions leave it null, which makes every instrument
        // call a no-op like the reference's nil *ChannelLimiterMetrics.
        _channelLimiterMetrics = channelLimiterMetrics;

        // Production passes the RequestService equivalent so the persistence
        // middleware can cache the previous channel and the selector can pin it.
        // Direct test constructions leave it null, like the reference's
        // selector without a PreviousChannelProvider.
        _previousChannels = previousChannels;

        // Production passes the provider quota cache and the system settings
        // reader; direct constructions leave the provider null, which makes the
        // gate a no-op exactly like the reference's nil ProviderQuotaStatusProvider.
        _quotaStatusProvider = quotaStatusProvider;
        _quotaRoutingSettings = quotaRoutingSettings;

        // Without a model-settings provider the reference's SystemService is
        // absent and ModelSettingsOrDefault is never called; the tester and
        // direct constructions then behave as AutoReasoningEffort = false.
        _modelSettingsProvider = modelSettings;

        // Production passes biz.QuotaService; a null checker makes enforceQuota
        // a no-op exactly like the reference's nil QuotaService (the tester).
        _quotaChecker = quotaChecker;

        // Production passes the process-wide multi-key selection state so the
        // fixed / round_robin_success cursors and the round_robin rotation
        // survive channel rebuilds. Direct constructions keep the historical
        // per-provider behavior.
        _apiKeySelection = apiKeySelection;

        // Production passes the auto-disable engine; direct constructions leave
        // it null, which makes both the counter queue and the failure fallback
        // no-ops exactly like a nil ChannelService (C236).
        _autoDisable = autoDisable;
        _autoDisableQueue = autoDisableQueue;

        // Production passes the per-channel proxy executor cache, which stands
        // in for the reference's channel.HTTPClient; direct constructions leave
        // it null and keep the historical shared-executor behavior.
        _channelProxyExecutors = channelProxyExecutors;

        // Production passes the per-channel Copilot exchanger cache, which
        // stands in for the reference's exchanger on the enabled-channel cache
        // entry; direct constructions keep the per-attempt exchanger (§194).
        _copilotTokens = copilotTokens;

        // Production passes the process-wide live preview registry and the
        // system settings reader; direct constructions leave both null, which
        // keeps the live-preview middleware disabled exactly like the
        // reference's nil LiveStreamRegistry (the tester).
        _liveStreamRegistry = liveStreamRegistry;
        _systemSettings = systemSettings;

        // Production passes biz.PromptService (through the orchestrator's
        // PromptProvider state); the tester mounts an empty stub instead, which
        // is the same as no provider.
        _promptProvider = promptProvider;
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
            _candidateSelector,
            _timeProvider,
            _requestChainRecorder,
            _upstreamErrorPolicy,
            null, // rateLimitTracker: the recorder below carries the state
            _channelLimiterManager,
            _usageCostService,
            _passThroughSettings,
            _promptProtectionRules,
            _previousChannels,
            _quotaStatusProvider,
            _quotaRoutingSettings,
            _modelSettingsProvider,
            _quotaChecker,
            _promptProvider,
            rateLimitRecorder: _rateLimitRecorder,
            apiKeySelection: _apiKeySelection,
            autoDisable: _autoDisable,
            autoDisableQueue: _autoDisableQueue,
            channelProxyExecutors: _channelProxyExecutors,
            liveStreamRegistry: _liveStreamRegistry,
            systemSettings: _systemSettings,
            copilotTokens: _copilotTokens);
    }

    /// <summary>
    /// Returns an orchestrator that uses another candidate selector while
    /// sharing this one's dependencies, mirroring the reference's playground
    /// handler that rebuilds the orchestrator with a
    /// <c>SpecifiedChannelSelector</c>
    /// (internal/server/api/playground.go:NewPlaygroundHandlers).
    /// </summary>
    public ChatCompletionOrchestrator WithChannelSelector(
        IModelCandidateSelector candidateSelector)
    {
        ArgumentNullException.ThrowIfNull(candidateSelector);

        return new ChatCompletionOrchestrator(
            _grainFactory,
            _inbound,
            _executor,
            _metrics,
            _retryPolicyProvider,
            candidateSelector,
            _timeProvider,
            _requestChainRecorder,
            _upstreamErrorPolicy,
            null, // rateLimitTracker: the recorder below carries the state
            _channelLimiterManager,
            _usageCostService,
            _passThroughSettings,
            _promptProtectionRules,
            _previousChannels,
            _quotaStatusProvider,
            _quotaRoutingSettings,
            _modelSettingsProvider,
            _quotaChecker,
            _promptProvider,
            rateLimitRecorder: _rateLimitRecorder,
            apiKeySelection: _apiKeySelection,
            autoDisable: _autoDisable,
            autoDisableQueue: _autoDisableQueue,
            channelProxyExecutors: _channelProxyExecutors,
            liveStreamRegistry: _liveStreamRegistry,
            systemSettings: _systemSettings,
            copilotTokens: _copilotTokens);
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
    /// convert quota exhaustion, classify a transport failure, apply the upstream
    /// error policy, then let the inbound transformer build the client envelope.
    /// </summary>
    public async Task<HttpResponseData> TransformErrorAsync(
        Exception error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);

        var quotaWrapped = UpstreamErrorPolicyApplier.WrapQuotaExhausted(error)!;
        var classified = UpstreamTransportErrors.Classify(quotaWrapped);
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

        // orchestrator.go:250-255 registers enforceQuota, applyAutoReasoningEffort
        // and checkApiKeyModelAccess before applyModelMapping and selectCandidates.
        // The port runs the same stages here, before the candidate list is built,
        // so their rejections never create a request row and the selector sees
        // the normalized model.
        await EnforceQuotaAsync(identity, cancellationToken).ConfigureAwait(false);
        var modelSettings = await ReadModelSettingsAsync(cancellationToken).ConfigureAwait(false);
        AutoReasoningEffort.Apply(llmRequest, modelSettings);
        ApiKeyModelAccess.Check(identity.ApiKey, llmRequest);

        // The mapping stage runs before selectCandidates; the port applies the
        // same middleware before selection so routing uses the profile-mapped
        // model, then mounts the instance in the pipeline so its inbound state
        // and response hooks see the same request.
        var modelMapping = new ApiKeyModelMappingMiddleware(identity.ApiKey);
        modelMapping.TransformInboundLlmRequest(llmRequest);
        var model = llmRequest.Model;

        // orchestrator.Process reads the policy once per request; the load
        // balancer derives top-K from the same values (1 + MaxChannelRetries,
        // or a single candidate when retries are disabled).
        var retryPolicy = await _retryPolicyProvider
            .GetRetryPolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        var requiredCount = retryPolicy.Enabled ? 1 + retryPolicy.MaxChannelRetries : 1;

        // The reference reads the pass-through flags lazily inside each
        // middleware; the port's hooks are synchronous, so one snapshot per
        // request serves all of them (C091).
        var passThroughSettings = await _passThroughSettings
            .GetAsync(cancellationToken)
            .ConfigureAwait(false);

        // Port of the selectCandidates inbound middleware: route the requested
        // model through its effective associations first (DefaultSelector), then
        // wrap the result in the load-balanced selector.
        var selection = await _candidateSelector
            .SelectAsync(llmRequest, identity, cancellationToken)
            .ConfigureAwait(false);

        // LoadBalancedSelector.Select derives the effective routing policy from
        // the retry policy, the model policy the selector attached to every
        // candidate, and the API key's active profile (retry.go:102-142).
        var effectivePolicy = DeriveRoutingPolicy(
            retryPolicy,
            identity.ApiKey,
            selection.RoutingPolicy);

        // select_candidates.go:27-63: the project profile (upper boundary) and
        // the API key profile narrow the base selector's candidates, the
        // native-tool decorators drop channels that cannot carry the request's
        // Google / Anthropic native tools, and the stream policy selector
        // applies the channel's stream requirement. All of this runs before the
        // load-balanced selector.
        var scopedCandidates = StreamPolicySelector.ApplyToCandidates(
            NativeToolFilters.ApplyToCandidates(
                ProfileFilters.ApplyToCandidates(selection.Candidates, identity),
                llmRequest),
            llmRequest);

        // select_candidates.go:80: the quota gate is built per request so its
        // dropped count belongs to this selection only.
        var quotaSettings = _quotaRoutingSettings is null
            ? QuotaRoutingSettings.Default
            : await _quotaRoutingSettings
                .GetQuotaRoutingSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
        var quotaGate = new QuotaRoutingGate(
            _quotaStatusProvider,
            quotaSettings.DefaultMode,
            _timeProvider);

        ChannelCandidate[] candidates = [];
        if (scopedCandidates.Count > 0)
        {
            candidates = await SortCandidatesAsync(
                    model,
                    llmRequest,
                    scopedCandidates,
                    identity,
                    effectivePolicy,
                    requiredCount,
                    quotaGate,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // select_candidates.go:108-114: an empty result is a quota error when
        // the gate removed candidates, otherwise ErrInvalidModel with the
        // request model appended unquoted.
        if (candidates.Length == 0)
        {
            if (quotaGate.DroppedCount > 0)
            {
                throw new QuotaExhaustedException(model);
            }

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
                modelSettings,
                modelMapping,
                promptProvider: _promptProvider,
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
    /// <param name="proxy">
    /// The tester's per-request proxy (tester.go:372's <c>proxy: proxy</c>),
    /// which overrides the channel's persisted <c>settings.proxy</c>.
    /// </param>
    public async Task<ChatCompletionResult> ProcessSpecifiedAsync(
        IInboundTransformer inbound,
        HttpRequestData request,
        Func<LlmRequest, CancellationToken, Task<ChannelCandidate?>> candidateFactory,
        RequestIdentity identity,
        CancellationToken cancellationToken = default,
        ProxyConfig? proxy = null)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(candidateFactory);
        ArgumentNullException.ThrowIfNull(identity);

        var llmRequest = inbound.TransformRequest(request);

        // The tester's processor mounts the same orchestrator inbound chain, so
        // auto reasoning effort and the model-access check run before the
        // SpecifiedChannelSelector too. The tester has no QuotaService
        // (tester.go:296-316), so enforceQuota is absent here; its identity
        // carries no API key, which makes the access check a no-op anyway.
        var modelSettings = await ReadModelSettingsAsync(cancellationToken).ConfigureAwait(false);
        AutoReasoningEffort.Apply(llmRequest, modelSettings);
        ApiKeyModelAccess.Check(identity.ApiKey, llmRequest);

        var modelMapping = new ApiKeyModelMappingMiddleware(identity.ApiKey);
        modelMapping.TransformInboundLlmRequest(llmRequest);

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

        // The tester's processor runs the same selectCandidates middleware, so
        // the profile, native-tool and stream-policy filters apply to the
        // specified channel too.
        var scoped = StreamPolicySelector.ApplyToCandidates(
            NativeToolFilters.ApplyToCandidates(
                ProfileFilters.ApplyToCandidates([candidate], identity),
                llmRequest),
            llmRequest);
        if (scoped.Count == 0)
        {
            throw TransformerErrors.InvalidModel(llmRequest.Model);
        }

        candidate = scoped[0];

        // tester.go:306-312 installs the same three load balancers as the
        // server, so selectCandidates still wraps the SpecifiedChannelSelector
        // in a LoadBalancedSelector. A lone candidate bypasses the balancer but
        // sortCandidates records its selection (candidates.go:921-925); the
        // specified selector attaches no model routing policy, so the effective
        // policy comes from the retry policy and the caller's API key alone.
        var requiredCount = retryPolicy.Enabled ? 1 + retryPolicy.MaxChannelRetries : 1;
        var effectivePolicy = DeriveRoutingPolicy(
            retryPolicy,
            identity.ApiKey,
            modelPolicy: null);
        var candidates = await _grainFactory
            .GetGrain<IChannelPoolGrain>(llmRequest.Model)
            .SortAsync(
                [candidate],
                _timeProvider.GetUtcNow(),
                trackSelection: true,
                requiredCount,
                effectivePolicy.LoadBalancerStrategy,
                cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Length == 0)
        {
            throw TransformerErrors.InvalidModel(llmRequest.Model);
        }

        // The tester's processor leaves the rate limit tracker nil, so
        // withRateLimitAdmission/withRateLimitTracking are no-ops there; the
        // concurrency limiter and the rest of the chain still run.
        return await RunPipelineAsync(
                inbound,
                request,
                candidates,
                identity,
                onRequestCreated: null,
                retryPolicy,
                passThroughSettings,
                modelSettings,
                modelMapping,
                // The tester's orchestrator mounts &stubPromptProvider{} (an
                // empty list), so no prompts are injected there.
                promptProvider: null,
                includeRateLimits: false,
                ensureUsage: true,
                cancellationToken,
                proxyOverride: proxy,
                // The tester builds its own orchestrator without a live
                // registry (tester.go:295-316), so channel tests never publish
                // preview buffers even though they share this production
                // instance.
                livePreview: false)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Port of orchestrator.enforceQuota (internal/server/orchestrator/quota.go:14-66):
    /// a nil checker or key, a key without an active profile, or a profile
    /// without a quota allows the request. A tripped quota throws the
    /// reference's 403 <c>quota_exceeded</c> envelope before any request row is
    /// created (the reference's quota middleware runs before persistRequest).
    /// </summary>
    private async Task EnforceQuotaAsync(
        RequestIdentity identity,
        CancellationToken cancellationToken)
    {
        if (_quotaChecker is null)
        {
            return;
        }

        var apiKey = identity.ApiKey;
        if (apiKey is null)
        {
            return;
        }

        var profile = ApiKeyProfilesCodec.ActiveProfile(apiKey.Profiles);
        if (profile?.Quota is null)
        {
            return;
        }

        var result = await _quotaChecker
            .CheckApiKeyQuotaAsync(apiKey.Id, profile.Quota, cancellationToken)
            .ConfigureAwait(false);
        if (result.Allowed)
        {
            return;
        }

        throw new ResponseError
        {
            StatusCode = 403,
            Detail = new ErrorDetail
            {
                Code = "quota_exceeded",
                Message = result.Message,
                Type = "quota_exceeded_error",
                RequestId = string.IsNullOrEmpty(identity.RequestId) ? null : identity.RequestId,
            },
        };
    }

    /// <summary>
    /// Port of SystemService.ModelSettingsOrDefault as read by the
    /// auto-reasoning-effort middleware. Without a provider the reference's
    /// SystemService is absent and the defaults apply.
    /// </summary>
    private async Task<ModelSettings> ReadModelSettingsAsync(CancellationToken cancellationToken)
        => _modelSettingsProvider is null
            ? ModelSettings.Default
            : await _modelSettingsProvider
                .GetModelSettingsOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

    private async Task<ChatCompletionResult> RunPipelineAsync(
        IInboundTransformer inbound,
        HttpRequestData request,
        ChannelCandidate[] candidates,
        RequestIdentity identity,
        Action<long>? onRequestCreated,
        RetryPolicy retryPolicy,
        PassThroughSettings passThroughSettings,
        ModelSettings modelSettings,
        ApiKeyModelMappingMiddleware modelMapping,
        IPromptProvider? promptProvider,
        bool includeRateLimits,
        bool ensureUsage,
        CancellationToken cancellationToken,
        ProxyConfig? proxyOverride = null,
        bool livePreview = true)
    {
        // channel_llm.go:122-130,700-774: the reference builds the channel's
        // transformer with the channel's proxy-aware client, and the same client
        // serves the OAuth token providers. Here the factory receives the
        // per-channel executor for direct token providers; the grain-backed
        // refresh (ChannelTokenSource) resolves the same cached client from the
        // seed's settings snapshot (§178).
        IExecutor ChannelExecutorFor(ChannelCandidate candidate)
            => _channelProxyExecutors?.Resolve(candidate) ?? _executor;

        var outbound = new PersistentOutboundTransformer(
            candidates,
            _timeProvider,
            _metrics,
            (candidate, request, now) => ChannelOutboundFactory.Create(
                candidate,
                request,
                now,
                ChannelExecutorFor(candidate),
                _grainFactory,
                _apiKeySelection,
                _copilotTokens),
            _channelProxyExecutors,
            proxyOverride);

        // orchestrator.go mounts injectPrompts then protectPrompts as inbound
        // middlewares; the reference reads the enabled prompts and rules from
        // its live caches there. The port takes both snapshots before building
        // the pipeline and applies them inside the inbound phase, so no
        // provider body is built from unprotected prompts.
        var promptProtecter = await LoadPromptProtecterAsync(cancellationToken)
            .ConfigureAwait(false);
        var prompts = await LoadPromptsAsync(promptProvider, identity, cancellationToken)
            .ConfigureAwait(false);

        // orchestrator.go:298-299 mounts withLivePreview after
        // persistRequestExecution; the middleware reads
        // SystemService.StoragePolicyOrDefault().LivePreview on the first
        // streaming request. The port's settings read is asynchronous, so the
        // snapshot happens here and the middleware still gates on the unified
        // request's stream flag. A failed read falls back to the default
        // policy, which disables the preview (StoragePolicyOrDefault).
        var livePreviewEnabled = false;
        if (livePreview && _liveStreamRegistry is not null && _systemSettings is not null)
        {
            try
            {
                livePreviewEnabled = (await _systemSettings
                    .GetStoragePolicyAsync(cancellationToken)
                    .ConfigureAwait(false)).LivePreview;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                livePreviewEnabled = false;
            }
        }

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

        // The pipeline parses the raw body again, so the pre-selection auto
        // reasoning effort must run here as well: the same settings snapshot is
        // applied to the freshly parsed request, and the model-mapping restore
        // branch below overwrites the routing model either way (the suffix is
        // already gone on the second pass, so the double application is
        // idempotent). orchestrator.go:252-254.
        options.Middlewares.Add(new AutoReasoningEffortMiddleware(modelSettings));

        // orchestrator.go:254: applyModelMapping runs before injectPrompts and
        // protectPrompts on the inbound pass, so the response hook also runs
        // after theirs in reverse order.
        options.Middlewares.Add(modelMapping);

        // Inbound-phase middleware: registered first so it runs before the
        // outbound chain, matching the reference's inbound/outbound split.
        // injectPrompts runs before protectPrompts, so the protection pass sees
        // the injected prompts (orchestrator.go:244-245).
        if (prompts is { Count: > 0 })
        {
            options.Middlewares.Add(new InjectPromptsMiddleware(prompts, identity.ApiKey?.Id ?? 0));
        }

        options.Middlewares.Add(new ProtectPromptsMiddleware(
            outbound,
            inbound,
            promptProtecter));
        options.Middlewares.Add(new ChannelMetricsMiddleware(
            outbound,
            _metrics,
            performance,
            _timeProvider,
            _apiKeySelection,
            _autoDisableQueue));

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
                _usageCostService,
                _previousChannels);
            options.Middlewares.Add(persistence);
        }

        // Forward the events to the live preview buffer (orchestrator.go:298-299:
        // registered right after persistRequestExecution and before the
        // admission middlewares). The tester's processor has no registry, so
        // channel tests never register preview buffers.
        if (livePreview && _liveStreamRegistry is not null)
        {
            options.Middlewares.Add(new LivePreviewMiddleware(
                _liveStreamRegistry,
                persistence,
                livePreviewEnabled));
        }

        // Per-channel admission, then RPM admission, then TPM/cooldown
        // tracking — the reference registers them after the persistence
        // middleware (orchestrator.go:298-304). The concurrency limiter runs
        // first so a queue rejection never consumes an RPM slot.
        options.Middlewares.Add(new ChannelLimiterMiddleware(
            outbound,
            _channelLimiterManager,
            cancellationToken,
            _channelLimiterMetrics,
            _timeProvider));
        if (includeRateLimits)
        {
            options.Middlewares.Add(new RateLimitAdmissionMiddleware(outbound, _rateLimitRecorder, _timeProvider));
            options.Middlewares.Add(new RateLimitTrackingMiddleware(outbound, _rateLimitRecorder, _timeProvider));
        }

        // Response pass-through capture middlewares must be last so they run
        // first in reverse order, before any other outbound response/stream
        // handler sees the provider payload (orchestrator.go:306-309).
        options.Middlewares.Add(new CaptureRawProviderResponseMiddleware(outbound, passThroughSettings));
        options.Middlewares.Add(new CaptureRawProviderStreamMiddleware(
            outbound,
            passThroughSettings,
            cancellationToken));

        // Run first on raw responses (reverse order), before capture fan-out:
        // an Anthropic message_stop is the provider protocol boundary, so close
        // the provider transport without canceling reviewed delivery
        // (orchestrator.go:353-354).
        options.Middlewares.Add(new AnthropicTerminalStreamMiddleware(outbound));

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

            await MaybeEvaluateChannelApiKeyRulesOnFailureAsync(performance, outbound, persistence, ex)
                .ConfigureAwait(false);

            // The reference stops each channel's outbound when the enabled
            // channels cache swaps; the port's transformer lives for one
            // attempt, so a failed attempt releases its WebSocket pool here
            // (C072).
            outbound.Stop();
            throw;
        }

        // orchestrator.go:360-375: the API boundary forwards Codex response
        // headers only when the selected outbound advertises the contract.
        var codexResponseHeadersSupported = outbound.SupportsCodexResponseHeaders();

        if (result.Stream)
        {
            // The stream outlives this method. Stop the attempt's transformer
            // when the consumer disposes the stream — after completion, error or
            // cancellation (C072).
            IStream<StreamEvent> stream = new DisposeCallbackStream<StreamEvent>(
                result.EventStream!,
                outbound.Stop);

            // The teardown wrapper is transparent, but it hides the
            // responseHeadersProvider interface the pipeline attached to the
            // stream (httpclient.WithResponseHeaders); re-attach the metadata so
            // api.writeForwardResponseHeaders can read it (C094).
            if (ResponseHeaders.GetResponseHeaders(result.EventStream) is { } headers)
            {
                stream = ResponseHeaders.WithResponseHeaders(stream, headers);
            }

            return new ChatCompletionResult
            {
                Stream = stream,
                CodexResponseHeadersSupported = codexResponseHeadersSupported,
            };
        }

        outbound.Stop();
        return new ChatCompletionResult
        {
            Response = result.Response,
            CodexResponseHeadersSupported = codexResponseHeadersSupported,
        };
    }

    /// <summary>
    /// Port of orchestrator.maybeEvaluateChannelAPIKeyRulesOnFailure
    /// (orchestrator.go:377-409): an attempt that failed after its request
    /// execution was created, and whose performance record was not completed by
    /// the metrics middleware, still feeds the credential rules. The reference
    /// evaluates synchronously on a detached ten-second context.
    /// </summary>
    private async Task MaybeEvaluateChannelApiKeyRulesOnFailureAsync(
        ChannelPerformanceState performance,
        PersistentOutboundTransformer outbound,
        RequestChainPersistenceMiddleware? persistence,
        Exception error)
    {
        if (_autoDisable is null || persistence is not { HasRequestExecution: true })
        {
            return;
        }

        // A completed record was already handed to the metrics worker.
        if (performance.Current is { RequestCompleted: true })
        {
            return;
        }

        var channelId = outbound.CurrentChannelId;
        if (channelId <= 0)
        {
            return;
        }

        // performance.go:79-83: the provider publishes the selected key; OAuth
        // channels carry the fixed credential ref instead.
        var apiKey = outbound.CurrentApiKey ?? string.Empty;
        if (apiKey.Length == 0
            && outbound.CurrentCandidate is { } candidate
            && ChannelCredentials.IsOAuth(candidate.Credentials))
        {
            apiKey = ChannelCredentials.OAuthCredentialRef;
        }

        if (apiKey.Length == 0 && performance.Current is { } record)
        {
            apiKey = record.ApiKey;
        }

        if (apiKey.Length == 0)
        {
            return;
        }

        // orchestrator.go:322-323: DetachWithTimeout, so a canceled client
        // request still records the credential failure.
        using var detached = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            _ = await _autoDisable
                .EvaluateApiKeyRulesForFailureAsync(
                    channelId,
                    apiKey,
                    ChannelMetricsMiddleware.ExtractErrorCode(error),
                    ChannelMetricsMiddleware.ExtractErrorMessageForMatching(error),
                    detached.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A failed rule evaluation must not replace the request error
            // (C236: the reference would surface only a panic here).
        }
    }

    /// <summary>
    /// The port of LoadBalancedSelector.Select's sticky half
    /// (candidates.go:738-791): when the effective policy prefers the previous
    /// channel, the highest-priority candidate for that channel is pinned ahead
    /// of the load-balanced order and every duplicate of it is removed from the
    /// fallback set. The fallback sort does not track a selection; the pinned
    /// candidate is tracked exactly once, after the fallback list is assembled.
    /// </summary>
    private async Task<ChannelCandidate[]> SortCandidatesAsync(
        string model,
        LlmRequest request,
        IReadOnlyList<ChannelCandidate> source,
        RequestIdentity identity,
        RetryPolicy effectivePolicy,
        int requiredCount,
        QuotaRoutingGate quotaGate,
        CancellationToken cancellationToken)
    {
        long stickyId = 0;
        var stickyFound = false;
        if (effectivePolicy.TraceStickyMode == RetryPolicy.TraceStickyPreferPreviousChannel)
        {
            (stickyId, stickyFound) = ResolveStickyChannelId(_previousChannels, identity, source);
        }

        // candidates.go:769-775: the quota gate runs after the sticky id is
        // resolved but before the sticky pin. A channel the gate removed can
        // therefore not be pinned, and the fallback sort sees the filtered list.
        var gated = quotaGate.Filter(source, request, stickyFound ? stickyId : 0);

        ChannelCandidate? sticky = null;
        IReadOnlyList<ChannelCandidate> remaining = gated;
        if (stickyFound)
        {
            (sticky, remaining) = ExtractStickyCandidate(gated, stickyId);
        }

        var now = _timeProvider.GetUtcNow();
        var grain = _grainFactory.GetGrain<IChannelPoolGrain>(model);
        var strategy = effectivePolicy.LoadBalancerStrategy;

        if (sticky is null)
        {
            return await grain
                .SortAsync(
                    [.. gated],
                    now,
                    trackSelection: true,
                    requiredCount,
                    strategy,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        sticky.TraceSticky = true;

        // requiredCount is 1 + MaxChannelRetries, so the pinned candidate
        // consumes one slot and the fallbacks share the rest.
        var fallbackCount = Math.Max(requiredCount - 1, 0);
        ChannelCandidate[] fallback = [];
        if (fallbackCount > 0)
        {
            fallback = await grain
                .SortAsync(
                    [.. remaining],
                    now,
                    trackSelection: false,
                    fallbackCount,
                    strategy,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // LoadBalancer.TrackSelection(stickyCandidate): a single-candidate sort
        // short-circuits to the same counter increment the reference performs.
        _ = await grain
            .SortAsync(
                [sticky],
                now,
                trackSelection: true,
                requiredCount: 1,
                strategy,
                cancellationToken)
            .ConfigureAwait(false);

        return [sticky, .. fallback];
    }

    /// <summary>
    /// Port of resolveStickyChannelID (candidates.go:815-870): the trace scope
    /// wins when its cached channel is still among the candidates; otherwise the
    /// thread scope is tried. Both scopes only count when a candidate exists, so
    /// a stale cache entry never selects a channel the model cannot use.
    /// </summary>
    private static (long ChannelId, bool Found) ResolveStickyChannelId(
        IPreviousChannelProvider? previousChannels,
        RequestIdentity identity,
        IReadOnlyList<ChannelCandidate> candidates)
    {
        if (previousChannels is null || candidates.Count == 0)
        {
            return (0, false);
        }

        var hasCandidate = (long channelId) =>
            channelId != 0
            && candidates.Any(candidate => candidate.ChannelId == channelId);

        if (identity.TraceId is > 0)
        {
            var channelId = previousChannels.GetPreviousChannelId(identity.TraceId.Value);
            if (hasCandidate(channelId))
            {
                return (channelId, true);
            }
        }

        var threadId = identity.ThreadId ?? identity.TraceThreadId;
        if (threadId is not > 0)
        {
            return (0, false);
        }

        var threadChannelId = previousChannels.GetPreviousChannelIdByThread(threadId.Value);
        return hasCandidate(threadChannelId)
            ? (threadChannelId, true)
            : (0, false);
    }

    /// <summary>
    /// Port of extractStickyCandidate (candidates.go:876-905): returns the
    /// lowest-priority-value candidate for the channel and removes every
    /// candidate of that channel from the fallback list, so the pinned channel
    /// cannot receive another retry budget through a duplicate association.
    /// </summary>
    private static (ChannelCandidate? Sticky, List<ChannelCandidate> Remaining)
        ExtractStickyCandidate(IReadOnlyList<ChannelCandidate> candidates, long channelId)
    {
        if (channelId == 0)
        {
            return (null, [.. candidates]);
        }

        ChannelCandidate? sticky = null;
        var remaining = new List<ChannelCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (candidate.ChannelId != channelId)
            {
                remaining.Add(candidate);
                continue;
            }

            if (sticky is null || candidate.Priority < sticky.Priority)
            {
                sticky = candidate;
            }
        }

        return sticky is null ? (null, [.. candidates]) : (sticky, remaining);
    }

    /// <summary>
    /// Port of deriveRoutingPolicy (retry.go:102-142): a non-default
    /// valid model policy overrides the retry policy, then the API key's active
    /// profile overrides both. An absent profile, or one whose field is null,
    /// leaves the earlier value untouched.
    /// </summary>
    internal static RetryPolicy DeriveRoutingPolicy(
        RetryPolicy retryPolicy,
        AxonHub.Persistence.ApiKeyRecord? apiKey,
        ModelRoutingPolicy? modelPolicy)
    {
        var policy = retryPolicy;

        if (modelPolicy is not null)
        {
            var strategy = RoutingPolicy.NormalizeValue(modelPolicy.LoadBalancerStrategy);
            if (strategy != RoutingPolicy.Default
                && RoutingPolicy.IsValidLoadBalancerStrategy(strategy))
            {
                policy = policy with { LoadBalancerStrategy = strategy };
            }

            var traceStickyMode = RoutingPolicy.NormalizeValue(modelPolicy.TraceStickyMode);
            if (traceStickyMode != RoutingPolicy.Default
                && RoutingPolicy.IsValidTraceStickyMode(traceStickyMode))
            {
                policy = policy with { TraceStickyMode = traceStickyMode };
            }
        }

        if (apiKey is null)
        {
            return policy;
        }

        var activeProfile = ApiKeyProfilesCodec.ActiveProfile(apiKey.Profiles);
        if (activeProfile is null)
        {
            return policy;
        }

        if (activeProfile.LoadBalanceStrategy is not null)
        {
            var strategy = RoutingPolicy.NormalizeValue(activeProfile.LoadBalanceStrategy);
            if (strategy != RoutingPolicy.Default
                && RoutingPolicy.IsValidLoadBalancerStrategy(strategy))
            {
                policy = policy with { LoadBalancerStrategy = strategy };
            }
        }

        if (activeProfile.TraceStickyMode is not null)
        {
            var traceStickyMode = RoutingPolicy.NormalizeValue(activeProfile.TraceStickyMode);
            if (traceStickyMode != RoutingPolicy.Default
                && RoutingPolicy.IsValidTraceStickyMode(traceStickyMode))
            {
                policy = policy with { TraceStickyMode = traceStickyMode };
            }
        }

        return policy;
    }

    /// <summary>
    /// Port of orchestrator.injectPrompts' provider read (prompt.go:37-87):
    /// no project in the context skips injection entirely, and a provider
    /// error is logged there with the request continuing uninjected (fail
    /// open), which a null snapshot reproduces.
    /// </summary>
    internal static async Task<IReadOnlyList<Prompt>?> LoadPromptsAsync(
        IPromptProvider? promptProvider,
        RequestIdentity identity,
        CancellationToken cancellationToken)
    {
        // RequestIdentity.ProjectId is non-nullable; 0 (or less) stands for the
        // reference's "no project ID in the request context".
        if (promptProvider is null || identity.ProjectId <= 0)
        {
            return null;
        }

        try
        {
            return await promptProvider
                .GetEnabledPromptsAsync(identity.ProjectId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
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

    /// <summary>
    /// Port of orchestrator.ChatCompletionResult.CodexResponseHeadersSupported:
    /// whether the successful attempt's outbound participates in the Codex
    /// response-header contract (codex outbound always true).
    /// </summary>
    public bool CodexResponseHeadersSupported { get; init; }
}
