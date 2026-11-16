using AxonHub.Api.Settings;
using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Management;

/// <summary>
/// Resolves the video-storage scan interval from
/// <c>system_video_storage_settings</c> (video_storage/worker.go:61-70, 83-86).
/// The settings row is re-read on every arm, so a mutation that only reschedules
/// still sees the new value; a non-positive interval floors to one minute.
/// </summary>
public sealed class GrainVideoStorageScheduleSource(IServiceScopeFactory scopeFactory)
    : IVideoStorageScheduleSource
{
    /// <summary>The reference worker's fallback (worker.go:61-64).</summary>
    public const int DefaultIntervalMinutes = 1;

    public async Task<TimeSpan> GetIntervalAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var settings = scope.ServiceProvider
            .GetRequiredService<ISystemSettingsAdminService>();
        var video = await settings.GetVideoStorageSettingsAsync(cancellationToken)
            .ConfigureAwait(false);

        var minutes = video.ScanIntervalMinutes;
        if (minutes <= 0)
        {
            minutes = DefaultIntervalMinutes;
        }

        return TimeSpan.FromMinutes(minutes);
    }
}

/// <summary>
/// Runs one scan pass in a fresh scope: the grain turn has no request scope of
/// its own (video_storage.Worker.runScanWithSystemContext, worker.go:97-107).
/// </summary>
public sealed class GrainVideoStorageScanRunner(IServiceScopeFactory scopeFactory)
    : IVideoStorageScanRunner
{
    /// <summary>runScanWithSystemContext's ten-minute scan budget (worker.go:101).</summary>
    public static readonly TimeSpan ScanTimeout = TimeSpan.FromMinutes(10);

    public async Task ScanAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var worker = scope.ServiceProvider.GetRequiredService<VideoStorageWorker>();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ScanTimeout);

        await worker.ScanAndSaveAsync(timeout.Token).ConfigureAwait(false);
    }
}

/// <summary>
/// Production <see cref="IVideoStorageRescheduleSignal"/>: re-arms the
/// singleton video-storage grain after the settings change
/// (video_storage.Worker.Reschedule, worker.go:73-95). Like the reference, a
/// scheduling failure is logged and does not fail the mutation.
/// </summary>
public sealed class GrainVideoStorageRescheduleSignal(
    IGrainFactory grainFactory,
    TimeProvider time,
    ILogger<GrainVideoStorageRescheduleSignal> logger) : IVideoStorageRescheduleSignal
{
    private static readonly Action<ILogger, Exception?> LogRescheduleFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "VideoStorageRescheduleFailed"),
            "Failed to reschedule video storage worker");

    public async Task RescheduleAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await grainFactory
                .GetGrain<IVideoStorageGrain>(IVideoStorageGrain.SingletonKey)
                .RescheduleAsync(time.GetUtcNow())
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRescheduleFailed(logger, ex);
        }
    }
}
