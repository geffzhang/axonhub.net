using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Resolves the auto-backup cron's next occurrence: <c>0 2 * * *</c> in the
/// system time zone (<c>biz.SystemService.TimeLocation</c>, autobackup.go:22-32).
/// The host owns the zone lookup; the grain only sees instants.
/// </summary>
public interface IAutoBackupScheduleSource
{
    /// <summary>
    /// The first 02:00 in the system time zone strictly after
    /// <paramref name="after"/>, applying the reference's DST rules
    /// (AutoBackupSchedule.NextOccurrence).
    /// </summary>
    Task<DateTimeOffset> GetNextOccurrenceAsync(
        DateTimeOffset after,
        CancellationToken cancellationToken = default);
}

/// <summary>Runs <c>BackupService.triggerAutoBackup</c> (autobackup.go:42-85).</summary>
public interface IAutoBackupRunner
{
    Task TriggerAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The reference's <c>"backup"</c> cron entry as an actor
/// (BackupService.RegisterScheduledTasks, service.go:39-55,
/// autobackup.go:22-32): <c>0 2 * * *</c> in the system time zone. The
/// reference keeps one pending occurrence per scheduler process; the grain
/// keeps one Orleans reminder per pending occurrence and re-arms it before
/// every run.
/// </summary>
public interface IAutoBackupGrain : IGrainWithStringKey
{
    /// <summary>The cluster-wide singleton key.</summary>
    public const string SingletonKey = "auto-backup";

    /// <summary>The reminder that carries the pending occurrence.</summary>
    public const string ReminderName = "backup";

    /// <summary>
    /// Registration: arms the pending occurrence when none is pending and
    /// returns the scheduled instant. Like the reference's <c>Register</c>,
    /// it never runs the task.
    /// </summary>
    Task<DateTimeOffset> EnsureScheduledAsync(DateTimeOffset now);

    /// <summary>
    /// Port of <c>BackupService.Reschedule</c> (autobackup.go:22-32): drops
    /// the pending occurrence and arms the first 02:00 after
    /// <paramref name="now"/> in the current system time zone.
    /// </summary>
    Task<DateTimeOffset> RescheduleAsync(DateTimeOffset now);

    /// <summary>
    /// The reminder body: arms the following occurrence first (so a failed
    /// run never loses the schedule), then triggers the backup. A stale tick
    /// for an occurrence this activation already consumed does not run again.
    /// </summary>
    Task<DateTimeOffset> FireScheduledAsync(DateTimeOffset now);
}
