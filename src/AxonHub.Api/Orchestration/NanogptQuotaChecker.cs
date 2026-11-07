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
/// Port of the NanoGPT quota checker
/// (internal/server/biz/provider_quota/nanogpt_checker.go). The checker asks
/// the subscription usage endpoint derived from the channel base URL and maps
/// the daily-image and daily/weekly token windows onto quota limits. The
/// subscription state is the base status, escalated by the worst window
/// (active &lt; warning &lt; exhausted); an unrecognized state stays unknown
/// unless a window supplies a ready status.
/// </summary>
public sealed class NanogptQuotaChecker : IQuotaChecker
{
    private const string ProviderType = "nanogpt";

    /// <summary>Port of the fallback usage endpoint (nanogpt_checker.go:214,219).</summary>
    internal const string DefaultQuotaUrl = "https://nano-gpt.com/api/subscription/v1/usage";

    /// <summary>Port of the path appended to the channel host (nanogpt_checker.go:227).</summary>
    private const string QuotaPath = "/api/subscription/v1/usage";

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;

    public NanogptQuotaChecker(
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

    /// <summary>Port of (*NanoGPTQuotaChecker).CheckQuota (nanogpt_checker.go:61-94).</summary>
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

        return Task.FromResult(ParseResponse(response.Body ?? [], _timeProvider.GetUtcNow()));
    }

