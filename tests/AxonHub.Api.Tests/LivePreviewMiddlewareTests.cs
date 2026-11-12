using System.Text;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks orchestrator.withLivePreview (internal/server/orchestrator/live_streaming.go):
/// the enable gate (registry present, streaming unified request, storage policy),
/// buffer registration after persistence created the rows, event mirroring into
/// both buffers, and the close/unregister on error and disposal.
/// </summary>
public sealed class LivePreviewMiddlewareTests
{
    [Fact]
    public void NonStreamRequestsNeverRegisterBuffers()
    {
        var registry = new LiveStreamRegistry();
        var state = new FakeState(42, 7);
        var middleware = new LivePreviewMiddleware(registry, state, livePreviewEnabled: true);

        middleware.TransformInboundLlmRequest(new LlmRequest { Stream = false });
        Assert.False(middleware.Enabled);

        middleware.TransformOutboundRawRequest(new HttpRequestData());

        Assert.Null(registry.GetRequestBuffer(42));
        Assert.Null(registry.GetExecutionBuffer(7));
    }

    [Fact]
    public void NullStreamFlagCountsAsNonStream()
    {
        var registry = new LiveStreamRegistry();
        var middleware = new LivePreviewMiddleware(
            registry,
            new FakeState(42, 7),
            livePreviewEnabled: true);

        middleware.TransformInboundLlmRequest(new LlmRequest());

        Assert.False(middleware.Enabled);
    }

    [Fact]
    public void MissingRegistryDisablesTheMiddleware()
    {
        var middleware = new LivePreviewMiddleware(
            null,
            new FakeState(42, 7),
            livePreviewEnabled: true);

        middleware.TransformInboundLlmRequest(new LlmRequest { Stream = true });

        Assert.False(middleware.Enabled);
    }

    [Fact]
    public void DisabledStoragePolicyDoesNotRegisterBuffers()
    {
        var registry = new LiveStreamRegistry();
        var state = new FakeState(42, 7);
        var middleware = new LivePreviewMiddleware(registry, state, livePreviewEnabled: false);

        middleware.TransformInboundLlmRequest(new LlmRequest { Stream = true });
        Assert.False(middleware.Enabled);

        var outbound = middleware.TransformOutboundRawRequest(new HttpRequestData());

        Assert.NotNull(outbound);
        Assert.Null(registry.GetRequestBuffer(42));
        Assert.Null(registry.GetExecutionBuffer(7));
    }

    [Fact]
    public void StreamingRequestsRegisterRequestAndExecutionBuffers()
    {
        var registry = new LiveStreamRegistry();
        var state = new FakeState(42, 7);
        var middleware = new LivePreviewMiddleware(registry, state, livePreviewEnabled: true);

        middleware.TransformInboundLlmRequest(new LlmRequest { Stream = true });
        Assert.True(middleware.Enabled);

        middleware.TransformOutboundRawRequest(new HttpRequestData());

        var requestBuffer = registry.GetRequestBuffer(42);
        var executionBuffer = registry.GetExecutionBuffer(7);
        Assert.NotNull(requestBuffer);
        Assert.NotNull(executionBuffer);

        // A second hook for the same rows keeps the registered buffers
        // (live_streaming.go:60-66 checks Get*Buffer before registering).
        middleware.TransformOutboundRawRequest(new HttpRequestData());

        Assert.Same(requestBuffer, registry.GetRequestBuffer(42));
        Assert.Same(executionBuffer, registry.GetExecutionBuffer(7));
    }

    [Fact]
    public void RetryAfterAnErrorRegistersFreshBuffers()
    {
        var registry = new LiveStreamRegistry();
        var state = new FakeState(42, 7);
        var middleware = new LivePreviewMiddleware(registry, state, livePreviewEnabled: true);
        middleware.TransformInboundLlmRequest(new LlmRequest { Stream = true });
        middleware.TransformOutboundRawRequest(new HttpRequestData());
        var firstRequestBuffer = registry.GetRequestBuffer(42)!;

        middleware.OnOutboundRawError(new IOException("upstream reset"));
        Assert.True(firstRequestBuffer.IsClosed);

        // The next attempt starts from scratch: both buffers are re-registered.
        state.CurrentExecutionId = 8;
        middleware.TransformOutboundRawRequest(new HttpRequestData());

        Assert.NotSame(firstRequestBuffer, registry.GetRequestBuffer(42));
        Assert.NotNull(registry.GetExecutionBuffer(8));
    }

    [Fact]
    public void OutboundStreamMirrorsEventsIntoTheExecutionBuffer()
    {
        var registry = new LiveStreamRegistry();
        var state = new FakeState(42, 7);
        var middleware = new LivePreviewMiddleware(registry, state, livePreviewEnabled: true);
        middleware.TransformInboundLlmRequest(new LlmRequest { Stream = true });
        middleware.TransformOutboundRawRequest(new HttpRequestData());
        var executionBuffer = registry.GetExecutionBuffer(7)!;

        var source = new ListStream<StreamEvent>(
        [
            Data("data", "{\"a\":1}"),
            new StreamEvent { Type = "audio/mp3", Data = "abcdefgh"u8.ToArray() },
        ]);

        using var wrapped = middleware.TransformOutboundRawStream(source);

        // The consumer still receives the unmodified events.
        Assert.True(wrapped.Next());
        Assert.Same(source.Current, wrapped.Current);
        Assert.True(wrapped.Next());
        Assert.Equal(8, wrapped.Current!.Data.Length);
        Assert.False(wrapped.Next());
        Assert.Null(wrapped.Current);

        var chunks = executionBuffer.Slice();
        Assert.Equal(2, chunks.Count);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(chunks[0].Data));

