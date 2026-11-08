using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Owner of the reference's <c>enabledChannelsCache</c>
/// (internal/server/biz/channel.go:108-430 over internal/pkg/xcache/live):
/// the last successfully loaded enabled-channel snapshot, a one-minute
/// background refresh, the <c>updated_at</c> fingerprint that skips unchanged
/// reloads, forced reloads after channel mutations, and the swap counter
/// exposed as <c>ChannelService.cacheVersion</c>.
/// </summary>
/// <remarks>
/// The reference cache is process-local and starts loading when
/// <c>NewChannelService</c> is constructed (a failed initial load panics). The
/// grain loads on first use instead, so an unavailable database fails the call
/// rather than the host; a later successful call starts serving. A failed
/// background refresh keeps the last good snapshot, exactly like the
/// reference's <c>doRefresh</c>.
/// </remarks>
public interface IEnabledChannelsGrain : IGrainWithStringKey
{
    /// <summary>
    /// Returns the current snapshot, loading it first on a cold activation.
    /// <c>live.Cache.GetData</c> plus <c>GetCacheVersion</c>.
    /// </summary>
    Task<EnabledChannelsSnapshot> GetSnapshotAsync();

    /// <summary>
    /// <c>live.Cache.Load(force)</c>: a synchronous reload. With
    /// <paramref name="force"/> false the refresh function first compares the
    /// latest <c>updated_at</c> (including soft-deleted rows) with the stored
    /// fingerprint and skips if nothing changed; a forced reload always queries
    /// the enabled list and swaps, bumping the version even when the contents
    /// are identical.
    /// </summary>
    Task<EnabledChannelsSnapshot> RefreshAsync(bool force);

    /// <summary>
    /// <c>ChannelService.asyncReloadChannels</c>: requests a debounced forced
    /// reload (the reference's watcher notifies
    /// <c>live.NewForceRefreshEvent</c>; the cache worker waits 500ms to batch
    /// triggers). Returns before the reload runs; failures are logged and keep
    /// the current snapshot.
    /// </summary>
    Task RequestRefreshAsync();
}
