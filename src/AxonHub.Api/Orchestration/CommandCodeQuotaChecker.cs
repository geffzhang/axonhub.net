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
/// Port of the Command Code quota checker
/// (internal/server/biz/provider_quota/commandcode_checker.go). Command Code
/// exposes two billing surfaces: <c>/alpha/*</c> authenticates with the account
/// API key (Bearer) and works for every plan, while <c>/internal/billing/*</c>
/// is Studio's browser surface and only accepts the session cookie. The checker
/// prefers the key and degrades to the cookie when the key is rejected.
/// </summary>
public sealed class CommandCodeQuotaChecker : IQuotaChecker
{
    internal const string ProviderType = "commandcode";

    /// <summary>commandCodeAPIBaseURL (commandcode_checker.go:24).</summary>
    internal const string ApiBaseUrl = "https://api.commandcode.ai";

    internal const string AlphaCreditsUrl = ApiBaseUrl + "/alpha/billing/credits";

    internal const string AlphaSubscriptionsUrl = ApiBaseUrl + "/alpha/billing/subscriptions";

    internal const string InternalCreditsUrl = ApiBaseUrl + "/internal/billing/credits";

    internal const string InternalSubscriptionsUrl = ApiBaseUrl + "/internal/billing/subscriptions";

    /// <summary>commandCodeQuotaUserAgent (commandcode_checker.go:31).</summary>
    internal const string QuotaUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>commandCodeMaxResetEpochMillis (commandcode_checker.go:32).</summary>
    internal const double MaxResetEpochMillis = 1e15;

    private const string ParseCreditsErrorPrefix = "parse Command Code credits response: ";

    private const string RequestFailedErrorPrefix = "Command Code billing request failed: ";

    private const string NoCredentialError = "channel has no Command Code API key or quota cookie";

    private const string InvalidCookieErrorPrefix = "invalid Command Code auth cookie: ";

    private static readonly Dictionary<string, PlanAllowance[]> PlanAllowances =
        new(StringComparer.Ordinal)
        {
            ["individual-go"] = [new PlanAllowance(10, 3, 6)],
            ["individual-goat"] = [new PlanAllowance(70, 14, 35)],
            // Legacy Pro reports the $30 allowance the CLI still ships; the
            // re-issued row is listed first, matching the reference order.
            ["individual-pro"] = [new PlanAllowance(80, 16, 40), new PlanAllowance(30, 6, 15)],
            ["individual-pro-v1"] = [new PlanAllowance(80, 16, 40)],
            ["individual-max"] = [new PlanAllowance(150, 45, 90)],
            ["individual-ultra"] = [new PlanAllowance(300, 90, 180)],
        };

