using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of gc.Worker.RegisterScheduledTasks (gc.go:77-84): the reference
/// registers <c>{Name:"gc", CronExpr:gc.cron, Timezone:"UTC"}</c> with the
/// shared scheduler, which fires the next matching instant and never
/// backfills an occurrence missed while the process was down. The port runs a
/// dedicated hosted service over the parsed <see cref="GcCronSchedule"/>.
/// </summary>
/// <remarks>
/// The reference's scheduler records a task panic into <c>lastError</c> and
/// logs it; the port has no scheduler admin surface, so it logs and moves on.
/// The cleanup itself already swallows per-resource failures, so only a
/// cancellation or an unexpected bug reaches this loop.
/// </remarks>
public sealed class GcScheduler : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogRunFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "GcScheduledRunFailed"),
            "Scheduled GC cleanup failed");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GcCronSchedule _schedule;
    private readonly TimeProvider _time;
    private readonly ILogger<GcScheduler> _logger;

    public GcScheduler(
        IServiceScopeFactory scopeFactory,
        GcCronSchedule schedule,
        TimeProvider time,
        ILogger<GcScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _schedule = schedule;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The reference's executor schedules the next cron occurrence; it does
        // not run the task at registration time.
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
            var cleanup = scope.ServiceProvider.GetRequiredService<IGcCleanupService>();
            await cleanup.RunAutomaticCleanupAsync(cancellationToken).ConfigureAwait(false);
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
