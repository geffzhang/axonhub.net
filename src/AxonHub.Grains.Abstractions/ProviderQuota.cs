using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// One channel as the quota planner loaded it (provider_quota.go:726-757): the
/// checkers read the same fields from the reference's <c>*ent.Channel</c>, so
/// the snapshot carries exactly those columns over the grain boundary instead
/// of making the grain re-read a row the planner already snapshotted.
/// </summary>
[GenerateSerializer]
public sealed class ProviderQuotaChannelSnapshot
{
    [Id(0)]
    public long Id { get; set; }

    [Id(1)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Ent channel type; <c>getProviderType</c> maps it to a provider key.</summary>
    [Id(2)]
    public string Type { get; set; } = string.Empty;

    [Id(3)]
    public string? BaseUrl { get; set; }

    /// <summary>Raw <c>credentials</c> jsonb text.</summary>
    [Id(4)]
    public string Credentials { get; set; } = "{}";

    /// <summary>Raw <c>settings</c> jsonb text (proxy parsing in some checkers).</summary>
    [Id(5)]
    public string? Settings { get; set; }

    /// <summary>Raw <c>supported_models</c> jsonb text.</summary>
    [Id(6)]
    public string SupportedModels { get; set; } = "null";

    [Id(7)]
    public string? ManualModels { get; set; }

    [Id(8)]
    public string? DisabledApiKeys { get; set; }

    /// <summary>
    /// The persisted quota status row the planner loaded through
    /// <c>WithProviderQuotaStatus</c>, or <see langword="null"/> when the
    /// channel has none. The reset refresh deliberately passes
    /// <see langword="null"/> (provider_quota.go:701-713).
    /// </summary>
    [Id(9)]
    public ProviderQuotaStoredStatus? Status { get; set; }
}

/// <summary>
/// Orleans-serializable twin of <c>ent.ProviderQuotaStatus</c> as the quota
/// check consumes it: the due test reads <see cref="NextCheckAt"/>, the error
/// path merges <see cref="QuotaData"/> and the previous provider identity.
/// </summary>
[GenerateSerializer]
public sealed class ProviderQuotaStoredStatus
{
    [Id(0)]
    public long Id { get; set; }

    [Id(1)]
    public long ChannelId { get; set; }

    [Id(2)]
    public string ProviderType { get; set; } = string.Empty;

    [Id(3)]
    public string Status { get; set; } = "unknown";

    /// <summary>objects.QuotaData JSON as stored in the jsonb column.</summary>
    [Id(4)]
    public string QuotaData { get; set; } = "{}";

    [Id(5)]
    public DateTime? NextResetAt { get; set; }

    [Id(6)]
    public bool Ready { get; set; }

    [Id(7)]
    public DateTime NextCheckAt { get; set; }

    [Id(8)]
    public string? AccountKey { get; set; } = string.Empty;
}

/// <summary>
/// One quota check group (provider_quota_account.go:14-17): the channels that
/// share one upstream account. The first member is the reference's
/// <c>group.channels[0]</c>, the channel whose credentials make the request.
/// </summary>
[GenerateSerializer]
public sealed class ProviderQuotaCheckGroup
{
    [Id(0)]
    public string AccountKey { get; set; } = string.Empty;

    [Id(1)]
    public List<ProviderQuotaChannelSnapshot> Members { get; set; } = [];
}

/// <summary>
/// Port of <c>provider_quota.QuotaLimitStatus</c> (types.go:69-104) in the
/// shape the per-channel cache keeps.
/// </summary>
[GenerateSerializer]
public sealed class ProviderQuotaLimitSnapshot
{
    [Id(0)]
    public string Type { get; set; } = string.Empty;

    [Id(1)]
    public string Status { get; set; } = string.Empty;

    [Id(2)]
    public double UsageRatio { get; set; }

    [Id(3)]
    public bool Ready { get; set; }

    [Id(4)]
    public DateTimeOffset? NextResetAt { get; set; }

