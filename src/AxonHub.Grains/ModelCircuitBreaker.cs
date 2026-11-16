using System.Collections.Concurrent;

namespace AxonHub.Grains;

/// <summary>Port of biz.CircuitBreakerState (model_circuit_breaker.go:15-27).</summary>
public enum ModelCircuitBreakerState
{
    Closed,
    HalfOpen,
    Open,
}

/// <summary>Snapshot of one (channel, model) circuit breaker's state.</summary>
public sealed class ModelCircuitBreakerStats
{
    public long ChannelId { get; init; }

    public string ModelId { get; init; } = string.Empty;

    public ModelCircuitBreakerState State { get; init; }

    public int ConsecutiveFailures { get; init; }

    public DateTimeOffset LastFailureAt { get; init; }

    public DateTimeOffset LastSuccessAt { get; init; }

    public DateTimeOffset NextProbeAt { get; init; }

    public int ProbeAttempts { get; init; }

    public bool ProbingInProgress { get; init; }
}

/// <summary>
/// Port of biz.ModelCircuitBreaker (internal/server/biz/model_circuit_breaker.go):
/// per-(channel, model) consecutive-failure state with closed / half-open / open
/// transitions, TTL-based lazy recovery and the probe-window weight used by the
/// circuit-breaker load balancer (C036).
/// </summary>
/// <remarks>
/// The reference keeps the default policy in memory
/// (half-open at 3, open at 5, 30-minute failure TTL, 5-minute probe interval,
/// half-open weight 0.3). The probe lease and its exponential backoff are
/// ported here; the reference's log lines are not.
/// </remarks>
public sealed class ModelCircuitBreaker
{
    public const int HalfOpenThreshold = 3;
    public const int OpenThreshold = 5;
    public const double HalfOpenWeight = 0.3;

    public static readonly TimeSpan FailureStatsTtl = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(long ChannelId, string ModelId), Stats> _stats = new();
    private readonly TimeProvider _timeProvider;

    public ModelCircuitBreaker(TimeProvider? timeProvider = null)
        => _timeProvider = timeProvider ?? TimeProvider.System;

    public double GetEffectiveWeight(
        long channelId,
        string modelId,
        DateTimeOffset now,
        double baseWeight = 1.0)
    {
        var stats = GetStatsInternal(channelId, modelId);
        lock (stats)
        {
            RecoverIfExpired(stats, now);

            return stats.State switch
            {
                ModelCircuitBreakerState.Closed => baseWeight,
                ModelCircuitBreakerState.HalfOpen => baseWeight * HalfOpenWeight,
                ModelCircuitBreakerState.Open
                    when now > stats.NextProbeAt && !stats.ProbingInProgress
                    => baseWeight * HalfOpenWeight,
                ModelCircuitBreakerState.Open => 0,
                _ => baseWeight,
            };
        }
    }

    public bool TryBeginProbe(long channelId, string modelId, DateTimeOffset now)
    {
        var stats = GetStatsInternal(channelId, modelId);
        lock (stats)
        {
            if (stats.State != ModelCircuitBreakerState.Open
                || now < stats.NextProbeAt
                || stats.ProbingInProgress)
            {
                return false;
            }

            stats.ProbingInProgress = true;
            return true;
        }
    }

    public void EndProbe(long channelId, string modelId)
    {
        var stats = GetStatsInternal(channelId, modelId);
        lock (stats)
        {
            stats.ProbingInProgress = false;
        }
    }

    public void RecordSuccess(long channelId, string modelId)
    {
        var stats = GetStatsInternal(channelId, modelId);
        lock (stats)
        {
            stats.State = ModelCircuitBreakerState.Closed;
            stats.ConsecutiveFailures = 0;
            stats.LastSuccessAt = _timeProvider.GetUtcNow();
            stats.NextProbeAt = default;
            stats.ProbeAttempts = 0;
            stats.ProbingInProgress = false;
        }
    }

    public void RecordError(long channelId, string modelId, bool wasProbe = false)
    {
        var stats = GetStatsInternal(channelId, modelId);
        lock (stats)
        {
            var now = _timeProvider.GetUtcNow();
            if (stats.ConsecutiveFailures > 0
                && now - stats.LastFailureAt > FailureStatsTtl)
            {
                stats.ConsecutiveFailures = 0;
            }

            stats.ConsecutiveFailures++;
            stats.LastFailureAt = now;

            if (stats.ConsecutiveFailures >= OpenThreshold)
            {
                if (stats.State != ModelCircuitBreakerState.Open)
                {
                    stats.State = ModelCircuitBreakerState.Open;
                    stats.NextProbeAt = now + ProbeInterval;
                    stats.ProbeAttempts = 0;
                }
                else if (wasProbe)
                {
                    var multiplier = Math.Pow(2, stats.ProbeAttempts);
                    if (multiplier > 8)
                    {
                        multiplier = 8;
                    }

                    stats.NextProbeAt = now + TimeSpan.FromTicks(
                        (long)(ProbeInterval.Ticks * multiplier));
                    stats.ProbeAttempts++;
                }
            }
            else if (stats.ConsecutiveFailures >= HalfOpenThreshold)
            {
                stats.State = ModelCircuitBreakerState.HalfOpen;
            }
        }
    }

    public ModelCircuitBreakerStats GetStats(long channelId, string modelId)
    {
        var stats = GetStatsInternal(channelId, modelId);
        lock (stats)
        {
            return Snapshot(stats);
        }
    }

    public void Reset(long channelId, string modelId)
    {
        var stats = GetStatsInternal(channelId, modelId);
        lock (stats)
        {
            ResetCore(stats);
        }
    }

    private Stats GetStatsInternal(long channelId, string modelId)
    {
        var key = (channelId, modelId ?? string.Empty);
        return _stats.GetOrAdd(
            key,
            static k => new Stats
            {
                ChannelId = k.ChannelId,
                ModelId = k.ModelId,
                LastSuccessAt = DateTimeOffset.UtcNow,
            });
    }

    private void RecoverIfExpired(Stats stats, DateTimeOffset now)
    {
        if (stats.State != ModelCircuitBreakerState.Closed
            && stats.ConsecutiveFailures > 0
            && stats.LastFailureAt != default
            && now - stats.LastFailureAt > FailureStatsTtl)
        {
            ResetCore(stats);
        }
    }

    private static void ResetCore(Stats stats)
    {
        stats.State = ModelCircuitBreakerState.Closed;
        stats.ConsecutiveFailures = 0;
        stats.NextProbeAt = default;
        stats.ProbeAttempts = 0;
        stats.ProbingInProgress = false;
    }

    private static ModelCircuitBreakerStats Snapshot(Stats stats)
        => new()
        {
            ChannelId = stats.ChannelId,
            ModelId = stats.ModelId,
            State = stats.State,
            ConsecutiveFailures = stats.ConsecutiveFailures,
            LastFailureAt = stats.LastFailureAt,
            LastSuccessAt = stats.LastSuccessAt,
            NextProbeAt = stats.NextProbeAt,
            ProbeAttempts = stats.ProbeAttempts,
            ProbingInProgress = stats.ProbingInProgress,
        };

    private sealed class Stats
    {
        public long ChannelId { get; init; }

        public string ModelId { get; init; } = string.Empty;

        public ModelCircuitBreakerState State { get; set; } = ModelCircuitBreakerState.Closed;

        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset LastFailureAt { get; set; }

        public DateTimeOffset LastSuccessAt { get; set; }

        public DateTimeOffset NextProbeAt { get; set; }

        public int ProbeAttempts { get; set; }

        public bool ProbingInProgress { get; set; }
    }
}
