using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Persistence;

/// <summary>
/// Port of LiveStreamRegistry.StartSweeper (stream_preview.go:110-124): every
/// five minutes, drop closed buffers and force-close buffers idle for more than
/// ten minutes. The reference starts this on the fx lifecycle; the port mounts
/// it as a hosted service on the same singleton registry.
/// </summary>
public sealed class LiveStreamRegistrySweeper(
    LiveStreamRegistry registry,
    TimeProvider timeProvider,
    ILogger<LiveStreamRegistrySweeper> logger) : BackgroundService
{
    /// <summary>Port of the 5-minute ticker.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private static readonly Action<ILogger, int, Exception?> LogSwept =
        LoggerMessage.Define<int>(
            LogLevel.Debug,
            new EventId(0, "LiveStreamRegistrySwept"),
            "preview registry swept stale entries: {Evicted}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval, timeProvider);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            var evicted = registry.SweepStaleEntries(timeProvider.GetUtcNow());
            if (evicted > 0)
            {
                LogSwept(logger, evicted, null);
            }
        }
    }
}
