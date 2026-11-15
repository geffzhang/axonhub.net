using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AxonHub.Abstractions;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Diagnostics;

/// <summary>A channel row already decoded for the database half of the export.</summary>
internal sealed record ChannelDiagnosticsRow(
    ChannelRecord Record,
    ChannelSettingsSnapshot? Settings);

/// <summary>A model row already decoded for the database half of the export.</summary>
internal sealed record ModelDiagnosticsRow(
    ModelRecord Record,
    ModelEntitySettings? Settings);

/// <summary>
/// Builds the <c>channelModelCacheDiagnosticsExport</c> JSON tree
/// (internal/server/gql/channel_model_cache_diagnostics.go:22-350).
/// </summary>
/// <remarks>
/// <para>
/// The export is a Go struct tree marshalled by <c>json.MarshalIndent</c>, so
/// every object here fixes the Go field order, the <c>omitempty</c> rules and
/// the nil-versus-empty slice distinctions. Values are rendered through the
/// same <c>GoValue</c> writer the override pipeline uses, then indented by
/// <see cref="GoJsonIndent"/>.
/// </para>
/// <para>
/// Slice rules from the reference: <c>append([]T(nil), value...)</c> collapses
/// the empty slice to <c>nil</c> (JSON <c>null</c>), while <c>lo.Map</c> always
/// returns a non-nil slice (JSON <c>[]</c> when the input was empty).
/// </para>
/// </remarks>
internal static class CacheDiagnosticsExport
{
    public static async Task<GoValue> BuildAsync(
        IReadOnlyList<DiagnosticsTargetValue> targets,
        string exportedAt,
        IReadOnlyList<ChannelDiagnosticsRow> channels,
        IReadOnlyList<ModelDiagnosticsRow> models,
        EnabledChannelsSnapshot enabledCache,
        ICacheDiagnosticsCandidateSelector candidateSelector,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var channelValues = new List<GoValue>(channels.Count);
        foreach (var channel in channels)
        {
            channelValues.Add(BuildChannelDbSnapshot(channel));
        }

        var modelValues = new List<GoValue>(models.Count);
        foreach (var model in models)
        {
            modelValues.Add(await BuildModelViewAsync(model, candidateSelector, cancellationToken)
                .ConfigureAwait(false));
        }

        var targetValues = new List<GoValue>(targets.Count);
        foreach (var target in targets)
        {
            targetValues.Add(new GoStringValue(DiagnosticsTargets.ToWire(target)));
        }

        // The model previews above run the selector, so the shared association
        // cache is populated by the time the reference's buildCacheDiagnostics
        // reads it (channel_model_cache_diagnostics.go:177-186).
        var associationEntries = candidateSelector.ReadAssociationCache();
        var associationValues = new List<GoValue>(associationEntries.Count);
        foreach (var entry in associationEntries)
        {
            associationValues.Add(BuildAssociationCacheEntry(entry));
        }

        return Object(
            Member("exportedAt", new GoStringValue(exportedAt)),
            Member("targets", new GoSliceValue(targetValues)),
            Member("database", Object(
                Member("channels", new GoSliceValue(channelValues)),
                Member("models", new GoSliceValue(modelValues)))),
            Member("cache", Object(
                Member(
                    "enabledChannelCache",
                    BuildEnabledChannelCacheSnapshot(enabledCache, exportedAt, timeProvider)),
                Member("modelAssociationCache", new GoSliceValue(associationValues)))));
    }

    /// <summary>
    /// Port of channelModelAssociationCacheEntrySnapshot
    /// (channel_model_cache_diagnostics.go:39-48 and 190-203): Go field order,
    /// second-precision RFC 3339 timestamps, and a nil associations slice when
    /// the entry has none.
    /// </summary>
    private static GoValue BuildAssociationCacheEntry(AssociationCacheEntrySnapshot entry)
        => Object(
            Member("modelId", new GoStringValue(entry.ModelId)),
            Member(
                "associations",
                entry.Associations.Count == 0
                    ? GoNullValue.Instance
                    : BuildCachedAssociations(entry.Associations)),
            Member("candidateCount", new GoIntValue(entry.CandidateCount)),
            Member("channelCount", new GoIntValue(entry.ChannelCount)),
            Member(
                "latestChannelUpdateTime",
                new GoStringValue(GoTime.Rfc3339(entry.LatestChannelUpdateTime))),
            Member(
                "latestModelUpdateTime",
                new GoStringValue(GoTime.Rfc3339(entry.LatestModelUpdateTime))),
            Member("channelCacheVersion", new GoIntValue(entry.ChannelCacheVersion)),
            Member("cachedAt", new GoStringValue(GoTime.Rfc3339(entry.CachedAt))));

