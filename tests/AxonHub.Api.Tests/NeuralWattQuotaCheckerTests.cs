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
/// Ports internal/server/biz/provider_quota/neuralwatt_checker.go together with
/// neuralwatt_checker_test.go: the fixed /v1/quota request derived from the
/// channel host, the API-key precedence, the kWh subscription window (overage
/// and zero remaining are exhausted, less than 20% remaining is a warning) and
/// the monthly period start clamped to the previous month's last day. The wire
/// shape is locked through the real <see cref="HttpExecutor"/> over a recording
/// message handler; a bodyless GET loses its Content-Type on the wire, which is
/// the accepted difference C206 (the checker still sets it on the request
/// data, asserted separately).
/// </summary>
public sealed class NeuralWattQuotaCheckerTests
{
    // Before every reset in the fixtures, so normalization keeps the provider's
    // reset instants intact.
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string QuotaUrl = "https://api.neuralwatt.com/v1/quota";

    private const string ApiKey = "test-api-key";

    private const string FullBody = """
        {
            "balance": {
                "credits_remaining_usd": 5.0,
                "total_credits_usd": 5.0,
                "accounting_method": "energy"
            },
            "subscription": {
                "plan": "standard",
                "status": "active",
                "kwh_included": 20.0,
                "kwh_used": 14.3126,
                "kwh_remaining": 5.6874,
                "in_overage": false
            }
        }
        """;

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaFetchesTheQuotaEndpointAndMapsTheAvailableState()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(FullBody);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(QuotaUrl, request.Url);
        Assert.Equal("Bearer " + ApiKey, request.Header("Authorization"));
        // C206: the checker sets Content-Type on the request data, but the
        // executor only attaches content headers when a body exists, so this
        // bodyless GET loses it on the wire (the reference sends it).
        Assert.Equal(string.Empty, request.Header("Content-Type"));
        // The shared executor adds its default Accept (Go would send none).
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Equal(string.Empty, request.Body);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal("neuralwatt", quota.ProviderType);
        Assert.True(quota.Ready);

