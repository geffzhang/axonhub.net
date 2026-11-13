using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the usageLogs GraphQL surface over /admin/graphql against the queries
/// the frontend sends (frontend/src/features/requests/data/usage-logs.ts): the
/// root usageLogs connection with its CREATED_AT→id order rewrite, the where
/// mapping, the node(id) UsageLog branch, the billing columns and the
/// request / project / channel edges (ent.resolvers.go:572-586, 334-342,
/// gql_edge.go:693-715).
/// </summary>
public sealed class AdminUsageLogsEndpointTests : IAsyncLifetime
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc);

    private const string CostItemsJson =
        """[{"itemCode":"prompt_tokens","quantity":10,"tierBreakdown":[{"upTo":1000,"units":10,"subtotal":0.5}],"subtotal":0.5},{"itemCode":"completion_tokens","quantity":20,"subtotal":1.5}]""";

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly UsageLogSliceStore _usageLogs = new();
    private readonly FakeRequestSegmentStore _requests = new();
    private readonly FakeChannelStore _channels = new();
    private readonly FakeTraceAdminStore _traces = new();

    // RequestQueryService resolves the storage lookups behind the dataStorage
    // output edges (§208); this fixture only reads the UsageLog shape, so an
    // empty store is enough to activate the service.
    private readonly FakeDataStorageStore _dataStorages = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IUserAdminStore>(_users);
        builder.Services.AddSingleton<IUsageLogStore>(_usageLogs);
        builder.Services.AddSingleton<IRequestSegmentStore>(_requests);
        builder.Services.AddSingleton<IRequestExecutionStore>(new RequestsSliceExecutionStore());
        builder.Services.AddSingleton<IChannelStore>(_channels);
        builder.Services.AddSingleton<ITraceAdminStore>(_traces);
        builder.Services.AddSingleton<IDataStorageStore>(_dataStorages);
        builder.Services.AddScoped<PermissionValidator>();
        builder.Services.AddScoped<ApiKeyPrivacy>();
        builder.Services.AddScoped<ModelPrivacy>();
        builder.Services.AddScoped<TracePrivacy>();
        builder.Services.AddScoped<DataStoragePrivacy>();
        builder.Services.AddScoped<UsageLogManagementService>();
        builder.Services.AddScoped<RequestQueryService>();

        // The gate's JWT user (id 9, owner) is the caller every fixture
        // client sends; the UsageLog policy lets owners read everything.
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
    public async Task UsageLogsQueryMatchesTheFrontendShape()
    {
        SeedLookups();
        SeedUsageLog(31, log =>
        {
            log.CreatedAt = CreatedAt.AddSeconds(2);
            log.UpdatedAt = UpdatedAt;
        });
        SeedUsageLog(32, log =>
        {
            log.CreatedAt = CreatedAt;
            log.UpdatedAt = CreatedAt;
        });
        SeedUsageLog(33, log =>
        {
            log.CreatedAt = CreatedAt.AddSeconds(1);
            log.UpdatedAt = CreatedAt.AddSeconds(1);
        });

        var data = await QueryDataAsync(
            """
            query GetUsageLogs($first: Int, $orderBy: UsageLogOrder, $where: UsageLogWhereInput) {
              usageLogs(first: $first, orderBy: $orderBy, where: $where) {
                edges {
                  node {
                    id
                    createdAt
                    updatedAt
                    requestID
                    apiKeyID
                    projectID
                    channelID
                    modelID
                    promptTokens
                    completionTokens
                    totalTokens
                    promptAudioTokens
                    promptCachedTokens
                    promptWriteCachedTokens
                    promptWriteCachedTokens5m
                    promptWriteCachedTokens1h
                    completionAudioTokens
                    completionReasoningTokens
                    completionAcceptedPredictionTokens
                    completionRejectedPredictionTokens
                    source
                    format
                    totalCost
                    costPriceReferenceID
                    costItems {
                      itemCode
                      quantity
                      subtotal
                      tierBreakdown {
                        upTo
                        units
                        subtotal
                      }
                    }
                    channel {
                      id
                      name
                      type
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
                first = 2,
                orderBy = new { field = "CREATED_AT", direction = "DESC" },
            });

        // The root resolver rewrites CREATED_AT to ent.DefaultUsageLogOrder's
        // id column and keeps the direction, so the order is 33, 32, 31 —
        // created_at would give 32, 33, 31.
        var connection = data.GetProperty("usageLogs");
        Assert.Equal(3, connection.GetProperty("totalCount").GetInt32());
        Assert.True(connection.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());
        var edges = connection.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(2, edges.Length);
        Assert.Equal(33, Cursor(edges[0]));
        Assert.Equal(32, Cursor(edges[1]));

        var first = edges[0].GetProperty("node");
        Assert.Equal("gid://axonhub/UsageLog/33", first.GetProperty("id").GetString());
        Assert.Equal("gid://axonhub/Request/11", first.GetProperty("requestID").GetString());
        Assert.Equal(2, first.GetProperty("apiKeyID").GetInt32());
        Assert.Equal("gid://axonhub/Project/1", first.GetProperty("projectID").GetString());
        Assert.Equal("gid://axonhub/Channel/9", first.GetProperty("channelID").GetString());
        Assert.Equal("gpt-4o", first.GetProperty("modelID").GetString());
        Assert.Equal(10, first.GetProperty("promptTokens").GetInt32());
        Assert.Equal(20, first.GetProperty("completionTokens").GetInt32());
        Assert.Equal(30, first.GetProperty("totalTokens").GetInt32());
        Assert.Equal(1, first.GetProperty("promptAudioTokens").GetInt32());
        Assert.Equal(4, first.GetProperty("promptCachedTokens").GetInt32());
        Assert.Equal(2, first.GetProperty("promptWriteCachedTokens").GetInt32());
        Assert.Equal(3, first.GetProperty("promptWriteCachedTokens5m").GetInt32());
        Assert.Equal(5, first.GetProperty("promptWriteCachedTokens1h").GetInt32());
        Assert.Equal(6, first.GetProperty("completionAudioTokens").GetInt32());
        Assert.Equal(7, first.GetProperty("completionReasoningTokens").GetInt32());
        Assert.Equal(8, first.GetProperty("completionAcceptedPredictionTokens").GetInt32());
        Assert.Equal(9, first.GetProperty("completionRejectedPredictionTokens").GetInt32());
        Assert.Equal("test", first.GetProperty("source").GetString());
        Assert.Equal("openai/chat_completions", first.GetProperty("format").GetString());
        Assert.Equal(2.5, first.GetProperty("totalCost").GetDouble());
        Assert.Equal("price-1", first.GetProperty("costPriceReferenceID").GetString());

        var items = first.GetProperty("costItems").EnumerateArray().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("prompt_tokens", items[0].GetProperty("itemCode").GetString());
        Assert.Equal(10, items[0].GetProperty("quantity").GetInt32());
        Assert.Equal(0.5, items[0].GetProperty("subtotal").GetDouble());
        var tier = Assert.Single(items[0].GetProperty("tierBreakdown").EnumerateArray());
        Assert.Equal(1000, tier.GetProperty("upTo").GetInt32());
        Assert.Equal("completion_tokens", items[1].GetProperty("itemCode").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("tierBreakdown").ValueKind);

        Assert.Equal(
            "gid://axonhub/Channel/9",
            first.GetProperty("channel").GetProperty("id").GetString());
        Assert.Equal("channel-9", first.GetProperty("channel").GetProperty("name").GetString());
        Assert.Equal("openai", first.GetProperty("channel").GetProperty("type").GetString());

        Assert.Equal(UsageLogSortField.Id, _usageLogs.Queries[0].SortField);
        Assert.Equal(SortDirection.Desc, _usageLogs.Queries[0].Direction);

        // The default order stays id ascending (ent.DefaultUsageLogOrder).
        var defaultData = await QueryDataAsync(
            """
            query { usageLogs(first: 10) { edges { cursor } totalCount } }
            """);
        Assert.Equal(
            [31L, 32L, 33L],
            defaultData.GetProperty("usageLogs").GetProperty("edges").EnumerateArray()
                .Select(Cursor).ToArray());
    }

    [Fact]
    public async Task UsageLogsWherePredicatesReachTheStore()
    {
        SeedLookups();
        SeedUsageLog(31);

        await QueryDataAsync(
            """
            query UsageLogs($where: UsageLogWhereInput, $orderBy: UsageLogOrder) {
              usageLogs(first: 10, where: $where, orderBy: $orderBy) { totalCount }
            }
            """,
            new
            {
                where = new
                {
                    sourceIn = new[] { "test" },
                    modelID = "gpt-4o",
                    channelIDIn = new[] { "gid://axonhub/Channel/9" },
                    requestIDIn = new[] { "gid://axonhub/Request/11" },
                    apiKeyID = 2,
                    projectID = "gid://axonhub/Project/1",
                },
                orderBy = new { field = "UPDATED_AT", direction = "ASC" },
            });

        var query = Assert.Single(_usageLogs.Queries);
        Assert.Equal(UsageLogSortField.UpdatedAt, query.SortField);
        Assert.Equal(SortDirection.Asc, query.Direction);
        var where = Assert.IsType<UsageLogListFilter>(query.Where);
        Assert.Equal(["test"], where.SourceIn);
        Assert.Equal("gpt-4o", where.ModelId);
        Assert.Equal([9L], where.ChannelIdIn);
        Assert.Equal([11L], where.RequestIdIn);
        Assert.Equal(2L, where.ApiKeyId);
        Assert.Equal(1L, where.ProjectId);
    }

    [Fact]
    public async Task UsageLogNodeReturnsTheDetailShapeAndReferenceErrors()
    {
        SeedLookups();
        SeedUsageLog(31);

        var data = await QueryDataAsync(
            """
            query GetUsageLog($id: ID!) {
              node(id: $id) {
                ... on UsageLog {
                  id
                  requestID
                  modelID
                  totalTokens
                  source
                  totalCost
                  request { id }
                  project { id }
                  channel { id }
                }
              }
            }
            """,
            new { id = "gid://axonhub/UsageLog/31" });

        var node = data.GetProperty("node");
        Assert.Equal("gid://axonhub/UsageLog/31", node.GetProperty("id").GetString());
        Assert.Equal("gid://axonhub/Request/11", node.GetProperty("requestID").GetString());
        Assert.Equal(30, node.GetProperty("totalTokens").GetInt32());
        Assert.Equal(
            "gid://axonhub/Request/11",
            node.GetProperty("request").GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/Project/1",
            node.GetProperty("project").GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/Channel/9",
            node.GetProperty("channel").GetProperty("id").GetString());

        var missing = await QueryErrorsAsync(
            """
            query Node($id: ID!) { node(id: $id) { id } }
            """,
            new { id = "gid://axonhub/UsageLog/999" });
        Assert.Equal("ent: usage_log not found", missing[0].GetProperty("message").GetString());
    }

    private void SeedLookups()
    {
        _gate.Projects.Add(project =>
        {
            project.Id = 1;
            project.Name = "project";
            project.Status = "active";
        });
        _gate.ApiKeys.Add(key =>
        {
            key.Id = 2;
            key.Name = "key-2";
            key.ProjectId = 1;
            key.Status = "enabled";
        });
        _requests.Seed(request =>
        {
            request.Id = 11;
            request.ProjectId = 1;
            request.Status = "completed";
            request.ModelId = "gpt-4o";
            request.Format = "openai/chat_completions";
            request.CreatedAt = CreatedAt;
            request.UpdatedAt = UpdatedAt;
        });
        _channels.Add(new ChannelRecord
        {
            Id = 9,
            Name = "channel-9",
            Type = "openai",
            Status = "enabled",
        });
    }

    private void SeedUsageLog(long id, Action<UsageLogRecord>? configure = null)
        => _usageLogs.Seed(log =>
        {
            log.Id = id;
            log.RequestId = 11;
            log.ApiKeyId = 2;
            log.ProjectId = 1;
            log.ChannelId = 9;
            log.ModelId = "gpt-4o";
            log.PromptTokens = 10;
            log.CompletionTokens = 20;
            log.TotalTokens = 30;
            log.PromptAudioTokens = 1;
            log.PromptCachedTokens = 4;
            log.PromptWriteCachedTokens = 2;
            log.PromptWriteCachedTokens5m = 3;
            log.PromptWriteCachedTokens1h = 5;
            log.CompletionAudioTokens = 6;
            log.CompletionReasoningTokens = 7;
            log.CompletionAcceptedPredictionTokens = 8;
            log.CompletionRejectedPredictionTokens = 9;
            log.Source = "test";
            log.Format = "openai/chat_completions";
            log.TotalCost = 2.5;
            log.CostItems = CostItemsJson;
            log.CostPriceReferenceId = "price-1";
            log.CreatedAt = CreatedAt;
            log.UpdatedAt = UpdatedAt;
            configure?.Invoke(log);
        });

    private static long Cursor(JsonElement edge)
        => CursorCodec.Decode(edge.GetProperty("cursor").GetString()).Id;

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

/// <summary>In-memory usage_logs store for the root connection and node lookup.</summary>
internal sealed class UsageLogSliceStore : IUsageLogStore
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
            .Where(row => query.ProjectScope is null || row.ProjectId == query.ProjectScope.Value)
            .Where(row => Matches(row, query.Where))
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

    public Task<UsageLogRecord?> GetByIdAsync(
        long id,
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(_rows.FirstOrDefault(row =>
            row.Id == id && (projectScope is null || row.ProjectId == projectScope.Value)));
    }

    /// <summary>The subset of the ent predicates the reverse-edge tests send.</summary>
    private static bool Matches(UsageLogRecord row, UsageLogListFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.ProjectId is not null && row.ProjectId != filter.ProjectId.Value)
        {
            return false;
        }

        if (filter.Id is not null && row.Id != filter.Id.Value)
        {
            return false;
        }

        if (filter.Not is not null && Matches(row, filter.Not))
        {
            return false;
        }

        if (filter.And is not null && !filter.And.All(nested => Matches(row, nested)))
        {
            return false;
        }

        if (filter.Or is not null && !filter.Or.Any(nested => Matches(row, nested)))
        {
            return false;
        }

        return true;
    }

    public Task<long> CreateAsync(
        UsageLogRecord usageLog,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the usage log surface");

    public Task<IReadOnlyList<UsageLogRecord>> ListByRequestIdAsync(
        long requestId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the usage log surface");

    public Task<long> CountByApiKeyAsync(
        long apiKeyId,
        DateTime? start,
        DateTime? end,
        bool endInclusive,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the usage log surface");

    public Task<UsageAggregate> AggregateByApiKeyAsync(
        long apiKeyId,
        DateTime? start,
        DateTime? end,
        bool endInclusive,
        bool needTokens,
        bool needCost,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the usage log surface");

    public Task<double> SumCostByChannelAsync(
        long channelId,
        DateTime since,
        DateTime until,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the usage log surface");
}

/// <summary>
/// Unit checks for the UsageLog query policy
/// (internal/ent/schema/usage_log.go:121-136): a project member gets the
/// project predicate plus the request-visibility predicate, project owners and
/// system owners audit the whole project, and a member without the read scope
/// is denied.
/// </summary>
public sealed class UsageLogPrivacyTests
{
    [Fact]
    public async Task MemberGetsProjectAndVisibilityPredicate()
    {
        var (users, identity) = NewIdentity(5, isOwner: false, projectScopes: ["read_requests"]);
        var store = new RecordingUsageLogStore();
        var service = new UsageLogManagementService(store, new TracePrivacy(identity, users));

        await service.ListAsync(new UsageLogListQuery());

        var query = Assert.Single(store.Queries);
        Assert.Equal(1L, query.ProjectScope);
        Assert.Equal(5L, query.VisibilityUserId);
    }

    [Fact]
    public async Task ProjectOwnerGetsTheProjectWithoutTheVisibilityPredicate()
    {
        var (users, identity) = NewIdentity(
            5,
            isOwner: false,
            projectScopes: ["read_requests"],
            projectOwner: true);
        var store = new RecordingUsageLogStore();
        var service = new UsageLogManagementService(store, new TracePrivacy(identity, users));

        await service.ListAsync(new UsageLogListQuery());

        var query = Assert.Single(store.Queries);
        Assert.Equal(1L, query.ProjectScope);
        Assert.Null(query.VisibilityUserId);
    }

    [Fact]
    public async Task SystemOwnerReadsEverything()
    {
        var (users, identity) = NewIdentity(9, isOwner: true, projectScopes: null);
        identity.ProjectId = null;
        var store = new RecordingUsageLogStore();
        var service = new UsageLogManagementService(store, new TracePrivacy(identity, users));

        await service.ListAsync(new UsageLogListQuery());

        var query = Assert.Single(store.Queries);
        Assert.Null(query.ProjectScope);
        Assert.Null(query.VisibilityUserId);
    }

    [Fact]
    public async Task SystemOwnerWithProjectHeaderKeepsTheProjectPredicate()
    {
        // HasSystemScope treats owners as fully scoped, so the project rule
        // installs WhereProjectID before the owner rule is reached.
        var (users, identity) = NewIdentity(9, isOwner: true, projectScopes: null);
        var store = new RecordingUsageLogStore();
        var service = new UsageLogManagementService(store, new TracePrivacy(identity, users));

        await service.ListAsync(new UsageLogListQuery());

        var query = Assert.Single(store.Queries);
        Assert.Equal(1L, query.ProjectScope);
        Assert.Null(query.VisibilityUserId);
    }

    [Fact]
    public async Task MemberWithoutTheReadScopeIsDenied()
    {
        var (users, identity) = NewIdentity(6, isOwner: false, projectScopes: null);
        var store = new RecordingUsageLogStore();
        var service = new UsageLogManagementService(store, new TracePrivacy(identity, users));

        var error = await Assert.ThrowsAsync<PrivacyDenyException>(
            () => service.ListAsync(new UsageLogListQuery()));

        Assert.Equal("default deny: ent/privacy: deny rule", error.Message);
        Assert.Empty(store.Queries);
    }

    private static (FakeUserAdminStore Users, FakeManagementIdentity Identity) NewIdentity(
        long userId,
        bool isOwner,
        IReadOnlyList<string>? projectScopes,
        bool projectOwner = false)
    {
        var users = new FakeUserAdminStore();
        users.CreateAsync(
            new UserRecord
            {
                Id = userId,
                Email = "user-" + userId + "@example.com",
                Status = "activated",
                IsOwner = isOwner,
            },
            roleIds: null).GetAwaiter().GetResult();

        if (projectScopes is not null)
        {
            users.AssignProject(userId, 1, isOwner: projectOwner, scopes: projectScopes);
        }

        return (users, new FakeManagementIdentity(new ManagementUser(userId, isOwner))
        {
            ProjectId = 1,
        });
    }

    private sealed class RecordingUsageLogStore : IUsageLogStore
    {
        public List<UsageLogListQuery> Queries { get; } = [];

        public Task<ListPage<UsageLogRecord>> ListAsync(
            UsageLogListQuery query,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            Queries.Add(query);
            return Task.FromResult(
                new ListPage<UsageLogRecord>(Array.Empty<UsageLogRecord>(), false, false, 0));
        }

        public Task<UsageLogRecord?> GetByIdAsync(
            long id,
            long? projectScope,
            long? visibilityUserId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("not used by the list path");

        public Task<long> CreateAsync(
            UsageLogRecord usageLog,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<UsageLogRecord>> ListByRequestIdAsync(
            long requestId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<long> CountByApiKeyAsync(
            long apiKeyId,
            DateTime? start,
            DateTime? end,
            bool endInclusive,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UsageAggregate> AggregateByApiKeyAsync(
            long apiKeyId,
            DateTime? start,
            DateTime? end,
            bool endInclusive,
            bool needTokens,
            bool needCost,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<double> SumCostByChannelAsync(
            long channelId,
            DateTime since,
            DateTime until,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
