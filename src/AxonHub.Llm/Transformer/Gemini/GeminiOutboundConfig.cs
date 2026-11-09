using AxonHub.Llm.Transformer.OpenAI; // IApiKeyProvider seam (llm/auth) lives in the OpenAI transformer file

namespace AxonHub.Llm.Transformer.Gemini;

/// <summary>Port of gemini.DefaultBaseURL / DefaultAPIVersion / PlatformVertex (outbound.go:16-25).</summary>
public static class GeminiConstants
{
    /// <summary>The default base URL for the Gemini API.</summary>
    public const string DefaultBaseURL = "https://generativelanguage.googleapis.com";

    /// <summary>The default API version.</summary>
    public const string DefaultAPIVersion = "v1beta";

    /// <summary>Vertex AI platform marker.</summary>
    public const string PlatformVertex = "vertex";
}

/// <summary>Port of gemini.Config (llm/transformer/gemini/outbound.go:28-48).</summary>
public sealed class GeminiOutboundConfig
{
    /// <summary>The base URL for the Gemini API.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Provides API keys for authentication.</summary>
    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>
    /// Port-only audit field: 1-based position of the key the provider serves in
    /// the channel's full key list, or null when the channel has a single key
    /// (request_executions.channel_api_key_index).
    /// </summary>
    public int? ChannelApiKeyIndex { get; set; }

    /// <summary>An optional custom path override for this endpoint. Must start with "/".</summary>
    public string? EndpointPath { get; set; }

    /// <summary>The API version to use.</summary>
    public string? ApiVersion { get; set; }

    /// <summary>Distinguishes different platform configurations (e.g., "vertex").</summary>
    public string? PlatformType { get; set; }

    /// <summary>Maps reasoning effort levels to thinking budget tokens.</summary>
    public Dictionary<string, long>? ReasoningEffortToBudget { get; set; }
}

/// <summary>
/// Port of gemini.clenupConfig (outbound.go:65-87): fills the base URL and API
/// version defaults and strips a trailing /v1beta or /v1 from the base URL.
/// </summary>
public static class GeminiOutboundConfigExtensions
{
    public static GeminiOutboundConfig Cleanup(this GeminiOutboundConfig config)
    {
        var cleaned = new GeminiOutboundConfig
        {
            BaseUrl = config.BaseUrl,
            ApiKeyProvider = config.ApiKeyProvider,
            ChannelApiKeyIndex = config.ChannelApiKeyIndex,
            EndpointPath = config.EndpointPath,
            ApiVersion = config.ApiVersion,
            PlatformType = config.PlatformType,
            ReasoningEffortToBudget = config.ReasoningEffortToBudget,
        };

        if (cleaned.BaseUrl.Length == 0)
        {
            // Go: strings.TrimSuffix(DefaultBaseURL, "/") removes at most one.
            cleaned.BaseUrl = GeminiConstants.DefaultBaseURL.EndsWith('/')
                ? GeminiConstants.DefaultBaseURL[..^1]
                : GeminiConstants.DefaultBaseURL;
        }

        if (cleaned.ApiVersion is null or "")
        {
            cleaned.ApiVersion = GeminiConstants.DefaultAPIVersion;

            if (cleaned.BaseUrl.EndsWith("/v1beta", StringComparison.Ordinal))
            {
                cleaned.ApiVersion = "v1beta";
                cleaned.BaseUrl = cleaned.BaseUrl[..^"/v1beta".Length];
            }

            if (cleaned.BaseUrl.EndsWith("/v1", StringComparison.Ordinal))
            {
                cleaned.ApiVersion = "v1";
                cleaned.BaseUrl = cleaned.BaseUrl[..^"/v1".Length];
            }
        }
        else
        {
            // Go: strings.TrimSuffix keeps a trailing slash untouched.
            var versionSuffix = "/" + cleaned.ApiVersion;
            if (cleaned.BaseUrl.EndsWith(versionSuffix, StringComparison.Ordinal))
            {
                cleaned.BaseUrl = cleaned.BaseUrl[..^versionSuffix.Length];
            }
        }

        return cleaned;
    }
}
