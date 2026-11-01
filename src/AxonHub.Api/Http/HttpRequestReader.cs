using System.IO.Compression;
using System.Text;
using AxonHub.Llm.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using ZstdSharp;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of httpclient.ReadHTTPRequest (llm/httpclient/utils.go:24-146): reads the
/// client body with the reference's 64 MB cap, decompresses gzip/deflate/zstd
/// bodies, and canonicalizes headers exactly like Go's textproto.
/// </summary>
/// <remarks>
/// Body-level compression errors keep Go's wrapping prefixes, but the .NET
/// inflaters do not expose Go's failure detail (deflate offsets, gzip trailer
/// checks, truncated-stream detection); see C015.
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

    /// <summary>
    /// Port of decodeRequestBody: gzip/x-gzip, deflate and zstd; <c>identity</c>
    /// is a pass-through. Encoding matching is case-insensitive and trimmed.
    /// </summary>
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
                var decoded = DecodeGzip(body);
                DropEncodingHeaders(headers);
                return decoded;

            case "deflate":
                var inflated = DecodeDeflate(body);
                DropEncodingHeaders(headers);
                return inflated;

            case "zstd":
                var decompressed = DecodeZstd(body);
                DropEncodingHeaders(headers);
                return decompressed;

            default:
                throw new InvalidOperationException("unsupported content encoding: " + contentEncoding);
        }
    }

    private static void DropEncodingHeaders(Dictionary<string, List<string>> headers)
    {
        headers.Remove("Content-Encoding");
        headers.Remove("Content-Length");
    }

    /// <summary>
    /// Port of the gzip branch. Go validates the member header with
    /// compress/gzip before touching the deflate stream, so the header is
    /// checked here with Go's wording; .NET's GZipStream validates far less
    /// (and rejects reserved FLG bits Go ignores).
    /// </summary>
    private static byte[] DecodeGzip(byte[] body)
    {
        ValidateGzipHeader(body);

        // Go's gzip.Reader ignores the reserved FLG bits (0xE0); .NET rejects
        // them, so clear them on a copy before decompressing.
        var payload = body;
        if ((body[3] & 0xE0) != 0)
        {
            payload = (byte[])body.Clone();
            payload[3] &= 0x1F;
        }

        try
        {
            using var gzip = new GZipStream(new MemoryStream(payload), CompressionMode.Decompress);
            return ReadLimited(gzip, MaxRequestBodySize);
        }
        catch (RequestBodyTooLargeException ex)
        {
            throw new InvalidOperationException("failed to decompress gzip body: " + ex.Message, ex);
        }
        catch (InvalidDataException ex)
        {
            // .NET folds framing/trailer failures into one message and does not
            // expose the input offset Go reports (C015).
            throw new InvalidOperationException("failed to decompress gzip body: flate: corrupt input", ex);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidOperationException("failed to decompress gzip body: unexpected EOF", ex);
        }
    }

    /// <summary>
    /// Port of compress/gzip's readHeader (Go 1.26): magic and method, optional
    /// FEXTRA/FNAME/FCOMMENT fields, and the FHCRC check. Reserved FLG bits are
    /// deliberately not rejected — Go ignores them.
    /// </summary>
    private static void ValidateGzipHeader(byte[] body)
    {
        // io.ReadFull returns EOF for an empty stream and ErrUnexpectedEOF for
        // a short one; only the optional fields below go through noEOF.
        if (body.Length == 0)
        {
            throw GzipReaderError("EOF");
        }

        if (body.Length < 10)
        {
            throw GzipReaderError("unexpected EOF");
        }

        if (body[0] != 0x1F || body[1] != 0x8B || body[2] != 8)
        {
            throw GzipReaderError("gzip: invalid header");
        }

        var flags = body[3];
        var offset = 10;

        if ((flags & 0x04) != 0)
        {
            if (body.Length - offset < 2)
            {
                throw GzipReaderError("unexpected EOF");
            }

            var extraLength = body[offset] | (body[offset + 1] << 8);
            offset += 2;
            if (body.Length - offset < extraLength)
            {
                throw GzipReaderError("unexpected EOF");
            }

            offset += extraLength;
        }

        if ((flags & 0x08) != 0)
        {
            offset = SkipGzipString(body, offset);
        }

        if ((flags & 0x10) != 0)
        {
            offset = SkipGzipString(body, offset);
        }

        if ((flags & 0x02) != 0)
        {
            if (body.Length - offset < 2)
            {
                throw GzipReaderError("unexpected EOF");
            }

            var expected = (ushort)(body[offset] | (body[offset + 1] << 8));
            if (expected != (ushort)Crc32(body.AsSpan(0, offset)))
            {
                throw GzipReaderError("gzip: invalid header");
            }
        }
    }

    /// <summary>Port of gzip.Reader.readString: at most 512 bytes, NUL terminated.</summary>
    private static int SkipGzipString(byte[] body, int offset)
    {
        for (var index = 0; ; index++)
        {
            if (index >= 512)
            {
                throw GzipReaderError("gzip: invalid header");
            }

            if (offset >= body.Length)
            {
                throw GzipReaderError("unexpected EOF");
            }

            if (body[offset++] == 0)
            {
                return offset;
            }
        }
    }

    private static InvalidOperationException GzipReaderError(string message)
        => new("failed to create gzip reader: " + message);

    /// <summary>
    /// Port of decodeZlibOrFlate: zlib framing is chosen by the RFC 1950
    /// header exactly like zlib.NewReader (including FDICT), with raw DEFLATE
    /// as the fallback. Read-time errors never fall back.
    /// </summary>
    private static byte[] DecodeDeflate(byte[] body)
    {
        try
        {
            if (HasValidZlibHeader(body))
            {
                using var zlib = new ZLibStream(new MemoryStream(body), CompressionMode.Decompress);
                return ReadLimited(zlib, MaxRequestBodySize);
            }

            using var deflate = new DeflateStream(new MemoryStream(body), CompressionMode.Decompress);
            return ReadLimited(deflate, MaxRequestBodySize);
        }
        catch (RequestBodyTooLargeException ex)
        {
            throw new InvalidOperationException("failed to decompress deflate body: " + ex.Message, ex);
        }
        catch (InvalidDataException ex)
        {
            // .NET folds zlib checksum and deflate framing errors into one
            // message; Go separates "zlib: invalid checksum" from
            // "flate: corrupt input before offset N" (C015).
            throw new InvalidOperationException("failed to decompress deflate body: flate: corrupt input", ex);
        }
    }

    /// <summary>zlib.NewReader's header checks: CM=deflate, CINFO&lt;=7, FCHECK, no FDICT.</summary>
    private static bool HasValidZlibHeader(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2)
        {
            return false;
        }

        var cmf = body[0];
        var flg = body[1];
        return (cmf & 0x0F) == 8
            && cmf >> 4 <= 7
            && (((cmf << 8) | flg) % 31) == 0
            && (flg & 0x20) == 0;
    }

    /// <summary>Port of the zstd branch (klauspost/compress in the reference).</summary>
    private static byte[] DecodeZstd(byte[] body)
    {
        try
        {
            using var zstd = new DecompressionStream(new MemoryStream(body));
            return ReadLimited(zstd, MaxRequestBodySize);
        }
        catch (RequestBodyTooLargeException ex)
        {
            throw new InvalidOperationException("failed to decode zstd compressed body: " + ex.Message, ex);
        }
        catch (ZstdException ex)
        {
            throw new InvalidOperationException("failed to decode zstd compressed body: " + ex.Message, ex);
        }
        catch (EndOfStreamException ex)
        {
            // klauspost's reader reports io.ErrUnexpectedEOF for a cut frame.
            throw new InvalidOperationException("failed to decode zstd compressed body: unexpected EOF", ex);
        }
    }

    /// <summary>IEEE CRC-32 over the gzip header bytes (compress/gzip's FHCRC digest).</summary>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1u)));
            }
        }

        return ~crc;
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
            throw new RequestBodyTooLargeException();
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

    /// <summary>Sentinel so the decoders can wrap the cap with their own prefix.</summary>
    private sealed class RequestBodyTooLargeException() : InvalidOperationException("request body too large");

    /// <summary>
    /// Canonicalizes header names like textproto.CanonicalMIMEHeaderKey. Trace
    /// extraction needs the same shape the wire request carries, because the
    /// OpenCode and Codex header readers look keys up by canonical name.
    /// </summary>
    public static Dictionary<string, List<string>> CanonicalizeHeaders(IHeaderDictionary headers)
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
