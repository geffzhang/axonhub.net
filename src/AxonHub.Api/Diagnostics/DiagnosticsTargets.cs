using HotChocolate;

namespace AxonHub.Api.Diagnostics;

/// <summary>Port of the <c>DiagnosticsTarget</c> enum (system.graphql:459-461).</summary>
[GraphQLName("DiagnosticsTarget")]
public enum DiagnosticsTargetValue
{
    [GraphQLName("CHANNEL_CACHE")]
    ChannelCache,
}

/// <summary>Target normalization shared by getCacheDiagnostics and clearCache.</summary>
public static class DiagnosticsTargets
{
    /// <summary>The single wire value declared by the reference SDL.</summary>
    public const string ChannelCacheWire = "CHANNEL_CACHE";

    /// <summary>
    /// Port of <c>normalizeDiagnosticsTargets</c>
    /// (channel_model_cache_diagnostics.go:125-134): an empty list means the
    /// channel cache, duplicates collapse in first-seen order, and the result is
    /// sorted by the enum's numeric value (declaration order in the SDL).
    /// </summary>
    public static IReadOnlyList<DiagnosticsTargetValue> Normalize(
        IReadOnlyList<DiagnosticsTargetValue>? targets)
    {
        if (targets is null || targets.Count == 0)
        {
            return [DiagnosticsTargetValue.ChannelCache];
        }

        var seen = new HashSet<DiagnosticsTargetValue>();
        var unique = new List<DiagnosticsTargetValue>(targets.Count);
        foreach (var target in targets)
        {
            if (seen.Add(target))
            {
                unique.Add(target);
            }
        }

        // slices.Sort orders by the enum's integer value.
        unique.Sort(static (left, right) => ((int)left).CompareTo((int)right));
        return unique;
    }

    /// <summary>Go's <c>%s</c> rendering of a target: the GraphQL wire value.</summary>
    public static string ToWire(DiagnosticsTargetValue target) => target switch
    {
        DiagnosticsTargetValue.ChannelCache => ChannelCacheWire,
        _ => target.ToString(),
    };
}
