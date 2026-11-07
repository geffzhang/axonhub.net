using System.Globalization;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Antigravity;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the Antigravity quota checker
/// (internal/server/biz/provider_quota/antigravity_checker.go and
/// antigravity_summary.go). The checker first asks the quota-summary endpoint,
/// which reports independent 5h/7d windows for the Gemini and Claude/GPT pools,
/// and only falls back to the legacy per-model session quotas when the summary
/// API is unavailable (404/501) or its body cannot be parsed. Resetting is not
/// supported by this provider.
/// </summary>
public sealed class AntigravityQuotaChecker : IQuotaChecker
{
    /// <summary>Port of antigravityQuotaURL (antigravity_checker.go:22).</summary>
    internal const string QuotaUrl = AntigravityConstants.EndpointProd + "/v1internal:fetchAvailableModels";

    /// <summary>Port of antigravityQuotaSummaryURL (antigravity_checker.go:23).</summary>
    internal const string QuotaSummaryUrl =
        AntigravityConstants.EndpointProd + "/v1internal:retrieveUserQuotaSummary";

    private const string ProviderType = "antigravity";

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;

    public AntigravityQuotaChecker(
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

    /// <summary>Port of (*AntigravityQuotaChecker).CheckQuota (antigravity_checker.go:44-96).</summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // provider_quota.go:46-48: a per-channel proxy replaces the shared
        // client for both the token refresh and the quota requests.
        var executor = _executor;
        if (ParseSettingsLenient(channel.Settings)?.Proxy is { } proxy)
        {
            executor = _proxyExecutors.Create(ToProxyConfig(proxy));
        }

        var (accessToken, projectId) = ResolveCredentials(channel, executor, cancellationToken);
        var now = _timeProvider.GetUtcNow();

        var body = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (projectId.Length > 0)
        {
            body["project"] = projectId;
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        AntigravityVersion.SetClientHeaders(headers);

        var request = new HttpRequestData
        {
            Method = "POST",
            Url = QuotaSummaryUrl,
            Headers = headers,
            Body = GoJson.Marshal(body),
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = accessToken,
            },
        };

        HttpResponseData? response = null;
        try
        {
            response = executor.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            // Only an upstream 404/501 marks a server without the summary API;
            // everything else is reported as-is.
            var upstreamError = GoErrors.As<HttpClientError>(ex);
            if (upstreamError is null
                || (upstreamError.StatusCode != 404 && upstreamError.StatusCode != 501))
            {
                throw GoErrors.Wrap(ex, "fetch Antigravity quota summary: ");
            }
        }

        if (response is not null)
        {
            if (response.StatusCode == 200)
            {
                try
                {
                    return Task.FromResult(ParseQuotaSummary(response.Body ?? [], now));
                }
                catch (Exception)
                {
                    // Older servers answer 200 with a shape this parser cannot
                    // read; the reference falls through silently.
                }
            }
            else if (response.StatusCode != 404 && response.StatusCode != 501)
            {
                throw new InvalidOperationException(
                    "fetch Antigravity quota summary: status " + response.StatusCode);
            }
        }

        // Older servers expose only per-model session quotas. Never infer weekly
        // allowances from those responses or label model IDs as time windows.
        request.Url = QuotaUrl;

        try
        {
            response = executor.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "fetch legacy Antigravity quota: ");
        }

        if (response.StatusCode != 200)
        {
            throw new InvalidOperationException(
                "fetch legacy Antigravity quota: status " + response.StatusCode);
        }

