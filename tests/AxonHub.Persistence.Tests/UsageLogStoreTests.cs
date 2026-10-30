namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class UsageLogStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task CreateAndListByRequestIdRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UsageLogStore(context);

        var usageLog = NewUsageLog(404);
        var id = await store.CreateAsync(usageLog);
        usageLog.Id = id;

        var actual = Assert.Single(await store.ListByRequestIdAsync(404));

        RecordAssert.Equivalent(usageLog, actual);
    }

    [Fact]
    public async Task ListByRequestIdReturnsEmptyForUnknownRequest()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UsageLogStore(context);

        Assert.Empty(await store.ListByRequestIdAsync(999));
    }

    [Fact]
    public async Task UnsetDetailTokensPersistAsZeroLikeEntDefaults()
    {
        // The reference only sets the detail columns when the response carries
        // token details; Ent then fills the schema default 0. Unknown details must
        // therefore read back as 0, never as NULL.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UsageLogStore(context);

        var id = await store.CreateAsync(new UsageLogRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            ModelId = "gpt-4o",
            RequestId = 505,
        });

        var actual = Assert.Single(await store.ListByRequestIdAsync(505));

        Assert.Equal(id, actual.Id);
        Assert.Equal(0L, actual.PromptTokens);
        Assert.Equal(0L, actual.CompletionTokens);
        Assert.Equal(0L, actual.TotalTokens);
        Assert.Equal(0L, actual.PromptAudioTokens);
        Assert.Equal(0L, actual.PromptCachedTokens);
        Assert.Equal(0L, actual.CompletionReasoningTokens);
        Assert.Equal(1L, actual.ProjectId);
        Assert.Equal("api", actual.Source);
        Assert.Equal("openai/chat_completions", actual.Format);
        Assert.Equal("[]", actual.CostItems);
        Assert.Null(actual.TotalCost);
    }

    [Fact]
    public async Task TokenCountsRoundTripAsInt64()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UsageLogStore(context);

        var usageLog = NewUsageLog(606);
        usageLog.PromptTokens = 4_000_000_000;
        usageLog.CompletionTokens = 4_000_000_001;
        usageLog.TotalTokens = 8_000_000_001;
        await store.CreateAsync(usageLog);

        var actual = Assert.Single(await store.ListByRequestIdAsync(606));

        Assert.Equal(4_000_000_000, actual.PromptTokens);
        Assert.Equal(4_000_000_001, actual.CompletionTokens);
        Assert.Equal(8_000_000_001, actual.TotalTokens);
    }

    private static UsageLogRecord NewUsageLog(long requestId) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        ApiKeyId = 3,
        ModelId = "gpt-4o",
        PromptTokens = 100,
        CompletionTokens = 50,
        TotalTokens = 150,
        PromptAudioTokens = 1,
        PromptCachedTokens = 2,
        PromptWriteCachedTokens = 3,
        PromptWriteCachedTokens5m = 4,
        PromptWriteCachedTokens1h = 5,
        CompletionAudioTokens = 6,
        CompletionReasoningTokens = 7,
        CompletionAcceptedPredictionTokens = 8,
        CompletionRejectedPredictionTokens = 9,
        Source = "test",
        Format = "openai/chat_completions",
        TotalCost = 0.125,
        CostItems = """[{"item":"prompt","cost":0.1}]""",
        CostPriceReferenceId = "price-1",
        ChannelId = 4,
        ProjectId = 5,
        RequestId = requestId,
    };
}
