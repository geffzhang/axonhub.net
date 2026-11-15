using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Vertex;

/// <summary>Port-only seams for the GCP token sources (transport and clock).</summary>
public sealed class GcpTokenOptions
{
    /// <summary>
    /// The handler used for token requests. When null the source builds its own
    /// client (no timeout, like Go's <c>http.DefaultClient</c>).
    /// </summary>
    public HttpMessageHandler? Transport { get; set; }

    /// <summary>The clock used for JWT issuance and expiry checks (tests inject one).</summary>
    public Func<DateTimeOffset>? Clock { get; set; }
}

/// <summary>Port of <c>oauth2.TokenSource</c>.</summary>
public interface IGcpTokenSource
{
    GcpToken GetToken();
}

/// <summary>Port of <c>oauth2.Token</c> (token.go:35-79).</summary>
public sealed class GcpToken
{
    public string AccessToken { get; init; } = string.Empty;

    public string TokenType { get; init; } = string.Empty;

    /// <summary>Null means the zero Go time: the token is reused forever.</summary>
    public DateTimeOffset? Expiry { get; init; }

    /// <summary>Port of <c>(*Token).Type</c> (token.go:92-105).</summary>
    public string Type
    {
        get
        {
            if (string.Equals(TokenType, "bearer", StringComparison.OrdinalIgnoreCase))
            {
                return "Bearer";
            }

            if (string.Equals(TokenType, "mac", StringComparison.OrdinalIgnoreCase))
            {
                return "MAC";
            }

            if (string.Equals(TokenType, "basic", StringComparison.OrdinalIgnoreCase))
            {
                return "Basic";
            }

            return TokenType.Length == 0 ? "Bearer" : TokenType;
        }
    }
}

/// <summary>
/// Port of <c>oauth2.reuseTokenSource</c> (oauth2.go:328-348, token.go:169-183):
/// cache the token until 10 seconds before its expiry; a zero expiry means the
/// token never expires. An empty access token is never valid, so it is fetched
/// again on every request (the reference behaves the same way).
/// </summary>
internal sealed class ReuseGcpTokenSource(IGcpTokenSource inner, GcpTokenOptions options) : IGcpTokenSource
{
    private static readonly TimeSpan ExpiryDelta = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private GcpToken? _token;

    public GcpToken GetToken()
    {
        lock (_gate)
        {
            if (_token is not null && IsValid(_token))
            {
                return _token;
            }

            _token = inner.GetToken();
            return _token;
        }
    }

    private bool IsValid(GcpToken token)
    {
        if (token.AccessToken.Length == 0)
        {
            return false;
        }

        if (token.Expiry is not { } expiry)
        {
            return true;
        }

        var now = options.Clock?.Invoke() ?? DateTimeOffset.UtcNow;
        // Go: expired() is now.After(expiry - delta), so equality is still valid.
        return now <= expiry - ExpiryDelta;
    }
}

/// <summary>
/// Port of <c>jwtSource</c> (golang.org/x/oauth2/jwt/jwt.go:96-166): sign a
/// JWT-bearer assertion with the service-account key and exchange it at the
/// token endpoint.
/// </summary>
internal sealed class ServiceAccountGcpTokenSource : IGcpTokenSource
{
    private const string JwtBearerGrantType = "urn:ietf:params:oauth:grant-type:jwt-bearer";
    private const string JwtTokenUrl = "https://oauth2.googleapis.com/token";

    private readonly string _email;
    private readonly string _privateKey;
    private readonly string _privateKeyId;
    private readonly string _tokenUrl;
    private readonly string _audience;
    private readonly GcpTokenOptions _options;
    private readonly HttpClient _client;

    public ServiceAccountGcpTokenSource(GcpCredentialFile file, GcpTokenOptions options)
    {
        _email = file.ClientEmail;
        _privateKey = file.PrivateKey;
        _privateKeyId = file.PrivateKeyId;
        _tokenUrl = file.TokenUri.Length == 0 ? JwtTokenUrl : file.TokenUri;
        // jwtSource sets Aud to the token URL unless the config overrides it
        // with a non-empty audience (oauth2/jwt/jwt.go:120-124).
        _audience = file.Audience.Length == 0 ? _tokenUrl : file.Audience;
        _options = options;
        _client = GcpTokenHttp.CreateClient(options.Transport);
    }

    public GcpToken GetToken()
    {
        var assertion = BuildAssertion();

        var form = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["grant_type"] = [JwtBearerGrantType],
            ["assertion"] = [assertion],
        };

        var result = GcpTokenHttp.PostForm(_client, _tokenUrl, form);
        if (result.Status is < 200 or > 299)
        {
            throw new InvalidOperationException(GcpTokenHttp.StatusError(result));
        }

