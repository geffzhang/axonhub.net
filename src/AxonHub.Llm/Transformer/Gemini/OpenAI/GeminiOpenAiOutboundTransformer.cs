using System.Globalization;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Gemini.OpenAI;

/// <summary>Port of geminioai.Config (llm/transformer/gemini/openai/outbound.go:20-25).</summary>
public sealed class GeminiOpenAiOutboundConfig
{
    /// <summary>Custom base URL, required.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Provides API keys for authentication, required.</summary>
    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>
    /// Port-only audit field: 1-based position of the key the provider serves in
    /// the channel's full key list, or null when the channel has a single key.
    /// </summary>
    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of the Gemini OpenAI-compatible outbound transformer
/// (llm/transformer/gemini/openai): an OpenAI chat transformer pointed at the
/// Gemini <c>v1beta/openai</c> endpoint, plus Gemini's <c>extra_body.google
/// .thinking_config</c> handling, Google-native tool filtering and thought
/// signature passthrough.
/// </summary>
public sealed class GeminiOpenAiOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    private readonly GeminiOpenAiOutboundConfig _config;
    private readonly string _baseUrl;
    private readonly OpenAiOutboundTransformer _inner;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    /// <summary>Port of NewOutboundTransformerWithConfig.</summary>
    public GeminiOpenAiOutboundTransformer(GeminiOpenAiOutboundConfig config)
    {
        if (config is null)
        {
            throw new InvalidOperationException(
                "invalid Gemini OpenAI transformer configuration: config cannot be nil");
        }

        if (config.BaseUrl.Length == 0)
        {
            throw new InvalidOperationException(
                "base URL is required for Gemini OpenAI transformer");
        }

        if (config.ApiKeyProvider is null)
        {
            throw new InvalidOperationException(
                "API key provider is required for Gemini OpenAI transformer");
        }

        _config = config;
        _baseUrl = HttpClientUtils.NormalizeBaseUrl(config.BaseUrl, "v1beta/openai");

        try
        {
            // gemini_openai is the OpenAI chat wire format with the Google
            // platform policy (keeps the thought signature) and reasoning in
            // reasoning_content.
            _inner = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
            {
                PlatformType = OpenAiPlatformType.Google,
                BaseUrl = _baseUrl,
                ApiKeyProvider = config.ApiKeyProvider,
                ReasoningField = ReasoningField.Content,
                ChannelApiKeyIndex = config.ChannelApiKeyIndex,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create Gemini OpenAI outbound transformer: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of NewOutboundTransformer(baseURL, apiKey).</summary>
    public static GeminiOpenAiOutboundTransformer Create(string baseUrl, string apiKey)
    {
        if (baseUrl.Length == 0)
        {
            throw new InvalidOperationException(
                "base URL is required for Gemini OpenAI transformer");
        }

        if (apiKey.Length == 0)
        {
            throw new InvalidOperationException(
                "API key provider is required for Gemini OpenAI transformer");
        }

        return new GeminiOpenAiOutboundTransformer(new GeminiOpenAiOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = new StaticApiKeyProvider(apiKey),
        });
    }

    public string ApiFormat => ApiFormats.OpenAIChatCompletion;

    public string BaseUrl => _baseUrl;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>Port of TransformRequest (outbound.go:274-374).</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("chat completion request is nil");
        }

        switch (request.RequestType)
        {
            case null or "" or RequestTypes.Chat:
                break;
            case RequestTypes.Compact:
                throw TransformerErrors.InvalidRequest(
                    "compact is only supported by OpenAI Responses API");
            default:
                throw TransformerErrors.InvalidRequest(request.RequestType + " is not supported");
        }

        if (string.IsNullOrEmpty(request.Model))
        {
            throw new InvalidOperationException("model is required");
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("messages are required");
        }

        // Go: req := *llmReq — a shallow copy; the Gemini OpenAI endpoint does
        // not accept metadata.
        var req = request.ShallowClone();
        req.Metadata = null;

        // Graceful degradation: the OpenAI-compatible endpoint cannot carry
        // Google native tools. When none remain, tool_choice has nothing to
        // point at either.
        if (GoogleToolFilter.ContainsGoogleNativeTools(req.Tools))
        {
            req.Tools = GoogleToolFilter.FilterGoogleNativeTools(req.Tools);
            if (req.Tools is null || req.Tools.Count == 0)
            {
                req.Tools = null;
                req.ToolChoice = null;
            }
        }

        ExtraBody? extraBody = null;
        if (req.ExtraBody is { } rawExtraBody)
        {
            extraBody = GeminiExtraBodyParser.ParseExtraBody(rawExtraBody);
            if (extraBody?.Google?.ThinkingConfig is { } thinkingConfig)
            {
                FillThinkingConfigFromReasoningEffort(
                    thinkingConfig,
                    req.ReasoningEffort,
                    req.Model);
                req.ReasoningEffort = "";
            }
        }

        var oaiReq = req.RequestFromLlm(ReasoningField.Content)
            ?? throw new InvalidOperationException("chat completion request is nil");

        FillGeminiThoughtSignatureForGeminiOpenAIRequest(req, oaiReq);

        byte[] body;
        try
        {
            body = GoJson.Marshal(oaiReq);

            // Request embeds openai.Request and appends extra_body last; Go's
            // marshal places it after every embedded field.
            if (extraBody is not null)
            {
                body = RawJsonPatch.SetTopLevelRaw(
                    body,
                    "extra_body"u8,
                    GoJson.Marshal(extraBody));
            }
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                "failed to transform request: " + ex.Message,
                ex);
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        return new HttpRequestData
        {
            Method = "POST",
            Url = _baseUrl + "/chat/completions",
            Headers = headers,
            Body = body,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
            SkipInboundQueryMerge = true,
            Metadata = null,
            ApiFormat = ApiFormats.OpenAIChatCompletion,
        };
    }

