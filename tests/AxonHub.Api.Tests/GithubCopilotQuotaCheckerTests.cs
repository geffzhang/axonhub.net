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
/// Ports internal/server/biz/provider_quota/github_copilot_checker.go together
/// with the github_copilot fixture of provider_contract_fixtures_test.go and
/// TestGithubCopilotQuotaChecker_PreservesMonthlyResetWithoutPeriod
/// (period_window_test.go): the copilot_internal/user request and its
/// LiteLLM-style editor headers, the credential fallback chain (typed oauth
/// object, oauth JSON in the legacy apiKey, raw apiKey), the numeric
/// limited_user_quotas and percentage quota_snapshots, the overall status from
/// the lowest percentage, and the monthly reset without a period window.
/// </summary>
public sealed class GithubCopilotQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private const string UserUrl = "https://api.github.com/copilot_internal/user";

    private const string Token = "fixture-token";

    private const string ContractFixtureBody = """
        {"copilot_plan":"individual","quota_reset_date_utc":"2099-02-01T00:00:00Z","limited_user_quotas":{"premium_interactions":8},"monthly_quotas":{"premium_interactions":10},"quota_snapshots":{"chat":{"percent_remaining":75}}}
        """;

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaFetchesTheCopilotUserPayloadAndMapsTheQuotaWindows()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(ContractFixtureBody);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(UserUrl, request.Url);
        Assert.Equal("token " + Token, request.Header("Authorization"));
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Equal("vscode/1.95.0", request.Header("Editor-Version"));
        Assert.Equal("copilot-chat/0.26.7", request.Header("Editor-Plugin-Version"));
        Assert.Equal("GitHubCopilotChat/0.26.7", request.Header("User-Agent"));
        Assert.Equal("vscode-chat", request.Header("Copilot-Integration-Id"));
        Assert.Equal("conversation-edits", request.Header("Openai-Intent"));
        Assert.Equal("2025-04-01", request.Header("X-Github-Api-Version"));
        Assert.Equal("electron-fetch", request.Header("X-Vscode-User-Agent-Library-Version"));
        Assert.Equal(string.Empty, request.Body);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal("github_copilot", quota.ProviderType);
        Assert.True(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);

        // Limited quotas are emitted before snapshots, each family sorted by key.
        Assert.Equal("premium_interactions", quota.Limits[0].Window);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[0].Status);
        Assert.Equal(0.2, quota.Limits[0].UsageRatio, 9);
        Assert.Equal(
            new DateTimeOffset(2099, 2, 1, 0, 0, 0, TimeSpan.Zero),
            quota.Limits[0].NextResetAt);
        Assert.Null(quota.Limits[0].PeriodStart);

        Assert.Equal("chat", quota.Limits[1].Window);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[1].Status);
        Assert.Equal(0.25, quota.Limits[1].UsageRatio, 9);
        Assert.Null(quota.Limits[1].PeriodStart);

        Assert.Equal(
            new DateTimeOffset(2099, 2, 1, 0, 0, 0, TimeSpan.Zero),
            quota.NextResetAt);

        Assert.Equal("individual", quota.RawData["copilot_plan"]);
        Assert.Equal(
            "2099-02-01T00:00:00Z",
            quota.RawData["quota_reset_date_utc"]);
        var limitedQuotas = Assert.IsType<Dictionary<string, object?>>(
            quota.RawData["limited_user_quotas"]);
        Assert.Equal(8d, limitedQuotas["premium_interactions"]);
        var totalQuotas = Assert.IsType<Dictionary<string, object?>>(quota.RawData["total_quotas"]);
        Assert.Equal(10d, totalQuotas["premium_interactions"]);
        var snapshots = Assert.IsType<Dictionary<string, object?>>(quota.RawData["quota_snapshots"]);
        var chat = Assert.IsType<Dictionary<string, object?>>(snapshots["chat"]);
        Assert.Equal(75d, chat["percent_remaining"]);
    }

    [Fact]
    public async Task CheckQuotaKeepsTheCopilotHeadersOnTheRequestData()
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok("{}") };
        var checker = CreateChecker(executor);

        await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var request = Assert.Single(executor.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(UserUrl, request.Url);
        Assert.Equal("token " + Token, HttpClientUtils.HeaderGet(request.Headers, "Authorization"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Accept"));
        Assert.Equal(
            "vscode/1.95.0",
            HttpClientUtils.HeaderGet(
                request.Headers,
                AxonHub.Llm.Transformer.OpenAI.Copilot.CopilotConstants.EditorVersionHeader));
        Assert.Equal(
            "copilot-chat/0.26.7",
            HttpClientUtils.HeaderGet(
                request.Headers,
                AxonHub.Llm.Transformer.OpenAI.Copilot.CopilotConstants.EditorPluginVersionHeader));
        Assert.Equal(
            "GitHubCopilotChat/0.26.7",
            HttpClientUtils.HeaderGet(
                request.Headers,
                AxonHub.Llm.Transformer.OpenAI.Copilot.CopilotConstants.UserAgentHeader));
        Assert.Equal(
            "vscode-chat",
            HttpClientUtils.HeaderGet(
                request.Headers,
                AxonHub.Llm.Transformer.OpenAI.Copilot.CopilotConstants.CopilotIntegrationIdHeader));
        Assert.Equal(
            "conversation-edits",
            HttpClientUtils.HeaderGet(
                request.Headers,
                AxonHub.Llm.Transformer.OpenAI.Copilot.CopilotConstants.OpenAiIntentHeader));
        Assert.Equal(
            "2025-04-01",
            HttpClientUtils.HeaderGet(
                request.Headers,
                AxonHub.Llm.Transformer.OpenAI.Copilot.CopilotConstants.GitHubApiVersionHeader));
        Assert.Equal(
            "electron-fetch",
            HttpClientUtils.HeaderGet(
                request.Headers,
                AxonHub.Llm.Transformer.OpenAI.Copilot.CopilotConstants.VSCodeUserAgentLibHeader));
        Assert.Null(request.Body);
        Assert.Null(request.Auth);
    }

    [Fact]
    public async Task CheckQuotaResolvesTheChannelProxyOnce()
    {
        var baseExecutor = new ScriptedExecutor();
        var proxied = new ScriptedExecutor { OnDo = (_, _, _) => Ok("{}") };
        var proxyFactory = new ScriptedProxyExecutorFactory { Executor = proxied };
        var checker = new GithubCopilotQuotaChecker(
            baseExecutor,
            proxyFactory,
            new AntigravityTestClock(Now));

        const string settings = """
            {"proxy":{"type":"url","url":"http://proxy.local:8080","username":"user","password":"pass","disableConnectionReuse":true}}
            """;
        await checker.CheckQuotaAsync(Channel(settings: settings), CancellationToken.None);

        var proxy = Assert.Single(proxyFactory.Requests);
        Assert.Equal(ProxyType.Url, proxy.Type);
        Assert.Equal("http://proxy.local:8080", proxy.Url);
        Assert.Equal("user", proxy.Username);
        Assert.Equal("pass", proxy.Password);
        Assert.True(proxy.DisableConnectionReuse);
        Assert.Equal(0, baseExecutor.Calls);
        var request = Assert.Single(proxied.Requests);
        Assert.Equal(UserUrl, request.Url);
        Assert.Equal("token " + Token, HttpClientUtils.HeaderGet(request.Headers, "Authorization"));
    }

    [Fact]
    public async Task CheckQuotaTreatsMalformedSettingsAsNoProxy()
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok("{}") };
        var proxyFactory = new ScriptedProxyExecutorFactory();
        var checker = new GithubCopilotQuotaChecker(
            executor,
            proxyFactory,
            new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(Channel(settings: "{not-json"), CancellationToken.None);

        Assert.Empty(proxyFactory.Requests);
        Assert.Equal(1, executor.Calls);
    }

    // ------------------------------------------------------------------
    // Credentials
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaPrefersTheTypedOauthAccessToken()
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok("{}") };
        var checker = CreateChecker(executor);
        var credentials = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oauth"] = new Dictionary<string, string> { ["access_token"] = "oauth-token" },
            ["apiKey"] = "legacy-token",
        });

        await checker.CheckQuotaAsync(Channel(credentials: credentials), CancellationToken.None);

        Assert.Equal(
            "token oauth-token",
            HttpClientUtils.HeaderGet(executor.Requests[0].Headers, "Authorization"));
    }

    [Fact]
    public async Task CheckQuotaParsesTheOauthJsonStoredInTheLegacyApiKey()
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok("{}") };
        var checker = CreateChecker(executor);
        var credentials = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["apiKey"] = """{"access_token":"legacy-oauth-token"}""",
        });

        await checker.CheckQuotaAsync(Channel(credentials: credentials), CancellationToken.None);

        Assert.Equal(
            "token legacy-oauth-token",
            HttpClientUtils.HeaderGet(executor.Requests[0].Headers, "Authorization"));
    }

    [Fact]
    public async Task CheckQuotaFallsBackToTheRawApiKeyWhenTheStoredOauthJsonHasNoToken()
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok("{}") };
        var checker = CreateChecker(executor);
        var credentials = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["apiKey"] = """{"access_token":""}""",
        });

        await checker.CheckQuotaAsync(Channel(credentials: credentials), CancellationToken.None);

        Assert.Equal(
            """token {"access_token":""}""",
            HttpClientUtils.HeaderGet(executor.Requests[0].Headers, "Authorization"));
    }

    [Fact]
    public async Task CheckQuotaFallsBackToAnUnparsableApiKey()
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok("{}") };
        var checker = CreateChecker(executor);
        var credentials = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["apiKey"] = "not-json",
        });

        await checker.CheckQuotaAsync(Channel(credentials: credentials), CancellationToken.None);

        Assert.Equal(
            "token not-json",
            HttpClientUtils.HeaderGet(executor.Requests[0].Headers, "Authorization"));
    }

    [Fact]
    public async Task CheckQuotaSendsAnEmptyOauthTokenVerbatim()
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok("{}") };
        var checker = CreateChecker(executor);
        var credentials = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["oauth"] = new Dictionary<string, string> { ["access_token"] = string.Empty },
        });

        await checker.CheckQuotaAsync(Channel(credentials: credentials), CancellationToken.None);

        // The reference returns the typed token without validation, so the
        // request header is the literal "token " and no error is raised.
        Assert.Equal(
            "token ",
            HttpClientUtils.HeaderGet(executor.Requests[0].Headers, "Authorization"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"apiKey":""}""")]
    [InlineData("""{"apiKey":"   "}""")]
    public async Task CheckQuotaRejectsAMissingAccessToken(string credentials)
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok("{}") };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(credentials: credentials), CancellationToken.None));

        Assert.Equal("channel has no credentials", error.Message);
        Assert.Equal(0, executor.Calls);
    }

    // ------------------------------------------------------------------
    // Transport and parse errors
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaWrapsTheExecutorError()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new HttpClientError(
                "GET",
                UserUrl,
                401,
                "401 Unauthorized"),
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "failed to fetch copilot user info: GET - " + UserUrl + " with status 401 Unauthorized",
            error.Message);
        Assert.IsType<HttpClientError>(error.InnerException);
    }

    [Theory]
    [InlineData(199)]
    [InlineData(302)]
    public async Task CheckQuotaRejectsStatusesOutsideTheSuccessRange(int statusCode)
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = statusCode,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
                Body = [],
            },
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("failed to fetch copilot user info, status: " + statusCode, error.Message);
    }

    [Fact]
    public async Task CheckQuotaWrapsAnUndecodableBody()
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => Ok("{not-json") };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.StartsWith("failed to parse copilot user response: ", error.Message, StringComparison.Ordinal);
        Assert.IsType<JsonException>(error.InnerException);
    }

    [Fact]
    public async Task CheckQuotaWrapsAFieldWithTheWrongJsonType()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok("""{"copilot_plan":5}"""),
        };
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "failed to parse copilot user response: json: cannot unmarshal number into Go struct field copilotUserPayload.copilot_plan of type string",
            error.Message);
    }

    // ------------------------------------------------------------------
    // Limits
    // ------------------------------------------------------------------

    [Fact]
    public void BuildLimitsPairsTheLimitedQuotasWithTheirMonthlyTotals()
    {
        var payload = Payload("""
            {"limited_user_quotas":{"b":5,"a":10},"monthly_quotas":{"b":10,"a":10}}
            """);

        var limits = GithubCopilotQuotaChecker.BuildLimits(payload, null);

        Assert.Equal(2, limits.Count);
        Assert.Equal("a", limits[0].Window);
        Assert.Equal(0.0, limits[0].UsageRatio);
        Assert.Equal("b", limits[1].Window);
        Assert.Equal(0.5, limits[1].UsageRatio, 9);
    }

    [Theory]
    [InlineData("""{"limited_user_quotas":{"a":5}}""")]
    [InlineData("""{"limited_user_quotas":{"a":5},"monthly_quotas":{"a":"10"}}""")]
    [InlineData("""{"limited_user_quotas":{"a":5},"monthly_quotas":{"a":-3}}""")]
    public void BuildLimitsFallsBackToTheRemainingAmountWhenTheTotalIsUnusable(string body)
    {
        var limits = GithubCopilotQuotaChecker.BuildLimits(Payload(body), null);

        var limit = Assert.Single(limits);
        Assert.Equal("a", limit.Window);
        Assert.Equal(0.0, limit.UsageRatio);
        Assert.Equal(ProviderQuotaStatuses.Available, limit.Status);
    }

    [Fact]
    public void BuildLimitsSkipsAZeroTotalWithRemainingQuota()
    {
        var limits = GithubCopilotQuotaChecker.BuildLimits(
            Payload("""{"limited_user_quotas":{"a":5},"monthly_quotas":{"a":0}}"""),
            null);

        Assert.Empty(limits);
    }

    [Fact]
    public void BuildLimitsKeepsAZeroRemainingOverAZeroTotal()
    {
        var limits = GithubCopilotQuotaChecker.BuildLimits(
            Payload("""{"limited_user_quotas":{"a":0},"monthly_quotas":{"a":0}}"""),
            null);

        var limit = Assert.Single(limits);
        Assert.Equal(0.0, limit.UsageRatio);
    }

    [Theory]
    [InlineData("""{"limited_user_quotas":{"a":-1}}""")]
    [InlineData("""{"limited_user_quotas":{"a":"5"}}""")]
    [InlineData("""{"limited_user_quotas":{"a":null}}""")]
    public void BuildLimitsSkipsANonNumericOrNegativeRemaining(string body)
    {
        Assert.Empty(GithubCopilotQuotaChecker.BuildLimits(Payload(body), null));
    }

    [Fact]
    public void BuildLimitsReadsTheSnapshotsAndSkipsUnlimitedOrOutOfRangeEntries()
    {
        var payload = Payload("""
            {"quota_snapshots":{
                "chat":{"percent_remaining":75},
                "unlimited":{"unlimited":true,"percent_remaining":1},
                "not-object":"x",
                "over":{"percent_remaining":101},
                "negative":{"percent_remaining":-1},
                "non-boolean-unlimited":{"unlimited":"yes","percent_remaining":50}
            }}
            """);

        var limits = GithubCopilotQuotaChecker.BuildLimits(payload, null);

        Assert.Equal(2, limits.Count);
        Assert.Equal("chat", limits[0].Window);
        Assert.Equal(0.25, limits[0].UsageRatio, 9);
        Assert.Equal("non-boolean-unlimited", limits[1].Window);
        Assert.Equal(0.5, limits[1].UsageRatio, 9);
    }

    [Theory]
    [InlineData(100.0, 0.0, "available")]
    [InlineData(20.0, 0.8, "warning")]
    [InlineData(0.0, 1.0, "exhausted")]
    [InlineData(99.99, 0.0001, "available")]
    public void BuildLimitsTurnsThePercentRemainingIntoTheUsageRatio(
        double percentRemaining,
        double expectedRatio,
        string expectedStatus)
    {
        var payload = new CopilotUserPayload
        {
            QuotaSnapshots = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["chat"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["percent_remaining"] = percentRemaining,
                },
            },
        };

        var limit = Assert.Single(GithubCopilotQuotaChecker.BuildLimits(payload, null));

        Assert.Equal(expectedRatio, limit.UsageRatio, 9);
        Assert.Equal(expectedStatus, limit.Status);
        Assert.Equal("chat", limit.Window);
    }

    [Theory]
    [InlineData(0.79, "available")]
    [InlineData(0.8, "warning")]
    [InlineData(0.99, "warning")]
    [InlineData(1.0, "exhausted")]
    [InlineData(1.5, "exhausted")]
    public void CopilotLimitUsesTheWarningThresholdRatio(double ratio, string expectedStatus)
    {
        var limit = GithubCopilotQuotaChecker.CopilotLimit("chat", ratio, null);

        Assert.Equal(expectedStatus, limit.Status);
        Assert.Equal(ratio, limit.UsageRatio);
        Assert.Equal(QuotaLimitTypes.Token, limit.Type);
        Assert.Null(limit.PeriodStart);
        Assert.Equal(ProviderQuotaStatuses.IsReadyStatus(expectedStatus), limit.Ready);
    }

    [Fact]
    public void BuildLimitsKeepsTheResetButNeverDerivesAPeriodStart()
    {
        var resetAt = new DateTimeOffset(2099, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var payload = Payload("""
            {"limited_user_quotas":{"premium_interactions":8},"monthly_quotas":{"premium_interactions":10}}
            """);

        var limit = Assert.Single(GithubCopilotQuotaChecker.BuildLimits(payload, resetAt));

        Assert.Equal(resetAt, limit.NextResetAt);
        Assert.Null(limit.PeriodStart);
    }

    // ------------------------------------------------------------------
    // Overall status
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("""{}""", "available")]
    [InlineData("""{"limited_user_quotas":{"a":8},"monthly_quotas":{"a":10}}""", "available")]
    [InlineData("""{"limited_user_quotas":{"a":1},"monthly_quotas":{"a":10}}""", "warning")]
    [InlineData("""{"limited_user_quotas":{"a":0},"monthly_quotas":{"a":10}}""", "exhausted")]
    [InlineData("""{"quota_snapshots":{"a":{"percent_remaining":15}}}""", "warning")]
    [InlineData("""{"quota_snapshots":{"a":{"percent_remaining":0}}}""", "exhausted")]
    [InlineData("""{"quota_snapshots":{"a":{"unlimited":true,"percent_remaining":1}}}""", "available")]
    [InlineData(
        """{"limited_user_quotas":{"a":8},"monthly_quotas":{"a":10},"quota_snapshots":{"chat":{"percent_remaining":10}}}""",
        "warning")]
    public void CalculateStatusUsesTheLowestReportedPercentage(string body, string expectedStatus)
    {
        var (status, _) = GithubCopilotQuotaChecker.CalculateStatus(Payload(body));

        Assert.Equal(expectedStatus, status);
    }

    [Fact]
    public void CalculateStatusCountsASnapshotWhoseUnlimitedFlagIsNotABoolean()
    {
        var (status, lowest) = GithubCopilotQuotaChecker.CalculateStatus(
            Payload("""{"quota_snapshots":{"a":{"unlimited":"yes","percent_remaining":5}}}"""));

        Assert.Equal(ProviderQuotaStatuses.Warning, status);
        Assert.Equal(5.0, lowest);
    }

    // ------------------------------------------------------------------
    // Reset parsing
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("""{"quota_reset_date_utc":"2099-02-01T00:00:00Z"}""", "2099-02-01T00:00:00Z")]
    [InlineData("""{"quota_reset_date_utc":"2099-02-01T8:00:00Z"}""", "2099-02-01T08:00:00Z")]
    [InlineData("""{"quota_reset_date_utc":"2099-02-01T00:00:00.5Z"}""", "2099-02-01T00:00:00.5Z")]
    public void ParseResetDateAcceptsTheUtcReset(string body, string expected)
    {
        var reset = GithubCopilotQuotaChecker.ParseResetDate(Payload(body));

        Assert.Equal(DateTimeOffset.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), reset);
    }

    [Fact]
    public void ParseResetDatePreservesTheOffset()
    {
        var reset = GithubCopilotQuotaChecker.ParseResetDate(
            Payload("""{"quota_reset_date_utc":"2099-02-01T08:00:00+08:00"}"""));

        Assert.NotNull(reset);
        Assert.Equal(TimeSpan.FromHours(8), reset.Value.Offset);
        Assert.Equal(
            new DateTimeOffset(2099, 2, 1, 8, 0, 0, TimeSpan.FromHours(8)),
            reset.Value);
    }

    [Fact]
    public void ParseResetDateFallsBackToTheDateOnlyFields()
    {
        var fromQuotaResetDate = GithubCopilotQuotaChecker.ParseResetDate(
            Payload("""{"quota_reset_date_utc":"bad","quota_reset_date":"2099-03-01"}"""));
        Assert.Equal(new DateTimeOffset(2099, 3, 1, 0, 0, 0, TimeSpan.Zero), fromQuotaResetDate);

        var fromLimitedResetDate = GithubCopilotQuotaChecker.ParseResetDate(
            Payload("""
                {"quota_reset_date_utc":"bad","quota_reset_date":"bad","limited_user_reset_date":"2099-04-01"}
                """));
        Assert.Equal(new DateTimeOffset(2099, 4, 1, 0, 0, 0, TimeSpan.Zero), fromLimitedResetDate);
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"quota_reset_date_utc":""}""")]
    [InlineData("""{"quota_reset_date_utc":"bad"}""")]
    [InlineData("""{"quota_reset_date_utc":"bad","quota_reset_date":"2099-02-30"}""")]
    [InlineData("""{"quota_reset_date":"2025-2-3"}""")]
    public void ParseResetDateRejectsEmptyAndInvalidValues(string body)
    {
        Assert.Null(GithubCopilotQuotaChecker.ParseResetDate(Payload(body)));
    }

    [Fact]
    public async Task CheckQuotaKeepsTheMonthlyResetWithoutADerivedPeriod()
    {
        // Ports TestGithubCopilotQuotaChecker_PreservesMonthlyResetWithoutPeriod.
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok("""
                {"copilot_plan":"individual","quota_reset_date_utc":"2099-03-01T00:00:00Z","quota_snapshots":{"premium_interactions":{"percent_remaining":40,"entitlement":300,"remaining":120}}}
                """),
        };
        var checker = CreateChecker(executor);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal("premium_interactions", limit.Window);
        Assert.Null(limit.PeriodStart);
        Assert.Equal(new DateTimeOffset(2099, 3, 1, 0, 0, 0, TimeSpan.Zero), limit.NextResetAt);
        Assert.Equal(0.6, limit.UsageRatio, 9);
    }

    // ------------------------------------------------------------------
    // Raw data
    // ------------------------------------------------------------------

    [Fact]
    public void PrepareRawDataAlwaysCarriesTheCopilotPlanAsNullWhenMissing()
    {
        var rawData = GithubCopilotQuotaChecker.PrepareRawData(
            GithubCopilotQuotaChecker.ParseUserPayload(Encoding.UTF8.GetBytes("{}")));

        Assert.Equal("""{"copilot_plan":null}""", GoJson.MarshalToString(rawData));
    }

    [Fact]
    public void PrepareRawDataWritesEveryPresentFieldInGoKeyOrder()
    {
        var rawData = GithubCopilotQuotaChecker.PrepareRawData(Payload("""
            {"copilot_plan":"individual","access_type_sku":"copilot_pro","limited_user_reset_date":"2099-01-01","quota_reset_date_utc":"2099-02-01T00:00:00Z","quota_reset_date":"2099-02-01","limited_user_quotas":{"premium_interactions":8},"monthly_quotas":{"premium_interactions":10},"quota_snapshots":{"chat":{"percent_remaining":75}}}
            """));

        Assert.Equal(
            """{"access_type_sku":"copilot_pro","copilot_plan":"individual","limited_user_quotas":{"premium_interactions":8},"limited_user_reset_date":"2099-01-01","plan_type":"Pro","quota_reset_date":"2099-02-01","quota_reset_date_utc":"2099-02-01T00:00:00Z","quota_snapshots":{"chat":{"percent_remaining":75}},"total_quotas":{"premium_interactions":10}}""",
            GoJson.MarshalToString(rawData));
    }

    [Theory]
    [InlineData("copilot_free", "Free")]
    [InlineData("free_limited_copilot", "Free")]
    [InlineData("copilot_pro", "Pro")]
    [InlineData("copilot_pro_plus", "Pro+")]
    [InlineData("copilot_business", "Business")]
    [InlineData("copilot_enterprise", "Enterprise")]
    [InlineData("free_educational_quota", "Edu")]
    [InlineData("something_else", "")]
    public void MapPlanTypeMatchesTheReferenceTable(string sku, string expected)
    {
        Assert.Equal(expected, GithubCopilotQuotaChecker.MapPlanType(sku));
    }

    [Fact]
    public void PrepareRawDataOmitsThePlanTypeForAnUnknownSku()
    {
        var rawData = GithubCopilotQuotaChecker.PrepareRawData(
            Payload("""{"access_type_sku":"something_else"}"""));

        Assert.Equal("something_else", rawData["access_type_sku"]);
        Assert.False(rawData.ContainsKey("plan_type"));
    }

    // ------------------------------------------------------------------
    // Capability and parsing edge cases
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("github_copilot", true)]
    [InlineData("xai_subscription", false)]
    [InlineData("codex", false)]
    public void SupportsChannelOnlyMatchesGithubCopilot(string channelType, bool expected)
    {
        var checker = CreateChecker(new ScriptedExecutor());
        var channel = new ChannelRecord { Type = channelType };

        Assert.Equal(expected, checker.SupportsChannel(channel));
    }

    [Fact]
    public void ParseUserPayloadTreatsJsonNullAsTheZeroPayload()
    {
        var payload = GithubCopilotQuotaChecker.ParseUserPayload(Encoding.UTF8.GetBytes("null"));

        Assert.Null(payload.CopilotPlan);
        Assert.Null(payload.LimitedUserQuotas);
        Assert.Null(payload.MonthlyQuotas);
        Assert.Null(payload.QuotaSnapshots);
    }

    [Fact]
    public void ParseUserPayloadRejectsANonObjectRoot()
    {
        var error = Assert.Throws<JsonException>(
            () => GithubCopilotQuotaChecker.ParseUserPayload(Encoding.UTF8.GetBytes("[]")));

        Assert.Equal(
            "json: cannot unmarshal array into Go value of type provider_quota.copilotUserPayload",
            error.Message);
    }

    [Fact]
    public void ParseUserPayloadMergesRepeatedMapFieldsLikeGo()
    {
        var payload = Payload("""
            {"quota_snapshots":{"first":{"percent_remaining":50}},"quota_snapshots":{"second":{"percent_remaining":75}}}
            """);

        Assert.NotNull(payload.QuotaSnapshots);
        Assert.Equal(2, payload.QuotaSnapshots.Count);
        Assert.True(payload.QuotaSnapshots.ContainsKey("first"));
        Assert.True(payload.QuotaSnapshots.ContainsKey("second"));
    }

    [Fact]
    public void ParseUserPayloadKeepsTheLastValueOfARepeatedScalarField()
    {
        var payload = Payload("""{"copilot_plan":"first","Copilot_Plan":null}""");

        Assert.Null(payload.CopilotPlan);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static GithubCopilotQuotaChecker CreateChecker(
        out ClaudeCodeQuotaCheckerTests.RecordingHttpHandler handler)
    {
        handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new GithubCopilotQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now));
    }

    private static GithubCopilotQuotaChecker CreateChecker(ScriptedExecutor executor)
        => new(executor, new ScriptedProxyExecutorFactory(), new AntigravityTestClock(Now));

    private static ChannelRecord Channel(string? credentials = null, string settings = "{}")
        => new()
        {
            Type = "github_copilot",
            Credentials = credentials ?? JsonSerializer.Serialize(
                new Dictionary<string, object?> { ["apiKey"] = Token }),
            Settings = settings,
        };

    private static CopilotUserPayload Payload(string json)
        => GithubCopilotQuotaChecker.ParseUserPayload(Encoding.UTF8.GetBytes(json));

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
