using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Auth.Oidc;

/// <summary>One RSA key of a JWKS document.</summary>
public sealed record OidcJwksKey(string? KeyId, string KeyType, string? Modulus, string? Exponent);

/// <summary>HTTP seam for the JWKS document behind the ID-token verifier.</summary>
public interface IOidcJwksClient
{
    Task<IReadOnlyList<OidcJwksKey>> GetKeysAsync(
        string jwksUri,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Fetches and parses the provider's JWKS (reference:
/// <c>oidc.NewRemoteKeySet</c>). go-oidc caches keys per remote key set; the
/// port fetches per verification (C170).
/// </summary>
public sealed class OidcJwksClient(HttpClient client) : IOidcJwksClient
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<OidcJwksKey>> GetKeysAsync(
        string jwksUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(jwksUri);

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(jwksUri, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OidcProtocolException("oidc: failed to get keys: " + ex.Message, ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new OidcProtocolException(
                    "oidc: failed to get keys: " + (int)response.StatusCode + ": " + body);
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                if (!document.RootElement.TryGetProperty("keys", out var keys)
                    || keys.ValueKind != JsonValueKind.Array)
                {
                    return [];
                }

                var result = new List<OidcJwksKey>(keys.GetArrayLength());
                foreach (var key in keys.EnumerateArray())
                {
                    if (key.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    result.Add(new OidcJwksKey(
                        GetString(key, "kid"),
                        GetString(key, "kty") ?? string.Empty,
                        GetString(key, "n"),
                        GetString(key, "e")));
                }

                return result;
            }
            catch (JsonException ex)
            {
                throw new OidcProtocolException(
                    "oidc: failed to decode keys: " + ex.Message, ex);
            }
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>The inputs of one ID-token verification.</summary>
public sealed record OidcIdTokenRequest(
    string RawToken,
    string Issuer,
    string ClientId,
    string? JwksUri);

/// <summary>The verified subject and the raw claim map.</summary>
public sealed record OidcVerifiedIdToken(
    string Subject,
    IReadOnlyDictionary<string, object?> Claims);

/// <summary>go-oidc <c>IDTokenVerifier.Verify</c> seam.</summary>
public interface IOidcIdTokenVerifier
{
    Task<OidcVerifiedIdToken> VerifyAsync(
        OidcIdTokenRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Port of the go-oidc v3 ID-token verification the reference performs with
/// <c>provider.Verifier</c>: compact JWS parsing, RS256 verification against
/// the JWKS, and the issuer/audience/expiry checks with go-oidc's error texts.
/// The port uses <see cref="RSA"/> directly instead of go-jose; malformed-token
/// and key-fetch messages are approximations (C170).
/// </summary>
public sealed class OidcIdTokenVerifier(IOidcJwksClient jwks) : IOidcIdTokenVerifier
{
    /// <inheritdoc />
    public async Task<OidcVerifiedIdToken> VerifyAsync(
        OidcIdTokenRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // jose.ParseSigned: compact serialization, then the protected header and
        // the three base64url segments are validated before any key is fetched.
        var headerPart = request.RawToken;
        var separator = headerPart.IndexOf('.', StringComparison.Ordinal);
        if (separator < 0)
        {
            throw Malformed(ThreePartsError);
        }

        var payloadPart = headerPart[(separator + 1)..];
        headerPart = headerPart[..separator];
        separator = payloadPart.IndexOf('.', StringComparison.Ordinal);
        if (separator < 0)
        {
            throw Malformed(ThreePartsError);
        }

        var signaturePart = payloadPart[(separator + 1)..];
        payloadPart = payloadPart[..separator];
        if (signaturePart.Contains('.', StringComparison.Ordinal))
        {
            throw Malformed(ThreePartsError);
        }

        byte[] headerBytes;
        byte[] payload;
        byte[] signature;
        try
        {
            headerBytes = DecodeRawUrl(headerPart);
            payload = DecodeRawUrl(payloadPart);
            signature = DecodeRawUrl(signaturePart);
        }
        catch (FormatException ex)
        {
            throw Malformed(ex.Message, ex);
        }

        string algorithm;
        string? keyId;
        try
        {
            using var header = JsonDocument.Parse(headerBytes);
            if (header.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Malformed(
                    "json: cannot unmarshal "
                    + KindWord(header.RootElement.ValueKind)
                    + " into Go value of type jose.rawHeader");
            }

            algorithm = GetString(header.RootElement, "alg") ?? string.Empty;
            keyId = GetString(header.RootElement, "kid");
        }
        catch (JsonException ex)
        {
            throw Malformed(GoJsonError(headerBytes, ex), ex);
        }

        if (!string.Equals(algorithm, "RS256", StringComparison.Ordinal))
        {
            throw Malformed(
                "unexpected signature algorithm " + GoCast.GoQuote(algorithm)
                + "; expected [\"RS256\"]");
        }

        if (string.IsNullOrEmpty(request.JwksUri))
        {
            // RemoteKeySet.updateKeys: http.NewRequest succeeds for an empty
            // URL, then the HTTP client renders the missing scheme.
            throw new OidcProtocolException(
                "failed to verify signature: fetching keys oidc: get keys failed "
                + "Get \"\": unsupported protocol scheme \"\"");
        }

        IReadOnlyList<OidcJwksKey> keys;
        try
        {
            keys = await jwks.GetKeysAsync(request.JwksUri, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OidcProtocolException(
                "failed to verify signature: fetching keys " + ex.Message,
                ex);
        }

        var signingInput = Encoding.ASCII.GetBytes(headerPart + "." + payloadPart);
        var verified = false;
        foreach (var key in keys)
        {
            if (!string.Equals(key.KeyType, "RSA", StringComparison.Ordinal)
                || key.Modulus is null || key.Exponent is null)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(keyId) && !string.Equals(key.KeyId, keyId, StringComparison.Ordinal))
            {
                continue;
            }

            using var rsa = RSA.Create();
            try
            {
                rsa.ImportParameters(new RSAParameters
                {
                    Modulus = DecodeRawUrl(key.Modulus),
                    Exponent = DecodeRawUrl(key.Exponent),
                });
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                continue;
            }

            if (rsa.VerifyData(
                    signingInput,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1))
            {
                verified = true;
                break;
            }
        }

        if (!verified)
        {
            throw new OidcProtocolException(
                "failed to verify signature: failed to verify id token signature");
        }

        JsonDocument claimsDocument;
        try
        {
            claimsDocument = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            throw new OidcProtocolException(
                "oidc: failed to unmarshal claims: " + GoJsonError(payload, ex), ex);
        }

        using (claimsDocument)
        {
            if (claimsDocument.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new OidcProtocolException(
                    "oidc: failed to unmarshal claims: json: cannot unmarshal "
                    + KindWord(claimsDocument.RootElement.ValueKind)
                    + " into Go value of type oidc.idToken");
            }

            var root = claimsDocument.RootElement;
            var rawClaims = OidcClaims.FromJson(root);

            var issuer = GetString(root, "iss") ?? string.Empty;
            if (!string.Equals(issuer, request.Issuer, StringComparison.Ordinal))
            {
                throw new OidcProtocolException(
                    "oidc: id token issued by a different provider, expected "
                    + GoCast.GoQuote(request.Issuer) + " got " + GoCast.GoQuote(issuer));
            }

            var audiences = GetAudiences(root);
            if (!audiences.Contains(request.ClientId, StringComparer.Ordinal))
            {
                throw new OidcProtocolException(
                    "oidc: expected audience " + GoCast.GoQuote(request.ClientId)
                    + " got " + OidcProtocol.GoStringSlice(audiences));
            }

            var expiry = GetExpiry(root);
            if (expiry is null || expiry.Value <= DateTimeOffset.UtcNow)
            {
                var value = expiry ?? DateTimeOffset.MinValue;
                throw new OidcProtocolException(
                    "oidc: token is expired (Token Expiry: "
                    + OidcProtocol.GoTimeString(value) + ")");
            }

            var subject = GetString(root, "sub") ?? string.Empty;
            return new OidcVerifiedIdToken(subject, rawClaims);
        }
    }

    /// <summary>go-jose's compact-serialization error, verbatim.</summary>
    private const string ThreePartsError = "compact JWS format must have three parts";

    /// <summary>go-oidc's wrapper for <c>jose.ParseSigned</c> failures.</summary>
    private static OidcProtocolException Malformed(string detail, Exception? inner = null)
        => new("oidc: malformed jwt: " + detail, inner);

    /// <summary>Go's decoding-error word for a JSON value kind.</summary>
    private static string KindWord(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        _ => "null",
    };

    /// <summary>
    /// Go's <c>encoding/json</c> text for an input the decoder rejected:
    /// whitespace-only input is "unexpected end of JSON input"; other decoder
    /// details are approximated by the platform message (C170).
    /// </summary>
    private static string GoJsonError(byte[] input, JsonException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        foreach (var value in input)
        {
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r'))
            {
                return exception.Message;
            }
        }

        return "unexpected end of JSON input";
    }

    private static string? GetString(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : null;
            }
        }

        return null;
    }

    private static List<string> GetAudiences(JsonElement root)
    {
        var audiences = new List<string>();
        if (!root.TryGetProperty("aud", out var audience))
        {
            return audiences;
        }

        if (audience.ValueKind == JsonValueKind.String)
        {
            audiences.Add(audience.GetString() ?? string.Empty);
        }
        else if (audience.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in audience.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    audiences.Add(item.GetString() ?? string.Empty);
                }
            }
        }

        return audiences;
    }

    private static DateTimeOffset? GetExpiry(JsonElement root)
    {
        if (!root.TryGetProperty("exp", out var expiry))
        {
            return null;
        }

        if (expiry.ValueKind == JsonValueKind.Number && expiry.TryGetDouble(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds((long)seconds);
        }

        if (expiry.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                expiry.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    /// <summary>
    /// Go's <c>base64.RawURLEncoding</c> decoder (non-strict, no padding):
    /// returns Go's <c>illegal base64 data at input byte N</c> text, with N
    /// relative to the segment the reference decodes.
    /// </summary>
    private static byte[] DecodeRawUrl(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var output = new List<byte>(value.Length / 4 * 3 + 3);
        var index = 0;
        while (index < value.Length)
        {
            var remaining = value.Length - index;
            if (remaining == 1)
            {
                throw new FormatException("illegal base64 data at input byte " + index);
            }

            var q0 = AlphabetValue(value[index]);
            var q1 = AlphabetValue(value[index + 1]);
            if (q0 < 0)
            {
                throw new FormatException("illegal base64 data at input byte " + index);
            }

            if (q1 < 0)
            {
                throw new FormatException("illegal base64 data at input byte " + (index + 1));
            }

            output.Add((byte)((q0 << 2) | (q1 >> 4)));
            if (remaining == 2)
            {
                break;
            }

            var q2 = AlphabetValue(value[index + 2]);
            if (q2 < 0)
            {
                throw new FormatException("illegal base64 data at input byte " + (index + 2));
            }

            output.Add((byte)(((q1 & 0x0F) << 4) | (q2 >> 2)));
            if (remaining == 3)
            {
                break;
            }

            var q3 = AlphabetValue(value[index + 3]);
            if (q3 < 0)
            {
                throw new FormatException("illegal base64 data at input byte " + (index + 3));
            }

            output.Add((byte)(((q2 & 0x03) << 6) | q3));
            index += 4;
        }

        return output.ToArray();
    }

    private static int AlphabetValue(char value) => value switch
    {
        >= 'A' and <= 'Z' => value - 'A',
        >= 'a' and <= 'z' => value - 'a' + 26,
        >= '0' and <= '9' => value - '0' + 52,
        '-' => 62,
        '_' => 63,
        _ => -1,
    };
}
