using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.OpenAI.Codex;

/// <summary>Port of codex.Params (llm/transformer/openai/codex/outbound.go:78-85).</summary>
public sealed class CodexOutboundParams
{
    public ITokenGetter? TokenProvider { get; set; }

    public string BaseUrl { get; set; } = string.Empty;

    public string Transport { get; set; } = string.Empty;

    public string AlphaSearchPath { get; set; } = string.Empty;

    /// <summary>Endpoint-level path forwarded to the Responses outbound.</summary>
    public string EndpointPath { get; set; } = string.Empty;

    /// <summary>
    /// Port-only: the owning channel id, used as the <c>ChannelID</c> half of
    /// the WebSocket pool key (websocket_executor.go:314, 568). Zero when the
    /// transformer is built without a channel.
    /// </summary>
    public long ChannelId { get; set; }

    /// <summary>
    /// The resolved channel default test model used to call the image
    /// generation tool. Empty values and image models use the fallback.
    /// </summary>
    public string ImageMainModel { get; set; } = string.Empty;

    /// <summary>
    /// Port-only seam for <c>shared.GetSessionID(ctx)</c> (difference C062).
    /// Return null for "no session"; an empty string is a present empty
    /// session.
    /// </summary>
    public Func<string?>? SessionIdProvider { get; set; }

    /// <summary>Port-only seam for <c>shared.GetSessionScope(ctx)</c> (difference C062).</summary>
    public Func<string?>? SessionScopeProvider { get; set; }

    /// <summary>
    /// Port-only (C096): record the access token actually sent upstream so the
    /// request execution can audit the selected channel key. The reference
    /// publishes it through the wrapped API-key provider's context value; OAuth
    /// tokens are not channel keys and are never audited.
    /// </summary>
    public bool AuditApiKey { get; set; }

