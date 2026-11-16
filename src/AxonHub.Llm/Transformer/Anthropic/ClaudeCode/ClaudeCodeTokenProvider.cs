using AxonHub.Llm.OAuth;

namespace AxonHub.Llm.Transformer.Anthropic.ClaudeCode;

/// <summary>Port of llm/transformer/anthropic/claudecode/token_provider.go.</summary>
public static class ClaudeCodeTokenProvider
{
    /// <summary>Production Claude OAuth endpoints.</summary>
    public static OAuthUrls DefaultTokenUrls()
        => new()
        {
            AuthorizeUrl = ClaudeCodeConstants.AuthorizeUrl,
            TokenUrl = ClaudeCodeConstants.TokenUrl,
        };

    /// <summary>
    /// Port of <c>NewTokenProvider</c>: Claude uses JSON token requests instead
    /// of form-encoded bodies.
    /// </summary>
    public static TokenProvider Create(TokenProviderParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        // Go passes the struct by value, so overriding these fields never
        // reaches the caller's variable.
        return new TokenProvider(new TokenProviderParams
        {
            Credentials = parameters.Credentials,
            HttpClient = parameters.HttpClient,
            OAuthUrls = DefaultTokenUrls(),
            UserAgent = parameters.UserAgent.Length == 0
                ? ClaudeCodeConstants.UserAgent
                : parameters.UserAgent,
            OnRefreshed = parameters.OnRefreshed,
            ExchangeStrategy = new JsonStrategy { UserAgent = ClaudeCodeConstants.UserAgent },
        });
    }
}
