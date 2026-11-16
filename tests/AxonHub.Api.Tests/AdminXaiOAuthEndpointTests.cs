using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Xai.Subscription;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the REST surface of routes.go:117-119: the JWT-gated
/// POST /admin/xai/oauth/start, /exchange and /sso, their gin envelopes, and
/// the user-bound session semantics of api/xai.go.
/// </summary>
public sealed class AdminXaiOAuthEndpointTests : IAsyncLifetime
{
    private const string Secret = "test-secret";

    private readonly FakeUserStore _users = new();
    private readonly FakeSystemStore _systems = new();
    private readonly FakeUserProfileStore _profiles = new();
    private readonly FakePasswordHasher _passwords = new();
    private readonly FakeOidcLoginOnlyProviders _oidc = new();
    private readonly AntigravityTestClock _clock = new();
    private readonly ScriptedExecutor _executor = new();
    private readonly ScriptedProxyExecutorFactory _proxyExecutors = new();
    private readonly XaiOAuthStateCache _states;
    private readonly SwitchableStateCache _stateCache;

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private UserRecord _user = null!;
    private UserRecord _other = null!;

    public AdminXaiOAuthEndpointTests()
    {
        _states = new XaiOAuthStateCache(_clock);
        _stateCache = new SwitchableStateCache(_states);
        _proxyExecutors.Executor = _executor;
    }

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
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton<IExecutor>(_executor);
        builder.Services.AddSingleton<IProxyExecutorFactory>(_proxyExecutors);
        builder.Services.AddSingleton<IXaiOAuthStateCache>(_stateCache);
        builder.Services.AddSingleton<IManagementIdentity, HttpContextManagementIdentity>();
        builder.Services.AddScoped<ManagementAuthService>();
        builder.Services.AddScoped<XaiOAuthService>(services => new XaiOAuthService(
            services.GetRequiredService<IXaiOAuthStateCache>(),
            services.GetRequiredService<IExecutor>(),
            services.GetRequiredService<IProxyExecutorFactory>(),
            services.GetRequiredService<IManagementIdentity>(),
            _ => new ScriptedSsoTransport(XaiSsoScript.Handler)));

