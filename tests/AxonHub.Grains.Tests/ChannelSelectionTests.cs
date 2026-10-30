using AxonHub.Abstractions;
using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Locks the round-robin pool semantics against the reference tests in
/// internal/server/orchestrator/lb_strategy_rr_test.go and load_balancer_test.go.
/// </summary>
public sealed class ChannelSelectionTests(GrainClusterFixture fixture) : IClassFixture<GrainClusterFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly long[] BasicModelOrder = [101L, 103L];
    private static readonly long[] HealthModelOrder = [302L, 301L];
    private static readonly long[] TieModelOrder = [1101L, 1102L, 1103L];

    [Fact]
    public async Task SelectsOnlyChannelsSupportingTheModel()
    {
        fixture.Channels.Set(
            NewChannel(101, "supports", """["basic-model"]"""),
            NewChannel(102, "other", """["other-model"]"""),
            NewChannel(103, "also-supports", """["basic-model"]""", weight: 50));

        var result = await Pool("basic-model").SelectAsync(Now, trackSelection: false, topK: 4);

        Assert.Equal(BasicModelOrder, result.Select(candidate => candidate.ChannelId).ToArray());
        Assert.Equal(150.0, result[0].Score);
        Assert.Equal("basic-model", result[0].ActualModel);
    }

    [Fact]
    public async Task SelectionTrackingSpreadsLoadEvenly()
    {
        fixture.Channels.Set(
            NewChannel(201, "a", """["rr-model"]"""),
            NewChannel(202, "b", """["rr-model"]"""));

        var pool = Pool("rr-model");
        var picks = new List<long>();
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var result = await pool.SelectAsync(Now, trackSelection: true, topK: 4);
            picks.Add(result[0].ChannelId);
        }

        Assert.Equal(10, picks.Count(id => id == 201));
        Assert.Equal(10, picks.Count(id => id == 202));
    }

    [Fact]
    public async Task SortWithoutTrackingDoesNotCountSelection()
    {
        fixture.Channels.Set(NewChannel(1201, "only", """["no-track-model"]"""));

        var result = await Pool("no-track-model").SelectAsync(Now, trackSelection: false, topK: 4);

        Assert.Single(result);
        Assert.Equal(0, (await Channel(1201).GetMetricsAsync(Now)).RequestCount);
    }

    [Fact]
    public async Task RecentlyFailingChannelMovesBehindHealthyChannels()
    {
        fixture.Channels.Set(
            NewChannel(301, "unhealthy", """["health-model"]"""),
            NewChannel(302, "busy", """["health-model"]"""));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Channel(301).RecordFailureAsync(Now.AddMinutes(-1));
        }

        await Channel(302).RecordSelectionAsync(Now.AddMinutes(-1));

        var result = await Pool("health-model").SelectAsync(Now, trackSelection: false, topK: 4);

        // The unhealthy channel has the higher raw score (zero requests), so only
        // the health partition can put it last.
        Assert.Equal(HealthModelOrder, result.Select(candidate => candidate.ChannelId).ToArray());
        Assert.True(result[0].Score < result[1].Score);
    }

    [Fact]
    public async Task UnhealthyChannelsAreExcludedFromTopK()
    {
        fixture.Channels.Set(
            NewChannel(401, "unhealthy", """["topk-model"]"""),
            NewChannel(402, "free", """["topk-model"]"""),
            NewChannel(403, "light", """["topk-model"]"""),
            NewChannel(404, "medium", """["topk-model"]"""),
            NewChannel(405, "heavy", """["topk-model"]"""));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Channel(401).RecordFailureAsync(Now.AddMinutes(-1));
        }

        await Channel(403).RecordSelectionAsync(Now.AddSeconds(-30));
        await Channel(404).RecordSelectionAsync(Now.AddSeconds(-30));
        await Channel(404).RecordSelectionAsync(Now.AddSeconds(-30));
        await Channel(405).RecordSelectionAsync(Now.AddSeconds(-30));
        await Channel(405).RecordSelectionAsync(Now.AddSeconds(-30));
        await Channel(405).RecordSelectionAsync(Now.AddSeconds(-30));

        var result = await Pool("topk-model").SelectAsync(Now, trackSelection: false, topK: 4);

        // topK = 1 + default MaxChannelRetries (3); the unhealthy channel ranks
        // first by score but is pushed behind all healthy candidates.
        Assert.Equal(4, result.Length);
        Assert.DoesNotContain(401L, result.Select(candidate => candidate.ChannelId));
        Assert.Equal(402L, result[0].ChannelId);
    }

    [Fact]
    public async Task TopKLimitsCandidatesToThePolicyBudget()
    {
        fixture.Channels.Set(
            NewChannel(1301, "a", """["topk-limit-model"]"""),
            NewChannel(1302, "b", """["topk-limit-model"]"""),
            NewChannel(1303, "c", """["topk-limit-model"]"""));

        var pool = Pool("topk-limit-model");

        Assert.Single(await pool.SelectAsync(Now, trackSelection: false, topK: 1));
        Assert.Equal(2, (await pool.SelectAsync(Now, trackSelection: false, topK: 2)).Length);
        Assert.Equal(3, (await pool.SelectAsync(Now, trackSelection: false, topK: 3)).Length);
    }

    [Fact]
    public async Task NonPositiveOrOversizedTopKKeepsEveryCandidate()
    {
        fixture.Channels.Set(
            NewChannel(1311, "a", """["topk-clamp-model"]"""),
            NewChannel(1312, "b", """["topk-clamp-model"]"""),
            NewChannel(1313, "c", """["topk-clamp-model"]"""));

        var pool = Pool("topk-clamp-model");

        // calculateTopK: topK <= 0 or topK >= len(candidates) sorts all.
        Assert.Equal(3, (await pool.SelectAsync(Now, trackSelection: false, topK: 0)).Length);
        Assert.Equal(3, (await pool.SelectAsync(Now, trackSelection: false, topK: -2)).Length);
        Assert.Equal(3, (await pool.SelectAsync(Now, trackSelection: false, topK: 10)).Length);
    }

    [Fact]
    public async Task FailureCooldownExpiresAfterFiveMinutes()
    {
        fixture.Channels.Set(
            NewChannel(501, "recovered", """["cooldown-model"]"""),
            NewChannel(502, "busy", """["cooldown-model"]"""));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Channel(501).RecordFailureAsync(Now.AddMinutes(-6));
        }

        await Channel(502).RecordSelectionAsync(Now.AddMinutes(-1));

        var result = await Pool("cooldown-model").SelectAsync(Now, trackSelection: false, topK: 4);

        Assert.Equal(501L, result[0].ChannelId);
    }

    [Fact]
    public async Task FailureCooldownBoundaryIsExclusive()
    {
        fixture.Channels.Set(
            NewChannel(511, "boundary", """["cooldown-boundary-model"]"""),
            NewChannel(512, "busy", """["cooldown-boundary-model"]"""));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Channel(511).RecordFailureAsync(Now.AddMinutes(-5));
        }

        await Channel(512).RecordSelectionAsync(Now.AddMinutes(-1));

        var result = await Pool("cooldown-boundary-model").SelectAsync(Now, trackSelection: false, topK: 4);

        // Exactly five minutes elapsed: time.Since(lastFailure) < cooldown is false.
        Assert.Equal(511L, result[0].ChannelId);
    }

    [Fact]
    public async Task SuccessResetsConsecutiveFailures()
    {
        var channel = Channel(601);
        await channel.RecordFailureAsync(Now.AddMinutes(-3));
        await channel.RecordFailureAsync(Now.AddMinutes(-2));
        await channel.RecordFailureAsync(Now.AddMinutes(-1));

        Assert.Equal(3, (await channel.GetMetricsAsync(Now)).ConsecutiveFailures);

        await channel.RecordSuccessAsync(Now);

        var snapshot = await channel.GetMetricsAsync(Now);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Equal(1, snapshot.SuccessCount);

        // A success only clears the consecutive counter; failures already inside
        // the ten-minute window stay counted (channelMetrics.recordSuccess).
        Assert.Equal(3, snapshot.FailureCount);
    }

    [Fact]
    public async Task SelectionCountExpiresAfterTenMinutes()
    {
        var channel = Channel(701);
        await channel.RecordSelectionAsync(Now);

        Assert.Equal(1, (await channel.GetMetricsAsync(Now)).RequestCount);
        Assert.Equal(1, (await channel.GetMetricsAsync(Now.AddSeconds(600))).RequestCount);
        Assert.Equal(0, (await channel.GetMetricsAsync(Now.AddSeconds(601))).RequestCount);
    }

    [Fact]
    public async Task LateEventOlderThanExpiredWindowIsDropped()
    {
        var channel = Channel(702);
        await channel.RecordSelectionAsync(Now.AddSeconds(10));
        await channel.GetMetricsAsync(Now.AddSeconds(601));

        // The window cutoff has advanced past this instant; the late event must
        // not re-enter the window.
        await channel.RecordSelectionAsync(Now);

        Assert.Equal(1, (await channel.GetMetricsAsync(Now.AddSeconds(610))).RequestCount);
    }

    [Fact]
    public async Task PrefixAutoTrimAndMappingResolveLikeTheReference()
    {
        fixture.Channels.Set(
            NewChannel(801, "prefix", """["prefix-upstream"]""", settings: """{"extraModelPrefix":"azure"}"""),
            NewChannel(802, "trim", """["vendor/trim-upstream"]""", settings: """{"autoTrimedModelPrefixes":["vendor"]}"""),
            NewChannel(
                803,
                "mapping",
                """["mapped-upstream"]""",
                settings: """{"modelMappings":[{"from":"mapped-alias","to":"mapped-upstream"}]}"""));

        var prefixed = Assert.Single(await Pool("azure/prefix-upstream").SelectAsync(Now, false, topK: 4));
        Assert.Equal(801L, prefixed.ChannelId);
        Assert.Equal("prefix-upstream", prefixed.ActualModel);

        var trimmed = Assert.Single(await Pool("trim-upstream").SelectAsync(Now, false, topK: 4));
        Assert.Equal(802L, trimmed.ChannelId);
        Assert.Equal("vendor/trim-upstream", trimmed.ActualModel);

        var mapped = Assert.Single(await Pool("mapped-alias").SelectAsync(Now, false, topK: 4));
        Assert.Equal(803L, mapped.ChannelId);
        Assert.Equal("mapped-upstream", mapped.ActualModel);
    }

    [Fact]
    public async Task HideOriginalAndMappedModelsRemovesDirectAliases()
    {
        fixture.Channels.Set(NewChannel(
            901,
            "hidden",
            """["hidden-target"]""",
            settings: """{"modelMappings":[{"from":"hidden-alias","to":"hidden-target"}],"hideOriginalModels":true,"hideMappedModels":true}"""));

        Assert.Empty(await Pool("hidden-target").SelectAsync(Now, false, topK: 4));

        var alias = Assert.Single(await Pool("hidden-alias").SelectAsync(Now, false, topK: 4));
        Assert.Equal("hidden-target", alias.ActualModel);
    }

    [Fact]
    public async Task LowercaseModelIdMatchesCaseInsensitively()
    {
        fixture.Channels.Set(NewChannel(
            1001,
            "lower",
            """["LOWER-MODEL"]""",
            settings: """{"lowercaseModelId":true}"""));

        var candidate = Assert.Single(await Pool("lower-model").SelectAsync(Now, false, topK: 4));

        Assert.Equal("LOWER-MODEL", candidate.ActualModel);
    }

    [Fact]
    public async Task EqualScoresKeepProviderOrder()
    {
        fixture.Channels.Set(
            NewChannel(1101, "first", """["tie-model"]""", weight: 100),
            NewChannel(1102, "second", """["tie-model"]""", weight: 50),
            NewChannel(1103, "third", """["tie-model"]""", weight: 10));

        var result = await Pool("tie-model").SelectAsync(Now, trackSelection: false, topK: 4);

        Assert.Equal(TieModelOrder, result.Select(candidate => candidate.ChannelId).ToArray());
    }

    private static ChannelDescriptor NewChannel(
        long id,
        string name,
        string supportedModels,
        long weight = 100,
        string? settings = null) => new()
        {
            Id = id,
            Name = name,
            OrderingWeight = weight,
            SupportedModels = supportedModels,
            Settings = settings,
        };

    private IChannelGrain Channel(long id) => fixture.Cluster.GrainFactory.GetGrain<IChannelGrain>(id);

    private IChannelPoolGrain Pool(string model) => fixture.Cluster.GrainFactory.GetGrain<IChannelPoolGrain>(model);
}
