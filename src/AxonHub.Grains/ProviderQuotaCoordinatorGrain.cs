using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.Logging;

namespace AxonHub.Grains;

/// <summary>
/// The reference's provider quota collection coordination
/// (provider_quota.go:346-409): the <c>provider-quota-check</c> cron as an
/// Orleans reminder, and the <c>svc.mu</c> turn as one singleton actor.
/// </summary>
/// <remarks>
/// <para>
/// The reference registers <c>{Name:"provider-quota-check", CronExpr:
/// intervalToCronExpr(checkInterval), Timezone:"UTC"}</c> once at startup and
/// holds <c>svc.mu</c> across scheduled checks, manual checks, invalidations
/// and post-reset refreshes. Grain turns give the same exclusion: a manual
/// check waits for a running sweep and blocks the next reminder-driven run,
/// and an invalidation cannot interleave with a check that is persisting the
/// same channel.
/// </para>
/// <para>
/// Reminders have no one-shot period; the fire path re-arms the following
/// occurrence before running the sweep, so a failed run never drops the
/// schedule. A missed occurrence is not backfilled, like the reference's
/// scheduler.
/// </para>
/// </remarks>
public sealed class ProviderQuotaCoordinatorGrain(
    IProviderQuotaScheduleSource schedule,
    IProviderQuotaCoordinatorOperations operations,
    TimeProvider time,
    ILogger<ProviderQuotaCoordinatorGrain> logger)
    : Grain, IProviderQuotaCoordinatorGrain, IRemindable
{
    /// <summary>The smallest period Orleans accepts (ReminderOptions default).</summary>
    private static readonly TimeSpan ReminderPeriod = TimeSpan.FromMinutes(1);

    private static readonly Action<ILogger, Exception?> LogRunFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ProviderQuotaScheduledRunFailed"),
            "Scheduled provider quota check failed");

    private static readonly Action<ILogger, Exception?> LogManualRunFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ProviderQuotaManualRunFailed"),
            "Manual provider quota check failed");

    private static readonly Action<ILogger, Exception?> LogRefreshFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ProviderQuotaRefreshFailed"),
            "Provider quota refresh after reset failed");

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

        // Arm the following occurrence first: a failed sweep or a failed
        // re-arm must not lose the schedule.
        var next = await ScheduleNextAsync(now).ConfigureAwait(true);

        if (pending is { } scheduled && now < scheduled)
        {
            // A stale delivery for an occurrence this activation already
            // consumed (the reminder table has not applied the re-arm yet).
            return next;
        }

        await RunGuardedAsync(
                static ops => ops.RunAsync(force: false, CancellationToken.None),
                LogRunFailed)
            .ConfigureAwait(true);

        return next;
    }

    public async Task RunManualAsync()
    {
        // ManualCheck never surfaces a run failure: runQuotaCheck logs and
        // returns (provider_quota.go:620-623, 719-790).
        await RunGuardedAsync(
                static ops => ops.RunAsync(force: true, CancellationToken.None),
                LogManualRunFailed)
            .ConfigureAwait(true);
    }

    public async Task RefreshChannelAsync(ProviderQuotaRefreshRequest request)
    {
        // checkChannelQuota logs its own failures; this only guards the
        // runner's scope construction.
        await RunGuardedAsync(
                ops => ops.RefreshChannelAsync(request, CancellationToken.None),
                LogRefreshFailed)
            .ConfigureAwait(true);
    }

    public Task InvalidateAsync(long channelId)
    {
        // The reference returns the delete error to the channel hook, which
        // logs it (channel_provider_quota_hook.go:22-32); propagate it.
        return operations.InvalidateAsync(channelId, CancellationToken.None);
    }

    public Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (!string.Equals(reminderName, IProviderQuotaCoordinatorGrain.ReminderName, StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return FireScheduledAsync(time.GetUtcNow());
    }

    private async Task RunGuardedAsync(
        Func<IProviderQuotaCoordinatorOperations, Task> operation,
        Action<ILogger, Exception?> logFailure)
    {
        try
        {
            await operation(operations).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            logFailure(logger, ex);
        }
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
                IProviderQuotaCoordinatorGrain.ReminderName,
                due,
                ReminderPeriod)
            .ConfigureAwait(true);

        _next = next;
        return next;
    }
}
