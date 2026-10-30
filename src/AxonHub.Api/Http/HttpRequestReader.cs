using System.IO.Compression;
using System.Text;
using AxonHub.Llm.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of httpclient.ReadHTTPRequest (llm/httpclient/utils.go:24-146): reads the
/// client body with the reference's 64 MB cap, decompresses gzip/deflate bodies,
/// and canonicalizes headers exactly like Go's textproto.
/// </summary>
/// <remarks>
/// zstd bodies are not decoded in this slice (C015).
/// </remarks>
public static class HttpRequestReader
{
    public const int MaxRequestBodySize = 64 * 1024 * 1024;

    public static async Task<HttpRequestData> ReadAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var headers = CanonicalizeHeaders(request.Headers);
        if (string.IsNullOrEmpty(HttpClientUtils.HeaderGet(headers, "Content-Type"))
            && !string.IsNullOrEmpty(request.ContentType))
        {
            HttpClientUtils.HeaderSet(headers, "Content-Type", request.ContentType);
        }

        byte[] body;
        try
        {
            body = await ReadLimitedAsync(request.Body, MaxRequestBodySize, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("failed to read request body: " + ex.Message, ex);
        }

        if (body.Length > 0)
        {
            body = DecodeRequestBody(body, headers);
        }

        return new HttpRequestData
        {
            Method = request.Method,
            Url = BuildUrl(request),
            Path = request.PathBase + request.Path,
            Query = ToMultiMap(request.Query),
            Headers = headers,
            ContentType = HttpClientUtils.HeaderGet(headers, "Content-Type"),
            Body = body,
            Auth = new AuthConfig(),
            RequestId = string.Empty,
            ClientIp = ClientIp(headers, request),
            UserAgent = HttpClientUtils.HeaderGet(headers, "User-Agent"),
        };
    }

    private static string BuildUrl(HttpRequest request)
        => request.Scheme + "://" + request.Host.ToUriComponent() + request.PathBase + request.Path + request.QueryString;

    /// <summary>Port of getClientIP.</summary>
    private static string ClientIp(Dictionary<string, List<string>> headers, HttpRequest request)
    {
        var forwarded = HttpClientUtils.HeaderGet(headers, "X-Forwarded-For");
        if (forwarded.Length > 0)
        {
            var comma = forwarded.IndexOf(',', StringComparison.Ordinal);
            return comma >= 0 ? forwarded[..comma].Trim() : forwarded;
        }

        var realIp = HttpClientUtils.HeaderGet(headers, "X-Real-IP");
        if (realIp.Length > 0)
        {
            return realIp;
        }

        if (request.HttpContext.Connection.RemoteIpAddress is { } address)
        {
            return address.ToString();
        }

        return string.Empty;
    }

    /// <summary>Port of decodeRequestBody: gzip and deflate; <c>identity</c> is a pass-through.</summary>
    private static byte[] DecodeRequestBody(byte[] body, Dictionary<string, List<string>> headers)
    {
        var contentEncoding = HttpClientUtils.HeaderGet(headers, "Content-Encoding");
        if (contentEncoding.Length == 0)
        {
            return body;
        }

        var encoding = contentEncoding.Trim().ToLowerInvariant();
        switch (encoding)
        {
            case "":
            case "identity":
                return body;

            case "gzip":
            case "x-gzip":
                using (var gzip = new GZipStream(new MemoryStream(body), CompressionMode.Decompress))
                {
                    var decoded = ReadLimited(gzip, MaxRequestBodySize);
                    headers.Remove("Content-Encoding");
                    headers.Remove("Content-Length");
                    return decoded;
                }

            case "deflate":
                var inflated = DecodeZlibOrDeflate(body);
                headers.Remove("Content-Encoding");
                headers.Remove("Content-Length");
                return inflated;

            default:
                throw new InvalidOperationException("unsupported content encoding: " + contentEncoding);
        }
    }

    /// <summary>Port of decodeZlibOrFlate: zlib framing first, raw DEFLATE as fallback.</summary>
    private static byte[] DecodeZlibOrDeflate(byte[] body)
    {
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(body), CompressionMode.Decompress);
            return ReadLimited(zlib, MaxRequestBodySize);
        }
        catch (InvalidDataException)
        {
            using var deflate = new DeflateStream(new MemoryStream(body), CompressionMode.Decompress);
            return ReadLimited(deflate, MaxRequestBodySize);
        }
    }

    private static byte[] ReadLimited(Stream stream, int maxSize)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (buffer.Length <= maxSize)
        {
            var toRead = (int)Math.Min(chunk.Length, maxSize + 1 - buffer.Length);
            var read = stream.Read(chunk, 0, toRead);
            if (read <= 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length > maxSize)
        {
            throw new InvalidOperationException("request body too large");
        }

        return buffer.ToArray();
    }

    private static async Task<byte[]> ReadLimitedAsync(Stream stream, int maxSize, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (buffer.Length <= maxSize)
        {
            var toRead = (int)Math.Min(chunk.Length, maxSize + 1 - buffer.Length);
            var read = await stream.ReadAsync(chunk.AsMemory(0, toRead), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length > maxSize)
        {
            throw new InvalidOperationException("request body too large");
        }

        return buffer.ToArray();
    }

    /// <summary>Canonicalizes header names like textproto.CanonicalMIMEHeaderKey.</summary>
    private static Dictionary<string, List<string>> CanonicalizeHeaders(IHeaderDictionary headers)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, values) in headers)
        {
            var canonical = HttpClientUtils.CanonicalHeaderKey(key);
            if (!result.TryGetValue(canonical, out var list))
            {
                list = [];
                result[canonical] = list;
            }

            AddValues(list, values);
        }

        return result;
    }

    private static Dictionary<string, List<string>> ToMultiMap(IQueryCollection query)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, values) in query)
        {
            result[key] = [.. values.Where(static value => value is not null).Select(static value => value!)];
        }

        return result;
    }

    private static void AddValues(List<string> list, StringValues values)
    {
        foreach (var value in values)
        {
            if (value is not null)
            {
                list.Add(value);
            }
        }

        if (values.Count == 0)
        {
            list.Add(string.Empty);
        }
    }
}
