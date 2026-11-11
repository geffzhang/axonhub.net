using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the REST surface of api/invitation.go: the JWT-gated
/// POST /admin/invitations (routes.go:112) and the public lookup/registration
/// pair (routes.go:87-88), including the gin envelope, the 400/401/403/404/500
/// classification and the InvitationResponse JSON shape.
/// </summary>
public sealed class AdminInvitationEndpointTests : IAsyncLifetime
{
    private const string Secret = "test-secret";

    private readonly FakeUserStore _users = new();
    private readonly FakeSystemStore _systems = new();
    private readonly FakeUserProfileStore _profiles = new();
    private readonly FakePasswordHasher _passwords = new();
    private readonly FakeOidcLoginOnlyProviders _oidc = new();
    private readonly FakeUserAdminStore _adminUsers = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeProjectStore _projects = new();
    private readonly FakeInvitationStore _invitations = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IUserStore>(_users);
        builder.Services.AddSingleton<ISystemStore>(_systems);
        builder.Services.AddSingleton<IUserProfileStore>(_profiles);
        builder.Services.AddSingleton<IPasswordHasher>(_passwords);
        builder.Services.AddSingleton<IOidcLoginOnlyProviders>(_oidc);
        builder.Services.AddSingleton<IUserAdminStore>(_adminUsers);
        builder.Services.AddSingleton<IRoleStore>(_roles);
        builder.Services.AddSingleton<IProjectStore>(_projects);
        builder.Services.AddSingleton<IInvitationStore>(_invitations);
        builder.Services.AddScoped<ManagementAuthService>();
        builder.Services.AddScoped<PermissionValidator>();
        builder.Services.AddScoped<InvitationService>();
        builder.Services.AddScoped<IManagementIdentity, HttpContextManagementIdentity>();

        _invitations.Users = _adminUsers;
        _invitations.RolesSource = _roles;
        _invitations.Profiles = _profiles;
        _adminUsers.RolesSource = _roles;

