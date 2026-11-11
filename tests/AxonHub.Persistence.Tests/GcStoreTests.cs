using Dapper;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// The GC queries of internal/server/gc (gc.go, gc_bodies.go) against a real
/// PostgreSQL schema: retained-trace exclusions, ascending id batching, the
/// payload placeholder predicates, the external-storage clauses and the strip
/// literals.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class GcStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime Old = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Recent = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Cutoff = new(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly long[] OldThreadIds = [1L];
    private static readonly long[] OldTraceIds = [11L];

    [Fact]
    public async Task ListOldRequestsExcludesRetainedTracesAndOrdersById()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        await SeedTraceAsync(db, 100, "retained");
        await SeedTraceAsync(db, 101, "active");
        await SeedRequestAsync(db, 30, traceId: 100);
        await SeedRequestAsync(db, 10, traceId: 101, dataStorageId: 3);
        await SeedRequestAsync(db, 20);
        await SeedRequestAsync(db, 40, createdAt: Recent, traceId: 101);

        await using var context = db.Factory.CreateDbContext();
        var store = new GcStore(context);

        var rows = await store.ListOldRequestsAsync(Cutoff, limit: 500);

        Assert.Equal([10L, 20L], rows.Select(static row => row.Id).ToArray());
        Assert.Equal(7L, rows[0].ProjectId);
        Assert.Equal(3L, rows[0].DataStorageId);
        Assert.Null(rows[1].DataStorageId);

        var limited = await store.ListOldRequestsAsync(Cutoff, limit: 1);
        Assert.Equal([10L], limited.Select(static row => row.Id).ToArray());

        Assert.Equal(2L, await store.CountOldRequestsAsync(Cutoff));
    }

    [Fact]
    public async Task ListOldExecutionsAndUsageLogsExcludeRetainedRequests()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        await SeedTraceAsync(db, 200, "retained");
        await SeedTraceAsync(db, 201, "active");
        await SeedRequestAsync(db, 50, traceId: 200);
        await SeedRequestAsync(db, 51, traceId: 201);
        await SeedRequestAsync(db, 52);

        await SeedExecutionAsync(db, 1000, requestId: 50);
        await SeedExecutionAsync(db, 1001, requestId: 51, dataStorageId: 4);
        await SeedExecutionAsync(db, 1002, requestId: 52);
        await SeedExecutionAsync(db, 1003, requestId: 51, createdAt: Recent);

        await SeedUsageLogAsync(db, 2000, requestId: 50);
        await SeedUsageLogAsync(db, 2001, requestId: 51);
        await SeedUsageLogAsync(db, 2002, requestId: 52);
        await SeedUsageLogAsync(db, 2003, requestId: 51, createdAt: Recent);

        await using var context = db.Factory.CreateDbContext();
        var store = new GcStore(context);

        var executions = await store.ListOldExecutionsAsync(Cutoff, limit: 500);
        Assert.Equal([1001L, 1002L], executions.Select(static row => row.Id).ToArray());
        Assert.Equal(51L, executions[0].RequestId);
        Assert.Equal(7L, executions[0].ProjectId);
        Assert.Equal(4L, executions[0].DataStorageId);

        var usageLogs = await store.ListOldUsageLogIdsAsync(Cutoff, limit: 500);
        Assert.Equal([2001L, 2002L], usageLogs.ToArray());

        Assert.Equal(2L, await store.CountOldExecutionsAsync(Cutoff));
        Assert.Equal(2L, await store.CountOldUsageLogsAsync(Cutoff));
    }

    [Fact]
    public async Task ListOldThreadsAndTracesSkipTheRetainedStatus()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        await SeedThreadAsync(db, 1, "active");
        await SeedThreadAsync(db, 2, "retained");
        await SeedThreadAsync(db, 3, "active", Recent);

        await SeedTraceAsync(db, 10, "retained");
        await SeedTraceAsync(db, 11, "active");
        await SeedTraceAsync(db, 12, "retained", Recent);
        await SeedTraceAsync(db, 13, "active", Recent);

        await using var context = db.Factory.CreateDbContext();
        var store = new GcStore(context);

        Assert.Equal(OldThreadIds, (await store.ListOldThreadIdsAsync(Cutoff, 500)).ToArray());
        Assert.Equal(OldTraceIds, (await store.ListOldTraceIdsAsync(Cutoff, 500)).ToArray());
    }

    [Fact]
    public async Task ListOldChannelProbesUsesTheStrictTimestampCutoffAndAscendingIds()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        var cutoffUnix = new DateTimeOffset(Cutoff).ToUnixTimeSeconds();
        await SeedChannelProbeAsync(db, 30, cutoffUnix - 1);
        await SeedChannelProbeAsync(db, 20, cutoffUnix - 100);
        // The predicate is timestamp < cutoff: the boundary row stays.
        await SeedChannelProbeAsync(db, 10, cutoffUnix);

        await using var context = db.Factory.CreateDbContext();
        var store = new GcStore(context);

        // The awaits are hoisted: a collection expression binds to the span
        // overload, and a span cannot survive an await boundary.
        var firstBatch = (await store.ListOldChannelProbeIdsAsync(cutoffUnix, 500)).ToArray();
        Assert.Equal([20L, 30L], firstBatch);

        var limitedBatch = (await store.ListOldChannelProbeIdsAsync(cutoffUnix, 1)).ToArray();
        Assert.Equal([20L], limitedBatch);

        Assert.Equal(1, await store.DeleteChannelProbesAsync([20L]));

        var remaining = (await store.ListOldChannelProbeIdsAsync(cutoffUnix, 500)).ToArray();
        Assert.Equal([30L], remaining);
    }

    [Fact]
    public async Task RequestPayloadPredicateMatchesTheReferencePlaceholders()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        // Matching rows.
        await SeedRequestAsync(db, 1, body: """{"a":1}""", headers: "{}");
        await SeedRequestAsync(db, 2, body: "{}", headers: """{"h":1}""");

        // Placeholder bodies and headers never match.
        await SeedRequestAsync(db, 3, body: "{}", headers: "{}");
        await SeedRequestAsync(db, 4, body: "null", headers: null);
        await SeedRequestAsync(
            db,
            6,
            body: "{}",
            headers: "{}",
            dataStorageId: 1);

        // External storage rows with the primary id only match when headers are
        // still set; the extra clause keys on request_headers IS NOT NULL.
        await SeedRequestAsync(db, 5, body: "{}", headers: "{}", dataStorageId: 2);

        // Recent rows and retained requests stay out of the window.
        await SeedRequestAsync(db, 7, createdAt: Recent, body: """{"a":1}""", headers: "{}");
        await SeedTraceAsync(db, 300, "retained");
        await SeedRequestAsync(
            db,
            8,
            traceId: 300,
            body: "{}",
            headers: "{}",
            dataStorageId: 2);

        await using var context = db.Factory.CreateDbContext();
        var store = new GcStore(context);

        var rows = await store.ListRequestPayloadsAsync(
            GcPayloadKind.RequestBodies, Cutoff, primaryStorageId: 1, skipIds: [], limit: 500);
        Assert.Equal([1L, 2L, 5L], rows.Select(static row => row.Id).ToArray());

        // The worker replays the pass with the failed external ids skipped.
        var withSkip = await store.ListRequestPayloadsAsync(
            GcPayloadKind.RequestBodies, Cutoff, primaryStorageId: 1, skipIds: [2], limit: 500);
        Assert.Equal([1L, 5L], withSkip.Select(static row => row.Id).ToArray());

        // Without a primary storage the external clauses are not emitted.
        var withoutPrimary = await store.ListRequestPayloadsAsync(
            GcPayloadKind.RequestBodies, Cutoff, primaryStorageId: 0, skipIds: [], limit: 500);
        Assert.Equal([1L, 2L], withoutPrimary.Select(static row => row.Id).ToArray());
    }

    [Fact]
    public async Task ResponsePredicatesAlsoSelectExternalNullPayloads()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        await SeedRequestAsync(
            db, 1, responseBody: """{"x":1}""", responseHeaders: "{}");
        await SeedRequestAsync(
            db, 2, responseBody: "{}", responseHeaders: """{"h":1}""");
        await SeedRequestAsync(
            db, 3, responseBody: null, responseHeaders: null, dataStorageId: 2);
        await SeedRequestAsync(
            db, 4, responseBody: null, responseHeaders: null, dataStorageId: 1);
        await SeedRequestAsync(
            db, 5, responseBody: "null", responseHeaders: null);

        await SeedRequestAsync(db, 60, body: "{}");
        await SeedExecutionAsync(db, 10, requestId: 60, chunks: """[1]""");
        await SeedExecutionAsync(db, 11, requestId: 60, chunks: "[]");
        await SeedExecutionAsync(db, 12, requestId: 60, chunks: null, dataStorageId: 2);
        await SeedExecutionAsync(db, 13, requestId: 60, chunks: null, dataStorageId: 1);
        await SeedExecutionAsync(db, 14, requestId: 60, chunks: "{}");

        await using var context = db.Factory.CreateDbContext();
        var store = new GcStore(context);

        var bodies = await store.ListRequestPayloadsAsync(
            GcPayloadKind.ResponseBodies, Cutoff, primaryStorageId: 1, skipIds: [], limit: 500);
        Assert.Equal([1L, 2L, 3L], bodies.Select(static row => row.Id).ToArray());

        var chunks = await store.ListExecutionPayloadsAsync(
            GcPayloadKind.ResponseChunks, Cutoff, primaryStorageId: 1, skipIds: [], limit: 500);
        Assert.Equal([10L, 12L], chunks.Select(static row => row.Id).ToArray());
    }

    [Fact]
    public async Task ClearPayloadsWritesTheReferenceLiterals()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        await SeedRequestAsync(
            db,
            1,
            body: """{"a":1}""",
            headers: """{"h":1}""",
            responseBody: """{"b":2}""",
            responseHeaders: """{"h":2}""",
            chunks: "[1]");
        await SeedRequestAsync(db, 60, body: "{}");
        await SeedExecutionAsync(
            db,
            10,
            requestId: 60,
            responseBody: """{"b":2}""",
            responseHeaders: """{"h":2}""",
            chunks: "[1]");

        await using var context = db.Factory.CreateDbContext();
        var store = new GcStore(context);

        Assert.Equal(
            1,
            await store.ClearRequestPayloadsAsync(GcPayloadKind.RequestBodies, [1]));
        Assert.Equal(
            1,
            await store.ClearRequestPayloadsAsync(GcPayloadKind.ResponseBodies, [1]));
        Assert.Equal(
            1,
            await store.ClearRequestPayloadsAsync(GcPayloadKind.ResponseChunks, [1]));

        await using (var connection = await db.Factory.OpenAsync())
        {
            async Task<T> ScalarAsync<T>(string column)
                => (await connection.ExecuteScalarAsync<T>(
                    "SELECT \"" + column + "\"::text FROM \"requests\" WHERE \"id\" = 1;"))!;

            Assert.Equal("{}", await ScalarAsync<string>("request_body"));
            Assert.Null(await ScalarAsync<string>("request_headers"));
            Assert.Equal("{}", await ScalarAsync<string>("response_body"));
            Assert.Null(await ScalarAsync<string>("response_headers"));
            Assert.Equal("[]", await ScalarAsync<string>("response_chunks"));

            var updatedAt = await connection.ExecuteScalarAsync<DateTime>(
                """SELECT "updated_at" FROM "requests" WHERE "id" = 1;""");
            Assert.True(updatedAt > Old);
        }

        Assert.Equal(
            1,
            await store.ClearExecutionPayloadsAsync(GcPayloadKind.ResponseBodies, [10]));
        Assert.Equal(
            1,
            await store.ClearExecutionPayloadsAsync(GcPayloadKind.ResponseChunks, [10]));

        await using (var connection = await db.Factory.OpenAsync())
        {
            async Task<T> ScalarAsync<T>(string column)
                => (await connection.ExecuteScalarAsync<T>(
                    "SELECT \"" + column + "\"::text FROM \"request_executions\" WHERE \"id\" = 10;"))!;

            Assert.Equal("{}", await ScalarAsync<string>("response_body"));
            Assert.Null(await ScalarAsync<string>("response_headers"));
            Assert.Equal("[]", await ScalarAsync<string>("response_chunks"));
        }

        // Empty batches are a no-op, not a SQL error (IN () is invalid).
        Assert.Equal(0, await store.ClearRequestPayloadsAsync(GcPayloadKind.ResponseChunks, []));
        Assert.Equal(0, await store.DeleteRequestsAsync([]));
    }

    [Fact]
    public async Task DeleteRemovesTheRequestedIdsOnly()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        await SeedRequestAsync(db, 1, body: "{}");
        await SeedRequestAsync(db, 2, body: "{}");
        await SeedRequestAsync(db, 3, body: "{}");
        await SeedThreadAsync(db, 10, "active");
        await SeedThreadAsync(db, 11, "active");

        await using var context = db.Factory.CreateDbContext();
        var store = new GcStore(context);

        Assert.Equal(2, await store.DeleteRequestsAsync([1, 3, 999]));
        Assert.Equal(1, await store.DeleteThreadsAsync([10]));
        Assert.Equal(1, await store.DeleteThreadsAsync([11]));

        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(1L, await connection.ExecuteScalarAsync<long>(
            """SELECT COUNT(*) FROM "requests";"""));
        Assert.Equal(0L, await connection.ExecuteScalarAsync<long>(
            """SELECT COUNT(*) FROM "threads";"""));
    }

    [Fact]
    public async Task PrimaryStorageLookupIncludesSoftDeletedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO "data_storages"
                    ("id", "name", "description", "primary", "type", "settings", "deleted_at")
                VALUES
                    (1, 'deleted-primary', '', true, 'database', '{}'::jsonb, 1700000000),
                    (2, 'live-primary', '', true, 'database', '{}'::jsonb, 0),
                    (3, 'secondary', '', false, 'database', '{}'::jsonb, 0);
                """);
        }

        await using var context = db.Factory.CreateDbContext();
        var store = new DataStorageStore(context);

        // The GC worker's context uses schematype.SkipSoftDelete, so the
        // soft-deleted primary row still wins by id order; the live-only
        // lookup skips it.
        Assert.Equal(1L, (await store.GetPrimaryIncludingDeletedAsync())?.Id);
        Assert.Equal(2L, (await store.GetPrimaryAsync())?.Id);
        Assert.NotNull(await store.GetByIdIncludingDeletedAsync(1));
        Assert.Null(await store.GetByIdAsync(1));
    }

    [Fact]
    public async Task VacuumRunsOutsideATransaction()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new GcStore(context);

        await store.VacuumAsync(full: false);
        await store.VacuumAsync(full: true);
    }

    private static async Task SeedTraceAsync(
        TestDatabase db,
        long id,
        string status,
        DateTime? createdAt = null)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "traces" ("id", "created_at", "updated_at", "trace_id", "status", "project_id")
            VALUES (@Id, @CreatedAt, @CreatedAt, @TraceId, @Status, 7);
            """,
            new
            {
                Id = id,
                CreatedAt = createdAt ?? Old,
                TraceId = "trace-" + id,
                Status = status,
            });
    }

    private static async Task SeedChannelProbeAsync(
        TestDatabase db,
        long id,
        long timestamp)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "channel_probes"
                ("id", "total_request_count", "success_request_count", "timestamp", "channel_id")
            VALUES (@Id, 1, 1, @Timestamp, 7);
            """,
            new { Id = id, Timestamp = timestamp });
    }

    private static async Task SeedThreadAsync(
        TestDatabase db,
        long id,
        string status,
        DateTime? createdAt = null)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "threads" ("id", "created_at", "updated_at", "thread_id", "status", "project_id")
            VALUES (@Id, @CreatedAt, @CreatedAt, @ThreadId, @Status, 7);
            """,
            new
            {
                Id = id,
                CreatedAt = createdAt ?? Old,
                ThreadId = "thread-" + id,
                Status = status,
            });
    }

    private static async Task SeedUsageLogAsync(
        TestDatabase db,
        long id,
        long requestId,
        DateTime? createdAt = null)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "usage_logs" ("id", "created_at", "updated_at", "model_id", "request_id", "project_id")
            VALUES (@Id, @CreatedAt, @CreatedAt, 'gpt-4o', @RequestId, 7);
            """,
            new
            {
                Id = id,
                CreatedAt = createdAt ?? Old,
                RequestId = requestId,
            });
    }

    private static async Task SeedRequestAsync(
        TestDatabase db,
        long id,
        DateTime? createdAt = null,
        long? traceId = null,
        long? dataStorageId = null,
        string? body = null,
        string? headers = null,
        string? responseBody = null,
        string? responseHeaders = null,
        string? chunks = null)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "requests"
                ("id", "created_at", "updated_at", "model_id", "request_body",
                 "request_headers", "response_body", "response_headers",
                 "response_chunks", "status", "project_id", "trace_id",
                 "data_storage_id")
            VALUES
                (@Id, @CreatedAt, @CreatedAt, 'gpt-4o', CAST(@Body AS jsonb),
                 CAST(@Headers AS jsonb), CAST(@ResponseBody AS jsonb),
                 CAST(@ResponseHeaders AS jsonb), CAST(@Chunks AS jsonb),
                 'completed', 7, @TraceId, @DataStorageId);
            """,
            new
            {
                Id = id,
                CreatedAt = createdAt ?? Old,
                Body = body ?? "{}",
                Headers = headers,
                ResponseBody = responseBody,
                ResponseHeaders = responseHeaders,
                Chunks = chunks,
                TraceId = traceId,
                DataStorageId = dataStorageId,
            });
    }

    private static async Task SeedExecutionAsync(
        TestDatabase db,
        long id,
        long requestId,
        DateTime? createdAt = null,
        long? dataStorageId = null,
        string? responseBody = null,
        string? responseHeaders = null,
        string? chunks = null)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "request_executions"
                ("id", "created_at", "updated_at", "model_id", "request_body",
                 "response_body", "response_headers", "response_chunks", "status",
                 "project_id", "request_id", "data_storage_id")
            VALUES
                (@Id, @CreatedAt, @CreatedAt, 'gpt-4o', '{}'::jsonb,
                 CAST(@ResponseBody AS jsonb), CAST(@ResponseHeaders AS jsonb),
                 CAST(@Chunks AS jsonb), 'completed', 7, @RequestId, @DataStorageId);
            """,
            new
            {
                Id = id,
                CreatedAt = createdAt ?? Old,
                RequestId = requestId,
                DataStorageId = dataStorageId,
                ResponseBody = responseBody,
                ResponseHeaders = responseHeaders,
                Chunks = chunks,
            });
    }
}
