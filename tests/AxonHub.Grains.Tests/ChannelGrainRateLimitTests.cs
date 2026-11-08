using AxonHub.Grains.Abstractions;

namespace AxonHub.Grains.Tests;

/// <summary>
/// Locks the per-channel rate-limit authority moved into
/// <see cref="IChannelGrain"/>: RPM admission, TPM accounting, the
/// natural-minute window, the extend-only 429 cooldown, reset, and Orleans turn
/// serialization. The channel grain is the single writer the pool grain scores
/// from, replacing the shared per-silo tracker.
/// </summary>
public sealed class ChannelGrainRateLimitTests(GrainClusterFixture fixture)
    : IClassFixture<GrainClusterFixture>
{
    private static readonly DateTimeOffset Start = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TryAcquireRequestEnforcesTheLimit()
    {
        var grain = Channel(5101);

        Assert.True(await grain.TryAcquireRequestAsync(2, Start));
        Assert.True(await grain.TryAcquireRequestAsync(2, Start));
        Assert.False(await grain.TryAcquireRequestAsync(2, Start));

        Assert.Equal(2, (await grain.GetMetricsAsync(Start)).RateLimitRequests);
    }

    [Fact]
    public async Task NonPositiveLimitBypassesAndConsumesNothing()
    {
        var grain = Channel(5102);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Assert.True(await grain.TryAcquireRequestAsync(0, Start));
        }

        Assert.Equal(0, (await grain.GetMetricsAsync(Start)).RateLimitRequests);
    }

    [Fact]
    public async Task AddTokensAccumulatesInTheCurrentBucket()
    {
        var grain = Channel(5103);

        await grain.AddTokensAsync(100, Start);
        await grain.AddTokensAsync(200, Start);
        await grain.AddTokensAsync(0, Start);
        await grain.AddTokensAsync(-10, Start);

        var metrics = await grain.GetMetricsAsync(Start);

        Assert.Equal(300, metrics.RateLimitTokens);
        Assert.Equal(0, metrics.RateLimitRequests);
    }

    [Fact]
    public async Task MinuteBoundaryResetsRequestsAndTokens()
    {
        var grain = Channel(5104);

        Assert.True(await grain.TryAcquireRequestAsync(100, Start));
        await grain.AddTokensAsync(500, Start);

        var nextMinute = Start.AddMinutes(1);
        var metrics = await grain.GetMetricsAsync(nextMinute);

        Assert.Equal(0, metrics.RateLimitRequests);
        Assert.Equal(0, metrics.RateLimitTokens);

        Assert.True(await grain.TryAcquireRequestAsync(100, nextMinute));

        var after = await grain.GetMetricsAsync(nextMinute);

        Assert.Equal(1, after.RateLimitRequests);
        Assert.Equal(0, after.RateLimitTokens);
    }

    [Fact]
    public async Task CooldownExtendsOnlyAndExpires()
    {
        var grain = Channel(5105);

        await grain.SetCooldownAsync(Start.AddSeconds(30));
        await grain.SetCooldownAsync(Start.AddSeconds(10));

        var active = await grain.GetMetricsAsync(Start);

        Assert.Equal<DateTimeOffset?>(Start.AddSeconds(30), active.CooldownUntil);

        var expired = await grain.GetMetricsAsync(Start.AddSeconds(31));

        Assert.Null(expired.CooldownUntil);
    }

    [Fact]
    public async Task ResetRateLimitClearsTheState()
    {
        var grain = Channel(5106);

        Assert.True(await grain.TryAcquireRequestAsync(10, Start));
        await grain.AddTokensAsync(100, Start);
        await grain.SetCooldownAsync(Start.AddSeconds(30));

        await grain.ResetRateLimitAsync();

        var metrics = await grain.GetMetricsAsync(Start);

        Assert.Equal(0, metrics.RateLimitRequests);
        Assert.Equal(0, metrics.RateLimitTokens);
        Assert.Null(metrics.CooldownUntil);
    }

    [Fact]
    public async Task ConcurrentAdmissionsAreSerialized()
    {
        const int limit = 100;
        var grain = Channel(5107);

        var results = await Task.WhenAll(
            Enumerable.Range(0, limit * 2)
                .Select(_ => grain.TryAcquireRequestAsync(limit, Start)));

        Assert.Equal(limit, results.Count(accepted => accepted));
        Assert.Equal(limit, (await grain.GetMetricsAsync(Start)).RateLimitRequests);
    }

    [Fact]
    public async Task PoolGrainScoresFromTheGrainRateLimitSnapshot()
    {
        var busy = Channel(5301);
        var idle = Channel(5302);

        for (var attempt = 0; attempt < 9; attempt++)
        {
            Assert.True(await busy.TryAcquireRequestAsync(10, Start));
        }

        Assert.True(await idle.TryAcquireRequestAsync(10, Start));

        var candidates = new[]
        {
            new ChannelCandidate
            {
                ChannelId = 5301,
                ChannelName = "busy",
                OrderingWeight = 100,
                RateLimitSettings = new ChannelRateLimitSettings { Rpm = 10 },
            },
            new ChannelCandidate
            {
                ChannelId = 5302,
                ChannelName = "idle",
                OrderingWeight = 100,
                RateLimitSettings = new ChannelRateLimitSettings { Rpm = 10 },
            },
        };

        var sorted = await fixture.Cluster.GrainFactory
            .GetGrain<IChannelPoolGrain>("rate-limit-snapshot-model")
            .SortAsync(
                candidates,
                Start,
                trackSelection: false,
                requiredCount: 2,
                LoadBalancerStrategies.RoundRobin);

        // Both candidates have the same round-robin score; the rate-limit score
        // is 90 for idle (1/10) and 10 for busy (9/10).
        Assert.Equal(new[] { 5302L, 5301L }, sorted.Select(candidate => candidate.ChannelId).ToArray());
    }

    private IChannelGrain Channel(long id) => fixture.Cluster.GrainFactory.GetGrain<IChannelGrain>(id);
}
