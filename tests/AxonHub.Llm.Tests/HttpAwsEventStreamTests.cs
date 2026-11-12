using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Locks the Bedrock EventStream decoder ported from llm/bedrock/stream.go and
/// the aws-sdk-go-v2 eventstream framing it embeds: chunk events, exception and
/// error messages, checksum/length failures, end-of-input handling and the
/// Content-Type routing in <see cref="HttpExecutor.DoStream"/>.
/// </summary>
public sealed class HttpAwsEventStreamTests
{
    private const string MessageType = ":message-type";
    private const string EventType = ":event-type";
    private const string ExceptionType = ":exception-type";
    private const string ErrorCode = ":error-code";
    private const string ErrorMessage = ":error-message";

    [Fact]
    public void EventMessageEmitsTheDecodedChunk()
    {
        var frame = Frame(
            Chunk("""{"type":"message_start","message":{"id":"test-id","content":"Hello, World!"}}"""),
            HeaderSpec.String(MessageType, "event"),
            HeaderSpec.String(EventType, "chunk"));
        var source = new TrackingStream(frame);

        using var stream = new HttpAwsEventStream(source);

        Assert.True(stream.Next(), stream.Err?.ToString());
        Assert.Null(stream.Err);
        Assert.Equal("message_start", stream.Current!.Type);
        Assert.Equal(
            """{"type":"message_start","message":{"id":"test-id","content":"Hello, World!"}}""",
            Encoding.UTF8.GetString(stream.Current.Data));

        Assert.False(stream.Next());
        Assert.Null(stream.Err);
        Assert.True(source.Disposed);
    }

    [Fact]
    public void ExceptionMessageSurfacesTheExceptionTypeAndMessage()
    {
        var frame = Frame(
            Encoding.UTF8.GetBytes("""{"__type":"ValidationException","message":"Invalid input parameter"}"""),
            HeaderSpec.String(MessageType, "exception"),
            HeaderSpec.String(ExceptionType, "ValidationException"));
        var source = new TrackingStream(frame);

        using var stream = new HttpAwsEventStream(source);

        Assert.False(stream.Next());
        Assert.NotNull(stream.Err);
        Assert.Contains("ValidationException", stream.Err.Message, StringComparison.Ordinal);
        Assert.Contains("Invalid input parameter", stream.Err.Message, StringComparison.Ordinal);
        Assert.Equal("received exception ValidationException: Invalid input parameter", stream.Err.Message);

        // Event-level errors do not close the reference's reader.
        Assert.False(source.Disposed);
    }

    [Fact]
    public void ExceptionWithoutAMessageFallsBackToUnknownError()
    {
        var frame = Frame(
            Encoding.UTF8.GetBytes("""{"__type":"ValidationException"}"""),
            HeaderSpec.String(MessageType, "exception"),
            HeaderSpec.String(ExceptionType, "ValidationException"));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.False(stream.Next());
        Assert.Equal("received exception ValidationException: UnknownError", stream.Err!.Message);
    }

    [Fact]
    public void ExceptionWithoutAHeaderFallsBackToThePayloadCode()
    {
        var frame = Frame(
            Encoding.UTF8.GetBytes("""{"code":"Throttled","message":"slow down"}"""),
            HeaderSpec.String(MessageType, "exception"),
            HeaderSpec.String(ExceptionType, string.Empty));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.False(stream.Next());
        Assert.Equal("received exception Throttled: slow down", stream.Err!.Message);
    }

