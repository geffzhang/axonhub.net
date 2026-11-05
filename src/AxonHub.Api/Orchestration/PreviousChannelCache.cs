namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of orchestrator.PreviousChannelProvider (candidates.go:65-70): the most
/// recently selected channel for a trace or thread routing scope.
/// </summary>
/// <remarks>
/// The reference resolves this through <c>RequestService.GetPreviousChannelID</c>
/// and <c>GetPreviousChannelIDByThread</c>, which read a cache only and never
/// query historical requests (request.go:1733-1755). The port keeps the same
/// shape: a miss returns zero instead of consulting the request table.
/// </remarks>
public interface IPreviousChannelProvider
{
    /// <summary>Returns the cached trace channel, or 0 when none is cached.</summary>
    long GetPreviousChannelId(long traceId);

    /// <summary>Returns the cached thread channel, or 0 when none is cached.</summary>
    long GetPreviousChannelIdByThread(long threadId);

    /// <summary>
    /// Port of cachePreviousChannelForRequest (request.go:1757-1789): writes the
    /// trace key and, when the request belongs to a thread, the thread key.
    /// Both entries share the reference's 30-minute expiry.
    /// </summary>
    void Remember(long traceId, long? threadId, long channelId);
}

/// <summary>
/// In-process port of the reference's <c>previousChannelCache</c>
/// (<c>xcache.NewFromConfig[int](requestCacheConfig)</c>).
/// </summary>
/// <remarks>
/// The reference can back this cache with Redis; the port always keeps it in
/// process memory (C181). Keys and TTL match the reference so the routing
/// decisions are the same in a single-silo deployment.
/// </remarks>
public sealed class PreviousChannelCache(TimeProvider timeProvider) : IPreviousChannelProvider
{
    /// <summary>Port of buildPreviousTraceChannelCacheKey (request.go:1795-1797).</summary>
    public const string TraceKeyPrefix = "axonhub:routing:previous-channel:v1:trace:";

    /// <summary>Port of buildPreviousThreadChannelCacheKey (request.go:1799-1801).</summary>
    public const string ThreadKeyPrefix = "axonhub:routing:previous-channel:v1:thread:";

    /// <summary>Port of the 30*time.Minute expiry in setPreviousChannelCache.</summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public long GetPreviousChannelId(long traceId) => Get(TraceKeyPrefix + traceId);

    public long GetPreviousChannelIdByThread(long threadId) => Get(ThreadKeyPrefix + threadId);

    public void Remember(long traceId, long? threadId, long channelId)
    {
        // cachePreviousChannelForRequest ignores requests without a trace or a
        // selected channel.
        if (traceId == 0 || channelId == 0)
        {
            return;
        }

        var expiresAt = timeProvider.GetUtcNow() + Ttl;
        lock (_gate)
        {
            _entries[TraceKeyPrefix + traceId] = new Entry(channelId, expiresAt);

            // A trace without a thread caches only the trace scope.
            if (threadId is > 0)
            {
                _entries[ThreadKeyPrefix + threadId.Value] = new Entry(channelId, expiresAt);
            }
        }
    }

    private long Get(string key)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var entry))
            {
                return 0;
            }

            // xcache returns a miss once the entry expires, which resets
            // trace/thread affinity (request.go:1735).
            if (entry.ExpiresAt <= timeProvider.GetUtcNow())
            {
                _entries.Remove(key);
                return 0;
            }

            return entry.ChannelId;
        }
    }

    private readonly record struct Entry(long ChannelId, DateTimeOffset ExpiresAt);
}
