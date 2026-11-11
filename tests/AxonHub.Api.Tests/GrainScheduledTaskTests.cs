using AxonHub.Api.Management;
using AxonHub.Api.Settings;
using AxonHub.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the host-side halves of the reminder-driven management tasks (§149):
/// the cron sources feed the grains the same instants the removed polling
/// schedulers computed (auto-backup in the system time zone, GC in UTC).
/// </summary>
public sealed class GrainScheduledTaskTests
{
    [Fact]
    public void TheGcSourceReturnsTheNextCronOccurrence()
    {
        var source = new GrainGcScheduleSource(new GcCronSchedule("0 2 * * *"));

        var next = source.NextOccurrence(
            new DateTimeOffset(2024, 1, 1, 3, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2024, 1, 2, 2, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public async Task TheBackupSourceResolvesTheConfiguredTimeZone()
    {
        var systems = new FakeSystemStore();
        systems.Add(
            SystemTimeLocations.GeneralSettingsKey,
            """{"timezone":"Asia/Shanghai"}""");
        var source = new GrainAutoBackupScheduleSource(ScopeFactory(systems));

        // 2024-01-01 00:00Z is 08:00 in Shanghai; the next 02:00 local is
        // 2024-01-02 02:00+08, i.e. 2024-01-01 18:00Z.
        var next = await source.GetNextOccurrenceAsync(
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2024, 1, 1, 18, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public async Task TheBackupSourceFallsBackToUtcOnReadFailure()
    {
        var systems = new FakeSystemStore
        {
            GetFailure = new InvalidOperationException("database down"),
        };
        var source = new GrainAutoBackupScheduleSource(ScopeFactory(systems));

        var next = await source.GetNextOccurrenceAsync(
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2024, 1, 1, 2, 0, 0, TimeSpan.Zero), next);
    }

    [Fact]
    public async Task TheVideoStorageSourceReadsTheStoredInterval()
    {
        var systems = VideoSettings("""{"enabled":true,"data_storage_id":7,"scan_interval_minutes":5,"scan_limit":50}""");
        var source = new GrainVideoStorageScheduleSource(VideoScopeFactory(systems));

        Assert.Equal(TimeSpan.FromMinutes(5), await source.GetIntervalAsync());
    }

    [Fact]
    public async Task TheVideoStorageSourceFloorsANonPositiveInterval()
    {
        var systems = VideoSettings("""{"enabled":true,"data_storage_id":7,"scan_interval_minutes":0,"scan_limit":50}""");
        var source = new GrainVideoStorageScheduleSource(VideoScopeFactory(systems));

        Assert.Equal(TimeSpan.FromMinutes(1), await source.GetIntervalAsync());
    }

    [Fact]
    public async Task TheVideoStorageSourceDefaultsWhenTheRowIsMissing()
    {
        var source = new GrainVideoStorageScheduleSource(VideoScopeFactory(new FakeSystemStore()));

        Assert.Equal(TimeSpan.FromMinutes(1), await source.GetIntervalAsync());
    }

    private static FakeSystemStore VideoSettings(string json)
    {
        var systems = new FakeSystemStore();
        systems.Add(SystemSettingsAdminService.VideoStorageSettingsKey, json);
        return systems;
    }

    private static IServiceScopeFactory VideoScopeFactory(ISystemStore systems)
    {
        var services = new ServiceCollection();
        services.AddSingleton(systems);
        services.AddSingleton<ISystemSettingsAdminService>(
            new SystemSettingsAdminService(systems));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static IServiceScopeFactory ScopeFactory(ISystemStore systems)
    {
        var services = new ServiceCollection();
        services.AddSingleton(systems);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }
}

/// <summary>Records BackupService.Reschedule signals without a cluster.</summary>
internal sealed class FakeAutoBackupRescheduleSignal : IAutoBackupRescheduleSignal
{
    public int Calls { get; private set; }

    public Task RescheduleAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Calls++;
        return Task.CompletedTask;
    }
}
