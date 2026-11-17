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
/// Locks the last ten entgql edges (ent.graphql 55-75 / 407-543 / 2111-2134 /
/// 5419-5520; gql_edge.go 27-129 / 195-235 / 577-625): APIKey.requests, the
/// three Channel connections plus channelProbes, the two DataStorage
/// connections, and Role.users / project / userRoles. Each edge ANDs its
/// foreign-key predicate into the caller's where filter, Channel.executions and
/// DataStorage.executions scope without a parent request, and the list edges
/// keep the reference's unpaginated All() semantics.
/// </summary>
public sealed class AdminResidualEdgesEndpointTests : IAsyncLifetime
{
    private static readonly DateTime T1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T3 = new(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);

    private const string Settings =
        """{"dsn":null,"directory":null,"s3":null,"gcs":null,"webdav":null}""";

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeProjectStore _projects = new();
    private readonly FakeApiKeyStore _apiKeys = new();
    private readonly FakeChannelAdminStore _channels = new();
    private readonly FakeChannelProbeStore _probes = new();
    private readonly FakeRequestSegmentStore _requests = new();
    private readonly EdgeExecutionStore _executions = new();
    private readonly UsageLogSliceStore _usageLogs = new();
    private readonly FakeDataStorageStore _dataStorages = new();
    private readonly FakeDataStorageBackends _backends = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IUserAdminStore>(_users);
        builder.Services.AddSingleton<IRoleStore>(_roles);
        builder.Services.AddSingleton<IProjectStore>(_projects);
        builder.Services.AddSingleton<IApiKeyStore>(_apiKeys);
        builder.Services.AddSingleton<IChannelAdminStore>(_channels);
        builder.Services.AddSingleton<IChannelProbeStore>(_probes);
        builder.Services.AddSingleton<IRequestSegmentStore>(_requests);
        builder.Services.AddSingleton<IRequestExecutionStore>(_executions);
        builder.Services.AddSingleton<IUsageLogStore>(_usageLogs);
        builder.Services.AddSingleton<IDataStorageStore>(_dataStorages);
        builder.Services.AddSingleton<IDataStorageBackendFactory>(_backends);
        builder.Services.AddSingleton<IChannelModelSyncReloadHook>(
            new RecordingModelSyncReloadHook());
        builder.Services.AddScoped<PermissionValidator>();
        builder.Services.AddScoped<ApiKeyPrivacy>();
        builder.Services.AddScoped<TracePrivacy>();
        builder.Services.AddScoped<DataStoragePrivacy>();
        builder.Services.AddScoped<ChannelQueryService>();
        builder.Services.AddScoped<ApiKeyManagementService>();
        builder.Services.AddScoped<DataStorageManagementService>();
        builder.Services.AddScoped<RequestManagementService>();
        builder.Services.AddScoped<UsageLogManagementService>();
        builder.Services.AddScoped<UserManagementService>();
        _users.RolesSource = _roles;

