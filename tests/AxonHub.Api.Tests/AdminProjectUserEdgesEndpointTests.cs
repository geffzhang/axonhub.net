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
/// Locks the reverse entgql connections of Project and User (ent.graphql
/// 2836-3132 / 7128-7285, gql_edge.go 245-862): the nine Project connections,
/// the five User connections, the projectUsers / userRoles lists, the
/// foreign-key predicate each edge ANDs into the caller's where filter, and the
/// edge ordering that keeps CREATED_AT (unlike the root resolvers).
/// </summary>
public sealed class AdminProjectUserEdgesEndpointTests : IAsyncLifetime
{
    private static readonly DateTime T1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T3 = new(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeProjectStore _projects = new();
    private readonly FakeApiKeyStore _apiKeys = new();
    private readonly FakeTraceAdminStore _traces = new();
    private readonly FakeThreadAdminStore _threads = new();
    private readonly FakeUsageMetadataStore _usage = new();
    private readonly FakeRequestSegmentStore _requests = new();
    private readonly UsageLogSliceStore _usageLogs = new();
    private readonly FakePromptStore _prompts = new();
    private readonly FakeChannelOverrideTemplateStore _overrideTemplates = new();
    private readonly FakeApiKeyProfileTemplateStore _profileTemplates = new();
    private readonly FakeOidcIdentityAdminStore _identities = new();
    private readonly FakeInvitationStore _invitations = new();
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
        builder.Services.AddSingleton<ITraceAdminStore>(_traces);
        builder.Services.AddSingleton<IThreadAdminStore>(_threads);
        builder.Services.AddSingleton<IUsageMetadataStore>(_usage);
        builder.Services.AddSingleton<IRequestSegmentStore>(_requests);
        builder.Services.AddSingleton<IUsageLogStore>(_usageLogs);
        builder.Services.AddSingleton<IPromptStore>(_prompts);
        builder.Services.AddSingleton<IChannelOverrideTemplateStore>(_overrideTemplates);
        builder.Services.AddSingleton<IApiKeyProfileTemplateStore>(_profileTemplates);
        builder.Services.AddSingleton<IOidcIdentityAdminStore>(_identities);
        builder.Services.AddSingleton<IInvitationStore>(_invitations);
        builder.Services.AddSingleton<IChannelModelSyncReloadHook>(
            new RecordingModelSyncReloadHook());
        builder.Services.AddSingleton<IDataStorageStore>(_dataStorages);
        builder.Services.AddSingleton<IDataStorageBackendFactory>(_backends);
        builder.Services.AddSingleton<ISystemSettingsAdminService>(
            new SystemSettingsAdminService(_gate.Systems));
        builder.Services.AddScoped<PermissionValidator>();
        builder.Services.AddScoped<ApiKeyPrivacy>();
        builder.Services.AddScoped<TracePrivacy>();
        builder.Services.AddScoped<SystemPrivacy>();
        builder.Services.AddScoped<PromptPrivacy>();
        builder.Services.AddScoped<ApiKeyProfileTemplatePrivacy>();
        builder.Services.AddScoped<TraceSegmentService>();
        builder.Services.AddScoped<RequestPayloadStorage>();
        builder.Services.AddScoped<UserManagementService>();
        builder.Services.AddScoped<RoleManagementService>();
        builder.Services.AddScoped<ProjectManagementService>();
        builder.Services.AddScoped<ApiKeyManagementService>();
        builder.Services.AddScoped<PromptManagementService>();
        builder.Services.AddScoped<ChannelOverrideTemplateService>();
        builder.Services.AddScoped<ApiKeyProfileTemplateService>();
        builder.Services.AddScoped<RequestManagementService>();
        builder.Services.AddScoped<UsageLogManagementService>();
        builder.Services.AddScoped<ThreadManagementService>();
        builder.Services.AddScoped<TraceManagementService>();
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
    public async Task ProjectEdgesReturnOnlyTheProjectsRowsAndMergeTheWhereFilter()
    {
        var alpha = SeedProjectsAndYourMembers();

        var data = await QueryDataAsync(
            """
            query ProjectEdges($id: ID!) {
              node(id: $id) {
                ... on Project {
                  id
                  users(first: 10, where: { emailContainsFold: "alice" }) {
                    edges { node { id email } }
                    totalCount
                  }
                  roles(first: 10, orderBy: { field: CREATED_AT, direction: ASC }) {
                    edges { node { id name projectID } }
                    totalCount
                  }
                  apiKeys(first: 10) {
                    edges { node { id name projectID } }
                    totalCount
                  }
                  requests(first: 10) {
                    edges { node { id } }
                    totalCount
                  }
                  usageLogs(first: 10) {
                    edges { node { id } }
                    totalCount
                  }
                  threads(first: 10) {
                    edges { node { id } }
                    totalCount
                  }
                  traces(first: 10) {
                    edges { node { id } }
                    totalCount
                  }
                  prompts(first: 10, where: { nameContainsFold: "alpha" }) {
                    edges { node { id name } }
                    totalCount
                  }
                  apiKeyProfileTemplates(first: 10) {
                    edges { node { id name } }
                    totalCount
                  }
                }
              }
            }
            """,
            new { id = new GidValue("Project", alpha.Id).ToString() });

        var project = data.GetProperty("node");

        // users: alice is linked to project 1; alice2 is linked to project 2,
        // so the where filter and the user_projects FK are ANDed.
        var users = project.GetProperty("users");
        Assert.Equal(1, users.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "alice@example.com",
            users.GetProperty("edges")[0].GetProperty("node").GetProperty("email").GetString());

        // roles: created_at order is the reverse of the id order, proving the
        // edge keeps CREATED_AT instead of the root resolver's id rewrite.
        var roles = project.GetProperty("roles").GetProperty("edges");
        Assert.Equal(2, roles.GetArrayLength());
        Assert.Equal("second", roles[0].GetProperty("node").GetProperty("name").GetString());
        Assert.Equal("first", roles[1].GetProperty("node").GetProperty("name").GetString());
        Assert.Equal(
            "gid://axonhub/Project/" + alpha.Id,
            roles[0].GetProperty("node").GetProperty("projectID").GetString());

        Assert.Equal(2, project.GetProperty("apiKeys").GetProperty("totalCount").GetInt32());
        Assert.Equal(1, project.GetProperty("requests").GetProperty("totalCount").GetInt32());
        Assert.Equal(2, project.GetProperty("usageLogs").GetProperty("totalCount").GetInt32());
        Assert.Equal(1, project.GetProperty("threads").GetProperty("totalCount").GetInt32());
        Assert.Equal(1, project.GetProperty("traces").GetProperty("totalCount").GetInt32());
        Assert.Equal(
            1,
            project.GetProperty("apiKeyProfileTemplates").GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "alpha-template",
            project.GetProperty("apiKeyProfileTemplates")
                .GetProperty("edges")[0].GetProperty("node").GetProperty("name").GetString());

        // prompts: "alpha system" is in project 1 and matches; "alpha beta" is
        // in project 2 and would only match without the edge FK.
        var prompts = project.GetProperty("prompts");
        Assert.Equal(1, prompts.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "alpha system",
            prompts.GetProperty("edges")[0].GetProperty("node").GetProperty("name").GetString());
    }

    [Fact]
    public async Task UserEdgesReturnOnlyTheUsersRowsAndExposeTheJoinLists()
    {
        SeedProjectsAndYourMembers();

        var data = await QueryDataAsync(
            """
            query UserEdges($id: ID!) {
              node(id: $id) {
                ... on User {
                  id
                  projects(first: 10, orderBy: { field: CREATED_AT, direction: ASC },
                           where: { nameContainsFold: "alp" }) {
                    edges { node { id name } }
                    totalCount
                  }
                  apiKeys(first: 10) {
                    edges { node { id name } }
                    totalCount
                  }
                  channelOverrideTemplates(first: 10) {
                    edges { node { id name } }
                    totalCount
                  }
                  oidcIdentities(first: 10) {
                    edges { node { id subject } }
                    totalCount
                  }
                  userRoles { id userID roleID }
                  projectUsers { id projectID }
                }
              }
            }
            """,
            new { id = new GidValue("User", AdminGraphqlGateServices.UserId).ToString() });

        var user = data.GetProperty("node");

        // projects: the user is linked to both projects; the where filter drops
        // Beta. The WHERE and the user_projects FK are ANDed.
        var projects = user.GetProperty("projects");
        Assert.Equal(1, projects.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "Alpha",
            projects.GetProperty("edges")[0].GetProperty("node").GetProperty("name").GetString());

        var apiKeys = user.GetProperty("apiKeys");
        Assert.Equal(1, apiKeys.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "gate-key",
            apiKeys.GetProperty("edges")[0].GetProperty("node").GetProperty("name").GetString());

        var templates = user.GetProperty("channelOverrideTemplates");
        Assert.Equal(1, templates.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "gate-template",
            templates.GetProperty("edges")[0].GetProperty("node").GetProperty("name").GetString());

        var identities = user.GetProperty("oidcIdentities");
        Assert.Equal(1, identities.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "gate-subject",
            identities.GetProperty("edges")[0].GetProperty("node").GetProperty("subject").GetString());

        // userRoles and projectUsers are SDL lists; the join rows keep their
        // natural insertion order and run no privacy filter.
        var userRoles = user.GetProperty("userRoles");
        Assert.Equal(2, userRoles.GetArrayLength());
        Assert.Equal(
            "gid://axonhub/User/" + AdminGraphqlGateServices.UserId,
            userRoles[0].GetProperty("userID").GetString());
        Assert.Equal(
            "gid://axonhub/Role/" + _roles.Rows[0].Id,
            userRoles[0].GetProperty("roleID").GetString());

        Assert.Equal(2, user.GetProperty("projectUsers").GetArrayLength());
    }

    [Fact]
    public async Task ProjectUsageLogsEdgeKeepsCreatedAtOrdering()
    {
        SeedProjectsAndYourMembers();

        var data = await QueryDataAsync(
            """
            query UsageLogEdges($id: ID!) {
              node(id: $id) {
                ... on Project {
                  usageLogs(first: 10, orderBy: { field: CREATED_AT, direction: DESC }) {
                    edges { node { id } }
                  }
                }
              }
            }
            """,
            new { id = new GidValue("Project", 1).ToString() });

        // Project 1 owns ids 1 and 2; DESC by created_at puts the newer row
        // (id 2) first even though id would put it last.
        var edges = data.GetProperty("node").GetProperty("usageLogs").GetProperty("edges");
        Assert.Equal(2, edges.GetArrayLength());
        Assert.Equal(
            "gid://axonhub/UsageLog/2",
            edges[0].GetProperty("node").GetProperty("id").GetString());
    }

    [Fact]
    public async Task OrderWithoutFieldIsRejectedLikeTheReferenceSchema()
    {
        SeedProjectsAndYourMembers();

        var (status, root) = await PostAsync(
            """
            query OrderWithoutField($id: ID!) {
              node(id: $id) {
                ... on Project {
                  roles(first: 10, orderBy: { direction: DESC }) {
                    edges { node { id } }
                  }
                }
              }
            }
            """,
            new { id = new GidValue("Project", 1).ToString() },
            null);

        // ent.graphql declares `field: XOrderField!` with no default
        // (ent.graphql:7319-7326), so gqlgen rejects an orderBy that only sets
        // the direction. The port's CLR initializer must not leak into the SDL
        // as a default value. The 400 status is HotChocolate's envelope
        // (C109's accepted engine substitution), not a semantic difference.
        Assert.Equal(HttpStatusCode.BadRequest, status);
        var error = Assert.Single(root.GetProperty("errors").EnumerateArray());
        Assert.Equal("field", error.GetProperty("extensions").GetProperty("field").GetString());
        Assert.Equal(
            """["node","roles"]""",
            error.GetProperty("path").GetRawText());
    }

    private ProjectRecord SeedProjectsAndYourMembers()
    {
        var alpha = new ProjectRecord
        {
            Id = 1,
            Name = "Alpha",
            Description = string.Empty,
            Status = "active",
            CreatedAt = T1,
            UpdatedAt = T1,
        };
        var beta = new ProjectRecord
        {
            Id = 2,
            Name = "Beta",
            Description = string.Empty,
            Status = "active",
            CreatedAt = T2,
            UpdatedAt = T2,
        };
        _projects.Rows.Add(alpha);
        _projects.Rows.Add(beta);

        // user_projects links seen by User.projects (ProjectStore.HasUsersWith).
        _projects.ProjectUsers.AddRange(
        [
            new UserProjectRecord
            {
                Id = 1,
                UserId = AdminGraphqlGateServices.UserId,
                ProjectId = alpha.Id,
                IsOwner = true,
            },
            new UserProjectRecord
            {
                Id = 2,
                UserId = AdminGraphqlGateServices.UserId,
                ProjectId = beta.Id,
                IsOwner = true,
            },
        ]);
        // The same links seen by Project.users (UserAdminStore).
        _users.AssignProject(AdminGraphqlGateServices.UserId, alpha.Id, isOwner: true);
        _users.AssignProject(AdminGraphqlGateServices.UserId, beta.Id, isOwner: true);

        var alice = new UserRecord
        {
            Id = 10,
            Email = "alice@example.com",
            Status = "activated",
            CreatedAt = T1,
            UpdatedAt = T1,
        };
        var alice2 = new UserRecord
        {
            Id = 11,
            Email = "alice2@example.com",
            Status = "activated",
            CreatedAt = T2,
            UpdatedAt = T2,
        };
        _users.CreateAsync(alice, roleIds: null).GetAwaiter().GetResult();
        _users.CreateAsync(alice2, roleIds: null).GetAwaiter().GetResult();
        _users.AssignProject(alice.Id, alpha.Id);
        _users.AssignProject(alice2.Id, beta.Id);

        // The role created later (third) sorts first by created_at.
        var first = new RoleRecord
        {
            Name = "first",
            Level = "project",
            ProjectId = alpha.Id,
            Scopes = "[]",
            CreatedAt = T3,
            UpdatedAt = T3,
        };
        _roles.CreateAsync(first).GetAwaiter().GetResult();
        _roles.CreateAsync(new RoleRecord
        {
            Name = "second",
            Level = "project",
            ProjectId = alpha.Id,
            Scopes = "[]",
            CreatedAt = T2,
            UpdatedAt = T2,
        }).GetAwaiter().GetResult();
        _roles.CreateAsync(new RoleRecord
        {
            Name = "beta",
            Level = "project",
            ProjectId = beta.Id,
            Scopes = "[]",
            CreatedAt = T1,
            UpdatedAt = T1,
        }).GetAwaiter().GetResult();

        // userRoles list rows for the gate user.
        _roles.Assign(AdminGraphqlGateServices.UserId, _roles.Rows[0].Id);
        _roles.Assign(AdminGraphqlGateServices.UserId, _roles.Rows[1].Id);

        _apiKeys.Rows.Add(new ApiKeyRecord
        {
            Id = 1,
            Key = "sk-alpha-1",
            Name = "alpha-key",
            Type = "service_account",
            Status = "enabled",
            ProjectId = alpha.Id,
            UserId = alice.Id,
            CreatedAt = T1,
            UpdatedAt = T1,
        });
        _apiKeys.Rows.Add(new ApiKeyRecord
        {
            Id = 2,
            Key = "sk-alpha-2",
            Name = "gate-key",
            Type = "user",
            Status = "enabled",
            ProjectId = alpha.Id,
            UserId = AdminGraphqlGateServices.UserId,
            CreatedAt = T2,
            UpdatedAt = T2,
        });
        _apiKeys.Rows.Add(new ApiKeyRecord
        {
            Id = 3,
            Key = "sk-beta",
            Name = "beta-key",
            Type = "service_account",
            Status = "enabled",
            ProjectId = beta.Id,
            UserId = alice.Id,
            CreatedAt = T3,
            UpdatedAt = T3,
        });

        _requests.Seed(request =>
        {
            request.Id = 1;
            request.ProjectId = alpha.Id;
            request.CreatedAt = T1;
            request.UpdatedAt = T1;
        });
        _requests.Seed(request =>
        {
            request.Id = 2;
            request.ProjectId = beta.Id;
            request.CreatedAt = T2;
            request.UpdatedAt = T2;
        });

        _usageLogs.Seed(log =>
        {
            log.Id = 1;
            log.ProjectId = alpha.Id;
            log.CreatedAt = T1;
            log.UpdatedAt = T1;
        });
        _usageLogs.Seed(log =>
        {
            log.Id = 2;
            log.ProjectId = alpha.Id;
            log.CreatedAt = T3;
            log.UpdatedAt = T3;
        });
        _usageLogs.Seed(log =>
        {
            log.Id = 3;
            log.ProjectId = beta.Id;
            log.CreatedAt = T2;
            log.UpdatedAt = T2;
        });

        _threads.Seed(thread =>
        {
            thread.Id = 1;
            thread.ProjectId = alpha.Id;
            thread.CreatedAt = T1;
            thread.UpdatedAt = T1;
        });
        _threads.Seed(thread =>
        {
            thread.Id = 2;
            thread.ProjectId = beta.Id;
            thread.CreatedAt = T2;
            thread.UpdatedAt = T2;
        });

        _traces.Seed(trace =>
        {
            trace.Id = 1;
            trace.ProjectId = alpha.Id;
            trace.CreatedAt = T1;
            trace.UpdatedAt = T1;
        });
        _traces.Seed(trace =>
        {
            trace.Id = 2;
            trace.ProjectId = beta.Id;
            trace.CreatedAt = T2;
            trace.UpdatedAt = T2;
        });

        _prompts.Seed(prompt =>
        {
            prompt.Id = 1;
            prompt.ProjectId = alpha.Id;
            prompt.Name = "alpha system";
            prompt.CreatedAt = T1;
            prompt.UpdatedAt = T1;
        });
        _prompts.Seed(prompt =>
        {
            prompt.Id = 2;
            prompt.ProjectId = beta.Id;
            prompt.Name = "alpha beta";
            prompt.CreatedAt = T2;
            prompt.UpdatedAt = T2;
        });

        _profileTemplates.Add(template =>
        {
            template.Id = 1;
            template.ProjectId = alpha.Id;
            template.Name = "alpha-template";
        });
        _profileTemplates.Add(template =>
        {
            template.Id = 2;
            template.ProjectId = beta.Id;
            template.Name = "beta-template";
        });

        _overrideTemplates.Templates.Add(new ChannelOverrideTemplateRecord
        {
            Id = 1,
            UserId = AdminGraphqlGateServices.UserId,
            Name = "gate-template",
            HeaderOverrideOperations = "[]",
            BodyOverrideOperations = "[]",
            CreatedAt = T1,
            UpdatedAt = T1,
        });
        _overrideTemplates.Templates.Add(new ChannelOverrideTemplateRecord
        {
            Id = 2,
            UserId = alice.Id,
            Name = "alice-template",
            HeaderOverrideOperations = "[]",
            BodyOverrideOperations = "[]",
            CreatedAt = T2,
            UpdatedAt = T2,
        });

        _identities.Add(identity =>
        {
            identity.UserId = AdminGraphqlGateServices.UserId;
            identity.Subject = "gate-subject";
        });
        _identities.Add(identity =>
        {
            identity.UserId = alice.Id;
            identity.Subject = "alice-subject";
        });

        return alpha;
    }

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null,
        string? operationName = null)
    {
        var (status, root) = await PostAsync(query, variables, operationName);
        Assert.True(status == HttpStatusCode.OK, root.ToString());
        Assert.False(root.TryGetProperty("errors", out _), root.ToString());
        return root.GetProperty("data");
    }

    private async Task<(HttpStatusCode Status, JsonElement Root)> PostAsync(
        string query,
        object? variables,
        string? operationName)
    {
        var payload = JsonSerializer.Serialize(new { query, variables, operationName });
        using var response = await _client.PostAsync(
            "/admin/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return (response.StatusCode, document.RootElement.Clone());
    }
}
