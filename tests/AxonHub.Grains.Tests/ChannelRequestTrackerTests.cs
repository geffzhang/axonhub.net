using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Port of internal/server/orchestrator/channel_request_tracker_test.go: the
/// natural-minute request/token windows and the 429 cooldown map. The reference
/// tests poke at the private maps to simulate a past minute; the port drives the
/// same transitions through the injected clock, which exercises the production
/// code path instead (same observable behaviour).
/// </summary>
public sealed class ChannelRequestTrackerTests
{
    private static readonly DateTimeOffset Start = new(2026, 5, 1, 12, 0, 30, TimeSpan.Zero);

    [Fact]
    public void NewTrackerStartsEmpty()
    {
        var tracker = new ChannelRequestTracker();

        Assert.Equal(0, tracker.GetRequestCount(1));
        Assert.Equal(0, tracker.GetTokenCount(1));
    }

    [Fact]
    public void TryAcquireRequestIncrementsCount()
    {
        var tracker = new ChannelRequestTracker();

        Assert.True(tracker.TryAcquireRequest(1, 10));
        Assert.True(tracker.TryAcquireRequest(1, 10));
        Assert.True(tracker.TryAcquireRequest(1, 10));

        Assert.Equal(3, tracker.GetRequestCount(1));
    }

    [Fact]
    public void TryAcquireRequestRejectsWhenLimitReached()
    {
        var tracker = new ChannelRequestTracker();

        Assert.True(tracker.TryAcquireRequest(1, 2));
        Assert.True(tracker.TryAcquireRequest(1, 2));
        Assert.False(tracker.TryAcquireRequest(1, 2));

        Assert.Equal(2, tracker.GetRequestCount(1));
    }

    [Fact]
    public void AddTokensAccumulates()
    {
        var tracker = new ChannelRequestTracker();

        tracker.AddTokens(1, 100);
        tracker.AddTokens(1, 200);

        Assert.Equal(300, tracker.GetTokenCount(1));
    }

    [Fact]
    public void AddTokensIgnoresNonPositive()
    {
        var tracker = new ChannelRequestTracker();

        tracker.AddTokens(1, 0);
        tracker.AddTokens(1, -10);

        Assert.Equal(0, tracker.GetTokenCount(1));
    }

    [Fact]
    public void GetRequestCountUnknownChannelIsZero()
    {
        var tracker = new ChannelRequestTracker();

        Assert.Equal(0, tracker.GetRequestCount(999));
    }

    [Fact]
    public void GetTokenCountUnknownChannelIsZero()
    {
        var tracker = new ChannelRequestTracker();

        Assert.Equal(0, tracker.GetTokenCount(999));
    }

    [Fact]
    public void ChannelsAreTrackedIndependently()
    {
        var tracker = new ChannelRequestTracker();

        Assert.True(tracker.TryAcquireRequest(1, 10));
        Assert.True(tracker.TryAcquireRequest(1, 10));
        Assert.True(tracker.TryAcquireRequest(2, 10));
        tracker.AddTokens(1, 100);
        tracker.AddTokens(2, 500);

        Assert.Equal(2, tracker.GetRequestCount(1));
        Assert.Equal(1, tracker.GetRequestCount(2));
        Assert.Equal(100, tracker.GetTokenCount(1));
        Assert.Equal(500, tracker.GetTokenCount(2));
    }

    [Fact]
    public void WindowExpiresAtTheNextNaturalMinute()
    {
        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);

        Assert.True(tracker.TryAcquireRequest(1, 100));
        tracker.AddTokens(1, 500);

        // Cross the minute boundary: reads of the stale window return zero.
        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.Equal(0, tracker.GetRequestCount(1));
        Assert.Equal(0, tracker.GetTokenCount(1));