    /// <summary>
    /// Port-only: 1-based index over the channel's full key list for the audit
    /// row; null for single-key channels and non-audited tokens.
    /// </summary>
    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of llm/transformer/openai/codex/outbound.go: the Codex proxy outbound.
/// It normally talks to the Codex Responses upstream over SSE and adapts
/// requests accordingly; compatible relays that return a completed JSON
/// response are also supported for non-stream callers.
/// </summary>
public sealed partial class CodexOutboundTransformer : IOutboundTransformer, IApiKeyProvenance,
    IPassThroughBodyPolicy, ITransportRequestFinalizer, IChannelCustomizedExecutor,
    ISupportsCodexResponseHeaders, IStoppableOutbound
{
    internal const string CodexBaseUrl = "https://chatgpt.com/backend-api/codex#";

    internal const string CodexApiUrl = "https://chatgpt.com/backend-api/codex/responses";

    private static readonly string[] ResponsesBlockedPassThroughFields =
    [
        "max_output_tokens",
        "max_completion_tokens",
        "max_tokens",
    ];

    /// <summary>Ask the upstream for encrypted reasoning content (Codex CLI parity).</summary>
    private static readonly string[] EncryptedReasoningInclude = ["reasoning.encrypted_content"];

    private readonly ITokenGetter _tokens;
    private readonly string _transport;

    private readonly long _channelId;
    private readonly string _baseUrl;
    private readonly string _alphaSearchPath;
    private readonly string _imageMainModel;

    /// <summary>
    /// Whether the configured upstream is the official Codex backend
    /// (chatgpt.com). Official endpoints always stream SSE, so they keep the
    /// DoStream path; only compatible relays get the JSON passthrough path.
    /// </summary>
    private readonly bool _official;

    /// <summary>The existing Responses outbound reused for payload building.</summary>
    private readonly ResponsesOutboundTransformer _responsesOutbound;
    private readonly Func<string?>? _sessionIdProvider;

    private readonly bool _auditApiKey;
    private readonly int? _channelApiKeyIndex;

    /// <summary>The token the latest TransformRequest sent; null when not audited.</summary>
    private string? _selectedApiKey;


    public CodexOutboundTransformer(CodexOutboundParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var tokenProvider = parameters.TokenProvider
            ?? throw new ArgumentException("token provider is required", nameof(parameters));

        var baseUrl = parameters.BaseUrl;
        // Compatibility with the old codex channel base url.
        if (baseUrl.Length == 0 || baseUrl == "https://api.openai.com/v1")
        {
            baseUrl = CodexBaseUrl;
        }

        var alphaSearchPath = parameters.AlphaSearchPath;
        if (alphaSearchPath.Length == 0)
        {
            alphaSearchPath = "/alpha/search";
        }

        var imageMainModel = parameters.ImageMainModel.Trim();
        if (imageMainModel.Length == 0
            || imageMainModel.ToLowerInvariant().StartsWith("gpt-image-", StringComparison.Ordinal))
        {
            imageMainModel = CodexConstants.DefaultImageMainModel;
        }

        var official = IsOfficialCodexBaseUrl(baseUrl);

        // The underlying responses outbound requires baseURL/apiKey. Only its
        // request body logic is needed; URL and auth are overridden below.
        _responsesOutbound = new ResponsesOutboundTransformer(new ResponsesOutboundConfig
        {
            BaseUrl = baseUrl,
            EndpointPath = parameters.EndpointPath,
            ApiKeyProvider = new StaticApiKeyProvider("dummy"),
            Transport = parameters.Transport,
            // Responses Lite keeps its tool definitions in an `additional_tools`
            // input item instead of the top-level `tools` array. That item
            // belongs to the private Codex protocol, so it is replayed only to
            // the official backend; relays are not assumed to implement it. The
            // same rule drops the Responses Lite header for relays in
            // TransformRequest.
            PreserveAdditionalTools = official,
            ChannelId = parameters.ChannelId,
            SessionIdProvider = parameters.SessionIdProvider,
            SessionScopeProvider = parameters.SessionScopeProvider,
        });

        _channelId = parameters.ChannelId;
        _tokens = tokenProvider;
        _transport = parameters.Transport;
        _baseUrl = baseUrl.EndsWith("##", StringComparison.Ordinal) ? baseUrl[..^2] : baseUrl;
        _alphaSearchPath = alphaSearchPath;
        _imageMainModel = imageMainModel;
        _official = official;
        _sessionIdProvider = parameters.SessionIdProvider;
        _auditApiKey = parameters.AuditApiKey;
        _channelApiKeyIndex = parameters.ChannelApiKeyIndex;
    }

    /// <summary>Port of codex.OutboundTransformer.APIFormat (outbound.go:154-159).</summary>
    public string ApiFormat => _transport == ResponsesOutboundTransformer.TransportWebSocket
        ? ApiFormats.OpenAIResponseWebSocket
        : ApiFormats.OpenAIResponse;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _channelApiKeyIndex;

    /// <summary>Port of <c>TokenProvider()</c>.</summary>
    public ITokenGetter TokenProvider => _tokens;

    /// <summary>Port of <c>SupportsCodexResponseHeaders</c>.</summary>
    public bool SupportsCodexResponseHeaders() => true;

    /// <summary>Go: <c>isOfficialCodex</c>.</summary>
    internal bool IsOfficialCodex() => _official;

    /// <summary>Go: <c>isOfficialCodexBaseURL</c>.</summary>
    /// <remarks>
    /// The host is compared as a whole: a relay reached through a path or
    /// hostname that merely mentions the official domain is still a relay.
    /// </remarks>
    internal static bool IsOfficialCodexBaseUrl(string baseUrl)
    {
        string host;
        if (CodexUtils.TryParseGoUrl(baseUrl, out var parsed) && parsed.Host.Length > 0)
        {
            host = GoHostname(parsed.Host);
        }
        else
        {
            // Tolerate a base URL written without a scheme, e.g. "chatgpt.com/v1".
            var remainder = baseUrl;
            if (remainder.StartsWith("//", StringComparison.Ordinal))
            {
                remainder = remainder[2..];
            }

            if (remainder.StartsWith('/'))
            {
                remainder = remainder[1..];
            }

            var slash = remainder.IndexOf('/');
            host = slash < 0 ? remainder : remainder[..slash];

            var colon = host.IndexOf(':');
            if (colon >= 0)
            {
                host = host[..colon];
            }
        }

        host = host.ToLowerInvariant();

        return host == "chatgpt.com" || host.EndsWith(".chatgpt.com", StringComparison.Ordinal);
    }

    /// <summary>Port of <c>net/url.URL.Hostname</c>.</summary>
    private static string GoHostname(string host)
    {
        var colon = host.LastIndexOf(':');
        if (colon >= 0 && IsValidOptionalPort(host[colon..]))
        {
            host = host[..colon];
        }

        return host.Length >= 2 && host[0] == '[' && host[^1] == ']'
            ? host[1..^1]
            : host;
    }

    /// <summary>Port of <c>net/url.validOptionalPort</c>.</summary>
    private static bool IsValidOptionalPort(string port)
    {
        if (port.Length == 0)
        {
            return true;
        }

        if (port[0] != ':')
        {
            return false;
        }

        for (var i = 1; i < port.Length; i++)
        {
            if (port[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Port of <c>AllowPassThroughBody</c>.</summary>
    /// <remarks>
    /// Go probes the raw body with gjson, which tolerates malformed documents;
    /// the port parses strictly and allows malformed bodies through (difference
    /// C094, matching the C033/C058/C060 convention).
    /// </remarks>
    public bool AllowPassThroughBody(LlmRequest? llmRequest, HttpRequestData? providerRequest)
    {
        _ = providerRequest;

        if (llmRequest is null
            || llmRequest.ApiFormat != ApiFormats.OpenAIResponse
            || llmRequest.RawRequest is null)
        {
            return true;
        }

        var body = llmRequest.RawRequest.Body;
        if (body is null || body.Length == 0)
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return true;
            }

            foreach (var field in ResponsesBlockedPassThroughFields)
            {
                if (document.RootElement.TryGetProperty(field, out _))
                {
                    return false;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            // gjson would still find a key inside some malformed objects; the
            // strict parser cannot, so the body is not treated as blocked.
            return true;
        }
    }

    /// <summary>Port of <c>TransformError</c>.</summary>
    public ResponseError TransformError(HttpClientError? error) => _responsesOutbound.TransformError(error);

    /// <summary>Port of <c>TransformRequest</c>.</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("request is nil");
        }

        if (request.RequestType == RequestTypes.AlphaSearch)
        {
            return TransformAlphaSearchRequest(request);
        }

        var rawSessionId = string.Empty;
        var rawOriginator = string.Empty;
        var rawUserAgent = string.Empty;
        var rawTurnMetadata = string.Empty;

        Dictionary<string, List<string>>? rawHeaders = null;

        if (request.RawRequest is { } rawRequest && rawRequest.Headers is not null)
        {
            rawHeaders = rawRequest.Headers;
            rawSessionId = HttpClientUtils.HeaderGet(rawHeaders, CodexHeaders.SessionHeader);
            if (rawSessionId.Length == 0)
            {
                rawSessionId = HttpClientUtils.HeaderGet(rawHeaders, CodexHeaders.SessionHeaderHyphen);
            }

            // Remove the underscore variant so it cannot leak upstream via
            // MergeInboundRequest.
            HttpClientUtils.HeaderDel(rawHeaders, CodexHeaders.SessionHeader);
            rawOriginator = HttpClientUtils.HeaderGet(rawHeaders, "Originator");
            rawUserAgent = HttpClientUtils.HeaderGet(rawHeaders, "User-Agent");
            rawTurnMetadata = HttpClientUtils.HeaderGet(rawHeaders, CodexHeaders.TurnMetadataHeader);

            // Responses Lite selects a private Codex protocol mode. It is not
            // identity metadata: never fabricate it for OpenAI-compatible
            // clients. A client-selected Lite request is retained only for the
            // official Codex backend; relays are not assumed to implement the
            // same private protocol.
            if (!IsOfficialCodex()
                || !string.Equals(
                    HttpClientUtils.HeaderGet(rawHeaders, CodexHeaders.ResponsesLiteHeader).Trim(),
                    "true",
                    StringComparison.OrdinalIgnoreCase))
            {
                HttpClientUtils.HeaderDel(rawHeaders, CodexHeaders.ResponsesLiteHeader);
            }
        }

        // Go passes ctx to the token provider (outbound.go); the port carries it
        // on the request (C094 (6)).
        var creds = _tokens.Get(request.RequestCancellationToken);
        if (_auditApiKey)
        {
            _selectedApiKey = creds.AccessToken;
        }

        // Parse the account id from the access token JWT.
        var accountId = CodexUtils.ExtractChatGptAccountIdFromJwt(creds.AccessToken);

        // Clone the request so upstream pipeline state is not mutated (Go: reqCopy := *llmReq).
        var reqCopy = request.ShallowClone();
        var originalRequestType = reqCopy.RequestType;
        var isImageRequest = originalRequestType == RequestTypes.Image;

        // Codex expects a Responses API payload with some strict rules.
        // Always enable stream except for compact requests, and disable store.
        reqCopy.Stream = originalRequestType != RequestTypes.Compact;
        reqCopy.Store = false;

        // Codex recommends parallel tool calls.
        reqCopy.ParallelToolCalls = true;

        reqCopy.TransformerMetadata ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (isImageRequest)
        {
            reqCopy.Model = _imageMainModel;
            reqCopy.TransformerMetadata[ResponsesImageConvert.ImageGenerationToolModelMetadataKey] =
                ResponsesConvertUtil.ToElement(request.Model);
        }
        else
        {
            // Ask for encrypted reasoning content so the downstream can surface
            // reasoning blocks.
            if (!reqCopy.TransformerMetadata.ContainsKey("include"))
            {
                reqCopy.TransformerMetadata["include"] =
                    ResponsesConvertUtil.ToElement(EncryptedReasoningInclude);
            }

            if (string.IsNullOrEmpty(reqCopy.ReasoningSummary))
            {
                // Enable reasoning summary for Codex CLI requests.
                reqCopy.ReasoningSummary = "auto";
            }
        }

        // Codex Responses rejects token limit fields, so strip them out.
        reqCopy.MaxCompletionTokens = null;
        reqCopy.MaxTokens = null;
        reqCopy.Metadata = null;

        // The Codex backend rejects the `user` field with a 400 Bad Request, so
        // strip it as well: Chat Completions clients may set it, and the shared
        // Responses outbound would otherwise forward it upstream.
        reqCopy.User = null;

        // Go's TransformOptions is a value struct, so this assignment only
        // affects the copy; the port's class must be cloned (difference C094).
        reqCopy.TransformOptions = new TransformOptions
        {
            ArrayInstructions = request.TransformOptions?.ArrayInstructions,
            ArrayInputs = true,
            DefaultMaxTokens = request.TransformOptions?.DefaultMaxTokens,
        };

        var httpRequest = _responsesOutbound.TransformRequest(reqCopy);
        var headers = httpRequest.Headers ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);
        httpRequest.Headers = headers;

        if (isImageRequest)
        {
            // Keep the Responses wire format so pass-through cannot replace the
            // converted payload or response with the incompatible Images
            // format. RequestType alone selects the image response conversion.
            httpRequest.RequestType = originalRequestType!;
        }

        // Overwrite auth.
        httpRequest.Auth = new AuthConfig
        {
            Type = AuthTypes.Bearer,
            ApiKey = creds.AccessToken,
        };

        // Compact requests expect a JSON response; everything else expects SSE.
        HttpClientUtils.HeaderSet(
            headers,
            "Accept",
            request.RequestType == RequestTypes.Compact ? "application/json" : "text/event-stream");

        HttpClientUtils.HeaderDel(headers, "User-Agent");

        HttpClientUtils.HeaderSet(
            headers,
            "Originator",
            rawOriginator.Length > 0 ? rawOriginator : CodexConstants.AxonHubOriginator);

        if (rawUserAgent.Length > 0)
        {
            HttpClientUtils.HeaderSet(headers, "User-Agent", rawUserAgent);
        }

        foreach (var header in CodexHeaders.PassthroughHeaders)
        {
            var value = HttpClientUtils.HeaderGet(rawHeaders, header);
            if (value.Length > 0)
            {
                HttpClientUtils.HeaderSet(headers, header, value);
            }
        }

        if (rawSessionId.Length > 0)
        {
            HttpClientUtils.HeaderSet(headers, CodexHeaders.SessionHeaderHyphen, rawSessionId);
        }
        else if (CodexHeaders.ExtractSessionIdFromTurnMetadata(rawTurnMetadata) is { Length: > 0 } turnSessionId)
        {
            HttpClientUtils.HeaderSet(headers, CodexHeaders.SessionHeaderHyphen, turnSessionId);
        }
        else if (HttpClientUtils.HeaderGet(headers, CodexHeaders.SessionHeaderHyphen).Length == 0)
        {
            if (_sessionIdProvider?.Invoke() is { } contextSessionId)
            {
                HttpClientUtils.HeaderSet(headers, CodexHeaders.SessionHeaderHyphen, contextSessionId);
            }
            else
            {
                HttpClientUtils.HeaderSet(headers, CodexHeaders.SessionHeaderHyphen, Guid.NewGuid().ToString());
            }
        }

        // Fabricate the remaining Codex identity headers for non-Codex inbound
        // clients so the upstream always sees a complete Codex session shape.
        var sessionId = HttpClientUtils.HeaderGet(headers, CodexHeaders.SessionHeaderHyphen);
        var windowId = sessionId + ":0";

        if (HttpClientUtils.HeaderGet(headers, CodexHeaders.ThreadIdHeader).Length == 0)
        {
            // Codex clients send Thread-Id equal to Session-Id (both identify the
            // conversation/thread); keep thread-scoped upstream behavior (e.g.
            // prompt caching) consistent for non-Codex clients too.
            HttpClientUtils.HeaderSet(headers, CodexHeaders.ThreadIdHeader, sessionId);
        }

        if (HttpClientUtils.HeaderGet(headers, CodexHeaders.WindowIdHeader).Length == 0)
        {
            HttpClientUtils.HeaderSet(headers, CodexHeaders.WindowIdHeader, windowId);
        }

        if (HttpClientUtils.HeaderGet(headers, CodexHeaders.TurnMetadataHeader).Length == 0)
        {
            // Deterministic per-account installation id derived from the
            // ChatGPT account id.
            var installationId = accountId.Length > 0
                ? CodexUtils.NewUuidV5(CodexUtils.NameSpaceOid, Encoding.UTF8.GetBytes(accountId))
                : string.Empty;

            var turnMetadata = new TurnMetadata
            {
                InstallationId = installationId,
                SessionId = sessionId,
                ThreadId = sessionId,
                TurnId = Guid.NewGuid().ToString(),
                WindowId = windowId,
                RequestKind = "turn",
                ThreadSource = "user",
                Sandbox = "none",
                TurnStartedAtUnixMs = CodexHeaders.TurnStartedAtUnixMs(sessionId),
            };

            HttpClientUtils.HeaderSet(headers, CodexHeaders.TurnMetadataHeader, GoJson.MarshalToString(turnMetadata));
        }

        if (HttpClientUtils.HeaderGet(headers, CodexHeaders.ClientRequestIdHeader).Length == 0)
        {
            HttpClientUtils.HeaderSet(headers, CodexHeaders.ClientRequestIdHeader, Guid.NewGuid().ToString());
        }

        if (HttpClientUtils.HeaderGet(headers, CodexHeaders.BetaFeaturesHeader).Length == 0)
        {
            HttpClientUtils.HeaderSet(headers, CodexHeaders.BetaFeaturesHeader, CodexConstants.FabricatedBetaFeatures);
        }

        if (accountId.Length > 0)
        {
            HttpClientUtils.HeaderSet(headers, "Chatgpt-Account-Id", accountId);
        }

        if (HttpClientUtils.HeaderGet(headers, "Conversation_id").Length == 0)
        {
            var conversationSessionId = HttpClientUtils.HeaderGet(headers, CodexHeaders.SessionHeaderHyphen);
            if (conversationSessionId.Length > 0)
            {
                HttpClientUtils.HeaderSet(headers, "Conversation_id", conversationSessionId);
            }
        }

        if (HttpClientUtils.HeaderGet(headers, "Version").Length == 0)
        {
            HttpClientUtils.HeaderSet(headers, "Version", CodexConstants.DefaultVersion);
        }

        return httpRequest;
    }

    /// <summary>Port of <c>TransformResponse</c>.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response?.Request is not null && response.Request.RequestType == RequestTypes.AlphaSearch)
        {
            return TransformAlphaSearchResponse(response);
        }

        if (response?.Request is not null && response.Request.RequestType == RequestTypes.Image)
        {
            if (response.StatusCode >= 400)
            {
                throw new InvalidOperationException(
                    "codex image HTTP error " + response.StatusCode + ": " + Encoding.UTF8.GetString(response.Body ?? []));
            }

            var upstream = LlmJson.Deserialize<ResponsesResponse>(response.Body ?? [])
                ?? new ResponsesResponse();

            return ResponsesImageConvert.BuildImageResponse(upstream, response.Request.TransformerMetadata);
        }

        return _responsesOutbound.TransformResponse(response);
    }

    /// <summary>Port of <c>TransformStream</c>.</summary>
    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
        => _responsesOutbound.TransformStream(request, stream);

    /// <summary>Port of <c>AggregateStreamChunks</c>.</summary>
    public byte[] AggregateStreamChunks(HttpRequestData? request, IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta)
        => _responsesOutbound.AggregateStreamChunks(request, chunks, out meta);

    /// <summary>Port of <c>CustomizeExecutor</c>.</summary>
    public IExecutor CustomizeExecutor(IExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);

        var inner = executor;
        if (_transport == ResponsesOutboundTransformer.TransportWebSocket)
        {
            inner = CustomizeWebSocketExecutor(executor);
        }

        return new CodexExecutor(inner, executor, this);
    }

