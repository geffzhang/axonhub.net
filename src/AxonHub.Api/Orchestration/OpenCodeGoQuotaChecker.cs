using System.Globalization;
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
/// Port of the OpenCode Go quota checker
/// (internal/server/biz/provider_quota/opencode_go_checker.go): the fixed
/// usage endpoint reports rolling / weekly / monthly windows as 0-100 percent
/// with a reset deadline; each window becomes a token limit and the worst
/// window drives the channel status.
/// </summary>
public sealed class OpenCodeGoQuotaChecker : IQuotaChecker
{
    private const string ProviderType = "opencode_go";

    /// <summary>opencodeGoUsageURL (opencode_go_checker.go:18).</summary>
    internal const string UsageUrl = "https://opencode.ai/zen/go/v1/usage";

    private const string RequestFailedPrefix = "opencode go usage request failed: ";

    private const string ParseResponseErrorPrefix = "parse OpenCode Go usage response: ";

    private static readonly string[] WindowOrder = ["rolling", "weekly", "monthly"];

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;

    public OpenCodeGoQuotaChecker(
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
    /// Port of (*OpenCodeGoQuotaChecker).CheckQuota
    /// (opencode_go_checker.go:82-106).
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
            Url = UsageUrl,
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
    /// Port of (*OpenCodeGoQuotaChecker).SupportsChannel
    /// (opencode_go_checker.go:109-111).
    /// </summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Type == ChannelOutboundFactory.OpenCodeGoChannelType
            || channel.Type == ChannelOutboundFactory.OpenCodeGoAnthropicChannelType;
    }

    /// <summary>
    /// Port of opencodeGoAPIKey (opencode_go_checker.go:70-78): the first
    /// usable key from GetAllAPIKeys, trimmed and skipping blanks.
    /// </summary>
    internal static string ResolveApiKey(ChannelRecord channel)
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

    /// <summary>Port of (*OpenCodeGoQuotaChecker).parseResponse (opencode_go_checker.go:113-210).</summary>
    internal static QuotaData ParseResponse(byte[] body, DateTimeOffset now)
    {
        OpenCodeGoUsageResponse parsed;
        try
        {
            parsed = ParseUsageResponse(body);
        }
        catch (JsonException ex)
        {
            throw GoErrors.Wrap(ex, ParseResponseErrorPrefix);
        }

        var windows = new Dictionary<string, OpenCodeGoNormalizedWindow>(StringComparer.Ordinal);
        foreach (var (key, window) in new (string Key, OpenCodeGoUsageWindow? Window)[]
                 {
                     ("rolling", parsed.Usage.Rolling),
                     ("weekly", parsed.Usage.Weekly),
                     ("monthly", parsed.Usage.Monthly),
                 })
        {
            if (window is null || window.ResetsAt is null)
            {
                continue;
            }

            var resetAt = ParseResetsAt(window.ResetsAt);
            if (resetAt is null)
            {
                continue;
            }

            var resetInSec = (resetAt.Value - now).TotalSeconds;
            if (resetInSec < 0)
            {
                resetInSec = 0;
            }

            windows[key] = new OpenCodeGoNormalizedWindow
            {
                UsagePercent = window.Percent,
                ResetInSec = resetInSec,
                ResetAt = resetAt.Value,
                ApiSubStatus = window.Status,
            };
        }

        if (windows.Count == 0)
        {
            throw new InvalidOperationException("could not parse OpenCode Go usage windows");
        }

        var rawWindows = new Dictionary<string, object?>(StringComparer.Ordinal);
        var limits = new List<QuotaLimitStatus>(windows.Count);
        var normalizedStatus = ProviderQuotaStatuses.Available;
        DateTimeOffset? nextResetAt = null;

        foreach (var key in WindowOrder)
        {
            if (!windows.TryGetValue(key, out var window))
            {
                continue;
            }

            var usageRatio = window.UsagePercent / 100.0;
            var status = NormalizeWindowStatus(usageRatio);
            if (QuotaStatusRank(status) > QuotaStatusRank(normalizedStatus))
            {
                normalizedStatus = status;
            }

            var resetAt = window.ResetAt;
            if (nextResetAt is null || resetAt < nextResetAt)
            {
                nextResetAt = resetAt;
            }

            var rawWindow = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["usage_percent"] = window.UsagePercent,
                ["reset_in_seconds"] = window.ResetInSec,
                ["reset_time"] = FormatRfc3339(resetAt),
                ["status"] = status,
                ["percent_remaining"] = 100 - window.UsagePercent,
            };
            if (window.ApiSubStatus.Length != 0)
            {
                rawWindow["api_status"] = window.ApiSubStatus;
            }

            rawWindows[key] = rawWindow;

            limits.Add(new QuotaLimitStatus
            {
                Type = QuotaLimitTypes.Token,
                Status = status,
                UsageRatio = usageRatio,
                Ready = ProviderQuotaStatuses.IsReadyStatus(status),
                NextResetAt = resetAt,
                Window = WindowLabel(key),
                PeriodStart = PeriodStart(key, resetAt),
            });
        }