    [Fact]
    public void MalformedExceptionPayloadIsWrapped()
    {
        var frame = Frame(
            Encoding.UTF8.GetBytes("invalid json"),
            HeaderSpec.String(MessageType, "exception"),
            HeaderSpec.String(ExceptionType, "ValidationException"));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.False(stream.Next());
        Assert.StartsWith(
            "received exception ValidationException: parsing exception payload failed: ",
            stream.Err!.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ErrorMessageUsesTheErrorHeaders()
    {
        var frame = Frame(
            [],
            HeaderSpec.String(MessageType, "error"),
            HeaderSpec.String(ErrorCode, "InternalError"),
            HeaderSpec.String(ErrorMessage, "Internal server error occurred"));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.False(stream.Next());
        Assert.Equal(
            "received error InternalError: Internal server error occurred",
            stream.Err!.Message);
    }

    [Fact]
    public void ErrorMessageWithoutHeadersUsesUnknownErrorForBothFields()
    {
        var frame = Frame([], HeaderSpec.String(MessageType, "error"));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.False(stream.Next());
        Assert.Equal("received error UnknownError: UnknownError", stream.Err!.Message);
    }

    [Fact]
    public void MissingMessageTypeHeaderFails()
    {
        var frame = Frame([], HeaderSpec.String(EventType, "chunk"));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.False(stream.Next());
        Assert.Equal(":message-type event header not present", stream.Err!.Message);
    }

    [Fact]
    public void MissingEventTypeHeaderFails()
    {
        var frame = Frame([], HeaderSpec.String(MessageType, "event"));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.False(stream.Next());
        Assert.Equal(":event-type event header not present", stream.Err!.Message);
    }

    [Fact]
    public void UnknownMessageTypeFails()
    {
        var frame = Frame([], HeaderSpec.String(MessageType, "mystery"));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.False(stream.Next());
        Assert.Equal("unknown message type: mystery", stream.Err!.Message);
    }

    [Fact]
    public void InvalidChunkJsonSurfacesTheParserFailure()
    {
        var frame = Frame(
            Encoding.UTF8.GetBytes("invalid json"),
            HeaderSpec.String(MessageType, "event"),
            HeaderSpec.String(EventType, "chunk"));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.False(stream.Next());
        Assert.IsType<JsonException>(stream.Err);
    }

    [Fact]
    public void InvalidBase64SurfacesTheDecoderFailure()
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { bytes = "invalid-base64!", p = "test" });
        var frame = Frame(
            payload,
            HeaderSpec.String(MessageType, "event"),
            HeaderSpec.String(EventType, "chunk"));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.False(stream.Next());
        Assert.IsType<FormatException>(stream.Err);
        Assert.StartsWith("illegal base64 data at input byte ", stream.Err.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleEventsAreReadInOrderThenTheStreamEndsCleanly()
    {
        var first = Frame(
            Chunk("""{"type":"message_start","message":{"id":"1"}}"""),
            HeaderSpec.String(MessageType, "event"),
            HeaderSpec.String(EventType, "chunk"));
        var second = Frame(
            Chunk("""{"type":"content_block_delta","delta":{"text":"Hello"}}"""),
            HeaderSpec.String(MessageType, "event"),
            HeaderSpec.String(EventType, "chunk"));
        var source = new TrackingStream([.. first, .. second]);

        using var stream = new HttpAwsEventStream(source);

        Assert.True(stream.Next());
        Assert.Equal("message_start", stream.Current!.Type);

        Assert.True(stream.Next());
        Assert.Equal("content_block_delta", stream.Current!.Type);

        Assert.False(stream.Next());
        Assert.Null(stream.Err);
        Assert.True(source.Disposed);
    }

    [Fact]
    public void EmptyStreamEndsCleanlyAndIsDisposed()
    {
        var source = new TrackingStream([]);
        var owner = new Tracker();

        using var stream = new HttpAwsEventStream(source, owner);

        Assert.False(stream.Next());
        Assert.Null(stream.Err);
        Assert.Null(stream.Current);
        Assert.True(source.Disposed);
        Assert.True(owner.Disposed);
    }

    [Fact]
    public void NextAfterDisposeReturnsFalse()
    {
        var source = new TrackingStream([]);
        var stream = new HttpAwsEventStream(source);

        stream.Dispose();
        stream.Dispose();

        Assert.False(stream.Next());
        Assert.Null(stream.Err);
        Assert.True(source.Disposed);
    }

    [Fact]
    public void NonChunkEventsAdvanceWithoutReplacingCurrent()
    {
        var other = Frame(
            [],
            HeaderSpec.String(MessageType, "event"),
            HeaderSpec.String(EventType, "other"));
        var chunk = Frame(
            Chunk("""{"type":"message_start"}"""),
            HeaderSpec.String(MessageType, "event"),
            HeaderSpec.String(EventType, "chunk"));

        using var stream = new HttpAwsEventStream(new MemoryStream([.. other, .. chunk]));

        Assert.True(stream.Next());
        Assert.Null(stream.Current);

        Assert.True(stream.Next());
        Assert.Equal("message_start", stream.Current!.Type);
    }

    [Fact]
    public void DuplicateHeadersKeepTheLastValue()
    {
        var frame = Frame(
            Chunk("""{"type":"message_start"}"""),
            HeaderSpec.String(MessageType, "mystery"),
            HeaderSpec.String(MessageType, "event"),
            HeaderSpec.String(EventType, "chunk"));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.True(stream.Next());
        Assert.Equal("message_start", stream.Current!.Type);
    }

    [Fact]
    public void AllHeaderValueTypesAreDecoded()
    {
        var uuid = new byte[16];
        uuid[0] = 0xAB;

        var frame = Frame(
            Chunk("""{"type":"message_start"}"""),
            HeaderSpec.String(MessageType, "event"),
            HeaderSpec.String(EventType, "chunk"),
            HeaderSpec.Of("b-true", 0, []),
            HeaderSpec.Of("b-false", 1, []),
            HeaderSpec.Of("i8", 2, [0xFF]),
            HeaderSpec.Of("i16", 3, [0xFF, 0xFE]),
            HeaderSpec.Of("i32", 4, [0xFF, 0xFF, 0xFF, 0xFD]),
            HeaderSpec.Of("i64", 5, [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFC]),
            HeaderSpec.Bytes("raw", [1, 2, 3]),
            HeaderSpec.Of("ts", 8, [0, 0, 0, 0, 0, 0, 0, 1]),
            HeaderSpec.Of("uuid", 9, uuid));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.True(stream.Next());
        Assert.Null(stream.Err);
        Assert.Equal("message_start", stream.Current!.Type);
    }

    [Fact]
    public void UnknownHeaderValueTypeFails()
    {
        var frame = Frame(
            [],
            HeaderSpec.Of(MessageType, 10, []));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.False(stream.Next());
        Assert.Equal(
            "unable to decode header of unknown value type 10",
            stream.Err!.Message);
    }

    [Fact]
    public void APreludeLengthOfZeroFailsBeforeTheChecksum()
    {
        var frame = Frame([], HeaderSpec.String(MessageType, "event"));

        // ValidateLens runs right after the two length words.
        BinaryPrimitives.WriteUInt32BigEndian(frame, 0);

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.False(stream.Next());
        Assert.Equal("message prelude length invalid, 16/0, <nil>", stream.Err!.Message);
    }

    [Fact]
    public void APreludeChecksumMismatchFails()
    {
        var frame = Frame([], HeaderSpec.String(MessageType, "event"));

        frame[11] ^= 0xFF;

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.False(stream.Next());
        Assert.Equal("message checksum mismatch", stream.Err!.Message);
    }

    [Fact]
    public void AMessageChecksumMismatchFails()
    {
        var frame = Frame(
            Chunk("""{"type":"message_start"}"""),
            HeaderSpec.String(MessageType, "event"),
            HeaderSpec.String(EventType, "chunk"));

        // Corrupt the payload after the CRCs were computed.
        frame[frame.Length - 5] ^= 0xFF;

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.False(stream.Next());
        Assert.Equal("message checksum mismatch", stream.Err!.Message);
    }

    [Fact]
    public void AChecksumFailureDisposesTheReader()
    {
        var frame = Frame([], HeaderSpec.String(MessageType, "event"));
        frame[11] ^= 0xFF;
        var source = new TrackingStream(frame);

        using var stream = new HttpAwsEventStream(source);

        Assert.False(stream.Next());
        Assert.True(source.Disposed);
    }

    [Fact]
    public void AFourByteTailIsACleanEnd()
    {
        // The reference reads the prelude as two independent uint32s, so a
        // stream that ends after the first word is io.EOF, not a decode error.
        var frame = Frame([], HeaderSpec.String(MessageType, "event"));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame[..4]));

        Assert.False(stream.Next());
        Assert.Null(stream.Err);
    }

