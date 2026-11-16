using System.Buffers.Text;
using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.OpenAI.Codex;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the behavior of api/codex.go (internal/server/api/codex.go): the PKCE
/// session, the callback exchange (including the early session delete), the
/// token request shape, and the auth.json decode.
/// </summary>
public sealed class CodexOAuthServiceTests
{
    private readonly AntigravityTestClock _clock = new();
    private readonly ScriptedExecutor _executor = new();
    private readonly ScriptedProxyExecutorFactory _proxyExecutors = new();
    private readonly CodexOAuthStateCache _states;

    public CodexOAuthServiceTests()
    {
        _states = new CodexOAuthStateCache(_clock);
        _proxyExecutors.Executor = _executor;
    }

    // ---------------- start ----------------

    [Fact]
    public async Task StartStoresA32ByteStateAnd64ByteVerifier()
    {
        var service = CreateService();

        var result = await service.StartAsync();

        Assert.Equal(32, Base64Url.DecodeFromChars(result.SessionId).Length);

        var stored = await _states.GetAsync(CodexOAuthService.CacheKey(result.SessionId));
        Assert.NotNull(stored);
        Assert.Equal(64, Base64Url.DecodeFromChars(stored!.CodeVerifier).Length);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds(), stored.CreatedAt);
        Assert.Equal(
            Pkce.GenerateCodeChallenge(stored.CodeVerifier),
            QueryValue(result.AuthUrl, "code_challenge"));
    }

    [Fact]
    public async Task StartBuildsTheAuthorizeUrlInSortedOrder()
    {
        var service = CreateService();

        var result = await service.StartAsync();
        var stored = await _states.GetAsync(CodexOAuthService.CacheKey(result.SessionId));

        Assert.StartsWith(CodexConstants.AuthorizeUrl + "?", result.AuthUrl, StringComparison.Ordinal);
        Assert.Equal(CodexConstants.ClientId, QueryValue(result.AuthUrl, "client_id"));
        Assert.Equal(CodexConstants.RedirectUri, QueryValue(result.AuthUrl, "redirect_uri"));
        Assert.Equal(CodexConstants.Scopes, QueryValue(result.AuthUrl, "scope"));
        Assert.Equal("code", QueryValue(result.AuthUrl, "response_type"));
        Assert.Equal("S256", QueryValue(result.AuthUrl, "code_challenge_method"));
        Assert.Equal("true", QueryValue(result.AuthUrl, "id_token_add_organizations"));
        Assert.Equal("true", QueryValue(result.AuthUrl, "codex_cli_simplified_flow"));
        Assert.Equal(result.SessionId, QueryValue(result.AuthUrl, "state"));
        Assert.Equal(
            Pkce.GenerateCodeChallenge(stored!.CodeVerifier),
            QueryValue(result.AuthUrl, "code_challenge"));

        // The reference test asserts the originator parameter stayed gone.
        var query = HttpClientUtils.ParseQuery(Query(result.AuthUrl));
        Assert.False(query.ContainsKey("originator"));
        Assert.False(query.ContainsKey("access_type"));
        Assert.False(query.ContainsKey("prompt"));

        // url.Values.Encode sorts the keys.
        string[] keys =
        [
            "client_id",
            "code_challenge",
            "code_challenge_method",
            "codex_cli_simplified_flow",
            "id_token_add_organizations",
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
    public async Task StartUsesFreshRandomValuesPerCall()
    {
        var service = CreateService();

        var first = await service.StartAsync();
        var second = await service.StartAsync();

        Assert.NotEqual(first.SessionId, second.SessionId);

        var firstState = await _states.GetAsync(CodexOAuthService.CacheKey(first.SessionId));
        var secondState = await _states.GetAsync(CodexOAuthService.CacheKey(second.SessionId));
        Assert.NotEqual(firstState!.CodeVerifier, secondState!.CodeVerifier);
    }

    [Fact]
    public async Task StartWrapsACacheFailure()
    {
        var service = new CodexOAuthService(
            new FailingStateCache(),
            _executor,
            _proxyExecutors,
            _clock);

        var error = await Assert.ThrowsAsync<CodexOAuthException>(
            () => service.StartAsync());

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("failed to save oauth state: boom", error.Message);
    }

    // ---------------- exchange: session and callback ----------------

    [Fact]
    public async Task ExchangeRejectsAnUnknownSession()
    {
        var service = CreateService();

        var error = await Assert.ThrowsAsync<CodexOAuthException>(
            () => service.ExchangeAsync(
                "unknown",
                "http://localhost:1455/auth/callback?code=abc&state=unknown",
                proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("invalid or expired oauth session", error.Message);
    }

    [Fact]
    public async Task ExchangeRejectsAnExpiredSession()
    {
        var service = CreateService();
        var start = await service.StartAsync();

        _clock.Advance(CodexOAuthService.StateExpiration + TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<CodexOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal("invalid or expired oauth session", error.Message);
    }

    [Fact]
    public async Task ExchangeKeepsTheStateAtTheExactDeadline()
    {
        ArrangeToken();
        var service = CreateService();
        var start = await service.StartAsync();

        // go-cache expires with time.Now().After(expiresAt), so the deadline
        // itself is still live.
        _clock.Advance(CodexOAuthService.StateExpiration);

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Contains("\"access_token\":\"access-1\"", result.Credentials, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("localhost:1455/auth/callback?code=abc&state=s")]
    [InlineData("ftp://localhost/auth/callback?code=abc&state=s")]
    public async Task ExchangeRequiresAFullCallbackUrl(string callbackUrl)
    {
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<CodexOAuthException>(
            () => service.ExchangeAsync(start.SessionId, callbackUrl, proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("callback_url must be a full URL", error.Message);
    }

    [Fact]
    public async Task ExchangeTrimsTheCallbackUrl()
    {
        ArrangeToken();
        var service = CreateService();
        var start = await service.StartAsync();

        var result = await service.ExchangeAsync(
            start.SessionId,
            "  " + Callback("abc", start.SessionId) + "  ",
            proxy: null);

        Assert.Contains("\"access_token\":\"access-1\"", result.Credentials, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeRejectsAMalformedCallbackUrl()
    {
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<CodexOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                "http://[::1/auth/callback?code=abc&state=" + start.SessionId,
                proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.StartsWith("invalid callback_url: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeRequiresTheCodeParameter()
    {
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<CodexOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                "http://localhost/auth/callback?state=" + start.SessionId,
                proxy: null));

        Assert.Equal("code parameter not found in callback_url", error.Message);
    }

    [Fact]
    public async Task ExchangeRequiresTheStateParameter()
    {
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<CodexOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                "http://localhost/auth/callback?code=abc",
                proxy: null));

        Assert.Equal("state parameter not found in callback_url", error.Message);
    }

    [Fact]
    public async Task ExchangeRejectsAStateMismatchAndConsumesTheSession()
    {
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<CodexOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", "another-state"),
                proxy: null));

        Assert.Equal("oauth state mismatch", error.Message);

        // codex.go deletes the session before validating the callback state.
        Assert.Null(await _states.GetAsync(CodexOAuthService.CacheKey(start.SessionId)));
    }

    [Fact]
    public async Task ExchangeConsumesTheSessionBeforeParsingTheCallback()
    {
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<CodexOAuthException>(
            () => service.ExchangeAsync(start.SessionId, "not-a-url", proxy: null));

        Assert.Equal("callback_url must be a full URL", error.Message);
        Assert.Null(await _states.GetAsync(CodexOAuthService.CacheKey(start.SessionId)));
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

        var error = await Assert.ThrowsAsync<CodexOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal("invalid or expired oauth session", error.Message);
    }

    // ---------------- exchange: token request ----------------

    [Fact]
    public async Task ExchangeBuildsTheTokenRequestExactly()
    {
        ArrangeToken();
        var service = CreateService();
        var start = await service.StartAsync();
        var stored = await _states.GetAsync(CodexOAuthService.CacheKey(start.SessionId));

        _ = await service.ExchangeAsync(
            start.SessionId,
            Callback("code-1", start.SessionId),
            proxy: null);

        var tokenRequest = Assert.Single(_executor.Requests);
        Assert.Equal(CodexConstants.TokenUrl, tokenRequest.Url);
        Assert.Equal("POST", tokenRequest.Method);
        Assert.Equal(
            "client_id=" + CodexConstants.ClientId
            + "&code=code-1"
            + "&code_verifier=" + stored!.CodeVerifier
            + "&grant_type=authorization_code"
            + "&redirect_uri=http%3A%2F%2Flocalhost%3A1455%2Fauth%2Fcallback",
            Encoding.UTF8.GetString(tokenRequest.Body!));
        Assert.Equal(
            "application/x-www-form-urlencoded",
            tokenRequest.Headers!["Content-Type"][0]);
        Assert.Equal("application/json", tokenRequest.Headers["Accept"][0]);
        Assert.False(tokenRequest.Headers.ContainsKey("User-Agent"));
    }

    [Fact]
    public async Task ExchangeWrapsTheProviderErrorTwice()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"error\":\"invalid_grant\",\"error_description\":\"bad code\"}");
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<CodexOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(
            "token exchange failed: token exchange failed: invalid_grant - bad code",
            error.Message);
        Assert.Null(await _states.GetAsync(CodexOAuthService.CacheKey(start.SessionId)));
    }

    [Fact]
    public async Task ExchangeWrapsTransportErrors()
    {
        _executor.OnDo = (call, request, ct) => throw new HttpClientError(
            "POST",
            CodexConstants.TokenUrl,
            500,
            "500 Internal Server Error",
            Encoding.UTF8.GetBytes("{}"));
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<CodexOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(
            "token exchange failed: POST - " + CodexConstants.TokenUrl
            + " with status 500 Internal Server Error",
            error.Message);
    }

    [Fact]
    public async Task ExchangeReturnsTheCredentialsJson()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\","
            + "\"token_type\":\"bearer\",\"scope\":\"openid email\"}");
        var service = CreateService();
        var start = await service.StartAsync();

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Equal(
            "{\"client_id\":\"" + CodexConstants.ClientId + "\","
            + "\"access_token\":\"access-1\","
            + "\"refresh_token\":\"refresh-1\","
            + "\"expires_at\":\"0001-01-01T00:00:00Z\","
            + "\"token_type\":\"bearer\","
            + "\"scopes\":[\"openid\",\"email\"]}",
            result.Credentials);
    }

    [Fact]
    public async Task ExchangeKeepsAnEmptyRefreshToken()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"access_token\":\"access-1\"}");
        var service = CreateService();
        var start = await service.StartAsync();

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Equal(
            "{\"client_id\":\"" + CodexConstants.ClientId + "\","
            + "\"access_token\":\"access-1\","
            + "\"refresh_token\":\"\","
            + "\"expires_at\":\"0001-01-01T00:00:00Z\"}",
            result.Credentials);
    }

    // ---------------- exchange: proxy ----------------

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
                DisableConnectionReuse = true,
            });

        Assert.Contains("\"access_token\":\"access-1\"", result.Credentials, StringComparison.Ordinal);
        var configured = Assert.Single(_proxyExecutors.Requests);
        Assert.Equal("http://proxy.local:8080", configured.Url);
        Assert.Equal("user", configured.Username);
        Assert.True(configured.DisableConnectionReuse);

        // The base executor never saw the exchange.
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

    // ---------------- decode auth.json ----------------

    [Fact]
    public void DecodeNormalizesTheCodexAuthFile()
    {
        var service = CreateService();

        var result = service.DecodeAuthJson(
            "{\"auth_mode\":\"chatgpt\","
            + "\"last_refresh\":\"2026-04-17T08:58:36.389Z\","
            + "\"tokens\":{\"access_token\":\"access\",\"refresh_token\":\"refresh\","
            + "\"id_token\":\"id\"}}");

        Assert.Equal(
            "{\"client_id\":\"" + CodexConstants.ClientId + "\","
            + "\"access_token\":\"access\","
            + "\"refresh_token\":\"refresh\","
            + "\"id_token\":\"id\","
            + "\"expires_at\":\"2026-04-17T09:58:36.389Z\","
            + "\"token_type\":\"bearer\","
            + "\"scopes\":[\"openid\",\"profile\",\"email\",\"offline_access\"]}",
            result.Credentials);
    }

    [Fact]
    public void DecodeAssumesOneHourForRefreshableCredentials()
    {
        var service = CreateService();
        var before = DateTimeOffset.UtcNow;

        var result = service.DecodeAuthJson(
            "{\"tokens\":{\"access_token\":\"access\",\"refresh_token\":\"refresh\"}}");

        var expiresAt = ReadExpiresAt(result.Credentials);
        Assert.InRange(
            expiresAt,
            before.AddHours(1).AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1).AddMinutes(1));
    }

    [Fact]
    public void DecodeIgnoresAnUnparseableLastRefresh()
    {
        var service = CreateService();

        var result = service.DecodeAuthJson(
            "{\"last_refresh\":\"not-a-time\",\"tokens\":{\"access_token\":\"access\"}}");

        Assert.Contains(
            "\"expires_at\":\"0001-01-01T00:00:00Z\"",
            result.Credentials,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DecodeRejectsAnEmptyDocument(string raw)
    {
        var service = CreateService();

        var error = Assert.Throws<CodexOAuthException>(
            () => service.DecodeAuthJson(raw));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("failed to decode auth json: empty auth json", error.Message);
    }

    [Fact]
    public void DecodeRejectsABlankAccessToken()
    {
        var service = CreateService();

        var error = Assert.Throws<CodexOAuthException>(
            () => service.DecodeAuthJson("{\"tokens\":{\"access_token\":\"   \"}}"));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("failed to decode auth json: access_token is empty", error.Message);
    }

    [Fact]
    public void DecodeRejectsMalformedJson()
    {
        var service = CreateService();

        var error = Assert.Throws<CodexOAuthException>(
            () => service.DecodeAuthJson("{"));

        Assert.Equal(400, error.StatusCode);
        Assert.StartsWith("failed to decode auth json: ", error.Message, StringComparison.Ordinal);
    }

    // ---------------- helpers ----------------

    private CodexOAuthService CreateService(IExecutor? executor = null)
        => new(_states, executor ?? _executor, _proxyExecutors, _clock);

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
        => "http://localhost:1455/auth/callback?code=" + code + "&state=" + state;

    private static string Query(string url)
    {
        var question = url.IndexOf('?', StringComparison.Ordinal);
        return url[(question + 1)..];
    }

    private static string QueryValue(string url, string key)
    {
        var query = HttpClientUtils.ParseQuery(Query(url));
        return query[key][0];
    }

    private static DateTimeOffset ReadExpiresAt(string credentialsJson)
    {
        using var document = System.Text.Json.JsonDocument.Parse(credentialsJson);
        var raw = document.RootElement.GetProperty("expires_at").GetString()!;
        return DateTimeOffset.Parse(
            raw,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None);
    }

    private sealed class FailingStateCache : ICodexOAuthStateCache
    {
        public Task<CodexOAuthState?> GetAsync(
            string key,
            CancellationToken cancellationToken = default)
            => Task.FromResult<CodexOAuthState?>(null);

        public Task SetAsync(
            string key,
            CodexOAuthState value,
            TimeSpan expiration,
            CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("boom"));

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
