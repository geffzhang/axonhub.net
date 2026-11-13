using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.Logging;

namespace AxonHub.Grains;

/// <summary>
/// The reference's <c>"channel-disabled-api-key-cleanup"</c> cron entry
/// (channel.go:204-223) as an actor: one pending Orleans reminder per UTC
/// minute boundary, i.e. the equivalent of <c>* * * * *</c> in UTC.
/// </summary>
/// <remarks>
/// <para>
/// The reminder is armed by <see cref="EnsureScheduledAsync"/> (the host's
/// startup activation) and re-armed by the fire path before the cleanup runs,
/// so a failed pass or a failed re-arm can never silently drop the schedule.
/// </para>
/// <para>
/// Orleans reminders have no one-shot period; <see cref="ReminderPeriod"/> is
/// the smallest period the runtime accepts, and the fire path overwrites the
/// reminder with the next minute boundary before running. A missed occurrence
/// is not backfilled, matching the reference's executor.
/// </para>
/// </remarks>
public sealed class ChannelCredentialRecoveryGrain(
    IChannelCredentialRecoveryRunner runner,
    TimeProvider time,
    ILogger<ChannelCredentialRecoveryGrain> logger) : Grain, IChannelCredentialRecoveryGrain, IRemindable
{
    /// <summary>The smallest period Orleans accepts (ReminderOptions default).</summary>
    private static readonly TimeSpan ReminderPeriod = TimeSpan.FromMinutes(1);

    private static readonly Action<ILogger, Exception?> LogRunFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ChannelCredentialRecoveryScheduledRunFailed"),
            "Scheduled channel credential recovery failed");

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

        // Arm the following minute first: if the cleanup fails, a stale tick
        // from the previous registration is ignored by the pending check.
        var next = await ScheduleNextAsync(now).ConfigureAwait(true);

        if (pending is { } scheduled && now < scheduled)
        {
            // A stale delivery for a minute this activation already consumed
            // (the reminder table has not applied the re-arm yet).
            return next;
        }

        try
        {
            await runner.RunRecoveryAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The cleanup swallows per-channel failures; this only guards the
            // runner's scope construction.
            LogRunFailed(logger, ex);
        }

        return next;
    }

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(
                reminderName,
                IChannelCredentialRecoveryGrain.ReminderName,
                StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return FireScheduledAsync(time.GetUtcNow());
    }

    private async Task<DateTimeOffset> ScheduleNextAsync(DateTimeOffset now)
    {
        var next = NextMinuteBoundary(now);

        await this.RegisterOrUpdateReminder(
                IChannelCredentialRecoveryGrain.ReminderName,
                next - now,
                ReminderPeriod)
            .ConfigureAwait(true);

        _next = next;
        return next;
    }

    /// <summary>
    /// The first UTC minute boundary strictly after <paramref name="now"/>,
    /// i.e. the next occurrence of <c>* * * * *</c> in UTC.
    /// </summary>
    internal static DateTimeOffset NextMinuteBoundary(DateTimeOffset now)
    {
        var ticks = now.UtcDateTime.Ticks;
        var remainder = ticks % TimeSpan.TicksPerMinute;
        if (remainder < 0)
        {
            remainder += TimeSpan.TicksPerMinute;
        }

        var boundary = new DateTimeOffset(ticks - remainder, TimeSpan.Zero);
        return boundary.AddMinutes(1);
    }
}
