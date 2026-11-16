using System.Globalization;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the Apertis quota checker
/// (internal/server/biz/provider_quota/apertis_checker.go): GET the billing
/// credits endpoint and combine the PAYG token pool, the account credits and
/// the subscription cycle into one quota status.
/// </summary>
public sealed class ApertisQuotaChecker : IQuotaChecker
{
    private const string ProviderType = "apertis";

    /// <summary>Port of ApertisDefaultBaseURL (types.go:66).</summary>
    internal const string DefaultBaseUrl = "https://api.apertis.ai";

    internal const string QuotaPath = "/v1/dashboard/billing/credits";

    /// <summary>The shared availability group of every Apertis limit (apertis_checker.go:67).</summary>
    internal const string AvailabilityGroup = "apertis_capacity";

    private readonly IExecutor _executor;
    private readonly TimeProvider _timeProvider;

    public ApertisQuotaChecker(IExecutor executor, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _executor = executor;
        _timeProvider = timeProvider;
    }

    /// <summary>Port of (*ApertisQuotaChecker).CheckQuota (apertis_checker.go:77-120).</summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var apiKey = ResolveApiKey(channel);
        if (apiKey.Length == 0)
        {
            // The reference returns an unknown row with a nil error, so this
            // branch is persisted as an ordinary quota result.
            return Task.FromResult(new QuotaData
            {
                Status = ProviderQuotaStatuses.Unknown,
                ProviderType = ProviderType,
                Ready = false,
                RawData = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["error"] = "missing API key",
                },
            });
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");

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

        HttpResponseData response;
        try
        {
            response = _executor.Do(cancellationToken, request);
        }
        catch (HttpClientError ex)
        {
            // Go's http.Client returns >=400 responses without an error; the
            // reference's explicit status check turns them into this message.
            throw InvalidStatusError(ex.StatusCode);
        }

        if (response.StatusCode != 200)
        {
            throw InvalidStatusError(response.StatusCode);
        }

