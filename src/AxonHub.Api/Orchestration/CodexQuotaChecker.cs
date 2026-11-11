using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the Codex (ChatGPT backend) quota checker
/// (internal/server/biz/provider_quota/codex_checker.go). The checker reads the
/// account usage endpoint for the primary/secondary rate-limit windows and
/// implements the optional <see cref="IQuotaResetter"/> capability through the
/// reset-credit endpoints.
/// </summary>
public sealed class CodexQuotaChecker : IQuotaChecker, IQuotaResetter
{
    /// <summary>Port of the usage endpoint in CheckQuota (codex_checker.go:234).</summary>
    internal const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";

    /// <summary>Port of the reset-credit list endpoint (codex_checker.go:125).</summary>
    internal const string ResetCreditsUrl =
        "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";

    /// <summary>Port of the reset-credit consume endpoint (codex_checker.go:170).</summary>
    internal const string ConsumeResetCreditUrl =
        "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume";

    private const string ProviderType = "codex";

    /// <summary>codexResetStatusAvailable (codex_checker.go:65).</summary>
    internal const string ResetStatusAvailable = "available";

    /// <summary>
    /// <c>maxDurationSeconds</c> in codexWindowDuration: int64 seconds that fit
    /// a <c>time.Duration</c> (codex_checker.go:353).
    /// </summary>
    private const long MaxDurationSeconds = long.MaxValue / 1_000_000_000;

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;

    public CodexQuotaChecker(
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

    /// <summary>Port of (*CodexQuotaChecker).CheckQuota (codex_checker.go:226-251).</summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        // The account id is required even though the usage endpoint does not
        // use it (codex_checker.go:227).
        var (accessToken, _) = ExtractCodexCredentials(channel);

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");

        var request = new HttpRequestData
        {
            Method = "GET",
            Url = UsageUrl,
            Headers = headers,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = accessToken,
            },
        };

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