        // The gate's JWT user (id 9, owner) is the caller every fixture client
        // sends; the privacy policies see it as an owner.
        await _users.CreateAsync(
            new UserRecord
            {
                Id = AdminGraphqlGateServices.UserId,
                Email = "gate-user@example.com",
                Status = "activated",
                IsOwner = true,
                CreatedAt = T1,
                UpdatedAt = T1,
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
    public async Task ApiKeyRequestsEdgeScopesToTheKeyAndKeepsCreatedAtOrdering()
    {
        SeedApiKeySurface();

        var data = await QueryDataAsync(
            """
            query ApiKeyRequests($id: ID!) {
              node(id: $id) {
                ... on APIKey {
                  requests(first: 10, orderBy: { field: CREATED_AT, direction: DESC }) {
                    edges { node { id status } }
                    totalCount
                  }
                  completed: requests(first: 10, where: { status: completed }) {
                    edges { node { id } }
                    totalCount
                  }
                }
              }
            }
            """,
            new { id = new GidValue("APIKey", 1).ToString() });

        var apiKey = data.GetProperty("node");

        // Key 1 owns requests 1 and 2; request 3 belongs to key 2, so the FK
        // predicate drops it. DESC by created_at puts request 2 first.
        var requests = apiKey.GetProperty("requests");
        Assert.Equal(2, requests.GetProperty("totalCount").GetInt32());
        var edges = requests.GetProperty("edges");
        Assert.Equal(2, edges.GetArrayLength());
        Assert.Equal(
            "gid://axonhub/Request/2",
            edges[0].GetProperty("node").GetProperty("id").GetString());

        // The where filter is ANDed with the FK predicate.
        var completed = apiKey.GetProperty("completed");
        Assert.Equal(1, completed.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "gid://axonhub/Request/1",
            completed.GetProperty("edges")[0].GetProperty("node").GetProperty("id").GetString());
    }

    [Fact]
    public async Task ChannelReverseEdgesScopeToTheChannel()
    {
        var first = SeedChannel("Alpha", T1);
        var second = SeedChannel("Beta", T2);

        // Requests 1 and 3 carry channel 1; request 2 carries channel 2.
        _requests.Seed(request =>
        {
            request.Id = 1;
            request.ProjectId = 1;
            request.ChannelId = first.Id;
            request.CreatedAt = T1;
            request.UpdatedAt = T1;
        });
        _requests.Seed(request =>
        {
            request.Id = 2;
            request.ProjectId = 1;
            request.ChannelId = second.Id;
            request.CreatedAt = T2;
            request.UpdatedAt = T2;
        });
        _requests.Seed(request =>
        {
            request.Id = 3;
            request.ProjectId = 1;
            request.ChannelId = first.Id;
            request.CreatedAt = T3;
            request.UpdatedAt = T3;
        });

        // Executions 1 and 3 run on channel 1, execution 2 on channel 2.
        // Execution 3 is the newest by created_at even though its id is last.
        _executions.Seed(execution =>
        {
            execution.Id = 1;
            execution.RequestId = 1;
            execution.ChannelId = first.Id;
            execution.Status = "completed";
            execution.CreatedAt = T1;
            execution.UpdatedAt = T1;
        });
        _executions.Seed(execution =>
        {
            execution.Id = 2;
            execution.RequestId = 2;
            execution.ChannelId = second.Id;
            execution.Status = "failed";
            execution.CreatedAt = T2;
            execution.UpdatedAt = T2;
        });
        _executions.Seed(execution =>
        {
            execution.Id = 3;
            execution.RequestId = 3;
            execution.ChannelId = first.Id;
            execution.Status = "failed";
            execution.CreatedAt = T3;
            execution.UpdatedAt = T3;
        });

        _usageLogs.Seed(log =>
        {
            log.Id = 1;
            log.ProjectId = 1;
            log.ChannelId = first.Id;
            log.CreatedAt = T1;
            log.UpdatedAt = T1;
        });
        _usageLogs.Seed(log =>
        {
            log.Id = 2;
            log.ProjectId = 1;
            log.ChannelId = second.Id;
            log.CreatedAt = T2;
            log.UpdatedAt = T2;
        });

        _probes.Seed(first.Id, 100);
        _probes.Seed(second.Id, 100);
        _probes.Seed(first.Id, 200);

        var data = await QueryDataAsync(
            """
            query ChannelEdges($id: ID!) {
              node(id: $id) {
                ... on Channel {
                  requests(first: 10) {
                    edges { node { id } }
                    totalCount
                  }
                  executions(first: 10, orderBy: { field: CREATED_AT, direction: DESC }) {
                    edges { node { id status } }
                    totalCount
                  }
                  completedExecutions: executions(first: 10, where: { status: completed }) {
                    totalCount
                  }
                  usageLogs(first: 10) {
                    edges { node { id } }
                    totalCount
                  }
                  channelProbes { id channelID }
                }
              }
            }
            """,
            new { id = new GidValue("Channel", first.Id).ToString() });

        var channel = data.GetProperty("node");

        var requests = channel.GetProperty("requests");
        Assert.Equal(2, requests.GetProperty("totalCount").GetInt32());

        // RequestExecution has no privacy policy; the edge still drops the
        // second channel's row and ANDs the where filter. Execution 3 (newest)
        // sorts first by created_at, not by id.
        var executions = channel.GetProperty("executions");
        Assert.Equal(2, executions.GetProperty("totalCount").GetInt32());
        var executionEdges = executions.GetProperty("edges");
        Assert.Equal(2, executionEdges.GetArrayLength());
        Assert.Equal(
            "gid://axonhub/RequestExecution/3",
            executionEdges[0].GetProperty("node").GetProperty("id").GetString());
        Assert.Equal(
            1,
            channel.GetProperty("completedExecutions").GetProperty("totalCount").GetInt32());

        var usageLogs = channel.GetProperty("usageLogs");
        Assert.Equal(1, usageLogs.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "gid://axonhub/UsageLog/1",
            usageLogs.GetProperty("edges")[0].GetProperty("node").GetProperty("id").GetString());

        // channelProbes is the SDL list form of the entgql connection: no
        // pagination, no privacy policy, id order (C164). The second
        // channel's bucket is filtered by channel_id.
        var probes = channel.GetProperty("channelProbes");
        Assert.Equal(2, probes.GetArrayLength());
        Assert.Equal(
            "gid://axonhub/ChannelProbe/1",
            probes[0].GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/ChannelProbe/3",
            probes[1].GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/Channel/" + first.Id,
            probes[0].GetProperty("channelID").GetString());
    }

    [Fact]
    public async Task DataStorageReverseEdgesScopeToTheStorage()
    {
        var primary = _dataStorages.Seed(record =>
        {
            record.Name = "primary";
            record.Description = "";
            record.Primary = true;
            record.Type = "database";
            record.Status = "active";
            record.Settings = Settings;
            record.CreatedAt = T1;
            record.UpdatedAt = T1;
        });
        var archive = _dataStorages.Seed(record =>
        {
            record.Name = "archive";
            record.Description = "";
            record.Type = "fs";
            record.Status = "active";
            record.Settings = Settings;
            record.CreatedAt = T2;
            record.UpdatedAt = T2;
        });

        _requests.Seed(request =>
        {
            request.Id = 1;
            request.ProjectId = 1;
            request.DataStorageId = primary.Id;
            request.CreatedAt = T1;
            request.UpdatedAt = T1;
        });
        _requests.Seed(request =>
        {
            request.Id = 2;
            request.ProjectId = 1;
            request.DataStorageId = archive.Id;
            request.CreatedAt = T2;
            request.UpdatedAt = T2;
        });
        _executions.Seed(execution =>
        {
            execution.Id = 1;
            execution.RequestId = 1;
            execution.DataStorageId = primary.Id;
            execution.CreatedAt = T1;
            execution.UpdatedAt = T1;
        });
        _executions.Seed(execution =>
        {
            execution.Id = 2;
            execution.RequestId = 2;
            execution.DataStorageId = archive.Id;
            execution.CreatedAt = T2;
            execution.UpdatedAt = T2;
        });

        var data = await QueryDataAsync(
            """
            query DataStorageEdges($id: ID!) {
              node(id: $id) {
                ... on DataStorage {
                  requests(first: 10) {
                    edges { node { id } }
                    totalCount
                  }
                  executions(first: 10) {
                    edges { node { id } }
                    totalCount
                  }
                }
              }
            }
            """,
            new { id = new GidValue("DataStorage", primary.Id).ToString() });

        var storage = data.GetProperty("node");
        Assert.Equal(1, storage.GetProperty("requests").GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "gid://axonhub/Request/1",
            storage.GetProperty("requests").GetProperty("edges")[0]
                .GetProperty("node").GetProperty("id").GetString());
        Assert.Equal(1, storage.GetProperty("executions").GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "gid://axonhub/RequestExecution/1",
            storage.GetProperty("executions").GetProperty("edges")[0]
                .GetProperty("node").GetProperty("id").GetString());
    }

    [Fact]
    public async Task RoleEdgesExposeUsersProjectAndTheUserRolesList()
    {
        var alpha = SeedProject(1, "Alpha");
        SeedProject(2, "Beta");

        var role = await _roles.CreateAsync(new RoleRecord
        {
            Name = "alpha-role",
            Level = "project",
            ProjectId = alpha.Id,
            Scopes = "[]",
            CreatedAt = T1,
            UpdatedAt = T1,
        });
        var betaRole = await _roles.CreateAsync(new RoleRecord
        {
            Name = "beta-role",
            Level = "project",
            ProjectId = 2,
            Scopes = "[]",
            CreatedAt = T2,
            UpdatedAt = T2,
        });
        var systemRole = await _roles.CreateAsync(new RoleRecord
        {
            Name = "system-role",
            Level = "system",
            ProjectId = null,
            Scopes = "[]",
            CreatedAt = T3,
            UpdatedAt = T3,
        });
        var ghostRole = await _roles.CreateAsync(new RoleRecord
        {
            Name = "ghost-role",
            Level = "project",
            ProjectId = 999,
            Scopes = "[]",
            CreatedAt = T3,
            UpdatedAt = T3,
        });

        await _users.CreateAsync(
            new UserRecord
            {
                Id = 10,
                Email = "alice@example.com",
                Status = "activated",
                CreatedAt = T1,
                UpdatedAt = T1,
            },
            roleIds: null);
        await _users.CreateAsync(
            new UserRecord
            {
                Id = 11,
                Email = "bob@example.com",
                Status = "activated",
                CreatedAt = T2,
                UpdatedAt = T2,
            },
            roleIds: null);

        _roles.Assign(AdminGraphqlGateServices.UserId, role);
        _roles.Assign(10, role);
        _roles.Assign(11, betaRole);

        var data = await QueryDataAsync(
            """
            query RoleEdges($id: ID!, $system: ID!, $ghost: ID!) {
              node(id: $id) {
                ... on Role {
                  users(first: 10) {
                    edges { node { id email } }
                    totalCount
                  }
                  aliceOnly: users(first: 10, where: { emailContainsFold: "alice" }) {
                    edges { node { email } }
                    totalCount
                  }
                  project { id name }
                  userRoles { id userID roleID }
                }
              }
              systemRole: node(id: $system) {
                ... on Role { project { id } }
              }
              ghostProject: node(id: $ghost) {
                ... on Role { project { id } }
              }
            }
            """,
            new
            {
                id = new GidValue("Role", role).ToString(),
                system = new GidValue("Role", systemRole).ToString(),
                ghost = new GidValue("Role", ghostRole).ToString(),
            });

        var roleNode = data.GetProperty("node");

        // The role links the gate user and alice; bob's betaRole does not
        // qualify, and the where filter ANDs with the join predicate.
        var users = roleNode.GetProperty("users");
        Assert.Equal(2, users.GetProperty("totalCount").GetInt32());
        var aliceOnly = roleNode.GetProperty("aliceOnly");
        Assert.Equal(1, aliceOnly.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "alice@example.com",
            aliceOnly.GetProperty("edges")[0].GetProperty("node").GetProperty("email").GetString());

        // Role.project runs the Project query policy and MaskNotFound.
        var project = roleNode.GetProperty("project");
        Assert.Equal("Alpha", project.GetProperty("name").GetString());

        // A system role has a nil project edge; the generated join reports
        // NotFound and MaskNotFound yields null.
        Assert.Equal(JsonValueKind.Null, data.GetProperty("systemRole")
            .GetProperty("project").ValueKind);

        // The list form of userRoles keeps the join rows' natural order.
        var userRoles = roleNode.GetProperty("userRoles");
        Assert.Equal(2, userRoles.GetArrayLength());
        Assert.Equal(
            "gid://axonhub/Role/" + role,
            userRoles[0].GetProperty("roleID").GetString());
        Assert.Equal(
            "gid://axonhub/User/" + AdminGraphqlGateServices.UserId,
            userRoles[0].GetProperty("userID").GetString());

        // A project row that does not exist is also MaskNotFound → null.
        Assert.Equal(JsonValueKind.Null, data.GetProperty("ghostProject")
            .GetProperty("project").ValueKind);
    }

    [Fact]
    public async Task OrderWithoutFieldIsRejectedOnTheNewEdgesLikeTheReferenceSchema()
    {
        SeedApiKeySurface();

        var (status, root, contentType) = await PostAsync(
            """
            query OrderWithoutField($id: ID!) {
              node(id: $id) {
                ... on APIKey {
                  requests(first: 10, orderBy: { direction: DESC }) {
                    edges { node { id } }
                  }
                }
              }
            }
            """,
            new { id = new GidValue("APIKey", 1).ToString() },
            null,
            "application/graphql-response+json");

        // Same request-validation contract as §209: ent.graphql declares
        // `field: XOrderField!` with no default.
        Assert.True(status == HttpStatusCode.UnprocessableEntity, root.ToString());
        Assert.Equal("application/graphql-response+json", contentType);
        var error = Assert.Single(root.GetProperty("errors").EnumerateArray());
        Assert.Equal("field", error.GetProperty("extensions").GetProperty("field").GetString());
    }

    [Fact]
    public async Task ResponseMediaTypeFollowsTheAcceptHeader()
    {
        var (successStatus, success, successContentType) = await PostAsync(
            "query { __typename }",
            null,
            null,
            "application/json");

        Assert.Equal(HttpStatusCode.OK, successStatus);
        Assert.Equal("application/json", successContentType);
        Assert.False(success.TryGetProperty("errors", out _), success.ToString());

        var (errorStatus, error, errorContentType) = await PostAsync(
            "query { unknownField }",
            null,
            null,
            "application/graphql-response+json");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, errorStatus);
        Assert.Equal("application/graphql-response+json", errorContentType);
        Assert.True(error.TryGetProperty("errors", out _), error.ToString());
    }

    [Fact]
    public async Task OrderingFieldsAreRequiredWithoutSchemaDefaults()
    {
        var (status, root, _) = await PostAsync(
            """
            query {
              request: __type(name: "RequestOrder") {
                inputFields { name defaultValue type { kind name ofType { kind name } } }
              }
              role: __type(name: "RoleOrder") {
                inputFields { name defaultValue type { kind name ofType { kind name } } }
              }
            }
            """,
            null,
            null);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(root.TryGetProperty("errors", out _), root.ToString());
        AssertRequiredOrderField(root.GetProperty("data").GetProperty("request"));
        AssertRequiredOrderField(root.GetProperty("data").GetProperty("role"));

        static void AssertRequiredOrderField(JsonElement inputType)
        {
            var field = inputType.GetProperty("inputFields")
                .EnumerateArray()
                .Single(static field => field.GetProperty("name").GetString() == "field");

            Assert.Equal(JsonValueKind.Null, field.GetProperty("defaultValue").ValueKind);
            Assert.Equal("NON_NULL", field.GetProperty("type").GetProperty("kind").GetString());
        }
    }

    private void SeedApiKeySurface()
    {
        _apiKeys.Rows.Add(new ApiKeyRecord
        {
            Id = 1,
            Key = "sk-one",
            Name = "one",
            Type = "service_account",
            Status = "enabled",
            ProjectId = 1,
            CreatedAt = T1,
            UpdatedAt = T1,
        });
        _apiKeys.Rows.Add(new ApiKeyRecord
        {
            Id = 2,
            Key = "sk-two",
            Name = "two",
            Type = "service_account",
            Status = "enabled",
            ProjectId = 1,
            CreatedAt = T2,
            UpdatedAt = T2,
        });

        _requests.Seed(request =>
        {
            request.Id = 1;
            request.ProjectId = 1;
            request.ApiKeyId = 1;
            request.ChannelId = 1;
            request.DataStorageId = 1;
            request.Status = "completed";
            request.CreatedAt = T1;
            request.UpdatedAt = T1;
        });
        _requests.Seed(request =>
        {
            request.Id = 2;
            request.ProjectId = 1;
            request.ApiKeyId = 1;
            request.ChannelId = 2;
            request.Status = "failed";
            request.CreatedAt = T2;
            request.UpdatedAt = T2;
        });
        _requests.Seed(request =>
        {
            request.Id = 3;
            request.ProjectId = 1;
            request.ApiKeyId = 2;
            request.ChannelId = 1;
            request.Status = "completed";
            request.CreatedAt = T3;
            request.UpdatedAt = T3;
        });
    }

    private ChannelRecord SeedChannel(string name, DateTime createdAt)
    {
        var record = new ChannelRecord
        {
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Type = "openai",
            Name = name,
            Status = "enabled",
            SupportedModels = "null",
            AutoSyncModelPattern = "",
            Credentials = "{}",
            OrderingWeight = 0,
        };
        _channels.Seed(record);
        return record;
    }

    private ProjectRecord SeedProject(long id, string name)
    {
        var record = new ProjectRecord
        {
            Id = id,
            Name = name,
            Description = string.Empty,
            Status = "active",
            CreatedAt = T1,
            UpdatedAt = T1,
        };
        _projects.Rows.Add(record);
        return record;
    }

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null,
        string? operationName = null)
    {
        var (status, root, contentType) = await PostAsync(query, variables, operationName);
        AdminGraphqlHttpResponseAssertions.AssertStatus(status, root, contentType);
        Assert.False(root.TryGetProperty("errors", out _), root.ToString());
        return root.GetProperty("data");
    }

