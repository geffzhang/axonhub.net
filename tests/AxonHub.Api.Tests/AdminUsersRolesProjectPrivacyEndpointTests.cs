using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the §197 wiring of the User / Role / Project / UserProject schema
/// policies over /admin/graphql: the root user/role/project gates, the
/// X-Project-ID filters, the node lookups and the UserProject self filter a
/// caller without read_users sees. The reference runs these policies inside
/// ent (internal/ent/schema/*.go); the port evaluates them in the management
/// services and the GraphQL edge resolvers.
/// </summary>
public sealed class AdminUsersRolesProjectPrivacyEndpointTests : IAsyncLifetime
{
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeInvitationStore _invitations = new();
    private readonly AdminGraphqlGateServices _gate = new(isOwner: false);

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
        builder.Services.AddSingleton<IInvitationStore>(_invitations);
        builder.Services.AddSingleton<IPasswordHasher, PasswordHasher>();
        builder.Services.AddScoped<PermissionValidator>();
        builder.Services.AddScoped<UserManagementService>();
        builder.Services.AddScoped<RoleManagementService>();
        builder.Services.AddScoped<ProjectManagementService>();
        _users.RolesSource = _roles;

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
    public async Task RootsDenyAScopeLessNonOwner()
    {
        var member = SeedUserAsync(77, scopes: null);
        var jwt = _gate.IssueJwt(member);

        var users = await QueryErrorsAsync(
            "{ users(first: 10) { edges { node { id } } } }",
            token: jwt);
        AssertDenied(users);

        var roles = await QueryErrorsAsync(
            "{ roles(first: 10) { edges { node { id } } } }",
            token: jwt);
        AssertDenied(roles);

        var projects = await QueryErrorsAsync(
            "{ projects(first: 10) { edges { node { id } } } }",
            token: jwt);
        AssertDenied(projects);
    }

    [Fact]
    public async Task RootsDenyAServiceAccountWithoutAUser()
    {
        // authz.GetPrincipal has no user for a service account, so every
        // user-scoped policy ends at no user / default deny.
        _gate.AddServiceAccountKey();

        var users = await QueryErrorsAsync(
            "{ users(first: 10) { edges { node { id } } } }",
            token: "sk-service-account");
        AssertDenied(users);

        var roles = await QueryErrorsAsync(
            "{ roles(first: 10) { edges { node { id } } } }",
            token: "sk-service-account");
        AssertDenied(roles);

        var projects = await QueryErrorsAsync(
            "{ projects(first: 10) { edges { node { id } } } }",
            token: "sk-service-account");
        AssertDenied(projects);
    }

