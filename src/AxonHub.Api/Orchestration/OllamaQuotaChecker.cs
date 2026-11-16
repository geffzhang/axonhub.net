using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AxonHub.Abstractions;
using AxonHub.Api.Management;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the Ollama quota checker
/// (internal/server/biz/provider_quota/ollama_checker.go). Ollama Cloud reports
/// quota on two surfaces: the account balance API authenticated with the
/// channel API key, and the logged-in Plan &amp; Billing page authenticated with
/// the browser session cookie. Only the <c>__Secure-session</c> cookie is
/// forwarded; every other cookie in a browser paste is dropped.
/// </summary>
public sealed class OllamaQuotaChecker : IQuotaChecker
{
    internal const string ProviderType = "ollama";

    /// <summary>ollamaBalanceURL: the balance API authenticated with the channel API key.</summary>
    internal const string BalanceUrl = "https://ollama.com/api/balance";

    /// <summary>ollamaSettingsURL: the Plan &amp; Billing page used with the session cookie.</summary>
    internal const string SettingsUrl = "https://ollama.com/settings";

    /// <summary>ollamaQuotaUA (ollama_checker.go:29).</summary>
    internal const string QuotaUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>ollamaCookieName: the only cookie that authenticates the Web session.</summary>
    internal const string CookieName = "__Secure-session";

    internal const string CookiePrefix = CookieName + "=";

    /// <summary>ollamaMinCookieLen guards against a bare name or a placeholder (ollama_checker.go:32-35).</summary>
    internal const int MinCookieLength = 30;

    /// <summary>The byte span scanned after each usage keyword (ollama_checker.go:359).</summary>
    private const int SettingsScanWindow = 4000;

    /// <summary>
    /// ollamaUsageMeterRe / ollamaPercentRe / ollamaResetTimeRe
    /// (ollama_checker.go:243-247). Go's <c>\s</c> is the ASCII set
    /// <c>[\t\n\f\r ]</c>, so the class is spelled out instead of using the
    /// Unicode-aware .NET <c>\s</c> (C221).
    /// </summary>
    private static readonly Regex UsageMeterRegex = new(
        "aria-label=\"([^\"]+)\"",
        RegexOptions.CultureInvariant);

    private static readonly Regex PercentRegex = new(
        "([0-9.]+)[ \t\n\f\r]*%[ \t\n\f\r]*used",
        RegexOptions.CultureInvariant);

    private static readonly Regex ResetTimeRegex = new(
        "data-time=\"([^\"]+)\"",
        RegexOptions.CultureInvariant);

    private static readonly string[] LoginPageMarkers =
    [
        "<title>sign in",
        "sign-in",
        "signin",
        "wos-session",
        "log in to your account",
        "/signin",
    ];

    /// <summary>Suffix of the port's representation-range error (C123/C207).</summary>
    private const string TimeOutOfRangeSuffix = ": timestamp is outside the DateTimeOffset range";

    private const string TimeNotStringError = "Time.UnmarshalJSON: input is not a JSON string";

    private const string ParseBalanceErrorPrefix = "parse Ollama balance response: ";

    private const string BalanceRequestFailedPrefix = "Ollama balance request failed: ";

    private const string SettingsRequestFailedPrefix = "Ollama settings request failed: ";

    private const string NoCredentialError = "channel has no Ollama API key or quota cookie";

    private const string InvalidCookieErrorPrefix = "invalid Ollama auth cookie: ";

    private const string NoQuotaError = "no Ollama quota found in balance response";

    private const string NoWindowsError =
        "no Ollama usage windows found in settings page (expired cookie or markup change)";

    private const string LoginPageError =
        "ollama.com/settings returned the sign-in page (expired session cookie?)";

