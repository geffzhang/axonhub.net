using System.Globalization;
using System.Text;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the Cline quota checker
/// (internal/server/biz/provider_quota/cline_checker.go). The checker reads the
/// Cline user identity and plans with the channel API key, then builds two
/// quota views from different pools:
/// <list type="bullet">
/// <item>ClinePass models use the official usage-limits windows plus a
/// supplementary cost ledger built from /usages.</item>
/// <item>Direct (usage-billing) models only report an informational credit
/// balance.</item>
/// </list>
/// The ledger is best-effort: Cline rate limits rapid pagination, so a ledger
/// failure downgrades the cost breakdown instead of failing the check.
/// </summary>
public sealed class ClineQuotaChecker : IQuotaChecker
{
    /// <summary>Port of clineProviderType (cline_checker.go:21).</summary>
    internal const string ProviderType = "cline";

    /// <summary>Port of clinePassModelPrefix (cline_checker.go:22).</summary>
    internal const string PassModelPrefix = "cline-pass/";

    /// <summary>Port of clineQuotaDefaultBaseURL (cline_checker.go:23).</summary>
    internal const string DefaultBaseUrl = "https://api.cline.bot";

    /// <summary>Port of clineUsagePageLimit (cline_checker.go:27).</summary>
    internal const int UsagePageLimit = 200;

    /// <summary>Port of clineMaxUsagePages (cline_checker.go:28).</summary>
    internal const int MaxUsagePages = 100;

    /// <summary>Port of clineCostUnitsPerUSD (cline_checker.go:29).</summary>
    internal const long CostUnitsPerUsd = 100_000_000;

    /// <summary>Port of clineMaxResponseBodySize (cline_checker.go:30).</summary>
    internal const int MaxResponseBodySize = 1 << 20;

    /// <summary>Port of clineUsageLimitsPath (cline_checker.go:31).</summary>
    internal const string UsageLimitsPath = "/api/v1/users/me/plan/usage-limits";

    /// <summary>Port of the clineUsageLimitType* constants (cline_checker.go:33-35).</summary>
    internal const string UsageLimitTypeFiveHour = "five_hour";
    internal const string UsageLimitTypeWeekly = "weekly";
    internal const string UsageLimitTypeMonthly = "monthly";

    /// <summary>Port of the clineUsageLimitsFetchStatus* constants (cline_checker.go:37-41).</summary>
    internal const string FetchStatusComplete = "complete";
    internal const string FetchStatusPartial = "partial";
    internal const string FetchStatusUnusable = "unusable";
    internal const string FetchStatusUnavailable = "unavailable";
    internal const string FetchStatusPassUnavailable = "cline_pass_unavailable";

    /// <summary>Port of the clineWindowSource* constants (cline_checker.go:43-46).</summary>
    internal const string WindowSourceOfficialUsageLimits = "official_usage_limits";
    internal const string WindowSourceOfficialWindowLedger = "cline_pass_ledger_official_window";
    internal const string WindowSourceOfficialNoActiveWindow = "official_no_active_window";
    internal const string WindowSourceUnavailable = "unavailable";

    /// <summary>Port of the clineResetState* constants (cline_checker.go:48-51).</summary>
    internal const string ResetStateActive = "active";
    internal const string ResetStateInactive = "inactive";
    internal const string ResetStateUnavailable = "unavailable";
    internal const string ResetStateInvalid = "invalid";

    /// <summary>Port of the clineWindowState* constants (cline_checker.go:53-56).</summary>
    internal const string WindowStateActive = "active";
    internal const string WindowStateInactive = "inactive";
    internal const string WindowStateUnavailable = "unavailable";
    internal const string WindowStateInvalid = "invalid";

    /// <summary>Port of clineWindowBoundaryTolerance (cline_checker.go:58).</summary>
    internal static readonly TimeSpan WindowBoundaryTolerance = TimeSpan.FromSeconds(2);

    /// <summary>Port of the clineLedgerError* constants (cline_checker.go:60-61).</summary>
    internal const string LedgerErrorRateLimited = "rate_limited";
    internal const string LedgerErrorTransport = "transport";

    /// <summary>Port of the clineModelScope* constants (cline_checker.go:230-235).</summary>
    internal const string ModelScopePassOnly = "cline_pass_only";
    internal const string ModelScopeMixed = "mixed";
    internal const string ModelScopeDirect = "direct_only";
    internal const string ModelScopeUnknown = "unknown";

    /// <summary>Port of the per-window map keys (clineUsageLimitWindowKey).</summary>
    internal const string WindowKeyFiveHours = "last5h";
    internal const string WindowKeySevenDays = "last7d";
    internal const string WindowKeyThirtyDays = "last30d";

    private const string StatusBasisPassWindows = "cline_pass_windows";
    private const string StatusBasisMixedPassExhausted = "mixed_pool_pass_exhausted";
    private const string StatusBasisPassUnavailable = "cline_pass_unavailable";
    private const string StatusBasisPassUnavailableMixed = "cline_pass_unavailable_mixed_pool";
    private const string StatusBasisDirectBalance = "direct_credit_balance_informational";
    private const string PoolClinePass = "cline_pass";
    private const string PoolDirectCredit = "direct_credit";
    private const string PoolNoteClinePass =
        "ClinePass is a separate provider; this quota applies to cline-pass/* models only.";
    private const string PoolNoteDirectCredit =
        "Cline (usage-billing) credits balance is informational until exact pay-as-you-go exhaustion semantics are verified.";
    private const string BalanceUnitNote =
        "Cline API response field name is balance; AxonHub displays it using Cline's Cline credits terminology.";

    private const string MissingApiKeyError = "channel has no API key";
    private const string MissingIdentityError = "Cline user identity response missing id";
    private const string MissingClinePassError =
        "Cline plans response does not include an active ClinePass threshold";
    private const string CreateRequestError = "failed to create Cline quota request";
    private const string TransportError = "Cline quota request failed during transport";
    private const string ReadResponseError = "failed to read Cline quota response";
    private const string ParseResponseError = "failed to parse Cline quota response";
    private const string UsageLimitsErrorPrefix = "failed to read Cline usage limits: ";
    private const string UsagesErrorPrefix = "failed to read Cline usages: ";

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;

    public ClineQuotaChecker(
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

    /// <summary>Port of (*ClineQuotaChecker).SupportsChannel (cline_checker.go:244-246).</summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Type == ChannelOutboundFactory.ClineChannelType;
    }

    /// <summary>Port of (*ClineQuotaChecker).CheckQuota (cline_checker.go:248-320).</summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var apiKey = ClineApiKey(channel);
        if (apiKey.Length == 0)
        {
            throw new InvalidOperationException(MissingApiKeyError);
        }

        // provider_quota.go:46-48: a channel proxy replaces the shared client
        // for every request of one check.
        var executor = ResolveExecutor(channel);

