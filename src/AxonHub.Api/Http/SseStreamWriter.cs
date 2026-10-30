using System.Text;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of api.writeSSEStreamWithoutHeartbeat (internal/server/api/chat.go:270-331)
/// and its helpers: SSE headers, gin-contrib/sse event encoding, per-event flush,
/// terminal-event tracking and the trailing error event.
/// </summary>
public static class SseStreamWriter
{
    /// <summary>gin-contrib/sse ContentType.</summary>
    public const string ContentType = "text/event-stream;charset=utf-8";

    /// <summary>Bounds how many events are drained after cancellation.</summary>
    public const int MaxEventsAfterCancel = 256;

    public static async Task WriteAsync(
        HttpContext context,
        IStream<StreamEvent> stream,
        int expectedChoices,
        CancellationToken cancellationToken,
        Func<Exception, CancellationToken, Task<Exception?>>? transformStreamError = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stream);

        SetHeaders(context.Response);

        try
        {
            await context.Response.Body.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when (IsClientWriteFailure(ex))
        {
            return;
        }

        var tracker = new StreamTerminalTracker(expectedChoices);
        var terminalSeen = false;
        var eventsAfterCancel = 0;

        while (true)
        {
            if (!stream.Next())
            {
                await WriteStreamEndAsync(
                    context,
                    stream.Err,
                    terminalSeen,
                    transformStreamError,
                    cancellationToken);
                return;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                eventsAfterCancel++;
                if (eventsAfterCancel > MaxEventsAfterCancel)
                {
                    await WriteStreamEndAsync(
                        context,
                        new OperationCanceledException(cancellationToken),
                        terminalSeen,
                        transformStreamError,
                        cancellationToken);
                    return;
                }
            }

            if (stream.Current is not { } streamEvent)
            {
                continue;
            }

            if (tracker.Observe(streamEvent))
            {
                terminalSeen = true;
            }

            try
            {
                await WriteEventAsync(context, streamEvent, cancellationToken);
            }
            catch (Exception ex) when (IsClientWriteFailure(ex))
            {
                return;
            }
        }
    }

    private static void SetHeaders(HttpResponse response)
    {
        response.ContentType = ContentType;
        response.Headers["Cache-Control"] = "no-cache";
        response.Headers["Connection"] = "keep-alive";
    }

    /// <summary>Port of writeSSEStreamEnd.</summary>
    private static async Task WriteStreamEndAsync(
        HttpContext context,
        Exception? streamError,
        bool terminalSeen,
        Func<Exception, CancellationToken, Task<Exception?>>? transformStreamError,
        CancellationToken cancellationToken)
    {
        if (terminalSeen)
        {
            // A terminal event already told the client how the stream ended;
            // a trailing transport error is suppressed.
            return;
        }

        // writeSSEErrorEvent always classifies first: a broken upstream
        // connection reaches the client as a 502 upstream_stream_interrupted
        // error object, never as a bare exception message.
        if (streamError is not null)
        {
            // The reference suppresses the event when the error is
            // context.Canceled or the request context is already canceled
            // (client disconnected).
            if (IsCancellation(streamError) || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // Port of newUpstreamErrorStream: classify and apply the upstream
            // error policy to the trailing stream error, if a policy transformer
            // was supplied. The writer still classifies afterwards, which is a
            // no-op for an already classified result, as in writeSSEErrorEvent.
            var reported = transformStreamError is null
                ? streamError
                : await transformStreamError(streamError, cancellationToken).ConfigureAwait(false) ?? streamError;

            await TryWriteErrorEventAsync(
                context,
                UpstreamTransportErrors.Classify(reported),
                cancellationToken);
            return;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        // The reference reports orchestrator.ErrStreamIncomplete (not the
        // llm-level sentinel): the stream ended without a terminal event or a
        // completed aggregated response.
        await TryWriteErrorEventAsync(
            context,
            UpstreamTransportErrors.Classify(StreamOutcome.ErrStreamIncomplete),
            cancellationToken);
    }

    /// <summary>Port of writeSSEErrorEvent + encodeStreamError.</summary>
    private static async Task TryWriteErrorEventAsync(
        HttpContext context,
        Exception error,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteEventAsync(
                context,
                new StreamEvent { Type = "error", Data = StreamErrorFormatter.Format(error) },
                cancellationToken);
        }
        catch (Exception ex) when (IsClientWriteFailure(ex))
        {
            // Client is gone; the reference only logs the failed write.
        }
    }

    /// <summary>Port of writeSSEEvent + gin-contrib/sse Encode for a byte payload.</summary>
    private static async Task WriteEventAsync(
        HttpContext context,
        StreamEvent streamEvent,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();

        if (!string.IsNullOrEmpty(streamEvent.Type))
        {
            WriteString(buffer, "event:");
            WriteString(buffer, EscapeField(streamEvent.Type));
            WriteString(buffer, "\n");
        }

        WriteString(buffer, "data:");
        WriteData(buffer, streamEvent.Data);
        WriteString(buffer, "\n\n");

        await context.Response.Body.WriteAsync(buffer.ToArray(), cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    /// <summary>Go dataReplacer: <c>\n</c> becomes <c>\ndata:</c>, <c>\r</c> becomes <c>\r</c>.</summary>
    private static void WriteData(MemoryStream buffer, byte[] data)
    {
        foreach (var value in data)
        {
            switch (value)
            {
                case (byte)'\n':
                    WriteString(buffer, "\ndata:");
                    break;
                case (byte)'\r':
                    WriteString(buffer, "\\r");
                    break;
                default:
                    buffer.WriteByte(value);
                    break;
            }
        }
    }

    /// <summary>Go fieldReplacer: newlines and carriage returns become escaped text.</summary>
    private static string EscapeField(string value)
        => value.Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal);

    private static void WriteString(MemoryStream buffer, string value)
        => buffer.Write(Encoding.UTF8.GetBytes(value));

    private static bool IsCancellation(Exception? error)
    {
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsClientWriteFailure(Exception error)
        => error is IOException or OperationCanceledException or ObjectDisposedException;
}
