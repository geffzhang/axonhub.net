using System.Text;
using System.Text.Json;
using AxonHub.Llm.Concurrency;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Llm.OAuth;

/// <summary>Port of oauth.DeviceFlowProviderParams (llm/oauth/device_flow_provider.go:99-109).</summary>
public sealed class DeviceFlowProviderParams
{
    public DeviceFlowConfig Config { get; set; } = new();

    public IExecutor? HttpClient { get; set; }

    /// <summary>Optional: can be set after the device flow completes.</summary>
    public OAuthCredentials? Credentials { get; set; }

    /// <summary>Optional two-step exchange, for example Copilot.</summary>
    public ITokenExchanger? TokenExchanger { get; set; }

    /// <summary>Called when credentials are refreshed (for persistence).</summary>
    public Func<CancellationToken, OAuthCredentials, Exception?>? OnRefreshed { get; set; }
}

/// <summary>
/// Port of oauth.DeviceFlowProvider (llm/oauth/device_flow_provider.go:69-97):
/// RFC 8628 device authorization grant with optional token exchange,
/// singleflight refresh and a background refresh loop.
/// </summary>
public sealed class DeviceFlowProvider
{
    private static readonly TimeSpan DefaultFallbackInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultRefreshBefore = TimeSpan.FromMinutes(5);
    private const string DeviceCodeGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    private readonly DeviceFlowConfig _config;
    private readonly IExecutor? _httpClient;
    private readonly ITokenExchanger? _tokenExchanger;
    private readonly SingleFlight<string> _singleFlight = new();
    private readonly object _sync = new();
    private readonly Func<CancellationToken, OAuthCredentials, Exception?>? _onRefreshed;
    private readonly object _autoSync = new();

    private OAuthCredentials? _creds;
    private CancellationTokenSource? _autoCts;

    public DeviceFlowProvider(DeviceFlowProviderParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        _config = parameters.Config;
        _httpClient = parameters.HttpClient;
        _creds = parameters.Credentials;
        _tokenExchanger = parameters.TokenExchanger;
        _onRefreshed = parameters.OnRefreshed;
    }

    /// <summary>The configuration this provider was built with (reference field <c>config</c>).</summary>
    internal DeviceFlowConfig Config => _config;

    /// <summary>Port of <c>(*DeviceFlowProvider).Start</c>: device authorization request.</summary>
    public DeviceFlowResponse Start(CancellationToken cancellationToken)
    {
        if (_httpClient is null)
        {
            throw new InvalidOperationException("http client is nil");
        }

        if (_config.DeviceAuthUrl.Length == 0)
        {
            throw new InvalidOperationException("device authorization URL is empty");
        }

        if (_config.ClientId.Length == 0)
        {
            throw new InvalidOperationException("client_id is empty");
        }

        var form = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["client_id"] = [_config.ClientId],
        };

        if (_config.Scopes.Count > 0)
        {
            form["scope"] = [string.Join(' ', _config.Scopes)];
        }

        var request = FormRequest(form, _config.DeviceAuthUrl);

