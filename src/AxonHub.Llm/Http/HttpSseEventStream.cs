using System.Text;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Http;

/// <summary>
/// Incremental port of the default SSE decoder (llm/httpclient/decoder.go,
/// <c>NewSSEDecoderWithMaxEventSize</c>), reading the upstream response body
/// byte by byte as it arrives.
/// </summary>
/// <remarks>
/// Framing matches <see cref="SseDecoder"/> exactly (see its remarks for the
/// go-sse rules), including the oversized-event accounting: the byte count of a
/// record spans its first field line through the terminating blank line, and a
/// leading UTF-8 BOM adds three bytes to the first record. A terminal read
/// failure is surfaced through <see cref="Err"/>, never thrown.
/// </remarks>
public sealed class HttpSseEventStream : IStream<StreamEvent>
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly int _maxEventBytes;
    private readonly byte[] _buffer = new byte[8192];
    private readonly Queue<byte> _pushback = new();

    private int _bufferPos;
    private int _bufferLength;
    private bool _eof;
    private bool _closed;
    private bool _bomDetected;
    private int _bomBytes;
    private long _lineCount;
    private string _lastEventId = string.Empty;
    private Exception? _err;

    public HttpSseEventStream(Stream stream, IDisposable? owner = null, int maxEventBytes = 0)
    {
        _stream = stream;
        _owner = owner;
        _maxEventBytes = maxEventBytes <= 0 ? StreamLimits.DefaultMaxSseEventBytes : maxEventBytes;
    }

    public StreamEvent? Current { get; private set; }

    public Exception? Err => _err;

    public bool Next()
    {
        if (_closed || _err is not null)
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
            return false;
        }
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _stream.Dispose();
        _owner?.Dispose();
    }

    private bool NextCore()
    {
        // BOM probing happens on the first read, inside Next's try/catch, so a
        // body read failure becomes Err (Go surfaces it as stream data too).
        EnsureBomDetected();

        var eventType = string.Empty;
        var data = new List<string>();
        var sawField = false;
        var recordBytes = 0L;
        var recordStarted = false;

        while (true)
        {
            var line = ReadLine(out var lineBytes, out var terminatorBytes);

            if (line is null)
            {
                // go-sse flushes a pending record at end of input.
                if (!sawField)
                {
                    return false;
                }

                break;
            }

            // SseDecoder counts the BOM only when the record starts at the very
            // beginning of the input, so the record's line index decides it.
            var lineIndex = _lineCount;
            _lineCount++;

            if (line.Length == 0)
            {
                if (sawField)
                {
                    // The blank line belongs to the record; SseDecoder counts it
                    // because its record span ends after the terminator.
                    recordBytes += terminatorBytes;
                    break;
                }

                continue;
            }

            if (!recordStarted)
            {
                recordStarted = true;
                if (lineIndex == 0)
                {
                    recordBytes += _bomBytes;
                }
            }

            recordBytes += lineBytes + terminatorBytes;

            if (line[0] == ':')
            {
                continue;
            }

            string field;
            string value;

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                field = line;
                value = string.Empty;
            }
            else
            {
                field = line[..colon];
                value = line[(colon + 1)..];
                if (value.Length > 0 && value[0] == ' ')
                {
                    value = value[1..];
                }
            }

            sawField = true;

            switch (field)
            {
                case "event":
                    eventType = value;
                    break;
                case "data":
                    data.Add(value);
                    break;
                case "id":
                    if (!value.Contains('\0', StringComparison.Ordinal))
                    {
                        _lastEventId = value;
                    }

                    break;
                default:
                    break;
            }
        }

        if (recordStarted && recordBytes > _maxEventBytes)
        {
            _err = new StreamEventTooLargeException(
                StreamLimits.EventTooLarge.Message
                    + ": limit "
                    + _maxEventBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " bytes",
                StreamLimits.EventTooLarge);
            return false;
        }

        Current = new StreamEvent
        {
            LastEventId = _lastEventId,
            Type = eventType,
            Data = Utf8.GetBytes(string.Join("\n", data)),
        };

        return true;
    }

    /// <summary>
    /// Reads one line the way SseDecoder's TryReadLine does: terminators are
    /// CRLF, LF or CR, and the terminator bytes are reported for the size cap.
    /// Returns null at end of input.
    /// </summary>
    private string? ReadLine(out int lineBytes, out int terminatorBytes)
    {
        lineBytes = 0;
        terminatorBytes = 0;

        var bytes = new List<byte>();
        while (true)
        {
            if (!TryReadByte(out var b))
            {
                if (bytes.Count == 0)
                {
                    return null;
                }

                // go-sse drops an unterminated final field. The reference wraps
                // the body in newlineTerminatingReader, which synthesizes '\n'
                // at EOF (#2692), so the final line and that newline both count
                // toward the event-size cap.
                lineBytes = bytes.Count;
                terminatorBytes = 1;
                return Decode(bytes);
            }

            if (b == '\n')
            {
                terminatorBytes = 1;
                break;
            }

            if (b == '\r')
            {
                terminatorBytes = 1;
                if (TryReadByte(out var next))
                {
                    if (next == '\n')
                    {
                        terminatorBytes = 2;
                    }
                    else
                    {
                        _pushback.Enqueue(next);
                    }
                }

                break;
            }

            bytes.Add(b);
        }

        lineBytes = bytes.Count;
        return Decode(bytes);
    }

    private static string Decode(List<byte> bytes) => Utf8.GetString(bytes.ToArray());

    private void EnsureBomDetected()
    {
        if (_bomDetected)
        {
            return;
        }

        _bomDetected = true;

        // Stream.Read may return fewer bytes than requested; keep reading until
        // the three-byte prefix is complete or the body ends.
        var prefix = new byte[3];
        var count = 0;
        while (count < prefix.Length)
        {
            var read = _stream.Read(prefix, count, prefix.Length - count);
            if (read <= 0)
            {
                _eof = true;
                break;
            }

            count += read;
        }

        if (count == 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
        {
            _bomBytes = 3;
            return;
        }

        // _pushback is FIFO, so re-queue the probe bytes in their original order.
        for (var i = 0; i < count; i++)
        {
            _pushback.Enqueue(prefix[i]);
        }
    }

    private bool TryReadByte(out byte value)
    {
        if (_pushback.Count > 0)
        {
            value = _pushback.Dequeue();
            return true;
        }

        if (_bufferPos >= _bufferLength)
        {
            if (_eof)
            {
                value = 0;
                return false;
            }

            var read = _stream.Read(_buffer, 0, _buffer.Length);
            if (read <= 0)
            {
                _eof = true;
                value = 0;
                return false;
            }

            _bufferPos = 0;
            _bufferLength = read;
        }

        value = _buffer[_bufferPos];
        _bufferPos++;
        return true;
    }
}
