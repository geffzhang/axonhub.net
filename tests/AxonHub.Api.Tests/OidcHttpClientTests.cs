using System.Net;
using System.Text;
using AxonHub.Api.Auth.Oidc;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the discovery call shape of go-oidc's oidc.NewProvider: the
/// <c>/.well-known/openid-configuration</c> lookup under the trimmed issuer,
/// the exact issuer echo check and the failure messages.
/// </summary>
public sealed class OidcHttpClientTests
{
    private const string ConfigurationJson = """
        {
          "issuer": "https://idp.example.com",
          "authorization_endpoint": "https://idp.example.com/auth",
          "token_endpoint": "https://idp.example.com/token",
          "userinfo_endpoint": "https://idp.example.com/userinfo",
          "jwks_uri": "https://idp.example.com/jwks",
          "id_token_signing_alg_values_supported": ["RS256", "ES256"]
        }
        """;

    [Fact]
    public async Task DiscoveryFetchesTheWellKnownDocumentUnderTheIssuer()
    {
        string? requested = null;
        using var handler = new StubHttpHandler(request =>
        {
            requested = request.RequestUri?.ToString();
            return JsonResponse(ConfigurationJson);
        });
        using var client = new HttpClient(handler);

        var document = await new OidcHttpClient(client)
            .DiscoverAsync("https://idp.example.com");

        Assert.Equal(
            "https://idp.example.com/.well-known/openid-configuration",
            requested);
        Assert.Equal("https://idp.example.com", document.Issuer);
        Assert.Equal("https://idp.example.com/auth", document.AuthorizationEndpoint);
        Assert.Equal("https://idp.example.com/token", document.TokenEndpoint);
        Assert.Equal("https://idp.example.com/userinfo", document.UserInfoEndpoint);
        Assert.Equal("https://idp.example.com/jwks", document.JwksUri);
        Assert.Equal(["RS256", "ES256"], document.IdTokenSigningAlgorithms);
    }

    [Fact]
    public async Task ATrailingSlashIssuerStillTrimsTheRequestButMustEchoExactly()
    {
        string? requested = null;
        using var handler = new StubHttpHandler(request =>
        {
            requested = request.RequestUri?.ToString();
            return JsonResponse(ConfigurationJson);
        });
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<OidcDiscoveryException>(
            () => new OidcHttpClient(client).DiscoverAsync("https://idp.example.com/"));

        Assert.Equal(
            "https://idp.example.com/.well-known/openid-configuration",
            requested);
        Assert.Equal(
            "oidc: issuer did not match the issuer returned by provider, "
            + "expected \"https://idp.example.com/\" got \"https://idp.example.com\"",
            exception.Message);
    }

    [Fact]
    public async Task ANonSuccessStatusBecomesADiscoveryException()
    {
        using var handler = new StubHttpHandler(_ => new HttpResponseMessage
        {
            StatusCode = HttpStatusCode.InternalServerError,
            ReasonPhrase = "Internal Server Error",
            Content = new StringContent("boom", Encoding.UTF8, "text/plain"),
        });
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<OidcDiscoveryException>(
            () => new OidcHttpClient(client).DiscoverAsync("https://idp.example.com"));

        Assert.Equal(
            "oidc: failed to query provider \"https://idp.example.com\": "
            + "500 Internal Server Error boom",
            exception.Message);
    }

    [Fact]
    public async Task AnIssuerEchoMismatchIsRejected()
    {
        using var handler = new StubHttpHandler(
            _ => JsonResponse(ConfigurationJson.Replace(
                "https://idp.example.com\"",
                "https://evil.example.com\"",
                StringComparison.Ordinal)));
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<OidcDiscoveryException>(
            () => new OidcHttpClient(client).DiscoverAsync("https://idp.example.com"));

        Assert.Equal(
            "oidc: issuer did not match the issuer returned by provider, "
            + "expected \"https://idp.example.com\" got \"https://evil.example.com\"",
            exception.Message);
    }

    [Fact]
    public async Task MalformedJsonBecomesADiscoveryException()
    {
        using var handler = new StubHttpHandler(_ => JsonResponse("{"));
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<OidcDiscoveryException>(
            () => new OidcHttpClient(client).DiscoverAsync("https://idp.example.com"));

        Assert.StartsWith(
            "oidc: failed to decode provider discovery object: ",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATransportFailureKeepsTheUnderlyingMessage()
    {
        using var handler = new StubHttpHandler(
            _ => throw new HttpRequestException("connection refused"));
        using var client = new HttpClient(handler);

        var exception = await Assert.ThrowsAsync<OidcDiscoveryException>(
            () => new OidcHttpClient(client).DiscoverAsync("https://idp.example.com"));

        Assert.Equal("connection refused", exception.Message);
        Assert.IsType<HttpRequestException>(exception.InnerException);
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new()
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class StubHttpHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            return Task.FromResult(respond(request));
        }
    }
}
