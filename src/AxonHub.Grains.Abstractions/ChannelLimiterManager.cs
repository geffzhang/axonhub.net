namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Port of <c>orchestrator.ChannelLimiterManager</c>
/// (internal/server/orchestrator/channel_limiter_manager.go): owns one
/// <see cref="ChannelLimiter"/> per channel and recreates an entry whenever the
/// channel's concurrency configuration changes.
/// </summary>
/// <remarks>
/// The reference is a per-process singleton shared by the orchestrator and the
/// load balancer; the port registers one instance per silo host, which is the
/// same scope. A limiter is only allocated when <c>MaxConcurrent &gt; 0</c>;
/// channels without a concurrency limit return <see langword="null"/> so callers
/// fast-path past admission control.
/// </remarks>
public interface IChannelLimiterStatsSource
{
    bool TryGetStats(long channelId, out int inFlight, out int waiting);

    IReadOnlyList<ChannelLimiterSnapshot> Snapshot();
}

public sealed class ChannelLimiterManager(IChannelLimiterStatsSource? sharedStats = null)
{
    private readonly Dictionary<long, LimiterEntry> _entries = [];
    private readonly object _lock = new();

    /// <summary>
    /// Returns the limiter for the channel, rebuilding the entry transparently
    /// when its configuration changed. Returns <see langword="null"/> when the
    /// channel has no concurrency limit, dropping a stale entry if one exists
    /// (in-flight requests still holding a slot release into the orphaned
    /// limiter, which is collected once the last reference drops).
    /// </summary>
    public ChannelLimiter? GetOrCreate(long channelId, string channelName, ChannelRateLimitSettings? rateLimit)
    {
        var config = ExtractLimiterConfig(rateLimit);
        if (config.Capacity == 0)
        {
            lock (_lock)
            {
                _entries.Remove(channelId);
            }

            return null;
        }

        lock (_lock)
        {
            if (_entries.TryGetValue(channelId, out var existing) && existing.Config == config)
            {
                return existing.Limiter;
            }

            var limiter = new ChannelLimiter(config.Capacity, config.QueueSize, config.TimeoutMs);
            _entries[channelId] = new LimiterEntry(limiter, config, channelName);

            return limiter;
        }
    }

    /// <summary>Drops the limiter for a channel (channel update/delete path).</summary>
    public void Forget(long channelId)
    {
        lock (_lock)
        {
            _entries.Remove(channelId);
        }
    }

    /// <summary>
    /// Current load for a channel. <see langword="false"/> means the channel has
    /// no limiter, which the load balancer treats as "unlimited capacity".
    /// </summary>
    public bool TryGetStats(long channelId, out int inFlight, out int waiting)
    {
        if (sharedStats is not null)
        {
            return sharedStats.TryGetStats(channelId, out inFlight, out waiting);
        }

        ChannelLimiter limiter;
        lock (_lock)
        {
            if (!_entries.TryGetValue(channelId, out var entry))
            {
                inFlight = 0;
                waiting = 0;

                return false;
            }

            limiter = entry.Limiter;
        }

        (inFlight, waiting) = limiter.Stats();

        return true;
    }

    /// <summary>A point-in-time copy of every tracked limiter's state.</summary>
    public IReadOnlyList<ChannelLimiterSnapshot> Snapshot()
    {
        if (sharedStats is not null)
        {
            return sharedStats.Snapshot();
        }

        KeyValuePair<long, LimiterEntry>[] entries;
        lock (_lock)
        {
            entries = [.. _entries];
        }

        var snapshots = new List<ChannelLimiterSnapshot>(entries.Length);
        foreach (var (channelId, entry) in entries)
        {
            var (inFlight, waiting) = entry.Limiter.Stats();
            snapshots.Add(new ChannelLimiterSnapshot(channelId, entry.ChannelName, inFlight, waiting));
        }

        return snapshots;
    }

    /// <summary>
    /// Port of <c>extractLimiterConfig</c>: normalizes a channel's rate-limit
    /// settings for the limiter. A capacity of 0 means "no concurrency limit".
    /// Negative queue fields are treated as unset (validation rejects them on the
    /// reference write path).
    /// </summary>
    public static LimiterConfig ExtractLimiterConfig(ChannelRateLimitSettings? rateLimit)
    {
        if (rateLimit?.MaxConcurrent is not { } maxConcurrent || maxConcurrent <= 0)
        {
            return default;
        }

        return new LimiterConfig(
            Capacity: ToInt(maxConcurrent),
            QueueSize: rateLimit.QueueSize is { } queueSize && queueSize > 0 ? ToInt(queueSize) : 0,
            TimeoutMs: rateLimit.QueueTimeoutMs is { } timeoutMs && timeoutMs > 0 ? timeoutMs : 0);
    }

    /// <summary>
    /// Go's <c>int</c> is 64-bit on the supported platforms while .NET's is
    /// 32-bit; an absurd configured capacity above 2^31 is clamped rather than
    /// wrapped negative (unobservable: both mean "effectively unlimited").
    /// </summary>
    private static int ToInt(long value)
        => value > int.MaxValue ? int.MaxValue : (int)value;

    /// <summary>Runtime view of the limiter parameters; equality detects config changes.</summary>
    public readonly record struct LimiterConfig(int Capacity, int QueueSize, long TimeoutMs);

    private sealed record LimiterEntry(ChannelLimiter Limiter, LimiterConfig Config, string ChannelName);
}

/// <summary>Point-in-time limiter state for one channel (observability callback shape).</summary>
public sealed record ChannelLimiterSnapshot(long ChannelId, string ChannelName, int InFlight, int Waiting);
