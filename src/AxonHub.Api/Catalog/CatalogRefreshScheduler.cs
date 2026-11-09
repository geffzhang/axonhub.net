using AxonHub.Api.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Catalog;

/// <summary>
/// The seam the catalog-settings mutation uses to re-arm the refresh task
/// (port of <c>CatalogService.Reschedule(ctx, sched)</c>, catalog.go:92-101).
/// </summary>
public interface ICatalogRescheduleSignal
{
    /// <summary>Re-arms the periodic refresh with the freshly stored interval.</summary>
    Task RescheduleAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Port of the catalog scheduler registration:
/// <c>scheduler.TaskSpec{Name:"providers-catalog-refresh",
/// FixRate:RefreshSeconds}</c> plus the detached initial refresh
/// (catalog.go:68-115).
/// </summary>
/// <remarks>
/// The reference registers one FixRate task and immediately launches a
/// goroutine running <c>Refresh</c> with <c>context.WithoutCancel</c>. The port
/// keeps that shape: the hosted service owns the timer, fires the initial
/// refresh without awaiting it, and restarts the wait when the settings
/// mutation asks for a reschedule. A missed tick is not backfilled — the next
/// fire is computed from the current instant.
/// </remarks>
public sealed class CatalogRefreshScheduler : BackgroundService, ICatalogRescheduleSignal
{
    private static readonly Action<ILogger, Exception?> LogInitialRefreshFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(0, "InitialCatalogRefreshFailed"),
            "initial catalog refresh failed");

    private static readonly Action<ILogger, Exception?> LogScheduledRefreshFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(0, "ScheduledCatalogRefreshFailed"),
            "scheduled catalog refresh failed");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CatalogRefreshScheduler> _logger;

    private TaskCompletionSource _wake =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CatalogRefreshScheduler(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<CatalogRefreshScheduler> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The reference's initial goroutine runs under context.WithoutCancel,
        // so it neither delays startup nor aborts with the hosting context.
        _ = Task.Run(
            () => RefreshAsync(CancellationToken.None, LogInitialRefreshFailed),
            CancellationToken.None);

        // scheduler.Register(...): the task fires every FixRate seconds, the
        // first occurrence one interval after registration.
        while (!stoppingToken.IsCancellationRequested)
        {
            var settings = await ReadSettingsOrDefaultAsync(stoppingToken)
                .ConfigureAwait(false);
            var interval = TimeSpan.FromSeconds(settings.RefreshSeconds);

            var wake = Volatile.Read(ref _wake);
            var delay = Task.Delay(interval, _timeProvider, stoppingToken);
            var completed = await Task.WhenAny(delay, wake.Task).ConfigureAwait(false);
            if (completed == wake.Task)
            {
                // Reschedule: recompute the interval from the stored settings.
                continue;
            }

            try
            {
                await delay.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await RefreshAsync(stoppingToken, LogScheduledRefreshFailed)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Port of <c>CatalogService.Reschedule</c>: a nil scheduler is a no-op,
    /// and a reschedule failure is logged rather than surfaced. The port
    /// restarts the wait; the interval itself is re-read from the settings.
    /// </summary>
    public Task RescheduleAsync(CancellationToken cancellationToken = default)
    {
        var previous = Interlocked.Exchange(
            ref _wake,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        previous.TrySetResult();
        return Task.CompletedTask;
    }

    private async Task RefreshAsync(
        CancellationToken cancellationToken,
        Action<ILogger, Exception?> logFailure)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<ICatalogService>();
            await catalog.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logFailure(_logger, ex);
        }
    }

    private async Task<CatalogSettings> ReadSettingsOrDefaultAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var settings = scope.ServiceProvider
                .GetRequiredService<ISystemSettingsAdminService>();
            return await settings.GetCatalogSettingsOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // CatalogSettingsOrDefault swallows read failures in the reference.
            return CatalogSettingsRules.Default;
        }
    }
}
