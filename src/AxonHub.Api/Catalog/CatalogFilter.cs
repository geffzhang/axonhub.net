using System.Globalization;
using System.Text.Json;

namespace AxonHub.Api.Catalog;

/// <summary>
/// Port of <c>internal/server/biz/catalog_filter.go</c>: the AxonHub developer
/// allow-list, the per-developer provider merges and the release-date model
/// ordering applied to the upstream catalog.
/// </summary>
internal static class CatalogFilter
{
    /// <summary>Mirrors frontend/src/features/models/data/constants.ts.</summary>
    public static readonly string[] DefaultDeveloperIds =
    [
        "deepseek",
        "alibaba",
        "tencent",
        "zai",
        "openai",
        "moonshot",
        "anthropic",
        "google",
        "minimax",
        "kwaipilot",
        "xiaomi",
        "longcat",
        "mistral",
        "nvidia",
        "xai",
        "bytedance",
        "stepfun",
        "meta",
        "ibm",
        "poolside",
        "inclusionai",
        "thinkingmachines",
    ];

    public static CatalogFile FilterProviders(CatalogFile data, IReadOnlyList<string> allowedIds)
    {
        var allowed = new HashSet<string>(allowedIds, StringComparer.Ordinal);
        var filtered = new CatalogFile
        {
            Providers = new Dictionary<string, CatalogProvider>(StringComparer.Ordinal),
        };

        if (data.Providers is not null)
        {
            foreach (var (key, provider) in data.Providers)
            {
                if (allowed.Contains(provider.Id))
                {
                    filtered.Providers[key] = provider.Clone();
                }
            }
        }

        if (allowed.Contains("meta"))
        {
            MapLlamaToMeta(data, filtered);
        }

        if (allowed.Contains("ibm"))
        {
            AggregateIbmGranite(data, filtered);
        }

        if (allowed.Contains("bytedance"))
        {
            MapDoubaoToByteDance(data, filtered);
        }

        if (allowed.Contains("tencent"))
        {
            MergeTencentPlans(data, filtered);
        }

        if (allowed.Contains("xiaomi"))
        {
            MergeXiaomiTokenPlans(data, filtered);
        }

        if (allowed.Contains("kwaipilot"))
        {
            var provider = BuildKwaiPilotProvider(data);
            if (provider is not null)
            {
                filtered.Providers["kwaipilot"] = provider;
            }
        }

        if (allowed.Contains("nvidia"))
        {
            FilterNvidiaCreatedModels(filtered);
        }

        if (allowed.Contains("thinkingmachines"))
        {
            filtered.Providers.Remove("thinkingmachines");
        }

        return filtered;
    }

    public static string ModelString(CatalogValue? model, string key)
    {
        if (model is CatalogObject obj && obj.TryGet(key, out var value)
            && value is CatalogString text)
        {
            return text.Value;
        }

        return string.Empty;
    }

    private static CatalogValue CloneModel(CatalogValue model) => model.Clone();

    private static List<CatalogValue> ModelsFromIdMap(
        Dictionary<string, CatalogValue> merged)
    {
        var keys = merged.Keys.OrderBy(static key => key, StringComparer.Ordinal);
        return [.. keys.Select(key => merged[key])];
    }

    private static void MapLlamaToMeta(CatalogFile data, CatalogFile filtered)
    {
        var hasMeta = data.Providers is not null
            && data.Providers.TryGetValue("meta", out var upstreamMetaValue);
        var upstreamMeta = hasMeta ? upstreamMetaValue! : new CatalogProvider();
        var hasLlama = data.Providers is not null
            && data.Providers.TryGetValue("llama", out var llamaValue);
        var llama = hasLlama ? llamaValue! : new CatalogProvider();

        if (!hasMeta && !hasLlama)
        {
            return;
        }

        var modelsById = new Dictionary<string, CatalogValue>(StringComparer.Ordinal);
        foreach (var model in upstreamMeta.Models ?? [])
        {
            var id = ModelString(model, "id");
            if (id.Length == 0)
            {
                continue;
            }

            modelsById[id] = CloneModel(model);
        }

        foreach (var model in llama.Models ?? [])
        {
            var id = ModelString(model, "id").ToLowerInvariant();
            if (id.StartsWith("llama", StringComparison.Ordinal))
            {
                var originalId = ModelString(model, "id");
                if (!modelsById.ContainsKey(originalId))
                {
                    modelsById[originalId] = CloneModel(model);
                }
            }
        }

        if (modelsById.Count == 0)
        {
            return;
        }

        var provider = upstreamMeta.Clone();
        if (!hasMeta)
        {
            provider = llama.Clone();
        }

        provider.Id = "meta";
        provider.Name = "Meta";
        provider.DisplayName = "Meta";
        provider.Models = ModelsFromIdMap(modelsById);
        filtered.Providers!["meta"] = provider;
    }

