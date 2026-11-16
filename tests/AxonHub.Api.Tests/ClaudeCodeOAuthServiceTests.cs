using System.Buffers.Text;
using System.Net;
using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Anthropic.ClaudeCode;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the behavior of api/claudecode.go (internal/server/api/claudecode.go):
/// the PKCE session, the delete-before-parse ordering, the fragment-first state
/// lookup, and the JSON token exchange.
/// </summary>
public sealed class ClaudeCodeOAuthServiceTests
{
    private readonly AntigravityTestClock _clock = new();
    private readonly ScriptedExecutor _executor = new();
    private readonly ScriptedProxyExecutorFactory _proxyExecutors = new();
    private readonly ClaudeCodeOAuthStateCache _states;

    public ClaudeCodeOAuthServiceTests()
    {
        _states = new ClaudeCodeOAuthStateCache(_clock);
        _proxyExecutors.Executor = _executor;
    }

    // ---------------- start ----------------

    [Fact]
    public async Task StartStoresTheSession()
    {
        var service = CreateService();

        var result = await service.StartAsync();

        Assert.Equal(32, Base64Url.DecodeFromChars(result.SessionId).Length);

        var stored = await _states.GetAsync(ClaudeCodeOAuthService.CacheKey(result.SessionId));
        Assert.NotNull(stored);
        Assert.Equal(64, Base64Url.DecodeFromChars(stored!.CodeVerifier).Length);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds(), stored.CreatedAt);
        Assert.Equal(
            Pkce.GenerateCodeChallenge(stored.CodeVerifier),
            QueryValue(result.AuthUrl, "code_challenge"));
    }

    [Fact]
    public async Task StartBuildsTheOfficialAuthorizeUrl()
    {
        var service = CreateService();

        var result = await service.StartAsync();

        Assert.StartsWith(
            ClaudeCodeConstants.AuthorizeUrl + "?",
            result.AuthUrl,
            StringComparison.Ordinal);
        Assert.Equal("code", QueryValue(result.AuthUrl, "response_type"));
        Assert.Equal(ClaudeCodeConstants.ClientId, QueryValue(result.AuthUrl, "client_id"));
        Assert.Equal(ClaudeCodeConstants.RedirectUri, QueryValue(result.AuthUrl, "redirect_uri"));
        Assert.Equal(ClaudeCodeConstants.Scopes, QueryValue(result.AuthUrl, "scope"));
        Assert.Equal("S256", QueryValue(result.AuthUrl, "code_challenge_method"));
        Assert.Equal(result.SessionId, QueryValue(result.AuthUrl, "state"));

        // url.Values.Encode sorts the keys.
        string[] keys =
        [
            "client_id",
            "code_challenge",
            "code_challenge_method",
            "redirect_uri",
            "response_type",
            "scope",
            "state",
        ];
        var positions = keys
            .Select(key => result.AuthUrl.IndexOf(key + "=", StringComparison.Ordinal))
            .ToList();
        Assert.All(positions, position => Assert.True(position >= 0));
        for (var i = 1; i < positions.Count; i++)
        {
            Assert.True(positions[i - 1] < positions[i]);
        }
    }

    [Fact]
    public async Task StartWrapsACacheFailure()
    {
        var service = new ClaudeCodeOAuthService(
            new FailingStateCache(),
            _executor,
            _proxyExecutors,
            _clock);

        var error = await Assert.ThrowsAsync<ClaudeCodeOAuthException>(
            () => service.StartAsync());

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("failed to save oauth state: boom", error.Message);
    }

    [Fact]
    public async Task SessionsSurviveExactlyTenMinutes()
    {
        ArrangeToken();
        var service = CreateService();
        var start = await service.StartAsync();

        _clock.Advance(ClaudeCodeOAuthService.StateExpiration);

        _ = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Single(_executor.Requests);
    }

    [Fact]
    public async Task SessionsExpireAfterTenMinutes()
    {
        var service = CreateService();
        var start = await service.StartAsync();

        _clock.Advance(ClaudeCodeOAuthService.StateExpiration + TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<ClaudeCodeOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal("invalid or expired oauth session", error.Message);
    }

    // ---------------- exchange: session lifecycle ----------------

    [Fact]
    public async Task ExchangeRejectsAnUnknownSession()
    {
        var service = CreateService();

        var error = await Assert.ThrowsAsync<ClaudeCodeOAuthException>(
            () => service.ExchangeAsync("unknown", "http://localhost/callback?code=abc", proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("invalid or expired oauth session", error.Message);
    }

    [Fact]
    public async Task ExchangeConsumesTheSessionBeforeParsingTheCallback()
    {
        var service = CreateService();
        var start = await service.StartAsync();

        // claudecode.go:190-192 deletes first; the malformed callback then
        // fails with the parse error and the session is already gone.
        var error = await Assert.ThrowsAsync<ClaudeCodeOAuthException>(
            () => service.ExchangeAsync(start.SessionId, "not-a-url", proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("callback_url must be a full URL", error.Message);
        Assert.Null(await _states.GetAsync(ClaudeCodeOAuthService.CacheKey(start.SessionId)));
    }

    [Fact]
    public async Task ExchangeConsumesTheSessionOnAStateMismatch()
    {
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<ClaudeCodeOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", "another-state"),
                proxy: null));

        Assert.Equal("oauth state mismatch", error.Message);
        Assert.Null(await _states.GetAsync(ClaudeCodeOAuthService.CacheKey(start.SessionId)));
    }

    [Fact]
    public async Task ExchangeConsumesTheSessionOnSuccess()
    {
        ArrangeToken();
        var service = CreateService();
        var start = await service.StartAsync();

        _ = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Null(await _states.GetAsync(ClaudeCodeOAuthService.CacheKey(start.SessionId)));
    }

    [Fact]
    public async Task ExchangeTrimsTheCallbackUrl()
    {
        ArrangeToken();
        var service = CreateService();
        var start = await service.StartAsync();

        _ = await service.ExchangeAsync(
            start.SessionId,
            "  " + Callback("abc", start.SessionId) + "  ",
            proxy: null);

        Assert.Single(_executor.Requests);
    }

    // ---------------- exchange: callback parsing ----------------

    [Theory]
    [InlineData("localhost:54545/callback?code=abc&state=s")]
    [InlineData("ftp://localhost/callback?code=abc&state=s")]
    [InlineData("")]
    public async Task ExchangeRequiresAnHttpPrefix(string callbackUrl)
    {
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<ClaudeCodeOAuthException>(
            () => service.ExchangeAsync(start.SessionId, callbackUrl, proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("callback_url must be a full URL", error.Message);
    }

    [Fact]
    public void ParseCallbackUrlRejectsAnUnparsableUrl()
    {
        var error = Assert.Throws<ClaudeCodeOAuthException>(
            () => ClaudeCodeOAuthService.ParseCallbackUrl("http://[::1"));

        Assert.Equal(400, error.StatusCode);
        Assert.StartsWith("invalid callback_url: ", error.Message);
    }

    [Fact]
    public void ParseCallbackUrlRequiresCode()
    {
        var error = Assert.Throws<ClaudeCodeOAuthException>(
            () => ClaudeCodeOAuthService.ParseCallbackUrl("http://localhost/callback?state=s"));

        Assert.Equal("code parameter not found in callback_url", error.Message);
    }

    [Fact]
    public void ParseCallbackUrlRequiresState()
    {
        var error = Assert.Throws<ClaudeCodeOAuthException>(
            () => ClaudeCodeOAuthService.ParseCallbackUrl("http://localhost/callback?code=abc"));

        Assert.Equal(
            "state parameter not found in callback_url (should be after # or in query)",
            error.Message);
    }

    [Fact]
    public void ParseCallbackUrlReadsTheStateFromTheFragment()
    {
        var (code, state) = ClaudeCodeOAuthService.ParseCallbackUrl(
            "http://localhost:54545/callback?code=abc#fragment-state");

        Assert.Equal("abc", code);
        Assert.Equal("fragment-state", state);
    }

    [Fact]
    public void ParseCallbackUrlPrefersTheFragmentOverTheQuery()
    {
        var (_, state) = ClaudeCodeOAuthService.ParseCallbackUrl(
            "http://localhost/callback?code=abc&state=query-state#fragment-state");

        Assert.Equal("fragment-state", state);
    }

    [Fact]
    public void ParseCallbackUrlPercentDecodesTheFragment()
    {
        var (_, state) = ClaudeCodeOAuthService.ParseCallbackUrl(
            "http://localhost/callback?code=abc#state%2B1");

        Assert.Equal("state+1", state);
    }

    [Fact]
    public void ParseCallbackUrlFallsBackToTheQueryState()
    {
        var (_, state) = ClaudeCodeOAuthService.ParseCallbackUrl(
            "http://localhost/callback?code=abc&state=query-state");

        Assert.Equal("query-state", state);
    }

    [Fact]
    public void ParseCallbackUrlTreatsAnEmptyFragmentAsAbsent()
    {
        // Go's url.URL.Fragment is empty for a bare "#", so the query fallback
        // applies; RawQuery stops before the delimiter.
        var (_, state) = ClaudeCodeOAuthService.ParseCallbackUrl(
            "http://localhost/callback?code=abc&state=query-state#");

        Assert.Equal("query-state", state);
    }

    // ---------------- exchange: token request ----------------

    [Fact]
    public async Task ExchangeBuildsTheJsonTokenRequestExactly()
    {
        ArrangeToken();
        var service = CreateService();
        var start = await service.StartAsync();
        var stored = await _states.GetAsync(ClaudeCodeOAuthService.CacheKey(start.SessionId));

        _ = await service.ExchangeAsync(
            start.SessionId,
            Callback("code-1", start.SessionId),
            proxy: null);

        var tokenRequest = Assert.Single(_executor.Requests);
        Assert.Equal(ClaudeCodeConstants.TokenUrl, tokenRequest.Url);
        Assert.Equal("POST", tokenRequest.Method);
        Assert.Equal("application/json", tokenRequest.Headers!["Content-Type"][0]);
        Assert.Equal("application/json", tokenRequest.Headers!["Accept"][0]);
        Assert.Equal(ClaudeCodeConstants.UserAgent, tokenRequest.Headers!["User-Agent"][0]);
        Assert.Equal(
            "{\"client_id\":\"" + ClaudeCodeConstants.ClientId + "\","
            + "\"code\":\"code-1\","
            + "\"code_verifier\":\"" + stored!.CodeVerifier + "\","
            + "\"grant_type\":\"authorization_code\","
            + "\"redirect_uri\":\"" + ClaudeCodeConstants.RedirectUri + "\","
            + "\"state\":\"" + start.SessionId + "\"}",
            Encoding.UTF8.GetString(tokenRequest.Body!));
    }

    [Fact]
    public async Task ExchangeWrapsTheProviderErrorTwice()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"error\":\"invalid_grant\",\"error_description\":\"bad code\"}");
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<ClaudeCodeOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal("token exchange failed: token exchange failed: invalid_grant - bad code", error.Message);
    }

    [Fact]
    public async Task ExchangeWrapsTransportErrors()
    {
        _executor.OnDo = (call, request, ct) => throw new HttpClientError(
            "POST",
            ClaudeCodeConstants.TokenUrl,
            500,
            "500 Internal Server Error",
            Encoding.UTF8.GetBytes("{}"));
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<ClaudeCodeOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(
            "token exchange failed: POST - " + ClaudeCodeConstants.TokenUrl
            + " with status 500 Internal Server Error",
            error.Message);
    }

    [Fact]
    public async Task ExchangeReturnsTheCredentialsJson()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\","
            + "\"expires_in\":3600,\"token_type\":\"Bearer\",\"scope\":\"user:inference\"}");
        var service = CreateService();
        var start = await service.StartAsync();

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Contains(
            "\"client_id\":\"" + ClaudeCodeConstants.ClientId + "\"",
            result.Credentials,
            StringComparison.Ordinal);
        Assert.Contains("\"access_token\":\"access-1\"", result.Credentials, StringComparison.Ordinal);
        Assert.Contains("\"scopes\":[\"user:inference\"]", result.Credentials, StringComparison.Ordinal);
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

        var service = CreateService();
        var start = await service.StartAsync();

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            new ProxyConfig
            {
                Type = ProxyType.Url,
                Url = "http://proxy.local:8080",
                Username = "user",
                Password = "secret",
            });

        Assert.Contains("\"access_token\":\"access-1\"", result.Credentials, StringComparison.Ordinal);
        var configured = Assert.Single(_proxyExecutors.Requests);
        Assert.Equal("http://proxy.local:8080", configured.Url);
        Assert.Equal("user", configured.Username);

        Assert.Empty(_executor.Requests);
        Assert.Single(proxyExecutor.Requests);
    }

    [Theory]
    [InlineData("disabled", "http://proxy.local:8080")]
    [InlineData("environment", "http://proxy.local:8080")]
    [InlineData("url", "")]
    public async Task ExchangeIgnoresProxiesOutsideTheUrlBranch(string type, string url)
    {
        ArrangeToken();
        var service = CreateService();
        var start = await service.StartAsync();

        _ = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            new ProxyConfig { Type = type, Url = url });

        Assert.Empty(_proxyExecutors.Requests);
        Assert.Single(_executor.Requests);
    }

    // ---------------- helpers ----------------

    private ClaudeCodeOAuthService CreateService()
        => new(_states, _executor, _proxyExecutors, _clock);

    private void ArrangeToken()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\"}");
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

    private static string Callback(string code, string state)
        => "http://localhost:54545/callback?code=" + code + "#" + state;

    private static string QueryValue(string url, string key)
        => HttpClientUtils.ParseQuery(Query(url))[key][0];

    private static string Query(string url)
    {
        var question = url.IndexOf('?', StringComparison.Ordinal);
        return url[(question + 1)..];
    }

    private sealed class FailingStateCache : IClaudeCodeOAuthStateCache
    {
        public Task<ClaudeCodeOAuthState?> GetAsync(
            string key,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ClaudeCodeOAuthState?>(null);

        public Task SetAsync(
            string key,
            ClaudeCodeOAuthState value,
            TimeSpan expiration,
            CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("boom"));

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
