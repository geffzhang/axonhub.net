using System.Globalization;
using System.Text;
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
/// Port of the Kimi Code quota checker
/// (internal/server/biz/provider_quota/kimi_code_checker.go): the channel's
/// base URL determines the usage endpoint, whose payload reports one usage
/// row plus a list of limits (optionally wrapped in a <c>detail</c> object) and
/// an optional booster wallet. Every readable row becomes a token limit; the
/// worst ratio drives the channel status.
/// </summary>
public sealed class KimiCodeQuotaChecker : IQuotaChecker
{
    private const string ProviderType = "kimi_code";

    /// <summary>kimiCodeDefaultBaseURL (kimi_code_checker.go:17).</summary>
    internal const string DefaultBaseUrl = "https://api.kimi.com/coding/v1";

    internal const string UsagePath = "/usages";

    private const string RequestFailedPrefix = "kimi code usage request failed: ";

    private const string ParseResponseErrorPrefix = "failed to parse kimi code usage response: ";

    private const long NanosecondsPerTick = 100;

    private const long SecondsToNanoseconds = 1_000_000_000;

    private const long MinuteNanoseconds = 60_000_000_000;

    private const long HourNanoseconds = 3_600_000_000_000;

    private const long DayNanoseconds = 24 * HourNanoseconds;

    private static readonly string[] ResetAtKeys = ["reset_at", "resetAt", "reset_time", "resetTime"];

    private static readonly string[] ResetAfterSecondKeys = ["reset_in", "resetIn", "ttl", "window"];

    /// <summary>kimiCodeLimitLabel's lookup keys (kimi_code_checker.go:241).</summary>
    private static readonly string[] LimitLabelKeys = ["name", "title", "scope"];

    /// <summary>
    /// parseKimiCodeUsageRow only looks at <c>name</c> and <c>title</c>
    /// (kimi_code_checker.go:198); <c>scope</c> belongs to the limit label.
    /// </summary>
    private static readonly string[] RowLabelKeys = ["name", "title"];

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;

    public KimiCodeQuotaChecker(
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
    /// Port of (*KimiCodeQuotaChecker).CheckQuota
    /// (kimi_code_checker.go:50-77).
    /// </summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var apiKey = ResolveApiKey(channel);
        if (apiKey.Length == 0)
        {
            throw new InvalidOperationException("channel has no API key");
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

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
    /// Port of (*KimiCodeQuotaChecker).SupportsChannel
    /// (kimi_code_checker.go:79-81).
    /// </summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Type == ChannelOutboundFactory.MoonshotCodingChannelType;
    }

    /// <summary>
    /// Port of the credential lookup at kimi_code_checker.go:51-57: the legacy
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
    /// Port of buildKimiCodeUsageURL (kimi_code_checker.go:83-102): the base
    /// URL keeps its authority, the path gets a <c>/v1</c> suffix when missing
    /// and <c>/usages</c> is appended; invalid input falls back to the default
    /// endpoint. Go's <c>URL.String()</c> re-encodes the decoded path with
    /// <c>encodePath</c> and keeps the <c>?</c> of an empty query (ForceQuery).
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

        var authority = RawAuthority(trimmed);
        if (authority.Length == 0)
        {
            authority = host;
        }

        var path = DecodePathBytes(RawPath(trimmed));
        while (path.Count > 0 && path[^1] == (byte)'/')
        {
            path.RemoveAt(path.Count - 1);
        }

        if (!EndsWithV1(path))
        {
            AppendAscii(path, "/v1");
        }

        AppendAscii(path, UsagePath);

        return scheme + "://" + authority
            + EncodePath(path)
            + (HasEmptyQuery(trimmed) ? "?" : string.Empty);
    }

