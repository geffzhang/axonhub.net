using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of the response half of orchestrator/pass_through.go: capture the raw
/// provider response/stream before the outbound transformer decodes it, then
/// replay it to the client after the inbound transform has built a response.
/// </summary>
/// <remarks>
/// The streaming fan-out mirrors the reference's goroutine: the provider stream
/// is consumed once and fanned out to the pipeline (so LLM middlewares still
/// run) and to the pass-through consumer. Events read before the consumer
/// attaches are held in a bounded backlog; the pipeline pre-read can otherwise
/// deadlock on the raw channel. Prompt protection is applied by the earlier
/// pass-through body middleware (C092).
/// </remarks>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The state is a per-attempt handle, not an owner: the fan-out thread completes both "
        + "channels and the attempt closer disposes the upstream stream. Disposal must be idempotent and "
        + "callable from the fan-out finally, so it lives in the closer rather than here.")]
internal sealed class PassThroughStreamState
{
    /// <summary>Raw provider events held for the pass-through consumer.</summary>
    public required BlockingCollection<StreamEvent> RawChannel { get; init; }

    /// <summary>Events captured before the pass-through consumer attached.</summary>
    public required RawStreamBacklog Backlog { get; init; }

    /// <summary>
    /// Bounded queue that owns the post-attachment raw channel and the held
    /// events (the reference's <c>state.RawStreamQueue</c>). Null in the legacy
    /// channel-only fixtures; production always sets it.
    /// </summary>
    public RawStreamQueue? RawQueue { get; init; }

    /// <summary>Terminal error of this attempt's fan-out thread.</summary>
    public required PassThroughErrorRef ErrorRef { get; init; }

    /// <summary>Cancels the attempt and closes the upstream stream.</summary>
    public required Action Cancel { get; init; }
}

/// <summary>
/// Per-attempt error slot shared by the fan-out thread and the pass-through
/// stream (the reference's <c>state.RawStreamErrRef</c>: a pointer to a
/// per-attempt local so an abandoned goroutine can never write into the next
/// attempt's error).
/// </summary>
internal sealed class PassThroughErrorRef
{
    private readonly object _sync = new();
    private Exception? _error;

    public Exception? Error
    {
        get
        {
            lock (_sync)
            {
                return _error;
            }
        }
    }

    /// <summary>Writes only when no earlier terminal error was recorded.</summary>
    public void SetIfNull(Exception? error)
    {
        lock (_sync)
        {
            _error ??= error;
        }
    }
}

/// <summary>
/// Port of rawStreamBacklog: holds raw provider events of one attempt until the
/// pass-through consumer attaches, bounded by count and bytes so a stream that
/// the pipeline filters away (for example Anthropic pings) cannot grow without
/// limit while the consumer is still unknown. After attachment the same budget
/// object tracks the live queue: events block on the post-attachment 128 MiB
/// budget and a single event above 32 MiB fails the attempt
/// (live pass_through.go:893-999).
/// </summary>
internal sealed class RawStreamBacklog
{
    internal const int MaxRawEventBytes = 32 * 1024 * 1024;
    internal const int MaxRawPreAttachEvents = 1024;
    internal const int MaxRawPreAttachBytes = 64 * 1024 * 1024;
    internal const int MaxRawQueueBytes = 128 * 1024 * 1024;

    private readonly object _sync = new();
    private readonly List<StreamEvent> _events = [];
    private bool _attached;
    private int _bytes;
    private int _count;
    private TaskCompletionSource<bool>? _waiter;

    /// <summary>
    /// Port of rawStreamBacklog.hold: keeps the event for the consumer that has
    /// not attached yet. Returns false (without an error) once attached, in
    /// which case the caller queues the event for the live consumer.
    /// </summary>
    public bool Hold(StreamEvent @event, out Exception? error)
    {
        error = null;

        lock (_sync)
        {
            if (_attached)
            {
                return false;
            }

            var size = EventSize(@event);
            var maxBytes = MaxRawPreAttachBytes;
            if (_count >= MaxRawPreAttachEvents
                || size > maxBytes - _bytes)
            {
                error = BudgetExceeded();
                return false;
            }

            _bytes += size;
            _count++;
            _events.Add(@event);
            return true;
        }
    }

