using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AxonHub.Abstractions;
using AxonHub.Api.Models;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>Port of resolvedAssociationCandidate (candidates.go:50-58).</summary>
internal sealed record ResolvedAssociationCandidate(
    ChannelRecord Channel,
    int Priority,
    IReadOnlyList<ChannelModelEntry> Models,
    ModelAssociationWhen? When);

/// <summary>
/// The association-resolution validity tuple
/// (candidates.go:276-290). Every member must match for a cache hit.
/// </summary>
internal sealed record AssociationCacheKey(
    string AssociationSignature,
    int ChannelCount,
    DateTime LatestChannelUpdateTime,
    DateTime LatestModelUpdateTime,
    long ChannelCacheVersion);

/// <summary>Port of orchestrator.AssociationCacheEntrySnapshot (diagnostics.go:12-21).</summary>
public sealed record AssociationCacheEntrySnapshot(
    string ModelId,
    IReadOnlyList<ModelAssociation> Associations,
    int CandidateCount,
    int ChannelCount,
    DateTimeOffset LatestChannelUpdateTime,
    DateTimeOffset LatestModelUpdateTime,
    long ChannelCacheVersion,
    DateTimeOffset CachedAt);

/// <summary>
/// Process-wide association resolution cache
/// (<c>DefaultSelector.associationCache</c>, candidates.go:90-102), shared by
/// every scoped selector instance and read by the cache diagnostics export
/// (<c>CandidateSelectorDiagnostics.ReadAssociationCache</c>, diagnostics.go:29-56).
/// </summary>
public sealed class ModelAssociationCache
{
    /// <summary>
    /// After five minutes an entry is invalid even when nothing changed
    /// (<c>associationCacheTTL</c>, candidates.go:91-95).
    /// </summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    internal bool TryGet(
        string modelId,
        AssociationCacheKey key,
        DateTimeOffset now,
        out IReadOnlyList<ResolvedAssociationCandidate> candidates)
    {
        lock (_sync)
        {
            if (_entries.TryGetValue(modelId, out var entry)
                && entry.Key == key
                && now - entry.CachedAt < Ttl)
            {
                candidates = entry.Candidates;
                return true;
            }
        }

        candidates = [];
        return false;
    }

    internal void Set(
        string modelId,
        AssociationCacheKey key,
        IReadOnlyList<ModelAssociation> associations,
        IReadOnlyList<ResolvedAssociationCandidate> candidates,
        DateTimeOffset cachedAt)
    {
        lock (_sync)
        {
            _entries[modelId] = new Entry(key, [.. associations], candidates, cachedAt);
        }
    }

    /// <summary>
    /// Port of ReadAssociationCache: entries sorted by model id, each carrying
    /// the original associations and the resolution bookkeeping.
    /// </summary>
    public IReadOnlyList<AssociationCacheEntrySnapshot> ReadAssociationCache()
    {
        lock (_sync)
        {
            var modelIds = _entries.Keys.ToArray();
            Array.Sort(modelIds, StringComparer.Ordinal);

            var result = new List<AssociationCacheEntrySnapshot>(modelIds.Length);
            foreach (var modelId in modelIds)
            {
                var entry = _entries[modelId];
                result.Add(new AssociationCacheEntrySnapshot(
                    modelId,
                    [.. entry.Associations],
                    entry.Candidates.Count,
                    entry.Key.ChannelCount,
                    ToOffset(entry.Key.LatestChannelUpdateTime),
                    ToOffset(entry.Key.LatestModelUpdateTime),
                    entry.Key.ChannelCacheVersion,
                    entry.CachedAt));
            }

            return result;
        }
    }

    /// <summary>
    /// The reference's <c>time.Time.Equal</c> compares instants; normalizing
    /// removes the DateTimeKind difference between PostgreSQL rows and the
    /// zero value used by test doubles.
    /// </summary>
    internal static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static DateTimeOffset ToOffset(DateTime value)
        => new(ToUtc(value), TimeSpan.Zero);

    private sealed record Entry(
        AssociationCacheKey Key,
        IReadOnlyList<ModelAssociation> Associations,
        IReadOnlyList<ResolvedAssociationCandidate> Candidates,
        DateTimeOffset CachedAt);
}

/// <summary>
/// Port of modelAssociationSignature (candidates.go:397-541): an FNV-1a 64
/// signature over the association document, written with the same NUL-separated
/// field encoding so equal documents hash equally across requests.
/// </summary>
internal static class ModelAssociationSignature
{
    private const ulong FnvOffsetBasis = 14695981039346656037;
    private const ulong FnvPrime = 1099511628211;

    public static string Compute(IReadOnlyList<ModelAssociation> associations)
    {
        var hash = FnvOffsetBasis;
        hash = WriteInt(hash, associations.Count);
        foreach (var association in associations)
        {
            hash = WriteAssociation(hash, association);
        }

        return hash.ToString("x", CultureInfo.InvariantCulture);
    }

