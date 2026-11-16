using AxonHub.Llm.Http;
using AxonHub.Llm.OAuth;

namespace AxonHub.Llm.Transformer.Antigravity;

/// <summary>Port of antigravity token provider wiring (llm/transformer/antigravity/token_provider.go).</summary>
public static class AntigravityTokenProviderFactory
{
    /// <summary>Port of DefaultTokenURLs: the Antigravity OAuth endpoints.</summary>
    public static readonly OAuthUrls DefaultTokenURLs = new()
    {
        AuthorizeUrl = AntigravityConstants.AuthorizeURL,
        TokenUrl = AntigravityConstants.TokenURL,
    };

    /// <summary>
    /// Port of NewTokenProvider: fills in the Antigravity OAuth endpoints,
    /// default user agent, and the client-secret exchange strategy, then
    /// constructs the shared OAuth token provider.
    /// </summary>
    public static TokenProvider NewTokenProvider(TokenProviderParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        parameters.OAuthUrls = DefaultTokenURLs;
        if (parameters.UserAgent.Length == 0)
        {
            parameters.UserAgent = AntigravityVersion.GetUserAgent();
        }

        parameters.ExchangeStrategy = new AntigravityExchangeStrategy
        {
            UserAgent = parameters.UserAgent,
            ClientSecret = AntigravityConstants.ClientSecret,
        };

        return new TokenProvider(parameters);
    }
}

/// <summary>
/// Port of antigravity.AntigravityExchangeStrategy: Google OAuth token requests
/// carry the installed-app client_secret.
/// </summary>
public sealed class AntigravityExchangeStrategy : IExchangeStrategy
{
    public string UserAgent { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Port of BuildExchangeRequest: authorization_code grant with client_secret.</summary>
    public HttpRequestData BuildExchangeRequest(ExchangeParams parameters, string tokenUrl)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var form = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["grant_type"] = ["authorization_code"],
            ["client_id"] = [parameters.ClientId],
            ["client_secret"] = [ClientSecret],
            ["code"] = [parameters.Code],
            ["redirect_uri"] = [parameters.RedirectUri],
            ["code_verifier"] = [parameters.CodeVerifier],
        };

        return FormRequest(form, tokenUrl);
    }

    /// <summary>Port of BuildRefreshRequest: refresh_token grant with client_secret.</summary>
    public HttpRequestData BuildRefreshRequest(OAuthCredentials? credentials, string tokenUrl)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (credentials.RefreshToken.Length == 0)
        {
            throw new InvalidOperationException("refresh_token is empty");
        }

        var form = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["grant_type"] = ["refresh_token"],
            ["client_id"] = [credentials.ClientId],
            ["client_secret"] = [ClientSecret],
            ["refresh_token"] = [credentials.RefreshToken],
        };

        return FormRequest(form, tokenUrl);
    }

    private HttpRequestData FormRequest(
        Dictionary<string, List<string>> form,
        string tokenUrl)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/x-www-form-urlencoded"],
            ["Accept"] = ["application/json"],
        };

        if (UserAgent.Length > 0)
        {
            headers["User-Agent"] = [UserAgent];
        }

        return new HttpRequestData
        {
            Method = "POST",
            Url = tokenUrl,
            Headers = headers,
            Body = System.Text.Encoding.UTF8.GetBytes(HttpClientUtils.EncodeQuery(form)),
        };
    }
}
