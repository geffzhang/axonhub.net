using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Api.Auth.Oidc;

/// <summary>Thrown when the OIDC discovery document cannot be fetched or validated.</summary>
public sealed class OidcDiscoveryException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Port of the subset of coreos/go-oidc's <c>oidc.Provider</c> the port needs.</summary>
public sealed record OidcDiscoveryDocument(
    string Issuer,
    string? AuthorizationEndpoint,
    string? TokenEndpoint,
    string? UserInfoEndpoint,
    string? JwksUri,
    IReadOnlyList<string> IdTokenSigningAlgorithms);

/// <summary>
/// The HTTP seam for the OIDC client. The reference calls
/// <c>oidc.NewProvider</c> (discovery) through the default Go HTTP client;
/// the port keeps those calls behind this interface so the tests can drive a
/// scripted identity provider.
/// </summary>
public interface IOidcHttpClient
{
    /// <summary>Port of <c>oidc.NewProvider</c> (GET /.well-known/openid-configuration).</summary>
    Task<OidcDiscoveryDocument> DiscoverAsync(
        string issuerUrl,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="IOidcHttpClient"/>: a faithful request shape for the
/// go-oidc discovery call (trimmed issuer + <c>/.well-known/openid-configuration</c>,
/// exact issuer echo validation).
/// </summary>
public sealed class OidcHttpClient(HttpClient client) : IOidcHttpClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    /// <inheritdoc />
    public async Task<OidcDiscoveryDocument> DiscoverAsync(
        string issuerUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(issuerUrl);

        // oidc.NewProvider: strings.TrimSuffix(issuer, "/") + "/.well-known/openid-configuration".
        var wellKnown = issuerUrl.TrimEnd('/') + "/.well-known/openid-configuration";

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(wellKnown, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // go-oidc's doRequest surfaces the transport error as-is.
            throw new OidcDiscoveryException(ex.Message, ex);
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false);
                var status = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
                    + " " + response.ReasonPhrase;
                throw new OidcDiscoveryException(
                    $"oidc: failed to query provider \"{issuerUrl}\": {status} {body}");
            }

            DiscoveryDto? document;
            try
            {
                await using var stream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                document = await JsonSerializer.DeserializeAsync<DiscoveryDto>(
                        stream,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new OidcDiscoveryException(
                    $"oidc: failed to decode provider discovery object: {ex.Message}",
                    ex);
            }

            document ??= new DiscoveryDto(null, null, null, null, null, null);

            // oidc.NewProvider rejects a document whose issuer does not echo the
            // configured one exactly.
            if (!string.Equals(document.Issuer, issuerUrl, StringComparison.Ordinal))
            {
                throw new OidcDiscoveryException(
                    "oidc: issuer did not match the issuer returned by provider, "
                    + $"expected \"{issuerUrl}\" got \"{document.Issuer}\"");
            }

            return new OidcDiscoveryDocument(
                document.Issuer ?? string.Empty,
                document.AuthorizationEndpoint,
                document.TokenEndpoint,
                document.UserInfoEndpoint,
                document.JwksUri,
                document.IdTokenSigningAlgValuesSupported ?? []);
        }
    }

    private sealed record DiscoveryDto(
        [property: JsonPropertyName("issuer")] string? Issuer,
        [property: JsonPropertyName("authorization_endpoint")] string? AuthorizationEndpoint,
        [property: JsonPropertyName("token_endpoint")] string? TokenEndpoint,
        [property: JsonPropertyName("userinfo_endpoint")] string? UserInfoEndpoint,
        [property: JsonPropertyName("jwks_uri")] string? JwksUri,
        [property: JsonPropertyName("id_token_signing_alg_values_supported")]
        IReadOnlyList<string>? IdTokenSigningAlgValuesSupported);
}
