using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Management;

namespace AxonHub.Api.Storage;

/// <summary>
/// Port of DataStorageService.createGcsFs (data_storage.go:445-469) and the GCS
/// branch of SaveData / SaveDataFromReader / DeleteData / LoadData.
/// </summary>
/// <remarks>
/// The reference composes cloud.google.com/go/storage with
/// spf13/afero/gcsfs and roots the filesystem at the bucket
/// (<c>afero.NewBasePathFs(gcsfs, BucketName)</c>). Google.Cloud.Storage.V1 is
/// not available to the port, so this backend speaks the same JSON API
/// directly: OAuth2 token acquisition from the service_account /
/// authorized_user credential JSON, media uploads for writes, object reads,
/// deletes, and a delimited listing for the retention sweep (C199).
/// </remarks>
public sealed class GcsDataStorageBackend : IDataStorageBackend
{
    internal const string StorageScope = "https://www.googleapis.com/auth/devstorage.full_control";

    private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";
    private const string DefaultStorageBaseUrl = "https://storage.googleapis.com";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _bucket;
    private readonly TimeProvider _time;
    private readonly GcsCredential _credential;

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiry;
    private bool _disposed;

    public GcsDataStorageBackend(GcsStorageSettings settings)
        : this(
            settings,
            new HttpClient(),
            DefaultStorageBaseUrl,
            TimeProvider.System)
    {
    }

    /// <summary>Test seam: alternate storage endpoint and clock.</summary>
    internal GcsDataStorageBackend(
        GcsStorageSettings settings,
        HttpClient http,
        string storageBaseUrl,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(storageBaseUrl);
        ArgumentNullException.ThrowIfNull(time);

        _http = http;
        _baseUrl = storageBaseUrl.TrimEnd('/');
        _bucket = settings.BucketName;
        _time = time;
        _credential = GcsCredential.Parse(settings.Credential);
    }

    public async Task WriteAsync(
        string key,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        using var content = new ByteArrayContent(data.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await SendStorageAsync(
                HttpMethod.Post,
                UploadUrl(key),
                content,
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "failed to write file: "
                + await DescribeAsync(response, cancellationToken).ConfigureAwait(false)
                + ", key: " + key);
        }
    }

