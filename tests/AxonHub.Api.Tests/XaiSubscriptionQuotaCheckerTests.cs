using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Xai.Subscription;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports internal/server/biz/provider_quota/xai_subscription_checker.go together
/// with the xai_subscription cases of xai_subscription_checker_test.go and the
/// xai fixture of provider_contract_fixtures_test.go: the weekly + monthly
/// billing requests, the credential resolution (typed oauth object over the
/// legacy apiKey JSON), the window merge and the reset handling. The wire shape
/// (URLs, auth, CLI headers) is locked through the real
/// <see cref="HttpExecutor"/> over a recording message handler; a bodyless
/// request loses its Content-Type header on the wire, which is the accepted
/// difference C206 (the checker still sets it on the request data, asserted
/// separately).
/// </summary>
public sealed class XaiSubscriptionQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private const string WeeklyBillingUrl = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";
    private const string MonthlyBillingUrl = "https://cli-chat-proxy.grok.com/v1/billing";

    private const string WeeklyWarningBody = """
        {"config":{"currentPeriod":{"type":"WEEKLY","end":"2099-07-16T03:25:00Z"},"creditUsagePercent":82.5}}
        """;

    private const string MonthlyHalfBody = """
        {"config":{"monthlyLimit":{"val":15000},"used":{"val":7500},"billingPeriodEnd":"2099-08-01T00:00:00Z"}}
        """;

    private const string WeeklyAvailableBody = """{"config":{"creditUsagePercent":10}}""";

    private const string MonthlyAvailableBody =
        """{"config":{"monthlyLimit":{"val":15000},"used":{"val":1500}}}""";

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaFetchesWeeklyThenMonthlyBillingAndMergesTheWindows()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = request => JsonResponse(
            request.Url == WeeklyBillingUrl ? WeeklyWarningBody : MonthlyHalfBody);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        var weeklyRequest = handler.Requests[0];
        var monthlyRequest = handler.Requests[1];
        Assert.Equal("GET", weeklyRequest.Method);
        Assert.Equal(WeeklyBillingUrl, weeklyRequest.Url);
        Assert.Equal("GET", monthlyRequest.Method);
        Assert.Equal(MonthlyBillingUrl, monthlyRequest.Url);

        foreach (var request in handler.Requests)
        {
            Assert.Equal("Bearer " + Token, request.Header("Authorization"));
            Assert.Equal("application/json", request.Header("Accept"));
            Assert.Equal("xai-grok-cli", request.Header("x-xai-token-auth"));
            Assert.Equal("0.2.114", request.Header("x-grok-client-version"));
            Assert.Equal(
                "grok-pager/0.2.114 grok-shell/0.2.114 (macos; aarch64)",
                request.Header("User-Agent"));
            // C206: the checker sets Content-Type on the request data, but the
            // port's executor only attaches content headers when a body exists,
            // so the wire loses it for these bodyless GETs (the reference
            // sends it).
            Assert.Equal(string.Empty, request.Header("Content-Type"));
            Assert.Equal(string.Empty, request.Body);
        }

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.Equal("xai_subscription", quota.ProviderType);
        Assert.True(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Limits[0].Status);
        Assert.Equal(QuotaWindows.Weekly, quota.Limits[0].Window);
        Assert.Equal(0.825, quota.Limits[0].UsageRatio, 9);
        Assert.Equal(
            new DateTimeOffset(2099, 7, 16, 3, 25, 0, TimeSpan.Zero),
            quota.Limits[0].NextResetAt);
        Assert.Equal(
            new DateTimeOffset(2099, 7, 9, 3, 25, 0, TimeSpan.Zero),
            quota.Limits[0].PeriodStart);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[1].Status);
        Assert.Equal(QuotaWindows.Monthly, quota.Limits[1].Window);
        Assert.Equal(0.5, quota.Limits[1].UsageRatio, 9);
        Assert.Equal(
            new DateTimeOffset(2099, 8, 1, 0, 0, 0, TimeSpan.Zero),
            quota.Limits[1].NextResetAt);
        Assert.Equal(
            new DateTimeOffset(2099, 7, 1, 0, 0, 0, TimeSpan.Zero),
            quota.Limits[1].PeriodStart);

        Assert.Equal(
            new DateTimeOffset(2099, 7, 16, 3, 25, 0, TimeSpan.Zero),
            quota.NextResetAt);
        Assert.Equal("SuperGrok", quota.RawData["plan_type"]);

        var billing = Assert.IsType<Dictionary<string, object?>>(quota.RawData["billing"]);
        var weekly = Assert.IsType<BillingWindow>(billing["weekly"]);
        Assert.Equal(82.5, weekly.UsagePercent);
        Assert.Equal("2099-07-16T03:25:00Z", weekly.ResetAt);
        var monthly = Assert.IsType<BillingWindow>(billing["monthly"]);
        Assert.Equal(150d, monthly.LimitUsd);
        Assert.Equal(75d, monthly.UsedUsd);
        Assert.Equal(50d, monthly.UsagePercent);
        Assert.Equal("2099-08-01T00:00:00Z", monthly.ResetAt);
    }

    [Fact]
    public async Task CheckQuotaKeepsTheBillingHeadersOnTheRequestData()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, request, _) => Ok(
                request.Url == WeeklyBillingUrl ? WeeklyAvailableBody : MonthlyAvailableBody),
        };
        var checker = CreateChecker(executor);

        await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(2, executor.Requests.Count);
        Assert.Equal(WeeklyBillingUrl, executor.Requests[0].Url);
        Assert.Equal(MonthlyBillingUrl, executor.Requests[1].Url);
        var request = executor.Requests[0];
        Assert.Equal("GET", request.Method);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Accept"));
        Assert.Equal(
            "xai-grok-cli",
            HttpClientUtils.HeaderGet(request.Headers, SubscriptionConstants.CliTokenAuthHeader));
        Assert.Equal(
            "0.2.114",
            HttpClientUtils.HeaderGet(request.Headers, SubscriptionConstants.CliClientVersionHeader));
        Assert.Equal(
            "grok-pager/0.2.114 grok-shell/0.2.114 (macos; aarch64)",
            HttpClientUtils.HeaderGet(request.Headers, "User-Agent"));
        Assert.Null(request.Body);
        Assert.Equal(AuthTypes.Bearer, request.Auth?.Type);
        Assert.Equal(Token, request.Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaResolvesTheChannelProxyOnceForBothFetches()
    {
        var baseExecutor = new ScriptedExecutor();
        var proxied = new ScriptedExecutor
        {
            OnDo = (_, request, _) => Ok(
                request.Url == WeeklyBillingUrl ? WeeklyAvailableBody : MonthlyAvailableBody),
        };
        var proxyFactory = new ScriptedProxyExecutorFactory { Executor = proxied };
        var checker = new XaiSubscriptionQuotaChecker(
            baseExecutor,
            proxyFactory,
            new AntigravityTestClock(Now));

        const string settings = """
            {"proxy":{"type":"url","url":"http://proxy.local:8080","username":"user","password":"pass","disableConnectionReuse":true}}
            """;
        var quota = await checker.CheckQuotaAsync(
            Channel(settings: settings),
            CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        var proxy = Assert.Single(proxyFactory.Requests);
        Assert.Equal(ProxyType.Url, proxy.Type);
        Assert.Equal("http://proxy.local:8080", proxy.Url);
        Assert.Equal("user", proxy.Username);
        Assert.Equal("pass", proxy.Password);
        Assert.True(proxy.DisableConnectionReuse);
        Assert.Equal(0, baseExecutor.Calls);
        Assert.Equal(2, proxied.Requests.Count);
        Assert.Equal(WeeklyBillingUrl, proxied.Requests[0].Url);
        Assert.Equal(MonthlyBillingUrl, proxied.Requests[1].Url);
        Assert.Equal(Token, proxied.Requests[0].Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaTreatsMalformedSettingsAsNoProxy()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, request, _) => Ok(
                request.Url == WeeklyBillingUrl ? WeeklyAvailableBody : MonthlyAvailableBody),
        };
        var proxyFactory = new ScriptedProxyExecutorFactory();
        var checker = new XaiSubscriptionQuotaChecker(executor, proxyFactory, new AntigravityTestClock(Now));

        var quota = await checker.CheckQuotaAsync(Channel(settings: "{broken"), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Empty(proxyFactory.Requests);
        Assert.Equal(2, executor.Calls);
    }

    // ------------------------------------------------------------------
    // Credentials
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaReadsTheLegacyOAuthJsonFromTheApiKey()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, request, _) => Ok(
                request.Url == WeeklyBillingUrl ? WeeklyAvailableBody : MonthlyAvailableBody),
        };
        var checker = CreateChecker(executor);
        var legacyJson = """
            {"access_token":"legacy-access-token","refresh_token":"legacy-refresh-token","client_id":"synthetic-client"}
            """;
        var credentials = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["apiKey"] = legacyJson });

        var quota = await checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal("legacy-access-token", executor.Requests[0].Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaPrefersTheOAuthObjectOverTheLegacyApiKey()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, request, _) => Ok(
                request.Url == WeeklyBillingUrl ? WeeklyAvailableBody : MonthlyAvailableBody),
        };
        var checker = CreateChecker(executor);
        var legacyJson = """{"access_token":"legacy-token"}""";
        var credentials = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oauth"] = new Dictionary<string, string> { ["access_token"] = Token },
            ["apiKey"] = legacyJson,
        });

        await checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None);

        Assert.Equal(Token, executor.Requests[0].Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaFallsBackToTheLegacyApiKeyWhenTheOAuthTokenIsBlank()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, request, _) => Ok(
                request.Url == WeeklyBillingUrl ? WeeklyAvailableBody : MonthlyAvailableBody),
        };
        var checker = CreateChecker(executor);
        var legacyJson = """{"access_token":"legacy-token"}""";
        var credentials = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oauth"] = new Dictionary<string, string> { ["access_token"] = "   " },
            ["apiKey"] = legacyJson,
        });

        await checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None);

        // ResolveOAuthCredentials treats a whitespace-only oauth token as
        // absent and parses the legacy apiKey instead.
        Assert.Equal("legacy-token", executor.Requests[0].Auth?.ApiKey);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"apiKey":"   "}""")]
    [InlineData("""{"oauth":{"access_token":"   "}}""")]
    public async Task CheckQuotaWrapsMissingCredentials(string credentials)
    {
        var executor = new ScriptedExecutor();
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None));

        Assert.Equal("parse xAI subscription credentials: empty credentials", error.Message);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task CheckQuotaRejectsABlankLegacyAccessToken()
    {
        var executor = new ScriptedExecutor();
        var checker = CreateChecker(executor);
        var credentials = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["apiKey"] = """{"access_token":"   "}""",
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None));

        // ParseCredentialsJSON does not trim the token itself; the checker's
        // TrimSpace turns it into the xai-specific "no access token" error.
        Assert.Equal("xAI subscription channel has no OAuth access token", error.Message);
        Assert.Equal(0, executor.Calls);
    }

    [Theory]
    [InlineData("not-json", "parse xAI subscription credentials: ")]
    [InlineData("""{"refresh_token":"only-refresh"}""", "parse xAI subscription credentials: access_token is empty")]
    public async Task CheckQuotaWrapsMalformedLegacyCredentials(string apiKey, string expectedMessage)
    {
        var executor = new ScriptedExecutor();
        var checker = CreateChecker(executor);
        var credentials = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["apiKey"] = apiKey });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None));

        Assert.StartsWith(expectedMessage, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, executor.Calls);
    }

    // ------------------------------------------------------------------
    // Errors
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaWrapsWeeklyTransportErrors()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new HttpClientError(
                "GET",
                WeeklyBillingUrl,
                500,
                "500 Internal Server Error"),
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "fetch xAI weekly billing: GET - " + WeeklyBillingUrl
            + " with status 500 Internal Server Error",
            error.Message);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task CheckQuotaWrapsMonthlyTransportErrors()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call == 0
                ? Ok(WeeklyAvailableBody)
                : throw new HttpClientError(
                    "GET",
                    MonthlyBillingUrl,
                    500,
                    "500 Internal Server Error"),
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "fetch xAI monthly billing: GET - " + MonthlyBillingUrl
            + " with status 500 Internal Server Error",
            error.Message);
        Assert.Equal(2, executor.Calls);
    }

    [Theory]
    [InlineData(302)]
    [InlineData(199)]
    public async Task CheckQuotaRejectsStatusesOutsideTwoHundredToThreeHundred(int statusCode)
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData { StatusCode = statusCode },
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("fetch xAI weekly billing: HTTP " + statusCode, error.Message);
    }

    [Fact]
    public async Task CheckQuotaWrapsBillingResponsesWithoutConfig()
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok("{}") };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "parse xAI billing responses: xAI billing responses contain no config",
            error.Message);
    }

    [Fact]
    public async Task CheckQuotaWrapsMalformedBillingBodies()
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok("not-json") };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.StartsWith("parse xAI billing responses: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckQuotaPrefersTheJwtTierOverTheMonthlyPlan()
    {
        const string heavyAccessToken = "header.eyJ0aWVyIjo1fQ.signature";
        var executor = new ScriptedExecutor
        {
            OnDo = (_, request, _) => Ok(
                request.Url == WeeklyBillingUrl ? WeeklyAvailableBody : MonthlyAvailableBody),
        };
        var checker = CreateChecker(executor);

        var quota = await checker.CheckQuotaAsync(
            Channel(CredentialsJson(heavyAccessToken)),
            CancellationToken.None);

        // The monthly limit says SuperGrok; the unsigned JWT tier wins.
        Assert.Equal("SuperGrok Heavy", quota.RawData["plan_type"]);
    }

    [Fact]
    public void SupportsChannelAcceptsOnlyTheSubscriptionType()
    {
        var checker = CreateChecker(new ScriptedExecutor());

        Assert.True(checker.SupportsChannel(new ChannelRecord { Type = "xai_subscription" }));
        Assert.False(checker.SupportsChannel(new ChannelRecord { Type = "xai" }));
        Assert.False(checker.SupportsChannel(new ChannelRecord { Type = "openai" }));
    }

    // ------------------------------------------------------------------
    // Window and status construction
    // ------------------------------------------------------------------

    [Fact]
    public void BuildBillingQuotaDataDoesNotInventMissingWindows()
    {
        var quota = XaiSubscriptionQuotaChecker.BuildBillingQuotaData(
            new BillingSummary(),
            Now);

        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.Equal("xai_subscription", quota.ProviderType);
        Assert.False(quota.Ready);
        Assert.Empty(quota.Limits);
        Assert.Null(quota.NextResetAt);
        Assert.Equal(string.Empty, quota.RawData["plan_type"]);

        // rawBilling is a non-nil empty map in the reference, so "billing" is
        // always present in rawData.
        var billing = Assert.IsType<Dictionary<string, object?>>(quota.RawData["billing"]);
        Assert.Empty(billing);
    }

    [Fact]
    public void BuildBillingQuotaDataLabelsTheMonthlyOnlyWindow()
    {
        var quota = XaiSubscriptionQuotaChecker.BuildBillingQuotaData(
            new BillingSummary { Monthly = new BillingWindow { UsagePercent = 50 } },
            Now);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaWindows.Monthly, limit.Window);
        Assert.Equal(0.5, limit.UsageRatio, 9);
        Assert.Null(limit.NextResetAt);
        Assert.Null(limit.PeriodStart);
        Assert.Null(quota.NextResetAt);
    }

    [Fact]
    public void BuildBillingQuotaDataKeepsTheWeeklyOnlyWindow()
    {
        var weeklyReset = new DateTimeOffset(2099, 7, 16, 3, 25, 0, TimeSpan.Zero);
        var quota = XaiSubscriptionQuotaChecker.BuildBillingQuotaData(
            new BillingSummary
            {
                Weekly = new BillingWindow
                {
                    UsagePercent = 82.5,
                    ResetAt = "2099-07-16T03:25:00Z",
                },
            },
            Now);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaWindows.Weekly, limit.Window);
        Assert.Equal(0.825, limit.UsageRatio, 9);
        Assert.Equal(weeklyReset, limit.NextResetAt);
        Assert.Equal(weeklyReset.AddDays(-7), limit.PeriodStart);
        Assert.Equal(weeklyReset, quota.NextResetAt);
    }

    [Fact]
    public void BuildBillingQuotaDataUsesTheHigherPercentForTheOverallStatus()
    {
        var quota = XaiSubscriptionQuotaChecker.BuildBillingQuotaData(
            new BillingSummary
            {
                Weekly = new BillingWindow { UsagePercent = 10 },
                Monthly = new BillingWindow { UsagePercent = 85 },
            },
            Now);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[0].Status);
        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Limits[1].Status);
    }

    [Fact]
    public void BuildBillingQuotaDataDropsANegativeWindow()
    {
        var quota = XaiSubscriptionQuotaChecker.BuildBillingQuotaData(
            new BillingSummary { Weekly = new BillingWindow { UsagePercent = -5 } },
            Now);

        // usagePercent stays at -1, so the overall status stays unknown; the
        // limit's negative ratio is dropped by NormalizeQuotaData.
        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.False(quota.Ready);
        Assert.Empty(quota.Limits);
        var billing = Assert.IsType<Dictionary<string, object?>>(quota.RawData["billing"]);
        Assert.IsType<BillingWindow>(billing["weekly"]);
    }

    [Fact]
    public void BuildBillingQuotaDataClampsRatiosAboveOne()
    {
        var quota = XaiSubscriptionQuotaChecker.BuildBillingQuotaData(
            new BillingSummary { Weekly = new BillingWindow { UsagePercent = 250 } },
            Now);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);
        Assert.Equal(1d, limit.UsageRatio);
    }

    [Theory]
    [InlineData(100, "exhausted")]
    [InlineData(150, "exhausted")]
    [InlineData(80, "warning")]
    [InlineData(99.99, "warning")]
    [InlineData(79.99, "available")]
    [InlineData(0, "available")]
    [InlineData(-1, "available")]
    public void BillingWindowStatusMatchesTheReferenceBoundaries(
        double usagePercent,
        string expected)
    {
        Assert.Equal(expected, XaiSubscriptionQuotaChecker.BillingWindowStatus(usagePercent));
    }

    // ------------------------------------------------------------------
    // Reset handling
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("2099-07-16T03:25:00Z", true)]
    [InlineData(" 2099-07-16T03:25:00Z ", true)]
    [InlineData("2099-07-16T03:25:00+08:00", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("not-a-time", false)]
    [InlineData("2099-13-16T03:25:00Z", false)]
    public void ParseXaiResetRequiresRfc3339(string raw, bool parsed)
    {
        var reset = XaiSubscriptionQuotaChecker.ParseXaiReset(raw);

        Assert.Equal(parsed, reset is not null);
    }

    [Fact]
    public void ParseXaiResetKeepsTheOffset()
    {
        var reset = XaiSubscriptionQuotaChecker.ParseXaiReset("2099-07-16T03:25:00+08:00");

        Assert.NotNull(reset);
        Assert.Equal(TimeSpan.FromHours(8), reset.Value.Offset);
        Assert.Equal(
            new DateTimeOffset(2099, 7, 15, 19, 25, 0, TimeSpan.Zero),
            reset.Value.ToUniversalTime());
    }

    [Fact]
    public void BuildBillingQuotaDataClearsAnExpiredReset()
    {
        var quota = XaiSubscriptionQuotaChecker.BuildBillingQuotaData(
            new BillingSummary
            {
                Weekly = new BillingWindow
                {
                    UsagePercent = 10,
                    ResetAt = "2000-01-01T00:00:00Z",
                },
            },
            Now);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        var limit = Assert.Single(quota.Limits);
        Assert.Null(limit.NextResetAt);
        Assert.Null(limit.PeriodStart);
        Assert.Null(quota.NextResetAt);
    }

    [Fact]
    public void BuildBillingQuotaDataPicksTheEarliestReset()
    {
        var quota = XaiSubscriptionQuotaChecker.BuildBillingQuotaData(
            new BillingSummary
            {
                Weekly = new BillingWindow
                {
                    UsagePercent = 10,
                    ResetAt = "2099-08-01T00:00:00Z",
                },
                Monthly = new BillingWindow
                {
                    UsagePercent = 10,
                    ResetAt = "2099-07-01T00:00:00Z",
                },
            },
            Now);

        // earliestReset ignores which window is more urgent.
        Assert.Equal(
            new DateTimeOffset(2099, 7, 1, 0, 0, 0, TimeSpan.Zero),
            quota.NextResetAt);
    }

    // ------------------------------------------------------------------
    // rawData shape
    // ------------------------------------------------------------------

    [Fact]
    public void BuildBillingQuotaDataSerializesBillingWindowsWithTheGoFieldNames()
    {
        var quota = XaiSubscriptionQuotaChecker.BuildBillingQuotaData(
            new BillingSummary
            {
                Plan = "SuperGrok",
                Weekly = new BillingWindow
                {
                    UsagePercent = 82.5,
                    ResetAt = "2099-07-16T03:25:00Z",
                },
                Monthly = new BillingWindow
                {
                    UsagePercent = 50,
                    ResetAt = "2099-08-01T00:00:00Z",
                    LimitUsd = 150,
                    UsedUsd = 75,
                },
            },
            Now);

        using var document = JsonDocument.Parse(GoJson.MarshalToString(quota.RawData));
        var root = document.RootElement;
        Assert.Equal("SuperGrok", root.GetProperty("plan_type").GetString());

        var billing = root.GetProperty("billing");
        var weekly = billing.GetProperty("weekly");
        Assert.Equal(82.5, weekly.GetProperty("usage_percent").GetDouble());
        Assert.Equal("2099-07-16T03:25:00Z", weekly.GetProperty("reset_at").GetString());
        // limit_usd / used_usd are zero for the weekly payload and omitted
        // (Go's omitempty).
        Assert.False(weekly.TryGetProperty("limit_usd", out _));
        Assert.False(weekly.TryGetProperty("used_usd", out _));

        var monthly = billing.GetProperty("monthly");
        Assert.Equal(50d, monthly.GetProperty("usage_percent").GetDouble());
        Assert.Equal(150d, monthly.GetProperty("limit_usd").GetDouble());
        Assert.Equal(75d, monthly.GetProperty("used_usd").GetDouble());
        Assert.Equal("2099-08-01T00:00:00Z", monthly.GetProperty("reset_at").GetString());
    }

    [Fact]
    public void BuildBillingQuotaDataUsesTheMonthlyLimitForThePlan()
    {
        var quota = XaiSubscriptionQuotaChecker.BuildBillingQuotaData(
            new BillingSummary
            {
                Plan = "SuperGrok Heavy",
                Monthly = new BillingWindow { UsagePercent = 1 },
            },
            Now);

        Assert.Equal("SuperGrok Heavy", quota.RawData["plan_type"]);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private const string Token = "access-token";

    private static XaiSubscriptionQuotaChecker CreateChecker(
        out ClaudeCodeQuotaCheckerTests.RecordingHttpHandler handler)
    {
        handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new XaiSubscriptionQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now));
    }

    private static XaiSubscriptionQuotaChecker CreateChecker(ScriptedExecutor executor)
        => new(executor, new ScriptedProxyExecutorFactory(), new AntigravityTestClock(Now));

    private static ChannelRecord Channel(string? credentials = null, string settings = "{}")
        => new()
        {
            Type = "xai_subscription",
            Credentials = credentials ?? CredentialsJson(Token),
            Settings = settings,
        };

    private static string CredentialsJson(string accessToken)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oauth"] = new Dictionary<string, string> { ["access_token"] = accessToken },
        });

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
