using Dapper;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks biz.TraceService.GetOrCreateTrace (internal/server/biz/trace.go) to the
/// traces table: the (trace_id, project_id) lookup, the active/current-thread
/// defaults, the global uniqueness of trace_id and the constraint-error retry.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TraceStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime Now = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    [Fact]
    public async Task CreatesTraceWithReferenceDefaults()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new TraceStore(context, FixedTimeProvider());

        var trace = await store.GetOrCreateAsync(11, "trace-create", null);

        Assert.True(trace.Id > 0);
        Assert.Equal("trace-create", trace.TraceId);
        Assert.Equal(11L, trace.ProjectId);
        Assert.Equal("active", trace.Status);
        Assert.Null(trace.ThreadId);
        Assert.Equal(Now, trace.CreatedAt);
        Assert.Equal(Now, trace.UpdatedAt);

        RecordAssert.Equivalent(trace, await ReadRowAsync(db, "trace-create"));
    }

    [Fact]
    public async Task ThreadIdRoundTrips()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new TraceStore(context, FixedTimeProvider());

        var trace = await store.GetOrCreateAsync(11, "trace-thread", 42L);

        Assert.Equal(42L, trace.ThreadId);
        Assert.Equal(42L, (await ReadRowAsync(db, "trace-thread")).ThreadId);
    }

    [Fact]
    public async Task ExistingTraceIsReturnedUntouched()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new TraceStore(context, FixedTimeProvider());

        var first = await store.GetOrCreateAsync(11, "trace-same", 5L);
        var second = await store.GetOrCreateAsync(11, "trace-same", 9L);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(5L, second.ThreadId);
        Assert.Equal(1L, await CountRowsAsync(db));
    }

    [Fact]
    public async Task TraceIdIsUniqueAcrossProjectsLikeTheEntSchema()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO "traces" ("created_at", "updated_at", "trace_id", "status", "project_id")
                VALUES (@Now, @Now, @TraceId, 'active', 22);
                """,
                new { Now, TraceId = "trace-other-project" });
        }

        var store = new TraceStore(context, FixedTimeProvider());

        // The lookup is scoped to (trace_id, project_id), so it misses; the insert
        // then violates the global unique index on trace_id, and the reference
        // re-runs the same lookup and returns its not-found error.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetOrCreateAsync(11, "trace-other-project", null));
        Assert.Equal("failed to create trace: trace not found", error.Message);
    }

    [Fact]
    public async Task FailedCreateLeavesTheUnitOfWorkUsable()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO "traces" ("created_at", "updated_at", "trace_id", "status", "project_id")
                VALUES (@Now, @Now, @TraceId, 'active', 22);
                """,
                new { Now, TraceId = "trace-occupied" });
        }

        var store = new TraceStore(context, FixedTimeProvider());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetOrCreateAsync(11, "trace-occupied", null));

        // The failed entity must be detached, or this save would retry the same
        // insert and fail forever inside the shared scoped context.
        var trace = await store.GetOrCreateAsync(11, "trace-recovered", null);
        Assert.True(trace.Id > 0);
        Assert.Equal("trace-recovered", (await ReadRowAsync(db, "trace-recovered")).TraceId);
    }

    [Fact]
    public async Task ConcurrentCreatesReturnOneRow()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var firstContext = db.Factory.CreateDbContext();
        await using var secondContext = db.Factory.CreateDbContext();
        var first = new TraceStore(firstContext, FixedTimeProvider());
        var second = new TraceStore(secondContext, FixedTimeProvider());

        var traces = await Task.WhenAll(
            first.GetOrCreateAsync(11, "trace-race", null),
            second.GetOrCreateAsync(11, "trace-race", null));

        Assert.Equal(traces[0].Id, traces[1].Id);
        Assert.Equal(1L, await CountRowsAsync(db));
    }

    private static FixedClock FixedTimeProvider()
        => new(new DateTimeOffset(Now));

    private static async Task<TraceRecord> ReadRowAsync(TestDatabase db, string traceId)
    {
        await using var connection = await db.Factory.OpenAsync();
        return await connection.QuerySingleAsync<TraceRecord>(
            """
            SELECT "id", "created_at", "updated_at", "trace_id", "status", "project_id",
                   "thread_id"
            FROM "traces"
            WHERE "trace_id" = @TraceId;
            """,
            new { TraceId = traceId });
    }

    private static async Task<long> CountRowsAsync(TestDatabase db)
    {
        await using var connection = await db.Factory.OpenAsync();
        return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM \"traces\";");
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
