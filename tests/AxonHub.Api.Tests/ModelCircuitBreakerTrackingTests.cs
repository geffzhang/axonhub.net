using AxonHub.Api.Orchestration;
using AxonHub.Grains;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;

namespace AxonHub.Api.Tests;

/// <summary>
/// C036: the performance middleware records the (channel, model) outcome only
/// when the effective load balancer strategy is circuit-breaker, mirroring the
/// reference's modelCircuitBreakerTracker.
/// </summary>
public sealed class ModelCircuitBreakerTrackingTests
{
    [Fact]
    public void SuccessClosesAnOpenModelCircuit()
    {
        var breaker = new ModelCircuitBreaker();
        for (var i = 0; i < ModelCircuitBreaker.OpenThreshold; i++)
        {
            breaker.RecordError(1, "m");
        }

        Assert.Equal(ModelCircuitBreakerState.Open, breaker.GetStats(1, "m").State);

        Middleware(breaker, track: true)
            .TransformOutboundLlmResponse(new LlmResponse());

        Assert.Equal(ModelCircuitBreakerState.Closed, breaker.GetStats(1, "m").State);
    }

    [Fact]
    public void FailuresOpenTheModelCircuit()
    {
        var breaker = new ModelCircuitBreaker();
        var middleware = Middleware(breaker, track: true);

        for (var i = 0; i < ModelCircuitBreaker.OpenThreshold; i++)
        {
            middleware.OnOutboundRawError(new InvalidOperationException("boom"));
        }

        var stats = breaker.GetStats(1, "m");
        Assert.Equal(ModelCircuitBreakerState.Open, stats.State);
        Assert.Equal(ModelCircuitBreaker.OpenThreshold, stats.ConsecutiveFailures);
    }

    [Fact]
    public void OtherStrategiesDoNotTrackTheModelCircuit()
    {
        var breaker = new ModelCircuitBreaker();
        var middleware = Middleware(breaker, track: false);

        for (var i = 0; i < ModelCircuitBreaker.OpenThreshold; i++)
        {
            middleware.OnOutboundRawError(new InvalidOperationException("boom"));
        }

        Assert.Equal(ModelCircuitBreakerState.Closed, breaker.GetStats(1, "m").State);
        Assert.Equal(0, breaker.GetStats(1, "m").ConsecutiveFailures);
    }

    private static ChannelMetricsMiddleware Middleware(
        ModelCircuitBreaker breaker,
        bool track)
    {
        var state = new ChannelPerformanceState
        {
            Current = new ChannelPerformanceRecord
            {
                ChannelId = 1,
                ModelId = "m",
                StartTime = DateTimeOffset.UtcNow,
            },
        };

        return new ChannelMetricsMiddleware(
            RateLimitTestData.EmptyOutbound(),
            new NoopMetricsRecorder(),
            state,
            TimeProvider.System,
            apiKeySelection: null,
            autoDisableQueue: null,
            modelCircuitBreaker: breaker,
            trackModelCircuitBreaker: track);
    }

    private sealed class NoopMetricsRecorder : IChannelMetricsRecorder
    {
        public void RecordSelection(long channelId, DateTimeOffset selectedAt)
        {
        }

        public void RecordSuccess(long channelId, DateTimeOffset completedAt)
        {
        }

        public void RecordFailure(long channelId, DateTimeOffset completedAt)
        {
        }
    }
}
