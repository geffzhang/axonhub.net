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
            return await GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // QuotaRoutingSettingsOrDefault logs and returns the default on any
            // read, decode or validation failure.
            return QuotaRoutingSettings.Default;
        }
    }

    /// <summary>
    /// Port of <c>SystemService.QuotaRoutingSettings</c> (system.go:1826-1862),
    /// the strict read behind the GraphQL query: every failure except a missing
    /// legacy key (which yields the defaults) is surfaced.
    /// </summary>
    public async Task<QuotaRoutingSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        string? value;
        try
        {
            var row = await systems
                .GetByKeyAsync(QuotaRoutingSettingsKey, cancellationToken)
                .ConfigureAwait(false);
            value = row?.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to get quota routing settings: failed to get system value: " + ex.Message,
                ex);
        }

        if (value is null)
        {
            string? legacyValue;
            try
            {
                var legacyRow = await systems
                    .GetByKeyAsync(LegacyQuotaEnforcementSettingsKey, cancellationToken)
                    .ConfigureAwait(false);
                legacyValue = legacyRow?.Value;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "failed to get legacy quota enforcement settings: failed to get system value: "
                    + ex.Message,
                    ex);
            }

            if (legacyValue is null)
            {
                return QuotaRoutingSettings.Default;
            }

            LegacyQuotaEnforcementSettings legacy;
            try
            {
                legacy = LlmJson.Deserialize<LegacyQuotaEnforcementSettings>(legacyValue)
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

        QuotaRoutingSettingsDraft? settings;
        try
        {
            settings = LlmJson.Deserialize<QuotaRoutingSettingsDraft>(value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal quota routing settings: " + ex.Message,
                ex);
        }

        var mode = string.IsNullOrEmpty(settings?.DefaultMode)
            ? QuotaRoutingSettings.Default.DefaultMode
            : settings.DefaultMode;

        if (!QuotaRoutingModes.IsValid(mode))
        {
            throw new InvalidOperationException(
                "invalid quota routing mode: " + GoCast.GoQuote(mode));
        }

        return new QuotaRoutingSettings(mode!);
    }

    /// <summary>Port of <c>SystemService.SetQuotaRoutingSettings</c> (system.go:1874-1888).</summary>
    public async Task SetAsync(
        QuotaRoutingSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var mode = string.IsNullOrEmpty(settings.DefaultMode)
            ? QuotaRoutingSettings.Default.DefaultMode
            : settings.DefaultMode;
        if (!QuotaRoutingModes.IsValid(mode))
        {
            throw new InvalidOperationException(
                "invalid quota routing mode: " + GoCast.GoQuote(mode));
        }

        // json.Marshal(QuotaRoutingSettings): a single field, always present.
        var json = AxonHub.Llm.Json.GoJson.MarshalToString(
            new QuotaRoutingSettingsDraft { DefaultMode = mode });

        try
        {
            await systems.SetAsync(QuotaRoutingSettingsKey, json, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // setSystemValue's own prefix is the only wrapper here.
            throw new InvalidOperationException(
                "failed to create system setting: " + ex.Message,
                ex);
        }
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
