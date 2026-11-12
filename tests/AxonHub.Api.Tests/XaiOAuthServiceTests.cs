using System.Buffers.Text;
using System.Net;
using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Xai.Subscription;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the behavior of api/xai.go (internal/server/api/xai.go): the
/// user-bound PKCE session, the callback exchange, and the SSO-cookie device
/// flow.
/// </summary>
public sealed class XaiOAuthServiceTests
{
    private readonly AntigravityTestClock _clock = new();
    private readonly ScriptedExecutor _executor = new();
    private readonly ScriptedProxyExecutorFactory _proxyExecutors = new();
    private readonly FakeManagementIdentity _identity = new(new ManagementUser(1, false));
    private readonly XaiOAuthStateCache _states;

    public XaiOAuthServiceTests()
    {
        _states = new XaiOAuthStateCache(_clock);
        _proxyExecutors.Executor = _executor;
    }

    // ---------------- start ----------------

    [Fact]
    public async Task StartRequiresAUser()
    {
        _identity.User = null;
        var service = CreateService();

        var error = await Assert.ThrowsAsync<XaiOAuthException>(
            () => service.StartAsync());

        Assert.Equal(401, error.StatusCode);
        Assert.Equal("authentication required", error.Message);
    }

    [Fact]
    public async Task StartStoresTheUserBoundState()
    {
        var service = CreateService();

        var result = await service.StartAsync();

        Assert.Equal(32, Base64Url.DecodeFromChars(result.SessionId).Length);

        var stored = await _states.GetAsync(XaiOAuthService.CacheKey(result.SessionId));
        Assert.NotNull(stored);
        Assert.Equal(1, stored!.UserId);
        Assert.Equal(32, Base64Url.DecodeFromChars(stored.CodeVerifier).Length);
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
            SubscriptionConstants.AuthorizeUrl + "?",
            result.AuthUrl,
            StringComparison.Ordinal);
        Assert.Equal("code", QueryValue(result.AuthUrl, "response_type"));
        Assert.Equal(SubscriptionConstants.ClientId, QueryValue(result.AuthUrl, "client_id"));
        Assert.Equal(SubscriptionConstants.RedirectUri, QueryValue(result.AuthUrl, "redirect_uri"));
        Assert.Equal(SubscriptionConstants.Scopes, QueryValue(result.AuthUrl, "scope"));
        Assert.Equal(result.SessionId, QueryValue(result.AuthUrl, "state"));
        Assert.Equal(16, Base64Url.DecodeFromChars(QueryValue(result.AuthUrl, "nonce")).Length);
        Assert.Equal("S256", QueryValue(result.AuthUrl, "code_challenge_method"));
        Assert.Equal("generic", QueryValue(result.AuthUrl, "plan"));
        Assert.Equal("axonhub", QueryValue(result.AuthUrl, "referrer"));

        // url.Values.Encode sorts the keys.
        string[] keys =
        [
            "client_id",
            "code_challenge",
            "code_challenge_method",
            "nonce",
            "plan",
            "redirect_uri",
            "referrer",
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
        Assert.NotEqual(
            QueryValue(first.AuthUrl, "nonce"),
            QueryValue(second.AuthUrl, "nonce"));

        var firstState = await _states.GetAsync(XaiOAuthService.CacheKey(first.SessionId));
        var secondState = await _states.GetAsync(XaiOAuthService.CacheKey(second.SessionId));
        Assert.NotEqual(firstState!.CodeVerifier, secondState!.CodeVerifier);
    }

    [Fact]
    public async Task StartWrapsACacheFailure()
    {
        var service = new XaiOAuthService(
            new FailingStateCache(),
            _executor,
            _proxyExecutors,
            _identity);

        var error = await Assert.ThrowsAsync<XaiOAuthException>(
            () => service.StartAsync());

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("save xAI oauth state: boom", error.Message);
    }

