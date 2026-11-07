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
/// Port of the Synthetic quota checker
/// (internal/server/biz/provider_quota/synthetic_checker.go). The checker GETs
/// the fixed <c>/v2/quotas</c> endpoint derived from the channel host and maps
/// the rolling five-hour pool and the weekly token pool onto quota limits. The
/// weekly pool has two shapes (credit amounts on regular plans, percentages on
/// big/pro plans); <see cref="SyntheticUsageResponse.WeeklyLimit"/> folds them
/// field by field. The hourly search counter is listed but never gates the
/// channel.
/// </summary>
public sealed class SyntheticQuotaChecker : IQuotaChecker
{
    private const string ProviderType = "synthetic";

    /// <summary>Port of the fallback quota URL (synthetic_checker.go:233,238).</summary>
    internal const string DefaultQuotaUrl = "https://api.synthetic.new/v2/quotas";

    /// <summary>Port of the path appended to the channel host (synthetic_checker.go:246).</summary>
    private const string QuotaPath = "/v2/quotas";

    /// <summary>Port of syntheticSearchHourlyWindow (synthetic_checker.go:294).</summary>
    internal const string SearchHourlyWindow = "search_hourly";

    private const string ParseResponseErrorPrefix = "failed to parse synthetic usage response: ";

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;

