using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the REST surface of routes.go:127-128: the JWT-gated
/// POST /admin/copilot/oauth/start and /poll, the EOF-only empty-body tolerance
/// of api/copilot.go, and its poll envelope (omitempty token fields).
/// </summary>
public sealed class AdminCopilotOAuthEndpointTests : IAsyncLifetime
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
    private readonly CopilotDeviceFlowCache _states;
    private readonly SwitchableStateCache _stateCache;

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private UserRecord _user = null!;

    public AdminCopilotOAuthEndpointTests()
    {
        _states = new CopilotDeviceFlowCache(_clock);
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
        builder.Services.AddSingleton<ICopilotDeviceFlowCache>(_stateCache);
        builder.Services.AddScoped<ManagementAuthService>();
        builder.Services.AddScoped<CopilotOAuthService>();

        _user = _users.Add(row =>
        {
            row.Id = 11;
            row.Email = "owner@example.com";
            row.IsOwner = true;
        });
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);

        _app = builder.Build();

        _app.UseWhen(
            static context => context.Request.Path.StartsWithSegments("/admin/copilot"),
            static branch =>
            {
                branch.UseMiddleware<AdminJwtAuthMiddleware>();
                branch.UseMiddleware<ProjectIdMiddleware>();
            });

        RequestDelegate start = context => CopilotOAuthHandlers.StartAsync(
            context,
            context.RequestServices.GetRequiredService<CopilotOAuthService>());
        RequestDelegate poll = context => CopilotOAuthHandlers.PollAsync(
            context,
            context.RequestServices.GetRequiredService<CopilotOAuthService>());

        _app.MapPost("/admin/copilot/oauth/start", start);
        _app.MapPost("/admin/copilot/oauth/poll", poll);

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
    [InlineData("/admin/copilot/oauth/start")]
    [InlineData("/admin/copilot/oauth/poll")]
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
            "/admin/copilot/oauth/start",
            "{}",
            token: IssueToken(_user.Id),
            projectId: "not-a-gid");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Invalid project ID", Message(body));
    }

    // ---------------- POST /admin/copilot/oauth/start ----------------

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t")]
    [InlineData("{}")]
    [InlineData("null")]
    public async Task StartAcceptsTheEmptyBind(string rawBody)
    {
        // copilot.go:159-166 tolerates only the EOF bind error, i.e. an empty
        // or whitespace-only body; {} and null also bind to the zero request.
        ArrangeDeviceCode();

        var (status, body) = await PostRawAsync(
            "/admin/copilot/oauth/start",
            rawBody,
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(43, body.GetProperty("session_id").GetString()!.Length);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("5")]
    [InlineData("\"text\"")]
    public async Task StartRejectsAnInvalidBody(string rawBody)
    {
        var (status, body) = await PostRawAsync(
            "/admin/copilot/oauth/start",
            rawBody,
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid request format", Message(body));
        Assert.Equal("Bad Request", ErrorType(body));
    }

    [Fact]
    public async Task StartReturnsTheDeviceFlowEnvelope()
    {
        ArrangeDeviceCode(expiresIn: 900, interval: 5);

        var (status, body) = await PostRawAsync(
            "/admin/copilot/oauth/start",
            "",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("USER-1", body.GetProperty("user_code").GetString());
        Assert.Equal("https://github.com/login/device", body.GetProperty("verification_uri").GetString());
        Assert.Equal(900, body.GetProperty("expires_in").GetInt64());
        Assert.Equal(5, body.GetProperty("interval").GetInt64());

        var sessionId = body.GetProperty("session_id").GetString()!;
        Assert.Equal(32, Base64Url.DecodeFromChars(sessionId).Length);
        Assert.NotNull(await _states.GetAsync(CopilotOAuthService.CacheKey(sessionId)));
    }

    [Fact]
    public async Task StartMapsACacheFailureToInternalServerError()
    {
        ArrangeDeviceCode();
        _stateCache.SetFailure = new InvalidOperationException("boom");

        var (status, body) = await PostRawAsync(
            "/admin/copilot/oauth/start",
            "",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("failed to save device flow state: boom", Message(body));
    }

    [Fact]
    public async Task StartRoutesTheUrlProxyThroughTheFactory()
    {
        var proxyExecutor = new ScriptedExecutor
        {
            OnDo = (call, request, ct) => DeviceCodeResponse(900),
        };
        _proxyExecutors.Executor = proxyExecutor;

        var (status, _) = await PostRawAsync(
            "/admin/copilot/oauth/start",
            """{"proxy":{"type":"url","url":"http://proxy.local:8080","username":"user","password":"secret"}}""",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        var configured = Assert.Single(_proxyExecutors.Requests);
        Assert.Equal("http://proxy.local:8080", configured.Url);
        Assert.Equal("user", configured.Username);
        Assert.Empty(_executor.Requests);
        Assert.Single(proxyExecutor.Requests);
    }

    // ---------------- POST /admin/copilot/oauth/poll ----------------

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"session_id":""}""")]
    [InlineData("""{"session_id":5}""")]
    public async Task PollRejectsAnInvalidBody(string rawBody)
    {
        var (status, body) = await PostRawAsync(
            "/admin/copilot/oauth/poll",
            rawBody,
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid request format", Message(body));
    }

    [Fact]
    public async Task PollReportsAnUnknownSession()
    {
        var (status, body) = await PostRawAsync(
            "/admin/copilot/oauth/poll",
            """{"session_id":"missing"}""",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("invalid or expired session", Message(body));
    }

    [Fact]
    public async Task PollReturnsPendingWithoutTokenFields()
    {
        var sessionId = await StartSessionAsync("{\"error\":\"authorization_pending\"}");

        var (status, body) = await PostRawAsync(
            "/admin/copilot/oauth/poll",
            JsonSerializer.Serialize(new { session_id = sessionId }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("pending", body.GetProperty("status").GetString());
        Assert.Equal(
            "Authorization pending. User has not yet authorized the device.",
            body.GetProperty("message").GetString());
        Assert.False(body.TryGetProperty("access_token", out _));
        Assert.False(body.TryGetProperty("token_type", out _));
        Assert.False(body.TryGetProperty("scope", out _));
    }

    [Fact]
    public async Task PollReturnsSlowDown()
    {
        var sessionId = await StartSessionAsync("{\"error\":\"slow_down\"}");

        var (status, body) = await PostRawAsync(
            "/admin/copilot/oauth/poll",
            JsonSerializer.Serialize(new { session_id = sessionId }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("slow_down", body.GetProperty("status").GetString());
        Assert.Equal(
            "Polling too fast. Please slow down.",
            body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task PollReturnsTheTokenAndConsumesTheSession()
    {
        var sessionId = await StartSessionAsync(
            "{\"access_token\":\"gho_1\",\"token_type\":\"bearer\",\"scope\":\"read:user\"}");

        var (status, body) = await PostRawAsync(
            "/admin/copilot/oauth/poll",
            JsonSerializer.Serialize(new { session_id = sessionId }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("gho_1", body.GetProperty("access_token").GetString());
        Assert.Equal("bearer", body.GetProperty("token_type").GetString());
        Assert.Equal("read:user", body.GetProperty("scope").GetString());
        Assert.Equal("complete", body.GetProperty("status").GetString());
        Assert.Equal(
            "Authorization complete. Access token received.",
            body.GetProperty("message").GetString());
        Assert.Null(await _states.GetAsync(CopilotOAuthService.CacheKey(sessionId)));
    }

    [Fact]
    public async Task PollRejectsAnAccessDenial()
    {
        var sessionId = await StartSessionAsync("{\"error\":\"access_denied\"}");

        var (status, body) = await PostRawAsync(
            "/admin/copilot/oauth/poll",
            JsonSerializer.Serialize(new { session_id = sessionId }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("access denied by user", Message(body));
        Assert.Null(await _states.GetAsync(CopilotOAuthService.CacheKey(sessionId)));
    }

    [Fact]
    public async Task PollExpiresTheDeviceCode()
    {
        var sessionId = await StartSessionAsync(
            "{\"error\":\"expired_token\"}",
            expiresIn: 0);

        _clock.Advance(TimeSpan.FromSeconds(1));

        var (status, body) = await PostRawAsync(
            "/admin/copilot/oauth/poll",
            JsonSerializer.Serialize(new { session_id = sessionId }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("device code expired", Message(body));
        Assert.Null(await _states.GetAsync(CopilotOAuthService.CacheKey(sessionId)));
    }

    [Fact]
    public async Task PollSurfacesAnUnknownOAuthError()
    {
        var sessionId = await StartSessionAsync(
            "{\"error\":\"bad_verification_code\",\"error_description\":\"wrong code\"}");

        var (status, body) = await PostRawAsync(
            "/admin/copilot/oauth/poll",
            JsonSerializer.Serialize(new { session_id = sessionId }),
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.BadGateway, status);
        Assert.Equal("OAuth error: bad_verification_code - wrong code", Message(body));
        Assert.Equal("Bad Gateway", ErrorType(body));
    }

    [Fact]
    public async Task PollRoutesTheUrlProxyThroughTheFactory()
    {
        var proxyExecutor = new ScriptedExecutor
        {
            OnDo = (call, request, ct) => Text(
                200,
                "{\"access_token\":\"gho_1\"}",
                "application/json"),
        };
        _proxyExecutors.Executor = proxyExecutor;
        var sessionId = await StartSessionAsync("{\"access_token\":\"gho_1\"}");

        var (status, body) = await PostRawAsync(
            "/admin/copilot/oauth/poll",
            JsonSerializer.Serialize(new
            {
                session_id = sessionId,
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
        Assert.Equal("complete", body.GetProperty("status").GetString());

        var configured = Assert.Single(_proxyExecutors.Requests);
        Assert.Equal("http://proxy.local:8080", configured.Url);
        Assert.Empty(_executor.Requests.Skip(1));
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

    private void ArrangeDeviceCode(long expiresIn = 900, long interval = 5)
    {
        _executor.OnDo = (call, request, ct) => DeviceCodeResponse(expiresIn, interval);
    }

    private async Task<string> StartSessionAsync(string tokenBody, long expiresIn = 900)
    {
        _executor.OnDo = (call, request, ct) => call == 0
            ? DeviceCodeResponse(expiresIn)
            : Text(200, tokenBody, "application/json");

        var (status, body) = await PostRawAsync(
            "/admin/copilot/oauth/start",
            "",
            token: IssueToken(_user.Id));

        Assert.Equal(HttpStatusCode.OK, status);
        return body.GetProperty("session_id").GetString()!;
    }

    private static HttpResponseData DeviceCodeResponse(long expiresIn = 900, long interval = 5)
        => Text(
            200,
            "{\"device_code\":\"device-1\",\"user_code\":\"USER-1\","
            + "\"verification_uri\":\"https://github.com/login/device\","
            + "\"expires_in\":" + expiresIn + ",\"interval\":" + interval + "}",
            "application/json");

    private static HttpResponseData Text(int statusCode, string body, string? contentType)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (contentType is not null)
        {
            headers["Content-Type"] = [contentType];
        }

        return new HttpResponseData
        {
            StatusCode = statusCode,
            Headers = headers,
            Body = Encoding.UTF8.GetBytes(body),
        };
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

    private sealed class SwitchableStateCache(CopilotDeviceFlowCache inner)
        : ICopilotDeviceFlowCache
    {
        public Exception? SetFailure { get; set; }

        public Task<CopilotDeviceFlowState?> GetAsync(
            string key,
            CancellationToken cancellationToken = default)
            => inner.GetAsync(key, cancellationToken);

        public Task SetAsync(
            string key,
            CopilotDeviceFlowState value,
            TimeSpan expiration,
            CancellationToken cancellationToken = default)
            => SetFailure is null
                ? inner.SetAsync(key, value, expiration, cancellationToken)
                : Task.FromException(SetFailure);

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(key, cancellationToken);
    }
}