        return ParseTokenResponse(result.Body);
    }

    /// <summary>Port of <c>jws.Encode</c> with an RS256 header and ClaimSet.</summary>
    private string BuildAssertion()
    {
        using var key = ParseKey(_privateKey);

        var now = _options.Clock?.Invoke() ?? DateTimeOffset.UtcNow;
        // jws.ClaimSet.encode (jws.go:68-84) backdates iat by 10s and, when the
        // claim is zero, sets exp to iat+1h.
        var issuedAt = now.AddSeconds(-10).ToUnixTimeSeconds();
        var expiresAt = issuedAt + 3600;

        var header = _privateKeyId.Length == 0
            ? "{\"alg\":\"RS256\",\"typ\":\"JWT\"}"
            : "{\"alg\":\"RS256\",\"typ\":\"JWT\",\"kid\":" + GoJson.MarshalToString(_privateKeyId) + "}";

        // ClaimSet field order: iss, scope, aud, exp, iat, typ, sub, prn. The
        // reference passes no scopes and no subject, and both are omitempty.
        var claims = new StringBuilder();
        claims.Append("{\"iss\":").Append(GoJson.MarshalToString(_email));
        claims.Append(",\"aud\":").Append(GoJson.MarshalToString(_audience));
        claims.Append(",\"exp\":").Append(expiresAt.ToString(CultureInfo.InvariantCulture));
        claims.Append(",\"iat\":").Append(issuedAt.ToString(CultureInfo.InvariantCulture));
        claims.Append('}');

        var signingInput = Base64Url(Encoding.UTF8.GetBytes(header))
            + "."
            + Base64Url(Encoding.UTF8.GetBytes(claims.ToString()));
        var signature = key.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return signingInput + "." + Base64Url(signature);
    }

    /// <summary>Port of <c>internal.ParseKey</c> (oauth2/internal/oauth2.go:27-47).</summary>
    private static RSA ParseKey(string pem)
    {
        var key = Encoding.UTF8.GetBytes(pem);
        if (PemEncoding.TryFind(pem, out var fields))
        {
            try
            {
                key = Convert.FromBase64String(pem[fields.Base64Data].ToString());
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    "private key should be a PEM or plain PKCS1 or PKCS8; parse error: " + ex.Message,
                    ex);
            }
        }

        var rsa = RSA.Create();
        try
        {
            rsa.ImportPkcs8PrivateKey(key, out _);
            return rsa;
        }
        catch (CryptographicException)
        {
            // Fall through to PKCS#1, like x509.ParsePKCS1PrivateKey.
        }

        try
        {
            rsa.ImportRSAPrivateKey(key, out _);
            return rsa;
        }
        catch (CryptographicException ex)
        {
            rsa.Dispose();
            throw new InvalidOperationException(
                "private key should be a PEM or plain PKCS1 or PKCS8; parse error: " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Port of the token decode in <c>jwtSource.Token</c>
    /// (jwt.go:134-158) — unlike the refresh flow it never validates that an
    /// access token was returned.
    /// </summary>
    private GcpToken ParseTokenResponse(byte[] body)
    {
        var syntax = GoJsonSyntax.Validate(body);
        if (syntax is not null)
        {
            throw new InvalidOperationException("oauth2: cannot fetch token: " + syntax);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("oauth2: cannot fetch token: " + ex.Message, ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Null)
            {
                return new GcpToken();
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "oauth2: cannot fetch token: json: cannot unmarshal "
                    + GcpTokenHttp.KindName(root) + " into Go value of type struct");
            }

            var accessToken = ReadString(root, "access_token");
            var tokenType = ReadString(root, "token_type");
            var refreshToken = ReadString(root, "refresh_token");
            var idToken = ReadString(root, "id_token");
            var expiresIn = ReadInt64(root, "expires_in");

            DateTimeOffset? expiry = null;
            if (expiresIn > 0)
            {
                var now = _options.Clock?.Invoke() ?? DateTimeOffset.UtcNow;
                expiry = now.AddSeconds(expiresIn);
            }

            if (idToken.Length > 0)
            {
                expiry = DecodeJwtExpiry(idToken);
            }

            return new GcpToken
            {
                AccessToken = accessToken,
                TokenType = tokenType,
                Expiry = expiry,
            };
        }
    }

    private static string ReadString(JsonElement root, string foldedName)
    {
        if (!GcpTokenHttp.TryGetProperty(root, foldedName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "oauth2: cannot fetch token: json: cannot unmarshal "
                + GcpTokenHttp.KindName(value) + " into Go struct field ." + foldedName
                + " of type string");
        }

        return value.GetString() ?? string.Empty;
    }

    private static long ReadInt64(JsonElement root, string foldedName)
    {
        if (!GcpTokenHttp.TryGetProperty(root, foldedName, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed))
        {
            return parsed;
        }

        // A fractional or out-of-range JSON number reports Go's "number <literal>".
        var kind = value.ValueKind == JsonValueKind.Number
            ? "number " + value.GetRawText()
            : GcpTokenHttp.KindName(value);
        throw new InvalidOperationException(
            "oauth2: cannot fetch token: json: cannot unmarshal "
            + kind + " into Go struct field ." + foldedName
            + " of type int64");
    }

    /// <summary>
    /// Port of <c>jws.Decode</c> (jws.go:84-101) for the id_token expiry
    /// override: three dot-separated parts, the claims decoded as one JSON
    /// value, a missing <c>exp</c> claim decoding as the zero Unix time.
    /// </summary>
    private DateTimeOffset DecodeJwtExpiry(string token)
    {
        var first = token.IndexOf('.', StringComparison.Ordinal);
        var second = first < 0 ? -1 : token.IndexOf('.', first + 1);
        var third = second < 0 ? -1 : token.IndexOf('.', second + 1);
        if (first < 0 || second < 0 || third >= 0)
        {
            throw new InvalidOperationException(
                "oauth2: error decoding JWT token: jws: invalid token received");
        }

        byte[] claims;
        try
        {
            claims = Base64UrlDecode(token[(first + 1)..second]);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException(
                "oauth2: error decoding JWT token: " + ex.Message,
                ex);
        }

        // json.Decoder.Decode reads one value and ignores anything after it.
        var syntax = GoJsonSyntax.ScanDecoderValue(claims, 0, out var end);
        if (syntax is not null)
        {
            throw new InvalidOperationException("oauth2: error decoding JWT token: " + syntax);
        }

        using var document = JsonDocument.Parse(claims.AsMemory(0, end));
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Null)
        {
            // Unmarshaling null into the ClaimSet leaves the zero value.
            return DateTimeOffset.FromUnixTimeSeconds(0);
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "oauth2: error decoding JWT token: json: cannot unmarshal "
                + GcpTokenHttp.KindName(root) + " into Go value of type jws.ClaimSet");
        }

        if (!GcpTokenHttp.TryGetProperty(root, "exp", out var exp)
            || exp.ValueKind == JsonValueKind.Null)
        {
            return DateTimeOffset.FromUnixTimeSeconds(0);
        }

        if (exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        var kind = exp.ValueKind == JsonValueKind.Number
            ? "number " + exp.GetRawText()
            : GcpTokenHttp.KindName(exp);
        throw new InvalidOperationException(
            "oauth2: error decoding JWT token: json: cannot unmarshal "
            + kind + " into Go struct field ClaimSet.exp of type int64");
    }

    private static string Base64Url(byte[] data)
        => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        var base64 = text.Replace('-', '+').Replace('_', '/');
        return (base64.Length % 4) switch
        {
            0 => Convert.FromBase64String(base64),
            2 => Convert.FromBase64String(base64 + "=="),
            3 => Convert.FromBase64String(base64 + "="),
            _ => throw new FormatException("illegal base64 data"),
        };
    }
}

