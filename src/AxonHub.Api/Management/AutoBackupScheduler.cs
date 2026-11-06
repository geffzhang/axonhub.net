using AxonHub.Api.Settings;
using AxonHub.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of the backup cron registration (autobackup.go:22-32,
/// internal/server/scheduler). The reference builds a cron entry
/// <c>0 2 * * *</c> in the system time zone and re-creates it through
/// <see cref="BackupService.Reschedule"/> whenever the settings change; the
/// port runs a polling loop that recomputes the next 02:00 from the current
/// system time zone, which makes Reschedule implicit. A run missed while the
/// process was down is not backfilled, matching robfig/cron.
/// </summary>
public sealed class AutoBackupScheduler : BackgroundService
{
    /// <summary>
    /// Ceiling for one wait. The reference re-creates the cron entry on a
    /// settings/timezone change; polling the zone at most once a minute keeps
    /// that responsiveness without a cross-service signal.
    /// </summary>
    private static readonly TimeSpan MaxPoll = TimeSpan.FromMinutes(1);

    private static readonly Action<ILogger, Exception?> LogTriggerFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "AutoBackupTriggerFailed"),
            "Failed to trigger automatic backup");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<AutoBackupScheduler> _logger;

    public AutoBackupScheduler(
        IServiceScopeFactory scopeFactory,
        TimeProvider time,
        ILogger<AutoBackupScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var (zone, zoneName) = await ResolveZoneAsync(stoppingToken).ConfigureAwait(false);
        var next = AutoBackupSchedule.NextOccurrence(_time.GetUtcNow(), zone);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _time.GetUtcNow();

            var (currentZone, currentName) = await ResolveZoneAsync(stoppingToken)
                .ConfigureAwait(false);
            if (!string.Equals(currentName, zoneName, StringComparison.Ordinal))
            {
                // Reschedule: recompute from now in the new zone.
                zone = currentZone;
                zoneName = currentName;
                next = AutoBackupSchedule.NextOccurrence(now, zone);
            }

            if (now >= next)
            {
                await TriggerAsync(stoppingToken).ConfigureAwait(false);
                next = AutoBackupSchedule.NextOccurrence(_time.GetUtcNow(), zone);
                continue;
            }

            var delay = next - now;
            if (delay > MaxPoll)
            {
                delay = MaxPoll;
            }

            try
            {
                await Task.Delay(delay, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TriggerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var backup = scope.ServiceProvider.GetRequiredService<BackupService>();
            await backup.TriggerAutoBackupAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            // triggerAutoBackup never propagates; this only guards the scope.
            LogTriggerFailed(_logger, ex);
        }
    }

    private async Task<(TimeZoneInfo Zone, string Name)> ResolveZoneAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var systems = scope.ServiceProvider.GetRequiredService<ISystemStore>();
            var location = await SystemTimeLocations
                .ResolveAsync(systems, cancellationToken)
                .ConfigureAwait(false);
            return (location.Zone, location.Name);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // SystemTimeLocations falls back to UTC on read failures already;
            // this guards a missing registration only.
            return (TimeZoneInfo.Utc, "UTC");
        }
    }
}