    /// <summary>Port of (*NanoGPTQuotaChecker).SupportsChannel (nanogpt_checker.go:204-206).</summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Type == ChannelOutboundFactory.NanogptChannelType
            || channel.Type == ChannelOutboundFactory.NanogptResponsesChannelType;
    }

    /// <summary>Port of the API-key extraction at the top of CheckQuota (nanogpt_checker.go:62-70).</summary>
    internal static string ResolveApiKey(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // Prefer the trimmed legacy field, then the first apiKeys entry
        // verbatim; the OAuth object is never consulted here.
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

    /// <summary>Port of buildNanoGPTQuotaURL (nanogpt_checker.go:211-228).</summary>
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

    /// <summary>Port of parseResponse (nanogpt_checker.go:96-202).</summary>
    internal static QuotaData ParseResponse(byte[] body, DateTimeOffset now)
    {
        NanogptUsageResponse response;
        try
        {
            response = ParseUsageResponse(body);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw GoErrors.Wrap(ex, "failed to parse nanogpt usage response: ");
        }

        var normalizedStatus = ProviderQuotaStatuses.Unknown;
        switch (response.State)
        {
            case "active":
                normalizedStatus = ProviderQuotaStatuses.Available;
                break;
            case "grace":
                normalizedStatus = ProviderQuotaStatuses.Warning;
                break;
            case "inactive":
                normalizedStatus = ProviderQuotaStatuses.Exhausted;
                break;
        }

        var limits = BuildLimitStatuses(
            response.DailyImages,
            response.DailyInputTokens,
            response.WeeklyInputTokens);

        // Escalate to the worst per-limit status (available < warning <
        // exhausted). Only escalate from available/warning bases so that
        // state-driven statuses (inactive -> exhausted, unrecognized ->
        // unknown) are never overridden.
        if (normalizedStatus == ProviderQuotaStatuses.Available
            || normalizedStatus == ProviderQuotaStatuses.Warning)
        {
            foreach (var limit in limits)
            {
                if (ProviderQuotaData.StatusRank(limit.Status)
                    > ProviderQuotaData.StatusRank(normalizedStatus))
                {
                    normalizedStatus = limit.Status;
                }
            }
        }

        var nextResetAt = FindEarliestResetAt(
            response.DailyImages,
            response.DailyInputTokens,
            response.WeeklyInputTokens);

        // During the grace period windows typically have no resetAt; fall back
        // to graceUntil as the relevant deadline.
        if (nextResetAt is null
            && response.GraceUntil is { } graceUntil
            && GoTimeParsing.TryParseRfc3339(graceUntil) is { } parsedGraceUntil)
        {
            nextResetAt = parsedGraceUntil;
        }

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

    /// <summary>Port of the json.Unmarshal call into NanoGPTUsageResponse (nanogpt_checker.go:99).</summary>
    /// <exception cref="JsonException">
    /// The payload is not a JSON object or a field has the wrong JSON type
    /// (the decoder's message text differs from Go — C003/C093).
    /// </exception>
    internal static NanogptUsageResponse ParseUsageResponse(byte[] data)
    {
        using var document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        var root = document.RootElement;

        // Go: unmarshaling JSON null into a struct is a no-op.
        if (root.ValueKind == JsonValueKind.Null)
        {
            return new NanogptUsageResponse();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal "
                + GoKindName(root.ValueKind)
                + " into Go value of type provider_quota.NanoGPTUsageResponse");
        }

        var response = new NanogptUsageResponse();
        foreach (var property in root.EnumerateObject())
        {
            // Go matches a key to its struct field exactly first and
            // case-insensitively second; both spellings reach the same field,
            // and a repeated key keeps the last value (objects merge into the
            // existing pointer, null resets it).
            switch (FoldAscii(property.Name))
            {
                case "active":
                    response.Active = NullableBool(property.Value, "NanoGPTUsageResponse", "active");
                    break;
                case "provider":
                    response.Provider = NullableString(property.Value, "NanoGPTUsageResponse", "provider");
                    break;
                case "providerstatus":
                    response.ProviderStatus = NullableString(
                        property.Value,
                        "NanoGPTUsageResponse",
                        "providerStatus");
                    break;
                case "limits":
                    response.Limits = ParseLimits(property.Value, response.Limits);
                    break;
                case "allowoverage":
                    response.AllowOverage = NullableBool(
                        property.Value,
                        "NanoGPTUsageResponse",
                        "allowOverage");
                    break;
                case "period":
                    response.Period = ParsePeriod(property.Value, response.Period);
                    break;
                case "dailyimages":
                    response.DailyImages = ParseWindow(
                        property.Value,
                        response.DailyImages,
                        "dailyImages");
                    break;
                case "dailyinputtokens":
                    response.DailyInputTokens = ParseWindow(
                        property.Value,
                        response.DailyInputTokens,
                        "dailyInputTokens");
                    break;
                case "weeklyinputtokens":
                    response.WeeklyInputTokens = ParseWindow(
                        property.Value,
                        response.WeeklyInputTokens,
                        "weeklyInputTokens");
                    break;
                case "state":
                    response.State = NullableString(property.Value, "NanoGPTUsageResponse", "state");
                    break;
                case "graceuntil":
                    response.GraceUntil = NullableString(
                        property.Value,
                        "NanoGPTUsageResponse",
                        "graceUntil");
                    break;
            }
        }

        return response;
    }

    /// <summary>Port of buildNanoGPTLimitStatuses (nanogpt_checker.go:230-281).</summary>
    internal static List<QuotaLimitStatus> BuildLimitStatuses(
        NanogptQuotaWindow? imageWindow,
        NanogptQuotaWindow? dailyTokenWindow,
        NanogptQuotaWindow? weeklyTokenWindow)
    {
        var typed = new (NanogptQuotaWindow? Window, string LimitType, string WindowLabel, TimeSpan WindowLength)[]
        {
            (imageWindow, QuotaLimitTypes.Image, QuotaWindows.Daily, TimeSpan.FromHours(24)),
            (dailyTokenWindow, QuotaLimitTypes.Token, QuotaWindows.Daily, TimeSpan.FromDays(1)),
            (weeklyTokenWindow, QuotaLimitTypes.Token, QuotaWindows.Weekly, TimeSpan.FromDays(7)),
        };

        var limits = new List<QuotaLimitStatus>(3);
        foreach (var (window, limitType, windowLabel, windowLength) in typed)
        {
            if (window is null)
            {
                continue;
            }

            var status = ProviderQuotaStatuses.Available;
            var usageRatio = 0.0;

            if (window.PercentUsed is { } percentUsed)
            {
                usageRatio = percentUsed;
            }

            if (window.Remaining is { } remaining && remaining <= 0)
            {
                status = ProviderQuotaStatuses.Exhausted;
                usageRatio = 1.0;
            }
            else if (usageRatio >= 1.0)
            {
                status = ProviderQuotaStatuses.Exhausted;
            }
            else if (usageRatio >= ProviderQuotaData.WarningThresholdRatio)
            {
                status = ProviderQuotaStatuses.Warning;
            }

            var resetAt = window.ResetAt is { } reset && reset > 0
                ? FromUnixMilliseconds(reset)
                : null;

            limits.Add(ProviderQuotaData.WithWindow(
                new QuotaLimitStatus
                {
                    Type = limitType,
                    Status = status,
                    UsageRatio = usageRatio,
                    Ready = ProviderQuotaStatuses.IsReadyStatus(status),
                    NextResetAt = resetAt,
                },
                windowLabel,
                windowLength));
        }

        return limits;
    }

    /// <summary>Port of findEarliestResetAt (nanogpt_checker.go:285-302).</summary>
    internal static DateTimeOffset? FindEarliestResetAt(params NanogptQuotaWindow?[] windows)
    {
        DateTimeOffset? earliest = null;

        foreach (var window in windows)
        {
            if (window?.ResetAt is not { } reset || reset <= 0)
            {
                continue;
            }

            var resetAt = FromUnixMilliseconds(reset);
            if (resetAt is null)
            {
                // Go's time.Time spans years far outside DateTimeOffset's
                // range; the port has no representable value (C123).
                continue;
            }

            if (earliest is null || resetAt.Value < earliest.Value)
            {
                earliest = resetAt;
            }
        }

        return earliest;
    }

    /// <summary>Port of the rawData construction in parseResponse (nanogpt_checker.go:141-192).</summary>
    internal static Dictionary<string, object?> PrepareRawData(NanogptUsageResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var rawData = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (response.Active is { } active)
        {
            rawData["active"] = active;
        }

        if (response.Provider is { } provider)
        {
            rawData["provider"] = provider;
        }

        if (response.ProviderStatus is { } providerStatus)
        {
            rawData["providerStatus"] = providerStatus;
        }

        if (response.State is { } state)
        {
            rawData["state"] = state;
        }

        if (response.AllowOverage is { } allowOverage)
        {
            rawData["allowOverage"] = allowOverage;
        }

        if (response.Limits is { } limits)
        {
            rawData["limits"] = ConvertLimitsToMap(limits);
        }

        if (response.Period is { } period)
        {
            rawData["period"] = ConvertPeriodToMap(period);
        }

        var windows = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (response.DailyImages is { } dailyImages)
        {
            windows["dailyImages"] = ConvertWindowToMap(dailyImages);
        }

        if (response.DailyInputTokens is { } dailyInputTokens)
        {
            windows["dailyInputTokens"] = ConvertWindowToMap(dailyInputTokens);
        }

        if (response.WeeklyInputTokens is { } weeklyInputTokens)
        {
            windows["weeklyInputTokens"] = ConvertWindowToMap(weeklyInputTokens);
        }

        if (windows.Count > 0)
        {
            rawData["windows"] = windows;
        }

        if (response.GraceUntil is { } graceUntil)
        {
            rawData["graceUntil"] = graceUntil;
        }

        return rawData;
    }

    /// <summary>Port of convertNanoGPTLimitsToMap (nanogpt_checker.go:304-320).</summary>
    internal static Dictionary<string, object?> ConvertLimitsToMap(NanogptLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (limits.WeeklyInputTokens is { } weeklyInputTokens)
        {
            result["weeklyInputTokens"] = weeklyInputTokens;
        }

        if (limits.DailyInputTokens is { } dailyInputTokens)
        {
            result["dailyInputTokens"] = dailyInputTokens;
        }

        if (limits.DailyImages is { } dailyImages)
        {
            result["dailyImages"] = dailyImages;
        }

        return result;
    }

    /// <summary>Port of convertNanoGPTPeriodToMap (nanogpt_checker.go:322-330).</summary>
    internal static Dictionary<string, object?> ConvertPeriodToMap(NanogptPeriod period)
    {
        ArgumentNullException.ThrowIfNull(period);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (period.CurrentPeriodEnd is { } currentPeriodEnd)
        {
            result["currentPeriodEnd"] = currentPeriodEnd;
        }

        return result;
    }

    /// <summary>Port of convertNanoGPTWindowToMap (nanogpt_checker.go:332-352).</summary>
    internal static Dictionary<string, object?> ConvertWindowToMap(NanogptQuotaWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (window.Used is { } used)
        {
            result["used"] = used;
        }

        if (window.Remaining is { } remaining)
        {
            result["remaining"] = remaining;
        }

        if (window.PercentUsed is { } percentUsed)
        {
            result["percentUsed"] = percentUsed;
        }

        if (window.ResetAt is { } resetAt)
        {
            result["resetAt"] = resetAt;
        }

        return result;
    }

    private static NanogptLimits? ParseLimits(JsonElement value, NanogptLimits? existing)
    {
        // Go sets a pointer field to nil on JSON null and reuses a non-nil
        // object for a repeated key, keeping the fields it already holds.
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "NanoGPTUsageResponse",
                "limits",
                "provider_quota.NanoGPTLimits");
        }

        var limits = existing ?? new NanogptLimits();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "weeklyinputtokens":
                    limits.WeeklyInputTokens = NullableInt64(
                        property.Value,
                        "NanoGPTLimits",
                        "limits.weeklyInputTokens");
                    break;
                case "dailyinputtokens":
                    limits.DailyInputTokens = NullableInt64(
                        property.Value,
                        "NanoGPTLimits",
                        "limits.dailyInputTokens");
                    break;
                case "dailyimages":
                    limits.DailyImages = NullableInt64(
                        property.Value,
                        "NanoGPTLimits",
                        "limits.dailyImages");
                    break;
            }
        }

        return limits;
    }

    private static NanogptPeriod? ParsePeriod(JsonElement value, NanogptPeriod? existing)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "NanoGPTUsageResponse",
                "period",
                "provider_quota.NanoGPTPeriod");
        }

        var period = existing ?? new NanogptPeriod();
        foreach (var property in value.EnumerateObject())
        {
            if (FoldAscii(property.Name) == "currentperiodend")
            {
                period.CurrentPeriodEnd = NullableString(
                    property.Value,
                    "NanoGPTPeriod",
                    "period.currentPeriodEnd");
            }
        }

        return period;
    }

    private static NanogptQuotaWindow? ParseWindow(
        JsonElement value,
        NanogptQuotaWindow? existing,
        string fieldPath)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "NanoGPTUsageResponse",
                fieldPath,
                "provider_quota.NanoGPTQuotaWindow");
        }

        var window = existing ?? new NanogptQuotaWindow();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "used":
                    window.Used = NullableInt64(
                        property.Value,
                        "NanoGPTQuotaWindow",
                        fieldPath + ".used");
                    break;
                case "remaining":
                    window.Remaining = NullableInt64(
                        property.Value,
                        "NanoGPTQuotaWindow",
                        fieldPath + ".remaining");
                    break;
                case "percentused":
                    window.PercentUsed = NullableFloat64(
                        property.Value,
                        "NanoGPTQuotaWindow",
                        fieldPath + ".percentUsed");
                    break;
                case "resetat":
                    window.ResetAt = NullableInt64(
                        property.Value,
                        "NanoGPTQuotaWindow",
                        fieldPath + ".resetAt");
                    break;
            }
        }

        return window;
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

    private static DateTimeOffset? FromUnixMilliseconds(long milliseconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Go's time.Time spans years far outside DateTimeOffset's range;
            // the port has no representable value (C123).
            return null;
        }
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
/// Port of <c>NanoGPTUsageResponse</c> (nanogpt_checker.go:16-29). Every field
/// is optional in the reference, so each maps to a nullable value; the nested
/// objects keep Go's "null resets the pointer, objects merge" semantics.
/// </summary>
internal sealed class NanogptUsageResponse
{
    public bool? Active { get; set; }

