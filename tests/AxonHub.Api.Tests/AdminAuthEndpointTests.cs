using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the unauthenticated admin auth endpoints (routes.go:97-98) against
/// api/auth.go: the signin payload (objects.UserInfo JSON shape, GUID objects,
/// token), gin's binding errors, and the refresh status/error classification
/// (200 rotate / 204 keep / 401 invalid / 500 internal).
/// </summary>
public sealed class AdminAuthEndpointTests : IAsyncLifetime
{
    private const string Secret = "test-secret";

    private readonly FakeUserStore _users = new();
    private readonly FakeSystemStore _systems = new();
    private readonly FakeUserProfileStore _profiles = new();
    private readonly FakePasswordHasher _passwords = new();
    private readonly FakeOidcLoginOnlyProviders _oidc = new();
    private readonly RecordingLogger<ManagementAuthService> _logger = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IUserStore>(_users);
        builder.Services.AddSingleton<ISystemStore>(_systems);
        builder.Services.AddSingleton<IUserProfileStore>(_profiles);
        builder.Services.AddSingleton<IPasswordHasher>(_passwords);
        builder.Services.AddSingleton<IOidcLoginOnlyProviders>(_oidc);
        builder.Services.AddSingleton<ILogger<ManagementAuthService>>(_logger);
        builder.Services.AddScoped<ManagementAuthService>();

        _app = builder.Build();

        // Register through RequestDelegate so the route-handler analyzer does
        // not try to generate interceptors for a method group from another
        // assembly (it crashes intermittently: AD0001). The resolved arguments
        // mirror minimal API binding: body/header from HttpContext, the scoped
        // service from RequestServices and RequestAborted for cancellation.
        RequestDelegate signIn = context => ManagementAuthHandlers.SignInAsync(
            context,
            context.RequestServices.GetRequiredService<ManagementAuthService>(),
            context.RequestAborted);
        RequestDelegate refresh = context => ManagementAuthHandlers.RefreshAsync(
            context,
            context.RequestServices.GetRequiredService<ManagementAuthService>(),
            context.RequestServices.GetRequiredService<ILogger<ManagementAuthService>>(),
            context.RequestAborted);
        _app.MapPost("/admin/auth/signin", signIn);
        _app.MapPost("/admin/auth/refresh", refresh);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task SignInReturnsTheUserInfoShapeAndAToken()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = NewUser();
        user.Scopes = """["direct"]""";
        _profiles.Roles.Add(NewRole(1, "System", projectId: 0, """["system"]"""));

        var (status, body, _) = await PostJsonAsync(
            "/admin/auth/signin",
            new { email = "user@example.com", password = "s3cret" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Object, body.GetProperty("user").ValueKind);

        var payload = body.GetProperty("user");
        var id = payload.GetProperty("id");
        Assert.Equal("User", id.GetProperty("type").GetString());
        Assert.Equal(user.Id, id.GetProperty("id").GetInt64());
        Assert.Equal("user@example.com", payload.GetProperty("email").GetString());
        Assert.Equal("First", payload.GetProperty("firstName").GetString());
        Assert.Equal("Last", payload.GetProperty("lastName").GetString());
        Assert.False(payload.GetProperty("isOwner").GetBoolean());
        Assert.Equal("en", payload.GetProperty("preferLanguage").GetString());
        Assert.Equal("avatar.png", payload.GetProperty("avatar").GetString());
        Assert.True(payload.GetProperty("hasPassword").GetBoolean());
        Assert.Equal(
            ["direct", "system"],
            payload.GetProperty("scopes").EnumerateArray()
                .Select(static value => value.GetString()!)
                .ToArray());
        Assert.Equal(
            ["System"],
            payload.GetProperty("roles").EnumerateArray()
                .Select(static role => role.GetProperty("name").GetString()!)
                .ToArray());
        Assert.Empty(payload.GetProperty("projects").EnumerateArray());
        Assert.Empty(payload.GetProperty("oidcIdentities").EnumerateArray());

        var token = body.GetProperty("token").GetString()!;
        var claims = JwtTokenCodec.Decode(token, Secret, DateTimeOffset.UtcNow);
        Assert.Equal((double?)user.Id, claims.UserId);
    }

    [Fact]
    public async Task SignInSerializesANullAvatarAsAnEmptyString()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        NewUser(avatar: null);