    private WebSocketExecutor CustomizeWebSocketExecutor(IExecutor executor)
    {
        // The reference caches the executor on the channel-cached transformer;
        // the port builds one transformer per attempt, so the cache lives
        // process-wide keyed by the inner executor and the channel (C072).
        return WebSocketExecutorCache.GetOrCreate(executor, _channelId);
    }

    /// <summary>
    /// Port of <c>Stop</c>. The reference closes the WebSocket executors cached
    /// on the channel provider when the enabled-channels cache swaps; the
    /// port's executor cache is process-wide and shared across attempts, so
    /// stopping one attempt must not close sessions other turns still reuse.
    /// Pooled connections retire through the executor's idle TTL, max lifetime
    /// and background cleanup instead (C072).
    /// </summary>
    public void Stop()
    {
    }
    /// <summary>Port of <c>FinalizeTransportRequest</c>.</summary>
    public HttpRequestData? FinalizeTransportRequest(HttpRequestData? request)
    {
        if (_transport == ResponsesOutboundTransformer.TransportWebSocket)
        {
            return request;
        }

        return ResponsesHttpTransport.PrepareHttpTransportRequest(
            request,
            stripPreviousResponseId: true);
    }

    /// <summary>Go: <c>codexExecutor</c>.</summary>
    internal sealed class CodexExecutor : IExecutor
    {
        private readonly IExecutor _inner;
        private readonly IExecutor _httpExecutor;
        private readonly CodexOutboundTransformer? _transformer;

