using AxonHub.Abstractions;
using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains;

/// <summary>
/// Round-robin candidate pool for one requested model.
/// <para>
/// This is the port of the reference's round-robin load balancer
/// (<c>ChatCompletionOrchestrator.roundRobinLoadBalancer</c>): the
/// <c>RoundRobinStrategy</c> score from internal/server/orchestrator/lb_strategy_rr.go,
/// the <c>RoundRobinHealthStrategy</c> partition, and the retry-policy top-K. The
/// default <c>adaptive</c> strategy chain is not ported yet (difference C012).
/// </para>
/// <para>
/// The grain key is the request model ID. Orleans serializes calls per key,
/// which replaces the reference's per-channel metrics locks.
/// </para>
/// </summary>
public sealed class ChannelPoolGrain(IEnabledChannelProvider channelProvider, IGrainFactory grainFactory)
    : Grain, IChannelPoolGrain
{
    private const double MaxScore = 150.0;
    private const double ScalingFactor = 150.0;

    // RoundRobinHealthStrategy: three consecutive failures within five minutes.
    private const long FailureThreshold = 3;
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);

    // biz.defaultRetryPolicy: Enabled = true, MaxChannelRetries = 3 → topK = 1 + 3.
    private const int MaxChannelRetries = 3;

    public async Task<ChannelCandidate[]> SelectAsync(
        DateTimeOffset now,
        bool trackSelection,
        CancellationToken cancellationToken = default)
    {
        var model = this.GetPrimaryKeyString();
        var channels = await channelProvider.ListEnabledAsync(cancellationToken);

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

            scored.Add(new ScoredCandidate(channel, entry, metrics, index));
        }

        if (scored.Count == 0)
        {
            return [];
        }

        // Descending score. Equal scores keep the provider order (ordering_weight
        // DESC); the round-robin balancer explicitly disables the weight tie-breaker.
        scored.Sort(static (left, right) =>
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : left.Index.CompareTo(right.Index);
        });

        // RoundRobinHealthStrategy: after ranking, move recently failing channels
        // behind healthy ones. The reference sorts every candidate before applying
        // the filter, then truncates to top-K.
        var ordered = new List<ScoredCandidate>(scored.Count);
        foreach (var candidate in scored)
        {
            if (!IsUnhealthy(candidate.Metrics, now))
            {
                ordered.Add(candidate);
            }
        }

        foreach (var candidate in scored)
        {
            if (IsUnhealthy(candidate.Metrics, now))
            {
                ordered.Add(candidate);
            }
        }

        var take = Math.Min(1 + MaxChannelRetries, ordered.Count);
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

    private sealed record ScoredCandidate(
        ChannelDescriptor Channel,
        ChannelModelEntry Entry,
        ChannelMetricsSnapshot Metrics,
        int Index)
    {
        public double Score => MaxScore / (1 + (Metrics.RequestCount / ScalingFactor));
    }
}
