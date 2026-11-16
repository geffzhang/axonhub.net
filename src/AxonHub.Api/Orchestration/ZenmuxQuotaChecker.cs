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
/// Port of the ZenMux quota checker
/// (internal/server/biz/provider_quota/zenmux_checker.go). The checker asks the
/// management subscription endpoint with a dedicated management API key and
/// maps the 5-hour and 7-day usage windows onto quota limits. The account
/// status and both windows combine into the overall status, with an
/// unrecognized account status ranking worse than every known status.
/// </summary>
public sealed class ZenmuxQuotaChecker : IQuotaChecker
{
    private const string ProviderType = "zenmux";

    /// <summary>Port of ZenmuxDefaultBaseURL (provider_quota/types.go:68).</summary>
    internal const string DefaultBaseUrl = "https://zenmux.ai";

    /// <summary>Port of zenmuxSubscriptionDetailPath (zenmux_checker.go:16).</summary>
    private const string SubscriptionDetailPath = "/api/v1/management/subscription/detail";

    /// <summary>The fixed request URL (the reference never derives it from the channel).</summary>
    internal const string QuotaUrl = DefaultBaseUrl + SubscriptionDetailPath;

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;

    public ZenmuxQuotaChecker(
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

    /// <summary>Port of (*ZenmuxQuotaChecker).CheckQuota (zenmux_checker.go:62-89).</summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var managementKey = ResolveManagementKey(channel);
        if (managementKey.Length == 0)
        {
            throw new InvalidOperationException("channel has no management API key");
        }

        // The reference builds a bare GET with bearer auth; the shared client
        // supplies the Accept and User-Agent defaults on the wire.
        var request = new HttpRequestData
        {
            Method = "GET",
            Url = QuotaUrl,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = managementKey,
            },
        };

        // provider_quota.go:46-48 and zenmux_checker.go:74-78: a channel proxy
        // replaces the shared client, and the result rejects HTTPS to HTTP
        // redirects.
        var executor = ResolveExecutor(channel);

