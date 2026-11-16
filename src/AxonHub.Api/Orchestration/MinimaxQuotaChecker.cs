using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the MiniMax quota checker
/// (internal/server/biz/provider_quota/minimax_checker.go): the channel's base
/// URL determines the token-plan endpoint, whose payload reports per-model
/// interval (5h) and weekly windows as remaining percentages plus optional
/// boost permille. Only the first <c>general</c> model is converted into
/// limits; the raw row keeps the normalized percentages.
/// </summary>
public sealed class MinimaxQuotaChecker : IQuotaChecker
{
    private const string ProviderType = "minimax";

    /// <summary>minimaxDefaultBaseURL (minimax_checker.go:16).</summary>
    internal const string DefaultBaseUrl = "https://www.minimaxi.com";

    internal const string UsagePath = "/v1/token_plan/remains";

    private const string RequestFailedPrefix = "minimax quota request failed: ";

    private const string ParseResponseErrorPrefix = "failed to parse minimax quota response: ";

    private const string NoModelRemainsMessage = "minimax quota response contains no model remains";

    private const string NoGeneralModelMessage = "minimax quota response contains no general model";

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;

    public MinimaxQuotaChecker(
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

    /// <summary>
    /// Port of (*MinimaxQuotaChecker).CheckQuota (minimax_checker.go:69-96).
    /// </summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var apiKey = ResolveApiKey(channel);
        if (apiKey.Length == 0)
        {
            throw new InvalidOperationException("channel has no API key");
        }

        // The reference sets Content-Type only; there is no Accept header.
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");

        var request = new HttpRequestData
        {
            Method = "GET",
            Url = BuildUsageUrl(channel.BaseUrl),
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
            throw GoErrors.Wrap(ex, RequestFailedPrefix);
        }

        // The reference performs no explicit status check: its HTTP client
        // already reports >=400 as an error, and anything below that is parsed.
        return Task.FromResult(ParseResponse(response.Body ?? [], _timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// Port of (*MinimaxQuotaChecker).SupportsChannel (minimax_checker.go:98-100).
    /// </summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Type is ChannelOutboundFactory.MinimaxChannelType
            or ChannelOutboundFactory.MinimaxAnthropicChannelType;
    }

    /// <summary>
    /// Port of the credential lookup at minimax_checker.go:70-76: the legacy
    /// <c>apiKey</c> first, then the first element of <c>apiKeys</c>; both are
    /// trimmed and only a non-empty result is accepted.
    /// </summary>
    internal static string ResolveApiKey(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var apiKey = GoStrings.TrimSpace(ChannelCredentials.GetLegacyApiKey(channel.Credentials));
        if (apiKey.Length == 0)
        {
            var apiKeys = ChannelCredentials.GetApiKeys(channel.Credentials);
            if (apiKeys.Count > 0)
            {
                apiKey = GoStrings.TrimSpace(apiKeys[0]);
            }
        }

        return apiKey;
    }

    /// <summary>
    /// Port of buildMinimaxQuotaURL (minimax_checker.go:102-114): the parsed
    /// scheme and host are kept (userinfo, path, query and fragment are
    /// dropped) and the token-plan path is appended; invalid input falls back
    /// to the default endpoint.
    /// </summary>
    internal static string BuildUsageUrl(string? baseUrl)
    {
        var trimmed = GoStrings.TrimSpace(baseUrl ?? string.Empty);
        if (trimmed.Length == 0)
        {
            return DefaultBaseUrl + UsagePath;
        }

        if (!GoUrl.TryParse(trimmed, out var scheme, out var host, out _)
            || scheme.Length == 0
            || host.Length == 0)
        {
            return DefaultBaseUrl + UsagePath;
        }

        return scheme + "://" + host + UsagePath;
    }

    /// <summary>
    /// Port of minimaxPeriodStart (minimax_checker.go:119-127): the API reports
    /// the epoch-millisecond window start directly, so no window length is
    /// assumed. Non-positive values produce no period start.
    /// </summary>
    internal static DateTimeOffset? PeriodStart(long startMillis)
        => startMillis <= 0 ? null : FromUnixMilliseconds(startMillis);

    /// <summary>Port of minimaxTotalPercent (minimax_checker.go:131-136).</summary>
    internal static double TotalPercent(long boostPermille)
        => boostPermille > 0 ? boostPermille / 10.0 : 100.0;

    /// <summary>
    /// Port of minimaxStatusForRatio (minimax_checker.go:151-165): API status 3
    /// is exhausted regardless of the ratio; otherwise the ratio thresholds
    /// apply, status 1 falls back to available and anything else is unknown.
    /// </summary>
    internal static string StatusForRatio(long apiStatus, double ratio)
    {
        if (apiStatus == 3)
        {
            return ProviderQuotaStatuses.Exhausted;
        }

        if (ratio >= 1.0)
        {
            return ProviderQuotaStatuses.Exhausted;
        }

        if (ratio >= ProviderQuotaData.WarningThresholdRatio)
        {
            return ProviderQuotaStatuses.Warning;
        }

        if (apiStatus == 1)
        {
            return ProviderQuotaStatuses.Available;
        }

        return ProviderQuotaStatuses.Unknown;
    }

    /// <summary>Port of worseStatus (minimax_checker.go:304-310).</summary>
    internal static string WorseStatus(string current, string candidate)
        => CheckerStatusRank(candidate) > CheckerStatusRank(current) ? candidate : current;

    /// <summary>
    /// The reference's local rank map (minimax_checker.go:305): available 0,
    /// warning 1, exhausted 2, anything else 0. Distinct from
    /// <see cref="ProviderQuotaData.StatusRank"/>, whose ranks start at 1.
    /// </summary>
    internal static int CheckerStatusRank(string? status) => status switch
    {
        ProviderQuotaStatuses.Exhausted => 2,
        ProviderQuotaStatuses.Warning => 1,
        ProviderQuotaStatuses.Available => 0,
        _ => 0,
    };

    /// <summary>
    /// Port of parseMinimaxResponse (minimax_checker.go:167-302). <paramref name="now"/>
    /// stands in for the reference's normalization clock.
    /// </summary>
    internal static QuotaData ParseResponse(byte[] body, DateTimeOffset now)
    {
        MinimaxResponse response;
        try
        {
            response = ParseUsageResponse(body);
        }
        catch (JsonException ex)
        {
            throw GoErrors.Wrap(ex, ParseResponseErrorPrefix);
        }

        if (response.BaseResp.StatusCode != 0)
        {
            throw new InvalidOperationException(
                "minimax API error: " + response.BaseResp.StatusMsg
                + " (code " + Invariant(response.BaseResp.StatusCode) + ")");
        }

        var remains = response.ModelRemains;
        if (remains is null || remains.Count == 0)
        {
            throw new InvalidOperationException(NoModelRemainsMessage);
        }

        // The reference scans for the first entry whose name is exactly
        // "general" and ignores every other model.
        MinimaxModelRemain? general = null;
        foreach (var item in remains)
        {
            if (item.ModelName == "general")
            {
                general = item;
                break;
            }
        }

        if (general is null)
        {
            throw new InvalidOperationException(NoGeneralModelMessage);
        }

        var overallStatus = ProviderQuotaStatuses.Available;
        var limits = new List<QuotaLimitStatus>();
        DateTimeOffset? nextResetAt = null;
        var rows = new List<MinimaxModelRow>(remains.Count);

        // Interval (5h) window: used = 100 - remaining; bar = used / total;
        // ratio = bar / 100.
        var intervalTotalPercent = TotalPercent(general.CurrentIntervalBoostPermille);
        var intervalUsedPercent = 100.0 - general.CurrentIntervalRemainingPercent;
        var intervalBarPercent = intervalUsedPercent / intervalTotalPercent * 100.0;
        var intervalRatio = intervalBarPercent / 100.0;
        var intervalStatus = StatusForRatio(general.CurrentIntervalStatus, intervalRatio);

        DateTimeOffset? intervalResetAt = null;
        if (general.EndTime > 0)
        {
            intervalResetAt = FromUnixMilliseconds(general.EndTime);
            nextResetAt = EarliestReset(nextResetAt, intervalResetAt);
        }

        limits.Add(new QuotaLimitStatus
        {
            Type = QuotaLimitTypes.Token,
            Status = intervalStatus,
            UsageRatio = intervalRatio,
            Ready = ProviderQuotaStatuses.IsReadyStatus(intervalStatus),
            NextResetAt = intervalResetAt,
            Window = QuotaWindows.FiveHours,
            PeriodStart = PeriodStart(general.StartTime),
        });

        overallStatus = WorseStatus(overallStatus, intervalStatus);

        // Weekly window: only when status == 1; status 2/3 means the plan has
        // no weekly limit.
        var weeklyTotalPercent = 0.0;
        var weeklyUsedPercent = 0.0;
        var weeklyBarPercent = 0.0;
        var weeklyStatus = string.Empty;
        string? weeklyResetAtText = null;

        if (general.CurrentWeeklyStatus == 1)
        {
            weeklyTotalPercent = TotalPercent(general.WeeklyBoostPermille);
            weeklyUsedPercent = 100.0 - general.CurrentWeeklyRemainingPercent;
            weeklyBarPercent = weeklyUsedPercent / weeklyTotalPercent * 100.0;
            var weeklyRatio = weeklyBarPercent / 100.0;
            weeklyStatus = StatusForRatio(general.CurrentWeeklyStatus, weeklyRatio);

            DateTimeOffset? weeklyResetAt = null;
            if (general.WeeklyEndTime > 0)
            {
                weeklyResetAt = FromUnixMilliseconds(general.WeeklyEndTime);
                nextResetAt = EarliestReset(nextResetAt, weeklyResetAt);
            }

            limits.Add(new QuotaLimitStatus
            {
                Type = QuotaLimitTypes.Token,
                Status = weeklyStatus,
                UsageRatio = weeklyRatio,
                Ready = ProviderQuotaStatuses.IsReadyStatus(weeklyStatus),
                NextResetAt = weeklyResetAt,
                Window = QuotaWindows.Weekly,
                PeriodStart = PeriodStart(general.WeeklyStartTime),
            });

            overallStatus = WorseStatus(overallStatus, weeklyStatus);

            if (weeklyResetAt is { } weeklyReset)
            {
                weeklyResetAtText = FormatRfc3339(weeklyReset);
            }
        }

        var intervalResetAtText = intervalResetAt is { } intervalReset
            ? FormatRfc3339(intervalReset)
            : null;

        rows.Add(new MinimaxModelRow
        {
            ModelName = general.ModelName,
            IntervalUsedPercent = intervalUsedPercent,
            IntervalTotalPercent = intervalTotalPercent,
            IntervalPercent = intervalBarPercent,
            IntervalStatus = intervalStatus,
            IntervalResetAt = intervalResetAtText,
            WeeklyUsedPercent = weeklyUsedPercent,
            WeeklyTotalPercent = weeklyTotalPercent,
            WeeklyPercent = weeklyBarPercent,
            WeeklyStatus = weeklyStatus,
            WeeklyResetAt = weeklyResetAtText,
            WeeklyBoostPermille = general.WeeklyBoostPermille,
        });

        var rawData = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rows"] = rows,
        };

        return ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = overallStatus,
                ProviderType = ProviderType,
                RawData = rawData,
                NextResetAt = nextResetAt,
                Ready = ProviderQuotaStatuses.IsReadyStatus(overallStatus),
                Limits = limits,
            },
            now);
    }

    /// <summary>
    /// Port of the <c>json.Unmarshal(body, &amp;response)</c> call at
    /// minimax_checker.go:169, including Go's struct/slice merge semantics for
    /// duplicate keys and its strict integer-literal rules.
    /// </summary>
    /// <exception cref="JsonException">
    /// The payload is not valid JSON or a field has the wrong JSON type; the
    /// message follows Go's UnmarshalTypeError text, while malformed JSON keeps
    /// the System.Text.Json wording (C003).
    /// </exception>
    internal static MinimaxResponse ParseUsageResponse(byte[] data)
    {
        using var document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        var root = document.RootElement;

        // Go: unmarshaling JSON null into a struct is a no-op.
        if (root.ValueKind == JsonValueKind.Null)
        {
            return new MinimaxResponse();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoValueName(root)
                + " into Go value of type provider_quota.minimaxResponse");
        }

        var response = new MinimaxResponse();
        foreach (var property in root.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "model_remains":
                    response.ModelRemains = DecodeModelRemains(property.Value, response.ModelRemains);
                    break;
                case "base_resp":
                    DecodeBaseResp(property.Value, response.BaseResp);
                    break;
            }
        }

        return response;
    }

    /// <summary>
    /// Go decodes a repeated slice field by reusing the backing array: existing
    /// element structs merge field by field, extra elements are truncated,
    /// missing ones appended; JSON null resets the slice to nil.
    /// </summary>
    private static List<MinimaxModelRemain>? DecodeModelRemains(
        JsonElement value,
        List<MinimaxModelRemain>? current)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoValueName(value)
                + " into Go struct field minimaxResponse.model_remains"
                + " of type []provider_quota.minimaxModelRemain");
        }

        var result = current ?? [];
        var index = 0;
        foreach (var element in value.EnumerateArray())
        {
            if (index >= result.Count)
            {
                result.Add(new MinimaxModelRemain());
            }

            var target = result[index];
            if (element.ValueKind == JsonValueKind.Object)
            {
                DecodeModelRemain(element, target);
            }
            else if (element.ValueKind != JsonValueKind.Null)
            {
                throw UnmarshalError(
                    "json: cannot unmarshal " + GoValueName(element)
                    + " into Go struct field minimaxResponse.model_remains"
                    + " of type provider_quota.minimaxModelRemain");
            }

            index++;
        }

        if (result.Count > index)
        {
            result.RemoveRange(index, result.Count - index);
        }

        return result;
    }

    private static void DecodeModelRemain(JsonElement element, MinimaxModelRemain target)
    {
        foreach (var property in element.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "model_name":
                    DecodeString(
                        property.Value, "minimaxModelRemain", "model_remains.model_name",
                        value => target.ModelName = value);
                    break;
                case "start_time":
                    DecodeInteger(
                        property.Value, "minimaxModelRemain", "model_remains.start_time", "int64",
                        value => target.StartTime = value);
                    break;
                case "end_time":
                    DecodeInteger(
                        property.Value, "minimaxModelRemain", "model_remains.end_time", "int64",
                        value => target.EndTime = value);
                    break;
                case "remains_time":
                    DecodeInteger(
                        property.Value, "minimaxModelRemain", "model_remains.remains_time", "int64",
                        value => target.RemainsTime = value);
                    break;
                case "current_interval_status":
                    DecodeInteger(
                        property.Value, "minimaxModelRemain", "model_remains.current_interval_status", "int",
                        value => target.CurrentIntervalStatus = value);
                    break;
                case "current_interval_remaining_percent":
                    DecodeInteger(
                        property.Value, "minimaxModelRemain",
                        "model_remains.current_interval_remaining_percent", "int",
                        value => target.CurrentIntervalRemainingPercent = value);
                    break;
                case "current_interval_boost_permille":
                    DecodeInteger(
                        property.Value, "minimaxModelRemain",
                        "model_remains.current_interval_boost_permille", "int",
                        value => target.CurrentIntervalBoostPermille = value);
                    break;
                case "weekly_start_time":
                    DecodeInteger(
                        property.Value, "minimaxModelRemain", "model_remains.weekly_start_time", "int64",
                        value => target.WeeklyStartTime = value);
                    break;
                case "weekly_end_time":
                    DecodeInteger(
                        property.Value, "minimaxModelRemain", "model_remains.weekly_end_time", "int64",
                        value => target.WeeklyEndTime = value);
                    break;
                case "weekly_remains_time":
                    DecodeInteger(
                        property.Value, "minimaxModelRemain", "model_remains.weekly_remains_time", "int64",
                        value => target.WeeklyRemainsTime = value);
                    break;
                case "current_weekly_status":
                    DecodeInteger(
                        property.Value, "minimaxModelRemain", "model_remains.current_weekly_status", "int",
                        value => target.CurrentWeeklyStatus = value);
                    break;
                case "current_weekly_remaining_percent":
                    DecodeInteger(
                        property.Value, "minimaxModelRemain",
                        "model_remains.current_weekly_remaining_percent", "int",
                        value => target.CurrentWeeklyRemainingPercent = value);
                    break;
                case "weekly_boost_permille":
                    DecodeInteger(
                        property.Value, "minimaxModelRemain", "model_remains.weekly_boost_permille", "int",
                        value => target.WeeklyBoostPermille = value);
                    break;
            }
        }
    }

    /// <summary>
    /// <c>base_resp</c> is a struct value, so JSON null is a no-op and repeated
    /// keys merge field by field into the same value.
    /// </summary>
    private static void DecodeBaseResp(JsonElement value, MinimaxBaseResp target)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoValueName(value)
                + " into Go struct field minimaxResponse.base_resp"
                + " of type provider_quota.minimaxBaseResp");
        }

        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "status_code":
                    DecodeInteger(
                        property.Value, "minimaxBaseResp", "base_resp.status_code", "int",
                        value => target.StatusCode = value);
                    break;
                case "status_msg":
                    DecodeString(
                        property.Value, "minimaxBaseResp", "base_resp.status_msg",
                        value => target.StatusMsg = value);
                    break;
            }
        }
    }

    /// <summary>
    /// Go's decoder for an integer field: JSON null is a no-op, and a number
    /// must be an integer literal that fits in int64 (fractions, exponents and
    /// numeric strings are type errors).
    /// </summary>
    private static void DecodeInteger(
        JsonElement value,
        string structName,
        string field,
        string goType,
        Action<long> set)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !long.TryParse(
                value.GetRawText(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoValueName(value)
                + " into Go struct field " + structName + "." + field
                + " of type " + goType);
        }

        set(parsed);
    }

    /// <summary>Go's decoder for a string field: JSON null is a no-op.</summary>
    private static void DecodeString(
        JsonElement value,
        string structName,
        string field,
        Action<string> set)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoValueName(value)
                + " into Go struct field " + structName + "." + field
                + " of type string");
        }

        set(value.GetString() ?? string.Empty);
    }

    /// <summary>Go's <see cref="System.Text.Json"/>-independent value description.</summary>
    private static string GoValueName(JsonElement value)
        => value.ValueKind == JsonValueKind.Number
            ? "number " + value.GetRawText()
            : GoKindName(value.ValueKind);

    private static JsonException UnmarshalError(string message) => new(message);

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

    /// <summary>
    /// Port of <c>time.UnixMilli(...).Format(time.RFC3339)</c>: seconds
    /// precision, with the port's UTC instant written as <c>Z</c> (C216).
    /// </summary>
    internal static string FormatRfc3339(DateTimeOffset value)
    {
        var text = value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return value.Offset == TimeSpan.Zero
            ? text + "Z"
            : text + value.ToString("zzz", CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? EarliestReset(DateTimeOffset? current, DateTimeOffset? candidate)
    {
        if (candidate is { } reset && (current is null || reset < current))
        {
            return reset;
        }

        return current;
    }

    private static DateTimeOffset? FromUnixMilliseconds(long milliseconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Go's time.Time spans years far outside DateTimeOffset's range;
            // the port has no representable value (C123 / C216).
            return null;
        }
    }

    private static string Invariant(long value)
        => value.ToString(CultureInfo.InvariantCulture);

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

/// <summary>Port of minimaxResponse (minimax_checker.go:19-22).</summary>
internal sealed class MinimaxResponse
{
    public List<MinimaxModelRemain>? ModelRemains { get; set; }

    public MinimaxBaseResp BaseResp { get; set; } = new();
}

/// <summary>Port of minimaxBaseResp (minimax_checker.go:24-27).</summary>
internal sealed class MinimaxBaseResp
{
    public long StatusCode { get; set; }

    public string StatusMsg { get; set; } = string.Empty;
}

/// <summary>
/// Port of minimaxModelRemain (minimax_checker.go:29-43). The reference's
/// <c>int</c> fields are 64-bit on amd64, so the port carries them as
/// <see cref="long"/>; the decoder still reports Go's <c>int</c> type name.
/// </summary>
internal sealed class MinimaxModelRemain
{
    public string ModelName { get; set; } = string.Empty;

    public long StartTime { get; set; }

    public long EndTime { get; set; }

    public long RemainsTime { get; set; }

    public long CurrentIntervalStatus { get; set; }

    public long CurrentIntervalRemainingPercent { get; set; }

    public long CurrentIntervalBoostPermille { get; set; }

    public long WeeklyStartTime { get; set; }

    public long WeeklyEndTime { get; set; }

    public long WeeklyRemainsTime { get; set; }

    public long CurrentWeeklyStatus { get; set; }

    public long CurrentWeeklyRemainingPercent { get; set; }

    public long WeeklyBoostPermille { get; set; }
}

/// <summary>Port of minimaxModelRow (minimax_checker.go:45-59).</summary>
internal sealed class MinimaxModelRow
{
    [JsonPropertyName("modelName")]
    [JsonPropertyOrder(0)]
    public string ModelName { get; set; } = string.Empty;

    [JsonPropertyName("intervalUsedPercent")]
    [JsonPropertyOrder(1)]
    [JsonConverter(typeof(GoDoubleConverter))]
    public double IntervalUsedPercent { get; set; }

    [JsonPropertyName("intervalTotalPercent")]
    [JsonPropertyOrder(2)]
    [JsonConverter(typeof(GoDoubleConverter))]
    public double IntervalTotalPercent { get; set; }

    [JsonPropertyName("intervalPercent")]
    [JsonPropertyOrder(3)]
    [JsonConverter(typeof(GoDoubleConverter))]
    public double IntervalPercent { get; set; }

    [JsonPropertyName("intervalStatus")]
    [JsonPropertyOrder(4)]
    public string IntervalStatus { get; set; } = string.Empty;

    [JsonPropertyName("intervalResetAt")]
    [JsonPropertyOrder(5)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IntervalResetAt { get; set; }

    [JsonPropertyName("weeklyUsedPercent")]
    [JsonPropertyOrder(6)]
    [JsonConverter(typeof(GoDoubleConverter))]
    public double WeeklyUsedPercent { get; set; }

    [JsonPropertyName("weeklyTotalPercent")]
    [JsonPropertyOrder(7)]
    [JsonConverter(typeof(GoDoubleConverter))]
    public double WeeklyTotalPercent { get; set; }

    [JsonPropertyName("weeklyPercent")]
    [JsonPropertyOrder(8)]
    [JsonConverter(typeof(GoDoubleConverter))]
    public double WeeklyPercent { get; set; }

    [JsonPropertyName("weeklyStatus")]
    [JsonPropertyOrder(9)]
    public string WeeklyStatus { get; set; } = string.Empty;

    [JsonPropertyName("weeklyResetAt")]
    [JsonPropertyOrder(10)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WeeklyResetAt { get; set; }

    [JsonPropertyName("weeklyBoostPermille")]
    [JsonPropertyOrder(11)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public long WeeklyBoostPermille { get; set; }
}

/// <summary>
/// Writes float64 the way Go's encoding/json does (shortest round-tripping
/// digits, scientific notation outside 1e-6..1e21); the quota rows are
/// serialized back through <c>GoJson</c> and locked byte-for-byte.
/// </summary>
internal sealed class GoDoubleConverter : JsonConverter<double>
{
    public override double Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.Number
            ? reader.GetDouble()
            : throw new JsonException("expected a number");

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        => writer.WriteRawValue(GoJsonFloat.Format(value), skipInputValidation: true);
}
