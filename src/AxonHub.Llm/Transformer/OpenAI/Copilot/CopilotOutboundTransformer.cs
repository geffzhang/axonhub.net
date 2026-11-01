using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Llm.Transformer.OpenAI.Copilot;

/// <summary>Port of copilot.OutboundTransformerParams (outbound.go:62-65).</summary>
public sealed class CopilotOutboundParams
{
    /// <summary>Required; supplies the Copilot token used as the bearer credential.</summary>
    public ICopilotTokenProvider? TokenProvider { get; set; }

    /// <summary>Optional; defaults to <see cref="CopilotConstants.DefaultCopilotBaseUrl"/>.</summary>
    public string BaseUrl { get; set; } = string.Empty;
}

/// <summary>
/// Port of copilot.OutboundTransformer (llm/transformer/openai/copilot/outbound.go):
/// GitHub Copilot's LiteLLM-style editor headers, device-flow token exchange,
/// the {"response": ...} envelope unwrap and the Responses API routing used by
/// GPT-5+ and Grok models.
/// </summary>
public sealed class CopilotOutboundTransformer : IOutboundTransformer
{
    private readonly ICopilotTokenProvider _tokenProvider;
    private readonly string _baseUrl;
    private readonly ResponsesOutboundTransformer _responses;
    private OpenAiOutboundTransformer? _openAiTransformer;

    /// <summary>Port of <c>NewOutboundTransformer</c> (outbound.go:69-95).</summary>
    public CopilotOutboundTransformer(CopilotOutboundParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        _tokenProvider = parameters.TokenProvider
            ?? throw new ArgumentException("token provider is required", nameof(parameters));

        var baseUrl = parameters.BaseUrl;
        if (baseUrl.Length == 0)
        {
            baseUrl = CopilotConstants.DefaultCopilotBaseUrl;
        }

        // strings.TrimSuffix(baseURL, "/") removes exactly one trailing slash.
        if (baseUrl.EndsWith('/'))
        {
            baseUrl = baseUrl[..^1];
        }

        _baseUrl = baseUrl;

        ResponsesOutboundTransformer responses;
        try
        {
            responses = new ResponsesOutboundTransformer(new ResponsesOutboundConfig
            {
                BaseUrl = baseUrl,
                ApiKeyProvider = new StaticApiKeyProvider(string.Empty),
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create responses transformer: " + ex.Message,
                ex);
        }

        _responses = responses;
    }

    /// <summary>Port of <c>(*OutboundTransformer).APIFormat</c>.</summary>
    public string ApiFormat => ApiFormats.OpenAIChatCompletion;

    /// <summary>The normalized Copilot base URL (one trailing slash removed).</summary>
    public string BaseUrl => _baseUrl;

    /// <summary>Port of <c>(*OutboundTransformer).TransformRequest</c>.</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("request is nil");
        }

        if (string.IsNullOrEmpty(request.Model))
        {
            throw new InvalidOperationException("model is required");
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            throw new InvalidOperationException("messages are required");
        }

        if (UsesResponsesApi(request.Model))
        {
            return TransformResponsesRequest(request);
        }

        var token = GetCopilotToken();

        var oaiRequest = request.RequestFromLlm(ReasoningField.All);

