using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the collection core of biz.ProviderQuotaService
/// (internal/server/biz/provider_quota.go, provider_quota_account.go,
/// provider_quota_cost.go): cache loading and invalidation, the run gates, the
/// group checker contract, the save/error branches with their backoff, the
/// period-cost fill and the manual reset surface. Provider checkers themselves
/// are registered by follow-up slices (C182); these tests drive the service
/// through scripted <see cref="IQuotaChecker"/> doubles.
/// </summary>
public sealed class ProviderQuotaServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private const string OAuthCredentials = """{"oauth":{"access_token":"token"}}""";

    // ------------------------------------------------------------------
    // Cache loading / reading / invalidation
    // ------------------------------------------------------------------

    [Fact]
    public async Task InitializeLoadsEveryLiveRowIntoTheCache()
    {
        var harness = new QuotaHarness();
        harness.Statuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = 7,
            ProviderType = "claudecode",
            Status = ProviderQuotaStatuses.Available,
            Ready = true,
            QuotaData = """{"_limits":[{"type":"token","status":"available","usageRatio":0.25,"ready":true,"window":"5h"}]}""",
        });

        await harness.Service.InitializeAsync();

        var status = harness.Service.GetQuotaStatus(7);
        Assert.NotNull(status);
        Assert.Equal("claudecode", status.ProviderType);
        Assert.Equal(ProviderQuotaStatuses.Available, status.Status);
        Assert.True(status.Ready);
        var limit = Assert.Single(status.Limits);
        Assert.Equal(QuotaLimitTypes.Token, limit.Type);
        Assert.Equal(0.25, limit.UsageRatio);
        Assert.Equal("5h", limit.Window);
    }

    [Fact]
    public async Task InitializeFailureLeavesTheCacheEmpty()
    {
        var harness = new QuotaHarness();
        harness.Statuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = 7,
            ProviderType = "claudecode",
            Status = ProviderQuotaStatuses.Available,
            QuotaData = "{}",
        });
        harness.Statuses.ListAllFailure = new InvalidOperationException("boom");

        await harness.Service.InitializeAsync();

        Assert.Null(harness.Service.GetQuotaStatus(7));
    }

    [Fact]
    public async Task GetQuotaStatusAppliesTheCollectionSettingsGate()
    {
        var harness = new QuotaHarness();
        harness.Statuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = 1,
            ProviderType = "claudecode",
            Status = ProviderQuotaStatuses.Available,
        });
        harness.Statuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = 2,
            ProviderType = "codex",
            Status = ProviderQuotaStatuses.Available,
        });
        harness.Statuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = 3,
            ProviderType = string.Empty,
            Status = ProviderQuotaStatuses.Available,
        });
        harness.Statuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = 4,
            ProviderType = "wafer",
            Status = ProviderQuotaStatuses.Available,
        });
        await harness.Service.InitializeAsync();

        var settings = ProviderQuotaCollectionSettingsService.Default();
        settings.Providers["codex"] = false;
        harness.SettingsCache.Update(settings);

        Assert.NotNull(harness.Service.GetQuotaStatus(1));
        Assert.Null(harness.Service.GetQuotaStatus(2));
        // An empty provider type bypasses the provider switch, as in Go.
        Assert.NotNull(harness.Service.GetQuotaStatus(3));
        // A retired provider type is not part of the settings map.
        Assert.Null(harness.Service.GetQuotaStatus(4));
        Assert.Null(harness.Service.GetQuotaStatus(99));

        harness.SettingsCache.Update(new ProviderQuotaCollectionSettings
        {
            Enabled = false,
            Providers = settings.Providers,
        });

        Assert.Null(harness.Service.GetQuotaStatus(1));
        Assert.NotNull(harness.Service.GetQuotaStatus(3));
    }

    [Fact]
    public async Task GetQuotaStatusHandsOutCopiesOfTheCachedLimits()
    {
        var harness = new QuotaHarness();
        harness.Statuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = 7,
            ProviderType = "claudecode",
            Status = ProviderQuotaStatuses.Available,
            QuotaData = """{"_limits":[{"type":"token","status":"available","usageRatio":0.25,"ready":true,"window":"5h"}]}""",
        });
        await harness.Service.InitializeAsync();

        var status = harness.Service.GetQuotaStatus(7);
        Assert.NotNull(status);
        status.Limits[0].UsageRatio = 0.99;

        var again = harness.Service.GetQuotaStatus(7);
        Assert.NotNull(again);
        Assert.Equal(0.25, again.Limits[0].UsageRatio);
    }

    [Fact]
    public async Task InvalidateHardDeletesTheRowAndDropsTheCacheEntry()
    {
        var harness = new QuotaHarness();
        harness.Statuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = 7,
            ProviderType = "claudecode",
            Status = ProviderQuotaStatuses.Available,
        });
        await harness.Service.InitializeAsync();

        await harness.Service.InvalidateChannelQuotaAsync(7);

        Assert.Equal([7L], harness.Statuses.HardDeletes);
        Assert.Empty(harness.Statuses.Rows);
        Assert.Null(harness.Service.GetQuotaStatus(7));
    }

    [Fact]
    public async Task InvalidateFailureStillDropsTheCacheEntryAndWrapsTheError()
    {
        var harness = new QuotaHarness();
        harness.Statuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = 7,
            ProviderType = "claudecode",
            Status = ProviderQuotaStatuses.Available,
        });
        await harness.Service.InitializeAsync();
        harness.Statuses.HardDeleteFailure = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.InvalidateChannelQuotaAsync(7));

        Assert.Equal("failed to invalidate provider quota status: boom", error.Message);
        Assert.Null(harness.Service.GetQuotaStatus(7));
        Assert.Single(harness.Statuses.Rows);
    }

    // ------------------------------------------------------------------
    // Run gates
    // ------------------------------------------------------------------

    [Fact]
    public async Task DisabledCollectionDoesNotQueryChannels()
    {
        var harness = new QuotaHarness();
        harness.Systems.Add(
            ProviderQuotaCollectionSettingsService.SystemKey,
            """{"enabled":false,"providers":{}}""");

        await harness.Service.RunQuotaCheckAsync(force: true);

        Assert.Equal(0, harness.Channels.ListEnabledByTypesCalls);
        Assert.False(harness.SettingsCache.Current.Enabled);
    }

    [Fact]
    public async Task ChannelsOutsideTheEnabledProviderMapAreSkipped()
    {
        var checker = new ScriptedQuotaChecker();
        var harness = new QuotaHarness(("claudecode", checker));
        harness.Systems.Add(
            ProviderQuotaCollectionSettingsService.SystemKey,
            """{"enabled":true,"providers":{"claudecode":false}}""");
        harness.AddChannel(1, "claudecode", OAuthCredentials);

        await harness.Service.RunQuotaCheckAsync(force: true);

        Assert.Equal(1, harness.Channels.ListEnabledByTypesCalls);
        Assert.Empty(checker.CheckedChannels);
        Assert.Empty(harness.Statuses.Upserts);
    }

    [Fact]
    public async Task ProviderQuotaChannelTypesMatchesTheReferenceList()
    {
        string[] expected =
        [
            "claudecode",
            "codex",
            "antigravity",
            "xai_subscription",
            "github_copilot",
            "nanogpt",
            "nanogpt_responses",
            "zenmux",
            "zenmux_responses",
            "zenmux_anthropic",
            "zenmux_gemini",
            "zenmux_video",
            "cline",
            "openai",
            "openai_responses",
            "opencode_go",
            "opencode_go_anthropic",
            "moonshot_coding",
            "minimax",
            "minimax_anthropic",
            "zhipu",
            "zhipu_anthropic",
            "zai",
            "zai_anthropic",
            "commandcode",
            "commandcode_anthropic",
            "ollama",
            "ollama_anthropic",
        ];

        Assert.Equal(expected, ProviderQuotaService.ProviderQuotaChannelTypes);
    }

    // ------------------------------------------------------------------
    // Successful checks
    // ------------------------------------------------------------------

    [Fact]
    public async Task CollectionPersistsTheNormalizedStatusAndRefreshesTheCache()
    {
        var checker = new ScriptedQuotaChecker
        {
            Result = new QuotaData
            {
                Status = ProviderQuotaStatuses.Available,
                ProviderType = "claudecode",
                RawData = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["email"] = "user@example.com",
                },
                Limits =
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Status = ProviderQuotaStatuses.Available,
                        UsageRatio = 0.2,
                        NextResetAt = Now.AddHours(4),
                        Window = QuotaWindows.FiveHours,
                    },
                ],
            },
        };
        var harness = new QuotaHarness(("claudecode", checker));
        harness.AddChannel(3, "claudecode", OAuthCredentials);

        await harness.Service.RunQuotaCheckAsync(force: true);

        var saved = Assert.Single(harness.Statuses.Upserts);
        Assert.Equal(3, saved.ChannelId);
        Assert.Equal("claudecode", saved.ProviderType);
        Assert.Equal(string.Empty, saved.AccountKey);
        Assert.Equal(ProviderQuotaStatuses.Available, saved.Status);
        Assert.True(saved.Ready);
        Assert.Equal(Now.AddHours(4).UtcDateTime, saved.NextResetAt);
        Assert.Equal(Now.AddMinutes(5).UtcDateTime, saved.NextCheckAt);

        using var document = JsonDocument.Parse(saved.QuotaData);
        var root = document.RootElement;
        Assert.Equal("user@example.com", root.GetProperty("email").GetString());

        // No resetter: supported stays false and the nil slice marshals to null.
        var resets = root.GetProperty("_resets");
        Assert.False(resets.GetProperty("supported").GetBoolean());
        Assert.Equal(JsonValueKind.Null, resets.GetProperty("resets").ValueKind);

        var stored = root.GetProperty("_limits")[0];
        Assert.Equal("token", stored.GetProperty("type").GetString());
        Assert.Equal("available", stored.GetProperty("status").GetString());
        Assert.Equal(0.2, stored.GetProperty("usageRatio").GetDouble());
        Assert.True(stored.GetProperty("ready").GetBoolean());
        Assert.Equal("5h", stored.GetProperty("window").GetString());
        Assert.Equal("2026-05-01T16:00:00Z", stored.GetProperty("nextResetAt").GetString());

        var cached = harness.Service.GetQuotaStatus(3);
        Assert.NotNull(cached);
        Assert.Equal(ProviderQuotaStatuses.Available, cached.Status);
        Assert.Equal(0.2, Assert.Single(cached.Limits).UsageRatio);
    }

    [Fact]
    public async Task WarningStatusSchedulesTheWarningInterval()
    {
        var checker = new ScriptedQuotaChecker
        {
            Result = new QuotaData
            {
                Status = ProviderQuotaStatuses.Warning,
                ProviderType = "claudecode",
                Limits =
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Status = ProviderQuotaStatuses.Warning,
                        UsageRatio = 0.9,
                        NextResetAt = Now.AddHours(2),
                        Window = QuotaWindows.FiveHours,
                    },
                ],
            },
        };
        var harness = new QuotaHarness(("claudecode", checker));
        harness.AddChannel(3, "claudecode", OAuthCredentials);

        await harness.Service.RunQuotaCheckAsync(force: true);

        var saved = Assert.Single(harness.Statuses.Upserts);
        Assert.Equal(ProviderQuotaStatuses.Warning, saved.Status);
        Assert.True(saved.Ready);
        Assert.Equal(Now.AddMinutes(20).UtcDateTime, saved.NextCheckAt);
    }

    [Fact]
    public async Task UpsertFailureLeavesTheCacheUntouched()
    {
        var checker = new ScriptedQuotaChecker
        {
            Result = new QuotaData
            {
                Status = ProviderQuotaStatuses.Available,
                ProviderType = "claudecode",
                Limits =
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Status = ProviderQuotaStatuses.Available,
                        UsageRatio = 0.1,
                        NextResetAt = Now.AddHours(1),
                        Window = QuotaWindows.FiveHours,
                    },
                ],
            },
        };
        var harness = new QuotaHarness(("claudecode", checker));
        harness.AddChannel(3, "claudecode", OAuthCredentials);
        harness.Statuses.UpsertFailure = new InvalidOperationException("boom");

        await harness.Service.RunQuotaCheckAsync(force: true);

        Assert.Null(harness.Service.GetQuotaStatus(3));
        Assert.Empty(harness.Statuses.Rows);
    }

    // ------------------------------------------------------------------
    // Check failures
    // ------------------------------------------------------------------

    [Fact]
    public async Task FailedCheckCreatesAStatusRowWithTheBaseBackoff()
    {
        var checker = new ScriptedQuotaChecker
        {
            Error = new InvalidOperationException("boom"),
        };
        var harness = new QuotaHarness(("claudecode", checker));
        harness.AddChannel(3, "claudecode", OAuthCredentials);

        await harness.Service.RunQuotaCheckAsync(force: true);

        var created = Assert.Single(harness.Statuses.Creates);
        Assert.Equal(3, created.ChannelId);
        Assert.Equal("claudecode", created.ProviderType);
        Assert.Equal(ProviderQuotaStatuses.Unknown, created.Status);
        Assert.False(created.Ready);
        Assert.Null(created.NextResetAt);
        Assert.Equal(Now.AddMinutes(5).UtcDateTime, created.NextCheckAt);
        Assert.Equal(
            """{"error_code":"check_failed","error_count":1}""",
            created.QuotaData);

        var cached = harness.Service.GetQuotaStatus(3);
        Assert.NotNull(cached);
        Assert.Equal(ProviderQuotaStatuses.Unknown, cached.Status);
        Assert.False(cached.Ready);
        Assert.Empty(cached.Limits);
    }

    [Fact]
    public async Task FailedCheckWithTheMissingCredentialsMessageUsesItsOwnCode()
    {
        var checker = new ScriptedQuotaChecker
        {
            Error = new InvalidOperationException("channel has no credentials"),
        };
        var harness = new QuotaHarness(("claudecode", checker));
        harness.AddChannel(3, "claudecode", OAuthCredentials);

        await harness.Service.RunQuotaCheckAsync(force: true);

        var created = Assert.Single(harness.Statuses.Creates);
        Assert.Equal(
            """{"error_code":"missing_credentials","error_count":1}""",
            created.QuotaData);
    }

    [Fact]
    public async Task RepeatedFailuresBackOffAndKeepThePreviousStatus()
    {
        var checker = new ScriptedQuotaChecker
        {
            Error = new InvalidOperationException("boom"),
        };
        var harness = new QuotaHarness(("claudecode", checker));
        harness.AddChannel(3, "claudecode", OAuthCredentials);
        var row = harness.Statuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = 3,
            ProviderType = "claudecode",
            Status = ProviderQuotaStatuses.Available,
            Ready = true,
            QuotaData = """{"error_count":2,"_limits":[{"type":"token","status":"available","usageRatio":0.1,"ready":true,"window":"5h"}]}""",
            NextCheckAt = Now.AddMinutes(-1).UtcDateTime,
        });

        await harness.Service.RunQuotaCheckAsync(force: true);

        var update = Assert.Single(harness.Statuses.ErrorUpdates);
        Assert.Equal(row.Id, update.Id);
        Assert.Equal(string.Empty, update.AccountKey);
        // failures=3 → 4x the 5-minute interval.
        Assert.Equal(Now.AddMinutes(20).UtcDateTime, update.NextCheckAt);

        using var document = JsonDocument.Parse(update.QuotaData);
        var root = document.RootElement;
        Assert.Equal("check_failed", root.GetProperty("error_code").GetString());
        Assert.Equal(3, root.GetProperty("error_count").GetInt32());
        Assert.Equal(1, root.GetProperty("_limits").GetArrayLength());

        var cached = harness.Service.GetQuotaStatus(3);
        Assert.NotNull(cached);
        Assert.Equal(ProviderQuotaStatuses.Available, cached.Status);
        Assert.True(cached.Ready);
        Assert.Equal(0.1, Assert.Single(cached.Limits).UsageRatio);
    }

    [Fact]
    public async Task ProviderChangeResetsTheExistingRow()
    {
        var checker = new ScriptedQuotaChecker
        {
            Error = new InvalidOperationException("boom"),
        };
        var harness = new QuotaHarness(("claudecode", checker));
        harness.AddChannel(3, "claudecode", OAuthCredentials);
        var row = harness.Statuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = 3,
            ProviderType = "codex",
            Status = ProviderQuotaStatuses.Available,
            Ready = true,
            QuotaData = """{"error_count":5}""",
            NextCheckAt = Now.AddMinutes(-1).UtcDateTime,
        });

        await harness.Service.RunQuotaCheckAsync(force: true);

        var reset = Assert.Single(harness.Statuses.ProviderResets);
        Assert.Equal(row.Id, reset.Id);
        Assert.Equal("claudecode", reset.ProviderType);
        Assert.Equal(string.Empty, reset.AccountKey);
        Assert.Equal(Now.AddMinutes(5).UtcDateTime, reset.NextCheckAt);
        Assert.Equal(
            """{"error_code":"check_failed","error_count":1}""",
            reset.QuotaData);

        var cached = harness.Service.GetQuotaStatus(3);
        Assert.NotNull(cached);
        Assert.Equal("claudecode", cached.ProviderType);
        Assert.Equal(ProviderQuotaStatuses.Unknown, cached.Status);
        Assert.False(cached.Ready);
        Assert.Empty(cached.Limits);
    }

    [Fact]
    public async Task InvalidCredentialsResetTheRowWithTheBaseBackoff()
    {
        var checker = new ScriptedQuotaChecker
        {
            Error = new InvalidProviderQuotaCredentialsException(),
        };
        var harness = new QuotaHarness(("claudecode", checker));
        harness.AddChannel(3, "claudecode", OAuthCredentials);
        var row = harness.Statuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = 3,
            ProviderType = "claudecode",
            Status = ProviderQuotaStatuses.Available,
            Ready = true,
            QuotaData = """{"error_count":4}""",
            NextCheckAt = Now.AddMinutes(-1).UtcDateTime,
        });

        await harness.Service.RunQuotaCheckAsync(force: true);

        var reset = Assert.Single(harness.Statuses.ProviderResets);
        Assert.Equal(row.Id, reset.Id);
        Assert.Equal(Now.AddMinutes(5).UtcDateTime, reset.NextCheckAt);
        Assert.Equal(
            """{"error_code":"check_failed","error_count":1}""",
            reset.QuotaData);
        Assert.Equal(ProviderQuotaStatuses.Unknown, harness.Service.GetQuotaStatus(3)?.Status);
    }

    // ------------------------------------------------------------------
    // Channels without credentials / checker
    // ------------------------------------------------------------------

    [Fact]
    public async Task ChannelsWithoutCredentialsLoseTheirStatusAndSkipTheChecker()
    {
        var checker = new ScriptedQuotaChecker();
        var harness = new QuotaHarness(("claudecode", checker));
        harness.AddChannel(3, "claudecode", "{}");
        harness.Statuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = 3,
            ProviderType = "claudecode",
            Status = ProviderQuotaStatuses.Available,
        });
        await harness.Service.InitializeAsync();

        await harness.Service.RunQuotaCheckAsync(force: true);

        Assert.Empty(checker.CheckedChannels);
        Assert.Equal([3L], harness.Statuses.HardDeletes);
        Assert.Empty(harness.Statuses.Rows);
        Assert.Null(harness.Service.GetQuotaStatus(3));
    }

    [Fact]
    public async Task ChannelsWithoutACheckerKeepTheirRowUntouched()
    {
        var harness = new QuotaHarness();
        harness.AddChannel(3, "codex", OAuthCredentials);
        var row = harness.Statuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = 3,
            ProviderType = "codex",
            Status = ProviderQuotaStatuses.Exhausted,
            Ready = false,
            QuotaData = """{"email":"old@example.com"}""",
        });

        await harness.Service.RunQuotaCheckAsync(force: true);

        Assert.Equal(1, harness.Channels.ListEnabledByTypesCalls);
        Assert.Empty(harness.Statuses.Upserts);
        Assert.Empty(harness.Statuses.Creates);
        Assert.Empty(harness.Statuses.ErrorUpdates);
        Assert.Empty(harness.Statuses.ProviderResets);
        Assert.Empty(harness.Statuses.HardDeletes);
        Assert.Equal("""{"email":"old@example.com"}""", row.QuotaData);
        Assert.Equal(ProviderQuotaStatuses.Exhausted, row.Status);
    }

    [Fact]
    public async Task SettingsReadFailureDuringACheckStillRunsTheChecker()
    {
        var checker = new ScriptedQuotaChecker
        {
            Result = new QuotaData
            {
                Status = ProviderQuotaStatuses.Available,
                ProviderType = "claudecode",
            },
        };
        var harness = new QuotaHarness(("claudecode", checker));
        harness.AddChannel(3, "claudecode", OAuthCredentials);
        harness.Systems.GetFailuresByKey[ProviderQuotaCollectionSettingsService.SystemKey] =
            new InvalidOperationException("boom");

        await harness.Service.RunQuotaCheckAsync(force: true);

        // The lenient read falls back to the all-enabled defaults, and the
        // per-channel settings failure is logged and ignored.
        Assert.Equal(1, checker.CheckCalls);
        Assert.Single(harness.Statuses.Upserts);
    }

    // ------------------------------------------------------------------
    // Scheduling
    // ------------------------------------------------------------------

    [Fact]
    public async Task ScheduledChecksHonourNextCheckAt()
    {
        var checker = new ScriptedQuotaChecker
        {
            Result = new QuotaData
            {
                Status = ProviderQuotaStatuses.Available,
                ProviderType = "claudecode",
                Limits =
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Status = ProviderQuotaStatuses.Available,
                        UsageRatio = 0.1,
                        NextResetAt = Now.AddHours(4),
                        Window = QuotaWindows.FiveHours,
                    },
                ],
            },
        };
        var harness = new QuotaHarness(("claudecode", checker));
        harness.AddChannel(3, "claudecode", OAuthCredentials);
        harness.Statuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = 3,
            ProviderType = "claudecode",
            Status = ProviderQuotaStatuses.Available,
            NextCheckAt = Now.AddMinutes(10).UtcDateTime,
        });

        await harness.Service.RunScheduledCheckAsync();
        Assert.Equal(0, checker.CheckCalls);

        await harness.Service.ManualCheckAsync();
        Assert.Equal(1, checker.CheckCalls);

        // The successful check moved next_check_at to now+5m.
        harness.Time.Advance(TimeSpan.FromMinutes(6));
        await harness.Service.RunScheduledCheckAsync();
        Assert.Equal(2, checker.CheckCalls);
    }

    [Fact]
    public async Task ScheduledChecksRunChannelsWithoutAStatusRow()
    {
        var checker = new ScriptedQuotaChecker
        {
            Result = new QuotaData
            {
                Status = ProviderQuotaStatuses.Available,
                ProviderType = "claudecode",
            },
        };
        var harness = new QuotaHarness(("claudecode", checker));
        harness.AddChannel(3, "claudecode", OAuthCredentials);

        await harness.Service.RunScheduledCheckAsync();

        Assert.Equal(1, checker.CheckCalls);
    }

    // ------------------------------------------------------------------
    // Period cost / quota fill
    // ------------------------------------------------------------------

    [Fact]
    public async Task CollectionFillsPeriodCostsAndQuotasFromTheUsageLogs()
    {
        var checker = new ScriptedQuotaChecker
        {
            Result = new QuotaData
            {
                Status = ProviderQuotaStatuses.Available,
                ProviderType = "claudecode",
                Limits =
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Status = ProviderQuotaStatuses.Available,
                        UsageRatio = 0.5,
                        Window = QuotaWindows.FiveHours,
                        NextResetAt = Now.AddHours(1),
                        PeriodStart = Now.AddHours(-4),
                    },
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Status = ProviderQuotaStatuses.Available,
                        UsageRatio = 0.25,
                        Window = QuotaWindows.SevenDays,
                        NextResetAt = Now.AddDays(1),
                        PeriodStart = Now.AddDays(-6),
                    },
                ],
            },
        };
        var harness = new QuotaHarness(("claudecode", checker));
        harness.AddChannel(3, "claudecode", OAuthCredentials);
        harness.UsageLogs.Rows.Add(UsageLog(3, Now.AddDays(-8), 100));
        harness.UsageLogs.Rows.Add(UsageLog(3, Now.AddDays(-3), 30));
        harness.UsageLogs.Rows.Add(UsageLog(3, Now.AddHours(-1), 12));
        harness.UsageLogs.Rows.Add(UsageLog(3, Now.AddMinutes(1), 99));

        await harness.Service.RunQuotaCheckAsync(force: true);

        var saved = Assert.Single(harness.Statuses.Upserts);
        using var document = JsonDocument.Parse(saved.QuotaData);
        var limits = document.RootElement.GetProperty("_limits");
        Assert.Equal(2, limits.GetArrayLength());

        var fiveHours = FindLimit(limits, "5h");
        Assert.Equal(12, fiveHours.GetProperty("periodCost").GetDouble());
        Assert.Equal(24, fiveHours.GetProperty("periodQuota").GetDouble());

        var sevenDays = FindLimit(limits, "7d");
        Assert.Equal(42, sevenDays.GetProperty("periodCost").GetDouble());
        Assert.Equal(168, sevenDays.GetProperty("periodQuota").GetDouble());

        // The cache is refreshed with the filled figures too.
        var cached = harness.Service.GetQuotaStatus(3);
        Assert.NotNull(cached);
        Assert.Equal(12, cached.Limits[0].PeriodCost);
        Assert.Equal(24, cached.Limits[0].PeriodQuota);
        Assert.Equal(42, cached.Limits[1].PeriodCost);
        Assert.Equal(168, cached.Limits[1].PeriodQuota);
    }

    [Fact]
    public async Task ZenmuxPeriodFiguresAreCleared()
    {
        var checker = new ScriptedQuotaChecker
        {
            Result = new QuotaData
            {
                Status = ProviderQuotaStatuses.Available,
                ProviderType = "zenmux",
                Limits =
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Status = ProviderQuotaStatuses.Available,
                        UsageRatio = 0.2,
                        Window = QuotaWindows.SevenDays,
                        NextResetAt = Now.AddDays(6),
                        PeriodStart = Now.AddDays(-1),
                        PeriodCost = 9,
                        PeriodQuota = 30,
                    },
                ],
            },
        };
        var harness = new QuotaHarness(("zenmux", checker));
        harness.AddChannel(5, "zenmux", """{"managementApiKey":"mk-1"}""");

        await harness.Service.RunQuotaCheckAsync(force: true);

        var saved = Assert.Single(harness.Statuses.Upserts);
        using var document = JsonDocument.Parse(saved.QuotaData);
        var stored = document.RootElement.GetProperty("_limits")[0];
        Assert.False(stored.TryGetProperty("periodCost", out _));
        Assert.False(stored.TryGetProperty("periodQuota", out _));

        var cached = harness.Service.GetQuotaStatus(5);
        Assert.NotNull(cached);
        Assert.Null(cached.Limits[0].PeriodCost);
        Assert.Null(cached.Limits[0].PeriodQuota);
    }

    [Fact]
    public async Task SyntheticPeriodFiguresStayAuthoritative()
    {
        var checker = new ScriptedQuotaChecker
        {
            Result = new QuotaData
            {
                Status = ProviderQuotaStatuses.Available,
                ProviderType = "synthetic",
                Limits =
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Status = ProviderQuotaStatuses.Available,
                        UsageRatio = 0.2,
                        Window = QuotaWindows.SevenDays,
                        NextResetAt = Now.AddDays(6),
                        PeriodStart = Now.AddDays(-1),
                        PeriodCost = 7,
                    },
                ],
            },
        };
        var harness = new QuotaHarness(("synthetic", checker));
        var channel = harness.AddChannel(5, "openai", """{"apiKey":"sk-synthetic"}""");
        channel.BaseUrl = "https://api.synthetic.new/v1";

        await harness.Service.RunQuotaCheckAsync(force: true);

        var saved = Assert.Single(harness.Statuses.Upserts);
        Assert.Equal("synthetic", saved.ProviderType);
        using var document = JsonDocument.Parse(saved.QuotaData);
        var stored = document.RootElement.GetProperty("_limits")[0];
        Assert.Equal(7, stored.GetProperty("periodCost").GetDouble());
        Assert.False(stored.TryGetProperty("periodQuota", out _));
    }

    // ------------------------------------------------------------------
    // Quota account grouping
    // ------------------------------------------------------------------

    [Fact]
    public async Task ZenmuxChannelsSharingAManagementKeyAreCheckedOnce()
    {
        var checker = new ScriptedQuotaChecker
        {
            Handler = channel => new QuotaData
            {
                Status = ProviderQuotaStatuses.Available,
                ProviderType = "zenmux",
                RawData = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["checked"] = channel.Id,
                },
                Limits =
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Status = ProviderQuotaStatuses.Available,
                        UsageRatio = 0.1,
                        Window = QuotaWindows.SevenDays,
                        NextResetAt = Now.AddDays(6),
                    },
                ],
            },
        };
        var harness = new QuotaHarness(("zenmux", checker));
        var first = harness.AddChannel(11, "zenmux", """{"managementApiKey":"shared"}""");
        var second = harness.AddChannel(12, "zenmux_responses", """{"managementApiKey":"shared"}""");
        var third = harness.AddChannel(13, "zenmux", """{"managementApiKey":"other"}""");

        await harness.Service.RunQuotaCheckAsync(force: true);

        Assert.Equal(2, checker.CheckCalls);
        Assert.Equal(3, harness.Statuses.Upserts.Count);

        var sharedKey = ProviderQuotaService.QuotaAccountKey("zenmux", first);
        Assert.Equal(16, sharedKey.Length);
        Assert.Equal(sharedKey, ProviderQuotaService.QuotaAccountKey("zenmux", second));
        Assert.Equal(sharedKey, harness.Statuses.Upserts.Single(row => row.ChannelId == 11).AccountKey);
        Assert.Equal(sharedKey, harness.Statuses.Upserts.Single(row => row.ChannelId == 12).AccountKey);
        Assert.NotEqual(
            sharedKey,
            harness.Statuses.Upserts.Single(row => row.ChannelId == 13).AccountKey);
        Assert.NotEqual(
            sharedKey,
            ProviderQuotaService.QuotaAccountKey("zenmux", third));
    }

    // ------------------------------------------------------------------
    // Reset surface
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListResetsReportsUnsupportedWithoutAResetter()
    {
        var harness = new QuotaHarness(("claudecode", new ScriptedQuotaChecker()));
        harness.AddChannel(3, "claudecode", OAuthCredentials);

        var resets = await harness.Service.ListResetsAsync(3);

        Assert.False(resets.Supported);
        Assert.Null(resets.Error);
    }

    [Fact]
    public async Task ListResetsReturnsTheProviderList()
    {
        var resetter = new ScriptedQuotaResetter
        {
            ListResult = new ResetList
            {
                Supported = false,
                Resets = [new QuotaReset { Id = "reset-1", Status = "available" }],
            },
        };
        var harness = new QuotaHarness(("claudecode", resetter));
        harness.AddChannel(3, "claudecode", OAuthCredentials);

        var resets = await harness.Service.ListResetsAsync(3);

        Assert.True(resets.Supported);
        Assert.Equal("reset-1", Assert.Single(resets.Resets!).Id);
    }

    [Fact]
    public async Task ListResetsSurfacesTheReferenceErrors()
    {
        var resetter = new ScriptedQuotaResetter();
        var harness = new QuotaHarness(("claudecode", resetter));

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.ListResetsAsync(999));
        Assert.Equal("failed to load channel: ent: channel not found", missing.Message);

        harness.AddChannel(3, "claudecode", OAuthCredentials);
        harness.Systems.Add(
            ProviderQuotaCollectionSettingsService.SystemKey,
            """{"enabled":false,"providers":{}}""");

        var disabled = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.ListResetsAsync(3));
        Assert.Equal("provider quota collection is disabled for claudecode", disabled.Message);

        await harness.Systems.SetAsync(
            ProviderQuotaCollectionSettingsService.SystemKey,
            """{"enabled":true,"providers":{}}""");
        harness.AddChannel(4, "claudecode", "{}");

        var noCredentials = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.ListResetsAsync(4));
        Assert.Equal("channel has no credentials", noCredentials.Message);

        resetter.ListError = new InvalidOperationException("boom");

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.ListResetsAsync(3));
        Assert.Equal("failed to list claudecode quota resets: boom", failed.Message);
    }

    [Fact]
    public async Task ResetChannelQuotaNowRefreshesTheStatus()
    {
        var resetter = new ScriptedQuotaResetter
        {
            Result = new QuotaData
            {
                Status = ProviderQuotaStatuses.Available,
                ProviderType = "claudecode",
                Limits =
                [
                    new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Status = ProviderQuotaStatuses.Available,
                        UsageRatio = 0,
                        Window = QuotaWindows.FiveHours,
                        NextResetAt = Now.AddHours(5),
                    },
                ],
            },
            ListResult = new ResetList { Supported = true, Resets = [] },
        };
        var harness = new QuotaHarness(("claudecode", resetter));
        harness.AddChannel(3, "claudecode", OAuthCredentials);

        await harness.Service.ResetChannelQuotaNowAsync(3);

        Assert.Equal(1, resetter.ResetCalls);
        Assert.Equal(1, resetter.CheckCalls);

        var saved = Assert.Single(harness.Statuses.Upserts);
        Assert.Equal(ProviderQuotaStatuses.Available, saved.Status);
        Assert.Equal(Now.AddMinutes(5).UtcDateTime, saved.NextCheckAt);
        using (var document = JsonDocument.Parse(saved.QuotaData))
        {
            Assert.True(
                document.RootElement.GetProperty("_resets").GetProperty("supported").GetBoolean());
        }

        var cached = harness.Service.GetQuotaStatus(3);
        Assert.NotNull(cached);
        Assert.Equal(ProviderQuotaStatuses.Available, cached.Status);
    }

    [Fact]
    public async Task ResetChannelQuotaNowSurfacesTheReferenceErrors()
    {
        var harness = new QuotaHarness(("claudecode", new ScriptedQuotaChecker()));
        harness.AddChannel(3, "claudecode", OAuthCredentials);

        var unsupported = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.ResetChannelQuotaNowAsync(3));
        Assert.Equal(
            "provider quota reset is not supported for provider \"claudecode\"",
            unsupported.Message);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.ResetChannelQuotaNowAsync(999));
        Assert.Equal("failed to load channel: ent: channel not found", missing.Message);

        var resetter = new ScriptedQuotaResetter();
        var second = new QuotaHarness(("claudecode", resetter));
        second.AddChannel(3, "claudecode", OAuthCredentials);
        second.Systems.Add(
            ProviderQuotaCollectionSettingsService.SystemKey,
            """{"enabled":false,"providers":{}}""");

        var disabled = await Assert.ThrowsAsync<InvalidOperationException>(
            () => second.Service.ResetChannelQuotaNowAsync(3));
        Assert.Equal("provider quota collection is disabled for claudecode", disabled.Message);

        await second.Systems.SetAsync(
            ProviderQuotaCollectionSettingsService.SystemKey,
            """{"enabled":true,"providers":{}}""");
        second.AddChannel(4, "claudecode", "{}");

        var noCredentials = await Assert.ThrowsAsync<InvalidOperationException>(
            () => second.Service.ResetChannelQuotaNowAsync(4));
        Assert.Equal("channel has no credentials", noCredentials.Message);

        resetter.ResetError = new InvalidOperationException("boom");

        var failed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => second.Service.ResetChannelQuotaNowAsync(3));
        Assert.Equal("failed to reset claudecode quota: boom", failed.Message);
        Assert.Equal(0, resetter.CheckCalls);
    }

    // ------------------------------------------------------------------
    // Coordinator and group-executor seams (§150)
    // ------------------------------------------------------------------

    [Fact]
    public async Task ManualCheckGoesThroughTheCoordinatorWhenOneIsWired()
    {
        var coordinator = new RecordingQuotaCoordinatorClient();
        var checker = new ScriptedQuotaChecker();
        var harness = new QuotaHarness(null, coordinator, ("claudecode", checker));
        harness.AddChannel(3, "claudecode", OAuthCredentials);

        await harness.Service.ManualCheckAsync();

        Assert.Equal(1, coordinator.ManualChecks);
        Assert.Equal(0, checker.CheckCalls);
    }

    [Fact]
    public async Task InvalidateChannelQuotaGoesThroughTheCoordinatorWhenOneIsWired()
    {
        var coordinator = new RecordingQuotaCoordinatorClient();
        var harness = new QuotaHarness(null, coordinator);

        await harness.Service.InvalidateChannelQuotaAsync(3);

        Assert.Equal([3L], coordinator.Invalidations);
        Assert.Empty(harness.Statuses.HardDeletes);
    }

    [Fact]
    public async Task ResetChannelQuotaRefreshGoesThroughTheCoordinatorWhenOneIsWired()
    {
        var coordinator = new RecordingQuotaCoordinatorClient();
        var resetter = new ScriptedQuotaResetter
        {
            Result = new QuotaData
            {
                Status = ProviderQuotaStatuses.Available,
                ProviderType = "claudecode",
            },
            ListResult = new ResetList { Supported = true, Resets = [] },
        };
        var harness = new QuotaHarness(null, coordinator, ("claudecode", resetter));
        harness.AddChannel(3, "claudecode", OAuthCredentials);

        await harness.Service.ResetChannelQuotaNowAsync(3);

        Assert.Equal(1, resetter.ResetCalls);

        // The refresh itself is deferred to the coordinator, so this process
        // has neither checked the provider nor written a status row.
        Assert.Equal(0, resetter.CheckCalls);
        Assert.Empty(harness.Statuses.Upserts);

        var refresh = Assert.Single(coordinator.Refreshes);
        Assert.Equal(string.Empty, refresh.AccountKey);
        Assert.Equal(3, refresh.Channel.Id);
        Assert.Equal("claudecode", refresh.Channel.Type);
        Assert.Equal(OAuthCredentials, refresh.Channel.Credentials);
        Assert.Null(refresh.Channel.Status);
    }

    [Fact]
    public async Task RunQuotaCheckDispatchesEachDueGroupThroughTheExecutorWhenOneIsWired()
    {
        var executor = new RecordingQuotaGroupExecutor();
        var checker = new ScriptedQuotaChecker();
        var harness = new QuotaHarness(executor, null, ("claudecode", checker));
        harness.AddChannel(3, "claudecode", OAuthCredentials);

        await harness.Service.RunQuotaCheckAsync(force: true);

        var group = Assert.Single(executor.Groups);
        Assert.Equal([3L], group.Members.Select(static member => member.Id));
        Assert.Equal("claudecode", group.Members[0].Type);
        Assert.Equal(0, checker.CheckCalls);
    }

    [Fact]
    public async Task RunGroupAsyncRunsTheCheckAndReportsEveryMembersCacheEntry()
    {
        var checker = new ScriptedQuotaChecker
        {
            Result = new QuotaData
            {
                Status = ProviderQuotaStatuses.Available,
                ProviderType = "claudecode",
                Ready = true,
            },
        };
        var harness = new QuotaHarness(("claudecode", checker));
        harness.AddChannel(11, "claudecode", OAuthCredentials);
        harness.AddChannel(12, "claudecode", OAuthCredentials);

        var group = new ProviderQuotaCheckGroup { AccountKey = string.Empty };
        foreach (var channelId in new long[] { 11, 12 })
        {
            group.Members.Add(new ProviderQuotaChannelSnapshot
            {
                Id = channelId,
                Name = "claudecode-channel",
                Type = "claudecode",
                Credentials = OAuthCredentials,
                BaseUrl = "https://example.test",
            });
        }

        var result = await harness.Service.RunGroupAsync(group, Now);

        // One provider request for the account, one status row per member.
        Assert.Equal(1, checker.CheckCalls);
        Assert.Equal(2, harness.Statuses.Upserts.Count);
        Assert.Equal([11L, 12L], result.Members.Select(static member => member.ChannelId));
        Assert.All(result.Members, static member => Assert.NotNull(member.Status));
        Assert.Equal(
            ProviderQuotaStatuses.Available,
            result.Members[0].Status!.Status);
        Assert.Equal(
            ProviderQuotaStatuses.Available,
            result.Members[1].Status!.Status);
    }

    // -- Helpers -------------------------------------------------------------

    private static UsageLogRecord UsageLog(
        long channelId,
        DateTimeOffset createdAt,
        double totalCost) => new()
    {
        ChannelId = channelId,
        ApiKeyId = 1,
        CreatedAt = createdAt.UtcDateTime,
        UpdatedAt = createdAt.UtcDateTime,
        ModelId = "test-model",
        TotalCost = totalCost,
    };

    private static JsonElement FindLimit(JsonElement limits, string window)
    {
        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.GetProperty("window").GetString() == window)
            {
                return limit;
            }
        }

        throw new Xunit.Sdk.XunitException("limit with window " + window + " not found");
    }

    /// <summary>
    /// The service resolves every store through a DI scope. The harness serves
    /// the fixed doubles from a root provider; the scoped settings service is
    /// stateless, so reusing it across parallel groups is equivalent to one
    /// instance per scope.
    /// </summary>
    private sealed class TestServiceProvider : IServiceProvider, IServiceScopeFactory, IServiceScope
    {
        private readonly Dictionary<Type, object> _services;

        public TestServiceProvider(params (Type Type, object Service)[] services)
        {
            _services = services.ToDictionary(
                static entry => entry.Type,
                static entry => entry.Service);
        }

        public object? GetService(Type serviceType)
            => _services.TryGetValue(serviceType, out var service) ? service : null;

        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public void Dispose()
        {
        }
    }

    private sealed class QuotaHarness
    {
        public QuotaHarness(params (string ProviderType, IQuotaChecker Checker)[] checkers)
            : this(null, null, checkers)
        {
        }

        public QuotaHarness(
            IQuotaGroupExecutor? groupExecutor,
            IProviderQuotaCoordinatorClient? coordinator,
            params (string ProviderType, IQuotaChecker Checker)[] checkers)
        {
            var registry = new ProviderQuotaCheckerRegistry(
                checkers.Select(static entry =>
                    new ProviderQuotaCheckerRegistration(entry.ProviderType, entry.Checker)));
            var settings = new ProviderQuotaCollectionSettingsService(Systems, SettingsCache);
            var services = new TestServiceProvider(
                (typeof(ISystemStore), Systems),
                (typeof(IChannelStore), Channels),
                (typeof(IUsageLogStore), UsageLogs),
                (typeof(IProviderQuotaStatusStore), Statuses),
                (typeof(IProviderQuotaCollectionSettingsService), settings),
                (typeof(ProviderQuotaSettingsCache), SettingsCache),
                (typeof(IProviderQuotaCheckerRegistry), registry));

            Service = new ProviderQuotaService(
                services,
                SettingsCache,
                registry,
                Time,
                NullLogger<ProviderQuotaService>.Instance,
                groupExecutor,
                coordinator);
        }

        public FakeSystemStore Systems { get; } = new();

        public FakeChannelStore Channels { get; } = new();

        public InMemoryUsageLogStore UsageLogs { get; } = new();

        public FakeProviderQuotaStatusStore Statuses { get; } = new();

        public ProviderQuotaSettingsCache SettingsCache { get; } = new();

        public TestTimeProvider Time { get; } = new(Now);

        public ProviderQuotaService Service { get; }

        public ChannelRecord AddChannel(long id, string type, string credentials)
            => Channels.Add(new ChannelRecord
            {
                Id = id,
                Type = type,
                Name = type + "-channel",
                Status = "enabled",
                Credentials = credentials,
                BaseUrl = "https://example.test",
            });
    }
}

/// <summary>
/// Records the coordinator entries the service delegates (§150): manual
/// checks, invalidations and post-reset refreshes.
/// </summary>
internal sealed class RecordingQuotaCoordinatorClient : IProviderQuotaCoordinatorClient
{
    public int ManualChecks { get; private set; }

    public List<long> Invalidations { get; } = [];

    public List<ProviderQuotaRefreshRequest> Refreshes { get; } = [];

    public Task RunManualAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        ManualChecks++;
        return Task.CompletedTask;
    }

    public Task RefreshChannelAsync(
        ProviderQuotaRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Refreshes.Add(request);
        return Task.CompletedTask;
    }

    public Task InvalidateAsync(long channelId, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Invalidations.Add(channelId);
        return Task.CompletedTask;
    }
}

/// <summary>Records every group the collection run dispatches (§150).</summary>
internal sealed class RecordingQuotaGroupExecutor : IQuotaGroupExecutor
{
    public List<ProviderQuotaCheckGroup> Groups { get; } = [];

    public Task ExecuteAsync(
        ProviderQuotaCheckGroup group,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        _ = now;
        _ = cancellationToken;
        Groups.Add(group);
        return Task.CompletedTask;
    }
}
