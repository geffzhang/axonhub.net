namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks the <c>channel_probes</c> read seam behind the <c>channelProbeData</c>
/// root: the channel filter, the inclusive timestamp window, the ascending
/// timestamp order with no tie-breaker, and the nullable averages
/// (biz/channel_probe.go:375-400, ent query on the un-mixed table).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelProbeStoreTests(PostgresFixture fixture)
{
    private const long End = 1_772_000_000L;

    [Fact]
    public async Task QueryFiltersByChannelAndInclusiveWindow()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await SeedAsync(
            db,
            // Outside the window on both sides.
            Row(1, End - 100, 1, 1),
            Row(1, End + 100, 2, 2),
            // Another channel inside the window.
            Row(2, End - 10, 3, 3),
            // Both window boundaries are inclusive.
            Row(1, End - 50, 10, 9, tokensPerSecond: 12.5, timeToFirstTokenMs: 300),
            Row(1, End, 20, null, tokensPerSecond: 20.5));

        await using var context = db.Factory.CreateDbContext();
        var rows = await new ChannelProbeStore(context).QueryAsync([1], End - 50, End);

        Assert.Equal(2, rows.Count);
        Assert.Equal(End - 50, rows[0].Timestamp);
        Assert.Equal(10, rows[0].TotalRequestCount);
        Assert.Equal(9, rows[0].SuccessRequestCount);
        Assert.Equal(12.5, rows[0].AvgTokensPerSecond);
        Assert.Equal(300, rows[0].AvgTimeToFirstTokenMs);
        Assert.Equal(1, rows[0].ChannelId);

        Assert.Equal(End, rows[1].Timestamp);
        Assert.Equal(20, rows[1].TotalRequestCount);
        Assert.Equal(0, rows[1].SuccessRequestCount);
        Assert.Equal(20.5, rows[1].AvgTokensPerSecond);
        Assert.Null(rows[1].AvgTimeToFirstTokenMs);
        Assert.Equal(1, rows[1].ChannelId);
    }

    [Fact]
    public async Task QueryOrdersByTimestampAscendingAcrossChannels()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await SeedAsync(
            db,
            Row(2, End, 30, 30),
            Row(1, End - 20, 10, 10),
            Row(2, End - 40, 20, 20));
        // Two rows on the same bucket: the table has no unique index, so both
        // must be returned (the service's later-wins map depends on it).
        await SeedAsync(
            db,
            Row(1, End - 20, 11, 11));

        await using var context = db.Factory.CreateDbContext();
        var rows = await new ChannelProbeStore(context).QueryAsync([1, 2], End - 40, End);

        Assert.Equal(4, rows.Count);
        Assert.Equal(End - 40, rows[0].Timestamp);
        Assert.Equal(2, rows[0].ChannelId);
        var sameBucket = rows
            .Where(static row => row.Timestamp == End - 20)
            .ToArray();
        Assert.Equal(2, sameBucket.Length);
        Assert.All(sameBucket, static row => Assert.Equal(1, row.ChannelId));
        Assert.Equal(End, rows[^1].Timestamp);
        Assert.Equal(30, rows[^1].TotalRequestCount);
    }

    [Fact]
    public async Task EmptyChannelIdsReturnNoRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await SeedAsync(db, Row(1, End, 10, 10));

        await using var context = db.Factory.CreateDbContext();
        var rows = await new ChannelProbeStore(context).QueryAsync([], End - 60, End);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task ComputeStatsAppliesTheAttemptBasedProbeFormulas()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        var start = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc);
        var end = start.AddMinutes(5);
        await using (var context = db.Factory.CreateDbContext())
        {
            context.RequestExecutions.AddRange(
                // Channel 1, streaming: effective latency excludes the first
                // token; only successful attempts carry tokens.
                Execution(100, 1, "completed", stream: true, latencyMs: 1200, firstTokenMs: 200, start.AddSeconds(10)),
                // Channel 1, non-streaming: the whole latency is effective.
                Execution(101, 1, "completed", stream: false, latencyMs: 500, firstTokenMs: null, start.AddSeconds(20)),
                // Failed attempt: counted in total, not in success or tokens.
                Execution(102, 1, "failed", stream: false, latencyMs: null, firstTokenMs: null, start.AddSeconds(30)),
                // Statuses outside completed/failed are ignored entirely.
                Execution(103, 1, "canceled", stream: false, latencyMs: 900, firstTokenMs: null, start.AddSeconds(40)),
                Execution(104, 1, "processing", stream: false, latencyMs: null, firstTokenMs: null, start.AddSeconds(50)),
                // Channel 2 retry of request 100: tokens are attributed per
                // channel, and a TTFT at/after the total latency clamps to 0.
                Execution(100, 2, "completed", stream: true, latencyMs: 300, firstTokenMs: 300, start.AddSeconds(5)));
            context.UsageLogs.AddRange(
                Usage(100, 1, completionTokens: 100, start.AddSeconds(10)),
                Usage(100, 1, completionTokens: 50, start.AddSeconds(11)),
                // Before the window: the joined sub-query must exclude it.
                Usage(100, 1, completionTokens: 1000, start.AddMinutes(-1)),
                Usage(101, 1, completionTokens: 30, start.AddSeconds(20)),
                // Failed execution: the join only matches completed rows.
                Usage(102, 1, completionTokens: 70, start.AddSeconds(30)),
                Usage(100, 2, completionTokens: 999, start.AddSeconds(5)));
            await context.SaveChangesAsync();
        }

        await using var probeContext = db.Factory.CreateDbContext();
        var rows = await new ChannelProbeStore(probeContext)
            .ComputeStatsAsync([1, 2], start, end);

        Assert.Equal(2, rows.Count);
        var channelOne = rows[0];
        Assert.Equal(1, channelOne.ChannelId);
        Assert.Equal(3, channelOne.TotalCount);
        Assert.Equal(2, channelOne.SuccessCount);
        Assert.Equal(180, channelOne.TotalTokens);
        Assert.Equal(1500, channelOne.EffectiveLatencyMs);
        Assert.Equal(200, channelOne.TotalFirstTokenLatency);
        Assert.Equal(3, channelOne.RequestCount);
        Assert.Equal(1, channelOne.StreamingRequestCount);

        var channelTwo = rows[1];
        Assert.Equal(2, channelTwo.ChannelId);
        Assert.Equal(1, channelTwo.TotalCount);
        Assert.Equal(1, channelTwo.SuccessCount);
        Assert.Equal(999, channelTwo.TotalTokens);
        Assert.Equal(0, channelTwo.EffectiveLatencyMs);
        Assert.Equal(300, channelTwo.TotalFirstTokenLatency);
        Assert.Equal(1, channelTwo.RequestCount);
        Assert.Equal(1, channelTwo.StreamingRequestCount);
    }

    [Fact]
    public async Task ComputeStatsExcludesExecutionsOutsideTheWindowAndOtherChannels()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        var start = new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc);
        var end = start.AddMinutes(5);
        await using (var context = db.Factory.CreateDbContext())
        {
            context.RequestExecutions.AddRange(
                // The start bound is inclusive; the end bound is exclusive.
                Execution(1, 1, "completed", stream: false, latencyMs: 100, firstTokenMs: null, start),
                Execution(2, 1, "completed", stream: false, latencyMs: 100, firstTokenMs: null, end),
                Execution(3, 1, "completed", stream: false, latencyMs: 100, firstTokenMs: null, start.AddMinutes(-1)),
                Execution(4, 5, "completed", stream: false, latencyMs: 100, firstTokenMs: null, start.AddSeconds(1)));
            await context.SaveChangesAsync();
        }

        await using var probeContext = db.Factory.CreateDbContext();
        var rows = await new ChannelProbeStore(probeContext)
            .ComputeStatsAsync([1], start, end);

        var row = Assert.Single(rows);
        Assert.Equal(1, row.ChannelId);
        Assert.Equal(1, row.TotalCount);
    }

    [Fact]
    public async Task ComputeStatsReturnsNothingForAnEmptyChannelList()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        await using var context = db.Factory.CreateDbContext();
        var rows = await new ChannelProbeStore(context).ComputeStatsAsync(
            [],
            new DateTime(2026, 3, 10, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 10, 10, 5, 0, DateTimeKind.Utc));

        Assert.Empty(rows);
    }

    [Fact]
    public async Task CreateRangeInsertsEveryBucketIncludingNullAverages()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelProbeStore(context);
        await store.CreateRangeAsync(
        [
            Row(1, End - 60, 10, 9, tokensPerSecond: 12.5, timeToFirstTokenMs: 300),
            Row(2, End, 20, 20),
        ]);

        var rows = await store.QueryAsync([1, 2], End - 60, End);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, static row => Assert.True(row.Id > 0));
        Assert.Equal(12.5, rows[0].AvgTokensPerSecond);
        Assert.Null(rows[1].AvgTokensPerSecond);
        Assert.Null(rows[1].AvgTimeToFirstTokenMs);
        Assert.Equal(End, rows[1].Timestamp);
    }

    private static RequestExecutionRecord Execution(
        long requestId,
        long channelId,
        string status,
        bool stream,
        long? latencyMs,
        long? firstTokenMs,
        DateTime createdAt)
        => new()
        {
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            ModelId = "gpt-4o",
            Status = status,
            Stream = stream,
            MetricsLatencyMs = latencyMs,
            MetricsFirstTokenLatencyMs = firstTokenMs,
            ChannelId = channelId,
            RequestId = requestId,
        };

    private static UsageLogRecord Usage(
        long requestId,
        long channelId,
        long completionTokens,
        DateTime createdAt)
        => new()
        {
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            ModelId = "gpt-4o",
            CompletionTokens = completionTokens,
            ChannelId = channelId,
            RequestId = requestId,
        };

    private static ChannelProbeRecord Row(
        long channelId,
        long timestamp,
        long totalRequestCount,
        long? successRequestCount,
        double? tokensPerSecond = null,
        double? timeToFirstTokenMs = null)
        => new()
        {
            ChannelId = channelId,
            Timestamp = timestamp,
            TotalRequestCount = totalRequestCount,
            SuccessRequestCount = successRequestCount ?? 0,
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