        return ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = normalizedStatus,
                ProviderType = ProviderType,
                RawData = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["plan_type"] = "go",
                    ["windows"] = rawWindows,
                },
                NextResetAt = nextResetAt,
                Ready = ProviderQuotaStatuses.IsReadyStatus(normalizedStatus),
                Limits = limits,
            },
            now);
    }

    /// <summary>
    /// Port of parseOpenCodeGoResetsAt (opencode_go_checker.go:215-246).
    /// Returns null where the reference returns false.
    /// </summary>
    internal static DateTimeOffset? ParseResetsAt(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(raw);
        }
        catch (JsonException)
        {
            // Go's json.Unmarshal into string and then float64 both fail.
            return null;
        }

        using var owned = document;
        var element = owned.RootElement;

        if (element.ValueKind == JsonValueKind.String)
        {
            return GoTimeParsing.TryParseRfc3339(element.GetString() ?? string.Empty);
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDouble(out var seconds))
        {
            return null;
        }

        // json.Unmarshal already rejects NaN/Infinity tokens, so only a sane
        // magnitude guard is needed here. 1e15 is well beyond any plausible
        // timestamp in either unit.
        if (seconds < 0 || seconds > 1e15)
        {
            return null;
        }

        // >= 1e12 is epoch milliseconds (year ~33,658 in seconds); seconds
        // never legitimately reach that magnitude.
        return seconds >= 1e12
            ? FromUnixMilliseconds((long)seconds)
            : FromUnixSeconds((long)seconds);
    }

    /// <summary>Port of opencodeGoWindowLabel (opencode_go_checker.go:251-262).</summary>
    internal static string WindowLabel(string key) => key switch
    {
        "rolling" => QuotaWindows.FiveHours,
        "weekly" => QuotaWindows.Weekly,
        "monthly" => QuotaWindows.Monthly,
        _ => key,
    };

    /// <summary>Port of opencodeGoPeriodStart (opencode_go_checker.go:268-279).</summary>
    internal static DateTimeOffset? PeriodStart(string key, DateTimeOffset resetAt) => key switch
    {
        "rolling" => ProviderQuotaData.PeriodStartFromReset(resetAt, TimeSpan.FromHours(5)),
        "weekly" => ProviderQuotaData.PeriodStartFromReset(resetAt, TimeSpan.FromDays(7)),
        "monthly" => ProviderQuotaData.PeriodStartFromMonthlyReset(resetAt),
        _ => null,
    };

    /// <summary>Port of normalizeOpenCodeGoWindowStatus (opencode_go_checker.go:281-289).</summary>
    internal static string NormalizeWindowStatus(double usageRatio)
    {
        if (usageRatio >= 1.0)
        {
            return ProviderQuotaStatuses.Exhausted;
        }

        if (usageRatio >= ProviderQuotaData.WarningThresholdRatio)
        {
            return ProviderQuotaStatuses.Warning;
        }

        return ProviderQuotaStatuses.Available;
    }

    /// <summary>Port of quotaStatusRank (opencode_go_checker.go:291-302).</summary>
    internal static int QuotaStatusRank(string status) => status switch
    {
        ProviderQuotaStatuses.Exhausted => 2,
        ProviderQuotaStatuses.Warning => 1,
        ProviderQuotaStatuses.Available => 0,
        _ => -1,
    };

    /// <summary>Go <c>time.Format(time.RFC3339)</c>: seconds precision, Z for a zero offset.</summary>
    internal static string FormatRfc3339(DateTimeOffset value)
    {
        var text = value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return value.Offset == TimeSpan.Zero
            ? text + "Z"
            : text + value.ToString("zzz", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Port of the <c>json.Unmarshal(body, &amp;parsed)</c> call at
    /// opencode_go_checker.go:115.
    /// </summary>
    /// <exception cref="JsonException">
    /// The payload is not valid JSON or a field has the wrong JSON type; the
    /// message follows Go's UnmarshalTypeError text, while malformed JSON keeps
    /// the System.Text.Json wording (C003).
    /// </exception>
    internal static OpenCodeGoUsageResponse ParseUsageResponse(byte[] data)
    {
        using var document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        var root = document.RootElement;

        // Go: unmarshaling JSON null into a struct is a no-op.
        if (root.ValueKind == JsonValueKind.Null)
        {
            return new OpenCodeGoUsageResponse();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoKindName(root.ValueKind)
                + " into Go value of type provider_quota.opencodeGoUsageResponse");
        }

        var response = new OpenCodeGoUsageResponse();
        foreach (var property in root.EnumerateObject())
        {
            if (FoldAscii(property.Name) != "usage")
            {
                continue;
            }

            ParseWindowsValue(property.Value, response.Usage, "usage");
        }

        return response;
    }

    private static void ParseWindowsValue(
        JsonElement value,
        OpenCodeGoUsageWindows target,
        string path)
    {
        // A struct field is not a pointer: null is a no-op.
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoKindName(value.ValueKind)
                + " into Go struct field opencodeGoUsageResponse." + path
                + " of type provider_quota.opencodeGoUsageWindows");
        }

        foreach (var property in value.EnumerateObject())
        {
            var fieldPath = path + "." + property.Name;
            switch (FoldAscii(property.Name))
            {
                case "rolling":
                    target.Rolling = ParseWindowValue(property.Value, target.Rolling, fieldPath);
                    break;
                case "weekly":
                    target.Weekly = ParseWindowValue(property.Value, target.Weekly, fieldPath);
                    break;
                case "monthly":
                    target.Monthly = ParseWindowValue(property.Value, target.Monthly, fieldPath);
                    break;
            }
        }
    }

    private static OpenCodeGoUsageWindow? ParseWindowValue(
        JsonElement value,
        OpenCodeGoUsageWindow? current,
        string path)
    {
        // A pointer field: null resets it and an object merges into it.
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw UnmarshalError(
                "json: cannot unmarshal " + GoKindName(value.ValueKind)
                + " into Go struct field opencodeGoUsageWindows." + path
                + " of type provider_quota.opencodeGoUsageWindow");
        }

        var window = current ?? new OpenCodeGoUsageWindow();
        foreach (var property in value.EnumerateObject())
        {
            var fieldPath = path + "." + property.Name;
            switch (FoldAscii(property.Name))
            {
                case "percent":
                    // null into a float64 field is a no-op.
                    if (property.Value.ValueKind == JsonValueKind.Null)
                    {
                        break;
                    }

                    if (property.Value.ValueKind != JsonValueKind.Number)
                    {
                        throw UnmarshalError(
                            "json: cannot unmarshal " + GoKindName(property.Value.ValueKind)
                            + " into Go struct field opencodeGoUsageWindow." + fieldPath
                            + " of type float64");
                    }

                    // System.Text.Json maps literals outside double's range to
                    // ±Infinity and reports success; Go's strconv.ParseFloat
                    // fails with a range error, which encoding/json surfaces as
                    // an UnmarshalTypeError naming the raw literal.
                    if (!property.Value.TryGetDouble(out var percent)
                        || !double.IsFinite(percent))
                    {
                        throw UnmarshalError(
                            "json: cannot unmarshal number " + property.Value.GetRawText()
                            + " into Go struct field opencodeGoUsageWindow." + fieldPath
                            + " of type float64");
                    }

                    window.Percent = percent;
                    break;
                case "resetsat":
                    // json.RawMessage keeps the value verbatim, including null.
                    window.ResetsAt = property.Value.GetRawText();
                    break;
                case "status":
                    if (property.Value.ValueKind == JsonValueKind.Null)
                    {
                        break;
                    }

                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        throw UnmarshalError(
                            "json: cannot unmarshal " + GoKindName(property.Value.ValueKind)
                            + " into Go struct field opencodeGoUsageWindow." + fieldPath
                            + " of type string");
                    }

                    window.Status = property.Value.GetString() ?? string.Empty;
                    break;
            }
        }

        return window;
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

    private static DateTimeOffset? FromUnixSeconds(long seconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Go's time.Time spans years far outside DateTimeOffset's range;
            // the port has no representable value (C123).
            return null;
        }
    }

    private static DateTimeOffset? FromUnixMilliseconds(long milliseconds)
    {
        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
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

/// <summary>Port of opencodeGoUsageResponse (opencode_go_checker.go:23-25).</summary>
internal sealed class OpenCodeGoUsageResponse
{
    public OpenCodeGoUsageWindows Usage { get; set; } = new();
}

/// <summary>Port of opencodeGoUsageWindows (opencode_go_checker.go:27-31).</summary>
internal sealed class OpenCodeGoUsageWindows
{
    public OpenCodeGoUsageWindow? Rolling { get; set; }

    public OpenCodeGoUsageWindow? Weekly { get; set; }

    public OpenCodeGoUsageWindow? Monthly { get; set; }
}

/// <summary>Port of opencodeGoUsageWindow (opencode_go_checker.go:33-41).</summary>
internal sealed class OpenCodeGoUsageWindow
{
    public double Percent { get; set; }

    /// <summary>The verbatim JSON text of <c>resetsAt</c> (Go's json.RawMessage).</summary>
    public string? ResetsAt { get; set; }

    public string Status { get; set; } = string.Empty;
}

/// <summary>Port of OpenCodeGoUsageWindow (opencode_go_checker.go:45-50).</summary>
internal sealed class OpenCodeGoNormalizedWindow
{
    public double UsagePercent { get; set; }

    public double ResetInSec { get; set; }

    public DateTimeOffset ResetAt { get; set; }

    public string ApiSubStatus { get; set; } = string.Empty;
}
