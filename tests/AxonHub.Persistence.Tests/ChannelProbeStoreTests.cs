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
