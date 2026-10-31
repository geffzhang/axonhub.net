using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Http;

/// <summary>
/// Port of <c>binaryChunkDecoder</c> (llm/httpclient/decoder.go:235-327): yields
/// raw bytes as stream events for provider streams that do not use SSE framing,
/// such as chunked TTS audio responses. One <c>binary.done</c> event terminates
/// the stream at end of input.
/// </summary>
/// <remarks>
/// .NET's synchronous <see cref="Stream.Read"/> either returns data, returns 0
/// at end of input (Go's <c>(0, io.EOF)</c>), or throws (Go's <c>(n, err)</c>
/// pairs). The reference's <c>(0, nil)</c> "no data right now" branch has no
/// .NET equivalent and is dropped (difference C079).
/// </remarks>
public sealed class HttpBinaryEventStream : IStream<StreamEvent>
{
    /// <summary>decoder.go:246 — the reference reads 32 KB chunks.</summary>
    private const int BufferSize = 32 * 1024;

    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly string _contentType;
    private readonly CancellationToken _cancellationToken;
    private readonly byte[] _buffer = new byte[BufferSize];

    private Exception? _err;
    private Exception? _pendingErr;
    private bool _doneSent;
    private bool _closed;

    public HttpBinaryEventStream(
        Stream stream,
        string contentType,
        CancellationToken cancellationToken,
        IDisposable? owner = null)
    {
        _stream = stream;
        _contentType = contentType;
        _cancellationToken = cancellationToken;
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
            // A transport failure is surfaced as stream data, never thrown.
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
        if (_err is not null)
        {
            return false;
        }

        if (_pendingErr is not null)
        {
            // A pending error from the previous partial read; surface it now.
            _err = _pendingErr;
            _pendingErr = null;
            return false;
        }

        // The reference probes the context non-blockingly before each read;
        // cancellation beats whatever the read would have returned.
        if (_cancellationToken.IsCancellationRequested)
        {
            _err = new OperationCanceledException(_cancellationToken);
            return false;
        }

        var n = _stream.Read(_buffer, 0, _buffer.Length);
        if (n > 0)
        {
            Current = new StreamEvent
            {
                Type = _contentType,
                Data = [.. _buffer[..n]],
            };

            return true;
        }

        // n == 0 is end of input in .NET. Emit exactly one binary.done marker.
        if (!_doneSent)
        {
            _doneSent = true;
            Current = new StreamEvent { Type = AuthTypes.BinaryStreamDoneEventType };
            return true;
        }

        return false;
    }
}
