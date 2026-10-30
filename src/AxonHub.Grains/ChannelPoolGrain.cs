using AxonHub.Abstractions;
using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains;

/// <summary>
/// Load-balanced candidate pool for one requested model; the port of the four
/// reference load balancers built in internal/server/orchestrator/orchestrator.go:
/// <list type="bullet">
/// <item>adaptive = ErrorAware + WeightRoundRobin + LatencyAware + RateLimitAware</item>
/// <item>failover = Weight + Random + RateLimitAware</item>
/// <item>circuit-breaker = Weight + ModelAwareCircuitBreaker + RateLimitAware</item>
/// <item>round-robin = RoundRobin + RateLimitAware, without the weight tie-breaker
/// and with the RoundRobinHealthStrategy partition</item>
/// </list>
/// Scores are summed exactly like the reference; strategies whose data source is
/// not ported yet contribute the reference's neutral score (see
/// <see cref="LatencyNeutralScore"/>, <see cref="RateLimitNeutralScore"/> and
/// <see cref="CircuitBreakerClosedScore"/>; differences C012 residual).
/// <para>
/// The grain key is the request model ID. Orleans serializes calls per key,
/// which replaces the reference's per-channel metrics locks.
/// </para>
/// </summary>
public sealed class ChannelPoolGrain(IEnabledChannelProvider channelProvider, IGrainFactory grainFactory)
    : Grain, IChannelPoolGrain
{
    private const double RoundRobinMaxScore = 150.0;
    private const double RoundRobinScalingFactor = 150.0;

    // lb_strategy_bp.go: ErrorAwareStrategy defaults.
    private const double ErrorAwareMaxScore = 200.0;
    private const double ErrorAwareBasePenalty = 40.0;
    private const double ErrorAwarePenaltyPerFailure = 30.0;
    private static readonly TimeSpan ErrorAwareCooldown = TimeSpan.FromMinutes(5);

    // lb_strategy_latency.go: neutral score when no matching samples exist.
    private const double LatencyNeutralScore = 40.0;

    // lb_strategy_rate_limit.go: full score without RPM/TPM/concurrency limits.
    private const double RateLimitNeutralScore = 100.0;

    // lb_strategy_model_aware_circuit_breaker.go: the closed-breaker effective
    // weight is 1.0, so its contribution is maxScore (200); the strategy adds a
    // 0-0.5 random factor while the effective weight is positive.
    private const double CircuitBreakerClosedScore = 200.0;

    // lb_strategy_rr.go: RoundRobinHealthStrategy.
    private const long FailureThreshold = 3;
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);

    public async Task<ChannelCandidate[]> SelectAsync(
        DateTimeOffset now,
        bool trackSelection,
        int topK,
        string loadBalancerStrategy,
        CancellationToken cancellationToken = default)
    {
        var model = this.GetPrimaryKeyString();
        var channels = await channelProvider.ListEnabledAsync(cancellationToken);
        var strategy = ResolveStrategy(loadBalancerStrategy);

        var scored = new List<ScoredCandidate>(channels.Length);
        for (var index = 0; index < channels.Length; index++)
        {
            var channel = channels[index];
            var entries = ChannelModelEntries.Build(channel);
            if (!entries.TryGetValue(model, out var entry))
            {
                continue;
            }

            var metrics = await grainFactory
                .GetGrain<IChannelGrain>(channel.Id)
                .GetMetricsAsync(now);

            scored.Add(new ScoredCandidate(
                channel,
                entry,
                metrics,
                index,
                Score(strategy, channel, metrics, now)));
        }

        if (scored.Count == 0)
        {
            return [];
        }

        // Descending score. adaptive/failover/circuit-breaker fall back to
        // OrderingWeight DESC (the reference weight tie-breaker); round-robin
        // explicitly disables that tie-breaker and keeps input order. The final
        // index comparison only pins the reference's unspecified tie order.
        var weightTieBreaker = strategy != LoadBalancerStrategy.RoundRobin;
        scored.Sort((left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            if (weightTieBreaker)
            {
                var byWeight = right.Channel.OrderingWeight.CompareTo(left.Channel.OrderingWeight);
                if (byWeight != 0)
                {
                    return byWeight;
                }
            }

            return left.Index.CompareTo(right.Index);
        });

        // RoundRobinHealthStrategy: after ranking, move recently failing channels
        // behind healthy ones. Only the round-robin balancer attaches this filter;
        // the reference sorts every candidate before applying it, then truncates
        // to top-K. Hard-unavailable scores cannot occur here because the port's
        // RateLimitAware is always neutral (C012 residual).
        IReadOnlyList<ScoredCandidate> ordered = scored;
        if (strategy == LoadBalancerStrategy.RoundRobin)
        {
            var healthy = new List<ScoredCandidate>(scored.Count);
            var unhealthy = new List<ScoredCandidate>(scored.Count);
            foreach (var candidate in scored)
            {
                if (IsUnhealthy(candidate.Metrics, now))
                {
                    unhealthy.Add(candidate);
                }
                else
                {
                    healthy.Add(candidate);
                }
            }

            healthy.AddRange(unhealthy);
            ordered = healthy;
        }

        // calculateTopK normalizes a non-positive or too-large K to the whole
        // candidate list before sorting; taking after the full sort keeps the
        // same first K.
        var take = topK <= 0 || topK >= ordered.Count ? ordered.Count : topK;
        var result = new ChannelCandidate[take];
        for (var index = 0; index < take; index++)
        {
            var candidate = ordered[index];
            result[index] = new ChannelCandidate
            {
                ChannelId = candidate.Channel.Id,
                ChannelName = candidate.Channel.Name,
                ActualModel = candidate.Entry.ActualModel,
                OrderingWeight = candidate.Channel.OrderingWeight,
                Score = candidate.Score,
                Type = candidate.Channel.Type,
                BaseUrl = candidate.Channel.BaseUrl,
                Credentials = candidate.Channel.Credentials,
                DisabledApiKeys = candidate.Channel.DisabledApiKeys,
                RetrySettings = ChannelRetrySettings.Parse(candidate.Channel.Settings),
            };
        }

        // LoadBalancer.TrackSelection: count the selection before returning so
        // concurrent requests do not all pick the same channel.
        if (trackSelection && result.Length > 0)
        {
            await grainFactory
                .GetGrain<IChannelGrain>(result[0].ChannelId)
                .RecordSelectionAsync(now);
        }

        return result;
    }

    private static LoadBalancerStrategy ResolveStrategy(string? strategy) => strategy switch
    {
        LoadBalancerStrategies.Failover => LoadBalancerStrategy.Failover,
        LoadBalancerStrategies.CircuitBreaker => LoadBalancerStrategy.CircuitBreaker,
        LoadBalancerStrategies.RoundRobin => LoadBalancerStrategy.RoundRobin,
        // resolveLoadBalancer falls back to the adaptive balancer.
        _ => LoadBalancerStrategy.Adaptive,
    };

    private static double Score(
        LoadBalancerStrategy strategy,
        ChannelDescriptor channel,
        ChannelMetricsSnapshot metrics,
        DateTimeOffset now)
        => strategy switch
        {
            LoadBalancerStrategy.RoundRobin =>
                RoundRobinScore(metrics.RequestCount) + RateLimitNeutralScore,
            LoadBalancerStrategy.Failover =>
                WeightScore(channel.OrderingWeight)
                + (Random.Shared.NextDouble() * 0.5)
                + RateLimitNeutralScore,
            LoadBalancerStrategy.CircuitBreaker =>
                WeightScore(channel.OrderingWeight)
                + CircuitBreakerClosedScore
                + (Random.Shared.NextDouble() * 0.5)
                + RateLimitNeutralScore,
            _ =>
                ErrorAwareScore(metrics, now)
                + WeightRoundRobinScore(metrics.RequestCount, channel.OrderingWeight)
                + LatencyNeutralScore
                + RateLimitNeutralScore,
        };

    // lb_strategy_rr.go: RoundRobinStrategy.
    private static double RoundRobinScore(long requestCount)
    {
        var effectiveCount = requestCount < 0 ? 0 : (double)requestCount;
        return RoundRobinMaxScore / (1 + (effectiveCount / RoundRobinScalingFactor));
    }

    // lb_strategy_rr.go: WeightRoundRobinStrategy. Non-positive weights keep the
    // default factor of 1.0.
    private static double WeightRoundRobinScore(long requestCount, long orderingWeight)
    {
        var effectiveCount = requestCount < 0 ? 0 : (double)requestCount;
        var effectiveWeight = orderingWeight <= 0 ? 100 : orderingWeight;
        var weightFactor = effectiveWeight / 100.0;
        var normalizedCount = effectiveCount / weightFactor;
        return RoundRobinMaxScore / (1 + (normalizedCount / RoundRobinScalingFactor));
    }

    // lb_strategy_weight.go: WeightStrategy, maxScore 100 over a 0-100 weight.
    private static double WeightScore(long orderingWeight)
        => (Math.Max(0, orderingWeight) / 100.0) * 100.0;

    // lb_strategy_bp.go: ErrorAwareStrategy. Penalties decay linearly to zero
    // over five minutes; a failure without a timestamp counts as just happened.
    private static double ErrorAwareScore(ChannelMetricsSnapshot metrics, DateTimeOffset now)
    {
        var score = ErrorAwareMaxScore;
        var cooldownRatio = 0.0;
        if (metrics.LastFailureAt is { } lastFailureAt)
        {
            var timeSinceFailure = now - lastFailureAt;
            if (timeSinceFailure < ErrorAwareCooldown)
            {
                cooldownRatio = 1.0 - (timeSinceFailure.TotalMinutes / 5.0);
            }
        }
        else if (metrics.ConsecutiveFailures > 0)
        {
            cooldownRatio = 1.0;
        }

        if (metrics.ConsecutiveFailures > 0 && cooldownRatio > 0)
        {
            score -= metrics.ConsecutiveFailures * ErrorAwarePenaltyPerFailure * cooldownRatio;
        }

        if (cooldownRatio > 0)
        {
            score -= ErrorAwareBasePenalty * cooldownRatio;
        }

        return score < 0 ? 0 : score;
    }

    private static bool IsUnhealthy(ChannelMetricsSnapshot metrics, DateTimeOffset now)
    {
        if (metrics.ConsecutiveFailures < FailureThreshold)
        {
            return false;
        }

        if (metrics.LastFailureAt is not { } lastFailureAt)
        {
            return true;
        }

        return now - lastFailureAt < FailureCooldown;
    }

    private enum LoadBalancerStrategy
    {
        Adaptive,
        Failover,
        CircuitBreaker,
        RoundRobin,
    }

    private sealed record ScoredCandidate(
        ChannelDescriptor Channel,
        ChannelModelEntry Entry,
        ChannelMetricsSnapshot Metrics,
        int Index,
        double Score);
}