    private static void AggregateIbmGranite(CatalogFile data, CatalogFile filtered)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var models = new List<CatalogValue>();

        if (data.Providers is not null)
        {
            foreach (var providerKey in data.Providers.Keys.OrderBy(
                         static key => key,
                         StringComparer.Ordinal))
            {
                foreach (var model in data.Providers[providerKey].Models ?? [])
                {
                    var id = ModelString(model, "id").ToLowerInvariant();
                    if (id.Length == 0)
                    {
                        continue;
                    }

                    if (seen.Contains(id))
                    {
                        continue;
                    }

                    if (!id.Contains("granite", StringComparison.Ordinal)
                        && !id.Contains("ibm", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (id.Contains("embedding", StringComparison.Ordinal)
                        || id.Contains("guardian", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (id.StartsWith("@cf/", StringComparison.Ordinal)
                        || id.Contains("workers-ai/", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    seen.Add(id);
                    models.Add(CloneModel(model));
                }
            }
        }

        if (models.Count == 0)
        {
            return;
        }

        filtered.Providers!["ibm"] = new CatalogProvider
        {
            Id = "ibm",
            Name = "IBM",
            DisplayName = "IBM",
            Models = models,
        };
    }

    private static void MapDoubaoToByteDance(CatalogFile data, CatalogFile filtered)
    {
        if (data.Providers is null
            || !data.Providers.TryGetValue("doubao", out var doubao))
        {
            return;
        }

        var models = new List<CatalogValue>();
        foreach (var model in doubao.Models ?? [])
        {
            var id = ModelString(model, "id").ToLowerInvariant();
            if (id.StartsWith("doubao", StringComparison.Ordinal))
            {
                models.Add(CloneModel(model));
            }
        }

        if (models.Count == 0)
        {
            return;
        }

        var provider = doubao.Clone();
        provider.Id = "bytedance";
        provider.Name = "ByteDance";
        provider.DisplayName = "ByteDance";
        provider.Models = models;
        filtered.Providers!["bytedance"] = provider;
    }

    private static void MergeTencentPlans(CatalogFile data, CatalogFile filtered)
    {
        var merged = new Dictionary<string, CatalogValue>(StringComparer.Ordinal);
        var baseProvider = filtered.Providers!.TryGetValue("tencent", out var filteredTencent)
            ? filteredTencent
            : null;
        if (baseProvider is null || baseProvider.Id.Length == 0)
        {
            baseProvider = data.Providers is not null
                && data.Providers.TryGetValue("tencent", out var upstreamTencent)
                ? upstreamTencent
                : new CatalogProvider();
        }

        void AddTencentModel(CatalogValue model, bool requireFamily)
        {
            var id = ModelString(model, "id").ToLowerInvariant();
            var normalized = id.StartsWith("tencent/", StringComparison.Ordinal)
                ? id["tencent/".Length..]
                : id;
            if (normalized.Length == 0)
            {
                return;
            }

            if (requireFamily
                && normalized != "hy3"
                && normalized != "hy4"
                && !normalized.StartsWith("hy3-", StringComparison.Ordinal)
                && !normalized.StartsWith("hy4-", StringComparison.Ordinal)
                && !normalized.StartsWith("hunyuan-", StringComparison.Ordinal))
            {
                return;
            }

            if (merged.ContainsKey(normalized))
            {
                return;
            }

            merged[normalized] = CloneModel(model);
        }

        foreach (var model in baseProvider.Models ?? [])
        {
            AddTencentModel(model, requireFamily: false);
        }

        foreach (var key in new[] { "tencent-token-plan", "tencent-tokenhub", "tencent-coding-plan" })
        {
            if (data.Providers?.TryGetValue(key, out var provider) != true)
            {
                continue;
            }

            foreach (var model in provider.Models ?? [])
            {
                AddTencentModel(model, requireFamily: true);
            }
        }

        if (merged.Count == 0)
        {
            return;
        }

        var result = baseProvider.Clone();
        result.Id = "tencent";
        result.Name = "Tencent";
        result.DisplayName = "Tencent";
        result.Models = ModelsFromIdMap(merged);
        filtered.Providers["tencent"] = result;
    }

    private static void MergeXiaomiTokenPlans(CatalogFile data, CatalogFile filtered)
    {
        var merged = new Dictionary<string, CatalogValue>(StringComparer.Ordinal);
        var baseProvider = filtered.Providers!.TryGetValue("xiaomi", out var filteredXiaomi)
            ? filteredXiaomi
            : null;
        if (baseProvider is null || baseProvider.Id.Length == 0)
        {
            baseProvider = data.Providers is not null
                && data.Providers.TryGetValue("xiaomi", out var upstreamXiaomi)
                ? upstreamXiaomi
                : new CatalogProvider();
        }

        foreach (var model in baseProvider.Models ?? [])
        {
            var id = ModelString(model, "id");
            if (id.Length == 0)
            {
                continue;
            }

            merged[id] = CloneModel(model);
        }

        foreach (var key in new[]
                 {
                     "xiaomi-token-plan-cn", "xiaomi-token-plan-sgp", "xiaomi-token-plan-ams",
                 })
        {
            if (data.Providers?.TryGetValue(key, out var provider) != true)
            {
                continue;
            }

            foreach (var model in provider.Models ?? [])
            {
                var id = ModelString(model, "id");
                if (id.Length == 0 || merged.ContainsKey(id))
                {
                    continue;
                }

                merged[id] = CloneModel(model);
            }
        }

        if (merged.Count == 0)
        {
            return;
        }

        var result = baseProvider.Clone();
        result.Id = "xiaomi";
        result.Name = "Xiaomi";
        result.DisplayName = "Xiaomi";
        result.Models = ModelsFromIdMap(merged);
        filtered.Providers["xiaomi"] = result;
    }

    private static string NormalizeKatSuffix(string modelId)
    {
        var trimmed = modelId.Trim();
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        var slash = trimmed.IndexOf('/');
        if (slash >= 0)
        {
            trimmed = trimmed[(slash + 1)..];
        }

        return trimmed.Trim().ToLowerInvariant();
    }

    private static bool IsKatFamilyModel(CatalogValue model)
    {
        var modelId = ModelString(model, "id");
        var family = ModelString(model, "family").ToLowerInvariant();
        if (family == "kat-coder")
        {
            return true;
        }

        var lowerId = modelId.ToLowerInvariant();
        if (lowerId.StartsWith("kwaipilot/", StringComparison.Ordinal)
            || lowerId.StartsWith("kuaishou/", StringComparison.Ordinal))
        {
            return true;
        }

        return lowerId.Contains("kat-coder", StringComparison.Ordinal)
            || lowerId.Contains("kat-dev", StringComparison.Ordinal);
    }

    private static CatalogProvider? BuildKwaiPilotProvider(CatalogFile data)
    {
        var byId = new Dictionary<string, CatalogObject>(StringComparer.Ordinal);

        if (data.Providers is not null)
        {
            foreach (var providerKey in data.Providers.Keys.OrderBy(
                         static key => key,
                         StringComparer.Ordinal))
            {
                foreach (var model in data.Providers[providerKey].Models ?? [])
                {
                    if (!IsKatFamilyModel(model))
                    {
                        continue;
                    }

                    var normalizedId = NormalizeKatSuffix(ModelString(model, "id"));
                    if (normalizedId.Length == 0)
                    {
                        continue;
                    }

                    if (model is not CatalogObject cloned)
                    {
                        continue;
                    }

                    cloned = (CatalogObject)cloned.Clone();
                    cloned.Set("id", new CatalogString(normalizedId));
                    if (ModelString(cloned, "family").Length == 0)
                    {
                        cloned.Set("family", new CatalogString("kat-coder"));
                    }

                    if (byId.TryGetValue(normalizedId, out var existing))
                    {
                        MergeDefined(existing, cloned);
                        continue;
                    }

                    byId[normalizedId] = cloned;
                }
            }
        }

        if (byId.Count == 0)
        {
            return null;
        }

        return new CatalogProvider
        {
            Id = "kwaipilot",
            Name = "KwaiPilot",
            DisplayName = "KwaiPilot",
            Models = [.. byId.Keys.OrderBy(static key => key, StringComparer.Ordinal)
                .Select(key => (CatalogValue)byId[key])],
        };
    }

    private static void FilterNvidiaCreatedModels(CatalogFile filtered)
    {
        if (filtered.Providers is null
            || !filtered.Providers.TryGetValue("nvidia", out var provider))
        {
            return;
        }

        var models = new List<CatalogValue>();
        foreach (var model in provider.Models ?? [])
        {
            if (ModelString(model, "id").ToLowerInvariant()
                .StartsWith("nvidia/", StringComparison.Ordinal))
            {
                models.Add(model);
            }
        }

        if (models.Count == 0)
        {
            return;
        }

        provider.Models = models;
        filtered.Providers["nvidia"] = provider;
    }

    private static void MergeDefined(CatalogObject target, CatalogObject source)
    {
        foreach (var (key, value) in source.Members)
        {
            if (value is CatalogNull)
            {
                continue;
            }

            var existing = target.Get(key);
            if (existing is null or CatalogNull
                || (existing is CatalogString text && text.Value.Length == 0))
            {
                target.Set(key, value);
                continue;
            }

            if (existing is CatalogObject destination && value is CatalogObject sourceObject)
            {
                MergeDefined(destination, sourceObject);
            }
        }
    }

    /// <summary>
    /// Port of <c>mergeExtraModels</c>. A top-level decode failure propagates;
    /// each per-key value that parses as neither a model list nor a provider is
    /// skipped, exactly like the reference's <c>continue</c>.
    /// </summary>
    public static void MergeExtraModels(CatalogFile data, ReadOnlySpan<byte> extra)
    {
        if (extra.Length == 0)
        {
            return;
        }

        using (JsonDocument.Parse(extra.ToArray()))
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
            {
                return;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException(
                    "json: cannot unmarshal " + root.ValueKind.ToString().ToLowerInvariant()
                    + " into Go value of type map[string]json.RawMessage");
            }

            data.Providers ??= new Dictionary<string, CatalogProvider>(StringComparer.Ordinal);

            foreach (var property in root.EnumerateObject())
            {
                var key = property.Name;
                var value = property.Value;

                // json.Unmarshal into []map[string]any: null means a nil slice.
                if (value.ValueKind == JsonValueKind.Null)
                {
                    AppendExtraModels(data, key, []);
                    continue;
                }

                if (value.ValueKind == JsonValueKind.Array)
                {
                    List<CatalogValue> models;
                    try
                    {
                        models = CatalogParser.ParseModels(value);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    AppendExtraModels(data, key, models);
                    continue;
                }

                CatalogProvider provider;
                try
                {
                    provider = CatalogParser.ParseProvider(value);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (provider.Id.Length == 0)
                {
                    provider.Id = key;
                }

                if (!data.Providers.TryGetValue(key, out var existing))
                {
                    data.Providers[key] = provider;
                    continue;
                }

                var existingIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var model in existing.Models ?? [])
                {
                    existingIds.Add(ModelString(model, "id"));
                }

                foreach (var model in provider.Models ?? [])
                {
                    var id = ModelString(model, "id");
                    if (id.Length == 0 || existingIds.Contains(id))
                    {
                        continue;
                    }

                    existing.Models ??= [];
                    existing.Models.Add(model);
                    existingIds.Add(id);
                }

                data.Providers[key] = existing;
            }
        }
    }

    private static void AppendExtraModels(
        CatalogFile data,
        string key,
        List<CatalogValue> models)
    {
        if (data.Providers?.TryGetValue(key, out var existing) != true)
        {
            data.Providers![key] = new CatalogProvider { Id = key, Models = models };
            return;
        }

        var existingIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in existing.Models ?? [])
        {
            existingIds.Add(ModelString(model, "id"));
        }

        foreach (var model in models)
        {
            var id = ModelString(model, "id");
            if (id.Length == 0 || existingIds.Contains(id))
            {
                continue;
            }

            existing.Models ??= [];
            existing.Models.Add(model);
            existingIds.Add(id);
        }

        data.Providers[key] = existing;
    }

    public static void SortCatalogModels(CatalogFile data)
    {
        if (data.Providers is null)
        {
            return;
        }

        foreach (var key in data.Providers.Keys.ToList())
        {
            var provider = data.Providers[key];
            if (provider.Models is { Count: > 1 })
            {
                provider.Models = SortModelsByReleaseDate(provider.Models);
            }
        }
    }

    public static List<CatalogValue> SortModelsByReleaseDate(List<CatalogValue> models)
        // sort.SliceStable: LINQ OrderBy is stable.
        => [.. models.OrderBy(static model => model, ReleaseDateComparer.Instance)];

    private sealed class ReleaseDateComparer : IComparer<CatalogValue>
    {
        public static ReleaseDateComparer Instance { get; } = new();

        public int Compare(CatalogValue? left, CatalogValue? right)
        {
            var leftDate = ParseReleaseDate(left);
            var rightDate = ParseReleaseDate(right);
            if (leftDate != rightDate)
            {
                // Newer releases first.
                return rightDate.CompareTo(leftDate);
            }

            return string.CompareOrdinal(
                ModelString(left, "id"),
                ModelString(right, "id"));
        }

        private static DateTime ParseReleaseDate(CatalogValue? model)
        {
            var text = ModelString(model, "release_date");
            return DateTime.TryParseExact(
                text,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed)
                ? parsed
                : DateTime.MinValue;
        }
    }
}
