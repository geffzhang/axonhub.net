using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of biz.QuotaRoutingSettings (internal/server/biz/system.go:157-160): the
/// system-wide default quota routing policy.
/// </summary>
public sealed record QuotaRoutingSettings(string DefaultMode)
{
    /// <summary>Port of biz.defaultQuotaRoutingSettings (system_default.go:138-140).</summary>
    public static QuotaRoutingSettings Default { get; } = new(QuotaRoutingModes.RemoveOnExhausted);
}

/// <summary>Read side of SystemService.QuotaRoutingSettingsOrDefault.</summary>
public interface IQuotaRoutingSettingsProvider
{
    /// <summary>
    /// Port of SystemService.QuotaRoutingSettingsOrDefault: every failure —
    /// missing rows, lookup errors, malformed JSON, an invalid mode — falls back
    /// to <see cref="QuotaRoutingSettings.Default"/>.
    /// </summary>
    Task<QuotaRoutingSettings> GetQuotaRoutingSettingsAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads and normalizes the <c>quota_routing_settings</c> system setting,
/// including the legacy <c>quota_enforcement_settings</c> mapping
/// (internal/server/biz/system.go:1819-1867 and
/// internal/server/biz/quota_routing_migration.go:128-142).
/// </summary>
public sealed class SystemQuotaRoutingSettingsService(ISystemStore systems)
    : IQuotaRoutingSettingsProvider
{
    /// <summary>biz.SystemKeyQuotaRoutingSettings.</summary>
    public const string QuotaRoutingSettingsKey = "quota_routing_settings";

    /// <summary>biz.legacyQuotaEnforcementSettingsKey.</summary>
    public const string LegacyQuotaEnforcementSettingsKey = "quota_enforcement_settings";

    public async Task<QuotaRoutingSettings> GetQuotaRoutingSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // QuotaRoutingSettingsOrDefault logs and returns the default on any
            // read, decode or validation failure.
            return QuotaRoutingSettings.Default;
        }
    }

    private async Task<QuotaRoutingSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var row = await systems
            .GetByKeyAsync(QuotaRoutingSettingsKey, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            var legacyRow = await systems
                .GetByKeyAsync(LegacyQuotaEnforcementSettingsKey, cancellationToken)
                .ConfigureAwait(false);

            if (legacyRow is null)
            {
                return QuotaRoutingSettings.Default;
            }

            LegacyQuotaEnforcementSettings legacy;
            try
            {
                legacy = LlmJson.Deserialize<LegacyQuotaEnforcementSettings>(legacyRow.Value)
                    ?? new LegacyQuotaEnforcementSettings();
            }
            catch (JsonException)
            {
                // The reference logs a warning and keeps the defaults.
                return QuotaRoutingSettings.Default;
            }

            return new QuotaRoutingSettings(
                QuotaRoutingModeFromLegacy(
                    legacy.Enabled,
                    legacy.ExhaustedOnly,
                    legacy.DePrioritize,
                    legacy.Mode));
        }

        var settings = LlmJson.Deserialize<QuotaRoutingSettingsDraft>(row.Value)
            ?? new QuotaRoutingSettingsDraft();
        var mode = string.IsNullOrEmpty(settings.DefaultMode)
            ? QuotaRoutingSettings.Default.DefaultMode
            : settings.DefaultMode;

        if (!QuotaRoutingModes.IsValid(mode))
        {
            // The reference returns an error; the OrDefault caller turns it into
            // the default settings.
            throw new InvalidOperationException(
                "invalid quota routing mode: \"" + mode + "\"");
        }

        return new QuotaRoutingSettings(mode!);
    }

    /// <summary>Port of quotaRoutingModeFromLegacy (quota_routing_migration.go:128-142).</summary>
    internal static string QuotaRoutingModeFromLegacy(
        bool enabled,
        bool exhaustedOnly,
        bool dePrioritize,
        string? mode)
    {
        if (!enabled)
        {
            return QuotaRoutingModes.IgnoreQuota;
        }

        if (exhaustedOnly || mode is "EXHAUSTED_ONLY" or "exhausted_only")
        {
            return QuotaRoutingModes.RemoveOnExhausted;
        }

        if (dePrioritize || mode is "DE_PRIORITIZE" or "de_prioritize")
        {
            return QuotaRoutingModes.Backpressure;
        }

        return QuotaRoutingModes.RemoveOnExhausted;
    }
}

/// <summary>JSON shape of <c>quota_routing_settings</c> (biz.QuotaRoutingSettings).</summary>
public sealed class QuotaRoutingSettingsDraft
{
    [JsonPropertyName("defaultMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DefaultMode { get; set; }
}

/// <summary>
/// JSON shape of the legacy <c>quota_enforcement_settings</c>
/// (biz.legacyQuotaEnforcementSettings, system.go:167-173).
/// </summary>
public sealed class LegacyQuotaEnforcementSettings
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("exhaustedOnly")]
    public bool ExhaustedOnly { get; set; }

    [JsonPropertyName("dePrioritize")]
    public bool DePrioritize { get; set; }

    [JsonPropertyName("allowedChannelIDs")]
    public IReadOnlyList<long>? AllowedChannelIds { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }
}
