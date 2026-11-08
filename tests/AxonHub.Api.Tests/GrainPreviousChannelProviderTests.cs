using AxonHub.Api.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the grain-backed previous-channel provider (§147): the production
/// <see cref="IPreviousChannelProvider"/> maps trace/thread ids onto the
/// reference's cache keys and keeps the cache-write rules of
/// cachePreviousChannelForRequest (request.go:1757-1801). The in-process
/// <see cref="PreviousChannelCache"/> keeps its own suite.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class GrainPreviousChannelProviderTests(GrainClusterFixture fixture)
{
    private GrainPreviousChannelProvider Provider()
        => new(fixture.Cluster.GrainFactory, NullLogger<GrainPreviousChannelProvider>.Instance);

    [Fact]
    public void RememberCachesTheTraceAndThreadScopes()
    {
        var provider = Provider();

        provider.Remember(traceId: 70010, threadId: 70020, channelId: 30);

        Assert.Equal(30, provider.GetPreviousChannelId(70010));
        Assert.Equal(30, provider.GetPreviousChannelIdByThread(70020));
    }

    [Fact]
    public void RememberIsAPerScopeValueNotAList()
    {
        var provider = Provider();

        provider.Remember(traceId: 70011, threadId: 70021, channelId: 30);
        provider.Remember(traceId: 70011, threadId: 70021, channelId: 31);

        // A later selection replaces the earlier one, exactly like cache.Set.
        Assert.Equal(31, provider.GetPreviousChannelId(70011));
        Assert.Equal(31, provider.GetPreviousChannelIdByThread(70021));

        // Scopes are keyed independently: another trace is unaffected.
        Assert.Equal(0, provider.GetPreviousChannelId(70012));
    }

    [Fact]
    public void RememberWithoutATraceOrChannelIsIgnored()
    {
        var provider = Provider();

        provider.Remember(traceId: 0, threadId: 70022, channelId: 30);
        provider.Remember(traceId: 70013, threadId: 70022, channelId: 0);

        Assert.Equal(0, provider.GetPreviousChannelId(70013));
        Assert.Equal(0, provider.GetPreviousChannelIdByThread(70022));
    }

    [Fact]
    public void TraceWithoutAThreadCachesOnlyTheTraceScope()
    {
        var provider = Provider();

        provider.Remember(traceId: 70014, threadId: null, channelId: 32);

        Assert.Equal(32, provider.GetPreviousChannelId(70014));
        Assert.Equal(0, provider.GetPreviousChannelIdByThread(70023));
    }

    [Fact]
    public void MissReturnsZeroAndDoesNotQueryHistory()
    {
        var provider = Provider();

        Assert.Equal(0, provider.GetPreviousChannelId(70015));
        Assert.Equal(0, provider.GetPreviousChannelIdByThread(70024));
    }
}
