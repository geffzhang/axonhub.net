using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using AxonHub.Abstractions;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Settings;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Management;

/// <summary>
/// The credential mutations the auto-disable engine drives
/// (<c>ChannelService.DisableAPIKey</c> / <c>DeleteDisabledAPIKeys</c>). The
/// engine is separated from the concrete lifecycle service so tests can script
/// these results without standing up the whole channel store.
/// </summary>
public interface IChannelCredentialMutations
{
    Task DisableApiKeyAsync(
        long channelId,
        string key,
        int errorCode,
        string reason,
        DateTime? expiresAt = null,
        CancellationToken cancellationToken = default);

    Task<DeleteDisabledApiKeysResult> DeleteDisabledApiKeysAsync(
        long channelId,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Port of the auto-disable bookkeeping fields of biz.ChannelService
/// (channel_auto_disable.go): <c>apiKeyErrorCounts</c> and
/// <c>apiKeyRuleActionsInFlight</c> behind one mutex.
/// </summary>
/// <remarks>
/// The maps live for the lifetime of the process, exactly like the reference's
/// in-memory maps on the ChannelService instance; the reference's single
/// background worker is replaced by the same singleton service, so the counters
/// observed under concurrent failures keep the reference's lock discipline.
/// </remarks>
public sealed class AutoDisableCounters
{
    private readonly object _lock = new();
    private readonly Dictionary<long, Dictionary<string, Dictionary<int, int>>> _counts = [];
    private readonly Dictionary<long, Dictionary<string, bool>> _actionsInFlight = [];

    /// <summary>
    /// Port of the counting half of evaluateRules (channel_auto_disable.go:225-248):
    /// increments the matched rule's single consecutive counter, claims the rule
    /// when the threshold is reached and subtracts the threshold so the next
    /// streak starts from zero.
    /// </summary>
    /// <returns>Whether the caller must execute the action, and the count that triggered it.</returns>
    public (bool ShouldAct, int Count) Increment(long channelId, string ruleKey, int threshold)
    {
        lock (_lock)
        {
            if (!_counts.TryGetValue(channelId, out var byRule))
            {
                byRule = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
                _counts[channelId] = byRule;
            }

            if (!_actionsInFlight.TryGetValue(channelId, out var inFlight))
            {
                inFlight = new Dictionary<string, bool>(StringComparer.Ordinal);
                _actionsInFlight[channelId] = inFlight;
            }

            if (!byRule.TryGetValue(ruleKey, out var counts))
            {
                counts = [];
                byRule[ruleKey] = counts;
            }

            // The reference uses one counter per rule (countKey 0); alternating
            // configured status codes still belong to the same streak.
            counts.TryGetValue(0, out var count);
            count++;
            counts[0] = count;

            var actionInFlight = inFlight.ContainsKey(ruleKey);
            var shouldAct = count >= threshold && !actionInFlight;
            if (shouldAct)
            {
                counts[0] -= threshold;
                inFlight[ruleKey] = false;
            }

            return (shouldAct, count);
        }
    }

    /// <summary>
    /// Port of the post-action half of evaluateRules (channel_auto_disable.go:250-268).
    /// A streak reset during the action wins over a failed action: the count is
    /// not restored, so the next failure starts a fresh streak.
    /// </summary>
    public void CompleteAction(long channelId, string ruleKey, int threshold, bool actionSucceeded)
    {
        lock (_lock)
        {
            if (!_actionsInFlight.TryGetValue(channelId, out var inFlight)
                || !inFlight.TryGetValue(ruleKey, out var streakReset))
            {
                return;
            }

            inFlight.Remove(ruleKey);

            if (!_counts.TryGetValue(channelId, out var byRule))
            {
                byRule = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
                _counts[channelId] = byRule;
            }

            if (!byRule.TryGetValue(ruleKey, out var counts))
            {
                counts = [];
                byRule[ruleKey] = counts;
            }

            if (!actionSucceeded && !streakReset)
            {
                counts.TryGetValue(0, out var count);
                counts[0] = count + threshold;
            }

            if (counts.GetValueOrDefault(0) == 0)
            {
                byRule.Remove(ruleKey);
            }
        }
    }

    /// <summary>
    /// Port of clearAutoDisableCountsOnSuccess (channel_auto_disable.go:278-294):
    /// drops every counter of the identity and flags the in-flight action, which
    /// tells the action's completion not to restore its streak.
    /// </summary>
    public void ClearOnSuccess(long channelId, string identity)
    {
        lock (_lock)
        {
            var prefix = identity + ":";
            if (_counts.TryGetValue(channelId, out var byRule))
            {
                foreach (var key in byRule.Keys
                             .Where(key => string.Equals(key, identity, StringComparison.Ordinal)
                                 || key.StartsWith(prefix, StringComparison.Ordinal))
                             .ToList())
                {
                    byRule.Remove(key);
                }
            }

            if (_actionsInFlight.TryGetValue(channelId, out var inFlight))
            {
                foreach (var key in inFlight.Keys
                             .Where(key => string.Equals(key, identity, StringComparison.Ordinal)
                                 || key.StartsWith(prefix, StringComparison.Ordinal))
                             .ToList())
                {
                    inFlight[key] = true;
                }
            }
        }
    }
}

/// <summary>
/// Port of biz.ChannelService's auto-disable evaluation and action engine
/// (channel_auto_disable.go:169-565): channel rules first when the effective
/// mode is custom, then the global retry-policy rules, each failure feeding one
/// rule's consecutive counter and each claimed streak driving one credential or
/// channel action.
/// </summary>
public sealed class ChannelAutoDisableService
{
    private const string ScopeChannel = "channel";
    private const string ScopeGlobal = "global";

    private static readonly Action<ILogger, long, string, Exception?> LogApiKeyCronScheduleFailed =
        LoggerMessage.Define<long, string>(
            LogLevel.Error,
            new EventId(0, "ApiKeyRuleCronScheduleFailed"),
            "Failed to resolve api key rule cron schedule channel_id={ChannelId} cron={Cron}");

    private static readonly Action<ILogger, long, Exception?> LogDisableUntilCronFailed =
        LoggerMessage.Define<long>(
            LogLevel.Error,
            new EventId(0, "DisableApiKeyUntilCronFailed"),
            "Failed to disable API key until cron occurrence channel_id={ChannelId}");

    private static readonly Action<ILogger, long, Exception?> LogPermanentDisableFailed =
        LoggerMessage.Define<long>(
            LogLevel.Error,
            new EventId(0, "PermanentDisableApiKeyFailed"),
            "Failed to permanently disable API key by auto-disable rule channel_id={ChannelId}");

    private static readonly Action<ILogger, long, Exception?> LogDeleteDisabledKeyFailed =
        LoggerMessage.Define<long>(
            LogLevel.Error,
            new EventId(0, "DeleteDisabledApiKeyFailed"),
            "Failed to delete API key disabled by auto-disable rule channel_id={ChannelId}");

    private static readonly Action<ILogger, long, Exception?> LogKeepPreservedKeyFailed =
        LoggerMessage.Define<long>(
            LogLevel.Error,
            new EventId(0, "KeepPreservedApiKeyFailed"),
            "Failed to keep preserved API key disabled by auto-disable rule channel_id={ChannelId}");

    private static readonly Action<ILogger, long, Exception?> LogTemporaryDisableFailed =
        LoggerMessage.Define<long>(
            LogLevel.Error,
            new EventId(0, "TemporaryDisableApiKeyFailed"),
            "Failed to temporarily disable API key by auto-disable rule channel_id={ChannelId}");

    private static readonly Action<ILogger, long, string, Exception?> LogChannelCronScheduleFailed =
        LoggerMessage.Define<long, string>(
            LogLevel.Error,
            new EventId(0, "ChannelRuleCronScheduleFailed"),
            "Failed to resolve channel auto-disable cron schedule channel_id={ChannelId} cron={Cron}");

    private static readonly Action<ILogger, long, Exception?> LogPermanentDeleteDegraded =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(0, "PermanentDeleteDegraded"),
            "permanent_disable_delete degraded to permanent_disable for keyless channel channel_id={ChannelId}");

    private static readonly Action<ILogger, long, int, Exception?> LogDisableChannelFailed =
        LoggerMessage.Define<long, int>(
            LogLevel.Error,
            new EventId(0, "DisableChannelFailed"),
            "Failed to disable channel on unrecoverable error channel_id={ChannelId} error_code={StatusCode}");

    private static readonly Action<ILogger, long, int, Exception?> LogChannelAlreadyDisabled =
        LoggerMessage.Define<long, int>(
            LogLevel.Debug,
            new EventId(0, "ChannelAlreadyDisabled"),
            "Channel already disabled, skipping channel_id={ChannelId} error_code={StatusCode}");

    private static readonly Action<ILogger, long, int, Exception?> LogChannelDisabled =
        LoggerMessage.Define<long, int>(
            LogLevel.Warning,
            new EventId(0, "ChannelDisabled"),
            "Channel disabled due to unrecoverable error channel_id={ChannelId} error_code={StatusCode}");

    private static readonly Action<ILogger, long, Exception?> LogFetchDisabledChannelFailed =
        LoggerMessage.Define<long>(
            LogLevel.Error,
            new EventId(0, "FetchDisabledChannelFailed"),
            "Failed to fetch disabled channel for webhook notification channel_id={ChannelId}");

    private static readonly Action<ILogger, long, Exception?> LogFetchDisabledChannelMissing =
        LoggerMessage.Define<long>(
            LogLevel.Error,
            new EventId(0, "FetchDisabledChannelMissing"),
            "Failed to fetch disabled channel for webhook notification channel_id={ChannelId}");

    private static readonly Action<ILogger, long, Exception?> LogReloadAfterAutoDisableFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(0, "ReloadAfterAutoDisableFailed"),
            "Failed to synchronously reload channels after auto-disable channel_id={ChannelId}");