    /// <summary>Go's reflect.ToString() of the anonymous Included struct (probe-locked wording).</summary>
    private const string IncludedStructType =
        """struct { BalanceUSD *float64 "json:\"balance_usd\""; AllowanceUSD *float64 "json:\"allowance_usd\""; Period *struct { From *time.Time "json:\"from\""; Until *time.Time "json:\"until\"" } "json:\"period\""; Session *provider_quota.ollamaBalanceWindow "json:\"session\""; Weekly *provider_quota.ollamaBalanceWindow "json:\"weekly\"" }""";

    /// <summary>Go's reflect.ToString() of the anonymous Period struct.</summary>
    private const string PeriodStructType =
        """struct { From *time.Time "json:\"from\""; Until *time.Time "json:\"until\"" }""";

    /// <summary>Go's reflect.ToString() of the anonymous Purchased struct.</summary>
    private const string PurchasedStructType =
        """struct { BalanceUSD float64 "json:\"balance_usd\"" }""";

    private readonly IExecutor _executor;
    private readonly IProxyExecutorFactory _proxyExecutors;
    private readonly TimeProvider _timeProvider;

    public OllamaQuotaChecker(
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

    /// <summary>Port of (*OllamaQuotaChecker).CheckQuota (ollama_checker.go:101-132).</summary>
    public Task<QuotaData> CheckQuotaAsync(ChannelRecord channel, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var apiKey = ApiKey(channel, _timeProvider.GetUtcNow());
        var rawCookie = RawCookie(channel);
        if (apiKey.Length == 0 && rawCookie.Length == 0)
        {
            throw new InvalidProviderQuotaCredentialsException(NoCredentialError);
        }

        // provider_quota.go:46-48 and ollama_checker.go:108-114: a channel
        // proxy replaces the shared client, and the result rejects HTTPS to
        // HTTP redirects.
        var executor = ResolveExecutor(channel);

        if (apiKey.Length != 0)
        {
            try
            {
                return Task.FromResult(FetchBalance(executor, apiKey, cancellationToken));
            }
            catch (Exception ex) when (rawCookie.Length != 0
                && GoErrors.As<InvalidProviderQuotaCredentialsException>(ex) is not null)
            {
                // A rejected API key still leaves the stored session cookie
                // usable, so degrade to the settings page instead of failing.
            }
        }

        var normalizeError = NormalizeOllamaCookie(rawCookie, out var cookie);
        if (normalizeError is not null)
        {
            throw new InvalidProviderQuotaCredentialsException(
                InvalidCookieErrorPrefix + normalizeError);
        }

        return Task.FromResult(FetchSettings(executor, cookie, cancellationToken));
    }

    /// <summary>Port of (*OllamaQuotaChecker).SupportsChannel (ollama_checker.go:90-96).</summary>
    public bool SupportsChannel(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.Type != ChannelOutboundFactory.OllamaChannelType
            && channel.Type != ChannelOutboundFactory.OllamaAnthropicChannelType)
        {
            return false;
        }

        return HasQuotaCredentials(channel, _timeProvider.GetUtcNow());
    }

    /// <summary>
    /// Port of HasOllamaQuotaCredentials (ollama_checker.go:80-86): the account
    /// API key or a stored session cookie. Cookie format validation stays in
    /// CheckQuota so a malformed cookie surfaces as a quota error instead of the
    /// channel being silently skipped.
    /// </summary>
    internal static bool HasQuotaCredentials(ChannelRecord? channel, DateTimeOffset now)
    {
        if (channel is null)
        {
            return false;
        }

        return ApiKey(channel, now).Length != 0 || RawCookie(channel).Length != 0;
    }