        ClineMeData me;
        try
        {
            var meBody = GetJson(
                executor,
                channel.BaseUrl,
                "/api/v1/users/me",
                null,
                apiKey,
                cancellationToken);
            me = ParseBody(() => ParseMeEnvelope(meBody));
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "failed to read Cline user identity: ");
        }

        if (GoStrings.TrimSpace(me.Id).Length == 0)
        {
            throw new InvalidOperationException(MissingIdentityError);
        }

        List<ClinePlan>? plans;
        try
        {
            var plansBody = GetJson(
                executor,
                channel.BaseUrl,
                "/api/v1/plans",
                null,
                apiKey,
                cancellationToken);
            plans = ParseBody(() => ParsePlansEnvelope(plansBody));
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "failed to read Cline plans: ");
        }

        var (threshold, planSummaries, hasClinePass) = SelectClinePassThreshold(plans);

        ClineBalanceData balance;
        var balancePath = "/api/v1/users/" + GoUrl.PathEscape(me.Id) + "/balance";
        try
        {
            var balanceBody = GetJson(
                executor,
                channel.BaseUrl,
                balancePath,
                null,
                apiKey,
                cancellationToken);
            balance = ParseBody(() => ParseBalanceEnvelope(balanceBody));
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "failed to read Cline balance: ");
        }

        var scope = ClassifyClineModelScope(channel);
        if (scope == ModelScopeDirect)
        {
            return Task.FromResult(BuildClineDirectOnlyQuota(balance.Balance, planSummaries));
        }

        if (!hasClinePass)
        {
            throw new InvalidOperationException(MissingClinePassError);
        }

        var usageLimits = FetchUsageLimits(executor, channel.BaseUrl, apiKey, cancellationToken);
        if (usageLimits.Error is { } usageLimitsError)
        {
            throw usageLimitsError;
        }

        if (usageLimits.Meta.Status == FetchStatusPassUnavailable)
        {
            return Task.FromResult(BuildClinePassUnavailableQuota(
                scope,
                planSummaries,
                balance.Balance,
                usageLimits.Meta));
        }

        var usageItems = FetchUsageItems(
            executor,
            channel.BaseUrl,
            me.Id,
            apiKey,
            cancellationToken);

        var items = usageItems.Items;
        var fetchMeta = usageItems.Meta;
        if (usageItems.Error is { } ledgerError)
        {
            // The /usages ledger only supplies the supplementary per-window
            // cost breakdown; the official usage-limits response fetched above
            // already carries the authoritative window percentages and reset
            // times. Cline rate limits rapid /usages pagination (HTTP 429), so
            // a ledger failure means "cost unavailable", not "quota unknown".
            fetchMeta.LedgerUnavailable = true;
            fetchMeta.LedgerErrorCode = ClineLedgerErrorCode(ledgerError);
            items = null;
        }

        return Task.FromResult(BuildClineQuotaData(
            Now(),
            scope,
            threshold,
            planSummaries,
            balance.Balance,
            items,
            fetchMeta,
            usageLimits.Limits,
            usageLimits.Meta));
    }

    /// <summary>
    /// Port of the identity/credential lookup at the top of CheckQuota
    /// (cline_checker.go:249-252, 379-392): the legacy key wins, then the first
    /// non-blank entry of <c>apiKeys</c>.
    /// </summary>
    internal static string ClineApiKey(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var apiKey = GoStrings.TrimSpace(ChannelCredentials.GetLegacyApiKey(channel.Credentials));
        if (apiKey.Length != 0)
        {
            return apiKey;
        }

        foreach (var candidate in ChannelCredentials.GetApiKeys(channel.Credentials))
        {
            var trimmed = GoStrings.TrimSpace(candidate);
            if (trimmed.Length != 0)
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    /// <summary>Port of buildClineQuotaURL (cline_checker.go:394-423).</summary>
    internal static string BuildClineQuotaUrl(
        string? baseUrl,
        string path,
        Dictionary<string, List<string>>? query)
    {
        var trimmed = GoStrings.TrimSpace(baseUrl ?? string.Empty);
        if (trimmed.Length == 0)
        {
            trimmed = DefaultBaseUrl;
        }

        // url.Parse drops the base path and query; only scheme and host feed
        // the quota URL. A base without a host falls back to the default.
        if (!GoUrl.TryParse(trimmed, out var scheme, out var host, out _) || host.Length == 0)
        {
            GoUrl.TryParse(DefaultBaseUrl, out scheme, out host, out _);
        }

        if (scheme.Length == 0 || scheme == "http")
        {
            scheme = "https";
        }

        if (host.Length == 0)
        {
            GoUrl.TryParse(DefaultBaseUrl, out scheme, out host, out _);
        }

        // url.URL.String escapes the already PathEscape'd path again, so a
        // percent sign in a user id travels as %25 (see difference C208).
        var result = scheme + "://" + host + EscapeUrlPath(path);
        if (query is { Count: > 0 })
        {
            result += "?" + HttpClientUtils.EncodeQuery(query);
        }

        return result;
    }

    /// <summary>Port of classifyClineModelScope (cline_checker.go:425-457).</summary>
    internal static string ClassifyClineModelScope(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var models = new List<string>();
        models.AddRange(ParseModelList(channel.SupportedModels));
        models.AddRange(ParseModelList(channel.ManualModels));
        if (models.Count == 0)
        {
            return ModelScopeUnknown;
        }

        var hasPass = false;
        var hasDirect = false;
        foreach (var raw in models)
        {
            var model = GoStrings.TrimSpace(raw);
            if (model.Length == 0)
            {
                continue;
            }

            if (model.StartsWith(PassModelPrefix, StringComparison.Ordinal))
            {
                hasPass = true;
            }
            else
            {
                hasDirect = true;
            }
        }

        return (hasPass, hasDirect) switch
        {
            (true, true) => ModelScopeMixed,
            (true, false) => ModelScopePassOnly,
            (false, true) => ModelScopeDirect,
            _ => ModelScopeUnknown,
        };
    }

    /// <summary>Port of selectClinePassThreshold (cline_checker.go:459-482).</summary>
    internal static (
        ClineInferenceCapThreshold Threshold,
        List<Dictionary<string, object?>>? Summaries,
        bool Found) SelectClinePassThreshold(List<ClinePlan>? plans)
    {
        var selected = new ClineInferenceCapThreshold();
        List<Dictionary<string, object?>>? summaries = null;
        var found = false;

        foreach (var plan in plans ?? new List<ClinePlan>())
        {
            var pass = plan.Entitlements.ClinePass;
            if (pass is null
                || !pass.Enabled
                || pass.InferenceCapThreshold is null
                || !plan.IsActive)
            {
                continue;
            }

            summaries ??= [];
            summaries.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = plan.Type,
                ["interval"] = plan.Interval,
            });

            if (!found)
            {
                selected = pass.InferenceCapThreshold.Clone();
                found = true;
            }
        }

        return (selected, summaries, found);
    }

    /// <summary>Port of (*ClineQuotaChecker).fetchUsageLimits (cline_checker.go:484-503).</summary>
    internal ClineUsageLimitsFetchOutcome FetchUsageLimits(
        IExecutor executor,
        string? baseUrl,
        string apiKey,
        CancellationToken cancellationToken)
    {
        ClineUsageLimitsData response;
        try
        {
            var body = GetJson(
                executor,
                baseUrl,
                UsageLimitsPath,
                null,
                apiKey,
                cancellationToken);
            response = ParseBody(() => ParseUsageLimitsEnvelope(body));
        }
        catch (Exception ex)
        {
            if (GoErrors.As<ClineHttpStatusException>(ex) is { StatusCode: 404 })
            {
                return new ClineUsageLimitsFetchOutcome
                {
                    Limits = null,
                    Meta = new ClineUsageLimitsFetchMeta
                    {
                        Status = FetchStatusPassUnavailable,
                    },
                };
            }

            return new ClineUsageLimitsFetchOutcome
            {
                Limits = null,
                Meta = new ClineUsageLimitsFetchMeta
                {
                    Status = FetchStatusUnavailable,
                },
                Error = GoErrors.Wrap(ex, UsageLimitsErrorPrefix),
            };
        }

        var (limits, meta) = ParseClineUsageLimits(response.Limits);
        if (meta.Status == FetchStatusUnusable)
        {
            return new ClineUsageLimitsFetchOutcome
            {
                Limits = null,
                Meta = meta,
                Error = new InvalidOperationException(
                    UsageLimitsErrorPrefix + "response contains no usable window data"),
            };
        }

        return new ClineUsageLimitsFetchOutcome { Limits = limits, Meta = meta };
    }

    /// <summary>Port of (*ClineQuotaChecker).fetchUsageItems (cline_checker.go:505-550).</summary>
    internal ClineUsageItemsFetchOutcome FetchUsageItems(
        IExecutor executor,
        string? baseUrl,
        string userId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var items = new List<ClineUsageItem>();
        var meta = new ClineUsageFetchMeta();
        var cursor = string.Empty;
        var cutoff = Now().AddDays(-30);
        var path = "/api/v1/users/" + GoUrl.PathEscape(userId) + "/usages";

        for (var page = 0; page < MaxUsagePages; page++)
        {
            var query = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["limit"] = [UsagePageLimit.ToString(CultureInfo.InvariantCulture)],
            };
            if (cursor.Length != 0)
            {
                query["cursor"] = [cursor];
            }

            ClineUsagesData response;
            try
            {
                var body = GetJson(executor, baseUrl, path, query, apiKey, cancellationToken);
                response = ParseBody(() => ParseUsagesEnvelope(body));
            }
            catch (Exception ex)
            {
                // Go returns the partial meta together with a nil item slice.
                return new ClineUsageItemsFetchOutcome
                {
                    Items = null,
                    Meta = meta,
                    Error = GoErrors.Wrap(ex, UsagesErrorPrefix),
                };
            }

            var pageItems = response.Items ?? new List<ClineUsageItem>();
            meta.Pages++;
            meta.ItemsSeen += pageItems.Count;
            foreach (var item in pageItems)
            {
                switch (GoStrings.TrimSpace(item.AiModelTypeName))
                {
                    case "cline-pass":
                        meta.ClinePassItemsSeen++;
                        break;
                    case "":
                        meta.UnclassifiedItemsSeen++;
                        break;
                    default:
                        meta.DirectItemsSeen++;
                        break;
                }

                if (ParseClineTime(item.CreatedAt) is null)
                {
                    meta.InvalidTimestampItems++;
                }
            }

            items.AddRange(pageItems);

            var oldest = OldestClineUsageTime(pageItems);
            cursor = GoStrings.TrimSpace(response.NextToken);
            if (cursor.Length == 0
                || pageItems.Count == 0
                || (oldest is { } oldestTime && oldestTime < cutoff))
            {
                return new ClineUsageItemsFetchOutcome
                {
                    Items = items,
                    Meta = meta,
                };
            }
        }

        meta.Truncated = true;
        return new ClineUsageItemsFetchOutcome { Items = items, Meta = meta };
    }

    /// <summary>
    /// Port of clineLedgerErrorCode (cline_checker.go:552-565): a safe
    /// classification that never embeds the raw error text.
    /// </summary>
    internal static string ClineLedgerErrorCode(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var httpError = GoErrors.As<ClineHttpStatusException>(error);
        if (httpError is not null)
        {
            return httpError.StatusCode == 429
                ? LedgerErrorRateLimited
                : "http_" + httpError.StatusCode.ToString(CultureInfo.InvariantCulture);
        }

        return LedgerErrorTransport;
    }

    /// <summary>Port of oldestClineUsageTime (cline_checker.go:567-581).</summary>
    internal static DateTimeOffset? OldestClineUsageTime(IReadOnlyList<ClineUsageItem> items)
    {
        DateTimeOffset? oldest = null;
        foreach (var item in items)
        {
            if (ParseClineTime(item.CreatedAt) is not { } parsed)
            {
                continue;
            }

            if (oldest is null || parsed < oldest)
            {
                oldest = parsed;
            }
        }

        return oldest;
    }

    /// <summary>Port of parseClineTime (cline_checker.go:583-595).</summary>
    internal static DateTimeOffset? ParseClineTime(string? value)
    {
        var trimmed = GoStrings.TrimSpace(value ?? string.Empty);
        if (trimmed.Length == 0)
        {
            return null;
        }

        // time.Parse(time.RFC3339Nano): the port's RFC3339 subset accepts the
        // same grammar; values DateTimeOffset cannot represent are treated as
        // invalid (difference C208).
        return GoTimeParsing.TryParseRfc3339(trimmed);
    }

    /// <summary>Port of parseClineUsageLimits (cline_checker.go:597-673).</summary>
    internal static (
        Dictionary<string, ClineOfficialWindowLimit> Limits,
        ClineUsageLimitsFetchMeta Meta) ParseClineUsageLimits(List<ClineUsageLimit>? items)
    {
        var limits = new Dictionary<string, ClineOfficialWindowLimit>(StringComparer.Ordinal);
        var meta = new ClineUsageLimitsFetchMeta
        {
            Status = FetchStatusUnusable,
            EntriesSeen = items?.Count ?? 0,
        };

        foreach (var item in items ?? new List<ClineUsageLimit>())
        {
            var key = UsageLimitWindowKey(item.Type);
            if (key is null)
            {
                continue;
            }

            meta.RecognizedEntries++;

            // Go copies the struct out of the map, mutates the copy and only
            // writes it back when it became usable; cloning reproduces that.
            var limit = limits.TryGetValue(key, out var existing)
                ? existing.Clone()
                : new ClineOfficialWindowLimit();

            if (limit.UsageRatio is null && item.PercentUsed is { } percentUsed)
            {
                var ratio = percentUsed / 100;
                if (ratio < 0)
                {
                    ratio = 0;
                }

                if (ratio > 1)
                {
                    ratio = 1;
                }

                limit.UsageRatio = ratio;
                meta.UsableFields++;
            }

            if (limit.ResetState.Length == 0
                || limit.ResetState == ResetStateUnavailable
                || limit.ResetState == ResetStateInvalid)
            {
                var resetState = item.ResetFieldState;
                if (resetState.Length == 0)
                {
                    resetState = GoStrings.TrimSpace(item.ResetsAt).Length != 0
                        ? ResetStateActive
                        : ResetStateUnavailable;
                }

                if (resetState == ResetStateInactive
                    && (limit.UsageRatio is null || limit.UsageRatio > 0))
                {
                    resetState = ResetStateUnavailable;
                }

                switch (resetState)
                {
                    case ResetStateActive:
                        if (ParseClineTime(item.ResetsAt) is { } resetAt)
                        {
                            limit.NextResetAt = resetAt;
                            limit.ResetState = ResetStateActive;
                            meta.UsableFields++;
                        }
                        else
                        {
                            limit.ResetState = ResetStateInvalid;
                        }

                        break;
                    case ResetStateInactive:
                        limit.ResetState = ResetStateInactive;
                        meta.UsableFields++;
                        break;
                    case ResetStateInvalid:
                        limit.ResetState = ResetStateInvalid;
                        break;
                    default:
                        limit.ResetState = ResetStateUnavailable;
                        break;
                }
            }

            if (limit.UsageRatio is not null || limit.ResetState != ResetStateUnavailable)
            {
                limits[key] = limit;
            }
        }

        meta.UsableWindows = limits.Count;
        meta.Status = meta.UsableFields == 0
            ? FetchStatusUnusable
            : meta.UsableWindows == 3 && meta.UsableFields == 6
                ? FetchStatusComplete
                : FetchStatusPartial;

        return (limits, meta);
    }

    /// <summary>Port of clineUsageLimitWindowKey (cline_checker.go:675-686).</summary>
    internal static string? UsageLimitWindowKey(string? value) =>
        GoStrings.TrimSpace(value ?? string.Empty) switch
        {
            UsageLimitTypeFiveHour => WindowKeyFiveHours,
            UsageLimitTypeWeekly => WindowKeySevenDays,
            UsageLimitTypeMonthly => WindowKeyThirtyDays,
            _ => null,
        };

    /// <summary>Port of buildClineQuotaData (cline_checker.go:688-747).</summary>
    internal static QuotaData BuildClineQuotaData(
        DateTimeOffset now,
        string scope,
        ClineInferenceCapThreshold threshold,
        List<Dictionary<string, object?>>? plans,
        long? balance,
        List<ClineUsageItem>? items,
        ClineUsageFetchMeta usageFetchMeta,
        Dictionary<string, ClineOfficialWindowLimit>? officialLimits,
        ClineUsageLimitsFetchMeta officialMeta)
    {
        ArgumentNullException.ThrowIfNull(threshold);
        ArgumentNullException.ThrowIfNull(usageFetchMeta);
        ArgumentNullException.ThrowIfNull(officialMeta);

        var costUnavailable = usageFetchMeta.Truncated || usageFetchMeta.LedgerUnavailable;
        var windows = new List<ClineWindow>(3)
        {
            BuildClineWindow(
                now,
                WindowKeyFiveHours,
                TimeSpan.FromHours(5),
                threshold.Last5HoursUsageCostUsdPerUser,
                items,
                costUnavailable,
                OfficialLimit(officialLimits, WindowKeyFiveHours)),
            BuildClineWindow(
                now,
                WindowKeySevenDays,
                TimeSpan.FromDays(7),
                threshold.Last7DaysUsageCostUsdPerUser,
                items,
                costUnavailable,
                OfficialLimit(officialLimits, WindowKeySevenDays)),
            BuildClineWindow(
                now,
                WindowKeyThirtyDays,
                TimeSpan.FromDays(30),
                threshold.Last30DaysUsageCostUsdPerUser,
                items,
                costUnavailable,
                OfficialLimit(officialLimits, WindowKeyThirtyDays)),
        };

        var usageFetch = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["pages"] = usageFetchMeta.Pages,
            ["items_seen"] = usageFetchMeta.ItemsSeen,
            ["cline_pass_items_seen"] = usageFetchMeta.ClinePassItemsSeen,
            ["direct_items_seen"] = usageFetchMeta.DirectItemsSeen,
            ["unclassified_items_seen"] = usageFetchMeta.UnclassifiedItemsSeen,
            ["invalid_timestamp_items"] = usageFetchMeta.InvalidTimestampItems,
            ["truncated"] = usageFetchMeta.Truncated,
            ["ledger_unavailable"] = usageFetchMeta.LedgerUnavailable,
        };
        if (usageFetchMeta.LedgerErrorCode.Length != 0)
        {
            usageFetch["ledger_error_code"] = usageFetchMeta.LedgerErrorCode;
        }

        var passStatus = WorstClineStatus(windows);
        var status = passStatus;
        var statusBasis = StatusBasisPassWindows;
        if (scope != ModelScopePassOnly && passStatus == ProviderQuotaStatuses.Exhausted)
        {
            status = ProviderQuotaStatuses.Warning;
            statusBasis = StatusBasisMixedPassExhausted;
        }

        return ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = status,
                ProviderType = ProviderType,
                Ready = ProviderQuotaStatuses.IsReadyStatus(status),
                NextResetAt = EarliestClineWindowReset(windows),
                Limits = ClineLimitStatuses(windows, scope == ModelScopePassOnly),
                RawData = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["model_scope"] = scope,
                    ["status_basis"] = statusBasis,
                    ["pool"] = PoolClinePass,
                    ["pool_note"] = PoolNoteClinePass,
                    ["cost_scale"] = CostUnitsPerUsd,
                    ["balance"] = ClineBalanceRawData(balance),
                    ["plans"] = plans,
                    ["windows"] = ClineWindowsRawData(windows),
                    ["usage_fetch"] = usageFetch,
                    ["usage_limits_fetch"] = ClineUsageLimitsFetchRawData(officialMeta),
                },
            },
            now);
    }

    /// <summary>Port of buildClinePassUnavailableQuota (cline_checker.go:749-779).</summary>
    internal static QuotaData BuildClinePassUnavailableQuota(
        string scope,
        List<Dictionary<string, object?>>? plans,
        long? balance,
        ClineUsageLimitsFetchMeta usageLimitsMeta)
    {
        ArgumentNullException.ThrowIfNull(usageLimitsMeta);

        var status = ProviderQuotaStatuses.Warning;
        var statusBasis = StatusBasisPassUnavailable;
        switch (scope)
        {
            case ModelScopePassOnly:
                status = ProviderQuotaStatuses.Exhausted;
                break;
            case ModelScopeMixed:
                statusBasis = StatusBasisPassUnavailableMixed;
                break;
        }

        // The reference does not normalize this branch: it is an explicit
        // "pass state unknown" marker, not a window-derived quota.
        return new QuotaData
        {
            Status = status,
            ProviderType = ProviderType,
            Ready = ProviderQuotaStatuses.IsReadyStatus(status),
            RawData = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model_scope"] = scope,
                ["status_basis"] = statusBasis,
                ["pool"] = PoolClinePass,
                ["pool_note"] = PoolNoteClinePass,
                ["pass_state"] = "unavailable",
                ["balance"] = ClineBalanceRawData(balance),
                ["plans"] = plans,
                ["usage_limits_fetch"] = ClineUsageLimitsFetchRawData(usageLimitsMeta),
            },
        };
    }

    /// <summary>Port of buildClineDirectOnlyQuota (cline_checker.go:781-795).</summary>
    internal static QuotaData BuildClineDirectOnlyQuota(
        long? balance,
        List<Dictionary<string, object?>>? plans)
    {
        // Direct (usage-billing) models do not consume the ClinePass windows;
        // the credit balance is informational until pay-as-you-go exhaustion
        // semantics are verified.
        return new QuotaData
        {
            Status = ProviderQuotaStatuses.Available,
            ProviderType = ProviderType,
            Ready = true,
            RawData = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model_scope"] = ModelScopeDirect,
                ["status_basis"] = StatusBasisDirectBalance,
                ["pool"] = PoolDirectCredit,
                ["pool_note"] = PoolNoteDirectCredit,
                ["balance"] = ClineBalanceRawData(balance),
                ["plans"] = plans,
            },
        };
    }

    /// <summary>Port of buildClineWindow (cline_checker.go:797-919).</summary>
    internal static ClineWindow BuildClineWindow(
        DateTimeOffset now,
        string key,
        TimeSpan duration,
        long limit,
        IReadOnlyList<ClineUsageItem>? items,
        bool costUnavailable,
        ClineOfficialWindowLimit official)
    {
        ArgumentNullException.ThrowIfNull(official);

        var window = new ClineWindow
        {
            Key = key,
            Duration = duration,
            LimitUnits = limit,
            UsageSource = WindowSourceUnavailable,
            CostSource = WindowSourceUnavailable,
            ResetSource = WindowSourceUnavailable,
            State = WindowStateUnavailable,
        };

        if (official.UsageRatio is { } officialRatio)
        {
            window.UsageRatio = officialRatio;
            window.UsageSource = WindowSourceOfficialUsageLimits;
        }

        var resetState = official.ResetState;
        if (official.NextResetAt is not null)
        {
            resetState = ResetStateActive;
        }

        if (resetState == ResetStateInactive
            && window.UsageRatio is { } inactiveRatio
            && inactiveRatio > 0)
        {
            resetState = ResetStateUnavailable;
        }

        switch (resetState)
        {
            case ResetStateInactive:
                window.State = WindowStateInactive;
                window.CostAvailable = true;
                window.CostSource = WindowSourceOfficialNoActiveWindow;
                window.ResetSource = WindowSourceOfficialUsageLimits;
                if (window.UsageRatio is null)
                {
                    window.UsageRatio = 0;
                    window.UsageSource = WindowSourceOfficialUsageLimits;
                }

                window.CostUsageRatio = 0;
                return window;
            case ResetStateInvalid:
                window.State = WindowStateInvalid;
                return window;
            case ResetStateUnavailable:
            case "":
                return window;
        }

        if (official.NextResetAt is not { } resetAt)
        {
            window.State = WindowStateInvalid;
            return window;
        }

        if (!(resetAt > now) || resetAt > now + duration + WindowBoundaryTolerance)
        {
            window.State = WindowStateInvalid;
            return window;
        }

        window.State = WindowStateActive;
        window.Active = true;
        window.NextResetAt = resetAt;
        window.ResetSource = WindowSourceOfficialUsageLimits;
        if (costUnavailable)
        {
            return window;
        }

        var officialStart = resetAt - duration;
        var costStart = AlignClineWindowStart(
            officialStart,
            items ?? Array.Empty<ClineUsageItem>());
        window.WindowStartAt = officialStart;
        window.CostStartAt = costStart;

        foreach (var item in items ?? Array.Empty<ClineUsageItem>())
        {
            if (ParseClineTime(item.CreatedAt) is not { } createdAt)
            {
                ResetWindowCost(window);
                return window;
            }

            if (createdAt < costStart || !(createdAt < resetAt))
            {
                continue;
            }

            switch (GoStrings.TrimSpace(item.AiModelTypeName))
            {
                case "cline-pass":
                    window.ItemsCount++;
                    window.UsedUnits += item.CostUsd;
                    window.CreditsUsed += item.CreditsUsed;
                    break;
                case "":
                    ResetWindowCost(window);
                    return window;
            }
        }

        window.CostAvailable = true;
        window.CostSource = WindowSourceOfficialWindowLedger;
        if (window.LimitUnits > 0)
        {
            var costRatio = (double)window.UsedUnits / window.LimitUnits;
            window.CostUsageRatio = costRatio;
            if (window.UsageRatio is null)
            {
                window.UsageRatio = costRatio;
                window.UsageSource = WindowSourceOfficialWindowLedger;
            }
        }

        return window;
    }

    /// <summary>Port of alignClineWindowStart (cline_checker.go:921-945).</summary>
    internal static DateTimeOffset AlignClineWindowStart(
        DateTimeOffset expected,
        IReadOnlyList<ClineUsageItem> items)
    {
        var aligned = expected;
        // Go starts at tolerance+1ns so a candidate exactly at the tolerance
        // still wins; TimeSpan resolution is 100ns (difference C208).
        var bestDistance = WindowBoundaryTolerance + TimeSpan.FromTicks(1);

        foreach (var item in items)
        {
            if (GoStrings.TrimSpace(item.AiModelTypeName) != "cline-pass")
            {
                continue;
            }

            if (ParseClineTime(item.CreatedAt) is not { } createdAt)
            {
                continue;
            }

            var distance = createdAt - expected;
            if (distance < TimeSpan.Zero)
            {
                distance = -distance;
            }

            if (distance <= WindowBoundaryTolerance && distance < bestDistance)
            {
                aligned = createdAt;
                bestDistance = distance;
            }
        }

        return aligned;
    }

    /// <summary>Port of clineWindowStatus (cline_checker.go:947-960).</summary>
    internal static string ClineWindowStatus(ClineWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.UsageRatio is not { } ratio)
        {
            return ProviderQuotaStatuses.Unknown;
        }

        if (ratio >= 1.0)
        {
            return ProviderQuotaStatuses.Exhausted;
        }

        return ratio >= ProviderQuotaData.WarningThresholdRatio
            ? ProviderQuotaStatuses.Warning
            : ProviderQuotaStatuses.Available;
    }

    /// <summary>Port of worstClineStatus (cline_checker.go:962-968).</summary>
    internal static string WorstClineStatus(IReadOnlyList<ClineWindow> windows)
    {
        var status = ProviderQuotaStatuses.Unknown;
        foreach (var window in windows)
        {
            status = WorseQuotaStatus(status, ClineWindowStatus(window));
        }

        return status;
    }

    /// <summary>Port of worseQuotaStatus (cline_checker.go:970-976).</summary>
    internal static string WorseQuotaStatus(string a, string b)
        => WorseQuotaStatusRank(b) > WorseQuotaStatusRank(a) ? b : a;

    /// <summary>Port of clineWindowLabel (cline_checker.go:978-990).</summary>
    internal static string ClineWindowLabel(string key) => key switch
    {
        WindowKeyFiveHours => QuotaWindows.FiveHours,
        WindowKeySevenDays => QuotaWindows.SevenDays,
        WindowKeyThirtyDays => QuotaWindows.ThirtyDays,
        _ => key,
    };

    /// <summary>Port of clineLimitStatuses (cline_checker.go:992-1018).</summary>
    internal static List<QuotaLimitStatus> ClineLimitStatuses(
        IReadOnlyList<ClineWindow> windows,
        bool allowExhausted)
    {
        var limits = new List<QuotaLimitStatus>(windows.Count);
        foreach (var window in windows)
        {
            var usageRatio = window.UsageRatio ?? 0;

            var status = ClineWindowStatus(window);
            if (!allowExhausted && status == ProviderQuotaStatuses.Exhausted)
            {
                status = ProviderQuotaStatuses.Warning;
            }

            limits.Add(new QuotaLimitStatus
            {
                Type = QuotaLimitTypes.Token,
                Status = status,
                UsageRatio = usageRatio,
                Ready = ProviderQuotaStatuses.IsReadyStatus(status),
                NextResetAt = window.NextResetAt,
                Window = ClineWindowLabel(window.Key),
                PeriodStart = window.WindowStartAt,
            });
        }

        return limits;
    }

    /// <summary>Port of earliestClineWindowReset (cline_checker.go:1020-1034).</summary>
    internal static DateTimeOffset? EarliestClineWindowReset(IReadOnlyList<ClineWindow> windows)
    {
        DateTimeOffset? earliest = null;
        foreach (var window in windows)
        {
            if (window.NextResetAt is not { } reset)
            {
                continue;
            }

            if (earliest is null || reset < earliest)
            {
                earliest = reset;
            }
        }

        return earliest;
    }

    /// <summary>Port of clineWindowsRawData (cline_checker.go:1036-1075).</summary>
    internal static Dictionary<string, object?> ClineWindowsRawData(IReadOnlyList<ClineWindow> windows)
    {
        var result = new Dictionary<string, object?>(windows.Count, StringComparer.Ordinal);
        foreach (var window in windows)
        {
            var entry = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["window_state"] = window.State,
                ["active_window"] = window.Active,
                ["limit_cost_units"] = window.LimitUnits,
                ["usage_source"] = window.UsageSource,
                ["reset_source"] = window.ResetSource,
                ["cost_source"] = window.CostSource,
            };

            if (window.CostAvailable)
            {
                entry["items_count"] = window.ItemsCount;
                entry["used_cost_units"] = window.UsedUnits;
                entry["remaining_cost_units"] = window.LimitUnits - window.UsedUnits;
                entry["credits_used"] = window.CreditsUsed;
            }

            if (window.UsageRatio is { } usageRatio)
            {
                entry["usage_ratio"] = usageRatio;
                entry["usage_percent"] = usageRatio * 100;
            }

            if (window.CostUsageRatio is { } costUsageRatio)
            {
                entry["cost_usage_ratio"] = costUsageRatio;
                entry["cost_usage_percent"] = costUsageRatio * 100;
            }

            if (window.WindowStartAt is { } windowStartAt)
            {
                entry["window_start_at"] = GoTime.Rfc3339Nano(windowStartAt);
            }

            if (window.CostStartAt is { } costStartAt
                && window.WindowStartAt is { } windowStart
                && costStartAt != windowStart)
            {
                entry["cost_start_at"] = GoTime.Rfc3339Nano(costStartAt);
            }

            if (window.NextResetAt is { } nextResetAt)
            {
                entry["next_reset_at"] = GoTime.Rfc3339Nano(nextResetAt);
            }

            result[window.Key] = entry;
        }

        return result;
    }

    /// <summary>Port of clineUsageLimitsFetchRawData (cline_checker.go:1077-1085).</summary>
    internal static Dictionary<string, object?> ClineUsageLimitsFetchRawData(
        ClineUsageLimitsFetchMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["status"] = meta.Status,
            ["entries_seen"] = meta.EntriesSeen,
            ["recognized_entries"] = meta.RecognizedEntries,
            ["usable_windows"] = meta.UsableWindows,
            ["usable_fields"] = meta.UsableFields,
        };
    }

    /// <summary>Port of clineBalanceRawData (cline_checker.go:1087-1095).</summary>
    internal static Dictionary<string, object?> ClineBalanceRawData(long? balance)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["unit_note"] = BalanceUnitNote,
        };
        if (balance is { } rawBalance)
        {
            result["raw_balance"] = rawBalance;
        }

        return result;
    }

    /// <summary>
    /// Port of (*ClineQuotaChecker).getJSON (cline_checker.go:322-355). The
    /// body is capped like <c>io.LimitReader</c>; parse failures are raised by
    /// <see cref="ParseBody{T}"/> so both halves keep the reference's error
    /// taxonomy.
    /// </summary>
    internal byte[] GetJson(
        IExecutor executor,
        string? baseUrl,
        string path,
        Dictionary<string, List<string>>? query,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var url = BuildClineQuotaUrl(baseUrl, path, query);

        // http.NewRequestWithContext rejects a URL net/url cannot parse; the
        // port's executor parses later, so surface the same failure here.
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(CreateRequestError);
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");
        HttpClientUtils.HeaderSet(headers, "Authorization", "Bearer " + apiKey);
        HttpClientUtils.HeaderSet(headers, "User-Agent", "axonhub/1.0");

        var request = new HttpRequestData
        {
            Method = "GET",
            Url = url,
            Headers = headers,
        };

        HttpResponseData response;
        try
        {
            response = executor.Do(cancellationToken, request);
        }
        catch (HttpClientError httpError)
        {
            // The shared executor reports >=400 through its own error; the
            // Cline checker surfaces only the status line, never the body.
            throw new ClineHttpStatusException(httpError.StatusCode);
        }
        catch (IOException)
        {
            throw new InvalidOperationException(ReadResponseError);
        }
        catch (Exception)
        {
            // Includes the duplicate-transport branches Go folds together and
            // cancellation, which the reference reports as a send failure.
            throw new InvalidOperationException(TransportError);
        }

        // Go's client already errors for >=400, so this only sees 1xx/3xx.
        if (response.StatusCode is < 200 or >= 300)
        {
            throw new ClineHttpStatusException(response.StatusCode);
        }

        var body = response.Body ?? [];
        return body.Length > MaxResponseBodySize ? body[..MaxResponseBodySize] : body;
    }

    /// <summary>Port of the <c>json.Unmarshal</c> step of getJSON: one generic message.</summary>
    internal static T ParseBody<T>(Func<T> parse)
    {
        ArgumentNullException.ThrowIfNull(parse);

        try
        {
            return parse();
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(ParseResponseError);
        }
    }

    private DateTimeOffset Now() => _timeProvider.GetUtcNow();

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

    private static ClineOfficialWindowLimit OfficialLimit(
        Dictionary<string, ClineOfficialWindowLimit>? limits,
        string key)
        => limits is not null && limits.TryGetValue(key, out var limit)
            ? limit
            : new ClineOfficialWindowLimit();

    private static void ResetWindowCost(ClineWindow window)
    {
        window.CostAvailable = false;
        window.CostSource = WindowSourceUnavailable;
        window.UsedUnits = 0;
        window.CreditsUsed = 0;
        window.ItemsCount = 0;
        window.CostUsageRatio = null;
    }

    /// <summary>
    /// Port of the map rank in worseQuotaStatus (cline_checker.go:970-976):
    /// unknown ranks below available.
    /// </summary>
    private static int WorseQuotaStatusRank(string? status) => status switch
    {
        ProviderQuotaStatuses.Unknown => -1,
        ProviderQuotaStatuses.Available => 0,
        ProviderQuotaStatuses.Warning => 1,
        ProviderQuotaStatuses.Exhausted => 2,
        _ => 0,
    };

    /// <summary>
    /// Port of ParseModelList in the channel model-sync service: the reference
    /// reads an Ent-decoded []string; the port decodes the JSON column
    /// leniently and treats anything unusable as empty (C013).
    /// </summary>
    private static List<string> ParseModelList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return [];
        }

        try
        {
            var values = LlmJson.Deserialize<List<string?>>(raw);
            return values is null
                ? []
                : values.Select(static value => value ?? string.Empty).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Port of <c>url.URL.String</c>'s path escaping (Go's <c>encodePath</c>
    /// mode, net/url/url.go:shouldEscape): only '?' among the reserved set is
    /// escaped; '/' and ':' stay literal. Percent signs emitted by PathEscape
    /// are escaped again, exactly like the reference.
    /// </summary>
    private static string EscapeUrlPath(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var builder = new StringBuilder(bytes.Length);

        foreach (var b in bytes)
        {
            if (ShouldEscapePathByte(b))
            {
                builder.Append('%');
                builder.Append(UpperHex[b >> 4]);
                builder.Append(UpperHex[b & 0xF]);
            }
            else
            {
                builder.Append((char)b);
            }
        }

        return builder.ToString();
    }

    private const string UpperHex = "0123456789ABCDEF";

    private static bool ShouldEscapePathByte(byte b)
    {
        if (b is >= (byte)'a' and <= (byte)'z'
            or >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'0' and <= (byte)'9'
            or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
        {
            return false;
        }

        if (b is (byte)'$' or (byte)'&' or (byte)'+' or (byte)',' or (byte)'/'
            or (byte)':' or (byte)';' or (byte)'=' or (byte)'?' or (byte)'@')
        {
            return b == (byte)'?';
        }

        return true;
    }

    // ------------------------------------------------------------------
    // Go wire decoding
    // ------------------------------------------------------------------

    /// <summary>Port of the <c>json.Unmarshal</c> into clineEnvelope[clineMeData].</summary>
    internal static ClineMeData ParseMeEnvelope(byte[] body)
    {
        using var document = ParseDocument(body);
        var root = document.RootElement;
        var data = new ClineMeData();
        if (root.ValueKind == JsonValueKind.Null)
        {
            return data;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw RootTypeMismatch(root, "provider_quota.clineEnvelope[provider_quota.clineMeData]");
        }

        foreach (var property in root.EnumerateObject())
        {
            if (FoldAscii(property.Name) == "data")
            {
                data = ParseMeData(property.Value, data);
            }
        }

        return data;
    }

    /// <summary>Port of the <c>json.Unmarshal</c> into clineEnvelope[clinePlansResponse].</summary>
    internal static List<ClinePlan>? ParsePlansEnvelope(byte[] body)
    {
        using var document = ParseDocument(body);
        var root = document.RootElement;
        List<ClinePlan>? data = null;
        if (root.ValueKind == JsonValueKind.Null)
        {
            return data;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw RootTypeMismatch(root, "provider_quota.clineEnvelope[provider_quota.clinePlansResponse]");
        }

        foreach (var property in root.EnumerateObject())
        {
            if (FoldAscii(property.Name) == "data")
            {
                data = ParsePlans(property.Value);
            }
        }

        return data;
    }

    /// <summary>Port of the <c>json.Unmarshal</c> into clineEnvelope[clineBalanceData].</summary>
    internal static ClineBalanceData ParseBalanceEnvelope(byte[] body)
    {
        using var document = ParseDocument(body);
        var root = document.RootElement;
        var data = new ClineBalanceData();
        if (root.ValueKind == JsonValueKind.Null)
        {
            return data;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw RootTypeMismatch(root, "provider_quota.clineEnvelope[provider_quota.clineBalanceData]");
        }

        foreach (var property in root.EnumerateObject())
        {
            if (FoldAscii(property.Name) == "data")
            {
                data = ParseBalanceData(property.Value, data);
            }
        }

        return data;
    }

    /// <summary>Port of the <c>json.Unmarshal</c> into clineEnvelope[clineUsagesData].</summary>
    internal static ClineUsagesData ParseUsagesEnvelope(byte[] body)
    {
        using var document = ParseDocument(body);
        var root = document.RootElement;
        var data = new ClineUsagesData();
        if (root.ValueKind == JsonValueKind.Null)
        {
            return data;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw RootTypeMismatch(root, "provider_quota.clineEnvelope[provider_quota.clineUsagesData]");
        }

        foreach (var property in root.EnumerateObject())
        {
            if (FoldAscii(property.Name) == "data")
            {
                data = ParseUsagesData(property.Value, data);
            }
        }

        return data;
    }

    /// <summary>Port of the <c>json.Unmarshal</c> into clineEnvelope[clineUsageLimitsData].</summary>
    internal static ClineUsageLimitsData ParseUsageLimitsEnvelope(byte[] body)
    {
        using var document = ParseDocument(body);
        var root = document.RootElement;
        var data = new ClineUsageLimitsData();
        if (root.ValueKind == JsonValueKind.Null)
        {
            return data;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw RootTypeMismatch(root, "provider_quota.clineEnvelope[provider_quota.clineUsageLimitsData]");
        }

        foreach (var property in root.EnumerateObject())
        {
            if (FoldAscii(property.Name) == "data")
            {
                data = ParseUsageLimitsData(property.Value, data);
            }
        }

        return data;
    }

    /// <summary>
    /// Port of (*clineUsageLimit).UnmarshalJSON (cline_checker.go:131-175):
    /// exact (case-sensitive) keys, per-field errors swallowed, non-object
    /// elements decode to the zero limit.
    /// </summary>
    internal static ClineUsageLimit ParseUsageLimitElement(JsonElement element)
    {
        var limit = new ClineUsageLimit();
        if (element.ValueKind != JsonValueKind.Object)
        {
            return limit;
        }

        // json.Unmarshal into map[string]json.RawMessage: last duplicate key
        // wins and keys are matched exactly.
        JsonElement? type = null;
        JsonElement? percentUsed = null;
        JsonElement? resetsAt = null;
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Name)
            {
                case "type":
                    type = property.Value;
                    break;
                case "percentUsed":
                    percentUsed = property.Value;
                    break;
                case "resetsAt":
                    resetsAt = property.Value;
                    break;
            }
        }

        if (type is { } typeValue && typeValue.ValueKind == JsonValueKind.String)
        {
            // A wrong type makes json.Unmarshal fail, and that error is
            // deliberately ignored; the field keeps its zero value.
            limit.Type = typeValue.GetString() ?? string.Empty;
        }

        if (percentUsed is { ValueKind: JsonValueKind.Number } percentValue
            && GoCast.TryParseFloat(percentValue.GetRawText(), out var percent))
        {
            limit.PercentUsed = percent;
        }

        if (resetsAt is { } resetValue)
        {
            var raw = resetValue.GetRawText().Trim();
            if (raw == "null")
            {
                limit.ResetFieldState = ResetStateInactive;
            }
            else if (resetValue.ValueKind == JsonValueKind.String)
            {
                limit.ResetsAt = resetValue.GetString() ?? string.Empty;
                limit.ResetFieldState = GoStrings.TrimSpace(limit.ResetsAt).Length == 0
                    ? ResetStateInactive
                    : ResetStateActive;
            }
            else
            {
                limit.ResetFieldState = ResetStateInvalid;
            }
        }
        else
        {
            limit.ResetFieldState = ResetStateUnavailable;
        }

        return limit;
    }

    private static JsonDocument ParseDocument(byte[] body)
        => JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(body) ?? body);

    private static ClineMeData ParseMeData(JsonElement value, ClineMeData current)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return current;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw FieldTypeMismatch(value, "clineEnvelope", "data", "provider_quota.clineMeData");
        }

        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "id":
                    current.Id = StringValue(property.Value, "clineMeData", "data.id", current.Id);
                    break;
                case "organizations":
                    // []any: null is a nil slice and any JSON array decodes;
                    // the checker never reads the contents (C208).
                    if (property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Array))
                    {
                        throw FieldTypeMismatch(
                            property.Value,
                            "clineMeData",
                            "data.organizations",
                            "[]interface {}");
                    }

                    break;
            }
        }

        return current;
    }

    private static List<ClinePlan>? ParsePlans(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw FieldTypeMismatch(
                value,
                "clineEnvelope",
                "data",
                "provider_quota.clinePlansResponse");
        }

        var plans = new List<ClinePlan>();
        foreach (var element in value.EnumerateArray())
        {
            plans.Add(ParsePlan(element));
        }

        return plans;
    }

    private static ClinePlan ParsePlan(JsonElement value)
    {
        // A null array element leaves the struct at its zero value, because
        // unmarshaling null into a struct is a no-op.
        if (value.ValueKind == JsonValueKind.Null)
        {
            return new ClinePlan();
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw FieldTypeMismatch(value, "clinePlansResponse", "[].", "provider_quota.clinePlan");
        }

        var plan = new ClinePlan();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "type":
                    plan.Type = StringValue(property.Value, "clinePlan", "type", plan.Type);
                    break;
                case "interval":
                    plan.Interval = StringValue(property.Value, "clinePlan", "interval", plan.Interval);
                    break;
                case "isactive":
                    plan.IsActive = BoolValue(property.Value, "clinePlan", "isActive", plan.IsActive);
                    break;
                case "entitlements":
                    plan.Entitlements = ParseEntitlements(property.Value, plan.Entitlements);
                    break;
            }
        }

        return plan;
    }

    private static ClineEntitlements ParseEntitlements(
        JsonElement value,
        ClineEntitlements current)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return current;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw FieldTypeMismatch(
                value,
                "clinePlan",
                "entitlements",
                "provider_quota.clineEntitlements");
        }

        foreach (var property in value.EnumerateObject())
        {
            if (FoldAscii(property.Name) == "cline_pass")
            {
                current.ClinePass = ParseClinePass(property.Value, current.ClinePass);
            }
        }

        return current;
    }

    private static ClinePassEntitlement? ParseClinePass(
        JsonElement value,
        ClinePassEntitlement? current)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw FieldTypeMismatch(
                value,
                "clineEntitlements",
                "entitlements.cline_pass",
                "provider_quota.clinePassEntitlement");
        }

        current ??= new ClinePassEntitlement();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "enabled":
                    current.Enabled = BoolValue(
                        property.Value,
                        "clinePassEntitlement",
                        "entitlements.cline_pass.enabled",
                        current.Enabled);
                    break;
                case "inferencecapthreshold":
                    current.InferenceCapThreshold = ParseInferenceCapThreshold(
                        property.Value,
                        current.InferenceCapThreshold);
                    break;
            }
        }

        return current;
    }

    private static ClineInferenceCapThreshold? ParseInferenceCapThreshold(
        JsonElement value,
        ClineInferenceCapThreshold? current)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw FieldTypeMismatch(
                value,
                "clinePassEntitlement",
                "entitlements.cline_pass.inferenceCapThreshold",
                "provider_quota.clineInferenceCapThreshold");
        }

        current ??= new ClineInferenceCapThreshold();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "last5hoursusagecostusdperuser":
                    current.Last5HoursUsageCostUsdPerUser = Int64Value(
                        property.Value,
                        "clineInferenceCapThreshold",
                        "last5HoursUsageCostUSDPerUser",
                        current.Last5HoursUsageCostUsdPerUser);
                    break;
                case "last7daysusagecostusdperuser":
                    current.Last7DaysUsageCostUsdPerUser = Int64Value(
                        property.Value,
                        "clineInferenceCapThreshold",
                        "last7daysUsageCostUSDPerUser",
                        current.Last7DaysUsageCostUsdPerUser);
                    break;
                case "last30daysusagecostusdperuser":
                    current.Last30DaysUsageCostUsdPerUser = Int64Value(
                        property.Value,
                        "clineInferenceCapThreshold",
                        "last30daysUsageCostUSDPerUser",
                        current.Last30DaysUsageCostUsdPerUser);
                    break;
            }
        }

        return current;
    }

    private static ClineBalanceData ParseBalanceData(JsonElement value, ClineBalanceData current)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return current;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw FieldTypeMismatch(
                value,
                "clineEnvelope",
                "data",
                "provider_quota.clineBalanceData");
        }

        foreach (var property in value.EnumerateObject())
        {
            if (FoldAscii(property.Name) != "balance")
            {
                continue;
            }

            // *int64: null resets the pointer, a number allocates it.
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                current.Balance = null;
                continue;
            }

            current.Balance = Int64Value(
                property.Value,
                "clineBalanceData",
                "data.balance",
                current.Balance ?? 0);
        }

        return current;
    }

    private static ClineUsagesData ParseUsagesData(JsonElement value, ClineUsagesData current)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return current;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw FieldTypeMismatch(
                value,
                "clineEnvelope",
                "data",
                "provider_quota.clineUsagesData");
        }

        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "items":
                    current.Items = ParseUsageItems(property.Value);
                    break;
                case "nexttoken":
                    current.NextToken = StringValue(
                        property.Value,
                        "clineUsagesData",
                        "data.nextToken",
                        current.NextToken);
                    break;
            }
        }

        return current;
    }

    private static List<ClineUsageItem>? ParseUsageItems(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw FieldTypeMismatch(
                value,
                "clineUsagesData",
                "data.items",
                "[]provider_quota.clineUsageItem");
        }

        var items = new List<ClineUsageItem>();
        foreach (var element in value.EnumerateArray())
        {
            items.Add(ParseUsageItem(element));
        }

        return items;
    }

    private static ClineUsageItem ParseUsageItem(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return new ClineUsageItem();
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw FieldTypeMismatch(
                value,
                "clineUsagesData",
                "data.items[]",
                "provider_quota.clineUsageItem");
        }

        var item = new ClineUsageItem();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "createdat":
                    item.CreatedAt = StringValue(
                        property.Value,
                        "clineUsageItem",
                        "createdAt",
                        item.CreatedAt);
                    break;
                case "costusd":
                    item.CostUsd = Int64Value(
                        property.Value,
                        "clineUsageItem",
                        "costUsd",
                        item.CostUsd);
                    break;
                case "creditsused":
                    item.CreditsUsed = Int64Value(
                        property.Value,
                        "clineUsageItem",
                        "creditsUsed",
                        item.CreditsUsed);
                    break;
                case "aimodeltypename":
                    item.AiModelTypeName = StringValue(
                        property.Value,
                        "clineUsageItem",
                        "aiModelTypeName",
                        item.AiModelTypeName);
                    break;
            }
        }

        return item;
    }

    private static ClineUsageLimitsData ParseUsageLimitsData(
        JsonElement value,
        ClineUsageLimitsData current)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return current;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw FieldTypeMismatch(
                value,
                "clineEnvelope",
                "data",
                "provider_quota.clineUsageLimitsData");
        }

        foreach (var property in value.EnumerateObject())
        {
            if (FoldAscii(property.Name) != "limits")
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                current.Limits = null;
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                throw FieldTypeMismatch(
                    property.Value,
                    "clineUsageLimitsData",
                    "data.limits",
                    "[]provider_quota.clineUsageLimit");
            }

            var limits = new List<ClineUsageLimit>();
            foreach (var element in property.Value.EnumerateArray())
            {
                limits.Add(ParseUsageLimitElement(element));
            }

            current.Limits = limits;
        }

        return current;
    }

    private static string StringValue(
        JsonElement value,
        string structName,
        string fieldPath,
        string current)
        => value.ValueKind switch
        {
            JsonValueKind.Null => current,
            JsonValueKind.String => value.GetString() ?? string.Empty,
            _ => throw FieldTypeMismatch(value, structName, fieldPath, "string"),
        };

    private static bool BoolValue(
        JsonElement value,
        string structName,
        string fieldPath,
        bool current)
        => value.ValueKind switch
        {
            JsonValueKind.Null => current,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw FieldTypeMismatch(value, structName, fieldPath, "bool"),
        };

    private static long Int64Value(
        JsonElement value,
        string structName,
        string fieldPath,
        long current)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return current;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            throw FieldTypeMismatch(value, structName, fieldPath, "int64");
        }

        var raw = value.GetRawText();
        // strconv.ParseInt(s, 10, 64): Go accepts only integer literals; a
        // fraction or exponent is a type error (e.g. 1.0 into int64).
        if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            throw FieldTypeMismatch("number " + raw, structName, fieldPath, "int64");
        }

        return parsed;
    }

    private static JsonException RootTypeMismatch(JsonElement value, string goType)
        => new("json: cannot unmarshal " + GoKindName(value.ValueKind) + " into Go value of type " + goType);

    private static JsonException FieldTypeMismatch(
        JsonElement value,
        string structName,
        string fieldPath,
        string type)
        => FieldTypeMismatch(GoKindName(value.ValueKind), structName, fieldPath, type);

    private static JsonException FieldTypeMismatch(
        string value,
        string structName,
        string fieldPath,
        string type)
        => new(
            "json: cannot unmarshal "
            + value
            + " into Go struct field "
            + structName
            + "."
            + fieldPath
            + " of type "
            + type);

    private static string GoKindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        _ => "null",
    };

    /// <summary>Go's encoding/json field lookup folds ASCII letters only.</summary>
    private static string FoldAscii(string name)
    {
        var chars = name.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (chars[index] is >= 'A' and <= 'Z')
            {
                chars[index] = (char)(chars[index] + ('a' - 'A'));
            }
        }

        return new string(chars);
    }
}

