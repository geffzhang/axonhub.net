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
/// Locks the requests GraphQL surface over /admin/graphql against the queries
/// the frontend sends (frontend/src/features/requests): the root requests
/// connection with its CREATED_AT→id order rewrite, the ported where
/// predicates, the node(id) Request branch, the body/chunk loaders, the
/// APIKey/Project/Trace edges and the nested executions/usageLogs
/// connections (ent.resolvers.go:496-935, gql_edge.go:471-551).
/// </summary>
public sealed class AdminRequestsEndpointTests : IAsyncLifetime
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 1, 2, 3, 4, 7, DateTimeKind.Utc);

    private const string ChatRequestBody =
        """{"model":"gpt-4o","messages":[{"role":"user","content":"Hello there"}]}""";

    private const string ChatResponseBody =
        """{"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"gpt-4o","choices":[{"index":0,"message":{"role":"assistant","content":"Hi!"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":20,"total_tokens":30}}""";

    private const string CostItemsJson =
        """[{"itemCode":"prompt_tokens","quantity":10,"tierBreakdown":[{"upTo":1000,"units":10,"subtotal":0.5}],"subtotal":0.5},{"itemCode":"completion_tokens","quantity":20,"subtotal":1.5}]""";

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeRequestSegmentStore _requests = new();
    private readonly RequestsSliceExecutionStore _executions = new();
    private readonly RequestsSliceUsageLogStore _usageLogs = new();
    private readonly FakeChannelStore _channels = new();
    private readonly FakeTraceAdminStore _traces = new();
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
        builder.Services.AddSingleton<IRequestSegmentStore>(_requests);
        builder.Services.AddSingleton<IRequestExecutionStore>(_executions);
        builder.Services.AddSingleton<IUsageLogStore>(_usageLogs);
        builder.Services.AddSingleton<IChannelStore>(_channels);
        builder.Services.AddSingleton<ITraceAdminStore>(_traces);
        builder.Services.AddSingleton<IDataStorageStore>(_dataStorages);
        builder.Services.AddSingleton<IDataStorageBackendFactory>(_backends);
        builder.Services.AddSingleton<ISystemSettingsAdminService>(
            new SystemSettingsAdminService(_systems));
        builder.Services.AddScoped<PermissionValidator>();
        builder.Services.AddScoped<ApiKeyPrivacy>();
        builder.Services.AddScoped<ModelPrivacy>();
        builder.Services.AddScoped<TracePrivacy>();
        builder.Services.AddScoped<TraceSegmentService>();
        builder.Services.AddScoped<RequestManagementService>();
        builder.Services.AddScoped<RequestQueryService>();

        // DataStorageBootstrap seeds the primary database storage on a real
        // install. Without it the payload loaders take the "external storage
        // not configured" branch and return {} for every row body.
        _dataStorages.Add(new DataStorageRecord
        {
            Name = "Primary",
            Description = "Primary database storage",
            Primary = true,
            Type = "database",
            Status = "active",
        });

        // The gate's JWT user (id 9, owner) is the caller every fixture
        // client sends; the request policy treats owners as fully visible.
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
    public async Task RequestsQueryMatchesTheFrontendShape()
    {
        SeedLookups();
        SeedRequest(11, request =>
        {
            request.ApiKeyId = 2;
            request.ChannelId = 4;
            request.TraceId = 7;
            request.Stream = true;
            request.RequestHeaders = """{"x-trace":"1"}""";
            request.ResponseChunks = """[{"id":"chunk-1"},{"id":"chunk-2"}]""";
            request.ExternalId = "req-ext";
            request.MetricsLatencyMs = 120;
            request.MetricsFirstTokenLatencyMs = 30;
            request.MetricsReasoningDurationMs = 10;
            request.ContentSaved = true;
            request.ContentStorageId = 5;
            request.ContentStorageKey = "requests/11.json";
            request.ContentSavedAt = UpdatedAt;
        });
        SeedExecution(21, execution =>
        {
            execution.Status = "failed";
            execution.Stream = true;
            execution.ChannelId = 4;
            execution.ChannelApiKeyIndex = 3;
            execution.ChannelApiKeySuffix = "f00d";
            execution.ResponseBody = """{"error":"rate limited"}""";
            execution.ResponseChunks = """[{"id":"chunk-e1"}]""";
            execution.ErrorMessage = "rate limited";
            execution.ResponseStatusCode = 429;
            execution.ExternalId = "exec-ext";
            execution.RequestUrl = "https://upstream.example/v1/chat/completions";
            execution.PassThroughApplied = true;
            execution.CreatedAt = CreatedAt;
            execution.UpdatedAt = UpdatedAt;
        });
        SeedExecution(22, execution =>
        {
            execution.Status = "completed";
            execution.Stream = false;
            execution.ResponseBody = ChatResponseBody;
            execution.ResponseChunks = """[{"id":"ignored"}]""";
            execution.CreatedAt = UpdatedAt;
            execution.UpdatedAt = Later;
        });
        _usageLogs.Seed(usageLog =>
        {
            usageLog.Id = 31;
            usageLog.RequestId = 11;
            usageLog.ApiKeyId = 2;
            usageLog.ProjectId = 1;
            usageLog.ChannelId = 9;
            usageLog.ModelId = "gpt-4o";
            usageLog.PromptTokens = 10;
            usageLog.CompletionTokens = 20;
            usageLog.TotalTokens = 30;
            usageLog.Source = "playground";
            usageLog.TotalCost = 2.5;
            usageLog.CostItems = CostItemsJson;
            usageLog.CostPriceReferenceId = "price-1";
            usageLog.CreatedAt = CreatedAt;
            usageLog.UpdatedAt = UpdatedAt;
        });

        var data = await QueryDataAsync(
            """
            query GetRequests($first: Int, $orderBy: RequestOrder) {
              requests(first: $first, orderBy: $orderBy) {
                edges {
                  node {
                    id
                    createdAt
                    updatedAt
                    projectID
                    apiKeyID
                    traceID
                    dataStorageID
                    channelID
                    externalID
                    source
                    modelID
                    reasoningEffort
                    format
                    status
                    stream
                    metricsLatencyMs
                    metricsFirstTokenLatencyMs
                    metricsReasoningDurationMs
                    requestHeaders
                    requestBody
                    responseHeaders
                    responseBody
                    responseChunks
                    contentSaved
                    contentStorageID
                    contentStorageKey
                    contentSavedAt
                    apiKey { id name }
                    project { id name }
                    trace { id traceID }
                    executions(first: 10, orderBy: {
                      field: CREATED_AT, direction: ASC
                    }) {
                      edges {
                        node {
                          id
                          projectID
                          requestID
                          channelID
                          channelAPIKeyIndex
                          channelAPIKeySuffix
                          errorMessage
                          responseStatusCode
                          externalID
                          requestURL
                          passThroughApplied
                          responseBody
                          responseChunks
                          status
                          request { id }
                          channel { id }
                        }
                        cursor
                      }
                      pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                      totalCount
                    }
                    usageLogs(first: 10, where: {
                      sourceIn: [playground], apiKeyID: 2
                    }) {
                      totalCount
                    }
                  }
                  cursor
                }
                pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                totalCount
              }
            }
            """,
            new
            {
                first = 10,
                orderBy = new { field = "CREATED_AT", direction = "DESC" },
            });

        var connection = data.GetProperty("requests");
        Assert.Equal(1, connection.GetProperty("totalCount").GetInt32());

        var edge = Assert.Single(connection.GetProperty("edges").EnumerateArray());
        var node = edge.GetProperty("node");
        Assert.Equal("gid://axonhub/Request/11", node.GetProperty("id").GetString());
        Assert.Equal("2026-01-02T03:04:05Z", node.GetProperty("createdAt").GetString());
        Assert.Equal("2026-01-02T03:04:06Z", node.GetProperty("updatedAt").GetString());
        Assert.Equal("gid://axonhub/Project/1", node.GetProperty("projectID").GetString());
        Assert.Equal("gid://axonhub/APIKey/2", node.GetProperty("apiKeyID").GetString());
        Assert.Equal("gid://axonhub/Trace/7", node.GetProperty("traceID").GetString());
        Assert.Equal(JsonValueKind.Null, node.GetProperty("dataStorageID").ValueKind);
        Assert.Equal("gid://axonhub/Channel/4", node.GetProperty("channelID").GetString());
        Assert.Equal("req-ext", node.GetProperty("externalID").GetString());
        Assert.Equal("api", node.GetProperty("source").GetString());
        Assert.Equal("gpt-4o", node.GetProperty("modelID").GetString());
        Assert.Equal(JsonValueKind.Null, node.GetProperty("reasoningEffort").ValueKind);
        Assert.Equal("openai/chat_completions", node.GetProperty("format").GetString());
        Assert.Equal("completed", node.GetProperty("status").GetString());
        Assert.True(node.GetProperty("stream").GetBoolean());
        Assert.Equal(120, node.GetProperty("metricsLatencyMs").GetInt32());
        Assert.Equal(30, node.GetProperty("metricsFirstTokenLatencyMs").GetInt32());
        Assert.Equal(10, node.GetProperty("metricsReasoningDurationMs").GetInt32());

        var headers = node.GetProperty("requestHeaders");
        Assert.Equal("1", headers.GetProperty("x-trace").GetString());
        Assert.Equal(JsonValueKind.Null, node.GetProperty("responseHeaders").ValueKind);

        // The loaders return the stored bytes (already compact JSON).
        Assert.Equal("gpt-4o", node.GetProperty("requestBody").GetProperty("model").GetString());
        Assert.Equal(
            "Hi!",
            node.GetProperty("responseBody")
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString());
        var requestChunks = node.GetProperty("responseChunks").EnumerateArray().ToArray();
        Assert.Equal(2, requestChunks.Length);
        Assert.Equal("chunk-1", requestChunks[0].GetProperty("id").GetString());
        Assert.Equal("chunk-2", requestChunks[1].GetProperty("id").GetString());

        Assert.True(node.GetProperty("contentSaved").GetBoolean());
        Assert.Equal(5, node.GetProperty("contentStorageID").GetInt32());
        Assert.Equal("requests/11.json", node.GetProperty("contentStorageKey").GetString());
        Assert.Equal("2026-01-02T03:04:06Z", node.GetProperty("contentSavedAt").GetString());

        Assert.Equal(
            "gid://axonhub/APIKey/2",
            node.GetProperty("apiKey").GetProperty("id").GetString());
        Assert.Equal("key-2", node.GetProperty("apiKey").GetProperty("name").GetString());
        Assert.Equal(
            "gid://axonhub/Project/1",
            node.GetProperty("project").GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/Trace/7",
            node.GetProperty("trace").GetProperty("id").GetString());
        Assert.Equal(
            "trace-7",
            node.GetProperty("trace").GetProperty("traceID").GetString());

        var executions = node.GetProperty("executions");
        Assert.Equal(2, executions.GetProperty("totalCount").GetInt32());
        var executionEdges = executions.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(
            ["gid://axonhub/RequestExecution/21", "gid://axonhub/RequestExecution/22"],
            executionEdges
                .Select(item => item.GetProperty("node").GetProperty("id").GetString()!)
                .ToArray());

        var failed = executionEdges[0].GetProperty("node");
        Assert.Equal(1, failed.GetProperty("projectID").GetInt32());
        Assert.Equal(
            "gid://axonhub/Request/11",
            failed.GetProperty("requestID").GetString());
        Assert.Equal(
            "gid://axonhub/Channel/4",
            failed.GetProperty("channelID").GetString());
        Assert.Equal(3, failed.GetProperty("channelAPIKeyIndex").GetInt32());
        Assert.Equal("f00d", failed.GetProperty("channelAPIKeySuffix").GetString());
        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Equal("rate limited", failed.GetProperty("errorMessage").GetString());
        Assert.Equal(429, failed.GetProperty("responseStatusCode").GetInt32());
        Assert.Equal("exec-ext", failed.GetProperty("externalID").GetString());
        Assert.Equal(
            "https://upstream.example/v1/chat/completions",
            failed.GetProperty("requestURL").GetString());
        Assert.True(failed.GetProperty("passThroughApplied").GetBoolean());

        // A non-completed execution reports the empty body even when a body was
        // stored (request.go:1758-1791).
        Assert.Empty(failed.GetProperty("responseBody").EnumerateObject());
        // failed is a finished stream status, so the chunks pass through.
        Assert.Equal(
            "chunk-e1",
            Assert.Single(failed.GetProperty("responseChunks").EnumerateArray())
                .GetProperty("id")
                .GetString());
        Assert.Equal(
            "gid://axonhub/Request/11",
            failed.GetProperty("request").GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/Channel/4",
            failed.GetProperty("channel").GetProperty("id").GetString());

        var completed = executionEdges[1].GetProperty("node");
        Assert.Equal("completed", completed.GetProperty("status").GetString());
        // The resolver always serializes a GUID, an absent edge is Channel/0.
        Assert.Equal(
            "gid://axonhub/Channel/0",
            completed.GetProperty("channelID").GetString());
        Assert.Equal(JsonValueKind.Null, completed.GetProperty("channelAPIKeyIndex").ValueKind);
        Assert.Equal(JsonValueKind.Null, completed.GetProperty("channelAPIKeySuffix").ValueKind);
        Assert.Equal(JsonValueKind.Null, completed.GetProperty("channel").ValueKind);
        // !stream short-circuits the chunk loader.
        Assert.Empty(completed.GetProperty("responseChunks").EnumerateArray());
        Assert.Equal(
            "chatcmpl-1",
            completed.GetProperty("responseBody").GetProperty("id").GetString());

        // Root order rewrite: CREATED_AT becomes ent.DefaultRequestOrder's id,
        // the descending direction survives (ent.resolvers.go:497-505).
        var query = Assert.Single(_requests.Queries);
        Assert.Equal(RequestSortField.Id, query.SortField);
        Assert.Equal(SortDirection.Desc, query.Direction);
        Assert.Null(query.ProjectScope);
        Assert.Null(query.VisibilityUserId);

        // The edge connection keeps CREATED_AT (entgql default for edges).
        var executionQuery = Assert.Single(_executions.Queries);
        Assert.Equal(11, executionQuery.RequestScope);
        Assert.Equal(RequestExecutionSortField.CreatedAt, executionQuery.SortField);
        Assert.Equal(SortDirection.Asc, executionQuery.Direction);

        var usageLogQuery = Assert.Single(_usageLogs.Queries);
        Assert.Equal(11, usageLogQuery.RequestScope);
        Assert.Equal(UsageLogSortField.Id, usageLogQuery.SortField);
        Assert.Equal(2, usageLogQuery.Where?.ApiKeyId);
        Assert.Equal(["playground"], usageLogQuery.Where?.SourceIn);
    }

    [Fact]
    public async Task RequestsWherePredicatesReachTheStore()
    {
        AddProject(1);
        SeedRequest(11);

        await QueryDataAsync(
            """
            query Requests($where: RequestWhereInput) {
              requests(first: 10, where: $where) { totalCount }
            }
            """,
            new
            {
                where = new
                {
                    createdAtGTE = "2026-01-02T03:04:05Z",
                    statusIn = new[] { "completed", "failed" },
                    sourceIn = new[] { "playground" },
                    channelIDIn = new[] { "gid://axonhub/Channel/4" },
                    apiKeyIDIn = new[] { "gid://axonhub/APIKey/2" },
                    modelIDContainsFold = "gpt",
                    projectID = "gid://axonhub/Project/1",
                    dataStorageIDIsNil = true,
                    reasoningEffortNotNil = true,
                },
            });

        var filter = Assert.Single(_requests.Queries).Where;
        Assert.NotNull(filter);
        Assert.Equal(CreatedAt, filter.CreatedAtGTE);
        Assert.Equal(["completed", "failed"], filter.StatusIn);
        Assert.Equal(["playground"], filter.SourceIn);
        Assert.Equal([4], filter.ChannelIdIn);
        Assert.Equal([2], filter.ApiKeyIdIn);
        Assert.Equal("gpt", filter.ModelIdContainsFold);
        Assert.Equal(1, filter.ProjectId);
        Assert.True(filter.DataStorageIdIsNil);
        Assert.True(filter.ReasoningEffortNotNil);
    }

    [Fact]
    public async Task RequestNodeReturnsBodiesAndChunkRules()
    {
        AddProject(1);

        // A completed stream whose chunks were moved to external storage: the
        // marker collapses to an empty list (request.go:143-151).
        SeedRequest(11, request =>
        {
            request.Stream = true;
            request.ResponseChunks = """ [{"_ext":1}] """;
        });
        // A request that is still processing never exposes bodies or chunks.
        SeedRequest(12, request =>
        {
            request.Status = "processing";
            request.Stream = true;
            request.ResponseBody = """{"partial":true}""";
            request.ResponseChunks = """[{"id":"chunk-1"}]""";
        });
        // Empty stored bodies load as {}.
        SeedRequest(13, request =>
        {
            request.RequestBody = string.Empty;
            request.ResponseBody = string.Empty;
        });

        foreach (var (id, expectedBody, expectedChunks) in new[]
        {
            (11, ChatResponseBody, 0),
            (12, "{}", 0),
            (13, "{}", 0),
        })
        {
            var data = await QueryDataAsync(
                """
                query Node($id: ID!) {
                  node(id: $id) {
                    ... on Request {
                      id
                      requestBody
                      responseBody
                      responseChunks
                    }
                  }
                }
                """,
                new { id = "gid://axonhub/Request/" + id });

            var node = data.GetProperty("node");
            Assert.Equal("gid://axonhub/Request/" + id, node.GetProperty("id").GetString());
            if (expectedBody == "{}")
            {
                Assert.Empty(node.GetProperty("responseBody").EnumerateObject());
            }
            else
            {
                Assert.Equal(
                    "chatcmpl-1",
                    node.GetProperty("responseBody").GetProperty("id").GetString());
            }

            Assert.Equal(
                expectedChunks,
                node.GetProperty("responseChunks").GetArrayLength());
        }
    }

    [Fact]
    public async Task UsageLogsConnectionReturnsBillingFields()
    {
        SeedLookups();
        SeedRequest(11);
        _usageLogs.Seed(usageLog =>
        {
            usageLog.Id = 31;
            usageLog.RequestId = 11;
            usageLog.ApiKeyId = 2;
            usageLog.ProjectId = 1;
            usageLog.ChannelId = 9;
            usageLog.ModelId = "gpt-4o";
            usageLog.PromptTokens = 10;
            usageLog.CompletionTokens = 20;
            usageLog.TotalTokens = 30;
            usageLog.PromptCachedTokens = 4;
            usageLog.Source = "test";
            usageLog.TotalCost = 2.5;
            usageLog.CostItems = CostItemsJson;
            usageLog.CostPriceReferenceId = "price-1";
            usageLog.CreatedAt = CreatedAt;
            usageLog.UpdatedAt = UpdatedAt;
        });

        var data = await QueryDataAsync(
            """
            query Node($id: ID!) {
              node(id: $id) {
                ... on Request {
                  usageLogs(first: 10, orderBy: {
                    field: UPDATED_AT, direction: DESC
                  }) {
                    edges {
                      node {
                        id
                        requestID
                        apiKeyID
                        projectID
                        channelID
                        modelID
                        promptTokens
                        completionTokens
                        totalTokens
                        promptCachedTokens
                        source
                        format
                        totalCost
                        costPriceReferenceID
                        costItems {
                          itemCode
                          quantity
                          subtotal
                          tierBreakdown { upTo units subtotal }
                        }
                        request { id }
                        project { id }
                        channel { id }
                      }
                      cursor
                    }
                    totalCount
                  }
                }
              }
            }
            """,
            new { id = "gid://axonhub/Request/11" });

        var connection = data.GetProperty("node").GetProperty("usageLogs");
        Assert.Equal(1, connection.GetProperty("totalCount").GetInt32());
        var node = Assert.Single(connection.GetProperty("edges").EnumerateArray())
            .GetProperty("node");

        Assert.Equal("gid://axonhub/UsageLog/31", node.GetProperty("id").GetString());
        Assert.Equal("gid://axonhub/Request/11", node.GetProperty("requestID").GetString());
        Assert.Equal(2, node.GetProperty("apiKeyID").GetInt32());
        Assert.Equal("gid://axonhub/Project/1", node.GetProperty("projectID").GetString());
        Assert.Equal("gid://axonhub/Channel/9", node.GetProperty("channelID").GetString());
        Assert.Equal("gpt-4o", node.GetProperty("modelID").GetString());
        Assert.Equal(10, node.GetProperty("promptTokens").GetInt32());
        Assert.Equal(20, node.GetProperty("completionTokens").GetInt32());
        Assert.Equal(30, node.GetProperty("totalTokens").GetInt32());
        Assert.Equal(4, node.GetProperty("promptCachedTokens").GetInt32());
        Assert.Equal("test", node.GetProperty("source").GetString());
        Assert.Equal("openai/chat_completions", node.GetProperty("format").GetString());
        Assert.Equal(2.5, node.GetProperty("totalCost").GetDouble());
        Assert.Equal("price-1", node.GetProperty("costPriceReferenceID").GetString());

        var items = node.GetProperty("costItems").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("prompt_tokens", items[0].GetProperty("itemCode").GetString());
        Assert.Equal(10, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal(0.5, items[0].GetProperty("subtotal").GetDouble());
        var tier = Assert.Single(items[0].GetProperty("tierBreakdown").EnumerateArray());
        Assert.Equal(1000, tier.GetProperty("upTo").GetInt32());
        Assert.Equal(10, tier.GetProperty("units").GetInt32());
        Assert.Equal(0.5, tier.GetProperty("subtotal").GetDouble());
        Assert.Equal("completion_tokens", items[1].GetProperty("itemCode").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("tierBreakdown").ValueKind);

        Assert.Equal(
            "gid://axonhub/Request/11",
            node.GetProperty("request").GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/Project/1",
            node.GetProperty("project").GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/Channel/9",
            node.GetProperty("channel").GetProperty("id").GetString());

        var query = Assert.Single(_usageLogs.Queries);
        Assert.Equal(UsageLogSortField.UpdatedAt, query.SortField);
        Assert.Equal(SortDirection.Desc, query.Direction);
    }

    [Fact]
    public async Task RequestNodeReportsTheReferenceErrors()
    {
        var missing = await QueryErrorsAsync(
            """
            query Node($id: ID!) { node(id: $id) { id } }
            """,
            new { id = "gid://axonhub/Request/999" });
        Assert.Equal("ent: request not found", missing[0].GetProperty("message").GetString());

        var unknown = await QueryErrorsAsync(
            """
            query Node($id: ID!) { node(id: $id) { id } }
            """,
            new { id = "gid://axonhub/Foo/1" });
        Assert.Equal("unknown node type: Foo", unknown[0].GetProperty("message").GetString());
    }

    private void SeedLookups()
    {
        AddProject(1);
        _gate.ApiKeys.Add(key =>
        {
            key.Id = 2;
            key.Name = "key-2";
            key.ProjectId = 1;
            key.Status = "enabled";
        });
        _traces.Seed(trace =>
        {
            trace.Id = 7;
            trace.TraceId = "trace-7";
            trace.ProjectId = 1;
            trace.Status = "active";
            trace.CreatedAt = CreatedAt;
            trace.UpdatedAt = UpdatedAt;
        });
        _channels.Add(new ChannelRecord
        {
            Id = 4,
            Name = "channel-4",
            Type = "openai",
            Status = "enabled",
        });
        _channels.Add(new ChannelRecord
        {
            Id = 9,
            Name = "channel-9",
            Type = "openai",
            Status = "enabled",
        });
    }

    private void AddProject(long id)
        => _gate.Projects.Add(project =>
        {
            project.Id = id;
            project.Name = "project";
            project.Status = "active";
        });

    private void SeedRequest(long id, Action<RequestRecord>? configure = null)
        => _requests.Seed(request =>
        {
            request.Id = id;
            request.ProjectId = 1;
            request.Status = "completed";
            request.ModelId = "gpt-4o";
            request.Format = "openai/chat_completions";
            request.RequestBody = ChatRequestBody;
            request.ResponseBody = ChatResponseBody;
            request.CreatedAt = CreatedAt;
            request.UpdatedAt = UpdatedAt;
            configure?.Invoke(request);
        });

    private void SeedExecution(long id, Action<RequestExecutionRecord>? configure = null)
        => _executions.Seed(execution =>
        {
            execution.Id = id;
            execution.RequestId = 11;
            execution.ProjectId = 1;
            execution.ModelId = "gpt-4o";
            execution.Format = "openai/chat_completions";
            execution.RequestBody = ChatRequestBody;
            execution.CreatedAt = CreatedAt;
            execution.UpdatedAt = UpdatedAt;
            configure?.Invoke(execution);
        });

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null,
        string? token = null)
    {
        var root = await ExecuteAsync(query, variables, token);
        Assert.False(root.TryGetProperty("errors", out var errors), errors.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(
        string query,
        object? variables = null,
        string? token = null)
    {
        var root = await ExecuteAsync(query, variables, token);
        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        return errors.EnumerateArray().Select(static error => error.Clone()).ToArray();
    }

    private async Task<JsonElement> ExecuteAsync(
        string query,
        object? variables,
        string? token)
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

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}

/// <summary>In-memory request_executions store for the Request.executions edge.</summary>
internal sealed class RequestsSliceExecutionStore : IRequestExecutionStore
{
    private long _nextId = 1;
    private readonly List<RequestExecutionRecord> _rows = [];

    public List<RequestExecutionListQuery> Queries { get; } = [];

    public RequestExecutionRecord Seed(Action<RequestExecutionRecord>? configure = null)
    {
        var record = new RequestExecutionRecord();
        configure?.Invoke(record);
        record.Id = record.Id > 0 ? record.Id : _nextId++;
        if (record.Id >= _nextId)
        {
            _nextId = record.Id + 1;
        }

        _rows.Add(record);
        return record;
    }

    public Task<ListPage<RequestExecutionRecord>> ListAsync(
        RequestExecutionListQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Queries.Add(query);

        var filtered = _rows.Where(row => row.RequestId == query.RequestScope).ToList();
        var page = FakePagination.Paginate(
            filtered,
            query.SortField != RequestExecutionSortField.Id,
            row => row.Id,
            row => query.SortField switch
            {
                RequestExecutionSortField.CreatedAt => row.CreatedAt,
                RequestExecutionSortField.UpdatedAt => row.UpdatedAt,
                _ => null,
            },
            query.Direction,
            query.After,
            query.Before,
            query.First,
            query.Last);

        return Task.FromResult(new ListPage<RequestExecutionRecord>(
            page.Items,
            page.HasNextPage,
            page.HasPreviousPage,
            page.TotalCount));
    }

    public Task<long> CreateAsync(
        RequestExecutionRecord execution,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the requests surface");

    public Task<RequestExecutionRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the requests surface");

    public Task<IReadOnlyList<RequestExecutionRecord>> ListByRequestIdAsync(
        long requestId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the requests surface");

    public Task<IReadOnlyList<RequestExecutionRecord>> ListByRequestIdDescAsync(
        long requestId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the requests surface");

    public Task<bool> UpdateStatusAsync(
        long id,
        string status,
        long? responseStatusCode,
        string? upstreamModelId,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the requests surface");

    public Task<bool> UpdateResultAsync(
        long id,
        string status,
        string? externalId,
        string? upstreamModelId,
        string? responseBody,
        bool setResponseBody,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the requests surface");

    public Task<bool> UpdateResponseChunksAsync(
        long id,
        string responseChunks,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the requests surface");

    public Task<bool> UpdateResponseHeadersAsync(
        long id,
        string responseHeaders,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the requests surface");
}

/// <summary>In-memory usage_logs store for the Request.usageLogs edge.</summary>
internal sealed class RequestsSliceUsageLogStore : IUsageLogStore
{
    private long _nextId = 1;
    private readonly List<UsageLogRecord> _rows = [];

    public List<UsageLogListQuery> Queries { get; } = [];

    public UsageLogRecord Seed(Action<UsageLogRecord>? configure = null)
    {
        var record = new UsageLogRecord();
        configure?.Invoke(record);
        record.Id = record.Id > 0 ? record.Id : _nextId++;
        if (record.Id >= _nextId)
        {
            _nextId = record.Id + 1;
        }

        _rows.Add(record);
        return record;
    }

    public Task<ListPage<UsageLogRecord>> ListAsync(
        UsageLogListQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Queries.Add(query);

        var filtered = _rows
            .Where(row => query.RequestScope is null || row.RequestId == query.RequestScope.Value)
            .ToList();
        var page = FakePagination.Paginate(
            filtered,
            query.SortField != UsageLogSortField.Id,
            row => row.Id,
            row => query.SortField switch
            {
                UsageLogSortField.CreatedAt => row.CreatedAt,
                UsageLogSortField.UpdatedAt => row.UpdatedAt,
                _ => null,
            },
            query.Direction,
            query.After,
            query.Before,
            query.First,
            query.Last);

        return Task.FromResult(new ListPage<UsageLogRecord>(
            page.Items,
            page.HasNextPage,
            page.HasPreviousPage,
            page.TotalCount));
    }

    public Task<long> CreateAsync(
        UsageLogRecord usageLog,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the requests surface");

    public Task<UsageLogRecord?> GetByIdAsync(
        long id,
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the requests surface");

    public Task<IReadOnlyList<UsageLogRecord>> ListByRequestIdAsync(
        long requestId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the requests surface");

    public Task<long> CountByApiKeyAsync(
        long apiKeyId,
        DateTime? start,
        DateTime? end,
        bool endInclusive,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the requests surface");

    public Task<UsageAggregate> AggregateByApiKeyAsync(
        long apiKeyId,
        DateTime? start,
        DateTime? end,
        bool endInclusive,
        bool needTokens,
        bool needCost,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the requests surface");

    public Task<double> SumCostByChannelAsync(
        long channelId,
        DateTime since,
        DateTime until,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the requests surface");
}

/// <summary>
/// Unit checks for the RequestQueryService channel edge: a deny collapses to
/// null, while a failed load keeps getNilableChannel's prefix
/// (internal/server/gql/graphql.go:228-246).
/// </summary>
public sealed class RequestQueryServiceVisibilityTests
{
    [Fact]
    public async Task ChannelQueryDenyCollapsesToNull()
    {
        var identity = new FakeManagementIdentity(new ManagementUser(1, IsOwner: false));
        var service = CreateService(identity, new FailingChannelStore());

        var channel = await service.GetVisibleChannelOrNullAsync(4);

        Assert.Null(channel);
    }

    [Fact]
    public async Task ChannelLoadFailureIsWrappedWithTheReferencePrefix()
    {
        var identity = new FakeManagementIdentity(
            new ManagementUser(AdminGraphqlGateServices.UserId, IsOwner: true));
        var service = CreateService(identity, new FailingChannelStore());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetVisibleChannelOrNullAsync(4));

        Assert.Equal("failed to load channel: boom", error.Message);
    }

    [Fact]
    public async Task NullOrZeroChannelIdReturnsNullWithoutQuerying()
    {
        var identity = new FakeManagementIdentity(new ManagementUser(1, IsOwner: false));
        var service = CreateService(identity, new FailingChannelStore());

        Assert.Null(await service.GetVisibleChannelOrNullAsync(null));
        Assert.Null(await service.GetVisibleChannelOrNullAsync(0));
    }

    private static RequestQueryService CreateService(
        FakeManagementIdentity identity,
        IChannelStore channels)
    {
        var users = new FakeUserAdminStore();
        var projects = new FakeProjectStore();
        return new RequestQueryService(
            null!,
            null!,
            null!,
            null!,
            channels,
            null!,
            new ApiKeyPrivacy(identity, users, projects),
            new ModelPrivacy(identity, users),
            new TracePrivacy(identity, users));
    }

    private sealed class FailingChannelStore : IChannelStore
    {
        public Task<long> CreateAsync(
            ChannelRecord channel,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ChannelRecord?> GetByIdAsync(
            long id,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("boom");

        public Task<IReadOnlyList<ChannelRecord>> ListEnabledAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DateTime?> GetLatestUpdatedAtAsync(
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ChannelRecord>> ListEnabledByTypesAsync(
            IReadOnlyCollection<string> types,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> UpdateStatusAsync(
            long id,
            string status,
            string? errorMessage,
            DateTime updatedAt,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
