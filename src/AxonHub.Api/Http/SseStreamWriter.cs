using System.Text;
using System.Threading.Channels;
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

    /// <summary>
    /// chat.go:605-618: every SSE write refreshes a 30 second response write
    /// deadline. It is deliberately not tied to the request context, so the
    /// client still receives the deadline error after the request ctx expired.
    /// </summary>
    public static readonly TimeSpan SseWriteTimeout = TimeSpan.FromSeconds(30);

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
            await FlushWithWriteDeadlineAsync(context);
        }
        catch (Exception ex) when (IsClientWriteFailure(ex))
        {
            return;
        }

        // The stream's own capability is authoritative when it has one
        // (InboundPersistentStream in the reference); the argument remains a
        // seam for callers that have a plain stream.
        var streamExpectedChoices = DeliveryCapabilities.ExpectedStreamChoicesOf(stream);
        var tracker = new StreamTerminalTracker(
            streamExpectedChoices != 0 ? streamExpectedChoices : expectedChoices);
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

            // chat.go:356-371: the terminal tracker observes only after the
            // event was written successfully, and the write runs inside the
            // stream's delivery boundary so the acknowledgement marks exactly
            // the terminal event the client received.
            var writeError = await stream.DeliverAsync(
                async () =>
                {
                    try
                    {
                        await WriteEventAsync(context, streamEvent).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsClientWriteFailure(ex))
                    {
                        return new DeliveryWrite(false, ex);
                    }

                    var terminal = !terminalSeen && tracker.Observe(streamEvent);
                    if (terminal)
                    {
                        terminalSeen = true;
                    }

                    return new DeliveryWrite(terminal, null);
                },
                cancellationToken).ConfigureAwait(false);

            if (writeError is not null)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Port of writeEncodedSSEStreamWithHeartbeat (api/chat.go:396-500). The
    /// stream is read on a helper task so an idle upstream can still be kept
    /// alive; every delivered event restarts the interval, and a write failure
    /// on the heartbeat is treated as a client disconnect.
    /// </summary>
    public static async Task WriteWithHeartbeatAsync(
        HttpContext context,
        IStream<StreamEvent> stream,
        int expectedChoices,
        CancellationToken cancellationToken,
        Func<Exception, CancellationToken, Task<Exception?>>? transformStreamError,
        SseKeepAliveSettings keepAlive,
        SseHeartbeatFormat heartbeatFormat)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(keepAlive);

        SetHeaders(context.Response);

        try
        {
            await FlushWithWriteDeadlineAsync(context);
        }
        catch (Exception ex) when (IsClientWriteFailure(ex))
        {
            return;
        }

        var streamExpectedChoices = DeliveryCapabilities.ExpectedStreamChoicesOf(stream);
        var tracker = new StreamTerminalTracker(
            streamExpectedChoices != 0 ? streamExpectedChoices : expectedChoices);
        var terminalSeen = false;
        var eventsAfterCancel = 0;
        var timerActive = keepAlive.Enabled;

        var reader = new SseStreamReader(stream);
        var cancelWait = cancellationToken.CanBeCanceled
            ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
            : null;

        try
        {
            Task<SseStreamReadResult>? pendingRead = null;

            while (true)
            {
                if (timerActive && cancellationToken.IsCancellationRequested)
                {
                    // chat.go:432-438: once the request context is done the
                    // heartbeat timer is stopped and the remaining buffered
                    // events are drained.
                    timerActive = false;
                }

                pendingRead ??= reader.ReadAsync();

                if (timerActive)
                {
                    using var heartbeatDelay = new CancellationTokenSource();
                    var heartbeatTask = Task.Delay(keepAlive.Interval, heartbeatDelay.Token);
                    var completed = cancelWait is null
                        ? await Task.WhenAny(pendingRead, heartbeatTask).ConfigureAwait(false)
                        : await Task.WhenAny(pendingRead, heartbeatTask, cancelWait).ConfigureAwait(false);

                    if (completed == cancelWait)
                    {
                        timerActive = false;
                        continue;
                    }

                    if (completed == heartbeatTask)
                    {
                        if (!await TryWriteHeartbeatAsync(context, heartbeatFormat).ConfigureAwait(false))
                        {
                            return;
                        }

                        continue;
                    }

                    // The read won the race; cancel the pending delay so it does
                    // not keep a timer rooted until the interval elapses.
                    await heartbeatDelay.CancelAsync().ConfigureAwait(false);
                }

                var result = await pendingRead.ConfigureAwait(false);
                pendingRead = null;

                if (result.Done)
                {
                    await WriteStreamEndAsync(
                        context,
                        result.Error,
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

                if (result.Event is not { } streamEvent)
                {
                    continue;
                }

                var writeError = await stream.DeliverAsync(
                    async () =>
                    {
                        try
                        {
                            await WriteEventAsync(context, streamEvent).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (IsClientWriteFailure(ex))
                        {
                            return new DeliveryWrite(false, ex);
                        }

                        var terminal = !terminalSeen && tracker.Observe(streamEvent);
                        if (terminal)
                        {
                            terminalSeen = true;
                        }

                        return new DeliveryWrite(terminal, null);
                    },
                    cancellationToken).ConfigureAwait(false);

                if (writeError is not null)
                {
                    return;
                }
            }
        }
        finally
        {
            // chat.go:425-427: wait for the blocking reader before the caller
            // closes the stream, so Next/Current never race with Close.
            reader.Stop();
        }
    }

    private static async Task<bool> TryWriteHeartbeatAsync(
        HttpContext context,
        SseHeartbeatFormat format)
    {
        var payload = format switch
        {
            SseHeartbeatFormat.OpenAi => ": keep-alive\n\n",
            SseHeartbeatFormat.Anthropic => "event: ping\ndata: {\"type\":\"ping\"}\n\n",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

        try
        {
            await WriteWithDeadlineAsync(context, Encoding.UTF8.GetBytes(payload)).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            // writeSSEHeartbeatEvent: any heartbeat write error is treated like
            // a disconnected client and ends the stream.
            return false;
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

        // chat.go:474-481: the server deadline wins over a missing/cancellation
        // stream error and is reported to the client as context.DeadlineExceeded.
        // The write uses its own 30 second deadline because the request token is
        // already canceled at this point.
        var timedOut = context.Features.Get<IRequestTimeoutFeature>()?.IsTimedOut == true;
        if (timedOut && (streamError is null || IsCancellation(streamError)))
        {
            await TryWriteErrorEventAsync(
                context,
                new TimeoutException("context deadline exceeded"));
            return;
        }

        // writeSSEErrorEvent always classifies first: a broken upstream
        // connection reaches the client as a 502 upstream_stream_interrupted
        // error object, never as a bare exception message.
        if (streamError is not null)
        {
            // chat.go:482-488 suppresses the event for context.Canceled or a
            // canceled request ctx (client disconnected). A deadline that left
            // a real stream error is not a client disconnect, so it falls
            // through to the error event.
            if (IsCancellation(streamError)
                || (cancellationToken.IsCancellationRequested && !timedOut))
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
                UpstreamTransportErrors.Classify(reported));
            return;
        }

        // chat.go:496-497: ctx.Err() == context.Canceled means the client is
        // gone; a deadline already returned through the branch above.
        if (cancellationToken.IsCancellationRequested && !timedOut)
        {
            return;
        }

        // The reference reports orchestrator.ErrStreamIncomplete (not the
        // llm-level sentinel): the stream ended without a terminal event or a
        // completed aggregated response.
        await TryWriteErrorEventAsync(
            context,
            UpstreamTransportErrors.Classify(StreamOutcome.ErrStreamIncomplete));
    }

    /// <summary>Port of writeSSEErrorEvent + encodeStreamError.</summary>
    private static async Task TryWriteErrorEventAsync(
        HttpContext context,
        Exception error)
    {
        try
        {
            await WriteEventAsync(
                context,
                new StreamEvent { Type = "error", Data = StreamErrorFormatter.Format(error) });
        }
        catch (Exception ex) when (IsClientWriteFailure(ex))
        {
            // Client is gone; the reference only logs the failed write.
        }
    }

    /// <summary>Port of writeSSEEvent + gin-contrib/sse Encode for a byte payload.</summary>
    private static async Task WriteEventAsync(
        HttpContext context,
        StreamEvent streamEvent)
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

        await WriteWithDeadlineAsync(context, buffer.ToArray());
    }

    /// <summary>writeAndFlushSSE: one 30 second response write deadline per flush.</summary>
    private static async Task WriteWithDeadlineAsync(HttpContext context, byte[] payload)
    {
        using var writeCts = new CancellationTokenSource(SseWriteTimeout);
        await context.Response.Body.WriteAsync(payload, writeCts.Token);
        await context.Response.Body.FlushAsync(writeCts.Token);
    }

    private static async Task FlushWithWriteDeadlineAsync(HttpContext context)
    {
        using var writeCts = new CancellationTokenSource(SseWriteTimeout);
        await context.Response.Body.FlushAsync(writeCts.Token);
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

    /// <summary>Port of sseStreamReadResult (api/sse_stream_reader.go:14-18).</summary>
    private sealed record SseStreamReadResult(StreamEvent? Event, Exception? Error, bool Done);

    /// <summary>
    /// Port of sseStreamReader (api/sse_stream_reader.go): keeps the blocking
    /// Next/Current pair off the response-writer loop. A panic in the reader is
    /// surfaced as the reference's "stream reader stopped unexpectedly" error.
    /// </summary>
    private sealed class SseStreamReader : IDisposable
    {
        private readonly Channel<SseStreamReadResult> _results;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _done;

        public SseStreamReader(IStream<StreamEvent> stream)
        {
            ArgumentNullException.ThrowIfNull(stream);

            _results = Channel.CreateBounded<SseStreamReadResult>(
                new BoundedChannelOptions(1)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait,
                });

            _done = Task.Run(() => Run(stream));
        }

        public Task<SseStreamReadResult> ReadAsync()
            => _results.Reader.ReadAsync().AsTask();

        public void Stop()
        {
            _stop.Cancel();
            try
            {
                _done.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // The reader stopped through the stop token.
            }
        }

        public void Dispose()
        {
            _stop.Dispose();
        }

        private void Run(IStream<StreamEvent> stream)
        {
            try
            {
                while (stream.Next())
                {
                    if (stream.Current is not { } current)
                    {
                        continue;
                    }

                    if (!Send(new SseStreamReadResult(current, null, false)))
                    {
                        return;
                    }
                }

                Send(new SseStreamReadResult(null, stream.Err, true));
            }
            catch (Exception ex)
            {
                // sse_stream_reader.go:60-70: a panic becomes a terminal error
                // instead of tearing down the response loop.
                Send(new SseStreamReadResult(
                    null,
                    new InvalidOperationException("stream reader stopped unexpectedly", ex),
                    true));
            }
        }

        private bool Send(SseStreamReadResult result)
        {
            try
            {
                _results.Writer.WriteAsync(result, _stop.Token).AsTask().GetAwaiter().GetResult();
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (ChannelClosedException)
            {
                return false;
            }
        }
    }
}
