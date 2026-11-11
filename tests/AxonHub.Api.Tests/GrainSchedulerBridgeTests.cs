using AxonHub.Api.Management;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the production bridges of §149 / §150 against the in-process cluster:
/// the startup activator arms the GC, backup and provider quota singleton
/// grains, and the settings-change signal re-arms the backup grain. One fact
/// keeps the shared singleton activations in a deterministic order.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class GrainSchedulerBridgeTests(GrainClusterFixture fixture)
{
    [Fact]
    public async Task TheActivatorArmsEveryGrainAndTheSettingsSignalRearmsTheBackupGrain()
    {
        var scheduled = DateTimeOffset.UtcNow.AddHours(12);
        fixture.AutoBackupSchedule.Reset(scheduled);
        fixture.GcSchedule.Reset(scheduled);
        fixture.QuotaSchedule.Reset(scheduled);
        fixture.VideoStorageSchedule.Reset(TimeSpan.FromMinutes(10));
        fixture.VideoStorageScan.Reset();

        var activator = new GrainSchedulerActivator(
            fixture.Cluster.Client,
            TimeProvider.System,
            NullLogger<GrainSchedulerActivator>.Instance);

        await activator.StartAsync(CancellationToken.None);

        Assert.Single(fixture.AutoBackupSchedule.Calls);
        Assert.Single(fixture.GcSchedule.Calls);
        Assert.Single(fixture.QuotaSchedule.Calls);
        Assert.Equal(1, fixture.VideoStorageSchedule.Calls);
        Assert.Equal(0, fixture.VideoStorageScan.Calls);

        // updateAutoBackupSettings (backup.resolvers.go:114) asks the singleton
        // backup grain to re-arm from the calling instant.
        var signal = new GrainAutoBackupRescheduleSignal(
            fixture.Cluster.Client,
            TimeProvider.System,
            NullLogger<GrainAutoBackupRescheduleSignal>.Instance);

        await signal.RescheduleAsync();

        Assert.Equal(2, fixture.AutoBackupSchedule.Calls.Count);
    }
}
