using AxonHub.Llm.Transformer.Antigravity;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/transformer/antigravity/health_tracker_test.go: per-model
/// endpoint cooldown state, TTL expiry, lazy cleanup, and concurrent access.
/// The real-time sleeps mirror the reference durations.
/// </summary>
public sealed class AntigravityHealthTrackerTests
{
    [Fact]
    public void TestAntigravityHealthTracker_RecordFailure()
    {
        var tracker = new AntigravityHealthTracker();

        tracker.RecordFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily, 429);

        var failure = tracker.GetFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily);
        Assert.NotNull(failure);
        Assert.Equal("claude-sonnet-4-5", failure.Model);
        Assert.Equal(AntigravityConstants.EndpointDaily, failure.Endpoint);
        Assert.Equal(429, failure.StatusCode);
        Assert.Equal(DateTimeOffset.UtcNow, failure.LastFailedAt, TimeSpan.FromSeconds(1));
        Assert.Equal(
            DateTimeOffset.UtcNow.Add(AntigravityHealthTracker.DefaultCooldownDuration),
            failure.CooldownUntil,
            TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void TestAntigravityHealthTracker_RecordSuccess()
    {
        var tracker = new AntigravityHealthTracker();

        // Record a failure first.
        tracker.RecordFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily, 429);
        Assert.NotNull(tracker.GetFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily));

        // Record success should clear the failure.
        tracker.RecordSuccess("claude-sonnet-4-5", AntigravityConstants.EndpointDaily);
        Assert.Null(tracker.GetFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily));
    }

    [Fact]
    public void TestAntigravityHealthTracker_ShouldSkip_WithinCooldown()
    {
        // Use a short cooldown for testing.
        var tracker = new AntigravityHealthTracker(TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(10));

        tracker.RecordFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily, 429);

        // Should skip immediately after failure.
        Assert.True(tracker.ShouldSkip("claude-sonnet-4-5", AntigravityConstants.EndpointDaily));
    }

    [Fact]
    public void TestAntigravityHealthTracker_ShouldSkip_AfterCooldown()
    {
        // Use a very short cooldown for testing.
        var tracker = new AntigravityHealthTracker(TimeSpan.FromMilliseconds(10), TimeSpan.FromMinutes(10));

        tracker.RecordFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily, 429);

        // Should skip within cooldown.
        Assert.True(tracker.ShouldSkip("claude-sonnet-4-5", AntigravityConstants.EndpointDaily));

        // Wait for cooldown to expire.
        Thread.Sleep(20);

        // Should not skip after cooldown.
        Assert.False(tracker.ShouldSkip("claude-sonnet-4-5", AntigravityConstants.EndpointDaily));
    }

    [Fact]
    public void TestAntigravityHealthTracker_PerModel_Isolation()
    {
        var tracker = new AntigravityHealthTracker();

        // Record failure for claude on Daily.
        tracker.RecordFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily, 429);

        // Claude should be in cooldown for Daily.
        Assert.True(tracker.ShouldSkip("claude-sonnet-4-5", AntigravityConstants.EndpointDaily));

        // Gemini should NOT be in cooldown for Daily (different model).
        Assert.False(tracker.ShouldSkip("gemini-2.5-pro", AntigravityConstants.EndpointDaily));

        // Claude should NOT be in cooldown for Prod (different endpoint).
        Assert.False(tracker.ShouldSkip("claude-sonnet-4-5", AntigravityConstants.EndpointProd));
    }

    [Fact]
    public void TestAntigravityHealthTracker_TTL_Expiration()
    {
        // Use very short TTL for testing.
        var tracker = new AntigravityHealthTracker(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(20));

        tracker.RecordFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily, 429);

        // Entry should exist initially.
        Assert.NotNull(tracker.GetFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily));

        // Wait for TTL to expire.
        Thread.Sleep(30);

        // Entry should be expired and return null.
        Assert.Null(tracker.GetFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily));

        // ShouldSkip should also clean up and return false.
        Assert.False(tracker.ShouldSkip("claude-sonnet-4-5", AntigravityConstants.EndpointDaily));
    }

    [Fact]
    public void TestAntigravityHealthTracker_LazyCleanup()
    {
        // Use very short TTL for testing.
        var tracker = new AntigravityHealthTracker(TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(20));

        tracker.RecordFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily, 429);

        // Stats should show 1 entry.
        Assert.Equal(1, tracker.Stats().TotalEntries);

        // Wait for TTL to expire.
        Thread.Sleep(30);

        // Entry is still in the map (not yet cleaned).
        var stats = tracker.Stats();
        Assert.Equal(1, stats.TotalEntries);
        Assert.Equal(1, stats.Expired);

        // Accessing via ShouldSkip triggers lazy cleanup.
        tracker.ShouldSkip("claude-sonnet-4-5", AntigravityConstants.EndpointDaily);

        // Entry should now be removed from the map.
        Assert.Equal(0, tracker.Stats().TotalEntries);
    }

    [Fact]
    public void TestAntigravityHealthTracker_Stats()
    {
        // Use custom durations for predictable testing.
        var tracker = new AntigravityHealthTracker(TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(10));

        // Initially empty.
        var stats = tracker.Stats();
        Assert.Equal(0, stats.TotalEntries);
        Assert.Equal(0, stats.InCooldown);

        // Add some failures.
        tracker.RecordFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily, 429);
        tracker.RecordFailure("gemini-2.5-pro", AntigravityConstants.EndpointProd, 503);

        // Check stats.
        stats = tracker.Stats();
        Assert.Equal(2, stats.TotalEntries);
        Assert.Equal(2, stats.InCooldown);
        Assert.Equal(2, stats.CooldownEntries.Count);

        // Wait for cooldown to expire.
        Thread.Sleep(150);

        // Check stats again.
        stats = tracker.Stats();
        Assert.Equal(2, stats.TotalEntries);
        Assert.Equal(0, stats.InCooldown);
        Assert.Empty(stats.CooldownEntries);
    }

    [Fact]
    public async Task TestAntigravityHealthTracker_ConcurrentAccess()
    {
        var tracker = new AntigravityHealthTracker();

        const int numGoroutines = 100;
        const int numOperations = 100;

        // Simulate concurrent access from multiple goroutines.
        var tasks = new Task[numGoroutines];
        for (var i = 0; i < numGoroutines; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                const string model = "test-model";
                var endpoint = AntigravityConstants.EndpointDaily;

                for (var j = 0; j < numOperations; j++)
                {
                    // Mix of operations.
                    switch (j % 4)
                    {
                        case 0:
                            tracker.RecordFailure(model, endpoint, 429);
                            break;
                        case 1:
                            tracker.ShouldSkip(model, endpoint);
                            break;
                        case 2:
                            tracker.RecordSuccess(model, endpoint);
                            break;
                        default:
                            tracker.GetFailure(model, endpoint);
                            break;
                    }
                }
            });
        }

        await Task.WhenAll(tasks);

        // Should not panic or deadlock; stats stay readable.
        Assert.True(tracker.Stats().TotalEntries >= 0);
    }

    [Fact]
    public void TestAntigravityHealthTracker_Clear()
    {
        var tracker = new AntigravityHealthTracker();

        // Add some failures.
        tracker.RecordFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily, 429);
        tracker.RecordFailure("gemini-2.5-pro", AntigravityConstants.EndpointProd, 503);

        Assert.Equal(2, tracker.Stats().TotalEntries);

        // Clear all entries.
        tracker.Clear();

        Assert.Equal(0, tracker.Stats().TotalEntries);
        Assert.Null(tracker.GetFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily));
    }

    [Fact]
    public void TestAntigravityHealthTracker_MultipleModels_MultipleEndpoints()
    {
        var tracker = new AntigravityHealthTracker();

        // Simulate various failures across models and endpoints.
        tracker.RecordFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily, 429);
        tracker.RecordFailure("claude-sonnet-4-5", AntigravityConstants.EndpointProd, 503);
        tracker.RecordFailure("gemini-2.5-pro", AntigravityConstants.EndpointDaily, 404);
        tracker.RecordFailure("gemini-3-flash", AntigravityConstants.EndpointAutopush, 500);

        // Verify each combination is tracked independently.
        Assert.True(tracker.ShouldSkip("claude-sonnet-4-5", AntigravityConstants.EndpointDaily));
        Assert.True(tracker.ShouldSkip("claude-sonnet-4-5", AntigravityConstants.EndpointProd));
        Assert.True(tracker.ShouldSkip("gemini-2.5-pro", AntigravityConstants.EndpointDaily));
        Assert.True(tracker.ShouldSkip("gemini-3-flash", AntigravityConstants.EndpointAutopush));

        // Verify non-failed combinations are not skipped.
        Assert.False(tracker.ShouldSkip("claude-sonnet-4-5", AntigravityConstants.EndpointAutopush));
        Assert.False(tracker.ShouldSkip("gemini-2.5-pro", AntigravityConstants.EndpointProd));
        Assert.False(tracker.ShouldSkip("gemini-3-flash", AntigravityConstants.EndpointDaily));

        // Stats should reflect 4 entries.
        var stats = tracker.Stats();
        Assert.Equal(4, stats.TotalEntries);
        Assert.Equal(4, stats.InCooldown);
    }

    [Fact]
    public void TestAntigravityHealthTracker_UpdateExistingFailure()
    {
        var tracker = new AntigravityHealthTracker(TimeSpan.FromMilliseconds(100), TimeSpan.FromMinutes(10));

        // Record initial failure.
        tracker.RecordFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily, 429);
        var firstFailure = tracker.GetFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily);
        Assert.NotNull(firstFailure);

        // Wait a bit.
        Thread.Sleep(20);

        // Record another failure for the same model+endpoint.
        tracker.RecordFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily, 503);
        var secondFailure = tracker.GetFailure("claude-sonnet-4-5", AntigravityConstants.EndpointDaily);
        Assert.NotNull(secondFailure);

        // Should have updated the entry.
        Assert.Equal(503, secondFailure.StatusCode);
        Assert.True(secondFailure.LastFailedAt > firstFailure.LastFailedAt);
        Assert.True(secondFailure.CooldownUntil > firstFailure.CooldownUntil);
    }
}
