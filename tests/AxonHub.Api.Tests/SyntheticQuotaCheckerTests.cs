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
/// Ports internal/server/biz/provider_quota/synthetic_checker.go together with
/// synthetic_checker_test.go: the fixed /v2/quotas request derived from the
/// channel host, the API-key precedence (trimmed legacy field, then the first
/// apiKeys entry verbatim), the rolling five-hour and weekly token pools, the
/// two weekly shapes (credits and plan percentages) and the display-only hourly
/// search counter.
/// </summary>
public sealed class SyntheticQuotaCheckerTests
{
    // Before every reset in the reference fixtures, so normalization keeps the
    // provider's reset instants intact.
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string QuotaUrl = "https://api.synthetic.new/v2/quotas";

    private const string ApiKey = "test-api-key";

    private const string FullBody = """
        {
            "subscription": {"limit": 750, "requests": 0, "renewsAt": "2099-04-25T08:47:39.947Z"},
            "search": {"hourly": {"limit": 250, "requests": 0, "renewsAt": "2099-04-25T05:00:00.000Z"}},
            "weeklyTokenLimit": {
                "nextRegenAt": "2099-04-25T04:48:26.000Z",
                "percentRemaining": 38.6,
                "maxCredits": "$36.00",
                "remainingCredits": "$13.90"
            },
            "rollingFiveHourLimit": {
                "nextTickAt": "2099-04-25T04:00:01.000Z",
                "tickPercent": 0.05,
                "remaining": 750,
                "max": 750,
                "limited": false
            }
        }
        """;

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaFetchesTheQuotasEndpointAndMapsThePools()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(FullBody);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(QuotaUrl, request.Url);
        Assert.Equal("Bearer " + ApiKey, request.Header("Authorization"));
        // The executor always attaches content, so the bodyless GET keeps the
        // Content-Type on the wire like Go's net/http (C206 resolved).
        Assert.Equal("application/json", request.Header("Content-Type"));
        // The shared executor adds its default Accept (Go would send none).
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Equal(string.Empty, request.Body);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal("synthetic", quota.ProviderType);
        Assert.True(quota.Ready);
        Assert.Equal(3, quota.Limits.Count);