    private static GoValue BuildCachedAssociations(IReadOnlyList<ModelAssociation> associations)
    {
        var values = new List<GoValue>(associations.Count);
        foreach (var association in associations)
        {
            values.Add(BuildAssociation(association));
        }

        return new GoSliceValue(values);
    }

    // ------------------------------------------------------------------
    // cache.enabledChannelCache
    // ------------------------------------------------------------------

    private static GoValue BuildEnabledChannelCacheSnapshot(
        EnabledChannelsSnapshot snapshot,
        string capturedAt,
        TimeProvider timeProvider)
    {
        var channels = snapshot.Channels;
        var ids = new List<GoValue>(channels.Count);
        var names = new List<GoValue>(channels.Count);
        var cached = new List<GoValue>(channels.Count);
        foreach (var channel in channels)
        {
            ids.Add(new GoIntValue(channel.Id));
            names.Add(new GoStringValue(channel.Name));
            cached.Add(BuildCachedChannelSnapshot(channel, timeProvider));
        }

        return Object(
            Member("channelCount", new GoIntValue(channels.Count)),
            Member("cacheVersion", new GoIntValue(snapshot.Version)),
            Member("channelIDs", new GoSliceValue(ids)),
            Member("channelNames", new GoSliceValue(names)),
            Member("channels", new GoSliceValue(cached)),
            Member("capturedAtUtc", new GoStringValue(capturedAt)));
    }

    /// <summary>
    /// The reference snapshots the built <c>biz.Channel</c>; the port keeps the
    /// same row as a descriptor. Every cached row is by construction
    /// <c>status = enabled</c> (reloadEnabledChannels queries that status only).
    /// </summary>
    private static GoValue BuildCachedChannelSnapshot(
        EnabledChannelSnapshot channel,
        TimeProvider timeProvider)
    {
        var descriptor = channel.ToDescriptor();
        var entries = ChannelModelEntries.Build(descriptor);
        var keys = entries.Keys.ToArray();
        Array.Sort(keys, StringComparer.Ordinal);

        var modelEntries = new List<GoValue>(keys.Length);
        foreach (var key in keys)
        {
            var entry = entries[key];
            modelEntries.Add(BuildEntry(entry.RequestModel, entry.ActualModel, entry.Source));
        }

        return Object(
            Member("id", new GoIntValue(channel.Id)),
            Member("name", new GoStringValue(channel.Name)),
            Member("type", new GoStringValue(channel.Type)),
            Member("status", new GoStringValue("enabled")),
            Member("updatedAt", new GoStringValue(GoTime.Rfc3339(channel.UpdatedAt))),
            Member("tags", StringListOrNull(channel.Tags)),
            Member("supportedModels", StringListOrNull(channel.SupportedModels)),
            Member("defaultTestModel", new GoStringValue(channel.DefaultTestModel)),
            // ch.GetEnabledAPIKeys(): the reference caches the enabled keys at
            // channel-build time; the port recomputes at export time (C240).
            Member(
                "enabledApiKeyCount",
                new GoIntValue(ChannelCredentials.GetEnabledApiKeys(
                    channel.Credentials,
                    channel.DisabledApiKeys,
                    timeProvider.GetUtcNow()).Count)),
            Member(
                "disabledApiKeyCount",
                new GoIntValue(ChannelCredentials.CountDisabledApiKeys(channel.DisabledApiKeys))),
            Member(
                "settings",
                BuildSettingsSnapshot(ChannelModelEntries.ParseSettingsSnapshot(channel.Settings))),
            Member("modelEntries", new GoSliceValue(modelEntries)));
    }

    // ------------------------------------------------------------------
    // database.channels
    // ------------------------------------------------------------------

