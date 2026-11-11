using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports internal/server/biz/provider_quota/zhipu_checker.go together with
/// zhipu_checker_test.go: the fixed open.bigmodel.cn quota endpoint, the
/// per-key account fan-out (enabled keys first, disabled keys bounded), the
/// TOKENS_LIMIT/CREDIT_LIMIT window mapping, the single-key window limits vs
/// multi-key availability-group binding limits, and the Go JSON decoding edge
/// cases (pointer/slice merge on duplicate keys, typed integers, float range).
/// </summary>
public sealed class ZhipuQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

    private const string DefaultQuotaUrl = "https://open.bigmodel.cn/api/monitor/usage/quota/limit";

    private const string DefaultCredentials = """{"apiKey":"test-key"}""";

    private const string SuccessBody = """
        {
            "success": true,
            "code": 200,
            "data": {
                "level": "standard",
                "limits": [
                    {"type": "TOKENS_LIMIT", "percentage": 10.0}
                ]
            }
        }
        """;

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    /// <summary>TestZhipu_CheckQuota_HappyPath (zhipu_checker_test.go:20-74).</summary>
    [Fact]
    public async Task HappyPathReportsBothWindowsAndTheWireShape()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse("""
            {
                "success": true,
                "code": 200,
                "data": {
                    "level": "standard",
                    "limits": [
                        {"type": "TOKENS_LIMIT", "percentage": 25.5, "nextResetTime": 1784322000000},
                        {"type": "TOKENS_LIMIT", "percentage": 10.0, "nextResetTime": 1784476800000},
                        {"type": "TIME_LIMIT", "percentage": 50.0}
                    ]
                }
            }
            """);

        var quota = await checker.CheckQuotaAsync(
            Channel(
                credentials: """{"apiKey":"test-key"}""",
                baseUrl: "https://open.bigmodel.cn/api/paas/v4"),
            CancellationToken.None);

        // The channel base URL never matters: the metadata endpoint is fixed,
        // and the API key travels verbatim in the Authorization header.
        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(DefaultQuotaUrl, request.Url);
        Assert.Equal("test-key", request.Header("Authorization"));
        Assert.Equal("application/json", request.Header("Content-Type"));
        Assert.Equal("en-US,en", request.Header("Accept-Language"));
        Assert.Equal(string.Empty, request.Body);

        Assert.Equal("available", quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal("zhipu", quota.ProviderType);
        Assert.Equal(2, quota.Limits.Count);

        // First window (five_hour): 25.5% used, reset at 2026-07-17T21:00Z.
        Assert.Equal("5h", quota.Limits[0].Window);
        Assert.Equal(0.255, quota.Limits[0].UsageRatio, 3);
        Assert.Equal("available", quota.Limits[0].Status);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1784322000000), quota.Limits[0].NextResetAt);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 17, 16, 0, 0, TimeSpan.Zero),
            quota.Limits[0].PeriodStart);

        // Second window (weekly_limit): 10.0% used.
        Assert.Equal("weekly", quota.Limits[1].Window);
        Assert.Equal(0.10, quota.Limits[1].UsageRatio, 3);
        Assert.Equal("available", quota.Limits[1].Status);

        var rows = Assert.IsType<List<ZhipuWindowRow>>(quota.RawData["rows"]);
        Assert.Equal(2, rows.Count);
        Assert.Equal("five_hour", rows[0].Window);
        Assert.Equal("2026-07-17T21:00:00Z", rows[0].ResetAt);
        Assert.Equal("weekly_limit", rows[1].Window);
        Assert.Equal("2026-07-19T16:00:00Z", rows[1].ResetAt);
        Assert.Equal("standard", quota.RawData["level"]);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1784322000000), quota.NextResetAt);

        var accounts = Assert.IsType<List<ZhipuAccountQuota>>(quota.RawData["accounts"]);
        var account = Assert.Single(accounts);
        Assert.Equal("-key", account.Suffix);
        Assert.Equal(8, account.Ref.Length);
        Assert.All(account.Ref, character => Assert.True(Uri.IsHexDigit(character)));
        Assert.False(account.Disabled);
        Assert.Equal("available", account.Status);
        Assert.True(account.Ready);
        Assert.Equal("standard", account.Level);
        Assert.Equal(2, account.Rows!.Count);
    }

    /// <summary>TestZhipu_CheckQuota_SingleWindow (zhipu_checker_test.go:76-106).</summary>
    [Fact]
    public async Task SingleWindowKeepsOnlyTheFiveHourLimit()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse("""
            {
                "success": true,
                "code": 200,
                "data": {
                    "level": "basic",
                    "limits": [
                        {"type": "TOKENS_LIMIT", "percentage": 5.0}
                    ]
                }
            }
            """);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal("5h", limit.Window);
        Assert.Equal("available", limit.Status);
        Assert.Equal(0.05, limit.UsageRatio, 3);
        Assert.Null(limit.NextResetAt);
        Assert.Null(limit.PeriodStart);
    }

    /// <summary>TestZhipu_CheckQuota_Warning (zhipu_checker_test.go:108-137).</summary>
    [Fact]
    public async Task NinetyPercentIsWarningAndStillReady()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(TokensBody("standard", 90.0));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal("warning", quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal("warning", Assert.Single(quota.Limits).Status);
    }

    /// <summary>TestZhipu_CheckQuota_Exhausted (zhipu_checker_test.go:139-168).</summary>
    [Fact]
    public async Task FullWindowIsExhausted()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(TokensBody("standard", 100.0));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal("exhausted", quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal("exhausted", Assert.Single(quota.Limits).Status);
    }

    /// <summary>TestZhipu_CheckQuota_APIError (zhipu_checker_test.go:170-193).</summary>
    [Fact]
    public async Task ApiErrorKeepsTheProviderMessage()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse("""
            {
                "success": false,
                "code": 500,
                "msg": "当前用户不存在coding plan"
            }
            """);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Contains("当前用户不存在coding plan", error.Message, StringComparison.Ordinal);
        Assert.Contains("failed for all 1 keys", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The API error message falls back to the code when the payload has none
    /// (zhipu_checker.go:503-511).
    /// </summary>
    [Fact]
    public async Task ApiErrorWithoutMessageFallsBackToTheCode()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse("""{"success":false,"code":500}""");

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Contains("zhipu API error: API error code 500", error.Message, StringComparison.Ordinal);
    }

    /// <summary>TestZhipu_CheckQuota_MissingAPIKey (zhipu_checker_test.go:195-199).</summary>
    [Fact]
    public async Task MissingApiKeyFailsBeforeAnyRequest()
    {
        var executor = new ScriptedExecutor();
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(credentials: "{}"), CancellationToken.None));

        Assert.Equal("channel has no API key", error.Message);
        Assert.Empty(executor.Requests);
    }

    /// <summary>TestZhipu_CheckQuota_APIKeysFallback (zhipu_checker_test.go:201-234).</summary>
    [Fact]
    public async Task EmptyLegacyKeyFallsBackToTheFirstApiKey()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(TokensBody("standard", 15.0));

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"","apiKeys":["fallback-key"]}"""),
            CancellationToken.None);

        Assert.Equal("available", quota.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("fallback-key", request.Header("Authorization"));
    }

    /// <summary>TestZhipu_CheckQuota_APIKeysFallbackSkipsBlankEntries (zhipu_checker_test.go:236-269).</summary>
    [Fact]
    public async Task BlankApiKeysAreSkippedUntilAServingKey()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(TokensBody("standard", 15.0));

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"","apiKeys":["","  ","fallback-key"]}"""),
            CancellationToken.None);

        Assert.Equal("available", quota.Status);
        Assert.Equal(
            "fallback-key",
            Assert.Single(handler.Requests).Header("Authorization"));
    }

    /// <summary>TestZhipu_CheckQuota_MalformedJSON (zhipu_checker_test.go:271-289).</summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("""{"success":true""")]
    public async Task MalformedJsonIsWrappedWithTheParsePrefix(string body)
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(body);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Contains("failed to parse zhipu quota response", error.Message, StringComparison.Ordinal);
    }

    /// <summary>TestZhipu_CheckQuota_NoData (zhipu_checker_test.go:291-310).</summary>
    [Theory]
    [InlineData("""{"success":true,"code":200}""")]
    [InlineData("""{"success":true,"code":200,"data":null}""")]
    public async Task MissingDataIsReported(string body)
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(body);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Contains("zhipu quota response contains no data", error.Message, StringComparison.Ordinal);
    }

    /// <summary>TestZhipu_CheckQuota_NoTokensLimit (zhipu_checker_test.go:312-340).</summary>
    [Theory]
    [InlineData("""{"success":true,"data":{"level":"standard","limits":[{"type":"TIME_LIMIT","percentage":50}]}}""")]
    [InlineData("""{"success":true,"data":{"level":"standard","limits":[]}}""")]
    [InlineData("""{"success":true,"data":{"level":"standard","limits":null}}""")]
    public async Task MissingTokenAndCreditEntriesAreReported(string body)
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(body);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Contains(
            "zhipu quota response contains no TOKENS_LIMIT or CREDIT_LIMIT entries",
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>TestZhipu_CheckQuota_NoResetIsFiveHour (zhipu_checker_test.go:342-383).</summary>
    [Fact]
    public async Task TheEntryWithoutResetIsTheFiveHourBucket()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse("""
            {
                "success": true,
                "code": 200,
                "data": {
                    "level": "standard",
                    "limits": [
                        {"type": "TOKENS_LIMIT", "percentage": 96.0, "nextResetTime": 1784541710987},
                        {"type": "TOKENS_LIMIT", "percentage": 0.0}
                    ]
                }
            }
            """);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(2, quota.Limits.Count);
        var rows = Assert.IsType<List<ZhipuWindowRow>>(quota.RawData["rows"]);
        Assert.Equal("five_hour", rows[0].Window);
        Assert.Equal(0.0, rows[0].UsedPercent, 3);
        Assert.Null(rows[0].ResetAt);
        Assert.Equal("weekly_limit", rows[1].Window);
        Assert.Equal(96.0, rows[1].UsedPercent, 3);
        Assert.Equal("2026-07-20T10:01:50Z", rows[1].ResetAt);
        Assert.Equal("5h", quota.Limits[0].Window);
        Assert.Null(quota.Limits[0].NextResetAt);
        Assert.Equal("weekly", quota.Limits[1].Window);
    }

    /// <summary>TestZhipu_CheckQuota_BothHaveResetTime (zhipu_checker_test.go:385-424).</summary>
    [Fact]
    public async Task BothResetsKeepTheApiOrder()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse("""
            {
                "success": true,
                "code": 200,
                "data": {
                    "level": "standard",
                    "limits": [
                        {"type": "TOKENS_LIMIT", "percentage": 1.0, "nextResetTime": 1784384374404},
                        {"type": "TOKENS_LIMIT", "percentage": 96.0, "nextResetTime": 1784541710987}
                    ]
                }
            }
            """);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var rows = Assert.IsType<List<ZhipuWindowRow>>(quota.RawData["rows"]);
        Assert.Equal("five_hour", rows[0].Window);
        Assert.Equal(1.0, rows[0].UsedPercent, 3);
        Assert.Equal("2026-07-18T14:19:34Z", rows[0].ResetAt);
        Assert.Equal("weekly_limit", rows[1].Window);
        Assert.Equal(96.0, rows[1].UsedPercent, 3);
        Assert.Equal("2026-07-20T10:01:50Z", rows[1].ResetAt);
    }

    /// <summary>TestZhipu_CheckQuota_WithResetTime (zhipu_checker_test.go:443-474).</summary>
    [Fact]
    public async Task NextResetAtIsTheEarliestWindowReset()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse("""
            {
                "success": true,
                "code": 200,
                "data": {
                    "level": "standard",
                    "limits": [
                        {"type": "TOKENS_LIMIT", "percentage": 50.0, "nextResetTime": 4087947600000},
                        {"type": "TOKENS_LIMIT", "percentage": 30.0, "nextResetTime": 4088534400000}
                    ]
                }
            }
            """);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.NotNull(quota.NextResetAt);
        Assert.Equal(4087947600, quota.NextResetAt!.Value.ToUnixTimeSeconds());
    }

    /// <summary>TestZhipu_CheckQuota_OverallStatusWorst (zhipu_checker_test.go:476-506).</summary>
    [Fact]
    public async Task OverallStatusTakesTheWorstWindow()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse("""
            {
                "success": true,
                "code": 200,
                "data": {
                    "level": "standard",
                    "limits": [
                        {"type": "TOKENS_LIMIT", "percentage": 50.0},
                        {"type": "TOKENS_LIMIT", "percentage": 95.0}
                    ]
                }
            }
            """);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal("warning", quota.Status);
        Assert.Equal("available", quota.Limits[0].Status);
        Assert.Equal("warning", quota.Limits[1].Status);
    }

    // ------------------------------------------------------------------
    // GLM coding plan (CREDIT_LIMIT)
    // ------------------------------------------------------------------

    /// <summary>TestZhipu_CheckQuota_CreditLimitCodingPlan (zhipu_checker_test.go:535-571).</summary>
    [Fact]
    public async Task CreditLimitPayloadMapsUnitAndNumberToWindows()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(CreditPayload("max", 10, 100));

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"coding-plan-key"}"""),
            CancellationToken.None);

        Assert.Equal("exhausted", quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal("max", quota.RawData["level"]);

        var rows = Assert.IsType<List<ZhipuWindowRow>>(quota.RawData["rows"]);
        Assert.Equal(2, rows.Count);

        Assert.Equal("five_hour", rows[0].Window);
        Assert.Equal(10.0, rows[0].UsedPercent, 3);
        Assert.Equal("available", rows[0].Status);
        Assert.Equal(28000L, rows[0].Usage);
        Assert.Equal(2896L, rows[0].Used);
        Assert.Equal(25103L, rows[0].Remaining);
        Assert.Equal("2100-01-04T07:40:00Z", rows[0].ResetAt);

        Assert.Equal("weekly_limit", rows[1].Window);
        Assert.Equal(100.0, rows[1].UsedPercent, 3);
        Assert.Equal("exhausted", rows[1].Status);
        Assert.Equal(140000L, rows[1].Usage);
        Assert.Equal(140019L, rows[1].Used);
        Assert.Equal(0L, rows[1].Remaining);
        Assert.Equal("2100-01-10T21:33:20Z", rows[1].ResetAt);

        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal("5h", quota.Limits[0].Window);
        Assert.Equal("weekly", quota.Limits[1].Window);
        Assert.Equal(
            new DateTimeOffset(2100, 1, 4, 2, 40, 0, TimeSpan.Zero),
            quota.Limits[0].PeriodStart);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(4102731600000), quota.NextResetAt);
    }

    /// <summary>TestZhipu_CheckQuota_MultipleKeysReportEveryAccount (zhipu_checker_test.go:573-622).</summary>
    [Fact]
    public async Task MultipleKeysReportEveryAccountAndRoutingStaysOpen()
    {
        var checker = CreateChecker(KeyedExecutor(new Dictionary<string, string>
        {
            ["aaaa-account-0001"] = CreditPayload("max", 10, 100),
            ["bbbb-account-0002"] = CreditPayload("max", 16, 31),
        }));

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKeys":["aaaa-account-0001","bbbb-account-0002"]}"""),
            CancellationToken.None);

        // One account is exhausted but the other still has quota, so the
        // channel stays available instead of being dropped from routing.
        Assert.Equal("available", quota.Status);
        Assert.True(quota.Ready);

        var accounts = Assert.IsType<List<ZhipuAccountQuota>>(quota.RawData["accounts"]);
        Assert.Equal(2, accounts.Count);
        Assert.Equal("0001", accounts[0].Suffix);
        Assert.Equal("exhausted", accounts[0].Status);
        Assert.Equal(2, accounts[0].Rows!.Count);
        Assert.Equal("0002", accounts[1].Suffix);
        Assert.Equal("available", accounts[1].Status);
        Assert.NotEmpty(accounts[0].Ref);
        Assert.NotEqual(accounts[0].Ref, accounts[1].Ref);

        // The channel keeps one binding limit per account inside one
        // availability group so the routing evaluator ORs the accounts.
        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal("zhipu_accounts", quota.Limits[0].AvailabilityGroup);
        Assert.Equal("zhipu_accounts", quota.Limits[1].AvailabilityGroup);
        Assert.Equal("weekly", quota.Limits[0].Window);
        Assert.Equal("exhausted", quota.Limits[0].Status);
        Assert.Equal("0001", quota.Limits[0].Account);
        Assert.Equal("available", quota.Limits[1].Status);
        Assert.Equal("0002", quota.Limits[1].Account);
        Assert.Equal(0.31, quota.Limits[1].UsageRatio, 3);
        Assert.Null(quota.Limits[0].PeriodStart);

        // The fallback rows stay available for the badge and the channel list:
        // the most used account per window, in five-hour then weekly order.
        var rows = Assert.IsType<List<ZhipuWindowRow>>(quota.RawData["rows"]);
        Assert.Equal(2, rows.Count);
        Assert.Equal(16.0, rows[0].UsedPercent, 3);
        Assert.Equal(100.0, rows[1].UsedPercent, 3);

        var (state, reason) = ProviderQuotaRouting.EvaluateQuotaRouting(
            quota.Limits,
            quota.Status,
            QuotaLimitTypes.Token,
            Now);
        Assert.Equal(QuotaRoutingStates.Open, state);
        Assert.True(string.IsNullOrEmpty(reason));
    }

    /// <summary>TestZhipu_CheckQuota_MultipleKeysAllExhausted (zhipu_checker_test.go:624-640).</summary>
    [Fact]
    public async Task MultipleKeysAllExhaustedCloseRouting()
    {
        var checker = CreateChecker(KeyedExecutor(new Dictionary<string, string>
        {
            ["aaaa-account-0001"] = CreditPayload("max", 10, 100),
            ["bbbb-account-0002"] = CreditPayload("max", 0, 100),
        }));

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKeys":["aaaa-account-0001","bbbb-account-0002"]}"""),
            CancellationToken.None);

        Assert.Equal("exhausted", quota.Status);
        Assert.False(quota.Ready);

        var (state, _) = ProviderQuotaRouting.EvaluateQuotaRouting(
            quota.Limits,
            quota.Status,
            QuotaLimitTypes.Token,
            Now);
        Assert.Equal(QuotaRoutingStates.Exhausted, state);
    }

    /// <summary>TestZhipu_CheckQuota_DisabledAccountKeepsRowsWithoutCapacity (zhipu_checker_test.go:642-678).</summary>
    [Fact]
    public async Task DisabledAccountKeepsRowsButContributesNoLimit()
    {
        var checker = CreateChecker(KeyedExecutor(new Dictionary<string, string>
        {
            ["aaaa-account-0001"] = CreditPayload("max", 0, 100),
            ["bbbb-account-0002"] = CreditPayload("max", 16, 31),
        }));

        var quota = await checker.CheckQuotaAsync(
            Channel(
                credentials: """{"apiKeys":["aaaa-account-0001","bbbb-account-0002"]}""",
                // The column is a JSON array of objects; a bare object is
                // malformed and reads as "no disabled keys" (C013).
                disabledApiKeys: "[" + DisabledEntry("aaaa-account-0001", Now.AddHours(1)) + "]"),
            CancellationToken.None);

        var accounts = Assert.IsType<List<ZhipuAccountQuota>>(quota.RawData["accounts"]);
        Assert.Equal(2, accounts.Count);

        // Serving keys are checked first, so parked keys can never hide them.
        Assert.Equal("0002", accounts[0].Suffix);
        Assert.False(accounts[0].Disabled);
        Assert.Equal("0001", accounts[1].Suffix);
        Assert.True(accounts[1].Disabled);
        Assert.Equal(2, accounts[1].Rows!.Count);

        // Only the serving account contributes a limit.
        var limit = Assert.Single(quota.Limits);
        Assert.Equal("0002", limit.Account);
        Assert.Equal("available", quota.Status);
    }

    /// <summary>TestZhipu_CheckQuota_EnabledFailuresWithDisabledSuccessIsError (zhipu_checker_test.go:680-702).</summary>
    [Fact]
    public async Task EnabledFailureCannotBeMaskedByADisabledSuccess()
    {
        // The only serving key is unreachable while the parked key answers
        // fine, so the channel status cannot be trusted.
        var checker = CreateChecker(KeyedExecutor(new Dictionary<string, string>
        {
            ["bbbb-account-0002"] = CreditPayload("max", 16, 31),
        }));

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(
                Channel(
                    credentials: """{"apiKeys":["aaaa-account-0001","bbbb-account-0002"]}""",
                    disabledApiKeys: "[" + DisabledEntry("bbbb-account-0002", Now.AddHours(1)) + "]"),
                CancellationToken.None));

        Assert.Contains("failed for all 1 enabled keys", error.Message, StringComparison.Ordinal);
    }

    /// <summary>TestZhipu_CheckQuota_DisabledOnlySingleKeyHasNoLimits (zhipu_checker_test.go:704-728).</summary>
    [Fact]
    public async Task DisabledOnlySingleKeyPublishesNoLimits()
    {
        var checker = CreateChecker(KeyedExecutor(new Dictionary<string, string>
        {
            ["aaaa-account-0001"] = CreditPayload("max", 10, 20),
        }));

        var quota = await checker.CheckQuotaAsync(
            Channel(
                credentials: """{"apiKey":"aaaa-account-0001"}""",
                disabledApiKeys: "[" + DisabledEntry("aaaa-account-0001", Now.AddHours(1)) + "]"),
            CancellationToken.None);

        Assert.Equal("exhausted", quota.Status);
        Assert.False(quota.Ready);
        Assert.Empty(quota.Limits);
    }

    /// <summary>TestZhipu_CheckQuota_OneAccountFailureKeepsTheRest (zhipu_checker_test.go:730-750).</summary>
    [Fact]
    public async Task OneAccountFailureKeepsTheOtherAccounts()
    {
        var checker = CreateChecker(KeyedExecutor(new Dictionary<string, string>
        {
            ["bbbb-account-0002"] = CreditPayload("max", 16, 31),
        }));

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKeys":["aaaa-account-0001","bbbb-account-0002"]}"""),
            CancellationToken.None);

        var accounts = Assert.IsType<List<ZhipuAccountQuota>>(quota.RawData["accounts"]);
        Assert.Equal(2, accounts.Count);
        Assert.Equal("unknown", accounts[0].Status);
        Assert.False(string.IsNullOrEmpty(accounts[0].Error));
        Assert.Null(accounts[1].Error);

        Assert.Equal("available", quota.Status);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal("0002", limit.Account);
    }

    // ------------------------------------------------------------------
    // Disabled-key bookkeeping
    // ------------------------------------------------------------------

    [Fact]
    public async Task ExpiredDisabledKeyIsCheckedAsAServingKey()
    {
        var checker = CreateChecker(KeyedExecutor(new Dictionary<string, string>
        {
            ["aaaa-account-0001"] = CreditPayload("max", 10, 20),
        }));

        var quota = await checker.CheckQuotaAsync(
            Channel(
                credentials: """{"apiKey":"aaaa-account-0001"}""",
                disabledApiKeys: "[" + DisabledEntry("aaaa-account-0001", Now.AddHours(-1)) + "]"),
            CancellationToken.None);

        var account = Assert.Single(Assert.IsType<List<ZhipuAccountQuota>>(quota.RawData["accounts"]));
        Assert.False(account.Disabled);
        Assert.Equal("available", quota.Status);
        Assert.Equal(2, quota.Limits.Count);
    }

    [Fact]
    public async Task DisabledEntryKeyIsTrimmedBeforeMatching()
    {
        var checker = CreateChecker(KeyedExecutor(new Dictionary<string, string>
        {
            ["aaaa-account-0001"] = CreditPayload("max", 10, 20),
        }));

        var quota = await checker.CheckQuotaAsync(
            Channel(
                credentials: """{"apiKey":"aaaa-account-0001"}""",
                disabledApiKeys: "[" + DisabledEntry("  aaaa-account-0001  ") + "]"),
            CancellationToken.None);

        var account = Assert.Single(Assert.IsType<List<ZhipuAccountQuota>>(quota.RawData["accounts"]));
        Assert.True(account.Disabled);
        Assert.Equal("exhausted", quota.Status);
        Assert.Empty(quota.Limits);
    }

    [Fact]
    public async Task DisabledKeysAreTruncatedAtThirtyTwoWithAWarning()
    {
        const long channelId = 42;
        const int keyCount = 40;
        var keys = Enumerable.Range(1, keyCount).Select(index => $"key-{index:D2}").ToArray();
        var disabled = "[" + string.Join(
            ",",
            keys.Select(key => DisabledEntry(key))) + "]";
        var credentials = "{\"apiKeys\":[" + string.Join(
            ",",
            keys.Select(key => "\"" + key + "\"")) + "]}";

        var logger = new RecordingLogger<ZhipuQuotaChecker>();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(SuccessBody),
        };
        var checker = CreateChecker(executor, logger);

        var quota = await checker.CheckQuotaAsync(
            Channel(id: channelId, credentials: credentials, disabledApiKeys: disabled),
            CancellationToken.None);

        // Every key is parked, so the 40 keys shrink to the 32 checked accounts,
        // and the channel has nothing left that can serve.
        Assert.Equal(32, executor.Requests.Count);
        var accounts = Assert.IsType<List<ZhipuAccountQuota>>(quota.RawData["accounts"]);
        Assert.Equal(32, accounts.Count);
        Assert.All(accounts, account => Assert.True(account.Disabled));
        Assert.Equal("exhausted", quota.Status);
        Assert.Empty(quota.Limits);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(
            "zhipu quota check truncates disabled keys: channel_id=42 disabled_keys=40 checked=32",
            entry.Message);
    }

    [Fact]
    public async Task AllKeysFailedReportsTheUntruncatedKeyCount()
    {
        const int keyCount = 40;
        var keys = Enumerable.Range(1, keyCount).Select(index => $"key-{index:D2}").ToArray();
        var disabled = "[" + string.Join(",", keys.Select(key => DisabledEntry(key))) + "]";
        var credentials = "{\"apiKeys\":[" + string.Join(
            ",",
            keys.Select(key => "\"" + key + "\"")) + "]}";

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new InvalidOperationException("boom"),
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(
                Channel(credentials: credentials, disabledApiKeys: disabled),
                CancellationToken.None));

        // Only the 32 bounded accounts are attempted, but the error counts the
        // channel's 40 keys, exactly like the reference.
        Assert.Equal(32, executor.Requests.Count);
        Assert.Equal(
            "zhipu quota check failed for all 40 keys: zhipu quota request failed: boom",
            error.Message);
    }

    [Fact]
    public async Task MalformedDisabledApiKeysColumnMeansNoDisabledKeys()
    {
        var checker = CreateChecker(KeyedExecutor(new Dictionary<string, string>
        {
            ["test-key"] = CreditPayload("max", 10, 20),
        }));

        var quota = await checker.CheckQuotaAsync(
            Channel(disabledApiKeys: "{\"not\":\"an array\""),
            CancellationToken.None);

        var account = Assert.Single(Assert.IsType<List<ZhipuAccountQuota>>(quota.RawData["accounts"]));
        Assert.False(account.Disabled);
        Assert.Equal("available", quota.Status);
    }

    // ------------------------------------------------------------------
    // Go JSON decoding
    // ------------------------------------------------------------------

    [Fact]
    public void DuplicateDataKeysMergeFieldByField()
    {
        var merged = ParseQuotaResponse(
            """{"data":{"level":"a"},"data":{"limits":[{"percentage":1}]}}""");
        var mergedData = merged.Data!;
        Assert.Equal("a", mergedData.Level);
        Assert.Single(mergedData.Limits!);

        // A later slice keeps the existing element structs and merges into them.
        var truncated = ParseQuotaResponse(
            """{"data":{"limits":[{"type":"A"},{"type":"B"}]},"data":{"limits":[{"percentage":2}]}}""");
        var limit = Assert.Single(truncated.Data!.Limits!);
        Assert.Equal("A", limit.Type);
        Assert.Equal(2.0, limit.Percentage, 3);

        // A longer duplicate slice appends zero elements before decoding.
        var grown = ParseQuotaResponse(
            """{"data":{"limits":[{"type":"A"}]},"data":{"limits":[{},{"type":"C"}]}}""");
        var grownLimits = grown.Data!.Limits!;
        Assert.Equal(2, grownLimits.Count);
        Assert.Equal("A", grownLimits[0].Type);
        Assert.Equal("C", grownLimits[1].Type);
    }

    [Fact]
    public void NullResetsPointersAndSlicesLikeGo()
    {
        var nullAfterObject = ParseQuotaResponse("""{"data":{"level":"a"},"data":null}""");
        Assert.Null(nullAfterObject.Data);

        var objectAfterNull = ParseQuotaResponse("""{"data":null,"data":{"level":"b"}}""");
        Assert.Equal("b", objectAfterNull.Data!.Level);

        var limitsNull = ParseQuotaResponse(
            """{"data":{"limits":[{"type":"A"}],"limits":null}}""");
        Assert.Null(limitsNull.Data!.Limits);
    }

    [Fact]
    public void FieldNamesFoldAsciiAndNullLeavesTheOldValue()
    {
        var folded = ParseQuotaResponse(
            """{"SUCCESS":true,"Data":{"LIMITS":[{"PERCENTAGE":3,"nextresettime":5,"TYPE":"TOKENS_LIMIT"}]}}""");
        Assert.True(folded.Success);
        var foldedLimit = Assert.Single(folded.Data!.Limits!);
        Assert.Equal(3.0, foldedLimit.Percentage, 3);
        Assert.Equal(5L, foldedLimit.NextResetTime);
        Assert.Equal("TOKENS_LIMIT", foldedLimit.Type);

        var nullIsNoOp = ParseQuotaResponse(
            """{"data":{"limits":[{"type":"A"}],"level":"x"},"data":{"limits":[{"type":null}],"level":null}}""");
        var noOpData = nullIsNoOp.Data!;
        var noOpLimit = Assert.Single(noOpData.Limits!);
        Assert.Equal("A", noOpLimit.Type);
        Assert.Equal("x", noOpData.Level);
    }

    [Theory]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public void IntegerFieldsAcceptTheInt64Bounds(string literal, long expected)
    {
        var response = ParseQuotaResponse(
            "{\"data\":{\"limits\":[{\"nextResetTime\":" + literal + "}]}}");

        Assert.Equal(expected, response.Data!.Limits![0].NextResetTime);
    }

    [Theory]
    [InlineData("1e308", 1e308)]
    [InlineData("1e-4000", 0.0)]
    [InlineData("5e-324", double.Epsilon)]
    public void FloatFieldsKeepGoRangeRulesForFiniteNumbers(string literal, double expected)
    {
        var response = ParseQuotaResponse(
            "{\"data\":{\"limits\":[{\"percentage\":" + literal + "}]}}");

        Assert.Equal(expected, response.Data!.Limits![0].Percentage);
    }

    /// <summary>
    /// Go's decoder reports type errors with the struct field path; the port
    /// reproduces the message, including the numeric literal only for numeric
    /// scalar fields (C217).
    /// </summary>
    [Theory]
    [InlineData(
        """{"success":1}""",
        "json: cannot unmarshal number into Go struct field zhipuQuotaResponse.success of type bool")]
    [InlineData(
        """{"success":"true"}""",
        "json: cannot unmarshal string into Go struct field zhipuQuotaResponse.success of type bool")]
    [InlineData(
        """{"msg":1}""",
        "json: cannot unmarshal number into Go struct field zhipuQuotaResponse.msg of type string")]
    [InlineData(
        """{"code":"1"}""",
        "json: cannot unmarshal string into Go struct field zhipuQuotaResponse.code of type int")]
    [InlineData(
        """{"code":1.5}""",
        "json: cannot unmarshal number 1.5 into Go struct field zhipuQuotaResponse.code of type int")]
    [InlineData(
        """{"data":5}""",
        "json: cannot unmarshal number into Go struct field zhipuQuotaResponse.data of type provider_quota.zhipuQuotaData")]
    [InlineData(
        """{"data":[]}""",
        "json: cannot unmarshal array into Go struct field zhipuQuotaResponse.data of type provider_quota.zhipuQuotaData")]
    [InlineData(
        """{"data":true}""",
        "json: cannot unmarshal bool into Go struct field zhipuQuotaResponse.data of type provider_quota.zhipuQuotaData")]
    [InlineData(
        """{"data":"x"}""",
        "json: cannot unmarshal string into Go struct field zhipuQuotaResponse.data of type provider_quota.zhipuQuotaData")]
    [InlineData(
        """{"data":{"limits":{}}}""",
        "json: cannot unmarshal object into Go struct field zhipuQuotaData.data.limits of type []provider_quota.zhipuLimitEntry")]
    [InlineData(
        """{"data":{"limits":[5]}}""",
        "json: cannot unmarshal number into Go struct field zhipuQuotaData.data.limits of type provider_quota.zhipuLimitEntry")]
    [InlineData(
        """{"data":{"limits":[{"type":1}]}}""",
        "json: cannot unmarshal number into Go struct field zhipuLimitEntry.data.limits.type of type string")]
    [InlineData(
        """{"data":{"limits":[{"percentage":"1"}]}}""",
        "json: cannot unmarshal string into Go struct field zhipuLimitEntry.data.limits.percentage of type float64")]
    [InlineData(
        """{"data":{"limits":[{"percentage":true}]}}""",
        "json: cannot unmarshal bool into Go struct field zhipuLimitEntry.data.limits.percentage of type float64")]
    [InlineData(
        """{"data":{"limits":[{"percentage":1e400}]}}""",
        "json: cannot unmarshal number 1e400 into Go struct field zhipuLimitEntry.data.limits.percentage of type float64")]
    [InlineData(
        """{"data":{"limits":[{"percentage":-1e400}]}}""",
        "json: cannot unmarshal number -1e400 into Go struct field zhipuLimitEntry.data.limits.percentage of type float64")]
    [InlineData(
        """{"data":{"limits":[{"nextResetTime":{}}]}}""",
        "json: cannot unmarshal object into Go struct field zhipuLimitEntry.data.limits.nextResetTime of type int64")]
    [InlineData(
        """{"data":{"limits":[{"nextResetTime":true}]}}""",
        "json: cannot unmarshal bool into Go struct field zhipuLimitEntry.data.limits.nextResetTime of type int64")]
    [InlineData(
        "5",
        "json: cannot unmarshal number into Go value of type provider_quota.zhipuQuotaResponse")]
    [InlineData(
        "\"x\"",
        "json: cannot unmarshal string into Go value of type provider_quota.zhipuQuotaResponse")]
    [InlineData(
        "[]",
        "json: cannot unmarshal array into Go value of type provider_quota.zhipuQuotaResponse")]
    [InlineData(
        "true",
        "json: cannot unmarshal bool into Go value of type provider_quota.zhipuQuotaResponse")]
    public void WrongJsonTypesReproduceTheGoUnmarshalErrors(string json, string expectedMessage)
    {
        var error = Assert.Throws<JsonException>(
            () => ZhipuQuotaChecker.ParseQuotaResponse(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(expectedMessage, error.Message);
    }

    [Theory]
    [InlineData("null", 0)]
    [InlineData("{}", 0)]
    [InlineData("""{"code":200}""", 200)]
    public async Task MissingSuccessIsAnApiErrorWithTheCode(
        string body,
        int expectedCode)
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(body);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Contains(
            "zhipu API error: API error code " + expectedCode.ToString(CultureInfo.InvariantCulture),
            error.Message,
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Window mapping
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("""[{"type":"TOKENS_LIMIT","percentage":1,"nextResetTime":1000}]""", "five_hour")]
    [InlineData(
        """[{"type":"TOKENS_LIMIT","percentage":1,"nextResetTime":1000},{"type":"TOKENS_LIMIT","percentage":2,"nextResetTime":2000}]""",
        "five_hour,weekly_limit")]
    [InlineData(
        """[{"type":"TOKENS_LIMIT","percentage":1,"nextResetTime":1000},{"type":"TOKENS_LIMIT","percentage":2}]""",
        "five_hour,weekly_limit")]
    [InlineData(
        """[{"type":"TOKENS_LIMIT","percentage":1,"nextResetTime":1000},{"type":"TOKENS_LIMIT","percentage":2,"nextResetTime":2000},{"type":"TOKENS_LIMIT","percentage":3,"nextResetTime":3000}]""",
        "five_hour,weekly_limit")]
    [InlineData(
        """[{"type":"CREDIT_LIMIT","percentage":1,"unit":3,"number":5},{"type":"CREDIT_LIMIT","percentage":2,"unit":6,"number":1}]""",
        "five_hour,weekly_limit")]
    [InlineData(
        """[{"type":"CREDIT_LIMIT","percentage":1,"unit":4,"number":5},{"type":"CREDIT_LIMIT","percentage":2,"unit":6,"number":1}]""",
        "five_hour,weekly_limit")]
    [InlineData(
        """[{"type":"CREDIT_LIMIT","percentage":1,"unit":3,"number":0},{"type":"CREDIT_LIMIT","percentage":2,"unit":6,"number":1}]""",
        "five_hour,weekly_limit")]
    [InlineData(
        """[{"type":"TOKENS_LIMIT","percentage":1},{"type":"CREDIT_LIMIT","percentage":2,"unit":6,"number":1}]""",
        "weekly_limit")]
    [InlineData(
        """[{"type":"CREDIT_LIMIT","percentage":2,"unit":6,"number":1},{"type":"CREDIT_LIMIT","percentage":1,"unit":3,"number":2251799813685253}]""",
        "weekly_limit,five_hour")]
    [InlineData(
        """[{"type":"CREDIT_LIMIT","percentage":1,"unit":6,"number":281474976710657}]""",
        "weekly_limit")]
    [InlineData(
        """[{"type":"credit_limit","percentage":1,"unit":3,"number":5},{"type":"Credit_Limit","percentage":2,"unit":6,"number":1}]""",
        "five_hour,weekly_limit")]
    public void ResponseWindowsMapUnitNumberThenApiPosition(string limits, string expected)
    {
        var response = ParseQuotaResponse(
            "{\"data\":{\"limits\":" + limits + "}}");
        var windows = ZhipuQuotaChecker.ResponseWindows(response.Data!.Limits);

        Assert.Equal(
            expected,
            string.Join(",", windows.Select(window => window.Spec.Name)));
    }

    // ------------------------------------------------------------------
    // Status classification
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, "available")]
    [InlineData(0.799, "available")]
    [InlineData(0.8, "warning")]
    [InlineData(0.999, "warning")]
    [InlineData(1.0, "exhausted")]
    [InlineData(1.5, "exhausted")]
    [InlineData(-0.5, "available")]
    [InlineData(double.PositiveInfinity, "exhausted")]
    public void StatusForRatioUsesTheWarningAndExhaustedThresholds(
        double ratio,
        string expected)
        => Assert.Equal(expected, ZhipuQuotaChecker.StatusForRatio(ratio));

    [Fact]
    public void StatusForRatioTreatsNaNAsAvailable()
    {
        // Both Go comparisons are false for NaN, so the row stays available.
        Assert.Equal("available", ZhipuQuotaChecker.StatusForRatio(double.NaN));
    }

    [Theory]
    [InlineData("available", "warning", "warning")]
    [InlineData("warning", "exhausted", "exhausted")]
    [InlineData("exhausted", "available", "exhausted")]
    // The Go rank map has no "unknown" (or empty) key, so the lookup yields the
    // zero value and the helper returns the current status unchanged.
    [InlineData("unknown", "available", "unknown")]
    [InlineData("unknown", "warning", "warning")]
    [InlineData("available", "unknown", "available")]
    public void WorseStatusKeepsTheHigherRank(string current, string candidate, string expected)
        => Assert.Equal(expected, ZhipuQuotaChecker.WorseStatus(current, candidate));

    [Theory]
    [InlineData("available", 0)]
    [InlineData("warning", 1)]
    [InlineData("exhausted", 2)]
    [InlineData("unknown", 0)]
    [InlineData("", 0)]
    [InlineData("weird", 0)]
    public void CheckerStatusRankMatchesTheLocalMap(string status, int expected)
        => Assert.Equal(expected, ZhipuQuotaChecker.CheckerStatusRank(status));

    [Fact]
    public async Task NormalizationClampsRatiosAboveOne()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(TokensBody("standard", 150.0));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(1.0, limit.UsageRatio, 3);
        Assert.Equal("exhausted", limit.Status);
        Assert.Equal("exhausted", quota.Status);

        var rows = Assert.IsType<List<ZhipuWindowRow>>(quota.RawData["rows"]);
        Assert.Equal(150.0, rows[0].UsedPercent, 3);
    }

    // ------------------------------------------------------------------
    // Aggregation helpers
    // ------------------------------------------------------------------

    [Fact]
    public void AggregateStatusSkipsExhaustedAccounts()
    {
        var exhausted = new ZhipuAccountQuota
        {
            Status = "exhausted",
            Rows = [new ZhipuWindowRow { Window = "five_hour" }],
        };
        var available = new ZhipuAccountQuota
        {
            Status = "available",
            Rows = [new ZhipuWindowRow { Window = "five_hour" }],
        };

        Assert.Equal(("available", true), ZhipuQuotaChecker.AggregateStatus([exhausted, available]));
        Assert.Equal(("exhausted", false), ZhipuQuotaChecker.AggregateStatus([exhausted, exhausted]));
        Assert.Equal(("exhausted", false), ZhipuQuotaChecker.AggregateStatus([]));
    }

    [Fact]
    public void BindingLimitsChooseTheMostUsedKnownWindow()
    {
        var account = new ZhipuAccountQuota
        {
            Suffix = "0001",
            Rows =
            [
                new ZhipuWindowRow { Window = "five_hour", UsedPercent = 10, Status = "available" },
                new ZhipuWindowRow { Window = "weekly_limit", UsedPercent = 31, Status = "available" },
                new ZhipuWindowRow { Window = "other", UsedPercent = 99, Status = "exhausted" },
            ],
        };

        var limit = Assert.Single(ZhipuQuotaChecker.BindingLimits([account]));
        Assert.Equal("weekly", limit.Window);
        Assert.Equal(0.31, limit.UsageRatio, 3);
        Assert.Equal("0001", limit.Account);

        // A tie keeps the first known window (strict greater-than in Go).
        account.Rows =
        [
            new ZhipuWindowRow { Window = "five_hour", UsedPercent = 50, Status = "warning" },
            new ZhipuWindowRow { Window = "weekly_limit", UsedPercent = 50, Status = "warning" },
        ];
        limit = Assert.Single(ZhipuQuotaChecker.BindingLimits([account]));
        Assert.Equal("5h", limit.Window);
    }

    [Fact]
    public void WorstRowsKeepTheFirstAccountOnATie()
    {
        var accounts = new List<ZhipuAccountQuota>
        {
            new()
            {
                Suffix = "0001",
                Rows =
                [
                    new ZhipuWindowRow { Window = "five_hour", UsedPercent = 40, Remaining = 1 },
                    new ZhipuWindowRow { Window = "weekly_limit", UsedPercent = 20, Remaining = 9 },
                ],
            },
            new()
            {
                Suffix = "0002",
                Rows =
                [
                    new ZhipuWindowRow { Window = "five_hour", UsedPercent = 40, Remaining = 2 },
                    new ZhipuWindowRow { Window = "weekly_limit", UsedPercent = 10, Remaining = 8 },
                ],
            },
        };

        var rows = ZhipuQuotaChecker.WorstRows(accounts);

        Assert.Equal(2, rows.Count);
        Assert.Equal("five_hour", rows[0].Window);
        Assert.Equal(1L, rows[0].Remaining);
        Assert.Equal("weekly_limit", rows[1].Window);
        Assert.Equal(9L, rows[1].Remaining);
    }

    [Fact]
    public void AggregateLevelAndEarliestResetWalkTheAccountList()
    {
        var accounts = new List<ZhipuAccountQuota>
        {
            new()
            {
                Level = null,
                Rows = [new ZhipuWindowRow { ResetAt = "2026-07-20T00:00:00Z" }],
            },
            new()
            {
                Level = "max",
                Rows = [new ZhipuWindowRow { ResetAt = "2026-07-18T00:00:00Z" }],
            },
        };

        Assert.Equal("max", ZhipuQuotaChecker.AggregateLevel(accounts));
        Assert.Equal(
            new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero),
            ZhipuQuotaChecker.EarliestReset(accounts));
    }

    // ------------------------------------------------------------------
    // Reset timestamps
    // ------------------------------------------------------------------

    [Fact]
    public async Task ResetTextDropsSubsecondsAndParsesBack()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse("""
            {"success":true,"data":{"limits":[
                {"type":"TOKENS_LIMIT","percentage":1,"nextResetTime":1784541710987}]}}
            """);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var row = Assert.Single(Assert.IsType<List<ZhipuWindowRow>>(quota.RawData["rows"]));
        Assert.Equal("2026-07-20T10:01:50Z", row.ResetAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1784541710000), quota.Limits[0].NextResetAt);
    }

    [Fact]
    public async Task OutOfRangeResetMomentIsDropped()
    {
        // Go's time.UnixMilli spans years far outside DateTimeOffset; the port
        // omits both the raw text and the parsed reset (C217).
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(
            """{"success":true,"data":{"limits":[{"type":"TOKENS_LIMIT","percentage":1,"nextResetTime":9223372036854775807}]}}""");

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var row = Assert.Single(Assert.IsType<List<ZhipuWindowRow>>(quota.RawData["rows"]));
        Assert.Null(row.ResetAt);
        Assert.Null(quota.NextResetAt);
        Assert.Null(quota.Limits[0].NextResetAt);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonPositiveResetIsIgnored(long resetTime)
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(
            "{\"success\":true,\"data\":{\"limits\":["
            + "{\"type\":\"TOKENS_LIMIT\",\"percentage\":96,\"nextResetTime\":1700000000000},"
            + "{\"type\":\"TOKENS_LIMIT\",\"percentage\":1,\"nextResetTime\":"
            + resetTime.ToString(CultureInfo.InvariantCulture)
            + "}]}}");

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        // A bucket without a positive reset leads the API-order fallback.
        var rows = Assert.IsType<List<ZhipuWindowRow>>(quota.RawData["rows"]);
        Assert.Equal("five_hour", rows[0].Window);
        Assert.Equal(1.0, rows[0].UsedPercent, 3);
        Assert.Null(rows[0].ResetAt);
        Assert.Equal("weekly_limit", rows[1].Window);
        Assert.Equal(96.0, rows[1].UsedPercent, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("10000-01-01T08:00:00+08:00")]
    [InlineData("2026-07-20 10:01:50")]
    public void UnparseableResetTextYieldsNoReset(string resetAt)
    {
        var row = new ZhipuWindowRow { ResetAt = resetAt };

        Assert.Null(ZhipuQuotaChecker.RowResetAt(row));
    }

    // ------------------------------------------------------------------
    // Credentials
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("""{"apiKey":"a"}""", "a")]
    [InlineData("""{"apiKeys":["a","b"]}""", "a,b")]
    [InlineData("""{"apiKey":"legacy","apiKeys":["a"]}""", "legacy,a")]
    [InlineData("""{"apiKey":"  a  ","apiKeys":[" a ","b"]}""", "a,b")]
    [InlineData("""{"apiKey":"","apiKeys":["","  ","b"]}""", "b")]
    [InlineData("""{"apiKey":"   "}""", "")]
    [InlineData("", "")]
    [InlineData("not json", "")]
    [InlineData("""{"oauth":{"access_token":"t"}}""", "")]
    [InlineData("""{"apiKey":"legacy","apiKeys":["a"],"oauth":{"access_token":"t"}}""", "a")]
    public void ChannelApiKeysTrimDedupeAndSkipBlankEntries(string credentials, string expected)
    {
        var keys = ZhipuQuotaChecker.ChannelApiKeys(Channel(credentials: credentials));

        Assert.Equal(expected, string.Join(",", keys));
    }

    [Fact]
    public void KeySuffixKeepsTheTrailingRunes()
    {
        Assert.Equal("-key", ZhipuQuotaChecker.KeySuffix("test-key"));
        Assert.Equal("abcd", ZhipuQuotaChecker.KeySuffix("abcd"));
        Assert.Equal("键三键四", ZhipuQuotaChecker.KeySuffix("键三键四"));
        Assert.Equal("二键三四", ZhipuQuotaChecker.KeySuffix("一键二键三四"));
        Assert.Equal("0002", ZhipuQuotaChecker.KeySuffix("bbbb-account-0002"));
    }

    // ------------------------------------------------------------------
    // Capability and URL
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("zhipu", true)]
    [InlineData("zhipu_anthropic", true)]
    [InlineData("zai", false)]
    [InlineData("zai_anthropic", false)]
    [InlineData("openai", false)]
    [InlineData("ollama", false)]
    public void SupportsChannelOnlyMatchesTheZhiPuTypes(string channelType, bool expected)
    {
        // TestZhipu_SupportsChannel (zhipu_checker_test.go:426-434).
        var checker = CreateChecker(new ScriptedExecutor());

        Assert.Equal(expected, checker.SupportsChannel(new ChannelRecord { Type = channelType }));
    }

    [Theory]
    [InlineData("https://open.bigmodel.cn/api/paas/v4")]
    [InlineData("https://open.bigmodel.cn/api/anthropic")]
    [InlineData("https://api.z.ai/api/paas/v4")]
    [InlineData("")]
    [InlineData("not a URL")]
    [InlineData(null)]
    public void BuildQuotaUrlAlwaysReturnsTheBigModelEndpoint(string? baseUrl)
    {
        // TestBuildZhipuQuotaURL (zhipu_checker_test.go:436-441).
        Assert.Equal(DefaultQuotaUrl, ZhipuQuotaChecker.BuildQuotaUrl(baseUrl));
    }

    // ------------------------------------------------------------------
    // Executor seams
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaRoutesThroughTheChannelProxyExecutor()
    {
        var direct = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new InvalidOperationException("direct executor used"),
        };
        var proxied = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(SuccessBody),
        };
        var factory = new ScriptedProxyExecutorFactory { Executor = proxied };
        var checker = CreateChecker(direct, factory: factory);

        var quota = await checker.CheckQuotaAsync(
            Channel(
                settings: """{"proxy":{"type":"url","url":"http://proxy.local:8080","username":"user","password":"pass","disableConnectionReuse":true}}"""),
            CancellationToken.None);

        Assert.Equal("available", quota.Status);
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
    public async Task MalformedSettingsFallBackToTheDirectExecutor()
    {
        var direct = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(SuccessBody),
        };
        var factory = new ScriptedProxyExecutorFactory();
        var checker = CreateChecker(direct, factory: factory);

        await checker.CheckQuotaAsync(Channel(settings: "{\"proxy\":{"), CancellationToken.None);

        Assert.Empty(factory.Requests);
        Assert.Single(direct.Requests);
    }

    [Fact]
    public async Task TransportFailureIsWrappedAndCountedPerKey()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new InvalidOperationException("boom"),
        });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "zhipu quota check failed for all 1 keys: zhipu quota request failed: boom",
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
                DefaultQuotaUrl,
                statusCode,
                status,
                Encoding.UTF8.GetBytes("""{"error":"nope"}""")),
        });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        // Go wraps the *httpclient.Error verbatim: "%s - %s with status %s".
        Assert.Contains(
            "zhipu quota request failed: GET - "
                + DefaultQuotaUrl
                + " with status "
                + status,
            error.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(204)]
    public async Task Non200SuccessStatusesAreStillParsed(int statusCode)
    {
        // The reference performs no explicit status check: its HTTP client
        // already reports >=400 as an error and anything below that is parsed.
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = statusCode,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
                Body = Encoding.UTF8.GetBytes(SuccessBody),
            },
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal("available", quota.Status);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ZhipuQuotaChecker CreateChecker(
        out ClaudeCodeQuotaCheckerTests.RecordingHttpHandler handler,
        ILogger<ZhipuQuotaChecker>? logger = null)
    {
        handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new ZhipuQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now),
            logger ?? NullLogger<ZhipuQuotaChecker>.Instance);
    }

    private static ZhipuQuotaChecker CreateChecker(
        ScriptedExecutor executor,
        ILogger<ZhipuQuotaChecker>? logger = null,
        ScriptedProxyExecutorFactory? factory = null)
        => new(
            executor,
            factory ?? new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now),
            logger ?? NullLogger<ZhipuQuotaChecker>.Instance);

    private static ChannelRecord Channel(
        string? credentials = null,
        string settings = "{}",
        string type = "zhipu",
        string? baseUrl = "https://open.bigmodel.cn/api/paas/v4",
        string? disabledApiKeys = null,
        long id = 0)
        => new()
        {
            Id = id,
            Type = type,
            BaseUrl = baseUrl,
            Credentials = credentials ?? DefaultCredentials,
            DisabledApiKeys = disabledApiKeys,
            Settings = settings,
        };

    private static ZhipuQuotaResponse ParseQuotaResponse(string json)
        => ZhipuQuotaChecker.ParseQuotaResponse(Encoding.UTF8.GetBytes(json));

    private static string TokensBody(string level, double percentage)
        => string.Create(
            CultureInfo.InvariantCulture,
            $$"""
            {
                "success": true,
                "code": 200,
                "data": {
                    "level": "{{level}}",
                    "limits": [
                        {"type": "TOKENS_LIMIT", "percentage": {{percentage}}}
                    ]
                }
            }
            """);

    /// <summary>
    /// zhipuCreditLimitPayload (zhipu_checker_test.go:508-514): the GLM coding
    /// plan shape where every window is a CREDIT_LIMIT with unit/number.
    /// </summary>
    private static string CreditPayload(
        string level,
        double fiveHourPercent,
        double weeklyPercent)
        => string.Create(
            CultureInfo.InvariantCulture,
            $$$"""
            {"success":true,"code":200,"data":{"level":"{{{level}}}","limits":[
                {"type":"CREDIT_LIMIT","percentage":{{{fiveHourPercent}}},"unit":3,"number":5,"usage":28000,"currentValue":2896,"remaining":25103,"nextResetTime":4102731600000},
                {"type":"CREDIT_LIMIT","percentage":{{{weeklyPercent}}},"unit":6,"number":1,"usage":140000,"currentValue":140019,"remaining":0,"nextResetTime":4103300000000}]}}
            """);

    /// <summary>
    /// zhipuKeyedHTTPClient (zhipu_checker_test.go:516-533): each key gets its
    /// own payload; unknown keys answer with the provider's auth error.
    /// </summary>
    private static ScriptedExecutor KeyedExecutor(Dictionary<string, string> payloads)
        => new()
        {
            OnDo = (_, request, _) =>
            {
                var key = HttpClientUtils.HeaderGet(request.Headers, "Authorization");
                var body = payloads.TryGetValue(key, out var payload)
                    ? payload
                    : """{"success":false,"code":401,"msg":"Authentication parameter not received"}""";
                return Ok(body);
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

    private static string DisabledEntry(string key, DateTimeOffset? expiresAt = null)
    {
        var expires = expiresAt is { } value
            ? ",\"expiresAt\":\"" + value.ToUniversalTime().ToString(
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture) + "\""
            : string.Empty;

        return "{\"key\":\"" + key
            + "\",\"disabledAt\":\"2026-07-17T00:00:00Z\",\"errorCode\":429"
            + ",\"reason\":\"rate limited\"" + expires + "}";
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
