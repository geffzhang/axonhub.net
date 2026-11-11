using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Json;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging;
using GoJson = AxonHub.Llm.Json.GoJson;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of biz.quotaRoutingMigrator (internal/server/biz/quota_routing_migration.go):
/// the one-shot startup data migration that carries the legacy quota
/// enforcement settings over to the quota routing settings key and marks the
/// channels the legacy configuration allowed.
/// </summary>
/// <remarks>
/// Everything runs in one transaction owned by <see cref="ISystemStore"/>:
/// a per-instance claim row makes concurrent starts idempotent, the legacy
/// value is decoded once, <c>quota_routing_settings</c> is created only when it
/// is still missing, every still-live <c>allowedChannelIDs</c> channel gets
/// <c>quotaRoutingMode = ignore_quota</c> through
/// <see cref="ChannelLifecycleService.UpdateAsync"/> (the reference calls
/// ChannelService.UpdateChannel, so its validation and serialization rules
/// apply), and the marker finally becomes <c>"true"</c>. Any failure rolls the
/// whole transaction back, leaving the migration retryable on the next start.
/// </remarks>
public sealed class QuotaRoutingMigrationService(
    ISystemStore systems,
    IChannelStore channelStore,
    ChannelLifecycleService lifecycle,
    ILogger<QuotaRoutingMigrationService> logger)
{
    /// <summary>Port of biz.SystemKeyQuotaRoutingMigrationDone.</summary>
    public const string MigrationDoneKey = "quota_routing_migration_done";

    private static readonly Action<ILogger, long, Exception?> LogSkippedMissingChannel =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(0, "QuotaRoutingMigrationSkippedChannel"),
            "quota routing migration skipped missing channel: channel_id={ChannelId}");

    private static readonly Action<ILogger, string, string, Exception?> LogMigrated =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(0, "QuotaRoutingMigrationCompleted"),
            "migrated quota routing settings: channel_ids={ChannelIds} default_mode={DefaultMode}");

    /// <summary>Port of quotaRoutingMigrator.Migrate.</summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        // authz.RunWithSystemBypass has no port: stores are already
        // system-scoped. system.RunInTransaction supplies the transaction.
        await systems
            .RunInTransactionAsync(MigrateInTransactionAsync, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task MigrateInTransactionAsync(CancellationToken cancellationToken)
    {
        // uuid.NewString(): lowercase, hyphenated.
        var claim = Guid.NewGuid().ToString();

        try
        {
            await systems
                .CreateIfAbsentAsync(MigrationDoneKey, claim, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to claim quota routing migration: " + ex.Message,
                ex);
        }

        SystemRecord? marker;
        try
        {
            marker = await systems
                .GetByKeyAsync(MigrationDoneKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to read quota routing migration marker: " + ex.Message,
                ex);
        }

        if (marker is null)
        {
            // ent's Only() reports a missing (or soft-deleted) row as not found.
            throw new InvalidOperationException(
                "failed to read quota routing migration marker: ent: system not found");
        }

        if (!string.Equals(marker.Value, claim, StringComparison.Ordinal))
        {
            // Another instance won the claim; its transaction will finish the
            // migration, or already has.
            return;
        }

        var legacyValue = await GetSystemValueAsync(
                SystemQuotaRoutingSettingsService.LegacyQuotaEnforcementSettingsKey,
                "failed to read legacy quota enforcement settings: ",
                cancellationToken)
            .ConfigureAwait(false);

        if (legacyValue is null)
        {
            // The reference returns setSystemValue(...) directly here, so a
            // failure surfaces as "failed to create system setting: ...".
            await SetSystemValueAsync(MigrationDoneKey, "true", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        LegacyQuotaEnforcementSettings legacy;
        try
        {
            // JSON null decodes into the Go zero struct and is a successful
            // decode (backup restore follows the same rule).
            legacy = LlmJson.Deserialize<LegacyQuotaEnforcementSettings>(legacyValue)
                ?? new LegacyQuotaEnforcementSettings();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to decode legacy quota enforcement settings: " + ex.Message,
                ex);
        }

        var mode = SystemQuotaRoutingSettingsService.QuotaRoutingModeFromLegacy(
            legacy.Enabled,
            legacy.ExhaustedOnly,
            legacy.DePrioritize,
            legacy.Mode);

        var existingRoutingSettings = await GetSystemValueAsync(
                SystemQuotaRoutingSettingsService.QuotaRoutingSettingsKey,
                "failed to check quota routing settings: ",
                cancellationToken)
            .ConfigureAwait(false);

        if (existingRoutingSettings is null)
        {
            try
            {
                await SetQuotaRoutingSettingsAsync(mode, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "failed to migrate quota routing settings: " + ex.Message,
                    ex);
            }
        }

        var migratedIds = new List<long>();
        foreach (var id in legacy.AllowedChannelIds ?? [])
        {
            ChannelRecord? channel;
            ChannelSettingsDraft settings;
            try
            {
                channel = await channelStore
                    .GetByIdAsync(id, cancellationToken)
                    .ConfigureAwait(false);
                if (channel is null)
                {
                    LogSkippedMissingChannel(logger, id, null);
                    continue;
                }

                settings = ChannelWriteSerialization.ParseSettings(channel.Settings)
                    ?? new ChannelSettingsDraft();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "failed to load channel " + id
                    + " during quota routing migration: " + ex.Message,
                    ex);
            }

            settings.QuotaRoutingMode = QuotaRoutingModes.IgnoreQuota;

            try
            {
                await lifecycle
                    .UpdateAsync(
                        id,
                        new UpdateChannelCommand { Settings = settings },
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    "failed to migrate channel " + id + ": " + ex.Message,
                    ex);
            }

            migratedIds.Add(id);
        }

        LogMigrated(logger, string.Join(",", migratedIds), mode, null);

        try
        {
            await SetSystemValueAsync(MigrationDoneKey, "true", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to write quota routing migration marker: " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Port of SystemService.getSystemValue: null means ent's not-found, every
    /// storage failure carries the "failed to get system value" prefix and the
    /// caller's operation prefix.
    /// </summary>
    private async Task<string?> GetSystemValueAsync(
        string key,
        string operationPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            var row = await systems
                .GetByKeyAsync(key, cancellationToken)
                .ConfigureAwait(false);
            return row?.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                operationPrefix + "failed to get system value: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of SystemService.setSystemValue.</summary>
    private async Task SetSystemValueAsync(
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        try
        {
            await systems
                .SetAsync(key, value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to create system setting: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of SystemService.SetQuotaRoutingSettings.</summary>
    private Task SetQuotaRoutingSettingsAsync(
        string mode,
        CancellationToken cancellationToken)
    {
        if (!QuotaRoutingModes.IsValid(mode))
        {
            throw new InvalidOperationException(
                "invalid quota routing mode: \"" + mode + "\"");
        }

        // json.Marshal(QuotaRoutingSettings{DefaultMode: mode}).
        var value = GoJson.MarshalToString(
            new QuotaRoutingSettingsDraft { DefaultMode = mode });
        return SetSystemValueAsync(
            SystemQuotaRoutingSettingsService.QuotaRoutingSettingsKey,
            value,
            cancellationToken);
    }
}
