using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Http;

/// <summary>
/// Incremental port of the NDJSON decoder registered in
/// <c>llm/transformer/ollama/decoder.go</c> (<c>NewNDJSONDecoder</c>), which
/// serves Ollama streaming responses: one JSON object per line, not SSE.
/// </summary>
/// <remarks>
/// Framing matches Go's <c>bufio.Reader.ReadBytes('\n')</c> loop: the terminator
/// is dropped, a single trailing <c>\r</c> is dropped too, empty lines still
/// produce an event with empty data, and a final line without a newline is
/// emitted verbatim. Terminal read failures (including cancellation) surface
/// through <see cref="Err"/>, never as exceptions.
/// </remarks>
public sealed class HttpNdjsonEventStream : IStream<StreamEvent>
{
    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly byte[] _buffer = new byte[8192];
    private readonly MemoryStream _line = new();

    private int _bufferPos;
    private int _bufferLength;
    private bool _closed;
    private Exception? _err;

    public HttpNdjsonEventStream(Stream stream, IDisposable? owner = null)
    {
        _stream = stream;
        _owner = owner;
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
            Dispose();
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
        _line.SetLength(0);

        while (true)
        {
            if (_bufferPos >= _bufferLength)
            {
                _bufferLength = _stream.Read(_buffer, 0, _buffer.Length);
                _bufferPos = 0;

                if (_bufferLength == 0)
                {
                    // io.EOF: a pending fragment is still one event; the decoder
                    // emits it without trimming (there is no terminator to trim).
                    if (_line.Length == 0)
                    {
                        return false;
                    }

                    Current = new StreamEvent { Data = _line.ToArray() };
                    return true;
                }
            }

            var next = _buffer[_bufferPos++];
            if (next == (byte)'\n')
            {
                var data = _line.ToArray();
                var length = data.Length;
                if (length > 0 && data[length - 1] == (byte)'\r')
                {
                    length--;
                }

                Current = new StreamEvent { Data = data.AsSpan(0, length).ToArray() };
                return true;
            }

            _line.WriteByte(next);
        }
    }
}
