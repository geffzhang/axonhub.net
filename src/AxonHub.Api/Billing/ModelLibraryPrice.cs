namespace AxonHub.Api.Billing;

/// <summary>
/// Port of the model-library billing fallback helpers in
/// internal/server/biz/channel_price.go: the synthetic usage-log reference id
/// and the exact-then-base model id candidates.
/// </summary>
internal static class ModelLibraryPrice
{
    /// <summary>Marks cost that fell back to the model library price.</summary>
    internal const string ReferencePrefix = "model_library:";

    /// <summary>
    /// Effort/tier suffixes stripped when the exact model library id has no
    /// price. Keep in sync with the provider naming conventions (for example
    /// Antigravity "gemini-3-flash" / "-low" / "-medium" / "-high").
    /// </summary>
    private static readonly string[] Suffixes = ["-high", "-medium", "-low"];

    /// <summary>Builds the synthetic reference id recorded on usage logs.</summary>
    internal static string ReferenceId(string modelId) => ReferencePrefix + modelId;

    /// <summary>
    /// Model ids to try in order. The exact id comes first, then the base model
    /// with a known effort/tier suffix stripped; duplicates are removed.
    /// </summary>
    internal static IReadOnlyList<string> Candidates(string modelId)
    {
        ArgumentNullException.ThrowIfNull(modelId);

        var candidates = new List<string>(Suffixes.Length + 1) { modelId };
        foreach (var suffix in Suffixes)
        {
            if (modelId.Length > suffix.Length
                && modelId.EndsWith(suffix, StringComparison.Ordinal))
            {
                candidates.Add(modelId[..^suffix.Length]);
            }
        }

        return candidates.Distinct(StringComparer.Ordinal).ToList();
    }
}
