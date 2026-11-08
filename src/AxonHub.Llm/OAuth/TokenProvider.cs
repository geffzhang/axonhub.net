using System.Text;
using AxonHub.Llm.Concurrency;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Llm.OAuth;

/// <summary>Port of oauth.TokenGetter (llm/oauth/token_provider.go:35-37).</summary>
public interface ITokenGetter
{
    /// <summary>Returns valid credentials, refreshing them when expired.</summary>
    OAuthCredentials Get(CancellationToken cancellationToken);
}

/// <summary>
/// Port of oauth.CredentialConflictError (llm/oauth/token_provider.go:55-64):
/// persistence found newer channel credentials.
/// <see cref="Credentials"/> is the winner; <see langword="null"/> means the
/// channel is no longer OAuth and the refreshing provider must retire its
/// automatic refresh loop.
/// </summary>
public sealed class CredentialConflictException(OAuthCredentials? credentials)
    : Exception("OAuth credentials changed while refreshing")
{
    public OAuthCredentials? Credentials { get; } = credentials;
}

/// <summary>Port of oauth.TokenProviderParams (llm/oauth/token_provider.go:71-81).</summary>
public sealed class TokenProviderParams
{
    public OAuthCredentials? Credentials { get; set; }

    /// <summary>HTTP seam pre-configured with proxy settings if needed.</summary>
    public IExecutor? HttpClient { get; set; }

    public OAuthUrls OAuthUrls { get; set; } = new();

    public string UserAgent { get; set; } = string.Empty;

    /// <summary>
    /// Called after a refresh succeeds. Returning
    /// <see cref="CredentialConflictException"/> makes the provider adopt the
    /// winner; any other failure is logged and ignored, keeping the refreshed
    /// credentials in memory.
    /// </summary>
    public Func<CancellationToken, OAuthCredentials, Exception?>? OnRefreshed { get; set; }

    /// <summary>Token-endpoint encoding; defaults to <see cref="FormEncodedStrategy"/>.</summary>
    public IExchangeStrategy? ExchangeStrategy { get; set; }
}

/// <summary>
/// Port of oauth.TokenProvider (llm/oauth/token_provider.go:39-53): per-channel
/// OAuth2 credentials with singleflight refresh and an optional background
/// refresh loop.
/// </summary>
public sealed class TokenProvider : ITokenGetter
{
    private static readonly TimeSpan DefaultFallbackInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultRefreshBefore = TimeSpan.FromMinutes(5);

    private readonly IExecutor? _httpClient;
    private readonly OAuthUrls _oauthUrls;
    private readonly IExchangeStrategy _strategy;
    /// <summary>
    /// token_provider.go:41-42: both Get and EnsureFresh share one singleflight
    /// key. The value carries the reference's refreshResult.retryExpired flag.
    /// </summary>
    private readonly SingleFlight<(OAuthCredentials Credentials, bool RetryExpired)> _singleFlight = new();
    private readonly object _sync = new();
    private readonly Func<CancellationToken, OAuthCredentials, Exception?>? _onRefreshed;
    private readonly object _autoSync = new();

    private OAuthCredentials? _creds;
    private CancellationTokenSource? _autoCts;

    public TokenProvider(TokenProviderParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        _httpClient = parameters.HttpClient;
        _oauthUrls = parameters.OAuthUrls;
        _creds = parameters.Credentials;
        _onRefreshed = parameters.OnRefreshed;
        _strategy = parameters.ExchangeStrategy
            ?? new FormEncodedStrategy { UserAgent = parameters.UserAgent };
    }

