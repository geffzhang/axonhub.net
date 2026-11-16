using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Xai;

/// <summary>Port of xai.Config (llm/transformer/xai/outbound.go:23-29).</summary>
public sealed class XaiOutboundConfig
{
    /// <summary>Custom base URL; empty falls back to <see cref="XaiOutboundTransformer.DefaultBaseUrl"/>.</summary>
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
/// Port of the xAI outbound transformer (llm/transformer/xai): the OpenAI
/// outbound with reasoning_content, a default API base URL, model-specific
/// field stripping, and a stream filter that drops empty delta events.
/// </summary>
public sealed class XaiOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    /// <summary>Go: xai.DefaultBaseURL.</summary>
    public const string DefaultBaseUrl = "https://api.x.ai/v1";

    private readonly XaiOutboundConfig _config;
    private readonly OpenAiOutboundTransformer _inner;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    /// <summary>Port of NewOutboundTransformerWithConfig.</summary>
    public XaiOutboundTransformer(XaiOutboundConfig config)
    {
        if (config is null)
        {
            throw new InvalidOperationException(
                "invalid xAI transformer configuration: config cannot be nil");
        }

        if (config.ApiKeyProvider is null)
        {
            throw new InvalidOperationException(
                "invalid xAI transformer configuration: API key provider is required");
        }

        if (config.BaseUrl.Length == 0)
        {
            // validateConfig mutates the caller's Config in the reference.
            config.BaseUrl = DefaultBaseUrl;
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
                "failed to create outbound transformer: " + ex.Message,
                ex);
        }
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

        if (string.IsNullOrEmpty(request.Model))
        {
            throw new InvalidOperationException("model is required");
        }

        if (request.Messages.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("messages are required");
        }

        // Grok models reject parameters they do not support outright.
        switch (request.Model)
        {
            case "grok-4":
                request.ReasoningEffort = null;
                request.PresencePenalty = null;
                request.FrequencyPenalty = null;
                request.Stop = null;
                break;
            case "grok-3":
            case "grok-3-mini":
                request.PresencePenalty = null;
                request.FrequencyPenalty = null;
                request.Stop = null;
                break;
            default:
                break;
        }

        var httpRequest = _inner.TransformRequest(request);
        _selectedApiKey = _inner.SelectedApiKey;
        return httpRequest;
    }

    /// <summary>Port of xai.IsValidResponse (outbound.go:129-185).</summary>
    public static bool IsValidResponse(LlmResponse response)
    {
        // Always allow the done response.
        if (response.Object == LlmResponse.DoneResponse.Object)
        {
            return true;
        }

        if (response.Choices.Count == 0)
        {
            return false;
        }

        var choice = response.Choices[0];
        if (choice.Delta is not { } delta)
        {
            return false;
        }

        var hasContent = false;

        if (delta.Content?.Content is { Length: > 0 })
        {
            hasContent = true;
        }

        if (delta.Content?.MultipleContent is { Count: > 0 })
        {
            hasContent = true;
        }

        if (delta.ToolCalls is { Count: > 0 })
        {
            hasContent = true;
        }

        if (!string.IsNullOrEmpty(delta.Role))
        {
            hasContent = true;
        }

        if (choice.FinishReason is not null)
        {
            hasContent = true;
        }

        if (!string.IsNullOrEmpty(delta.Refusal))
        {
            hasContent = true;
        }

        if (!string.IsNullOrEmpty(delta.ReasoningContent))
        {
            hasContent = true;
        }

        return hasContent;
    }

    /// <summary>Port of TransformStream: filter out xAI's empty delta events.</summary>
    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
        => new FilterStream<LlmResponse>(_inner.TransformStream(request, stream), IsValidResponse);

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
        => _inner.TransformResponse(response);

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public ResponseError TransformError(HttpClientError? error) => _inner.TransformError(error);

    /// <summary>Port of the embedded OpenAI outbound.</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => _inner.AggregateStreamChunks(request, chunks, out meta);
}
