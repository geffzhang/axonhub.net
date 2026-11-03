using System.Text.Json;
using AxonHub.Api.Billing;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Persistence;
using GoJson = AxonHub.Llm.Json.GoJson;

namespace AxonHub.Api.Management;

/// <summary>
/// The reload hook in syncChannelModels (channel_model_sync.go:59-61,
/// 214-216). The reference swaps its in-memory enabled-channel cache; the port
/// has no channel cache yet, so the hook is a no-op (C158).
/// </summary>
public interface IChannelModelSyncReloadHook
{
    void ReloadAfterModelSync();
}

/// <summary>No-op reload hook used until the channel cache is ported.</summary>
public sealed class NullChannelModelSyncReloadHook : IChannelModelSyncReloadHook
{
    public void ReloadAfterModelSync()
    {
    }
}

/// <summary>Aggregate counters returned by the sync-all pass (biz logs them only).</summary>
public sealed record ChannelModelSyncSummary(int SuccessCount, int FailureCount, int ChangedCount);

/// <summary>
/// The GraphQL payload source after one channel sync: the re-read/updated row
/// plus its decoded model lists (the resolver reads ch.SupportedModels and
/// normalizes a nil manual_models to an empty list).
/// </summary>
public sealed record ChannelModelSyncResult(
    ChannelRecord Channel,
    IReadOnlyList<string> SupportedModels,
    IReadOnlyList<string> ManualModels,
    bool Changed);

