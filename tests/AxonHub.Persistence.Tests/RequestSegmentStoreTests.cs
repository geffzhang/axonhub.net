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
    public async Task ListAppliesTheExtendedScalarPredicates()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestSegmentStore(context);

        var savedAt = CreatedAt.AddMinutes(5);
        await SeedRequestAsync(
            db, 1, traceId: null, status: "completed", createdAt: CreatedAt,
            stream: true, clientIp: "alpha-host", userAgent: "curl/8.4.0",
            metricsLatencyMs: 100, metricsFirstTokenLatencyMs: 10, metricsReasoningDurationMs: 5,
            contentSaved: true, contentStorageId: 9, contentStorageKey: "tenants/1",
            contentSavedAt: savedAt);
        await SeedRequestAsync(
            db, 2, traceId: null, status: "completed", createdAt: CreatedAt.AddSeconds(1),
            stream: false, clientIp: "beta-host", userAgent: "axonhub-sdk/1.0");
        await SeedRequestAsync(
            db, 3, traceId: null, status: "completed", createdAt: CreatedAt.AddSeconds(2),
            stream: true, clientIp: "gamma-host", userAgent: "Mozilla/5.0",
            metricsLatencyMs: 250, metricsFirstTokenLatencyMs: 30,
            contentSaved: true, contentStorageId: 10, contentStorageKey: "tenants/2",
            contentSavedAt: savedAt.AddMinutes(1));
        await SeedRequestAsync(
            db, 4, traceId: null, status: "completed", createdAt: CreatedAt.AddSeconds(3),
            stream: false, clientIp: "alpha-host", userAgent: "curl/8.4.0",
            metricsFirstTokenLatencyMs: 20);

        async Task<IReadOnlyList<long>> QueryAsync(RequestListFilter where)
            => (await store.ListAsync(new RequestListQuery { Where = where }))
                .Nodes.Select(static row => row.Id).ToArray();

        Assert.Equal([1L, 3L], await QueryAsync(new RequestListFilter { Stream = true }));
        Assert.Equal([2L, 4L], await QueryAsync(new RequestListFilter { Stream = false }));
        Assert.Equal([2L, 4L], await QueryAsync(new RequestListFilter { StreamNEQ = true }));

        Assert.Equal([1L, 4L], await QueryAsync(new RequestListFilter { ClientIp = "alpha-host" }));
        Assert.Equal([2L, 3L], await QueryAsync(new RequestListFilter { ClientIpNEQ = "alpha-host" }));
        Assert.Equal([2L], await QueryAsync(new RequestListFilter { ClientIpIn = ["beta-host"] }));
        Assert.Equal([1L, 4L],
            await QueryAsync(new RequestListFilter { ClientIpNotIn = ["beta-host", "gamma-host"] }));
        Assert.Equal([3L], await QueryAsync(new RequestListFilter { ClientIpGT = "beta-host" }));
        Assert.Equal([3L], await QueryAsync(new RequestListFilter { ClientIpGTE = "gamma-host" }));
        Assert.Equal([1L, 2L, 4L], await QueryAsync(new RequestListFilter { ClientIpLT = "gamma-host" }));
        Assert.Equal([1L, 4L], await QueryAsync(new RequestListFilter { ClientIpLTE = "alpha-host" }));
        Assert.Equal([3L], await QueryAsync(new RequestListFilter { ClientIpContains = "amm" }));
        Assert.Equal([1L, 4L], await QueryAsync(new RequestListFilter { ClientIpHasPrefix = "alpha" }));
        Assert.Equal([1L, 2L, 3L, 4L],
            await QueryAsync(new RequestListFilter { ClientIpHasSuffix = "-host" }));
        Assert.Equal([1L, 4L],
            await QueryAsync(new RequestListFilter { ClientIpEqualFold = "ALPHA-HOST" }));
        Assert.Equal([3L], await QueryAsync(new RequestListFilter { ClientIpContainsFold = "GAMMA" }));

        Assert.Equal([1L, 4L], await QueryAsync(new RequestListFilter { UserAgentHasPrefix = "curl" }));
        Assert.Equal([2L], await QueryAsync(new RequestListFilter { UserAgentHasSuffix = "sdk/1.0" }));
        Assert.Equal([3L], await QueryAsync(new RequestListFilter { UserAgentContains = "Mozilla" }));
        Assert.Equal([2L, 3L], await QueryAsync(new RequestListFilter { UserAgentNEQ = "curl/8.4.0" }));
        Assert.Equal([1L, 4L],
            await QueryAsync(new RequestListFilter { UserAgentEqualFold = "CURL/8.4.0" }));
        Assert.Equal([3L], await QueryAsync(new RequestListFilter { UserAgentContainsFold = "MOZILLA" }));

        Assert.Equal([1L], await QueryAsync(new RequestListFilter { MetricsLatencyMs = 100 }));
        Assert.Equal([3L], await QueryAsync(new RequestListFilter { MetricsLatencyMsNEQ = 100 }));
        Assert.Equal([3L], await QueryAsync(new RequestListFilter { MetricsLatencyMsIn = [250] }));
        Assert.Equal([1L], await QueryAsync(new RequestListFilter { MetricsLatencyMsNotIn = [250] }));
        Assert.Equal([3L], await QueryAsync(new RequestListFilter { MetricsLatencyMsGT = 100 }));
        Assert.Equal([1L, 3L],
            await QueryAsync(new RequestListFilter { MetricsLatencyMsGTE = 100 }));
        Assert.Equal([1L], await QueryAsync(new RequestListFilter { MetricsLatencyMsLT = 200 }));
        Assert.Equal([1L], await QueryAsync(new RequestListFilter { MetricsLatencyMsLTE = 100 }));
        Assert.Equal([2L, 4L], await QueryAsync(new RequestListFilter { MetricsLatencyMsIsNil = true }));
        Assert.Equal([1L, 3L],
            await QueryAsync(new RequestListFilter { MetricsLatencyMsNotNil = true }));

        Assert.Equal([1L, 3L],
            await QueryAsync(new RequestListFilter { MetricsFirstTokenLatencyMsIn = [10, 30] }));
        Assert.Equal([3L, 4L],
            await QueryAsync(new RequestListFilter { MetricsFirstTokenLatencyMsGTE = 20 }));
        Assert.Equal([1L],
            await QueryAsync(new RequestListFilter { MetricsFirstTokenLatencyMsLT = 20 }));
        Assert.Equal([2L],
            await QueryAsync(new RequestListFilter { MetricsFirstTokenLatencyMsIsNil = true }));

        Assert.Equal([1L],
            await QueryAsync(new RequestListFilter { MetricsReasoningDurationMs = 5 }));
        Assert.Equal([2L, 3L, 4L],
            await QueryAsync(new RequestListFilter { MetricsReasoningDurationMsIsNil = true }));
        Assert.Equal([1L],
            await QueryAsync(new RequestListFilter { MetricsReasoningDurationMsNotNil = true }));

        Assert.Equal([1L, 3L], await QueryAsync(new RequestListFilter { ContentSaved = true }));
        Assert.Equal([2L, 4L], await QueryAsync(new RequestListFilter { ContentSavedNEQ = true }));

        Assert.Equal([1L, 3L],
            await QueryAsync(new RequestListFilter { ContentStorageIdIn = [9, 10] }));
        Assert.Equal([3L], await QueryAsync(new RequestListFilter { ContentStorageIdNotIn = [9] }));
        Assert.Equal([3L], await QueryAsync(new RequestListFilter { ContentStorageIdGT = 9 }));
        Assert.Equal([1L], await QueryAsync(new RequestListFilter { ContentStorageIdLT = 10 }));
        Assert.Equal([2L, 4L], await QueryAsync(new RequestListFilter { ContentStorageIdIsNil = true }));
        Assert.Equal([1L, 3L],
            await QueryAsync(new RequestListFilter { ContentStorageIdNotNil = true }));

        Assert.Equal([1L, 3L],
            await QueryAsync(new RequestListFilter { ContentStorageKeyContains = "tenants/" }));
        Assert.Equal([1L], await QueryAsync(new RequestListFilter { ContentStorageKeyHasSuffix = "/1" }));
        Assert.Equal([3L],
            await QueryAsync(new RequestListFilter { ContentStorageKeyEqualFold = "TENANTS/2" }));
        Assert.Equal([3L],
            await QueryAsync(new RequestListFilter { ContentStorageKeyNEQ = "tenants/1" }));
        Assert.Equal([2L, 4L], await QueryAsync(new RequestListFilter { ContentStorageKeyIsNil = true }));

        Assert.Equal([1L], await QueryAsync(new RequestListFilter { ContentSavedAt = savedAt }));
        Assert.Equal([3L], await QueryAsync(new RequestListFilter { ContentSavedAtGT = savedAt }));
        Assert.Equal([1L], await QueryAsync(new RequestListFilter { ContentSavedAtLT = savedAt.AddMinutes(1) }));
        Assert.Equal([1L], await QueryAsync(new RequestListFilter { ContentSavedAtIn = [savedAt] }));
        Assert.Equal([3L],
            await QueryAsync(new RequestListFilter { ContentSavedAtNotIn = [savedAt] }));
        Assert.Equal([2L, 4L], await QueryAsync(new RequestListFilter { ContentSavedAtIsNil = true }));
        Assert.Equal([1L, 3L], await QueryAsync(new RequestListFilter { ContentSavedAtNotNil = true }));

        // entgql ignores empty In/NotIn lists; the port must not emit IN ().
        Assert.Equal([1L, 2L, 3L, 4L], await QueryAsync(new RequestListFilter { StatusIn = [] }));
        Assert.Equal([1L, 2L, 3L, 4L], await QueryAsync(new RequestListFilter { ContentStorageIdIn = [] }));
    }

    [Fact]
    public async Task ListAppliesTheEdgePredicates()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestSegmentStore(context);

        await SeedChannelRowAsync(db, 300, "Alpha");
        // Soft-deleted targets still satisfy nested edge predicates: ent's
        // generated filter never re-applies the target's deleted_at = 0.
        await SeedChannelRowAsync(db, 301, "Beta", deletedAt: 99);
        await SeedProjectRowAsync(db, 1, "Alpha");
        await SeedProjectRowAsync(db, 2, "Ghost", deletedAt: 99);
        await SeedApiKeyRowAsync(db, 500, "shared-key");
        await SeedApiKeyRowAsync(db, 501, "ghost-key", deletedAt: 99);
        await SeedTraceAsync(db, 700, threadId: null);
        await SeedTraceAsync(db, 701, threadId: null);
        await SeedDataStorageRowAsync(db, 900, "Primary", primary: true);
        await SeedExecutionRowAsync(db, 1, requestId: 1, status: "completed", channelId: 300);
        await SeedExecutionRowAsync(db, 2, requestId: 2, status: "failed", channelId: 301);
        await SeedUsageLogRowAsync(db, 1, requestId: 1, totalTokens: 10, projectId: 1, channelId: 300);
        await SeedUsageLogRowAsync(db, 2, requestId: 2, totalTokens: 20, projectId: 2, channelId: 301);

        await SeedRequestAsync(
            db, 1, traceId: 700, status: "completed", apiKeyId: 500, channelId: 300,
            dataStorageId: 900);
        await SeedRequestAsync(
            db, 2, traceId: 701, status: "failed", projectId: 2, apiKeyId: 501, channelId: 301);
        await SeedRequestAsync(db, 3, traceId: null, status: "completed");
        await SeedRequestAsync(db, 4, traceId: null, status: "completed");

        async Task<IReadOnlyList<long>> QueryAsync(RequestListFilter where)
            => (await store.ListAsync(new RequestListQuery { Where = where }))
                .Nodes.Select(static row => row.Id).ToArray();

        Assert.Equal([1L, 2L], await QueryAsync(new RequestListFilter { HasApiKey = true }));
        Assert.Equal([3L, 4L], await QueryAsync(new RequestListFilter { HasApiKey = false }));
        Assert.Equal([1L],
            await QueryAsync(new RequestListFilter
            {
                HasApiKeyWith = [new ApiKeyListFilter { Name = "shared-key" }],
            }));
        Assert.Equal([2L],
            await QueryAsync(new RequestListFilter
            {
                HasApiKeyWith = [new ApiKeyListFilter { Name = "ghost-key" }],
            }));
        Assert.Equal([1L],
            await QueryAsync(new RequestListFilter
            {
                HasApiKeyWith = [new ApiKeyListFilter { NameContains = "shared" }],
            }));

        Assert.Equal([1L, 2L, 3L, 4L],
            await QueryAsync(new RequestListFilter { HasProject = true }));
        Assert.Equal([1L, 3L, 4L],
            await QueryAsync(new RequestListFilter
            {
                HasProjectWith = [new ProjectListFilter { Name = "Alpha" }],
            }));
        Assert.Equal([2L],
            await QueryAsync(new RequestListFilter
            {
                HasProjectWith = [new ProjectListFilter { Name = "Ghost" }],
            }));

        Assert.Equal([1L, 2L], await QueryAsync(new RequestListFilter { HasTrace = true }));
        Assert.Equal([3L, 4L], await QueryAsync(new RequestListFilter { HasTrace = false }));
        Assert.Equal([1L],
            await QueryAsync(new RequestListFilter
            {
                HasTraceWith = [new TraceListFilter { TraceId = "trace-700" }],
            }));
        Assert.Equal([1L, 2L],
            await QueryAsync(new RequestListFilter
            {
                HasTraceWith = [new TraceListFilter { ProjectId = 1, Status = "active" }],
            }));

        Assert.Equal([1L], await QueryAsync(new RequestListFilter { HasDataStorage = true }));
        Assert.Equal([2L, 3L, 4L], await QueryAsync(new RequestListFilter { HasDataStorage = false }));
        Assert.Equal([1L],
            await QueryAsync(new RequestListFilter
            {
                HasDataStorageWith = [new DataStorageListFilter { Primary = true }],
            }));
        Assert.Equal([1L],
            await QueryAsync(new RequestListFilter
            {
                HasDataStorageWith = [new DataStorageListFilter { Name = "Primary" }],
            }));

        Assert.Equal([1L, 2L], await QueryAsync(new RequestListFilter { HasExecutions = true }));
        Assert.Equal([3L, 4L], await QueryAsync(new RequestListFilter { HasExecutions = false }));
        Assert.Equal([2L],
            await QueryAsync(new RequestListFilter
            {
                HasExecutionsWith = [new RequestExecutionListFilter { Status = "failed" }],
            }));
        Assert.Equal([1L, 2L],
            await QueryAsync(new RequestListFilter
            {
                HasExecutionsWith = [new RequestExecutionListFilter { ModelIdContains = "gpt" }],
            }));
        Assert.Equal([1L],
            await QueryAsync(new RequestListFilter
            {
                HasExecutionsWith =
                [
                    new RequestExecutionListFilter
                    {
                        HasChannelWith = [new ChannelListFilter { Name = "Alpha" }],
                    },
                ],
            }));

        Assert.Equal([1L, 2L], await QueryAsync(new RequestListFilter { HasChannel = true }));
        Assert.Equal([3L, 4L], await QueryAsync(new RequestListFilter { HasChannel = false }));
        Assert.Equal([1L],
            await QueryAsync(new RequestListFilter
            {
                HasChannelWith = [new ChannelListFilter { Name = "Alpha" }],
            }));
        Assert.Equal([2L],
            await QueryAsync(new RequestListFilter
            {
                HasChannelWith = [new ChannelListFilter { Name = "Beta" }],
            }));

        Assert.Equal([1L, 2L], await QueryAsync(new RequestListFilter { HasUsageLogs = true }));
        Assert.Equal([3L, 4L], await QueryAsync(new RequestListFilter { HasUsageLogs = false }));
        Assert.Equal([2L],
            await QueryAsync(new RequestListFilter
            {
                HasUsageLogsWith = [new UsageLogListFilter { TotalTokensGTE = 20 }],
            }));
        Assert.Equal([2L],
            await QueryAsync(new RequestListFilter
            {
                HasUsageLogsWith =
                [
                    new UsageLogListFilter
                    {
                        HasChannelWith = [new ChannelListFilter { Name = "Beta" }],
                    },
                ],
            }));

        // HasXWith: [] is ignored, exactly like entgql's empty predicate list.
        Assert.Equal([1L, 2L, 3L, 4L],
            await QueryAsync(new RequestListFilter { HasApiKeyWith = [] }));
        Assert.Equal([1L, 2L, 3L, 4L],
            await QueryAsync(new RequestListFilter { HasChannelWith = [] }));

        Assert.Equal([3L, 4L], await QueryAsync(new RequestListFilter
        {
            Not = new RequestListFilter { HasChannel = true },
        }));
        Assert.Equal([1L, 2L], await QueryAsync(new RequestListFilter
        {
            Or =
            [
                new RequestListFilter { HasDataStorage = true },
                new RequestListFilter
                {
                    HasExecutionsWith = [new RequestExecutionListFilter { Status = "failed" }],
                },
            ],
        }));
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
        DateTime? createdAt = null,
        long? channelId = null,
        long? dataStorageId = null,
        bool stream = false,
        string clientIp = "",
        string userAgent = "",
        long? metricsLatencyMs = null,
        long? metricsFirstTokenLatencyMs = null,
        long? metricsReasoningDurationMs = null,
        bool contentSaved = false,
        long? contentStorageId = null,
        string? contentStorageKey = null,
        DateTime? contentSavedAt = null)
    {
        var created = createdAt ?? CreatedAt;
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "requests"
                ("id", "created_at", "updated_at", "user_id", "source", "model_id",
                 "request_body", "status", "client_ip", "user_agent", "stream",
                 "metrics_latency_ms", "metrics_first_token_latency_ms",
                 "metrics_reasoning_duration_ms", "content_saved", "content_storage_id",
                 "content_storage_key", "content_saved_at", "api_key_id", "channel_id",
                 "data_storage_id", "project_id", "trace_id")
            VALUES (@Id, @CreatedAt, @CreatedAt, @UserId, @Source, 'gpt-4o',
                    '{}'::jsonb, @Status, @ClientIp, @UserAgent, @Stream,
                    @MetricsLatencyMs, @MetricsFirstTokenLatencyMs,
                    @MetricsReasoningDurationMs, @ContentSaved, @ContentStorageId,
                    @ContentStorageKey, @ContentSavedAt, @ApiKeyId, @ChannelId,
                    @DataStorageId, @ProjectId, @TraceId);
            """,
            new
            {
                Id = id,
                CreatedAt = created,
                UserId = userId,
                Source = source,
                Status = status,
                ClientIp = clientIp,
                UserAgent = userAgent,
                Stream = stream,
                MetricsLatencyMs = metricsLatencyMs,
                MetricsFirstTokenLatencyMs = metricsFirstTokenLatencyMs,
                MetricsReasoningDurationMs = metricsReasoningDurationMs,
                ContentSaved = contentSaved,
                ContentStorageId = contentStorageId,
                ContentStorageKey = contentStorageKey,
                ContentSavedAt = contentSavedAt,
                ApiKeyId = apiKeyId,
                ChannelId = channelId,
                DataStorageId = dataStorageId,
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

    private static async Task SeedApiKeyRowAsync(
        TestDatabase db,
        long id,
        string name,
        long deletedAt = 0)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "api_keys"
                ("id", "created_at", "updated_at", "deleted_at", "key", "name", "type", "status")
            VALUES (@Id, @CreatedAt, @CreatedAt, @DeletedAt, @Key, @Name, 'service_account', 'enabled');
            """,
            new { Id = id, CreatedAt, Key = "key-" + id, Name = name, DeletedAt = deletedAt });
    }

    private static async Task SeedDataStorageRowAsync(
        TestDatabase db,
        long id,
        string name,
        bool primary = false)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "data_storages"
                ("id", "created_at", "updated_at", "deleted_at", "name", "description",
                 "primary", "type", "settings", "status")
            VALUES (@Id, @CreatedAt, @CreatedAt, 0, @Name, '', @Primary, 'database',
                    '{}'::jsonb, 'active');
            """,
            new { Id = id, CreatedAt, Name = name, Primary = primary });
    }

    private static async Task SeedExecutionRowAsync(
        TestDatabase db,
        long id,
        long requestId,
        string status,
        long? channelId = null)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "request_executions"
                ("id", "created_at", "updated_at", "project_id", "model_id", "request_body",
                 "status", "channel_id", "request_id")
            VALUES (@Id, @CreatedAt, @CreatedAt, 1, 'gpt-4o', '{}'::jsonb, @Status,
                    @ChannelId, @RequestId);
            """,
            new
            {
                Id = id,
                CreatedAt,
                Status = status,
                ChannelId = channelId,
                RequestId = requestId,
            });
    }

    private static async Task SeedUsageLogRowAsync(
        TestDatabase db,
        long id,
        long requestId,
        long totalTokens,
        long projectId = 1,
        long? channelId = null)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            """
            INSERT INTO "usage_logs"
                ("id", "created_at", "updated_at", "model_id", "total_tokens", "project_id",
                 "request_id", "channel_id")
            VALUES (@Id, @CreatedAt, @CreatedAt, 'gpt-4o', @TotalTokens, @ProjectId,
                    @RequestId, @ChannelId);
            """,
            new
            {
                Id = id,
                CreatedAt,
                TotalTokens = totalTokens,
                ProjectId = projectId,
                RequestId = requestId,
                ChannelId = channelId,
            });
    }
}
