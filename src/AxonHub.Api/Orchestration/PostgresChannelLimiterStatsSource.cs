using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

public sealed class PostgresChannelLimiterStatsSource(
    PostgresChannelAdmissionStore store) : IChannelLimiterStatsSource
{
    public bool TryGetStats(long channelId, out int inFlight, out int waiting)
    {
        var stats = store.GetStatsAsync(channelId).GetAwaiter().GetResult().SingleOrDefault();
        inFlight = stats?.InFlight ?? 0;
        waiting = stats?.Waiting ?? 0;
        return stats is not null;
    }

    public IReadOnlyList<ChannelLimiterSnapshot> Snapshot()
        => store.GetStatsAsync().GetAwaiter().GetResult()
            .Select(static row => new ChannelLimiterSnapshot(
                row.ChannelId, row.ChannelName, row.InFlight, row.Waiting)).ToArray();
}