        // Writes create a fresh window.
        Assert.True(tracker.TryAcquireRequest(1, 100));
        Assert.Equal(1, tracker.GetRequestCount(1));
        Assert.Equal(0, tracker.GetTokenCount(1));
    }

    [Fact]
    public void AddTokensAfterWindowExpiryStartsAFreshWindow()
    {
        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);

        Assert.True(tracker.TryAcquireRequest(1, 100));
        clock.Advance(TimeSpan.FromSeconds(31));

        tracker.AddTokens(1, 200);

        Assert.Equal(200, tracker.GetTokenCount(1));
        // Requests were reset too: AddTokens creates a new window.
        Assert.Equal(0, tracker.GetRequestCount(1));
    }

    [Fact]
    public async Task ConcurrentCountersAreAtomic()
    {
        const int Goroutines = 100;
        const int OpsPerGoroutine = 100;

        // Pin the clock so the test can never straddle a minute boundary.
        var tracker = new ChannelRequestTracker(new TestTimeProvider(Start));

        var tasks = new Task[Goroutines * 2];
        for (var index = 0; index < Goroutines; index++)
        {
            tasks[index] = Task.Run(() =>
            {
                for (var op = 0; op < OpsPerGoroutine; op++)
                {
                    tracker.TryAcquireRequest(1, Goroutines * OpsPerGoroutine);
                }
            });
        }

        for (var index = 0; index < Goroutines; index++)
        {
            tasks[Goroutines + index] = Task.Run(() =>
            {
                for (var op = 0; op < OpsPerGoroutine; op++)
                {
                    tracker.AddTokens(1, 10);
                }
            });
        }

        await Task.WhenAll(tasks);

        Assert.Equal(Goroutines * OpsPerGoroutine, tracker.GetRequestCount(1));
        Assert.Equal(Goroutines * OpsPerGoroutine * 10, tracker.GetTokenCount(1));
    }

    [Fact]
    public async Task ConcurrentReadsAndWritesAreSafe()
    {
        var tracker = new ChannelRequestTracker(new TestTimeProvider(Start));

        var writer = Task.Run(() =>
        {
            for (var index = 0; index < 1000; index++)
            {
                tracker.TryAcquireRequest(1, 1000);
            }
        });

        var readerOne = Task.Run(() =>
        {
            for (var index = 0; index < 1000; index++)
            {
                _ = tracker.GetRequestCount(1);
            }
        });

        var readerTwo = Task.Run(() =>
        {
            for (var index = 0; index < 1000; index++)
            {
                _ = tracker.GetTokenCount(1);
            }
        });

        await Task.WhenAll(writer, readerOne, readerTwo);

        Assert.Equal(1000, tracker.GetRequestCount(1));
    }

    [Fact]
    public async Task TryAcquireStrictlyCapsConcurrentBurst()
    {
        const long ChannelId = 42;
        const long Limit = 2;
        const int Goroutines = 50;

        var tracker = new ChannelRequestTracker(new TestTimeProvider(Start));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var successes = 0;

        var tasks = new Task[Goroutines];
        for (var index = 0; index < Goroutines; index++)
        {
            tasks[index] = Task.Run(async () =>
            {
                await start.Task;

                if (tracker.TryAcquireRequest(ChannelId, Limit))
                {
                    Interlocked.Increment(ref successes);
                }
            });
        }

        start.SetResult();
        await Task.WhenAll(tasks);

        Assert.Equal(Limit, successes);
        Assert.Equal(Limit, tracker.GetRequestCount(ChannelId));
    }

    [Fact]
    public void TryAcquireResetsExpiredWindow()
    {
        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);

        Assert.True(tracker.TryAcquireRequest(1, 100));
        tracker.AddTokens(1, 500);
        clock.Advance(TimeSpan.FromSeconds(31));

        Assert.True(tracker.TryAcquireRequest(1, 1));

        Assert.Equal(1, tracker.GetRequestCount(1));
        Assert.Equal(0, tracker.GetTokenCount(1));
    }

    [Fact]
    public void SetCooldownMarksOnlyTheTargetChannel()
    {
        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);

        tracker.SetCooldown(1, clock.GetUtcNow().AddSeconds(30));

        Assert.True(tracker.IsCoolingDown(1));
        Assert.False(tracker.IsCoolingDown(2));
    }

    [Fact]
    public void IsCoolingDownIsFalseWhenNeverSet()
    {
        var tracker = new ChannelRequestTracker(new TestTimeProvider(Start));

        Assert.False(tracker.IsCoolingDown(1));
    }

    [Fact]
    public void ExpiredCooldownIsReportedAndCleanedUp()
    {
        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);

        tracker.SetCooldown(1, clock.GetUtcNow().AddSeconds(-10));

        Assert.False(tracker.IsCoolingDown(1));
        Assert.False(tracker.HasCooldownEntry(1));
    }

    [Fact]
    public void TryGetCooldownUntilReturnsTheExpiry()
    {
        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);

        Assert.False(tracker.TryGetCooldownUntil(1, out _));

        var until = clock.GetUtcNow().AddSeconds(30);
        tracker.SetCooldown(1, until);

        Assert.True(tracker.TryGetCooldownUntil(1, out var gotUntil));
        Assert.Equal(until, gotUntil);
    }

    [Fact]
    public void TryGetCooldownUntilCleansUpExpiredEntry()
    {
        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);

        tracker.SetCooldown(1, clock.GetUtcNow().AddSeconds(-10));

        Assert.False(tracker.TryGetCooldownUntil(1, out _));
        Assert.False(tracker.HasCooldownEntry(1));
    }

    [Fact]
    public void ClearExpiredCooldownDoesNotDeleteANewerValue()
    {
        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);

        var expiredUntil = clock.GetUtcNow().AddSeconds(-10);
        var newUntil = clock.GetUtcNow().AddSeconds(30);

        tracker.SetCooldown(1, expiredUntil);
        tracker.SetCooldown(1, newUntil);
        tracker.ClearExpiredCooldown(1, expiredUntil, clock.GetUtcNow());

        Assert.True(tracker.TryGetCooldownUntil(1, out var gotUntil));
        Assert.Equal(newUntil, gotUntil);
    }

    [Fact]
    public void CooldownOnlyEverExtends()
    {
        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);

        var longer = clock.GetUtcNow().AddSeconds(30);
        tracker.SetCooldown(1, longer);
        tracker.SetCooldown(1, clock.GetUtcNow().AddSeconds(10));

        Assert.True(tracker.TryGetCooldownUntil(1, out var gotUntil));
        Assert.Equal(longer, gotUntil);
    }

    [Fact]
    public void CooldownsAreTrackedPerChannel()
    {
        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);

        tracker.SetCooldown(1, clock.GetUtcNow().AddSeconds(10));
        tracker.SetCooldown(2, clock.GetUtcNow().AddSeconds(20));
        tracker.SetCooldown(3, clock.GetUtcNow().AddSeconds(30));

        Assert.True(tracker.IsCoolingDown(1));
        Assert.True(tracker.IsCoolingDown(2));
        Assert.True(tracker.IsCoolingDown(3));
        Assert.False(tracker.IsCoolingDown(4));
    }

    [Fact]
    public async Task ConcurrentCooldownsAreAllRecorded()
    {
        const int Goroutines = 100;

        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);
        var until = clock.GetUtcNow().AddSeconds(30);

        var tasks = new Task[Goroutines];
        for (var index = 0; index < Goroutines; index++)
        {
            var channelId = index;
            tasks[index] = Task.Run(() => tracker.SetCooldown(channelId, until));
        }

        await Task.WhenAll(tasks);

        for (var index = 0; index < Goroutines; index++)
        {
            Assert.True(tracker.IsCoolingDown(index));
        }
    }

    [Fact]
    public async Task ConcurrentCooldownReadsAndWritesAreSafe()
    {
        const int Ops = 1000;

        var clock = new TestTimeProvider(Start);
        var tracker = new ChannelRequestTracker(clock);
        var until = clock.GetUtcNow().AddSeconds(30);

        var tasks = new Task[Ops * 2];
        for (var index = 0; index < Ops; index++)
        {
            tasks[index] = Task.Run(() => tracker.SetCooldown(1, until));
            tasks[Ops + index] = Task.Run(() => tracker.IsCoolingDown(1));
        }

        await Task.WhenAll(tasks);

        Assert.True(tracker.IsCoolingDown(1));
    }
}
