using System.Text.RegularExpressions;

namespace AxonHub.Llm.Transformer.Antigravity;

/// <summary>Port of antigravity quota routing (llm/transformer/antigravity/router.go).</summary>
public static class AntigravityRouter
{
    public const string QuotaAntigravity = "antigravity";
    public const string QuotaGeminiCLI = "gemini-cli";

    /// <summary>Matches an explicit quota suffix like :antigravity or :gemini-cli.</summary>
    private static readonly Regex SuffixRegex = new(":(antigravity|gemini-cli)$");

    /// <summary>Matches an explicit antigravity- prefix.</summary>
    private static readonly Regex PrefixRegex = new("^antigravity-(.+)");

    /// <summary>
    /// Port of DetermineQuotaPreference: analyzes the model name to determine
    /// which quota pool to use. Priority:
    /// 1. explicit suffix (:antigravity or :gemini-cli)
    /// 2. explicit antigravity- prefix
    /// 3. model-specific rules (claude*, gpt*, image models, legacy gemini-3)
    /// 4. default to gemini-cli for standard Gemini models.
    /// </summary>
    public static string DetermineQuotaPreference(string modelName)
    {
        // Normalize to lowercase for case-insensitive matching.
        var lowerModelName = modelName.ToLowerInvariant();

        // Step 1: explicit suffix.
        var suffixMatch = SuffixRegex.Match(lowerModelName);
        if (suffixMatch.Success)
        {
            return suffixMatch.Groups[1].Value == "antigravity"
                ? QuotaAntigravity
                : QuotaGeminiCLI;
        }

        // Step 2: explicit antigravity- prefix.
        if (PrefixRegex.IsMatch(lowerModelName))
        {
            return QuotaAntigravity;
        }

        // Step 3: Antigravity-only models. Claude and GPT-OSS only exist on Antigravity.
        if (lowerModelName.StartsWith("claude", StringComparison.Ordinal)
            || lowerModelName.StartsWith("gpt", StringComparison.Ordinal))
        {
            return QuotaAntigravity;
        }

        // Image generation models require Antigravity.
        if (lowerModelName.Contains("image", StringComparison.Ordinal)
            || lowerModelName.Contains("imagen", StringComparison.Ordinal))
        {
            return QuotaAntigravity;
        }

        // Legacy Gemini 3 model names (backward compatibility; the old naming
        // convention before the -preview suffix was added).
        string[] legacyGemini3Patterns =
        [
            "gemini-3-pro-low",
            "gemini-3-pro-high",
            "gemini-3-pro-medium",
            "gemini-3-flash",
            "gemini-3-flash-low",
            "gemini-3-flash-medium",
            "gemini-3-flash-high",
        ];
        if (legacyGemini3Patterns.Contains(lowerModelName))
        {
            return QuotaAntigravity;
        }

        // Step 4: standard Gemini models use the Gemini CLI quota
        // (gemini-2.5-*, gemini-1.5-*, gemini-3-*-preview, etc.).
        return QuotaGeminiCLI;
    }

    /// <summary>
    /// Port of GetInitialEndpoint: both quota pools always start with Daily
    /// sandbox to match reference implementation behavior.
    /// </summary>
    public static string GetInitialEndpoint(string quotaPreference)
    {
        _ = quotaPreference;

        return AntigravityConstants.EndpointDaily;
    }

    /// <summary>
    /// Port of GetFallbackEndpoints: regardless of the initial endpoint, all
    /// three endpoints are tried in order for maximum reliability.
    /// </summary>
    public static List<string> GetFallbackEndpoints() =>
    [
        AntigravityConstants.EndpointDaily,
        AntigravityConstants.EndpointAutopush,
        AntigravityConstants.EndpointProd,
    ];

    /// <summary>
    /// Port of StripModelSuffix: removes the quota preference suffix, e.g.
    /// "gemini-2.5-pro:antigravity" -&gt; "gemini-2.5-pro".
    /// </summary>
    public static string StripModelSuffix(string modelName)
        => SuffixRegex.Replace(modelName, "");

    /// <summary>
    /// Port of StripAntigravityPrefix: removes the "antigravity-" prefix
    /// (case-sensitive), e.g. "antigravity-gemini-2.5-pro" -&gt; "gemini-2.5-pro".
    /// </summary>
    public static string StripAntigravityPrefix(string modelName)
    {
        var match = PrefixRegex.Match(modelName);
        return match.Success ? match.Groups[1].Value : modelName;
    }

    /// <summary>Port of NormalizeModelName: strips suffix first, then prefix.</summary>
    public static string NormalizeModelName(string modelName)
        => StripAntigravityPrefix(StripModelSuffix(modelName));

    /// <summary>
    /// Port of ShouldRetryWithDifferentEndpoint: retry on 429 rate limits,
    /// 403/404 config issues, and 5xx server errors.
    /// </summary>
    public static bool ShouldRetryWithDifferentEndpoint(int statusCode)
        => statusCode == 429 || statusCode == 403 || statusCode == 404
            || (statusCode >= 500 && statusCode < 600);

    /// <summary>
    /// Port of transformModelForAntigravity: the Antigravity API requires tier
    /// suffixes for gemini-3-pro models (e.g. gemini-3-pro-low), so a bare
    /// gemini-3-pro gets the default -low tier appended.
    /// </summary>
    public static string TransformModelForAntigravity(string modelName)
    {
        // First normalize to remove any existing prefixes/suffixes.
        var normalized = NormalizeModelName(modelName);
        var lowerModel = normalized.ToLowerInvariant();

        // Check if this is gemini-3-pro without a tier suffix.
        if (lowerModel.StartsWith("gemini-3-pro", StringComparison.Ordinal))
        {
            var hasTierSuffix = lowerModel.EndsWith("-low", StringComparison.Ordinal)
                || lowerModel.EndsWith("-medium", StringComparison.Ordinal)
                || lowerModel.EndsWith("-high", StringComparison.Ordinal);

            if (!hasTierSuffix)
            {
                return normalized + "-low";
            }
        }

        return normalized;
    }
}