        HttpResponseData response;
        try
        {
            response = executor.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "zenmux quota request failed: ");
        }

        // Go's client already errors for >=400, so this only sees 1xx/3xx.
        if (response.StatusCode < 200 || response.StatusCode >= 300)
        {
            throw new InvalidOperationException(
                "zenmux quota request returned HTTP "
                + response.StatusCode.ToString(CultureInfo.InvariantCulture));
        }

        return Task.FromResult(ParseResponse(response.Body ?? [], _timeProvider.GetUtcNow()));
    }

    /// <summary>Port of (*ZenmuxQuotaChecker).SupportsChannel (zenmux_checker.go:91-102).</summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Type == ChannelOutboundFactory.ZenmuxChannelType
            || channel.Type == ChannelOutboundFactory.ZenmuxResponsesChannelType
            || channel.Type == ChannelOutboundFactory.ZenmuxAnthropicChannelType
            || channel.Type == ChannelOutboundFactory.ZenmuxGeminiChannelType
            || channel.Type == ChannelOutboundFactory.ZenmuxVideoChannelType;
    }

    /// <summary>
    /// Port of the management-key extraction at the top of CheckQuota
    /// (zenmux_checker.go:63-66): only the dedicated field is consulted, and it
    /// is trimmed.
    /// </summary>
    internal static string ResolveManagementKey(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return GoStrings.TrimSpace(ChannelCredentials.GetManagementApiKey(channel.Credentials));
    }

    /// <summary>Port of parseZenmuxQuotaResponse (zenmux_checker.go:104-160).</summary>
    internal static QuotaData ParseResponse(byte[] body, DateTimeOffset now)
    {
        ZenmuxSubscriptionResponse response;
        try
        {
            response = ParseSubscriptionResponse(body);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw GoErrors.Wrap(ex, "failed to parse zenmux quota response: ");
        }

        if (!response.Success)
        {
            if (response.Message.Length != 0)
            {
                throw new InvalidOperationException("zenmux API error: " + response.Message);
            }

            throw new InvalidOperationException("zenmux API returned success=false");
        }

        if (response.Data is not { } data)
        {
            throw new InvalidOperationException("zenmux quota response contains no data");
        }

        DateTimeOffset? fiveHourReset;
        try
        {
            fiveHourReset = ParseResetAt(data.Quota5Hour.ResetsAt);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "parse zenmux 5-hour reset: ");
        }

        DateTimeOffset? sevenDayReset;
        try
        {
            sevenDayReset = ParseResetAt(data.Quota7Day.ResetsAt);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "parse zenmux 7-day reset: ");
        }

        var fiveHourStatus = WindowStatus(data.Quota5Hour.UsagePercentage);
        var sevenDayStatus = WindowStatus(data.Quota7Day.UsagePercentage);
        var overallStatus = WorstStatus(
            AccountStatus(data.AccountStatus),
            fiveHourStatus,
            sevenDayStatus);

        var nextResetAt = fiveHourReset;
        if (sevenDayReset is { } sevenDay
            && (nextResetAt is null || sevenDay < nextResetAt.Value))
        {
            nextResetAt = sevenDay;
        }

        var limits = new List<QuotaLimitStatus>(2)
        {
            ProviderQuotaData.WithWindow(
                ProviderQuotaData.NewTokenLimitStatus(
                    fiveHourStatus,
                    data.Quota5Hour.UsagePercentage,
                    fiveHourReset),
                QuotaWindows.FiveHours,
                TimeSpan.FromHours(5)),
            ProviderQuotaData.WithWindow(
                ProviderQuotaData.NewTokenLimitStatus(
                    sevenDayStatus,
                    data.Quota7Day.UsagePercentage,
                    sevenDayReset),
                QuotaWindows.SevenDays,
                TimeSpan.FromDays(7)),
        };

        return ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = overallStatus,
                ProviderType = ProviderType,
                RawData = PrepareRawData(data),
                NextResetAt = nextResetAt,
                Ready = ProviderQuotaStatuses.IsReadyStatus(overallStatus),
                Limits = limits,
            },
            now);
    }

    /// <summary>
    /// Port of the json.Unmarshal call into zenmuxSubscriptionResponse
    /// (zenmux_checker.go:106).
    /// </summary>
    /// <exception cref="JsonException">
    /// The payload is not a JSON object or a field has the wrong JSON type.
    /// </exception>
    internal static ZenmuxSubscriptionResponse ParseSubscriptionResponse(byte[] data)
    {
        using var document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        var root = document.RootElement;

        // Go: unmarshaling JSON null into a struct is a no-op.
        if (root.ValueKind == JsonValueKind.Null)
        {
            return new ZenmuxSubscriptionResponse();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal "
                + GoKindName(root.ValueKind)
                + " into Go value of type provider_quota.zenmuxSubscriptionResponse");
        }

        var response = new ZenmuxSubscriptionResponse();
        foreach (var property in root.EnumerateObject())
        {
            // Go matches the JSON tag case-insensitively and lets the last
            // duplicate key win; repeated objects merge into the existing
            // value, null resets a pointer or is a no-op for a value field.
            switch (FoldAscii(property.Name))
            {
                case "success":
                    response.Success = BoolValue(
                        property.Value,
                        "zenmuxSubscriptionResponse",
                        "success",
                        response.Success);
                    break;
                case "message":
                    response.Message = StringValue(
                        property.Value,
                        "zenmuxSubscriptionResponse",
                        "message",
                        response.Message);
                    break;
                case "data":
                    response.Data = ParseData(property.Value, response.Data);
                    break;
            }
        }

        return response;
    }

    /// <summary>Port of parseZenmuxResetAt (zenmux_checker.go:162-172).</summary>
    /// <exception cref="InvalidOperationException">
    /// The value is not a valid RFC3339 timestamp. The message reproduces Go's
    /// <c>time.Parse</c> text for the common failures and reports the port's
    /// range limit for values DateTimeOffset cannot hold (C123/C207).
    /// </exception>
    internal static DateTimeOffset? ParseResetAt(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (GoTimeParsing.TryParseRfc3339(value) is { } parsed)
        {
            return parsed;
        }

        var error = GoRfc3339ParseError(value)
            ?? "parsing time "
                + GoCast.GoQuote(value)
                + ": timestamp is outside the DateTimeOffset range";

        throw new InvalidOperationException(
            "invalid reset timestamp " + GoCast.GoQuote(value) + ": " + error);
    }

    /// <summary>Port of zenmuxWindowStatus (zenmux_checker.go:174-182).</summary>
    internal static string WindowStatus(double usageRatio)
    {
        if (usageRatio >= 1)
        {
            return ProviderQuotaStatuses.Exhausted;
        }

        return usageRatio >= ProviderQuotaData.WarningThresholdRatio
            ? ProviderQuotaStatuses.Warning
            : ProviderQuotaStatuses.Available;
    }

    /// <summary>Port of zenmuxAccountStatus (zenmux_checker.go:184-195).</summary>
    internal static string AccountStatus(string status)
    {
        switch (LowerAscii(GoStrings.TrimSpace(status)))
        {
            case "healthy":
            case "monitored":
                return ProviderQuotaStatuses.Available;
            case "abusive":
                return ProviderQuotaStatuses.Warning;
            case "suspended":
            case "banned":
                return ProviderQuotaStatuses.Exhausted;
            default:
                return ProviderQuotaStatuses.Unknown;
        }
    }

    /// <summary>Port of worstZenmuxStatus (zenmux_checker.go:197-205).</summary>
    internal static string WorstStatus(params string[] statuses)
    {
        ArgumentNullException.ThrowIfNull(statuses);

        var worst = ProviderQuotaStatuses.Available;
        foreach (var status in statuses)
        {
            if (StatusRank(status) > StatusRank(worst))
            {
                worst = status;
            }
        }

        return worst;
    }

    /// <summary>
    /// Port of zenmuxStatusRank (zenmux_checker.go:207-218): unknown outranks
    /// exhausted, unlike the normalization rank.
    /// </summary>
    internal static int StatusRank(string? status) => status switch
    {
        ProviderQuotaStatuses.Available => 0,
        ProviderQuotaStatuses.Warning => 1,
        ProviderQuotaStatuses.Exhausted => 2,
        _ => 3,
    };

    /// <summary>Port of the rawData map literal (zenmux_checker.go:151-155).</summary>
    internal static Dictionary<string, object?> PrepareRawData(ZenmuxSubscriptionData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["plan"] = data.Plan,
            ["quota_monthly"] = data.QuotaMonthly,
            ["account_status"] = data.AccountStatus,
        };
    }

    /// <summary>
    /// Reproduces the message of the error <c>time.Parse(time.RFC3339, value)</c>
    /// would return, or null when Go accepts the value. Go's range checks fire
    /// per layout chunk (month/hour/minute/second/zone), while the day check is
    /// deferred until the whole string has been consumed; the staged parser
    /// follows the same order. Values Go accepts but DateTimeOffset cannot
    /// represent (year 0000, offsets beyond ±14:00, or a UTC instant outside
    /// the calendar) report the port's range limit instead (C123/C207).
    /// </summary>
    internal static string? GoRfc3339ParseError(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var position = 0;

        // "2006": four characters with a digit first, then atoi over the four.
        if (value.Length - position < 4 || !IsDigit(value[position]))
        {
            return CannotParse(value, position, "2006");
        }

        var year = 0;
        for (var index = 0; index < 4; index++)
        {
            if (!IsDigit(value[position + index]))
            {
                return CannotParse(value, position, "2006");
            }

            year = (year * 10) + (value[position + index] - '0');
        }

        position += 4;

        if (!TakeLiteral(value, ref position, '-'))
        {
            return CannotParse(value, position, "-");
        }

        if (!TryGetNumFixed(value, ref position, out var month))
        {
            return CannotParse(value, position, "01");
        }

        if (month is < 1 or > 12)
        {
            return RangeError(value, "month");
        }

        if (!TakeLiteral(value, ref position, '-'))
        {
            return CannotParse(value, position, "-");
        }

        if (!TryGetNumFixed(value, ref position, out var day))
        {
            return CannotParse(value, position, "02");
        }

        if (!TakeLiteral(value, ref position, 'T'))
        {
            return CannotParse(value, position, "T");
        }

        if (!TryGetNumFlexible(value, ref position, out var hour))
        {
            return CannotParse(value, position, "15");
        }

        if (hour >= 24)
        {
            return RangeError(value, "hour");
        }

        if (!TakeLiteral(value, ref position, ':'))
        {
            return CannotParse(value, position, ":");
        }

        if (!TryGetNumFixed(value, ref position, out var minute))
        {
            return CannotParse(value, position, "04");
        }

        if (minute >= 60)
        {
            return RangeError(value, "minute");
        }

        if (!TakeLiteral(value, ref position, ':'))
        {
            return CannotParse(value, position, ":");
        }

        if (!TryGetNumFixed(value, ref position, out var second))
        {
            return CannotParse(value, position, "05");
        }

        if (second >= 60)
        {
            return RangeError(value, "second");
        }

        // time.Parse accepts a fractional second even though the RFC3339
        // layout does not contain one, but only when a digit follows the
        // separator; otherwise the text is left for the zone chunk.
        if (position + 1 < value.Length
            && value[position] is '.' or ','
            && IsDigit(value[position + 1]))
        {
            position++;
            while (position < value.Length && IsDigit(value[position]))
            {
                position++;
            }
        }

        var offsetSeconds = 0;
        if (position < value.Length && value[position] == 'Z')
        {
            position++;
        }
        else
        {
            var zoneStart = position;
            if (value.Length - position < 6 || value[position + 3] != ':')
            {
                return CannotParse(value, zoneStart, "Z07:00");
            }

            // The reference parses the hour pair, then the minute pair only if
            // the hour parsed, but always runs the range checks (failed pairs
            // read as zero). A range error therefore wins over a malformed
            // pair or an invalid sign.
            var hourParsed = TryParseTwoDigits(value.Substring(position + 1, 2), out var zoneHour);
            var minuteParsed = false;
            var zoneMinute = 0;
            if (hourParsed)
            {
                minuteParsed = TryParseTwoDigits(value.Substring(position + 4, 2), out zoneMinute);
            }

            // The reference allows offsets of 24 hours and 60 minutes; the
            // minute check runs second and wins when both are out of range.
            var zoneRange = string.Empty;
            if (zoneHour > 24)
            {
                zoneRange = "time zone offset hour";
            }

            if (zoneMinute > 60)
            {
                zoneRange = "time zone offset minute";
            }

            var sign = value[position];
            position += 6;

            if (zoneRange.Length != 0)
            {
                return RangeError(value, zoneRange);
            }

            if (!hourParsed || !minuteParsed || sign is not ('+' or '-'))
            {
                return CannotParse(value, zoneStart, "Z07:00");
            }

            offsetSeconds = ((zoneHour * 60) + zoneMinute) * 60;
            if (sign == '-')
            {
                offsetSeconds = -offsetSeconds;
            }
        }

        if (position < value.Length)
        {
            return "parsing time "
                + GoCast.GoQuote(value)
                + ": extra text: "
                + GoCast.GoQuote(value[position..]);
        }

        // The month/day combination is validated after the whole value parsed.
        if (day < 1 || day > DaysIn(month, year))
        {
            return RangeError(value, "day");
        }

        // Go accepted the value; the remaining failures are DateTimeOffset's
        // representable range (go's time.Time spans year 0 and ±24:60 offsets).
        if (year < 1
            || offsetSeconds < -14 * 3600
            || offsetSeconds > 14 * 3600)
        {
            return OutOfRange(value);
        }

        return null;
    }

    private static string RangeError(string value, string field)
        => "parsing time " + GoCast.GoQuote(value) + ": " + field + " out of range";

    private static string OutOfRange(string value)
        => "parsing time " + GoCast.GoQuote(value) + ": timestamp is outside the DateTimeOffset range";

    private static string CannotParse(string value, int position, string layoutElement)
        => "parsing time "
            + GoCast.GoQuote(value)
            + " as "
            + GoCast.GoQuote("2006-01-02T15:04:05Z07:00")
            + ": cannot parse "
            + GoCast.GoQuote(value[position..])
            + " as "
            + GoCast.GoQuote(layoutElement);

    private static bool TakeLiteral(string value, ref int position, char literal)
    {
        if (position >= value.Length || value[position] != literal)
        {
            return false;
        }

        position++;
        return true;
    }

    /// <summary>Go getnum(value, true): exactly two digits required.</summary>
    private static bool TryGetNumFixed(string value, ref int position, out int number)
    {
        if (position + 1 >= value.Length
            || !IsDigit(value[position])
            || !IsDigit(value[position + 1]))
        {
            number = 0;
            return false;
        }

        number = ((value[position] - '0') * 10) + (value[position + 1] - '0');
        position += 2;
        return true;
    }

    /// <summary>Go getnum(value, true) over an already sliced two-character field.</summary>
    private static bool TryParseTwoDigits(string value, out int number)
    {
        if (value.Length != 2 || !IsDigit(value[0]) || !IsDigit(value[1]))
        {
            number = 0;
            return false;
        }

        number = ((value[0] - '0') * 10) + (value[1] - '0');
        return true;
    }

    /// <summary>Go getnum(value, false): one or two digits.</summary>
    private static bool TryGetNumFlexible(string value, ref int position, out int number)
    {
        if (position >= value.Length || !IsDigit(value[position]))
        {
            number = 0;
            return false;
        }

        number = value[position] - '0';
        position++;
        if (position < value.Length && IsDigit(value[position]))
        {
            number = (number * 10) + (value[position] - '0');
            position++;
        }

        return true;
    }

    /// <summary>Go daysIn: leap years, including year 0, which DateTime.IsLeapYear rejects.</summary>
    private static int DaysIn(int month, int year)
    {
        if (month == 2)
        {
            return IsLeapYear(year) ? 29 : 28;
        }

        return month is 4 or 6 or 9 or 11 ? 30 : 31;
    }

    private static bool IsLeapYear(int year)
        => (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;

    private static bool IsDigit(char value) => value is >= '0' and <= '9';

    private static ZenmuxSubscriptionData? ParseData(
        JsonElement value,
        ZenmuxSubscriptionData? existing)
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
                "zenmuxSubscriptionResponse",
                "data",
                "provider_quota.zenmuxSubscriptionData");
        }

        var data = existing ?? new ZenmuxSubscriptionData();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "plan":
                    data.Plan = ParsePlan(property.Value, data.Plan);
                    break;
                case "account_status":
                    data.AccountStatus = StringValue(
                        property.Value,
                        "zenmuxSubscriptionData",
                        "data.account_status",
                        data.AccountStatus);
                    break;
                case "quota_5_hour":
                    data.Quota5Hour = ParseWindow(
                        property.Value,
                        data.Quota5Hour,
                        "data.quota_5_hour");
                    break;
                case "quota_7_day":
                    data.Quota7Day = ParseWindow(
                        property.Value,
                        data.Quota7Day,
                        "data.quota_7_day");
                    break;
                case "quota_monthly":
                    data.QuotaMonthly = ParseMonthly(property.Value, data.QuotaMonthly);
                    break;
            }
        }

        return data;
    }

    private static ZenmuxPlan ParsePlan(JsonElement value, ZenmuxPlan existing)
    {
        // plan is a value struct: null is a no-op.
        if (value.ValueKind == JsonValueKind.Null)
        {
            return existing;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "zenmuxSubscriptionData",
                "data.plan",
                "provider_quota.zenmuxPlan");
        }

        var plan = existing;
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "tier":
                    plan.Tier = StringValue(
                        property.Value,
                        "zenmuxPlan",
                        "data.plan.tier",
                        plan.Tier);
                    break;
                case "amount_usd":
                    plan.AmountUsd = DoubleValue(
                        property.Value,
                        "zenmuxPlan",
                        "data.plan.amount_usd",
                        plan.AmountUsd);
                    break;
                case "interval":
                    plan.Interval = StringValue(
                        property.Value,
                        "zenmuxPlan",
                        "data.plan.interval",
                        plan.Interval);
                    break;
                case "expires_at":
                    plan.ExpiresAt = StringValue(
                        property.Value,
                        "zenmuxPlan",
                        "data.plan.expires_at",
                        plan.ExpiresAt);
                    break;
            }
        }

        return plan;
    }

    private static ZenmuxQuotaWindow ParseWindow(
        JsonElement value,
        ZenmuxQuotaWindow existing,
        string fieldPath)
    {
        // quota_5_hour / quota_7_day are value structs: null is a no-op.
        if (value.ValueKind == JsonValueKind.Null)
        {
            return existing;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "zenmuxSubscriptionData",
                fieldPath,
                "provider_quota.zenmuxQuotaWindow");
        }

        var window = existing;
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "usage_percentage":
                    window.UsagePercentage = DoubleValue(
                        property.Value,
                        "zenmuxQuotaWindow",
                        fieldPath + ".usage_percentage",
                        window.UsagePercentage);
                    break;
                case "resets_at":
                    window.ResetsAt = NullableString(
                        property.Value,
                        "zenmuxQuotaWindow",
                        fieldPath + ".resets_at");
                    break;
                case "max_flows":
                    window.MaxFlows = DoubleValue(
                        property.Value,
                        "zenmuxQuotaWindow",
                        fieldPath + ".max_flows",
                        window.MaxFlows);
                    break;
                case "used_flows":
                    window.UsedFlows = DoubleValue(
                        property.Value,
                        "zenmuxQuotaWindow",
                        fieldPath + ".used_flows",
                        window.UsedFlows);
                    break;
                case "remaining_flows":
                    window.RemainingFlows = DoubleValue(
                        property.Value,
                        "zenmuxQuotaWindow",
                        fieldPath + ".remaining_flows",
                        window.RemainingFlows);
                    break;
                case "used_value_usd":
                    window.UsedValueUsd = DoubleValue(
                        property.Value,
                        "zenmuxQuotaWindow",
                        fieldPath + ".used_value_usd",
                        window.UsedValueUsd);
                    break;
                case "max_value_usd":
                    window.MaxValueUsd = DoubleValue(
                        property.Value,
                        "zenmuxQuotaWindow",
                        fieldPath + ".max_value_usd",
                        window.MaxValueUsd);
                    break;
            }
        }

        return window;
    }

    private static ZenmuxMonthlyQuota ParseMonthly(
        JsonElement value,
        ZenmuxMonthlyQuota existing)
    {
        // quota_monthly is a value struct: null is a no-op.
        if (value.ValueKind == JsonValueKind.Null)
        {
            return existing;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                "zenmuxSubscriptionData",
                "data.quota_monthly",
                "provider_quota.zenmuxMonthlyQuota");
        }

        var monthly = existing;
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "max_flows":
                    monthly.MaxFlows = NullableDouble(
                        property.Value,
                        "zenmuxMonthlyQuota",
                        "data.quota_monthly.max_flows");
                    break;
                case "max_value_usd":
                    monthly.MaxValueUsd = NullableDouble(
                        property.Value,
                        "zenmuxMonthlyQuota",
                        "data.quota_monthly.max_value_usd");
                    break;
            }
        }

        return monthly;
    }

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
            _ => throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                structName,
                fieldPath,
                "bool"),
        };

    private static string StringValue(
        JsonElement value,
        string structName,
        string fieldPath,
        string current)
        => value.ValueKind switch
        {
            JsonValueKind.Null => current,
            JsonValueKind.String => value.GetString() ?? string.Empty,
            _ => throw StructTypeMismatch(
                GoKindName(value.ValueKind),
                structName,
                fieldPath,
                "string"),
        };

    private static double DoubleValue(
        JsonElement value,
        string structName,
        string fieldPath,
        double current)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return current;
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

    private static string? NullableString(
        JsonElement value,
        string structName,
        string fieldPath)
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

    private static double? NullableDouble(
        JsonElement value,
        string structName,
        string fieldPath)
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

    /// <summary>strings.ToLower over the ASCII statuses (non-ASCII bytes are unchanged).</summary>
    private static string LowerAscii(string value)
    {
        var chars = value.ToCharArray();
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
    {
        // zenmux_checker.go:74-78: the channel proxy replaces the shared
        // client first, then the HTTPS-downgrade rejection is installed so the
        // management key cannot be forwarded over a redirect to cleartext.
        var executor = ParseSettingsLenient(channel.Settings)?.Proxy is { } proxy
            ? _proxyExecutors.Create(ToProxyConfig(proxy))
            : _executor;

        return executor.WithRejectHttpsDowngrade();
    }

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
/// Port of <c>zenmuxSubscriptionResponse</c> (zenmux_checker.go:18-22). The
/// nested objects keep Go's "null is a no-op for value structs / resets a
/// pointer, repeated objects merge" semantics.
/// </summary>
internal sealed class ZenmuxSubscriptionResponse
{
    public bool Success { get; set; }

    public string Message { get; set; } = string.Empty;

    public ZenmuxSubscriptionData? Data { get; set; }
}

/// <summary>Port of <c>zenmuxSubscriptionData</c> (zenmux_checker.go:24-30).</summary>
internal sealed class ZenmuxSubscriptionData
{
    public ZenmuxPlan Plan { get; set; } = new();

    public string AccountStatus { get; set; } = string.Empty;

    public ZenmuxQuotaWindow Quota5Hour { get; set; } = new();

    public ZenmuxQuotaWindow Quota7Day { get; set; } = new();

    public ZenmuxMonthlyQuota QuotaMonthly { get; set; } = new();
}

/// <summary>
/// Port of <c>zenmuxPlan</c> (zenmux_checker.go:32-37), carried verbatim in the
/// quota raw data. Property names and declaration order mirror the Go tags so
/// GoJson emits the same object.
/// </summary>
internal sealed class ZenmuxPlan
{
    [JsonPropertyName("tier")]
    public string Tier { get; set; } = string.Empty;

    [JsonPropertyName("amount_usd")]
    public double AmountUsd { get; set; }

    [JsonPropertyName("interval")]
    public string Interval { get; set; } = string.Empty;

    [JsonPropertyName("expires_at")]
    public string ExpiresAt { get; set; } = string.Empty;
}

/// <summary>Port of <c>zenmuxQuotaWindow</c> (zenmux_checker.go:39-47).</summary>
internal sealed class ZenmuxQuotaWindow
{
    public double UsagePercentage { get; set; }

    public string? ResetsAt { get; set; }

    public double MaxFlows { get; set; }

    public double UsedFlows { get; set; }

    public double RemainingFlows { get; set; }

    public double UsedValueUsd { get; set; }

    public double MaxValueUsd { get; set; }
}

/// <summary>
/// Port of <c>zenmuxMonthlyQuota</c> (zenmux_checker.go:49-52), carried
/// verbatim in the quota raw data.
/// </summary>
internal sealed class ZenmuxMonthlyQuota
{
    [JsonPropertyName("max_flows")]
    public double? MaxFlows { get; set; }

    [JsonPropertyName("max_value_usd")]
    public double? MaxValueUsd { get; set; }
}
