using System.Buffers.Text;
using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the behavior of api/copilot.go (internal/server/api/copilot.go): the
/// GitHub device-code flow, the 15-minute cache cap, the strict expiry
/// comparison, and the JSON/form token response matrix.
/// </summary>
public sealed class CopilotOAuthServiceTests
{
    private readonly AntigravityTestClock _clock = new();
    private readonly ScriptedExecutor _executor = new();
    private readonly ScriptedProxyExecutorFactory _proxyExecutors = new();
    private readonly CopilotDeviceFlowCache _states;

    public CopilotOAuthServiceTests()
    {
        _states = new CopilotDeviceFlowCache(_clock);
        _proxyExecutors.Executor = _executor;
    }

    // ---------------- start ----------------

    [Fact]
    public async Task StartBuildsTheDeviceCodeRequestExactly()
    {
        ArrangeDeviceCode(expiresIn: 900);
        var service = CreateService();

        var result = await service.StartAsync(proxy: null);

        Assert.Equal(32, Base64Url.DecodeFromChars(result.SessionId).Length);

        var request = Assert.Single(_executor.Requests);
        Assert.Equal(CopilotOAuthService.GithubDeviceCodeUrl, request.Url);
        Assert.Equal("POST", request.Method);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Equal("application/json", request.Headers!["Accept"][0]);
        Assert.Equal(
            "client_id=" + CopilotOAuthService.DefaultGithubCopilotClientId
            + "&scope=read%3Auser",
            Encoding.UTF8.GetString(request.Body!));
    }

