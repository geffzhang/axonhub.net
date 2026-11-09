using AxonHub.Llm.Pipeline;

namespace AxonHub.Llm.Http;

/// <summary>
/// The <see cref="IExecutor"/> counterparts of
/// <c>httpclient.HttpClient.WithRejectHTTPSDowngrade</c> and
/// <c>WithRejectRedirects</c> (llm/httpclient/client.go:110-144) and of the
/// Codex catalog's same-origin redirect hook (codex_catalog.go:150-161).
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

    /// <summary>
    /// codex_catalog.go:150-161: follow a redirect only while it stays on the
    /// initial request's scheme and authority, stopping after ten hops. Both
    /// stops return the 3xx response to the caller instead of an error.
    /// </summary>
    public static IExecutor WithSameOriginRedirects(this IExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(executor);

        return executor is HttpExecutor http ? http.WithSameOriginRedirects() : executor;
    }
}
