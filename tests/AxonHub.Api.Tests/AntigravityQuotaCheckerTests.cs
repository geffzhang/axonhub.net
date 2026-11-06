using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Antigravity;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports internal/server/biz/provider_quota/antigravity_checker_test.go and
/// antigravity_summary_test.go: the summary-first request flow with its legacy
/// fallback, the legacy per-model pool collapse, the summary cadence windows,
/// the pool availability groups and the accepted reset-time shapes. The checker
/// runs through the real <see cref="HttpExecutor"/> over a recording message
/// handler, so the wire shape (URLs, auth, client headers, body) is locked too.
/// </summary>
public sealed class AntigravityQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private const string SummaryFixture = """
        {"response":{"groups":[
         {"displayName":"Gemini Models","buckets":[
          {"bucketId":"gemini-weekly","displayName":"Weekly Limit","remaining":{"remainingFraction":0.82},"resetTime":"2099-09-11T08:00:00Z"},
          {"bucketId":"gemini-5h","displayName":"Five Hour Limit","remaining":{"remainingFraction":0.91},"resetTime":"2099-09-04T13:00:00Z"}]},
         {"displayName":"Claude and GPT models","buckets":[
          {"bucketId":"3p-weekly","remaining":{"case":"remainingFraction","value":0.64}},
          {"bucketId":"3p-5h","remainingFraction":0.73}]}]}}
        """;

    private const string LegacyModelsFixture = """
        {"models":{
          "gemini-3-pro":{"displayName":"Gemini 3 Pro","quotaInfo":{"remainingFraction":0.75,"resetTime":"2099-09-04T08:00:00Z"}},
          "claude-sonnet":{"displayName":"Claude Sonnet","quotaInfo":{"remainingFraction":0.1,"resetTime":"2099-09-04T08:00:00Z"}},
          "internal":{"isInternal":true,"quotaInfo":{"remainingFraction":0}}
        }}
        """;

    private const string ValidCredentials = """
        {"apiKey":"refresh-token|project-id","oauth":{"access_token":"access-token","expires_at":"2099-01-01T00:00:00Z"}}
        """;

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaParsesTheLegacyBodyWhenTheSummaryBodyIsNotReadable()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(HttpStatusCode.OK, LegacyModelsFixture);

        var quota = await checker.CheckQuotaAsync(Channel(ValidCredentials), CancellationToken.None);

        // The summary endpoint answers with the legacy shape: the summary parser
        // rejects it silently and the request falls back to the legacy endpoint.
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(AntigravityQuotaChecker.QuotaSummaryUrl, handler.Requests[0].Url);
        Assert.Equal(AntigravityQuotaChecker.QuotaUrl, handler.Requests[1].Url);
        foreach (var request in handler.Requests)
        {
            Assert.Equal("POST", request.Method);
            Assert.Equal("Bearer access-token", request.Header("Authorization"));
            Assert.Equal("application/json", request.Header("Content-Type"));
            Assert.Equal("application/json", request.Header("Accept"));
            Assert.Equal("antigravity", request.Header("X-Client-Name"));
            Assert.Equal(AntigravityConstants.ApiClient, request.Header("X-Goog-Api-Client"));
            Assert.Equal(AntigravityConstants.ClientMetadata, request.Header("Client-Metadata"));
            var version = request.Header("X-Client-Version");
            Assert.Matches("[0-9]+\\.[0-9]+\\.[0-9]+", version);
            Assert.Equal("antigravity/" + version + " windows/amd64", request.Header("User-Agent"));
            Assert.Equal("""{"project":"project-id"}""", request.Body);
        }

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.Equal("antigravity", quota.ProviderType);
        Assert.True(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal("gemini_5h", quota.Limits[0].Window);
        Assert.Equal("claude_gpt_5h", quota.Limits[1].Window);
        Assert.Equal(TimeSpan.FromHours(5), quota.Limits[0].ResetWindow());
        var models = Assert.IsAssignableFrom<Dictionary<string, object?>>(quota.RawData["models"]);
        Assert.Equal(2, models.Count);
        Assert.Equal(
            "warning",
            Assert.IsAssignableFrom<Dictionary<string, object?>>(models["claude-sonnet"])["status"]);
        Assert.Equal(new DateTimeOffset(2099, 9, 4, 8, 0, 0, TimeSpan.Zero), quota.NextResetAt);

        Assert.True(checker.SupportsChannel(Channel(ValidCredentials)));
        Assert.False(checker.SupportsChannel(new ChannelRecord { Type = "gemini" }));
    }

    [Fact]
    public async Task CheckQuotaRefreshesLegacyCredentials()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = request => request.Url == AntigravityConstants.TokenURL
            ? JsonResponse(
                HttpStatusCode.OK,
                """{"access_token":"fresh-access-token","expires_in":3600,"token_type":"Bearer"}""")
            : JsonResponse(
                HttpStatusCode.OK,
                """{"models":{"gemini":{"quotaInfo":{"remainingFraction":0.5}}}}""");

        var quota = await checker.CheckQuotaAsync(
            Channel("""{"apiKey":"refresh-token|project-id"}"""),
            CancellationToken.None);

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(AntigravityConstants.TokenURL, handler.Requests[0].Url);
        var form = handler.Requests[0].Form();
        Assert.Equal("refresh_token", Assert.Single(form["grant_type"]));
        Assert.Equal("refresh-token", Assert.Single(form["refresh_token"]));
        Assert.Equal(AntigravityConstants.ClientID, Assert.Single(form["client_id"]));
        Assert.Equal(AntigravityConstants.ClientSecret, Assert.Single(form["client_secret"]));

        Assert.Equal(AntigravityQuotaChecker.QuotaSummaryUrl, handler.Requests[1].Url);
        Assert.Equal(AntigravityQuotaChecker.QuotaUrl, handler.Requests[2].Url);
        Assert.Equal("Bearer fresh-access-token", handler.Requests[1].Header("Authorization"));
        Assert.Equal("Bearer fresh-access-token", handler.Requests[2].Header("Authorization"));
        Assert.Equal("""{"project":"project-id"}""", handler.Requests[1].Body);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
    }

    [Theory]
    [InlineData(200, 1, 4)]
    [InlineData(404, 2, 1)]
    [InlineData(403, 1, 0)]
    public async Task CheckQuotaPrefersSummaryAndFallsBackOnlyWhenUnavailable(
        int summaryStatus,
        int expectedCalls,
        int expectedLimits)
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = request =>
        {
            var status = (HttpStatusCode)summaryStatus;
            var payload = SummaryFixture;
            if (handler.Requests.Count > 1)
            {
                Assert.Equal(AntigravityQuotaChecker.QuotaUrl, request.Url);
                status = HttpStatusCode.OK;
                payload = """{"models":{"gemini":{"quotaInfo":{"remainingFraction":0.5}}}}""";
            }
            else
            {
                Assert.Equal(AntigravityQuotaChecker.QuotaSummaryUrl, request.Url);
            }

            Assert.Equal("Bearer access", request.Header("Authorization"));
            using var body = JsonDocument.Parse(request.Body);
            Assert.Equal("project", body.RootElement.GetProperty("project").GetString());
            return JsonResponse(status, payload);
        };

        var credentials = """
            {"apiKey":"refresh|project","oauth":{"access_token":"access","expires_at":"2099-01-01T00:00:00Z"}}
            """;

        if (summaryStatus == 403)
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(
                () => checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None));
            Assert.StartsWith("fetch Antigravity quota summary: ", error.Message);
        }
        else
        {
            var quota = await checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None);
            Assert.Equal(expectedLimits, quota.Limits.Count);
        }

        Assert.Equal(expectedCalls, handler.Requests.Count);
    }

    [Fact]
    public async Task CheckQuotaHandlesScriptedNonThrowingResponses()
    {
        // The Go client returns an error for non-2xx statuses, but the checker
        // also has a returned-response branch for custom transports. Scripted
        // executors (and only they) exercise it.
        var summaryMissing = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call == 0
                ? ModelFetchTestResponses.Json(SummaryFixture, statusCode: 404, reasonPhrase: "Not Found")
                : ModelFetchTestResponses.Json(
                    """{"models":{"gemini":{"quotaInfo":{"remainingFraction":0.5}}}}"""),
        };
        var checker = new AntigravityQuotaChecker(
            summaryMissing,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock());

        var quota = await checker.CheckQuotaAsync(Channel(ValidCredentials), CancellationToken.None);

        Assert.Equal(2, summaryMissing.Calls);
        Assert.Equal(AntigravityQuotaChecker.QuotaUrl, summaryMissing.Requests[1].Url);
        Assert.Single(quota.Limits);

        var summaryFailing = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ModelFetchTestResponses.Json(
                """{"error":"boom"}""",
                statusCode: 500,
                reasonPhrase: "Internal Server Error"),
        };
        checker = new AntigravityQuotaChecker(
            summaryFailing,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(ValidCredentials), CancellationToken.None));
        Assert.Equal("fetch Antigravity quota summary: status 500", error.Message);
        Assert.Equal(1, summaryFailing.Calls);

        var legacyFailing = new ScriptedExecutor();
        legacyFailing.OnDo = (call, _, _) => call == 0
            // A 200 the summary parser cannot read triggers the fallback.
            ? ModelFetchTestResponses.Json("{}")
            : ModelFetchTestResponses.Json(
                """{"error":"boom"}""",
                statusCode: 500,
                reasonPhrase: "Internal Server Error");
        checker = new AntigravityQuotaChecker(
            legacyFailing,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock());

        error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(ValidCredentials), CancellationToken.None));
        Assert.Equal("fetch legacy Antigravity quota: status 500", error.Message);
        Assert.Equal(2, legacyFailing.Calls);
    }

    [Fact]
    public async Task CheckQuotaFailsWithoutOAuthCredentials()
    {
        var checker = CreateChecker(out var handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel("{}"), CancellationToken.None));

        Assert.Equal("channel has no Antigravity OAuth credentials", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CheckQuotaRefreshesTheOAuthRefreshTokenBeforeTheLegacyApiKey()
    {
        var executor = new ScriptedExecutor();
        executor.OnDo = (call, request, _) =>
        {
            if (request.Url == AntigravityConstants.TokenURL)
            {
                Assert.Equal("oauth-refresh", Assert.Single(FormValue(request, "refresh_token")));
                return ModelFetchTestResponses.Json(
                    """{"access_token":"fresh-access","expires_in":3600,"token_type":"Bearer"}""");
            }

            Assert.Equal("fresh-access", request.Auth?.ApiKey);
            return ModelFetchTestResponses.Json(
                """{"models":{"gemini":{"quotaInfo":{"remainingFraction":0.5}}}}""");
        };
        var checker = new AntigravityQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock());

        var credentials = """
            {"apiKey":"legacy-refresh|project","oauth":{"access_token":"expired","refresh_token":"oauth-refresh","expires_at":"0001-01-01T00:00:00Z"}}
            """;
        var quota = await checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None);

        Assert.Equal(3, executor.Calls);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
    }

    [Fact]
    public async Task CheckQuotaWrapsRefreshFailures()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(
            HttpStatusCode.BadRequest,
            """{"error":"invalid_grant"}""");

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => checker.CheckQuotaAsync(
                Channel("""{"apiKey":"refresh-token|project-id"}"""),
                CancellationToken.None));

        Assert.StartsWith("refresh Antigravity OAuth token: ", error.Message);
        // No quota request may be sent with a token that could not be refreshed.
        Assert.Single(handler.Requests);
        Assert.Equal(AntigravityConstants.TokenURL, handler.Requests[0].Url);
    }

    [Fact]
    public async Task CheckQuotaUsesThePerChannelProxyInsteadOfTheSharedExecutor()
    {
        var baseExecutor = new ScriptedExecutor();
        var proxied = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ModelFetchTestResponses.Json(SummaryFixture),
        };
        var proxyFactory = new ScriptedProxyExecutorFactory { Executor = proxied };
        var checker = new AntigravityQuotaChecker(
            baseExecutor,
            proxyFactory,
            new AntigravityTestClock());

        const string settings = """
            {"proxy":{"type":"url","url":"http://proxy.local:8080","username":"user","password":"pass","disableConnectionReuse":true}}
            """;
        var quota = await checker.CheckQuotaAsync(
            Channel(ValidCredentials, settings),
            CancellationToken.None);

        Assert.Equal(4, quota.Limits.Count);
        var proxy = Assert.Single(proxyFactory.Requests);
        Assert.Equal(ProxyType.Url, proxy.Type);
        Assert.Equal("http://proxy.local:8080", proxy.Url);
        Assert.Equal("user", proxy.Username);
        Assert.Equal("pass", proxy.Password);
        Assert.True(proxy.DisableConnectionReuse);
        Assert.Equal(0, baseExecutor.Calls);
        var request = Assert.Single(proxied.Requests);
        Assert.Equal(AntigravityQuotaChecker.QuotaSummaryUrl, request.Url);
        Assert.Equal("access-token", request.Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaTreatsMalformedSettingsAsNoProxy()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call == 0
                ? ModelFetchTestResponses.Json(SummaryFixture)
                : throw new InvalidOperationException("unexpected fallback"),
        };
        var proxyFactory = new ScriptedProxyExecutorFactory();
        var checker = new AntigravityQuotaChecker(
            executor,
            proxyFactory,
            new AntigravityTestClock());

        var quota = await checker.CheckQuotaAsync(
            Channel(ValidCredentials, "{broken"),
            CancellationToken.None);

        Assert.Equal(4, quota.Limits.Count);
        Assert.Empty(proxyFactory.Requests);
    }

    // ------------------------------------------------------------------
    // Summary parsing
    // ------------------------------------------------------------------

    [Fact]
    public void ParseQuotaSummaryReportsIndependentPoolWindows()
    {
        var quota = AntigravityQuotaChecker.ParseQuotaSummary(
            Encoding.UTF8.GetBytes(SummaryFixture),
            Now);

        Assert.Equal(4, quota.Limits.Count);
        var expected = new (string Window, double Usage)[]
        {
            ("gemini_5h", 0.09),
            ("gemini_7d", 0.18),
            ("claude_gpt_5h", 0.27),
            ("claude_gpt_7d", 0.36),
        };
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Window, quota.Limits[i].Window);
            Assert.Equal(expected[i].Usage, quota.Limits[i].UsageRatio, 9);
            Assert.Equal(
                ProviderQuotaModelFilter.AntigravityPoolAvailabilityGroup(
                    ProviderQuotaModelFilter.AntigravityModelPool(
                        quota.Limits[i].Window!.Replace('_', ' '))),
                quota.Limits[i].AvailabilityGroup);
        }

        Assert.Equal(TimeSpan.FromHours(5), quota.Limits[0].ResetWindow());
        Assert.Equal(TimeSpan.FromDays(7), quota.Limits[1].ResetWindow());
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal("antigravity", quota.ProviderType);
        Assert.NotNull(quota.RawData["response"]);
    }

    [Theory]
    [InlineData("", "gemini-weekly", "Weekly Limit", "7d", 168)]
    [InlineData("", "3p-weekly", "", "7d", 168)]
    [InlineData("7d", "", "", "7d", 168)]
    [InlineData("", "gemini-5h", "Five Hour Limit", "5h", 5)]
    [InlineData("", "gemini-5h limit", "", "5h", 5)]
    [InlineData("", "gemini_session", "session", "5h", 5)]
    [InlineData("", "gemini-session-history", "", "5h", 5)]
    [InlineData("5-hour", "", "", "5h", 5)]
    [InlineData("", "gemini-5-hour", "", "5h", 5)]
    [InlineData("five hour limit", "", "", "5h", 5)]
    [InlineData("", "gemini-7-day", "", "7d", 168)]
    [InlineData("", "gemini-15h", "", "", 0)]
    [InlineData("", "gemini-24h", "", "", 0)]
    [InlineData("", "gemini-biweekly", "", "", 0)]
    [InlineData("", "gemini-bi-weekly", "", "", 0)]
    [InlineData("", "gemini-semi-weekly", "", "", 0)]
    [InlineData("", "gemini-15-hour", "", "", 0)]
    [InlineData("", "gemini-monthly", "Monthly Limit", "", 0)]
    [InlineData("", "", "", "", 0)]
    public void SummaryWindowRecognizesCadenceAliases(
        string window,
        string id,
        string displayName,
        string expectedWindow,
        int expectedHours)
    {
        var (actualWindow, duration) = AntigravityQuotaChecker.SummaryWindow(window, id, displayName);

        Assert.Equal(expectedWindow, actualWindow);
        Assert.Equal(TimeSpan.FromHours(expectedHours), duration);
    }

    [Fact]
    public void ParseQuotaSummaryDoesNotInventWindows()
    {
        var body = """
            {"groups":[{"displayName":"Gemini Models","buckets":[
             {"bucketId":"gemini-weekly","remainingFraction":0},
             {"bucketId":"gemini-5h","disabled":true,"remainingFraction":1},
             {"bucketId":"gemini-mystery","remainingFraction":1},
             {"bucketId":"gemini-5h"},
             {"bucketId":"gemini-5h","remainingFraction":-0.1}]}]}
            """;

        var quota = AntigravityQuotaChecker.ParseQuotaSummary(Encoding.UTF8.GetBytes(body), Now);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal("gemini_7d", limit.Window);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);

        var error = Assert.Throws<InvalidOperationException>(
            () => AntigravityQuotaChecker.ParseQuotaSummary(
                Encoding.UTF8.GetBytes(
                    """{"groups":[{"displayName":"Gemini Models","buckets":[{"bucketId":"gemini-5h"}]}]}"""),
                Now));
        Assert.Equal("Antigravity quota summary has no measured pool windows", error.Message);
    }

    [Fact]
    public void ParseQuotaSummaryPrefersResponseThenSummaryThenTopLevel()
    {
        static string Bucket(string id, double remaining) =>
            $$"""{"groups":[{"displayName":"Gemini Models","buckets":[{"bucketId":"{{id}}","remainingFraction":{{remaining}}}]}]}""";

        var allThree =
            """{"groups":[{"displayName":"Gemini Models","buckets":[{"bucketId":"gemini-5h","remainingFraction":0.9}]}],"""
            + "\"summary\":" + Bucket("3p-weekly", 0.9)
            + ",\"response\":" + Bucket("gemini-weekly", 0.9) + "}";

        var withResponse = AntigravityQuotaChecker.ParseQuotaSummary(
            Encoding.UTF8.GetBytes(allThree),
            Now);
        Assert.Equal("gemini_7d", Assert.Single(withResponse.Limits).Window);

        var summaryAndTopLevel =
            """{"groups":[{"displayName":"Gemini Models","buckets":[{"bucketId":"gemini-5h","remainingFraction":0.9}]}],"""
            + "\"summary\":" + Bucket("3p-weekly", 0.9) + "}";

        var withSummary = AntigravityQuotaChecker.ParseQuotaSummary(
            Encoding.UTF8.GetBytes(summaryAndTopLevel),
            Now);
        Assert.Equal("claude_gpt_7d", Assert.Single(withSummary.Limits).Window);

        var topLevelOnly =
            """{"groups":[{"displayName":"Gemini Models","buckets":[{"bucketId":"gemini-5h","remainingFraction":0.9}]}]}""";
        var plain = AntigravityQuotaChecker.ParseQuotaSummary(
            Encoding.UTF8.GetBytes(topLevelOnly),
            Now);
        Assert.Equal("gemini_5h", Assert.Single(plain.Limits).Window);
    }

    // ------------------------------------------------------------------
    // Legacy parsing
    // ------------------------------------------------------------------

    [Fact]
    public void ParseQuotaCollapsesLegacyEffortVariantsPerPool()
    {
        var body = """
            {"models":{
             "claude-opus-5-5-low":{"quotaInfo":{"remainingFraction":1}},
             "claude-opus-5-5-medium":{"quotaInfo":{"remainingFraction":0.4}},
             "claude-sonnet-5-5-high":{"quotaInfo":{"remainingFraction":0.7}},
             "gemini-3.8-flash-low":{"quotaInfo":{"remainingFraction":0.8}},
             "gemini-3.8-flash-high":{"quotaInfo":{"remainingFraction":0.3}}}}
            """;

        var quota = AntigravityQuotaChecker.ParseQuota(Encoding.UTF8.GetBytes(body), Now);

        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal("gemini_5h", quota.Limits[0].Window);
        Assert.Equal(0.7, quota.Limits[0].UsageRatio, 9);
        Assert.Equal("claude_gpt_5h", quota.Limits[1].Window);
        Assert.Equal(0.6, quota.Limits[1].UsageRatio, 9);
        var models = Assert.IsAssignableFrom<Dictionary<string, object?>>(quota.RawData["models"]);
        Assert.Equal(5, models.Count);
    }

    [Fact]
    public void PoolsCarryIndependentAvailabilityGroups()
    {
        var summary = AntigravityQuotaChecker.ParseQuotaSummary(
            Encoding.UTF8.GetBytes(SummaryFixture),
            Now);
        var legacy = AntigravityQuotaChecker.ParseQuota(
            Encoding.UTF8.GetBytes(
                """{"models":{"gemini-3-pro":{"quotaInfo":{"remainingFraction":0.8}},"claude-sonnet":{"quotaInfo":{"remainingFraction":0.5}}}}"""),
            Now);

        foreach (var quota in new[] { summary, legacy })
        {
            foreach (var limit in quota.Limits)
            {
                var pool = ProviderQuotaModelFilter.AntigravityModelPool(
                    limit.Window!.Replace('_', ' '));
                Assert.NotEmpty(pool);
                Assert.Equal(
                    ProviderQuotaModelFilter.AntigravityPoolAvailabilityGroup(pool),
                    limit.AvailabilityGroup);
                Assert.NotEqual("zhipu_accounts", limit.AvailabilityGroup);
                Assert.NotEqual("apertis_capacity", limit.AvailabilityGroup);
            }
        }

        Assert.NotEqual(
            ProviderQuotaModelFilter.AntigravityPoolAvailabilityGroup("gemini"),
            ProviderQuotaModelFilter.AntigravityPoolAvailabilityGroup("claude_gpt"));
    }

    [Theory]
    [InlineData("gemini")]
    [InlineData("claude_gpt")]
    public void ExhaustedPoolKeepsTheOtherPoolRoutable(string exhaustedPool)
    {
        var limits = new List<QuotaLimitStatus>
        {
            new()
            {
                Type = QuotaLimitTypes.Token,
                Status = ProviderQuotaStatuses.Available,
                UsageRatio = 0.08,
                Window = "gemini_7d",
                AvailabilityGroup = ProviderQuotaModelFilter.AntigravityPoolAvailabilityGroup("gemini"),
            },
            new()
            {
                Type = QuotaLimitTypes.Token,
                Status = ProviderQuotaStatuses.Exhausted,
                UsageRatio = 1,
                Window = "claude_gpt_7d",
                AvailabilityGroup = ProviderQuotaModelFilter.AntigravityPoolAvailabilityGroup("claude_gpt"),
            },
        };
        foreach (var limit in limits)
        {
            if (limit.Window!.StartsWith(exhaustedPool, StringComparison.Ordinal))
            {
                limit.Status = ProviderQuotaStatuses.Exhausted;
                limit.UsageRatio = 1;
            }
            else
            {
                limit.Status = ProviderQuotaStatuses.Available;
                limit.UsageRatio = 0.08;
            }
        }

        var (state, _) = ProviderQuotaRouting.EvaluateQuotaRouting(
            limits,
            ProviderQuotaStatuses.Available,
            QuotaLimitTypes.Token,
            Now);

        Assert.Equal(QuotaRoutingStates.Open, state);
    }

    [Fact]
    public void UngroupedPoolsStillCollapseForRouting()
    {
        var limits = new List<QuotaLimitStatus>
        {
            new()
            {
                Type = QuotaLimitTypes.Token,
                Status = ProviderQuotaStatuses.Available,
                UsageRatio = 0.08,
                Window = "gemini_7d",
            },
            new()
            {
                Type = QuotaLimitTypes.Token,
                Status = ProviderQuotaStatuses.Exhausted,
                UsageRatio = 1,
                Window = "claude_gpt_7d",
            },
        };

        var (state, _) = ProviderQuotaRouting.EvaluateQuotaRouting(
            limits,
            ProviderQuotaStatuses.Available,
            QuotaLimitTypes.Token,
            Now);

        Assert.Equal(QuotaRoutingStates.Exhausted, state);
    }

    // ------------------------------------------------------------------
    // Error messages and reset times
    // ------------------------------------------------------------------

    [Fact]
    public void ParseQuotaReportsTheReferenceErrorMessages()
    {
        Assert.Equal(
            "Antigravity quota response has no models",
            Assert.Throws<InvalidOperationException>(
                () => AntigravityQuotaChecker.ParseQuota(Encoding.UTF8.GetBytes("{}"), Now)).Message);

        Assert.Equal(
            "Antigravity quota response has no quota data",
            Assert.Throws<InvalidOperationException>(
                () => AntigravityQuotaChecker.ParseQuota(
                    Encoding.UTF8.GetBytes(
                        """{"models":{"internal":{"isInternal":true,"quotaInfo":{"remainingFraction":0}}}}"""),
                    Now)).Message);

        Assert.Equal(
            "Antigravity quota response has no recognized model pools",
            Assert.Throws<InvalidOperationException>(
                () => AntigravityQuotaChecker.ParseQuota(
                    Encoding.UTF8.GetBytes(
                        """{"models":{"mystery":{"quotaInfo":{"remainingFraction":0.5}}}}"""),
                    Now)).Message);

        Assert.StartsWith(
            "decode Antigravity quota response: ",
            Assert.Throws<InvalidOperationException>(
                () => AntigravityQuotaChecker.ParseQuota(Encoding.UTF8.GetBytes("{broken"), Now)).Message);

        Assert.StartsWith(
            "decode Antigravity quota summary: ",
            Assert.Throws<InvalidOperationException>(
                () => AntigravityQuotaChecker.ParseQuotaSummary(Encoding.UTF8.GetBytes("{broken"), Now)).Message);
    }

    [Fact]
    public void QuotaStatusMatchesTheWarningThreshold()
    {
        Assert.Equal(ProviderQuotaStatuses.Available, AntigravityQuotaChecker.QuotaStatus(0));
        Assert.Equal(ProviderQuotaStatuses.Available, AntigravityQuotaChecker.QuotaStatus(0.79));
        Assert.Equal(ProviderQuotaStatuses.Warning, AntigravityQuotaChecker.QuotaStatus(0.8));
        Assert.Equal(ProviderQuotaStatuses.Warning, AntigravityQuotaChecker.QuotaStatus(0.999));
        Assert.Equal(ProviderQuotaStatuses.Exhausted, AntigravityQuotaChecker.QuotaStatus(1));
        Assert.Equal(ProviderQuotaStatuses.Exhausted, AntigravityQuotaChecker.QuotaStatus(1.5));
    }

    [Fact]
    public void ParseResetTimeAcceptsEpochAndRfc3339Shapes()
    {
        var utc = new DateTimeOffset(2099, 9, 4, 8, 0, 0, TimeSpan.Zero);
        var epoch = DateTimeOffset.FromUnixTimeSeconds(4_102_444_800); // 2100-01-01T00:00:00Z

        Assert.Equal(utc, Reset("\"2099-09-04T08:00:00Z\""));
        Assert.Equal(utc, Reset("\"2099-09-04T08:00:00+00:00\""));

        var offset = Reset("\"2099-09-04T08:00:00+08:00\"");
        Assert.Equal(utc, offset);
        Assert.Equal(TimeSpan.FromHours(8), offset.GetValueOrDefault().Offset);

        Assert.Equal(utc.AddTicks(1_234_567), Reset("\"2099-09-04T08:00:00.123456789Z\""));
        Assert.Equal(utc.AddMilliseconds(500), Reset("\"2099-09-04T08:00:00,5Z\""));
        // Go's stdHour layout accepts a one-digit hour.
        Assert.Equal(utc, Reset("\"2099-09-04T8:00:00Z\""));

        Assert.Equal(epoch, Reset("\"4102444800\""));
        Assert.Equal(epoch, Reset("\"4102444800000\""));
        Assert.Equal(epoch, Reset("\"+4102444800\""));
        Assert.Equal(epoch, Reset("4102444800"));
        Assert.Equal(epoch, Reset("4102444800000"));
        Assert.Equal(epoch, Reset("4102444800000.9"));
        Assert.Equal(DateTimeOffset.UnixEpoch, Reset("0"));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(-1), Reset("\"-1\""));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"2099-9-04T08:00:00Z\"")] // month must be two digits
    [InlineData("\"2099-09-04 08:00:00Z\"")] // T is a literal
    [InlineData("\"2099-02-30T08:00:00Z\"")] // day out of range
    [InlineData("\"2099-13-01T08:00:00Z\"")]
    [InlineData("\"2099-09-04T24:00:00Z\"")]
    [InlineData("\"2099-09-04T08:60:00Z\"")]
    [InlineData("\"2099-09-04T08:00:60Z\"")]
    [InlineData("\"2099-09-04T08:00:00z\"")] // only uppercase Z is accepted
    [InlineData("\"0000-01-01T00:00:00Z\"")] // Go year 0 is not representable (C123)
    [InlineData("\"2099-09-04T08:00:00+15:00\"")] // DateTimeOffset caps offsets at 14h (C123)
    [InlineData("\"9999-12-31T23:59:59-14:00\"")] // UTC instant past DateTimeOffset's max (C123)
    [InlineData("\"99999999999999999999\"")] // ParseInt overflow is not RFC3339
    [InlineData("\" 4102444800\"")] // Go rejects surrounding whitespace
    [InlineData("\"4102444800 \"")]
    [InlineData("\"1_000\"")] // base-10 ParseInt rejects underscores
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void ParseResetTimeRejectsUnrepresentableValues(string json)
        => Assert.Null(Reset(json));

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static AntigravityQuotaChecker CreateChecker(out RecordingHttpHandler handler)
    {
        handler = new RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new AntigravityQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock());
    }

    private static ChannelRecord Channel(string credentials, string settings = "{}") => new()
    {
        Type = "antigravity",
        Credentials = credentials,
        Settings = settings,
    };

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body)
        => new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static DateTimeOffset? Reset(string json)
    {
        using var document = JsonDocument.Parse(json);
        return AntigravityQuotaChecker.ParseResetTime(document.RootElement);
    }

    private static List<string> FormValue(HttpRequestData request, string key)
        => HttpClientUtils.ParseQuery(Encoding.UTF8.GetString(request.Body ?? []))
            .TryGetValue(key, out var values)
            ? values
            : [];

    /// <summary>Records every outbound request the way Go's RoundTripper does.</summary>
    internal sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<RecordedHttpRequest> _requests = [];

        public Func<RecordedHttpRequest, HttpResponseMessage> OnSend { get; set; }
            = _ => JsonResponse(HttpStatusCode.OK, "{}");

        public IReadOnlyList<RecordedHttpRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return [.. _requests];
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, values) in request.Headers)
            {
                headers[name] = string.Join(", ", values);
            }

            var body = string.Empty;
            if (request.Content is not null)
            {
                foreach (var (name, values) in request.Content.Headers)
                {
                    headers[name] = string.Join(", ", values);
                }

                body = request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
            }

            var recorded = new RecordedHttpRequest(
                request.Method.Method,
                request.RequestUri?.ToString() ?? string.Empty,
                headers,
                body);
            lock (_gate)
            {
                _requests.Add(recorded);
            }

            return Task.FromResult(OnSend(recorded));
        }
    }

    /// <summary>The wire shape of one recorded request.</summary>
    internal sealed record RecordedHttpRequest(
        string Method,
        string Url,
        IReadOnlyDictionary<string, string> Headers,
        string Body)
    {
        public string Header(string name)
            => Headers.TryGetValue(name, out var value) ? value : string.Empty;

        public Dictionary<string, List<string>> Form()
            => HttpClientUtils.ParseQuery(Body);
    }
}

internal static class AntigravityQuotaLimitAssertions
{
    /// <summary>The period a windowed limit covers: next reset minus period start.</summary>
    public static TimeSpan ResetWindow(this QuotaLimitStatus limit)
    {
        var reset = limit.NextResetAt
            ?? throw new InvalidOperationException("limit has no next reset");
        var start = limit.PeriodStart
            ?? throw new InvalidOperationException("limit has no period start");
        return reset - start;
    }
}
