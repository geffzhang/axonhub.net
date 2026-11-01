using System.Security.Cryptography;
using System.Text;

namespace AxonHub.Llm.OAuth;

/// <summary>
/// Port of llm/oauth/pkce.go: RFC 7636 code verifier, S256 challenge and state
/// generation.
/// </summary>
public static class Pkce
{
    public const int MinCodeVerifierBytes = 32;
    public const int MaxCodeVerifierBytes = 96;
    public const int MinStateBytes = 16;
    public const int MaxStateBytes = 96;

    public static string GenerateCodeVerifier(int byteLength)
    {
        if (byteLength < MinCodeVerifierBytes || byteLength > MaxCodeVerifierBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteLength),
                "code verifier byte length must be between "
                    + MinCodeVerifierBytes + " and " + MaxCodeVerifierBytes);
        }

        return RandomUrlSafeString(byteLength);
    }

    public static string GenerateCodeChallenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(verifier));
        return Base64UrlEncode(hash);
    }

    public static string GenerateState(int byteLength)
    {
        if (byteLength < MinStateBytes || byteLength > MaxStateBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteLength),
                "state byte length must be between " + MinStateBytes + " and " + MaxStateBytes);
        }

        return RandomUrlSafeString(byteLength);
    }

    private static string RandomUrlSafeString(int byteLength)
        => Base64UrlEncode(RandomNumberGenerator.GetBytes(byteLength));

    /// <summary>Go's <c>base64.RawURLEncoding</c>: URL alphabet, no padding.</summary>
    private static string Base64UrlEncode(byte[] value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