        HttpResponseData response;
        try
        {
            response = _httpClient.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "device authorization request failed: ");
        }

        DeviceFlowResponse deviceResponse;
        try
        {
            deviceResponse = LlmJson.Deserialize<DeviceFlowResponse>(response.Body ?? [])
                ?? throw new JsonException("device authorization response: unexpected JSON null");
        }
        catch (JsonException ex)
        {
            throw GoErrors.Wrap(ex, "decode device authorization response: ");
        }

        deviceResponse.DeviceCode = NonNull(deviceResponse.DeviceCode);

        if (deviceResponse.DeviceCode.Length == 0)
        {
            TokenError? tokenError = null;
            try
            {
                tokenError = LlmJson.Deserialize<TokenError>(response.Body ?? []);
            }
            catch (JsonException)
            {
                // The reference ignores the error envelope's decode failure.
            }

            if (!string.IsNullOrEmpty(tokenError?.Error))
            {
                throw new InvalidOperationException(
                    "device authorization failed: " + tokenError.Error + " - " + NonNull(tokenError.ErrorDescription));
            }

            throw new InvalidOperationException("device authorization response missing device_code");
        }

        return deviceResponse;
    }

    /// <summary>
    /// Port of <c>(*DeviceFlowProvider).Poll</c>: RFC 8628 polling. Known error
    /// codes surface verbatim so the caller can decide whether to retry.
    /// </summary>
    public OAuthCredentials Poll(CancellationToken cancellationToken, string deviceCode)
    {
        if (_httpClient is null)
        {
            throw new InvalidOperationException("http client is nil");
        }

        if (_config.TokenUrl.Length == 0)
        {
            throw new InvalidOperationException("token URL is empty");
        }

        if (deviceCode.Length == 0)
        {
            throw new InvalidOperationException("device_code is empty");
        }

        var form = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["grant_type"] = [DeviceCodeGrantType],
            ["client_id"] = [_config.ClientId],
            ["device_code"] = [deviceCode],
        };

        var request = FormRequest(form, _config.TokenUrl);

        HttpResponseData response;
        try
        {
            response = _httpClient.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "token request failed: ");
        }

        // Check for error response first.
        TokenError? tokenError = null;
        try
        {
            tokenError = LlmJson.Deserialize<TokenError>(response.Body ?? []);
        }
        catch (JsonException)
        {
            // A malformed body is reported by ParseTokenResponse below.
        }

        if (!string.IsNullOrEmpty(tokenError?.Error))
        {
            throw tokenError.Error switch
            {
                "authorization_pending" => new InvalidOperationException("authorization_pending"),
                "slow_down" => new InvalidOperationException("slow_down"),
                "expired_token" => new InvalidOperationException("expired_token"),
                "access_denied" => new InvalidOperationException("access_denied"),
                _ => new InvalidOperationException(
                    "token request failed: " + tokenError.Error + " - " + NonNull(tokenError.ErrorDescription)),
            };
        }

        var creds = OAuthCredentialsJson.ParseTokenResponse(response.Body ?? [], _config.ClientId);

        lock (_sync)
        {
            _creds = creds;
        }

        return creds;
    }

    /// <summary>
    /// Port of <c>(*DeviceFlowProvider).GetToken</c>: the token for API
    /// requests, exchanged when a <see cref="ITokenExchanger"/> is configured.
    /// </summary>
    public string GetToken(CancellationToken cancellationToken)
    {
        var creds = ReadCredentials();

        if (creds is null)
        {
            throw new InvalidOperationException("credentials is nil");
        }

        if (creds.AccessToken.Length == 0)
        {
            throw new InvalidOperationException("access token is empty");
        }

        return _tokenExchanger is not null
            ? GetExchangedToken(cancellationToken, creds.AccessToken)
            : GetAccessTokenWithRefresh(cancellationToken);
    }

    /// <summary>Port of <c>(*DeviceFlowProvider).GetCredentials</c>: a defensive copy.</summary>
    public OAuthCredentials? GetCredentials()
    {
        var creds = ReadCredentials();
        return creds?.Clone();
    }

    /// <summary>Port of <c>(*DeviceFlowProvider).UpdateCredentials</c>.</summary>
    public void UpdateCredentials(OAuthCredentials? credentials)
    {
        lock (_sync)
        {
            _creds = credentials?.Clone();
        }
    }

    /// <summary>Port of <c>(*DeviceFlowProvider).StartAutoRefresh</c>.</summary>
    public void StartAutoRefresh(CancellationToken cancellationToken, AutoRefreshOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var fallbackInterval = options.Interval > TimeSpan.Zero
            ? options.Interval
            : DefaultFallbackInterval;
        var refreshBefore = options.RefreshBefore > TimeSpan.Zero
            ? options.RefreshBefore
            : DefaultRefreshBefore;

        CancellationTokenSource autoCts;

        lock (_autoSync)
        {
            if (_autoCts is not null)
            {
                return;
            }

            autoCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _autoCts = autoCts;
        }

        _ = Task.Factory.StartNew(
            () => RunAutoRefresh(autoCts.Token, refreshBefore, fallbackInterval),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>Port of <c>(*DeviceFlowProvider).StopAutoRefresh</c>; idempotent.</summary>
    public void StopAutoRefresh()
    {
        CancellationTokenSource? autoCts;

        lock (_autoSync)
        {
            autoCts = _autoCts;
            _autoCts = null;
        }

        autoCts?.Cancel();
    }

    private string GetExchangedToken(CancellationToken cancellationToken, string accessToken)
    {
        return _singleFlight.Do("exchange", () =>
        {
            // Try with the custom HTTP client first.
            if (_httpClient is not null)
            {
                var result = _tokenExchanger!.ExchangeWithClient(cancellationToken, _httpClient, accessToken);
                if (result.Error is null)
                {
                    return result.Token;
                }

                // Fall back to the default exchange.
            }

            var fallback = _tokenExchanger!.Exchange(cancellationToken, accessToken);
            if (fallback.Error is not null)
            {
                throw GoErrors.Wrap(fallback.Error, "token exchange failed: ");
            }

            return fallback.Token;
        });
    }

    private string GetAccessTokenWithRefresh(CancellationToken cancellationToken)
    {
        var creds = ReadCredentials();

        if (creds is null)
        {
            throw new InvalidOperationException("credentials is nil");
        }

        if (!creds.IsExpired(DateTimeOffset.UtcNow))
        {
            return creds.AccessToken;
        }

        return _singleFlight.Do("refresh", () =>
        {
            var current = ReadCredentials();
            var onRefreshed = _onRefreshed;

            if (current is null)
            {
                throw new InvalidOperationException("credentials is nil");
            }

            if (current.RefreshToken.Length == 0)
            {
                throw new InvalidOperationException("refresh_token is empty");
            }

            // Double-check expiration inside singleflight.
            if (!current.IsExpired(DateTimeOffset.UtcNow))
            {
                return current.AccessToken;
            }

            var fresh = Refresh(cancellationToken, current);

            lock (_sync)
            {
                _creds = fresh;
            }

            if (onRefreshed is not null)
            {
                _ = onRefreshed(cancellationToken, fresh);
            }

            return fresh.AccessToken;
        });
    }

    /// <summary>Port of <c>(*DeviceFlowProvider).refresh</c>.</summary>
    internal OAuthCredentials Refresh(CancellationToken cancellationToken, OAuthCredentials? credentials)
    {
        if (credentials is null)
        {
            throw new InvalidOperationException("nil credentials");
        }

        if (credentials.RefreshToken.Length == 0)
        {
            throw new InvalidOperationException("refresh_token is empty");
        }

        if (_config.TokenUrl.Length == 0)
        {
            throw new InvalidOperationException("token URL is empty");
        }

        var form = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["grant_type"] = ["refresh_token"],
            ["client_id"] = [_config.ClientId],
            ["refresh_token"] = [credentials.RefreshToken],
        };

        var request = FormRequest(form, _config.TokenUrl);

        HttpResponseData response;
        try
        {
            response = _httpClient!.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            throw GoErrors.Wrap(ex, "refresh request failed: ");
        }

        var refreshed = OAuthCredentialsJson.ParseTokenResponse(response.Body ?? [], _config.ClientId);

        // Preserve refresh token if not returned in response.
        if (refreshed.RefreshToken.Length == 0)
        {
            refreshed.RefreshToken = credentials.RefreshToken;
        }

        return refreshed;
    }

    private OAuthCredentials? ReadCredentials()
    {
        lock (_sync)
        {
            return _creds;
        }
    }

    private HttpRequestData FormRequest(Dictionary<string, List<string>> form, string url)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/x-www-form-urlencoded"],
            ["Accept"] = ["application/json"],
        };

        if (_config.UserAgent.Length > 0)
        {
            headers["User-Agent"] = [_config.UserAgent];
        }

        return new HttpRequestData
        {
            Method = "POST",
            Url = url,
            Headers = headers,
            Body = Encoding.UTF8.GetBytes(HttpClientUtils.EncodeQuery(form)),
        };
    }

    private void RunAutoRefresh(
        CancellationToken autoToken,
        TimeSpan refreshBefore,
        TimeSpan fallbackInterval)
    {
        try
        {
            var delay = TimeSpan.Zero;
            while (true)
            {
                if (!SleepForAutoRefresh(autoToken, delay))
                {
                    return;
                }

                var (refreshFailed, ok) = RunAutoRefreshOnce(autoToken);
                if (!ok)
                {
                    return;
                }

                delay = refreshFailed
                    ? fallbackInterval
                    : NextAutoRefreshDelay(refreshBefore, fallbackInterval);
            }
        }
        catch (Exception)
        {
            // The reference recovers a panic in this goroutine and ends it.
        }
    }

    private (bool RefreshFailed, bool Ok) RunAutoRefreshOnce(CancellationToken autoToken)
    {
        if (autoToken.IsCancellationRequested)
        {
            return (false, false);
        }

        var refreshFailed = false;
        try
        {
            _ = GetToken(autoToken);
        }
        catch (Exception)
        {
            // The reference logs the failure and retries after the fallback.
            refreshFailed = true;
        }

        if (autoToken.IsCancellationRequested)
        {
            return (refreshFailed, false);
        }

        return (refreshFailed, true);
    }

    private static bool SleepForAutoRefresh(CancellationToken autoToken, TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
        {
            return !autoToken.IsCancellationRequested;
        }

        try
        {
            Task.Delay(ClampDelay(delay), autoToken).GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Task.Delay caps at <see cref="int.MaxValue"/> milliseconds.</summary>
    private static TimeSpan ClampDelay(TimeSpan delay)
        => delay > TimeSpan.FromMilliseconds(int.MaxValue)
            ? TimeSpan.FromMilliseconds(int.MaxValue)
            : delay;

    private TimeSpan NextAutoRefreshDelay(TimeSpan refreshBefore, TimeSpan fallbackInterval)
    {
        var creds = ReadCredentials();

        if (fallbackInterval <= TimeSpan.Zero)
        {
            fallbackInterval = DefaultFallbackInterval;
        }

        if (creds is null || creds.RefreshToken.Length == 0 || creds.ExpiresAt == DateTimeOffset.MinValue)
        {
            return fallbackInterval;
        }

        var target = creds.ExpiresAt - refreshBefore;
        var delay = target - DateTimeOffset.UtcNow;

        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    private static string NonNull(string? value) => value ?? string.Empty;
}
