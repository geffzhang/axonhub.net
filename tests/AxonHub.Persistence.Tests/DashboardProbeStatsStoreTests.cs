namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks the channel_probes aggregation behind <c>channelPerformanceStats</c>
/// (queryChannelProbeStats, channel_performance_helpers.go:35-62): the local
/// date and channel grouping, the request-count-weighted throughput and TTFT,
/// and the SQL filters (start bound, NULL/over-cap throughput rows).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DashboardProbeStatsStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime Start = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);

    private static readonly long DayOneNoon = new DateTimeOffset(
        2026, 3, 10, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static readonly long DayTwoNoon = new DateTimeOffset(
        2026, 3, 11, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public async Task GroupsByLocalDateAndChannelWithWeightedAverages()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await SeedAsync(
            db,
            Row(1, DayOneNoon, 100, tokensPerSecond: 10, timeToFirstTokenMs: 100),
            Row(1, DayOneNoon + 60, 300, tokensPerSecond: 30),
            // NULL throughput rows are excluded by the SQL predicate.
            Row(1, DayOneNoon + 120, 50, timeToFirstTokenMs: 1000),
            // The cap (maxThroughputCap = 2000) excludes outliers.
            Row(1, DayOneNoon + 180, 50, tokensPerSecond: 2500, timeToFirstTokenMs: 1000),
            Row(2, DayOneNoon, 10, tokensPerSecond: 5, timeToFirstTokenMs: 50),
            Row(1, DayTwoNoon, 40, tokensPerSecond: 20, timeToFirstTokenMs: 200),
            // Before the start bound.
            Row(
                1,
                new DateTimeOffset(Start.AddSeconds(-1)).ToUnixTimeSeconds(),
                999,
                tokensPerSecond: 1));

        await using var context = db.Factory.CreateDbContext();
        var rows = await new DashboardStore(context)
            .GetDailyChannelPerformanceFromProbesAsync(Start, "UTC");

        Assert.Equal(3, rows.Count);

        // Throughput = (10*100 + 30*300) / 400 = 25; TTFT = 100*100 / 100.
        Assert.Equal("2026-03-10", rows[0].Date);
        Assert.Equal(1, rows[0].ChannelId);
        Assert.Equal(400, rows[0].RequestCount);
        Assert.Equal(25, rows[0].Throughput);
        Assert.Equal(100, rows[0].FirstTokenMs);

        // The weighted TTFT denominator counts only rows with a sample.
        Assert.Equal("2026-03-10", rows[1].Date);
        Assert.Equal(2, rows[1].ChannelId);
        Assert.Equal(10, rows[1].RequestCount);
        Assert.Equal(5, rows[1].Throughput);
        Assert.Equal(50, rows[1].FirstTokenMs);

        Assert.Equal("2026-03-11", rows[2].Date);
        Assert.Equal(1, rows[2].ChannelId);
        Assert.Equal(40, rows[2].RequestCount);
        Assert.Equal(20, rows[2].Throughput);
        Assert.Equal(200, rows[2].FirstTokenMs);
    }

    [Fact]
    public async Task EmptyTableReturnsNoRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        await using var context = db.Factory.CreateDbContext();
        var rows = await new DashboardStore(context)
            .GetDailyChannelPerformanceFromProbesAsync(Start, "UTC");

        Assert.Empty(rows);
    }

    private static ChannelProbeRecord Row(
        long channelId,
        long timestamp,
        long totalRequestCount,
        double? tokensPerSecond = null,
        double? timeToFirstTokenMs = null)
        => new()
        {
            ChannelId = channelId,
            Timestamp = timestamp,
            TotalRequestCount = totalRequestCount,
            SuccessRequestCount = totalRequestCount,
            AvgTokensPerSecond = tokensPerSecond,
            AvgTimeToFirstTokenMs = timeToFirstTokenMs,
        };

    private static async Task SeedAsync(TestDatabase db, params ChannelProbeRecord[] rows)
    {
        await using var context = db.Factory.CreateDbContext();
        context.ChannelProbes.AddRange(rows);
        await context.SaveChangesAsync();
    }
}
