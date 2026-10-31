using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.OpenAI.Decisions;

/// <summary>Port of openai/decisions.Config (outbound.go:20-24).</summary>
public sealed class DecisionsOutboundConfig
{
    public string BaseUrl { get; set; } = string.Empty;

    public IApiKeyProvider? ApiKeyProvider { get; set; }

    public string EndpointPath { get; set; } = string.Empty;

    /// <summary>
    /// Port-only audit field: 1-based position of the key the provider serves in
    /// the channel's full key list, or null when the channel has a single key.
    /// </summary>
    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of openai/decisions.OutboundTransformer
/// (llm/transformer/openai/decisions/outbound.go): forwards the raw request body
/// with only the routed model patched, and wraps the provider's raw answer body.
/// </summary>
public sealed class DecisionsOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    /// <summary>Go's encoding/json accepts nesting up to 10000 levels.</summary>
    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 10_000 };

    private readonly DecisionsOutboundConfig _config;
    private readonly bool _rawUrl;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    public DecisionsOutboundTransformer(DecisionsOutboundConfig? config)
    {
        if (config is null)
        {
            throw new InvalidOperationException("config cannot be nil");
        }

        if (config.ApiKeyProvider is null)
        {
            throw new InvalidOperationException("API key provider is required");
        }

        if (string.IsNullOrEmpty(config.BaseUrl))
        {
            throw new InvalidOperationException("base URL is required");
        }

        _rawUrl = config.BaseUrl.EndsWith("##", StringComparison.Ordinal);
        if (_rawUrl)
        {
            config.BaseUrl = config.BaseUrl[..^2];
        }
        else if (config.EndpointPath.Length > 0)
        {
            config.BaseUrl = HttpClientUtils.NormalizeBaseUrl(config.BaseUrl, string.Empty);
        }
        else
        {
            config.BaseUrl = HttpClientUtils.NormalizeBaseUrl(config.BaseUrl, "v1");
        }

        _config = config;
    }

    /// <summary>Port of NewOutboundTransformer(baseURL, apiKey).</summary>
    public static DecisionsOutboundTransformer Create(string baseUrl, string apiKey)
        => Create(baseUrl, new StaticApiKeyProvider(apiKey));

    /// <summary>Port of NewOutboundTransformerWithConfig for the channel factory.</summary>
    public static DecisionsOutboundTransformer Create(
        string baseUrl,
        IApiKeyProvider apiKeyProvider,
        int? channelApiKeyIndex = null)
        => new(new DecisionsOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = channelApiKeyIndex,
        });

    public string ApiFormat => ApiFormats.OpenAIDecisions;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>Port of OutboundTransformer.TransformRequest.</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request?.Decisions is null || request.Decisions.Body.Length == 0)
        {
            throw new InvalidOperationException("decisions request is nil");
        }

        if (request.Model.Trim().Length == 0)
        {
            throw new InvalidOperationException("model is required");
        }

        byte[] body;
        try
        {
            body = RawJsonModelPatch.SetModel(request.Decisions.Body, request.Model);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "failed to patch decisions model: " + ex.Message, ex);
        }

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        return new HttpRequestData
        {
            Method = "POST",
            Url = BuildUrl(),
            Headers = headers,
            Body = body,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
            RequestType = RequestTypes.Decisions,
            ApiFormat = ApiFormats.OpenAIDecisions,
        };
    }

    /// <summary>Port of OutboundTransformer.buildURL (outbound.go:89-97).</summary>
    private string BuildUrl()
    {
        if (_rawUrl)
        {
            return _config.BaseUrl.TrimEnd('/');
        }

        return _config.EndpointPath.Length > 0
            ? _config.BaseUrl + _config.EndpointPath
            : _config.BaseUrl + "/decisions";
    }

    /// <summary>Port of OutboundTransformer.TransformResponse.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("http response is nil");
        }

        if (response.StatusCode >= 300)
        {
            // The reference builds the error without a status line; only the
            // status code, body and headers reach TransformError.
            var httpError = new HttpClientError(
                string.Empty,
                string.Empty,
                response.StatusCode,
                string.Empty,
                response.Body)
            {
                Headers = response.Headers,
            };
            throw TransformError(httpError);
        }

        var body = response.Body ?? [];
        if (body.Length == 0)
        {
            return new LlmResponse
            {
                RequestType = RequestTypes.Decisions,
                ApiFormat = ApiFormats.OpenAIDecisions,
                Decisions = new DecisionsResponse(),
            };
        }

        if (GoJsonSyntax.Validate(body) is not null)
        {
            throw TransformerErrors.InvalidResponse("invalid decisions response body");
        }

        DecisionsWireResponse wire;
        try
        {
            wire = DecisionsWire.DecodeResponse(body);
        }
        catch (DecisionsWireException ex)
        {
            throw TransformerErrors.InvalidResponse(
                "failed to decode decisions response: " + ex.Message, ex);
        }

        var parsed = new DecisionsResponse
        {
            // append([]byte(nil), response.Body...)
            Body = (byte[])body.Clone(),
            Model = wire.Model,
            Answers = wire.Answers,
        };
        if (wire.Usage is not null)
        {
            parsed.Usage = new DecisionsUsage { InputTokens = wire.Usage.InputTokens };
        }

        var result = new LlmResponse
        {
            Model = wire.Model,
            RequestType = RequestTypes.Decisions,
            ApiFormat = ApiFormats.OpenAIDecisions,
            Decisions = parsed,
        };
        if (parsed.Usage is not null)
        {
            result.Usage = new Usage { PromptTokens = parsed.Usage.InputTokens };
        }

        return result;
    }

    /// <summary>Go: OutboundTransformer.TransformStream.</summary>
    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
        => throw TransformerErrors.InvalidRequest("decisions does not support streaming");

    /// <summary>Go: OutboundTransformer.AggregateStreamChunks.</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
    {
        meta = new ResponseMeta();
        throw new InvalidOperationException("decisions does not support streaming");
    }

    /// <summary>
    /// Port of OutboundTransformer.TransformError (outbound.go:158-173): the
    /// provider body is decoded leniently, decode failures are ignored, and the
    /// cause is kept for downstream <c>errors.As</c> matching.
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

        var detail = DecodeLenientDetail(error.Body);
        if (detail.Message.Length == 0)
        {
            detail.Message = Encoding.UTF8.GetString(error.Body).Trim();
        }

        if (detail.Type.Length == 0)
        {
            detail.Type = "api_error";
        }

        return new ResponseError(string.Empty, error)
        {
            StatusCode = error.StatusCode,
            Detail = detail,
            CauseException = error,
        };
    }

    /// <summary>
    /// Port of the ignored <c>json.Unmarshal(body, &amp;struct{Error ErrorDetail})</c>:
    /// syntax errors decode nothing; per-field type errors keep every value
    /// decoded before them.
    /// </summary>
    private static ErrorDetail DecodeLenientDetail(byte[] body)
    {
        var detail = new ErrorDetail();
        if (GoJsonSyntax.Validate(body) is not null)
        {
            return detail;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(body) ?? body, ParseOptions);
        }
        catch (JsonException)
        {
            return detail;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return detail;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, "error", StringComparison.OrdinalIgnoreCase))
                {
                    DecodeErrorDetail(property.Value, detail);
                }
            }
        }

        return detail;
    }

    private static void DecodeErrorDetail(JsonElement value, ErrorDetail detail)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            // null is a no-op; any other kind is a type error the reference
            // deliberately ignores.
            return;
        }

        foreach (var property in value.EnumerateObject())
        {
            switch (GoFoldField(property.Name))
            {
                case "code":
                    detail.Code = ReadLenientString(property.Value, detail.Code);
                    break;
                case "message":
                    // Message starts non-null and the helper either keeps it or
                    // returns a decoded string, so the result is never null.
                    detail.Message = ReadLenientString(property.Value, detail.Message)!;
                    break;
                case "type":
                    detail.Type = ReadLenientString(property.Value, detail.Type)!;
                    break;
                case "param":
                    detail.Param = ReadLenientString(property.Value, detail.Param);
                    break;
                case "request_id":
                    detail.RequestId = ReadLenientString(property.Value, detail.RequestId);
                    break;
            }
        }
    }

    /// <summary>Decoding into <c>string</c>: null is a no-op, non-strings are skipped.</summary>
    private static string? ReadLenientString(JsonElement value, string? current)
        => value.ValueKind switch
        {
            JsonValueKind.Null => current,
            JsonValueKind.String => value.GetString() ?? string.Empty,
            _ => current,
        };

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
}
