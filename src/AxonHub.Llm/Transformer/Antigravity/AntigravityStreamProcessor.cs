using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;

namespace AxonHub.Llm.Transformer.Antigravity;

/// <summary>
/// Port of antigravity.StreamProcessor (llm/transformer/antigravity/streaming.go):
/// reads the raw Antigravity SSE byte stream line by line and converts each
/// chunk through the Gemini transformer (unwrapping the "response" envelope).
/// </summary>
public sealed class AntigravityStreamProcessor
{
    private readonly BufioReader _reader;
    private readonly IOutboundTransformer _geminiTransformer;

    /// <summary>Port of NewStreamProcessor.</summary>
    public AntigravityStreamProcessor(Stream reader, IOutboundTransformer geminiTransformer)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(geminiTransformer);

        _reader = new BufioReader(reader);
        _geminiTransformer = geminiTransformer;
    }

    /// <summary>
    /// Port of Recv: returns the next response chunk, or null at end of stream
    /// (Go's io.EOF). Transport errors are thrown.
    /// </summary>
    public LlmResponse? Recv()
    {
        while (true)
        {
            var (line, eof, error) = _reader.ReadBytes();
            var handleEof = false;

            if (error is not null)
            {
                throw error;
            }

            if (eof)
            {
                if (line is not null)
                {
                    // A partial final line without a newline - process it.
                    handleEof = true;
                }
                else
                {
                    // Clean EOF with no data.
                    return null;
                }
            }

            var lineText = Encoding.UTF8.GetString(line!).Trim();

            if (lineText.Length == 0)
            {
                if (handleEof)
                {
                    return null;
                }

                continue;
            }

            if (!lineText.StartsWith("data:", StringComparison.Ordinal))
            {
                if (handleEof)
                {
                    return null;
                }

                continue;
            }

            var data = lineText["data:".Length..].Trim();

            if (data == "[DONE]")
            {
                return null;
            }

            // Hack: use the gemini transformer to convert the chunk.
            // We create a fake http response with the chunk body.
            // Unwrapping: extract the inner `response` object.
            var chunkBytes = UnwrapChunk(Encoding.UTF8.GetBytes(data));

            var fakeResp = new HttpResponseData
            {
                StatusCode = 200,
                Body = chunkBytes,
            };

            LlmResponse llmResp;
            try
            {
                llmResp = _geminiTransformer.TransformResponse(fakeResp);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("gemini transform error: " + ex.Message, ex);
            }

            // Fix up for streaming: move Message to Delta.
            foreach (var choice in llmResp.Choices)
            {
                if (choice.Message is not null)
                {
                    choice.Delta = choice.Message;
                    choice.Message = null;
                }
            }

            // Also the object type.
            llmResp.Object = "chat.completion.chunk";

            return llmResp;
        }
    }

    /// <summary>
    /// Port of the wrapper unwrap in streaming.go:79-86: when the chunk
    /// unmarshals into an object with a non-empty "response" field, that inner
    /// payload is used; otherwise the chunk passes through unchanged.
    /// </summary>
    internal static byte[] UnwrapChunk(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("response", out var response))
            {
                return Encoding.UTF8.GetBytes(response.GetRawText());
            }
        }
        catch (JsonException)
        {
            // Not JSON (or not an object): pass through unchanged.
        }

        return data;
    }

    /// <summary>
    /// Port of the Go <c>bufio.Reader.ReadBytes('\n')</c> semantics used by
    /// Recv: data up to and including '\n'; a partial final line at EOF comes
    /// back with <c>Eof=true</c>; clean EOF returns (null, true).
    /// </summary>
    internal sealed class BufioReader(Stream stream)
    {
        private const int BufferSize = 4096;

        private readonly byte[] _buffer = new byte[BufferSize];
        private readonly List<byte> _pending = [];

        private int _pos;

        private int _len;

        private bool _eof;

        public (byte[]? Line, bool Eof, Exception? Error) ReadBytes()
        {
            while (true)
            {
                for (var i = _pos; i < _len; i++)
                {
                    if (_buffer[i] == (byte)'\n')
                    {
                        var line = new byte[_pending.Count + (i - _pos + 1)];
                        _pending.CopyTo(line);
                        Array.Copy(_buffer, _pos, line, _pending.Count, i - _pos + 1);
                        _pending.Clear();
                        _pos = i + 1;

                        return (line, false, null);
                    }
                }

                if (_eof)
                {
                    if (_pending.Count > 0 || _pos < _len)
                    {
                        var line = new byte[_pending.Count + (_len - _pos)];
                        _pending.CopyTo(line);
                        Array.Copy(_buffer, _pos, line, _pending.Count, _len - _pos);
                        _pending.Clear();
                        _pos = _len;

                        return (line, true, null);
                    }

                    return (null, true, null);
                }

                // No newline yet: the scanned bytes join the pending line, like
                // bufio's collectFragments growing the accumulated slice.
                if (_pos < _len)
                {
                    _pending.AddRange(new ReadOnlySpan<byte>(_buffer, _pos, _len - _pos));
                }

                _pos = 0;
                _len = 0;

                int n;
                try
                {
                    n = stream.Read(_buffer, 0, BufferSize);
                }
                catch (Exception ex)
                {
                    return (null, false, ex);
                }

                if (n == 0)
                {
                    _eof = true;
                    continue;
                }

                _len = n;
            }
        }
    }
}
