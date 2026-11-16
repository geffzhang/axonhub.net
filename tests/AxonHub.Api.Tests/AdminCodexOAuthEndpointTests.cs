using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the REST surface of routes.go:114-116: the JWT-gated
/// POST /admin/codex/oauth/start, /exchange and /auth/decode, their gin
/// envelopes, and the 400/401/500/502 classifications of api/codex.go.
/// </summary>
public sealed class AdminCodexOAuthEndpointTests : IAsyncLifetime
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
    private readonly CodexOAuthStateCache _states;
    private readonly SwitchableStateCache _stateCache;

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private UserRecord _user = null!;

    public AdminCodexOAuthEndpointTests()
    {
        _states = new CodexOAuthStateCache(_clock);
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
        builder.Services.AddSingleton<ICodexOAuthStateCache>(_stateCache);
        builder.Services.AddScoped<ManagementAuthService>();
        builder.Services.AddScoped<CodexOAuthService>();

        _user = _users.Add(row =>
        {
            row.Id = 11;
            row.Email = "owner@example.com";
            row.IsOwner = true;
        });
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);

        _app = builder.Build();

        // Program.cs mounts the admin group's WithJWTAuth + WithProjectID on
        // /admin/codex.
        _app.UseWhen(
            static context => context.Request.Path.StartsWithSegments("/admin/codex"),
            static branch =>
            {
                branch.UseMiddleware<AdminJwtAuthMiddleware>();
                branch.UseMiddleware<ProjectIdMiddleware>();
            });

        // Register through RequestDelegate so the route-handler analyzer does
        // not try to generate interceptors for a method group from another
        // assembly (AD0001), the same pattern as the Antigravity endpoint tests.
        RequestDelegate start = context => CodexOAuthHandlers.StartAsync(
            context,
            context.RequestServices.GetRequiredService<CodexOAuthService>());
        RequestDelegate exchange = context => CodexOAuthHandlers.ExchangeAsync(
            context,
            context.RequestServices.GetRequiredService<CodexOAuthService>());
        RequestDelegate decode = context => CodexOAuthHandlers.DecodeAuthJsonAsync(
            context,
            context.RequestServices.GetRequiredService<CodexOAuthService>());

        _app.MapPost("/admin/codex/oauth/start", start);
        _app.MapPost("/admin/codex/oauth/exchange", exchange);
        _app.MapPost("/admin/codex/auth/decode", decode);

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
    [InlineData("/admin/codex/oauth/start")]
    [InlineData("/admin/codex/oauth/exchange")]
    [InlineData("/admin/codex/auth/decode")]
    public async Task RoutesRequireTheJwtGate(string path)
    {
        var (status, body) = await PostRawAsync(path, "{}");

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("API key is required", Message(body));
        Assert.Equal("Unauthorized", ErrorType(body));
    }

    // ---------------- POST /admin/codex/oauth/start ----------------

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("5")]
    [InlineData("\"text\"")]
    public async Task StartRejectsAnInvalidBody(string rawBody)
    {
        var (status, body) = await PostRawAsync(
            "/admin/codex/oauth/start",
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
            "/admin/codex/oauth/start",
            "null",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        var sessionId = body.GetProperty("session_id").GetString()!;
        Assert.NotNull(await _states.GetAsync(CodexOAuthService.CacheKey(sessionId)));
    }

    [Fact]
    public async Task StartReturnsSessionIdAndAuthUrl()
    {
        var (status, body) = await PostRawAsync(
            "/admin/codex/oauth/start",
            "{}",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);

        var sessionId = body.GetProperty("session_id").GetString()!;
        var authUrl = body.GetProperty("auth_url").GetString()!;
        var query = HttpClientUtils.ParseQuery(authUrl[(authUrl.IndexOf('?') + 1)..]);

        Assert.StartsWith(CodexConstants.AuthorizeUrl + "?", authUrl, StringComparison.Ordinal);
        Assert.Equal(CodexConstants.ClientId, query["client_id"][0]);
        Assert.Equal(CodexConstants.RedirectUri, query["redirect_uri"][0]);
        Assert.Equal("true", query["codex_cli_simplified_flow"][0]);
        Assert.Equal(sessionId, query["state"][0]);
        Assert.False(query.ContainsKey("originator"));
    }

    [Fact]
    public async Task StartValidatesTheProjectHeader()
    {
        var (status, body) = await PostRawAsync(
            "/admin/codex/oauth/start",
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
            "/admin/codex/oauth/start",
            "{}",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("failed to save oauth state: boom", Message(body));
        Assert.Equal("Internal Server Error", ErrorType(body));
    }

    // ---------------- POST /admin/codex/oauth/exchange ----------------

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
            "/admin/codex/oauth/exchange",
            rawBody,
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid request format", Message(body));
    }

    [Fact]
    public async Task ExchangeReportsAnUnknownSession()
    {
        var (status, body) = await PostRawAsync(
            "/admin/codex/oauth/exchange",
            """{"session_id":"missing","callback_url":"http://localhost/cb?code=a&state=missing"}""",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid or expired oauth session", Message(body));
        Assert.Equal("Bad Request", ErrorType(body));
    }

    [Fact]
    public async Task ExchangeRejectsANonUrlCallbackAndConsumesTheSession()
    {
        var sessionId = await StartSessionAsync();

        var (status, body) = await ExchangeAsync(
            sessionId,
            "localhost:1455/auth/callback?code=abc&state=" + sessionId);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("callback_url must be a full URL", Message(body));
        Assert.Null(await _states.GetAsync(CodexOAuthService.CacheKey(sessionId)));
    }

    [Fact]
    public async Task ExchangeRejectsAStateMismatchAndConsumesTheSession()
    {
        var sessionId = await StartSessionAsync();

        var (status, body) = await ExchangeAsync(
            sessionId,
            "http://localhost:1455/auth/callback?code=abc&state=other");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("oauth state mismatch", Message(body));

        // The reference test proves the retry with the right state also fails.
        var (againStatus, againBody) = await ExchangeAsync(
            sessionId,
            "http://localhost:1455/auth/callback?code=abc&state=" + sessionId);
        Assert.Equal(HttpStatusCode.BadRequest, againStatus);
        Assert.Equal("invalid or expired oauth session", Message(againBody));
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
            "http://localhost:1455/auth/callback?code=abc&state=" + sessionId);

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Equal(
            "token exchange failed: token exchange failed: invalid_grant - bad code",
            Message(body));
        Assert.Equal("Bad Gateway", ErrorType(body));
        Assert.Null(await _states.GetAsync(CodexOAuthService.CacheKey(sessionId)));
    }

    [Fact]
    public async Task ExchangeReturnsCredentialsAndConsumesTheSession()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\",\"token_type\":\"bearer\"}");
        var sessionId = await StartSessionAsync();

        var (status, body) = await ExchangeAsync(
            sessionId,
            "http://localhost:1455/auth/callback?code=abc&state=" + sessionId);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(
            "{\"client_id\":\"" + CodexConstants.ClientId + "\","
            + "\"access_token\":\"access-1\","
            + "\"refresh_token\":\"refresh-1\","
            + "\"expires_at\":\"0001-01-01T00:00:00Z\","
            + "\"token_type\":\"bearer\"}",
            body.GetProperty("credentials").GetString());
        Assert.Null(await _states.GetAsync(CodexOAuthService.CacheKey(sessionId)));

        var (againStatus, againBody) = await ExchangeAsync(
            sessionId,
            "http://localhost:1455/auth/callback?code=abc&state=" + sessionId);
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
        var sessionId = await StartSessionAsync();

        var (status, body) = await PostRawAsync(
            "/admin/codex/oauth/exchange",
            JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                callback_url = "http://localhost:1455/auth/callback?code=abc&state=" + sessionId,
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
        Assert.Equal("secret", configured.Password);
        Assert.True(configured.DisableConnectionReuse);

        Assert.Single(proxyExecutor.Requests);
        Assert.Empty(_executor.Requests);
    }

    [Fact]
    public async Task ExchangeIgnoresADisabledProxy()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"access_token\":\"access-1\"}");
        var sessionId = await StartSessionAsync();

        var (status, _) = await PostRawAsync(
            "/admin/codex/oauth/exchange",
            JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                callback_url = "http://localhost:1455/auth/callback?code=abc&state=" + sessionId,
                proxy = new { type = "disabled", url = "http://proxy.local:8080" },
            }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(_proxyExecutors.Requests);
        Assert.Single(_executor.Requests);
    }

    // ---------------- POST /admin/codex/auth/decode ----------------

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"auth_json":""}""")]
    [InlineData("""{"auth_json":5}""")]
    public async Task DecodeRejectsAnInvalidBody(string rawBody)
    {
        var (status, body) = await PostRawAsync(
            "/admin/codex/auth/decode",
            rawBody,
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid request format", Message(body));
    }

    [Fact]
    public async Task DecodeReturnsTheNormalizedCredentials()
    {
        var authJson = "{\"auth_mode\":\"chatgpt\","
            + "\"last_refresh\":\"2026-04-17T08:58:36.389Z\","
            + "\"tokens\":{\"access_token\":\"access\",\"refresh_token\":\"refresh\","
            + "\"id_token\":\"id\"}}";

        var (status, body) = await PostRawAsync(
            "/admin/codex/auth/decode",
            JsonSerializer.Serialize(new { auth_json = authJson }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        var credentials = body.GetProperty("credentials").GetString()!;
        Assert.Contains(
            "\"client_id\":\"" + CodexConstants.ClientId + "\"",
            credentials,
            StringComparison.Ordinal);
        Assert.Contains("\"access_token\":\"access\"", credentials, StringComparison.Ordinal);
        Assert.Contains("\"token_type\":\"bearer\"", credentials, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DecodeMapsAnInvalidAuthJsonToBadRequest()
    {
        var (status, body) = await PostRawAsync(
            "/admin/codex/auth/decode",
            """{"auth_json":"{\"tokens\":{}}"}""",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(
            "failed to decode auth json: access_token is empty",
            Message(body));
        Assert.Equal("Bad Request", ErrorType(body));
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

    private async Task<string> StartSessionAsync()
    {
        var (status, body) = await PostRawAsync(
            "/admin/codex/oauth/start",
            "{}",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("session_id").GetString()!;
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> ExchangeAsync(
        string sessionId,
        string callbackUrl)
        => PostRawAsync(
            "/admin/codex/oauth/exchange",
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
    private sealed class SwitchableStateCache(CodexOAuthStateCache inner)
        : ICodexOAuthStateCache
    {
        public Exception? SetFailure { get; set; }

        public Task<CodexOAuthState?> GetAsync(
            string key,
            CancellationToken cancellationToken = default)
            => inner.GetAsync(key, cancellationToken);

        public Task SetAsync(
            string key,
            CodexOAuthState value,
            TimeSpan expiration,
            CancellationToken cancellationToken = default)
            => SetFailure is null
                ? inner.SetAsync(key, value, expiration, cancellationToken)
                : Task.FromException(SetFailure);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(key, cancellationToken);
    }
}
