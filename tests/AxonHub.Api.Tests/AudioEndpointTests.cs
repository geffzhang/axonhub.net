using System.Globalization;
using System.Text;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Http;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Endpoint glue tests for the OpenAI audio slice: the speech route's binary
/// stream probe (openai.go:437-465), the strconv.ParseFloat fidelity of
/// <see cref="GoCast.TryParseFloat"/> used by the temperature field, and the
/// raw-byte writer (chat.go:668-757). The transformers themselves are locked
/// one-for-one in AxonHub.Llm.Tests.
/// </summary>
public sealed class AudioEndpointTests
{
    // ---------------------------------------------------------------- speech route probe

    [Fact]
    public void ShouldUseBinarySpeechStreamNilRequest()
    {
        var ex = Assert.Throws<TransformerException>(() => SpeechEndpoint.ShouldUseBinarySpeechStream(null));
        Assert.Contains("http request is nil", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldUseBinarySpeechStreamEmptyBody()
    {
        var ex = Assert.Throws<TransformerException>(
            () => SpeechEndpoint.ShouldUseBinarySpeechStream(Request([])));
        Assert.Contains("request body is empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ShouldUseBinarySpeechStreamNonJsonContentTypeSkipsDecode()
    {
        Assert.False(SpeechEndpoint.ShouldUseBinarySpeechStream(
            Request(Encoding.UTF8.GetBytes("{\"stream_format\":\"audio\"}"), "text/plain")));
    }

    [Theory]
    [InlineData("{\"stream_format\":\"audio\"}", true)]
    [InlineData("{\"stream_format\":\"sse\"}", false)]
    [InlineData("{\"stream_format\":\"\"}", false)]
    [InlineData("{}", false)]
    // trim + lower are applied before the sse comparison (openai.go:458-462).
    [InlineData("{\"stream_format\":\"SSE\"}", false)]
    [InlineData("{\"stream_format\":\" audio \"}", true)]
    [InlineData("{\"stream_format\":\"AUDIO\"}", true)]
    [InlineData("null", false)]
    public void ShouldUseBinarySpeechStreamFormatTable(string body, bool expected)
    {
        Assert.Equal(expected, SpeechEndpoint.ShouldUseBinarySpeechStream(Request(Encoding.UTF8.GetBytes(body))));
    }

    [Theory]
    [InlineData("{not json", "failed to decode speech request")]
    [InlineData("[]", "cannot unmarshal array into Go value of type api.speechRouteRequestBody")]
    [InlineData("\"sse\"", "cannot unmarshal string into Go value of type api.speechRouteRequestBody")]
    [InlineData(
        "{\"stream_format\":123}",
        "cannot unmarshal number into Go struct field speechRouteRequestBody.stream_format of type string")]
    public void ShouldUseBinarySpeechStreamDecodeErrors(string body, string expectedSubstring)
    {
        var ex = Assert.Throws<TransformerException>(
            () => SpeechEndpoint.ShouldUseBinarySpeechStream(Request(Encoding.UTF8.GetBytes(body))));
        Assert.Contains(expectedSubstring, ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- strconv.ParseFloat fidelity

    /// <summary>
    /// Lock for <see cref="GoCast.TryParseFloat"/> against strconv.ParseFloat
    /// ground truth: specials, hex floats (atofHex with the negative-bias
    /// exponent and zero-mantissa short-circuit), underscores, range errors.
    /// </summary>
    [Theory]
    [InlineData("1.5", true, "1.5")]
    [InlineData("0.2", true, "0.2")]
    [InlineData("1e3", true, "1000")]
    [InlineData(".5", true, "0.5")]
    [InlineData("5.", true, "5")]
    [InlineData("1.5e2", true, "150")]
    [InlineData("0e10000", true, "0")]
    [InlineData("1e-400", true, "0")]
    [InlineData("1_000", true, "1000")]
    [InlineData("0x1p4", true, "16")]
    [InlineData("0x1.8p1", true, "3")]
    [InlineData("0x_1p2", true, "4")]
    [InlineData("nan", true, "NaN")]
    [InlineData("NaN", true, "NaN")]
    [InlineData("inf", true, "Inf")]
    [InlineData("Infinity", true, "Inf")]
    [InlineData("+inf", true, "Inf")]
    [InlineData("-Inf", true, "-Inf")]
    [InlineData("+nan", false, "")]
    [InlineData("-nan", false, "")]
    [InlineData("infinit", false, "")]
    [InlineData("1e400", false, "")]
    [InlineData("1e309", false, "")]
    [InlineData("_1", false, "")]
    [InlineData("1__0", false, "")]
    [InlineData("1._5", false, "")]
    [InlineData("1_.5", false, "")]
    [InlineData("0x", false, "")]
    [InlineData("1p3", false, "")]
    [InlineData("0x1", false, "")]
    [InlineData("1e", false, "")]
    [InlineData("1e+", false, "")]
    [InlineData("abc", false, "")]
    [InlineData("", false, "")]
    [InlineData(" 1", false, "")]
    [InlineData("1 ", false, "")]
    public void ParseFloatFidelity(string input, bool valid, string expectedText)
    {
        var parsed = GoCast.TryParseFloat(input, out var value);
        Assert.Equal(valid, parsed);
        if (!valid)
        {
            return;
        }

        switch (expectedText)
        {
            case "NaN":
                Assert.True(double.IsNaN(value));
                break;
            case "Inf":
                Assert.True(double.IsPositiveInfinity(value));
                break;
            case "-Inf":
                Assert.True(double.IsNegativeInfinity(value));
                break;
            default:
                Assert.Equal(double.Parse(expectedText, CultureInfo.InvariantCulture), value);
                break;
        }
    }

    // ---------------------------------------------------------------- binary stream writer

    private static (DefaultHttpContext Context, MemoryStream Body) NewContext()
    {
        var context = new DefaultHttpContext();
        var body = new MemoryStream();
        context.Response.Body = body;
        return (context, body);
    }

    [Fact]
    public async Task BinaryStreamWriterWritesRawChunksAndFirstChunkContentType()
    {
        var (context, body) = NewContext();
        var stream = new ListStream<StreamEvent>(
        [
            new StreamEvent { Type = "audio/mpeg", Data = [0x01, 0x02] },
            new StreamEvent { Type = "audio/mpeg", Data = [0x03] },
            new StreamEvent { Type = AuthTypes.BinaryStreamDoneEventType },
        ]);

        await BinaryStreamWriter.WriteAsync(context, stream, expectedChoices: 1, CancellationToken.None, transformStreamError: null);

        Assert.Equal("audio/mpeg", context.Response.ContentType);
        Assert.Equal("no-cache", context.Response.Headers["Cache-Control"].ToString());
        Assert.Equal("keep-alive", context.Response.Headers["Connection"].ToString());
        Assert.Equal("*", context.Response.Headers["Access-Control-Allow-Origin"].ToString());
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, body.ToArray());
    }

    [Fact]
    public async Task BinaryStreamWriterDefaultsContentTypeForUntypedFirstChunk()
    {
        var (context, body) = NewContext();
        var stream = new ListStream<StreamEvent>([new StreamEvent { Data = [0xAA] }]);

        await BinaryStreamWriter.WriteAsync(context, stream, expectedChoices: 1, CancellationToken.None, transformStreamError: null);

        Assert.Equal(BinaryStreamWriter.DefaultContentType, context.Response.ContentType);
        Assert.Equal(new byte[] { 0xAA }, body.ToArray());
    }

    [Fact]
    public async Task BinaryStreamWriterSwallowsBinaryDoneAndEmptyChunks()
    {
        var (context, body) = NewContext();
        var stream = new ListStream<StreamEvent>(
        [
            new StreamEvent { Type = AuthTypes.BinaryStreamDoneEventType },
            new StreamEvent { Type = "audio/mpeg", Data = [] },
        ]);

        await BinaryStreamWriter.WriteAsync(context, stream, expectedChoices: 1, CancellationToken.None, transformStreamError: null);

        Assert.Empty(body.ToArray());
    }

    [Fact]
    public async Task BinaryStreamWriterWritesClassifiedErrorBeforeHeaders()
    {
        var (context, body) = NewContext();
        var stream = new ListStream<StreamEvent>([], terminalError: new IOException("connection reset"));

        await BinaryStreamWriter.WriteAsync(context, stream, expectedChoices: 1, CancellationToken.None, transformStreamError: null);

        // streamErrorStatus classifies first: a broken upstream connection is a
        // 502 upstream_stream_interrupted, not a bare exception message.
        Assert.Equal(502, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal(
            "{\"error\":{\"code\":\"upstream_stream_interrupted\","
            + "\"message\":\"Upstream provider closed the connection before the response completed: connection reset\","
            + "\"type\":\"upstream_error\"},\"request_id\":\"\"}",
            Encoding.UTF8.GetString(body.ToArray()));
    }

    [Fact]
    public async Task BinaryStreamWriterUsesResponseErrorStatusCode()
    {
        var (context, body) = NewContext();
        var stream = new ListStream<StreamEvent>(
            [],
            terminalError: new ResponseError
            {
                StatusCode = 429,
                Detail = new ErrorDetail { Message = "rate limited", Type = "rate_limit_error" },
            });

        await BinaryStreamWriter.WriteAsync(context, stream, expectedChoices: 1, CancellationToken.None, transformStreamError: null);

        Assert.Equal(429, context.Response.StatusCode);
        Assert.Contains("rate limited", Encoding.UTF8.GetString(body.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BinaryStreamWriterErrorAfterHeadersIsLogOnly()
    {
        var (context, body) = NewContext();
        var stream = new ListStream<StreamEvent>(
            [new StreamEvent { Type = "audio/mpeg", Data = [0x01] }],
            terminalError: new IOException("connection reset"));

        await BinaryStreamWriter.WriteAsync(context, stream, expectedChoices: 1, CancellationToken.None, transformStreamError: null);

        // Headers already written: the reference only logs the error.
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(new byte[] { 0x01 }, body.ToArray());
    }

    [Fact]
    public async Task BinaryStreamWriterCancelledStreamErrorIsLogOnly()
    {
        var (context, body) = NewContext();
        var stream = new ListStream<StreamEvent>([], terminalError: new OperationCanceledException());

        await BinaryStreamWriter.WriteAsync(context, stream, expectedChoices: 1, CancellationToken.None, transformStreamError: null);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Empty(body.ToArray());
    }

    // ---------------------------------------------------------------- helpers

    private static HttpRequestData Request(byte[] body, string contentType = "application/json")
        => new()
        {
            Method = "POST",
            Url = "https://gateway.test/v1/audio/speech",
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = [contentType],
            },
            Body = body,
        };
}
