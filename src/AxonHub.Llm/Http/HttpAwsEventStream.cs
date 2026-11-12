using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Http;

/// <summary>
/// Port of the AWS EventStream response decoder registered by
/// <c>llm/bedrock/register.go</c> (<c>NewAWSEventStreamDecoder</c>), including
/// the <c>eventstream</c> message framing it embeds (aws-sdk-go-v2
/// <c>aws/protocol/eventstream</c> v1.7.10).
/// </summary>
/// <remarks>
/// A frame is a prelude (total length, headers length, prelude CRC32), the
/// headers, the payload and a trailing message CRC32. The CRC covers every
/// byte before the trailing checksum. A chunk event carries
/// <c>{"bytes": "&lt;base64 of a JSON event&gt;"}</c>; the decoded payload is
/// yielded as one stream event whose type is the payload's <c>type</c> field.
/// <para>
/// End of input is a clean end (the reader is disposed); any other decode
/// failure is stored in <see cref="Err"/> and also disposes the reader, while
/// event-level protocol errors (missing <c>:message-type</c>, exception or
/// error messages) set <see cref="Err"/> without disposing — exactly like the
/// reference's <c>Next</c>.
/// </para>
/// </remarks>
public sealed class HttpAwsEventStream : IStream<StreamEvent>
{
    private const int MinMessageLength = 16;

    private const string MessageTypeHeader = ":message-type";
    private const string EventTypeHeader = ":event-type";
    private const string ExceptionTypeHeader = ":exception-type";
    private const string ErrorCodeHeader = ":error-code";
    private const string ErrorMessageHeader = ":error-message";

    private const string EventMessageType = "event";
    private const string ExceptionMessageType = "exception";
    private const string ErrorMessageType = "error";

    private const string UnknownError = "UnknownError";

    private static readonly JsonSerializerOptions ChunkJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions ExceptionJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Stream _stream;
    private readonly IDisposable? _owner;

    private StreamEvent? _evt;
    private Exception? _err;
    private bool _closed;

    public HttpAwsEventStream(Stream stream, IDisposable? owner = null)
    {
        _stream = stream;
        _owner = owner;
    }

    public StreamEvent? Current => _evt;

    public Exception? Err => _err;

    public bool Next()
    {
        if (_err is not null || _closed)
        {
            return false;
        }

        try
        {
            return NextCore();
        }
        catch (Exception ex)
        {
            _err = ex;
            CloseQuietly();
            return false;
        }
    }

    public void Dispose()
    {
        // Idempotent, like the reference's Close.
        if (_closed)
        {
            return;
        }

        _closed = true;
        _stream.Dispose();
        _owner?.Dispose();
    }

    // The reference's Next discards Close's error (`_ = d.Close()`).
    private void CloseQuietly()
    {
        try
        {
            Dispose();
        }
        catch (Exception)
        {
            // A failing close does not replace the decode outcome.
        }
    }

    private bool NextCore()
    {
        AwsEventStreamMessage message;
        try
        {
            message = DecodeMessage();
        }
        catch (AwsEventStreamEndOfStreamException)
        {
            // errors.Is(err, io.EOF): a clean end, no error recorded.
            CloseQuietly();
            return false;
        }
        catch (Exception ex)
        {
            _err = ex;
            CloseQuietly();
            return false;
        }

        if (!message.Headers.TryGetValue(MessageTypeHeader, out var messageType))
        {
            _err = new InvalidDataException(MessageTypeHeader + " event header not present");
            return false;
        }

        return messageType.GoString() switch
        {
            EventMessageType => HandleEventMessage(message),
            ExceptionMessageType => HandleExceptionMessage(message),
            ErrorMessageType => HandleErrorMessage(message),
            var other => Fail("unknown message type: " + other),
        };
    }

    private bool Fail(string message)
    {
        _err = new InvalidDataException(message);
        return false;
    }

