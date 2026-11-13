using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Management;

/// <summary>
/// Arms the reminder-driven management tasks at startup: the backup cron
/// (<c>BackupService.RegisterScheduledTasks</c>, service.go:39-55), the GC
/// cron (<c>gc.Worker.RegisterScheduledTasks</c>, gc.go:77-84), the provider
/// quota cron
/// (<c>ProviderQuotaService.RegisterScheduledTasks</c>, provider_quota.go:401-409),
/// the video-storage fixed-rate scan
/// (<c>video_storage.Worker.RegisterScheduledTasks</c>, worker.go:54-71), the
/// channel-probe minute task
/// (<c>ChannelProbeService.RegisterScheduledTasks</c>, channel_probe.go:70-79)
/// and the expired-credential / auto-disable recovery minute task
/// (<c>ChannelService.RegisterScheduledTasks</c>, channel.go:204-223).
/// </summary>
/// <remarks>
/// Orleans' in-memory reminder table does not survive a silo stop and never
/// activates a grain by itself, so on every process start the singleton grains
/// must be touched; their <c>EnsureScheduledAsync</c> re-arms from the current
/// instant, which is exactly the reference's process-restart behaviour (a
/// missed occurrence is not backfilled). The hosted service must start after
/// the silo, so Program registers it after <c>UseOrleans</c>.
/// </remarks>
public sealed class GrainSchedulerActivator(
    IGrainFactory grainFactory,
    TimeProvider time,
    ILogger<GrainSchedulerActivator> logger) : IHostedService
{
    private static readonly Action<ILogger, Exception?> LogRegisterFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ScheduledTaskRegistrationFailed"),
            "Failed to register the scheduled tasks");

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = time.GetUtcNow();
            await grainFactory
                .GetGrain<IGcGrain>(IGcGrain.SingletonKey)
                .EnsureScheduledAsync(now)
                .ConfigureAwait(false);
            await grainFactory
                .GetGrain<IAutoBackupGrain>(IAutoBackupGrain.SingletonKey)
                .EnsureScheduledAsync(now)
                .ConfigureAwait(false);
            await grainFactory
                .GetGrain<IProviderQuotaCoordinatorGrain>(IProviderQuotaCoordinatorGrain.SingletonKey)
                .EnsureScheduledAsync(now)
                .ConfigureAwait(false);
            await grainFactory
                .GetGrain<IVideoStorageGrain>(IVideoStorageGrain.SingletonKey)
                .EnsureScheduledAsync(now)
                .ConfigureAwait(false);
            await grainFactory
                .GetGrain<IChannelProbeGrain>(IChannelProbeGrain.SingletonKey)
                .EnsureScheduledAsync(now)
                .ConfigureAwait(false);
            await grainFactory
                .GetGrain<IChannelCredentialRecoveryGrain>(IChannelCredentialRecoveryGrain.SingletonKey)
                .EnsureScheduledAsync(now)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The reference fails application construction when the scheduled
            // task registration fails; keep that timing so a broken scheduler
            // is not silently skipped.
            LogRegisterFailed(logger, ex);
            throw new InvalidOperationException(
                "failed to register the scheduled tasks",
                ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
