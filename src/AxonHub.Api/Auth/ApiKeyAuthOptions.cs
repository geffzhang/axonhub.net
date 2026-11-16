namespace AxonHub.Api.Auth;

/// <summary>
/// Port of conf.APIServer.API.Auth (server.api.auth in config.yml) and
/// server.trusted_proxies. The reference calls SetTrustedProxies with its
/// configured list, whose default is empty: forwarded client-IP headers are only
/// honoured when the TCP peer belongs to one of these networks.
/// </summary>
public sealed record ApiKeyAuthOptions(
    bool AllowNoAuth,
    IReadOnlyList<string> TrustedProxies,
    string KeyPrefix = "ah")
{
    /// <summary>
    /// Reference defaults: allow_no_auth = false, trusted_proxies = [],
    /// server.api.auth.key_prefix = "ah" (conf/conf.go:257).
    /// </summary>
    public static ApiKeyAuthOptions Default { get; } = new(
        AllowNoAuth: false,
        TrustedProxies: Array.Empty<string>());
}
