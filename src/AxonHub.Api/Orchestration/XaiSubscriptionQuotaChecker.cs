using System.Globalization;
using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Llm.Transformer.Xai.Subscription;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the xAI subscription (Grok CLI proxy) quota checker
/// (internal/server/biz/provider_quota/xai_subscription_checker.go). The
/// checker merges the weekly credit-usage payload with the monthly plan
/// payload into the provider quota shape.
/// </summary>
public sealed class XaiSubscriptionQuotaChecker : IQuotaChecker
{
    private const string ProviderType = "xai_subscription";

    /// <summary>
    /// CLI user agent used by fetchBilling
    /// (xai_subscription_checker.go:81): <c>grok-pager/&lt;v&gt; grok-shell/&lt;v&gt; (macos; aarch64)</c>.
    /// </summary>
    internal static readonly string BillingUserAgent =
        "grok-pager/" + SubscriptionConstants.CliClientVersion
        + " grok-shell/" + SubscriptionConstants.CliClientVersion
        + " (macos; aarch64)";

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;

    public XaiSubscriptionQuotaChecker(
        IExecutor executor,
        IProxyExecutorFactory proxyExecutors,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(proxyExecutors);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _executor = executor;
        _proxyExecutors = proxyExecutors;
        _timeProvider = timeProvider;
    }

