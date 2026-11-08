using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the seam between the synchronous pipeline middlewares and the channel
/// grain's rate-limit authority: the production recorder writes through to the
/// grain, while the in-memory recorder keeps the tracker's own clock
/// authoritative for direct test constructions.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class ChannelRateLimitRecorderTests(GrainClusterFixture fixture)
{
    private static readonly DateTimeOffset Start = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GrainRecorderWritesThroughToTheChannelGrain()
    {
        var recorder = new GrainChannelRateLimitRecorder(fixture.Cluster.GrainFactory);

        Assert.True(recorder.TryAcquireRequest(5201, 2, Start));
        Assert.True(recorder.TryAcquireRequest(5201, 2, Start));
        Assert.False(recorder.TryAcquireRequest(5201, 2, Start));

        recorder.AddTokens(5201, 300, Start);
        recorder.SetCooldown(5201, Start.AddSeconds(30));

        var metrics = await fixture.Cluster.GrainFactory
            .GetGrain<IChannelGrain>(5201)
            .GetMetricsAsync(Start);

        Assert.Equal(2, metrics.RateLimitRequests);
        Assert.Equal(300, metrics.RateLimitTokens);
        Assert.Equal<DateTimeOffset?>(Start.AddSeconds(30), metrics.CooldownUntil);
    }

    [Fact]
    public void InMemoryRecorderUsesTheTrackerClock()
    {
        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);
        var recorder = new InMemoryChannelRateLimitRecorder(tracker);

        // The supplied instant is deliberately in another minute; the adapter
        // must still bill the tracker's current bucket.
        Assert.True(recorder.TryAcquireRequest(1, 1, Start.AddMinutes(5)));
        recorder.AddTokens(1, 50, Start.AddMinutes(5));

        Assert.Equal(1, tracker.GetRequestCount(1));
        Assert.Equal(50, tracker.GetTokenCount(1));
    }
}