    public string? Provider { get; set; }

    public string? ProviderStatus { get; set; }

    public NanogptLimits? Limits { get; set; }

    public bool? AllowOverage { get; set; }

    public NanogptPeriod? Period { get; set; }

    public NanogptQuotaWindow? DailyImages { get; set; }

    public NanogptQuotaWindow? DailyInputTokens { get; set; }

    public NanogptQuotaWindow? WeeklyInputTokens { get; set; }

    public string? State { get; set; }

    public string? GraceUntil { get; set; }
}

/// <summary>Port of <c>NanoGPTLimits</c> (nanogpt_checker.go:31-36).</summary>
internal sealed class NanogptLimits
{
    public long? WeeklyInputTokens { get; set; }

    public long? DailyInputTokens { get; set; }

    public long? DailyImages { get; set; }
}

/// <summary>Port of <c>NanoGPTQuotaWindow</c> (nanogpt_checker.go:38-44).</summary>
internal sealed class NanogptQuotaWindow
{
    public long? Used { get; set; }

    public long? Remaining { get; set; }

    public double? PercentUsed { get; set; }

    public long? ResetAt { get; set; }
}

/// <summary>Port of <c>NanoGPTPeriod</c> (nanogpt_checker.go:46-49).</summary>
internal sealed class NanogptPeriod
{
    public string? CurrentPeriodEnd { get; set; }
}
