using Dapper;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks the Dapper request store behind the trace/thread surfaces: the
/// completed trace reads used by the segment builder and usage lookups, the
/// entgql Request connection filters (including the time predicates), the
/// project/thread scopes and the UserProjectScopeReadRequestsRule visibility
/// predicate.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RequestSegmentStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    [Fact]
    public async Task CompletedTraceReadsKeepCreatedAtOrderAndScope()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestSegmentStore(context);

        await SeedRequestAsync(db, 21, traceId: 7, status: "completed", createdAt: CreatedAt.AddSeconds(1));
        await SeedRequestAsync(db, 22, traceId: 7, status: "failed", createdAt: CreatedAt);
        await SeedRequestAsync(db, 23, traceId: 7, status: "completed", createdAt: CreatedAt);
        await SeedRequestAsync(db, 24, traceId: 7, status: "completed", projectId: 2, createdAt: CreatedAt.AddSeconds(2));
        await SeedRequestAsync(db, 25, traceId: 7, status: "completed", createdAt: CreatedAt.AddSeconds(3));
        await SeedRequestAsync(db, 26, traceId: 8, status: "completed", createdAt: CreatedAt.AddSeconds(4));

        var all = await store.ListCompletedByTraceAsync(7, null, null);
        Assert.Equal([23L, 21L, 24L, 25L], all.Select(static row => row.Id).ToArray());

        var scoped = await store.ListCompletedByTraceAsync(7, 1, null);
        Assert.Equal([23L, 21L, 25L], scoped.Select(static row => row.Id).ToArray());

        Assert.Equal(23L, (await store.GetFirstCompletedByTraceAsync(7, null, null))?.Id);
        Assert.Equal(25L, (await store.GetLastCompletedByTraceAsync(7, null, null))?.Id);
        Assert.Equal(25L, (await store.GetLastCompletedByTraceAsync(7, 1, null))?.Id);
        Assert.Null(await store.GetFirstCompletedByTraceAsync(99, null, null));
    }

    [Fact]
    public async Task ListAppliesFiltersScopesTimestampsAndKeyset()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestSegmentStore(context);

        await SeedTraceAsync(db, 100, threadId: 500);
        await SeedTraceAsync(db, 101, threadId: 500);
        await SeedTraceAsync(db, 102, threadId: 501);
        await SeedRequestAsync(db, 1, traceId: 100, status: "completed", createdAt: CreatedAt);
        await SeedRequestAsync(db, 2, traceId: 100, status: "failed", createdAt: CreatedAt.AddSeconds(1));
        await SeedRequestAsync(db, 3, traceId: 100, status: "completed", projectId: 2, createdAt: CreatedAt.AddSeconds(2));
        await SeedRequestAsync(db, 4, traceId: 101, status: "processing", createdAt: CreatedAt.AddSeconds(3));
        await SeedRequestAsync(db, 5, traceId: 102, status: "completed", createdAt: CreatedAt.AddSeconds(4));

        var traceScope = await store.ListAsync(new RequestListQuery { TraceScope = 100 });
        Assert.Equal([1L, 2L, 3L], traceScope.Nodes.Select(static row => row.Id).ToArray());
        Assert.Equal(3, traceScope.TotalCount);

        var threadScope = await store.ListAsync(new RequestListQuery { ThreadScope = 500 });
        Assert.Equal([1L, 2L, 3L, 4L], threadScope.Nodes.Select(static row => row.Id).ToArray());

        var scoped = await store.ListAsync(new RequestListQuery
        {
            TraceScope = 100,
            ProjectScope = 1,
        });
        Assert.Equal([1L, 2L], scoped.Nodes.Select(static row => row.Id).ToArray());

        var completed = await store.ListAsync(new RequestListQuery
        {
            TraceScope = 100,
            Where = new RequestListFilter { Status = "completed" },
        });
        Assert.Equal([1L, 3L], completed.Nodes.Select(static row => row.Id).ToArray());

        var inList = await store.ListAsync(new RequestListQuery
        {
            Where = new RequestListFilter { StatusIn = ["failed", "processing"] },
        });
        Assert.Equal([2L, 4L], inList.Nodes.Select(static row => row.Id).ToArray());

        var since = await store.ListAsync(new RequestListQuery
        {
            TraceScope = 100,
            Where = new RequestListFilter { CreatedAtGTE = CreatedAt.AddSeconds(2) },
        });
        Assert.Equal([3L], since.Nodes.Select(static row => row.Id).ToArray());

        var beforeUpdate = await store.ListAsync(new RequestListQuery
        {
            TraceScope = 100,
            Where = new RequestListFilter { UpdatedAtLT = CreatedAt.AddSeconds(2) },
        });
        Assert.Equal([1L, 2L], beforeUpdate.Nodes.Select(static row => row.Id).ToArray());

        var notCompleted = await store.ListAsync(new RequestListQuery
        {
            TraceScope = 100,
            Where = new RequestListFilter
            {
                Not = new RequestListFilter { Status = "completed" },
            },
        });
        Assert.Equal([2L], notCompleted.Nodes.Select(static row => row.Id).ToArray());

        var and = await store.ListAsync(new RequestListQuery
        {
            TraceScope = 100,
            Where = new RequestListFilter
            {
                And =
                [
                    new RequestListFilter { Status = "completed" },
                    new RequestListFilter { IdGT = 1 },
                ],
            },
        });
        Assert.Equal([3L], and.Nodes.Select(static row => row.Id).ToArray());

        var or = await store.ListAsync(new RequestListQuery
        {
            Where = new RequestListFilter
            {
                Or =
                [
                    new RequestListFilter { Id = 1 },
                    new RequestListFilter { Id = 4 },
                ],
            },
        });
        Assert.Equal([1L, 4L], or.Nodes.Select(static row => row.Id).ToArray());

        var newest = await store.ListAsync(new RequestListQuery
        {
            SortField = RequestSortField.CreatedAt,
            Direction = SortDirection.Desc,
            First = 2,
        });
        Assert.Equal([5L, 4L], newest.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(newest.HasNextPage);

        var older = await store.ListAsync(new RequestListQuery
        {
            SortField = RequestSortField.CreatedAt,
            Direction = SortDirection.Desc,
            After = new ListCursor(5, CreatedAt.AddSeconds(4)),
            First = 2,
        });
        Assert.Equal([4L, 3L], older.Nodes.Select(static row => row.Id).ToArray());

        var tail = await store.ListAsync(new RequestListQuery
        {
            SortField = RequestSortField.CreatedAt,
            Direction = SortDirection.Asc,
            Last = 2,
        });
        Assert.Equal([4L, 5L], tail.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(tail.HasPreviousPage);
        Assert.False(tail.HasNextPage);
    }

    [Fact]
    public async Task VisibilityFilterMatchesTheReadRequestsRule()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestSegmentStore(context);

        await SeedApiKeyAsync(db, 100, type: "personal", userId: 6);
        await SeedApiKeyAsync(db, 101, type: "personal", userId: 5);
        await SeedApiKeyAsync(db, 102, type: "service_account", userId: null);
        await SeedRequestAsync(db, 1, traceId: 1, status: "completed", source: "playground", userId: 5);
        await SeedRequestAsync(db, 2, traceId: 1, status: "completed", source: "playground", userId: 6);
        await SeedRequestAsync(db, 3, traceId: 1, status: "completed", apiKeyId: null);
        await SeedRequestAsync(db, 4, traceId: 1, status: "completed", apiKeyId: 100);
        await SeedRequestAsync(db, 5, traceId: 1, status: "completed", apiKeyId: 101);
        await SeedRequestAsync(db, 6, traceId: 1, status: "completed", apiKeyId: 102);

        var visible = await store.ListAsync(new RequestListQuery { VisibilityUserId = 5 });
        Assert.Equal([1L, 3L, 5L, 6L], visible.Nodes.Select(static row => row.Id).ToArray());

        var unscoped = await store.ListAsync(new RequestListQuery());
        Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], unscoped.Nodes.Select(static row => row.Id).ToArray());
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
        long? traceId,
        string status,
        long projectId = 1,
        string source = "api",
        long? userId = null,
        long? apiKeyId = null,
        DateTime? createdAt = null)
    {
        var created = createdAt ?? CreatedAt;
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "requests"
                ("id", "created_at", "updated_at", "user_id", "source", "model_id",
                 "request_body", "status", "api_key_id", "project_id", "trace_id")
            VALUES (@Id, @CreatedAt, @CreatedAt, @UserId, @Source, 'gpt-4o',
                    '{}'::jsonb, @Status, @ApiKeyId, @ProjectId, @TraceId);
            """,
            new
            {
                Id = id,
                CreatedAt = created,
                UserId = userId,
                Source = source,
                Status = status,
                ApiKeyId = apiKeyId,
                ProjectId = projectId,
                TraceId = traceId,
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
}
