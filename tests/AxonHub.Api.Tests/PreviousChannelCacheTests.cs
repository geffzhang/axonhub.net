using AxonHub.Api.Orchestration;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks RequestService's previous-channel cache (request.go:1733-1801,
/// request_sticky_test.go): the cache is the only source of truth for trace and
/// thread affinity, misses return zero instead of querying historical requests,
/// and entries expire after thirty minutes.
/// </summary>
public sealed class PreviousChannelCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RememberCachesTheTraceAndThreadScopes()
    {
        var time = new ManualTimeProvider(Now);
        var cache = new PreviousChannelCache(time);

        cache.Remember(traceId: 10, threadId: 20, channelId: 30);

        Assert.Equal(30, cache.GetPreviousChannelId(10));
        Assert.Equal(30, cache.GetPreviousChannelIdByThread(20));
    }

    [Fact]
    public void RememberIsAPerScopeValueNotAList()
    {
        var cache = new PreviousChannelCache(new ManualTimeProvider(Now));

        cache.Remember(traceId: 10, threadId: 20, channelId: 30);
        cache.Remember(traceId: 10, threadId: 20, channelId: 31);

        // A later selection replaces the earlier one, exactly like the
        // reference's cache.Set.
        Assert.Equal(31, cache.GetPreviousChannelId(10));
        Assert.Equal(31, cache.GetPreviousChannelIdByThread(20));

        // Scopes are keyed independently: another trace is unaffected.
        Assert.Equal(0, cache.GetPreviousChannelId(11));
    }

    [Fact]
    public void MissReturnsZeroAndDoesNotQueryHistory()
    {
        var cache = new PreviousChannelCache(new ManualTimeProvider(Now));

        // request_sticky_test.go: a cached miss never reads the request table.
        Assert.Equal(0, cache.GetPreviousChannelId(10));
        Assert.Equal(0, cache.GetPreviousChannelIdByThread(20));
    }

    [Fact]
    public void RememberWithoutATraceOrChannelIsIgnored()
    {
        var cache = new PreviousChannelCache(new ManualTimeProvider(Now));

        cache.Remember(traceId: 0, threadId: 20, channelId: 30);
        cache.Remember(traceId: 10, threadId: 20, channelId: 0);

        Assert.Equal(0, cache.GetPreviousChannelId(10));
        Assert.Equal(0, cache.GetPreviousChannelIdByThread(20));
    }

    [Fact]
    public void TraceWithoutAThreadCachesOnlyTheTraceScope()
    {
        var cache = new PreviousChannelCache(new ManualTimeProvider(Now));

        cache.Remember(traceId: 10, threadId: null, channelId: 30);

        Assert.Equal(30, cache.GetPreviousChannelId(10));
        Assert.Equal(0, cache.GetPreviousChannelIdByThread(20));
    }

    [Fact]
    public void EntriesExpireAfterThirtyMinutes()
    {
        var time = new ManualTimeProvider(Now);
        var cache = new PreviousChannelCache(time);
        cache.Remember(traceId: 10, threadId: 20, channelId: 30);

        time.UtcNow = Now + PreviousChannelCache.Ttl - TimeSpan.FromSeconds(1);
        Assert.Equal(30, cache.GetPreviousChannelId(10));
        Assert.Equal(30, cache.GetPreviousChannelIdByThread(20));

        // The reference cache returns an error after the expiration, which
        // GetPreviousChannelID turns into 0 and resets affinity.
        time.UtcNow = Now + PreviousChannelCache.Ttl;
        Assert.Equal(0, cache.GetPreviousChannelId(10));
        Assert.Equal(0, cache.GetPreviousChannelIdByThread(20));
    }
}