/// <summary>
/// Port of <c>clineHTTPError</c> (cline_checker.go:364-377): only the status
/// code, so the error text can never leak the upstream body.
/// </summary>
internal sealed class ClineHttpStatusException : Exception
{
    public ClineHttpStatusException(int statusCode)
        : base(Format(statusCode))
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }

    private static string Format(int statusCode)
    {
        var code = statusCode.ToString(CultureInfo.InvariantCulture);
        var text = GoHttpStatus.Text(statusCode);
        return text.Length == 0 ? "HTTP " + code : "HTTP " + code + " " + text;
    }
}

/// <summary>Port of <c>clineMeData</c> (cline_checker.go:73-76).</summary>
internal sealed class ClineMeData
{
    public string Id { get; set; } = string.Empty;
}

/// <summary>Port of <c>clineBalanceData</c> (cline_checker.go:78-80).</summary>
internal sealed class ClineBalanceData
{
    /// <summary>*int64: null means the response had no balance field.</summary>
    public long? Balance { get; set; }
}

/// <summary>Port of <c>clinePlan</c> (cline_checker.go:84-89).</summary>
internal sealed class ClinePlan
{
    public string Type { get; set; } = string.Empty;

    public string Interval { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public ClineEntitlements Entitlements { get; set; } = new();
}

/// <summary>Port of <c>clineEntitlements</c> (cline_checker.go:91-93).</summary>
internal sealed class ClineEntitlements
{
    public ClinePassEntitlement? ClinePass { get; set; }
}

/// <summary>Port of <c>clinePassEntitlement</c> (cline_checker.go:95-98).</summary>
internal sealed class ClinePassEntitlement
{
    public bool Enabled { get; set; }

