using AxonHub.Api.Orchestration;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the checker-boundary contract: provider_quota/normalization.go,
/// provider_quota/model_filter.go, routing.go EffectiveStatus and the
/// ProviderQuotaService statics (provider_quota.go:85-126, 482-540,
/// 1097-1219; provider_quota_account.go:25-37). These are the pure functions
/// every provider checker in the follow-up slices is judged against.
/// </summary>
public sealed class ProviderQuotaDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NormalizeClampsRatiosAndDropsMalformedIdentities()
    {
        // provider_quota/normalization_contract_test.go:
        // TestQuotaCheckers_NormalizeRatioAndStatusBoundaries.
        var quota = ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = ProviderQuotaStatuses.Unknown,
                NextResetAt = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
                Limits =
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Window = QuotaWindows.FiveHours,
                        UsageRatio = 1.5,
                        Status = ProviderQuotaStatuses.Available,
                    },
                    // Go's zero Type is ""; a limit without an identity is dropped.
                    new QuotaLimitStatus
                    {
                        Type = string.Empty,
                        Window = QuotaWindows.Weekly,
                        UsageRatio = 0.2,
                        Status = ProviderQuotaStatuses.Available,
                    },
                ],
            },
            Now);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal(1, limit.UsageRatio);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, limit.Status);
        Assert.False(limit.Ready);

        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Status);
        Assert.False(quota.Ready);
        Assert.Null(quota.NextResetAt);
    }

    [Fact]
    public void NormalizeClearsExpiredTimingAndKeepsAGroupedReadyStatusOverExhaustion()
    {
        // TestQuotaCheckers_ExpiryAndStatusPrecedence.
        var expiredReset = Now.AddMinutes(-1);
        var expiredPeriodStart = expiredReset.AddHours(-5);
        var futureReset = Now.AddHours(1);

        var quota = ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = ProviderQuotaStatuses.Warning,
                Limits =
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Window = QuotaWindows.Weekly,
                        UsageRatio = 0.5,
                        Status = ProviderQuotaStatuses.Warning,
                        NextResetAt = expiredReset,
                        PeriodStart = expiredPeriodStart,
                    },
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Window = QuotaWindows.FiveHours,
                        UsageRatio = 1.2,
                        Status = ProviderQuotaStatuses.Available,
                        NextResetAt = futureReset,
                    },
                ],
            },
            Now);

        Assert.Equal(2, quota.Limits.Count);
        Assert.Null(quota.Limits[0].NextResetAt);
        Assert.Null(quota.Limits[0].PeriodStart);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, quota.Limits[1].Status);
        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
        Assert.Equal(futureReset, quota.NextResetAt);
    }

    [Fact]
    public void NormalizeOmitsMalformedRatiosAndInvalidPeriodStarts()
    {
        // TestQuotaCheckers_MalformedRatios.
        var resetAt = new DateTimeOffset(2099, 9, 11, 10, 0, 0, TimeSpan.Zero);

        var quota = ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = ProviderQuotaStatuses.Unknown,
                Limits =
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Window = "empty-window",
                        UsageRatio = double.NaN,
                        Status = ProviderQuotaStatuses.Available,
                    },
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Window = "infinite-ratio",
                        UsageRatio = double.PositiveInfinity,
                        Status = ProviderQuotaStatuses.Available,
                    },
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Window = "negative-ratio",
                        UsageRatio = -0.1,
                        Status = ProviderQuotaStatuses.Available,
                    },
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Window = "bad-period",
                        UsageRatio = 0.2,
                        Status = ProviderQuotaStatuses.Available,
                        NextResetAt = resetAt,
                        PeriodStart = resetAt.AddHours(1),
                    },
                ],
            },
            Now);

        var limit = Assert.Single(quota.Limits);
        Assert.Equal("bad-period", limit.Window);
        Assert.Null(limit.PeriodStart);
    }

    [Fact]
    public void NormalizeDerivesTheOverallStatusFromReadyLimits()
    {
        // TestNormalizeQuotaData_OverallStatusUsesReadyLimitOR.
        var cases = new (string Name, QuotaLimitStatus[] Limits, string Status, bool Ready)[]
        {
            (
                "exhausted and available",
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.SubscriptionCycle,
                        Window = QuotaWindows.Cycle,
                        UsageRatio = 1,
                        Status = ProviderQuotaStatuses.Exhausted,
                    },
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Window = QuotaWindows.PayAsYouGo,
                        UsageRatio = 0.2,
                        Status = ProviderQuotaStatuses.Available,
                    },
                ],
                ProviderQuotaStatuses.Available,
                true),
            (
                "exhausted and warning",
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.SubscriptionCycle,
                        Window = QuotaWindows.Cycle,
                        UsageRatio = 1,
                        Status = ProviderQuotaStatuses.Exhausted,
                    },
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Window = QuotaWindows.PayAsYouGo,
                        UsageRatio = 0.9,
                        Status = ProviderQuotaStatuses.Warning,
                    },
                ],
                ProviderQuotaStatuses.Warning,
                true),
            (
                "all exhausted",
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.SubscriptionCycle,
                        Window = QuotaWindows.Cycle,
                        UsageRatio = 1,
                        Status = ProviderQuotaStatuses.Exhausted,
                    },
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Window = QuotaWindows.PayAsYouGo,
                        UsageRatio = 1,
                        Status = ProviderQuotaStatuses.Exhausted,
                    },
                ],
                ProviderQuotaStatuses.Exhausted,
                false),
            (
                "unknown only",
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Window = QuotaWindows.PayAsYouGo,
                        UsageRatio = 0,
                        Status = ProviderQuotaStatuses.Unknown,
                    },
                ],
                ProviderQuotaStatuses.Unknown,
                false),
        };

        foreach (var testCase in cases)
        {
            var quota = ProviderQuotaData.Normalize(
                new QuotaData
                {
                    Status = ProviderQuotaStatuses.Unknown,
                    Limits = [.. testCase.Limits],
                },
                Now);

            Assert.Equal(testCase.Status, quota.Status);
            Assert.Equal(testCase.Ready, quota.Ready);
        }
    }

    [Fact]
    public void NormalizeKeepsAnExplicitReadyStatusOverAnExhaustedLimit()
    {
        // TestNormalizeQuotaData_ExplicitStatusContributesReadyState.
        var quota = ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = ProviderQuotaStatuses.Warning,
                Limits =
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Window = QuotaWindows.FiveHours,
                        UsageRatio = 1,
                        Status = ProviderQuotaStatuses.Exhausted,
                    },
                ],
            },
            Now);

        Assert.Equal(ProviderQuotaStatuses.Warning, quota.Status);
        Assert.True(quota.Ready);
    }

    [Fact]
    public void NormalizeKeepsDisplayOnlySearchLimitsOutOfTheChannelStatus()
    {
        // TestQuotaCheckers_DisplayOnlyLimitsKeepTheirRowButNotTheChannel.
        var weeklyReset = Now.AddHours(72);

        foreach (var (status, ratio) in new[]
                 {
                     (ProviderQuotaStatuses.Exhausted, 1.0),
                     (ProviderQuotaStatuses.Warning, 0.9),
                 })
        {
            var quota = ProviderQuotaData.Normalize(
                new QuotaData
                {
                    Status = ProviderQuotaStatuses.Unknown,
                    Limits =
                    [
                        new QuotaLimitStatus
                        {
                            Type = QuotaLimitTypes.Token,
                            Window = QuotaWindows.Weekly,
                            Status = ProviderQuotaStatuses.Available,
                            UsageRatio = 0.2,
                            NextResetAt = weeklyReset,
                        },
                        new QuotaLimitStatus
                        {
                            Type = QuotaLimitTypes.Search,
                            Window = "search_hourly",
                            Status = status,
                            UsageRatio = ratio,
                            NextResetAt = Now.AddMinutes(30),
                        },
                    ],
                },
                Now);

            Assert.Equal(2, quota.Limits.Count);
            Assert.Equal(status, quota.Limits[1].Status);
            Assert.Equal(ProviderQuotaStatuses.Available, quota.Status);
            Assert.True(quota.Ready);
            Assert.Equal(weeklyReset, quota.NextResetAt);
        }

        // A search-only payload must not report the channel as exhausted either.
        var onlySearch = ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = ProviderQuotaStatuses.Unknown,
                Limits =
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Search,
                        Window = "search_hourly",
                        Status = ProviderQuotaStatuses.Exhausted,
                        UsageRatio = 1,
                    },
                ],
            },
            Now);

        Assert.Equal(ProviderQuotaStatuses.Unknown, onlySearch.Status);
        Assert.False(onlySearch.Ready);
        Assert.Null(onlySearch.NextResetAt);
    }

    [Fact]
    public void FilterLimitsForModelKeepsTheModelPoolAndGlobalLimits()
    {
        // provider_quota/model_filter_test.go: TestFilterLimitsForModel.
        var antigravityPoolPrefix = "antigravity_pool:";
        var limits = new List<QuotaLimitStatus>
        {
            new()
            {
                Type = QuotaLimitTypes.Token,
                Status = ProviderQuotaStatuses.Available,
                UsageRatio = 0.1,
                Window = "gemini_7d",
                AvailabilityGroup = antigravityPoolPrefix + "gemini",
            },
            new()
            {
                Type = QuotaLimitTypes.Token,
                Status = ProviderQuotaStatuses.Exhausted,
                UsageRatio = 1.0,
                Window = "claude_gpt_7d",
                AvailabilityGroup = antigravityPoolPrefix + "claude_gpt",
            },
            new()
            {
                Type = QuotaLimitTypes.Token,
                Status = ProviderQuotaStatuses.Available,
                UsageRatio = 0.2,
                Window = "global_rate",
            },
        };

        var geminiFiltered = ProviderQuotaModelFilter.FilterLimitsForModel(
            limits,
            "gemini-3.8-flash");
        Assert.Equal(2, geminiFiltered.Count);
        Assert.Equal("gemini_7d", geminiFiltered[0].Window);
        Assert.Equal("global_rate", geminiFiltered[1].Window);

        var claudeFiltered = ProviderQuotaModelFilter.FilterLimitsForModel(
            limits,
            "claude-sonnet-5-5");
        Assert.Equal(2, claudeFiltered.Count);
        Assert.Equal("claude_gpt_7d", claudeFiltered[0].Window);
        Assert.Equal("global_rate", claudeFiltered[1].Window);

        var geminiState = ProviderQuotaRouting.EvaluateQuotaRouting(
            geminiFiltered,
            ProviderQuotaStatuses.Available,
            QuotaLimitTypes.Token,
            Now);
        Assert.Equal(QuotaRoutingStates.Open, geminiState.State);

        var claudeState = ProviderQuotaRouting.EvaluateQuotaRouting(
            claudeFiltered,
            ProviderQuotaStatuses.Available,
            QuotaLimitTypes.Token,
            Now);
        Assert.Equal(QuotaRoutingStates.Exhausted, claudeState.State);
    }

    [Fact]
    public void EffectiveStatusFallsBackToTheChannelStatusWithoutLimits()
    {
        // quota_channel_status_test.go: TestQuotaChannelStatus_EffectiveStatus_NoLimits.
        var (status, ready) = ProviderQuotaData.EffectiveStatus(
            null,
            ProviderQuotaStatuses.Warning,
            overallReady: true,
            QuotaLimitTypes.Token);

        Assert.Equal(ProviderQuotaStatuses.Warning, status);
        Assert.True(ready);
    }

    [Fact]
    public void EffectiveStatusSeparatesImageAndTokenLimits()
    {
        // TestQuotaChannelStatus_EffectiveStatus_ImageExhausted_TokenAvailable.
        List<QuotaLimitStatus> limits =
        [
            new()
            {
                Type = QuotaLimitTypes.Image,
                Status = ProviderQuotaStatuses.Exhausted,
                UsageRatio = 1.0,
                Ready = false,
            },
            new()
            {
                Type = QuotaLimitTypes.Token,
                Status = ProviderQuotaStatuses.Available,
                UsageRatio = 0.3,
                Ready = true,
            },
        ];

        Assert.Equal(
            (ProviderQuotaStatuses.Exhausted, false),
            ProviderQuotaData.EffectiveStatus(
                limits, ProviderQuotaStatuses.Warning, true, QuotaLimitTypes.Image));
        Assert.Equal(
            (ProviderQuotaStatuses.Available, true),
            ProviderQuotaData.EffectiveStatus(
                limits, ProviderQuotaStatuses.Warning, true, QuotaLimitTypes.Token));
    }

    [Fact]
    public void EffectiveStatusTakesTheWorstOfMultipleTokenLimits()
    {
        // TestQuotaChannelStatus_EffectiveStatus_MultipleTokenLimits_WorstWins.
        List<QuotaLimitStatus> limits =
        [
            new()
            {
                Type = QuotaLimitTypes.Token,
                Status = ProviderQuotaStatuses.Available,
                UsageRatio = 0.3,
                Ready = true,
            },
            new()
            {
                Type = QuotaLimitTypes.Token,
                Status = ProviderQuotaStatuses.Warning,
                UsageRatio = 0.85,
                Ready = true,
            },
        ];

        Assert.Equal(
            (ProviderQuotaStatuses.Warning, true),
            ProviderQuotaData.EffectiveStatus(
                limits, ProviderQuotaStatuses.Warning, true, QuotaLimitTypes.Token));
    }

    [Fact]
    public void EffectiveStatusCombinesAlternativeCapacitySourcesWithOr()
    {
        // TestQuotaChannelStatus_EffectiveStatus_AlternativeCapacitySources.
        List<QuotaLimitStatus> limits =
        [
            new()
            {
                Type = QuotaLimitTypes.Token,
                Status = ProviderQuotaStatuses.Exhausted,
                Ready = false,
                AvailabilityGroup = "capacity",
            },
            new()
            {
                Type = QuotaLimitTypes.SubscriptionCycle,
                Status = ProviderQuotaStatuses.Available,
                Ready = true,
                AvailabilityGroup = "capacity",
            },
        ];

        Assert.Equal(
            (ProviderQuotaStatuses.Available, true),
            ProviderQuotaData.EffectiveStatus(
                limits, ProviderQuotaStatuses.Available, true, QuotaLimitTypes.Token));
    }

    [Fact]
    public void QuotaErrorBackoffDoublesAndSaturatesAtEightTimes()
    {
        // provider_quota_backoff_test.go: TestQuotaErrorBackoff and
        // TestQuotaErrorBackoffSaturation.
        var @base = TimeSpan.FromMinutes(5);

        Assert.Equal(@base, ProviderQuotaService.QuotaErrorBackoff(@base, 0));
        Assert.Equal(@base, ProviderQuotaService.QuotaErrorBackoff(@base, 1));
        Assert.Equal(2 * @base, ProviderQuotaService.QuotaErrorBackoff(@base, 2));
        Assert.Equal(4 * @base, ProviderQuotaService.QuotaErrorBackoff(@base, 3));
        Assert.Equal(8 * @base, ProviderQuotaService.QuotaErrorBackoff(@base, 4));
        Assert.Equal(8 * @base, ProviderQuotaService.QuotaErrorBackoff(@base, 5));
        Assert.Equal(8 * @base, ProviderQuotaService.QuotaErrorBackoff(@base, 100));

        var minute = TimeSpan.FromMinutes(1);
        var capped = ProviderQuotaService.MaxQuotaErrorBackoffMultiplier * minute;
        Assert.Equal(
            capped,
            ProviderQuotaService.QuotaErrorBackoff(
                minute,
                ProviderQuotaService.MaxQuotaErrorBackoffSteps));
        Assert.True(
            ProviderQuotaService.QuotaErrorBackoff(
                minute,
                ProviderQuotaService.MaxQuotaErrorBackoffSteps - 1) < capped);
    }

    [Fact]
    public void QuotaErrorCountReadsIntegralJsonNumbersOnly()
    {
        // provider_quota_backoff_test.go: TestQuotaErrorCount.
        Assert.Equal(0, ProviderQuotaService.QuotaErrorCount(null));
        Assert.Equal(0, ProviderQuotaService.QuotaErrorCount("{}"));
        Assert.Equal(0, ProviderQuotaService.QuotaErrorCount("not-json"));
        Assert.Equal(3, ProviderQuotaService.QuotaErrorCount("""{"error_count":3}"""));
        Assert.Equal(5, ProviderQuotaService.QuotaErrorCount("""{"error_count":5}"""));
        // Reloaded from the DB, JSON numbers arrive as floating point.
        Assert.Equal(4, ProviderQuotaService.QuotaErrorCount("""{"error_count":4.0}"""));
    }

    [Fact]
    public void NextQuotaErrorCountClampsAtTheBackoffSaturationPoint()
    {
        // provider_quota_backoff_test.go: TestNextQuotaErrorCount.
        Assert.Equal(1, ProviderQuotaService.NextQuotaErrorCount(0));
        Assert.Equal(2, ProviderQuotaService.NextQuotaErrorCount(1));
        Assert.Equal(
            ProviderQuotaService.MaxQuotaErrorBackoffSteps,
            ProviderQuotaService.NextQuotaErrorCount(
                ProviderQuotaService.MaxQuotaErrorBackoffSteps - 1));
        Assert.Equal(
            ProviderQuotaService.MaxQuotaErrorBackoffSteps,
            ProviderQuotaService.NextQuotaErrorCount(
                ProviderQuotaService.MaxQuotaErrorBackoffSteps));
        Assert.Equal(
            ProviderQuotaService.MaxQuotaErrorBackoffSteps,
            ProviderQuotaService.NextQuotaErrorCount(100));
    }

    [Theory]
    [InlineData(5, "*/5 * * * *")]
    [InlineData(60, "0 * * * *")]
    [InlineData(120, "0 */2 * * *")]
    [InlineData(1440, "0 */24 * * *")]
    [InlineData(30, "*/30 * * * *")]
    [InlineData(7, "*/6 * * * *")]
    [InlineData(90, "*/60 * * * *")]
    [InlineData(0, "*/60 * * * *")]
    public void IntervalToCronExprRoundsDownToASupportedInterval(int minutes, string expected)
    {
        // provider_quota.go:482-516.
        Assert.Equal(
            expected,
            ProviderQuotaService.IntervalToCronExpr(TimeSpan.FromMinutes(minutes)));
    }

    [Fact]
    public void MergeAndExtractLimitsRoundTripsTheStoredDocument()
    {
        // provider_quota.go:1097-1219.
        var resetAt = new DateTimeOffset(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);
        var periodStart = new DateTimeOffset(2026, 9, 4, 5, 0, 0, TimeSpan.Zero);
        var quota = new QuotaData
        {
            RawData = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["email"] = "user@example.com",
            },
            Resets = new ResetList
            {
                Supported = true,
                Resets =
                [
                    new QuotaReset
                    {
                        Id = "reset-1",
                        Status = "available",
                        Type = "codex_rate_limits",
                        GrantedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
                        ExpiresAt = new DateTimeOffset(2026, 9, 8, 8, 30, 0, TimeSpan.FromHours(8))
                            .AddTicks(1_230_000),
                        Title = "Weekly reset",
                    },
                ],
            },
            Limits =
            [
                new QuotaLimitStatus
                {
                    Type = QuotaLimitTypes.Token,
                    Window = QuotaWindows.FiveHours,
                    Status = ProviderQuotaStatuses.Warning,
                    UsageRatio = 0.85,
                    Ready = true,
                    NextResetAt = resetAt,
                    PeriodStart = periodStart,
                    PeriodCost = 12.5,
                    PeriodQuota = 50,
                    Account = "acct",
                    AvailabilityGroup = "pool",
                },
            ],
        };

        var merged = ProviderQuotaService.MergeLimitsIntoQuotaData(quota);

        using (var document = System.Text.Json.JsonDocument.Parse(merged))
        {
            var root = document.RootElement;
            Assert.Equal("user@example.com", root.GetProperty("email").GetString());

            // Go marshals Reset times with time.Time.MarshalJSON: RFC3339Nano,
            // "Z" for a zero offset and the parsed offset otherwise.
            var resets = root.GetProperty("_resets");
            Assert.True(resets.GetProperty("supported").GetBoolean());
            var reset = resets.GetProperty("resets")[0];
            Assert.Equal("reset-1", reset.GetProperty("id").GetString());
            Assert.Equal("available", reset.GetProperty("status").GetString());
            Assert.Equal("codex_rate_limits", reset.GetProperty("type").GetString());
            Assert.Equal("2026-09-01T00:00:00Z", reset.GetProperty("grantedAt").GetString());
            Assert.Equal("2026-09-08T08:30:00.123+08:00", reset.GetProperty("expiresAt").GetString());
            Assert.Equal("Weekly reset", reset.GetProperty("title").GetString());
            Assert.False(resets.TryGetProperty("error", out _));

            var stored = root.GetProperty("_limits")[0];
            Assert.Equal("token", stored.GetProperty("type").GetString());
            Assert.Equal("warning", stored.GetProperty("status").GetString());
            Assert.Equal(0.85, stored.GetProperty("usageRatio").GetDouble());
            Assert.True(stored.GetProperty("ready").GetBoolean());
            Assert.Equal("2026-09-05T10:00:00Z", stored.GetProperty("nextResetAt").GetString());
            Assert.Equal("5h", stored.GetProperty("window").GetString());
            Assert.Equal("acct", stored.GetProperty("account").GetString());
            Assert.Equal("pool", stored.GetProperty("availabilityGroup").GetString());
            Assert.Equal("2026-09-04T05:00:00Z", stored.GetProperty("periodStart").GetString());
            Assert.Equal(12.5, stored.GetProperty("periodCost").GetDouble());
            Assert.Equal(50, stored.GetProperty("periodQuota").GetDouble());
        }

        var extracted = ProviderQuotaService.ExtractLimitsFromQuotaData(merged);

        var limit = Assert.Single(extracted!);
        Assert.Equal(QuotaLimitTypes.Token, limit.Type);
        Assert.Equal(QuotaWindows.FiveHours, limit.Window);
        Assert.Equal(ProviderQuotaStatuses.Warning, limit.Status);
        Assert.Equal(0.85, limit.UsageRatio);
        Assert.True(limit.Ready);
        Assert.Equal(resetAt, limit.NextResetAt);
        Assert.Equal(periodStart, limit.PeriodStart);
        Assert.Equal(12.5, limit.PeriodCost);
        Assert.Equal(50, limit.PeriodQuota);
        Assert.Equal("acct", limit.Account);
        Assert.Equal("pool", limit.AvailabilityGroup);
    }

    [Fact]
    public void ExtractLimitsTreatsMissingOrMalformedDocumentsAsNoLimits()
    {
        Assert.Null(ProviderQuotaService.ExtractLimitsFromQuotaData(null));
        Assert.Null(ProviderQuotaService.ExtractLimitsFromQuotaData(" "));
        Assert.Null(ProviderQuotaService.ExtractLimitsFromQuotaData("not-json"));
        Assert.Null(ProviderQuotaService.ExtractLimitsFromQuotaData("""{"email":"a@b"}"""));
        Assert.Null(ProviderQuotaService.ExtractLimitsFromQuotaData("""{"_limits":{}}"""));

        // A limit row without a type keeps Go's zero value rather than the
        // port initializer's "token".
        var extracted = ProviderQuotaService.ExtractLimitsFromQuotaData(
            """{"_limits":[{"window":"5h","usageRatio":0.5}]}""");
        var limit = Assert.Single(extracted!);
        Assert.Equal(string.Empty, limit.Type);
        Assert.Equal("5h", limit.Window);
    }

    [Fact]
    public void MergeQuotaErrorDataReplacesTheLegacyErrorKey()
    {
        var merged = ProviderQuotaService.MergeQuotaErrorData(
            """{"error":"old","email":"a@b","error_count":2}""",
            ProviderQuotaService.ErrorCodeCheckFailed,
            failures: 3);

        using var document = System.Text.Json.JsonDocument.Parse(merged);
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("error", out _));
        Assert.Equal("check_failed", root.GetProperty("error_code").GetString());
        Assert.Equal(3, root.GetProperty("error_count").GetInt32());
        Assert.Equal("a@b", root.GetProperty("email").GetString());
    }

    [Fact]
    public void QuotaErrorCodeDistinguishesMissingCredentials()
    {
        Assert.Equal(
            ProviderQuotaService.ErrorCodeMissingCredentials,
            ProviderQuotaService.QuotaErrorCode(new InvalidOperationException("channel has no credentials")));
        Assert.Equal(
            ProviderQuotaService.ErrorCodeCheckFailed,
            ProviderQuotaService.QuotaErrorCode(new InvalidOperationException("boom")));
        Assert.Equal(
            ProviderQuotaService.ErrorCodeCheckFailed,
            ProviderQuotaService.QuotaErrorCode(null));
    }

    [Theory]
    [InlineData("claudecode", "claudecode")]
    [InlineData("codex", "codex")]
    [InlineData("antigravity", "antigravity")]
    [InlineData("xai_subscription", "xai_subscription")]
    [InlineData("github_copilot", "github_copilot")]
    [InlineData("nanogpt", "nanogpt")]
    [InlineData("nanogpt_responses", "nanogpt")]
    [InlineData("zenmux", "zenmux")]
    [InlineData("zenmux_video", "zenmux")]
    [InlineData("cline", "cline")]
    [InlineData("opencode_go", "opencode_go")]
    [InlineData("opencode_go_anthropic", "opencode_go")]
    [InlineData("moonshot_coding", "kimi_code")]
    [InlineData("minimax_anthropic", "minimax")]
    [InlineData("zhipu_anthropic", "zhipu")]
    [InlineData("zai_anthropic", "zai")]
    [InlineData("commandcode_anthropic", "commandcode")]
    [InlineData("ollama_anthropic", "ollama")]
    [InlineData("anthropic", "")]
    public void GetProviderTypeMapsChannelAliasesToTheCheckerKey(string channelType, string expected)
    {
        // provider_quota.go:1012-1049.
        Assert.Equal(expected, ProviderQuotaService.GetProviderType(new ChannelRecord { Type = channelType }));
    }

    [Fact]
    public void GetProviderTypeDetectsUrlMappedOpenAiProviders()
    {
        Assert.Equal(
            "synthetic",
            ProviderQuotaService.GetProviderType(new ChannelRecord
            {
                Type = "openai",
                BaseUrl = "https://api.synthetic.new/v1",
            }));
        Assert.Equal(
            "neuralwatt",
            ProviderQuotaService.GetProviderType(new ChannelRecord
            {
                Type = "openai_responses",
                BaseUrl = "https://api.neuralwatt.com",
            }));
        Assert.Equal(
            string.Empty,
            ProviderQuotaService.GetProviderType(new ChannelRecord
            {
                Type = "openai",
                BaseUrl = "https://api.openai.com/v1",
            }));
    }

    [Fact]
    public void QuotaAccountKeyHashesOnlyTheZenmuxManagementKey()
    {
        // provider_quota_account.go:25-37.
        var hashed = ProviderQuotaService.QuotaAccountKey(
            "zenmux",
            new ChannelRecord { Credentials = """{"managementApiKey":"mgmt-key"}""" });

        var expected = Convert.ToHexStringLower(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes("zenmux:mgmt-key")))
            [..16];
        Assert.Equal(expected, hashed);
        Assert.Equal(16, hashed.Length);

        // Whitespace is trimmed before hashing.
        Assert.Equal(
            hashed,
            ProviderQuotaService.QuotaAccountKey(
                "zenmux",
                new ChannelRecord { Credentials = """{"managementApiKey":"  mgmt-key  "}""" }));

        Assert.Equal(
            string.Empty,
            ProviderQuotaService.QuotaAccountKey(
                "zenmux",
                new ChannelRecord { Credentials = "{}" }));
        Assert.Equal(
            string.Empty,
            ProviderQuotaService.QuotaAccountKey(
                "codex",
                new ChannelRecord { Credentials = """{"managementApiKey":"mgmt-key"}""" }));
    }

    [Fact]
    public void FormatRfc3339PrintsSecondsAndTheUtcSuffix()
    {
        Assert.Equal(
            "2026-09-04T10:00:00Z",
            ProviderQuotaService.FormatRfc3339(Now));
        Assert.Equal(
            "2026-09-04T18:00:00+08:00",
            ProviderQuotaService.FormatRfc3339(
                new DateTimeOffset(2026, 9, 4, 18, 0, 0, TimeSpan.FromHours(8))));
    }
}