    public SyntheticQuotaChecker(
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

    /// <summary>Port of (*SyntheticQuotaChecker).CheckQuota (synthetic_checker.go:84-118).</summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var apiKey = ResolveApiKey(channel);
        if (apiKey.Length == 0)
        {
            throw new InvalidOperationException("channel has no API key");
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");

        // WithBearerToken stores the request auth; the shared executor turns it
        // into "Authorization: Bearer <token>" on the wire.
        var request = new HttpRequestData
        {
            Method = "GET",
            Url = BuildQuotaUrl(channel.BaseUrl),
            Headers = headers,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
        };

        // provider_quota.go:46-48: a channel proxy replaces the shared client.
        var executor = ResolveExecutor(channel);

        HttpResponseData response;
        try
        {
            response = executor.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "quota request failed: ");
        }

        // The shared executor already raises for >=400, so this only sees
        // 1xx/3xx and non-200 2xx, mirroring the reference's explicit check.
        if (response.StatusCode != 200)
        {
            throw new InvalidOperationException(
                "HTTP "
                + response.StatusCode
                + ": "
                + Encoding.UTF8.GetString(response.Body ?? []));
        }

        return Task.FromResult(ParseResponse(response.Body ?? [], _timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// Port of (*SyntheticQuotaChecker).SupportsChannel
    /// (synthetic_checker.go:222-228).
    /// </summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.Type != ChannelOutboundFactory.OpenAiChannelType
            && channel.Type != ChannelOutboundFactory.OpenaiResponsesChannelType)
        {
            return false;
        }

        return ProviderQuotaUrlDetection.DetectProviderFromUrl(channel.BaseUrl) == ProviderType;
    }

    /// <summary>Port of the API-key extraction at the top of CheckQuota (synthetic_checker.go:85-92).</summary>
    internal static string ResolveApiKey(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // Prefer the trimmed legacy field, then the first apiKeys entry
        // verbatim (the reference does not trim the fallback entry).
        var apiKey = GoStrings.TrimSpace(ChannelCredentials.GetLegacyApiKey(channel.Credentials));
        if (apiKey.Length == 0)
        {
            var apiKeys = ChannelCredentials.GetApiKeys(channel.Credentials);
            if (apiKeys.Count > 0)
            {
                apiKey = apiKeys[0];
            }
        }

        return apiKey;
    }

    /// <summary>Port of buildSyntheticQuotaURL (synthetic_checker.go:230-247).</summary>
    internal static string BuildQuotaUrl(string? baseUrl)
    {
        var trimmed = GoStrings.TrimSpace(baseUrl ?? string.Empty);
        if (trimmed.Length == 0)
        {
            return DefaultQuotaUrl;
        }

        // url.Parse errors and the "empty scheme and host" case both fall back;
        // the Go parse also lowercases the scheme and drops userinfo/path.
        if (!GoUrl.TryParse(trimmed, out var scheme, out var host, out _)
            || (scheme.Length == 0 && host.Length == 0))
        {
            return DefaultQuotaUrl;
        }

        if (scheme == "http")
        {
            scheme = "https";
        }

        return scheme + "://" + host + QuotaPath;
    }

    /// <summary>Port of parseResponse (synthetic_checker.go:120-193).</summary>
    internal static QuotaData ParseResponse(byte[] body, DateTimeOffset now)
    {
        SyntheticUsageResponse response;
        try
        {
            response = ParseUsageResponse(body);
        }
        catch (JsonException ex)
        {
            throw GoErrors.Wrap(ex, ParseResponseErrorPrefix);
        }

        var weekly = response.WeeklyLimit();

        var normalizedStatus = ProviderQuotaStatuses.Unknown;
        var limits = BuildSyntheticLimitStatuses(weekly, response.RollingFiveHourLimit);

        if (limits.Count > 0)
        {
            normalizedStatus = ProviderQuotaStatuses.Available;
            foreach (var limit in limits)
            {
                if (limit.Status == ProviderQuotaStatuses.Exhausted)
                {
                    normalizedStatus = ProviderQuotaStatuses.Exhausted;
                    break;
                }

                if (limit.Status == ProviderQuotaStatuses.Warning)
                {
                    normalizedStatus = ProviderQuotaStatuses.Warning;
                }
            }
        }

        // The search counter is displayed but never gates the channel: it is
        // appended after the blocking limits and stays out of the escalation.
        if (response.Search is not null
            && SearchHourlyLimitStatus(response.Search.Hourly, out var searchLimit))
        {
            limits.Add(searchLimit);
        }

        var nextResetAt = FindEarliestSyntheticResetAt(
            response.Subscription,
            weekly,
            response.RollingFiveHourLimit);

        return ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = normalizedStatus,
                ProviderType = ProviderType,
                RawData = PrepareRawData(response),
                NextResetAt = nextResetAt,
                Ready = ProviderQuotaStatuses.IsReadyStatus(normalizedStatus),
                Limits = limits,
            },
            now);
    }

    /// <summary>Port of buildSyntheticLimitStatuses (synthetic_checker.go:249-290).</summary>
    internal static List<QuotaLimitStatus> BuildSyntheticLimitStatuses(
        SyntheticWeeklyTokenLimit? weekly,
        SyntheticRollingFiveHourLimit? fiveHour)
    {
        var limits = new List<QuotaLimitStatus>(2);

        if (fiveHour is not null)
        {
            var status = ProviderQuotaStatuses.Available;
            var usageRatio = 0.0;

            if (fiveHour.Limited is true)
            {
                status = ProviderQuotaStatuses.Exhausted;
                usageRatio = 1.0;
            }
            else if (fiveHour.TickPercent is { } tickPercent)
            {
                usageRatio = tickPercent;
                if (usageRatio >= ProviderQuotaData.WarningThresholdRatio)
                {
                    status = ProviderQuotaStatuses.Warning;
                }
            }

            // The rolling window's nextTickAt is a regeneration tick, not the
            // end of a fixed window, so no period start can be derived from it.
            limits.Add(new QuotaLimitStatus
            {
                Type = QuotaLimitTypes.Token,
                Status = status,
                UsageRatio = usageRatio,
                Ready = ProviderQuotaStatuses.IsReadyStatus(status),
                NextResetAt = fiveHour.NextTickAt is { } nextTickAt
                    ? GoTimeParsing.TryParseRfc3339(nextTickAt)
                    : null,
                Window = QuotaWindows.FiveHours,
            });
        }

        if (weekly is not null)
        {
            limits.Add(WeeklyTokenLimitStatus(weekly));
        }

        return limits;
    }

    /// <summary>
    /// Port of searchHourlyLimitStatus (synthetic_checker.go:302-331). The
    /// hourly search-request counter is converted into a display-only limit:
    /// it is never blocking because it carries no availability group, and the
    /// status escalation above runs before it is appended.
    /// </summary>
    internal static bool SearchHourlyLimitStatus(
        SyntheticSearchHourly? hourly,
        out QuotaLimitStatus limit)
    {
        limit = null!;
        if (hourly?.Limit is not { } limitValue
            || hourly.Requests is not { } requests
            || limitValue <= 0)
        {
            return false;
        }

        var usageRatio = (double)requests / limitValue;

        var status = ProviderQuotaStatuses.Available;
        if (usageRatio >= 1.0)
        {
            status = ProviderQuotaStatuses.Exhausted;
        }
        else if (usageRatio >= ProviderQuotaData.WarningThresholdRatio)
        {
            status = ProviderQuotaStatuses.Warning;
        }

        limit = new QuotaLimitStatus
        {
            Type = QuotaLimitTypes.Search,
            Status = status,
            UsageRatio = usageRatio,
            Ready = ProviderQuotaStatuses.IsReadyStatus(status),
            NextResetAt = hourly.RenewsAt is { } renewsAt
                ? GoTimeParsing.TryParseRfc3339(renewsAt)
                : null,
            Window = SearchHourlyWindow,
        };
        return true;
    }

    /// <summary>Port of weeklyTokenLimitStatus (synthetic_checker.go:333-388).</summary>
    internal static QuotaLimitStatus WeeklyTokenLimitStatus(SyntheticWeeklyTokenLimit weekly)
    {
        ArgumentNullException.ThrowIfNull(weekly);

        var status = ProviderQuotaStatuses.Available;
        var usageRatio = 0.0;

        if (weekly.PercentRemaining is { } percentRemaining)
        {
            usageRatio = 1.0 - (percentRemaining / 100.0);
            if (usageRatio >= 1.0)
            {
                status = ProviderQuotaStatuses.Exhausted;
            }
            else if (usageRatio >= ProviderQuotaData.WarningThresholdRatio)
            {
                status = ProviderQuotaStatuses.Warning;
            }
        }

        // nextRegenAt marks the next incremental regeneration of the weekly
        // pool rather than a hard window boundary, so it cannot anchor a
        // period start.
        var limit = new QuotaLimitStatus
        {
            Type = QuotaLimitTypes.Token,
            Status = status,
            UsageRatio = usageRatio,
            Ready = ProviderQuotaStatuses.IsReadyStatus(status),
            NextResetAt = weekly.NextRegenAt is { } nextRegenAt
                ? GoTimeParsing.TryParseRfc3339(nextRegenAt)
                : null,
            Window = QuotaWindows.Weekly,
        };

        // Synthetic bills this pool in its own credits and regenerates it in
        // ticks instead of resetting on a window boundary, so the provider's
        // figures are the only honest period money.
        if (TryParseSyntheticCredits(weekly.MaxCredits, out var maxCredits) && maxCredits > 0)
        {
            if (TryParseSyntheticCredits(weekly.RemainingCredits, out var remainingCredits))
            {
                // Two finite, individually valid amounts can still overflow
                // the subtraction; the derived money must stay finite or the
                // quota payload stops being JSON-serializable.
                var usedCredits = maxCredits - remainingCredits;
                if (!double.IsInfinity(usedCredits))
                {
                    var cost = Math.Max(usedCredits, 0.0);
                    limit.PeriodCost = cost;
                    limit.PeriodQuota = maxCredits;
                }
            }
        }

        return limit;
    }

    /// <summary>
    /// Port of parseSyntheticCredits (synthetic_checker.go:390-408): reads the
    /// "$10.50" amounts Synthetic reports for the weekly credit pool.
    /// </summary>
    internal static bool TryParseSyntheticCredits(string? value, out double credits)
    {
        credits = 0;
        if (value is null)
        {
            return false;
        }

        // Go's strings.TrimPrefix removes at most one leading dollar sign.
        var trimmed = GoStrings.TrimSpace(value);
        if (trimmed.Length > 0 && trimmed[0] == '$')
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.Length == 0)
        {
            return false;
        }

        return GoCast.TryParseFloat(trimmed, out credits)
            && !double.IsNaN(credits)
            && !double.IsInfinity(credits);
    }

    /// <summary>Port of findEarliestSyntheticResetAt (synthetic_checker.go:410-444).</summary>
    internal static DateTimeOffset? FindEarliestSyntheticResetAt(
        SyntheticSubscription? subscription,
        SyntheticWeeklyTokenLimit? weeklyTokenLimit,
        SyntheticRollingFiveHourLimit? rollingFiveHourLimit)
    {
        // The search and free-tool-call counters renew hourly/daily, so
        // reporting them as the channel reset would mask the plan quota the
        // status is driven by.
        DateTimeOffset? earliest = null;

        if (subscription?.RenewsAt is { } subscriptionRenewsAt
            && GoTimeParsing.TryParseRfc3339(subscriptionRenewsAt) is { } subscriptionReset)
        {
            earliest = subscriptionReset;
        }

        if (weeklyTokenLimit?.NextRegenAt is { } weeklyNextRegenAt
            && GoTimeParsing.TryParseRfc3339(weeklyNextRegenAt) is { } weeklyReset
            && (earliest is null || weeklyReset < earliest.Value))
        {
            earliest = weeklyReset;
        }

        if (rollingFiveHourLimit?.NextTickAt is { } nextTickAt
            && GoTimeParsing.TryParseRfc3339(nextTickAt) is { } fiveHourReset
            && (earliest is null || fiveHourReset < earliest.Value))
        {
            earliest = fiveHourReset;
        }

        return earliest;
    }

    /// <summary>Port of the rawData construction in parseResponse (synthetic_checker.go:159-183).</summary>
    internal static Dictionary<string, object?> PrepareRawData(SyntheticUsageResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var rawData = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (response.Subscription is { } subscription)
        {
            rawData["subscription"] = ConvertSubscriptionToMap(subscription);
        }

        if (response.Search is { } search)
        {
            rawData["search"] = ConvertSearchToMap(search);
        }

        if (response.FreeToolCalls is { } freeToolCalls)
        {
            rawData["freeToolCalls"] = ConvertFreeToolCallsToMap(freeToolCalls);
        }

        if (response.WeeklyTokenLimit is { } weeklyTokenLimit)
        {
            rawData["weeklyTokenLimit"] = ConvertWeeklyTokenLimitToMap(weeklyTokenLimit);
        }

        if (response.PlanWeeklyLimit is { } planWeeklyLimit)
        {
            rawData["planWeeklyLimit"] = ConvertPlanWeeklyLimitToMap(planWeeklyLimit);
        }

        if (response.RollingFiveHourLimit is { } rollingFiveHourLimit)
        {
            rawData["rollingFiveHourLimit"] = ConvertRollingFiveHourLimitToMap(rollingFiveHourLimit);
        }

        return rawData;
    }

    /// <summary>Port of convertSyntheticSubscriptionToMap (synthetic_checker.go:446-462).</summary>
    internal static Dictionary<string, object?> ConvertSubscriptionToMap(SyntheticSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (subscription.Limit is { } limit)
        {
            result["limit"] = limit;
        }

        if (subscription.Requests is { } requests)
        {
            result["requests"] = requests;
        }

        if (subscription.RenewsAt is { } renewsAt)
        {
            result["renewsAt"] = renewsAt;
        }

        return result;
    }

    /// <summary>Port of convertSyntheticSearchToMap (synthetic_checker.go:464-472).</summary>
    internal static Dictionary<string, object?> ConvertSearchToMap(SyntheticSearch search)
    {
        ArgumentNullException.ThrowIfNull(search);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (search.Hourly is { } hourly)
        {
            result["hourly"] = ConvertSearchHourlyToMap(hourly);
        }

        return result;
    }

    /// <summary>Port of convertSyntheticSearchHourlyToMap (synthetic_checker.go:474-490).</summary>
    internal static Dictionary<string, object?> ConvertSearchHourlyToMap(SyntheticSearchHourly hourly)
    {
        ArgumentNullException.ThrowIfNull(hourly);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (hourly.Limit is { } limit)
        {
            result["limit"] = limit;
        }

        if (hourly.Requests is { } requests)
        {
            result["requests"] = requests;
        }

        if (hourly.RenewsAt is { } renewsAt)
        {
            result["renewsAt"] = renewsAt;
        }

        return result;
    }

    /// <summary>Port of convertSyntheticFreeToolCallsToMap (synthetic_checker.go:492-508).</summary>
    internal static Dictionary<string, object?> ConvertFreeToolCallsToMap(SyntheticFreeToolCalls freeToolCalls)
    {
        ArgumentNullException.ThrowIfNull(freeToolCalls);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (freeToolCalls.Limit is { } limit)
        {
            result["limit"] = limit;
        }

        if (freeToolCalls.Requests is { } requests)
        {
            result["requests"] = requests;
        }

        if (freeToolCalls.RenewsAt is { } renewsAt)
        {
            result["renewsAt"] = renewsAt;
        }

        return result;
    }

    /// <summary>Port of convertSyntheticWeeklyTokenLimitToMap (synthetic_checker.go:510-530).</summary>
    internal static Dictionary<string, object?> ConvertWeeklyTokenLimitToMap(
        SyntheticWeeklyTokenLimit weeklyTokenLimit)
    {
        ArgumentNullException.ThrowIfNull(weeklyTokenLimit);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (weeklyTokenLimit.NextRegenAt is { } nextRegenAt)
        {
            result["nextRegenAt"] = nextRegenAt;
        }

        if (weeklyTokenLimit.PercentRemaining is { } percentRemaining)
        {
            result["percentRemaining"] = percentRemaining;
        }

        if (weeklyTokenLimit.MaxCredits is { } maxCredits)
        {
            result["maxCredits"] = maxCredits;
        }

        if (weeklyTokenLimit.RemainingCredits is { } remainingCredits)
        {
            result["remainingCredits"] = remainingCredits;
        }

        return result;
    }

    /// <summary>Port of convertSyntheticPlanWeeklyLimitToMap (synthetic_checker.go:532-548).</summary>
    internal static Dictionary<string, object?> ConvertPlanWeeklyLimitToMap(
        SyntheticPlanWeeklyLimit planWeeklyLimit)
    {
        ArgumentNullException.ThrowIfNull(planWeeklyLimit);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (planWeeklyLimit.NextRegenAt is { } nextRegenAt)
        {
            result["nextRegenAt"] = nextRegenAt;
        }

        if (planWeeklyLimit.PercentRemaining is { } percentRemaining)
        {
            result["percentRemaining"] = percentRemaining;
        }

        if (planWeeklyLimit.NextRegenPercent is { } nextRegenPercent)
        {
            result["nextRegenPercent"] = nextRegenPercent;
        }

        return result;
    }

    /// <summary>Port of convertSyntheticRollingFiveHourLimitToMap (synthetic_checker.go:550-574).</summary>
    internal static Dictionary<string, object?> ConvertRollingFiveHourLimitToMap(
        SyntheticRollingFiveHourLimit rollingFiveHourLimit)
    {
        ArgumentNullException.ThrowIfNull(rollingFiveHourLimit);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (rollingFiveHourLimit.NextTickAt is { } nextTickAt)
        {
            result["nextTickAt"] = nextTickAt;
        }

        if (rollingFiveHourLimit.TickPercent is { } tickPercent)
        {
            result["tickPercent"] = tickPercent;
        }

        if (rollingFiveHourLimit.Remaining is { } remaining)
        {
            result["remaining"] = remaining;
        }

        if (rollingFiveHourLimit.Max is { } max)
        {
            result["max"] = max;
        }

        if (rollingFiveHourLimit.Limited is { } limited)
        {
            result["limited"] = limited;
        }

        return result;
    }

    /// <summary>Port of the json.Unmarshal call into SyntheticUsageResponse (synthetic_checker.go:123).</summary>
    /// <exception cref="JsonException">
    /// The payload is not a JSON object or a field has the wrong JSON type
    /// (the decoder's message text differs from Go — C003/C093).
    /// </exception>
    internal static SyntheticUsageResponse ParseUsageResponse(byte[] data)
    {
        using var document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        var root = document.RootElement;

        // Go: unmarshaling JSON null into a struct is a no-op.
        if (root.ValueKind == JsonValueKind.Null)
        {
            return new SyntheticUsageResponse();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal "
                + GoKindName(root.ValueKind)
                + " into Go value of type provider_quota.SyntheticUsageResponse");
        }

        var response = new SyntheticUsageResponse();
        foreach (var property in root.EnumerateObject())
        {
            // Go matches a key to its struct field exactly first and
            // case-insensitively second; both spellings reach the same field,
            // and a repeated key keeps the last value (objects merge into the
            // existing pointer, null resets it).
            switch (FoldAscii(property.Name))
            {
                case "subscription":
                    response.Subscription = ParseSubscription(property.Value, response.Subscription);
                    break;
                case "search":
                    response.Search = ParseSearch(property.Value, response.Search);
                    break;
                case "freetoolcalls":
                    response.FreeToolCalls = ParseFreeToolCalls(property.Value, response.FreeToolCalls);
                    break;
                case "weeklytokenlimit":
                    response.WeeklyTokenLimit = ParseWeeklyTokenLimit(
                        property.Value,
                        response.WeeklyTokenLimit);
                    break;
                case "planweeklylimit":
                    response.PlanWeeklyLimit = ParsePlanWeeklyLimit(
                        property.Value,
                        response.PlanWeeklyLimit);
                    break;
                case "rollingfivehourlimit":
                    response.RollingFiveHourLimit = ParseRollingFiveHourLimit(
                        property.Value,
                        response.RollingFiveHourLimit);
                    break;
            }
        }

        return response;
    }

    private static SyntheticSubscription? ParseSubscription(
        JsonElement value,
        SyntheticSubscription? existing)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "SyntheticUsageResponse",
                "subscription",
                "provider_quota.SyntheticSubscription");
        }

        var subscription = existing ?? new SyntheticSubscription();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "limit":
                    subscription.Limit = NullableInt64(
                        property.Value,
                        "SyntheticSubscription",
                        "subscription.limit");
                    break;
                case "requests":
                    subscription.Requests = NullableInt64(
                        property.Value,
                        "SyntheticSubscription",
                        "subscription.requests");
                    break;
                case "renewsat":
                    subscription.RenewsAt = NullableString(
                        property.Value,
                        "SyntheticSubscription",
                        "subscription.renewsAt");
                    break;
            }
        }

        return subscription;
    }

    private static SyntheticSearch? ParseSearch(JsonElement value, SyntheticSearch? existing)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "SyntheticUsageResponse",
                "search",
                "provider_quota.SyntheticSearch");
        }

        var search = existing ?? new SyntheticSearch();
        foreach (var property in value.EnumerateObject())
        {
            if (FoldAscii(property.Name) == "hourly")
            {
                search.Hourly = ParseSearchHourly(property.Value, search.Hourly);
            }
        }

        return search;
    }

    private static SyntheticSearchHourly? ParseSearchHourly(
        JsonElement value,
        SyntheticSearchHourly? existing)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "SyntheticSearch",
                "search.hourly",
                "provider_quota.SyntheticSearchHourly");
        }

        var hourly = existing ?? new SyntheticSearchHourly();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "limit":
                    hourly.Limit = NullableInt64(
                        property.Value,
                        "SyntheticSearchHourly",
                        "search.hourly.limit");
                    break;
                case "requests":
                    hourly.Requests = NullableInt64(
                        property.Value,
                        "SyntheticSearchHourly",
                        "search.hourly.requests");
                    break;
                case "renewsat":
                    hourly.RenewsAt = NullableString(
                        property.Value,
                        "SyntheticSearchHourly",
                        "search.hourly.renewsAt");
                    break;
            }
        }

        return hourly;
    }

    private static SyntheticFreeToolCalls? ParseFreeToolCalls(
        JsonElement value,
        SyntheticFreeToolCalls? existing)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "SyntheticUsageResponse",
                "freeToolCalls",
                "provider_quota.SyntheticFreeToolCalls");
        }

        var freeToolCalls = existing ?? new SyntheticFreeToolCalls();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "limit":
                    freeToolCalls.Limit = NullableInt64(
                        property.Value,
                        "SyntheticFreeToolCalls",
                        "freeToolCalls.limit");
                    break;
                case "requests":
                    freeToolCalls.Requests = NullableInt64(
                        property.Value,
                        "SyntheticFreeToolCalls",
                        "freeToolCalls.requests");
                    break;
                case "renewsat":
                    freeToolCalls.RenewsAt = NullableString(
                        property.Value,
                        "SyntheticFreeToolCalls",
                        "freeToolCalls.renewsAt");
                    break;
            }
        }

        return freeToolCalls;
    }

    private static SyntheticWeeklyTokenLimit? ParseWeeklyTokenLimit(
        JsonElement value,
        SyntheticWeeklyTokenLimit? existing)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "SyntheticUsageResponse",
                "weeklyTokenLimit",
                "provider_quota.SyntheticWeeklyTokenLimit");
        }

        var weeklyTokenLimit = existing ?? new SyntheticWeeklyTokenLimit();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "nextregenat":
                    weeklyTokenLimit.NextRegenAt = NullableString(
                        property.Value,
                        "SyntheticWeeklyTokenLimit",
                        "weeklyTokenLimit.nextRegenAt");
                    break;
                case "percentremaining":
                    weeklyTokenLimit.PercentRemaining = NullableFloat64(
                        property.Value,
                        "SyntheticWeeklyTokenLimit",
                        "weeklyTokenLimit.percentRemaining");
                    break;
                case "maxcredits":
                    weeklyTokenLimit.MaxCredits = NullableString(
                        property.Value,
                        "SyntheticWeeklyTokenLimit",
                        "weeklyTokenLimit.maxCredits");
                    break;
                case "remainingcredits":
                    weeklyTokenLimit.RemainingCredits = NullableString(
                        property.Value,
                        "SyntheticWeeklyTokenLimit",
                        "weeklyTokenLimit.remainingCredits");
                    break;
            }
        }

        return weeklyTokenLimit;
    }

    private static SyntheticPlanWeeklyLimit? ParsePlanWeeklyLimit(
        JsonElement value,
        SyntheticPlanWeeklyLimit? existing)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "SyntheticUsageResponse",
                "planWeeklyLimit",
                "provider_quota.SyntheticPlanWeeklyLimit");
        }

        var planWeeklyLimit = existing ?? new SyntheticPlanWeeklyLimit();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "nextregenat":
                    planWeeklyLimit.NextRegenAt = NullableString(
                        property.Value,
                        "SyntheticPlanWeeklyLimit",
                        "planWeeklyLimit.nextRegenAt");
                    break;
                case "percentremaining":
                    planWeeklyLimit.PercentRemaining = NullableFloat64(
                        property.Value,
                        "SyntheticPlanWeeklyLimit",
                        "planWeeklyLimit.percentRemaining");
                    break;
                case "nextregenpercent":
                    planWeeklyLimit.NextRegenPercent = NullableFloat64(
                        property.Value,
                        "SyntheticPlanWeeklyLimit",
                        "planWeeklyLimit.nextRegenPercent");
                    break;
            }
        }

        return planWeeklyLimit;
    }

    private static SyntheticRollingFiveHourLimit? ParseRollingFiveHourLimit(
        JsonElement value,
        SyntheticRollingFiveHourLimit? existing)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "SyntheticUsageResponse",
                "rollingFiveHourLimit",
                "provider_quota.SyntheticRollingFiveHourLimit");
        }

        var rollingFiveHourLimit = existing ?? new SyntheticRollingFiveHourLimit();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "nexttickat":
                    rollingFiveHourLimit.NextTickAt = NullableString(
                        property.Value,
                        "SyntheticRollingFiveHourLimit",
                        "rollingFiveHourLimit.nextTickAt");
                    break;
                case "tickpercent":
                    rollingFiveHourLimit.TickPercent = NullableFloat64(
                        property.Value,
                        "SyntheticRollingFiveHourLimit",
                        "rollingFiveHourLimit.tickPercent");
                    break;
                case "remaining":
                    rollingFiveHourLimit.Remaining = NullableFloat64(
                        property.Value,
                        "SyntheticRollingFiveHourLimit",
                        "rollingFiveHourLimit.remaining");
                    break;
                case "max":
                    rollingFiveHourLimit.Max = NullableFloat64(
                        property.Value,
                        "SyntheticRollingFiveHourLimit",
                        "rollingFiveHourLimit.max");
                    break;
                case "limited":
                    rollingFiveHourLimit.Limited = NullableBool(
                        property.Value,
                        "SyntheticRollingFiveHourLimit",
                        "rollingFiveHourLimit.limited");
                    break;
            }
        }

        return rollingFiveHourLimit;
    }

    private static bool? NullableBool(JsonElement value, string structName, string fieldPath)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                structName,
                fieldPath,
                "bool"),
        };

    private static string? NullableString(JsonElement value, string structName, string fieldPath)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => value.GetString() ?? string.Empty,
            _ => throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                structName,
                fieldPath,
                "string"),
        };

    private static long? NullableInt64(JsonElement value, string structName, string fieldPath)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            throw StructTypeMismatch(GoKindName(value.ValueKind), structName, fieldPath, "int64");
        }

        var raw = value.GetRawText();
        if (!GoCast.TryParseInt64(raw, out var parsed))
        {
            throw StructTypeMismatch("number " + raw, structName, fieldPath, "int64");
        }

        return parsed;
    }

    private static double? NullableFloat64(JsonElement value, string structName, string fieldPath)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            throw StructTypeMismatch(GoKindName(value.ValueKind), structName, fieldPath, "float64");
        }

        var raw = value.GetRawText();
        if (!GoCast.TryParseFloat(raw, out var parsed))
        {
            throw StructTypeMismatch("number " + raw, structName, fieldPath, "float64");
        }

        return parsed;
    }

    private static JsonException StructTypeMismatch(
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

/// <summary>
/// Port of <c>SyntheticUsageResponse</c> (synthetic_checker.go:19-26). Every
/// field is optional in the reference, so each maps to a nullable value; the
/// nested objects keep Go's "null resets the pointer, objects merge" semantics.
/// </summary>
internal sealed class SyntheticUsageResponse
{
    public SyntheticSubscription? Subscription { get; set; }

    public SyntheticSearch? Search { get; set; }

    public SyntheticFreeToolCalls? FreeToolCalls { get; set; }

    public SyntheticWeeklyTokenLimit? WeeklyTokenLimit { get; set; }

    public SyntheticPlanWeeklyLimit? PlanWeeklyLimit { get; set; }

    public SyntheticRollingFiveHourLimit? RollingFiveHourLimit { get; set; }

    /// <summary>Port of (*SyntheticUsageResponse).weeklyLimit (synthetic_checker.go:200-220).</summary>
    public SyntheticWeeklyTokenLimit? WeeklyLimit()
    {
        if (WeeklyTokenLimit is null && PlanWeeklyLimit is null)
        {
            return null;
        }

        var merged = new SyntheticWeeklyTokenLimit();
        if (WeeklyTokenLimit is not null)
        {
            merged.NextRegenAt = WeeklyTokenLimit.NextRegenAt;
            merged.PercentRemaining = WeeklyTokenLimit.PercentRemaining;
            merged.MaxCredits = WeeklyTokenLimit.MaxCredits;
            merged.RemainingCredits = WeeklyTokenLimit.RemainingCredits;
        }

        if (PlanWeeklyLimit is { } plan)
        {
            if (plan.NextRegenAt is not null)
            {
                merged.NextRegenAt = plan.NextRegenAt;
            }

            if (plan.PercentRemaining is not null)
            {
                merged.PercentRemaining = plan.PercentRemaining;
            }
        }

        return merged;
    }
}

/// <summary>Port of <c>SyntheticSubscription</c> (synthetic_checker.go:28-32).</summary>
internal sealed class SyntheticSubscription
{
    public long? Limit { get; set; }

    public long? Requests { get; set; }

    public string? RenewsAt { get; set; }
}

/// <summary>Port of <c>SyntheticSearch</c> (synthetic_checker.go:34-36).</summary>
internal sealed class SyntheticSearch
{
    public SyntheticSearchHourly? Hourly { get; set; }
}

/// <summary>Port of <c>SyntheticSearchHourly</c> (synthetic_checker.go:38-42).</summary>
internal sealed class SyntheticSearchHourly
{
    public long? Limit { get; set; }

    public long? Requests { get; set; }

    public string? RenewsAt { get; set; }
}

/// <summary>Port of <c>SyntheticFreeToolCalls</c> (synthetic_checker.go:44-48).</summary>
internal sealed class SyntheticFreeToolCalls
{
    public long? Limit { get; set; }

    public long? Requests { get; set; }

    public string? RenewsAt { get; set; }
}

/// <summary>Port of <c>SyntheticWeeklyTokenLimit</c> (synthetic_checker.go:50-55).</summary>
internal sealed class SyntheticWeeklyTokenLimit
{
    public string? NextRegenAt { get; set; }

    public double? PercentRemaining { get; set; }

    public string? MaxCredits { get; set; }

    public string? RemainingCredits { get; set; }
}

/// <summary>
/// Port of <c>SyntheticPlanWeeklyLimit</c> (synthetic_checker.go:57-64): the
/// weekly pool shape reported by big/pro new-version plans, with percentages
/// and the next regeneration tick but no credit amounts.
/// </summary>
internal sealed class SyntheticPlanWeeklyLimit
{
    public string? NextRegenAt { get; set; }

    public double? PercentRemaining { get; set; }

    public double? NextRegenPercent { get; set; }
}

/// <summary>Port of <c>SyntheticRollingFiveHourLimit</c> (synthetic_checker.go:66-72).</summary>
internal sealed class SyntheticRollingFiveHourLimit
{
    public string? NextTickAt { get; set; }

    public double? TickPercent { get; set; }

    public double? Remaining { get; set; }

    public double? Max { get; set; }

    public bool? Limited { get; set; }
}
