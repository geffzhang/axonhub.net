using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// The reference's model-sync execution gate
/// (internal/server/biz/channel_internal.go:23-59): <c>modelSyncMu</c> plus
/// <c>lastModelSyncExecutionTime</c>, which let the periodic
/// <c>runSyncChannelModelsPeriodically</c> run at most once per aligned
/// auto-sync interval.
/// </summary>
/// <remarks>
/// <para>
/// The reference keeps this guard inside <c>ChannelService</c>, so every
/// process has its own copy. One singleton grain gives the whole cluster a
/// single gate: Orleans serializes the grain's turns (replacing
/// <c>modelSyncMu</c>) and the stored window is shared by every silo
/// (§148; C228).
/// </para>
/// <para>
/// The state is activation-local, like the reference's process-local fields:
/// it disappears when the silo restarts, which re-arms the next tick exactly
/// as a reference restart does. The grain renews its keep-alive on every call,
/// so an hourly scheduler tick keeps the window across deactivation.
/// </para>
/// </remarks>
public interface IChannelModelSyncGrain : IGrainWithStringKey
{
    /// <summary>The cluster-wide singleton key.</summary>
    public const string SingletonKey = "model-sync";

    /// <summary>
    /// <c>shouldRunModelSync</c> (channel_internal.go:33-46): truncates
    /// <paramref name="now"/> down to a multiple of
    /// <paramref name="intervalMinutes"/> and returns true exactly once per
    /// aligned window, recording that window as the last execution. Every call
    /// renews the activation keep-alive.
    /// </summary>
    Task<bool> TryBeginRunAsync(DateTimeOffset now, int intervalMinutes);
}