    private static readonly Action<ILogger, long, Exception?> LogReloadForAlreadyDisabledFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(0, "ReloadForAlreadyDisabledFailed"),
            "Failed to refresh local cache for already-disabled channel channel_id={ChannelId}");

    private static readonly Action<ILogger, Exception?> LogEnabledChannelsReadFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(0, "EnabledChannelsReadFailed"),
            "failed to read enabled channels for auto-disable evaluation");

    private static readonly Action<ILogger, Exception?> LogRetryPolicyReadFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(0, "RetryPolicyReadFailed"),
            "failed to get retry policy");

    private static readonly ConcurrentDictionary<string, Regex?> CompiledRuleRegexes =
        new(StringComparer.Ordinal);

    private readonly IEnabledChannelProvider _enabledChannels;
    private readonly ISystemSettingsAdminService _settings;
    private readonly IChannelCredentialMutations _credentials;
    private readonly IChannelWriteStore _channels;
    private readonly AutoDisableCounters _counters;
    private readonly IEnabledChannelsCacheRefresher? _cacheRefresher;
    private readonly IWebhookNotificationSink? _webhookSink;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ChannelAutoDisableService>? _logger;

    public ChannelAutoDisableService(
        IEnabledChannelProvider enabledChannels,
        ISystemSettingsAdminService settings,
        IChannelCredentialMutations credentials,
        IChannelWriteStore channels,
        AutoDisableCounters counters,
        IEnabledChannelsCacheRefresher? cacheRefresher = null,
        IWebhookNotificationSink? webhookSink = null,
        TimeProvider? timeProvider = null,
        ILogger<ChannelAutoDisableService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(enabledChannels);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(counters);

        _enabledChannels = enabledChannels;
        _settings = settings;
        _credentials = credentials;
        _channels = channels;
        _counters = counters;
        _cacheRefresher = cacheRefresher;
        _webhookSink = webhookSink;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>
    /// Port of RecordPerformance's counter dispatch (channel_metrics.go:515-530):
    /// a success resets the identity's streaks, a cancellation is ignored and a
    /// failure evaluates the rules.
    /// </summary>
    public async Task EvaluateAsync(
        ChannelPerformanceRecord performance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(performance);

        if (!performance.IsValid)
        {
            return;
        }

        if (performance.Success)
        {
            _counters.ClearOnSuccess(performance.ChannelId, CounterIdentity(performance));
            return;
        }

        if (!performance.Canceled)
        {
            await EvaluateFailureAsync(performance, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Port of EvaluateAPIKeyRulesForFailure (channel_auto_disable.go:149-167):
    /// evaluates a keyed failure that was persisted outside the normal
    /// performance path, bypassing the IsValid guard exactly like the reference.
    /// </summary>
    public async Task<bool> EvaluateApiKeyRulesForFailureAsync(
        long channelId,
        string apiKey,
        int responseStatusCode,
        string errorMessage,
        CancellationToken cancellationToken = default)
    {
        if (channelId == 0 || string.IsNullOrEmpty(apiKey))
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        var performance = new ChannelPerformanceRecord
        {
            ChannelId = channelId,
            StartTime = now,
            ApiKey = apiKey,
        };
        performance.MarkFailed(responseStatusCode, errorMessage, now);

        var (_, acted) = await EvaluateFailureAsync(performance, cancellationToken)
            .ConfigureAwait(false);
        return acted;
    }

    /// <summary>
    /// Port of evaluateAutoDisableForFailure (channel_auto_disable.go:169-191).
    /// </summary>
    private async Task<(bool Matched, bool Acted)> EvaluateFailureAsync(
        ChannelPerformanceRecord performance,
        CancellationToken cancellationToken)
    {
        var channel = await FindEnabledChannelAsync(performance.ChannelId, cancellationToken)
            .ConfigureAwait(false);
        if (channel is null)
        {
            return (false, false);
        }

        var policies = ParsePolicies(channel.Policies);
        switch (EffectiveMode(policies.Mode, policies.Rules.Count))
        {
            case "off":
                return (false, false);
            case "custom":
                var (matched, acted) = await EvaluateRulesAsync(
                        performance,
                        policies.Rules,
                        ScopeChannel,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (matched)
                {
                    return (matched, acted);
                }

                break;
        }

        var policy = await GetRetryPolicyOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (!policy.AutoDisableChannel.Enabled || policy.AutoDisableChannel.Rules.Count == 0)
        {
            return (false, false);
        }

        return await EvaluateRulesAsync(
                performance,
                policy.AutoDisableChannel.Rules,
                ScopeGlobal,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Port of evaluateRules (channel_auto_disable.go:206-276): only the first
    /// matching rule is evaluated, and only a claimed streak returns acted.
    /// </summary>
    private async Task<(bool Matched, bool Acted)> EvaluateRulesAsync(
        ChannelPerformanceRecord performance,
        IReadOnlyList<AutoDisableRule> rules,
        string scope,
        CancellationToken cancellationToken)
    {
        if (rules.Count == 0)
        {
            return (false, false);
        }

        for (var ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
        {
            var rule = rules[ruleIndex];
            if (!Matches(rule, performance))
            {
                continue;
            }

            var ruleKey = RuleCounterKey(CounterIdentity(performance), scope, ruleIndex, rule);
            var threshold = Math.Max(rule.Times, 1);
            var (shouldAct, count) = _counters.Increment(
                performance.ChannelId,
                ruleKey,
                threshold);

            if (shouldAct)
            {
                var actionSucceeded = await ExecuteMatchedRuleActionAsync(
                        performance,
                        rule,
                        count,
                        cancellationToken)
                    .ConfigureAwait(false);
                _counters.CompleteAction(
                    performance.ChannelId,
                    ruleKey,
                    threshold,
                    actionSucceeded);
                return (true, actionSucceeded);
            }

            return (true, false);
        }

        return (false, false);
    }

    /// <summary>
    /// Port of executeMatchedRuleAction (channel_auto_disable.go:397-408):
    /// a keyless channel acts on the channel, everything else on the credential.
    /// </summary>
    private Task<bool> ExecuteMatchedRuleActionAsync(
        ChannelPerformanceRecord performance,
        AutoDisableRule rule,
        int count,
        CancellationToken cancellationToken)
        => string.IsNullOrEmpty(performance.ApiKey)
            ? ExecuteChannelRuleActionAsync(performance, rule, count, cancellationToken)
            : ExecuteApiKeyRuleActionAsync(performance, rule, count, cancellationToken);

    /// <summary>
    /// Port of executeAPIKeyRuleAction (channel_auto_disable.go:410-523).
    /// </summary>
    private async Task<bool> ExecuteApiKeyRuleActionAsync(
        ChannelPerformanceRecord performance,
        AutoDisableRule rule,
        int count,
        CancellationToken cancellationToken)
    {
        var reason = DisabledReason(count);
        var action = rule.Action;

        // An OAuth channel's credential cannot be deleted; keep it disabled
        // instead so a failed action does not restore the streak forever.
        if (action == RetryPolicy.AutoDisableActionPermanentDelete
            && performance.ApiKey == ChannelCredentials.OAuthCredentialRef)
        {
            action = RetryPolicy.AutoDisableActionPermanent;
        }

        switch (action)
        {
            case RetryPolicy.AutoDisableActionUntilCron:
            {
                DateTimeOffset expiresAt;
                try
                {
                    expiresAt = NextRuleCronOccurrence(rule, _timeProvider.GetUtcNow());
                }
                catch (InvalidOperationException ex)
                {
                    if (_logger is { } cronLogger)
                    {
                        LogApiKeyCronScheduleFailed(cronLogger, performance.ChannelId, rule.DisableUntilCron, ex);
                    }

                    return false;
                }

                reason = "Disabled until " + FormatRfc3339(expiresAt)
                    + " by auto-disable rule after " + count.ToString(CultureInfo.InvariantCulture)
                    + " consecutive errors";

                try
                {
                    await _credentials.DisableApiKeyAsync(
                            performance.ChannelId,
                            performance.ApiKey,
                            performance.ResponseStatusCode,
                            reason,
                            expiresAt.UtcDateTime,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (_logger is { } untilCronLogger)
                    {
                        LogDisableUntilCronFailed(untilCronLogger, performance.ChannelId, ex);
                    }

                    return false;
                }

                return true;
            }

            case RetryPolicy.AutoDisableActionPermanent:
            {
                try
                {
                    await _credentials.DisableApiKeyAsync(
                            performance.ChannelId,
                            performance.ApiKey,
                            performance.ResponseStatusCode,
                            reason,
                            null,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (_logger is { } permanentLogger)
                    {
                        LogPermanentDisableFailed(permanentLogger, performance.ChannelId, ex);
                    }

                    return false;
                }

                return true;
            }

            case RetryPolicy.AutoDisableActionPermanentDelete:
            {
                try
                {
                    await _credentials.DisableApiKeyAsync(
                            performance.ChannelId,
                            performance.ApiKey,
                            performance.ResponseStatusCode,
                            reason,
                            null,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (_logger is { } permanentLogger)
                    {
                        LogPermanentDisableFailed(permanentLogger, performance.ChannelId, ex);
                    }

                    return false;
                }

                DeleteDisabledApiKeysResult result;
                try
                {
                    result = await _credentials.DeleteDisabledApiKeysAsync(
                            performance.ChannelId,
                            [performance.ApiKey],
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (_logger is { } deleteLogger)
                    {
                        LogDeleteDisabledKeyFailed(deleteLogger, performance.ChannelId, ex);
                    }

                    return false;
                }

                // Channels must retain at least one credential. When deletion
                // cannot remove the last key, keep it permanently disabled; the
                // delete helper would otherwise re-enable the channel.
                if (string.Equals(result.Message, "ONE_KEY_PRESERVED", StringComparison.Ordinal))
                {
                    try
                    {
                        await _credentials.DisableApiKeyAsync(
                                performance.ChannelId,
                                performance.ApiKey,
                                performance.ResponseStatusCode,
                                reason,
                                null,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        if (_logger is { } preservedLogger)
                        {
                            LogKeepPreservedKeyFailed(preservedLogger, performance.ChannelId, ex);
                        }

                        return false;
                    }
                }

                return true;
            }
        }

        DateTime? temporaryExpiresAt = null;
        if (rule.DisableDurationMinutes is { } minutes)
        {
            temporaryExpiresAt = _timeProvider.GetUtcNow().AddMinutes(minutes).UtcDateTime;
            reason = "Temporarily disabled for " + minutes.ToString(CultureInfo.InvariantCulture)
                + " minutes by auto-disable rule after " + count.ToString(CultureInfo.InvariantCulture)
                + " consecutive errors";
        }

        try
        {
            await _credentials.DisableApiKeyAsync(
                    performance.ChannelId,
                    performance.ApiKey,
                    performance.ResponseStatusCode,
                    reason,
                    temporaryExpiresAt,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger is { } temporaryLogger)
            {
                LogTemporaryDisableFailed(temporaryLogger, performance.ChannelId, ex);
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Port of executeChannelRuleAction (channel_auto_disable.go:525-565).
    /// </summary>
    private async Task<bool> ExecuteChannelRuleActionAsync(
        ChannelPerformanceRecord performance,
        AutoDisableRule rule,
        int count,
        CancellationToken cancellationToken)
    {
        var reason = DisabledReason(count);
        var action = rule.Action;
        if (action == RetryPolicy.AutoDisableActionPermanentDelete)
        {
            if (_logger is { } degradedLogger)
            {
                LogPermanentDeleteDegraded(degradedLogger, performance.ChannelId, null);
            }

            action = RetryPolicy.AutoDisableActionPermanent;
        }

        DateTime? expiresAt = null;
        switch (action)
        {
            case RetryPolicy.AutoDisableActionUntilCron:
            {
                DateTimeOffset next;
                try
                {
                    next = NextRuleCronOccurrence(rule, _timeProvider.GetUtcNow());
                }
                catch (InvalidOperationException ex)
                {
                    if (_logger is { } channelCronLogger)
                    {
                        LogChannelCronScheduleFailed(
                            channelCronLogger,
                            performance.ChannelId,
                            rule.DisableUntilCron,
                            ex);
                    }

                    return false;
                }

                expiresAt = next.UtcDateTime;
                reason = "Disabled until " + FormatRfc3339(next)
                    + " by auto-disable rule after " + count.ToString(CultureInfo.InvariantCulture)
                    + " consecutive errors";
                break;
            }

            case RetryPolicy.AutoDisableActionTemporary:
                if (rule.DisableDurationMinutes is { } minutes)
                {
                    expiresAt = _timeProvider.GetUtcNow().AddMinutes(minutes).UtcDateTime;
                    reason = "Temporarily disabled for " + minutes.ToString(CultureInfo.InvariantCulture)
                        + " minutes by auto-disable rule after "
                        + count.ToString(CultureInfo.InvariantCulture) + " consecutive errors";
                }

                break;
        }

        await MarkChannelUnavailableAsync(
                performance.ChannelId,
                performance.ResponseStatusCode,
                Math.Max(rule.Times, 1),
                count,
                expiresAt,
                reason,
                cancellationToken)
            .ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// Port of markChannelUnavailable (channel_auto_disable.go:29-130): a
    /// conditional update that only disables an enabled channel, a best-effort
    /// notification with the updated snapshot, and the local plus cross-instance
    /// cache reloads.
    /// </summary>
    private async Task MarkChannelUnavailableAsync(
        long channelId,
        int responseStatusCode,
        int threshold,
        int actualCount,
        DateTime? expiresAt,
        string reason,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        // xcontext.DetachWithTimeout: the caller's cancellation must not abort
        // the best-effort disable, and the whole block is bounded to ten seconds.
        using var detached = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        if (reason.Length == 0)
        {
            reason = DeriveErrorMessage(responseStatusCode);
        }

        var now = _timeProvider.GetUtcNow();

        bool affected;
        try
        {
            affected = await _channels
                .AutoDisableAsync(
                    channelId,
                    reason,
                    now.UtcDateTime,
                    expiresAt,
                    detached.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger is { } disableLogger)
            {
                LogDisableChannelFailed(disableLogger, channelId, responseStatusCode, ex);
            }

            return;
        }

        if (!affected)
        {
            if (_logger is { } skippedLogger)
            {
                LogChannelAlreadyDisabled(skippedLogger, channelId, responseStatusCode, null);
            }

            // Another instance may have disabled the channel while this one
            // still serves it from a stale cache; force a local refresh.
            await ReloadLocalCacheAsync(LogReloadForAlreadyDisabledFailed, channelId)
                .ConfigureAwait(false);
            return;
        }

        if (_logger is { } disabledLogger)
        {
            LogChannelDisabled(disabledLogger, channelId, responseStatusCode, null);
        }

        ChannelRecord? updated = null;
        var fetchFailed = false;
        try
        {
            updated = await _channels.GetAsync(channelId, detached.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            fetchFailed = true;
            if (_logger is { } fetchLogger)
            {
                LogFetchDisabledChannelFailed(fetchLogger, channelId, ex);
            }
        }

        if (updated is not null)
        {
            _webhookSink?.PublishChannelAutoDisabled(new ChannelAutoDisabledEvent
            {
                ChannelId = updated.Id,
                ChannelName = updated.Name,
                ChannelProvider = updated.Type,
                ChannelBaseUrl = updated.BaseUrl ?? string.Empty,
                ChannelStatus = updated.Status,
                StatusCode = responseStatusCode,
                Threshold = threshold,
                ActualCount = actualCount,
                Reason = reason,
                OccurredAt = now,
            });
        }
        else if (!fetchFailed && _logger is { } missingLogger)
        {
            // ent's Get returns a not-found error; the store returns null.
            LogFetchDisabledChannelMissing(missingLogger, channelId, null);
        }

        // Synchronously reload the local cache so the disabled channel leaves
        // the candidate pool without waiting for the watcher debounce.
        await ReloadLocalCacheAsync(LogReloadAfterAutoDisableFailed, channelId)
            .ConfigureAwait(false);

        // Also notify other instances via the watcher for cross-instance
        // cache invalidation.
        _cacheRefresher?.RequestRefresh();
    }

    private async Task ReloadLocalCacheAsync(
        Action<ILogger, long, Exception?> logFailure,
        long channelId)
    {
        if (_cacheRefresher is null)
        {
            return;
        }

        try
        {
            await _cacheRefresher.ReloadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger is { } logger)
            {
                logFailure(logger, channelId, ex);
            }
        }
    }

    /// <summary>
    /// Port of matchesAPIKeyRule (channel_auto_disable.go:323-350): an empty
    /// condition matches any upstream error; patterns are compiled
    /// case-insensitively and fall back to a literal keyword when invalid.
    /// </summary>
    internal static bool Matches(AutoDisableRule rule, ChannelPerformanceRecord performance)
    {
        if (rule.StatusCodes is { Count: > 0 }
            && !rule.StatusCodes.Contains(performance.ResponseStatusCode))
        {
            return false;
        }

        if (rule.KeywordPatterns is null || rule.KeywordPatterns.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(performance.ErrorMessage))
        {
            return false;
        }

        var lowerMessage = performance.ErrorMessage.ToLowerInvariant();
        foreach (var pattern in rule.KeywordPatterns)
        {
            var regex = CompiledRuleRegex(pattern);
            if (regex is not null)
            {
                try
                {
                    if (regex.IsMatch(performance.ErrorMessage))
                    {
                        return true;
                    }
                }
                catch (RegexMatchTimeoutException)
                {
                    // The reference's RE2 cannot backtrack; a pathological
                    // pattern simply does not match here (C258).
                }

                continue;
            }

            if (lowerMessage.Contains(pattern.ToLowerInvariant(), StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Regex? CompiledRuleRegex(string pattern)
    {
        var cacheKey = "(?i)" + pattern;
        return CompiledRuleRegexes.GetOrAdd(cacheKey, static key =>
        {
            try
            {
                return new Regex(
                    key,
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException)
            {
                return null;
            }
        });
    }

    /// <summary>
    /// Port of autoDisableCounterIdentity (channel_auto_disable.go:296-302).
    /// </summary>
    internal static string CounterIdentity(ChannelPerformanceRecord performance)
        => string.IsNullOrEmpty(performance.ApiKey)
            ? "channel:" + performance.ChannelId.ToString(CultureInfo.InvariantCulture)
            : performance.ApiKey;

    /// <summary>
    /// Port of apiKeyRuleCounterKey (channel_auto_disable.go:304-321). The lists
    /// are formatted like Go's <c>%v</c>, so a nil and an empty list both print
    /// <c>[]</c> and an absent duration prints <c>0</c>.
    /// </summary>
    internal static string RuleCounterKey(
        string identity,
        string scope,
        int ruleIndex,
        AutoDisableRule rule)
        => string.Concat(
            identity,
            ":",
            scope,
            ":rule:",
            ruleIndex.ToString(CultureInfo.InvariantCulture),
            ":",
            FormatIntList(rule.StatusCodes),
            ":",
            FormatStringList(rule.KeywordPatterns),
            ":",
            rule.Times.ToString(CultureInfo.InvariantCulture),
            ":",
            rule.Action,
            ":",
            (rule.DisableDurationMinutes ?? 0).ToString(CultureInfo.InvariantCulture));

    private static string FormatIntList(IReadOnlyList<int>? values)
        => values is null or { Count: 0 }
            ? "[]"
            : "[" + string.Join(
                " ",
                values.Select(static value => value.ToString(CultureInfo.InvariantCulture))) + "]";

    private static string FormatStringList(IReadOnlyList<string>? values)
        => values is null or { Count: 0 }
            ? "[]"
            : "[" + string.Join(" ", values) + "]";

    private static string DisabledReason(int count)
        => "Disabled by auto-disable rule after "
            + count.ToString(CultureInfo.InvariantCulture)
            + " consecutive errors";

    /// <summary>Port of deriveErrorMessage (channel_metrics.go:660-666).</summary>
    internal static string DeriveErrorMessage(int errorCode)
    {
        var text = GoHttpStatus.Text(errorCode);
        return text.Length > 0 ? text : "Error " + errorCode.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Port of nextAPIKeyRuleCronOccurrence (channel_auto_disable.go:372-395):
    /// the first occurrence strictly after the failure, in the rule's timezone.
    /// The port's cron parser is the five-field <see cref="GcCronSchedule"/>
    /// (difference C258).
    /// </summary>
    internal static DateTimeOffset NextRuleCronOccurrence(AutoDisableRule rule, DateTimeOffset now)
    {
        var zone = TimeZoneInfo.Utc;
        if (!string.IsNullOrEmpty(rule.DisableUntilTimezone))
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(rule.DisableUntilTimezone);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "invalid timezone " + GoCast.GoQuote(rule.DisableUntilTimezone) + ": " + ex.Message,
                    ex);
            }
        }

        GcCronSchedule schedule;
        try
        {
            schedule = new GcCronSchedule(rule.DisableUntilCron);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "invalid cron expression " + GoCast.GoQuote(rule.DisableUntilCron) + ": " + ex.Message,
                ex);
        }

        // GcCronSchedule matches UTC wall-clock fields; feed it the wall clock
        // of the rule's zone and restore the zone offset afterwards.
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var wallClockNow = new DateTimeOffset(localNow.DateTime, TimeSpan.Zero);

        DateTimeOffset nextWall;
        try
        {
            nextWall = schedule.NextOccurrence(wallClockNow);
        }
        catch (InvalidOperationException)
        {
            throw new InvalidOperationException(
                "cron expression " + GoCast.GoQuote(rule.DisableUntilCron) + " never fires again");
        }

        var nextLocal = DateTime.SpecifyKind(nextWall.DateTime, DateTimeKind.Unspecified);
        return new DateTimeOffset(nextLocal, zone.GetUtcOffset(nextLocal));
    }

    private static string FormatRfc3339(DateTimeOffset value)
        => value.Offset == TimeSpan.Zero
            ? value.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    private async Task<ChannelDescriptor?> FindEnabledChannelAsync(
        long channelId,
        CancellationToken cancellationToken)
    {
        try
        {
            var channels = await _enabledChannels.ListEnabledAsync(cancellationToken)
                .ConfigureAwait(false);
            return channels.FirstOrDefault(channel => channel.Id == channelId);
        }
        catch (Exception ex)
        {
            // GetEnabledChannel reads an in-memory cache in the reference and
            // cannot fail; the port's provider may hit storage, so a read
            // failure degrades to "not found" instead of failing the request.
            if (_logger is { } logger)
            {
                LogEnabledChannelsReadFailed(logger, ex);
            }

            return null;
        }
    }

    /// <summary>
    /// Port of RetryPolicyOrDefault (system.go:1065-1083): a read failure logs a
    /// warning and falls back to the default policy.
    /// </summary>
    private async Task<RetryPolicy> GetRetryPolicyOrDefaultAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _settings.GetRetryPolicyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (_logger is { } logger)
            {
                LogRetryPolicyReadFailed(logger, ex);
            }

            return RetryPolicy.Default;
        }
    }

    /// <summary>
    /// Decodes the stored policies like the reference's ent unmarshal into
    /// objects.ChannelPolicies: an absent/blank document is the zero policy and
    /// an invalid document degrades to it rather than failing the evaluation.
    /// </summary>
    private static (string? Mode, IReadOnlyList<AutoDisableRule> Rules) ParsePolicies(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return (null, []);
        }

        ChannelPoliciesDraft policies;
        try
        {
            policies = LlmJson.Deserialize<ChannelPoliciesDraft>(raw) ?? new ChannelPoliciesDraft();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            return (null, []);
        }

        if (policies.ApiKeyAutoDisableRules is not { Count: > 0 } drafts)
        {
            return (policies.ApiKeyAutoDisableMode, []);
        }

        var rules = new List<AutoDisableRule>(drafts.Count);
        foreach (var draft in drafts)
        {
            // Go's value slice: a JSON null element leaves the zero rule.
            rules.Add(draft is null
                ? new AutoDisableRule(null, null, 0, string.Empty, null, string.Empty, string.Empty)
                : ChannelValidation.ToAutoDisableRule(draft));
        }

        return (policies.ApiKeyAutoDisableMode, rules);
    }

    /// <summary>Port of ChannelPolicies.EffectiveAutoDisableMode (channel.go:558-575).</summary>
    private static string EffectiveMode(string? mode, int ruleCount)
    {
        switch (mode)
        {
            case "off":
                return "off";
            case "custom":
                return ruleCount == 0 ? "inherit" : "custom";
            case "inherit":
                return "inherit";
            default:
                return ruleCount > 0 ? "custom" : "inherit";
        }
    }
}

/// <summary>
/// The seam the metrics middleware uses to hand a finished attempt to the
/// auto-disable engine without delaying the response. The reference enqueues
/// onto <c>perfCh</c> and lets one worker goroutine process records in order.
/// </summary>
public interface IChannelAutoDisableEvaluationQueue
{
    void Enqueue(ChannelPerformanceRecord performance);
}

/// <summary>
/// Production queue: one unbounded channel and one sequential consumer with a
/// fresh DI scope per record, mirroring <c>ChannelService.startPerformanceProcess</c>.
/// </summary>
public sealed class ChannelAutoDisableEvaluationQueue(
    IServiceScopeFactory scopeFactory,
    ILogger<ChannelAutoDisableEvaluationQueue>? logger = null)
    : BackgroundService, IChannelAutoDisableEvaluationQueue
{
    private static readonly Action<ILogger, Exception?> LogPanicInRecordPerformance =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "RecordPerformancePanicked"),
            "panic in record performance");

    private readonly Channel<ChannelPerformanceRecord> _queue =
        Channel.CreateUnbounded<ChannelPerformanceRecord>(
            new UnboundedChannelOptions { SingleReader = true });

    public void Enqueue(ChannelPerformanceRecord performance)
    {
        ArgumentNullException.ThrowIfNull(performance);

        // The reference's unbounded send blocks until the worker takes it; the
        // port's channel never drops a record while the process is alive.
        _queue.Writer.TryWrite(performance);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var performance in _queue.Reader
                               .ReadAllAsync(stoppingToken)
                               .ConfigureAwait(false))
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var service = scope.ServiceProvider
                        .GetRequiredService<ChannelAutoDisableService>();
                    await service.EvaluateAsync(performance, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // channel_metrics.go:520-524: the worker recovers from a
                    // panic and keeps consuming.
                    if (logger is { } workerLogger)
                    {
                        LogPanicInRecordPerformance(workerLogger, ex);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }
}
