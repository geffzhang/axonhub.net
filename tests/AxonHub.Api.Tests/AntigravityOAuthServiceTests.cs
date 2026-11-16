using System.Buffers.Text;
using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Antigravity;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the behavior of api/antigravity.go (the reference has no tests):
/// PKCE state generation and storage, callback parsing, the token exchange, and
/// the loadCodeAssist / onboardUser project discovery loop.
/// </summary>
public sealed class AntigravityOAuthServiceTests
{
    private readonly AntigravityTestClock _clock = new();
    private readonly ScriptedExecutor _executor = new();
    private readonly ScriptedProxyExecutorFactory _proxyExecutors = new();
    private readonly AntigravityOAuthStateCache _states;

    public AntigravityOAuthServiceTests()
    {
        _states = new AntigravityOAuthStateCache(_clock);
        _proxyExecutors.Executor = _executor;
    }

    // ---------------- start ----------------

    [Fact]
    public async Task StartStoresA32ByteStateAnd64ByteVerifier()
    {
        var service = CreateService();

        var result = await service.StartAsync("projects/from-request");

        Assert.Equal(32, Base64Url.DecodeFromChars(result.SessionId).Length);

        var stored = await _states.GetAsync(AntigravityOAuthService.CacheKey(result.SessionId));
        Assert.NotNull(stored);
        Assert.Equal(64, Base64Url.DecodeFromChars(stored!.CodeVerifier).Length);
        Assert.Equal("projects/from-request", stored.ProjectId);
        Assert.Equal(_clock.GetUtcNow().ToUnixTimeSeconds(), stored.CreatedAt);
        Assert.Equal(
            AntigravityOAuthService.GenerateCodeChallenge(stored.CodeVerifier),
            QueryValue(result.AuthUrl, "code_challenge"));
    }

    [Fact]
    public async Task StartBuildsTheAuthorizeUrlInSortedOrder()
    {
        var service = CreateService();

        var result = await service.StartAsync(string.Empty);
        var stored = await _states.GetAsync(AntigravityOAuthService.CacheKey(result.SessionId));

        Assert.StartsWith(AntigravityConstants.AuthorizeURL + "?", result.AuthUrl, StringComparison.Ordinal);
        Assert.Equal("offline", QueryValue(result.AuthUrl, "access_type"));
        Assert.Equal(AntigravityConstants.ClientID, QueryValue(result.AuthUrl, "client_id"));
        Assert.Equal("S256", QueryValue(result.AuthUrl, "code_challenge_method"));
        Assert.Equal("consent", QueryValue(result.AuthUrl, "prompt"));
        Assert.Equal(AntigravityConstants.RedirectURI, QueryValue(result.AuthUrl, "redirect_uri"));
        Assert.Equal("code", QueryValue(result.AuthUrl, "response_type"));
        Assert.Equal(AntigravityConstants.ScopesString, QueryValue(result.AuthUrl, "scope"));
        Assert.Equal(result.SessionId, QueryValue(result.AuthUrl, "state"));
        Assert.Equal(
            AntigravityOAuthService.GenerateCodeChallenge(stored!.CodeVerifier),
            QueryValue(result.AuthUrl, "code_challenge"));

        // url.Values.Encode sorts the keys.
        string[] keys =
        [
            "access_type",
            "client_id",
            "code_challenge",
            "code_challenge_method",
            "prompt",
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

        var first = await service.StartAsync(string.Empty);
        var second = await service.StartAsync(string.Empty);

        Assert.NotEqual(first.SessionId, second.SessionId);

        var firstState = await _states.GetAsync(AntigravityOAuthService.CacheKey(first.SessionId));
        var secondState = await _states.GetAsync(AntigravityOAuthService.CacheKey(second.SessionId));
        Assert.NotEqual(firstState!.CodeVerifier, secondState!.CodeVerifier);
    }

    [Fact]
    public async Task StartWrapsACacheFailure()
    {
        var service = new AntigravityOAuthService(
            new FailingStateCache(),
            _executor,
            _proxyExecutors,
            _clock);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(string.Empty));

        Assert.Equal("failed to save oauth state: boom", error.Message);
    }

    // ---------------- exchange: session and callback ----------------