    private async Task<(HttpStatusCode Status, JsonElement Root, string? ContentType)> PostAsync(
        string query,
        object? variables,
        string? operationName,
        string? accept = null)
    {
        var payload = JsonSerializer.Serialize(new { query, variables, operationName });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/graphql")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        if (accept is not null)
        {
            request.Headers.TryAddWithoutValidation("Accept", accept);
        }

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return (
            response.StatusCode,
            document.RootElement.Clone(),
            response.Content.Headers.ContentType?.MediaType);
    }
}

/// <summary>
/// In-memory channel_probes store for Channel.channelProbes: the reference's
/// All() has no time window and no privacy policy, so the fake only filters by
/// channel and pins id order (C164).
/// </summary>
internal sealed class FakeChannelProbeStore : IChannelProbeStore
{
    private readonly List<ChannelProbeRecord> _rows = [];
    private long _nextId = 1;

    public List<long> EdgeReads { get; } = [];

    public ChannelProbeRecord Seed(long channelId, long timestamp)
    {
        var record = new ChannelProbeRecord
        {
            Id = _nextId++,
            ChannelId = channelId,
            Timestamp = timestamp,
            TotalRequestCount = 1,
            SuccessRequestCount = 1,
        };
        _rows.Add(record);
        return record;
    }