        // Exactly one kWh token window, derived from kwh_used / kwh_included.
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaLimitTypes.Token, limit.Type);
        Assert.Equal(NeuralWattQuotaChecker.KwhWindow, limit.Window);
        Assert.Equal("kwh", limit.Window);
        Assert.Equal(ProviderQuotaStatuses.Available, limit.Status);
        Assert.Equal(0.71563, limit.UsageRatio, 9);
        Assert.True(limit.Ready);
        Assert.Null(limit.NextResetAt);
        Assert.Null(limit.PeriodStart);
        Assert.Null(quota.NextResetAt);

        // rawData keeps the provider's snake_case field names and drops absent
        // pointers; whole floats render without a fraction, as Go does.
        Assert.Equal(
            """{"balance":{"accounting_method":"energy","credits_remaining_usd":5,"total_credits_usd":5},"subscription":{"in_overage":false,"kwh_included":20,"kwh_remaining":5.6874,"kwh_used":14.3126,"plan":"standard","status":"active"}}""",
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
        Assert.Equal(QuotaUrl, request.Url);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(request.Headers, "Content-Type"));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(request.Headers, "Authorization"));
        Assert.NotNull(request.Auth);
        Assert.Equal(AuthTypes.Bearer, request.Auth!.Type);
        Assert.Equal(ApiKey, request.Auth.ApiKey);
        Assert.Null(request.Body);
    }

    // ------------------------------------------------------------------
    // Status classification
    // ------------------------------------------------------------------

    [Fact]
    public async Task WarningStateFromLowRemainingStampsTheMonthlyPeriodStart()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            // kwh_remaining = 3 of 20 -> 15% < 20% -> warning.
            OnDo = (_, _, _) => Ok(
                """
                {
                    "balance": {"credits_remaining_usd": 1.5, "total_credits_usd": 5.0, "accounting_method": "energy"},
                    "subscription": {
                        "plan": "standard",
                        "status": "active",
                        "kwh_included": 20.0,
                        "kwh_used": 17.0,
                        "kwh_remaining": 3.0,
                        "in_overage": false,
                        "kwh_reset_date": "2099-06-02T05:58:36Z"
                    }
                }
                """),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);

        var resetAt = new DateTimeOffset(2099, 6, 2, 5, 58, 36, TimeSpan.Zero);
        Assert.Equal(resetAt, quota.NextResetAt);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Warning, limit.Status);
        Assert.Equal(0.85, limit.UsageRatio, 9);
        Assert.True(limit.Ready);
        Assert.Equal(resetAt, limit.NextResetAt);
        // PeriodStartFromMonthlyReset: one month earlier, same day-of-month.
        Assert.Equal(
            new DateTimeOffset(2099, 5, 2, 5, 58, 36, TimeSpan.Zero),
            limit.PeriodStart);

        Assert.Equal(
            """{"balance":{"accounting_method":"energy","credits_remaining_usd":1.5,"total_credits_usd":5},"subscription":{"in_overage":false,"kwh_included":20,"kwh_remaining":3,"kwh_reset_date":"2099-06-02T05:58:36Z","kwh_used":17,"plan":"standard","status":"active"}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task MissingResetDateLeavesBothResetsNull()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"subscription":{"kwh_included":20,"kwh_used":5,"kwh_remaining":15,"in_overage":false}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Null(quota.NextResetAt);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal("kwh", limit.Window);
        Assert.Equal(0.25, limit.UsageRatio, 9);
        Assert.Null(limit.NextResetAt);
        Assert.Null(limit.PeriodStart);
    }

    [Fact]
    public async Task ExhaustedStateViaOverageWithZeroRemaining()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """
                {
                    "balance": {"credits_remaining_usd": 0.0, "total_credits_usd": 5.0, "accounting_method": "energy"},
                    "subscription": {
                        "plan": "standard",
                        "status": "active",
                        "kwh_included": 20.0,
                        "kwh_used": 22.5,
                        "kwh_remaining": 0.0,
                        "in_overage": true,
                        "kwh_reset_date": "2099-06-02T05:58:36Z"
                    }
                }
                """),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);

        var resetAt = new DateTimeOffset(2099, 6, 2, 5, 58, 36, TimeSpan.Zero);
        Assert.Equal(resetAt, quota.NextResetAt);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);
        // 22.5 / 20 = 1.125, clamped to 1 by normalization.
        Assert.Equal(1.0, limit.UsageRatio, 9);
        Assert.False(limit.Ready);
        Assert.Equal(resetAt, limit.NextResetAt);
        Assert.Equal(
            new DateTimeOffset(2099, 5, 2, 5, 58, 36, TimeSpan.Zero),
            limit.PeriodStart);
    }

    [Fact]
    public async Task ZeroRemainingWithoutOverageIsExhausted()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """
                {
                    "subscription": {
                        "plan": "standard",
                        "status": "active",
                        "kwh_included": 20.0,
                        "kwh_used": 20.0,
                        "kwh_remaining": 0.0,
                        "in_overage": false,
                        "kwh_reset_date": "2099-06-02T05:58:36Z"
                    }
                }
                """),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal(
            new DateTimeOffset(2099, 6, 2, 5, 58, 36, TimeSpan.Zero),
            quota.NextResetAt);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);
        Assert.Equal(1.0, limit.UsageRatio, 9);
        Assert.False(limit.Ready);
        Assert.Equal(
            new DateTimeOffset(2099, 5, 2, 5, 58, 36, TimeSpan.Zero),
            limit.PeriodStart);
    }

    [Fact]
    public async Task SubscriptionWithoutKeyFieldsIsUnknownButKeepsTheLimit()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"balance":{"credits_remaining_usd":5.0,"total_credits_usd":5.0},"subscription":{"plan":"standard","status":"active"}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.False(quota.Ready);
        Assert.Null(quota.NextResetAt);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Unknown, limit.Status);
        Assert.Equal(0.0, limit.UsageRatio, 9);
        Assert.False(limit.Ready);
        Assert.Null(limit.NextResetAt);
        Assert.Null(limit.PeriodStart);

        Assert.Equal(
            """{"balance":{"credits_remaining_usd":5,"total_credits_usd":5},"subscription":{"plan":"standard","status":"active"}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Theory]
    [InlineData("""{"in_overage":true,"kwh_remaining":10,"kwh_included":20}""", ProviderQuotaStatuses.Exhausted, false)]
    [InlineData("""{"kwh_remaining":0,"kwh_included":20}""", ProviderQuotaStatuses.Exhausted, false)]
    [InlineData("""{"kwh_remaining":4,"kwh_included":20}""", ProviderQuotaStatuses.Available, true)]
    [InlineData("""{"kwh_remaining":3.999,"kwh_included":20}""", ProviderQuotaStatuses.Warning, true)]
    [InlineData("""{"kwh_remaining":10,"kwh_included":20}""", ProviderQuotaStatuses.Available, true)]
    [InlineData("""{"kwh_included":20}""", ProviderQuotaStatuses.Unknown, false)]
    [InlineData("""{"kwh_remaining":10}""", ProviderQuotaStatuses.Available, true)]
    [InlineData("""{"kwh_included":0,"kwh_remaining":1}""", ProviderQuotaStatuses.Available, true)]
    [InlineData("""{"kwh_included":-5,"kwh_remaining":-1}""", ProviderQuotaStatuses.Exhausted, false)]
    [InlineData("""{"in_overage":false}""", ProviderQuotaStatuses.Unknown, false)]
    [InlineData("""{}""", ProviderQuotaStatuses.Unknown, false)]
    public async Task StatusFollowsTheReferencePrecedence(
        string subscription,
        string expectedStatus,
        bool expectedReady)
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok("""{"subscription":""" + subscription + "}"),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(expectedStatus, quota.Status);
        Assert.Equal(expectedReady, quota.Ready);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(expectedStatus, limit.Status);
        Assert.Equal(expectedReady, limit.Ready);
    }

    [Theory]
    [InlineData("""{"kwh_included":20,"kwh_used":5}""", 0.25)]
    [InlineData("""{"kwh_included":20,"kwh_used":5,"kwh_remaining":15}""", 0.25)]
    [InlineData("""{"kwh_included":20,"kwh_remaining":15}""", 0.25)]
    [InlineData("""{"kwh_included":2e1,"kwh_used":1e1}""", 0.5)]
    [InlineData("""{"kwh_included":20}""", 0.0)]
    [InlineData("""{"kwh_included":0,"kwh_used":5,"kwh_remaining":10}""", 0.0)]
    [InlineData("""{"kwh_included":-20,"kwh_used":-10,"kwh_remaining":10}""", 0.0)]
    public async Task UsageRatioFollowsTheReferencePrecedence(string subscription, double expectedRatio)
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok("""{"subscription":""" + subscription + "}"),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(expectedRatio, Assert.Single(quota.Limits).UsageRatio, 9);
    }

    [Fact]
    public async Task UsageRatioAboveOneIsClampedAndTheExplicitStatusWins()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            // kwh_remaining is healthy, so the explicit status is available, but
            // kwh_used overstates the window; normalization clamps the ratio to
            // 1 and marks the limit exhausted while the explicit status survives.
            OnDo = (_, _, _) => Ok(
                """{"subscription":{"kwh_included":20,"kwh_used":30,"kwh_remaining":15}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);
        Assert.Equal(1.0, limit.UsageRatio, 9);
        Assert.False(limit.Ready);
    }

    [Fact]
    public async Task NegativeUsageRatioDropsTheLimitButKeepsTheExplicitStatus()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"subscription":{"kwh_included":20,"kwh_used":-5,"kwh_remaining":15}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Empty(quota.Limits);
    }

    // ------------------------------------------------------------------
    // Reset parsing and the monthly period start
    // ------------------------------------------------------------------

    [Fact]
    public async Task FractionalResetKeepsThe100NanosecondTicks()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"subscription":{"kwh_included":20,"kwh_remaining":10,"kwh_reset_date":"2099-06-02T05:58:36.123456789Z"}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        // Go keeps nanoseconds; DateTimeOffset keeps 100ns ticks, truncating the
        // remainder (C123). The monthly period start keeps the same ticks.
        var expectedReset = new DateTimeOffset(2099, 6, 2, 5, 58, 36, TimeSpan.Zero)
            .AddTicks(1234567);
        Assert.Equal(expectedReset, quota.NextResetAt);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(expectedReset, limit.NextResetAt);
        Assert.Equal(
            new DateTimeOffset(2099, 5, 2, 5, 58, 36, TimeSpan.Zero).AddTicks(1234567),
            limit.PeriodStart);
    }

    [Fact]
    public async Task ResetWithOffsetKeepsTheOffsetForTheMonthlyStart()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"subscription":{"kwh_included":20,"kwh_remaining":10,"kwh_reset_date":"2099-06-02T05:58:36+02:00"}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var expectedReset = new DateTimeOffset(2099, 6, 2, 5, 58, 36, TimeSpan.FromHours(2));
        Assert.Equal(expectedReset, quota.NextResetAt);

        var periodStart = Assert.Single(quota.Limits).PeriodStart;
        Assert.NotNull(periodStart);
        Assert.Equal(
            new DateTimeOffset(2099, 5, 2, 5, 58, 36, TimeSpan.FromHours(2)),
            periodStart!.Value);
        Assert.Equal(TimeSpan.FromHours(2), periodStart.Value.Offset);
    }

    [Theory]
    [InlineData("2099-01-31T10:00:00Z", "2098-12-31T10:00:00Z")]
    [InlineData("2099-03-31T10:00:00Z", "2099-02-28T10:00:00Z")]
    [InlineData("2099-03-30T10:00:00Z", "2099-02-28T10:00:00Z")]
    [InlineData("2100-03-31T10:00:00Z", "2100-02-28T10:00:00Z")]
    [InlineData("2096-03-31T10:00:00Z", "2096-02-29T10:00:00Z")]
    [InlineData("2099-03-01T00:00:00Z", "2099-02-01T00:00:00Z")]
    [InlineData("2099-03-31T10:00:00+05:30", "2099-02-28T10:00:00+05:30")]
    public async Task MonthlyPeriodStartClampsTheDayToThePreviousMonth(
        string reset,
        string expectedPeriodStart)
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                $$"""{"subscription":{"kwh_included":20,"kwh_remaining":10,"kwh_reset_date":"{{reset}}"}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var expected = DateTimeOffset.Parse(expectedPeriodStart, CultureInfo.InvariantCulture);
        var actual = Assert.Single(quota.Limits).PeriodStart;
        Assert.NotNull(actual);
        Assert.Equal(expected, actual!.Value);
        Assert.Equal(expected.Offset, actual.Value.Offset);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("2099-06-02T05:58:36")]
    [InlineData("2099-13-02T05:58:36Z")]
    [InlineData("0000-01-01T00:00:00Z")]
    public async Task InvalidResetStringsAreIgnored(string reset)
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                $$"""{"subscription":{"kwh_included":20,"kwh_remaining":10,"kwh_reset_date":"{{reset}}"}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Null(quota.NextResetAt);
        var limit = Assert.Single(quota.Limits);
        Assert.Null(limit.NextResetAt);
        Assert.Null(limit.PeriodStart);
    }

    [Theory]
    [InlineData("2020-01-01T00:00:00Z")]
    [InlineData("2026-01-01T00:00:00Z")]
    public async Task PastAndPresentResetsAreClearedByNormalization(string reset)
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                $$"""{"subscription":{"kwh_included":20,"kwh_remaining":10,"kwh_reset_date":"{{reset}}"}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Null(quota.NextResetAt);
        var limit = Assert.Single(quota.Limits);
        Assert.Null(limit.NextResetAt);
        Assert.Null(limit.PeriodStart);
    }

    // ------------------------------------------------------------------
    // rawData shapes
    // ------------------------------------------------------------------

    [Fact]
    public async Task EmptyPayloadIsUnknownWithOneLimit()
    {
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok("{}") });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.False(quota.Ready);
        Assert.Null(quota.NextResetAt);
        Assert.Equal("{}", GoJson.MarshalToString(quota.RawData));

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaLimitTypes.Token, limit.Type);
        Assert.Equal("kwh", limit.Window);
        Assert.Equal(ProviderQuotaStatuses.Unknown, limit.Status);
        Assert.Equal(0.0, limit.UsageRatio, 9);
        Assert.False(limit.Ready);
    }

    [Fact]
    public async Task BalanceOnlyPayloadKeepsTheEmptyBalanceMap()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok("""{"balance":{}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.Equal("""{"balance":{}}""", GoJson.MarshalToString(quota.RawData));
        Assert.Single(quota.Limits);
    }

    [Fact]
    public async Task EmptySubscriptionObjectKeepsTheEmptySubscriptionMap()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok("""{"subscription":{}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.Equal("""{"subscription":{}}""", GoJson.MarshalToString(quota.RawData));
        Assert.Single(quota.Limits);
    }

    [Fact]
    public async Task ZeroAndFalseValuesArePreservedInRawData()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """
                {
                    "balance": {"credits_remaining_usd": 0, "total_credits_usd": 0, "accounting_method": ""},
                    "subscription": {
                        "plan": "",
                        "status": "",
                        "kwh_included": 0,
                        "kwh_used": 0,
                        "kwh_remaining": 0,
                        "in_overage": false,
                        "kwh_reset_date": ""
                    }
                }
                """),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        // kwh_remaining is zero -> exhausted; the empty reset string is ignored.
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.Null(quota.NextResetAt);
        Assert.Equal(
            """{"balance":{"accounting_method":"","credits_remaining_usd":0,"total_credits_usd":0},"subscription":{"in_overage":false,"kwh_included":0,"kwh_remaining":0,"kwh_reset_date":"","kwh_used":0,"plan":"","status":""}}""",
            GoJson.MarshalToString(quota.RawData));

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);
        Assert.Equal(0.0, limit.UsageRatio, 9);
        Assert.Null(limit.NextResetAt);
    }

    [Fact]
    public async Task DuplicateBalanceObjectsMergeInRawData()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"balance":{"credits_remaining_usd":1},"balance":{"total_credits_usd":2},"subscription":{"kwh_included":20,"kwh_remaining":10}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(
            """{"balance":{"credits_remaining_usd":1,"total_credits_usd":2},"subscription":{"kwh_included":20,"kwh_remaining":10}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task ExplicitNullResetsTheBalancePointer()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """{"balance":{"credits_remaining_usd":1},"balance":null,"subscription":{"kwh_remaining":10}}"""),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(
            """{"subscription":{"kwh_remaining":10}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    // ------------------------------------------------------------------
    // Credentials and errors
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaTrimsTheLegacyApiKey()
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"  test-api-key  "}"""),
            CancellationToken.None);

        Assert.Equal(ApiKey, Assert.Single(executor.Requests).Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaFallsBackToTheFirstApiKeysEntryVerbatim()
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(credentials: """{"apiKeys":["  fallback-key  ","second-key"]}"""),
            CancellationToken.None);

        Assert.Equal("  fallback-key  ", Assert.Single(executor.Requests).Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaPrefersTheLegacyApiKeyOverTheArray()
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"legacy-key","apiKeys":["fallback-key"]}"""),
            CancellationToken.None);

        Assert.Equal("legacy-key", Assert.Single(executor.Requests).Auth?.ApiKey);
    }

    [Fact]
    public async Task CheckQuotaUsesTheArrayWhenTheLegacyKeyIsOnlyWhitespace()
    {
        var executor = OkExecutor();

        await CreateChecker(executor).CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"   ","apiKeys":["fallback-key"]}"""),
            CancellationToken.None);

        Assert.Equal("fallback-key", Assert.Single(executor.Requests).Auth?.ApiKey);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"apiKey":"   "}""")]
    [InlineData("""{"apiKeys":[]}""")]
    [InlineData("""{"apiKeys":[""]}""")]
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

        Assert.Contains("no API key", error.Message);
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task CheckQuotaReportsMalformedJson()
    {
        var checker = CreateChecker(new ScriptedExecutor { OnDo = (_, _, _) => Ok("not json") });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Contains("failed to parse neuralwatt usage response", error.Message);
    }

    [Theory]
    [InlineData(401, "401 Unauthorized", """{"error":"unauthorized"}""")]
    [InlineData(403, "403 Forbidden", "denied")]
    [InlineData(429, "429 Too Many Requests", "slow down")]
    [InlineData(500, "500 Internal Server Error", "boom")]
    public async Task CheckQuotaWrapsExecutorStatusFailures(int statusCode, string status, string body)
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new HttpClientError(
                "GET",
                QuotaUrl,
                statusCode,
                status,
                Encoding.UTF8.GetBytes(body)),
        });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.StartsWith("quota request failed: ", error.Message);
        Assert.Contains(statusCode.ToString(CultureInfo.InvariantCulture), error.Message);
    }

    [Fact]
    public async Task CheckQuotaWrapsTransportFailures()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new IOException("connection reset"),
        });

        var error = await Assert.ThrowsAsync<WrappedException>(
            () => checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("quota request failed: connection reset", error.Message);
    }

    [Theory]
    [InlineData(201, "created", "HTTP 201: created")]
    [InlineData(204, "", "HTTP 204: ")]
    [InlineData(302, "redirect", "HTTP 302: redirect")]
    [InlineData(100, "continue", "HTTP 100: continue")]
    public async Task CheckQuotaRejectsNonOkStatusesBelowTheExecutorThreshold(
        int statusCode,
        string body,
        string expectedMessage)
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

        Assert.Equal(expectedMessage, error.Message);
    }

    // ------------------------------------------------------------------
    // Base URL
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaUsesTheCustomBaseUrl()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(
            """{"subscription":{"kwh_included":20,"kwh_used":10,"kwh_remaining":10,"in_overage":false}}""");

        var quota = await checker.CheckQuotaAsync(
            Channel(baseUrl: "https://custom.neuralwatt.com"),
            CancellationToken.None);

        Assert.Equal(
            "https://custom.neuralwatt.com/v1/quota",
            Assert.Single(handler.Requests).Url);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
    }

    [Theory]
    [InlineData("", "https://api.neuralwatt.com/v1/quota")]
    [InlineData("  ", "https://api.neuralwatt.com/v1/quota")]
    [InlineData("  https://api.neuralwatt.com  ", "https://api.neuralwatt.com/v1/quota")]
    [InlineData("https://api.neuralwatt.com", "https://api.neuralwatt.com/v1/quota")]
    [InlineData("https://api.neuralwatt.com/v1/chat", "https://api.neuralwatt.com/v1/quota")]
    [InlineData("https://api.neuralwatt.com/", "https://api.neuralwatt.com/v1/quota")]
    [InlineData("http://api.neuralwatt.com", "https://api.neuralwatt.com/v1/quota")]
    [InlineData("HTTP://API.NEURALWATT.COM/V1", "https://API.NEURALWATT.COM/v1/quota")]
    [InlineData("https://user:pass@api.neuralwatt.com:8443/v1", "https://api.neuralwatt.com:8443/v1/quota")]
    [InlineData("https://api.neuralwatt.com:443", "https://api.neuralwatt.com:443/v1/quota")]
    [InlineData("https://[::1]:8080/v1", "https://[::1]:8080/v1/quota")]
    [InlineData("ftp://host/v1", "ftp://host/v1/quota")]
    [InlineData("//api.neuralwatt.com", "://api.neuralwatt.com/v1/quota")]
    [InlineData("mailto:foo@bar", "mailto:///v1/quota")]
    [InlineData("https:", "https:///v1/quota")]
    [InlineData("://invalid", "https://api.neuralwatt.com/v1/quota")]
    [InlineData("api.neuralwatt.com", "https://api.neuralwatt.com/v1/quota")]
    [InlineData("https://api%2Eneuralwatt.com", "https://api.neuralwatt.com/v1/quota")]
    [InlineData("https://api neuralwatt.com", "https://api.neuralwatt.com/v1/quota")]
    public void BuildQuotaUrlMatchesTheReferenceBaseHandling(string baseUrl, string expected)
    {
        Assert.Equal(expected, NeuralWattQuotaChecker.BuildQuotaUrl(baseUrl));
    }

    // ------------------------------------------------------------------
    // Capability
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("openai", "https://api.neuralwatt.com", true)]
    [InlineData("openai_responses", "https://api.neuralwatt.com", true)]
    [InlineData("openai", "https://api.neuralwatt.com:443", true)]
    [InlineData("openai", "https://us.api.neuralwatt.com", true)]
    [InlineData("openai", "https://api.neuralwatt.com/v1", true)]
    [InlineData("openai", "HTTP://API.NEURALWATT.COM", true)]
    [InlineData("openai", "https://api.openai.com", false)]
    [InlineData("diagnostic", "https://api.neuralwatt.com", false)]
    [InlineData("claudecode", "https://api.neuralwatt.com", false)]
    [InlineData("openai", "https://notapi.neuralwatt.com", false)]
    [InlineData("openai", "https://api.neuralwatt.com.evil.com", false)]
    [InlineData("openai", null, false)]
    public void SupportsChannelRequiresAnOpenAiTypeAndNeuralWattHost(
        string channelType,
        string? baseUrl,
        bool expected)
    {
        var checker = CreateChecker(new ScriptedExecutor());

        Assert.Equal(expected, checker.SupportsChannel(new ChannelRecord
        {
            Type = channelType,
            BaseUrl = baseUrl,
        }));
    }

    // ------------------------------------------------------------------
    // JSON decoding: Go semantics
    // ------------------------------------------------------------------

    [Fact]
    public void ParseUsageResponseTreatsRootNullAsAnEmptyStruct()
    {
        var response = ParseUsageResponse("null");

        Assert.Null(response.Balance);
        Assert.Null(response.Subscription);
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
            + " into Go value of type provider_quota.NeuralWattUsageResponse",
            error.Message);
    }

    [Theory]
    [InlineData("""{"balance":[]}""", "json: cannot unmarshal array into Go struct field NeuralWattUsageResponse.balance of type provider_quota.NeuralWattBalance")]
    [InlineData("""{"balance":5}""", "json: cannot unmarshal number into Go struct field NeuralWattUsageResponse.balance of type provider_quota.NeuralWattBalance")]
    [InlineData("""{"balance":{"credits_remaining_usd":"x"}}""", "json: cannot unmarshal string into Go struct field NeuralWattBalance.balance.credits_remaining_usd of type float64")]
    [InlineData("""{"balance":{"credits_remaining_usd":1e400}}""", "json: cannot unmarshal number 1e400 into Go struct field NeuralWattBalance.balance.credits_remaining_usd of type float64")]
    [InlineData("""{"balance":{"total_credits_usd":[]}}""", "json: cannot unmarshal array into Go struct field NeuralWattBalance.balance.total_credits_usd of type float64")]
    [InlineData("""{"balance":{"accounting_method":5}}""", "json: cannot unmarshal number into Go struct field NeuralWattBalance.balance.accounting_method of type string")]
    [InlineData("""{"subscription":true}""", "json: cannot unmarshal bool into Go struct field NeuralWattUsageResponse.subscription of type provider_quota.NeuralWattSubscription")]
    [InlineData("""{"subscription":{"plan":5}}""", "json: cannot unmarshal number into Go struct field NeuralWattSubscription.subscription.plan of type string")]
    [InlineData("""{"subscription":{"status":{}}}""", "json: cannot unmarshal object into Go struct field NeuralWattSubscription.subscription.status of type string")]
    [InlineData("""{"subscription":{"kwh_included":false}}""", "json: cannot unmarshal bool into Go struct field NeuralWattSubscription.subscription.kwh_included of type float64")]
    [InlineData("""{"subscription":{"kwh_used":{}}}""", "json: cannot unmarshal object into Go struct field NeuralWattSubscription.subscription.kwh_used of type float64")]
    [InlineData("""{"subscription":{"kwh_remaining":"x"}}""", "json: cannot unmarshal string into Go struct field NeuralWattSubscription.subscription.kwh_remaining of type float64")]
    [InlineData("""{"subscription":{"in_overage":"yes"}}""", "json: cannot unmarshal string into Go struct field NeuralWattSubscription.subscription.in_overage of type bool")]
    [InlineData("""{"subscription":{"kwh_reset_date":5}}""", "json: cannot unmarshal number into Go struct field NeuralWattSubscription.subscription.kwh_reset_date of type string")]
    public void ParseUsageResponseReportsGoTypeMismatches(string json, string expectedMessage)
    {
        var error = Assert.Throws<JsonException>(() => ParseUsageResponse(json));

        Assert.Equal(expectedMessage, error.Message);
    }

    [Fact]
    public void ParseUsageResponseMergesDuplicateAndCaseFoldedKeys()
    {
        var response = ParseUsageResponse(
            """{"balance":{"credits_remaining_usd":1},"Balance":{"total_credits_usd":2},"SUBSCRIPTION":{"PLAN":"pro"},"subscription":{"KWH_REMAINING":3}}""");

        Assert.NotNull(response.Balance);
        Assert.Equal(1.0, response.Balance!.CreditsRemainingUsd!.Value, 9);
        Assert.Equal(2.0, response.Balance.TotalCreditsUsd!.Value, 9);
        Assert.NotNull(response.Subscription);
        Assert.Equal("pro", response.Subscription!.Plan);
        Assert.Equal(3.0, response.Subscription.KwhRemaining!.Value, 9);
    }

    [Fact]
    public void ParseUsageResponseResetsObjectsToNullOnExplicitNull()
    {
        var response = ParseUsageResponse(
            """{"balance":{"credits_remaining_usd":1},"balance":null,"subscription":{"plan":"x"},"SUBSCRIPTION":null}""");

        Assert.Null(response.Balance);
        Assert.Null(response.Subscription);
    }

    [Fact]
    public void ParseUsageResponseIgnoresUnknownFields()
    {
        var response = ParseUsageResponse(
            """{"unknown":{"a":1},"another":[1,2],"subscription":{"plan":"pro","extra":"x"},"balance":{"credits_remaining_usd":1,"bonus":2}}""");

        Assert.Equal("pro", response.Subscription!.Plan);
        Assert.Equal(1.0, response.Balance!.CreditsRemainingUsd!.Value, 9);
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
        var checker = new NeuralWattQuotaChecker(direct, factory, new AntigravityTestClock(Now));

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
        var checker = new NeuralWattQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(Channel(settings: "{\"proxy\":{"), CancellationToken.None);

        Assert.Empty(factory.Requests);
        Assert.Single(direct.Requests);
    }

    [Fact]
    public async Task CheckQuotaUsesTheDirectExecutorWithoutASettingsProxy()
    {
        var direct = OkExecutor();
        var factory = new ScriptedProxyExecutorFactory();
        var checker = new NeuralWattQuotaChecker(direct, factory, new AntigravityTestClock(Now));

        await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Empty(factory.Requests);
        Assert.Single(direct.Requests);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static NeuralWattQuotaChecker CreateChecker(
        out ClaudeCodeQuotaCheckerTests.RecordingHttpHandler handler)
    {
        handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new NeuralWattQuotaChecker(
            executor,
            new ScriptedProxyExecutorFactory(),
            new AntigravityTestClock(Now));
    }

    private static NeuralWattQuotaChecker CreateChecker(ScriptedExecutor executor)
        => new(executor, new ScriptedProxyExecutorFactory(), new AntigravityTestClock(Now));

    private static ChannelRecord Channel(
        string? credentials = null,
        string? baseUrl = null,
        string settings = "{}",
        string type = "openai")
        => new()
        {
            Type = type,
            BaseUrl = baseUrl,
            Credentials = credentials ?? """{"apiKey":"test-api-key"}""",
            Settings = settings,
        };

    private static NeuralWattUsageResponse ParseUsageResponse(string json)
        => NeuralWattQuotaChecker.ParseUsageResponse(Encoding.UTF8.GetBytes(json));

    private static ScriptedExecutor OkExecutor(string body = "{}") => new()
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