    /// <summary>Port of (*XAISubscriptionQuotaChecker).CheckQuota (xai_subscription_checker.go:24-58).</summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        OAuthCredentials credentials;
        try
        {
            credentials = OAuthCredentialsJson.ResolveChannelCredentials(channel.Credentials);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "parse xAI subscription credentials: ");
        }

        var accessToken = GoStrings.TrimSpace(credentials.AccessToken);
        if (accessToken.Length == 0)
        {
            throw new InvalidOperationException(
                "xAI subscription channel has no OAuth access token");
        }

        // The reference resolves the client once, before both fetches, so a
        // proxied channel creates exactly one proxy executor per check.
        var executor = ResolveExecutor(channel);

        byte[] weeklyBody;
        try
        {
            weeklyBody = FetchBilling(
                executor,
                SubscriptionConstants.BillingWeeklyUrl,
                accessToken,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "fetch xAI weekly billing: ");
        }

        byte[] monthlyBody;
        try
        {
            monthlyBody = FetchBilling(
                executor,
                SubscriptionConstants.BillingMonthlyUrl,
                accessToken,
                cancellationToken);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "fetch xAI monthly billing: ");
        }

        BillingSummary summary;
        try
        {
            summary = SubscriptionBilling.ParseBillingResponses(weeklyBody, monthlyBody);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "parse xAI billing responses: ");
        }

        var plan = SubscriptionPlan.PlanFromAccessToken(accessToken);
        if (plan.Length != 0)
        {
            summary.Plan = plan;
        }

        return Task.FromResult(BuildBillingQuotaData(summary, _timeProvider.GetUtcNow()));
    }

    /// <summary>Port of (*XAISubscriptionQuotaChecker).SupportsChannel (xai_subscription_checker.go:57-59).</summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Type == ChannelOutboundFactory.XaiSubscriptionChannelType;
    }

    /// <summary>Port of fetchBilling (xai_subscription_checker.go:61-85).</summary>
    private static byte[] FetchBilling(
        IExecutor executor,
        string endpoint,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(
            headers,
            SubscriptionConstants.CliTokenAuthHeader,
            SubscriptionConstants.CliTokenAuth);
        HttpClientUtils.HeaderSet(
            headers,
            SubscriptionConstants.CliClientVersionHeader,
            SubscriptionConstants.CliClientVersion);
        HttpClientUtils.HeaderSet(headers, "User-Agent", BillingUserAgent);

        var request = new HttpRequestData
        {
            Method = "GET",
            Url = endpoint,
            Headers = headers,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = accessToken,
            },
        };

        var response = executor.Do(cancellationToken, request);

        // The reference rejects everything outside [200, 300); the executor
        // already throws for 4xx/5xx, so this fires for 1xx/3xx.
        if (response.StatusCode < 200 || response.StatusCode >= 300)
        {
            throw new InvalidOperationException(
                "HTTP " + response.StatusCode.ToString(CultureInfo.InvariantCulture));
        }

        return response.Body ?? [];
    }

    /// <summary>Port of xaiBillingQuotaData (xai_subscription_checker.go:87-133).</summary>
    internal static QuotaData BuildBillingQuotaData(BillingSummary summary, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var limits = new List<QuotaLimitStatus>(2);
        var usagePercent = -1.0;
        DateTimeOffset? weeklyResetAt = null;
        DateTimeOffset? monthlyResetAt = null;

        if (summary.Weekly is { } weekly)
        {
            weeklyResetAt = ParseXaiReset(weekly.ResetAt);
            usagePercent = weekly.UsagePercent;
            limits.Add(ProviderQuotaData.WithWindow(
                ProviderQuotaData.NewTokenLimitStatus(
                    BillingWindowStatus(weekly.UsagePercent),
                    weekly.UsagePercent / 100,
                    weeklyResetAt),
                QuotaWindows.Weekly,
                TimeSpan.FromDays(7)));
        }

        if (summary.Monthly is { } monthly)
        {
            monthlyResetAt = ParseXaiReset(monthly.ResetAt);
            var monthlyStatus = BillingWindowStatus(monthly.UsagePercent);

            // Go's builtin max propagates NaN, as does Math.Max.
            usagePercent = Math.Max(usagePercent, monthly.UsagePercent);

            var monthlyLimit = ProviderQuotaData.NewTokenLimitStatus(
                monthlyStatus,
                monthly.UsagePercent / 100,
                monthlyResetAt);
            monthlyLimit.Window = QuotaWindows.Monthly;
            monthlyLimit.PeriodStart = ProviderQuotaData.PeriodStartFromMonthlyReset(monthlyResetAt);
            limits.Add(monthlyLimit);
        }

        var status = ProviderQuotaStatuses.Unknown;
        if (usagePercent >= 0)
        {
            status = BillingWindowStatus(usagePercent);
        }

        var nextResetAt = EarliestReset(weeklyResetAt, monthlyResetAt);

        // rawBilling is a non-nil empty map when both windows are absent, so
        // "billing" is always present in rawData.
        var rawBilling = new Dictionary<string, object?>(2, StringComparer.Ordinal);
        if (summary.Weekly is not null)
        {
            rawBilling["weekly"] = summary.Weekly;
        }

        if (summary.Monthly is not null)
        {
            rawBilling["monthly"] = summary.Monthly;
        }

        return ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = status,
                ProviderType = ProviderType,
                RawData = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["plan_type"] = summary.Plan ?? string.Empty,
                    ["billing"] = rawBilling,
                },
                NextResetAt = nextResetAt,
                Ready = ProviderQuotaStatuses.IsReadyStatus(status),
                Limits = limits,
            },
            now);
    }

    /// <summary>Port of xaiBillingWindowStatus (xai_subscription_checker.go:135-143).</summary>
    internal static string BillingWindowStatus(double usagePercent)
    {
        if (usagePercent >= 100)
        {
            return ProviderQuotaStatuses.Exhausted;
        }

        if (usagePercent >= ProviderQuotaData.WarningThresholdRatio * 100)
        {
            return ProviderQuotaStatuses.Warning;
        }

        return ProviderQuotaStatuses.Available;
    }

    /// <summary>Port of parseXAIReset (xai_subscription_checker.go:145-151).</summary>
    internal static DateTimeOffset? ParseXaiReset(string? raw)
        => GoTimeParsing.TryParseRfc3339(GoStrings.TrimSpace(raw ?? string.Empty));

    /// <summary>
    /// Port of earliestReset (xai_subscription_checker.go:153-161): nil-ignoring
    /// minimum with no future filter.
    /// </summary>
    internal static DateTimeOffset? EarliestReset(params DateTimeOffset?[] resets)
    {
        DateTimeOffset? earliest = null;
        foreach (var reset in resets)
        {
            if (reset is { } value && (earliest is null || value < earliest.Value))
            {
                earliest = value;
            }
        }

        return earliest;
    }

    private IExecutor ResolveExecutor(ChannelRecord channel)
        => ParseSettingsLenient(channel.Settings)?.Proxy is { } proxy
            ? _proxyExecutors.Create(ToProxyConfig(proxy))
            : _executor;

    private static ChannelSettingsDraft? ParseSettingsLenient(string? raw)
    {
        try
        {
            return ChannelWriteSerialization.ParseSettings(raw);
        }
        catch (JsonException)
        {
            // The reference decodes settings with Ent before reaching the
            // checker; malformed JSON is treated as "no settings" (C013).
            return null;
        }
    }

    private static ProxyConfig ToProxyConfig(ProxyConfigDraft proxy) => new()
    {
        Type = proxy.Type,
        Url = proxy.Url,
        Username = proxy.Username,
        Password = proxy.Password,
        DisableConnectionReuse = proxy.DisableConnectionReuse ?? false,
    };
}