    private static GoValue BuildChannelDbSnapshot(ChannelDiagnosticsRow row)
    {
        var channel = row.Record;

        return Object(
            Member("id", new GoIntValue(channel.Id)),
            Member("name", new GoStringValue(channel.Name)),
            Member("type", new GoStringValue(channel.Type)),
            Member("status", new GoStringValue(channel.Status)),
            Member("updatedAt", new GoStringValue(GoTime.Rfc3339(channel.UpdatedAt))),
            // Ent's optional string field is a plain string: a NULL column
            // scans as "".
            Member("baseUrl", new GoStringValue(channel.BaseUrl ?? string.Empty)),
            Member("tags", StringListOrNull(channel.Tags)),
            Member("supportedModels", StringListOrNull(channel.SupportedModels)),
            Member("defaultTestModel", new GoStringValue(channel.DefaultTestModel)),
            Member("autoSyncSupportedModels", new GoBoolValue(channel.AutoSyncSupportedModels)),
            Member("settings", BuildSettingsSnapshot(row.Settings)));
    }

    // ------------------------------------------------------------------
    // settings snapshot (shared by both channel views)
    // ------------------------------------------------------------------

    private static GoValue BuildSettingsSnapshot(ChannelSettingsSnapshot? settings)
    {
        var members = new List<KeyValuePair<string, GoValue>>();

        var extraModelPrefix = settings?.ExtraModelPrefix ?? string.Empty;
        if (extraModelPrefix.Length > 0)
        {
            members.Add(Member("extraModelPrefix", new GoStringValue(extraModelPrefix)));
        }

        if (settings?.AutoTrimedModelPrefixes is { Count: > 0 } prefixes)
        {
            members.Add(Member("autoTrimedModelPrefixes", StringListValue(prefixes)));
        }

        members.Add(Member(
            "hideMappedModels",
            new GoBoolValue(settings?.HideMappedModels ?? false)));
        members.Add(Member(
            "hideOriginalModels",
            new GoBoolValue(settings?.HideOriginalModels ?? false)));
        members.Add(Member(
            "lowercaseModelId",
            new GoBoolValue(settings?.LowercaseModelId ?? false)));

        if (settings?.ModelMappings is { Count: > 0 } mappings)
        {
            var mappingValues = new List<GoValue>(mappings.Count);
            foreach (var mapping in mappings)
            {
                mappingValues.Add(Object(
                    Member("from", new GoStringValue(mapping.From)),
                    Member("to", new GoStringValue(mapping.To))));
            }

            members.Add(Member("modelMappings", new GoSliceValue(mappingValues)));
        }

        return new GoMapValue(members, sortKeys: false);
    }

    // ------------------------------------------------------------------
    // database.models
    // ------------------------------------------------------------------

