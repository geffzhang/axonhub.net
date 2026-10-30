using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>Port of anthropic.Config (outbound.go:44-69).</summary>
public sealed class AnthropicOutboundConfig
{
    /// <summary>Platform configuration; <see cref="AnthropicPlatformType.Direct"/> by default.</summary>
    public AnthropicPlatformType Type { get; set; } = AnthropicPlatformType.Direct;

    /// <summary>For Vertex.</summary>
    public string Region { get; set; } = string.Empty;

    /// <summary>For Vertex.</summary>
    public string ProjectId { get; set; } = string.Empty;

    /// <summary>For Vertex.</summary>
    public string JsonData { get; set; } = string.Empty;

    /// <summary>The base URL for the Anthropic API, required.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Provides API keys for authentication.</summary>
    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>
    /// Optional custom path override. When set it replaces the default API path
    /// (<c>/messages</c>) and skips the default version normalization.
    /// </summary>
    public string EndpointPath { get; set; } = string.Empty;

    /// <summary>Maps reasoning-effort values to Anthropic thinking budget tokens.</summary>
    public Dictionary<string, long>? ReasoningEffortToBudget { get; set; }

    /// <summary>
    /// Port-only audit field: 1-based position of the key the provider serves in
    /// the channel's full key list, or null when the channel has a single key.
    /// Persisted on request_executions.channel_api_key_index.
    /// </summary>
    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of the Anthropic Messages outbound transformer
/// (llm/transformer/anthropic/outbound.go, outbound_convert.go,
/// outbound_stream.go).
/// </summary>
/// <remarks>
/// Scope cut: the Vertex AI executor (and its OAuth/JWT signing) is not ported,
/// so a Vertex config is rejected at construction. The Bedrock wire envelope is
/// a codec on the shared HTTP client in the reference and is likewise not
/// ported; it is unreachable because <c>ChannelOutboundFactory</c> only builds
/// Direct transformers (difference C056).
/// </remarks>
public sealed class AnthropicOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    private readonly AnthropicOutboundConfig _config;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    /// <summary>Port of NewOutboundTransformerWithConfig.</summary>
    public AnthropicOutboundTransformer(AnthropicOutboundConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.Type == AnthropicPlatformType.Vertex)
        {
            // The reference wraps the transformer in a VertexTransformer backed
            // by vertex.NewExecutorFromJSON; that executor is not ported.
            throw new NotSupportedException(
                "Vertex AI executor is not ported: choose the direct Anthropic platform");
        }

        _config = config;