    [Fact]
    public async Task ExchangeKeepsTheStateAtTheExactDeadline()
    {
        ArrangeToken();
        var service = CreateService();
        var start = await service.StartAsync();

        // go-cache expires with time.Now().After(expiresAt), so the deadline
        // itself is still live.
        _clock.Advance(XaiOAuthService.StateExpiration);

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Contains("\"access_token\":\"access-1\"", result.Credentials, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeRejectsAnExpiredSession()
    {
        var service = CreateService();
        var start = await service.StartAsync();

        _clock.Advance(XaiOAuthService.StateExpiration + TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<XaiOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal("invalid or expired oauth session", error.Message);
    }

    // ---------------- exchange: session and callback ----------------

    [Fact]
    public async Task ExchangeRejectsAnUnknownSession()
    {
        var service = CreateService();

        var error = await Assert.ThrowsAsync<XaiOAuthException>(
            () => service.ExchangeAsync(
                "unknown",
                "http://127.0.0.1:56121/callback?code=abc&state=unknown",
                proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("invalid or expired oauth session", error.Message);
    }

    [Fact]
    public async Task ExchangeRequiresAUserAndKeepsTheSession()
    {
        var service = CreateService();
        var start = await service.StartAsync();
        _identity.User = null;

        var error = await Assert.ThrowsAsync<XaiOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal(401, error.StatusCode);
        Assert.Equal("authentication required", error.Message);
        Assert.NotNull(await _states.GetAsync(XaiOAuthService.CacheKey(start.SessionId)));
    }

    [Fact]
    public async Task ExchangeRejectsAnotherUsersSessionAndKeepsIt()
    {
        var service = CreateService();
        var start = await service.StartAsync();
        _identity.User = new ManagementUser(2, false);

        var error = await Assert.ThrowsAsync<XaiOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("invalid or expired oauth session", error.Message);

        // xai.go:143-146 returns before the delete at 156-158.
        Assert.NotNull(await _states.GetAsync(XaiOAuthService.CacheKey(start.SessionId)));
    }

    [Theory]
    [InlineData("localhost:56121/callback?code=abc&state=s")]
    [InlineData("ftp://localhost/callback?code=abc&state=s")]
    [InlineData("")]
    public async Task ExchangeRequiresAFullCallbackUrl(string callbackUrl)
    {
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<XaiOAuthException>(
            () => service.ExchangeAsync(start.SessionId, callbackUrl, proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("callback_url must be a full URL", error.Message);
        Assert.NotNull(await _states.GetAsync(XaiOAuthService.CacheKey(start.SessionId)));
    }

    [Theory]
    [InlineData("http://localhost/callback?state=s")]
    [InlineData("http://localhost/callback?code=abc")]
    [InlineData("http://localhost/callback")]
    public async Task ExchangeRequiresBothCodeAndState(string callbackUrl)
    {
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<XaiOAuthException>(
            () => service.ExchangeAsync(start.SessionId, callbackUrl, proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("callback_url must contain code and state", error.Message);
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
    public async Task ExchangeRejectsAStateMismatchAndKeepsTheSession()
    {
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<XaiOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", "another-state"),
                proxy: null));

        Assert.Equal("oauth state mismatch", error.Message);

        // The delete follows the validation (xai.go:147-158).
        Assert.NotNull(await _states.GetAsync(XaiOAuthService.CacheKey(start.SessionId)));
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

        Assert.Null(await _states.GetAsync(XaiOAuthService.CacheKey(start.SessionId)));
    }

    // ---------------- exchange: token request ----------------

    [Fact]
    public async Task ExchangeBuildsTheTokenRequestExactly()
    {
        ArrangeToken();
        var service = CreateService();
        var start = await service.StartAsync();
        var stored = await _states.GetAsync(XaiOAuthService.CacheKey(start.SessionId));

        _ = await service.ExchangeAsync(
            start.SessionId,
            Callback("code-1", start.SessionId),
            proxy: null);

        var tokenRequest = Assert.Single(_executor.Requests);
        Assert.Equal(SubscriptionConstants.TokenUrl, tokenRequest.Url);
        Assert.Equal("POST", tokenRequest.Method);
        Assert.Equal(
            "client_id=" + SubscriptionConstants.ClientId
            + "&code=code-1"
            + "&code_verifier=" + stored!.CodeVerifier
            + "&grant_type=authorization_code"
            + "&redirect_uri=http%3A%2F%2F127.0.0.1%3A56121%2Fcallback",
            Encoding.UTF8.GetString(tokenRequest.Body!));
        Assert.Equal(
            "application/x-www-form-urlencoded",
            tokenRequest.Headers!["Content-Type"][0]);
        Assert.Equal("application/json", tokenRequest.Headers["Accept"][0]);
        Assert.Equal(SubscriptionConstants.CliUserAgent, tokenRequest.Headers["User-Agent"][0]);
    }

    [Fact]
    public async Task ExchangeWrapsTheProviderErrorTwice()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"error\":\"invalid_grant\",\"error_description\":\"bad code\"}");
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<XaiOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(
            "xAI token exchange failed: token exchange failed: invalid_grant - bad code",
            error.Message);
        Assert.Null(await _states.GetAsync(XaiOAuthService.CacheKey(start.SessionId)));
    }

    [Fact]
    public async Task ExchangeWrapsTransportErrors()
    {
        _executor.OnDo = (call, request, ct) => throw new HttpClientError(
            "POST",
            SubscriptionConstants.TokenUrl,
            500,
            "500 Internal Server Error",
            Encoding.UTF8.GetBytes("{}"));
        var service = CreateService();
        var start = await service.StartAsync();

        var error = await Assert.ThrowsAsync<XaiOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(
            "xAI token exchange failed: POST - " + SubscriptionConstants.TokenUrl
            + " with status 500 Internal Server Error",
            error.Message);
    }

    [Fact]
    public async Task ExchangeReturnsTheCredentialsJson()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"access_token\":\"access-1\",\"refresh_token\":\"refresh-1\","
            + "\"token_type\":\"Bearer\",\"scope\":\"openid email\"}");
        var service = CreateService();
        var start = await service.StartAsync();

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Equal(
            "{\"client_id\":\"" + SubscriptionConstants.ClientId + "\","
            + "\"access_token\":\"access-1\","
            + "\"refresh_token\":\"refresh-1\","
            + "\"expires_at\":\"0001-01-01T00:00:00Z\","
            + "\"token_type\":\"Bearer\","
            + "\"scopes\":[\"openid\",\"email\"]}",
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

    // ---------------- sso ----------------

    [Fact]
    public async Task DecodeSsoConvertsTheCookieToCredentials()
    {
        var transport = new ScriptedSsoTransport(XaiSsoScript.Handler);
        var service = CreateService(_ => transport);

        var result = service.DecodeSso("sso=synthetic-sso", proxy: null);

        Assert.Contains("\"access_token\":\"access\"", result.Credentials, StringComparison.Ordinal);
        Assert.Equal(8, transport.Requests.Count);

        Assert.Equal("GET", transport.Requests[0].Method);
        Assert.Equal(SubscriptionConstants.SsoAccountsUrl, transport.Requests[0].Url);
        Assert.Contains("sso=synthetic-sso", transport.Requests[0].Cookie, StringComparison.Ordinal);

        Assert.Equal(SubscriptionConstants.SsoDeviceUrl, transport.Requests[1].Url);
        Assert.Equal(
            "client_id=" + SubscriptionConstants.ClientId
            + "&scope=" + HttpClientUtils.QueryEscape(SubscriptionConstants.SsoScopes),
            transport.Requests[1].Body);

        Assert.Equal(SubscriptionConstants.SsoVerifyUrl, transport.Requests[3].Url);
        Assert.Equal("user_code=user", transport.Requests[3].Body);
        Assert.Equal(SubscriptionConstants.SsoApproveUrl, transport.Requests[5].Url);
        Assert.Equal(SubscriptionConstants.TokenUrl, transport.Requests[7].Url);
    }

    [Fact]
    public async Task DecodeSsoWrapsConversionErrors()
    {
        var transport = new ScriptedSsoTransport((request, _) =>
            request.RequestUri!.AbsoluteUri == SubscriptionConstants.SsoAccountsUrl
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : throw new InvalidOperationException("unexpected request"));
        var service = CreateService(_ => transport);

        var error = Assert.Throws<XaiOAuthException>(
            () => service.DecodeSso("sso=synthetic-sso", proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal("xAI SSO conversion failed: xAI SSO token is unauthorized", error.Message);
    }

    [Fact]
    public void DecodeSsoRoutesAUrlProxyThroughTheSsoSeam()
    {
        ProxyConfig? seen = null;
        var transport = new ScriptedSsoTransport((request, _) =>
            request.RequestUri!.AbsoluteUri == SubscriptionConstants.SsoAccountsUrl
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                : throw new InvalidOperationException("unexpected request"));
        var service = CreateService(proxy =>
        {
            seen = proxy;
            return transport;
        });

        _ = Assert.Throws<XaiOAuthException>(
            () => service.DecodeSso(
                "sso=synthetic-sso",
                new ProxyConfig { Type = ProxyType.Url, Url = "http://proxy.local:8080" }));

        Assert.NotNull(seen);
        Assert.Equal("http://proxy.local:8080", seen!.Url);
    }

    // ---------------- helpers ----------------

    private XaiOAuthService CreateService(
        Func<ProxyConfig?, HttpMessageHandler?>? ssoTransportFactory = null)
        => new(
            _states,
            _executor,
            _proxyExecutors,
            _identity,
            ssoTransportFactory);

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
        => "http://127.0.0.1:56121/callback?code=" + code + "&state=" + state;

    private static string QueryValue(string url, string key)
        => HttpClientUtils.ParseQuery(Query(url))[key][0];

    private static string Query(string url)
    {
        var question = url.IndexOf('?', StringComparison.Ordinal);
        return url[(question + 1)..];
    }

    private sealed class FailingStateCache : IXaiOAuthStateCache
    {
        public Task<XaiOAuthState?> GetAsync(
            string key,
            CancellationToken cancellationToken = default)
            => Task.FromResult<XaiOAuthState?>(null);

        public Task SetAsync(
            string key,
            XaiOAuthState value,
            TimeSpan expiration,
            CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("boom"));

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}

/// <summary>
/// The eight responses of the xAI SSO device flow (sso_device.go:80-143):
/// accounts check, device code, verification page, verify redirect, consent,
/// approve redirect, done page, and the polled token.
/// </summary>
internal static class XaiSsoScript
{
    public const string ConsentUrl = "https://auth.x.ai/oauth2/device/consent";
    public const string DoneUrl = "https://auth.x.ai/oauth2/device/done";

    public static readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> Handler
        = (request, _) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == SubscriptionConstants.SsoAccountsUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (url == SubscriptionConstants.SsoDeviceUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"device_code\":\"device\",\"user_code\":\"user\","
                        + "\"verification_uri_complete\":"
                        + "\"https://auth.x.ai/oauth2/device/verify?user_code=user\","
                        + "\"interval\":1,\"expires_in\":60}",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            if (url == "https://auth.x.ai/oauth2/device/verify?user_code=user")
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (url == SubscriptionConstants.SsoVerifyUrl)
            {
                return Redirect(ConsentUrl);
            }

            if (url == ConsentUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (url == SubscriptionConstants.SsoApproveUrl)
            {
                return Redirect(DoneUrl);
            }

            if (url == DoneUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            if (url == SubscriptionConstants.TokenUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "{\"access_token\":\"access\",\"refresh_token\":\"refresh\","
                        + "\"expires_in\":3600,\"token_type\":\"Bearer\"}",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            throw new InvalidOperationException("unexpected request: " + url);
        };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }
}

/// <summary>
/// An <see cref="HttpMessageHandler"/> that scripts the xAI SSO flow and keeps
/// a materialized copy of each request (the flow disposes them).
/// </summary>
internal sealed class ScriptedSsoTransport(
    Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler)
    : HttpMessageHandler
{
    public List<RecordedSsoRequest> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Requests.Add(new RecordedSsoRequest(
            request.Method.Method,
            request.RequestUri!.AbsoluteUri,
            request.Headers.TryGetValues("Cookie", out var cookies)
                ? string.Join("; ", cookies)
                : string.Empty,
            request.Content?.ReadAsStringAsync(cancellationToken)
                .GetAwaiter()
                .GetResult() ?? string.Empty));

        return Task.FromResult(handler(request, cancellationToken));
    }
}

internal sealed record RecordedSsoRequest(string Method, string Url, string Cookie, string Body);
