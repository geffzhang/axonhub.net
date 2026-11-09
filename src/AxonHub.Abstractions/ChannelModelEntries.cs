using System.Text.Json;

namespace AxonHub.Abstractions;

/// <summary>One request-model → upstream-model mapping exposed by a channel.</summary>
public sealed record ChannelModelEntry(string RequestModel, string ActualModel, string Source);

/// <summary>
/// The subset of <c>objects.ChannelSettings</c> the cache diagnostics export
/// re-serializes (channel_model_cache_diagnostics.go:251-269).
/// </summary>
public sealed record ChannelSettingsSnapshot(
    string ExtraModelPrefix,
    IReadOnlyList<string>? AutoTrimedModelPrefixes,
    bool HideMappedModels,
    bool HideOriginalModels,
    bool LowercaseModelId,
    IReadOnlyList<ModelMappingSnapshot>? ModelMappings);

/// <summary>Port of <c>objects.ModelMapping</c> for the diagnostics snapshot.</summary>
public sealed record ModelMappingSnapshot(string From, string To);

/// <summary>
/// Faithful port of <c>biz.Channel.GetModelEntries</c>
/// (internal/server/biz/channel_llm.go): direct supported models plus prefix,
/// auto-trim and model-mapping aliases, then the hide/lowercase post-processing.
/// Shared by the load balancer (channel selection) and the models API
/// (<c>ModelService.ListEnabledModels</c> derives its model list from the same
/// entries).
/// </summary>
public static class ChannelModelEntries
{
    private const string SourceDirect = "direct";
    private const string SourcePrefix = "prefix";
    private const string SourceAutoTrim = "auto_trim";
    private const string SourceMapping = "mapping";

    private static readonly Dictionary<string, int> SourcePriority = new(StringComparer.Ordinal)
    {
        [SourceDirect] = 4,
        [SourceAutoTrim] = 3,
        [SourceMapping] = 2,
        [SourcePrefix] = 1,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Port of <c>biz.Channel.ChooseModel</c> (channel_llm.go:95-104): resolves a
    /// request model to the upstream model this channel would send, or null when
    /// the channel does not support it. LowercaseModelID lowercases the lookup
    /// key, matching the lowercased entry keys <see cref="Build"/> produces.
    /// </summary>
    public static string? ChooseModel(ChannelDescriptor channel, string model)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(model);

        var settings = ParseSettings(channel);
        var key = settings?.LowercaseModelId == true ? model.ToLowerInvariant() : model;

        return Build(channel).TryGetValue(key, out var entry) ? entry.ActualModel : null;
    }

    /// <summary>
    /// Port of <c>biz.Channel.GetDirectModelEntries</c>
    /// (channel_llm.go:1614-1633): the raw supported models, ignoring every
    /// settings-driven alias and the HideOriginalModels filter. The channel
    /// tester reads this when a model is not a request-facing entry.
    /// </summary>
    public static Dictionary<string, ChannelModelEntry> BuildDirect(ChannelDescriptor channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var entries = new Dictionary<string, ChannelModelEntry>(StringComparer.Ordinal);
        foreach (var model in ParseSupportedModels(channel))
        {
            entries.TryAdd(model, new ChannelModelEntry(model, model, SourceDirect));
        }

        return entries;
    }

    /// <summary>
    /// Port of <c>biz.Channel.ForcedAPIFormats</c>
    /// (channel_endpoint.go:448-463): the first enabled modelProtocols override
    /// matching <paramref name="model"/>, or an empty list.
    /// </summary>
    public static IReadOnlyList<string> ForcedApiFormats(ChannelDescriptor channel, string model)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(model);

        if (model.Length == 0)
        {
            return [];
        }

        var settings = ParseSettings(channel);
        foreach (var protocol in settings?.ModelProtocols ?? [])
        {
            if (protocol.Model == model && protocol.IsEnabled)
            {
                return protocol.ApiFormats ?? [];
            }
        }

