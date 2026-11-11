using System.Text;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Settings;
using AxonHub.Llm.Http;
using AxonHub.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// C236: port of channel_auto_disable.go. Locks the consecutive-error counter
/// protocol, rule matching, every action's reason/expiry strings, the
/// mark-channel-unavailable flow (webhook snapshot plus cache reloads), and the
/// background evaluation queue.
/// </summary>
public sealed class ChannelAutoDisableTests
{
    private const string Permanent = RetryPolicy.AutoDisableActionPermanent;

    private const string PermanentDelete = RetryPolicy.AutoDisableActionPermanentDelete;

    private const string Temporary = RetryPolicy.AutoDisableActionTemporary;

    private const string UntilCron = RetryPolicy.AutoDisableActionUntilCron;

    [Fact]
    public async Task OffModeSkipsBothRuleLayers()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("off", Rule([401], 1, Permanent)));
        harness.Settings.RetryPolicy = Global(GlobalRule([401], 1, Permanent));

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 401, "bad key"));

        Assert.Empty(harness.Credentials.DisableCalls);
        Assert.Empty(harness.Store.ChannelAutoDisableCalls);
        Assert.Empty(harness.Webhooks.Events);
    }

    [Fact]
    public async Task CustomModeMatchingRuleDisablesTheApiKeyPermanently()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([401], 1, Permanent)));

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 401, "bad key"));

        var call = Assert.Single(harness.Credentials.DisableCalls);
        Assert.Equal(channel.Id, call.ChannelId);
        Assert.Equal("sk-1", call.Key);
        Assert.Equal(401, call.ErrorCode);
        Assert.Equal("Disabled by auto-disable rule after 1 consecutive errors", call.Reason);
        Assert.Null(call.ExpiresAt);

        // A keyed action never touches the channel row or the webhook.
        Assert.Empty(harness.Store.ChannelAutoDisableCalls);
        Assert.Empty(harness.Webhooks.Events);
    }

    [Fact]
    public async Task RuleCountsConsecutiveFailuresBeforeActing()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([500], 3, Permanent)));

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 500, "one"));
        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 500, "two"));
        Assert.Empty(harness.Credentials.DisableCalls);

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 500, "three"));

        var call = Assert.Single(harness.Credentials.DisableCalls);
        Assert.Equal("Disabled by auto-disable rule after 3 consecutive errors", call.Reason);
    }

    [Fact]
    public async Task CustomModeFallsBackToGlobalRulesWhenNothingMatches()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([500], 1, Permanent)));
        harness.Settings.RetryPolicy = Global(GlobalRule([401], 1, Permanent));

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 401, "bad key"));

        Assert.Single(harness.Credentials.DisableCalls);
    }

    [Fact]
    public async Task CustomModeWithoutRulesInheritsGlobalRules()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom"));
        harness.Settings.RetryPolicy = Global(GlobalRule([401], 1, Permanent));

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 401, "bad key"));

        Assert.Single(harness.Credentials.DisableCalls);
    }

    [Fact]
    public async Task InheritModeIgnoresChannelRules()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("inherit", Rule([401], 1, Permanent)));
        harness.Settings.RetryPolicy = new RetryPolicy
        {
            AutoDisableChannel = new AutoDisableChannel(false, []),
        };

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 401, "bad key"));

        Assert.Empty(harness.Credentials.DisableCalls);
    }

    [Fact]
    public async Task PoliciesWithoutModeBehaveAsCustom()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies(null, Rule([401], 1, Permanent)));

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 401, "bad key"));

        Assert.Single(harness.Credentials.DisableCalls);
    }

    [Fact]
    public async Task DisabledGlobalPolicySkipsEvaluation()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel();
        harness.Settings.RetryPolicy = new RetryPolicy
        {
            AutoDisableChannel = new AutoDisableChannel(false, [GlobalRule([401], 1, Permanent)]),
        };

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 401, "bad key"));

        Assert.Empty(harness.Credentials.DisableCalls);
    }

    [Fact]
    public async Task RetryPolicyReadFailureFallsBackToTheDefaultPolicy()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel();
        harness.Settings.RetryPolicyFailure = new InvalidOperationException("settings unavailable");

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 401, "bad key"));

        Assert.Empty(harness.Credentials.DisableCalls);
    }

    [Fact]
    public async Task SuccessResetsTheStreak()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([500], 2, Permanent)));

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 500, "one"));
        await harness.Service.EvaluateAsync(harness.Success(channel.Id, "sk-1"));
        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 500, "two"));
        Assert.Empty(harness.Credentials.DisableCalls);

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 500, "three"));

        var call = Assert.Single(harness.Credentials.DisableCalls);
        Assert.Equal("Disabled by auto-disable rule after 2 consecutive errors", call.Reason);
    }

    [Fact]
    public async Task CanceledFailuresDoNotCount()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([500], 2, Permanent)));

        await harness.Service.EvaluateAsync(harness.Canceled(channel.Id, "sk-1"));
        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 500, "one"));

        // A canceled attempt must not have advanced the streak to the threshold.
        Assert.Empty(harness.Credentials.DisableCalls);
    }

    [Fact]
    public async Task UnfinishedRecordsAreIgnored()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([500], 1, Permanent)));
        var open = new ChannelPerformanceRecord
        {
            ChannelId = channel.Id,
            StartTime = harness.Clock.GetUtcNow(),
            ApiKey = "sk-1",
        };

        await harness.Service.EvaluateAsync(open);

        Assert.Empty(harness.Credentials.DisableCalls);
        Assert.False(open.IsValid);
    }

    [Fact]
    public async Task TemporaryDisableSetsTheExpiryAndReason()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(
            ChannelPolicies("custom", Rule([429], 1, Temporary, disableDurationMinutes: 5)));

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 429, "rate limited"));

        var call = Assert.Single(harness.Credentials.DisableCalls);
        Assert.Equal(
            "Temporarily disabled for 5 minutes by auto-disable rule after 1 consecutive errors",
            call.Reason);
        Assert.Equal(new DateTime(2026, 1, 2, 3, 9, 5, DateTimeKind.Utc), call.ExpiresAt);
    }

    [Fact]
    public async Task TemporaryDisableWithoutDurationKeepsTheGenericReason()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([429], 1, Temporary)));

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 429, "rate limited"));

        var call = Assert.Single(harness.Credentials.DisableCalls);
        Assert.Equal("Disabled by auto-disable rule after 1 consecutive errors", call.Reason);
        Assert.Null(call.ExpiresAt);
    }

    [Fact]
    public async Task UntilCronDisablesUntilTheNextUtcOccurrence()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(
            ChannelPolicies("custom", Rule([401], 1, UntilCron, disableUntilCron: "0 9 * * *")));

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 401, "bad key"));

        var call = Assert.Single(harness.Credentials.DisableCalls);
        Assert.Equal(
            "Disabled until 2026-01-02T09:00:00Z by auto-disable rule after 1 consecutive errors",
            call.Reason);
        Assert.Equal(new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc), call.ExpiresAt);
    }

    [Fact]
    public async Task UntilCronHonoursTheConfiguredTimezone()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(
            ChannelPolicies(
                "custom",
                Rule(
                    [401],
                    1,
                    UntilCron,
                    disableUntilCron: "0 9 * * *",
                    disableUntilTimezone: "Asia/Shanghai")));

        // 03:04:05Z is 11:04:05 +08:00, so the next 09:00 wall clock is the
        // following day in Shanghai.
        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 401, "bad key"));

        var call = Assert.Single(harness.Credentials.DisableCalls);
        Assert.Equal(
            "Disabled until 2026-01-03T09:00:00+08:00 by auto-disable rule after 1 consecutive errors",
            call.Reason);
        Assert.Equal(new DateTime(2026, 1, 3, 1, 0, 0, DateTimeKind.Utc), call.ExpiresAt);
    }

    [Fact]
    public async Task UntilCronWithAnInvalidExpressionDoesNotAct()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(
            ChannelPolicies("custom", Rule([401], 1, UntilCron, disableUntilCron: "not a cron")));

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 401, "bad key"));

        Assert.Empty(harness.Credentials.DisableCalls);
    }

    [Fact]
    public void NextRuleCronOccurrenceReportsInvalidInputs()
    {
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

        var badCron = new AutoDisableRule([401], null, 1, UntilCron, null, "not a cron", string.Empty);
        var cronError = Assert.Throws<InvalidOperationException>(
            () => ChannelAutoDisableService.NextRuleCronOccurrence(badCron, now));
        Assert.Contains("invalid cron expression", cronError.Message, StringComparison.Ordinal);

        var badZone = new AutoDisableRule([401], null, 1, UntilCron, null, "0 9 * * *", "Not/AZone");
        var zoneError = Assert.Throws<InvalidOperationException>(
            () => ChannelAutoDisableService.NextRuleCronOccurrence(badZone, now));
        Assert.Contains("invalid timezone", zoneError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PermanentDeleteDisablesThenDeletesTheKey()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([401], 1, PermanentDelete)));

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 401, "bad key"));

        var call = Assert.Single(harness.Credentials.DisableCalls);
        Assert.Equal("sk-1", call.Key);
        Assert.Null(call.ExpiresAt);

        var delete = Assert.Single(harness.Credentials.DeleteCalls);
        Assert.Equal(channel.Id, delete.ChannelId);
        Assert.Equal(new[] { "sk-1" }, delete.Keys);
    }

    [Fact]
    public async Task OAuthPermanentDeleteDowngradesToPermanent()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([401], 1, PermanentDelete)));

        await harness.Service.EvaluateAsync(
            harness.Failure(channel.Id, ChannelCredentials.OAuthCredentialRef, 401, "bad key"));

        var call = Assert.Single(harness.Credentials.DisableCalls);
        Assert.Equal(ChannelCredentials.OAuthCredentialRef, call.Key);
        Assert.Empty(harness.Credentials.DeleteCalls);
    }

    [Fact]
    public async Task PreservedLastKeyStaysDisabled()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([401], 1, PermanentDelete)));
        harness.Credentials.DeleteResult = new DeleteDisabledApiKeysResult(false, "ONE_KEY_PRESERVED");

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, "sk-1", 401, "bad key"));

        Assert.Equal(2, harness.Credentials.DisableCalls.Count);
        Assert.Single(harness.Credentials.DeleteCalls);
    }

    [Fact]
    public async Task KeylessChannelRuleDisablesTheChannelAndNotifies()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([500], 1, Permanent)));

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, string.Empty, 500, "boom"));

        var call = Assert.Single(harness.Store.ChannelAutoDisableCalls);
        Assert.Equal(channel.Id, call.Id);
        Assert.Equal("Disabled by auto-disable rule after 1 consecutive errors", call.Reason);
        Assert.Equal(harness.Clock.GetUtcNow().UtcDateTime, call.AutoDisabledAt);
        Assert.Null(call.ExpiresAt);

        var row = Assert.Single(harness.Store.LiveRows);
        Assert.Equal("disabled", row.Status);
        Assert.Equal(call.Reason, row.ErrorMessage);

        var notification = Assert.Single(harness.Webhooks.Events);
        Assert.Equal(channel.Id, notification.ChannelId);
        Assert.Equal("primary", notification.ChannelName);
        Assert.Equal("openai", notification.ChannelProvider);
        Assert.Equal("https://api.example.com", notification.ChannelBaseUrl);
        Assert.Equal("disabled", notification.ChannelStatus);
        Assert.Equal(500, notification.StatusCode);
        Assert.Equal(1, notification.Threshold);
        Assert.Equal(1, notification.ActualCount);
        Assert.Equal(call.Reason, notification.Reason);
        Assert.Equal(harness.Clock.GetUtcNow(), notification.OccurredAt);

        Assert.Equal(1, harness.Refresher.ReloadCount);
        Assert.Equal(1, harness.Refresher.RequestCount);
    }

    [Fact]
    public async Task KeylessPermanentDeleteDegradesToPermanent()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([500], 1, PermanentDelete)));

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, string.Empty, 500, "boom"));

        var call = Assert.Single(harness.Store.ChannelAutoDisableCalls);
        Assert.Equal("Disabled by auto-disable rule after 1 consecutive errors", call.Reason);
        Assert.Null(call.ExpiresAt);
        Assert.Single(harness.Webhooks.Events);
    }

    [Fact]
    public async Task KeylessUntilCronSetsTheChannelExpiry()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(
            ChannelPolicies("custom", Rule([500], 1, UntilCron, disableUntilCron: "0 9 * * *")));

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, string.Empty, 500, "boom"));

        var call = Assert.Single(harness.Store.ChannelAutoDisableCalls);
        Assert.Equal(
            "Disabled until 2026-01-02T09:00:00Z by auto-disable rule after 1 consecutive errors",
            call.Reason);
        Assert.Equal(new DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc), call.ExpiresAt);
    }

    [Fact]
    public async Task AlreadyDisabledChannelRefreshesTheCacheWithoutNotifying()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(
            ChannelPolicies("custom", Rule([500], 1, Permanent)),
            status: "disabled");

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, string.Empty, 500, "boom"));

        Assert.Empty(harness.Store.ChannelAutoDisableCalls);
        Assert.Empty(harness.Webhooks.Events);
        Assert.Equal(1, harness.Refresher.ReloadCount);
        Assert.Equal(0, harness.Refresher.RequestCount);
    }

    [Fact]
    public async Task DisableStoreFailureIsSwallowed()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([500], 1, Permanent)));
        harness.Store.AutoDisableFailure = new InvalidOperationException("database down");

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, string.Empty, 500, "boom"));

        Assert.Empty(harness.Webhooks.Events);
        Assert.Equal(0, harness.Refresher.ReloadCount);
        Assert.Equal(0, harness.Refresher.RequestCount);
    }

    [Fact]
    public async Task MissingChannelSnapshotSkipsTheWebhookButStillReloads()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([500], 1, Permanent)));
        harness.Store.GetFailure = new InvalidOperationException("row vanished");

        await harness.Service.EvaluateAsync(harness.Failure(channel.Id, string.Empty, 500, "boom"));

        Assert.Empty(harness.Webhooks.Events);
        Assert.Equal(1, harness.Refresher.ReloadCount);
        Assert.Equal(1, harness.Refresher.RequestCount);
    }

    [Fact]
    public async Task EnabledChannelReadFailureSkipsEvaluation()
    {
        var clock = new AntigravityTestClock(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));
        var credentials = new FakeChannelCredentialMutations();
        var store = new FakeChannelWriteStore();
        var refresher = new RecordingEnabledChannelsRefresher();
        var settings = new FakeGcSettingsService
        {
            RetryPolicy = Global(GlobalRule([401], 1, Permanent)),
        };
        var service = new ChannelAutoDisableService(
            new FailingEnabledChannelProvider(new InvalidOperationException("cache down")),
            settings,
            credentials,
            store,
            new AutoDisableCounters(),
            refresher,
            new FakeWebhookSink(),
            clock);

        var failure = new ChannelPerformanceRecord
        {
            ChannelId = 1,
            StartTime = clock.GetUtcNow(),
            ApiKey = "sk-1",
        };
        failure.MarkFailed(401, "bad key", clock.GetUtcNow());

        await service.EvaluateAsync(failure);

        Assert.Empty(credentials.DisableCalls);
        Assert.Equal(0, refresher.RequestCount);
    }

    [Fact]
    public async Task EvaluateApiKeyRulesForFailureGuardsEmptyInputs()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([401], 1, Permanent)));

        Assert.False(await harness.Service.EvaluateApiKeyRulesForFailureAsync(0, "sk-1", 401, "m"));
        Assert.False(
            await harness.Service.EvaluateApiKeyRulesForFailureAsync(channel.Id, string.Empty, 401, "m"));
        Assert.Empty(harness.Credentials.DisableCalls);
    }

    [Fact]
    public async Task EvaluateApiKeyRulesForFailureCountsAndActs()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([401], 1, Permanent)));

        var acted = await harness.Service.EvaluateApiKeyRulesForFailureAsync(
            channel.Id,
            "sk-1",
            401,
            "bad key");

        Assert.True(acted);
        var call = Assert.Single(harness.Credentials.DisableCalls);
        Assert.Equal(401, call.ErrorCode);
        Assert.Equal("Disabled by auto-disable rule after 1 consecutive errors", call.Reason);
    }

    [Fact]
    public void CounterClaimsTheThresholdAndSubtractsIt()
    {
        var counters = new AutoDisableCounters();

        Assert.Equal((false, 1), counters.Increment(1, "rule", 3));
        Assert.Equal((false, 2), counters.Increment(1, "rule", 3));
        Assert.Equal((true, 3), counters.Increment(1, "rule", 3));

        // The action is in flight: further failures keep counting but the rule
        // cannot be claimed twice.
        Assert.Equal((false, 1), counters.Increment(1, "rule", 3));
    }

    [Fact]
    public void FailedActionRestoresTheStreak()
    {
        var counters = new AutoDisableCounters();

        counters.Increment(1, "rule", 3);
        counters.Increment(1, "rule", 3);
        Assert.Equal((true, 3), counters.Increment(1, "rule", 3));

        counters.CompleteAction(1, "rule", 3, actionSucceeded: false);

        Assert.Equal((true, 4), counters.Increment(1, "rule", 3));
    }

    [Fact]
    public void SuccessfulActionStartsANewStreak()
    {
        var counters = new AutoDisableCounters();

        counters.Increment(1, "rule", 2);
        Assert.Equal((true, 2), counters.Increment(1, "rule", 2));

        counters.CompleteAction(1, "rule", 2, actionSucceeded: true);

        Assert.Equal((false, 1), counters.Increment(1, "rule", 2));
    }

    [Fact]
    public void SuccessDuringActionSuppressesTheStreakRestoration()
    {
        var counters = new AutoDisableCounters();
        const string ruleKey = "sk-1:global:rule:0";

        Assert.Equal((false, 1), counters.Increment(1, ruleKey, 2));
        Assert.Equal((true, 2), counters.Increment(1, ruleKey, 2));

        counters.ClearOnSuccess(1, "sk-1");
        counters.CompleteAction(1, ruleKey, 2, actionSucceeded: false);

        // Restoring the failed action's streak would report 3 here.
        Assert.Equal((false, 1), counters.Increment(1, ruleKey, 2));
    }

    [Fact]
    public void ClearOnSuccessOnlyTouchesTheIdentity()
    {
        var counters = new AutoDisableCounters();

        counters.Increment(1, "sk-1:rule", 5);
        counters.Increment(1, "sk-2:rule", 5);

        counters.ClearOnSuccess(1, "sk-1");

        Assert.Equal((false, 2), counters.Increment(1, "sk-2:rule", 5));
        Assert.Equal((false, 1), counters.Increment(1, "sk-1:rule", 5));
    }

    [Fact]
    public void RuleCounterKeyFormatsLikeGoPercentV()
    {
        var rule = new AutoDisableRule(
            [401, 429],
            ["timeout", "bad gateway"],
            3,
            Permanent,
            null,
            string.Empty,
            string.Empty);

        Assert.Equal(
            "sk-1:global:rule:0:[401 429]:[timeout bad gateway]:3:permanent_disable:0",
            ChannelAutoDisableService.RuleCounterKey("sk-1", "global", 0, rule));

        var zero = new AutoDisableRule(null, null, 0, string.Empty, 5, string.Empty, string.Empty);
        Assert.Equal(
            "channel:7:channel:rule:2:[]:[]:0::5",
            ChannelAutoDisableService.RuleCounterKey("channel:7", "channel", 2, zero));
    }

    [Fact]
    public void CounterIdentityUsesTheChannelForKeylessRecords()
    {
        using var harness = new Harness();

        Assert.Equal(
            "sk-1",
            ChannelAutoDisableService.CounterIdentity(harness.Failure(7, "sk-1", 500, "x")));
        Assert.Equal(
            "channel:7",
            ChannelAutoDisableService.CounterIdentity(harness.Failure(7, string.Empty, 500, "x")));
    }

    [Fact]
    public void MatchesRequiresTheConfiguredStatusCode()
    {
        using var harness = new Harness();
        var rule = new AutoDisableRule([401], null, 1, Permanent, null, string.Empty, string.Empty);

        Assert.True(ChannelAutoDisableService.Matches(rule, harness.Failure(1, "sk", 401, "x")));
        Assert.False(ChannelAutoDisableService.Matches(rule, harness.Failure(1, "sk", 403, "x")));
    }

    [Fact]
    public void MatchesTreatsEmptyConditionsAsAnyFailure()
    {
        using var harness = new Harness();
        var rule = new AutoDisableRule(null, null, 1, Permanent, null, string.Empty, string.Empty);

        Assert.True(ChannelAutoDisableService.Matches(rule, harness.Failure(1, "sk", 500, "boom")));
        Assert.True(ChannelAutoDisableService.Matches(rule, harness.Failure(1, "sk", 500, string.Empty)));
    }

    [Fact]
    public void MatchesUsesCaseInsensitiveRegexes()
    {
        using var harness = new Harness();
        var rule = new AutoDisableRule(
            null,
            ["timeout.*expired"],
            1,
            Permanent,
            null,
            string.Empty,
            string.Empty);

        Assert.True(
            ChannelAutoDisableService.Matches(rule, harness.Failure(1, "sk", 500, "A TIMEOUT has expired")));
        Assert.False(
            ChannelAutoDisableService.Matches(rule, harness.Failure(1, "sk", 500, "connection reset")));
    }

    [Fact]
    public void MatchesFallsBackToLiteralContainsForInvalidRegex()
    {
        using var harness = new Harness();
        var rule = new AutoDisableRule(
            null,
            ["["],
            1,
            Permanent,
            null,
            string.Empty,
            string.Empty);

        Assert.True(
            ChannelAutoDisableService.Matches(rule, harness.Failure(1, "sk", 500, "body [ truncated")));
        Assert.False(
            ChannelAutoDisableService.Matches(rule, harness.Failure(1, "sk", 500, "no bracket")));
    }

    [Fact]
    public void MatchesRejectsKeywordRulesWithoutAMessage()
    {
        using var harness = new Harness();
        var rule = new AutoDisableRule(
            null,
            ["boom"],
            1,
            Permanent,
            null,
            string.Empty,
            string.Empty);

        Assert.False(ChannelAutoDisableService.Matches(rule, harness.Failure(1, "sk", 500, string.Empty)));
    }

    [Fact]
    public void DeriveErrorMessageUsesTheGoStatusText()
    {
        Assert.Equal("Unauthorized", ChannelAutoDisableService.DeriveErrorMessage(401));
        Assert.Equal("Error 599", ChannelAutoDisableService.DeriveErrorMessage(599));
    }

    [Fact]
    public void ExtractErrorCodeUsesTheHttpStatusOrDefaultsTo500()
    {
        // performance.go:255-266: only an HTTP error carries a provider status.
        Assert.Equal(
            502,
            ChannelMetricsMiddleware.ExtractErrorCode(
                new HttpClientError("POST", "https://api.example.com", 502, "502 Bad Gateway")));
        Assert.Equal(
            500,
            ChannelMetricsMiddleware.ExtractErrorCode(new InvalidOperationException("boom")));
    }

    [Fact]
    public void ExtractErrorMessageForMatchingAppendsAtMostEightKilobytesOfBody()
    {
        // performance.go:268-281: the raw body is appended so keyword patterns
        // can match provider prose outside the JSON envelope; the cut is 8 KiB.
        var body = new byte[9000];
        Array.Fill(body, (byte)'a');
        Encoding.ASCII.GetBytes("TAIL").CopyTo(body, 8996);
        var error = new HttpClientError(
            "POST",
            "https://api.example.com/v1/chat/completions",
            502,
            "502 Bad Gateway",
            body);

        var message = ChannelMetricsMiddleware.ExtractErrorMessageForMatching(error);
        var appended = message[(message.IndexOf('\n') + 1)..];

        Assert.Equal(
            "POST - https://api.example.com/v1/chat/completions with status 502 Bad Gateway",
            message[..message.IndexOf('\n')]);
        Assert.Equal(8 * 1024, appended.Length);
        Assert.DoesNotContain("TAIL", message, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedUtf8AtTheErrorMatchCutDecodesAsAReplacementCharacter()
    {
        // C262: Go hands `string(body[:8192])` through unchanged even when the
        // cut splits a multi-byte sequence; .NET decodes, so the split code
        // point surfaces as U+FFFD instead of the raw bytes.
        var body = new byte[(8 * 1024) + 1];
        Array.Fill(body, (byte)'a');
        body[8191] = 0xC3; // first byte of "é"...
        body[8192] = 0xA9; // ...and its continuation, past the 8 KiB cut.

        var error = new HttpClientError("POST", "https://api.example.com", 502, "502 Bad Gateway", body);

        var message = ChannelMetricsMiddleware.ExtractErrorMessageForMatching(error);
        var appended = message[(message.IndexOf('\n') + 1)..];

        Assert.Equal(8 * 1024, appended.Length);
        Assert.EndsWith("�", appended, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailureDuringEvaluationStillRunsTheBestEffortReload()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([500], 1, Permanent)));
        harness.Refresher.ReloadFailure = new InvalidOperationException("reload failed");

        // The reload failure is logged and swallowed; the disable and the
        // watcher notification still happen.
        await harness.Service
            .EvaluateAsync(harness.Failure(channel.Id, string.Empty, 500, "boom"))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(harness.Store.ChannelAutoDisableCalls);
        Assert.Equal(1, harness.Refresher.RequestCount);
    }

    [Fact]
    public async Task EvaluationQueueProcessesEnqueuedRecords()
    {
        using var harness = new Harness();
        var channel = harness.SeedChannel(ChannelPolicies("custom", Rule([401], 1, Permanent)));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEnabledChannelProvider>(harness.Enabled);
        services.AddSingleton<ISystemSettingsAdminService>(harness.Settings);
        services.AddSingleton<IChannelCredentialMutations>(harness.Credentials);
        services.AddSingleton<IChannelWriteStore>(harness.Store);
        services.AddSingleton(harness.Counters);
        services.AddSingleton<IEnabledChannelsCacheRefresher>(harness.Refresher);
        services.AddSingleton<IWebhookNotificationSink>(harness.Webhooks);
        services.AddSingleton<TimeProvider>(harness.Clock);
        services.AddScoped<ChannelAutoDisableService>();

        await using var provider = services.BuildServiceProvider();
        var queue = new ChannelAutoDisableEvaluationQueue(
            provider.GetRequiredService<IServiceScopeFactory>());

        await queue.StartAsync(CancellationToken.None);
        try
        {
            queue.Enqueue(harness.Failure(channel.Id, "sk-1", 401, "bad key"));
            var call = await harness.Credentials.DisableStarted.Task
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("sk-1", call.Key);
        }
        finally
        {
            await queue.StopAsync(CancellationToken.None);
            queue.Dispose();
        }
    }

    /// <summary>
    /// The builders stay close to the reference's JSON shapes: policies are the
    /// stored <c>channel.policies</c> document and rules mirror
    /// objects.APIKeyAutoDisableRule.
    /// </summary>
    private static string ChannelPolicies(string? mode, params object[] rules)
    {
        var payload = new Dictionary<string, object?>
        {
            ["apiKeyAutoDisableRules"] = rules,
        };
        if (mode is not null)
        {
            payload["apiKeyAutoDisableMode"] = mode;
        }

        return JsonSerializer.Serialize(payload);
    }

    private static object Rule(
        int[]? statusCodes,
        int times,
        string action,
        int? disableDurationMinutes = null,
        string? disableUntilCron = null,
        string? disableUntilTimezone = null)
        => new
        {
            statusCodes,
            times,
            action,
            disableDurationMinutes,
            disableUntilCron,
            disableUntilTimezone,
        };

    private static RetryPolicy Global(params AutoDisableRule[] rules)
        => new() { AutoDisableChannel = new AutoDisableChannel(true, rules) };

    private static AutoDisableRule GlobalRule(
        int[]? statusCodes,
        int times,
        string action)
        => new(statusCodes, null, times, action, null, string.Empty, string.Empty);

    private sealed class Harness : IDisposable
    {
        public FakeEnabledChannelProvider Enabled { get; } = new();

        public FakeGcSettingsService Settings { get; } = new();

        public FakeChannelCredentialMutations Credentials { get; } = new();

        public FakeChannelWriteStore Store { get; } = new();

        public RecordingEnabledChannelsRefresher Refresher { get; } = new();

        public FakeWebhookSink Webhooks { get; } = new();

        public AntigravityTestClock Clock { get; } =
            new(new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero));

        public AutoDisableCounters Counters { get; } = new();

        public ChannelAutoDisableService Service { get; }

        public Harness()
        {
            Service = new ChannelAutoDisableService(
                Enabled,
                Settings,
                Credentials,
                Store,
                Counters,
                Refresher,
                Webhooks,
                Clock);
        }

        public ChannelRecord SeedChannel(
            string? policies = null,
            string status = "enabled",
            string type = "openai",
            string name = "primary")
        {
            var record = new ChannelRecord
            {
                Name = name,
                Type = type,
                BaseUrl = "https://api.example.com",
                Status = status,
                Policies = policies,
            };
            Store.Seed(record);

            Enabled.Set(new ChannelDescriptor
            {
                Id = record.Id,
                Name = record.Name,
                OrderingWeight = 0,
                Type = record.Type,
                BaseUrl = record.BaseUrl,
                Policies = record.Policies,
            });

            return record;
        }

        public ChannelPerformanceRecord Failure(
            long channelId,
            string apiKey,
            int statusCode,
            string message)
        {
            var record = new ChannelPerformanceRecord
            {
                ChannelId = channelId,
                StartTime = Clock.GetUtcNow(),
                ApiKey = apiKey,
            };
            record.MarkFailed(statusCode, message, Clock.GetUtcNow());
            return record;
        }

        public ChannelPerformanceRecord Success(long channelId, string apiKey)
        {
            var record = new ChannelPerformanceRecord
            {
                ChannelId = channelId,
                StartTime = Clock.GetUtcNow(),
                ApiKey = apiKey,
            };
            record.MarkSuccess(Clock.GetUtcNow());
            return record;
        }

        public ChannelPerformanceRecord Canceled(long channelId, string apiKey)
        {
            var record = new ChannelPerformanceRecord
            {
                ChannelId = channelId,
                StartTime = Clock.GetUtcNow(),
                ApiKey = apiKey,
            };
            record.MarkCanceled(Clock.GetUtcNow());
            return record;
        }

        public void Dispose()
        {
            // The harness owns no unmanaged state; the method exists so the
            // tests can use `using var` and future fields stay disposable.
            GC.SuppressFinalize(this);
        }
    }
}