    public ClineInferenceCapThreshold? InferenceCapThreshold { get; set; }
}

/// <summary>Port of <c>clineInferenceCapThreshold</c> (cline_checker.go:100-104).</summary>
internal sealed class ClineInferenceCapThreshold
{
    public long Last5HoursUsageCostUsdPerUser { get; set; }

    public long Last7DaysUsageCostUsdPerUser { get; set; }

    public long Last30DaysUsageCostUsdPerUser { get; set; }

    public ClineInferenceCapThreshold Clone() => new()
    {
        Last5HoursUsageCostUsdPerUser = Last5HoursUsageCostUsdPerUser,
        Last7DaysUsageCostUsdPerUser = Last7DaysUsageCostUsdPerUser,
        Last30DaysUsageCostUsdPerUser = Last30DaysUsageCostUsdPerUser,
    };
}

/// <summary>Port of <c>clineUsagesData</c> (cline_checker.go:106-109).</summary>
internal sealed class ClineUsagesData
{
    public List<ClineUsageItem>? Items { get; set; }

    public string NextToken { get; set; } = string.Empty;
}

/// <summary>Port of <c>clineUsageItem</c> (cline_checker.go:111-116).</summary>
internal sealed class ClineUsageItem
{
    public string CreatedAt { get; set; } = string.Empty;

