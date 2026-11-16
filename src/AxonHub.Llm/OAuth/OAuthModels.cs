using System.Text.Json.Serialization;

namespace AxonHub.Llm.OAuth;

/// <summary>
/// Port of oauth.OAuthCredentials (llm/oauth/credentials.go:10-18): the
/// credentials a channel persists for an OAuth-backed provider.
/// </summary>
/// <remarks>
/// Go's <c>time.Time</c> zero value is modelled by <see
/// cref="DateTimeOffset.MinValue"/>; both mean "unknown expiry" to
/// <see cref="IsExpired"/>. The JSON wire shape is written by
/// <see cref="OAuthCredentialsJson.ToJson"/>, which reproduces Go's field order
/// and <c>omitempty</c> rules.
/// </remarks>
public sealed partial class OAuthCredentials
{
    /// <summary>Go: <c>json:"client_id,omitempty"</c>.</summary>
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Go: <c>json:"access_token"</c>.</summary>
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Go: <c>json:"refresh_token"</c>.</summary>
    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>Go: <c>json:"id_token,omitempty"</c>.</summary>
    [JsonPropertyName("id_token")]
    public string IdToken { get; set; } = string.Empty;

    /// <summary>Go: <c>json:"expires_at"</c>; <see cref="DateTimeOffset.MinValue"/> is Go's zero time.</summary>
    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Go: <c>json:"token_type,omitempty"</c>.</summary>
    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;

    /// <summary>Go: <c>json:"scopes,omitempty"</c>; null and empty both omit.</summary>
    [JsonPropertyName("scopes")]
    public List<string>? Scopes { get; set; }

    /// <summary>Port of <c>(*OAuthCredentials).IsExpired</c>: expired 3 minutes early.</summary>
    public bool IsExpired(DateTimeOffset now)
    {
        if (ExpiresAt == DateTimeOffset.MinValue)
        {
            return true;
        }

        return now.AddMinutes(3) > ExpiresAt;
    }

    /// <summary>Shallow copy (Go's <c>c := *creds</c>); the scope slice is shared.</summary>
    public OAuthCredentials Clone() => (OAuthCredentials)MemberwiseClone();
}

/// <summary>Port of oauth.TokenResponse (llm/oauth/credentials.go:20-27).</summary>
public sealed class TokenResponse
{
    [JsonPropertyName("id_token")]
    public string IdToken { get; set; } = string.Empty;

    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = string.Empty;

    [JsonPropertyName("scope")]
    public string Scope { get; set; } = string.Empty;

    /// <summary>Port of <c>(*TokenResponse).ExpiresAt</c>: zero when <c>expires_in</c> is absent.</summary>
    public DateTimeOffset ExpiresAt()
        => ExpiresIn > 0
            ? DateTimeOffset.UtcNow.AddSeconds(ExpiresIn)
            : DateTimeOffset.MinValue;
}

/// <summary>Port of oauth.TokenError (llm/oauth/credentials.go:38-41).</summary>
public sealed class TokenError
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("error_description")]
    public string ErrorDescription { get; set; } = string.Empty;
}

/// <summary>Port of oauth.OAuthUrls (llm/oauth/token_provider.go:30-33).</summary>
public sealed class OAuthUrls
{
    public string AuthorizeUrl { get; set; } = string.Empty;

    public string TokenUrl { get; set; } = string.Empty;
}

/// <summary>Port of oauth.ExchangeParams (llm/oauth/token_provider.go:66-72).</summary>
public sealed class ExchangeParams
{
    public string Code { get; set; } = string.Empty;

    public string CodeVerifier { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>Optional: for providers that require state in token exchange.</summary>
    public string State { get; set; } = string.Empty;
}

/// <summary>Port of oauth.AutoRefreshOptions (llm/oauth/token_provider.go:74-77).</summary>
public sealed class AutoRefreshOptions
{
    public TimeSpan Interval { get; set; }

    public TimeSpan RefreshBefore { get; set; }
}

/// <summary>Port of oauth.DeviceFlowState (llm/oauth/device_flow_provider.go:22-29).</summary>
public sealed class DeviceFlowState
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = string.Empty;

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = string.Empty;

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }
}

/// <summary>Port of oauth.DeviceFlowResponse (llm/oauth/device_flow_provider.go:33-40).</summary>
public sealed class DeviceFlowResponse
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = string.Empty;

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = string.Empty;

    [JsonPropertyName("verification_uri")]
    public string VerificationUri { get; set; } = string.Empty;

    [JsonPropertyName("verification_uri_complete")]
    public string CompleteUri { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; }
}

/// <summary>Port of oauth.DeviceFlowConfig (llm/oauth/device_flow_provider.go:43-54).</summary>
public sealed class DeviceFlowConfig
{
    /// <summary>URL for the device authorization endpoint.</summary>
    public string DeviceAuthUrl { get; set; } = string.Empty;

    /// <summary>URL for the token endpoint.</summary>
    public string TokenUrl { get; set; } = string.Empty;

    public string ClientId { get; set; } = string.Empty;

    public List<string> Scopes { get; set; } = [];

    public string UserAgent { get; set; } = string.Empty;
}

/// <summary>
/// Port of oauth.TokenExchanger (llm/oauth/device_flow_provider.go:59-67): the
/// optional second step of a two-legged flow (for example Copilot).
/// </summary>
public interface ITokenExchanger
{
    /// <summary>Exchanges an OAuth access token for a provider-specific token.</summary>
    TokenExchangeResult Exchange(CancellationToken cancellationToken, string accessToken);

    /// <summary>Same, using the caller's pre-configured HTTP client (proxy settings).</summary>
    TokenExchangeResult ExchangeWithClient(
        CancellationToken cancellationToken,
        AxonHub.Llm.Pipeline.IExecutor? httpClient,
        string accessToken);
}

/// <summary>
/// Go's <c>(token string, expiresAt int64, err error)</c> triple.
/// </summary>
public readonly record struct TokenExchangeResult(string Token, long ExpiresAt, Exception? Error)
{
    public static TokenExchangeResult Success(string token, long expiresAt)
        => new(token, expiresAt, null);

    public static TokenExchangeResult Failure(Exception error)
        => new(string.Empty, 0, error);
}
