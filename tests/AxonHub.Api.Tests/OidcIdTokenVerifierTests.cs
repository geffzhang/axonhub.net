using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth.Oidc;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the go-oidc ID-token verification the reference performs with
/// <c>provider.Verifier.Verify</c>: compact JWS parsing, RS256 against the
/// JWKS, and the issuer/audience/expiry checks with go-oidc's error texts.
/// </summary>
public sealed class OidcIdTokenVerifierTests
{
    private const string Issuer = "https://issuer.example.com";
    private const string ClientId = "client-1";
    private const string JwksUri = "https://issuer.example.com/jwks";

    [Fact]
    public async Task AValidRs256TokenIsAccepted()
    {
        using var rsa = RSA.Create(2048);
        var claims = Claims();
        var token = CreateToken(rsa, claims);
        var verifier = CreateVerifier(JwksKey(rsa));

        var result = await verifier.VerifyAsync(new OidcIdTokenRequest(
            token,
            Issuer,
            ClientId,
            JwksUri));

        Assert.Equal("subject-1", result.Subject);
        Assert.Equal("user@example.com", result.Claims["email"]);
    }

    [Fact]
    public async Task AnIssuerMismatchIsRejected()
    {
        using var rsa = RSA.Create(2048);
        var claims = Claims();
        claims["iss"] = "https://other.example.com";
        var verifier = CreateVerifier(JwksKey(rsa));

        var exception = await Assert.ThrowsAsync<OidcProtocolException>(
            () => verifier.VerifyAsync(new OidcIdTokenRequest(
                CreateToken(rsa, claims),
                Issuer,
                ClientId,
                JwksUri)));

        Assert.Equal(
            "oidc: id token issued by a different provider, expected "
            + "\"https://issuer.example.com\" got \"https://other.example.com\"",
            exception.Message);
    }

    [Fact]
    public async Task AnAudienceMismatchIsRejected()
    {
        using var rsa = RSA.Create(2048);
        var claims = Claims();
        claims["aud"] = new[] { "client-2" };
        var verifier = CreateVerifier(JwksKey(rsa));

        var exception = await Assert.ThrowsAsync<OidcProtocolException>(
            () => verifier.VerifyAsync(new OidcIdTokenRequest(
                CreateToken(rsa, claims),
                Issuer,
                ClientId,
                JwksUri)));

        Assert.Equal(
            "oidc: expected audience \"client-1\" got [\"client-2\"]",
            exception.Message);
    }

    [Fact]
    public async Task AnExpiredTokenIsRejected()
    {
        using var rsa = RSA.Create(2048);
        var claims = Claims();
        claims["exp"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();
        var verifier = CreateVerifier(JwksKey(rsa));

        var exception = await Assert.ThrowsAsync<OidcProtocolException>(
            () => verifier.VerifyAsync(new OidcIdTokenRequest(
                CreateToken(rsa, claims),
                Issuer,
                ClientId,
                JwksUri)));

        Assert.StartsWith(
            "oidc: token is expired (Token Expiry: ",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenSignedByAnotherKeyIsRejected()
    {
        using var signing = RSA.Create(2048);
        using var published = RSA.Create(2048);
        var verifier = CreateVerifier(JwksKey(published));

        var exception = await Assert.ThrowsAsync<OidcProtocolException>(
            () => verifier.VerifyAsync(new OidcIdTokenRequest(
                CreateToken(signing, Claims()),
                Issuer,
                ClientId,
                JwksUri)));

        Assert.Equal(
            "failed to verify signature: failed to verify id token signature",
            exception.Message);
    }

    [Fact]
    public async Task ANonRs256AlgorithmIsRejected()
    {
        using var rsa = RSA.Create(2048);
        var verifier = CreateVerifier(JwksKey(rsa));

        var exception = await Assert.ThrowsAsync<OidcProtocolException>(
            () => verifier.VerifyAsync(new OidcIdTokenRequest(
                "header.payload.signature",
                Issuer,
                ClientId,
                JwksUri)));

        // go-jose decodes every segment before the key fetch, so the invalid
        // base64 in the 9-character signature is what fails first.
        Assert.Equal(
            "oidc: malformed jwt: illegal base64 data at input byte 8",
            exception.Message);

        var payload = Base64Url(JsonSerializer.SerializeToUtf8Bytes(Claims()));
        var hs256 = Base64Url(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"))
            + "." + payload + ".c2ln";
        var algorithmError = await Assert.ThrowsAsync<OidcProtocolException>(
            () => verifier.VerifyAsync(new OidcIdTokenRequest(
                hs256,
                Issuer,
                ClientId,
                JwksUri)));

        Assert.Equal(
            "oidc: malformed jwt: unexpected signature algorithm \"HS256\"; "
            + "expected [\"RS256\"]",
            algorithmError.Message);
    }

    [Fact]
    public async Task AMissingJwksUriFailsBeforeAnyFetch()
    {
        using var rsa = RSA.Create(2048);
        var verifier = CreateVerifier(JwksKey(rsa));

        var exception = await Assert.ThrowsAsync<OidcProtocolException>(
            () => verifier.VerifyAsync(new OidcIdTokenRequest(
                CreateToken(rsa, Claims()),
                Issuer,
                ClientId,
                null)));

        // RemoteKeySet.updateKeys: http.NewRequest("GET", "", nil) succeeds and
        // the HTTP client then rejects the missing scheme.
        Assert.Equal(
            "failed to verify signature: fetching keys oidc: get keys failed "
            + "Get \"\": unsupported protocol scheme \"\"",
            exception.Message);
    }

    private static OidcIdTokenVerifier CreateVerifier(params OidcJwksKey[] keys)
        => new(new FakeOidcJwksClient { Keys = keys });

    private static Dictionary<string, object?> Claims() => new()
    {
        ["sub"] = "subject-1",
        ["iss"] = Issuer,
        ["aud"] = new[] { ClientId },
        ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        ["email"] = "user@example.com",
    };

    private static string CreateToken(
        RSA rsa,
        Dictionary<string, object?> claims,
        string algorithm = "RS256",
        string? kid = "k1")
    {
        var header = new Dictionary<string, object>
        {
            ["alg"] = algorithm,
            ["typ"] = "JWT",
        };
        if (kid is not null)
        {
            header["kid"] = kid;
        }

        var headerPart = Base64Url(JsonSerializer.SerializeToUtf8Bytes(header));
        var payloadPart = Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signingInput = Encoding.ASCII.GetBytes(headerPart + "." + payloadPart);
        var signature = rsa.SignData(
            signingInput,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return headerPart + "." + payloadPart + "." + Base64Url(signature);
    }

    private static OidcJwksKey JwksKey(RSA rsa, string kid = "k1")
    {
        var parameters = rsa.ExportParameters(false);
        return new OidcJwksKey(
            kid,
            "RSA",
            Base64Url(parameters.Modulus!),
            Base64Url(parameters.Exponent!));
    }

    private static string Base64Url(byte[] value)
        => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
