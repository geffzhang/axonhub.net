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
}