        return [];
    }

    public static Dictionary<string, ChannelModelEntry> Build(ChannelDescriptor channel)
    {
        var supportedModels = ParseSupportedModels(channel);
        var entries = new Dictionary<string, ChannelModelEntry>(StringComparer.Ordinal);

        // 1. Direct models from SupportedModels.
        foreach (var model in supportedModels)
        {
            entries.TryAdd(model, new ChannelModelEntry(model, model, SourceDirect));
        }

        var settings = ParseSettings(channel);
        if (settings is null)
        {
            return entries;
        }

        // 2. Prefixed models (ExtraModelPrefix).
        if (!string.IsNullOrEmpty(settings.ExtraModelPrefix))
        {
            foreach (var model in supportedModels)
            {
                var prefixed = settings.ExtraModelPrefix + "/" + model;
                entries.TryAdd(prefixed, new ChannelModelEntry(prefixed, model, SourcePrefix));
            }
        }

        // 3. Auto-trimmed models (AutoTrimedModelPrefixes).
        foreach (var prefix in settings.AutoTrimedModelPrefixes ?? [])
        {
            if (string.IsNullOrEmpty(prefix))
            {
                continue;
            }

            var prefixWithSlash = prefix + "/";
            foreach (var model in supportedModels)
            {
                if (!model.StartsWith(prefixWithSlash, StringComparison.Ordinal))
                {
                    continue;
                }

                var trimmed = model[prefixWithSlash.Length..];
                entries.TryAdd(trimmed, new ChannelModelEntry(trimmed, model, SourceAutoTrim));
            }
        }

        // 4. Model mappings. Only aliases whose target is actually supported.
        foreach (var mapping in settings.ModelMappings ?? [])
        {
            // Go's []ModelMapping has value elements: a JSON null element
            // decodes into the zero struct.
            var from = mapping?.From ?? string.Empty;
            var to = mapping?.To ?? string.Empty;
            if (!supportedModels.Contains(to, StringComparer.Ordinal))
            {
                continue;
            }

            if (!entries.TryAdd(from, new ChannelModelEntry(from, to, SourceMapping)))
            {
                continue;
            }

            if (settings.HideMappedModels)
            {
                foreach (var key in entries
                    .Where(pair => pair.Value.ActualModel == to && pair.Value.Source != SourceMapping)
                    .Select(pair => pair.Key)
                    .ToArray())
                {
                    entries.Remove(key);
                }
            }
        }

        // 5. Hide the original models when requested.
        if (settings.HideOriginalModels)
        {
            foreach (var key in entries
                .Where(pair => pair.Value.Source == SourceDirect)
                .Select(pair => pair.Key)
                .ToArray())
            {
                entries.Remove(key);
            }
        }

        // 6. Lowercase matching keys. On a collision the higher-priority source
        // wins, exactly like the reference. The reference iterates a Go map, so
        // equal-priority collisions are already unspecified there.
        if (settings.LowercaseModelId)
        {
            var lowercased = new Dictionary<string, ChannelModelEntry>(entries.Count, StringComparer.Ordinal);
            foreach (var (key, entry) in entries)
            {
                var lowerKey = key.ToLowerInvariant();
                var lowerEntry = entry with { RequestModel = entry.RequestModel.ToLowerInvariant() };
                if (!lowercased.TryGetValue(lowerKey, out var existing)
                    || SourcePriority[lowerEntry.Source] > SourcePriority[existing.Source])
                {
                    lowercased[lowerKey] = lowerEntry;
                }
            }

            entries = lowercased;
        }

        return entries;
    }

    private static string[] ParseSupportedModels(ChannelDescriptor channel)
    {
        if (string.IsNullOrWhiteSpace(channel.SupportedModels) || channel.SupportedModels == "null")
        {
            return [];
        }

        // Go unmarshals a null element of a []string into "", so the reference
        // sees an empty model ID rather than a missing one.
        var models = JsonSerializer.Deserialize<string?[]>(channel.SupportedModels, JsonOptions) ?? [];
        return [.. models.Select(static model => model ?? string.Empty)];
    }

    /// <summary>
    /// Decodes the six settings fields the diagnostics export mirrors. A null
    /// or <c>null</c> document yields <see langword="null"/> (the reference's
    /// nil <c>*objects.ChannelSettings</c>); malformed JSON throws like Ent's
    /// scan.
    /// </summary>
    public static ChannelSettingsSnapshot? ParseSettingsSnapshot(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)
            || string.Equals(settingsJson, "null", StringComparison.Ordinal))
        {
            return null;
        }

        var settings = JsonSerializer.Deserialize<ChannelSettings>(settingsJson, JsonOptions);
        if (settings is null)
        {
            return null;
        }

        return new ChannelSettingsSnapshot(
            settings.ExtraModelPrefix ?? string.Empty,
            settings.AutoTrimedModelPrefixes?
                .Select(static prefix => prefix ?? string.Empty)
                .ToList(),
            settings.HideMappedModels,
            settings.HideOriginalModels,
            settings.LowercaseModelId,
            settings.ModelMappings?
                .Select(static mapping => new ModelMappingSnapshot(
                    mapping?.From ?? string.Empty,
                    mapping?.To ?? string.Empty))
                .ToList());
    }

    private static ChannelSettings? ParseSettings(ChannelDescriptor channel)
    {
        if (string.IsNullOrWhiteSpace(channel.Settings) || channel.Settings == "null")
        {
            return null;
        }

        return JsonSerializer.Deserialize<ChannelSettings>(channel.Settings, JsonOptions);
    }

    /// <summary>Mirror of <c>objects.ChannelSettings</c> (internal/objects/channel.go).</summary>
    private sealed class ChannelSettings
    {
        public string? ExtraModelPrefix { get; set; }

        public string[]? AutoTrimedModelPrefixes { get; set; }

        public ModelMapping?[]? ModelMappings { get; set; }

        public bool HideOriginalModels { get; set; }

        public bool HideMappedModels { get; set; }

        public bool LowercaseModelId { get; set; }

        public ModelProtocol[]? ModelProtocols { get; set; }
    }

    private sealed class ModelMapping
    {
        public string? From { get; set; }

        public string? To { get; set; }
    }

    /// <summary>Mirror of <c>objects.ModelProtocol</c> (internal/objects/channel.go:135-146).</summary>
    private sealed class ModelProtocol
    {
        public string? Model { get; set; }

        public string[]? ApiFormats { get; set; }

        public bool? Enabled { get; set; }

        /// <summary>Missing enabled values default to true (backwards compatibility).</summary>
        public bool IsEnabled => Enabled != false;
    }
}