        return Task.FromResult(ParseResponse(response.Body ?? [], _timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// Port of (*ApertisQuotaChecker).SupportsChannel (apertis_checker.go:160-162):
    /// Apertis channels are OpenAI-compatible, with no URL check.
    /// </summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Type == ChannelOutboundFactory.OpenAiChannelType
            || channel.Type == ChannelOutboundFactory.OpenaiResponsesChannelType;
    }

    /// <summary>Port of the API-key extraction at the top of CheckQuota (apertis_checker.go:78-81).</summary>
    internal static string ResolveApiKey(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var apiKey = GoStrings.TrimSpace(ChannelCredentials.GetLegacyApiKey(channel.Credentials));
        if (apiKey.Length == 0)
        {
            var apiKeys = ChannelCredentials.GetApiKeys(channel.Credentials);
            if (apiKeys.Count > 0)
            {
                // The reference takes the first entry verbatim (no trim).
                apiKey = apiKeys[0];
            }
        }

        return apiKey;
    }

    /// <summary>Port of buildApertisQuotaURL (apertis_checker.go:165-182).</summary>
    internal static string BuildQuotaUrl(string? baseUrl)
    {
        var schemeHost = GoStrings.TrimSpace(baseUrl ?? string.Empty);
        if (schemeHost.Length == 0)
        {
            schemeHost = DefaultBaseUrl;
        }

        if (!GoUrl.TryParse(schemeHost, out var scheme, out var host, out _))
        {
            return DefaultBaseUrl + QuotaPath;
        }

        // Unlike the NeuralWatt helper there is no empty-host fallback and no
        // http→https upgrade; only an empty scheme defaults to https.
        if (scheme.Length == 0)
        {
            scheme = "https";
        }

        return scheme + "://" + host + QuotaPath;
    }

    /// <summary>Port of parseResponse (apertis_checker.go:123-157).</summary>
    /// <exception cref="JsonException">
    /// The payload is not a JSON object or a field has the wrong JSON type
    /// (C211; the decoder's message text follows Go where the port constructs it).
    /// </exception>
    internal static QuotaData ParseResponse(byte[] body, DateTimeOffset now)
    {
        var response = ParseBillingCreditsResponse(body);

        var quotaData = new QuotaData
        {
            ProviderType = ProviderType,
            RawData = ConvertResponseToMap(response),
        };

        var status = DetermineStatus(response);
        quotaData.Status = status;
        quotaData.Ready = ProviderQuotaStatuses.IsReadyStatus(status);

        // Only subscribers carry an authoritative cycle boundary, and the
        // reference ignores parse failures without an error.
        DateTimeOffset? nextResetAt = null;
        if (response.IsSubscriber
            && response.Subscription is { } resetSubscription
            && resetSubscription.CycleEnd.Length != 0
            && GoTimeParsing.TryParseRfc3339(resetSubscription.CycleEnd) is { } resetTime)
        {
            nextResetAt = resetTime;
            quotaData.NextResetAt = resetTime;
        }

        quotaData.Limits = BuildLimits(response, nextResetAt);

        return ProviderQuotaData.Normalize(quotaData, now);
    }

    /// <summary>Port of determineApertisStatus (apertis_checker.go:192-228).</summary>
    internal static string DetermineStatus(ApertisBillingCreditsResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var bestStatus = ProviderQuotaStatuses.Unknown;

        if (response.Subscription is { } subscription)
        {
            bestStatus = BetterStatus(bestStatus, DetermineSubscriptionStatus(subscription));
        }

        if (response.Payg is { } payg)
        {
            var shouldCheckPayg = !response.IsSubscriber;
            if (response.Subscription is { } paygSubscription)
            {
                if (paygSubscription.PaygFallbackEnabled)
                {
                    shouldCheckPayg = true;
                }

                if (IsSuspendedOrCancelled(paygSubscription.Status))
                {
                    shouldCheckPayg = true;
                }

                if (paygSubscription.CycleQuotaRemaining <= 0)
                {
                    shouldCheckPayg = true;
                }
            }

            if (shouldCheckPayg)
            {
                bestStatus = BetterStatus(bestStatus, DeterminePaygStatus(payg));
            }
        }

        return bestStatus;
    }

    /// <summary>Port of determineSubscriptionStatus (apertis_checker.go:231-249).</summary>
    internal static string DetermineSubscriptionStatus(ApertisSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        if (IsSuspendedOrCancelled(subscription.Status))
        {
            return ProviderQuotaStatuses.Exhausted;
        }

        if (subscription.CycleQuotaLimit <= 0)
        {
            return ProviderQuotaStatuses.Unknown;
        }

        var usageRatio = (double)subscription.CycleQuotaUsed / subscription.CycleQuotaLimit;
        if (subscription.CycleQuotaRemaining <= 0)
        {
            return ProviderQuotaStatuses.Exhausted;
        }

        if (usageRatio >= ProviderQuotaData.WarningThresholdRatio)
        {
            return ProviderQuotaStatuses.Warning;
        }

        return ProviderQuotaStatuses.Available;
    }

    /// <summary>Port of determinePaygStatus (apertis_checker.go:252-279).</summary>
    internal static string DeterminePaygStatus(ApertisPayg payg)
    {
        ArgumentNullException.ThrowIfNull(payg);

        if (payg.TokenIsUnlimited)
        {
            return ProviderQuotaStatuses.Available;
        }

        if (payg.AccountCredits > 0)
        {
            // A positive balance keeps PAYG available even when the token pool
            // is exhausted; only a non-exhausted pool can report a warning.
            if (TryToFloat64(payg.TokenTotal, out var total) && total > 0)
            {
                var usageRatio = payg.TokenUsed / total;
                if (usageRatio >= 1)
                {
                    return ProviderQuotaStatuses.Available;
                }

                if (usageRatio >= ProviderQuotaData.WarningThresholdRatio)
                {
                    return ProviderQuotaStatuses.Warning;
                }
            }

            return ProviderQuotaStatuses.Available;
        }

        if (!TryToFloat64(payg.TokenTotal, out var paygTotal) || paygTotal <= 0)
        {
            return ProviderQuotaStatuses.Unknown;
        }

        return ProviderQuotaStatuses.Exhausted;
    }

    /// <summary>Port of betterStatus (apertis_checker.go:283-288).</summary>
    internal static string BetterStatus(string first, string second)
        => BetterStatusRank(second) > BetterStatusRank(first) ? second : first;

    private static int BetterStatusRank(string status) => status switch
    {
        ProviderQuotaStatuses.Available => 3,
        ProviderQuotaStatuses.Warning => 2,
        ProviderQuotaStatuses.Exhausted => 1,
        _ => 0,
    };

    private static bool IsSuspendedOrCancelled(string status)
        => string.Equals(status, "suspended", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);

    /// <summary>Port of buildApertisLimits (apertis_checker.go:290-385).</summary>
    internal static List<QuotaLimitStatus> BuildLimits(
        ApertisBillingCreditsResponse response,
        DateTimeOffset? nextResetAt)
    {
        ArgumentNullException.ThrowIfNull(response);

        var limits = new List<QuotaLimitStatus>();

        if (response.Payg is { } payg)
        {
            // Skip the PAYG limit only for an exactly "active" subscription
            // with remaining quota and no fallback (case-sensitive in Go).
            var shouldSkipPayg = response.IsSubscriber
                && response.Subscription is { } skipSubscription
                && !skipSubscription.PaygFallbackEnabled
                && skipSubscription.Status == "active"
                && skipSubscription.CycleQuotaRemaining > 0;

            if (!shouldSkipPayg)
            {
                string tokenStatus;
                double usageRatio;
                if (payg.TokenIsUnlimited)
                {
                    tokenStatus = ProviderQuotaStatuses.Available;
                    usageRatio = 0;
                }
                else if (TryToFloat64(payg.TokenTotal, out var total) && total > 0)
                {
                    usageRatio = payg.TokenUsed / total;
                    if (payg.TokenUsed >= total)
                    {
                        tokenStatus = ProviderQuotaStatuses.Exhausted;
                    }
                    else if (usageRatio >= ProviderQuotaData.WarningThresholdRatio)
                    {
                        tokenStatus = ProviderQuotaStatuses.Warning;
                    }
                    else
                    {
                        tokenStatus = ProviderQuotaStatuses.Available;
                    }
                }
                else
                {
                    tokenStatus = ProviderQuotaStatuses.Unknown;
                    usageRatio = 0;
                }

                var tokenLimit = ProviderQuotaData.NewTokenLimitStatus(
                    tokenStatus,
                    usageRatio,
                    nextResetAt);
                tokenLimit.AvailabilityGroup = AvailabilityGroup;
                tokenLimit.Window = QuotaWindows.PayAsYouGo;
                limits.Add(tokenLimit);

                if (payg.AccountCredits > 0)
                {
                    limits.Add(new QuotaLimitStatus
                    {
                        Type = QuotaLimitTypes.Token,
                        Status = ProviderQuotaStatuses.Available,
                        UsageRatio = 0,
                        Ready = true,
                        AvailabilityGroup = AvailabilityGroup,
                        Window = QuotaWindows.Credits,
                    });
                }
            }
        }

        if (response.IsSubscriber && response.Subscription is { } subscription)
        {
            string subscriptionStatus;
            var usageRatio = 0.0;

            if (IsSuspendedOrCancelled(subscription.Status))
            {
                subscriptionStatus = ProviderQuotaStatuses.Exhausted;
            }
            else if (subscription.CycleQuotaLimit > 0)
            {
                usageRatio = (double)subscription.CycleQuotaUsed / subscription.CycleQuotaLimit;
                if (subscription.CycleQuotaRemaining <= 0)
                {
                    subscriptionStatus = ProviderQuotaStatuses.Exhausted;
                }
                else if (usageRatio >= ProviderQuotaData.WarningThresholdRatio)
                {
                    subscriptionStatus = ProviderQuotaStatuses.Warning;
                }
                else
                {
                    subscriptionStatus = ProviderQuotaStatuses.Available;
                }
            }
            else
            {
                subscriptionStatus = ProviderQuotaStatuses.Unknown;
            }

            limits.Add(new QuotaLimitStatus
            {
                Type = QuotaLimitTypes.SubscriptionCycle,
                Status = subscriptionStatus,
                UsageRatio = usageRatio,
                Ready = ProviderQuotaStatuses.IsReadyStatus(subscriptionStatus),
                NextResetAt = nextResetAt,
                AvailabilityGroup = AvailabilityGroup,
                Window = QuotaWindows.Cycle,
                PeriodStart = ParseCycleStart(subscription.CycleStart),
            });
        }

        return limits;
    }

    /// <summary>Port of parseApertisCycleStart (apertis_checker.go:389-400).</summary>
    internal static DateTimeOffset? ParseCycleStart(string? cycleStart)
    {
        if (string.IsNullOrEmpty(cycleStart))
        {
            return null;
        }

        return GoTimeParsing.TryParseRfc3339(cycleStart);
    }

    /// <summary>Port of convertApertisResponseToMap (apertis_checker.go:403-449).</summary>
    internal static Dictionary<string, object?> ConvertResponseToMap(
        ApertisBillingCreditsResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var rawData = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["is_subscriber"] = response.IsSubscriber,
        };

        if (response.Payg is { } payg)
        {
            var paygMap = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["account_credits"] = payg.AccountCredits,
                ["token_used"] = payg.TokenUsed,
                ["token_total"] = payg.TokenTotal,
                ["token_remaining"] = payg.TokenRemaining,
                ["token_is_unlimited"] = payg.TokenIsUnlimited,
            };
            if (payg.TokenMonthlyLimitUsd is { } monthlyLimitUsd)
            {
                paygMap["token_monthly_limit_usd"] = monthlyLimitUsd;
            }

            if (payg.TokenMonthlyUsedUsd is { } monthlyUsedUsd)
            {
                paygMap["token_monthly_used_usd"] = monthlyUsedUsd;
            }

            if (payg.MonthlyResetDay is { } monthlyResetDay)
            {
                paygMap["monthly_reset_day"] = monthlyResetDay;
            }

            rawData["payg"] = paygMap;
        }