        // Audio bytes are summarized before buffering (live_streaming.go:155).
        Assert.Empty(chunks[1].Data);
        Assert.Equal(8, chunks[1].Size);
        Assert.Equal("audio/mp3", chunks[1].Type);
    }

    [Fact]
    public void InboundStreamMirrorsEventsIntoTheRequestBuffer()
    {
        var registry = new LiveStreamRegistry();
        var middleware = new LivePreviewMiddleware(
            registry,
            new FakeState(42, 7),
            livePreviewEnabled: true);
        middleware.TransformInboundLlmRequest(new LlmRequest { Stream = true });
        middleware.TransformOutboundRawRequest(new HttpRequestData());
        var requestBuffer = registry.GetRequestBuffer(42)!;

        using var wrapped = middleware.TransformInboundRawStream(
            new ListStream<StreamEvent>([Data("data", "{\"b\":2}")]));

        Assert.True(wrapped.Next());
        Assert.False(wrapped.Next());

        var chunk = Assert.Single(requestBuffer.Slice());
        Assert.Equal("{\"b\":2}", Encoding.UTF8.GetString(chunk.Data));
    }

    [Fact]
    public void DisposingAnOutboundStreamClosesAndUnregistersTheExecutionBuffer()
    {
        var registry = new LiveStreamRegistry();
        var middleware = new LivePreviewMiddleware(
            registry,
            new FakeState(42, 7),
            livePreviewEnabled: true);
        middleware.TransformInboundLlmRequest(new LlmRequest { Stream = true });
        middleware.TransformOutboundRawRequest(new HttpRequestData());
        var executionBuffer = registry.GetExecutionBuffer(7)!;

        var wrapped = middleware.TransformOutboundRawStream(new ListStream<StreamEvent>([]));
        wrapped.Dispose();
        wrapped.Dispose();

        Assert.True(executionBuffer.IsClosed);
        Assert.Null(registry.GetExecutionBuffer(7));

        // The request buffer is owned by the inbound wrapper and stays open.
        Assert.NotNull(registry.GetRequestBuffer(42));
    }

    [Fact]
    public void DisposingAnInboundStreamClosesAndUnregistersTheRequestBuffer()
    {
        var registry = new LiveStreamRegistry();
        var middleware = new LivePreviewMiddleware(
            registry,
            new FakeState(42, 7),
            livePreviewEnabled: true);
        middleware.TransformInboundLlmRequest(new LlmRequest { Stream = true });
        middleware.TransformOutboundRawRequest(new HttpRequestData());
        var requestBuffer = registry.GetRequestBuffer(42)!;

        var wrapped = middleware.TransformInboundRawStream(new ListStream<StreamEvent>([]));
        wrapped.Dispose();

        Assert.True(requestBuffer.IsClosed);
        Assert.Null(registry.GetRequestBuffer(42));
    }

    [Fact]
    public void OutboundErrorsCloseAndUnregisterBothBuffers()
    {
        var registry = new LiveStreamRegistry();
        var middleware = new LivePreviewMiddleware(
            registry,
            new FakeState(42, 7),
            livePreviewEnabled: true);
        middleware.TransformInboundLlmRequest(new LlmRequest { Stream = true });
        middleware.TransformOutboundRawRequest(new HttpRequestData());
        var requestBuffer = registry.GetRequestBuffer(42)!;
        var executionBuffer = registry.GetExecutionBuffer(7)!;

        middleware.OnOutboundRawError(new IOException("upstream reset"));

        Assert.True(executionBuffer.IsClosed);
        Assert.True(requestBuffer.IsClosed);
        Assert.Null(registry.GetRequestBuffer(42));
        Assert.Null(registry.GetExecutionBuffer(7));
    }

    [Fact]
    public void MiddlewareWithoutBuffersLeavesTheStreamsUntouched()
    {
        var registry = new LiveStreamRegistry();
        var middleware = new LivePreviewMiddleware(
            registry,
            new FakeState(0, 0),
            livePreviewEnabled: true);
        middleware.TransformInboundLlmRequest(new LlmRequest { Stream = true });

        var source = new ListStream<StreamEvent>([]);

        Assert.Same(source, middleware.TransformOutboundRawStream(source));
        Assert.Same(source, middleware.TransformInboundRawStream(source));
    }

    private sealed class FakeState(long requestId, long executionId) : IRequestChainState
    {
        public long CurrentRequestId { get; set; } = requestId;

        public long CurrentExecutionId { get; set; } = executionId;
    }

    private static StreamEvent Data(string type, string payload)
        => new() { Type = type, Data = Encoding.UTF8.GetBytes(payload) };
}
