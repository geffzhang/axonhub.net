using AxonHub.Abstractions;
using AxonHub.Api.Orchestration;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the production seams of the enabled-channel cache slice:
/// <see cref="GrainEnabledChannelProvider"/> serves the snapshot owned by
/// <see cref="AxonHub.Grains.Abstractions.IEnabledChannelsGrain"/> instead of
/// querying PostgreSQL per selection, and
/// <see cref="GrainEnabledChannelsCacheRefresher"/> forces a synchronous reload
/// through the same grain.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class EnabledChannelsGrainProviderTests(GrainClusterFixture fixture)
{
    private static readonly DateTime Fingerprint = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ProviderMapsTheGrainSnapshot()
    {
        await SeedAsync(Channel(41, "one"));

        var channels = await Provider().ListEnabledAsync();

        var channel = Assert.Single(channels);
        Assert.Equal(41, channel.Id);
        Assert.Equal("one", channel.Name);
        Assert.Equal(41, channel.OrderingWeight);
        Assert.Equal(Fingerprint, channel.CreatedAt);
        Assert.Equal("openai", channel.Type);
        Assert.Equal("https://api.example.com/v1", channel.BaseUrl);
        Assert.Equal("""{"api_key":"sk-grain"}""", channel.Credentials);
        Assert.Equal("""["gpt-4o"]""", channel.SupportedModels);
        Assert.Equal("gpt-4o", channel.DefaultTestModel);
    }

    [Fact]
    public async Task ReloadAsyncForcesTheSnapshotWithoutAFingerprintChange()
    {
        await SeedAsync(Channel(51, "before"));
        _ = await Provider().ListEnabledAsync();

        // Same fingerprint: only a forced reload reads the store again, which
        // is what the synchronous API-key-exhaustion path relies on.
        fixture.Channels.Set(Channel(52, "after"));

        await Refresher().ReloadAsync();

        var channel = Assert.Single(await Provider().ListEnabledAsync());
        Assert.Equal(52, channel.Id);
        Assert.Equal("after", channel.Name);
    }

    private async Task SeedAsync(params ChannelDescriptor[] channels)
    {
        fixture.Channels.LatestUpdatedAt = Fingerprint;
        fixture.Channels.Set(channels);
        await Refresher().ReloadAsync();
    }

    private GrainEnabledChannelProvider Provider() =>
        new(fixture.Cluster.GrainFactory);

    private GrainEnabledChannelsCacheRefresher Refresher() =>
        new(fixture.Cluster.GrainFactory);

    private static ChannelDescriptor Channel(long id, string name) => new()
    {
        Id = id,
        Name = name,
        OrderingWeight = id,
        CreatedAt = Fingerprint,
        Type = "openai",
        BaseUrl = "https://api.example.com/v1",
        Credentials = """{"api_key":"sk-grain"}""",
        SupportedModels = """["gpt-4o"]""",
        DefaultTestModel = "gpt-4o",
    };
}