    /// <summary>Port of <c>(*TokenProvider).Exchange</c>: authorization_code grant.</summary>
    public OAuthCredentials Exchange(CancellationToken cancellationToken, ExchangeParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (_httpClient is null)
        {
            throw new InvalidOperationException("http client is nil");
        }

        if (_oauthUrls.TokenUrl.Length == 0)
        {
            throw new InvalidOperationException("token URL is empty");
        }

        if (parameters.Code.Length == 0)
        {
            throw new InvalidOperationException("code is empty");
        }

        if (parameters.CodeVerifier.Length == 0)
        {
            throw new InvalidOperationException("code_verifier is empty");
        }

        if (parameters.ClientId.Length == 0)
        {
            throw new InvalidOperationException("client_id is empty");
        }

        if (parameters.RedirectUri.Length == 0)
        {
            throw new InvalidOperationException("redirect_uri is empty");
        }

        var request = _strategy.BuildExchangeRequest(parameters, _oauthUrls.TokenUrl);

        // The reference returns transport errors unwrapped.
        var response = _httpClient.Do(cancellationToken, request);

        OAuthCredentials creds;
        try
        {
            creds = OAuthCredentialsJson.ParseTokenResponse(response.Body ?? [], parameters.ClientId);
        }
        catch (InvalidOperationException ex)
            when (ex.Message.StartsWith("token request failed: ", StringComparison.Ordinal))
        {
            // Rephrase the exchange failure the way the reference does, keeping
            // the parsed provider error text.
            throw new InvalidOperationException(
                "token exchange failed: " + ex.Message["token request failed: ".Length..],
                ex);
        }

        lock (_sync)
        {
            _creds = creds;
        }

        return creds;
    }

    /// <summary>Port of <c>(*TokenProvider).Get</c>: refresh when expired, deduplicated.</summary>
    public OAuthCredentials Get(CancellationToken cancellationToken)
    {
        var creds = ReadCredentials();
        if (creds is null)
        {
            throw new InvalidOperationException("credentials is nil");
        }

        if (!creds.IsExpired(DateTimeOffset.UtcNow))
        {
            return creds;
        }

        // token_provider.go:196-257: a conflict winner that is still expired is
        // refreshed again, one loop per conflict.
        while (true)
        {
            var (fresh, retryExpired) = _singleFlight.Do(
                "refresh",
                () => RefreshWithinSingleFlight(cancellationToken));

            if (retryExpired && fresh.RefreshToken.Length != 0 && fresh.IsExpired(DateTimeOffset.UtcNow))
            {
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }

            return fresh;
        }
    }

    /// <summary>Port of <c>(*TokenProvider).EnsureFresh</c>: proactive refresh window.</summary>
    public OAuthCredentials EnsureFresh(CancellationToken cancellationToken, TimeSpan refreshBefore)
    {
        var creds = ReadCredentials();
        if (creds is null)
        {
            throw new InvalidOperationException("credentials is nil");
        }

        if (creds.RefreshToken.Length == 0)
        {
            return creds;
        }

        if (refreshBefore <= TimeSpan.Zero)
        {
            refreshBefore = DefaultRefreshBefore;
        }

        var now = DateTimeOffset.UtcNow;
        var shouldRefresh = creds.ExpiresAt == DateTimeOffset.MinValue
            || now.Add(refreshBefore) > creds.ExpiresAt;

        if (!shouldRefresh)
        {
            return creds;
        }

        // token_provider.go:284-351: same conflict retry as Get.
        while (true)
        {
            var (fresh, retryExpired) = _singleFlight.Do(
                "refresh",
                () => RefreshWithinSingleFlight(cancellationToken, refreshBefore));

            if (retryExpired && fresh.RefreshToken.Length != 0 && fresh.IsExpired(DateTimeOffset.UtcNow))
            {
                cancellationToken.ThrowIfCancellationRequested();
                continue;
            }

            return fresh;
        }
    }

    /// <summary>Port of <c>(*TokenProvider).StartAutoRefresh</c>.</summary>
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

    /// <summary>Port of <c>(*TokenProvider).StopAutoRefresh</c>; idempotent.</summary>
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

    private OAuthCredentials? ReadCredentials()
    {
        lock (_sync)
        {
            return _creds;
        }
    }

