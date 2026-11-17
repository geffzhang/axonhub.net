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
/// Locks the me.graphql wire surface over /admin/graphql against the reference
/// SDL (me.graphql) and resolvers (me.resolvers.go): gid IDs, RFC3339Nano
/// times, the ProjectStatus enum, the User payload of updateMe, and the exact
/// error strings.
/// </summary>
public sealed class AdminMeEndpointTests : IAsyncLifetime
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private readonly FakeUserProfileStore _store = new();
    private readonly FakePasswordHasher _passwords = new();
    private readonly AdminGraphqlGateServices _gate = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IUserProfileStore>(_store);
        builder.Services.AddSingleton<IPasswordHasher>(_passwords);
        builder.Services.AddSingleton<UserProfileService>();

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
    public async Task MeReturnsTheReferenceShape()
    {
        _store.User = NewUser();
        _store.Roles.Add(NewRole(1, "Viewer", projectId: 0, "system", """["system:read"]"""));
        _store.Roles.Add(NewRole(2, "Member", projectId: 7, "project", """["p:read"]"""));
        _store.UserProjects.Add(new UserProjectRecord
        {
            Id = 11,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            IsOwner = true,
            Scopes = """["p:write"]""",
            UserId = 9,
            ProjectId = 7,
        });
        _store.OidcIdentities.Add(new OidcIdentityRecord
        {
            Id = 5,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Issuer = "https://issuer.example.com",
            Subject = "subject-5",
            Email = "oidc@example.com",
            IdpName = "Example",
            UserId = 9,
        });

        var data = await QueryDataAsync(
            """
            query {
              me {
                id email firstName lastName isOwner preferLanguage avatar
                scopes
                roles { name }
                projects { projectID isOwner scopes effectiveScopes roles { name } }
                oidcIdentities { id idpName issuer subject email }
                hasPassword
              }
            }
            """);

        var me = data.GetProperty("me");
        Assert.Equal("gid://axonhub/User/9", me.GetProperty("id").GetString());
        Assert.Equal("me@example.com", me.GetProperty("email").GetString());
        Assert.Equal("John", me.GetProperty("firstName").GetString());
        Assert.Equal("Doe", me.GetProperty("lastName").GetString());
        Assert.True(me.GetProperty("isOwner").GetBoolean());
        Assert.Equal("en", me.GetProperty("preferLanguage").GetString());
        Assert.Equal("avatar.png", me.GetProperty("avatar").GetString());
        Assert.True(me.GetProperty("hasPassword").GetBoolean());

        // UserInfo.scopes is the user's own scopes plus system-role scopes;
        // project membership scopes only appear on each UserProjectInfo.
        Assert.Equal(
            ["system:read"],
            me.GetProperty("scopes").EnumerateArray()
                .Select(static value => value.GetString()!)
                .ToArray());
        Assert.Equal(
            ["Viewer"],
            me.GetProperty("roles").EnumerateArray()
                .Select(static role => role.GetProperty("name").GetString()!)
                .ToArray());

        var project = Assert.Single(me.GetProperty("projects").EnumerateArray());
        Assert.Equal("gid://axonhub/Project/7", project.GetProperty("projectID").GetString());
        Assert.True(project.GetProperty("isOwner").GetBoolean());
        Assert.Equal(
            ["p:write"],
            project.GetProperty("scopes").EnumerateArray()
                .Select(static value => value.GetString()!)
                .ToArray());
        Assert.Equal(
            ["p:read", "p:write"],
            project.GetProperty("effectiveScopes").EnumerateArray()
                .Select(static value => value.GetString()!)
                .ToArray());
        Assert.Equal(
            ["Member"],
            project.GetProperty("roles").EnumerateArray()
                .Select(static role => role.GetProperty("name").GetString()!)
                .ToArray());

        var identity = Assert.Single(me.GetProperty("oidcIdentities").EnumerateArray());
        Assert.Equal(
            "gid://axonhub/OIDCIdentity/5",
            identity.GetProperty("id").GetString());
        Assert.Equal("Example", identity.GetProperty("idpName").GetString());
        Assert.Equal(
            "https://issuer.example.com",
            identity.GetProperty("issuer").GetString());
        Assert.Equal("subject-5", identity.GetProperty("subject").GetString());
        Assert.Equal("oidc@example.com", identity.GetProperty("email").GetString());
    }

    [Fact]
    public async Task MeSerializesAnAbsentAvatarAsEmptyString()
    {
        _store.User = NewUser(avatar: null);

        var data = await QueryDataAsync("{ me { avatar } }");

        Assert.Equal(
            string.Empty,
            data.GetProperty("me").GetProperty("avatar").GetString());
    }

    [Fact]
    public async Task MeReturnsUserNotFoundForAnApiKeyPrincipal()
    {
        // A service-account API key reaches the resolvers without a user
        // (contexts.GetUser is nil on an API-key request).
        _gate.AddServiceAccountKey();
        using var client = _app.GetTestClient();
        AdminGraphqlGateServices.Authorize(client, "sk-service-account");

        var errors = await QueryErrorsAsync(client, "{ me { id } }");

        Assert.Equal(
            "user not found in context",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task MyProjectsReturnsTheProjectScalarFields()
    {
        _store.ActiveProjects.Add(new ProjectRecord
        {
            Id = 7,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Name = "Alpha",
            Description = "First",
            Status = "active",
        });
        _store.ActiveProjects.Add(new ProjectRecord
        {
            Id = 8,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Name = "Beta",
            Description = string.Empty,
            Status = "archived",
        });

        var data = await QueryDataAsync(
            "{ myProjects { id createdAt updatedAt name description status } }");

        var projects = data.GetProperty("myProjects").EnumerateArray().ToArray();
        Assert.Equal(2, projects.Length);
        Assert.Equal("gid://axonhub/Project/7", projects[0].GetProperty("id").GetString());
        Assert.Equal("2026-01-02T03:04:05Z", projects[0].GetProperty("createdAt").GetString());
        Assert.Equal("2026-02-03T04:05:06Z", projects[0].GetProperty("updatedAt").GetString());
        Assert.Equal("Alpha", projects[0].GetProperty("name").GetString());
        Assert.Equal("First", projects[0].GetProperty("description").GetString());
        Assert.Equal("active", projects[0].GetProperty("status").GetString());
        Assert.Equal("archived", projects[1].GetProperty("status").GetString());
    }

    [Fact]
    public async Task UpdateMeReturnsTheFullUserPayloadAndForwardsTheInput()
    {
        _store.User = NewUser();
        _store.UpdateResult = NewUser(email: "updated@example.com", avatar: string.Empty);

        var data = await MutateAsync(
            """
            mutation Update($input: UpdateMeInput!) {
              updateMe(input: $input) {
                id email firstName lastName preferLanguage avatar isOwner
              }
            }
            """,
            new
            {
                input = new
                {
                    firstName = "Alice",
                    preferLanguage = "zh",
                    avatar = string.Empty,
                },
            });

        var user = data.GetProperty("updateMe");
        Assert.Equal("gid://axonhub/User/9", user.GetProperty("id").GetString());
        Assert.Equal("updated@example.com", user.GetProperty("email").GetString());
        Assert.Equal("John", user.GetProperty("firstName").GetString());
        Assert.Equal("Doe", user.GetProperty("lastName").GetString());
        Assert.Equal("en", user.GetProperty("preferLanguage").GetString());
        Assert.Equal(string.Empty, user.GetProperty("avatar").GetString());
        Assert.True(user.GetProperty("isOwner").GetBoolean());

        var write = Assert.IsType<OwnProfileUpdate>(_store.LastUpdate);
        Assert.Equal(9, write.Id);
        Assert.Equal("Alice", write.FirstName);
        Assert.Null(write.LastName);
        Assert.Equal("zh", write.PreferLanguage);
        Assert.Equal(string.Empty, write.Avatar);
    }

    [Fact]
    public async Task UpdateMyPasswordReturnsTrueAndStoresTheHash()
    {
        _store.User = NewUser(password: PasswordHasher.OidcOnlyPlaceholder);
        _store.UpdateResult = _store.User;
        _passwords.HashResult = "hashed:new";

        var data = await MutateAsync(
            """
            mutation Password($input: UpdateMyPasswordInput!) {
              updateMyPassword(input: $input)
            }
            """,
            new { input = new { newPassword = "new-secret" } });

        Assert.True(data.GetProperty("updateMyPassword").GetBoolean());
        Assert.Equal("hashed:new", _store.LastUpdate?.Password);
    }

    [Fact]
    public async Task UnlinkOIDCIdentityParsesTheGidAndReturnsTrue()
    {
        _store.User = NewUser();
        _store.OidcIdentity = new OidcIdentityRecord
        {
            Id = 5,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Issuer = "https://issuer.example.com",
            Subject = "subject-5",
            UserId = 9,
        };

        var data = await MutateAsync(
            """
            mutation Unlink($id: ID!) {
              unlinkOIDCIdentity(id: $id)
            }
            """,
            new { id = "gid://axonhub/OIDCIdentity/5" });

        Assert.True(data.GetProperty("unlinkOIDCIdentity").GetBoolean());
        Assert.Equal(5, _store.UnlinkedIdentityId);
    }

    [Fact]
    public async Task UnlinkOIDCIdentityOfAMissingRowReturnsTheEntError()
    {
        _store.User = NewUser();
        _store.OidcIdentity = null;

        var errors = await MutateErrorsAsync(
            """
            mutation Unlink($id: ID!) {
              unlinkOIDCIdentity(id: $id)
            }
            """,
            new { id = "gid://axonhub/OIDCIdentity/5" });

        Assert.Equal(
            "failed to get identity: ent: oidc_identity not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task UpdateMeWrapsStoreFailuresLikeTheResolver()
    {
        _store.User = NewUser();
        _store.UpdateError = new InvalidOperationException("db down");

        var errors = await MutateErrorsAsync(
            """
            mutation Update($input: UpdateMeInput!) {
              updateMe(input: $input) { id }
            }
            """,
            new { input = new { firstName = "Alice" } });

        Assert.Equal(
            "failed to update user profile: db down",
            errors[0].GetProperty("message").GetString());
    }

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null)
    {
        var root = await ExecuteAsync(query, variables);
        Assert.False(root.TryGetProperty("errors", out _), root.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(
        string query,
        object? variables = null)
        => await QueryErrorsAsync(_client, query, variables);

    private async Task<JsonElement[]> QueryErrorsAsync(
        HttpClient client,
        string query,
        object? variables = null)
    {
        var root = await ExecuteAsync(client, query, variables);
        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        return errors.EnumerateArray().Select(static error => error.Clone()).ToArray();
    }

    private async Task<JsonElement> MutateAsync(string query, object? variables)
        => await QueryDataAsync(query, variables);

    private async Task<JsonElement[]> MutateErrorsAsync(string query, object? variables)
        => await QueryErrorsAsync(query, variables);

    private async Task<JsonElement> ExecuteAsync(string query, object? variables)
        => await ExecuteAsync(_client, query, variables);

    private async Task<JsonElement> ExecuteAsync(
        HttpClient client,
        string query,
        object? variables)
    {
        var payload = JsonSerializer.Serialize(new { query, variables, operationName = (string?)null });
        using var response = await client.PostAsync(
            "/admin/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        AdminGraphqlHttpResponseAssertions.AssertStatus(response, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private static UserRecord NewUser(
        string email = "me@example.com",
        string password = "stored-hash",
        string? avatar = "avatar.png") => new()
        {
            Id = 9,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Email = email,
            Status = "activated",
            PreferLanguage = "en",
            Password = password,
            FirstName = "John",
            LastName = "Doe",
            Avatar = avatar,
            IsOwner = true,
            Scopes = """["system:read"]""",
        };

    private static RoleRecord NewRole(
        long id,
        string name,
        long? projectId,
        string level,
        string scopes) => new()
        {
            Id = id,
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            Name = name,
            Level = level,
            Scopes = scopes,
            ProjectId = projectId,
        };
}
