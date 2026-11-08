using AxonHub.Abstractions;
using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Locks the enabled-channel cache actor
/// (internal/server/biz/channel.go:108-430, internal/pkg/xcache/live/cache.go):
/// the cold load that starts the serving snapshot, the <c>updated_at</c>
/// fingerprint that includes soft deletes, forced reloads that always swap and
/// bump <c>cacheVersion</c>, the keep-last-good behavior on refresh failure, and
/// the 500ms debounce behind channel mutations.
/// </summary>
[Collection(GrainClusterCollection.Name)]
public sealed class EnabledChannelsGrainTests(GrainClusterFixture fixture)
{
    private static readonly DateTime Start = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ColdLoadServesTheEnabledSnapshot()
    {
        fixture.Channels.Reset();
        fixture.Channels.LatestUpdatedAt = Start;
        fixture.Channels.Set(Channel(101, "alpha"), Channel(102, "beta"));

        var grain = Grain("cold-load");

        var snapshot = await grain.GetSnapshotAsync();

        Assert.Equal(1, snapshot.Version);
        Assert.Equal(Start, snapshot.LastUpdatedAt);
        Assert.Equal(new[] { 101L, 102L }, snapshot.Channels.Select(channel => channel.Id).ToArray());
        Assert.Equal("alpha", snapshot.Channels[0].Name);
        Assert.Equal(1, fixture.Channels.LatestUpdatedAtCalls);
        Assert.Equal(1, fixture.Channels.ListEnabledCalls);

        // Reads are served from the swapped snapshot, like live.Cache.GetData.
        var again = await grain.GetSnapshotAsync();

        Assert.Equal(1, again.Version);
        Assert.Equal(1, fixture.Channels.LatestUpdatedAtCalls);
        Assert.Equal(1, fixture.Channels.ListEnabledCalls);
    }

    [Fact]
    public async Task NonForcedRefreshSkipsAnUnchangedFingerprint()
    {
        fixture.Channels.Reset();
        fixture.Channels.LatestUpdatedAt = Start;
        fixture.Channels.Set(Channel(201, "first"));

        var grain = Grain("fingerprint-skip");
        _ = await grain.GetSnapshotAsync();

        // The store changed but the fingerprint did not (e.g. something wrote
        // the same channel again): reloadEnabledChannels returns changed=false
        // and loadInternal leaves the snapshot alone.
        fixture.Channels.Set(Channel(202, "not-read"));

        var snapshot = await grain.RefreshAsync(force: false);

        Assert.Equal(1, snapshot.Version);
        Assert.Equal(201, Assert.Single(snapshot.Channels).Id);
        Assert.Equal(1, fixture.Channels.ListEnabledCalls);
    }

    [Fact]
    public async Task NonForcedRefreshReloadsWhenTheFingerprintAdvances()
    {
        fixture.Channels.Reset();
        fixture.Channels.LatestUpdatedAt = Start;
        fixture.Channels.Set(Channel(301, "old"));

        var grain = Grain("fingerprint-advance");
        _ = await grain.GetSnapshotAsync();

        var advanced = Start.AddSeconds(5);
        fixture.Channels.LatestUpdatedAt = advanced;
        fixture.Channels.Set(Channel(302, "new"));

        var snapshot = await grain.RefreshAsync(force: false);

        Assert.Equal(2, snapshot.Version);
        Assert.Equal(302, Assert.Single(snapshot.Channels).Id);
        Assert.Equal(advanced, snapshot.LastUpdatedAt);
        Assert.Equal(2, fixture.Channels.ListEnabledCalls);
    }

    [Fact]
    public async Task DeletionOnlyFingerprintAdvanceDropsTheChannel()
    {
        fixture.Channels.Reset();
        fixture.Channels.LatestUpdatedAt = Start;
        fixture.Channels.Set(Channel(401, "kept"), Channel(402, "deleted"));

        var grain = Grain("fingerprint-delete");
        _ = await grain.GetSnapshotAsync();

        // A soft delete keeps updated_at in the row, so the fingerprint moves
        // even though the enabled query now returns one row fewer.
        fixture.Channels.LatestUpdatedAt = Start.AddMinutes(1);
        fixture.Channels.Set(Channel(401, "kept"));

        var snapshot = await grain.RefreshAsync(force: false);

        Assert.Equal(2, snapshot.Version);
        Assert.Equal(401, Assert.Single(snapshot.Channels).Id);
        Assert.Equal(Start.AddMinutes(1), snapshot.LastUpdatedAt);
    }

