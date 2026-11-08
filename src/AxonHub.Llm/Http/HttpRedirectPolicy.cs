namespace AxonHub.Llm.Http;

/// <summary>
/// Redirect policy carried by an <see cref="HttpExecutor"/>, mirroring the
/// <c>CheckRedirect</c> configurations of llm/httpclient/client.go:87-151.
/// </summary>
/// <remarks>
/// Go installs <c>CheckRedirect</c> only for the policy clients, and doing so
/// replaces <c>http.Client</c>'s default 10-hop check (client.go:504-507). The
/// port keeps that split: <see cref="Follow"/> applies the default hop cap,
/// while the two rejecting policies intentionally have none.
/// </remarks>
public enum HttpRedirectPolicy
{
    /// <summary>Follow redirects like Go's default client: refuse after 10 hops.</summary>
    Follow = 0,

    /// <summary>
    /// Follow redirects but refuse an HTTPS to HTTP hop
    /// (<c>rejectHTTPSDowngrade</c>, client.go:146-151). Installed with
    /// <c>WithRejectHTTPSDowngrade</c>; no hop cap.
    /// </summary>
    RejectHttpsDowngrade = 1,

    /// <summary>
    /// Refuse every redirect (<c>WithRejectRedirects</c>, client.go:132-144),
    /// for credential-bearing catalog requests. No hop cap.
    /// </summary>
    RejectAll = 2,
}
