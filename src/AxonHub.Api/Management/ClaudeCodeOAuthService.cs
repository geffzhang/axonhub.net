using AxonHub.Api.Auth;
using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Anthropic.ClaudeCode;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Api.Management;

/// <summary>Port of api.StartClaudeCodeOAuthResponse (claudecode.go:45-48).</summary>
public sealed record ClaudeCodeStartResult(string SessionId, string AuthUrl);

/// <summary>Port of api.ExchangeClaudeCodeOAuthResponse (claudecode.go:133-135).</summary>
public sealed record ClaudeCodeCredentialsResult(string Credentials);

/// <summary>
/// A JSONError-shaped failure of the Claude Code OAuth handler, carrying the
/// status the reference passes to <c>JSONError</c>.
/// </summary>
public sealed class ClaudeCodeOAuthException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Port of api.ClaudeCodeHandlers' business half
/// (internal/server/api/claudecode.go): the PKCE session behind
/// POST /admin/claudecode/oauth/start and the callback exchange of
/// POST /admin/claudecode/oauth/exchange.
/// </summary>
public sealed class ClaudeCodeOAuthService(
    IClaudeCodeOAuthStateCache stateCache,
    IExecutor httpClient,
    IProxyExecutorFactory proxyExecutors,
    TimeProvider timeProvider)
{
    /// <summary>claudecode.go:108: <c>xcache.WithExpiration(10*time.Minute)</c>.</summary>
    public static readonly TimeSpan StateExpiration = TimeSpan.FromMinutes(10);

    /// <summary>claudecode.go:78-80.</summary>
    public static string CacheKey(string sessionId) => "claudecode:oauth:" + sessionId;

    /// <summary>Port of ClaudeCodeHandlers.StartOAuth (claudecode.go:82-125).</summary>
    public async Task<ClaudeCodeStartResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        // generateClaudeCodeState (claudecode.go:69-76) is Pkce.GenerateState(32):
        // 32 crypto/rand bytes, base64url without padding.
        string state;
        try
        {
            state = Pkce.GenerateState(32);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ClaudeCodeOAuthException(
                500,
                "failed to generate oauth state: " + ex.Message);
        }

        // generateClaudeCodeCodeVerifier (claudecode.go:55-62) is the same
        // encoding over 64 bytes.
        string codeVerifier;
        try
        {
            codeVerifier = Pkce.GenerateCodeVerifier(64);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ClaudeCodeOAuthException(
                500,
                "failed to generate code verifier: " + ex.Message);
        }

        var codeChallenge = Pkce.GenerateCodeChallenge(codeVerifier);

        try
        {
            await stateCache
                .SetAsync(
                    CacheKey(state),
                    new ClaudeCodeOAuthState(
                        codeVerifier,
                        timeProvider.GetUtcNow().ToUnixTimeSeconds()),
                    StateExpiration,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ClaudeCodeOAuthException(
                500,
                "failed to save oauth state: " + ex.Message);
        }

        return new ClaudeCodeStartResult(state, BuildAuthorizeUrl(state, codeChallenge));
    }

    /// <summary>Port of ClaudeCodeHandlers.Exchange (claudecode.go:171-234).</summary>
    public async Task<ClaudeCodeCredentialsResult> ExchangeAsync(
        string sessionId,
        string callbackUrl,
        ProxyConfig? proxy,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKey(sessionId);

        ClaudeCodeOAuthState? state;
        try
        {
            state = await stateCache.GetAsync(cacheKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // xcache.Get errors are indistinguishable from a miss
            // (claudecode.go:184-188).
            _ = ex;
            state = null;
        }

        if (state is null)
        {
            throw new ClaudeCodeOAuthException(400, "invalid or expired oauth session");
        }

        // claudecode.go:190-192 deletes the state right after the lookup,
        // before the callback is parsed or validated: a malformed or
        // mismatched callback still consumes the session.
        try
        {
            await stateCache.DeleteAsync(cacheKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Warn only.
        }

        var (code, callbackState) = ParseCallbackUrl(callbackUrl);
        if (!string.Equals(callbackState, sessionId, StringComparison.Ordinal))
        {
            throw new ClaudeCodeOAuthException(400, "oauth state mismatch");
        }

        var executor = httpClient;
        if (proxy is not null && proxy.Type == ProxyType.Url && !string.IsNullOrEmpty(proxy.Url))
        {
            executor = proxyExecutors.Create(proxy);
        }

        var tokenProvider = ClaudeCodeTokenProvider.Create(
            new TokenProviderParams { HttpClient = executor });

        OAuthCredentials credentials;
        try
        {
            credentials = tokenProvider.Exchange(
                cancellationToken,
                new ExchangeParams
                {
                    Code = code,
                    State = callbackState,
                    CodeVerifier = state.CodeVerifier,
                    ClientId = ClaudeCodeConstants.ClientId,
                    RedirectUri = ClaudeCodeConstants.RedirectUri,
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ClaudeCodeOAuthException(
                502,
                "token exchange failed: " + ex.Message);
        }

        return new ClaudeCodeCredentialsResult(EncodeCredentials(credentials));
    }

    /// <summary>Port of parseClaudeCodeCallbackURL (claudecode.go:137-169).</summary>
    internal static (string Code, string State) ParseCallbackUrl(string callbackUrl)
    {
        ArgumentNullException.ThrowIfNull(callbackUrl);

        var trimmed = GoStrings.TrimSpace(callbackUrl);
        if (!trimmed.StartsWith("http://", StringComparison.Ordinal)
            && !trimmed.StartsWith("https://", StringComparison.Ordinal))
        {
            throw new ClaudeCodeOAuthException(400, "callback_url must be a full URL");
        }

        Uri parsed;
        try
        {
            parsed = new Uri(trimmed, UriKind.Absolute);
        }
        catch (UriFormatException ex)
        {
            throw new ClaudeCodeOAuthException(
                400,
                "invalid callback_url: " + ex.Message);
        }

        var query = HttpClientUtils.ParseQuery(RawQuery(trimmed));

        var code = FirstValue(query, "code");
        if (code.Length == 0)
        {
            throw new ClaudeCodeOAuthException(
                400,
                "code parameter not found in callback_url");
        }

        // Claude puts state in the URL fragment, for example
        // http://localhost:54545/callback?code=xxx#state; the query is the
        // fallback (claudecode.go:155-163).
        var state = Fragment(parsed);
        if (state.Length == 0)
        {
            state = FirstValue(query, "state");
        }

        if (state.Length == 0)
        {
            throw new ClaudeCodeOAuthException(
                400,
                "state parameter not found in callback_url (should be after # or in query)");
        }

        return (code, state);
    }

    /// <summary>Port of the authorize URL construction (claudecode.go:113-122).</summary>
    internal static string BuildAuthorizeUrl(string state, string codeChallenge)
    {
        // url.Values.Encode sorts keys and escapes like url.QueryEscape.
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["response_type"] = ["code"],
            ["client_id"] = [ClaudeCodeConstants.ClientId],
            ["redirect_uri"] = [ClaudeCodeConstants.RedirectUri],
            ["scope"] = [ClaudeCodeConstants.Scopes],
            ["code_challenge"] = [codeChallenge],
            ["code_challenge_method"] = ["S256"],
            ["state"] = [state],
        };

        return ClaudeCodeConstants.AuthorizeUrl + "?" + HttpClientUtils.EncodeQuery(values);
    }

    /// <summary>
    /// Go's <c>url.URL.Fragment</c>: percent-decoded and without the leading
    /// <c>#</c>; .NET keeps the delimiter and the escaped form.
    /// </summary>
    private static string Fragment(Uri uri)
        => uri.Fragment.Length > 1 ? Uri.UnescapeDataString(uri.Fragment[1..]) : string.Empty;

    private static string EncodeCredentials(OAuthCredentials credentials)
    {
        try
        {
            return credentials.ToJson();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new ClaudeCodeOAuthException(
                500,
                "failed to encode credentials: " + ex.Message);
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
