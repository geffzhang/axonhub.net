using System.Text;
using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports internal/server/biz/provider_quota/cline_checker.go together with the
/// reference cline_checker_test.go: the five-request wire flow, the official
/// usage-limits windows, the supplementary ClinePass cost ledger, the
/// direct-credit informational branch, the safe error taxonomy, and the exact
/// decoder semantics of the custom clineUsageLimit unmarshaller.
/// </summary>
public sealed class ClineQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 10, 30, 0, TimeSpan.Zero);

    private const string ApiKey = "test-api-key";

    private const string MeBody = """{"data":{"id":"user_test","organizations":[]}}""";

    private const string PlansBody = """
        {"data":[{"type":"individual","interval":"Monthly","isActive":true,"entitlements":{"cline_pass":{"enabled":true,"inferenceCapThreshold":{"last5HoursUsageCostUSDPerUser":1000000000,"last7daysUsageCostUSDPerUser":2500000000,"last30daysUsageCostUSDPerUser":5000000000}}}}]}
        """;

    private const string SmallPlansBody = """
        {"data":[{"type":"individual","interval":"Monthly","isActive":true,"entitlements":{"cline_pass":{"enabled":true,"inferenceCapThreshold":{"last5HoursUsageCostUSDPerUser":100,"last7daysUsageCostUSDPerUser":200,"last30daysUsageCostUSDPerUser":400}}}}]}
        """;

    private const string BalanceBody = """{"data":{"balance":497582}}""";

    private const string UsageLimitsBody = """
        {"data":{"limits":[{"type":"five_hour","percentUsed":10,"resetsAt":"2026-07-07T14:00:00Z"},{"type":"weekly","percentUsed":79,"resetsAt":"2026-07-14T10:18:10Z"},{"type":"monthly","percentUsed":49,"resetsAt":"2026-08-01T11:13:17Z"}]}}
        """;

    private const string UsagesBody = """
        {"data":{"items":[{"createdAt":"2026-07-07T10:18:10Z","costUsd":462,"creditsUsed":0,"aiModelTypeName":"cline-pass"},{"createdAt":"2026-07-02T10:31:31Z","costUsd":497184013,"creditsUsed":0,"aiModelTypeName":"cline-pass"}]}}
        """;

    private static readonly string[] SensitiveMarkers =
    [
        "person@example.test",
        "sk-sensitive-test-key",
        "user_sensitive_123",
        "cursor_sensitive_456",
        "acct_sensitive_456",
        "gen_sensitive_789",
        "payment_id",
        "pay_sensitive_000",
        "/api/v1/users/",
    ];

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaFetchesEveryEndpointAndReportsOfficialWindows()
    {
        var executor = HappyExecutor();
        var checker = CreateChecker(executor);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(5, executor.Requests.Count);
        Assert.Equal("https://api.cline.bot/api/v1/users/me", executor.Requests[0].Url);
        Assert.Equal("https://api.cline.bot/api/v1/plans", executor.Requests[1].Url);
        Assert.Equal("https://api.cline.bot/api/v1/users/user_test/balance", executor.Requests[2].Url);
        Assert.Equal(
            "https://api.cline.bot/api/v1/users/me/plan/usage-limits",
            executor.Requests[3].Url);
        Assert.Equal(
            "https://api.cline.bot/api/v1/users/user_test/usages?limit=200",
            executor.Requests[4].Url);

        foreach (var request in executor.Requests)
        {
            Assert.Equal("GET", request.Method);
            Assert.Equal("Bearer " + ApiKey, HttpClientUtils.HeaderGet(request.Headers, "Authorization"));
            Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Accept"));
            Assert.Equal("axonhub/1.0", HttpClientUtils.HeaderGet(request.Headers, "User-Agent"));
            Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        }

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal("cline", quota.ProviderType);
        Assert.Equal(3, quota.Limits.Count);
        Assert.Equal(new DateTimeOffset(2026, 7, 7, 14, 0, 0, TimeSpan.Zero), quota.NextResetAt);

        Assert.Equal(QuotaWindows.FiveHours, quota.Limits[0].Window);
        Assert.Equal(0.10, Assert.IsType<double>(quota.Limits[0].UsageRatio), 9);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[0].Status);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 7, 9, 0, 0, TimeSpan.Zero),
            quota.Limits[0].PeriodStart);

        Assert.Equal(QuotaWindows.SevenDays, quota.Limits[1].Window);
        Assert.Equal(0.79, Assert.IsType<double>(quota.Limits[1].UsageRatio), 9);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[1].Status);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 7, 10, 18, 10, TimeSpan.Zero),
            quota.Limits[1].PeriodStart);

        Assert.Equal(QuotaWindows.ThirtyDays, quota.Limits[2].Window);
        Assert.Equal(0.49, Assert.IsType<double>(quota.Limits[2].UsageRatio), 9);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[2].Status);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 2, 11, 13, 17, TimeSpan.Zero),
            quota.Limits[2].PeriodStart);

        var raw = quota.RawData;
        Assert.Equal("cline_pass_only", raw["model_scope"]);
        Assert.Equal("cline_pass_windows", raw["status_basis"]);
        Assert.Equal("cline_pass", raw["pool"]);
        Assert.Equal(100_000_000L, Assert.IsType<long>(raw["cost_scale"]));
        Assert.False(raw.ContainsKey("user_id"));
        Assert.False(raw.ContainsKey("email"));

        var plans = Assert.IsAssignableFrom<List<Dictionary<string, object?>>>(raw["plans"]);
        var plan = Assert.Single(plans);
        Assert.Equal("individual", Assert.IsType<string>(plan["type"]));
        Assert.Equal("Monthly", Assert.IsType<string>(plan["interval"]));

        var balance = Assert.IsType<Dictionary<string, object?>>(raw["balance"]);
        Assert.Equal(497582L, Assert.IsType<long>(balance["raw_balance"]));

        var last7d = Window(quota, "last7d");
        Assert.Equal(0.79, Assert.IsType<double>(last7d["usage_ratio"]), 9);
        Assert.Equal(79.0, Assert.IsType<double>(last7d["usage_percent"]), 4);
        Assert.Equal(462L, Assert.IsType<long>(last7d["used_cost_units"]));
        Assert.Equal(0.0000001848, Assert.IsType<double>(last7d["cost_usage_ratio"]), 9);
        Assert.Equal(0.00001848, Assert.IsType<double>(last7d["cost_usage_percent"]), 8);
        Assert.Equal("2026-07-14T10:18:10Z", Assert.IsType<string>(last7d["next_reset_at"]));
        Assert.Equal("official_usage_limits", Assert.IsType<string>(last7d["usage_source"]));
        Assert.Equal("official_usage_limits", Assert.IsType<string>(last7d["reset_source"]));
        Assert.Equal("cline_pass_ledger_official_window", Assert.IsType<string>(last7d["cost_source"]));

        var last30d = Window(quota, "last30d");
        Assert.Equal(462L, Assert.IsType<long>(last30d["used_cost_units"]));
        Assert.Equal(
            "2026-07-02T11:13:17Z",
            Assert.IsType<string>(last30d["window_start_at"]));

        var usageFetch = UsageFetch(quota);
        Assert.Equal(1, Assert.IsType<int>(usageFetch["pages"]));
        Assert.Equal(2, Assert.IsType<int>(usageFetch["items_seen"]));
        Assert.Equal(2, Assert.IsType<int>(usageFetch["cline_pass_items_seen"]));
        Assert.Equal(0, Assert.IsType<int>(usageFetch["direct_items_seen"]));
        Assert.Equal(0, Assert.IsType<int>(usageFetch["unclassified_items_seen"]));
        Assert.Equal(0, Assert.IsType<int>(usageFetch["invalid_timestamp_items"]));
        Assert.False(Assert.IsType<bool>(usageFetch["truncated"]));
        Assert.False(Assert.IsType<bool>(usageFetch["ledger_unavailable"]));
        Assert.False(usageFetch.ContainsKey("ledger_error_code"));

        var limitsFetch = UsageLimitsFetch(quota);
        Assert.Equal("complete", Assert.IsType<string>(limitsFetch["status"]));
        Assert.Equal(3, Assert.IsType<int>(limitsFetch["entries_seen"]));
        Assert.Equal(3, Assert.IsType<int>(limitsFetch["recognized_entries"]));
        Assert.Equal(3, Assert.IsType<int>(limitsFetch["usable_windows"]));
        Assert.Equal(6, Assert.IsType<int>(limitsFetch["usable_fields"]));
    }

    [Theory]
    [InlineData("""["cline-pass/deepseek-v4-flash"]""", "exhausted", false, "cline_pass_unavailable")]
    [InlineData("""["cline-pass/deepseek-v4-flash","zai/glm-5.2"]""", "warning", true, "cline_pass_unavailable_mixed_pool")]
    [InlineData("null", "warning", true, "cline_pass_unavailable")]
    public async Task UsageLimitsNotFoundMarksPassUnavailable(
        string supportedModels,
        string expectedStatus,
        bool expectedReady,
        string expectedStatusBasis)
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call switch
            {
                0 => Ok(MeBody),
                1 => Ok(SmallPlansBody),
                2 => Ok(BalanceBody),
                3 => Status(404, """{"error":"not found"}"""),
                _ => throw new InvalidOperationException("unexpected Cline request " + call),
            },
        };
        var checker = CreateChecker(executor);

        var quota = await checker.CheckQuotaAsync(
            Channel(supportedModels: supportedModels),
            CancellationToken.None);

        Assert.Equal(4, executor.Requests.Count);
        Assert.Equal(expectedStatus, quota.Status);
        Assert.Equal(expectedReady, quota.Ready);
        Assert.Null(quota.NextResetAt);
        Assert.Empty(quota.Limits);
        Assert.Equal(expectedStatusBasis, quota.RawData["status_basis"]);
        Assert.Equal("unavailable", quota.RawData["pass_state"]);
        Assert.False(quota.RawData.ContainsKey("windows"));
        Assert.False(quota.RawData.ContainsKey("usage_fetch"));

        var balance = Assert.IsType<Dictionary<string, object?>>(quota.RawData["balance"]);
        Assert.Equal(497582L, Assert.IsType<long>(balance["raw_balance"]));
        var limitsFetch = UsageLimitsFetch(quota);
        Assert.Equal("cline_pass_unavailable", Assert.IsType<string>(limitsFetch["status"]));
    }

    [Fact]
    public async Task UserIdentityNotFoundRemainsFailure()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Status(404, """{"error":"not found"}"""),
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("failed to read Cline user identity: HTTP 404 Not Found", error.Message);
        Assert.Single(executor.Requests);
    }

    [Theory]
    [InlineData(401, "Unauthorized")]
    [InlineData(403, "Forbidden")]
    [InlineData(429, "Too Many Requests")]
    [InlineData(500, "Internal Server Error")]
    public void UsageLimitsNonNotFoundErrorsRemainFailures(int statusCode, string statusText)
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Status(statusCode, """{"error":"upstream failure"}"""),
        };
        var checker = CreateChecker(executor);

        var outcome = checker.FetchUsageLimits(
            executor,
            "https://api.cline.bot/v1",
            ApiKey,
            CancellationToken.None);

        Assert.NotNull(outcome.Error);
        Assert.Null(outcome.Limits);
        Assert.Equal("unavailable", outcome.Meta.Status);
        Assert.Equal(
            "failed to read Cline usage limits: HTTP " + statusCode + " " + statusText,
            outcome.Error!.Message);
    }

    [Fact]
    public async Task UsageLimitsFailureReturnsSafeError()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call switch
            {
                0 => Ok("""{"data":{"id":"user_sensitive_123"}}"""),
                1 => Ok(SmallPlansBody),
                2 => Ok(BalanceBody),
                3 => Status(
                    503,
                    """{"api_key":"sk-sensitive-test-key","email":"person@example.test","user_id":"user_sensitive_123"}"""),
                4 => Ok("""{"data":{"items":[{"createdAt":"2026-07-07T11:00:00Z","costUsd":50,"creditsUsed":7}]}}"""),
                _ => throw new InvalidOperationException("unexpected Cline request " + call),
            },
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(
                Channel(
                    credentials: """{"apiKey":"sk-sensitive-test-key"}""",
                    supportedModels: """["cline-pass/test"]"""),
                CancellationToken.None));

        Assert.Equal(4, executor.Requests.Count);
        Assert.Equal("failed to read Cline usage limits: HTTP 503 Service Unavailable", error.Message);
        AssertOmitsSensitiveValues(error.Message);
    }

    [Fact]
    public async Task PartialUsageLimitsFallbackIsPerFieldAndPerWindow()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call switch
            {
                0 => Ok("""{"data":{"id":"user_test"}}"""),
                1 => Ok(SmallPlansBody),
                2 => Ok("""{"data":{"balance":1000}}"""),
                3 => Ok("""{"data":{"limits":[{"type":"five_hour","percentUsed":80},{"type":"weekly","resetsAt":"2026-07-17T02:56:47Z"},{"type":"unrecognized","percentUsed":99}]}}"""),
                4 => Ok("""{"data":{"items":[{"createdAt":"2026-07-14T11:00:00Z","costUsd":50,"creditsUsed":3,"aiModelTypeName":"cline-pass"}]}}"""),
                _ => throw new InvalidOperationException("unexpected Cline request " + call),
            },
        };
        var checker = CreateChecker(executor, now: new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero));

        var quota = await checker.CheckQuotaAsync(
            Channel(supportedModels: """["cline-pass/test"]"""),
            CancellationToken.None);

        var fiveHour = Window(quota, "last5h");
        Assert.Equal(0.80, Assert.IsType<double>(fiveHour["usage_ratio"]), 9);
        Assert.Equal("official_usage_limits", Assert.IsType<string>(fiveHour["usage_source"]));
        Assert.Equal("unavailable", Assert.IsType<string>(fiveHour["reset_source"]));
        Assert.Equal("unavailable", Assert.IsType<string>(fiveHour["cost_source"]));
        Assert.False(fiveHour.ContainsKey("next_reset_at"));
        Assert.False(fiveHour.ContainsKey("used_cost_units"));

        var weekly = Window(quota, "last7d");
        Assert.Equal(0.25, Assert.IsType<double>(weekly["usage_ratio"]), 9);
        Assert.Equal("cline_pass_ledger_official_window", Assert.IsType<string>(weekly["usage_source"]));
        Assert.Equal("official_usage_limits", Assert.IsType<string>(weekly["reset_source"]));
        Assert.Equal("cline_pass_ledger_official_window", Assert.IsType<string>(weekly["cost_source"]));
        Assert.Equal("2026-07-17T02:56:47Z", Assert.IsType<string>(weekly["next_reset_at"]));

        var monthly = Window(quota, "last30d");
        Assert.False(monthly.ContainsKey("usage_ratio"));
        Assert.Equal("unavailable", Assert.IsType<string>(monthly["usage_source"]));
        Assert.Equal("unavailable", Assert.IsType<string>(monthly["reset_source"]));
        Assert.Equal("unavailable", Assert.IsType<string>(monthly["cost_source"]));

        var fetch = UsageLimitsFetch(quota);
        Assert.Equal("partial", Assert.IsType<string>(fetch["status"]));
        Assert.Equal(3, Assert.IsType<int>(fetch["entries_seen"]));
        Assert.Equal(2, Assert.IsType<int>(fetch["recognized_entries"]));
        Assert.Equal(2, Assert.IsType<int>(fetch["usable_windows"]));
        Assert.Equal(2, Assert.IsType<int>(fetch["usable_fields"]));
    }

    [Fact]
    public async Task MalformedUsageLimitFieldsPreserveOtherOfficialValues()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call switch
            {
                0 => Ok("""{"data":{"id":"user_test"}}"""),
                1 => Ok(SmallPlansBody),
                2 => Ok("""{"data":{"balance":1000}}"""),
                3 => Ok("""{"data":{"limits":[{"type":"five_hour","percentUsed":"unknown","resetsAt":"2026-07-14T15:00:00Z"},{"type":"weekly","percentUsed":90,"resetsAt":123},{"type":"monthly","percentUsed":40,"resetsAt":"2026-08-01T11:13:17Z"},false]}}"""),
                4 => Ok("""{"data":{"items":[{"createdAt":"2026-07-14T11:00:00Z","costUsd":50,"creditsUsed":3,"aiModelTypeName":"cline-pass"}]}}"""),
                _ => throw new InvalidOperationException("unexpected Cline request " + call),
            },
        };
        var checker = CreateChecker(executor, now: new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero));

        var quota = await checker.CheckQuotaAsync(
            Channel(supportedModels: """["cline-pass/test"]"""),
            CancellationToken.None);

        var fiveHour = Window(quota, "last5h");
        Assert.Equal(0.5, Assert.IsType<double>(fiveHour["usage_ratio"]), 9);
        Assert.Equal("cline_pass_ledger_official_window", Assert.IsType<string>(fiveHour["usage_source"]));
        Assert.Equal("official_usage_limits", Assert.IsType<string>(fiveHour["reset_source"]));
        Assert.Equal("cline_pass_ledger_official_window", Assert.IsType<string>(fiveHour["cost_source"]));
        Assert.Equal("2026-07-14T15:00:00Z", Assert.IsType<string>(fiveHour["next_reset_at"]));

        var weekly = Window(quota, "last7d");
        Assert.Equal(0.9, Assert.IsType<double>(weekly["usage_ratio"]), 9);
        Assert.Equal("official_usage_limits", Assert.IsType<string>(weekly["usage_source"]));
        Assert.Equal("unavailable", Assert.IsType<string>(weekly["reset_source"]));
        Assert.Equal("unavailable", Assert.IsType<string>(weekly["cost_source"]));
        Assert.False(weekly.ContainsKey("next_reset_at"));

        var monthly = Window(quota, "last30d");
        Assert.Equal(0.4, Assert.IsType<double>(monthly["usage_ratio"]), 9);
        Assert.Equal("official_usage_limits", Assert.IsType<string>(monthly["usage_source"]));
        Assert.Equal("official_usage_limits", Assert.IsType<string>(monthly["reset_source"]));
        Assert.Equal("cline_pass_ledger_official_window", Assert.IsType<string>(monthly["cost_source"]));
        Assert.Equal("2026-08-01T11:13:17Z", Assert.IsType<string>(monthly["next_reset_at"]));

        var fetch = UsageLimitsFetch(quota);
        Assert.Equal("partial", Assert.IsType<string>(fetch["status"]));
        Assert.Equal(4, Assert.IsType<int>(fetch["entries_seen"]));
        Assert.Equal(3, Assert.IsType<int>(fetch["recognized_entries"]));
        Assert.Equal(3, Assert.IsType<int>(fetch["usable_windows"]));
        Assert.Equal(4, Assert.IsType<int>(fetch["usable_fields"]));
    }

    [Fact]
    public async Task DirectOnlySkipsPassUsageLimitsAndHistory()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call switch
            {
                0 => Ok(MeBody),
                1 => Ok("""{"data":[{"type":"individual","interval":"Monthly","isActive":true,"entitlements":{}}]}"""),
                2 => Ok(BalanceBody),
                _ => throw new InvalidOperationException("unexpected Cline request " + call),
            },
        };
        var checker = CreateChecker(executor);

        var quota = await checker.CheckQuotaAsync(
            Channel(supportedModels: """["anthropic/claude-sonnet-5"]"""),
            CancellationToken.None);

        Assert.Equal(3, executor.Requests.Count);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal("direct_only", quota.RawData["model_scope"]);
        Assert.False(quota.RawData.ContainsKey("usage_limits_fetch"));
        Assert.False(quota.RawData.ContainsKey("windows"));
        Assert.Empty(quota.Limits);
    }

    [Fact]
    public async Task DirectOnlyWithoutABalanceFieldOmitsTheRawBalance()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call switch
            {
                0 => Ok(MeBody),
                1 => Ok("""{"data":[]}"""),
                2 => Ok("""{"data":{}}"""),
                _ => throw new InvalidOperationException("unexpected Cline request " + call),
            },
        };
        var checker = CreateChecker(executor);

        var quota = await checker.CheckQuotaAsync(
            Channel(supportedModels: """["anthropic/claude-sonnet-5"]"""),
            CancellationToken.None);

        var balance = Assert.IsType<Dictionary<string, object?>>(quota.RawData["balance"]);
        Assert.False(balance.ContainsKey("raw_balance"));
        Assert.Equal(
            "Cline API response field name is balance; AxonHub displays it using Cline's Cline credits terminology.",
            Assert.IsType<string>(balance["unit_note"]));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"apiKey":"   "}""")]
    [InlineData("""{"apiKeys":["","  "]}""")]
    public async Task MissingCredentialsFailsBeforeAnyRequest(string credentials)
    {
        var executor = new ScriptedExecutor();
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(credentials: credentials), CancellationToken.None));

        Assert.Equal("channel has no API key", error.Message);
        Assert.Empty(executor.Requests);
    }

    [Theory]
    [InlineData("""{"apiKeys":[""," fallback-key "]}""", "fallback-key")]
    [InlineData("""{"apiKey":" legacy-key ","apiKeys":["fallback-key"]}""", "legacy-key")]
    [InlineData("""{"apiKey":"   ","apiKeys":[" fallback-key "]}""", "fallback-key")]
    [InlineData("""{"apiKey":"key-1","apiKeys":["key-2"]}""", "key-1")]
    public void ClineApiKeyPrefersTheLegacyKeyAndSkipsBlankEntries(string credentials, string expected)
    {
        Assert.Equal(
            expected,
            ClineQuotaChecker.ClineApiKey(new ChannelRecord { Credentials = credentials }));
    }

    [Fact]
    public async Task CheckQuotaFailsWithoutAnActiveClinePassThreshold()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call switch
            {
                0 => Ok(MeBody),
                1 => Ok("""{"data":[]}"""),
                2 => Ok(BalanceBody),
                _ => throw new InvalidOperationException("unexpected Cline request " + call),
            },
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "Cline plans response does not include an active ClinePass threshold",
            error.Message);
        Assert.Equal(3, executor.Requests.Count);
    }

    [Fact]
    public async Task CheckQuotaTreatsEmptyUsageLimitsAsUnusable()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call switch
            {
                0 => Ok(MeBody),
                1 => Ok(SmallPlansBody),
                2 => Ok(BalanceBody),
                3 => Ok("""{"data":{"limits":[]}}"""),
                _ => throw new InvalidOperationException("unexpected Cline request " + call),
            },
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "failed to read Cline usage limits: response contains no usable window data",
            error.Message);
        Assert.Equal(4, executor.Requests.Count);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"id":"   "}}""")]
    public async Task MissingIdentityIdFailsAfterTheFirstRequest(string meBody)
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(meBody),
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("Cline user identity response missing id", error.Message);
        Assert.Single(executor.Requests);
    }

    // ------------------------------------------------------------------
    // Ledger failures are non-fatal
    // ------------------------------------------------------------------

    [Fact]
    public async Task UsageTransportErrorIsNonFatalAndOmitsSensitiveValues()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call switch
            {
                0 => Ok("""{"data":{"id":"user_sensitive_123","organizations":[]}}"""),
                1 => Ok(SmallPlansBody),
                2 => Ok(BalanceBody),
                3 => Ok("""{"data":{"limits":[{"type":"five_hour","percentUsed":1,"resetsAt":"2026-07-07T16:00:00Z"},{"type":"weekly","percentUsed":1,"resetsAt":"2026-07-14T11:00:00Z"},{"type":"monthly","percentUsed":1,"resetsAt":"2026-08-06T11:00:00Z"}]}}"""),
                4 => Ok("""{"data":{"items":[{"createdAt":"2026-07-07T11:00:00Z","costUsd":1,"aiModelTypeName":"cline-pass"}],"nextToken":"cursor_sensitive_456"}}"""),
                5 => throw new IOException(
                    "dial failed with api key sk-sensitive-test-key for person@example.test account acct_sensitive_456 generation gen_sensitive_789 payment_id pay_sensitive_000"),
                _ => throw new InvalidOperationException("unexpected Cline request " + call),
            },
        };
        var checker = CreateChecker(executor, now: new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero));

        var quota = await checker.CheckQuotaAsync(
            Channel(
                credentials: """{"apiKey":"sk-sensitive-test-key"}""",
                supportedModels: """["cline-pass/deepseek-v4-flash"]"""),
            CancellationToken.None);

        // A ledger read error must not fail the quota check: the official
        // usage-limits response already drives the reported windows.
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);

        var usageFetch = UsageFetch(quota);
        Assert.True(Assert.IsType<bool>(usageFetch["ledger_unavailable"]));
        Assert.Equal("transport", Assert.IsType<string>(usageFetch["ledger_error_code"]));
        Assert.Equal(1, Assert.IsType<int>(usageFetch["pages"]));
        Assert.Equal(1, Assert.IsType<int>(usageFetch["items_seen"]));

        AssertOmitsSensitiveValues(GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task UsageLedgerRateLimitedKeepsOfficialWindows()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call switch
            {
                0 => Ok(MeBody),
                1 => Ok(PlansBody),
                2 => Ok(BalanceBody),
                3 => Ok(UsageLimitsBody),
                4 => Status(429, """{"error":"rate limited"}"""),
                _ => throw new InvalidOperationException("unexpected Cline request " + call),
            },
        };
        var checker = CreateChecker(executor);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(3, quota.Limits.Count);
        Assert.Equal(
            "https://api.cline.bot/api/v1/users/user_test/usages?limit=200",
            executor.Requests[4].Url);

        var usageFetch = UsageFetch(quota);
        Assert.True(Assert.IsType<bool>(usageFetch["ledger_unavailable"]));
        Assert.Equal("rate_limited", Assert.IsType<string>(usageFetch["ledger_error_code"]));

        var last7d = Window(quota, "last7d");
        Assert.Equal(0.79, Assert.IsType<double>(last7d["usage_ratio"]), 9);
        Assert.Equal(79.0, Assert.IsType<double>(last7d["usage_percent"]), 4);
        Assert.Equal("official_usage_limits", Assert.IsType<string>(last7d["usage_source"]));
        Assert.Equal("unavailable", Assert.IsType<string>(last7d["cost_source"]));
        Assert.False(last7d.ContainsKey("used_cost_units"));
        Assert.False(last7d.ContainsKey("cost_usage_ratio"));
    }

    // ------------------------------------------------------------------
    // getJSON error taxonomy
    // ------------------------------------------------------------------

    [Fact]
    public void GetJsonUpstreamHttpErrorOmitsSensitiveBody()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Status(
                403,
                """
                {"email":"person@example.test","api_key":"sk-sensitive-test-key","user_id":"user_sensitive_123","account_id":"acct_sensitive_456","generation_id":"gen_sensitive_789"}
                """),
        };
        var checker = DummyChecker();

        var error = Assert.Throws<ClineHttpStatusException>(
            () => checker.GetJson(
                executor,
                "https://api.cline.bot",
                "/api/v1/users/me",
                null,
                ApiKey,
                CancellationToken.None));

        Assert.Equal(403, error.StatusCode);
        Assert.Equal("HTTP 403 Forbidden", error.Message);
        AssertOmitsSensitiveValues(error.Message);
    }

    [Fact]
    public void GetJsonWrapsTransportFailuresWithoutLeakingTheCause()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new InvalidOperationException(
                "dial to https://api.cline.bot/api/v1/users/user_sensitive_123/usages failed with api key sk-sensitive-test-key for person@example.test account acct_sensitive_456 generation gen_sensitive_789 payment_id pay_sensitive_000"),
        };
        var checker = DummyChecker();

        var error = Assert.Throws<InvalidOperationException>(
            () => checker.GetJson(
                executor,
                "https://api.cline.bot",
                "/api/v1/users/user_sensitive_123/usages",
                null,
                ApiKey,
                CancellationToken.None));

        Assert.Equal("Cline quota request failed during transport", error.Message);
        AssertOmitsSensitiveValues(error.Message);
    }

    [Fact]
    public void GetJsonWrapsReadFailuresWithoutLeakingTheCause()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new IOException(
                "read failed for person@example.test sk-sensitive-test-key user_sensitive_123 acct_sensitive_456 gen_sensitive_789 payment_id pay_sensitive_000"),
        };
        var checker = DummyChecker();

        var error = Assert.Throws<InvalidOperationException>(
            () => checker.GetJson(
                executor,
                "https://api.cline.bot",
                "/api/v1/users/user_sensitive_123/usages",
                new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["cursor"] = ["cursor_sensitive_456"],
                },
                ApiKey,
                CancellationToken.None));

        Assert.Equal("failed to read Cline quota response", error.Message);
        AssertOmitsSensitiveValues(error.Message);
    }

    [Fact]
    public void GetJsonWrapsDecodeFailuresWithoutLeakingTheBody()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"data": {"id": "user_sensitive_123", "email": "person@example.test", "api_key": "sk-sensitive-test-key", "account_id": "acct_sensitive_456", "generation_id": "gen_sensitive_789", "payment_id": "pay_sensitive_000"""),
        };
        var checker = DummyChecker();

        var bytes = checker.GetJson(
            executor,
            "https://api.cline.bot",
            "/api/v1/users/user_sensitive_123/usages",
            null,
            ApiKey,
            CancellationToken.None);

        var error = Assert.Throws<InvalidOperationException>(
            () => ClineQuotaChecker.ParseBody(() => ClineQuotaChecker.ParseMeEnvelope(bytes)));

        Assert.Equal("failed to parse Cline quota response", error.Message);
        AssertOmitsSensitiveValues(error.Message);
    }

    [Theory]
    [InlineData(301, "Moved Permanently")]
    [InlineData(302, "Found")]
    public void GetJsonRejectsNon2xxResponsesBelow400(int statusCode, string statusText)
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Status(statusCode, string.Empty),
        };
        var checker = DummyChecker();

        var error = Assert.Throws<ClineHttpStatusException>(
            () => checker.GetJson(
                executor,
                "https://api.cline.bot",
                "/api/v1/users/me",
                null,
                ApiKey,
                CancellationToken.None));

        Assert.Equal(statusCode, error.StatusCode);
        Assert.Equal("HTTP " + statusCode + " " + statusText, error.Message);
    }

    [Fact]
    public async Task GetJsonCapsTheBodyAtOneMiB()
    {
        var body = """{"data":{"id":""" + '"' + new string('a', 1 << 20) + '"' + "}}";
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(body),
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "failed to read Cline user identity: failed to parse Cline quota response",
            error.Message);
        Assert.Single(executor.Requests);
    }

    // ------------------------------------------------------------------
    // Usage-limit decoding
    // ------------------------------------------------------------------

    [Fact]
    public void ParseClineUsageLimitsMapsFieldsIndependentlyAndKeepsFirstValidValue()
    {
        var (limits, meta) = ClineQuotaChecker.ParseClineUsageLimits(
        [
            new ClineUsageLimit { Type = "unknown", PercentUsed = 25.0, ResetsAt = "2026-07-14T13:00:00Z" },
            new ClineUsageLimit { Type = "five_hour", PercentUsed = 25.0 },
            new ClineUsageLimit { Type = "five_hour", PercentUsed = 90.0, ResetsAt = "2026-07-14T15:00:00Z" },
            new ClineUsageLimit { Type = "weekly", PercentUsed = -5.0, ResetsAt = "not-a-time" },
            new ClineUsageLimit { Type = "monthly", PercentUsed = 150.0, ResetsAt = "2026-08-01T11:13:17Z" },
        ]);

        Assert.Equal("partial", meta.Status);
        Assert.Equal(5, meta.EntriesSeen);
        Assert.Equal(4, meta.RecognizedEntries);
        Assert.Equal(3, meta.UsableWindows);
        Assert.Equal(5, meta.UsableFields);

        var fiveHour = limits["last5h"];
        Assert.Equal(0.25, Assert.IsType<double>(fiveHour.UsageRatio), 6);
        Assert.Equal(
            new DateTimeOffset(2026, 7, 14, 15, 0, 0, TimeSpan.Zero),
            fiveHour.NextResetAt);

        var weekly = limits["last7d"];
        Assert.Equal(0.0, Assert.IsType<double>(weekly.UsageRatio), 6);
        Assert.Null(weekly.NextResetAt);
        Assert.Equal("invalid", weekly.ResetState);

        var monthly = limits["last30d"];
        Assert.Equal(1.0, Assert.IsType<double>(monthly.UsageRatio), 6);
        Assert.NotNull(monthly.NextResetAt);
        Assert.Equal("active", monthly.ResetState);
    }

    [Theory]
    [InlineData("""{"type":"five_hour","percentUsed":0}""", "unavailable")]
    [InlineData("""{"type":"five_hour","percentUsed":0,"resetsAt":null}""", "inactive")]
    [InlineData("""{"type":"five_hour","percentUsed":0,"resetsAt":""}""", "inactive")]
    [InlineData("""{"type":"five_hour","percentUsed":0,"resetsAt":"2026-08-02T02:45:10Z"}""", "active")]
    [InlineData("""{"type":"five_hour","percentUsed":0,"resetsAt":123}""", "invalid")]
    public void ParseUsageLimitElementTracksResetFieldState(string raw, string expectedState)
    {
        using var document = JsonDocument.Parse(raw);

        var limit = ClineQuotaChecker.ParseUsageLimitElement(document.RootElement);

        Assert.Equal(expectedState, limit.ResetFieldState);
    }

    [Fact]
    public void ParseUsageLimitElementSwallowsFieldTypeErrorsAndKeepsLastDuplicate()
    {
        using (var document = JsonDocument.Parse(
            """{"type":"weekly","percentUsed":"unknown","resetsAt":123}"""))
        {
            var limit = ClineQuotaChecker.ParseUsageLimitElement(document.RootElement);
            Assert.Equal("weekly", limit.Type);
            Assert.Null(limit.PercentUsed);
            Assert.Equal("invalid", limit.ResetFieldState);
        }

        using (var document = JsonDocument.Parse(
            """{"type":"five_hour","type":"weekly","percentUsed":50,"percentUsed":90}"""))
        {
            var limit = ClineQuotaChecker.ParseUsageLimitElement(document.RootElement);
            Assert.Equal("weekly", limit.Type);
            Assert.Equal(90.0, Assert.IsType<double>(limit.PercentUsed), 9);
        }

        using (var document = JsonDocument.Parse(
            """{"Type":"weekly","PercentUsed":50,"ResetsAt":"2026-08-02T02:45:10Z"}"""))
        {
            // The custom unmarshaller matches exact keys only.
            var limit = ClineQuotaChecker.ParseUsageLimitElement(document.RootElement);
            Assert.Equal(string.Empty, limit.Type);
            Assert.Null(limit.PercentUsed);
            Assert.Equal("unavailable", limit.ResetFieldState);
        }

        using (var document = JsonDocument.Parse("false"))
        {
            var limit = ClineQuotaChecker.ParseUsageLimitElement(document.RootElement);
            Assert.Equal(string.Empty, limit.Type);
            Assert.Null(limit.PercentUsed);
            Assert.Equal(string.Empty, limit.ResetFieldState);
        }
    }

    [Fact]
    public void ParseClineUsageLimitsEmptyResetOnlyMeansInactiveAtZeroUsage()
    {
        var (limits, meta) = ClineQuotaChecker.ParseClineUsageLimits(
        [
            new ClineUsageLimit
            {
                Type = "five_hour",
                PercentUsed = 0.0,
                ResetFieldState = "inactive",
            },
            new ClineUsageLimit
            {
                Type = "weekly",
                PercentUsed = 25.0,
                ResetFieldState = "inactive",
            },
            new ClineUsageLimit
            {
                Type = "monthly",
                PercentUsed = 0.0,
                ResetFieldState = "unavailable",
            },
        ]);

        Assert.Equal("partial", meta.Status);
        Assert.Equal("inactive", limits["last5h"].ResetState);
        Assert.Equal("unavailable", limits["last7d"].ResetState);
        Assert.Equal("unavailable", limits["last30d"].ResetState);
    }

    [Fact]
    public void ParseMeEnvelopeMergesDuplicateDataObjectsAndFoldsKeys()
    {
        Assert.Equal(
            "two",
            ClineQuotaChecker.ParseMeEnvelope(Bytes("""{"data":{"id":"one"},"data":{"id":"two"}}""")).Id);
        Assert.Equal(
            "two",
            ClineQuotaChecker.ParseMeEnvelope(Bytes("""{"data":{"id":"one","id":"two"}}""")).Id);
        Assert.Equal(
            "x",
            ClineQuotaChecker.ParseMeEnvelope(Bytes("""{"DATA":{"ID":"x"}}""")).Id);
        Assert.Equal(
            "x",
            ClineQuotaChecker.ParseMeEnvelope(Bytes("""{"data":{"id":"x"},"data":null}""")).Id);
        Assert.Equal(
            string.Empty,
            ClineQuotaChecker.ParseMeEnvelope(Bytes("null")).Id);
        Assert.Equal(
            string.Empty,
            ClineQuotaChecker.ParseMeEnvelope(Bytes("{}")).Id);
    }

    [Fact]
    public void ParseMeEnvelopeRejectsOrganizationsThatAreNotAnArray()
    {
        var error = Assert.Throws<JsonException>(
            () => ClineQuotaChecker.ParseMeEnvelope(
                Bytes("""{"data":{"id":"x","organizations":{}}}""")));

        Assert.Contains("organizations", error.Message);
    }

    [Fact]
    public void ParseBalanceEnvelopeTracksTheBalancePointer()
    {
        Assert.Equal(
            5L,
            ClineQuotaChecker.ParseBalanceEnvelope(
                Bytes("""{"data":{"balance":5},"data":null}""")).Balance);
        Assert.Null(
            ClineQuotaChecker.ParseBalanceEnvelope(
                Bytes("""{"data":{"balance":5},"data":{"balance":null}}""")).Balance);
        Assert.Null(
            ClineQuotaChecker.ParseBalanceEnvelope(
                Bytes("""{"data":{}}""")).Balance);
    }

    [Fact]
    public void ParsePlansEnvelopeKeepsNilAndEmptyDataDistinct()
    {
        Assert.Null(ClineQuotaChecker.ParsePlansEnvelope(Bytes("{}")));
        Assert.Null(ClineQuotaChecker.ParsePlansEnvelope(Bytes("""{"data":null}""")));
        Assert.Empty(ClineQuotaChecker.ParsePlansEnvelope(Bytes("""{"data":[]}"""))!);
        Assert.Single(ClineQuotaChecker.ParsePlansEnvelope(Bytes("""{"data":[null]}"""))!);
    }

    // ------------------------------------------------------------------
    // Window builders
    // ------------------------------------------------------------------

    [Fact]
    public void BuildClineQuotaDataOfficialValuesDriveStatusAndResetWhileCostRemainsExact()
    {
        var now = new DateTimeOffset(2099, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var quota = ClineQuotaChecker.BuildClineQuotaData(
            now,
            "cline_pass_only",
            new ClineInferenceCapThreshold
            {
                Last5HoursUsageCostUsdPerUser = 100,
                Last7DaysUsageCostUsdPerUser = 100,
                Last30DaysUsageCostUsdPerUser = 100,
            },
            null,
            null,
            [
                new ClineUsageItem
                {
                    CreatedAt = "2099-07-14T11:00:00Z",
                    CostUsd = 50,
                    CreditsUsed = 7,
                    AiModelTypeName = "cline-pass",
                },
            ],
            new ClineUsageFetchMeta { Pages = 1, ItemsSeen = 1 },
            new Dictionary<string, ClineOfficialWindowLimit>(StringComparer.Ordinal)
            {
                ["last5h"] = new()
                {
                    UsageRatio = 0.20,
                    NextResetAt = new DateTimeOffset(2099, 7, 14, 14, 0, 0, TimeSpan.Zero),
                },
                ["last7d"] = new()
                {
                    UsageRatio = 0.90,
                    NextResetAt = new DateTimeOffset(2099, 7, 17, 2, 56, 47, TimeSpan.Zero),
                },
                ["last30d"] = new()
                {
                    UsageRatio = 0.40,
                    NextResetAt = new DateTimeOffset(2099, 8, 1, 11, 13, 17, TimeSpan.Zero),
                },
            },
            new ClineUsageLimitsFetchMeta
            {
                Status = "complete",
                EntriesSeen = 3,
                RecognizedEntries = 3,
                UsableWindows = 3,
                UsableFields = 6,
            });

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(new DateTimeOffset(2099, 7, 14, 14, 0, 0, TimeSpan.Zero), quota.NextResetAt);
        Assert.Equal(3, quota.Limits.Count);
        Assert.Equal(0.90, Assert.IsType<double>(quota.Limits[1].UsageRatio), 6);
        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Limits[1].Status);

        var weekly = Window(quota, "last7d");
        Assert.Equal(50L, Assert.IsType<long>(weekly["used_cost_units"]));
        Assert.Equal(100L, Assert.IsType<long>(weekly["limit_cost_units"]));
        Assert.Equal(50L, Assert.IsType<long>(weekly["remaining_cost_units"]));
        Assert.Equal(7L, Assert.IsType<long>(weekly["credits_used"]));
        Assert.Equal(0.90, Assert.IsType<double>(weekly["usage_ratio"]), 6);
        Assert.Equal(90.0, Assert.IsType<double>(weekly["usage_percent"]), 6);
        Assert.Equal(0.50, Assert.IsType<double>(weekly["cost_usage_ratio"]), 6);
        Assert.Equal(50.0, Assert.IsType<double>(weekly["cost_usage_percent"]), 6);
        Assert.Equal("official_usage_limits", Assert.IsType<string>(weekly["usage_source"]));
        Assert.Equal("official_usage_limits", Assert.IsType<string>(weekly["reset_source"]));
        Assert.Equal("2099-07-17T02:56:47Z", Assert.IsType<string>(weekly["next_reset_at"]));
    }

    [Fact]
    public void BuildClineWindowUsesOfficialBucketBoundaryAndFiltersDirectUsage()
    {
        var now = new DateTimeOffset(2026, 8, 1, 16, 0, 0, TimeSpan.Zero);
        var resetAt = new DateTimeOffset(2026, 8, 31, 15, 12, 9, TimeSpan.Zero);
        var costStart = new DateTimeOffset(2026, 8, 1, 15, 12, 8, TimeSpan.Zero)
            .AddTicks(9_173_690);

        var window = ClineQuotaChecker.BuildClineWindow(
            now,
            "last30d",
            TimeSpan.FromDays(30),
            100,
            [
                new ClineUsageItem
                {
                    CreatedAt = "2026-08-01T15:12:08.917369Z",
                    CostUsd = 10,
                    AiModelTypeName = "cline-pass",
                },
                new ClineUsageItem
                {
                    CreatedAt = "2026-08-01T15:30:00Z",
                    CostUsd = 20,
                    AiModelTypeName = "cline-pass",
                },
                new ClineUsageItem
                {
                    CreatedAt = "2026-08-01T15:40:00Z",
                    CostUsd = 30,
                    AiModelTypeName = "openai",
                },
                new ClineUsageItem
                {
                    CreatedAt = "2026-07-31T15:30:00Z",
                    CostUsd = 40,
                    AiModelTypeName = "cline-pass",
                },
            ],
            false,
            new ClineOfficialWindowLimit { UsageRatio = 0.01, NextResetAt = resetAt });

        Assert.Equal("active", window.State);
        Assert.True(window.CostAvailable);
        Assert.Equal(30L, window.UsedUnits);
        Assert.Equal(2, window.ItemsCount);
        Assert.Equal(resetAt, window.NextResetAt);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 15, 12, 9, TimeSpan.Zero), window.WindowStartAt);
        Assert.Equal(costStart, window.CostStartAt);
        Assert.Equal("cline_pass_ledger_official_window", window.CostSource);
    }

    [Fact]
    public void BuildClineWindowZeroPercentWithResetRemainsActive()
    {
        var now = new DateTimeOffset(2026, 8, 1, 21, 46, 0, TimeSpan.Zero);
        var resetAt = new DateTimeOffset(2026, 8, 2, 2, 45, 10, TimeSpan.Zero);

        var window = ClineQuotaChecker.BuildClineWindow(
            now,
            "last5h",
            TimeSpan.FromHours(5),
            1_000_000_000,
            [
                new ClineUsageItem
                {
                    CreatedAt = "2026-08-01T21:45:10.443078Z",
                    CostUsd = 1232,
                    AiModelTypeName = "cline-pass",
                },
            ],
            false,
            new ClineOfficialWindowLimit { UsageRatio = 0.0, NextResetAt = resetAt });

        Assert.Equal("active", window.State);
        Assert.True(window.Active);
        Assert.True(window.CostAvailable);
        Assert.Equal(1232L, window.UsedUnits);
        Assert.Equal(resetAt, window.NextResetAt);
        Assert.Equal(0.0, Assert.IsType<double>(window.UsageRatio));
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(0, 7)]
    [InlineData(0, 30)]
    public void BuildClineWindowUsesTheOfficialBoundaryForEveryDuration(int hours, int days)
    {
        var now = new DateTimeOffset(2026, 8, 2, 1, 0, 0, TimeSpan.Zero);
        var duration = hours > 0 ? TimeSpan.FromHours(hours) : TimeSpan.FromDays(days);
        var resetAt = now + duration / 2;
        var expectedStart = resetAt - duration;

        var window = ClineQuotaChecker.BuildClineWindow(
            now,
            "window",
            duration,
            100,
            [
                new ClineUsageItem
                {
                    CreatedAt = GoTime.Rfc3339Nano(expectedStart.AddMinutes(-1)),
                    CostUsd = 90,
                    AiModelTypeName = "cline-pass",
                },
                new ClineUsageItem
                {
                    CreatedAt = GoTime.Rfc3339Nano(expectedStart.AddMinutes(1)),
                    CostUsd = 10,
                    AiModelTypeName = "cline-pass",
                },
            ],
            false,
            new ClineOfficialWindowLimit { UsageRatio = 0.1, NextResetAt = resetAt });

        Assert.True(window.CostAvailable);
        Assert.Equal(10L, window.UsedUnits);
        Assert.Equal(1, window.ItemsCount);
    }

    [Fact]
    public void BuildClineWindowUnclassifiedCurrentUsageHidesCost()
    {
        var now = new DateTimeOffset(2026, 8, 1, 16, 0, 0, TimeSpan.Zero);
        var resetAt = now.AddHours(4);

        var window = ClineQuotaChecker.BuildClineWindow(
            now,
            "last5h",
            TimeSpan.FromHours(5),
            100,
            [
                new ClineUsageItem
                {
                    CreatedAt = GoTime.Rfc3339Nano(now.AddHours(-1)),
                    CostUsd = 10,
                },
            ],
            false,
            new ClineOfficialWindowLimit { UsageRatio = 0.1, NextResetAt = resetAt });

        Assert.Equal("active", window.State);
        Assert.False(window.CostAvailable);
        Assert.Equal("unavailable", window.CostSource);
        Assert.Equal(0L, window.UsedUnits);
        Assert.Equal(0.1, Assert.IsType<double>(window.UsageRatio), 6);
        Assert.Equal("official_usage_limits", window.UsageSource);
    }

    [Fact]
    public void BuildClineWindowOfficialInactiveWindowIgnoresRecentHistory()
    {
        var now = new DateTimeOffset(2026, 8, 1, 20, 41, 30, TimeSpan.Zero);

        var window = ClineQuotaChecker.BuildClineWindow(
            now,
            "last5h",
            TimeSpan.FromHours(5),
            100,
            [
                new ClineUsageItem
                {
                    CreatedAt = "2026-08-01T16:31:16Z",
                    CostUsd = 80,
                    AiModelTypeName = "cline-pass",
                },
            ],
            false,
            new ClineOfficialWindowLimit
            {
                UsageRatio = 0.0,
                ResetState = "inactive",
            });

        Assert.Equal("inactive", window.State);
        Assert.False(window.Active);
        Assert.True(window.CostAvailable);
        Assert.Equal(0, window.ItemsCount);
        Assert.Equal(0L, window.UsedUnits);
        Assert.Null(window.NextResetAt);
        Assert.Equal("official_no_active_window", window.CostSource);
        Assert.Equal(0.0, Assert.IsType<double>(window.CostUsageRatio));
    }

    [Fact]
    public void BuildClineWindowStaleOfficialResetDoesNotFallBackToRollingEstimate()
    {
        var now = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var staleReset = now.AddHours(-2);

        var window = ClineQuotaChecker.BuildClineWindow(
            now,
            "last5h",
            TimeSpan.FromHours(5),
            100,
            [
                new ClineUsageItem
                {
                    CreatedAt = GoTime.Rfc3339Nano(now.AddHours(-1)),
                    CostUsd = 50,
                    AiModelTypeName = "cline-pass",
                },
            ],
            false,
            new ClineOfficialWindowLimit { UsageRatio = 0.5, NextResetAt = staleReset });

        Assert.Equal("invalid", window.State);
        Assert.False(window.CostAvailable);
        Assert.Equal(0L, window.UsedUnits);
        Assert.Null(window.NextResetAt);
        Assert.Equal("unavailable", window.CostSource);
        Assert.Equal("unavailable", window.ResetSource);
        Assert.Equal(0.5, Assert.IsType<double>(window.UsageRatio), 6);
    }

    [Fact]
    public void BuildClineWindowTruncatedUsageHistoryKeepsOfficialStatusButHidesCost()
    {
        var now = new DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
        var resetAt = now.AddHours(4);

        var window = ClineQuotaChecker.BuildClineWindow(
            now,
            "last5h",
            TimeSpan.FromHours(5),
            100,
            [
                new ClineUsageItem
                {
                    CreatedAt = GoTime.Rfc3339Nano(now.AddHours(-1)),
                    CostUsd = 25,
                    AiModelTypeName = "cline-pass",
                },
            ],
            true,
            new ClineOfficialWindowLimit { UsageRatio = 0.25, NextResetAt = resetAt });

        Assert.Equal("active", window.State);
        Assert.False(window.CostAvailable);
        Assert.Equal("unavailable", window.CostSource);
        Assert.Equal(0.25, Assert.IsType<double>(window.UsageRatio), 6);
        Assert.Equal(resetAt, window.NextResetAt);
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData(0.0, "available")]
    [InlineData(0.79, "available")]
    [InlineData(0.8, "warning")]
    [InlineData(0.99, "warning")]
    [InlineData(1.0, "exhausted")]
    public void ClineWindowStatusRanksTheOfficialRatio(double? ratio, string expected)
    {
        var window = new ClineWindow { Key = "last5h", UsageRatio = ratio };

        Assert.Equal(expected, ClineQuotaChecker.ClineWindowStatus(window));
    }

    [Fact]
    public void WorseQuotaStatusRanksUnknownBelowAvailable()
    {
        Assert.Equal("available", ClineQuotaChecker.WorseQuotaStatus("unknown", "available"));
        Assert.Equal("available", ClineQuotaChecker.WorseQuotaStatus("available", "unknown"));
        Assert.Equal("exhausted", ClineQuotaChecker.WorseQuotaStatus("warning", "exhausted"));
        Assert.Equal("exhausted", ClineQuotaChecker.WorseQuotaStatus("exhausted", "warning"));
    }

    [Fact]
    public void BuildClineQuotaDataWarningAtEightyPercent()
    {
        var quota = ClineQuotaChecker.BuildClineQuotaData(
            new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero),
            "cline_pass_only",
            new ClineInferenceCapThreshold
            {
                Last5HoursUsageCostUsdPerUser = 100,
                Last7DaysUsageCostUsdPerUser = 1000,
                Last30DaysUsageCostUsdPerUser = 2000,
            },
            null,
            null,
            null,
            new ClineUsageFetchMeta(),
            new Dictionary<string, ClineOfficialWindowLimit>(StringComparer.Ordinal)
            {
                ["last5h"] = new() { UsageRatio = 0.8, ResetState = "inactive" },
            },
            new ClineUsageLimitsFetchMeta { Status = "partial" });

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal("cline_pass_windows", quota.RawData["status_basis"]);
    }

    [Fact]
    public void BuildClineQuotaDataExhaustedWhenPassOnly()
    {
        var quota = ClineQuotaChecker.BuildClineQuotaData(
            new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero),
            "cline_pass_only",
            new ClineInferenceCapThreshold
            {
                Last5HoursUsageCostUsdPerUser = 100,
                Last7DaysUsageCostUsdPerUser = 1000,
                Last30DaysUsageCostUsdPerUser = 2000,
            },
            null,
            null,
            null,
            new ClineUsageFetchMeta(),
            new Dictionary<string, ClineOfficialWindowLimit>(StringComparer.Ordinal)
            {
                ["last5h"] = new() { UsageRatio = 1.0, ResetState = "inactive" },
            },
            new ClineUsageLimitsFetchMeta { Status = "partial" });

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
    }

    [Fact]
    public void BuildClineQuotaDataMixedScopeDoesNotExhaustTheWholeChannelFromThePassPool()
    {
        var now = new DateTimeOffset(2099, 7, 7, 12, 0, 0, TimeSpan.Zero);
        var fiveHourReset = now.AddHours(4);

        var quota = ClineQuotaChecker.BuildClineQuotaData(
            now,
            "mixed",
            new ClineInferenceCapThreshold
            {
                Last5HoursUsageCostUsdPerUser = 100,
                Last7DaysUsageCostUsdPerUser = 1000,
                Last30DaysUsageCostUsdPerUser = 2000,
            },
            null,
            null,
            [
                new ClineUsageItem
                {
                    CreatedAt = "2099-07-07T11:00:00Z",
                    CostUsd = 1,
                    AiModelTypeName = "cline-pass",
                },
            ],
            new ClineUsageFetchMeta { Pages = 1, ItemsSeen = 1 },
            new Dictionary<string, ClineOfficialWindowLimit>(StringComparer.Ordinal)
            {
                ["last5h"] = new() { UsageRatio = 1.0, NextResetAt = fiveHourReset },
                ["last7d"] = new() { UsageRatio = 0.0, ResetState = "inactive" },
                ["last30d"] = new() { UsageRatio = 0.0, ResetState = "inactive" },
            },
            new ClineUsageLimitsFetchMeta
            {
                Status = "complete",
                EntriesSeen = 3,
                RecognizedEntries = 3,
                UsableWindows = 3,
                UsableFields = 6,
            });

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal("mixed_pool_pass_exhausted", quota.RawData["status_basis"]);
        Assert.NotEmpty(quota.Limits);
        foreach (var limit in quota.Limits)
        {
            if (limit.Window == QuotaWindows.FiveHours)
            {
                Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);
                Assert.False(limit.Ready);
                continue;
            }

            Assert.NotEqual(ProviderQuotaStatuses.Exhausted, limit.Status);
            Assert.True(limit.Ready);
        }

        var fiveHour = Window(quota, "last5h");
        Assert.Equal(1.0, Assert.IsType<double>(fiveHour["usage_ratio"]), 6);
        Assert.Equal(0.01, Assert.IsType<double>(fiveHour["cost_usage_ratio"]), 6);
    }

    [Fact]
    public void BuildClineDirectOnlyQuotaUsesTheBalanceInformationally()
    {
        var quota = ClineQuotaChecker.BuildClineDirectOnlyQuota(
            497582,
            [new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "individual",
                ["interval"] = "Monthly",
            }]);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal("direct_only", quota.RawData["model_scope"]);
        Assert.Equal("direct_credit_balance_informational", quota.RawData["status_basis"]);
        Assert.Empty(quota.Limits);
        Assert.Equal(
            """{"balance":{"raw_balance":497582,"unit_note":"Cline API response field name is balance; AxonHub displays it using Cline's Cline credits terminology."},"model_scope":"direct_only","plans":[{"interval":"Monthly","type":"individual"}],"pool":"direct_credit","pool_note":"Cline (usage-billing) credits balance is informational until exact pay-as-you-go exhaustion semantics are verified.","status_basis":"direct_credit_balance_informational"}""",
            GoJson.MarshalToString(quota.RawData));
    }

    // ------------------------------------------------------------------
    // URL, classification and parsing helpers
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("", "/api/v1/users/me", "https://api.cline.bot/api/v1/users/me")]
    [InlineData("https://api.cline.bot/v1", "/api/v1/plans", "https://api.cline.bot/api/v1/plans")]
    [InlineData("http://api.cline.bot/v1", "/api/v1/plans", "https://api.cline.bot/api/v1/plans")]
    [InlineData("://invalid", "/api/v1/plans", "https://api.cline.bot/api/v1/plans")]
    [InlineData("   ", "/api/v1/users/me", "https://api.cline.bot/api/v1/users/me")]
    [InlineData(
        "https://api.cline.bot/api?x=1",
        "/api/v1/users/a%3Fb/usages",
        "https://api.cline.bot/api/v1/users/a%253Fb/usages")]
    public void BuildClineQuotaUrlMatchesTheReferenceBaseHandling(
        string baseUrl,
        string path,
        string expected)
    {
        Assert.Equal(expected, ClineQuotaChecker.BuildClineQuotaUrl(baseUrl, path, null));
    }

    [Fact]
    public void BuildClineQuotaUrlEncodesTheQueryWithSortedKeys()
    {
        var url = ClineQuotaChecker.BuildClineQuotaUrl(
            "https://api.cline.bot",
            "/api/v1/users/user_test/usages",
            new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["limit"] = ["200"],
                ["cursor"] = ["cursor_2"],
            });

        Assert.Equal(
            "https://api.cline.bot/api/v1/users/user_test/usages?cursor=cursor_2&limit=200",
            url);
    }

    [Theory]
    [InlineData("""["cline-pass/a","cline-pass/b"]""", "cline_pass_only")]
    [InlineData("""["zai/glm-5.2"]""", "direct_only")]
    [InlineData("""["cline-pass/a","zai/glm-5.2"]""", "mixed")]
    [InlineData("null", "unknown")]
    [InlineData("[]", "unknown")]
    [InlineData("""["Cline-Pass/a"]""", "direct_only")]
    public void ClassifyClineModelScopeUsesThePassPrefix(string supportedModels, string expected)
    {
        Assert.Equal(
            expected,
            ClineQuotaChecker.ClassifyClineModelScope(
                new ChannelRecord { SupportedModels = supportedModels }));
    }

    [Fact]
    public void ClassifyClineModelScopeCombinesSupportedAndManualModels()
    {
        Assert.Equal(
            "mixed",
            ClineQuotaChecker.ClassifyClineModelScope(new ChannelRecord
            {
                SupportedModels = """["  ","cline-pass/a"]""",
                ManualModels = """["zai/glm-5.2"]""",
            }));

        Assert.Equal(
            "direct_only",
            ClineQuotaChecker.ClassifyClineModelScope(new ChannelRecord
            {
                SupportedModels = "null",
                ManualModels = """["zai/glm-5.2"]""",
            }));
    }

    [Fact]
    public void SelectClinePassThresholdSkipsInactiveAndDisabledPlans()
    {
        var plans = new List<ClinePlan>
        {
            Plan("inactive", 111, enabled: true, active: false),
            Plan("disabled", 222, enabled: false, active: true),
            new ClinePlan
            {
                Type = "no-pass",
                IsActive = true,
                Entitlements = new ClineEntitlements { ClinePass = null },
            },
            new ClinePlan
            {
                Type = "no-threshold",
                IsActive = true,
                Entitlements = new ClineEntitlements
                {
                    ClinePass = new ClinePassEntitlement { Enabled = true },
                },
            },
            Plan("first", 1, enabled: true, active: true),
            Plan("second", 9, enabled: true, active: true),
        };

        var (threshold, summaries, found) = ClineQuotaChecker.SelectClinePassThreshold(plans);

        Assert.True(found);
        Assert.Equal(1L, threshold.Last5HoursUsageCostUsdPerUser);
        Assert.Equal(1L, threshold.Last7DaysUsageCostUsdPerUser);
        Assert.Equal(1L, threshold.Last30DaysUsageCostUsdPerUser);
        Assert.NotNull(summaries);
        Assert.Equal(2, summaries!.Count);
        Assert.Equal("first", Assert.IsType<string>(summaries[0]["type"]));
        Assert.Equal("monthly", Assert.IsType<string>(summaries[0]["interval"]));
        Assert.Equal("second", Assert.IsType<string>(summaries[1]["type"]));

        var (_, none, noneFound) = ClineQuotaChecker.SelectClinePassThreshold(null);
        Assert.False(noneFound);
        Assert.Null(none);
    }

    [Theory]
    [InlineData("2026-08-01T15:12:08Z", true, 0L)]
    [InlineData(" 2026-08-01T15:12:08Z ", true, 0L)]
    [InlineData("2026-08-01T15:12:08.917369Z", true, 9_173_690L)]
    [InlineData("2026-08-01T15:12:08.917369123Z", true, 9_173_691L)]
    [InlineData("2026-08-01T15:12:08,5Z", true, 5_000_000L)]
    [InlineData("2026-08-01 15:12:08Z", false, 0L)]
    [InlineData("not-a-time", false, 0L)]
    [InlineData("", false, 0L)]
    public void ParseClineTimeUsesRfc3339NanoSemantics(string raw, bool parsed, long fractionTicks)
    {
        var value = ClineQuotaChecker.ParseClineTime(raw);

        if (!parsed)
        {
            Assert.Null(value);
            return;
        }

        Assert.NotNull(value);
        Assert.Equal(fractionTicks, value!.Value.Ticks % TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void ClineLedgerErrorCodeNeverEmbedsTheRawErrorText()
    {
        Assert.Equal(
            "rate_limited",
            ClineQuotaChecker.ClineLedgerErrorCode(new ClineHttpStatusException(429)));
        Assert.Equal(
            "http_500",
            ClineQuotaChecker.ClineLedgerErrorCode(new ClineHttpStatusException(500)));
        Assert.Equal(
            "transport",
            ClineQuotaChecker.ClineLedgerErrorCode(new IOException("sensitive transport text")));
    }

    // ------------------------------------------------------------------
    // Usage pagination
    // ------------------------------------------------------------------

    [Fact]
    public void FetchUsageItemsFollowsTheCursorToTheNextPage()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call switch
            {
                0 => Ok("""{"data":{"items":[{"createdAt":"2026-07-07T11:00:00Z","costUsd":1,"aiModelTypeName":"cline-pass"}],"nextToken":"cursor_2"}}"""),
                1 => Ok("""{"data":{"items":[{"createdAt":"2026-06-01T11:00:00Z","costUsd":2,"aiModelTypeName":"openai"}]}}"""),
                _ => throw new InvalidOperationException("unexpected page request " + call),
            },
        };
        var checker = CreateChecker(executor, now: new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero));

        var outcome = checker.FetchUsageItems(
            executor,
            "https://api.cline.bot/v1",
            "user_test",
            "key",
            CancellationToken.None);

        Assert.Null(outcome.Error);
        Assert.NotNull(outcome.Items);
        Assert.Equal(2, outcome.Items!.Count);
        Assert.Equal(2, outcome.Meta.Pages);
        Assert.Equal(2, outcome.Meta.ItemsSeen);
        Assert.Equal(1, outcome.Meta.ClinePassItemsSeen);
        Assert.Equal(1, outcome.Meta.DirectItemsSeen);
        Assert.Equal(0, outcome.Meta.UnclassifiedItemsSeen);
        Assert.False(outcome.Meta.Truncated);
        Assert.Equal(
            "https://api.cline.bot/api/v1/users/user_test/usages?limit=200",
            executor.Requests[0].Url);
        Assert.Equal(
            "https://api.cline.bot/api/v1/users/user_test/usages?cursor=cursor_2&limit=200",
            executor.Requests[1].Url);
    }

    [Fact]
    public void FetchUsageItemsStopsAtThePageCeiling()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"data":{"items":[{"createdAt":"2026-07-07T11:00:00Z","costUsd":1}],"nextToken":"still_more"}}"""),
        };
        var checker = CreateChecker(executor, now: new DateTimeOffset(2026, 7, 7, 12, 0, 0, TimeSpan.Zero));

        var outcome = checker.FetchUsageItems(
            executor,
            "https://api.cline.bot/v1",
            "user_test",
            "key",
            CancellationToken.None);

        Assert.Null(outcome.Error);
        Assert.NotNull(outcome.Items);
        Assert.Equal(ClineQuotaChecker.MaxUsagePages, outcome.Items!.Count);
        Assert.True(outcome.Meta.Truncated);
        Assert.Equal(ClineQuotaChecker.MaxUsagePages, outcome.Meta.Pages);
        Assert.Equal(ClineQuotaChecker.MaxUsagePages, outcome.Meta.ItemsSeen);
        Assert.Equal(ClineQuotaChecker.MaxUsagePages, executor.Requests.Count);
    }

    // ------------------------------------------------------------------
    // Proxy routing and channel support
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaUsesTheProxyExecutorFromTheChannelSettings()
    {
        var direct = new ScriptedExecutor();
        var proxied = HappyExecutor();
        var factory = new ScriptedProxyExecutorFactory { Executor = proxied };
        var checker = new ClineQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(
            Channel(settings: """
                {"proxy":{"type":"http","url":"http://127.0.0.1:8080","username":"user","password":"secret","disableConnectionReuse":true}}
                """),
            CancellationToken.None);

        var proxy = Assert.Single(factory.Requests);
        Assert.Equal("http", proxy.Type);
        Assert.Equal("http://127.0.0.1:8080", proxy.Url);
        Assert.Equal("user", proxy.Username);
        Assert.Equal("secret", proxy.Password);
        Assert.True(proxy.DisableConnectionReuse);
        Assert.Empty(direct.Requests);
        Assert.Equal(5, proxied.Requests.Count);
    }

    [Fact]
    public async Task CheckQuotaTreatsMalformedSettingsAsNoProxy()
    {
        var direct = HappyExecutor();
        var factory = new ScriptedProxyExecutorFactory();
        var checker = new ClineQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(Channel(settings: "{\"proxy\":{"), CancellationToken.None);

        Assert.Empty(factory.Requests);
        Assert.Equal(5, direct.Requests.Count);
    }

    [Fact]
    public async Task CheckQuotaUsesTheDirectExecutorWithoutASettingsProxy()
    {
        var direct = HappyExecutor();
        var factory = new ScriptedProxyExecutorFactory();
        var checker = new ClineQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Empty(factory.Requests);
        Assert.Equal(5, direct.Requests.Count);
    }

    [Theory]
    [InlineData("cline", true)]
    [InlineData("openai", false)]
    [InlineData("claudecode", false)]
    public void SupportsChannelAcceptsOnlyCline(string channelType, bool expected)
    {
        var checker = DummyChecker();

        Assert.Equal(
            expected,
            checker.SupportsChannel(new ChannelRecord { Type = channelType }));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ClineQuotaChecker CreateChecker(
        IExecutor executor,
        DateTimeOffset? now = null)
        => new(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(now ?? Now));

    private static ClineQuotaChecker DummyChecker()
        => CreateChecker(new ScriptedExecutor());

    private static ScriptedExecutor HappyExecutor(string plans = PlansBody) => new()
    {
        OnDo = (call, _, _) => call switch
        {
            0 => Ok(MeBody),
            1 => Ok(plans),
            2 => Ok(BalanceBody),
            3 => Ok(UsageLimitsBody),
            4 => Ok(UsagesBody),
            _ => throw new InvalidOperationException("unexpected Cline request " + call),
        },
    };

    private static ChannelRecord Channel(
        string credentials = """{"apiKey":"test-api-key"}""",
        string supportedModels = """["cline-pass/deepseek-v4-flash","cline-pass/qwen3.7-plus"]""",
        string? manualModels = null,
        string baseUrl = "https://api.cline.bot/v1",
        string settings = "{}")
        => new()
        {
            Type = "cline",
            BaseUrl = baseUrl,
            Credentials = credentials,
            SupportedModels = supportedModels,
            ManualModels = manualModels,
            Settings = settings,
        };

    private static ClinePlan Plan(string type, long threshold, bool enabled, bool active) => new()
    {
        Type = type,
        Interval = "monthly",
        IsActive = active,
        Entitlements = new ClineEntitlements
        {
            ClinePass = new ClinePassEntitlement
            {
                Enabled = enabled,
                InferenceCapThreshold = new ClineInferenceCapThreshold
                {
                    Last5HoursUsageCostUsdPerUser = threshold,
                    Last7DaysUsageCostUsdPerUser = threshold,
                    Last30DaysUsageCostUsdPerUser = threshold,
                },
            },
        },
    };

    private static Dictionary<string, object?> Window(QuotaData quota, string key)
        => Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<Dictionary<string, object?>>(quota.RawData["windows"])[key]);

    private static Dictionary<string, object?> UsageFetch(QuotaData quota)
        => Assert.IsType<Dictionary<string, object?>>(quota.RawData["usage_fetch"]);

    private static Dictionary<string, object?> UsageLimitsFetch(QuotaData quota)
        => Assert.IsType<Dictionary<string, object?>>(quota.RawData["usage_limits_fetch"]);

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    private static HttpResponseData Ok(string body) => new()
    {
        StatusCode = 200,
        Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
        Body = Encoding.UTF8.GetBytes(body),
    };

    private static HttpResponseData Status(int statusCode, string body) => new()
    {
        StatusCode = statusCode,
        Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
        Body = Encoding.UTF8.GetBytes(body),
    };

    private static void AssertOmitsSensitiveValues(string message)
    {
        foreach (var marker in SensitiveMarkers)
        {
            Assert.DoesNotContain(marker, message);
        }
    }
}
