using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AxonHub.Api.Auth;

/// <summary>
/// The HMAC JWT subset used by biz.AuthService (internal/server/biz/auth.go):
/// HS256 tokens with the <c>user_id / iat / auth_time / exp</c> claims. The
/// encoder reproduces golang-jwt v5's header (<c>{"alg":"HS256","typ":"JWT"}</c>)
/// and its alphabetical MapClaims key order; the decoder mirrors jwt.Parse's
/// default validation (signature, exp and nbf; iat is only checked when the
/// parser is built WithIssuedAt, which the reference does not do).
/// </summary>
internal static class JwtTokenCodec
{
    /// <summary>The claims biz.AuthService reads from a parsed token.</summary>
    internal sealed record Claims(double? UserId, long? ExpiresAt, long? AuthTime);

    public static string EncodeHs256(
        string secret,
        long userId,
        long issuedAt,
        long authTime,
        long expiresAt)
    {
        ArgumentNullException.ThrowIfNull(secret);

        var header = Base64UrlEncode("""{"alg":"HS256","typ":"JWT"}"""u8);
        var payload = Base64UrlEncode(EncodePayload(userId, issuedAt, authTime, expiresAt));
        var signingInput = header + "." + payload;
        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.ASCII.GetBytes(signingInput));

        return signingInput + "." + Base64UrlEncode(signature);
    }

    public static Claims Decode(string token, string secret, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(secret);

        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException(
                "token is malformed: token contains an invalid number of segments");
        }

        var algorithm = ReadAlgorithm(parts[0]);
        var expected = Sign(algorithm, Encoding.UTF8.GetBytes(secret),
            Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));

        byte[] actual;
        try
        {
            actual = Base64UrlDecode(parts[2]);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "token is malformed: could not base64 decode signature");
        }

        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new InvalidOperationException("signature is invalid");
        }

        using var payload = ParseSegment(parts[1]);
        var root = payload.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "token is malformed: token claims are not a JSON object");
        }

        var expiresAt = ReadNumericDate(root, "exp");
        var notBefore = ReadNumericDate(root, "nbf");
        var nowSeconds = now.ToUnixTimeSeconds();

        if (expiresAt is long expires && nowSeconds >= expires)
        {
            throw new InvalidOperationException("token is expired");
        }

        if (notBefore is long validFrom && nowSeconds < validFrom)
        {
            throw new InvalidOperationException("token is not valid yet");
        }

        // AuthService.jwtTimeClaim requires a numeric value greater than zero;
        // non-numeric and non-positive auth_time are treated as absent (the
        // session ceiling then does not apply). exp is already validated as a
        // numeric, non-expired claim by the parser above.
        var authTime = ReadNumber(root, "auth_time");

        return new Claims(
            ReadNumber(root, "user_id"),
            expiresAt,
            authTime is double value and > 0 ? (long)value : null);
    }

    private static byte[] EncodePayload(
        long userId,
        long issuedAt,
        long authTime,
        long expiresAt)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            // Go's encoding/json sorts map keys, which is also the claim order
            // visible when a reference token is decoded.
            writer.WriteNumber("auth_time", authTime);
            writer.WriteNumber("exp", expiresAt);
            writer.WriteNumber("iat", issuedAt);
            writer.WriteNumber("user_id", userId);
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static string ReadAlgorithm(string segment)
    {
        using var header = ParseSegment(segment);
        if (header.RootElement.ValueKind != JsonValueKind.Object
            || !header.RootElement.TryGetProperty("alg", out var algorithm)
            || algorithm.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "token is unverifiable: signing method (alg) is unavailable");
        }

        return algorithm.GetString()!;
    }

    private static byte[] Sign(string algorithm, byte[] key, byte[] data) => algorithm switch
    {
        "HS256" => HMACSHA256.HashData(key, data),
        "HS384" => HMACSHA384.HashData(key, data),
        "HS512" => HMACSHA512.HashData(key, data),
        _ => throw new InvalidOperationException(
            "unexpected signing method: " + algorithm),
    };

    private static JsonDocument ParseSegment(string segment)
    {
        try
        {
            return JsonDocument.Parse(Base64UrlDecode(segment));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "token is malformed: could not JSON decode segment", ex);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException(
                "token is malformed: could not base64 decode segment");
        }
    }

    private static long? ReadNumericDate(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidOperationException(
                $"token has invalid claims: invalid type for claim: {name}");
        }

        return (long)value.GetDouble();
    }

    private static double? ReadNumber(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.GetDouble();
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight(
            normalized.Length + ((4 - (normalized.Length % 4)) % 4),
            '=');
        return Convert.FromBase64String(normalized);
    }
}
