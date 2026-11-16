using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports internal/server/biz/provider_quota/codex_checker.go together with the
/// codex cases of codex_checker_test.go, codex_quota_regression_test.go,
/// period_window_test.go and the codex fixture of
/// provider_contract_fixtures_test.go: the usage request, the reset-credit list
/// and consume flows, the credential precedence (OAuth object over the legacy
/// apiKey JSON) and the window/status normalization. The wire shape (URL, auth,
/// headers) is locked through the real <see cref="HttpExecutor"/> over a
/// recording message handler; the bodyless GETs keep their Content-Type header
/// on the wire like Go (C206 resolved).
/// </summary>
public sealed class CodexQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private const string UsageBody = """{"plan_type":"plus","rate_limit":{"allowed":true}}""";

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaSendsTheMinimalUsageRequestAndParsesTheWindows()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(UsageBody);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("https://chatgpt.com/backend-api/wham/usage", request.Url);
        Assert.Equal("Bearer " + Token(), request.Header("Authorization"));
        Assert.Equal("axonhub/1.0", request.Header("User-Agent"));
        Assert.Equal(string.Empty, request.Header("Originator"));
        Assert.Equal(string.Empty, request.Header("Chatgpt-Account-Id"));
        // The executor always attaches content, so the bodyless GET keeps the
        // Content-Type on the wire like Go's net/http (C206 resolved).
        Assert.Equal("application/json", request.Header("Content-Type"));
        Assert.Equal(string.Empty, request.Body);

        Assert.Equal("codex", quota.ProviderType);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Empty(quota.Limits);
        Assert.Null(quota.NextResetAt);
        Assert.Equal("plus", quota.RawData["plan_type"]);
        var rateLimit = Assert.IsType<Dictionary<string, object?>>(quota.RawData["rate_limit"]);
        Assert.True(Assert.IsType<bool>(rateLimit["allowed"]));
        Assert.False(rateLimit.ContainsKey("limit_reached"));

        Assert.True(checker.SupportsChannel(Channel()));
        Assert.False(checker.SupportsChannel(new ChannelRecord { Type = "openai" }));
    }

    [Fact]
    public async Task CheckQuotaKeepsTheContentTypeOnTheRequestData()
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok(UsageBody) };
        var checker = CreateChecker(executor);

        await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var request = Assert.Single(executor.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(CodexQuotaChecker.UsageUrl, request.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Null(request.Body);
        Assert.Equal(AuthTypes.Bearer, request.Auth?.Type);
        Assert.Equal(Token(), request.Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaUsesThePerChannelProxyInsteadOfTheSharedExecutor()
    {
        var baseExecutor = new ScriptedExecutor();
        var proxied = new ScriptedExecutor { OnDo = (_, _, _) => Ok(UsageBody) };
        var proxyFactory = new ScriptedProxyExecutorFactory { Executor = proxied };
        var checker = new CodexQuotaChecker(
            baseExecutor,
            proxyFactory,
            new AntigravityTestClock(Now));

        const string settings = """
            {"proxy":{"type":"url","url":"http://proxy.local:8080","username":"user","password":"pass","disableConnectionReuse":true}}
            """;
        var quota = await checker.CheckQuotaAsync(Channel(settings: settings), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        var proxy = Assert.Single(proxyFactory.Requests);
        Assert.Equal(ProxyType.Url, proxy.Type);
        Assert.Equal("http://proxy.local:8080", proxy.Url);
        Assert.Equal("user", proxy.Username);
        Assert.Equal("pass", proxy.Password);
        Assert.True(proxy.DisableConnectionReuse);
        Assert.Equal(0, baseExecutor.Calls);
        var request = Assert.Single(proxied.Requests);
        Assert.Equal(CodexQuotaChecker.UsageUrl, request.Url);
        Assert.Equal(Token(), request.Auth?.ApiKey);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
    }

    [Fact]
    public async Task CheckQuotaTreatsMalformedSettingsAsNoProxy()
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok(UsageBody) };
        var proxyFactory = new ScriptedProxyExecutorFactory();
        var checker = new CodexQuotaChecker(executor, proxyFactory, new AntigravityTestClock(Now));

        var quota = await checker.CheckQuotaAsync(Channel(settings: "{broken"), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Empty(proxyFactory.Requests);
        Assert.Equal(1, executor.Calls);
    }

    // ------------------------------------------------------------------
    // Credentials
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaReadsTheLegacyApiKeyJson()
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok(UsageBody) };
        var checker = CreateChecker(executor);
        var legacyToken = Token("acct_legacy");
        var legacyJson = "{\"access_token\":" + JsonSerializer.Serialize(legacyToken) + "}";
        var credentials = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["apiKey"] = legacyJson });

        await checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None);

        Assert.Equal(legacyToken, Assert.Single(executor.Requests).Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaPrefersTheOAuthObjectOverTheLegacyApiKey()
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok(UsageBody) };
        var checker = CreateChecker(executor);
        var oauthToken = Token("acct_oauth");
        var legacyJson = "{\"access_token\":" + JsonSerializer.Serialize(Token("acct_legacy")) + "}";
        var credentials = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oauth"] = new Dictionary<string, string> { ["access_token"] = oauthToken },
            ["apiKey"] = legacyJson,
        });

        await checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None);

        Assert.Equal(oauthToken, Assert.Single(executor.Requests).Auth?.ApiKey);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"apiKey":"   "}""")]
    public async Task CheckQuotaFailsWithoutCredentials(string credentials)
    {
        var executor = new ScriptedExecutor();
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None));

        Assert.Equal("channel has no credentials", error.Message);
        Assert.Equal(0, executor.Calls);
    }

    [Fact]
    public async Task CheckQuotaFailsWhenTheOAuthObjectHasNoAccessToken()
    {
        var executor = new ScriptedExecutor();
        var checker = CreateChecker(executor);
        var legacyJson = "{\"access_token\":" + JsonSerializer.Serialize(Token()) + "}";
        var credentials = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oauth"] = new Dictionary<string, string>(),
            ["apiKey"] = legacyJson,
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None));

        // The codex checker has its own wording here (codex_checker.go:215);
        // an OAuth object wins even when the legacy apiKey is usable.
        Assert.Equal("OAuth missing access_token", error.Message);
        Assert.Equal(0, executor.Calls);
    }

    [Theory]
    [InlineData("not-json", "failed to parse OAuth credentials: ")]
    [InlineData("""{"refresh_token":"only-refresh"}""", "failed to parse OAuth credentials: access_token is empty")]
    public async Task CheckQuotaWrapsMalformedLegacyCredentials(string apiKey, string expectedPrefix)
    {
        var executor = new ScriptedExecutor();
        var checker = CreateChecker(executor);
        var credentials = JsonSerializer.Serialize(new Dictionary<string, string> { ["apiKey"] = apiKey });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None));

        Assert.StartsWith(expectedPrefix, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, executor.Calls);
    }

    [Theory]
    [InlineData("plain-token")]
    [InlineData("a.b")]
    [InlineData("""{}""")]
    [InlineData("""{"https://api.openai.com/auth":{"chatgpt_account_id":123}}""")]
    [InlineData("""{"https://api.openai.com/auth":{"chatgpt_account_id":""}}""")]
    public async Task CheckQuotaRequiresTheAccountIdClaim(string claimsJsonOrToken)
    {
        var executor = new ScriptedExecutor();
        var checker = CreateChecker(executor);
        var token = claimsJsonOrToken.StartsWith('{')
            ? TokenWithClaims(claimsJsonOrToken)
            : claimsJsonOrToken;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(CredentialsJson(token)), CancellationToken.None));

        Assert.Equal(
            "failed to extract ChatGPT account id from access token",
            error.Message);
        Assert.Equal(0, executor.Calls);
    }

    // ------------------------------------------------------------------
    // Errors
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaWrapsTransportErrors()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new HttpClientError(
                "GET",
                CodexQuotaChecker.UsageUrl,
                500,
                "500 Internal Server Error"),
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "quota request failed: GET - https://chatgpt.com/backend-api/wham/usage"
            + " with status 500 Internal Server Error",
            error.Message);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("""{"plan_type":123}""")]
    [InlineData("""{"rate_limit":"x"}""")]
    [InlineData("""{"rate_limit":{"allowed":1}}""")]
    [InlineData("""{"rate_limit":{"primary_window":{"used_percent":"x"}}}""")]
    [InlineData("""{"rate_limit":{"primary_window":{"reset_at":1.5}}}""")]
    public void CheckQuotaWrapsMalformedBodies(string body)
    {
        var error = Assert.Throws<WrappedException>(
            () => CodexQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(body), Now));

        Assert.StartsWith("failed to parse codex usage response: ", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public void CheckQuotaTreatsJsonNullAsTheZeroStruct(string body)
    {
        // Go's json.Unmarshal of null into a struct is a no-op: the zero
        // CodexUsageResponse has an empty plan type and no rate limits.
        var quota = CodexQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(body), Now);

        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.False(quota.Ready);
        Assert.Empty(quota.Limits);
        Assert.Null(quota.NextResetAt);
        Assert.Equal(string.Empty, quota.RawData["plan_type"]);
        Assert.False(quota.RawData.ContainsKey("rate_limit"));
    }

    [Fact]
    public void CheckQuotaKeepsCodeReviewRateLimitOnlyInRawData()
    {
        const string body = """
            {"plan_type":"pro","code_review_rate_limit":{"allowed":true,"primary_window":{"used_percent":50,"limit_window_seconds":18000}}}
            """;

        var quota = CodexQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(body), Now);

        // code_review_rate_limit never contributes limits or the overall
        // status (codex_checker.go:269-293).
        Assert.Empty(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.False(quota.Ready);
        var review = Assert.IsType<Dictionary<string, object?>>(
            quota.RawData["code_review_rate_limit"]);
        Assert.True(Assert.IsType<bool>(review["allowed"]));
        var window = Assert.IsType<Dictionary<string, object?>>(review["primary_window"]);
        Assert.Equal(50.0, Assert.IsType<double>(window["used_percent"]));
        Assert.Equal(18000L, Assert.IsType<long>(window["limit_window_seconds"]));
        Assert.False(quota.RawData.ContainsKey("rate_limit"));
    }

    [Fact]
    public void CheckQuotaCollectsTheRawUsageShape()
    {
        const string body = """
            {
              "plan_type": "plus",
              "rate_limit": {
                "allowed": false,
                "limit_reached": true,
                "primary_window": {
                  "used_percent": 12.5,
                  "reset_at": 4102444800,
                  "reset_after_seconds": 60,
                  "limit_window_seconds": 18000
                },
                "secondary_window": {}
              },
              "code_review_rate_limit": {}
            }
            """;

        var quota = CodexQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(body), Now);

        Assert.Equal("plus", quota.RawData["plan_type"]);
        var rateLimit = Assert.IsType<Dictionary<string, object?>>(quota.RawData["rate_limit"]);
        Assert.False(Assert.IsType<bool>(rateLimit["allowed"]));
        Assert.True(Assert.IsType<bool>(rateLimit["limit_reached"]));
        var primary = Assert.IsType<Dictionary<string, object?>>(rateLimit["primary_window"]);
        Assert.Equal(12.5, Assert.IsType<double>(primary["used_percent"]));
        Assert.Equal(4_102_444_800L, Assert.IsType<long>(primary["reset_at"]));
        Assert.Equal(60L, Assert.IsType<long>(primary["reset_after_seconds"]));
        Assert.Equal(18_000L, Assert.IsType<long>(primary["limit_window_seconds"]));
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(rateLimit["secondary_window"]));
        Assert.Empty(Assert.IsType<Dictionary<string, object?>>(quota.RawData["code_review_rate_limit"]));

        // Both present windows yield a limit. The primary keeps its reported
        // percentage; the empty secondary window has no percentage, so the
        // account-wide exhaustion flags infer 100% (codex_checker.go:332-337).
        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(QuotaWindows.FiveHours, quota.Limits[0].Window);
        Assert.Equal(0.125, quota.Limits[0].UsageRatio, 12);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[0].Status);
        Assert.Equal(QuotaWindows.Secondary, quota.Limits[1].Window);
        Assert.Equal(1.0, quota.Limits[1].UsageRatio, 12);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Limits[1].Status);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
    }

    // ------------------------------------------------------------------
    // Window/status regression (codex_quota_regression_test.go)
    // ------------------------------------------------------------------

    [Fact]
    public void ReportedExhaustionKeepsWeeklyUsage()
    {
        const string body = """
            {
              "plan_type": "plus",
              "rate_limit": {
                "allowed": false,
                "limit_reached": true,
                "primary_window": {
                  "limit_window_seconds": 18000,
                  "reset_after_seconds": 65,
                  "reset_at": 1791129646,
                  "used_percent": 100
                },
                "secondary_window": {
                  "limit_window_seconds": 604800,
                  "reset_after_seconds": 529934,
                  "reset_at": 1791659515,
                  "used_percent": 36
                }
              }
            }
            """;

        var quota = CodexQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(body), Now);

        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(QuotaWindows.FiveHours, quota.Limits[0].Window);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Limits[0].Status);
        Assert.False(quota.Limits[0].Ready);
        Assert.Equal(1.0, quota.Limits[0].UsageRatio, 12);
        Assert.Equal(QuotaWindows.SevenDays, quota.Limits[1].Window);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[1].Status);
        Assert.True(quota.Limits[1].Ready);
        Assert.Equal(0.36, quota.Limits[1].UsageRatio, 12);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
    }

    [Theory]
    [InlineData(false, true, 10.0, "available", 100.0, "exhausted")]
    [InlineData(false, true, 90.0, "warning", 100.0, "exhausted")]
    [InlineData(false, false, 0.0, "available", 36.0, "available")]
    [InlineData(true, true, 25.0, "available", 36.0, "available")]
    public void DeniedRequestsPreserveEachWindow(
        bool allowed,
        bool limitReached,
        double primaryPercent,
        string primaryStatus,
        double secondaryPercent,
        string secondaryStatus)
    {
        var body = "{\"rate_limit\":{\"allowed\":" + (allowed ? "true" : "false")
            + ",\"limit_reached\":" + (limitReached ? "true" : "false")
            + ",\"primary_window\":{\"used_percent\":"
            + primaryPercent.ToString(CultureInfo.InvariantCulture)
            + ",\"limit_window_seconds\":18000},\"secondary_window\":{\"used_percent\":"
            + secondaryPercent.ToString(CultureInfo.InvariantCulture)
            + ",\"limit_window_seconds\":604800}}}";

        var quota = CodexQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(body), Now);

        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(primaryPercent / 100, quota.Limits[0].UsageRatio, 12);
        Assert.Equal(primaryStatus, quota.Limits[0].Status);
        Assert.Equal(ProviderQuotaStatuses.IsReadyStatus(primaryStatus), quota.Limits[0].Ready);
        Assert.Equal(secondaryPercent / 100, quota.Limits[1].UsageRatio, 12);
        Assert.Equal(secondaryStatus, quota.Limits[1].Status);
        Assert.Equal(ProviderQuotaStatuses.IsReadyStatus(secondaryStatus), quota.Limits[1].Ready);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);

        var (status, ready) = ProviderQuotaData.EffectiveStatus(
            quota.Limits,
            quota.Status,
            quota.Ready,
            QuotaLimitTypes.Token);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, status);
        Assert.False(ready);

        var (state, reason) = ProviderQuotaRouting.EvaluateQuotaRouting(
            quota.Limits,
            quota.Status,
            QuotaLimitTypes.Token,
            Now);
        Assert.Equal(QuotaRoutingStates.Exhausted, state);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void ExhaustedWindowWithoutUsageReportsZeroRemaining()
    {
        var resetAt = new DateTimeOffset(2099, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var body = """
            {"rate_limit":{"allowed":false,"primary_window":{"reset_at":RESET,"limit_window_seconds":604800}}}
            """.Replace(
            "RESET",
            resetAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);

        var quota = CodexQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(body), Now);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaWindows.SevenDays, limit.Window);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);
        Assert.False(limit.Ready);
        Assert.Equal(1.0, limit.UsageRatio, 12);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal(resetAt, quota.NextResetAt);
        Assert.Equal(resetAt.AddDays(-7), limit.PeriodStart);
    }

    // ------------------------------------------------------------------
    // Period/window rules (period_window_test.go)
    // ------------------------------------------------------------------

    [Fact]
    public void LimitPeriodUsesReportedWindowLength()
    {
        var resetAt = Now.AddHours(2);
        var body = UnixResetBody(40.0, resetAt, 18000);

        var quota = CodexQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(body), Now);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(0.4, limit.UsageRatio, 12);
        Assert.Equal(QuotaWindows.FiveHours, limit.Window);
        Assert.Equal(resetAt.AddHours(-5), limit.PeriodStart);
    }

    [Fact]
    public void ProPrimarySevenDayWindow()
    {
        var resetAt = new DateTimeOffset(2099, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var body = UnixResetBody(40.0, resetAt, 604800);

        var quota = CodexQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(body), Now);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaWindows.SevenDays, limit.Window);
        Assert.Equal(0.4, limit.UsageRatio, 12);
        Assert.Equal(resetAt.AddDays(-7), limit.PeriodStart);
    }

    [Fact]
    public void DualWindowsRemainIndependent()
    {
        var fiveHourReset = new DateTimeOffset(2099, 9, 8, 12, 0, 0, TimeSpan.Zero);
        var sevenDayReset = new DateTimeOffset(2099, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var body = """
            {
              "plan_type": "pro",
              "rate_limit": {
                "allowed": true,
                "primary_window": {
                  "used_percent": 25,
                  "reset_at": FIVE_RESET,
                  "limit_window_seconds": 18000
                },
                "secondary_window": {
                  "used_percent": 90,
                  "reset_at": SEVEN_RESET,
                  "limit_window_seconds": 604800
                }
              }
            }
            """
            .Replace("FIVE_RESET", Unix(fiveHourReset), StringComparison.Ordinal)
            .Replace("SEVEN_RESET", Unix(sevenDayReset), StringComparison.Ordinal);

        var quota = CodexQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(body), Now);

        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(QuotaWindows.FiveHours, quota.Limits[0].Window);
        Assert.Equal(0.25, quota.Limits[0].UsageRatio, 12);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[0].Status);
        Assert.Equal(fiveHourReset.AddHours(-5), quota.Limits[0].PeriodStart);

        Assert.Equal(QuotaWindows.SevenDays, quota.Limits[1].Window);
        Assert.Equal(0.9, quota.Limits[1].UsageRatio, 12);
        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Limits[1].Status);
        Assert.Equal(sevenDayReset.AddDays(-7), quota.Limits[1].PeriodStart);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.Equal(fiveHourReset, quota.NextResetAt);
    }

    [Fact]
    public void UnknownWindowDurationKeepsNeutralLabel()
    {
        var resetAt = new DateTimeOffset(2099, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var body = """
            {
              "rate_limit": {
                "allowed": true,
                "primary_window": {
                  "used_percent": 10,
                  "reset_at": PRIMARY_RESET,
                  "limit_window_seconds": 12345
                },
                "secondary_window": {
                  "used_percent": 20,
                  "reset_at": SECONDARY_RESET
                }
              }
            }
            """
            .Replace("PRIMARY_RESET", Unix(resetAt), StringComparison.Ordinal)
            .Replace("SECONDARY_RESET", Unix(resetAt.AddHours(1)), StringComparison.Ordinal);

        var quota = CodexQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(body), Now);

        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(QuotaWindows.Primary, quota.Limits[0].Window);
        Assert.Equal(QuotaWindows.Secondary, quota.Limits[1].Window);
        Assert.NotEqual(QuotaWindows.FiveHours, quota.Limits[0].Window);
        Assert.NotEqual(QuotaWindows.SevenDays, quota.Limits[1].Window);
        Assert.Equal(resetAt.AddSeconds(-12345), quota.Limits[0].PeriodStart);
        Assert.Null(quota.Limits[1].PeriodStart);
    }

    [Fact]
    public void StatusAndResetAggregateAcrossWindows()
    {
        var expiredReset = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var futureReset = new DateTimeOffset(2099, 9, 13, 12, 0, 0, TimeSpan.Zero);
        var body = """
            {
              "rate_limit": {
                "allowed": true,
                "primary_window": {
                  "used_percent": 10,
                  "reset_at": EXPIRED_RESET,
                  "limit_window_seconds": 18000
                },
                "secondary_window": {
                  "used_percent": 100,
                  "reset_at": FUTURE_RESET,
                  "limit_window_seconds": 604800
                }
              }
            }
            """
            .Replace("EXPIRED_RESET", Unix(expiredReset), StringComparison.Ordinal)
            .Replace("FUTURE_RESET", Unix(futureReset), StringComparison.Ordinal);

        var quota = CodexQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(body), Now);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal(futureReset, quota.NextResetAt);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Limits[1].Status);
        Assert.Equal(1.0, quota.Limits[1].UsageRatio, 12);
        // The expired reset is dropped during normalization.
        Assert.Null(quota.Limits[0].NextResetAt);
        Assert.Null(quota.Limits[0].PeriodStart);
    }

    [Theory]
    [InlineData("""{"used_percent":-1,"limit_window_seconds":0}""", """{"used_percent":10,"limit_window_seconds":-1}""", 0)]
    [InlineData("""{"used_percent":-1,"limit_window_seconds":18000}""", null, 0)]
    [InlineData("""{"used_percent":10,"limit_window_seconds":0}""", null, 0)]
    [InlineData("""{"used_percent":10,"limit_window_seconds":18000}""", """{"used_percent":-0.5,"limit_window_seconds":604800}""", 1)]
    public void InvalidWindowsAreOmitted(string primary, string? secondary, int expectedLimits)
    {
        var body = "{\"rate_limit\":{\"allowed\":true,\"primary_window\":" + primary
            + (secondary is null ? string.Empty : ",\"secondary_window\":" + secondary)
            + "}}";

        var quota = CodexQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(body), Now);

        Assert.Equal(expectedLimits, quota.Limits.Count);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Null(quota.NextResetAt);
    }

    [Theory]
    [InlineData(9223372036, 1)]
    [InlineData(9223372037, 0)]
    [InlineData(1, 1)]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    public void WindowDurationBoundaries(long limitWindowSeconds, int expectedLimits)
    {
        // codexWindowDuration caps the seconds at int64(time.Second)'s
        // complement (9223372036); a larger value is an invalid duration.
        var body = "{\"rate_limit\":{\"allowed\":true,\"primary_window\":{"
            + "\"used_percent\":10,\"reset_at\":4102444800,\"limit_window_seconds\":"
            + limitWindowSeconds.ToString(CultureInfo.InvariantCulture) + "}}}";

        var quota = CodexQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(body), Now);

        Assert.Equal(expectedLimits, quota.Limits.Count);
    }

    [Fact]
    public void ResetAfterSecondsSuppliesResetWhenResetAtInvalid()
    {
        const string body = """
            {
              "rate_limit": {
                "allowed": true,
                "primary_window": {
                  "used_percent": 10,
                  "reset_at": -1,
                  "reset_after_seconds": 3600,
                  "limit_window_seconds": 18000
                }
              }
            }
            """;

        var quota = CodexQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(body), Now);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(Now.AddHours(1), limit.NextResetAt);
        Assert.Equal(Now.AddHours(1), quota.NextResetAt);
        Assert.Equal(Now.AddHours(1).AddHours(-5), limit.PeriodStart);
    }

    [Fact]
    public void NoPeriodWithoutWindowLength()
    {
        var resetAt = Now.AddHours(2);
        var body = "{\"plan_type\":\"pro\",\"rate_limit\":{\"allowed\":true,"
            + "\"primary_window\":{\"used_percent\":40,\"reset_at\":"
            + Unix(resetAt) + "}}}";

        var quota = CodexQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(body), Now);

        var limit = Assert.Single(quota.Limits);
        Assert.Null(limit.PeriodStart);
        Assert.Equal(QuotaWindows.Primary, limit.Window);
        Assert.Equal(resetAt, quota.NextResetAt);
    }

    // ------------------------------------------------------------------
    // Reset-credit list and consume flows (codex_checker_test.go)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListResetsReturnsTheAvailableCredits()
    {
        var token = Token("acct_reset");
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse("""
            {
              "credits": [
                {"id": "cred_1", "status": "available", "reset_type": "codex_rate_limits", "granted_at": "2026-09-01T00:00:00Z", "expires_at": "2026-09-08T00:00:00Z"},
                {"id": "cred_2", "status": "redeemed"}
              ],
              "available_count": 1
            }
            """);

        var resets = await checker.ListResetsAsync(
            Channel(CredentialsJson(token)),
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal("https://chatgpt.com/backend-api/wham/rate-limit-reset-credits", request.Url);
        Assert.Equal("Bearer " + token, request.Header("Authorization"));
        Assert.Equal("acct_reset", request.Header("ChatGPT-Account-Id"));
        // The executor always attaches content, so the bodyless GET keeps the
        // Content-Type on the wire like Go's net/http (C206 resolved).
        Assert.Equal("application/json", request.Header("Content-Type"));

        Assert.True(resets.Supported);
        var reset = Assert.Single(resets.Resets!);
        Assert.Equal("cred_1", reset.Id);
        Assert.Equal("available", reset.Status);
        Assert.Equal("codex_rate_limits", reset.Type);
        Assert.Equal(new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), reset.GrantedAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero), reset.ExpiresAt);
        Assert.Null(reset.Title);
    }

    [Fact]
    public async Task ListResetsReturnsAnEmptyListWhenNoCreditIsAvailable()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok("""
                {"credits": [{"id": "cred_1", "status": "redeemed"}], "available_count": 0}
                """),
        };
        var checker = CreateChecker(executor);

        var resets = await checker.ListResetsAsync(Channel(), CancellationToken.None);

        Assert.True(resets.Supported);
        Assert.NotNull(resets.Resets);
        Assert.Empty(resets.Resets);
        Assert.Null(resets.Error);
    }

    [Fact]
    public async Task ListResetsSkipsNullAndZeroCredits()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok("""
                {"credits": [null, {}, {"id": "cred_9", "status": "available"}]}
                """),
        };
        var checker = CreateChecker(executor);

        var resets = await checker.ListResetsAsync(Channel(), CancellationToken.None);

        var reset = Assert.Single(resets.Resets!);
        Assert.Equal("cred_9", reset.Id);
        Assert.Equal("available", reset.Status);
        Assert.Null(reset.Type);
        Assert.Null(reset.GrantedAt);
        Assert.Null(reset.ExpiresAt);
        Assert.Null(reset.Title);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task ListResetsTreatsNullResponsesAsEmpty(string body)
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) };
        var checker = CreateChecker(executor);

        var resets = await checker.ListResetsAsync(Channel(), CancellationToken.None);

        Assert.True(resets.Supported);
        Assert.NotNull(resets.Resets);
        Assert.Empty(resets.Resets);
    }

    [Fact]
    public async Task ListResetsWrapsTransportErrors()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new HttpClientError(
                "GET",
                CodexQuotaChecker.ResetCreditsUrl,
                500,
                "500 Internal Server Error"),
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.ListResetsAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "list codex reset credits failed: GET - "
            + "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits"
            + " with status 500 Internal Server Error",
            error.Message);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"credits":"x"}""")]
    [InlineData("""{"available_count":"x"}""")]
    [InlineData("""{"credits":[{"granted_at":123}]}""")]
    public async Task ListResetsWrapsMalformedBodies(string body)
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.ListResetsAsync(Channel(), CancellationToken.None));

        Assert.StartsWith(
            "failed to parse codex reset credits response: ",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetConsumesTheFirstAvailableCredit()
    {
        var token = Token("acct_reset");
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call switch
            {
                0 => Ok("""
                    {"credits": [{"id": "cred_1", "status": "redeemed"}, {"id": "cred_2", "status": "available", "reset_type": "codex_rate_limits"}], "available_count": 1}
                    """),
                1 => Ok("""
                    {"code":"reset","windows_reset":1,"credit":{"id":"cred_2","status":"redeemed","redeemed_at":"2026-06-13T13:12:31Z"}}
                    """),
                _ => throw new InvalidOperationException("unexpected request"),
            },
        };
        var checker = CreateChecker(executor);

        await checker.ResetAsync(Channel(CredentialsJson(token)), CancellationToken.None);

        Assert.Equal(2, executor.Calls);
        var list = executor.Requests[0];
        Assert.Equal("GET", list.Method);
        Assert.Equal(CodexQuotaChecker.ResetCreditsUrl, list.Url);
        Assert.Equal("acct_reset", HttpClientUtils.HeaderGet(list.Headers, "ChatGPT-Account-Id"));

        var consume = executor.Requests[1];
        Assert.Equal("POST", consume.Method);
        Assert.Equal(CodexQuotaChecker.ConsumeResetCreditUrl, consume.Url);
        Assert.Equal(AuthTypes.Bearer, consume.Auth?.Type);
        Assert.Equal(token, consume.Auth?.ApiKey);
        Assert.Equal("acct_reset", HttpClientUtils.HeaderGet(consume.Headers, "ChatGPT-Account-Id"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(consume.Headers, "Content-Type"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(consume.Headers, "content-type"));

        var body = Encoding.UTF8.GetString(consume.Body ?? []);
        Assert.Matches(
            "^\\{\"credit_id\":\"cred_2\",\"redeem_request_id\":\""
            + "[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\"\\}$",
            body);
    }

    [Fact]
    public async Task ResetFailsWhenNoCreditIsAvailable()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok("""
                {"credits": [{"id": "cred_1", "status": "redeemed"}], "available_count": 0}
                """),
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.ResetAsync(Channel(), CancellationToken.None));

        Assert.Equal("no available codex reset credit", error.Message);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task ResetWrapsConsumeTransportErrors()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call switch
            {
                0 => Ok("""{"credits":[{"id":"cred_2","status":"available"}]}"""),
                1 => throw new HttpClientError(
                    "POST",
                    CodexQuotaChecker.ConsumeResetCreditUrl,
                    500,
                    "500 Internal Server Error"),
                _ => throw new InvalidOperationException("unexpected request"),
            },
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.ResetAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "consume codex reset credit failed: POST - "
            + "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume"
            + " with status 500 Internal Server Error",
            error.Message);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("""{"credit":"x"}""")]
    [InlineData("""{"windows_reset":"x"}""")]
    public async Task ResetWrapsMalformedConsumeBodies(string consumeBody)
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call switch
            {
                0 => Ok("""{"credits":[{"id":"cred_2","status":"available"}]}"""),
                1 => Ok(consumeBody),
                _ => throw new InvalidOperationException("unexpected request"),
            },
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.ResetAsync(Channel(), CancellationToken.None));

        Assert.StartsWith(
            "failed to parse codex reset consume response: ",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResetUsesThePerChannelProxyForBothRequests()
    {
        var baseExecutor = new ScriptedExecutor();
        var proxied = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call switch
            {
                0 => Ok("""{"credits":[{"id":"cred_2","status":"available"}]}"""),
                1 => Ok("""{"code":"reset","windows_reset":1}"""),
                _ => throw new InvalidOperationException("unexpected request"),
            },
        };
        var proxyFactory = new ScriptedProxyExecutorFactory { Executor = proxied };
        var checker = new CodexQuotaChecker(
            baseExecutor,
            proxyFactory,
            new AntigravityTestClock(Now));

        const string settings = """
            {"proxy":{"type":"url","url":"http://proxy.local:8080"}}
            """;
        await checker.ResetAsync(Channel(settings: settings), CancellationToken.None);

        Assert.Equal(0, baseExecutor.Calls);
        Assert.Equal(2, proxied.Calls);
        // Each private step resolves the executor on its own, exactly like the
        // reference's two ResolveExecutor calls (codex_checker.go:124,170).
        Assert.Equal(2, proxyFactory.Requests.Count);
        Assert.Equal(CodexQuotaChecker.ResetCreditsUrl, proxied.Requests[0].Url);
        Assert.Equal(CodexQuotaChecker.ConsumeResetCreditUrl, proxied.Requests[1].Url);
    }

    // ------------------------------------------------------------------
    // parseCodexResetTime
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("", null)]
    [InlineData("2026-09-01T00:00:00Z", "2026-09-01T00:00:00Z")]
    [InlineData("2026-09-01T00:00:00+08:00", "2026-08-31T16:00:00Z")]
    [InlineData("2026-09-01T00:00:00-05:30", "2026-09-01T05:30:00Z")]
    [InlineData("2026-09-01T00:00:00.123456789Z", "2026-09-01T00:00:00.1234567Z")]
    [InlineData("2026-09-01T00:00:00,5Z", "2026-09-01T00:00:00.5Z")]
    [InlineData("2026-09-01T0:00:00Z", "2026-09-01T00:00:00Z")]
    [InlineData("2026-09-01T00:00:00.0Z", "2026-09-01T00:00:00Z")]
    [InlineData("not-a-time", null)]
    [InlineData("2026-09-01T00:00:00", null)]
    [InlineData("2026-02-30T00:00:00Z", null)]
    [InlineData("2026-09-01T24:00:00Z", null)]
    [InlineData("0000-01-01T00:00:00Z", null)]
    [InlineData("2026-09-01T00:00:00+14:01", null)]
    [InlineData("2026-09-01T00:00:00+24:60", null)]
    [InlineData("2026-09-01t00:00:00z", null)]
    public void ParseResetTimeMatchesGoTimeParse(string value, string? expectedUtc)
    {
        var parsed = CodexQuotaChecker.ParseResetTime(value);

        if (expectedUtc is null)
        {
            Assert.Null(parsed);
            return;
        }

        var expected = DateTimeOffset.Parse(expectedUtc, CultureInfo.InvariantCulture);
        Assert.Equal(expected.UtcDateTime, parsed!.Value.UtcDateTime);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static CodexQuotaChecker CreateChecker(out ClaudeCodeQuotaCheckerTests.RecordingHttpHandler handler)
    {
        handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new CodexQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now));
    }

    private static CodexQuotaChecker CreateChecker(ScriptedExecutor executor)
        => new(executor, new ScriptedProxyExecutorFactory(), new AntigravityTestClock(Now));

    private static ChannelRecord Channel(string? credentials = null, string settings = "{}")
        => new()
        {
            Type = "codex",
            Credentials = credentials ?? CredentialsJson(Token()),
            Settings = settings,
        };

    private static string CredentialsJson(string accessToken)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oauth"] = new Dictionary<string, string> { ["access_token"] = accessToken },
        });

    private const string TestAccountId = "acct_test";

    private static string Token(string accountId = TestAccountId)
        => TokenWithClaims(
            """{"https://api.openai.com/auth":{"chatgpt_account_id":"""
            + JsonSerializer.Serialize(accountId) + "}}");

    private static string TokenWithClaims(string claimsJson)
        => Base64Url("""{"alg":"none","typ":"JWT"}""") + "." + Base64Url(claimsJson) + ".";

    private static string Base64Url(string text)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string Unix(DateTimeOffset value)
        => value.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private static string UnixResetBody(double usedPercent, DateTimeOffset resetAt, long windowSeconds)
        => "{\"plan_type\":\"pro\",\"rate_limit\":{\"allowed\":true,\"primary_window\":{"
            + "\"used_percent\":" + usedPercent.ToString(CultureInfo.InvariantCulture)
            + ",\"reset_at\":" + Unix(resetAt)
            + ",\"limit_window_seconds\":" + windowSeconds.ToString(CultureInfo.InvariantCulture)
            + "}}}";

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
