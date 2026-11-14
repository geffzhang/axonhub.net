using AxonHub.Api.Persistence;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of orchestrator.withLivePreview (internal/server/orchestrator/live_streaming.go):
/// registers a buffer for the request and the current execution, and mirrors
/// the provider and client stream events into them so the request detail page
/// can tail a running generation.
/// </summary>
/// <remarks>
/// The reference reads the storage policy lazily on the first streaming
/// inbound request; the port's settings reader is asynchronous and the inbound
/// hook is not, so the orchestrator takes the policy snapshot up front and
/// passes the resulting flag. The middleware still only enables itself for a
/// streaming unified request, exactly like the reference.
/// </remarks>
public sealed class LivePreviewMiddleware(
    LiveStreamRegistry? registry,
    IRequestChainState? persistence,
    bool livePreviewEnabled) : DummyMiddleware("live-preview")
{
    private bool _enabled;
    private bool _initialized;

    /// <summary>Whether the middleware registered buffers for this request (test seam).</summary>
    public bool Enabled => _enabled;

    public override LlmRequest TransformInboundLlmRequest(LlmRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (registry is null)
        {
            _enabled = false;
            return request;
        }

        if (request.Stream is not true)
        {
            _enabled = false;
            return request;
        }

        if (!_initialized)
        {
            _enabled = livePreviewEnabled;
            _initialized = true;
        }

        return request;
    }

    public override HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_enabled || registry is null || persistence is null)
        {
            return request;
        }

        var requestId = persistence.CurrentRequestId;
        if (requestId != 0 && registry.GetRequestBuffer(requestId) is null)
        {
            registry.RegisterRequest(requestId, registry.CreateBuffer());
        }

        var executionId = persistence.CurrentExecutionId;
        if (executionId != 0 && registry.GetExecutionBuffer(executionId) is null)
        {
            registry.RegisterExecution(executionId, registry.CreateBuffer());
        }

        return request;
    }

    public override void OnOutboundRawError(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (!_enabled || registry is null || persistence is null)
        {
            return;
        }

        var executionId = persistence.CurrentExecutionId;
        if (executionId != 0
            && registry.GetExecutionBuffer(executionId) is { } executionBuffer)
        {
            executionBuffer.Close();
            registry.UnregisterExecution(executionId);
        }

        var requestId = persistence.CurrentRequestId;
        if (requestId != 0
            && registry.GetRequestBuffer(requestId) is { } requestBuffer)
        {
            requestBuffer.Close();
            registry.UnregisterRequest(requestId);
        }
    }

    public override IStream<StreamEvent> TransformOutboundRawStream(IStream<StreamEvent> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!_enabled || registry is null || persistence is null)
        {
            return stream;
        }

        var executionId = persistence.CurrentExecutionId;
        if (executionId == 0)
        {
            return stream;
        }

        var buffer = registry.GetExecutionBuffer(executionId);
        return buffer is null
            ? stream
            : new BufferingStream(stream, buffer, () => registry.UnregisterExecution(executionId));
    }

    public override IStream<StreamEvent> TransformInboundRawStream(IStream<StreamEvent> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!_enabled || registry is null || persistence is null)
        {
            return stream;
        }

        var requestId = persistence.CurrentRequestId;
        if (requestId == 0)
        {
            return stream;
        }

        var buffer = registry.GetRequestBuffer(requestId);
        return buffer is null
            ? stream
            : DeliveryCapabilities.Preserve(
                stream,
                new BufferingStream(stream, buffer, () => registry.UnregisterRequest(requestId)));
    }

    /// <summary>
    /// Port of liveRequestStream / liveRequestExecutionStream: append every
    /// event to the buffer (binary payloads summarized first), and close and
    /// unregister the buffer when the stream is disposed.
    /// </summary>
    private sealed class BufferingStream(
        IStream<StreamEvent> stream,
        ChunkBuffer buffer,
        Action unregister) : IStream<StreamEvent>
    {
        private bool _closed;

        public StreamEvent? Current { get; private set; }

        public Exception? Err => stream.Err;

        public bool Next()
        {
            if (!stream.Next())
            {
                Current = null;
                return false;
            }

            Current = stream.Current;
            if (Current is not null)
            {
                // Summarize raw binary audio chunks before buffering so the
                // live preview path does not retain full TTS audio bytes in
                // memory. Downstream consumers still receive the unmodified
                // event via Current.
                buffer.Append(StreamEvent.SummarizeBinaryChunk(Current));
            }

            return true;
        }

        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            try
            {
                buffer.Close();
                unregister();
            }
            finally
            {
                stream.Dispose();
            }
        }
    }
}