    [Id(5)]
    public string? AvailabilityGroup { get; set; }

    [Id(6)]
    public string? Window { get; set; }

    [Id(7)]
    public string? Account { get; set; }

    [Id(8)]
    public DateTimeOffset? PeriodStart { get; set; }

    [Id(9)]
    public double? PeriodCost { get; set; }

    [Id(10)]
    public double? PeriodQuota { get; set; }
}

/// <summary>
/// Port of <c>biz.QuotaChannelStatus</c> (provider_quota.go:128-133): the
/// in-memory cache entry one channel grain holds. It mirrors the reference's
/// <c>quotaCache</c> value; the routing gate still reads the API-side copy of
/// the same entry (the reference has a single process, so both are one object
/// there).
/// </summary>
[GenerateSerializer]
public sealed class ProviderQuotaStatusSnapshot
{
    [Id(0)]
    public long ChannelId { get; set; }

    [Id(1)]
    public string ProviderType { get; set; } = string.Empty;

    [Id(2)]
    public string Status { get; set; } = string.Empty;

    [Id(3)]
    public bool Ready { get; set; }

    [Id(4)]
    public List<ProviderQuotaLimitSnapshot> Limits { get; set; } = [];
}

/// <summary>
/// The cache entry after a group check, or <see langword="null"/> when the
/// check removed the channel's status (credentials disappeared,
/// provider_quota.go:800-808) so the member grain must clear its copy too.
/// </summary>
[GenerateSerializer]
public sealed class ProviderQuotaMemberStatus
{
    [Id(0)]
    public long ChannelId { get; set; }

    [Id(1)]
    public ProviderQuotaStatusSnapshot? Status { get; set; }
}

/// <summary>Per-member cache entries produced by one group check.</summary>
[GenerateSerializer]
public sealed class ProviderQuotaGroupCheckResult
{
    [Id(0)]
    public List<ProviderQuotaMemberStatus> Members { get; set; } = [];
}

/// <summary>
/// The reference's post-reset refresh group (provider_quota.go:701-713): the
/// channel that was just reset, with no loaded status edge.
/// </summary>
[GenerateSerializer]
public sealed class ProviderQuotaRefreshRequest
{
    [Id(0)]
    public string AccountKey { get; set; } = string.Empty;

    [Id(1)]
    public ProviderQuotaChannelSnapshot Channel { get; set; } = new();
}

/// <summary>
/// The per-channel quota actor. It is the single entry point for checking one
/// channel's quota group: Orleans runs its turns one at a time, which is the
/// per-channel half of the reference's <c>svc.mu</c> (provider_quota.go:346-359),
/// and it keeps the last cache entry the check produced
/// (provider_quota.go:586-593), like the reference's <c>quotaCache</c>.
/// </summary>
/// <remarks>
/// The API-side <c>ProviderQuotaService</c> keeps its own process mirror of the
/// same cache because the routing gate reads it synchronously; the grain state
/// is activation-local, exactly as the reference's cache is process-local.
/// </remarks>
public interface IProviderQuotaGrain : IGrainWithIntegerKey
{
    /// <summary>
    /// <c>checkChannelQuota</c> for one group (provider_quota.go:792-864),
    /// invoked on the leader channel's grain. Returns every member's cache
    /// entry after the check so the caller can mirror it into the members'
    /// grains. Unexpected failures are logged and produce an empty result,
    /// matching the reference's group goroutine that only logs
    /// (provider_quota.go:779-786).
    /// </summary>
    Task<ProviderQuotaGroupCheckResult> CheckGroupAsync(
        ProviderQuotaCheckGroup group,
        DateTimeOffset now);

    /// <summary>The cache entry this activation holds, or null when none.</summary>
    Task<ProviderQuotaStatusSnapshot?> GetStatusAsync();

    /// <summary>Stores a cache entry (used for group members that did not lead).</summary>
    Task ApplyStatusAsync(ProviderQuotaStatusSnapshot status);

