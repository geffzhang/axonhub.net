using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Llm.Transformer.Xai.Subscription;

/// <summary>Port of subscription.TokenProviderParams (token.go:15-19).</summary>
public sealed class SubscriptionTokenProviderParams
{
    public OAuthCredentials? Credentials { get; set; }

    public IExecutor? HttpClient { get; set; }

    public Func<CancellationToken, OAuthCredentials, Exception?>? OnRefreshed { get; set; }
}

/// <summary>Port of llm/transformer/xai/subscription/token.go.</summary>
public static class SubscriptionToken
{
    /// <summary>Go: <c>DefaultTokenURLs</c> — the production xAI OAuth endpoints.</summary>
    public static OAuthUrls DefaultTokenUrls()
        => new()
        {
            AuthorizeUrl = SubscriptionConstants.AuthorizeUrl,
            TokenUrl = SubscriptionConstants.TokenUrl,
        };

    /// <summary>
    /// Port of <c>NewTokenProvider</c> (token.go:21-29): an OAuth provider
    /// pinned to the xAI endpoints with the CLI user agent on token requests.
    /// </summary>
    public static TokenProvider NewTokenProvider(SubscriptionTokenProviderParams parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        return new TokenProvider(new TokenProviderParams
        {
            Credentials = parameters.Credentials,
            HttpClient = parameters.HttpClient,
            OAuthUrls = DefaultTokenUrls(),
            UserAgent = SubscriptionConstants.CliUserAgent,
            OnRefreshed = parameters.OnRefreshed,
        });
    }
}
