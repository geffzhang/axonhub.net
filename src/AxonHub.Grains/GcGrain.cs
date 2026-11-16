using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.Logging;

namespace AxonHub.Grains;

/// <summary>
/// The reference's <c>"gc"</c> cron entry as an actor
/// (gc.Worker.RegisterScheduledTasks, gc.go:77-84): one pending Orleans
/// reminder per occurrence of the configured <c>gc.cron</c> in UTC.
/// </summary>
/// <remarks>
/// <para>
/// The reminder is armed by <see cref="EnsureScheduledAsync"/> (the host's
/// startup activation) and re-armed by the fire path before the cleanup runs,
/// so a failed run or a failed re-arm can never silently drop the schedule:
/// the reference's executor keeps the cron entry across task panics.
/// </para>
/// <para>
/// Orleans reminders have no one-shot period; <see cref="ReminderPeriod"/> is
/// the minimum the runtime accepts, and the fire path overwrites the reminder
/// with the next occurrence before running. A missed occurrence is not
/// backfilled, matching the reference's executor.
/// </para>
/// </remarks>
public sealed class GcGrain(
    IGcScheduleSource schedule,
    IGcCleanupRunner cleanup,
    TimeProvider time,
    ILogger<GcGrain> logger) : Grain, IGcGrain, IRemindable
{
    /// <summary>The smallest period Orleans accepts (ReminderOptions default).</summary>
    private static readonly TimeSpan ReminderPeriod = TimeSpan.FromMinutes(1);

    private static readonly Action<ILogger, Exception?> LogRunFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "GcScheduledRunFailed"),
            "Scheduled GC cleanup failed");

    private DateTimeOffset? _next;

    public async Task<DateTimeOffset> EnsureScheduledAsync(DateTimeOffset now)
    {
        if (_next is { } pending && pending > now)
        {
            return pending;
        }

        return await ScheduleNextAsync(now).ConfigureAwait(true);
    }

    public async Task<DateTimeOffset> FireScheduledAsync(DateTimeOffset now)
    {
        var pending = _next;

        // Arm the following occurrence first: if the cleanup fails, yesterday's
        // registration can still deliver a stale tick, which the pending check
        // below ignores.
        var next = await ScheduleNextAsync(now).ConfigureAwait(true);

        if (pending is { } scheduled && now < scheduled)
        {
            // A stale delivery for an occurrence this activation already
            // consumed (the reminder table has not applied the re-arm yet).
            return next;
        }

        try
        {
            await cleanup.RunAutomaticCleanupAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The cleanup swallows per-resource failures; this only guards the
            // runner's scope construction.
            LogRunFailed(logger, ex);
        }

        return next;
    }

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, IGcGrain.ReminderName, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return FireScheduledAsync(time.GetUtcNow());
    }

    private async Task<DateTimeOffset> ScheduleNextAsync(DateTimeOffset now)
    {
        var next = schedule.NextOccurrence(now);

        var due = next - now;
        if (due <= TimeSpan.Zero)
        {
            // NextOccurrence is strictly after now; keep Orleans' due-time
            // validation happy if a source ever violates it.
            due = TimeSpan.FromMilliseconds(1);
        }

        await this.RegisterOrUpdateReminder(
                IGcGrain.ReminderName,
                due,
                ReminderPeriod)
            .ConfigureAwait(true);

        _next = next;
        return next;
    }
}
