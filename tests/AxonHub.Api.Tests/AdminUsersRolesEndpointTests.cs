using System.Net;
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
/// Locks the users/roles GraphQL surface over /admin/graphql against the query
/// and mutation shapes the frontend sends (frontend/src/gql/users.ts and
/// frontend/src/gql/roles.ts): gid IDs, the Time and Cursor wire formats, the
/// entgql pagination/ordering rules and the reference error strings.
/// </summary>
public sealed class AdminUsersRolesEndpointTests : IAsyncLifetime
{
    private static readonly string[] DeactivatedStatuses = ["deactivated"];
    private static readonly string[] ReadUsersScopes = ["read_users"];
    private static readonly string[] UserStatusNames = ["activated", "deactivated"];
    private static readonly string[] RoleLevelNames = ["system", "project"];
    private static readonly string[] OrderDirectionNames = ["ASC", "DESC"];
    private static readonly string[] ReadProjectScopes = ["read_project"];
    private static readonly string[] ReadWriteProjectScopes = ["read_project", "write_project"];

    private readonly FakeUserAdminStore _users = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeInvitationStore _invitations = new();

    private readonly AdminGraphqlGateServices _gate = new();

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
    public async Task UsersWithoutFirstOrLastFailsLikeValidatePaginationArgs()
    {
        // useUser calls USERS_QUERY with only where:{id} (users.ts:166-176);
        // gqlgen's validatePaginationArgs rejects that request.
        var errors = await QueryErrorsAsync(
            """
            query Users($where: UserWhereInput) {
              users(where: $where) { edges { node { id } } }
            }
            """,
            new { where = new { id = "gid://axonhub/User/1" } });

        Assert.Equal(
            "either first or last must be provided",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task UsersQueryMatchesTheFrontendShapeAndOrdering()
    {
        var alice = SeedUser(
            "alice@example.com",
            new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            firstName: "Alice");
        alice.Scopes = """["read_users"]""";
        var bob = SeedUser(
            "bob@example.com",
            new DateTime(2026, 1, 2, 3, 4, 5, 123, DateTimeKind.Utc),
            firstName: "Bob",
            status: "deactivated");
        var role = SeedRole("Developer", projectId: 0);

        _roles.Assign(bob.Id, role.Id);

        var data = await QueryDataAsync(
            """
            query Users(
              $first: Int,
              $after: Cursor,
              $orderBy: UserOrder,
              $where: UserWhereInput) {
              users(first: $first, after: $after, orderBy: $orderBy, where: $where) {
                edges {
                  node {
                    id createdAt updatedAt email status firstName lastName
                    isOwner preferLanguage scopes
                    roles { edges { node { id name } } }
                  }
                }
                pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                totalCount
              }
            }
            """,
            new
            {
                first = 10,
                after = (string?)null,
                orderBy = new { field = "CREATED_AT", direction = "DESC" },
                where = new { emailContainsFold = "EXAMPLE.COM" },
            });

        // CREATED_AT is remapped to ent's default order (id) by the reference
        // resolver, keeping the requested direction (ent.resolvers.go:579-592).
        var users = data.GetProperty("users");
        var edges = users.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(2, edges.Length);
        Assert.Equal(
            "gid://axonhub/User/" + bob.Id,
            edges[0].GetProperty("node").GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/User/" + alice.Id,
            edges[1].GetProperty("node").GetProperty("id").GetString());

        var bobNode = edges[0].GetProperty("node");
        Assert.Equal("bob@example.com", bobNode.GetProperty("email").GetString());
        Assert.Equal("deactivated", bobNode.GetProperty("status").GetString());
        Assert.Equal("Bob", bobNode.GetProperty("firstName").GetString());
        Assert.Equal("", bobNode.GetProperty("lastName").GetString());
        Assert.False(bobNode.GetProperty("isOwner").GetBoolean());
        Assert.Equal("en", bobNode.GetProperty("preferLanguage").GetString());
        Assert.Equal(JsonValueKind.Null, bobNode.GetProperty("scopes").ValueKind);

        // time.Format(time.RFC3339Nano) trims trailing fractional zeroes.
        Assert.Equal(
            "2026-01-02T03:04:05.123Z",
            bobNode.GetProperty("createdAt").GetString());
        Assert.Equal(
            "2026-01-02T03:04:05Z",
            edges[1].GetProperty("node").GetProperty("createdAt").GetString());

        var bobRoles = bobNode.GetProperty("roles").GetProperty("edges")
            .EnumerateArray().ToArray();
        Assert.Single(bobRoles);
        Assert.Equal(
            "gid://axonhub/Role/" + role.Id,
            bobRoles[0].GetProperty("node").GetProperty("id").GetString());
        Assert.Equal("Developer", bobRoles[0].GetProperty("node").GetProperty("name").GetString());

        var pageInfo = users.GetProperty("pageInfo");
        Assert.False(pageInfo.GetProperty("hasNextPage").GetBoolean());
        Assert.False(pageInfo.GetProperty("hasPreviousPage").GetBoolean());
        Assert.Equal(2, users.GetProperty("totalCount").GetInt32());

        var cursor = pageInfo.GetProperty("endCursor").GetString()!;
        Assert.Equal(alice.Id, CursorCodec.Decode(cursor).Id);

        // The captured query proves CREATED_AT travelled as the default id order.
        var captured = Assert.Single(_users.Queries);
        Assert.Equal(UserSortField.Id, captured.SortField);
        Assert.Equal(SortDirection.Desc, captured.Direction);
        Assert.Equal("EXAMPLE.COM", captured.Where?.EmailContainsFold);
    }

    [Fact]
    public async Task UsersCursorPaginatesWithEntgqlPageInfo()
    {
        var first = SeedUser("a@example.com", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var second = SeedUser("b@example.com", new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        var page = await QueryDataAsync(
            """
            query Users($first: Int, $orderBy: UserOrder) {
              users(first: $first, orderBy: $orderBy) {
                edges { cursor node { id } }
                pageInfo { hasNextPage hasPreviousPage endCursor }
              }
            }
            """,
            new { first = 1, orderBy = new { field = "CREATED_AT", direction = "ASC" } });

        var edges = page.GetProperty("users").GetProperty("edges").EnumerateArray().ToArray();
        Assert.Single(edges);
        Assert.Equal(first.Id, CursorCodec.Decode(edges[0].GetProperty("cursor").GetString()!).Id);
        Assert.True(page.GetProperty("users").GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());

        var next = await QueryDataAsync(
            """
            query Users($first: Int, $after: Cursor) {
              users(first: $first, after: $after) {
                edges { node { id } }
                pageInfo { hasNextPage hasPreviousPage }
              }
            }
            """,
            new { first = 1, after = edges[0].GetProperty("cursor").GetString() });

        var nextEdges = next.GetProperty("users").GetProperty("edges").EnumerateArray().ToArray();
        Assert.Single(nextEdges);
        Assert.Equal(
            "gid://axonhub/User/" + second.Id,
            nextEdges[0].GetProperty("node").GetProperty("id").GetString());
        Assert.True(next.GetProperty("users").GetProperty("pageInfo").GetProperty("hasPreviousPage").GetBoolean());
    }

    [Fact]
    public async Task UserWhereFiltersStatusInAndIdIn()
    {
        var alice = SeedUser("alice@example.com", DateTime.UtcNow);
        SeedUser("bob@example.com", DateTime.UtcNow, status: "deactivated");

        var data = await QueryDataAsync(
            """
            query Users($where: UserWhereInput) {
              users(first: 10, where: $where) {
                edges { node { id status } }
              }
            }
            """,
            new { where = new { statusIn = DeactivatedStatuses } });

        var nodes = data.GetProperty("users").GetProperty("edges").EnumerateArray()
            .Select(static edge => edge.GetProperty("node")).ToArray();
        Assert.Single(nodes);
        Assert.Equal("deactivated", nodes[0].GetProperty("status").GetString());

        var byId = await QueryDataAsync(
            """
            query Users($where: UserWhereInput) {
              users(first: 10, where: $where) { edges { node { id } } }
            }
            """,
            new { where = new { idIn = new[] { "gid://axonhub/User/" + alice.Id } } });

        var byIdNodes = byId.GetProperty("users").GetProperty("edges").EnumerateArray().ToArray();
        Assert.Single(byIdNodes);
        Assert.Equal(
            "gid://axonhub/User/" + alice.Id,
            byIdNodes[0].GetProperty("node").GetProperty("id").GetString());
    }

    [Fact]
    public async Task NestedRolesAreUnboundedWithoutPaginationArgs()
    {
        // entgql's User.roles edge resolver routes through Paginate, which
        // validates first/last but accepts both being nil (gql_edge.go:759).
        var user = SeedUser("alice@example.com", DateTime.UtcNow);
        var systemRole = SeedRole("Developer", projectId: 0);
        var projectRole = SeedRole("Viewer", projectId: 7, level: "project");
        _roles.Assign(user.Id, projectRole.Id);
        _roles.Assign(user.Id, systemRole.Id);

        var data = await QueryDataAsync(
            """
            query Users {
              users(first: 1) {
                edges {
                  node {
                    roles {
                      edges {
                        node { id name level projectID scopes }
                      }
                      totalCount
                    }
                  }
                }
              }
            }
            """);

        var roles = data.GetProperty("users").GetProperty("edges")[0]
            .GetProperty("node").GetProperty("roles");
        var nodes = roles.GetProperty("edges").EnumerateArray()
            .Select(static edge => edge.GetProperty("node")).ToArray();

        Assert.Equal(2, nodes.Length);
        Assert.Equal("gid://axonhub/Role/" + systemRole.Id, nodes[0].GetProperty("id").GetString());
        Assert.Equal("system", nodes[0].GetProperty("level").GetString());
        Assert.Equal("gid://axonhub/Project/0", nodes[0].GetProperty("projectID").GetString());
        Assert.Equal("gid://axonhub/Role/" + projectRole.Id, nodes[1].GetProperty("id").GetString());
        Assert.Equal("project", nodes[1].GetProperty("level").GetString());
        Assert.Equal("gid://axonhub/Project/7", nodes[1].GetProperty("projectID").GetString());
        Assert.Equal(2, roles.GetProperty("totalCount").GetInt32());

        var captured = _roles.Queries[^1];
        Assert.Equal(user.Id, captured.UserId);
        Assert.Null(captured.First);
        Assert.Null(captured.Last);
    }

    [Fact]
    public async Task CreateUserHashesThePasswordLikeTheReference()
    {
        var role = SeedRole("Developer", projectId: 0);
        _roles.Assign(1, role.Id);

        var data = await QueryDataAsync(
            """
            mutation CreateUser($input: CreateUserInput!) {
              createUser(input: $input) {
                id email status preferLanguage firstName lastName isOwner scopes
                roles { edges { node { id } } }
              }
            }
            """,
            new
            {
                input = new
                {
                    email = "new@example.com",
                    password = "s3cret",
                    firstName = "New",
                    lastName = "User",
                    roleIDs = new[] { "gid://axonhub/Role/" + role.Id },
                    scopes = ReadUsersScopes,
                },
            });

        var user = data.GetProperty("createUser");
        Assert.Equal("gid://axonhub/User/1", user.GetProperty("id").GetString());
        Assert.Equal("activated", user.GetProperty("status").GetString());
        Assert.Equal("en", user.GetProperty("preferLanguage").GetString());
        Assert.Equal("New", user.GetProperty("firstName").GetString());
        Assert.False(user.GetProperty("isOwner").GetBoolean());
        Assert.Equal(ReadUsersScopes, user.GetProperty("scopes").EnumerateArray()
            .Select(static scope => scope.GetString()).ToArray());

        var stored = await _users.GetByIdAsync(1);
        Assert.NotNull(stored);
        Assert.Equal(120, stored.Password.Length);
        // hex.EncodeToString([]byte("$2a$10$...")) starts with 24 32 61 24 31 30 24.
        Assert.StartsWith("24326124313024", stored.Password, StringComparison.Ordinal);
        new PasswordHasher().Verify(stored.Password, "s3cret");
    }

    [Fact]
    public async Task DuplicateRoleNameReturnsTheCodedError()
    {
        SeedRole("Developer", projectId: 0);

        var errors = await QueryErrorsAsync(
            """
            mutation CreateRole($input: CreateRoleInput!) {
              createRole(input: $input) { id }
            }
            """,
            new { input = new { name = "Developer", level = "system" } });

        var error = errors[0];
        Assert.Equal("role name 'Developer' already exists", error.GetProperty("message").GetString());
        var extensions = error.GetProperty("extensions");
        Assert.Equal("DUPLICATE_NAME", extensions.GetProperty("code").GetString());
        Assert.Equal("role", extensions.GetProperty("resource").GetString());
        Assert.Equal("name", extensions.GetProperty("field").GetString());
        Assert.Equal("Developer", extensions.GetProperty("value").GetString());
    }

    [Fact]
    public async Task ProjectRolesRequireAProjectID()
    {
        var errors = await QueryErrorsAsync(
            """
            mutation CreateRole($input: CreateRoleInput!) {
              createRole(input: $input) { id }
            }
            """,
            new { input = new { name = "Viewer", level = "project" } });

        Assert.Equal(
            "project ID is required for project roles",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task RolesQueryFiltersByTheProjectGid()
    {
        SeedRole("Developer", projectId: 0);
        var projectRole = SeedRole("Viewer", projectId: 5, level: "project");

        var data = await QueryDataAsync(
            """
            query Roles($where: RoleWhereInput, $orderBy: RoleOrder) {
              roles(first: 10, where: $where, orderBy: $orderBy) {
                edges { node { id name level projectID scopes } }
                totalCount
              }
            }
            """,
            new
            {
                where = new { projectID = "gid://axonhub/Project/5" },
                orderBy = new { field = "CREATED_AT", direction = "DESC" },
            });

        var roles = data.GetProperty("roles");
        var nodes = roles.GetProperty("edges").EnumerateArray()
            .Select(static edge => edge.GetProperty("node")).ToArray();
        Assert.Single(nodes);
        Assert.Equal("gid://axonhub/Role/" + projectRole.Id, nodes[0].GetProperty("id").GetString());
        Assert.Equal("project", nodes[0].GetProperty("level").GetString());
        Assert.Equal("gid://axonhub/Project/5", nodes[0].GetProperty("projectID").GetString());
        Assert.Equal(1, roles.GetProperty("totalCount").GetInt32());

        Assert.Equal(5, _roles.Queries[^1].Where?.ProjectId);
    }

    [Fact]
    public async Task DeleteUserRejectsTheOwner()
    {
        var owner = SeedUser("owner@example.com", DateTime.UtcNow, isOwner: true);

        var errors = await QueryErrorsAsync(
            """
            mutation DeleteUser($id: ID!) { deleteUser(id: $id) }
            """,
            new { id = "gid://axonhub/User/" + owner.Id });

        Assert.Equal(
            "cannot delete owner user, transfer ownership first",
            errors[0].GetProperty("message").GetString());
        Assert.NotNull(await _users.GetByIdAsync(owner.Id));
    }

    [Fact]
    public async Task UpdateUserStatusAndDeleteUserMutateTheStore()
    {
        var user = SeedUser("alice@example.com", DateTime.UtcNow);

        var data = await QueryDataAsync(
            """
            mutation UpdateUserStatus($id: ID!, $status: UserStatus!) {
              updateUserStatus(id: $id, status: $status) { id status }
            }
            """,
            new { id = "gid://axonhub/User/" + user.Id, status = "deactivated" });

        Assert.Equal(
            "deactivated",
            data.GetProperty("updateUserStatus").GetProperty("status").GetString());

        var deleted = await QueryDataAsync(
            """
            mutation DeleteUser($id: ID!) { deleteUser(id: $id) }
            """,
            new { id = "gid://axonhub/User/" + user.Id });

        Assert.True(deleted.GetProperty("deleteUser").GetBoolean());
        Assert.Null(await _users.GetByIdAsync(user.Id));
    }

    [Fact]
    public async Task DeleteRoleRejectsTheDefaultDeveloperRole()
    {
        var developer = SeedRole("Developer", projectId: 0);

        var errors = await QueryErrorsAsync(
            """
            mutation DeleteRole($id: ID!) { deleteRole(id: $id) }
            """,
            new { id = "gid://axonhub/Role/" + developer.Id });

        Assert.Equal(
            "cannot delete the default Developer role",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task BulkDeleteRolesAcceptsTheIdList()
    {
        var first = SeedRole("Viewer", projectId: 0);
        var second = SeedRole("Editor", projectId: 0);

        var data = await QueryDataAsync(
            """
            mutation BulkDeleteRoles($ids: [ID!]!) { bulkDeleteRoles(ids: $ids) }
            """,
            new
            {
                ids = new[]
                {
                    "gid://axonhub/Role/" + first.Id,
                    "gid://axonhub/Role/" + second.Id,
                },
            });

        Assert.True(data.GetProperty("bulkDeleteRoles").GetBoolean());
        Assert.Null(await _roles.GetByIdAsync(first.Id));
        Assert.Null(await _roles.GetByIdAsync(second.Id));
    }

    [Fact]
    public async Task IntrospectionExposesTheReferenceEnumValues()
    {
        var data = await QueryDataAsync(
            """
            {
              userStatus: __type(name: "UserStatus") { enumValues { name } }
              roleLevel: __type(name: "RoleLevel") { enumValues { name } }
              orderDirection: __type(name: "OrderDirection") { enumValues { name } }
              time: __type(name: "Time") { kind }
              cursor: __type(name: "Cursor") { kind }
            }
            """);

        Assert.Equal(
            UserStatusNames,
            data.GetProperty("userStatus").GetProperty("enumValues").EnumerateArray()
                .Select(static value => value.GetProperty("name").GetString()).ToArray());
        Assert.Equal(
            RoleLevelNames,
            data.GetProperty("roleLevel").GetProperty("enumValues").EnumerateArray()
                .Select(static value => value.GetProperty("name").GetString()).ToArray());
        Assert.Equal(
            OrderDirectionNames,
            data.GetProperty("orderDirection").GetProperty("enumValues").EnumerateArray()
                .Select(static value => value.GetProperty("name").GetString()).ToArray());
        Assert.Equal("SCALAR", data.GetProperty("time").GetProperty("kind").GetString());
        Assert.Equal("SCALAR", data.GetProperty("cursor").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task NodeResolvesProjectWithProjectUsersLikeTheFrontendQuery()
    {
        var project = _gate.Projects.Add(static project =>
        {
            project.Name = "Test Project";
            project.Description = "A test project";
        });
        var alice = SeedUser("alice@example.com", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), firstName: "Alice");
        var developer = SeedRole("Developer", project.Id, level: "project");
        _roles.Assign(alice.Id, developer.Id);

        await _users.AddProjectUserAsync(alice.Id, project.Id, true, ReadProjectScopes, [developer.Id]);

        // buildProjectUsersQuery (frontend/src/features/proejct-users/data/users.ts).
        var projectId = "gid://axonhub/Project/" + project.Id;
        var data = await QueryDataAsync(
            """
            query NodeProject($projectId: ID!) {
              node(id: $projectId) {
                ... on Project {
                  id
                  name
                  projectUsers {
                    id
                    userID
                    projectID
                    isOwner
                    scopes
                    user {
                      id
                      createdAt
                      updatedAt
                      email
                      status
                      firstName
                      lastName
                      preferLanguage
                      roles(where: { projectID: $projectId }) { edges { node { id name } } }
                    }
                  }
                }
              }
            }
            """,
            new { projectId });

        var node = data.GetProperty("node");
        Assert.Equal(projectId, node.GetProperty("id").GetString());
        Assert.Equal("Test Project", node.GetProperty("name").GetString());

        var projectUser = Assert.Single(
            node.GetProperty("projectUsers").EnumerateArray().ToArray());
        Assert.Equal(
            "gid://axonhub/UserProject/1",
            projectUser.GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/User/" + alice.Id,
            projectUser.GetProperty("userID").GetString());
        Assert.Equal(projectId, projectUser.GetProperty("projectID").GetString());
        Assert.True(projectUser.GetProperty("isOwner").GetBoolean());
        Assert.Equal(
            ReadProjectScopes,
            projectUser.GetProperty("scopes").EnumerateArray()
                .Select(static scope => scope.GetString()).ToArray());

        var user = projectUser.GetProperty("user");
        Assert.Equal("gid://axonhub/User/" + alice.Id, user.GetProperty("id").GetString());
        Assert.Equal("alice@example.com", user.GetProperty("email").GetString());
        Assert.Equal("activated", user.GetProperty("status").GetString());
        Assert.Equal("Alice", user.GetProperty("firstName").GetString());

        var roles = user.GetProperty("roles").GetProperty("edges").EnumerateArray().ToArray();
        Assert.Single(roles);
        Assert.Equal(
            "gid://axonhub/Role/" + developer.Id,
            roles[0].GetProperty("node").GetProperty("id").GetString());
    }

    [Fact]
    public async Task NodeResolvesUserWithProjectUsers()
    {
        var project = _gate.Projects.Add();
        var alice = SeedUser("alice@example.com", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await _users.AddProjectUserAsync(alice.Id, project.Id, false, null, []);

        // users-add-to-project-dialog.tsx checks the user's existing projects.
        var data = await QueryDataAsync(
            """
            query NodeUser($userId: ID!) {
              node(id: $userId) {
                ... on User {
                  id
                  projectUsers { projectID }
                }
              }
            }
            """,
            new { userId = "gid://axonhub/User/" + alice.Id });

        var node = data.GetProperty("node");
        Assert.Equal("gid://axonhub/User/" + alice.Id, node.GetProperty("id").GetString());
        var projectUsers = node.GetProperty("projectUsers").EnumerateArray().ToArray();
        Assert.Single(projectUsers);
        Assert.Equal(
            "gid://axonhub/Project/" + project.Id,
            projectUsers[0].GetProperty("projectID").GetString());
    }

    [Fact]
    public async Task NodeReportsUnknownTypesAndMissingNodesLikeTheReference()
    {
        // The reference maps every ent type to a Noder lookup; the port only
        // resolves Project and User (C165).
        var unknown = await QueryErrorsAsync(
            """
            query Node($id: ID!) { node(id: $id) { id } }
            """,
            new { id = "gid://axonhub/Channel/1" });
        Assert.Equal(
            "unknown node type: Channel",
            unknown[0].GetProperty("message").GetString());

        var missingProject = await QueryErrorsAsync(
            """
            query Node($id: ID!) { node(id: $id) { id } }
            """,
            new { id = "gid://axonhub/Project/999" });
        Assert.Equal(
            "ent: project not found",
            missingProject[0].GetProperty("message").GetString());

        var missingUser = await QueryErrorsAsync(
            """
            query Node($id: ID!) { node(id: $id) { id } }
            """,
            new { id = "gid://axonhub/User/999" });
        Assert.Equal(
            "ent: user not found",
            missingUser[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task AddUserToProjectMutationMatchesTheFrontendShape()
    {
        var project = _gate.Projects.Add();
        var alice = SeedUser("alice@example.com", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var developer = SeedRole("Developer", project.Id, level: "project");

        var data = await QueryDataAsync(
            """
            mutation AddUserToProject($input: AddUserToProjectInput!) {
              addUserToProject(input: $input) {
                id userID projectID isOwner scopes
              }
            }
            """,
            new
            {
                input = new
                {
                    projectId = "gid://axonhub/Project/" + project.Id,
                    userId = "gid://axonhub/User/" + alice.Id,
                    isOwner = true,
                    scopes = ReadProjectScopes,
                    roleIDs = new[] { "gid://axonhub/Role/" + developer.Id },
                },
            });

        var userProject = data.GetProperty("addUserToProject");
        Assert.Equal("gid://axonhub/UserProject/1", userProject.GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/User/" + alice.Id,
            userProject.GetProperty("userID").GetString());
        Assert.Equal(
            "gid://axonhub/Project/" + project.Id,
            userProject.GetProperty("projectID").GetString());
        Assert.True(userProject.GetProperty("isOwner").GetBoolean());
        Assert.Equal(
            ReadProjectScopes,
            userProject.GetProperty("scopes").EnumerateArray()
                .Select(static scope => scope.GetString()).ToArray());

        Assert.Single(await _users.ListProjectUsersForUserAsync(alice.Id));
        Assert.Equal([developer.Id], _roles.UserRoleIds(alice.Id));
    }

    [Fact]
    public async Task AddUserToProjectRejectsRolesOfOtherProjectsLikeTheReference()
    {
        var project = _gate.Projects.Add();
        var otherProject = _gate.Projects.Add();
        var alice = SeedUser("alice@example.com", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var foreignRole = SeedRole("Other Admin", otherProject.Id, level: "project");

        // ProjectIDEQ(projectID) in the role query rejects roles of other
        // projects and system roles alike (user.go:404-409).
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
                    projectId = "gid://axonhub/Project/" + project.Id,
                    userId = "gid://axonhub/User/" + alice.Id,
                    roleIDs = new[] { "gid://axonhub/Role/" + foreignRole.Id },
                },
            });

        Assert.Equal(
            "project role " + foreignRole.Id + " not found",
            errors[0].GetProperty("message").GetString());

        // The failure happened before the transaction: no relationship exists.
        Assert.Empty(await _users.ListProjectUsersForUserAsync(alice.Id));
    }

    [Fact]
    public async Task RemoveUserFromProjectMutationReturnsTrueAndCleansUpProjectRoles()
    {
        var project = _gate.Projects.Add();
        var alice = SeedUser("alice@example.com", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var developer = SeedRole("Developer", project.Id, level: "project");
        var globalRole = SeedRole("Global Viewer", projectId: 0);
        await _users.AddProjectUserAsync(alice.Id, project.Id, false, null, [developer.Id]);
        _roles.Assign(alice.Id, globalRole.Id);

        var data = await QueryDataAsync(
            """
            mutation RemoveUserFromProject($input: RemoveUserFromProjectInput!) {
              removeUserFromProject(input: $input)
            }
            """,
            new
            {
                input = new
                {
                    projectId = "gid://axonhub/Project/" + project.Id,
                    userId = "gid://axonhub/User/" + alice.Id,
                },
            });

        Assert.True(data.GetProperty("removeUserFromProject").GetBoolean());
        Assert.Empty(await _users.ListProjectUsersForUserAsync(alice.Id));
        // The project role link is removed; the global role link survives.
        Assert.Equal([globalRole.Id], _roles.UserRoleIds(alice.Id));

        // Removing the now-missing relationship still returns true
        // (user.go:476-478).
        var again = await QueryDataAsync(
            """
            mutation RemoveUserFromProject($input: RemoveUserFromProjectInput!) {
              removeUserFromProject(input: $input)
            }
            """,
            new
            {
                input = new
                {
                    projectId = "gid://axonhub/Project/" + project.Id,
                    userId = "gid://axonhub/User/" + alice.Id,
                },
            });

        Assert.True(again.GetProperty("removeUserFromProject").GetBoolean());
    }

    [Fact]
    public async Task UpdateProjectUserMutationMatchesTheFrontendShape()
    {
        var project = _gate.Projects.Add();
        var alice = SeedUser("alice@example.com", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var admin = SeedRole("Project Admin", project.Id, level: "project");
        var member = SeedRole("Project Member", project.Id, level: "project");
        await _users.AddProjectUserAsync(alice.Id, project.Id, false, ReadProjectScopes, [member.Id]);

        var data = await QueryDataAsync(
            """
            mutation UpdateProjectUser($input: UpdateProjectUserInput!) {
              updateProjectUser(input: $input) {
                id userID projectID isOwner scopes
              }
            }
            """,
            new
            {
                input = new
                {
                    projectId = "gid://axonhub/Project/" + project.Id,
                    userId = "gid://axonhub/User/" + alice.Id,
                    isOwner = true,
                    scopes = ReadWriteProjectScopes,
                    addRoleIDs = new[] { "gid://axonhub/Role/" + admin.Id },
                    removeRoleIDs = new[] { "gid://axonhub/Role/" + member.Id },
                },
            });

        var userProject = data.GetProperty("updateProjectUser");
        Assert.True(userProject.GetProperty("isOwner").GetBoolean());
        Assert.Equal(
            ReadWriteProjectScopes,
            userProject.GetProperty("scopes").EnumerateArray()
                .Select(static scope => scope.GetString()).ToArray());
        Assert.Equal([admin.Id], _roles.UserRoleIds(alice.Id));
    }

    [Fact]
    public async Task UpdateProjectUserReportsTheMissingRelationshipLikeTheReference()
    {
        var project = _gate.Projects.Add();
        var alice = SeedUser("alice@example.com", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var errors = await QueryErrorsAsync(
            """
            mutation UpdateProjectUser($input: UpdateProjectUserInput!) {
              updateProjectUser(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    projectId = "gid://axonhub/Project/" + project.Id,
                    userId = "gid://axonhub/User/" + alice.Id,
                    scopes = ReadProjectScopes,
                },
            });

        Assert.Equal(
            "failed to find user project relationship: ent: user_project not found",
            errors[0].GetProperty("message").GetString());
    }

    private UserRecord SeedUser(
        string email,
        DateTime createdAt,
        string status = "activated",
        string firstName = "",
        bool isOwner = false)
    {
        var user = new UserRecord
        {
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            Email = email,
            Status = status,
            PreferLanguage = "en",
            Password = PasswordHasher.OidcOnlyPlaceholder,
            FirstName = firstName,
            LastName = "",
            IsOwner = isOwner,
        };

        _users.CreateAsync(user, null).GetAwaiter().GetResult();
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

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null,
        string? operationName = null)
    {
        var root = await ExecuteAsync(query, variables, operationName);
        Assert.False(root.TryGetProperty("errors", out _), root.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(
        string query,
        object? variables = null,
        string? operationName = null)
    {
        var root = await ExecuteAsync(query, variables, operationName);
        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        return errors.EnumerateArray().Select(static error => error.Clone()).ToArray();
    }

    private async Task<JsonElement> ExecuteAsync(
        string query,
        object? variables,
        string? operationName)
    {
        var payload = JsonSerializer.Serialize(new { query, variables, operationName });
        using var response = await _client.PostAsync(
            "/admin/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}

/// <summary>In-memory IUserAdminStore with ent-like filtering and keyset paging.</summary>
internal sealed class FakeUserAdminStore : IUserAdminStore
{
    private readonly List<UserRecord> _rows = [];
    private readonly List<UserProjectRecord> _userProjects = [];
    private long _nextId = 1;
    private long _nextProjectUserId = 1;

    public List<UserListQuery> Queries { get; } = [];

    /// <summary>Resolves the user_roles join for GetPermissionUserAsync (C161).</summary>
    public IRoleStore? RolesSource { get; set; }

    /// <summary>When set, UpdateStatusAsync throws it like a failed key disable would.</summary>
    public Exception? UpdateStatusFailure { get; set; }

    /// <summary>When set, DeleteAsync throws it like a failed key archive would.</summary>
    public Exception? DeleteFailure { get; set; }

    public void AssignProject(
        long userId,
        long projectId,
        bool isOwner = false,
        IReadOnlyList<string>? scopes = null)
    {
        _userProjects.Add(new UserProjectRecord
        {
            UserId = userId,
            ProjectId = projectId,
            IsOwner = isOwner,
            Scopes = scopes is null ? null : JsonSerializer.Serialize(scopes),
        });
    }

    public Task<long> CreateAsync(
        UserRecord user,
        IReadOnlyList<long>? roleIds,
        CancellationToken cancellationToken = default)
    {
        // Seeded rows may pin the id the gate fixtures key on (e.g. the JWT
        // user id 9); fresh rows take the next auto id.
        user.Id = user.Id > 0 ? user.Id : _nextId++;
        if (user.Id >= _nextId)
        {
            _nextId = user.Id + 1;
        }

        _rows.Add(user);
        return Task.FromResult(user.Id);
    }

    public Task<UserRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
        => Task.FromResult(Live(id));

    public async Task<PermissionUser?> GetPermissionUserAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var user = Live(id);
        if (user is null)
        {
            return null;
        }

        IReadOnlyList<RoleRecord> roleRows = [];
        if (RolesSource is not null)
        {
            var page = await RolesSource
                .ListAsync(new RoleListQuery { UserId = id, First = 10_000 }, cancellationToken)
                .ConfigureAwait(false);
            roleRows = page.Nodes;
        }

        var projects = _userProjects.Where(up => up.UserId == id).ToList();
        return new PermissionUser(user, roleRows, projects);
    }

    public Task<ListPage<UserRecord>> ListAsync(
        UserListQuery query,
        CancellationToken cancellationToken = default)
    {
        Queries.Add(query);

        var filtered = _rows
            .Where(row => row.DeletedAt == 0 && Matches(row, query.Where))
            .ToList();

        var page = FakePagination.Paginate(
            filtered,
            query.SortField == UserSortField.UpdatedAt,
            row => row.Id,
            row => row.UpdatedAt,
            query.Direction,
            query.After,
            query.Before,
            query.First,
            query.Last);

        return Task.FromResult(new ListPage<UserRecord>(
            page.Items, page.HasNextPage, page.HasPreviousPage, page.TotalCount));
    }

    public Task<UserRecord?> UpdateAsync(
        UserUpdate update,
        CancellationToken cancellationToken = default)
    {
        var row = Live(update.Id);
        if (row is null)
        {
            return Task.FromResult<UserRecord?>(null);
        }

        if (update.Email is not null)
        {
            row.Email = update.Email;
        }

        if (update.FirstName is not null)
        {
            row.FirstName = update.FirstName;
        }

        if (update.LastName is not null)
        {
            row.LastName = update.LastName;
        }

        if (update.PreferLanguage is not null)
        {
            row.PreferLanguage = update.PreferLanguage;
        }

        if (update.ClearAvatar)
        {
            row.Avatar = null;
        }
        else if (update.Avatar is not null)
        {
            row.Avatar = update.Avatar;
        }

        if (update.IsOwner is not null)
        {
            row.IsOwner = update.IsOwner.Value;
        }

        if (update.Password is not null)
        {
            row.Password = update.Password;
        }

        if (update.Scopes is not null)
        {
            row.Scopes = JsonSerializer.Serialize(update.Scopes);
        }

        row.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult<UserRecord?>(row);
    }

    public Task<UserRecord?> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default)
    {
        if (UpdateStatusFailure is not null)
        {
            throw UpdateStatusFailure;
        }

        var row = Live(id);
        if (row is null)
        {
            return Task.FromResult<UserRecord?>(null);
        }

        row.Status = status;
        row.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult<UserRecord?>(row);
    }

    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        if (DeleteFailure is not null)
        {
            throw DeleteFailure;
        }

        var row = Live(id);
        if (row is null)
        {
            return Task.FromResult(false);
        }

        row.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return Task.FromResult(true);
    }

    /// <summary>When set, AddProjectUserAsync throws it like a failed insert would.</summary>
    public Exception? AddProjectUserFailure { get; set; }

    public Task<UserProjectRecord> AddProjectUserAsync(
        long userId,
        long projectId,
        bool? isOwner,
        IReadOnlyList<string>? scopes,
        IReadOnlyList<long> roleIds,
        CancellationToken cancellationToken = default)
    {
        if (AddProjectUserFailure is not null)
        {
            throw AddProjectUserFailure;
        }

        if (_userProjects.Any(up => up.UserId == userId && up.ProjectId == projectId))
        {
            // The reference's SQLite error text for the unique index
            // user_projects_by_user_id_project_id (C153).
            throw new StoreFailureException(
                "failed to add user to project: ent: constraint failed: insert node to table "
                    + "\"user_projects\": UNIQUE constraint failed: user_projects.user_id, "
                    + "user_projects.project_id",
                new InvalidOperationException("duplicate relationship"));
        }

        var row = new UserProjectRecord
        {
            Id = _nextProjectUserId++,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            UserId = userId,
            ProjectId = projectId,
            // Ent's create default for is_owner is false (user.go:426-428).
            IsOwner = isOwner ?? false,
            Scopes = scopes is null ? null : JsonSerializer.Serialize(scopes),
        };
        _userProjects.Add(row);

        if (roleIds.Count > 0)
        {
            if (Live(userId) is null)
            {
                // The reference runs the whole mutation in one transaction, so
                // the created row rolls back with the failure (user.go:443-446).
                _userProjects.Remove(row);
                throw new StoreFailureException(
                    "failed to get user: ent: user not found",
                    new InvalidOperationException("ent: user not found"));
            }

            if (RolesSource is FakeRoleStore fakeRoles)
            {
                foreach (var roleId in roleIds)
                {
                    fakeRoles.Assign(userId, roleId);
                }
            }
        }

        return Task.FromResult(row);
    }

    public Task RemoveProjectUserAsync(
        long userId,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        var removed = _userProjects.RemoveAll(
            up => up.UserId == userId && up.ProjectId == projectId);

        // The reference returns success without role cleanup when no
        // relationship matched (user.go:476-478).
        if (removed == 0)
        {
            return Task.CompletedTask;
        }

        if (RolesSource is FakeRoleStore fakeRoles)
        {
            // Roles of this project the removed user still has (user.go:480-485).
            foreach (var roleId in fakeRoles.ProjectRoleIds(projectId))
            {
                if (fakeRoles.UserRoleIds(userId).Contains(roleId))
                {
                    fakeRoles.Unassign(userId, roleId);
                }
            }
        }

        return Task.CompletedTask;
    }

    public Task<UserProjectRecord> UpdateProjectUserAsync(
        long userId,
        long projectId,
        bool? isOwner,
        IReadOnlyList<string>? scopes,
        IReadOnlyList<long> addRoleIds,
        IReadOnlyList<long> removeRoleIds,
        CancellationToken cancellationToken = default)
    {
        var row = _userProjects.FirstOrDefault(
            up => up.UserId == userId && up.ProjectId == projectId);
        if (row is null)
        {
            throw new StoreFailureException(
                "failed to find user project relationship: ent: user_project not found",
                new InvalidOperationException("ent: user_project not found"));
        }

        if (isOwner is not null)
        {
            row.IsOwner = isOwner.Value;
        }

        if (scopes is not null)
        {
            row.Scopes = JsonSerializer.Serialize(scopes);
        }

        row.UpdatedAt = DateTime.UtcNow;

        if (Live(userId) is null)
        {
            throw new StoreFailureException(
                "failed to get user: ent: user not found",
                new InvalidOperationException("ent: user not found"));
        }

        if (RolesSource is FakeRoleStore fakeRoles)
        {
            // Ent applies AddRoleIDs before RemoveRoleIDs, so a role in both
            // lists ends up removed (user.go:560-570).
            foreach (var roleId in addRoleIds)
            {
                fakeRoles.Assign(userId, roleId);
            }

            foreach (var roleId in removeRoleIds)
            {
                fakeRoles.Unassign(userId, roleId);
            }
        }

        return Task.FromResult(row);
    }

    public Task<IReadOnlyList<UserProjectRecord>> ListProjectUsersAsync(
        long projectId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<UserProjectRecord>>(
            _userProjects.Where(up => up.ProjectId == projectId).ToList());

    public Task<IReadOnlyList<UserProjectRecord>> ListProjectUsersForUserAsync(
        long userId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<UserProjectRecord>>(
            _userProjects.Where(up => up.UserId == userId).ToList());

    private UserRecord? Live(long id)
        => _rows.FirstOrDefault(row => row.Id == id && row.DeletedAt == 0);

    private static bool Matches(UserRecord row, UserListFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.Id is not null && row.Id != filter.Id)
        {
            return false;
        }

        if (filter.IdIn is not null && !filter.IdIn.Contains(row.Id))
        {
            return false;
        }

        if (filter.FirstNameContainsFold is not null
            && !row.FirstName.Contains(
                filter.FirstNameContainsFold,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.EmailContainsFold is not null
            && !row.Email.Contains(
                filter.EmailContainsFold,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.StatusIn is not null && !filter.StatusIn.Contains(row.Status))
        {
            return false;
        }

        if (filter.Not is not null && Matches(row, filter.Not))
        {
            return false;
        }

        if (filter.And is not null && !filter.And.All(inner => Matches(row, inner)))
        {
            return false;
        }

        if (filter.Or is not null && !filter.Or.Any(inner => Matches(row, inner)))
        {
            return false;
        }

        return true;
    }
}

/// <summary>In-memory IRoleStore with ent-like filtering and keyset paging.</summary>
internal sealed class FakeRoleStore : IRoleStore
{
    private readonly List<RoleRecord> _rows = [];
    private readonly Dictionary<long, List<long>> _userRoles = [];
    private long _nextId = 1;

    public List<RoleListQuery> Queries { get; } = [];

    /// <summary>All role rows in creation order, soft-deleted included.</summary>
    public IReadOnlyList<RoleRecord> Rows => _rows;

    /// <summary>When set, CreateAsync throws it like a failed insert would.</summary>
    public Exception? CreateFailure { get; set; }

    public void Assign(long userId, long roleId)
    {
        if (!_userRoles.TryGetValue(userId, out var roleIds))
        {
            roleIds = [];
            _userRoles[userId] = roleIds;
        }

        // Mirrors the store's dedupe against the existing user_roles links.
        if (!roleIds.Contains(roleId))
        {
            roleIds.Add(roleId);
        }
    }

    /// <summary>The role IDs currently linked to the user (mutation order).</summary>
    public List<long> UserRoleIds(long userId)
        => _userRoles.TryGetValue(userId, out var roleIds) ? [.. roleIds] : [];

    /// <summary>The live role IDs whose project_id equals the given project.</summary>
    public List<long> ProjectRoleIds(long projectId)
        => _rows
            .Where(row => row.DeletedAt == 0 && row.ProjectId == projectId)
            .Select(static row => row.Id)
            .ToList();

    public void Unassign(long userId, long roleId)
    {
        if (_userRoles.TryGetValue(userId, out var roleIds))
        {
            roleIds.RemoveAll(id => id == roleId);
        }
    }

    public Task<long> CreateAsync(RoleRecord role, CancellationToken cancellationToken = default)
    {
        if (CreateFailure is not null)
        {
            return Task.FromException<long>(CreateFailure);
        }

        role.Id = _nextId++;
        _rows.Add(role);
        return Task.FromResult(role.Id);
    }

    public Task<RoleRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
        => Task.FromResult(Live(id));

    public Task<T> RunInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
        => action(cancellationToken);

    public Task RunInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
        => action(cancellationToken);

    public Task<bool> NameExistsAsync(
        string name,
        long projectId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_rows.Any(row =>
            row.DeletedAt == 0
            && row.Name == name
            && (row.ProjectId ?? 0) == projectId));

    public Task<ListPage<RoleRecord>> ListAsync(
        RoleListQuery query,
        CancellationToken cancellationToken = default)
    {
        Queries.Add(query);

        var filtered = _rows
            .Where(row => row.DeletedAt == 0 && Matches(row, query.Where))
            .Where(row => query.UserId is null
                || (_userRoles.TryGetValue(query.UserId.Value, out var roleIds)
                    && roleIds.Contains(row.Id)))
            .ToList();

        var page = FakePagination.Paginate(
            filtered,
            query.SortField == RoleSortField.UpdatedAt,
            row => row.Id,
            row => row.UpdatedAt,
            query.Direction,
            query.After,
            query.Before,
            query.First,
            query.Last);

        return Task.FromResult(new ListPage<RoleRecord>(
            page.Items, page.HasNextPage, page.HasPreviousPage, page.TotalCount));
    }

    public Task<IReadOnlyList<RoleRecord>> ListByIdsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<RoleRecord>>(
            _rows.Where(row => row.DeletedAt == 0 && ids.Contains(row.Id))
                .OrderBy(static row => row.Id)
                .ToList());

    public Task<RoleRecord?> UpdateAsync(
        long id,
        string? name,
        IReadOnlyList<string>? scopes,
        CancellationToken cancellationToken = default)
    {
        var row = Live(id);
        if (row is null)
        {
            return Task.FromResult<RoleRecord?>(null);
        }

        if (name is not null)
        {
            row.Name = name;
        }

        if (scopes is not null)
        {
            row.Scopes = JsonSerializer.Serialize(scopes);
        }

        row.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult<RoleRecord?>(row);
    }

    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        var row = Live(id);
        if (row is null)
        {
            return Task.FromResult(false);
        }

        SoftDelete(row);
        return Task.FromResult(true);
    }

    public Task DeleteManyAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        foreach (var id in ids)
        {
            var row = Live(id);
            if (row is not null)
            {
                SoftDelete(row);
            }
        }

        return Task.CompletedTask;
    }

    private RoleRecord? Live(long id)
        => _rows.FirstOrDefault(row => row.Id == id && row.DeletedAt == 0);

    private static void SoftDelete(RoleRecord row)
        => row.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static bool Matches(RoleRecord row, RoleListFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.Id is not null && row.Id != filter.Id)
        {
            return false;
        }

        if (filter.IdIn is not null && !filter.IdIn.Contains(row.Id))
        {
            return false;
        }

        if (filter.NameContainsFold is not null
            && !row.Name.Contains(filter.NameContainsFold, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.Level is not null && row.Level != filter.Level)
        {
            return false;
        }

        if (filter.ProjectId is not null && (row.ProjectId ?? 0) != filter.ProjectId)
        {
            return false;
        }

        if (filter.Not is not null && Matches(row, filter.Not))
        {
            return false;
        }

        if (filter.And is not null && !filter.And.All(inner => Matches(row, inner)))
        {
            return false;
        }

        if (filter.Or is not null && !filter.Or.Any(inner => Matches(row, inner)))
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// Mirrors entgql's connection algorithm: totalCount before cursors, keyset
/// filtering on (value,id), first/last trimming and the PageInfo flags.
/// </summary>
internal static class FakePagination
{
    public sealed record Result<T>(
        List<T> Items,
        bool HasNextPage,
        bool HasPreviousPage,
        int TotalCount);

    public static Result<T> Paginate<T>(
        List<T> filtered,
        bool sortByValue,
        Func<T, long> id,
        Func<T, object?> value,
        SortDirection direction,
        ListCursor? after,
        ListCursor? before,
        int? first,
        int? last)
    {
        var sorted = filtered
            .OrderBy(row => KeyValue(row, sortByValue, value))
            .ThenBy(row => id(row))
            .ToList();

        if (direction == SortDirection.Desc)
        {
            sorted.Reverse();
        }

        var totalCount = sorted.Count;

        IEnumerable<T> rows = sorted;
        if (after is not null)
        {
            rows = rows.Where(row => After(row, after, direction, sortByValue, id, value));
        }

        if (before is not null)
        {
            rows = rows.Where(row => Before(row, before, direction, sortByValue, id, value));
        }

        var items = rows.ToList();

        var hasNextPage = before is not null;
        var hasPreviousPage = after is not null;

        if (first is not null)
        {
            if (items.Count > first.Value)
            {
                items = items.Take(first.Value).ToList();
                hasNextPage = true;
            }
        }
        else if (last is not null)
        {
            if (items.Count > last.Value)
            {
                items = items.TakeLast(last.Value).ToList();
                hasPreviousPage = true;
            }
        }

        return new Result<T>(items, hasNextPage, hasPreviousPage, totalCount);
    }

    private static object? KeyValue<T>(T row, bool sortByValue, Func<T, object?> value)
        => sortByValue ? value(row) : null;

    private static bool After<T>(
        T row,
        ListCursor cursor,
        SortDirection direction,
        bool sortByValue,
        Func<T, long> id,
        Func<T, object?> value)
    {
        var comparison = Compare(row, cursor, sortByValue, id, value);
        return direction == SortDirection.Asc ? comparison > 0 : comparison < 0;
    }

    private static bool Before<T>(
        T row,
        ListCursor cursor,
        SortDirection direction,
        bool sortByValue,
        Func<T, long> id,
        Func<T, object?> value)
    {
        var comparison = Compare(row, cursor, sortByValue, id, value);
        return direction == SortDirection.Asc ? comparison < 0 : comparison > 0;
    }

    private static int Compare<T>(
        T row,
        ListCursor cursor,
        bool sortByValue,
        Func<T, long> id,
        Func<T, object?> value)
    {
        if (sortByValue)
        {
            var byValue = CompareValues(value(row), cursor.Value);
            if (byValue != 0)
            {
                return byValue;
            }
        }

        return id(row).CompareTo(cursor.Id);
    }

    private static int CompareValues(object? left, object? right) => (left, right) switch
    {
        (null, null) => 0,
        (null, _) => -1,
        (_, null) => 1,
        (DateTime first, DateTime second) => first.CompareTo(second),
        (string first, string second) => string.CompareOrdinal(first, second),
        (long first, long second) => first.CompareTo(second),
        _ => throw new InvalidOperationException(
            "unsupported cursor value comparison"),
    };
}