    public long CostUsd { get; set; }

    public long CreditsUsed { get; set; }

    public string AiModelTypeName { get; set; } = string.Empty;
}

/// <summary>Port of <c>clineUsageLimitsData</c> (cline_checker.go:118-120).</summary>
internal sealed class ClineUsageLimitsData
{
    public List<ClineUsageLimit>? Limits { get; set; }
}

/// <summary>
/// Port of <c>clineUsageLimit</c> (cline_checker.go:122-129). The reference
/// implements UnmarshalJSON for this type, so the decoder tracks whether the
/// reset field was missing, null, empty, active or invalid.
/// </summary>
internal sealed class ClineUsageLimit
{
    public string Type { get; set; } = string.Empty;

    public double? PercentUsed { get; set; }

    public string ResetsAt { get; set; } = string.Empty;

    /// <summary><c>active</c>, <c>inactive</c>, <c>unavailable</c> or <c>invalid</c>.</summary>
    public string ResetFieldState { get; set; } = string.Empty;
}

/// <summary>Port of <c>clineOfficialWindowLimit</c> (cline_checker.go:177-181).</summary>
internal sealed class ClineOfficialWindowLimit
{
    public double? UsageRatio { get; set; }

    public DateTimeOffset? NextResetAt { get; set; }

    public string ResetState { get; set; } = string.Empty;