        internal CodexExecutor(
            IExecutor inner,
            IExecutor httpExecutor,
            CodexOutboundTransformer? transformer)
        {
            _inner = inner;
            _httpExecutor = httpExecutor;
            _transformer = transformer;
        }

        /// <summary>Go: <c>codexExecutor.inner</c>.</summary>
        internal IExecutor Inner => _inner;

        /// <summary>Go: <c>codexExecutor.executor(ctx)</c> — the context flag has no port equivalent.</summary>
        internal IExecutor Executor() => _inner;

        public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
        {
            request = RequestForTransport(request);

            // Compact and alpha search are non-streaming endpoints; proxy them
            // through the real HTTP client instead of the SSE stream path.
            if (request.RequestType == RequestTypes.Compact
                || request.RequestType == RequestTypes.AlphaSearch)
            {
                return _httpExecutor.Do(cancellationToken, request);
            }

            // The official Codex backend always streams SSE, so keep the
            // historical DoStream path for it. Compatible relays may instead
            // return a completed Responses JSON document; for those, execute
            // once and dispatch on the response Content-Type. The request is
            // never reissued, which could duplicate model execution and billing.
            if (_transformer is not null && _transformer.IsOfficialCodex())
            {
                return DoStreamAndAggregate(cancellationToken, request);
            }

            return DoOnceAndDispatch(cancellationToken, request);
        }

