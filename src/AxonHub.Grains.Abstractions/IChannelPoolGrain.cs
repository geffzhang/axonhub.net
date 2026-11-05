using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Load-balancing sort over a candidate list selected by the orchestrator. The
/// grain key is the request model ID; calls are serialized by Orleans, which is
/// the same serialization the reference gets from its per-channel metrics
/// locks.
/// </summary>
/// <remarks>
/// Port of orchestrator.sortCandidates (candidates.go:909-986) together with
/// LoadBalancer.SortWithoutTracking (load_balancer.go:184-330): candidates are
/// grouped by association priority, each group is scored and ranked, groups are
/// concatenated in ascending priority order until <c>requiredCount</c> is
/// reached, and only then is the first result tracked.
/// </remarks>
public interface IChannelPoolGrain : IGrainWithStringKey
{
    /// <summary>
    /// Returns the candidates ranked for the requested load balancer strategy.
    /// A non-positive <paramref name="requiredCount"/> returns an empty list. At
    /// most one candidate short-circuits to an identity result (tracking that
    /// candidate when requested). The round-robin strategy additionally moves
    /// unhealthy channels behind healthy ones inside every priority group.
    /// </summary>
    /// <param name="requiredCount">
    /// The selector's <c>requiredCount</c> (1, or 1 + MaxChannelRetries when the
    /// retry policy is enabled); also the load balancer's top-K because both are
    /// derived from the same retry policy.
    /// </param>
    /// <param name="loadBalancerStrategy">
    /// One of <see cref="LoadBalancerStrategies"/>. An unknown value falls back
    /// to <see cref="LoadBalancerStrategies.Adaptive"/>, like
    /// <c>resolveLoadBalancer</c>.
    /// </param>
    Task<ChannelCandidate[]> SortAsync(
        ChannelCandidate[] candidates,
        DateTimeOffset now,
        bool trackSelection,
        int requiredCount,
        string loadBalancerStrategy,
        CancellationToken cancellationToken = default);
}

/// <summary>Port of the <c>biz.LoadBalancerStrategy*</c> constants.</summary>
public static class LoadBalancerStrategies
{
    public const string Adaptive = "adaptive";

    public const string Failover = "failover";

    public const string CircuitBreaker = "circuit-breaker";

    public const string RoundRobin = "round-robin";
}
