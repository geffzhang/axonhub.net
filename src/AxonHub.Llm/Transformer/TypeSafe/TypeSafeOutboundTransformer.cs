using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.TypeSafe;

/// <summary>
/// Port of typesafe.OutboundTransformer (llm/transformer/typesafe/outbound.go):
/// speaks the TypeSafe SystemOne decision protocol to an upstream.
/// </summary>
public sealed class TypeSafeOutboundTransformer
    : IOutboundTransformer, IApiKeyProvenance, IPassThroughBodyPolicy
{
    private readonly TypeSafeOutboundConfig _config;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    public TypeSafeOutboundTransformer(TypeSafeOutboundConfig config)
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
            config.BaseUrl = "https://api.typesafe.ai/v1";
        }

        config.BaseUrl = HttpClientUtils.NormalizeBaseUrl(
            config.BaseUrl,
            config.EndpointPath.Length > 0 ? string.Empty : "v1");

        _config = config;
    }

    /// <summary>Port of NewOutboundTransformer(baseURL, apiKey).</summary>
    public static TypeSafeOutboundTransformer Create(string baseUrl, string apiKey)
        => Create(baseUrl, new StaticApiKeyProvider(apiKey));

    /// <summary>
    /// Builds a transformer with a key provider and its audit index, mirroring
    /// the OpenAI outbound factory (getAPIKeyProvider + channel_api_key_index).
    /// </summary>
    public static TypeSafeOutboundTransformer Create(
        string baseUrl,
        IApiKeyProvider apiKeyProvider,
        int? channelApiKeyIndex = null,
        string endpointPath = "")
        => new(new TypeSafeOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = channelApiKeyIndex,
            EndpointPath = endpointPath,
        });

    /// <summary>
    /// Port of TypeSafeOutboundTransformer.AllowPassThroughBody: only SystemOne
    /// requests may replay the raw body.
    /// </summary>
    public bool AllowPassThroughBody(LlmRequest? llmRequest, HttpRequestData? providerRequest)
    {
        _ = providerRequest;
        return llmRequest is not null && llmRequest.RequestType == RequestTypes.SystemOne;
    }

    /// <summary>Port of APIFormat.</summary>
    public string ApiFormat => ApiFormats.TypeSafeSystemOne;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>Port of TransformRequest.</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("llm request is nil");
        }

        if (request.SystemOne is null)
        {
            throw new InvalidOperationException("systemone request is nil");
        }

        if (string.IsNullOrEmpty(request.Model))
        {
            throw new InvalidOperationException("model is required");
        }

        // The reference builds the wire request fresh and never copies
        // request.Stream, so the body has no "stream" member at all.
        var wireReq = new SystemOneWireRequest
        {
            Model = request.Model,
            State = request.SystemOne.State,
            Questions = request.SystemOne.Questions,
        };

        var body = GoJson.Marshal(wireReq);

        var apiKey = _config.ApiKeyProvider.Get();
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
            RequestType = RequestTypes.SystemOne,
            ApiFormat = ApiFormats.TypeSafeSystemOne,
        };
    }

    /// <summary>Port of buildURL.</summary>
    public string BuildUrl()
        => _config.EndpointPath.Length > 0
            ? _config.BaseUrl + _config.EndpointPath
            : _config.BaseUrl + "/systemone";

    /// <summary>Port of TransformResponse.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("http response is nil");
        }

        if (response.StatusCode >= 400)
        {
            throw TransformError(new HttpClientError(
                "POST",
                string.Empty,
                response.StatusCode,
                GoHttpStatus.Text(response.StatusCode),
                response.Body));
        }

        if (response.Body is null || response.Body.Length == 0)
        {
            throw new InvalidOperationException("response body is empty");
        }

        // Go checks json.Valid first: trailing bytes make the body invalid,
        // unlike the streaming decode the inbound transformer uses.
        if (GoJsonSyntax.Validate(response.Body) is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal systemone response: invalid json");
        }

        var rootKind = GoStructJson.RootKind(response.Body);
        if (rootKind is not null)
        {
            // null leaves the zero struct in place; other roots are a type error.
            return rootKind == "null"
                ? WireToLlm(new SystemOneWireResponse())
                : throw new InvalidOperationException(
                    "failed to unmarshal systemone response: "
                    + GoStructJson.TypeMismatch(rootKind, "typesafe.systemOneWireResponse"));
        }

        SystemOneWireResponse wireResp;
        try
        {
            wireResp = LlmJson.Deserialize<SystemOneWireResponse>(response.Body)
                ?? new SystemOneWireResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal systemone response: "
                + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }

        return WireToLlm(wireResp);
    }

    /// <summary>Port of the response-to-unified mapping in TransformResponse.</summary>
    private static LlmResponse WireToLlm(SystemOneWireResponse wireResp)
    {
        var llmResp = new LlmResponse
        {
            // Go decodes a JSON null string into the zero value "".
            Model = wireResp.Model ?? string.Empty,
            RequestType = RequestTypes.SystemOne,
            ApiFormat = ApiFormats.TypeSafeSystemOne,
            SystemOne = new SystemOneResponse
            {
                // A null map value decodes as the zero answer in Go.
                Answers = SystemOneJson.CoalesceAnswers(wireResp.Answers),
            },
        };

        if (wireResp.Usage is { } usage)
        {
            llmResp.Usage = new Usage
            {
                PromptTokens = usage.InputTokens,
                CompletionTokens = usage.OutputTokens,
                TotalTokens = usage.InputTokens + usage.OutputTokens,
            };
        }

        return llmResp;
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
                    Message = GoHttpStatus.Text(500),
                    Type = "api_error",
                },
            };
        }

        // error is also the inner exception so Go's Unwrap chain — and therefore
        // errors.As(err, &httpErr) — still reaches the transport error, exactly
        // like llm.ResponseError.Unwrap (model.go:918).
        var wireError = TryParseError(error.Body);
        if (wireError is { Message.Length: > 0 })
        {
            return new ResponseError(string.Empty, error)
            {
                StatusCode = error.StatusCode,
                CauseException = error,
                Detail = new ErrorDetail
                {
                    Message = wireError.Message,
                    Type = wireError.Type ?? string.Empty,
                    // Go: code has omitempty; type does not.
                    Code = string.IsNullOrEmpty(wireError.Code) ? null : wireError.Code,
                },
            };
        }

        return new ResponseError(string.Empty, error)
        {
            StatusCode = error.StatusCode,
            CauseException = error,
            Detail = new ErrorDetail
            {
                Message = error.Body is { Length: > 0 } body ? Encoding.UTF8.GetString(body) : string.Empty,
                Type = "api_error",
            },
        };
    }

    /// <summary>Port of TransformStream: systemone never streams.</summary>
    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
        => throw new InvalidOperationException("systemone does not support streaming");

    /// <summary>Port of AggregateStreamChunks: systemone never streams.</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => throw new InvalidOperationException("systemone does not support streaming");

    /// <summary>
    /// Go <c>json.Unmarshal(body, &amp;struct{ Error struct{...} })</c>: invalid
    /// JSON, a non-object root or a mistyped member all fail and fall back to
    /// the raw-body envelope.
    /// </summary>
    private static WireError? TryParseError(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            return null;
        }

        if (GoJsonSyntax.Validate(body) is not null || GoStructJson.RootKind(body) is not null)
        {
            return null;
        }

        try
        {
            return LlmJson.Deserialize<SystemOneWireErrorEnvelope>(body)?.Error;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class SystemOneWireErrorEnvelope
    {
        [JsonPropertyName("error")]
        public WireError? Error { get; set; }
    }

    private sealed class WireError
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }
    }
}

/// <summary>Port of typesafe.Config (llm/transformer/typesafe/outbound.go).</summary>
public sealed class TypeSafeOutboundConfig
{
    public string BaseUrl { get; set; } = string.Empty;

    public IApiKeyProvider ApiKeyProvider { get; set; } = null!;

    /// <summary>Optional custom path override, replacing "/systemone".</summary>
    public string EndpointPath { get; set; } = string.Empty;

    /// <summary>1-based index over the channel's full key list, or null when unambiguous.</summary>
    public int? ChannelApiKeyIndex { get; set; }
}
