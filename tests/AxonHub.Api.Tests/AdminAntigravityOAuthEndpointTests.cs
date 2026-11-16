using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Antigravity;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the REST surface of routes.go:124-125: the JWT-gated
/// POST /admin/antigravity/oauth/start and /exchange, their gin envelopes, and
/// the 400/401/502 classifications of api/antigravity.go.
/// </summary>
public sealed class AdminAntigravityOAuthEndpointTests : IAsyncLifetime
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
    private readonly AntigravityOAuthStateCache _states;
    private readonly SwitchableStateCache _stateCache;

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private UserRecord _user = null!;

    public AdminAntigravityOAuthEndpointTests()
    {
        _states = new AntigravityOAuthStateCache(_clock);
        _stateCache = new SwitchableStateCache(_states);
        _proxyExecutors.Executor = _executor;
    }

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IUserStore>(_users);
        builder.Services.AddSingleton<ISystemStore>(_systems);
        builder.Services.AddSingleton<IUserProfileStore>(_profiles);
        builder.Services.AddSingleton<IPasswordHasher>(_passwords);
        builder.Services.AddSingleton<IOidcLoginOnlyProviders>(_oidc);
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton<IExecutor>(_executor);
        builder.Services.AddSingleton<IProxyExecutorFactory>(_proxyExecutors);
        builder.Services.AddSingleton<IAntigravityOAuthStateCache>(_stateCache);
        builder.Services.AddScoped<ManagementAuthService>();
        builder.Services.AddScoped<AntigravityOAuthService>();

        _user = _users.Add(row =>
        {
            row.Id = 11;
            row.Email = "owner@example.com";
            row.IsOwner = true;
        });
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);

        _app = builder.Build();

        // Program.cs mounts the admin group's WithJWTAuth + WithProjectID on
        // /admin/antigravity/oauth.
        _app.UseWhen(
            static context => context.Request.Path.StartsWithSegments("/admin/antigravity/oauth"),
            static branch =>
            {
                branch.UseMiddleware<AdminJwtAuthMiddleware>();
                branch.UseMiddleware<ProjectIdMiddleware>();
            });

        // Register through RequestDelegate so the route-handler analyzer does
        // not try to generate interceptors for a method group from another
        // assembly (AD0001), the same pattern as the invitation endpoint tests.
        RequestDelegate start = context => AntigravityOAuthHandlers.StartAsync(
            context,
            context.RequestServices.GetRequiredService<AntigravityOAuthService>());
        RequestDelegate exchange = context => AntigravityOAuthHandlers.ExchangeAsync(
            context,
            context.RequestServices.GetRequiredService<AntigravityOAuthService>());

        _app.MapPost("/admin/antigravity/oauth/start", start);
        _app.MapPost("/admin/antigravity/oauth/exchange", exchange);

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    // ---------------- POST /admin/antigravity/oauth/start ----------------

    [Fact]
    public async Task StartRequiresTheJwtGate()
    {
        var (status, body) = await PostRawAsync("/admin/antigravity/oauth/start", "{}");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("API key is required", Message(body));
        Assert.Equal("Unauthorized", ErrorType(body));
    }

    [Fact]
    public async Task StartRejectsANonBearerAuthorizationHeader()
    {
        var (status, body) = await PostRawAsync(
            "/admin/antigravity/oauth/start",
            "{}",
            authorization: "Basic abc");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(
            "invalid token: Authorization header must start with 'Bearer '",
            Message(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("""{"project_id":5}""")]
    public async Task StartRejectsAnInvalidBody(string rawBody)
    {
        var (status, body) = await PostRawAsync(
            "/admin/antigravity/oauth/start",
            rawBody,
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid request format", Message(body));
        Assert.Equal("Bad Request", ErrorType(body));
    }

    [Fact]
    public async Task StartAcceptsAJsonNullBodyAsTheZeroRequest()
    {
        // ShouldBindJSON into a struct with no required fields accepts the
        // literal null.
        var (status, body) = await PostRawAsync(
            "/admin/antigravity/oauth/start",
            "null",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        var sessionId = body.GetProperty("session_id").GetString()!;
        var stored = await _states.GetAsync(AntigravityOAuthService.CacheKey(sessionId));
        Assert.Equal(string.Empty, stored!.ProjectId);
    }

    [Fact]
    public async Task StartReturnsSessionIdAndAuthUrl()
    {
        var (status, body) = await PostRawAsync(
            "/admin/antigravity/oauth/start",
            """{"project_id":"projects/from-request"}""",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);

        var sessionId = body.GetProperty("session_id").GetString()!;
        Assert.Equal(32, Base64Url.DecodeFromChars(sessionId).Length);

        var authUrl = body.GetProperty("auth_url").GetString()!;
        Assert.StartsWith(AntigravityConstants.AuthorizeURL + "?", authUrl, StringComparison.Ordinal);
        Assert.Contains("client_id=" + AntigravityConstants.ClientID, authUrl, StringComparison.Ordinal);
        Assert.Contains("state=" + sessionId, authUrl, StringComparison.Ordinal);

        var stored = await _states.GetAsync(AntigravityOAuthService.CacheKey(sessionId));
        Assert.Equal("projects/from-request", stored!.ProjectId);
    }

    [Fact]
    public async Task StartValidatesTheProjectHeader()
    {
        var (status, body) = await PostRawAsync(
            "/admin/antigravity/oauth/start",
            "{}",
            token: IssueToken(_user.Id),
            projectId: "not-a-gid");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Invalid project ID", Message(body));
    }

    [Fact]
    public async Task StartMapsACacheFailureToInternalServerError()
    {
        _stateCache.SetFailure = new InvalidOperationException("boom");

        var (status, body) = await PostRawAsync(
            "/admin/antigravity/oauth/start",
            "{}",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("failed to save oauth state: boom", Message(body));
        Assert.Equal("Internal Server Error", ErrorType(body));
    }

    // ---------------- POST /admin/antigravity/oauth/exchange ----------------

    [Fact]
    public async Task ExchangeRequiresTheJwtGate()
    {
        var (status, body) = await PostRawAsync(
            "/admin/antigravity/oauth/exchange",
            """{"session_id":"s","callback_url":"http://localhost/cb?code=a&state=s"}""");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("API key is required", Message(body));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"session_id":"s"}""")]
    [InlineData("""{"callback_url":"http://localhost/cb?code=a&state=s"}""")]
    [InlineData("""{"session_id":"","callback_url":"http://localhost/cb?code=a&state=s"}""")]
    public async Task ExchangeRejectsAnInvalidBody(string rawBody)
    {
        var (status, body) = await PostRawAsync(
            "/admin/antigravity/oauth/exchange",
            rawBody,
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid request format", Message(body));
    }

    [Fact]
    public async Task ExchangeReportsAnUnknownSession()
    {
        var (status, body) = await PostRawAsync(
            "/admin/antigravity/oauth/exchange",
            """{"session_id":"missing","callback_url":"http://localhost/cb?code=a&state=missing"}""",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid or expired oauth session", Message(body));
        Assert.Equal("Bad Request", ErrorType(body));
    }

    [Fact]
    public async Task ExchangeRejectsANonUrlCallback()
    {
        var sessionId = await StartSessionAsync();

        var (status, body) = await ExchangeAsync(
            sessionId,
            "localhost:51121/oauth-callback?code=abc&state=" + sessionId);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("callback_url must be a full URL", Message(body));
    }

    [Fact]
    public async Task ExchangeRejectsAStateMismatch()
    {
        var sessionId = await StartSessionAsync();

        var (status, body) = await ExchangeAsync(
            sessionId,
            "http://localhost:51121/oauth-callback?code=abc&state=other");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("oauth state mismatch", Message(body));
        Assert.NotNull(await _states.GetAsync(AntigravityOAuthService.CacheKey(sessionId)));
    }

    [Fact]
    public async Task ExchangeMapsTokenFailuresToBadGateway()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"error\":\"invalid_grant\",\"error_description\":\"bad code\"}");
        var sessionId = await StartSessionAsync();

        var (status, body) = await ExchangeAsync(
            sessionId,
            "http://localhost:51121/oauth-callback?code=abc&state=" + sessionId);

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Equal(
            "token exchange failed: token exchange failed: invalid_grant - bad code",
            Message(body));
        Assert.Equal("Bad Gateway", ErrorType(body));
    }

    [Fact]
    public async Task ExchangeMapsResolutionFailuresToBadGateway()
    {
        _executor.OnDo = (call, request, ct) => request.Url == AntigravityConstants.TokenURL
            ? Json(
                200,
                "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"expires_in\":3600}")
            : Json(201, "{}");
        var sessionId = await StartSessionAsync();

        var (status, body) = await ExchangeAsync(
            sessionId,
            "http://localhost:51121/oauth-callback?code=abc&state=" + sessionId);

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Equal(
            "failed to resolve project id and none provided: status 201",
            Message(body));
    }

    [Fact]
    public async Task ExchangeReturnsCredentialsAndConsumesTheSession()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"expires_in\":3600}");
        var sessionId = await StartSessionAsync("projects/from-request");

        var (status, body) = await ExchangeAsync(
            sessionId,
            "http://localhost:51121/oauth-callback?code=abc&state=" + sessionId);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("refresh-1|projects/from-request", body.GetProperty("credentials").GetString());
        Assert.Null(await _states.GetAsync(AntigravityOAuthService.CacheKey(sessionId)));

        var (againStatus, againBody) = await ExchangeAsync(
            sessionId,
            "http://localhost:51121/oauth-callback?code=abc&state=" + sessionId);
        Assert.Equal(HttpStatusCode.BadRequest, againStatus);
        Assert.Equal("invalid or expired oauth session", Message(againBody));
    }

    [Fact]
    public async Task ExchangeRoutesTheUrlProxyThroughTheFactory()
    {
        var proxyExecutor = new ScriptedExecutor
        {
            OnDo = (call, request, ct) => request.Url == AntigravityConstants.TokenURL
                ? Json(
                    200,
                    "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"expires_in\":3600}")
                : Json(200, "{\"cloudaicompanionProject\":\"proj-proxy\"}"),
        };
        _proxyExecutors.Executor = proxyExecutor;
        var sessionId = await StartSessionAsync();

        var (status, body) = await PostRawAsync(
            "/admin/antigravity/oauth/exchange",
            JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                callback_url = "http://localhost:51121/oauth-callback?code=abc&state=" + sessionId,
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
        Assert.Equal("refresh-1|proj-proxy", body.GetProperty("credentials").GetString());

        var configured = Assert.Single(_proxyExecutors.Requests);
        Assert.Equal("http://proxy.local:8080", configured.Url);
        Assert.Equal("user", configured.Username);
        Assert.Equal("secret", configured.Password);
        Assert.True(configured.DisableConnectionReuse);

        // Both the exchange and the project discovery ran on the proxy.
        Assert.Equal(2, proxyExecutor.Requests.Count);
        Assert.Empty(_executor.Requests);
    }

    [Fact]
    public async Task ExchangeIgnoresADisabledProxy()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"expires_in\":3600}");
        var sessionId = await StartSessionAsync("projects/from-request");

        var (status, body) = await PostRawAsync(
            "/admin/antigravity/oauth/exchange",
            JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                callback_url = "http://localhost:51121/oauth-callback?code=abc&state=" + sessionId,
                proxy = new { type = "disabled", url = "http://proxy.local:8080" },
            }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("refresh-1|projects/from-request", body.GetProperty("credentials").GetString());
        Assert.Empty(_proxyExecutors.Requests);
        Assert.Single(_executor.Requests);
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

    private async Task<string> StartSessionAsync(string? projectId = null)
    {
        var (status, body) = await PostRawAsync(
            "/admin/antigravity/oauth/start",
            JsonSerializer.Serialize(new { project_id = projectId }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("session_id").GetString()!;
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> ExchangeAsync(
        string sessionId,
        string callbackUrl)
        => PostRawAsync(
            "/admin/antigravity/oauth/exchange",
            JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                callback_url = callbackUrl,
            }),
            token: IssueToken(_user.Id));

    private async Task<(HttpStatusCode Status, JsonElement Body)> PostRawAsync(
        string path,
        string rawBody,
        string? token = null,
        string? projectId = null,
        string? authorization = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(rawBody, Encoding.UTF8, "application/json"),
        };

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
    private sealed class SwitchableStateCache(AntigravityOAuthStateCache inner)
        : IAntigravityOAuthStateCache
    {
        public Exception? SetFailure { get; set; }

        public Task<AntigravityOAuthState?> GetAsync(
            string key,
            CancellationToken cancellationToken = default)
            => inner.GetAsync(key, cancellationToken);

        public Task SetAsync(
            string key,
            AntigravityOAuthState value,
            TimeSpan expiration,
            CancellationToken cancellationToken = default)
            => SetFailure is null
                ? inner.SetAsync(key, value, expiration, cancellationToken)
                : Task.FromException(SetFailure);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(key, cancellationToken);
    }
}
