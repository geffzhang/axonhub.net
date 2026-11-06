using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using AxonHub.Api.Management;

namespace AxonHub.Api.Storage;

/// <summary>
/// Port of DataStorageService.createWebDAVFs (data_storage.go:471-502) and the
/// WebDAV branch of SaveData / SaveDataFromReader / DeleteData / LoadData.
/// </summary>
/// <remarks>
/// The reference composes studio-b12/gowebdav 0.12.0 with looplj/afero-webdav:
/// a 10-minute timeout, Basic auth, optional TLS verification bypass, and a
/// BasePathFs rooted at <c>Path</c> (falling back to <c>Directory</c>) with one
/// leading slash stripped for Synology/NAS compatibility. webdavfs buffers
/// write streams in memory and issues a single PUT on Close, so the port also
/// buffers before uploading.
/// </remarks>
public sealed class WebDavDataStorageBackend : IDataStorageBackend
{
    private readonly HttpClient _http;
    private readonly string _rootUrl;

    public WebDavDataStorageBackend(DataStorageSettings settings, HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(http);

        var webDav = settings.WebDav
            ?? throw new InvalidOperationException("webdav settings not configured");

        _http = http;

        var baseUrl = (webDav.Url ?? string.Empty).TrimEnd('/');
        var path = webDav.Path;
        if (string.IsNullOrEmpty(path) && settings.Directory is not null)
        {
            // createWebDAVFs: cfg.Path wins, otherwise settings.Directory.
            path = settings.Directory;
        }

        // createWebDAVFs: strings.TrimPrefix(path, "/") — exactly one slash.
        path = path.TrimStart('/');

        _rootUrl = path.Length == 0
            ? baseUrl
            : baseUrl + "/" + EscapePath(path);
    }

