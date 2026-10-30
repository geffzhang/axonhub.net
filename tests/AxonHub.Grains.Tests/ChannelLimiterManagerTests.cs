using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Port of internal/server/orchestrator/channel_limiter_manager_test.go: config
/// extraction, get-or-create stability, rebuilds on config change, stale-entry
/// dropping and the stats/snapshot views.
/// </summary>
public sealed class ChannelLimiterManagerTests
{
    [Fact]
    public void ExtractLimiterConfigDisabled()
    {
        // The reference's nil-channel case has no port analogue: the extractor
        // takes the decoded rate-limit settings, so "no channel" and "no rate
        // limit" both arrive as null.
        (string Name, ChannelRateLimitSettings? Settings)[] cases =
        [
            ("null settings", null),
            ("nullable rate limit", new ChannelRateLimitSettings()),
            ("zero max concurrent", new ChannelRateLimitSettings { MaxConcurrent = 0 }),
            ("negative max concurrent", new ChannelRateLimitSettings { MaxConcurrent = -5 }),
        ];

        foreach (var (name, settings) in cases)
        {
            var config = ChannelLimiterManager.ExtractLimiterConfig(settings);

            Assert.True(config.Capacity == 0, $"{name}: disabled cfg must have zero capacity");
            Assert.True(config.QueueSize == 0, $"{name}: disabled cfg must have zero queue size");
            Assert.True(config.TimeoutMs == 0, $"{name}: disabled cfg must have zero timeout");
        }
    }

    [Fact]
    public void ExtractLimiterConfigNoQueue()
    {
        var config = ChannelLimiterManager.ExtractLimiterConfig(
            new ChannelRateLimitSettings { MaxConcurrent = 5 });

        Assert.Equal(5, config.Capacity);
        Assert.Equal(0, config.QueueSize);
        Assert.Equal(0, config.TimeoutMs);
    }

    [Fact]
    public void ExtractLimiterConfigHardMode()
    {
        var config = ChannelLimiterManager.ExtractLimiterConfig(new ChannelRateLimitSettings
        {
            MaxConcurrent = 5,
            QueueSize = 10,
            QueueTimeoutMs = 30000,
        });

        Assert.Equal(5, config.Capacity);
        Assert.Equal(10, config.QueueSize);
        Assert.Equal(30000, config.TimeoutMs);
    }

    [Fact]
    public void ExtractLimiterConfigNegativeQueueFieldsTreatedAsUnset()
    {
        var config = ChannelLimiterManager.ExtractLimiterConfig(new ChannelRateLimitSettings
        {
            MaxConcurrent = 5,
            QueueSize = -10,
            QueueTimeoutMs = -100,
        });

        // Validation rejects negatives upstream; defensively treat as unset here.
        Assert.Equal(5, config.Capacity);
        Assert.Equal(0, config.QueueSize);
        Assert.Equal(0, config.TimeoutMs);
    }

    [Fact]
    public void LimiterConfigEquality()
    {
        var a = new ChannelLimiterManager.LimiterConfig(5, 10, 30000);
        var b = new ChannelLimiterManager.LimiterConfig(5, 10, 30000);
        var c = new ChannelLimiterManager.LimiterConfig(5, 11, 30000);
        var d = new ChannelLimiterManager.LimiterConfig(5, 10, 0);

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
    }

    [Fact]
    public void GetOrCreateReturnsNullWhenDisabled()
    {
        var manager = new ChannelLimiterManager();

        Assert.Null(manager.GetOrCreate(1, "c1", null));
        Assert.Null(manager.GetOrCreate(
            1,
            "c1",
            new ChannelRateLimitSettings { MaxConcurrent = 0 }));
    }

    [Fact]
    public void GetOrCreateReturnsStableInstance()
    {
        var manager = new ChannelLimiterManager();
        var settings = new ChannelRateLimitSettings { MaxConcurrent = 5, QueueSize = 10 };

        var first = manager.GetOrCreate(1, "c1", settings);
        var second = manager.GetOrCreate(1, "c1", settings);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void GetOrCreateRebuildsOnConfigChange()
    {
        var manager = new ChannelLimiterManager();

        var first = manager.GetOrCreate(
            1,
            "c1",
            new ChannelRateLimitSettings { MaxConcurrent = 5, QueueSize = 10 });
        var second = manager.GetOrCreate(
            1,
            "c1",
            new ChannelRateLimitSettings { MaxConcurrent = 5, QueueSize = 20 });
        var third = manager.GetOrCreate(
            1,
            "c1",
            new ChannelRateLimitSettings { MaxConcurrent = 5, QueueSize = 20, QueueTimeoutMs = 500 });

        Assert.NotSame(first, second);
        Assert.NotSame(second, third);
    }

    [Fact]
    public void ForgetDropsTheLimiter()
    {
        var manager = new ChannelLimiterManager();
        var settings = new ChannelRateLimitSettings { MaxConcurrent = 5 };

        var first = manager.GetOrCreate(1, "c1", settings);
        Assert.NotNull(first);

        manager.Forget(1);

        var second = manager.GetOrCreate(1, "c1", settings);
        Assert.NotNull(second);
        Assert.NotSame(first, second);

        // Forgetting an unknown channel must be a no-op.
        manager.Forget(9999);
    }

    [Fact]
    public void StatsReportsTrackedLoad()
    {
        var manager = new ChannelLimiterManager();
        var settings = new ChannelRateLimitSettings { MaxConcurrent = 3, QueueSize = 2 };

        // Unknown channel: false.
        Assert.False(manager.TryGetStats(7, out var inFlight, out var waiting));
        Assert.Equal(0, inFlight);
        Assert.Equal(0, waiting);

        var limiter = manager.GetOrCreate(7, "c7", settings);
        Assert.NotNull(limiter);
        limiter.Acquire(CancellationToken.None);
        limiter.Acquire(CancellationToken.None);

        Assert.True(manager.TryGetStats(7, out inFlight, out waiting));
        Assert.Equal(2, inFlight);
        Assert.Equal(0, waiting);

        limiter.Release();
        limiter.Release();
    }

    [Fact]
    public void GetOrCreateDropsStaleEntryWhenDisabled()
    {
        var manager = new ChannelLimiterManager();

        // First, create a limiter for the channel.
        Assert.NotNull(manager.GetOrCreate(
            42,
            "c42",
            new ChannelRateLimitSettings { MaxConcurrent = 5 }));

        Assert.True(manager.TryGetStats(42, out _, out _));

        // Same channel ID, but the rate limit was cleared.
        Assert.Null(manager.GetOrCreate(42, "c42", null));

        Assert.False(manager.TryGetStats(42, out _, out _));

        // Snapshot must also no longer report the channel.
        foreach (var snapshot in manager.Snapshot())
        {
            Assert.NotEqual(42, snapshot.ChannelId);
        }
    }

    [Fact]
    public void GetOrCreateReturnsDistinctLimitersPerChannel()
    {
        var manager = new ChannelLimiterManager();
        var settings = new ChannelRateLimitSettings { MaxConcurrent = 2 };

        Assert.NotSame(
            manager.GetOrCreate(1, "c1", settings),
            manager.GetOrCreate(2, "c2", settings));
    }
}
