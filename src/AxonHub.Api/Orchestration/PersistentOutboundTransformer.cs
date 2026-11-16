using System.Text;
using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Api.PromptProtection;
using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic.ClaudeCode;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of orchestrator.PersistentOutboundTransformer
/// (internal/server/orchestrator/outbound.go:516-880) for the chat slice: it owns
/// the ordered candidate list, points every attempt at the current candidate and
/// implements the pipeline's retry seams.
/// </summary>
/// <remarks>
/// The retry loop itself lives in <see cref="Pipeline"/>, exactly as in the
/// reference: <c>CanRetry</c> decides same-channel retries, <c>HasMoreChannels</c>
/// and <c>NextChannel</c> drive channel switches, and the pipeline enforces the
/// two budgets and the inter-retry delay.
/// </remarks>
public sealed class PersistentOutboundTransformer : IOutboundTransformer, IRetryable,
    IChannelRetryable, IChannelCustomizedExecutor, ITransportRequestFinalizer,
    IResponseTimeoutProvider, IChannelSameChannelRetryLimit
{
    private readonly ChannelCandidate[] _candidates;
    private readonly TimeProvider _timeProvider;
    private readonly IChannelMetricsRecorder _metrics;
    private readonly Func<ChannelCandidate, LlmRequest, DateTimeOffset, IOutboundTransformer> _factory;
    private readonly ChannelProxyExecutors? _proxyExecutors;
    private readonly ProxyConfig? _proxyOverride;
    private readonly IChannelRefreshSource? _channelRefresh;

    private int _candidateIndex;
    private int _modelIndex;
    private IOutboundTransformer? _current;
    private bool _originalModelCaptured;
    private bool _originalStreamCaptured;

    /// <param name="proxyExecutors">
    /// Per-channel proxy executor cache (the reference's
    /// <c>channel.HTTPClient</c>). Null in direct constructions that have no
    /// proxy seam; production always passes it.
    /// </param>
    /// <param name="proxyOverride">
    /// The tester's per-request proxy (the reference's <c>state.Proxy</c>); it
    /// wins over the channel's persisted proxy setting.
    /// </param>
    public PersistentOutboundTransformer(
        ChannelCandidate[] candidates,
        TimeProvider timeProvider,
        IChannelMetricsRecorder metrics,
        Func<ChannelCandidate, LlmRequest, DateTimeOffset, IOutboundTransformer>? factory = null,
        ChannelProxyExecutors? proxyExecutors = null,
        ProxyConfig? proxyOverride = null,
        IChannelRefreshSource? channelRefresh = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(metrics);

        _candidates = candidates;
        _timeProvider = timeProvider;
        _metrics = metrics;
        _factory = factory ?? ((candidate, request, now) =>
            ChannelOutboundFactory.Create(candidate, request, now));
        _proxyExecutors = proxyExecutors;
        _proxyOverride = proxyOverride;
        _channelRefresh = channelRefresh;
    }

    /// <summary>The channel serving the current attempt, or 0 before the first transform.</summary>
    public long CurrentChannelId { get; private set; }

    /// <summary>
    /// The channel model used for routing and pricing (the reference's
    /// <c>GetCurrentModelID</c>); provider transforms may rewrite the model in
    /// the final HTTP request, so this is not read back from the body.
    /// </summary>
    public string CurrentModel { get; private set; } = string.Empty;

    /// <summary>
    /// The model the client originally requested, captured once before the
    /// first attempt rewrites it (the reference's <c>state.OriginalModel</c>).
    /// Channel override templates expose it as <c>.RequestModel</c>.
    /// </summary>
    public string OriginalModel { get; private set; } = string.Empty;

    /// <summary>
    /// The unified request the current attempt is transforming (the reference's
    /// <c>state.LlmRequest</c>). Raw-request middlewares render override
    /// templates against it.
    /// </summary>
    public LlmRequest? CurrentLlmRequest { get; private set; }

    /// <summary>
    /// The client's stream intent before any candidate-specific forcing
    /// (the reference's <c>state.OriginalRequestStream</c>). Pass-through
    /// requires the inbound and outbound stream intents to agree.
    /// </summary>
    public bool? OriginalRequestStream { get; internal set; }

    /// <summary>
    /// The per-request Responses continuation state
    /// (the reference's <c>state.ResponsesContinuation</c>). The orchestrator
    /// creates it from the client's raw body before the inbound transform, so
    /// this transformer can pin a continuing turn to the endpoint that owns its
    /// <c>previous_response_id</c> and expand the stored history (C289).
    /// </summary>
    internal ResponsesContinuation? ResponsesContinuation { get; set; }

    /// <summary>
    /// The request's stream lifecycle (streamLifecycleFromContext in the
    /// reference). A retry must wait for the previous attempt's accounting tail
    /// before it takes over the shared source (C293).
    /// </summary>
    public StreamLifecycle? Lifecycle { get; set; }

    /// <summary>
    /// The actual outbound provider request of the latest attempt
    /// (the reference's <c>state.RawProviderRequest</c>), stored by the
    /// pass-through body middleware before any enable check so the emitted API
    /// format is known.
    /// </summary>
    public HttpRequestData? RawProviderRequest { get; internal set; }

    /// <summary>
    /// Whether the inbound body replaced the outbound provider body on this
    /// attempt (request_executions.pass_through_applied). Reset by both retry
    /// paths, exactly like <c>PrepareForRetry</c> / <c>NextChannel</c>.
    /// </summary>
    public bool PassThroughApplied { get; internal set; }

    /// <summary>
    /// The raw provider response of the latest attempt (the reference's
    /// <c>state.RawProviderResponse</c>), captured before the outbound
    /// transformer decodes it so the response can be replayed verbatim.
    /// </summary>
    public HttpResponseData? RawProviderResponse { get; internal set; }

    /// <summary>
    /// The live fan-out state of the latest streaming attempt (the reference's
    /// RawStreamCh / RawStreamBacklog / RawStreamErrRef / RawStreamCancel).
    /// </summary>
    internal PassThroughStreamState? PassThroughStream { get; set; }

    /// <summary>
    /// The rules that masked the unified request on the inbound pass
    /// (the reference's <c>state.PromptProtectionMaskRules</c>). Set once per
    /// request, not per attempt, so retries replay the same protected body.
    /// </summary>
    public IReadOnlyList<PromptProtectionRule> PromptProtectionMaskRules { get; internal set; } = [];

    /// <summary>
    /// Snapshot validating that a replayed provider body decodes to exactly the
    /// protected prompts (the reference's
    /// <c>state.PromptProtectionBodyCheck</c>). Null when no rule matched and no
    /// legacy protector changed the prompt text.
    /// </summary>
    internal PromptProtectionBodyCheck? PromptProtectionBodyCheck { get; set; }

    /// <summary>
    /// Port of resetPassThroughStreamState: unblock the previous attempt's
    /// fan-out thread and clear its state before the next attempt starts.
    /// </summary>
    internal void ResetPassThroughStreamState()
    {
        var state = PassThroughStream;
        PassThroughStream = null;
        state?.Cancel();
    }

    /// <summary>
    /// The candidate serving the current attempt, or <see langword="null"/> before
    /// the first transform. Raw-request middlewares read the channel-level
    /// override settings from here.
    /// </summary>
    public ChannelCandidate? CurrentCandidate =>
        _candidateIndex < _candidates.Length ? _candidates[_candidateIndex] : null;

    /// <summary>
    /// Port of orchestrator.PersistentOutboundTransformer.ResponseTimeouts
    /// (outbound.go:913-940): the current channel's response timeout overrides,
    /// or null for a timeout the channel inherits from the global retry policy.
    /// Resolving per attempt keeps the overrides correct after a channel switch.
    /// </summary>
    public (TimeSpan? StreamFirstEventTimeout, TimeSpan? NonStreamTimeout) ResponseTimeouts()
    {
        if (CurrentCandidate is not { } candidate)
        {
            return (null, null);
        }

        var responseTimeout = ChannelTransformOptions.SettingsOf(candidate)?.ResponseTimeout;
        if (responseTimeout is null || responseTimeout.Mode != "custom")
        {
            return (null, null);
        }

        return (
            SecondsToDuration(responseTimeout.StreamFirstEventTimeoutSeconds),
            SecondsToDuration(responseTimeout.NonStreamResponseTimeoutSeconds));
    }

    private static TimeSpan? SecondsToDuration(int? seconds)
        => seconds is { } value ? TimeSpan.FromSeconds(value) : null;

    /// <summary>
    /// Port of SameChannelRetryLimit (outbound.go:900-912): a channel-level
    /// maxSingleChannelRetries overrides the global retry policy; unset keeps
    /// the global value. A globally disabled retry policy still wins because
    /// the pipeline checks RetriesDisabled first.
    /// </summary>
    public (int Limit, bool HasOverride) SameChannelRetryLimit()
    {
        if (CurrentCandidate is not { } candidate)
        {
            return (0, false);
        }

        var limit = ChannelTransformOptions.SettingsOf(candidate)?.MaxSingleChannelRetries;
        return limit is { } value ? (value, true) : (0, false);
    }

    /// <summary>
    /// The channel key the current attempt sent upstream, when the provider
    /// exposes it (request_executions.channel_api_key_suffix).
    /// </summary>
    public string? CurrentApiKey => (_current as IApiKeyProvenance)?.SelectedApiKey;

    /// <summary>
    /// The 1-based position of <see cref="CurrentApiKey"/> over the channel's
    /// full key list, or null for single-key channels and before the first
    /// transform (request_executions.channel_api_key_index).
    /// </summary>
    public int? CurrentApiKeyIndex => (_current as IApiKeyProvenance)?.SelectedApiKeyIndex;

    /// <summary>The wrapped transformer for the current attempt.</summary>
    private IOutboundTransformer Current =>
        _current ?? throw new InvalidOperationException("no candidate selected yet");

    /// <summary>
    /// The provider transformer of the current attempt, for middleware that
    /// aggregates the provider's raw stream (request_execution persistence).
    /// </summary>
    internal IOutboundTransformer CurrentTransformer => Current;

    public string ApiFormat => Current.ApiFormat;

    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("chat completion request is nil");
        }

        if (_candidates.Length == 0)
        {
            throw new InvalidOperationException(
                "no candidates available: candidates should be selected by inbound transformer");
        }

        // outbound.go:712-716 — a continuing Responses turn must be pinned to a
        // candidate that can reach the endpoint which produced the previous
        // response, before the retry loop reads the current candidate.
        SelectResponsesContinuationCandidate(request);

        if (_candidateIndex >= _candidates.Length)
        {
            throw new InvalidOperationException(
                "server internal error, please try again later: all candidates exhausted");
        }

        // Reference model_mapper captures the client's model once per request,
        // before the outbound transformer rewrites it per candidate.
        if (!_originalModelCaptured)
        {
            OriginalModel = request.Model;
            _originalModelCaptured = true;
        }

        // The reference captures this in the inbound transformer
        // (inbound.go:416), before the outbound transform can force provider
        // streaming; capturing here, before the provider transform runs, sees
        // the same client intent.
        if (!_originalStreamCaptured)
        {
            OriginalRequestStream = request.Stream;
            _originalStreamCaptured = true;
        }

        CurrentLlmRequest = request;

        var candidate = _candidates[_candidateIndex];
        var entry = CurrentModelEntry(candidate);

        // outbound.go:528-532 — synchronize the candidate-level protocol with
        // the model currently being attempted before selecting its transformer.
        RefreshCandidateApiFormat(candidate, _modelIndex, request);

        CurrentChannelId = candidate.ChannelId;
        CurrentModel = entry.ActualModel;

        // The reference keeps one transformer per channel and stops it when the
        // enabled-channels cache swaps; the port builds one per attempt, so an
        // abandoned attempt's transformer (and its WebSocket pool) is stopped
        // here before the next one replaces it (C072).
        StopTransformer(_current);
        _current = _factory(candidate, request, _timeProvider.GetUtcNow());

        // The upstream model is per-entry: the client's requested model is only
        // used to select candidates.
        request.Model = entry.ActualModel;

        // outbound.go:741-746 — the continuation expands a stored history into a
        // cloned request; the pipeline's request (state.LlmRequest) keeps the
        // client's own messages, exactly like the reference.
        request = PrepareResponsesAttempt(request);

        var outboundFormat = _current.ApiFormat;
        if (!string.IsNullOrEmpty(candidate.ApiFormat))
        {
            outboundFormat = candidate.ApiFormat;
        }

        // outbound.go:550-563 — channel transform options and the reasoning
        // effort mapping shape the unified request before the provider
        // transformer renders it.
        var settings = ChannelTransformOptions.SettingsOf(candidate);
        var outboundRequest = ChannelTransformOptions.Apply(request, settings);
        outboundRequest = ChannelTransformOptions.ApplyReasoningEffortMapping(outboundRequest, settings);
        outboundRequest = ApplyModelDefaultMaxTokens(outboundRequest, candidate, outboundFormat);

        // orchestrator.go:245,556-562 + claude_code.go:30-43: the Claude Code
        // billing system messages injected by the CLI are only meaningful to the
        // official OAuth claudecode channel; every other candidate strips them
        // before the provider transformer renders the body. The returned copy
        // replaces the per-attempt request, so the shared pipeline request is
        // never mutated.
        if (!(string.Equals(
                candidate.Type,
                ChannelOutboundFactory.ClaudeCodeChannelType,
                StringComparison.Ordinal)
            && ChannelCredentials.IsOAuth(candidate.Credentials)))
        {
            outboundRequest = ClaudeCodeUtils.RemoveBillingSystemMessages(outboundRequest);
        }

        outboundRequest = ChannelTransformOptions.FilterResponseCustomToolMessagesForNonResponsesOutbound(
            outboundRequest,
            outboundFormat);

        // outbound.go:565-567 — a messages-protocol round is never left with an
        // ambiguous stream intent.
        if (outboundRequest.Stream is null && outboundFormat == ApiFormats.AnthropicMessage)
        {
            outboundRequest.Stream = false;
        }

        // outbound.go:569-585 — require-streaming channels serve aggregatable
        // non-streaming requests by forcing the upstream stream and syncing the
        // state request the pipeline reads afterwards.
        if (ChannelTransformOptions.ShouldForceStreamingForCandidate(candidate, outboundRequest))
        {
            outboundRequest.Stream = true;
            outboundRequest.StreamOptions ??= new StreamOptions();
            outboundRequest.StreamOptions.IncludeUsage = true;

            request.Stream = true;
            request.StreamOptions ??= new StreamOptions();
            request.StreamOptions.IncludeUsage = true;

            // outbound.go:777-783 — the pipeline reads the stream intent from
            // the pre-continuation request (state.LlmRequest), which the clone
            // above deliberately does not replace.
            if (CurrentLlmRequest is { } stateRequest)
            {
                stateRequest.Stream = true;
                stateRequest.StreamOptions ??= new StreamOptions();
                stateRequest.StreamOptions.IncludeUsage = true;
            }
        }

        // The options helpers may hand back a rebuilt request; the transform-stage
        // token (Go's ctx, C094 (6)) travels with it so OAuth refreshes see the
        // client's cancellation.
        outboundRequest.RequestCancellationToken = request.RequestCancellationToken;

        return _current.TransformRequest(outboundRequest);
    }

    /// <summary>
    /// The entry the current candidate is attempting. Candidates built by the
    /// model-association selector always carry at least one entry; directly
    /// constructed candidates (the tester, tests) may leave the list empty, in
    /// which case <see cref="ChannelCandidate.ActualModel"/> is the single
    /// entry.
    /// </summary>
    private ChannelCandidateModel CurrentModelEntry(ChannelCandidate candidate)
    {
        if (candidate.Models.Count > 0)
        {
            return candidate.Models[_modelIndex];
        }

        return new ChannelCandidateModel
        {
            RequestModel = string.Empty,
            ActualModel = candidate.ActualModel,
        };
    }

    /// <summary>
    /// Port of refreshCandidateAPIFormat (outbound.go:468-498): prefer the
    /// selector's per-model format table; otherwise preserve an explicitly
    /// populated format; otherwise recompute it from the current entry and the
    /// channel's endpoints using the original client model.
    /// </summary>
    private void RefreshCandidateApiFormat(
        ChannelCandidate candidate,
        int modelIndex,
        LlmRequest? request)
    {
        if (modelIndex < 0 || modelIndex >= candidate.Models.Count)
        {
            return;
        }

        if (candidate.ModelApiFormats is { } formats
            && formats.Count == candidate.Models.Count)
        {
            candidate.ApiFormat = formats[modelIndex];
            return;
        }

        // Preserve an explicitly populated format when no per-model table is
        // available (legacy/specified selectors pick their endpoint up front).
        if (!string.IsNullOrEmpty(candidate.ApiFormat) || request is null)
        {
            return;
        }

        var requestModel = request.Model;
        if (OriginalModel.Length != 0)
        {
            requestModel = OriginalModel;
        }

        var entry = new ChannelModelEntry(
            candidate.Models[modelIndex].RequestModel,
            candidate.Models[modelIndex].ActualModel,
            "direct");
        var channel = new ChannelDescriptor
        {
            Id = candidate.ChannelId,
            Name = candidate.ChannelName,
            OrderingWeight = candidate.OrderingWeight,
            Type = candidate.Type,
            BaseUrl = candidate.BaseUrl,
            Settings = candidate.Settings,
            Endpoints = candidate.Endpoints,
        };
        var endpoints = ChannelEndpointSelection.ApplyForcedApiFormatsForRequest(
            channel,
            [entry],
            requestModel,
            request.RequestType,
            ChannelEndpoints.Resolve(candidate.Type, candidate.Endpoints, candidate.BaseUrl));
        candidate.ApiFormat = ChannelEndpointSelection.SelectApiFormat(endpoints, request);
    }

    /// <summary>
    /// Port of selectResponsesContinuationCandidate (responses_attempt.go:167-202):
    /// when the response being continued belongs to a candidate that can reach
    /// the same upstream endpoint, pin every later attempt to that candidate and
    /// model. The reference also rebuilds the wrapped transformer here; the port
    /// only moves the indexes because the attempt's transformer is created right
    /// after this call.
    /// </summary>
    private void SelectResponsesContinuationCandidate(LlmRequest request)
    {
        var continuation = ResponsesContinuation;
        if (continuation is null || continuation.Previous.Length == 0)
        {
            return;
        }

        continuation.Owner ??= continuation.LoadOwner();
        if (continuation.Owner is null || continuation.Owner.Snapshot is not null)
        {
            return;
        }

        for (var index = _candidateIndex; index < _candidates.Length; index++)
        {
            var candidate = _candidates[index];
            if (candidate is null || candidate.Models.Count == 0)
            {
                continue;
            }

            for (var modelIndex = 0; modelIndex < candidate.Models.Count; modelIndex++)
            {
                RefreshCandidateApiFormat(candidate, modelIndex, request);
                var origin = new ResponsesOrigin(
                    (int)candidate.ChannelId,
                    string.Empty,
                    candidate.ApiFormat ?? string.Empty,
                    candidate.Models[modelIndex].ActualModel,
                    string.Empty,
                    0);
                if (!AxonHub.Api.Orchestration.ResponsesContinuation.OriginMatches(
                        continuation.Owner.Origin,
                        origin))
                {
                    continue;
                }

                _candidateIndex = index;
                _modelIndex = modelIndex;
                return;
            }
        }
    }

    /// <summary>
    /// Port of prepareResponsesAttempt (responses_attempt.go:224-286): computes
    /// the attempt origin, expands a complete continuation snapshot into the
    /// body, and prepends the replayed history to a cloned unified request. The
    /// pipeline's request keeps the client's own messages, like
    /// <c>state.LlmRequest</c> in the reference.
    /// </summary>
    private LlmRequest PrepareResponsesAttempt(LlmRequest request)
    {
        var continuation = ResponsesContinuation;
        if (continuation is null)
        {
            return request;
        }

        SelectResponsesContinuationCandidate(request);

        var origin = new ResponsesOrigin(
            (int)CurrentChannelId,
            string.Empty,
            _current?.ApiFormat ?? string.Empty,
            request.Model,
            string.Empty,
            0);
        if (CurrentCandidate is { ApiFormat: { Length: > 0 } candidateFormat })
        {
            origin = origin with { Format = candidateFormat };
        }

        var body = continuation.PrepareAttemptBody(
            new ResponsesAttemptTarget(origin, ForwardsResponsesId()));

        request.RawRequest?.ResponsesAttempt?.SetOrigin(origin);

        if (!continuation.Complete || continuation.Previous.Length == 0)
        {
            return request;
        }

        // The expanded body is decoded by a fresh Responses inbound transformer,
        // so the merged history is represented exactly as a client turn would
        // be (responses_attempt.go:245-248).
        var parsed = new ResponsesInboundTransformer().TransformRequest(
            new HttpRequestData { Body = body });

        if (!ResponsesJson.TryUnmarshalRawObject(body, out var expandedPayload, out var expandedError))
        {
            throw new ResponsesContinuationException(expandedError ?? "invalid Responses body");
        }

        if (!ResponsesJson.TryUnmarshalRawObject(
                continuation.Original,
                out var originalPayload,
                out var originalError))
        {
            throw new ResponsesContinuationException(originalError ?? "invalid Responses body");
        }

        var expandedItems = ResponsesSessionStore.ResponseSessionInputItems(
            expandedPayload?.GetValueOrDefault("input"));
        var originalItems = ResponsesSessionStore.ResponseSessionInputItems(
            originalPayload?.GetValueOrDefault("input"));
        if (expandedItems.Count < originalItems.Count)
        {
            throw ResponsesRecovery.Error();
        }

        var historyItems = expandedItems.GetRange(0, expandedItems.Count - originalItems.Count);
        var historyMessages = new List<Message>();
        if (historyItems.Count > 0)
        {
            var historyPayload = new Dictionary<string, byte[]>(expandedPayload!);
            historyPayload["input"] = ResponsesSessionStore.MarshalRawArray(historyItems);

            // Instructions shape the next turn, not the replayed history.
            historyPayload.Remove("instructions");
            var historyBody = ResponsesSessionStore.MarshalRawObject(historyPayload);
            var historyRequest = new ResponsesInboundTransformer().TransformRequest(
                new HttpRequestData { Body = historyBody });
            historyMessages = historyRequest.Messages;
        }

        // Go's struct copy keeps every other field shared; only Messages and the
        // provider sidecar are replaced (responses_attempt.go:282-285).
        var cloned = request.ShallowClone();
        var messages = new List<Message>(historyMessages.Count + request.Messages.Count);
        messages.AddRange(historyMessages);
        messages.AddRange(request.Messages);
        cloned.Messages = messages;
        cloned.ProviderExtensions = parsed.ProviderExtensions;

        return cloned;
    }

    /// <summary>
    /// Port of forwardsResponsesID (responses_attempt.go:216-222): probes the
    /// provider transformer to learn whether <c>previous_response_id</c> survives
    /// its transport finalization.
    /// </summary>
    internal bool ForwardsResponsesId()
    {
        var current = _current;
        if (current is ITransportRequestFinalizer finalizer)
        {
            var probe = finalizer.FinalizeTransportRequest(new HttpRequestData
            {
                Body = Encoding.UTF8.GetBytes("{\"previous_response_id\":\"probe\"}"),
            });

            return probe is not null
                   && ResponsesJson.TopLevelString(probe.Body, "previous_response_id") == "probe";
        }

        return current is not null
               && string.Equals(
                   current.ApiFormat,
                   ApiFormats.OpenAIResponseWebSocket,
                   StringComparison.Ordinal);
    }

    /// <summary>
    /// Port of effectiveResponsesRequestBody (responses_attempt.go:288-302): the
    /// body later outbound middlewares replay — the expanded continuation body
    /// when one exists, otherwise the client's inbound body.
    /// </summary>
    internal byte[]? EffectiveResponsesRequestBody()
    {
        if (ResponsesContinuation?.Effective is { } effective)
        {
            return effective;
        }

        // state.RawRequest and state.LlmRequest.RawRequest are the same inbound
        // request in the reference; the port reads it through the unified
        // request, which is the object the inbound transformer populated.
        var inbound = CurrentLlmRequest?.RawRequest;
        if (inbound?.Body is { Length: > 0 } body)
        {
            return body;
        }

        if (inbound?.JsonBody is { Length: > 0 } jsonBody)
        {
            return jsonBody;
        }

        return inbound?.Body;
    }

    /// <summary>
    /// Port of applyModelDefaultMaxTokens (transform_options.go:49-70): copy the
    /// model card's output limit onto a copy of the request so Anthropic
    /// outbound conversion can use it as <c>max_tokens</c>, but only when the
    /// client set no output cap and the round targets Anthropic messages.
    /// </summary>
    private static LlmRequest ApplyModelDefaultMaxTokens(
        LlmRequest request,
        ChannelCandidate candidate,
        string outboundFormat)
    {
        if (candidate.DefaultMaxTokens <= 0)
        {
            return request;
        }

        if (outboundFormat != ApiFormats.AnthropicMessage)
        {
            return request;
        }

        if (request.MaxTokens is not null || request.MaxCompletionTokens is not null)
        {
            return request;
        }

        if (request.TransformOptions?.DefaultMaxTokens is not null)
        {
            return request;
        }

        // Go copies the request struct, whose TransformOptions is an inline
        // struct: the copy's field is replaced without touching the original.
        // The port clones the mutable TransformOptions object to match.
        var cloned = request.ShallowClone();
        var options = request.TransformOptions is null
            ? new TransformOptions()
            : new TransformOptions
            {
                ArrayInstructions = request.TransformOptions.ArrayInstructions,
                ArrayInputs = request.TransformOptions.ArrayInputs,
                DefaultMaxTokens = request.TransformOptions.DefaultMaxTokens,
            };
        options.DefaultMaxTokens = candidate.DefaultMaxTokens;
        cloned.TransformOptions = options;

        return cloned;
    }

    public LlmResponse TransformResponse(HttpResponseData? response) => Current.TransformResponse(response);

    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
        => Current.TransformStream(request, stream);

    public ResponseError TransformError(HttpClientError? error) => Current.TransformError(error);

    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => Current.AggregateStreamChunks(request, chunks, out meta);

    /// <summary>
    /// Port of SupportsCodexResponseHeaders (outbound.go:505-510): whether the
    /// current provider transformer advertises the Codex response-header
    /// contract. The result carries it to api.writeForwardResponseHeaders,
    /// which forwards X-Reasoning-Included only when it is true (§142).
    /// </summary>
    public bool SupportsCodexResponseHeaders()
        => _current is ISupportsCodexResponseHeaders supports
            && supports.SupportsCodexResponseHeaders();

    /// <summary>
    /// Port of p.CustomizeExecutor (outbound.go:1164-1196): proxy selection,
    /// then the Command Code redirect policy, then the per-attempt provider
    /// transformer's own transport customization.
    /// </summary>
    public IExecutor CustomizeExecutor(IExecutor executor)
    {
        var customized = executor;

        // outbound.go:1173-1186, step 1: the tester's per-request proxy wins
        // over the channel's persisted settings.proxy; without an override the
        // channel's own client is used (the shared client when it configures
        // no proxy).
        if (_proxyExecutors is not null)
        {
            if (_proxyOverride is not null)
            {
                customized = _proxyExecutors.CreateOverride(_proxyOverride);
            }
            else if (CurrentCandidate is { } candidate)
            {
                customized = _proxyExecutors.Resolve(candidate);
            }
        }

        // channel_llm.go:757-763: both Command Code channel types are built
        // with a client that refuses HTTPS-to-HTTP redirects, so a
        // Bearer-bearing inference request can never be replayed in cleartext.
        // Applying it after proxy selection reproduces WithProxy's option
        // preservation, including for the tester's override client.
        if (CurrentCandidate?.Type is ChannelOutboundFactory.CommandcodeChannelType
            or ChannelOutboundFactory.CommandcodeAnthropicChannelType)
        {
            customized = customized.WithRejectHttpsDowngrade();
        }

        var current = _current;
        return current is IChannelCustomizedExecutor custom
            ? custom.CustomizeExecutor(customized)
            : customized;
    }

    /// <summary>
    /// Port of finalizeTransportRequest (outbound.go:912-925): applies the
    /// current provider transformer's transport cleanup to the raw request.
    /// </summary>
    public HttpRequestData? FinalizeTransportRequest(HttpRequestData? request)
    {
        if (request is null)
        {
            return null;
        }

        var current = _current;
        return current is ITransportRequestFinalizer finalizer
            ? finalizer.FinalizeTransportRequest(request) ?? request
            : request;
    }

    /// <summary>Port of HasMoreChannels: is there a candidate after the current one?</summary>
    public bool HasMoreChannels() => _candidateIndex + 1 < _candidates.Length;

    /// <summary>
    /// Terminal provider authentication, quota, and content-filter failures
    /// must be returned to the caller rather than sent to another channel.
    /// </summary>
    public bool CanSwitchChannel(Exception error)
        => !ChannelRetryPredicate.IsTerminalUpstreamError(error);

    /// <summary>Port of NextChannel: advance the candidate and count the retry attempt.</summary>
    public Exception? TryNextChannel(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        // outbound.go:991-993 — a retry must not start while the previous
        // attempt still owns the source's accounting tail.
        Lifecycle?.WaitForAccounting();

        // outbound.go:823 — release the previous attempt's fan-out thread
        // before switching candidates.
        ResetPassThroughStreamState();

        _candidateIndex++;
        _modelIndex = 0;
        if (_candidateIndex >= _candidates.Length)
        {
            return new InvalidOperationException("no more candidates available for retry");
        }

        // outbound.go:825 — a switched channel decides pass-through afresh.
        PassThroughApplied = false;

        var candidate = _candidates[_candidateIndex];
        RecordSelection(candidate);

        // outbound.go:827-830 — align the protocol with the new candidate's
        // first model and rebuild its transformer before the next attempt.
        RefreshCandidateApiFormat(candidate, _modelIndex, CurrentLlmRequest);
        StopTransformer(_current);
        _current = CurrentLlmRequest is null
            ? null
            : _factory(candidate, CurrentLlmRequest, _timeProvider.GetUtcNow());

        return null;
    }

    /// <summary>Port of CanRetry (outbound.go:766-818).</summary>
    public bool CanRetry(Exception error)
    {
        if (_candidateIndex >= _candidates.Length)
        {
            return false;
        }

        if (ChannelRetryPredicate.IsTerminalUpstreamError(error))
        {
            return false;
        }

        // Local circuit-breaker bounce: never retry the same channel
        // (outbound.go:1040-1042).
        if (error is ModelCircuitBreakerSkipException)
        {
            return false;
        }

        // Local admission rejection: the same channel cannot make progress until
        // its local queue/RPM state changes, so bounce immediately to the next
        // candidate instead of burning a same-channel retry
        // (outbound.go: isChannelQueueError / isLocalRPMExhaustedError).
        if (GoErrors.As<ChannelQueueError>(error) is not null
            || GoErrors.As<LocalRpmExhaustedError>(error) is not null)
        {
            return false;
        }

        // Empty response detection: retry the same channel so the pipeline can
        // re-execute the request.
        if (GoErrors.IsAny(
            error,
            PipelineErrors.EmptyResponse,
            PipelineErrors.EmptyStreamChunks,
            PipelineErrors.EmptyAggregatedBody))
        {
            return true;
        }

        var candidate = _candidates[_candidateIndex];

        // 429: the upstream is rate-limiting this channel; a same-channel retry
        // would burn the budget without a chance of success. A channel that
        // lists 429 in its retryable status codes opts out and falls through to
        // the per-channel rules (outbound.go:1040-1055).
        if (ChannelRetryPredicate.ExtractStatusCode(error) == 429
            && !ChannelRetriesOn429(candidate))
        {
            return false;
        }

        // outbound.go:807-812 — a candidate with another model left is always
        // worth a same-channel retry, before consulting the channel rules.
        if (_modelIndex + 1 < candidate.Models.Count)
        {
            return true;
        }

        return ChannelRetryPredicate.IsRetryable(error, candidate.RetrySettings);
    }

    /// <summary>
    /// Port of channelRetriesOn429 (retry.go:47-61): only an explicit 429 in
    /// the channel's retryable status codes asks for a same-channel retry on a
    /// rate limit; every other channel keeps switching.
    /// </summary>
    private static bool ChannelRetriesOn429(ChannelCandidate candidate)
        => candidate.RetrySettings.RetryableStatusCodes.Contains(429);

    /// <summary>
    /// retry_channel_refresh.go's errChannelUnavailableForRetry: the pipeline
    /// stops retrying this candidate and switches to the next one, or ends the
    /// request when there is none.
    /// </summary>
    public static readonly Exception ChannelUnavailableForRetry = new PipelineSentinelException(
        "current channel is no longer available for retry");

    /// <summary>
    /// Port of refreshChannelBeforeRetry (retry_channel_refresh.go): re-read the
    /// stored channel so a credential disabled mid-request is not retried, and
    /// a channel that left service stops consuming the same-channel budget.
    /// Transient read failures leave the pinned candidate untouched.
    /// </summary>
    private Exception? RefreshChannelBeforeRetry()
    {
        if (_channelRefresh is null
            || CurrentCandidate is not { } candidate
            || CurrentLlmRequest is null)
        {
            return null;
        }

        var lookup = _channelRefresh.GetEnabledChannel(candidate.ChannelId);
        if (lookup.Failed)
        {
            // A degraded read must never cancel an otherwise legitimate retry.
            return null;
        }

        if (lookup.Channel is not { } fresh)
        {
            // Deleted, disabled or unknown: the channel cannot serve anymore.
            return ChannelUnavailableForRetry;
        }

        // A channel that authenticates without any credential has nothing to
        // refresh. Check the pinned snapshot, not the stored row: a channel
        // served with a credential that has since lost all of them must still
        // end the retry.
        if (ChannelCredentials.GetAllCredentialRefs(candidate.Credentials).Count == 0)
        {
            return null;
        }

        var currentRef = CurrentRetryCredentialRef(candidate);
        if (currentRef.Length == 0)
        {
            return null;
        }

        List<DisabledApiKey> disabledKeys;
        try
        {
            disabledKeys = ChannelWriteSerialization.ParseDisabledApiKeys(fresh.DisabledApiKeys);
        }
        catch (System.Text.Json.JsonException)
        {
            // A stored document the reference's ent scan would reject is a
            // degraded read; leave the pinned candidate untouched.
            return null;
        }

        var enabledRefs = ChannelCredentials.GetEnabledCredentialRefs(
            fresh.Credentials,
            disabledKeys,
            _timeProvider.GetUtcNow());
        if (enabledRefs.Contains(currentRef, StringComparer.Ordinal))
        {
            return null;
        }

        if (enabledRefs.Count == 0)
        {
            // The candidate was credentialed and the channel has no usable
            // credential left; continuing would resend a rejected credential.
            return ChannelUnavailableForRetry;
        }

        var refreshed = candidate.Clone();
        ApplyStoredChannel(refreshed, fresh);

        IOutboundTransformer rebuilt;
        try
        {
            rebuilt = _factory(refreshed, CurrentLlmRequest, _timeProvider.GetUtcNow());
        }
        catch (Exception)
        {
            // The refreshed snapshot is the only way this candidate can
            // authenticate with an enabled credential; without a transformer
            // the retry cannot succeed.
            return ChannelUnavailableForRetry;
        }

        StopTransformer(_current);
        _current = rebuilt;
        _candidates[_candidateIndex] = refreshed;
        return null;
    }

    /// <summary>
    /// The credential the current attempt authenticated with. OAuth channels do
    /// not pass through the key provider, so they carry the fixed OAuth
    /// credential reference, matching the auto-disable bookkeeping.
    /// </summary>
    private string CurrentRetryCredentialRef(ChannelCandidate candidate)
    {
        var apiKey = CurrentApiKey ?? string.Empty;
        if (apiKey.Length == 0 && ChannelCredentials.IsOAuth(candidate.Credentials))
        {
            apiKey = ChannelCredentials.OAuthCredentialRef;
        }

        return apiKey;
    }

    /// <summary>
    /// Applies the stored channel fields to the refreshed candidate copy while
    /// preserving the candidate's model list, score, placement and per-request
    /// key override.
    /// </summary>
    private static void ApplyStoredChannel(
        ChannelCandidate candidate,
        ChannelDescriptor channel)
    {
        candidate.ChannelName = channel.Name;
        candidate.OrderingWeight = channel.OrderingWeight;
        candidate.UpdatedAt = channel.UpdatedAt;
        candidate.Type = channel.Type;
        candidate.BaseUrl = channel.BaseUrl;
        candidate.Credentials = channel.Credentials;
        candidate.DisabledApiKeys = channel.DisabledApiKeys;
        candidate.RetrySettings = ChannelRetrySettings.Parse(channel.Settings);
        candidate.OverrideSettings = ChannelOverrideSettings.Parse(channel.Settings);
        candidate.RateLimitSettings = ChannelRateLimitSettings.Parse(channel.Settings);
        candidate.PassThroughSettings = ChannelPassThroughSettings.Parse(channel.Settings);
        candidate.DefaultTestModel = ModelCandidateSelector.ResolveDefaultTestModel(channel);
        candidate.Settings = channel.Settings;
        candidate.Endpoints = channel.Endpoints;
        candidate.Tags = channel.Tags;
        candidate.Policies = channel.Policies;
    }

    /// <summary>Port of PrepareForRetry: count the retry attempt on the same channel.</summary>
    public Exception? TryPrepareForRetry(CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        // retry_channel_refresh.go: re-read the stored channel before spending
        // any of the same-channel budget.
        var refreshError = RefreshChannelBeforeRetry();
        if (refreshError is not null)
        {
            return refreshError;
        }

        // outbound.go:1095-1097 — a same-channel retry must also wait for the
        // previous attempt's accounting tail (C293).
        Lifecycle?.WaitForAccounting();

        // outbound.go:739 — a same-channel retry re-runs the pass-through check.
        PassThroughApplied = false;

        // outbound.go:832 — an abandoned attempt's pending events must never
        // reach the retried attempt's consumer.
        ResetPassThroughStreamState();

        var candidate = _candidates[_candidateIndex];

        // outbound.go:838-856 — advance to the candidate's next model when there
        // is one, refreshing its protocol and rebuilding the transformer.
        if (_modelIndex + 1 < candidate.Models.Count)
        {
            _modelIndex++;
            RefreshCandidateApiFormat(candidate, _modelIndex, CurrentLlmRequest);
            if (CurrentLlmRequest is not null)
            {
                StopTransformer(_current);
                _current = _factory(candidate, CurrentLlmRequest, _timeProvider.GetUtcNow());
            }
        }

        RecordSelection(candidate);
        return null;
    }

    private void RecordSelection(ChannelCandidate candidate)
        => _metrics.RecordSelection(candidate.ChannelId, _timeProvider.GetUtcNow());

    /// <summary>
    /// Stops the current attempt's transformer. The reference's
    /// <c>stopChannelOutbounds</c> runs when the enabled-channels cache swaps
    /// (internal/server/biz/channel.go:326-349); the port has no channel
    /// teardown hook and builds one transformer per attempt, so the orchestrator
    /// stops the attempt's transformer once the attempt ends (C072).
    /// </summary>
    public void Stop() => StopTransformer(_current);

    /// <summary>
    /// Port of the structural <c>interface{ Stop() }</c> check in
    /// <c>stopChannelOutbounds</c>: only transformers that own connections
    /// implement <see cref="IStoppableOutbound"/> (responses, codex and the
    /// opencode session-header wrapper).
    /// </summary>
    private static void StopTransformer(IOutboundTransformer? transformer)
    {
        if (transformer is IStoppableOutbound stoppable)
        {
            stoppable.Stop();
        }
    }
}
