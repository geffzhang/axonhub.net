using System.Text;
using System.Text.Json;
using AxonHub.Api.Http;
using AxonHub.Api.Persistence;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of api.RequestPreviewHandlers.PreviewRequest
/// (internal/server/api/request_live.go:97-153), mounted by routes.go:150 on
/// the admin group. An active streaming request whose buffer is still in the
/// live registry is tailed as SSE (preview.replay / preview.chunk /
/// preview.completed); everything else serves the reference's static-fetch
/// fallback.
/// </summary>
public static class RequestPreviewHandlers
{
    public const string StaticFetchMode = "static-fetch";
    public const string CompletedStatus = "completed";
    public const string FailedStatus = "failed";
    public const string CanceledStatus = "canceled";
    public const string ProcessingStatus = "processing";

    /// <summary>Event names of the request detail preview contract (request_live.go:82).</summary>
    public const string ReplayEvent = "preview.replay";
    public const string ChunkEvent = "preview.chunk";
    public const string CompletedEvent = "preview.completed";

    /// <summary>Port of PreviewRequest (request_live.go:97-146).</summary>
    public static async Task PreviewRequestAsync(
        HttpContext context,
        string? requestId,
        IManagementIdentity identity,
        IRequestStore requests,
        RequestPayloadStorage payloads,
        LiveStreamRegistry liveStreams)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(payloads);
        ArgumentNullException.ThrowIfNull(liveStreams);

        var cancellationToken = context.RequestAborted;

        var projectId = await AdminRequestEndpoints
            .RequireProjectIdAsync(context, identity)
            .ConfigureAwait(false);
        if (projectId is null)
        {
            return;
        }

        var request = await AdminRequestEndpoints
            .ResolveRequestAsync(context, requestId, projectId.Value, requests)
            .ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        // request_live.go:127-136: only an active stream whose buffer is still
        // registered reaches the live branch; a completed request or a lost
        // buffer falls back to the persisted chunks.
        if (request.Status == ProcessingStatus
            && request.Stream
            && liveStreams.GetRequestBuffer(request.Id) is { } buffer)
        {
            using var stream = new RequestPreviewStream(cancellationToken, buffer);
            await SseStreamWriter.WriteAsync(
                    context,
                    stream,
                    expectedChoices: 0,
                    cancellationToken,
                    transformStreamError: null)
                .ConfigureAwait(false);
            return;
        }

        await WriteStaticPreviewAsync(context, request, payloads, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of writeStaticPreview (request_live.go:155-171).</summary>
    private static async Task WriteStaticPreviewAsync(
        HttpContext context,
        RequestRecord request,
        RequestPayloadStorage payloads,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string> chunks;
        try
        {
            chunks = RequestPayloadStorage.SanitizeLoadedResponseChunks(request.ResponseChunks);
        }
        catch (JsonException)
        {
            chunks = [];
        }

        if (chunks.Count == 0)
        {
            try
            {
                chunks = await payloads.LoadResponseChunksAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                // The reference's ent row already holds decoded raw messages,
                // so its loader cannot fail here; a corrupt text column in the
                // port degrades to the empty list instead of a 500.
                chunks = [];
            }
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("mode", StaticFetchMode);
            writer.WritePropertyName("responseChunks");
            writer.WriteStartArray();
            foreach (var chunk in chunks)
            {
                // encoding/json compacts each json.RawMessage on the way out.
                writer.WriteRawValue(GoJson.Compact(Encoding.UTF8.GetBytes(chunk)));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.Body
            .WriteAsync(buffer.ToArray(), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Port of requestPreviewStream (request_live.go:165-269): reads the live
    /// buffer from the subscription's replay cutoff onward, skips the [DONE]
    /// terminal marker, and emits one preview.completed event once the buffer
    /// closes. A client disconnect or three idle minutes end the stream.
    /// </summary>
    /// <remarks>
    /// The reference blocks its goroutine on the subscriber channel; the port
    /// blocks the request thread on the subscription semaphore with the same
    /// timeout. Preview connections are admin-page tails, one per open request
    /// detail page.
    /// </remarks>
    private sealed class RequestPreviewStream : IStream<StreamEvent>
    {
        private static readonly byte[] DoneStreamEventData = "[DONE]"u8.ToArray();
        private static readonly byte[] CompletedEventData = "{\"status\":\"completed\"}"u8.ToArray();
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);

        private readonly ChunkBuffer _buffer;
        private readonly BufferSubscription _subscription;
        private readonly CancellationToken _requestAborted;
        private int _index;
        private bool _completed;
        private StreamEvent? _current;

        public RequestPreviewStream(CancellationToken requestAborted, ChunkBuffer buffer)
        {
            _buffer = buffer;
            _requestAborted = requestAborted;
            _subscription = buffer.SubscribeFromCurrent();
        }

        public StreamEvent? Current => _current;

        public Exception? Err => null;

        public bool Next()
        {
            while (true)
            {
                var bufferClosed = false;

                while (true)
                {
                    var chunkIndex = _index;
                    var ok = _buffer.TryRead(
                        _index,
                        out var chunk,
                        out var nextIndex,
                        out var closed);
                    bufferClosed = closed;

                    if (!ok)
                    {
                        break;
                    }

                    _index = nextIndex;

                    if (chunk is null || IsPreviewTerminalChunk(chunk))
                    {
                        continue;
                    }

                    var eventType = chunkIndex < _subscription.ReplayUntil
                        ? ReplayEvent
                        : ChunkEvent;

                    _current = new StreamEvent
                    {
                        Type = eventType,
                        Data = chunk.Data,
                    };
                    return true;
                }

                if (bufferClosed && !_completed)
                {
                    _completed = true;
                    _current = new StreamEvent
                    {
                        Type = CompletedEvent,
                        Data = CompletedEventData,
                    };
                    return true;
                }

                if (_completed)
                {
                    _current = null;
                    return false;
                }

                bool signalled;
                try
                {
                    signalled = _subscription.Signal.Wait(IdleTimeout, _requestAborted);
                }
                catch (OperationCanceledException)
                {
                    _current = null;
                    return false;
                }

                if (!signalled)
                {
                    // request_live.go:247-250: an idle preview ends silently;
                    // the writer's incomplete-stream error reports it.
                    _current = null;
                    return false;
                }
            }
        }

        public void Dispose() => _subscription.Unsubscribe();

        private static bool IsPreviewTerminalChunk(StreamEvent chunk)
            => chunk.Data.AsSpan().SequenceEqual(DoneStreamEventData);
    }
}
