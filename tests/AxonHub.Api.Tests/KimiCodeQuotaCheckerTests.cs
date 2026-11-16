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
/// Ports internal/server/biz/provider_quota/kimi_code_checker.go together with
/// kimi_code_checker_test.go: the base-URL-derived /usages endpoint, the
/// legacy-first API key selection, the usage row plus limit rows (optionally
/// wrapped in a <c>detail</c> object), the booster wallet mapping, and the
/// Go URL/JSON edge semantics (ForceQuery, encodePath, map/slice merge on
/// duplicate keys). The wire shape is locked through the real
/// <see cref="HttpExecutor"/> over a recording handler.
/// </summary>
public sealed class KimiCodeQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2099, 6, 25, 10, 0, 0, TimeSpan.Zero);

    private const string DefaultBaseUrl = "https://api.kimi.com/coding";

    private const string DefaultUsageUrl = "https://api.kimi.com/coding/v1/usages";

    private const string DefaultCredentials = """{"apiKey":"test-api-key"}""";

    /// <summary>The payload of TestKimiCode_CheckQuota (kimi_code_checker_test.go:26-38).</summary>
    private const string ReferenceBody = """
        {
            "usage":{"name":"Weekly limit","used":80,"limit":100,"resetAt":"2099-07-20T00:00:00.123456Z"},
            "limits":[
                {"detail":{"remaining":"0","limit":"20"},"window":{"duration":300,"timeUnit":"MINUTE"}},
                {"detail":{"used":1,"limit":10,"title":"Daily limit"}}
            ],
            "boosterWallet":{
                "balance":{"type":"BOOSTER","amount":1250000000,"amountLeft":250000000},
                "monthlyChargeLimitEnabled":true,
                "monthlyChargeLimit":{"priceInCents":5000,"currency":"USD"},
                "monthlyUsed":{"priceInCents":1234,"currency":"USD"}
            }
        }
        """;

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaFetchesTheUsagesEndpointAndMapsTheReferencePayload()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(ReferenceBody);

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKey":" kimi-token "}"""),
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(DefaultUsageUrl, request.Url);
        Assert.Equal("Bearer kimi-token", request.Header("Authorization"));
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Equal(string.Empty, request.Body);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal("kimi_code", quota.ProviderType);

        var resetAt = new DateTimeOffset(2099, 7, 20, 0, 0, 0, TimeSpan.Zero)
            .AddTicks(1_234_560);
        Assert.Equal(resetAt, quota.NextResetAt);

        Assert.Equal(3, quota.Limits.Count);

        var weekly = quota.Limits[0];
        Assert.Equal(QuotaLimitTypes.Token, weekly.Type);
        Assert.Equal("7d", weekly.Window);
        Assert.Equal(ProviderQuotaStatuses.Warning, weekly.Status);
        Assert.Equal(0.8, weekly.UsageRatio, 9);
        Assert.True(weekly.Ready);
        Assert.Equal(resetAt, weekly.NextResetAt);
        Assert.Equal(resetAt.AddDays(-7), weekly.PeriodStart);

        var fiveHours = quota.Limits[1];
        Assert.Equal("5h", fiveHours.Window);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, fiveHours.Status);
        Assert.Equal(1.0, fiveHours.UsageRatio, 9);
        Assert.False(fiveHours.Ready);
        Assert.Null(fiveHours.NextResetAt);
        Assert.Null(fiveHours.PeriodStart);

        var daily = quota.Limits[2];
        Assert.Equal("Daily limit", daily.Window);
        Assert.Equal(ProviderQuotaStatuses.Available, daily.Status);
        Assert.Equal(0.1, daily.UsageRatio, 9);

        var rows = Assert.IsType<List<KimiCodeUsageRow>>(quota.RawData["rows"]);
        Assert.Equal(3, rows.Count);
        Assert.Equal("Weekly limit", rows[0].Label);
        Assert.Equal(80, rows[0].Used);
        Assert.Equal(100, rows[0].Limit);
        Assert.Equal("2099-07-20T00:00:00.123456Z", rows[0].ResetAt);
        Assert.Null(rows[0].ResetAfterSeconds);
        Assert.Equal("5h limit", rows[1].Label);
        Assert.Equal(20, rows[1].Used);
        Assert.Equal(20, rows[1].Limit);
        Assert.Equal("Daily limit", rows[2].Label);

        var wallet = Assert.IsType<KimiCodeBoosterWallet>(quota.RawData["boosterWallet"]);
        Assert.Equal(250, wallet.BalanceCents);
        Assert.Equal(1250, wallet.TotalCents);
        Assert.True(wallet.MonthlyChargeLimitEnabled);
        Assert.Equal(5000, wallet.MonthlyChargeLimitCents);
        Assert.Equal(1234, wallet.MonthlyUsedCents);
        Assert.Equal("USD", wallet.Currency);

        Assert.Equal(
            """{"boosterWallet":{"balanceCents":250,"totalCents":1250,"monthlyChargeLimitEnabled":true,"monthlyChargeLimitCents":5000,"monthlyUsedCents":1234,"currency":"USD"},"rows":[{"label":"Weekly limit","used":80,"limit":100,"resetAt":"2099-07-20T00:00:00.123456Z"},{"label":"5h limit","used":20,"limit":20},{"label":"Daily limit","used":1,"limit":10}]}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task CheckQuotaCarriesTheBearerAuthAndAcceptOnTheRequestData()
    {
        var executor = OkExecutor();
        var checker = CreateChecker(executor);

        await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var request = Assert.Single(executor.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(DefaultUsageUrl, request.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Accept"));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(request.Headers, "Authorization"));
        Assert.NotNull(request.Auth);
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal("test-api-key", request.Auth.ApiKey);
        Assert.Null(request.Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a URL")]
    public async Task CheckQuotaFallsBackToTheDefaultEndpointForUnusableBaseUrls(string baseUrl)
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(baseUrl: baseUrl),
            CancellationToken.None);

        Assert.Equal(DefaultUsageUrl, Assert.Single(executor.Requests).Url);
    }

    // ------------------------------------------------------------------
    // Usage URL building (TestBuildKimiCodeUsageURL)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("", "https://api.kimi.com/coding/v1/usages")]
    [InlineData("https://example.com/kimi", "https://example.com/kimi/v1/usages")]
    [InlineData("https://example.com/kimi/v1/", "https://example.com/kimi/v1/usages")]
    [InlineData("not a URL", "https://api.kimi.com/coding/v1/usages")]
    // Reference behaviour (kimi_code_checker.go:83-102) beyond the reference test.
    [InlineData("   ", "https://api.kimi.com/coding/v1/usages")]
    [InlineData(" https://example.com/kimi ", "https://example.com/kimi/v1/usages")]
    [InlineData("https://example.com/kimi/v1", "https://example.com/kimi/v1/usages")]
    [InlineData("https://example.com/kimi///", "https://example.com/kimi/v1/usages")]
    [InlineData("https://example.com", "https://example.com/v1/usages")]
    [InlineData("https://example.com/", "https://example.com/v1/usages")]
    [InlineData("HTTPS://Example.COM/Kimi/", "https://Example.COM/Kimi/v1/usages")]
    [InlineData("https://example.com/kimi?x=1", "https://example.com/kimi/v1/usages")]
    [InlineData("https://example.com/kimi#frag", "https://example.com/kimi/v1/usages")]
    // Go keeps a '?' for a query that was present but empty (ForceQuery).
    [InlineData("https://example.com/kimi?", "https://example.com/kimi/v1/usages?")]
    [InlineData("https://example.com/kimi?#frag", "https://example.com/kimi/v1/usages?")]
    [InlineData("https://example.com/kimi#frag?", "https://example.com/kimi/v1/usages")]
    [InlineData("https://example.com/kimi??", "https://example.com/kimi/v1/usages")]
    // URL.String() re-escapes the decoded path with encodePath.
    [InlineData("https://example.com/a%2Fb", "https://example.com/a/b/v1/usages")]
    [InlineData("https://example.com/a%3Fb", "https://example.com/a%3Fb/v1/usages")]
    [InlineData("https://example.com/a%3Bb", "https://example.com/a;b/v1/usages")]
    [InlineData("https://example.com/caf%C3%A9", "https://example.com/caf%C3%A9/v1/usages")]
    [InlineData("https://example.com/café", "https://example.com/caf%C3%A9/v1/usages")]
    [InlineData("https://example.com/a b", "https://example.com/a%20b/v1/usages")]
    [InlineData("https://example.com/a%5Cb", "https://example.com/a%5Cb/v1/usages")]
    [InlineData("https://example.com/a!b", "https://example.com/a%21b/v1/usages")]
    // Go re-encodes userinfo; the port preserves the raw authority (C213).
    [InlineData("https://user:pa%41ss@example.com/kimi", "https://user:pa%41ss@example.com/kimi/v1/usages")]
    public void BuildUsageUrlMatchesGoUrlString(string baseUrl, string expected)
    {
        Assert.Equal(expected, KimiCodeQuotaChecker.BuildUsageUrl(baseUrl));
    }

    [Fact]
    public void UsagePathConstantsMatchTheReference()
    {
        Assert.Equal("https://api.kimi.com/coding/v1", KimiCodeQuotaChecker.DefaultBaseUrl);
        Assert.Equal("/usages", KimiCodeQuotaChecker.UsagePath);
    }

    // ------------------------------------------------------------------
    // Credentials
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaTrimsTheLegacyApiKey()
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"  kimi-token  "}"""),
            CancellationToken.None);

        Assert.Equal("kimi-token", Assert.Single(executor.Requests).Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaUsesTheFirstApiKeysEntry()
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(credentials: """{"apiKeys":["  key-one  ","key-two"]}"""),
            CancellationToken.None);

        Assert.Equal("key-one", Assert.Single(executor.Requests).Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaPrefersTheLegacyKeyOverTheArray()
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"legacy-key","apiKeys":["array-key"]}"""),
            CancellationToken.None);

        Assert.Equal("legacy-key", Assert.Single(executor.Requests).Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaFallsBackToTheArrayWhenTheLegacyKeyIsBlank()
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"   ","apiKeys":["array-key"]}"""),
            CancellationToken.None);

        Assert.Equal("array-key", Assert.Single(executor.Requests).Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaOnlyConsultsTheFirstArrayKey()
    {
        // kimi_code_checker.go:52-54 trims APIKeys[0] without skipping blanks,
        // so a blank first entry fails even when a later key is usable.
        var executor = OkExecutor();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateChecker(executor).CheckQuotaAsync(
                Channel(credentials: """{"apiKeys":["   ","key-two"]}"""),
                CancellationToken.None));

        Assert.Equal("channel has no API key", error.Message);
        Assert.Empty(executor.Requests);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"apiKey":"   "}""")]
    [InlineData("""{"apiKeys":[]}""")]
    [InlineData("""{"apiKeys":[""]}""")]
    [InlineData("""{"oauth":{"access_token":"oauth-token"}}""")]
    [InlineData("{not json")]
    public async Task CheckQuotaRejectsChannelsWithoutAUsableKey(string credentials)
    {
        var executor = OkExecutor();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateChecker(executor).CheckQuotaAsync(
                Channel(credentials: credentials),
                CancellationToken.None));

        Assert.Equal("channel has no API key", error.Message);
        Assert.Empty(executor.Requests);
    }

    // ------------------------------------------------------------------
    // Usage row parsing
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(10.0, 4.0, 10L, 4L)]
    [InlineData("10", "4", 10L, 4L)] // JSON strings parsed with ParseFloat
    [InlineData("9.9", 3.9, 9L, 3L)] // float to int64 truncates toward zero
    [InlineData(-2.5, 1.0, -2L, 1L)]
    public void ParseUsageRowReadsLimitAndUsed(
        object limit,
        object used,
        long expectedLimit,
        long expectedUsed)
    {
        var row = KimiCodeQuotaChecker.ParseUsageRow(
            Map(("limit", limit), ("used", used)),
            "fallback");

        Assert.NotNull(row);
        Assert.Equal("fallback", row!.Label);
        Assert.Equal(expectedLimit, row.Limit);
        Assert.Equal(expectedUsed, row.Used);
    }

    [Theory]
    [InlineData(10.0, "3", 10L, 7L)]
    [InlineData(10.0, 4.0, 10L, 6L)]
    [InlineData(-1.0, 0.0, -1L, -1L)]
    public void ParseUsageRowDerivesUsedFromRemaining(
        object limit,
        object remaining,
        long expectedLimit,
        long expectedUsed)
    {
        var row = KimiCodeQuotaChecker.ParseUsageRow(
            Map(("limit", limit), ("remaining", remaining)),
            "fallback");

        Assert.NotNull(row);
        Assert.Equal(expectedLimit, row!.Limit);
        Assert.Equal(expectedUsed, row.Used);
    }

    [Fact]
    public void ParseUsageRowPrefersUsedOverRemaining()
    {
        var row = KimiCodeQuotaChecker.ParseUsageRow(
            Map(("limit", 10.0), ("used", 2.0), ("remaining", 5.0)),
            "fallback");

        Assert.NotNull(row);
        Assert.Equal(2, row!.Used);
    }

    [Theory]
    [InlineData("""{"name":"x"}""")]
    [InlineData("""{}""")]
    [InlineData("""{"limit":"not-a-number","remaining":true}""")]
    public void ParseUsageRowReturnsNullWithoutANumber(string json)
    {
        var map = Assert.IsType<Dictionary<string, object?>>(DecodeAny(json));

        Assert.Null(KimiCodeQuotaChecker.ParseUsageRow(map, "fallback"));
    }

    [Theory]
    [InlineData("N", null, "name")]
    [InlineData(null, "T", "title")]
    [InlineData("", "T", "title")]
    public void ParseUsageRowOnlyUsesNameAndTitleForTheLabel(
        string? name,
        string? title,
        string expectedKey)
    {
        var raw = Map(("used", 1.0));
        if (name is not null)
        {
            raw["name"] = name;
        }

        if (title is not null)
        {
            raw["title"] = title;
        }

        var row = KimiCodeQuotaChecker.ParseUsageRow(raw, "fallback");

        Assert.NotNull(row);
        Assert.Equal((string)raw[expectedKey]!, row!.Label);
    }

    [Fact]
    public void ParseUsageRowIgnoresScopeForTheRowLabel()
    {
        // scope belongs to kimiCodeLimitLabel, not parseKimiCodeUsageRow.
        var row = KimiCodeQuotaChecker.ParseUsageRow(
            Map(("scope", "Scoped"), ("used", 1.0)),
            "fallback");

        Assert.NotNull(row);
        Assert.Equal("fallback", row!.Label);
    }

    [Theory]
    [InlineData("reset_at")]
    [InlineData("resetAt")]
    [InlineData("reset_time")]
    [InlineData("resetTime")]
    public void ResetAtReadsEveryReferenceKey(string key)
    {
        var raw = Map(("used", 1.0));
        raw[key] = "2099-07-20T00:00:00Z";

        Assert.Equal("2099-07-20T00:00:00Z", KimiCodeQuotaChecker.ResetAt(raw));
    }

    [Fact]
    public void ResetAtFollowsTheReferenceKeyPrecedence()
    {
        var raw = Map(
            ("reset_at", "a"),
            ("resetAt", "b"),
            ("reset_time", "c"),
            ("resetTime", "d"));
        Assert.Equal("a", KimiCodeQuotaChecker.ResetAt(raw));

        raw = Map(("resetAt", "b"), ("reset_time", "c"));
        Assert.Equal("b", KimiCodeQuotaChecker.ResetAt(raw));

        raw = Map(("reset_time", "c"), ("resetTime", "d"));
        Assert.Equal("c", KimiCodeQuotaChecker.ResetAt(raw));
    }

    [Fact]
    public void ResetAtSkipsEmptyAndNonStringValues()
    {
        Assert.Null(KimiCodeQuotaChecker.ResetAt(Map(("reset_at", ""))));
        Assert.Null(KimiCodeQuotaChecker.ResetAt(Map(("reset_at", 5.0))));
        Assert.Equal(
            "fallback",
            KimiCodeQuotaChecker.ResetAt(Map(("reset_at", ""), ("resetAt", "fallback"))));
    }

    [Theory]
    [InlineData("reset_in")]
    [InlineData("resetIn")]
    [InlineData("ttl")]
    [InlineData("window")]
    public void ResetAfterSecondsReadsEveryReferenceKey(string key)
    {
        Assert.Equal(60, KimiCodeQuotaChecker.ResetAfterSeconds(Map((key, 60.0))));
        Assert.Equal(60, KimiCodeQuotaChecker.ResetAfterSeconds(Map((key, "60.9"))));
    }

    [Fact]
    public void ResetAfterSecondsSkipsNonPositiveAndInvalidValues()
    {
        Assert.Null(KimiCodeQuotaChecker.ResetAfterSeconds(Map(("reset_in", 0.0))));
        Assert.Null(KimiCodeQuotaChecker.ResetAfterSeconds(Map(("reset_in", -5.0))));
        Assert.Null(KimiCodeQuotaChecker.ResetAfterSeconds(Map(("reset_in", ""))));
        Assert.Null(KimiCodeQuotaChecker.ResetAfterSeconds(Map()));
        Assert.Null(
            KimiCodeQuotaChecker.ResetAfterSeconds(Map(("window", Map(("duration", 1.0))))));
    }

    [Fact]
    public void ResetAfterSecondsFollowsTheReferenceKeyPrecedence()
    {
        var raw = Map(("reset_in", 10.0), ("resetIn", 20.0), ("ttl", 30.0), ("window", 40.0));
        Assert.Equal(10, KimiCodeQuotaChecker.ResetAfterSeconds(raw));

        raw = Map(("reset_in", -1.0), ("resetIn", 20.0), ("ttl", 30.0));
        Assert.Equal(20, KimiCodeQuotaChecker.ResetAfterSeconds(raw));
    }

    // ------------------------------------------------------------------
    // Window length
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(300.0, "MINUTE", 18_000_000_000_000L)] // 5 hours
    [InlineData(90.0, "MINUTE", 5_400_000_000_000L)]
    [InlineData(2.0, "HOUR", 7_200_000_000_000L)]
    [InlineData(1.0, "DAY", 86_400_000_000_000L)]
    [InlineData(7.0, "DAY", 604_800_000_000_000L)]
    [InlineData(45.0, "", 45_000_000_000L)]
    [InlineData(45.0, "minute", 45_000_000_000L)] // matching is case-sensitive
    [InlineData(45.0, "BILLING_MINUTES", 2_700_000_000_000L)]
    public void WindowLengthFollowsTheReferenceUnits(double duration, string unit, long expected)
    {
        var item = Map(("window", Map(("duration", duration), ("timeUnit", unit))));

        Assert.Equal(expected, KimiCodeQuotaChecker.WindowLength(item, item));
    }

    [Fact]
    public void WindowLengthParsesNumericStringDurations()
    {
        var item = Map(("window", Map(("duration", "300"), ("timeUnit", "MINUTE"))));

        Assert.Equal(18_000_000_000_000L, KimiCodeQuotaChecker.WindowLength(item, item));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void WindowLengthRejectsNonPositiveDurations(double duration)
    {
        var item = Map(("window", Map(("duration", duration), ("timeUnit", "MINUTE"))));

        Assert.Equal(0, KimiCodeQuotaChecker.WindowLength(item, item));
    }

    [Fact]
    public void WindowLengthFallsBackToTheItemAndDetailDurations()
    {
        var itemOnly = Map(("duration", 2.0), ("timeUnit", "HOUR"));
        Assert.Equal(
            7_200_000_000_000L,
            KimiCodeQuotaChecker.WindowLength(itemOnly, itemOnly));

        Assert.Equal(
            86_400_000_000_000L,
            KimiCodeQuotaChecker.WindowLength(
                Map(),
                Map(("duration", 1.0), ("timeUnit", "DAY"))));
    }

    [Fact]
    public void WindowLengthPrefersTheWindowObjectDurationAndUnit()
    {
        var item = Map(
            ("window", Map(("duration", 300.0), ("timeUnit", "MINUTE"))),
            ("duration", 5.0),
            ("timeUnit", "HOUR"));
        Assert.Equal(18_000_000_000_000L, KimiCodeQuotaChecker.WindowLength(item, item));

        // window.duration wins even when the unit comes from the item.
        item = Map(
            ("window", Map(("duration", 2.0))),
            ("timeUnit", "HOUR"));
        Assert.Equal(7_200_000_000_000L, KimiCodeQuotaChecker.WindowLength(item, item));
    }

    // ------------------------------------------------------------------
    // Limit labels
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("name", "ItemName")]
    [InlineData("title", "ItemTitle")]
    [InlineData("scope", "ItemScope")]
    public void LimitLabelReadsNameTitleAndScope(string key, string expected)
    {
        var item = Map((key, expected));

        Assert.Equal(expected, KimiCodeQuotaChecker.LimitLabel(item, item, 0));
    }

    [Fact]
    public void LimitLabelPrefersTheDetailNameOverTheItemTitle()
    {
        var item = Map(("title", "ItemTitle"));
        var detail = Map(("name", "DetailName"));

        // The key loop is outermost (kimi_code_checker.go:241-247), so the
        // detail's "name" beats the item's "title".
        Assert.Equal("DetailName", KimiCodeQuotaChecker.LimitLabel(item, detail, 0));
    }

    [Fact]
    public void LimitLabelFallsBackToTheDetail()
    {
        var detail = Map(("name", "DetailName"));

        Assert.Equal("DetailName", KimiCodeQuotaChecker.LimitLabel(Map(), detail, 0));
    }

    [Fact]
    public void LimitLabelSkipsEmptyStrings()
    {
        var item = Map(("name", ""), ("title", "T"));

        Assert.Equal("T", KimiCodeQuotaChecker.LimitLabel(item, item, 0));
    }

    [Theory]
    [InlineData(300.0, "MINUTE", "5h limit")]
    [InlineData(60.0, "MINUTE", "1h limit")]
    [InlineData(90.0, "MINUTE", "90m limit")]
    [InlineData(3.0, "HOUR", "3h limit")]
    [InlineData(2.0, "DAY", "2d limit")]
    [InlineData(45.0, "", "45s limit")]
    [InlineData(45.0, "minute", "45s limit")]
    public void LimitLabelDerivesFromTheWindowDuration(
        double duration,
        string unit,
        string expected)
    {
        var item = Map(("window", Map(("duration", duration), ("timeUnit", unit))));

        Assert.Equal(expected, KimiCodeQuotaChecker.LimitLabel(item, item, 0));
    }

    [Theory]
    [InlineData(0, "Limit #1")]
    [InlineData(1, "Limit #2")]
    [InlineData(9, "Limit #10")]
    public void LimitLabelFallsBackToTheIndex(int index, string expected)
    {
        Assert.Equal(expected, KimiCodeQuotaChecker.LimitLabel(Map(), Map(), index));
        Assert.Equal(expected, KimiCodeQuotaChecker.LimitLabel(null, null, index));
    }

    // ------------------------------------------------------------------
    // Whole-response mapping
    // ------------------------------------------------------------------

    [Fact]
    public void UsageRowFallsBackToTheWeeklyLabelAndWindow()
    {
        var quota = ParseResponse("""{"usage":{"used":1,"limit":2}}""");

        var limit = Assert.Single(quota.Limits);
        Assert.Equal("7d", limit.Window);
        Assert.Equal(0.5, limit.UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Available, limit.Status);

        var row = Assert.IsType<KimiCodeUsageRow>(
            Assert.IsType<List<KimiCodeUsageRow>>(quota.RawData["rows"])[0]);
        Assert.Equal("Weekly limit", row.Label);
    }

    [Fact]
    public void LimitWindowAndRowLabelCanPickDifferentSources()
    {
        // kimiCodeLimitLabel prefers the item's title, but parseKimiCodeUsageRow
        // then overrides the fallback with the detail's own title, and the limit
        // window finally falls back to the row label (kimi_code_checker.go:159-163).
        var quota = ParseResponse(
            """{"limits":[{"title":"ItemTitle","detail":{"title":"DetailTitle","used":1,"limit":2}}]}""");

        Assert.Equal("DetailTitle", Assert.Single(quota.Limits).Window);

        var row = Assert.IsType<List<KimiCodeUsageRow>>(quota.RawData["rows"])[0];
        Assert.Equal("DetailTitle", row.Label);
    }

    [Fact]
    public void ScopeLabelsTheLimitAndTheRowFallback()
    {
        var quota = ParseResponse(
            """{"limits":[{"scope":"Scoped","used":1,"limit":2}]}""");

        Assert.Equal("Scoped", Assert.Single(quota.Limits).Window);
        Assert.Equal(
            "Scoped",
            Assert.IsType<List<KimiCodeUsageRow>>(quota.RawData["rows"])[0].Label);
    }

    [Fact]
    public void WindowDurationLabelsTheLimitAndBecomesTheRowFallback()
    {
        // The row label falls back to kimiCodeLimitLabel, so an unnamed
        // duration window shows "5h limit"; the limit window itself is the
        // normalized "5h".
        var quota = ParseResponse(
            """{"limits":[{"window":{"duration":300,"timeUnit":"MINUTE"},"used":1,"limit":2}]}""");

        Assert.Equal("5h", Assert.Single(quota.Limits).Window);
        Assert.Equal(
            "5h limit",
            Assert.IsType<List<KimiCodeUsageRow>>(quota.RawData["rows"])[0].Label);
    }

    [Fact]
    public void RowsWithoutNumbersAreSkipped()
    {
        var quota = ParseResponse(
            """
            {"usage":{"name":"weekly"},
             "limits":[{"detail":{"name":"no numbers"}},{"detail":{"used":1,"limit":2}}]}
            """);

        Assert.Single(quota.Limits);
        var rows = Assert.IsType<List<KimiCodeUsageRow>>(quota.RawData["rows"]);
        Assert.Single(rows);
        Assert.Equal("Limit #2", rows[0].Label);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"usage":{},"limits":[]}""")]
    [InlineData("""{"limits":[null,{"detail":{"name":"no numbers"}}]}""")]
    public void PayloadWithoutQuotaLimitsThrows(string json)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ParseResponse(json));

        Assert.Equal(
            "kimi code usage response contains no quota limits",
            error.Message);
    }

    [Fact]
    public void UsedOnlyRowProducesAnAvailableLimitWithZeroRatio()
    {
        var quota = ParseResponse("""{"usage":{"used":100}}""");

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(0.0, limit.UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Available, limit.Status);
        Assert.Equal("7d", limit.Window);

        var row = Assert.IsType<List<KimiCodeUsageRow>>(quota.RawData["rows"])[0];
        Assert.Equal(100, row.Used);
        Assert.Equal(0, row.Limit);
    }

    [Fact]
    public void NegativeUsageKeepsTheRawRowButDropsTheLimit()
    {
        var quota = ParseResponse("""{"usage":{"used":-5,"limit":10}}""");

        Assert.Empty(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);

        var row = Assert.IsType<List<KimiCodeUsageRow>>(quota.RawData["rows"])[0];
        Assert.Equal(-5, row.Used);
        Assert.Equal(10, row.Limit);
    }

    [Fact]
    public void UsageRatioOverOneClampsTheLimitButKeepsTheRawRow()
    {
        var quota = ParseResponse("""{"usage":{"used":250,"limit":100}}""");

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(1.0, limit.UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);

        var row = Assert.IsType<List<KimiCodeUsageRow>>(quota.RawData["rows"])[0];
        Assert.Equal(250, row.Used);
    }

    // ------------------------------------------------------------------
    // Reset instants
    // ------------------------------------------------------------------

    [Fact]
    public void ResetAtTakesPrecedenceOverResetAfterSeconds()
    {
        var quota = ParseResponse(
            """{"usage":{"used":1,"limit":2,"resetAt":"2099-07-20T00:00:00Z","reset_in":60}}""");

        Assert.Equal(
            new DateTimeOffset(2099, 7, 20, 0, 0, 0, TimeSpan.Zero),
            quota.NextResetAt);
        Assert.Equal(
            new DateTimeOffset(2099, 7, 20, 0, 0, 0, TimeSpan.Zero),
            Assert.Single(quota.Limits).NextResetAt);
    }

    [Fact]
    public void UnparseableResetAtSuppressesResetAfterSeconds()
    {
        // kimi_code_checker.go:143-149: only the else branch consults
        // resetAfterSeconds, so a present-but-invalid resetAt wins with no
        // reset instant at all.
        var quota = ParseResponse(
            """{"usage":{"used":1,"limit":2,"resetAt":"not-a-time","reset_in":3600}}""");

        Assert.Null(quota.NextResetAt);
        Assert.Null(Assert.Single(quota.Limits).NextResetAt);
    }

    [Fact]
    public void ResetAfterSecondsIsAnchoredAtTheInjectedClock()
    {
        var quota = ParseResponse("""{"usage":{"used":1,"limit":2,"reset_in":3600}}""");

        var expected = Now.AddSeconds(3600);
        Assert.Equal(expected, quota.NextResetAt);
        Assert.Equal(expected, Assert.Single(quota.Limits).NextResetAt);
    }

    [Theory]
    [InlineData("resetIn", 3600.0)]
    [InlineData("ttl", 3600.0)]
    [InlineData("window", 3600.0)]
    [InlineData("reset_in", "3600")]
    public void ResetAfterSecondsAliasesUseTheSameClock(string key, object value)
    {
        var body = "{\"usage\":{\"used\":1,\"limit\":2,\"" + key + "\":"
            + (value is string text ? "\"" + text + "\"" : value.ToString())
            + "}}";

        var quota = ParseResponse(body);

        Assert.Equal(Now.AddSeconds(3600), quota.NextResetAt);
    }

    [Fact]
    public void NextResetAtIsTheEarliestRowReset()
    {
        var quota = ParseResponse(
            """
            {"usage":{"used":0,"limit":1,"resetAt":"2099-07-20T00:00:00Z"},
             "limits":[{"detail":{"used":0,"limit":1,"reset_in":3600}}]}
            """);

        Assert.Equal(Now.AddSeconds(3600), quota.NextResetAt);
        Assert.Equal(
            new DateTimeOffset(2099, 7, 20, 0, 0, 0, TimeSpan.Zero),
            quota.Limits[0].NextResetAt);
        Assert.Equal(Now.AddSeconds(3600), quota.Limits[1].NextResetAt);
    }

    [Fact]
    public void FractionalResetAtKeepsTickPrecision()
    {
        var quota = ParseResponse(
            """{"usage":{"used":1,"limit":2,"resetAt":"2099-07-20T00:00:00.123456789Z"}}""");

        var expected = new DateTimeOffset(2099, 7, 20, 0, 0, 0, TimeSpan.Zero)
            .AddTicks(1_234_567);
        Assert.Equal(expected, quota.NextResetAt);
        Assert.Equal(expected, Assert.Single(quota.Limits).NextResetAt);
    }

    [Fact]
    public void OffsetResetKeepsItsOffset()
    {
        var quota = ParseResponse(
            """{"usage":{"used":1,"limit":2,"resetAt":"2099-07-20T00:00:00+02:00"}}""");

        var expected = new DateTimeOffset(2099, 7, 20, 0, 0, 0, TimeSpan.FromHours(2));
        Assert.Equal(expected, quota.NextResetAt);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(expected, limit.NextResetAt);
        Assert.NotNull(limit.PeriodStart);
        var periodStart = limit.PeriodStart!.Value;
        Assert.Equal(TimeSpan.FromHours(2), periodStart.Offset);
        Assert.Equal(expected.AddDays(-7), periodStart);
    }

    [Fact]
    public void ResetInThePastIsClearedByNormalization()
    {
        var quota = ParseResponse(
            """{"usage":{"used":1,"limit":2,"resetAt":"2020-01-01T00:00:00Z"}}""");

        Assert.Null(quota.NextResetAt);
        var limit = Assert.Single(quota.Limits);
        Assert.Null(limit.NextResetAt);
        Assert.Null(limit.PeriodStart);
    }

    // ------------------------------------------------------------------
    // Status classification
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, ProviderQuotaStatuses.Available)]
    [InlineData(0.7999, ProviderQuotaStatuses.Available)]
    [InlineData(0.8, ProviderQuotaStatuses.Warning)]
    [InlineData(0.999, ProviderQuotaStatuses.Warning)]
    [InlineData(1.0, ProviderQuotaStatuses.Exhausted)]
    [InlineData(1.5, ProviderQuotaStatuses.Exhausted)]
    public void StatusForUsageRatioMatchesTheReferenceThresholds(double ratio, string expected)
    {
        Assert.Equal(expected, KimiCodeQuotaChecker.StatusForUsageRatio(ratio));
    }

    [Theory]
    [InlineData(ProviderQuotaStatuses.Available, ProviderQuotaStatuses.Warning, ProviderQuotaStatuses.Warning)]
    [InlineData(ProviderQuotaStatuses.Warning, ProviderQuotaStatuses.Available, ProviderQuotaStatuses.Warning)]
    [InlineData(ProviderQuotaStatuses.Exhausted, ProviderQuotaStatuses.Warning, ProviderQuotaStatuses.Exhausted)]
    [InlineData(ProviderQuotaStatuses.Warning, ProviderQuotaStatuses.Exhausted, ProviderQuotaStatuses.Exhausted)]
    [InlineData("unknown", ProviderQuotaStatuses.Available, "unknown")]
    public void WorseStatusUsesTheReferenceRankMap(string a, string b, string expected)
    {
        Assert.Equal(expected, KimiCodeQuotaChecker.WorseStatus(a, b));
    }

    [Theory]
    [InlineData(ProviderQuotaStatuses.Available, 0)]
    [InlineData(ProviderQuotaStatuses.Warning, 1)]
    [InlineData(ProviderQuotaStatuses.Exhausted, 2)]
    [InlineData("unknown", 0)]
    [InlineData("", 0)]
    public void CheckerStatusRankMatchesTheLocalMap(string status, int expected)
    {
        Assert.Equal(expected, KimiCodeQuotaChecker.CheckerStatusRank(status));
    }

    [Fact]
    public void WarningRatioMarksTheLimitWarning()
    {
        var quota = ParseResponse("""{"usage":{"used":80,"limit":100}}""");

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(ProviderQuotaStatuses.Warning, Assert.Single(quota.Limits).Status);
    }

    // ------------------------------------------------------------------
    // Booster wallet
    // ------------------------------------------------------------------

    [Fact]
    public void WalletCentsFollowTheReferenceFixedPointConversion()
    {
        var quota = ParseResponse(
            """
            {"usage":{"used":1,"limit":2},
             "boosterWallet":{"balance":{"type":"BOOSTER","amount":2500000,"amountLeft":1500000}}}
            """);

        var wallet = Assert.IsType<KimiCodeBoosterWallet>(quota.RawData["boosterWallet"]);
        Assert.Equal(2, wallet.BalanceCents);
        Assert.Equal(3, wallet.TotalCents);
        Assert.False(wallet.MonthlyChargeLimitEnabled);
        Assert.Equal(0, wallet.MonthlyChargeLimitCents);
        Assert.Equal(0, wallet.MonthlyUsedCents);
        Assert.Equal("USD", wallet.Currency);
    }

    [Theory]
    [InlineData("""{"balance":{"type":"GIFT","amount":1000000}}""")]
    [InlineData("""{"balance":{"type":"BOOSTER"}}""")]
    [InlineData("""{"balance":{"type":"BOOSTER","amount":0}}""")]
    [InlineData("""{"balance":{"type":"BOOSTER","amount":-1}}""")]
    [InlineData("""{"balance":{"type":"BOOSTER","amount":"not-a-number"}}""")]
    [InlineData("""{"balance":[]}""")]
    [InlineData("""{"balance":null}""")]
    public void WalletIsAbsentWithoutABoosterBalance(string walletJson)
    {
        var quota = ParseResponse("{\"usage\":{\"used\":1,\"limit\":2},\"boosterWallet\":" + walletJson + "}");

        Assert.False(quota.RawData.ContainsKey("boosterWallet"));
    }

    [Fact]
    public void WalletAcceptsStringAmounts()
    {
        var quota = ParseResponse(
            """
            {"usage":{"used":1,"limit":2},
             "boosterWallet":{"balance":{"type":"BOOSTER","amount":"1250000000","amountLeft":"250000000"}}}
            """);

        var wallet = Assert.IsType<KimiCodeBoosterWallet>(quota.RawData["boosterWallet"]);
        Assert.Equal(250, wallet.BalanceCents);
        Assert.Equal(1250, wallet.TotalCents);
    }

    [Theory]
    [InlineData("CNY", "USD", "CNY")]
    [InlineData("", "USD", "USD")]
    [InlineData(null, "USD", "USD")]
    [InlineData("", "", "USD")]
    [InlineData(null, null, "USD")]
    public void WalletCurrencyPrefersTheChargeLimitThenTheUsedRecord(
        string? limitCurrency,
        string? usedCurrency,
        string expected)
    {
        var limit = limitCurrency is null
            ? """{"priceInCents":5}"""
            : "{\"priceInCents\":5,\"currency\":\"" + limitCurrency + "\"}";
        var used = usedCurrency is null
            ? """{"priceInCents":1}"""
            : "{\"priceInCents\":1,\"currency\":\"" + usedCurrency + "\"}";
        var body = "{\"usage\":{\"used\":1,\"limit\":2},\"boosterWallet\":{\"balance\":"
            + "{\"type\":\"BOOSTER\",\"amount\":1000000},"
            + "\"monthlyChargeLimit\":" + limit + ",\"monthlyUsed\":" + used + "}}";

        var quota = ParseResponse(body);

        Assert.Equal(
            expected,
            Assert.IsType<KimiCodeBoosterWallet>(quota.RawData["boosterWallet"]).Currency);
    }

    [Fact]
    public void WalletChargeLimitEnabledOnlyAcceptsJsonTrue()
    {
        var quota = ParseResponse(
            """
            {"usage":{"used":1,"limit":2},
             "boosterWallet":{"balance":{"type":"BOOSTER","amount":1000000},
                              "monthlyChargeLimitEnabled":"true"}}
            """);

        Assert.False(
            Assert.IsType<KimiCodeBoosterWallet>(quota.RawData["boosterWallet"])
                .MonthlyChargeLimitEnabled);
    }

    [Fact]
    public void WalletMoneyReadsStringCents()
    {
        var (cents, currency) = KimiCodeQuotaChecker.Money(
            Map(("priceInCents", "1234.9"), ("currency", "EUR")));

        Assert.Equal(1234, cents);
        Assert.Equal("EUR", currency);

        Assert.Equal((0L, string.Empty), KimiCodeQuotaChecker.Money(null));
        Assert.Equal((0L, string.Empty), KimiCodeQuotaChecker.Money(5.0));
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(1L, 1L)]
    [InlineData(999_999L, 1L)]
    [InlineData(1_000_000L, 1L)]
    [InlineData(1_500_000L, 2L)]
    [InlineData(2_500_000L, 3L)]
    [InlineData(250_000_000L, 250L)]
    [InlineData(-1_000_000L, 0L)]
    [InlineData(-2_000_000L, -1L)]
    public void FixedPointToCentsMatchesTheReference(long value, long expected)
    {
        Assert.Equal(expected, KimiCodeQuotaChecker.FixedPointToCents(value));
    }

    // ------------------------------------------------------------------
    // JSON decoding: Go semantics
    // ------------------------------------------------------------------

    [Fact]
    public void ParseUsageResponseTreatsRootNullAsAnEmptyStruct()
    {
        var response = ParseUsageResponse("null");

        Assert.Null(response.Usage);
        Assert.Null(response.Limits);
        Assert.Null(response.BoosterWallet);
    }

    [Theory]
    [InlineData("[]", "array")]
    [InlineData("\"text\"", "string")]
    [InlineData("5", "number")]
    [InlineData("true", "bool")]
    public void ParseUsageResponseRejectsNonObjectRoots(string json, string kind)
    {
        var error = Assert.Throws<JsonException>(() => ParseUsageResponse(json));

        Assert.Equal(
            "json: cannot unmarshal " + kind
            + " into Go value of type provider_quota.kimiCodeUsageResponse",
            error.Message);
    }

    [Theory]
    [InlineData(
        """{"usage":[]}""",
        "json: cannot unmarshal array into Go struct field kimiCodeUsageResponse.usage of type map[string]interface {}")]
    [InlineData(
        """{"limits":{}}""",
        "json: cannot unmarshal object into Go struct field kimiCodeUsageResponse.limits of type []map[string]interface {}")]
    [InlineData(
        """{"limits":[5]}""",
        "json: cannot unmarshal number into Go struct field kimiCodeUsageResponse.limits of type map[string]interface {}")]
    [InlineData(
        """{"boosterWallet":"text"}""",
        "json: cannot unmarshal string into Go struct field kimiCodeUsageResponse.boosterWallet of type map[string]interface {}")]
    [InlineData(
        """{"usage":{"limit":1e400}}""",
        "json: cannot unmarshal number 1e400 into Go struct field kimiCodeUsageResponse.usage of type float64")]
    [InlineData(
        """{"limits":[{"limit":1e400}]}""",
        "json: cannot unmarshal number 1e400 into Go struct field kimiCodeUsageResponse.limits of type float64")]
    public void ParseUsageResponseReportsGoTypeMismatches(string json, string expectedMessage)
    {
        var error = Assert.Throws<JsonException>(() => ParseUsageResponse(json));

        Assert.Equal(expectedMessage, error.Message);
    }

    [Fact]
    public void ParseUsageResponseMergesDuplicateMapFields()
    {
        var response = ParseUsageResponse(
            """{"usage":{"a":1},"USAGE":{"b":2},"boosterWallet":{"x":1},"BOOSTERWALLET":{"y":2}}""");

        Assert.NotNull(response.Usage);
        Assert.Equal(1.0, (double)response.Usage!["a"]!, 9);
        Assert.Equal(2.0, (double)response.Usage["b"]!, 9);
        Assert.NotNull(response.BoosterWallet);
        Assert.Equal(1.0, (double)response.BoosterWallet!["x"]!, 9);
        Assert.Equal(2.0, (double)response.BoosterWallet["y"]!, 9);
    }

    [Fact]
    public void ParseUsageResponseReplacesNestedObjectsInsteadOfMergingThem()
    {
        var response = ParseUsageResponse(
            """{"usage":{"detail":{"x":1}},"usage":{"detail":{"y":2}}}""");

        var detail = Assert.IsType<Dictionary<string, object?>>(response.Usage!["detail"]);
        Assert.False(detail.ContainsKey("x"));
        Assert.Equal(2.0, (double)detail["y"]!, 9);
    }

    [Fact]
    public void ParseUsageResponseMergesLimitsPerIndexAndTruncates()
    {
        var response = ParseUsageResponse(
            """{"limits":[{"used":1,"limit":2},{"used":3,"limit":4}],"limits":[{"limit":10}]}""");

        Assert.NotNull(response.Limits);
        var item = Assert.IsType<Dictionary<string, object?>>(Assert.Single(response.Limits!));
        Assert.Equal(1.0, (double)item["used"]!, 9);
        Assert.Equal(10.0, (double)item["limit"]!, 9);
    }

    [Fact]
    public void ParseUsageResponseResetsLimitsOnExplicitNull()
    {
        var response = ParseUsageResponse("""{"limits":[{"used":1}],"limits":null}""");

        Assert.Null(response.Limits);
    }

    [Fact]
    public void ParseUsageResponseKeepsNullLimitElements()
    {
        var response = ParseUsageResponse("""{"limits":[null,{"used":1,"limit":2}]}""");

        Assert.NotNull(response.Limits);
        Assert.Equal(2, response.Limits!.Count);
        Assert.Null(response.Limits[0]);
        Assert.NotNull(response.Limits[1]);
    }

    [Fact]
    public void ParseUsageResponseIgnoresUnknownFields()
    {
        var response = ParseUsageResponse(
            """{"unknown":true,"usage":{"extra":1,"used":2},"another":[1,2]}""");

        Assert.NotNull(response.Usage);
        Assert.Equal(2.0, (double)response.Usage!["used"]!, 9);
        Assert.Equal(1.0, (double)response.Usage["extra"]!, 9);
    }

    [Fact]
    public void ParseUsageResponseReplacesInvalidUtf8LikeGo()
    {
        var prefix = Encoding.UTF8.GetBytes("""{"usage":{"name":"ok""");
        var suffix = Encoding.UTF8.GetBytes("\",\"used\":1}}");
        byte[] body = [.. prefix, 0xFF, .. suffix];

        var response = KimiCodeQuotaChecker.ParseUsageResponse(body);

        Assert.Equal(
            "ok" + char.ConvertFromUtf32(0xFFFD),
            response.Usage!["name"]);
    }

    // ------------------------------------------------------------------
    // Numeric coercion
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(3.9, true, 3L)]
    [InlineData(-3.9, true, -3L)]
    [InlineData("42", true, 42L)]
    [InlineData("4.9", true, 4L)]
    [InlineData("1e3", true, 1000L)]
    [InlineData("abc", false, 0L)]
    [InlineData("", false, 0L)]
    [InlineData(true, false, 0L)]
    [InlineData(null, false, 0L)]
    public void KimiCodeIntMatchesTheReferenceTypeSwitch(object? value, bool ok, long expected)
    {
        Assert.Equal(ok, KimiCodeQuotaChecker.KimiCodeInt(value, out var result));
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1.9, 1L)]
    [InlineData(-1.9, -1L)]
    [InlineData(0.0, 0L)]
    [InlineData(-9223372036854775808.0, long.MinValue)]
    [InlineData(9223372036854775808.0, long.MinValue)]
    [InlineData(double.NaN, long.MinValue)]
    [InlineData(double.PositiveInfinity, long.MinValue)]
    public void GoFloatToInt64MatchesGoOnAmd64(double value, long expected)
    {
        Assert.Equal(expected, KimiCodeQuotaChecker.GoFloatToInt64(value));
    }

    [Fact]
    public void FirstHelpersReturnTheFirstMatchingType()
    {
        Assert.Equal(
            (true, 0L),
            KimiCodeQuotaChecker.FirstKimiCodeInt(null, "0", 5.0));
        Assert.Equal(
            (false, 0L),
            KimiCodeQuotaChecker.FirstKimiCodeInt(null, true));

        Assert.Equal(
            (true, (string?)string.Empty),
            KimiCodeQuotaChecker.FirstKimiCodeString(null, "", 5.0));
        Assert.Equal(
            (false, (string?)null),
            KimiCodeQuotaChecker.FirstKimiCodeString(null, 5.0));
    }

    // ------------------------------------------------------------------
    // Errors
    // ------------------------------------------------------------------

    [Fact]
    public async Task MalformedJsonReportsTheParsePrefix()
    {
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok("not json") });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.StartsWith("failed to parse kimi code usage response: ", error.Message);
    }

    [Fact]
    public async Task EmptyBodyReportsTheParsePrefix()
    {
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok("") });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.StartsWith("failed to parse kimi code usage response: ", error.Message);
    }

    [Fact]
    public async Task TypeMismatchesReportTheParsePrefixAndGoMessage()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok("""{"limits":[{"limit":1e400}]}"""),
        });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "failed to parse kimi code usage response: "
            + "json: cannot unmarshal number 1e400 into Go struct field "
            + "kimiCodeUsageResponse.limits of type float64",
            error.Message);
    }

    [Theory]
    [InlineData(401, "401 Unauthorized")]
    [InlineData(403, "403 Forbidden")]
    [InlineData(429, "429 Too Many Requests")]
    [InlineData(500, "500 Internal Server Error")]
    public async Task ExecutorStatusFailuresAreWrapped(int statusCode, string status)
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new HttpClientError(
                "GET",
                DefaultUsageUrl,
                statusCode,
                status,
                Encoding.UTF8.GetBytes("""{"error":"nope"}""")),
        });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.StartsWith("kimi code usage request failed: ", error.Message);
        Assert.Contains(statusCode.ToString(CultureInfo.InvariantCulture), error.Message);
    }

    [Fact]
    public async Task TransportFailuresAreWrapped()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new IOException("connection reset"),
        });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("kimi code usage request failed: connection reset", error.Message);
    }

    [Theory]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(204)]
    public async Task NonErrorStatusesBelowTheExecutorThresholdAreParsed(int statusCode)
    {
        // The reference performs no explicit status check: the HTTP client
        // rejects >=400 and every other status is parsed.
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = statusCode,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
                Body = Encoding.UTF8.GetBytes("""{"usage":{"used":1,"limit":2}}"""),
            },
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Single(quota.Limits);
    }

    // ------------------------------------------------------------------
    // Proxy routing
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaRoutesThroughTheChannelProxyExecutor()
    {
        var direct = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new InvalidOperationException("direct executor used"),
        };
        var proxied = OkExecutor();
        var factory = new ScriptedProxyExecutorFactory { Executor = proxied };
        var checker = new KimiCodeQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(
            Channel(settings: """{"proxy":{"type":"url","url":"http://proxy.local:8080","username":"user","password":"pass","disableConnectionReuse":true}}"""),
            CancellationToken.None);

        var proxy = Assert.Single(factory.Requests);
        Assert.Equal("url", proxy.Type);
        Assert.Equal("http://proxy.local:8080", proxy.Url);
        Assert.Equal("user", proxy.Username);
        Assert.Equal("pass", proxy.Password);
        Assert.True(proxy.DisableConnectionReuse);
        Assert.Single(proxied.Requests);
        Assert.Empty(direct.Requests);
    }

    [Fact]
    public async Task CheckQuotaUsesTheDirectExecutorForMalformedSettings()
    {
        var direct = OkExecutor();
        var factory = new ScriptedProxyExecutorFactory();
        var checker = new KimiCodeQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(Channel(settings: "{\"proxy\":{"), CancellationToken.None);

        Assert.Empty(factory.Requests);
        Assert.Single(direct.Requests);
    }

    [Fact]
    public async Task CheckQuotaUsesTheDirectExecutorWithoutASettingsProxy()
    {
        var direct = OkExecutor();
        var factory = new ScriptedProxyExecutorFactory();
        var checker = new KimiCodeQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Empty(factory.Requests);
        Assert.Single(direct.Requests);
    }

    // ------------------------------------------------------------------
    // Capability
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("moonshot_coding", true)]
    [InlineData("moonshot", false)]
    [InlineData("openai", false)]
    [InlineData("anthropic", false)]
    [InlineData("opencode_go", false)]
    [InlineData("", false)]
    public void SupportsChannelOnlyMatchesMoonshotCoding(string channelType, bool expected)
    {
        var checker = CreateChecker(new ScriptedExecutor());

        Assert.Equal(expected, checker.SupportsChannel(new ChannelRecord { Type = channelType }));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static KimiCodeQuotaChecker CreateChecker(
        out ClaudeCodeQuotaCheckerTests.RecordingHttpHandler handler)
    {
        handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new KimiCodeQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now));
    }

    private static KimiCodeQuotaChecker CreateChecker(ScriptedExecutor executor)
        => new(executor, new ScriptedProxyExecutorFactory(), new AntigravityTestClock(Now));

    private static ChannelRecord Channel(
        string? credentials = null,
        string settings = "{}",
        string type = "moonshot_coding",
        string? baseUrl = DefaultBaseUrl)
        => new()
        {
            Type = type,
            BaseUrl = baseUrl,
            Credentials = credentials ?? DefaultCredentials,
            Settings = settings,
        };

    private static QuotaData ParseResponse(string json)
        => KimiCodeQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(json), Now);

    private static KimiCodeUsageResponse ParseUsageResponse(string json)
        => KimiCodeQuotaChecker.ParseUsageResponse(Encoding.UTF8.GetBytes(json));

    /// <summary>Decodes a JSON value with the checker's Go-compatible decoder.</summary>
    private static object? DecodeAny(string json)
    {
        // Wrap the value in a field the checker understands and read it back.
        var response = ParseUsageResponse("{\"usage\":{\"value\":" + json + "}}");
        return response.Usage!["value"];
    }

    private static Dictionary<string, object?> Map(params (string Key, object? Value)[] entries)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            map[key] = value;
        }

        return map;
    }

    private static ScriptedExecutor OkExecutor(string body = DefaultSuccessfulBody) => new()
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

    private const string DefaultSuccessfulBody = """{"usage":{"used":1,"limit":2}}""";
}
