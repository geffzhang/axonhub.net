using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Billing;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Settings;
using AxonHub.Api.Storage;
using AxonHub.Llm.Json;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging;
using GoJson = AxonHub.Llm.Json.GoJson;
using Path = System.IO.Path;

namespace AxonHub.Api.Management;

/// <summary>
/// The Reschedule side effect of a settings change
/// (<c>BackupService.Reschedule</c>, autobackup.go:22-32). Production re-arms
/// the singleton backup grain; hosts without Orleans (minimal test hosts)
/// leave it unregistered and Reschedule stays a no-op like the pre-grain port.
/// </summary>
public interface IAutoBackupRescheduleSignal
{
    Task RescheduleAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Port of internal/server/backup (backup_ops.go, restore.go, autobackup.go).
/// The service keeps the reference's observable contract: owner gates and
/// their exact messages, the streamed backup document, the transactional
/// restore order, the auto-backup frequency/retention rules, and every wrapped
/// error string.
/// </summary>
public sealed class BackupService(
    IBackupBatchSource batchSource,
    IBackupRestoreStore restoreStore,
    ISystemStore systems,
    ISystemSettingsAdminService systemSettings,
    IDataStorageStore dataStorages,
    IManagementIdentity identity,
    IDataStorageBackendFactory dataStorageBackends,
    ILogger<BackupService>? logger = null,
    IAutoBackupRescheduleSignal? rescheduleSignal = null)
{
    /// <summary>backup.backupBatchSize (backup_ops.go:23).</summary>
    public const int BatchSize = 500;

    /// <summary>biz.SystemKeyQuotaRoutingSettings.</summary>
    private const string QuotaRoutingSettingsKey = "quota_routing_settings";

    /// <summary>biz.SystemKeyQuotaEnforcementSettings.</summary>
    private const string QuotaEnforcementSettingsKey = "quota_enforcement_settings";

    /// <summary>biz.SystemKeyQuotaRoutingMigrationDone.</summary>
    private const string QuotaRoutingMigrationDoneKey = "quota_routing_migration_done";

    /// <summary>datastorage.TypeDatabase.</summary>
    private const string DatabaseStorageType = "database";

    private static readonly Action<ILogger, string, long, Exception?> LogBackupUploaded =
        LoggerMessage.Define<string, long>(
            LogLevel.Information,
            new EventId(0, "BackupUploaded"),
            "Backup uploaded to storage {Path} ({Size} bytes)");

    private static readonly Action<ILogger, Exception?> LogCleanupFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(0, "CleanupOldBackupsFailed"),
            "Failed to cleanup old backups");

    private static readonly Action<ILogger, string, Exception?> LogDeletedOldBackup =
        LoggerMessage.Define<string>(
            LogLevel.Information,
            new EventId(0, "DeletedOldBackup"),
            "Deleted old backup {File}");

    private static readonly Action<ILogger, string, Exception?> LogDeleteOldBackupFailed =
        LoggerMessage.Define<string>(
            LogLevel.Warning,
            new EventId(0, "DeleteOldBackupFailed"),
            "Failed to delete old backup {File}");

    // --------------------------------------------------------------- backup

    /// <summary>Port of BackupService.Backup (backup_ops.go:27-39).</summary>
    public async Task<string> BackupAsync(
        BackupOptions options,
        CancellationToken cancellationToken = default)
    {
        var user = await identity.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            throw new InvalidOperationException("user not found in context");
        }

        if (!user.IsOwner)
        {
            throw new InvalidOperationException("only owners can perform backup operations");
        }

        return await BackupWithoutAuthAsync(options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Port of BackupService.BackupWithoutAuth (backup_ops.go:43-45).</summary>
    public async Task<string> BackupWithoutAuthAsync(
        BackupOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        await using var buffer = new MemoryStream();
        await BackupJson.WriteAsync(
                buffer,
                options,
                batchSource,
                DateTime.Now,
                cancellationToken)
            .ConfigureAwait(false);

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    // -------------------------------------------------------------- restore

    /// <summary>Port of BackupService.Restore (restore.go:29-82).</summary>
    public async Task RestoreAsync(
        byte[] data,
        RestoreOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(options);

        var user = await identity.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            throw new InvalidOperationException("user not found in context");
        }

        if (!user.IsOwner)
        {
            throw new InvalidOperationException("only owners can perform restore operations");
        }

        // Go returns the encoding/json error unchanged.
        var backupData = LlmJson.Deserialize<BackupData>(data) ?? new BackupData();

        if (!BackupVersions.Accepted.Contains(backupData.Version, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "backup version mismatch: expected " + BackupVersions.Current
                + ", got " + backupData.Version);
        }

        IBackupRestoreTransaction transaction;
        try
        {
            transaction = await restoreStore.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to start transaction: " + ex.Message,
                ex);
        }

        await using (transaction.ConfigureAwait(false))
        {
            await RestoreCoreAsync(backupData, options, cancellationToken).ConfigureAwait(false);

            try
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to commit transaction: " + ex.Message,
                    ex);
            }

