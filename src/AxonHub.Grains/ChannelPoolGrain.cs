using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains;

/// <summary>
/// Load-balancing sort over a candidate list, the port of
/// orchestrator.sortCandidates (candidates.go:909-986) and the four reference
/// load balancers built in internal/server/orchestrator/orchestrator.go:
/// <list type="bullet">
/// <item>adaptive = ErrorAware + WeightRoundRobin + LatencyAware + RateLimitAware</item>
/// <item>failover = Weight + Random + RateLimitAware</item>
/// <item>circuit-breaker = Weight + ModelAwareCircuitBreaker + RateLimitAware</item>
/// <item>round-robin = RoundRobin + RateLimitAware, without the weight tie-breaker
/// and with the RoundRobinHealthStrategy partition</item>
/// </list>
/// Scores are summed exactly like the reference; strategies whose data source is
/// not ported yet contribute the reference's neutral score (see
/// <see cref="LatencyNeutralScore"/> and <see cref="CircuitBreakerClosedScore"/>;
/// differences C012 residual). The RateLimitAware strategy reads RPM/TPM and
/// the 429 cooldown from the per-candidate <see cref="ChannelMetricsSnapshot"/>
/// (the channel grain's authority) and concurrency from the local per-silo
/// limiter manager (<see cref="RateLimitAwareScorer"/>).
/// <para>
/// The grain key is the request model ID. Orleans serializes calls per key,
/// which replaces the reference's per-channel metrics locks.
/// </para>
/// </summary>
public sealed class ChannelPoolGrain(
    IGrainFactory grainFactory,
    ChannelLimiterManager channelLimiterManager,
    ModelCircuitBreaker modelCircuitBreaker)
    : Grain, IChannelPoolGrain
{
    private readonly RateLimitAwareScorer _rateLimitAware = new(channelLimiterManager);

    public async Task<ChannelCandidate[]> SortAsync(
        ChannelCandidate[] candidates,
        DateTimeOffset now,
        bool trackSelection,
        int requiredCount,
        string loadBalancerStrategy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        // sortCandidates: a non-positive required count selects nothing.
        if (requiredCount <= 0)
        {
            return [];
        }

        // sortCandidates: a single candidate bypasses the balancer entirely, but
        // still counts the selection so concurrent requests spread out.
        if (candidates.Length <= 1)
        {
            if (trackSelection && candidates.Length == 1)
            {
                await TrackSelectionAsync(candidates[0].ChannelId, now);
            }

            return [.. candidates];
        }

        var strategy = ResolveStrategy(loadBalancerStrategy);

        // Group by association priority, preserving the selector's first-seen
        // order inside each group and iterating priorities ascending.
        var groups = new Dictionary<int, List<ChannelCandidate>>();
        var priorities = new List<int>();
        foreach (var candidate in candidates)
        {
            if (!groups.TryGetValue(candidate.Priority, out var group))
            {
                group = [];
                groups[candidate.Priority] = group;
                priorities.Add(candidate.Priority);
            }

            group.Add(candidate);
        }

        priorities.Sort();

        var result = new List<ChannelCandidate>(requiredCount);
        foreach (var priority in priorities)
        {
            var remaining = requiredCount - result.Count;
            if (remaining <= 0)
            {
                break;
            }

            var sorted = await SortGroupAsync(groups[priority], strategy, now, cancellationToken);

            if (sorted.Count <= remaining)
            {
                result.AddRange(sorted);
                continue;
            }

            result.AddRange(sorted.GetRange(0, remaining));
            break;
        }

        // LoadBalancer.TrackSelection: count the selection exactly once, after the
        // final list is assembled, so concurrent requests do not all pick the
        // same channel.
        if (trackSelection && result.Count > 0)
        {
            await TrackSelectionAsync(result[0].ChannelId, now);
        }

        return [.. result];
    }

    /// <summary>
    /// Port of LoadBalancer.SortWithoutTracking for one priority group
    /// (load_balancer.go:184-330), excluding the final top-K truncation: the
    /// caller already stops at <c>requiredCount</c>, and <c>requiredCount</c> is
    /// the same value the reference computes as top-K.
    /// </summary>
    private async Task<List<ChannelCandidate>> SortGroupAsync(
        List<ChannelCandidate> group,
        string strategy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (group.Count <= 1)
        {
            return [.. group];
        }

        var scored = new List<ScoredCandidate>(group.Count);
        for (var index = 0; index < group.Count; index++)
        {
            var candidate = group[index];
            var metrics = await grainFactory
                .GetGrain<IChannelGrain>(candidate.ChannelId)
                .GetMetricsAsync(now);
            cancellationToken.ThrowIfCancellationRequested();

            scored.Add(new ScoredCandidate(
                candidate,
                metrics,
                index,
                Score(strategy, candidate, metrics, now)));
        }

        // Descending score. adaptive/failover/circuit-breaker fall back to
        // OrderingWeight DESC (the reference weight tie-breaker); round-robin
        // explicitly disables that tie-breaker and keeps input order. The final
        // index comparison only pins the reference's unspecified tie order.
        var weightTieBreaker = LoadBalancerScoring.UsesWeightTieBreaker(strategy);
        scored.Sort((left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            if (weightTieBreaker)
            {
                var byWeight = right.Candidate.OrderingWeight.CompareTo(left.Candidate.OrderingWeight);
                if (byWeight != 0)
                {
                    return byWeight;
                }
            }

            return left.Index.CompareTo(right.Index);
        });

        // RoundRobinHealthStrategy: after ranking, move recently failing channels
        // behind healthy ones and hard-unavailable ones (exhausted rate limits or
        // an active cooldown) behind both. Only the round-robin balancer attaches
        // this filter (load_balancer.go:290-320).
        if (strategy != LoadBalancerStrategies.RoundRobin)
        {
            return [.. scored.Select(static item => item.Candidate)];
        }

        var healthy = new List<ChannelCandidate>(scored.Count);
        var unhealthy = new List<ChannelCandidate>(scored.Count);
        var hardUnavailable = new List<ChannelCandidate>(scored.Count);
        foreach (var candidate in scored)
        {
            if (LoadBalancerScoring.IsHardUnavailableScore(candidate.Score))
            {
                hardUnavailable.Add(candidate.Candidate);
            }
            else if (LoadBalancerScoring.IsUnhealthy(candidate.Metrics, now))
            {
                unhealthy.Add(candidate.Candidate);
            }
            else
            {
                healthy.Add(candidate.Candidate);
            }
        }

        healthy.AddRange(unhealthy);
        healthy.AddRange(hardUnavailable);
        return healthy;
    }

    private async Task TrackSelectionAsync(long channelId, DateTimeOffset now)
        => await grainFactory
            .GetGrain<IChannelGrain>(channelId)
            .RecordSelectionAsync(now);

    private static string ResolveStrategy(string? strategy) => strategy switch
    {
        // resolveLoadBalancer falls back to the adaptive balancer.
        LoadBalancerStrategies.Failover or LoadBalancerStrategies.CircuitBreaker
            or LoadBalancerStrategies.RoundRobin => strategy,
        _ => LoadBalancerStrategies.Adaptive,
    };

    private double Score(
        string strategy,
        ChannelCandidate candidate,
        ChannelMetricsSnapshot metrics,
        DateTimeOffset now)
        => LoadBalancerScoring.StrategyScore(
            strategy,
            candidate,
            metrics,
            now,
            _rateLimitAware,
            modelCircuitBreaker,
            this.GetPrimaryKeyString());

    private sealed record ScoredCandidate(
        ChannelCandidate Candidate,
        ChannelMetricsSnapshot Metrics,
        int Index,
        double Score);
}