/// <summary>
/// Port of the model-sync half of ChannelService
/// (internal/server/biz/channel_model_sync.go and channel_internal.go:23-59):
/// per-channel fetch/merge/price-ensure plus the hourly schedule guard.
/// </summary>
/// <remarks>
/// Each channel gets a fresh ModelFetcher, like the reference's
/// NewModelFetcher call inside syncChannelModelsForChannel. Per-channel proxy
/// settings and the scheduled cron registration are not ported (C158).
/// </remarks>
public sealed class ChannelModelSyncService(
    IChannelModelSyncStore store,
    IChannelModelPriceStore prices,
    IExecutor executor,
    TimeProvider time,
    IChannelAutoSyncFrequencyProvider frequency,
    IChannelModelSyncReloadHook reloadHook)
{
    private readonly object _modelSyncGate = new();
    private DateTimeOffset? _lastModelSyncExecutionTime;

    /// <summary>
    /// Port of ChannelService.SyncChannelModels (channel_model_sync.go:203-219):
    /// load the channel, sync it, return the updated row.
    /// </summary>
    public async Task<ChannelModelSyncResult> SyncChannelModelsAsync(
        long channelId,
        string? patternOverride,
        CancellationToken cancellationToken = default)
    {
        ChannelRecord? channel;
        try
        {
            channel = await store.GetAsync(channelId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to get channel: " + ex.Message, ex);
        }

        if (channel is null)
        {
            throw new InvalidOperationException("failed to get channel: ent: channel not found");
        }

        var result = await SyncChannelModelsForChannelAsync(channel, patternOverride, cancellationToken)
            .ConfigureAwait(false);

        if (result.Changed)
        {
            reloadHook.ReloadAfterModelSync();
        }

        return result;
    }

    /// <summary>
    /// Port of ChannelService.syncChannelModels (channel_model_sync.go:15-67):
    /// every enabled channel with auto-sync on, one channel failure never stops
    /// the pass.
    /// </summary>
    public async Task<ChannelModelSyncSummary> SyncAllAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ChannelRecord> channels;
        try
        {
            channels = await store.ListAutoSyncEnabledAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The reference logs the query error and returns.
            return new ChannelModelSyncSummary(0, 0, 0);
        }

        if (channels.Count == 0)
        {
            return new ChannelModelSyncSummary(0, 0, 0);
        }

        var successCount = 0;
        var failureCount = 0;
        var changedCount = 0;

        foreach (var channel in channels)
        {
            try
            {
                var result = await SyncChannelModelsForChannelAsync(channel, null, cancellationToken)
                    .ConfigureAwait(false);
                successCount++;
                if (result.Changed)
                {
                    changedCount++;
                }
            }
            catch (Exception)
            {
                failureCount++;
            }
        }

        if (changedCount > 0)
        {
            // asyncReloadChannels: fire-and-forget in the reference; the port's
            // hook is synchronous and currently a no-op.
            reloadHook.ReloadAfterModelSync();
        }

        return new ChannelModelSyncSummary(successCount, failureCount, changedCount);
    }

    /// <summary>
    /// Port of ChannelService.syncChannelModelsForChannel
    /// (channel_model_sync.go:69-201).
    /// </summary>
    public async Task<ChannelModelSyncResult> SyncChannelModelsForChannelAsync(
        ChannelRecord channel,
        string? patternOverride,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // The reference constructs a fresh fetcher per channel, so the provider
        // conf cache is not shared across the pass.
        var fetcher = new ModelFetcher(executor, store, time);
        var fetchResult = await fetcher.FetchModelsAsync(
                new FetchModelsInput
                {
                    ChannelType = channel.Type,
                    BaseUrl = channel.BaseUrl ?? string.Empty,
                    ChannelId = channel.Id,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (fetchResult.Error is not null)
        {
            throw new InvalidOperationException(
                "model fetch returned error: " + fetchResult.Error);
        }

        if (fetchResult.Fallback)
        {
            throw new InvalidOperationException("model fetch returned fallback models");
        }

        var fetchedModelIds = fetchResult.Models.Select(static model => model.Id).ToList();

        var pattern = channel.AutoSyncModelPattern ?? string.Empty;
        if (patternOverride is not null)
        {
            pattern = patternOverride;
        }

        // An invalid pattern is logged and the filter is skipped, not fatal.
        if (pattern.Length > 0 && XRegexp.ValidateRegex(pattern) is null)
        {
            fetchedModelIds = XRegexp.Filter(fetchedModelIds, pattern);
        }

        var (updated, changed) = await store.SyncAsync(
                channel.Id,
                current => BuildPlan(current, fetchedModelIds),
                EnsurePricesAsync,
                cancellationToken)
            .ConfigureAwait(false);

        if (updated is null)
        {
            throw new InvalidOperationException("failed to reload channel for model sync: ent: channel not found");
        }

        return new ChannelModelSyncResult(
            updated,
            ParseModelList(updated.SupportedModels),
            ParseModelList(updated.ManualModels),
            changed);
    }

    /// <summary>
    /// Port of shouldRunModelSync (channel_internal.go:33-46): run at most once
    /// per aligned interval window. Go's zero-time check maps to a null field.
    /// </summary>
    public bool ShouldRunModelSync(DateTimeOffset now, AutoSyncFrequency autoSyncFrequency)
    {
        var interval = TimeSpan.FromMinutes(GetIntervalMinutes(autoSyncFrequency));
        var utcTicks = now.UtcTicks;
        var aligned = new DateTimeOffset(utcTicks - (utcTicks % interval.Ticks), TimeSpan.Zero);

        lock (_modelSyncGate)
        {
            if (_lastModelSyncExecutionTime is { } last && last == aligned)
            {
                return false;
            }

            _lastModelSyncExecutionTime = aligned;
            return true;
        }
    }

    /// <summary>
    /// Port of ChannelService.runSyncChannelModelsPeriodically
    /// (channel_internal.go:23-31). The reference registers it at cron
    /// <c>11 * * * *</c> in UTC; host scheduling is not ported yet (C158).
    /// </summary>
    public async Task RunSyncChannelModelsPeriodicallyAsync(
        CancellationToken cancellationToken = default)
    {
        var autoSyncFrequency = await frequency.GetAutoSyncFrequencyAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!ShouldRunModelSync(time.GetUtcNow(), autoSyncFrequency))
        {
            return;
        }

        await SyncAllAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Port of getIntervalMinutesFromAutoSyncFrequency (channel_internal.go:48-59).</summary>
    public static int GetIntervalMinutes(AutoSyncFrequency autoSyncFrequency) => autoSyncFrequency switch
    {
        AutoSyncFrequency.SixHours => 360,
        AutoSyncFrequency.OneDay => 1440,
        _ => 60,
    };

    /// <summary>
    /// The merge decision built inside the transaction
    /// (channel_model_sync.go:139-167).
    /// </summary>
    internal static ChannelModelSyncPlan? BuildPlan(
        ChannelRecord current,
        IReadOnlyList<string> fetchedModelIds)
    {
        var manualModels = ParseModelList(current.ManualModels);
        var mergedModels = Uniq(manualModels.Concat(fetchedModelIds));

        if (mergedModels.Count == 0)
        {
            // Both fetched and manual models are empty: nothing is written.
            return null;
        }

        var supportedModels = ParseModelList(current.SupportedModels);
        var supportedSet = new HashSet<string>(supportedModels, StringComparer.Ordinal);
        var mergedSet = new HashSet<string>(mergedModels, StringComparer.Ordinal);
        var modelsChanged = mergedModels.Any(model => !supportedSet.Contains(model))
            || supportedModels.Any(model => !mergedSet.Contains(model));

        ChannelSettingsDraft? settings;
        try
        {
            settings = ChannelWriteSerialization.ParseSettings(current.Settings);
        }
        catch (JsonException ex)
        {
            // Ent would have failed the in-transaction re-read.
            throw new StoreFailureException(
                "failed to reload channel for model sync: " + ex.Message,
                ex);
        }

        var modelProtocolsChanged = false;
        string? settingsJson = null;
        if (modelsChanged && settings is not null)
        {
            modelProtocolsChanged =
                ChannelValidation.RemoveRemovedModelProtocolOverrides(settings, mergedModels);
            if (modelProtocolsChanged)
            {
                settingsJson = GoJson.MarshalToString(settings);
            }
        }

        return new ChannelModelSyncPlan(
            mergedModels,
            GoJson.MarshalToString(mergedModels),
            modelsChanged,
            modelProtocolsChanged,
            settingsJson);
    }

    private Task<bool> EnsurePricesAsync(
        long channelId,
        IReadOnlyList<string> modelIds,
        CancellationToken cancellationToken)
        => prices.EnsureAutoPricesAsync(
            channelId,
            modelIds,
            ModelCardPricing.ToPriceJson,
            ChannelModelPriceService.GenerateReferenceId,
            cancellationToken);

    /// <summary>
    /// Decodes the raw JSON string list columns (manual_models / supported_models).
    /// Ent would have rejected a malformed row during the re-read; the sync
    /// reports the same prefix instead of silently merging against an empty
    /// list (C141 documents the lenient read used elsewhere).
    /// </summary>
    private static List<string> ParseModelList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return [];
        }

        try
        {
            var values = LlmJson.Deserialize<List<string?>>(raw);
            return values is null
                ? []
                : values.Select(static value => value ?? string.Empty).ToList();
        }
        catch (JsonException ex)
        {
            throw new StoreFailureException(
                "failed to reload channel for model sync: " + ex.Message,
                ex);
        }
    }

    /// <summary>Port of lo.Uniq: first occurrence order.</summary>
    private static List<string> Uniq(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var value in values)
        {
            if (seen.Add(value))
            {
                result.Add(value);
            }
        }

        return result;
    }
}