/// <summary>
/// Port of <c>tokenRefresher</c> (oauth2.go:265-291): exchange the stored
/// refresh token for an access token, with the client credentials in the form
/// body (Google's <c>AuthStyleInParams</c>).
/// </summary>
internal sealed class AuthorizedUserGcpTokenSource : IGcpTokenSource
{
    private const string DefaultTokenUrl = "https://oauth2.googleapis.com/token";

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _tokenUrl;
    private readonly GcpTokenOptions _options;
    private readonly HttpClient _client;
    private readonly object _gate = new();
    private string _refreshToken;

    public AuthorizedUserGcpTokenSource(GcpCredentialFile file, GcpTokenOptions options)
    {
        _clientId = file.ClientId;
        _clientSecret = file.ClientSecret;
        _tokenUrl = file.TokenUri.Length == 0 ? DefaultTokenUrl : file.TokenUri;
        _options = options;
        _client = GcpTokenHttp.CreateClient(options.Transport);
        _refreshToken = file.RefreshToken;
    }

    public GcpToken GetToken()
    {
        string refreshToken;
        lock (_gate)
        {
            refreshToken = _refreshToken;
        }

        if (refreshToken.Length == 0)
        {
            throw new InvalidOperationException("oauth2: token expired and refresh token is not set");
        }

        var form = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["grant_type"] = ["refresh_token"],
            ["refresh_token"] = [refreshToken],
        };
        if (_clientId.Length > 0)
        {
            form["client_id"] = [_clientId];
        }

        if (_clientSecret.Length > 0)
        {
            form["client_secret"] = [_clientSecret];
        }

        var (token, newRefreshToken) = GcpTokenHttp.Retrieve(_client, _tokenUrl, form, _options);

        if (!string.Equals(refreshToken, newRefreshToken, StringComparison.Ordinal))
        {
            lock (_gate)
            {
                _refreshToken = newRefreshToken;
            }
        }

        return token;
    }
}

/// <summary>
/// Port of externalaccount.fileCredentialSource (filecredsource.go): read a
/// subject token from a text or JSON file, bounded to 1 MiB.
/// </summary>
internal sealed class ExternalAccountFileGcpTokenSource : IGcpTokenSource
{
    private readonly string _file;
    private readonly GcpCredentialFormat _format;

    public ExternalAccountFileGcpTokenSource(string file, GcpCredentialFormat format)
    {
        _file = file;
        _format = format;
    }

    public GcpToken GetToken()
        => throw new InvalidOperationException(
            "external account subject token is not an access token");

    public string SubjectToken()
    {
        byte[] bytes;
        try
        {
            using var stream = File.OpenRead(_file);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (buffer.Length < (1 << 20))
            {
                var read = stream.Read(
                    chunk,
                    0,
                    (int)Math.Min(chunk.Length, (1 << 20) - buffer.Length));
                if (read <= 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
            }

            bytes = buffer.ToArray();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "oauth2/google/externalaccount: failed to open credential file "
                + GoCast.GoQuote(_file),
                ex);
        }

        return ParseSubjectToken(Encoding.UTF8.GetString(bytes).Trim(), _format);
    }

    internal static string ParseSubjectToken(string body, GcpCredentialFormat format)
    {
        switch (format.Type)
        {
            case "json":
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(body);
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException(
                        "oauth2/google/externalaccount: failed to unmarshal "
                        + "subject token file: " + ex.Message,
                        ex);
                }

                using (document)
                {
                    if (document.RootElement.ValueKind != JsonValueKind.Object
                        || !document.RootElement.TryGetProperty(
                            format.SubjectTokenFieldName,
                            out var value))
                    {
                        throw new InvalidOperationException(
                            "oauth2/google/externalaccount: provided "
                            + "subject_token_field_name not found in credentials");
                    }

                    if (value.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidOperationException(
                            "oauth2/google/externalaccount: improperly formatted subject token");
                    }

                    return value.GetString() ?? string.Empty;
                }

            case "text":
            case "":
                return body;

            default:
                throw new InvalidOperationException(
                    "oauth2/google/externalaccount: invalid credential_source file format type");
        }
    }
}

/// <summary>
/// Port of externalaccount.urlCredentialSource (urlcredsource.go): GET a
/// subject token from a local URL with optional headers.
/// </summary>
internal sealed class ExternalAccountUrlGcpTokenSource : IGcpTokenSource
{
    private readonly string _url;
    private readonly IReadOnlyDictionary<string, string>? _headers;
    private readonly GcpCredentialFormat _format;
    private readonly GcpTokenOptions _options;

    public ExternalAccountUrlGcpTokenSource(
        string url,
        IReadOnlyDictionary<string, string>? headers,
        GcpCredentialFormat format,
        GcpTokenOptions options)
    {
        _url = url;
        _headers = headers;
        _format = format;
        _options = options;
    }

