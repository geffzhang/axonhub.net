using Dapper;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks the Dapper thread admin store to the reference ThreadService
/// transitions (internal/server/biz/thread.go:126-222): the status-predicated
/// thread update, the same-status trace cascade inside one transaction and the
/// distinct cascade error marker.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ThreadAdminStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task ListAppliesFiltersScopesAndOrdering()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ThreadAdminStore(context);

        await SeedThreadAsync(db, 1, "thread-a", projectId: 1, status: "active", CreatedAt);
        await SeedThreadAsync(db, 2, "thread-b", projectId: 1, status: "archived", CreatedAt.AddSeconds(1));
        await SeedThreadAsync(db, 3, "thread-c", projectId: 2, status: "active", CreatedAt.AddSeconds(2));

        var all = await store.ListAsync(new ThreadListQuery());
        Assert.Equal([1L, 2L, 3L], all.Nodes.Select(static row => row.Id).ToArray());
        Assert.Equal(3, all.TotalCount);

        var first = await store.ListAsync(new ThreadListQuery { First = 2 });
        Assert.Equal([1L, 2L], first.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(first.HasNextPage);

        var next = await store.ListAsync(new ThreadListQuery
        {
            After = new ListCursor(2, null),
            First = 2,
        });
        Assert.Equal([3L], next.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(next.HasPreviousPage);
        Assert.False(next.HasNextPage);

        var scoped = await store.ListAsync(new ThreadListQuery { ProjectScope = 1 });
        Assert.Equal([1L, 2L], scoped.Nodes.Select(static row => row.Id).ToArray());

        var notArchived = await store.ListAsync(new ThreadListQuery
        {
            Where = new ThreadListFilter { StatusNEQ = "archived" },
        });
        Assert.Equal([1L, 3L], notArchived.Nodes.Select(static row => row.Id).ToArray());

        var prefixed = await store.ListAsync(new ThreadListQuery
        {
            Where = new ThreadListFilter { ThreadIdPrefix = "thread-b" },
        });
        Assert.Equal([2L], prefixed.Nodes.Select(static row => row.Id).ToArray());

        var newest = await store.ListAsync(new ThreadListQuery
        {
            SortField = ThreadSortField.CreatedAt,
            Direction = SortDirection.Desc,
            First = 2,
        });
        Assert.Equal([3L, 2L], newest.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(newest.HasNextPage);

        var tail = await store.ListAsync(new ThreadListQuery
        {
            SortField = ThreadSortField.CreatedAt,
            Direction = SortDirection.Asc,
            Last = 2,
        });
        Assert.Equal([2L, 3L], tail.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(tail.HasPreviousPage);
        Assert.False(tail.HasNextPage);
    }

    [Fact]
    public async Task GetByIdHonorsProjectScope()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ThreadAdminStore(context);
        await SeedThreadAsync(db, 7, "thread-7", projectId: 5);

        Assert.Equal(7L, (await store.GetByIdAsync(7, null))?.Id);
        Assert.Equal(7L, (await store.GetByIdAsync(7, 5))?.Id);
        Assert.Null(await store.GetByIdAsync(7, 6));
        Assert.Null(await store.GetByIdAsync(8, null));
    }

    [Fact]
    public async Task UpdateStatusCascadesToTheThreadTracesOfTheSameStatus()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ThreadAdminStore(context);
        await SeedThreadAsync(db, 1, "thread-1", projectId: 1, status: "active");
        await SeedTraceAsync(db, 1, "trace-1", projectId: 1, threadId: 1, status: "active");
        await SeedTraceAsync(db, 2, "trace-2", projectId: 1, threadId: 1, status: "retained");
        await SeedTraceAsync(db, 3, "trace-3", projectId: 1, threadId: 2, status: "active");

        var affected = await store.UpdateStatusAndCascadeAsync(1, 1, "active", "archived");

        Assert.Equal(1, affected);
        var thread = await store.GetByIdAsync(1, null);
        Assert.NotNull(thread);
        Assert.Equal("archived", thread.Status);
        Assert.True(thread.UpdatedAt > UpdatedAt);

        Assert.Equal("archived", (await ReadTraceStatusAsync(db, 1)));
        Assert.Equal("retained", await ReadTraceStatusAsync(db, 2));
        Assert.Equal("active", await ReadTraceStatusAsync(db, 3));

        // The predicate requires the thread's current status to equal from.
        Assert.Equal(0, await store.UpdateStatusAndCascadeAsync(1, 1, "active", "archived"));
        Assert.Equal("archived", await ReadTraceStatusAsync(db, 1));
    }

    [Fact]
    public async Task CascadeAppliesTheTraceMutationProjectPredicate()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ThreadAdminStore(context);
        await SeedThreadAsync(db, 1, "thread-1", projectId: 1, status: "active");
        await SeedTraceAsync(db, 1, "trace-1", projectId: 1, threadId: 1, status: "active");
        await SeedTraceAsync(db, 2, "trace-2", projectId: 2, threadId: 1, status: "active");

        Assert.Equal(1, await store.UpdateStatusAndCascadeAsync(1, 1, "active", "archived"));

        Assert.Equal("archived", await ReadTraceStatusAsync(db, 1));
        Assert.Equal("active", await ReadTraceStatusAsync(db, 2));

        // The thread update itself is project-scoped too.
        await SeedThreadAsync(db, 2, "thread-2", projectId: 1, status: "active");
        Assert.Equal(0, await store.UpdateStatusAndCascadeAsync(2, 9, "active", "archived"));
        Assert.Equal("active", (await store.GetByIdAsync(2, null))?.Status);
    }

    [Fact]
    public async Task CascadeFailureRollsTheThreadUpdateBack()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ThreadAdminStore(context);
        await SeedThreadAsync(db, 1, "thread-1", projectId: 1, status: "active");
        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync("DROP TABLE \"traces\";");
        }

        var error = await Assert.ThrowsAsync<TraceCascadeException>(
            () => store.UpdateStatusAndCascadeAsync(1, 1, "active", "archived"));

        Assert.Contains("traces", error.InnerException?.Message ?? error.Message);
        var thread = await store.GetByIdAsync(1, null);
        Assert.Equal("active", thread?.Status);
    }

    private static async Task SeedThreadAsync(
        TestDatabase db,
        long id,
        string threadId,
        long projectId,
        string status = "active",
        DateTime? createdAt = null)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "threads"
                ("id", "created_at", "updated_at", "thread_id", "status", "project_id")
            VALUES (@Id, @CreatedAt, @UpdatedAt, @ThreadId, @Status, @ProjectId);
            """,
            new
            {
                Id = id,
                CreatedAt = createdAt ?? CreatedAt,
                UpdatedAt,
                ThreadId = threadId,
                Status = status,
                ProjectId = projectId,
            });
    }

    private static async Task SeedTraceAsync(
        TestDatabase db,
        long id,
        string traceId,
        long projectId,
        long? threadId,
        string status = "active")
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "traces"
                ("id", "created_at", "updated_at", "trace_id", "status", "project_id", "thread_id")
            VALUES (@Id, @CreatedAt, @UpdatedAt, @TraceId, @Status, @ProjectId, @ThreadId);
            """,
            new
            {
                Id = id,
                CreatedAt,
                UpdatedAt,
                TraceId = traceId,
                Status = status,
                ProjectId = projectId,
                ThreadId = threadId,
            });
    }

    private static async Task<string?> ReadTraceStatusAsync(TestDatabase db, long id)
    {
        await using var connection = await db.Factory.OpenAsync();
        return await connection.ExecuteScalarAsync<string>(
            "SELECT \"status\" FROM \"traces\" WHERE \"id\" = @Id;",
            new { Id = id });
    }
}
