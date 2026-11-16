using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Models;

/// <summary>
/// Go-compatible JSON codec, normalization and validation for the
/// <c>system_model_settings</c> system setting (internal/server/biz/system.go
/// 437-485, 1236-1291; model_settings_inheritance.go:12-107).
/// </summary>
public static class ModelSettingsCodec
{
    /// <summary>biz.SystemKeyModelSettings.</summary>
    public const string ModelSettingsKey = "system_model_settings";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        // Go matches JSON keys to struct tags case-insensitively.
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new(LlmJson.Options)
    {
        Converters = { new GoJsonNodeConverter() },
    };

    /// <summary>
    /// Port of json.Unmarshal into biz.SystemModelSettings followed by
    /// normalizeSystemModelSettings.
    /// </summary>
    /// <exception cref="JsonException">The document cannot be decoded.</exception>
    public static ModelSettings Deserialize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var root = value.AsSpan().TrimStart();
        if (root.IsEmpty)
        {
            throw new JsonException("unexpected end of JSON input");
        }

        if (root.SequenceEqual("null"))
        {
            // json.Unmarshal("null", &settings) leaves the zero value; every
            // flag stays false and normalization installs the empty lists.
            return Normalize(new ModelSettings());
        }

        if (root[0] != '{')
        {
            throw new JsonException(
                "json: cannot unmarshal " + GoKindName(root[0])
                + " into Go value of type biz.SystemModelSettings");
        }

        var stored = JsonSerializer.Deserialize<ModelSettingsJson>(value, ReadOptions)
            ?? new ModelSettingsJson();

        var developers = new List<DeveloperModelSettings?>(stored.DeveloperSettings?.Count ?? 0);
        foreach (var developer in stored.DeveloperSettings ?? [])
        {
            if (developer is null)
            {
                // A nil *DeveloperModelSettings stays in the slice.
                developers.Add(null);
                continue;
            }

            var associations = new List<ModelAssociation?>(developer.Associations?.Count ?? 0);
            foreach (var association in developer.Associations ?? [])
            {
                associations.Add(association is null ? null : ToModelAssociation(association));
            }

            developers.Add(new DeveloperModelSettings(
                developer.Developer ?? string.Empty,
                associations));
        }