    private (OAuthCredentials Credentials, bool RetryExpired) RefreshWithinSingleFlight(
        CancellationToken cancellationToken,
        TimeSpan? refreshBefore = null)
    {
        var current = ReadCredentials();
        var onRefreshed = _onRefreshed;

        if (current is null)
        {
            throw new InvalidOperationException("credentials is nil");
        }

        if (refreshBefore is { } window)
        {
            if (current.RefreshToken.Length == 0)
            {
                return (current, false);
            }

            var n = DateTimeOffset.UtcNow;
            var need = current.ExpiresAt == DateTimeOffset.MinValue
                || n.Add(window) > current.ExpiresAt;
            if (!need)
            {
                return (current, false);
            }
        }
        else if (!current.IsExpired(DateTimeOffset.UtcNow))
        {
            return (current, false);
        }

        OAuthCredentials? fresh = Refresh(cancellationToken, current);
        var retryExpired = false;

        if (onRefreshed is not null)
        {
            // token_provider.go:217-228: a credential conflict adopts the
            // winner (possibly nil, retiring the provider); any other
            // persistence failure is logged and the refreshed credentials stay
            // in memory.
            var error = onRefreshed(cancellationToken, fresh);
            if (error is CredentialConflictException conflict)
            {
                fresh = conflict.Credentials;
                retryExpired = fresh is not null;
            }
        }

        lock (_sync)
        {
            _creds = fresh;
        }

        if (fresh is null)
        {
            StopAutoRefresh();
            throw new InvalidOperationException("credentials is nil");
        }

        return (fresh, retryExpired);
    }

    /// <summary>Port of <c>(*TokenProvider).refresh</c>.</summary>
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

        if (_oauthUrls.TokenUrl.Length == 0)
        {
            throw new InvalidOperationException("token URL is empty");
        }

        if (_httpClient is null)
        {
            throw new InvalidOperationException("http client is nil");
        }

        var request = _strategy.BuildRefreshRequest(credentials, _oauthUrls.TokenUrl);

        HttpResponseData response;
        try
        {
            response = _httpClient.Do(cancellationToken, request);
        }
        catch (Exception ex)
        {
            throw WrapHttpError(ex);
        }

        var refreshed = OAuthCredentialsJson.ParseTokenResponse(response.Body ?? [], credentials.ClientId);

        // Preserve refresh token if not returned in response.
        if (refreshed.RefreshToken.Length == 0)
        {
            refreshed.RefreshToken = credentials.RefreshToken;
        }

        return refreshed;
    }

    /// <summary>Port of <c>wrapHttpError</c>: append the upstream error body.</summary>
    internal static Exception WrapHttpError(Exception error)
    {
        var httpError = GoErrors.As<HttpClientError>(error);
        if (httpError is not null && httpError.Body.Length > 0)
        {
            return new WrappedException(
                error.Message + " (response body: " + Encoding.UTF8.GetString(httpError.Body) + ")",
                error);
        }

        return error;
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

                var (refreshFailed, ok) = RunAutoRefreshOnce(autoToken, refreshBefore);
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

    private (bool RefreshFailed, bool Ok) RunAutoRefreshOnce(
        CancellationToken autoToken,
        TimeSpan refreshBefore)
    {
        if (autoToken.IsCancellationRequested)
        {
            return (false, false);
        }

        var refreshFailed = false;
        try
        {
            _ = EnsureFresh(autoToken, refreshBefore);
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
}

/// <summary>Port of oauth.StaticTokenProvider (llm/oauth/token_provider.go:416-427).</summary>
public sealed class StaticTokenProvider(OAuthCredentials? credentials) : ITokenGetter
{
    private readonly OAuthCredentials? _credentials = credentials;

    public OAuthCredentials Get(CancellationToken cancellationToken) => _credentials!;
}

/// <summary>Port of oauth.APIKeyProviderFunc (llm/oauth/token_provider.go:429-434).</summary>
public delegate string ApiKeyProvider(CancellationToken cancellationToken);

/// <summary>
/// Port of oauth.APIKeyTokenProvider (llm/oauth/token_provider.go:436-457): lets
/// transformers that expect OAuth tokens work with a plain API key.
/// </summary>
public sealed class ApiKeyTokenProvider(ApiKeyProvider provider) : ITokenGetter
{
    public OAuthCredentials Get(CancellationToken cancellationToken)
    {
        var apiKey = provider(cancellationToken);
        if (apiKey.Length == 0)
        {
            throw new InvalidOperationException("api key is empty");
        }

        return new OAuthCredentials { AccessToken = apiKey };
    }
}
