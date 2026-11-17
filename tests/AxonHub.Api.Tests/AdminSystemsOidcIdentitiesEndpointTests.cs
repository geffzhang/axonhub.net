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
/// Locks the systems / oidcIdentities root queries and node(System) over
/// /admin/graphql (ent.resolvers.go:427-436, 519-528; ent.graphql:2586-2657,
/// 5616-5674): the entgql connection shape, the missing CREATED_AT rewrite,
/// the System policy (owner or read_settings) and the OIDCIdentity policy
/// (owner/read_users see all, every other user sees their own rows).
/// </summary>
public sealed class AdminSystemsOidcIdentitiesEndpointTests : IAsyncLifetime
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly FakeSystemStore _systems = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeOidcIdentityAdminStore _identities = new();
    private readonly AdminGraphqlGateServices _gate = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<ISystemStore>(_systems);
        builder.Services.AddSingleton<IUserAdminStore>(_users);
        builder.Services.AddSingleton<IOidcIdentityAdminStore>(_identities);
        builder.Services.AddScoped<SystemPrivacy>();

        _app = builder.Build();
        // The fixture's ISystemStore replaces the gate's, so seed the JWT
        // secret into the store the auth middleware actually reads.
        _gate.SeedJwtSecret(_app.Services);
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
    public async Task SystemsQueryMatchesTheEntgqlConnectionShape()
    {
        var alpha = _systems.Add(
            "alpha",
            """{"a":1}""",
            row =>
            {
                row.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                row.UpdatedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc);
            });
        var beta = _systems.Add(
            "beta",
            """{"b":2}""",
            row =>
            {
                row.CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
                row.UpdatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
            });
        _systems.Add("gone", "x", row => row.DeletedAt = 1);

        var data = await QueryDataAsync(
            """
            query Systems($first: Int, $orderBy: SystemOrder, $where: SystemWhereInput) {
              systems(first: $first, orderBy: $orderBy, where: $where) {
                edges { cursor node { id key value createdAt updatedAt } }
                pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                totalCount
              }
            }
            """,
            new
            {
                first = 10,
                orderBy = new { field = "CREATED_AT", direction = "DESC" },
                where = new { keyContainsFold = "A" },
            });

        var systems = data.GetProperty("systems");
        var edges = systems.GetProperty("edges").EnumerateArray().ToArray();

        // systems does not rewrite CREATED_AT to the default order: the rows
        // really are ordered by created_at descending.
        Assert.Equal(2, edges.Length);
        Assert.Equal(
            "gid://axonhub/System/" + beta.Id,
            edges[0].GetProperty("node").GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/System/" + alpha.Id,
            edges[1].GetProperty("node").GetProperty("id").GetString());

        var betaNode = edges[0].GetProperty("node");
        Assert.Equal("beta", betaNode.GetProperty("key").GetString());
        Assert.Equal("""{"b":2}""", betaNode.GetProperty("value").GetString());
        Assert.Equal("2026-01-02T00:00:00Z", betaNode.GetProperty("createdAt").GetString());
        Assert.Equal("2026-01-02T00:00:00Z", betaNode.GetProperty("updatedAt").GetString());

        Assert.False(systems.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());
        Assert.False(systems.GetProperty("pageInfo").GetProperty("hasPreviousPage").GetBoolean());
        Assert.Equal(2, systems.GetProperty("totalCount").GetInt32());

        var start = CursorCodec.Decode(
            systems.GetProperty("pageInfo").GetProperty("startCursor").GetString());
        Assert.Equal(beta.Id, start.Id);
        Assert.IsType<DateTime>(start.Value);

        var captured = Assert.Single(_systems.ListQueries);
        Assert.Equal(SystemSortField.CreatedAt, captured.SortField);
        Assert.Equal(SortDirection.Desc, captured.Direction);
        Assert.Equal("A", captured.Where?.KeyContainsFold);
    }

    [Fact]
    public async Task SystemsCursorPaginatesOnTheOrderField()
    {
        var alpha = _systems.Add(
            "alpha",
            "1",
            row => row.UpdatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        _systems.Add(
            "beta",
            "2",
            row => row.UpdatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        _systems.Add(
            "gamma",
            "3",
            row => row.UpdatedAt = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));

        var page = await QueryDataAsync(
            """
            query Systems($first: Int, $orderBy: SystemOrder, $where: SystemWhereInput) {
              systems(first: $first, orderBy: $orderBy, where: $where) {
                edges { cursor node { key } }
                pageInfo { hasNextPage hasPreviousPage }
              }
            }
            """,
            new
            {
                first = 1,
                orderBy = new { field = "UPDATED_AT", direction = "ASC" },
                // Keeps the gate's seeded JWT-secret row out of the page.
                where = new { keyIn = new[] { "alpha", "beta", "gamma" } },
            });

        var edges = page.GetProperty("systems").GetProperty("edges").EnumerateArray().ToArray();
        Assert.Single(edges);
        Assert.Equal("alpha", edges[0].GetProperty("node").GetProperty("key").GetString());
        Assert.True(
            page.GetProperty("systems").GetProperty("pageInfo")
                .GetProperty("hasNextPage").GetBoolean());

        var cursor = edges[0].GetProperty("cursor").GetString();
        var decoded = CursorCodec.Decode(cursor);
        Assert.Equal(alpha.Id, decoded.Id);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), decoded.Value);

        var next = await QueryDataAsync(
            """
            query Systems($first: Int, $after: Cursor) {
              systems(first: $first, after: $after) {
                edges { node { key } }
                pageInfo { hasNextPage hasPreviousPage }
              }
            }
            """,
            new { first = 1, after = cursor });

        var nextEdges = next.GetProperty("systems").GetProperty("edges")
            .EnumerateArray().ToArray();
        Assert.Equal("beta", nextEdges[0].GetProperty("node").GetProperty("key").GetString());
        Assert.True(
            next.GetProperty("systems").GetProperty("pageInfo")
                .GetProperty("hasPreviousPage").GetBoolean());
    }

    [Fact]
    public async Task SystemsQueryDeniesANonOwnerWithoutReadSettings()
    {
        _systems.Add("security_settings", "{}");
        var member = await SeedMemberAsync("member@example.com", scopes: null);

        var errors = await QueryErrorsAsync(
            """
            query Systems { systems(first: 10) { edges { node { key } } } }
            """,
            variables: null,
            token: _gate.IssueJwt(member));

        Assert.Equal("permission denied", errors[0].GetProperty("message").GetString());
        Assert.Equal(
            "FORBIDDEN",
            errors[0].GetProperty("extensions").GetProperty("code").GetString());
        Assert.Empty(_systems.ListQueries);
    }

    [Fact]
    public async Task SystemsQueryAllowsANonOwnerWithReadSettings()
    {
        _systems.Add("security_settings", "{}");
        _systems.Add("retry_policy", "{}");
        var member = await SeedMemberAsync(
            "scoped@example.com",
            scopes: """["read_settings"]""");

        var data = await QueryDataAsync(
            """
            query Systems($where: SystemWhereInput) {
              systems(first: 10, where: $where) { edges { node { key } } totalCount }
            }
            """,
            new { where = new { keyIn = new[] { "security_settings", "retry_policy" } } },
            token: _gate.IssueJwt(member));

        Assert.Equal(2, data.GetProperty("systems").GetProperty("totalCount").GetInt32());
        Assert.Single(_systems.ListQueries);
    }

    [Fact]
    public async Task SystemNodeRunsTheSystemQueryPolicy()
    {
        var system = _systems.Add("security_settings", """{"blocked_ips":[]}""");

        var data = await QueryDataAsync(
            """
            query SystemNode($id: ID!) {
              node(id: $id) { ... on System { id key value } }
            }
            """,
            new { id = "gid://axonhub/System/" + system.Id });

        var node = data.GetProperty("node");
        Assert.Equal("gid://axonhub/System/" + system.Id, node.GetProperty("id").GetString());
        Assert.Equal("security_settings", node.GetProperty("key").GetString());

        var member = await SeedMemberAsync("denied@example.com", scopes: null);
        var errors = await QueryErrorsAsync(
            """
            query SystemNode($id: ID!) { node(id: $id) { id } }
            """,
            new { id = "gid://axonhub/System/" + system.Id },
            token: _gate.IssueJwt(member));

        Assert.Equal("permission denied", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task OidcIdentityNodeTypeStaysUnknown()
    {
        // The reference's guidTypeToNodeType map does not register
        // OIDCIdentity even though the SDL type implements Node.
        var errors = await QueryErrorsAsync(
            """
            query Node($id: ID!) { node(id: $id) { id } }
            """,
            new { id = "gid://axonhub/OIDCIdentity/1" });

        Assert.Equal(
            "unknown node type: OIDCIdentity",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task OidcIdentitiesOwnerSeesEveryRowAndTheUserEdge()
    {
        await SeedAdminOnlyUserAsync(9, "owner@example.com");
        var member = await SeedMemberAsync("member@example.com", scopes: null);

        var own = _identities.Add(row =>
        {
            row.UserId = 9;
            row.Email = "owner@example.com";
        });
        var theirs = _identities.Add(row =>
        {
            row.Issuer = "https://other.example.com";
            row.Subject = "sub-2";
            row.UserId = 77;
            row.Email = "member@example.com";
            row.IdpName = "okta";
            row.LastLoginAt = new DateTime(2026, 2, 3, 4, 5, 6, 123, DateTimeKind.Utc);
        });
        _identities.Add(row => row.DeletedAt = 1);

        var data = await QueryDataAsync(
            """
            query OidcIdentities($first: Int, $orderBy: OIDCIdentityOrder) {
              oidcIdentities(first: $first, orderBy: $orderBy) {
                edges {
                  node {
                    id issuer subject email idpName lastLoginAt userID
                    user { id email }
                  }
                }
                pageInfo { hasNextPage hasPreviousPage }
                totalCount
              }
            }
            """,
            new { first = 10, orderBy = new { field = "CREATED_AT", direction = "ASC" } });

        var identities = data.GetProperty("oidcIdentities");
        var edges = identities.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(2, edges.Length);
        Assert.Equal(
            "gid://axonhub/OIDCIdentity/" + own.Id,
            edges[0].GetProperty("node").GetProperty("id").GetString());

        var theirsNode = edges[1].GetProperty("node");
        Assert.Equal(
            "gid://axonhub/OIDCIdentity/" + theirs.Id,
            theirsNode.GetProperty("id").GetString());
        Assert.Equal("https://other.example.com", theirsNode.GetProperty("issuer").GetString());
        Assert.Equal("sub-2", theirsNode.GetProperty("subject").GetString());
        Assert.Equal("member@example.com", theirsNode.GetProperty("email").GetString());
        Assert.Equal("okta", theirsNode.GetProperty("idpName").GetString());
        Assert.Equal(
            "2026-02-03T04:05:06.123Z",
            theirsNode.GetProperty("lastLoginAt").GetString());
        Assert.Equal(
            "gid://axonhub/User/77",
            theirsNode.GetProperty("userID").GetString());

        // The user edge loads the related user by user_id, not by the identity
        // row id (ent's obj.User(ctx)).
        var user = theirsNode.GetProperty("user");
        Assert.Equal("gid://axonhub/User/" + member.Id, user.GetProperty("id").GetString());
        Assert.Equal("member@example.com", user.GetProperty("email").GetString());

        Assert.Equal(2, identities.GetProperty("totalCount").GetInt32());
        Assert.False(
            identities.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());

        var captured = Assert.Single(_identities.Queries);
        Assert.Equal(OidcIdentitySortField.CreatedAt, captured.SortField);
        Assert.Null(captured.Where);
    }

    [Fact]
    public async Task OidcIdentitiesNonOwnerSeesOnlyTheirOwnRows()
    {
        await SeedAdminOnlyUserAsync(9, "owner@example.com");
        var member = await SeedMemberAsync("member@example.com", scopes: null);

        _identities.Add(row => row.UserId = 9);
        var mine = _identities.Add(row =>
        {
            row.Issuer = "https://other.example.com";
            row.Subject = "sub-2";
            row.UserId = 77;
        });

        var data = await QueryDataAsync(
            """
            query OidcIdentities($first: Int) {
              oidcIdentities(first: $first) {
                edges { node { id userID } }
                totalCount
              }
            }
            """,
            new { first = 10 },
            token: _gate.IssueJwt(member));

        var identities = data.GetProperty("oidcIdentities");
        var edges = identities.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Single(edges);
        Assert.Equal(
            "gid://axonhub/OIDCIdentity/" + mine.Id,
            edges[0].GetProperty("node").GetProperty("id").GetString());
        Assert.Equal(1, identities.GetProperty("totalCount").GetInt32());

        // UserOwnedQueryRule evaluates as a user_id predicate.
        var captured = Assert.Single(_identities.Queries);
        Assert.Equal(77, captured.Where?.UserId);
    }

    [Fact]
    public async Task OidcIdentitiesNonOwnerWhereIsAndedWithTheOwnerPredicate()
    {
        await SeedAdminOnlyUserAsync(9, "owner@example.com");
        var member = await SeedMemberAsync("member@example.com", scopes: null);

        _identities.Add(row => row.UserId = 9);
        var mine = _identities.Add(row =>
        {
            row.Issuer = "https://other.example.com";
            row.Subject = "sub-2";
            row.UserId = 77;
            row.Email = "member@example.com";
        });

        var data = await QueryDataAsync(
            """
            query OidcIdentities($first: Int, $where: OIDCIdentityWhereInput) {
              oidcIdentities(first: $first, where: $where) {
                edges { node { id } }
                totalCount
              }
            }
            """,
            new
            {
                first = 10,
                where = new { emailContainsFold = "MEMBER@" },
            },
            token: _gate.IssueJwt(member));

        var edges = data.GetProperty("oidcIdentities").GetProperty("edges")
            .EnumerateArray().ToArray();
        Assert.Single(edges);
        Assert.Equal(
            "gid://axonhub/OIDCIdentity/" + mine.Id,
            edges[0].GetProperty("node").GetProperty("id").GetString());

        var captured = Assert.Single(_identities.Queries);
        Assert.NotNull(captured.Where?.And);
        Assert.Equal(2, captured.Where!.And!.Count);
        Assert.Equal("MEMBER@", captured.Where.And[0].EmailContainsFold);
        Assert.Equal(77, captured.Where.And[1].UserId);
    }

    [Fact]
    public async Task OidcIdentitiesAllowsAUserWithReadUsersToSeeEverything()
    {
        await SeedAdminOnlyUserAsync(9, "owner@example.com");
        var member = await SeedMemberAsync(
            "member@example.com",
            scopes: """["read_users"]""");

        _identities.Add(row => row.UserId = 9);
        _identities.Add(row =>
        {
            row.Issuer = "https://other.example.com";
            row.Subject = "sub-2";
            row.UserId = 77;
        });

        var data = await QueryDataAsync(
            """
            query OidcIdentities($first: Int) {
              oidcIdentities(first: $first) { edges { node { id } } totalCount }
            }
            """,
            new { first = 10 },
            token: _gate.IssueJwt(member));

        Assert.Equal(2, data.GetProperty("oidcIdentities").GetProperty("totalCount").GetInt32());
        Assert.Null(Assert.Single(_identities.Queries).Where);
    }

    [Fact]
    public async Task OidcIdentitiesRejectsApiKeyPrincipals()
    {
        _identities.Add(row => row.UserId = 9);
        _gate.AddServiceAccountKey();

        var errors = await QueryErrorsAsync(
            """
            query OidcIdentities { oidcIdentities(first: 10) { edges { node { id } } } }
            """,
            variables: null,
            token: "sk-service-account");

        Assert.Equal("permission denied", errors[0].GetProperty("message").GetString());
        Assert.Equal(
            "FORBIDDEN",
            errors[0].GetProperty("extensions").GetProperty("code").GetString());
        Assert.Empty(_identities.Queries);
    }

    [Fact]
    public async Task QueryOidcIdentitiesMapsEveryPredicateGroup()
    {
        _identities.Add(row => row.UserId = 9);

        await QueryDataAsync(
            """
            query OidcIdentities($where: OIDCIdentityWhereInput) {
              oidcIdentities(first: 10, where: $where) { totalCount }
            }
            """,
            new
            {
                where = new
                {
                    id = "gid://axonhub/OIDCIdentity/1",
                    issuer = "https://issuer.example.com",
                    subjectContainsFold = "SUB",
                    email = "alice@example.com",
                    emailNotNil = true,
                    idpNameIsNil = true,
                    lastLoginAtIsNil = true,
                    userID = "gid://axonhub/User/9",
                    hasUser = true,
                    hasUserWith = new[] { new { email = "alice@example.com" } },
                    not = new { subject = "Nope" },
                },
            });

        var where = Assert.Single(_identities.Queries).Where!;
        Assert.Equal(1L, where.Id);
        Assert.Equal("https://issuer.example.com", where.Issuer);
        Assert.Equal("SUB", where.SubjectContainsFold);
        Assert.Equal("alice@example.com", where.Email);
        Assert.True(where.EmailNotNil);
        Assert.True(where.IdpNameIsNil);
        Assert.True(where.LastLoginAtIsNil);
        Assert.Equal(9L, where.UserId);
        Assert.True(where.HasUser);
        Assert.Equal("alice@example.com", Assert.Single(where.HasUserWith!).Email);
        Assert.Equal("Nope", where.Not!.Subject);
    }

    [Fact]
    public async Task OidcIdentityWhereInputExposesEveryScalarAndUserEdgePredicate()
    {
        var data = await QueryDataAsync(
            """
            { where: __type(name: "OIDCIdentityWhereInput") { inputFields { name } } }
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
        string[] nullableText =
        [
            .. comparison,
            "Contains",
            "HasPrefix",
            "HasSuffix",
            "IsNil",
            "NotNil",
            "EqualFold",
            "ContainsFold",
        ];

        var expected = new List<string> { "and", "not", "or" };
        expected.AddRange(comparison.Select(static suffix => "id" + suffix));
        expected.AddRange(comparison.Select(static suffix => "createdAt" + suffix));
        expected.AddRange(comparison.Select(static suffix => "updatedAt" + suffix));
        expected.AddRange(text.Select(static suffix => "issuer" + suffix));
        expected.AddRange(text.Select(static suffix => "subject" + suffix));
        expected.AddRange(nullableText.Select(static suffix => "email" + suffix));
        expected.AddRange(nullableText.Select(static suffix => "idpName" + suffix));
        expected.AddRange(
        [
            .. comparison.Select(static suffix => "lastLoginAt" + suffix),
            "lastLoginAtIsNil",
            "lastLoginAtNotNil",
        ]);
        expected.AddRange(comparison.Take(4).Select(static suffix => "userID" + suffix));
        expected.AddRange(["hasUser", "hasUserWith"]);

        var actual = data.GetProperty("where").GetProperty("inputFields").EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expected.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            actual);
    }

    [Fact]
    public async Task SchemaExposesEverySystemWherePredicate()
    {
        var data = await QueryDataAsync(
            """
            { where: __type(name: "SystemWhereInput") { inputFields { name } } }
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
        expected.AddRange(text.Select(static suffix => "key" + suffix));
        expected.AddRange(text.Select(static suffix => "value" + suffix));

        var whereFields = data.GetProperty("where").GetProperty("inputFields")
            .EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expected.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            whereFields);
    }

    private async Task<UserRecord> SeedMemberAsync(string email, string? scopes)
    {
        var member = _gate.Users.Add(user =>
        {
            user.Id = 77;
            user.Email = email;
            user.Status = "activated";
            user.IsOwner = false;
        });

        await _users.CreateAsync(
            new UserRecord
            {
                Id = member.Id,
                Email = email,
                Status = "activated",
                IsOwner = false,
                Scopes = scopes,
            },
            roleIds: null);

        return member;
    }

    /// <summary>
    /// Adds a row to the admin user store only (the gate already knows id 9),
    /// so the OIDCIdentity.user edge resolves without a duplicate gate user.
    /// </summary>
    private async Task SeedAdminOnlyUserAsync(long id, string email)
    {
        await _users.CreateAsync(
            new UserRecord
            {
                Id = id,
                Email = email,
                Status = "activated",
                IsOwner = false,
            },
            roleIds: null);
    }

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null,
        string? token = null)
    {
        var root = await ExecuteAsync(query, variables, token);
        Assert.False(root.TryGetProperty("errors", out _), root.ToString());
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
        var payload = JsonSerializer.Serialize(new { query, variables });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/graphql")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        if (token is not null)
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        AdminGraphqlHttpResponseAssertions.AssertStatus(response, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// In-memory oidc_identities table mirroring the Dapper store's live-row
/// predicate, scalar filters and entgql connection algorithm.
/// </summary>
internal sealed class FakeOidcIdentityAdminStore : IOidcIdentityAdminStore
{
    private readonly List<OidcIdentityRecord> _rows = [];
    private long _nextId = 1;

    public List<OidcIdentityListQuery> Queries { get; } = [];

    public OidcIdentityRecord Add(Action<OidcIdentityRecord>? configure = null)
    {
        var row = new OidcIdentityRecord
        {
            CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc),
            Issuer = "https://issuer.example.com",
            Subject = "subject-" + _nextId,
        };
        configure?.Invoke(row);
        row.Id = _nextId;
        _nextId++;
        _rows.Add(row);
        return row;
    }

    public Task<ListPage<OidcIdentityRecord>> ListAsync(
        OidcIdentityListQuery query,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Queries.Add(query);

        var filtered = _rows
            .Where(row => row.DeletedAt == 0 && Matches(row, query.Where))
            .ToList();

        var page = FakePagination.Paginate(
            filtered,
            query.SortField != OidcIdentitySortField.Id,
            row => row.Id,
            row => query.SortField switch
            {
                OidcIdentitySortField.CreatedAt => row.CreatedAt,
                OidcIdentitySortField.UpdatedAt => row.UpdatedAt,
                _ => null,
            },
            query.Direction,
            query.After,
            query.Before,
            query.First,
            query.Last);

        return Task.FromResult(new ListPage<OidcIdentityRecord>(
            page.Items,
            page.HasNextPage,
            page.HasPreviousPage,
            page.TotalCount));
    }

    private static bool Matches(OidcIdentityRecord row, OidcIdentityListFilter? filter)
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

        if (filter.Issuer is not null
            && !string.Equals(row.Issuer, filter.Issuer, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.IssuerIn is not null
            && !filter.IssuerIn.Contains(row.Issuer, StringComparer.Ordinal))
        {
            return false;
        }

        if (filter.Subject is not null
            && !string.Equals(row.Subject, filter.Subject, StringComparison.Ordinal))
        {
            return false;
        }

        if (filter.SubjectIn is not null
            && !filter.SubjectIn.Contains(row.Subject, StringComparer.Ordinal))
        {
            return false;
        }

        if (filter.EmailContainsFold is not null
            && (row.Email is null
                || !row.Email.Contains(
                    filter.EmailContainsFold,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (filter.IdpNameContainsFold is not null
            && (row.IdpName is null
                || !row.IdpName.Contains(
                    filter.IdpNameContainsFold,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (filter.UserId is not null && row.UserId != filter.UserId)
        {
            return false;
        }

        if (filter.UserIdIn is not null && !filter.UserIdIn.Contains(row.UserId))
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
}