        return Normalize(new ModelSettings
        {
            FallbackToChannelsOnModelNotFound = stored.FallbackToChannelsOnModelNotFound ?? false,
            QueryAllChannelModels = stored.QueryAllChannelModels ?? false,
            DefaultModelApiIncludeAll = stored.DefaultModelApiIncludeAll ?? false,
            AutoReasoningEffort = stored.AutoReasoningEffort ?? false,
            ModelBlacklistRegex = stored.ModelBlacklistRegex ?? string.Empty,
            HideUnroutableModelsInList = stored.HideUnroutableModelsInList ?? false,
            DeveloperSettings = developers,
        });
    }

    /// <summary>Port of normalizeSystemModelSettings (model_settings_inheritance.go:12).</summary>
    public static ModelSettings Normalize(ModelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var developers = new List<DeveloperModelSettings?>(settings.DeveloperSettings.Count);
        foreach (var developer in settings.DeveloperSettings)
        {
            if (developer is null)
            {
                // nil elements are skipped, not removed.
                developers.Add(null);
                continue;
            }

            var associations = new List<ModelAssociation?>(developer.Associations.Count);
            foreach (var association in developer.Associations)
            {
                if (association is null)
                {
                    associations.Add(null);
                    continue;
                }

                // Developer-level rules never pin the model id: the model comes
                // from the model entity that inherits the rule.
                associations.Add(association.Type switch
                {
                    ModelAssociation.TypeChannelModel when association.ChannelModel is not null
                        => association with
                        {
                            ChannelModel = association.ChannelModel with { ModelId = string.Empty },
                        },
                    ModelAssociation.TypeChannelTagsModel
                        when association.ChannelTagsModel is not null
                        => association with
                        {
                            ChannelTagsModel =
                                association.ChannelTagsModel with { ModelId = string.Empty },
                        },
                    _ => association,
                });
            }

            developers.Add(new DeveloperModelSettings(developer.Developer.Trim(), associations));
        }

        return settings with { DeveloperSettings = developers };
    }

    /// <summary>
    /// Port of validateSystemModelSettings (model_settings_inheritance.go:33),
    /// throwing <see cref="ModelSettingsValidationException"/> with the
    /// reference's message.
    /// </summary>
    public static void Validate(ModelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var developerSettings in settings.DeveloperSettings)
        {
            if (developerSettings is null)
            {
                continue;
            }

            var developer = developerSettings.Developer.Trim();
            if (developer.Length == 0)
            {
                throw new ModelSettingsValidationException("model developer is required");
            }

            if (!seen.Add(developer))
            {
                throw new ModelSettingsValidationException(
                    "duplicate model developer " + GoCast.GoQuote(developer));
            }

            try
            {
                ModelAssociationValidation.ValidateDeveloperAssociations(
                    developerSettings.Associations);
            }
            catch (ModelSettingsValidationException ex)
            {
                throw new ModelSettingsValidationException(
                    "invalid developer settings for " + GoCast.GoQuote(developer) + ": "
                    + ex.Message);
            }
        }
    }

    /// <summary>Port of json.Marshal(biz.SystemModelSettings).</summary>
    public static string Serialize(ModelSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var write = new ModelSettingsWriteJson
        {
            FallbackToChannelsOnModelNotFound = settings.FallbackToChannelsOnModelNotFound,
            QueryAllChannelModels = settings.QueryAllChannelModels,
            DefaultModelApiIncludeAll = settings.DefaultModelApiIncludeAll,
            AutoReasoningEffort = settings.AutoReasoningEffort,
            ModelBlacklistRegex = settings.ModelBlacklistRegex,
            HideUnroutableModelsInList = settings.HideUnroutableModelsInList,
            DeveloperSettings = settings.DeveloperSettings
                .Select(static developer => developer is null
                    ? null
                    : new DeveloperSettingsWriteJson
                    {
                        Developer = developer.Developer,
                        Associations = developer.Associations
                            .Select(static association => association is null
                                ? null
                                : ToAssociationWrite(association))
                            .ToList(),
                    })
                .ToList(),
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(write, WriteOptions);
        return Encoding.UTF8.GetString(GoJson.Escape(bytes));
    }

    /// <summary>
    /// Port of ent scanning <c>models.settings</c> into
    /// <c>*objects.ModelSettings</c> (internal/ent/model.go:147-153) and
    /// <c>objects.ModelSettings.UnmarshalJSON</c> (internal/objects/routing.go:62-77).
    /// </summary>
    /// <remarks>
    /// ent skips the unmarshal for a NULL or empty column, so the field stays
    /// nil; a JSON <c>null</c> likewise sets the pointer to nil. Any other
    /// document decodes over a struct seeded with <c>"default"</c> routing
    /// policies and is normalized afterwards.
    /// </remarks>
    /// <exception cref="JsonException">The document cannot be decoded.</exception>
    public static ModelEntitySettings? DeserializeEntitySettings(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var root = value.AsSpan().Trim();
        if (root.IsEmpty)
        {
            throw new JsonException("unexpected end of JSON input");
        }

        if (root.SequenceEqual("null"))
        {
            return null;
        }

        if (root[0] != '{')
        {
            throw new JsonException(
                "failed to unmarshal model settings: json: cannot unmarshal "
                + GoKindName(root[0])
                + " into Go value of type objects.modelSettingsAlias");
        }

        ModelEntitySettingsJson? stored;
        try
        {
            stored = JsonSerializer.Deserialize<ModelEntitySettingsJson>(value, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new JsonException(
                "failed to unmarshal model settings: " + ex.Message,
                ex);
        }

        if (stored is null)
        {
            return null;
        }

        return new ModelEntitySettings
        {
            DisableDeveloperSettingsInheritance =
                stored.DisableDeveloperSettingsInheritance ?? false,
            Associations = stored.Associations?
                .Select(static association => association is null
                    ? null
                    : ToModelAssociation(association))
                .ToList(),
            LoadBalancerStrategy = stored.LoadBalancerStrategy ?? RoutingPolicy.Default,
            TraceStickyMode = stored.TraceStickyMode ?? RoutingPolicy.Default,
        }.NormalizeRoutingPolicy();
    }

    /// <summary>Port of <c>json.Marshal(objects.ModelSettings)</c>.</summary>
    public static string SerializeEntitySettings(ModelEntitySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var write = new ModelEntitySettingsWriteJson
        {
            DisableDeveloperSettingsInheritance = settings.DisableDeveloperSettingsInheritance,
            Associations = settings.Associations?
                .Select(static association => association is null
                    ? null
                    : ToAssociationWrite(association))
                .ToList(),
            LoadBalancerStrategy = settings.LoadBalancerStrategy,
            TraceStickyMode = settings.TraceStickyMode,
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(write, WriteOptions);
        return Encoding.UTF8.GetString(GoJson.Escape(bytes));
    }

    /// <summary>
    /// Port of <c>normalizeJSONNumberValue</c> (internal/objects/condition.go:294):
    /// decoder numbers become int64 when integral, float64 otherwise, and
    /// strings when neither parse succeeds. Object keys are sorted because Go
    /// marshals <c>map[string]any</c> in sorted order.
    /// </summary>
    public static JsonNode? NormalizeValue(JsonElement? element)
    {
        if (element is not { } value || value.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return NormalizeElement(value);
    }

    /// <inheritdoc cref="NormalizeValue(JsonElement?)"/>
    public static JsonNode? NormalizeValue(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
                var sorted = new JsonObject();
                foreach (var property in obj.OrderBy(
                             static pair => pair.Key,
                             StringComparer.Ordinal))
                {
                    sorted[property.Key] = NormalizeValue(property.Value);
                }

                return sorted;
            case JsonArray array:
                var normalizedArray = new JsonArray();
                foreach (var item in array)
                {
                    normalizedArray.Add(NormalizeValue(item));
                }

                return normalizedArray;
            case JsonValue scalar:
                if (scalar.TryGetValue<JsonElement>(out var element))
                {
                    return NormalizeElement(element);
                }

                return scalar.GetValueKind() switch
                {
                    JsonValueKind.String => JsonValue.Create(scalar.GetValue<string>()),
                    JsonValueKind.True or JsonValueKind.False
                        => JsonValue.Create(scalar.GetValue<bool>()),
                    JsonValueKind.Number => NormalizeNumber(scalar.ToJsonString()),
                    _ => null,
                };
            default:
                return null;
        }
    }

    private static JsonNode? NormalizeElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => JsonValue.Create(element.GetString()),
        JsonValueKind.Number => NormalizeNumber(element.GetRawText()),
        JsonValueKind.True or JsonValueKind.False => JsonValue.Create(element.GetBoolean()),
        JsonValueKind.Array => NormalizeArray(element),
        JsonValueKind.Object => NormalizeObject(element),
        _ => null,
    };

    private static JsonArray NormalizeArray(JsonElement element)
    {
        var array = new JsonArray();
        foreach (var item in element.EnumerateArray())
        {
            array.Add(NormalizeElement(item));
        }

        return array;
    }

    private static JsonObject NormalizeObject(JsonElement element)
    {
        var obj = new JsonObject();
        foreach (var property in element.EnumerateObject()
                     .OrderBy(static pair => pair.Name, StringComparer.Ordinal))
        {
            obj[property.Name] = NormalizeElement(property.Value);
        }

        return obj;
    }

    private static JsonValue NormalizeNumber(string raw)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            // json.Number.Int64 succeeded, so Go stores an int64 and marshals
            // it in fixed notation.
            return JsonValue.Create(integer);
        }

        if (double.TryParse(
                raw,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var floating)
            && double.IsFinite(floating))
        {
            return JsonValue.Create(floating);
        }

        // strconv.ParseFloat failed (range error): normalizeJSONNumberValue
        // falls back to the raw text as a string.
        return JsonValue.Create(raw);
    }

    private static ModelAssociation ToModelAssociation(ModelAssociationJson association)
    {
        var when = association.When is null
            ? null
            : new ModelAssociationWhen(
                association.When.Enabled ?? false,
                association.When.Condition is null
                    ? null
                    : ToCondition(association.When.Condition));

        return new ModelAssociation
        {
            Type = association.Type ?? string.Empty,
            Priority = association.Priority ?? 0,
            Disabled = association.Disabled ?? false,
            When = when,
            ChannelModel = association.ChannelModel is null
                ? null
                : new ChannelModelAssociation(
                    association.ChannelModel.ChannelId ?? 0,
                    association.ChannelModel.ModelId ?? string.Empty),
            ChannelRegex = association.ChannelRegex is null
                ? null
                : new ChannelRegexAssociation(
                    association.ChannelRegex.ChannelId ?? 0,
                    association.ChannelRegex.Pattern ?? string.Empty),
            Regex = association.Regex is null
                ? null
                : new RegexAssociation(
                    association.Regex.Pattern ?? string.Empty,
                    ToExcludes(association.Regex.Exclude)),
            ModelId = association.ModelId is null
                ? null
                : new ModelIdAssociation(
                    association.ModelId.ModelId ?? string.Empty,
                    ToExcludes(association.ModelId.Exclude)),
            ChannelTagsModel = association.ChannelTagsModel is null
                ? null
                : new ChannelTagsModelAssociation(
                    ToStrings(association.ChannelTagsModel.ChannelTags),
                    association.ChannelTagsModel.ModelId ?? string.Empty),
            ChannelTagsRegex = association.ChannelTagsRegex is null
                ? null
                : new ChannelTagsRegexAssociation(
                    ToStrings(association.ChannelTagsRegex.ChannelTags),
                    association.ChannelTagsRegex.Pattern ?? string.Empty),
        };
    }

    private static List<ExcludeAssociation?>? ToExcludes(List<ExcludeAssociationJson?>? stored)
    {
        if (stored is null)
        {
            return null;
        }

        return stored
            .Select(static exclude => exclude is null
                ? null
                : new ExcludeAssociation(
                    exclude.ChannelNamePattern ?? string.Empty,
                    exclude.ChannelIds?
                        .Select(static id => id ?? 0)
                        .ToList(),
                    ToStrings(exclude.ChannelTags)))
            .ToList();
    }

    private static List<string>? ToStrings(List<string?>? stored)
        => stored?
            .Select(static value => value ?? string.Empty)
            .ToList();

    private static FilterCondition ToCondition(ConditionJson condition)
    {
        List<FilterCondition>? children = null;
        if (condition.Conditions is not null)
        {
            // Go's []Condition has value elements: a JSON null decodes into the
            // zero Condition, not a nil pointer.
            children = condition.Conditions
                .Select(static child => ToCondition(child ?? new ConditionJson()))
                .ToList();
        }

        return new FilterCondition
        {
            Type = condition.Type ?? string.Empty,
            Logic = condition.Logic ?? string.Empty,
            Conditions = children,
            Field = condition.Field ?? string.Empty,
            Operator = condition.Operator ?? string.Empty,
            Value = NormalizeValue(condition.Value),
        };
    }

    private static ModelAssociationWriteJson ToAssociationWrite(ModelAssociation association) => new()
    {
        Type = association.Type,
        Priority = association.Priority,
        Disabled = association.Disabled,
        When = association.When is null
            ? null
            : new ModelAssociationWhenWriteJson
            {
                Enabled = association.When.Enabled,
                Condition = association.When.Condition is null
                    ? null
                    : ToConditionWrite(association.When.Condition),
            },
        ChannelModel = association.ChannelModel is null
            ? null
            : new ChannelModelAssociationWriteJson
            {
                ChannelId = association.ChannelModel.ChannelId,
                ModelId = association.ChannelModel.ModelId,
            },
        ChannelRegex = association.ChannelRegex is null
            ? null
            : new ChannelRegexAssociationWriteJson
            {
                ChannelId = association.ChannelRegex.ChannelId,
                Pattern = association.ChannelRegex.Pattern,
            },
        Regex = association.Regex is null
            ? null
            : new RegexAssociationWriteJson
            {
                Pattern = association.Regex.Pattern,
                Exclude = ToExcludesWrite(association.Regex.Exclude),
            },
        ModelId = association.ModelId is null
            ? null
            : new ModelIdAssociationWriteJson
            {
                ModelId = association.ModelId.ModelId,
                Exclude = ToExcludesWrite(association.ModelId.Exclude),
            },
        ChannelTagsModel = association.ChannelTagsModel is null
            ? null
            : new ChannelTagsModelAssociationWriteJson
            {
                ChannelTags = association.ChannelTagsModel.ChannelTags,
                ModelId = association.ChannelTagsModel.ModelId,
            },
        ChannelTagsRegex = association.ChannelTagsRegex is null
            ? null
            : new ChannelTagsRegexAssociationWriteJson
            {
                ChannelTags = association.ChannelTagsRegex.ChannelTags,
                Pattern = association.ChannelTagsRegex.Pattern,
            },
    };

    private static List<ExcludeAssociationWriteJson?>? ToExcludesWrite(
        IReadOnlyList<ExcludeAssociation?>? excludes)
        => excludes?
            .Select(static exclude => exclude is null
                ? null
                : new ExcludeAssociationWriteJson
                {
                    ChannelNamePattern = exclude.ChannelNamePattern,
                    ChannelIds = exclude.ChannelIds,
                    ChannelTags = exclude.ChannelTags,
                })
            .ToList();

    private static ConditionWriteJson ToConditionWrite(FilterCondition condition) => new()
    {
        Type = condition.Type,
        Logic = condition.Logic.Length == 0 ? null : condition.Logic,
        Conditions = condition.Conditions is { Count: > 0 }
            ? condition.Conditions.Select(ToConditionWrite).ToList()
            : null,
        Field = condition.Field.Length == 0 ? null : condition.Field,
        Operator = condition.Operator.Length == 0 ? null : condition.Operator,
        Value = condition.Value,
    };

    private static string GoKindName(char first) => first switch
    {
        '[' => "array",
        '"' => "string",
        't' or 'f' => "bool",
        '-' or (>= '0' and <= '9') => "number",
        _ => "object",
    };

    /// <summary>
    /// Writes <c>any</c>-typed condition values with Go's float formatting and
    /// sorted object keys. STJ's default would emit <c>1E+20</c> where Go emits
    /// <c>1e+20</c>.
    /// </summary>
    private sealed class GoJsonNodeConverter : JsonConverter<JsonNode>
    {
        public override JsonNode? Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
            => JsonNode.Parse(ref reader);

        public override void Write(Utf8JsonWriter writer, JsonNode value, JsonSerializerOptions options)
            => WriteNode(writer, value);

        private static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
        {
            switch (node)
            {
                case null:
                    writer.WriteNullValue();
                    return;
                case JsonObject obj:
                    writer.WriteStartObject();
                    foreach (var property in obj.OrderBy(
                                 static pair => pair.Key,
                                 StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Key);
                        WriteNode(writer, property.Value);
                    }

                    writer.WriteEndObject();
                    return;
                case JsonArray array:
                    writer.WriteStartArray();
                    foreach (var item in array)
                    {
                        WriteNode(writer, item);
                    }

                    writer.WriteEndArray();
                    return;
                case JsonValue scalar:
                    WriteScalar(writer, scalar);
                    return;
                default:
                    writer.WriteNullValue();
                    return;
            }
        }

        private static void WriteScalar(Utf8JsonWriter writer, JsonValue scalar)
        {
            if (scalar.TryGetValue<JsonElement>(out var element))
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.String:
                        writer.WriteStringValue(element.GetString());
                        return;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        writer.WriteBooleanValue(element.GetBoolean());
                        return;
                    case JsonValueKind.Number:
                        writer.WriteRawValue(
                            GoJsonFloat.Format(element.GetDouble()),
                            skipInputValidation: true);
                        return;
                    case JsonValueKind.Null:
                        writer.WriteNullValue();
                        return;
                }
            }

            switch (scalar.GetValueKind())
            {
                case JsonValueKind.String:
                    writer.WriteStringValue(scalar.GetValue<string>());
                    return;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    writer.WriteBooleanValue(scalar.GetValue<bool>());
                    return;
                case JsonValueKind.Number:
                    var raw = scalar.ToJsonString();
                    if (long.TryParse(
                            raw,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var integer))
                    {
                        writer.WriteNumberValue(integer);
                    }
                    else
                    {
                        writer.WriteRawValue(
                            GoJsonFloat.Format(scalar.GetValue<double>()),
                            skipInputValidation: true);
                    }

                    return;
                default:
                    writer.WriteNullValue();
                    return;
            }
        }
    }

    /// <summary>objects.ModelSettings for the per-model column.</summary>
    private sealed class ModelEntitySettingsJson
    {
        [JsonPropertyName("disableDeveloperSettingsInheritance")]
        public bool? DisableDeveloperSettingsInheritance { get; set; }

        [JsonPropertyName("associations")]
        public List<ModelAssociationJson?>? Associations { get; set; }

        [JsonPropertyName("loadBalancerStrategy")]
        public string? LoadBalancerStrategy { get; set; }

        [JsonPropertyName("traceStickyMode")]
        public string? TraceStickyMode { get; set; }
    }

    private sealed class ModelEntitySettingsWriteJson
    {
        [JsonPropertyName("disableDeveloperSettingsInheritance")]
        public bool DisableDeveloperSettingsInheritance { get; init; }

        [JsonPropertyName("associations")]
        public List<ModelAssociationWriteJson?>? Associations { get; init; }

        [JsonPropertyName("loadBalancerStrategy")]
        public string LoadBalancerStrategy { get; init; } = RoutingPolicy.Default;

        [JsonPropertyName("traceStickyMode")]
        public string TraceStickyMode { get; init; } = RoutingPolicy.Default;
    }

    private sealed class ModelSettingsJson
    {
        [JsonPropertyName("fallback_to_channels_on_model_not_found")]
        public bool? FallbackToChannelsOnModelNotFound { get; set; }

        [JsonPropertyName("query_all_channel_models")]
        public bool? QueryAllChannelModels { get; set; }

        [JsonPropertyName("default_model_api_include_all")]
        public bool? DefaultModelApiIncludeAll { get; set; }

        [JsonPropertyName("auto_reasoning_effort")]
        public bool? AutoReasoningEffort { get; set; }

        [JsonPropertyName("model_blacklist_regex")]
        public string? ModelBlacklistRegex { get; set; }

        [JsonPropertyName("hide_unroutable_models_in_list")]
        public bool? HideUnroutableModelsInList { get; set; }

        [JsonPropertyName("developer_settings")]
        public List<DeveloperSettingsJson?>? DeveloperSettings { get; set; }
    }

    private sealed class DeveloperSettingsJson
    {
        [JsonPropertyName("developer")]
        public string? Developer { get; set; }

        [JsonPropertyName("associations")]
        public List<ModelAssociationJson?>? Associations { get; set; }
    }

    private sealed class ModelAssociationJson
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("priority")]
        public int? Priority { get; set; }

        [JsonPropertyName("disabled")]
        public bool? Disabled { get; set; }

        [JsonPropertyName("when")]
        public ModelAssociationWhenJson? When { get; set; }

        [JsonPropertyName("channelModel")]
        public ChannelModelAssociationJson? ChannelModel { get; set; }

        [JsonPropertyName("channelRegex")]
        public ChannelRegexAssociationJson? ChannelRegex { get; set; }

        [JsonPropertyName("regex")]
        public RegexAssociationJson? Regex { get; set; }

        [JsonPropertyName("modelId")]
        public ModelIdAssociationJson? ModelId { get; set; }

        [JsonPropertyName("channelTagsModel")]
        public ChannelTagsModelAssociationJson? ChannelTagsModel { get; set; }

        [JsonPropertyName("channelTagsRegex")]
        public ChannelTagsRegexAssociationJson? ChannelTagsRegex { get; set; }
    }

    private sealed class ModelAssociationWhenJson
    {
        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("condition")]
        public ConditionJson? Condition { get; set; }
    }

    private sealed class ConditionJson
    {
        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("logic")]
        public string? Logic { get; set; }

        [JsonPropertyName("conditions")]
        public List<ConditionJson?>? Conditions { get; set; }

        [JsonPropertyName("field")]
        public string? Field { get; set; }

        [JsonPropertyName("operator")]
        public string? Operator { get; set; }

        [JsonPropertyName("value")]
        public JsonElement? Value { get; set; }
    }

    private sealed class ChannelModelAssociationJson
    {
        [JsonPropertyName("channelId")]
        public int? ChannelId { get; set; }

        [JsonPropertyName("modelId")]
        public string? ModelId { get; set; }
    }

    private sealed class ChannelRegexAssociationJson
    {
        [JsonPropertyName("channelId")]
        public int? ChannelId { get; set; }

        [JsonPropertyName("pattern")]
        public string? Pattern { get; set; }
    }

    private sealed class RegexAssociationJson
    {
        [JsonPropertyName("pattern")]
        public string? Pattern { get; set; }

        [JsonPropertyName("exclude")]
        public List<ExcludeAssociationJson?>? Exclude { get; set; }
    }

    private sealed class ModelIdAssociationJson
    {
        [JsonPropertyName("modelId")]
        public string? ModelId { get; set; }

        [JsonPropertyName("exclude")]
        public List<ExcludeAssociationJson?>? Exclude { get; set; }
    }

    private sealed class ChannelTagsModelAssociationJson
    {
        [JsonPropertyName("channelTags")]
        public List<string?>? ChannelTags { get; set; }

        [JsonPropertyName("modelId")]
        public string? ModelId { get; set; }
    }

    private sealed class ChannelTagsRegexAssociationJson
    {
        [JsonPropertyName("channelTags")]
        public List<string?>? ChannelTags { get; set; }

        [JsonPropertyName("pattern")]
        public string? Pattern { get; set; }
    }

    private sealed class ExcludeAssociationJson
    {
        [JsonPropertyName("channelNamePattern")]
        public string? ChannelNamePattern { get; set; }

        [JsonPropertyName("channelIds")]
        public List<int?>? ChannelIds { get; set; }

        [JsonPropertyName("channelTags")]
        public List<string?>? ChannelTags { get; set; }
    }

    private sealed class ModelSettingsWriteJson
    {
        [JsonPropertyName("fallback_to_channels_on_model_not_found")]
        public bool FallbackToChannelsOnModelNotFound { get; init; }

        [JsonPropertyName("query_all_channel_models")]
        public bool QueryAllChannelModels { get; init; }

        [JsonPropertyName("default_model_api_include_all")]
        public bool DefaultModelApiIncludeAll { get; init; }

        [JsonPropertyName("auto_reasoning_effort")]
        public bool AutoReasoningEffort { get; init; }

        [JsonPropertyName("model_blacklist_regex")]
        public string ModelBlacklistRegex { get; init; } = string.Empty;

        [JsonPropertyName("hide_unroutable_models_in_list")]
        public bool HideUnroutableModelsInList { get; init; }

        [JsonPropertyName("developer_settings")]
        public List<DeveloperSettingsWriteJson?> DeveloperSettings { get; init; } = [];
    }

    private sealed class DeveloperSettingsWriteJson
    {
        [JsonPropertyName("developer")]
        public string Developer { get; init; } = string.Empty;

        [JsonPropertyName("associations")]
        public List<ModelAssociationWriteJson?> Associations { get; init; } = [];
    }

    private sealed class ModelAssociationWriteJson
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("priority")]
        public int Priority { get; init; }

        [JsonPropertyName("disabled")]
        public bool Disabled { get; init; }

        [JsonPropertyName("when")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ModelAssociationWhenWriteJson? When { get; init; }

        [JsonPropertyName("channelModel")]
        public ChannelModelAssociationWriteJson? ChannelModel { get; init; }

        [JsonPropertyName("channelRegex")]
        public ChannelRegexAssociationWriteJson? ChannelRegex { get; init; }

        [JsonPropertyName("regex")]
        public RegexAssociationWriteJson? Regex { get; init; }

        [JsonPropertyName("modelId")]
        public ModelIdAssociationWriteJson? ModelId { get; init; }

        [JsonPropertyName("channelTagsModel")]
        public ChannelTagsModelAssociationWriteJson? ChannelTagsModel { get; init; }

        [JsonPropertyName("channelTagsRegex")]
        public ChannelTagsRegexAssociationWriteJson? ChannelTagsRegex { get; init; }
    }

    private sealed class ModelAssociationWhenWriteJson
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; }

        [JsonPropertyName("condition")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ConditionWriteJson? Condition { get; init; }
    }

    private sealed class ConditionWriteJson
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("logic")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Logic { get; init; }

        [JsonPropertyName("conditions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ConditionWriteJson>? Conditions { get; init; }

        [JsonPropertyName("field")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Field { get; init; }

        [JsonPropertyName("operator")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Operator { get; init; }

        [JsonPropertyName("value")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonNode? Value { get; init; }
    }

    private sealed class ChannelModelAssociationWriteJson
    {
        [JsonPropertyName("channelId")]
        public int ChannelId { get; init; }

        [JsonPropertyName("modelId")]
        public string ModelId { get; init; } = string.Empty;
    }

    private sealed class ChannelRegexAssociationWriteJson
    {
        [JsonPropertyName("channelId")]
        public int ChannelId { get; init; }

        [JsonPropertyName("pattern")]
        public string Pattern { get; init; } = string.Empty;
    }

    private sealed class RegexAssociationWriteJson
    {
        [JsonPropertyName("pattern")]
        public string Pattern { get; init; } = string.Empty;

        [JsonPropertyName("exclude")]
        public List<ExcludeAssociationWriteJson?>? Exclude { get; init; }
    }

    private sealed class ModelIdAssociationWriteJson
    {
        [JsonPropertyName("modelId")]
        public string ModelId { get; init; } = string.Empty;

        [JsonPropertyName("exclude")]
        public List<ExcludeAssociationWriteJson?>? Exclude { get; init; }
    }

    private sealed class ChannelTagsModelAssociationWriteJson
    {
        [JsonPropertyName("channelTags")]
        public IReadOnlyList<string>? ChannelTags { get; init; }

        [JsonPropertyName("modelId")]
        public string ModelId { get; init; } = string.Empty;
    }

    private sealed class ChannelTagsRegexAssociationWriteJson
    {
        [JsonPropertyName("channelTags")]
        public IReadOnlyList<string>? ChannelTags { get; init; }

        [JsonPropertyName("pattern")]
        public string Pattern { get; init; } = string.Empty;
    }

    private sealed class ExcludeAssociationWriteJson
    {
        [JsonPropertyName("channelNamePattern")]
        public string ChannelNamePattern { get; init; } = string.Empty;

        [JsonPropertyName("channelIds")]
        public IReadOnlyList<int>? ChannelIds { get; init; }

        [JsonPropertyName("channelTags")]
        public IReadOnlyList<string>? ChannelTags { get; init; }
    }
}
