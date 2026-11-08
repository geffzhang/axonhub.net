using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Port of internal/server/orchestrator/lb_strategy_rate_limit_test.go: the
/// RateLimitAware load-balancing strategy's RPM/TPM, cooldown and concurrency
/// sub-scores, the exhausted penalty, and the diagnostic details map.
/// </summary>
public sealed class RateLimitAwareScorerTests
{
    private static readonly DateTimeOffset Start = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ScoreWithoutRateLimitIsFull()
    {
        var scorer = new RateLimitAwareScorer(new ChannelRequestTracker());

        Assert.Equal(100.0, scorer.Score(1, null));
    }

    [Fact]
    public void NoRateLimitChannelIgnoresManagerStats()
    {
        var manager = new ChannelLimiterManager();
        var scorer = new RateLimitAwareScorer(new ChannelRequestTracker(), manager);

        // No limiter is created for the channel because RateLimit is nil.
        Assert.Equal(100.0, scorer.Score(1, null));
    }

    [Fact]
    public void CooldownScoresExhausted()
    {
        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);
        var scorer = new RateLimitAwareScorer(tracker);

        tracker.SetCooldown(1, clock.GetUtcNow().AddSeconds(30));

        Assert.Equal(RateLimitAwareScorer.ExhaustedScore, scorer.Score(1, null));
    }

    [Fact]
    public void RpmExhaustedScoresExhausted()
    {
        var tracker = new ChannelRequestTracker(new TestTimeProvider(Start));
        var scorer = new RateLimitAwareScorer(tracker);

        var settings = new ChannelRateLimitSettings { Rpm = 100 };
        for (var index = 0; index < 100; index++)
        {
            tracker.TryAcquireRequest(1, 100);
        }

        Assert.Equal(RateLimitAwareScorer.ExhaustedScore, scorer.Score(1, settings));
    }

    [Fact]
    public void TpmExhaustedScoresExhausted()
    {
        var tracker = new ChannelRequestTracker(new TestTimeProvider(Start));
        var scorer = new RateLimitAwareScorer(tracker);

        var settings = new ChannelRateLimitSettings { Tpm = 1000 };
        tracker.AddTokens(1, 1000);

        Assert.Equal(RateLimitAwareScorer.ExhaustedScore, scorer.Score(1, settings));
    }

    [Fact]
    public void CooldownTakesPriority()
    {
        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);
        var scorer = new RateLimitAwareScorer(tracker);

        var settings = new ChannelRateLimitSettings { Rpm = 100 };
        tracker.SetCooldown(1, clock.GetUtcNow().AddSeconds(30));
        tracker.TryAcquireRequest(1, 100);

        Assert.Equal(RateLimitAwareScorer.ExhaustedScore, scorer.Score(1, settings));
    }

    [Fact]
    public void NormalUsageScalesByTheLargestRatio()
    {
        var tracker = new ChannelRequestTracker(new TestTimeProvider(Start));
        var scorer = new RateLimitAwareScorer(tracker);

        var settings = new ChannelRateLimitSettings { Rpm = 100, Tpm = 1000 };
        tracker.TryAcquireRequest(1, 100);
        tracker.TryAcquireRequest(1, 100);
        tracker.AddTokens(1, 500);

        // maxRatio = max(2/100, 500/1000) = 0.5 -> score 50.
        Assert.Equal(50.0, scorer.Score(1, settings));
    }

    [Fact]
    public void UnboundedQueueConcurrencyContributes()
    {
        var manager = new ChannelLimiterManager();
        var scorer = new RateLimitAwareScorer(new ChannelRequestTracker(), manager);

        // No QueueSize -> unbounded blocking queue.
        var settings = new ChannelRateLimitSettings { MaxConcurrent = 10 };
        var limiter = manager.GetOrCreate(1, "test", settings);
        Assert.NotNull(limiter);

        // 8/10 in flight (below capacity): 30 + 70*(1-0.8) = 44.
        for (var index = 0; index < 8; index++)
        {
            limiter.Acquire(CancellationToken.None);
        }

        Assert.Equal(44.0, scorer.Score(1, settings), precision: 4);

        // Saturate to capacity (10/10, no waiters): an unbounded queue is NEVER
        // exhausted — the channel sits at the queueing ceiling (30).
        for (var index = 0; index < 2; index++)
        {
            limiter.Acquire(CancellationToken.None);
        }

        var score = scorer.Score(1, settings);
        Assert.Equal(30.0, score, precision: 4);
        Assert.NotEqual(RateLimitAwareScorer.ExhaustedScore, score);

        for (var index = 0; index < 10; index++)
        {
            limiter.Release();
        }
    }

    [Fact]
    public async Task HardModeCapsQueueingScore()
    {
        var manager = new ChannelLimiterManager();
        var scorer = new RateLimitAwareScorer(new ChannelRequestTracker(), manager);

        var settings = new ChannelRateLimitSettings { MaxConcurrent = 2, QueueSize = 10 };
        var limiter = manager.GetOrCreate(1, "test", settings);
        Assert.NotNull(limiter);

        // Saturate capacity: inFlight == capacity, waiting == 0 -> 100*0.3 = 30.
        limiter.Acquire(CancellationToken.None);
        limiter.Acquire(CancellationToken.None);

        Assert.Equal(30.0, scorer.Score(1, settings), precision: 4);

        using var cancel = new CancellationTokenSource();
        var enqueued = LimiterHarness.LaunchAcquire(limiter, cancel.Token, 5);

        try
        {
            await LimiterHarness.WaitUntilAsync(
                () => limiter.Stats().Waiting == 5,
                "five waiters should enqueue");

            // waitingRatio = 5/10 = 0.5 -> 100*0.3*0.5 = 15.
            Assert.Equal(15.0, scorer.Score(1, settings), precision: 4);

            // Capacity + queue are now full -> exhausted.
            var enqueuedExtra = LimiterHarness.LaunchAcquire(limiter, cancel.Token, 5);

            await LimiterHarness.WaitUntilAsync(
                () => limiter.Stats().Waiting == 10,
                "the queue should fill to ten waiters");

            Assert.Equal(RateLimitAwareScorer.ExhaustedScore, scorer.Score(1, settings));

            cancel.Cancel();
            await LimiterHarness.DrainGrantsAsync(limiter, enqueued);
            await LimiterHarness.DrainGrantsAsync(limiter, enqueuedExtra);
        }
        finally
        {
            cancel.Cancel();
        }

        limiter.Release();
        limiter.Release();
    }

    [Fact]
    public void HardModeScoreIsMonotonic()
    {
        var manager = new ChannelLimiterManager();
        var scorer = new RateLimitAwareScorer(new ChannelRequestTracker(), manager);

        var settings = new ChannelRateLimitSettings { MaxConcurrent = 10, QueueSize = 10 };
        var limiter = manager.GetOrCreate(1, "test", settings);
        Assert.NotNull(limiter);

        // 9/10 in flight: a saturated channel must never outrank this one.
        for (var index = 0; index < 9; index++)
        {
            limiter.Acquire(CancellationToken.None);
        }

        var belowCapacityScore = scorer.Score(1, settings);

        limiter.Acquire(CancellationToken.None);
        var atCapacityScore = scorer.Score(1, settings);

        Assert.True(
            belowCapacityScore > atCapacityScore,
            "below-capacity must score higher than at-capacity-with-empty-queue");

        // At capacity with an empty queue: queueingScoreCeiling × maxScore = 30.
        Assert.Equal(30.0, atCapacityScore, precision: 4);

        for (var index = 0; index < 10; index++)
        {
            limiter.Release();
        }
    }

    [Fact]
    public void StaleEntryAfterLimiterDisabledReadsAsUnlimited()
    {
        var manager = new ChannelLimiterManager();
        var scorer = new RateLimitAwareScorer(new ChannelRequestTracker(), manager);

        Assert.NotNull(manager.GetOrCreate(
            1,
            "test",
            new ChannelRateLimitSettings { MaxConcurrent = 5 }));

        // The user cleared the rate limit between GetOrCreate (which admitted a
        // request) and the next Score call, which still sees the stale entry.
        Assert.Equal(100.0, scorer.Score(1, null));
    }

    [Fact]
    public void ScoreIsTheMinOfRpmAndConcurrency()
    {
        var tracker = new ChannelRequestTracker(new TestTimeProvider(Start));
        var manager = new ChannelLimiterManager();
        var scorer = new RateLimitAwareScorer(tracker, manager);

        var settings = new ChannelRateLimitSettings { Rpm = 100, MaxConcurrent = 10 };

        // 30% RPM (score 70) and 80% in flight below capacity (score 30 + 70*0.2 = 44).
        for (var index = 0; index < 30; index++)
        {
            tracker.TryAcquireRequest(1, 100);
        }

        var limiter = manager.GetOrCreate(1, "test", settings);
        Assert.NotNull(limiter);
        for (var index = 0; index < 8; index++)
        {
            limiter.Acquire(CancellationToken.None);
        }

        Assert.Equal(44.0, scorer.Score(1, settings), precision: 4);

        for (var index = 0; index < 8; index++)
        {
            limiter.Release();
        }
    }

    [Fact]
    public void ScoreWithDebugReportsCooldown()
    {
        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);
        var scorer = new RateLimitAwareScorer(tracker);

        tracker.SetCooldown(1, clock.GetUtcNow().AddSeconds(30));

        var debug = scorer.ScoreWithDebug(1, null);

        Assert.Equal(RateLimitAwareScorer.ExhaustedScore, debug.Score);
        Assert.Equal("RateLimitAware", debug.StrategyName);
        Assert.Equal("channel_in_cooldown", debug.Details["reason"]);
        Assert.True(debug.Details["exhausted"] is true);
        Assert.True(debug.Details.ContainsKey("cooldown_until"));
    }

    [Fact]
    public void ScoreWithDebugReportsRpmExhaustion()
    {
        var tracker = new ChannelRequestTracker(new TestTimeProvider(Start));
        var scorer = new RateLimitAwareScorer(tracker);

        var settings = new ChannelRateLimitSettings { Rpm = 10 };
        for (var index = 0; index < 10; index++)
        {
            tracker.TryAcquireRequest(1, 10);
        }

        var debug = scorer.ScoreWithDebug(1, settings);

        Assert.Equal(RateLimitAwareScorer.ExhaustedScore, debug.Score);
        Assert.True(debug.Details["rpm_exhausted"] is true);
        Assert.Equal(10L, Assert.IsType<long>(debug.Details["rpm_limit"]));
        Assert.Equal(10L, Assert.IsType<long>(debug.Details["rpm_current"]));
    }

    [Fact]
    public void ScoreWithDebugReportsNoRateLimit()
    {
        var scorer = new RateLimitAwareScorer(new ChannelRequestTracker());

        var debug = scorer.ScoreWithDebug(1, null);

        Assert.Equal(100.0, debug.Score);
        Assert.Equal("no_rpm_tpm_configured", debug.Details["rpm_tpm_reason"]);
    }

    [Fact]
    public void ExpiredCooldownScoresFullAndIsCleanedUp()
    {
        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);
        var scorer = new RateLimitAwareScorer(tracker);

        tracker.SetCooldown(1, clock.GetUtcNow().AddSeconds(-10));

        Assert.Equal(100.0, scorer.Score(1, null));
        Assert.False(tracker.IsCoolingDown(1));
    }

    [Fact]
    public void ScoresArePerChannel()
    {
        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);
        var scorer = new RateLimitAwareScorer(tracker);

        tracker.SetCooldown(1, clock.GetUtcNow().AddSeconds(30));

        Assert.Equal(RateLimitAwareScorer.ExhaustedScore, scorer.Score(1, null));
        Assert.Equal(100.0, scorer.Score(2, null));
    }

    [Fact]
    public void NameIsRateLimitAware()
    {
        var scorer = new RateLimitAwareScorer(new ChannelRequestTracker());

        Assert.Equal("RateLimitAware", scorer.Name);
    }

    [Fact]
    public void SnapshotStateScoresRpmAndTpm()
    {
        var scorer = new RateLimitAwareScorer((ChannelLimiterManager?)null);
        var settings = new ChannelRateLimitSettings { Rpm = 100, Tpm = 1000 };

        // maxRatio = max(2/100, 500/1000) = 0.5 -> score 50.
        var score = scorer.Score(1, new ChannelRateLimitState(2, 500, null), settings);

        Assert.Equal(50.0, score);
    }

    [Fact]
    public void SnapshotCooldownScoresExhausted()
    {
        var scorer = new RateLimitAwareScorer((ChannelLimiterManager?)null);
        var state = new ChannelRateLimitState(0, 0, Start.AddSeconds(30));

        Assert.Equal(RateLimitAwareScorer.ExhaustedScore, scorer.Score(1, state, null));
    }

    [Fact]
    public void SnapshotRpmExhaustionScoresExhausted()
    {
        var scorer = new RateLimitAwareScorer((ChannelLimiterManager?)null);
        var settings = new ChannelRateLimitSettings { Rpm = 100 };
        var state = new ChannelRateLimitState(100, 0, null);

        Assert.Equal(RateLimitAwareScorer.ExhaustedScore, scorer.Score(1, state, settings));
    }

    [Fact]
    public void TrackerlessScorerRequiresSnapshotState()
    {
        var scorer = new RateLimitAwareScorer((ChannelLimiterManager?)null);

        Assert.Throws<InvalidOperationException>(() => scorer.Score(1, null));
        Assert.Throws<InvalidOperationException>(() => scorer.ScoreWithDebug(1, null));
    }
}
