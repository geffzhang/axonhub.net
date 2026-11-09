using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.Logging;

namespace AxonHub.Grains;

/// <summary>
/// The reference's <c>"backup"</c> cron entry as an actor
/// (BackupService.RegisterScheduledTasks, service.go:39-55, autobackup.go:22-32):
/// one pending Orleans reminder per daily 02:00 occurrence in the system time
/// zone.
/// </summary>
/// <remarks>
/// <para>
/// The reminder is armed by <see cref="EnsureScheduledAsync"/> (the host's
/// startup activation) and re-armed by the fire path before the backup runs,
/// so a failed run or a failed re-arm can never silently drop the schedule:
/// the reference's cron continues after a task failure in the same way.
/// </para>
/// <para>
/// Orleans reminders have no one-shot period; <see cref="ReminderPeriod"/> is
/// the minimum the runtime accepts, and the fire path overwrites the reminder
/// with the next occurrence before running. The in-memory reminder table loses
/// pending occurrences when every silo stops; the startup activation then arms
/// from the current instant, which is the reference's process-restart
/// behaviour (a missed 02:00 is never backfilled).
/// </para>
/// </remarks>
public sealed class AutoBackupGrain(
    IAutoBackupScheduleSource schedule,
    IAutoBackupRunner runner,
    TimeProvider time,
    ILogger<AutoBackupGrain> logger) : Grain, IAutoBackupGrain, IRemindable
{
    /// <summary>The smallest period Orleans accepts (ReminderOptions default).</summary>
    private static readonly TimeSpan ReminderPeriod = TimeSpan.FromMinutes(1);

    private static readonly Action<ILogger, Exception?> LogRunFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "AutoBackupTriggerFailed"),
            "Failed to trigger automatic backup");

    private DateTimeOffset? _next;

    public async Task<DateTimeOffset> EnsureScheduledAsync(DateTimeOffset now)
    {
        if (_next is { } pending && pending > now)
        {
            return pending;
        }

        return await ScheduleNextAsync(now).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset> RescheduleAsync(DateTimeOffset now)
    {
        _next = null;
        return await ScheduleNextAsync(now).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset> FireScheduledAsync(DateTimeOffset now)
    {
        var pending = _next;

        // Arm the following occurrence first: if the backup fails, yesterday's
        // registration can still deliver a stale tick, which the pending check
        // below ignores.
        var next = await ScheduleNextAsync(now).ConfigureAwait(false);

        if (pending is { } scheduled && now < scheduled)
        {
            // A stale delivery for an occurrence this activation already
            // consumed (the reminder table has not applied the re-arm yet).
            return next;
        }

        try
        {
            await runner.TriggerAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // triggerAutoBackup never propagates; this only guards the runner's
            // scope construction.
            LogRunFailed(logger, ex);
        }

        return next;
    }

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, IAutoBackupGrain.ReminderName, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return FireScheduledAsync(time.GetUtcNow());
    }

    private async Task<DateTimeOffset> ScheduleNextAsync(DateTimeOffset now)
    {
        var next = await schedule.GetNextOccurrenceAsync(now, CancellationToken.None)
            .ConfigureAwait(false);

        var due = next - now;
        if (due <= TimeSpan.Zero)
        {
            // NextOccurrence is strictly after now; keep Orleans' due-time
            // validation happy if a source ever violates it.
            due = TimeSpan.FromMilliseconds(1);
        }

        await this.RegisterOrUpdateReminder(
                IAutoBackupGrain.ReminderName,
                due,
                ReminderPeriod)
            .ConfigureAwait(false);

        _next = next;
        return next;
    }
}
