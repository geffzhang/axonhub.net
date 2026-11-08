using AxonHub.Llm.Pipeline;

namespace AxonHub.Llm.Http;

/// <summary>
/// The <see cref="IExecutor"/> counterparts of
/// <c>httpclient.HttpClient.WithRejectHTTPSDowngrade</c> and
/// <c>WithRejectRedirects</c> (llm/httpclient/client.go:110-144).
/// </summary>
/// <remarks>
/// The reference methods live on the concrete client; the port's transports are
/// behind <see cref="IExecutor"/>, so the policy is applied to whatever executor
/// the caller holds. Scripted test executors have no transport to police and are
/// returned unchanged.
/// </remarks>
public static class HttpRedirectPolicies
{
    /// <summary>client.go:110-130: refuse HTTPS to HTTP redirects.</summary>
    public static IExecutor WithRejectHttpsDowngrade(this IExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);

        return executor is HttpExecutor http ? http.WithRejectHttpsDowngrade() : executor;
    }

    /// <summary>client.go:132-144: refuse every redirect.</summary>
    public static IExecutor WithRejectRedirects(this IExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);

        return executor is HttpExecutor http ? http.WithRejectRedirects() : executor;
    }
}
