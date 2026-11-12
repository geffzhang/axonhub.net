using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Api.Auth;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Api.Management;

/// <summary>Port of api.StartCopilotOAuthResponse (copilot.go:145-151).</summary>
public sealed record CopilotStartResult(
    string SessionId,
    string UserCode,
    string VerificationUri,
    long ExpiresIn,
    long Interval);

/// <summary>
/// Port of api.PollCopilotOAuthResponse (copilot.go:260-266). Empty fields
/// correspond to Go's <c>omitempty</c> and are omitted by the handler.
/// </summary>
public sealed record CopilotPollResult(
    string Token,
    string TokenType,
    string Scope,
    string Status,
    string Message);

/// <summary>
/// A JSONError-shaped failure of the Copilot OAuth handler, carrying the status
/// the reference passes to <c>JSONError</c>.
/// </summary>
public sealed class CopilotOAuthException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Port of api.CopilotHandlers' business half (internal/server/api/copilot.go):
/// the GitHub device-code flow behind POST /admin/copilot/oauth/start and the
/// token polling of POST /admin/copilot/oauth/poll.
/// </summary>
/// <remarks>
/// <paramref name="clientIdProvider"/> is a port-only seam for the
/// <c>GITHUB_COPILOT_CLIENT_ID</c> lookup of copilot.go:49-54; production
/// leaves it null and reads the environment on every request.
/// </remarks>
public sealed class CopilotOAuthService(
    ICopilotDeviceFlowCache deviceCodeCache,
    IExecutor httpClient,
    IProxyExecutorFactory proxyExecutors,
    TimeProvider timeProvider,
    Func<string>? clientIdProvider = null)
{
    /// <summary>copilot.go:26.</summary>
    public const string GithubDeviceCodeUrl = "https://github.com/login/device/code";

    /// <summary>copilot.go:27.</summary>
    public const string GithubAccessTokenUrl = "https://github.com/login/oauth/access_token";

    /// <summary>copilot.go:31: the VS Code public client ID.</summary>
    public const string DefaultGithubCopilotClientId = "Iv1.b507a08c87ecfe98";

    /// <summary>copilot.go:34.</summary>
    public const string GithubCopilotScope = "read:user";

    /// <summary>copilot.go:37.</summary>
    public const string DeviceGrantType = "urn:ietf:params:oauth:grant-type:device_code";

    /// <summary>copilot.go:40: the cache key prefix.</summary>
    public const string CacheKeyPrefix = "copilot:oauth";

    /// <summary>copilot.go:43.</summary>
    public static readonly TimeSpan DeviceFlowCacheExpiration = TimeSpan.FromMinutes(15);

    /// <summary>copilot.go:49-54.</summary>
    public static string GetGithubCopilotClientId()
    {
        var clientId = Environment.GetEnvironmentVariable("GITHUB_COPILOT_CLIENT_ID");
        return string.IsNullOrEmpty(clientId) ? DefaultGithubCopilotClientId : clientId;
    }

    /// <summary>copilot.go:134-137.</summary>
    public static string CacheKey(string sessionId) => CacheKeyPrefix + ":" + sessionId;

    /// <summary>Port of CopilotHandlers.StartOAuth (copilot.go:153-215).</summary>
    public async Task<CopilotStartResult> StartAsync(
        ProxyConfig? proxy,
        CancellationToken cancellationToken = default)
    {
        string sessionId;
        try
        {
            // generateCopilotSessionID (copilot.go:124-132): 32 crypto/rand
            // bytes, base64url without padding.
            sessionId = Pkce.GenerateState(32);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CopilotOAuthException(
                500,
                "failed to generate session ID: " + ex.Message);
        }

        var executor = httpClient;
        if (proxy is not null && proxy.Type == ProxyType.Url && !string.IsNullOrEmpty(proxy.Url))
        {
            executor = proxyExecutors.Create(proxy);
        }

        DeviceCodeResponse device;
        try
        {
            device = RequestDeviceCode(executor, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CopilotOAuthException(
                502,
                "failed to request device code: " + ex.Message);
        }

        var state = new CopilotDeviceFlowState(
            device.DeviceCode ?? string.Empty,
            device.UserCode ?? string.Empty,
            device.VerificationUri ?? string.Empty,
            device.ExpiresIn,
            device.Interval,
            timeProvider.GetUtcNow().ToUnixTimeSeconds());

        // copilot.go:198-201: the cache lifetime is expires_in capped at 15
        // minutes; zero and negative values keep go-cache's special meanings
        // (see CopilotDeviceFlowCache).
        var expiration = TimeSpan.FromSeconds(device.ExpiresIn);
        if (expiration > DeviceFlowCacheExpiration)
        {
            expiration = DeviceFlowCacheExpiration;
        }

        try
        {
            await deviceCodeCache
                .SetAsync(CacheKey(sessionId), state, expiration, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CopilotOAuthException(
                500,
                "failed to save device flow state: " + ex.Message);
        }

        return new CopilotStartResult(
            sessionId,
            device.UserCode ?? string.Empty,
            device.VerificationUri ?? string.Empty,
            device.ExpiresIn,
            device.Interval);
    }

    /// <summary>Port of CopilotHandlers.PollOAuth (copilot.go:268-356).</summary>
    public async Task<CopilotPollResult> PollAsync(
        string sessionId,
        ProxyConfig? proxy,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKey(sessionId);

        CopilotDeviceFlowState? state;
        try
        {
            state = await deviceCodeCache.GetAsync(cacheKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // xcache.Get errors are indistinguishable from a miss
            // (copilot.go:281-285).
            _ = ex;
            state = null;
        }

        if (state is null)
        {
            throw new CopilotOAuthException(400, "invalid or expired session");
        }

        // copilot.go:288 uses a strict comparison: the deadline itself is
        // still pollable.
        if (timeProvider.GetUtcNow().ToUnixTimeSeconds() > state.CreatedAt + state.ExpiresIn)
        {
            await DeleteQuietlyAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            throw new CopilotOAuthException(400, "device code expired");
        }

        var executor = httpClient;
        if (proxy is not null && proxy.Type == ProxyType.Url && !string.IsNullOrEmpty(proxy.Url))
        {
            executor = proxyExecutors.Create(proxy);
        }

        AccessTokenResponse token;
        try
        {
            token = PollAccessToken(executor, state.DeviceCode, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new CopilotOAuthException(
                502,
                "token poll failed: " + ex.Message);
        }

        if (!string.IsNullOrEmpty(token.Error))
        {
            switch (token.Error)
            {
                case "authorization_pending":
                    return new CopilotPollResult(
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        "pending",
                        "Authorization pending. User has not yet authorized the device.");
                case "slow_down":
                    return new CopilotPollResult(
                        string.Empty,
                        string.Empty,
                        string.Empty,
                        "slow_down",
                        "Polling too fast. Please slow down.");
                case "expired_token":
                    await DeleteQuietlyAsync(cacheKey, cancellationToken).ConfigureAwait(false);
                    throw new CopilotOAuthException(400, "device code expired");
                case "access_denied":
                    await DeleteQuietlyAsync(cacheKey, cancellationToken).ConfigureAwait(false);
                    throw new CopilotOAuthException(400, "access denied by user");
                default:
                    throw new CopilotOAuthException(
                        502,
                        "OAuth error: " + token.Error + " - " + token.ErrorDescription);
            }
        }

        if (!string.IsNullOrEmpty(token.Token))
        {
            // copilot.go:340-342: a deletion failure is logged, never surfaced.
            await DeleteQuietlyAsync(cacheKey, cancellationToken).ConfigureAwait(false);
            return new CopilotPollResult(
                token.Token,
                token.TokenType,
                token.Scope,
                "complete",
                "Authorization complete. Access token received.");
        }

        throw new CopilotOAuthException(500, "unexpected response from GitHub");
    }

    /// <summary>Port of requestDeviceCode (copilot.go:218-251).</summary>
    private DeviceCodeResponse RequestDeviceCode(
        IExecutor executor,
        CancellationToken cancellationToken)
    {
        var request = BuildFormRequest(
            GithubDeviceCodeUrl,
            new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["client_id"] = [GithubCopilotClientId()],
                ["scope"] = [GithubCopilotScope],
            });

        HttpResponseData response;
        try
        {
            response = executor.Do(cancellationToken, request);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "device code request failed: " + ex.Message,
                ex);
        }

        if (response.StatusCode < 200 || response.StatusCode >= 300)
        {
            throw new InvalidOperationException(
                "device code request failed with status " + response.StatusCode
                + ": " + Encoding.UTF8.GetString(response.Body ?? []));
        }

        DeviceCodeResponse? device;
        try
        {
            // Go: unmarshaling JSON null into a struct is a no-op.
            device = LlmJson.Deserialize<DeviceCodeResponse>(response.Body ?? []);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to parse device code response: " + ex.Message,
                ex);
        }

        device ??= new DeviceCodeResponse();

        if (string.IsNullOrEmpty(device.DeviceCode))
        {
            throw new InvalidOperationException("device code not received from GitHub");
        }

        return device;
    }

    /// <summary>Port of pollAccessToken (copilot.go:359-407).</summary>
    private AccessTokenResponse PollAccessToken(
        IExecutor executor,
        string deviceCode,
        CancellationToken cancellationToken)
    {
        var request = BuildFormRequest(
            GithubAccessTokenUrl,
            new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["client_id"] = [GithubCopilotClientId()],
                ["device_code"] = [deviceCode],
                ["grant_type"] = [DeviceGrantType],
            });

        HttpResponseData response;
        try
        {
            response = executor.Do(cancellationToken, request);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "access token request failed: " + ex.Message,
                ex);
        }

        if (response.StatusCode < 200 || response.StatusCode >= 300)
        {
            throw new InvalidOperationException(
                "access token request failed with status " + response.StatusCode);
        }

        // GitHub returns form-encoded or JSON depending on the Accept header.
        var contentType = HttpClientUtils.HeaderGet(response.Headers, "Content-Type");
        if (contentType.Contains("application/json", StringComparison.Ordinal))
        {
            try
            {
                return LlmJson.Deserialize<AccessTokenResponse>(response.Body ?? [])
                    ?? new AccessTokenResponse();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "failed to parse access token JSON response: " + ex.Message,
                    ex);
            }
        }

        var values = HttpClientUtils.ParseQuery(
            Encoding.UTF8.GetString(response.Body ?? []),
            out var error);
        if (error is not null)
        {
            throw new InvalidOperationException(
                "failed to parse access token form response: " + error);
        }

        return new AccessTokenResponse
        {
            Token = FirstValue(values, "access_token"),
            TokenType = FirstValue(values, "token_type"),
            Scope = FirstValue(values, "scope"),
            Error = FirstValue(values, "error"),
            ErrorDescription = FirstValue(values, "error_description"),
        };
    }

    private string GithubCopilotClientId()
        => clientIdProvider?.Invoke() ?? GetGithubCopilotClientId();

    private async Task DeleteQuietlyAsync(string cacheKey, CancellationToken cancellationToken)
    {
        try
        {
            await deviceCodeCache.DeleteAsync(cacheKey, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Warn only.
        }
    }

    private static HttpRequestData BuildFormRequest(
        string url,
        Dictionary<string, List<string>> form)
        => new()
        {
            Method = "POST",
            Url = url,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Accept"] = ["application/json"],
            },
            ContentType = "application/x-www-form-urlencoded",
            Body = Encoding.UTF8.GetBytes(HttpClientUtils.EncodeQuery(form)),
        };

    /// <summary>url.Values.Get: the first value, or an empty string.</summary>
    private static string FirstValue(Dictionary<string, List<string>> values, string key)
        => values.TryGetValue(key, out var list) && list.Count > 0
            ? list[0]
            : string.Empty;

    /// <summary>Port of api.deviceCodeResponse (copilot.go:95-101).</summary>
    internal sealed class DeviceCodeResponse
    {
        [JsonPropertyName("device_code")]
        public string? DeviceCode { get; set; }

        [JsonPropertyName("user_code")]
        public string? UserCode { get; set; }

        [JsonPropertyName("verification_uri")]
        public string? VerificationUri { get; set; }

        /// <summary>Go's <c>int</c> is 64-bit on the supported platforms.</summary>
        [JsonPropertyName("expires_in")]
        public long ExpiresIn { get; set; }

        [JsonPropertyName("interval")]
        public long Interval { get; set; }
    }

    /// <summary>Port of api.accessTokenResponse (copilot.go:104-110).</summary>
    internal sealed class AccessTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string Token { get; set; } = string.Empty;

        [JsonPropertyName("token_type")]
        public string TokenType { get; set; } = string.Empty;

        [JsonPropertyName("scope")]
        public string Scope { get; set; } = string.Empty;

        [JsonPropertyName("error")]
        public string Error { get; set; } = string.Empty;

        [JsonPropertyName("error_description")]
        public string ErrorDescription { get; set; } = string.Empty;
    }
}
