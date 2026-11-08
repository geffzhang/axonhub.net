using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>Port of responses.Config (outbound.go:30-67).</summary>
public sealed class ResponsesOutboundConfig
{
    /// <summary>The base URL for the OpenAI API.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Use the base URL as-is instead of appending the responses endpoint.</summary>
    public bool RawUrl { get; set; }

    /// <summary>
    /// Optional custom path override. When set it replaces the default API path
    /// (<c>/responses</c>) and skips the default version normalization.
    /// </summary>
    public string EndpointPath { get; set; } = string.Empty;

    /// <summary>
    /// Replays <c>additional_tools</c> input items upstream. The item belongs to
    /// Codex's private Responses Lite protocol: it carries tool definitions that
    /// Lite keeps out of the top-level <c>tools</c> array, and an
    /// OpenAI-compatible upstream rejects it as an unsupported input item type.
    /// </summary>
    public bool PreserveAdditionalTools { get; set; }

    /// <summary>Provides API keys for authentication, required.</summary>
    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>
    /// Upstream transport: empty and <c>http</c> use the HTTP/SSE transport;
    /// <c>websocket</c> selects Responses WebSocket mode
    /// (<see cref="TransportWebSocket"/>).
    /// </summary>
    public string Transport { get; set; } = string.Empty;

    /// <summary>
    /// Port-only audit field: 1-based position of the key the provider serves in
    /// the channel's full key list, or null when the channel has a single key.
    /// Persisted on request_executions.channel_api_key_index.
    /// </summary>
    public int? ChannelApiKeyIndex { get; set; }

    /// <summary>
    /// Port-only seam for <c>shared.GetSessionID(ctx)</c>. The reference reads
    /// the trace session from the request context; the port's trace middleware
    /// publishes the id and the chat endpoint writes it onto the wire request,
    /// so the orchestrator supplies it here (difference C062). Returning null
    /// means "no session"; an empty string is a present empty session, exactly
    /// as Go's <c>ok</c> flag distinguishes the two.
    /// </summary>
    public Func<string?>? SessionIdProvider { get; set; }

    /// <summary>
    /// Port-only seam for <c>shared.GetSessionScope(ctx)</c>. The reference
    /// reads the WebSocket pool scope from the request context; the port cannot
    /// because it has no request-context seam, so the orchestrator supplies it
    /// here and the transformer copies it onto the wire request (difference
    /// C062). A null or blank value disables WebSocket connection pooling.
    /// </summary>
    public Func<string?>? SessionScopeProvider { get; set; }
}