        if (response.Subscription is { } subscription)
        {
            var subscriptionMap = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["plan_type"] = subscription.PlanType,
                ["status"] = subscription.Status,
                ["cycle_quota_limit"] = subscription.CycleQuotaLimit,
                ["cycle_quota_used"] = subscription.CycleQuotaUsed,
                ["cycle_quota_remaining"] = subscription.CycleQuotaRemaining,
                ["cycle_start"] = subscription.CycleStart,
                ["cycle_end"] = subscription.CycleEnd,
                ["payg_fallback_enabled"] = subscription.PaygFallbackEnabled,
            };
            if (subscription.PaygSpentUsd is { } paygSpentUsd)
            {
                subscriptionMap["payg_spent_usd"] = paygSpentUsd;
            }

            if (subscription.PaygLimitUsd is { } paygLimitUsd)
            {
                subscriptionMap["payg_limit_usd"] = paygLimitUsd;
            }

            rawData["subscription"] = subscriptionMap;
        }

        return rawData;
    }

    /// <summary>Port of toFloat64 (apertis_checker.go:452-476).</summary>
    internal static bool TryToFloat64(object? value, out double result)
    {
        switch (value)
        {
            case double doubleValue:
                result = doubleValue;
                return true;
            case float floatValue:
                result = floatValue;
                return true;
            case int intValue:
                result = intValue;
                return true;
            case long longValue:
                result = longValue;
                return true;
            case string stringValue:
                // The reference treats the literal "unlimited" as non-numeric
                // and otherwise runs fmt.Sscanf("%f").
                if (string.Equals(stringValue, "unlimited", StringComparison.Ordinal))
                {
                    result = 0;
                    return false;
                }

                return TryScanFloat(stringValue, out result);
            default:
                result = 0;
                return false;
        }
    }

    /// <summary>
    /// Approximation of <c>fmt.Sscanf(s, "%f", &amp;f)</c>: Go skips leading
    /// space, scans the floating-point token exactly like
    /// <c>fmt.floatToken</c> (which never backtracks over a malformed prefix)
    /// and parses it with <c>strconv.ParseFloat</c>. Only the value matters
    /// here, so the match count is ignored.
    /// </summary>
    internal static bool TryScanFloat(string input, out double value)
    {
        value = 0;
        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        var index = 0;
        while (index < input.Length && char.IsWhiteSpace(input[index]))
        {
            index++;
        }

        var start = index;

        // NaN, spellings accepted in any case.
        var probe = index;
        if (AcceptChar(input, ref probe, 'n', 'N')
            && AcceptChar(input, ref probe, 'a', 'A')
            && AcceptChar(input, ref probe, 'n', 'N'))
        {
            return GoCast.TryParseFloat(input[start..probe], out value);
        }

        // A leading sign is part of every numeric token, Inf included.
        probe = index;
        AcceptChar(input, ref probe, '+', '-');

        var digits = "0123456789_";
        var exponent = "eE";

        if (AcceptChar(input, ref probe, 'i', 'I')
            && AcceptChar(input, ref probe, 'n', 'N')
            && AcceptChar(input, ref probe, 'f', 'F'))
        {
            return GoCast.TryParseFloat(input[start..probe], out value);
        }

        if (AcceptChar(input, ref probe, '0', '0') && AcceptChar(input, ref probe, 'x', 'X'))
        {
            digits = "0123456789abcdefABCDEF_";
            exponent = "pP";
        }

        while (probe < input.Length && digits.Contains(input[probe]))
        {
            probe++;
        }

        if (probe < input.Length && input[probe] == '.')
        {
            probe++;
            while (probe < input.Length && digits.Contains(input[probe]))
            {
                probe++;
            }
        }

        if (probe < input.Length && exponent.Contains(input[probe]))
        {
            probe++;
            AcceptChar(input, ref probe, '+', '-');
            while (probe < input.Length && "0123456789_".Contains(input[probe]))
            {
                probe++;
            }
        }

        return GoCast.TryParseFloat(input[start..probe], out value);
    }

    private static bool AcceptChar(string input, ref int index, char first, char second)
    {
        if (index < input.Length && (input[index] == first || input[index] == second))
        {
            index++;
            return true;
        }

        return false;
    }

    private static InvalidOperationException InvalidStatusError(int statusCode)
        => new(
            "apertis API returned status "
            + statusCode.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Port of the json.Unmarshal call into ApertisBillingCreditsResponse
    /// (apertis_checker.go:124-132), following Go's encoding/json semantics:
    /// root null is a no-op, duplicate keys merge, null resets pointers but is
    /// a no-op for scalars, and unknown keys are ignored.
    /// </summary>
    internal static ApertisBillingCreditsResponse ParseBillingCreditsResponse(byte[] data)
    {
        // Go reports syntax errors as *json.SyntaxError; System.Text.Json's
        // JsonReaderException is a different type, so the Go scanner runs
        // first and the failure surfaces as a plain JsonException.
        var syntaxError = GoJsonSyntax.Validate(data);
        if (syntaxError is not null)
        {
            throw new JsonException(syntaxError);
        }

        using var document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Null)
        {
            return new ApertisBillingCreditsResponse();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal "
                + GoKindName(root.ValueKind)
                + " into Go value of type provider_quota.ApertisBillingCreditsResponse");
        }

        var response = new ApertisBillingCreditsResponse();
        foreach (var property in root.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "object":
                    response.Object = StringField(
                        property.Value,
                        response.Object,
                        "ApertisBillingCreditsResponse",
                        "object");
                    break;
                case "is_subscriber":
                    response.IsSubscriber = BoolField(
                        property.Value,
                        response.IsSubscriber,
                        "ApertisBillingCreditsResponse",
                        "is_subscriber");
                    break;
                case "payg":
                    response.Payg = ParsePayg(property.Value, response.Payg);
                    break;
                case "subscription":
                    response.Subscription = ParseSubscription(
                        property.Value,
                        response.Subscription);
                    break;
            }
        }

        return response;
    }

    private static ApertisPayg? ParsePayg(JsonElement value, ApertisPayg? existing)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "ApertisBillingCreditsResponse",
                "payg",
                "provider_quota.ApertisPayg");
        }

        var payg = existing ?? new ApertisPayg();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "account_credits":
                    payg.AccountCredits = Float64Field(
                        property.Value,
                        payg.AccountCredits,
                        "ApertisPayg",
                        "payg.account_credits");
                    break;
                case "token_used":
                    payg.TokenUsed = Float64Field(
                        property.Value,
                        payg.TokenUsed,
                        "ApertisPayg",
                        "payg.token_used");
                    break;
                case "token_total":
                    payg.TokenTotal = AnyField(
                        property.Value,
                        "ApertisPayg",
                        "payg.token_total");
                    break;
                case "token_remaining":
                    payg.TokenRemaining = AnyField(
                        property.Value,
                        "ApertisPayg",
                        "payg.token_remaining");
                    break;
                case "token_is_unlimited":
                    payg.TokenIsUnlimited = BoolField(
                        property.Value,
                        payg.TokenIsUnlimited,
                        "ApertisPayg",
                        "payg.token_is_unlimited");
                    break;
                case "token_monthly_limit_usd":
                    payg.TokenMonthlyLimitUsd = NullableFloat64Field(
                        property.Value,
                        "ApertisPayg",
                        "payg.token_monthly_limit_usd");
                    break;
                case "token_monthly_used_usd":
                    payg.TokenMonthlyUsedUsd = NullableFloat64Field(
                        property.Value,
                        "ApertisPayg",
                        "payg.token_monthly_used_usd");
                    break;
                case "monthly_reset_day":
                    payg.MonthlyResetDay = NullableIntField(
                        property.Value,
                        "ApertisPayg",
                        "payg.monthly_reset_day");
                    break;
            }
        }

        return payg;
    }

    private static ApertisSubscription? ParseSubscription(
        JsonElement value,
        ApertisSubscription? existing)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "ApertisBillingCreditsResponse",
                "subscription",
                "provider_quota.ApertisSubscription");
        }

        var subscription = existing ?? new ApertisSubscription();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "plan_type":
                    subscription.PlanType = StringField(
                        property.Value,
                        subscription.PlanType,
                        "ApertisSubscription",
                        "subscription.plan_type");
                    break;
                case "status":
                    subscription.Status = StringField(
                        property.Value,
                        subscription.Status,
                        "ApertisSubscription",
                        "subscription.status");
                    break;
                case "cycle_quota_limit":
                    subscription.CycleQuotaLimit = IntField(
                        property.Value,
                        subscription.CycleQuotaLimit,
                        "ApertisSubscription",
                        "subscription.cycle_quota_limit");
                    break;
                case "cycle_quota_used":
                    subscription.CycleQuotaUsed = IntField(
                        property.Value,
                        subscription.CycleQuotaUsed,
                        "ApertisSubscription",
                        "subscription.cycle_quota_used");
                    break;
                case "cycle_quota_remaining":
                    subscription.CycleQuotaRemaining = IntField(
                        property.Value,
                        subscription.CycleQuotaRemaining,
                        "ApertisSubscription",
                        "subscription.cycle_quota_remaining");
                    break;
                case "cycle_start":
                    subscription.CycleStart = StringField(
                        property.Value,
                        subscription.CycleStart,
                        "ApertisSubscription",
                        "subscription.cycle_start");
                    break;
                case "cycle_end":
                    subscription.CycleEnd = StringField(
                        property.Value,
                        subscription.CycleEnd,
                        "ApertisSubscription",
                        "subscription.cycle_end");
                    break;
                case "payg_fallback_enabled":
                    subscription.PaygFallbackEnabled = BoolField(
                        property.Value,
                        subscription.PaygFallbackEnabled,
                        "ApertisSubscription",
                        "subscription.payg_fallback_enabled");
                    break;
                case "payg_spent_usd":
                    subscription.PaygSpentUsd = NullableFloat64Field(
                        property.Value,
                        "ApertisSubscription",
                        "subscription.payg_spent_usd");
                    break;
                case "payg_limit_usd":
                    subscription.PaygLimitUsd = NullableFloat64Field(
                        property.Value,
                        "ApertisSubscription",
                        "subscription.payg_limit_usd");
                    break;
            }
        }

        return subscription;
    }

    private static string StringField(
        JsonElement value,
        string existing,
        string structName,
        string fieldPath)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return existing;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw StructTypeMismatch(GoKindName(value.ValueKind), structName, fieldPath, "string");
        }

        return value.GetString() ?? string.Empty;
    }

    private static bool BoolField(
        JsonElement value,
        bool existing,
        string structName,
        string fieldPath)
        => value.ValueKind switch
        {
            JsonValueKind.Null => existing,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                structName,
                fieldPath,
                "bool"),
        };

    private static double Float64Field(
        JsonElement value,
        double existing,
        string structName,
        string fieldPath)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return existing;
        }

        return NumberAsFloat64(value, structName, fieldPath);
    }

    private static double? NullableFloat64Field(
        JsonElement value,
        string structName,
        string fieldPath)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return NumberAsFloat64(value, structName, fieldPath);
    }

    private static double NumberAsFloat64(JsonElement value, string structName, string fieldPath)
    {
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

    private static long IntField(
        JsonElement value,
        long existing,
        string structName,
        string fieldPath)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return existing;
        }

        return NumberAsInt64(value, structName, fieldPath);
    }

    private static long? NullableIntField(JsonElement value, string structName, string fieldPath)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return NumberAsInt64(value, structName, fieldPath);
    }

    private static long NumberAsInt64(JsonElement value, string structName, string fieldPath)
    {
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw StructTypeMismatch(GoKindName(value.ValueKind), structName, fieldPath, "int");
        }

        var raw = value.GetRawText();
        if (!GoCast.TryParseInt64(raw, out var parsed))
        {
            throw StructTypeMismatch("number " + raw, structName, fieldPath, "int");
        }

        return parsed;
    }

    /// <summary>
    /// Decodes an <c>any</c> field the way encoding/json decodes into
    /// interface{}: numbers become float64, objects become maps, arrays
    /// become slices, and null becomes nil.
    /// </summary>
    private static object? AnyField(JsonElement value, string structName, string fieldPath)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.String:
                return value.GetString() ?? string.Empty;
            case JsonValueKind.Number:
                return NumberAsFloat64(value, structName, fieldPath);
            case JsonValueKind.Array:
                var items = new List<object?>();
                foreach (var item in value.EnumerateArray())
                {
                    items.Add(AnyField(item, structName, fieldPath));
                }

                return items;
            case JsonValueKind.Object:
                var map = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                {
                    map[property.Name] = AnyField(property.Value, structName, fieldPath);
                }

                return map;
            default:
                return null;
        }
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
}