    public GcpToken GetToken()
        => throw new InvalidOperationException(
            "external account subject token is not an access token");

    public string SubjectToken()
    {
        using var client = GcpTokenHttp.CreateClient(_options.Transport);
        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        if (_headers is not null)
        {
            foreach (var (key, value) in _headers)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        using var response = client.SendAsync(request, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var body = response.Content?.ReadAsStringAsync().GetAwaiter().GetResult()
            ?? string.Empty;

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "oauth2/google/externalaccount: status code "
                + (int)response.StatusCode + ": " + body);
        }

        return ExternalAccountFileGcpTokenSource.ParseSubjectToken(body, _format);
    }
}

/// <summary>
/// Port of externalaccount.tokenSource.Token (basecredentials.go:460-505):
/// fetch the subject token and exchange it for a GCP access token via STS.
/// </summary>
internal sealed class ExternalAccountGcpTokenSource : IGcpTokenSource
{
    private readonly ExternalAccountFileGcpTokenSource? _fileSource;
    private readonly ExternalAccountUrlGcpTokenSource? _urlSource;
    private readonly string _audience;
    private readonly string _subjectTokenType;
    private readonly string _tokenUrl;
    private readonly IReadOnlyList<string> _scopes;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _workforcePoolUserProject;
    private readonly GcpTokenOptions _options;
    private readonly HttpClient _client;

    public ExternalAccountGcpTokenSource(
        IGcpTokenSource source,
        string audience,
        string subjectTokenType,
        string tokenUrl,
        IReadOnlyList<string> scopes,
        string clientId,
        string clientSecret,
        string workforcePoolUserProject,
        GcpTokenOptions options)
    {
        _fileSource = source as ExternalAccountFileGcpTokenSource;
        _urlSource = source as ExternalAccountUrlGcpTokenSource;
        _audience = audience;
        _subjectTokenType = subjectTokenType;
        _tokenUrl = tokenUrl;
        _scopes = scopes;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _workforcePoolUserProject = workforcePoolUserProject;
        _options = options;
        _client = GcpTokenHttp.CreateClient(options.Transport);
    }

    public GcpToken GetToken()
    {
        var subjectToken = _fileSource?.SubjectToken()
            ?? _urlSource?.SubjectToken()
            ?? throw new InvalidOperationException(
                "oauth2/google/externalaccount: unable to parse credential source");

        var form = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["audience"] = [_audience],
            ["grant_type"] = ["urn:ietf:params:oauth:grant-type:token-exchange"],
            ["requested_token_type"] = ["urn:ietf:params:oauth:token-type:access_token"],
            ["subject_token_type"] = [_subjectTokenType],
            ["subject_token"] = [subjectToken],
            ["scope"] = [string.Join(" ", _scopes)],
        };

        // Do not pass workforce_pool_user_project when client authentication
        // is used; the client ID is sufficient there.
        if (_workforcePoolUserProject.Length > 0 && _clientId.Length == 0)
        {
            form["options"] = [
                """{"userProject":""" + GoJson.MarshalToString(_workforcePoolUserProject) + "}",
            ];
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _tokenUrl);
        var content = new ByteArrayContent(
            Encoding.UTF8.GetBytes(HttpClientUtils.EncodeQuery(form)));
        content.Headers.TryAddWithoutValidation(
            "Content-Type",
            "application/x-www-form-urlencoded");
        request.Content = content;

        if (_clientId.Length > 0 && _clientSecret.Length > 0)
        {
            request.Headers.TryAddWithoutValidation(
                "Authorization",
                "Basic " + Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(_clientId + ":" + _clientSecret)));
        }

        HttpResponseMessage response;
        try
        {
            response = _client.SendAsync(request, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "oauth2/google: invalid response from Secure Token Server: " + ex.Message,
                ex);
        }

        using (response)
        {
            var body = response.Content?.ReadAsStringAsync().GetAwaiter().GetResult()
                ?? string.Empty;
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    "oauth2/google: status code " + (int)response.StatusCode + ": " + body);
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "oauth2/google: failed to unmarshal response body from Secure Token Server: "
                    + ex.Message,
                    ex);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException(
                        "oauth2/google: failed to unmarshal response body from Secure Token Server: "
                        + "json: cannot unmarshal " + GcpTokenHttp.KindName(root)
                        + " into Go value of type stsexchange.Response");
                }

                var accessToken = ReadStsString(root, "access_token");
                var tokenType = ReadStsString(root, "token_type");
                var refreshToken = ReadStsString(root, "refresh_token");
                var expiresIn = ReadStsInt(root, "expires_in");
                if (expiresIn <= 0)
                {
                    throw new InvalidOperationException(
                        "oauth2/google/externalaccount: got invalid expiry from security token service");
                }

                var now = _options.Clock?.Invoke() ?? DateTimeOffset.UtcNow;
                return new GcpToken
                {
                    AccessToken = accessToken,
                    TokenType = tokenType,
                    Expiry = now.AddSeconds(expiresIn),
                };
            }
        }
    }

    private static string ReadStsString(JsonElement root, string name)
    {
        if (!GcpTokenHttp.TryGetProperty(root, name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "oauth2/google: failed to unmarshal response body from Secure Token Server: "
                + "json: cannot unmarshal " + GcpTokenHttp.KindName(value)
                + " into Go struct field Response." + name + " of type string");
        }

        return value.GetString() ?? string.Empty;
    }

    private static int ReadStsInt(JsonElement root, string name)
    {
        if (!GcpTokenHttp.TryGetProperty(root, name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            "oauth2/google: failed to unmarshal response body from Secure Token Server: "
            + "json: cannot unmarshal " + GcpTokenHttp.KindName(value)
            + " into Go struct field Response." + name + " of type int");
    }
}

