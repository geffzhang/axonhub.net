using System.Globalization;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.OpenAI.Copilot;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the GitHub Copilot quota checker
/// (internal/server/biz/provider_quota/github_copilot_checker.go). The checker
/// reads the Copilot user payload once and derives a limit for every reported
/// quota window: the numeric <c>limited_user_quotas</c> (Free accounts, paired
/// with <c>monthly_quotas</c>) and the percentage-based <c>quota_snapshots</c>
/// (EDU/Premium accounts). The provider does not report a fixed window length,
/// so limits carry the window label only and no period start.
/// </summary>
public sealed class GithubCopilotQuotaChecker : IQuotaChecker
{
    private const string ProviderType = "github_copilot";

    /// <summary>Port of the Copilot user endpoint in fetchUserPayload
    /// (github_copilot_checker.go:160).</summary>
    internal const string UserUrl = "https://api.github.com/copilot_internal/user";

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;

    public GithubCopilotQuotaChecker(
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

    /// <summary>Port of (*GithubCopilotQuotaChecker).CheckQuota (github_copilot_checker.go:40-71).</summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var accessToken = GetAccessToken(channel);

        // provider_quota.go:46-48: a channel proxy replaces the shared client.
        var executor = ResolveExecutor(channel);
        var payload = FetchUserPayload(executor, accessToken, cancellationToken);

        var (status, _) = CalculateStatus(payload);

        // Copilot quotas reset monthly on the account's billing date, but the
        // provider does not report a fixed window length for deriving a
        // PeriodStart (github_copilot_checker.go:58-60).
        var resetAt = ParseResetDate(payload);
        var limits = BuildLimits(payload, resetAt);

        return Task.FromResult(ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = status,
                ProviderType = ProviderType,
                RawData = PrepareRawData(payload),
                NextResetAt = resetAt,
                Ready = ProviderQuotaStatuses.IsReadyStatus(status),
                Limits = limits,
            },
            _timeProvider.GetUtcNow()));
    }

    /// <summary>Port of (*GithubCopilotQuotaChecker).SupportsChannel (github_copilot_checker.go:312-314).</summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Type == ChannelOutboundFactory.GithubCopilotChannelType;
    }

    /// <summary>Port of getAccessToken (github_copilot_checker.go:134-155).</summary>
    internal static string GetAccessToken(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var oauthJson = ChannelCredentials.GetOAuthObjectJson(channel.Credentials);
        var apiKey = ChannelCredentials.GetLegacyApiKey(channel.Credentials);

        if (oauthJson is null && GoStrings.TrimSpace(apiKey).Length == 0)
        {
            throw new InvalidOperationException("channel has no credentials");
        }

        if (oauthJson is not null)
        {
            // The reference returns the typed token verbatim; an empty token is
            // not rejected here, so the request still goes out as "token ".
            return OAuthCredentialsJson.ParseOAuthObject(oauthJson).AccessToken;
        }

        // Usually GitHub Copilot channels have the oauth token stored either
        // directly or as oauth json; the apiKey itself is the last resort.
        OAuthCredentials? credentials = null;
        try
        {
            credentials = OAuthCredentialsJson.ParseCredentialsJson(apiKey);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            // err != nil -> fall through to the raw apiKey.
        }

        if (credentials is not null && credentials.AccessToken.Length != 0)
        {
            return credentials.AccessToken;
        }

        var token = GoStrings.TrimSpace(apiKey);
        if (token.Length == 0)
        {
            throw new InvalidOperationException("GitHub access token is missing");
        }

        return token;
    }

    /// <summary>Port of fetchUserPayload (github_copilot_checker.go:157-180).</summary>
    internal static CopilotUserPayload FetchUserPayload(
        IExecutor executor,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Authorization", "token " + accessToken);
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");
        CopilotOutboundTransformer.SetCopilotHeaders(headers);

        var request = new HttpRequestData
        {
            Method = "GET",
            Url = UserUrl,
            Headers = headers,
        };

        HttpResponseData response;
        try
        {
            response = executor.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "failed to fetch copilot user info: ");
        }

        // The reference rejects everything outside [200, 300); the executor
        // already throws for 4xx/5xx, so this fires for 1xx/3xx.
        if (response.StatusCode < 200 || response.StatusCode >= 300)
        {
            throw new InvalidOperationException(
                "failed to fetch copilot user info, status: "
                + response.StatusCode.ToString(CultureInfo.InvariantCulture));
        }

        try
        {
            return ParseUserPayload(response.Body ?? []);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw GoErrors.Wrap(ex, "failed to parse copilot user response: ");
        }
    }

    /// <summary>Port of the json.Unmarshal call into copilotUserPayload (github_copilot_checker.go:175-178).</summary>
    /// <exception cref="JsonException">
    /// The payload is not a JSON object or a field has the wrong JSON type
    /// (the decoder's message text differs from Go — C093).
    /// </exception>
    internal static CopilotUserPayload ParseUserPayload(byte[] data)
    {
        using var document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        var root = document.RootElement;

        // Go: unmarshaling JSON null into a struct is a no-op.
        if (root.ValueKind == JsonValueKind.Null)
        {
            return new CopilotUserPayload();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal "
                + GoKindName(root.ValueKind)
                + " into Go value of type provider_quota.copilotUserPayload");
        }

        var payload = new CopilotUserPayload();
        foreach (var property in root.EnumerateObject())
        {
            // Go matches a key to its struct field exactly first and
            // case-insensitively second; both spellings reach the same field,
            // and a repeated key keeps the last value.
            switch (FoldAscii(property.Name))
            {
                case "copilot_plan":
                    payload.CopilotPlan = NullableString(property.Value, "copilot_plan");
                    break;
                case "access_type_sku":
                    payload.AccessTypeSku = NullableString(property.Value, "access_type_sku");
                    break;
                case "limited_user_reset_date":
                    payload.LimitedUserResetDate = NullableString(
                        property.Value,
                        "limited_user_reset_date");
                    break;
                case "quota_reset_date_utc":
                    payload.QuotaResetDateUtc = NullableString(property.Value, "quota_reset_date_utc");
                    break;
                case "quota_reset_date":
                    payload.QuotaResetDate = NullableString(property.Value, "quota_reset_date");
                    break;
                case "limited_user_quotas":
                    payload.LimitedUserQuotas = NullableMap(
                        property.Value,
                        payload.LimitedUserQuotas,
                        "limited_user_quotas");
                    break;
                case "monthly_quotas":
                    payload.MonthlyQuotas = NullableMap(
                        property.Value,
                        payload.MonthlyQuotas,
                        "monthly_quotas");
                    break;
                case "quota_snapshots":
                    payload.QuotaSnapshots = NullableMap(
                        property.Value,
                        payload.QuotaSnapshots,
                        "quota_snapshots");
                    break;
            }
        }

        return payload;
    }

    /// <summary>Port of buildLimits (github_copilot_checker.go:73-117).</summary>
    internal static List<QuotaLimitStatus> BuildLimits(
        CopilotUserPayload payload,
        DateTimeOffset? resetAt)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var limits = new List<QuotaLimitStatus>(
            (payload.LimitedUserQuotas?.Count ?? 0) + (payload.QuotaSnapshots?.Count ?? 0));

        if (payload.LimitedUserQuotas is { } limitedUserQuotas)
        {
            var keys = new List<string>(limitedUserQuotas.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var (remaining, remainingOk) = GetNumber(limitedUserQuotas[key]);
                if (!remainingOk || !IsFinite(remaining) || remaining < 0)
                {
                    continue;
                }

                var (total, totalOk) = payload.MonthlyQuotas is { } monthlyQuotas
                    && monthlyQuotas.TryGetValue(key, out var monthlyValue)
                    ? GetNumber(monthlyValue)
                    : (0, false);
                if (!totalOk || !IsFinite(total) || total < 0)
                {
                    total = remaining;
                }

                if (total == 0 && remaining != 0)
                {
                    continue;
                }

                var ratio = 0.0;
                if (total > 0)
                {
                    ratio = (total - remaining) / total;
                }

                limits.Add(CopilotLimit(key, ratio, resetAt));
            }
        }

        if (payload.QuotaSnapshots is { } quotaSnapshots)
        {
            var keys = new List<string>(quotaSnapshots.Keys);
            keys.Sort(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                if (quotaSnapshots[key] is not Dictionary<string, object?> snapshot)
                {
                    continue;
                }

                // Go: snapshot["unlimited"] == true, so only a bool true skips.
                if (snapshot.TryGetValue("unlimited", out var unlimited) && unlimited is true)
                {
                    continue;
                }

                var (remaining, remainingOk) = GetNumber(snapshot.GetValueOrDefault("percent_remaining"));
                if (!remainingOk || !IsFinite(remaining) || remaining < 0 || remaining > 100)
                {
                    continue;
                }

                limits.Add(CopilotLimit(key, 1 - (remaining / 100), resetAt));
            }
        }

        return limits;
    }

    /// <summary>Port of copilotLimit (github_copilot_checker.go:119-128).</summary>
    internal static QuotaLimitStatus CopilotLimit(
        string window,
        double ratio,
        DateTimeOffset? resetAt)
    {
        var status = ProviderQuotaStatuses.Available;
        if (ratio >= 1)
        {
            status = ProviderQuotaStatuses.Exhausted;
        }
        else if (ratio >= ProviderQuotaData.WarningThresholdRatio)
        {
            status = ProviderQuotaStatuses.Warning;
        }

        // A zero window length only sets the label; PeriodStart stays nil.
        return ProviderQuotaData.WithWindow(
            ProviderQuotaData.NewTokenLimitStatus(status, ratio, resetAt),
            window,
            TimeSpan.Zero);
    }

    /// <summary>Port of calculateStatus (github_copilot_checker.go:245-291).</summary>
    internal static (string Status, double LowestPercentage) CalculateStatus(CopilotUserPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var lowestPercentage = 100.0;

        // 1. Limited quotas (Free accounts).
        if (payload.LimitedUserQuotas is { } limitedUserQuotas)
        {
            foreach (var (key, remainingValue) in limitedUserQuotas)
            {
                var (remaining, remainingOk) = GetNumber(remainingValue);
                if (!remainingOk)
                {
                    continue;
                }

                var total = remaining;
                if (payload.MonthlyQuotas is { } monthlyQuotas
                    && monthlyQuotas.TryGetValue(key, out var monthlyValue))
                {
                    var (monthlyTotal, monthlyOk) = GetNumber(monthlyValue);
                    if (monthlyOk && monthlyTotal > 0)
                    {
                        total = monthlyTotal;
                    }
                }

                if (total > 0)
                {
                    var percentage = remaining / total * 100;
                    if (percentage < lowestPercentage)
                    {
                        lowestPercentage = percentage;
                    }
                }
            }
        }

        // 2. Quota snapshots (EDU/Premium accounts).
        if (payload.QuotaSnapshots is { } quotaSnapshots)
        {
            foreach (var snapshotValue in quotaSnapshots.Values)
            {
                if (snapshotValue is not Dictionary<string, object?> snapshot)
                {
                    continue;
                }

                if (snapshot.TryGetValue("unlimited", out var unlimited) && unlimited is true)
                {
                    continue;
                }

                var (percentage, percentageOk) = GetNumber(
                    snapshot.GetValueOrDefault("percent_remaining"));
                if (percentageOk && percentage < lowestPercentage)
                {
                    lowestPercentage = percentage;
                }
            }
        }

        var status = ProviderQuotaStatuses.Available;
        if (lowestPercentage <= 0)
        {
            status = ProviderQuotaStatuses.Exhausted;
        }
        else if (lowestPercentage < 20)
        {
            status = ProviderQuotaStatuses.Warning;
        }

        return (status, lowestPercentage);
    }

    /// <summary>Port of parseResetDate (github_copilot_checker.go:293-310).</summary>
    internal static DateTimeOffset? ParseResetDate(CopilotUserPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (payload.QuotaResetDateUtc is { Length: > 0 } resetDateUtc
            && GoTimeParsing.TryParseRfc3339(resetDateUtc) is { } parsedUtc)
        {
            return parsedUtc;
        }

        if (payload.QuotaResetDate is { Length: > 0 } resetDate
            && TryParseGoDate(resetDate) is { } parsedDate)
        {
            return parsedDate;
        }

        if (payload.LimitedUserResetDate is { Length: > 0 } limitedResetDate
            && TryParseGoDate(limitedResetDate) is { } parsedLimited)
        {
            return parsedLimited;
        }

        return null;
    }

    /// <summary>Port of prepareRawData (github_copilot_checker.go:201-230).</summary>
    internal static Dictionary<string, object?> PrepareRawData(CopilotUserPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // The reference stores the *string, so a missing plan is emitted as
        // JSON null rather than omitted.
        var rawData = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["copilot_plan"] = payload.CopilotPlan,
        };

        if (payload.AccessTypeSku is not null)
        {
            rawData["access_type_sku"] = payload.AccessTypeSku;
            var plan = MapPlanType(payload.AccessTypeSku);
            if (plan.Length != 0)
            {
                rawData["plan_type"] = plan;
            }
        }

        if (payload.QuotaResetDateUtc is not null)
        {
            rawData["quota_reset_date_utc"] = payload.QuotaResetDateUtc;
        }

        if (payload.LimitedUserResetDate is not null)
        {
            rawData["limited_user_reset_date"] = payload.LimitedUserResetDate;
        }

        if (payload.QuotaResetDate is not null)
        {
            rawData["quota_reset_date"] = payload.QuotaResetDate;
        }

        if (payload.LimitedUserQuotas is not null)
        {
            rawData["limited_user_quotas"] = payload.LimitedUserQuotas;
        }

        if (payload.MonthlyQuotas is not null)
        {
            rawData["total_quotas"] = payload.MonthlyQuotas;
        }

        if (payload.QuotaSnapshots is not null)
        {
            rawData["quota_snapshots"] = payload.QuotaSnapshots;
        }

        return rawData;
    }

    /// <summary>Port of mapPlanType (github_copilot_checker.go:182-199).</summary>
    internal static string MapPlanType(string sku) => sku switch
    {
        "copilot_free" or "free_limited_copilot" => "Free",
        "copilot_pro" => "Pro",
        "copilot_pro_plus" => "Pro+",
        "copilot_business" => "Business",
        "copilot_enterprise" => "Enterprise",
        "free_educational_quota" => "Edu",
        _ => string.Empty,
    };

    /// <summary>Port of getNumber (github_copilot_checker.go:232-243).</summary>
    internal static (double Value, bool Ok) GetNumber(object? value) => value switch
    {
        double number => (number, true),
        int number => (number, true),
        long number => (number, true),
        _ => (0, false),
    };

    /// <summary>Port of isFiniteCopilotNumber (github_copilot_checker.go:130-132).</summary>
    internal static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static string? NullableString(JsonElement value, string field) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString() ?? string.Empty,
        _ => throw new JsonException(
            "json: cannot unmarshal "
            + GoKindName(value.ValueKind)
            + " into Go struct field copilotUserPayload."
            + field
            + " of type string"),
    };

    private static Dictionary<string, object?>? NullableMap(
        JsonElement value,
        Dictionary<string, object?>? existing,
        string field)
    {
        // Go sets a map field to nil on JSON null and reuses a non-nil map for
        // a repeated key, keeping the entries it already holds.
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal "
                + GoKindName(value.ValueKind)
                + " into Go struct field copilotUserPayload."
                + field
                + " of type map[string]interface {}");
        }

        var map = existing ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            map[property.Name] = ToGoValue(property.Value);
        }

        return map;
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

    /// <summary>Port of time.Parse("2006-01-02", value).</summary>
    private static DateTimeOffset? TryParseGoDate(string value)
    {
        if (!DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return null;
        }

        // time.Parse yields UTC midnight for a date-only layout.
        return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
    }

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
/// Port of <c>copilotUserPayload</c> (github_copilot_checker.go:29-38). The
/// map fields hold Go-style decoded values (<c>Dictionary&lt;string, object?&gt;</c>,
/// <c>List&lt;object?&gt;</c>, <c>double</c>, <c>string</c>, <c>bool</c> or null).
/// </summary>
internal sealed class CopilotUserPayload
{
    public string? CopilotPlan { get; set; }

    public string? AccessTypeSku { get; set; }

    public Dictionary<string, object?>? LimitedUserQuotas { get; set; }

    public Dictionary<string, object?>? MonthlyQuotas { get; set; }

    public Dictionary<string, object?>? QuotaSnapshots { get; set; }

    public string? LimitedUserResetDate { get; set; }

    public string? QuotaResetDateUtc { get; set; }

    public string? QuotaResetDate { get; set; }
}
