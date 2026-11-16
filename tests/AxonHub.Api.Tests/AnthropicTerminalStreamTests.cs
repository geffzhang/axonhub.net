using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/orchestrator/anthropic_terminal_stream_test.go: the
/// Anthropic <c>message_stop</c> event is the semantic completion of the
/// provider stream, so the transport is closed there while errors and
/// incomplete EOF before a terminal event are preserved.
/// </summary>
public sealed class AnthropicTerminalStreamTests
{
    private static StreamEvent Event(string body) => new()
    {
        Data = System.Text.Encoding.UTF8.GetBytes(body),
    };

    [Theory]
    [InlineData("""{"type":"message_stop"}""", null, true)]
    [InlineData("""{"type":"message_delta","delta":{"stop_reason":"tool_use"}}""", null, false)]
    [InlineData("""{"type":"error"}""", "upstream failed", false)]
    public void ClosesTransportAtSemanticCompletion(
        string body,
        string? upstreamError,
        bool complete)
    {
        var error = upstreamError is null ? null : new InvalidOperationException(upstreamError);
        var source = new TerminalCloseProbe(new SliceStream<StreamEvent>([Event(body)]))
        {
            Error = error,
        };
        var stream = new AnthropicTerminalStream(source);

        Assert.True(stream.Next());
        Assert.Equal(body, System.Text.Encoding.UTF8.GetString(stream.Current!.Data));
        Assert.False(stream.Next());

        if (complete)
        {
            Assert.Equal(1, source.Reads);
            Assert.Equal(1, source.Closes);
            Assert.Null(stream.Err);
        }
        else
        {
            Assert.Equal(0, source.Closes);
            Assert.Same(error, stream.Err);
        }

        stream.Dispose();
        stream.Dispose();
        Assert.Equal(1, source.Closes);
    }

    [Theory]
    [InlineData(ApiFormats.AnthropicMessage, true)]
    [InlineData(ApiFormats.OpenAIResponse, false)]
    [InlineData(ApiFormats.OpenAIChatCompletion, false)]
    public void MiddlewareOnlyWrapsAnthropicProviderStreams(string format, bool wrapped)
    {
        var outbound = new PersistentOutboundTransformer(
            [],
            TimeProvider.System,
            new RecordingMetricsRecorder())
        {
            RawProviderRequest = new HttpRequestData { ApiFormat = format },
        };
        var source = new SliceStream<StreamEvent>([]);

        var result = new AnthropicTerminalStreamMiddleware(outbound)
            .TransformOutboundRawStream(source);

        if (wrapped)
        {
            Assert.NotSame(source, result);
        }
        else
        {
            Assert.Same(source, result);
        }
    }

    /// <summary>
    /// Go's <c>terminalCloseProbe</c>: counts reads and closes and exposes its
    /// own terminal error instead of the wrapped stream's.
    /// </summary>
    private sealed class TerminalCloseProbe(IStream<StreamEvent> stream) : IStream<StreamEvent>
    {
        public int Reads { get; private set; }

        public int Closes { get; private set; }

        public Exception? Error { get; init; }

        public StreamEvent? Current => stream.Current;

        public Exception? Err => Error;

        public bool Next()
        {
            Reads++;
            return stream.Next();
        }

        public void Dispose()
        {
            Closes++;
        }
    }
}
