using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenRouter;

namespace AxonHub.Llm.Transformer.Cerebras;

/// <summary>Port of cerebras.Config.</summary>
public sealed class CerebrasOutboundConfig
{
    public string BaseUrl { get; set; } = string.Empty;

    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>Port-only audit index; see <see cref="IApiKeyProvenance"/>.</summary>
    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of llm/transformer/cerebras/outbound.go. Cerebras wraps the OpenRouter
/// transformer and strips OpenAI-specific fields it does not support (store).
/// </summary>
public sealed class CerebrasOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    public const string DefaultBaseUrl = "https://api.cerebras.ai/v1";

    private readonly OpenRouterOutboundTransformer _inner;
    private readonly int? _channelApiKeyIndex;

    public CerebrasOutboundTransformer(CerebrasOutboundConfig? config)
    {
        if (config is null)
        {
            throw new InvalidOperationException(
                "invalid Cerebras transformer configuration: config is nil");
        }

        if (config.ApiKeyProvider is null)
        {
            throw new InvalidOperationException(
                "invalid Cerebras transformer configuration: API key provider is required");
        }

        _channelApiKeyIndex = config.ChannelApiKeyIndex;

        var baseUrl = config.BaseUrl.Length == 0 ? DefaultBaseUrl : config.BaseUrl;

        try
        {
            _inner = new OpenRouterOutboundTransformer(new OpenRouterOutboundConfig
            {
                BaseUrl = baseUrl,
                ApiKeyProvider = config.ApiKeyProvider,
                ChannelApiKeyIndex = config.ChannelApiKeyIndex,
            });
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "invalid Cerebras transformer configuration: " + ex.Message, ex);
        }
    }

    public string ApiFormat => _inner.ApiFormat;

    public string? SelectedApiKey => _inner.SelectedApiKey;

    public int? SelectedApiKeyIndex => _channelApiKeyIndex;

    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("chat completion request is nil");
        }

        var copy = request.ShallowClone();
        copy.Store = null; // Cerebras does not support the `store` parameter.

        return _inner.TransformRequest(copy);
    }

    public LlmResponse TransformResponse(HttpResponseData? response)
        => _inner.TransformResponse(response);

    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
        => _inner.TransformStream(request, stream);

    public ResponseError TransformError(HttpClientError? error)
        => _inner.TransformError(error);

    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => _inner.AggregateStreamChunks(request, chunks, out meta);
}