        return Task.FromResult(ParseQuota(response.Body ?? [], now));
    }

    /// <summary>Port of (*AntigravityQuotaChecker).SupportsChannel (antigravity_checker.go:98-100).</summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Type == ChannelOutboundFactory.AntigravityChannelType;
    }

    /// <summary>
    /// Port of (*AntigravityQuotaChecker).credentials (antigravity_checker.go:102-149):
    /// the legacy <c>refreshToken|projectID</c> api key seeds the OAuth
    /// credentials, and an expired access token is refreshed through the shared
    /// token provider.
    /// </summary>
    private (string AccessToken, string ProjectId) ResolveCredentials(
        ChannelRecord channel,
        IExecutor executor,
        CancellationToken cancellationToken)
    {
        // Go: strings.SplitN(strings.TrimSpace(APIKey), "|", 2).
        var legacy = ChannelCredentials.GetLegacyApiKey(channel.Credentials).Trim();
        var legacyParts = legacy.Split('|', 2);
        var legacyRefreshToken = legacyParts.Length > 0 ? legacyParts[0] : string.Empty;
        var projectId = legacyParts.Length == 2 ? legacyParts[1] : string.Empty;

        var oauthJson = ChannelCredentials.GetOAuthObjectJson(channel.Credentials);
        var oauth = oauthJson is null ? null : OAuthCredentialsJson.ParseOAuthObject(oauthJson);

        if (oauth is null)
        {
            oauth = new OAuthCredentials();
        }
        else if (oauth.AccessToken.Length > 0
            && (!oauth.IsExpired(_timeProvider.GetUtcNow())
                || (oauth.RefreshToken.Length == 0 && legacyRefreshToken.Length == 0)))
        {
            return (oauth.AccessToken, projectId);
        }

        var credentials = oauth.Clone();
        if (credentials.RefreshToken.Length == 0)
        {
            credentials.RefreshToken = legacyRefreshToken;
        }

        if (credentials.ClientId.Length == 0)
        {
            credentials.ClientId = AntigravityConstants.ClientID;
        }

        if (credentials.Scopes is not { Count: > 0 })
        {
            credentials.Scopes = [.. AntigravityConstants.Scopes];
        }

        if (credentials.RefreshToken.Length == 0)
        {
            throw new InvalidOperationException("channel has no Antigravity OAuth credentials");
        }

        var tokenProvider = AntigravityTokenProviderFactory.NewTokenProvider(new TokenProviderParams
        {
            Credentials = credentials,
            HttpClient = executor,
        });

        OAuthCredentials refreshed;
        try
        {
            refreshed = tokenProvider.Get(cancellationToken);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "refresh Antigravity OAuth token: ");
        }

        return (refreshed.AccessToken, projectId);
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

    /// <summary>
    /// Port of parseAntigravityQuota (antigravity_checker.go:151-231): the
    /// legacy <c>fetchAvailableModels</c> response only reports one session
    /// quota per model, so effort variants collapse into one conservative 5h
    /// limit per pool and no weekly limit is invented.
    /// </summary>
    internal static QuotaData ParseQuota(byte[] body, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(body);

        AntigravityQuotaResponse? response;
        try
        {
            response = LlmJson.Deserialize<AntigravityQuotaResponse>(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("decode Antigravity quota response: " + ex.Message, ex);
        }

        if (response?.Models is not { Count: > 0 } decodedModels)
        {
            throw new InvalidOperationException("Antigravity quota response has no models");
        }

        var models = new Dictionary<string, object?>(StringComparer.Ordinal);
        var maxRemaining = 0.0;
        DateTimeOffset? nextResetAt = null;
        var poolLimits = new Dictionary<string, QuotaLimitStatus>(StringComparer.Ordinal);

        var modelIds = new List<string>(decodedModels.Count);
        foreach (var modelId in decodedModels.Keys)
        {
            modelIds.Add(modelId);
        }

        // Go sorts the model IDs ordinally; the order decides which effort
        // variant wins a usage-ratio tie.
        modelIds.Sort(StringComparer.Ordinal);

        foreach (var modelId in modelIds)
        {
            var model = decodedModels[modelId];
            if (model is null
                || model.IsInternal == true
                || model.QuotaInfo is null
                || model.QuotaInfo.RemainingFraction is not { } fraction)
            {
                continue;
            }

            if (!ProviderQuotaData.IsFiniteRatio(fraction) || fraction < 0)
            {
                continue;
            }

            var remaining = Math.Max(0, Math.Min(1, fraction));
            var usageRatio = 1 - remaining;
            var status = QuotaStatus(usageRatio);
            var resetAt = ParseResetTime(model.QuotaInfo.ResetTime);
            if (resetAt is { } reset && (nextResetAt is null || reset < nextResetAt.Value))
            {
                nextResetAt = reset;
            }

            var modelData = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["displayName"] = model.DisplayName ?? string.Empty,
                ["remainingPercentage"] = remaining * 100,
                ["status"] = status,
            };
            if (resetAt is { } resetValue)
            {
                modelData["resetAt"] = ProviderQuotaService.FormatRfc3339(resetValue);
            }

            models[modelId] = modelData;

            var pool = ProviderQuotaModelFilter.AntigravityModelPool(modelId);
            if (pool.Length == 0)
            {
                continue;
            }

            var window = pool + "_5h";
            if (!poolLimits.TryGetValue(window, out var current) || usageRatio > current.UsageRatio)
            {
                var limit = ProviderQuotaData.WithWindow(
                    ProviderQuotaData.NewTokenLimitStatus(status, usageRatio, resetAt),
                    window,
                    TimeSpan.FromHours(5));
                limit.AvailabilityGroup = ProviderQuotaModelFilter.AntigravityPoolAvailabilityGroup(pool);
                poolLimits[window] = limit;
            }
        }

        if (models.Count == 0)
        {
            throw new InvalidOperationException("Antigravity quota response has no quota data");
        }

        var limits = new List<QuotaLimitStatus>(2);
        foreach (var pool in AntigravityPools)
        {
            if (poolLimits.TryGetValue(pool + "_5h", out var limit))
            {
                limits.Add(limit);
                maxRemaining = Math.Max(maxRemaining, 1 - limit.UsageRatio);
            }
        }

        if (limits.Count == 0)
        {
            throw new InvalidOperationException(
                "Antigravity quota response has no recognized model pools");
        }

        var overallStatus = QuotaStatus(1 - maxRemaining);

        return ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = overallStatus,
                ProviderType = ProviderType,
                RawData = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["models"] = models,
                },
                NextResetAt = nextResetAt,
                Ready = ProviderQuotaStatuses.IsReadyStatus(overallStatus),
                Limits = limits,
            },
            now);
    }

    /// <summary>
    /// Port of parseAntigravityQuotaSummary (antigravity_summary.go:100-169):
    /// one limit per pool and cadence, preserving the upstream payload for
    /// diagnosis. The embedded summary is decoded from the top level with
    /// <c>response</c> taking precedence over <c>summary</c>.
    /// </summary>
    internal static QuotaData ParseQuotaSummary(byte[] body, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(body);

        AntigravitySummaryEnvelope? response;
        try
        {
            response = LlmJson.Deserialize<AntigravitySummaryEnvelope>(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("decode Antigravity quota summary: " + ex.Message, ex);
        }

        AntigravitySummary? summary = response;
        if (response?.Response is not null)
        {
            summary = response.Response;
        }
        else if (response?.Summary is not null)
        {
            summary = response.Summary;
        }

        var byWindow = new Dictionary<string, QuotaLimitStatus>(StringComparer.Ordinal);
        foreach (var group in summary?.Groups ?? new List<AntigravitySummaryGroup?>())
        {
            if (group is null)
            {
                continue;
            }

            foreach (var bucket in group.Buckets ?? new List<AntigravitySummaryBucket?>())
            {
                if (bucket is null)
                {
                    continue;
                }

                var id = bucket.BucketId ?? string.Empty;
                if (id.Length == 0)
                {
                    id = bucket.Id ?? string.Empty;
                }

                var pool = ProviderQuotaModelFilter.AntigravityModelPool(
                    id + " " + (group.DisplayName ?? string.Empty) + " " + (group.Name ?? string.Empty));
                var (window, duration) = SummaryWindow(
                    bucket.Window ?? string.Empty,
                    id,
                    bucket.DisplayName ?? string.Empty);

                double? remaining = bucket.RemainingFraction;
                if (remaining is null && bucket.Remaining is not null)
                {
                    remaining = bucket.Remaining.RemainingFraction;
                    if (remaining is null && bucket.Remaining.Case == "remainingFraction")
                    {
                        remaining = bucket.Remaining.Value;
                    }
                }

                if (bucket.Disabled == true
                    || pool.Length == 0
                    || window.Length == 0
                    || remaining is not { } remainingValue
                    || !ProviderQuotaData.IsFiniteRatio(remainingValue)
                    || remainingValue < 0
                    || remainingValue > 1)
                {
                    continue;
                }

                var key = pool + "_" + window;
                var usage = 1 - remainingValue;
                if (!byWindow.TryGetValue(key, out var existing) || usage > existing.UsageRatio)
                {
                    var limit = ProviderQuotaData.WithWindow(
                        ProviderQuotaData.NewTokenLimitStatus(
                            QuotaStatus(usage),
                            usage,
                            ParseResetTime(bucket.ResetTime)),
                        key,
                        duration);
                    limit.AvailabilityGroup =
                        ProviderQuotaModelFilter.AntigravityPoolAvailabilityGroup(pool);
                    byWindow[key] = limit;
                }
            }
        }

        var limits = new List<QuotaLimitStatus>(4);
        var bestRemaining = 0.0;
        foreach (var pool in AntigravityPools)
        {
            var poolRemaining = 1.0;
            var found = false;
            foreach (var window in AntigravityWindows)
            {
                if (byWindow.TryGetValue(pool + "_" + window, out var limit))
                {
                    limits.Add(limit);
                    poolRemaining = Math.Min(poolRemaining, 1 - limit.UsageRatio);
                    found = true;
                }
            }

            if (found)
            {
                bestRemaining = Math.Max(bestRemaining, poolRemaining);
            }
        }

        if (limits.Count == 0)
        {
            throw new InvalidOperationException(
                "Antigravity quota summary has no measured pool windows");
        }

        Dictionary<string, object?> raw;
        try
        {
            raw = ToGoObjectMap(body);
        }
        catch (Exception ex)
        {
            // Go has a single error return from json.Unmarshal; anything the
            // preservation decode cannot read is wrapped the same way.
            throw new InvalidOperationException(
                "preserve Antigravity quota summary: " + ex.Message,
                ex);
        }

        return ProviderQuotaData.Normalize(
            new QuotaData
            {
                Status = QuotaStatus(1 - bestRemaining),
                ProviderType = ProviderType,
                RawData = raw,
                Limits = limits,
            },
            now);
    }

    private static readonly string[] AntigravityPools = ["gemini", "claude_gpt"];

    private static readonly string[] AntigravityWindows = ["5h", "7d"];

    /// <summary>Port of antigravityQuotaStatus (antigravity_checker.go:233-241).</summary>
    internal static string QuotaStatus(double usageRatio)
    {
        if (usageRatio >= 1)
        {
            return ProviderQuotaStatuses.Exhausted;
        }

        if (usageRatio >= ProviderQuotaData.WarningThresholdRatio)
        {
            return ProviderQuotaStatuses.Warning;
        }

        return ProviderQuotaStatuses.Available;
    }

    /// <summary>
    /// Port of antigravityCadenceTokens (antigravity_summary.go:69-73): lower-case
    /// alphanumeric tokens; only complete tokens are matched later because
    /// substring matching misreads near misses such as "15h" or "biweekly".
    /// </summary>
    internal static List<string> CadenceTokens(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in value.ToLowerInvariant())
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                current.Append(ch);
                continue;
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>
    /// Port of antigravitySummaryWindow (antigravity_summary.go:76-97): only the
    /// cadences the provider names are recognized, never reset-time guesses.
    /// </summary>
    internal static (string Window, TimeSpan Duration) SummaryWindow(
        string window,
        string id,
        string displayName)
    {
        foreach (var value in new[] { window, id, displayName })
        {
            var tokens = CadenceTokens(value ?? string.Empty);
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token is "7d" or "week" or "weekly")
                {
                    // "bi-weekly" and "semi-weekly" name a different cadence.
                    if (i > 0 && tokens[i - 1] is "bi" or "semi")
                    {
                        continue;
                    }

                    return ("7d", TimeSpan.FromDays(7));
                }

                if (token is "5h" or "session")
                {
                    return ("5h", TimeSpan.FromHours(5));
                }

                if (i + 1 < tokens.Count
                    && token is "5" or "five"
                    && tokens[i + 1] == "hour")
                {
                    return ("5h", TimeSpan.FromHours(5));
                }

                if (i + 1 < tokens.Count
                    && token is "7" or "seven"
                    && tokens[i + 1] == "day")
                {
                    return ("7d", TimeSpan.FromDays(7));
                }
            }
        }

        return (string.Empty, TimeSpan.Zero);
    }

    /// <summary>
    /// Port of parseAntigravityResetTime (antigravity_checker.go:243-267). A
    /// string is first tried as a decimal Unix timestamp, then as RFC3339; a
    /// JSON number is a Unix timestamp (seconds, or milliseconds once it
    /// reaches 1e12). Anything else yields no reset time.
    /// </summary>
    internal static DateTimeOffset? ParseResetTime(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var text = value.GetString() ?? string.Empty;
                if (TryParseGoInt64(text, out var timestamp))
                {
                    return timestamp >= 1_000_000_000_000
                        ? FromUnixMilliseconds(timestamp)
                        : FromUnixSeconds(timestamp);
                }

                return GoTimeParsing.TryParseRfc3339(text);

            case JsonValueKind.Number:
                double number;
                try
                {
                    number = value.GetDouble();
                }
                catch (FormatException)
                {
                    // Go's decoder rejects a number outside float64's range
                    // before this point; the port keeps the model but drops the
                    // unrepresentable reset time (C123).
                    return null;
                }

                if (number >= 1_000_000_000_000)
                {
                    return FromUnixMilliseconds((long)number);
                }

                return FromUnixSeconds((long)number);

            default:
                return null;
        }
    }

    /// <summary>
    /// Go <c>strconv.ParseInt(s, 10, 64)</c>: an optional sign, then decimal
    /// digits only (no whitespace, base prefixes or underscores).
    /// </summary>
    private static bool TryParseGoInt64(string value, out long result)
    {
        result = 0;

        var start = 0;
        var negative = false;
        if (value.Length > 0 && (value[0] == '+' || value[0] == '-'))
        {
            negative = value[0] == '-';
            start = 1;
        }

        if (start >= value.Length)
        {
            return false;
        }

        for (var i = start; i < value.Length; i++)
        {
            if (value[i] is < '0' or > '9')
            {
                return false;
            }
        }

        if (!ulong.TryParse(
                value.AsSpan(start),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var magnitude))
        {
            return false;
        }

        if (negative)
        {
            const ulong minMagnitude = 9223372036854775808UL;
            if (magnitude > minMagnitude)
            {
                return false;
            }

            result = magnitude == minMagnitude ? long.MinValue : -(long)magnitude;
            return true;
        }

        if (magnitude > long.MaxValue)
        {
            return false;
        }

        result = (long)magnitude;
        return true;
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

    /// <summary>
    /// Port of <c>json.Unmarshal(body, &amp;map[string]any)</c>: the preserved
    /// payload keeps Go's value model (objects as maps, arrays as lists,
    /// numbers as float64) so re-marshalling matches the reference.
    /// </summary>
    private static Dictionary<string, object?> ToGoObjectMap(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal " + GoKindName(root.ValueKind)
                + " into Go value of type map[string]interface {}");
        }

        return ToGoMap(root);
    }

    private static Dictionary<string, object?> ToGoMap(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            // Go's decoder lets the last duplicate key win.
            result[property.Name] = ToGoValue(property.Value);
        }

        return result;
    }

    private static object? ToGoValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => ToGoMap(element),
        JsonValueKind.Array => ToGoList(element),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static List<object?> ToGoList(JsonElement element)
    {
        var result = new List<object?>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            result.Add(ToGoValue(item));
        }

        return result;
    }

    private static string GoKindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        _ => "null",
    };

    /// <summary>Port of antigravityQuotaResponse (antigravity_checker.go:25-34).</summary>
    private sealed class AntigravityQuotaResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("models")]
        public Dictionary<string, AntigravityQuotaModel?>? Models { get; set; }
    }

    /// <summary>One model entry of the legacy quota response.</summary>
    private sealed class AntigravityQuotaModel
    {
        [System.Text.Json.Serialization.JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("isInternal")]
        public bool? IsInternal { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("quotaInfo")]
        public AntigravityQuotaInfo? QuotaInfo { get; set; }
    }

    /// <summary>Port of the anonymous quotaInfo struct (antigravity_checker.go:29-32).</summary>
    private sealed class AntigravityQuotaInfo
    {
        [System.Text.Json.Serialization.JsonPropertyName("remainingFraction")]
        public double? RemainingFraction { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("resetTime")]
        public JsonElement ResetTime { get; set; }
    }

    /// <summary>Port of antigravitySummary (antigravity_summary.go:11-30).</summary>
    private class AntigravitySummary
    {
        [System.Text.Json.Serialization.JsonPropertyName("groups")]
        public List<AntigravitySummaryGroup?>? Groups { get; set; }
    }

    /// <summary>
    /// Port of the anonymous envelope in parseAntigravityQuotaSummary: the
    /// embedded summary decodes top-level <c>groups</c>, while <c>response</c>
    /// and <c>summary</c> wrap another copy of it.
    /// </summary>
    private sealed class AntigravitySummaryEnvelope : AntigravitySummary
    {
        [System.Text.Json.Serialization.JsonPropertyName("response")]
        public AntigravitySummary? Response { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("summary")]
        public AntigravitySummary? Summary { get; set; }
    }

    private sealed class AntigravitySummaryGroup
    {
        [System.Text.Json.Serialization.JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("name")]
        public string? Name { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("buckets")]
        public List<AntigravitySummaryBucket?>? Buckets { get; set; }
    }

    private sealed class AntigravitySummaryBucket
    {
        [System.Text.Json.Serialization.JsonPropertyName("bucketId")]
        public string? BucketId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public string? Id { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("window")]
        public string? Window { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("disabled")]
        public bool? Disabled { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("remainingFraction")]
        public double? RemainingFraction { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("remaining")]
        public AntigravitySummaryRemaining? Remaining { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("resetTime")]
        public JsonElement ResetTime { get; set; }
    }

    private sealed class AntigravitySummaryRemaining
    {
        [System.Text.Json.Serialization.JsonPropertyName("remainingFraction")]
        public double? RemainingFraction { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("case")]
        public string? Case { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public double? Value { get; set; }
    }
}