    [Fact]
    public async Task ExchangeRejectsAnUnknownSession()
    {
        var service = CreateService();

        var error = await Assert.ThrowsAsync<AntigravityOAuthException>(
            () => service.ExchangeAsync(
                "unknown",
                "http://localhost/cb?code=abc&state=unknown",
                proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("invalid or expired oauth session", error.Message);
    }

    [Fact]
    public async Task ExchangeRejectsAnExpiredSession()
    {
        var service = CreateService();
        var start = await service.StartAsync("proj");

        _clock.Advance(AntigravityOAuthService.StateExpiration + TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<AntigravityOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal("invalid or expired oauth session", error.Message);
    }

    [Fact]
    public async Task ExchangeKeepsTheStateAtTheExactDeadline()
    {
        ArrangeTokenAndProject();
        var service = CreateService();
        var start = await service.StartAsync("proj-start");

        // go-cache expires with time.Now().After(expiresAt), so the deadline
        // itself is still live.
        _clock.Advance(AntigravityOAuthService.StateExpiration);

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Equal("refresh-1|proj-start", result.Credentials);
    }

    [Theory]
    [InlineData("localhost:51121/oauth-callback?code=abc&state=s")]
    [InlineData("ftp://localhost/oauth-callback?code=abc&state=s")]
    public async Task ExchangeRequiresAFullCallbackUrl(string callbackUrl)
    {
        var service = CreateService();
        var start = await service.StartAsync("proj");

        var error = await Assert.ThrowsAsync<AntigravityOAuthException>(
            () => service.ExchangeAsync(start.SessionId, callbackUrl, proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal("callback_url must be a full URL", error.Message);
    }

    [Fact]
    public async Task ExchangeTrimsTheCallbackUrl()
    {
        ArrangeTokenAndProject();
        var service = CreateService();
        var start = await service.StartAsync("proj-start");

        var result = await service.ExchangeAsync(
            start.SessionId,
            "  " + Callback("abc", start.SessionId) + "  ",
            proxy: null);

        Assert.Equal("refresh-1|proj-start", result.Credentials);
    }

    [Fact]
    public async Task ExchangeRejectsAMalformedCallbackUrl()
    {
        var service = CreateService();
        var start = await service.StartAsync("proj");

        var error = await Assert.ThrowsAsync<AntigravityOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                "http://[::1/oauth-callback?code=abc&state=" + start.SessionId,
                proxy: null));

        Assert.Equal(400, error.StatusCode);
        Assert.StartsWith("invalid callback_url: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeRequiresTheCodeParameter()
    {
        var service = CreateService();
        var start = await service.StartAsync("proj");

        var error = await Assert.ThrowsAsync<AntigravityOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                "http://localhost/cb?state=" + start.SessionId,
                proxy: null));

        Assert.Equal("code parameter not found in callback_url", error.Message);
    }

    [Fact]
    public async Task ExchangeRequiresTheStateParameter()
    {
        var service = CreateService();
        var start = await service.StartAsync("proj");

        var error = await Assert.ThrowsAsync<AntigravityOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                "http://localhost/cb?code=abc",
                proxy: null));

        Assert.Equal("state parameter not found in callback_url", error.Message);
    }

    [Fact]
    public async Task ExchangeRejectsAStateMismatchWithoutConsumingTheSession()
    {
        var service = CreateService();
        var start = await service.StartAsync("proj");

        var error = await Assert.ThrowsAsync<AntigravityOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", "another-state"),
                proxy: null));

