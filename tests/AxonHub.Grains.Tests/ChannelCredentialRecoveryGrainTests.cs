using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Locks the reminder-driven expired-credential / auto-disable recovery minute
/// task (channel.go:204-223, C285): the registration arms the next UTC minute
/// without running the cleanup, every fire re-arms the following minute before
/// running, a stale tick for an already consumed minute is ignored, and a
/// failed pass never drops the schedule.
/// </summary>
[Collection(GrainClusterCollection.Name)]
public sealed class ChannelCredentialRecoveryGrainTests(GrainClusterFixture fixture)
{
    private static readonly DateTimeOffset Now =
        new(2024, 1, 1, 10, 0, 0, TimeSpan.Zero);

    private IChannelCredentialRecoveryGrain Grain(string key)
        => fixture.Cluster.GrainFactory.GetGrain<IChannelCredentialRecoveryGrain>(key);

    [Fact]
    public async Task RegistrationArmsTheNextMinuteWithoutRunningTheCleanup()
    {
        fixture.ChannelCredentialRecovery.Reset();

        var scheduled = await Grain("credential-recovery:register").EnsureScheduledAsync(Now);

        Assert.Equal(Now.AddMinutes(1), scheduled);
        Assert.Equal(0, fixture.ChannelCredentialRecovery.Calls);
    }

    [Fact]
    public async Task RegistrationAlignsToTheNextUtcMinuteBoundary()
    {
        fixture.ChannelCredentialRecovery.Reset();

        var scheduled = await Grain("credential-recovery:register-align")
            .EnsureScheduledAsync(Now.AddSeconds(31));

        Assert.Equal(Now.AddMinutes(1), scheduled);
        Assert.Equal(0, fixture.ChannelCredentialRecovery.Calls);
    }

    [Fact]
    public async Task EnsureKeepsThePendingMinuteAcrossCalls()
    {
        fixture.ChannelCredentialRecovery.Reset();

        var grain = Grain("credential-recovery:ensure-twice");

        Assert.Equal(Now.AddMinutes(1), await grain.EnsureScheduledAsync(Now));
        Assert.Equal(
            Now.AddMinutes(1),
            await grain.EnsureScheduledAsync(Now.AddSeconds(30)));
        Assert.Equal(0, fixture.ChannelCredentialRecovery.Calls);
    }

    [Fact]
    public async Task TheFireArmsTheFollowingMinuteBeforeRunningTheCleanup()
    {
        fixture.ChannelCredentialRecovery.Reset();

        var grain = Grain("credential-recovery:fire");
        await grain.EnsureScheduledAsync(Now);

        var firstOccurrence = Now.AddMinutes(1);
        var next = await grain.FireScheduledAsync(firstOccurrence);

        Assert.Equal(firstOccurrence.AddMinutes(1), next);
        Assert.Equal(1, fixture.ChannelCredentialRecovery.Calls);
    }

    [Fact]
    public async Task AStaleTickDoesNotRunTheCleanup()
    {
        fixture.ChannelCredentialRecovery.Reset();

        var grain = Grain("credential-recovery:stale-tick");
        await grain.EnsureScheduledAsync(Now);

        // A delivery that arrives before the pending minute still re-arms from
        // its own instant but must not run the pass.
        var next = await grain.FireScheduledAsync(Now.AddSeconds(30));

        Assert.Equal(Now.AddMinutes(1), next);
        Assert.Equal(0, fixture.ChannelCredentialRecovery.Calls);
    }

    [Fact]
    public async Task AFailedCleanupKeepsTheScheduleArmed()
    {
        fixture.ChannelCredentialRecovery.Reset();
        fixture.ChannelCredentialRecovery.Failure =
            new InvalidOperationException("cleanup failed");

        var grain = Grain("credential-recovery:run-failure");
        await grain.EnsureScheduledAsync(Now);

        var firstOccurrence = Now.AddMinutes(1);
        var next = await grain.FireScheduledAsync(firstOccurrence);

        Assert.Equal(firstOccurrence.AddMinutes(1), next);
        Assert.Equal(1, fixture.ChannelCredentialRecovery.Calls);
    }
}

public sealed class FakeChannelCredentialRecoveryRunner : IChannelCredentialRecoveryRunner
{
    public int Calls { get; private set; }

    public Exception? Failure { get; set; }

    public void Reset()
    {
        Calls = 0;
        Failure = null;
    }

    public Task RunRecoveryAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
    }
}
