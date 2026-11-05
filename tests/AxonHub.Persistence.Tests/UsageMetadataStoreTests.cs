using Dapper;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks the usage aggregates behind Trace.usageMetadata / Thread.usageMetadata
/// (internal/server/biz/usage_metadata.go): sums over usage_logs whose request
/// belongs to the trace/thread and completed, with NULL sums coalesced to zero.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UsageMetadataStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public async Task TraceAggregateKeepsOnlyCompletedRequestsOfTheTrace()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UsageMetadataStore(context);

        await SeedRequestAsync(db, 10, traceId: 1, status: "completed");
        await SeedRequestAsync(db, 11, traceId: 1, status: "failed");
        await SeedRequestAsync(db, 12, traceId: 2, status: "completed");
        await SeedRequestAsync(db, 13, traceId: 1, status: "completed");
        await SeedUsageAsync(db, 10, prompt: 10, completion: 20, total: 30, cached: 3, cachedWrite: 4, cost: 0.5);
        await SeedUsageAsync(db, 11, prompt: 1_000, completion: 1_000, total: 2_000, cached: 0, cachedWrite: 0, cost: 9);
        await SeedUsageAsync(db, 12, prompt: 100, completion: 200, total: 300, cached: 0, cachedWrite: 0, cost: 2);
        await SeedUsageAsync(db, 13, prompt: 5, completion: 6, total: 11, cached: 7, cachedWrite: 8, cost: 1.25);

        var trace = await store.AggregateForTraceAsync(1);

        Assert.Equal(15L, trace.TotalInputTokens);
        Assert.Equal(26L, trace.TotalOutputTokens);
        Assert.Equal(41L, trace.TotalTokens);
        Assert.Equal(10L, trace.TotalCachedTokens);
        Assert.Equal(12L, trace.TotalCachedWriteTokens);
        Assert.Equal(1.75, trace.TotalCost, precision: 10);
    }

    [Fact]
    public async Task ThreadAggregateFollowsTheTraceThreadLink()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UsageMetadataStore(context);

        await SeedTraceAsync(db, 1, threadId: 100);
        await SeedTraceAsync(db, 2, threadId: 100);
        await SeedTraceAsync(db, 3, threadId: 200);
        await SeedRequestAsync(db, 10, traceId: 1, status: "completed");
        await SeedRequestAsync(db, 11, traceId: 2, status: "completed");
        await SeedRequestAsync(db, 12, traceId: 3, status: "completed");
        await SeedRequestAsync(db, 13, traceId: 2, status: "failed");
        await SeedUsageAsync(db, 10, prompt: 1, completion: 2, total: 3, cached: 0, cachedWrite: 0, cost: 0.1);
        await SeedUsageAsync(db, 11, prompt: 4, completion: 5, total: 9, cached: 1, cachedWrite: 2, cost: 0.2);
        await SeedUsageAsync(db, 12, prompt: 40, completion: 50, total: 90, cached: 0, cachedWrite: 0, cost: 3);
        await SeedUsageAsync(db, 13, prompt: 400, completion: 500, total: 900, cached: 9, cachedWrite: 9, cost: 30);

        var thread = await store.AggregateForThreadAsync(100);

        Assert.Equal(5L, thread.TotalInputTokens);
        Assert.Equal(7L, thread.TotalOutputTokens);
        Assert.Equal(12L, thread.TotalTokens);
        Assert.Equal(1L, thread.TotalCachedTokens);
        Assert.Equal(2L, thread.TotalCachedWriteTokens);
        Assert.Equal(0.3, thread.TotalCost, precision: 10);
    }

    [Fact]
    public async Task EmptyAggregatesReturnTheZeroRow()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new UsageMetadataStore(context);

        var trace = await store.AggregateForTraceAsync(999);
        var thread = await store.AggregateForThreadAsync(999);

        Assert.Equal(default, trace);
        Assert.Equal(default, thread);
    }

    private static async Task SeedTraceAsync(TestDatabase db, long id, long? threadId)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "traces"
                ("id", "created_at", "updated_at", "trace_id", "status", "project_id", "thread_id")
            VALUES (@Id, @CreatedAt, @CreatedAt, @TraceId, 'active', 1, @ThreadId);
            """,
            new { Id = id, CreatedAt, TraceId = "trace-" + id, ThreadId = threadId });
    }

    private static async Task SeedRequestAsync(
        TestDatabase db,
        long id,
        long traceId,
        string status)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "requests"
                ("id", "created_at", "updated_at", "model_id", "request_body",
                 "status", "project_id", "trace_id")
            VALUES (@Id, @CreatedAt, @CreatedAt, 'gpt-4o', '{}'::jsonb,
                    @Status, 1, @TraceId);
            """,
            new { Id = id, CreatedAt, Status = status, TraceId = traceId });
    }

    private static async Task SeedUsageAsync(
        TestDatabase db,
        long requestId,
        long prompt,
        long completion,
        long total,
        long cached,
        long cachedWrite,
        double cost)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "usage_logs"
                ("created_at", "updated_at", "model_id", "prompt_tokens",
                 "completion_tokens", "total_tokens", "prompt_cached_tokens",
                 "prompt_write_cached_tokens", "total_cost", "project_id", "request_id")
            VALUES (@CreatedAt, @CreatedAt, 'gpt-4o', @Prompt, @Completion, @Total,
                    @Cached, @CachedWrite, @Cost, 1, @RequestId);
            """,
            new
            {
                CreatedAt,
                Prompt = prompt,
                Completion = completion,
                Total = total,
                Cached = cached,
                CachedWrite = cachedWrite,
                Cost = cost,
                RequestId = requestId,
            });
    }
}
