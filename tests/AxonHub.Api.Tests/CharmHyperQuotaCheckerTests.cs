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
/// Ports internal/server/biz/provider_quota/charm_hyper_checker.go together
/// with charm_hyper_checker_test.go: the independent /v1/credits balance
/// checker (it does not share the ZhiPu-family collector), its 100-credit
/// baseline thresholds, the host-detection gate and the Go net/url rebuild of
/// the base URL.
/// </summary>
public sealed class CharmHyperQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

    private const string DefaultQuotaUrl = "https://hyper.charm.land/v1/credits";

    private const string ApiKey = "test-api-key";

    private const string DefaultCredentials = """{"apiKey":"test-api-key"}""";

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    /// <summary>TestCharmHyper_CheckQuota_HappyPath (charm_hyper_checker_test.go:19-48).</summary>
    [Fact]
    public async Task HappyPathUsesTheDefaultHostAndCreditsPayload()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse("""{"balance": 80}""");

        var quota = await checker.CheckQuotaAsync(
            Channel(baseUrl: "https://hyper.charm.land"),
            CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(DefaultQuotaUrl, request.Url);
        Assert.Equal("Bearer " + ApiKey, request.Header("Authorization"));
        Assert.Equal("application/json", request.Header("Accept"));
        // A bodyless GET: no content headers go on the wire.
        Assert.Equal(string.Empty, request.Header("Content-Type"));
        Assert.Equal(string.Empty, request.Body);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal("charm_hyper", quota.ProviderType);
        Assert.Null(quota.NextResetAt);

        // One credits-window token limit; the 100-credit baseline turns an
        // 80-credit balance into a 20% usage ratio.
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaLimitTypes.Token, limit.Type);
        Assert.Equal("credits", limit.Window);
        Assert.Equal(ProviderQuotaStatuses.Available, limit.Status);
        Assert.Equal(0.2, limit.UsageRatio, 12);
        Assert.True(limit.Ready);
        Assert.Null(limit.NextResetAt);
        Assert.Null(limit.PeriodStart);
        Assert.Null(limit.AvailabilityGroup);

        Assert.Equal("""{"balance":80}""", GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task CheckQuotaCarriesOnlyTheAcceptHeaderOnTheRequestData()
    {
        var executor = OkExecutor("""{"balance": 80}""");
        var checker = CreateChecker(executor);

        await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var request = Assert.Single(executor.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(DefaultQuotaUrl, request.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Accept"));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Null(request.Body);
        Assert.NotNull(request.Auth);
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal(ApiKey, request.Auth.ApiKey);
    }

    [Theory]
    [InlineData(null, "https://hyper.charm.land/v1/credits")]
    [InlineData("", "https://hyper.charm.land/v1/credits")]
    [InlineData("https://custom.charm.land", "https://custom.charm.land/v1/credits")]
    [InlineData("https://custom.charm.land/v1/chat?x=1", "https://custom.charm.land/v1/credits")]
    [InlineData("http://localhost:8080/v1", "http://localhost:8080/v1/credits")]
    public async Task CheckQuotaDerivesTheEndpointFromTheBaseUrl(string? baseUrl, string expectedUrl)
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse("""{"balance": 50}""");

        await checker.CheckQuotaAsync(Channel(baseUrl: baseUrl), CancellationToken.None);

        Assert.Equal(expectedUrl, Assert.Single(handler.Requests).Url);
    }

    /// <summary>TestCharmHyper_CheckQuota_CustomBaseURL (charm_hyper_checker_test.go:196-219).</summary>
    [Fact]
    public async Task CustomBaseUrlDrivesTheHost()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse("""{"balance": 50}""");

        var quota = await checker.CheckQuotaAsync(
            Channel(baseUrl: "https://custom.charm.land"),
            CancellationToken.None);

        Assert.Equal("https://custom.charm.land/v1/credits", Assert.Single(handler.Requests).Url);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal("charm_hyper", quota.ProviderType);
    }

    // ------------------------------------------------------------------
    // Status classification
    // ------------------------------------------------------------------

    /// <summary>TestCharmHyper_CheckQuota_WarningState (charm_hyper_checker_test.go:50-74).</summary>
    [Fact]
    public async Task WarningStateMapsTheBalanceRatio()
    {
        var checker = CreateChecker(OkExecutor("""{"balance": 15}"""));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(0.85, Assert.Single(quota.Limits).UsageRatio, 12);
        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Limits[0].Status);
    }

    /// <summary>TestCharmHyper_CheckQuota_ExhaustedState (charm_hyper_checker_test.go:76-100).</summary>
    [Fact]
    public async Task ExhaustedStateAtZeroBalance()
    {
        var checker = CreateChecker(OkExecutor("""{"balance": 0}"""));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        Assert.False(quota.Limits[0].Ready);
        Assert.Equal(1.0, quota.Limits[0].UsageRatio);
    }

    /// <summary>TestCharmHyper_CheckQuota_WarningBoundary (charm_hyper_checker_test.go:276-299).</summary>
    [Fact]
    public async Task WarningBoundaryAt20()
    {
        var checker = CreateChecker(OkExecutor("""{"balance": 20}"""));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(0.8, quota.Limits[0].UsageRatio, 12);
    }

    /// <summary>
    /// computeStatus (charm_hyper_checker.go:96-109), before normalization: the
    /// ratio is clamped at zero for balances above the baseline, and a negative
    /// balance keeps the warning status with a ratio above one.
    /// </summary>
    [Theory]
    [InlineData(80.0, "available", 0.2)]
    [InlineData(100.0, "available", 0.0)]
    [InlineData(150.0, "available", 0.0)]
    [InlineData(20.000001, "available", 0.79999999)]
    [InlineData(1.0, "warning", 0.99)]
    [InlineData(-5.0, "warning", 1.05)]
    public void ComputeStatusKeepsTheReferenceBoundaries(
        double balance,
        string expectedStatus,
        double expectedRatio)
    {
        var (status, ready, usageRatio) = CharmHyperQuotaChecker.ComputeStatus(balance);

        Assert.Equal(expectedStatus, status);
        Assert.True(ready);
        Assert.Equal(expectedRatio, usageRatio, 9);
    }

    [Fact]
    public async Task NegativeBalanceIsClampedAndTheLimitIsExhausted()
    {
        var checker = CreateChecker(OkExecutor("""{"balance": -5}"""));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        // computeStatus returns warning with 1.05; normalization clamps the
        // limit to 1 and marks that limit exhausted, but the explicit overall
        // status (warning, ready) wins over the exhausted summary.
        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(1.0, limit.UsageRatio);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);
        Assert.False(limit.Ready);
        Assert.Equal("""{"balance":-5}""", GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task UnderflowedBalanceDecodesToZeroAndIsExhausted()
    {
        var checker = CreateChecker(OkExecutor("""{"balance": 1e-400}"""));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        // Go's ParseFloat underflows to zero without a range error.
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal("""{"balance":0}""", GoJson.MarshalToString(quota.RawData));
    }

    // ------------------------------------------------------------------
    // Credentials
    // ------------------------------------------------------------------

    /// <summary>TestCharmHyper_CheckQuota_MissingCredentials (charm_hyper_checker_test.go:102-110).</summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"apiKey":"   "}""")]
    [InlineData("""{"apiKeys":["   "]}""")]
    public async Task MissingCredentialsFailBeforeAnyRequest(string credentials)
    {
        var executor = OkExecutor();
        var checker = CreateChecker(executor);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(credentials: credentials), CancellationToken.None));

        Assert.Equal("missing API key for Charm Hyper channel", error.Message);
        Assert.Empty(executor.Requests);
    }

    /// <summary>
    /// TestCharmHyper_CheckQuota_FallbackKey (charm_hyper_checker_test.go:221-243)
    /// plus the precedence rules at charm_hyper_checker.go:118-127: the legacy
    /// apiKey wins when non-blank, and only the first apiKeys entry is trimmed
    /// and used.
    /// </summary>
    [Theory]
    [InlineData("""{"apiKey":"  test-api-key  "}""", "Bearer test-api-key")]
    [InlineData("""{"apiKey":"   ","apiKeys":["  fallback-key  "]}""", "Bearer fallback-key")]
    [InlineData("""{"apiKeys":["first-key","second-key"]}""", "Bearer first-key")]
    public async Task ApiKeyPrecedenceMatchesTheReference(string credentials, string expectedAuthorization)
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse("""{"balance": 50}""");

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: credentials),
            CancellationToken.None);

        Assert.Equal(expectedAuthorization, Assert.Single(handler.Requests).Header("Authorization"));
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
    }

    // ------------------------------------------------------------------
    // Response decoding
    // ------------------------------------------------------------------

    /// <summary>TestCharmHyper_CheckQuota_MalformedJSON (charm_hyper_checker_test.go:112-132).</summary>
    [Fact]
    public async Task MalformedJsonIsWrappedWithTheParsePrefix()
    {
        var checker = CreateChecker(OkExecutor("{invalid json}"));

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.StartsWith("parsing Charm Hyper quota response: ", error.Message, StringComparison.Ordinal);
        // Malformed JSON raises JsonReaderException, a JsonException subtype.
        Assert.IsAssignableFrom<JsonException>(error.InnerException);
    }

    /// <summary>
    /// TestCharmHyper_CheckQuota_MissingBalanceField (charm_hyper_checker_test.go:134-152)
    /// and TestCharmHyper_CheckQuota_NullBalance (:154-172); a JSON null root is
    /// a no-op for a struct, so it also leaves the balance unset.
    /// </summary>
    [Theory]
    [InlineData("{}")]
    [InlineData("""{"balance": null}""")]
    [InlineData("null")]
    public async Task MissingBalanceFailsAfterDecoding(string body)
    {
        var checker = CreateChecker(OkExecutor(body));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("Charm Hyper quota response missing balance field", error.Message);
    }

    /// <summary>
    /// Go's decoder wording for a wrong JSON type on a *float64 field and for a
    /// non-object root, wrapped by the parse error prefix (C003).
    /// </summary>
    [Theory]
    [InlineData(
        """{"balance":"80"}""",
        "json: cannot unmarshal string into Go struct field CharmHyperCreditsResponse.balance of type float64")]
    [InlineData(
        """{"balance":true}""",
        "json: cannot unmarshal bool into Go struct field CharmHyperCreditsResponse.balance of type float64")]
    [InlineData(
        """{"balance":{}}""",
        "json: cannot unmarshal object into Go struct field CharmHyperCreditsResponse.balance of type float64")]
    [InlineData(
        """{"balance":[]}""",
        "json: cannot unmarshal array into Go struct field CharmHyperCreditsResponse.balance of type float64")]
    [InlineData(
        """{"balance":8e999}""",
        "json: cannot unmarshal number 8e999 into Go struct field CharmHyperCreditsResponse.balance of type float64")]
    [InlineData(
        "[1]",
        "json: cannot unmarshal array into Go value of type provider_quota.CharmHyperCreditsResponse")]
    [InlineData(
        "\"x\"",
        "json: cannot unmarshal string into Go value of type provider_quota.CharmHyperCreditsResponse")]
    public async Task BalanceDecodeErrorsUseGoWording(string body, string expectedInner)
    {
        var checker = CreateChecker(OkExecutor(body));

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("parsing Charm Hyper quota response: " + expectedInner, error.Message);
        Assert.IsType<JsonException>(error.InnerException);
    }

    [Theory]
    [InlineData("""{"balance": 80, "balance": 15}""", "warning", 0.85)]
    [InlineData("""{"balance": null, "balance": 80}""", "available", 0.2)]
    public async Task DuplicateKeysLetTheLastValueWin(
        string body,
        string expectedStatus,
        double expectedRatio)
    {
        var checker = CreateChecker(OkExecutor(body));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(expectedStatus, quota.Status);
        Assert.Equal(expectedRatio, quota.Limits[0].UsageRatio, 12);
    }

    [Fact]
    public async Task ADuplicateNullBalanceResetsTheEarlierValue()
    {
        var checker = CreateChecker(OkExecutor("""{"balance": 80, "balance": null}"""));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("Charm Hyper quota response missing balance field", error.Message);
    }

    [Fact]
    public async Task CaseInsensitiveFieldNameAndUnknownFieldsAreAccepted()
    {
        var checker = CreateChecker(OkExecutor("""{"Balance": 80, "extra": true}"""));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal(0.2, quota.Limits[0].UsageRatio, 12);
    }

    // ------------------------------------------------------------------
    // URL rebuild (buildCharmHyperQuotaURL, charm_hyper_checker.go:129-140)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null, "/v1/credits")]
    [InlineData("", "/v1/credits")]
    [InlineData("   ", "/v1/credits")]
    [InlineData("https://hyper.charm.land", "https://hyper.charm.land/v1/credits")]
    [InlineData("https://hyper.charm.land/", "https://hyper.charm.land/v1/credits")]
    [InlineData(
        "https://hyper.charm.land/api/v1?x=1#frag",
        "https://hyper.charm.land/v1/credits")]
    [InlineData(
        "https://user:pass@custom.charm.land/x",
        "https://custom.charm.land/v1/credits")]
    [InlineData(
        "http://custom.charm.land:8080/x",
        "http://custom.charm.land:8080/v1/credits")]
    [InlineData("https://host/x?y=1", "https://host/v1/credits")]
    [InlineData("not a URL", "/v1/credits")]
    [InlineData("https://host/x?", "https://host/v1/credits?")]
    [InlineData("https:opaque", "https:opaque")]
    [InlineData("https://host/%zz", "https://hyper.charm.land/v1/credits")]
    public void BuildQuotaUrlFollowsGoUrlSemantics(string? baseUrl, string expected)
    {
        Assert.Equal(expected, CharmHyperQuotaChecker.BuildQuotaUrl(baseUrl));
    }

    // ------------------------------------------------------------------
    // Executor seams
    // ------------------------------------------------------------------

    /// <summary>TestCharmHyper_CheckQuota_HTTPError (charm_hyper_checker_test.go:174-194).</summary>
    [Fact]
    public async Task HttpErrorKeepsTheExecutorStatusWording()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new HttpClientError(
                "GET",
                DefaultQuotaUrl,
                401,
                "401 Unauthorized",
                Encoding.UTF8.GetBytes("""{"error": "unauthorized"}""")),
        });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        // The reference's httpclient already reports >=400 as an error, so the
        // checker wraps it instead of hitting the explicit status branch.
        Assert.Equal(
            "executing Charm Hyper quota request: GET - " + DefaultQuotaUrl
            + " with status 401 Unauthorized",
            error.Message);
        Assert.IsType<HttpClientError>(error.InnerException);
    }

    [Theory]
    [InlineData(201, """{"balance": 80}""")]
    [InlineData(204, "")]
    [InlineData(302, "redirect")]
    public async Task Non200Sub400StatusesUseTheProviderMessage(int statusCode, string body)
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

        Assert.Equal(
            "Charm Hyper quota API returned status " + statusCode + ": " + body,
            error.Message);
    }

    [Fact]
    public async Task TransportFailureIsWrapped()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new InvalidOperationException("boom"),
        });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("executing Charm Hyper quota request: boom", error.Message);
    }

    /// <summary>TestCharmHyper_CheckQuota_WithProxy (charm_hyper_checker_test.go:245-274).</summary>
    [Fact]
    public async Task ProxySettingsRouteThroughTheProxyExecutor()
    {
        var direct = OkExecutor();
        var proxied = OkExecutor("""{"balance": 50}""");
        var factory = new ScriptedProxyExecutorFactory { Executor = proxied };
        var checker = CreateChecker(direct, factory);

        var quota = await checker.CheckQuotaAsync(
            Channel(
                settings: """{"proxy":{"type":"url","url":"http://proxy.local:8080","username":"user","password":"pass","disableConnectionReuse":true}}"""),
            CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal("charm_hyper", quota.ProviderType);
        var proxy = Assert.Single(factory.Requests);
        Assert.Equal("http://proxy.local:8080", proxy.Url);
        Assert.Single(proxied.Requests);
        Assert.Empty(direct.Requests);
        Assert.Equal(DefaultQuotaUrl, proxied.Requests[0].Url);
    }

    // ------------------------------------------------------------------
    // Channel gate
    // ------------------------------------------------------------------

    /// <summary>
    /// TestCharmHyper_SupportsChannel (charm_hyper_checker_test.go:301-331):
    /// openai / openai_responses channels whose host is hyper.charm.land or a
    /// subdomain; the port reuses DetectProviderFromURL, which matches host
    /// suffixes at a dot boundary.
    /// </summary>
    [Theory]
    [InlineData("openai", "https://hyper.charm.land", true)]
    [InlineData("openai_responses", "https://custom.hyper.charm.land", true)]
    [InlineData("openai", "https://hyper.charm.land:8443/v1", true)]
    [InlineData("openai", "https://HYPER.CHARM.LAND", true)]
    [InlineData("anthropic", "https://hyper.charm.land", false)]
    [InlineData("openai", "https://api.openai.com", false)]
    [InlineData("openai", "https://not-hyper.charm.land", false)]
    [InlineData("openai", null, false)]
    public void SupportsChannelDetectsTheHost(string channelType, string? baseUrl, bool expected)
    {
        var checker = CreateChecker(OkExecutor());

        Assert.Equal(
            expected,
            checker.SupportsChannel(new ChannelRecord
            {
                Type = channelType,
                BaseUrl = baseUrl,
            }));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static CharmHyperQuotaChecker CreateChecker(
        out ClaudeCodeQuotaCheckerTests.RecordingHttpHandler handler)
    {
        handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new CharmHyperQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now));
    }

    private static CharmHyperQuotaChecker CreateChecker(
        ScriptedExecutor executor,
        ScriptedProxyExecutorFactory? factory = null)
        => new(
            executor,
            factory ?? new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now));

    private static ChannelRecord Channel(
        string? credentials = null,
        string? baseUrl = "https://hyper.charm.land",
        string settings = "{}",
        string type = "openai")
        => new()
        {
            Type = type,
            BaseUrl = baseUrl,
            Credentials = credentials ?? DefaultCredentials,
            Settings = settings,
        };

    private static ScriptedExecutor OkExecutor(string body = """{"balance": 50}""") => new()
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