    [Fact]
    public async Task RootsAllowAScopedNonOwner()
    {
        var member = SeedUserAsync(77, scopes: """["read_users","read_roles","read_projects"]""");
        SeedUserAsync(78, scopes: null);
        SeedRole("Admin", projectId: 5, level: "project");
        _gate.Projects.Add(project => project.Id = 5);
        var jwt = _gate.IssueJwt(member);

        var users = await QueryDataAsync(
            "{ users(first: 10) { totalCount } }",
            token: jwt);
        Assert.Equal(2, users.GetProperty("users").GetProperty("totalCount").GetInt32());

        var roles = await QueryDataAsync(
            "{ roles(first: 10) { totalCount } }",
            token: jwt);
        Assert.Equal(1, roles.GetProperty("roles").GetProperty("totalCount").GetInt32());

        var projects = await QueryDataAsync(
            "{ projects(first: 10) { totalCount } }",
            token: jwt);
        Assert.Equal(1, projects.GetProperty("projects").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ProjectHeaderNarrowsProjectsRolesAndUsersToThatProject()
    {
        // The project member holds the read scopes on project 5 only, so each
        // first rule installs its project predicate (WhereID / WhereProjectID /
        // WhereHasProjectUsersWith).
        var member = SeedUserAsync(77, scopes: null);
        SeedUserAsync(78, scopes: null);
        SeedUserAsync(79, scopes: null);
        _users.AssignProject(
            77,
            projectId: 5,
            isOwner: false,
            scopes: ["read_projects", "read_roles", "read_users"]);
        _users.AssignProject(78, projectId: 5);
        _users.AssignProject(79, projectId: 7);

        _gate.Projects.Add(project => project.Id = 5);
        _gate.Projects.Add(project => project.Id = 7);
        SeedRole("Project Admin", projectId: 5, level: "project");
        SeedRole("System Viewer", projectId: 0);
        SeedRole("Other Admin", projectId: 7, level: "project");

        var jwt = _gate.IssueJwt(member);

        var projects = await QueryDataAsync(
            "{ projects(first: 10) { edges { node { id } } } }",
            token: jwt,
            projectId: 5);
        var projectNodes = projects.GetProperty("projects").GetProperty("edges")
            .EnumerateArray().ToArray();
        Assert.Single(projectNodes);
        Assert.Equal(
            "gid://axonhub/Project/5",
            projectNodes[0].GetProperty("node").GetProperty("id").GetString());

        var roles = await QueryDataAsync(
            "{ roles(first: 10) { edges { node { id projectID } } } }",
            token: jwt,
            projectId: 5);
        var roleNodes = roles.GetProperty("roles").GetProperty("edges")
            .EnumerateArray().ToArray();
        Assert.Single(roleNodes);
        Assert.Equal(
            "gid://axonhub/Project/5",
            roleNodes[0].GetProperty("node").GetProperty("projectID").GetString());

        var users = await QueryDataAsync(
            "{ users(first: 10) { edges { node { id } } } }",
            token: jwt,
            projectId: 5);
        var userNodes = users.GetProperty("users").GetProperty("edges")
            .EnumerateArray().Select(static edge => edge.GetProperty("node")).ToArray();
        Assert.Equal(2, userNodes.Length);
        Assert.Equal(
            new string?[] { "gid://axonhub/User/77", "gid://axonhub/User/78" },
            userNodes.Select(static node => node.GetProperty("id").GetString()).ToArray());
    }

    [Fact]
    public async Task UserNodeHonoursTheProjectHeaderFilter()
    {
        var member = SeedUserAsync(77, scopes: null);
        SeedUserAsync(78, scopes: null);
        SeedUserAsync(79, scopes: null);
        _users.AssignProject(77, projectId: 5, isOwner: true);
        _users.AssignProject(78, projectId: 5);
        _users.AssignProject(79, projectId: 7);
        var jwt = _gate.IssueJwt(member);

        var visible = await QueryDataAsync(
            "{ node(id: \"gid://axonhub/User/78\") { id } }",
            token: jwt,
            projectId: 5);
        Assert.Equal(
            "gid://axonhub/User/78",
            visible.GetProperty("node").GetProperty("id").GetString());

        var hidden = await QueryErrorsAsync(
            "{ node(id: \"gid://axonhub/User/79\") { id } }",
            token: jwt,
            projectId: 5);
        Assert.Equal(
            "ent: user not found",
            hidden[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ProjectUsersEdgeFallsBackToTheCallersOwnRows()
    {
        // UserOwnedQueryRule applies to *ent.UserProjectFilter: a caller with
        // read_projects (the Project node is visible) but no read_users sees
        // only their own memberships.
        var member = SeedUserAsync(77, scopes: """["read_projects"]""");
        SeedUserAsync(78, scopes: null);
        _users.AssignProject(77, projectId: 5);
        _users.AssignProject(78, projectId: 5);
        _gate.Projects.Add(project => project.Id = 5);
        var jwt = _gate.IssueJwt(member);

        var data = await QueryDataAsync(
            """
            query NodeProject {
              node(id: "gid://axonhub/Project/5") {
                ... on Project { projectUsers { userID } }
              }
            }
            """,
            token: jwt);

        var rows = data.GetProperty("node").GetProperty("projectUsers")
            .EnumerateArray().ToArray();
        var row = Assert.Single(rows);
        Assert.Equal(
            "gid://axonhub/User/77",
            row.GetProperty("userID").GetString());
    }

    [Fact]
    public async Task NestedUserRolesAreFilteredByTheProjectHeader()
    {
        // The User policy keeps a system read_users caller unrestricted, while
        // the Role policy's first rule still narrows the roles edge by
        // project_id = ctx.
        var member = SeedUserAsync(
            77,
            scopes: """["read_users","read_roles"]""");
        var projectRole = SeedRole("Project Admin", projectId: 5, level: "project");
        var systemRole = SeedRole("System Viewer", projectId: 0);
        var otherRole = SeedRole("Other Admin", projectId: 7, level: "project");
        _roles.Assign(77, projectRole.Id);
        _roles.Assign(77, systemRole.Id);
        _roles.Assign(77, otherRole.Id);
        var jwt = _gate.IssueJwt(member);

        var data = await QueryDataAsync(
            """
            query Users {
              users(first: 1) {
                edges { node { id roles { edges { node { id projectID } } } } }
              }
            }
            """,
            token: jwt,
            projectId: 5);

        var roles = data.GetProperty("users").GetProperty("edges")[0]
            .GetProperty("node").GetProperty("roles").GetProperty("edges")
            .EnumerateArray().ToArray();
        var role = Assert.Single(roles);
        Assert.Equal(
            "gid://axonhub/Project/5",
            role.GetProperty("node").GetProperty("projectID").GetString());
    }

    [Fact]
    public async Task CreateUserIsDeniedWithoutWriteUsers()
    {
        var member = SeedUserAsync(77, scopes: """["read_users"]""");
        var jwt = _gate.IssueJwt(member);

        var errors = await QueryErrorsAsync(
            """
            mutation CreateUser($input: CreateUserInput!) {
              createUser(input: $input) { id }
            }
            """,
            new { input = new { email = "new@example.com", password = "s3cret" } },
            token: jwt);

        AssertDenied(errors);
        Assert.Null(await _users.GetByIdAsync(78));
    }

    [Fact]
    public async Task UpdateProjectFollowsTheOwnerRuleAndThenTheSystemScope()
    {
        // project.go:129-132: the Project mutation chain is OwnerRule ->
        // UserWriteScopeRule(write_projects); it never runs the project-scope
        // rule, so owning the membership and holding write_projects on that
        // project still ends at default deny.
        var member = SeedUserAsync(77, scopes: null);
        _users.AssignProject(
            77,
            projectId: 5,
            isOwner: true,
            scopes: ["write_projects"]);
        _gate.Projects.Add(project =>
        {
            project.Id = 5;
            project.Name = "Original";
        });
        var memberJwt = _gate.IssueJwt(member);

        var denied = await QueryErrorsAsync(
            """
            mutation UpdateProject($id: ID!, $input: UpdateProjectInput!) {
              updateProject(id: $id, input: $input) { id name }
            }
            """,
            new
            {
                id = "gid://axonhub/Project/5",
                input = new { name = "Renamed" },
            },
            token: memberJwt);
        AssertDenied(denied);
        Assert.Equal(
            "Original",
            _gate.Projects.Rows.Single(row => row.Id == 5).Name);

        // OwnerRule allows the system owner without any scope.
        var owner = SeedUserAsync(78, scopes: null, isOwner: true);
        var ownerJwt = _gate.IssueJwt(owner);

        var data = await QueryDataAsync(
            """
            mutation UpdateProject($id: ID!, $input: UpdateProjectInput!) {
              updateProject(id: $id, input: $input) { id name }
            }
            """,
            new
            {
                id = "gid://axonhub/Project/5",
                input = new { name = "Renamed" },
            },
            token: ownerJwt);

        Assert.Equal(
            "Renamed",
            data.GetProperty("updateProject").GetProperty("name").GetString());
    }

    [Fact]
    public async Task AddUserToProjectReportsTheInsertFailureBeforeTheUserReadPolicy()
    {
        // user.go:420-452: UserProject.Create().Save runs inside the
        // transaction before client.User.Get. A duplicate membership therefore
        // surfaces the unique-index failure even though the same caller would
        // also be denied by the User query policy afterwards.
        var member = SeedUserAsync(77, scopes: """["write_users"]""");
        SeedUserAsync(78, scopes: null);
        _users.AssignProject(78, projectId: 5);
        var role = SeedRole("Member", projectId: 5, level: "project");
        var jwt = _gate.IssueJwt(member);

        var errors = await QueryErrorsAsync(
            """
            mutation AddUserToProject($input: AddUserToProjectInput!) {
              addUserToProject(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    projectId = "gid://axonhub/Project/5",
                    userId = "gid://axonhub/User/78",
                    roleIDs = new[] { "gid://axonhub/Role/" + role.Id },
                },
            },
            token: jwt);

        var message = errors[0].GetProperty("message").GetString();
        Assert.StartsWith(
            "failed to add user to project: ent: constraint failed: insert node to table",
            message);
        Assert.Contains("UNIQUE constraint failed", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddUserToProjectStillDeniesAtTheUserReadWhenTheInsertSucceeds()
    {
        // Same caller and targets, but user 79 is not a member yet: the insert
        // succeeds and the failure moves to client.User.Get, whose User query
        // policy denies a caller with no read_users.
        var member = SeedUserAsync(77, scopes: """["write_users"]""");
        SeedUserAsync(79, scopes: null);
        var role = SeedRole("Member", projectId: 5, level: "project");
        var jwt = _gate.IssueJwt(member);

        var errors = await QueryErrorsAsync(
            """
            mutation AddUserToProject($input: AddUserToProjectInput!) {
              addUserToProject(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    projectId = "gid://axonhub/Project/5",
                    userId = "gid://axonhub/User/79",
                    roleIDs = new[] { "gid://axonhub/Role/" + role.Id },
                },
            },
            token: jwt);

        // The deny wraps the User query policy at client.User.Get; the error
        // presenter reports every privacy-denied chain as "permission denied".
        AssertDenied(errors);
        // The transaction rolled back: no relationship and no role link.
        Assert.Empty(await _users.ListProjectUsersForUserAsync(79));
        Assert.Empty(_roles.UserRoleIds(79));
    }

    [Fact]
    public async Task AddUserToProjectDeniesAtTheCreatePolicyBeforeAnyInsert()
    {
        // A caller without write_users on the project is denied by the
        // UserProject mutation policy at mut.Save, before the insert.
        var member = SeedUserAsync(77, scopes: null);
        SeedUserAsync(78, scopes: null);
        var jwt = _gate.IssueJwt(member);

        var errors = await QueryErrorsAsync(
            """
            mutation AddUserToProject($input: AddUserToProjectInput!) {
              addUserToProject(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    projectId = "gid://axonhub/Project/5",
                    userId = "gid://axonhub/User/78",
                },
            },
            token: jwt);

        AssertDenied(errors);
        Assert.Empty(await _users.ListProjectUsersForUserAsync(78));
    }

    private UserRecord SeedUserAsync(long id, string? scopes, bool isOwner = false)
    {
        var user = _gate.Users.Add(row =>
        {
            row.Id = id;
            row.Email = $"user{id}@example.com";
            row.Status = "activated";
            row.IsOwner = isOwner;
        });

        _users.CreateAsync(
                new UserRecord
                {
                    Id = id,
                    Email = user.Email,
                    Status = "activated",
                    IsOwner = isOwner,
                    Scopes = scopes,
                },
                roleIds: null)
            .GetAwaiter()
            .GetResult();

        return user;
    }

    private RoleRecord SeedRole(string name, long projectId, string level = "system")
    {
        var now = DateTime.UtcNow;
        var role = new RoleRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            Name = name,
            Level = level,
            ProjectId = projectId,
        };

        _roles.CreateAsync(role).GetAwaiter().GetResult();
        return role;
    }

    private static void AssertDenied(JsonElement[] errors)
    {
        Assert.Equal("permission denied", errors[0].GetProperty("message").GetString());
        Assert.Equal(
            "FORBIDDEN",
            errors[0].GetProperty("extensions").GetProperty("code").GetString());
    }

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null,
        string? token = null,
        long? projectId = null)
    {
        var root = await ExecuteAsync(query, variables, token, projectId);
        Assert.False(root.TryGetProperty("errors", out _), root.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(
        string query,
        object? variables = null,
        string? token = null,
        long? projectId = null)
    {
        var root = await ExecuteAsync(query, variables, token, projectId);
        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        return errors.EnumerateArray().Select(static error => error.Clone()).ToArray();
    }

    private async Task<JsonElement> ExecuteAsync(
        string query,
        object? variables,
        string? token,
        long? projectId)
    {
        var payload = JsonSerializer.Serialize(new { query, variables });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/graphql")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (projectId is not null)
        {
            request.Headers.TryAddWithoutValidation(
                "X-Project-ID",
                "gid://axonhub/Project/" + projectId);
        }

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        AdminGraphqlHttpResponseAssertions.AssertStatus(response, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
