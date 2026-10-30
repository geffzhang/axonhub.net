using System.IO.Compression;
using System.Text;
using AxonHub.Api.Http;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port checks for ReadHTTPRequest: canonical headers, URL reconstruction,
/// client IP precedence, gzip/deflate decoding and unsupported encodings.
/// </summary>
public sealed class HttpRequestReaderTests
{
    private static DefaultHttpContext NewContext(byte[] body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.test");
        context.Request.Path = "/v1/chat/completions";
        context.Request.Body = new MemoryStream(body);
        return context;
    }

    [Fact]
    public async Task ReadsBodyCanonicalizesHeadersAndDerivesClientIp()
    {
        var payload = Encoding.UTF8.GetBytes("{\"model\":\"gpt-4o\"}");
        var context = NewContext(payload);
        context.Request.ContentType = "application/json";
        context.Request.Headers["x-custom-header"] = "value";
        context.Request.Headers["X-Forwarded-For"] = "203.0.113.7, 10.0.0.1";

        var request = await HttpRequestReader.ReadAsync(context.Request);

        Assert.Equal("POST", request.Method);
        Assert.Equal("http://gateway.test/v1/chat/completions", request.Url);
        Assert.Equal("/v1/chat/completions", request.Path);
        Assert.Equal(payload, request.Body);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("203.0.113.7", request.ClientIp);
        Assert.Equal("value", Assert.Single(request.Headers!["X-Custom-Header"]));
        Assert.Equal("application/json", Assert.Single(request.Headers["Content-Type"]));
    }

    [Fact]
    public async Task DecompressesGzipBodyAndDropsEncodingHeaders()
    {
        var payload = Encoding.UTF8.GetBytes("{\"model\":\"gpt-4o\"}");
        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
            {
                gzip.Write(payload);
            }

            compressed = buffer.ToArray();
        }

        var context = NewContext(compressed);
        context.Request.ContentType = "application/json";
        context.Request.Headers["Content-Encoding"] = "gzip";
        context.Request.Headers["Content-Length"] = compressed.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var request = await HttpRequestReader.ReadAsync(context.Request);