    // stream.go:106-136.
    private bool HandleEventMessage(AwsEventStreamMessage message)
    {
        if (!message.Headers.TryGetValue(EventTypeHeader, out var eventType))
        {
            return Fail(EventTypeHeader + " event header not present");
        }

        if (eventType.GoString() == "chunk")
        {
            EventStreamChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<EventStreamChunk>(message.Payload, ChunkJsonOptions);
            }
            catch (JsonException ex)
            {
                _err = ex;
                return false;
            }

            byte[] decoded;
            try
            {
                decoded = DecodeBase64Std(chunk?.Bytes ?? string.Empty);
            }
            catch (FormatException ex)
            {
                _err = ex;
                return false;
            }

            _evt = new StreamEvent
            {
                Type = GjsonRootKeyProbe.GetString(decoded, "type"u8),
                Data = decoded,
            };
        }

        // A non-chunk event still advances the stream; Current keeps its
        // previous value (stream.go:135).
        return true;
    }

    // stream.go:138-177.
    private bool HandleExceptionMessage(AwsEventStreamMessage message)
    {
        if (!message.Headers.TryGetValue(ExceptionTypeHeader, out var exceptionType))
        {
            return Fail(ExceptionTypeHeader + " event header not present");
        }

        ExceptionInfo? info;
        try
        {
            info = JsonSerializer.Deserialize<ExceptionInfo>(message.Payload, ExceptionJsonOptions);
        }
        catch (JsonException ex)
        {
            _err = new InvalidDataException(
                "received exception "
                    + exceptionType.GoString()
                    + ": parsing exception payload failed: "
                    + ex.Message,
                ex);
            return false;
        }

        info ??= new ExceptionInfo();

        var errorCode = UnknownError;
        var errorMessage = errorCode;

        var headerType = exceptionType.GoString();
        if (headerType.Length > 0)
        {
            errorCode = headerType;
        }
        else if (!string.IsNullOrEmpty(info.Code))
        {
            errorCode = info.Code;
        }
        else if (!string.IsNullOrEmpty(info.Type))
        {
            errorCode = info.Type;
        }

        if (!string.IsNullOrEmpty(info.Message))
        {
            errorMessage = info.Message;
        }

        return Fail("received exception " + errorCode + ": " + errorMessage);
    }

    // stream.go:179-195.
    private bool HandleErrorMessage(AwsEventStreamMessage message)
    {
        var errorCode = UnknownError;
        var errorMessage = errorCode;

        if (message.Headers.TryGetValue(ErrorCodeHeader, out var code))
        {
            errorCode = code.GoString();
        }

        if (message.Headers.TryGetValue(ErrorMessageHeader, out var text))
        {
            errorMessage = text.GoString();
        }

        return Fail("received error " + errorCode + ": " + errorMessage);
    }

    // Port of eventstream.decodeMessage (decode.go:64-95).
    private AwsEventStreamMessage DecodeMessage()
    {
        var reader = new FrameReader(_stream);

        // Two independent decodeUint32 calls: end of input at either boundary
        // is io.EOF (a clean end), while a partial word is ErrUnexpectedEOF.
        var length = ReadUInt32(reader);
        var headersLength = ReadUInt32(reader);

        // messagePrelude.ValidateLens rejects only a zero length.
        if (length == 0)
        {
            throw new AwsEventStreamLengthException("message prelude", MinMessageLength, 0);
        }

        var preludeCrc = reader.Crc;
        var storedPreludeCrc = ReadUInt32(reader);
        if (storedPreludeCrc != preludeCrc)
        {
            throw new AwsEventStreamChecksumException();
        }

        var headers = new Dictionary<string, AwsHeaderValue>(StringComparer.Ordinal);
        if (headersLength > 0)
        {
            headers = DecodeHeaders(new LimitedReader(reader, headersLength));
        }

        var payload = Array.Empty<byte>();
        var payloadLength = unchecked(length - headersLength - MinMessageLength);
        if (payloadLength > 0)
        {
            payload = ReadPayload(new LimitedReader(reader, payloadLength));
        }

        // The trailing checksum is read from the raw stream and therefore is
        // not part of the digest.
        var expectedCrc = reader.Crc;
        var messageCrcBytes = new byte[4];
        ReadFullRaw(_stream, messageCrcBytes);
        if (BinaryPrimitives.ReadUInt32BigEndian(messageCrcBytes) != expectedCrc)
        {
            throw new AwsEventStreamChecksumException();
        }

        return new AwsEventStreamMessage(headers, payload);
    }

    // Port of decodeHeaders (header.go:64-86): EOF while reading a header name
    // means the header section ended; everything else is an error.
    private static Dictionary<string, AwsHeaderValue> DecodeHeaders(LimitedReader reader)
    {
        var headers = new Dictionary<string, AwsHeaderValue>(StringComparer.Ordinal);

        while (true)
        {
            byte nameLength;
            try
            {
                nameLength = ReadByte(reader);
            }
            catch (AwsEventStreamEndOfStreamException)
            {
                break;
            }

            var nameBytes = new byte[nameLength];
            try
            {
                ReadFull(reader, nameBytes);
            }
            catch (AwsEventStreamEndOfStreamException)
            {
                // decodeHeaderName reports io.EOF for a zero-byte name read.
                break;
            }

            var value = DecodeHeaderValue(reader);

            // Headers.Set: a duplicate name replaces the earlier value while
            // keeping its position (header.go:20-32).
            headers[Encoding.Latin1.GetString(nameBytes)] = value;
        }

        return headers;
    }

    // Port of decodeHeaderValue (header.go:105-159).
    private static AwsHeaderValue DecodeHeaderValue(LimitedReader reader)
    {
        var type = ReadByte(reader);

        return type switch
        {
            0 => AwsHeaderValue.Bool(true),
            1 => AwsHeaderValue.Bool(false),
            2 => AwsHeaderValue.Int8(unchecked((sbyte)ReadByte(reader))),
            3 => AwsHeaderValue.Int16(unchecked((short)ReadUInt16(reader))),
            4 => AwsHeaderValue.Int32(unchecked((int)ReadUInt32(reader))),
            5 => AwsHeaderValue.Int64(unchecked((long)ReadUInt64(reader))),
            6 => AwsHeaderValue.Bytes(ReadLengthPrefixed(reader)),
            7 => AwsHeaderValue.String(Encoding.UTF8.GetString(ReadLengthPrefixed(reader))),
            8 => AwsHeaderValue.Timestamp(ReadUInt64(reader)),
            9 => AwsHeaderValue.Uuid(ReadFixed(reader, 16)),
            _ => throw new InvalidDataException(
                "unable to decode header of unknown value type "
                    + type.ToString(CultureInfo.InvariantCulture)),
        };
    }

    private static byte[] ReadLengthPrefixed(LimitedReader reader)
    {
        var length = ReadUInt16(reader);
        var buffer = new byte[length];
        ReadFull(reader, buffer);
        return buffer;
    }

    private static byte[] ReadFixed(LimitedReader reader, int length)
    {
        var buffer = new byte[length];
        ReadFull(reader, buffer);
        return buffer;
    }

    // Port of decodePayload (decode.go:155-160): io.Copy stops at end of input
    // without reporting an error, so a short payload surfaces only when the
    // trailing checksum cannot be read.
    private static byte[] ReadPayload(LimitedReader reader)
    {
        using var payload = new MemoryStream();
        var buffer = new byte[32 * 1024];

        while (true)
        {
            var read = reader.Read(buffer);
            if (read == 0)
            {
                break;
            }

            payload.Write(buffer, 0, read);
        }

        return payload.ToArray();
    }

    private static byte ReadByte(IByteSource source)
    {
        Span<byte> buffer = stackalloc byte[1];
        ReadFull(source, buffer);
        return buffer[0];
    }

    private static ushort ReadUInt16(IByteSource source)
    {
        Span<byte> buffer = stackalloc byte[2];
        ReadFull(source, buffer);
        return BinaryPrimitives.ReadUInt16BigEndian(buffer);
    }

    private static uint ReadUInt32(IByteSource source)
    {
        Span<byte> buffer = stackalloc byte[4];
        ReadFull(source, buffer);
        return BinaryPrimitives.ReadUInt32BigEndian(buffer);
    }

    private static ulong ReadUInt64(IByteSource source)
    {
        Span<byte> buffer = stackalloc byte[8];
        ReadFull(source, buffer);
        return BinaryPrimitives.ReadUInt64BigEndian(buffer);
    }

    // io.ReadFull: zero bytes read is io.EOF, a short read is ErrUnexpectedEOF.
    private static void ReadFull(IByteSource source, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = source.Read(buffer[read..]);
            if (count == 0)
            {
                throw read == 0
                    ? new AwsEventStreamEndOfStreamException()
                    : new IOException("unexpected EOF");
            }

            read += count;
        }
    }

    private static void ReadFullRaw(Stream stream, Span<byte> buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = stream.Read(buffer[read..]);
            if (count == 0)
            {
                throw read == 0
                    ? new AwsEventStreamEndOfStreamException()
                    : new IOException("unexpected EOF");
            }

            read += count;
        }
    }

    // Port of base64.StdEncoding.DecodeString: \r and \n are ignored anywhere,
    // padding is required, and any other non-alphabet byte is an error.
    private static byte[] DecodeBase64Std(string value)
    {
        var input = Encoding.UTF8.GetBytes(value);
        var filtered = new List<byte>(input.Length);
        var positions = new List<int>(input.Length);
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] is (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            filtered.Add(input[i]);
            positions.Add(i);
        }

        if (filtered.Count % 4 != 0)
        {
            var index = (filtered.Count / 4) * 4;
            throw IllegalBase64(positions[index]);
        }

        for (var quantum = 0; quantum < filtered.Count; quantum += 4)
        {
            for (var offset = 0; offset < 4; offset++)
            {
                var index = quantum + offset;
                var data = filtered[index];
                if (data == (byte)'=')
                {
                    var padding = quantum + 4 == filtered.Count
                        && offset >= 2
                        && (offset != 2 || filtered[quantum + 3] == (byte)'=');
                    if (!padding)
                    {
                        throw IllegalBase64(positions[index]);
                    }

                    continue;
                }

                if (!IsBase64Byte(data))
                {
                    throw IllegalBase64(positions[index]);
                }
            }
        }

        return Convert.FromBase64String(Encoding.ASCII.GetString([.. filtered]));
    }

    private static FormatException IllegalBase64(int index)
        => new("illegal base64 data at input byte " + index.ToString(CultureInfo.InvariantCulture));

    private static bool IsBase64Byte(byte value)
        => value is >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'a' and <= (byte)'z'
            or >= (byte)'0' and <= (byte)'9'
            or (byte)'+'
            or (byte)'/';

    private sealed class EventStreamChunk
    {
        [JsonPropertyName("bytes")]
        public string? Bytes { get; set; }

        [JsonPropertyName("p")]
        public string? P { get; set; }
    }

    private sealed class ExceptionInfo
    {
        public string? Code { get; set; }

        [JsonPropertyName("__type")]
        public string? Type { get; set; }

        public string? Message { get; set; }
    }

    private sealed record AwsEventStreamMessage(
        Dictionary<string, AwsHeaderValue> Headers,
        byte[] Payload);

    // Port of the eventstream Header values. Only String() is observable to
    // the decoder; it follows each value type's Go String() rendering.
    private sealed class AwsHeaderValue
    {
        private readonly ValueKind _kind;
        private readonly bool _bool;
        private readonly long _signed;
        private readonly ulong _unsigned;
        private readonly byte[]? _bytes;
        private readonly string? _text;

        private AwsHeaderValue(
            ValueKind kind,
            bool boolValue = false,
            long signed = 0,
            ulong unsigned = 0,
            byte[]? bytes = null,
            string? text = null)
        {
            _kind = kind;
            _bool = boolValue;
            _signed = signed;
            _unsigned = unsigned;
            _bytes = bytes;
            _text = text;
        }

        private enum ValueKind
        {
            Bool,
            Int8,
            Int16,
            Int32,
            Int64,
            Bytes,
            String,
            Timestamp,
            Uuid,
        }

        public static AwsHeaderValue Bool(bool value) => new(ValueKind.Bool, boolValue: value);

        public static AwsHeaderValue Int8(sbyte value) => new(ValueKind.Int8, signed: value);

        public static AwsHeaderValue Int16(short value) => new(ValueKind.Int16, signed: value);

        public static AwsHeaderValue Int32(int value) => new(ValueKind.Int32, signed: value);

        public static AwsHeaderValue Int64(long value) => new(ValueKind.Int64, signed: value);

        public static AwsHeaderValue Bytes(byte[] value) => new(ValueKind.Bytes, bytes: value);

        public static AwsHeaderValue String(string value) => new(ValueKind.String, text: value);

        public static AwsHeaderValue Timestamp(ulong value)
            => new(ValueKind.Timestamp, unsigned: value);

        public static AwsHeaderValue Uuid(byte[] value) => new(ValueKind.Uuid, bytes: value);

        public string GoString() => _kind switch
        {
            ValueKind.Bool => _bool ? "true" : "false",
            ValueKind.Int8 => "0x" + SignedHex(_signed, 2),
            ValueKind.Int16 => "0x" + SignedHex(_signed, 4),
            ValueKind.Int32 => "0x" + SignedHex(_signed, 8),
            ValueKind.Int64 => "0x" + SignedHex(_signed, 16),
            ValueKind.Bytes => Convert.ToBase64String(_bytes!),
            ValueKind.String => _text!,
            ValueKind.Timestamp => unchecked((long)_unsigned).ToString(CultureInfo.InvariantCulture),
            ValueKind.Uuid => FormatUuid(_bytes!),
            _ => string.Empty,
        };

        // fmt's %0Nx for a signed integer: a '-' sign followed by the absolute
        // value padded with zeros to the remaining width.
        private static string SignedHex(long value, int width)
        {
            if (value >= 0)
            {
                return value.ToString("x", CultureInfo.InvariantCulture).PadLeft(width, '0');
            }

            var magnitude = unchecked((ulong)(-(value + 1)) + 1);
            return "-"
                + magnitude.ToString("x", CultureInfo.InvariantCulture).PadLeft(width - 1, '0');
        }

        private static string FormatUuid(byte[] value)
        {
            static string Hex(ReadOnlySpan<byte> bytes)
                => Convert.ToHexStringLower(bytes);

            return Hex(value.AsSpan(0, 4))
                + "-"
                + Hex(value.AsSpan(4, 2))
                + "-"
                + Hex(value.AsSpan(6, 2))
                + "-"
                + Hex(value.AsSpan(8, 2))
                + "-"
                + Hex(value.AsSpan(10, 6));
        }
    }

    private interface IByteSource
    {
        int Read(Span<byte> buffer);
    }

    /// <summary>
    /// The raw reader plus the running IEEE CRC-32 over every byte consumed
    /// through it (decode.go:65-66).
    /// </summary>
    private sealed class FrameReader(Stream stream) : IByteSource
    {
        private uint _crc = 0xFFFFFFFFu;

        public uint Crc => ~_crc;

        public int Read(Span<byte> buffer)
        {
            var read = stream.Read(buffer);
            if (read > 0)
            {
                var data = buffer[..read];
                foreach (var value in data)
                {
                    _crc ^= value;
                    for (var bit = 0; bit < 8; bit++)
                    {
                        _crc = (_crc >> 1) ^ (0xEDB88320u & (0u - (_crc & 1u)));
                    }
                }
            }

            return read;
        }
    }

    // Port of io.LimitReader around the hashing reader.
    private sealed class LimitedReader(FrameReader reader, long limit) : IByteSource
    {
        private readonly FrameReader _reader = reader;
        private long _remaining = limit;

        public int Read(Span<byte> buffer)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var count = (int)Math.Min(buffer.Length, _remaining);
            var read = _reader.Read(buffer[..count]);
            _remaining -= read;
            return read;
        }
    }

    private sealed class AwsEventStreamEndOfStreamException : Exception;

    private sealed class AwsEventStreamChecksumException : Exception
    {
        public AwsEventStreamChecksumException()
            : base("message checksum mismatch")
        {
        }
    }

    private sealed class AwsEventStreamLengthException : Exception
    {
        public AwsEventStreamLengthException(string part, int want, int have)
            : base(
                part
                    + " length invalid, "
                    + want.ToString(CultureInfo.InvariantCulture)
                    + "/"
                    + have.ToString(CultureInfo.InvariantCulture)
                    + ", <nil>")
        {
        }
    }
}