    /// <summary>
    /// Port of <c>thinkingConfigToReasoningEffort</c> (outbound.go:181-211).
    /// ThinkingLevel wins over ThinkingBudget; only the four canonical integer
    /// budgets have an effort mapping.
    /// </summary>
    internal static string ThinkingConfigToReasoningEffort(ThinkingConfig? config)
    {
        if (config is null)
        {
            return "";
        }

        if (config.ThinkingLevel is { Length: > 0 } level)
        {
            return level;
        }

        if (config.ThinkingBudget is { } budget)
        {
            if (budget.IntValue is { } intValue)
            {
                return intValue switch
                {
                    1024 => ReasoningEfforts.Low,
                    8192 => ReasoningEfforts.Medium,
                    24576 => ReasoningEfforts.High,
                    0 => ReasoningEfforts.None,
                    _ => "",
                };
            }

            if (budget.StringValue is { } stringValue)
            {
                return stringValue;
            }
        }

        return "";
    }

    /// <summary>Port of <c>reasoningEffortToThinkingBudget</c> (outbound.go:231-244).</summary>
    internal static ThinkingBudget? ReasoningEffortToThinkingBudget(string effort)
    {
        return effort.ToLowerInvariant() switch
        {
            "minimal" or "low" => new ThinkingBudget { IntValue = 1024 },
            "medium" => new ThinkingBudget { IntValue = 8192 },
            "high" => new ThinkingBudget { IntValue = 24576 },
            "none" => new ThinkingBudget { IntValue = 0 },
            _ => null,
        };
    }

    /// <summary>
    /// Port of <c>fillThinkingConfigFromReasoningEffort</c> (outbound.go:246-272).
    /// Gemini 2.5 accepts the integer ladder; every other model (Gemini 3)
    /// receives the lowercased effort as the thinking level.
    /// </summary>
    internal static void FillThinkingConfigFromReasoningEffort(
        ThinkingConfig config,
        string? reasoningEffort,
        string model)
    {
        if (config.ThinkingLevel is { Length: > 0 })
        {
            config.ThinkingBudget = null;
            return;
        }

        if (config.ThinkingBudget is not null)
        {
            return;
        }

        if (string.IsNullOrEmpty(reasoningEffort))
        {
            return;
        }

        if (IsGemini25Model(model))
        {
            var budget = ReasoningEffortToThinkingBudget(reasoningEffort);
            if (budget is not null)
            {
                config.ThinkingBudget = budget;
                return;
            }
        }

        config.ThinkingLevel = reasoningEffort.ToLowerInvariant();
    }

    internal static bool IsGemini25Model(string model)
        => model.ToLowerInvariant().Contains("gemini-2.5", StringComparison.Ordinal);