    public Task<ChannelProbeRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(_rows.FirstOrDefault(row => row.Id == id));
    }

    public Task<IReadOnlyList<ChannelProbeRecord>> ListByChannelIdAsync(
        long channelId,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        EdgeReads.Add(channelId);
        return Task.FromResult<IReadOnlyList<ChannelProbeRecord>>(
            _rows.Where(row => row.ChannelId == channelId).OrderBy(row => row.Id).ToList());
    }

    public Task<IReadOnlyList<ChannelProbeRecord>> QueryAsync(
        IReadOnlyList<long> channelIds,
        long startUnix,
        long endUnix,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the edge surface");

    public Task<IReadOnlyList<ChannelProbeStatsRow>> ComputeStatsAsync(
        IReadOnlyList<long> channelIds,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the edge surface");

    public Task CreateRangeAsync(
        IReadOnlyList<ChannelProbeRecord> probes,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the edge surface");
}

/// <summary>
/// In-memory request_executions store for the Channel / DataStorage edges:
/// applies the nullable RequestScope, the edge foreign-key predicates and the
/// subset of the where input the fixtures send.
/// </summary>
internal sealed class EdgeExecutionStore : IRequestExecutionStore
{
    private readonly List<RequestExecutionRecord> _rows = [];
    private long _nextId = 1;

    public RequestExecutionRecord Seed(Action<RequestExecutionRecord> configure)
    {
        var record = new RequestExecutionRecord();
        configure(record);
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

        var filtered = _rows
            .Where(row => query.RequestScope is null || row.RequestId == query.RequestScope.Value)
            .Where(row => Matches(row, query.Where))
            .ToList();
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

    private static bool Matches(RequestExecutionRecord row, RequestExecutionListFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        return (filter.Id is null || row.Id == filter.Id.Value)
            && (filter.RequestId is null || row.RequestId == filter.RequestId.Value)
            && (filter.ChannelId is null || row.ChannelId == filter.ChannelId.Value)
            && (filter.DataStorageId is null || row.DataStorageId == filter.DataStorageId.Value)
            && (filter.Status is null || row.Status == filter.Status)
            && (filter.Not is null || !Matches(row, filter.Not))
            && (filter.And is null || filter.And.All(nested => Matches(row, nested)))
            && (filter.Or is null || filter.Or.Any(nested => Matches(row, nested)));
    }

    public Task<long> CreateAsync(
        RequestExecutionRecord execution,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the edge surface");

    public Task<RequestExecutionRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the edge surface");

    public Task<IReadOnlyList<RequestExecutionRecord>> ListByRequestIdAsync(
        long requestId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the edge surface");

    public Task<IReadOnlyList<RequestExecutionRecord>> ListByRequestIdDescAsync(
        long requestId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the edge surface");

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
        => throw new NotSupportedException("not used by the edge surface");

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
        => throw new NotSupportedException("not used by the edge surface");

    public Task<bool> UpdateResponseChunksAsync(
        long id,
        string responseChunks,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the edge surface");

    public Task<bool> UpdateResponseHeadersAsync(
        long id,
        string responseHeaders,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("not used by the edge surface");
}
