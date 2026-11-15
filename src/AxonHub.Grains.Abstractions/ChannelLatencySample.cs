using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// One successful request's latency sample for the reference's aggregated
/// metrics (internal/server/biz/channel_metrics.go:417-458, C034). A streaming
/// request without a first-token timestamp falls into the non-streaming bucket,
/// exactly like the reference.
/// </summary>
[GenerateSerializer]
public sealed class ChannelLatencySample
{
    [Id(0)]
    public long ChannelId { get; set; }

    /// <summary>True when this is a streaming sample (first-token + TPS).</summary>
    [Id(1)]
    public bool Streaming { get; set; }

    [Id(2)]
    public long FirstTokenLatencyMs { get; set; }

    [Id(3)]
    public long RequestLatencyMs { get; set; }

    [Id(4)]
    public double TokensPerSecond { get; set; }
}
