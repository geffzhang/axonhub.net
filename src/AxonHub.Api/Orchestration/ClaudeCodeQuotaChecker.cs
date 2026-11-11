using System.Text;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Anthropic.ClaudeCode;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the Claude Code quota checker
/// (internal/server/biz/provider_quota/claudecode_checker.go). The checker
/// spends a one-token Haiku request on the channel's messages endpoint and
/// reads the unified rate-limit headers from the response; the response body
/// itself is ignored. Only channel-level platform quotas exist, so resetting is
/// not supported.
/// </summary>
public sealed class ClaudeCodeQuotaChecker : IQuotaChecker
{
    /// <summary>Port of the default endpoint in getEndpointURL (claudecode_checker.go:238-250).</summary>
    internal const string DefaultQuotaUrl = "https://api.anthropic.com/v1/messages";

    private const string ProviderType = "claudecode";

    private static readonly string[] WindowKeys = ["5h", "7d"];

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;

    public ClaudeCodeQuotaChecker(
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

    /// <summary>Port of (*ClaudeCodeQuotaChecker).CheckQuota (claudecode_checker.go:26-91).</summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // Verify credentials (claudecode_checker.go:28-30). The legacy apiKey
        // may itself hold an OAuth JSON document, so either form is enough.
        var oauthJson = ChannelCredentials.GetOAuthObjectJson(channel.Credentials);
        var legacyApiKey = ChannelCredentials.GetLegacyApiKey(channel.Credentials);
        if (oauthJson is null && legacyApiKey.Trim().Length == 0)
        {
            throw new InvalidOperationException("channel has no credentials");
        }

        // Parse OAuth credentials from apiKey JSON (claudecode_checker.go:32-43).
        // An OAuth object wins even when its access token is empty; the legacy
        // apiKey is only consulted when the object is absent.
        string accessToken;
        if (oauthJson is not null)
        {
            accessToken = OAuthCredentialsJson.ParseOAuthObject(oauthJson).AccessToken;
        }
        else
        {
            OAuthCredentials credentials;
            try
            {
                credentials = OAuthCredentialsJson.ParseCredentialsJson(legacyApiKey);
            }
            catch (Exception ex)
            {
                throw GoErrors.Wrap(ex, "failed to parse OAuth credentials: ");
            }

            accessToken = credentials.AccessToken;
        }

        if (accessToken.Length == 0)
        {
            throw new InvalidOperationException("channel credentials missing access token");
        }

        // Build HTTP request using Bearer auth like ClaudeCode transformers
        // (claudecode_checker.go:49-72).
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "anthropic-beta", ClaudeCodeConstants.QuotaCheckHeader);
        HttpClientUtils.HeaderSet(headers, "anthropic-version", ClaudeCodeConstants.VersionHeader);
        HttpClientUtils.HeaderSet(
            headers,
            "anthropic-dangerous-direct-browser-access",
            ClaudeCodeConstants.BrowserAccessHeader);
        HttpClientUtils.HeaderSet(headers, "x-app", ClaudeCodeConstants.AppHeader);
        HttpClientUtils.HeaderSet(headers, "content-type", "application/json");

        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = ClaudeCodeConstants.QuotaCheckModel,
            ["messages"] = new List<Dictionary<string, object?>>
            {
                new(StringComparer.Ordinal)
                {
                    ["role"] = "user",
                    ["content"] = "limit",
                },
            },
            ["max_tokens"] = 1,
        };

        var request = new HttpRequestData
        {
            Method = "POST",
            Url = GetEndpointUrl(channel.BaseUrl),
            Headers = headers,
            Body = AxonHub.Llm.Json.GoJson.Marshal(body),
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = accessToken,
            },
        };

        // Use proxy-configured HTTP client if available
        // (claudecode_checker.go:74-78).
        var executor = _executor;
        if (ParseSettingsLenient(channel.Settings)?.Proxy is { } proxy)
        {
            executor = _proxyExecutors.Create(ToProxyConfig(proxy));
        }

        HttpResponseData response;
        try
        {
            response = executor.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "HTTP request failed: ");
        }

        // Check HTTP status (claudecode_checker.go:85-88). The executor already
        // reports 4xx/5xx through HttpClientError, so this branch only sees
        // non-OK statuses below 400 (redirects, empty 2xx variants).
        if (response.StatusCode != 200)
        {
            throw new InvalidOperationException(
                "HTTP " + response.StatusCode + ": "
                + Encoding.UTF8.GetString(response.Body ?? []));
        }

        return Task.FromResult(ParseResponse(response.Headers, _timeProvider.GetUtcNow()));
    }

    /// <summary>Port of (*ClaudeCodeQuotaChecker).SupportsChannel (claudecode_checker.go:189-191).</summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Type == ChannelOutboundFactory.ClaudeCodeChannelType;
    }

    /// <summary>
    /// Port of (*ClaudeCodeQuotaChecker).parseResponse (claudecode_checker.go:93-187):
    /// the unified status plus the per-window utilization/reset headers become
    /// the normalized quota, and every raw header value is preserved under
    /// <c>rawData</c> with the reference's key names.
    /// </summary>
    internal static QuotaData ParseResponse(
        Dictionary<string, List<string>> headers,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(headers);

        // Guard clause - early return if no quota headers
        // (claudecode_checker.go:94-97).
        var unifiedStatus = HttpClientUtils.HeaderGet(headers, "Anthropic-Ratelimit-Unified-Status");
        if (unifiedStatus.Length == 0)
        {
            throw new InvalidOperationException("missing quota headers");
        }

        var representativeClaim = HttpClientUtils.HeaderGet(
            headers,
            "Anthropic-Ratelimit-Unified-Representative-Claim");

        // Parse window data (claudecode_checker.go:102-119).
        var windows = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["5h"] = ParseWindow(headers, "5h"),
            ["7d"] = ParseWindow(headers, "7d"),
            ["overage"] = ParseWindow(headers, "overage"),
        };

        var rawData = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["unified_status"] = unifiedStatus,
            ["windows"] = windows,
            ["representative_claim"] = representativeClaim,
            ["fallback"] = HttpClientUtils.HeaderGet(headers, "Anthropic-Ratelimit-Unified-Fallback"),
            ["fallback_percentage"] = ParseFloat(
                HttpClientUtils.HeaderGet(headers, "Anthropic-Ratelimit-Unified-Fallback-Percentage")),
            ["reset"] = ParseUnixTimestamp(
                HttpClientUtils.HeaderGet(headers, "Anthropic-Ratelimit-Unified-Reset")),
        };

        // Normalize status: allowed -> available, throttled/rejected -> exhausted
        // (claudecode_checker.go:130-150).
        var normalizedStatus = unifiedStatus switch
        {
            "allowed" => ProviderQuotaStatuses.Available,
            "throttled" or "rejected" => ProviderQuotaStatuses.Exhausted,
            _ => ProviderQuotaStatuses.Unknown,
        };

        // Check for warning state (utilization >= 80% on any window). The
        // overage bucket never raises the channel-wide warning by itself.
        if (normalizedStatus == ProviderQuotaStatuses.Available)
        {
            var fiveHourUtilization = ParseFloat(
                HttpClientUtils.HeaderGet(headers, "Anthropic-Ratelimit-Unified-5h-Utilization"));
            var sevenDayUtilization = ParseFloat(
                HttpClientUtils.HeaderGet(headers, "Anthropic-Ratelimit-Unified-7d-Utilization"));
            if (fiveHourUtilization >= ProviderQuotaData.WarningThresholdRatio
                || sevenDayUtilization >= ProviderQuotaData.WarningThresholdRatio)
            {
                normalizedStatus = ProviderQuotaStatuses.Warning;
            }
        }

        // Extract next reset time based on representative claim
        // (claudecode_checker.go:152-169): "five_hour" -> "5h",
        // "seven_day" -> "7d", anything else is looked up verbatim.
        var windowKey = representativeClaim switch
        {
            "five_hour" => "5h",
            "seven_day" => "7d",
            _ => representativeClaim,
        };

        DateTimeOffset? nextResetAt = null;
        if (windows.TryGetValue(windowKey, out var windowValue)
            && windowValue is Dictionary<string, object?> resetWindow
            && resetWindow.TryGetValue("reset", out var resetValue)
            && resetValue is long resetTs
            && resetTs > 0)
        {
            nextResetAt = FromUnixSeconds(resetTs);
        }

        // Limits are built per measured window (claudecode_checker.go:171-177).
        var limits = new List<QuotaLimitStatus>(2);
        foreach (var key in WindowKeys)
        {
            if (TryBuildTokenLimit(headers, key, out var limit))
            {
                limits.Add(limit);
            }
        }

        return ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = normalizedStatus,
                ProviderType = ProviderType,
                RawData = rawData,
                NextResetAt = nextResetAt,
                Ready = ProviderQuotaStatuses.IsReadyStatus(normalizedStatus),
                Limits = limits,
            },
            now);
    }

    /// <summary>Port of getEndpointURL (claudecode_checker.go:238-250).</summary>
    internal static string GetEndpointUrl(string? baseUrl)
    {
        var url = baseUrl ?? string.Empty;
        if (url.Length == 0)
        {
            return DefaultQuotaUrl;
        }

        // Go: strings.TrimSuffix(baseURL, "/") removes exactly one slash.
        if (url.EndsWith('/'))
        {
            url = url[..^1];
        }

        return url.EndsWith("/v1", StringComparison.Ordinal)
            ? url + "/messages"
            : url + "/v1/messages";
    }

    /// <summary>
    /// Port of parseUnixTimestamp (claudecode_checker.go:252-255):
    /// <c>strconv.ParseInt(s, 10, 64)</c> with the error ignored, so a syntax
    /// error yields 0 while a range error yields the clamped int64 limit.
    /// </summary>
    internal static long ParseUnixTimestamp(string value)
    {
        if (GoCast.TryParseInt64(value, out var parsed))
        {
            return parsed;
        }

        // Distinguish the two Go failure modes: ParseInt returns 0 for a syntax
        // error but the saturated value together with ErrRange, which the
        // reference discards.
        var index = 0;
        var negative = false;
        if (value.Length > 0 && value[0] is '+' or '-')
        {
            negative = value[0] == '-';
            index = 1;
        }

        if (index == value.Length)
        {
            return 0;
        }

        for (var i = index; i < value.Length; i++)
        {
            if (value[i] is < '0' or > '9')
            {
                return 0;
            }
        }

        return negative ? long.MinValue : long.MaxValue;
    }

    /// <summary>Port of parseFloat (claudecode_checker.go:257-260).</summary>
    internal static double ParseFloat(string value) => GoCast.ParseFloatIgnoringError(value);

    private static Dictionary<string, object?> ParseWindow(
        Dictionary<string, List<string>> headers,
        string windowKey) => new(StringComparer.Ordinal)
        {
            ["status"] = HttpClientUtils.HeaderGet(
                headers,
                "Anthropic-Ratelimit-Unified-" + windowKey + "-Status"),
            ["reset"] = ParseUnixTimestamp(HttpClientUtils.HeaderGet(
                headers,
                "Anthropic-Ratelimit-Unified-" + windowKey + "-Reset")),
            ["utilization"] = ParseFloat(HttpClientUtils.HeaderGet(
                headers,
                "Anthropic-Ratelimit-Unified-" + windowKey + "-Utilization")),
        };

    /// <summary>
    /// Port of (*ClaudeCodeQuotaChecker).buildTokenLimit (claudecode_checker.go:193-236).
    /// A window is reported only when at least one of its three headers is
    /// present; a completely absent window is not invented.
    /// </summary>
    private static bool TryBuildTokenLimit(
        Dictionary<string, List<string>> headers,
        string windowKey,
        out QuotaLimitStatus limit)
    {
        string utilizationKey;
        string resetKey;
        TimeSpan window;
        switch (windowKey)
        {
            case "5h":
                utilizationKey = "Anthropic-Ratelimit-Unified-5h-Utilization";
                resetKey = "Anthropic-Ratelimit-Unified-5h-Reset";
                window = TimeSpan.FromHours(5);
                break;
            case "7d":
                utilizationKey = "Anthropic-Ratelimit-Unified-7d-Utilization";
                resetKey = "Anthropic-Ratelimit-Unified-7d-Reset";
                window = TimeSpan.FromDays(7);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(windowKey), windowKey, null);
        }

        if (HttpClientUtils.HeaderGet(headers, utilizationKey).Length == 0
            && HttpClientUtils.HeaderGet(headers, resetKey).Length == 0
            && HttpClientUtils.HeaderGet(
                headers,
                "Anthropic-Ratelimit-Unified-" + windowKey + "-Status").Length == 0)
        {
            limit = null!;
            return false;
        }

        var utilization = ParseFloat(HttpClientUtils.HeaderGet(headers, utilizationKey));
        var resetTs = ParseUnixTimestamp(HttpClientUtils.HeaderGet(headers, resetKey));

        var status = ProviderQuotaStatuses.Available;
        if (utilization >= 1.0)
        {
            status = ProviderQuotaStatuses.Exhausted;
        }
        else if (utilization >= ProviderQuotaData.WarningThresholdRatio)
        {
            status = ProviderQuotaStatuses.Warning;
        }

        DateTimeOffset? nextReset = null;
        if (resetTs > 0)
        {
            nextReset = FromUnixSeconds(resetTs);
        }

        limit = ProviderQuotaData.WithWindow(
            ProviderQuotaData.NewTokenLimitStatus(status, utilization, nextReset),
            windowKey,
            window);
        return true;
    }

    /// <summary>
    /// Go's <c>time.Unix(seconds, 0)</c> projected onto
    /// <see cref="DateTimeOffset"/>. Go can represent years outside 1..9999;
    /// those resets become null in the port (difference C123 item 2).
    /// </summary>
    private static DateTimeOffset? FromUnixSeconds(long seconds)
        => seconds is < -62_135_596_800 or > 253_402_300_799
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(seconds);

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
