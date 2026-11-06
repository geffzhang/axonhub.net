using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Antigravity;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Api.Management;

/// <summary>Port of api.StartAntigravityOAuthResponse (antigravity.go:49-52).</summary>
public sealed record AntigravityStartResult(string SessionId, string AuthUrl);

/// <summary>Port of api.ExchangeAntigravityOAuthResponse (antigravity.go:146-148).</summary>
public sealed record AntigravityExchangeResult(string Credentials);

/// <summary>
/// A JSONError-shaped failure of the Antigravity OAuth handler, carrying the
/// status the reference passes to <c>JSONError</c>.
/// </summary>
public sealed class AntigravityOAuthException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Port of api.AntigravityHandlers' business half (internal/server/api/antigravity.go):
/// the PKCE session behind POST /admin/antigravity/oauth/start, the callback
/// exchange of POST /admin/antigravity/oauth/exchange, and the project
/// discovery fallback (loadCodeAssist / onboardUser).
/// </summary>
/// <remarks>
/// The handler keeps the HTTP binding and JSON envelopes; this service keeps the
/// reference handler's logic so it is testable without a server, matching the
/// port's handler/service split.
/// </remarks>
public sealed class AntigravityOAuthService(
    IAntigravityOAuthStateCache stateCache,
    IExecutor httpClient,
    IProxyExecutorFactory proxyExecutors,
    TimeProvider timeProvider)
{
    /// <summary>antigravity.go:119: <c>xcache.WithExpiration(10*time.Minute)</c>.</summary>
    public static readonly TimeSpan StateExpiration = TimeSpan.FromMinutes(10);

    /// <summary>antigravity.go:83-85.</summary>
    public static string CacheKey(string sessionId) => "antigravity:oauth:" + sessionId;

    /// <summary>Port of AntigravityHandlers.StartOAuth (antigravity.go:87-138).</summary>
    public async Task<AntigravityStartResult> StartAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        string state;
        string codeVerifier;
        try
        {
            state = GenerateState();
            codeVerifier = GenerateCodeVerifier();
        }
        catch (CryptographicException ex)
        {
            // crypto/rand failure is effectively unreachable; the reference
            // answers 500 "internal server error" (antigravity.go:98-110).
            throw new InvalidOperationException("internal server error", ex);
        }

        var codeChallenge = GenerateCodeChallenge(codeVerifier);

        try
        {
            await stateCache
                .SetAsync(
                    CacheKey(state),
                    new AntigravityOAuthState(
                        codeVerifier,
                        projectId,
                        timeProvider.GetUtcNow().ToUnixTimeSeconds()),
                    StateExpiration,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to save oauth state: " + ex.Message,
                ex);
        }

        return new AntigravityStartResult(state, BuildAuthorizeUrl(state, codeChallenge));
    }

    /// <summary>Port of AntigravityHandlers.Exchange (antigravity.go:176-247).</summary>
    public async Task<AntigravityExchangeResult> ExchangeAsync(
        string sessionId,
        string callbackUrl,
        ProxyConfig? proxy,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKey(sessionId);

        AntigravityOAuthState? state;
        try
        {
            state = await stateCache.GetAsync(cacheKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // xcache.Get errors are indistinguishable from a miss in the
            // reference handler (antigravity.go:189-193).
            _ = ex;
            state = null;
        }

        if (state is null)
        {
            throw new AntigravityOAuthException(400, "invalid or expired oauth session");
        }

        var (code, callbackState) = ParseCallbackUrl(callbackUrl);
        if (!string.Equals(callbackState, sessionId, StringComparison.Ordinal))
        {
            throw new AntigravityOAuthException(400, "oauth state mismatch");
        }

        // Delete state after validation succeeds; a failure is only logged in
        // the reference (antigravity.go:206-209).
        try
        {
            await stateCache.DeleteAsync(cacheKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Warn only.
        }

        // antigravity.go:211-215: the proxy only applies to a URL proxy with a
        // non-empty URL.
        var executor = httpClient;
        if (proxy is not null && proxy.Type == ProxyType.Url && !string.IsNullOrEmpty(proxy.Url))
        {
            executor = proxyExecutors.Create(proxy);
        }

        var tokenProvider = AntigravityTokenProviderFactory.NewTokenProvider(
            new TokenProviderParams { HttpClient = executor });

        OAuthCredentials credentials;
        try
        {
            credentials = tokenProvider.Exchange(
                cancellationToken,
                new ExchangeParams
                {
                    Code = code,
                    CodeVerifier = state.CodeVerifier,
                    ClientId = AntigravityConstants.ClientID,
                    RedirectUri = AntigravityConstants.RedirectURI,
                });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new AntigravityOAuthException(
                502,
                "token exchange failed: " + ex.Message);
        }

        var projectId = state.ProjectId;
        if (projectId.Length == 0)
        {
            try
            {
                projectId = await ResolveProjectIdAsync(
                        credentials.AccessToken,
                        executor,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AntigravityOAuthException(
                    502,
                    "failed to resolve project id and none provided: " + ex.Message);
            }
        }

        // antigravity.go:243-244: refreshToken|projectId.
        return new AntigravityExchangeResult(credentials.RefreshToken + "|" + projectId);
    }

    /// <summary>Port of parseAntigravityCallbackURL (antigravity.go:150-174).</summary>
    internal static (string Code, string State) ParseCallbackUrl(string callbackUrl)
    {
        ArgumentNullException.ThrowIfNull(callbackUrl);

        var trimmed = GoStrings.TrimSpace(callbackUrl);
        if (!trimmed.StartsWith("http://", StringComparison.Ordinal)
            && !trimmed.StartsWith("https://", StringComparison.Ordinal))
        {
            throw new AntigravityOAuthException(400, "callback_url must be a full URL");
        }

        // url.Parse rejects malformed URLs before the query is read; the port
        // validates with System.Uri, whose message text differs (C202).
        try
        {
            _ = new Uri(trimmed, UriKind.Absolute);
        }
        catch (UriFormatException ex)
        {
            throw new AntigravityOAuthException(
                400,
                "invalid callback_url: " + ex.Message);
        }

        var query = HttpClientUtils.ParseQuery(RawQuery(trimmed));

        var code = FirstValue(query, "code");
        if (code.Length == 0)
        {
            throw new AntigravityOAuthException(
                400,
                "code parameter not found in callback_url");
        }

        var state = FirstValue(query, "state");
        if (state.Length == 0)
        {
            throw new AntigravityOAuthException(
                400,
                "state parameter not found in callback_url");
        }

        return (code, state);
    }

    /// <summary>
    /// Port of resolveProjectID (antigravity.go:249-346): try each load
    /// endpoint in order, return its project, or onboard a user and retry.
    /// </summary>
    internal async Task<string> ResolveProjectIdAsync(
        string accessToken,
        IExecutor executor,
        CancellationToken cancellationToken = default)
    {
        if (AntigravityConstants.LoadEndpoints.Count == 0)
        {
            throw new InvalidOperationException("no load endpoints configured");
        }

        Exception? lastErr = null;
        var defaultTierId = "FREE";

        foreach (var baseEndpoint in AntigravityConstants.LoadEndpoints)
        {
            var request = BuildProjectRequest(
                baseEndpoint + "/v1internal:loadCodeAssist",
                LoadCodeAssistBody(),
                accessToken);

            HttpResponseData response;
            try
            {
                response = executor.Do(cancellationToken, request);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastErr = ex;
                continue;
            }

            if (response.StatusCode != 200)
            {
                lastErr = new InvalidOperationException(
                    "status " + response.StatusCode.ToString(CultureInfo.InvariantCulture));
                continue;
            }

            LoadCodeAssistResponse data;
            try
            {
                data = ParseLoadCodeAssist(response.Body);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastErr = ex;
                continue;
            }

            if (data.ProjectId.Length > 0)
            {
                return data.ProjectId;
            }

            // antigravity.go:324-332: the first tier is the fallback; an
            // explicit isDefault tier wins.
            if (data.AllowedTiers.Count > 0)
            {
                defaultTierId = data.AllowedTiers[0].Id;
                foreach (var tier in data.AllowedTiers)
                {
                    if (tier.IsDefault)
                    {
                        defaultTierId = tier.Id;
                        break;
                    }
                }
            }

            try
            {
                var projectId = await OnboardUserAsync(
                        accessToken,
                        defaultTierId,
                        executor,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (projectId.Length > 0)
                {
                    return projectId;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastErr = ex;
            }
        }

        // The reference returns ("", lastErr); every failed endpoint leaves a
        // lastErr in practice.
        throw lastErr ?? new InvalidOperationException(
            "failed to resolve project id");
    }

    /// <summary>Port of onboardUser (antigravity.go:348-405).</summary>
    internal async Task<string> OnboardUserAsync(
        string accessToken,
        string tierId,
        IExecutor executor,
        CancellationToken cancellationToken = default)
    {
        foreach (var baseEndpoint in AntigravityConstants.LoadEndpoints)
        {
            // The reference builds the request once per endpoint and reuses it
            // for the three attempts (antigravity.go:350-373).
            var request = BuildProjectRequest(
                baseEndpoint + "/v1internal:onboardUser",
                OnboardUserBody(tierId),
                accessToken);

            for (var attempt = 0; attempt < 3; attempt++)
            {
                HttpResponseData response;
                try
                {
                    response = executor.Do(cancellationToken, request);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _ = ex;
                    await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                if (response.StatusCode != 200)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                OnboardResponse data;
                try
                {
                    data = ParseOnboardUser(response.Body);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // antigravity.go:392-394: an undecodable body retries
                    // without the two-second wait.
                    continue;
                }

                if (data.Done && data.ProjectId.Length > 0)
                {
                    return data.ProjectId;
                }

                await Task.Delay(TimeSpan.FromSeconds(2), timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("failed to onboard user after retries");
    }

    /// <summary>Port of generateAntigravityState (antigravity.go:74-81).</summary>
    internal static string GenerateState()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return System.Buffers.Text.Base64Url.EncodeToString(bytes);
    }

    /// <summary>Port of generateAntigravityCodeVerifier (antigravity.go:60-67).</summary>
    internal static string GenerateCodeVerifier()
    {
        var bytes = new byte[64];
        RandomNumberGenerator.Fill(bytes);
        return System.Buffers.Text.Base64Url.EncodeToString(bytes);
    }

    /// <summary>Port of generateAntigravityCodeChallenge (antigravity.go:69-72).</summary>
    internal static string GenerateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return System.Buffers.Text.Base64Url.EncodeToString(hash);
    }

    /// <summary>Port of the authorize URL construction (antigravity.go:124-135).</summary>
    internal static string BuildAuthorizeUrl(string state, string codeChallenge)
    {
        // url.Values.Encode sorts keys and escapes like url.QueryEscape.
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["response_type"] = ["code"],
            ["client_id"] = [AntigravityConstants.ClientID],
            ["redirect_uri"] = [AntigravityConstants.RedirectURI],
            ["scope"] = [AntigravityConstants.ScopesString],
            ["code_challenge"] = [codeChallenge],
            ["code_challenge_method"] = ["S256"],
            ["state"] = [state],
            ["access_type"] = ["offline"],
            ["prompt"] = ["consent"],
        };

        return AntigravityConstants.AuthorizeURL + "?" + HttpClientUtils.EncodeQuery(values);
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

    private static HttpRequestData BuildProjectRequest(
        string url,
        byte[] body,
        string accessToken)
        => new()
        {
            Method = "POST",
            Url = url,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Authorization"] = ["Bearer " + accessToken],
                ["Content-Type"] = ["application/json"],
                ["User-Agent"] = [AntigravityVersion.GetUserAgent()],
                ["X-Goog-Api-Client"] = [AntigravityConstants.ApiClient],
                ["Client-Metadata"] = [AntigravityConstants.ClientMetadata],
            },
            Body = body,
        };

    /// <summary>json.Marshal of the loadCodeAssist body (antigravity.go:260-266).</summary>
    private static byte[] LoadCodeAssistBody()
        => Encoding.UTF8.GetBytes(
            """{"metadata":{"ideType":"ANTIGRAVITY","platform":"PLATFORM_UNSPECIFIED","pluginType":"GEMINI"}}""");

    /// <summary>json.Marshal of the onboardUser body (antigravity.go:352-359).</summary>
    private static byte[] OnboardUserBody(string tierId)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("metadata");
            writer.WriteStartObject();
            writer.WriteString("ideType", "ANTIGRAVITY");
            writer.WriteString("platform", "PLATFORM_UNSPECIFIED");
            writer.WriteString("pluginType", "GEMINI");
            writer.WriteEndObject();
            writer.WriteString("tierId", tierId);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Port of the loadCodeAssist response decode (antigravity.go:297-307): Go
    /// unmarshals into a struct, so a type mismatch anywhere fails the whole
    /// decode, while a null or absent member keeps the zero value.
    /// </summary>
    internal static LoadCodeAssistResponse ParseLoadCodeAssist(byte[]? body)
    {
        using var document = ParseObject(body);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Null)
        {
            return new LoadCodeAssistResponse(string.Empty, []);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Mismatch(root.ValueKind, "Go value of type struct");
        }

        var projectId = string.Empty;
        if (TryGetProperty(root, "cloudaicompanionProject", out var project))
        {
            // any accepts every JSON value; only a non-empty string or an
            // object with a string id yields a project (antigravity.go:310-317).
            if (project.ValueKind == JsonValueKind.String)
            {
                projectId = project.GetString() ?? string.Empty;
            }
            else if (project.ValueKind == JsonValueKind.Object
                && TryGetProperty(project, "id", out var id)
                && id.ValueKind == JsonValueKind.String)
            {
                projectId = id.GetString() ?? string.Empty;
            }
        }

        var tiers = new List<AllowedTier>();
        if (TryGetProperty(root, "allowedTiers", out var allowed))
        {
            if (allowed.ValueKind == JsonValueKind.Null)
            {
                return new LoadCodeAssistResponse(projectId, tiers);
            }

            if (allowed.ValueKind != JsonValueKind.Array)
            {
                throw Mismatch(
                    allowed.ValueKind,
                    "Go struct field .allowedTiers of type []struct { ... }");
            }

            foreach (var element in allowed.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.Null)
                {
                    tiers.Add(new AllowedTier(string.Empty, false));
                    continue;
                }

                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw Mismatch(element.ValueKind, "Go value of type struct");
                }

                var tierId = string.Empty;
                if (TryGetProperty(element, "id", out var id)
                    && id.ValueKind != JsonValueKind.Null)
                {
                    if (id.ValueKind != JsonValueKind.String)
                    {
                        throw Mismatch(
                            id.ValueKind,
                            "Go struct field .allowedTiers.id of type string");
                    }

                    tierId = id.GetString() ?? string.Empty;
                }

                var isDefault = false;
                if (TryGetProperty(element, "isDefault", out var defaultElement)
                    && defaultElement.ValueKind != JsonValueKind.Null)
                {
                    if (defaultElement.ValueKind
                        is not (JsonValueKind.True or JsonValueKind.False))
                    {
                        throw Mismatch(
                            defaultElement.ValueKind,
                            "Go struct field .allowedTiers.isDefault of type bool");
                    }

                    isDefault = defaultElement.GetBoolean();
                }

                tiers.Add(new AllowedTier(tierId, isDefault));
            }
        }

        return new LoadCodeAssistResponse(projectId, tiers);
    }

    /// <summary>
    /// Port of the onboardUser response decode (antigravity.go:383-395): a
    /// bool done flag and response.cloudaicompanionProject.id.
    /// </summary>
    internal static OnboardResponse ParseOnboardUser(byte[]? body)
    {
        using var document = ParseObject(body);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Null)
        {
            return new OnboardResponse(false, string.Empty);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw Mismatch(root.ValueKind, "Go value of type struct");
        }

        var done = false;
        if (TryGetProperty(root, "done", out var doneElement)
            && doneElement.ValueKind != JsonValueKind.Null)
        {
            if (doneElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw Mismatch(doneElement.ValueKind, "Go struct field .done of type bool");
            }

            done = doneElement.GetBoolean();
        }

        var projectId = string.Empty;
        if (TryGetProperty(root, "response", out var response)
            && response.ValueKind != JsonValueKind.Null)
        {
            if (response.ValueKind != JsonValueKind.Object)
            {
                throw Mismatch(response.ValueKind, "Go value of type struct");
            }

            if (TryGetProperty(response, "cloudaicompanionProject", out var project)
                && project.ValueKind != JsonValueKind.Null)
            {
                if (project.ValueKind != JsonValueKind.Object)
                {
                    throw Mismatch(project.ValueKind, "Go value of type struct");
                }

                if (TryGetProperty(project, "id", out var id)
                    && id.ValueKind != JsonValueKind.Null)
                {
                    if (id.ValueKind != JsonValueKind.String)
                    {
                        throw Mismatch(
                            id.ValueKind,
                            "Go struct field .response.cloudaicompanionProject.id of type string");
                    }

                    projectId = id.GetString() ?? string.Empty;
                }
            }
        }

        return new OnboardResponse(done, projectId);
    }

    private static JsonDocument ParseObject(byte[]? body)
    {
        if (body is null || body.Length == 0)
        {
            throw new InvalidOperationException("unexpected end of JSON input");
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            // Go reports its own decoder text here; the port surfaces the
            // System.Text.Json text (C202).
            throw new InvalidOperationException(ex.Message, ex);
        }
    }

    /// <summary>
    /// encoding/json matches a tag exactly first, then case-insensitively;
    /// JsonElement.TryGetProperty is case-sensitive, hence the second pass.
    /// </summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static InvalidOperationException Mismatch(JsonValueKind kind, string target)
        => new("json: cannot unmarshal " + KindName(kind) + " into " + target);

    private static string KindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        _ => "null",
    };

    /// <summary>The decoded loadCodeAssist response (antigravity.go:297-307).</summary>
    internal sealed record LoadCodeAssistResponse(string ProjectId, List<AllowedTier> AllowedTiers);

    /// <summary>One allowedTiers element (antigravity.go:299-302).</summary>
    internal sealed record AllowedTier(string Id, bool IsDefault);

    /// <summary>The decoded onboardUser response (antigravity.go:383-390).</summary>
    internal sealed record OnboardResponse(bool Done, string ProjectId);
}
