using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the production model-sync guard (§148): the reference's
/// shouldRunModelSync window lives in the singleton grain, the auto-sync
/// frequency becomes the interval, and every bridge instance observes the same
/// cluster-wide decision.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class GrainChannelModelSyncGuardTests(GrainClusterFixture fixture)
{
    private GrainChannelModelSyncGuard Guard()
        => new(fixture.Cluster.GrainFactory, NullLogger<GrainChannelModelSyncGuard>.Instance);

    [Fact]
    public async Task TheGuardKeepsTheSingletonWindowAcrossBridges()
    {
        var guard = Guard();
        var now = new DateTimeOffset(2024, 1, 1, 10, 2, 0, TimeSpan.Zero);

        // OneHour maps to a sixty-minute window.
        Assert.True(await guard.TryBeginRunAsync(now, AutoSyncFrequency.OneHour));
        Assert.False(await guard.TryBeginRunAsync(now.AddMinutes(2), AutoSyncFrequency.OneHour));

        // A second bridge reads the same cluster state.
        Assert.False(await Guard().TryBeginRunAsync(now.AddMinutes(5), AutoSyncFrequency.OneHour));

        // The decision is stored in the production singleton grain.
        var grain = fixture.Cluster.GrainFactory.GetGrain<IChannelModelSyncGrain>(
            IChannelModelSyncGrain.SingletonKey);
        Assert.True(await grain.TryBeginRunAsync(now.AddHours(1), 60));
        Assert.False(await guard.TryBeginRunAsync(now.AddHours(1).AddMinutes(30), AutoSyncFrequency.OneHour));

        // SixHours maps to a 360-minute window aligned on 06:00/12:00/18:00/00:00.
        Assert.True(await guard.TryBeginRunAsync(
            new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero), AutoSyncFrequency.SixHours));
        Assert.False(await guard.TryBeginRunAsync(
            new DateTimeOffset(2024, 1, 1, 12, 30, 0, TimeSpan.Zero), AutoSyncFrequency.SixHours));

        // OneDay maps to a 1440-minute window aligned on midnight.
        Assert.True(await guard.TryBeginRunAsync(
            new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero), AutoSyncFrequency.OneDay));
        Assert.False(await guard.TryBeginRunAsync(
            new DateTimeOffset(2024, 1, 2, 23, 59, 0, TimeSpan.Zero), AutoSyncFrequency.OneDay));
    }
}
