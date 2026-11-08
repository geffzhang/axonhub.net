using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains;

/// <summary>
/// Per-channel runtime authority. A port of the reference's
/// <c>channelMetrics</c> (internal/server/biz/channel_metrics.go) plus the
/// per-channel slice of <c>orchestrator.ChannelRequestTracker</c>: selections,
/// successes and failures land in per-second slots that expire after ten
/// minutes, while consecutive failures persist until the next success; RPM/TPM
/// live in natural-minute buckets and the 429 cooldown is extended-only.
/// </summary>
/// <remarks>
/// Orleans serializes every turn of one activation, replacing the reference's
/// per-channel metrics locks and the tracker's process-wide lock. The
/// rate-limit state is returned in <see cref="GetMetricsAsync"/> so
/// <c>ChannelPoolGrain</c> scores from the same snapshot it already fetches per
/// candidate; the limiter itself stays in the per-silo
/// <c>ChannelLimiterManager</c> because its blocking FIFO gate cannot move into
/// a grain scheduler (C042).
/// </remarks>
public sealed class ChannelGrain : Grain, IChannelGrain
{
    private const int WindowSeconds = 600;

    private readonly SortedDictionary<long, Slot> _slots = [];
    private readonly ChannelRequestTracker _rateLimit = new();
    private long _windowCutoff;
    private long _requestCount;
    private long _successCount;
    private long _failureCount;
    private long _consecutiveFailures;
    private DateTimeOffset? _lastSelectedAt;
    private DateTimeOffset? _lastFailureAt;

    private long ChannelId => this.GetPrimaryKeyLong();

    public Task<ChannelMetricsSnapshot> GetMetricsAsync(DateTimeOffset now)
    {
        Prune(now.ToUnixTimeSeconds() - WindowSeconds);

        var hasCooldown = _rateLimit.TryGetCooldownUntil(ChannelId, now, out var cooldownUntil);

        return Task.FromResult(new ChannelMetricsSnapshot
        {
            ChannelId = ChannelId,
            RequestCount = _requestCount,
            SuccessCount = _successCount,
            FailureCount = _failureCount,
            ConsecutiveFailures = _consecutiveFailures,
            LastSelectedAt = _lastSelectedAt,
            LastFailureAt = _lastFailureAt,
            RateLimitRequests = _rateLimit.GetRequestCount(ChannelId, now),
            RateLimitTokens = _rateLimit.GetTokenCount(ChannelId, now),
            CooldownUntil = hasCooldown ? cooldownUntil : null,
        });
    }

    public Task RecordSelectionAsync(DateTimeOffset selectedAt)
    {
        if (TryGetSlot(selectedAt) is not { } slot)
        {
            return Task.CompletedTask;
        }

        slot.Requests++;
        _requestCount++;

        if (_lastSelectedAt is null || _lastSelectedAt < selectedAt)
        {
            _lastSelectedAt = selectedAt;
        }

        return Task.CompletedTask;
    }

    public Task RecordSuccessAsync(DateTimeOffset completedAt)
    {
        if (TryGetSlot(completedAt) is not { } slot)
        {
            return Task.CompletedTask;
        }

        slot.Successes++;
        _successCount++;
        _consecutiveFailures = 0;

        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(DateTimeOffset completedAt)
    {
        if (TryGetSlot(completedAt) is not { } slot)
        {
            return Task.CompletedTask;
        }

        slot.Failures++;
        _failureCount++;
        _consecutiveFailures++;

        if (_lastFailureAt is null || _lastFailureAt < completedAt)
        {
            _lastFailureAt = completedAt;
        }

        return Task.CompletedTask;
    }

    public Task ResetMetricsAsync()
    {
        _slots.Clear();
        _windowCutoff = 0;
        _requestCount = 0;
        _successCount = 0;
        _failureCount = 0;
        _consecutiveFailures = 0;
        _lastSelectedAt = null;
        _lastFailureAt = null;

        return Task.CompletedTask;
    }

    public Task<bool> TryAcquireRequestAsync(long limit, DateTimeOffset now)
        => Task.FromResult(_rateLimit.TryAcquireRequest(ChannelId, limit, now));

    public Task AddTokensAsync(long tokens, DateTimeOffset now)
    {
        _rateLimit.AddTokens(ChannelId, tokens, now);

        return Task.CompletedTask;
    }

    public Task SetCooldownAsync(DateTimeOffset until)
    {
        _rateLimit.SetCooldown(ChannelId, until);

        return Task.CompletedTask;
    }

    public Task ResetRateLimitAsync()
    {
        _rateLimit.Clear(ChannelId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the slot for <paramref name="at"/>, or null when the event is
    /// older than the window. Mirrors <c>getOrCreateTimeSlot</c>: expiry is
    /// evaluated against the event's own timestamp and the cutoff is monotonic.
    /// </summary>
    private Slot? TryGetSlot(DateTimeOffset at)
    {
        var second = at.ToUnixTimeSeconds();
        Prune(second - WindowSeconds);

        // A late event older than the already-expired window is dropped instead
        // of re-entering it, matching channelMetrics.getOrCreateTimeSlot.
        if (second < _windowCutoff)
        {
            return null;
        }

        if (!_slots.TryGetValue(second, out var slot))
        {
            slot = new Slot();
            _slots.Add(second, slot);
        }

        return slot;
    }

    private void Prune(long cutoffSecond)
    {
        _windowCutoff = Math.Max(cutoffSecond, _windowCutoff);

        while (_slots.Count > 0)
        {
            var oldest = _slots.First();
            if (oldest.Key >= cutoffSecond)
            {
                return;
            }

            _requestCount -= oldest.Value.Requests;
            _successCount -= oldest.Value.Successes;
            _failureCount -= oldest.Value.Failures;
            _slots.Remove(oldest.Key);
        }
    }

    private sealed class Slot
    {
        public long Requests { get; set; }

        public long Successes { get; set; }

        public long Failures { get; set; }
    }
}