    private static async Task<GoValue> BuildModelViewAsync(
        ModelDiagnosticsRow row,
        ICacheDiagnosticsCandidateSelector candidateSelector,
        CancellationToken cancellationToken)
    {
        var model = row.Record;

        IReadOnlyList<ChannelCandidate> candidates = [];
        if (string.Equals(model.Status, "enabled", StringComparison.Ordinal))
        {
            try
            {
                candidates = await candidateSelector
                    .SelectAsync(model.ModelId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                // buildModelDiagnostics keeps `candidates` nil whenever Select
                // fails; the preview is best-effort and the error is dropped.
                candidates = [];
            }
        }

        var associationCount = 0;
        GoValue associations = GoNullValue.Instance;
        if (row.Settings is not null)
        {
            associationCount = row.Settings.Associations?.Count ?? 0;
            if (row.Settings.Associations is not null)
            {
                associations = BuildAssociations(row.Settings.Associations);
            }
        }

        return Object(
            Member("id", new GoIntValue(model.Id)),
            Member("modelId", new GoStringValue(model.ModelId)),
            Member("status", new GoStringValue(model.Status)),
            Member("updatedAt", new GoStringValue(GoTime.Rfc3339(model.UpdatedAt))),
            Member("associationCount", new GoIntValue(associationCount)),
            Member("associations", associations),
            Member("candidateSelectionPreview", BuildCandidatePreview(candidates)));
    }

    private static GoValue BuildCandidatePreview(IReadOnlyList<ChannelCandidate> candidates)
    {
        var previews = new List<GoValue>(candidates.Count);
        foreach (var candidate in candidates)
        {
            var models = new List<GoValue>(candidate.Models.Count);
            foreach (var entry in candidate.Models)
            {
                models.Add(BuildEntry(entry.RequestModel, entry.ActualModel, entry.Source));
            }

            previews.Add(Object(
                Member("channelId", new GoIntValue(candidate.ChannelId)),
                Member("channelName", new GoStringValue(candidate.ChannelName)),
                Member("priority", new GoIntValue(candidate.Priority)),
                Member("models", new GoSliceValue(models))));
        }

        return new GoSliceValue(previews);
    }

    // ------------------------------------------------------------------
    // associations ([]*objects.ModelAssociation)
    // ------------------------------------------------------------------

    private static GoValue BuildAssociations(IReadOnlyList<ModelAssociation?> associations)
    {
        var values = new List<GoValue>(associations.Count);
        foreach (var association in associations)
        {
            values.Add(association is null ? GoNullValue.Instance : BuildAssociation(association));
        }

        return new GoSliceValue(values);
    }

    /// <summary>Field order and tags of objects.ModelAssociation (model.go:63-80).</summary>
    private static GoValue BuildAssociation(ModelAssociation association)
    {
        var members = new List<KeyValuePair<string, GoValue>>
        {
            Member("type", new GoStringValue(association.Type)),
            Member("priority", new GoIntValue(association.Priority)),
            Member("disabled", new GoBoolValue(association.Disabled)),
        };

        if (association.When is not null)
        {
            members.Add(Member("when", BuildAssociationWhen(association.When)));
        }

        members.Add(Member(
            "channelModel",
            association.ChannelModel is null
                ? GoNullValue.Instance
                : Object(
                    Member("channelId", new GoIntValue(association.ChannelModel.ChannelId)),
                    Member("modelId", new GoStringValue(association.ChannelModel.ModelId)))));
        members.Add(Member(
            "channelRegex",
            association.ChannelRegex is null
                ? GoNullValue.Instance
                : Object(
                    Member("channelId", new GoIntValue(association.ChannelRegex.ChannelId)),
                    Member("pattern", new GoStringValue(association.ChannelRegex.Pattern)))));
        members.Add(Member(
            "regex",
            association.Regex is null
                ? GoNullValue.Instance
                : Object(
                    Member("pattern", new GoStringValue(association.Regex.Pattern)),
                    Member("exclude", BuildExcludes(association.Regex.Exclude)))));
        members.Add(Member(
            "modelId",
            association.ModelId is null
                ? GoNullValue.Instance
                : Object(
                    Member("modelId", new GoStringValue(association.ModelId.ModelId)),
                    Member("exclude", BuildExcludes(association.ModelId.Exclude)))));
        members.Add(Member(
            "channelTagsModel",
            association.ChannelTagsModel is null
                ? GoNullValue.Instance
                : Object(
                    Member(
                        "channelTags",
                        association.ChannelTagsModel.ChannelTags is null
                            ? GoNullValue.Instance
                            : StringListValue(association.ChannelTagsModel.ChannelTags)),
                    Member("modelId", new GoStringValue(association.ChannelTagsModel.ModelId)))));
        members.Add(Member(
            "channelTagsRegex",
            association.ChannelTagsRegex is null
                ? GoNullValue.Instance
                : Object(
                    Member(
                        "channelTags",
                        association.ChannelTagsRegex.ChannelTags is null
                            ? GoNullValue.Instance
                            : StringListValue(association.ChannelTagsRegex.ChannelTags)),
                    Member("pattern", new GoStringValue(association.ChannelTagsRegex.Pattern)))));

        return new GoMapValue(members, sortKeys: false);
    }

    private static GoValue BuildAssociationWhen(ModelAssociationWhen when)
    {
        var members = new List<KeyValuePair<string, GoValue>>
        {
            Member("enabled", new GoBoolValue(when.Enabled)),
        };

        if (when.Condition is not null)
        {
            members.Add(Member("condition", BuildCondition(when.Condition)));
        }

        return new GoMapValue(members, sortKeys: false);
    }

    /// <summary>Field order and tags of objects.Condition (condition.go:25-32).</summary>
    private static GoValue BuildCondition(FilterCondition condition)
    {
        var members = new List<KeyValuePair<string, GoValue>>
        {
            Member("type", new GoStringValue(condition.Type)),
        };

        if (condition.Logic.Length > 0)
        {
            members.Add(Member("logic", new GoStringValue(condition.Logic)));
        }

        if (condition.Conditions is { Count: > 0 } children)
        {
            var childValues = new List<GoValue>(children.Count);
            foreach (var child in children)
            {
                childValues.Add(BuildCondition(child));
            }

            members.Add(Member("conditions", new GoSliceValue(childValues)));
        }

        if (condition.Field.Length > 0)
        {
            members.Add(Member("field", new GoStringValue(condition.Field)));
        }

        if (condition.Operator.Length > 0)
        {
            members.Add(Member("operator", new GoStringValue(condition.Operator)));
        }

        // `value` is `any` with omitempty: only a nil interface is omitted, and
        // any non-nil scalar/object is emitted (no zero-value elision).
        if (condition.Value is not null)
        {
            members.Add(Member("value", BuildJsonValue(condition.Value)));
        }

        return new GoMapValue(members, sortKeys: false);
    }

    private static GoValue BuildExcludes(IReadOnlyList<ExcludeAssociation?>? excludes)
    {
        if (excludes is null)
        {
            return GoNullValue.Instance;
        }

        var values = new List<GoValue>(excludes.Count);
        foreach (var exclude in excludes)
        {
            if (exclude is null)
            {
                values.Add(GoNullValue.Instance);
                continue;
            }

            values.Add(Object(
                Member("channelNamePattern", new GoStringValue(exclude.ChannelNamePattern)),
                Member(
                    "channelIds",
                    exclude.ChannelIds is null
                        ? GoNullValue.Instance
                        : new GoSliceValue([.. exclude.ChannelIds.Select(static id => (GoValue)new GoIntValue(id))])),
                Member(
                    "channelTags",
                    exclude.ChannelTags is null
                        ? GoNullValue.Instance
                        : StringListValue(exclude.ChannelTags))));
        }

        return new GoSliceValue(values);
    }

    // ------------------------------------------------------------------
    // JSON `any` values (condition.Value)
    // ------------------------------------------------------------------

    private static GoValue BuildJsonValue(JsonNode? node) => node switch
    {
        null => GoNullValue.Instance,
        JsonObject obj => new GoMapValue(
            obj.OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => Member(pair.Key, BuildJsonValue(pair.Value))),
            sortKeys: true),
        JsonArray array => new GoSliceValue([.. array.Select(BuildJsonValue)]),
        JsonValue scalar => BuildJsonScalar(scalar),
        _ => GoNullValue.Instance,
    };