        // The rolling five-hour window comes first, then the weekly pool, then
        // the display-only search counter.
        Assert.Equal(QuotaLimitTypes.Token, quota.Limits[0].Type);
        Assert.Equal(QuotaWindows.FiveHours, quota.Limits[0].Window);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[0].Status);
        Assert.Equal(0.05, quota.Limits[0].UsageRatio, 9);
        Assert.True(quota.Limits[0].Ready);
        Assert.Equal(new DateTimeOffset(2099, 4, 25, 4, 0, 1, TimeSpan.Zero), quota.Limits[0].NextResetAt);
        Assert.Null(quota.Limits[0].PeriodStart);

        Assert.Equal(QuotaLimitTypes.Token, quota.Limits[1].Type);
        Assert.Equal(QuotaWindows.Weekly, quota.Limits[1].Window);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[1].Status);
        Assert.Equal(0.614, quota.Limits[1].UsageRatio, 9);
        Assert.True(quota.Limits[1].Ready);
        Assert.Equal(new DateTimeOffset(2099, 4, 25, 4, 48, 26, TimeSpan.Zero), quota.Limits[1].NextResetAt);
        Assert.Equal(36.0, quota.Limits[1].PeriodQuota!.Value, 9);
        Assert.Equal(22.1, quota.Limits[1].PeriodCost!.Value, 9);
        Assert.Null(quota.Limits[1].PeriodStart);

        Assert.Equal(QuotaLimitTypes.Search, quota.Limits[2].Type);
        Assert.Equal(SyntheticQuotaChecker.SearchHourlyWindow, quota.Limits[2].Window);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[2].Status);
        Assert.Equal(0.0, quota.Limits[2].UsageRatio, 9);
        Assert.True(quota.Limits[2].Ready);
        Assert.Equal(new DateTimeOffset(2099, 4, 25, 5, 0, 0, TimeSpan.Zero), quota.Limits[2].NextResetAt);
        Assert.Null(quota.Limits[2].PeriodStart);

        // The five-hour tick is the earliest of the three blocking resets; the
        // search counter is excluded.
        Assert.Equal(new DateTimeOffset(2099, 4, 25, 4, 0, 1, TimeSpan.Zero), quota.NextResetAt);

        Assert.Equal(
            """{"rollingFiveHourLimit":{"limited":false,"max":750,"nextTickAt":"2099-04-25T04:00:01.000Z","remaining":750,"tickPercent":0.05},"search":{"hourly":{"limit":250,"renewsAt":"2099-04-25T05:00:00.000Z","requests":0}},"subscription":{"limit":750,"renewsAt":"2099-04-25T08:47:39.947Z","requests":0},"weeklyTokenLimit":{"maxCredits":"$36.00","nextRegenAt":"2099-04-25T04:48:26.000Z","percentRemaining":38.6,"remainingCredits":"$13.90"}}""",
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
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(request.Headers, "Authorization"));
        Assert.NotNull(request.Auth);
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal(ApiKey, request.Auth.ApiKey);
        Assert.Null(request.Body);
    }

    // ------------------------------------------------------------------
    // Search counter: listed but never blocking
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchQuotaIsListedButNeverBlocks()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"weeklyTokenLimit":{"nextRegenAt":"2099-04-25T04:48:26.000Z","percentRemaining":50.0,"maxCredits":"$36.00","remainingCredits":"$18.00"},"search":{"hourly":{"limit":250,"requests":250,"renewsAt":"2099-04-25T05:00:00.000Z"}}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);

        Assert.Equal(QuotaLimitTypes.Search, quota.Limits[1].Type);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Limits[1].Status);
        Assert.Equal(1.0, quota.Limits[1].UsageRatio, 9);
        Assert.False(quota.Limits[1].Ready);

        var (tokenRouting, tokenReason) = ProviderQuotaRouting.EvaluateQuotaRouting(
            quota.Limits,
            quota.Status,
            QuotaLimitTypes.Token,
            Now);
        Assert.Equal(QuotaRoutingStates.Open, tokenRouting);
        Assert.Equal(string.Empty, tokenReason);

        var (imageRouting, imageReason) = ProviderQuotaRouting.EvaluateQuotaRouting(
            quota.Limits,
            quota.Status,
            QuotaLimitTypes.Image,
            Now);
        Assert.Equal(QuotaRoutingStates.Open, imageRouting);
        Assert.Equal(string.Empty, imageReason);

        var (status, ready) = ProviderQuotaData.EffectiveStatus(
            quota.Limits,
            quota.Status,
            quota.Ready,
            QuotaLimitTypes.Token);
        Assert.Equal(ProviderQuotaStatuses.Available, status);
        Assert.True(ready);
    }

    [Fact]
    public async Task SearchOnlyPayloadDoesNotBlock()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"search":{"hourly":{"limit":250,"requests":250,"renewsAt":"2099-04-25T05:00:00.000Z"}}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.False(quota.Ready);
        Assert.Single(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Limits[0].Status);
        Assert.Null(quota.NextResetAt);

        var (routing, _) = ProviderQuotaRouting.EvaluateQuotaRouting(
            quota.Limits,
            quota.Status,
            QuotaLimitTypes.Token,
            Now);
        Assert.Equal(QuotaRoutingStates.Unknown, routing);
    }

    [Fact]
    public async Task SearchWarningThresholdBelowExhaustion()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"weeklyTokenLimit":{"nextRegenAt":"2099-04-25T04:48:26.000Z","percentRemaining":50.0},"search":{"hourly":{"limit":250,"requests":200,"renewsAt":"2099-04-25T05:00:00.000Z"}}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        // 200/250 sits exactly at WarningThresholdRatio: the search limit warns
        // without exhausting, and the channel stays available with search ready.
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);

        Assert.Equal(QuotaLimitTypes.Search, quota.Limits[1].Type);
        Assert.Equal(SyntheticQuotaChecker.SearchHourlyWindow, quota.Limits[1].Window);
        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Limits[1].Status);
        Assert.Equal(0.8, quota.Limits[1].UsageRatio, 9);
        Assert.True(quota.Limits[1].Ready);
        Assert.NotNull(quota.Limits[1].NextResetAt);

        var (routing, _) = ProviderQuotaRouting.EvaluateQuotaRouting(
            quota.Limits,
            quota.Status,
            QuotaLimitTypes.Token,
            Now);
        Assert.Equal(QuotaRoutingStates.Open, routing);

        var (status, ready) = ProviderQuotaData.EffectiveStatus(
            quota.Limits,
            quota.Status,
            quota.Ready,
            QuotaLimitTypes.Token);
        Assert.Equal(ProviderQuotaStatuses.Available, status);
        Assert.True(ready);
    }

    [Theory]
    [InlineData(
        """{"weeklyTokenLimit":{"nextRegenAt":"2099-04-25T04:48:26.000Z","percentRemaining":50.0},"search":{"hourly":{"limit":0,"requests":0,"renewsAt":"2099-04-25T05:00:00.000Z"}}}""")]
    [InlineData(
        """{"weeklyTokenLimit":{"nextRegenAt":"2099-04-25T04:48:26.000Z","percentRemaining":50.0},"search":{"hourly":{"limit":250,"renewsAt":"2099-04-25T05:00:00.000Z"}}}""")]
    public async Task SkipsUnusableSearchLimit(string body)
    {
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Single(quota.Limits);
        Assert.Equal(QuotaWindows.Weekly, quota.Limits[0].Window);
        Assert.Contains("search", quota.RawData.Keys);
        Assert.Null(quota.Limits[0].PeriodCost);
        Assert.Null(quota.Limits[0].PeriodQuota);
    }

    [Theory]
    [InlineData(250, 0, ProviderQuotaStatuses.Available, 0.0)]
    [InlineData(250, 199, ProviderQuotaStatuses.Available, 0.796)]
    [InlineData(250, 200, ProviderQuotaStatuses.Warning, 0.8)]
    [InlineData(250, 249, ProviderQuotaStatuses.Warning, 0.996)]
    [InlineData(250, 250, ProviderQuotaStatuses.Exhausted, 1.0)]
    [InlineData(250, 300, ProviderQuotaStatuses.Exhausted, 1.2)]
    [InlineData(1, 1, ProviderQuotaStatuses.Exhausted, 1.0)]
    public void SearchHourlyLimitStatusMapsRatiosToStatuses(
        long limit,
        long requests,
        string expectedStatus,
        double expectedRatio)
    {
        var hourly = new SyntheticSearchHourly { Limit = limit, Requests = requests };

        Assert.True(SyntheticQuotaChecker.SearchHourlyLimitStatus(hourly, out var status));

        Assert.Equal(QuotaLimitTypes.Search, status.Type);
        Assert.Equal(expectedStatus, status.Status);
        Assert.Equal(expectedRatio, status.UsageRatio, 9);
        Assert.Equal(SyntheticQuotaChecker.SearchHourlyWindow, status.Window);
        Assert.Null(status.AvailabilityGroup);
        Assert.Null(status.PeriodStart);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0L, 0L)]
    [InlineData(-1L, 0L)]
    [InlineData(null, 10L)]
    [InlineData(250L, null)]
    public void SearchHourlyLimitStatusRejectsUnusableCounters(long? limit, long? requests)
    {
        var hourly = new SyntheticSearchHourly { Limit = limit, Requests = requests };

        Assert.False(SyntheticQuotaChecker.SearchHourlyLimitStatus(hourly, out _));
        Assert.False(SyntheticQuotaChecker.SearchHourlyLimitStatus(null, out _));
    }

    // ------------------------------------------------------------------
    // Raw counter fields
    // ------------------------------------------------------------------

    [Fact]
    public async Task PersistsNewCounterRawFields()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"subscription":{"limit":500,"requests":0,"renewsAt":"2099-10-17T09:26:59.603Z"},"search":{"hourly":{"limit":250,"requests":0,"renewsAt":"2099-10-17T05:26:59.604Z"}},"freeToolCalls":{"limit":0,"requests":0,"renewsAt":"2099-10-18T04:26:59.606Z"},"weeklyTokenLimit":{"nextRegenAt":"2099-10-17T07:19:01.000Z","percentRemaining":99.73476194285715,"maxCredits":"$10.50","remainingCredits":"$10.47"}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(QuotaLimitTypes.Search, quota.Limits[1].Type);

        Assert.Equal(10.50, quota.Limits[0].PeriodQuota!.Value, 9);
        Assert.Equal(0.03, quota.Limits[0].PeriodCost!.Value, 9);

        var subscription = Assert.IsType<Dictionary<string, object?>>(quota.RawData["subscription"]);
        Assert.Equal(500L, subscription["limit"]);
        Assert.Equal("2099-10-17T09:26:59.603Z", subscription["renewsAt"]);

        var search = Assert.IsType<Dictionary<string, object?>>(quota.RawData["search"]);
        var hourly = Assert.IsType<Dictionary<string, object?>>(search["hourly"]);
        Assert.Equal("2099-10-17T05:26:59.604Z", hourly["renewsAt"]);

        var freeToolCalls = Assert.IsType<Dictionary<string, object?>>(quota.RawData["freeToolCalls"]);
        Assert.Equal(0L, freeToolCalls["limit"]);
        Assert.Equal("2099-10-18T04:26:59.606Z", freeToolCalls["renewsAt"]);
    }

    [Fact]
    public void PrepareRawDataKeepsZeroAndFalseValues()
    {
        var quota = Parse(
            """{"subscription":{"limit":0,"requests":0},"freeToolCalls":{"limit":0,"requests":0},"rollingFiveHourLimit":{"tickPercent":0,"limited":false}}""");

        Assert.Equal(
            """{"freeToolCalls":{"limit":0,"requests":0},"rollingFiveHourLimit":{"limited":false,"tickPercent":0},"subscription":{"limit":0,"requests":0}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public void PrepareRawDataOmitsMissingFields()
    {
        var quota = Parse("""{"subscription":{},"search":{},"freeToolCalls":{}}""");

        Assert.Equal(
            """{"freeToolCalls":{},"search":{},"subscription":{}}""",
            GoJson.MarshalToString(quota.RawData));
        Assert.Empty(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
    }

    // ------------------------------------------------------------------
    // Weekly credit amounts
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("$10.50", "$10.47", 0.03, 10.50)]
    [InlineData("36", "13.9", 22.1, 36)]
    [InlineData(" $10.50 ", " $10.47 ", 0.03, 10.50)]
    [InlineData("+1.05e1", "+1.047e1", 0.03, 10.50)]
    [InlineData("$10.50", "$0", 10.50, 10.50)]
    [InlineData("$10.50", "-1.50", 12, 10.50)]
    [InlineData("$10.50", "$11", 0, 10.50)]
    [InlineData("0x1p4", "0x1p3", 8, 16)]
    [InlineData("1_0.5", "1_0.25", 0.25, 10.5)]
    [InlineData("5.", ".5", 4.5, 5)]
    public async Task WeeklyCreditAmounts(string maxCredits, string remainingCredits, double cost, double quota)
    {
        var body = $$$"""{"weeklyTokenLimit":{"percentRemaining":50,"maxCredits":"{{{maxCredits}}}","remainingCredits":"{{{remainingCredits}}}"}}""";
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var result = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, result.Status);
        Assert.True(result.Ready);
        Assert.Single(result.Limits);
        Assert.Equal(QuotaWindows.Weekly, result.Limits[0].Window);
        Assert.Equal(cost, result.Limits[0].PeriodCost!.Value, 9);
        Assert.Equal(quota, result.Limits[0].PeriodQuota!.Value, 9);
        Assert.Contains("\"weeklyTokenLimit\"", GoJson.MarshalToString(result.RawData));
    }

    [Theory]
    [InlineData("1e308", "-1e308")]
    [InlineData("0", "0")]
    [InlineData("-1", "0")]
    [InlineData("NaN", "$5.00")]
    [InlineData("Inf", "$5.00")]
    [InlineData("+Inf", "$5.00")]
    [InlineData("-Inf", "$5.00")]
    [InlineData("Infinity", "$5.00")]
    [InlineData("+Infinity", "$5.00")]
    [InlineData("-Infinity", "$5.00")]
    [InlineData("$NaN", "$5.00")]
    [InlineData("$+Inf", "$5.00")]
    [InlineData("$-Inf", "$5.00")]
    [InlineData("$10.50", "NaN")]
    [InlineData("$10.50", "Inf")]
    [InlineData("$10.50", "+Inf")]
    [InlineData("$10.50", "-Inf")]
    [InlineData("$10.50", "Infinity")]
    [InlineData("$10.50", "+Infinity")]
    [InlineData("$10.50", "-Infinity")]
    public async Task WeeklyCreditAmountsAreOmittedWhenUnusable(string maxCredits, string remainingCredits)
    {
        var body = $$$"""{"weeklyTokenLimit":{"percentRemaining":50,"maxCredits":"{{{maxCredits}}}","remainingCredits":"{{{remainingCredits}}}"}}""";
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Single(quota.Limits);
        Assert.Null(quota.Limits[0].PeriodCost);
        Assert.Null(quota.Limits[0].PeriodQuota);
    }

    [Fact]
    public void ParseSyntheticCreditsRejectsMissingValues()
    {
        Assert.False(SyntheticQuotaChecker.TryParseSyntheticCredits(null, out var credits));
        Assert.Equal(0, credits);
    }

    [Theory]
    [InlineData("$10.50", true, 10.50)]
    [InlineData(" 10.50 ", true, 10.50)]
    [InlineData("+1.05e1", true, 10.50)]
    [InlineData("-1.5", true, -1.5)]
    [InlineData("0", true, 0)]
    [InlineData("", false, 0)]
    [InlineData("$", false, 0)]
    [InlineData(" $ ", false, 0)]
    [InlineData("$$5", false, 0)]
    [InlineData("not a credit amount", false, 0)]
    [InlineData("NaN", false, 0)]
    [InlineData("Inf", false, 0)]
    [InlineData("+Inf", false, 0)]
    [InlineData("-Inf", false, 0)]
    [InlineData("Infinity", false, 0)]
    [InlineData("+Infinity", false, 0)]
    [InlineData("-Infinity", false, 0)]
    [InlineData("$NaN", false, 0)]
    [InlineData("$+Inf", false, 0)]
    [InlineData("$-Inf", false, 0)]
    [InlineData("1e400", false, 0)]
    public void ParseSyntheticCreditsMatchesGoStrconv(string value, bool expectedOk, double expected)
    {
        var ok = SyntheticQuotaChecker.TryParseSyntheticCredits(value, out var credits);

        Assert.Equal(expectedOk, ok);
        Assert.Equal(expected, credits, 9);
    }

    // ------------------------------------------------------------------
    // Weekly pool shapes
    // ------------------------------------------------------------------

    [Fact]
    public async Task PlanWeeklyLimitShape()
    {
        var weeklyReset = new DateTimeOffset(2099, 10, 23, 0, 8, 27, TimeSpan.Zero);

        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"subscription":{"limit":500,"requests":0,"renewsAt":"2099-10-23T03:40:12.700Z"},"search":{"hourly":{"limit":250,"requests":13,"renewsAt":"2099-10-22T23:11:12.702Z"}},"planWeeklyLimit":{"nextRegenAt":"2099-10-23T00:08:27.000Z","percentRemaining":90.19385924897959,"nextRegenPercent":2}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);

        Assert.Equal(QuotaLimitTypes.Token, quota.Limits[0].Type);
        Assert.Equal(QuotaWindows.Weekly, quota.Limits[0].Window);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[0].Status);
        Assert.Equal(0.0980614075102041, quota.Limits[0].UsageRatio, 12);
        Assert.Equal(weeklyReset, quota.Limits[0].NextResetAt);
        Assert.Equal(weeklyReset, quota.NextResetAt);
        Assert.Null(quota.Limits[0].PeriodCost);
        Assert.Null(quota.Limits[0].PeriodQuota);

        Assert.Equal(QuotaLimitTypes.Search, quota.Limits[1].Type);
        Assert.Equal(0.052, quota.Limits[1].UsageRatio, 9);

        var planWeekly = Assert.IsType<Dictionary<string, object?>>(quota.RawData["planWeeklyLimit"]);
        Assert.Equal(90.19385924897959, (double)planWeekly["percentRemaining"]!, 12);
        Assert.Equal(2.0, (double)planWeekly["nextRegenPercent"]!, 12);
        Assert.DoesNotContain("weeklyTokenLimit", quota.RawData.Keys);
    }

    [Fact]
    public async Task BothWeeklyShapesPreferPlanPercentKeepCredits()
    {
        var planReset = new DateTimeOffset(2099, 10, 23, 0, 8, 27, TimeSpan.Zero);

        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"weeklyTokenLimit":{"nextRegenAt":"2099-10-23T04:00:00.000Z","percentRemaining":50.0,"maxCredits":"$10.50","remainingCredits":"$10.47"},"planWeeklyLimit":{"nextRegenAt":"2099-10-23T00:08:27.000Z","percentRemaining":90.0,"nextRegenPercent":2}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Single(quota.Limits);
        Assert.Equal(0.1, quota.Limits[0].UsageRatio, 9);
        Assert.Equal(planReset, quota.Limits[0].NextResetAt);
        Assert.Equal(10.50, quota.Limits[0].PeriodQuota!.Value, 9);
        Assert.Equal(0.03, quota.Limits[0].PeriodCost!.Value, 9);
        Assert.Contains("weeklyTokenLimit", quota.RawData.Keys);
        Assert.Contains("planWeeklyLimit", quota.RawData.Keys);
    }

    [Fact]
    public async Task PartialPlanShapeFallsBackPerField()
    {
        var legacyReset = new DateTimeOffset(2099, 10, 23, 4, 0, 0, TimeSpan.Zero);

        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"weeklyTokenLimit":{"nextRegenAt":"2099-10-23T04:00:00.000Z","percentRemaining":50.0,"maxCredits":"$10.50","remainingCredits":"$10.47"},"planWeeklyLimit":{"nextRegenPercent":2}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Single(quota.Limits);
        Assert.Equal(0.5, quota.Limits[0].UsageRatio, 9);
        Assert.Equal(legacyReset, quota.Limits[0].NextResetAt);
        Assert.Equal(10.50, quota.Limits[0].PeriodQuota!.Value, 9);
    }

    [Theory]
    [InlineData(
        """{"percentRemaining":90.0,"nextRegenAt":"2099-10-23T00:08:27.000Z"}""",
        0.1,
        ProviderQuotaStatuses.Available,
        "2099-10-23T00:08:27.000Z",
        10.50,
        5.25,
        ProviderQuotaStatuses.Available,
        QuotaRoutingStates.Open)]
    [InlineData(
        """{"nextRegenAt":"2099-10-23T00:08:27.000Z"}""",
        0.5,
        ProviderQuotaStatuses.Available,
        "2099-10-23T00:08:27.000Z",
        10.50,
        5.25,
        ProviderQuotaStatuses.Available,
        QuotaRoutingStates.Open)]
    [InlineData(
        """{"percentRemaining":0.0,"nextRegenPercent":2}""",
        1.0,
        ProviderQuotaStatuses.Exhausted,
        "2099-10-23T04:00:00.000Z",
        10.50,
        5.25,
        ProviderQuotaStatuses.Exhausted,
        QuotaRoutingStates.Exhausted)]
    public async Task MixedWeeklyShapesResolvePerField(
        string planWeekly,
        double expectedUsage,
        string expectedWeeklyStatus,
        string expectedWeeklyReset,
        double expectedQuota,
        double expectedCost,
        string expectedChannelStatus,
        string expectedRouting)
    {
        var fiveHourReset = new DateTimeOffset(2099, 10, 20, 6, 0, 0, TimeSpan.Zero);
        var body = $$$"""{"weeklyTokenLimit":{"nextRegenAt":"2099-10-23T04:00:00.000Z","percentRemaining":50.0,"maxCredits":"$10.50","remainingCredits":"$5.25"},"planWeeklyLimit":{{{planWeekly}}},"rollingFiveHourLimit":{"nextTickAt":"2099-10-20T06:00:00.000Z","tickPercent":0.05,"remaining":700,"max":750,"limited":false}}""";
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(expectedChannelStatus, quota.Status);
        Assert.Equal(expectedChannelStatus == ProviderQuotaStatuses.Available, quota.Ready);
        Assert.Equal(fiveHourReset, quota.NextResetAt);

        var weekly = Assert.Single(quota.Limits, limit => limit.Window == QuotaWindows.Weekly);
        Assert.Equal(expectedWeeklyStatus, weekly.Status);
        Assert.Equal(expectedUsage, weekly.UsageRatio, 9);
        Assert.Equal(
            DateTimeOffset.Parse(expectedWeeklyReset, CultureInfo.InvariantCulture),
            weekly.NextResetAt);
        Assert.Equal(expectedQuota, weekly.PeriodQuota!.Value, 9);
        Assert.Equal(expectedCost, weekly.PeriodCost!.Value, 9);

        var (routing, _) = ProviderQuotaRouting.EvaluateQuotaRouting(
            quota.Limits,
            quota.Status,
            QuotaLimitTypes.Token,
            Now);
        Assert.Equal(expectedRouting, routing);

        var (status, ready) = ProviderQuotaData.EffectiveStatus(
            quota.Limits,
            quota.Status,
            quota.Ready,
            QuotaLimitTypes.Token);
        Assert.Equal(expectedChannelStatus, status);
        Assert.Equal(expectedChannelStatus == ProviderQuotaStatuses.Available, ready);
    }

    [Fact]
    public void WeeklyLimitReturnsNullWithoutEitherShape()
    {
        Assert.Null(new SyntheticUsageResponse().WeeklyLimit());
    }

    [Fact]
    public void WeeklyLimitCopiesTheCreditShape()
    {
        var response = new SyntheticUsageResponse
        {
            WeeklyTokenLimit = new SyntheticWeeklyTokenLimit
            {
                NextRegenAt = "2099-04-25T04:48:26.000Z",
                PercentRemaining = 38.6,
                MaxCredits = "$36.00",
                RemainingCredits = "$13.90",
            },
        };

        var merged = response.WeeklyLimit();

        Assert.NotNull(merged);
        Assert.Equal("2099-04-25T04:48:26.000Z", merged!.NextRegenAt);
        Assert.Equal(38.6, merged.PercentRemaining!.Value, 9);
        Assert.Equal("$36.00", merged.MaxCredits);
        Assert.Equal("$13.90", merged.RemainingCredits);
    }

    [Fact]
    public void WeeklyLimitMergesOnlyThePlanFieldsPresent()
    {
        var response = new SyntheticUsageResponse
        {
            WeeklyTokenLimit = new SyntheticWeeklyTokenLimit
            {
                NextRegenAt = "2099-10-23T04:00:00.000Z",
                PercentRemaining = 50.0,
                MaxCredits = "$10.50",
                RemainingCredits = "$10.47",
            },
            PlanWeeklyLimit = new SyntheticPlanWeeklyLimit
            {
                NextRegenPercent = 2,
            },
        };

        var merged = response.WeeklyLimit();

        Assert.NotNull(merged);
        Assert.Equal("2099-10-23T04:00:00.000Z", merged!.NextRegenAt);
        Assert.Equal(50.0, merged.PercentRemaining!.Value, 9);
        Assert.Equal("$10.50", merged.MaxCredits);
        Assert.Equal("$10.47", merged.RemainingCredits);
    }

    // ------------------------------------------------------------------
    // Status states
    // ------------------------------------------------------------------

    [Fact]
    public async Task WarningState()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"subscription":{"limit":750,"requests":0,"renewsAt":"2026-04-25T08:47:39.947Z"},"weeklyTokenLimit":{"nextRegenAt":"2026-04-25T04:48:26.000Z","percentRemaining":15.0,"maxCredits":"$36.00","remainingCredits":"$5.40"},"rollingFiveHourLimit":{"nextTickAt":"2026-04-25T04:00:01.000Z","tickPercent":0.85,"remaining":112,"max":750,"limited":false}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Limits[0].Status);
        Assert.Equal(0.85, quota.Limits[0].UsageRatio, 9);
        Assert.True(quota.Limits[0].Ready);
        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Limits[1].Status);
        Assert.Equal(0.85, quota.Limits[1].UsageRatio, 9);
        Assert.True(quota.Limits[1].Ready);
    }

    [Fact]
    public void WarningAtUsageThreshold()
    {
        var tickPercent = 0.8;
        var percentRemaining = 20.0;

        var limits = SyntheticQuotaChecker.BuildSyntheticLimitStatuses(
            new SyntheticWeeklyTokenLimit { PercentRemaining = percentRemaining },
            new SyntheticRollingFiveHourLimit { TickPercent = tickPercent });

        Assert.Equal(2, limits.Count);
        Assert.Equal(ProviderQuotaStatuses.Warning, limits[0].Status);
        Assert.Equal(ProviderQuotaStatuses.Warning, limits[1].Status);
    }

    [Fact]
    public async Task ExhaustedState()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"subscription":{"limit":750,"requests":750,"renewsAt":"2026-04-25T08:47:39.947Z"},"weeklyTokenLimit":{"nextRegenAt":"2026-04-25T04:48:26.000Z","percentRemaining":0.0,"maxCredits":"$36.00","remainingCredits":"$0.00"},"rollingFiveHourLimit":{"nextTickAt":"2026-04-25T04:00:01.000Z","tickPercent":1.0,"remaining":0,"max":750,"limited":true}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Limits[0].Status);
        Assert.Equal(1.0, quota.Limits[0].UsageRatio, 9);
        Assert.False(quota.Limits[0].Ready);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Limits[1].Status);
        Assert.Equal(1.0, quota.Limits[1].UsageRatio, 9);
        Assert.False(quota.Limits[1].Ready);
    }

    // ------------------------------------------------------------------
    // Limit builders
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, false, ProviderQuotaStatuses.Available, 0.0)]
    [InlineData(0.79, false, ProviderQuotaStatuses.Available, 0.79)]
    [InlineData(0.8, false, ProviderQuotaStatuses.Warning, 0.8)]
    [InlineData(0.99, false, ProviderQuotaStatuses.Warning, 0.99)]
    [InlineData(1.0, false, ProviderQuotaStatuses.Warning, 1.0)]
    [InlineData(null, true, ProviderQuotaStatuses.Exhausted, 1.0)]
    [InlineData(0.05, true, ProviderQuotaStatuses.Exhausted, 1.0)]
    public void FiveHourLimitUsesLimitedBeforeTickPercent(
        double? tickPercent,
        bool limited,
        string expectedStatus,
        double expectedRatio)
    {
        var limits = SyntheticQuotaChecker.BuildSyntheticLimitStatuses(
            null,
            new SyntheticRollingFiveHourLimit
            {
                NextTickAt = "2099-04-25T04:00:01.000Z",
                TickPercent = tickPercent,
                Limited = limited,
            });

        var limit = Assert.Single(limits);
        Assert.Equal(QuotaLimitTypes.Token, limit.Type);
        Assert.Equal(QuotaWindows.FiveHours, limit.Window);
        Assert.Equal(expectedStatus, limit.Status);
        Assert.Equal(expectedRatio, limit.UsageRatio, 9);
        Assert.Equal(new DateTimeOffset(2099, 4, 25, 4, 0, 1, TimeSpan.Zero), limit.NextResetAt);
        Assert.Null(limit.PeriodStart);
    }

    [Theory]
    [InlineData(null, ProviderQuotaStatuses.Available, 0.0)]
    [InlineData(100.0, ProviderQuotaStatuses.Available, 0.0)]
    [InlineData(80.0, ProviderQuotaStatuses.Available, 0.2)]
    [InlineData(20.0, ProviderQuotaStatuses.Warning, 0.8)]
    [InlineData(0.0, ProviderQuotaStatuses.Exhausted, 1.0)]
    [InlineData(-1.0, ProviderQuotaStatuses.Exhausted, 1.01)]
    [InlineData(120.0, ProviderQuotaStatuses.Available, -0.2)]
    public void WeeklyLimitMapsPercentRemainingToAUsageRatio(
        double? percentRemaining,
        string expectedStatus,
        double expectedRatio)
    {
        var limit = SyntheticQuotaChecker.WeeklyTokenLimitStatus(
            new SyntheticWeeklyTokenLimit
            {
                NextRegenAt = "2099-04-25T04:48:26.000Z",
                PercentRemaining = percentRemaining,
            });

        Assert.Equal(QuotaLimitTypes.Token, limit.Type);
        Assert.Equal(QuotaWindows.Weekly, limit.Window);
        Assert.Equal(expectedStatus, limit.Status);
        Assert.Equal(expectedRatio, limit.UsageRatio, 9);
        Assert.Equal(new DateTimeOffset(2099, 4, 25, 4, 48, 26, TimeSpan.Zero), limit.NextResetAt);
        Assert.Null(limit.PeriodStart);
    }

    [Theory]
    [InlineData(0.0, ProviderQuotaStatuses.Available, 0.0)]
    [InlineData(1.0, ProviderQuotaStatuses.Exhausted, 1.0)]
    [InlineData(1.2, ProviderQuotaStatuses.Exhausted, 1.0)]
    public async Task FiveHourTickAtOneWarnsBeforeNormalizationThenExhausts(
        double tickPercent,
        string expectedStatus,
        double expectedRatio)
    {
        var body = $$$"""{"rollingFiveHourLimit":{"tickPercent":{{{tickPercent}}},"limited":false}}""";
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        // The builder only escalates tickPercent to warning; normalization then
        // forces exhausted (and clamps the ratio) at >= 1.
        Assert.Equal(expectedRatio, quota.Limits[0].UsageRatio, 9);
        Assert.Equal(expectedStatus, quota.Limits[0].Status);
    }

    [Theory]
    [InlineData(-0.2, 0)]
    [InlineData(-1.0, 0)]
    public async Task NegativeUsageRatiosAreDroppedByNormalization(double tickPercent, int expectedLimits)
    {
        var body = $$$"""{"rollingFiveHourLimit":{"tickPercent":{{{tickPercent}}}}}""";
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        // The builder saw a limit and set the explicit status to available; the
        // dropped limit leaves that explicit status in place (normalization
        // never degrades a ready explicit status).
        Assert.Equal(expectedLimits, quota.Limits.Count);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
    }

    [Fact]
    public async Task WeeklyPercentAboveOneHundredDropsTheLimitButKeepsTheExplicitReset()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"subscription":{"renewsAt":"2099-04-25T08:47:39.947Z"},"weeklyTokenLimit":{"nextRegenAt":"2099-04-25T04:48:26.000Z","percentRemaining":120}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Empty(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        // The reset aggregation reads the raw payload, not the surviving
        // limits: the weekly regeneration instant is earlier than renewsAt, so
        // it still wins even though the over-100% weekly limit was dropped.
        Assert.Equal(new DateTimeOffset(2099, 4, 25, 4, 48, 26, TimeSpan.Zero), quota.NextResetAt);
    }

    [Fact]
    public void BuildersWithoutEitherShapeReturnNoLimits()
    {
        Assert.Empty(SyntheticQuotaChecker.BuildSyntheticLimitStatuses(null, null));
    }

    // ------------------------------------------------------------------
    // Reset aggregation and parsing
    // ------------------------------------------------------------------

    [Fact]
    public async Task NextResetAtIgnoresTheSearchCounter()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"subscription":{"limit":750,"requests":0,"renewsAt":"2099-04-25T08:47:39.947Z"},"search":{"hourly":{"limit":250,"requests":0,"renewsAt":"2099-04-25T03:00:00.000Z"}},"weeklyTokenLimit":{"nextRegenAt":"2099-04-25T04:48:26.000Z","percentRemaining":38.6,"maxCredits":"$36.00","remainingCredits":"$13.90"},"rollingFiveHourLimit":{"nextTickAt":"2099-04-25T04:00:01.000Z","tickPercent":0.05,"remaining":750,"max":750,"limited":false}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(new DateTimeOffset(2099, 4, 25, 4, 0, 1, TimeSpan.Zero), quota.NextResetAt);
    }

    [Theory]
    [InlineData("2099-04-25T08:47:39.947Z", "2099-04-25T04:48:26.000Z", "2099-04-25T04:00:01.000Z", "2099-04-25T04:00:01.000Z")]
    [InlineData("2099-04-25T01:00:00Z", "2099-04-25T04:48:26.000Z", "2099-04-25T04:00:01.000Z", "2099-04-25T01:00:00.000Z")]
    [InlineData("2099-04-25T08:47:39.947Z", "2099-04-25T02:00:00Z", "2099-04-25T04:00:01.000Z", "2099-04-25T02:00:00.000Z")]
    [InlineData("2099-04-25T08:47:39.947Z", "2099-04-25T04:48:26.000Z", "2099-04-25T00:00:00Z", "2099-04-25T00:00:00.000Z")]
    public void FindEarliestResetAtPicksTheEarliestValidInstant(
        string subscriptionRenewsAt,
        string weeklyNextRegenAt,
        string fiveHourNextTickAt,
        string expected)
    {
        var earliest = SyntheticQuotaChecker.FindEarliestSyntheticResetAt(
            new SyntheticSubscription { RenewsAt = subscriptionRenewsAt },
            new SyntheticWeeklyTokenLimit { NextRegenAt = weeklyNextRegenAt },
            new SyntheticRollingFiveHourLimit { NextTickAt = fiveHourNextTickAt });

        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), earliest);
    }

    [Fact]
    public void FindEarliestResetAtIgnoresUnparseableValues()
    {
        var earliest = SyntheticQuotaChecker.FindEarliestSyntheticResetAt(
            new SyntheticSubscription { RenewsAt = "not a time" },
            new SyntheticWeeklyTokenLimit { NextRegenAt = "2099-04-25T04:48:26.000Z" },
            new SyntheticRollingFiveHourLimit { NextTickAt = "" });

        Assert.Equal(new DateTimeOffset(2099, 4, 25, 4, 48, 26, TimeSpan.Zero), earliest);
        Assert.Null(SyntheticQuotaChecker.FindEarliestSyntheticResetAt(null, null, null));
    }

    [Fact]
    public async Task ResetFractionsAcceptCommasAndTruncateToTicks()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"rollingFiveHourLimit":{"nextTickAt":"2099-04-25T04:00:01,123456789012Z","tickPercent":0.05}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        // Go keeps nanoseconds; DateTimeOffset keeps 100ns ticks (C123).
        var expected = new DateTimeOffset(2099, 4, 25, 4, 0, 1, TimeSpan.Zero)
            .AddTicks(1234567);
        Assert.Equal(expected, quota.Limits[0].NextResetAt);
    }

    [Fact]
    public async Task ResetOutsideTheRepresentableRangeIsIgnored()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"subscription":{"renewsAt":"0000-01-01T00:00:00Z"},"weeklyTokenLimit":{"nextRegenAt":"9999-12-31T23:59:59Z","percentRemaining":50}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        // Year 0 parses in Go but not in DateTimeOffset; the weekly reset is
        // still representable and wins (C123).
        Assert.Equal(new DateTimeOffset(9999, 12, 31, 23, 59, 59, TimeSpan.Zero), quota.NextResetAt);
    }

    [Fact]
    public async Task ResetsAtOrBeforeNowAreCleared()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"weeklyTokenLimit":{"nextRegenAt":"2026-01-01T00:00:00Z","percentRemaining":50}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Null(quota.NextResetAt);
        Assert.Null(quota.Limits[0].NextResetAt);
    }

    // ------------------------------------------------------------------
    // Credentials and errors
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

        Assert.Contains("no API key", error.Message);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task CheckQuotaReportsMalformedJson()
    {
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok("not json") });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Contains("failed to parse synthetic usage response", error.Message);
    }

    [Theory]
    [InlineData(401, "401 Unauthorized", """{"error":"unauthorized"}""")]
    [InlineData(403, "403 Forbidden", "denied")]
    [InlineData(429, "429 Too Many Requests", "slow down")]
    [InlineData(500, "500 Internal Server Error", "boom")]
    public async Task CheckQuotaWrapsExecutorStatusFailures(int statusCode, string status, string body)
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new HttpClientError(
                "GET",
                QuotaUrl,
                statusCode,
                status,
                Encoding.UTF8.GetBytes(body)),
        });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.StartsWith("quota request failed: ", error.Message);
        Assert.Contains(statusCode.ToString(CultureInfo.InvariantCulture), error.Message);
    }

    [Fact]
    public async Task CheckQuotaWrapsTransportFailures()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new IOException("connection reset"),
        });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("quota request failed: connection reset", error.Message);
    }

    [Theory]
    [InlineData(201, "created", "HTTP 201: created")]
    [InlineData(204, "", "HTTP 204: ")]
    [InlineData(302, "redirect", "HTTP 302: redirect")]
    [InlineData(100, "continue", "HTTP 100: continue")]
    public async Task CheckQuotaRejectsNonOkStatusesBelowTheExecutorThreshold(
        int statusCode,
        string body,
        string expectedMessage)
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = statusCode,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
                Body = Encoding.UTF8.GetBytes(body),
            },
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(expectedMessage, error.Message);
    }

    // ------------------------------------------------------------------
    // Base URL
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaUsesTheCustomBaseUrl()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse("""{"weeklyTokenLimit":{"percentRemaining":50}}""");

        var quota = await checker.CheckQuotaAsync(
            Channel(baseUrl: "https://us-east.api.synthetic.new"),
            CancellationToken.None);

        Assert.Equal("https://us-east.api.synthetic.new/v2/quotas", Assert.Single(handler.Requests).Url);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
    }

    [Theory]
    [InlineData("", "https://api.synthetic.new/v2/quotas")]
    [InlineData("  ", "https://api.synthetic.new/v2/quotas")]
    [InlineData("  https://api.synthetic.new  ", "https://api.synthetic.new/v2/quotas")]
    [InlineData("https://api.synthetic.new", "https://api.synthetic.new/v2/quotas")]
    [InlineData("https://api.synthetic.new/v1/chat", "https://api.synthetic.new/v2/quotas")]
    [InlineData("https://api.synthetic.new/", "https://api.synthetic.new/v2/quotas")]
    [InlineData("http://api.synthetic.new", "https://api.synthetic.new/v2/quotas")]
    [InlineData("HTTP://API.SYNTHETIC.NEW/V1", "https://API.SYNTHETIC.NEW/v2/quotas")]
    [InlineData("https://user:pass@api.synthetic.new:8443/v1", "https://api.synthetic.new:8443/v2/quotas")]
    [InlineData("https://api.synthetic.new:443", "https://api.synthetic.new:443/v2/quotas")]
    [InlineData("https://[::1]:8080/v1", "https://[::1]:8080/v2/quotas")]
    [InlineData("ftp://host/v1", "ftp://host/v2/quotas")]
    [InlineData("//api.synthetic.new", "://api.synthetic.new/v2/quotas")]
    [InlineData("mailto:foo@bar", "mailto:///v2/quotas")]
    [InlineData("https:", "https:///v2/quotas")]
    [InlineData("://invalid", "https://api.synthetic.new/v2/quotas")]
    [InlineData("api.synthetic.new", "https://api.synthetic.new/v2/quotas")]
    [InlineData("https://api%2Esynthetic.new", "https://api.synthetic.new/v2/quotas")]
    [InlineData("https://api synthetic.new", "https://api.synthetic.new/v2/quotas")]
    public void BuildQuotaUrlMatchesTheReferenceBaseHandling(string baseUrl, string expected)
    {
        Assert.Equal(expected, SyntheticQuotaChecker.BuildQuotaUrl(baseUrl));
    }

    // ------------------------------------------------------------------
    // Capability
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("openai", "https://api.synthetic.new", true)]
    [InlineData("openai_responses", "https://api.synthetic.new", true)]
    [InlineData("openai", "https://api.synthetic.new:443", true)]
    [InlineData("openai", "https://us-east.api.synthetic.new", true)]
    [InlineData("openai", "HTTP://API.SYNTHETIC.NEW", true)]
    [InlineData("openai", "https://api.openai.com", false)]
    [InlineData("diagnostic", "https://api.synthetic.new", false)]
    [InlineData("nanogpt", "https://api.synthetic.new", false)]
    [InlineData("openai", "https://notapi.synthetic.new", false)]
    [InlineData("openai", "https://api.synthetic.new.evil.com", false)]
    [InlineData("openai", null, false)]
    public void SupportsChannelRequiresAnOpenAiTypeAndSyntheticHost(
        string channelType,
        string? baseUrl,
        bool expected)
    {
        var checker = CreateChecker(new ScriptedExecutor());

        Assert.Equal(expected, checker.SupportsChannel(new ChannelRecord
        {
            Type = channelType,
            BaseUrl = baseUrl,
        }));
    }

    // ------------------------------------------------------------------
    // JSON decoding: Go semantics
    // ------------------------------------------------------------------

    [Fact]
    public void ParseUsageResponseTreatsRootNullAsAnEmptyStruct()
    {
        var response = ParseUsageResponse("null");

        Assert.Null(response.Subscription);
        Assert.Null(response.Search);
        Assert.Null(response.FreeToolCalls);
        Assert.Null(response.WeeklyTokenLimit);
        Assert.Null(response.PlanWeeklyLimit);
        Assert.Null(response.RollingFiveHourLimit);
        Assert.Null(response.WeeklyLimit());
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
            "json: cannot unmarshal " + kind + " into Go value of type provider_quota.SyntheticUsageResponse",
            error.Message);
    }

    [Theory]
    [InlineData("""{"subscription":[]}""", "json: cannot unmarshal array into Go struct field SyntheticUsageResponse.subscription of type provider_quota.SyntheticSubscription")]
    [InlineData("""{"subscription":5}""", "json: cannot unmarshal number into Go struct field SyntheticUsageResponse.subscription of type provider_quota.SyntheticSubscription")]
    [InlineData("""{"subscription":{"limit":1.5}}""", "json: cannot unmarshal number 1.5 into Go struct field SyntheticSubscription.subscription.limit of type int64")]
    [InlineData("""{"subscription":{"limit":1e3}}""", "json: cannot unmarshal number 1e3 into Go struct field SyntheticSubscription.subscription.limit of type int64")]
    [InlineData("""{"subscription":{"limit":99999999999999999999}}""", "json: cannot unmarshal number 99999999999999999999 into Go struct field SyntheticSubscription.subscription.limit of type int64")]
    [InlineData("""{"subscription":{"requests":"x"}}""", "json: cannot unmarshal string into Go struct field SyntheticSubscription.subscription.requests of type int64")]
    [InlineData("""{"subscription":{"renewsAt":5}}""", "json: cannot unmarshal number into Go struct field SyntheticSubscription.subscription.renewsAt of type string")]
    [InlineData("""{"search":[]}""", "json: cannot unmarshal array into Go struct field SyntheticUsageResponse.search of type provider_quota.SyntheticSearch")]
    [InlineData("""{"search":{"hourly":[]}}""", "json: cannot unmarshal array into Go struct field SyntheticSearch.search.hourly of type provider_quota.SyntheticSearchHourly")]
    [InlineData("""{"search":{"hourly":{"limit":1.5}}}""", "json: cannot unmarshal number 1.5 into Go struct field SyntheticSearchHourly.search.hourly.limit of type int64")]
    [InlineData("""{"search":{"hourly":{"requests":true}}}""", "json: cannot unmarshal bool into Go struct field SyntheticSearchHourly.search.hourly.requests of type int64")]
    [InlineData("""{"search":{"hourly":{"renewsAt":[]}}}""", "json: cannot unmarshal array into Go struct field SyntheticSearchHourly.search.hourly.renewsAt of type string")]
    [InlineData("""{"freeToolCalls":[]}""", "json: cannot unmarshal array into Go struct field SyntheticUsageResponse.freeToolCalls of type provider_quota.SyntheticFreeToolCalls")]
    [InlineData("""{"freeToolCalls":{"limit":1e3}}""", "json: cannot unmarshal number 1e3 into Go struct field SyntheticFreeToolCalls.freeToolCalls.limit of type int64")]
    [InlineData("""{"freeToolCalls":{"renewsAt":0}}""", "json: cannot unmarshal number into Go struct field SyntheticFreeToolCalls.freeToolCalls.renewsAt of type string")]
    [InlineData("""{"weeklyTokenLimit":[]}""", "json: cannot unmarshal array into Go struct field SyntheticUsageResponse.weeklyTokenLimit of type provider_quota.SyntheticWeeklyTokenLimit")]
    [InlineData("""{"weeklyTokenLimit":{"nextRegenAt":5}}""", "json: cannot unmarshal number into Go struct field SyntheticWeeklyTokenLimit.weeklyTokenLimit.nextRegenAt of type string")]
    [InlineData("""{"weeklyTokenLimit":{"percentRemaining":"x"}}""", "json: cannot unmarshal string into Go struct field SyntheticWeeklyTokenLimit.weeklyTokenLimit.percentRemaining of type float64")]
    [InlineData("""{"weeklyTokenLimit":{"percentRemaining":1e400}}""", "json: cannot unmarshal number 1e400 into Go struct field SyntheticWeeklyTokenLimit.weeklyTokenLimit.percentRemaining of type float64")]
    [InlineData("""{"weeklyTokenLimit":{"maxCredits":5}}""", "json: cannot unmarshal number into Go struct field SyntheticWeeklyTokenLimit.weeklyTokenLimit.maxCredits of type string")]
    [InlineData("""{"weeklyTokenLimit":{"remainingCredits":false}}""", "json: cannot unmarshal bool into Go struct field SyntheticWeeklyTokenLimit.weeklyTokenLimit.remainingCredits of type string")]
    [InlineData("""{"planWeeklyLimit":[]}""", "json: cannot unmarshal array into Go struct field SyntheticUsageResponse.planWeeklyLimit of type provider_quota.SyntheticPlanWeeklyLimit")]
    [InlineData("""{"planWeeklyLimit":{"nextRegenPercent":"x"}}""", "json: cannot unmarshal string into Go struct field SyntheticPlanWeeklyLimit.planWeeklyLimit.nextRegenPercent of type float64")]
    [InlineData("""{"rollingFiveHourLimit":[]}""", "json: cannot unmarshal array into Go struct field SyntheticUsageResponse.rollingFiveHourLimit of type provider_quota.SyntheticRollingFiveHourLimit")]
    [InlineData("""{"rollingFiveHourLimit":{"tickPercent":"x"}}""", "json: cannot unmarshal string into Go struct field SyntheticRollingFiveHourLimit.rollingFiveHourLimit.tickPercent of type float64")]
    [InlineData("""{"rollingFiveHourLimit":{"remaining":{}}}""", "json: cannot unmarshal object into Go struct field SyntheticRollingFiveHourLimit.rollingFiveHourLimit.remaining of type float64")]
    [InlineData("""{"rollingFiveHourLimit":{"max":[]}}""", "json: cannot unmarshal array into Go struct field SyntheticRollingFiveHourLimit.rollingFiveHourLimit.max of type float64")]
    [InlineData("""{"rollingFiveHourLimit":{"limited":"yes"}}""", "json: cannot unmarshal string into Go struct field SyntheticRollingFiveHourLimit.rollingFiveHourLimit.limited of type bool")]
    [InlineData("""{"rollingFiveHourLimit":{"nextTickAt":5}}""", "json: cannot unmarshal number into Go struct field SyntheticRollingFiveHourLimit.rollingFiveHourLimit.nextTickAt of type string")]
    public void ParseUsageResponseReportsGoTypeMismatches(string json, string expectedMessage)
    {
        var error = Assert.Throws<JsonException>(() => ParseUsageResponse(json));

        Assert.Equal(expectedMessage, error.Message);
    }

    [Fact]
    public void ParseUsageResponseMergesDuplicateAndCaseFoldedKeys()
    {
        var response = ParseUsageResponse(
            """{"subscription":{"limit":1},"Subscription":{"requests":2},"SEARCH":{"HOURLY":{"LIMIT":3,"ignored":true}},"weeklyTokenLimit":{"percentRemaining":10},"weeklytokenlimit":{"maxCredits":"$1"}}""");

        Assert.NotNull(response.Subscription);
        Assert.Equal(1, response.Subscription!.Limit);
        Assert.Equal(2, response.Subscription.Requests);
        Assert.NotNull(response.Search?.Hourly);
        Assert.Equal(3, response.Search!.Hourly!.Limit);
        Assert.Equal(10.0, response.WeeklyTokenLimit!.PercentRemaining!.Value, 9);
        Assert.Equal("$1", response.WeeklyTokenLimit.MaxCredits);
    }

    [Fact]
    public void ParseUsageResponseResetsObjectsToNullOnExplicitNull()
    {
        var response = ParseUsageResponse(
            """{"subscription":{"limit":1},"subscription":null,"search":{"hourly":{"limit":2}},"SEARCH":null}""");

        Assert.Null(response.Subscription);
        Assert.Null(response.Search);
    }

    [Fact]
    public void ParseUsageResponseIgnoresUnknownFields()
    {
        var response = ParseUsageResponse(
            """{"unknown":{"a":1},"another":[1,2],"subscription":{"limit":7,"extra":"x"}}""");

        Assert.Equal(7, response.Subscription!.Limit);
    }

    [Fact]
    public void ParseResponseOfAnEmptyObjectIsUnknown()
    {
        var quota = Parse("{}");

        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.False(quota.Ready);
        Assert.Empty(quota.Limits);
        Assert.Null(quota.NextResetAt);
        Assert.Empty(quota.RawData);
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
        var checker = new SyntheticQuotaChecker(direct, factory, new AntigravityTestClock(Now));

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
        var checker = new SyntheticQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(Channel(settings: "{\"proxy\":{"), CancellationToken.None);

        Assert.Empty(factory.Requests);
        Assert.Single(direct.Requests);
    }

    [Fact]
    public async Task CheckQuotaUsesTheDirectExecutorWithoutASettingsProxy()
    {
        var direct = OkExecutor();
        var factory = new ScriptedProxyExecutorFactory();
        var checker = new SyntheticQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Empty(factory.Requests);
        Assert.Single(direct.Requests);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static SyntheticQuotaChecker CreateChecker(
        out ClaudeCodeQuotaCheckerTests.RecordingHttpHandler handler)
    {
        handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new SyntheticQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now));
    }

    private static SyntheticQuotaChecker CreateChecker(ScriptedExecutor executor)
        => new(executor, new ScriptedProxyExecutorFactory(), new AntigravityTestClock(Now));

    private static ChannelRecord Channel(
        string? credentials = null,
        string? baseUrl = null,
        string settings = "{}",
        string type = "openai")
        => new()
        {
            Type = type,
            BaseUrl = baseUrl,
            Credentials = credentials ?? """{"apiKey":"test-api-key"}""",
            Settings = settings,
        };

    private static QuotaData Parse(string json)
        => SyntheticQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(json), Now);

    private static SyntheticUsageResponse ParseUsageResponse(string json)
        => SyntheticQuotaChecker.ParseUsageResponse(Encoding.UTF8.GetBytes(json));

    private static ScriptedExecutor OkExecutor(string body = "{}") => new()
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
