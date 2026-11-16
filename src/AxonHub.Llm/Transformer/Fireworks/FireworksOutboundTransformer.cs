using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Fireworks;

/// <summary>Port of fireworks.Config (llm/transformer/fireworks/outbound.go:16-23).</summary>
public sealed class FireworksOutboundConfig
{
    /// <summary>Custom base URL; empty selects <see cref="FireworksOutboundTransformer.DefaultBaseUrl"/>.</summary>
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
/// Port of the Fireworks outbound factory (llm/transformer/fireworks/outbound.go).
/// The provider is OpenAI-compatible; the reference only supplies a default
/// base URL, trims one trailing slash and pins the reasoning field to
/// <c>reasoning_content</c>, then returns the plain OpenAI outbound.
/// </summary>
/// <remarks>
/// The comment on <c>openai.ReasoningFieldNone</c> names Fireworks, but the
/// Fireworks factory actually passes <c>ReasoningFieldContent</c>; the port
/// follows the code (C098).
/// </remarks>
public static class FireworksOutboundTransformer
{
    /// <summary>Port of fireworks.DefaultBaseURL.</summary>
    public const string DefaultBaseUrl = "https://api.fireworks.ai/inference/v1";

    /// <summary>Port of NewOutboundTransformer(baseURL, apiKey).</summary>
    public static OpenAiOutboundTransformer Create(string baseUrl, string apiKey)
        => Create(new FireworksOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = new StaticApiKeyProvider(apiKey),
        });

    /// <summary>Port of NewOutboundTransformerWithConfig.</summary>
    public static OpenAiOutboundTransformer Create(FireworksOutboundConfig config)
    {
        if (config is null)
        {
            throw new InvalidOperationException(
                "invalid Fireworks transformer configuration: config is nil");
        }

        if (config.ApiKeyProvider is null)
        {
            throw new InvalidOperationException(
                "invalid Fireworks transformer configuration: API key provider is required");
        }

        var baseUrl = config.BaseUrl;
        if (baseUrl.Length == 0)
        {
            baseUrl = DefaultBaseUrl;
        }

        // strings.TrimSuffix(baseURL, "/"): exactly one trailing slash.
        baseUrl = baseUrl.EndsWith('/') ? baseUrl[..^1] : baseUrl;

        return new OpenAiOutboundTransformer(new OpenAiOutboundConfig
        {
            PlatformType = OpenAiPlatformType.OpenAi,
            BaseUrl = baseUrl,
            ApiKeyProvider = config.ApiKeyProvider,
            ReasoningField = ReasoningField.Content,
            ChannelApiKeyIndex = config.ChannelApiKeyIndex,
        });
    }
}
