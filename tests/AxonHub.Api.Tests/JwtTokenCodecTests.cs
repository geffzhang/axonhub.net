using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the golang-jwt v5 wire shape used by biz.AuthService: the HS256
/// header, the alphabetical MapClaims payload order, raw base64url segments,
/// and jwt.Parse's default validation (signature, exp, nbf; iat is not
/// verified). Any HMAC algorithm is accepted, exactly like the reference's
/// keyfunc, which only asserts that the method is HMAC.
/// </summary>
public sealed class JwtTokenCodecTests
{
    private const string Secret = "test-secret";
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void EncodeWritesTheReferenceHeaderAndAlphabeticalClaims()
    {
        var token = JwtTokenCodec.EncodeHs256(
            Secret,
            userId: 9,
            issuedAt: 100,
            authTime: 90,
            expiresAt: 2_592_100);

        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal("""{"alg":"HS256","typ":"JWT"}""", Encoding.UTF8.GetString(Base64UrlDecode(parts[0])));
        Assert.Equal(
            """{"auth_time":90,"exp":2592100,"iat":100,"user_id":9}""",
            Encoding.UTF8.GetString(Base64UrlDecode(parts[1])));

        // The signature is HMAC-SHA256 over the two base64url segments.
        var expected = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(Secret),
            Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
        Assert.Equal(expected, Base64UrlDecode(parts[2]));
    }

    [Fact]
    public void DecodeReturnsTheClaimsAndValidatesTheSignature()
    {
        var token = JwtTokenCodec.EncodeHs256(Secret, 9, 100, 90, Now.ToUnixTimeSeconds() + 60);

        var claims = JwtTokenCodec.Decode(token, Secret, Now);

        Assert.Equal((double?)9d, claims.UserId);
        Assert.Equal((long?)(Now.ToUnixTimeSeconds() + 60), claims.ExpiresAt);
        Assert.Equal((long?)90L, claims.AuthTime);
    }

    [Fact]
    public void DecodeAcceptsEveryHmacAlgorithmLikeTheReferenceKeyfunc()
    {
        var token = CustomToken(
            Secret,
            """{"exp":9999999999,"user_id":9}""",
            "HS384");

        var claims = JwtTokenCodec.Decode(token, Secret, Now);

        Assert.Equal((double?)9d, claims.UserId);
    }

    [Fact]
    public void DecodeRejectsATamperedSignature()
    {
        var token = JwtTokenCodec.EncodeHs256(Secret, 9, 100, 90, Now.ToUnixTimeSeconds() + 60);
        var tampered = token[..^1] + (token[^1] == 'A' ? 'B' : 'A');

        var error = Assert.Throws<InvalidOperationException>(
            () => JwtTokenCodec.Decode(tampered, Secret, Now));

        Assert.Equal("signature is invalid", error.Message);
    }

    [Fact]
    public void DecodeRejectsAnExpiredToken()
    {
        var token = JwtTokenCodec.EncodeHs256(
            Secret,
            9,
            Now.ToUnixTimeSeconds() - 120,
            Now.ToUnixTimeSeconds() - 120,
            Now.ToUnixTimeSeconds() - 60);

        var error = Assert.Throws<InvalidOperationException>(
            () => JwtTokenCodec.Decode(token, Secret, Now));

        Assert.Equal("token is expired", error.Message);
    }

    [Fact]
    public void DecodeRejectsANotBeforeClaimInTheFuture()
    {
        var token = CustomToken(
            Secret,
            $$"""{"exp":{{Now.ToUnixTimeSeconds() + 600}},"nbf":{{Now.ToUnixTimeSeconds() + 60}},"user_id":9}""");

        var error = Assert.Throws<InvalidOperationException>(
            () => JwtTokenCodec.Decode(token, Secret, Now));

        Assert.Equal("token is not valid yet", error.Message);
    }

    [Fact]
    public void DecodeRejectsNonHmacAlgorithms()
    {
        var token = CustomToken(Secret, """{"exp":9999999999,"user_id":9}""", "none");

        var error = Assert.Throws<InvalidOperationException>(
            () => JwtTokenCodec.Decode(token, Secret, Now));

        Assert.Equal("unexpected signing method: none", error.Message);
    }

    [Fact]
    public void DecodeRejectsMalformedSegments()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => JwtTokenCodec.Decode("only-one-segment", Secret, Now));

        Assert.StartsWith("token is malformed: token contains an invalid number", error.Message);
    }

    [Fact]
    public void DecodeRejectsANonNumericExpiry()
    {
        var token = CustomToken(Secret, """{"exp":"soon","user_id":9}""");

        var error = Assert.Throws<InvalidOperationException>(
            () => JwtTokenCodec.Decode(token, Secret, Now));

        Assert.Equal("token has invalid claims: invalid type for claim: exp", error.Message);
    }

    [Fact]
    public void DecodeLeavesNonNumericUserIdAndAuthTimeForTheCallerToClassify()
    {
        // jwtTimeClaim ignores a non-numeric auth_time; the user_id type
        // assertion fails later in AuthenticateJWTToken. Neither is a parse error.
        var token = CustomToken(
            Secret,
            """{"auth_time":"yesterday","exp":9999999999,"user_id":"9"}""");

        var claims = JwtTokenCodec.Decode(token, Secret, Now);

        Assert.Null(claims.UserId);
        Assert.Null(claims.AuthTime);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void DecodeTreatsNonPositiveAuthTimeAsAbsentLikeJwtTimeClaim(string authTime)
    {
        // AuthService.jwtTimeClaim: value <= 0 -> ok == false, so the maximum
        // session age check is skipped.
        var token = CustomToken(
            Secret,
            $$"""{"auth_time":{{authTime}},"exp":9999999999,"user_id":9}""");

        var claims = JwtTokenCodec.Decode(token, Secret, Now);

        Assert.Null(claims.AuthTime);
    }

    [Fact]
    public void DecodeTruncatesAFractionalAuthTimeToWholeSeconds()
    {
        // jwtTimeClaim converts float64 seconds with int64 truncation.
        var token = CustomToken(
            Secret,
            """{"auth_time":90.9,"exp":9999999999,"user_id":9}""");

        var claims = JwtTokenCodec.Decode(token, Secret, Now);

        Assert.Equal((long?)90L, claims.AuthTime);
    }

    [Fact]
    public void EncodeIsDeterministicForTheSameInputs()
    {
        var first = JwtTokenCodec.EncodeHs256(Secret, 9, 100, 90, 200);
        var second = JwtTokenCodec.EncodeHs256(Secret, 9, 100, 90, 200);

        Assert.Equal(first, second);
    }

    private static string CustomToken(string secret, string payload, string algorithm = "HS256")
    {
        var header = $$"""{"alg":"{{algorithm}}","typ":"JWT"}""";
        var headerSegment = Base64UrlEncode(Encoding.UTF8.GetBytes(header));
        var payloadSegment = Base64UrlEncode(Encoding.UTF8.GetBytes(payload));
        var signingInput = headerSegment + "." + payloadSegment;
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.ASCII.GetBytes(signingInput);
        var signature = algorithm switch
        {
            "HS256" => HMACSHA256.HashData(key, data),
            "HS384" => HMACSHA384.HashData(key, data),
            "HS512" => HMACSHA512.HashData(key, data),
            _ => Array.Empty<byte>(),
        };

        return signingInput + "." + Base64UrlEncode(signature);
    }

    private static string Base64UrlEncode(byte[] bytes)
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
