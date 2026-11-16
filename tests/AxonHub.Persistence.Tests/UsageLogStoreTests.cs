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

    [Fact]
    public async Task ListAppliesEveryScalarPredicate()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UsageLogStore(context);

        var first = new UsageLogRecord
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
            Source = "api",
            Format = "openai/chat_completions",
            TotalCost = 0.125,
            CostPriceReferenceId = "price-alpha",
            ChannelId = 300,
            ProjectId = 1,
            RequestId = 101,
        };
        var firstId = await store.CreateAsync(first);

        var second = new UsageLogRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            ApiKeyId = null,
            ModelId = "claude-4",
            PromptTokens = 200,
            CompletionTokens = 100,
            TotalTokens = 300,
            Source = "playground",
            Format = "anthropic/messages",
            TotalCost = null,
            CostPriceReferenceId = null,
            ChannelId = 301,
            ProjectId = 2,
            RequestId = 201,
        };
        var secondId = await store.CreateAsync(second);

        var third = new UsageLogRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            ApiKeyId = 4,
            ModelId = "gpt-4o-mini",
            PromptTokens = 10,
            CompletionTokens = 5,
            TotalTokens = 15,
            PromptAudioTokens = 0,
            Source = "api",
            Format = "openai/chat_completions",
            TotalCost = 2.5,
            CostPriceReferenceId = "price-beta",
            ChannelId = 300,
            ProjectId = 1,
            RequestId = 301,
        };
        var thirdId = await store.CreateAsync(third);

        var fourth = new UsageLogRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            ApiKeyId = 5,
            ModelId = "o3",
            PromptTokens = 0,
            CompletionTokens = 0,
            TotalTokens = 0,
            PromptAudioTokens = 0,
            Source = "api",
            Format = "openai/responses",
            TotalCost = 0.5,
            CostPriceReferenceId = null,
            ChannelId = null,
            ProjectId = 1,
            RequestId = 401,
        };
        var fourthId = await store.CreateAsync(fourth);

        // The detail columns carry a schema default of 0, so reach NULL through
        // SQL to exercise ent's IsNil/NotNil predicates.
        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                UPDATE "usage_logs"
                SET "prompt_audio_tokens" = NULL,
                    "prompt_cached_tokens" = NULL,
                    "prompt_write_cached_tokens" = NULL,
                    "prompt_write_cached_tokens_5m" = NULL,
                    "prompt_write_cached_tokens_1h" = NULL,
                    "completion_audio_tokens" = NULL,
                    "completion_reasoning_tokens" = NULL,
                    "completion_accepted_prediction_tokens" = NULL,
                    "completion_rejected_prediction_tokens" = NULL
                WHERE "id" = @Id;
                """,
                new { Id = secondId });
        }

        async Task<IReadOnlyList<long>> QueryAsync(UsageLogListFilter where)
            => (await store.ListAsync(new UsageLogListQuery { Where = where }))
                .Nodes.Select(static row => row.Id).ToArray();

        Assert.Equal([firstId], await QueryAsync(new UsageLogListFilter { ApiKeyId = 3 }));
        Assert.Equal([thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { ApiKeyIdNEQ = 3 }));
        Assert.Equal([firstId, thirdId],
            await QueryAsync(new UsageLogListFilter { ApiKeyIdIn = [3, 4] }));
        Assert.Equal([thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { ApiKeyIdNotIn = [3] }));
        Assert.Equal([thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { ApiKeyIdGT = 3 }));
        Assert.Equal([thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { ApiKeyIdGTE = 4 }));
        Assert.Empty(await QueryAsync(new UsageLogListFilter { ApiKeyIdLT = 3 }));
        Assert.Equal([firstId], await QueryAsync(new UsageLogListFilter { ApiKeyIdLTE = 3 }));
        Assert.Equal([secondId], await QueryAsync(new UsageLogListFilter { ApiKeyIdIsNil = true }));
        Assert.Equal([firstId, thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { ApiKeyIdNotNil = true }));

        Assert.Equal([firstId], await QueryAsync(new UsageLogListFilter { ModelId = "gpt-4o" }));
        Assert.Equal([secondId, thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { ModelIdNEQ = "gpt-4o" }));
        Assert.Equal([secondId, fourthId],
            await QueryAsync(new UsageLogListFilter { ModelIdIn = ["claude-4", "o3"] }));
        Assert.Equal([firstId, thirdId],
            await QueryAsync(new UsageLogListFilter { ModelIdContains = "gpt" }));
        Assert.Equal([firstId, thirdId],
            await QueryAsync(new UsageLogListFilter { ModelIdContainsFold = "GPT" }));
        Assert.Equal([secondId],
            await QueryAsync(new UsageLogListFilter { ModelIdHasPrefix = "claude" }));
        Assert.Equal([thirdId],
            await QueryAsync(new UsageLogListFilter { ModelIdHasSuffix = "mini" }));
        Assert.Equal([fourthId], await QueryAsync(new UsageLogListFilter { ModelIdEqualFold = "O3" }));

        Assert.Equal([firstId], await QueryAsync(new UsageLogListFilter { PromptTokens = 100 }));
        Assert.Equal([firstId, thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { PromptTokensNEQ = 200 }));
        Assert.Equal([firstId, secondId],
            await QueryAsync(new UsageLogListFilter { PromptTokensGT = 10 }));
        Assert.Equal([thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { PromptTokensLTE = 10 }));
        Assert.Equal([firstId, secondId],
            await QueryAsync(new UsageLogListFilter { CompletionTokensGT = 5 }));
        Assert.Equal([thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { CompletionTokensLT = 50 }));
        Assert.Equal([secondId],
            await QueryAsync(new UsageLogListFilter { TotalTokensIn = [300] }));
        Assert.Equal([firstId, thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { TotalTokensNotIn = [300] }));
        Assert.Equal([firstId, secondId],
            await QueryAsync(new UsageLogListFilter { TotalTokensGTE = 150 }));
        Assert.Equal([thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { TotalTokensLT = 50 }));

        Assert.Equal([firstId], await QueryAsync(new UsageLogListFilter { PromptAudioTokens = 1 }));
        Assert.Equal([thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { PromptAudioTokensNEQ = 1 }));
        Assert.Equal([secondId],
            await QueryAsync(new UsageLogListFilter { PromptAudioTokensIsNil = true }));
        Assert.Equal([firstId, thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { PromptAudioTokensNotNil = true }));
        Assert.Equal([thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { PromptAudioTokensLT = 1 }));
        Assert.Equal([firstId], await QueryAsync(new UsageLogListFilter { PromptCachedTokens = 2 }));
        Assert.Equal([firstId],
            await QueryAsync(new UsageLogListFilter { PromptWriteCachedTokens = 3 }));
        Assert.Equal([firstId],
            await QueryAsync(new UsageLogListFilter { PromptWriteCachedTokens5m = 4 }));
        Assert.Equal([firstId],
            await QueryAsync(new UsageLogListFilter { PromptWriteCachedTokens1h = 5 }));
        Assert.Equal([firstId],
            await QueryAsync(new UsageLogListFilter { CompletionAudioTokens = 6 }));
        Assert.Equal([firstId],
            await QueryAsync(new UsageLogListFilter { CompletionReasoningTokens = 7 }));
        Assert.Equal([firstId],
            await QueryAsync(new UsageLogListFilter { CompletionAcceptedPredictionTokens = 8 }));
        Assert.Equal([firstId],
            await QueryAsync(new UsageLogListFilter { CompletionRejectedPredictionTokens = 9 }));

        Assert.Equal([secondId], await QueryAsync(new UsageLogListFilter { SourceIn = ["playground"] }));
        Assert.Equal([secondId], await QueryAsync(new UsageLogListFilter { SourceNEQ = "api" }));
        Assert.Equal([firstId, thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { SourceNotIn = ["playground"] }));

        Assert.Equal([secondId], await QueryAsync(new UsageLogListFilter { FormatContains = "anthropic" }));
        Assert.Equal([firstId, thirdId],
            await QueryAsync(new UsageLogListFilter { Format = "openai/chat_completions" }));
        Assert.Equal([secondId, fourthId],
            await QueryAsync(new UsageLogListFilter { FormatNEQ = "openai/chat_completions" }));

        Assert.Equal([firstId], await QueryAsync(new UsageLogListFilter { TotalCost = 0.125 }));
        Assert.Equal([thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { TotalCostNEQ = 0.125 }));
        Assert.Equal([thirdId], await QueryAsync(new UsageLogListFilter { TotalCostIn = [2.5] }));
        Assert.Equal([thirdId], await QueryAsync(new UsageLogListFilter { TotalCostGT = 1 }));
        Assert.Equal([thirdId], await QueryAsync(new UsageLogListFilter { TotalCostGTE = 2.5 }));
        Assert.Equal([firstId, fourthId],
            await QueryAsync(new UsageLogListFilter { TotalCostLT = 1 }));
        Assert.Equal([firstId], await QueryAsync(new UsageLogListFilter { TotalCostLTE = 0.125 }));
        Assert.Equal([secondId], await QueryAsync(new UsageLogListFilter { TotalCostIsNil = true }));
        Assert.Equal([firstId, thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { TotalCostNotNil = true }));

        Assert.Equal([firstId],
            await QueryAsync(new UsageLogListFilter { CostPriceReferenceId = "price-alpha" }));
        Assert.Equal([firstId, thirdId],
            await QueryAsync(new UsageLogListFilter { CostPriceReferenceIdContains = "price" }));
        Assert.Equal([firstId],
            await QueryAsync(new UsageLogListFilter { CostPriceReferenceIdHasPrefix = "price-a" }));
        Assert.Equal([thirdId],
            await QueryAsync(new UsageLogListFilter { CostPriceReferenceIdHasSuffix = "-beta" }));
        Assert.Equal([firstId],
            await QueryAsync(new UsageLogListFilter { CostPriceReferenceIdEqualFold = "PRICE-ALPHA" }));
        Assert.Equal([thirdId],
            await QueryAsync(new UsageLogListFilter { CostPriceReferenceIdContainsFold = "BETA" }));
        Assert.Equal([secondId, fourthId],
            await QueryAsync(new UsageLogListFilter { CostPriceReferenceIdIsNil = true }));
        Assert.Equal([firstId, thirdId],
            await QueryAsync(new UsageLogListFilter { CostPriceReferenceIdNotNil = true }));

        // entgql ignores empty In lists.
        Assert.Equal([firstId, secondId, thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { SourceIn = [] }));
        Assert.Equal([firstId, secondId, thirdId, fourthId],
            await QueryAsync(new UsageLogListFilter { PromptTokensIn = [] }));
    }

    [Fact]
    public async Task ListAppliesTheEdgePredicates()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UsageLogStore(context);

        await SeedChannelRowAsync(db, 300, "Alpha");
        // Soft-deleted targets still satisfy nested edge predicates.
        await SeedChannelRowAsync(db, 301, "Beta", deletedAt: 99);
        await SeedProjectRowAsync(db, 1, "Alpha");
        await SeedProjectRowAsync(db, 2, "Ghost", deletedAt: 99);
        await SeedRequestRowAsync(db, 101, "completed", channelId: 300);
        await SeedRequestRowAsync(db, 201, "failed", channelId: 301);
        await SeedRequestRowAsync(db, 301, "completed", channelId: 300);

        var first = NewUsageLog(101);
        first.ProjectId = 1;
        first.ChannelId = 300;
        var firstId = await store.CreateAsync(first);

        var second = NewUsageLog(201);
        second.ProjectId = 2;
        second.ChannelId = 301;
        var secondId = await store.CreateAsync(second);

        var third = NewUsageLog(301);
        third.ProjectId = 1;
        third.ChannelId = null;
        var thirdId = await store.CreateAsync(third);

        async Task<IReadOnlyList<long>> QueryAsync(UsageLogListFilter where)
            => (await store.ListAsync(new UsageLogListQuery { Where = where }))
                .Nodes.Select(static row => row.Id).ToArray();

        Assert.Equal([firstId, secondId, thirdId],
            await QueryAsync(new UsageLogListFilter { HasRequest = true }));
        Assert.Empty(await QueryAsync(new UsageLogListFilter { HasRequest = false }));
        Assert.Equal([firstId, thirdId],
            await QueryAsync(new UsageLogListFilter
            {
                HasRequestWith = [new RequestListFilter { Status = "completed" }],
            }));
        Assert.Equal([firstId, thirdId],
            await QueryAsync(new UsageLogListFilter
            {
                HasRequestWith =
                [
                    new RequestListFilter
                    {
                        HasChannelWith = [new ChannelListFilter { Name = "Alpha" }],
                    },
                ],
            }));

        Assert.Equal([firstId, secondId, thirdId],
            await QueryAsync(new UsageLogListFilter { HasProject = true }));
        Assert.Equal([firstId, thirdId],
            await QueryAsync(new UsageLogListFilter
            {
                HasProjectWith = [new ProjectListFilter { Name = "Alpha" }],
            }));
        Assert.Equal([secondId],
            await QueryAsync(new UsageLogListFilter
            {
                HasProjectWith = [new ProjectListFilter { Name = "Ghost" }],
            }));

        Assert.Equal([firstId, secondId],
            await QueryAsync(new UsageLogListFilter { HasChannel = true }));
        Assert.Equal([thirdId], await QueryAsync(new UsageLogListFilter { HasChannel = false }));
        Assert.Equal([firstId],
            await QueryAsync(new UsageLogListFilter
            {
                HasChannelWith = [new ChannelListFilter { Name = "Alpha" }],
            }));
        Assert.Equal([secondId],
            await QueryAsync(new UsageLogListFilter
            {
                HasChannelWith = [new ChannelListFilter { Name = "Beta" }],
            }));
        Assert.Equal([firstId, secondId],
            await QueryAsync(new UsageLogListFilter
            {
                HasChannelWith = [new ChannelListFilter { Status = "enabled" }],
            }));

        Assert.Equal([firstId, secondId, thirdId],
            await QueryAsync(new UsageLogListFilter { HasChannelWith = [] }));
        Assert.Equal([thirdId],
            await QueryAsync(new UsageLogListFilter
            {
                Not = new UsageLogListFilter { HasChannel = true },
            }));
        Assert.Equal([firstId, secondId],
            await QueryAsync(new UsageLogListFilter
            {
                Or =
                [
                    new UsageLogListFilter
                    {
                        HasProjectWith = [new ProjectListFilter { Name = "Ghost" }],
                    },
                    new UsageLogListFilter
                    {
                        HasChannelWith = [new ChannelListFilter { Name = "Alpha" }],
                    },
                ],
            }));
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

    private static async Task SeedRequestRowAsync(
        TestDatabase db,
        long id,
        string status,
        long? channelId = null)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "requests"
                ("id", "created_at", "updated_at", "source", "model_id", "request_body",
                 "status", "channel_id", "project_id")
            VALUES (@Id, @CreatedAt, @CreatedAt, 'api', 'gpt-4o', '{}'::jsonb, @Status,
                    @ChannelId, 1);
            """,
            new { Id = id, CreatedAt, Status = status, ChannelId = channelId });
    }

    private static async Task SeedChannelRowAsync(
        TestDatabase db,
        long id,
        string name,
        long deletedAt = 0)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "channels"
                ("id", "created_at", "updated_at", "deleted_at", "type", "name", "status",
                 "credentials", "supported_models", "default_test_model")
            VALUES (@Id, @CreatedAt, @CreatedAt, @DeletedAt, 'openai', @Name, 'enabled',
                    '{}'::jsonb, 'null'::jsonb, 'gpt-4o');
            """,
            new { Id = id, CreatedAt, Name = name, DeletedAt = deletedAt });
    }

    private static async Task SeedProjectRowAsync(
        TestDatabase db,
        long id,
        string name,
        long deletedAt = 0)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "projects"
                ("id", "created_at", "updated_at", "deleted_at", "name", "description", "status")
            VALUES (@Id, @CreatedAt, @CreatedAt, @DeletedAt, @Name, '', 'active');
            """,
            new { Id = id, CreatedAt, Name = name, DeletedAt = deletedAt });
    }
}