/// <summary>
/// Port of <c>ApertisBillingCreditsResponse</c> (apertis_checker.go:27-32).
/// Scalar fields are Go non-pointer values: a JSON null is a no-op, and the
/// nested objects keep Go's "null resets the pointer, objects merge" semantics.
/// </summary>
public sealed class ApertisBillingCreditsResponse
{
    public string Object { get; set; } = string.Empty;

    public bool IsSubscriber { get; set; }

    public ApertisPayg? Payg { get; set; }

    public ApertisSubscription? Subscription { get; set; }
}

/// <summary>Port of <c>ApertisPayg</c> (apertis_checker.go:37-46).</summary>
public sealed class ApertisPayg
{
    public double AccountCredits { get; set; }

    public double TokenUsed { get; set; }

    /// <summary>float64 or the string "unlimited"; decoded as Go's <c>any</c>.</summary>
    public object? TokenTotal { get; set; }

    /// <summary>float64 or the string "unlimited"; decoded as Go's <c>any</c>.</summary>
    public object? TokenRemaining { get; set; }

    public bool TokenIsUnlimited { get; set; }

    public double? TokenMonthlyLimitUsd { get; set; }

    public double? TokenMonthlyUsedUsd { get; set; }

    /// <summary>Go <c>int</c>; the port uses long (64-bit on both runtimes).</summary>
    public long? MonthlyResetDay { get; set; }
}

/// <summary>Port of <c>ApertisSubscription</c> (apertis_checker.go:49-60).</summary>
public sealed class ApertisSubscription
{
    public string PlanType { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    /// <summary>Go <c>int</c>; the port uses long (64-bit on both runtimes).</summary>
    public long CycleQuotaLimit { get; set; }

    /// <summary>Go <c>int</c>; the port uses long (64-bit on both runtimes).</summary>
    public long CycleQuotaUsed { get; set; }

    /// <summary>Go <c>int</c>; the port uses long (64-bit on both runtimes).</summary>
    public long CycleQuotaRemaining { get; set; }

    public string CycleStart { get; set; } = string.Empty;

    public string CycleEnd { get; set; } = string.Empty;

    public bool PaygFallbackEnabled { get; set; }

    public double? PaygSpentUsd { get; set; }

    public double? PaygLimitUsd { get; set; }
}