        var (status, body, _) = await PostJsonAsync(
            "/admin/auth/signin",
            new { email = "user@example.com", password = "s3cret" });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            string.Empty,
            body.GetProperty("user").GetProperty("avatar").GetString());
    }

    [Fact]
    public async Task SignInRejectsAnInvalidBodyWithGinsBindingError()
    {
        var (status, body, _) = await PostRawAsync("/admin/auth/signin", "{");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Bad Request", body.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(
            "Invalid request format",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Theory]
    [InlineData("", "s3cret")]
    [InlineData("not-an-email", "s3cret")]
    [InlineData("user@example.com", "")]
    public async Task SignInRejectsMissingOrMalformedFields(
        string email,
        string password)
    {
        var (status, body, _) = await PostJsonAsync(
            "/admin/auth/signin",
            new { email, password });

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(
            "Invalid request format",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task SignInRejectsAnUnknownEmailAsUnauthorized()
    {
        var (status, body, _) = await PostJsonAsync(
            "/admin/auth/signin",
            new { email = "nobody@example.com", password = "s3cret" });

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(
            "Invalid email or password",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task SignInRejectsAWrongPasswordAsUnauthorized()
    {
        NewUser();
        _passwords.VerifyError = new InvalidOperationException("bcrypt mismatch");

        var (status, body, _) = await PostJsonAsync(
            "/admin/auth/signin",
            new { email = "user@example.com", password = "wrong" });

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(
            "Invalid email or password",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task SignInMapsTheOidcOnlyGuardToInternalServerError()
    {
        // ErrOIDCLoginRequired is neither ErrInvalidPassword nor a token error,
        // so AuthHandlers.SignIn falls into its generic 500 branch.
        NewUser(password: PasswordHasher.OidcOnlyPlaceholder);

        var (status, body, _) = await PostJsonAsync(
            "/admin/auth/signin",
            new { email = "user@example.com", password = "anything" });

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal(
            "Internal server error",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task SignInMapsAMissingSecretKeyToInternalServerError()
    {
        NewUser();

        var (status, body, _) = await PostJsonAsync(
            "/admin/auth/signin",
            new { email = "user@example.com", password = "s3cret" });

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal(
            "Internal server error",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task SignInMapsAUserStoreFailureToInternalServerError()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        _users.GetActivatedByEmailFailure = new InvalidOperationException("db down");

        var (status, body, _) = await PostJsonAsync(
            "/admin/auth/signin",
            new { email = "user@example.com", password = "s3cret" });

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal(
            "Internal server error",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task RefreshRejectsAMissingOrMalformedAuthorizationHeader()
    {
        var missing = await PostRefreshAsync(null);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.Status);
        Assert.Equal(
            "Invalid authorization token",
            missing.Body.GetProperty("error").GetProperty("message").GetString());

        var basic = await PostRefreshAsync("Basic abc");
        Assert.Equal(HttpStatusCode.Unauthorized, basic.Status);

        var empty = await PostRefreshAsync("Bearer ");
        Assert.Equal(HttpStatusCode.Unauthorized, empty.Status);
    }

    [Fact]
    public async Task RefreshRejectsAnInvalidToken()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);

        var (status, body) = await PostRefreshAsync("Bearer not-a-jwt");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(
            "Invalid authorization token",
            body.GetProperty("error").GetProperty("message").GetString());
        Assert.Contains(
            _logger.Entries,
            entry => entry.Level == LogLevel.Information
                && entry.Message.Contains("refresh rejected invalid JWT", StringComparison.Ordinal));
        Assert.DoesNotContain(
            _logger.Entries,
            entry => entry.Message.Contains("not-a-jwt", StringComparison.Ordinal));
        Assert.All(_logger.Entries, entry => Assert.Null(entry.Exception));
    }

    [Fact]
    public async Task RefreshReturnsNoContentWhileTheTokenIsYoung()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = NewUser();
        var auth = DateTimeOffset.UtcNow.AddDays(-10);
        var token = await CreateService().GenerateJwtTokenAtAsync(user, auth, auth);

        var (status, _) = await PostRefreshAsync("Bearer " + token);

        Assert.Equal(HttpStatusCode.NoContent, status);
    }

    [Fact]
    public async Task RefreshRotatesATokenInsideTheRenewalWindow()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = NewUser();
        var auth = DateTimeOffset.UtcNow.AddDays(-25);
        var token = await CreateService().GenerateJwtTokenAtAsync(user, auth, auth);

        var (status, body) = await PostRefreshAsync("Bearer " + token);

        Assert.Equal(HttpStatusCode.OK, status);
        var refreshed = body.GetProperty("token").GetString()!;
        Assert.NotEqual(token, refreshed);

        var claims = JwtTokenCodec.Decode(refreshed, Secret, DateTimeOffset.UtcNow);
        Assert.Equal((double?)user.Id, claims.UserId);
        Assert.Equal(auth.ToUnixTimeSeconds(), claims.AuthTime);
    }

    [Fact]
    public async Task RefreshMapsAMissingSecretKeyToInternalServerError()
    {
        var (status, body) = await PostRefreshAsync("Bearer token");

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal(
            "Internal server error",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task RefreshMapsAUserStoreFailureToInternalServerError()
    {
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);
        var user = NewUser();
        _users.GetByIdFailure = new InvalidOperationException("db down");
        var auth = DateTimeOffset.UtcNow.AddDays(-25);
        var token = JwtTokenCodec.EncodeHs256(
            Secret,
            user.Id,
            auth.ToUnixTimeSeconds(),
            auth.ToUnixTimeSeconds(),
            auth.AddDays(30).ToUnixTimeSeconds());

        var (status, body) = await PostRefreshAsync("Bearer " + token);

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal(
            "Internal server error",
            body.GetProperty("error").GetProperty("message").GetString());
    }

    private ManagementAuthService CreateService()
        => new(_users, _systems, _profiles, _passwords, _oidc);

    private UserRecord NewUser(
        string password = "stored-hash",
        string? avatar = "avatar.png") => _users.Add(user =>
        {
            user.Email = "user@example.com";
            user.Password = password;
            user.Status = "activated";
            user.FirstName = "First";
            user.LastName = "Last";
            user.Avatar = avatar;
            user.IsOwner = false;
        });

    private static RoleRecord NewRole(long id, string name, long? projectId, string scopes) => new()
    {
        Id = id,
        Name = name,
        ProjectId = projectId,
        Scopes = scopes,
    };

    private async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> PostJsonAsync(
        string path,
        object payload)
        => await PostRawAsync(path, JsonSerializer.Serialize(payload));

    private async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> PostRawAsync(
        string path,
        string json)
    {
        using var response = await _client.PostAsync(
            path,
            new StringContent(json, Encoding.UTF8, "application/json"));
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var document = JsonDocument.Parse(raw);
        return (response.StatusCode, document.RootElement.Clone(), raw);
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostRefreshAsync(
        string? authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/auth/refresh");
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        using var response = await _client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        if (raw.Length == 0)
        {
            return (response.StatusCode, default);
        }

        using var document = JsonDocument.Parse(raw);
        return (response.StatusCode, document.RootElement.Clone());
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _ = eventId;
            Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
