using AxonHub.Grains.Abstractions;
using Orleans;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Seam over the per-channel RPM/TPM/cooldown state. The reference keeps it in
/// the process-wide <c>orchestrator.ChannelRequestTracker</c>; the port routes
/// every admission, token record and Retry-After cooldown to the channel grain
/// that owns the counters. The pipeline hooks are synchronous (the Go pipeline
/// is synchronous), so the grain call is awaited on the calling thread exactly
/// like <see cref="GrainChannelMetricsRecorder"/>.
/// </summary>
public interface IChannelRateLimitRecorder
{
    /// <summary>
    /// Atomically checks and consumes one request slot in the current natural
    /// minute bucket. A non-positive limit bypasses the budget.
    /// </summary>
    bool TryAcquireRequest(long channelId, long limit, DateTimeOffset now);

    /// <summary>Adds tokens to the channel's current bucket; non-positive values are ignored.</summary>
    void AddTokens(long channelId, long tokens, DateTimeOffset now);

    /// <summary>Extends the channel's 429 cooldown; a shorter value never overwrites a longer one.</summary>
    void SetCooldown(long channelId, DateTimeOffset until);
}

/// <summary>
/// In-process recorder used by direct test constructions (and as the fallback
/// when an orchestrator is built without a grain factory). The tracker remains
/// the reference state machine; production hosts one per channel inside
/// <see cref="IChannelGrain"/> instead.
/// </summary>
public sealed class InMemoryChannelRateLimitRecorder(ChannelRequestTracker tracker) : IChannelRateLimitRecorder
{
    // The in-memory adapter keeps the tracker's own TimeProvider authoritative:
    // direct test constructions move that clock between minute buckets, and the
    // production grain path always passes an explicit now instead.
    public bool TryAcquireRequest(long channelId, long limit, DateTimeOffset now)
        => tracker.TryAcquireRequest(channelId, limit);

    public void AddTokens(long channelId, long tokens, DateTimeOffset now)
        => tracker.AddTokens(channelId, tokens);

    public void SetCooldown(long channelId, DateTimeOffset until)
        => tracker.SetCooldown(channelId, until);
}

/// <summary>
/// Grain-backed recorder. Each call is one grain turn, which serializes against
/// every other request for the same channel and makes the counters the single
/// authority the pool grain scores from.
/// </summary>
public sealed class GrainChannelRateLimitRecorder(IGrainFactory grainFactory) : IChannelRateLimitRecorder
{
    public bool TryAcquireRequest(long channelId, long limit, DateTimeOffset now)
        => CallBool(channelId, grain => grain.TryAcquireRequestAsync(limit, now));

    public void AddTokens(long channelId, long tokens, DateTimeOffset now)
        => CallVoid(channelId, grain => grain.AddTokensAsync(tokens, now));

    public void SetCooldown(long channelId, DateTimeOffset until)
        => CallVoid(channelId, grain => grain.SetCooldownAsync(until));

    private bool CallBool(long channelId, Func<IChannelGrain, Task<bool>> call)
        => call(grainFactory.GetGrain<IChannelGrain>(channelId)).GetAwaiter().GetResult();

    private void CallVoid(long channelId, Func<IChannelGrain, Task> call)
        => call(grainFactory.GetGrain<IChannelGrain>(channelId)).GetAwaiter().GetResult();
}