    public async Task<(string Key, long BytesWritten)> WriteFromStreamAsync(
        string key,
        Stream source,
        CancellationToken cancellationToken)
    {
        await using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        await WriteAsync(key, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
        return (key, buffer.Length);
    }

    public async Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await SendStorageAsync(
                HttpMethod.Get,
                ObjectUrl(key) + "?alt=media",
                content: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // cloud.google.com/go/storage's ErrObjectNotExist.
            throw new FileNotFoundException("storage: object doesn't exist");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "failed to read file: "
                + await DescribeAsync(response, cancellationToken).ConfigureAwait(false));
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        using var response = await SendStorageAsync(
                HttpMethod.Delete,
                ObjectUrl(key),
                content: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new FileNotFoundException("storage: object doesn't exist");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "failed to remove file: "
                + await DescribeAsync(response, cancellationToken).ConfigureAwait(false)
                + ", key: " + key);
        }
    }

    public async Task<IReadOnlyList<DataStorageEntry>> ListAsync(
        CancellationToken cancellationToken)
    {
        var entries = new List<DataStorageEntry>();
        string? pageToken = null;

        do
        {
            var url = _baseUrl + "/storage/v1/b/" + Uri.EscapeDataString(_bucket)
                + "/o?delimiter=%2F&maxResults=1000";
            if (!string.IsNullOrEmpty(pageToken))
            {
                url += "&pageToken=" + Uri.EscapeDataString(pageToken);
            }

            using var response = await SendStorageAsync(
                    HttpMethod.Get,
                    url,
                    content: null,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    "failed to list data storage files: "
                    + await DescribeAsync(response, cancellationToken).ConfigureAwait(false));
            }

            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken)
                    .ConfigureAwait(false));

            if (document.RootElement.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var nameValue)
                        ? nameValue.GetString()
                        : null;
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    var modified = DateTimeOffset.MinValue;
                    if (item.TryGetProperty("updated", out var updated)
                        && updated.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(
                            updated.GetString(),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var parsed))
                    {
                        modified = parsed;
                    }

                    var baseName = name.EndsWith('/') ? name[..^1] : name;
                    baseName = baseName[(baseName.LastIndexOf('/') + 1)..];
                    entries.Add(new DataStorageEntry(baseName, modified));
                }
            }

            pageToken = document.RootElement.TryGetProperty("nextPageToken", out var next)
                && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return entries;
    }

    /// <summary>
    /// storage JSON API media upload:
    /// POST /upload/storage/v1/b/{bucket}/o?uploadType=media&amp;name={object}.
    /// </summary>
    private string UploadUrl(string key)
        => _baseUrl + "/upload/storage/v1/b/" + Uri.EscapeDataString(_bucket)
            + "/o?uploadType=media&name=" + Uri.EscapeDataString(key);

    /// <summary>GET/DELETE /storage/v1/b/{bucket}/o/{object}.</summary>
    private string ObjectUrl(string key)
        => _baseUrl + "/storage/v1/b/" + Uri.EscapeDataString(_bucket)
            + "/o/" + Uri.EscapeDataString(key);

    private async Task<HttpResponseMessage> SendStorageAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendOnceAsync(method, url, content, token, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        // The token may have been revoked or expired early; refresh once.
        response.Dispose();
        _accessToken = null;
        token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        return await SendOnceAsync(method, url, content, token, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        string token,
        CancellationToken cancellationToken)
    {
        // The request is deliberately not disposed: HttpRequestMessage.Dispose
        // disposes Content, which the caller owns so the 401 retry can resend it.
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await _http.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var cached = _accessToken;
        if (cached is not null && now < _accessTokenExpiry - TimeSpan.FromSeconds(60))
        {
            return cached;
        }

        await _tokenLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _time.GetUtcNow();
            if (_accessToken is not null && now < _accessTokenExpiry - TimeSpan.FromSeconds(60))
            {
                return _accessToken;
            }

            var (token, expiresIn) = await FetchTokenAsync(cancellationToken)
                .ConfigureAwait(false);
            _accessToken = token;
            _accessTokenExpiry = now + TimeSpan.FromSeconds(expiresIn);
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<(string Token, int ExpiresIn)> FetchTokenAsync(
        CancellationToken cancellationToken)
    {
        using var form = new FormUrlEncodedContent(_credential.TokenForm(_time.GetUtcNow()));
        using var response = await _http.PostAsync(
                _credential.TokenUri,
                form,
                cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "failed to fetch GCP access token: "
                + (int)response.StatusCode + " " + response.ReasonPhrase
                + ": " + body);
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var token = root.TryGetProperty("access_token", out var accessToken)
            && accessToken.ValueKind == JsonValueKind.String
            ? accessToken.GetString()
            : null;
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException(
                "failed to fetch GCP access token: missing access_token in response");
        }

        var expiresIn = root.TryGetProperty("expires_in", out var expires)
            && expires.TryGetInt32(out var seconds)
            ? seconds
            : 3600;

        return (token, expiresIn);
    }

    private static async Task<string> DescribeAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        string message;
        try
        {
            message = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            using var document = JsonDocument.Parse(message);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var errorMessage)
                && errorMessage.ValueKind == JsonValueKind.String)
            {
                message = errorMessage.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            message = response.ReasonPhrase ?? string.Empty;
        }

        return "googleapi: Error " + (int)response.StatusCode + ": " + message
            + ", " + Reason((int)response.StatusCode);
    }

    private static string Reason(int status) => status switch
    {
        400 => "badRequest",
        401 => "unauthorized",
        403 => "forbidden",
        404 => "notFound",
        409 => "conflict",
        412 => "conditionNotMet",
        429 => "rateLimitExceeded",
        500 => "internalError",
        502 => "backendError",
        503 => "backendError",
        504 => "backendError",
        _ => "unknown",
    };

    /// <summary>
    /// google.CredentialsFromJSON accepts the two credential shapes this
    /// product stores: service_account (RS256 JWT bearer grant) and
    /// authorized_user (refresh token grant).
    /// </summary>
    private abstract class GcsCredential
    {
        public abstract string TokenUri { get; }

        public abstract IEnumerable<KeyValuePair<string, string>> TokenForm(
            DateTimeOffset now);

        public static GcsCredential Parse(string credential)
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(credential);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "failed to parse GCP credentials: " + ex.Message,
                    ex);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException(
                        "failed to parse GCP credentials: credentials must be a JSON object");
                }

                var type = ReadString(root, "type");
                switch (type)
                {
                    case "service_account":
                        {
                            // Cloud's CredentialsFromJSON parses the key eagerly, so
                            // an unusable PEM fails construction, not the first request.
                            var privateKey = Require(root, "private_key");
                            ValidatePrivateKey(privateKey);
                            return new ServiceAccountCredential(
                                Require(root, "client_email"),
                                privateKey,
                                ReadString(root, "token_uri") ?? DefaultTokenUri);
                        }

                    case "authorized_user":
                        return new AuthorizedUserCredential(
                            Require(root, "client_id"),
                            Require(root, "client_secret"),
                            Require(root, "refresh_token"),
                            ReadString(root, "token_uri") ?? DefaultTokenUri);
                    default:
                        throw new InvalidOperationException(
                            "failed to parse GCP credentials: unsupported credential type \""
                            + type + "\"");
                }
            }
        }

        private static string Require(JsonElement root, string name)
        {
            var value = ReadString(root, name);
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException(
                    "failed to parse GCP credentials: missing \"" + name + "\" field");
            }

            return value;
        }

        private static void ValidatePrivateKey(string pem)
        {
            try
            {
                using var rsa = RSA.Create();
                rsa.ImportFromPem(pem);
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                throw new InvalidOperationException(
                    "failed to parse GCP credentials: " + ex.Message,
                    ex);
            }
        }
    }

    private sealed class ServiceAccountCredential(
        string clientEmail,
        string privateKeyPem,
        string tokenUri) : GcsCredential
    {
        public override string TokenUri { get; } = tokenUri;

        public override IEnumerable<KeyValuePair<string, string>> TokenForm(
            DateTimeOffset now)
        {
            var assertion = CreateAssertion(now);
            yield return new(
                "grant_type",
                "urn:ietf:params:oauth:grant-type:jwt-bearer");
            yield return new("assertion", assertion);
        }

        private string CreateAssertion(DateTimeOffset now)
        {
            var issuedAt = now.ToUnixTimeSeconds();
            var header = Base64Url("{\"alg\":\"RS256\",\"typ\":\"JWT\"}");
            var claims = Base64Url(
                "{\"iss\":" + Quote(clientEmail)
                + ",\"scope\":" + Quote(StorageScope)
                + ",\"aud\":" + Quote(TokenUri)
                + ",\"exp\":" + (issuedAt + 3600).ToString(CultureInfo.InvariantCulture)
                + ",\"iat\":" + issuedAt.ToString(CultureInfo.InvariantCulture)
                + "}");

            var signingInput = header + "." + claims;
            using var rsa = RSA.Create();
            try
            {
                rsa.ImportFromPem(privateKeyPem);
            }
            catch (Exception ex) when (ex is ArgumentException or CryptographicException)
            {
                throw new InvalidOperationException(
                    "failed to parse GCP credentials: " + ex.Message,
                    ex);
            }

            var signature = rsa.SignData(
                Encoding.UTF8.GetBytes(signingInput),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            return signingInput + "." + Base64Url(signature);
        }

        private static string Quote(string value)
            => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

        private static string Base64Url(string value)
            => Base64Url(Encoding.UTF8.GetBytes(value));

        private static string Base64Url(byte[] value)
            => Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
    }

    private sealed class AuthorizedUserCredential(
        string clientId,
        string clientSecret,
        string refreshToken,
        string tokenUri) : GcsCredential
    {
        public override string TokenUri { get; } = tokenUri;

        public override IEnumerable<KeyValuePair<string, string>> TokenForm(
            DateTimeOffset now)
        {
            yield return new("grant_type", "refresh_token");
            yield return new("client_id", clientId);
            yield return new("client_secret", clientSecret);
            yield return new("refresh_token", refreshToken);
        }
    }

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tokenLock.Dispose();
        _http.Dispose();
    }
}
