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
    /// Returns eligible channels ordered by descending round-robin score, with
    /// unhealthy channels moved behind healthy ones and only the retry-policy
    /// top-K retained. When <paramref name="trackSelection"/> is true the first
    /// result is counted immediately so concurrent requests spread out, exactly
    /// like <c>LoadBalancer.TrackSelection</c>.
    /// </summary>
    /// <param name="topK">
    /// Number of candidates to keep (LoadBalancer.calculateTopK). A non-positive
    /// value, or one at least as large as the candidate list, keeps every
    /// candidate.
    /// </param>
    Task<ChannelCandidate[]> SelectAsync(
        DateTimeOffset now,
        bool trackSelection,
        int topK,
        CancellationToken cancellationToken = default);
}
