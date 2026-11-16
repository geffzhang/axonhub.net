using AxonHub.Abstractions;
using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Production <see cref="IEnabledChannelProvider"/>: reads the snapshot owned by
/// <see cref="IEnabledChannelsGrain"/> instead of querying PostgreSQL per
/// selection, matching the reference's <c>ChannelService.GetEnabledChannels()</c>
/// (<c>enabledChannelsCache.GetData()</c>).
/// </summary>
public sealed class GrainEnabledChannelProvider(
    IGrainFactory grainFactory,
    ChannelApiKeySelectionRegistry? apiKeySelection = null) : IEnabledChannelProvider
{
    public async Task<ChannelDescriptor[]> ListEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        var snapshot = await grainFactory
            .GetGrain<IEnabledChannelsGrain>(EnabledChannelsGrainKey.Default)
            .GetSnapshotAsync()
            .ConfigureAwait(false);

        // Orleans delivers a deserialized copy; the mapping only restores the
        // in-process descriptor shape the selector consumes.
        var channels = new ChannelDescriptor[snapshot.Channels.Count];
        for (var index = 0; index < snapshot.Channels.Count; index++)
        {
            channels[index] = snapshot.Channels[index].ToDescriptor();
        }

        // Port of the enabled-channel cache publishing each freshly built
        // channel (channel.go's enabledChannelsCache load): this is the only
        // read that feeds the serving set, so it is the only one that may
        // publish the generation the round_robin_success report walks. Single
        // lookups, key tests and endpoint detection never come through here.
        if (apiKeySelection is not null)
        {
            foreach (var channel in channels)
            {
                ChannelApiKeyStrategies.Publish(apiKeySelection, channel);
            }
        }

        return channels;
    }
}

/// <summary>
/// Read access to the enabled-channel cache contents plus the cache version
/// (<c>ChannelService.GetEnabledChannels()</c> and
/// <c>ChannelService.GetCacheVersion()</c> as read by
/// buildChannelModelCacheDiagnosticsExport, channel_model_cache_diagnostics.go:164).
/// </summary>
public interface IEnabledChannelCacheSnapshotProvider
{
    Task<EnabledChannelsSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Grain-backed snapshot reader. Both halves come from one activation call so
/// the diagnostics export sees the channels and the version that belong
/// together; the reference reads the two accessors back to back instead.
/// </summary>
public sealed class GrainEnabledChannelCacheSnapshotProvider(IGrainFactory grainFactory)
    : IEnabledChannelCacheSnapshotProvider
{
    public async Task<EnabledChannelsSnapshot> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        return await grainFactory
            .GetGrain<IEnabledChannelsGrain>(EnabledChannelsGrainKey.Default)
            .GetSnapshotAsync()
            .ConfigureAwait(false);
    }
}

/// <summary>
/// The cache invalidation seam for channel mutations
/// (<c>ChannelService.asyncReloadChannels</c>, channel.go:1267-1275) and for the
/// synchronous reloads that must stop selecting a channel immediately
/// (channel_apikey.go:129-139).
/// </summary>
public interface IEnabledChannelsCacheRefresher
{
    /// <summary>
    /// Debounced force refresh: the reference publishes
    /// <c>live.NewForceRefreshEvent</c> and the cache worker batches triggers
    /// over 500ms. Never blocks the mutation.
    /// </summary>
    void RequestRefresh();

    /// <summary>
    /// Synchronous force reload (<c>enabledChannelsCache.Load(ctx, true)</c>);
    /// used where the reference needs the new snapshot before the call returns.
    /// </summary>
    Task ReloadAsync();
}

/// <summary>
/// Grain-backed refresher. A failure to signal the grain is logged and ignored,
/// exactly like the reference's watcher notify warning: the periodic refresh
/// still picks the change up within one minute.
/// </summary>
public sealed class GrainEnabledChannelsCacheRefresher(
    IGrainFactory grainFactory,
    ILogger<GrainEnabledChannelsCacheRefresher>? logger = null) : IEnabledChannelsCacheRefresher
{
    private static readonly Action<ILogger, Exception?> LogNotifyFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(0, "EnabledChannelsCacheNotifyFailed"),
            "channel cache watcher notify failed");

    private IEnabledChannelsGrain Grain =>
        grainFactory.GetGrain<IEnabledChannelsGrain>(EnabledChannelsGrainKey.Default);

    public void RequestRefresh()
    {
        // The grain method only schedules its debounce timer, but the call can
        // still fail while the silo is shutting down; observe the task so the
        // failure never surfaces as unobserved.
        _ = NotifyAsync();

        async Task NotifyAsync()
        {
            try
            {
                await Grain.RequestRefreshAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (logger is not null)
                {
                    LogNotifyFailed(logger, ex);
                }
            }
        }
    }

    public async Task ReloadAsync()
    {
        await Grain.RefreshAsync(force: true).ConfigureAwait(false);
    }
}