/// <summary>
/// Port of the Responses API outbound transformer
/// (llm/transformer/openai/responses/outbound.go, outbound_convert.go,
/// compact_outbound.go).
/// </summary>
/// <remarks>
/// Both transports are ported: HTTP attempts strip WebSocket-only request
/// metadata (http_executor.go), while <c>websocket</c> attempts run through
/// <see cref="WebSocketExecutor"/> (websocket_executor.go). A channel whose
/// base URL uses the ws/wss scheme selects the WebSocket transport; endpoint
/// records (and with them an explicit per-endpoint transport) are not modeled
/// yet (C072 / C082). The trace session and scope reach the transformer through
/// <see cref="ResponsesOutboundConfig.SessionIdProvider"/> /
/// <see cref="ResponsesOutboundConfig.SessionScopeProvider"/>, which the
/// orchestrator fills from the wire request (C062).
/// </remarks>
public sealed class ResponsesOutboundTransformer : IOutboundTransformer, IApiKeyProvenance,
    IChannelCustomizedExecutor, ITransportRequestFinalizer, IStoppableOutbound
{
    public const string TransportHttp = "http";

    public const string TransportWebSocket = "websocket";

    private readonly ResponsesOutboundConfig _config;

    private readonly object _executorMu = new();

    private Dictionary<IExecutor, WebSocketExecutor>? _webSocketExecutors;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    /// <summary>Port of NewOutboundTransformerWithConfig.</summary>
    public ResponsesOutboundTransformer(ResponsesOutboundConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.ApiKeyProvider is null)
        {
            throw new ArgumentException("API key provider is required", nameof(config));
        }

        _config = config;

        if (_config.BaseUrl.EndsWith("##", StringComparison.Ordinal))
        {
            _config.RawUrl = true;
            _config.BaseUrl = _config.BaseUrl[..^2];
        }
        else
        {
            _config.BaseUrl = HttpClientUtils.NormalizeBaseUrl(
                _config.BaseUrl,
                _config.EndpointPath.Length > 0 ? string.Empty : "v1");
        }
    }

    /// <summary>Port of NewOutboundTransformer(baseURL, apiKey).</summary>
    public static ResponsesOutboundTransformer Create(
        string baseUrl,
        string apiKey,
        Func<string?>? sessionIdProvider = null,
        Func<string?>? sessionScopeProvider = null)
    {
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrEmpty(baseUrl))
        {
            throw new ArgumentException("apiKey or baseURL is empty");
        }

        return Create(
            baseUrl,
            new StaticApiKeyProvider(apiKey),
            sessionIdProvider: sessionIdProvider,
            sessionScopeProvider: sessionScopeProvider);
    }

    /// <summary>
    /// Builds a transformer with a key provider and its audit index
    /// (the reference's getAPIKeyProvider + channel_api_key_index row). The
    /// session seams default to null, which is the reference's "no trace
    /// session" case (difference C062).
    /// </summary>
    public static ResponsesOutboundTransformer Create(
        string baseUrl,
        IApiKeyProvider apiKeyProvider,
        int? channelApiKeyIndex = null,
        Func<string?>? sessionIdProvider = null,
        Func<string?>? sessionScopeProvider = null)
        => new(new ResponsesOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = channelApiKeyIndex,
            SessionIdProvider = sessionIdProvider,
            SessionScopeProvider = sessionScopeProvider,
        });

    /// <summary>Port of responses.OutboundTransformer.APIFormat (outbound.go:168-173).</summary>
    public string ApiFormat => _config.Transport == TransportWebSocket
        ? ApiFormats.OpenAIResponseWebSocket
        : ApiFormats.OpenAIResponse;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    public string BaseUrl => _config.BaseUrl;

    public ResponsesOutboundConfig Config => _config;

    /// <summary>Go: <c>preserveAdditionalTools</c>.</summary>
    private bool PreserveAdditionalTools => _config.PreserveAdditionalTools;

    /// <summary>Port of TransformRequest: unified request to the upstream HTTP request.</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("chat request is nil");
        }

        var originalRequestType = request.RequestType;
        var isImageRequest = originalRequestType == RequestTypes.Image;

        // The reference reads these from the request context inside the
        // executor; the port captures them once per transform and carries them
        // on the wire request (difference C062).
        var sessionId = _config.SessionIdProvider?.Invoke();
        var sessionScope = _config.SessionScopeProvider?.Invoke();

        switch (request.RequestType)
        {
            case RequestTypes.Compact:
                return TransformCompactRequest(request, sessionId, sessionScope);
            case RequestTypes.Image:
                request = ResponsesImageConvert.BuildImageToolRequest(request);
                break;
            case null:
            case "":
            case RequestTypes.Chat:
                break;
            default:
                throw TransformerErrors.InvalidRequest(request.RequestType + " is not supported");
        }

        // Initialize TransformerMetadata if nil.
        request.TransformerMetadata ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        List<ResponsesTool>? tools = null;
        var namespaceIndexes = new Dictionary<string, int>(StringComparer.Ordinal);

        // Convert tools to Responses API format.
        foreach (var item in request.Tools ?? [])
        {
            switch (item.Type)
            {
                case ToolTypes.ImageGeneration:
                {
                    var tool = ResponsesOutboundConvert.ConvertImageGenerationToTool(item);
                    if (ResponsesMetadata.GetStringPtr(request.TransformerMetadata, "image_generation_action")
                        is { } action)
                    {
                        tool.Action = ResponsesConvertUtil.NullIfEmpty(action);
                    }

                    (tools ??= []).Add(tool);

                    // Store image output format in TransformerMetadata.
                    request.TransformerMetadata["image_output_format"] =
                        ResponsesConvertUtil.ToElement(tool.OutputFormat ?? string.Empty);
                    break;
                }

                case ToolTypes.WebSearch:
                case ToolTypes.GoogleSearch:
                    (tools ??= []).Add(ResponsesOutboundConvert.ConvertWebSearchToTool(item));
                    break;

                case ToolTypes.ResponsesCustomTool:
                    (tools ??= []).Add(ResponsesOutboundConvert.ConvertCustomToTool(item));
                    break;

                case "function":
                {
                    var tool = ResponsesOutboundConvert.ConvertFunctionToTool(item);
                    var @namespace = item.Function.Namespace;
                    if (!string.IsNullOrEmpty(@namespace))
                    {
                        if (namespaceIndexes.TryGetValue(@namespace, out var index))
                        {
                            tools![index].Tools!.Add(tool);
                        }
                        else
                        {
                            namespaceIndexes[@namespace] = tools?.Count ?? 0;
                            (tools ??= []).Add(new ResponsesTool
                            {
                                Type = "namespace",
                                Name = @namespace,
                                Tools = [tool],
                            });
                        }
                    }
                    else
                    {
                        (tools ??= []).Add(tool);
                    }

                    break;
                }

                default:
                    // Skip unsupported tool types.
                    break;
            }
        }

        var input = ResponsesOutboundConvert.ConvertInputFromMessages(
            request.Messages, request.TransformOptions);
        var include = ResponsesMetadata.GetStringSlice(request.TransformerMetadata, "include");

        var payload = new ResponsesRequest
        {
            Model = request.Model,
            // Go tag is `input,omitzero`: a zero Input (no text, no items) is omitted.
            Input = input.Text is null && input.Items is null ? null : input,
            Instructions = ResponsesOutboundConvert.ConvertInstructionsFromMessages(request.Messages),
            Tools = tools,
            ParallelToolCalls = request.ParallelToolCalls,
            Stream = request.Stream,
            Text = ResponsesOutboundConvert.ConvertToTextOptions(request),
            Store = request.Store,
            ServiceTier = request.ServiceTier,
            SafetyIdentifier = request.SafetyIdentifier,
            User = request.User,
            Metadata = request.Metadata is { Count: > 0 } ? request.Metadata : null,
            MaxOutputTokens = request.MaxCompletionTokens,
            TopLogprobs = request.TopLogprobs,
            TopP = request.TopP,
            ToolChoice = ResponsesOutboundConvert.ConvertToolChoice(request.ToolChoice),
            StreamOptions = ResponsesOutboundConvert.ConvertStreamOptions(
                request.StreamOptions, request.TransformerMetadata),
            Reasoning = ResponsesOutboundConvert.ConvertReasoning(request),
            PromptCacheKey = request.PromptCacheKey,
            PreviousResponseId = request.PreviousResponseId,
            Include = include is { Count: > 0 } ? include : null,
            MaxToolCalls = ResponsesMetadata.GetInt64Ptr(request.TransformerMetadata, "max_tool_calls"),
            PromptCacheRetention = ResponsesMetadata.GetStringPtr(
                request.TransformerMetadata, "prompt_cache_retention"),
            Truncation = ResponsesMetadata.GetStringPtr(request.TransformerMetadata, "truncation"),
        };

        if (string.IsNullOrEmpty(payload.PromptCacheKey))
        {
            if (sessionId is { } value)
            {
                // A session may multiplex several concurrent conversations
                // (e.g. Claude Code subagents); scope the cache key to the
                // conversation so they do not evict each other upstream.
                var anchor = ResponsesCacheAnchor.ConversationAnchor(request.Messages);
                if (anchor.Length > 0)
                {
                    value = value + "-" + anchor;
                }

                payload.PromptCacheKey = value;
            }
        }

        // Responses Lite requires an explicit false value, even when no
        // top-level tools are sent.
        if (request.RawRequest is not null
            && string.Equals(
                HttpClientUtils.HeaderGet(
                    request.RawRequest.Headers, ResponsesKeys.ResponsesLiteHeader).Trim(),
                "true",
                StringComparison.OrdinalIgnoreCase))
        {
            payload.ParallelToolCalls = false;
        }
        else if (payload.Tools is not { Count: > 0 })
        {
            // Other Responses providers may reject parallel_tool_calls when
            // tools are absent.
            payload.ParallelToolCalls = null;
        }

        // Set MaxOutputTokens to MaxTokens if not set.
        payload.MaxOutputTokens ??= request.MaxTokens;

        byte[] body;
        try
        {
            body = ResponsesRequestExtensions.MarshalRequestPayload(
                payload, request, PreserveAdditionalTools);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to marshal responses api request: " + ex.Message, ex);
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        var httpRequest = new HttpRequestData
        {
            Method = "POST",
            Url = BuildFullRequestUrl(),
            Headers = headers,
            Body = body,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
            ApiFormat = ApiFormats.OpenAIResponse,
            TransformerMetadata = request.TransformerMetadata,
            SkipInboundQueryMerge = true,
            Metadata = null,
            SessionId = sessionId,
            SessionScope = sessionScope,
        };

        // Keep the image request type on the wire request so TransformResponse
        // selects the image response conversion.
        if (isImageRequest)
        {
            httpRequest.RequestType = originalRequestType!;
        }

        return httpRequest;
    }

    /// <summary>Port of buildFullRequestURL.</summary>
    public string BuildFullRequestUrl()
    {
        if (_config.RawUrl)
        {
            return _config.BaseUrl;
        }

        if (_config.EndpointPath.Length > 0)
        {
            return _config.BaseUrl + _config.EndpointPath;
        }

        return _config.BaseUrl + "/responses";
    }

    /// <summary>Port of buildCompactURL.</summary>
    public string BuildCompactUrl()
    {
        if (_config.RawUrl)
        {
            return _config.BaseUrl;
        }

        return _config.BaseUrl + "/responses/compact";
    }

    /// <summary>Port of TransformResponse: upstream HTTP response to unified response.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("http response is nil");
        }

        // Route compact responses to the specialized handler.
        if (response.Request is not null && response.Request.RequestType == RequestTypes.Compact)
        {
            return TransformCompactResponse(response);
        }

        if (response.Request is not null && response.Request.RequestType == RequestTypes.Image)
        {
            return TransformImageResponse(response);
        }

        return TransformStandardResponse(response);
    }

    /// <summary>Port of transformImageResponse (outbound.go:415-431).</summary>
    private static LlmResponse TransformImageResponse(HttpResponseData response)
    {
        if (response.StatusCode >= 400)
        {
            throw new InvalidOperationException(
                "HTTP error " + response.StatusCode + ": " + BodyText(response.Body).Trim());
        }

        var body = response.Body ?? [];
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal responses api image response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind is not null && rootKind != "null")
        {
            throw new InvalidOperationException(
                "failed to unmarshal responses api image response: "
                + GoStructJson.TypeMismatch(rootKind, "responses.Response"));
        }

        ResponsesResponse? upstream;
        try
        {
            upstream = rootKind == "null"
                ? new ResponsesResponse()
                : LlmJson.Deserialize<ResponsesResponse>(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal responses api image response: "
                + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }

        var metadata = response.Request?.TransformerMetadata;
        return ResponsesImageConvert.BuildImageResponse(upstream, metadata);
    }

    /// <summary>Port of transformStandardResponse.</summary>
    private LlmResponse TransformStandardResponse(HttpResponseData response)
    {
        if (response.StatusCode >= 400)
        {
            throw new InvalidOperationException(
                "HTTP error " + response.StatusCode + ": " + BodyText(response.Body).Trim());
        }

        if (response.Body is null || response.Body.Length == 0)
        {
            throw new InvalidOperationException("response body is empty");
        }

        // Go validates the whole document before decoding, so syntax errors are
        // always reported with encoding/json's own wording.
        var syntaxError = GoJsonSyntax.Validate(response.Body);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal responses api response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(response.Body);
        if (rootKind is not null && rootKind != "null")
        {
            throw new InvalidOperationException(
                "failed to unmarshal responses api response: "
                + GoStructJson.TypeMismatch(rootKind, "responses.Response"));
        }

        ResponsesResponse? resp;
        try
        {
            resp = rootKind == "null"
                ? new ResponsesResponse()
                : LlmJson.Deserialize<ResponsesResponse>(response.Body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal responses api response: "
                + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }

        if (resp is null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal responses api response: unexpected null");
        }

        // Validate that we got a valid response.
        if (resp.Id.Length == 0
            && resp.Model.Length == 0
            && resp.Output is not { Count: > 0 })
        {
            throw new InvalidOperationException(
                "responses api returned empty response: body=" + BodyText(response.Body));
        }

        var llmResp = new LlmResponse
        {
            Object = "chat.completion",
            Id = resp.Id,
            Model = resp.Model,
            Created = resp.CreatedAt,
            PreviousResponseId = resp.PreviousResponseId,
            Choices = [],
            TransformerMetadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal),
        };

        if (resp.Usage is not null)
        {
            llmResp.Usage = resp.Usage.ToUsage();
        }

        if (response.Request?.TransformerMetadata is not null)
        {
            llmResp.TransformerMetadata = new Dictionary<string, JsonElement>(
                response.Request.TransformerMetadata, StringComparer.Ordinal);
        }

        var msg = ResponsesOutboundConvert.ConvertOutputToMessage(
            resp.Output ?? [], llmResp.TransformerMetadata);

        string? finishReason = null;
        if (msg.ToolCalls is { Count: > 0 })
        {
            finishReason = "tool_calls";
        }
        else if (resp.Status is not null)
        {
            finishReason = resp.Status switch
            {
                "completed" => "stop",
                "failed" => "error",
                "incomplete" => "length",
                "canceled" or "cancelled" => "cancelled",
                _ => null,
            };
        }

        llmResp.Choices.Add(new Choice
        {
            Index = 0,
            Message = msg,
            FinishReason = finishReason,
        });

        // The reference's empty-choices fallback below this point is
        // unreachable (a choice was just appended) and is not reproduced.

        return llmResp;
    }

    /// <summary>Port of transformCompactRequest.</summary>
    private HttpRequestData TransformCompactRequest(
        LlmRequest request,
        string? sessionId,
        string? sessionScope)
    {
        if (request.Compact is null)
        {
            throw new InvalidOperationException("compact request is nil in llm.Request");
        }

        // Compact request input is always an ordered message array and must not
        // be collapsed to plain text.
        var input = ResponsesOutboundConvert.ConvertInputFromMessages(
            request.Compact.Input ?? [],
            new TransformOptions { ArrayInputs = true });

        var payload = new CompactApiRequest
        {
            Model = request.Model,
            // Go tag is `input,omitzero`.
            Input = input.Text is null && input.Items is null ? null : input,
            Instructions = ResponsesConvertUtil.NullIfEmpty(request.Compact.Instructions),
            PromptCacheKey = ResponsesConvertUtil.NullIfEmpty(request.Compact.PromptCacheKey),
        };

        byte[] body;
        try
        {
            body = ResponsesRequestExtensions.MarshalCompactRequestPayload(payload, request);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to marshal compact request: " + ex.Message, ex);
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        return new HttpRequestData
        {
            Method = "POST",
            Url = BuildCompactUrl(),
            Headers = headers,
            Body = body,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
            RequestType = RequestTypes.Compact,
            ApiFormat = ApiFormats.OpenAIResponseCompact,
            SkipInboundQueryMerge = true,
            Metadata = null,
            SessionId = sessionId,
            SessionScope = sessionScope,
        };
    }

    /// <summary>Port of transformCompactResponse.</summary>
    private LlmResponse TransformCompactResponse(HttpResponseData response)
    {
        if (response.StatusCode >= 400)
        {
            throw TransformError(new HttpClientError(
                string.Empty, string.Empty, response.StatusCode, string.Empty, response.Body));
        }

        if (response.Body is null || response.Body.Length == 0)
        {
            throw new InvalidOperationException("response body is empty");
        }

        var syntaxError = GoJsonSyntax.Validate(response.Body);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal compact response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(response.Body);
        if (rootKind is not null && rootKind != "null")
        {
            throw new InvalidOperationException(
                "failed to unmarshal compact response: "
                + GoStructJson.TypeMismatch(rootKind, "responses.CompactAPIResponse"));
        }

        CompactApiResponse? compactResp;
        try
        {
            compactResp = rootKind == "null"
                ? new CompactApiResponse()
                : LlmJson.Deserialize<CompactApiResponse>(response.Body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal compact response: "
                + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }

        if (compactResp is null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal compact response: unexpected null");
        }

        List<Message> outputMessages;
        try
        {
            outputMessages = ResponsesCompactConvert.ConvertCompactInputToMessages(
                new ResponsesInput { Items = compactResp.Output });
        }
        catch (TransformerException ex)
        {
            throw new InvalidOperationException(
                "failed to convert compact response output: " + ex.Message, ex);
        }

        foreach (var message in outputMessages)
        {
            message.ReasoningSignature =
                OpenAiSignatures.EncodeOpenAIEncryptedContent(message.ReasoningSignature);
        }

        var llmResp = new LlmResponse
        {
            RequestType = RequestTypes.Compact,
            ApiFormat = ApiFormats.OpenAIResponseCompact,
            Id = compactResp.Id,
            Created = compactResp.CreatedAt,
            Object = "response.compaction",
            Model = compactResp.Model ?? string.Empty,
            Compact = new CompactResponse
            {
                Id = compactResp.Id,
                CreatedAt = compactResp.CreatedAt,
                Object = "response.compaction",
                Instructions = compactResp.Instructions,
                Output = outputMessages,
            },
        };

        if (compactResp.Usage is not null)
        {
            llmResp.Usage = compactResp.Usage.ToUsage();
        }

        return llmResp;
    }

    /// <summary>Port of TransformError: HTTP error body to unified error.</summary>
    public ResponseError TransformError(HttpClientError? error)
    {
        if (error is null)
        {
            return new ResponseError
            {
                StatusCode = 500,
                Detail = new ErrorDetail
                {
                    Message = GoHttpStatus.Text(500),
                    Type = "api_error",
                },
            };
        }

        // Try to parse the OpenAI error format first.
        ErrorDetail? detail = null;
        try
        {
            detail = LlmJson.Deserialize<ResponsesUpstreamErrorEnvelope>(error.Body)?.Error;
        }
        catch (JsonException)
        {
            // Fall through to the status-text form.
        }

        if (detail is { Message.Length: > 0 })
        {
            return new ResponseError
            {
                StatusCode = error.StatusCode,
                Detail = detail,
            };
        }

        // If JSON parsing fails, use the upstream body text.
        return new ResponseError
        {
            StatusCode = error.StatusCode,
            Detail = new ErrorDetail
            {
                Message = BodyText(error.Body).Trim(),
                Type = "api_error",
            },
        };
    }

    /// <summary>Port of TransformStream (outbound_stream.go:20-31).</summary>
    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request, IStream<StreamEvent> stream)
    {
        _ = request;
        var source = GoStreams.NoNil(new ResponsesOutboundStream(stream));
        return new MapStream<LlmResponse, LlmResponse>(
            source, resp => ResponsesNamespace.MapResponseFunctionNames(resp, true));
    }

    /// <summary>Port of AggregateStreamChunks (outbound_stream.go:169-187).</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
    {
        _ = request;
        return ResponsesAggregate.AggregateStreamChunks(chunks, out meta);
    }

    /// <summary>Port of OutboundTransformer.CustomizeExecutor.</summary>
    /// <remarks>
    /// Go guards the cache with <c>reflect.TypeOf(executor).Comparable()</c>;
    /// every CLR object can key a dictionary, so the port keeps the cache for
    /// every executor and relies on the default equality comparer — identity
    /// for the port's executor classes, field equality for comparable value
    /// types, matching Go's map-key rules.
    /// </remarks>
    public IExecutor CustomizeExecutor(IExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);

        if (_config.Transport != TransportWebSocket)
        {
            return new HttpTransportExecutor(executor, FinalizeTransportRequest);
        }

        lock (_executorMu)
        {
            _webSocketExecutors ??= [];
            if (_webSocketExecutors.TryGetValue(executor, out var cached))
            {
                return cached;
            }

            var webSocketExecutor = new WebSocketExecutor(executor);
            _webSocketExecutors[executor] = webSocketExecutor;
            return webSocketExecutor;
        }
    }

    /// <summary>Port of OutboundTransformer.FinalizeTransportRequest.</summary>
    public HttpRequestData? FinalizeTransportRequest(HttpRequestData? request)
    {
        if (_config.Transport == TransportWebSocket)
        {
            return request;
        }

        return ResponsesHttpTransport.PrepareHttpTransportRequest(
            request,
            stripPreviousResponseId: false);
    }

    /// <summary>
    /// Port of OutboundTransformer.Stop: closes every cached WebSocket
    /// executor. The reference calls it when the enabled-channels cache swaps;
    /// the port builds one transformer per attempt, so the orchestrator stops
    /// the attempt's transformer when the attempt ends or is replaced (C072).
    /// </summary>
    public void Stop()
    {
        List<WebSocketExecutor> executors;
        lock (_executorMu)
        {
            executors = _webSocketExecutors is null ? [] : [.. _webSocketExecutors.Values];
            _webSocketExecutors = null;
        }

        foreach (var executor in executors)
        {
            executor.Close();
        }
    }

    private static string BodyText(byte[]? body)
        => body is null ? string.Empty : Encoding.UTF8.GetString(body);
}

/// <summary>Port of the anonymous <c>struct{ Error llm.ErrorDetail }</c> decode in TransformError.</summary>
internal sealed class ResponsesUpstreamErrorEnvelope
{
    [JsonPropertyName("error")]
    public ErrorDetail? Error { get; set; }
}