    /// <summary>
    /// Port of ollamaAPIKey (ollama_checker.go:56-66): the first non-blank key
    /// from GetAllAPIKeys that is not actively disabled.
    /// </summary>
    internal static string ApiKey(ChannelRecord channel, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var disabled = DisabledKeySet(channel, now);
        foreach (var candidate in ChannelCredentials.GetAllApiKeys(channel.Credentials))
        {
            var trimmed = GoStrings.TrimSpace(candidate);
            if (trimmed.Length != 0 && !disabled.Contains(trimmed))
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    /// <summary>Port of ollamaRawCookie (ollama_checker.go:68-74).</summary>
    internal static string RawCookie(ChannelRecord channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var settings = ParseSettingsLenient(channel.Settings);
        return GoStrings.TrimSpace(settings?.ProviderQuota?.Ollama?.AuthCookie ?? string.Empty);
    }

    /// <summary>Port of zhipuDisabledKeySet (zhipu_checker.go:684-697).</summary>
    private static HashSet<string> DisabledKeySet(ChannelRecord channel, DateTimeOffset now)
    {
        var disabled = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<DisabledApiKey> entries;
        try
        {
            entries = ChannelWriteSerialization.ParseDisabledApiKeys(channel.DisabledApiKeys);
        }
        catch (JsonException)
        {
            // The reference decodes the column with Ent before reaching the
            // checker; malformed JSON is treated as "no settings" (C013).
            return disabled;
        }

        foreach (var entry in entries)
        {
            var key = GoStrings.TrimSpace(entry.Key);
            if (key.Length == 0 || entry.IsExpired(now))
            {
                continue;
            }

            disabled.Add(key);
        }

        return disabled;
    }

    /// <summary>Port of (*OllamaQuotaChecker).fetchBalance (ollama_checker.go:134-154).</summary>
    private QuotaData FetchBalance(IExecutor executor, string apiKey, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // The reference sets the Authorization header literally (not through
        // the auth config); both produce the same wire header.
        HttpClientUtils.HeaderSet(headers, "Authorization", "Bearer " + apiKey);
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        var request = new HttpRequestData
        {
            Method = "GET",
            Url = BalanceUrl,
            Headers = headers,
        };

        HttpResponseData response;
        try
        {
            response = executor.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            if (GoErrors.As<HttpClientError>(ex) is { } httpError)
            {
                throw BalanceStatusError(httpError.StatusCode);
            }

            throw GoErrors.Wrap(ex, BalanceRequestFailedPrefix);
        }

        // The shared executor already raises for >=400 (Go's httpclient.Do does
        // the same), so this only sees 1xx/3xx and non-200 2xx.
        if (response.StatusCode < 200 || response.StatusCode >= 300)
        {
            throw BalanceStatusError(response.StatusCode);
        }

        return ParseBalance(response.Body ?? [], _timeProvider.GetUtcNow());
    }

    /// <summary>Port of ollamaBalanceStatusError (ollama_checker.go:156-162).</summary>
    private static Exception BalanceStatusError(int statusCode)
    {
        if (statusCode is 401 or 403)
        {
            return new InvalidProviderQuotaCredentialsException(
                "Ollama balance API returned " + statusCode);
        }

        return new InvalidOperationException("Ollama balance API returned " + statusCode);
    }

    /// <summary>Port of (*OllamaQuotaChecker).fetchSettings (ollama_checker.go:164-191).</summary>
    private QuotaData FetchSettings(IExecutor executor, string cookie, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Cookie", cookie);
        HttpClientUtils.HeaderSet(headers, "Accept", "text/html,application/xhtml+xml");
        HttpClientUtils.HeaderSet(headers, "User-Agent", QuotaUserAgent);

        var request = new HttpRequestData
        {
            Method = "GET",
            Url = SettingsUrl,
            Headers = headers,
        };

        HttpResponseData response;
        try
        {
            response = executor.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            if (GoErrors.As<HttpClientError>(ex) is { } httpError)
            {
                throw SettingsStatusError(httpError.StatusCode);
            }

            throw GoErrors.Wrap(ex, SettingsRequestFailedPrefix);
        }

        if (response.StatusCode < 200 || response.StatusCode >= 300)
        {
            throw SettingsStatusError(response.StatusCode);
        }

        return ParseResponse(response.Body ?? []);
    }

    /// <summary>Port of the settings status branch (ollama_checker.go:174-188).</summary>
    private static Exception SettingsStatusError(int statusCode)
    {
        if (statusCode is 401 or 403)
        {
            return new InvalidProviderQuotaCredentialsException(
                "Ollama settings page returned " + statusCode + " (expired session cookie?)");
        }

        return new InvalidOperationException("Ollama settings page returned " + statusCode);
    }

    /// <summary>
    /// Port of NormalizeOllamaCookie (ollama_checker.go:201-241): canonicalizes
    /// a browser cookie capture into the allowlisted Cookie header value,
    /// keeping only <c>__Secure-session</c>. Returns the Go error text, or null
    /// on success.
    /// </summary>
    internal static string? NormalizeOllamaCookie(string raw, out string normalized)
    {
        normalized = string.Empty;

        var cleaned = GoStrings.TrimSpace(raw);
        if (cleaned.Length == 0)
        {
            return "cookie is empty";
        }

        // Strip an optional leading "Cookie:" label (browser devtools paste).
        var labelIndex = cleaned.IndexOf(':', StringComparison.Ordinal);
        if (labelIndex >= 0
            && string.Equals(
                GoStrings.TrimSpace(cleaned[..labelIndex]),
                "cookie",
                StringComparison.OrdinalIgnoreCase))
        {
            cleaned = GoStrings.TrimSpace(cleaned[(labelIndex + 1)..]);
        }

        // Reject multi-line pastes (e.g. cURL) outright.
        if (cleaned.Contains('\r') || cleaned.Contains('\n'))
        {
            return "cookie contains line breaks";
        }

        foreach (var rawPart in cleaned.Split(';'))
        {
            var part = GoStrings.TrimSpace(rawPart);
            if (part.Length == 0)
            {
                continue;
            }

            var cut = part.IndexOf('=', StringComparison.Ordinal);
            if (cut < 0)
            {
                return "invalid cookie segment: expected name=value";
            }

            var name = part[..cut];
            var value = part[(cut + 1)..];
            if (GoStrings.TrimSpace(name).Length == 0 || value.Length == 0)
            {
                return "invalid cookie segment: expected name=value";
            }

            // Only the __Secure-session cookie is forwarded.
            if (!string.Equals(
                    GoStrings.TrimSpace(name),
                    CookieName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = GoStrings.TrimSpace(value);
            // Go's len counts bytes, not runes.
            if (Encoding.UTF8.GetByteCount(value) < MinCookieLength)
            {
                return "Ollama cookie is too short to be a valid session";
            }

            normalized = CookiePrefix + value;
            return null;
        }

        return "no __Secure-session Ollama cookie found";
    }

    /// <summary>Port of (*OllamaQuotaChecker).parseResponse (ollama_checker.go:346-405).</summary>
    internal static QuotaData ParseResponse(byte[] body)
    {
        var windows = new List<OllamaUsageWindow>();
        ExtractWindow(body, "Session usage", QuotaWindows.FiveHours, windows);
        ExtractWindow(body, "Weekly usage", QuotaWindows.Weekly, windows);

        if (windows.Count == 0)
        {
            // No usage windows matched: the page may be a sign-in page (an
            // expired cookie that returns 200 instead of 401/403), which
            // invalidates the credential, or the markup changed, which is a
            // generic check failure.
            if (LooksLikeLoginPage(Encoding.UTF8.GetString(body)))
            {
                throw new InvalidProviderQuotaCredentialsException(LoginPageError);
            }

            throw new InvalidOperationException(NoWindowsError);
        }

        return BuildQuotaData(windows);
    }

    /// <summary>
    /// Port of the extractWindow closure (ollama_checker.go:354-385). The
    /// keyword search and the 4000-byte scan window operate on UTF-8 bytes like
    /// Go's string indexing; the chunk is decoded before the rune-based regexes
    /// run (C221).
    /// </summary>
    private static void ExtractWindow(
        byte[] body,
        string keyword,
        string key,
        List<OllamaUsageWindow> windows)
    {
        var idx = body.AsSpan().IndexOf(Encoding.UTF8.GetBytes(keyword));
        if (idx < 0)
        {
            return;
        }

        var end = Math.Min(idx + SettingsScanWindow, body.Length);
        var chunk = Encoding.UTF8.GetString(body, idx, end - idx);

        var meter = UsageMeterRegex.Match(chunk);
        if (!meter.Success)
        {
            return;
        }

        var percentMatch = PercentRegex.Match(meter.Groups[1].Value);
        if (!percentMatch.Success)
        {
            return;
        }

        if (!GoCast.TryParseFloat(percentMatch.Groups[1].Value, out var percent))
        {
            return;
        }

        DateTimeOffset? resetAt = null;
        var resetMatch = ResetTimeRegex.Match(chunk);
        if (resetMatch.Success)
        {
            // time.Parse(time.RFC3339Nano, ...) failure leaves the reset nil;
            // Go accepts a wider value range than DateTimeOffset can hold
            // (year 0000, offsets beyond ±14:00), which also drops the reset
            // (C123).
            resetAt = GoTimeParsing.TryParseRfc3339(resetMatch.Groups[1].Value);
        }

        windows.Add(new OllamaUsageWindow
        {
            Key = key,
            Percent = percent,
            ResetAt = resetAt,
        });
    }

    /// <summary>
    /// Port of ollamaLooksLikeLoginPage (ollama_checker.go:498-514): the stable
    /// AuthKit sign-in markers, matched case-insensitively against the page.
    /// </summary>
    internal static bool LooksLikeLoginPage(string html)
    {
        var lower = html.ToLowerInvariant();
        foreach (var marker in LoginPageMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Port of (*OllamaQuotaChecker).parseBalance (ollama_checker.go:280-340).
    /// The balance payload is decoded with Go's struct semantics, then the
    /// assembled status is normalized at the injected clock.
    /// </summary>
    internal static QuotaData ParseBalance(byte[] body, DateTimeOffset now)
    {
        OllamaBalanceResponse parsed;
        try
        {
            parsed = ParseBalanceResponse(body);
        }
        catch (JsonException ex)
        {
            throw GoErrors.Wrap(ex, ParseBalanceErrorPrefix);
        }

        var windows = new List<OllamaUsageWindow>();

        var allowance = parsed.AllowanceUsd ?? 0;
        if (allowance > 0)
        {
            var window = new OllamaUsageWindow
            {
                Key = QuotaWindows.Monthly,
                Percent = ClampPercent((1 - ((parsed.BalanceUsd ?? 0) / allowance)) * 100),
            };
            if (parsed.Period is not null)
            {
                window.ResetAt = parsed.Period.Until;
                window.PeriodStart = parsed.Period.From;
            }

            windows.Add(window);
        }

        foreach (var (key, legacy) in new[]
        {
            (QuotaWindows.FiveHours, parsed.Session),
            (QuotaWindows.Weekly, parsed.Weekly),
        })
        {
            if (legacy is null || legacy.RemainingPercent is not { } remaining)
            {
                continue;
            }

            windows.Add(new OllamaUsageWindow
            {
                Key = key,
                Percent = ClampPercent(100 - remaining),
                ResetAt = legacy.ResetsAt,
            });
        }

        var purchased = parsed.PurchasedBalanceUsd;
        if (windows.Count == 0 && purchased <= 0)
        {
            throw new InvalidOperationException(NoQuotaError);
        }

        var data = BuildQuotaData(windows);
        if (purchased > 0)
        {
            data.Limits.Add(new QuotaLimitStatus
            {
                Type = QuotaLimitTypes.Token,
                Status = ProviderQuotaStatuses.Available,
                Ready = true,
                Window = QuotaWindows.Credits,
            });
            data.RawData["credits"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["purchased_usd"] = purchased,
            };
            // Ollama draws from purchased credits once the included allowance
            // is spent, so the overall status comes from the best usable limit.
            data.Status = string.Empty;
        }

        return ProviderQuotaData.Normalize(data, now);
    }

    /// <summary>Port of clampOllamaPercent (ollama_checker.go:342-344).</summary>
    internal static double ClampPercent(double percent) => Math.Max(0, Math.Min(100, percent));

    /// <summary>Port of buildOllamaQuotaData (ollama_checker.go:407-465).</summary>
    internal static QuotaData BuildQuotaData(IReadOnlyList<OllamaUsageWindow> windows)
    {
        var normalizedStatus = ProviderQuotaStatuses.Available;
        DateTimeOffset? nextResetAt = null;
        var limits = new List<QuotaLimitStatus>(windows.Count);
        var rawWindows = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var window in windows)
        {
            var usageRatio = window.Percent / 100.0;
            var status = WindowStatus(usageRatio);
            if (OllamaStatusRank(status) > OllamaStatusRank(normalizedStatus))
            {
                normalizedStatus = status;
            }

            DateTimeOffset? resetAt = window.ResetAt;
            if (resetAt is { } reset && (nextResetAt is null || reset < nextResetAt))
            {
                nextResetAt = reset;
            }

            var rawWindow = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["usage_percent"] = window.Percent,
                ["status"] = status,
                ["percent_remaining"] = 100 - window.Percent,
            };
            if (resetAt is { } renderedReset)
            {
                // Go's Format(time.RFC3339): the parsed zone offset is kept,
                // a zero offset renders as Z, subseconds are dropped (C221).
                rawWindow["reset_time"] = FormatRfc3339(renderedReset);
            }

            rawWindows[window.Key] = rawWindow;

            var periodStart = window.PeriodStart
                ?? ProviderQuotaData.PeriodStartFromReset(resetAt, WindowDuration(window.Key));

            limits.Add(new QuotaLimitStatus
            {
                Type = QuotaLimitTypes.Token,
                Status = status,
                UsageRatio = usageRatio,
                Ready = ProviderQuotaStatuses.IsReadyStatus(status),
                NextResetAt = resetAt,
                Window = window.Key,
                PeriodStart = periodStart,
            });
        }

        return new QuotaData
        {
            Status = normalizedStatus,
            ProviderType = ProviderType,
            RawData = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["windows"] = rawWindows,
            },
            NextResetAt = nextResetAt,
            Ready = ProviderQuotaStatuses.IsReadyStatus(normalizedStatus),
            Limits = limits,
        };
    }

    /// <summary>Port of ollamaWindowDuration (ollama_checker.go:469-478).</summary>
    internal static TimeSpan WindowDuration(string key) => key switch
    {
        QuotaWindows.FiveHours => TimeSpan.FromHours(5),
        QuotaWindows.Weekly => TimeSpan.FromDays(7),
        _ => TimeSpan.Zero,
    };

    /// <summary>Port of normalizeOllamaWindowStatus (ollama_checker.go:480-488).</summary>
    internal static string WindowStatus(double usageRatio)
    {
        if (usageRatio >= 1.0)
        {
            return ProviderQuotaStatuses.Exhausted;
        }

        return usageRatio >= ProviderQuotaData.WarningThresholdRatio
            ? ProviderQuotaStatuses.Warning
            : ProviderQuotaStatuses.Available;
    }

    /// <summary>
    /// Port of quotaStatusRank (opencode_go_checker.go:291-301), used by
    /// buildOllamaQuotaData: exhausted 2, warning 1, available 0, default -1.
    /// </summary>
    internal static int OllamaStatusRank(string? status) => status switch
    {
        ProviderQuotaStatuses.Exhausted => 2,
        ProviderQuotaStatuses.Warning => 1,
        ProviderQuotaStatuses.Available => 0,
        _ => -1,
    };

    /// <summary>Port of <c>time.Time.Format(time.RFC3339)</c> on a parsed value.</summary>
    internal static string FormatRfc3339(DateTimeOffset value)
    {
        var text = value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return value.Offset == TimeSpan.Zero
            ? text + "Z"
            : text + value.ToString("zzz", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Port of <c>json.Unmarshal(body, &amp;ollamaBalanceResponse{})</c>
    /// (ollama_checker.go:280-284): Go's struct decode including the
    /// anonymous-struct type text in mismatch errors and the exact
    /// time.UnmarshalJSON errors.
    /// </summary>
    /// <exception cref="JsonException">Go's UnmarshalTypeError / time.ParseError text.</exception>
    internal static OllamaBalanceResponse ParseBalanceResponse(byte[] data)
    {
        var syntaxError = GoJsonSyntax.Validate(data);
        if (syntaxError is not null)
        {
            throw new JsonException(syntaxError);
        }

        var rootKind = GoStructJson.RootKind(data);
        if (rootKind == "null")
        {
            // Go: unmarshaling JSON null into a struct is a no-op.
            return new OllamaBalanceResponse();
        }

        if (rootKind is not null)
        {
            throw new JsonException(
                GoStructJson.TypeMismatch(rootKind, "provider_quota.ollamaBalanceResponse"));
        }

        using var document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        var response = new OllamaBalanceResponse();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            // Go's field lookup folds ASCII letters and binds both the JSON
            // tag and the struct field name; the last occurrence of a field
            // decodes over the previous one.
            switch (FoldAscii(property.Name))
            {
                case "included":
                    DecodeIncluded(property.Value, response);
                    break;
                case "purchased":
                    DecodePurchased(property.Value, response);
                    break;
            }
        }

        return response;
    }

    private static void DecodeIncluded(JsonElement value, OllamaBalanceResponse target)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal " + GoKindName(value.ValueKind)
                + " into Go struct field ollamaBalanceResponse.included of type "
                + IncludedStructType);
        }

        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "balance_usd" or "balanceusd":
                    target.BalanceUsd = DecodeFloat(property.Value, string.Empty, "included.balance_usd");
                    break;
                case "allowance_usd" or "allowanceusd":
                    target.AllowanceUsd = DecodeFloat(property.Value, string.Empty, "included.allowance_usd");
                    break;
                case "period":
                    target.Period = DecodePeriod(property.Value, target.Period);
                    break;
                case "session":
                    target.Session = DecodeWindow(property.Value, "included.session", target.Session);
                    break;
                case "weekly":
                    target.Weekly = DecodeWindow(property.Value, "included.weekly", target.Weekly);
                    break;
            }
        }
    }

    private static void DecodePurchased(JsonElement value, OllamaBalanceResponse target)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal " + GoKindName(value.ValueKind)
                + " into Go struct field ollamaBalanceResponse.purchased of type "
                + PurchasedStructType);
        }

        foreach (var property in value.EnumerateObject())
        {
            if (FoldAscii(property.Name) is not ("balance_usd" or "balanceusd"))
            {
                continue;
            }

            // A non-pointer float64: JSON null leaves the previous value.
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            target.PurchasedBalanceUsd =
                DecodeFloat(property.Value, string.Empty, "purchased.balance_usd")!.Value;
        }
    }

    private static OllamaBalancePeriod? DecodePeriod(JsonElement value, OllamaBalancePeriod? current)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal " + GoKindName(value.ValueKind)
                + " into Go struct field .included.period of type " + PeriodStructType);
        }

        var target = current ?? new OllamaBalancePeriod();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "from":
                    target.From = DecodeGoTime(property.Value);
                    break;
                case "until":
                    target.Until = DecodeGoTime(property.Value);
                    break;
            }
        }

        return target;
    }

    private static OllamaBalanceWindow? DecodeWindow(
        JsonElement value,
        string fieldPath,
        OllamaBalanceWindow? current)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal " + GoKindName(value.ValueKind)
                + " into Go struct field ." + fieldPath
                + " of type provider_quota.ollamaBalanceWindow");
        }

        var target = current ?? new OllamaBalanceWindow();
        foreach (var property in value.EnumerateObject())
        {
            switch (FoldAscii(property.Name))
            {
                case "remaining_percent" or "remainingpercent":
                    target.RemainingPercent = DecodeFloat(
                        property.Value,
                        "ollamaBalanceWindow",
                        fieldPath + ".remaining_percent");
                    break;
                case "resets_at" or "resetsat":
                    target.ResetsAt = DecodeGoTime(property.Value);
                    break;
            }
        }

        return target;
    }

    /// <summary>
    /// Go's decoder for a <c>*float64</c> field: JSON null clears the pointer;
    /// a number goes through strconv.ParseFloat (overflow is a type error
    /// naming the literal, underflow decodes to zero).
    /// </summary>
    private static double? DecodeFloat(JsonElement value, string structName, string fieldPath)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number
            || !GoCast.TryParseFloat(value.GetRawText(), out var parsed))
        {
            throw StructTypeMismatch(GoValueName(value), structName, fieldPath, "float64");
        }

        return parsed;
    }

    /// <summary>
    /// Port of <c>time.Time.UnmarshalJSON</c>: the raw token text is parsed as
    /// Go would (escapes are not decoded — Go passes the raw bytes through).
    /// </summary>
    private static DateTimeOffset? DecodeGoTime(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new JsonException(TimeNotStringError);
        }

        // GetRawText keeps the source text, including escape sequences, so a
        // timestamp written with JSON escapes fails exactly like Go's parse.
        var raw = value.GetRawText();
        var text = raw.Length >= 2 ? raw[1..^1] : string.Empty;

        if (GoTimeParsing.TryParseRfc3339(text) is { } parsed)
        {
            return parsed;
        }

        var error = ZenmuxQuotaChecker.GoRfc3339ParseError(text);
        if (error is null || error.EndsWith(TimeOutOfRangeSuffix, StringComparison.Ordinal))
        {
            // Go accepted the value; DateTimeOffset cannot represent it (C123).
            return null;
        }

        throw new JsonException(error);
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

    /// <summary>
    /// Go's notion of the offending value for a float64 field: the literal for
    /// numbers (strconv overflow reports <c>number 1e400</c>), the kind word
    /// otherwise. Struct-level mismatches use <see cref="GoKindName"/> instead
    /// because Go drops the literal there.
    /// </summary>
    private static string GoValueName(JsonElement value)
        => value.ValueKind == JsonValueKind.Number
            ? "number " + value.GetRawText()
            : GoKindName(value.ValueKind);

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
    {
        // ollama_checker.go:108-114: the channel proxy replaces the shared
        // client first, then the HTTPS-downgrade rejection is installed so
        // neither the API key nor the session cookie can be forwarded over a
        // redirect to cleartext.
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
/// Port of <c>ollamaUsageWindow</c> (ollama_checker.go:249-254): one parsed
/// window before the quota data is assembled.
/// </summary>
internal sealed class OllamaUsageWindow
{
    public string Key { get; set; } = string.Empty;

    public double Percent { get; set; }

    public DateTimeOffset? ResetAt { get; set; }

    public DateTimeOffset? PeriodStart { get; set; }
}

/// <summary>
/// Port of <c>ollamaBalanceResponse</c> (ollama_checker.go:259-273): the
/// anonymous Included / Purchased structs are flattened because only their
/// fields are observable.
/// </summary>
internal sealed class OllamaBalanceResponse
{
    public double? BalanceUsd { get; set; }

    public double? AllowanceUsd { get; set; }

    public OllamaBalancePeriod? Period { get; set; }

    public OllamaBalanceWindow? Session { get; set; }

    public OllamaBalanceWindow? Weekly { get; set; }

    public double PurchasedBalanceUsd { get; set; }
}

/// <summary>Port of the anonymous period struct inside <c>Included</c>.</summary>
internal sealed class OllamaBalancePeriod
{
    public DateTimeOffset? From { get; set; }

    public DateTimeOffset? Until { get; set; }
}

/// <summary>Port of <c>ollamaBalanceWindow</c> (ollama_checker.go:275-278).</summary>
internal sealed class OllamaBalanceWindow
{
    public double? RemainingPercent { get; set; }

    public DateTimeOffset? ResetsAt { get; set; }
}