    private static ulong WriteAssociation(ulong hash, ModelAssociation? association)
    {
        if (association is null)
        {
            return WriteString(hash, "<nil>");
        }

        hash = WriteString(hash, association.Type);
        hash = WriteInt(hash, association.Priority);
        hash = WriteBool(hash, association.Disabled);
        hash = WriteWhen(hash, association.When);

        if (association.ChannelModel is not null)
        {
            hash = WriteString(hash, "channelModel");
            hash = WriteInt(hash, association.ChannelModel.ChannelId);
            hash = WriteString(hash, association.ChannelModel.ModelId);
        }

        if (association.ChannelRegex is not null)
        {
            hash = WriteString(hash, "channelRegex");
            hash = WriteInt(hash, association.ChannelRegex.ChannelId);
            hash = WriteString(hash, association.ChannelRegex.Pattern);
        }

        if (association.Regex is not null)
        {
            hash = WriteString(hash, "regex");
            hash = WriteString(hash, association.Regex.Pattern);
            hash = WriteExcludes(hash, association.Regex.Exclude);
        }

        if (association.ModelId is not null)
        {
            hash = WriteString(hash, "modelId");
            hash = WriteString(hash, association.ModelId.ModelId);
            hash = WriteExcludes(hash, association.ModelId.Exclude);
        }

        if (association.ChannelTagsModel is not null)
        {
            hash = WriteString(hash, "channelTagsModel");
            hash = WriteStrings(hash, association.ChannelTagsModel.ChannelTags);
            hash = WriteString(hash, association.ChannelTagsModel.ModelId);
        }

        if (association.ChannelTagsRegex is not null)
        {
            hash = WriteString(hash, "channelTagsRegex");
            hash = WriteStrings(hash, association.ChannelTagsRegex.ChannelTags);
            hash = WriteString(hash, association.ChannelTagsRegex.Pattern);
        }

        return hash;
    }

    private static ulong WriteWhen(ulong hash, ModelAssociationWhen? when)
    {
        if (when is null)
        {
            return WriteString(hash, "when:nil");
        }

        hash = WriteString(hash, "when");
        hash = WriteBool(hash, when.Enabled);
        return WriteCondition(hash, when.Condition);
    }

    private static ulong WriteCondition(ulong hash, FilterCondition? condition)
    {
        if (condition is null)
        {
            return WriteString(hash, "condition:nil");
        }

        hash = WriteString(hash, condition.Type);
        hash = WriteString(hash, condition.Logic);
        hash = WriteString(hash, condition.Field);
        hash = WriteString(hash, condition.Operator);
        hash = WriteString(hash, FormatConditionValue(condition.Value));
        hash = WriteInt(hash, condition.Conditions?.Count ?? 0);
        if (condition.Conditions is not null)
        {
            foreach (var child in condition.Conditions)
            {
                hash = WriteCondition(hash, child);
            }
        }

        return hash;
    }

    private static ulong WriteExcludes(ulong hash, IReadOnlyList<ExcludeAssociation?>? excludes)
    {
        hash = WriteInt(hash, excludes?.Count ?? 0);
        if (excludes is null)
        {
            return hash;
        }

        foreach (var exclude in excludes)
        {
            if (exclude is null)
            {
                hash = WriteString(hash, "<nil>");
                continue;
            }

            hash = WriteString(hash, exclude.ChannelNamePattern);
            hash = WriteInts(hash, exclude.ChannelIds);
            hash = WriteStrings(hash, exclude.ChannelTags);
        }

        return hash;
    }

    private static ulong WriteInts(ulong hash, IReadOnlyList<int>? values)
    {
        hash = WriteInt(hash, values?.Count ?? 0);
        if (values is not null)
        {
            foreach (var value in values)
            {
                hash = WriteInt(hash, value);
            }
        }

        return hash;
    }

    private static ulong WriteStrings(ulong hash, IReadOnlyList<string>? values)
    {
        hash = WriteInt(hash, values?.Count ?? 0);
        if (values is not null)
        {
            foreach (var value in values)
            {
                hash = WriteString(hash, value);
            }
        }

        return hash;
    }

    private static ulong WriteInt(ulong hash, int value)
        => WriteString(hash, value.ToString(CultureInfo.InvariantCulture));

    private static ulong WriteBool(ulong hash, bool value)
        => WriteString(hash, value ? "1" : "0");

    private static ulong WriteString(ulong hash, string value)
    {
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        hash ^= 0;
        hash *= FnvPrime;
        return hash;
    }

    /// <summary>
    /// Approximation of Go's <c>fmt.Sprintf("%T:%v", value, value)</c> over the
    /// decoded <c>any</c>. The signature is internal to the cache, so exact
    /// float formatting only affects whether an equivalent document misses.
    /// </summary>
    private static string FormatConditionValue(JsonNode? value)
    {
        if (value is null)
        {
            return "<nil>:<nil>";
        }

        return value.GetValueKind() switch
        {
            JsonValueKind.String => "string:" + value.GetValue<string>(),
            JsonValueKind.True => "bool:true",
            JsonValueKind.False => "bool:false",
            JsonValueKind.Number => "float64:" + value.ToJsonString(),
            JsonValueKind.Array => "[]interface {}:" + value.ToJsonString(),
            JsonValueKind.Object => "map[string]interface {}:" + value.ToJsonString(),
            _ => "json:" + value.ToJsonString(),
        };
    }
}
