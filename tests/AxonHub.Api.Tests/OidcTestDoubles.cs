using AxonHub.Api.Auth.Oidc;

namespace AxonHub.Api.Tests;

/// <summary>
/// Scripted identity-provider discovery endpoint for the OIDC tests, so the
/// registry's startup checks never reach the network.
/// </summary>
internal sealed class FakeOidcHttpClient : IOidcHttpClient
{
    public List<string> Requests { get; } = [];

    public Dictionary<string, OidcDiscoveryDocument> Documents { get; } =
        new(StringComparer.Ordinal);

    public Exception? Failure { get; set; }

    public Task<OidcDiscoveryDocument> DiscoverAsync(
        string issuerUrl,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Requests.Add(issuerUrl);

        if (Failure is not null)
        {
            return Task.FromException<OidcDiscoveryDocument>(Failure);
        }

        return Documents.TryGetValue(issuerUrl, out var document)
            ? Task.FromResult(document)
            : Task.FromException<OidcDiscoveryDocument>(
                new OidcDiscoveryException($"oidc: failed to query provider \"{issuerUrl}\""));
    }
}
