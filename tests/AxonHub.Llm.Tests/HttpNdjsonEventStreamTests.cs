using System.Net;
using System.Text;
using AxonHub.Llm.Http;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Locks the NDJSON decoder registered by llm/transformer/ollama/decoder.go: one
/// JSON object per line, no SSE framing, CRLF tolerated, a final unterminated
/// line still emitted, and read failures surfaced through <c>Err</c>.
/// </summary>
public sealed class HttpNdjsonEventStreamTests
{
    private static readonly string[] TwoObjects = ["{}", "{\"done\":true}"];

    private static readonly string[] EmptyLineBetween = ["a", string.Empty, "b"];

    private static readonly string[] TwoCrlfObjects = ["{\"a\":1}", "{\"b\":2}"];

    private static readonly string[] OnePaddedObject = [" {\"a\":1} "];

    private static readonly string[] OneObjectThenFragment = ["{\"a\":1}", "{\"b\":2}"];

    private static readonly string[] OneObject = ["{\"a\":1}"];

    private static readonly string[] NdjsonFrames = ["{\"message\":{\"content\":\"Hel\"}}", "{\"done\":true}"];

    [Fact]
    public void EmitsOneEventPerLine()
    {
        var events = ReadAll("{}\n{\"done\":true}\n");

        Assert.Null(events.Err);
        Assert.Equal(TwoObjects, events.Data);
    }

    [Fact]
    public void EmitsAnEmptyLineAsAnEmptyEvent()
    {
        // ReadBytes('\n') returns "\n"; the reference trims it to "" and still
        // emits the event.
        var events = ReadAll("a\n\nb\n");

        Assert.Equal(EmptyLineBetween, events.Data);
    }

    [Fact]
    public void StripsOneCarriageReturnBeforeTheNewline()
    {
        var events = ReadAll("{\"a\":1}\r\n{\"b\":2}\r\n");

        Assert.Equal(TwoCrlfObjects, events.Data);
    }

    [Fact]
    public void KeepsTheDataVerbatimWithoutTrimming()
    {
        // Only \n and one \r are removed; surrounding spaces stay.
        var events = ReadAll(" {\"a\":1} \n");

        Assert.Equal(OnePaddedObject, events.Data);
    }

    [Fact]
    public void EmitsATrailingFragmentWithoutANewline()
    {
        var events = ReadAll("{\"a\":1}\n{\"b\":2}");

        Assert.Equal(OneObjectThenFragment, events.Data);
    }

    [Fact]
    public void DoesNotEmitAnExtraEventWhenTheStreamEndsWithANewline()
    {
        var events = ReadAll("{\"a\":1}\n");

        Assert.Equal(OneObject, events.Data);
    }

    [Fact]
    public void EmptyStreamHasNoEvents()
    {
        var events = ReadAll(string.Empty);

        Assert.Empty(events.Data);
        Assert.Null(events.Err);
    }

    [Fact]
    public void ReportsReadErrorsThroughErr()
    {
        using var stream = new HttpNdjsonEventStream(new ThrowingStream("{\"a\":1}\n"));

        Assert.True(stream.Next());
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(stream.Current!.Data));
        Assert.False(stream.Next());
        Assert.IsType<IOException>(stream.Err);
    }

    [Fact]
    public void DoStreamSelectsTheNdjsonDecoderForApplicationXNdjson()
    {
        // client.go:408-426 routes the registered content type to the Ollama
        // decoder instead of treating the body as SSE.
        var handler = new StaticBodyHandler(
            "application/x-ndjson",
            "{\"message\":{\"content\":\"Hel\"}}\n{\"done\":true}\n");

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);
        var request = new HttpRequestData
        {
            Method = "POST",
            Url = "http://localhost:11434/api/chat",
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("""{"model":"qwen3"}"""),
        };

        using var stream = executor.DoStream(CancellationToken.None, request);
        var data = new List<string>();
        while (stream.Next())
        {
            data.Add(Encoding.UTF8.GetString(stream.Current!.Data));
        }

        Assert.Null(stream.Err);
        Assert.Equal(NdjsonFrames, data);
    }

    [Fact]
    public void DoStreamAcceptsTheCharsetVariantOfTheNdjsonContentType()
    {
        var handler = new StaticBodyHandler(
            "application/x-ndjson; charset=utf-8",
            "{\"done\":true}\n");

        using var client = new HttpClient(handler);
        var executor = new HttpExecutor(client);
        var request = new HttpRequestData
        {
            Method = "POST",
            Url = "http://localhost:11434/api/chat",
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("""{"model":"qwen3"}"""),
        };

        using var stream = executor.DoStream(CancellationToken.None, request);

        Assert.True(stream.Next());
        Assert.Equal("{\"done\":true}", Encoding.UTF8.GetString(stream.Current!.Data));
        Assert.False(stream.Next());
    }

    private static (List<string> Data, Exception? Err) ReadAll(string body)
    {
        using var bytes = new MemoryStream(Encoding.UTF8.GetBytes(body));
        using var stream = new HttpNdjsonEventStream(bytes);

        var data = new List<string>();
        while (stream.Next())
        {
            data.Add(Encoding.UTF8.GetString(stream.Current!.Data));
        }

        return (data, stream.Err);
    }

    private sealed class ThrowingStream(string firstChunk) : Stream
    {
        private bool _served;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_served)
            {
                throw new IOException("connection reset");
            }

            _served = true;
            var payload = Encoding.UTF8.GetBytes(firstChunk);
            Array.Copy(payload, 0, buffer, offset, payload.Length);
            return payload.Length;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class StaticBodyHandler(string contentType, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }
}
