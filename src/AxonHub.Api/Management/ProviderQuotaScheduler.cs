using AxonHub.Api.Orchestration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of <c>ProviderQuotaService.RegisterScheduledTasks</c>
/// (provider_quota.go:396-404): the reference registers
/// <c>{Name:"provider-quota-check", CronExpr:intervalToCronExpr(checkInterval),
/// Timezone:"UTC"}</c> with the shared scheduler. With the service's fixed
/// five-minute interval the expression is <c>*/5 * * * *</c>, evaluated in UTC,
/// firing the next occurrence and never backfilling a missed one.
/// </summary>
/// <remarks>
/// <see cref="GcCronSchedule"/> is reused for the five-field parsing even
/// though its name says GC; the reference's tasks share one scheduler
/// implementation.
/// </remarks>
public sealed class ProviderQuotaScheduler : BackgroundService
{
    private static readonly Action<ILogger, Exception?> LogRunFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ProviderQuotaScheduledRunFailed"),
            "Scheduled provider quota check failed");

    private readonly ProviderQuotaService _quotaService;
    private readonly GcCronSchedule _schedule;
    private readonly TimeProvider _time;
    private readonly ILogger<ProviderQuotaScheduler> _logger;

    public ProviderQuotaScheduler(
        ProviderQuotaService quotaService,
        TimeProvider time,
        ILogger<ProviderQuotaScheduler> logger)
    {
        _quotaService = quotaService;
        _time = time;
        _logger = logger;
        _schedule = new GcCronSchedule(
            ProviderQuotaService.IntervalToCronExpr(ProviderQuotaService.DefaultCheckInterval));
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
            await _quotaService.RunScheduledCheckAsync(cancellationToken).ConfigureAwait(false);
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
