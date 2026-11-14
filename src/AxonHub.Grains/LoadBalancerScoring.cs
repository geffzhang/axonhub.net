using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains;

/// <summary>
/// The scoring half of the reference load balancers
/// (internal/server/orchestrator/lb_strategy_*.go). Kept as pure functions so
/// the score values the reference's own unit tests assert can be tested without
/// a silo; <see cref="ChannelPoolGrain"/> composes them into the sort order.
/// </summary>
/// <remarks>
/// Strategies whose data source is not ported yet contribute the reference's
/// neutral score (<see cref="LatencyNeutralScore"/> for LatencyAware and
/// <see cref="CircuitBreakerClosedScore"/> for the closed ModelAwareCircuitBreaker;
/// differences C012 residual).
/// </remarks>
public static class LoadBalancerScoring
{
    // lb_strategy_rr.go: RoundRobinStrategy.
    public const double RoundRobinMaxScore = 150.0;
    public const double RoundRobinScalingFactor = 150.0;

    // lb_strategy_bp.go: ErrorAwareStrategy defaults.
    public const double ErrorAwareMaxScore = 200.0;
    public const double ErrorAwareBasePenalty = 40.0;
    public const double ErrorAwarePenaltyPerFailure = 30.0;
    public static readonly TimeSpan ErrorAwareCooldown = TimeSpan.FromMinutes(5);

    // lb_strategy_latency.go: neutral score when no matching samples exist.
    public const double LatencyNeutralScore = 40.0;

    // lb_strategy_model_aware_circuit_breaker.go: the closed-breaker effective
    // weight is 1.0, so its contribution is maxScore (200); the strategy adds a
    // 0-0.5 random factor while the effective weight is positive.
    public const double CircuitBreakerClosedScore = 200.0;

    // lb_strategy_rr.go: RoundRobinHealthStrategy.
    public const long FailureThreshold = 3;
    public static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Sums the strategies registered on the balancer for
    /// <paramref name="loadBalancerStrategy"/>, in the reference's registration
    /// order. Every reference balancer appends RateLimitAware last.
    /// </summary>
    public static double StrategyScore(
        string loadBalancerStrategy,
        ChannelCandidate candidate,
        ChannelMetricsSnapshot metrics,
        DateTimeOffset now,
        RateLimitAwareScorer rateLimitAware,
        ModelCircuitBreaker? modelCircuitBreaker = null,
        string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(rateLimitAware);

        // The metrics snapshot is the channel grain's rate-limit authority, so
        // scoring does not reach back into a per-silo tracker.
        var rateLimitScore = rateLimitAware.Score(
            candidate.ChannelId,
            new ChannelRateLimitState(
                metrics.RateLimitRequests,
                metrics.RateLimitTokens,
                metrics.CooldownUntil),
            candidate.RateLimitSettings);

        return loadBalancerStrategy switch
        {
            LoadBalancerStrategies.RoundRobin =>
                RoundRobinScore(metrics.RequestCount) + rateLimitScore,
            LoadBalancerStrategies.Failover =>
                WeightScore(candidate.OrderingWeight)
                + (Random.Shared.NextDouble() * 0.5)
                + rateLimitScore,
            LoadBalancerStrategies.CircuitBreaker =>
                WeightScore(candidate.OrderingWeight)
                + CircuitBreakerScore(candidate, now, modelCircuitBreaker, modelId)
                + rateLimitScore,
            _ =>
                ErrorAwareScore(metrics, now)
                + WeightRoundRobinScore(metrics.RequestCount, candidate.OrderingWeight)
                + LatencyNeutralScore
                + rateLimitScore,
        };
    }

    /// <summary>
    /// lb_strategy_model_aware_circuit_breaker.go: the effective weight is 1.0
    /// while closed, 0.3 while half-open, and 0 while open unless the probe
    /// window has opened. A positive weight contributes maxScore plus the
    /// reference's 0-0.5 random tie-breaker.
    /// </summary>
    private static double CircuitBreakerScore(
        ChannelCandidate candidate,
        DateTimeOffset now,
        ModelCircuitBreaker? modelCircuitBreaker,
        string? modelId)
    {
        var weight = modelCircuitBreaker is null || string.IsNullOrEmpty(modelId)
            ? 1.0
            : modelCircuitBreaker.GetEffectiveWeight(candidate.ChannelId, modelId, now);
        var score = weight * CircuitBreakerClosedScore;
        if (weight > 0)
        {
            score += Random.Shared.NextDouble() * 0.5;
        }

        return score;
    }

    /// <summary>
    /// adaptive/failover/circuit-breaker fall back to OrderingWeight DESC as the
    /// tie-breaker; round-robin explicitly disables it and keeps input order.
    /// </summary>
    public static bool UsesWeightTieBreaker(string loadBalancerStrategy)
        => loadBalancerStrategy != LoadBalancerStrategies.RoundRobin;

    // lb_strategy_rr.go: RoundRobinStrategy.
    public static double RoundRobinScore(long requestCount)
    {
        var effectiveCount = requestCount < 0 ? 0 : (double)requestCount;
        return RoundRobinMaxScore / (1 + (effectiveCount / RoundRobinScalingFactor));
    }

    // lb_strategy_rr.go: WeightRoundRobinStrategy. Non-positive weights keep the
    // default factor of 1.0.
    public static double WeightRoundRobinScore(long requestCount, long orderingWeight)
    {
        var effectiveCount = requestCount < 0 ? 0 : (double)requestCount;
        var effectiveWeight = orderingWeight <= 0 ? 100 : orderingWeight;
        var weightFactor = effectiveWeight / 100.0;
        var normalizedCount = effectiveCount / weightFactor;
        return RoundRobinMaxScore / (1 + (normalizedCount / RoundRobinScalingFactor));
    }

    // lb_strategy_weight.go: WeightStrategy, maxScore 100 over a 0-100 weight.
    public static double WeightScore(long orderingWeight)
        => (Math.Max(0, orderingWeight) / 100.0) * 100.0;

    // lb_strategy_bp.go: ErrorAwareStrategy. Penalties decay linearly to zero
    // over five minutes; a failure without a timestamp counts as just happened.
    public static double ErrorAwareScore(ChannelMetricsSnapshot metrics, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(metrics);

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

    // load_balancer.go:317: a score at or below half the exhausted score can only
    // come from RateLimitAware's hard-unavailable branches (cooldown, exhausted
    // RPM/TPM, or fully saturated concurrency).
    public static bool IsHardUnavailableScore(double score)
        => score <= RateLimitAwareScorer.ExhaustedScore / 2;

    // lb_strategy_rr.go: RoundRobinHealthStrategy.
    public static bool IsUnhealthy(ChannelMetricsSnapshot metrics, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(metrics);

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
}
