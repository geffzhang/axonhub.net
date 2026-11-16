using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.Logging;

namespace AxonHub.Grains;

/// <summary>
/// The reference's <c>"video-storage"</c> fixed-rate scan entry
/// (internal/server/video_storage/worker.go:54-95) as an Orleans reminder.
/// </summary>
/// <remarks>
/// <para>
/// The reference schedules <c>FixRate = scanIntervalMinutes</c>; the grain
/// keeps one reminder whose due time and period are the current interval, and
/// re-arms it before every run so a failed scan cannot drop the schedule.
/// </para>
/// <para>
/// Orleans reminders have no one-shot period; the minimum period is one minute,
/// which is also the reference's floor for a non-positive stored interval.
/// </para>
/// </remarks>
public sealed class VideoStorageGrain(
    IVideoStorageScheduleSource schedule,
    IVideoStorageScanRunner runner,
    TimeProvider time,
    ILogger<VideoStorageGrain> logger) : Grain, IVideoStorageGrain, IRemindable
{
    /// <summary>The smallest period Orleans accepts (ReminderOptions default).</summary>
    private static readonly TimeSpan ReminderPeriod = TimeSpan.FromMinutes(1);

    private static readonly Action<ILogger, Exception?> LogRunFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "VideoStorageScanFailed"),
            "Video storage worker failed");

    private DateTimeOffset? _next;

    public async Task<DateTimeOffset> EnsureScheduledAsync(DateTimeOffset now)
    {
        if (_next is { } pending && pending > now)
        {
            return pending;
        }

        return await ScheduleNextAsync(now).ConfigureAwait(true);
    }

    public async Task<DateTimeOffset> RescheduleAsync(DateTimeOffset now)
    {
        _next = null;
        return await ScheduleNextAsync(now).ConfigureAwait(true);
    }

    public async Task<DateTimeOffset> FireScheduledAsync(DateTimeOffset now)
    {
        var pending = _next;

        // Arm the following scan first: a failed pass or a failed re-arm must
        // not lose the schedule.
        var next = await ScheduleNextAsync(now).ConfigureAwait(true);

        if (pending is { } scheduled && now < scheduled)
        {
            // A stale delivery for an occurrence this activation already
            // consumed (the reminder table has not applied the re-arm yet).
            return next;
        }

        try
        {
            await runner.ScanAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // runScanWithSystemContext logs and returns; the schedule continues.
            LogRunFailed(logger, ex);
        }

        return next;
    }

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, IVideoStorageGrain.ReminderName, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return FireScheduledAsync(time.GetUtcNow());
    }

    private async Task<DateTimeOffset> ScheduleNextAsync(DateTimeOffset now)
    {
        var interval = await schedule.GetIntervalAsync(CancellationToken.None)
            .ConfigureAwait(true);
        if (interval < ReminderPeriod)
        {
            // The reference worker floors a non-positive interval to one minute
            // and Orleans rejects a sub-minute reminder period.
            interval = ReminderPeriod;
        }

        var next = now + interval;
        await this.RegisterOrUpdateReminder(
                IVideoStorageGrain.ReminderName,
                interval,
                interval)
            .ConfigureAwait(true);

        _next = next;
        return next;
    }
}
