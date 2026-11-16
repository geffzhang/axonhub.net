using AxonHub.Grains;

namespace AxonHub.Grains.Tests;

/// <summary>
/// C036: per-(channel, model) consecutive-failure state, half-open/open weights,
/// the probe window and TTL recovery, mirrored from
/// internal/server/biz/model_circuit_breaker.go.
/// </summary>
public sealed class ModelCircuitBreakerTests
{
    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void ConsecutiveFailuresMoveThroughHalfOpenAndOpen()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var breaker = new ModelCircuitBreaker(time);
        var now = time.GetUtcNow();

        Assert.Equal(1.0, breaker.GetEffectiveWeight(1, "m", now));

        breaker.RecordError(1, "m");
        breaker.RecordError(1, "m");
        Assert.Equal(ModelCircuitBreakerState.Closed, breaker.GetStats(1, "m").State);

        breaker.RecordError(1, "m");
        Assert.Equal(ModelCircuitBreakerState.HalfOpen, breaker.GetStats(1, "m").State);
        Assert.Equal(0.3, breaker.GetEffectiveWeight(1, "m", now), 3);

        breaker.RecordError(1, "m");
        breaker.RecordError(1, "m");
        Assert.Equal(ModelCircuitBreakerState.Open, breaker.GetStats(1, "m").State);
        Assert.Equal(0, breaker.GetEffectiveWeight(1, "m", now));
    }

    [Fact]
    public void ProbeWindowAndSuccessRecover()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var breaker = new ModelCircuitBreaker(time);
        for (var i = 0; i < ModelCircuitBreaker.OpenThreshold; i++)
        {
            breaker.RecordError(7, "m");
        }

        var now = time.GetUtcNow();
        Assert.Equal(0, breaker.GetEffectiveWeight(7, "m", now));

        time.Advance(ModelCircuitBreaker.ProbeInterval + TimeSpan.FromSeconds(1));
        var probeNow = time.GetUtcNow();
        Assert.Equal(0.3, breaker.GetEffectiveWeight(7, "m", probeNow), 3);
        Assert.True(breaker.TryBeginProbe(7, "m", probeNow));
        Assert.Equal(0, breaker.GetEffectiveWeight(7, "m", probeNow));
        Assert.False(breaker.TryBeginProbe(7, "m", probeNow));

        breaker.EndProbe(7, "m");
        breaker.RecordSuccess(7, "m");

        Assert.Equal(ModelCircuitBreakerState.Closed, breaker.GetStats(7, "m").State);
        Assert.Equal(1.0, breaker.GetEffectiveWeight(7, "m", probeNow));
    }

    [Fact]
    public void FailureTtlAutoRecovers()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var breaker = new ModelCircuitBreaker(time);
        for (var i = 0; i < ModelCircuitBreaker.OpenThreshold; i++)
        {
            breaker.RecordError(9, "m");
        }

        Assert.Equal(0, breaker.GetEffectiveWeight(9, "m", time.GetUtcNow()));

        time.Advance(ModelCircuitBreaker.FailureStatsTtl + TimeSpan.FromMinutes(1));

        Assert.Equal(1.0, breaker.GetEffectiveWeight(9, "m", time.GetUtcNow()));
        Assert.Equal(ModelCircuitBreakerState.Closed, breaker.GetStats(9, "m").State);
    }

    [Fact]
    public void ProbeFailureAppliesCappedExponentialBackoff()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var breaker = new ModelCircuitBreaker(time);
        for (var i = 0; i < ModelCircuitBreaker.OpenThreshold; i++)
        {
            breaker.RecordError(11, "m");
        }

        time.Advance(ModelCircuitBreaker.ProbeInterval + TimeSpan.FromSeconds(1));
        breaker.RecordError(11, "m", wasProbe: true);

        var stats = breaker.GetStats(11, "m");
        Assert.Equal(1, stats.ProbeAttempts);
        Assert.Equal(
            time.GetUtcNow() + ModelCircuitBreaker.ProbeInterval,
            stats.NextProbeAt);
    }
}