    /// <summary>
    /// Port of rawStreamBacklog.attach: hands the held events to the consumer;
    /// every later event arrives on the raw channel, so the consumer prepends
    /// these to preserve order. The held events stay counted until the queue
    /// releases them (live raw_stream_queue.go:25-40).
    /// </summary>
    public StreamEvent[] Attach()
    {
        lock (_sync)
        {
            _attached = true;
            var events = _events.ToArray();
            _events.Clear();
            return events;
        }
    }

    /// <summary>
    /// Port of rawStreamBacklog.reserve: charges one event against the live
    /// budget. Before attachment an overflow fails immediately; afterwards it
    /// waits for the consumer to release capacity, observing cancellation like
    /// the reference's <c>waitCh</c> / <c>ctx.Done()</c> select.
    /// </summary>
    public void Reserve(CancellationToken cancellationToken, int size)
    {
        if (size > MaxRawEventBytes)
        {
            throw BudgetExceeded();
        }

        while (true)
        {
            TaskCompletionSource<bool>? waiter;

            lock (_sync)
            {
                var maxBytes = _attached ? MaxRawQueueBytes : MaxRawPreAttachBytes;
                if (_count < MaxRawPreAttachEvents && size <= maxBytes - _bytes)
                {
                    _bytes += size;
                    _count++;
                    return;
                }

                if (!_attached)
                {
                    throw BudgetExceeded();
                }

                waiter = _waiter ??= new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            }

            waiter.Task.Wait(cancellationToken);
        }
    }

    /// <summary>Port of rawStreamBacklog.release: frees one consumed event.</summary>
    public void Release(int size)
    {
        lock (_sync)
        {
            _bytes -= size;
            _count--;
            if (_waiter is { } waiter)
            {
                _waiter = null;
                waiter.TrySetResult(true);
            }
        }
    }

    internal static Exception BudgetExceeded()
        => GoErrors.Wrap(
            PipelineErrors.PreCommitBufferExceeded,
            "pass-through pre-attachment raw event budget exceeded: ");

    internal static int EventSize(StreamEvent @event)
        => @event.Data.Length + @event.Type.Length + (@event.LastEventId?.Length ?? 0);
}

/// <summary>
/// Port of rawStreamQueue (live raw_stream_queue.go): the post-attachment raw
/// channel plus the events held before attachment. Every queued event is
/// charged to <see cref="RawStreamBacklog"/> on enqueue and released when the
/// consumer reads it, so a slow client applies backpressure to the provider
/// reader instead of growing memory without bound.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "The queue is a per-attempt handle like PassThroughStreamState: the fan-out thread "
        + "completes the channel, and disposing it from either side would race the other. The backing "
        + "BlockingCollection is only ever completed, never disposed, matching the Go channel lifetime.")]
internal sealed class RawStreamQueue
{
    /// <summary>Go channel buffer size in the reference.</summary>
    public const int ChannelCapacity = 64;

    private readonly RawStreamBacklog _backlog;
    private readonly BlockingCollection<StreamEvent> _channel;
    private readonly object _heldSync = new();
    private readonly List<StreamEvent> _held = [];
    private readonly List<int> _heldSizes = [];

    public RawStreamQueue(RawStreamBacklog backlog)
    {
        _backlog = backlog;
        _channel = new BlockingCollection<StreamEvent>(
            new ConcurrentQueue<StreamEvent>(),
            ChannelCapacity);
    }

    /// <summary>The channel the fan-out writes and the consumer reads.</summary>
    public BlockingCollection<StreamEvent> Channel => _channel;

    /// <summary>
    /// Port of rawStreamQueue.attach: marks the backlog attached and moves its
    /// events ahead of the channel. The returned array mirrors the reference's
    /// return value; the consumer reads them through <see cref="Next"/>.
    /// </summary>
    public StreamEvent[] Attach()
    {
        var events = _backlog.Attach();

        lock (_heldSync)
        {
            _held.AddRange(events);
            foreach (var @event in events)
            {
                _heldSizes.Add(RawStreamBacklog.EventSize(@event));
            }
        }

        return events;
    }

