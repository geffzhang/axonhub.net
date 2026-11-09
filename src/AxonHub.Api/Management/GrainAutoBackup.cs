using AxonHub.Api.Settings;
using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Management;

/// <summary>
/// Resolves the backup cron's next occurrence from the system time zone
/// (<c>biz.SystemService.TimeLocation</c>). The settings row is re-read on
/// every arm, like the ported polling scheduler did (C186); a read failure or
/// an unknown zone falls back to UTC.
/// </summary>
public sealed class GrainAutoBackupScheduleSource(IServiceScopeFactory scopeFactory)
    : IAutoBackupScheduleSource
{
    public async Task<DateTimeOffset> GetNextOccurrenceAsync(
        DateTimeOffset after,
        CancellationToken cancellationToken = default)
    {
        TimeZoneInfo zone;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var systems = scope.ServiceProvider.GetRequiredService<ISystemStore>();
            var location = await SystemTimeLocations
                .ResolveAsync(systems, cancellationToken)
                .ConfigureAwait(false);
            zone = location.Zone;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // SystemTimeLocations falls back to UTC on read failures already;
            // this guards a missing registration only.
            zone = TimeZoneInfo.Utc;
        }

        return AutoBackupSchedule.NextOccurrence(after, zone);
    }
}

/// <summary>
/// Runs <c>BackupService.triggerAutoBackup</c> (autobackup.go:42-85) in a
/// fresh scope: the grain turn has no request scope of its own.
/// </summary>
public sealed class GrainAutoBackupRunner(IServiceScopeFactory scopeFactory)
    : IAutoBackupRunner
{
    public async Task TriggerAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var backup = scope.ServiceProvider.GetRequiredService<BackupService>();
        await backup.TriggerAutoBackupAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Production <see cref="IAutoBackupRescheduleSignal"/>: re-arms the singleton
/// backup grain after the settings change (BackupService.Reschedule,
/// autobackup.go:22-32). Like the reference's <c>Reschedule</c>, a scheduling
/// failure is logged and does not fail the mutation.
/// </summary>
public sealed class GrainAutoBackupRescheduleSignal(
    IGrainFactory grainFactory,
    TimeProvider time,
    ILogger<GrainAutoBackupRescheduleSignal> logger) : IAutoBackupRescheduleSignal
{
    private static readonly Action<ILogger, Exception?> LogRescheduleFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "AutoBackupRescheduleFailed"),
            "Failed to reschedule the automatic backup");

    public async Task RescheduleAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await grainFactory
                .GetGrain<IAutoBackupGrain>(IAutoBackupGrain.SingletonKey)
                .RescheduleAsync(time.GetUtcNow())
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRescheduleFailed(logger, ex);
        }
    }
}
