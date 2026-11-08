using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Production <see cref="IPreviousChannelProvider"/>: one
/// <see cref="IPreviousChannelGrain"/> per trace or thread cache key, so every
/// silo in the cluster observes the same trace/thread affinity (§147). The
/// in-process <see cref="PreviousChannelCache"/> remains for direct
/// constructions and tests.
/// </summary>
/// <remarks>
/// The reference reads and writes <c>RequestService.previousChannelCache</c>
/// synchronously (request.go:1733-1801), and both call sites — the persistence
/// middleware's outbound hook and the candidate selector — are synchronous by
/// design (C018). The grain calls are therefore awaited on the calling thread,
/// like <c>GrainTokenGetter</c>. A lookup failure becomes a cache miss, which
/// the reference logs and treats the same way; a write failure is discarded,
/// like <c>_ = s.previousChannelCache.Set(...)</c>.
/// </remarks>
public sealed class GrainPreviousChannelProvider(
    IGrainFactory grainFactory,
    ILogger<GrainPreviousChannelProvider> logger) : IPreviousChannelProvider
{
    private static readonly Action<ILogger, long, Exception?> LogTraceLookupFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(0, "PreviousTraceChannelLookupFailed"),
            "failed to get previous trace channel trace_id={TraceId}");

    private static readonly Action<ILogger, long, Exception?> LogThreadLookupFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(1, "PreviousThreadChannelLookupFailed"),
            "failed to get previous thread channel thread_id={ThreadId}");

    public long GetPreviousChannelId(long traceId)
        => Lookup(PreviousChannelCache.TraceKeyPrefix + traceId, traceId, thread: false);

    public long GetPreviousChannelIdByThread(long threadId)
        => Lookup(PreviousChannelCache.ThreadKeyPrefix + threadId, threadId, thread: true);

    public void Remember(long traceId, long? threadId, long channelId)
    {
        // cachePreviousChannelForRequest ignores requests without a trace or a
        // selected channel.
        if (traceId == 0 || channelId == 0)
        {
            return;
        }

        TryRemember(PreviousChannelCache.TraceKeyPrefix + traceId, channelId);

        // A trace without a thread caches only the trace scope.
        if (threadId is > 0)
        {
            TryRemember(PreviousChannelCache.ThreadKeyPrefix + threadId.Value, channelId);
        }
    }

    private long Lookup(string key, long id, bool thread)
    {
        try
        {
            return grainFactory
                .GetGrain<IPreviousChannelGrain>(key)
                .GetAsync()
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            // request.go:1737-1757 logs and falls back to a zero (no affinity).
            if (thread)
            {
                LogThreadLookupFailed(logger, id, ex);
            }
            else
            {
                LogTraceLookupFailed(logger, id, ex);
            }

            return 0;
        }
    }

    private void TryRemember(string key, long channelId)
    {
        try
        {
            grainFactory
                .GetGrain<IPreviousChannelGrain>(key)
                .RememberAsync(channelId)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception)
        {
            // setPreviousChannelCache discards write errors; affinity is a cache.
        }
    }
}