    /// <summary>
    /// Port of <c>fillGeminiThoughtSignatureForGeminiOpenAIRequest</c>
    /// (outbound.go:376-437). Gemini OpenAI responses carry the raw signature
    /// (no prefix encoding), so metadata values pass through verbatim; when no
    /// tool call carries one, the message-level reasoning signature seeds the
    /// first call.
    /// </summary>
    internal static void FillGeminiThoughtSignatureForGeminiOpenAIRequest(
        LlmRequest source,
        OpenAiRequest destination)
    {
        var srcMessages = source.Messages;
        var dstMessages = destination.Messages;
        if (srcMessages is null || dstMessages is null)
        {
            return;
        }

        for (var i = 0; i < srcMessages.Count; i++)
        {
            if (i >= dstMessages.Count)
            {
                break;
            }

            var srcMsg = srcMessages[i];
            var dstMsg = dstMessages[i];
            if (srcMsg is null || dstMsg is null
                || srcMsg.ToolCalls is not { Count: > 0 } srcToolCalls
                || dstMsg.ToolCalls is not { Count: > 0 } dstToolCalls)
            {
                continue;
            }

            var dstToolCallIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var j = 0; j < dstToolCalls.Count; j++)
            {
                if (!string.IsNullOrEmpty(dstToolCalls[j].Id))
                {
                    dstToolCallIndexById[dstToolCalls[j].Id!] = j;
                }
            }

            var hasToolCallThoughtSignature = false;

            for (var j = 0; j < srcToolCalls.Count; j++)
            {
                var srcToolCall = srcToolCalls[j];
                if (srcToolCall?.TransformerMetadata is null
                    || !srcToolCall.TransformerMetadata.TryGetValue(
                        OpenAiConvertToLlm.TransformerMetadataKeyGoogleThoughtSignature,
                        out var raw)
                    || raw.ValueKind != JsonValueKind.String
                    || raw.GetString() is not { Length: > 0 } signature)
                {
                    continue;
                }

                var dstToolCallIndex = -1;

                if (!string.IsNullOrEmpty(srcToolCall.Id)
                    && dstToolCallIndexById.TryGetValue(srcToolCall.Id!, out var matched))
                {
                    dstToolCallIndex = matched;
                }

                if (dstToolCallIndex == -1 && j < dstToolCalls.Count)
                {
                    dstToolCallIndex = j;
                }

                if (dstToolCallIndex == -1)
                {
                    continue;
                }

                EnsureGoogleThoughtSignatureExtraContent(dstToolCalls[dstToolCallIndex])
                    .ThoughtSignature = signature;
                hasToolCallThoughtSignature = true;
            }

            if (hasToolCallThoughtSignature)
            {
                continue;
            }

            if (srcMsg.ReasoningSignature is { Length: > 0 } reasoningSignature
                && dstToolCalls.Count > 0)
            {
                EnsureGoogleThoughtSignatureExtraContent(dstToolCalls[0])
                    .ThoughtSignature = reasoningSignature;
            }
        }
    }

    private static OpenAiToolCallGoogleExtraContent EnsureGoogleThoughtSignatureExtraContent(
        OpenAiToolCall toolCall)
    {
        toolCall.ExtraContent ??= new OpenAiToolCallExtraContent();
        toolCall.ExtraContent.Google ??= new OpenAiToolCallGoogleExtraContent();
        return toolCall.ExtraContent.Google;
    }

    /// <summary>
    /// Port of the Gemini branch of <c>TransformError</c> (outbound.go:451-517):
    /// the Gemini OpenAI endpoint reports errors inside an array envelope;
    /// object envelopes and raw bodies are also accepted, and an empty body
    /// falls back to the embedded OpenAI transformer.
    /// </summary>
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

        if (TryParseArrayEnvelope(error.Body, out var detail)
            || TryParseObjectEnvelope(error.Body, out detail))
        {
            return new ResponseError { StatusCode = error.StatusCode, Detail = detail };
        }

        if (error.Body.Length > 0)
        {
            return new ResponseError
            {
                StatusCode = error.StatusCode,
                Detail = new ErrorDetail
                {
                    Message = Encoding.UTF8.GetString(error.Body),
                    Type = "api_error",
                },
            };
        }

        return _inner.TransformError(error);
    }

    /// <summary>Port of the <c>[]geminiOpenAIErrorEnvelope</c> branch.</summary>
    private static bool TryParseArrayEnvelope(byte[] body, out ErrorDetail detail)
    {
        detail = new ErrorDetail();

        if (!TryParseRoot(body, out var root) || root.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        ErrorEnvelope? first = null;
        foreach (var element in root.EnumerateArray())
        {
            var envelope = ParseEnvelope(element, out var valid);
            if (!valid)
            {
                // Go fails the whole array decode on any bad element.
                return false;
            }

            first ??= envelope;
        }

        return first is not null && BuildDetail(first, out detail);
    }

    /// <summary>Port of the <c>geminiOpenAIErrorEnvelope</c> object branch.</summary>
    private static bool TryParseObjectEnvelope(byte[] body, out ErrorDetail detail)
    {
        detail = new ErrorDetail();

        if (!TryParseRoot(body, out var root) || root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var envelope = ParseEnvelope(root, out var valid);
        return valid && BuildDetail(envelope, out detail);
    }

    private static bool BuildDetail(ErrorEnvelope envelope, out ErrorDetail detail)
    {
        if (envelope.Message.Length == 0)
        {
            detail = new ErrorDetail();
            return false;
        }

        detail = new ErrorDetail
        {
            Code = envelope.Code.ToString(CultureInfo.InvariantCulture),
            Message = envelope.Message,
            Type = envelope.Status.Length == 0 ? "api_error" : envelope.Status,
        };
        return true;
    }

    private static bool TryParseRoot(byte[] body, out JsonElement root)
    {
        root = default;

        // Go validates the whole document first; ill-formed UTF-8 inside a
        // string literal is repaired before decoding.
        if (GoJsonSyntax.Validate(body) is not null)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(body) ?? body);
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes one <c>{error:{code,message,status}}</c> envelope with Go's
    /// struct rules: case-insensitive field match, later duplicates win, a null
    /// value is a no-op and any other type error fails the whole document.
    /// </summary>
    private static ErrorEnvelope ParseEnvelope(JsonElement element, out bool valid)
    {
        valid = true;

        if (element.ValueKind == JsonValueKind.Null)
        {
            return new ErrorEnvelope();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            valid = false;
            return new ErrorEnvelope();
        }

        var envelope = new ErrorEnvelope();
        var errorSeen = false;
        var errorEnvelope = new ErrorEnvelope();

        foreach (var property in element.EnumerateObject())
        {
            if (!FieldEquals(property.Name, "error"))
            {
                continue;
            }

            errorSeen = true;
            errorEnvelope = ParseErrorDetail(property.Value, out valid);
            if (!valid)
            {
                return new ErrorEnvelope();
            }
        }

        return errorSeen ? errorEnvelope : new ErrorEnvelope();
    }

    private static ErrorEnvelope ParseErrorDetail(JsonElement element, out bool valid)
    {
        valid = true;

        if (element.ValueKind == JsonValueKind.Null)
        {
            return new ErrorEnvelope();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            valid = false;
            return new ErrorEnvelope();
        }

        var envelope = new ErrorEnvelope();
        foreach (var property in element.EnumerateObject())
        {
            if (FieldEquals(property.Name, "code"))
            {
                switch (property.Value.ValueKind)
                {
                    case JsonValueKind.Null:
                        continue;
                    case JsonValueKind.Number:
                        if (property.Value.TryGetInt64(out var code))
                        {
                            envelope.Code = code;
                            continue;
                        }

                        valid = false;
                        return new ErrorEnvelope();
                    default:
                        valid = false;
                        return new ErrorEnvelope();
                }
            }

            if (FieldEquals(property.Name, "message"))
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    valid = false;
                    return new ErrorEnvelope();
                }

                envelope.Message = property.Value.GetString() ?? string.Empty;
                continue;
            }

            if (FieldEquals(property.Name, "status"))
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    valid = false;
                    return new ErrorEnvelope();
                }

                envelope.Status = property.Value.GetString() ?? string.Empty;
            }
        }

        return envelope;
    }

    private static bool FieldEquals(string name, string tag)
    {
        if (name.Length != tag.Length)
        {
            return false;
        }

        for (var i = 0; i < name.Length; i++)
        {
            if (FoldAscii(name[i]) != FoldAscii(tag[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static char FoldAscii(char c) => c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;

    private sealed class ErrorEnvelope
    {
        public long Code { get; set; }

        public string Message { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;
    }

    /// <summary>Port of TransformResponse: the embedded OpenAI transformer.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response) => _inner.TransformResponse(response);

    /// <summary>Port of TransformStream: the embedded OpenAI transformer.</summary>
    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
        => _inner.TransformStream(request, stream);

    /// <summary>Port of AggregateStreamChunks: the embedded OpenAI transformer.</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => _inner.AggregateStreamChunks(request, chunks, out meta);
}