    /// <summary>
    /// Port of rawStreamQueue.enqueue: reserve budget, then block on the bounded
    /// channel. Cancellation releases the reservation so a retry never leaks
    /// budget into the next attempt.
    /// </summary>
    public void Enqueue(CancellationToken cancellationToken, StreamEvent @event)
    {
        var size = RawStreamBacklog.EventSize(@event);
        _backlog.Reserve(cancellationToken, size);

        try
        {
            _channel.Add(@event, cancellationToken);
        }
        catch
        {
            _backlog.Release(size);
            throw;
        }
    }

    /// <summary>
    /// Port of rawStreamQueue.next: held events first, then the blocking
    /// channel. Returns null when the channel is completed; cancellation is
    /// propagated as <see cref="OperationCanceledException"/> so the consumer
    /// can drain what is already buffered.
    /// </summary>
    public StreamEvent? Next(CancellationToken cancellationToken)
    {
        if (TryTakeHeld(out var held))
        {
            return held;
        }

        if (_channel.TryTake(out var @event, Timeout.Infinite, cancellationToken))
        {
            _backlog.Release(RawStreamBacklog.EventSize(@event));
            return @event;
        }

        return null;
    }

    /// <summary>
    /// Port of rawStreamQueue.nextBuffered: never blocks on the producer, so
    /// already-buffered events survive a client disconnect.
    /// </summary>
    public StreamEvent? NextBuffered()
    {
        if (TryTakeHeld(out var held))
        {
            return held;
        }

        if (_channel.TryTake(out var @event))
        {
            _backlog.Release(RawStreamBacklog.EventSize(@event));
            return @event;
        }

        return null;
    }

    private bool TryTakeHeld(out StreamEvent @event)
    {
        lock (_heldSync)
        {
            if (_held.Count > 0)
            {
                @event = _held[0];
                _held.RemoveAt(0);
                var size = _heldSizes[0];
                _heldSizes.RemoveAt(0);
                _backlog.Release(size);
                return true;
            }
        }

        @event = null!;
        return false;
    }
}

