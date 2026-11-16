using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Runs one <c>ChannelService.cleanupExpiredDisabledAPIKeys</c> pass
/// (internal/server/biz/channel_apikey.go:393-436).
/// </summary>
public interface IChannelCredentialRecoveryRunner
{
    Task RunRecoveryAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The reference's <c>"channel-disabled-api-key-cleanup"</c> cron entry
/// (<c>RegisterScheduledTasks</c>, channel.go:204-223:
/// <c>TaskSpec{Name:"channel-disabled-api-key-cleanup", CronExpr:"* * * * *",
/// Timezone:"UTC"}</c>) as an actor: one pending Orleans reminder per UTC
/// minute.
/// </summary>
/// <remarks>
/// The cron string has no source and no mutation path, so the grain computes
/// the minute boundary directly instead of carrying a schedule source. The
/// reference wraps every pass in a system bypass
/// (<c>authz.WithSystemBypass(ctx, "channel-cleanup-expired-disabled-api-keys")</c>,
/// channel_apikey.go:398); the runner resolves the scoped lifecycle service,
/// whose cleanup path performs no privacy evaluation.
/// </remarks>
public interface IChannelCredentialRecoveryGrain : IGrainWithStringKey
{
    /// <summary>The reference task name, used as the cluster-wide singleton key.</summary>
    public const string SingletonKey = "channel-disabled-api-key-cleanup";

    /// <summary>The reminder that carries the minute cadence.</summary>
    public const string ReminderName = "channel-disabled-api-key-cleanup";

    /// <summary>
    /// Registration: arms the next minute boundary when none is pending and
    /// returns the scheduled instant. Like the reference's <c>Register</c>, it
    /// never runs the cleanup.
    /// </summary>
    Task<DateTimeOffset> EnsureScheduledAsync(DateTimeOffset now);

    /// <summary>
    /// The reminder body: arms the following minute first, then runs one
    /// cleanup pass. A stale tick for a minute this activation already consumed
    /// does not run again.
    /// </summary>
    Task<DateTimeOffset> FireScheduledAsync(DateTimeOffset now);
}
