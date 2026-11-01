using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Modelscope;

/// <summary>Port of modelscope.Config (llm/transformer/modelscope/outbound.go:16-20).</summary>
public sealed class ModelscopeOutboundConfig
{
    /// <summary>Custom base URL (optional in the reference).</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Provides API keys for authentication, required.</summary>
    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>
    /// Port seam for the reference's optional proxy-aware
    /// <c>*httpclient.HttpClient</c>: used for task polling and image download.
    /// Null keeps the reference's <c>http.DefaultClient</c> fallback.
    /// </summary>
    public IExecutor? HttpClient { get; set; }

    /// <summary>Port-only audit field; see <see cref="IApiKeyProvenance"/>.</summary>
    public int? ChannelApiKeyIndex { get; set; }

    /// <summary>Delay between task polls; zero or negative uses two seconds.</summary>
    public TimeSpan? PollInterval { get; set; }

    /// <summary>Overall task budget; zero or negative uses fifteen minutes.</summary>
    public TimeSpan? TaskTimeout { get; set; }
}

/// <summary>
/// Port of the ModelScope outbound transformer
/// (llm/transformer/modelscope/outbound.go): the OpenAI outbound plus the
/// asynchronous image task protocol. Chat requests delegate to OpenAI unchanged
/// (metadata is dropped); image generation and editing submit a ModelScope task,
/// poll it to completion, and download the output URLs into base64 because
/// Codex-style clients only read <c>data[].b64_json</c>.
/// </summary>
/// <remarks>
/// The reference receives a <c>context.Context</c> in every method; the port's
/// context-free transformer interface carries the caller token on
/// <see cref="HttpRequestData.RequestCancellationToken"/> instead (C104).
/// </remarks>
public sealed partial class ModelscopeOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    private readonly ModelscopeOutboundConfig _config;
    private readonly OpenAiOutboundTransformer _inner;
    private readonly string _baseUrl;
    private string? _selectedApiKey;

    /// <summary>Port of NewOutboundTransformerWithConfig.</summary>
    public ModelscopeOutboundTransformer(ModelscopeOutboundConfig? config)
    {
        if (config is null)
        {
            throw new InvalidOperationException(
                "invalid ModelScope transformer configuration: config is nil");
        }

        _config = config;
        _baseUrl = config.BaseUrl.TrimEnd('/');

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
                "invalid ModelScope transformer configuration: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of the embedded OpenAI outbound's ApiFormat.</summary>
    public string ApiFormat => _inner.ApiFormat;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>The normalized base URL used for image task requests.</summary>
    public string BaseUrl => _baseUrl;

    /// <summary>Port of TransformRequest (outbound.go:68-87).</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("request is nil");
        }

        // Image generation and editing use the ModelScope async image task API.
        if (request.RequestType == RequestTypes.Image)
        {
            return BuildImageRequest(request);
        }

        // Create a shallow copy to avoid modifying the original request.
        var requestCopy = request.ShallowClone();
        requestCopy.Metadata = null; // ModelScope does not support metadata.

        var httpRequest = _inner.TransformRequest(requestCopy);
        _selectedApiKey = _inner.SelectedApiKey;

        return httpRequest;
    }

    /// <summary>Port of TransformResponse (image.go:157-169).</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null || response.Request is null)
        {
            return _inner.TransformResponse(response);
        }

        if (response.Request.ApiFormat != ApiFormats.ModelScopeImage)
        {
            return _inner.TransformResponse(response);
        }

        return TransformImageResponse(response);
    }

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
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