    /// <summary>
    /// Port of parseKimiCodeUsageResponse (kimi_code_checker.go:104-180).
    /// <paramref name="now"/> stands in for the reference's <c>time.Now()</c>
    /// inside the row loop.
    /// </summary>
    internal static QuotaData ParseResponse(byte[] body, DateTimeOffset now)
    {
        KimiCodeUsageResponse response;
        try
        {
            response = ParseUsageResponse(body);
        }
        catch (JsonException ex)
        {
            throw GoErrors.Wrap(ex, ParseResponseErrorPrefix);
        }

        var parsedRows = new List<(KimiCodeUsageRow Row, long Window)>();
        if (ParseUsageRow(response.Usage, "Weekly limit") is { } weeklyRow)
        {
            parsedRows.Add((weeklyRow, 7L * 24 * 3600 * SecondsToNanoseconds));
        }

        if (response.Limits is { } limits)
        {
            for (var index = 0; index < limits.Count; index++)
            {
                var item = limits[index];
                var detail = item;
                if (item is not null
                    && item.TryGetValue("detail", out var nested)
                    && nested is Dictionary<string, object?> nestedMap)
                {
                    detail = nestedMap;
                }

                var label = LimitLabel(item, detail, index);
                if (ParseUsageRow(detail, label) is { } row)
                {
                    parsedRows.Add((row, WindowLength(item, detail)));
                }
            }
        }

        if (parsedRows.Count == 0)
        {
            throw new InvalidOperationException("kimi code usage response contains no quota limits");
        }

        var status = ProviderQuotaStatuses.Available;
        var quotaLimits = new List<QuotaLimitStatus>(parsedRows.Count);
        var rows = new List<KimiCodeUsageRow>(parsedRows.Count);
        DateTimeOffset? nextResetAt = null;

        foreach (var (row, windowNanos) in parsedRows)
        {
            var ratio = row.Limit > 0 ? (double)row.Used / row.Limit : 0.0;
            var rowStatus = StatusForUsageRatio(ratio);
            status = WorseStatus(status, rowStatus);

            DateTimeOffset? resetAt = null;
            if (row.ResetAt is { } resetAtText)
            {
                // A present-but-unparseable resetAt suppresses resetAfterSeconds.
                if (GoTimeParsing.TryParseRfc3339(resetAtText) is { } parsed)
                {
                    resetAt = parsed;
                    if (nextResetAt is null || parsed < nextResetAt)
                    {
                        nextResetAt = parsed;
                    }
                }
            }
            else if (row.ResetAfterSeconds is { } seconds && seconds > 0)
            {
                if (AddGoSeconds(now, seconds) is { } parsed)
                {
                    resetAt = parsed;
                    if (nextResetAt is null || parsed < nextResetAt)
                    {
                        nextResetAt = parsed;
                    }
                }
            }

            var window = NanosecondsToTimeSpan(windowNanos);
            var windowLabel = ProviderQuotaData.NormalizeQuotaWindowLabel(window);
            if (windowLabel.Length == 0)
            {
                windowLabel = row.Label;
            }

            var limit = ProviderQuotaData.NewTokenLimitStatus(rowStatus, ratio, resetAt);
            ProviderQuotaData.WithWindow(limit, windowLabel, window);
            quotaLimits.Add(limit);
            rows.Add(row);
        }

        var rawData = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["rows"] = rows,
        };
        if (ParseBoosterWallet(response.BoosterWallet) is { } wallet)
        {
            rawData["boosterWallet"] = wallet;
        }

