using System.Text;
using AxonHub.Llm.Http;

namespace AxonHub.Llm.OAuth;

/// <summary>
/// Port of oauth.ExchangeStrategy (llm/oauth/exchange_strategy.go:16-21):
/// providers disagree on the token-endpoint encoding (standard form-encoded
/// versus Claude Code's JSON body).
/// </summary>
public interface IExchangeStrategy
{
    /// <summary>Builds the HTTP request for exchanging an authorization code.</summary>
    HttpRequestData BuildExchangeRequest(ExchangeParams parameters, string tokenUrl);

    /// <summary>Builds the HTTP request for refreshing a token.</summary>
    HttpRequestData BuildRefreshRequest(OAuthCredentials? credentials, string tokenUrl);
}

/// <summary>Port of oauth.FormEncodedStrategy: standard OAuth2 form bodies.</summary>
public sealed class FormEncodedStrategy : IExchangeStrategy
{
    public string UserAgent { get; set; } = string.Empty;

    public HttpRequestData BuildExchangeRequest(ExchangeParams parameters, string tokenUrl)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var form = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["grant_type"] = ["authorization_code"],
            ["client_id"] = [parameters.ClientId],
            ["code"] = [parameters.Code],
            ["redirect_uri"] = [parameters.RedirectUri],
            ["code_verifier"] = [parameters.CodeVerifier],
        };

        return FormRequest(form, "application/x-www-form-urlencoded", tokenUrl);
    }

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
            ["refresh_token"] = [credentials.RefreshToken],
        };

        return FormRequest(form, "application/x-www-form-urlencoded", tokenUrl);
    }

    private HttpRequestData FormRequest(
        Dictionary<string, List<string>> form,
        string contentType,
        string tokenUrl)
        => new()
        {
            Method = "POST",
            Url = tokenUrl,
            Headers = Headers(contentType),
            Body = Encoding.UTF8.GetBytes(HttpClientUtils.EncodeQuery(form)),
        };

    private Dictionary<string, List<string>> Headers(string contentType)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = [contentType],
            ["Accept"] = ["application/json"],
        };

        if (UserAgent.Length > 0)
        {
            headers["User-Agent"] = [UserAgent];
        }

        return headers;
    }
}

/// <summary>Port of oauth.JSONStrategy: Claude Code's JSON token requests.</summary>
public sealed class JsonStrategy : IExchangeStrategy
{
    public string UserAgent { get; set; } = string.Empty;

    public HttpRequestData BuildExchangeRequest(ExchangeParams parameters, string tokenUrl)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        var body = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = parameters.Code,
            ["client_id"] = parameters.ClientId,
            ["redirect_uri"] = parameters.RedirectUri,
            ["code_verifier"] = parameters.CodeVerifier,
        };

        // Claude requires state in the token exchange.
        if (parameters.State.Length > 0)
        {
            body["state"] = parameters.State;
        }

        return JsonRequest(body, tokenUrl);
    }

    public HttpRequestData BuildRefreshRequest(OAuthCredentials? credentials, string tokenUrl)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (credentials.RefreshToken.Length == 0)
        {
            throw new InvalidOperationException("refresh_token is empty");
        }

        var body = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = credentials.ClientId,
            ["refresh_token"] = credentials.RefreshToken,
        };

        return JsonRequest(body, tokenUrl);
    }

    private HttpRequestData JsonRequest(Dictionary<string, string> body, string tokenUrl)
    {
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
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
            // Go marshals map[string]string with keys in sorted order; the shared
            // options reproduce that through GoSortedMapConverterFactory.
            Body = Json.LlmJson.SerializeToUtf8Bytes(body),
        };
    }
}