    [Fact]
    public async Task NonForcedRefreshWithNothingCachedAndAnEmptyTableDoesNotReload()
    {
        fixture.Channels.Reset();
        fixture.Channels.LatestUpdatedAt = null;
        fixture.Channels.Set();

        var grain = Grain("empty-skip");

        var first = await grain.GetSnapshotAsync();

        Assert.Empty(first.Channels);
        Assert.Equal(1, first.Version);
        Assert.Null(first.LastUpdatedAt);
        Assert.Equal(1, fixture.Channels.ListEnabledCalls);

        var second = await grain.RefreshAsync(force: false);

        // lastUpdate zero + empty current returns changed=false.
        Assert.Equal(1, second.Version);
        Assert.Equal(1, fixture.Channels.ListEnabledCalls);
    }

    [Fact]
    public async Task ForcedRefreshBypassesTheFingerprint()
    {
        fixture.Channels.Reset();
        fixture.Channels.LatestUpdatedAt = Start;
        fixture.Channels.Set(Channel(501, "first"));

        var grain = Grain("forced-bypass");
        _ = await grain.GetSnapshotAsync();

        fixture.Channels.Set(Channel(502, "forced"));

        var snapshot = await grain.RefreshAsync(force: true);

        // loadInternal swaps whenever force is set, even with changed=false.
        Assert.Equal(2, snapshot.Version);
        Assert.Equal(502, Assert.Single(snapshot.Channels).Id);
        Assert.Equal(2, fixture.Channels.ListEnabledCalls);
    }

    [Fact]
    public async Task ForcedRefreshOnAnEmptyTableStillBumpsTheVersion()
    {
        fixture.Channels.Reset();
        fixture.Channels.LatestUpdatedAt = null;
        fixture.Channels.Set();

        var grain = Grain("forced-empty");
        _ = await grain.GetSnapshotAsync();

        var snapshot = await grain.RefreshAsync(force: true);

        // The reference still swaps (and runs OnSwap) in this case, so
        // cacheVersion advances even though the contents stay empty.
        Assert.Equal(2, snapshot.Version);
        Assert.Empty(snapshot.Channels);
        Assert.Null(snapshot.LastUpdatedAt);
        Assert.Equal(2, fixture.Channels.ListEnabledCalls);
    }

    [Fact]
    public async Task FailedReloadKeepsTheLastGoodSnapshot()
    {
        fixture.Channels.Reset();
        fixture.Channels.LatestUpdatedAt = Start;
        fixture.Channels.Set(Channel(601, "serving"));

        var grain = Grain("failure-keeps-last");
        _ = await grain.GetSnapshotAsync();

        fixture.Channels.LatestUpdatedAt = Start.AddMinutes(1);
        fixture.Channels.Set(Channel(602, "never-served"));
        fixture.Channels.ListEnabledFailure = new InvalidOperationException("database is down");

        // Load returns the error to its caller; the swap never happens.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => grain.RefreshAsync(force: false));

        var snapshot = await grain.GetSnapshotAsync();

        Assert.Equal(1, snapshot.Version);
        Assert.Equal(601, Assert.Single(snapshot.Channels).Id);
        Assert.Equal(Start, snapshot.LastUpdatedAt);
    }

    [Fact]
    public async Task ColdLoadFailureSurfacesAndTheNextReadRetries()
    {
        fixture.Channels.Reset();
        fixture.Channels.LatestUpdatedAtFailure = new InvalidOperationException("database is down");

        var grain = Grain("cold-load-retry");

        // The reference panics in NewChannelService; the port's first caller
        // gets the error and the activation is still clean for a retry (C226).
        await Assert.ThrowsAsync<InvalidOperationException>(() => grain.GetSnapshotAsync());

        fixture.Channels.LatestUpdatedAtFailure = null;
        fixture.Channels.LatestUpdatedAt = Start;
        fixture.Channels.Set(Channel(701, "recovered"));

        var snapshot = await grain.GetSnapshotAsync();

        Assert.Equal(1, snapshot.Version);
        Assert.Equal(701, Assert.Single(snapshot.Channels).Id);
    }

