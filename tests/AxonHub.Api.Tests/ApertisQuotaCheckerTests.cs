using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Ports internal/server/biz/provider_quota/apertis_checker.go together with
/// apertis_checker_test.go: the billing-credits request derived from the
/// channel host, the API-key precedence, the combined PAYG / credits /
/// subscription status ranking, the three limits that share the
/// apertis_capacity availability group, and the cycle boundary reported as
/// both the reset instant and the limit period start. The wire shape is locked
/// through the real <see cref="HttpExecutor"/> over a recording message
/// handler; a bodyless GET loses its Content-Type on the wire (C206, the
/// checker still sets it on the request data, asserted separately).
/// </summary>
public sealed class ApertisQuotaCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private const string QuotaUrl = "https://api.apertis.ai/v1/dashboard/billing/credits";

    private const string ApiKey = "test-api-key";

    private const string FullBody = """
        {
            "object": "billing_credits",
            "is_subscriber": false,
            "payg": {
                "account_credits": 5.0,
                "token_used": 2.0,
                "token_total": 7.0,
                "token_remaining": 5.0,
                "token_is_unlimited": false
            }
        }
        """;

    // ------------------------------------------------------------------
    // Wire flow
    // ------------------------------------------------------------------

    [Fact]
    public async Task CheckQuotaFetchesTheCreditsEndpointAndMapsThePaygOnlyState()
    {
        var checker = CreateChecker(out var handler);
        handler.OnSend = _ => JsonResponse(FullBody);

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(QuotaUrl, request.Url);
        Assert.Equal("Bearer " + ApiKey, request.Header("Authorization"));
        // C206: the checker sets Content-Type on the request data, but the
        // executor only attaches content headers when a body exists.
        Assert.Equal(string.Empty, request.Header("Content-Type"));
        // The shared executor adds its default Accept (Go would send none).
        Assert.Equal("application/json", request.Header("Accept"));
        Assert.Equal(string.Empty, request.Body);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal("apertis", quota.ProviderType);
        Assert.True(quota.Ready);
        Assert.Null(quota.NextResetAt);

        // Two limits: the PAYG token pool and the positive account credits.
        Assert.Equal(2, quota.Limits.Count);
        var paygLimit = quota.Limits[0];
        Assert.Equal(QuotaLimitTypes.Token, paygLimit.Type);
        Assert.Equal(QuotaWindows.PayAsYouGo, paygLimit.Window);
        Assert.Equal(ApertisQuotaChecker.AvailabilityGroup, paygLimit.AvailabilityGroup);
        Assert.Equal("apertis_capacity", paygLimit.AvailabilityGroup);
        Assert.Equal(ProviderQuotaStatuses.Available, paygLimit.Status);
        Assert.Equal(2.0 / 7.0, paygLimit.UsageRatio, 9);
        Assert.True(paygLimit.Ready);
        Assert.Null(paygLimit.NextResetAt);
        Assert.Null(paygLimit.PeriodStart);

        var creditsLimit = quota.Limits[1];
        Assert.Equal(QuotaLimitTypes.Token, creditsLimit.Type);
        Assert.Equal(QuotaWindows.Credits, creditsLimit.Window);
        Assert.Equal(ApertisQuotaChecker.AvailabilityGroup, creditsLimit.AvailabilityGroup);
        Assert.Equal(ProviderQuotaStatuses.Available, creditsLimit.Status);
        Assert.Equal(0.0, creditsLimit.UsageRatio, 9);
        Assert.True(creditsLimit.Ready);
        Assert.Null(creditsLimit.NextResetAt);

        // rawData keeps the provider's snake_case names; whole floats render
        // without a fraction, as Go does, and absent pointers are omitted.
        Assert.Equal(
            """{"is_subscriber":false,"payg":{"account_credits":5,"token_is_unlimited":false,"token_remaining":5,"token_total":7,"token_used":2}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task CheckQuotaCarriesTheBearerAuthAndContentTypeOnTheRequestData()
    {
        var executor = OkExecutor(FullBody);
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
    // Credentials
    // ------------------------------------------------------------------

    [Fact]
    public async Task MissingApiKeyReturnsUnknownWithoutSendingARequest()
    {
        var executor = new ScriptedExecutor();
        var checker = CreateChecker(executor);

        var quota = await checker.CheckQuotaAsync(Channel(credentials: "{}"), CancellationToken.None);

        Assert.Empty(executor.Requests);
        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.Equal("apertis", quota.ProviderType);
        Assert.False(quota.Ready);
        Assert.Empty(quota.Limits);
        Assert.Null(quota.NextResetAt);
        Assert.Equal("missing API key", quota.RawData["error"]);
        Assert.Equal(
            """{"error":"missing API key"}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task BlankApiKeysEntryReturnsUnknownWithoutSendingARequest()
    {
        // The first APIKeys entry is taken verbatim (no trim), so a blank entry
        // still hits the missing-key branch.
        var executor = new ScriptedExecutor();
        var checker = CreateChecker(executor);

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKeys":[""]}"""),
            CancellationToken.None);

        Assert.Empty(executor.Requests);
        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.Equal("missing API key", quota.RawData["error"]);
    }

    [Fact]
    public async Task CheckQuotaFallsBackToTheFirstApiKeysEntryVerbatim()
    {
        var executor = OkExecutor(FullBody);
        var checker = CreateChecker(executor);

        var quota = await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKeys":["fallback-key","other-key"]}"""),
            CancellationToken.None);

        var request = Assert.Single(executor.Requests);
        Assert.Equal("fallback-key", request.Auth!.ApiKey);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
    }

    [Fact]
    public async Task CheckQuotaTrimsTheLegacyApiKeyAndPrefersItOverApiKeys()
    {
        var executor = OkExecutor(FullBody);
        var checker = CreateChecker(executor);

        await checker.CheckQuotaAsync(
            Channel(credentials: """{"apiKey":"  legacy-key  ","apiKeys":["fallback-key"]}"""),
            CancellationToken.None);

        var request = Assert.Single(executor.Requests);
        Assert.Equal("legacy-key", request.Auth!.ApiKey);
    }

    // ------------------------------------------------------------------
    // Status classification
    // ------------------------------------------------------------------

    [Fact]
    public async Task WarningStateWhenPaygUsageReachesTheThreshold()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """
                {
                    "is_subscriber": false,
                    "payg": {
                        "account_credits": 50.0,
                        "token_used": 9.0,
                        "token_total": 10.0,
                        "token_remaining": 1.0,
                        "token_is_unlimited": false
                    }
                }
                """),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(0.9, quota.Limits[0].UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Limits[0].Status);
    }

    [Fact]
    public async Task ExhaustedStateWhenPaygTokensRunOutWithoutCredits()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok(
                """
                {
                    "is_subscriber": false,
                    "payg": {
                        "account_credits": 0,
                        "token_used": 10.0,
                        "token_total": 10.0,
                        "token_remaining": 0,
                        "token_is_unlimited": false
                    }
                }
                """),
        });

        var quota = await checker.CheckQuotaAsync(Channel(), CancellationToken.None);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaWindows.PayAsYouGo, limit.Window);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);
        Assert.Equal(1.0, limit.UsageRatio, 9);
    }

    [Fact]
    public async Task EmptySourcesAreUnknown()
    {
        var quota = await ParseAsync(
            """{"object":"billing_credits","is_subscriber":false}""");

        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.False(quota.Ready);
        Assert.Empty(quota.Limits);
        Assert.Equal(
            """{"is_subscriber":false}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task EmptySourceObjectsAreUnknown()
    {
        var quota = await ParseAsync(
            """
            {
                "object": "billing_credits",
                "is_subscriber": true,
                "payg": {},
                "subscription": {"status": "active"}
            }
            """);

        Assert.Equal(ProviderQuotaStatuses.Unknown, quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);
        Assert.All(quota.Limits, limit => Assert.Equal(ProviderQuotaStatuses.Unknown, limit.Status));
        Assert.Equal(QuotaWindows.PayAsYouGo, quota.Limits[0].Window);
        Assert.Equal(QuotaWindows.Cycle, quota.Limits[1].Window);
    }

    [Fact]
    public async Task PaygCreditsRemainAvailableWhenTheTokenLimitIsExhausted()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "payg": {
                    "account_credits": 3.0,
                    "token_used": 10.0,
                    "token_total": 10.0,
                    "token_remaining": 0.0,
                    "token_is_unlimited": false
                },
                "subscription": {
                    "status": "active",
                    "cycle_quota_limit": 5000,
                    "cycle_quota_used": 5000,
                    "cycle_quota_remaining": 0,
                    "payg_fallback_enabled": true
                }
            }
            """);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(3, quota.Limits.Count);

        var paygLimit = quota.Limits[0];
        Assert.Equal(QuotaWindows.PayAsYouGo, paygLimit.Window);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, paygLimit.Status);
        Assert.Equal(ApertisQuotaChecker.AvailabilityGroup, paygLimit.AvailabilityGroup);

        var creditsLimit = quota.Limits[1];
        Assert.Equal(QuotaWindows.Credits, creditsLimit.Window);
        Assert.Equal(ProviderQuotaStatuses.Available, creditsLimit.Status);
        Assert.True(creditsLimit.Ready);
        Assert.Equal(ApertisQuotaChecker.AvailabilityGroup, creditsLimit.AvailabilityGroup);
    }

    [Fact]
    public async Task ActiveSubscriptionWithRemainingQuotaSkipsThePaygLimit()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "payg": {
                    "account_credits": 9.98,
                    "token_used": 0.05,
                    "token_total": 1.0,
                    "token_remaining": 0.95,
                    "token_is_unlimited": false
                },
                "subscription": {
                    "plan_type": "lite",
                    "status": "active",
                    "cycle_quota_limit": 600,
                    "cycle_quota_used": 10,
                    "cycle_quota_remaining": 590,
                    "cycle_start": "2099-03-16T10:02:35Z",
                    "cycle_end": "2099-04-16T10:02:35Z",
                    "payg_fallback_enabled": false
                }
            }
            """);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);

        var resetAt = new DateTimeOffset(2099, 4, 16, 10, 2, 35, TimeSpan.Zero);
        Assert.Equal(resetAt, quota.NextResetAt);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaLimitTypes.SubscriptionCycle, limit.Type);
        Assert.Equal(QuotaWindows.Cycle, limit.Window);
        Assert.Equal(ProviderQuotaStatuses.Available, limit.Status);
        Assert.Equal(10.0 / 600.0, limit.UsageRatio, 9);
        Assert.Equal(resetAt, limit.NextResetAt);
        Assert.Equal(
            new DateTimeOffset(2099, 3, 16, 10, 2, 35, TimeSpan.Zero),
            limit.PeriodStart);
    }

    [Fact]
    public async Task SubscriptionWarningAtEightyFivePercent()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "payg": {
                    "account_credits": 100.0,
                    "token_used": 0.5,
                    "token_total": 3.5,
                    "token_remaining": 3.0,
                    "token_is_unlimited": false
                },
                "subscription": {
                    "plan_type": "pro",
                    "status": "active",
                    "cycle_quota_limit": 1000,
                    "cycle_quota_used": 850,
                    "cycle_quota_remaining": 150,
                    "cycle_start": "2099-01-01T00:00:00Z",
                    "cycle_end": "2099-02-01T00:00:00Z",
                    "payg_fallback_enabled": false
                }
            }
            """);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaWindows.Cycle, limit.Window);
        Assert.Equal(ProviderQuotaStatuses.Warning, limit.Status);
        Assert.Equal(0.85, limit.UsageRatio, 9);
    }

    [Fact]
    public async Task SuspendedSubscriptionWithPaygCreditsIsAvailable()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "payg": {
                    "account_credits": 100.0,
                    "token_used": 0.5,
                    "token_total": 3.5,
                    "token_remaining": 3.0,
                    "token_is_unlimited": false
                },
                "subscription": {
                    "plan_type": "pro",
                    "status": "suspended",
                    "cycle_quota_limit": 1000,
                    "cycle_quota_used": 500,
                    "cycle_quota_remaining": 500,
                    "cycle_start": "2099-01-01T00:00:00Z",
                    "cycle_end": "2099-02-01T00:00:00Z",
                    "payg_fallback_enabled": false
                }
            }
            """);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(3, quota.Limits.Count);

        var subscriptionLimit = quota.Limits[2];
        Assert.Equal(QuotaLimitTypes.SubscriptionCycle, subscriptionLimit.Type);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, subscriptionLimit.Status);
        // The suspended branch never computes a ratio.
        Assert.Equal(0.0, subscriptionLimit.UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[0].Status);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[1].Status);
    }

    [Fact]
    public async Task SuspendedSubscriptionWithoutPaygCreditsIsExhausted()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "payg": {
                    "account_credits": 0,
                    "token_used": 10.0,
                    "token_total": 10.0,
                    "token_remaining": 0,
                    "token_is_unlimited": false
                },
                "subscription": {
                    "plan_type": "pro",
                    "status": "suspended",
                    "cycle_quota_limit": 1000,
                    "cycle_quota_used": 500,
                    "cycle_quota_remaining": 500,
                    "cycle_start": "2026-01-01T00:00:00Z",
                    "cycle_end": "2026-02-01T00:00:00Z",
                    "payg_fallback_enabled": false
                }
            }
            """);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);
        var subscriptionLimit = Assert.Single(
            quota.Limits,
            limit => limit.Type == QuotaLimitTypes.SubscriptionCycle);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, subscriptionLimit.Status);
    }

    [Fact]
    public async Task ExhaustedCycleWithPaygFallbackIsAvailable()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "payg": {
                    "account_credits": 3.0,
                    "token_used": 0.5,
                    "token_total": 3.5,
                    "token_remaining": 3.0,
                    "token_is_unlimited": false
                },
                "subscription": {
                    "plan_type": "max",
                    "status": "active",
                    "cycle_quota_limit": 5000,
                    "cycle_quota_used": 5000,
                    "cycle_quota_remaining": 0,
                    "cycle_start": "2026-03-01T00:00:00Z",
                    "cycle_end": "2026-04-01T00:00:00Z",
                    "payg_fallback_enabled": true,
                    "payg_spent_usd": 2.5,
                    "payg_limit_usd": 10.0
                }
            }
            """);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(3, quota.Limits.Count);

        var subscriptionLimit = quota.Limits[2];
        Assert.Equal(QuotaLimitTypes.SubscriptionCycle, subscriptionLimit.Type);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, subscriptionLimit.Status);
        Assert.Equal(1.0, subscriptionLimit.UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[0].Status);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[1].Status);
        Assert.Equal(
            new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero),
            quota.NextResetAt);
    }

    [Fact]
    public async Task ExhaustedCycleWithoutFallbackIsExhausted()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "payg": {
                    "account_credits": 0,
                    "token_used": 10.0,
                    "token_total": 10.0,
                    "token_remaining": 0,
                    "token_is_unlimited": false
                },
                "subscription": {
                    "plan_type": "lite",
                    "status": "active",
                    "cycle_quota_limit": 600,
                    "cycle_quota_used": 600,
                    "cycle_quota_remaining": 0,
                    "cycle_start": "2026-01-01T00:00:00Z",
                    "cycle_end": "2026-02-01T00:00:00Z",
                    "payg_fallback_enabled": false
                }
            }
            """);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);
        Assert.All(quota.Limits, limit => Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status));
    }

    [Fact]
    public async Task UnlimitedPaygDoesNotRescueAnActiveSubscription()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "payg": {
                    "account_credits": 0,
                    "token_used": 0,
                    "token_total": "unlimited",
                    "token_remaining": "unlimited",
                    "token_is_unlimited": true
                },
                "subscription": {
                    "plan_type": "lite",
                    "status": "active",
                    "cycle_quota_limit": 600,
                    "cycle_quota_used": 183,
                    "cycle_quota_remaining": 417,
                    "cycle_start": "2099-05-20T23:28:04Z",
                    "cycle_end": "2099-06-20T23:28:04Z",
                    "payg_fallback_enabled": false
                }
            }
            """);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaLimitTypes.SubscriptionCycle, limit.Type);
        Assert.Equal(ProviderQuotaStatuses.Available, limit.Status);
        Assert.Equal(183.0 / 600.0, limit.UsageRatio, 9);
        Assert.Equal(
            new DateTimeOffset(2099, 6, 20, 23, 28, 4, TimeSpan.Zero),
            quota.NextResetAt);

        // rawData still carries both sources.
        Assert.True(Assert.IsType<bool>(quota.RawData["is_subscriber"]));
        Assert.NotNull(quota.RawData["payg"]);
        Assert.NotNull(quota.RawData["subscription"]);
    }

    [Fact]
    public async Task UnlimitedPaygTokensAreAvailableWithZeroRatio()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": false,
                "payg": {
                    "account_credits": 500.0,
                    "token_used": 87.61,
                    "token_total": "unlimited",
                    "token_remaining": "unlimited",
                    "token_is_unlimited": true
                }
            }
            """);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(2, quota.Limits.Count);
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Limits[0].Status);
        Assert.Equal(0.0, quota.Limits[0].UsageRatio, 9);
        Assert.Equal(
            """{"account_credits":500,"token_is_unlimited":true,"token_remaining":"unlimited","token_total":"unlimited","token_used":87.61}""",
            GoJson.MarshalToString(quota.RawData["payg"]!));
    }

    [Fact]
    public async Task NonSubscriberSubscriptionAffectsTheStatusButNotTheLimits()
    {
        // The reference evaluates the subscription object regardless of
        // is_subscriber, but only builds the cycle limit for subscribers, and
        // only parses cycle_end into NextResetAt for subscribers.
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": false,
                "subscription": {
                    "status": "active",
                    "cycle_quota_limit": 10,
                    "cycle_quota_used": 9,
                    "cycle_quota_remaining": 1,
                    "cycle_start": "2099-01-01T00:00:00Z",
                    "cycle_end": "2099-02-01T00:00:00Z"
                }
            }
            """);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        Assert.Empty(quota.Limits);
        Assert.Null(quota.NextResetAt);
    }

    [Fact]
    public async Task CapitalizedActiveStatusKeepsThePaygLimit()
    {
        // determineApertisStatus folds case, but buildApertisLimits compares
        // Status == "active" exactly, so "Active" does not skip the PAYG limit.
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "payg": {
                    "account_credits": 5.0,
                    "token_used": 2.0,
                    "token_total": 7.0,
                    "token_remaining": 5.0,
                    "token_is_unlimited": false
                },
                "subscription": {
                    "status": "Active",
                    "cycle_quota_limit": 100,
                    "cycle_quota_used": 10,
                    "cycle_quota_remaining": 90,
                    "payg_fallback_enabled": false
                }
            }
            """);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal(3, quota.Limits.Count);
        Assert.Equal(QuotaWindows.PayAsYouGo, quota.Limits[0].Window);
        Assert.Equal(QuotaWindows.Credits, quota.Limits[1].Window);
        Assert.Equal(QuotaWindows.Cycle, quota.Limits[2].Window);
    }

    [Fact]
    public async Task PaygIsNotCheckedForASubscriberWithoutASubscriptionObject()
    {
        // determinePaygStatus is only reached for subscribers when a subscription
        // object exists; the limits are still built even though the explicit
        // status stays unknown, and normalization promotes them.
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "payg": {
                    "account_credits": 5.0,
                    "token_used": 2.0,
                    "token_total": 7.0,
                    "token_remaining": 5.0
                }
            }
            """);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal(2, quota.Limits.Count);
    }

    // ------------------------------------------------------------------
    // Direct status classification
    // ------------------------------------------------------------------

    public static TheoryData<string, ApertisBillingCreditsResponse> StatusCases => new()
    {
        {
            ProviderQuotaStatuses.Unknown,
            StatusResponse(null, null, isSubscriber: false)
        },
        {
            // PAYG is not consulted for a subscriber without a subscription object.
            ProviderQuotaStatuses.Unknown,
            StatusResponse(Payg(credits: 5, used: 2, total: 7.0), null, isSubscriber: true)
        },
        {
            ProviderQuotaStatuses.Available,
            StatusResponse(Payg(credits: 5, used: 2, total: 7.0), null, isSubscriber: false)
        },
        {
            ProviderQuotaStatuses.Exhausted,
            StatusResponse(Payg(credits: 0, used: 0, total: 7.0), null, isSubscriber: false)
        },
        {
            ProviderQuotaStatuses.Warning,
            StatusResponse(Payg(credits: 100, used: 8.5, total: 10.0), null, isSubscriber: false)
        },
        {
            ProviderQuotaStatuses.Available,
            StatusResponse(Payg(credits: 100, used: 10, total: 10.0), null, isSubscriber: false)
        },
        {
            ProviderQuotaStatuses.Unknown,
            StatusResponse(Payg(credits: 0, used: 0, total: "unlimited"), null, isSubscriber: false)
        },
        {
            ProviderQuotaStatuses.Exhausted,
            StatusResponse(Payg(credits: 0, used: 0, total: "12.5"), null, isSubscriber: false)
        },
        {
            ProviderQuotaStatuses.Exhausted,
            StatusResponse(null, Subscription(status: "suspended"), isSubscriber: true)
        },
        {
            ProviderQuotaStatuses.Exhausted,
            StatusResponse(null, Subscription(status: "SUSPENDED"), isSubscriber: true)
        },
        {
            ProviderQuotaStatuses.Available,
            StatusResponse(
                Payg(credits: 5, used: 0, total: 10.0),
                Subscription(status: "Cancelled"),
                isSubscriber: true)
        },
        {
            ProviderQuotaStatuses.Warning,
            StatusResponse(
                null,
                Subscription(status: "active", limit: 10, used: 8, remaining: 2),
                isSubscriber: true)
        },
        {
            // Fallback disabled and quota left: PAYG is not consulted even
            // though its own usage would be exhausted.
            ProviderQuotaStatuses.Warning,
            StatusResponse(
                Payg(credits: 5, used: 100, total: 10.0),
                Subscription(status: "active", limit: 10, used: 8, remaining: 2),
                isSubscriber: true)
        },
        {
            ProviderQuotaStatuses.Available,
            StatusResponse(
                Payg(credits: 0, used: 0, total: null, unlimited: true),
                Subscription(status: "active", limit: 10, used: 10, remaining: 0),
                isSubscriber: true)
        },
        {
            ProviderQuotaStatuses.Exhausted,
            StatusResponse(
                Payg(credits: 0, used: 0, total: 10.0),
                Subscription(status: "active", limit: 10, used: 10, remaining: 0),
                isSubscriber: true)
        },
        {
            // PAYG fallback enabled: a warning PAYG pool cannot lower the
            // available subscription status, and betterStatus keeps available.
            ProviderQuotaStatuses.Available,
            StatusResponse(
                Payg(credits: 5, used: 9, total: 10.0),
                Subscription(
                    status: "active",
                    limit: 10,
                    used: 0,
                    remaining: 10,
                    fallback: true),
                isSubscriber: true)
        },
        {
            // Subscription warning survives; the skipped PAYG pool is ignored.
            ProviderQuotaStatuses.Warning,
            StatusResponse(
                Payg(credits: 0, used: 10, total: 10.0),
                Subscription(status: "active", limit: 10, used: 9, remaining: 1),
                isSubscriber: true)
        },
        {
            ProviderQuotaStatuses.Unknown,
            StatusResponse(
                Payg(credits: 0, used: 0, total: -5.0),
                Subscription(status: "active", limit: 0, used: 0, remaining: 0),
                isSubscriber: true)
        },
        {
            ProviderQuotaStatuses.Unknown,
            StatusResponse(
                null,
                Subscription(status: "active", limit: 0, used: 0, remaining: 0),
                isSubscriber: true)
        },
    };

    [Theory]
    [MemberData(nameof(StatusCases))]
    public void DetermineStatusRanksSubscriptionAndPayg(
        string expected,
        ApertisBillingCreditsResponse response)
        => Assert.Equal(expected, ApertisQuotaChecker.DetermineStatus(response));

    public static TheoryData<ApertisSubscription, string> SubscriptionStatusCases => new()
    {
        { Subscription(status: "suspended"), ProviderQuotaStatuses.Exhausted },
        { Subscription(status: "Suspended", limit: 10, used: 0, remaining: 10), ProviderQuotaStatuses.Exhausted },
        { Subscription(status: "cancelled"), ProviderQuotaStatuses.Exhausted },
        { Subscription(status: "active", limit: 0, used: 0, remaining: 0), ProviderQuotaStatuses.Unknown },
        { Subscription(status: "active", limit: -1, used: 0, remaining: 5), ProviderQuotaStatuses.Unknown },
        { Subscription(status: "active", limit: 10, used: 0, remaining: 0), ProviderQuotaStatuses.Exhausted },
        // remaining <= 0 wins over a warning-level ratio.
        { Subscription(status: "active", limit: 10, used: 9, remaining: 0), ProviderQuotaStatuses.Exhausted },
        { Subscription(status: "active", limit: 10, used: 8, remaining: 2), ProviderQuotaStatuses.Warning },
        { Subscription(status: "active", limit: 10, used: 7, remaining: 3), ProviderQuotaStatuses.Available },
        { Subscription(status: "active", limit: 10, used: -5, remaining: 15), ProviderQuotaStatuses.Available },
        { Subscription(status: "", limit: 1, used: 0, remaining: 1), ProviderQuotaStatuses.Available },
    };

    [Theory]
    [MemberData(nameof(SubscriptionStatusCases))]
    public void DetermineSubscriptionStatusFollowsTheReference(
        ApertisSubscription subscription,
        string expected)
        => Assert.Equal(expected, ApertisQuotaChecker.DetermineSubscriptionStatus(subscription));

    public static TheoryData<ApertisPayg, string> PaygStatusCases => new()
    {
        { Payg(credits: 0, used: 0, total: null, unlimited: true), ProviderQuotaStatuses.Available },
        { Payg(credits: 0, used: 0, total: "garbage", unlimited: true), ProviderQuotaStatuses.Available },
        { Payg(credits: 5, used: 2, total: 7.0), ProviderQuotaStatuses.Available },
        { Payg(credits: 5, used: 5.6, total: 7.0), ProviderQuotaStatuses.Warning },
        { Payg(credits: 5, used: 7, total: 7.0), ProviderQuotaStatuses.Available },
        { Payg(credits: 5, used: 7.1, total: 7.0), ProviderQuotaStatuses.Available },
        { Payg(credits: 5, used: 0, total: null), ProviderQuotaStatuses.Available },
        { Payg(credits: 5, used: 0, total: 0.0), ProviderQuotaStatuses.Available },
        { Payg(credits: 5, used: 0, total: -1.0), ProviderQuotaStatuses.Available },
        { Payg(credits: 0, used: 0, total: null), ProviderQuotaStatuses.Unknown },
        { Payg(credits: 0, used: 0, total: "unlimited"), ProviderQuotaStatuses.Unknown },
        { Payg(credits: 0, used: 0, total: "abc"), ProviderQuotaStatuses.Unknown },
        { Payg(credits: 0, used: 0, total: 0.0), ProviderQuotaStatuses.Unknown },
        { Payg(credits: 0, used: 0, total: -3.0), ProviderQuotaStatuses.Unknown },
        { Payg(credits: 0, used: 0, total: 7.0), ProviderQuotaStatuses.Exhausted },
        { Payg(credits: 0, used: 100, total: 7.0), ProviderQuotaStatuses.Exhausted },
        { Payg(credits: -1, used: 0, total: 7.0), ProviderQuotaStatuses.Exhausted },
        { Payg(credits: 0, used: 0, total: "12"), ProviderQuotaStatuses.Exhausted },
        // fmt.Sscanf("1e", "%f") does not backtrack: the token is invalid.
        { Payg(credits: 0, used: 0, total: "1e"), ProviderQuotaStatuses.Unknown },
        { Payg(credits: 0, used: 0, total: " 12"), ProviderQuotaStatuses.Exhausted },
        { Payg(credits: 0, used: 0, total: true), ProviderQuotaStatuses.Unknown },
    };

    [Theory]
    [MemberData(nameof(PaygStatusCases))]
    public void DeterminePaygStatusFollowsTheReference(ApertisPayg payg, string expected)
        => Assert.Equal(expected, ApertisQuotaChecker.DeterminePaygStatus(payg));

    [Theory]
    [InlineData(ProviderQuotaStatuses.Available, ProviderQuotaStatuses.Warning, ProviderQuotaStatuses.Available)]
    [InlineData(ProviderQuotaStatuses.Warning, ProviderQuotaStatuses.Available, ProviderQuotaStatuses.Available)]
    [InlineData(ProviderQuotaStatuses.Warning, ProviderQuotaStatuses.Exhausted, ProviderQuotaStatuses.Warning)]
    [InlineData(ProviderQuotaStatuses.Exhausted, ProviderQuotaStatuses.Unknown, ProviderQuotaStatuses.Exhausted)]
    [InlineData(ProviderQuotaStatuses.Unknown, ProviderQuotaStatuses.Exhausted, ProviderQuotaStatuses.Exhausted)]
    // Ties keep the first status.
    [InlineData(ProviderQuotaStatuses.Warning, ProviderQuotaStatuses.Warning, ProviderQuotaStatuses.Warning)]
    [InlineData(ProviderQuotaStatuses.Unknown, ProviderQuotaStatuses.Unknown, ProviderQuotaStatuses.Unknown)]
    public void BetterStatusKeepsTheMorePermissiveStatus(
        string first,
        string second,
        string expected)
        => Assert.Equal(expected, ApertisQuotaChecker.BetterStatus(first, second));

    // ------------------------------------------------------------------
    // Limits and normalization
    // ------------------------------------------------------------------

    [Fact]
    public async Task NegativeTokenUsageDropsThePaygLimitButKeepsTheCreditsLimit()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": false,
                "payg": {
                    "account_credits": 5.0,
                    "token_used": -5.0,
                    "token_total": 10.0,
                    "token_remaining": 15.0
                }
            }
            """);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaWindows.Credits, limit.Window);
        Assert.Equal(ProviderQuotaStatuses.Available, limit.Status);
    }

    [Fact]
    public async Task TokenUsageAboveThePoolIsClampedAndForcedExhausted()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": false,
                "payg": {
                    "account_credits": 5.0,
                    "token_used": 15.0,
                    "token_total": 10.0,
                    "token_remaining": 0.0
                }
            }
            """);

        // The explicit PAYG status is available because account_credits > 0.
        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal(2, quota.Limits.Count);
        var paygLimit = quota.Limits[0];
        Assert.Equal(QuotaWindows.PayAsYouGo, paygLimit.Window);
        Assert.Equal(1.0, paygLimit.UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, paygLimit.Status);
    }

    [Fact]
    public async Task SubscriptionRatioAboveOneIsClampedAndForcedExhausted()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "subscription": {
                    "status": "active",
                    "cycle_quota_limit": 10,
                    "cycle_quota_used": 15,
                    "cycle_quota_remaining": 5
                }
            }
            """);

        // determineSubscriptionStatus sees a warning ratio, but the limit is
        // clamped to 1 and forced exhausted; the explicit warning wins overall.
        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(1.0, limit.UsageRatio, 9);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);
    }

    [Fact]
    public async Task ZeroRemainingWithUnusedCycleIsExhausted()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "subscription": {
                    "status": "active",
                    "cycle_quota_limit": 10,
                    "cycle_quota_used": 0,
                    "cycle_quota_remaining": 0
                }
            }
            """);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);
        Assert.Equal(0.0, limit.UsageRatio, 9);
    }

    [Fact]
    public async Task PaygTokenLimitCarriesTheSubscriptionCycleReset()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "payg": {
                    "account_credits": 3.0,
                    "token_used": 1.0,
                    "token_total": 5.0,
                    "token_remaining": 4.0
                },
                "subscription": {
                    "status": "active",
                    "cycle_quota_limit": 100,
                    "cycle_quota_used": 100,
                    "cycle_quota_remaining": 0,
                    "cycle_end": "2099-02-01T00:00:00Z",
                    "payg_fallback_enabled": true
                }
            }
            """);

        var resetAt = new DateTimeOffset(2099, 2, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(resetAt, quota.NextResetAt);
        Assert.Equal(resetAt, quota.Limits[0].NextResetAt);
        Assert.Equal(resetAt, quota.Limits[2].NextResetAt);
        // Credits limits never carry a reset.
        Assert.Null(quota.Limits[1].NextResetAt);
    }

    // ------------------------------------------------------------------
    // Cycle boundaries
    // ------------------------------------------------------------------

    [Fact]
    public async Task NextResetTimeParsing()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "subscription": {
                    "status": "active",
                    "cycle_quota_limit": 600,
                    "cycle_quota_used": 10,
                    "cycle_quota_remaining": 590,
                    "cycle_start": "2099-01-15T00:00:00Z",
                    "cycle_end": "2099-02-15T12:00:00Z"
                }
            }
            """);

        Assert.Equal(
            new DateTimeOffset(2099, 2, 15, 12, 0, 0, TimeSpan.Zero),
            quota.NextResetAt);
    }

    [Fact]
    public async Task InvalidCycleTimesAreIgnored()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "subscription": {
                    "status": "active",
                    "cycle_quota_limit": 10,
                    "cycle_quota_used": 1,
                    "cycle_quota_remaining": 9,
                    "cycle_start": "not-a-time",
                    "cycle_end": "also-not-a-time"
                }
            }
            """);

        Assert.Null(quota.NextResetAt);
        var limit = Assert.Single(quota.Limits);
        Assert.Null(limit.NextResetAt);
        Assert.Null(limit.PeriodStart);
    }

    [Fact]
    public async Task EmptyCycleEndClearsTheResetAndThePeriodStart()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "subscription": {
                    "status": "active",
                    "cycle_quota_limit": 10,
                    "cycle_quota_used": 1,
                    "cycle_quota_remaining": 9,
                    "cycle_start": "2099-01-15T00:00:00Z",
                    "cycle_end": ""
                }
            }
            """);

        Assert.Null(quota.NextResetAt);
        var limit = Assert.Single(quota.Limits);
        Assert.Null(limit.NextResetAt);
        // PeriodStart is cleared together with the limit's reset by normalization.
        Assert.Null(limit.PeriodStart);
    }

    [Fact]
    public async Task FractionalCycleTimesKeepTheHundredNanosecondTicks()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "subscription": {
                    "status": "active",
                    "cycle_quota_limit": 10,
                    "cycle_quota_used": 1,
                    "cycle_quota_remaining": 9,
                    "cycle_start": "2099-01-15T00:00:00.123456789Z",
                    "cycle_end": "2099-02-15T00:00:00.987654321Z"
                }
            }
            """);

        var resetAt = new DateTimeOffset(2099, 2, 15, 0, 0, 0, TimeSpan.Zero)
            .AddTicks(9_876_543);
        Assert.Equal(resetAt, quota.NextResetAt);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(resetAt, limit.NextResetAt);
        Assert.Equal(
            new DateTimeOffset(2099, 1, 15, 0, 0, 0, TimeSpan.Zero).AddTicks(1_234_567),
            limit.PeriodStart);
    }

    [Fact]
    public async Task OffsetCycleTimesKeepTheOffset()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "subscription": {
                    "status": "active",
                    "cycle_quota_limit": 10,
                    "cycle_quota_used": 1,
                    "cycle_quota_remaining": 9,
                    "cycle_start": "2099-01-15T00:00:00+05:30",
                    "cycle_end": "2099-02-15T00:00:00+05:30"
                }
            }
            """);

        var offset = TimeSpan.FromMinutes(330);
        Assert.Equal(new DateTimeOffset(2099, 2, 15, 0, 0, 0, offset), quota.NextResetAt);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(new DateTimeOffset(2099, 1, 15, 0, 0, 0, offset), limit.PeriodStart);
    }

    [Fact]
    public async Task PastCycleEndIsClearedWithThePeriodStart()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "subscription": {
                    "status": "active",
                    "cycle_quota_limit": 10,
                    "cycle_quota_used": 1,
                    "cycle_quota_remaining": 9,
                    "cycle_start": "2025-01-01T00:00:00Z",
                    "cycle_end": "2025-02-01T00:00:00Z"
                }
            }
            """);

        Assert.Null(quota.NextResetAt);
        var limit = Assert.Single(quota.Limits);
        Assert.Null(limit.NextResetAt);
        Assert.Null(limit.PeriodStart);
    }

    // ------------------------------------------------------------------
    // rawData
    // ------------------------------------------------------------------

    [Fact]
    public async Task RawDataContainsAllFields()
    {
        var quota = await ParseAsync(
            """
            {
                "object": "billing_credits",
                "is_subscriber": true,
                "payg": {
                    "account_credits": 9.98,
                    "token_used": 0.05,
                    "token_total": 1.0,
                    "token_remaining": 0.95,
                    "token_is_unlimited": false,
                    "token_monthly_limit_usd": 100.0,
                    "token_monthly_used_usd": 12.50,
                    "monthly_reset_day": 1
                },
                "subscription": {
                    "plan_type": "max",
                    "status": "active",
                    "cycle_quota_limit": 5000,
                    "cycle_quota_used": 5000,
                    "cycle_quota_remaining": 0,
                    "cycle_start": "2026-03-01T00:00:00Z",
                    "cycle_end": "2026-04-01T00:00:00Z",
                    "payg_fallback_enabled": true,
                    "payg_spent_usd": 2.5,
                    "payg_limit_usd": 10.0
                }
            }
            """);

        Assert.Equal(
            """
            {"is_subscriber":true,"payg":{"account_credits":9.98,"monthly_reset_day":1,"token_is_unlimited":false,"token_monthly_limit_usd":100,"token_monthly_used_usd":12.5,"token_remaining":0.95,"token_total":1,"token_used":0.05},"subscription":{"cycle_end":"2026-04-01T00:00:00Z","cycle_quota_limit":5000,"cycle_quota_remaining":0,"cycle_quota_used":5000,"cycle_start":"2026-03-01T00:00:00Z","payg_fallback_enabled":true,"payg_limit_usd":10,"payg_spent_usd":2.5,"plan_type":"max","status":"active"}}
            """.Trim(),
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task EmptyPaygObjectKeepsZeroesAndNullAnyFields()
    {
        var quota = await ParseAsync(
            """{"is_subscriber":false,"payg":{}}""");

        Assert.Equal(
            """{"is_subscriber":false,"payg":{"account_credits":0,"token_is_unlimited":false,"token_remaining":null,"token_total":null,"token_used":0}}""",
            GoJson.MarshalToString(quota.RawData));
    }

    [Fact]
    public async Task DuplicateSubscriptionObjectsMergeInRawData()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "subscription": {"plan_type": "pro"},
                "subscription": {"status": "active"}
            }
            """);

        var subscription = Assert.IsType<Dictionary<string, object?>>(quota.RawData["subscription"]);
        Assert.Equal("pro", subscription["plan_type"]);
        Assert.Equal("active", subscription["status"]);
    }

    [Fact]
    public async Task ExplicitNullResetsThePaygPointer()
    {
        var quota = await ParseAsync(
            """
            {
                "is_subscriber": true,
                "payg": {"account_credits": 5.0},
                "payg": null,
                "subscription": {"status": "active", "cycle_quota_limit": 10, "cycle_quota_remaining": 10}
            }
            """);

        Assert.False(quota.RawData.ContainsKey("payg"));
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaWindows.Cycle, limit.Window);
    }

    [Fact]
    public async Task UnknownFieldsAreIgnored()
    {
        var quota = await ParseAsync(
            """
            {
                "object": "billing_credits",
                "unknown": {"nested": [1, 2, 3]},
                "is_subscriber": false,
                "payg": {
                    "account_credits": 5.0,
                    "token_used": 0,
                    "token_total": 7.0,
                    "token_remaining": 7.0,
                    "something_else": true
                }
            }
            """);

        Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
        Assert.Equal(2, quota.Limits.Count);
        Assert.DoesNotContain("unknown", quota.RawData.Keys);
    }

    // ------------------------------------------------------------------
    // URL construction
    // ------------------------------------------------------------------

    [Fact]
    public async Task CustomBaseUrlIsUsedForTheCreditsEndpoint()
    {
        var executor = OkExecutor(FullBody);
        var checker = CreateChecker(executor);

        await checker.CheckQuotaAsync(
            Channel(baseUrl: "https://custom.apertis.ai"),
            CancellationToken.None);

        Assert.Equal(
            "https://custom.apertis.ai/v1/dashboard/billing/credits",
            Assert.Single(executor.Requests).Url);
    }

    [Theory]
    [InlineData(null, "https://api.apertis.ai/v1/dashboard/billing/credits")]
    [InlineData("", "https://api.apertis.ai/v1/dashboard/billing/credits")]
    [InlineData("   ", "https://api.apertis.ai/v1/dashboard/billing/credits")]
    [InlineData("https://api.apertis.ai", "https://api.apertis.ai/v1/dashboard/billing/credits")]
    [InlineData("https://api.apertis.ai/", "https://api.apertis.ai/v1/dashboard/billing/credits")]
    [InlineData("  https://api.apertis.ai  ", "https://api.apertis.ai/v1/dashboard/billing/credits")]
    [InlineData("https://api.apertis.ai/v1/other?x=1", "https://api.apertis.ai/v1/dashboard/billing/credits")]
    // Unlike NeuralWatt, http is NOT upgraded to https.
    [InlineData("http://api.apertis.ai", "http://api.apertis.ai/v1/dashboard/billing/credits")]
    [InlineData("HTTP://API.APERTIS.AI/V1", "http://API.APERTIS.AI/v1/dashboard/billing/credits")]
    [InlineData("ftp://mirror.local/base", "ftp://mirror.local/v1/dashboard/billing/credits")]
    [InlineData("https://user:pass@api.apertis.ai:8443/v1", "https://api.apertis.ai:8443/v1/dashboard/billing/credits")]
    [InlineData("https://api.apertis.ai:443", "https://api.apertis.ai:443/v1/dashboard/billing/credits")]
    [InlineData("https://api%2Eapertis.ai", "https://api.apertis.ai/v1/dashboard/billing/credits")]
    [InlineData("https://[::1]:8080/v1", "https://[::1]:8080/v1/dashboard/billing/credits")]
    // Empty scheme defaults to https; an empty host is not replaced.
    [InlineData("//api.apertis.ai", "https://api.apertis.ai/v1/dashboard/billing/credits")]
    [InlineData("api.apertis.ai", "https:///v1/dashboard/billing/credits")]
    [InlineData("https:", "https:///v1/dashboard/billing/credits")]
    [InlineData("mailto:foo@bar", "mailto:///v1/dashboard/billing/credits")]
    // Unparsable values fall back to the default base URL.
    [InlineData("://invalid", "https://api.apertis.ai/v1/dashboard/billing/credits")]
    [InlineData("https://api apertis.ai", "https://api.apertis.ai/v1/dashboard/billing/credits")]
    public void BuildQuotaUrlMatchesTheReferenceBaseHandling(string? baseUrl, string expected)
        => Assert.Equal(expected, ApertisQuotaChecker.BuildQuotaUrl(baseUrl));

    // ------------------------------------------------------------------
    // SupportsChannel
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("openai", true)]
    [InlineData("openai_responses", true)]
    [InlineData("claudecode", false)]
    [InlineData("codex", false)]
    [InlineData("apertis", false)]
    public void SupportsChannelChecksOnlyTheChannelType(string type, bool expected)
    {
        var checker = CreateChecker(new ScriptedExecutor());

        Assert.Equal(expected, checker.SupportsChannel(Channel(type: type)));
    }

    [Fact]
    public void SupportsChannelIgnoresTheBaseUrl()
    {
        var checker = CreateChecker(new ScriptedExecutor());

        Assert.True(checker.SupportsChannel(Channel(type: "openai", baseUrl: "https://example.com")));
        Assert.True(checker.SupportsChannel(Channel(type: "openai_responses", baseUrl: null)));
    }

    // ------------------------------------------------------------------
    // Error paths
    // ------------------------------------------------------------------

    [Fact]
    public async Task MalformedJsonThrowsTheDecoderError()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok("{invalid json"),
        });

        // The reference returns the parse error together with discarded data;
        // the service keeps only the error (C211).
        await Assert.ThrowsAsync<JsonException>(
            async () => await checker.CheckQuotaAsync(Channel(), CancellationToken.None));
    }

    [Fact]
    public async Task TypeMismatchesThrowGoStyleDecoderMessages()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => Ok("""{"payg":{"account_credits":"5"}}"""),
        });

        var error = await Assert.ThrowsAsync<JsonException>(
            async () => await checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "json: cannot unmarshal string into Go struct field ApertisPayg.payg.account_credits of type float64",
            error.Message);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task ExecutorStatusFailuresBecomeTheReferenceMessage(int statusCode)
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new HttpClientError(
                "GET",
                QuotaUrl,
                statusCode,
                "error",
                Encoding.UTF8.GetBytes("""{"error":"invalid key"}""")),
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "apertis API returned status " + statusCode.ToString(CultureInfo.InvariantCulture),
            error.Message);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(201)]
    [InlineData(204)]
    [InlineData(302)]
    public async Task NonOkStatusesBelowTheExecutorThresholdUseTheReferenceMessage(int statusCode)
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = statusCode,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
                Body = Encoding.UTF8.GetBytes("unexpected"),
            },
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal(
            "apertis API returned status " + statusCode.ToString(CultureInfo.InvariantCulture),
            error.Message);
    }

    [Fact]
    public async Task TransportFailuresPropagateUnchanged()
    {
        var checker = CreateChecker(new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new IOException("connection reset"),
        });

        var error = await Assert.ThrowsAsync<IOException>(
            async () => await checker.CheckQuotaAsync(Channel(), CancellationToken.None));

        Assert.Equal("connection reset", error.Message);
    }

    // ------------------------------------------------------------------
    // Go JSON decoding
    // ------------------------------------------------------------------

    [Fact]
    public void ParseBillingCreditsResponseTreatsNullRootAsAnEmptyStruct()
    {
        var response = ParseResponse("null");

        Assert.Equal(string.Empty, response.Object);
        Assert.False(response.IsSubscriber);
        Assert.Null(response.Payg);
        Assert.Null(response.Subscription);
    }

    [Theory]
    [InlineData("[]", "array")]
    [InlineData("1", "number")]
    [InlineData("\"text\"", "string")]
    [InlineData("true", "bool")]
    public void ParseBillingCreditsResponseRejectsNonObjectRoots(string json, string kind)
    {
        var error = Assert.Throws<JsonException>(() => ParseResponse(json));

        Assert.Equal(
            "json: cannot unmarshal " + kind + " into Go value of type provider_quota.ApertisBillingCreditsResponse",
            error.Message);
    }

    [Theory]
    [InlineData(
        """{"payg":1}""",
        "json: cannot unmarshal number into Go struct field ApertisBillingCreditsResponse.payg of type provider_quota.ApertisPayg")]
    [InlineData(
        """{"payg":[]}""",
        "json: cannot unmarshal array into Go struct field ApertisBillingCreditsResponse.payg of type provider_quota.ApertisPayg")]
    [InlineData(
        """{"subscription":"x"}""",
        "json: cannot unmarshal string into Go struct field ApertisBillingCreditsResponse.subscription of type provider_quota.ApertisSubscription")]
    [InlineData(
        """{"object":1}""",
        "json: cannot unmarshal number into Go struct field ApertisBillingCreditsResponse.object of type string")]
    [InlineData(
        """{"is_subscriber":"yes"}""",
        "json: cannot unmarshal string into Go struct field ApertisBillingCreditsResponse.is_subscriber of type bool")]
    [InlineData(
        """{"payg":{"account_credits":"5"}}""",
        "json: cannot unmarshal string into Go struct field ApertisPayg.payg.account_credits of type float64")]
    [InlineData(
        """{"payg":{"token_used":true}}""",
        "json: cannot unmarshal bool into Go struct field ApertisPayg.payg.token_used of type float64")]
    [InlineData(
        """{"payg":{"token_total":1e400}}""",
        "json: cannot unmarshal number 1e400 into Go struct field ApertisPayg.payg.token_total of type float64")]
    [InlineData(
        """{"payg":{"token_is_unlimited":1}}""",
        "json: cannot unmarshal number into Go struct field ApertisPayg.payg.token_is_unlimited of type bool")]
    [InlineData(
        """{"payg":{"token_monthly_limit_usd":"x"}}""",
        "json: cannot unmarshal string into Go struct field ApertisPayg.payg.token_monthly_limit_usd of type float64")]
    [InlineData(
        """{"payg":{"monthly_reset_day":1.5}}""",
        "json: cannot unmarshal number 1.5 into Go struct field ApertisPayg.payg.monthly_reset_day of type int")]
    [InlineData(
        """{"payg":{"monthly_reset_day":"1"}}""",
        "json: cannot unmarshal string into Go struct field ApertisPayg.payg.monthly_reset_day of type int")]
    [InlineData(
        """{"subscription":{"cycle_quota_limit":10.5}}""",
        "json: cannot unmarshal number 10.5 into Go struct field ApertisSubscription.subscription.cycle_quota_limit of type int")]
    [InlineData(
        """{"subscription":{"cycle_quota_used":10.0}}""",
        "json: cannot unmarshal number 10.0 into Go struct field ApertisSubscription.subscription.cycle_quota_used of type int")]
    [InlineData(
        """{"subscription":{"cycle_quota_remaining":1e2}}""",
        "json: cannot unmarshal number 1e2 into Go struct field ApertisSubscription.subscription.cycle_quota_remaining of type int")]
    [InlineData(
        """{"subscription":{"cycle_quota_limit":9223372036854775808}}""",
        "json: cannot unmarshal number 9223372036854775808 into Go struct field ApertisSubscription.subscription.cycle_quota_limit of type int")]
    [InlineData(
        """{"subscription":{"status":false}}""",
        "json: cannot unmarshal bool into Go struct field ApertisSubscription.subscription.status of type string")]
    [InlineData(
        """{"subscription":{"payg_fallback_enabled":0}}""",
        "json: cannot unmarshal number into Go struct field ApertisSubscription.subscription.payg_fallback_enabled of type bool")]
    [InlineData(
        """{"subscription":{"payg_spent_usd":[]}}""",
        "json: cannot unmarshal array into Go struct field ApertisSubscription.subscription.payg_spent_usd of type float64")]
    public void ParseBillingCreditsResponseReportsGoTypeMismatches(string json, string expected)
    {
        var error = Assert.Throws<JsonException>(() => ParseResponse(json));

        Assert.Equal(expected, error.Message);
    }

    [Fact]
    public void ParseBillingCreditsResponseMergesDuplicateObjects()
    {
        var response = ParseResponse(
            """
            {
                "payg": {"account_credits": 5.0},
                "payg": {"token_used": 2.0}
            }
            """);

        Assert.NotNull(response.Payg);
        Assert.Equal(5.0, response.Payg!.AccountCredits);
        Assert.Equal(2.0, response.Payg.TokenUsed);
    }

    [Fact]
    public void ParseBillingCreditsResponseTreatsNullScalarsAsNoOps()
    {
        var response = ParseResponse(
            """
            {
                "object": "billing_credits",
                "object": null,
                "is_subscriber": true,
                "is_subscriber": null,
                "payg": {
                    "account_credits": 5.0,
                    "account_credits": null,
                    "token_is_unlimited": true,
                    "token_is_unlimited": null
                }
            }
            """);

        Assert.Equal("billing_credits", response.Object);
        Assert.True(response.IsSubscriber);
        Assert.Equal(5.0, response.Payg!.AccountCredits);
        Assert.True(response.Payg.TokenIsUnlimited);
    }

    [Fact]
    public void ParseBillingCreditsResponseNullResetsPointerFields()
    {
        var response = ParseResponse(
            """
            {
                "payg": {
                    "token_monthly_limit_usd": 2.5,
                    "token_monthly_limit_usd": null,
                    "monthly_reset_day": 3,
                    "monthly_reset_day": null
                }
            }
            """);

        Assert.Null(response.Payg!.TokenMonthlyLimitUsd);
        Assert.Null(response.Payg.MonthlyResetDay);
    }

    [Fact]
    public void ParseBillingCreditsResponseFoldsAsciiKeysOnly()
    {
        var response = ParseResponse(
            """
            {
                "PAYG": {
                    "ACCOUNT_CREDITS": 3.0,
                    "Token_Used": 1.0
                },
                "Subscription": {"Plan_Type": "lite"}
            }
            """);

        Assert.Equal(3.0, response.Payg!.AccountCredits);
        Assert.Equal(1.0, response.Payg.TokenUsed);
        Assert.Equal("lite", response.Subscription!.PlanType);
    }

    [Fact]
    public void ParseBillingCreditsResponseKeepsAnyValueShapes()
    {
        var response = ParseResponse(
            """
            {
                "payg": {
                    "token_total": true,
                    "token_remaining": [1, "x", null, {"a": 2}]
                }
            }
            """);

        Assert.True(Assert.IsType<bool>(response.Payg!.TokenTotal));
        var remaining = Assert.IsType<List<object?>>(response.Payg.TokenRemaining);
        Assert.Equal(4, remaining.Count);
        Assert.Equal(1.0, Assert.IsType<double>(remaining[0]));
        Assert.Equal("x", Assert.IsType<string>(remaining[1]));
        Assert.Null(remaining[2]);
        var nested = Assert.IsType<Dictionary<string, object?>>(remaining[3]);
        Assert.Equal(2.0, Assert.IsType<double>(nested["a"]));
    }

    [Fact]
    public void ParseBillingCreditsResponseAccepts64BitInts()
    {
        var response = ParseResponse(
            """
            {
                "subscription": {
                    "cycle_quota_limit": 2147483648,
                    "cycle_quota_used": -5,
                    "cycle_quota_remaining": 9223372036854775807
                },
                "payg": {"monthly_reset_day": -1}
            }
            """);

        Assert.Equal(2147483648L, response.Subscription!.CycleQuotaLimit);
        Assert.Equal(-5L, response.Subscription.CycleQuotaUsed);
        Assert.Equal(long.MaxValue, response.Subscription.CycleQuotaRemaining);
        Assert.Equal(-1L, response.Payg!.MonthlyResetDay);
    }

    [Fact]
    public void ParseBillingCreditsResponseIgnoresUnknownKeys()
    {
        var response = ParseResponse(
            """{"object":"x","extra":{"a":1},"payg":{"unknown":true,"account_credits":1.5}}""");

        Assert.Equal("x", response.Object);
        Assert.Equal(1.5, response.Payg!.AccountCredits);
    }

    // ------------------------------------------------------------------
    // toFloat64 / fmt.Sscanf semantics
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("7", 7.0)]
    [InlineData(" 7.5abc", 7.5)]
    [InlineData("+5", 5.0)]
    [InlineData(".5x", 0.5)]
    [InlineData("1.", 1.0)]
    [InlineData("1e5x", 100000.0)]
    [InlineData("1.2.3", 1.2)]
    [InlineData("1_000", 1000.0)]
    [InlineData("0x1p3zz", 8.0)]
    public void TryToFloat64ScansNumericStringPrefixes(string input, double expected)
    {
        Assert.True(ApertisQuotaChecker.TryToFloat64(input, out var value));
        Assert.Equal(expected, value, 9);
    }

    [Theory]
    [InlineData("unlimited")]
    [InlineData("unlimited2")]
    [InlineData("unlimited ")]
    [InlineData("abc")]
    [InlineData("1e")]
    [InlineData("1e+")]
    [InlineData("0x")]
    [InlineData("0x1p")]
    [InlineData("")]
    [InlineData("   ")]
    public void TryToFloat64RejectsNonNumericStrings(string input)
    {
        Assert.False(ApertisQuotaChecker.TryToFloat64(input, out var value));
        Assert.Equal(0.0, value);
    }

    [Fact]
    public void TryToFloat64HandlesSpecialAndMalformedTokens()
    {
        Assert.True(ApertisQuotaChecker.TryToFloat64("NaN", out var nan));
        Assert.True(double.IsNaN(nan));

        Assert.True(ApertisQuotaChecker.TryToFloat64("-Inf", out var negativeInfinity));
        Assert.True(double.IsNegativeInfinity(negativeInfinity));

        Assert.True(ApertisQuotaChecker.TryToFloat64("Infinity", out var infinity));
        Assert.True(double.IsPositiveInfinity(infinity));

        // fmt.Sscanf does not backtrack over the malformed exponent.
        Assert.False(ApertisQuotaChecker.TryToFloat64("1e400", out _));
    }

    [Fact]
    public void TryToFloat64AcceptsOnlyTheReferenceGoTypes()
    {
        Assert.True(ApertisQuotaChecker.TryToFloat64(7.5, out var fromDouble));
        Assert.Equal(7.5, fromDouble);

        Assert.True(ApertisQuotaChecker.TryToFloat64(7, out var fromInt));
        Assert.Equal(7.0, fromInt);

        Assert.True(ApertisQuotaChecker.TryToFloat64(7L, out var fromLong));
        Assert.Equal(7.0, fromLong);

        Assert.True(ApertisQuotaChecker.TryToFloat64(7.5f, out var fromFloat));
        Assert.Equal(7.5, fromFloat, 6);

        Assert.False(ApertisQuotaChecker.TryToFloat64(null, out _));
        Assert.False(ApertisQuotaChecker.TryToFloat64(true, out _));
        Assert.False(ApertisQuotaChecker.TryToFloat64(7m, out _));
        Assert.False(ApertisQuotaChecker.TryToFloat64(new List<object?>(), out _));
        Assert.False(ApertisQuotaChecker.TryToFloat64(
            new Dictionary<string, object?>(StringComparer.Ordinal),
            out _));
    }

    [Fact]
    public async Task NumericStringTokenTotalsDriveTheStatus()
    {
        var quota = await ParseAsync(
            """{"is_subscriber":false,"payg":{"account_credits":0,"token_used":0,"token_total":"12.5abc"}}""");

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        var limit = Assert.Single(quota.Limits);
        Assert.Equal(QuotaWindows.PayAsYouGo, limit.Window);
        Assert.Equal(0.0, limit.UsageRatio, 9);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static ApertisQuotaChecker CreateChecker(
        out ClaudeCodeQuotaCheckerTests.RecordingHttpHandler handler)
    {
        handler = new ClaudeCodeQuotaCheckerTests.RecordingHttpHandler();
        var executor = new HttpExecutor(new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        });
        return new ApertisQuotaChecker(executor, new AntigravityTestClock(Now));
    }

    private static ApertisQuotaChecker CreateChecker(ScriptedExecutor executor)
        => new(executor, new AntigravityTestClock(Now));

    private static ChannelRecord Channel(
        string? credentials = null,
        string? baseUrl = null,
        string type = "openai")
        => new()
        {
            Type = type,
            BaseUrl = baseUrl,
            Credentials = credentials ?? """{"apiKey":"test-api-key"}""",
        };

    private static Task<QuotaData> ParseAsync(string json)
        => Task.FromResult(
            ApertisQuotaChecker.ParseResponse(Encoding.UTF8.GetBytes(json), Now));

    private static ApertisBillingCreditsResponse ParseResponse(string json)
        => ApertisQuotaChecker.ParseBillingCreditsResponse(Encoding.UTF8.GetBytes(json));

    private static ScriptedExecutor OkExecutor(string body) => new()
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

    private static ApertisBillingCreditsResponse StatusResponse(
        ApertisPayg? payg,
        ApertisSubscription? subscription,
        bool isSubscriber) => new()
    {
        IsSubscriber = isSubscriber,
        Payg = payg,
        Subscription = subscription,
    };

    private static ApertisPayg Payg(
        double credits,
        double used,
        object? total,
        bool unlimited = false) => new()
    {
        AccountCredits = credits,
        TokenUsed = used,
        TokenTotal = total,
        TokenIsUnlimited = unlimited,
    };

    private static ApertisSubscription Subscription(
        string status,
        long limit = 1000,
        long used = 500,
        long remaining = 500,
        bool fallback = false) => new()
    {
        Status = status,
        CycleQuotaLimit = limit,
        CycleQuotaUsed = used,
        CycleQuotaRemaining = remaining,
        PaygFallbackEnabled = fallback,
    };
}