        public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
            => _inner.DoStream(cancellationToken, RequestForTransport(request));

        internal HttpRequestData RequestForTransport(HttpRequestData request)
        {
            if (_transformer is null)
            {
                return request;
            }

            return _transformer.FinalizeTransportRequest(request) ?? request;
        }

        /// <summary>
        /// Preserves the original Codex behavior: consume the upstream SSE stream
        /// and aggregate the events into a completed Responses JSON body.
        /// </summary>
        private HttpResponseData DoStreamAndAggregate(
            CancellationToken cancellationToken,
            HttpRequestData request)
        {
            var stream = _inner.DoStream(cancellationToken, request);
            try
            {
                var chunks = new List<StreamEvent>();
                while (stream.Next())
                {
                    var ev = stream.Current;
                    if (ev is null)
                    {
                        continue;
                    }

                    chunks.Add(new StreamEvent
                    {
                        Type = ev.Type,
                        LastEventId = ev.LastEventId,
                        Data = (byte[])ev.Data.Clone(),
                    });
                }

                if (stream.Err is { } streamError)
                {
                    throw streamError;
                }

                if (WebSocketExecutor.TopLevelWebSocketError(chunks) is { } topLevelError)
                {
                    throw topLevelError;
                }

                var body = _transformer!.AggregateStreamChunks(request, chunks, out _);

                var responseHeaders = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Content-Type"] = ["application/json"],
                };
                var mergedHeaders = ResponseHeaders.MergeForwardResponseHeaders(
                    responseHeaders,
                    ResponseHeaders.GetResponseHeaders(stream));

                return new HttpResponseData
                {
                    StatusCode = 200,
                    Headers = mergedHeaders ?? responseHeaders,
                    Body = body,
                    Request = request,
                };
            }
            finally
            {
                stream.Dispose();
            }
        }

