using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Locks the reminder-driven channel-probe minute task (C249,
/// channel_probe.go:70-79): the registration arms the next UTC minute without
/// running the probe, every fire re-arms the following minute before running,
/// a stale tick for an already consumed minute is ignored, and a failed pass
/// never drops the schedule.
/// </summary>
[Collection(GrainClusterCollection.Name)]
public sealed class ChannelProbeGrainTests(GrainClusterFixture fixture)
{
    private static readonly DateTimeOffset Now =
        new(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private IChannelProbeGrain Grain(string key)
        => fixture.Cluster.GrainFactory.GetGrain<IChannelProbeGrain>(key);

    [Fact]
    public async Task RegistrationArmsTheNextMinuteWithoutRunningTheProbe()
    {
        fixture.ChannelProbeRun.Reset();

        var scheduled = await Grain("channel-probe:register").EnsureScheduledAsync(Now);

        Assert.Equal(Now.AddMinutes(1), scheduled);
        Assert.Equal(0, fixture.ChannelProbeRun.Calls);
    }

    [Fact]
    public async Task RegistrationAlignsToTheNextUtcMinuteBoundary()
    {
        fixture.ChannelProbeRun.Reset();

        var scheduled = await Grain("channel-probe:register-align")
            .EnsureScheduledAsync(Now.AddSeconds(31));

        Assert.Equal(Now.AddMinutes(1), scheduled);
        Assert.Equal(0, fixture.ChannelProbeRun.Calls);
    }

    [Fact]
    public async Task EnsureKeepsThePendingMinuteAcrossCalls()
    {
        fixture.ChannelProbeRun.Reset();

        var grain = Grain("channel-probe:ensure-twice");

        Assert.Equal(Now.AddMinutes(1), await grain.EnsureScheduledAsync(Now));
        Assert.Equal(
            Now.AddMinutes(1),
            await grain.EnsureScheduledAsync(Now.AddSeconds(30)));
        Assert.Equal(0, fixture.ChannelProbeRun.Calls);
    }

    [Fact]
    public async Task TheFireArmsTheFollowingMinuteBeforeRunningTheProbe()
    {
        fixture.ChannelProbeRun.Reset();

        var grain = Grain("channel-probe:fire");
        await grain.EnsureScheduledAsync(Now);

        var firstOccurrence = Now.AddMinutes(1);
        var next = await grain.FireScheduledAsync(firstOccurrence);

        Assert.Equal(firstOccurrence.AddMinutes(1), next);
        Assert.Equal(1, fixture.ChannelProbeRun.Calls);
    }

    [Fact]
    public async Task AStaleTickDoesNotRunTheProbe()
    {
        fixture.ChannelProbeRun.Reset();

        var grain = Grain("channel-probe:stale-tick");
        await grain.EnsureScheduledAsync(Now);

        // A delivery that arrives before the pending minute still re-arms from
        // its own instant but must not run the pass.
        var next = await grain.FireScheduledAsync(Now.AddSeconds(30));

        Assert.Equal(Now.AddMinutes(1), next);
        Assert.Equal(0, fixture.ChannelProbeRun.Calls);
    }

    [Fact]
    public async Task AFailedProbeKeepsTheScheduleArmed()
    {
        fixture.ChannelProbeRun.Reset();
        fixture.ChannelProbeRun.Failure = new InvalidOperationException("probe failed");

        var grain = Grain("channel-probe:run-failure");
        await grain.EnsureScheduledAsync(Now);

        var firstOccurrence = Now.AddMinutes(1);
        var next = await grain.FireScheduledAsync(firstOccurrence);

        Assert.Equal(firstOccurrence.AddMinutes(1), next);
        Assert.Equal(1, fixture.ChannelProbeRun.Calls);
    }
}

public sealed class FakeChannelProbeRunRunner : IChannelProbeRunRunner
{
    public int Calls { get; private set; }

    public Exception? Failure { get; set; }

    public void Reset()
    {
        Calls = 0;
        Failure = null;
    }

    public Task RunProbeAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}
