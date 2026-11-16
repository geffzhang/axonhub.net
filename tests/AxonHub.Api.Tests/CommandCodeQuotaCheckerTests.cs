using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports internal/server/biz/provider_quota/commandcode_checker.go together
/// with commandcode_checker_test.go: the dual billing surfaces (/alpha/* with
/// the account API key, /internal/billing/* with the Studio session cookie),
/// the cookie normalizer seam, the plan-allowance table and the Go map/fold
/// decoding of the credits and subscriptions payloads.
/// </summary>
public sealed class CommandCodeQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

    private const string AlphaCredits = "https://api.commandcode.ai/alpha/billing/credits";

    private const string AlphaSubscriptions = "https://api.commandcode.ai/alpha/billing/subscriptions";

    private const string InternalCredits = "https://api.commandcode.ai/internal/billing/credits";

    private const string InternalSubscriptions = "https://api.commandcode.ai/internal/billing/subscriptions";

    private const string Cookie = "__Secure-commandcode_prod_.session_token=tok";

    private const string CreditsOk =
        """{"credits":{"monthly_remaining_usd":60,"purchased_credits_usd":0,"windowLimits":{"five_hour":{"used_usd":2,"cap_usd":16,"usage_percent":12.5,"reset_time":2000000000},"weekly":{"used_usd":8,"cap_usd":40,"usage_percent":20,"reset_time":2000000000}}}}""";

    private const string SubscriptionsOk =
        """{"planId":"individual-pro","status":"active","currentPeriodEnd":"2026-10-01T00:00:00Z"}""";

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    /// <summary>TestCommandCodeQuotaChecker_CheckQuota happy path (commandcode_checker_test.go:132-166).</summary>
    [Fact]
    public async Task CookiePathUsesTheInternalEndpointsAndTrustedMonthlyWindow()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = request => new Uri(request.Url).AbsolutePath.EndsWith(
            "/subscriptions", StringComparison.Ordinal)
            ? JsonResponse(SubscriptionsOk)
            : JsonResponse(CreditsOk);

        var quota = await checker.CheckQuotaAsync(
            Channel(settings: CookieSettings(Cookie + "; _ga=drop")),
            CancellationToken.None);

        Assert.Equal(
            new[] { InternalCredits, InternalSubscriptions },
            handler.Requests.Select(request => request.Url));
        var creditsRequest = handler.Requests[0];
        Assert.Equal("GET", creditsRequest.Method);
        Assert.Equal(Cookie, creditsRequest.Header("Cookie"));
        Assert.Equal(string.Empty, creditsRequest.Header("Authorization"));
        Assert.Equal("application/json, text/plain, */*", creditsRequest.Header("Accept"));
        Assert.Equal("en-US,en;q=0.9", creditsRequest.Header("Accept-Language"));
        Assert.Equal("https://commandcode.ai", creditsRequest.Header("Origin"));
        Assert.Equal("https://commandcode.ai/", creditsRequest.Header("Referer"));
        Assert.Contains("Chrome", creditsRequest.Header("User-Agent"), StringComparison.Ordinal);
        Assert.Equal(string.Empty, creditsRequest.Header("Content-Type"));
        Assert.Equal(string.Empty, creditsRequest.Body);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal("commandcode", quota.ProviderType);

        // 5h (2/16 = 12.5%), weekly (8/40 = 20%) and trusted monthly
        // ((80-60)/80 = 25%).
        Assert.Equal(3, quota.Limits.Count);
        Assert.Equal(QuotaWindows.FiveHours, quota.Limits[0].Window);
        Assert.Equal(QuotaWindows.Weekly, quota.Limits[1].Window);
        Assert.Equal(QuotaWindows.Monthly, quota.Limits[2].Window);
        Assert.Equal(0.125, quota.Limits[0].UsageRatio, 12);
        Assert.Equal(0.2, quota.Limits[1].UsageRatio, 12);
        Assert.Equal(0.25, quota.Limits[2].UsageRatio, 12);
        foreach (var limit in quota.Limits)
        {
            Assert.Equal(QuotaLimitTypes.SubscriptionCycle, limit.Type);
            Assert.Equal(ProviderQuotaStatuses.Available, limit.Status);
            Assert.True(limit.Ready);
        }

        var reset = DateTimeOffset.FromUnixTimeSeconds(2_000_000_000);
        Assert.Equal(reset, quota.NextResetAt);
        Assert.Equal(reset.AddHours(-5), quota.Limits[0].PeriodStart);
        Assert.Equal(reset.AddDays(-7), quota.Limits[1].PeriodStart);
        Assert.Null(quota.Limits[2].PeriodStart);

        Assert.Equal(
            """{"credits":{"monthly_limit_usd":80,"monthly_remaining_usd":60,"purchased_credits_usd":0},"current_period_end":"2026-10-01T00:00:00Z","plan_id":"individual-pro","plan_label":"","subscription_status":"active","windows":{"five_hour":{"cap_usd":16,"reset_time":"2033-05-18T03:33:20Z","usage_percent":12.5,"used_usd":2},"weekly":{"cap_usd":40,"reset_time":"2033-05-18T03:33:20Z","usage_percent":20,"used_usd":8}}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    /// <summary>TestCommandCodeQuotaChecker_APIKey go plan quota (commandcode_checker_test.go:519-541).</summary>
    [Fact]
    public async Task ApiKeyPathUsesTheAlphaEndpointsWithTheBearerToken()
    {
        const string credits =
            """{"credits":{"monthlyCredits":9.09842701,"purchasedCredits":0,"freeCredits":0},"windowLimits":{"fiveHour":{"used":0.104066178,"cap":3},"weekly":{"used":0.90157299,"cap":6}}}""";
        const string subscriptions =
            """{"success":true,"data":{"planId":"individual-go","status":"active"}}""";

        var checker = CreateChecker(out var handler);
        handler.OnSend = request => new Uri(request.Url).AbsolutePath.EndsWith(
            "/subscriptions", StringComparison.Ordinal)
            ? JsonResponse(subscriptions)
            : JsonResponse(credits);

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"user_key"}"""),
            CancellationToken.None);

        Assert.Equal(
            new[] { AlphaCredits, AlphaSubscriptions },
            handler.Requests.Select(request => request.Url));
        foreach (var request in handler.Requests)
        {
            Assert.Equal("Bearer user_key", request.Header("Authorization"));
            Assert.Equal(string.Empty, request.Header("Cookie"));
        }

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal("individual-go", quota.RawData["plan_id"]);
        Assert.Equal(3, quota.Limits.Count);
        Assert.Equal(0.034688726, quota.Limits[0].UsageRatio, 6);
        Assert.Equal(0.150262165, quota.Limits[1].UsageRatio, 6);
        Assert.Equal(0.090157299, quota.Limits[2].UsageRatio, 6);
        Assert.Equal(10.0, Number(Map(quota.RawData["credits"]), "monthly_limit_usd"));
    }

    /// <summary>TestCommandCodeQuotaChecker_APIKey rejected-key fallback (commandcode_checker_test.go:557-578).</summary>
    [Fact]
    public async Task RejectedKeyFallsBackToTheStoredCookie()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = request =>
        {
            var path = new Uri(request.Url).AbsolutePath;
            if (path.StartsWith("/alpha/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            return path.EndsWith("/subscriptions", StringComparison.Ordinal)
                ? JsonResponse(SubscriptionsOk)
                : JsonResponse(CreditsOk);
        };

        var quota = await checker.CheckQuotaAsync(
            Channel(
                credentials: """{"apiKey":"surface_only_key"}""",
                settings: CookieSettings(Cookie)),
            CancellationToken.None);

        Assert.Equal(
            new[] { AlphaCredits, InternalCredits, InternalSubscriptions },
            handler.Requests.Select(request => request.Url));
        Assert.Equal("individual-pro", quota.RawData["plan_id"]);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
    }

    /// <summary>TestCommandCodeQuotaChecker_APIKey rejected key without a cookie (commandcode_checker_test.go:580-588).</summary>
    [Fact]
    public async Task RejectedKeyWithoutACookieSurfacesTheKeyError()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var error = await Assert.ThrowsAsync<InvalidProviderQuotaCredentialsException>(
            () => checker.CheckQuotaAsync(
                Channel(credentials: """{"apiKey":"bad_key"}"""),
                CancellationToken.None));

        Assert.Equal(
            "provider quota credentials are invalid: Command Code billing API returned 401 (invalid API key?)",
            error.Message);
        Assert.Equal(new[] { AlphaCredits }, handler.Requests.Select(request => request.Url));
    }

    /// <summary>TestCommandCodeQuotaChecker_APIKey "api key wins over an unusable cookie" (commandcode_checker_test.go:543-555).</summary>
    [Fact]
    public async Task AnUnusableCookieDoesNotBlockTheApiKey()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = request => new Uri(request.Url).AbsolutePath.EndsWith(
            "/subscriptions", StringComparison.Ordinal)
            ? JsonResponse(SubscriptionsOk)
            : JsonResponse(CreditsOk);

        var quota = await checker.CheckQuotaAsync(
            Channel(
                credentials: """{"apiKey":"user_key"}""",
                settings: CookieSettings("_ga=foreign-cookie")),
            CancellationToken.None);

        Assert.Equal(
            new[] { AlphaCredits, AlphaSubscriptions },
            handler.Requests.Select(request => request.Url));
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
    }

    [Fact]
    public async Task InvalidStoredCookieFailsBeforeAnyRequest()
    {
        var executor = OkExecutor();
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidProviderQuotaCredentialsException>(
            () => checker.CheckQuotaAsync(
                Channel(settings: CookieSettings("_ga=only")),
                CancellationToken.None));

        Assert.Equal(
            "provider quota credentials are invalid: invalid Command Code auth cookie: "
            + "no Command Code session_token cookie found",
            error.Message);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task NoKeyAndNoCookieFailsBeforeAnyRequest()
    {
        var executor = OkExecutor();
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidProviderQuotaCredentialsException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "provider quota credentials are invalid: channel has no Command Code API key or quota cookie",
            error.Message);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task WhitespaceOnlyCredentialsAreNoCredential()
    {
        var executor = OkExecutor();
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidProviderQuotaCredentialsException>(
            () => checker.CheckQuotaAsync(
                Channel(credentials: """{"apiKeys":["   ",""]}""", settings: CookieSettings("  ")),
                CancellationToken.None));

        Assert.Equal(
            "provider quota credentials are invalid: channel has no Command Code API key or quota cookie",
            error.Message);
        Assert.Empty(executor.Requests);
    }

    // ------------------------------------------------------------------
    // Optional subscriptions endpoint
    // ------------------------------------------------------------------

    /// <summary>TestCommandCodeQuotaChecker_CheckQuota subscription failure (commandcode_checker_test.go:168-181).</summary>
    [Fact]
    public async Task SubscriptionFailureDegradesToTheCreditsResult()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = request => new Uri(request.Url).AbsolutePath.EndsWith(
            "/subscriptions", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : JsonResponse(CreditsOk);

        var quota = await checker.CheckQuotaAsync(
            Channel(settings: CookieSettings(Cookie)),
            CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        // Without subscription the monthly denominator is not trusted.
        Assert.Equal(2, quota.Limits.Count);
        Assert.False(Map(quota.RawData["credits"]).ContainsKey("monthly_limit_usd"));
    }

    [Fact]
    public async Task SubscriptionParseFailureOnlyDropsThePlanInfo()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = request => new Uri(request.Url).AbsolutePath.EndsWith(
            "/subscriptions", StringComparison.Ordinal)
            ? JsonResponse("not json")
            : JsonResponse(CreditsOk);

        var quota = await checker.CheckQuotaAsync(
            Channel(settings: CookieSettings(Cookie)),
            CancellationToken.None);

        Assert.Equal(2, quota.Limits.Count);
        Assert.False(Map(quota.RawData["credits"]).ContainsKey("monthly_limit_usd"));
        Assert.Equal("", quota.RawData["plan_id"]);
        Assert.Equal("", quota.RawData["subscription_status"]);
    }

    /// <summary>TestCommandCodeQuotaChecker_CheckQuota plan mismatch (commandcode_checker_test.go:183-198).</summary>
    [Fact]
    public async Task PlanMismatchOmitsTheMonthlyDenominatorButKeepsTheBalance()
    {
        const string credits =
            """{"credits":{"monthly_remaining_usd":5,"windowLimits":{"five_hour":{"used_usd":1,"cap_usd":16},"weekly":{"used_usd":2,"cap_usd":40}}}}""";
        const string subscriptions = """{"planId":"individual-ultra","status":"active"}""";

        var checker = CreateChecker(out var handler);
        handler.OnSend = request => new Uri(request.Url).AbsolutePath.EndsWith(
            "/subscriptions", StringComparison.Ordinal)
            ? JsonResponse(subscriptions)
            : JsonResponse(credits);

        var quota = await checker.CheckQuotaAsync(
            Channel(settings: CookieSettings(Cookie)),
            CancellationToken.None);

        // Ultra's table (90/180) does not match the wire caps (16/40).
        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(5.0, Number(Map(quota.RawData["credits"]), "monthly_remaining_usd"));
        Assert.False(Map(quota.RawData["credits"]).ContainsKey("monthly_limit_usd"));
    }

    // ------------------------------------------------------------------
    // Pay-as-you-go branch
    // ------------------------------------------------------------------

    /// <summary>TestCommandCodeQuotaChecker_CheckQuota top-up (commandcode_checker_test.go:200-209).</summary>
    [Fact]
    public async Task TopUpBalanceWithoutWindowsIsAvailable()
    {
        var checker = CreateChecker(OkExecutor(
            """{"credits":{"monthly_remaining_usd":0,"purchased_credits_usd":25}}"""));

        var quota = await checker.CheckQuotaAsync(
            Channel(settings: CookieSettings(Cookie)),
            CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaWindows.Monthly, limit.Window);
        Assert.Equal(QuotaLimitTypes.SubscriptionCycle, limit.Type);
        Assert.Equal(0.0, limit.UsageRatio);
        Assert.False(quota.RawData.ContainsKey("windows"));
    }

    /// <summary>TestCommandCodeQuotaChecker_CheckQuota exhausted balance (commandcode_checker_test.go:211-219).</summary>
    [Fact]
    public async Task NoWindowsAndNoBalanceIsExhausted()
    {
        var checker = CreateChecker(OkExecutor("""{"credits":{"monthly_remaining_usd":0}}"""));

        var quota = await checker.CheckQuotaAsync(
            Channel(settings: CookieSettings(Cookie)),
            CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);
        Assert.False(limit.Ready);
    }

    [Theory]
    [InlineData("""{"credits":{"monthlyCredits":25}}""", "available", true)]
    [InlineData("""{"credits":{"purchasedCredits":0.5}}""", "available", true)]
    [InlineData("""{"credits":{"monthlyCredits":0}}""", "exhausted", false)]
    [InlineData("""{"credits":{"monthlyCredits":-5}}""", "exhausted", false)]
    [InlineData("""{"credits":{"monthlyCredits":-5,"purchasedCredits":3}}""", "available", true)]
    public void PayAsYouGoAvailabilityNeedsAnyPositiveBalance(
        string body,
        string expectedStatus,
        bool expectedReady)
    {
        var quota = Parse(body);

        Assert.Equal(expectedStatus, quota.Status);
        Assert.Equal(expectedReady, quota.Ready);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaWindows.Monthly, limit.Window);
        Assert.Equal(expectedStatus, limit.Status);
        Assert.Equal(0.0, limit.UsageRatio);
    }

    // ------------------------------------------------------------------
    // Status mapping
    // ------------------------------------------------------------------

    /// <summary>TestCommandCodeQuotaChecker_CheckQuota 401 (commandcode_checker_test.go:221-231).</summary>
    [Fact]
    public async Task UnauthorizedMapsToTheCookieCredentialError()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var error = await Assert.ThrowsAsync<InvalidProviderQuotaCredentialsException>(
            () => checker.CheckQuotaAsync(
                Channel(settings: CookieSettings("__Secure-commandcode_prod_.session_token=expired")),
                CancellationToken.None));

        Assert.Equal(
            "provider quota credentials are invalid: Command Code billing API returned 401 (expired session cookie?)",
            error.Message);
    }

    /// <summary>TestCommandCodeQuotaChecker_APIKey bad key on a 403 (commandcode_checker_test.go:599-606).</summary>
    [Fact]
    public async Task ForbiddenWithAKeyIsAKeyCredentialError()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAsync<InvalidProviderQuotaCredentialsException>(
            () => checker.CheckQuotaAsync(
                Channel(credentials: """{"apiKey":"bad_key"}"""),
                CancellationToken.None));

        Assert.Equal(
            "provider quota credentials are invalid: Command Code billing API returned 403 (invalid API key?)",
            error.Message);
    }

    /// <summary>TestCommandCodeQuotaChecker_CheckQuota 429 (commandcode_checker_test.go:233-240).</summary>
    [Fact]
    public async Task RateLimitKeepsTheGenericMessageAndDoesNotFallBack()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(
                Channel(
                    credentials: """{"apiKey":"user_key"}""",
                    settings: CookieSettings(Cookie)),
                CancellationToken.None));

        Assert.Equal("Command Code billing API returned 429", error.Message);
        // Only the alpha credits request ran: a non-credential failure never
        // degrades to the cookie surface.
        Assert.Equal(new[] { AlphaCredits }, handler.Requests.Select(request => request.Url));
    }

    [Fact]
    public async Task CreditsServerErrorWithAKeyAndCookieDoesNotFallBack()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(
                Channel(
                    credentials: """{"apiKey":"user_key"}""",
                    settings: CookieSettings(Cookie)),
                CancellationToken.None));

        Assert.Equal("Command Code billing API returned 500", error.Message);
        Assert.Equal(new[] { AlphaCredits }, handler.Requests.Select(request => request.Url));
    }

    [Fact]
    public async Task ExecutorHttpErrorMapsToTheStatusError()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new HttpClientError(
                "GET", AlphaCredits, 403, "403 Forbidden"),
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidProviderQuotaCredentialsException>(
            () => checker.CheckQuotaAsync(
                Channel(credentials: """{"apiKey":"bad_key"}"""),
                CancellationToken.None));

        Assert.Equal(
            "provider quota credentials are invalid: Command Code billing API returned 403 (invalid API key?)",
            error.Message);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task TransportFailureIsWrapped()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new InvalidOperationException("boom"),
        });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(
                Channel(credentials: """{"apiKey":"user_key"}"""),
                CancellationToken.None));

        Assert.Equal("Command Code billing request failed: boom", error.Message);
    }

    /// <summary>TestCommandCodeQuotaChecker_CheckQuota malformed JSON (commandcode_checker_test.go:242-249).</summary>
    [Fact]
    public async Task MalformedCreditsJsonUsesTheParsePrefix()
    {
        var checker = CreateChecker(OkExecutor("not json"));

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(
                Channel(settings: CookieSettings(Cookie)),
                CancellationToken.None));

        Assert.Equal(
            "parse Command Code credits response: invalid character 'o' in literal null (expecting 'u')",
            error.Message);
        Assert.IsType<JsonException>(error.InnerException);
    }

    [Fact]
    public async Task NonSuccess2xxBodiesAreStillParsed()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = 201,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
                Body = Encoding.UTF8.GetBytes("""{"credits":{"monthlyCredits":5}}"""),
            },
        });

        var quota = await checker.CheckQuotaAsync(
            Channel(settings: CookieSettings(Cookie)),
            CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Single(quota.Limits);
    }

    [Fact]
    public async Task RedirectStatusUsesTheGenericMessage()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = 302,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
                Body = Encoding.UTF8.GetBytes("redirect"),
            },
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(
                Channel(settings: CookieSettings(Cookie)),
                CancellationToken.None));

        Assert.Equal("Command Code billing API returned 302", error.Message);
    }

    [Fact]
    public async Task EmptyCreditsBodyIsAParseError()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = 204,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
                Body = [],
            },
        });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(
                Channel(settings: CookieSettings(Cookie)),
                CancellationToken.None));

        Assert.Equal(
            "parse Command Code credits response: unexpected end of JSON input",
            error.Message);
        Assert.IsType<JsonException>(error.InnerException);
    }

    // ------------------------------------------------------------------
    // Request shape
    // ------------------------------------------------------------------

    [Fact]
    public async Task RequestCarriesTheBillingHeadersAndTheBearerToken()
    {
        var executor = OkExecutor(CreditsOk);
        var checker = CreateChecker(executor);

        await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"user_key"}"""),
            CancellationToken.None);

        Assert.Equal(2, executor.Requests.Count);
        var request = executor.Requests[0];
        Assert.Equal("GET", request.Method);
        Assert.Equal(AlphaCredits, request.Url);
        Assert.Equal(
            "application/json, text/plain, */*",
            HttpClientUtils.HeaderGet(request.Headers, "Accept"));
        Assert.Equal("en-US,en;q=0.9", HttpClientUtils.HeaderGet(request.Headers, "Accept-Language"));
        Assert.Equal(
            CommandCodeQuotaChecker.QuotaUserAgent,
            HttpClientUtils.HeaderGet(request.Headers, "User-Agent"));
        Assert.Equal("https://commandcode.ai", HttpClientUtils.HeaderGet(request.Headers, "Origin"));
        Assert.Equal("https://commandcode.ai/", HttpClientUtils.HeaderGet(request.Headers, "Referer"));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(request.Headers, "Cookie"));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Null(request.Body);
        Assert.NotNull(request.Auth);
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal("user_key", request.Auth.ApiKey);
    }

    [Fact]
    public async Task CookieRequestCarriesTheNormalizedCookieHeaderOnly()
    {
        var executor = OkExecutor(CreditsOk);
        var checker = CreateChecker(executor);

        await checker.CheckQuotaAsync(
            Channel(settings: CookieSettings("Cookie: " + Cookie + "; unused=1; _ga=x")),
            CancellationToken.None);

        Assert.Equal(2, executor.Requests.Count);
        var request = executor.Requests[0];
        Assert.Equal(Cookie, HttpClientUtils.HeaderGet(request.Headers, "Cookie"));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(request.Headers, "Authorization"));
        Assert.Null(request.Auth);
    }

    /// <summary>TestCommandCodeQuotaChecker proxy routing (compare charm_hyper_checker_test.go:245-274).</summary>
    [Fact]
    public async Task ProxySettingsRouteThroughTheProxyExecutor()
    {
        var direct = OkExecutor();
        var proxied = OkExecutor(CreditsOk);
        var factory = new ScriptedProxyExecutorFactory { Executor = proxied };
        var checker = CreateChecker(direct, factory);

        var quota = await checker.CheckQuotaAsync(
            Channel(
                settings: """{"proxy":{"type":"url","url":"http://proxy.local:8080","username":"user","password":"pass","disableConnectionReuse":true},"providerQuota":{"commandCode":{"authCookie":"__Secure-commandcode_prod_.session_token=tok"}}}"""),
            CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        var proxy = Assert.Single(factory.Requests);
        Assert.Equal("http://proxy.local:8080", proxy.Url);
        Assert.Equal(2, proxied.Requests.Count);
        Assert.Empty(direct.Requests);
        Assert.Equal(InternalCredits, proxied.Requests[0].Url);
    }

    // ------------------------------------------------------------------
    // Credits parsing (parseCommandCodeCredits, commandcode_checker.go:326-478)
    // ------------------------------------------------------------------

    /// <summary>TestCommandCodeCamelSnakeAndNestedWindowLimits (commandcode_checker_test.go:370-381).</summary>
    [Fact]
    public void RootWindowLimitsAndSnakeCaseCreditsAreAccepted()
    {
        const string body =
            """{"windowLimits":{"fiveHour":{"usedUsd":1,"capUsd":16,"usagePercent":6.25,"reset":2000000000},"weekly":{"usedUsd":2,"capUsd":40,"usagePercent":5,"reset":2000000000}},"monthly_remaining_usd":60,"purchased_credits_usd":0}""";

        var quota = Parse(body);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(QuotaWindows.FiveHours, quota.Limits[0].Window);
        Assert.Equal(0.0625, quota.Limits[0].UsageRatio, 12);
        Assert.Equal(60.0, Number(Map(quota.RawData["credits"]), "monthly_remaining_usd"));
        Assert.False(Map(quota.RawData["credits"]).ContainsKey("monthly_limit_usd"));
    }

    /// <summary>TestCommandCodeRealWirePayload (commandcode_checker_test.go:389-426).</summary>
    [Fact]
    public void RealWirePayloadKeepsMillisecondResetsAndTheGoatAllowance()
    {
        var fiveHourReset = Now.AddHours(2).AddMilliseconds(798);
        var weeklyReset = Now.AddDays(3).AddMilliseconds(618);
        var credits =
            """{"credits":{"belowThreshold":false,"creditThreshold":0,"monthlyCredits":24.7335198407,"purchasedCredits":0,"premiumMonthlyCredits":0,"opensourceMonthlyCredits":24.7335198407},"windowLimits":{"limited":true,"exceeded":null,"fiveHour":{"used":0.3185113898,"cap":14,"exceeded":false,"resetAt":"""
            + fiveHourReset.ToUnixTimeMilliseconds()
            + """},"weekly":{"used":1.2796088158,"cap":35,"exceeded":false,"resetAt":"""
            + weeklyReset.ToUnixTimeMilliseconds()
            + "}}}";
        const string subscriptions =
            """{"success":true,"data":{"id":"sub_x","status":"active","planId":"individual-goat","currentPeriodEnd":"2026-09-19T12:02:05.000Z","metadata":{"commandCode":"true"}}}""";

        var quota = Parse(credits, subscriptions);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal(3, quota.Limits.Count);
        Assert.Equal(QuotaWindows.FiveHours, quota.Limits[0].Window);
        Assert.Equal(QuotaWindows.Weekly, quota.Limits[1].Window);
        Assert.Equal(QuotaWindows.Monthly, quota.Limits[2].Window);

        var creditsRaw = Map(quota.RawData["credits"]);
        Assert.Equal(24.7335198407, Number(creditsRaw, "monthly_remaining_usd"), 10);
        Assert.Equal(0.0, Number(creditsRaw, "purchased_credits_usd"));
        Assert.Equal(70.0, Number(creditsRaw, "monthly_limit_usd"), 10);
        Assert.Equal("individual-goat", quota.RawData["plan_id"]);
        Assert.Equal("active", quota.RawData["subscription_status"]);
        Assert.Equal("2026-09-19T12:02:05.000Z", quota.RawData["current_period_end"]);

        // rawData renders the reset as UTC RFC3339 text, dropping subseconds
        // (C217 family); the instant itself keeps millisecond precision.
        var windows = Map(quota.RawData["windows"]);
        Assert.Equal(
            fiveHourReset.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Map(windows["five_hour"])["reset_time"]);
        Assert.Equal(
            weeklyReset.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            Map(windows["weekly"])["reset_time"]);
        Assert.NotNull(quota.NextResetAt);
        Assert.Equal(fiveHourReset.ToUnixTimeMilliseconds(), quota.NextResetAt!.Value.ToUnixTimeMilliseconds());
    }

    /// <summary>TestCommandCodeGoPlanAllowance (commandcode_checker_test.go:428-448).</summary>
    [Fact]
    public void GoPlanGetsTheTenDollarDenominatorFromTheLocalTable()
    {
        var fiveHourReset = Now.AddHours(2).AddMilliseconds(599);
        var weeklyReset = Now.AddDays(3).AddMilliseconds(234);
        var credits =
            """{"credits":{"belowThreshold":false,"creditThreshold":0,"monthlyCredits":9.09842701,"purchasedCredits":0,"freeCredits":0},"windowLimits":{"limited":true,"exceeded":null,"fiveHour":{"used":0.104066178,"cap":3,"exceeded":false,"resetAt":"""
            + fiveHourReset.ToUnixTimeMilliseconds()
            + """},"weekly":{"used":0.90157299,"cap":6,"exceeded":false,"resetAt":"""
            + weeklyReset.ToUnixTimeMilliseconds()
            + "}}}";
        const string subscriptions =
            """{"success":true,"data":{"id":"sub_x","status":"active","planId":"individual-go","currentPeriodEnd":"2026-10-10T08:20:47.000Z"}}""";

        var quota = Parse(credits, subscriptions);

        Assert.Equal(3, quota.Limits.Count);
        Assert.Equal(QuotaWindows.Monthly, quota.Limits[2].Window);
        Assert.Equal(0.09016, quota.Limits[2].UsageRatio, 5);
        var creditsRaw = Map(quota.RawData["credits"]);
        Assert.Equal(10.0, Number(creditsRaw, "monthly_limit_usd"), 10);
        Assert.Equal(9.09842701, Number(creditsRaw, "monthly_remaining_usd"), 10);
        Assert.NotNull(quota.NextResetAt);
        Assert.Equal(fiveHourReset.ToUnixTimeMilliseconds(), quota.NextResetAt!.Value.ToUnixTimeMilliseconds());
    }

    /// <summary>TestCommandCodePlanAllowanceRows (commandcode_checker_test.go:452-504).</summary>
    [Theory]
    [InlineData("individual-go", "", "3", "6", 10.0)]
    [InlineData("individual-goat", "", "14", "35", 70.0)]
    [InlineData("individual-pro", "", "16", "40", 80.0)]
    [InlineData("individual-pro", "", "6", "15", 30.0)]
    [InlineData("individual-pro-v1", "", "16", "40", 80.0)]
    [InlineData("individual-max", "", "45", "90", 150.0)]
    [InlineData("individual-ultra", "", "90", "180", 300.0)]
    [InlineData("", "Team Pro", "12", "24", 40.0)]
    [InlineData(" individual-go ", "", "3", "6", 10.0)]
    [InlineData("individual-pro", "", "8", "20", null)]
    [InlineData("unknown-plan", "", "16", "40", null)]
    public void PlanRowsSelectTheMonthlyDenominator(
        string planId,
        string planLabel,
        string fiveHourCap,
        string weeklyCap,
        double? expectedMonthlyLimit)
    {
        var credits =
            """{"credits":{"monthlyCredits":1},"windowLimits":{"fiveHour":{"used":0,"cap":"""
            + fiveHourCap
            + """},"weekly":{"used":0,"cap":"""
            + weeklyCap
            + "}}}";
        var subscriptions =
            """{"success":true,"data":{"planId":"""
            + JsonSerializer.Serialize(planId)
            + ""","planLabel":"""
            + JsonSerializer.Serialize(planLabel)
            + ",\"status\":\"active\"}}";

        var quota = Parse(credits, subscriptions);
        var creditsRaw = Map(quota.RawData["credits"]);

        if (expectedMonthlyLimit is { } limit)
        {
            Assert.Equal(3, quota.Limits.Count);
            Assert.Equal(limit, Number(creditsRaw, "monthly_limit_usd"), 10);
            Assert.Equal((limit - 1) / limit, quota.Limits[2].UsageRatio, 10);
        }
        else
        {
            Assert.Equal(2, quota.Limits.Count);
            Assert.False(creditsRaw.ContainsKey("monthly_limit_usd"));
        }
    }

    [Theory]
    [InlineData("""{"five_hour":{"used_usd":2,"cap_usd":16},"weekly":{"used_usd":8,"cap_usd":40}}""", 0.125)]
    [InlineData("""{"fiveHour":{"usedUsd":2,"capUsd":16},"weekly":{"usedUsd":8,"capUsd":40}}""", 0.125)]
    [InlineData("""{"5h":{"used":2,"cap":16},"weekly":{"used":8,"cap":40}}""", 0.125)]
    [InlineData("""{"FIVE_HOUR":{"USED_USD":2,"CAP_USD":16},"weekly":{"used_usd":8,"cap_usd":40}}""", 0.125)]
    [InlineData("""{"five_hour":{"used_usd":2,"limitUsd":16},"weekly":{"used_usd":8,"limit_usd":40}}""", 0.125)]
    [InlineData("""{"five_hour":{"usage_percent":37.5},"weekly":{"used_usd":8,"cap_usd":40}}""", 0.375)]
    [InlineData("""{"five_hour":{"used_usd":2,"cap_usd":0},"weekly":{"used_usd":8,"cap_usd":40}}""", 0.0)]
    [InlineData("""{"five_hour":{"used_usd":2,"cap_usd":16,"usage_percent":0},"weekly":{"used_usd":8,"cap_usd":40}}""", 0.125)]
    public void WindowKeyAndFieldSpellingsFoldAscii(string windowLimits, double expectedFiveHourRatio)
    {
        var quota = Parse("""{"windowLimits":""" + windowLimits + "}");

        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(QuotaWindows.FiveHours, quota.Limits[0].Window);
        Assert.Equal(expectedFiveHourRatio, quota.Limits[0].UsageRatio, 12);
    }

    [Fact]
    public void RootWindowLimitsWinOverTheNestedCreditsObject()
    {
        const string body =
            """{"credits":{"windowLimits":{"five_hour":{"used_usd":5,"cap_usd":10},"weekly":{"used_usd":5,"cap_usd":10}}},"windowLimits":{"five_hour":{"used_usd":1,"cap_usd":10},"weekly":{"used_usd":1,"cap_usd":10}}}""";

        var quota = Parse(body);

        Assert.Equal(0.1, quota.Limits[0].UsageRatio, 12);
    }

    [Theory]
    [InlineData(14.0, "warning", "warning", true)]
    [InlineData(0.0, "exhausted", "exhausted", false)]
    [InlineData(70.0, "available", "available", true)]
    [InlineData(56.0, "available", "available", true)]
    [InlineData(13.999999, "warning", "warning", true)]
    public void MonthlyRatioUsesTheWarningAndExhaustedThresholds(
        double remaining,
        string expectedStatus,
        string expectedLimitStatus,
        bool expectedReady)
    {
        var credits =
            """{"credits":{"monthlyCredits":"""
            + remaining.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + """},"windowLimits":{"fiveHour":{"used":0,"cap":14},"weekly":{"used":0,"cap":35}}}""";
        const string subscriptions =
            """{"success":true,"data":{"planId":"individual-goat","status":"active"}}""";

        var quota = Parse(credits, subscriptions);

        Assert.Equal(3, quota.Limits.Count);
        Assert.Equal(QuotaWindows.Monthly, quota.Limits[2].Window);
        Assert.Equal(expectedStatus, quota.Status);
        Assert.Equal(expectedReady, quota.Ready);
        Assert.Equal(expectedLimitStatus, quota.Limits[2].Status);
        Assert.Equal((70 - remaining) / 70, quota.Limits[2].UsageRatio, 10);
    }

    [Theory]
    [InlineData("""{"five_hour":{"used_usd":8,"cap_usd":10},"weekly":{"used_usd":0,"cap_usd":40}}""", "warning")]
    [InlineData("""{"five_hour":{"used_usd":10,"cap_usd":10},"weekly":{"used_usd":0,"cap_usd":40}}""", "exhausted")]
    [InlineData("""{"five_hour":{"used_usd":7.999,"cap_usd":10},"weekly":{"used_usd":0,"cap_usd":40}}""", "available")]
    public void WindowRatioUsesTheSameThresholds(string windowLimits, string expectedStatus)
    {
        var quota = Parse("""{"windowLimits":""" + windowLimits + "}");

        Assert.Equal(expectedStatus, quota.Limits[0].Status);
        Assert.Equal(expectedStatus, quota.Status);
    }

    [Fact]
    public void DuplicateCreditsKeysLetTheLastValueWin()
    {
        var quota = Parse("""{"credits":{"monthlyCredits":60,"monthlyCredits":10}}""");

        Assert.Equal(10.0, Number(Map(quota.RawData["credits"]), "monthly_remaining_usd"));
    }

    [Fact]
    public void FoldEqualCreditsKeysResolveInDocumentOrder()
    {
        // Go iterates the map randomly when two spellings fold together; the
        // port keeps the first document-order spelling (C052/C065/C172 family).
        var quota = Parse("""{"credits":{"monthlyCredits":60,"MonthlyCredits":10}}""");

        Assert.Equal(60.0, Number(Map(quota.RawData["credits"]), "monthly_remaining_usd"));
    }

    [Fact]
    public void TrustedMonthlyPrefersTheWireLimitWhenPresent()
    {
        const string credits =
            """{"credits":{"monthlyCredits":60,"monthlyLimitUsd":999},"windowLimits":{"fiveHour":{"used":2,"cap":16},"weekly":{"used":8,"cap":40}}}""";
        const string subscriptions =
            """{"success":true,"data":{"planId":"individual-pro","status":"active"}}""";

        var quota = Parse(credits, subscriptions);

        Assert.Equal(3, quota.Limits.Count);
        Assert.Equal(999.0, Number(Map(quota.RawData["credits"]), "monthly_limit_usd"));
        Assert.Equal(0.25, quota.Limits[2].UsageRatio, 12);
    }

    [Fact]
    public void RemainingAboveTheAllowanceSkipsTheDenominator()
    {
        const string credits =
            """{"credits":{"monthlyCredits":100},"windowLimits":{"fiveHour":{"used":2,"cap":16},"weekly":{"used":8,"cap":40}}}""";
        const string subscriptions =
            """{"success":true,"data":{"planId":"individual-pro","status":"active"}}""";

        var quota = Parse(credits, subscriptions);

        Assert.Equal(2, quota.Limits.Count);
        Assert.False(Map(quota.RawData["credits"]).ContainsKey("monthly_limit_usd"));
    }

    [Fact]
    public void NegativeRemainingTrustsThePlanAndClampsTheMonthlyLimit()
    {
        const string credits =
            """{"credits":{"monthlyCredits":-5},"windowLimits":{"fiveHour":{"used":2,"cap":14},"weekly":{"used":8,"cap":35}}}""";
        const string subscriptions =
            """{"success":true,"data":{"planId":"individual-goat","status":"active"}}""";

        var quota = Parse(credits, subscriptions);

        // used = 70 - (-5) = 75 -> a ratio above 1 sets the limit and the
        // overall status to exhausted; normalization clamps the ratio to 1.
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        var monthly = quota.Limits[2];
        Assert.Equal(1.0, monthly.UsageRatio);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, monthly.Status);
        Assert.False(monthly.Ready);
    }

    [Fact]
    public void NumericPlanFieldsRenderWithGoFormatting()
    {
        const string credits =
            """{"credits":{"monthlyCredits":5},"windowLimits":{"fiveHour":{"used":0,"cap":16},"weekly":{"used":0,"cap":40}}}""";
        const string subscriptions =
            """{"success":true,"data":{"planId":5,"planLabel":3.5,"status":true}}""";

        var quota = Parse(credits, subscriptions);

        Assert.Equal("5", quota.RawData["plan_id"]);
        Assert.Equal("3.5", quota.RawData["plan_label"]);
        Assert.Equal("", quota.RawData["subscription_status"]);
    }

    [Fact]
    public void RawCreditsAreOmittedWithoutBalanceFields()
    {
        const string body =
            """{"windowLimits":{"five_hour":{"used_usd":2,"cap_usd":16},"weekly":{"used_usd":8,"cap_usd":40}}}""";

        var quota = Parse(body);

        Assert.False(quota.RawData.ContainsKey("credits"));
        Assert.True(quota.RawData.ContainsKey("windows"));
        Assert.Equal(2, quota.Limits.Count);
    }

    [Fact]
    public void NextResetAtIsTheEarliestWindowReset()
    {
        const string body =
            """{"credits":{"monthlyCredits":5},"windowLimits":{"five_hour":{"used_usd":2,"cap_usd":16,"reset_time":2000000000},"weekly":{"used_usd":8,"cap_usd":40,"reset_time":1900000000}}}""";

        var quota = Parse(body);

        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1_900_000_000),
            quota.NextResetAt);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(2_000_000_000),
            quota.Limits[0].NextResetAt);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1_900_000_000),
            quota.Limits[1].NextResetAt);
    }

    [Fact]
    public void PastResetsAreClearedByNormalizationButStayInRawData()
    {
        const string body =
            """{"credits":{"monthlyCredits":5},"windowLimits":{"five_hour":{"used_usd":2,"cap_usd":16,"reset_time":1000000000},"weekly":{"used_usd":8,"cap_usd":40,"reset_time":1000000000}}}""";

        var quota = Parse(body);

        Assert.Null(quota.NextResetAt);
        Assert.All(quota.Limits, limit => Assert.Null(limit.NextResetAt));
        var windows = Map(quota.RawData["windows"]);
        Assert.Equal("2001-09-09T01:46:40Z", Map(windows["five_hour"])["reset_time"]);
    }

    // ------------------------------------------------------------------
    // JSON decoding (parseJSONObject, commandcode_checker.go:622-631)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("[1]", "json: cannot unmarshal array into Go value of type map[string]interface {}")]
    [InlineData("\"x\"", "json: cannot unmarshal string into Go value of type map[string]interface {}")]
    [InlineData("true", "json: cannot unmarshal bool into Go value of type map[string]interface {}")]
    [InlineData("1", "json: cannot unmarshal number into Go value of type map[string]interface {}")]
    [InlineData("null", "empty JSON object")]
    [InlineData("", "unexpected end of JSON input")]
    [InlineData(
        """{"credits":{"monthlyCredits":1e999}}""",
        "json: cannot unmarshal number 1e999 into Go value of type float64")]
    public void JsonDecodeErrorsUseGoWording(string body, string expectedInner)
    {
        var error = Assert.Throws<WrappedException>(() => Parse(body));

        Assert.Equal("parse Command Code credits response: " + expectedInner, error.Message);
        Assert.IsType<JsonException>(error.InnerException);
    }

    // ------------------------------------------------------------------
    // Reset parsing (parseCommandCodeReset, commandcode_checker.go:558-594)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(2000000000.0, "2033-05-18T03:33:20Z")]
    [InlineData(1767225600000.0, "2026-01-01T00:00:00Z")]
    [InlineData(1767225600000.9, "2026-01-01T00:00:00Z")]
    [InlineData("2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z")]
    [InlineData("2026-01-01T08:00:00+08:00", "2026-01-01T00:00:00Z")]
    [InlineData("2026-01-01T00:00:00.123Z", "2026-01-01T00:00:00Z")]
    [InlineData(" 2000000000 ", "2033-05-18T03:33:20Z")]
    [InlineData(0.0, null)]
    [InlineData(-5.0, null)]
    [InlineData(1000000000000001.0, null)]
    [InlineData("garbage", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(true, null)]
    public void ResetValuesParseSecondsMillisecondsAndRfc3339(
        object? value,
        string? expectedIso)
    {
        var parsed = CommandCodeQuotaChecker.ParseReset(value);

        if (expectedIso is null)
        {
            Assert.Null(parsed);
            return;
        }

        Assert.NotNull(parsed);
        Assert.Equal(
            expectedIso,
            parsed!.Value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void NonScalarResetValuesAreIgnored()
    {
        Assert.Null(CommandCodeQuotaChecker.ParseReset(new Dictionary<string, object?>()));
        Assert.Null(CommandCodeQuotaChecker.ParseReset(new List<object?>()));
        Assert.Null(CommandCodeQuotaChecker.ParseReset(false));
    }

    // ------------------------------------------------------------------
    // Credential gate
    // ------------------------------------------------------------------

    /// <summary>TestHasCommandCodeQuotaCredentials (commandcode_checker_test.go:293-318).</summary>
    [Fact]
    public void NullChannelHasNoQuotaCredentials()
    {
        Assert.False(CommandCodeQuotaChecker.HasQuotaCredentials(null!));
    }

    [Theory]
    [InlineData("""{"apiKey":"user_key"}""", "{}", true)]
    [InlineData("""{"apiKeys":["user_key"]}""", "{}", true)]
    [InlineData("{}", """{"providerQuota":{"commandCode":{"authCookie":"commandcode_prod_.session_token=tok"}}}""", true)]
    [InlineData("{}", "{}", false)]
    [InlineData("{}", """{"providerQuota":{"commandCode":{"authCookie":"_ga=foreign"}}}""", true)]
    [InlineData("""{"apiKey":"user_key"}""", """{"providerQuota":{"commandCode":{"authCookie":"_ga=foreign"}}}""", true)]
    [InlineData("""{"apiKeys":["   ",""]}""", "{}", false)]
    [InlineData("{}", """{"providerQuota":{"commandCode":{"authCookie":"  "}}}""", false)]
    [InlineData("""{"apiKey":"{\"access_token\":\"oauth\"}"}""", "{}", false)]
    public void HasQuotaCredentialsCountsKeysAndConfiguredCookies(
        string credentials,
        string settings,
        bool expected)
    {
        Assert.Equal(
            expected,
            CommandCodeQuotaChecker.HasQuotaCredentials(
                Channel(credentials: credentials, settings: settings)));
    }

    /// <summary>TestCommandCodeQuotaChecker_SupportsChannel (commandcode_checker_test.go:262-291).</summary>
    [Theory]
    [InlineData("commandcode", """{"apiKey":"legacy_key"}""", "{}", true)]
    [InlineData("commandcode", """{"apiKeys":["user_key"]}""", "{}", true)]
    [InlineData("commandcode_anthropic", """{"apiKeys":["","user_key"]}""", "{}", true)]
    [InlineData("commandcode_anthropic", """{"apiKey":"legacy"}""", "{}", true)]
    [InlineData("commandcode_anthropic", "{}", "{}", false)]
    [InlineData("commandcode", "{}", "{}", false)]
    [InlineData("anthropic", """{"apiKey":"user_key"}""", "{}", false)]
    [InlineData("commandcode", """{"apiKey":"{\"access_token\":\"oauth\"}"}""", "{}", false)]
    [InlineData("commandcode", """{"apiKey":"{\"access_token\":\"oauth\"}","apiKeys":["user_key"]}""", "{}", true)]
    [InlineData("commandcode", "{}", """{"providerQuota":{"commandCode":{"authCookie":"commandcode_prod_.session_token=tok"}}}""", true)]
    [InlineData("commandcode", "{}", """{"providerQuota":{"commandCode":{"authCookie":"_ga=foreign"}}}""", true)]
    [InlineData("commandcode", """{"apiKeys":["   ",""]}""", "{}", false)]
    [InlineData("commandcode", "{}", """{"providerQuota":{"commandCode":{"authCookie":"   "}}}""", false)]
    public void SupportsChannelMatchesOnlyCommandCodeTypesWithCredentials(
        string channelType,
        string credentials,
        string settings,
        bool expected)
    {
        var checker = CreateChecker(OkExecutor());

        Assert.Equal(
            expected,
            checker.SupportsChannel(Channel(
                credentials: credentials,
                settings: settings,
                type: channelType)));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static CommandCodeQuotaChecker CreateChecker(
        out ClaudeCodeQuotaCheckerTests.RecordingHttpHandler handler)
    {
        handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new CommandCodeQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now));
    }

    private static CommandCodeQuotaChecker CreateChecker(
        ScriptedExecutor executor,
        ScriptedProxyExecutorFactory? factory = null)
        => new(
            executor,
            factory ?? new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now));

    private static ChannelRecord Channel(
        string? credentials = null,
        string settings = "{}",
        string type = "commandcode")
        => new()
        {
            Type = type,
            Credentials = credentials ?? "{}",
            Settings = settings,
        };

    private static string CookieSettings(string cookie)
        => "{\"providerQuota\":{\"commandCode\":{\"authCookie\":"
            + JsonSerializer.Serialize(cookie)
            + "}}}";

    private static QuotaData Parse(string credits, string? subscriptions = null)
        => CommandCodeQuotaChecker.ParseCredits(
            Encoding.UTF8.GetBytes(credits),
            subscriptions is null ? null : Encoding.UTF8.GetBytes(subscriptions),
            Now);

    private static Dictionary<string, object?> Map(object? value)
        => Assert.IsType<Dictionary<string, object?>>(value);

    private static double Number(Dictionary<string, object?> map, string key)
        => Assert.IsType<double>(map[key]);

    private static ScriptedExecutor OkExecutor(string body = CreditsOk) => new()
    {
        OnDo = (_, _, _) => Ok(body),
    };

    private static HttpResponseData Ok(string body) => new()
    {
        StatusCode = 200,
        Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
        Body = Encoding.UTF8.GetBytes(body),
    };

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}
