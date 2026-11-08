using System.Globalization;
using System.Net;
using System.Text;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports internal/server/biz/provider_quota/minimax_checker.go together with
/// minimax_checker_test.go: the fixed token-plan endpoint built from the
/// channel base URL, the legacy-first API key selection, the 5h interval and
/// optional weekly window math (remaining percent + boost permille), the
/// shortest-reset rule and the Go JSON decoding edge cases (typed integers,
/// slice merge on duplicate keys, ASCII-folded field names).
/// </summary>
public sealed class MinimaxQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

    private const string DefaultBaseUrl = "https://www.minimaxi.com";

    private const string DefaultUsageUrl = "https://www.minimaxi.com/v1/token_plan/remains";

    private const string DefaultCredentials = """{"apiKey":"test-api-key"}""";

    /// <summary>TestMinimax_CheckQuota_HappyPath (minimax_checker_test.go:25-49).</summary>
    private const string ReferenceBody = """
        {
            "model_remains": [
                {
                    "model_name": "general",
                    "start_time": 1784304000000,
                    "end_time": 1784322000000,
                    "current_interval_status": 1,
                    "current_interval_remaining_percent": 98,
                    "current_interval_boost_permille": 0,
                    "weekly_start_time": 1783872000000,
                    "weekly_end_time": 1784476800000,
                    "current_weekly_status": 1,
                    "current_weekly_remaining_percent": 95,
                    "weekly_boost_permille": 0
                },
                {
                    "model_name": "video",
                    "current_interval_status": 1,
                    "current_interval_remaining_percent": 50,
                    "current_weekly_status": 1,
                    "current_weekly_remaining_percent": 50
                }
            ],
            "base_resp": {"status_code": 0, "status_msg": "success"}
        }
        """;

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaFetchesTheTokenPlanEndpointAndMapsTheReferencePayload()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(ReferenceBody);

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKey":" minimax-token "}"""),
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(DefaultUsageUrl, request.Url);
        Assert.Equal("Bearer minimax-token", request.Header("Authorization"));
        Assert.Equal("application/json", request.Header("Content-Type"));
        Assert.Equal(string.Empty, request.Header("Accept"));
        Assert.Equal(string.Empty, request.Body);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal("minimax", quota.ProviderType);

        var intervalReset = new DateTimeOffset(2026, 7, 17, 21, 0, 0, TimeSpan.Zero);
        var weeklyReset = new DateTimeOffset(2026, 7, 19, 16, 0, 0, TimeSpan.Zero);
        Assert.Equal(intervalReset, quota.NextResetAt);

        Assert.Equal(2, quota.Limits.Count);

        var fiveHours = quota.Limits[0];
        Assert.Equal(QuotaLimitTypes.Token, fiveHours.Type);
        Assert.Equal("5h", fiveHours.Window);
        Assert.Equal(ProviderQuotaStatuses.Available, fiveHours.Status);
        Assert.Equal(0.02, fiveHours.UsageRatio, 9);
        Assert.True(fiveHours.Ready);
        Assert.Equal(intervalReset, fiveHours.NextResetAt);
        Assert.Equal(new DateTimeOffset(2026, 7, 17, 16, 0, 0, TimeSpan.Zero), fiveHours.PeriodStart);

        var weekly = quota.Limits[1];
        Assert.Equal(QuotaLimitTypes.Token, weekly.Type);
        Assert.Equal("weekly", weekly.Window);
        Assert.Equal(ProviderQuotaStatuses.Available, weekly.Status);
        Assert.Equal(0.05, weekly.UsageRatio, 9);
        Assert.True(weekly.Ready);
        Assert.Equal(weeklyReset, weekly.NextResetAt);
        Assert.Equal(new DateTimeOffset(2026, 7, 12, 16, 0, 0, TimeSpan.Zero), weekly.PeriodStart);

        var rows = Assert.IsType<List<MinimaxModelRow>>(quota.RawData["rows"]);
        var row = Assert.Single(rows);
        Assert.Equal("general", row.ModelName);
        Assert.Equal(2.0, row.IntervalUsedPercent);
        Assert.Equal(100.0, row.IntervalTotalPercent);
        Assert.Equal(2.0, row.IntervalPercent);
        Assert.Equal(ProviderQuotaStatuses.Available, row.IntervalStatus);
        Assert.Equal("2026-07-17T21:00:00Z", row.IntervalResetAt);
        Assert.Equal(5.0, row.WeeklyUsedPercent);
        Assert.Equal(100.0, row.WeeklyTotalPercent);
        Assert.Equal(5.0, row.WeeklyPercent);
        Assert.Equal(ProviderQuotaStatuses.Available, row.WeeklyStatus);
        Assert.Equal("2026-07-19T16:00:00Z", row.WeeklyResetAt);
        Assert.Equal(0, row.WeeklyBoostPermille);

        Assert.Equal(
            """{"rows":[{"modelName":"general","intervalUsedPercent":2,"intervalTotalPercent":100,"intervalPercent":2,"intervalStatus":"available","intervalResetAt":"2026-07-17T21:00:00Z","weeklyUsedPercent":5,"weeklyTotalPercent":100,"weeklyPercent":5,"weeklyStatus":"available","weeklyResetAt":"2026-07-19T16:00:00Z"}]}""",
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
        Assert.Equal(DefaultUsageUrl, request.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(request.Headers, "Accept"));
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
    // Usage URL building (TestBuildMinimaxQuotaURL)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("", "https://www.minimaxi.com/v1/token_plan/remains")]
    [InlineData("https://custom.example.com", "https://custom.example.com/v1/token_plan/remains")]
    [InlineData("https://custom.example.com/v1", "https://custom.example.com/v1/token_plan/remains")]
    [InlineData("not a URL", "https://www.minimaxi.com/v1/token_plan/remains")]
    // Reference behaviour (minimax_checker.go:102-114) beyond the reference test.
    [InlineData("   ", "https://www.minimaxi.com/v1/token_plan/remains")]
    [InlineData(" https://custom.example.com ", "https://custom.example.com/v1/token_plan/remains")]
    [InlineData("HTTPS://Example.COM", "https://Example.COM/v1/token_plan/remains")]
    [InlineData("https://custom.example.com/api?x=1#f", "https://custom.example.com/v1/token_plan/remains")]
    [InlineData("https://user:pass@custom.example.com", "https://custom.example.com/v1/token_plan/remains")]
    [InlineData("https://user:pa%41ss@custom.example.com/path", "https://custom.example.com/v1/token_plan/remains")]
    [InlineData("http://localhost:8080/base", "http://localhost:8080/v1/token_plan/remains")]
    [InlineData("https://[2001:db8::1]:8443/x", "https://[2001:db8::1]:8443/v1/token_plan/remains")]
    [InlineData("ftp://example.com", "ftp://example.com/v1/token_plan/remains")]
    [InlineData("custom://host", "custom://host/v1/token_plan/remains")]
    [InlineData("https://", "https://www.minimaxi.com/v1/token_plan/remains")]
    [InlineData("://example.com", "https://www.minimaxi.com/v1/token_plan/remains")]
    [InlineData("https://exa mple.com", "https://www.minimaxi.com/v1/token_plan/remains")]
    [InlineData("https://example.com/%zz", "https://www.minimaxi.com/v1/token_plan/remains")]
    [InlineData("https://example.com:bad", "https://www.minimaxi.com/v1/token_plan/remains")]
    [InlineData("https://example.com:99999", "https://example.com:99999/v1/token_plan/remains")]
    [InlineData("https://example.com:", "https://example.com:/v1/token_plan/remains")]
    [InlineData("https://café.example", "https://café.example/v1/token_plan/remains")]
    [InlineData("https://exa%20mple.com", "https://www.minimaxi.com/v1/token_plan/remains")]
    [InlineData("//example.com", "https://www.minimaxi.com/v1/token_plan/remains")]
    [InlineData("https:example.com", "https://www.minimaxi.com/v1/token_plan/remains")]
    [InlineData("https:/example.com", "https://www.minimaxi.com/v1/token_plan/remains")]
    [InlineData("https://example.com#frag", "https://example.com/v1/token_plan/remains")]
    [InlineData(" https://example.com/a b ", "https://example.com/v1/token_plan/remains")]
    public void BuildUsageUrlMatchesGoUrl(string baseUrl, string expected)
    {
        Assert.Equal(expected, MinimaxQuotaChecker.BuildUsageUrl(baseUrl));
    }

    [Fact]
    public void BuildUsageUrlPreservesEscapedHostBytes()
    {
        // net/url decodes the host into UTF-8 bytes; the shared port
        // materializes one char per decoded byte, so the two inputs that Go
        // folds into the same Host differ in .NET's Unicode view (C216).
        Assert.Equal(
            "https://café.example/v1/token_plan/remains",
            MinimaxQuotaChecker.BuildUsageUrl("https://café.example"));
        Assert.Equal(
            "https://cafÃ©.example/v1/token_plan/remains",
            MinimaxQuotaChecker.BuildUsageUrl("https://caf%C3%A9.example"));
    }

    [Fact]
    public void UsagePathConstantsMatchTheReference()
    {
        Assert.Equal("https://www.minimaxi.com", MinimaxQuotaChecker.DefaultBaseUrl);
        Assert.Equal("/v1/token_plan/remains", MinimaxQuotaChecker.UsagePath);
    }

    // ------------------------------------------------------------------
    // Credentials
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaTrimsTheLegacyApiKey()
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"  minimax-token  "}"""),
            CancellationToken.None);

        Assert.Equal("minimax-token", Assert.Single(executor.Requests).Auth?.ApiKey);
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
        // minimax_checker.go:70-73 trims APIKeys[0] without skipping blanks,
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
    // Reference scenarios
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaAppliesTheWeeklyBoost()
    {
        // TestMinimax_CheckQuota_WithBoost (minimax_checker_test.go:83-133).
        const string body = """
            {
                "model_remains": [{
                    "model_name": "general",
                    "start_time": 4087929600000,
                    "end_time": 4087947600000,
                    "current_interval_status": 1,
                    "current_interval_remaining_percent": 100,
                    "current_interval_boost_permille": 0,
                    "weekly_start_time": 4087324800000,
                    "weekly_end_time": 4088534400000,
                    "current_weekly_status": 1,
                    "current_weekly_remaining_percent": 97,
                    "weekly_boost_permille": 1500
                }],
                "base_resp": {"status_code": 0, "status_msg": "success"}
            }
            """;
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(0.0, quota.Limits[0].UsageRatio, 9);
        Assert.Equal(0.02, quota.Limits[1].UsageRatio, 9);

        var row = Assert.Single(Assert.IsType<List<MinimaxModelRow>>(quota.RawData["rows"]));
        Assert.Equal(3.0, row.WeeklyUsedPercent);
        Assert.Equal(150.0, row.WeeklyTotalPercent);
        Assert.Equal(2.0, row.WeeklyPercent);
        Assert.Equal(1500, row.WeeklyBoostPermille);

        Assert.Equal(
            """{"rows":[{"modelName":"general","intervalUsedPercent":0,"intervalTotalPercent":100,"intervalPercent":0,"intervalStatus":"available","intervalResetAt":"2099-07-17T05:00:00Z","weeklyUsedPercent":3,"weeklyTotalPercent":150,"weeklyPercent":2,"weeklyStatus":"available","weeklyResetAt":"2099-07-24T00:00:00Z","weeklyBoostPermille":1500}]}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task CheckQuotaAppliesTheIntervalBoostAndSkipsWeeklyStatus3()
    {
        // TestMinimax_CheckQuota_5hBoost (minimax_checker_test.go:135-174).
        const string body = """
            {
                "model_remains": [{
                    "model_name": "general",
                    "current_interval_status": 1,
                    "current_interval_remaining_percent": 50,
                    "current_interval_boost_permille": 2000,
                    "current_weekly_status": 3,
                    "current_weekly_remaining_percent": 100,
                    "weekly_boost_permille": 0
                }],
                "base_resp": {"status_code": 0, "status_msg": "success"}
            }
            """;
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(0.25, limit.UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Available, limit.Status);

        var row = Assert.Single(Assert.IsType<List<MinimaxModelRow>>(quota.RawData["rows"]));
        Assert.Equal(50.0, row.IntervalUsedPercent);
        Assert.Equal(200.0, row.IntervalTotalPercent);
        Assert.Equal(25.0, row.IntervalPercent);
        Assert.Equal(string.Empty, row.WeeklyStatus);
        Assert.Equal(0.0, row.WeeklyUsedPercent);
        Assert.Equal(0.0, row.WeeklyTotalPercent);
        Assert.Equal(0.0, row.WeeklyPercent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task CheckQuotaSkipsTheWeeklyWindowWhenItsStatusIsNotOne(int weeklyStatus)
    {
        // TestMinimax_CheckQuota_WeeklyStatus3_SkipsWeekly (minimax_checker_test.go:176-209).
        var body = """
            {
                "model_remains": [{
                    "model_name": "general",
                    "current_interval_status": 1,
                    "current_interval_remaining_percent": 80,
                    "weekly_end_time": 4088534400000,
                    "current_weekly_status": __STATUS__,
                    "current_weekly_remaining_percent": 100
                }],
                "base_resp": {"status_code": 0, "status_msg": "success"}
            }
            """.Replace("__STATUS__", weeklyStatus.ToString(CultureInfo.InvariantCulture));
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Single(quota.Limits);
        Assert.Null(quota.NextResetAt);

        var row = Assert.Single(Assert.IsType<List<MinimaxModelRow>>(quota.RawData["rows"]));
        Assert.Equal(string.Empty, row.WeeklyStatus);
        Assert.Null(row.WeeklyResetAt);
        Assert.Equal(0.0, row.WeeklyTotalPercent);
    }

    [Fact]
    public async Task CheckQuotaReportsWarning()
    {
        // TestMinimax_CheckQuota_Warning (minimax_checker_test.go:211-241).
        const string body = """
            {
                "model_remains": [{
                    "model_name": "general",
                    "current_interval_status": 1,
                    "current_interval_remaining_percent": 10,
                    "current_weekly_status": 1,
                    "current_weekly_remaining_percent": 90
                }],
                "base_resp": {"status_code": 0, "status_msg": "success"}
            }
            """;
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Limits[0].Status);
        Assert.Equal(0.9, quota.Limits[0].UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[1].Status);
    }

    [Fact]
    public async Task CheckQuotaReportsExhaustedAtZeroRemaining()
    {
        // TestMinimax_CheckQuota_Exhausted (minimax_checker_test.go:243-273).
        const string body = """
            {
                "model_remains": [{
                    "model_name": "general",
                    "current_interval_status": 1,
                    "current_interval_remaining_percent": 0,
                    "current_weekly_status": 1,
                    "current_weekly_remaining_percent": 100
                }],
                "base_resp": {"status_code": 0, "status_msg": "success"}
            }
            """;
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal(1.0, quota.Limits[0].UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Limits[0].Status);
    }

    [Fact]
    public async Task CheckQuotaKeepsBoostedWeeklyUsageAvailable()
    {
        // TestMinimax_CheckQuota_ExhaustedByBoost (minimax_checker_test.go:275-307).
        const string body = """
            {
                "model_remains": [{
                    "model_name": "general",
                    "current_interval_status": 1,
                    "current_interval_remaining_percent": 100,
                    "current_weekly_status": 1,
                    "current_weekly_remaining_percent": 30,
                    "weekly_boost_permille": 1500
                }],
                "base_resp": {"status_code": 0, "status_msg": "success"}
            }
            """;
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[0].Status);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[1].Status);
        Assert.Equal(0.4666666666666667, quota.Limits[1].UsageRatio, 9);

        var row = Assert.Single(Assert.IsType<List<MinimaxModelRow>>(quota.RawData["rows"]));
        Assert.Equal(70.0, row.WeeklyUsedPercent);
        Assert.Equal(150.0, row.WeeklyTotalPercent);
        Assert.Equal(46.666666666666664, row.WeeklyPercent, 9);
    }

    [Fact]
    public async Task CheckQuotaTreatsApiStatus3AsExhausted()
    {
        // TestMinimax_CheckQuota_ExhaustedByAPIStatus (minimax_checker_test.go:309-339).
        const string body = """
            {
                "model_remains": [{
                    "model_name": "general",
                    "current_interval_status": 3,
                    "current_interval_remaining_percent": 100,
                    "current_weekly_status": 1,
                    "current_weekly_remaining_percent": 100
                }],
                "base_resp": {"status_code": 0, "status_msg": "success"}
            }
            """;
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Limits[0].Status);
        Assert.Equal(0.0, quota.Limits[0].UsageRatio, 9);
    }

    [Fact]
    public async Task CheckQuotaSurfacesTheApiError()
    {
        // TestMinimax_CheckQuota_APIError (minimax_checker_test.go:371-393).
        const string body = """
            {
                "model_remains": [],
                "base_resp": {"status_code": 1001, "status_msg": "invalid api key"}
            }
            """;
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("minimax API error: invalid api key (code 1001)", error.Message);
    }

    [Fact]
    public void ParseResponseChecksTheApiErrorBeforeTheModelList()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ParseResponse("""{"model_remains":[],"base_resp":{"status_code":7,"status_msg":"denied"}}"""));

        Assert.Equal("minimax API error: denied (code 7)", error.Message);
    }

    [Fact]
    public async Task CheckQuotaUsesTheEarliestReset()
    {
        // TestMinimax_CheckQuota_WithResetTime (minimax_checker_test.go:472-506).
        const string body = """
            {
                "model_remains": [{
                    "model_name": "general",
                    "start_time": 4087929600000,
                    "end_time": 4087947600000,
                    "current_interval_status": 1,
                    "current_interval_remaining_percent": 100,
                    "weekly_start_time": 4087324800000,
                    "weekly_end_time": 4088534400000,
                    "current_weekly_status": 1,
                    "current_weekly_remaining_percent": 100
                }],
                "base_resp": {"status_code": 0, "status_msg": "success"}
            }
            """;
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var intervalReset = DateTimeOffset.FromUnixTimeMilliseconds(4087947600000);
        var weeklyReset = DateTimeOffset.FromUnixTimeMilliseconds(4088534400000);
        Assert.Equal(intervalReset, quota.NextResetAt);
        Assert.Equal(intervalReset, quota.Limits[0].NextResetAt);
        Assert.Equal(weeklyReset, quota.Limits[1].NextResetAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(4087929600000), quota.Limits[0].PeriodStart);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(4087324800000), quota.Limits[1].PeriodStart);

        var row = Assert.Single(Assert.IsType<List<MinimaxModelRow>>(quota.RawData["rows"]));
        Assert.Equal("2099-07-17T05:00:00Z", row.IntervalResetAt);
        Assert.Equal("2099-07-24T00:00:00Z", row.WeeklyResetAt);
    }

    [Fact]
    public void ParseResponseAcceptsTheFirstGeneralAmongOtherModels()
    {
        // TestMinimax_CheckQuota_OnlyVideo_ReturnsError uses a video-only list;
        // with a later general entry the first one wins and the rest is ignored.
        var quota = ParseResponse(
            """
            {"model_remains":[
                {"model_name":"video","current_interval_status":1,"current_interval_remaining_percent":50},
                {"model_name":"general","current_interval_status":1,"current_interval_remaining_percent":100,"current_weekly_status":3},
                {"model_name":"general","current_interval_status":3,"current_interval_remaining_percent":0}
            ],"base_resp":{"status_code":0}}
            """);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(0.0, limit.UsageRatio, 9);
        var row = Assert.Single(Assert.IsType<List<MinimaxModelRow>>(quota.RawData["rows"]));
        Assert.Equal("general", row.ModelName);
    }

    // ------------------------------------------------------------------
    // Window math
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(3, 0.0, "exhausted")]
    [InlineData(3, 2.0, "exhausted")]
    [InlineData(1, 0.0, "available")]
    [InlineData(1, 0.79, "available")]
    [InlineData(1, 0.8, "warning")]
    [InlineData(1, 0.999, "warning")]
    [InlineData(1, 1.0, "exhausted")]
    [InlineData(2, 0.0, "unknown")]
    [InlineData(0, 0.0, "unknown")]
    [InlineData(2, 0.8, "warning")]
    [InlineData(0, 1.5, "exhausted")]
    public void StatusForRatioFollowsTheApiStatusAndThresholds(long apiStatus, double ratio, string expected)
    {
        Assert.Equal(expected, MinimaxQuotaChecker.StatusForRatio(apiStatus, ratio));
    }

    [Theory]
    [InlineData(-5L, 100.0)]
    [InlineData(0L, 100.0)]
    [InlineData(1L, 0.1)]
    [InlineData(10L, 1.0)]
    [InlineData(1500L, 150.0)]
    [InlineData(2000L, 200.0)]
    public void TotalPercentConvertsTheBoostPermille(long boostPermille, double expected)
    {
        Assert.Equal(expected, MinimaxQuotaChecker.TotalPercent(boostPermille), 9);
    }

    [Theory]
    [InlineData(0L, null)]
    [InlineData(-1L, null)]
    [InlineData(4087929600000L, "2099-07-17T00:00:00Z")]
    public void PeriodStartRequiresAPositiveEpoch(long startMillis, string? expectedText)
    {
        var start = MinimaxQuotaChecker.PeriodStart(startMillis);

        Assert.Equal(
            expectedText,
            start is null ? null : MinimaxQuotaChecker.FormatRfc3339(start.Value));
    }

    [Theory]
    [InlineData("available", 0)]
    [InlineData("warning", 1)]
    [InlineData("exhausted", 2)]
    [InlineData("unknown", 0)]
    [InlineData("", 0)]
    [InlineData("other", 0)]
    public void CheckerStatusRankOrdersLikeTheReference(string status, int expected)
    {
        Assert.Equal(expected, MinimaxQuotaChecker.CheckerStatusRank(status));
    }

    [Theory]
    [InlineData("available", "warning", "warning")]
    [InlineData("warning", "available", "warning")]
    [InlineData("available", "exhausted", "exhausted")]
    [InlineData("exhausted", "available", "exhausted")]
    [InlineData("available", "unknown", "available")]
    [InlineData("unknown", "available", "available")]
    [InlineData("warning", "unknown", "warning")]
    public void WorseStatusKeepsTheWorseCandidate(string current, string candidate, string expected)
    {
        Assert.Equal(expected, MinimaxQuotaChecker.WorseStatus(current, candidate));
    }

    [Fact]
    public async Task CheckQuotaDropsLimitsWithANegativeRatioButKeepsTheRawRow()
    {
        // remaining > 100 yields used < 0; the reference passes the negative
        // ratio to NormalizeQuotaData, which drops the limit.
        const string body = """
            {"model_remains":[{
                "model_name":"general",
                "current_interval_status":1,
                "current_interval_remaining_percent":120,
                "current_weekly_status":3
            }],"base_resp":{"status_code":0}}
            """;
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Empty(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        var row = Assert.Single(Assert.IsType<List<MinimaxModelRow>>(quota.RawData["rows"]));
        Assert.Equal(-20.0, row.IntervalUsedPercent);
        Assert.Equal(-20.0, row.IntervalPercent);
        Assert.Equal(ProviderQuotaStatuses.Available, row.IntervalStatus);
    }

    [Fact]
    public async Task CheckQuotaClampsRatiosAboveOneThroughNormalization()
    {
        const string body = """
            {"model_remains":[{
                "model_name":"general",
                "current_interval_status":1,
                "current_interval_remaining_percent":-20,
                "current_weekly_status":3
            }],"base_resp":{"status_code":0}}
            """;
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(1.0, limit.UsageRatio);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);
    }

    [Fact]
    public async Task CheckQuotaMarksAnUnknownApiStatus()
    {
        const string body = """
            {"model_remains":[{
                "model_name":"general",
                "current_interval_status":0,
                "current_interval_remaining_percent":100,
                "current_weekly_status":3
            }],"base_resp":{"status_code":0}}
            """;
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Unknown, limit.Status);
        Assert.False(limit.Ready);
    }

    [Fact]
    public async Task CheckQuotaWithoutStartTimesLeavesThePeriodStartsUnset()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok("""{"model_remains":[{"model_name":"general","current_interval_status":1,"current_interval_remaining_percent":100,"current_weekly_status":1,"current_weekly_remaining_percent":100}],"base_resp":{"status_code":0}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.All(quota.Limits, limit => Assert.Null(limit.PeriodStart));
        Assert.All(quota.Limits, limit => Assert.Null(limit.NextResetAt));
        Assert.Null(quota.NextResetAt);
    }

    [Fact]
    public async Task CheckQuotaClearsPastResetsThroughNormalizationButKeepsTheRawRow()
    {
        const string body = """
            {"model_remains":[{
                "model_name":"general",
                "start_time":1000,
                "end_time":1000,
                "current_interval_status":1,
                "current_interval_remaining_percent":100,
                "current_weekly_status":3
            }],"base_resp":{"status_code":0}}
            """;
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var limit = Assert.Single(quota.Limits);
        Assert.Null(limit.NextResetAt);
        Assert.Null(limit.PeriodStart);
        Assert.Null(quota.NextResetAt);

        var row = Assert.Single(Assert.IsType<List<MinimaxModelRow>>(quota.RawData["rows"]));
        Assert.Equal("1970-01-01T00:00:01Z", row.IntervalResetAt);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    // 253402300800000 ms is year 10000 in Go but beyond DateTimeOffset.MaxValue;
    // the port treats the reset as unrepresentable (C216).
    [InlineData(253402300800000L)]
    public async Task CheckQuotaSkipsUnrepresentableOrNonPositiveIntervalResets(long endTime)
    {
        var body = """
            {"model_remains":[{
                "model_name":"general",
                "end_time":__END_TIME__,
                "current_interval_status":1,
                "current_interval_remaining_percent":100,
                "current_weekly_status":3
            }],"base_resp":{"status_code":0}}
            """.Replace("__END_TIME__", endTime.ToString(CultureInfo.InvariantCulture));
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Null(quota.NextResetAt);
        var limit = Assert.Single(quota.Limits);
        Assert.Null(limit.NextResetAt);
        var row = Assert.Single(Assert.IsType<List<MinimaxModelRow>>(quota.RawData["rows"]));
        Assert.Null(row.IntervalResetAt);
    }

    // ------------------------------------------------------------------
    // Response validation
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("""{"model_remains":[],"base_resp":{"status_code":0}}""")]
    [InlineData("""{"model_remains":null,"base_resp":{"status_code":0}}""")]
    public void ParseResponseRejectsAnEmptyModelRemainsList(string body)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ParseResponse(body));

        Assert.Equal("minimax quota response contains no model remains", error.Message);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("General")]
    [InlineData("general ")]
    [InlineData("")]
    public void ParseResponseRequiresTheExactGeneralModel(string modelName)
    {
        // TestMinimax_CheckQuota_OnlyVideo_ReturnsError (minimax_checker_test.go:341-369).
        var body = """
            {"model_remains":[{"model_name":__NAME__}],"base_resp":{"status_code":0}}
            """.Replace("__NAME__", System.Text.Json.JsonSerializer.Serialize(modelName));

        var error = Assert.Throws<InvalidOperationException>(() => ParseResponse(body));

        Assert.Equal("minimax quota response contains no general model", error.Message);
    }

    // ------------------------------------------------------------------
    // Go decoder semantics
    // ------------------------------------------------------------------

    [Fact]
    public void ParseUsageResponseAcceptsANullRoot()
    {
        var response = ParseUsageResponse("null");

        Assert.Null(response.ModelRemains);
        Assert.Equal(0, response.BaseResp.StatusCode);
        Assert.Equal(string.Empty, response.BaseResp.StatusMsg);
    }

    [Theory]
    [InlineData("[]", "json: cannot unmarshal array into Go value of type provider_quota.minimaxResponse")]
    [InlineData("\"x\"", "json: cannot unmarshal string into Go value of type provider_quota.minimaxResponse")]
    [InlineData("1", "json: cannot unmarshal number 1 into Go value of type provider_quota.minimaxResponse")]
    [InlineData("1.5", "json: cannot unmarshal number 1.5 into Go value of type provider_quota.minimaxResponse")]
    [InlineData("true", "json: cannot unmarshal bool into Go value of type provider_quota.minimaxResponse")]
    [InlineData("false", "json: cannot unmarshal bool into Go value of type provider_quota.minimaxResponse")]
    public void ParseUsageResponseRejectsNonObjectRoots(string json, string message)
    {
        var error = Assert.Throws<System.Text.Json.JsonException>(() => ParseUsageResponse(json));

        Assert.Equal(message, error.Message);
    }

    [Theory]
    [InlineData("""{"model_remains":{}}""", "json: cannot unmarshal object into Go struct field minimaxResponse.model_remains of type []provider_quota.minimaxModelRemain")]
    [InlineData("""{"model_remains":1}""", "json: cannot unmarshal number 1 into Go struct field minimaxResponse.model_remains of type []provider_quota.minimaxModelRemain")]
    [InlineData("""{"model_remains":[1]}""", "json: cannot unmarshal number 1 into Go struct field minimaxResponse.model_remains of type provider_quota.minimaxModelRemain")]
    [InlineData("""{"model_remains":["x"]}""", "json: cannot unmarshal string into Go struct field minimaxResponse.model_remains of type provider_quota.minimaxModelRemain")]
    [InlineData("""{"base_resp":[]}""", "json: cannot unmarshal array into Go struct field minimaxResponse.base_resp of type provider_quota.minimaxBaseResp")]
    [InlineData("""{"base_resp":"x"}""", "json: cannot unmarshal string into Go struct field minimaxResponse.base_resp of type provider_quota.minimaxBaseResp")]
    [InlineData("""{"base_resp":{"status_code":"1"}}""", "json: cannot unmarshal string into Go struct field minimaxBaseResp.base_resp.status_code of type int")]
    [InlineData("""{"base_resp":{"status_code":1.5}}""", "json: cannot unmarshal number 1.5 into Go struct field minimaxBaseResp.base_resp.status_code of type int")]
    [InlineData("""{"base_resp":{"status_code":1e3}}""", "json: cannot unmarshal number 1e3 into Go struct field minimaxBaseResp.base_resp.status_code of type int")]
    [InlineData("""{"base_resp":{"status_msg":1}}""", "json: cannot unmarshal number 1 into Go struct field minimaxBaseResp.base_resp.status_msg of type string")]
    [InlineData("""{"model_remains":[{"model_name":1}]}""", "json: cannot unmarshal number 1 into Go struct field minimaxModelRemain.model_remains.model_name of type string")]
    [InlineData("""{"model_remains":[{"start_time":1.5}]}""", "json: cannot unmarshal number 1.5 into Go struct field minimaxModelRemain.model_remains.start_time of type int64")]
    [InlineData("""{"model_remains":[{"remains_time":"5"}]}""", "json: cannot unmarshal string into Go struct field minimaxModelRemain.model_remains.remains_time of type int64")]
    [InlineData("""{"model_remains":[{"current_interval_status":"1"}]}""", "json: cannot unmarshal string into Go struct field minimaxModelRemain.model_remains.current_interval_status of type int")]
    [InlineData("""{"model_remains":[{"current_weekly_remaining_percent":1e3}]}""", "json: cannot unmarshal number 1e3 into Go struct field minimaxModelRemain.model_remains.current_weekly_remaining_percent of type int")]
    [InlineData("""{"model_remains":[{"weekly_boost_permille":9223372036854775808}]}""", "json: cannot unmarshal number 9223372036854775808 into Go struct field minimaxModelRemain.model_remains.weekly_boost_permille of type int")]
    [InlineData("""{"model_remains":[{"remains_time":1e400}]}""", "json: cannot unmarshal number 1e400 into Go struct field minimaxModelRemain.model_remains.remains_time of type int64")]
    [InlineData("""{"model_remains":[{"current_interval_status":true}]}""", "json: cannot unmarshal bool into Go struct field minimaxModelRemain.model_remains.current_interval_status of type int")]
    public void ParseUsageResponseRejectsWrongFieldTypes(string json, string message)
    {
        var error = Assert.Throws<System.Text.Json.JsonException>(() => ParseUsageResponse(json));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void ParseUsageResponseTreatsNullFieldsAsNoOps()
    {
        const string body = """
            {"model_remains":[{
                "model_name":"general",
                "start_time":null,
                "end_time":null,
                "remains_time":null,
                "current_interval_status":null,
                "current_interval_remaining_percent":null,
                "current_interval_boost_permille":null,
                "weekly_start_time":null,
                "weekly_end_time":null,
                "weekly_remains_time":null,
                "current_weekly_status":null,
                "current_weekly_remaining_percent":null,
                "weekly_boost_permille":null
            }],"base_resp":null}
            """;

        var response = ParseUsageResponse(body);

        var remain = Assert.Single(response.ModelRemains!);
        Assert.Equal("general", remain.ModelName);
        Assert.Equal(0, remain.StartTime);
        Assert.Equal(0, remain.EndTime);
        Assert.Equal(0, remain.RemainsTime);
        Assert.Equal(0, remain.CurrentIntervalStatus);
        Assert.Equal(0, remain.CurrentIntervalRemainingPercent);
        Assert.Equal(0, remain.CurrentIntervalBoostPermille);
        Assert.Equal(0, remain.WeeklyStartTime);
        Assert.Equal(0, remain.WeeklyEndTime);
        Assert.Equal(0, remain.WeeklyRemainsTime);
        Assert.Equal(0, remain.CurrentWeeklyStatus);
        Assert.Equal(0, remain.CurrentWeeklyRemainingPercent);
        Assert.Equal(0, remain.WeeklyBoostPermille);
        Assert.Equal(0, response.BaseResp.StatusCode);
        Assert.Equal(string.Empty, response.BaseResp.StatusMsg);

        // A later null does not erase a value decoded from an earlier duplicate.
        var kept = ParseUsageResponse(
            """{"base_resp":{"status_code":5,"status_msg":"x"},"base_resp":null}""");
        Assert.Equal(5, kept.BaseResp.StatusCode);
        Assert.Equal("x", kept.BaseResp.StatusMsg);
    }

    [Fact]
    public void ParseUsageResponseResetsTheModelListOnExplicitNull()
    {
        var response = ParseUsageResponse(
            """{"model_remains":[{"model_name":"general"}],"model_remains":null}""");

        Assert.Null(response.ModelRemains);
    }

    [Fact]
    public void ParseUsageResponseMergesDuplicateModelRemainsKeys()
    {
        // Go reuses the backing array: element structs merge field by field,
        // extra elements are truncated and missing ones appended.
        var merged = ParseUsageResponse(
            """
            {"model_remains":[{"model_name":"general","start_time":1},{"model_name":"video"}],
             "model_remains":[{"end_time":2}]}
            """);

        var remain = Assert.Single(merged.ModelRemains!);
        Assert.Equal("general", remain.ModelName);
        Assert.Equal(1, remain.StartTime);
        Assert.Equal(2, remain.EndTime);

        var grown = ParseUsageResponse(
            """
            {"model_remains":[{"model_name":"a"}],
             "model_remains":[{"end_time":1},{"model_name":"b"}]}
            """);

        Assert.Equal(2, grown.ModelRemains!.Count);
        Assert.Equal("a", grown.ModelRemains[0].ModelName);
        Assert.Equal(1, grown.ModelRemains[0].EndTime);
        Assert.Equal("b", grown.ModelRemains[1].ModelName);
    }

    [Fact]
    public void ParseUsageResponseMergesDuplicateFieldsAndFoldsAsciiCase()
    {
        var response = ParseUsageResponse(
            """
            {"model_remains":[{"model_name":"first","MODEL_NAME":"second","start_time":1,"START_TIME":2}],
             "base_resp":{"status_code":1,"STATUS_CODE":2},
             "BASE_RESP":{"status_msg":"merged"}}
            """);

        var remain = Assert.Single(response.ModelRemains!);
        Assert.Equal("second", remain.ModelName);
        Assert.Equal(2, remain.StartTime);
        Assert.Equal(2, response.BaseResp.StatusCode);
        Assert.Equal("merged", response.BaseResp.StatusMsg);
    }

    [Fact]
    public void ParseUsageResponseIgnoresUnknownKeys()
    {
        var response = ParseUsageResponse(
            """
            {"unknown":{"a":1},
             "model_remains":[{"model_name":"general","what":1,"future":{"x":true}}],
             "base_resp":{"status_code":0,"extra":null}}
            """);

        Assert.Equal("general", Assert.Single(response.ModelRemains!).ModelName);
        Assert.Equal(0, response.BaseResp.StatusCode);
    }

    [Theory]
    [InlineData(9223372036854775807L, -9223372036854775808L)]
    public void ParseUsageResponseAcceptsLongIntegerBoundaries(long max, long min)
    {
        var body = $$"""{"model_remains":[{"remains_time":{{max}},"weekly_remains_time":{{min}}}]}""";

        var remain = Assert.Single(ParseUsageResponse(body).ModelRemains!);

        Assert.Equal(max, remain.RemainsTime);
        Assert.Equal(min, remain.WeeklyRemainsTime);
    }

    [Fact]
    public void ParseUsageResponseReplacesInvalidUtf8LikeGo()
    {
        var prefix = Encoding.UTF8.GetBytes("""{"model_remains":[{"model_name":"gen""");
        var suffix = Encoding.UTF8.GetBytes("""eral"}]}""");
        byte[] body = [.. prefix, 0xFF, .. suffix];

        var remain = Assert.Single(ParseUsageResponse(body).ModelRemains!);

        Assert.Equal("gen" + char.ConvertFromUtf32(0xFFFD) + "eral", remain.ModelName);
    }

    [Fact]
    public void ParseUsageResponseKeepsNullElementsAsZeroValues()
    {
        var response = ParseUsageResponse(
            """{"model_remains":[null,{"model_name":"general"}]}""");

        Assert.Equal(2, response.ModelRemains!.Count);
        Assert.Equal(string.Empty, response.ModelRemains[0].ModelName);
        Assert.Equal("general", response.ModelRemains[1].ModelName);
    }

    [Fact]
    public void ParseResponseWrapsDecoderErrorsWithTheParsePrefix()
    {
        var error = Assert.Throws<WrappedException>(
            () => ParseResponse("""{"model_remains":{}}"""));

        Assert.Equal(
            "failed to parse minimax quota response: "
            + "json: cannot unmarshal object into Go struct field "
            + "minimaxResponse.model_remains of type []provider_quota.minimaxModelRemain",
            error.Message);
    }

    // ------------------------------------------------------------------
    // Executor errors and proxy routing
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"model_remains":""")]
    public async Task CheckQuotaWithMalformedJsonReportsTheParsePrefix(string body)
    {
        // TestMinimax_CheckQuota_MalformedJSON (minimax_checker_test.go:436-454).
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.StartsWith("failed to parse minimax quota response: ", error.Message);
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

        Assert.StartsWith("minimax quota request failed: ", error.Message);
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

        Assert.Equal("minimax quota request failed: connection reset", error.Message);
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
                Body = Encoding.UTF8.GetBytes(ReferenceBody),
            },
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal(2, quota.Limits.Count);
    }

    [Fact]
    public async Task CheckQuotaRoutesThroughTheChannelProxyExecutor()
    {
        var direct = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new InvalidOperationException("direct executor used"),
        };
        var proxied = OkExecutor();
        var factory = new ScriptedProxyExecutorFactory { Executor = proxied };
        var checker = new MinimaxQuotaChecker(direct, factory, new AntigravityTestClock(Now));

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
        var checker = new MinimaxQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(Channel(settings: "{\"proxy\":{"), CancellationToken.None);

        Assert.Empty(factory.Requests);
        Assert.Single(direct.Requests);
    }

    [Fact]
    public async Task CheckQuotaUsesTheDirectExecutorWithoutASettingsProxy()
    {
        var direct = OkExecutor();
        var factory = new ScriptedProxyExecutorFactory();
        var checker = new MinimaxQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Empty(factory.Requests);
        Assert.Single(direct.Requests);
    }

    // ------------------------------------------------------------------
    // Capability
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("minimax", true)]
    [InlineData("minimax_anthropic", true)]
    [InlineData("openai", false)]
    [InlineData("moonshot_coding", false)]
    [InlineData("anthropic", false)]
    [InlineData("", false)]
    public void SupportsChannelOnlyMatchesTheMiniMaxTypes(string channelType, bool expected)
    {
        // TestMinimax_SupportsChannel (minimax_checker_test.go:463-470).
        var checker = CreateChecker(new ScriptedExecutor());

        Assert.Equal(expected, checker.SupportsChannel(new ChannelRecord { Type = channelType }));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static MinimaxQuotaChecker CreateChecker(
        out ClaudeCodeQuotaCheckerTests.RecordingHttpHandler handler)
    {
        handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new MinimaxQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now));
    }

    private static MinimaxQuotaChecker CreateChecker(ScriptedExecutor executor)
        => new(executor, new ScriptedProxyExecutorFactory(), new AntigravityTestClock(Now));

    private static ChannelRecord Channel(
        string? credentials = null,
        string settings = "{}",
        string type = "minimax",
        string? baseUrl = DefaultBaseUrl)
        => new()
        {
            Type = type,
            BaseUrl = baseUrl,
            Credentials = credentials ?? DefaultCredentials,
            Settings = settings,
        };

    private static QuotaData ParseResponse(string json)
        => MinimaxQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(json), Now);

    private static MinimaxResponse ParseUsageResponse(string json)
        => MinimaxQuotaChecker.ParseUsageResponse(Encoding.UTF8.GetBytes(json));

    private static MinimaxResponse ParseUsageResponse(byte[] body)
        => MinimaxQuotaChecker.ParseUsageResponse(body);

    private static ScriptedExecutor OkExecutor() => new()
    {
        OnDo = (_, _, _) => Ok(ReferenceBody),
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
