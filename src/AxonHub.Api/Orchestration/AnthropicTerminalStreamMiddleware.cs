using System.Text.Json;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of closeCompletedAnthropicStream
/// (internal/server/orchestrator/anthropic_terminal_stream.go:15-26). Treats an
/// Anthropic <c>message_stop</c> as the upstream protocol boundary: the provider
/// transport is closed without canceling client delivery, so both pass-through
/// consumers and the normalized accounting pipeline see a clean EOF. Registered
/// last so it wraps the raw stream before the capture fan-out.
/// </summary>
internal sealed class AnthropicTerminalStreamMiddleware(PersistentOutboundTransformer outbound)
    : DummyMiddleware("anthropic-message-stop")
{
    public override IStream<StreamEvent> TransformOutboundRawStream(IStream<StreamEvent> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var request = outbound.RawProviderRequest;
        if (request is null
            || !string.Equals(request.ApiFormat, ApiFormats.AnthropicMessage, StringComparison.Ordinal))
        {
            return stream;
        }

        return ResponseHeaders.WithResponseHeaders(
            new AnthropicTerminalStream(stream),
            ResponseHeaders.GetResponseHeaders(stream));
    }
}

/// <summary>
/// Port of anthropicTerminalStream (anthropic_terminal_stream.go:28-73): closes
/// only the provider transport after a complete message. The outer stream still
/// owns client delivery and usage finalization.
/// </summary>
internal sealed class AnthropicTerminalStream : IStream<StreamEvent>
{
    private readonly IStream<StreamEvent> _stream;
    private readonly object _sync = new();
    private StreamEvent? _current;
    private bool _complete;
    private bool _closed;

    public AnthropicTerminalStream(IStream<StreamEvent> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    public StreamEvent? Current => _current;

    /// <summary>
    /// Port of <c>Next</c>: forwards message_stop once and never waits for HTTP
    /// EOF after it.
    /// </summary>
    public bool Next()
    {
        if (_complete || !_stream.Next())
        {
            return false;
        }

        _current = _stream.Current;
        if (_current is not null && IsMessageStop(_current.Data))
        {
            // Keep the final event stable even if closing releases provider
            // buffers: Go deep-copies the event and its Data before Close.
            _current = CloneEvent(_current);
            _complete = true;
            Close();
        }

        return true;
    }

    /// <summary>
    /// Port of <c>Err</c>: preserves failures before message_stop;
    /// transport-close errors after semantic completion are cleanup, not an
    /// incomplete generation.
    /// </summary>
    public Exception? Err => _complete ? null : _stream.Err;

    /// <summary>
    /// Port of <c>Close</c>: releases the provider connection exactly once,
    /// without canceling the request lifecycle that guards downstream delivery.
    /// </summary>
    public void Dispose() => Close();

    private void Close()
    {
        lock (_sync)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            try
            {
                _stream.Dispose();
            }
            catch (Exception)
            {
                // The reference stores the close error but every caller ignores
                // it; release failures are cleanup after a complete message.
            }
        }
    }

    private static StreamEvent CloneEvent(StreamEvent @event) => new()
    {
        LastEventId = @event.LastEventId,
        Type = @event.Type,
        Data = (byte[])@event.Data.Clone(),
        CleanEofCompletionEvidence = @event.CleanEofCompletionEvidence,
        Size = @event.Size,
        // Go's struct copy shares the header map.
        Headers = @event.Headers,
    };

    /// <summary>
    /// Go <c>gjson.GetBytes(event.Data, "type").String() == "message_stop"</c>.
    /// Only a JSON string can equal the literal; non-object roots and malformed
    /// documents match nothing, like gjson's permissive lookup. Trailing bytes
    /// after the first value are ignored.
    /// </summary>
    private static bool IsMessageStop(byte[] data)
    {
        if (data.Length == 0)
        {
            return false;
        }

        try
        {
            // Go's decoder accepts nesting up to 10000 levels.
            var reader = new Utf8JsonReader(
                data,
                new JsonReaderOptions { MaxDepth = 10_000 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return false;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return false;
                }

                var isType = reader.ValueTextEquals("type"u8);
                if (!reader.Read())
                {
                    return false;
                }

                var tokenType = reader.TokenType;
                var value = tokenType == JsonTokenType.String
                    ? reader.GetString()
                    : null;

                if (tokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }

                if (isType)
                {
                    return string.Equals(value, "message_stop", StringComparison.Ordinal);
                }
            }
        }
        catch (JsonException)
        {
            // gjson does not error on malformed input; it simply finds no key.
        }

        return false;
    }
}