/// <summary>
/// Port of passThroughChannelStream: a bounded channel exposed as a stream.
/// After cancellation it drains already-buffered events and never blocks on the
/// producer, so a terminal event racing the client disconnect is still seen.
/// When a <see cref="RawStreamQueue"/> is supplied it owns the held events and
/// the per-event budget releases.
/// </summary>
internal sealed class PassThroughChannelStream(
    CancellationToken requestToken,
    BlockingCollection<StreamEvent> channel,
    PassThroughErrorRef errorRef,
    Action? cancel,
    RawStreamQueue? queue = null) : IStream<StreamEvent>
{
    private StreamEvent? _current;
    private bool _canceled;
    private bool _closed;

    public StreamEvent? Current => _current;

    public Exception? Err
    {
        get
        {
            if (errorRef.Error is { } error)
            {
                return error;
            }

            return requestToken.IsCancellationRequested
                ? new OperationCanceledException(requestToken)
                : null;
        }
    }

    public bool Next()
    {
        if (_canceled || requestToken.IsCancellationRequested)
        {
            _canceled = true;
            return NextBuffered();
        }

        try
        {
            if (queue is not null)
            {
                var queued = queue.Next(requestToken);
                if (queued is null)
                {
                    return false;
                }

                _current = queued;
                return true;
            }

            if (channel.TryTake(out var @event, Timeout.Infinite, requestToken))
            {
                _current = @event;
                return true;
            }

            return false;
        }
        catch (OperationCanceledException)
        {
            _canceled = true;
            return NextBuffered();
        }
        catch (ObjectDisposedException)
        {
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
        cancel?.Invoke();
    }

    /// <summary>
    /// Consumes buffered events after cancellation was observed, then ends the
    /// stream (closing upstream) at the first empty-buffer moment.
    /// </summary>
    private bool NextBuffered()
    {
        var @event = queue is not null
            ? queue.NextBuffered()
            : channel.TryTake(out var buffered) ? buffered : null;

        if (@event is not null)
        {
            _current = @event;
            return true;
        }

        Dispose();
        return false;
    }
}

/// <summary>Port of captureRawProviderResponse.</summary>
internal sealed class CaptureRawProviderResponseMiddleware(
    PersistentOutboundTransformer outbound,
    PassThroughSettings settings)
    : DummyMiddleware("capture-raw-provider-response")
{
    public override HttpResponseData TransformOutboundRawResponse(HttpResponseData response)
    {
        ArgumentNullException.ThrowIfNull(response);

        // Adaptation and identity preservation need the untouched provider
        // payload even when the channel's pass-through setting is off.
        if (PassThrough.IsEnabled(outbound, settings)
            || PassThrough.TransportAdaptationEnabled(outbound)
            || PassThrough.IdentityPreservationEnabled(outbound))
        {
            outbound.RawProviderResponse = response;
        }

        return response;
    }
}

/// <summary>
/// Port of applyPassThroughResponse: replaces the transformed response with the
/// raw provider response when pass-through, Responses transport adaptation or
/// Responses identity preservation applies, unless the outbound transformer
/// vetoes it via <see cref="AxonHub.Llm.Transformer.IPassThroughResponsePolicy"/>.
/// </summary>
internal sealed class PassThroughResponseMiddleware(
    PersistentOutboundTransformer outbound,
    PassThroughSettings settings)
    : DummyMiddleware("pass-through-response")
{
    public override HttpResponseData TransformInboundRawResponse(HttpResponseData response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var passThrough = PassThrough.IsEnabled(outbound, settings);
        var adaptation = PassThrough.TransportAdaptationEnabled(outbound);
        var identity = PassThrough.IdentityPreservationEnabled(outbound);
        if (!passThrough && !adaptation && !identity)
        {
            return response;
        }

        if (!PassThrough.AllowPassThroughResponse(outbound))
        {
            return response;
        }

        var raw = outbound.RawProviderResponse;
        if (raw is null)
        {
            return response;
        }

        if (passThrough || identity)
        {
            var cloned = raw.ShallowClone();
            if (identity)
            {
                // Same transport: only the model the provider echoed back is
                // restored to the client's requested value.
                cloned.Body = PassThrough.PreserveResponsesModel(
                    raw.Body,
                    PassThrough.ResponsesClientModel(outbound));
            }

            return cloned;
        }

        var llmRequest = outbound.CurrentLlmRequest;
        var providerRequest = outbound.RawProviderRequest;
        if (llmRequest is null || providerRequest is null)
        {
            return response;
        }

        var source = llmRequest.ApiFormat;
        var target = providerRequest.ApiFormat;
        var body = ResponsesProtocolCodec.AdaptResponseBody(raw.Body, target, source);
        body = PassThrough.PreserveResponsesModel(body, PassThrough.ResponsesClientModel(outbound));

        var adapted = raw.ShallowClone();
        adapted.Body = body;
        // The inbound response owns the request plumbing the client sees.
        adapted.Request = response.Request;

        return adapted;
    }
}

/// <summary>Port of captureRawProviderStream.</summary>
internal sealed class CaptureRawProviderStreamMiddleware(
    PersistentOutboundTransformer outbound,
    PassThroughSettings settings,
    CancellationToken cancellationToken)
    : DummyMiddleware("capture-raw-provider-stream")
{
    /// <summary>
    /// Go channel buffer size in the reference. The raw side now lives on
    /// <see cref="RawStreamQueue"/>, which uses the same capacity.
    /// </summary>
    public const int ChannelCapacity = 64;

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The bounded channels live in PassThroughStreamState until the attempt ends; the "
            + "attempt closer disposes the upstream stream and cancels the linked token source, whose lifetime "
            + "the analyzer cannot follow through the fan-out thread.")]
    public override IStream<StreamEvent> TransformOutboundRawStream(IStream<StreamEvent> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!PassThrough.IsEnabled(outbound, settings)
            && !PassThrough.TransportAdaptationEnabled(outbound)
            && !PassThrough.IdentityPreservationEnabled(outbound))
        {
            return stream;
        }

        var pipelineChannel = new BlockingCollection<StreamEvent>(new ConcurrentQueue<StreamEvent>(), ChannelCapacity);
        var backlog = new RawStreamBacklog();
        var rawQueue = new RawStreamQueue(backlog);
        var errorRef = new PassThroughErrorRef();
        var closer = new PassThroughAttemptCloser(stream, cancellationToken);

        outbound.PassThroughStream = new PassThroughStreamState
        {
            RawChannel = rawQueue.Channel,
            RawQueue = rawQueue,
            Backlog = backlog,
            ErrorRef = errorRef,
            Cancel = closer.Close,
        };

        _ = Task.Factory.StartNew(
            () => FanOut(
                stream,
                pipelineChannel,
                rawQueue,
                backlog,
                errorRef,
                closer,
                cancellationToken),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        return new PassThroughChannelStream(cancellationToken, pipelineChannel, errorRef, closer.Close);
    }

    /// <summary>
    /// Port of the fan-out goroutine: one upstream read, one send to the
    /// pipeline, and (after attachment) one send to the pass-through consumer.
    /// Sends are blocking so a slow consumer never drops events.
    /// </summary>
    private static void FanOut(
        IStream<StreamEvent> source,
        BlockingCollection<StreamEvent> pipelineChannel,
        RawStreamQueue rawQueue,
        RawStreamBacklog backlog,
        PassThroughErrorRef errorRef,
        PassThroughAttemptCloser closer,
        CancellationToken requestToken)
    {
        try
        {
            var attemptToken = closer.Token;

            while (true)
            {
                if (attemptToken.IsCancellationRequested)
                {
                    return;
                }

                if (!source.Next())
                {
                    return;
                }

                var @event = source.Current!;
                var held = backlog.Hold(@event, out var holdError);
                if (holdError is not null)
                {
                    errorRef.SetIfNull(holdError);
                    return;
                }

                try
                {
                    pipelineChannel.Add(@event, attemptToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (held)
                {
                    continue;
                }

                try
                {
                    rawQueue.Enqueue(attemptToken, @event);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Budget overflow (single event > 32 MiB or a full queue
                    // that cannot be charged) fails the attempt, matching the
                    // reference's rawStreamErr assignment.
                    errorRef.SetIfNull(ex);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // The reference recovers a panic in the goroutine and records it as
            // the attempt's stream error.
            errorRef.SetIfNull(new InvalidOperationException("passthrough stream panic: " + ex.Message, ex));
        }
        finally
        {
            // Deferred closeStream runs before the error capture and channel
            // close in the reference, so an upstream terminal error is read
            // after the connection is released.
            closer.Close();
            errorRef.SetIfNull(
                source.Err
                ?? (requestToken.IsCancellationRequested
                    ? new OperationCanceledException(requestToken)
                    : null));
            pipelineChannel.CompleteAdding();
            rawQueue.Channel.CompleteAdding();
            closer.Dispose();
        }
    }

    /// <summary>
    /// Owns the per-attempt cancelable token and upstream stream. Mirrors Go's
    /// <c>sync.Once</c> + <c>WithCancel</c> pair: Close is idempotent and may be
    /// called from the consumer while the fan-out thread is still running.
    /// </summary>
    private sealed class PassThroughAttemptCloser : IDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly IStream<StreamEvent> _upstream;
        private readonly object _sync = new();
        private bool _closed;
        private bool _disposed;

        public PassThroughAttemptCloser(IStream<StreamEvent> upstream, CancellationToken requestToken)
        {
            _upstream = upstream;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        }

        public CancellationToken Token => _cts.Token;

        public void Close()
        {
            lock (_sync)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                _cts.Cancel();
                _upstream.Dispose();
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                if (!_closed)
                {
                    _closed = true;
                    _cts.Cancel();
                    _upstream.Dispose();
                }

                _disposed = true;
                _cts.Dispose();
            }
        }
    }
}

/// <summary>
/// Port of applyPassThroughStream: hands the raw provider events to the client
/// and drains the transformed stream in the background so LLM middlewares
/// (connection tracking, performance recording, rate-limit tracking) still see
/// every event.
/// </summary>
internal sealed class PassThroughStreamMiddleware(
    PersistentOutboundTransformer outbound,
    PassThroughSettings settings,
    CancellationToken cancellationToken)
    : DummyMiddleware("pass-through-response-stream")
{
    public override IStream<StreamEvent> TransformInboundRawStream(IStream<StreamEvent> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var adaptation = PassThrough.TransportAdaptationEnabled(outbound);
        var identity = PassThrough.IdentityPreservationEnabled(outbound);
        if (!PassThrough.IsEnabled(outbound, settings) && !adaptation && !identity)
        {
            return stream;
        }

        var state = outbound.PassThroughStream;
        if (state is null)
        {
            return stream;
        }

        DrainInBackground(stream);

        IStream<StreamEvent> result;
        if (state.RawQueue is { } queue)
        {
            // The queue owns both the pre-attachment events and the live
            // channel; every read releases that event's budget (live
            // raw_stream_queue.go:25-100).
            queue.Attach();
            result = new PassThroughChannelStream(
                cancellationToken,
                queue.Channel,
                state.ErrorRef,
                state.Cancel,
                queue);
        }
        else
        {
            var held = state.Backlog.Attach();
            var rawStream = new PassThroughChannelStream(
                cancellationToken,
                state.RawChannel,
                state.ErrorRef,
                state.Cancel);

            result = held.Length == 0
                ? rawStream
                : new PrependStream<StreamEvent>(rawStream, held);
        }

        // The transport adapter runs first (source -> target), then identity
        // preservation restores the client's model in the adapted payload.
        if (adaptation)
        {
            result = new ResponsesTransportStream(
                result,
                outbound.RawProviderRequest!.ApiFormat,
                outbound.CurrentLlmRequest!.ApiFormat ?? string.Empty);
        }

        if (adaptation || identity)
        {
            result = new ResponsesIdentityStream(
                result,
                PassThrough.ResponsesClientModel(outbound));
        }

        return result;
    }

    /// <summary>
    /// Port of the drain goroutine: the transformed stream is discarded, but
    /// consuming it is what keeps the LLM middlewares and persistence alive.
    /// </summary>
    private static void DrainInBackground(IStream<StreamEvent> stream)
    {
        _ = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    while (stream.Next())
                    {
                        _ = stream.Current;
                    }
                }
                catch (Exception)
                {
                    // The reference recovers a panic here too; the raw stream
                    // carries the client-visible result.
                }
                finally
                {
                    stream.Dispose();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }
}

/// <summary>
/// Port of responsesTransportStream: validates and normalizes each raw provider
/// event for the client's Responses transport without rebuilding provider
/// items. An adaptation failure becomes the stream's terminal error and stops
/// delivery, exactly like the reference's wrapper.
/// </summary>
internal sealed class ResponsesTransportStream(
    IStream<StreamEvent> stream,
    string source,
    string target) : IStream<StreamEvent>
{
    private StreamEvent? _current;
    private Exception? _error;

    public StreamEvent? Current => _current;

    public Exception? Err => _error ?? stream.Err;

    public bool Next()
    {
        if (_error is not null || !stream.Next())
        {
            return false;
        }

        try
        {
            _current = ResponsesProtocolCodec.AdaptStreamEvent(stream.Current, source, target);
        }
        catch (Exception ex)
        {
            _error = ex;
            return false;
        }

        return true;
    }

    public void Dispose() => stream.Dispose();
}

/// <summary>
/// Port of responsesIdentityStream: restores the client's requested model in
/// every provider event it echoes back (the provider may have rewritten it to
/// the upstream model id).
/// </summary>
internal sealed class ResponsesIdentityStream(
    IStream<StreamEvent> stream,
    string clientModel) : IStream<StreamEvent>
{
    private StreamEvent? _current;

    public StreamEvent? Current => _current;

    public Exception? Err => stream.Err;

    public bool Next()
    {
        if (!stream.Next())
        {
            return false;
        }

        _current = PassThrough.PreserveResponsesStreamEventModel(stream.Current, clientModel);
        return true;
    }

    public void Dispose() => stream.Dispose();
}
