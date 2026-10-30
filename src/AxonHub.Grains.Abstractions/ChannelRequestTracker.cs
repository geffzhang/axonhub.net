namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Port of <c>orchestrator.ChannelRequestTracker</c>
/// (internal/server/orchestrator/channel_request_tracker.go): per-channel request
/// and token counts in fixed natural-minute buckets, plus the 429 cooldown map.
/// </summary>
/// <remarks>
/// The reference reads the wall clock directly; the port takes a
/// <see cref="TimeProvider"/> so tests can move between minute buckets without
/// sleeping. Registered as a per-silo singleton and shared by RPM admission,
/// TPM/cooldown tracking and the RateLimitAware load-balancer strategy, exactly
/// like the reference's per-process instance.
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
    {
        if (limit <= 0)
        {
            return true;
        }

        lock (_lock)
        {
            var window = GetOrResetWindow(channelId);
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
    {
        if (tokens <= 0)
        {
            return;
        }

        lock (_lock)
        {
            GetOrResetWindow(channelId).Tokens += tokens;
        }
    }

    /// <summary>Requests consumed in the current window; 0 for a stale window.</summary>
    public long GetRequestCount(long channelId)
    {
        lock (_lock)
        {
            if (!_counters.TryGetValue(channelId, out var window)
                || window.WindowStart != CurrentWindowStart())
            {
                return 0;
            }

            return window.Requests;
        }
    }

    /// <summary>Tokens consumed in the current window; 0 for a stale window.</summary>
    public long GetTokenCount(long channelId)
    {
        lock (_lock)
        {
            if (!_counters.TryGetValue(channelId, out var window)
                || window.WindowStart != CurrentWindowStart())
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

    /// <summary>
    /// The cooldown expiration time, or <see langword="false"/> when the channel
    /// is not cooling down (an expired entry is cleaned up first).
    /// </summary>
    public bool TryGetCooldownUntil(long channelId, out DateTimeOffset until)
    {
        lock (_lock)
        {
            if (!_cooldowns.TryGetValue(channelId, out until))
            {
                return false;
            }
        }

        var now = _timeProvider.GetUtcNow();
        if (now > until)
        {
            ClearExpiredCooldown(channelId, until, now);

            return false;
        }

        return true;
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

    private RateLimitWindow GetOrResetWindow(long channelId)
    {
        var windowStart = CurrentWindowStart();
        if (!_counters.TryGetValue(channelId, out var window) || window.WindowStart != windowStart)
        {
            window = new RateLimitWindow { WindowStart = windowStart };
            _counters[channelId] = window;
        }

        return window;
    }

    /// <summary>Go: <c>time.Now().Truncate(time.Minute)</c> — the natural minute boundary.</summary>
    private DateTimeOffset CurrentWindowStart()
    {
        var now = _timeProvider.GetUtcNow();

        return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMinute));
    }

    private sealed class RateLimitWindow
    {
        public long Requests { get; set; }

        public long Tokens { get; set; }

        public DateTimeOffset WindowStart { get; set; }
    }
}
