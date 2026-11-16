using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Locks the model-sync execution guard (§148): shouldRunModelSync decides at
/// most once per aligned auto-sync window, and Orleans serializes the turns so
/// two concurrent ticks cannot both win.
/// </summary>
[Collection(GrainClusterCollection.Name)]
public sealed class ChannelModelSyncGrainTests(GrainClusterFixture fixture)
{
    private IChannelModelSyncGrain Grain(string key)
        => fixture.Cluster.GrainFactory.GetGrain<IChannelModelSyncGrain>(key);

    [Fact]
    public async Task TheFirstCallInAWindowRunsAndTheSameWindowIsSkipped()
    {
        var grain = Grain("model-sync:window");
        var now = new DateTimeOffset(2024, 1, 1, 10, 2, 0, TimeSpan.Zero);

        Assert.True(await grain.TryBeginRunAsync(now, 60));
        Assert.False(await grain.TryBeginRunAsync(now.AddMinutes(2), 60));
    }

    [Fact]
    public async Task TheNextAlignedWindowRuns()
    {
        var grain = Grain("model-sync:next");

        Assert.True(await grain.TryBeginRunAsync(
            new DateTimeOffset(2024, 1, 1, 10, 59, 0, TimeSpan.Zero), 60));
        Assert.True(await grain.TryBeginRunAsync(
            new DateTimeOffset(2024, 1, 1, 11, 0, 0, TimeSpan.Zero), 60));
    }

    [Fact]
    public async Task TheGuardAlignsToTheRequestedSixHourInterval()
    {
        var grain = Grain("model-sync:six-hours");

        Assert.True(await grain.TryBeginRunAsync(
            new DateTimeOffset(2024, 1, 1, 10, 30, 0, TimeSpan.Zero), 360));
        Assert.False(await grain.TryBeginRunAsync(
            new DateTimeOffset(2024, 1, 1, 11, 59, 0, TimeSpan.Zero), 360));
        Assert.True(await grain.TryBeginRunAsync(
            new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero), 360));
    }

    [Fact]
    public async Task TheGuardAlignsToTheRequestedDailyInterval()
    {
        var grain = Grain("model-sync:daily");

        Assert.True(await grain.TryBeginRunAsync(
            new DateTimeOffset(2024, 1, 1, 10, 30, 0, TimeSpan.Zero), 1440));
        Assert.False(await grain.TryBeginRunAsync(
            new DateTimeOffset(2024, 1, 1, 23, 59, 0, TimeSpan.Zero), 1440));
        Assert.True(await grain.TryBeginRunAsync(
            new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero), 1440));
    }

    [Fact]
    public async Task ConcurrentCallsInOneWindowYieldOneWinner()
    {
        var grain = Grain("model-sync:concurrent");
        var now = new DateTimeOffset(2024, 1, 1, 10, 2, 0, TimeSpan.Zero);

        var results = await Task.WhenAll(
            grain.TryBeginRunAsync(now, 60),
            grain.TryBeginRunAsync(now.AddSeconds(1), 60));

        Assert.Equal(1, results.Count(static result => result));
    }
}