            // The reference invalidates the system-value cache here. The port
            // has no cache (C197); the commit itself is the observable effect.
        }
    }

    /// <summary>Port of BackupService.restore (restore.go:84-146).</summary>
    private async Task RestoreCoreAsync(
        BackupData backupData,
        RestoreOptions options,
        CancellationToken cancellationToken)
    {
        if (options.IncludeSystemConfigs)
        {
            await RestoreSystemConfigsAsync(backupData.SystemConfigs, cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.IncludeChannels)
        {
            await RestoreChannelsAsync(backupData.Channels, options, cancellationToken)
                .ConfigureAwait(false);
        }

        // The map is always built: it also remaps model settings and profiles.
        var channelIdMap = await BuildChannelIdMapAsync(backupData.Channels, cancellationToken)
            .ConfigureAwait(false);

        if (options.IncludeSystemConfigs)
        {
            await RestoreLegacyQuotaRoutingAsync(
                    backupData.SystemConfigs,
                    channelIdMap,
                    options.IncludeChannels,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.IncludeModelPrices)
        {
            await RestoreChannelModelPricesAsync(
                    backupData.ChannelModelPrices,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.IncludeModels)
        {
            await RestoreModelsAsync(
                    backupData.Models,
                    options,
                    channelIdMap,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.IncludeProjects)
        {
            foreach (var projectData in backupData.Projects ?? [])
            {
                if (projectData is null)
                {
                    continue;
                }

                projectData.Profiles = RemapProjectProfilesChannelIds(
                    projectData.Profiles,
                    channelIdMap);
            }

            await RestoreProjectsAsync(backupData.Projects, options, cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.IncludeApiKeys)
        {
            await RestoreApiKeysAsync(
                    backupData.ApiKeys,
                    options,
                    channelIdMap,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.IncludeUsageStats || options.IncludeRequestLogs)
        {
            await RestoreUsageDataAsync(
                    backupData.UsageRequests,
                    backupData.UsageLogs,
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------- system configs

    /// <summary>Port of restoreSystemConfigs (restore.go:148-165).</summary>
    private async Task RestoreSystemConfigsAsync(
        List<BackupSystemConfig?>? configs,
        CancellationToken cancellationToken)
    {
        foreach (var config in configs ?? [])
        {
            if (config is null || !SystemConfigBackupKeys.Contains(config.Key))
            {
                continue;
            }

            try
            {
                // JSON null decodes into the Go string zero value ("").
                await systems.SetAsync(
                        config.Key,
                        config.Value ?? string.Empty,
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
                    "failed to restore system configuration "
                    + GoCast.GoQuote(config.Key) + ": " + ex.Message,
                    ex);
            }
        }
    }

    // ---------------------------------------------------- quota routing

    /// <summary>Port of restoreLegacyQuotaRouting (restore.go:167-226).</summary>
    private async Task RestoreLegacyQuotaRoutingAsync(
        List<BackupSystemConfig?>? configs,
        IReadOnlyDictionary<long, long> channelIdMap,
        bool includeChannels,
        CancellationToken cancellationToken)
    {
        var legacy = DecodeLegacyQuotaEnforcementSettings(configs);
        if (legacy is null)
        {
            return;
        }

        var hasQuotaRoutingSettings = HasSystemConfig(configs, QuotaRoutingSettingsKey);

        if (includeChannels && !hasQuotaRoutingSettings)
        {
            foreach (var oldId in legacy.AllowedChannelIds ?? [])
            {
                if (!channelIdMap.TryGetValue(oldId, out var newId))
                {
                    continue;
                }

                var channel = await restoreStore.FindLiveChannelByIdAsync(newId, cancellationToken)
                    .ConfigureAwait(false);
                if (channel is null)
                {
                    throw new InvalidOperationException(
                        "failed to load restored channel " + newId
                        + ": ent: channel not found");
                }

                ChannelSettingsDraft settings;
                try
                {
                    settings = ChannelWriteSerialization.ParseSettings(channel.Settings)
                        ?? new ChannelSettingsDraft();
                }
                catch (JsonException ex)
                {
                    // In the reference the malformed column fails ent's scan
                    // during the Only(ctx) above; keep the same operation text.
                    throw new InvalidOperationException(
                        "failed to load restored channel " + newId + ": " + ex.Message,
                        ex);
                }

                settings.QuotaRoutingMode = QuotaRoutingModes.IgnoreQuota;
                var settingsJson = GoJson.MarshalToString(
                    ChannelWriteSerialization.NormalizeSettings(settings));

                try
                {
                    await restoreStore
                        .SetChannelSettingsAsync(newId, settingsJson, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "failed to restore quota routing for channel " + newId
                        + ": " + ex.Message,
                        ex);
                }
            }
        }

        if (!hasQuotaRoutingSettings)
        {
            var mode = SystemQuotaRoutingSettingsService.QuotaRoutingModeFromLegacy(
                legacy.Enabled,
                legacy.ExhaustedOnly,
                legacy.DePrioritize,
                legacy.Mode);
            var value = GoJson.MarshalToString(
                new QuotaRoutingSettingsDraft { DefaultMode = mode });

            try
            {
                await systems.SetAsync(QuotaRoutingSettingsKey, value, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to restore quota routing settings: " + ex.Message,
                    ex);
            }
        }

        try
        {
            await systems.SetAsync(QuotaRoutingMigrationDoneKey, "true", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to mark quota routing migration complete: " + ex.Message,
                ex);
        }
    }

    private static LegacyQuotaEnforcementSettings? DecodeLegacyQuotaEnforcementSettings(
        List<BackupSystemConfig?>? configs)
    {
        foreach (var config in configs ?? [])
        {
            if (config is null || config.Key != QuotaEnforcementSettingsKey)
            {
                continue;
            }

            try
            {
                // JSON null leaves the Go zero struct and is a successful
                // decode, so a null result becomes an empty settings object.
                return LlmJson.Deserialize<LegacyQuotaEnforcementSettings>(
                        config.Value ?? string.Empty)
                    ?? new LegacyQuotaEnforcementSettings();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static bool HasSystemConfig(
        List<BackupSystemConfig?>? configs,
        string key)
        => (configs ?? []).Any(config => config is not null && config.Key == key);

    // --------------------------------------------------------- channel map

    /// <summary>Port of buildChannelIDMap (restore.go:259-304).</summary>
    private async Task<IReadOnlyDictionary<long, long>> BuildChannelIdMapAsync(
        List<BackupChannelData?>? channels,
        CancellationToken cancellationToken)
    {
        var idMap = new Dictionary<long, long>();
        if (channels is null || channels.Count == 0)
        {
            return idMap;
        }

        var nameToOldId = new Dictionary<string, long>(StringComparer.Ordinal);
        var names = new List<string>(channels.Count);

        foreach (var channelData in channels)
        {
            if (channelData is null)
            {
                continue;
            }

            if (channelData.Id == 0 || string.IsNullOrEmpty(channelData.Name))
            {
                continue;
            }

            nameToOldId[channelData.Name] = channelData.Id;
            names.Add(channelData.Name);
        }

        if (names.Count == 0)
        {
            return idMap;
        }

        var existing = await restoreStore
            .ListLiveChannelIdNamesByNamesAsync(names, cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in existing)
        {
            if (nameToOldId.TryGetValue(row.Name, out var oldId))
            {
                idMap[oldId] = row.Id;
            }
        }

        return idMap;
    }

    // ------------------------------------------------------------ channels

    /// <summary>Port of restoreChannels (restore.go:700-789).</summary>
    private async Task RestoreChannelsAsync(
        List<BackupChannelData?>? channels,
        RestoreOptions options,
        CancellationToken cancellationToken)
    {
        foreach (var channelData in channels ?? [])
        {
            // The reference dereferences a nil element (panic); the port keeps
            // the same shape and lets the null dereference surface.
            var channel = channelData!;

            // JSON null decodes into the Go string zero value ("").
            var name = channel.Name ?? string.Empty;
            var status = channel.Status ?? string.Empty;
            var type = channel.Type ?? string.Empty;

            // The lookup runs before the empty-credentials skip.
            var existing = await restoreStore
                .FindLiveChannelByNameAsync(name, cancellationToken)
                .ConfigureAwait(false);

            var credentials = channel.Credentials ?? new ChannelCredentialsDraft();
            if (string.IsNullOrEmpty(credentials.ApiKey)
                && (credentials.ApiKeys?.Count ?? 0) == 0
                && credentials.OAuth is null)
            {
                continue;
            }

            var settingsJson = channel.Settings is null
                ? "null"
                : GoJson.MarshalToString(
                    ChannelWriteSerialization.NormalizeSettings(channel.Settings));
            var credentialsJson = GoJson.MarshalToString(
                ChannelWriteSerialization.NormalizeCredentials(credentials));
            var supportedModelsJson = channel.SupportedModels is null
                ? "null"
                : GoJson.MarshalToString(channel.SupportedModels);
            var manualModelsJson = channel.ManualModels is null
                ? "null"
                : GoJson.MarshalToString(channel.ManualModels);
            var tagsJson = channel.Tags is null
                ? "null"
                : GoJson.MarshalToString(channel.Tags);

            string? baseUrl = string.IsNullOrEmpty(channel.BaseUrl)
                ? null
                : channel.BaseUrl;

            if (existing is not null)
            {
                switch (options.ChannelConflictStrategy)
                {
                    case BackupConflictStrategy.Skip:
                        continue;
                    case BackupConflictStrategy.Error:
                        throw new InvalidOperationException(
                            "channel " + name + " already exists");
                    case BackupConflictStrategy.Overwrite:
                        try
                        {
                            await restoreStore.UpdateChannelAsync(
                                    existing.Id,
                                    new ChannelRestoreUpdate
                                    {
                                        BaseUrl = baseUrl,
                                        Status = status,
                                        CredentialsJson = credentialsJson,
                                        SupportedModelsJson = supportedModelsJson,
                                        AutoSyncSupportedModels =
                                            channel.AutoSyncSupportedModels,
                                        AutoSyncModelPattern =
                                            channel.AutoSyncModelPattern ?? string.Empty,
                                        ManualModelsJson = manualModelsJson,
                                        TagsJson = tagsJson,
                                        DefaultTestModel =
                                            channel.DefaultTestModel ?? string.Empty,
                                        SettingsJson = settingsJson,
                                        OrderingWeight = channel.OrderingWeight,
                                        Remark = channel.Remark,
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
                                "failed to restore channel " + name
                                + ": " + ex.Message,
                                ex);
                        }

                        break;
                }

                continue;
            }

            var now = DateTime.UtcNow;
            var record = new ChannelRecord
            {
                CreatedAt = now,
                UpdatedAt = now,
                Type = type,
                BaseUrl = baseUrl,
                Name = name,
                Status = status,
                Credentials = credentialsJson,
                // The reference never touches these columns on create, so the
                // ent schema defaults apply.
                DisabledApiKeys = "[]",
                SupportedModels = supportedModelsJson,
                ManualModels = manualModelsJson,
                AutoSyncSupportedModels = channel.AutoSyncSupportedModels,
                AutoSyncModelPattern = channel.AutoSyncModelPattern ?? string.Empty,
                Tags = tagsJson,
                DefaultTestModel = channel.DefaultTestModel ?? string.Empty,
                Policies = ChannelWriteSerialization.DefaultPoliciesJson,
                Settings = settingsJson,
                OrderingWeight = channel.OrderingWeight,
                Remark = channel.Remark,
                Endpoints = "[]",
            };

            try
            {
                await restoreStore.CreateChannelAsync(record, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to create channel " + name + ": " + ex.Message,
                    ex);
            }
        }
    }

    // ---------------------------------------------------- channel prices

    /// <summary>Port of restoreChannelModelPrices (restore.go:536-698).</summary>
    private async Task RestoreChannelModelPricesAsync(
        List<BackupChannelModelPriceData?>? prices,
        RestoreOptions options,
        CancellationToken cancellationToken)
    {
        if (prices is null || prices.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var channelCache = new Dictionary<string, ChannelRecord?>(StringComparer.Ordinal);
        var updatedChannels = new List<long>();
        var updatedChannelSet = new HashSet<long>();

        async Task<ChannelRecord?> GetChannelAsync(string name)
        {
            if (channelCache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var channel = await restoreStore
                .FindLiveChannelByNameAsync(name, cancellationToken)
                .ConfigureAwait(false);
            channelCache[name] = channel;
            return channel;
        }

        foreach (var priceData in prices)
        {
            if (priceData is null)
            {
                continue;
            }

            // JSON null decodes into the Go zero value of the price struct.
            var price = priceData.Price ?? new ModelPrice();
            var channelName = priceData.ChannelName ?? string.Empty;
            var modelId = priceData.ModelId ?? string.Empty;

            try
            {
                ModelPriceValidation.ValidateModelPrice(price);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(
                    "invalid channel model price: channel=" + channelName
                    + " model_id=" + modelId + ": " + ex.Message,
                    ex);
            }

            var channel = await GetChannelAsync(channelName)
                .ConfigureAwait(false);
            if (channel is null)
            {
                continue;
            }

            var existing = await restoreStore.FindLiveChannelModelPriceAsync(
                    channel.Id,
                    modelId,
                    cancellationToken)
                .ConfigureAwait(false);

            var referenceId = priceData.ReferenceId;
            if (string.IsNullOrEmpty(referenceId))
            {
                throw new InvalidOperationException(
                    "channel model price reference ID is empty: channel="
                    + channelName + " model_id=" + modelId);
            }

            if (existing is not null)
            {
                if (existing.ReferenceId == referenceId
                    && ModelPriceValidation.ModelPriceEquals(
                        ChannelModelPriceService.DeserializePrice(existing.Price),
                        price))
                {
                    continue;
                }

                switch (options.ModelPriceConflictStrategy)
                {
                    case BackupConflictStrategy.Skip:
                        continue;
                    case BackupConflictStrategy.Error:
                        throw new InvalidOperationException(
                            "channel model price already exists: channel="
                            + channelName + " model_id=" + modelId);
                    case BackupConflictStrategy.Overwrite:
                        try
                        {
                            await restoreStore
                                .ArchiveActiveChannelModelPriceVersionsAsync(
                                    existing.Id,
                                    now,
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
                                "failed to archive old channel model price versions: "
                                + ex.Message,
                                ex);
                        }

                        try
                        {
                            await restoreStore
                                .UpdateChannelModelPriceAsync(
                                    existing.Id,
                                    ChannelModelPriceService.SerializePrice(price),
                                    referenceId,
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
                                "failed to restore channel model price: channel="
                                + channelName + " model_id="
                                + modelId + ": " + ex.Message,
                                ex);
                        }

                        await CreateActivePriceVersionAsync(
                                channel.Id,
                                modelId,
                                existing.Id,
                                price,
                                referenceId,
                                now,
                                cancellationToken)
                            .ConfigureAwait(false);

                        TrackUpdatedChannel(updatedChannels, updatedChannelSet, channel.Id);
                        break;
                }

                continue;
            }

            ChannelModelPriceRecord entity;
            try
            {
                entity = await restoreStore.CreateChannelModelPriceAsync(
                        new ChannelModelPriceRecord
                        {
                            CreatedAt = now,
                            UpdatedAt = now,
                            ModelId = modelId,
                            Price = ChannelModelPriceService.SerializePrice(price),
                            ReferenceId = referenceId,
                            ChannelId = channel.Id,
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
                    "failed to create channel model price: channel="
                    + channelName + " model_id=" + modelId
                    + ": " + ex.Message,
                    ex);
            }

            await CreateActivePriceVersionAsync(
                    channel.Id,
                    modelId,
                    entity.Id,
                    price,
                    referenceId,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);

            TrackUpdatedChannel(updatedChannels, updatedChannelSet, channel.Id);
        }

        // Force a channel updated_at bump so the reference's cache reloads.
        foreach (var channelId in updatedChannels)
        {
            try
            {
                await restoreStore
                    .SetChannelUpdatedAtAsync(channelId, now, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to update channel updated_at: " + ex.Message,
                    ex);
            }
        }
    }

    private async Task CreateActivePriceVersionAsync(
        long channelId,
        string modelId,
        long channelModelPriceId,
        ModelPrice price,
        string referenceId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        try
        {
            await restoreStore.CreateChannelModelPriceVersionAsync(
                    new ChannelModelPriceVersionRecord
                    {
                        CreatedAt = now,
                        UpdatedAt = now,
                        ChannelId = channelId,
                        ModelId = modelId,
                        ChannelModelPriceId = channelModelPriceId,
                        Price = ChannelModelPriceService.SerializePrice(price),
                        Status = "active",
                        EffectiveStartAt = now,
                        EffectiveEndAt = null,
                        ReferenceId = referenceId,
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
                "failed to create channel model price version: " + ex.Message,
                ex);
        }
    }

    private static void TrackUpdatedChannel(
        List<long> order,
        HashSet<long> seen,
        long channelId)
    {
        if (seen.Add(channelId))
        {
            order.Add(channelId);
        }
    }

    // -------------------------------------------------------------- models

    /// <summary>Port of restoreModels (restore.go:791-869).</summary>
    private async Task RestoreModelsAsync(
        List<BackupModelData?>? models,
        RestoreOptions options,
        IReadOnlyDictionary<long, long> channelIdMap,
        CancellationToken cancellationToken)
    {
        foreach (var modelData in models ?? [])
        {
            if (modelData is null)
            {
                continue;
            }

            // JSON null decodes into the Go string zero value ("").
            var developer = modelData.Developer ?? string.Empty;
            var modelId = modelData.ModelId ?? string.Empty;
            var settings = RemapModelSettingsChannelIds(
                DecodeModelSettings(modelData.Settings),
                channelIdMap);
            var settingsJson = settings is null
                ? "null"
                : ModelSettingsCodec.SerializeEntitySettings(settings);
            var modelCardJson = SerializeModelCard(modelData.ModelCard);

            var existing = await restoreStore.FindLiveModelAsync(
                    developer,
                    modelId,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                switch (options.ModelConflictStrategy)
                {
                    case BackupConflictStrategy.Skip:
                        continue;
                    case BackupConflictStrategy.Error:
                        throw new InvalidOperationException(
                            "model " + modelId + " already exists");
                    case BackupConflictStrategy.Overwrite:
                        try
                        {
                            await restoreStore.UpdateModelAsync(
                                    existing.Id,
                                    new ModelRestoreUpdate
                                    {
                                        Name = modelData.Name ?? string.Empty,
                                        Icon = modelData.Icon ?? string.Empty,
                                        Group = modelData.Group ?? string.Empty,
                                        ModelCardJson = modelCardJson,
                                        SettingsJson = settingsJson,
                                        Status = modelData.Status ?? string.Empty,
                                        Remark = modelData.Remark,
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
                                "failed to restore model " + modelId
                                + ": " + ex.Message,
                                ex);
                        }

                        break;
                }

                continue;
            }

            var now = DateTime.UtcNow;
            var record = new ModelRecord
            {
                CreatedAt = now,
                UpdatedAt = now,
                Developer = developer,
                ModelId = modelId,
                Type = modelData.Type ?? string.Empty,
                Name = modelData.Name ?? string.Empty,
                Icon = modelData.Icon ?? string.Empty,
                Group = modelData.Group ?? string.Empty,
                ModelCard = modelCardJson,
                Settings = settingsJson,
                Status = modelData.Status ?? string.Empty,
                Remark = modelData.Remark,
            };

            try
            {
                await restoreStore.CreateModelAsync(record, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to create model " + modelId + ": " + ex.Message,
                    ex);
            }
        }
    }

    private static ModelEntitySettings? DecodeModelSettings(JsonElement? element)
    {
        if (element is not { } value
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return ModelSettingsCodec.DeserializeEntitySettings(value.GetRawText());
    }

    private static string SerializeModelCard(JsonElement? element)
    {
        if (element is not { } value
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "null";
        }

        var card = ModelCardCodec.Deserialize(value.GetRawText());
        return card is null ? "null" : ModelCardCodec.Serialize(card);
    }

    /// <summary>Port of remapModelSettingsChannelIDs (restore.go:398-429).</summary>
    private static ModelEntitySettings? RemapModelSettingsChannelIds(
        ModelEntitySettings? settings,
        IReadOnlyDictionary<long, long> channelIdMap)
    {
        if (settings is null || channelIdMap.Count == 0 || settings.Associations is null)
        {
            return settings;
        }

        var associations = new List<ModelAssociation?>(settings.Associations.Count);
        foreach (var association in settings.Associations)
        {
            if (association is null)
            {
                associations.Add(null);
                continue;
            }

            associations.Add(association with
            {
                ChannelModel = association.ChannelModel is { } channelModel
                    && channelIdMap.TryGetValue(channelModel.ChannelId, out var newId)
                        ? channelModel with { ChannelId = checked((int)newId) }
                        : association.ChannelModel,
                ChannelRegex = association.ChannelRegex is { } channelRegex
                    && channelIdMap.TryGetValue(channelRegex.ChannelId, out var newRegexId)
                        ? channelRegex with { ChannelId = checked((int)newRegexId) }
                        : association.ChannelRegex,
                Regex = association.Regex is { } regex
                    ? regex with
                    {
                        Exclude = RemapExcludeAssociationChannelIds(
                            regex.Exclude,
                            channelIdMap),
                    }
                    : association.Regex,
                ModelId = association.ModelId is { } modelId
                    ? modelId with
                    {
                        Exclude = RemapExcludeAssociationChannelIds(
                            modelId.Exclude,
                            channelIdMap),
                    }
                    : association.ModelId,
            });
        }

        return settings with { Associations = associations };
    }

    /// <summary>Port of remapExcludeAssociationChannelIDs (restore.go:431-445).</summary>
    private static IReadOnlyList<ExcludeAssociation?>? RemapExcludeAssociationChannelIds(
        IReadOnlyList<ExcludeAssociation?>? excludes,
        IReadOnlyDictionary<long, long> channelIdMap)
    {
        if (excludes is null)
        {
            return null;
        }

        var remapped = new List<ExcludeAssociation?>(excludes.Count);
        foreach (var exclude in excludes)
        {
            if (exclude is null
                || exclude.ChannelIds is null
                || exclude.ChannelIds.Count == 0)
            {
                remapped.Add(exclude);
                continue;
            }

            var ids = new List<int>(exclude.ChannelIds.Count);
            foreach (var oldId in exclude.ChannelIds)
            {
                ids.Add(channelIdMap.TryGetValue(oldId, out var newId)
                    ? checked((int)newId)
                    : oldId);
            }

            remapped.Add(exclude with { ChannelIds = ids });
        }

        return remapped;
    }

    /// <summary>Port of remapProjectProfilesChannelIDs (restore.go:471-490).</summary>
    private static ProjectProfilesModel? RemapProjectProfilesChannelIds(
        ProjectProfilesModel? profiles,
        IReadOnlyDictionary<long, long> channelIdMap)
    {
        if (profiles is null || channelIdMap.Count == 0 || profiles.Profiles is null)
        {
            return profiles;
        }

        var remapped = new List<ProjectProfileModel>(profiles.Profiles.Count);
        foreach (var profile in profiles.Profiles)
        {
            // A null element panics in the reference as well.
            var current = profile!;
            if (current.ChannelIDs is not { Count: > 0 })
            {
                remapped.Add(current);
                continue;
            }

            var ids = new List<int>(current.ChannelIDs.Count);
            foreach (var oldId in current.ChannelIDs)
            {
                ids.Add(channelIdMap.TryGetValue(oldId, out var newId)
                    ? checked((int)newId)
                    : oldId);
            }

            remapped.Add(current with { ChannelIDs = ids });
        }

        return profiles with { Profiles = remapped };
    }

    // ------------------------------------------------------------ projects

    /// <summary>Port of restoreProjects (restore.go:482-534).</summary>
    private async Task RestoreProjectsAsync(
        List<BackupProjectData?>? projects,
        RestoreOptions options,
        CancellationToken cancellationToken)
    {
        if (projects is null || projects.Count == 0)
        {
            return;
        }

        foreach (var projectData in projects)
        {
            if (projectData is null)
            {
                continue;
            }

            // JSON null decodes into the Go string zero value ("").
            var name = projectData.Name ?? string.Empty;
            var existing = await restoreStore
                .FindLiveProjectByNameAsync(name, cancellationToken)
                .ConfigureAwait(false);

            var profilesJson = projectData.Profiles is null
                ? "null"
                : ProjectProfilesCodec.Serialize(projectData.Profiles);

            if (existing is not null)
            {
                switch (options.ProjectConflictStrategy)
                {
                    case BackupConflictStrategy.Skip:
                        continue;
                    case BackupConflictStrategy.Error:
                        throw new InvalidOperationException(
                            "project " + name + " already exists");
                    case BackupConflictStrategy.Overwrite:
                        try
                        {
                            await restoreStore.UpdateProjectAsync(
                                    existing.Id,
                                    new ProjectRestoreUpdate
                                    {
                                        Name = name,
                                        Description = projectData.Description
                                            ?? string.Empty,
                                        Status = projectData.Status ?? string.Empty,
                                        ProfilesJson = profilesJson,
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
                                "failed to restore project " + name
                                + ": " + ex.Message,
                                ex);
                        }

                        break;
                }

                continue;
            }

            var now = DateTime.UtcNow;
            var record = new ProjectRecord
            {
                CreatedAt = now,
                UpdatedAt = now,
                Name = name,
                Description = projectData.Description ?? string.Empty,
                Status = projectData.Status ?? string.Empty,
                Profiles = profilesJson,
            };

            try
            {
                await restoreStore.CreateProjectAsync(record, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to create project " + name + ": " + ex.Message,
                    ex);
            }
        }
    }

    // ------------------------------------------------------------ api keys

    /// <summary>Port of restoreAPIKeys (restore.go:871-959).</summary>
    private async Task RestoreApiKeysAsync(
        List<BackupApiKeyData?>? apiKeys,
        RestoreOptions options,
        IReadOnlyDictionary<long, long> channelIdMap,
        CancellationToken cancellationToken)
    {
        // The reference re-checks the identity even for an empty list.
        var user = await identity.GetCurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            throw new InvalidOperationException(
                "user not found in context for restoring API keys");
        }

        foreach (var apiKeyData in apiKeys ?? [])
        {
            if (apiKeyData is null)
            {
                continue;
            }

            var profiles = RemapApiKeyProfilesChannelIds(
                apiKeyData.Profiles,
                channelIdMap);
            var profilesJson = profiles is null
                ? "null"
                : ApiKeyProfilesCodec.Serialize(profiles);
            var scopesJson = apiKeyData.Scopes is null
                ? "null"
                : GoJson.MarshalToString(apiKeyData.Scopes);

            // JSON null decodes into the Go string zero value ("").
            var key = apiKeyData.Key ?? string.Empty;
            var existing = await restoreStore
                .FindLiveApiKeyByKeyAsync(key, cancellationToken)
                .ConfigureAwait(false);

            if (existing is not null)
            {
                switch (options.ApiKeyConflictStrategy)
                {
                    case BackupConflictStrategy.Skip:
                        continue;
                    case BackupConflictStrategy.Error:
                        throw new InvalidOperationException(
                            "API key " + apiKeyData.Name + " already exists");
                    case BackupConflictStrategy.Overwrite:
                        try
                        {
                            await restoreStore.UpdateApiKeyAsync(
                                    existing.Id,
                                    new ApiKeyRestoreUpdate
                                    {
                                        Name = apiKeyData.Name ?? string.Empty,
                                        Type = apiKeyData.Type ?? string.Empty,
                                        Status = apiKeyData.Status ?? string.Empty,
                                        ScopesJson = scopesJson,
                                        ProfilesJson = profilesJson,
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
                                "failed to restore API key " + apiKeyData.Name
                                + ": " + ex.Message,
                                ex);
                        }

                        break;
                }

                continue;
            }

            var projectName = string.IsNullOrEmpty(apiKeyData.ProjectName)
                ? "Default"
                : apiKeyData.ProjectName;
            var project = await restoreStore
                .FindLiveProjectByNameAsync(projectName, cancellationToken)
                .ConfigureAwait(false);
            if (project is null)
            {
                // ent.IsNotFound: warn and skip.
                continue;
            }

            var now = DateTime.UtcNow;
            var record = new ApiKeyRecord
            {
                CreatedAt = now,
                UpdatedAt = now,
                Key = key,
                Name = apiKeyData.Name ?? string.Empty,
                Type = apiKeyData.Type ?? string.Empty,
                Status = apiKeyData.Status ?? string.Empty,
                Scopes = scopesJson,
                Profiles = profilesJson,
                // The reference never sets allowed_ips on create: schema
                // default [].
                AllowedIps = "[]",
                ProjectId = project.Id,
                UserId = user.Id,
            };

            try
            {
                await restoreStore.CreateApiKeyAsync(record, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to create API key " + apiKeyData.Name + ": " + ex.Message,
                    ex);
            }
        }
    }

    /// <summary>Port of remapAPIKeyProfilesChannelIDs (restore.go:447-469).</summary>
    private static ApiKeyProfilesModel? RemapApiKeyProfilesChannelIds(
        ApiKeyProfilesModel? profiles,
        IReadOnlyDictionary<long, long> channelIdMap)
    {
        if (profiles is null || channelIdMap.Count == 0 || profiles.Profiles is null)
        {
            return profiles;
        }

        var remapped = new List<ApiKeyProfileModel>(profiles.Profiles.Count);
        foreach (var profile in profiles.Profiles)
        {
            // A null element panics in the reference as well.
            var current = profile!;
            if (current.ChannelIDs is not { Count: > 0 })
            {
                remapped.Add(current);
                continue;
            }

            var ids = new List<long>(current.ChannelIDs.Count);
            foreach (var oldId in current.ChannelIDs)
            {
                ids.Add(channelIdMap.TryGetValue(oldId, out var newId)
                    ? newId
                    : oldId);
            }

            remapped.Add(current with { ChannelIDs = ids });
        }

        return profiles with { Profiles = remapped };
    }

    // --------------------------------------------------------------- usage

    /// <summary>Port of restoreUsageData (restore.go:961-986).</summary>
    private async Task RestoreUsageDataAsync(
        List<BackupUsageRequestData?>? requestsData,
        List<BackupUsageLogData?>? usageLogs,
        RestoreOptions options,
        CancellationToken cancellationToken)
    {
        var resolver = await CreateUsageRestoreResolverAsync(cancellationToken)
            .ConfigureAwait(false);

        var requestIdMap = new Dictionary<long, long>();
        if (options.IncludeRequestLogs)
        {
            requestIdMap = await RestoreUsageRequestsAsync(
                    requestsData,
                    resolver,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (options.IncludeUsageStats)
        {
            await RestoreUsageLogsAsync(
                    usageLogs,
                    requestIdMap,
                    resolver,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Port of usageRestoreResolver/newUsageRestoreResolver (restore.go:306-396).</summary>
    private async Task<UsageRestoreResolver> CreateUsageRestoreResolverAsync(
        CancellationToken cancellationToken)
    {
        var projects = await restoreStore.ListLiveProjectIdNamesAsync(cancellationToken)
            .ConfigureAwait(false);
        var channels = await restoreStore.ListLiveChannelIdNamesAsync(cancellationToken)
            .ConfigureAwait(false);
        var apiKeys = await restoreStore.ListLiveApiKeyIdKeysAsync(cancellationToken)
            .ConfigureAwait(false);

        var resolver = new UsageRestoreResolver();
        foreach (var project in projects)
        {
            resolver.ProjectIds.Add(project.Id);
            resolver.ProjectNames[project.Name] = project.Id;
        }

        foreach (var channel in channels)
        {
            resolver.ChannelIds.Add(channel.Id);
            resolver.ChannelNames[channel.Name] = channel.Id;
        }

        foreach (var apiKey in apiKeys)
        {
            resolver.ApiKeyKeys[apiKey.Key] = apiKey.Id;
        }

        return resolver;
    }

    /// <summary>Port of restoreUsageRequests (restore.go:988-1085).</summary>
    private async Task<Dictionary<long, long>> RestoreUsageRequestsAsync(
        List<BackupUsageRequestData?>? requestsData,
        UsageRestoreResolver resolver,
        CancellationToken cancellationToken)
    {
        var idMap = new Dictionary<long, long>();
        if (requestsData is null || requestsData.Count == 0)
        {
            return idMap;
        }

        var existing = await ExistingUsageRequestsAsync(requestsData, cancellationToken)
            .ConfigureAwait(false);

        foreach (var requestData in requestsData)
        {
            if (requestData is null)
            {
                continue;
            }

            var oldId = requestData.Id;
            if (oldId == 0)
            {
                continue;
            }

            var (projectId, projectOk) = resolver.ResolveProjectId(
                requestData.ProjectId,
                requestData.ProjectName);
            if (!projectOk)
            {
                continue;
            }

            var (channelId, _) = resolver.ResolveChannelId(
                requestData.ChannelId,
                requestData.ChannelName);
            var (apiKeyId, _) = resolver.ResolveApiKeyId(requestData.ApiKeyKey);

            if (existing.ById.TryGetValue(oldId, out var byId)
                && SameUsageRequest(byId.Request, requestData, projectId, channelId, apiKeyId))
            {
                idMap[oldId] = byId.Request.Id;
                continue;
            }

            if (existing.ByFingerprint.TryGetValue(
                    BackupUsageRequestFingerprint(requestData),
                    out var byFingerprint))
            {
                idMap[oldId] = byFingerprint.Request.Id;
                continue;
            }

            var record = new RequestRecord
            {
                CreatedAt = requestData.CreatedAt,
                UpdatedAt = requestData.UpdatedAt,
                ProjectId = projectId,
                // JSON null decodes into the Go string zero value ("").
                Source = requestData.Source ?? string.Empty,
                ModelId = requestData.ModelId ?? string.Empty,
                Format = requestData.Format ?? string.Empty,
                RequestBody = CompactRawJson(requestData.RequestBody),
                Status = requestData.Status ?? string.Empty,
                Stream = requestData.Stream,
                ClientIp = requestData.ClientIp ?? string.Empty,
                ContentSaved = requestData.ContentSaved,
                ApiKeyId = apiKeyId == 0 ? null : apiKeyId,
                ChannelId = channelId == 0 ? null : channelId,
                ReasoningEffort = string.IsNullOrEmpty(requestData.ReasoningEffort)
                    ? null
                    : requestData.ReasoningEffort,
                RequestHeaders = CompactRawJson(requestData.RequestHeaders),
                ResponseHeaders = CompactRawJson(requestData.ResponseHeaders),
                ResponseBody = CompactRawJson(requestData.ResponseBody),
                ResponseChunks = CompactRawJsonArray(requestData.ResponseChunks),
                ExternalId = string.IsNullOrEmpty(requestData.ExternalId)
                    ? null
                    : requestData.ExternalId,
                MetricsLatencyMs = requestData.MetricsLatencyMs,
                MetricsFirstTokenLatencyMs = requestData.MetricsFirstTokenLatencyMs,
                MetricsReasoningDurationMs = requestData.MetricsReasoningDurationMs,
                ContentStorageId = requestData.ContentStorageId,
                ContentStorageKey = requestData.ContentStorageKey,
                ContentSavedAt = requestData.ContentSavedAt,
            };

            RequestRecord created;
            try
            {
                created = await restoreStore
                    .CreateUsageRequestAsync(record, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to restore usage request " + oldId + ": " + ex.Message,
                    ex);
            }

            idMap[oldId] = created.Id;
        }

        return idMap;
    }

    /// <summary>Port of existingUsageRequests (restore.go:1096-1161).</summary>
    private async Task<UsageRequestLookup> ExistingUsageRequestsAsync(
        IReadOnlyList<BackupUsageRequestData?> requestsData,
        CancellationToken cancellationToken)
    {
        var ids = new List<long>(requestsData.Count);
        var createdAt = new List<DateTime>(requestsData.Count);
        var createdAtSeen = new HashSet<DateTime>();

        foreach (var requestData in requestsData)
        {
            if (requestData is null)
            {
                continue;
            }

            if (requestData.Id != 0)
            {
                ids.Add(requestData.Id);
            }

            if (requestData.CreatedAt != default
                && createdAtSeen.Add(requestData.CreatedAt))
            {
                createdAt.Add(requestData.CreatedAt);
            }
        }

        var lookup = new UsageRequestLookup();

        foreach (var batch in Batch(ids))
        {
            var rows = await restoreStore
                .FindUsageRequestsByIdsAsync(batch, cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in rows)
            {
                AddExistingUsageRequest(lookup, row);
            }
        }

        foreach (var batch in Batch(createdAt))
        {
            var rows = await restoreStore
                .FindUsageRequestsByCreatedAtAsync(batch, cancellationToken)
                .ConfigureAwait(false);
            foreach (var row in rows)
            {
                AddExistingUsageRequest(lookup, row);
            }
        }

        return lookup;
    }

    private static void AddExistingUsageRequest(
        UsageRequestLookup lookup,
        BackupUsageRequestRow row)
    {
        lookup.ById[row.Request.Id] = row;
        lookup.ByFingerprint[ExistingUsageRequestFingerprint(row, includeApiKey: true)] = row;
        lookup.ByFingerprint[ExistingUsageRequestFingerprint(row, includeApiKey: false)] = row;
    }

    /// <summary>Port of usageRequestBackupFingerprint (restore.go:1163-1188).</summary>
    private static string BackupUsageRequestFingerprint(BackupUsageRequestData request)
        => UsageRequestFingerprint(
            request.CreatedAt,
            request.ModelId,
            request.Format,
            request.Source,
            request.Status,
            request.Stream,
            request.ClientIp,
            request.ExternalId,
            request.ReasoningEffort,
            request.ProjectName,
            request.ChannelName,
            request.ApiKeyKey);

    /// <summary>Port of usageRequestExistingFingerprint (restore.go:1190-1222).</summary>
    private static string ExistingUsageRequestFingerprint(
        BackupUsageRequestRow row,
        bool includeApiKey)
        => UsageRequestFingerprint(
            row.Request.CreatedAt,
            row.Request.ModelId,
            row.Request.Format,
            row.Request.Source,
            row.Request.Status,
            row.Request.Stream,
            row.Request.ClientIp,
            row.Request.ExternalId ?? string.Empty,
            row.Request.ReasoningEffort ?? string.Empty,
            row.ProjectName ?? string.Empty,
            row.ChannelName ?? string.Empty,
            includeApiKey ? row.ApiKeyKey ?? string.Empty : string.Empty);

    /// <summary>Port of usageRequestFingerprint (restore.go:1224-1248).</summary>
    /// <remarks>
    /// Every string parameter is nullable: a JSON null decodes into the Go
    /// string zero value, and <see cref="string.Join(char, string?[])"/> renders
    /// null as an empty field, exactly like the reference's <c>%s</c> joins.
    /// </remarks>
    private static string UsageRequestFingerprint(
        DateTime createdAt,
        string? modelId,
        string? format,
        string? source,
        string? status,
        bool stream,
        string? clientIp,
        string? externalId,
        string? reasoningEffort,
        string? projectName,
        string? channelName,
        string? apiKeyKey)
    {
        var timestamp = createdAt.Kind switch
        {
            DateTimeKind.Utc => createdAt,
            DateTimeKind.Local => createdAt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(createdAt, DateTimeKind.Utc),
        };

        var parts = new[]
        {
            GoTime.Rfc3339Nano(timestamp),
            modelId,
            format,
            source,
            status,
            stream ? "true" : "false",
            clientIp,
            externalId,
            reasoningEffort,
            projectName,
            channelName,
            apiKeyKey,
        };

        return string.Join('\0', parts);
    }

    /// <summary>Port of sameUsageRequest (restore.go:1250-1265).</summary>
    private static bool SameUsageRequest(
        RequestRecord existing,
        BackupUsageRequestData backup,
        long projectId,
        long channelId,
        long apiKeyId)
    {
        var apiKeyMatches = string.IsNullOrEmpty(backup.ApiKeyKey)
            || (existing.ApiKeyId ?? 0) == apiKeyId;

        return existing.ProjectId == projectId
            && (existing.ChannelId ?? 0) == channelId
            && apiKeyMatches
            && existing.ModelId == (backup.ModelId ?? string.Empty)
            && existing.Format == (backup.Format ?? string.Empty)
            && existing.Source == (backup.Source ?? string.Empty)
            && existing.Status == (backup.Status ?? string.Empty)
            && existing.Stream == backup.Stream
            && existing.ClientIp == (backup.ClientIp ?? string.Empty)
            && (existing.ExternalId ?? string.Empty)
                == (backup.ExternalId ?? string.Empty)
            && (existing.ReasoningEffort ?? string.Empty)
                == (backup.ReasoningEffort ?? string.Empty)
            && existing.CreatedAt == backup.CreatedAt;
    }

    /// <summary>Port of restoreUsageLogs (restore.go:1267-1409).</summary>
    private async Task RestoreUsageLogsAsync(
        List<BackupUsageLogData?>? usageLogs,
        Dictionary<long, long> requestIdMap,
        UsageRestoreResolver resolver,
        CancellationToken cancellationToken)
    {
        if (usageLogs is null || usageLogs.Count == 0)
        {
            return;
        }

        await EnsureUsageLogRequestsAsync(
                usageLogs,
                requestIdMap,
                resolver,
                cancellationToken)
            .ConfigureAwait(false);

        var requestIds = new List<long>(requestIdMap.Values);
        var existingLogRequestIds = new HashSet<long>();
        foreach (var batch in Batch(requestIds))
        {
            var found = await restoreStore
                .ListUsageLogRequestIdsAsync(batch, cancellationToken)
                .ConfigureAwait(false);
            foreach (var requestId in found)
            {
                existingLogRequestIds.Add(requestId);
            }
        }

        var restoredLogRequestIds = new HashSet<long>();
        var builders = new List<UsageLogRecord>(Math.Min(usageLogs.Count, BatchSize));

        async Task FlushAsync()
        {
            if (builders.Count == 0)
            {
                return;
            }

            try
            {
                await restoreStore.CreateUsageLogsAsync(builders, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to restore usage logs: " + ex.Message,
                    ex);
            }

            builders = [];
        }

        foreach (var usageData in usageLogs)
        {
            if (usageData is null)
            {
                continue;
            }

            if (!requestIdMap.TryGetValue(usageData.RequestId, out var requestId))
            {
                continue;
            }

            if (existingLogRequestIds.Contains(requestId))
            {
                continue;
            }

            if (restoredLogRequestIds.Contains(requestId))
            {
                continue;
            }

            var (projectId, projectOk) = resolver.ResolveProjectId(
                usageData.ProjectId,
                usageData.ProjectName);
            if (!projectOk)
            {
                continue;
            }

            var (channelId, _) = resolver.ResolveChannelId(
                usageData.ChannelId,
                usageData.ChannelName);
            var (apiKeyId, _) = resolver.ResolveApiKeyId(usageData.ApiKeyKey);

            builders.Add(new UsageLogRecord
            {
                CreatedAt = usageData.CreatedAt,
                UpdatedAt = usageData.UpdatedAt,
                RequestId = requestId,
                ApiKeyId = apiKeyId == 0 ? null : apiKeyId,
                ProjectId = projectId,
                ChannelId = channelId == 0 ? null : channelId,
                ModelId = usageData.ModelId ?? string.Empty,
                PromptTokens = usageData.PromptTokens,
                CompletionTokens = usageData.CompletionTokens,
                TotalTokens = usageData.TotalTokens,
                PromptAudioTokens = usageData.PromptAudioTokens,
                PromptCachedTokens = usageData.PromptCachedTokens,
                PromptWriteCachedTokens = usageData.PromptWriteCachedTokens,
                PromptWriteCachedTokens5m = usageData.PromptWriteCachedTokens5m,
                PromptWriteCachedTokens1h = usageData.PromptWriteCachedTokens1h,
                CompletionAudioTokens = usageData.CompletionAudioTokens,
                CompletionReasoningTokens = usageData.CompletionReasoningTokens,
                CompletionAcceptedPredictionTokens =
                    usageData.CompletionAcceptedPredictionTokens,
                CompletionRejectedPredictionTokens =
                    usageData.CompletionRejectedPredictionTokens,
                Source = usageData.Source ?? string.Empty,
                Format = usageData.Format ?? string.Empty,
                TotalCost = usageData.TotalCost,
                CostItems = usageData.CostItems is null
                    ? "null"
                    : GoJson.MarshalToString(usageData.CostItems),
                CostPriceReferenceId = string.IsNullOrEmpty(
                    usageData.CostPriceReferenceId)
                    ? null
                    : usageData.CostPriceReferenceId,
            });

            // The reference marks the request restored only after the builder
            // was appended.
            restoredLogRequestIds.Add(requestId);

            if (builders.Count >= BatchSize)
            {
                await FlushAsync().ConfigureAwait(false);
            }
        }

        await FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Port of ensureUsageLogRequests (restore.go:1411-1495).</summary>
    private async Task EnsureUsageLogRequestsAsync(
        IReadOnlyList<BackupUsageLogData?> usageLogs,
        Dictionary<long, long> requestIdMap,
        UsageRestoreResolver resolver,
        CancellationToken cancellationToken)
    {
        var shells = UsageLogRequestShells(usageLogs, requestIdMap);
        var existing = await ExistingUsageRequestsAsync(shells, cancellationToken)
            .ConfigureAwait(false);

        foreach (var usageData in usageLogs)
        {
            if (usageData is null || usageData.RequestId == 0)
            {
                continue;
            }

            if (requestIdMap.ContainsKey(usageData.RequestId))
            {
                continue;
            }

            var (projectId, projectOk) = resolver.ResolveProjectId(
                usageData.ProjectId,
                usageData.ProjectName);
            if (!projectOk)
            {
                continue;
            }

            var (channelId, _) = resolver.ResolveChannelId(
                usageData.ChannelId,
                usageData.ChannelName);
            var (apiKeyId, _) = resolver.ResolveApiKeyId(usageData.ApiKeyKey);

            var shellData = UsageLogRequestShell(usageData);

            if (existing.ById.TryGetValue(usageData.RequestId, out var byId)
                && SameUsageRequest(byId.Request, shellData, projectId, channelId, apiKeyId))
            {
                requestIdMap[usageData.RequestId] = byId.Request.Id;
                continue;
            }

            if (existing.ByFingerprint.TryGetValue(
                    BackupUsageRequestFingerprint(shellData),
                    out var byFingerprint))
            {
                requestIdMap[usageData.RequestId] = byFingerprint.Request.Id;
                continue;
            }

            if (apiKeyId == 0)
            {
                shellData.ApiKeyKey = string.Empty;
                if (existing.ByFingerprint.TryGetValue(
                        BackupUsageRequestFingerprint(shellData),
                        out var withoutApiKey))
                {
                    requestIdMap[usageData.RequestId] = withoutApiKey.Request.Id;
                    continue;
                }
            }

            var record = new RequestRecord
            {
                CreatedAt = usageData.CreatedAt,
                UpdatedAt = usageData.UpdatedAt,
                ProjectId = projectId,
                Source = usageData.Source ?? string.Empty,
                ModelId = usageData.ModelId ?? string.Empty,
                Format = usageData.Format ?? string.Empty,
                RequestBody = "{}",
                Status = "completed",
                Stream = false,
                ClientIp = string.Empty,
                ApiKeyId = apiKeyId == 0 ? null : apiKeyId,
                ChannelId = channelId == 0 ? null : channelId,
            };

            RequestRecord created;
            try
            {
                created = await restoreStore
                    .CreateUsageRequestAsync(record, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to create usage log request shell "
                    + usageData.RequestId + ": " + ex.Message,
                    ex);
            }

            requestIdMap[usageData.RequestId] = created.Id;
        }
    }

    /// <summary>Port of usageLogRequestShells (restore.go:1497-1522).</summary>
    private static List<BackupUsageRequestData?> UsageLogRequestShells(
        IReadOnlyList<BackupUsageLogData?> usageLogs,
        IReadOnlyDictionary<long, long> requestIdMap)
    {
        var shells = new List<BackupUsageRequestData?>(usageLogs.Count);
        var seen = new HashSet<long>();

        foreach (var usageData in usageLogs)
        {
            if (usageData is null || usageData.RequestId == 0)
            {
                continue;
            }

            if (requestIdMap.ContainsKey(usageData.RequestId)
                || !seen.Add(usageData.RequestId))
            {
                continue;
            }

            shells.Add(UsageLogRequestShell(usageData));
        }

        return shells;
    }

    /// <summary>Port of usageLogRequestShell (restore.go:1524-1544).</summary>
    private static BackupUsageRequestData UsageLogRequestShell(
        BackupUsageLogData usageData)
        => new()
        {
            Id = usageData.RequestId,
            CreatedAt = usageData.CreatedAt,
            UpdatedAt = usageData.UpdatedAt,
            Source = usageData.Source ?? string.Empty,
            ModelId = usageData.ModelId ?? string.Empty,
            Format = usageData.Format ?? string.Empty,
            Status = "completed",
            Stream = false,
            ClientIp = string.Empty,
            ProjectName = usageData.ProjectName,
            ChannelName = usageData.ChannelName,
            ApiKeyKey = usageData.ApiKeyKey,
        };

    // ---------------------------------------------------------- auto backup

    /// <summary>
    /// Port of BackupService.Reschedule (autobackup.go:26-40): the reference
    /// cancels and re-creates the backup cron entry; the port asks the
    /// singleton backup grain to re-arm from the current instant (§149). The
    /// signal logs its own failures, so a settings mutation never fails
    /// because scheduling did, matching the reference.
    /// </summary>
    public async Task RescheduleAsync(CancellationToken cancellationToken = default)
    {
        if (rescheduleSignal is null)
        {
            return;
        }

        await rescheduleSignal.RescheduleAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Port of BackupService.triggerAutoBackup (autobackup.go:42-85).</summary>
    public async Task TriggerAutoBackupAsync(CancellationToken cancellationToken = default)
    {
        AutoBackupSettings settings;
        try
        {
            settings = await systemSettings.GetAutoBackupSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The reference logs and returns.
            return;
        }

        if (!settings.Enabled)
        {
            return;
        }

        if (!ShouldRunBackup(DateTime.Now, settings))
        {
            return;
        }

        var errorMessage = string.Empty;
        try
        {
            await PerformBackupAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
        }

        try
        {
            await systemSettings
                .UpdateAutoBackupLastRunAsync(errorMessage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Logged only in the reference.
        }
    }

    /// <summary>Port of BackupService.shouldRunBackup (autobackup.go:87-98).</summary>
    public static bool ShouldRunBackup(DateTime now, AutoBackupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.Frequency switch
        {
            BackupFrequencies.Daily => true,
            BackupFrequencies.Weekly => now.DayOfWeek == DayOfWeek.Sunday,
            BackupFrequencies.Monthly => now.Day == 1,
            _ => true,
        };
    }

    /// <summary>Port of BackupService.RunBackupNow (autobackup.go:210-233).</summary>
    public async Task RunBackupNowAsync(CancellationToken cancellationToken = default)
    {
        AutoBackupSettings settings;
        try
        {
            settings = await systemSettings.GetAutoBackupSettingsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get auto backup settings: " + ex.Message,
                ex);
        }

        if (settings.DataStorageId == 0)
        {
            throw new InvalidOperationException(
                "data storage not configured for backup");
        }

        Exception? failure = null;
        try
        {
            await PerformBackupAsync(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            await systemSettings
                .UpdateAutoBackupLastRunAsync(failure?.Message ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Logged only in the reference.
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>Port of BackupService.performBackup (autobackup.go:100-165).</summary>
    private async Task PerformBackupAsync(
        AutoBackupSettings settings,
        CancellationToken cancellationToken)
    {
        var storage = await GetDataStorageAsync(settings.DataStorageId, cancellationToken)
            .ConfigureAwait(false);

        var options = new BackupOptions
        {
            IncludeSystemConfigs = settings.IncludeSystemConfigs,
            IncludeChannels = settings.IncludeChannels,
            IncludeModels = settings.IncludeModels,
            IncludeApiKeys = settings.IncludeApiKeys,
            IncludeModelPrices = settings.IncludeModelPrices,
            IncludeUsageStats = settings.IncludeUsageStats,
            IncludeRequestLogs = settings.IncludeRequestLogs,
        };

        var timestamp = DateTime.Now.ToString(
            "yyyy-MM-dd_HH-mm-ss",
            System.Globalization.CultureInfo.InvariantCulture);
        var filename = "axonhub-backup-" + timestamp + ".json";

        if (storage.Type == DatabaseStorageType)
        {
            try
            {
                await using var buffer = new MemoryStream();
                await BackupJson.WriteAsync(
                        buffer,
                        options,
                        batchSource,
                        DateTime.Now,
                        cancellationToken)
                    .ConfigureAwait(false);

                // DataStorageService.SaveData for the database backend returns
                // nil and has no observable side effect (autobackup.go:107-118).
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to create backup: " + ex.Message,
                    ex);
            }
        }
        else
        {
            // autobackup.go:119-144: stream into a temp file, rewind, hand the
            // reader to SaveDataFromReader.
            string tempPath;
            FileStream file;
            try
            {
                tempPath = Path.Combine(
                    Path.GetTempPath(),
                    "axonhub-backup-" + Guid.NewGuid().ToString("N") + ".json");
                file = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "failed to create temp backup file: " + ex.Message,
                    ex);
            }

            try
            {
                try
                {
                    await BackupJson.WriteAsync(
                            file,
                            options,
                            batchSource,
                            DateTime.Now,
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
                        "failed to create backup: " + ex.Message,
                        ex);
                }

                try
                {
                    file.Position = 0;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "failed to seek backup file: " + ex.Message,
                        ex);
                }

                using var backend = CreateBackend(storage, "failed to write backup file: ");

                string key;
                long written;
                try
                {
                    (key, written) = await backend
                        .WriteFromStreamAsync(filename, file, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "failed to write backup file: " + ex.Message,
                        ex);
                }

                if (logger is not null)
                {
                    LogBackupUploaded(logger, key, written, null);
                }
            }
            finally
            {
                await file.DisposeAsync().ConfigureAwait(false);
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception)
                {
                    // defer os.Remove ignores the error.
                }
            }
        }

        if (settings.RetentionDays > 0)
        {
            try
            {
                await CleanupOldBackupsAsync(
                        storage,
                        settings.RetentionDays,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // autobackup.go:146-150: cleanup failures are logged, not raised.
                if (logger is not null)
                {
                    LogCleanupFailed(logger, ex);
                }
            }
        }
    }

    /// <summary>
    /// SaveDataFromReader wraps GetFileSystem failures with
    /// <c>failed to get file system: %w</c> (data_storage.go:609-612), but S3
    /// never reaches GetFileSystem: objectStoreFor runs first and wraps client
    /// construction with <c>failed to create s3 object store: %w</c>; a missing
    /// settings block and an unknown type are returned bare
    /// (objectstore.go:59-66, data_storage.go:644-646).
    /// </summary>
    private IDataStorageBackend CreateBackend(DataStorageRecord storage, string prefix)
    {
        const string S3FileSystemPrefix = "failed to create s3 filesystem: ";

        try
        {
            return dataStorageBackends.Create(storage);
        }
        catch (InvalidOperationException ex)
        {
            var message = ex.Message;

            if (storage.Type == "s3")
            {
                if (message.StartsWith(S3FileSystemPrefix, StringComparison.Ordinal))
                {
                    message = "failed to create s3 object store: "
                        + message[S3FileSystemPrefix.Length..];
                }
            }
            else if (storage.Type is "fs" or "gcs" or "webdav")
            {
                message = "failed to get file system: " + message;
            }

            throw new InvalidOperationException(prefix + message, ex);
        }
    }

    /// <summary>Port of BackupService.cleanupOldBackups (autobackup.go:155-205).</summary>
    private async Task CleanupOldBackupsAsync(
        DataStorageRecord storage,
        int retentionDays,
        CancellationToken cancellationToken)
    {
        IDataStorageBackend backend;
        try
        {
            backend = dataStorageBackends.Create(storage);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get data storage filesystem: " + ex.Message,
                ex);
        }

        using (backend)
        {
            IReadOnlyList<DataStorageEntry> files;
            try
            {
                files = await backend.ListAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("failed to list backups: " + ex.Message, ex);
            }

            var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
            var backups = files
                .Where(static file => file.Name.StartsWith("axonhub-backup-", StringComparison.Ordinal)
                    && file.Name.EndsWith(".json", StringComparison.Ordinal))
                .OrderBy(static file => file.LastModified)
                .ToList();

            foreach (var backup in backups)
            {
                if (backup.LastModified >= cutoff)
                {
                    continue;
                }

                try
                {
                    await backend.DeleteAsync(backup.Name, cancellationToken)
                        .ConfigureAwait(false);
                    if (logger is not null)
                    {
                        LogDeletedOldBackup(logger, backup.Name, null);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (logger is not null)
                    {
                        LogDeleteOldBackupFailed(logger, backup.Name, ex);
                    }
                }
            }
        }
    }

    private async Task<DataStorageRecord> GetDataStorageAsync(
        int dataStorageId,
        CancellationToken cancellationToken)
    {
        DataStorageRecord? storage;
        try
        {
            storage = await dataStorages.GetByIdAsync(dataStorageId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get data storage: failed to get data storage by ID "
                + dataStorageId + ": " + ex.Message,
                ex);
        }

        if (storage is null)
        {
            throw new InvalidOperationException(
                "failed to get data storage: failed to get data storage by ID "
                + dataStorageId + ": ent: data storage not found");
        }

        return storage;
    }

    // -------------------------------------------------------------- helpers

    private static IEnumerable<IReadOnlyList<T>> Batch<T>(IReadOnlyList<T> items)
    {
        for (var start = 0; start < items.Count; start += BatchSize)
        {
            var count = Math.Min(BatchSize, items.Count - start);
            var batch = new T[count];
            for (var index = 0; index < count; index++)
            {
                batch[index] = items[start + index];
            }

            yield return batch;
        }
    }

    /// <summary>
    /// Go's <c>json.Marshal(json.RawMessage)</c>: compact and HTML-escape the
    /// fragment; a nil raw message marshals as <c>null</c>.
    /// </summary>
    private static string CompactRawJson(JsonElement? element)
    {
        if (element is not { } value
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "null";
        }

        return Encoding.UTF8.GetString(
            GoJson.MarshalRaw(Encoding.UTF8.GetBytes(value.GetRawText())));
    }

    /// <summary>
    /// Go's <c>json.Marshal([]json.RawMessage)</c>: <c>null</c> for a nil
    /// slice, otherwise a compact array of the raw elements.
    /// </summary>
    private static string CompactRawJsonArray(List<JsonElement>? elements)
    {
        if (elements is null)
        {
            return "null";
        }

        var builder = new StringBuilder("[");
        for (var index = 0; index < elements.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(CompactRawJson(elements[index]));
        }

        builder.Append(']');
        return builder.ToString();
    }

    /// <summary>Port of usageRestoreResolver (restore.go:306-396).</summary>
    private sealed class UsageRestoreResolver
    {
        public Dictionary<string, long> ProjectNames { get; } =
            new(StringComparer.Ordinal);

        public HashSet<long> ProjectIds { get; } = [];

        public Dictionary<string, long> ChannelNames { get; } =
            new(StringComparer.Ordinal);

        public HashSet<long> ChannelIds { get; } = [];

        public Dictionary<string, long> ApiKeyKeys { get; } =
            new(StringComparer.Ordinal);

        public (long Id, bool Ok) ResolveProjectId(long projectId, string projectName)
        {
            if (!string.IsNullOrEmpty(projectName))
            {
                return ProjectNames.TryGetValue(projectName, out var id)
                    ? (id, true)
                    : (0, false);
            }

            if (projectId == 0)
            {
                return (0, false);
            }

            return ProjectIds.Contains(projectId) ? (projectId, true) : (0, false);
        }

        public (long Id, bool Ok) ResolveChannelId(long channelId, string channelName)
        {
            if (!string.IsNullOrEmpty(channelName))
            {
                return ChannelNames.TryGetValue(channelName, out var id)
                    ? (id, true)
                    : (0, false);
            }

            if (channelId == 0)
            {
                return (0, false);
            }

            return ChannelIds.Contains(channelId) ? (channelId, true) : (0, false);
        }

        public (long Id, bool Ok) ResolveApiKeyId(string apiKeyKey)
        {
            if (!string.IsNullOrEmpty(apiKeyKey))
            {
                return ApiKeyKeys.TryGetValue(apiKeyKey, out var id)
                    ? (id, true)
                    : (0, false);
            }

            return (0, false);
        }
    }

    /// <summary>Port of existingUsageRequestLookup (restore.go:1087-1094).</summary>
    private sealed class UsageRequestLookup
    {
        public Dictionary<long, BackupUsageRequestRow> ById { get; } = [];

        public Dictionary<string, BackupUsageRequestRow> ByFingerprint { get; } =
            new(StringComparer.Ordinal);
    }
}