        _user = _users.Add(row =>
        {
            row.Id = 11;
            row.Email = "owner@example.com";
            row.IsOwner = true;
        });
        _other = _users.Add(row =>
        {
            row.Id = 12;
            row.Email = "other@example.com";
            row.IsOwner = false;
        });
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);

        _app = builder.Build();

        _app.UseWhen(
            static context => context.Request.Path.StartsWithSegments("/admin/xai"),
            static branch =>
            {
                branch.UseMiddleware<AdminJwtAuthMiddleware>();
                branch.UseMiddleware<ProjectIdMiddleware>();
            });

        RequestDelegate start = context => XaiOAuthHandlers.StartAsync(
            context,
            context.RequestServices.GetRequiredService<XaiOAuthService>());
        RequestDelegate exchange = context => XaiOAuthHandlers.ExchangeAsync(
            context,
            context.RequestServices.GetRequiredService<XaiOAuthService>());
        RequestDelegate sso = context => XaiOAuthHandlers.DecodeSsoAsync(
            context,
            context.RequestServices.GetRequiredService<XaiOAuthService>());

        _app.MapPost("/admin/xai/oauth/start", start);
        _app.MapPost("/admin/xai/oauth/exchange", exchange);
        _app.MapPost("/admin/xai/oauth/sso", sso);

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    // ---------------- admin gate ----------------

    [Theory]
    [InlineData("/admin/xai/oauth/start")]
    [InlineData("/admin/xai/oauth/exchange")]
    [InlineData("/admin/xai/oauth/sso")]
    public async Task RoutesRequireTheJwtGate(string path)
    {
        var (status, body) = await PostRawAsync(path, "{}");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("API key is required", Message(body));
        Assert.Equal("Unauthorized", ErrorType(body));
    }

    [Fact]
    public async Task StartValidatesTheProjectHeader()
    {
        var (status, body) = await PostRawAsync(
            "/admin/xai/oauth/start",
            string.Empty,
            token: IssueToken(_user.Id),
            projectId: "not-a-gid");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Invalid project ID", Message(body));
    }

    // ---------------- POST /admin/xai/oauth/start ----------------

    [Fact]
    public async Task StartNeverReadsTheBody()
    {
        // xai.go's StartOAuth has no request struct and never binds the body.
        var (status, body) = await PostRawAsync(
            "/admin/xai/oauth/start",
            "{not json",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotEmpty(body.GetProperty("session_id").GetString()!);
    }

    [Fact]
    public async Task StartStoresTheUserBoundSession()
    {
        var (status, body) = await PostRawAsync(
            "/admin/xai/oauth/start",
            string.Empty,
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        var sessionId = body.GetProperty("session_id").GetString()!;
        var authUrl = body.GetProperty("auth_url").GetString()!;

        Assert.StartsWith(SubscriptionConstants.AuthorizeUrl + "?", authUrl, StringComparison.Ordinal);

        var stored = await _states.GetAsync(XaiOAuthService.CacheKey(sessionId));
        Assert.NotNull(stored);
        Assert.Equal(_user.Id, stored!.UserId);
    }

    [Fact]
    public async Task StartMapsACacheFailureToInternalServerError()
    {
        _stateCache.SetFailure = new InvalidOperationException("boom");

        var (status, body) = await PostRawAsync(
            "/admin/xai/oauth/start",
            string.Empty,
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("save xAI oauth state: boom", Message(body));
        Assert.Equal("Internal Server Error", ErrorType(body));
    }

    // ---------------- POST /admin/xai/oauth/exchange ----------------

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"session_id":"s"}""")]
    [InlineData("""{"callback_url":"http://localhost/cb?code=a&state=s"}""")]
    [InlineData("""{"session_id":"","callback_url":"http://localhost/cb?code=a&state=s"}""")]
    [InlineData("""{"session_id":"s","callback_url":""}""")]
    [InlineData("""{"session_id":5,"callback_url":"http://localhost/cb?code=a&state=s"}""")]
    public async Task ExchangeRejectsAnInvalidBody(string rawBody)
    {
        var (status, body) = await PostRawAsync(
            "/admin/xai/oauth/exchange",
            rawBody,
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid request format", Message(body));
    }

    [Fact]
    public async Task ExchangeReportsAnUnknownSession()
    {
        var (status, body) = await PostRawAsync(
            "/admin/xai/oauth/exchange",
            """{"session_id":"missing","callback_url":"http://localhost/cb?code=a&state=missing"}""",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid or expired oauth session", Message(body));
    }

    [Fact]
    public async Task ExchangeRejectsAnotherUsersSessionAndKeepsIt()
    {
        var sessionId = await StartSessionAsync(_user.Id);

        var (status, body) = await PostRawAsync(
            "/admin/xai/oauth/exchange",
            JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                callback_url = Callback(sessionId),
            }),
            token: IssueToken(_other.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid or expired oauth session", Message(body));
        Assert.NotNull(await _states.GetAsync(XaiOAuthService.CacheKey(sessionId)));
    }

    [Fact]
    public async Task ExchangeRejectsAStateMismatchAndKeepsTheSession()
    {
        var sessionId = await StartSessionAsync(_user.Id);

        var (status, body) = await PostRawAsync(
            "/admin/xai/oauth/exchange",
            JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                callback_url = "http://127.0.0.1:56121/callback?code=abc&state=other",
            }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("oauth state mismatch", Message(body));

        // xai.go deletes only after the callback validates.
        Assert.NotNull(await _states.GetAsync(XaiOAuthService.CacheKey(sessionId)));
    }

    [Fact]
    public async Task ExchangeMapsTokenFailuresToBadGateway()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"error\":\"invalid_grant\",\"error_description\":\"bad code\"}");
        var sessionId = await StartSessionAsync(_user.Id);

        var (status, body) = await PostRawAsync(
            "/admin/xai/oauth/exchange",
            JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                callback_url = Callback(sessionId),
            }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Equal(
            "xAI token exchange failed: token exchange failed: invalid_grant - bad code",
            Message(body));
        Assert.Null(await _states.GetAsync(XaiOAuthService.CacheKey(sessionId)));
    }

    [Fact]
    public async Task ExchangeReturnsCredentialsAndConsumesTheSession()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"token_type\":\"bearer\"}");
        var sessionId = await StartSessionAsync(_user.Id);

        var (status, body) = await PostRawAsync(
            "/admin/xai/oauth/exchange",
            JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                callback_url = Callback(sessionId),
            }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            "{\"client_id\":\"" + SubscriptionConstants.ClientId + "\","
            + "\"access_token\":\"access-1\","
            + "\"refresh_token\":\"refresh-1\","
            + "\"expires_at\":\"0001-01-01T00:00:00Z\","
            + "\"token_type\":\"bearer\"}",
            body.GetProperty("credentials").GetString());
        Assert.Null(await _states.GetAsync(XaiOAuthService.CacheKey(sessionId)));

        var (againStatus, againBody) = await PostRawAsync(
            "/admin/xai/oauth/exchange",
            JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                callback_url = Callback(sessionId),
            }),
            token: IssueToken(_user.Id));
        Assert.Equal(HttpStatusCode.BadRequest, againStatus);
        Assert.Equal("invalid or expired oauth session", Message(againBody));
    }

    [Fact]
    public async Task ExchangeRoutesTheUrlProxyThroughTheFactory()
    {
        var proxyExecutor = new ScriptedExecutor
        {
            OnDo = (call, request, ct) => Json(
                200,
                "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\"}"),
        };
        _proxyExecutors.Executor = proxyExecutor;
        var sessionId = await StartSessionAsync(_user.Id);

        var (status, body) = await PostRawAsync(
            "/admin/xai/oauth/exchange",
            JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                callback_url = Callback(sessionId),
                proxy = new
                {
                    type = "url",
                    url = "http://proxy.local:8080",
                    username = "user",
                    password = "secret",
                    disableConnectionReuse = true,
                },
            }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(
            "\"access_token\":\"access-1\"",
            body.GetProperty("credentials").GetString(),
            StringComparison.Ordinal);

        var configured = Assert.Single(_proxyExecutors.Requests);
        Assert.Equal("http://proxy.local:8080", configured.Url);
        Assert.Equal("user", configured.Username);
        Assert.True(configured.DisableConnectionReuse);
        Assert.Empty(_executor.Requests);
    }

    // ---------------- POST /admin/xai/oauth/sso ----------------

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"sso_token":""}""")]
    [InlineData("""{"sso_token":5}""")]
    public async Task SsoRejectsAnInvalidBody(string rawBody)
    {
        var (status, body) = await PostRawAsync(
            "/admin/xai/oauth/sso",
            rawBody,
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid request format", Message(body));
    }

    [Fact]
    public async Task SsoReturnsTheConvertedCredentials()
    {
        var (status, body) = await PostRawAsync(
            "/admin/xai/oauth/sso",
            """{"sso_token":"sso=synthetic-sso"}""",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        var credentials = body.GetProperty("credentials").GetString()!;
        Assert.Contains(
            "\"client_id\":\"" + SubscriptionConstants.ClientId + "\"",
            credentials,
            StringComparison.Ordinal);
        Assert.Contains("\"access_token\":\"access\"", credentials, StringComparison.Ordinal);
    }

    // ---------------- helpers ----------------

    private static string ErrorType(JsonElement body)
        => body.GetProperty("error").GetProperty("type").GetString()!;

    private static string Message(JsonElement body)
        => body.GetProperty("error").GetProperty("message").GetString()!;

    private static string IssueToken(long userId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return JwtTokenCodec.EncodeHs256(Secret, userId, now, now, now + 3600);
    }

    private static string Callback(string sessionId)
        => "http://127.0.0.1:56121/callback?code=abc&state=" + sessionId;

    private async Task<string> StartSessionAsync(long userId)
    {
        var (status, body) = await PostRawAsync(
            "/admin/xai/oauth/start",
            string.Empty,
            token: IssueToken(userId));

        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("session_id").GetString()!;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostRawAsync(
        string path,
        string rawBody,
        string? token = null,
        string? projectId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(rawBody, Encoding.UTF8, "application/json"),
        };

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
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

    private static HttpResponseData Json(int statusCode, string body)
        => new()
        {
            StatusCode = statusCode,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(body),
        };

    /// <summary>
    /// Delegates to the memory cache, but can fail Set so the handler's 500
    /// branch is reachable without a broken production cache.
    /// </summary>
    private sealed class SwitchableStateCache(XaiOAuthStateCache inner)
        : IXaiOAuthStateCache
    {
        public Exception? SetFailure { get; set; }

        public Task<XaiOAuthState?> GetAsync(
            string key,
            CancellationToken cancellationToken = default)
            => inner.GetAsync(key, cancellationToken);

        public Task SetAsync(
            string key,
            XaiOAuthState value,
            TimeSpan expiration,
            CancellationToken cancellationToken = default)
            => SetFailure is null
                ? inner.SetAsync(key, value, expiration, cancellationToken)
                : Task.FromException(SetFailure);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(key, cancellationToken);
    }
}
