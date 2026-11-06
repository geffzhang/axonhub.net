using Dapper;

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

    [Fact]
    public async Task CountByApiKeyScopesToTheKeyAndWindow()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UsageLogStore(context);

        await store.CreateAsync(Log(3, CreatedAt, 10, 0.1, 701));
        await store.CreateAsync(Log(3, CreatedAt.AddSeconds(1), 20, 0.2, 702));
        await store.CreateAsync(Log(4, CreatedAt.AddSeconds(1), 30, 0.3, 703));
        await store.CreateAsync(Log(3, CreatedAt.AddSeconds(-1), 40, 0.4, 704));

        Assert.Equal(3L, await store.CountByApiKeyAsync(3, null, null, endInclusive: true));
        Assert.Equal(
            2L,
            await store.CountByApiKeyAsync(
                3,
                CreatedAt,
                CreatedAt.AddSeconds(1),
                endInclusive: true));
        Assert.Equal(
            1L,
            await store.CountByApiKeyAsync(
                3,
                CreatedAt,
                CreatedAt.AddSeconds(1),
                endInclusive: false));
    }

    [Fact]
    public async Task AggregateByApiKeySumsTokensAndCostWithCoalesce()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UsageLogStore(context);

        await store.CreateAsync(Log(3, CreatedAt, 10, 0.125, 711));
        await store.CreateAsync(Log(3, CreatedAt.AddSeconds(1), 20, 0.25, 712));
        await store.CreateAsync(Log(3, CreatedAt.AddSeconds(2), 30, null, 713));
        await store.CreateAsync(Log(4, CreatedAt.AddSeconds(1), 90, 9.0, 714));

        var usage = await store.AggregateByApiKeyAsync(
            3,
            null,
            null,
            endInclusive: true,
            needTokens: true,
            needCost: true);

        Assert.Equal(60L, usage.TotalTokens);
        Assert.Equal(0.375, usage.TotalCost, precision: 10);

        // A NULL total_cost is coalesced to zero, like the reference query.
        var tokensOnly = await store.AggregateByApiKeyAsync(
            3,
            CreatedAt.AddSeconds(2),
            null,
            endInclusive: true,
            needTokens: true,
            needCost: true);
        Assert.Equal(30L, tokensOnly.TotalTokens);
        Assert.Equal(0.0, tokensOnly.TotalCost, precision: 10);
    }

    [Fact]
    public async Task AggregateByApiKeyReturnsZeroWithoutRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UsageLogStore(context);

        var usage = await store.AggregateByApiKeyAsync(
            404,
            null,
            null,
            endInclusive: true,
            needTokens: true,
            needCost: true);

        Assert.Equal(0L, usage.TotalTokens);
        Assert.Equal(0.0, usage.TotalCost);
        Assert.Equal(0L, await store.CountByApiKeyAsync(404, null, null, endInclusive: true));
    }

    [Fact]
    public async Task AggregateByApiKeySkipsUnrequestedColumns()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UsageLogStore(context);

        await store.CreateAsync(Log(3, CreatedAt, 10, 0.5, 721));

        var usage = await store.AggregateByApiKeyAsync(
            3,
            null,
            null,
            endInclusive: true,
            needTokens: false,
            needCost: false);

        Assert.Equal(0L, usage.TotalTokens);
        Assert.Equal(0.0, usage.TotalCost);
    }

    [Fact]
    public async Task SumCostByChannelSumsTheHalfOpenWindowAndCoalescesMissingRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UsageLogStore(context);

        await store.CreateAsync(LogForChannel(7, CreatedAt, 0.25, requestId: 801));
        await store.CreateAsync(LogForChannel(7, CreatedAt.AddSeconds(1), 0.5, requestId: 802));
        await store.CreateAsync(LogForChannel(7, CreatedAt.AddSeconds(2), null, requestId: 803));
        await store.CreateAsync(LogForChannel(8, CreatedAt.AddSeconds(1), 9.0, requestId: 804));

        // channelCostSince queries [since, until), so the row at +2s is out.
        var total = await store.SumCostByChannelAsync(
            7,
            CreatedAt.AddSeconds(1),
            CreatedAt.AddSeconds(2));
        Assert.Equal(0.5, total, precision: 10);

        Assert.Equal(
            0.0,
            await store.SumCostByChannelAsync(7, CreatedAt.AddDays(1), CreatedAt.AddDays(2)));
        Assert.Equal(
            0.0,
            await store.SumCostByChannelAsync(404, CreatedAt, CreatedAt.AddDays(1)));
    }

    [Fact]
    public async Task ListAndGetByIdApplyProjectAndUsageLogVisibility()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UsageLogStore(context);

        await SeedApiKeyAsync(db, 100, type: "personal", userId: 6);
        await SeedApiKeyAsync(db, 101, type: "personal", userId: 5);
        await SeedApiKeyAsync(db, 102, type: "service_account", userId: null);
        await SeedRequestAsync(db, 1, source: "playground", userId: 5);
        await SeedRequestAsync(db, 2, source: "playground", userId: 6);
        await SeedRequestAsync(db, 3, apiKeyId: null);
        await SeedRequestAsync(db, 4, apiKeyId: 100);
        await SeedRequestAsync(db, 5, apiKeyId: 101);
        await SeedRequestAsync(db, 6, apiKeyId: 102);
        await SeedRequestAsync(db, 7, projectId: 2);

        for (var requestId = 1L; requestId <= 7; requestId++)
        {
            await store.CreateAsync(new UsageLogRecord
            {
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                ModelId = "gpt-4o",
                RequestId = requestId,
                ProjectId = requestId == 7 ? 2 : 1,
            });
        }

        // The UsageLogFilter branch of UserProjectScopeReadRequestsRule:
        // Playground rows of the caller and API requests whose key is missing,
        // shared, or not another user's personal key.
        var visible = await store.ListAsync(new UsageLogListQuery { VisibilityUserId = 5 });
        Assert.Equal(
            [1L, 3L, 5L, 6L, 7L],
            visible.Nodes.Select(static row => row.RequestId).ToArray());

        var scoped = await store.ListAsync(new UsageLogListQuery { ProjectScope = 2 });
        Assert.Equal([7L], scoped.Nodes.Select(static row => row.RequestId).ToArray());
        Assert.Equal(1, scoped.TotalCount);

        var unscoped = await store.ListAsync(new UsageLogListQuery());
        Assert.Equal(7, unscoped.TotalCount);

        Assert.Null(await store.GetByIdAsync(4, 1, 5));
        Assert.Equal(5L, (await store.GetByIdAsync(5, 1, 5))?.RequestId);
        Assert.Null(await store.GetByIdAsync(1, 2, null));
        Assert.Equal(7L, (await store.GetByIdAsync(7, 2, null))?.RequestId);
    }

    private static async Task SeedRequestAsync(
        TestDatabase db,
        long id,
        long projectId = 1,
        string source = "api",
        long? userId = null,
        long? apiKeyId = null)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "requests"
                ("id", "created_at", "updated_at", "user_id", "source", "model_id",
                 "request_body", "status", "api_key_id", "project_id")
            VALUES (@Id, @CreatedAt, @CreatedAt, @UserId, @Source, 'gpt-4o',
                    '{}'::jsonb, 'completed', @ApiKeyId, @ProjectId);
            """,
            new
            {
                Id = id,
                CreatedAt,
                UserId = userId,
                Source = source,
                ApiKeyId = apiKeyId,
                ProjectId = projectId,
            });
    }

    private static async Task SeedApiKeyAsync(
        TestDatabase db,
        long id,
        string type,
        long? userId)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "api_keys"
                ("id", "created_at", "updated_at", "key", "name", "type", "user_id")
            VALUES (@Id, @CreatedAt, @CreatedAt, @Key, @Name, @Type, @UserId);
            """,
            new
            {
                Id = id,
                CreatedAt,
                Key = "key-" + id,
                Name = "key-" + id,
                Type = type,
                UserId = userId,
            });
    }

    private static UsageLogRecord Log(
        long apiKeyId,
        DateTime createdAt,
        long totalTokens,
        double? totalCost,
        long requestId)
        => new()
        {
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            ApiKeyId = apiKeyId,
            ModelId = "gpt-4o",
            TotalTokens = totalTokens,
            TotalCost = totalCost,
            RequestId = requestId,
        };

    private static UsageLogRecord LogForChannel(
        long channelId,
        DateTime createdAt,
        double? totalCost,
        long requestId)
        => new()
        {
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            ChannelId = channelId,
            ModelId = "gpt-4o",
            TotalCost = totalCost,
            RequestId = requestId,
        };

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
