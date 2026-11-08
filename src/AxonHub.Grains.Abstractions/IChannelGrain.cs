using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Owner of one channel's runtime load and health counters. The reference keeps
/// these in <c>ChannelService.channelPerfMetrics</c> (internal/server/biz/channel_metrics.go);
/// here each channel's counters have a single writer, so no cross-request locking
/// is needed.
/// </summary>
public interface IChannelGrain : IGrainWithIntegerKey
{
    /// <summary>Returns the current snapshot, expiring selections older than the window.</summary>
    Task<ChannelMetricsSnapshot> GetMetricsAsync(DateTimeOffset now);

    /// <summary>Records a selection and starts its sliding-window lifetime.</summary>
    Task RecordSelectionAsync(DateTimeOffset selectedAt);

    /// <summary>Records a completed request; resets consecutive failures.</summary>
    Task RecordSuccessAsync(DateTimeOffset completedAt);

    /// <summary>Records a failed request; extends the failure cooldown.</summary>
    Task RecordFailureAsync(DateTimeOffset completedAt);

    /// <summary>Clears all counters, mirroring a fresh reference process.</summary>
    Task ResetMetricsAsync();

    /// <summary>
    /// Atomically checks and consumes one request slot in the current natural
    /// minute bucket. A non-positive <paramref name="limit"/> means "no RPM
    /// limit", so no slot is consumed.
    /// </summary>
    Task<bool> TryAcquireRequestAsync(long limit, DateTimeOffset now);

    /// <summary>
    /// Adds tokens to the current natural-minute bucket; non-positive values are
    /// ignored (the reference's <c>AddTokens</c>).
    /// </summary>
    Task AddTokensAsync(long tokens, DateTimeOffset now);

    /// <summary>
    /// Extends the channel's 429 cooldown. Only extends: a shorter value never
    /// overwrites a longer existing one.
    /// </summary>
    Task SetCooldownAsync(DateTimeOffset until);

    /// <summary>Clears the rate-limit counters and cooldown of this channel.</summary>
    Task ResetRateLimitAsync();
}