        /// <summary>
        /// Sends a single upstream request and inspects the completed body. Codex
        /// normally responds with SSE even for downstream non-stream requests,
        /// but compatible relays can return a completed Responses JSON document
        /// instead. JSON responses pass through unchanged; everything else is
        /// decoded as SSE and aggregated into a completed JSON response.
        /// </summary>
        private HttpResponseData DoOnceAndDispatch(
            CancellationToken cancellationToken,
            HttpRequestData request)
        {
            var response = _inner.Do(cancellationToken, request);
            if (response is null)
            {
                throw new InvalidOperationException("empty response");
            }

            if (IsJsonResponse(response))
            {
                return response;
            }

            var chunks = DecodeSseChunks(response.Body ?? []);
            if (WebSocketExecutor.TopLevelWebSocketError(chunks) is { } topLevelError)
            {
                throw topLevelError;
            }

            var body = _transformer!.AggregateStreamChunks(request, chunks, out _);

            return new HttpResponseData
            {
                StatusCode = 200,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Content-Type"] = ["application/json"],
                },
                Body = body,
                Request = request,
            };
        }

        /// <summary>Go: <c>isJSONResponse</c>.</summary>
        internal static bool IsJsonResponse(HttpResponseData? response)
        {
            if (response is null)
            {
                return false;
            }

            if (!GoMimeTypes.TryParseMediaType(
                    HttpClientUtils.HeaderGet(response.Headers, "Content-Type"),
                    out var mediaType,
                    out _))
            {
                return false;
            }

            mediaType = mediaType.ToLowerInvariant();
            return mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.Ordinal);
        }

        /// <summary>Go: <c>decodeSSEChunks</c>.</summary>
        private static List<StreamEvent> DecodeSseChunks(byte[] body)
        {
            using var stream = new HttpSseEventStream(new MemoryStream(body));
            var chunks = new List<StreamEvent>();
            while (stream.Next())
            {
                var ev = stream.Current;
                if (ev is null)
                {
                    continue;
                }

                chunks.Add(new StreamEvent
                {
                    Type = ev.Type,
                    LastEventId = ev.LastEventId,
                    Data = (byte[])ev.Data.Clone(),
                });
            }

            if (stream.Err is { } streamError)
            {
                throw streamError;
            }

            return chunks;
        }
    }
}
