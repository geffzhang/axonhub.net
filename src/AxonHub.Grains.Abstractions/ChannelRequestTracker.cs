namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Port of <c>orchestrator.ChannelRequestTracker</c>
/// (internal/server/orchestrator/channel_request_tracker.go): per-channel request
/// and token counts in fixed natural-minute buckets, plus the 429 cooldown map.
/// </summary>
/// <remarks>
/// The reference reads the wall clock directly; the port takes a
/// <see cref="TimeProvider"/> so tests can move between minute buckets without
/// sleeping. The state machine has two hosts: one instance per channel inside
/// <see cref="IChannelGrain"/> (the serving authority), and the standalone
/// in-memory instance used by direct test constructions and the API's
/// in-memory recorder seam. Every public operation has an explicit
/// <c>now</c> overload so a grain turn and its snapshot observe one instant.
/// </remarks>
public sealed class ChannelRequestTracker
{
    private readonly Dictionary<long, RateLimitWindow> _counters = [];
    private readonly Dictionary<long, DateTimeOffset> _cooldowns = [];
    private readonly object _lock = new();
    private readonly TimeProvider _timeProvider;

    public ChannelRequestTracker(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Atomically checks and consumes one request slot in the current minute
    /// bucket. A non-positive limit means "no RPM limit", so no slot is consumed.
    /// </summary>
    public bool TryAcquireRequest(long channelId, long limit)
        => TryAcquireRequest(channelId, limit, _timeProvider.GetUtcNow());

    /// <summary>Explicit-clock overload used by the channel grain.</summary>
    public bool TryAcquireRequest(long channelId, long limit, DateTimeOffset now)
    {
        if (limit <= 0)
        {
            return true;
        }

        lock (_lock)
        {
            var window = GetOrResetWindow(channelId, now);
            if (window.Requests >= limit)
            {
                return false;
            }

            window.Requests++;

            return true;
        }
    }

    /// <summary>Adds tokens to the channel's current window; non-positive values are ignored.</summary>
    public void AddTokens(long channelId, long tokens)
        => AddTokens(channelId, tokens, _timeProvider.GetUtcNow());

    /// <summary>Explicit-clock overload used by the channel grain.</summary>
    public void AddTokens(long channelId, long tokens, DateTimeOffset now)
    {
        if (tokens <= 0)
        {
            return;
        }

        lock (_lock)
        {
            GetOrResetWindow(channelId, now).Tokens += tokens;
        }
    }

    /// <summary>Requests consumed in the current window; 0 for a stale window.</summary>
    public long GetRequestCount(long channelId)
        => GetRequestCount(channelId, _timeProvider.GetUtcNow());

    /// <summary>Explicit-clock overload used by the channel grain.</summary>
    public long GetRequestCount(long channelId, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_counters.TryGetValue(channelId, out var window)
                || window.WindowStart != WindowStart(now))
            {
                return 0;
            }

            return window.Requests;
        }
    }

    /// <summary>Tokens consumed in the current window; 0 for a stale window.</summary>
    public long GetTokenCount(long channelId)
        => GetTokenCount(channelId, _timeProvider.GetUtcNow());

    /// <summary>Explicit-clock overload used by the channel grain.</summary>
    public long GetTokenCount(long channelId, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_counters.TryGetValue(channelId, out var window)
                || window.WindowStart != WindowStart(now))
            {
                return 0;
            }

            return window.Tokens;
        }
    }

    /// <summary>
    /// Sets a cooldown until the given instant. Only extends: a shorter value
    /// never overwrites a longer existing one.
    /// </summary>
    public void SetCooldown(long channelId, DateTimeOffset until)
    {
        lock (_lock)
        {
            if (_cooldowns.TryGetValue(channelId, out var existing) && existing > until)
            {
                return;
            }

            _cooldowns[channelId] = until;
        }
    }

    /// <summary>
    /// Whether the channel is cooling down. Expired entries are removed lazily.
    /// </summary>
    public bool IsCoolingDown(long channelId) => TryGetCooldownUntil(channelId, out _);

    /// <summary>Explicit-clock overload used by the channel grain.</summary>
    public bool IsCoolingDown(long channelId, DateTimeOffset now) => TryGetCooldownUntil(channelId, now, out _);

    /// <summary>
    /// The cooldown expiration time, or <see langword="false"/> when the channel
    /// is not cooling down (an expired entry is cleaned up first).
    /// </summary>
    public bool TryGetCooldownUntil(long channelId, out DateTimeOffset until)
        => TryGetCooldownUntil(channelId, _timeProvider.GetUtcNow(), out until);

    /// <summary>Explicit-clock overload used by the channel grain.</summary>
    public bool TryGetCooldownUntil(long channelId, DateTimeOffset now, out DateTimeOffset until)
    {
        lock (_lock)
        {
            if (!_cooldowns.TryGetValue(channelId, out until))
            {
                return false;
            }
        }

        if (now > until)
        {
            ClearExpiredCooldown(channelId, until, now);

            return false;
        }

        return true;
    }

    /// <summary>Drops the counters and cooldown of one channel (grain reset seam).</summary>
    public void Clear(long channelId)
    {
        lock (_lock)
        {
            _counters.Remove(channelId);
            _cooldowns.Remove(channelId);
        }
    }

    /// <summary>
    /// Removes an expired cooldown entry only if it still matches the value the
    /// caller observed, so a newer <see cref="SetCooldown"/> is never lost.
    /// </summary>
    internal void ClearExpiredCooldown(long channelId, DateTimeOffset observedUntil, DateTimeOffset now)
    {
        lock (_lock)
        {
            if (!_cooldowns.TryGetValue(channelId, out var currentUntil))
            {
                return;
            }

            if (currentUntil == observedUntil && now > currentUntil)
            {
                _cooldowns.Remove(channelId);
            }
        }
    }

    /// <summary>Test seam mirroring the Go package-internal map access.</summary>
    internal bool HasCooldownEntry(long channelId)
    {
        lock (_lock)
        {
            return _cooldowns.ContainsKey(channelId);
        }
    }

    private RateLimitWindow GetOrResetWindow(long channelId, DateTimeOffset now)
    {
        var windowStart = WindowStart(now);
        if (!_counters.TryGetValue(channelId, out var window) || window.WindowStart != windowStart)
        {
            window = new RateLimitWindow { WindowStart = windowStart };
            _counters[channelId] = window;
        }

        return window;
    }

    /// <summary>Go: <c>time.Now().Truncate(time.Minute)</c> — the natural minute boundary.</summary>
    private static DateTimeOffset WindowStart(DateTimeOffset now)
        => now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMinute));

    private sealed class RateLimitWindow
    {
        public long Requests { get; set; }

        public long Tokens { get; set; }

        public DateTimeOffset WindowStart { get; set; }
    }
}
