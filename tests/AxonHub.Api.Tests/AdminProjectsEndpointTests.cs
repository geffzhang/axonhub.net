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
/// Locks the projects GraphQL surface over /admin/graphql against the queries
/// and mutations the frontend sends (frontend/src/features/projects/data/
/// projects.ts): gid IDs, Time and Cursor wire formats, the entgql
/// pagination/ordering rules, the profiles JSON shape and the reference error
/// strings from internal/server/biz/project.go.
/// </summary>
public sealed class AdminProjectsEndpointTests : IAsyncLifetime
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly string[] User3Gid = ["gid://axonhub/User/3"];
    private static readonly string[] User2Gid = ["gid://axonhub/User/2"];
    private static readonly int[] ChannelIds = [1, 2];
    private static readonly string[] ChannelTags = ["t1"];

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeUserAdminStore _users = new();

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
        builder.Services.AddScoped<PermissionValidator>();
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
    public async Task ProjectsQueryMatchesTheFrontendShapeAndOrdering()
    {
        var alpha = SeedProject(
            "Alpha",
            description: "First",
            createdAt: CreatedAt.AddSeconds(1),
            profiles:
                """{"activeProfile":"a","profiles":[{"name":"a","channelIDs":[1,2],"channelTags":["t1"],"channelTagsMatchMode":"all"},{"name":"b"}]}""");
        var beta = SeedProject("Beta", createdAt: CreatedAt);

        var data = await QueryDataAsync(
            ProjectsQuery,
            new
            {
                first = 10,
                orderBy = new { field = "CREATED_AT", direction = "DESC" },
            });

        // CREATED_AT is remapped to ent.DefaultProjectOrder's field (id) by the
        // reference resolver, keeping the requested direction.
        var connection = data.GetProperty("projects");
        var edges = connection.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(2, edges.Length);
        Assert.Equal(
            "gid://axonhub/Project/" + beta.Id,
            edges[0].GetProperty("node").GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/Project/" + alpha.Id,
            edges[1].GetProperty("node").GetProperty("id").GetString());

        var betaNode = edges[0].GetProperty("node");
        Assert.Equal("Beta", betaNode.GetProperty("name").GetString());
        Assert.Equal("", betaNode.GetProperty("description").GetString());
        Assert.Equal("active", betaNode.GetProperty("status").GetString());
        // The default column value {"activeProfile":"","profiles":null} maps to
        // an object with an empty active profile and a null list.
        var betaProfiles = betaNode.GetProperty("profiles");
        Assert.Equal(string.Empty, betaProfiles.GetProperty("activeProfile").GetString());
        Assert.Equal(JsonValueKind.Null, betaProfiles.GetProperty("profiles").ValueKind);

        var alphaNode = edges[1].GetProperty("node");
        Assert.Equal("Alpha", alphaNode.GetProperty("name").GetString());
        Assert.Equal("First", alphaNode.GetProperty("description").GetString());
        Assert.Equal("active", alphaNode.GetProperty("status").GetString());
        Assert.Equal(
            "2026-01-02T03:04:06Z",
            alphaNode.GetProperty("createdAt").GetString());

        var profiles = alphaNode.GetProperty("profiles");
        Assert.Equal("a", profiles.GetProperty("activeProfile").GetString());
        var profileNodes = profiles.GetProperty("profiles").EnumerateArray().ToArray();
        Assert.Equal(2, profileNodes.Length);
        Assert.Equal("a", profileNodes[0].GetProperty("name").GetString());
        Assert.Equal(
            [1, 2],
            profileNodes[0].GetProperty("channelIDs").EnumerateArray()
                .Select(static id => id.GetInt32()).ToArray());
        Assert.Equal(
            ["t1"],
            profileNodes[0].GetProperty("channelTags").EnumerateArray()
                .Select(static tag => tag.GetString()!).ToArray());
        Assert.Equal("all", profileNodes[0].GetProperty("channelTagsMatchMode").GetString());
        Assert.Equal("b", profileNodes[1].GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, profileNodes[1].GetProperty("channelIDs").ValueKind);
        Assert.Equal(JsonValueKind.Null, profileNodes[1].GetProperty("channelTags").ValueKind);

        // The empty match mode still marshals as "" (gqlgen binds the enum to a
        // plain Go string, C175).
        Assert.Equal(
            string.Empty,
            profileNodes[1].GetProperty("channelTagsMatchMode").GetString());

        Assert.Equal(alpha.Id, CursorCodec.Decode(edges[1].GetProperty("cursor").GetString()!).Id);

        var pageInfo = connection.GetProperty("pageInfo");
        Assert.False(pageInfo.GetProperty("hasNextPage").GetBoolean());
        Assert.False(pageInfo.GetProperty("hasPreviousPage").GetBoolean());
        Assert.Equal(2, connection.GetProperty("totalCount").GetInt32());

        var captured = Assert.Single(_gate.Projects.Queries);
        Assert.Equal(ProjectSortField.Id, captured.SortField);
        Assert.Equal(SortDirection.Desc, captured.Direction);
        Assert.Null(captured.Where);
    }

    [Fact]
    public async Task ProjectsQueryFiltersByNameContainsFoldAndStatus()
    {
        var alpha = SeedProject("Alpha", createdAt: CreatedAt);
        var beta = SeedProject("Beta", createdAt: CreatedAt.AddSeconds(1), status: "archived");

        // The frontend search sends or: [{ nameContainsFold }].
        var data = await QueryDataAsync(
            """
            query Projects($where: ProjectWhereInput) {
              projects(first: 10, where: $where) {
                edges { node { id name status } }
                totalCount
              }
            }
            """,
            new { where = new { or = new[] { new { nameContainsFold = "ALPH" } } } });

        var nodes = data.GetProperty("projects").GetProperty("edges").EnumerateArray()
            .Select(static edge => edge.GetProperty("node")).ToArray();
        Assert.Single(nodes);
        Assert.Equal("gid://axonhub/Project/" + alpha.Id, nodes[0].GetProperty("id").GetString());
        Assert.Equal(1, data.GetProperty("projects").GetProperty("totalCount").GetInt32());

        var captured = _gate.Projects.Queries[^1];
        var or = Assert.Single(captured.Where!.Or!);
        Assert.Equal("ALPH", or.NameContainsFold);

        var archived = await QueryDataAsync(
            """
            query Projects($where: ProjectWhereInput) {
              projects(first: 10, where: $where) { edges { node { id } } }
            }
            """,
            new { where = new { status = "archived" } });

        var archivedNodes = archived.GetProperty("projects").GetProperty("edges")
            .EnumerateArray().Select(static edge => edge.GetProperty("node")).ToArray();
        Assert.Single(archivedNodes);
        Assert.Equal(
            "gid://axonhub/Project/" + beta.Id,
            archivedNodes[0].GetProperty("id").GetString());
        Assert.Equal("archived", _gate.Projects.Queries[^1].Where!.Status);
    }

    [Fact]
    public async Task ProjectWhereInputExposesEveryScalarPredicate()
    {
        var data = await QueryDataAsync(
            """
            { where: __type(name: "ProjectWhereInput") { inputFields { name } } }
            """);

        string[] comparison = ["", "NEQ", "In", "NotIn", "GT", "GTE", "LT", "LTE"];
        string[] text =
        [
            .. comparison,
            "Contains",
            "HasPrefix",
            "HasSuffix",
            "EqualFold",
            "ContainsFold",
        ];

        var expected = new List<string> { "and", "not", "or" };
        expected.AddRange(comparison.Select(static suffix => "id" + suffix));
        expected.AddRange(comparison.Select(static suffix => "createdAt" + suffix));
        expected.AddRange(comparison.Select(static suffix => "updatedAt" + suffix));
        expected.AddRange(text.Select(static suffix => "name" + suffix));
        expected.AddRange(text.Select(static suffix => "description" + suffix));
        expected.AddRange(comparison.Take(4).Select(static suffix => "status" + suffix));

        var actual = data.GetProperty("where").GetProperty("inputFields").EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expected.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            actual);
    }

    [Fact]
    public async Task QueryProjectsMapsEveryScalarPredicate()
    {
        await QueryDataAsync(
            """
            query Projects($where: ProjectWhereInput) {
              projects(first: 10, where: $where) {
                edges { node { id } }
                totalCount
              }
            }
            """,
            new
            {
                where = new
                {
                    id = "gid://axonhub/Project/1",
                    idNEQ = "gid://axonhub/Project/2",
                    idIn = new[] { "gid://axonhub/Project/3" },
                    idNotIn = new[] { "gid://axonhub/Project/4" },
                    idGT = "gid://axonhub/Project/5",
                    idGTE = "gid://axonhub/Project/6",
                    idLT = "gid://axonhub/Project/7",
                    idLTE = "gid://axonhub/Project/8",
                    createdAt = "2026-01-02T03:04:05Z",
                    createdAtNEQ = "2026-01-02T03:04:06Z",
                    createdAtIn = new[] { "2026-01-02T03:04:07Z" },
                    createdAtNotIn = new[] { "2026-01-02T03:04:08Z" },
                    createdAtGT = "2026-01-02T03:04:09Z",
                    createdAtGTE = "2026-01-02T03:04:10Z",
                    createdAtLT = "2026-01-02T03:04:11Z",
                    createdAtLTE = "2026-01-02T03:04:12Z",
                    updatedAt = "2026-02-03T04:05:05Z",
                    updatedAtNEQ = "2026-02-03T04:05:06Z",
                    updatedAtIn = new[] { "2026-02-03T04:05:07Z" },
                    updatedAtNotIn = new[] { "2026-02-03T04:05:08Z" },
                    updatedAtGT = "2026-02-03T04:05:09Z",
                    updatedAtGTE = "2026-02-03T04:05:10Z",
                    updatedAtLT = "2026-02-03T04:05:11Z",
                    updatedAtLTE = "2026-02-03T04:05:12Z",
                    name = "Alpha",
                    nameNEQ = "Beta",
                    nameIn = new[] { "Gamma" },
                    nameNotIn = new[] { "Delta" },
                    nameGT = "a",
                    nameGTE = "b",
                    nameLT = "c",
                    nameLTE = "d",
                    nameContains = "amm",
                    nameHasPrefix = "Al",
                    nameHasSuffix = "ta",
                    nameEqualFold = "ALPHA",
                    nameContainsFold = "AMM",
                    description = "First",
                    descriptionNEQ = "Second",
                    descriptionIn = new[] { "Third" },
                    descriptionNotIn = new[] { "Fourth" },
                    descriptionGT = "e",
                    descriptionGTE = "f",
                    descriptionLT = "g",
                    descriptionLTE = "h",
                    descriptionContains = "ir",
                    descriptionHasPrefix = "Fi",
                    descriptionHasSuffix = "st",
                    descriptionEqualFold = "FIRST",
                    descriptionContainsFold = "IRS",
                    status = "archived",
                    statusNEQ = "active",
                    statusIn = new[] { "archived" },
                    statusNotIn = new[] { "active" },
                    not = new { name = "Nope" },
                    and = new[] { new { nameContains = "a" } },
                    or = new[] { new { nameHasSuffix = "z" } },
                },
            });

        var where = _gate.Projects.Queries[^1].Where!;
        Assert.Equal(1L, where.Id);
        Assert.Equal(2L, where.IdNEQ);
        Assert.Equal([3L], where.IdIn!);
        Assert.Equal([4L], where.IdNotIn!);
        Assert.Equal(5L, where.IdGT);
        Assert.Equal(6L, where.IdGTE);
        Assert.Equal(7L, where.IdLT);
        Assert.Equal(8L, where.IdLTE);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), where.CreatedAt);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 6, DateTimeKind.Utc), where.CreatedAtNEQ);
        Assert.Equal(
            [new DateTime(2026, 1, 2, 3, 4, 7, DateTimeKind.Utc)],
            where.CreatedAtIn!);
        Assert.Equal(
            [new DateTime(2026, 1, 2, 3, 4, 8, DateTimeKind.Utc)],
            where.CreatedAtNotIn!);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 9, DateTimeKind.Utc), where.CreatedAtGT);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 10, DateTimeKind.Utc), where.CreatedAtGTE);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 11, DateTimeKind.Utc), where.CreatedAtLT);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 12, DateTimeKind.Utc), where.CreatedAtLTE);
        Assert.Equal(new DateTime(2026, 2, 3, 4, 5, 5, DateTimeKind.Utc), where.UpdatedAt);
        Assert.Equal(new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc), where.UpdatedAtNEQ);
        Assert.Equal(
            [new DateTime(2026, 2, 3, 4, 5, 7, DateTimeKind.Utc)],
            where.UpdatedAtIn!);
        Assert.Equal(
            [new DateTime(2026, 2, 3, 4, 5, 8, DateTimeKind.Utc)],
            where.UpdatedAtNotIn!);
        Assert.Equal(new DateTime(2026, 2, 3, 4, 5, 9, DateTimeKind.Utc), where.UpdatedAtGT);
        Assert.Equal(new DateTime(2026, 2, 3, 4, 5, 10, DateTimeKind.Utc), where.UpdatedAtGTE);
        Assert.Equal(new DateTime(2026, 2, 3, 4, 5, 11, DateTimeKind.Utc), where.UpdatedAtLT);
        Assert.Equal(new DateTime(2026, 2, 3, 4, 5, 12, DateTimeKind.Utc), where.UpdatedAtLTE);
        Assert.Equal("Alpha", where.Name);
        Assert.Equal("Beta", where.NameNEQ);
        Assert.Equal(["Gamma"], where.NameIn!);
        Assert.Equal(["Delta"], where.NameNotIn!);
        Assert.Equal("a", where.NameGT);
        Assert.Equal("b", where.NameGTE);
        Assert.Equal("c", where.NameLT);
        Assert.Equal("d", where.NameLTE);
        Assert.Equal("amm", where.NameContains);
        Assert.Equal("Al", where.NameHasPrefix);
        Assert.Equal("ta", where.NameHasSuffix);
        Assert.Equal("ALPHA", where.NameEqualFold);
        Assert.Equal("AMM", where.NameContainsFold);
        Assert.Equal("First", where.Description);
        Assert.Equal("Second", where.DescriptionNEQ);
        Assert.Equal(["Third"], where.DescriptionIn!);
        Assert.Equal(["Fourth"], where.DescriptionNotIn!);
        Assert.Equal("e", where.DescriptionGT);
        Assert.Equal("f", where.DescriptionGTE);
        Assert.Equal("g", where.DescriptionLT);
        Assert.Equal("h", where.DescriptionLTE);
        Assert.Equal("ir", where.DescriptionContains);
        Assert.Equal("Fi", where.DescriptionHasPrefix);
        Assert.Equal("st", where.DescriptionHasSuffix);
        Assert.Equal("FIRST", where.DescriptionEqualFold);
        Assert.Equal("IRS", where.DescriptionContainsFold);
        Assert.Equal("archived", where.Status);
        Assert.Equal("active", where.StatusNEQ);
        Assert.Equal(["archived"], where.StatusIn!);
        Assert.Equal(["active"], where.StatusNotIn!);
        Assert.Equal("Nope", where.Not!.Name);
        Assert.Equal("a", Assert.Single(where.And!).NameContains);
        Assert.Equal("z", Assert.Single(where.Or!).NameHasSuffix);
    }

    [Fact]
    public async Task ProjectsCursorPaginatesWithEntgqlPageInfo()
    {
        SeedProject("One", createdAt: CreatedAt);
        SeedProject("Two", createdAt: CreatedAt.AddSeconds(1));
        SeedProject("Three", createdAt: CreatedAt.AddSeconds(2));

        var page = await QueryDataAsync(
            """
            query Projects($first: Int, $orderBy: ProjectOrder) {
              projects(first: $first, orderBy: $orderBy) {
                edges { cursor node { id name } }
                pageInfo { hasNextPage hasPreviousPage endCursor }
              }
            }
            """,
            new { first = 2, orderBy = new { field = "CREATED_AT", direction = "ASC" } });

        var edges = page.GetProperty("projects").GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(2, edges.Length);
        Assert.True(
            page.GetProperty("projects").GetProperty("pageInfo")
                .GetProperty("hasNextPage").GetBoolean());
        var cursor = page.GetProperty("projects").GetProperty("pageInfo")
            .GetProperty("endCursor").GetString();
        Assert.Equal(2, CursorCodec.Decode(cursor).Id);

        var next = await QueryDataAsync(
            """
            query Projects($first: Int, $after: Cursor) {
              projects(first: $first, after: $after) {
                edges { node { id name } }
                pageInfo { hasNextPage hasPreviousPage }
              }
            }
            """,
            new { first = 2, after = cursor });

        var nextEdges = next.GetProperty("projects").GetProperty("edges")
            .EnumerateArray().ToArray();
        Assert.Single(nextEdges);
        Assert.Equal("Three", nextEdges[0].GetProperty("node").GetProperty("name").GetString());
        Assert.False(
            next.GetProperty("projects").GetProperty("pageInfo")
                .GetProperty("hasNextPage").GetBoolean());
        Assert.True(
            next.GetProperty("projects").GetProperty("pageInfo")
                .GetProperty("hasPreviousPage").GetBoolean());
    }

    [Fact]
    public async Task ProjectsWithoutFirstOrLastFailsLikeValidatePaginationArgs()
    {
        var errors = await QueryErrorsAsync(
            """
            query Projects($where: ProjectWhereInput) {
              projects(where: $where) { edges { node { id } } }
            }
            """,
            new { where = new { id = "gid://axonhub/Project/1" } });

        Assert.Equal(
            "either first or last must be provided",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task CreateProjectWritesThePresetRolesAndOwnerRow()
    {
        var data = await QueryDataAsync(
            """
            mutation CreateProject($input: CreateProjectInput!) {
              createProject(input: $input) {
                id name description status createdAt updatedAt
                profiles {
                  activeProfile
                  profiles { name channelIDs channelTags channelTagsMatchMode }
                }
              }
            }
            """,
            new { input = new { name = "Alpha", description = "First", status = "archived" } });

        var created = data.GetProperty("createProject");
        Assert.Equal("Alpha", created.GetProperty("name").GetString());
        Assert.Equal("First", created.GetProperty("description").GetString());
        // CreateProject ignores the input status and applies ent's default.
        Assert.Equal("active", created.GetProperty("status").GetString());
        var createdProfiles = created.GetProperty("profiles");
        Assert.Equal(string.Empty, createdProfiles.GetProperty("activeProfile").GetString());
        Assert.Equal(JsonValueKind.Null, createdProfiles.GetProperty("profiles").ValueKind);

        var project = Assert.Single(_gate.Projects.Rows);
        Assert.Equal("Alpha", project.Name);
        Assert.Equal(
            "gid://axonhub/Project/" + project.Id,
            created.GetProperty("id").GetString());

        Assert.Equal(["Admin", "Developer", "Viewer"], _roles.Rows.Select(static row => row.Name));
        Assert.All(
            _roles.Rows,
            row =>
            {
                Assert.Equal(RoleManagementService.LevelProject, row.Level);
                Assert.Equal(project.Id, row.ProjectId);
            });

        var owner = Assert.Single(_gate.Projects.ProjectUsers);
        Assert.Equal(AdminGraphqlGateServices.UserId, owner.UserId);
        Assert.Equal(project.Id, owner.ProjectId);
        Assert.True(owner.IsOwner);
        Assert.Equal("[]", owner.Scopes);
    }

    [Fact]
    public async Task CreateProjectDuplicateNameReturnsTheCodedExtensions()
    {
        SeedProject("Alpha", createdAt: CreatedAt);

        var errors = await QueryErrorsAsync(
            """
            mutation CreateProject($input: CreateProjectInput!) {
              createProject(input: $input) { id }
            }
            """,
            new { input = new { name = "Alpha" } });

        var error = errors[0];
        Assert.Equal("project name 'Alpha' already exists", error.GetProperty("message").GetString());
        var extensions = error.GetProperty("extensions");
        Assert.Equal("DUPLICATE_NAME", extensions.GetProperty("code").GetString());
        Assert.Equal("project", extensions.GetProperty("resource").GetString());
        Assert.Equal("name", extensions.GetProperty("field").GetString());
        Assert.Equal("Alpha", extensions.GetProperty("value").GetString());
        Assert.Single(_gate.Projects.Rows);
        Assert.Empty(_roles.Rows);
    }

    [Fact]
    public async Task UpdateProjectAppliesNameAndEdgesAndIgnoresStatus()
    {
        var project = SeedProject("Alpha", createdAt: CreatedAt);
        _gate.Projects.ProjectUsers.Add(ProjectUser(project.Id, 2));

        var data = await QueryDataAsync(
            """
            mutation UpdateProject($id: ID!, $input: UpdateProjectInput!) {
              updateProject(id: $id, input: $input) {
                id name description status updatedAt
              }
            }
            """,
            new
            {
                id = "gid://axonhub/Project/" + project.Id,
                input = new
                {
                    name = "Beta",
                    description = "Second",
                    status = "archived",
                    addUserIDs = User3Gid,
                    removeUserIDs = User2Gid,
                },
            });

        var updated = data.GetProperty("updateProject");
        Assert.Equal("Beta", updated.GetProperty("name").GetString());
        Assert.Equal("Second", updated.GetProperty("description").GetString());
        // UpdateProject only sets name/description and the user edges, so the
        // input status is ignored and status changes go through
        // updateProjectStatus.
        Assert.Equal("active", updated.GetProperty("status").GetString());

        Assert.Equal("Beta", project.Name);
        Assert.Equal("Second", project.Description);
        Assert.Equal("active", project.Status);
        var added = Assert.Single(_gate.Projects.ProjectUsers);
        Assert.Equal(3, added.UserId);
        Assert.False(added.IsOwner);
        Assert.Equal("[]", added.Scopes);
    }

    [Fact]
    public async Task UpdateProjectDuplicateUserAddFailsWithTheEdgePrefix()
    {
        var project = SeedProject("Alpha", createdAt: CreatedAt);
        _gate.Projects.ProjectUsers.Add(ProjectUser(project.Id, 3));

        var errors = await QueryErrorsAsync(
            """
            mutation UpdateProject($id: ID!, $input: UpdateProjectInput!) {
              updateProject(id: $id, input: $input) { id }
            }
            """,
            new
            {
                id = "gid://axonhub/Project/" + project.Id,
                input = new
                {
                    name = "Beta",
                    addUserIDs = User3Gid,
                },
            });

        Assert.Equal(
            "failed to update project: add m2m edge for table user_projects: "
            + "duplicate key value violates unique constraint "
            + "\"user_projects_by_user_id_project_id\"",
            errors[0].GetProperty("message").GetString());
        // The reference update is one ent transaction: the name rolls back too.
        Assert.Equal("Alpha", project.Name);
    }

    [Fact]
    public async Task UpdateProjectStatusArchivesAndActivates()
    {
        var project = SeedProject("Alpha", createdAt: CreatedAt);

        var archived = await QueryDataAsync(
            """
            mutation UpdateProjectStatus($id: ID!, $status: ProjectStatus!) {
              updateProjectStatus(id: $id, status: $status) { id status }
            }
            """,
            new { id = "gid://axonhub/Project/" + project.Id, status = "archived" });

        Assert.Equal(
            "archived",
            archived.GetProperty("updateProjectStatus").GetProperty("status").GetString());
        Assert.Equal("archived", project.Status);

        var active = await QueryDataAsync(
            """
            mutation UpdateProjectStatus($id: ID!, $status: ProjectStatus!) {
              updateProjectStatus(id: $id, status: $status) { status }
            }
            """,
            new { id = "gid://axonhub/Project/" + project.Id, status = "active" });

        Assert.Equal(
            "active",
            active.GetProperty("updateProjectStatus").GetProperty("status").GetString());
        Assert.Equal("active", project.Status);
    }

    [Fact]
    public async Task UpdateProjectProfilesRoundTripsTheGoJsonShape()
    {
        var project = SeedProject("Alpha", createdAt: CreatedAt);

        var data = await QueryDataAsync(
            """
            mutation UpdateProjectProfiles($id: ID!, $input: UpdateProjectProfilesInput!) {
              updateProjectProfiles(id: $id, input: $input) {
                id
                profiles {
                  activeProfile
                  profiles { name channelIDs channelTags channelTagsMatchMode }
                }
              }
            }
            """,
            new
            {
                id = "gid://axonhub/Project/" + project.Id,
                input = new
                {
                    activeProfile = "a",
                    profiles = new object[]
                    {
                        new
                        {
                            name = "a",
                            channelIDs = ChannelIds,
                            channelTags = ChannelTags,
                            channelTagsMatchMode = "none",
                        },
                        new { name = "b" },
                    },
                },
            });

        var profiles = data.GetProperty("updateProjectProfiles").GetProperty("profiles");
        Assert.Equal("a", profiles.GetProperty("activeProfile").GetString());
        var nodes = profiles.GetProperty("profiles").EnumerateArray().ToArray();
        Assert.Equal("none", nodes[0].GetProperty("channelTagsMatchMode").GetString());
        Assert.Equal(string.Empty, nodes[1].GetProperty("channelTagsMatchMode").GetString());

        // The stored jsonb is Go's encoding/json shape with omitempty.
        Assert.Equal(
            """{"activeProfile":"a","profiles":[{"name":"a","channelIDs":[1,2],"channelTags":["t1"],"channelTagsMatchMode":"none"},{"name":"b"}]}""",
            project.Profiles);
    }

    [Fact]
    public async Task UpdateProjectProfilesValidationErrorsAreNotStoreWrapped()
    {
        var project = SeedProject("Alpha", createdAt: CreatedAt);
        var before = project.Profiles;

        var errors = await QueryErrorsAsync(
            """
            mutation UpdateProjectProfiles($id: ID!, $input: UpdateProjectProfilesInput!) {
              updateProjectProfiles(id: $id, input: $input) { id }
            }
            """,
            new
            {
                id = "gid://axonhub/Project/" + project.Id,
                input = new
                {
                    activeProfile = "",
                    profiles = new object[] { new { name = "a" }, new { name = "A" } },
                },
            });

        Assert.Equal("duplicate profile name: A", errors[0].GetProperty("message").GetString());
        Assert.Equal(before, project.Profiles);
    }

    [Fact]
    public async Task DeleteProjectSoftDeletesAndReturnsTrue()
    {
        var project = SeedProject("Alpha", createdAt: CreatedAt);
        _gate.Projects.ProjectUsers.Add(ProjectUser(project.Id, 2));

        var data = await QueryDataAsync(
            """
            mutation DeleteProject($id: ID!) { deleteProject(id: $id) }
            """,
            new { id = "gid://axonhub/Project/" + project.Id });

        Assert.True(data.GetProperty("deleteProject").GetBoolean());
        Assert.Null(await _gate.Projects.GetByIdAsync(project.Id));
        Assert.Empty(_gate.Projects.ProjectUsers);
    }

    [Fact]
    public async Task DeleteProjectMissingReturnsTheEntNotFoundError()
    {
        var errors = await QueryErrorsAsync(
            """
            mutation DeleteProject($id: ID!) { deleteProject(id: $id) }
            """,
            new { id = "gid://axonhub/Project/42" });

        Assert.Equal(
            "failed to get project: ent: project not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task DeleteProjectRequiresTheSystemOwner()
    {
        var project = SeedProject("Alpha", createdAt: CreatedAt);
        var outsider = _gate.Users.Add(user =>
        {
            user.Id = 12;
            user.Email = "outsider@example.com";
            user.IsOwner = false;
        });

        using var client = _app.GetTestClient();
        AdminGraphqlGateServices.Authorize(client, _gate.IssueJwt(outsider));

        var payload = JsonSerializer.Serialize(new
        {
            query = """
                mutation DeleteProject($id: ID!) { deleteProject(id: $id) }
                """,
            variables = new { id = "gid://axonhub/Project/" + project.Id },
        });
        using var response = await client.PostAsync(
            "/admin/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("errors").EnumerateArray().First();
        Assert.Equal(
            "permission denied: insufficient permissions: only system owners can delete projects",
            error.GetProperty("message").GetString());
        Assert.NotNull(await _gate.Projects.GetByIdAsync(project.Id));
    }

    private const string ProjectsQuery = """
        query GetProjects(
          $first: Int,
          $after: Cursor,
          $orderBy: ProjectOrder,
          $where: ProjectWhereInput) {
          projects(first: $first, after: $after, orderBy: $orderBy, where: $where) {
            edges {
              node {
                id
                createdAt
                updatedAt
                name
                description
                status
                profiles {
                  activeProfile
                  profiles {
                    name
                    channelIDs
                    channelTags
                    channelTagsMatchMode
                  }
                }
              }
              cursor
            }
            pageInfo {
              hasNextPage
              hasPreviousPage
              startCursor
              endCursor
            }
            totalCount
          }
        }
        """;

    private ProjectRecord SeedProject(
        string name,
        string description = "",
        DateTime? createdAt = null,
        string? profiles = """{"activeProfile":"","profiles":null}""",
        string status = "active")
        => _gate.Projects.Add(project =>
        {
            project.Name = name;
            project.Description = description;
            project.Status = status;
            project.Profiles = profiles;
            project.CreatedAt = createdAt ?? CreatedAt;
            project.UpdatedAt = createdAt ?? CreatedAt;
        });

    private static UserProjectRecord ProjectUser(long projectId, long userId) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
        IsOwner = false,
        Scopes = "[]",
        UserId = userId,
        ProjectId = projectId,
    };

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
