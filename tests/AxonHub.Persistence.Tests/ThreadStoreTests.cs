using Dapper;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks biz.ThreadService.GetOrCreateThread (internal/server/biz/thread.go) to
/// the threads table: the (thread_id, project_id) lookup, the active defaults,
/// the global uniqueness of thread_id and the constraint-error retry.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ThreadStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime Now = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

    [Fact]
    public async Task CreatesThreadWithReferenceDefaults()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ThreadStore(context, FixedTimeProvider());

        var thread = await store.GetOrCreateAsync(11, "thread-create");

        Assert.True(thread.Id > 0);
        Assert.Equal("thread-create", thread.ThreadId);
        Assert.Equal(11L, thread.ProjectId);
        Assert.Equal("active", thread.Status);
        Assert.Equal(Now, thread.CreatedAt);
        Assert.Equal(Now, thread.UpdatedAt);

        RecordAssert.Equivalent(thread, await ReadRowAsync(db, "thread-create"));
    }

    [Fact]
    public async Task ExistingThreadIsReturnedUntouched()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ThreadStore(context, FixedTimeProvider());

        var first = await store.GetOrCreateAsync(11, "thread-same");
        var second = await store.GetOrCreateAsync(11, "thread-same");

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1L, await CountRowsAsync(db));
    }

    [Fact]
    public async Task ThreadIdIsUniqueAcrossProjectsLikeTheEntSchema()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO "threads" ("created_at", "updated_at", "thread_id", "status", "project_id")
                VALUES (@Now, @Now, @ThreadId, 'active', 22);
                """,
                new { Now, ThreadId = "thread-other-project" });
        }

        var store = new ThreadStore(context, FixedTimeProvider());

        // The lookup is scoped to (thread_id, project_id), so it misses; the
        // insert then violates the global unique index on thread_id, and the
        // reference re-runs the same lookup and returns its not-found error.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetOrCreateAsync(11, "thread-other-project"));
        Assert.Equal("failed to create thread: thread not found", error.Message);
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
                INSERT INTO "threads" ("created_at", "updated_at", "thread_id", "status", "project_id")
                VALUES (@Now, @Now, @ThreadId, 'active', 22);
                """,
                new { Now, ThreadId = "thread-occupied" });
        }

        var store = new ThreadStore(context, FixedTimeProvider());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.GetOrCreateAsync(11, "thread-occupied"));

        // The failed entity must be detached, or this save would retry the same
        // insert and fail forever inside the shared scoped context.
        var thread = await store.GetOrCreateAsync(11, "thread-recovered");
        Assert.True(thread.Id > 0);
        Assert.Equal("thread-recovered", (await ReadRowAsync(db, "thread-recovered")).ThreadId);
    }

    [Fact]
    public async Task ConcurrentCreatesReturnOneRow()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var firstContext = db.Factory.CreateDbContext();
        await using var secondContext = db.Factory.CreateDbContext();
        var first = new ThreadStore(firstContext, FixedTimeProvider());
        var second = new ThreadStore(secondContext, FixedTimeProvider());

        var threads = await Task.WhenAll(
            first.GetOrCreateAsync(11, "thread-race"),
            second.GetOrCreateAsync(11, "thread-race"));

        Assert.Equal(threads[0].Id, threads[1].Id);
        Assert.Equal(1L, await CountRowsAsync(db));
    }

    private static FixedClock FixedTimeProvider()
        => new(new DateTimeOffset(Now));

    private static async Task<ThreadRecord> ReadRowAsync(TestDatabase db, string threadId)
    {
        await using var connection = await db.Factory.OpenAsync();
        return await connection.QuerySingleAsync<ThreadRecord>(
            """
            SELECT "id", "created_at", "updated_at", "thread_id", "status", "project_id"
            FROM "threads"
            WHERE "thread_id" = @ThreadId;
            """,
            new { ThreadId = threadId });
    }

    private static async Task<long> CountRowsAsync(TestDatabase db)
    {
        await using var connection = await db.Factory.OpenAsync();
        return await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM \"threads\";");
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
