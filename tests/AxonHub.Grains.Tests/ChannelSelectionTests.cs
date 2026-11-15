using AxonHub.Abstractions;
using AxonHub.Grains;
using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Locks the pool semantics against the reference load balancer tests
/// (internal/server/orchestrator/lb_strategy_*_test.go, load_balancer_test.go and
/// lb_simulation_adaptive_test.go): round-robin scoring and its health partition,
/// the adaptive/failover/circuit-breaker chains, the weight tie-breaker, the
/// priority grouping and the required-count budget.
/// </summary>
/// <remarks>
/// The reference tests the strategies directly and sortCandidates through its
/// load balancer; the port keeps the scoring math in
/// <see cref="LoadBalancerScoring"/> so the same values can be asserted without
/// a silo, and drives <c>ChannelPoolGrain.SortAsync</c> for ordering.
/// </remarks>
[Collection(GrainClusterCollection.Name)]
public sealed class ChannelSelectionTests(GrainClusterFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly long[] HealthModelOrder = [302L, 301L];
    private static readonly long[] TieModelOrder = [1101L, 1102L, 1103L];

    private readonly RateLimitAwareScorer _rateLimitAware = new(new ChannelRequestTracker());

    [Fact]
    public async Task RoundRobinKeepsInputOrderOnEqualScores()
    {
        var result = await Sort(
            "basic-model",
            [Candidate(101), Candidate(103, weight: 50)],
            requiredCount: 4,
            LoadBalancerStrategies.RoundRobin);

        // RoundRobin(0) + neutral RateLimitAware(100) ties, and the round-robin
        // balancer disables the OrderingWeight tie-breaker.
        Assert.Equal([101L, 103L], result.Select(candidate => candidate.ChannelId).ToArray());
    }

    [Fact]
    public async Task SelectionTrackingSpreadsLoadEvenly()
    {
        var candidates = new[] { Candidate(201), Candidate(202) };
        var picks = new List<long>();
        for (var iteration = 0; iteration < 20; iteration++)
        {
            var result = await Sort(
                "rr-model",
                candidates,
                requiredCount: 4,
                LoadBalancerStrategies.RoundRobin,
                trackSelection: true);
            picks.Add(result[0].ChannelId);
        }

        Assert.Equal(10, picks.Count(id => id == 201));
        Assert.Equal(10, picks.Count(id => id == 202));
    }

    [Fact]
    public async Task SortWithoutTrackingDoesNotCountSelection()
    {
        var result = await Sort(
            "no-track-model",
            [Candidate(1201)],
            requiredCount: 4,
            LoadBalancerStrategies.RoundRobin);

        Assert.Single(result);
        Assert.Equal(0, (await Channel(1201).GetMetricsAsync(Now)).RequestCount);
    }

    [Fact]
    public async Task TrackingASingleCandidateCountsItOnce()
    {
        // sortCandidates tracks a lone candidate even though it bypasses the
        // balancer entirely (candidates.go:921-925).
        var result = await Sort(
            "single-track-model",
            [Candidate(1211)],
            requiredCount: 4,
            LoadBalancerStrategies.RoundRobin,
            trackSelection: true);

        Assert.Single(result);
        Assert.Equal(1, (await Channel(1211).GetMetricsAsync(Now)).RequestCount);
    }

    [Fact]
    public async Task TrackingCountsOnlyTheFinalTopCandidateAcrossPriorityGroups()
    {
        // candidates_sticky_test.go
        // "TestLoadBalancedSelector_TracksOnlyFinalTopCandidateAcrossPriorityGroups":
        // the sorted result keeps priority order, but only the first candidate
        // of the assembled result increments its selection counter.
        var result = await Sort(
            "priority-track-model",
            [Candidate(1221), Candidate(1222, priority: 1), Candidate(1223, priority: 1)],
            requiredCount: 3,
            LoadBalancerStrategies.RoundRobin,
            trackSelection: true);

        Assert.Equal([1221L, 1222L, 1223L], result.Select(candidate => candidate.ChannelId).ToArray());
        Assert.Equal(1, (await Channel(1221).GetMetricsAsync(Now)).RequestCount);
        Assert.Equal(0, (await Channel(1222).GetMetricsAsync(Now)).RequestCount);
        Assert.Equal(0, (await Channel(1223).GetMetricsAsync(Now)).RequestCount);
    }

    [Fact]
    public async Task RecentlyFailingChannelMovesBehindHealthyChannels()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Channel(301).RecordFailureAsync(Now.AddMinutes(-1));
        }

        await Channel(302).RecordSelectionAsync(Now.AddMinutes(-1));

        var result = await Sort(
            "health-model",
            [Candidate(301), Candidate(302)],
            requiredCount: 4,
            LoadBalancerStrategies.RoundRobin);

        // The unhealthy channel has the higher raw score (zero requests), so only
        // the health partition can put it last.
        Assert.Equal(HealthModelOrder, result.Select(candidate => candidate.ChannelId).ToArray());
    }

    [Fact]
    public async Task UnhealthyChannelsAreExcludedFromTheRequiredCount()
    {
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

        var result = await Sort(
            "topk-model",
            [Candidate(401), Candidate(402), Candidate(403), Candidate(404), Candidate(405)],
            requiredCount: 4,
            LoadBalancerStrategies.RoundRobin);

        // requiredCount = 1 + MaxChannelRetries (3); the unhealthy channel ranks
        // first by score but is pushed behind all healthy candidates.
        Assert.Equal(4, result.Length);
        Assert.DoesNotContain(401L, result.Select(candidate => candidate.ChannelId));
        Assert.Equal(402L, result[0].ChannelId);
    }

    [Fact]
    public async Task RequiredCountLimitsCandidates()
    {
        var candidates = new[] { Candidate(1301), Candidate(1302), Candidate(1303) };

        Assert.Single(await Sort(
            "topk-limit-model", candidates, requiredCount: 1, LoadBalancerStrategies.RoundRobin));
        Assert.Equal(2, (await Sort(
            "topk-limit-model", candidates, requiredCount: 2, LoadBalancerStrategies.RoundRobin)).Length);
        Assert.Equal(3, (await Sort(
            "topk-limit-model", candidates, requiredCount: 3, LoadBalancerStrategies.RoundRobin)).Length);
    }

    [Fact]
    public async Task NonPositiveRequiredCountReturnsNothing()
    {
        // sortCandidates: requiredCount <= 0 returns nil (candidates.go:917-919).
        var candidates = new[] { Candidate(1311), Candidate(1312), Candidate(1313) };

        Assert.Empty(await Sort(
            "topk-clamp-model", candidates, requiredCount: 0, LoadBalancerStrategies.RoundRobin));
        Assert.Empty(await Sort(
            "topk-clamp-model", candidates, requiredCount: -2, LoadBalancerStrategies.RoundRobin));
        Assert.Equal(3, (await Sort(
            "topk-clamp-model", candidates, requiredCount: 10, LoadBalancerStrategies.RoundRobin)).Length);
    }

    [Fact]
    public async Task PriorityGroupsAreEmptiedInAscendingOrder()
    {
        // Lower priority values are served first; the second group only fills the
        // remaining budget and never reorders the first group.
        var result = await Sort(
            "priority-model",
            [Candidate(1401, priority: 2), Candidate(1402, priority: 0), Candidate(1403, priority: 1)],
            requiredCount: 2,
            LoadBalancerStrategies.RoundRobin);

        Assert.Equal([1402L, 1403L], result.Select(candidate => candidate.ChannelId).ToArray());
    }

    [Fact]
    public async Task FailureCooldownExpiresAfterFiveMinutes()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Channel(501).RecordFailureAsync(Now.AddMinutes(-6));
        }

        await Channel(502).RecordSelectionAsync(Now.AddMinutes(-1));

        var result = await Sort(
            "cooldown-model",
            [Candidate(501), Candidate(502)],
            requiredCount: 4,
            LoadBalancerStrategies.RoundRobin);

        Assert.Equal(501L, result[0].ChannelId);
    }

    [Fact]
    public async Task FailureCooldownBoundaryIsExclusive()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Channel(511).RecordFailureAsync(Now.AddMinutes(-5));
        }

        await Channel(512).RecordSelectionAsync(Now.AddMinutes(-1));

        var result = await Sort(
            "cooldown-boundary-model",
            [Candidate(511), Candidate(512)],
            requiredCount: 4,
            LoadBalancerStrategies.RoundRobin);

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
    public async Task EqualScoresKeepProviderOrder()
    {
        var result = await Sort(
            "tie-model",
            [Candidate(1101, weight: 100), Candidate(1102, weight: 50), Candidate(1103, weight: 10)],
            requiredCount: 4,
            LoadBalancerStrategies.RoundRobin);

        // Round-robin disables the OrderingWeight tie-breaker and keeps input order.
        Assert.Equal(TieModelOrder, result.Select(candidate => candidate.ChannelId).ToArray());
    }

    [Fact]
    public async Task AdaptiveEqualScoresUseOrderingWeightAsTheTieBreaker()
    {
        var result = await Sort(
            "adaptive-tie-model",
            [Candidate(2101, weight: 10), Candidate(2102, weight: 100), Candidate(2103, weight: 50)],
            requiredCount: 4,
            LoadBalancerStrategies.Adaptive);

        Assert.Equal(
            [2102L, 2103L, 2101L],
            result.Select(candidate => candidate.ChannelId).ToArray());
    }

    [Fact]
    public async Task UnknownStrategyFallsBackToAdaptive()
    {
        var result = await Sort(
            "adaptive-fallback-model",
            [Candidate(2111, weight: 10), Candidate(2112, weight: 100)],
            requiredCount: 4,
            "weighted");

        // resolveLoadBalancer picks the adaptive balancer for unknown names, whose
        // weight tie-breaker outranks the input order.
        Assert.Equal([2112L, 2111L], result.Select(candidate => candidate.ChannelId).ToArray());
    }

    [Fact]
    public async Task FailoverRanksByOrderingWeight()
    {
        var result = await Sort(
            "failover-model",
            [Candidate(2301, weight: 10), Candidate(2302, weight: 100), Candidate(2303, weight: 0)],
            requiredCount: 4,
            LoadBalancerStrategies.Failover);

        Assert.Equal(
            [2302L, 2301L, 2303L],
            result.Select(candidate => candidate.ChannelId).ToArray());
    }

    [Fact]
    public async Task CircuitBreakerRanksClosedChannelsByWeight()
    {
        var result = await Sort(
            "breaker-model",
            [Candidate(2401, weight: 10), Candidate(2402, weight: 100), Candidate(2403, weight: 0)],
            requiredCount: 4,
            LoadBalancerStrategies.CircuitBreaker);

        Assert.Equal(
            [2402L, 2401L, 2403L],
            result.Select(candidate => candidate.ChannelId).ToArray());
    }

    [Fact]
    public void CircuitBreakerScoreUsesTheModelState()
    {
        var breaker = new ModelCircuitBreaker(new FixedTimeProvider(Now));
        var candidate = Candidate(2501, weight: 100);

        var closed = ScoreWithBreaker(breaker, candidate);
        Assert.InRange(closed, 400.0, 400.5);

        for (var i = 0; i < ModelCircuitBreaker.HalfOpenThreshold; i++)
        {
            breaker.RecordError(candidate.ChannelId, "m");
        }

        var halfOpen = ScoreWithBreaker(breaker, candidate);
        Assert.InRange(halfOpen, 260.0, 260.5);

        for (var i = ModelCircuitBreaker.HalfOpenThreshold;
             i < ModelCircuitBreaker.OpenThreshold;
             i++)
        {
            breaker.RecordError(candidate.ChannelId, "m");
        }

        Assert.Equal(200.0, ScoreWithBreaker(breaker, candidate));
    }

    [Fact]
    public void LatencyScoreUsesTheMatchingSampleBucket()
    {
        // No matching samples keep the reference's neutral 40.
        Assert.Equal(40, LoadBalancerScoring.LatencyScore(NeutralMetrics(), stream: true), 3);
        Assert.Equal(40, LoadBalancerScoring.LatencyScore(NeutralMetrics(), stream: false), 3);

        // Streaming: 80 * (0.7 * 1 + 0.3 * 0) = 56 for an instant first token
        // with the minimum TPS.
        var streaming = new ChannelMetricsSnapshot
        {
            StreamingSampleCount = 1,
            StreamingFirstTokenLatencyEwmaMs = 0,
            StreamingTokensPerSecondEwma = 5,
        };
        Assert.Equal(56, LoadBalancerScoring.LatencyScore(streaming, stream: true), 3);

        // Slow first token, maximum TPS: 80 * (0 + 0.3) = 24.
        var slowStream = new ChannelMetricsSnapshot
        {
            StreamingSampleCount = 1,
            StreamingFirstTokenLatencyEwmaMs = 3000,
            StreamingTokensPerSecondEwma = 100,
        };
        Assert.Equal(24, LoadBalancerScoring.LatencyScore(slowStream, stream: true), 3);

        // Non-streaming: half the 3s budget scores 40.
        var nonStreaming = new ChannelMetricsSnapshot
        {
            NonStreamingSampleCount = 1,
            NonStreamingLatencyEwmaMs = 1500,
        };
        Assert.Equal(40, LoadBalancerScoring.LatencyScore(nonStreaming, stream: false), 3);
    }

    private double ScoreWithBreaker(
        ModelCircuitBreaker breaker,
        ChannelCandidate candidate)
        => LoadBalancerScoring.StrategyScore(
            LoadBalancerStrategies.CircuitBreaker,
            candidate,
            NeutralMetrics(),
            Now,
            _rateLimitAware,
            breaker,
            "m");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task AdaptiveAndRoundRobinRankTheSameStateDifferently()
    {
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

        var candidates = new[] { Candidate(2501), Candidate(2502) };
        var adaptive = await Sort(
            "adaptive-health-model", candidates, requiredCount: 4, LoadBalancerStrategies.Adaptive);
        var roundRobin = await Sort(
            "adaptive-health-model", candidates, requiredCount: 4, LoadBalancerStrategies.RoundRobin);

        Assert.Equal(2501L, adaptive[0].ChannelId);

        // Round-robin scores the failing channel higher (zero requests) but its
        // health partition pushes it behind the busy healthy channel.
        Assert.Equal(2502L, roundRobin[0].ChannelId);
    }

    // -- Strategy scoring ----------------------------------------------------

    [Fact]
    public void AdaptiveScoreSumsTheNeutralStrategies()
    {
        // ErrorAware 200 + WeightRoundRobin(0) 150 + LatencyAware neutral 40 +
        // RateLimitAware neutral 100.
        Assert.Equal(490.0, Score(LoadBalancerStrategies.Adaptive, Candidate(2201), NeutralMetrics()));
    }

    [Fact]
    public void AdaptivePenalizesRecentFailuresWithLinearDecay()
    {
        var metrics = FailureMetrics(consecutiveFailures: 3, lastFailureAt: Now.AddMinutes(-4));

        // cooldownRatio = 1 - 4/5 = 0.2; consecutive penalty 3*30*0.2 = 18 and
        // base penalty 40*0.2 = 8: ErrorAware = 174, plus 150 + 40 + 100.
        Assert.Equal(464.0, Score(LoadBalancerStrategies.Adaptive, Candidate(2211), metrics), 9);
    }

    [Fact]
    public void AdaptiveForgetsFailuresAfterTheCooldown()
    {
        var metrics = FailureMetrics(consecutiveFailures: 3, lastFailureAt: Now.AddMinutes(-5));

        // Exactly five minutes: the decay window is exclusive.
        Assert.Equal(490.0, Score(LoadBalancerStrategies.Adaptive, Candidate(2221), metrics), 9);
    }

    [Fact]
    public void AdaptiveFloorsTheErrorPenaltyAtZero()
    {
        var metrics = FailureMetrics(consecutiveFailures: 10, lastFailureAt: Now.AddMinutes(-1));

        // 200 - 10*30*0.8 - 40*0.8 = -72 is clamped to zero.
        Assert.Equal(0.0, LoadBalancerScoring.ErrorAwareScore(metrics, Now), 9);
    }

    [Fact]
    public void FailoverScoreIsWeightPlusRandomPlusRateLimit()
    {
        var score = Score(LoadBalancerStrategies.Failover, Candidate(2302, weight: 100), NeutralMetrics());

        // Weight + Random(0-0.5) + neutral RateLimitAware.
        Assert.InRange(score, 200.0, 200.5);
    }

    [Fact]
    public void CircuitBreakerScoreIsWeightPlusClosedBreakerPlusRandom()
    {
        var score = Score(
            LoadBalancerStrategies.CircuitBreaker, Candidate(2402, weight: 100), NeutralMetrics());

        // Weight + closed breaker 200 + Random(0-0.5) + neutral RateLimitAware.
        Assert.InRange(score, 400.0, 400.5);
    }

    [Fact]
    public void RoundRobinScoreFallsAsRequestCountRises()
    {
        Assert.Equal(150.0, LoadBalancerScoring.RoundRobinScore(0));
        Assert.Equal(75.0, LoadBalancerScoring.RoundRobinScore(150));
        Assert.Equal(40.909090909, LoadBalancerScoring.RoundRobinScore(400), 9);

        // Negative counters cannot occur, but the reference clamps them to zero.
        Assert.Equal(150.0, LoadBalancerScoring.RoundRobinScore(-5));
    }

    [Fact]
    public void WeightRoundRobinScoreScalesByOrderingWeight()
    {
        // Weight 200 halves the normalized count: 150 / (1 + 150/150) = 75.
        Assert.Equal(75.0, LoadBalancerScoring.WeightRoundRobinScore(300, 200));

        // Non-positive weights keep the reference's default factor of 1.0.
        Assert.Equal(75.0, LoadBalancerScoring.WeightRoundRobinScore(150, 0));
    }

    [Fact]
    public void HardUnavailableScoresAreAtOrBelowHalfTheExhaustedScore()
    {
        Assert.True(LoadBalancerScoring.IsHardUnavailableScore(
            RateLimitAwareScorer.ExhaustedScore));
        Assert.True(LoadBalancerScoring.IsHardUnavailableScore(
            RateLimitAwareScorer.ExhaustedScore / 2));
        Assert.False(LoadBalancerScoring.IsHardUnavailableScore(
            (RateLimitAwareScorer.ExhaustedScore / 2) + 1));
        Assert.False(LoadBalancerScoring.IsHardUnavailableScore(0));
    }

    [Fact]
    public void UnhealthyThresholdIsThreeFailuresInsideTheCooldown()
    {
        Assert.False(LoadBalancerScoring.IsUnhealthy(
            FailureMetrics(2, Now.AddMinutes(-1)), Now));
        Assert.True(LoadBalancerScoring.IsUnhealthy(
            FailureMetrics(3, Now.AddMinutes(-1)), Now));
        Assert.False(LoadBalancerScoring.IsUnhealthy(
            FailureMetrics(3, Now.AddMinutes(-5)), Now));

        // A failure count without a timestamp counts as just happened.
        Assert.True(LoadBalancerScoring.IsUnhealthy(
            FailureMetrics(3, lastFailureAt: null), Now));
    }

    private static ChannelCandidate Candidate(
        long id,
        long weight = 100,
        int priority = 0) => new()
        {
            ChannelId = id,
            ChannelName = "channel-" + id,
            OrderingWeight = weight,
            Priority = priority,
        };

    private static ChannelMetricsSnapshot NeutralMetrics() => new();

    private static ChannelMetricsSnapshot FailureMetrics(
        long consecutiveFailures,
        DateTimeOffset? lastFailureAt) => new()
        {
            ConsecutiveFailures = consecutiveFailures,
            LastFailureAt = lastFailureAt,
        };

    private double Score(string strategy, ChannelCandidate candidate, ChannelMetricsSnapshot metrics)
        => LoadBalancerScoring.StrategyScore(strategy, candidate, metrics, Now, _rateLimitAware);

    private Task<ChannelCandidate[]> Sort(
        string key,
        ChannelCandidate[] candidates,
        int requiredCount,
        string strategy,
        bool trackSelection = false)
        => fixture.Cluster.GrainFactory
            .GetGrain<IChannelPoolGrain>(key)
            .SortAsync(candidates, Now, trackSelection, requiredCount, strategy);

    private IChannelGrain Channel(long id) => fixture.Cluster.GrainFactory.GetGrain<IChannelGrain>(id);
}
