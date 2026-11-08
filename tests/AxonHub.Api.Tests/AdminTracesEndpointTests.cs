using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Settings;
using AxonHub.Api.Storage;
using AxonHub.Api.Traces;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the Trace and Thread GraphQL surface over /admin/graphql against the
/// queries the frontend sends (frontend/src/features/traces and
/// frontend/src/features/threads): gid IDs, the root CREATED_AT→id order
/// rewrite, the edge created_at order, the ported where predicates, the
/// segment/usage resolvers and the eight status mutations with their exact
/// reference messages (internal/server/biz/trace.go, thread.go).
/// </summary>
public sealed class AdminTracesEndpointTests : IAsyncLifetime
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc);

    private const string ChatRequestBody =
        """{"model":"gpt-4o","messages":[{"role":"user","content":"Hello there"}]}""";

    private const string ChatResponseBody =
        """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-4o","choices":[{"index":0,"message":{"role":"assistant","content":"Hi!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":20,"total_tokens":30}}""";

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeTraceAdminStore _traces = new();
    private readonly FakeThreadAdminStore _threads = new();
    private readonly FakeUsageMetadataStore _usage = new();
    private readonly FakeRequestSegmentStore _requests = new();
    private readonly FakeDataStorageStore _dataStorages = new();
    private readonly FakeDataStorageBackends _backends = new();
    private readonly FakeSystemStore _systems = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IUserAdminStore>(_users);
        builder.Services.AddSingleton<ITraceAdminStore>(_traces);
        builder.Services.AddSingleton<IThreadAdminStore>(_threads);
        builder.Services.AddSingleton<IUsageMetadataStore>(_usage);
        builder.Services.AddSingleton<IRequestSegmentStore>(_requests);
        builder.Services.AddSingleton<IDataStorageStore>(_dataStorages);
        builder.Services.AddSingleton<IDataStorageBackendFactory>(_backends);
        builder.Services.AddSingleton<ISystemSettingsAdminService>(
            new SystemSettingsAdminService(_systems));
        builder.Services.AddScoped<PermissionValidator>();
        builder.Services.AddScoped<ApiKeyPrivacy>();
        builder.Services.AddScoped<TracePrivacy>();
        builder.Services.AddScoped<TraceSegmentService>();
        builder.Services.AddScoped<TraceManagementService>();
        builder.Services.AddScoped<ThreadManagementService>();
        builder.Services.AddScoped<RequestManagementService>();

        // The gate's JWT user (id 9, owner) is the caller every fixture
        // client sends; the privacy policies see it as an owner.
        await _users.CreateAsync(
            new UserRecord
            {
                Id = AdminGraphqlGateServices.UserId,
                Email = "gate-user@example.com",
                Status = "activated",
                IsOwner = true,
            },
            roleIds: null);

        _app = builder.Build();
        _app.MapAdminGraphql();
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _gate.Authorize(_client);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task TracesQueryMatchesTheFrontendShape()
    {
        AddProject(1);
        SeedThread(3, "thread-xyz");
        _traces.Seed(trace =>
        {
            trace.Id = 7;
            trace.TraceId = "trace-abc";
            trace.Status = "active";
            trace.ProjectId = 1;
            trace.ThreadId = 3;
            trace.CreatedAt = CreatedAt;
            trace.UpdatedAt = UpdatedAt;
        });
        SeedCompletedRequest(11, traceId: 7, "Hello there");
        SeedCompletedRequest(12, traceId: 7, "Another question");
        _requests.Seed(request =>
        {
            request.Id = 13;
            request.TraceId = 7;
            request.Status = "failed";
            request.ProjectId = 1;
            request.CreatedAt = CreatedAt;
            request.UpdatedAt = UpdatedAt;
        });

        var data = await QueryDataAsync(
            """
            query GetTraces($first: Int, $where: TraceWhereInput) {
              traces(first: $first, where: $where) {
                edges {
                  node {
                    id
                    traceID
                    firstUserQuery
                    status
                    createdAt
                    updatedAt
                    thread { id threadID }
                    requests(where: { status: completed }) { totalCount }
                  }
                  cursor
                }
                pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                totalCount
              }
            }
            """,
            new { first = 10, where = new { statusNEQ = "archived" } });

        var connection = data.GetProperty("traces");
        Assert.Equal(1, connection.GetProperty("totalCount").GetInt32());
        var edge = Assert.Single(connection.GetProperty("edges").EnumerateArray());
        var node = edge.GetProperty("node");
        Assert.Equal("gid://axonhub/Trace/7", node.GetProperty("id").GetString());
        Assert.Equal("trace-abc", node.GetProperty("traceID").GetString());
        Assert.Equal("Hello there", node.GetProperty("firstUserQuery").GetString());
        Assert.Equal("active", node.GetProperty("status").GetString());
        Assert.Equal("2026-01-02T03:04:05Z", node.GetProperty("createdAt").GetString());
        Assert.Equal("2026-01-02T03:04:06Z", node.GetProperty("updatedAt").GetString());
        Assert.Equal(
            "gid://axonhub/Thread/3",
            node.GetProperty("thread").GetProperty("id").GetString());
        Assert.Equal(
            "thread-xyz",
            node.GetProperty("thread").GetProperty("threadID").GetString());
        Assert.Equal(
            2,
            node.GetProperty("requests").GetProperty("totalCount").GetInt32());

        // The frontend's default order is the entgql default: id ascending.
        var query = Assert.Single(_traces.Queries);
        Assert.Equal(TraceSortField.Id, query.SortField);
        Assert.Equal(SortDirection.Asc, query.Direction);
        Assert.Null(query.ProjectScope);
        Assert.Equal("archived", query.Where?.StatusNEQ);
        Assert.Null(query.Where?.Status);
    }

    [Fact]
    public async Task TracesRootCreatedAtOrderFallsBackToId()
    {
        AddProject(1);
        _traces.Seed(trace =>
        {
            trace.Id = 1;
            trace.ProjectId = 1;
            trace.CreatedAt = CreatedAt;
            trace.UpdatedAt = UpdatedAt;
        });

        await QueryDataAsync(
            """
            query Traces($orderBy: TraceOrder) {
              traces(first: 10, orderBy: $orderBy) { totalCount }
            }
            """,
            new
            {
                orderBy = new
                {
                    field = "CREATED_AT",
                    direction = "DESC",
                },
            });

        var query = Assert.Single(_traces.Queries);
        Assert.Equal(TraceSortField.Id, query.SortField);
        Assert.Equal(SortDirection.Desc, query.Direction);
    }

    [Fact]
    public async Task TracesWherePredicatesReachTheStore()
    {
        AddProject(5);
        _traces.Seed(trace =>
        {
            trace.Id = 1;
            trace.TraceId = "abc-1";
            trace.ProjectId = 5;
            trace.ThreadId = null;
        });
        _traces.Seed(trace =>
        {
            trace.Id = 2;
            trace.TraceId = "abc-2";
            trace.ProjectId = 5;
            trace.ThreadId = 3;
        });
        _traces.Seed(trace =>
        {
            trace.Id = 3;
            trace.TraceId = "abc-3";
            trace.ProjectId = 6;
        });
        _traces.Seed(trace =>
        {
            trace.Id = 4;
            trace.TraceId = "zzz";
            trace.ProjectId = 5;
            trace.Status = "archived";
        });

        var data = await QueryDataAsync(
            """
            query Traces($where: TraceWhereInput) {
              traces(first: 10, where: $where) {
                totalCount
                edges { node { id traceID } }
              }
            }
            """,
            new
            {
                where = new
                {
                    projectID = "gid://axonhub/Project/5",
                    traceIDHasPrefix = "abc",
                    threadIDIsNil = true,
                    statusNEQ = "archived",
                },
            });

        var connection = data.GetProperty("traces");
        Assert.Equal(1, connection.GetProperty("totalCount").GetInt32());
        var node = connection.GetProperty("edges")[0].GetProperty("node");
        Assert.Equal("gid://axonhub/Trace/1", node.GetProperty("id").GetString());

        var filter = Assert.Single(_traces.Queries).Where;
        Assert.NotNull(filter);
        Assert.Equal(5, filter.ProjectId);
        Assert.Equal("abc", filter.TraceIdPrefix);
        Assert.True(filter.ThreadIdIsNil);
        Assert.Equal("archived", filter.StatusNEQ);
    }

    [Fact]
    public async Task TraceNodeExposesUsageSegmentsAndEdges()
    {
        AddProject(1);
        SeedThread(3, "thread-xyz");
        var trace = _traces.Seed(record =>
        {
            record.Id = 7;
            record.TraceId = "trace-abc";
            record.ProjectId = 1;
            record.ThreadId = 3;
            record.CreatedAt = CreatedAt;
            record.UpdatedAt = UpdatedAt;
        });
        SeedCompletedRequest(11, trace.Id, "Hello there");
        _usage.Traces[trace.Id] = new UsageMetadataRow(100, 200, 300, 40, 50, 0.125);

        var data = await QueryDataAsync(
            """
            query GetTraceWithSegments($id: ID!) {
              node(id: $id) {
                ... on Trace {
                  id
                  traceID
                  status
                  createdAt
                  updatedAt
                  usageMetadata {
                    totalInputTokens
                    totalOutputTokens
                    totalTokens
                    totalCost
                    totalCachedTokens
                    totalCachedWriteTokens
                  }
                  project { id name }
                  thread { id threadID }
                  requests(where: { status: completed }) { totalCount }
                  rawRootSegment
                  rootSegment {
                    id
                    parentId
                    model
                    duration
                    startTime
                    endTime
                    metadata { itemCount inputTokens outputTokens totalTokens cachedTokens }
                    requestSpans { id type value { userQuery { text } } }
                    responseSpans { id type value { text { text } } }
                  }
                  firstUserQuery
                  firstText
                }
              }
            }
            """,
            new { id = "gid://axonhub/Trace/7" });

        var node = data.GetProperty("node");
        Assert.Equal("gid://axonhub/Trace/7", node.GetProperty("id").GetString());

        var usage = node.GetProperty("usageMetadata");
        Assert.Equal(100, usage.GetProperty("totalInputTokens").GetInt32());
        Assert.Equal(200, usage.GetProperty("totalOutputTokens").GetInt32());
        Assert.Equal(300, usage.GetProperty("totalTokens").GetInt32());
        Assert.Equal(40, usage.GetProperty("totalCachedTokens").GetInt32());
        Assert.Equal(50, usage.GetProperty("totalCachedWriteTokens").GetInt32());
        Assert.Equal(0.125, usage.GetProperty("totalCost").GetDouble());

        Assert.Equal(
            "gid://axonhub/Project/1",
            node.GetProperty("project").GetProperty("id").GetString());
        Assert.Equal("project", node.GetProperty("project").GetProperty("name").GetString());
        Assert.Equal(
            "gid://axonhub/Thread/3",
            node.GetProperty("thread").GetProperty("id").GetString());
        Assert.Equal(
            1,
            node.GetProperty("requests").GetProperty("totalCount").GetInt32());

        var segment = node.GetProperty("rootSegment");
        Assert.Equal("gid://axonhub/Request/11", segment.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, segment.GetProperty("parentId").ValueKind);
        Assert.Equal("gpt-4o", segment.GetProperty("model").GetString());
        Assert.Equal(1000, segment.GetProperty("duration").GetInt32());
        Assert.Equal("2026-01-02T03:04:05Z", segment.GetProperty("startTime").GetString());
        Assert.Equal("2026-01-02T03:04:06Z", segment.GetProperty("endTime").GetString());

        var metadata = segment.GetProperty("metadata");
        Assert.Equal(10, metadata.GetProperty("inputTokens").GetInt32());
        Assert.Equal(20, metadata.GetProperty("outputTokens").GetInt32());
        Assert.Equal(30, metadata.GetProperty("totalTokens").GetInt32());
        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("itemCount").ValueKind);
        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("cachedTokens").ValueKind);

        var requestSpan = Assert.Single(segment.GetProperty("requestSpans").EnumerateArray());
        Assert.Equal("user_query", requestSpan.GetProperty("type").GetString());
        Assert.Equal(
            "Hello there",
            requestSpan.GetProperty("value").GetProperty("userQuery").GetProperty("text").GetString());

        var responseSpan = Assert.Single(segment.GetProperty("responseSpans").EnumerateArray());
        Assert.Equal("text", responseSpan.GetProperty("type").GetString());
        Assert.Equal(
            "Hi!",
            responseSpan.GetProperty("value").GetProperty("text").GetProperty("text").GetString());

        Assert.Equal("Hello there", node.GetProperty("firstUserQuery").GetString());
        Assert.Equal("Hi!", node.GetProperty("firstText").GetString());

        // rawRootSegment embeds the JSON-encoded segment structurally.
        var raw = node.GetProperty("rawRootSegment");
        Assert.Equal(JsonValueKind.Object, raw.ValueKind);
        Assert.Equal(11, raw.GetProperty("id").GetInt64());
        Assert.Equal("gpt-4o", raw.GetProperty("model").GetString());
        Assert.Equal(
            "Hello there",
            raw.GetProperty("requestSpans")[0]
                .GetProperty("value")
                .GetProperty("userQuery")
                .GetProperty("text")
                .GetString());
        Assert.Equal(1000, raw.GetProperty("duration").GetInt64());
    }

    [Fact]
    public async Task TraceRawRootSegmentIsNullWithoutCompletedRequests()
    {
        AddProject(1);
        _traces.Seed(record =>
        {
            record.Id = 7;
            record.TraceId = "trace-abc";
            record.ProjectId = 1;
            record.CreatedAt = CreatedAt;
            record.UpdatedAt = UpdatedAt;
        });

        var data = await QueryDataAsync(
            """
            query Trace($id: ID!) {
              node(id: $id) {
                ... on Trace {
                  rawRootSegment
                  rootSegment { id }
                  firstUserQuery
                  firstText
                }
              }
            }
            """,
            new { id = "gid://axonhub/Trace/7" });

        var node = data.GetProperty("node");
        Assert.Equal(JsonValueKind.Null, node.GetProperty("rawRootSegment").ValueKind);
        Assert.Equal(JsonValueKind.Null, node.GetProperty("rootSegment").ValueKind);
        Assert.Equal(JsonValueKind.Null, node.GetProperty("firstUserQuery").ValueKind);
        Assert.Equal(JsonValueKind.Null, node.GetProperty("firstText").ValueKind);
    }

    [Fact]
    public async Task TraceProjectEdgeFailsLikeEnt()
    {
        _traces.Seed(record =>
        {
            record.Id = 7;
            record.TraceId = "trace-abc";
            record.ProjectId = 99;
            record.ThreadId = 5;
        });

        // The Project edge propagates the Only error while the Thread edge
        // masks NotFound to null (gql_edge.go:652-668).
        var root = await ExecuteAsync(
            """
            query Trace($id: ID!) {
              node(id: $id) {
                ... on Trace {
                  project { id }
                  thread { id }
                }
              }
            }
            """,
            new { id = "gid://axonhub/Trace/7" },
            token: null,
            extraHeaders: null);

        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        var message = Assert.Single(errors.EnumerateArray())
            .GetProperty("message")
            .GetString();
        Assert.Equal("ent: project not found", message);
    }

    [Fact]
    public async Task TraceThreadEdgeIsNullWhenTheThreadIsMissing()
    {
        AddProject(1);
        _traces.Seed(record =>
        {
            record.Id = 7;
            record.TraceId = "trace-abc";
            record.ProjectId = 1;
            record.ThreadId = 5;
        });

        var data = await QueryDataAsync(
            """
            query Trace($id: ID!) {
              node(id: $id) { ... on Trace { thread { id } } }
            }
            """,
            new { id = "gid://axonhub/Trace/7" });

        Assert.Equal(
            JsonValueKind.Null,
            data.GetProperty("node").GetProperty("thread").ValueKind);
    }

    [Fact]
    public async Task TraceThreadIdIsTraceZeroWhenTheColumnIsNull()
    {
        AddProject(1);
        _traces.Seed(record =>
        {
            record.Id = 7;
            record.ProjectId = 1;
            record.ThreadId = null;
        });

        var data = await QueryDataAsync(
            """
            query Trace($id: ID!) {
              node(id: $id) { ... on Trace { threadID } }
            }
            """,
            new { id = "gid://axonhub/Trace/7" });

        // The reference resolver always builds a GUID from the non-pointer
        // Go int, so an absent thread serializes as Thread/0 (C190).
        Assert.Equal(
            "gid://axonhub/Thread/0",
            data.GetProperty("node").GetProperty("threadID").GetString());
    }

    [Fact]
    public async Task TraceRequestsEdgeScopesToTheTraceAndKeepsCreatedAtOrder()
    {
        AddProject(1);
        _traces.Seed(record =>
        {
            record.Id = 7;
            record.TraceId = "trace-abc";
            record.ProjectId = 1;
            record.CreatedAt = CreatedAt;
            record.UpdatedAt = UpdatedAt;
        });
        SeedCompletedRequest(11, traceId: 7, "first", dataStorageId: 0);
        SeedCompletedRequest(12, traceId: 7, "second", dataStorageId: 42);
        _requests.Seed(request =>
        {
            request.Id = 13;
            request.TraceId = 7;
            request.Status = "failed";
            request.ProjectId = 1;
            request.CreatedAt = CreatedAt;
            request.UpdatedAt = UpdatedAt;
        });

        var data = await QueryDataAsync(
            """
            query Trace($id: ID!) {
              node(id: $id) {
                ... on Trace {
                  requests(
                    first: 2
                    orderBy: { field: CREATED_AT, direction: ASC }
                    where: { status: completed }
                  ) {
                    totalCount
                    pageInfo { hasNextPage hasPreviousPage endCursor }
                    edges { cursor node { id status apiKeyID traceID dataStorageID } }
                  }
                }
              }
            }
            """,
            new { id = "gid://axonhub/Trace/7" });

        var connection = data.GetProperty("node").GetProperty("requests");
        Assert.Equal(2, connection.GetProperty("totalCount").GetInt32());
        var edges = connection.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(2, edges.Length);
        // Request ID resolvers always build a GUID: a missing key becomes
        // APIKey/0, while dataStorageID is the one zero-to-null exception
        // (C190).
        Assert.Equal(
            "gid://axonhub/APIKey/0",
            edges[0].GetProperty("node").GetProperty("apiKeyID").GetString());
        Assert.Equal(
            "gid://axonhub/Trace/7",
            edges[0].GetProperty("node").GetProperty("traceID").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            edges[0].GetProperty("node").GetProperty("dataStorageID").ValueKind);
        Assert.Equal(
            "gid://axonhub/DataStorage/42",
            edges[1].GetProperty("node").GetProperty("dataStorageID").GetString());
        Assert.False(connection.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());
        Assert.False(
            connection.GetProperty("pageInfo").GetProperty("hasPreviousPage").GetBoolean());

        var query = Assert.Single(_requests.Queries);
        Assert.Equal(7, query.TraceScope);
        Assert.Equal(RequestSortField.CreatedAt, query.SortField);
        Assert.Equal(2, query.First);
        Assert.Equal("completed", query.Where?.Status);
    }

    [Fact]
    public async Task TraceRequestsWhereTimePredicatesReachTheStore()
    {
        AddProject(1);
        _traces.Seed(record =>
        {
            record.Id = 7;
            record.TraceId = "trace-abc";
            record.ProjectId = 1;
            record.CreatedAt = CreatedAt;
            record.UpdatedAt = UpdatedAt;
        });
        SeedCompletedRequest(11, traceId: 7, "first");

        var data = await QueryDataAsync(
            """
            query Trace($id: ID!, $where: RequestWhereInput) {
              node(id: $id) {
                ... on Trace {
                  requests(where: $where) { totalCount }
                }
              }
            }
            """,
            new
            {
                id = "gid://axonhub/Trace/7",
                where = new
                {
                    createdAtGTE = "2026-01-01T00:00:00Z",
                    createdAtLT = "2026-02-01T00:00:00Z",
                    updatedAtGT = "2026-01-02T03:04:05.5Z",
                    updatedAtLTE = "2026-01-03T00:00:00Z",
                },
            });

        Assert.Equal(
            1,
            data.GetProperty("node").GetProperty("requests").GetProperty("totalCount").GetInt32());
        var filter = Assert.Single(_requests.Queries).Where;
        Assert.NotNull(filter);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), filter.CreatedAtGTE);
        Assert.Equal(new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), filter.CreatedAtLT);
        Assert.Equal(
            new DateTime(2026, 1, 2, 3, 4, 5, 500, DateTimeKind.Utc),
            filter.UpdatedAtGT);
        Assert.Equal(new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc), filter.UpdatedAtLTE);
    }

    [Fact]
    public async Task ThreadsQueryMatchesTheFrontendShape()
    {
        AddProject(1);
        SeedThread(3, "thread-xyz");
        _traces.Seed(trace =>
        {
            trace.Id = 1;
            trace.TraceId = "trace-1";
            trace.Status = "active";
            trace.ProjectId = 1;
            trace.ThreadId = 3;
            trace.CreatedAt = CreatedAt;
            trace.UpdatedAt = UpdatedAt;
        });
        _traces.Seed(trace =>
        {
            trace.Id = 2;
            trace.TraceId = "trace-2";
            trace.Status = "retained";
            trace.ProjectId = 1;
            trace.ThreadId = 3;
            trace.CreatedAt = CreatedAt.AddMinutes(1);
            trace.UpdatedAt = UpdatedAt.AddMinutes(1);
        });
        _traces.Seed(trace =>
        {
            trace.Id = 3;
            trace.TraceId = "trace-3";
            trace.Status = "archived";
            trace.ProjectId = 1;
            trace.ThreadId = 3;
            trace.CreatedAt = CreatedAt.AddMinutes(2);
            trace.UpdatedAt = UpdatedAt.AddMinutes(2);
        });
        SeedCompletedRequest(11, traceId: 1, "older question");
        SeedCompletedRequest(12, traceId: 2, "newer question");

        var data = await QueryDataAsync(
            """
            query GetThreads($first: Int, $where: ThreadWhereInput) {
              threads(first: $first, where: $where) {
                edges {
                  node {
                    id
                    threadID
                    status
                    createdAt
                    updatedAt
                    project { id name }
                    tracesSummary: traces(first: 1, where: { statusNEQ: archived }) {
                      totalCount
                    }
                    archivedTracesCount
                    firstUserQuery
                  }
                  cursor
                }
                pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                totalCount
              }
            }
            """,
            new { first = 10, where = new { statusNEQ = "archived" } });

        var connection = data.GetProperty("threads");
        Assert.Equal(1, connection.GetProperty("totalCount").GetInt32());
        var node = Assert.Single(connection.GetProperty("edges").EnumerateArray())
            .GetProperty("node");
        Assert.Equal("gid://axonhub/Thread/3", node.GetProperty("id").GetString());
        Assert.Equal("thread-xyz", node.GetProperty("threadID").GetString());
        Assert.Equal("active", node.GetProperty("status").GetString());
        Assert.Equal("gid://axonhub/Project/1", node.GetProperty("project").GetProperty("id").GetString());
        Assert.Equal(
            2,
            node.GetProperty("tracesSummary").GetProperty("totalCount").GetInt32());
        Assert.Equal(1, node.GetProperty("archivedTracesCount").GetInt32());
        Assert.Equal("older question", node.GetProperty("firstUserQuery").GetString());

        var query = Assert.Single(_threads.Queries);
        Assert.Equal(ThreadSortField.Id, query.SortField);
        Assert.Equal("archived", query.Where?.StatusNEQ);

        // The tracesSummary edge is thread-scoped and keeps the user filter
        // on top of the implicit ThreadIDEQ predicate.
        var traceQuery = Assert.Single(_traces.Queries);
        Assert.Equal(3, traceQuery.ThreadScope);
        Assert.Equal("archived", traceQuery.Where?.StatusNEQ);
    }

    [Fact]
    public async Task ThreadDetailKeepsCreatedAtOrderOnTheTracesEdge()
    {
        AddProject(1);
        SeedThread(3, "thread-xyz");
        _traces.Seed(trace =>
        {
            trace.Id = 1;
            trace.TraceId = "trace-1";
            trace.ProjectId = 1;
            trace.ThreadId = 3;
            trace.CreatedAt = CreatedAt.AddMinutes(2);
            trace.UpdatedAt = UpdatedAt.AddMinutes(2);
        });
        _traces.Seed(trace =>
        {
            trace.Id = 2;
            trace.TraceId = "trace-2";
            trace.ProjectId = 1;
            trace.ThreadId = 3;
            trace.CreatedAt = CreatedAt;
            trace.UpdatedAt = UpdatedAt;
        });
        _usage.Threads[3] = new UsageMetadataRow(11, 22, 33, 4, 5, 0.5);

        var data = await QueryDataAsync(
            """
            query GetThreadDetail($id: ID!, $traceWhere: TraceWhereInput) {
              node(id: $id) {
                ... on Thread {
                  id
                  usageMetadata {
                    totalInputTokens
                    totalOutputTokens
                    totalTokens
                    totalCache: totalCachedTokens
                    totalCost
                  }
                  tracesConnection: traces(
                    first: 10
                    orderBy: { field: CREATED_AT, direction: ASC }
                    where: $traceWhere
                  ) {
                    totalCount
                    edges { node { id traceID } }
                  }
                }
              }
            }
            """,
            new { id = "gid://axonhub/Thread/3", traceWhere = new { } });

        var node = data.GetProperty("node");
        var usage = node.GetProperty("usageMetadata");
        Assert.Equal(11, usage.GetProperty("totalInputTokens").GetInt32());
        Assert.Equal(22, usage.GetProperty("totalOutputTokens").GetInt32());
        Assert.Equal(33, usage.GetProperty("totalTokens").GetInt32());
        Assert.Equal(4, usage.GetProperty("totalCache").GetInt32());
        Assert.Equal(0.5, usage.GetProperty("totalCost").GetDouble());

        var connection = node.GetProperty("tracesConnection");
        Assert.Equal(2, connection.GetProperty("totalCount").GetInt32());
        var edges = connection.GetProperty("edges").EnumerateArray().ToArray();
        // created_at ascending: id 2 predates id 1 even though its id is lower.
        Assert.Equal("gid://axonhub/Trace/2", edges[0].GetProperty("node").GetProperty("id").GetString());
        Assert.Equal("gid://axonhub/Trace/1", edges[1].GetProperty("node").GetProperty("id").GetString());

        var traceQuery = Assert.Single(_traces.Queries);
        Assert.Equal(3, traceQuery.ThreadScope);
        Assert.Equal(TraceSortField.CreatedAt, traceQuery.SortField);
        Assert.Equal(SortDirection.Asc, traceQuery.Direction);

        // The frontend sends `where: {}` when archived traces are shown; the
        // empty input becomes an empty filter, never an error.
        Assert.NotNull(traceQuery.Where);
        Assert.Null(traceQuery.Where.Status);
        Assert.Null(traceQuery.Where.StatusNEQ);
        Assert.Null(traceQuery.Where.ThreadId);
    }

    [Fact]
    public async Task ThreadCreatedAtOrderFallsBackToId()
    {
        AddProject(1);
        SeedThread(3, "thread-xyz");

        await QueryDataAsync(
            """
            query Threads($orderBy: ThreadOrder) {
              threads(first: 10, orderBy: $orderBy) { totalCount }
            }
            """,
            new
            {
                orderBy = new
                {
                    field = "CREATED_AT",
                    direction = "DESC",
                },
            });

        var query = Assert.Single(_threads.Queries);
        Assert.Equal(ThreadSortField.Id, query.SortField);
        Assert.Equal(SortDirection.Desc, query.Direction);
    }

    [Fact]
    public async Task ThreadArchivedTracesCountFailureKeepsThePrefix()
    {
        AddProject(1);
        SeedThread(3, "thread-xyz");
        _traces.CountArchivedFailure = new InvalidOperationException("count blew up");

        var errors = await QueryErrorsAsync(
            """
            query Thread($id: ID!) {
              node(id: $id) { ... on Thread { archivedTracesCount } }
            }
            """,
            new { id = "gid://axonhub/Thread/3" });

        Assert.Equal(
            "failed to count archived traces: count blew up",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task TraceAndThreadNodeLookupsReturnTheRows()
    {
        AddProject(1);
        SeedThread(3, "thread-xyz");
        _traces.Seed(record =>
        {
            record.Id = 7;
            record.TraceId = "trace-abc";
            record.ProjectId = 1;
            record.ThreadId = 3;
        });

        var traceData = await QueryDataAsync(
            """
            query Trace($id: ID!) {
              node(id: $id) { id ... on Trace { traceID status } }
            }
            """,
            new { id = "gid://axonhub/Trace/7" });
        var traceNode = traceData.GetProperty("node");
        Assert.Equal("gid://axonhub/Trace/7", traceNode.GetProperty("id").GetString());
        Assert.Equal("trace-abc", traceNode.GetProperty("traceID").GetString());
        Assert.Equal("active", traceNode.GetProperty("status").GetString());

        var threadData = await QueryDataAsync(
            """
            query Thread($id: ID!) {
              node(id: $id) { id ... on Thread { threadID status } }
            }
            """,
            new { id = "gid://axonhub/Thread/3" });
        var threadNode = threadData.GetProperty("node");
        Assert.Equal("gid://axonhub/Thread/3", threadNode.GetProperty("id").GetString());
        Assert.Equal("thread-xyz", threadNode.GetProperty("threadID").GetString());
        Assert.Equal("active", threadNode.GetProperty("status").GetString());
    }

    [Fact]
    public async Task UnknownTraceOrThreadNodeFailsWithEntNotFound()
    {
        var traceErrors = await QueryErrorsAsync(
            """
            query Trace($id: ID!) {
              node(id: $id) { id ... on Trace { traceID } }
            }
            """,
            new { id = "gid://axonhub/Trace/999" });
        Assert.Equal("ent: trace not found", traceErrors[0].GetProperty("message").GetString());

        var threadErrors = await QueryErrorsAsync(
            """
            query Thread($id: ID!) {
              node(id: $id) { id ... on Thread { threadID } }
            }
            """,
            new { id = "gid://axonhub/Thread/999" });
        Assert.Equal("ent: thread not found", threadErrors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task TraceStatusMutationsCallTheReferenceTransitions()
    {
        _traces.Seed(record => record.Id = 1);
        _traces.Seed(record =>
        {
            record.Id = 2;
            record.Status = "archived";
        });
        _traces.Seed(record => record.Id = 3);
        _traces.Seed(record =>
        {
            record.Id = 4;
            record.Status = "retained";
        });

        Assert.True(await MutateTrueAsync("archiveTrace", "Trace", 1));
        Assert.True(await MutateTrueAsync("unarchiveTrace", "Trace", 2));
        Assert.True(await MutateTrueAsync("retainTrace", "Trace", 3));
        Assert.True(await MutateTrueAsync("unretainTrace", "Trace", 4));

        Assert.Collection(
            _traces.Updates,
            update => Assert.Equal((1L, (long?)null, "active", "archived"), update),
            update => Assert.Equal((2L, (long?)null, "archived", "active"), update),
            update => Assert.Equal((3L, (long?)null, "active", "retained"), update),
            update => Assert.Equal((4L, (long?)null, "retained", "active"), update));
    }

    [Fact]
    public async Task TraceMutationOnWrongStatusReturnsTheCannotMessage()
    {
        _traces.Seed(record =>
        {
            record.Id = 1;
            record.Status = "archived";
        });

        var errors = await QueryErrorsAsync(
            """
            mutation Archive($id: ID!) { archiveTrace(id: $id) }
            """,
            new { id = "gid://axonhub/Trace/1" });

        Assert.Equal(
            "cannot archive trace: current status is not active or trace not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ThreadStatusMutationsCallTheReferenceTransitions()
    {
        SeedThread(1, "thread-1");
        _threads.Seed(record =>
        {
            record.Id = 2;
            record.ThreadId = "thread-2";
            record.Status = "archived";
            record.ProjectId = 1;
            record.CreatedAt = CreatedAt;
            record.UpdatedAt = UpdatedAt;
        });

        Assert.True(await MutateTrueAsync("archiveThread", "Thread", 1));
        Assert.True(await MutateTrueAsync("unarchiveThread", "Thread", 2));

        Assert.Collection(
            _threads.Updates,
            update => Assert.Equal((1L, (long?)null, "active", "archived"), update),
            update => Assert.Equal((2L, (long?)null, "archived", "active"), update));
    }

    [Fact]
    public async Task ThreadCascadeFailureKeepsItsOwnPrefix()
    {
        SeedThread(3, "thread-xyz");
        _threads.CascadeFailure = new InvalidOperationException("cascade blew up");

        var errors = await QueryErrorsAsync(
            """
            mutation Archive($id: ID!) { archiveThread(id: $id) }
            """,
            new { id = "gid://axonhub/Thread/3" });

        Assert.Equal(
            "failed to cascade archive traces: cascade blew up",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ThreadMutationOnMissingRowReturnsTheCannotMessage()
    {
        var errors = await QueryErrorsAsync(
            """
            mutation Archive($id: ID!) { archiveThread(id: $id) }
            """,
            new { id = "gid://axonhub/Thread/999" });

        Assert.Equal(
            "cannot archive thread: current status is not active or thread not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task TracesAreScopedToTheContextProjectForProjectMembers()
    {
        var member = _gate.Users.Add(user =>
        {
            user.Id = 12;
            user.Email = "member@example.com";
            user.Status = "activated";
            user.IsOwner = false;
        });
        await _users.CreateAsync(
            new UserRecord
            {
                Id = 12,
                Email = "member@example.com",
                Status = "activated",
            },
            roleIds: null);
        _users.AssignProject(12, projectId: 5, isOwner: false, scopes: ["read_requests"]);
        AddProject(5);
        _traces.Seed(trace =>
        {
            trace.Id = 1;
            trace.ProjectId = 5;
        });
        _traces.Seed(trace =>
        {
            trace.Id = 2;
            trace.ProjectId = 6;
        });

        var data = await QueryDataAsync(
            """
            query Traces {
              traces(first: 10) { totalCount edges { node { id projectID } } }
            }
            """,
            variables: null,
            token: _gate.IssueJwt(member),
            extraHeaders: new Dictionary<string, string>
            {
                ["X-Project-ID"] = "gid://axonhub/Project/5",
            });

        var connection = data.GetProperty("traces");
        Assert.Equal(1, connection.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            5,
            Assert.Single(_traces.Queries).ProjectScope);
        Assert.Equal(
            "gid://axonhub/Project/5",
            connection.GetProperty("edges")[0].GetProperty("node").GetProperty("projectID").GetString());
    }

    [Fact]
    public async Task ServiceAccountApiKeyHasNoTraceAccess()
    {
        _traces.Seed(record => record.Id = 1);
        _gate.AddServiceAccountKey();

        var errors = await QueryErrorsAsync(
            """
            query Traces { traces(first: 10) { totalCount } }
            """,
            variables: null,
            token: "sk-service-account");

        // The presenter maps a privacy deny to the reference's FORBIDDEN error.
        Assert.Equal("permission denied", errors[0].GetProperty("message").GetString());
    }

    private void AddProject(long id)
        => _gate.Projects.Add(project =>
        {
            project.Id = id;
            project.Name = "project";
            project.Status = "active";
        });

    private void SeedThread(long id, string threadId)
        => _threads.Seed(record =>
        {
            record.Id = id;
            record.ThreadId = threadId;
            record.ProjectId = 1;
            record.CreatedAt = CreatedAt;
            record.UpdatedAt = UpdatedAt;
        });

    private void SeedCompletedRequest(
        long id,
        long traceId,
        string userQuery,
        long? dataStorageId = null)
        => _requests.Seed(request =>
        {
            request.Id = id;
            request.TraceId = traceId;
            request.DataStorageId = dataStorageId;
            request.ProjectId = 1;
            request.Status = "completed";
            request.ModelId = "gpt-4o";
            request.Format = "openai/chat_completions";
            request.RequestBody = ChatRequestBody.Replace(
                "Hello there",
                userQuery,
                StringComparison.Ordinal);
            request.ResponseBody = ChatResponseBody;
            request.CreatedAt = CreatedAt;
            request.UpdatedAt = UpdatedAt;
        });

    private async Task<bool> MutateTrueAsync(string field, string type, long id)
    {
        var data = await QueryDataAsync(
            "mutation Mutate($id: ID!) { " + field + "(id: $id) }",
            new { id = "gid://axonhub/" + type + "/" + id });
        return data.GetProperty(field).GetBoolean();
    }

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null,
        string? token = null,
        Dictionary<string, string>? extraHeaders = null)
    {
        var root = await ExecuteAsync(query, variables, token, extraHeaders);
        Assert.False(root.TryGetProperty("errors", out var errors), errors.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(
        string query,
        object? variables = null,
        string? token = null)
    {
        var root = await ExecuteAsync(query, variables, token, extraHeaders: null);
        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        return errors.EnumerateArray().Select(static error => error.Clone()).ToArray();
    }

    private async Task<JsonElement> ExecuteAsync(
        string query,
        object? variables,
        string? token,
        Dictionary<string, string>? extraHeaders)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/graphql")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query, variables }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            token ?? _gate.Jwt);
        if (extraHeaders is not null)
        {
            foreach (var (name, value) in extraHeaders)
            {
                request.Headers.Add(name, value);
            }
        }

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