        byte[] body;
        try
        {
            body = GoJson.Marshal(oaiRequest);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException("failed to marshal request: " + ex.Message, ex);
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        // LiteLLM-style editor headers required by Copilot.
        SetCopilotHeaders(headers);

        if (HasVisionContent(request))
        {
            HttpClientUtils.HeaderSet(
                headers,
                CopilotConstants.CopilotVisionRequestHeader,
                "true");
        }

        HttpClientUtils.HeaderSet(
            headers,
            CopilotConstants.InitiatorHeader,
            ResolveCopilotInitiator(request));

        return new HttpRequestData
        {
            Method = "POST",
            Url = _baseUrl + CopilotConstants.CopilotChatCompletionsEndpoint,
            Headers = headers,
            Body = body,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = token,
            },
            ApiFormat = ApiFormats.OpenAIChatCompletion,
        };
    }

    /// <summary>Port of <c>(*OutboundTransformer).TransformResponse</c>.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("http response is nil");
        }

        var body = response.Body ?? [];

        if (response.StatusCode >= 400)
        {
            throw new InvalidOperationException(
                "HTTP error "
                + response.StatusCode.ToString(CultureInfo.InvariantCulture)
                + ": "
                + HttpErrorBodyMessage(body));
        }

        if (body.Length == 0)
        {
            throw new InvalidOperationException("response body is empty");
        }

        var isResponsesFormat = IsResponsesFormat(body);
        byte[]? unwrappedBody = null;

        if (!isResponsesFormat)
        {
            var responseField = GetField(body, "response");
            if (responseField is not null)
            {
                // gjson .Raw: the value's original JSON text (quotes included
                // for strings), re-parsed by the reference.
                var inner = Encoding.UTF8.GetBytes(responseField.Value.GetRawText());
                if (IsResponsesFormat(inner))
                {
                    isResponsesFormat = true;
                    unwrappedBody = inner;
                }
            }
        }

        if (isResponsesFormat)
        {
            if (unwrappedBody is not null)
            {
                var wrappedResponse = new HttpResponseData
                {
                    StatusCode = response.StatusCode,
                    Headers = response.Headers,
                    Body = unwrappedBody,
                };

                return _responses.TransformResponse(wrappedResponse);
            }

            return _responses.TransformResponse(response);
        }

        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException("failed to unmarshal response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind is not null)
        {
            // A JSON null leaves Go's zero struct in place.
            return rootKind == "null"
                ? new LlmResponse()
                : throw new InvalidOperationException(
                    "failed to unmarshal response: "
                    + GoStructJson.TypeMismatch(rootKind, "openai.Response"));
        }

        OpenAiResponse? oaiResponse;
        try
        {
            oaiResponse = LlmJson.Deserialize<OpenAiResponse>(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal response: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }

        return oaiResponse?.ToLlmResponse() ?? new LlmResponse();
    }

    /// <summary>Port of <c>(*OutboundTransformer).TransformStream</c>.</summary>
    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
    {
        var isResponsesFormat = false;
        StreamEvent? firstEvent = null;

        if (stream.Next())
        {
            firstEvent = stream.Current;
            if (firstEvent is not null && firstEvent.Data.Length > 0)
            {
                isResponsesFormat = GJsonString(GetField(firstEvent.Data, "type"))
                    .StartsWith("response.", StringComparison.Ordinal);
            }
        }

        IStream<StreamEvent> compositeStream = firstEvent is not null
            ? new PrependStream<StreamEvent>(stream, firstEvent)
            : stream;

        if (!isResponsesFormat)
        {
            if (_openAiTransformer is null)
            {
                try
                {
                    _openAiTransformer = OpenAiOutboundTransformer.Create(_baseUrl, string.Empty);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    throw new InvalidOperationException(
                        "failed to create openai transformer: " + ex.Message,
                        ex);
                }
            }

            return _openAiTransformer.TransformStream(request, compositeStream);
        }

        // Per-stream state: Copilot reuses call ids across output items and puts
        // random hashes into item_id, so deltas have to be remapped.
        var state = new CopilotStreamConversionState();

        var convertedStream = new MapStream<StreamEvent, StreamEvent>(
            compositeStream,
            ev =>
            {
                if (ev.Data.Length == 0)
                {
                    return ev;
                }

                if (ev.Data.AsSpan().StartsWith("[DONE]"u8))
                {
                    return ev;
                }

                return new StreamEvent
                {
                    Data = ConvertCopilotStreamEvent(ev.Data, state),
                };
            });

        var filteredStream = new FilterStream<StreamEvent>(
            convertedStream,
            ev => ev.Data.Length > 0);

        return _responses.TransformStream(request, filteredStream);
    }

    /// <summary>Port of <c>(*OutboundTransformer).TransformError</c>.</summary>
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

        CopilotErrorEnvelope? envelope = null;
        try
        {
            envelope = LlmJson.Deserialize<CopilotErrorEnvelope>(error.Body);
        }
        catch (JsonException)
        {
            // A malformed body falls back to the upstream status text.
        }

        if (envelope is not null
            && (!string.IsNullOrEmpty(envelope.Error?.Message)
                || !string.IsNullOrEmpty(envelope.Errors?.Message)))
        {
            var detail = envelope.Error ?? new ErrorDetail();
            if (string.IsNullOrEmpty(detail.Message))
            {
                detail = envelope.Errors ?? new ErrorDetail();
            }

            return new ResponseError
            {
                StatusCode = error.StatusCode,
                Detail = detail,
            };
        }

        return new ResponseError
        {
            StatusCode = error.StatusCode,
            Detail = new ErrorDetail
            {
                Message = GoHttpStatus.Text(error.StatusCode),
                Type = "api_error",
            },
        };
    }

    /// <summary>Port of <c>(*OutboundTransformer).AggregateStreamChunks</c>.</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
    {
        _ = request;

        if (IsResponsesApiStream(chunks))
        {
            return _responses.AggregateStreamChunks(null, chunks, out meta);
        }

        return OpenAiAggregator.AggregateStreamChunks(
            chunks,
            OpenAiAggregator.DefaultTransformChunk,
            out meta);
    }

    /// <summary>Port of <c>SetCopilotHeaders</c> (outbound.go:160-168).</summary>
    internal static void SetCopilotHeaders(Dictionary<string, List<string>> headers)
    {
        HttpClientUtils.HeaderSet(headers, CopilotConstants.EditorVersionHeader, CopilotConstants.DefaultEditorVersion);
        HttpClientUtils.HeaderSet(
            headers,
            CopilotConstants.EditorPluginVersionHeader,
            CopilotConstants.DefaultEditorPluginVersion);
        HttpClientUtils.HeaderSet(headers, CopilotConstants.UserAgentHeader, CopilotConstants.DefaultUserAgent);
        HttpClientUtils.HeaderSet(
            headers,
            CopilotConstants.CopilotIntegrationIdHeader,
            CopilotConstants.DefaultCopilotIntegrationId);
        HttpClientUtils.HeaderSet(headers, CopilotConstants.OpenAiIntentHeader, CopilotConstants.DefaultOpenAiIntent);
        HttpClientUtils.HeaderSet(
            headers,
            CopilotConstants.GitHubApiVersionHeader,
            CopilotConstants.DefaultGitHubApiVersion);
        HttpClientUtils.HeaderSet(
            headers,
            CopilotConstants.VSCodeUserAgentLibHeader,
            CopilotConstants.DefaultVSCodeUserAgentLib);
    }

    /// <summary>Port of <c>normalizeInitiator</c> (outbound.go:170-177).</summary>
    internal static string NormalizeInitiator(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "user" or "agent" ? normalized : string.Empty;
    }

    /// <summary>Port of <c>inferCopilotInitiator</c> (outbound.go:180-206).</summary>
    internal static string InferCopilotInitiator(IReadOnlyList<Message>? messages)
    {
        if (messages is null || messages.Count == 0)
        {
            return "user";
        }

        var lastMessage = messages[^1];

        // The attribution field takes priority.
        var normalized = NormalizeInitiator(lastMessage.Attribution);
        if (normalized.Length > 0)
        {
            return normalized;
        }

        // A non-user role indicates an agent turn.
        if (lastMessage.Role != "user")
        {
            return "agent";
        }

        // Only the last content block is inspected (oh-my-pi logic).
        if (lastMessage.Content?.MultipleContent is { Count: > 0 } content
            && content[^1].Type == "tool_result")
        {
            return "agent";
        }

        return "user";
    }

    /// <summary>Port of <c>resolveCopilotInitiator</c> (outbound.go:208-221).</summary>
    internal static string ResolveCopilotInitiator(LlmRequest request)
    {
        var initiator = InferCopilotInitiator(request.Messages);

        // A valid X-Initiator header overrides the inference.
        if (request.RawRequest?.Headers is { } headers)
        {
            var value = HttpClientUtils.HeaderGet(headers, CopilotConstants.InitiatorHeader);
            if (value.Length > 0)
            {
                var normalized = NormalizeInitiator(value);
                if (normalized.Length > 0)
                {
                    initiator = normalized;
                }
            }
        }

        return initiator;
    }

    /// <summary>Port of <c>hasVisionContent</c> (outbound.go:223-243).</summary>
    internal static bool HasVisionContent(LlmRequest request)
    {
        foreach (var message in request.Messages)
        {
            if (message.Content?.Content is { } content && IsImageDataUrl(content))
            {
                return true;
            }

            if (message.Content?.MultipleContent is not { } parts)
            {
                continue;
            }

            foreach (var part in parts)
            {
                if (part.Type == "image_url" || part.ImageUrl is not null)
                {
                    return true;
                }

                if (part.Text is { } text && IsImageDataUrl(text))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Port of <c>isImageDataURL</c> (outbound.go:245-247).</summary>
    internal static bool IsImageDataUrl(string content)
        => content.StartsWith("data:image/", StringComparison.Ordinal);

    /// <summary>Port of <c>usesResponsesAPI</c> (outbound.go:634-657).</summary>
    internal static bool UsesResponsesApi(string model)
    {
        var normalizedModel = model.ToLowerInvariant();

        // Copilot Grok models do not support /chat/completions.
        if (normalizedModel.StartsWith("grok-", StringComparison.Ordinal))
        {
            return true;
        }

        // Go's ^gpt-(\d+): one or more ASCII digits after the prefix.
        const string prefix = "gpt-";
        if (!normalizedModel.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var start = prefix.Length;
        var end = start;
        while (end < normalizedModel.Length && normalizedModel[end] is >= '0' and <= '9')
        {
            end++;
        }

        if (end == start)
        {
            return false;
        }

        // strconv.Atoi rejects an overflowing run, which disables the route.
        if (!int.TryParse(
                normalizedModel.AsSpan(start, end - start),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var major))
        {
            return false;
        }

        // Match OpenCode's pattern: GPT-5+ uses the Responses API (except gpt-5-mini).
        return major >= 5
            && !normalizedModel.StartsWith("gpt-5-mini", StringComparison.Ordinal);
    }

    /// <summary>Port of <c>isResponsesAPIStream</c> (outbound.go:577-594).</summary>
    internal static bool IsResponsesApiStream(IReadOnlyList<StreamEvent> chunks)
    {
        foreach (var chunk in chunks)
        {
            if (chunk is null || chunk.Data.Length == 0)
            {
                continue;
            }

            var data = Encoding.UTF8.GetString(chunk.Data);
            if (data.Contains("\"type\":\"response.completed\"", StringComparison.Ordinal)
                || data.Contains("\"type\":\"response.created\"", StringComparison.Ordinal)
                || data.Contains("\"type\":\"response.in_progress\"", StringComparison.Ordinal)
                || data.Contains("\"type\":\"response.output_item.added\"", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Port of <c>convertCopilotStreamEvent</c> (outbound.go:413-524).</summary>
    internal static byte[] ConvertCopilotStreamEvent(byte[] data, CopilotStreamConversionState state)
    {
        using var document = TryParseDocument(data);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return data;
        }

        var root = document.RootElement;
        var eventType = GJsonString(TryGetPath(root, "type"));

        switch (eventType)
        {
            case "response.output_item.added":
            {
                var callId = GJsonString(TryGetPath(root, "item.call_id"));
                if (callId.Length == 0)
                {
                    return data;
                }

                // Capture the original item id before overriding it, then track
                // both identifiers so later deltas can be remapped.
                var originalId = GJsonString(TryGetPath(root, "item.id"));
                state.ItemIdToCallId[callId] = callId;
                if (originalId.Length > 0 && originalId != callId)
                {
                    state.ItemIdToCallId[originalId] = callId;
                }

                state.MostRecentCallId = callId;

                // The aggregator creates items by item.id; forcing it to the
                // call id makes the second Copilot item merge into the first.
                var eventObject = ToGoObject(root);
                if (!TryGetItem(eventObject, out var item))
                {
                    return data;
                }

                item["id"] = callId;
                if (!(item.TryGetValue("name", out var nameValue)
                    && nameValue is string name
                    && name.Length > 0))
                {
                    item["name"] = "function";
                }

                return TryMarshalEvent(eventObject, data);
            }

            case "response.function_call_arguments.delta":
            case "response.function_call_arguments.done":
            {
                // Copilot uses random hashes for item_id; override it with the
                // call id captured above, falling back to the most recent one.
                var itemId = GJsonString(TryGetPath(root, "item_id"));
                var callId = itemId.Length > 0
                    ? state.ItemIdToCallId.GetValueOrDefault(itemId) ?? string.Empty
                    : string.Empty;
                if (callId.Length == 0)
                {
                    callId = state.MostRecentCallId ?? string.Empty;
                }

                if (callId.Length == 0)
                {
                    return data;
                }

                var eventObject = ToGoObject(root);
                eventObject["item_id"] = callId;

                if (eventType == "response.function_call_arguments.done")
                {
                    eventObject["call_id"] = callId;
                }

                return TryMarshalEvent(eventObject, data);
            }

            case "response.output_item.done":
            {
                // A random hash in item.id would update the wrong item.
                var callId = GJsonString(TryGetPath(root, "item.call_id"));
                if (callId.Length == 0)
                {
                    return data;
                }

                var eventObject = ToGoObject(root);
                if (!TryGetItem(eventObject, out var item))
                {
                    return data;
                }

                item["id"] = callId;
                return TryMarshalEvent(eventObject, data);
            }

            default:
                return data;
        }
    }

    private HttpRequestData TransformResponsesRequest(LlmRequest request)
    {
        HttpRequestData responsesRequest;
        try
        {
            responsesRequest = _responses.TransformRequest(request);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "failed to transform request for Responses API: ");
        }

        var token = GetCopilotToken();

        // Override the placeholder auth with the Copilot token.
        responsesRequest.Auth = new AuthConfig
        {
            Type = AuthTypes.Bearer,
            ApiKey = token,
        };

        var headers = responsesRequest.Headers
            ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);

        SetCopilotHeaders(headers);

        if (HasVisionContent(request))
        {
            HttpClientUtils.HeaderSet(
                headers,
                CopilotConstants.CopilotVisionRequestHeader,
                "true");
        }

        HttpClientUtils.HeaderSet(
            headers,
            CopilotConstants.InitiatorHeader,
            ResolveCopilotInitiator(request));

        return responsesRequest;
    }

    private string GetCopilotToken()
    {
        try
        {
            return _tokenProvider.GetToken(CancellationToken.None);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "failed to get copilot token: ");
        }
    }

    private static string HttpErrorBodyMessage(byte[] body)
    {
        if (body.Length == 0)
        {
            return "(empty body)";
        }

        if (body.Length > 100)
        {
            // Go slices the raw bytes; the port decodes the first 100 bytes,
            // which can cut a multibyte rune (C107).
            return "(first 100 chars: "
                + Encoding.UTF8.GetString(body, 0, 100)
                + ", total length: "
                + body.Length.ToString(CultureInfo.InvariantCulture)
                + ")";
        }

        return "(body: "
            + Encoding.UTF8.GetString(body)
            + ", length: "
            + body.Length.ToString(CultureInfo.InvariantCulture)
            + ")";
    }

    private static bool IsResponsesFormat(byte[] body)
        => GetField(body, "output") is not null
            || GJsonString(GetField(body, "object")) == "response";

    /// <summary>
    /// gjson path lookup with the port's strict decoder (difference C107): an
    /// existing JSON <c>null</c> is present (gjson <c>Exists()</c> is true), a
    /// malformed document is absent, and only object roots are traversed.
    /// </summary>
    internal static JsonElement? GetField(byte[] body, string path)
    {
        using var document = TryParseDocument(body);
        return document is null ? null : TryGetPath(document.RootElement, path);
    }

    /// <summary>gjson's <c>.String()</c>: strings unquoted, null absent, other kinds raw.</summary>
    internal static string GJsonString(JsonElement? element)
    {
        if (element is not { } value)
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => string.Empty,
            JsonValueKind.String => value.GetString() ?? string.Empty,
            _ => value.GetRawText(),
        };
    }

    private static JsonElement? TryGetPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var segment in path.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.Clone();
    }

    private static JsonDocument? TryParseDocument(byte[] data)
    {
        try
        {
            return JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<string, object?> ToGoObject(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = ToGoValue(property.Value);
        }

        return result;
    }

    private static object? ToGoValue(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return ToGoObject(element);
            case JsonValueKind.Array:
                var items = new List<object?>();
                foreach (var item in element.EnumerateArray())
                {
                    items.Add(ToGoValue(item));
                }

                return items;
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                // Go decodes every number as float64; the port keeps the raw
                // literal so large integers survive (C107).
                return element.Clone();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            default:
                return null;
        }
    }

    private static bool TryGetItem(
        Dictionary<string, object?> eventObject,
        out Dictionary<string, object?> item)
    {
        if (eventObject.TryGetValue("item", out var value)
            && value is Dictionary<string, object?> found)
        {
            item = found;
            return true;
        }

        item = null!;
        return false;
    }

    private static byte[] TryMarshalEvent(
        Dictionary<string, object?> eventObject,
        byte[] original)
    {
        try
        {
            return GoJson.Marshal(eventObject);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return original;
        }
    }
}

/// <summary>
/// The per-stream call-id bookkeeping of <c>convertCopilotStreamEvent</c>
/// (outbound.go:372-374). A holder keeps the mutable state out of the stream
/// lambda's closure.
/// </summary>
internal sealed class CopilotStreamConversionState
{
    public Dictionary<string, string> ItemIdToCallId { get; } = new(StringComparer.Ordinal);

    public string? MostRecentCallId { get; set; }
}

/// <summary>Port of the anonymous error envelope in <c>TransformError</c> (outbound.go:539-542).</summary>
internal sealed class CopilotErrorEnvelope
{
    [JsonPropertyName("error")]
    public ErrorDetail? Error { get; set; }

    [JsonPropertyName("errors")]
    public ErrorDetail? Errors { get; set; }
}
