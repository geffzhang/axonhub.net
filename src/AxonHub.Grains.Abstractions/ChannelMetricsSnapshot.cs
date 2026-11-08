using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Point-in-time view of a channel's runtime metrics, matching the reference
/// <c>biz.AggregatedMetrics</c> fields the round-robin load balancer consumes.
/// </summary>
[GenerateSerializer]
public sealed class ChannelMetricsSnapshot
{
    [Id(0)]
    public long ChannelId { get; set; }

    /// <summary>Selections recorded inside the reference's 10 minute window.</summary>
    [Id(1)]
    public long RequestCount { get; set; }

    [Id(2)]
    public long ConsecutiveFailures { get; set; }

    [Id(3)]
    public DateTimeOffset? LastSelectedAt { get; set; }

    [Id(4)]
    public DateTimeOffset? LastFailureAt { get; set; }

    /// <summary>Successful completions inside the same 10 minute window.</summary>
    [Id(5)]
    public long SuccessCount { get; set; }

    /// <summary>Failed completions inside the same 10 minute window.</summary>
    [Id(6)]
    public long FailureCount { get; set; }

    /// <summary>
    /// Requests consumed in the current natural-minute RPM bucket (the
    /// reference's <c>ChannelRequestTracker.GetRequestCount</c>).
    /// </summary>
    [Id(7)]
    public long RateLimitRequests { get; set; }

    /// <summary>
    /// Tokens consumed in the current natural-minute TPM bucket (the
    /// reference's <c>ChannelRequestTracker.GetTokenCount</c>).
    /// </summary>
    [Id(8)]
    public long RateLimitTokens { get; set; }

    /// <summary>
    /// Active 429 cooldown expiry, or <see langword="null"/> when the channel is
    /// not cooling down. Expired entries are cleaned before this is returned.
    /// </summary>
    [Id(9)]
    public DateTimeOffset? CooldownUntil { get; set; }
}

/// <summary>
/// The rate-limit slice of <see cref="ChannelMetricsSnapshot"/>, consumed by
/// <see cref="RateLimitAwareScorer"/> without reaching back into the per-silo
/// tracker. The cooldown is already expiry-checked by the grain.
/// </summary>
public readonly record struct ChannelRateLimitState(
    long RequestCount,
    long TokenCount,
    DateTimeOffset? CooldownUntil);
