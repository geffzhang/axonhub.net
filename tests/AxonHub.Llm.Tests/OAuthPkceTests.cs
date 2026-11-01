using System.Security.Cryptography;
using System.Text;
using AxonHub.Llm.OAuth;

namespace AxonHub.Llm.Tests;

/// <summary>Port of llm/oauth/pkce_test.go.</summary>
public sealed class OAuthPkceTests
{
    [Fact]
    public void CodeVerifierReturnsUrlSafeValueWithRequestedEntropy()
    {
        const int byteLength = 32;

        var verifier = Pkce.GenerateCodeVerifier(byteLength);

        var decoded = OAuthTestSupport.FromBase64Url(verifier);
        Assert.Equal(byteLength, decoded.Length);
    }

    [Fact]
    public void CodeChallengeReturnsS256Base64UrlValue()
    {
        const string verifier = "synthetic-pkce-verifier";

        var challenge = Pkce.GenerateCodeChallenge(verifier);

        // Independently recomputed RFC 7636 S256 value.
        Assert.Equal("S__5IsvcRYqgGCx9bQESEPzR5KU4JndGW8-Ptad_LRA", challenge);

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        var expected = Convert.ToBase64String(expectedHash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, challenge);
    }

    [Fact]
    public void StateReturnsUrlSafeValueWithRequestedEntropy()
    {
        const int byteLength = 32;

        var state = Pkce.GenerateState(byteLength);

        var decoded = OAuthTestSupport.FromBase64Url(state);
        Assert.Equal(byteLength, decoded.Length);
    }

    [Fact]
    public void CodeVerifierEnforcesRfc7636ByteLengthBounds()
    {
        foreach (var byteLength in new[] { 0, 8, 31, 97, 1024 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Pkce.GenerateCodeVerifier(byteLength));
        }

        foreach (var byteLength in new[]
                 {
                     Pkce.MinCodeVerifierBytes, 64, Pkce.MaxCodeVerifierBytes,
                 })
        {
            var verifier = Pkce.GenerateCodeVerifier(byteLength);
            var decoded = OAuthTestSupport.FromBase64Url(verifier);
            Assert.Equal(byteLength, decoded.Length);
        }
    }

    [Fact]
    public void StateEnforcesByteLengthBounds()
    {
        foreach (var byteLength in new[] { 0, 8, 15, 97 })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => Pkce.GenerateState(byteLength));
        }

        foreach (var byteLength in new[] { Pkce.MinStateBytes, 32, Pkce.MaxStateBytes })
        {
            var state = Pkce.GenerateState(byteLength);
            var decoded = OAuthTestSupport.FromBase64Url(state);
            Assert.Equal(byteLength, decoded.Length);
        }
    }
}