        // For Vertex/Bedrock the reference does not append a version segment -
        // they have special URL formats. Vertex is rejected above; Bedrock keeps
        // the shape so the config behaves the same if a codec lands later.
        _config.BaseUrl = config.Type is AnthropicPlatformType.Vertex or AnthropicPlatformType.Bedrock
            ? HttpClientUtils.NormalizeBaseUrl(config.BaseUrl, string.Empty)
            : config.EndpointPath.Length > 0
                ? HttpClientUtils.NormalizeBaseUrl(config.BaseUrl, string.Empty)
                : HttpClientUtils.NormalizeBaseUrl(config.BaseUrl, "v1");
    }

    /// <summary>Port of NewOutboundTransformer(baseURL, apiKey).</summary>
    public static AnthropicOutboundTransformer Create(string baseUrl, string apiKey)
        => Create(baseUrl, new StaticApiKeyProvider(apiKey));

    /// <summary>
    /// Builds a transformer with a key provider and its audit index
    /// (the reference's getAPIKeyProvider + channel_api_key_index row).
    /// </summary>
    public static AnthropicOutboundTransformer Create(
        string baseUrl,
        IApiKeyProvider apiKeyProvider,
        int? channelApiKeyIndex = null)
        => new(new AnthropicOutboundConfig
        {
            Type = AnthropicPlatformType.Direct,
            BaseUrl = baseUrl,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = channelApiKeyIndex,
        });

    public string ApiFormat => ApiFormats.AnthropicMessage;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    public AnthropicOutboundConfig Config => _config;

    /// <summary>Port of TransformRequest.</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("chat completion request is nil");
        }

        // Vertex/ClaudeCode use OAuth, not API keys, in the reference.
        var apiKey = _config.ApiKeyProvider?.Get() ?? string.Empty;
        _selectedApiKey = apiKey;

        switch (request.RequestType)
        {
            case null or "" or RequestTypes.Chat:
                break;
            case RequestTypes.Compact:
                throw TransformerErrors.InvalidRequest("compact is only supported by OpenAI Responses API");
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

        if (request.MaxTokens is { } maxTokens && maxTokens <= 0)
        {
            throw TransformerErrors.InvalidRequest("max_tokens must be positive");
        }

        AnthropicOutboundRequest.ValidateUnsupportedContentParts(request.Messages);

        // Content parts without an Anthropic equivalent are dropped during the
        // conversion below; reject a message left without any content instead of
        // forwarding "content": null.
        AnthropicOutboundRequest.ValidateDroppedContentParts(request.Messages);

        var anthropicRequest = AnthropicOutboundRequest.ConvertToAnthropicRequestWithConfig(request, _config);

        // System and developer messages become the top-level system prompt; with
        // nothing else there is no conversation turn left and Anthropic rejects
        // the empty messages array with an unhelpful error.
        if (anthropicRequest.Messages is null || anthropicRequest.Messages.Count == 0)
        {
            throw TransformerErrors.InvalidRequest(
                "messages must contain at least one user or assistant message; system and developer messages are converted to the system prompt");
        }

        // Automatic caching (top-level cache_control) is forwarded untouched; the
        // breakpoint optimizer only runs for explicit per-block breakpoints.
        if (anthropicRequest.CacheControl is null
            && AnthropicCacheControlOptimizer.CountCacheControls(anthropicRequest) > 0)
        {
            AnthropicCacheControlOptimizer.Optimize(anthropicRequest);
        }

        string url;
        try
        {
            url = BuildFullRequestUrl(request);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("failed to build platform URL: " + ex.Message, ex);
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        switch (_config.Type)
        {
            case AnthropicPlatformType.Bedrock:
                HttpClientUtils.HeaderSet(headers, "Anthropic-Version", "bedrock-2023-05-31");

                anthropicRequest.AnthropicVersion = "bedrock-2023-05-31";
                // The model is carried in the URL and stream is a URL variant.
                anthropicRequest.Model = null;
                anthropicRequest.Stream = null;
                break;
            case AnthropicPlatformType.Vertex:
                HttpClientUtils.HeaderSet(headers, "Anthropic-Version", "vertex-2023-10-16");
                break;
            default:
                HttpClientUtils.HeaderSet(headers, "Anthropic-Version", "2023-06-01");
                break;
        }

        byte[] body;
        try
        {
            body = GoJson.Marshal(anthropicRequest);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("failed to marshal anthropic request: " + ex.Message, ex);
        }

        // The beta header is added after marshalling; on Bedrock the reference
        // appends to anthropic_beta instead, which therefore never reaches the
        // already-marshalled body.
        if (AnthropicOutboundRequest.ContainsNativeWebSearchTool(anthropicRequest.Tools))
        {
            switch (_config.Type)
            {
                case AnthropicPlatformType.Direct:
                    HttpClientUtils.HeaderAdd(headers, "Anthropic-Beta", "web-search-2025-03-05");
                    break;
                case AnthropicPlatformType.Bedrock:
                    anthropicRequest.AnthropicBeta =
                    [
                        .. anthropicRequest.AnthropicBeta ?? [],
                        "web-search-2025-03-05",
                    ];
                    break;
            }
        }

        AuthConfig? authConfig = null;
        if (apiKey.Length > 0)
        {
            // LongCat, Ollama, Bedrock and Command Code use bearer auth.
            authConfig = _config.Type is AnthropicPlatformType.LongCat
                or AnthropicPlatformType.Ollama
                or AnthropicPlatformType.Bedrock
                or AnthropicPlatformType.CommandCode
                ? new AuthConfig { Type = AuthTypes.Bearer, ApiKey = apiKey }
                : new AuthConfig { Type = AuthTypes.ApiKey, ApiKey = apiKey, HeaderKey = "X-API-Key" };
        }

        return new HttpRequestData
        {
            Method = "POST",
            Url = url,
            Headers = headers,
            Body = body,
            Auth = authConfig,
            ApiFormat = ApiFormats.AnthropicMessage,
        };
    }

    /// <summary>Port of buildFullRequestURL.</summary>
    public string BuildFullRequestUrl(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        switch (_config.Type)
        {
            case AnthropicPlatformType.Bedrock:
                // Bedrock URL format: /model/{model}/invoke[-with-response-stream]
                var endpoint = request.Stream == true
                    ? "/model/" + request.Model + "/invoke-with-response-stream"
                    : "/model/" + request.Model + "/invoke";

                return _config.BaseUrl + endpoint;

            case AnthropicPlatformType.Vertex:
                // Vertex AI URL format:
                // /v1/projects/{project}/locations/{region}/publishers/anthropic/models/{model}:rawPredict
                if (string.IsNullOrEmpty(_config.ProjectId))
                {
                    throw new InvalidOperationException("project ID is required for Vertex AI");
                }

                if (string.IsNullOrEmpty(_config.Region))
                {
                    throw new InvalidOperationException("region is required for Vertex AI");
                }

                var specifier = request.Stream == true ? "streamRawPredict" : "rawPredict";

                return _config.BaseUrl
                    + "/v1/projects/" + _config.ProjectId
                    + "/locations/" + _config.Region
                    + "/publishers/anthropic/models/" + request.Model + ":" + specifier;

            default:
                // BaseUrl is already normalized with the version segment.
                if (_config.EndpointPath.Length > 0)
                {
                    return _config.BaseUrl + _config.EndpointPath;
                }

                return _config.BaseUrl + "/messages";
        }
    }

    /// <summary>Port of TransformResponse.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("http response is nil");
        }

        if (response.StatusCode >= 400)
        {
            throw new InvalidOperationException("HTTP error " + response.StatusCode);
        }

        if (response.Body is null || response.Body.Length == 0)
        {
            throw new InvalidOperationException("response body is empty");
        }

        // Go validates the whole document before decoding.
        var syntaxError = GoJsonSyntax.Validate(response.Body);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException("failed to unmarshal anthropic response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(response.Body);
        if (rootKind is not null && rootKind != "null")
        {
            throw new InvalidOperationException(
                "failed to unmarshal anthropic response: "
                + GoStructJson.TypeMismatch(rootKind, "anthropic.Message"));
        }

        // A JSON null leaves the zero struct in place (Go's Unmarshal no-op).
        var anthropicResponse = rootKind == "null"
            ? new AnthropicMessage()
            : DeserializeResponse(response.Body);

        return AnthropicOutboundResponse.ConvertToLlmResponse(anthropicResponse, _config.Type);
    }

    /// <summary>Port of TransformStream.</summary>
    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
    {
        _ = request;

        // Skip the events that do not contribute to the unified response, append
        // the DONE sentinel, then run the state machine. Null results are
        // dropped, like streams.NoNil.
        var filtered = new FilterStream<StreamEvent>(stream, AnthropicOutboundStream.FilterStreamEvent);
        var withDone = new AppendStream<StreamEvent>(
            filtered,
            new StreamEvent { Data = "[DONE]"u8.ToArray() });

        return new AnthropicOutboundStream(withDone, _config.Type);
    }

    /// <summary>Port of AggregateStreamChunks.</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
    {
        _ = request;
        return AnthropicAggregator.AggregateStreamChunks(chunks, _config.Type, out meta);
    }

    /// <summary>Port of TransformError.</summary>
    public ResponseError TransformError(HttpClientError? error)
    {
        if (error is null)
        {
            return new ResponseError
            {
                StatusCode = 500,
                Detail = new ErrorDetail
                {
                    Message = "Request failed.",
                    Type = "api_error",
                },
            };
        }

        var anthropicError = TryParseAnthropicError(error.Body);
        if (anthropicError is not null && anthropicError.Error.Message.Length > 0)
        {
            return new ResponseError
            {
                StatusCode = error.StatusCode,
                Detail = new ErrorDetail
                {
                    Type = "api_error",
                    Message = anthropicError.Error.Message,
                    RequestId = NullIfEmpty(anthropicError.RequestId),
                },
            };
        }

        var bodyText = Encoding.UTF8.GetString(error.Body);
        var statusText = GoHttpStatus.Text(error.StatusCode);

        return new ResponseError
        {
            StatusCode = error.StatusCode,
            Detail = new ErrorDetail
            {
                Message = bodyText.Length > 0 ? bodyText.Trim() : statusText,
                Type = "api_error",
                Code = statusText,
                Param = null,
                RequestId = null,
            },
        };
    }

    private AnthropicMessage DeserializeResponse(byte[] body)
    {
        try
        {
            return LlmJson.Deserialize<AnthropicMessage>(body)
                ?? throw new InvalidOperationException(
                    "failed to unmarshal anthropic response: unexpected null");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal anthropic response: " + AnthropicInboundTransformer.JsonErrorText(ex),
                ex);
        }
    }

    /// <summary>
    /// Decodes the Anthropic error envelope with Go's field rules: a null body
    /// or null members are no-ops, any other type mismatch fails the whole
    /// decode (which the caller turns into the body fallback). Returns null when
    /// the reference's <c>xjson.To</c> would have failed.
    /// </summary>
    private static AnthropicError? TryParseAnthropicError(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return null;
        }

        if (GoJsonSyntax.Validate(body) is not null)
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(body) ?? body);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
            {
                // Decoding null into the struct leaves it zero (message empty ->
                // the caller falls back).
                return new AnthropicError();
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var error = new AnthropicError();
            var errorSection = new AnthropicErrorDetail();

            foreach (var property in root.EnumerateObject())
            {
                // encoding/json matches struct fields case-insensitively.
                switch (GoFoldField(property.Name))
                {
                    case "type":
                        if (!TryReadGoString(property.Value, out var type))
                        {
                            return null;
                        }

                        error.Type = NullIfEmpty(type);
                        break;
                    case "request_id":
                        if (!TryReadGoString(property.Value, out var requestId))
                        {
                            return null;
                        }

                        error.RequestId = requestId;
                        break;
                    case "error":
                        if (property.Value.ValueKind == JsonValueKind.Null)
                        {
                            // Decoding null into the nested struct is a no-op.
                            errorSection = new AnthropicErrorDetail();
                            break;
                        }

                        if (property.Value.ValueKind != JsonValueKind.Object)
                        {
                            return null;
                        }

                        var section = new AnthropicErrorDetail();
                        foreach (var member in property.Value.EnumerateObject())
                        {
                            switch (GoFoldField(member.Name))
                            {
                                case "type":
                                    if (!TryReadGoString(member.Value, out var detailType))
                                    {
                                        return null;
                                    }

                                    section.Type = detailType;
                                    break;
                                case "message":
                                    if (!TryReadGoString(member.Value, out var detailMessage))
                                    {
                                        return null;
                                    }

                                    section.Message = detailMessage;
                                    break;
                            }
                        }

                        errorSection = section;
                        break;
                }
            }

            error.Error = errorSection;
            return error;
        }
    }

    /// <summary>Go string decode: null is a no-op on the zero value, non-strings are type errors.</summary>
    private static bool TryReadGoString(JsonElement element, out string value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                value = string.Empty;
                return true;
            case JsonValueKind.String:
                value = element.GetString() ?? string.Empty;
                return true;
            default:
                value = string.Empty;
                return false;
        }
    }

    /// <summary>Go's struct field lookup folds ASCII letters only.</summary>
    private static string GoFoldField(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'A' and <= 'Z')
            {
                chars[i] = (char)(chars[i] + ('a' - 'A'));
            }
        }

        return new string(chars);
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