    /// <summary>
    /// gowebdav.NewClient + SetTimeout(time.Minute*10) + the optional
    /// InsecureSkipVerify transport.
    /// </summary>
    public static HttpClient CreateHttpClient(WebDavStorageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var handler = new HttpClientHandler();
        if (settings.InsecureSkipTls)
        {
            // gowebdav.SetTransport(&http.Transport{TLSClientConfig:
            // &tls.Config{InsecureSkipVerify: true}}); explicitly user-opted.
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(10),
        };

        if (!string.IsNullOrEmpty(settings.Username) || !string.IsNullOrEmpty(settings.Password))
        {
            var raw = settings.Username + ":" + settings.Password;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)));
        }

        return client;
    }

    public async Task WriteAsync(
        string key,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeKey(key);
        await CreateDirectoryAsync(normalized, cancellationToken).ConfigureAwait(false);

        using var content = new ByteArrayContent(data.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await SendAsync(
                HttpMethod.Put,
                ObjectUrl(normalized),
                content,
                cancellationToken)
            .ConfigureAwait(false);

        if (!IsSuccess(response.StatusCode))
        {
            throw new InvalidOperationException(
                "failed to write file: " + await DescribeAsync(response).ConfigureAwait(false)
                + ", key: " + normalized);
        }
    }

    public async Task<(string Key, long BytesWritten)> WriteFromStreamAsync(
        string key,
        Stream source,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeKey(key);
        await CreateDirectoryAsync(normalized, cancellationToken).ConfigureAwait(false);

        // webdavfs keeps an O_WRONLY buffer and flushes it with one PUT.
        await using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

        using var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var response = await SendAsync(
                HttpMethod.Put,
                ObjectUrl(normalized),
                content,
                cancellationToken)
            .ConfigureAwait(false);

        if (!IsSuccess(response.StatusCode))
        {
            throw new InvalidOperationException(
                "failed to write file: " + await DescribeAsync(response).ConfigureAwait(false)
                + ", key: " + normalized);
        }

        return (normalized, buffer.Length);
    }

    public async Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var normalized = NormalizeKey(key);

        using var response = await SendAsync(
                HttpMethod.Get,
                ObjectUrl(normalized),
                content: null,
                cancellationToken)
            .ConfigureAwait(false);

        if (!IsSuccess(response.StatusCode))
        {
            throw new InvalidOperationException(
                "failed to read file: " + await DescribeAsync(response).ConfigureAwait(false));
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var normalized = NormalizeKey(key);

        using var response = await SendAsync(
                HttpMethod.Delete,
                ObjectUrl(normalized),
                content: null,
                cancellationToken)
            .ConfigureAwait(false);

        // gowebdav.Remove reports every non-2xx status; it does not translate
        // 404 into os.ErrNotExist the way the fs branch does.
        if (!IsSuccess(response.StatusCode))
        {
            throw new InvalidOperationException(
                "failed to remove file: " + await DescribeAsync(response).ConfigureAwait(false)
                + ", key: " + normalized);
        }
    }

    public async Task<IReadOnlyList<DataStorageEntry>> ListAsync(
        CancellationToken cancellationToken)
    {
        const string Body =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<d:propfind xmlns:d=\"DAV:\"><d:prop>"
            + "<d:displayname/><d:getlastmodified/><d:resourcetype/><d:getcontentlength/>"
            + "</d:prop></d:propfind>";

        using var content = new StringContent(Body, Encoding.UTF8, "application/xml");
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), _rootUrl + "/")
        {
            Content = content,
        };
        request.Headers.Add("Depth", "1");

        using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!IsSuccess(response.StatusCode) && response.StatusCode != HttpStatusCode.MultiStatus)
        {
            throw new InvalidOperationException(
                "failed to list data storage files: "
                + await DescribeAsync(response).ConfigureAwait(false));
        }

        var xml = await response.Content.ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
        return ParseMultistatus(xml, _rootUrl);
    }

    /// <summary>
    /// SaveData / SaveDataFromReader both call
    /// <c>mkdirAll(fs, filepath.Dir(key))</c> for WebDAV; BasePathFs resolves
    /// "." to the configured base path.
    /// </summary>
    private async Task CreateDirectoryAsync(string key, CancellationToken cancellationToken)
    {
        var segments = new List<string>();
        if (_rootUrl.Length > 0)
        {
            // Everything below the configured base path is created top-down.
            segments.Add(_rootUrl);
        }

        var separator = key.LastIndexOf('/');
        if (separator > 0)
        {
            var url = _rootUrl;
            foreach (var segment in key[..separator].Split('/'))
            {
                if (segment.Length == 0)
                {
                    continue;
                }

                url = (url.Length == 0 ? string.Empty : url + "/") + EscapePath(segment);
                segments.Add(url);
            }
        }

        foreach (var url in segments)
        {
            using var response = await SendAsync(
                    new HttpMethod("MKCOL"),
                    url + "/",
                    content: null,
                    cancellationToken)
                .ConfigureAwait(false);

            // MKCOL on an existing collection is 405 on most servers; some
            // return 301/409. gowebdav treats those as "already there".
            if (IsSuccess(response.StatusCode)
                || response.StatusCode is HttpStatusCode.MethodNotAllowed
                    or HttpStatusCode.Conflict
                    or HttpStatusCode.MovedPermanently
                    or HttpStatusCode.Found)
            {
                continue;
            }

            throw new InvalidOperationException(
                "failed to create directory: "
                + await DescribeAsync(response).ConfigureAwait(false)
                + ", key: " + key);
        }
    }

    private static IReadOnlyList<DataStorageEntry> ParseMultistatus(string xml, string rootUrl)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }

        XNamespace dav = "DAV:";
        var entries = new List<DataStorageEntry>();
        var rootPath = (Uri.TryCreate(rootUrl, UriKind.Absolute, out var root)
                ? root.AbsolutePath
                : rootUrl)
            .TrimEnd('/');

        foreach (var response in document.Descendants(dav + "response"))
        {
            var href = response.Element(dav + "href")?.Value;
            if (string.IsNullOrEmpty(href))
            {
                continue;
            }

            var path = Uri.TryCreate(href, UriKind.Absolute, out var absolute)
                ? absolute.AbsolutePath
                : href;
            path = Uri.UnescapeDataString(path).TrimEnd('/');

            // gowebdav's ReadDir drops the collection itself.
            if (string.Equals(path, rootPath, StringComparison.Ordinal))
            {
                continue;
            }

            var name = path[(path.LastIndexOf('/') + 1)..];
            if (name.Length == 0)
            {
                continue;
            }

            var modified = DateTimeOffset.MinValue;
            var lastModified = response
                .Descendants(dav + "getlastmodified")
                .FirstOrDefault()?.Value;
            if (!string.IsNullOrEmpty(lastModified)
                && DateTimeOffset.TryParseExact(
                    lastModified,
                    "R",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                modified = parsed;
            }

            entries.Add(new DataStorageEntry(name, modified));
        }

        return entries;
    }

    private string ObjectUrl(string normalizedKey)
        => _rootUrl + "/" + EscapePath(normalizedKey);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string url,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        => _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);

    private static async Task<string> DescribeAsync(HttpResponseMessage response)
    {
        var reason = string.IsNullOrEmpty(response.ReasonPhrase)
            ? response.StatusCode.ToString()
            : response.ReasonPhrase;
        return (int)response.StatusCode + " " + reason;
    }

    /// <summary>strings.TrimPrefix(key, "/") — exactly one leading slash.</summary>
    private static string NormalizeKey(string key)
        => key.StartsWith('/') ? key[1..] : key;

    private static bool IsSuccess(HttpStatusCode status)
        => (int)status is >= 200 and <= 299;

    private static string EscapePath(string path)
        => string.Join(
            "/",
            path.Split('/').Select(Uri.EscapeDataString));

    public void Dispose() => _http.Dispose();
}
