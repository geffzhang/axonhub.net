using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Load-balanced candidate pool for one requested model. The grain key is the
/// request model ID; calls are serialized by Orleans, which is the same
/// serialization the reference gets from its per-channel metrics locks.
/// </summary>
public interface IChannelPoolGrain : IGrainWithStringKey
{
    /// <summary>
    /// Returns eligible channels ordered by descending score for the requested
    /// load balancer strategy. The round-robin strategy additionally moves
    /// unhealthy channels behind healthy ones; every strategy keeps only the
    /// retry-policy top-K. When <paramref name="trackSelection"/> is true the
    /// first result is counted immediately so concurrent requests spread out,
    /// exactly like <c>LoadBalancer.TrackSelection</c>.
    /// </summary>
    /// <param name="topK">
    /// Number of candidates to keep (LoadBalancer.calculateTopK). A non-positive
    /// value, or one at least as large as the candidate list, keeps every
    /// candidate.
    /// </param>
    /// <param name="loadBalancerStrategy">
    /// One of <see cref="LoadBalancerStrategies"/>. An unknown value falls back
    /// to <see cref="LoadBalancerStrategies.Adaptive"/>, like
    /// <c>resolveLoadBalancer</c>.
    /// </param>
    Task<ChannelCandidate[]> SelectAsync(
        DateTimeOffset now,
        bool trackSelection,
        int topK,
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