    [Fact]
    public void APartialPreludeIsUnexpectedEof()
    {
        var frame = Frame([], HeaderSpec.String(MessageType, "event"));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame[..2]));

        Assert.False(stream.Next());
        Assert.Equal("unexpected EOF", stream.Err!.Message);
    }

    [Fact]
    public void AFrameCutInsideTheHeadersStillEndsCleanly()
    {
        // Header decoding treats EOF at a header boundary as the end of the
        // header section; the payload and checksum reads then hit EOF too.
        var frame = Frame(
            Chunk("""{"type":"message_start"}"""),
            HeaderSpec.String(MessageType, "event"),
            HeaderSpec.String(EventType, "chunk"));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame[..12]));

        Assert.False(stream.Next());
        Assert.Null(stream.Err);
    }

    [Theory]
    [InlineData("""{"type":"message_start"}""", "message_start")]
    [InlineData("""{"type":123}""", "123")]
    [InlineData("""{"type":1e3}""", "1000")]
    [InlineData("""{"type":1.5}""", "1.5")]
    [InlineData("""{"type":true}""", "true")]
    [InlineData("""{"type":false}""", "false")]
    [InlineData("""{"type":null}""", "")]
    [InlineData("""{"other":1}""", "")]
    [InlineData("""{"type":{"a":1}}""", """{"a":1}""")]
    [InlineData("""{"type":["a"]}""", """["a"]""")]
    [InlineData("not json", "")]
    public void ChunkTypeFollowsGjsonStringSemantics(string inner, string expected)
    {
        var frame = Frame(
            Chunk(inner),
            HeaderSpec.String(MessageType, "event"),
            HeaderSpec.String(EventType, "chunk"));

        using var stream = new HttpAwsEventStream(new MemoryStream(frame));

        Assert.True(stream.Next(), stream.Err?.ToString());
        Assert.Equal(expected, stream.Current!.Type);
    }

    [Fact]
    public void DoStreamSelectsTheEventStreamDecoderForTheBedrockContentType()
    {
        var frame = Frame(
            Chunk("""{"type":"message_start"}"""),
            HeaderSpec.String(MessageType, "event"),
            HeaderSpec.String(EventType, "chunk"));

        using var client = new HttpClient(new StaticBodyHandler(
            "application/vnd.amazon.eventstream",
            frame));
        var executor = new HttpExecutor(client);
        var request = new HttpRequestData
        {
            Method = "POST",
            Url = "https://bedrock-runtime.us-east-1.amazonaws.com/model/anthropic.claude/invoke-with-response-stream",
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("""{"stream":true}"""),
        };

        using var stream = executor.DoStream(CancellationToken.None, request);

        Assert.True(stream.Next());
        Assert.Equal("message_start", stream.Current!.Type);
        Assert.False(stream.Next());
        Assert.Null(stream.Err);
    }

    [Fact]
    public void DoStreamAcceptsTheCharsetVariantOfTheBedrockContentType()
    {
        var frame = Frame(
            Chunk("""{"type":"content_block_delta"}"""),
            HeaderSpec.String(MessageType, "event"),
            HeaderSpec.String(EventType, "chunk"));

        using var client = new HttpClient(new StaticBodyHandler(
            "application/vnd.amazon.eventstream; charset=utf-8",
            frame));
        var executor = new HttpExecutor(client);
        var request = new HttpRequestData
        {
            Method = "POST",
            Url = "https://bedrock-runtime.us-east-1.amazonaws.com/model/anthropic.claude/invoke-with-response-stream",
            ContentType = "application/json",
            Body = Encoding.UTF8.GetBytes("""{"stream":true}"""),
        };

        using var stream = executor.DoStream(CancellationToken.None, request);

        Assert.True(stream.Next());
        Assert.Equal("content_block_delta", stream.Current!.Type);
    }

    private static byte[] Chunk(string innerJson)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            bytes = Convert.ToBase64String(Encoding.UTF8.GetBytes(innerJson)),
            p = "test",
        });

    private static byte[] Frame(byte[] payload, params HeaderSpec[] headers)
    {
        using var headerStream = new MemoryStream();
        foreach (var header in headers)
        {
            var name = Encoding.ASCII.GetBytes(header.Name);
            headerStream.WriteByte((byte)name.Length);
            headerStream.Write(name);
            headerStream.WriteByte(header.Type);
            headerStream.Write(header.Data);
        }

        var headerBytes = headerStream.ToArray();
        var total = 16 + headerBytes.Length + payload.Length;
        var frame = new byte[total];

        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)total);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(4), (uint)headerBytes.Length);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8), Crc32(frame.AsSpan(0, 8)));
        headerBytes.CopyTo(frame.AsSpan(12));
        payload.CopyTo(frame.AsSpan(12 + headerBytes.Length));
        BinaryPrimitives.WriteUInt32BigEndian(
            frame.AsSpan(total - 4),
            Crc32(frame.AsSpan(0, total - 4)));

        return frame;
    }

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

    private readonly record struct HeaderSpec(string Name, byte Type, byte[] Data)
    {
        public static HeaderSpec String(string name, string value)
            => new(name, 7, LengthPrefixed(Encoding.UTF8.GetBytes(value)));

        public static HeaderSpec Bytes(string name, byte[] value)
            => new(name, 6, LengthPrefixed(value));

        public static HeaderSpec Of(string name, byte type, byte[] data)
            => new(name, type, data);

        private static byte[] LengthPrefixed(byte[] data)
        {
            var result = new byte[2 + data.Length];
            BinaryPrimitives.WriteUInt16BigEndian(result, (ushort)data.Length);
            data.CopyTo(result, 2);
            return result;
        }
    }

    private sealed class TrackingStream(byte[] data) : MemoryStream(data)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class Tracker : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class StaticBodyHandler(string contentType, byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(body);
            content.Headers.TryAddWithoutValidation("Content-Type", contentType);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
            });
        }
    }
}