        // The admin JWT gate authenticates against the system secret; without
        // it every token-bearing request fails with "failed to validate token".
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);

        _app = builder.Build();

        // Program.cs mounts the admin group's WithJWTAuth + WithProjectID on
        // /admin/invitations only; the /auth pair stays public.
        _app.UseWhen(
            static context => context.Request.Path.StartsWithSegments("/admin/invitations"),
            static branch =>
            {
                branch.UseMiddleware<AdminJwtAuthMiddleware>();
                branch.UseMiddleware<ProjectIdMiddleware>();
            });

        // Register through RequestDelegate so the route-handler analyzer does
        // not try to generate interceptors for a method group from another
        // assembly (it crashes intermittently: AD0001). The resolved arguments
        // mirror minimal API binding.
        RequestDelegate create = context => InvitationHandlers.CreateAsync(
            context,
            context.RequestServices.GetRequiredService<InvitationService>(),
            context.RequestServices.GetRequiredService<IManagementIdentity>());
        RequestDelegate get = context => InvitationHandlers.GetAsync(
            context,
            (string)context.Request.RouteValues["token"]!,
            context.RequestServices.GetRequiredService<InvitationService>());
        RequestDelegate register = context => InvitationHandlers.RegisterAsync(
            context,
            (string)context.Request.RouteValues["token"]!,
            context.RequestServices.GetRequiredService<InvitationService>(),
            context.RequestServices.GetRequiredService<ManagementAuthService>());

        _app.MapPost("/admin/invitations", create);
        _app.MapGet("/auth/invitations/{token}", get);
        _app.MapPost("/auth/invitations/{token}/register", register);

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    // ---------------- POST /admin/invitations ----------------

    [Fact]
    public async Task CreateRequiresTheJwtGate()
    {
        var (status, body) = await PostJsonAsync(
            "/admin/invitations",
            new { roleID = 1 },
            projectId: "gid://axonhub/Project/1");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("API key is required", Message(body));
    }

    [Fact]
    public async Task CreateRejectsANonBearerAuthorizationHeader()
    {
        var (status, body) = await PostJsonAsync(
            "/admin/invitations",
            new { roleID = 1 },
            authorization: "Basic abc");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(
            "invalid token: Authorization header must start with 'Bearer '",
            Message(body));
    }

    [Fact]
    public async Task CreateRequiresTheProjectHeader()
    {
        var owner = await SeedOwnerAsync();

        var (status, body) = await PostJsonAsync(
            "/admin/invitations",
            new { roleID = 1 },
            token: IssueToken(owner.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("X-Project-ID header is required", Message(body));
    }

    [Theory]
    [InlineData("""{"roleID":0}""")]
    [InlineData("""{"roleID":null}""")]
    [InlineData("""{}""")]
    [InlineData("{")]
    public async Task CreateRejectsAnInvalidBody(string rawBody)
    {
        var owner = await SeedOwnerAsync();
        var (project, _) = await SeedProjectAsync("project", "write_requests");

        var (status, body) = await PostRawAsync(
            "/admin/invitations",
            rawBody,
            token: IssueToken(owner.Id),
            projectId: ProjectGid(project.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid invitation request", Message(body));
    }

    [Fact]
    public async Task CreateReturnsTheInvitationResponseShape()
    {
        var owner = await SeedOwnerAsync();
        var (project, role) = await SeedProjectAsync("shape-project", "write_requests");

        var (status, body) = await PostJsonAsync(
            "/admin/invitations",
            new { roleID = role.Id, maxUses = 1, expiresInHours = 2 },
            token: IssueToken(owner.Id),
            projectId: ProjectGid(project.Id));

        Assert.Equal(HttpStatusCode.OK, status);

        var token = body.GetProperty("token").GetString()!;
        Assert.Equal(8, token.Length);
        Assert.Equal("shape-project", body.GetProperty("projectName").GetString());
        Assert.Equal(1, body.GetProperty("maxUses").GetInt64());
        Assert.Equal(0, body.GetProperty("usedCount").GetInt64());
        Assert.Equal(1, body.GetProperty("remainingUses").GetInt64());

        var expiresAt = body.GetProperty("expiresAt").GetString()!;
        var parsed = DateTimeOffset.Parse(
            expiresAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        Assert.InRange(
            parsed,
            DateTimeOffset.UtcNow.AddHours(1.9),
            DateTimeOffset.UtcNow.AddHours(2.1));
    }

    [Fact]
    public async Task CreateSerializesANeverExpiringInvitationAsNull()
    {
        var owner = await SeedOwnerAsync();
        var (project, role) = await SeedProjectAsync("project", "write_requests");

        var (status, body) = await PostJsonAsync(
            "/admin/invitations",
            new { roleID = role.Id, expiresInHours = 0, maxUses = 0 },
            token: IssueToken(owner.Id),
            projectId: ProjectGid(project.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("expiresAt").ValueKind);
        Assert.Equal(0, body.GetProperty("maxUses").GetInt64());
        Assert.Equal(0, body.GetProperty("remainingUses").GetInt64());
    }

    [Fact]
    public async Task CreateMapsServiceErrorsToForbidden()
    {
        var owner = await SeedOwnerAsync();
        var (project, _) = await SeedProjectAsync("project", "write_requests");

        var (status, body) = await PostJsonAsync(
            "/admin/invitations",
            new { roleID = 999, maxUses = 1 },
            token: IssueToken(owner.Id),
            projectId: ProjectGid(project.Id));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("project role not found", Message(body));
    }

    [Fact]
    public async Task CreateRejectsAnInvalidProjectGid()
    {
        var owner = await SeedOwnerAsync();

        var (status, body) = await PostJsonAsync(
            "/admin/invitations",
            new { roleID = 1 },
            token: IssueToken(owner.Id),
            projectId: "not-a-gid");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Invalid project ID", Message(body));
    }

    // ---------------- GET /auth/invitations/{token} ----------------

    [Fact]
    public async Task GetReturnsTheMetadataWithoutTheToken()
    {
        var owner = await SeedOwnerAsync();
        var (project, role) = await SeedProjectAsync("lookup-project", "write_requests");
        var (createStatus, created) = await PostJsonAsync(
            "/admin/invitations",
            new { roleID = role.Id, maxUses = 1, expiresInHours = 3 },
            token: IssueToken(owner.Id),
            projectId: ProjectGid(project.Id));
        Assert.Equal(HttpStatusCode.OK, createStatus);
        var token = created.GetProperty("token").GetString()!;

        var (status, body) = await GetAsync($"/auth/invitations/{token}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.TryGetProperty("token", out _));
        Assert.Equal("lookup-project", body.GetProperty("projectName").GetString());
        Assert.Equal(
            created.GetProperty("expiresAt").GetString(),
            body.GetProperty("expiresAt").GetString());
        Assert.Equal(1, body.GetProperty("maxUses").GetInt64());
        Assert.Equal(0, body.GetProperty("usedCount").GetInt64());
        Assert.Equal(1, body.GetProperty("remainingUses").GetInt64());
    }

    [Fact]
    public async Task GetReportsAnUnknownTokenAsNotFound()
    {
        var (status, body) = await GetAsync("/auth/invitations/unknown-token");

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("invitation not found", Message(body));
    }

    // ---------------- POST /auth/invitations/{token}/register ----------------

    [Fact]
    public async Task RegisterCreatesTheUserAndReturnsASession()
    {
        var (token, projectId) = await SeedInvitationAsync();

        var (status, body) = await PostJsonAsync(
            $"/auth/invitations/{token}/register",
            new
            {
                email = "member@example.com",
                password = "password",
                firstName = "Member",
                lastName = "User",
            });

        Assert.Equal(HttpStatusCode.OK, status);

        var user = body.GetProperty("user");
        Assert.Equal("User", user.GetProperty("id").GetProperty("type").GetString());
        Assert.Equal("member@example.com", user.GetProperty("email").GetString());
        Assert.Equal("Member", user.GetProperty("firstName").GetString());
        Assert.Equal("User", user.GetProperty("lastName").GetString());
        Assert.True(user.GetProperty("hasPassword").GetBoolean());

        var project = Assert.Single(user.GetProperty("projects").EnumerateArray());
        Assert.Equal("Project", project.GetProperty("projectID").GetProperty("type").GetString());
        Assert.Equal(projectId, project.GetProperty("projectID").GetProperty("id").GetInt64());
        Assert.Contains(
            "write_requests",
            project.GetProperty("effectiveScopes").EnumerateArray()
                .Select(static scope => scope.GetString()!));
        Assert.Equal(
            "Developer",
            Assert.Single(project.GetProperty("roles").EnumerateArray())
                .GetProperty("name").GetString());
        Assert.Empty(user.GetProperty("roles").EnumerateArray());
        Assert.Empty(user.GetProperty("oidcIdentities").EnumerateArray());

        var session = body.GetProperty("token").GetString()!;
        var claims = JwtTokenCodec.Decode(session, Secret, DateTimeOffset.UtcNow);
        Assert.Equal(
            user.GetProperty("id").GetProperty("id").GetInt64(),
            (long)claims.UserId!.Value);
    }

    [Theory]
    [InlineData("""{"email":"member@example.com","password":"short"}""")]
    [InlineData("""{"email":"not-an-email","password":"password"}""")]
    [InlineData("""{"password":"password"}""")]
    [InlineData("""{"email":"member@example.com"}""")]
    [InlineData("{")]
    public async Task RegisterRejectsAnInvalidBody(string rawBody)
    {
        var (token, _) = await SeedInvitationAsync();

        var (status, body) = await PostRawAsync(
            $"/auth/invitations/{token}/register",
            rawBody);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid registration request", Message(body));
    }

    [Fact]
    public async Task RegisterCountsThePasswordMinimumInRunes()
    {
        // binding:"required,min=7" counts runes: seven CJK runes pass even
        // though the service's byte check would also accept fewer.
        var (token, _) = await SeedInvitationAsync();

        var (status, body) = await PostJsonAsync(
            $"/auth/invitations/{token}/register",
            new
            {
                email = "member@example.com",
                password = "密码密码密码密",
                firstName = "",
                lastName = "",
            });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("member@example.com", body.GetProperty("user").GetProperty("email").GetString());
    }

    [Fact]
    public async Task RegisterMapsServiceErrorsToBadRequest()
    {
        var (status, body) = await PostJsonAsync(
            "/auth/invitations/unknown-token/register",
            new
            {
                email = "member@example.com",
                password = "password",
                firstName = "Member",
                lastName = "User",
            });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invitation not found", Message(body));
    }

    [Fact]
    public async Task RegisterMapsAMissingSecretToAFailedSession()
    {
        var (token, _) = await SeedInvitationAsync();
        // The fixture seeds the system secret for the gate; drop it so the
        // post-registration session creation fails like a fresh install.
        _systems.Rows.RemoveAll(row => row.Key == ManagementAuthService.SecretKeyKey);

        var (status, body) = await PostJsonAsync(
            $"/auth/invitations/{token}/register",
            new
            {
                email = "member@example.com",
                password = "password",
                firstName = "Member",
                lastName = "User",
            });

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("failed to create session", Message(body));
    }

    // ---------------- helpers ----------------

    private static string ProjectGid(long projectId) => $"gid://axonhub/Project/{projectId}";

    private static string Message(JsonElement body)
        => body.GetProperty("error").GetProperty("message").GetString()!;

    private static string IssueToken(long userId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return JwtTokenCodec.EncodeHs256(Secret, userId, now, now, now + 3600);
    }

    private async Task<UserRecord> SeedOwnerAsync()
    {
        var owner = _users.Add(row =>
        {
            row.Id = 11;
            row.Email = "owner@example.com";
            row.IsOwner = true;
        });

        await _adminUsers.CreateAsync(new UserRecord
        {
            Id = owner.Id,
            Email = owner.Email,
            Status = "activated",
            IsOwner = true,
        }, null);

        return owner;
    }

    private async Task<(ProjectRecord Project, RoleRecord Role)> SeedProjectAsync(
        string name,
        params string[] roleScopes)
    {
        var project = new ProjectRecord
        {
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Name = name,
            Status = "active",
        };
        await _projects.CreateAsync(project);
        _invitations.ProjectNames[project.Id] = name;

        var role = new RoleRecord
        {
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Name = "Developer",
            Level = "project",
            ProjectId = project.Id,
            Scopes = JsonSerializer.Serialize(roleScopes),
        };
        await _roles.CreateAsync(role);
        return (project, role);
    }

    /// <summary>Creates an invitation straight through the fake store (owner project).</summary>
    private async Task<(string Token, long ProjectId)> SeedInvitationAsync()
    {
        var (project, role) = await SeedProjectAsync("register-project", "write_requests");
        var token = InvitationService.GenerateInvitationToken();
        await _invitations.CreateAsync(new InvitationRecord
        {
            TokenHash = InvitationService.HashInvitationToken(token),
            ProjectId = project.Id,
            RoleId = role.Id,
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            MaxUses = 1,
        });

        return (token, project.Id);
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> PostJsonAsync(
        string path,
        object body,
        string? token = null,
        string? projectId = null,
        string? authorization = null)
        => SendAsync(
            HttpMethod.Post,
            path,
            JsonSerializer.Serialize(body),
            token,
            projectId,
            authorization);

    private Task<(HttpStatusCode Status, JsonElement Body)> PostRawAsync(
        string path,
        string rawBody,
        string? token = null,
        string? projectId = null)
        => SendAsync(HttpMethod.Post, path, rawBody, token, projectId, authorization: null);

    private async Task<(HttpStatusCode Status, JsonElement Body)> GetAsync(string path)
        => await SendAsync(HttpMethod.Get, path, body: null, token: null, projectId: null, authorization: null);

    private async Task<(HttpStatusCode Status, JsonElement Body)> SendAsync(
        HttpMethod method,
        string path,
        string? body,
        string? token,
        string? projectId,
        string? authorization)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (projectId is not null)
        {
            request.Headers.Add("X-Project-ID", projectId);
        }

        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return (response.StatusCode, document.RootElement.Clone());
    }
}
