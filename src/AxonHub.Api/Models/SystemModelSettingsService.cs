using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Persistence;

namespace AxonHub.Api.Models;

/// <summary>Read side of the reference's SystemService.ModelSettingsOrDefault.</summary>
public interface IModelSettingsProvider
{
    /// <summary>
    /// Port of SystemService.ModelSettingsOrDefault: a missing row, a lookup
    /// failure or malformed JSON falls back to <see cref="ModelSettings.Default"/>.
    /// A stored JSON <c>null</c> is a valid document and yields the zero
    /// settings after normalization, exactly like Go's encoding/json.
    /// </summary>
    Task<ModelSettings> GetModelSettingsOrDefaultAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads and normalizes the <c>system_model_settings</c> system setting
/// (internal/server/biz/system.go:1237-1291 and
/// model_settings_inheritance.go:12-32).
/// </summary>
public sealed class SystemModelSettingsService(ISystemStore systems) : IModelSettingsProvider
{
    /// <summary>biz.SystemKeyModelSettings.</summary>
    public const string ModelSettingsKey = "system_model_settings";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Go matches JSON keys to struct tags case-insensitively.
        PropertyNameCaseInsensitive = true,
    };

    public async Task<ModelSettings> GetModelSettingsOrDefaultAsync(
        CancellationToken cancellationToken = default)
    {
        SystemRecord? row;
        try
        {
            row = await systems.GetByKeyAsync(ModelSettingsKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ModelSettingsOrDefault logs and falls back on every lookup error.
            return ModelSettings.Default;
        }

        if (row is null)
        {
            // ent.IsNotFound: the default document is returned without
            // normalization (the two are identical for the default value).
            return ModelSettings.Default;
        }

        ModelSettingsJson? stored;
        try
        {
            stored = JsonSerializer.Deserialize<ModelSettingsJson>(row.Value, JsonOptions);
        }
        catch (JsonException)
        {
            // ModelSettings returns the unmarshal error; the OrDefault caller
            // then falls back to the defaults.
            return ModelSettings.Default;
        }

        // The literal "null" unmarshals into Go's zero struct without error, so
        // every flag stays false instead of taking the defaults.
        return stored is null ? new ModelSettings() : Normalize(stored);
    }

    /// <summary>Port of normalizeSystemModelSettings (nil developer list becomes empty).</summary>
    private static ModelSettings Normalize(ModelSettingsJson stored)
    {
        var developers = new List<DeveloperModelSettings>();
        foreach (var developer in stored.DeveloperSettings ?? [])
        {
            if (developer is null)
            {
                continue;
            }

            developers.Add(new DeveloperModelSettings(
                developer.Developer?.Trim() ?? string.Empty,
                developer.Associations ?? new List<JsonElement>()));
        }

        return new ModelSettings
        {
            FallbackToChannelsOnModelNotFound = stored.FallbackToChannelsOnModelNotFound ?? false,
            QueryAllChannelModels = stored.QueryAllChannelModels ?? false,
            DefaultModelApiIncludeAll = stored.DefaultModelApiIncludeAll ?? false,
            AutoReasoningEffort = stored.AutoReasoningEffort ?? false,
            ModelBlacklistRegex = stored.ModelBlacklistRegex ?? string.Empty,
            HideUnroutableModelsInList = stored.HideUnroutableModelsInList ?? false,
            DeveloperSettings = developers,
        };
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
        public List<DeveloperModelSettingsJson?>? DeveloperSettings { get; set; }
    }

    private sealed class DeveloperModelSettingsJson
    {
        [JsonPropertyName("developer")]
        public string? Developer { get; set; }

        [JsonPropertyName("associations")]
        public List<JsonElement>? Associations { get; set; }
    }
}