/// <summary>
/// Port of google/internal/externalaccountauthorizeduser.tokenSource
/// (externalaccountauthorizeduser.go:84-168): exchange a workforce-pool
/// refresh token at the STS endpoint with HTTP Basic client authentication.
/// </summary>
internal sealed class ExternalAccountAuthorizedUserGcpTokenSource : IGcpTokenSource
{
    private const string CannotRefreshMessage =
        "oauth2/google: Token should be created with fields to make it valid "
        + "(`token` and `expiry`), or fields to allow it to refresh "
        + "(`refresh_token`, `token_url`, `client_id`, `client_secret`).";

    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly string _tokenUrl;
    private readonly GcpTokenOptions _options;
    private readonly HttpClient _client;
    private readonly object _gate = new();
    private string _refreshToken;

    private ExternalAccountAuthorizedUserGcpTokenSource(
        GcpCredentialFile file,
        GcpTokenOptions options)
    {
        _clientId = file.ClientId;
        _clientSecret = file.ClientSecret;
        _tokenUrl = file.TokenUrlExternal;
        _options = options;
        _client = GcpTokenHttp.CreateClient(options.Transport);
        _refreshToken = file.RefreshToken;
    }

    public static ExternalAccountAuthorizedUserGcpTokenSource Create(
        GcpCredentialFile file,
        GcpTokenOptions options)
    {
        if (!CanRefresh(file))
        {
            throw new InvalidOperationException(CannotRefreshMessage);
        }

        return new ExternalAccountAuthorizedUserGcpTokenSource(file, options);
    }

    private static bool CanRefresh(GcpCredentialFile file)
        => file.ClientId.Length > 0
            && file.ClientSecret.Length > 0
            && file.RefreshToken.Length > 0
            && file.TokenUrlExternal.Length > 0;

