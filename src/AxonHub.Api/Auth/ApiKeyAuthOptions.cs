namespace AxonHub.Api.Auth;

/// <summary>
/// Port of conf.APIServer.API.Auth (server.api.auth in config.yml) and
/// server.trusted_proxies. The reference calls SetTrustedProxies with its
/// configured list, whose default is empty: forwarded client-IP headers are only
/// honoured when the TCP peer belongs to one of these networks.
/// </summary>
public sealed record ApiKeyAuthOptions(
    bool AllowNoAuth,
    IReadOnlyList<string> TrustedProxies)
{
    /// <summary>Reference defaults: allow_no_auth = false, trusted_proxies = [].</summary>
    public static ApiKeyAuthOptions Default { get; } = new(
        AllowNoAuth: false,
        TrustedProxies: Array.Empty<string>());
}