    private static GoValue BuildJsonScalar(JsonValue scalar)
    {
        if (scalar.TryGetValue<JsonElement>(out var element))
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => new GoStringValue(element.GetString() ?? string.Empty),
                JsonValueKind.True => new GoBoolValue(true),
                JsonValueKind.False => new GoBoolValue(false),
                JsonValueKind.Number => BuildJsonNumber(element.GetRawText()),
                _ => GoNullValue.Instance,
            };
        }

        return scalar.GetValueKind() switch
        {
            JsonValueKind.String => new GoStringValue(scalar.GetValue<string>()),
            JsonValueKind.True => new GoBoolValue(true),
            JsonValueKind.False => new GoBoolValue(false),
            JsonValueKind.Number => BuildJsonNumber(scalar.ToJsonString()),
            _ => GoNullValue.Instance,
        };
    }

    /// <summary>
    /// normalizeJSONNumberValue stores integral numbers as int64 and the rest
    /// as float64; Go marshals int64 in fixed notation and float64 with
    /// encoding/json's exponent rules.
    /// </summary>
    private static GoValue BuildJsonNumber(string raw)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return new GoIntValue(integer);
        }

        return new GoFloatValue(double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static GoValue BuildEntry(string requestModel, string actualModel, string source)
        => Object(
            Member("requestModel", new GoStringValue(requestModel)),
            Member("actualModel", new GoStringValue(actualModel)),
            Member("source", new GoStringValue(source)));

    /// <summary>
    /// Port of <c>append([]string(nil), values...)</c>: a nil or empty slice is
    /// JSON <c>null</c>.
    /// </summary>
    private static GoValue StringListOrNull(string? raw)
    {
        var values = ParseStringList(raw);
        return values is { Count: > 0 } ? StringListValue(values) : GoNullValue.Instance;
    }

    private static GoSliceValue StringListValue(IReadOnlyList<string> values)
        => new([.. values.Select(static value => (GoValue)new GoStringValue(value))]);

    /// <summary>
    /// Ent scans a <c>jsonb []string</c> column; a null element decodes as ""
    /// and a malformed document is rejected during the scan. The port degrades
    /// the malformed case to an empty list like the other lenient reads (C141).
    /// </summary>
    private static List<string>? ParseStringList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var values = JsonSerializer.Deserialize<string?[]>(raw, JsonOptions);
            return values?
                .Select(static value => value ?? string.Empty)
                .ToList();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static KeyValuePair<string, GoValue> Member(string name, GoValue value)
        => new(name, value);

    private static GoMapValue Object(params KeyValuePair<string, GoValue>[] members)
        => new(members, sortKeys: false);
}