    public GcpToken GetToken()
    {
        string refreshToken;
        lock (_gate)
        {
            refreshToken = _refreshToken;
        }

        var form = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["grant_type"] = ["refresh_token"],
            ["refresh_token"] = [refreshToken],
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _tokenUrl);
        var content = new ByteArrayContent(
            Encoding.UTF8.GetBytes(HttpClientUtils.EncodeQuery(form)));
        content.Headers.TryAddWithoutValidation(
            "Content-Type",
            "application/x-www-form-urlencoded");
        request.Content = content;
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            "Basic " + Convert.ToBase64String(
                Encoding.UTF8.GetBytes(_clientId + ":" + _clientSecret)));

        HttpResponseMessage response;
        try
        {
            response = _client.SendAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "oauth2/google: invalid response from Secure Token Server: " + ex.Message,
                ex);
        }

        using (response)
        {
            byte[] body;
            try
            {
                body = ReadBody(response);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(ex.Message, ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    "oauth2/google: status code " + (int)response.StatusCode + ": "
                    + Encoding.UTF8.GetString(body));
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "oauth2/google: failed to unmarshal response body from Secure Token Server: "
                    + ex.Message,
                    ex);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException(
                        "oauth2/google: failed to unmarshal response body from Secure Token Server: "
                        + "json: cannot unmarshal " + GcpTokenHttp.KindName(root)
                        + " into Go value of type stsexchange.Response");
                }

                var accessToken = ReadStsString(root, "access_token");
                var newRefreshToken = ReadStsString(root, "refresh_token");
                var expiresIn = ReadStsInt(root, "expires_in");

                if (expiresIn < 0)
                {
                    throw new InvalidOperationException(
                        "oauth2/google: got invalid expiry from security token service");
                }

                if (newRefreshToken.Length > 0)
                {
                    lock (_gate)
                    {
                        _refreshToken = newRefreshToken;
                    }
                }

                var now = _options.Clock?.Invoke() ?? DateTimeOffset.UtcNow;
                return new GcpToken
                {
                    AccessToken = accessToken,
                    TokenType = "Bearer",
                    Expiry = now.AddSeconds(expiresIn),
                };
            }
        }
    }

    private static byte[] ReadBody(HttpResponseMessage response)
    {
        var stream = response.Content is null ? Stream.Null : response.Content.ReadAsStream();
        using (stream)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (buffer.Length < (1 << 20))
            {
                var read = stream.Read(
                    chunk,
                    0,
                    (int)Math.Min(chunk.Length, (1 << 20) - buffer.Length));
                if (read <= 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private static string ReadStsString(JsonElement root, string name)
    {
        if (!GcpTokenHttp.TryGetProperty(root, name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "oauth2/google: failed to unmarshal response body from Secure Token Server: "
                + "json: cannot unmarshal " + GcpTokenHttp.KindName(value)
                + " into Go struct field Response." + name + " of type string");
        }

        return value.GetString() ?? string.Empty;
    }

    private static int ReadStsInt(JsonElement root, string name)
    {
        if (!GcpTokenHttp.TryGetProperty(root, name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed))
        {
            return parsed;
        }

        throw new InvalidOperationException(
            "oauth2/google: failed to unmarshal response body from Secure Token Server: "
            + "json: cannot unmarshal " + GcpTokenHttp.KindName(value)
            + " into Go struct field Response." + name + " of type int");
    }
}

/// <summary>
/// Port of google/internal/impersonate.ImpersonateTokenSource
/// (impersonate.go:44-111): exchange a source token for a short-lived
/// impersonated access token via the IAM credentials endpoint.
/// </summary>
internal sealed class ImpersonatedGcpTokenSource : IGcpTokenSource
{
    private readonly IGcpTokenSource _source;
    private readonly string _url;
    private readonly IReadOnlyList<string>? _delegates;
    private readonly int _tokenLifetimeSeconds;
    private readonly GcpTokenOptions _options;
    private readonly HttpClient _client;

    public ImpersonatedGcpTokenSource(
        IGcpTokenSource source,
        string url,
        IReadOnlyList<string>? delegates,
        int tokenLifetimeSeconds,
        GcpTokenOptions options)
    {
        _source = source;
        _url = url;
        _delegates = delegates;
        _tokenLifetimeSeconds = tokenLifetimeSeconds;
        _options = options;
        _client = GcpTokenHttp.CreateClient(options.Transport);
    }

    public GcpToken GetToken()
    {
        var sourceToken = _source.GetToken();
        var lifetime = _tokenLifetimeSeconds > 0
            ? _tokenLifetimeSeconds.ToString(CultureInfo.InvariantCulture) + "s"
            : "3600s";

        var body = new ImpersonationRequest
        {
            Delegates = _delegates is { Count: > 0 } ? [.. _delegates] : null,
            Lifetime = lifetime,
        };

        var payload = JsonSerializer.Serialize(body);
        using var request = new HttpRequestMessage(HttpMethod.Post, _url);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            "Bearer " + sourceToken.AccessToken);

        HttpResponseMessage response;
        try
        {
            response = _client.SendAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "oauth2/google: unable to generate access token: " + ex.Message,
                ex);
        }

        using (response)
        {
            byte[] bodyBytes;
            try
            {
                bodyBytes = ReadBody(response);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "oauth2/google: unable to read body: " + ex.Message,
                    ex);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    "oauth2/google: status code " + (int)response.StatusCode + ": "
                    + Encoding.UTF8.GetString(bodyBytes));
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(bodyBytes);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "oauth2/google: unable to parse response: " + ex.Message,
                    ex);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException(
                        "oauth2/google: unable to parse response: "
                        + "json: cannot unmarshal " + GcpTokenHttp.KindName(root)
                        + " into Go value of type impersonate.impersonateTokenResponse");
                }

                var accessToken = ReadString(root, "accesstoken");
                var expireTime = ReadString(root, "expiretime");
                var expiry = GoTimeParsing.TryParseRfc3339(expireTime)
                    ?? throw new InvalidOperationException(
                        "oauth2/google: unable to parse expiry: parsing time "
                        + GoCast.GoQuote(expireTime)
                        + " as \"2006-01-02T15:04:05Z07:00\": cannot parse");

                return new GcpToken
                {
                    AccessToken = accessToken,
                    Expiry = expiry,
                    TokenType = "Bearer",
                };
            }
        }
    }

    private static byte[] ReadBody(HttpResponseMessage response)
    {
        var stream = response.Content is null ? Stream.Null : response.Content.ReadAsStream();
        using (stream)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (buffer.Length < (1 << 20))
            {
                var read = stream.Read(
                    chunk,
                    0,
                    (int)Math.Min(chunk.Length, (1 << 20) - buffer.Length));
                if (read <= 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private static string ReadString(JsonElement root, string name)
    {
        if (!GcpTokenHttp.TryGetProperty(root, name, out var value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "oauth2/google: unable to parse response: json: cannot unmarshal "
                + GcpTokenHttp.KindName(value)
                + " into Go struct field impersonateTokenResponse." + name
                + " of type string");
        }

        return value.GetString() ?? string.Empty;
    }

    private sealed class ImpersonationRequest
    {
        [JsonPropertyName("delegates")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Delegates { get; init; }

        [JsonPropertyName("lifetime")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Lifetime { get; init; }
    }
}

/// <summary>
/// Port of <c>authhandler.TokenSourceWithPKCE</c> for a client_credentials.json
/// document. The reference requires an interactive authorization handler and
/// panics on a nil one; the port reports the gap as an error instead (C128).
/// </summary>
internal sealed class ClientConfigGcpTokenSource : IGcpTokenSource
{
    public GcpToken GetToken() => throw new NotSupportedException(
        "GCP OAuth client credentials require an interactive authorization handler, which the .NET port does not provide (difference C128)");
}

/// <summary>
/// Port of the token-endpoint half of <c>oauth2/internal/token.go</c>
/// (RetrieveToken/doTokenRoundTrip) and of <c>jwtSource.Token</c>'s POST.
/// </summary>
internal static class GcpTokenHttp
{
    private const int MaxResponseBody = 1 << 20;

    public static HttpClient CreateClient(HttpMessageHandler? transport) => transport is null
        ? new HttpClient(new SocketsHttpHandler { UseCookies = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        }
        : new HttpClient(transport, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };

    /// <summary>Posts a form and reads at most 1 MiB of response body.</summary>
    public static TokenHttpResult PostForm(
        HttpClient client,
        string url,
        Dictionary<string, List<string>> form)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(HttpClientUtils.EncodeQuery(form)));
        content.Headers.TryAddWithoutValidation(
            "Content-Type",
            "application/x-www-form-urlencoded");
        request.Content = content;

        HttpResponseMessage response;
        try
        {
            response = client.SendAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("oauth2: cannot fetch token: " + ex.Message, ex);
        }

        using (response)
        {
            byte[] body;
            try
            {
                body = ReadLimited(response);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("oauth2: cannot fetch token: " + ex.Message, ex);
            }

            return new TokenHttpResult(
                (int)response.StatusCode,
                StatusLine(response),
                ContentType(response),
                body);
        }
    }

    /// <summary>RetrieveError text for a response without an OAuth error code.</summary>
    public static string StatusError(TokenHttpResult result)
        => "oauth2: cannot fetch token: " + result.StatusLine + "\nResponse: "
            + Encoding.UTF8.GetString(result.Body);

    /// <summary>Port of <c>oauth2.RetrieveError.Error</c> (token.go:243-254).</summary>
    private static string RetrieveError(
        TokenHttpResult result,
        string errorCode,
        string errorDescription,
        string errorUri)
    {
        if (errorCode.Length == 0)
        {
            return StatusError(result);
        }

        var message = new StringBuilder("oauth2: ").Append(GoCast.GoQuote(errorCode));
        if (errorDescription.Length > 0)
        {
            message.Append(' ').Append(GoCast.GoQuote(errorDescription));
        }

        if (errorUri.Length > 0)
        {
            message.Append(' ').Append(GoCast.GoQuote(errorUri));
        }

        return message.ToString();
    }

    /// <summary>Port of <c>retrieveToken</c> + <c>doTokenRoundTrip</c> (internal/token.go:231-320).</summary>
    public static (GcpToken Token, string RefreshToken) Retrieve(
        HttpClient client,
        string tokenUrl,
        Dictionary<string, List<string>> form,
        GcpTokenOptions options)
    {
        var result = PostForm(client, tokenUrl, form);
        var failureStatus = result.Status is < 200 or > 299;

        string accessToken;
        string tokenType;
        string refreshToken;
        long expiresIn;
        string errorCode;
        string errorDescription;
        string errorUri;

        var mediaType = GoMimeTypes.TryParseMediaType(result.ContentType, out var parsedType, out _)
            ? parsedType
            : string.Empty;

        if (mediaType is "application/x-www-form-urlencoded" or "text/plain")
        {
            var values = ParseForm(Encoding.UTF8.GetString(result.Body), out var formError);
            if (formError is not null)
            {
                if (failureStatus)
                {
                    throw new InvalidOperationException(
                        RetrieveError(result, string.Empty, string.Empty, string.Empty));
                }

                throw new InvalidOperationException("oauth2: cannot parse response: " + formError);
            }

            accessToken = GetFirst(values, "access_token");
            tokenType = GetFirst(values, "token_type");
            refreshToken = GetFirst(values, "refresh_token");
            errorCode = GetFirst(values, "error");
            errorDescription = GetFirst(values, "error_description");
            errorUri = GetFirst(values, "error_uri");
            // Go ignores strconv.Atoi's error and treats it as zero.
            expiresIn = GoCast.TryParseInt64(GetFirst(values, "expires_in"), out var seconds)
                ? seconds
                : 0;
        }
        else
        {
            var decoded = ParseJsonToken(result.Body, failureStatus, result);
            accessToken = decoded.AccessToken;
            tokenType = decoded.TokenType;
            refreshToken = decoded.RefreshToken;
            expiresIn = decoded.ExpiresIn;
            errorCode = decoded.ErrorCode;
            errorDescription = decoded.ErrorDescription;
            errorUri = decoded.ErrorUri;
        }

        if (failureStatus || errorCode.Length > 0)
        {
            throw new InvalidOperationException(
                RetrieveError(result, errorCode, errorDescription, errorUri));
        }

        if (accessToken.Length == 0)
        {
            throw new InvalidOperationException("oauth2: server response missing access_token");
        }

        // Don't overwrite RefreshToken with an empty value on a refresh request.
        if (refreshToken.Length == 0)
        {
            refreshToken = GetFirst(form, "refresh_token");
        }

        DateTimeOffset? expiry = null;
        if (expiresIn != 0)
        {
            var now = options.Clock?.Invoke() ?? DateTimeOffset.UtcNow;
            expiry = now.AddSeconds(expiresIn);
        }

        return (new GcpToken
        {
            AccessToken = accessToken,
            TokenType = tokenType,
            Expiry = expiry,
        }, refreshToken);
    }

    private static DecodedToken ParseJsonToken(
        byte[] body,
        bool failureStatus,
        TokenHttpResult result)
    {
        var syntax = GoJsonSyntax.Validate(body);
        if (syntax is not null)
        {
            if (failureStatus)
            {
                throw new InvalidOperationException(
                    RetrieveError(result, string.Empty, string.Empty, string.Empty));
            }

            throw new InvalidOperationException("oauth2: cannot parse json: " + syntax);
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Null)
        {
            return default;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            if (failureStatus)
            {
                throw new InvalidOperationException(
                    RetrieveError(result, string.Empty, string.Empty, string.Empty));
            }

            throw new InvalidOperationException(
                "oauth2: cannot parse json: json: cannot unmarshal " + KindName(root)
                + " into Go value of type internal.tokenJSON");
        }

        try
        {
            return new DecodedToken
            {
                AccessToken = TokenJsonString(root, "access_token"),
                TokenType = TokenJsonString(root, "token_type"),
                RefreshToken = TokenJsonString(root, "refresh_token"),
                ExpiresIn = TokenJsonExpiresIn(root),
                ErrorCode = TokenJsonString(root, "error"),
                ErrorDescription = TokenJsonString(root, "error_description"),
                ErrorUri = TokenJsonString(root, "error_uri"),
            };
        }
        catch (InvalidOperationException) when (failureStatus)
        {
            // json.Unmarshal errors are reported as a RetrieveError on a
            // non-2xx response, before any field is inspected.
            throw new InvalidOperationException(
                RetrieveError(result, string.Empty, string.Empty, string.Empty));
        }
    }

    private static string TokenJsonString(JsonElement root, string foldedName)
    {
        if (!TryGetProperty(root, foldedName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "oauth2: cannot parse json: json: cannot unmarshal " + KindName(value)
                + " into Go struct field tokenJSON." + foldedName + " of type string");
        }

        return value.GetString() ?? string.Empty;
    }

    private static long TokenJsonExpiresIn(JsonElement root)
    {
        if (!TryGetProperty(root, "expires_in", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return 0;
        }

        string raw;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                raw = value.GetRawText();
                break;
            case JsonValueKind.String:
                raw = value.GetString() ?? string.Empty;
                break;
            default:
                throw new InvalidOperationException(
                    "oauth2: cannot parse json: json: cannot unmarshal " + KindName(value)
                    + " into Go value of type json.Number");
        }

        if (!GoCast.TryParseInt64(raw, out var seconds))
        {
            throw new InvalidOperationException(
                "oauth2: cannot parse json: strconv.ParseInt: parsing "
                + GoCast.GoQuote(raw) + ": invalid syntax");
        }

        // expirationTime clamps values above math.MaxInt32.
        return Math.Min(seconds, int.MaxValue);
    }

    /// <summary>Port of <c>url.ParseQuery</c> (net/url/url.go:1026-1075).</summary>
    private static Dictionary<string, List<string>> ParseForm(string query, out string? error)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        error = null;

        while (query.Length > 0)
        {
            string part;
            var amp = query.IndexOf('&', StringComparison.Ordinal);
            if (amp < 0)
            {
                part = query;
                query = string.Empty;
            }
            else
            {
                part = query[..amp];
                query = query[(amp + 1)..];
            }

            if (part.Contains(';', StringComparison.Ordinal))
            {
                error ??= "invalid semicolon separator in query";
                continue;
            }

            if (part.Length == 0)
            {
                continue;
            }

            var equals = part.IndexOf('=', StringComparison.Ordinal);
            var rawKey = equals < 0 ? part : part[..equals];
            var rawValue = equals < 0 ? string.Empty : part[(equals + 1)..];

            if (!TryQueryUnescape(rawKey, out var key, out var keyError))
            {
                error ??= keyError;
                continue;
            }

            if (!TryQueryUnescape(rawValue, out var value, out var valueError))
            {
                error ??= valueError;
                continue;
            }

            if (!values.TryGetValue(key, out var list))
            {
                list = [];
                values[key] = list;
            }

            list.Add(value);
        }

        return values;
    }

    /// <summary>Port of <c>url.QueryUnescape</c> (net/url/url.go:131-165).</summary>
    private static bool TryQueryUnescape(string input, out string value, out string? error)
    {
        value = string.Empty;
        error = null;

        var bytes = Encoding.UTF8.GetBytes(input);
        var output = new byte[bytes.Length];
        var length = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            switch (bytes[index])
            {
                case (byte)'+':
                    output[length++] = (byte)' ';
                    break;
                case (byte)'%':
                    if (index + 2 >= bytes.Length
                        || !IsHex(bytes[index + 1])
                        || !IsHex(bytes[index + 2]))
                    {
                        var end = Math.Min(index + 3, bytes.Length);
                        error = "invalid URL escape "
                            + GoCast.GoQuote(Encoding.UTF8.GetString(bytes, index, end - index));
                        return false;
                    }

                    output[length++] = (byte)((HexValue(bytes[index + 1]) << 4) | HexValue(bytes[index + 2]));
                    index += 2;
                    break;
                default:
                    output[length++] = bytes[index];
                    break;
            }
        }

        value = Encoding.UTF8.GetString(output, 0, length);
        return true;
    }

    private static bool IsHex(byte value)
        => value is >= (byte)'0' and <= (byte)'9'
            or >= (byte)'a' and <= (byte)'f'
            or >= (byte)'A' and <= (byte)'F';

    private static int HexValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        _ => value - 'A' + 10,
    };

    private static string GetFirst(
        Dictionary<string, List<string>> values,
        string key)
        => values.TryGetValue(key, out var list) && list.Count > 0 ? list[0] : string.Empty;

    /// <summary>Go's encoding/json field lookup prefers the last duplicate key.</summary>
    public static bool TryGetProperty(JsonElement root, string foldedName, out JsonElement value)
    {
        var found = false;
        value = default;
        foreach (var property in root.EnumerateObject())
        {
            if (FoldAscii(property.Name) == foldedName)
            {
                value = property.Value;
                found = true;
            }
        }

        return found;
    }

    public static string KindName(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        _ => "null",
    };

    private static string FoldAscii(string name)
    {
        var chars = name.ToCharArray();
        for (var index = 0; index < chars.Length; index++)
        {
            if (chars[index] is >= 'A' and <= 'Z')
            {
                chars[index] = (char)(chars[index] + ('a' - 'A'));
            }
        }

        return new string(chars);
    }

    private static byte[] ReadLimited(HttpResponseMessage response)
    {
        var stream = response.Content is null ? Stream.Null : response.Content.ReadAsStream();
        using (stream)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (buffer.Length < MaxResponseBody)
            {
                var read = stream.Read(
                    chunk,
                    0,
                    (int)Math.Min(chunk.Length, MaxResponseBody - buffer.Length));
                if (read <= 0)
                {
                    break;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private static string StatusLine(HttpResponseMessage response)
    {
        var reason = response.ReasonPhrase ?? string.Empty;
        return ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)
            + (reason.Length > 0 ? " " + reason : string.Empty);
    }

    private static string ContentType(HttpResponseMessage response)
    {
        if (response.Content is not null
            && response.Content.Headers.TryGetValues("Content-Type", out var values))
        {
            foreach (var value in values)
            {
                return value;
            }
        }

        return string.Empty;
    }

    private readonly record struct DecodedToken
    {
        public string AccessToken { get; init; }

        public string TokenType { get; init; }

        public string RefreshToken { get; init; }

        public long ExpiresIn { get; init; }

        public string ErrorCode { get; init; }

        public string ErrorDescription { get; init; }

        public string ErrorUri { get; init; }
    }
}

/// <summary>The status line, content type and body of a token endpoint response.</summary>
internal readonly record struct TokenHttpResult(
    int Status,
    string StatusLine,
    string ContentType,
    byte[] Body);