    public ClineOfficialWindowLimit Clone() => new()
    {
        UsageRatio = UsageRatio,
        NextResetAt = NextResetAt,
        ResetState = ResetState,
    };
}

/// <summary>Port of <c>clineUsageLimitsFetchMeta</c> (cline_checker.go:183-189).</summary>
internal sealed class ClineUsageLimitsFetchMeta
{
    public string Status { get; set; } = string.Empty;

    public int EntriesSeen { get; set; }

    public int RecognizedEntries { get; set; }

    public int UsableWindows { get; set; }

    public int UsableFields { get; set; }
}

/// <summary>Result of fetchUsageLimits: limits, meta and the optional error.</summary>
internal sealed class ClineUsageLimitsFetchOutcome
{
    public Dictionary<string, ClineOfficialWindowLimit>? Limits { get; init; }

    public ClineUsageLimitsFetchMeta Meta { get; init; } = new();

    public Exception? Error { get; init; }
}

/// <summary>Port of <c>clineWindow</c> (cline_checker.go:191-209).</summary>
internal sealed class ClineWindow
{
    public string Key { get; set; } = string.Empty;

    public TimeSpan Duration { get; set; }

    public long LimitUnits { get; set; }

    public long UsedUnits { get; set; }

    public long CreditsUsed { get; set; }

