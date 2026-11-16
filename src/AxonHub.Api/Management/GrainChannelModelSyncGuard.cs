using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Management;

/// <summary>
/// Production <see cref="IChannelModelSyncGuard"/>: the reference's
/// <c>modelSyncMu</c> and <c>lastModelSyncExecutionTime</c>
/// (channel_internal.go:33-46) moved into the singleton
/// <see cref="IChannelModelSyncGrain"/>, so every silo shares one execution
/// window (§148; C228).
/// </summary>
/// <remarks>
/// The reference's guard is process-local and cannot fail; a grain dispatch
/// failure has no counterpart. This bridge treats it as "do not run this
/// tick": running the pass on every silo at once (the failure fallback that
/// would mimic the reference's per-process guard) is worse than skipping one
/// tick, and the next hourly tick retries.
/// </remarks>
public sealed class GrainChannelModelSyncGuard(
    IGrainFactory grainFactory,
    ILogger<GrainChannelModelSyncGuard> logger) : IChannelModelSyncGuard
{
    private static readonly Action<ILogger, Exception?> LogBeginRunFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(0, "ChannelModelSyncGuardFailed"),
            "failed to evaluate the channel model sync window; skipping this tick");

    public async Task<bool> TryBeginRunAsync(
        DateTimeOffset now,
        AutoSyncFrequency autoSyncFrequency,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await grainFactory
                .GetGrain<IChannelModelSyncGrain>(IChannelModelSyncGrain.SingletonKey)
                .TryBeginRunAsync(now, ChannelModelSyncService.GetIntervalMinutes(autoSyncFrequency))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogBeginRunFailed(logger, ex);
            return false;
        }
    }
}
