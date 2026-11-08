using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.Logging;

namespace AxonHub.Grains;

/// <summary>
/// The reference's <c>enabledChannelsCache</c> as an actor: it owns the enabled
/// channel snapshot the load balancer selects from, the one-minute periodic
/// refresh, the <c>updated_at</c> fingerprint (soft-deleted rows included) and
/// the forced reload requested by channel mutations
/// (internal/server/biz/channel.go:108-430, internal/pkg/xcache/live/cache.go).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IEnabledChannelSource"/> is resolved from the silo container; the
/// grains module cannot reference the persistence module. Orleans serializes
/// every turn of this activation, replacing the reference cache's
/// <c>reloadMu</c>/singleflight pair: two reloads can never overlap.
/// </para>
/// <para>
/// Differences from the reference cache (C226): the snapshot is loaded lazily on
/// first use instead of in <c>NewChannelService</c>; readers wait for an
/// in-flight reload instead of reading the old snapshot lock-free; and the
/// reference's <c>OnSwap</c> resource work (token providers, idle connection
/// cleanup) has no counterpart because the port builds no per-channel outbound
/// objects here. The swap counter is kept for parity even though nothing in the
/// port consumes it yet.
/// </para>
/// </remarks>
public sealed class EnabledChannelsGrain(
    IEnabledChannelSource source,
    TimeProvider timeProvider,
    ILogger<EnabledChannelsGrain> logger) : Grain, IEnabledChannelsGrain
{
    /// <summary>Reference <c>RefreshInterval: time.Minute</c>.</summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);

    /// <summary>Reference <c>DebounceDelay</c> default (live/cache.go).</summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>Reference <c>RefreshTimeout</c> default (live/cache.go).</summary>
    private static readonly TimeSpan RefreshTimeout = TimeSpan.FromSeconds(30);

    private static readonly Action<ILogger, bool, Exception?> LogRefreshFailed =
        LoggerMessage.Define<bool>(
            LogLevel.Error,
            new EventId(0, "EnabledChannelsRefreshFailed"),
            "enabled channels cache refresh failed forced={Forced}");

    private List<EnabledChannelSnapshot> _channels = [];
    private long _version;
    private DateTime? _lastUpdate;
    private bool _loaded;
    private IGrainTimer? _debounceTimer;
    private DateTimeOffset _debounceDeadline;
    private bool _pendingForceRefresh;

    public override Task OnActivateAsync(CancellationToken cancellationToken)
    {
        // The reference starts its ticker in NewCache; the first tick arrives
        // one interval later, so the initial load is left to GetSnapshotAsync.
        this.RegisterGrainTimer(PeriodicRefreshAsync, RefreshInterval, RefreshInterval);
        return Task.CompletedTask;
    }

    public async Task<EnabledChannelsSnapshot> GetSnapshotAsync()
    {
        if (!_loaded)
        {
            // The reference loads synchronously in NewChannelService and panics
            // on failure; the port's first caller gets the error instead.
            await RefreshCoreAsync(force: true, CancellationToken.None).ConfigureAwait(false);
        }

        return BuildSnapshot();
    }

    public async Task<EnabledChannelsSnapshot> RefreshAsync(bool force)
    {
        // live.Cache.Load(ctx, force) runs without the cache's own timeout:
        // the caller's context bounds it. RefreshCore keeps the 30s guard.
        await RefreshCoreAsync(force, CancellationToken.None).ConfigureAwait(false);
        return BuildSnapshot();
    }

    public Task RequestRefreshAsync()
    {
        // TriggerForceAsyncReload: mark the pending force and restart the
        // debounce window. Repeated triggers within 500ms coalesce into one
        // reload (live/cache.go worker + pendingForceRefresh).
        _pendingForceRefresh = true;
        _debounceDeadline = timeProvider.GetUtcNow() + DebounceDelay;
        _debounceTimer?.Dispose();
        _debounceTimer = this.RegisterGrainTimer(
            DebounceAsync,
            DebounceDelay,
            Timeout.InfiniteTimeSpan);
        return Task.CompletedTask;
    }

    private async Task DebounceAsync(CancellationToken cancellationToken)
    {
        var remaining = _debounceDeadline - timeProvider.GetUtcNow();
        if (remaining > TimeSpan.Zero)
        {
            // Another trigger arrived while the timer was running: the
            // reference resets its debounce timer, which extends the wait.
            _debounceTimer?.Dispose();
            _debounceTimer = this.RegisterGrainTimer(
                DebounceAsync,
                remaining,
                Timeout.InfiniteTimeSpan);
            return;
        }

        _debounceTimer = null;
        var force = _pendingForceRefresh;
        _pendingForceRefresh = false;
        await RefreshSafelyAsync(force, cancellationToken).ConfigureAwait(false);
    }

    private Task PeriodicRefreshAsync(CancellationToken cancellationToken)
        => RefreshSafelyAsync(false, cancellationToken);

    private async Task RefreshSafelyAsync(bool force, CancellationToken cancellationToken)
    {
        try
        {
            await RefreshCoreAsync(force, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The reference's doRefresh recovers from panics and logs; the
            // previous snapshot keeps serving.
            LogRefreshFailed(logger, force, ex);
        }
    }

    /// <summary>
    /// Port of <c>reloadEnabledChannels</c> plus the swap half of
    /// <c>live.Cache.loadInternal</c>.
    /// </summary>
    private async Task RefreshCoreAsync(bool force, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RefreshTimeout);
        var token = timeout.Token;

        var latestUpdate = Normalize(await source.GetLatestUpdatedAtAsync(token).ConfigureAwait(false));

        if (!force)
        {
            if (latestUpdate is null)
            {
                // An empty table with nothing cached is the reference's
                // lastUpdate zero + empty current special case.
                if (_lastUpdate is null && _channels.Count == 0)
                {
                    return;
                }
            }
            else if (_lastUpdate is not null && latestUpdate.Value <= _lastUpdate.Value)
            {
                // No channel (including a soft-deleted one) changed since the
                // last successful load.
                return;
            }
        }

        var descriptors = await source.ListEnabledAsync(token).ConfigureAwait(false);

        // The reference builds each channel and skips the ones whose outbound
        // construction fails (C178); the port's descriptor projection is pure,
        // so every enabled row lands in the snapshot.
        _channels = [.. descriptors.Select(EnabledChannelSnapshot.FromDescriptor)];
        _lastUpdate = latestUpdate;
        _version++;
        _loaded = true;
    }

    private EnabledChannelsSnapshot BuildSnapshot() => new()
    {
        Channels = [.. _channels],
        Version = _version,
        LastUpdatedAt = _lastUpdate,
    };

    /// <summary>
    /// PostgreSQL returns UTC timestamps; Npgsql hands them back as
    /// <see cref="DateTimeKind.Utc"/>. Unspecified values are treated as UTC
    /// like the rest of the persistence layer, and local values are converted.
    /// </summary>
    private static DateTime? Normalize(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } utc => utc,
        { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        { } unspecified => DateTime.SpecifyKind(unspecified, DateTimeKind.Utc),
    };
}