        return ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = status,
                ProviderType = ProviderType,
                RawData = rawData,
                NextResetAt = nextResetAt,
                Ready = ProviderQuotaStatuses.IsReadyStatus(status),
                Limits = quotaLimits,
            },
            now);
    }

    /// <summary>Port of parseKimiCodeUsageRow (kimi_code_checker.go:182-212).</summary>
    internal static KimiCodeUsageRow? ParseUsageRow(
        IReadOnlyDictionary<string, object?>? raw,
        string fallbackLabel)
    {
        if (raw is null || raw.Count == 0)
        {
            return null;
        }

        var limitOk = KimiCodeInt(Get(raw, "limit"), out var limit);
        var usedOk = KimiCodeInt(Get(raw, "used"), out var used);
        if (!usedOk && limitOk)
        {
            if (KimiCodeInt(Get(raw, "remaining"), out var remaining))
            {
                used = unchecked(limit - remaining);
                usedOk = true;
            }
        }

        if (!limitOk && !usedOk)
        {
            return null;
        }

        var label = fallbackLabel;
        foreach (var key in RowLabelKeys)
        {
            if (Get(raw, key) is string value && value.Length != 0)
            {
                label = value;
                break;
            }
        }

        return new KimiCodeUsageRow
        {
            Label = label,
            Used = used,
            Limit = limit,
            ResetAt = ResetAt(raw),
            ResetAfterSeconds = ResetAfterSeconds(raw),
        };
    }

    /// <summary>
    /// Port of kimiCodeWindowLength (kimi_code_checker.go:218-238). Returns the
    /// window as Go nanoseconds; the multiplications wrap on int64 overflow
    /// exactly like Go's <c>time.Duration</c> arithmetic.
    /// </summary>
    internal static long WindowLength(
        IReadOnlyDictionary<string, object?>? item,
        IReadOnlyDictionary<string, object?>? detail)
    {
        var window = Get(item, "window") as Dictionary<string, object?>;
        var (durationOk, duration) = FirstKimiCodeInt(
            Get(window, "duration"),
            Get(item, "duration"),
            Get(detail, "duration"));
        if (!durationOk || duration <= 0)
        {
            return 0;
        }

        var (_, unit) = FirstKimiCodeString(
            Get(window, "timeUnit"),
            Get(item, "timeUnit"),
            Get(detail, "timeUnit"));
        unit ??= string.Empty;

        if (unit.Contains("MINUTE", StringComparison.Ordinal))
        {
            return unchecked(duration * MinuteNanoseconds);
        }

        if (unit.Contains("HOUR", StringComparison.Ordinal))
        {
            return unchecked(duration * HourNanoseconds);
        }

        if (unit.Contains("DAY", StringComparison.Ordinal))
        {
            return unchecked(duration * DayNanoseconds);
        }

        return unchecked(duration * SecondsToNanoseconds);
    }

    /// <summary>Port of kimiCodeLimitLabel (kimi_code_checker.go:240-267).</summary>
    internal static string LimitLabel(
        IReadOnlyDictionary<string, object?>? item,
        IReadOnlyDictionary<string, object?>? detail,
        int index)
    {
        foreach (var key in LimitLabelKeys)
        {
            foreach (var source in new[] { item, detail })
            {
                if (Get(source, key) is string value && value.Length != 0)
                {
                    return value;
                }
            }
        }

        var window = Get(item, "window") as Dictionary<string, object?>;
        var (durationOk, duration) = FirstKimiCodeInt(
            Get(window, "duration"),
            Get(item, "duration"),
            Get(detail, "duration"));
        if (durationOk)
        {
            var (_, unit) = FirstKimiCodeString(
                Get(window, "timeUnit"),
                Get(item, "timeUnit"),
                Get(detail, "timeUnit"));
            unit ??= string.Empty;

            if (unit.Contains("MINUTE", StringComparison.Ordinal)
                && duration >= 60
                && duration % 60 == 0)
            {
                return Invariant(duration / 60) + "h limit";
            }

            if (unit.Contains("MINUTE", StringComparison.Ordinal))
            {
                return Invariant(duration) + "m limit";
            }

            if (unit.Contains("HOUR", StringComparison.Ordinal))
            {
                return Invariant(duration) + "h limit";
            }

            if (unit.Contains("DAY", StringComparison.Ordinal))
            {
                return Invariant(duration) + "d limit";
            }

            return Invariant(duration) + "s limit";
        }

        return "Limit #" + Invariant(index + 1);
    }

    /// <summary>Port of kimiCodeResetAt (kimi_code_checker.go:269-276).</summary>
    internal static string? ResetAt(IReadOnlyDictionary<string, object?> raw)
    {
        foreach (var key in ResetAtKeys)
        {
            if (Get(raw, key) is string value && value.Length != 0)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Port of kimiCodeResetAfterSeconds (kimi_code_checker.go:278-285).</summary>
    internal static long? ResetAfterSeconds(IReadOnlyDictionary<string, object?> raw)
    {
        foreach (var key in ResetAfterSecondKeys)
        {
            if (KimiCodeInt(Get(raw, key), out var value) && value > 0)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Port of parseKimiCodeBoosterWallet (kimi_code_checker.go:287-315).</summary>
    internal static KimiCodeBoosterWallet? ParseBoosterWallet(
        IReadOnlyDictionary<string, object?>? raw)
    {
        if (Get(raw, "balance") is not Dictionary<string, object?> balance
            || Get(balance, "type") is not string balanceType
            || balanceType != "BOOSTER")
        {
            return null;
        }

        if (!KimiCodeInt(Get(balance, "amount"), out var amount) || amount <= 0)
        {
            return null;
        }

        KimiCodeInt(Get(balance, "amountLeft"), out var amountLeft);
        var (monthlyLimit, limitCurrency) = Money(Get(raw, "monthlyChargeLimit"));
        var (monthlyUsed, usedCurrency) = Money(Get(raw, "monthlyUsed"));
        var currency = limitCurrency;
        if (currency.Length == 0)
        {
            currency = usedCurrency;
        }

        if (currency.Length == 0)
        {
            currency = "USD";
        }

        return new KimiCodeBoosterWallet
        {
            BalanceCents = FixedPointToCents(amountLeft),
            TotalCents = FixedPointToCents(amount),
            MonthlyChargeLimitEnabled = Get(raw, "monthlyChargeLimitEnabled") is true,
            MonthlyChargeLimitCents = monthlyLimit,
            MonthlyUsedCents = monthlyUsed,
            Currency = currency,
        };
    }

    /// <summary>Port of kimiCodeMoney (kimi_code_checker.go:317-325).</summary>
    internal static (long Cents, string Currency) Money(object? raw)
    {
        if (raw is not Dictionary<string, object?> record)
        {
            return (0, string.Empty);
        }

        KimiCodeInt(Get(record, "priceInCents"), out var cents);
        var currency = Get(record, "currency") is string value ? value : string.Empty;
        return (cents, currency);
    }

    /// <summary>Port of kimiCodeFixedPointToCents (kimi_code_checker.go:327-332).</summary>
    internal static long FixedPointToCents(long value)
    {
        if (value > 0 && value < 1_000_000)
        {
            return 1;
        }

        return unchecked((value + 500_000) / 1_000_000);
    }

    /// <summary>
    /// Port of kimiCodeInt (kimi_code_checker.go:334-344): JSON numbers are
    /// float64 and strings go through <c>strconv.ParseFloat</c>; the float to
    /// int64 conversion matches Go on amd64 (out-of-range and NaN yield the
    /// minimum int64).
    /// </summary>
    internal static bool KimiCodeInt(object? value, out long result)
    {
        switch (value)
        {
            case double number:
                result = GoFloatToInt64(number);
                return true;
            case string text:
                if (GoCast.TryParseFloat(text, out var parsed))
                {
                    result = GoFloatToInt64(parsed);
                    return true;
                }

                break;
        }

        result = 0;
        return false;
    }

    /// <summary>Port of firstKimiCodeInt (kimi_code_checker.go:346-353).</summary>
    internal static (bool Ok, long Value) FirstKimiCodeInt(params object?[] values)
    {
        foreach (var value in values)
        {
            if (KimiCodeInt(value, out var parsed))
            {
                return (true, parsed);
            }
        }

        return (false, 0);
    }

    /// <summary>
    /// Port of firstKimiCodeString (kimi_code_checker.go:355-362): the first
    /// value of the right type wins even when it is empty.
    /// </summary>
    internal static (bool Ok, string? Value) FirstKimiCodeString(params object?[] values)
    {
        foreach (var value in values)
        {
            if (value is string text)
            {
                return (true, text);
            }
        }

        return (false, null);
    }

    /// <summary>Port of kimiCodeStatusForUsageRatio (kimi_code_checker.go:364-372).</summary>
    internal static string StatusForUsageRatio(double ratio)
    {
        if (ratio >= 1.0)
        {
            return ProviderQuotaStatuses.Exhausted;
        }

        if (ratio >= ProviderQuotaData.WarningThresholdRatio)
        {
            return ProviderQuotaStatuses.Warning;
        }

        return ProviderQuotaStatuses.Available;
    }

    /// <summary>Port of worseKimiCodeStatus (kimi_code_checker.go:374-380).</summary>
    internal static string WorseStatus(string current, string candidate)
        => CheckerStatusRank(candidate) > CheckerStatusRank(current) ? candidate : current;

    /// <summary>
    /// The reference's local rank map (kimi_code_checker.go:375): available 0,
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
    /// Port of the <c>json.Unmarshal(body, &amp;response)</c> call at
    /// kimi_code_checker.go:106, including Go's map/slice merge semantics for
    /// duplicate keys.
    /// </summary>
    /// <exception cref="JsonException">
    /// The payload is not valid JSON or a field has the wrong JSON type; the
    /// message follows Go's UnmarshalTypeError text, while malformed JSON keeps
    /// the System.Text.Json wording (C003).
    /// </exception>
    internal static KimiCodeUsageResponse ParseUsageResponse(byte[] data)
    {
        using var document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        var root = document.RootElement;

        // Go: unmarshaling JSON null into a struct is a no-op.
        if (root.ValueKind == JsonValueKind.Null)
        {
            return new KimiCodeUsageResponse();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoKindName(root.ValueKind)
                + " into Go value of type provider_quota.kimiCodeUsageResponse");
        }

        var response = new KimiCodeUsageResponse();
        foreach (var property in root.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "usage":
                    response.Usage = DecodeAnyMap(property.Value, response.Usage, "usage");
                    break;
                case "limits":
                    response.Limits = DecodeMapSlice(property.Value, response.Limits);
                    break;
                case "boosterwallet":
                    response.BoosterWallet = DecodeAnyMap(
                        property.Value,
                        response.BoosterWallet,
                        "boosterWallet");
                    break;
            }
        }

        return response;
    }

    /// <summary>
    /// Go decodes a repeated map field into the existing map, merging the new
    /// keys; JSON null resets the field to nil.
    /// </summary>
    private static Dictionary<string, object?>? DecodeAnyMap(
        JsonElement value,
        Dictionary<string, object?>? current,
        string field)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoKindName(value.ValueKind)
                + " into Go struct field kimiCodeUsageResponse." + field
                + " of type map[string]interface {}");
        }

        var target = current ?? new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            // A map element never merges with a previous value: objects are
            // rebuilt fresh, so a repeated key replaces the whole value.
            target[property.Name] = DecodeAnyValue(property.Value, field);
        }

        return target;
    }

    /// <summary>
    /// Go decodes a repeated slice field by reusing the backing array: existing
    /// element maps merge, extra elements are truncated, missing ones appended.
    /// </summary>
    private static List<Dictionary<string, object?>?>? DecodeMapSlice(
        JsonElement value,
        List<Dictionary<string, object?>?>? current)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoKindName(value.ValueKind)
                + " into Go struct field kimiCodeUsageResponse.limits of type []map[string]interface {}");
        }

        var result = current ?? [];
        var index = 0;
        foreach (var element in value.EnumerateArray())
        {
            if (index >= result.Count)
            {
                result.Add(null);
            }

            if (element.ValueKind == JsonValueKind.Null)
            {
                result[index] = null;
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                var target = result[index] ?? new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    target[property.Name] = DecodeAnyValue(property.Value, "limits");
                }

                result[index] = target;
            }
            else
            {
                throw UnmarshalError(
                    "json: cannot unmarshal " + GoKindName(element.ValueKind)
                    + " into Go struct field kimiCodeUsageResponse.limits"
                    + " of type map[string]interface {}");
            }

            index++;
        }

        if (result.Count > index)
        {
            result.RemoveRange(index, result.Count - index);
        }

        return result;
    }

    /// <summary>Port of Go's decoder for a value stored in a map/slice of <c>any</c>.</summary>
    private static object? DecodeAnyValue(JsonElement value, string field) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => DecodeNumber(value, field),
        JsonValueKind.Array => DecodeAnyArray(value, field),
        JsonValueKind.Object => DecodeAnyObject(value, field),
        _ => null,
    };

    private static double DecodeNumber(JsonElement value, string field)
    {
        var raw = value.GetRawText();
        if (!GoCast.TryParseFloat(raw, out var parsed))
        {
            throw UnmarshalError(
                "json: cannot unmarshal number " + raw
                + " into Go struct field kimiCodeUsageResponse." + field
                + " of type float64");
        }

        return parsed;
    }

    private static List<object?> DecodeAnyArray(JsonElement value, string field)
    {
        var result = new List<object?>();
        foreach (var element in value.EnumerateArray())
        {
            result.Add(DecodeAnyValue(element, field));
        }

        return result;
    }

    private static Dictionary<string, object?> DecodeAnyObject(JsonElement value, string field)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            // Objects inside an `any` value are decoded fresh; repeated keys
            // follow Go's map assignment, so the last one wins.
            result[property.Name] = DecodeAnyValue(property.Value, field);
        }

        return result;
    }

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
    /// Go's int64(float64) conversion on amd64: NaN, ±Inf and values outside
    /// [-2^63, 2^63) produce the minimum int64; the rest truncate toward zero.
    /// </summary>
    internal static long GoFloatToInt64(double value)
    {
        if (double.IsNaN(value) || value >= 9223372036854775808.0 || value < -9223372036854775808.0)
        {
            return long.MinValue;
        }

        return (long)value;
    }

    /// <summary>
    /// Port of <c>time.Now().Add(time.Duration(seconds) * time.Second)</c>:
    /// the multiplication wraps on int64 overflow, and a resulting instant
    /// outside <see cref="DateTimeOffset"/>'s range has no port representation
    /// (C123). The reference anchors at the local wall clock while the port
    /// anchors at the injected UTC instant, so only the stored offset differs
    /// (C214).
    /// </summary>
    private static DateTimeOffset? AddGoSeconds(DateTimeOffset now, long seconds)
    {
        var nanoseconds = unchecked(seconds * SecondsToNanoseconds);
        try
        {
            return now.AddTicks(nanoseconds / NanosecondsPerTick);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static TimeSpan NanosecondsToTimeSpan(long nanoseconds)
        => TimeSpan.FromTicks(nanoseconds / NanosecondsPerTick);

    private static string Invariant(long value)
        => value.ToString(CultureInfo.InvariantCulture);

    private static object? Get(IReadOnlyDictionary<string, object?>? map, string key)
        => map is not null && map.TryGetValue(key, out var value) ? value : null;

    /// <summary>
    /// The raw authority of a parsed URL (userinfo and port included), used to
    /// keep the reference's host spelling. Go normalizes percent escapes in
    /// userinfo and the host; that re-encoding is registered as C213.
    /// </summary>
    private static string RawAuthority(string rawUrl)
    {
        var separator = rawUrl.IndexOf("://", StringComparison.Ordinal);
        if (separator < 0)
        {
            return string.Empty;
        }

        var start = separator + 3;
        var end = rawUrl.Length;
        foreach (var delimiter in new[] { '/', '?', '#' })
        {
            var index = rawUrl.IndexOf(delimiter, start);
            if (index >= 0 && index < end)
            {
                end = index;
            }
        }

        return rawUrl[start..end];
    }

    /// <summary>The raw path of a parsed URL, before percent decoding.</summary>
    private static string RawPath(string rawUrl)
    {
        var hash = rawUrl.IndexOf('#');
        var withoutFragment = hash < 0 ? rawUrl : rawUrl[..hash];
        var question = withoutFragment.IndexOf('?');
        var withoutQuery = question < 0 ? withoutFragment : withoutFragment[..question];

        var separator = withoutQuery.IndexOf("://", StringComparison.Ordinal);
        var searchStart = separator < 0 ? 0 : separator + 3;
        var slash = withoutQuery.IndexOf('/', searchStart);
        return slash < 0 ? string.Empty : withoutQuery[slash..];
    }

    /// <summary>
    /// Go's <c>URL.String()</c> keeps the <c>?</c> of a query that was present
    /// but empty (ForceQuery): a '?' before the fragment with nothing after it.
    /// </summary>
    private static bool HasEmptyQuery(string rawUrl)
    {
        var hash = rawUrl.IndexOf('#');
        var withoutFragment = hash < 0 ? rawUrl : rawUrl[..hash];
        var question = withoutFragment.IndexOf('?');
        return question >= 0 && question == withoutFragment.Length - 1;
    }

    /// <summary>
    /// Percent-decodes the raw path into the byte sequence Go stores in
    /// <c>URL.Path</c>; literal characters contribute their UTF-8 bytes.
    /// </summary>
    private static List<byte> DecodePathBytes(string rawPath)
    {
        var bytes = new List<byte>(rawPath.Length);
        for (var index = 0; index < rawPath.Length;)
        {
            if (rawPath[index] == '%'
                && index + 2 < rawPath.Length
                && IsHex(rawPath[index + 1])
                && IsHex(rawPath[index + 2]))
            {
                bytes.Add((byte)((HexValue(rawPath[index + 1]) << 4) | HexValue(rawPath[index + 2])));
                index += 3;
                continue;
            }

            var rune = Rune.GetRuneAt(rawPath, index);
            Span<byte> buffer = stackalloc byte[4];
            var written = rune.EncodeToUtf8(buffer);
            for (var offset = 0; offset < written; offset++)
            {
                bytes.Add(buffer[offset]);
            }

            index += rune.Utf16SequenceLength;
        }

        return bytes;
    }

    /// <summary>
    /// Go's <c>escape(path, encodePath)</c>: unreserved bytes and
    /// <c>$&amp;+,/:;=@</c> stay literal, <c>?</c> and everything else become
    /// uppercase percent escapes.
    /// </summary>
    private static string EncodePath(List<byte> path)
    {
        var builder = new StringBuilder(path.Count);
        foreach (var value in path)
        {
            if (IsPathLiteral(value))
            {
                builder.Append((char)value);
                continue;
            }

            builder.Append('%');
            builder.Append(UpperHex[value >> 4]);
            builder.Append(UpperHex[value & 0xF]);
        }

        return builder.ToString();
    }

    private const string UpperHex = "0123456789ABCDEF";

    private static bool IsPathLiteral(byte value)
        => value is >= (byte)'a' and <= (byte)'z'
            or >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'0' and <= (byte)'9'
            or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~'
            or (byte)'$' or (byte)'&' or (byte)'+' or (byte)','
            or (byte)'/' or (byte)':' or (byte)';' or (byte)'='
            or (byte)'@';

    private static bool EndsWithV1(List<byte> path)
        => path.Count >= 3
            && path[^3] == (byte)'/'
            && path[^2] == (byte)'v'
            && path[^1] == (byte)'1';

    private static void AppendAscii(List<byte> target, string text)
    {
        foreach (var c in text)
        {
            target.Add((byte)c);
        }
    }

    private static bool IsHex(char c)
        => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

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

/// <summary>Port of kimiCodeUsageResponse (kimi_code_checker.go:19-23).</summary>
internal sealed class KimiCodeUsageResponse
{
    public Dictionary<string, object?>? Usage { get; set; }

    public List<Dictionary<string, object?>?>? Limits { get; set; }

    public Dictionary<string, object?>? BoosterWallet { get; set; }
}

/// <summary>Port of kimiCodeUsageRow (kimi_code_checker.go:25-31).</summary>
internal sealed class KimiCodeUsageRow
{
    [JsonPropertyName("label")]
    [JsonPropertyOrder(0)]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("used")]
    [JsonPropertyOrder(1)]
    public long Used { get; set; }

    [JsonPropertyName("limit")]
    [JsonPropertyOrder(2)]
    public long Limit { get; set; }

    [JsonPropertyName("resetAt")]
    [JsonPropertyOrder(3)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResetAt { get; set; }

    [JsonPropertyName("resetAfterSeconds")]
    [JsonPropertyOrder(4)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ResetAfterSeconds { get; set; }
}

/// <summary>Port of kimiCodeBoosterWallet (kimi_code_checker.go:33-40).</summary>
internal sealed class KimiCodeBoosterWallet
{
    [JsonPropertyName("balanceCents")]
    [JsonPropertyOrder(0)]
    public long BalanceCents { get; set; }

    [JsonPropertyName("totalCents")]
    [JsonPropertyOrder(1)]
    public long TotalCents { get; set; }

    [JsonPropertyName("monthlyChargeLimitEnabled")]
    [JsonPropertyOrder(2)]
    public bool MonthlyChargeLimitEnabled { get; set; }

    [JsonPropertyName("monthlyChargeLimitCents")]
    [JsonPropertyOrder(3)]
    public long MonthlyChargeLimitCents { get; set; }

    [JsonPropertyName("monthlyUsedCents")]
    [JsonPropertyOrder(4)]
    public long MonthlyUsedCents { get; set; }

    [JsonPropertyName("currency")]
    [JsonPropertyOrder(5)]
    public string Currency { get; set; } = string.Empty;
}
