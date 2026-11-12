using AxonHub.Api.Auth;
using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Llm.Transformer.Xai.Subscription;

namespace AxonHub.Api.Management;

/// <summary>Port of api.StartXAIOAuthResponse (xai.go:39-42).</summary>
public sealed record XaiStartResult(string SessionId, string AuthUrl);

/// <summary>
/// Port of api.ExchangeXAIOAuthResponse / DecodeXAISSOResponse (xai.go:50-62).
/// </summary>
public sealed record XaiCredentialsResult(string Credentials);

/// <summary>
/// A JSONError-shaped failure of the xAI OAuth handler, carrying the status the
/// reference passes to <c>JSONError</c>.
/// </summary>
public sealed class XaiOAuthException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Port of api.XAIHandlers' business half (internal/server/api/xai.go): the
/// PKCE session behind POST /admin/xai/oauth/start, the callback exchange of
/// POST /admin/xai/oauth/exchange, and the SSO-cookie conversion of
/// POST /admin/xai/oauth/sso.
/// </summary>
/// <remarks>
/// <paramref name="ssoTransportFactory"/> is a port-only seam: the reference
/// hands the SSO flow its shared native <c>*http.Client</c> (xai.go:192), while
/// the port's <see cref="IExecutor"/> carries no transport. Production passes
/// null and the proxy branch below builds an <see cref="HttpProxyExecutorFactory"/>
/// transport; tests use the seam to script the SSO exchange.
/// </remarks>
public sealed class XaiOAuthService(
    IXaiOAuthStateCache stateCache,
    IExecutor httpClient,
    IProxyExecutorFactory proxyExecutors,
    IManagementIdentity identity,
    Func<ProxyConfig?, HttpMessageHandler?>? ssoTransportFactory = null)
{
    /// <summary>xai.go:101: <c>xcache.WithExpiration(30*time.Minute)</c>.</summary>
    public static readonly TimeSpan StateExpiration = TimeSpan.FromMinutes(30);

    /// <summary>xai.go:70-72.</summary>
    public static string CacheKey(string sessionId) => "xai:oauth:" + sessionId;

    /// <summary>Port of XAIHandlers.StartOAuth (xai.go:74-122).</summary>
    public async Task<XaiStartResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        var user = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            throw new XaiOAuthException(401, "authentication required");
        }

        string state;
        try
        {
            state = Pkce.GenerateState(32);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new XaiOAuthException(
                500,
                "generate xAI oauth state: " + ex.Message);
        }

        string codeVerifier;
        try
        {
            codeVerifier = Pkce.GenerateCodeVerifier(32);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new XaiOAuthException(
                500,
                "generate xAI code verifier: " + ex.Message);
        }

        string nonce;
        try
        {
            nonce = Pkce.GenerateState(16);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new XaiOAuthException(
                500,
                "generate xAI nonce: " + ex.Message);
        }

        try
        {
            await stateCache
                .SetAsync(
                    CacheKey(state),
                    new XaiOAuthState(codeVerifier, user.Id),
                    StateExpiration,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new XaiOAuthException(
                500,
                "save xAI oauth state: " + ex.Message);
        }

        return new XaiStartResult(state, BuildAuthorizeUrl(state, nonce, codeVerifier));
    }

    /// <summary>Port of XAIHandlers.Exchange (xai.go:124-177).</summary>
    public async Task<XaiCredentialsResult> ExchangeAsync(
        string sessionId,
        string callbackUrl,
        ProxyConfig? proxy,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKey(sessionId);

        XaiOAuthState? state;
        try
        {
            state = await stateCache.GetAsync(cacheKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // xcache.Get errors are indistinguishable from a miss (xai.go:133-137).
            _ = ex;
            state = null;
        }

        if (state is null)
        {
            throw new XaiOAuthException(400, "invalid or expired oauth session");
        }

        var user = await identity.GetCurrentUserAsync(cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            throw new XaiOAuthException(401, "authentication required");
        }

        // The session belongs to the user who started it; a mismatch leaves
        // the state in the cache (xai.go:143-146).
        if (state.UserId != user.Id)
        {
            throw new XaiOAuthException(400, "invalid or expired oauth session");
        }

        var (code, callbackState) = ParseCallbackUrl(callbackUrl);
        if (!string.Equals(callbackState, sessionId, StringComparison.Ordinal))
        {
            throw new XaiOAuthException(400, "oauth state mismatch");
        }

        // xai.go:156-158: deleted only after the callback validated; a
        // deletion failure is logged, never surfaced.
        try
        {
            await stateCache.DeleteAsync(cacheKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Warn only.
        }

        var executor = httpClient;
        if (proxy is not null && proxy.Type == ProxyType.Url && !string.IsNullOrEmpty(proxy.Url))
        {
            executor = proxyExecutors.Create(proxy);
        }

        var tokenProvider = SubscriptionToken.NewTokenProvider(
            new SubscriptionTokenProviderParams { HttpClient = executor });

        OAuthCredentials credentials;
        try
        {
            credentials = tokenProvider.Exchange(
                cancellationToken,
                new ExchangeParams
                {
                    Code = code,
                    CodeVerifier = state.CodeVerifier,
                    ClientId = SubscriptionConstants.ClientId,
                    RedirectUri = SubscriptionConstants.RedirectUri,
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new XaiOAuthException(
                502,
                "xAI token exchange failed: " + ex.Message);
        }

        return new XaiCredentialsResult(EncodeCredentials(credentials));
    }

    /// <summary>Port of XAIHandlers.DecodeSSO (xai.go:179-203).</summary>
    public XaiCredentialsResult DecodeSso(
        string ssoToken,
        ProxyConfig? proxy,
        CancellationToken cancellationToken = default)
    {
        var transport = CreateSsoTransport(proxy);

        OAuthCredentials credentials;
        try
        {
            credentials = SubscriptionSsoDevice.ConvertSsoToBuild(
                cancellationToken,
                ssoToken,
                new SsoDeviceOptions { Transport = transport });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new XaiOAuthException(
                502,
                "xAI SSO conversion failed: " + ex.Message);
        }
        finally
        {
            // SsoDeviceOptions does not take ownership of an injected
            // transport (SubscriptionSsoDevice uses disposeHandler: false).
            transport?.Dispose();
        }

        return new XaiCredentialsResult(EncodeCredentials(credentials));
    }

    /// <summary>Port of parseXAICallbackURL (xai.go:205-216).</summary>
    internal static (string Code, string State) ParseCallbackUrl(string callbackUrl)
    {
        ArgumentNullException.ThrowIfNull(callbackUrl);

        var trimmed = GoStrings.TrimSpace(callbackUrl);
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new XaiOAuthException(400, "callback_url must be a full URL");
        }

        var query = HttpClientUtils.ParseQuery(RawQuery(trimmed));

        var code = FirstValue(query, "code");
        var state = FirstValue(query, "state");
        if (code.Length == 0 || state.Length == 0)
        {
            throw new XaiOAuthException(
                400,
                "callback_url must contain code and state");
        }

        return (code, state);
    }

    /// <summary>Port of the authorize URL construction (xai.go:106-120).</summary>
    internal static string BuildAuthorizeUrl(
        string state,
        string nonce,
        string codeVerifier)
    {
        // url.Values.Encode sorts keys and escapes like url.QueryEscape.
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["response_type"] = ["code"],
            ["client_id"] = [SubscriptionConstants.ClientId],
            ["redirect_uri"] = [SubscriptionConstants.RedirectUri],
            ["scope"] = [SubscriptionConstants.Scopes],
            ["state"] = [state],
            ["nonce"] = [nonce],
            ["code_challenge"] = [Pkce.GenerateCodeChallenge(codeVerifier)],
            ["code_challenge_method"] = ["S256"],
            ["plan"] = ["generic"],
            ["referrer"] = ["axonhub"],
        };

        return SubscriptionConstants.AuthorizeUrl + "?" + HttpClientUtils.EncodeQuery(values);
    }

    private HttpMessageHandler? CreateSsoTransport(ProxyConfig? proxy)
    {
        if (ssoTransportFactory is not null)
        {
            return ssoTransportFactory(proxy);
        }

        // xai.go:188-192: only a URL proxy with a non-empty URL swaps the
        // native client; otherwise the shared default transport is used.
        return proxy is not null
            && proxy.Type == ProxyType.Url
            && !string.IsNullOrEmpty(proxy.Url)
                ? HttpProxyExecutorFactory.CreateTransport(proxy)
                : null;
    }

    private static string EncodeCredentials(OAuthCredentials credentials)
    {
        try
        {
            return credentials.ToJson();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new XaiOAuthException(
                500,
                "encode xAI credentials: " + ex.Message);
        }
    }

    /// <summary>
    /// The substring Go's <c>url.URL.RawQuery</c> would hold: everything after
    /// the first <c>?</c> that precedes the fragment.
    /// </summary>
    private static string RawQuery(string url)
    {
        var hash = url.IndexOf('#', StringComparison.Ordinal);
        var beforeFragment = hash < 0 ? url : url[..hash];
        var question = beforeFragment.IndexOf('?', StringComparison.Ordinal);
        return question < 0 ? string.Empty : beforeFragment[(question + 1)..];
    }

    /// <summary>url.Values.Get: the first value, or an empty string.</summary>
    private static string FirstValue(Dictionary<string, List<string>> query, string key)
        => query.TryGetValue(key, out var values) && values.Count > 0
            ? values[0]
            : string.Empty;
}
