using System.Globalization;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Llm.Transformer.OpenAI.Codex;

/// <summary>Port of codex.TokenProviderParams (llm/transformer/openai/codex/token.go:18-22).</summary>
public sealed class CodexTokenProviderParams
{
    public OAuthCredentials? Credentials { get; set; }

    public IExecutor? HttpClient { get; set; }

    public Func<CancellationToken, OAuthCredentials, Exception?>? OnRefreshed { get; set; }
}

/// <summary>
/// Port of codex.AuthJSON: the <c>~/.codex/auth.json</c> shape, whose tokens
/// are nested under <c>tokens</c>.
/// </summary>
public sealed class CodexAuthJson
{
    [JsonPropertyName("last_refresh")]
    public string LastRefresh { get; set; } = string.Empty;

    [JsonPropertyName("tokens")]
    public CodexAuthJsonTokens? Tokens { get; set; }
}

/// <summary>Port of the anonymous <c>AuthJSON.Tokens</c> struct.</summary>
public sealed class CodexAuthJsonTokens
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("id_token")]
    public string IdToken { get; set; } = string.Empty;
}

/// <summary>Port of llm/transformer/openai/codex/token.go.</summary>
public static class CodexToken
{
    /// <summary>Go: <c>DefaultTokenURLs</c> — the production OpenAI OAuth endpoints.</summary>
    public static OAuthUrls DefaultTokenUrls()
        => new()
        {
            AuthorizeUrl = CodexConstants.AuthorizeUrl,
            TokenUrl = CodexConstants.TokenUrl,
        };

    /// <summary>
    /// Port of <c>DecodeAuthJSON</c>: turns the Codex CLI auth file into
    /// channel credentials. The access token is required; the expiry is derived
    /// from <c>last_refresh</c> plus one hour, and falls back to one hour from
    /// now for refreshable credentials without a usable timestamp.
    /// </summary>
    public static OAuthCredentials DecodeAuthJson(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException("empty auth json");
        }

        // The reference returns json.Unmarshal's error unchanged; System.Text.Json
        // raises its own JsonException with different wording (difference C094).
        var authJson = LlmJson.Deserialize<CodexAuthJson>(trimmed) ?? new CodexAuthJson();
        var tokens = authJson.Tokens;
        var accessToken = tokens?.AccessToken ?? string.Empty;

        if (accessToken.Trim().Length == 0)
        {
            throw new InvalidOperationException("access_token is empty");
        }

        var creds = new OAuthCredentials
        {
            ClientId = CodexConstants.ClientId,
            AccessToken = accessToken,
            RefreshToken = tokens!.RefreshToken,
            IdToken = tokens.IdToken,
            TokenType = "bearer",
            Scopes = OAuthCredentialsJson.SplitFields(CodexConstants.Scopes),
        };

        if (authJson.LastRefresh.Length > 0)
        {
            // Go parses strictly as RFC3339Nano and ignores a failure; the port
            // reuses the shared GoTime parse seam (difference C093).
            if (DateTimeOffset.TryParse(
                    authJson.LastRefresh,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var lastRefresh))
            {
                creds.ExpiresAt = lastRefresh.AddHours(1);
            }
        }

        if (creds.RefreshToken.Length > 0 && creds.ExpiresAt == DateTimeOffset.MinValue)
        {
            creds.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
        }

        return creds;
    }

    /// <summary>Port of <c>NewTokenProvider</c>: an OAuth provider pinned to the Codex endpoints.</summary>
    public static TokenProvider CreateTokenProvider(CodexTokenProviderParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return new TokenProvider(new TokenProviderParams
        {
            Credentials = parameters.Credentials,
            HttpClient = parameters.HttpClient,
            OAuthUrls = DefaultTokenUrls(),
            OnRefreshed = parameters.OnRefreshed,
        });
    }
}
