using System.Globalization;
using System.Text;

namespace AxonHub.Llm.Http;

/// <summary>
/// Port of httpclient.NewDefaultSSEDecoder (llm/httpclient/decoder.go), which
/// wraps <c>github.com/tmaxmax/go-sse</c>.
/// </summary>
/// <remarks>
/// Framing follows the SSE spec as go-sse implements it, verified against the
/// reference with the <c>client-stream</c> corpus:
/// <list type="bullet">
/// <item>Lines end with CRLF, LF or CR; a blank line dispatches the record.</item>
/// <item><c>field: value</c> strips exactly one leading space from the value;
/// <c>field</c> alone means an empty value; <c>:</c> starts a comment.</item>
/// <item>Multiple <c>data</c> lines join with <c>\n</c>.</item>
/// <item>A record is dispatched when at least one field line was seen — even
/// with an empty data buffer (go-sse does not implement the spec's
/// "abort dispatch when the data buffer is empty" rule).</item>
/// <item>A trailing record with no blank line is flushed at end of input.</item>
/// <item>A leading UTF-8 BOM is ignored.</item>
/// <item><c>id</c> sets the last event id, which persists across records.</item>
/// </list>
/// </remarks>
public sealed class SseDecoder : IDisposable
{
    private readonly string _text;
    private readonly int _maxEventBytes;
    private readonly int _bomBytes;
    private int _pos;
    private string _lastEventId = string.Empty;
    private bool _closed;

    public SseDecoder(string text, int maxEventBytes = 0)
    {
        var hasBom = text.Length > 0 && text[0] == '﻿';
        _text = hasBom ? text[1..] : text;

        // The reference counts raw bytes before its BOM is stripped, so the
        // first event must still account for the three BOM bytes.
        _bomBytes = hasBom ? 3 : 0;
        _maxEventBytes = maxEventBytes <= 0 ? StreamLimits.DefaultMaxSseEventBytes : maxEventBytes;
    }

    /// <summary>Port of StreamEvent; null before the first successful Next.</summary>
    public StreamEvent? Current { get; private set; }

    /// <summary>Terminal error, if any.</summary>
    public Exception? Err { get; private set; }

    public bool Next()
    {
        if (_closed || Err is not null)
        {
            return false;
        }

        var eventType = string.Empty;
        var data = new List<string>();
        var sawField = false;
        var recordStart = -1;

        while (true)
        {
            var lineStart = _pos;
            if (!TryReadLine(out var line))
            {
                // go-sse flushes a pending record at end of input.
                if (!sawField)
                {
                    return false;
                }

                break;
            }

            if (line.Length == 0)
            {
                if (sawField)
                {
                    break;
                }

                continue;
            }

            if (recordStart < 0)
            {
                recordStart = lineStart;
            }

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
                    // "retry" and unknown fields are ignored, as in go-sse.
                    break;
            }
        }

        if (recordStart >= 0)
        {
            // Port of go-sse's MaxEventSize: bufio.Scanner splits into whole
            // events, so the limit covers the event block including the line
            // terminators. The reference's newlineTerminatingReader synthesizes
            // one '\n' when the body ends without one (#2692), so that byte
            // counts too. An oversized event is reported instead of being
            // delivered, and every later Next returns false.
            var recordBytes = Encoding.UTF8.GetByteCount(_text.AsSpan(recordStart, _pos - recordStart))
                + (recordStart == 0 ? _bomBytes : 0);
            if (_pos >= _text.Length
                && _text.Length > 0
                && _text[^1] != '\n'
                && _text[^1] != '\r')
            {
                recordBytes++;
            }

            if (recordBytes > _maxEventBytes)
            {
                Err = new StreamEventTooLargeException(
                    StreamLimits.EventTooLarge.Message
                        + ": limit "
                        + _maxEventBytes.ToString(CultureInfo.InvariantCulture)
                        + " bytes",
                    StreamLimits.EventTooLarge);
                return false;
            }
        }

        Current = new StreamEvent
        {
            LastEventId = _lastEventId,
            Type = eventType,
            Data = Encoding.UTF8.GetBytes(string.Join("\n", data)),
        };

        return true;
    }

    public void Dispose()
    {
        _closed = true;
    }

    private bool TryReadLine(out string line)
    {
        if (_pos >= _text.Length)
        {
            line = string.Empty;
            return false;
        }

        var start = _pos;
        while (_pos < _text.Length && _text[_pos] != '\n' && _text[_pos] != '\r')
        {
            _pos++;
        }

        line = _text[start.._pos];

        if (_pos < _text.Length)
        {
            if (_text[_pos] == '\r' && _pos + 1 < _text.Length && _text[_pos + 1] == '\n')
            {
                _pos += 2;
            }
            else
            {
                _pos++;
            }
        }

        return true;
    }
}