    public int ItemsCount { get; set; }

    public double? UsageRatio { get; set; }

    public double? CostUsageRatio { get; set; }

    public string UsageSource { get; set; } = string.Empty;

    public string CostSource { get; set; } = string.Empty;

    public DateTimeOffset? NextResetAt { get; set; }

    public DateTimeOffset? WindowStartAt { get; set; }

    public DateTimeOffset? CostStartAt { get; set; }

    public string ResetSource { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    public bool Active { get; set; }

    public bool CostAvailable { get; set; }
}

/// <summary>Port of <c>clineUsageFetchMeta</c> (cline_checker.go:211-226).</summary>
internal sealed class ClineUsageFetchMeta
{
    public int Pages { get; set; }

    public int ItemsSeen { get; set; }

    public int ClinePassItemsSeen { get; set; }

    public int DirectItemsSeen { get; set; }

    public int UnclassifiedItemsSeen { get; set; }

    public int InvalidTimestampItems { get; set; }

    public bool Truncated { get; set; }

    public bool LedgerUnavailable { get; set; }

    public string LedgerErrorCode { get; set; } = string.Empty;
}

/// <summary>Result of fetchUsageItems: partial items, partial meta and the error.</summary>
internal sealed class ClineUsageItemsFetchOutcome
{
    public List<ClineUsageItem>? Items { get; init; }

    public ClineUsageFetchMeta Meta { get; init; } = new();

    public Exception? Error { get; init; }
}
