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
/// Ports internal/server/biz/provider_quota/nanogpt_checker.go together with
/// the nanogpt fixture of provider_contract_fixtures_test.go: the
/// subscription usage request derived from the channel base URL, the
/// API-key precedence (trimmed legacy field, then the first apiKeys entry),
/// the daily-image and daily/weekly-token windows, the subscription-state
/// escalation and the graceUntil fallback reset.
/// </summary>
public sealed class NanogptQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private const string QuotaUrl = "https://nano-gpt.com/api/subscription/v1/usage";

    private const string ApiKey = "test-api-key";

    private const string FullBody = """
        {"active":true,"provider":"nano-gpt","providerStatus":"active","limits":{"weeklyInputTokens":1000000,"dailyInputTokens":200000,"dailyImages":50},"allowOverage":false,"period":{"currentPeriodEnd":"2099-05-01T00:00:00Z"},"dailyImages":{"used":5,"remaining":45,"percentUsed":0.1,"resetAt":4102444800000},"dailyInputTokens":{"used":1000,"remaining":199000,"percentUsed":0.005,"resetAt":4102444800000},"weeklyInputTokens":{"used":20000,"remaining":980000,"percentUsed":0.02,"resetAt":4103049600000},"state":"active","graceUntil":"2099-05-01T00:00:00Z"}
        """;

    private const string ContractFixtureBody = """
        {"dailyImages":{"used":1,"remaining":9,"percentUsed":0.1,"resetAt":4102444800000},"dailyInputTokens":{"used":10,"remaining":90,"percentUsed":0.1,"resetAt":4102444800000},"weeklyInputTokens":{"used":20,"remaining":80,"percentUsed":0.2,"resetAt":4103049600000}}
        """;

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaFetchesTheUsagePayloadAndMapsTheWindows()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(FullBody);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(QuotaUrl, request.Url);
        Assert.Equal("Bearer " + ApiKey, request.Header("Authorization"));
        Assert.Equal("application/json", request.Header("Content-Type"));
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Equal(string.Empty, request.Body);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal("nanogpt", quota.ProviderType);
        Assert.True(quota.Ready);
        Assert.Equal(3, quota.Limits.Count);

        // The reference emits the image window first, then the two token
        // windows, each with its own label and length.
        Assert.Equal(QuotaLimitTypes.Image, quota.Limits[0].Type);
        Assert.Equal(QuotaWindows.Daily, quota.Limits[0].Window);
        Assert.Equal(0.1, quota.Limits[0].UsageRatio, 9);
        Assert.Equal(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), quota.Limits[0].NextResetAt);
        Assert.Equal(new DateTimeOffset(2099, 12, 31, 0, 0, 0, TimeSpan.Zero), quota.Limits[0].PeriodStart);

        Assert.Equal(QuotaLimitTypes.Token, quota.Limits[1].Type);
        Assert.Equal(QuotaWindows.Daily, quota.Limits[1].Window);
        Assert.Equal(0.005, quota.Limits[1].UsageRatio, 9);
        Assert.Equal(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), quota.Limits[1].NextResetAt);
        Assert.Equal(new DateTimeOffset(2099, 12, 31, 0, 0, 0, TimeSpan.Zero), quota.Limits[1].PeriodStart);

        Assert.Equal(QuotaLimitTypes.Token, quota.Limits[2].Type);
        Assert.Equal(QuotaWindows.Weekly, quota.Limits[2].Window);
        Assert.Equal(0.02, quota.Limits[2].UsageRatio, 9);
        Assert.Equal(new DateTimeOffset(2100, 1, 8, 0, 0, 0, TimeSpan.Zero), quota.Limits[2].NextResetAt);
        Assert.Equal(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), quota.Limits[2].PeriodStart);

        // The earliest window reset wins over graceUntil.
        Assert.Equal(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), quota.NextResetAt);

        Assert.Equal(
            """{"active":true,"allowOverage":false,"graceUntil":"2099-05-01T00:00:00Z","limits":{"dailyImages":50,"dailyInputTokens":200000,"weeklyInputTokens":1000000},"period":{"currentPeriodEnd":"2099-05-01T00:00:00Z"},"provider":"nano-gpt","providerStatus":"active","state":"active","windows":{"dailyImages":{"percentUsed":0.1,"remaining":45,"resetAt":4102444800000,"used":5},"dailyInputTokens":{"percentUsed":0.005,"remaining":199000,"resetAt":4102444800000,"used":1000},"weeklyInputTokens":{"percentUsed":0.02,"remaining":980000,"resetAt":4103049600000,"used":20000}}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task CheckQuotaCarriesTheBearerAuthAndContentTypeOnTheRequestData()
    {
        var executor = OkExecutor();
        var checker = CreateChecker(executor);

        await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var request = Assert.Single(executor.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(QuotaUrl, request.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));

        // WithBearerToken sets the request auth; the executor writes the
        // Authorization header, so the request data itself has none.
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(request.Headers, "Authorization"));
        Assert.NotNull(request.Auth);
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal(ApiKey, request.Auth.ApiKey);
        Assert.Null(request.Body);
    }

    [Theory]
    [InlineData("", "https://nano-gpt.com/api/subscription/v1/usage")]
    [InlineData("   ", "https://nano-gpt.com/api/subscription/v1/usage")]
    [InlineData("  https://api.nano-gpt.com  ", "https://api.nano-gpt.com/api/subscription/v1/usage")]
    [InlineData("https://api.nano-gpt.com", "https://api.nano-gpt.com/api/subscription/v1/usage")]
    [InlineData("https://api.nano-gpt.com/v1/chat", "https://api.nano-gpt.com/api/subscription/v1/usage")]
    [InlineData("https://api.nano-gpt.com/", "https://api.nano-gpt.com/api/subscription/v1/usage")]
    [InlineData("http://api.nano-gpt.com/v1", "https://api.nano-gpt.com/api/subscription/v1/usage")]
    [InlineData("HTTP://API.NANO-GPT.COM/V1", "https://API.NANO-GPT.COM/api/subscription/v1/usage")]
    [InlineData("https://user:pass@api.nano-gpt.com:8443/v1", "https://api.nano-gpt.com:8443/api/subscription/v1/usage")]
    [InlineData("https://api.nano-gpt.com:443", "https://api.nano-gpt.com:443/api/subscription/v1/usage")]
    [InlineData("https://[::1]:8080/v1", "https://[::1]:8080/api/subscription/v1/usage")]
    [InlineData("ftp://host/v1", "ftp://host/api/subscription/v1/usage")]
    [InlineData("//api.nano-gpt.com", "://api.nano-gpt.com/api/subscription/v1/usage")]
    [InlineData("mailto:foo@bar", "mailto:///api/subscription/v1/usage")]
    [InlineData("https:", "https:///api/subscription/v1/usage")]
    [InlineData("://invalid", "https://nano-gpt.com/api/subscription/v1/usage")]
    [InlineData("api.nano-gpt.com", "https://nano-gpt.com/api/subscription/v1/usage")]
    [InlineData("https://api%2Enano-gpt.com", "https://nano-gpt.com/api/subscription/v1/usage")]
    [InlineData("https://api nano-gpt.com", "https://nano-gpt.com/api/subscription/v1/usage")]
    [InlineData("https://api.nano-gpt.com:abc", "https://nano-gpt.com/api/subscription/v1/usage")]
    [InlineData("https://api.nano-gpt.com/%zz", "https://nano-gpt.com/api/subscription/v1/usage")]
    public void BuildQuotaUrlMatchesGoUrlParse(string baseUrl, string expected)
    {
        Assert.Equal(expected, NanogptQuotaChecker.BuildQuotaUrl(baseUrl));
    }

    [Fact]
    public async Task CheckQuotaWrapsTransportErrors()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new InvalidOperationException("boom"),
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("quota request failed: boom", error.Message);
    }

    [Fact]
    public async Task CheckQuotaWrapsHttpStatusFailures()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "quota request failed: GET - " + QuotaUrl + " with status 401 Unauthorized",
            error.Message);
    }

    [Fact]
    public async Task CheckQuotaParsesBodiesBelowTheErrorStatusRange()
    {
        // The reference performs no status check of its own; the executor only
        // rejects >= 400, so a 3xx body is still parsed.
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => new HttpResponseMessage(HttpStatusCode.Found)
        {
            Content = new StringContent(
                """{"state":"active","dailyInputTokens":{"remaining":50}}""",
                Encoding.UTF8,
                "application/json"),
        };

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Single(quota.Limits);
    }

    // ------------------------------------------------------------------
    // Credentials
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaTrimsTheLegacyApiKey()
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"  test-api-key  "}"""),
            CancellationToken.None);

        Assert.Equal(ApiKey, Assert.Single(executor.Requests).Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaFallsBackToTheFirstApiKeysEntryVerbatim()
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(credentials: """{"apiKeys":["  fallback-key  ","second-key"]}"""),
            CancellationToken.None);

        Assert.Equal("  fallback-key  ", Assert.Single(executor.Requests).Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaPrefersTheLegacyApiKeyOverTheArray()
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"legacy-key","apiKeys":["fallback-key"]}"""),
            CancellationToken.None);

        Assert.Equal("legacy-key", Assert.Single(executor.Requests).Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaUsesTheArrayWhenTheLegacyKeyIsOnlyWhitespace()
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"   ","apiKeys":["fallback-key"]}"""),
            CancellationToken.None);

        Assert.Equal("fallback-key", Assert.Single(executor.Requests).Auth?.ApiKey);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"apiKey":"   "}""")]
    [InlineData("""{"apiKeys":[]}""")]
    [InlineData("""{"apiKeys":[""]}""")]
    [InlineData("""{"apiKey":"   ","apiKeys":[]}""")]
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
    // Contract fixture and windows
    // ------------------------------------------------------------------

    [Fact]
    public void ParseResponseMatchesTheSharedContractFixture()
    {
        var quota = Parse(ContractFixtureBody);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal("nanogpt", quota.ProviderType);
        Assert.True(quota.Ready);
        Assert.Equal(3, quota.Limits.Count);

        Assert.Equal(QuotaLimitTypes.Image, quota.Limits[0].Type);
        Assert.Equal(QuotaWindows.Daily, quota.Limits[0].Window);
        Assert.Equal(0.1, quota.Limits[0].UsageRatio, 9);
        Assert.Equal(QuotaLimitTypes.Token, quota.Limits[1].Type);
        Assert.Equal(QuotaWindows.Daily, quota.Limits[1].Window);
        Assert.Equal(0.1, quota.Limits[1].UsageRatio, 9);
        Assert.Equal(QuotaLimitTypes.Token, quota.Limits[2].Type);
        Assert.Equal(QuotaWindows.Weekly, quota.Limits[2].Window);
        Assert.Equal(0.2, quota.Limits[2].UsageRatio, 9);

        Assert.Equal(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), quota.NextResetAt);
        Assert.Equal(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), quota.Limits[2].PeriodStart);

        Assert.Equal(
            """{"windows":{"dailyImages":{"percentUsed":0.1,"remaining":9,"resetAt":4102444800000,"used":1},"dailyInputTokens":{"percentUsed":0.1,"remaining":90,"resetAt":4102444800000,"used":10},"weeklyInputTokens":{"percentUsed":0.2,"remaining":80,"resetAt":4103049600000,"used":20}}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Theory]
    [InlineData("0", ProviderQuotaStatuses.Available, 0.0)]
    [InlineData("0.25", ProviderQuotaStatuses.Available, 0.25)]
    [InlineData("0.799999", ProviderQuotaStatuses.Available, 0.799999)]
    [InlineData("0.8", ProviderQuotaStatuses.Warning, 0.8)]
    [InlineData("0.99", ProviderQuotaStatuses.Warning, 0.99)]
    [InlineData("1", ProviderQuotaStatuses.Exhausted, 1.0)]
    [InlineData("1.5", ProviderQuotaStatuses.Exhausted, 1.0)]
    public void ParseResponseMapsPercentUsedToTheLimitStatus(
        string percentUsed,
        string expectedStatus,
        double expectedRatio)
    {
        var quota = Parse(
            "{\"state\":\"active\",\"dailyInputTokens\":{\"remaining\":50,\"percentUsed\":"
            + percentUsed
            + "}}");

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(expectedStatus, limit.Status);
        Assert.Equal(expectedRatio, limit.UsageRatio, 9);
        Assert.Equal(expectedStatus, quota.Status);
        Assert.Equal(ProviderQuotaStatuses.IsReadyStatus(expectedStatus), quota.Ready);
    }

    [Theory]
    [InlineData("""{"remaining":0,"percentUsed":0.1}""", ProviderQuotaStatuses.Exhausted, 1.0)]
    [InlineData("""{"remaining":-5}""", ProviderQuotaStatuses.Exhausted, 1.0)]
    [InlineData("""{"remaining":0,"percentUsed":null}""", ProviderQuotaStatuses.Exhausted, 1.0)]
    [InlineData("""{"percentUsed":0.1}""", ProviderQuotaStatuses.Available, 0.1)]
    [InlineData("""{"remaining":5}""", ProviderQuotaStatuses.Available, 0.0)]
    public void ParseResponseLetsANonPositiveRemainingExhaustTheWindow(
        string window,
        string expectedStatus,
        double expectedRatio)
    {
        var quota = Parse("{\"state\":\"active\",\"dailyInputTokens\":" + window + "}");

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(expectedStatus, limit.Status);
        Assert.Equal(expectedRatio, limit.UsageRatio, 9);
    }

    [Theory]
    [InlineData("""{"state":"active"}""", ProviderQuotaStatuses.Available, true)]
    [InlineData("""{"state":"grace"}""", ProviderQuotaStatuses.Warning, true)]
    [InlineData("""{"state":"inactive"}""", ProviderQuotaStatuses.Exhausted, false)]
    [InlineData("""{"state":"suspended"}""", ProviderQuotaStatuses.Unknown, false)]
    [InlineData("""{}""", ProviderQuotaStatuses.Unknown, false)]
    public void ParseResponseMapsTheSubscriptionStateToTheBaseStatus(
        string body,
        string expectedStatus,
        bool expectedReady)
    {
        var quota = Parse(body);

        Assert.Equal(expectedStatus, quota.Status);
        Assert.Equal(expectedReady, quota.Ready);
    }

    [Theory]
    [InlineData("""{"state":"active","weeklyInputTokens":{"percentUsed":0.85}}""", ProviderQuotaStatuses.Warning)]
    [InlineData("""{"state":"active","dailyImages":{"remaining":0}}""", ProviderQuotaStatuses.Exhausted)]
    [InlineData("""{"state":"grace","dailyInputTokens":{"percentUsed":0.1}}""", ProviderQuotaStatuses.Warning)]
    [InlineData("""{"state":"inactive","dailyInputTokens":{"percentUsed":0.1}}""", ProviderQuotaStatuses.Exhausted)]
    [InlineData("""{"state":"suspended","dailyInputTokens":{"percentUsed":0.1}}""", ProviderQuotaStatuses.Available)]
    [InlineData("""{"state":"suspended","dailyImages":{"remaining":0}}""", ProviderQuotaStatuses.Exhausted)]
    public void ParseResponseAppliesTheStateThenEscalatesFromTheWindows(
        string body,
        string expectedStatus)
    {
        Assert.Equal(expectedStatus, Parse(body).Status);
    }

    [Fact]
    public void ParseResponseEmitsTheWindowsInImageDailyWeeklyOrder()
    {
        var quota = Parse("""
            {"weeklyInputTokens":{"percentUsed":0.2},"dailyInputTokens":{"percentUsed":0.1},"dailyImages":{"percentUsed":0.05}}
            """);

        Assert.Equal(3, quota.Limits.Count);
        Assert.Equal(QuotaLimitTypes.Image, quota.Limits[0].Type);
        Assert.Equal(QuotaWindows.Daily, quota.Limits[0].Window);
        Assert.Equal(QuotaLimitTypes.Token, quota.Limits[1].Type);
        Assert.Equal(QuotaWindows.Daily, quota.Limits[1].Window);
        Assert.Equal(QuotaLimitTypes.Token, quota.Limits[2].Type);
        Assert.Equal(QuotaWindows.Weekly, quota.Limits[2].Window);
    }

    [Fact]
    public void ParseResponseDropsANegativeUsageRatioFromTheLimitsButKeepsTheState()
    {
        // normalizeQuotaLimits rejects a negative ratio; the state was already
        // computed from the raw window (available).
        var quota = Parse("""{"state":"active","dailyInputTokens":{"percentUsed":-0.1}}""");

        Assert.Empty(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
    }

    // ------------------------------------------------------------------
    // Reset selection
    // ------------------------------------------------------------------

    [Fact]
    public void ParseResponseUsesTheEarliestWindowReset()
    {
        var quota = Parse("""
            {"weeklyInputTokens":{"remaining":50,"resetAt":4103049600000},"dailyInputTokens":{"remaining":50,"resetAt":4102444800000}}
            """);

        Assert.Equal(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), quota.NextResetAt);
    }

    [Fact]
    public void ParseResponseIgnoresNonPositiveResetsWhenChoosingTheEarliest()
    {
        var quota = Parse("""
            {"dailyInputTokens":{"remaining":50,"resetAt":0},"weeklyInputTokens":{"remaining":50,"resetAt":-1}}
            """);

        Assert.Null(quota.NextResetAt);
        Assert.All(quota.Limits, limit => Assert.Null(limit.NextResetAt));
    }

    [Fact]
    public void ParseResponseClearsResetsThatAreAlreadyPast()
    {
        // 2020-01-01T00:00:00Z; normalization drops a reset that is not in the
        // future, but rawData keeps the provider value.
        var quota = Parse("""{"dailyInputTokens":{"remaining":50,"resetAt":1577836800000}}""");

        Assert.Null(quota.NextResetAt);
        Assert.Null(quota.Limits[0].NextResetAt);
        Assert.Null(quota.Limits[0].PeriodStart);
        Assert.Equal(
            """{"windows":{"dailyInputTokens":{"remaining":50,"resetAt":1577836800000}}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Theory]
    [InlineData("""{"graceUntil":"2099-04-30T00:00:00Z"}""", "2099-04-30T00:00:00Z", 0)]
    [InlineData("""{"graceUntil":"2099-04-30T08:00:00+08:00"}""", "2099-04-30T00:00:00Z", 8)]
    [InlineData("""{"graceUntil":"2099-04-30T0:00:00Z"}""", "2099-04-30T00:00:00Z", 0)]
    [InlineData("""{"graceUntil":"2099-04-30T00:00:00.5Z"}""", "2099-04-30T00:00:00.5Z", 0)]
    public void ParseResponseFallsBackToGraceUntilWhenNoWindowResets(
        string body,
        string expected,
        double expectedOffsetHours)
    {
        var quota = Parse(body);

        Assert.NotNull(quota.NextResetAt);
        Assert.Equal(expectedOffsetHours, quota.NextResetAt.Value.Offset.TotalHours);
        Assert.Equal(
            DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            quota.NextResetAt.Value);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"graceUntil":""}""")]
    [InlineData("""{"graceUntil":"not-a-date"}""")]
    [InlineData("""{"graceUntil":"2099-04-30"}""")]
    public void ParseResponseRejectsAnInvalidGraceUntil(string body)
    {
        Assert.Null(Parse(body).NextResetAt);
    }

    [Fact]
    public void ParseResponsePrefersAWindowResetOverGraceUntil()
    {
        var quota = Parse("""
            {"dailyInputTokens":{"remaining":50,"resetAt":4102444800000},"graceUntil":"2099-04-30T00:00:00Z"}
            """);

        Assert.Equal(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), quota.NextResetAt);
    }

    // ------------------------------------------------------------------
    // Decoding
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("[]", "json: cannot unmarshal array into Go value of type provider_quota.NanoGPTUsageResponse")]
    [InlineData("\"text\"", "json: cannot unmarshal string into Go value of type provider_quota.NanoGPTUsageResponse")]
    [InlineData("""{"active":"yes"}""", "json: cannot unmarshal string into Go struct field NanoGPTUsageResponse.active of type bool")]
    [InlineData("""{"provider":1}""", "json: cannot unmarshal number into Go struct field NanoGPTUsageResponse.provider of type string")]
    [InlineData("""{"limits":[]}""", "json: cannot unmarshal array into Go struct field NanoGPTUsageResponse.limits of type provider_quota.NanoGPTLimits")]
    [InlineData("""{"limits":{"dailyImages":"x"}}""", "json: cannot unmarshal string into Go struct field NanoGPTLimits.limits.dailyImages of type int64")]
    [InlineData("""{"dailyImages":[]}""", "json: cannot unmarshal array into Go struct field NanoGPTUsageResponse.dailyImages of type provider_quota.NanoGPTQuotaWindow")]
    [InlineData("""{"dailyImages":{"used":1.5}}""", "json: cannot unmarshal number 1.5 into Go struct field NanoGPTQuotaWindow.dailyImages.used of type int64")]
    [InlineData("""{"dailyImages":{"used":1e3}}""", "json: cannot unmarshal number 1e3 into Go struct field NanoGPTQuotaWindow.dailyImages.used of type int64")]
    [InlineData("""{"dailyImages":{"used":99999999999999999999}}""", "json: cannot unmarshal number 99999999999999999999 into Go struct field NanoGPTQuotaWindow.dailyImages.used of type int64")]
    [InlineData("""{"dailyImages":{"remaining":true}}""", "json: cannot unmarshal bool into Go struct field NanoGPTQuotaWindow.dailyImages.remaining of type int64")]
    [InlineData("""{"dailyImages":{"percentUsed":"x"}}""", "json: cannot unmarshal string into Go struct field NanoGPTQuotaWindow.dailyImages.percentUsed of type float64")]
    [InlineData("""{"dailyImages":{"percentUsed":1e400}}""", "json: cannot unmarshal number 1e400 into Go struct field NanoGPTQuotaWindow.dailyImages.percentUsed of type float64")]
    [InlineData("""{"dailyImages":{"resetAt":{}}}""", "json: cannot unmarshal object into Go struct field NanoGPTQuotaWindow.dailyImages.resetAt of type int64")]
    [InlineData("""{"period":{"currentPeriodEnd":1}}""", "json: cannot unmarshal number into Go struct field NanoGPTPeriod.period.currentPeriodEnd of type string")]
    [InlineData("""{"period":[]}""", "json: cannot unmarshal array into Go struct field NanoGPTUsageResponse.period of type provider_quota.NanoGPTPeriod")]
    public void ParseResponseUsesTheGoDecoderErrors(string body, string expectedMessage)
    {
        var error = Assert.Throws<WrappedException>(() => Parse(body));

        Assert.Equal(
            "failed to parse nanogpt usage response: " + expectedMessage,
            error.Message);
    }

    [Fact]
    public void ParseResponseWrapsMalformedJson()
    {
        var error = Assert.Throws<WrappedException>(() => Parse("{"));

        Assert.StartsWith("failed to parse nanogpt usage response: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseUsageResponseTreatsJsonNullAsTheZeroPayload()
    {
        var response = ParseUsageResponse("null");

        Assert.Null(response.Active);
        Assert.Null(response.Provider);
        Assert.Null(response.State);
        Assert.Null(response.Limits);
        Assert.Null(response.DailyImages);
    }

    [Fact]
    public void ParseUsageResponseMergesRepeatedObjectsAndKeepsTheLastScalar()
    {
        var response = ParseUsageResponse("""
            {"limits":{"dailyImages":5},"Limits":{"weeklyInputTokens":7},"state":"active","state":null,"DAILYINPUTTOKENS":{"PercentUsed":0.5}}
            """);

        Assert.NotNull(response.Limits);
        Assert.Equal(5L, response.Limits.DailyImages);
        Assert.Equal(7L, response.Limits.WeeklyInputTokens);
        Assert.Null(response.State);
        Assert.NotNull(response.DailyInputTokens);
        Assert.Equal(0.5, response.DailyInputTokens.PercentUsed);
    }

    [Fact]
    public void ParseUsageResponseResetsARepeatedObjectWithNull()
    {
        var response = ParseUsageResponse("""{"dailyImages":{"used":1},"dailyImages":null}""");

        Assert.Null(response.DailyImages);
    }

    [Fact]
    public void ParseResponseSerializesEmptyNestedObjects()
    {
        var quota = Parse("""{"limits":{},"period":{}}""");

        Assert.Empty(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.Equal("""{"limits":{},"period":{}}""", GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public void ParseResponseOmitsAbsentFieldsAndWindows()
    {
        var quota = Parse("""{"active":true}""");

        Assert.Empty(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal("""{"active":true}""", GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public void ParseResponseKeepsRawWindowValuesEvenWhenTheyAreUnusable()
    {
        var quota = Parse("""{"dailyInputTokens":{"used":-2,"remaining":50,"percentUsed":1,"resetAt":0}}""");

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.Null(quota.NextResetAt);
        Assert.Null(quota.Limits[0].NextResetAt);
        Assert.Null(quota.Limits[0].PeriodStart);
        Assert.Equal(
            """{"windows":{"dailyInputTokens":{"percentUsed":1,"remaining":50,"resetAt":0,"used":-2}}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    // ------------------------------------------------------------------
    // Proxy, capability and helpers
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
        var checker = new NanogptQuotaChecker(direct, factory, new AntigravityTestClock(Now));

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
        var checker = new NanogptQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(
            Channel(settings: "{\"proxy\":{"),
            CancellationToken.None);

        Assert.Empty(factory.Requests);
        Assert.Single(direct.Requests);
    }

    [Fact]
    public async Task CheckQuotaUsesTheDirectExecutorWithoutASettingsProxy()
    {
        var direct = OkExecutor();
        var factory = new ScriptedProxyExecutorFactory();
        var checker = new NanogptQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Empty(factory.Requests);
        Assert.Single(direct.Requests);
    }

    [Theory]
    [InlineData("nanogpt", true)]
    [InlineData("nanogpt_responses", true)]
    [InlineData("openai", false)]
    [InlineData("claudecode", false)]
    public void SupportsChannelMatchesBothNanoGptTypes(string channelType, bool expected)
    {
        var checker = CreateChecker(new ScriptedExecutor());

        Assert.Equal(expected, checker.SupportsChannel(new ChannelRecord { Type = channelType }));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static NanogptQuotaChecker CreateChecker(
        out ClaudeCodeQuotaCheckerTests.RecordingHttpHandler handler)
    {
        handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new NanogptQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now));
    }

    private static NanogptQuotaChecker CreateChecker(ScriptedExecutor executor)
        => new(executor, new ScriptedProxyExecutorFactory(), new AntigravityTestClock(Now));

    private static ChannelRecord Channel(
        string? credentials = null,
        string? baseUrl = null,
        string settings = "{}")
        => new()
        {
            Type = "nanogpt",
            BaseUrl = baseUrl,
            Credentials = credentials ?? """{"apiKey":"test-api-key"}""",
            Settings = settings,
        };

    private static QuotaData Parse(string json)
        => NanogptQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(json), Now);

    private static NanogptUsageResponse ParseUsageResponse(string json)
        => NanogptQuotaChecker.ParseUsageResponse(Encoding.UTF8.GetBytes(json));

    private static ScriptedExecutor OkExecutor() => new()
    {
        OnDo = (_, _, _) => Ok("{}"),
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