    [Fact]
    public async Task TriggeredRefreshIsDebouncedIntoOneForcedReload()
    {
        fixture.Channels.Reset();
        fixture.Channels.LatestUpdatedAt = Start;
        fixture.Channels.Set(Channel(801, "before"));

        var grain = Grain("debounce");
        _ = await grain.GetSnapshotAsync();

        // Same fingerprint: only the pending force flag can make the reload
        // read the store again.
        fixture.Channels.Set(Channel(802, "after"));

        await grain.RequestRefreshAsync();
        await grain.RequestRefreshAsync();

        var snapshot = await WaitForVersionAsync(grain, 2);

        Assert.Equal(2, snapshot.Version);
        Assert.Equal(802, Assert.Single(snapshot.Channels).Id);
        Assert.Equal(2, fixture.Channels.ListEnabledCalls);
    }

    [Fact]
    public async Task FingerprintTimesAreNormalizedToUtc()
    {
        fixture.Channels.Reset();
        fixture.Channels.LatestUpdatedAt = DateTime.SpecifyKind(Start, DateTimeKind.Unspecified);
        fixture.Channels.Set(Channel(901, "utc"));

        var snapshot = await Grain("utc-normalize").GetSnapshotAsync();

        Assert.NotNull(snapshot.LastUpdatedAt);
        Assert.Equal(DateTimeKind.Utc, snapshot.LastUpdatedAt.Value.Kind);
        Assert.Equal(Start, snapshot.LastUpdatedAt.Value);
    }

    private IEnabledChannelsGrain Grain(string key) =>
        fixture.Cluster.GrainFactory.GetGrain<IEnabledChannelsGrain>("test:" + key);

    /// <summary>
    /// The refresh timer fires 500ms after the last trigger; polling absorbs
    /// the scheduling jitter without pinning the wait to one exact delay.
    /// </summary>
    private static async Task<EnabledChannelsSnapshot> WaitForVersionAsync(
        IEnabledChannelsGrain grain,
        long version)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var snapshot = await grain.GetSnapshotAsync();
            if (snapshot.Version >= version || DateTime.UtcNow >= deadline)
            {
                return snapshot;
            }

            await Task.Delay(25);
        }
    }

    private static ChannelDescriptor Channel(long id, string name) => new()
    {
        Id = id,
        Name = name,
        OrderingWeight = id,
        CreatedAt = Start,
        Type = "openai",
        BaseUrl = "https://api.example.com/v1",
        Credentials = """{"api_key":"sk-test"}""",
        SupportedModels = """["gpt-4o"]""",
        DefaultTestModel = "gpt-4o",
    };
}

/// <summary>
/// The descriptor is an in-process type without Orleans attributes, so the
/// grain carries this twin; the API-side provider maps it straight back, and
/// every field the selector reads must survive the hop.
/// </summary>
public sealed class EnabledChannelSnapshotTests
{
    [Fact]
    public void DescriptorRoundTripsEveryField()
    {
        var descriptor = new ChannelDescriptor
        {
            Id = 42,
            Name = "round-trip",
            OrderingWeight = 7,
            CreatedAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc),
            Type = "anthropic",
            BaseUrl = "https://api.example.com",
            Credentials = """{"api_key":"sk-a","oauth_token":"t"}""",
            DisabledApiKeys = """[{"key":"sk-a","disabled_at":1}]""",
            SupportedModels = """["claude-sonnet-5","claude-haiku-5"]""",
            Settings = """{"timeout":30,"stream":true}""",
            Endpoints = """[{"name":"chat"}]""",
            DefaultTestModel = "claude-sonnet-5",
            Tags = """["prod","us-east"]""",
            Policies = """{"stream":"require"}""",
        };

        var restored = EnabledChannelSnapshot.FromDescriptor(descriptor).ToDescriptor();

        Assert.Equal(descriptor, restored);
    }
}