    [Fact]
    public async Task StartReturnsTheDeviceFlowFields()
    {
        ArrangeDeviceCode(expiresIn: 900, interval: 5);
        var service = CreateService();

        var result = await service.StartAsync(proxy: null);

        Assert.Equal("USER-1", result.UserCode);
        Assert.Equal("https://github.com/login/device", result.VerificationUri);
        Assert.Equal(900, result.ExpiresIn);
        Assert.Equal(5, result.Interval);

        var stored = await _states.GetAsync(CopilotOAuthService.CacheKey(result.SessionId));
        Assert.NotNull(stored);
        Assert.Equal("device-1", stored!.DeviceCode);
        Assert.Equal("USER-1", stored.UserCode);
        Assert.Equal(900, stored.ExpiresIn);
        Assert.Equal(5, stored.Interval);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds(), stored.CreatedAt);
    }

    [Fact]
    public async Task StartUsesTheClientIdSeam()
    {
        ArrangeDeviceCode();
        var service = CreateService(clientId: () => "custom-client");

        _ = await service.StartAsync(proxy: null);

        Assert.Equal(
            "client_id=custom-client&scope=read%3Auser",
            Encoding.UTF8.GetString(_executor.Requests[0].Body!));
    }

    [Fact]
    public void DefaultClientIdMatchesTheReference()
    {
        // The reference value (copilot.go:31); the environment can override it,
        // so only the fallback constant is asserted here.
        Assert.Equal("Iv1.b507a08c87ecfe98", CopilotOAuthService.DefaultGithubCopilotClientId);
        Assert.Equal("read:user", CopilotOAuthService.GithubCopilotScope);
        Assert.Equal(
            "urn:ietf:params:oauth:grant-type:device_code",
            CopilotOAuthService.DeviceGrantType);
        Assert.Equal("copilot:oauth", CopilotOAuthService.CacheKeyPrefix);
    }

    [Fact]
    public async Task StartCapsTheCacheLifetimeAtFifteenMinutes()
    {
        ArrangeDeviceCode(expiresIn: 3600);
        var service = CreateService();

        var result = await service.StartAsync(proxy: null);
        var key = CopilotOAuthService.CacheKey(result.SessionId);

        _clock.Advance(TimeSpan.FromMinutes(15));
        Assert.NotNull(await _states.GetAsync(key));

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await _states.GetAsync(key));
    }

    [Fact]
    public async Task StartUsesTheCacheDefaultForAZeroExpiry()
    {
        ArrangeDeviceCode(expiresIn: 0);
        var service = CreateService();

        var result = await service.StartAsync(proxy: null);
        var key = CopilotOAuthService.CacheKey(result.SessionId);

        // go-cache: a zero duration selects the five-minute default.
        _clock.Advance(CopilotDeviceFlowCache.DefaultExpiration);
        Assert.NotNull(await _states.GetAsync(key));

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await _states.GetAsync(key));
    }

    [Fact]
    public async Task StartWrapsANonSuccessfulDeviceCodeResponse()
    {
        _executor.OnDo = (call, request, ct) => Text(503, "service unavailable", "text/plain");
        var service = CreateService();

        var error = await Assert.ThrowsAsync<CopilotOAuthException>(
            () => service.StartAsync(proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(
            "failed to request device code: device code request failed with status 503: service unavailable",
            error.Message);
    }

    [Fact]
    public async Task StartWrapsATransportFailure()
    {
        _executor.OnDo = (call, request, ct) => throw new HttpClientError(
            "POST",
            CopilotOAuthService.GithubDeviceCodeUrl,
            500,
            "500 Internal Server Error",
            Encoding.UTF8.GetBytes("{}"));
        var service = CreateService();

        var error = await Assert.ThrowsAsync<CopilotOAuthException>(
            () => service.StartAsync(proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(
            "failed to request device code: device code request failed: POST - "
            + CopilotOAuthService.GithubDeviceCodeUrl
            + " with status 500 Internal Server Error",
            error.Message);
    }

    [Fact]
    public async Task StartWrapsAParseFailure()
    {
        _executor.OnDo = (call, request, ct) => Text(200, "{", "application/json");
        var service = CreateService();

        var error = await Assert.ThrowsAsync<CopilotOAuthException>(
            () => service.StartAsync(proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.StartsWith(
            "failed to request device code: failed to parse device code response: ",
            error.Message);
    }

    [Fact]
    public async Task StartRejectsAMissingDeviceCode()
    {
        _executor.OnDo = (call, request, ct) => Json(200, "{\"user_code\":\"USER-1\"}");
        var service = CreateService();

        var error = await Assert.ThrowsAsync<CopilotOAuthException>(
            () => service.StartAsync(proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(
            "failed to request device code: device code not received from GitHub",
            error.Message);
    }

    [Fact]
    public async Task StartWrapsACacheFailure()
    {
        ArrangeDeviceCode();
        var service = new CopilotOAuthService(
            new FailingStateCache(),
            _executor,
            _proxyExecutors,
            _clock);

        var error = await Assert.ThrowsAsync<CopilotOAuthException>(
            () => service.StartAsync(proxy: null));

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("failed to save device flow state: boom", error.Message);
    }

    [Fact]
    public async Task StartRoutesTheUrlProxyThroughTheFactory()
    {
        var proxyExecutor = new ScriptedExecutor { OnDo = DeviceCodeHandler(900) };
        _proxyExecutors.Executor = proxyExecutor;
        var service = CreateService();

        _ = await service.StartAsync(new ProxyConfig
        {
            Type = ProxyType.Url,
            Url = "http://proxy.local:8080",
        });

        Assert.Empty(_executor.Requests);
        Assert.Single(proxyExecutor.Requests);
        var configured = Assert.Single(_proxyExecutors.Requests);
        Assert.Equal("http://proxy.local:8080", configured.Url);
    }

    [Theory]
    [InlineData("disabled", "http://proxy.local:8080")]
    [InlineData("environment", "http://proxy.local:8080")]
    [InlineData("url", "")]
    public async Task StartIgnoresProxiesOutsideTheUrlBranch(string type, string url)
    {
        ArrangeDeviceCode();
        var service = CreateService();

        _ = await service.StartAsync(new ProxyConfig { Type = type, Url = url });

        Assert.Empty(_proxyExecutors.Requests);
        Assert.Single(_executor.Requests);
    }

    // ---------------- poll: session checks ----------------

    [Fact]
    public async Task PollRejectsAnUnknownSession()
    {
        var service = CreateService();

        var error = await Assert.ThrowsAsync<CopilotOAuthException>(
            () => service.PollAsync("unknown", proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("invalid or expired session", error.Message);
    }

    [Fact]
    public async Task PollTreatsTheExpirationDeadlineAsLive()
    {
        _executor.OnDo = (call, request, ct) => call == 0
            ? DeviceCodeResponse(expiresIn: 0)
            : AccessToken("application/json", "{\"error\":\"authorization_pending\"}");
        var service = CreateService();
        var start = await service.StartAsync(proxy: null);

        // copilot.go:288: now > created+expires_in is strict; equal is live.
        var result = await service.PollAsync(start.SessionId, proxy: null);

        Assert.Equal("pending", result.Status);
    }

    [Fact]
    public async Task PollExpiresTheDeviceCodeAndDeletesIt()
    {
        ArrangeDeviceCode(expiresIn: 0);
        var service = CreateService();
        var start = await service.StartAsync(proxy: null);

        _clock.Advance(TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<CopilotOAuthException>(
            () => service.PollAsync(start.SessionId, proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("device code expired", error.Message);
        Assert.Null(await _states.GetAsync(CopilotOAuthService.CacheKey(start.SessionId)));
        Assert.Empty(_executor.Requests.Skip(1));
    }

    // ---------------- poll: token request ----------------

    [Fact]
    public async Task PollBuildsTheAccessTokenRequestExactly()
    {
        ArrangeDeviceCode();
        _executor.OnDo = (call, request, ct) => call == 0
            ? DeviceCodeResponse(900)
            : AccessToken("application/json", "{\"access_token\":\"gho_1\"}");
        var service = CreateService();
        var start = await service.StartAsync(proxy: null);

        _ = await service.PollAsync(start.SessionId, proxy: null);

        var request = _executor.Requests[1];
        Assert.Equal(CopilotOAuthService.GithubAccessTokenUrl, request.Url);
        Assert.Equal("POST", request.Method);
        Assert.Equal("application/x-www-form-urlencoded", request.ContentType);
        Assert.Equal("application/json", request.Headers!["Accept"][0]);
        Assert.Equal(
            "client_id=" + CopilotOAuthService.DefaultGithubCopilotClientId
            + "&device_code=device-1"
            + "&grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code",
            Encoding.UTF8.GetString(request.Body!));
    }

    [Fact]
    public async Task PollReturnsPending()
    {
        var result = await PollWithError("authorization_pending");

        Assert.Equal(string.Empty, result.Token);
        Assert.Equal(string.Empty, result.TokenType);
        Assert.Equal(string.Empty, result.Scope);
        Assert.Equal("pending", result.Status);
        Assert.Equal(
            "Authorization pending. User has not yet authorized the device.",
            result.Message);
    }

    [Fact]
    public async Task PollReturnsSlowDown()
    {
        var result = await PollWithError("slow_down");

        Assert.Equal("slow_down", result.Status);
        Assert.Equal("Polling too fast. Please slow down.", result.Message);
    }

    [Fact]
    public async Task PollExpiresOnTheExpiredTokenError()
    {
        var (service, start) = ArrangePending("expired_token");

        var error = await Assert.ThrowsAsync<CopilotOAuthException>(
            () => service.PollAsync(start.SessionId, proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("device code expired", error.Message);
        Assert.Null(await _states.GetAsync(CopilotOAuthService.CacheKey(start.SessionId)));
    }

    [Fact]
    public async Task PollRejectsAnAccessDenial()
    {
        var (service, start) = ArrangePending("access_denied");

        var error = await Assert.ThrowsAsync<CopilotOAuthException>(
            () => service.PollAsync(start.SessionId, proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("access denied by user", error.Message);
        Assert.Null(await _states.GetAsync(CopilotOAuthService.CacheKey(start.SessionId)));
    }

    [Fact]
    public async Task PollSurfacesAnUnknownOAuthError()
    {
        var (service, start) = ArrangePending(
            "bad_verification_code",
            errorDescription: "The device code is incorrect.");

        var error = await Assert.ThrowsAsync<CopilotOAuthException>(
            () => service.PollAsync(start.SessionId, proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(
            "OAuth error: bad_verification_code - The device code is incorrect.",
            error.Message);
    }

    [Fact]
    public async Task PollReturnsTheJsonTokenAndDeletesTheSession()
    {
        var (service, start) = ArrangePending(
            error: null,
            body: "{\"access_token\":\"gho_1\",\"token_type\":\"bearer\",\"scope\":\"read:user\"}");

        var result = await service.PollAsync(start.SessionId, proxy: null);

        Assert.Equal("gho_1", result.Token);
        Assert.Equal("bearer", result.TokenType);
        Assert.Equal("read:user", result.Scope);
        Assert.Equal("complete", result.Status);
        Assert.Equal("Authorization complete. Access token received.", result.Message);
        Assert.Null(await _states.GetAsync(CopilotOAuthService.CacheKey(start.SessionId)));
    }

    [Fact]
    public async Task PollParsesAFormEncodedTokenResponse()
    {
        var (service, start) = ArrangePending(
            error: null,
            contentType: "application/x-www-form-urlencoded; charset=utf-8",
            body: "access_token=gho_1&scope=read%3Auser&token_type=bearer");

        var result = await service.PollAsync(start.SessionId, proxy: null);

        Assert.Equal("gho_1", result.Token);
        Assert.Equal("bearer", result.TokenType);
        Assert.Equal("read:user", result.Scope);
        Assert.Equal("complete", result.Status);
    }

    [Fact]
    public async Task PollParsesAFormEncodedError()
    {
        var (service, start) = ArrangePending(
            error: null,
            contentType: "application/x-www-form-urlencoded",
            body: "error=authorization_pending&error_description=pending");

        var result = await service.PollAsync(start.SessionId, proxy: null);

        Assert.Equal("pending", result.Status);
    }

    [Fact]
    public async Task PollRejectsAnEmptyResponse()
    {
        var (service, start) = ArrangePending(error: null, body: "{}");

        var error = await Assert.ThrowsAsync<CopilotOAuthException>(
            () => service.PollAsync(start.SessionId, proxy: null));

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("unexpected response from GitHub", error.Message);
    }

    [Fact]
    public async Task PollWrapsAJsonParseFailure()
    {
        var (service, start) = ArrangePending(error: null, body: "{");

        var error = await Assert.ThrowsAsync<CopilotOAuthException>(
            () => service.PollAsync(start.SessionId, proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.StartsWith(
            "token poll failed: failed to parse access token JSON response: ",
            error.Message);
    }

    [Fact]
    public async Task PollWrapsAFormParseFailure()
    {
        var (service, start) = ArrangePending(
            error: null,
            contentType: "application/x-www-form-urlencoded",
            body: "access_token=%zz");

        var error = await Assert.ThrowsAsync<CopilotOAuthException>(
            () => service.PollAsync(start.SessionId, proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(
            "token poll failed: failed to parse access token form response: invalid URL escape \"%zz\"",
            error.Message);
    }

    [Fact]
    public async Task PollWrapsANonSuccessfulTokenResponse()
    {
        var (service, start) = ArrangePending(error: null, statusCode: 503, body: string.Empty);

        var error = await Assert.ThrowsAsync<CopilotOAuthException>(
            () => service.PollAsync(start.SessionId, proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(
            "token poll failed: access token request failed with status 503",
            error.Message);
    }

    [Fact]
    public async Task PollRoutesTheUrlProxyThroughTheFactory()
    {
        var proxyExecutor = new ScriptedExecutor();
        proxyExecutor.OnDo = (call, request, ct) => call == 0
            ? AccessToken("application/json", "{\"access_token\":\"gho_1\"}")
            : throw new InvalidOperationException("unexpected poll count " + call);
        _proxyExecutors.Executor = proxyExecutor;

        ArrangeDeviceCode();
        var service = CreateService();
        var start = await service.StartAsync(proxy: null);

        var result = await service.PollAsync(
            start.SessionId,
            new ProxyConfig { Type = ProxyType.Url, Url = "http://proxy.local:8080" });

        Assert.Equal("complete", result.Status);
        Assert.Empty(_executor.Requests.Skip(1));
        var configured = Assert.Single(_proxyExecutors.Requests);
        Assert.Equal("http://proxy.local:8080", configured.Url);
    }

    // ---------------- helpers ----------------

    private CopilotOAuthService CreateService(Func<string>? clientId = null)
        => new(_states, _executor, _proxyExecutors, _clock, clientId);

    private void ArrangeDeviceCode(long expiresIn = 900, long interval = 5)
    {
        _executor.OnDo = (call, request, ct) => DeviceCodeResponse(expiresIn, interval);
    }

    private (CopilotOAuthService Service, CopilotStartResult Start) ArrangePending(
        string? error,
        string? errorDescription = null,
        string? contentType = "application/json",
        string? body = null,
        int statusCode = 200)
    {
        ArrangeDeviceCode();
        var tokenBody = body ?? "{\"error\":\"" + error + "\",\"error_description\":\""
            + errorDescription + "\"}";
        _executor.OnDo = (call, request, ct) => call == 0
            ? DeviceCodeResponse(900)
            : Text(statusCode, tokenBody, contentType);

        var service = CreateService();
        var start = service.StartAsync(proxy: null).GetAwaiter().GetResult();
        return (service, start);
    }

    private async Task<CopilotPollResult> PollWithError(string error)
    {
        var (service, start) = ArrangePending(error);
        return await service.PollAsync(start.SessionId, proxy: null);
    }

    private static Func<int, HttpRequestData, CancellationToken, HttpResponseData>
        DeviceCodeHandler(long expiresIn)
        => (call, request, ct) => DeviceCodeResponse(expiresIn, 5);

    private static HttpResponseData DeviceCodeResponse(long expiresIn, long interval = 5)
        => Json(
            200,
            "{\"device_code\":\"device-1\",\"user_code\":\"USER-1\","
            + "\"verification_uri\":\"https://github.com/login/device\","
            + "\"expires_in\":" + expiresIn + ",\"interval\":" + interval + "}");

    private static HttpResponseData AccessToken(string contentType, string body)
        => Text(200, body, contentType);

    private static HttpResponseData Json(int statusCode, string body)
        => Text(statusCode, body, "application/json");

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

    private sealed class FailingStateCache : ICopilotDeviceFlowCache
    {
        public Task<CopilotDeviceFlowState?> GetAsync(
            string key,
            CancellationToken cancellationToken = default)
            => Task.FromResult<CopilotDeviceFlowState?>(null);

        public Task SetAsync(
            string key,
            CopilotDeviceFlowState value,
            TimeSpan expiration,
            CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("boom"));

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
