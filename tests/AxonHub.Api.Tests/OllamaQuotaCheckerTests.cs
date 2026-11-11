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
/// Ports internal/server/biz/provider_quota/ollama_checker.go together with
/// ollama_checker_test.go: the balance API (account key) and settings page
/// (__Secure-session browser cookie) quote surfaces, the cookie allowlist
/// normalizer, the HTML meter scrape and the Go struct/time decoding of the
/// balance payload.
/// </summary>
public sealed class OllamaQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    private const string BalanceUrl = "https://ollama.com/api/balance";

    private const string SettingsUrl = "https://ollama.com/settings";

    private const string QuotaUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private const string CookieHeader =
        "__Secure-session=aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789";

    private const string MonthlyBalanceJson = """
        {
          "included": {
            "balance_usd": 72.5,
            "allowance_usd": 100,
            "period": {"from": "2026-09-15T09:30:00Z", "until": "2026-10-15T09:30:00Z"}
          },
          "purchased": {"balance_usd": 25}
        }
        """;

    // ------------------------------------------------------------------
    // Settings page collection
    // ------------------------------------------------------------------

    /// <summary>TestOllama_CheckQuota_Success (ollama_checker_test.go:63-104).</summary>
    [Fact]
    public async Task SettingsPageUsesTheSessionCookieAndParsesBothWindows()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => HtmlResponse(SettingsHtml());

        var quota = await checker.CheckQuotaAsync(
            Channel(settings: CookieSettings(CookieHeader)),
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(SettingsUrl, request.Url);
        Assert.Equal(CookieHeader, request.Header("Cookie"));
        Assert.Equal(string.Empty, request.Header("Authorization"));
        Assert.Equal("text/html,application/xhtml+xml", request.Header("Accept"));
        Assert.Equal(QuotaUserAgent, request.Header("User-Agent"));
        Assert.Equal(string.Empty, request.Body);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal("ollama", quota.ProviderType);
        Assert.Equal(2, quota.Limits.Count);

        var session = quota.Limits[0];
        Assert.Equal(QuotaWindows.FiveHours, session.Window);
        Assert.Equal(QuotaLimitTypes.Token, session.Type);
        Assert.Equal(ProviderQuotaStatuses.Available, session.Status);
        Assert.True(session.Ready);
        Assert.Equal(0.117, session.UsageRatio, 12);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 21, 0, 0, TimeSpan.Zero), session.NextResetAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 16, 0, 0, TimeSpan.Zero), session.PeriodStart);

        var weekly = quota.Limits[1];
        Assert.Equal(QuotaWindows.Weekly, weekly.Window);
        Assert.Equal(ProviderQuotaStatuses.Available, weekly.Status);
        Assert.Equal(0.625, weekly.UsageRatio, 12);
        Assert.Equal(new DateTimeOffset(2026, 9, 7, 0, 0, 0, TimeSpan.Zero), weekly.NextResetAt);
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero), weekly.PeriodStart);

        Assert.Equal(new DateTimeOffset(2026, 9, 5, 21, 0, 0, TimeSpan.Zero), quota.NextResetAt);
        Assert.Equal(
            """{"windows":{"5h":{"percent_remaining":88.3,"reset_time":"2026-09-05T21:00:00Z","status":"available","usage_percent":11.7},"weekly":{"percent_remaining":37.5,"reset_time":"2026-09-07T00:00:00Z","status":"available","usage_percent":62.5}}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    /// <summary>TestOllama_CheckQuota_Warning (ollama_checker_test.go:108-133).</summary>
    [Fact]
    public async Task WarningAtEightyFivePercentEscalatesTheOverallStatus()
    {
        var html = SettingsHtml().Replace(
            "aria-label=\"Weekly usage 62.5% used\"",
            "aria-label=\"Weekly usage 85% used\"",
            StringComparison.Ordinal);
        var checker = CreateChecker(OkExecutor(html));

        var quota = await checker.CheckQuotaAsync(
            Channel(settings: CookieSettings(CookieHeader)),
            CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        var windows = Map(quota.RawData["windows"]);
        Assert.Equal(ProviderQuotaStatuses.Warning, Map(windows[QuotaWindows.Weekly])["status"]);
    }

    /// <summary>TestOllama_CheckQuota_ExhaustedWeekly (ollama_checker_test.go:135-155).</summary>
    [Fact]
    public async Task AHundredPercentWindowIsExhaustedAndNotReady()
    {
        var html = SettingsHtml().Replace(
            "aria-label=\"Weekly usage 62.5% used\"",
            "aria-label=\"Weekly usage 100% used\"",
            StringComparison.Ordinal);
        var checker = CreateChecker(OkExecutor(html));

        var quota = await checker.CheckQuotaAsync(
            Channel(settings: CookieSettings(CookieHeader)),
            CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal(1.0, quota.Limits[1].UsageRatio, 12);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Limits[1].Status);
        Assert.False(quota.Limits[1].Ready);
    }

    /// <summary>TestOllama_CheckQuota_MissingCookie (ollama_checker_test.go:157-163).</summary>
    [Theory]
    [InlineData("{}", "{}")]
    [InlineData("""{"apiKey":"   "}""", "{}")]
    [InlineData("{}", """{"providerQuota":{"ollama":{"authCookie":"   "}}}""")]
    public async Task MissingCredentialsFailBeforeAnyRequest(
        string credentials,
        string settings)
    {
        var checker = CreateChecker(out var handler);

        var error = await Assert.ThrowsAsync<InvalidProviderQuotaCredentialsException>(
            () => checker.CheckQuotaAsync(
                Channel(credentials: credentials, settings: settings),
                CancellationToken.None));

        Assert.Equal(
            "provider quota credentials are invalid: channel has no Ollama API key or quota cookie",
            error.Message);
        Assert.Empty(handler.Requests);
    }

    /// <summary>TestOllama_CheckQuota_ExpiredCookie (ollama_checker_test.go:165-182).</summary>
    [Fact]
    public async Task ForbiddenSettingsPageIsACredentialError()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("forbidden", Encoding.UTF8, "text/plain"),
        };

        var error = await Assert.ThrowsAsync<InvalidProviderQuotaCredentialsException>(
            () => checker.CheckQuotaAsync(
                Channel(settings: CookieSettings(CookieHeader)),
                CancellationToken.None));

        Assert.Equal(
            "provider quota credentials are invalid: Ollama settings page returned 403 (expired session cookie?)",
            error.Message);
    }

    /// <summary>TestOllama_CheckQuota_ExpiredCookieLoginPage200 (ollama_checker_test.go:186-205).</summary>
    [Fact]
    public async Task LoginPageReturnedWith200IsACredentialError()
    {
        const string loginHtml =
            """<!DOCTYPE html><html><head><title>Sign in &middot; Ollama</title></head><body><form action="/signin"><input name="email"/><button>Sign in</button></form></body></html>""";
        var checker = CreateChecker(OkExecutor(loginHtml));

        var error = await Assert.ThrowsAsync<InvalidProviderQuotaCredentialsException>(
            () => checker.CheckQuotaAsync(
                Channel(settings: CookieSettings(CookieHeader)),
                CancellationToken.None));

        Assert.Equal(
            "provider quota credentials are invalid: ollama.com/settings returned the sign-in page (expired session cookie?)",
            error.Message);
    }

    /// <summary>TestOllama_LooksLikeLoginPage (ollama_checker_test.go:207-215).</summary>
    [Fact]
    public void LooksLikeLoginPageMatchesTheStableMarkers()
    {
        Assert.True(OllamaQuotaChecker.LooksLikeLoginPage(
            """<html><head><title>Sign in &middot; Ollama</title></head><body>...</body></html>"""));
        Assert.True(OllamaQuotaChecker.LooksLikeLoginPage("""<form action="/signin">Sign in</form>"""));
        Assert.True(OllamaQuotaChecker.LooksLikeLoginPage("signin"));
        Assert.True(OllamaQuotaChecker.LooksLikeLoginPage("Log in to your account"));
        Assert.True(OllamaQuotaChecker.LooksLikeLoginPage("""<div class="wos-session"></div>"""));
        Assert.True(OllamaQuotaChecker.LooksLikeLoginPage("""<a href="/sign-in">Continue</a>"""));

        // A normal settings page and a genuinely broken page are not logins.
        Assert.False(OllamaQuotaChecker.LooksLikeLoginPage(SettingsHtml()));
        Assert.False(OllamaQuotaChecker.LooksLikeLoginPage("""<html><body><p>no data</p></body></html>"""));
    }

    /// <summary>TestOllama_CheckQuota_OnlySessionWindow (ollama_checker_test.go:217-241).</summary>
    [Fact]
    public async Task OnlyTheSessionWindowIsKept()
    {
        const string html =
            """<!DOCTYPE html><html><head><title>Usage</title></head><body>"""
            + """<span class="text-sm">Session usage</span>"""
            + """<span class="text-sm">50% used</span>"""
            + """<div data-usage-track aria-label="Session usage 50% used"><div style="width: 50%;"></div></div>"""
            + """<div class="local-time" data-time="2026-09-05T21:00:00Z">Resets soon.</div>"""
            + """</body></html>""";
        var checker = CreateChecker(OkExecutor(html));

        var quota = await checker.CheckQuotaAsync(
            Channel(settings: CookieSettings(CookieHeader)),
            CancellationToken.None);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaWindows.FiveHours, limit.Window);
        Assert.Equal(0.5, limit.UsageRatio, 12);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 21, 0, 0, TimeSpan.Zero), limit.NextResetAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 5, 16, 0, 0, TimeSpan.Zero), limit.PeriodStart);
    }

    /// <summary>TestOllama_CheckQuota_NoWindows (ollama_checker_test.go:243-260).</summary>
    [Fact]
    public async Task NoUsageWindowsIsAGenericParseFailure()
    {
        const string html =
            """<!DOCTYPE html><html><head><title>Usage</title></head><body><p>No data</p></body></html>""";
        var checker = CreateChecker(OkExecutor(html));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(
                Channel(settings: CookieSettings(CookieHeader)),
                CancellationToken.None));

        Assert.Equal(
            "no Ollama usage windows found in settings page (expired cookie or markup change)",
            error.Message);
    }

    /// <summary>The settings status branch (ollama_checker.go:174-188) for non-handler statuses.</summary>
    [Theory]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(500, false)]
    [InlineData(302, false)]
    public async Task SettingsStatusesClassifyAsCredentialOrGeneric(int statusCode, bool credential)
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = statusCode,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
                Body = Encoding.UTF8.GetBytes("nope"),
            },
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => checker.CheckQuotaAsync(
                Channel(settings: CookieSettings(CookieHeader)),
                CancellationToken.None));

        if (credential)
        {
            var invalid = Assert.IsType<InvalidProviderQuotaCredentialsException>(error);
            Assert.Equal(
                "provider quota credentials are invalid: Ollama settings page returned "
                + statusCode
                + " (expired session cookie?)",
                invalid.Message);
        }
        else
        {
            Assert.IsNotType<InvalidProviderQuotaCredentialsException>(error);
            Assert.Equal("Ollama settings page returned " + statusCode, error.Message);
        }
    }

    /// <summary>TestOllama_SupportsChannel (ollama_checker_test.go:262-279).</summary>
    [Theory]
    [InlineData("ollama", "{}", """{"providerQuota":{"ollama":{"authCookie":"__Secure-session=aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789"}}}""", true)]
    [InlineData("ollama_anthropic", "{}", """{"providerQuota":{"ollama":{"authCookie":"__Secure-session=aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789"}}}""", true)]
    [InlineData("openai", "{}", """{"providerQuota":{"ollama":{"authCookie":"__Secure-session=aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789"}}}""", false)]
    [InlineData("ollama", "{}", "{}", false)]
    [InlineData("ollama", """{"apiKey":"ollama-key"}""", "{}", true)]
    [InlineData("ollama", """{"apiKey":"  "}""", "{}", false)]
    [InlineData("ollama", """{"apiKeys":["   ",""]}""", "{}", false)]
    [InlineData("ollama_anthropic", """{"apiKeys":["","user-key"]}""", "{}", true)]
    public void SupportsChannelMatchesOnlyOllamaTypesWithCredentials(
        string channelType,
        string credentials,
        string settings,
        bool expected)
    {
        var checker = CreateChecker(OkExecutor(SettingsHtml()));

        Assert.Equal(
            expected,
            checker.SupportsChannel(Channel(
                type: channelType,
                credentials: credentials,
                settings: settings)));
    }

    /// <summary>TestOllama_SupportsChannel_APIKey (ollama_checker_test.go:550-560).</summary>
    [Fact]
    public void SupportsChannelAcceptsAKeyWhenSettingsIsNull()
    {
        var checker = CreateChecker(OkExecutor(SettingsHtml()));

        Assert.True(checker.SupportsChannel(Channel(
            credentials: """{"apiKey":"ollama-key"}""",
            settings: null)));
        Assert.False(checker.SupportsChannel(Channel(
            credentials: """{"apiKey":"  "}""",
            settings: null)));
    }

    // ------------------------------------------------------------------
    // Cookie normalization
    // ------------------------------------------------------------------

    /// <summary>TestOllama_NormalizeCookie (ollama_checker_test.go:281-319).</summary>
    [Theory]
    [InlineData("__Secure-session=aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789; aid=38e79ba9; cf_clearance=xyz", CookieHeader)]
    [InlineData("Cookie: __Secure-session=aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789", CookieHeader)]
    [InlineData("__secure-session=aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789", CookieHeader)]
    [InlineData("  __Secure-session=aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789  ", CookieHeader)]
    [InlineData("aid=1; __Secure-session=aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789; cf=2", CookieHeader)]
    [InlineData("COOKIE: __Secure-session=aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789", CookieHeader)]
    public void NormalizeCookieKeepsOnlyTheSessionCookie(string raw, string expected)
    {
        Assert.Null(OllamaQuotaChecker.NormalizeOllamaCookie(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    /// <summary>The error branch of NormalizeOllamaCookie (ollama_checker.go:201-241).</summary>
    [Theory]
    [InlineData("aid=38e79ba9", "no __Secure-session Ollama cookie found")]
    [InlineData("__Secure-session=abc123", "Ollama cookie is too short to be a valid session")]
    [InlineData("__Secure-session=aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789\ncf_clearance=x", "cookie contains line breaks")]
    [InlineData("__Secure-session=aBcDeFgHiJkLmNoPqRsTuVwXyZ0123456789\r\ncf_clearance=x", "cookie contains line breaks")]
    [InlineData("", "cookie is empty")]
    [InlineData("   ", "cookie is empty")]
    [InlineData("__Secure-session=", "invalid cookie segment: expected name=value")]
    [InlineData("__Secure-session", "invalid cookie segment: expected name=value")]
    [InlineData("=value", "invalid cookie segment: expected name=value")]
    [InlineData("Cookie:", "no __Secure-session Ollama cookie found")]
    public void NormalizeCookieErrorsUseTheReferenceWording(string raw, string expected)
    {
        Assert.Equal(expected, OllamaQuotaChecker.NormalizeOllamaCookie(raw, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    /// <summary>Go's len counts bytes, so a short rune sequence can still pass (ollama_checker.go:234).</summary>
    [Fact]
    public void CookieLengthCountsUtf8Bytes()
    {
        // 15 two-byte runes = 30 bytes: Go accepts, a rune count would not.
        var accepted = "__Secure-session=" + new string('é', 15);
        Assert.Null(OllamaQuotaChecker.NormalizeOllamaCookie(accepted, out var normalized));
        Assert.Equal(accepted, normalized);

        // 14 two-byte runes = 28 bytes: below the minimum.
        var rejected = "__Secure-session=" + new string('é', 14);
        Assert.Equal(
            "Ollama cookie is too short to be a valid session",
            OllamaQuotaChecker.NormalizeOllamaCookie(rejected, out _));
    }

    // ------------------------------------------------------------------
    // Balance API
    // ------------------------------------------------------------------

    /// <summary>TestOllama_CheckQuota_BalanceMonthly (ollama_checker_test.go:395-424).</summary>
    [Fact]
    public async Task BalanceMonthlyMapsTheAllowanceAndCredits()
    {
        var executor = OkExecutor(MonthlyBalanceJson);
        var checker = CreateChecker(executor);

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"ollama-key"}"""),
            CancellationToken.None);

        var request = Assert.Single(executor.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(BalanceUrl, request.Url);
        Assert.Equal("Bearer ollama-key", Header(request, "Authorization"));
        Assert.Equal("application/json", Header(request, "Accept"));
        Assert.Equal(string.Empty, Header(request, "Cookie"));
        Assert.Null(request.Body);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal("ollama", quota.ProviderType);
        Assert.Equal(2, quota.Limits.Count);

        var monthly = quota.Limits[0];
        Assert.Equal(QuotaWindows.Monthly, monthly.Window);
        Assert.Equal(0.275, monthly.UsageRatio, 12);
        Assert.Equal(ProviderQuotaStatuses.Available, monthly.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero), monthly.NextResetAt);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 9, 30, 0, TimeSpan.Zero), monthly.PeriodStart);

        var credits = quota.Limits[1];
        Assert.Equal(QuotaWindows.Credits, credits.Window);
        Assert.True(ProviderQuotaRouting.IsBalanceLimit(credits));
        Assert.Equal(ProviderQuotaStatuses.Available, credits.Status);
        Assert.True(credits.Ready);
        Assert.Equal(0, credits.UsageRatio);
        Assert.Null(credits.NextResetAt);
        Assert.Null(credits.PeriodStart);

        Assert.Equal(new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero), quota.NextResetAt);
        // (1 - 72.5/100) * 100 is 27.500000000000004 under IEEE 754; Go's
        // json.Marshal emits the shortest round-trip form of that exact value,
        // which is why the reference test compares it with require.InDelta.
        Assert.Equal(
            """{"credits":{"purchased_usd":25},"windows":{"monthly":{"percent_remaining":72.5,"reset_time":"2026-10-15T09:30:00Z","status":"available","usage_percent":27.500000000000004}}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    /// <summary>TestOllama_CheckQuota_BalanceFreeAllowanceExhausted (ollama_checker_test.go:426-445).</summary>
    [Fact]
    public async Task FreeAllowanceExhaustedHasNoCreditsLimit()
    {
        const string body = """
            {
              "included": {
                "balance_usd": 0,
                "allowance_usd": 2.5,
                "period": {"from": "2026-09-22T14:45:33.799639Z", "until": "2026-10-22T14:45:33.799639Z"}
              },
              "purchased": {"balance_usd": 0}
            }
            """;
        var checker = CreateChecker(OkExecutor(body));

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"ollama-key"}"""),
            CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaWindows.Monthly, limit.Window);
        Assert.Equal(1.0, limit.UsageRatio, 12);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);
        Assert.False(quota.RawData.ContainsKey("credits"));

        // Subsecond resets survive parsing but not the rawData render.
        var reset = new DateTimeOffset(2026, 10, 22, 14, 45, 33, TimeSpan.Zero)
            .AddTicks(7_996_390);
        Assert.Equal(reset, limit.NextResetAt);
        Assert.Equal(
            """{"windows":{"monthly":{"percent_remaining":0,"reset_time":"2026-10-22T14:45:33Z","status":"exhausted","usage_percent":100}}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    /// <summary>TestOllama_CheckQuota_BalanceExhaustedAllowanceFallsBackToCredits (ollama_checker_test.go:449-469).</summary>
    [Fact]
    public async Task ExhaustedAllowanceWithCreditsKeepsTheChannelAvailable()
    {
        const string body = """
            {
              "included": {
                "balance_usd": 0,
                "allowance_usd": 100,
                "period": {"from": "2026-09-15T09:30:00Z", "until": "2026-10-15T09:30:00Z"}
              },
              "purchased": {"balance_usd": 5}
            }
            """;
        var checker = CreateChecker(OkExecutor(body));

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"ollama-key"}"""),
            CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);

        var monthly = quota.Limits.Single(limit => limit.Window == QuotaWindows.Monthly);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, monthly.Status);
        Assert.False(monthly.Ready);

        var (state, reason) = ProviderQuotaRouting.EvaluateQuotaRouting(
            quota.Limits,
            quota.Status,
            QuotaLimitTypes.Token,
            Now);
        Assert.Equal(QuotaRoutingStates.StickyOnly, state);
        Assert.Equal("window_exhausted_balance_fallback", reason);
    }

    /// <summary>TestOllama_CheckQuota_BalanceLegacyWindows (ollama_checker_test.go:471-496).</summary>
    [Fact]
    public async Task LegacyBalanceWindowsUseRemainingPercent()
    {
        const string body = """
            {
              "included": {
                "session": {"remaining_percent": 75, "resets_at": "2026-10-08T05:00:00Z"},
                "weekly": {"remaining_percent": 15, "resets_at": "2026-10-12T00:00:00Z"}
              },
              "purchased": {"balance_usd": 0}
            }
            """;
        var checker = CreateChecker(OkExecutor(body));

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"ollama-key"}"""),
            CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);

        var session = quota.Limits[0];
        Assert.Equal(QuotaWindows.FiveHours, session.Window);
        Assert.Equal(0.25, session.UsageRatio, 12);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 5, 0, 0, TimeSpan.Zero), session.NextResetAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 8, 0, 0, 0, TimeSpan.Zero), session.PeriodStart);

        var weekly = quota.Limits[1];
        Assert.Equal(QuotaWindows.Weekly, weekly.Window);
        Assert.Equal(0.85, weekly.UsageRatio, 12);
        Assert.Equal(ProviderQuotaStatuses.Warning, weekly.Status);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), weekly.NextResetAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 0, 0, 0, TimeSpan.Zero), weekly.PeriodStart);

        Assert.Equal(new DateTimeOffset(2026, 10, 8, 5, 0, 0, TimeSpan.Zero), quota.NextResetAt);
        Assert.Equal(
            """{"windows":{"5h":{"percent_remaining":75,"reset_time":"2026-10-08T05:00:00Z","status":"available","usage_percent":25},"weekly":{"percent_remaining":15,"reset_time":"2026-10-12T00:00:00Z","status":"warning","usage_percent":85}}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    /// <summary>TestOllama_CheckQuota_BalanceNoQuota (ollama_checker_test.go:498-506).</summary>
    [Fact]
    public async Task BalanceWithoutQuotaIsAnError()
    {
        var checker = CreateChecker(OkExecutor("""{"included": {}, "purchased": {"balance_usd": 0}}"""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(
                Channel(credentials: """{"apiKey":"ollama-key"}"""),
                CancellationToken.None));

        Assert.Equal("no Ollama quota found in balance response", error.Message);
    }

    /// <summary>TestOllama_CheckQuota_BalanceUnauthorized (ollama_checker_test.go:508-515).</summary>
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task RejectedBalanceStatusesAreCredentialErrors(int statusCode)
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = statusCode,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
                Body = Encoding.UTF8.GetBytes("""{"error":"invalid credentials"}"""),
            },
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidProviderQuotaCredentialsException>(
            () => checker.CheckQuotaAsync(
                Channel(credentials: """{"apiKey":"bad-key"}"""),
                CancellationToken.None));

        Assert.Equal(
            "provider quota credentials are invalid: Ollama balance API returned " + statusCode,
            error.Message);
    }

    /// <summary>TestOllama_CheckQuota_BalanceUnauthorizedFallsBackToCookie (ollama_checker_test.go:517-534).</summary>
    [Fact]
    public async Task RejectedKeyFallsBackToTheStoredCookie()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, request, _) => request.Url == BalanceUrl
                ? new HttpResponseData
                {
                    StatusCode = 401,
                    Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
                    Body = Encoding.UTF8.GetBytes("""{"error":"invalid credentials"}"""),
                }
                : Ok(SettingsHtml()),
        };
        var checker = CreateChecker(executor);

        var quota = await checker.CheckQuotaAsync(
            Channel(
                credentials: """{"apiKey":"bad-key"}""",
                settings: CookieSettings(CookieHeader)),
            CancellationToken.None);

        Assert.Equal(
            new[] { BalanceUrl, SettingsUrl },
            executor.Requests.Select(request => request.Url));
        Assert.Equal("Bearer bad-key", Header(executor.Requests[0], "Authorization"));

        var settingsRequest = executor.Requests[1];
        Assert.Equal(CookieHeader, Header(settingsRequest, "Cookie"));
        Assert.Equal(string.Empty, Header(settingsRequest, "Authorization"));

        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(QuotaWindows.FiveHours, quota.Limits[0].Window);
    }

    /// <summary>TestOllama_CheckQuota_BalanceServerErrorDoesNotFallBack (ollama_checker_test.go:536-548).</summary>
    [Fact]
    public async Task ServerErrorDoesNotFallBackToTheCookie()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = 502,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
                Body = Encoding.UTF8.GetBytes("bad gateway"),
            },
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(
                Channel(
                    credentials: """{"apiKey":"ollama-key"}""",
                    settings: CookieSettings(CookieHeader)),
                CancellationToken.None));

        Assert.Equal("Ollama balance API returned 502", error.Message);
        Assert.Null(GoErrors.As<InvalidProviderQuotaCredentialsException>(error));
        Assert.Equal(1, executor.Calls);
    }

    /// <summary>TestOllama_CheckQuota_BalanceSkipsDisabledKey (ollama_checker_test.go:562-577).</summary>
    [Theory]
    // No disabled list means the first configured key serves; only an entry for
    // revoked-key (with an unexpired expiry) parks it.
    [InlineData(null, "Bearer revoked-key")]
    [InlineData("[]", "Bearer revoked-key")]
    [InlineData("""[{"key":"revoked-key","expiresAt":"2026-10-07T00:00:00Z"}]""", "Bearer revoked-key")]
    [InlineData("""[{"key":"revoked-key","expiresAt":"2026-10-09T00:00:00Z"}]""", "Bearer good-key")]
    [InlineData("""[{"key":"  revoked-key  "}]""", "Bearer good-key")]
    [InlineData("not-json", "Bearer revoked-key")]
    public async Task DisabledKeysAreSkipped(string? disabledApiKeys, string expectedAuthorization)
    {
        var executor = OkExecutor(MonthlyBalanceJson);
        var checker = CreateChecker(executor);

        await checker.CheckQuotaAsync(
            Channel(
                credentials: """{"apiKeys":["revoked-key","good-key"]}""",
                disabledApiKeys: disabledApiKeys),
            CancellationToken.None);

        Assert.Equal(expectedAuthorization, Header(Assert.Single(executor.Requests), "Authorization"));
    }

    /// <summary>TestOllama_CheckQuota_BalanceLegacyExhaustedWindow (ollama_checker_test.go:579-605).</summary>
    [Theory]
    [InlineData("0", ProviderQuotaStatuses.Exhausted, false)]
    [InlineData("5", ProviderQuotaStatuses.Available, true)]
    public async Task LegacyExhaustedWindowNeedsCreditsToStayReady(
        string purchased,
        string expectedStatus,
        bool expectedReady)
    {
        var body = """
            {
              "included": {
                "session": {"remaining_percent": 0, "resets_at": "2026-10-08T05:00:00Z"},
                "weekly": {"remaining_percent": 60, "resets_at": "2026-10-12T00:00:00Z"}
              },
              "purchased": {"balance_usd":
            """
            + purchased
            + """
              }
            }
            """;
        var checker = CreateChecker(OkExecutor(body));

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"ollama-key"}"""),
            CancellationToken.None);

        Assert.Equal(expectedStatus, quota.Status);
        Assert.Equal(expectedReady, quota.Ready);
    }

    [Fact]
    public async Task BalanceTransportFailureIsWrapped()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new HttpRequestException("connection refused"),
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(
                Channel(credentials: """{"apiKey":"ollama-key"}"""),
                CancellationToken.None));

        Assert.Equal("Ollama balance request failed: connection refused", error.Message);
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    [Fact]
    public async Task SettingsTransportFailureIsWrapped()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new HttpRequestException("connection refused"),
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(
                Channel(settings: CookieSettings(CookieHeader)),
                CancellationToken.None));

        Assert.Equal("Ollama settings request failed: connection refused", error.Message);
    }

    /// <summary>
    /// ollama_checker.go:108-114 and ollama_checker_test.go:324: the checker's
    /// client refuses HTTPS to HTTP redirects, so the API key is never replayed
    /// over cleartext.
    /// </summary>
    [Fact]
    public async Task BalanceDowngradeRedirectIsRefused()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.TryAddWithoutValidation("Location", "http://ollama.com/api/balance");
            return response;
        };

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(
                Channel(credentials: """{"apiKey":"ollama-key"}"""),
                CancellationToken.None));

        Assert.Equal(
            """Ollama balance request failed: HTTP request failed: Get "http://ollama.com/api/balance": refusing HTTPS to HTTP redirect""",
            error.Message);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(BalanceUrl, request.Url);
    }

    /// <summary>Same rejection covers the cookie-bearing settings-page request.</summary>
    [Fact]
    public async Task SettingsDowngradeRedirectIsRefused()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.TryAddWithoutValidation("Location", "http://ollama.com/settings");
            return response;
        };

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(
                Channel(settings: CookieSettings(CookieHeader)),
                CancellationToken.None));

        Assert.Equal(
            """Ollama settings request failed: HTTP request failed: Get "http://ollama.com/settings": refusing HTTPS to HTTP redirect""",
            error.Message);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(SettingsUrl, request.Url);
    }

    [Fact]
    public async Task ExecutorHttpErrorMapsToTheBalanceStatusError()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new HttpClientError(
                "GET", BalanceUrl, 403, "403 Forbidden"),
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidProviderQuotaCredentialsException>(
            () => checker.CheckQuotaAsync(
                Channel(credentials: """{"apiKey":"bad-key"}"""),
                CancellationToken.None));

        Assert.Equal(
            "provider quota credentials are invalid: Ollama balance API returned 403",
            error.Message);
    }

    // ------------------------------------------------------------------
    // Go balance decoding semantics
    // ------------------------------------------------------------------

    /// <summary>
    /// json.Unmarshal into ollamaBalanceResponse: the type texts are Go's
    /// reflect rendering, probe-locked (C003/C221).
    /// </summary>
    [Theory]
    [InlineData("[]", "json: cannot unmarshal array into Go value of type provider_quota.ollamaBalanceResponse")]
    [InlineData("\"x\"", "json: cannot unmarshal string into Go value of type provider_quota.ollamaBalanceResponse")]
    [InlineData("123", "json: cannot unmarshal number into Go value of type provider_quota.ollamaBalanceResponse")]
    [InlineData("true", "json: cannot unmarshal bool into Go value of type provider_quota.ollamaBalanceResponse")]
    [InlineData(
        """{"included":"x"}""",
        """json: cannot unmarshal string into Go struct field ollamaBalanceResponse.included of type struct { BalanceUSD *float64 "json:\"balance_usd\""; AllowanceUSD *float64 "json:\"allowance_usd\""; Period *struct { From *time.Time "json:\"from\""; Until *time.Time "json:\"until\"" } "json:\"period\""; Session *provider_quota.ollamaBalanceWindow "json:\"session\""; Weekly *provider_quota.ollamaBalanceWindow "json:\"weekly\"" }""")]
    [InlineData(
        """{"included":[]}""",
        """json: cannot unmarshal array into Go struct field ollamaBalanceResponse.included of type struct { BalanceUSD *float64 "json:\"balance_usd\""; AllowanceUSD *float64 "json:\"allowance_usd\""; Period *struct { From *time.Time "json:\"from\""; Until *time.Time "json:\"until\"" } "json:\"period\""; Session *provider_quota.ollamaBalanceWindow "json:\"session\""; Weekly *provider_quota.ollamaBalanceWindow "json:\"weekly\"" }""")]
    [InlineData(
        """{"included":123}""",
        """json: cannot unmarshal number into Go struct field ollamaBalanceResponse.included of type struct { BalanceUSD *float64 "json:\"balance_usd\""; AllowanceUSD *float64 "json:\"allowance_usd\""; Period *struct { From *time.Time "json:\"from\""; Until *time.Time "json:\"until\"" } "json:\"period\""; Session *provider_quota.ollamaBalanceWindow "json:\"session\""; Weekly *provider_quota.ollamaBalanceWindow "json:\"weekly\"" }""")]
    [InlineData("""{"included":{"balance_usd":"x"}}""", "json: cannot unmarshal string into Go struct field .included.balance_usd of type float64")]
    [InlineData("""{"included":{"balance_usd":true}}""", "json: cannot unmarshal bool into Go struct field .included.balance_usd of type float64")]
    [InlineData("""{"included":{"balance_usd":[]}}""", "json: cannot unmarshal array into Go struct field .included.balance_usd of type float64")]
    [InlineData("""{"included":{"balance_usd":1e400}}""", "json: cannot unmarshal number 1e400 into Go struct field .included.balance_usd of type float64")]
    [InlineData("""{"included":{"allowance_usd":"x"}}""", "json: cannot unmarshal string into Go struct field .included.allowance_usd of type float64")]
    [InlineData("""{"included":{"allowance_usd":1e400}}""", "json: cannot unmarshal number 1e400 into Go struct field .included.allowance_usd of type float64")]
    [InlineData(
        """{"included":{"period":"x"}}""",
        """json: cannot unmarshal string into Go struct field .included.period of type struct { From *time.Time "json:\"from\""; Until *time.Time "json:\"until\"" }""")]
    [InlineData(
        """{"included":{"period":[]}}""",
        """json: cannot unmarshal array into Go struct field .included.period of type struct { From *time.Time "json:\"from\""; Until *time.Time "json:\"until\"" }""")]
    [InlineData("""{"included":{"period":{"from":"x"}}}""", "parsing time \"x\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"x\" as \"2006\"")]
    [InlineData("""{"included":{"period":{"until":"nope"}}}""", "parsing time \"nope\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"nope\" as \"2006\"")]
    [InlineData("""{"included":{"period":{"from":123}}}""", "Time.UnmarshalJSON: input is not a JSON string")]
    [InlineData("""{"included":{"period":{"from":"2026-02-30T00:00:00Z"}}}""", """parsing time "2026-02-30T00:00:00Z": day out of range""")]
    [InlineData("""{"included":{"session":"x"}}""", "json: cannot unmarshal string into Go struct field .included.session of type provider_quota.ollamaBalanceWindow")]
    [InlineData("""{"included":{"session":[]}}""", "json: cannot unmarshal array into Go struct field .included.session of type provider_quota.ollamaBalanceWindow")]
    [InlineData("""{"included":{"session":{"remaining_percent":"x"}}}""", "json: cannot unmarshal string into Go struct field ollamaBalanceWindow.included.session.remaining_percent of type float64")]
    [InlineData("""{"included":{"session":{"remaining_percent":1e400}}}""", "json: cannot unmarshal number 1e400 into Go struct field ollamaBalanceWindow.included.session.remaining_percent of type float64")]
    [InlineData("""{"included":{"session":{"resets_at":"nope"}}}""", "parsing time \"nope\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"nope\" as \"2006\"")]
    [InlineData("""{"included":{"session":{"resets_at":123}}}""", "Time.UnmarshalJSON: input is not a JSON string")]
    [InlineData("""{"included":{"weekly":{"remaining_percent":true}}}""", "json: cannot unmarshal bool into Go struct field ollamaBalanceWindow.included.weekly.remaining_percent of type float64")]
    [InlineData(
        """{"purchased":"x"}""",
        """json: cannot unmarshal string into Go struct field ollamaBalanceResponse.purchased of type struct { BalanceUSD float64 "json:\"balance_usd\"" }""")]
    [InlineData("""{"purchased":{"balance_usd":"x"}}""", "json: cannot unmarshal string into Go struct field .purchased.balance_usd of type float64")]
    [InlineData("""{"purchased":{"balance_usd":1e400}}""", "json: cannot unmarshal number 1e400 into Go struct field .purchased.balance_usd of type float64")]
    public void JsonDecodeErrorsUseGoWording(string json, string expected)
    {
        var error = Assert.Throws<JsonException>(
            () => OllamaQuotaChecker.ParseBalanceResponse(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(expected, error.Message);
    }

    /// <summary>json.Unmarshal(null) leaves the struct zero: the payload then reports no quota (ollama_checker.go:280-284 / 318-321).</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("""{"included":null,"purchased":null}""")]
    public void RootNullDecodesToAnEmptyPayload(string json)
    {
        var parsed = OllamaQuotaChecker.ParseBalanceResponse(Encoding.UTF8.GetBytes(json));
        Assert.Null(parsed.BalanceUsd);
        Assert.Null(parsed.AllowanceUsd);
        Assert.Null(parsed.Session);
        Assert.Null(parsed.Weekly);
        Assert.Equal(0, parsed.PurchasedBalanceUsd);

        var error = Assert.Throws<InvalidOperationException>(
            () => OllamaQuotaChecker.ParseBalance(Encoding.UTF8.GetBytes(json), Now));
        Assert.Equal("no Ollama quota found in balance response", error.Message);
    }

    [Fact]
    public void UnderflowedNumbersDecodeToZero()
    {
        var parsed = OllamaQuotaChecker.ParseBalanceResponse(
            Encoding.UTF8.GetBytes("""{"included":{"balance_usd":1e-400,"session":{"remaining_percent":1e-400}}}"""));

        Assert.Equal(0, parsed.BalanceUsd);
        Assert.Equal(0, parsed.Session!.RemainingPercent);
    }

    /// <summary>Duplicate keys decode over the same struct, last value wins per field (Go merge semantics).</summary>
    [Fact]
    public void DuplicateKeysMergeIntoTheSameStruct()
    {
        const string json = """
            {
              "included": {"balance_usd": 50, "allowance_usd": 100, "session": {"remaining_percent": 75}},
              "Included": {"session": {"resets_at": "2026-10-08T05:00:00Z"}},
              "included": {"balance_usd": 60}
            }
            """;

        var parsed = OllamaQuotaChecker.ParseBalanceResponse(Encoding.UTF8.GetBytes(json));

        // The third key overwrites balance_usd; the folded third key decodes
        // into the same Included struct, keeping the earlier allowance, session
        // percentage and reset.
        Assert.Equal(60, parsed.BalanceUsd);
        Assert.Equal(100, parsed.AllowanceUsd);
        Assert.Equal(75, parsed.Session!.RemainingPercent);
        Assert.Equal(
            new DateTimeOffset(2026, 10, 8, 5, 0, 0, TimeSpan.Zero),
            parsed.Session.ResetsAt);
    }

    [Fact]
    public void NullResetsPointerFieldsButLeavesValueFields()
    {
        var parsed = OllamaQuotaChecker.ParseBalanceResponse(Encoding.UTF8.GetBytes(
            """{"included":{"balance_usd":10,"balance_usd":null,"session":{"remaining_percent":5}},"purchased":{"balance_usd":25},"purchased":{"balance_usd":null}}"""));

        Assert.Null(parsed.BalanceUsd);
        Assert.Equal(25, parsed.PurchasedBalanceUsd);
        Assert.Equal(5, parsed.Session!.RemainingPercent);
    }

    /// <summary>Go matches the JSON tag and the struct field name, folding ASCII case only.</summary>
    [Theory]
    [InlineData("""{"Included":{"BalanceUSD":50,"AllowanceUSD":100}}""")]
    [InlineData("""{"INCLUDED":{"BALANCE_USD":50,"ALLOWANCE_USD":100}}""")]
    [InlineData("""{"included":{"balanceusd":50,"allowanceusd":100}}""")]
    [InlineData("""{"included":{"balance_USD":50,"allowanceUsd":100}}""")]
    public void FieldNamesFoldAsciiAndAcceptTheGoFieldName(string json)
    {
        var parsed = OllamaQuotaChecker.ParseBalanceResponse(Encoding.UTF8.GetBytes(json));

        Assert.Equal(50, parsed.BalanceUsd);
        Assert.Equal(100, parsed.AllowanceUsd);
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var parsed = OllamaQuotaChecker.ParseBalanceResponse(Encoding.UTF8.GetBytes(
            """{"included":{"unknown":1,"balance_usd":5},"other":[1,2]}"""));

        Assert.Equal(5, parsed.BalanceUsd);
        Assert.Null(parsed.AllowanceUsd);
    }

    /// <summary>time.Parse(RFC3339) leniency that DateTimeOffset can represent (C221).</summary>
    [Theory]
    [InlineData("2026-09-15T9:30:00Z", 0, 0)]
    [InlineData("2026-09-15T09:30:00,5Z", 0, 5_000_000)]
    [InlineData("2026-09-15T09:30:00+14:00", 840, 0)]
    [InlineData("2026-09-15T09:30:00-05:30", -330, 0)]
    public void AcceptedTimeSpellingsDecode(string value, int offsetMinutes, long subsecondTicks)
    {
        var parsed = OllamaQuotaChecker.ParseBalanceResponse(Encoding.UTF8.GetBytes(
            "{\"included\":{\"period\":{\"from\":\"" + value + "\"}}}"));

        var from = Assert.IsType<DateTimeOffset>(parsed.Period!.From);
        Assert.Equal(offsetMinutes, from.Offset.TotalMinutes);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 15, 9, 30, 0, TimeSpan.Zero)
                .AddMinutes(-offsetMinutes)
                .AddTicks(subsecondTicks),
            from);
    }

    [Fact]
    public void SubsecondFractionsAreTruncatedToTicks()
    {
        var parsed = OllamaQuotaChecker.ParseBalanceResponse(Encoding.UTF8.GetBytes(
            """{"included":{"period":{"from":"2026-09-15T09:30:00.123456789Z"}}}"""));

        Assert.Equal(
            new DateTimeOffset(2026, 9, 15, 9, 30, 0, TimeSpan.Zero).AddTicks(1_234_567),
            parsed.Period!.From);
    }

    /// <summary>Values Go accepts but DateTimeOffset cannot hold drop the time (C123/C207).</summary>
    [Theory]
    [InlineData("0000-01-01T00:00:00Z")]
    [InlineData("2026-09-15T09:30:00+24:00")]
    public void GoAcceptedButUnrepresentableTimesAreDropped(string value)
    {
        var parsed = OllamaQuotaChecker.ParseBalanceResponse(Encoding.UTF8.GetBytes(
            "{\"included\":{\"period\":{\"from\":\"" + value + "\"}}}"));

        Assert.Null(parsed.Period!.From);
    }

    /// <summary>The rawData reset text keeps the parsed offset and drops subseconds (C221).</summary>
    [Theory]
    [InlineData("2026-10-08T05:00:00+02:00", "2026-10-08T05:00:00+02:00")]
    [InlineData("2026-10-08T05:00:00.123456Z", "2026-10-08T05:00:00Z")]
    [InlineData("2026-10-07T22:00:00+02:00", "2026-10-07T22:00:00+02:00")]
    public async Task ResetTimeKeepsTheSourceOffset(string resetsAt, string expected)
    {
        var body = """
            {"included":{"session":{"remaining_percent":75,"resets_at":
            """
            + "\""
            + resetsAt
            + "\""
            + """
              }},"purchased":{"balance_usd":0}}
            """;
        var checker = CreateChecker(OkExecutor(body));

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"ollama-key"}"""),
            CancellationToken.None);

        var windows = Map(quota.RawData["windows"]);
        Assert.Equal(expected, Map(windows[QuotaWindows.FiveHours])["reset_time"]);
    }

    [Fact]
    public async Task PastResetsAreClearedByNormalizationButStayInRawData()
    {
        const string body = """
            {
              "included": {
                "session": {"remaining_percent": 75, "resets_at": "2026-10-07T22:00:00Z"},
                "weekly": {"remaining_percent": 15, "resets_at": "2026-10-12T00:00:00Z"}
              },
              "purchased": {"balance_usd": 0}
            }
            """;
        var checker = CreateChecker(OkExecutor(body));

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"ollama-key"}"""),
            CancellationToken.None);

        var session = quota.Limits.Single(limit => limit.Window == QuotaWindows.FiveHours);
        Assert.Null(session.NextResetAt);
        Assert.Null(session.PeriodStart);
        Assert.Equal(
            "2026-10-07T22:00:00Z",
            Map(Map(quota.RawData["windows"])[QuotaWindows.FiveHours])["reset_time"]);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), quota.NextResetAt);
    }

    [Theory]
    [InlineData(150, 0, ProviderQuotaStatuses.Available)]
    [InlineData(-20, 1.0, ProviderQuotaStatuses.Exhausted)]
    public async Task RemainingPercentIsClampedToTheZeroHundredRange(
        double remainingPercent,
        double expectedRatio,
        string expectedStatus)
    {
        var body = """
            {"included":{"session":{"remaining_percent":
            """
            + remainingPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "}},\"purchased\":{\"balance_usd\":0}}";
        var checker = CreateChecker(OkExecutor(body));

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"ollama-key"}"""),
            CancellationToken.None);

        var session = Assert.Single(quota.Limits);
        Assert.Equal(expectedRatio, session.UsageRatio, 12);
        Assert.Equal(expectedStatus, session.Status);
    }

    // ------------------------------------------------------------------
    // HTML scrape edge cases
    // ------------------------------------------------------------------

    /// <summary>The keyword scan only looks 4000 bytes ahead (ollama_checker.go:359).</summary>
    [Theory]
    [InlineData(3_000, true)]
    [InlineData(3_990, false)]
    public void TheMeterMustFitInTheFourThousandByteScanWindow(int fillerLength, bool found)
    {
        var html = "Session usage"
            + new string('x', fillerLength)
            + """<div aria-label="Session usage 10% used"></div><div data-time="2026-09-05T21:00:00Z"></div>""";

        if (found)
        {
            var quota = OllamaQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(html));
            var limit = Assert.Single(quota.Limits);
            Assert.Equal(0.1, limit.UsageRatio, 12);
        }
        else
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => OllamaQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(html)));
            Assert.Contains("no Ollama usage windows found", error.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>Go's \s is the ASCII class; NBSP is not a separator (C221).</summary>
    [Theory]
    [InlineData("10 % used", false)]
    [InlineData("10 \t % used", true)]
    public void PercentSeparatorsAreAsciiWhitespaceOnly(string meterText, bool found)
    {
        var html = "<div>Session usage</div><div aria-label=\"Session usage "
            + meterText
            + "\"></div>";

        if (found)
        {
            var quota = OllamaQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(html));
            Assert.Equal(0.1, Assert.Single(quota.Limits).UsageRatio, 12);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(
                () => OllamaQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(html)));
        }
    }

    /// <summary>The first keyword occurrence wins; the scraper never rescans (ollama_checker.go:355-358).</summary>
    [Fact]
    public void OnlyTheFirstKeywordOccurrenceIsScanned()
    {
        var html = "Session usage"
            + new string('x', 5_000)
            + """<div aria-label="Session usage 10% used"></div>""";

        var error = Assert.Throws<InvalidOperationException>(
            () => OllamaQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(html)));
        Assert.Contains("no Ollama usage windows found", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-a-time")]
    [InlineData("0000-01-01T00:00:00Z")]
    public void AnInvalidResetTimeDropsTheResetButKeepsTheWindow(string dataTime)
    {
        var html = "<div>Session usage</div><div aria-label=\"Session usage 10% used\"></div>"
            + "<div data-time=\""
            + dataTime
            + "\"></div>";

        var quota = OllamaQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(html));

        var limit = Assert.Single(quota.Limits);
        Assert.Null(limit.NextResetAt);
        Assert.Null(limit.PeriodStart);
    }

    [Fact]
    public void APercentThatIsNotANumberDropsTheWindow()
    {
        const string html = """<div>Session usage</div><div aria-label="Session usage 1.2.3% used"></div>""";

        var error = Assert.Throws<InvalidOperationException>(
            () => OllamaQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(html)));
        Assert.Contains("no Ollama usage windows found", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidUtf8BytesInFillerStillParseTheWindow()
    {
        var prefix = Encoding.UTF8.GetBytes("<div>Session usage</div>");
        var invalid = new byte[] { 0xFF, 0xFE, 0x80 };
        var suffix = Encoding.UTF8.GetBytes("""<div aria-label="Session usage 10% used"></div>""");
        var body = prefix.Concat(invalid).Concat(suffix).ToArray();

        var quota = OllamaQuotaChecker.ParseResponse(body);

        Assert.Equal(0.1, Assert.Single(quota.Limits).UsageRatio, 12);
    }

    [Fact]
    public void EmptyBodyIsAGenericParseFailure()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => OllamaQuotaChecker.ParseResponse([]));

        Assert.Equal(
            "no Ollama usage windows found in settings page (expired cookie or markup change)",
            error.Message);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static OllamaQuotaChecker CreateChecker(
        out ClaudeCodeQuotaCheckerTests.RecordingHttpHandler handler)
    {
        handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new OllamaQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now));
    }

    private static OllamaQuotaChecker CreateChecker(
        ScriptedExecutor executor,
        ScriptedProxyExecutorFactory? factory = null)
        => new(
            executor,
            factory ?? new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now));

    private static ChannelRecord Channel(
        string type = "ollama",
        string credentials = "{}",
        string? settings = "{}",
        string? disabledApiKeys = null)
        => new()
        {
            Type = type,
            Credentials = credentials,
            Settings = settings,
            DisabledApiKeys = disabledApiKeys,
        };

    private static string CookieSettings(string cookie)
        => "{\"providerQuota\":{\"ollama\":{\"authCookie\":"
            + JsonSerializer.Serialize(cookie)
            + "}}}";

    private static string SettingsHtml()
        => """<!DOCTYPE html><html><head><title>Usage &middot; Settings</title></head><body>"""
            + """<h2>Cloud usage</h2>"""
            + """<div>"""
            + """<div class="flex justify-between mb-2">"""
            + """<span class="text-sm">Session usage</span>"""
            + """<span class="text-sm">11.7% used</span>"""
            + """</div>"""
            + """<div class="relative group" data-usage-meter>"""
            + """<div class="relative h-3 overflow-hidden rounded-full bg-neutral-200" data-usage-track aria-label="Session usage 11.7% used">"""
            + """<div class="flex h-full" style="width: 11.7%;"></div>"""
            + """</div>"""
            + """</div>"""
            + """<div class="text-xs text-neutral-500 mt-1 local-time" data-time="2026-09-05T21:00:00Z">Resets in 3 hours.</div>"""
            + """</div>"""
            + """<div>"""
            + """<div class="flex justify-between mb-2">"""
            + """<span class="text-sm">Weekly usage</span>"""
            + """<span class="text-sm">62.5% used</span>"""
            + """</div>"""
            + """<div class="relative group" data-usage-meter>"""
            + """<div class="relative h-3 overflow-hidden rounded-full bg-neutral-200" data-usage-track aria-label="Weekly usage 62.5% used">"""
            + """<div class="flex h-full" style="width: 62.5%;"></div>"""
            + """</div>"""
            + """</div>"""
            + """<div class="text-xs text-neutral-500 mt-1 local-time" data-time="2026-09-07T00:00:00Z">Resets in 1 day.</div>"""
            + """</div>"""
            + """</body></html>""";

    private static ScriptedExecutor OkExecutor(string body) => new()
    {
        OnDo = (_, _, _) => Ok(body),
    };

    private static HttpResponseData Ok(string body) => new()
    {
        StatusCode = 200,
        Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
        Body = Encoding.UTF8.GetBytes(body),
    };

    private static HttpResponseMessage HtmlResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/html"),
    };

    private static string Header(HttpRequestData request, string name)
        => request.Headers is not null && request.Headers.TryGetValue(name, out var values)
            ? string.Join(", ", values)
            : string.Empty;

    private static Dictionary<string, object?> Map(object? value)
        => Assert.IsType<Dictionary<string, object?>>(value);
}
