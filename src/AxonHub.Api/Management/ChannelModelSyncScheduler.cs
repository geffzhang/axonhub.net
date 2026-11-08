using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of the channel-model-sync cron registration
/// (ChannelService.RegisterScheduledTasks, channel.go:204-212): the reference
/// registers <c>{Name:"channel-model-sync", CronExpr:"11 * * * *",
/// Timezone:"UTC"}</c> with the shared scheduler. The task fires at minute 11
/// of every UTC hour and lets <c>shouldRunModelSync</c> collapse the ticks to
/// the configured auto-sync frequency (one hour, six hours or one day).
/// </summary>
/// <remarks>
/// Like the other ported schedulers (<see cref="ProviderQuotaScheduler"/>,
/// <see cref="GcScheduler"/>), a missed occurrence is not backfilled: the next
/// fire time is recomputed from the current instant. The execution window
/// itself lives in the cluster-wide grain (§148), so two silos ticking in the
/// same second still start at most one pass.
/// </remarks>
public sealed class ChannelModelSyncScheduler : BackgroundService
{
    /// <summary>
    /// <c>scheduler.TaskSpec{Name:"channel-model-sync"}</c>'s expression.
    /// </summary>
    public const string CronExpression = "11 * * * *";

    private static readonly Action<ILogger, Exception?> LogRunFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ChannelModelSyncScheduledRunFailed"),
            "Scheduled channel model sync failed");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GcCronSchedule _schedule;
    private readonly TimeProvider _time;
    private readonly ILogger<ChannelModelSyncScheduler> _logger;

    public ChannelModelSyncScheduler(
        IServiceScopeFactory scopeFactory,
        TimeProvider time,
        ILogger<ChannelModelSyncScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _time = time;
        _logger = logger;
        _schedule = new GcCronSchedule(CronExpression);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The scheduler does not run the task at registration time.
        var next = _schedule.NextOccurrence(_time.GetUtcNow());

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _time.GetUtcNow();
            if (now >= next)
            {
                await RunAsync(stoppingToken).ConfigureAwait(false);
                next = _schedule.NextOccurrence(_time.GetUtcNow());
                continue;
            }

            try
            {
                await Task.Delay(next - now, _time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var sync = scope.ServiceProvider.GetRequiredService<ChannelModelSyncService>();
            await sync.RunSyncChannelModelsPeriodicallyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            LogRunFailed(_logger, ex);
        }
    }
}
