using AxonHub.Api.Auth;
using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Api.Management;

/// <summary>Port of api.StartCodexOAuthResponse (codex.go:42-45).</summary>
public sealed record CodexStartResult(string SessionId, string AuthUrl);

/// <summary>Port of api.ExchangeCodexOAuthResponse / DecodeCodexAuthJSONResponse (codex.go:109-119).</summary>
public sealed record CodexCredentialsResult(string Credentials);

/// <summary>
/// A JSONError-shaped failure of the Codex OAuth handler, carrying the status
/// the reference passes to <c>JSONError</c>.
/// </summary>
public sealed class CodexOAuthException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Port of api.CodexHandlers' business half (internal/server/api/codex.go): the
/// PKCE session behind POST /admin/codex/oauth/start, the callback exchange of
/// POST /admin/codex/oauth/exchange, and the auth.json normalization of
/// POST /admin/codex/auth/decode.
/// </summary>
/// <remarks>
/// The handler keeps the HTTP binding and JSON envelopes; this service keeps the
/// reference handler's logic so it is testable without a server, matching the
/// port's handler/service split (the Antigravity pair is the same shape).
/// </remarks>
public sealed class CodexOAuthService(
    ICodexOAuthStateCache stateCache,
    IExecutor httpClient,
    IProxyExecutorFactory proxyExecutors,
    TimeProvider timeProvider)
{
    /// <summary>codex.go:82: <c>xcache.WithExpiration(10*time.Minute)</c>.</summary>
    public static readonly TimeSpan StateExpiration = TimeSpan.FromMinutes(10);

    /// <summary>codex.go:52-54.</summary>
    public static string CacheKey(string sessionId) => "codex:oauth:" + sessionId;

    /// <summary>Port of CodexHandlers.StartOAuth (codex.go:58-101).</summary>
    public async Task<CodexStartResult> StartAsync(
        CancellationToken cancellationToken = default)
    {
        string state;
        try
        {
            // oauth.GenerateState(32) can only fail on crypto/rand, exactly like
            // GenerateCodeVerifier below; the reference answers 500 per step.
            state = Pkce.GenerateState(32);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CodexOAuthException(
                500,
                "failed to generate oauth state: " + ex.Message);
        }

        string codeVerifier;
        try
        {
            codeVerifier = Pkce.GenerateCodeVerifier(64);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CodexOAuthException(
                500,
                "failed to generate code verifier: " + ex.Message);
        }

        var codeChallenge = Pkce.GenerateCodeChallenge(codeVerifier);

        try
        {
            await stateCache
                .SetAsync(
                    CacheKey(state),
                    new CodexOAuthState(
                        codeVerifier,
                        timeProvider.GetUtcNow().ToUnixTimeSeconds()),
                    StateExpiration,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CodexOAuthException(
                500,
                "failed to save oauth state: " + ex.Message);
        }

        return new CodexStartResult(state, BuildAuthorizeUrl(state, codeChallenge));
    }

    /// <summary>Port of CodexHandlers.Exchange (codex.go:171-238).</summary>
    public async Task<CodexCredentialsResult> ExchangeAsync(
        string sessionId,
        string callbackUrl,
        ProxyConfig? proxy,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKey(sessionId);

        CodexOAuthState? state;
        try
        {
            state = await stateCache.GetAsync(cacheKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // xcache.Get errors are indistinguishable from a miss in the
            // reference handler (codex.go:189-193).
            _ = ex;
            state = null;
        }

        if (state is null)
        {
            throw new CodexOAuthException(400, "invalid or expired oauth session");
        }

        // codex.go:195-197 deletes the state right after the lookup, before the
        // callback is parsed or validated: a malformed or mismatched callback
        // still consumes the session.
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
            throw new CodexOAuthException(400, "oauth state mismatch");
        }

        // codex.go:210-214: the proxy only applies to a URL proxy with a
        // non-empty URL.
        var executor = httpClient;
        if (proxy is not null && proxy.Type == ProxyType.Url && !string.IsNullOrEmpty(proxy.Url))
        {
            executor = proxyExecutors.Create(proxy);
        }

        var tokenProvider = CodexToken.CreateTokenProvider(
            new CodexTokenProviderParams { HttpClient = executor });

        OAuthCredentials credentials;
        try
        {
            credentials = tokenProvider.Exchange(
                cancellationToken,
                new ExchangeParams
                {
                    Code = code,
                    CodeVerifier = state.CodeVerifier,
                    ClientId = CodexConstants.ClientId,
                    RedirectUri = CodexConstants.RedirectUri,
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CodexOAuthException(
                502,
                "token exchange failed: " + ex.Message);
        }

        return new CodexCredentialsResult(EncodeCredentials(credentials));
    }

    /// <summary>Port of CodexHandlers.DecodeAuthJSON (codex.go:147-169).</summary>
    public CodexCredentialsResult DecodeAuthJson(string authJson)
    {
        OAuthCredentials credentials;
        try
        {
            credentials = CodexToken.DecodeAuthJson(authJson);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CodexOAuthException(
                400,
                "failed to decode auth json: " + ex.Message);
        }

        return new CodexCredentialsResult(EncodeCredentials(credentials));
    }

    /// <summary>Port of parseCodexCallbackURL (codex.go:121-145).</summary>
    internal static (string Code, string State) ParseCallbackUrl(string callbackUrl)
    {
        ArgumentNullException.ThrowIfNull(callbackUrl);

        var trimmed = GoStrings.TrimSpace(callbackUrl);
        if (!trimmed.StartsWith("http://", StringComparison.Ordinal)
            && !trimmed.StartsWith("https://", StringComparison.Ordinal))
        {
            throw new CodexOAuthException(400, "callback_url must be a full URL");
        }

        // url.Parse rejects malformed URLs before the query is read; the port
        // validates with System.Uri, whose message text differs (C202).
        try
        {
            _ = new Uri(trimmed, UriKind.Absolute);
        }
        catch (UriFormatException ex)
        {
            throw new CodexOAuthException(
                400,
                "invalid callback_url: " + ex.Message);
        }

        var query = HttpClientUtils.ParseQuery(RawQuery(trimmed));

        var code = FirstValue(query, "code");
        if (code.Length == 0)
        {
            throw new CodexOAuthException(
                400,
                "code parameter not found in callback_url");
        }

        var state = FirstValue(query, "state");
        if (state.Length == 0)
        {
            throw new CodexOAuthException(
                400,
                "state parameter not found in callback_url");
        }

        return (code, state);
    }

    /// <summary>Port of the authorize URL construction (codex.go:87-98).</summary>
    internal static string BuildAuthorizeUrl(string state, string codeChallenge)
    {
        // url.Values.Encode sorts keys and escapes like url.QueryEscape.
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["response_type"] = ["code"],
            ["client_id"] = [CodexConstants.ClientId],
            ["redirect_uri"] = [CodexConstants.RedirectUri],
            ["scope"] = [CodexConstants.Scopes],
            ["code_challenge"] = [codeChallenge],
            ["code_challenge_method"] = ["S256"],
            ["state"] = [state],
            ["id_token_add_organizations"] = ["true"],
            ["codex_cli_simplified_flow"] = ["true"],
        };

        return CodexConstants.AuthorizeUrl + "?" + HttpClientUtils.EncodeQuery(values);
    }

    private static string EncodeCredentials(OAuthCredentials credentials)
    {
        try
        {
            return credentials.ToJson();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CodexOAuthException(
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
