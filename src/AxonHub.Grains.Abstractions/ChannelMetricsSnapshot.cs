using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Point-in-time view of a channel's runtime metrics, matching the reference
/// <c>biz.AggregatedMetrics</c> fields the round-robin load balancer consumes.
/// </summary>
[GenerateSerializer]
public sealed class ChannelMetricsSnapshot
{
    [Id(0)]
    public long ChannelId { get; set; }

    /// <summary>Selections recorded inside the reference's 10 minute window.</summary>
    [Id(1)]
    public long RequestCount { get; set; }

    [Id(2)]
    public long ConsecutiveFailures { get; set; }

    [Id(3)]
    public DateTimeOffset? LastSelectedAt { get; set; }

    [Id(4)]
    public DateTimeOffset? LastFailureAt { get; set; }

    /// <summary>Successful completions inside the same 10 minute window.</summary>
    [Id(5)]
    public long SuccessCount { get; set; }

    /// <summary>Failed completions inside the same 10 minute window.</summary>
    [Id(6)]
    public long FailureCount { get; set; }
}
