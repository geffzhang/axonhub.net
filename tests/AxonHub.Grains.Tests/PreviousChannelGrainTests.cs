using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Locks the previous-channel cache actor (§147): one grain per trace/thread
/// cache key, the per-key overwrite, the zero miss for absent entries, and the
/// thirty-minute expiry that production configures (request.go:1733-1801). The
/// shared cluster shortens the TTL to one second so expiry is observable.
/// </summary>
[Collection(GrainClusterCollection.Name)]
public sealed class PreviousChannelGrainTests(GrainClusterFixture fixture)
{
    private static readonly TimeSpan ExpiryWait = TimeSpan.FromSeconds(1.1);

    private IPreviousChannelGrain Grain(string key)
        => fixture.Cluster.GrainFactory.GetGrain<IPreviousChannelGrain>(key);

    [Fact]
    public async Task MissingKeyReturnsZero()
    {
        // request_sticky_test.go: a cached miss never reads the request table.
        Assert.Equal(0, await Grain("prior:missing").GetAsync());
    }

    [Fact]
    public async Task RememberStoresTheChannelPerKey()
    {
        var trace = Grain("prior:trace:1");
        var thread = Grain("prior:thread:1");

        await trace.RememberAsync(30);
        await thread.RememberAsync(31);

        // Scopes are keyed independently: a later selection on one scope never
        // leaks into another.
        Assert.Equal(30, await trace.GetAsync());
        Assert.Equal(31, await thread.GetAsync());
    }

    [Fact]
    public async Task RememberOverwritesThePreviousChannel()
    {
        var grain = Grain("prior:overwrite");

        await grain.RememberAsync(30);
        await grain.RememberAsync(31);

        // A later selection replaces the earlier one, like cache.Set.
        Assert.Equal(31, await grain.GetAsync());
    }

    [Fact]
    public async Task EntryExpiresAfterTheTtl()
    {
        var grain = Grain("prior:expiry");

        await grain.RememberAsync(30);
        Assert.Equal(30, await grain.GetAsync());

        await Task.Delay(ExpiryWait);

        // The reference cache returns an error after the expiration, which
        // GetPreviousChannelID turns into 0 and resets affinity.
        Assert.Equal(0, await grain.GetAsync());
    }
}
