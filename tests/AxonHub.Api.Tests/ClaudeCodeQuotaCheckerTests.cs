using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports internal/server/biz/provider_quota/claudecode_checker.go together with
/// the Claude Code cases of period_window_test.go and the claudecode entry of
/// provider_contract_fixtures_test.go: the one-token request whose unified
/// ratelimit headers carry the quota, the credential precedence (OAuth object
/// over the legacy apiKey JSON), the status thresholds, the measured-window
/// rules and the raw header preservation. The wire shape (URL, auth, beta
/// headers, body) is locked through the real <see cref="HttpExecutor"/> over a
/// recording message handler.
/// </summary>
public sealed class ClaudeCodeQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private const string StatusHeader = "Anthropic-Ratelimit-Unified-Status";
    private const string ClaimHeader = "Anthropic-Ratelimit-Unified-Representative-Claim";
    private const string FiveHourUtilizationHeader = "Anthropic-Ratelimit-Unified-5h-Utilization";
    private const string FiveHourResetHeader = "Anthropic-Ratelimit-Unified-5h-Reset";
    private const string FiveHourStatusHeader = "Anthropic-Ratelimit-Unified-5h-Status";
    private const string SevenDayUtilizationHeader = "Anthropic-Ratelimit-Unified-7d-Utilization";
    private const string SevenDayResetHeader = "Anthropic-Ratelimit-Unified-7d-Reset";
    private const string SevenDayStatusHeader = "Anthropic-Ratelimit-Unified-7d-Status";
    private const string OverageUtilizationHeader = "Anthropic-Ratelimit-Unified-Overage-Utilization";
    private const string OverageResetHeader = "Anthropic-Ratelimit-Unified-Overage-Reset";
    private const string OverageStatusHeader = "Anthropic-Ratelimit-Unified-Overage-Status";

    private const string ValidCredentials = """{"oauth":{"access_token":"access-token"}}""";

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaSendsTheOneTokenHaikuRequestAndParsesTheUnifiedHeaders()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => QuotaResponse(FixtureHeaders());

        var quota = await checker.CheckQuotaAsync(Channel(ValidCredentials), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("https://api.anthropic.com/v1/messages", request.Url);
        Assert.Equal("Bearer access-token", request.Header("Authorization"));
        Assert.Equal(
            "claude-code-20250219,interleaved-thinking-2025-05-14,redact-thinking-2026-02-12,"
            + "context-management-2025-06-27,prompt-caching-scope-2026-01-05,"
            + "mid-conversation-system-2026-04-07,effort-2025-11-24",
            request.Header("anthropic-beta"));
        Assert.Equal("2023-06-01", request.Header("anthropic-version"));
        Assert.Equal("true", request.Header("anthropic-dangerous-direct-browser-access"));
        Assert.Equal("cli", request.Header("x-app"));
        Assert.Equal("application/json", request.Header("Content-Type"));
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Equal("axonhub/1.0", request.Header("User-Agent"));
        Assert.Equal(
            """{"max_tokens":1,"messages":[{"content":"limit","role":"user"}],"model":"claude-haiku-4-5"}""",
            request.Body);

        Assert.Equal("claudecode", quota.ProviderType);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(QuotaLimitTypes.Token, quota.Limits[0].Type);
        Assert.Equal(QuotaWindows.FiveHours, quota.Limits[0].Window);
        Assert.Equal(0.25, quota.Limits[0].UsageRatio, 12);
        Assert.Equal("available", quota.Limits[0].Status);
        Assert.Equal(TimeSpan.FromHours(5), quota.Limits[0].ResetWindow());
        Assert.Equal(QuotaWindows.SevenDays, quota.Limits[1].Window);
        Assert.Equal(0.50, quota.Limits[1].UsageRatio, 12);
        Assert.Equal(TimeSpan.FromDays(7), quota.Limits[1].ResetWindow());
        Assert.Equal(new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero), quota.NextResetAt);

        Assert.True(checker.SupportsChannel(Channel(ValidCredentials)));
        Assert.False(checker.SupportsChannel(new ChannelRecord { Type = "anthropic" }));
    }

    [Fact]
    public async Task CheckQuotaUsesTheConfiguredBaseUrl()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => QuotaResponse(FixtureHeaders());

        await checker.CheckQuotaAsync(
            Channel(ValidCredentials, baseUrl: "https://proxy.example/v1"),
            CancellationToken.None);

        Assert.Equal(
            "https://proxy.example/v1/messages",
            Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task CheckQuotaReadsTheAccessTokenFromTheOAuthObject()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => QuotaResponse(FixtureHeaders());

        await checker.CheckQuotaAsync(
            Channel("""{"oauth":{"access_token":"oauth-token"}}"""),
            CancellationToken.None);

        Assert.Equal("Bearer oauth-token", Assert.Single(handler.Requests).Header("Authorization"));
    }

    [Fact]
    public async Task CheckQuotaParsesTheLegacyOAuthJsonApiKey()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => QuotaResponse(FixtureHeaders());

        await checker.CheckQuotaAsync(
            Channel("""{"apiKey":"{\"access_token\":\"legacy-token\"}"}"""),
            CancellationToken.None);

        Assert.Equal("Bearer legacy-token", Assert.Single(handler.Requests).Header("Authorization"));
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"apiKey":"   "}""")]
    public async Task CheckQuotaFailsWithoutCredentials(string credentials)
    {
        var checker = CreateChecker(out var handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None));

        Assert.Equal("channel has no credentials", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CheckQuotaFailsWhenTheOAuthObjectHasNoAccessToken()
    {
        var checker = CreateChecker(out var handler);

        // The OAuth object wins even though the legacy apiKey is perfectly
        // usable; the checker never falls back once an oauth object is present.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(
                Channel("""{"apiKey":"{\"access_token\":\"legacy-token\"}","oauth":{}}"""),
                CancellationToken.None));

        Assert.Equal("channel credentials missing access token", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("not-json", "failed to parse OAuth credentials: ")]
    [InlineData("""{"refresh_token":"only-refresh"}""", "failed to parse OAuth credentials: access_token is empty")]
    public async Task CheckQuotaWrapsMalformedLegacyCredentials(string apiKey, string expectedPrefix)
    {
        var checker = CreateChecker(out var handler);
        var credentials = JsonSerializer.Serialize(new Dictionary<string, string> { ["apiKey"] = apiKey });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(credentials), CancellationToken.None));

        Assert.StartsWith(expectedPrefix, error.Message, StringComparison.Ordinal);
        Assert.NotNull(error.InnerException);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task CheckQuotaWrapsUpstreamHttpErrors()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            ReasonPhrase = "Internal Server Error",
            Content = new StringContent("boom", Encoding.UTF8, "text/plain"),
        };

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(ValidCredentials), CancellationToken.None));

        Assert.Equal(
            "HTTP request failed: POST - https://api.anthropic.com/v1/messages"
            + " with status 500 Internal Server Error",
            error.Message);
    }

    [Fact]
    public async Task CheckQuotaReportsNonOkStatusesBelowFourHundred()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => new HttpResponseMessage((HttpStatusCode)201)
        {
            ReasonPhrase = "Created",
            Content = new StringContent("created", Encoding.UTF8, "text/plain"),
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(ValidCredentials), CancellationToken.None));

        Assert.Equal("HTTP 201: created", error.Message);
    }

    [Fact]
    public void CheckQuotaRequiresTheUnifiedStatusHeader()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => ClaudeCodeQuotaChecker.ParseResponse(
                Headers((FiveHourUtilizationHeader, "0.5")),
                Now));
        Assert.Equal("missing quota headers", error.Message);

        var empty = Assert.Throws<InvalidOperationException>(
            () => ClaudeCodeQuotaChecker.ParseResponse(Headers((StatusHeader, string.Empty)), Now));
        Assert.Equal("missing quota headers", empty.Message);
    }

    [Fact]
    public async Task CheckQuotaUsesThePerChannelProxyInsteadOfTheSharedExecutor()
    {
        var baseExecutor = new ScriptedExecutor();
        var proxied = new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = 200,
                Headers = FixtureHeaders(),
                Body = Encoding.UTF8.GetBytes("{}"),
            },
        };
        var proxyFactory = new ScriptedProxyExecutorFactory { Executor = proxied };
        var checker = new ClaudeCodeQuotaChecker(
            baseExecutor,
            proxyFactory,
            new AntigravityTestClock(Now));

        const string settings = """
            {"proxy":{"type":"url","url":"http://proxy.local:8080","username":"user","password":"pass","disableConnectionReuse":true}}
            """;
        var quota = await checker.CheckQuotaAsync(
            Channel(ValidCredentials, settings),
            CancellationToken.None);

        Assert.Equal(2, quota.Limits.Count);
        var proxy = Assert.Single(proxyFactory.Requests);
        Assert.Equal(ProxyType.Url, proxy.Type);
        Assert.Equal("http://proxy.local:8080", proxy.Url);
        Assert.Equal("user", proxy.Username);
        Assert.Equal("pass", proxy.Password);
        Assert.True(proxy.DisableConnectionReuse);
        Assert.Equal(0, baseExecutor.Calls);
        var request = Assert.Single(proxied.Requests);
        Assert.Equal("https://api.anthropic.com/v1/messages", request.Url);
        Assert.Equal("access-token", request.Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaTreatsMalformedSettingsAsNoProxy()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = 200,
                Headers = FixtureHeaders(),
                Body = Encoding.UTF8.GetBytes("{}"),
            },
        };
        var proxyFactory = new ScriptedProxyExecutorFactory();
        var checker = new ClaudeCodeQuotaChecker(executor, proxyFactory, new AntigravityTestClock(Now));

        var quota = await checker.CheckQuotaAsync(
            Channel(ValidCredentials, "{broken"),
            CancellationToken.None);

        Assert.Equal(2, quota.Limits.Count);
        Assert.Empty(proxyFactory.Requests);
    }

    // ------------------------------------------------------------------
    // Header parsing
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("allowed", "0.10", "0.10", "available", "available", "available")]
    [InlineData("allowed", "0.80", "0.10", "warning", "warning", "available")]
    [InlineData("allowed", "0.79", "0.85", "warning", "available", "warning")]
    [InlineData("allowed", "1.0", "0.0", "warning", "exhausted", "available")]
    [InlineData("throttled", "0.10", "0.10", "exhausted", "available", "available")]
    [InlineData("rejected", "0.95", "0.10", "exhausted", "warning", "available")]
    [InlineData("unexpected", "0.10", "0.10", "available", "available", "available")]
    public void CheckQuotaNormalizesTheUnifiedStatus(
        string unifiedStatus,
        string fiveHourUtilization,
        string sevenDayUtilization,
        string expectedOverall,
        string expectedFiveHour,
        string expectedSevenDay)
    {
        var quota = ClaudeCodeQuotaChecker.ParseResponse(
            Headers(
                (StatusHeader, unifiedStatus),
                (FiveHourUtilizationHeader, fiveHourUtilization),
                (FiveHourStatusHeader, "allowed"),
                (SevenDayUtilizationHeader, sevenDayUtilization),
                (SevenDayStatusHeader, "allowed")),
            Now);

        Assert.Equal(expectedOverall, quota.Status);
        Assert.Equal(ProviderQuotaStatuses.IsReadyStatus(expectedOverall), quota.Ready);
        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(expectedFiveHour, quota.Limits[0].Status);
        Assert.Equal(expectedSevenDay, quota.Limits[1].Status);
    }

    [Theory]
    [InlineData("five-hour-status-only", 1, "5h")]
    [InlineData("seven-day-utilization-only", 1, "7d")]
    [InlineData("seven-day-reset-only", 1, "7d")]
    [InlineData("both-windows", 2, "5h")]
    [InlineData("overage-only", 0, "")]
    [InlineData("no-window-headers", 0, "")]
    public void CheckQuotaOnlyBuildsWindowsThatWereMeasured(
        string shape,
        int expectedCount,
        string expectedFirstWindow)
    {
        var headers = Headers((StatusHeader, "allowed"));
        switch (shape)
        {
            case "five-hour-status-only":
                headers[FiveHourStatusHeader] = ["allowed"];
                break;
            case "seven-day-utilization-only":
                headers[SevenDayUtilizationHeader] = ["0.5"];
                break;
            case "seven-day-reset-only":
                headers[SevenDayResetHeader] = ["4103049600"];
                break;
            case "both-windows":
                headers[FiveHourUtilizationHeader] = ["0.25"];
                headers[SevenDayUtilizationHeader] = ["0.5"];
                break;
            case "overage-only":
                headers[OverageUtilizationHeader] = ["0.9"];
                headers[OverageResetHeader] = ["4105123200"];
                headers[OverageStatusHeader] = ["allowed"];
                break;
        }

        var quota = ClaudeCodeQuotaChecker.ParseResponse(headers, Now);

        Assert.Equal(expectedCount, quota.Limits.Count);
        if (expectedCount > 0)
        {
            Assert.Equal(expectedFirstWindow, quota.Limits[0].Window);
        }
    }

    [Theory]
    [InlineData("five_hour", 4102444800L)]
    [InlineData("7d", 4102444800L)]
    [InlineData("seven_day", 4102444800L)] // claim reset loses to the earlier 5h limit
    [InlineData("overage", 4101000000L)] // the overage window is not a token limit
    [InlineData("unexpected", 4102444800L)]
    [InlineData("", 4102444800L)]
    public void CheckQuotaResolvesTheNextResetThroughTheClaimAndNormalization(
        string representativeClaim,
        long expectedResetUnix)
    {
        var headers = FixtureHeaders();
        headers[OverageResetHeader] = ["4101000000"];
        if (representativeClaim.Length == 0)
        {
            headers.Remove(ClaimHeader);
        }
        else
        {
            headers[ClaimHeader] = [representativeClaim];
        }

        var quota = ClaudeCodeQuotaChecker.ParseResponse(headers, Now);

        // The claim picks a window's reset, but the shared normalization keeps
        // the earliest live reset: "five_hour" and "seven_day" both resolve to
        // the 5h limit's reset, while the overage claim survives because the
        // overage bucket never becomes a limit. An unknown or absent claim
        // contributes nothing and the earliest limit reset wins.
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expectedResetUnix), quota.NextResetAt);
    }

    [Fact]
    public void CheckQuotaKeepsTheRawHeaderShape()
    {
        var headers = FixtureHeaders();
        headers["Anthropic-Ratelimit-Unified-Fallback"] = ["surge"];
        headers["Anthropic-Ratelimit-Unified-Fallback-Percentage"] = ["42.5"];
        headers["Anthropic-Ratelimit-Unified-Reset"] = ["4102444000"];

        var quota = ClaudeCodeQuotaChecker.ParseResponse(headers, Now);

        Assert.Equal("allowed", quota.RawData["unified_status"]);
        Assert.Equal("five_hour", quota.RawData["representative_claim"]);
        Assert.Equal("surge", quota.RawData["fallback"]);
        Assert.Equal(42.5, Assert.IsType<double>(quota.RawData["fallback_percentage"]));
        Assert.Equal(4102444000L, Assert.IsType<long>(quota.RawData["reset"]));

        var windows = Assert.IsType<Dictionary<string, object?>>(quota.RawData["windows"]);
        Assert.Equal(3, windows.Count);
        var fiveHour = Assert.IsType<Dictionary<string, object?>>(windows["5h"]);
        Assert.Equal("allowed", fiveHour["status"]);
        Assert.Equal(4102444800L, Assert.IsType<long>(fiveHour["reset"]));
        Assert.Equal(0.25, Assert.IsType<double>(fiveHour["utilization"]));
        var sevenDay = Assert.IsType<Dictionary<string, object?>>(windows["7d"]);
        Assert.Equal(0.5, Assert.IsType<double>(sevenDay["utilization"]));
        var overage = Assert.IsType<Dictionary<string, object?>>(windows["overage"]);
        Assert.Equal(string.Empty, overage["status"]);
        Assert.Equal(0L, Assert.IsType<long>(overage["reset"]));
        Assert.Equal(0.0, Assert.IsType<double>(overage["utilization"]));
    }

    [Fact]
    public void CheckQuotaLimitsCarryTheirPeriod()
    {
        var fiveHourReset = Now.AddHours(2);
        var sevenDayReset = Now.AddDays(3);
        var headers = Headers(
            (StatusHeader, "allowed"),
            (FiveHourUtilizationHeader, "0.5"),
            (FiveHourResetHeader, fiveHourReset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (SevenDayUtilizationHeader, "0.25"),
            (SevenDayResetHeader, sevenDayReset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var quota = ClaudeCodeQuotaChecker.ParseResponse(headers, Now);

        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(QuotaWindows.FiveHours, quota.Limits[0].Window);
        Assert.Equal(fiveHourReset, quota.Limits[0].NextResetAt);
        Assert.Equal(fiveHourReset.Add(-TimeSpan.FromHours(5)), quota.Limits[0].PeriodStart);
        Assert.Equal(QuotaWindows.SevenDays, quota.Limits[1].Window);
        Assert.Equal(sevenDayReset, quota.Limits[1].NextResetAt);
        Assert.Equal(sevenDayReset.Add(-TimeSpan.FromDays(7)), quota.Limits[1].PeriodStart);
    }

    [Fact]
    public void CheckQuotaHasNoPeriodWithoutAResetHeader()
    {
        var quota = ClaudeCodeQuotaChecker.ParseResponse(
            Headers(
                (StatusHeader, "allowed"),
                (FiveHourUtilizationHeader, "0.5")),
            Now);

        var limit = Assert.Single(quota.Limits);
        Assert.Null(limit.PeriodStart);
        Assert.Null(limit.NextResetAt);
        Assert.Null(quota.NextResetAt);
    }

    [Theory]
    [InlineData("4102444800", 4102444800L)]
    [InlineData("0", 0L)]
    [InlineData("+42", 42L)]
    [InlineData("-7", -7L)]
    [InlineData("", 0L)]
    [InlineData(" 42", 0L)]
    [InlineData("42 ", 0L)]
    [InlineData("4_2", 0L)]
    [InlineData("1.5", 0L)]
    [InlineData("0x10", 0L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("9223372036854775808", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    [InlineData("-9223372036854775809", long.MinValue)]
    [InlineData("99999999999999999999999", long.MaxValue)]
    [InlineData("-99999999999999999999999", long.MinValue)]
    [InlineData("+", 0L)]
    [InlineData("-", 0L)]
    public void ParseUnixTimestampMatchesTheReference(string value, long expected)
        => Assert.Equal(expected, ClaudeCodeQuotaChecker.ParseUnixTimestamp(value));

    [Theory]
    [InlineData("0.25", 0.25)]
    [InlineData("0", 0.0)]
    [InlineData("-0.5", -0.5)]
    [InlineData("1e-3", 0.001)]
    [InlineData("5.", 5.0)]
    [InlineData(".5", 0.5)]
    [InlineData("1_0", 10.0)]
    [InlineData("0x1p-2", 0.25)]
    [InlineData("abc", 0.0)]
    [InlineData("", 0.0)]
    [InlineData(" 0.5", 0.0)]
    [InlineData("0.5 ", 0.0)]
    [InlineData("Inf", double.PositiveInfinity)]
    [InlineData("-Inf", double.NegativeInfinity)]
    [InlineData("NaN", double.NaN)]
    [InlineData("1e400", double.PositiveInfinity)]
    [InlineData("-1e400", double.NegativeInfinity)]
    [InlineData("1e-400", 0.0)]
    public void ParseFloatMatchesTheReference(string value, double expected)
        => Assert.Equal(expected, ClaudeCodeQuotaChecker.ParseFloat(value));

    [Theory]
    [InlineData("", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://api.anthropic.com", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://api.anthropic.com/", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://api.anthropic.com/v1", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://api.anthropic.com/v1/", "https://api.anthropic.com/v1/messages")]
    [InlineData("https://proxy.test/", "https://proxy.test/v1/messages")]
    [InlineData("https://proxy.test//", "https://proxy.test//v1/messages")]
    [InlineData("https://proxy.test/v1/messages", "https://proxy.test/v1/messages/v1/messages")]
    public void GetEndpointUrlMatchesTheReference(string baseUrl, string expected)
        => Assert.Equal(expected, ClaudeCodeQuotaChecker.GetEndpointUrl(baseUrl));

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Dictionary<string, List<string>> FixtureHeaders() => Headers(
        (StatusHeader, "allowed"),
        (ClaimHeader, "five_hour"),
        (FiveHourUtilizationHeader, "0.25"),
        (FiveHourResetHeader, "4102444800"),
        (FiveHourStatusHeader, "allowed"),
        (SevenDayUtilizationHeader, "0.50"),
        (SevenDayResetHeader, "4103049600"),
        (SevenDayStatusHeader, "allowed"));

    private static Dictionary<string, List<string>> Headers(
        params (string Name, string Value)[] values)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, value) in values)
        {
            HttpClientUtils.HeaderSet(headers, name, value);
        }

        return headers;
    }

    private static ClaudeCodeQuotaChecker CreateChecker(out RecordingHttpHandler handler)
    {
        handler = new RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new ClaudeCodeQuotaChecker(executor, new ScriptedProxyExecutorFactory(), new AntigravityTestClock(Now));
    }

    private static ChannelRecord Channel(
        string credentials,
        string settings = "{}",
        string? baseUrl = null) => new()
    {
        Type = "claudecode",
        BaseUrl = baseUrl,
        Credentials = credentials,
        Settings = settings,
    };

    private static HttpResponseMessage QuotaResponse(Dictionary<string, List<string>> headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
        };
        foreach (var (name, values) in headers)
        {
            _ = response.Headers.TryAddWithoutValidation(name, values);
        }

        return response;
    }

    /// <summary>Records every outbound request the way Go's RoundTripper does.</summary>
    internal sealed class RecordingHttpHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<RecordedHttpRequest> _requests = [];

        public Func<RecordedHttpRequest, HttpResponseMessage> OnSend { get; set; }
            = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };

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
    }
}
