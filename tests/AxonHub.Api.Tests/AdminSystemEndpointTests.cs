using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Management;
using AxonHub.Api.Settings;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the unauthenticated system routes (routes.go:84,86,94-95): the /health
/// envelope with build.Info's json tags, GET /admin/system/status, and
/// POST /admin/system/initialize with gin's binding rules, the
/// InitializeSystemResponse shape and the 400/500 classification.
/// </summary>
public sealed class AdminSystemEndpointTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly FakeSystemStore _systems = new();
    private readonly FakeUserStore _users = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeProjectStore _projects = new();
    private readonly FakeDataStorageStore _storages = new();
    private readonly FakeUserAdminStore _adminUsers = new();
    private readonly FakePasswordHasher _hasher = new();
    private readonly FakeVersionService _versions = new();
    private readonly UtcClock _clock = new(Now);

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _adminUsers.RolesSource = _roles;

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IUserStore>(_users);
        builder.Services.AddSingleton<ISystemStore>(_systems);
        builder.Services.AddSingleton<IProjectStore>(_projects);
        builder.Services.AddSingleton<IRoleStore>(_roles);
        builder.Services.AddSingleton<IDataStorageStore>(_storages);
        builder.Services.AddSingleton<IUserAdminStore>(_adminUsers);
        builder.Services.AddSingleton<IPasswordHasher>(_hasher);
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton<ISystemVersionService>(_versions);
        builder.Services.AddSingleton<ISystemSettingsAdminService, SystemSettingsAdminService>();
        builder.Services.AddSingleton<IManagementIdentity>(new FakeManagementIdentity(null));
        builder.Services.AddScoped<PermissionValidator>();
        builder.Services.AddScoped<ProjectManagementService>();
        builder.Services.AddScoped<ISystemInitializationService, SystemInitializationService>();

        _app = builder.Build();

        // Register through RequestDelegate so the route-handler analyzer does
        // not try to generate interceptors for a method group from another
        // assembly (AD0001). The resolved arguments mirror minimal API binding.
        RequestDelegate health = context => SystemHandlers.HealthAsync(
            context,
            context.RequestServices.GetRequiredService<ISystemVersionService>(),
            context.RequestServices.GetRequiredService<TimeProvider>(),
            context.RequestAborted);
        RequestDelegate status = context => SystemHandlers.GetSystemStatusAsync(
            context,
            context.RequestServices.GetRequiredService<ISystemInitializationService>(),
            context.RequestAborted);
        RequestDelegate initialize = context => SystemHandlers.InitializeSystemAsync(
            context,
            context.RequestServices.GetRequiredService<ISystemInitializationService>(),
            context.RequestAborted);

        _app.MapGet("/health", health);
        _app.MapGet("/admin/system/status", status);
        _app.MapPost("/admin/system/initialize", initialize);

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    // ---------------- GET /health ----------------

    [Fact]
    public async Task HealthReturnsTheReferenceEnvelope()
    {
        _versions.Info = new SystemBuildInfo
        {
            Version = "v1.0.0-beta9",
            GoVersion = ".NET 10.0.0",
            Platform = "windows/amd64",
            Uptime = "1h2m3.5s",
        };

        var (status, body, _) = await GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("healthy", body.GetProperty("status").GetString());
        Assert.Equal(
            "2026-01-02T03:04:05Z",
            body.GetProperty("timestamp").GetString());
        Assert.Equal("v1.0.0-beta9", body.GetProperty("version").GetString());
        Assert.Equal("1h2m3.5s", body.GetProperty("uptime").GetString());

        var build = body.GetProperty("build");
        Assert.Equal(
            ["version", "go_version", "platform", "uptime"],
            build.EnumerateObject().Select(static property => property.Name));
        Assert.Equal("v1.0.0-beta9", build.GetProperty("version").GetString());
        Assert.Equal(".NET 10.0.0", build.GetProperty("go_version").GetString());
        Assert.Equal("windows/amd64", build.GetProperty("platform").GetString());
        Assert.Equal("1h2m3.5s", build.GetProperty("uptime").GetString());
    }

    [Fact]
    public async Task HealthEmitsCommitAndBuildTimeWhenTheBuildSetsThem()
    {
        _versions.Info = new SystemBuildInfo
        {
            Version = "v1.0.0-beta9",
            Commit = "abc123",
            BuildTime = "2026-01-01T00:00:00Z",
            GoVersion = "go1.26.0",
            Platform = "linux/arm64",
            Uptime = "2s",
        };

        var (status, body, _) = await GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, status);
        var build = body.GetProperty("build");
        Assert.Equal(
            ["version", "commit", "build_time", "go_version", "platform", "uptime"],
            build.EnumerateObject().Select(static property => property.Name));
        Assert.Equal("abc123", build.GetProperty("commit").GetString());
        Assert.Equal(
            "2026-01-01T00:00:00Z",
            build.GetProperty("build_time").GetString());
    }

    // ---------------- GET /admin/system/status ----------------

    [Fact]
    public async Task StatusReportsFalseBeforeInitialization()
    {
        var (status, body, _) = await GetAsync("/admin/system/status");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.False(body.GetProperty("isInitialized").GetBoolean());
        Assert.Single(body.EnumerateObject());
    }

    [Fact]
    public async Task StatusComparesTheFlagCaseInsensitively()
    {
        _systems.Add(SystemSettingsAdminService.InitializedKey, "True");

        var (status, body, _) = await GetAsync("/admin/system/status");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("isInitialized").GetBoolean());
    }

    [Fact]
    public async Task StatusReadFailureReturnsTheGinErrorEnvelope()
    {
        _systems.GetFailure = new InvalidOperationException("db down");

        var (status, body, _) = await GetAsync("/admin/system/status");

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        var error = body.GetProperty("error");
        Assert.Equal("Internal Server Error", error.GetProperty("type").GetString());
        Assert.Equal("Failed to check system status", error.GetProperty("message").GetString());
    }

    // ---------------- POST /admin/system/initialize ----------------

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("null")]
    public async Task InitializeRejectsAnUnparseableBodyWithTheInitializeEnvelope(string json)
    {
        var (status, body, _) = await PostRawAsync("/admin/system/initialize", json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("Invalid request format", body.GetProperty("message").GetString());
        Assert.False(body.TryGetProperty("error", out _));
        Assert.Empty(_users.Rows);
    }

    [Theory]
    [InlineData("""{"ownerPassword":"secret1","ownerFirstName":"F","ownerLastName":"L","brandName":"B"}""")]
    [InlineData("""{"ownerEmail":"not-an-email","ownerPassword":"secret1","ownerFirstName":"F","ownerLastName":"L","brandName":"B"}""")]
    [InlineData("""{"ownerEmail":"owner@example.com","ownerPassword":"12345","ownerFirstName":"F","ownerLastName":"L","brandName":"B"}""")]
    [InlineData("""{"ownerEmail":"owner@example.com","ownerPassword":"secret1","ownerLastName":"L","brandName":"B"}""")]
    [InlineData("""{"ownerEmail":"owner@example.com","ownerPassword":"secret1","ownerFirstName":"F","brandName":"B"}""")]
    [InlineData("""{"ownerEmail":"owner@example.com","ownerPassword":"secret1","ownerFirstName":"F","ownerLastName":"L"}""")]
    public async Task InitializeRejectsGinsBindingFailures(string json)
    {
        var (status, body, _) = await PostRawAsync("/admin/system/initialize", json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("Invalid request format", body.GetProperty("message").GetString());
        Assert.Empty(_users.Rows);
        Assert.Empty(_storages.Rows);
    }

    [Fact]
    public async Task InitializeCountsPasswordRunesLikeTheValidator()
    {
        // Six runes, eighteen UTF-8 bytes: go-playground's min passes.
        var (status, body, raw) = await PostRawAsync(
            "/admin/system/initialize",
            """
            {"ownerEmail":"owner@example.com","ownerPassword":"密码密码密码",
             "ownerFirstName":"F","ownerLastName":"L","brandName":"B"}
            """);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Contains("System initialized successfully", raw);
        Assert.Equal("密码密码密码", Assert.Single(_hasher.HashedPasswords));
    }

    [Fact]
    public async Task InitializeRejectsAnAlreadyInitializedSystem()
    {
        _systems.Add(SystemSettingsAdminService.InitializedKey, "true");

        var (status, body, _) = await PostJsonAsync(
            "/admin/system/initialize",
            ValidRequest());

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("System is already initialized", body.GetProperty("message").GetString());
        Assert.Empty(_users.Rows);
    }

    [Fact]
    public async Task InitializeCreatesTheSystem()
    {
        var (status, body, raw) = await PostJsonAsync(
            "/admin/system/initialize",
            ValidRequest());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            """{"success":true,"message":"System initialized successfully"}""",
            raw);

        var user = Assert.Single(_users.Rows);
        Assert.Equal("owner@example.com", user.Email);
        Assert.Equal("First", user.FirstName);
        Assert.Equal("Last", user.LastName);
        Assert.Equal("en", user.PreferLanguage);
        Assert.True(user.IsOwner);
        Assert.Equal("""["*"]""", user.Scopes);
        Assert.Equal("hashed", user.Password);

        Assert.Equal("Default", Assert.Single(_projects.Rows).Name);
        Assert.Equal(
            ["Admin", "Developer", "Viewer"],
            _roles.Rows.Select(static row => row.Name));
        Assert.Single(_projects.ProjectUsers);

        var storage = Assert.Single(_storages.Rows);
        Assert.Equal("Primary", storage.Name);
        Assert.True(storage.Primary);

        Assert.Equal("AxonHub", Value(SystemInitializationService.BrandNameKey));
        Assert.Equal("true", Value(SystemSettingsAdminService.InitializedKey));
        Assert.Equal(
            storage.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Value(SystemSettingsAdminService.DefaultDataStorageIdKey));
        Assert.Equal(
            SystemVersionService.Version,
            Value(SystemInitializationService.VersionKey));
        Assert.Equal(
            64,
            Value(ManagementAuthService.SecretKeyKey).Length);
    }

    [Fact]
    public async Task InitializeStatusReadFailureReturnsTheGinErrorEnvelope()
    {
        _systems.GetFailure = new InvalidOperationException("db down");

        var (status, body, _) = await PostJsonAsync(
            "/admin/system/initialize",
            ValidRequest());

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        var error = body.GetProperty("error");
        Assert.Equal("Internal Server Error", error.GetProperty("type").GetString());
        Assert.Equal(
            "Failed to check initialization status",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task InitializeServiceFailureReturnsTheInitializeEnvelope()
    {
        _storages.CreateFailure = new StoreFailureException(
            "boom",
            new InvalidOperationException("boom"));

        var (status, body, _) = await PostJsonAsync(
            "/admin/system/initialize",
            ValidRequest());

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(
            "Failed to initialize system: failed to create primary data storage: boom",
            body.GetProperty("message").GetString());
    }

    private static object ValidRequest() => new
    {
        ownerEmail = "owner@example.com",
        ownerPassword = "secret1",
        ownerFirstName = "First",
        ownerLastName = "Last",
        brandName = "AxonHub",
    };

    private string Value(string key)
        => Assert.Single(_systems.Rows, row => row.Key == key).Value;

    private async Task<(HttpStatusCode Status, JsonElement Body, string Raw)> GetAsync(
        string path)
    {
        using var response = await _client.GetAsync(path);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(raw);
        return (response.StatusCode, document.RootElement.Clone(), raw);
    }

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

    /// <summary>Fixed clock with a UTC local zone, so the health timestamp is exact.</summary>
    private sealed class UtcClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    /// <summary>Fixed build metadata for the health envelope.</summary>
    private sealed class FakeVersionService : ISystemVersionService
    {
        public SystemBuildInfo Info { get; set; } = new();

        public SystemBuildInfo GetBuildInfo() => Info;

        public Task<VersionCheckResult> CheckForUpdateAsync(
            bool includeBeta,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
