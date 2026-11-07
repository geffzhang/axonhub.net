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
/// Ports internal/server/biz/provider_quota/zenmux_checker.go together with
/// the zenmux rows of provider_contract_fixtures_test.go and
/// provider_contract_test.go: the fixed management subscription request, the
/// trimmed management key, the 5-hour and 7-day windows, the account-status
/// escalation (unknown outranks exhausted), the monthly metadata kept in raw
/// data only, and the exact Go decoder and time.Parse error texts.
/// </summary>
public sealed class ZenmuxQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private const string QuotaUrl = "https://zenmux.ai/api/v1/management/subscription/detail";

    private const string ManagementKey = "management-key";

    private const string FullBody = """
        {"success":true,"data":{"plan":{"tier":"pro","amount_usd":200,"interval":"month","expires_at":"2099-10-01T00:00:00Z"},"account_status":"healthy","quota_5_hour":{"usage_percentage":0.4,"resets_at":"2099-09-03T15:00:00Z","max_flows":1000,"used_flows":400,"remaining_flows":600,"used_value_usd":40,"max_value_usd":100},"quota_7_day":{"usage_percentage":0.6,"resets_at":"2099-09-10T10:00:00Z","max_flows":10000,"used_flows":6000,"remaining_flows":4000,"used_value_usd":120,"max_value_usd":200},"quota_monthly":{"max_flows":50000,"max_value_usd":500}}}
        """;

    private const string ContractFixtureBody = """
        {"success":true,"data":{"account_status":"healthy","quota_5_hour":{"usage_percentage":0.25,"resets_at":"2099-01-01T00:00:00Z"},"quota_7_day":{"usage_percentage":0.5,"resets_at":"2099-01-08T00:00:00Z"},"quota_monthly":{"max_flows":1000,"max_value_usd":50}}}
        """;

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaFetchesTheSubscriptionDetailAndMapsBothWindows()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(FullBody);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(QuotaUrl, request.Url);
        Assert.Equal("Bearer " + ManagementKey, request.Header("Authorization"));
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Equal(string.Empty, request.Header("Content-Type"));
        Assert.Equal(string.Empty, request.Body);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal("zenmux", quota.ProviderType);
        Assert.True(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);

        // Only the 5-hour and 7-day windows become limits, in that order, each
        // with its own label and window length.
        Assert.Equal(QuotaLimitTypes.Token, quota.Limits[0].Type);
        Assert.Equal(QuotaWindows.FiveHours, quota.Limits[0].Window);
        Assert.Equal(0.4, quota.Limits[0].UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[0].Status);
        Assert.Equal(new DateTimeOffset(2099, 9, 3, 15, 0, 0, TimeSpan.Zero), quota.Limits[0].NextResetAt);
        Assert.Equal(new DateTimeOffset(2099, 9, 3, 10, 0, 0, TimeSpan.Zero), quota.Limits[0].PeriodStart);

        Assert.Equal(QuotaLimitTypes.Token, quota.Limits[1].Type);
        Assert.Equal(QuotaWindows.SevenDays, quota.Limits[1].Window);
        Assert.Equal(0.6, quota.Limits[1].UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[1].Status);
        Assert.Equal(new DateTimeOffset(2099, 9, 10, 10, 0, 0, TimeSpan.Zero), quota.Limits[1].NextResetAt);
        Assert.Equal(new DateTimeOffset(2099, 9, 3, 10, 0, 0, TimeSpan.Zero), quota.Limits[1].PeriodStart);

        // The earliest of the two resets wins.
        Assert.Equal(new DateTimeOffset(2099, 9, 3, 15, 0, 0, TimeSpan.Zero), quota.NextResetAt);

        Assert.Equal(
            """{"account_status":"healthy","plan":{"tier":"pro","amount_usd":200,"interval":"month","expires_at":"2099-10-01T00:00:00Z"},"quota_monthly":{"max_flows":50000,"max_value_usd":500}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task CheckQuotaCarriesOnlyTheBearerAuthOnTheRequestData()
    {
        var executor = OkExecutor();
        var checker = CreateChecker(executor);

        await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var request = Assert.Single(executor.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(QuotaUrl, request.Url);

        // The reference builds a bare GET: no Content-Type, and the auth lives
        // on the request object rather than in the header map.
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(request.Headers, "Authorization"));
        Assert.NotNull(request.Auth);
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal(ManagementKey, request.Auth.ApiKey);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task CheckQuotaMapsAnExhaustedFiveHourWindow()
    {
        var checker = CreateChecker(OkExecutor(Fixture("healthy", "1", "\"2099-09-03T15:00:00Z\"")));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Limits[0].Status);
        Assert.Equal(1.0, quota.Limits[0].UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[1].Status);
        Assert.Equal(new DateTimeOffset(2099, 9, 3, 15, 0, 0, TimeSpan.Zero), quota.Limits[0].NextResetAt);
    }

    [Fact]
    public async Task CheckQuotaMapsAWarningFiveHourWindow()
    {
        var checker = CreateChecker(OkExecutor(Fixture("healthy", "0.85", "\"2099-09-03T15:00:00Z\"")));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Limits[0].Status);
    }

    [Fact]
    public async Task CheckQuotaHandlesANullFiveHourReset()
    {
        var checker = CreateChecker(OkExecutor(Fixture("healthy", "0.2", "null")));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Null(quota.Limits[0].NextResetAt);
        Assert.Null(quota.Limits[0].PeriodStart);
        Assert.Equal(new DateTimeOffset(2099, 9, 10, 10, 0, 0, TimeSpan.Zero), quota.Limits[1].NextResetAt);

        // The 7-day reset becomes the earliest available reset.
        Assert.Equal(new DateTimeOffset(2099, 9, 10, 10, 0, 0, TimeSpan.Zero), quota.NextResetAt);
    }

    [Fact]
    public async Task CheckQuotaMapsSuspendedAccountsToExhausted()
    {
        var checker = CreateChecker(OkExecutor(Fixture("suspended", "0.2", "\"2099-09-03T15:00:00Z\"")));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[0].Status);
        Assert.Equal("suspended", RawAccountStatus(quota));
    }

    [Fact]
    public async Task CheckQuotaUsesTheEarliestOfTheTwoResets()
    {
        var body = Fixture("healthy", "0.2", "\"2099-09-20T00:00:00Z\"")
            .Replace("\"2099-09-10T10:00:00Z\"", "\"2099-09-05T00:00:00Z\"", StringComparison.Ordinal);
        var checker = CreateChecker(OkExecutor(body));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(new DateTimeOffset(2099, 9, 5, 0, 0, 0, TimeSpan.Zero), quota.NextResetAt);
    }

    [Fact]
    public void ParseResponseKeepsBothWindowsWhenTheDataObjectIsEmpty()
    {
        var quota = Parse("""{"success":true,"data":{}}""");

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);
        Assert.All(quota.Limits, limit => Assert.Equal(ProviderQuotaStatuses.Available, limit.Status));
        Assert.All(quota.Limits, limit => Assert.Equal(0.0, limit.UsageRatio));
        Assert.All(quota.Limits, limit => Assert.Null(limit.NextResetAt));
        Assert.Null(quota.NextResetAt);
        Assert.Equal(
            """{"account_status":"","plan":{"tier":"","amount_usd":0,"interval":"","expires_at":""},"quota_monthly":{"max_flows":null,"max_value_usd":null}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public void ParseResponseClampsRatiosAboveOne()
    {
        var quota = Parse("""{"success":true,"data":{"quota_5_hour":{"usage_percentage":1.5}}}""");

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal(1.0, quota.Limits[0].UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Limits[0].Status);
    }

    // ------------------------------------------------------------------
    // Response errors
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("""{"success":false,"message":"invalid management key"}""", "zenmux API error: invalid management key")]
    [InlineData("""{"success":false,"message":""}""", "zenmux API returned success=false")]
    [InlineData("""{"success":false}""", "zenmux API returned success=false")]
    [InlineData("null", "zenmux API returned success=false")]
    public void CheckQuotaRejectsUnsuccessfulResponses(string body, string expectedMessage)
    {
        var checker = CreateChecker(OkExecutor(body));

        var error = Assert.Throws<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None).GetAwaiter().GetResult());

        Assert.Equal(expectedMessage, error.Message);
    }

    [Theory]
    [InlineData("""{"success":true}""")]
    [InlineData("""{"success":true,"data":null}""")]
    [InlineData("""{"success":true,"data":{"account_status":"healthy"},"data":null}""")]
    public void CheckQuotaRejectsResponsesWithoutData(string body)
    {
        var checker = CreateChecker(OkExecutor(body));

        var error = Assert.Throws<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None).GetAwaiter().GetResult());

        Assert.Equal("zenmux quota response contains no data", error.Message);
    }

    [Theory]
    [InlineData("""{"success":true,"data":{"quota_5_hour":{"resets_at":"2099-13-01T00:00:00Z"}}}""", "parse zenmux 5-hour reset: invalid reset timestamp \"2099-13-01T00:00:00Z\": parsing time \"2099-13-01T00:00:00Z\": month out of range")]
    [InlineData("""{"success":true,"data":{"quota_7_day":{"resets_at":"bad"}}}""", "parse zenmux 7-day reset: invalid reset timestamp \"bad\": parsing time \"bad\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"bad\" as \"2006\"")]
    public void CheckQuotaWrapsResetParseErrorsInWindowOrder(string body, string expectedMessage)
    {
        var checker = CreateChecker(OkExecutor(body));

        var error = Assert.Throws<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None).GetAwaiter().GetResult());

        Assert.Equal(expectedMessage, error.Message);
    }

    // ------------------------------------------------------------------
    // Management key
    // ------------------------------------------------------------------

    [Fact]
    public void CheckQuotaRequiresAManagementApiKeyWithoutCredentials()
    {
        var executor = OkExecutor();
        var checker = CreateChecker(executor);

        var error = Assert.Throws<InvalidOperationException>(
            () => checker.CheckQuotaAsync(
                new ChannelRecord { Type = "zenmux" },
                CancellationToken.None).GetAwaiter().GetResult());

        Assert.Equal("channel has no management API key", error.Message);
        Assert.Empty(executor.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""{"managementApiKey":""}""")]
    [InlineData("""{"managementApiKey":"   "}""")]
    [InlineData("""{"managementApiKey":5}""")]
    [InlineData("""{"apiKey":"legacy","apiKeys":["first"]}""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void CheckQuotaRequiresAManagementApiKey(string? credentials)
    {
        var executor = OkExecutor();
        var checker = CreateChecker(executor);

        var error = Assert.Throws<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None).GetAwaiter().GetResult());

        Assert.Equal("channel has no management API key", error.Message);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task CheckQuotaTrimsTheManagementKey()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(FullBody);

        await checker.CheckQuotaAsync(
            Channel("""{"managementApiKey":" management-key "}"""),
            CancellationToken.None);

        Assert.Equal("Bearer " + ManagementKey, Assert.Single(handler.Requests).Header("Authorization"));
    }

    [Fact]
    public async Task CheckQuotaPrefersTheManagementKeyOverOtherCredentialFields()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(FullBody);

        await checker.CheckQuotaAsync(
            Channel("""{"apiKey":"legacy","apiKeys":["first"],"managementApiKey":"management-key"}"""),
            CancellationToken.None);

        Assert.Equal("Bearer " + ManagementKey, Assert.Single(handler.Requests).Header("Authorization"));
    }

    [Fact]
    public async Task CheckQuotaReadsTheManagementKeyCaseInsensitively()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(FullBody);

        await checker.CheckQuotaAsync(
            Channel("""{"MANAGEMENTAPIKEY":"management-key"}"""),
            CancellationToken.None);

        Assert.Equal("Bearer " + ManagementKey, Assert.Single(handler.Requests).Header("Authorization"));
    }

    [Fact]
    public async Task CheckQuotaUsesTheLastDuplicateManagementKey()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(FullBody);

        await checker.CheckQuotaAsync(
            Channel("""{"managementApiKey":"first","ManagementAPIKey":"management-key"}"""),
            CancellationToken.None);

        Assert.Equal("Bearer " + ManagementKey, Assert.Single(handler.Requests).Header("Authorization"));
    }

    // ------------------------------------------------------------------
    // Transport and status codes
    // ------------------------------------------------------------------

    [Fact]
    public void CheckQuotaWrapsTransportErrors()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new IOException("boom"),
        };
        var checker = CreateChecker(executor);

        var error = Assert.Throws<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None).GetAwaiter().GetResult());

        Assert.Equal("zenmux quota request failed: boom", error.Message);
        Assert.IsType<IOException>(error.InnerException);
    }

    [Theory]
    [InlineData(199)]
    [InlineData(300)]
    [InlineData(301)]
    [InlineData(399)]
    public void CheckQuotaRejectsStatusesOutsideThe2xxRange(int statusCode)
    {
        var checker = CreateChecker(StatusExecutor(statusCode, FullBody));

        var error = Assert.Throws<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None).GetAwaiter().GetResult());

        Assert.Equal("zenmux quota request returned HTTP " + statusCode, error.Message);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(299)]
    public async Task CheckQuotaAcceptsEvery2xxStatus(int statusCode)
    {
        var checker = CreateChecker(StatusExecutor(statusCode, FullBody));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
    }

    // ------------------------------------------------------------------
    // Decoder errors
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("""{"success":"x"}""", "json: cannot unmarshal string into Go struct field zenmuxSubscriptionResponse.success of type bool")]
    [InlineData("""{"success":1}""", "json: cannot unmarshal number into Go struct field zenmuxSubscriptionResponse.success of type bool")]
    [InlineData("""{"message":1}""", "json: cannot unmarshal number into Go struct field zenmuxSubscriptionResponse.message of type string")]
    [InlineData("""{"success":true,"data":"x"}""", "json: cannot unmarshal string into Go struct field zenmuxSubscriptionResponse.data of type provider_quota.zenmuxSubscriptionData")]
    [InlineData("""{"success":true,"data":[]}""", "json: cannot unmarshal array into Go struct field zenmuxSubscriptionResponse.data of type provider_quota.zenmuxSubscriptionData")]
    [InlineData("""{"success":true,"data":{"plan":1}}""", "json: cannot unmarshal number into Go struct field zenmuxSubscriptionData.data.plan of type provider_quota.zenmuxPlan")]
    [InlineData("""{"success":true,"data":{"plan":{"tier":1}}}""", "json: cannot unmarshal number into Go struct field zenmuxPlan.data.plan.tier of type string")]
    [InlineData("""{"success":true,"data":{"plan":{"amount_usd":"x"}}}""", "json: cannot unmarshal string into Go struct field zenmuxPlan.data.plan.amount_usd of type float64")]
    [InlineData("""{"success":true,"data":{"plan":{"interval":1}}}""", "json: cannot unmarshal number into Go struct field zenmuxPlan.data.plan.interval of type string")]
    [InlineData("""{"success":true,"data":{"plan":{"expires_at":1}}}""", "json: cannot unmarshal number into Go struct field zenmuxPlan.data.plan.expires_at of type string")]
    [InlineData("""{"success":true,"data":{"account_status":1}}""", "json: cannot unmarshal number into Go struct field zenmuxSubscriptionData.data.account_status of type string")]
    [InlineData("""{"success":true,"data":{"quota_5_hour":"x"}}""", "json: cannot unmarshal string into Go struct field zenmuxSubscriptionData.data.quota_5_hour of type provider_quota.zenmuxQuotaWindow")]
    [InlineData("""{"success":true,"data":{"quota_7_day":[]}}""", "json: cannot unmarshal array into Go struct field zenmuxSubscriptionData.data.quota_7_day of type provider_quota.zenmuxQuotaWindow")]
    [InlineData("""{"success":true,"data":{"quota_5_hour":{"usage_percentage":"x"}}}""", "json: cannot unmarshal string into Go struct field zenmuxQuotaWindow.data.quota_5_hour.usage_percentage of type float64")]
    [InlineData("""{"success":true,"data":{"quota_7_day":{"usage_percentage":"x"}}}""", "json: cannot unmarshal string into Go struct field zenmuxQuotaWindow.data.quota_7_day.usage_percentage of type float64")]
    [InlineData("""{"success":true,"data":{"quota_5_hour":{"resets_at":[]}}}""", "json: cannot unmarshal array into Go struct field zenmuxQuotaWindow.data.quota_5_hour.resets_at of type string")]
    [InlineData("""{"success":true,"data":{"quota_5_hour":{"resets_at":{}}}}""", "json: cannot unmarshal object into Go struct field zenmuxQuotaWindow.data.quota_5_hour.resets_at of type string")]
    [InlineData("""{"success":true,"data":{"quota_5_hour":{"max_flows":"x"}}}""", "json: cannot unmarshal string into Go struct field zenmuxQuotaWindow.data.quota_5_hour.max_flows of type float64")]
    [InlineData("""{"success":true,"data":{"quota_5_hour":{"used_flows":[]}}}""", "json: cannot unmarshal array into Go struct field zenmuxQuotaWindow.data.quota_5_hour.used_flows of type float64")]
    [InlineData("""{"success":true,"data":{"quota_5_hour":{"remaining_flows":1e400}}}""", "json: cannot unmarshal number 1e400 into Go struct field zenmuxQuotaWindow.data.quota_5_hour.remaining_flows of type float64")]
    [InlineData("""{"success":true,"data":{"quota_5_hour":{"used_value_usd":true}}}""", "json: cannot unmarshal bool into Go struct field zenmuxQuotaWindow.data.quota_5_hour.used_value_usd of type float64")]
    [InlineData("""{"success":true,"data":{"quota_5_hour":{"max_value_usd":{}}}}""", "json: cannot unmarshal object into Go struct field zenmuxQuotaWindow.data.quota_5_hour.max_value_usd of type float64")]
    [InlineData("""{"success":true,"data":{"quota_monthly":"x"}}""", "json: cannot unmarshal string into Go struct field zenmuxSubscriptionData.data.quota_monthly of type provider_quota.zenmuxMonthlyQuota")]
    [InlineData("""{"success":true,"data":{"quota_monthly":{"max_flows":"x"}}}""", "json: cannot unmarshal string into Go struct field zenmuxMonthlyQuota.data.quota_monthly.max_flows of type float64")]
    [InlineData("""{"success":true,"data":{"quota_monthly":{"max_value_usd":true}}}""", "json: cannot unmarshal bool into Go struct field zenmuxMonthlyQuota.data.quota_monthly.max_value_usd of type float64")]
    [InlineData("true", "json: cannot unmarshal bool into Go value of type provider_quota.zenmuxSubscriptionResponse")]
    [InlineData("1", "json: cannot unmarshal number into Go value of type provider_quota.zenmuxSubscriptionResponse")]
    [InlineData("[]", "json: cannot unmarshal array into Go value of type provider_quota.zenmuxSubscriptionResponse")]
    public void ParseResponseUsesTheGoDecoderErrors(string body, string expectedMessage)
    {
        var error = Assert.Throws<WrappedException>(() => Parse(body));

        Assert.Equal(
            "failed to parse zenmux quota response: " + expectedMessage,
            error.Message);
    }

    [Fact]
    public void ParseResponseWrapsMalformedJson()
    {
        var error = Assert.Throws<WrappedException>(() => Parse("{"));

        Assert.StartsWith("failed to parse zenmux quota response: ", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Decoder semantics
    // ------------------------------------------------------------------

    [Fact]
    public void ParseSubscriptionResponseTreatsJsonNullAsTheZeroPayload()
    {
        var response = ParseSubscriptionResponse("null");

        Assert.False(response.Success);
        Assert.Equal(string.Empty, response.Message);
        Assert.Null(response.Data);
    }

    [Fact]
    public void ParseSubscriptionResponseMergesRepeatedObjectsAndKeepsTheLastScalar()
    {
        var response = ParseSubscriptionResponse(
            """{"success":true,"data":{"plan":{"tier":"x"},"account_status":"healthy"},"data":{"plan":{"interval":"month"}}}""");

        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.Equal("x", response.Data!.Plan.Tier);
        Assert.Equal(0.0, response.Data.Plan.AmountUsd);
        Assert.Equal("month", response.Data.Plan.Interval);
        Assert.Equal(string.Empty, response.Data.Plan.ExpiresAt);
        Assert.Equal("healthy", response.Data.AccountStatus);

        var scalars = ParseSubscriptionResponse(
            """{"message":"first","message":"second","message":null,"success":true,"success":null}""");
        Assert.True(scalars.Success);
        Assert.Equal("second", scalars.Message);
    }

    [Fact]
    public void ParseSubscriptionResponseResetsDataWithNull()
    {
        var response = ParseSubscriptionResponse(
            """{"success":true,"data":{"plan":{"tier":"x"}},"data":null}""");

        Assert.True(response.Success);
        Assert.Null(response.Data);
    }

    [Fact]
    public void ParseSubscriptionResponseTreatsNullValueStructsAsNoOps()
    {
        var response = ParseSubscriptionResponse(
            """{"success":true,"data":{"plan":{"tier":"x"},"plan":null,"quota_5_hour":{"usage_percentage":0.5},"quota_5_hour":null,"quota_monthly":{"max_flows":7},"quota_monthly":null}}""");

        Assert.NotNull(response.Data);
        Assert.Equal("x", response.Data!.Plan.Tier);
        Assert.Equal(0.5, response.Data.Quota5Hour.UsagePercentage);
        Assert.Equal(7.0, response.Data.QuotaMonthly.MaxFlows);
        Assert.Null(response.Data.QuotaMonthly.MaxValueUsd);
    }

    [Fact]
    public void ParseSubscriptionResponseMatchesFieldsCaseInsensitively()
    {
        var response = ParseSubscriptionResponse(
            """{"SUCCESS":true,"DATA":{"ACCOUNT_STATUS":"HeAlThY","QUOTA_5_HOUR":{"USAGE_PERCENTAGE":0.2,"RESETS_AT":"2099-09-03T15:00:00Z"}}}""");

        Assert.True(response.Success);
        Assert.NotNull(response.Data);
        Assert.Equal("HeAlThY", response.Data!.AccountStatus);
        Assert.Equal(0.2, response.Data.Quota5Hour.UsagePercentage);
        Assert.Equal("2099-09-03T15:00:00Z", response.Data.Quota5Hour.ResetsAt);
    }

    [Fact]
    public void ParseResponseAcceptsADataObjectAfterANull()
    {
        var quota = Parse("""{"success":true,"data":null,"data":{"account_status":"healthy"}}""");

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal("healthy", RawAccountStatus(quota));
    }

    // ------------------------------------------------------------------
    // Status mapping
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(-0.5, ProviderQuotaStatuses.Available)]
    [InlineData(0.0, ProviderQuotaStatuses.Available)]
    [InlineData(0.25, ProviderQuotaStatuses.Available)]
    [InlineData(0.799999, ProviderQuotaStatuses.Available)]
    [InlineData(0.8, ProviderQuotaStatuses.Warning)]
    [InlineData(0.99, ProviderQuotaStatuses.Warning)]
    [InlineData(1.0, ProviderQuotaStatuses.Exhausted)]
    [InlineData(1.5, ProviderQuotaStatuses.Exhausted)]
    public void WindowStatusMapsTheUsageRatio(double usageRatio, string expectedStatus)
    {
        Assert.Equal(expectedStatus, ZenmuxQuotaChecker.WindowStatus(usageRatio));
    }

    [Theory]
    [InlineData("healthy", ProviderQuotaStatuses.Available)]
    [InlineData(" Healthy ", ProviderQuotaStatuses.Available)]
    [InlineData("HeAlThY", ProviderQuotaStatuses.Available)]
    [InlineData("monitored", ProviderQuotaStatuses.Available)]
    [InlineData("abusive", ProviderQuotaStatuses.Warning)]
    [InlineData("suspended", ProviderQuotaStatuses.Exhausted)]
    [InlineData("banned", ProviderQuotaStatuses.Exhausted)]
    [InlineData("SUSPENDED", ProviderQuotaStatuses.Exhausted)]
    [InlineData("paid", ProviderQuotaStatuses.Unknown)]
    [InlineData("", ProviderQuotaStatuses.Unknown)]
    public void AccountStatusMapsTheTrimmedLowercaseValue(string status, string expectedStatus)
    {
        Assert.Equal(expectedStatus, ZenmuxQuotaChecker.AccountStatus(status));
    }

    [Fact]
    public void WorstStatusRanksUnknownAboveExhausted()
    {
        Assert.Equal(ProviderQuotaStatuses.Available, ZenmuxQuotaChecker.WorstStatus());
        Assert.Equal(
            ProviderQuotaStatuses.Warning,
            ZenmuxQuotaChecker.WorstStatus(
                ProviderQuotaStatuses.Available,
                ProviderQuotaStatuses.Warning));
        Assert.Equal(
            ProviderQuotaStatuses.Exhausted,
            ZenmuxQuotaChecker.WorstStatus(
                ProviderQuotaStatuses.Available,
                ProviderQuotaStatuses.Warning,
                ProviderQuotaStatuses.Exhausted));
        Assert.Equal(
            ProviderQuotaStatuses.Unknown,
            ZenmuxQuotaChecker.WorstStatus(
                ProviderQuotaStatuses.Exhausted,
                ProviderQuotaStatuses.Unknown));
        Assert.Equal(
            ProviderQuotaStatuses.Unknown,
            ZenmuxQuotaChecker.WorstStatus(
                ProviderQuotaStatuses.Unknown,
                ProviderQuotaStatuses.Exhausted));
    }

    [Fact]
    public void ParseResponseMasksAnUnknownAccountStatusWhenTheWindowsAreReady()
    {
        var quota = Parse(
            """{"success":true,"data":{"account_status":"paid","quota_5_hour":{"usage_percentage":0.2}}}""");

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);
    }

    [Fact]
    public void ParseResponseKeepsTheUnknownAccountStatusWhenTheWindowsAreDropped()
    {
        var quota = Parse(
            """{"success":true,"data":{"account_status":"paid","quota_5_hour":{"usage_percentage":-0.1},"quota_7_day":{"usage_percentage":-0.2}}}""");

        // The account-level unknown outranks the two window availabilities, and
        // normalization then drops both negative ratios.
        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.False(quota.Ready);
        Assert.Empty(quota.Limits);
        Assert.Equal(
            """{"account_status":"paid","plan":{"tier":"","amount_usd":0,"interval":"","expires_at":""},"quota_monthly":{"max_flows":null,"max_value_usd":null}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    // ------------------------------------------------------------------
    // Reset parsing
    // ------------------------------------------------------------------

    [Fact]
    public void ParseResetAtReturnsNullForNull()
    {
        Assert.Null(ZenmuxQuotaChecker.ParseResetAt(null));
    }

    [Theory]
    [InlineData("2099-09-03T15:00:00Z", 0)]
    [InlineData("2099-09-03T15:00:00+08:00", 8)]
    [InlineData("2099-09-03T15:00:00-05:00", -5)]
    [InlineData("2099-09-03T15:00:00.5Z", 0)]
    [InlineData("2099-09-03T15:00:00,5Z", 0)]
    public void ParseResetAtReadsRfc3339Timestamps(string value, int expectedOffsetHours)
    {
        var parsed = ZenmuxQuotaChecker.ParseResetAt(value);

        Assert.NotNull(parsed);
        Assert.Equal(TimeSpan.FromHours(expectedOffsetHours), parsed!.Value.Offset);
        Assert.Equal(2099, parsed.Value.Year);
        Assert.Equal(9, parsed.Value.Month);
        Assert.Equal(3, parsed.Value.Day);
        Assert.Equal(15, parsed.Value.Hour);
    }

    [Fact]
    public void ParseResetAtTruncatesSubTickFractions()
    {
        var parsed = ZenmuxQuotaChecker.ParseResetAt("2099-09-03T15:00:00.123456789Z");

        Assert.NotNull(parsed);
        Assert.Equal(
            new DateTimeOffset(2099, 9, 3, 15, 0, 0, TimeSpan.Zero).AddTicks(1234567),
            parsed!.Value);
    }

    [Theory]
    [InlineData("", "parsing time \"\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"\" as \"2006\"")]
    [InlineData("bad", "parsing time \"bad\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"bad\" as \"2006\"")]
    [InlineData("2099-1-01T00:00:00Z", "parsing time \"2099-1-01T00:00:00Z\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"1-01T00:00:00Z\" as \"01\"")]
    [InlineData("2099-01-01t00:00:00Z", "parsing time \"2099-01-01t00:00:00Z\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"t00:00:00Z\" as \"T\"")]
    [InlineData("2099-01-01T000:00:00Z", "parsing time \"2099-01-01T000:00:00Z\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"0:00:00Z\" as \":\"")]
    [InlineData("2099-01-01T00:0:00Z", "parsing time \"2099-01-01T00:0:00Z\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"0:00Z\" as \"04\"")]
    [InlineData("2099-01-01T00:00:00", "parsing time \"2099-01-01T00:00:00\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"\" as \"Z07:00\"")]
    [InlineData("2099-01-01T00:00:00+08", "parsing time \"2099-01-01T00:00:00+08\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"+08\" as \"Z07:00\"")]
    [InlineData("2099-01-01T00:00:00.Z", "parsing time \"2099-01-01T00:00:00.Z\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \".Z\" as \"Z07:00\"")]
    [InlineData("2099-01-01T00:00:00.5", "parsing time \"2099-01-01T00:00:00.5\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"\" as \"Z07:00\"")]
    [InlineData("2099-01-01T00:00:00.", "parsing time \"2099-01-01T00:00:00.\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \".\" as \"Z07:00\"")]
    [InlineData("2099-01-01T00:00:00x8:000", "parsing time \"2099-01-01T00:00:00x8:000\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"x8:000\" as \"Z07:00\"")]
    [InlineData("2099-01-01T00:00:00+08x00", "parsing time \"2099-01-01T00:00:00+08x00\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"+08x00\" as \"Z07:00\"")]
    [InlineData("2099-01-01T00:00:00+0 :00", "parsing time \"2099-01-01T00:00:00+0 :00\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"+0 :00\" as \"Z07:00\"")]
    [InlineData("2099-01-01T00:00:00+08:000", "parsing time \"2099-01-01T00:00:00+08:000\": extra text: \"0\"")]
    [InlineData("2099-01-01T00:00:00Z ", "parsing time \"2099-01-01T00:00:00Z \": extra text: \" \"")]
    [InlineData("2099-01-01T00:00:00+08:00x", "parsing time \"2099-01-01T00:00:00+08:00x\": extra text: \"x\"")]
    [InlineData("2099-13-01t00:00:00Z", "parsing time \"2099-13-01t00:00:00Z\": month out of range")]
    [InlineData("2099-00-01T00:00:00Z", "parsing time \"2099-00-01T00:00:00Z\": month out of range")]
    [InlineData("2099-02-29T00:00:00Z", "parsing time \"2099-02-29T00:00:00Z\": day out of range")]
    [InlineData("2099-01-00T00:00:00Z", "parsing time \"2099-01-00T00:00:00Z\": day out of range")]
    [InlineData("2099-01-32T00:00:00x", "parsing time \"2099-01-32T00:00:00x\" as \"2006-01-02T15:04:05Z07:00\": cannot parse \"x\" as \"Z07:00\"")]
    [InlineData("2099-01-01T24:00:00+25:00", "parsing time \"2099-01-01T24:00:00+25:00\": hour out of range")]
    [InlineData("2099-01-01T00:60:00Z", "parsing time \"2099-01-01T00:60:00Z\": minute out of range")]
    [InlineData("2099-01-01T00:00:60Z", "parsing time \"2099-01-01T00:00:60Z\": second out of range")]
    [InlineData("2099-01-01T00:00:00+25:00", "parsing time \"2099-01-01T00:00:00+25:00\": time zone offset hour out of range")]
    [InlineData("2099-01-01T00:00:00-25:00", "parsing time \"2099-01-01T00:00:00-25:00\": time zone offset hour out of range")]
    [InlineData("2099-01-01T00:00:00+25:61", "parsing time \"2099-01-01T00:00:00+25:61\": time zone offset minute out of range")]
    [InlineData("2099-01-01T00:00:00+00:61", "parsing time \"2099-01-01T00:00:00+00:61\": time zone offset minute out of range")]
    public void ParseResetAtReproducesTheGoParseErrors(string value, string expectedGoError)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ZenmuxQuotaChecker.ParseResetAt(value));

        Assert.Equal(
            "invalid reset timestamp \"" + value + "\": " + expectedGoError,
            error.Message);
    }

    [Theory]
    [InlineData("0000-02-29T00:00:00Z")]
    [InlineData("2099-01-01T00:00:00+14:01")]
    [InlineData("2099-01-01T00:00:00+14:60")]
    [InlineData("2099-01-01T00:00:00+24:60")]
    [InlineData("2099-01-01T00:00:00+00:60")]
    [InlineData("0001-01-01T00:00:00+14:00")]
    [InlineData("9999-12-31T23:59:59-14:00")]
    public void ParseResetAtRejectsValuesOutsideTheDateTimeOffsetRange(string value)
    {
        // +00:60 parses as +01:00 and is representable; the rest exercise the
        // documented C123/C207 range gap.
        if (value == "2099-01-01T00:00:00+00:60")
        {
            Assert.NotNull(ZenmuxQuotaChecker.ParseResetAt(value));
            return;
        }

        var error = Assert.Throws<InvalidOperationException>(
            () => ZenmuxQuotaChecker.ParseResetAt(value));

        Assert.Contains("timestamp is outside the DateTimeOffset range", error.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Raw data and the shared contract fixture
    // ------------------------------------------------------------------

    [Fact]
    public void ParseResponseKeepsQuotaMonthlyInRawDataOnly()
    {
        var quota = Parse(ContractFixtureBody);

        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(QuotaWindows.FiveHours, quota.Limits[0].Window);
        Assert.Equal(QuotaWindows.SevenDays, quota.Limits[1].Window);
        Assert.Equal(0.25, quota.Limits[0].UsageRatio, 9);
        Assert.Equal(0.5, quota.Limits[1].UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero), quota.NextResetAt);
        Assert.Equal(new DateTimeOffset(2098, 12, 31, 19, 0, 0, TimeSpan.Zero), quota.Limits[0].PeriodStart);
        Assert.Equal(new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero), quota.Limits[1].PeriodStart);

        Assert.Equal(
            """{"account_status":"healthy","plan":{"tier":"","amount_usd":0,"interval":"","expires_at":""},"quota_monthly":{"max_flows":1000,"max_value_usd":50}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public void ParseResponseSerializesTheMapKeysInOrder()
    {
        var quota = Parse(FullBody);

        Assert.Equal(
            """{"account_status":"healthy","plan":{"tier":"pro","amount_usd":200,"interval":"month","expires_at":"2099-10-01T00:00:00Z"},"quota_monthly":{"max_flows":50000,"max_value_usd":500}}""",
            GoJson.MarshalToString(quota.RawData));
        Assert.Equal(3, quota.RawData.Count);
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
        var checker = new ZenmuxQuotaChecker(direct, factory, new AntigravityTestClock(Now));

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
        var checker = new ZenmuxQuotaChecker(direct, factory, new AntigravityTestClock(Now));

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
        var checker = new ZenmuxQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Empty(factory.Requests);
        Assert.Single(direct.Requests);
    }

    [Theory]
    [InlineData("zenmux", true)]
    [InlineData("zenmux_responses", true)]
    [InlineData("zenmux_anthropic", true)]
    [InlineData("zenmux_gemini", true)]
    [InlineData("zenmux_video", true)]
    [InlineData("openai", false)]
    [InlineData("claudecode", false)]
    [InlineData("nanogpt", false)]
    [InlineData("github_copilot", false)]
    public void SupportsChannelMatchesEveryZenmuxType(string channelType, bool expected)
    {
        var checker = CreateChecker(new ScriptedExecutor());

        Assert.Equal(expected, checker.SupportsChannel(new ChannelRecord { Type = channelType }));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ZenmuxQuotaChecker CreateChecker(
        out ClaudeCodeQuotaCheckerTests.RecordingHttpHandler handler)
    {
        handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new ZenmuxQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now));
    }

    private static ZenmuxQuotaChecker CreateChecker(ScriptedExecutor executor)
        => new(executor, new ScriptedProxyExecutorFactory(), new AntigravityTestClock(Now));

    private static ChannelRecord Channel(
        string? credentials = null,
        string settings = "{}")
        => new()
        {
            Type = "zenmux",
            Credentials = credentials ?? """{"managementApiKey":"management-key"}""",
            Settings = settings,
        };

    private static QuotaData Parse(string json)
        => ZenmuxQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(json), Now);

    private static ZenmuxSubscriptionResponse ParseSubscriptionResponse(string json)
        => ZenmuxQuotaChecker.ParseSubscriptionResponse(Encoding.UTF8.GetBytes(json));

    private static string Fixture(string accountStatus, string usage, string reset) => $$"""
        {"success":true,"data":{"plan":{"tier":"pro","amount_usd":200,"interval":"month","expires_at":"2099-10-01T00:00:00Z"},"account_status":"{{accountStatus}}","quota_5_hour":{"usage_percentage":{{usage}},"resets_at":{{reset}},"max_flows":1000,"used_flows":400,"remaining_flows":600,"used_value_usd":40,"max_value_usd":100},"quota_7_day":{"usage_percentage":0.6,"resets_at":"2099-09-10T10:00:00Z","max_flows":10000,"used_flows":6000,"remaining_flows":4000,"used_value_usd":120,"max_value_usd":200},"quota_monthly":{"max_flows":50000,"max_value_usd":500}}}
        """;

    private static string RawAccountStatus(QuotaData quota)
        => Assert.IsType<string>(quota.RawData["account_status"]);

    private static ScriptedExecutor OkExecutor(string body = FullBody) => new()
    {
        OnDo = (_, _, _) => Ok(body),
    };

    private static ScriptedExecutor StatusExecutor(int statusCode, string body) => new()
    {
        OnDo = (_, _, _) => new HttpResponseData
        {
            StatusCode = statusCode,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            Body = Encoding.UTF8.GetBytes(body),
        },
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
