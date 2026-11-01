using AxonHub.Abstractions;
using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Locks the pool semantics against the reference load balancer tests
/// (internal/server/orchestrator/lb_strategy_*_test.go, load_balancer_test.go and
/// lb_simulation_adaptive_test.go): round-robin scoring and its health partition,
/// the adaptive/failover/circuit-breaker chains, the weight tie-breaker and the
/// top-K normalization.
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

        var result = await Pool("basic-model").SelectAsync(
            Now, trackSelection: false, topK: 4, LoadBalancerStrategies.RoundRobin);

        Assert.Equal(BasicModelOrder, result.Select(candidate => candidate.ChannelId).ToArray());
        // RoundRobin(0) + neutral RateLimitAware(100).
        Assert.Equal(250.0, result[0].Score);
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
            var result = await pool.SelectAsync(
                Now, trackSelection: true, topK: 4, LoadBalancerStrategies.RoundRobin);
            picks.Add(result[0].ChannelId);
        }

        Assert.Equal(10, picks.Count(id => id == 201));
        Assert.Equal(10, picks.Count(id => id == 202));
    }

    [Fact]
    public async Task SortWithoutTrackingDoesNotCountSelection()
    {
        fixture.Channels.Set(NewChannel(1201, "only", """["no-track-model"]"""));

        var result = await Pool("no-track-model").SelectAsync(
            Now, trackSelection: false, topK: 4, LoadBalancerStrategies.RoundRobin);

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

        var result = await Pool("health-model").SelectAsync(
            Now, trackSelection: false, topK: 4, LoadBalancerStrategies.RoundRobin);

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

        var result = await Pool("topk-model").SelectAsync(
            Now, trackSelection: false, topK: 4, LoadBalancerStrategies.RoundRobin);

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

        Assert.Single(await pool.SelectAsync(
            Now, trackSelection: false, topK: 1, LoadBalancerStrategies.RoundRobin));
        Assert.Equal(2, (await pool.SelectAsync(
            Now, trackSelection: false, topK: 2, LoadBalancerStrategies.RoundRobin)).Length);
        Assert.Equal(3, (await pool.SelectAsync(
            Now, trackSelection: false, topK: 3, LoadBalancerStrategies.RoundRobin)).Length);
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
        Assert.Equal(3, (await pool.SelectAsync(
            Now, trackSelection: false, topK: 0, LoadBalancerStrategies.RoundRobin)).Length);
        Assert.Equal(3, (await pool.SelectAsync(
            Now, trackSelection: false, topK: -2, LoadBalancerStrategies.RoundRobin)).Length);
        Assert.Equal(3, (await pool.SelectAsync(
            Now, trackSelection: false, topK: 10, LoadBalancerStrategies.RoundRobin)).Length);
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

        var result = await Pool("cooldown-model").SelectAsync(
            Now, trackSelection: false, topK: 4, LoadBalancerStrategies.RoundRobin);

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

        var result = await Pool("cooldown-boundary-model").SelectAsync(
            Now, trackSelection: false, topK: 4, LoadBalancerStrategies.RoundRobin);

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

        var prefixed = Assert.Single(await Pool("azure/prefix-upstream").SelectAsync(
            Now, false, topK: 4, LoadBalancerStrategies.RoundRobin));
        Assert.Equal(801L, prefixed.ChannelId);
        Assert.Equal("prefix-upstream", prefixed.ActualModel);

        var trimmed = Assert.Single(await Pool("trim-upstream").SelectAsync(
            Now, false, topK: 4, LoadBalancerStrategies.RoundRobin));
        Assert.Equal(802L, trimmed.ChannelId);
        Assert.Equal("vendor/trim-upstream", trimmed.ActualModel);

        var mapped = Assert.Single(await Pool("mapped-alias").SelectAsync(
            Now, false, topK: 4, LoadBalancerStrategies.RoundRobin));
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

        Assert.Empty(await Pool("hidden-target").SelectAsync(
            Now, false, topK: 4, LoadBalancerStrategies.RoundRobin));

        var alias = Assert.Single(await Pool("hidden-alias").SelectAsync(
            Now, false, topK: 4, LoadBalancerStrategies.RoundRobin));
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

        var candidate = Assert.Single(await Pool("lower-model").SelectAsync(
            Now, false, topK: 4, LoadBalancerStrategies.RoundRobin));

        Assert.Equal("LOWER-MODEL", candidate.ActualModel);
    }

    [Fact]
    public async Task EqualScoresKeepProviderOrder()
    {
        fixture.Channels.Set(
            NewChannel(1101, "first", """["tie-model"]""", weight: 100),
            NewChannel(1102, "second", """["tie-model"]""", weight: 50),
            NewChannel(1103, "third", """["tie-model"]""", weight: 10));

        var result = await Pool("tie-model").SelectAsync(
            Now, trackSelection: false, topK: 4, LoadBalancerStrategies.RoundRobin);

        // Round-robin disables the OrderingWeight tie-breaker and keeps input order.
        Assert.Equal(TieModelOrder, result.Select(candidate => candidate.ChannelId).ToArray());
    }

    [Fact]
    public async Task AdaptiveEqualScoresUseOrderingWeightAsTheTieBreaker()
    {
        fixture.Channels.Set(
            NewChannel(2101, "light", """["adaptive-tie-model"]""", weight: 10),
            NewChannel(2102, "heavy", """["adaptive-tie-model"]""", weight: 100),
            NewChannel(2103, "medium", """["adaptive-tie-model"]""", weight: 50));

        var result = await Pool("adaptive-tie-model").SelectAsync(
            Now, trackSelection: false, topK: 4, LoadBalancerStrategies.Adaptive);

        Assert.Equal(
            [2102L, 2103L, 2101L],
            result.Select(candidate => candidate.ChannelId).ToArray());
    }

    [Fact]
    public async Task UnknownStrategyFallsBackToAdaptive()
    {
        fixture.Channels.Set(
            NewChannel(2111, "light", """["adaptive-fallback-model"]""", weight: 10),
            NewChannel(2112, "heavy", """["adaptive-fallback-model"]""", weight: 100));

        var result = await Pool("adaptive-fallback-model").SelectAsync(
            Now, trackSelection: false, topK: 4, "weighted");

        // resolveLoadBalancer picks the adaptive balancer for unknown names, whose
        // weight tie-breaker outranks the input order.
        Assert.Equal([2112L, 2111L], result.Select(candidate => candidate.ChannelId).ToArray());
        Assert.Equal(490.0, result[0].Score);
    }

    [Fact]
    public async Task AdaptiveScoreSumsTheNeutralStrategies()
    {
        fixture.Channels.Set(NewChannel(2201, "idle", """["adaptive-neutral-model"]"""));

        var candidate = Assert.Single(await Pool("adaptive-neutral-model").SelectAsync(
            Now, false, topK: 4, LoadBalancerStrategies.Adaptive));

        // ErrorAware 200 + WeightRoundRobin(0) 150 + LatencyAware neutral 40 +
        // RateLimitAware neutral 100.
        Assert.Equal(490.0, candidate.Score);
    }

    [Fact]
    public async Task AdaptivePenalizesRecentFailuresWithLinearDecay()
    {
        fixture.Channels.Set(
            NewChannel(2211, "failing", """["adaptive-decay-model"]"""),
            NewChannel(2212, "healthy", """["adaptive-decay-model"]"""));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Channel(2211).RecordFailureAsync(Now.AddMinutes(-4));
        }

        var result = await Pool("adaptive-decay-model").SelectAsync(
            Now, false, topK: 4, LoadBalancerStrategies.Adaptive);

        // cooldownRatio = 1 - 4/5 = 0.2; consecutive penalty 3*30*0.2 = 18 and
        // base penalty 40*0.2 = 8: ErrorAware = 174, plus 150 + 40 + 100.
        Assert.Equal(2212L, result[0].ChannelId);
        Assert.Equal(464.0, result.Single(c => c.ChannelId == 2211).Score, 9);
    }

    [Fact]
    public async Task AdaptiveForgetsFailuresAfterTheCooldown()
    {
        fixture.Channels.Set(
            NewChannel(2221, "recovered", """["adaptive-cooldown-model"]"""),
            NewChannel(2222, "healthy", """["adaptive-cooldown-model"]"""));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Channel(2221).RecordFailureAsync(Now.AddMinutes(-5));
        }

        var result = await Pool("adaptive-cooldown-model").SelectAsync(
            Now, false, topK: 4, LoadBalancerStrategies.Adaptive);

        // Exactly five minutes: the decay window is exclusive, so both channels
        // score 490 and the equal weight applies (input order keeps 2221 first).
        Assert.Equal([2221L, 2222L], result.Select(candidate => candidate.ChannelId).ToArray());
        Assert.All(result, candidate => Assert.Equal(490.0, candidate.Score));
    }

    [Fact]
    public async Task FailoverRanksByOrderingWeight()
    {
        fixture.Channels.Set(
            NewChannel(2301, "low", """["failover-model"]""", weight: 10),
            NewChannel(2302, "high", """["failover-model"]""", weight: 100),
            NewChannel(2303, "zero", """["failover-model"]""", weight: 0));

        var result = await Pool("failover-model").SelectAsync(
            Now, false, topK: 4, LoadBalancerStrategies.Failover);

        Assert.Equal(
            [2302L, 2301L, 2303L],
            result.Select(candidate => candidate.ChannelId).ToArray());

        // Weight + Random(0-0.5) + neutral RateLimitAware.
        Assert.InRange(result[0].Score, 200.0, 200.5);
    }

    [Fact]
    public async Task CircuitBreakerRanksClosedChannelsByWeight()
    {
        fixture.Channels.Set(
            NewChannel(2401, "low", """["breaker-model"]""", weight: 10),
            NewChannel(2402, "high", """["breaker-model"]""", weight: 100),
            NewChannel(2403, "zero", """["breaker-model"]""", weight: 0));

        var result = await Pool("breaker-model").SelectAsync(
            Now, false, topK: 4, LoadBalancerStrategies.CircuitBreaker);

        Assert.Equal(
            [2402L, 2401L, 2403L],
            result.Select(candidate => candidate.ChannelId).ToArray());

        // Weight + closed breaker 200 + Random(0-0.5) + neutral RateLimitAware.
        Assert.InRange(result[0].Score, 400.0, 400.5);
    }

    [Fact]
    public async Task AdaptiveAndRoundRobinRankTheSameStateDifferently()
    {
        fixture.Channels.Set(
            NewChannel(2501, "failing", """["adaptive-health-model"]"""),
            NewChannel(2502, "busy", """["adaptive-health-model"]"""));

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Channel(2501).RecordFailureAsync(Now.AddMinutes(-1));
        }

        // 150/(1+400/150) = 40.9, so the adaptive total of the busy channel is
        // 200 + 40.9 + 40 + 100 = 380.9 against 96 + 150 + 40 + 100 = 386 for
        // the failing one: ErrorAware still ranks it first.
        for (var selection = 0; selection < 400; selection++)
        {
            await Channel(2502).RecordSelectionAsync(Now.AddMinutes(-1));
        }

        var adaptive = await Pool("adaptive-health-model").SelectAsync(
            Now, false, topK: 4, LoadBalancerStrategies.Adaptive);
        var roundRobin = await Pool("adaptive-health-model").SelectAsync(
            Now, false, topK: 4, LoadBalancerStrategies.RoundRobin);

        Assert.Equal(2501L, adaptive[0].ChannelId);

        // Round-robin scores the failing channel higher (zero requests) but its
        // health partition pushes it behind the busy healthy channel.
        Assert.Equal(2502L, roundRobin[0].ChannelId);
    }

    [Theory]
    [InlineData(
        "select-a",
        """["gpt-6-sol","select-a"]""",
        " main ",
        """{"modelMappings":[{"from":"main","to":"gpt-6-sol"}]}""",
        "gpt-6-sol")]
    [InlineData(
        "select-b",
        """["gpt-6-sol","select-b"]""",
        "MAIN",
        """{"lowercaseModelId":true,"modelMappings":[{"from":"main","to":"gpt-6-sol"}]}""",
        "gpt-6-sol")]
    [InlineData("select-c", """["gpt-6-sol","select-c"]""", "  gpt-6-sol  ", null, "gpt-6-sol")]
    [InlineData("select-d", """["gpt-6-sol","select-d"]""", "unknown-model", null, "unknown-model")]
    public async Task DefaultTestModelIsTrimmedAndResolvedThroughModelEntries(
        string model,
        string supportedModels,
        string defaultTestModel,
        string? settings,
        string want)
    {
        // Port of TestCodexChannel_ImageMainModelUsesDefaultTestModel's model
        // resolution: buildCodexOutbound trims default_test_model and maps it
        // through the channel's model entries (ChooseModel), keeping the trimmed
        // value when the channel does not support it.
        fixture.Channels.Set(new ChannelDescriptor
        {
            Id = 2601,
            Name = "codex",
            OrderingWeight = 100,
            Type = "codex",
            SupportedModels = supportedModels,
            DefaultTestModel = defaultTestModel,
            Settings = settings,
        });

        var result = await Pool(model).SelectAsync(
            Now, trackSelection: false, topK: 4, LoadBalancerStrategies.RoundRobin);

        Assert.Single(result);
        Assert.Equal(want, result[0].DefaultTestModel);
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
