using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;

namespace AxonHub.Api.Tests;

/// <summary>
/// C034: performance records derive the reference's latency sample from
/// StartTime/FirstTokenTime/EndTime/CompletionTokens, and the metrics middleware
/// forwards a successful sample to the channel grain.
/// </summary>
public sealed class ChannelLatencyRecordingTests
{
    [Fact]
    public void StreamingRecordMeasuresFirstTokenAndTps()
    {
        var start = DateTimeOffset.UnixEpoch;
        var record = new ChannelPerformanceRecord
        {
            ChannelId = 1,
            StartTime = start,
            Stream = true,
        };
        record.ObserveFirstToken(start.AddMilliseconds(500));
        record.ObserveCompletionTokens(100);
        record.MarkSuccess(start.AddSeconds(2));

        var sample = Assert.IsType<ChannelLatencySample>(record.MeasureLatency());

        Assert.True(sample.Streaming);
        Assert.Equal(500, sample.FirstTokenLatencyMs);
        Assert.Equal(2000, sample.RequestLatencyMs);
        Assert.Equal(66.6667, sample.TokensPerSecond, 3);
    }

    [Fact]
    public void StreamWithoutFirstTokenFallsIntoTheNonStreamingBucket()
    {
        var start = DateTimeOffset.UnixEpoch;
        var record = new ChannelPerformanceRecord
        {
            ChannelId = 2,
            StartTime = start,
            Stream = true,
        };
        record.MarkSuccess(start.AddMilliseconds(1200));

        var sample = Assert.IsType<ChannelLatencySample>(record.MeasureLatency());

        Assert.False(sample.Streaming);
        Assert.Equal(1200, sample.RequestLatencyMs);
    }

    [Fact]
    public void SuccessfulReportRecordsASample()
    {
        var recorder = new RecordingMetricsRecorder();
        var record = new ChannelPerformanceRecord
        {
            ChannelId = 3,
            StartTime = DateTimeOffset.UtcNow.AddSeconds(-2),
        };
        var middleware = new ChannelMetricsMiddleware(
            RateLimitTestData.EmptyOutbound(),
            recorder,
            new ChannelPerformanceState { Current = record },
            TimeProvider.System);

        middleware.TransformOutboundLlmResponse(new LlmResponse());

        var sample = Assert.Single(recorder.Latencies);
        Assert.False(sample.Streaming);
        Assert.InRange(sample.RequestLatencyMs, 1900, 3000);
    }
}
