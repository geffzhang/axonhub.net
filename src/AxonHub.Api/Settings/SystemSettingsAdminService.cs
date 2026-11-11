using System.Globalization;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Security;
using AxonHub.Persistence;

namespace AxonHub.Api.Settings;

/// <summary>
/// Strict read/write facade over the <c>systems</c> key/value table, ported from
/// the system-settings methods of internal/server/biz/system.go. Unlike the
/// *OrDefault providers used by the runtime, every method here surfaces lookup
/// and decode failures to the management GraphQL resolvers.
/// </summary>
public interface ISystemSettingsAdminService
{
    /// <summary>Port of SystemService.IsInitialized.</summary>
    Task<bool> IsInitializedAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.RetryPolicy.</summary>
    Task<RetryPolicy> GetRetryPolicyAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.ModelSettings.</summary>
    Task<ModelSettings> GetModelSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.SecuritySettings.</summary>
    Task<SecuritySettings> GetSecuritySettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.SetRetryPolicy.</summary>
    Task SetRetryPolicyAsync(RetryPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.SetModelSettings.</summary>
    Task SetModelSettingsAsync(ModelSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.SetSecuritySettings.</summary>
    Task SetSecuritySettingsAsync(
        SecuritySettings settings,
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.StoragePolicy.</summary>
    Task<StoragePolicy> GetStoragePolicyAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.SetStoragePolicy.</summary>
    Task SetStoragePolicyAsync(
        StoragePolicy policy,
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.DefaultDataStorageID (system.go:1590-1608).</summary>
    Task<long> GetDefaultDataStorageIdAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.SetDefaultDataStorageID (system.go:1610-1613).</summary>
    Task SetDefaultDataStorageIdAsync(
        long id,
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.AutoBackupSettings (system.go:1604-1640).</summary>
    Task<AutoBackupSettings> GetAutoBackupSettingsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.SetAutoBackupSettings (system.go:1642-1649).</summary>
    Task SetAutoBackupSettingsAsync(
        AutoBackupSettings settings,
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.UpdateAutoBackupLastRun (system.go:1651-1670).</summary>
    Task UpdateAutoBackupLastRunAsync(
        string lastError,
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.GeneralSettings (system.go:1544-1569).</summary>
    Task<SystemGeneralSettings> GetGeneralSettingsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.SetGeneralSettings (system.go:1571-1588).</summary>
    Task SetGeneralSettingsAsync(
        SystemGeneralSettings settings,
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.ChannelSetting (system.go:1319-1338).</summary>
    Task<SystemChannelSettings> GetChannelSettingsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of SystemService.UpdateChannelSetting (system.go:1385-1462): the
    /// five-attempt compare-and-set loop that keeps concurrent patches.
    /// </summary>
    Task UpdateChannelSettingsAsync(
        SystemChannelSettingsPatch patch,
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.BrandName (system.go:877-892).</summary>
    Task<string> GetBrandNameAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.BrandLogo (system.go:899-914).</summary>
    Task<string> GetBrandLogoAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.Title (system.go:921-936).</summary>
    Task<string> GetTitleAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.SetBrandName (system.go:894-897).</summary>
    Task SetBrandNameAsync(string brandName, CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.SetBrandLogo (system.go:916-919).</summary>
    Task SetBrandLogoAsync(string brandLogo, CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.SetTitle (system.go:938-941).</summary>
    Task SetTitleAsync(string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of SystemService.UserAgentPassThrough (system.go:1741-1754): a
    /// missing row is false and only the exact string "true" enables the flag.
    /// </summary>
    Task<bool> GetUserAgentPassThroughAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.SetUserAgentPassThrough (system.go:1756-1764).</summary>
    Task SetUserAgentPassThroughAsync(
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.PassThrough (system.go:1766-1781).</summary>
    Task<bool> GetPassThroughAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.SetPassThrough (system.go:1783-1791).</summary>
    Task SetPassThroughAsync(bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of SystemService.InjectUsageCostEnabled (system.go:1793-1806).
    /// </summary>
    Task<bool> GetInjectUsageCostEnabledAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of SystemService.SetInjectUsageCostEnabled (system.go:1808-1817).
    /// </summary>
    Task SetInjectUsageCostEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of SystemService.VideoStorageSettings (system.go:1678-1695): a
    /// missing row returns the defaults and non-positive scan values are
    /// normalized to 1 / 50.
    /// </summary>
    Task<VideoStorageSettings> GetVideoStorageSettingsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of SystemService.SetVideoStorageSettings (system.go:1697-1740):
    /// normalizes the scan values, validates the target data storage and
    /// writes the document.
    /// </summary>
    Task SetVideoStorageSettingsAsync(
        VideoStorageSettings settings,
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.ProxyPresets (system_proxy.go:26-39).</summary>
    Task<IReadOnlyList<ProxyPreset>> GetProxyPresetsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of SystemService.SaveProxyPreset (system_proxy.go:42-67): upserts
    /// by URL, replacing the existing entry in place.
    /// </summary>
    Task SaveProxyPresetAsync(ProxyPreset preset, CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.DeleteProxyPreset (system_proxy.go:70-94).</summary>
    Task DeleteProxyPresetAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of SystemService.OnboardingInfo (system_onboarding.go:31-48): the
    /// missing row is null, not an error.
    /// </summary>
    Task<OnboardingRecord?> GetOnboardingInfoAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.CompleteOnboarding (system_onboarding.go:61-81).</summary>
    Task CompleteOnboardingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of SystemService.CompleteSystemModelSettingOnboarding
    /// (system_onboarding.go:84-98).
    /// </summary>
    Task CompleteSystemModelSettingOnboardingAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of SystemService.CompleteAutoDisableChannelOnboarding
    /// (system_onboarding.go:101-115).
    /// </summary>
    Task CompleteAutoDisableChannelOnboardingAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of SystemService.WebhookNotifierConfig (system.go:1188-1209): a
    /// missing row returns the normalized empty configuration.
    /// </summary>
    Task<WebhookNotifierConfig> GetWebhookNotifierConfigAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of SystemService.SetWebhookNotifierConfig (system.go:1225-1234):
    /// normalizes both lists and writes the marshalled document.
    /// </summary>
    Task SetWebhookNotifierConfigAsync(
        WebhookNotifierConfig config,
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.CatalogSettings (catalog_settings.go:80-98).</summary>
    Task<CatalogSettings> GetCatalogSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.CatalogSettingsOrDefault: read failures fall back to defaults.</summary>
    Task<CatalogSettings> GetCatalogSettingsOrDefaultAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of SystemService.SetCatalogSettings (catalog_settings.go:107-123):
    /// normalize, validate, then write the marshalled document.
    /// </summary>
    Task SetCatalogSettingsAsync(
        CatalogSettings settings,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISystemSettingsAdminService"/>
/// <param name="systems">The systems key/value store.</param>
/// <param name="dataStorages">
/// The data_storages store used by the video-storage target validation. Hosts
/// always register it; the optional default keeps unit construction that does
/// not touch video storage unchanged.
/// </param>
public sealed class SystemSettingsAdminService(
    ISystemStore systems,
    IDataStorageStore? dataStorages = null) : ISystemSettingsAdminService
{
    /// <summary>biz.SystemKeyInitialized.</summary>
    public const string InitializedKey = "system_initialized";

    /// <summary>biz.SystemKeyDefaultDataStorage (system.go:80).</summary>
    public const string DefaultDataStorageIdKey = "default_data_storage_id";

    /// <summary>biz.SystemKeyVideoStorageSettings (system.go:104).</summary>
    public const string VideoStorageSettingsKey = "system_video_storage_settings";

    /// <summary>biz.SystemKeyOnboarded (system.go:84).</summary>
    public const string OnboardedKey = "system_onboarded";

    /// <summary>datastorage.TypeDatabase.</summary>
    private const string DatabaseStorageType = "database";

    /// <summary>maxChannelSettingUpdateAttempts (system.go:37).</summary>
    public const int MaxChannelSettingUpdateAttempts = 5;

    public async Task<bool> IsInitializedAsync(CancellationToken cancellationToken = default)
    {
        // IsInitialized queries ent directly (no getSystemValue wrapper) and
        // compares case-insensitively, so "TRUE" is initialized.
        var row = await systems.GetByKeyAsync(InitializedKey, cancellationToken)
            .ConfigureAwait(false);

        return row is not null
            && string.Equals(row.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<RetryPolicy> GetRetryPolicyAsync(
        CancellationToken cancellationToken = default)
    {
        var row = await GetValueAsync(
                RetryPolicyCodec.RetryPolicyKey,
                "failed to get retry policy",
                cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            // RetryPolicy normalizes the default before returning it.
            return RetryPolicyCodec.Normalize(RetryPolicy.Default);
        }

        try
        {
            return RetryPolicyCodec.Deserialize(row.Value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal retry policy: " + ex.Message,
                ex);
        }
    }

    public async Task<ModelSettings> GetModelSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var row = await GetValueAsync(
                ModelSettingsCodec.ModelSettingsKey,
                "failed to get model settings",
                cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            // ent.IsNotFound: the default document is returned as-is.
            return ModelSettings.Default;
        }

        try
        {
            return ModelSettingsCodec.Deserialize(row.Value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal model settings: " + ex.Message,
                ex);
        }
    }

    public async Task<SecuritySettings> GetSecuritySettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var row = await GetValueAsync(
                SecuritySettingsCodec.SecuritySettingsKey,
                "failed to get security settings",
                cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return SecuritySettings.Default;
        }

        try
        {
            return SecuritySettingsCodec.Deserialize(row.Value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal security settings: " + ex.Message,
                ex);
        }
    }

    public async Task SetRetryPolicyAsync(
        RetryPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var normalized = RetryPolicyCodec.Normalize(policy);

        IReadOnlyList<AutoDisableRule> rules;
        try
        {
            rules = RetryPolicyCodec.NormalizeAutoDisableRules(
                normalized.AutoDisableChannel.Rules,
                allowDelete: false);
        }
        catch (AutoDisableRuleValidationException ex)
        {
            // The global auto-disable rules are validated by a coded error; the
            // presenter reports only this message, not the resolver prefix.
            throw new CodedException(CodedException.ValidationFailed, ex.Message);
        }

        normalized = normalized with
        {
            AutoDisableChannel = normalized.AutoDisableChannel with { Rules = rules },
        };

        await SetValueAsync(
                RetryPolicyCodec.RetryPolicyKey,
                RetryPolicyCodec.Serialize(normalized),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetModelSettingsAsync(
        ModelSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var regexError = XRegexp.ValidateRegex(settings.ModelBlacklistRegex);
        if (regexError is not null)
        {
            throw new InvalidOperationException(
                "invalid model blacklist regex: " + regexError);
        }

        var normalized = ModelSettingsCodec.Normalize(settings);
        ModelSettingsCodec.Validate(normalized);

        await SetValueAsync(
                ModelSettingsCodec.ModelSettingsKey,
                ModelSettingsCodec.Serialize(normalized),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetSecuritySettingsAsync(
        SecuritySettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = SecuritySettingsCodec.Normalize(settings);

        await SetValueAsync(
                SecuritySettingsCodec.SecuritySettingsKey,
                SecuritySettingsCodec.Serialize(normalized),
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<StoragePolicy> GetStoragePolicyAsync(
        CancellationToken cancellationToken = default)
    {
        var row = await GetValueAsync(
                StoragePolicyCodec.Key,
                "failed to get storage policy",
                cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            // ent.IsNotFound: the default policy is returned as-is (already
            // carrying the merged cleanup list).
            return StoragePolicy.Default;
        }

        try
        {
            return StoragePolicyCodec.Deserialize(row.Value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal storage policy: " + ex.Message,
                ex);
        }
    }

    public async Task SetStoragePolicyAsync(
        StoragePolicy policy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);

        foreach (var option in policy.CleanupOptions ?? [])
        {
            if (option.CleanupDays <= 0)
            {
                throw new InvalidOperationException(
                    "cleanup_days for \"" + option.ResourceType
                    + "\" must be positive; set enabled=false to keep data forever");
            }
        }

        string value;
        try
        {
            value = StoragePolicyCodec.Serialize(policy);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to marshal storage policy: " + ex.Message,
                ex);
        }

        await SetValueAsync(StoragePolicyCodec.Key, value, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<long> GetDefaultDataStorageIdAsync(
        CancellationToken cancellationToken = default)
    {
        // getSystemValue wraps the lookup; DefaultDataStorageID adds its own
        // operation prefix, so a storage error carries both.
        var row = await GetValueAsync(
                DefaultDataStorageIdKey,
                "failed to get default data storage ID",
                cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            // ent.IsNotFound: not set means "no default storage".
            return 0;
        }

        try
        {
            return ParseDefaultDataStorageId(row.Value);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "failed to parse default data storage ID: " + ex.Message,
                ex);
        }
    }

    public async Task SetDefaultDataStorageIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        await SetValueAsync(
                DefaultDataStorageIdKey,
                id.ToString(CultureInfo.InvariantCulture),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Port of <c>fmt.Sscanf(value, "%d", &amp;id)</c>: skip spaces, take an
    /// optional sign and at least one decimal digit, ignore trailing input.
    /// The reference reports <c>EOF</c> for exhausted input, the scanning
    /// library's <c>expected integer</c> for a syntactic miss and
    /// <c>value out of range</c> on overflow.
    /// </summary>
    private static long ParseDefaultDataStorageId(string value)
    {
        var index = 0;
        while (index < value.Length && IsGoScanSpace(value[index]))
        {
            index++;
        }

        if (index == value.Length)
        {
            throw new FormatException("EOF");
        }

        var start = index;
        if (value[index] is '+' or '-')
        {
            index++;
        }

        var digitsStart = index;
        while (index < value.Length && value[index] is >= '0' and <= '9')
        {
            index++;
        }

        if (index == digitsStart)
        {
            throw new FormatException("expected integer");
        }

        var token = value[start..index];
        try
        {
            return long.Parse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        }
        catch (OverflowException ex)
        {
            throw new FormatException("value out of range", ex);
        }
    }

    private static bool IsGoScanSpace(char c)
        => c is ' ' or '\t' or '\n' or '\r' or '\v' or '\f' or '\u0085' or '\u00A0';

    public async Task<AutoBackupSettings> GetAutoBackupSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var row = await GetValueAsync(
                AutoBackupSettingsCodec.Key,
                "failed to get auto backup settings",
                cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            // ent.IsNotFound: the defaults are returned as-is.
            return AutoBackupSettingsCodec.Default;
        }

        try
        {
            return AutoBackupSettingsCodec.Deserialize(row.Value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal auto backup settings: " + ex.Message,
                ex);
        }
    }

    public async Task SetAutoBackupSettingsAsync(
        AutoBackupSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string value;
        try
        {
            value = AutoBackupSettingsCodec.Serialize(settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to marshal auto backup settings: " + ex.Message,
                ex);
        }

        try
        {
            await systems.SetAsync(AutoBackupSettingsCodec.Key, value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to set auto backup settings: " + ex.Message,
                ex);
        }
    }

    public async Task UpdateAutoBackupLastRunAsync(
        string lastError,
        CancellationToken cancellationToken = default)
    {
        var settings = await GetAutoBackupSettingsAsync(cancellationToken)
            .ConfigureAwait(false);

        // The reference stamps time.Now(); the port stores UTC (C198).
        var updated = settings with
        {
            LastBackupAt = DateTime.UtcNow,
            LastBackupError = lastError,
        };

        await SetAutoBackupSettingsAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SystemGeneralSettings> GetGeneralSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var row = await GetValueAsync(
                SystemConfigBackupKeys.GeneralSettings,
                "failed to get general settings",
                cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            // ent.IsNotFound: the defaults are returned as-is.
            return SystemSettingsDefaults.General;
        }

        SystemGeneralSettings settings;
        try
        {
            settings = SystemSettingsCodec.DeserializeGeneral(row.Value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal general settings: " + ex.Message,
                ex);
        }

        // GeneralSettings normalizes only the two empty strings.
        if (settings.CurrencyCode.Length == 0)
        {
            settings = settings with
            {
                CurrencyCode = SystemSettingsDefaults.General.CurrencyCode,
            };
        }

        if (settings.Timezone.Length == 0)
        {
            settings = settings with { Timezone = SystemSettingsDefaults.General.Timezone };
        }

        return settings;
    }

    public async Task SetGeneralSettingsAsync(
        SystemGeneralSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string value;
        try
        {
            value = SystemSettingsCodec.SerializeGeneral(settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to marshal general settings: " + ex.Message,
                ex);
        }

        try
        {
            await SetValueAsync(
                    SystemConfigBackupKeys.GeneralSettings,
                    value,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to set general settings: " + ex.Message,
                ex);
        }
    }

    public async Task<SystemChannelSettings> GetChannelSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var row = await GetValueAsync(
                SystemConfigBackupKeys.ChannelSettings,
                "failed to get channel setting",
                cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            // ent.IsNotFound: ChannelSetting returns the default as-is.
            return SystemSettingsDefaults.Channel;
        }

        try
        {
            return SystemSettingsCodec.Normalize(
                SystemSettingsCodec.DeserializeChannel(row.Value));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal channel setting: " + ex.Message,
                ex);
        }
    }

    public async Task UpdateChannelSettingsAsync(
        SystemChannelSettingsPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);

        for (var attempt = 0; attempt < MaxChannelSettingUpdateAttempts; attempt++)
        {
            SystemRecord? current;
            try
            {
                // The loop queries ent directly, so a lookup error is not
                // wrapped by getSystemValue.
                current = await systems
                    .GetByKeyAsync(SystemConfigBackupKeys.ChannelSettings, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to get channel setting: " + ex.Message,
                    ex);
            }

            if (current is null)
            {
                var created = SystemSettingsCodec.Normalize(
                    SystemSettingsCodec.ApplyPatch(SystemSettingsDefaults.Channel, patch));
                SystemSettingsCodec.Validate(created);

                var createdJson = SerializeChannelSetting(created);

                bool inserted;
                try
                {
                    inserted = await systems.TryCreateAsync(
                            new SystemRecord
                            {
                                Key = SystemConfigBackupKeys.ChannelSettings,
                                Value = createdJson,
                                CreatedAt = DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow,
                            },
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "failed to create channel setting: " + ex.Message,
                        ex);
                }

                if (!inserted)
                {
                    // ent.IsConstraintError: a concurrent insert won; retry.
                    continue;
                }

                return;
            }

            SystemChannelSettings setting;
            try
            {
                setting = SystemSettingsCodec.DeserializeChannel(current.Value);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "failed to unmarshal channel setting: " + ex.Message,
                    ex);
            }

            setting = SystemSettingsCodec.Normalize(setting);
            setting = SystemSettingsCodec.ApplyPatch(setting, patch);
            setting = SystemSettingsCodec.Normalize(setting);
            SystemSettingsCodec.Validate(setting);

            var json = SerializeChannelSetting(setting);
            if (string.Equals(json, current.Value, StringComparison.Ordinal))
            {
                // No write when normalization and the patch reproduce the row.
                return;
            }

            int affected;
            try
            {
                affected = await systems.UpdateValueIfUnchangedAsync(
                        current.Id,
                        current.UpdatedAt,
                        current.Value,
                        json,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to update channel setting: " + ex.Message,
                    ex);
            }

            if (affected == 0)
            {
                continue;
            }

            return;
        }

        throw new InvalidOperationException(
            "failed to update channel setting after "
            + MaxChannelSettingUpdateAttempts
            + " concurrent attempts");
    }

    public Task<string> GetBrandNameAsync(CancellationToken cancellationToken = default)
        => GetBrandValueAsync(
            SystemConfigBackupKeys.BrandName,
            "failed to get brand name",
            cancellationToken);

    public Task<string> GetBrandLogoAsync(CancellationToken cancellationToken = default)
        => GetBrandValueAsync(
            SystemConfigBackupKeys.BrandLogo,
            "failed to get brand logo",
            cancellationToken);

    public Task<string> GetTitleAsync(CancellationToken cancellationToken = default)
        => GetBrandValueAsync(
            SystemConfigBackupKeys.Title,
            "failed to get title",
            cancellationToken);

    public Task SetBrandNameAsync(
        string brandName,
        CancellationToken cancellationToken = default)
        => SetValueAsync(SystemConfigBackupKeys.BrandName, brandName, cancellationToken);

    public Task SetBrandLogoAsync(
        string brandLogo,
        CancellationToken cancellationToken = default)
        => SetValueAsync(SystemConfigBackupKeys.BrandLogo, brandLogo, cancellationToken);

    public Task SetTitleAsync(string title, CancellationToken cancellationToken = default)
        => SetValueAsync(SystemConfigBackupKeys.Title, title, cancellationToken);

    public async Task<bool> GetUserAgentPassThroughAsync(
        CancellationToken cancellationToken = default)
        => await GetFlagAsync(
                SystemConfigBackupKeys.UserAgentPassThrough,
                "failed to get user-agent pass-through",
                cancellationToken)
            .ConfigureAwait(false);

    public Task SetUserAgentPassThroughAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
        => SetValueAsync(
            SystemConfigBackupKeys.UserAgentPassThrough,
            enabled ? "true" : "false",
            cancellationToken);

    public async Task<bool> GetPassThroughAsync(
        CancellationToken cancellationToken = default)
        => await GetFlagAsync(
                SystemConfigBackupKeys.PassThrough,
                "failed to get pass-through",
                cancellationToken)
            .ConfigureAwait(false);

    public Task SetPassThroughAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
        => SetValueAsync(
            SystemConfigBackupKeys.PassThrough,
            enabled ? "true" : "false",
            cancellationToken);

    public async Task<bool> GetInjectUsageCostEnabledAsync(
        CancellationToken cancellationToken = default)
        => await GetFlagAsync(
                SystemConfigBackupKeys.InjectUsageCost,
                "failed to get inject usage cost",
                cancellationToken)
            .ConfigureAwait(false);

    public Task SetInjectUsageCostEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
        => SetValueAsync(
            SystemConfigBackupKeys.InjectUsageCost,
            enabled ? "true" : "false",
            cancellationToken);

    public async Task<VideoStorageSettings> GetVideoStorageSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var row = await GetValueAsync(
                VideoStorageSettingsKey,
                "failed to get video storage settings",
                cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            // ent.IsNotFound: the defaults are returned as-is.
            return SystemSettingsDefaults.VideoStorage;
        }

        VideoStorageSettings settings;
        try
        {
            settings = SystemSettingsCodec.DeserializeVideoStorage(row.Value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal video storage settings: " + ex.Message,
                ex);
        }

        return NormalizeVideoStorage(settings);
    }

    public async Task SetVideoStorageSettingsAsync(
        VideoStorageSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings = NormalizeVideoStorage(settings);

        if (settings.Enabled)
        {
            if (settings.DataStorageId == 0)
            {
                throw new InvalidOperationException(
                    "data_storage_id is required when video storage is enabled");
            }

            DataStorageRecord? dataStorage;
            try
            {
                if (dataStorages is null)
                {
                    throw new InvalidOperationException("data storage store is not configured");
                }

                dataStorage = await dataStorages
                    .GetByIdAsync(settings.DataStorageId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to get data storage: " + ex.Message,
                    ex);
            }

            if (dataStorage is null)
            {
                // DataStorageManagementService.DataStorageNotFound: ent's
                // NotFoundError text for the data_storages table.
                throw new InvalidOperationException(
                    "failed to get data storage: "
                    + DataStorageManagementService.DataStorageNotFound);
            }

            if (dataStorage.Primary
                || string.Equals(dataStorage.Type, DatabaseStorageType, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "video storage must use a non-database data storage");
            }
        }

        string value;
        try
        {
            value = SystemSettingsCodec.SerializeVideoStorage(settings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to marshal video storage settings: " + ex.Message,
                ex);
        }

        try
        {
            await SetValueAsync(VideoStorageSettingsKey, value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to set video storage settings: " + ex.Message,
                ex);
        }
    }

    public async Task<IReadOnlyList<ProxyPreset>> GetProxyPresetsAsync(
        CancellationToken cancellationToken = default)
    {
        var row = await GetValueAsync(
                SystemConfigBackupKeys.ProxyPresets,
                "failed to get proxy presets",
                cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            // ent.IsNotFound: an empty, non-nil list.
            return [];
        }

        try
        {
            return SystemSettingsCodec.DeserializeProxyPresets(row.Value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal proxy presets: " + ex.Message,
                ex);
        }
    }

    public async Task SaveProxyPresetAsync(
        ProxyPreset preset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preset);

        // The reference returns ProxyPresets errors unchanged here.
        var presets = new List<ProxyPreset>(
            await GetProxyPresetsAsync(cancellationToken).ConfigureAwait(false));

        var found = false;
        for (var i = 0; i < presets.Count; i++)
        {
            if (string.Equals(presets[i].Url, preset.Url, StringComparison.Ordinal))
            {
                presets[i] = preset;
                found = true;

                break;
            }
        }

        if (!found)
        {
            presets.Add(preset);
        }

        string value;
        try
        {
            value = SystemSettingsCodec.SerializeProxyPresets(presets);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to marshal proxy presets: " + ex.Message,
                ex);
        }

        // setSystemValue errors pass through with their own prefix.
        await SetValueAsync(SystemConfigBackupKeys.ProxyPresets, value, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteProxyPresetAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);

        var presets = await GetProxyPresetsAsync(cancellationToken).ConfigureAwait(false);

        var filtered = new List<ProxyPreset>(presets.Count);
        foreach (var preset in presets)
        {
            if (!string.Equals(preset.Url, url, StringComparison.Ordinal))
            {
                filtered.Add(preset);
            }
        }

        string value;
        try
        {
            value = SystemSettingsCodec.SerializeProxyPresets(filtered);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to marshal proxy presets: " + ex.Message,
                ex);
        }

        await SetValueAsync(SystemConfigBackupKeys.ProxyPresets, value, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OnboardingRecord?> GetOnboardingInfoAsync(
        CancellationToken cancellationToken = default)
    {
        var row = await GetValueAsync(
                OnboardedKey,
                "failed to get onboarding info",
                cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            // ent.IsNotFound: not set yet.
            return null;
        }

        try
        {
            return SystemSettingsCodec.DeserializeOnboarding(row.Value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal onboarding info: " + ex.Message,
                ex);
        }
    }

    public async Task CompleteOnboardingAsync(CancellationToken cancellationToken = default)
    {
        var info = await GetExistingOnboardingInfoAsync(cancellationToken)
                .ConfigureAwait(false)
            ?? new OnboardingRecord();

        // time.Now(): one instant shared by the record and the auto-completed
        // module.
        var now = DateTimeOffset.Now;

        info = info with { Onboarded = true, CompletedAt = now };

        // For new users the auto-disable-channel module is completed in the
        // main flow; an existing module keeps its timestamp.
        if (info.AutoDisableChannel is null)
        {
            info = info with
            {
                AutoDisableChannel = new OnboardingModule
                {
                    Onboarded = true,
                    CompletedAt = now,
                },
            };
        }

        await SetOnboardingInfoAsync(info, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteSystemModelSettingOnboardingAsync(
        CancellationToken cancellationToken = default)
    {
        var info = await GetExistingOnboardingInfoAsync(cancellationToken)
                .ConfigureAwait(false)
            ?? new OnboardingRecord();

        info = info with
        {
            SystemModelSetting = new OnboardingModule
            {
                Onboarded = true,
                CompletedAt = DateTimeOffset.Now,
            },
        };

        await SetOnboardingInfoAsync(info, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAutoDisableChannelOnboardingAsync(
        CancellationToken cancellationToken = default)
    {
        var info = await GetExistingOnboardingInfoAsync(cancellationToken)
                .ConfigureAwait(false)
            ?? new OnboardingRecord();

        info = info with
        {
            AutoDisableChannel = new OnboardingModule
            {
                Onboarded = true,
                CompletedAt = DateTimeOffset.Now,
            },
        };

        await SetOnboardingInfoAsync(info, cancellationToken).ConfigureAwait(false);
    }

    public async Task<WebhookNotifierConfig> GetWebhookNotifierConfigAsync(
        CancellationToken cancellationToken = default)
    {
        var row = await GetValueAsync(
                SystemConfigBackupKeys.WebhookNotifierConfig,
                "failed to get webhook notifier config",
                cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            // ent.IsNotFound: the normalized zero configuration.
            return WebhookNotifierConfigCodec.Default;
        }

        try
        {
            return WebhookNotifierConfigCodec.Deserialize(row.Value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal webhook notifier config: " + ex.Message,
                ex);
        }
    }

    public async Task<CatalogSettings> GetCatalogSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        var row = await GetValueAsync(
                CatalogSettingsRules.Key,
                "failed to get catalog settings",
                cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            // ent.IsNotFound: defaultCatalogSettings (upstream URL "", 3600).
            return CatalogSettingsRules.Default;
        }

        try
        {
            return CatalogSettingsCodec.Deserialize(row.Value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal catalog settings: " + ex.Message,
                ex);
        }
    }

    public async Task<CatalogSettings> GetCatalogSettingsOrDefaultAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetCatalogSettingsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The reference swallows every read failure, cancellation included.
            return CatalogSettingsRules.Default;
        }
    }

    public async Task SetCatalogSettingsAsync(
        CatalogSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var normalized = CatalogSettingsRules.Normalize(settings);
        var validation = CatalogSettingsRules.Validate(normalized);
        if (validation is not null)
        {
            throw new InvalidOperationException(validation);
        }

        var value = CatalogSettingsCodec.Serialize(normalized);
        await SetValueAsync(CatalogSettingsRules.Key, value, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SetWebhookNotifierConfigAsync(
        WebhookNotifierConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        var normalized = WebhookNotifierConfigCodec.Normalize(config);

        string value;
        try
        {
            value = WebhookNotifierConfigCodec.Serialize(normalized);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to marshal webhook notifier config: " + ex.Message,
                ex);
        }

        await SetValueAsync(
                SystemConfigBackupKeys.WebhookNotifierConfig,
                value,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static VideoStorageSettings NormalizeVideoStorage(VideoStorageSettings settings)
    {
        if (settings.ScanIntervalMinutes <= 0)
        {
            settings = settings with
            {
                ScanIntervalMinutes = SystemSettingsDefaults.VideoStorage.ScanIntervalMinutes,
            };
        }

        if (settings.ScanLimit <= 0)
        {
            settings = settings with
            {
                ScanLimit = SystemSettingsDefaults.VideoStorage.ScanLimit,
            };
        }

        return settings;
    }

    /// <summary>
    /// The Complete* methods wrap OnboardingInfo failures with
    /// "failed to get existing onboarding info: " (system_onboarding.go:64-67).
    /// </summary>
    private async Task<OnboardingRecord?> GetExistingOnboardingInfoAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await GetOnboardingInfoAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get existing onboarding info: " + ex.Message,
                ex);
        }
    }

    private async Task SetOnboardingInfoAsync(
        OnboardingRecord info,
        CancellationToken cancellationToken)
    {
        string value;
        try
        {
            value = SystemSettingsCodec.SerializeOnboarding(info);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to marshal onboarding info: " + ex.Message,
                ex);
        }

        await SetValueAsync(OnboardedKey, value, cancellationToken).ConfigureAwait(false);
    }

    private static string SerializeChannelSetting(SystemChannelSettings setting)
    {
        try
        {
            return SystemSettingsCodec.SerializeChannel(setting);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to marshal channel setting: " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Port of BrandName/BrandLogo/Title: the reference queries ent directly,
    /// so the not-found row becomes "" without the getSystemValue wrapper.
    /// </summary>
    private async Task<string> GetBrandValueAsync(
        string key,
        string scope,
        CancellationToken cancellationToken)
    {
        try
        {
            var row = await systems.GetByKeyAsync(key, cancellationToken)
                .ConfigureAwait(false);
            return row?.Value ?? string.Empty;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(scope + ": " + ex.Message, ex);
        }
    }

    private async Task<bool> GetFlagAsync(
        string key,
        string scope,
        CancellationToken cancellationToken)
    {
        var row = await GetValueAsync(key, scope, cancellationToken).ConfigureAwait(false);

        // Missing means false; only the exact stored "true" enables the flag.
        return row is not null && string.Equals(row.Value, "true", StringComparison.Ordinal);
    }

    private async Task<SystemRecord?> GetValueAsync(
        string key,
        string scope,
        CancellationToken cancellationToken)
    {
        try
        {
            return await systems.GetByKeyAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation must stay distinguishable from a storage failure:
            // callers that swallow the wrapped error would otherwise swallow a
            // cancelled request too.
            throw;
        }
        catch (Exception ex)
        {
            // getSystemValue wraps the storage error; the caller adds its own
            // operation prefix (e.g. "failed to get retry policy: ...").
            throw new InvalidOperationException(
                scope + ": failed to get system value: " + ex.Message,
                ex);
        }
    }

    private async Task SetValueAsync(
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        try
        {
            await systems.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to create system setting: " + ex.Message,
                ex);
        }
    }
}