        Assert.Equal(payload, request.Body);
        Assert.False(request.Headers!.ContainsKey("Content-Encoding"));
        Assert.False(request.Headers.ContainsKey("Content-Length"));
    }

    [Fact]
    public async Task DecompressesZlibFramedDeflate()
    {
        var payload = Encoding.UTF8.GetBytes("{\"model\":\"gpt-4o\"}");
        byte[] compressed;
        using (var buffer = new MemoryStream())
        {
            using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(payload);
            }

            compressed = buffer.ToArray();
        }

        var context = NewContext(compressed);
        context.Request.ContentType = "application/json";
        context.Request.Headers["Content-Encoding"] = "deflate";

        var request = await HttpRequestReader.ReadAsync(context.Request);

        Assert.Equal(payload, request.Body);
    }

    [Fact]
    public async Task RejectsUnsupportedContentEncoding()
    {
        var context = NewContext(Encoding.UTF8.GetBytes("{}"));
        context.Request.ContentType = "application/json";
        context.Request.Headers["Content-Encoding"] = "br";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HttpRequestReader.ReadAsync(context.Request));

        Assert.Equal("unsupported content encoding: br", ex.Message);
    }

    [Fact]
    public async Task DecompressesGoProducedZstdBodyAndDropsEncodingHeaders()
    {
        // Produced by the reference's klauspost/compress zstd writer for
        // {"model":"gpt-4o"} (E:\tmp\goenc probe).
        var compressed = Convert.FromBase64String("KLUv/QQAkQAAeyJtb2RlbCI6ImdwdC00byJ9G6WJGA==");
        var context = NewContext(compressed);
        context.Request.ContentType = "application/json";
        context.Request.Headers["Content-Encoding"] = "zstd";
        context.Request.Headers["Content-Length"] = compressed.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var request = await HttpRequestReader.ReadAsync(context.Request);

        Assert.Equal("{\"model\":\"gpt-4o\"}", Encoding.UTF8.GetString(request.Body!));
        Assert.False(request.Headers!.ContainsKey("Content-Encoding"));
        Assert.False(request.Headers.ContainsKey("Content-Length"));
    }

    [Fact]
    public async Task ReportsInvalidZstdBodyWithGoPrefix()
    {
        var context = NewContext(Encoding.ASCII.GetBytes("not-zstd"));
        context.Request.ContentType = "application/json";
        context.Request.Headers["Content-Encoding"] = "zstd";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HttpRequestReader.ReadAsync(context.Request));

        Assert.StartsWith("failed to decode zstd compressed body: ", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportsGzipHeaderErrorsWithGoWording()
    {
        // Messages captured from the reference (Go 1.26 compress/gzip):
        // an empty stream is EOF, a short header is unexpected EOF, and a
        // complete header with bad magic/method is gzip: invalid header.
        await AssertGzipHeaderErrorAsync("garbage", "failed to create gzip reader: unexpected EOF");
        await AssertGzipHeaderErrorAsync("0123456789", "failed to create gzip reader: gzip: invalid header");
        await AssertGzipHeaderErrorAsync("x", "failed to create gzip reader: unexpected EOF");
    }

    [Fact]
    public async Task ReportsCorruptGzipBodyWithGoPrefix()
    {
        var compressed = Gzip(Encoding.UTF8.GetBytes("{\"model\":\"gpt-4o\"}"));
        compressed[compressed.Length / 2] ^= 0xFF;

        var context = NewContext(compressed);
        context.Request.ContentType = "application/json";
        context.Request.Headers["Content-Encoding"] = "gzip";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HttpRequestReader.ReadAsync(context.Request));

        Assert.Equal("failed to decompress gzip body: flate: corrupt input", ex.Message);
    }

    [Fact]
    public async Task IgnoresReservedGzipFlagsLikeGo()
    {
        var compressed = Gzip(Encoding.UTF8.GetBytes("{\"model\":\"gpt-4o\"}"));
        compressed[3] |= 0x20;

        var context = NewContext(compressed);
        context.Request.ContentType = "application/json";
        context.Request.Headers["Content-Encoding"] = "gzip";

        var request = await HttpRequestReader.ReadAsync(context.Request);

        Assert.Equal("{\"model\":\"gpt-4o\"}", Encoding.UTF8.GetString(request.Body!));
    }

    [Fact]
    public async Task SkipsGzipNameFieldBeforeDecompressing()
    {
        // FNAME is set and the NUL-terminated name is inserted right after the
        // fixed header, as Go's gzip reader expects.
        var plain = Gzip(Encoding.UTF8.GetBytes("{\"model\":\"gpt-4o\"}"));
        var named = new byte[plain.Length + 6];
        Array.Copy(plain, named, 10);
        named[3] |= 0x08;
        Encoding.ASCII.GetBytes("x.txt").CopyTo(named, 10);
        named[15] = 0;
        Array.Copy(plain, 10, named, 16, plain.Length - 10);

        var context = NewContext(named);
        context.Request.ContentType = "application/json";
        context.Request.Headers["Content-Encoding"] = "gzip";

        var request = await HttpRequestReader.ReadAsync(context.Request);

        Assert.Equal("{\"model\":\"gpt-4o\"}", Encoding.UTF8.GetString(request.Body!));
    }

    [Fact]
    public async Task DecodesGoProducedRawDeflateWhenZlibHeaderIsInvalid()
    {
        // Produced by the reference's compress/flate writer for
        // {"model":"gpt-4o"}; its first two bytes are not a valid zlib header.
        var raw = Convert.FromBase64String("qlbKzU9JzVGyUkovKNE1yVeqBQQAAP//");
        var context = NewContext(raw);
        context.Request.ContentType = "application/json";
        context.Request.Headers["Content-Encoding"] = "deflate";

        var request = await HttpRequestReader.ReadAsync(context.Request);

        Assert.Equal("{\"model\":\"gpt-4o\"}", Encoding.UTF8.GetString(request.Body!));
    }

    [Fact]
    public async Task MatchesEncodingNameCaseInsensitively()
    {
        var compressed = Gzip(Encoding.UTF8.GetBytes("{\"model\":\"gpt-4o\"}"));
        var context = NewContext(compressed);
        context.Request.ContentType = "application/json";
        context.Request.Headers["Content-Encoding"] = " GZIP ";

        var request = await HttpRequestReader.ReadAsync(context.Request);

        Assert.Equal("{\"model\":\"gpt-4o\"}", Encoding.UTF8.GetString(request.Body!));
    }

    private static async Task AssertGzipHeaderErrorAsync(string body, string expected)
    {
        var context = NewContext(Encoding.ASCII.GetBytes(body));
        context.Request.ContentType = "application/json";
        context.Request.Headers["Content-Encoding"] = "gzip";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HttpRequestReader.ReadAsync(context.Request));

        Assert.Equal(expected, ex.Message);
    }

    private static byte[] Gzip(byte[] payload)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(payload);
        }

        return buffer.ToArray();
    }
}
