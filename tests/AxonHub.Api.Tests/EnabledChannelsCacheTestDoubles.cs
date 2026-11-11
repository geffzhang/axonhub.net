using AxonHub.Api.Orchestration;

namespace AxonHub.Api.Tests;

/// <summary>
/// Records the cache invalidations requested by channel mutations, so tests can
/// assert the reference's reload call sites (asynchronous debounced refresh)
/// without a silo.
/// </summary>
internal sealed class RecordingEnabledChannelsRefresher : IEnabledChannelsCacheRefresher
{
    public int RequestCount { get; private set; }

    public int ReloadCount { get; private set; }

    /// <summary>Scripts a synchronous reload failure (auto-disable C236 tests).</summary>
    public Exception? ReloadFailure { get; set; }

    public void RequestRefresh() => RequestCount++;

    public Task ReloadAsync()
    {
        ReloadCount++;
        return ReloadFailure is null ? Task.CompletedTask : Task.FromException(ReloadFailure);
    }
}
