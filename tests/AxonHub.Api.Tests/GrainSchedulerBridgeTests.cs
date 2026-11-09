using AxonHub.Api.Management;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the production bridges of §149 against the in-process cluster: the
/// startup activator arms both singleton grains, and the settings-change signal
/// re-arms the backup grain. One fact keeps the shared singleton activations in
/// a deterministic order.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class GrainSchedulerBridgeTests(GrainClusterFixture fixture)
{
    [Fact]
    public async Task TheActivatorArmsBothGrainsAndTheSettingsSignalRearmsTheBackupGrain()
    {
        var scheduled = DateTimeOffset.UtcNow.AddHours(12);
        fixture.AutoBackupSchedule.Reset(scheduled);
        fixture.GcSchedule.Reset(scheduled);

        var activator = new GrainSchedulerActivator(
            fixture.Cluster.Client,
            TimeProvider.System,
            NullLogger<GrainSchedulerActivator>.Instance);

        await activator.StartAsync(CancellationToken.None);

        Assert.Single(fixture.AutoBackupSchedule.Calls);
        Assert.Single(fixture.GcSchedule.Calls);

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
