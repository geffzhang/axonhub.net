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
/// Ports internal/server/biz/provider_quota/opencode_go_checker.go together with
/// opencode_go_checker_test.go: the fixed usage endpoint, the first-non-blank
/// API key selection (legacy key first, OAuth excluded), the rolling / weekly /
/// monthly percent windows, the resetsAt decoder (RFC3339 string, unix seconds
/// or epoch milliseconds) and the token limits derived from each window. The
/// wire shape is locked through the real <see cref="HttpExecutor"/> over a
/// recording handler; numeric reset instants render in UTC while Go renders
/// them in the process timezone, the accepted difference C123 / C212.
/// </summary>
public sealed class OpenCodeGoQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2099, 6, 25, 10, 0, 0, TimeSpan.Zero);

    private const string UsageUrl = "https://opencode.ai/zen/go/v1/usage";

    private const string DefaultCredentials = """{"apiKey":"test-api-key"}""";

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaFetchesTheUsageEndpointAndMapsAllThreeWindows()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(
            """
            {"usage":{
                "rolling":{"percent":12,"resetsAt":"2099-06-25T15:00:00Z"},
                "weekly":{"percent":85,"resetsAt":"2099-07-02T10:00:00Z"},
                "monthly":{"percent":35,"resetsAt":"2099-07-25T10:00:00Z"}
            }}
            """);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(UsageUrl, request.Url);
        Assert.Equal("Bearer test-api-key", request.Header("Authorization"));
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Equal(string.Empty, request.Body);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal("opencode_go", quota.ProviderType);
        Assert.Equal(new DateTimeOffset(2099, 6, 25, 15, 0, 0, TimeSpan.Zero), quota.NextResetAt);

        Assert.Equal(3, quota.Limits.Count);

        var rolling = quota.Limits[0];
        Assert.Equal(QuotaLimitTypes.Token, rolling.Type);
        Assert.Equal("5h", rolling.Window);
        Assert.Equal(ProviderQuotaStatuses.Available, rolling.Status);
        Assert.Equal(0.12, rolling.UsageRatio, 9);
        Assert.True(rolling.Ready);
        Assert.Equal(new DateTimeOffset(2099, 6, 25, 15, 0, 0, TimeSpan.Zero), rolling.NextResetAt);
        Assert.Equal(new DateTimeOffset(2099, 6, 25, 10, 0, 0, TimeSpan.Zero), rolling.PeriodStart);

        var weekly = quota.Limits[1];
        Assert.Equal("weekly", weekly.Window);
        Assert.Equal(ProviderQuotaStatuses.Warning, weekly.Status);
        Assert.Equal(0.85, weekly.UsageRatio, 9);
        Assert.True(weekly.Ready);
        Assert.Equal(new DateTimeOffset(2099, 7, 2, 10, 0, 0, TimeSpan.Zero), weekly.NextResetAt);
        Assert.Equal(new DateTimeOffset(2099, 6, 25, 10, 0, 0, TimeSpan.Zero), weekly.PeriodStart);

        var monthly = quota.Limits[2];
        Assert.Equal("monthly", monthly.Window);
        Assert.Equal(ProviderQuotaStatuses.Available, monthly.Status);
        Assert.Equal(0.35, monthly.UsageRatio, 9);
        Assert.Equal(new DateTimeOffset(2099, 7, 25, 10, 0, 0, TimeSpan.Zero), monthly.NextResetAt);
        Assert.Equal(new DateTimeOffset(2099, 6, 25, 10, 0, 0, TimeSpan.Zero), monthly.PeriodStart);

        Assert.Equal(
            """{"plan_type":"go","windows":{"monthly":{"percent_remaining":65,"reset_in_seconds":2592000,"reset_time":"2099-07-25T10:00:00Z","status":"available","usage_percent":35},"rolling":{"percent_remaining":88,"reset_in_seconds":18000,"reset_time":"2099-06-25T15:00:00Z","status":"available","usage_percent":12},"weekly":{"percent_remaining":15,"reset_in_seconds":604800,"reset_time":"2099-07-02T10:00:00Z","status":"warning","usage_percent":85}}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task CheckQuotaCarriesTheBearerAuthAndAcceptOnTheRequestData()
    {
        var executor = OkExecutor();
        var checker = CreateChecker(executor);

        await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var request = Assert.Single(executor.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(UsageUrl, request.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Accept"));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(request.Headers, "Authorization"));
        Assert.NotNull(request.Auth);
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal("test-api-key", request.Auth.ApiKey);
        Assert.Null(request.Body);
    }

    // ------------------------------------------------------------------
    // Status classification
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(0, ProviderQuotaStatuses.Available, true)]
    [InlineData(79.9, ProviderQuotaStatuses.Available, true)]
    [InlineData(80, ProviderQuotaStatuses.Warning, true)]
    [InlineData(99.9, ProviderQuotaStatuses.Warning, true)]
    [InlineData(100, ProviderQuotaStatuses.Exhausted, false)]
    [InlineData(120, ProviderQuotaStatuses.Exhausted, false)]
    public async Task WindowStatusFollowsThePercentThresholds(
        double percent,
        string expectedStatus,
        bool expectedReady)
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                "{\"usage\":{\"rolling\":{\"percent\":"
                + percent.ToString(CultureInfo.InvariantCulture)
                + ",\"resetsAt\":\"2099-06-25T15:00:00Z\"}}}"),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(expectedStatus, quota.Status);
        Assert.Equal(expectedReady, quota.Ready);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(expectedStatus, limit.Status);
        Assert.Equal(expectedReady, limit.Ready);
        // Normalization clamps the ratio to 1 while the raw percent survives.
        Assert.Equal(Math.Min(percent / 100.0, 1.0), limit.UsageRatio, 9);

        var window = RawWindow(quota, "rolling");
        Assert.Equal(percent, (double)window["usage_percent"]!, 9);
        Assert.Equal(100 - percent, (double)window["percent_remaining"]!, 9);
        Assert.Equal("2099-06-25T15:00:00Z", window["reset_time"]);
    }

    [Fact]
    public async Task NegativePercentKeepsTheRawWindowButDropsTheLimit()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"usage":{"rolling":{"percent":-5,"resetsAt":"2099-06-25T15:00:00Z"}}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        // Normalization drops limits with a negative ratio.
        Assert.Empty(quota.Limits);

        var window = RawWindow(quota, "rolling");
        Assert.Equal(-5.0, (double)window["usage_percent"]!, 9);
        Assert.Equal(105.0, (double)window["percent_remaining"]!, 9);
        Assert.Equal("available", window["status"]);
        Assert.Equal(
            """{"plan_type":"go","windows":{"rolling":{"percent_remaining":105,"reset_in_seconds":18000,"reset_time":"2099-06-25T15:00:00Z","status":"available","usage_percent":-5}}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task PercentOver100KeepsTheRawValuesButClampsTheLimit()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"usage":{"rolling":{"percent":120,"resetsAt":"2099-06-25T15:00:00Z"}}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(1.0, limit.UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);

        Assert.Equal(
            """{"plan_type":"go","windows":{"rolling":{"percent_remaining":-20,"reset_in_seconds":18000,"reset_time":"2099-06-25T15:00:00Z","status":"exhausted","usage_percent":120}}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Theory]
    [InlineData(0.0, ProviderQuotaStatuses.Available)]
    [InlineData(0.79, ProviderQuotaStatuses.Available)]
    [InlineData(0.8, ProviderQuotaStatuses.Warning)]
    [InlineData(0.999, ProviderQuotaStatuses.Warning)]
    [InlineData(1.0, ProviderQuotaStatuses.Exhausted)]
    [InlineData(1.5, ProviderQuotaStatuses.Exhausted)]
    public void NormalizeWindowStatusMatchesTheReferenceThresholds(double ratio, string expected)
    {
        Assert.Equal(expected, OpenCodeGoQuotaChecker.NormalizeWindowStatus(ratio));
    }

    [Theory]
    [InlineData("exhausted", 2)]
    [InlineData("warning", 1)]
    [InlineData("available", 0)]
    [InlineData("unknown", -1)]
    [InlineData("", -1)]
    [InlineData("custom", -1)]
    public void QuotaStatusRankMatchesTheReference(string status, int expected)
    {
        Assert.Equal(expected, OpenCodeGoQuotaChecker.QuotaStatusRank(status));
    }

    // ------------------------------------------------------------------
    // resetsAt decoding
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("\"2026-08-12T11:24:29Z\"", "2026-08-12T11:24:29Z")]
    [InlineData("\"2026-08-12T11:24:29.905Z\"", "2026-08-12T11:24:29.905Z")]
    [InlineData("\"2026-08-12T11:24:29.123456789Z\"", "2026-08-12T11:24:29.1234567Z")]
    [InlineData("\"2026-08-12T11:24:29+02:00\"", "2026-08-12T11:24:29+02:00")]
    [InlineData("1782370800", "2026-06-25T07:00:00Z")]
    [InlineData("1782370800000", "2026-06-25T07:00:00Z")]
    [InlineData("1782370800.9", "2026-06-25T07:00:00Z")]
    [InlineData("4086082800000", "2099-06-25T15:00:00Z")]
    [InlineData("1000000000000", "2001-09-09T01:46:40Z")]
    public void ParseResetsAtAcceptsTheReferenceForms(string raw, string expectedIso)
    {
        var expected = DateTimeOffset.Parse(expectedIso, CultureInfo.InvariantCulture);

        var actual = OpenCodeGoQuotaChecker.ParseResetsAt(raw);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual!.Value);
        Assert.Equal(expected.Offset, actual.Value.Offset);
    }

    [Theory]
    [InlineData("1e300")] // beyond the 1e15 magnitude guard
    [InlineData("\"not-a-time\"")]
    [InlineData("\"2099-06-25T15:00:00\"")] // RFC3339 requires a zone
    [InlineData("null")]
    [InlineData("-5")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("999999999999")] // Go: year 33658; outside DateTimeOffset (C212)
    [InlineData("1e15")] // Go: year 33658 in ms; outside DateTimeOffset (C212)
    public void ParseResetsAtRejectsTheReferenceInvalidForms(string raw)
    {
        Assert.Null(OpenCodeGoQuotaChecker.ParseResetsAt(raw));
    }

    [Fact]
    public async Task MillisecondRfc3339ResetKeepsTicksAndFormatsSeconds()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"usage":{"rolling":{"status":"ok","percent":0,"resetsAt":"2099-08-12T11:24:29.905Z"}}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var expected = new DateTimeOffset(2099, 8, 12, 11, 24, 29, TimeSpan.Zero)
            .AddTicks(9_050_000);
        Assert.Equal(expected, quota.NextResetAt);
        Assert.Equal(expected, Assert.Single(quota.Limits).NextResetAt);

        var window = RawWindow(quota, "rolling");
        // time.Format(time.RFC3339) drops fractional seconds.
        Assert.Equal("2099-08-12T11:24:29Z", window["reset_time"]);
    }

    [Fact]
    public async Task OffsetResetKeepsItsOffsetInRawTime()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"usage":{"rolling":{"percent":10,"resetsAt":"2099-06-26T13:00:00+02:00"}}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var expected = new DateTimeOffset(2099, 6, 26, 13, 0, 0, TimeSpan.FromHours(2));
        Assert.Equal(expected, quota.NextResetAt);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(expected, limit.NextResetAt);
        Assert.NotNull(limit.PeriodStart);
        Assert.Equal(TimeSpan.FromHours(2), limit.PeriodStart!.Value.Offset);
        Assert.Equal(
            new DateTimeOffset(2099, 6, 26, 8, 0, 0, TimeSpan.FromHours(2)),
            limit.PeriodStart.Value);

        Assert.Equal("2099-06-26T13:00:00+02:00", RawWindow(quota, "rolling")["reset_time"]);
    }

    [Theory]
    [InlineData("1782370800")]
    [InlineData("1782370800000")]
    public async Task UnixSecondAndMillisecondResetsProduceTheSameUtcInstant(string raw)
    {
        var checker = CreateChecker(
            new ScriptedExecutor
            {
                OnDo = (_, _, _) => Ok(
                    "{\"usage\":{\"rolling\":{\"percent\":12,\"resetsAt\":" + raw + "}}}"),
            },
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var expected = new DateTimeOffset(2026, 6, 25, 7, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, quota.NextResetAt);
        Assert.Equal(expected, Assert.Single(quota.Limits).NextResetAt);
        Assert.Equal("2026-06-25T07:00:00Z", RawWindow(quota, "rolling")["reset_time"]);
    }

    [Fact]
    public async Task EpochMillisecondsAtTheReferenceValueMapToTheSameInstant()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"usage":{"rolling":{"percent":12,"resetsAt":4086082800000}}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var expected = DateTimeOffset.FromUnixTimeMilliseconds(4086082800000);
        Assert.Equal(expected, quota.NextResetAt);
        Assert.Equal(expected, Assert.Single(quota.Limits).NextResetAt);
        Assert.Equal("2099-06-25T15:00:00Z", RawWindow(quota, "rolling")["reset_time"]);
    }

    // ------------------------------------------------------------------
    // Window selection and resets
    // ------------------------------------------------------------------

    [Fact]
    public async Task PartialWindowsKeepOnlyTheParseableOne()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"usage":{"rolling":{"percent":100,"resetsAt":"2099-06-25T15:00:00Z"}}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal("5h", limit.Window);
        Assert.Equal(new DateTimeOffset(2099, 6, 25, 15, 0, 0, TimeSpan.Zero), quota.NextResetAt);
    }

    [Fact]
    public async Task AllWindowsUnparseableThrows()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """
                {"usage":{
                    "rolling":{"percent":12,"resetsAt":"not-a-time"},
                    "weekly":{"percent":8,"resetsAt":null},
                    "monthly":{"percent":35,"resetsAt":-5}
                }}
                """),
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("could not parse OpenCode Go usage windows", error.Message);
    }

    [Fact]
    public async Task PayloadWithoutWindowsThrows()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok("""{"error":"unauthorized"}"""),
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("could not parse OpenCode Go usage windows", error.Message);
    }

    [Fact]
    public async Task MissingResetsAtSkipsTheWindow()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok("""{"usage":{"weekly":{"percent":21}}}"""),
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("could not parse OpenCode Go usage windows", error.Message);
    }

    [Fact]
    public async Task SurfacesApiSubStatusInRawData()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"usage":{"weekly":{"status":"ok","percent":21,"resetsAt":"2099-06-25T15:00:00Z"}}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var window = RawWindow(quota, "weekly");
        // The percent-derived status stays authoritative; the API value is
        // surfaced next to it for observability.
        Assert.Equal("available", window["status"]);
        Assert.Equal("ok", window["api_status"]);
        Assert.Equal(
            """{"plan_type":"go","windows":{"weekly":{"api_status":"ok","percent_remaining":79,"reset_in_seconds":18000,"reset_time":"2099-06-25T15:00:00Z","status":"available","usage_percent":21}}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task ResetInThePastIsClampedToZeroAndClearedByNormalization()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"usage":{"rolling":{"percent":12,"resetsAt":"2020-01-01T00:00:00Z"}}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Null(quota.NextResetAt);
        var limit = Assert.Single(quota.Limits);
        Assert.Null(limit.NextResetAt);
        Assert.Null(limit.PeriodStart);

        var window = RawWindow(quota, "rolling");
        Assert.Equal(0.0, (double)window["reset_in_seconds"]!, 9);
        Assert.Equal("2020-01-01T00:00:00Z", window["reset_time"]);
    }

    [Fact]
    public async Task NextResetAtIsTheEarliestWindowReset()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """
                {"usage":{
                    "rolling":{"percent":10,"resetsAt":"2099-07-01T00:00:00Z"},
                    "weekly":{"percent":50,"resetsAt":"2099-06-26T00:00:00Z"}
                }}
                """),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(new DateTimeOffset(2099, 6, 26, 0, 0, 0, TimeSpan.Zero), quota.NextResetAt);
        Assert.Equal(
            new DateTimeOffset(2099, 7, 1, 0, 0, 0, TimeSpan.Zero),
            quota.Limits[0].NextResetAt);
        Assert.Equal(
            new DateTimeOffset(2099, 6, 26, 0, 0, 0, TimeSpan.Zero),
            quota.Limits[1].NextResetAt);
    }

    [Theory]
    [InlineData("2099-03-31T10:00:00Z", "2099-02-28T10:00:00Z")]
    [InlineData("2096-03-31T10:00:00Z", "2096-02-29T10:00:00Z")]
    [InlineData("2099-01-31T10:00:00Z", "2098-12-31T10:00:00Z")]
    [InlineData("2099-03-31T10:00:00+05:30", "2099-02-28T10:00:00+05:30")]
    public async Task MonthlyPeriodStartClampsTheDayToThePreviousMonth(
        string reset,
        string expectedPeriodStart)
    {
        var checker = CreateChecker(
            new ScriptedExecutor
            {
                OnDo = (_, _, _) => Ok(
                    "{\"usage\":{\"monthly\":{\"percent\":10,\"resetsAt\":\"" + reset + "\"}}}"),
            },
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var expected = DateTimeOffset.Parse(expectedPeriodStart, CultureInfo.InvariantCulture);
        var actual = Assert.Single(quota.Limits).PeriodStart;
        Assert.NotNull(actual);
        Assert.Equal(expected, actual!.Value);
        Assert.Equal(expected.Offset, actual.Value.Offset);
    }

    [Theory]
    [InlineData("rolling", "5h")]
    [InlineData("weekly", "weekly")]
    [InlineData("monthly", "monthly")]
    [InlineData("custom", "custom")]
    [InlineData("", "")]
    public void WindowLabelMapsTheReferenceKeys(string key, string expected)
    {
        Assert.Equal(expected, OpenCodeGoQuotaChecker.WindowLabel(key));
    }

    [Fact]
    public void PeriodStartForAnUnknownKeyIsNull()
    {
        Assert.Null(
            OpenCodeGoQuotaChecker.PeriodStart(
                "custom",
                new DateTimeOffset(2099, 6, 25, 15, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData("2099-06-25T15:00:00Z", "2099-06-25T15:00:00Z")]
    [InlineData("2099-06-25T15:00:00.905Z", "2099-06-25T15:00:00Z")]
    [InlineData("2099-06-25T15:00:00+02:00", "2099-06-25T15:00:00+02:00")]
    [InlineData("2099-06-25T15:00:00-05:30", "2099-06-25T15:00:00-05:30")]
    public void FormatRfc3339MatchesGoSecondsPrecision(string iso, string expected)
    {
        Assert.Equal(
            expected,
            OpenCodeGoQuotaChecker.FormatRfc3339(
                DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture)));
    }

    // ------------------------------------------------------------------
    // Credentials
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaTrimsTheLegacyApiKey()
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"  test-api-key  "}"""),
            CancellationToken.None);

        Assert.Equal("test-api-key", Assert.Single(executor.Requests).Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaUsesTheFirstNonBlankApiKeysEntry()
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(credentials: """{"apiKeys":["", "  ", "  key-two  ", "key-three"]}"""),
            CancellationToken.None);

        Assert.Equal("key-two", Assert.Single(executor.Requests).Auth?.ApiKey);
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
    public async Task CheckQuotaRejectsOAuthOnlyCredentials()
    {
        var executor = OkExecutor();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateChecker(executor).CheckQuotaAsync(
                Channel(credentials: """{"apiKey":"{\"access_token\":\"oauth-token\"}"}"""),
                CancellationToken.None));

        Assert.Equal("channel has no API key", error.Message);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task CheckQuotaUsesArrayKeysWhenTheLegacyKeyHoldsOAuthJson()
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"{\"access_token\":\"oauth-token\"}","apiKeys":["array-key"]}"""),
            CancellationToken.None);

        Assert.Equal("array-key", Assert.Single(executor.Requests).Auth?.ApiKey);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"apiKey":"   "}""")]
    [InlineData("""{"apiKeys":[]}""")]
    [InlineData("""{"apiKeys":[""]}""")]
    [InlineData("""{"apiKeys":["  ","\t"]}""")]
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

        Assert.Equal("channel has no API key", error.Message);
        Assert.Empty(executor.Requests);
    }

    // ------------------------------------------------------------------
    // Errors
    // ------------------------------------------------------------------

    [Fact]
    public async Task MalformedJsonReportsTheParsePrefix()
    {
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok("not json") });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.StartsWith("parse OpenCode Go usage response: ", error.Message);
    }

    [Fact]
    public async Task EmptyBodyReportsTheParsePrefix()
    {
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok("") });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.StartsWith("parse OpenCode Go usage response: ", error.Message);
    }

    [Theory]
    [InlineData(
        """{"usage":[]}""",
        "json: cannot unmarshal array into Go struct field opencodeGoUsageResponse.usage of type provider_quota.opencodeGoUsageWindows")]
    [InlineData(
        """{"usage":{"rolling":[]}}""",
        "json: cannot unmarshal array into Go struct field opencodeGoUsageWindows.usage.rolling of type provider_quota.opencodeGoUsageWindow")]
    [InlineData(
        """{"usage":{"rolling":{"percent":"x"}}}""",
        "json: cannot unmarshal string into Go struct field opencodeGoUsageWindow.usage.rolling.percent of type float64")]
    [InlineData(
        """{"usage":{"rolling":{"status":5}}}""",
        "json: cannot unmarshal number into Go struct field opencodeGoUsageWindow.usage.rolling.status of type string")]
    public async Task TypeMismatchesReportTheParsePrefixAndGoMessage(
        string body,
        string expectedInner)
    {
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok(body) });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("parse OpenCode Go usage response: " + expectedInner, error.Message);
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
                UsageUrl,
                statusCode,
                status,
                Encoding.UTF8.GetBytes("""{"error":"nope"}""")),
        });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.StartsWith("opencode go usage request failed: ", error.Message);
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

        Assert.Equal("opencode go usage request failed: connection reset", error.Message);
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
                Body = Encoding.UTF8.GetBytes(
                    """{"usage":{"rolling":{"percent":12,"resetsAt":"2099-06-25T15:00:00Z"}}}"""),
            },
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Single(quota.Limits);
    }

    // ------------------------------------------------------------------
    // JSON decoding: Go semantics
    // ------------------------------------------------------------------

    [Fact]
    public void ParseUsageResponseTreatsRootNullAsAnEmptyStruct()
    {
        var response = ParseUsageResponse("null");

        Assert.NotNull(response.Usage);
        Assert.Null(response.Usage.Rolling);
        Assert.Null(response.Usage.Weekly);
        Assert.Null(response.Usage.Monthly);
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
            "json: cannot unmarshal "
            + kind
            + " into Go value of type provider_quota.opencodeGoUsageResponse",
            error.Message);
    }

    [Theory]
    [InlineData(
        """{"usage":5}""",
        "json: cannot unmarshal number into Go struct field opencodeGoUsageResponse.usage of type provider_quota.opencodeGoUsageWindows")]
    [InlineData(
        """{"usage":true}""",
        "json: cannot unmarshal bool into Go struct field opencodeGoUsageResponse.usage of type provider_quota.opencodeGoUsageWindows")]
    [InlineData(
        """{"usage":{"weekly":true}}""",
        "json: cannot unmarshal bool into Go struct field opencodeGoUsageWindows.usage.weekly of type provider_quota.opencodeGoUsageWindow")]
    [InlineData(
        """{"usage":{"rolling":{"percent":false}}}""",
        "json: cannot unmarshal bool into Go struct field opencodeGoUsageWindow.usage.rolling.percent of type float64")]
    [InlineData(
        """{"usage":{"rolling":{"percent":1e400}}}""",
        "json: cannot unmarshal number 1e400 into Go struct field opencodeGoUsageWindow.usage.rolling.percent of type float64")]
    [InlineData(
        """{"usage":{"weekly":{"status":{}}}}""",
        "json: cannot unmarshal object into Go struct field opencodeGoUsageWindow.usage.weekly.status of type string")]
    public void ParseUsageResponseReportsGoTypeMismatches(string json, string expectedMessage)
    {
        var error = Assert.Throws<JsonException>(() => ParseUsageResponse(json));

        Assert.Equal(expectedMessage, error.Message);
    }

    [Fact]
    public void ParseUsageResponseAcceptsEmptyWindowObjects()
    {
        var response = ParseUsageResponse("""{"usage":{"monthly":{}}}""");

        Assert.NotNull(response.Usage.Monthly);
        Assert.Equal(0.0, response.Usage.Monthly!.Percent, 9);
        Assert.Null(response.Usage.Monthly.ResetsAt);
    }

    [Fact]
    public void ParseUsageResponseMergesDuplicateAndCaseFoldedKeys()
    {
        var response = ParseUsageResponse(
            """{"usage":{"rolling":{"percent":12}},"USAGE":{"Weekly":{"percent":30}},"usage":{"rolling":{"resetsAt":"2099-01-01T00:00:00Z"}}}""");

        Assert.NotNull(response.Usage.Rolling);
        Assert.Equal(12.0, response.Usage.Rolling!.Percent, 9);
        Assert.Equal("\"2099-01-01T00:00:00Z\"", response.Usage.Rolling.ResetsAt);
        Assert.NotNull(response.Usage.Weekly);
        Assert.Equal(30.0, response.Usage.Weekly!.Percent, 9);
        Assert.Null(response.Usage.Monthly);
    }

    [Fact]
    public void ParseUsageResponseResetsWindowPointersOnExplicitNull()
    {
        var response = ParseUsageResponse(
            """{"usage":{"rolling":{"percent":12},"rolling":null,"weekly":{"percent":1},"WEEKLY":null}}""");

        Assert.Null(response.Usage.Rolling);
        Assert.Null(response.Usage.Weekly);
    }

    [Fact]
    public void ParseUsageResponseIgnoresUnknownFields()
    {
        var response = ParseUsageResponse(
            """{"unknown":true,"usage":{"extra":1,"rolling":{"percent":12,"extra":2}},"another":[1,2]}""");

        Assert.NotNull(response.Usage.Rolling);
        Assert.Equal(12.0, response.Usage.Rolling!.Percent, 9);
        Assert.Empty(response.Usage.Rolling.Status);
    }

    [Fact]
    public async Task ExplicitNullResetsAtMakesTheWindowUnparseable()
    {
        // json.RawMessage keeps the literal null, so a later null overwrites an
        // earlier valid value rather than leaving it in place.
        var response = ParseUsageResponse(
            """{"usage":{"rolling":{"percent":12,"resetsAt":"2099-01-01T00:00:00Z"},"rolling":{"resetsAt":null}}}""");
        Assert.Equal("null", response.Usage.Rolling!.ResetsAt);

        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"usage":{"rolling":{"percent":12,"resetsAt":"2099-01-01T00:00:00Z"},"rolling":{"resetsAt":null}}}"""),
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));
        Assert.Equal("could not parse OpenCode Go usage windows", error.Message);
    }

    [Fact]
    public void ParseUsageResponseReplacesInvalidUtf8LikeGo()
    {
        var prefix = Encoding.UTF8.GetBytes(
            """{"usage":{"rolling":{"percent":12,"status":"ok""");
        var suffix = Encoding.UTF8.GetBytes(
            "\",\"resetsAt\":\"2099-01-01T00:00:00Z\"}}}");
        byte[] body = [.. prefix, 0xFF, .. suffix];

        var response = OpenCodeGoQuotaChecker.ParseUsageResponse(body);

        Assert.Equal("ok" + char.ConvertFromUtf32(0xFFFD), response.Usage.Rolling!.Status);
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
        var checker = new OpenCodeGoQuotaChecker(direct, factory, new AntigravityTestClock(Now));

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
        var checker = new OpenCodeGoQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(Channel(settings: "{\"proxy\":{"), CancellationToken.None);

        Assert.Empty(factory.Requests);
        Assert.Single(direct.Requests);
    }

    [Fact]
    public async Task CheckQuotaUsesTheDirectExecutorWithoutASettingsProxy()
    {
        var direct = OkExecutor();
        var factory = new ScriptedProxyExecutorFactory();
        var checker = new OpenCodeGoQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Empty(factory.Requests);
        Assert.Single(direct.Requests);
    }

    // ------------------------------------------------------------------
    // Capability
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("opencode_go", true)]
    [InlineData("opencode_go_anthropic", true)]
    [InlineData("openai", false)]
    [InlineData("anthropic", false)]
    [InlineData("claudecode", false)]
    [InlineData("", false)]
    public void SupportsChannelMatchesTheTwoOpenCodeGoVariants(string channelType, bool expected)
    {
        var checker = CreateChecker(new ScriptedExecutor());

        Assert.Equal(expected, checker.SupportsChannel(new ChannelRecord { Type = channelType }));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static OpenCodeGoQuotaChecker CreateChecker(
        out ClaudeCodeQuotaCheckerTests.RecordingHttpHandler handler)
    {
        handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new OpenCodeGoQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now));
    }

    private static OpenCodeGoQuotaChecker CreateChecker(ScriptedExecutor executor)
        => new(executor, new ScriptedProxyExecutorFactory(), new AntigravityTestClock(Now));

    private static OpenCodeGoQuotaChecker CreateChecker(
        ScriptedExecutor executor,
        DateTimeOffset now)
        => new(executor, new ScriptedProxyExecutorFactory(), new AntigravityTestClock(now));

    private static ChannelRecord Channel(
        string? credentials = null,
        string settings = "{}",
        string type = "opencode_go")
        => new()
        {
            Type = type,
            Credentials = credentials ?? DefaultCredentials,
            Settings = settings,
        };

    private static OpenCodeGoUsageResponse ParseUsageResponse(string json)
        => OpenCodeGoQuotaChecker.ParseUsageResponse(Encoding.UTF8.GetBytes(json));

    private static Dictionary<string, object?> RawWindow(QuotaData quota, string key)
    {
        var windows = Assert.IsType<Dictionary<string, object?>>(quota.RawData["windows"]);
        return Assert.IsType<Dictionary<string, object?>>(windows[key]);
    }

    private static ScriptedExecutor OkExecutor(string body = DefaultSuccessfulBody) => new()
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

    private const string DefaultSuccessfulBody =
        """{"usage":{"rolling":{"percent":12,"resetsAt":"2099-06-25T15:00:00Z"}}}""";
}
