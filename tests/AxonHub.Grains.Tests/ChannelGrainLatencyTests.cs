using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// C034: ChannelGrain keeps separate streaming (first-token + TPS) and
/// non-streaming end-to-end EWMA samples, seeded by the first sample and then
/// blended with alpha 0.3 like channel_metrics.go:417-458.
/// </summary>
[Collection(GrainClusterCollection.Name)]
public sealed class ChannelGrainLatencyTests(GrainClusterFixture fixture)
{
    [Fact]
    public async Task StreamingAndNonStreamingSamplesUseSeparateEwmaBuckets()
    {
        var grain = fixture.Cluster.GrainFactory.GetGrain<IChannelGrain>(2601);
        await grain.ResetMetricsAsync();

        await grain.RecordLatencyAsync(new ChannelLatencySample
        {
            ChannelId = 2601,
            Streaming = true,
            FirstTokenLatencyMs = 100,
            RequestLatencyMs = 2000,
            TokensPerSecond = 20,
        });
        await grain.RecordLatencyAsync(new ChannelLatencySample
        {
            ChannelId = 2601,
            Streaming = true,
            FirstTokenLatencyMs = 300,
            RequestLatencyMs = 2200,
            TokensPerSecond = 40,
        });
        await grain.RecordLatencyAsync(new ChannelLatencySample
        {
            ChannelId = 2601,
            Streaming = false,
            RequestLatencyMs = 1000,
        });
        await grain.RecordLatencyAsync(new ChannelLatencySample
        {
            ChannelId = 2601,
            Streaming = false,
            RequestLatencyMs = 2000,
        });

        var metrics = await grain.GetMetricsAsync(DateTimeOffset.UtcNow);
        Assert.Equal(2, metrics.StreamingSampleCount);
        Assert.Equal(160, metrics.StreamingFirstTokenLatencyEwmaMs, 3);
        Assert.Equal(26, metrics.StreamingTokensPerSecondEwma, 3);
        Assert.Equal(2, metrics.NonStreamingSampleCount);
        Assert.Equal(1300, metrics.NonStreamingLatencyEwmaMs, 3);

        await grain.ResetMetricsAsync();
        metrics = await grain.GetMetricsAsync(DateTimeOffset.UtcNow);
        Assert.Equal(0, metrics.StreamingSampleCount);
        Assert.Equal(0, metrics.NonStreamingSampleCount);
    }
}
