using System.Globalization;
using System.Net;
using System.Text;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports internal/server/biz/provider_quota/zai_checker.go together with
/// zai_checker_test.go: the fixed api.z.ai host, the shared ZhiPu-family
/// collector under the "zai" provider type, and the zai / zai_anthropic
/// channel gate. The collector's per-key and Go-decoding details stay locked by
/// ZhipuQuotaCheckerTests.
/// </summary>
public sealed class ZaiQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

    private const string DefaultQuotaUrl = "https://api.z.ai/api/monitor/usage/quota/limit";

    private const string DefaultCredentials = """{"apiKey":"zai-key"}""";

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

    /// <summary>TestZai_CheckQuota_HappyPath (zai_checker_test.go:18-64).</summary>
    [Fact]
    public async Task HappyPathUsesTheFixedHostAndFamilyPayload()
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
                credentials: """{"apiKey":"zai-key"}""",
                baseUrl: "https://api.z.ai/api/paas/v4"),
            CancellationToken.None);

        // The channel base URL never matters: the Z.ai quota endpoint is fixed,
        // and the shared family collector sends the key verbatim.
        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(DefaultQuotaUrl, request.Url);
        Assert.Equal("zai-key", request.Header("Authorization"));
        Assert.Equal("application/json", request.Header("Content-Type"));
        Assert.Equal("en-US,en", request.Header("Accept-Language"));
        Assert.Equal(string.Empty, request.Body);

        Assert.Equal("available", quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal("zai", quota.ProviderType);
        Assert.Equal(2, quota.Limits.Count);

        Assert.Equal("5h", quota.Limits[0].Window);
        Assert.Equal(0.255, quota.Limits[0].UsageRatio, 3);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1784322000000), quota.Limits[0].NextResetAt);
        Assert.Equal("weekly", quota.Limits[1].Window);
        Assert.Equal(0.10, quota.Limits[1].UsageRatio, 3);

        var rows = Assert.IsType<List<ZhipuWindowRow>>(quota.RawData["rows"]);
        Assert.Equal(2, rows.Count);
        Assert.Equal("five_hour", rows[0].Window);
        Assert.Equal("weekly_limit", rows[1].Window);
        Assert.Equal("standard", quota.RawData["level"]);

        var accounts = Assert.IsType<List<ZhipuAccountQuota>>(quota.RawData["accounts"]);
        var account = Assert.Single(accounts);
        Assert.Equal("-key", account.Suffix);
        Assert.False(account.Disabled);
        Assert.Equal("available", account.Status);
        Assert.Equal("standard", account.Level);
    }

    /// <summary>TestZai_CheckQuota_AnthropicChannelType (zai_checker_test.go:66-89).</summary>
    [Fact]
    public async Task AnthropicChannelTypeUsesTheSameEndpoint()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(
            """{"success":true,"code":200,"data":{"level":"basic","limits":[{"type":"TOKENS_LIMIT","percentage":5.0}]}}""");

        var quota = await checker.CheckQuotaAsync(
            Channel(
                type: "zai_anthropic",
                baseUrl: "https://api.z.ai/api/anthropic"),
            CancellationToken.None);

        Assert.Equal(DefaultQuotaUrl, Assert.Single(handler.Requests).Url);
        Assert.Equal("zai", quota.ProviderType);
        Assert.Single(quota.Limits);
        Assert.Equal("5h", quota.Limits[0].Window);
    }

    [Theory]
    [InlineData("https://api.z.ai/api/paas/v4")]
    [InlineData("https://api.z.ai/api/anthropic")]
    [InlineData("https://open.bigmodel.cn/api/paas/v4")]
    [InlineData("")]
    [InlineData("not a URL")]
    [InlineData(null)]
    public async Task BaseUrlDoesNotMoveTheEndpoint(string? baseUrl)
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(SuccessBody);

        var quota = await checker.CheckQuotaAsync(
            Channel(baseUrl: baseUrl),
            CancellationToken.None);

        Assert.Equal(DefaultQuotaUrl, Assert.Single(handler.Requests).Url);
        Assert.Equal("zai", quota.ProviderType);
    }

    /// <summary>TestZai_CheckQuota_APIError (zai_checker_test.go:91-110).</summary>
    [Fact]
    public async Task ApiErrorKeepsTheProviderMessage()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(
            """{"success":false,"code":1001,"msg":"Authentication parameter not received in Header, unable to authenticate"}""");

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        // The collector's all-keys-failed prefix is family-wide (it says
        // "zhipu" even for the zai provider), matching the reference.
        Assert.Contains(
            "zhipu quota check failed for all 1 keys: "
            + "zhipu API error: Authentication parameter not received in Header, unable to authenticate",
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>TestZai_CheckQuota_MissingAPIKey (zai_checker_test.go:112-116).</summary>
    [Fact]
    public async Task MissingApiKeyFailsBeforeAnyRequest()
    {
        var checker = CreateChecker(out var handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(
                Channel(credentials: "{}"),
                CancellationToken.None));

        Assert.Equal("channel has no API key", error.Message);
        Assert.Empty(handler.Requests);
    }

    /// <summary>TestZai_SupportsChannel (zai_checker_test.go:118-126).</summary>
    [Theory]
    [InlineData("zai", true)]
    [InlineData("zai_anthropic", true)]
    [InlineData("zhipu", false)]
    [InlineData("zhipu_anthropic", false)]
    [InlineData("openai", false)]
    public void SupportsChannelOnlyMatchesTheZaiTypes(string channelType, bool expected)
    {
        var checker = CreateChecker(new ScriptedExecutor());

        Assert.Equal(expected, checker.SupportsChannel(new ChannelRecord { Type = channelType }));
    }

    // ------------------------------------------------------------------
    // Shared collector through the zai specialization
    // ------------------------------------------------------------------

    [Fact]
    public async Task AllKeysFailedKeepsTheFamilyPrefix()
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

    [Fact]
    public async Task MultipleKeysKeepTheFamilyAvailabilityGroup()
    {
        var executor = KeyedExecutor(new Dictionary<string, string>
        {
            ["zai-a"] = """{"success":true,"code":200,"data":{"level":"standard","limits":[{"type":"TOKENS_LIMIT","percentage":5}]}}""",
            ["zai-b"] = """{"success":true,"code":200,"data":{"level":"standard","limits":[{"type":"TOKENS_LIMIT","percentage":100}]}}""",
        });
        var checker = CreateChecker(executor);

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKeys":["zai-a","zai-b"]}"""),
            CancellationToken.None);

        // The exhausted account is skipped by the family aggregation, while
        // both accounts feed the shared availability group (OR semantics).
        Assert.Equal("zai", quota.ProviderType);
        Assert.Equal("available", quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);
        Assert.All(
            quota.Limits,
            limit => Assert.Equal("zhipu_accounts", limit.AvailabilityGroup));
        Assert.Equal(new[] { 0.05, 1.0 }, quota.Limits.Select(limit => limit.UsageRatio));

        var accounts = Assert.IsType<List<ZhipuAccountQuota>>(quota.RawData["accounts"]);
        Assert.Equal(2, accounts.Count);
    }

    [Fact]
    public async Task DisabledKeysKeepTheFamilyTruncationWarning()
    {
        var keys = new[] { "live-key" }
            .Concat(Enumerable.Range(0, 40).Select(index => "dead-" + index.ToString(CultureInfo.InvariantCulture)))
            .ToArray();
        var credentials = "{\"apiKeys\":["
            + string.Join(",", keys.Select(key => "\"" + key + "\""))
            + "]}";
        var disabledApiKeys = "["
            + string.Join(",", keys.Skip(1).Select(key => DisabledEntry(key)))
            + "]";
        var executor = KeyedExecutor(new Dictionary<string, string>
        {
            ["live-key"] = SuccessBody,
        });
        var logger = new RecordingLogger<ZhipuQuotaChecker>();
        var checker = CreateChecker(executor, logger);

        var quota = await checker.CheckQuotaAsync(
            Channel(
                credentials: credentials,
                disabledApiKeys: disabledApiKeys,
                id: 42),
            CancellationToken.None);

        // 1 serving key + the 32 bounded parked keys.
        Assert.Equal(33, executor.Requests.Count);
        Assert.Equal("zai", quota.ProviderType);

        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal(
            "zhipu quota check truncates disabled keys: channel_id=42 disabled_keys=40 checked=32",
            warning.Message);
    }

    [Fact]
    public void ParseZaiQuotaResponseDelegatesToTheFamilyParser()
    {
        var quota = ZaiQuotaChecker.ParseZaiQuotaResponse(
            Encoding.UTF8.GetBytes("""
                {
                    "success": true,
                    "code": 200,
                    "data": {
                        "level": "standard",
                        "limits": [
                            {"type": "TOKENS_LIMIT", "percentage": 25.5, "nextResetTime": 1784322000000}
                        ]
                    }
                }
                """),
            Now);

        Assert.Equal("zai", quota.ProviderType);
        Assert.Equal("available", quota.Status);
        Assert.True(quota.Ready);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal("5h", limit.Window);
        Assert.Equal(0.255, limit.UsageRatio, 3);
        Assert.Equal("standard", quota.RawData["level"]);
    }

    [Fact]
    public void ParseZaiQuotaResponseWrapsDecodeErrorsWithTheFamilyPrefix()
    {
        var error = Assert.Throws<WrappedException>(
            () => ZaiQuotaChecker.ParseZaiQuotaResponse(
                Encoding.UTF8.GetBytes("{bad"),
                Now));

        Assert.StartsWith(
            "failed to parse zhipu quota response: ",
            error.Message,
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Executor seams
    // ------------------------------------------------------------------

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

    [Fact]
    public async Task CheckQuotaRoutesThroughTheFamilyProxyExecutor()
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
        Assert.Equal("http://proxy.local:8080", proxy.Url);
        Assert.Single(proxied.Requests);
        Assert.Empty(direct.Requests);
        Assert.Equal(DefaultQuotaUrl, proxied.Requests[0].Url);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ZaiQuotaChecker CreateChecker(
        out ClaudeCodeQuotaCheckerTests.RecordingHttpHandler handler,
        ILogger<ZhipuQuotaChecker>? logger = null)
    {
        handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new ZaiQuotaChecker(new ZhipuQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now),
            logger ?? NullLogger<ZhipuQuotaChecker>.Instance));
    }

    private static ZaiQuotaChecker CreateChecker(
        ScriptedExecutor executor,
        ILogger<ZhipuQuotaChecker>? logger = null,
        ScriptedProxyExecutorFactory? factory = null)
        => new(new ZhipuQuotaChecker(
            executor,
            factory ?? new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now),
            logger ?? NullLogger<ZhipuQuotaChecker>.Instance));

    private static ChannelRecord Channel(
        string? credentials = null,
        string settings = "{}",
        string type = "zai",
        string? baseUrl = "https://api.z.ai/api/paas/v4",
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

    private static string DisabledEntry(string key)
        => "{\"key\":\"" + key
            + "\",\"disabledAt\":\"2026-07-17T00:00:00Z\",\"errorCode\":429"
            + ",\"reason\":\"rate limited\"}";

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