    /// <summary>
    /// Removes the cache entry, the in-memory half of
    /// <c>invalidateChannelQuotaLocked</c> (provider_quota.go:605-618).
    /// </summary>
    Task ClearStatusAsync();
}

/// <summary>
/// The API half of <see cref="IProviderQuotaGrain.CheckGroupAsync"/>: performs
/// the actual provider request and persistence (provider_quota.go:792-864).
/// </summary>
public interface IProviderQuotaGroupRunner
{
    Task<ProviderQuotaGroupCheckResult> RunGroupAsync(
        ProviderQuotaCheckGroup group,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The next <c>provider-quota-check</c> firing instant. The reference registers
/// <c>intervalToCronExpr(checkInterval)</c> with timezone UTC
/// (provider_quota.go:401-409); the service's interval is the fixed five-minute
/// default (provider_quota.go:534-540), so this is <c>*/5 * * * *</c>.
/// </summary>
public interface IProviderQuotaScheduleSource
{
    DateTimeOffset NextOccurrence(DateTimeOffset after);
}

/// <summary>
/// The API work the coordinator serializes. The reference holds <c>svc.mu</c>
/// across the whole collection run, cache invalidation and the post-reset
/// refresh (provider_quota.go:357, 605-618, 665-717, 719-790); the coordinator
/// grain's turns provide that exclusion cluster-wide.
/// </summary>
public interface IProviderQuotaCoordinatorOperations
{
    /// <summary><c>runQuotaCheck</c> (provider_quota.go:719-790).</summary>
    Task RunAsync(bool force, CancellationToken cancellationToken = default);

    /// <summary>The post-reset refresh group (provider_quota.go:701-713).</summary>
    Task RefreshChannelAsync(
        ProviderQuotaRefreshRequest request,
        CancellationToken cancellationToken = default);

    /// <summary><c>invalidateChannelQuotaLocked</c> (provider_quota.go:605-618).</summary>
    Task InvalidateAsync(long channelId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The reference's provider quota scheduling and mutual exclusion
/// (provider_quota.go:346-359) as one singleton actor: the
/// <c>provider-quota-check</c> reminder replaces the cron registered by
/// <c>RegisterScheduledTasks</c> (provider_quota.go:401-409), and grain turns
/// replace <c>svc.mu</c> across scheduled checks, manual checks, invalidations
/// and post-reset refreshes.
/// </summary>
/// <remarks>
/// Reminders have no one-shot form and a one-minute minimum period, so the
/// grain keeps the pending occurrence and re-arms it on every firing; the same
/// shape as the backup and GC grains (§149). The in-memory reminder table does
/// not survive a silo stop, so the startup activator re-arms from the current
/// instant, like the reference re-registering its cron.
/// </remarks>
public interface IProviderQuotaCoordinatorGrain : IGrainWithStringKey
{
    /// <summary>The cluster-wide singleton key.</summary>
    public const string SingletonKey = "provider-quota";

    /// <summary>The reference's task name (provider_quota.go:404).</summary>
    public const string ReminderName = "provider-quota-check";

    /// <summary>Arms the next occurrence without running the check.</summary>
    Task<DateTimeOffset> EnsureScheduledAsync(DateTimeOffset now);

    /// <summary>Re-arms the next occurrence, then runs the scheduled check.</summary>
    Task<DateTimeOffset> FireScheduledAsync(DateTimeOffset now);

    /// <summary><c>ManualCheck</c> (provider_quota.go:620-623).</summary>
    Task RunManualAsync();

    /// <summary>The post-reset refresh (provider_quota.go:701-713).</summary>
    Task RefreshChannelAsync(ProviderQuotaRefreshRequest request);

    /// <summary>
    /// <c>InvalidateChannelQuota</c> (provider_quota.go:595-603); errors
    /// propagate to the caller, which logs them like the reference's channel
    /// hook (channel_provider_quota_hook.go:22-32).
    /// </summary>
    Task InvalidateAsync(long channelId);
}