    /// <summary>
    /// commandCodeTeamProAllowance (commandcode_checker.go:74): matched by label
    /// because Team Pro has no stable plan id in the subscriptions payload.
    /// </summary>
    private static readonly PlanAllowance TeamProAllowance = new(40, 12, 24);

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        // Go's encoding/json rejects documents nested deeper than 10000.
        MaxDepth = 10_000,
    };

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;

    public CommandCodeQuotaChecker(
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

    /// <summary>Port of (*CommandCodeQuotaChecker).CheckQuota (commandcode_checker.go:143-175).</summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var apiKey = ApiKey(channel);

        var cookie = Cookie(channel, out var cookieError);
        if (apiKey.Length == 0 && cookie.Length == 0)
        {
            if (cookieError is not null)
            {
                throw new InvalidProviderQuotaCredentialsException(
                    InvalidCookieErrorPrefix + cookieError);
            }

            throw new InvalidProviderQuotaCredentialsException(NoCredentialError);
        }

        var executor = ResolveExecutor(channel);

        // Prefer the account API key: it does not expire the way the Studio
        // session cookie does.
        if (apiKey.Length != 0)
        {
            try
            {
                return Task.FromResult(FetchQuota(
                    executor,
                    AlphaCreditsUrl,
                    AlphaSubscriptionsUrl,
                    string.Empty,
                    apiKey,
                    cancellationToken,
                    _timeProvider.GetUtcNow()));
            }
            catch (Exception ex) when (cookie.Length != 0
                && GoErrors.As<InvalidProviderQuotaCredentialsException>(ex) is not null)
            {
                // Keys that only authenticate the chat surface still leave the
                // cookie usable, so degrade to it instead of failing the whole
                // check.
            }
        }

        return Task.FromResult(FetchQuota(
            executor,
            InternalCreditsUrl,
            InternalSubscriptionsUrl,
            cookie,
            string.Empty,
            cancellationToken,
            _timeProvider.GetUtcNow()));
    }

    /// <summary>Port of (*CommandCodeQuotaChecker).SupportsChannel (commandcode_checker.go:135-141).</summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.Type != ChannelOutboundFactory.CommandcodeChannelType
            && channel.Type != ChannelOutboundFactory.CommandcodeAnthropicChannelType)
        {
            return false;
        }

        return HasQuotaCredentials(channel);
    }

    /// <summary>
    /// Port of commandCodeAPIKey (commandcode_checker.go:80-88): the first
    /// non-blank key from GetAllAPIKeys, which keeps the legacy-key ordering and
    /// skips OAuth JSON payloads (C149).
    /// </summary>
    internal static string ApiKey(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        foreach (var candidate in ChannelCredentials.GetAllApiKeys(channel.Credentials))
        {
            var trimmed = GoStrings.TrimSpace(candidate);
            if (trimmed.Length != 0)
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Port of commandCodeCookie (commandcode_checker.go:93-103): the normalized
    /// Studio session cookie, or an empty string when none is configured. A
    /// malformed cookie is reported so the caller can decide whether the API key
    /// covers it.
    /// </summary>
    internal static string Cookie(ChannelRecord channel, out string? error)
    {
        ArgumentNullException.ThrowIfNull(channel);

        error = null;
        var settings = ParseSettingsLenient(channel.Settings);
        var raw = GoStrings.TrimSpace(settings?.ProviderQuota?.CommandCode?.AuthCookie ?? string.Empty);
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        var normalizeError = ChannelValidation.NormalizeCommandCodeCookie(raw, out var normalized);
        if (normalizeError is not null)
        {
            error = normalizeError;
            return string.Empty;
        }

        return normalized;
    }

    /// <summary>
    /// Port of HasCommandCodeQuotaCredentials (commandcode_checker.go:110-121).
    /// Format validation stays in CheckQuota so a malformed credential surfaces
    /// as a quota error instead of the channel being silently skipped.
    /// </summary>
    internal static bool HasQuotaCredentials(ChannelRecord channel)
    {
        if (channel is null)
        {
            return false;
        }

        if (ApiKey(channel).Length != 0)
        {
            return true;
        }

        var cookie = Cookie(channel, out var error);

        return error is not null || cookie.Length != 0;
    }

    /// <summary>Port of commandCodeFetchQuota (commandcode_checker.go:177-192).</summary>
    private static QuotaData FetchQuota(
        IExecutor executor,
        string creditsUrl,
        string subscriptionsUrl,
        string cookie,
        string apiKey,
        CancellationToken cancellationToken,
        DateTimeOffset now)
    {
        var creditsBody = Get(executor, creditsUrl, cookie, apiKey, cancellationToken);

        // The subscription endpoint is optional: a failure only drops plan info
        // (and with it the trusted monthly denominator), never the credits
        // result.
        byte[]? subscriptionsBody = null;
        try
        {
            subscriptionsBody = Get(executor, subscriptionsUrl, cookie, apiKey, cancellationToken);
        }
        catch
        {
            // Fail-open, exactly like the reference's `subscriptionsBody, _ :=`.
        }

        return ParseCredits(creditsBody, subscriptionsBody, now);
    }

    /// <summary>
    /// Port of commandCodeGet (commandcode_checker.go:196-224). The API key is
    /// sent as a Bearer token when supplied; otherwise the session cookie is
    /// forwarded verbatim through the Cookie header.
    /// </summary>
    private static byte[] Get(
        IExecutor executor,
        string url,
        string cookie,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json, text/plain, */*");
        HttpClientUtils.HeaderSet(headers, "Accept-Language", "en-US,en;q=0.9");
        HttpClientUtils.HeaderSet(headers, "User-Agent", QuotaUserAgent);
        HttpClientUtils.HeaderSet(headers, "Origin", "https://commandcode.ai");
        HttpClientUtils.HeaderSet(headers, "Referer", "https://commandcode.ai/");

        var request = new HttpRequestData
        {
            Method = "GET",
            Url = url,
            Headers = headers,
        };

        if (apiKey.Length != 0)
        {
            request.Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            };
        }
        else
        {
            HttpClientUtils.HeaderSet(headers, "Cookie", cookie);
        }

        HttpResponseData response;
        try
        {
            response = executor.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            if (GoErrors.As<HttpClientError>(ex) is { } httpError)
            {
                throw StatusError(httpError.StatusCode, apiKey);
            }

            throw GoErrors.Wrap(ex, RequestFailedErrorPrefix);
        }

        // The shared executor already raises for >=400 (Go's httpclient.Do does
        // the same), so this only sees 1xx/3xx and non-200 2xx.
        if (response.StatusCode < 200 || response.StatusCode >= 300)
        {
            throw StatusError(response.StatusCode, apiKey);
        }

        return response.Body ?? [];
    }

    /// <summary>
    /// Port of commandCodeStatusError (commandcode_checker.go:229-240):
    /// 401/403 mean the credential can no longer be trusted; every other status
    /// keeps the generic message so the standard quota-error backoff applies.
    /// </summary>
    private static Exception StatusError(int statusCode, string apiKey)
    {
        if (statusCode is 401 or 403)
        {
            var credential = apiKey.Length != 0
                ? "invalid API key?"
                : "expired session cookie?";

            return new InvalidProviderQuotaCredentialsException(
                "Command Code billing API returned " + statusCode + " (" + credential + ")");
        }

        return new InvalidOperationException(
            "Command Code billing API returned " + statusCode);
    }

    /// <summary>
    /// Port of parseCommandCodeCredits (commandcode_checker.go:326-478): turns
    /// the credits body (and optional subscriptions body) into normalized
    /// QuotaData. The credits payload is consumed case-insensitively;
    /// windowLimits may be nested at the root or under credits.
    /// </summary>
    internal static QuotaData ParseCredits(
        byte[] creditsBody,
        byte[]? subscriptionsBody,
        DateTimeOffset now)
    {
        Dictionary<string, object?> root;
        try
        {
            root = ParseJsonObject(creditsBody);
        }
        catch (JsonException ex)
        {
            throw GoErrors.Wrap(ex, ParseCreditsErrorPrefix);
        }

        // Credits-scoped map: prefer the nested credits object when present.
        var creditsObj = NestedFold(root, "credits") ?? root;

        // Windows map may sit at the root or under the credits object.
        var windowsObj = NestedFold(root, "windowLimits", "windows", "limits")
            ?? NestedFold(creditsObj, "windowLimits", "windows", "limits");

        var fiveHour = ParseWindow(windowsObj, "five_hour", "5h", "fiveHour");
        var weekly = ParseWindow(windowsObj, "weekly");

        // Wire payload: monthlyCredits / purchasedCredits, kept as camelCase
        // doubles with no unit suffix. Older snake_case names stay accepted for
        // tolerance.
        var (monthlyRemaining, monthlyRemainingOk) = NumFold(
            creditsObj, "monthlyCredits", "monthlyRemainingUsd", "monthly_remaining_usd");
        var (monthlyLimitWire, monthlyLimitWireOk) = NumFold(
            creditsObj, "monthlyLimitUsd", "monthly_limit_usd");
        var (purchased, purchasedOk) = NumFold(
            creditsObj, "purchasedCredits", "purchasedCreditsUsd", "purchased_credits_usd");

        // Optional subscription enrichments (plan identity for the monthly
        // denominator). Failures degrade silently to a balance-only result.
        var planId = string.Empty;
        var planLabel = string.Empty;
        var subscriptionStatus = string.Empty;
        var currentPeriodEnd = string.Empty;
        if (subscriptionsBody is { Length: > 0 })
        {
            try
            {
                var subRoot = ParseJsonObject(subscriptionsBody);
                // Wire payload wraps the subscription object under "data"
                // ({"success":true,"data":{...}}); accept both shapes.
                var subObj = NestedFold(subRoot, "data", "subscription") ?? subRoot;
                planId = StringFold(subObj, "planId", "plan_id");
                planLabel = StringFold(subObj, "planLabel", "plan_label");
                subscriptionStatus = StringFold(subObj, "status", "subscription_status");
                currentPeriodEnd = StringFold(subObj, "currentPeriodEnd", "current_period_end");
            }
            catch (JsonException)
            {
                // Fail-open: only plan info is lost.
            }
        }

        var rawCredits = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (monthlyRemainingOk)
        {
            rawCredits["monthly_remaining_usd"] = monthlyRemaining;
        }

        if (purchasedOk)
        {
            rawCredits["purchased_credits_usd"] = purchased;
        }

        var raw = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["plan_id"] = planId,
            ["plan_label"] = planLabel,
            ["subscription_status"] = subscriptionStatus,
        };
        if (currentPeriodEnd.Length != 0)
        {
            raw["current_period_end"] = currentPeriodEnd;
        }

        if (rawCredits.Count != 0)
        {
            raw["credits"] = rawCredits;
        }

        if (fiveHour is not null || weekly is not null)
        {
            var windowsRaw = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (fiveHour is not null)
            {
                windowsRaw["five_hour"] = fiveHour.ToRaw();
            }

            if (weekly is not null)
            {
                windowsRaw["weekly"] = weekly.ToRaw();
            }

            raw["windows"] = windowsRaw;
        }

        // Trusted monthly denominator: only when the wire 5h/weekly caps select
        // one of the plan's local allowance rows and the remaining balance fits
        // inside that row. Unknown plan / subscription failure / price drift
        // never guess a denominator. The raw wire limit is only exposed as a
        // structured monthly_limit_usd when it is trusted, so the UI never
        // renders an untrusted "$used / $limit" bar.
        var (allowance, allowanceOk) = MatchPlan(planId, planLabel, fiveHour, weekly);
        var trustedMonthly = allowanceOk && monthlyRemainingOk
            && monthlyRemaining <= allowance.MonthlyUsd;
        if (trustedMonthly)
        {
            // Production payload has no monthly limit field; the plan table
            // (matched by planId with wire caps verified above) is the
            // denominator source.
            rawCredits["monthly_limit_usd"] = monthlyLimitWireOk
                ? monthlyLimitWire
                : allowance.MonthlyUsd;
        }

        var overall = ProviderQuotaStatuses.Unknown;
        var nextResetAt = EarliestReset(fiveHour, weekly);

        var limits = new List<QuotaLimitStatus>(3);

        void AddWindow(string window, TimeSpan windowLength, double ratio, DateTimeOffset? resetAt)
        {
            var status = ProviderQuotaStatuses.Available;
            if (ratio >= 1.0)
            {
                status = ProviderQuotaStatuses.Exhausted;
            }
            else if (ratio >= ProviderQuotaData.WarningThresholdRatio)
            {
                status = ProviderQuotaStatuses.Warning;
            }

            if (ProviderQuotaData.StatusRank(status) > ProviderQuotaData.StatusRank(overall))
            {
                overall = status;
            }

            var limit = new QuotaLimitStatus
            {
                Type = QuotaLimitTypes.SubscriptionCycle,
                Status = status,
                UsageRatio = ratio,
                Ready = ProviderQuotaStatuses.IsReadyStatus(status),
                NextResetAt = resetAt,
            };
            limits.Add(ProviderQuotaData.WithWindow(limit, window, windowLength));
        }

        if (fiveHour is not null && weekly is not null)
        {
            AddWindow(QuotaWindows.FiveHours, TimeSpan.FromHours(5), fiveHour.UsageRatio(), fiveHour.ResetAt);
            AddWindow(QuotaWindows.Weekly, TimeSpan.FromDays(7), weekly.UsageRatio(), weekly.ResetAt);
            if (trustedMonthly)
            {
                var used = allowance.MonthlyUsd - monthlyRemaining;
                AddWindow(QuotaWindows.Monthly, TimeSpan.Zero, used / allowance.MonthlyUsd, nextResetAt);
            }
        }
        else
        {
            // No windows: provider is on pay-as-you-go. Available only while any
            // balance remains; top-ups only ever show the numeric balance.
            var hasBalance = (monthlyRemainingOk && monthlyRemaining > 0)
                || (purchasedOk && purchased > 0);
            overall = hasBalance
                ? ProviderQuotaStatuses.Available
                : ProviderQuotaStatuses.Exhausted;
            limits.Add(new QuotaLimitStatus
            {
                Type = QuotaLimitTypes.SubscriptionCycle,
                Status = overall,
                UsageRatio = 0,
                Ready = ProviderQuotaStatuses.IsReadyStatus(overall),
                Window = QuotaWindows.Monthly,
            });
        }

        return ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = overall,
                ProviderType = ProviderType,
                RawData = raw,
                NextResetAt = nextResetAt,
                Ready = ProviderQuotaStatuses.IsReadyStatus(overall),
                Limits = limits,
            },
            now);
    }

    /// <summary>Port of parseCommandCodeWindow (commandcode_checker.go:489-519).</summary>
    private static CommandCodeWindow? ParseWindow(Dictionary<string, object?>? obj, params string[] keys)
    {
        if (obj is null)
        {
            return null;
        }

        var found = FindMapFold(obj, keys);
        if (!found.Ok)
        {
            return null;
        }

        var raw = found.Value!;
        var window = new CommandCodeWindow();
        var (used, usedOk) = NumFold(raw, "used_usd", "usedUsd", "used");
        var (cap, capOk) = NumFold(raw, "cap_usd", "capUsd", "cap", "limitUsd", "limit_usd");
        var (pct, pctOk) = NumFold(raw, "usage_percent", "usagePercent", "percent");
        window.HasUsage = usedOk || pctOk;
        window.HasCap = capOk;
        window.UsedUsd = used;
        if (pctOk)
        {
            window.UsagePct = pct / 100.0;
        }
        else if (usedOk && capOk && cap > 0)
        {
            window.UsagePct = used / cap;
        }

        if (capOk)
        {
            window.CapUsd = cap;
        }

        var reset = FindFoldAny(raw, "reset_time", "resetTime", "resetAt", "reset", "reset_at");
        if (reset.Ok)
        {
            window.ResetAt = ParseReset(reset.Value);
        }

        return window;
    }

    /// <summary>Port of earliestCommandCodeReset (commandcode_checker.go:543-554).</summary>
    private static DateTimeOffset? EarliestReset(params CommandCodeWindow?[] windows)
    {
        DateTimeOffset? earliest = null;
        foreach (var window in windows)
        {
            if (window?.ResetAt is not { } resetAt)
            {
                continue;
            }

            if (earliest is null || resetAt < earliest)
            {
                earliest = resetAt;
            }
        }

        return earliest;
    }

    /// <summary>
    /// Port of parseCommandCodeReset (commandcode_checker.go:558-582): a window
    /// reset value is unix seconds, unix milliseconds, or RFC3339. Values &lt;= 0
    /// or unparsable produce null (no reset).
    /// </summary>
    internal static DateTimeOffset? ParseReset(object? value)
    {
        switch (value)
        {
            case double number:
                return ResetFromEpoch(number);
            case string text:
            {
                var trimmed = GoStrings.TrimSpace(text);
                if (trimmed.Length == 0)
                {
                    return null;
                }

                if (GoCast.TryParseFloat(trimmed, out var epoch))
                {
                    return ResetFromEpoch(epoch);
                }

                return GoTimeParsing.TryParseRfc3339(trimmed);
            }
            default:
                return null;
        }
    }

    /// <summary>Port of commandCodeResetFromEpoch (commandcode_checker.go:584-594).</summary>
    internal static DateTimeOffset? ResetFromEpoch(double epoch)
    {
        if (epoch <= 0 || epoch > MaxResetEpochMillis)
        {
            return null;
        }

        try
        {
            // Go truncates toward zero; the value is positive here.
            return epoch >= 1e12
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)epoch)
                : DateTimeOffset.FromUnixTimeSeconds((long)epoch);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Go's time.Time covers years outside DateTimeOffset's range; such
            // instants are dropped (C123).
            return null;
        }
    }

    /// <summary>
    /// Port of commandCodeMatchPlan (commandcode_checker.go:600-620): a plan id
    /// can have several rows (legacy vs re-issued Pro), so the 5h/weekly caps
    /// reported by the provider pick the row and double as the drift check.
    /// Pay-as-you-go plans report no windows and never match.
    /// </summary>
    private static (PlanAllowance Allowance, bool Ok) MatchPlan(
        string planId,
        string planLabel,
        CommandCodeWindow? fiveHour,
        CommandCodeWindow? weekly)
    {
        if (fiveHour is null || weekly is null)
        {
            return (default, false);
        }

        PlanAllowances.TryGetValue(GoStrings.TrimSpace(planId), out var candidates);
        if (candidates is not { Length: > 0 }
            && string.Equals(
                GoStrings.TrimSpace(planLabel),
                "team pro",
                StringComparison.OrdinalIgnoreCase))
        {
            candidates = [TeamProAllowance];
        }

        if (candidates is not null)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.FiveHourUsd == fiveHour.CapUsd
                    && candidate.WeeklyUsd == weekly.CapUsd)
                {
                    return (candidate, true);
                }
            }
        }

        return (default, false);
    }

    /// <summary>
    /// Port of parseJSONObject (commandcode_checker.go:622-631): Go's
    /// <c>json.Unmarshal</c> into <c>map[string]any</c>. Only the types Go
    /// produces reach the fold helpers below (numbers become doubles).
    /// </summary>
    private static Dictionary<string, object?> ParseJsonObject(byte[] body)
    {
        var syntaxError = GoJsonSyntax.Validate(body);
        if (syntaxError is not null)
        {
            throw new JsonException(syntaxError);
        }

        var rootKind = GoStructJson.RootKind(body);
        if (rootKind == "null")
        {
            // Go leaves the map nil and the reference reports this itself.
            throw new JsonException("empty JSON object");
        }

        if (rootKind is not null)
        {
            throw new JsonException(
                GoStructJson.TypeMismatch(rootKind, "map[string]interface {}"));
        }

        using var document = JsonDocument.Parse(
            GoJsonUtf8.SanitizeStringLiterals(body) ?? body,
            DocumentOptions);

        return ToGoMap(document.RootElement);
    }

    /// <summary>Decodes one value the way Go decodes into <c>any</c>.</summary>
    private static object? ToGoValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ToGoMap(element),
        JsonValueKind.Array => ToGoList(element),
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => ToGoNumber(element),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static Dictionary<string, object?> ToGoMap(JsonElement element)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            // Go's map decode keeps the last duplicate spelling.
            map[property.Name] = ToGoValue(property.Value);
        }

        return map;
    }

    private static List<object?> ToGoList(JsonElement element)
    {
        var list = new List<object?>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            list.Add(ToGoValue(item));
        }

        return list;
    }

    private static double ToGoNumber(JsonElement element)
    {
        var raw = element.GetRawText();
        if (!GoCast.TryParseFloat(raw, out var value))
        {
            // Go: an out-of-range literal is an UnmarshalTypeError naming the
            // literal and the target type.
            throw new JsonException(
                "json: cannot unmarshal number "
                + raw
                + " into Go value of type float64");
        }

        return value;
    }

    /// <summary>
    /// Port of findMapFold (commandcode_checker.go:633-643): the first
    /// fold-equal key wins even when its value is not an object (Go returns
    /// immediately). Duplicate fold-equal spellings are resolved in document
    /// order instead of Go's random map order (C052/C065/C172 family).
    /// </summary>
    private static (Dictionary<string, object?>? Value, bool Ok) FindMapFold(
        Dictionary<string, object?> obj,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var pair in obj)
            {
                if (EqualFoldAscii(pair.Key, key))
                {
                    return (pair.Value as Dictionary<string, object?>, true);
                }
            }
        }

        return (null, false);
    }

    /// <summary>Port of nestedFold (commandcode_checker.go:645-656).</summary>
    private static Dictionary<string, object?>? NestedFold(
        Dictionary<string, object?> obj,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var pair in obj)
            {
                if (EqualFoldAscii(pair.Key, key)
                    && pair.Value is Dictionary<string, object?> map)
                {
                    return map;
                }
            }
        }

        return null;
    }

    /// <summary>Port of findFoldAny (commandcode_checker.go:658-667).</summary>
    private static (object? Value, bool Ok) FindFoldAny(
        Dictionary<string, object?> obj,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var pair in obj)
            {
                if (EqualFoldAscii(pair.Key, key))
                {
                    return (pair.Value, true);
                }
            }
        }

        return (null, false);
    }

    /// <summary>Port of numFold (commandcode_checker.go:669-686).</summary>
    private static (double Value, bool Ok) NumFold(
        Dictionary<string, object?> obj,
        params string[] keys)
    {
        var (value, found) = FindFoldAny(obj, keys);
        if (!found)
        {
            return (0, false);
        }

        switch (value)
        {
            case double number:
                return (number, true);
            case string text:
                return GoCast.TryParseFloat(GoStrings.TrimSpace(text), out var parsed)
                    ? (parsed, true)
                    : (0, false);
            default:
                // json.Number is unreachable: Go only produces it when decoding
                // into interface{} with UseNumber, which the reference does not.
                return (0, false);
        }
    }

    /// <summary>Port of stringFold (commandcode_checker.go:688-703).</summary>
    private static string StringFold(Dictionary<string, object?> obj, params string[] keys)
    {
        var (value, found) = FindFoldAny(obj, keys);
        if (!found)
        {
            return string.Empty;
        }

        return value switch
        {
            string text => text,
            double number => GoCast.GoFloatToString(number),
            _ => string.Empty,
        };
    }

    /// <summary>
    /// The reference folds keys with <c>strings.EqualFold</c>; the literals here
    /// are ASCII, so ASCII folding is the same for every payload a provider
    /// emits (C220).
    /// </summary>
    private static bool EqualFoldAscii(string value, string literal)
    {
        if (value.Length != literal.Length)
        {
            return false;
        }

        for (var index = 0; index < value.Length; index++)
        {
            var left = value[index];
            if (left is >= 'A' and <= 'Z')
            {
                left = (char)(left + ('a' - 'A'));
            }

            var right = literal[index];
            if (right is >= 'A' and <= 'Z')
            {
                right = (char)(right + ('a' - 'A'));
            }

            if (left != right)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Port of (*commandCodeWindow).raw (commandcode_checker.go:531-541). The
    /// reset instant is written as UTC <c>yyyy-MM-dd'T'HH:mm:ss'Z'</c>; Go's
    /// <c>Format(time.RFC3339)</c> uses the local zone and drops subseconds
    /// too (C217 family).
    /// </summary>
    private sealed class CommandCodeWindow
    {
        public double UsedUsd { get; set; }

        public double CapUsd { get; set; }

        public double UsagePct { get; set; }

        public DateTimeOffset? ResetAt { get; set; }

        public bool HasCap { get; set; }

        public bool HasUsage { get; set; }

        /// <summary>Port of (*commandCodeWindow).usageRatio (commandcode_checker.go:521-529).</summary>
        public double UsageRatio()
        {
            if (UsagePct > 0)
            {
                return UsagePct;
            }

            if (HasCap && CapUsd > 0 && HasUsage)
            {
                return UsedUsd / CapUsd;
            }

            return 0;
        }

        public Dictionary<string, object?> ToRaw()
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["usage_percent"] = UsageRatio() * 100,
            };
            if (HasCap)
            {
                map["cap_usd"] = CapUsd;
            }

            map["used_usd"] = UsedUsd;
            if (ResetAt is { } resetAt)
            {
                map["reset_time"] = resetAt.ToUniversalTime().ToString(
                    "yyyy-MM-dd'T'HH:mm:ss'Z'",
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            return map;
        }
    }

    /// <summary>Port of commandCodePlanAllowance (commandcode_checker.go:49-53).</summary>
    private readonly record struct PlanAllowance(double MonthlyUsd, double FiveHourUsd, double WeeklyUsd);

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

    private IExecutor ResolveExecutor(ChannelRecord channel)
        => ParseSettingsLenient(channel.Settings)?.Proxy is { } proxy
            ? _proxyExecutors.Create(ToProxyConfig(proxy))
            : _executor;

    private static ProxyConfig ToProxyConfig(ProxyConfigDraft proxy) => new()
    {
        Type = proxy.Type,
        Url = proxy.Url,
        Username = proxy.Username,
        Password = proxy.Password,
        DisableConnectionReuse = proxy.DisableConnectionReuse ?? false,
    };
}
