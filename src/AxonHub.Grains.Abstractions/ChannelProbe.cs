using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Runs one <c>ChannelProbeService.runProbe</c> pass
/// (internal/server/biz/channel_probe.go:237-335).
/// </summary>
public interface IChannelProbeRunRunner
{
    Task RunProbeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The reference's <c>"channel-probe"</c> cron entry
/// (<c>RegisterScheduledTasks</c>, channel_probe.go:70-79:
/// <c>TaskSpec{Name:"channel-probe", CronExpr:"* * * * *", Timezone:"UTC"}</c>)
/// as an actor: one pending Orleans reminder per UTC minute.
/// </summary>
/// <remarks>
/// The cron string has no source and no mutation path, so the grain computes
/// the minute boundary directly instead of carrying a schedule source. The
/// reference wraps every pass in a system bypass
/// (<c>runProbePeriodically</c>, channel_probe_internal.go:8-12); the runner
/// resolves the scoped service, whose setting read is system-scoped.
/// </remarks>
public interface IChannelProbeGrain : IGrainWithStringKey
{
    /// <summary>The cluster-wide singleton key.</summary>
    public const string SingletonKey = "channel-probe";

    /// <summary>The reminder that carries the minute cadence.</summary>
    public const string ReminderName = "channel-probe";

    /// <summary>
    /// Registration: arms the next minute boundary when none is pending and
    /// returns the scheduled instant. Like the reference's <c>Register</c>, it
    /// never runs the probe.
    /// </summary>
    Task<DateTimeOffset> EnsureScheduledAsync(DateTimeOffset now);

    /// <summary>
    /// The reminder body: arms the following minute first, then runs one pass.
    /// A stale tick for a minute this activation already consumed does not run
    /// again.
    /// </summary>
    Task<DateTimeOffset> FireScheduledAsync(DateTimeOffset now);
}
