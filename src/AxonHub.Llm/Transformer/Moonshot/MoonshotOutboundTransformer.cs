using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Moonshot;

/// <summary>Port of moonshot.Config (llm/transformer/moonshot/outbound.go:16-22).</summary>
public sealed class MoonshotOutboundConfig
{
    /// <summary>Custom base URL (optional in the reference; the OpenAI outbound requires one).</summary>
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
/// Port of the Moonshot outbound transformer (llm/transformer/moonshot/outbound.go):
/// an OpenAI chat transformer with its own request path, which rejects every
/// request type except chat and downgrades <c>json_schema</c> to
/// <c>json_object</c> because Moonshot cannot express a schema.
/// </summary>
/// <remarks>
/// The reference embeds the OpenAI outbound, so response, stream, error and
/// aggregation handling delegate to it unchanged; only <c>TransformRequest</c>
/// is overridden. Consequently the Moonshot request path skips the OpenAI
/// outbound's model/document validation and its tool-call extra-content
/// stripping. A nil config panics in Go; the port raises
/// <see cref="InvalidOperationException"/> (C098).
/// </remarks>
public sealed class MoonshotOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    private readonly MoonshotOutboundConfig _config;
    private readonly OpenAiOutboundTransformer _inner;
    private readonly string _baseUrl;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    /// <summary>Port of NewOutboundTransformerWithConfig.</summary>
    public MoonshotOutboundTransformer(MoonshotOutboundConfig config)
    {
        if (config is null)
        {
            throw new InvalidOperationException(
                "invalid Moonshot transformer configuration: config cannot be nil");
        }

        _config = config;

        try
        {
            _inner = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
            {
                PlatformType = OpenAiPlatformType.OpenAi,
                BaseUrl = config.BaseUrl,
                ApiKeyProvider = config.ApiKeyProvider,
                ReasoningField = ReasoningField.Content,
                ChannelApiKeyIndex = config.ChannelApiKeyIndex,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "invalid Moonshot transformer configuration: " + ex.Message,
                ex);
        }

        // transformer.NormalizeBaseURL(config.BaseURL, "v1") runs on the raw
        // config URL, not on the already-normalized OpenAI config.
        _baseUrl = HttpClientUtils.NormalizeBaseUrl(config.BaseUrl, "v1");
    }

    public string ApiFormat => ApiFormats.OpenAIChatCompletion;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>Port of TransformRequest.</summary>
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
                throw TransformerErrors.InvalidRequest("compact is only supported by OpenAI Responses API");
            default:
                throw TransformerErrors.InvalidRequest(request.RequestType + " is not supported");
        }

        if (request.Messages.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("messages are required");
        }

        var oaiReq = request.RequestFromLlm(ReasoningField.Content)
            ?? throw new InvalidOperationException("chat completion request is nil");

        if (oaiReq.ResponseFormat is { Type: "json_schema" } responseFormat)
        {
            // Moonshot doesn't support json_schema, convert to json_object.
            responseFormat.Type = "json_object";
            responseFormat.JsonSchema = null;
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
            Body = GoJson.Marshal(oaiReq),
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
            ApiFormat = ApiFormats.OpenAIChatCompletion,
        };
    }

    /// <summary>Port of the embedded OpenAI outbound: response handling is untouched.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
        => _inner.TransformResponse(response);

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
        => _inner.TransformStream(request, stream);

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public ResponseError TransformError(HttpClientError? error) => _inner.TransformError(error);

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => _inner.AggregateStreamChunks(request, chunks, out meta);
}