    /// <summary>Port of (*CodexQuotaChecker).SupportsChannel (codex_checker.go:421-423).</summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return channel.Type == ChannelOutboundFactory.CodexChannelType;
    }

    /// <summary>Port of (*CodexQuotaChecker).ListResets (codex_checker.go:89-115).</summary>
    public Task<ResetList> ListResetsAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        return Task.FromResult(ListResets(channel, cancellationToken));
    }

    /// <summary>Port of (*CodexQuotaChecker).Reset (codex_checker.go:75-87).</summary>
    public Task ResetAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var resets = ListResets(channel, cancellationToken);
        if (resets.Resets is not { Count: > 0 } available)
        {
            throw new InvalidOperationException("no available codex reset credit");
        }

        ConsumeResetCredit(channel, available[0].Id, cancellationToken);
        return Task.CompletedTask;
    }

    private ResetList ListResets(ChannelRecord channel, CancellationToken cancellationToken)
    {
        var response = ListResetCredits(channel, cancellationToken);

        // Go's make([]Reset, 0, len(credits)) is a non-nil empty slice when no
        // credit is available.
        var resets = new List<QuotaReset>(response.Credits?.Count ?? 0);
        foreach (var credit in response.Credits ?? [])
        {
            if (credit is null || credit.Status != ResetStatusAvailable)
            {
                continue;
            }

            resets.Add(new QuotaReset
            {
                Id = credit.Id ?? string.Empty,
                Status = credit.Status ?? string.Empty,
                Type = EmptyToNull(credit.ResetType),
                GrantedAt = ParseResetTime(credit.GrantedAt),
                ExpiresAt = ParseResetTime(credit.ExpiresAt),
                Title = EmptyToNull(credit.Title),
            });
        }

        return new ResetList
        {
            Supported = true,
            Resets = resets,
        };
    }

    /// <summary>Port of listResetCredits (codex_checker.go:117-147).</summary>
    private CodexResetCreditsResponse ListResetCredits(
        ChannelRecord channel,
        CancellationToken cancellationToken)
    {
        var (accessToken, accountId) = ExtractCodexCredentials(channel);

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "ChatGPT-Account-Id", accountId);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");

        var request = new HttpRequestData
        {
            Method = "GET",
            Url = ResetCreditsUrl,
            Headers = headers,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = accessToken,
            },
        };

        var executor = ResolveExecutor(channel);
        HttpResponseData response;
        try
        {
            response = executor.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "list codex reset credits failed: ");
        }

        try
        {
            // Go's json.Unmarshal of a JSON null into a struct is a no-op, so
            // the result stays the zero struct.
            return LlmJson.Deserialize<CodexResetCreditsResponse>(response.Body ?? [])
                ?? new CodexResetCreditsResponse();
        }
        catch (JsonException ex)
        {
            throw GoErrors.Wrap(ex, "failed to parse codex reset credits response: ");
        }
    }

    /// <summary>Port of consumeResetCredit (codex_checker.go:162-196).</summary>
    private void ConsumeResetCredit(
        ChannelRecord channel,
        string creditId,
        CancellationToken cancellationToken)
    {
        var (accessToken, accountId) = ExtractCodexCredentials(channel);

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "ChatGPT-Account-Id", accountId);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");

        // uuid.NewString(): lowercase canonical v4 UUID. Go's json.Marshal of a
        // map sorts the keys, as does GoJson.Marshal.
        var body = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["credit_id"] = creditId,
            ["redeem_request_id"] = Guid.NewGuid().ToString("D"),
        };

        var request = new HttpRequestData
        {
            Method = "POST",
            Url = ConsumeResetCreditUrl,
            Headers = headers,
            Body = AxonHub.Llm.Json.GoJson.Marshal(body),
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = accessToken,
            },
        };

        var executor = ResolveExecutor(channel);
        HttpResponseData response;
        try
        {
            response = executor.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "consume codex reset credit failed: ");
        }

        try
        {
            _ = LlmJson.Deserialize<CodexResetConsumeResponse>(response.Body ?? [])
                ?? new CodexResetConsumeResponse();
        }
        catch (JsonException ex)
        {
            throw GoErrors.Wrap(ex, "failed to parse codex reset consume response: ");
        }
    }

    /// <summary>
    /// Port of extractCodexCredentials (codex_checker.go:198-224). An OAuth
    /// object wins even when its access token is empty; the legacy apiKey is
    /// parsed as OAuth JSON only when the object is absent.
    /// </summary>
    internal static (string AccessToken, string AccountId) ExtractCodexCredentials(
        ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var oauthJson = ChannelCredentials.GetOAuthObjectJson(channel.Credentials);
        var legacyApiKey = ChannelCredentials.GetLegacyApiKey(channel.Credentials);
        if (oauthJson is null && legacyApiKey.Trim().Length == 0)
        {
            throw new InvalidOperationException("channel has no credentials");
        }

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
            throw new InvalidOperationException("OAuth missing access_token");
        }

        var accountId = CodexUtils.ExtractChatGptAccountIdFromJwt(accessToken);
        if (accountId.Length == 0)
        {
            throw new InvalidOperationException(
                "failed to extract ChatGPT account id from access token");
        }

        return (accessToken, accountId);
    }

    /// <summary>Port of parseCodexResetTime (codex_checker.go:149-160).</summary>
    internal static DateTimeOffset? ParseResetTime(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return GoTimeParsing.TryParseRfc3339(value);
    }

    /// <summary>Port of (*CodexQuotaChecker).parseResponse (codex_checker.go:253-304).</summary>
    internal static QuotaData ParseResponse(byte[] body, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(body);

        CodexUsageResponse? response;
        try
        {
            // Go's json.Unmarshal of a JSON null into a struct is a no-op.
            response = LlmJson.Deserialize<CodexUsageResponse>(body)
                ?? new CodexUsageResponse();
        }
        catch (JsonException ex)
        {
            throw GoErrors.Wrap(ex, "failed to parse codex usage response: ");
        }

        // Convert to raw data map (codex_checker.go:260-271).
        var rawData = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["plan_type"] = response.PlanType ?? string.Empty,
        };

        if (response.RateLimit is not null)
        {
            rawData["rate_limit"] = ConvertRateLimitToMap(response.RateLimit);
        }

        if (response.CodeReviewRateLimit is not null)
        {
            rawData["code_review_rate_limit"] = ConvertRateLimitToMap(
                response.CodeReviewRateLimit);
        }

        var limits = new List<QuotaLimitStatus>(2);
        if (response.RateLimit is not null)
        {
            var rateLimitExhausted = IsRateLimitExhausted(response.RateLimit);
            var windows = new (string Name, CodeUsageWindow? Value)[]
            {
                (QuotaWindows.Primary, response.RateLimit.PrimaryWindow),
                (QuotaWindows.Secondary, response.RateLimit.SecondaryWindow),
            };

            foreach (var candidate in windows)
            {
                if (TryBuildLimit(
                        candidate.Name,
                        candidate.Value,
                        rateLimitExhausted,
                        now,
                        out var limit))
                {
                    limits.Add(limit);
                }
            }
        }

        var normalizedStatus = AggregateStatus(response.RateLimit, limits);
        var nextResetAt = EarliestFutureReset(limits, now);

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

    /// <summary>Port of codexRateLimitExhausted (codex_checker.go:306-309).</summary>
    private static bool IsRateLimitExhausted(CodeRateLimitInfo rateLimit)
        => (rateLimit.LimitReached is { } limitReached && limitReached)
        || (rateLimit.Allowed is { } allowed && !allowed);

    /// <summary>Port of buildCodexQuotaLimit (codex_checker.go:311-346).</summary>
    private static bool TryBuildLimit(
        string name,
        CodeUsageWindow? window,
        bool rateLimitExhausted,
        DateTimeOffset now,
        out QuotaLimitStatus limit)
    {
        limit = null!;
        if (window is null)
        {
            return false;
        }

        if (!TryGetWindowDuration(window.LimitWindowSeconds, out var duration)
            || (window.UsedPercent is { } measured
                && (!ProviderQuotaData.IsFiniteRatio(measured) || measured < 0)))
        {
            return false;
        }

        var usageRatio = 0.0;
        var status = ProviderQuotaStatuses.Available;
        if (window.UsedPercent is { } usedPercent)
        {
            usageRatio = Math.Min(usedPercent / 100, 1);
            if (usageRatio >= 1)
            {
                status = ProviderQuotaStatuses.Exhausted;
            }
            else if (usageRatio >= ProviderQuotaData.WarningThresholdRatio)
            {
                status = ProviderQuotaStatuses.Warning;
            }
        }

        // The flags apply to the whole account; infer usage only when this
        // window does not report its own percentage (codex_checker.go:332-337).
        if (rateLimitExhausted && window.UsedPercent is null)
        {
            status = ProviderQuotaStatuses.Exhausted;
            usageRatio = 1;
        }

        var resetAt = WindowResetAt(window, now);
        var label = ProviderQuotaData.NormalizeQuotaWindowLabel(duration);
        if (label.Length == 0)
        {
            label = name;
        }

        limit = ProviderQuotaData.WithWindow(
            ProviderQuotaData.NewTokenLimitStatus(status, usageRatio, resetAt),
            label,
            duration);
        return true;
    }

    /// <summary>Port of codexWindowDuration (codex_checker.go:348-359).</summary>
    private static bool TryGetWindowDuration(long? seconds, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (seconds is not { } value)
        {
            return true;
        }

        if (value <= 0 || value > MaxDurationSeconds)
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(value);
        return true;
    }

    /// <summary>Port of codexWindowResetAt (codex_checker.go:361-373).</summary>
    private static DateTimeOffset? WindowResetAt(CodeUsageWindow window, DateTimeOffset now)
    {
        if (window.ResetAt is { } resetAt && resetAt > 0)
        {
            return FromUnixSeconds(resetAt);
        }

        if (TryGetWindowDuration(window.ResetAfterSeconds, out var resetAfter)
            && resetAfter > TimeSpan.Zero)
        {
            return now.Add(resetAfter);
        }

        return null;
    }

    /// <summary>Port of codexAggregateStatus (codex_checker.go:375-391).</summary>
    private static string AggregateStatus(
        CodeRateLimitInfo? rateLimit,
        IReadOnlyList<QuotaLimitStatus> limits)
    {
        var status = ProviderQuotaStatuses.Unknown;
        if (rateLimit?.Allowed is { } allowed && allowed)
        {
            status = ProviderQuotaStatuses.Available;
        }

        if (rateLimit is not null && IsRateLimitExhausted(rateLimit))
        {
            status = ProviderQuotaStatuses.Exhausted;
        }

        foreach (var limit in limits)
        {
            if (ProviderQuotaData.StatusRank(limit.Status)
                > ProviderQuotaData.StatusRank(status))
            {
                status = limit.Status;
            }
        }

        return status;
    }

    /// <summary>Port of codexEarliestFutureReset (codex_checker.go:406-419).</summary>
    private static DateTimeOffset? EarliestFutureReset(
        IReadOnlyList<QuotaLimitStatus> limits,
        DateTimeOffset now)
    {
        DateTimeOffset? earliest = null;
        foreach (var limit in limits)
        {
            if (limit.NextResetAt is not { } resetAt || resetAt <= now)
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

    /// <summary>Port of convertRateLimitToMap (codex_checker.go:425-445).</summary>
    private static Dictionary<string, object?> ConvertRateLimitToMap(CodeRateLimitInfo rateLimit)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (rateLimit.Allowed is { } allowed)
        {
            result["allowed"] = allowed;
        }

        if (rateLimit.LimitReached is { } limitReached)
        {
            result["limit_reached"] = limitReached;
        }

        if (rateLimit.PrimaryWindow is not null)
        {
            result["primary_window"] = ConvertWindowToMap(rateLimit.PrimaryWindow);
        }

        if (rateLimit.SecondaryWindow is not null)
        {
            result["secondary_window"] = ConvertWindowToMap(rateLimit.SecondaryWindow);
        }

        return result;
    }

    /// <summary>Port of convertWindowToMap (codex_checker.go:447-466).</summary>
    private static Dictionary<string, object?> ConvertWindowToMap(CodeUsageWindow window)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (window.UsedPercent is { } usedPercent)
        {
            result["used_percent"] = usedPercent;
        }

        if (window.ResetAt is { } resetAt)
        {
            result["reset_at"] = resetAt;
        }

        if (window.ResetAfterSeconds is { } resetAfterSeconds)
        {
            result["reset_after_seconds"] = resetAfterSeconds;
        }

        if (window.LimitWindowSeconds is { } limitWindowSeconds)
        {
            result["limit_window_seconds"] = limitWindowSeconds;
        }

        return result;
    }

    private IExecutor ResolveExecutor(ChannelRecord channel)
        => ParseSettingsLenient(channel.Settings)?.Proxy is { } proxy
            ? _proxyExecutors.Create(ToProxyConfig(proxy))
            : _executor;

    /// <summary>
    /// Go's <c>time.Unix(seconds, 0)</c> projected onto
    /// <see cref="DateTimeOffset"/>. Go can represent years outside 1..9999;
    /// those resets become null in the port (difference C123 item 2).
    /// </summary>
    private static DateTimeOffset? FromUnixSeconds(long seconds)
        => seconds is < -62_135_596_800 or > 253_402_300_799
            ? null
            : DateTimeOffset.FromUnixTimeSeconds(seconds);

    private static string? EmptyToNull(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

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

    /// <summary>Port of codexUsageResponse (codex_checker.go:20-25).</summary>
    private sealed class CodexUsageResponse
    {
        [JsonPropertyName("plan_type")]
        public string? PlanType { get; set; }

        [JsonPropertyName("rate_limit")]
        public CodeRateLimitInfo? RateLimit { get; set; }

        [JsonPropertyName("code_review_rate_limit")]
        public CodeRateLimitInfo? CodeReviewRateLimit { get; set; }
    }

    /// <summary>Port of codeRateLimitInfo (codex_checker.go:27-32).</summary>
    private sealed class CodeRateLimitInfo
    {
        [JsonPropertyName("allowed")]
        public bool? Allowed { get; set; }

        [JsonPropertyName("limit_reached")]
        public bool? LimitReached { get; set; }

        [JsonPropertyName("primary_window")]
        public CodeUsageWindow? PrimaryWindow { get; set; }

        [JsonPropertyName("secondary_window")]
        public CodeUsageWindow? SecondaryWindow { get; set; }
    }

    /// <summary>
    /// Port of codeUsageWindow (codex_checker.go:34-39). The reference's
    /// <c>*int</c> is 64-bit on amd64, so the seconds fields are
    /// <see cref="long"/>.
    /// </summary>
    private sealed class CodeUsageWindow
    {
        [JsonPropertyName("used_percent")]
        public double? UsedPercent { get; set; }

        [JsonPropertyName("reset_at")]
        public long? ResetAt { get; set; }

        [JsonPropertyName("reset_after_seconds")]
        public long? ResetAfterSeconds { get; set; }

        [JsonPropertyName("limit_window_seconds")]
        public long? LimitWindowSeconds { get; set; }
    }

    /// <summary>Port of codexResetCredit (codex_checker.go:41-48).</summary>
    private sealed class CodexResetCredit
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("reset_type")]
        public string? ResetType { get; set; }

        [JsonPropertyName("granted_at")]
        public string? GrantedAt { get; set; }

        [JsonPropertyName("expires_at")]
        public string? ExpiresAt { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }
    }

    /// <summary>Port of codexResetCreditsResponse (codex_checker.go:50-53).</summary>
    private sealed class CodexResetCreditsResponse
    {
        [JsonPropertyName("credits")]
        public List<CodexResetCredit?>? Credits { get; set; }

        [JsonPropertyName("available_count")]
        public long? AvailableCount { get; set; }
    }

    /// <summary>Port of codexResetConsumeResponse (codex_checker.go:55-59).</summary>
    private sealed class CodexResetConsumeResponse
    {
        [JsonPropertyName("code")]
        public string? Code { get; set; }

        [JsonPropertyName("windows_reset")]
        public long? WindowsReset { get; set; }

        [JsonPropertyName("credit")]
        public CodexResetCredit? Credit { get; set; }
    }
}
