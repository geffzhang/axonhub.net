using Dapper;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks the Dapper trace admin store to the ent queries of the trace/thread
/// GraphQL surface (internal/server/biz/trace.go and the entgql Trace
/// connection): filters, project scope, keyset pagination, status updates and
/// the thread-scoped lookups.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TraceAdminStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task ListAppliesFiltersScopesAndOrdering()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new TraceAdminStore(context);

        await SeedTraceAsync(db, 1, "trace-a", projectId: 1, threadId: 10, status: "active", CreatedAt);
        await SeedTraceAsync(db, 2, "trace-b", projectId: 1, threadId: 10, status: "archived", CreatedAt.AddSeconds(1));
        await SeedTraceAsync(db, 3, "trace-c", projectId: 2, threadId: 11, status: "active", CreatedAt.AddSeconds(2));
        await SeedTraceAsync(db, 4, "trace-d", projectId: 1, threadId: null, status: "retained", CreatedAt.AddSeconds(3));

        var all = await store.ListAsync(new TraceListQuery());
        Assert.Equal([1L, 2L, 3L, 4L], all.Nodes.Select(static row => row.Id).ToArray());
        Assert.Equal(4, all.TotalCount);
        Assert.False(all.HasNextPage);
        Assert.False(all.HasPreviousPage);

        var first = await store.ListAsync(new TraceListQuery { First = 2 });
        Assert.Equal([1L, 2L], first.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(first.HasNextPage);
        Assert.False(first.HasPreviousPage);

        var next = await store.ListAsync(new TraceListQuery
        {
            After = new ListCursor(2, null),
            First = 2,
        });
        Assert.Equal([3L, 4L], next.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(next.HasPreviousPage);
        Assert.False(next.HasNextPage);

        var scoped = await store.ListAsync(new TraceListQuery { ProjectScope = 1 });
        Assert.Equal([1L, 2L, 4L], scoped.Nodes.Select(static row => row.Id).ToArray());

        var thread = await store.ListAsync(new TraceListQuery { ThreadScope = 10 });
        Assert.Equal([1L, 2L], thread.Nodes.Select(static row => row.Id).ToArray());

        var notArchived = await store.ListAsync(new TraceListQuery
        {
            Where = new TraceListFilter { StatusNEQ = "archived" },
        });
        Assert.Equal([1L, 3L, 4L], notArchived.Nodes.Select(static row => row.Id).ToArray());

        var prefixed = await store.ListAsync(new TraceListQuery
        {
            Where = new TraceListFilter { TraceIdPrefix = "trace-a" },
        });
        Assert.Equal([1L], prefixed.Nodes.Select(static row => row.Id).ToArray());

        var noThread = await store.ListAsync(new TraceListQuery
        {
            Where = new TraceListFilter { ThreadIdIsNil = true },
        });
        Assert.Equal([4L], noThread.Nodes.Select(static row => row.Id).ToArray());

        var since = await store.ListAsync(new TraceListQuery
        {
            Where = new TraceListFilter { CreatedAtGTE = CreatedAt.AddSeconds(2) },
        });
        Assert.Equal([3L, 4L], since.Nodes.Select(static row => row.Id).ToArray());

        // CREATED_AT DESC issues the time keyset cursor.
        var newest = await store.ListAsync(new TraceListQuery
        {
            SortField = TraceSortField.CreatedAt,
            Direction = SortDirection.Desc,
            First = 2,
        });
        Assert.Equal([4L, 3L], newest.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(newest.HasNextPage);

        var older = await store.ListAsync(new TraceListQuery
        {
            SortField = TraceSortField.CreatedAt,
            Direction = SortDirection.Desc,
            After = new ListCursor(4, CreatedAt.AddSeconds(3)),
            First = 2,
        });
        Assert.Equal([3L, 2L], older.Nodes.Select(static row => row.Id).ToArray());

        // last walks backwards and still returns the tail in ascending order.
        var tail = await store.ListAsync(new TraceListQuery
        {
            SortField = TraceSortField.CreatedAt,
            Direction = SortDirection.Asc,
            Last = 2,
        });
        Assert.Equal([3L, 4L], tail.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(tail.HasPreviousPage);
        Assert.False(tail.HasNextPage);
    }

    [Fact]
    public async Task GetByIdHonorsProjectScope()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new TraceAdminStore(context);
        await SeedTraceAsync(db, 7, "trace-7", projectId: 5);

        Assert.Equal(7L, (await store.GetByIdAsync(7, null))?.Id);
        Assert.Equal(7L, (await store.GetByIdAsync(7, 5))?.Id);
        Assert.Null(await store.GetByIdAsync(7, 6));
        Assert.Null(await store.GetByIdAsync(8, null));
    }

    [Fact]
    public async Task UpdateStatusMatchesTheFromStatusAndScope()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new TraceAdminStore(context);
        await SeedTraceAsync(db, 1, "trace-1", projectId: 1, status: "active");
        await SeedTraceAsync(db, 2, "trace-2", projectId: 2, status: "active");

        var affected = await store.UpdateStatusAsync(1, 1, "active", "archived");

        Assert.Equal(1, affected);
        var updated = await store.GetByIdAsync(1, null);
        Assert.NotNull(updated);
        Assert.Equal("archived", updated.Status);
        Assert.True(updated.UpdatedAt > UpdatedAt);

        // A second transition from the same status matches nothing.
        Assert.Equal(0, await store.UpdateStatusAsync(1, 1, "active", "archived"));

        // The mutation policy's project predicate rejects another project.
        Assert.Equal(0, await store.UpdateStatusAsync(2, 1, "active", "archived"));
        Assert.Equal("active", (await store.GetByIdAsync(2, null))?.Status);
    }

    [Fact]
    public async Task ThreadLookupsRespectOrderAndScope()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new TraceAdminStore(context);
        await SeedTraceAsync(db, 1, "trace-1", projectId: 1, threadId: 10, status: "active", CreatedAt);
        await SeedTraceAsync(db, 2, "trace-2", projectId: 1, threadId: 10, status: "archived", CreatedAt.AddSeconds(1));
        await SeedTraceAsync(db, 3, "trace-3", projectId: 2, threadId: 10, status: "archived", CreatedAt.AddSeconds(2));
        await SeedTraceAsync(db, 4, "trace-4", projectId: 1, threadId: 11, status: "active", CreatedAt.AddSeconds(3));

        Assert.Equal(1L, (await store.GetThreadFirstTraceAsync(10, null))?.Id);
        Assert.Equal(3L, (await store.GetThreadFirstTraceAsync(10, 2))?.Id);
        Assert.Null(await store.GetThreadFirstTraceAsync(12, null));

        // The previous trace is the newest one created strictly before the
        // current trace; the current id is excluded and ties are skipped.
        Assert.Equal(1L, (await store.GetPreviousTraceAsync(10, 2, CreatedAt.AddSeconds(1), null))?.Id);
        Assert.Null(await store.GetPreviousTraceAsync(10, 1, CreatedAt, null));
        Assert.Null(await store.GetPreviousTraceAsync(10, 3, CreatedAt.AddSeconds(2), 2));

        Assert.Equal(2L, await store.CountArchivedByThreadAsync(10, null));
        Assert.Equal(1L, await store.CountArchivedByThreadAsync(10, 2));
        Assert.Equal(0L, await store.CountArchivedByThreadAsync(11, null));
    }

    private static async Task SeedTraceAsync(
        TestDatabase db,
        long id,
        string traceId,
        long projectId,
        long? threadId = null,
        string status = "active",
        DateTime? createdAt = null)
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
                CreatedAt = createdAt ?? CreatedAt,
                UpdatedAt,
                TraceId = traceId,
                Status = status,
                ProjectId = projectId,
                ThreadId = threadId,
            });
    }
}