        Assert.Equal("oauth state mismatch", error.Message);
        Assert.NotNull(await _states.GetAsync(AntigravityOAuthService.CacheKey(start.SessionId)));
    }

    [Fact]
    public async Task ExchangeConsumesTheSessionAfterValidation()
    {
        ArrangeTokenAndProject();
        var service = CreateService();
        var start = await service.StartAsync("proj-start");

        _ = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        var error = await Assert.ThrowsAsync<AntigravityOAuthException>(
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
        ArrangeTokenAndProject();
        var service = CreateService();
        var start = await service.StartAsync("proj-start");
        var stored = await _states.GetAsync(AntigravityOAuthService.CacheKey(start.SessionId));

        _ = await service.ExchangeAsync(
            start.SessionId,
            Callback("code-1", start.SessionId),
            proxy: null);

        var tokenRequest = Assert.Single(
            _executor.Requests,
            request => request.Url == AntigravityConstants.TokenURL);

        Assert.Equal("POST", tokenRequest.Method);
        Assert.Equal(
            "client_id=" + AntigravityConstants.ClientID
            + "&client_secret=" + AntigravityConstants.ClientSecret
            + "&code=code-1"
            + "&code_verifier=" + stored!.CodeVerifier
            + "&grant_type=authorization_code"
            + "&redirect_uri=http%3A%2F%2Flocalhost%3A51121%2Foauth-callback",
            Encoding.UTF8.GetString(tokenRequest.Body!));
        Assert.Equal(
            "application/x-www-form-urlencoded",
            tokenRequest.Headers!["Content-Type"][0]);
        Assert.Equal("application/json", tokenRequest.Headers["Accept"][0]);
        Assert.Equal(AntigravityVersion.GetUserAgent(), tokenRequest.Headers["User-Agent"][0]);
    }

    [Fact]
    public async Task ExchangeWrapsTheProviderErrorTwice()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"error\":\"invalid_grant\",\"error_description\":\"bad code\"}");
        var service = CreateService();
        var start = await service.StartAsync("proj");

        var error = await Assert.ThrowsAsync<AntigravityOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(
            "token exchange failed: token exchange failed: invalid_grant - bad code",
            error.Message);
    }

    [Fact]
    public async Task ExchangeWrapsTransportErrors()
    {
        _executor.OnDo = (call, request, ct) => throw new HttpClientError(
            "POST",
            AntigravityConstants.TokenURL,
            500,
            "500 Internal Server Error",
            Encoding.UTF8.GetBytes("{}"));
        var service = CreateService();
        var start = await service.StartAsync("proj");

        var error = await Assert.ThrowsAsync<AntigravityOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(
            "token exchange failed: POST - https://oauth2.googleapis.com/token "
            + "with status 500 Internal Server Error",
            error.Message);
    }

    [Fact]
    public async Task ExchangeUsesTheProjectIdFromTheStartRequest()
    {
        ArrangeTokenAndProject();
        var service = CreateService();
        var start = await service.StartAsync("proj-start");

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Equal("refresh-1|proj-start", result.Credentials);
        Assert.Single(_executor.Requests);
        Assert.Equal(AntigravityConstants.TokenURL, _executor.Requests[0].Url);
    }

    [Fact]
    public async Task ExchangeKeepsAnEmptyRefreshToken()
    {
        _executor.OnDo = (call, request, ct) => Json(
            200,
            "{\"access_token\":\"access-1\",\"expires_in\":3600}");
        var service = CreateService();
        var start = await service.StartAsync("proj-start");

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Equal("|proj-start", result.Credentials);
    }

    // ---------------- exchange: project discovery ----------------

    [Fact]
    public async Task ExchangeResolvesTheProjectFromTheStringField()
    {
        _executor.OnDo = (call, request, ct) => request.Url == AntigravityConstants.TokenURL
            ? TokenResponse()
            : Json(200, "{\"cloudaicompanionProject\":\"proj-load\"}");
        var service = CreateService();
        var start = await service.StartAsync(string.Empty);

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Equal("refresh-1|proj-load", result.Credentials);

        var load = Assert.Single(
            _executor.Requests,
            request => request.Url.EndsWith("/v1internal:loadCodeAssist", StringComparison.Ordinal));
        Assert.Equal(
            AntigravityConstants.EndpointProd + "/v1internal:loadCodeAssist",
            load.Url);
        Assert.Equal("POST", load.Method);
        Assert.Equal("Bearer access-1", load.Headers!["Authorization"][0]);
        Assert.Equal("application/json", load.Headers["Content-Type"][0]);
        Assert.Equal(AntigravityVersion.GetUserAgent(), load.Headers["User-Agent"][0]);
        Assert.Equal(AntigravityConstants.ApiClient, load.Headers["X-Goog-Api-Client"][0]);
        Assert.Equal(AntigravityConstants.ClientMetadata, load.Headers["Client-Metadata"][0]);
        Assert.Equal(
            "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\",\"platform\":\"PLATFORM_UNSPECIFIED\",\"pluginType\":\"GEMINI\"}}",
            Encoding.UTF8.GetString(load.Body!));
    }

    [Fact]
    public async Task ExchangeResolvesTheProjectFromTheObjectField()
    {
        ArrangeTokenAndProject("{\"cloudaicompanionProject\":{\"id\":\"proj-obj\"}}");
        var service = CreateService();
        var start = await service.StartAsync(string.Empty);

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Equal("refresh-1|proj-obj", result.Credentials);
    }

    [Fact]
    public async Task ExchangeMatchesJsonFieldsCaseInsensitively()
    {
        ArrangeTokenAndProject("{\"CloudAICompanionProject\":\"proj-ci\"}");
        var service = CreateService();
        var start = await service.StartAsync(string.Empty);

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Equal("refresh-1|proj-ci", result.Credentials);
    }

    [Fact]
    public async Task ExchangeIgnoresAProjectObjectWithoutAStringId()
    {
        _executor.OnDo = (call, request, ct) =>
        {
            if (request.Url == AntigravityConstants.TokenURL)
            {
                return TokenResponse();
            }

            if (request.Url.EndsWith("/v1internal:loadCodeAssist", StringComparison.Ordinal))
            {
                return Json(200, "{\"cloudaicompanionProject\":{\"id\":5}}");
            }

            return Json(200, "{\"done\":true,\"response\":{\"cloudaicompanionProject\":{\"id\":\"proj-after-onboard\"}}}");
        };
        var service = CreateService();
        var start = await service.StartAsync(string.Empty);

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Equal("refresh-1|proj-after-onboard", result.Credentials);
        var onboard = Assert.Single(
            _executor.Requests,
            request => request.Url.EndsWith("/v1internal:onboardUser", StringComparison.Ordinal));
        Assert.Contains("\"tierId\":\"FREE\"", Encoding.UTF8.GetString(onboard.Body!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeOnboardsWithTheDefaultTier()
    {
        _executor.OnDo = (call, request, ct) =>
        {
            if (request.Url == AntigravityConstants.TokenURL)
            {
                return TokenResponse();
            }

            if (request.Url.EndsWith("/v1internal:loadCodeAssist", StringComparison.Ordinal))
            {
                return Json(
                    200,
                    "{\"allowedTiers\":[{\"id\":\"tier-a\"},{\"id\":\"tier-b\",\"isDefault\":true}]}");
            }

            return Json(
                200,
                "{\"done\":true,\"response\":{\"cloudaicompanionProject\":{\"id\":\"proj-onboard\"}}}");
        };
        var service = CreateService();
        var start = await service.StartAsync(string.Empty);

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Equal("refresh-1|proj-onboard", result.Credentials);

        var onboard = _executor.Requests[2];
        Assert.Equal(
            AntigravityConstants.EndpointProd + "/v1internal:onboardUser",
            onboard.Url);
        Assert.Equal(
            "{\"metadata\":{\"ideType\":\"ANTIGRAVITY\",\"platform\":\"PLATFORM_UNSPECIFIED\",\"pluginType\":\"GEMINI\"},\"tierId\":\"tier-b\"}",
            Encoding.UTF8.GetString(onboard.Body!));
    }

    [Fact]
    public async Task ExchangeOnboardsWithTheFirstTierWhenNoDefaultIsMarked()
    {
        _executor.OnDo = (call, request, ct) =>
        {
            if (request.Url == AntigravityConstants.TokenURL)
            {
                return TokenResponse();
            }

            if (request.Url.EndsWith("/v1internal:loadCodeAssist", StringComparison.Ordinal))
            {
                return Json(200, "{\"allowedTiers\":[{\"id\":\"tier-a\"},{\"id\":\"tier-b\"}]}");
            }

            return Json(
                200,
                "{\"done\":true,\"response\":{\"cloudaicompanionProject\":{\"id\":\"proj-onboard\"}}}");
        };
        var service = CreateService();
        var start = await service.StartAsync(string.Empty);

        _ = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Contains(
            "\"tierId\":\"tier-a\"",
            Encoding.UTF8.GetString(_executor.Requests[2].Body!),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExchangeFallsBackToTheNextLoadEndpoint()
    {
        _executor.OnDo = (call, request, ct) =>
        {
            if (request.Url == AntigravityConstants.TokenURL)
            {
                return TokenResponse();
            }

            return request.Url.StartsWith(AntigravityConstants.EndpointProd, StringComparison.Ordinal)
                ? Json(201, "{}")
                : Json(200, "{\"cloudaicompanionProject\":\"proj-daily\"}");
        };
        var service = CreateService();
        var start = await service.StartAsync(string.Empty);

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Equal("refresh-1|proj-daily", result.Credentials);
        Assert.Equal(3, _executor.Requests.Count);
        Assert.Equal(AntigravityConstants.TokenURL, _executor.Requests[0].Url);
        Assert.Equal(
            AntigravityConstants.EndpointProd + "/v1internal:loadCodeAssist",
            _executor.Requests[1].Url);
        Assert.Equal(
            AntigravityConstants.EndpointDaily + "/v1internal:loadCodeAssist",
            _executor.Requests[2].Url);
    }

    [Fact]
    public async Task ExchangeFallsBackWhenTheLoadEndpointThrows()
    {
        _executor.OnDo = (call, request, ct) =>
        {
            if (request.Url == AntigravityConstants.TokenURL)
            {
                return TokenResponse();
            }

            if (request.Url.StartsWith(AntigravityConstants.EndpointProd, StringComparison.Ordinal))
            {
                throw new HttpClientError(
                    "POST",
                    request.Url,
                    500,
                    "500 Internal Server Error",
                    Encoding.UTF8.GetBytes("{}"));
            }

            return Json(200, "{\"cloudaicompanionProject\":\"proj-daily\"}");
        };
        var service = CreateService();
        var start = await service.StartAsync(string.Empty);

        var result = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            proxy: null);

        Assert.Equal("refresh-1|proj-daily", result.Credentials);
    }

    [Fact]
    public async Task ExchangeRetriesOnboardingThreeTimesPerEndpoint()
    {
        _executor.OnDo = (call, request, ct) =>
        {
            if (request.Url == AntigravityConstants.TokenURL)
            {
                return TokenResponse();
            }

            return request.Url.EndsWith("/v1internal:loadCodeAssist", StringComparison.Ordinal)
                ? Json(200, "{}")
                : Json(200, "{\"done\":false}");
        };
        var service = CreateService();
        var start = await service.StartAsync(string.Empty);

        var error = await Assert.ThrowsAsync<AntigravityOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal(502, error.StatusCode);
        Assert.Equal(
            "failed to resolve project id and none provided: failed to onboard user after retries",
            error.Message);

        // resolveProjectID invokes onboardUser once per load endpoint, and
        // onboardUser itself walks all three endpoints three times: 3 x 3 x 3.
        var onboardUrls = _executor.Requests
            .Where(request => request.Url.EndsWith("/v1internal:onboardUser", StringComparison.Ordinal))
            .Select(request => request.Url)
            .ToList();
        Assert.Equal(27, onboardUrls.Count);

        string[] endpoints =
        [
            AntigravityConstants.EndpointProd,
            AntigravityConstants.EndpointDaily,
            AntigravityConstants.EndpointAutopush,
        ];
        for (var i = 0; i < onboardUrls.Count; i++)
        {
            Assert.StartsWith(endpoints[(i / 3) % 3], onboardUrls[i], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ExchangeReportsTheLastEndpointStatus()
    {
        // A non-200 status below 400 is the only status the reference reaches
        // here: httpclient.Do turns >= 400 into an error before the check.
        _executor.OnDo = (call, request, ct) => request.Url == AntigravityConstants.TokenURL
            ? TokenResponse()
            : Json(201, "{}");
        var service = CreateService();
        var start = await service.StartAsync(string.Empty);

        var error = await Assert.ThrowsAsync<AntigravityOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.Equal(
            "failed to resolve project id and none provided: status 201",
            error.Message);
        Assert.Equal(4, _executor.Requests.Count);
    }

    [Fact]
    public async Task ExchangeTreatsAMalformedLoadBodyAsAnError()
    {
        _executor.OnDo = (call, request, ct) => request.Url == AntigravityConstants.TokenURL
            ? TokenResponse()
            : Json(200, "{");
        var service = CreateService();
        var start = await service.StartAsync(string.Empty);

        var error = await Assert.ThrowsAsync<AntigravityOAuthException>(
            () => service.ExchangeAsync(
                start.SessionId,
                Callback("abc", start.SessionId),
                proxy: null));

        Assert.StartsWith(
            "failed to resolve project id and none provided: ",
            error.Message,
            StringComparison.Ordinal);
    }

    // ---------------- exchange: proxy ----------------

    [Fact]
    public async Task ExchangeRoutesBothPhasesThroughTheUrlProxy()
    {
        var proxyExecutor = new ScriptedExecutor
        {
            OnDo = (call, request, ct) => request.Url == AntigravityConstants.TokenURL
                ? TokenResponse()
                : Json(200, "{\"cloudaicompanionProject\":\"proj-proxy\"}"),
        };
        _proxyExecutors.Executor = proxyExecutor;

        var service = CreateService();
        var start = await service.StartAsync(string.Empty);

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

        Assert.Equal("refresh-1|proj-proxy", result.Credentials);
        var configured = Assert.Single(_proxyExecutors.Requests);
        Assert.Equal("http://proxy.local:8080", configured.Url);
        Assert.Equal("user", configured.Username);
        Assert.True(configured.DisableConnectionReuse);

        // The base executor never saw the exchange or the load request.
        Assert.Empty(_executor.Requests);
        Assert.Equal(2, proxyExecutor.Requests.Count);
    }

    [Theory]
    [InlineData("disabled", "http://proxy.local:8080")]
    [InlineData("environment", "http://proxy.local:8080")]
    [InlineData("url", "")]
    public async Task ExchangeIgnoresProxiesOutsideTheUrlBranch(string type, string url)
    {
        ArrangeTokenAndProject();
        var service = CreateService();
        var start = await service.StartAsync("proj-start");

        _ = await service.ExchangeAsync(
            start.SessionId,
            Callback("abc", start.SessionId),
            new ProxyConfig { Type = type, Url = url });

        Assert.Empty(_proxyExecutors.Requests);
        Assert.Single(_executor.Requests);
    }

    // ---------------- helpers ----------------

    private AntigravityOAuthService CreateService(IExecutor? executor = null)
        => new(_states, executor ?? _executor, _proxyExecutors, _clock);

    private void ArrangeTokenAndProject(
        string projectJson = "{\"cloudaicompanionProject\":\"proj-1\"}")
    {
        _executor.OnDo = (call, request, ct) =>
        {
            if (request.Url == AntigravityConstants.TokenURL)
            {
                return TokenResponse();
            }

            if (request.Url.EndsWith("/v1internal:loadCodeAssist", StringComparison.Ordinal))
            {
                return Json(200, projectJson);
            }

            throw new InvalidOperationException("unexpected request " + request.Url);
        };
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

    private static HttpResponseData TokenResponse(
        string accessToken = "access-1",
        string refreshToken = "refresh-1")
        => Json(
            200,
            "{\"access_token\":\"" + accessToken + "\",\"refresh_token\":\"" + refreshToken
            + "\",\"expires_in\":3600,\"token_type\":\"Bearer\"}");

    private static string Callback(string code, string state)
        => "http://localhost:51121/oauth-callback?code=" + code + "&state=" + state;

    private static string QueryValue(string url, string key)
    {
        var question = url.IndexOf('?', StringComparison.Ordinal);
        var query = HttpClientUtils.ParseQuery(url[(question + 1)..]);
        return query[key][0];
    }

    private sealed class FailingStateCache : IAntigravityOAuthStateCache
    {
        public Task<AntigravityOAuthState?> GetAsync(
            string key,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AntigravityOAuthState?>(null);

        public Task SetAsync(
            string key,
            AntigravityOAuthState value,
            TimeSpan expiration,
            CancellationToken cancellationToken = default)
            => Task.FromException(new InvalidOperationException("boom"));

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
