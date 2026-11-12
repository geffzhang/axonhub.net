using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Anthropic.ClaudeCode;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the REST surface of routes.go:120-121: the JWT-gated
/// POST /admin/claudecode/oauth/start and /exchange, their gin envelopes, and
/// the delete-before-parse session semantics of api/claudecode.go.
/// </summary>
public sealed class AdminClaudeCodeOAuthEndpointTests : IAsyncLifetime
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
    private readonly ClaudeCodeOAuthStateCache _states;
    private readonly SwitchableStateCache _stateCache;

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private UserRecord _user = null!;

    public AdminClaudeCodeOAuthEndpointTests()
    {
        _states = new ClaudeCodeOAuthStateCache(_clock);
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
        builder.Services.AddSingleton<IClaudeCodeOAuthStateCache>(_stateCache);
        builder.Services.AddScoped<ManagementAuthService>();
        builder.Services.AddScoped<ClaudeCodeOAuthService>();

        _user = _users.Add(row =>
        {
            row.Id = 11;
            row.Email = "owner@example.com";
            row.IsOwner = true;
        });
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);

        _app = builder.Build();

        _app.UseWhen(
            static context => context.Request.Path.StartsWithSegments("/admin/claudecode"),
            static branch =>
            {
                branch.UseMiddleware<AdminJwtAuthMiddleware>();
                branch.UseMiddleware<ProjectIdMiddleware>();
            });

        RequestDelegate start = context => ClaudeCodeOAuthHandlers.StartAsync(
            context,
            context.RequestServices.GetRequiredService<ClaudeCodeOAuthService>());
        RequestDelegate exchange = context => ClaudeCodeOAuthHandlers.ExchangeAsync(
            context,
            context.RequestServices.GetRequiredService<ClaudeCodeOAuthService>());

        _app.MapPost("/admin/claudecode/oauth/start", start);
        _app.MapPost("/admin/claudecode/oauth/exchange", exchange);

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
    [InlineData("/admin/claudecode/oauth/start")]
    [InlineData("/admin/claudecode/oauth/exchange")]
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
            "/admin/claudecode/oauth/start",
            "{}",
            token: IssueToken(_user.Id),
            projectId: "not-a-gid");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Invalid project ID", Message(body));
    }

    // ---------------- POST /admin/claudecode/oauth/start ----------------

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
            "/admin/claudecode/oauth/start",
            rawBody,
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid request format", Message(body));
        Assert.Equal("Bad Request", ErrorType(body));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task StartAcceptsAnEmptyRequest(string rawBody)
    {
        var (status, body) = await PostRawAsync(
            "/admin/claudecode/oauth/start",
            rawBody,
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.NotEmpty(body.GetProperty("session_id").GetString()!);
    }

    [Fact]
    public async Task StartReturnsSessionIdAndAuthUrl()
    {
        var (status, body) = await PostRawAsync(
            "/admin/claudecode/oauth/start",
            "{}",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        var sessionId = body.GetProperty("session_id").GetString()!;
        var authUrl = body.GetProperty("auth_url").GetString()!;

        Assert.StartsWith(ClaudeCodeConstants.AuthorizeUrl + "?", authUrl, StringComparison.Ordinal);

        var query = HttpClientUtils.ParseQuery(authUrl[(authUrl.IndexOf('?') + 1)..]);
        Assert.Equal(ClaudeCodeConstants.ClientId, query["client_id"][0]);
        Assert.Equal(ClaudeCodeConstants.RedirectUri, query["redirect_uri"][0]);
        Assert.Equal(ClaudeCodeConstants.Scopes, query["scope"][0]);
        Assert.Equal(sessionId, query["state"][0]);

        Assert.NotNull(await _states.GetAsync(ClaudeCodeOAuthService.CacheKey(sessionId)));
    }

    [Fact]
    public async Task StartMapsACacheFailureToInternalServerError()
    {
        _stateCache.SetFailure = new InvalidOperationException("boom");

        var (status, body) = await PostRawAsync(
            "/admin/claudecode/oauth/start",
            "{}",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("failed to save oauth state: boom", Message(body));
    }

    // ---------------- POST /admin/claudecode/oauth/exchange ----------------

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"session_id":"s"}""")]
    [InlineData("""{"callback_url":"http://localhost/cb?code=a"}""")]
    [InlineData("""{"session_id":"","callback_url":"http://localhost/cb?code=a"}""")]
    [InlineData("""{"session_id":"s","callback_url":""}""")]
    [InlineData("""{"session_id":5,"callback_url":"http://localhost/cb?code=a"}""")]
    public async Task ExchangeRejectsAnInvalidBody(string rawBody)
    {
        var (status, body) = await PostRawAsync(
            "/admin/claudecode/oauth/exchange",
            rawBody,
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid request format", Message(body));
    }

    [Fact]
    public async Task ExchangeReportsAnUnknownSession()
    {
        var (status, body) = await PostRawAsync(
            "/admin/claudecode/oauth/exchange",
            """{"session_id":"missing","callback_url":"http://localhost/cb?code=a#missing"}""",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid or expired oauth session", Message(body));
    }

    [Fact]
    public async Task ExchangeConsumesTheSessionBeforeParsingTheCallback()
    {
        var sessionId = await StartSessionAsync();

        var (status, body) = await PostRawAsync(
            "/admin/claudecode/oauth/exchange",
            JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                callback_url = "localhost:54545/callback?code=abc",
            }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("callback_url must be a full URL", Message(body));
        Assert.Null(await _states.GetAsync(ClaudeCodeOAuthService.CacheKey(sessionId)));
    }

    [Fact]
    public async Task ExchangeRejectsAStateMismatchAndConsumesTheSession()
    {
        var sessionId = await StartSessionAsync();

        var (status, body) = await ExchangeAsync(sessionId, "#other");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("oauth state mismatch", Message(body));
        Assert.Null(await _states.GetAsync(ClaudeCodeOAuthService.CacheKey(sessionId)));
    }

    [Fact]
    public async Task ExchangeReadsTheStateFromTheFragment()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\"}");
        var sessionId = await StartSessionAsync();

        var (status, body) = await ExchangeAsync(sessionId, "#" + sessionId);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains(
            "\"access_token\":\"access-1\"",
            body.GetProperty("credentials").GetString(),
            StringComparison.Ordinal);
        Assert.Null(await _states.GetAsync(ClaudeCodeOAuthService.CacheKey(sessionId)));
    }

    [Fact]
    public async Task ExchangeFallsBackToTheQueryState()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\"}");
        var sessionId = await StartSessionAsync();

        var (status, _) = await PostRawAsync(
            "/admin/claudecode/oauth/exchange",
            JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                callback_url = "http://localhost:54545/callback?code=abc&state=" + sessionId,
            }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
    }

    [Fact]
    public async Task ExchangeMapsTokenFailuresToBadGateway()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"error\":\"invalid_grant\",\"error_description\":\"bad code\"}");
        var sessionId = await StartSessionAsync();

        var (status, body) = await ExchangeAsync(sessionId, "#" + sessionId);

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Equal(
            "token exchange failed: token exchange failed: invalid_grant - bad code",
            Message(body));
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

        var (status, _) = await PostRawAsync(
            "/admin/claudecode/oauth/exchange",
            JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                callback_url = "http://localhost:54545/callback?code=abc#" + sessionId,
                proxy = new
                {
                    type = "url",
                    url = "http://proxy.local:8080",
                    username = "user",
                    password = "secret",
                },
            }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        var configured = Assert.Single(_proxyExecutors.Requests);
        Assert.Equal("http://proxy.local:8080", configured.Url);
        Assert.Equal("user", configured.Username);
        Assert.Empty(_executor.Requests);
        Assert.Single(proxyExecutor.Requests);
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
            "/admin/claudecode/oauth/start",
            "{}",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("session_id").GetString()!;
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> ExchangeAsync(
        string sessionId,
        string fragment)
        => PostRawAsync(
            "/admin/claudecode/oauth/exchange",
            JsonSerializer.Serialize(new
            {
                session_id = sessionId,
                callback_url = "http://localhost:54545/callback?code=abc" + fragment,
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

    private sealed class SwitchableStateCache(ClaudeCodeOAuthStateCache inner)
        : IClaudeCodeOAuthStateCache
    {
        public Exception? SetFailure { get; set; }

        public Task<ClaudeCodeOAuthState?> GetAsync(
            string key,
            CancellationToken cancellationToken = default)
            => inner.GetAsync(key, cancellationToken);

        public Task SetAsync(
            string key,
            ClaudeCodeOAuthState value,
            TimeSpan expiration,
            CancellationToken cancellationToken = default)
            => SetFailure is null
                ? inner.SetAsync(key, value, expiration, cancellationToken)
                : Task.FromException(SetFailure);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(key, cancellationToken);
    }
}
