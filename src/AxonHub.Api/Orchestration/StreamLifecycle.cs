using System.Text;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of orchestrator.streamLifecycle
/// (internal/server/orchestrator/stream_lifecycle.go): separates downstream
/// cancellation from the execution context. Before acknowledgement, downstream
/// cancellation aborts execution; after acknowledgement it only opens the
/// bounded collection window that finishes accounting.
/// </summary>
/// <remarks>
/// The reference's mutex also serialises <c>Deliver</c> against
/// <c>AcknowledgeDelivery</c>, <c>Close</c> and the downstream watcher, so a
/// terminal write that is already in flight still wins the acknowledgement
/// race. A <see cref="SemaphoreSlim"/> reproduces that lock across the awaitable
/// write callback. Cancellation callbacks never block: they publish one event
/// and the watcher task performs the state transition.
/// </remarks>
public sealed class StreamLifecycle
    : IDeliveryAcknowledger, IDeliveryBoundary, IFinalizationWaiter, IDisposable
{
    /// <summary>streamTailTimeout: how long a tail may collect and wait for a reader.</summary>
    internal static readonly TimeSpan TailTimeout = TimeSpan.FromSeconds(1);

    /// <summary>streamTailEventLimit.</summary>
    internal const int TailEventLimit = 256;

    /// <summary>streamTailPayloadLimit.</summary>
    internal const int TailPayloadLimit = 1 << 20;

    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationToken _downstream;
    private readonly Func<Exception?>? _downstreamCause;
    private readonly CancellationToken _shutdown;
    private readonly Func<Exception?>? _shutdownCause;
    private readonly CancellationTokenSource _execution = new();
    private readonly TaskCompletionSource _watchEvent =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopWatch =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _done =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _finalized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _watcher;

    private bool _acknowledged;
    private bool _canceled;
    private bool _closed;
    private Exception? _cancelCause;
    private StreamTailOwner? _tail;

    /// <summary>Port of newStreamLifecycle.</summary>
    /// <param name="downstream">The client request's token (Go: the request ctx).</param>
    /// <param name="downstreamCause">
    /// Builds the error Go's <c>ctx.Err()</c> would report, so an expired server
    /// deadline stays distinguishable from a client disconnect.
    /// </param>
    /// <param name="shutdown">
    /// The application shutdown token. The port has no lifecycle manager yet,
    /// so production callers leave it default (C255).
    /// </param>
    /// <param name="shutdownCause">Builds the shutdown cause.</param>
    public StreamLifecycle(
        CancellationToken downstream,
        Func<Exception?>? downstreamCause = null,
        CancellationToken shutdown = default,
        Func<Exception?>? shutdownCause = null)
    {
        _downstream = downstream;
        _downstreamCause = downstreamCause;
        _shutdown = shutdown;
        _shutdownCause = shutdownCause;
        _watcher = WatchAsync();
    }

    /// <summary>
    /// The execution token (Go: the lifecycle's derived context). Pipeline and
    /// transport work observe this token, not the client request's token.
    /// </summary>
    public CancellationToken ExecutionToken => _execution.Token;

    /// <summary>
    /// Go: <c>execution.Err()</c>. Null while the execution context is alive,
    /// otherwise the first recorded cancellation cause.
    /// </summary>
    public Exception? ExecutionError
    {
        get
        {
            lock (_sync)
            {
                return _execution.IsCancellationRequested
                    ? _cancelCause ?? new OperationCanceledException()
                    : null;
            }
        }
    }

    /// <summary>Port of streamLifecycle.registerTailOwner.</summary>
    internal void RegisterTailOwner(StreamTailOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        bool acknowledged;
        lock (_sync)
        {
            _tail = owner;
            acknowledged = _acknowledged;
        }

        if (acknowledged)
        {
            StartTail(owner, cause: null);
        }
    }

    /// <summary>
    /// Port of streamLifecycle.waitForAccounting: a retry must not start while
    /// the previous attempt still owns the source. Starting the tail cancels the
    /// abandoned source and waits for its accounting to finish.
    /// </summary>
    internal void WaitForAccounting()
    {
        StreamTailOwner? tail;
        lock (_sync)
        {
            tail = _tail;
        }

        if (tail is null)
        {
            return;
        }

        StartTail(tail, new OperationCanceledException());
        tail.AccountingDone.Task.GetAwaiter().GetResult();
    }

    /// <summary>Port of streamLifecycle.startTail.</summary>
    internal void StartTail(StreamTailOwner owner, Exception? cause)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (Interlocked.Exchange(ref owner.Started, 1) != 0)
        {
            return;
        }

        var tailCts = CancellationTokenSource.CreateLinkedTokenSource(_execution.Token);
        tailCts.CancelAfter(TailTimeout);

        // A dedicated thread matches the reference's goroutine: collection may
        // block inside Next until the abort closes the source.
        _ = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    owner.Run(tailCts.Token, cause);
                }
                finally
                {
                    owner.AccountingDone.TrySetResult();
                    tailCts.Dispose();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>Port of streamLifecycle.AcknowledgeDelivery.</summary>
    public void AcknowledgeDelivery() => TryAcknowledgeDelivery();

    /// <summary>Port of streamLifecycle.AcknowledgeDelivery, returning whether this call acknowledged.</summary>
    public bool TryAcknowledgeDelivery()
    {
        // Deliver holds this gate across the terminal write and records
        // successful delivery before releasing it, exactly like the reference's
        // mutex.
        _gate.Wait();
        try
        {
            lock (_sync)
            {
                if (_closed || _acknowledged || _canceled || _downstream.IsCancellationRequested)
                {
                    return false;
                }

                _acknowledged = true;
                return true;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Port of streamLifecycle.Deliver.</summary>
    public async Task<Exception?> DeliverAsync(
        Func<Task<DeliveryWrite>> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        // Go's mutex has no cancellation; the gate is released by the finally
        // below even when the caller's token is already canceled.
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_closed || _canceled)
                {
                    return new OperationCanceledException("stream lifecycle canceled");
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return CancellationError(cancellationToken);
            }

            if (_shutdown.IsCancellationRequested)
            {
                return _shutdownCause?.Invoke() ?? new OperationCanceledException(_shutdown);
            }

            var result = await write().ConfigureAwait(false);
            if (result.Error is null && result.Terminal)
            {
                // The reference records the acknowledgement under the same lock
                // it held for the write, so a concurrent cancellation cannot
                // demote a delivered terminal event.
                lock (_sync)
                {
                    _acknowledged = true;
                }
            }

            return result.Error;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Port of streamLifecycle.DeliveryAcknowledged.</summary>
    public bool DeliveryAcknowledged
    {
        get
        {
            lock (_sync)
            {
                return _acknowledged;
            }
        }
    }

    /// <summary>Port of streamLifecycle.Close.</summary>
    public void Close()
    {
        StreamTailOwner? tail;
        bool acknowledged;

        _gate.Wait();
        try
        {
            lock (_sync)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                acknowledged = _acknowledged;
                tail = _tail;

                if (!_canceled && (!acknowledged || tail is null))
                {
                    _canceled = true;
                    _cancelCause ??= new OperationCanceledException();
                    _execution.Cancel();
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (tail is not null && acknowledged)
        {
            StartTail(tail, cause: null);
            tail.Done.Task.GetAwaiter().GetResult();
        }
        else if (tail is not null)
        {
            // The tail must not touch the raw source until the consumer has
            // both finished its in-flight read and stopped iterating.
            tail.Abort?.Invoke();
            AwaitTailBarrier(CancellationToken.None, abort: null, tail.ReaderDone);
            if (tail.ReaderDoneFunc is not null)
            {
                AwaitTailBarrier(CancellationToken.None, abort: null, tail.ReaderDoneFunc());
            }

            StartTail(tail, new OperationCanceledException());
            tail.Done.Task.GetAwaiter().GetResult();
        }

        _stopWatch.TrySetResult();
        _done.Task.GetAwaiter().GetResult();
        _finalized.TrySetResult();
    }

    /// <summary>Port of streamLifecycle.WaitForFinalization.</summary>
    public void WaitForFinalization() => _finalized.Task.GetAwaiter().GetResult();

    /// <summary>Dispose request that equals the reference's Close.</summary>
    public void Dispose() => Close();

    /// <summary>
    /// Stops the downstream watcher without closing the lifecycle, mirroring the
    /// reference test's <c>stopOnce</c>/<c>done</c> pair. Used to prove that a
    /// direct acknowledgement is rejected by a cancellation the watcher never
    /// observes.
    /// </summary>
    internal void StopWatcher()
    {
        _stopWatch.TrySetResult();
        _done.Task.GetAwaiter().GetResult();
    }

    /// <summary>Port of streamLifecycle.watch.</summary>
    private async Task WatchAsync()
    {
        var downstreamRegistration = _downstream.Register(
            static state => ((StreamLifecycle)state!).NotifyWatch(fromShutdown: false),
            this);
        var shutdownRegistration = _shutdown.Register(
            static state => ((StreamLifecycle)state!).NotifyWatch(fromShutdown: true),
            this);

        try
        {
            await Task.WhenAny(_stopWatch.Task, _watchEvent.Task).ConfigureAwait(false);

            if (_watchEvent.Task.IsCompleted && !_stopWatch.Task.IsCompleted)
            {
                // The transition runs on this task, never on the cancellation
                // callback, so waiting for an in-flight delivery is safe.
                var fromShutdown = Volatile.Read(ref _watchFromShutdown) == 1;
                DownstreamEnded(fromShutdown ? ShutdownCause() : DownstreamCause(), fromShutdown);
            }
        }
        catch (Exception)
        {
            // The reference logs a watcher panic and still releases done.
        }
        finally
        {
            // Disposing a registration waits for a running callback, so done is
            // never published while a notification is still being recorded.
            downstreamRegistration.Dispose();
            shutdownRegistration.Dispose();
            _done.TrySetResult();
        }
    }

    private int _watchFromShutdown;

    private void NotifyWatch(bool fromShutdown)
    {
        if (fromShutdown)
        {
            Volatile.Write(ref _watchFromShutdown, 1);
        }

        _watchEvent.TrySetResult();
    }

    private Exception DownstreamCause()
        => _downstreamCause?.Invoke() ?? new OperationCanceledException(_downstream);

    private Exception ShutdownCause()
        => _shutdownCause?.Invoke() ?? new OperationCanceledException(_shutdown);

    /// <summary>Port of streamLifecycle.downstreamEnded.</summary>
    private void DownstreamEnded(Exception? cause, bool fromShutdown)
    {
        StreamTailOwner? tail;
        Exception? tailCause;
        var shutdown = fromShutdown && _shutdown.IsCancellationRequested;

        // The reference takes the same mutex Deliver holds for the duration of
        // the terminal write; a cancellation that arrives mid-write waits.
        _gate.Wait();
        try
        {
            lock (_sync)
            {
                if (_closed || _canceled)
                {
                    return;
                }

                tail = _tail;

                if (_acknowledged && cause is not null && !shutdown && !IsDeadline(cause))
                {
                    // Acknowledged downstream loss only opens the accounting
                    // window; it must not abort the execution.
                    tailCause = null;
                }
                else
                {
                    _canceled = true;
                    _cancelCause = cause ?? new OperationCanceledException();
                    _execution.Cancel();
                    tailCause = _cancelCause;
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        if (tail is not null)
        {
            StartTail(tail, tailCause);
        }
    }

    private static bool IsDeadline(Exception cause)
        => StreamOutcome.HasTimeoutCause(cause);

    private Exception CancellationError(CancellationToken token)
    {
        if (token == _downstream && _downstreamCause?.Invoke() is { } cause)
        {
            return cause;
        }

        return new OperationCanceledException(token);
    }

    /// <summary>Port of awaitTailBarrier.</summary>
    internal static void AwaitTailBarrier(CancellationToken context, Action? abort, Task? barrier)
    {
        if (barrier is null)
        {
            return;
        }

        try
        {
            barrier.Wait(context);
            return;
        }
        catch (OperationCanceledException)
        {
            abort?.Invoke();
        }

        // The reference waits for the barrier unconditionally after aborting.
        barrier.GetAwaiter().GetResult();
    }

    /// <summary>Port of collectStreamTail.</summary>
    internal static StreamTailResult CollectTail(
        CancellationToken context,
        IStream<StreamEvent>? source,
        Func<StreamEvent?, Exception?>? onEvent)
    {
        if (source is null)
        {
            return new StreamTailResult(SourceEndReason.CleanEof, null);
        }

        using var tailCts = CancellationTokenSource.CreateLinkedTokenSource(context);
        tailCts.CancelAfter(TailTimeout);

        try
        {
            return CollectCore(tailCts.Token, source, onEvent);
        }
        catch (Exception ex)
        {
            // The reference recovers a collection panic in the goroutine and
            // reports it as an error end.
            return new StreamTailResult(SourceEndReason.Error, ex);
        }
    }

    private static StreamTailResult CollectCore(
        CancellationToken context,
        IStream<StreamEvent> source,
        Func<StreamEvent?, Exception?>? onEvent)
    {
        var events = 0;
        var bytes = 0;

        while (events < TailEventLimit && bytes < TailPayloadLimit)
        {
            if (context.IsCancellationRequested)
            {
                return new StreamTailResult(
                    SourceEndReason.Canceled,
                    new OperationCanceledException(context));
            }

            if (!source.Next())
            {
                var error = source.Err;
                if (error is null)
                {
                    return new StreamTailResult(SourceEndReason.CleanEof, null);
                }

                // A source canceled mid-tail is not a clean end: its usage may
                // be a placeholder, so it must not be certified as final.
                if (StreamOutcome.IsContextCancellation(error)
                    || StreamOutcome.HasTimeoutCause(error))
                {
                    return new StreamTailResult(SourceEndReason.Canceled, error);
                }

                return new StreamTailResult(SourceEndReason.Error, error);
            }

            var streamEvent = source.Current;
            if (streamEvent is not null)
            {
                var eventBytes = streamEvent.Data.Length
                    + Encoding.UTF8.GetByteCount(streamEvent.Type)
                    + Encoding.UTF8.GetByteCount(streamEvent.LastEventId ?? string.Empty);

                if (eventBytes >= TailPayloadLimit || bytes >= TailPayloadLimit - eventBytes)
                {
                    return new StreamTailResult(
                        SourceEndReason.Budget,
                        new IOException("short buffer"));
                }

                bytes += eventBytes;
            }

            events++;

            if (onEvent is not null)
            {
                var error = onEvent(streamEvent);
                if (error is not null)
                {
                    return new StreamTailResult(SourceEndReason.Error, error);
                }
            }
        }

        return new StreamTailResult(SourceEndReason.Budget, null);
    }

    /// <summary>Port of streamLifecycle.downstreamEnded's normalizeCancelCause.</summary>
    internal static Exception NormalizeCancelCause(Exception? cause)
        => cause ?? new OperationCanceledException();
}

/// <summary>Port of orchestrator.streamTailOwner.</summary>
internal sealed class StreamTailOwner
{
    public IStream<StreamEvent>? Source { get; init; }

    /// <summary>The consumer barrier: completes when the consumer stopped iterating.</summary>
    public Task? ReaderDone { get; init; }

    /// <summary>The in-flight reader barrier (nil when no read is active).</summary>
    public Func<Task?>? ReaderDoneFunc { get; init; }

    /// <summary>Closes the source so a blocked reader can return.</summary>
    public Action? Abort { get; init; }

    public Func<StreamEvent?, Exception?>? OnEvent { get; init; }

    public Func<SourceEndReason>? SourceEnd { get; init; }

    public Func<SourceEndReason, Exception?>? Finalize { get; init; }

    public TaskCompletionSource Done { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource AccountingDone { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal int Started;

    /// <summary>Port of streamTailOwner.run.</summary>
    internal void Run(CancellationToken context, Exception? cause)
    {
        var result = new StreamTailResult(
            SourceEndReason.Canceled,
            new OperationCanceledException(context));

        try
        {
            if (cause is not null)
            {
                Abort?.Invoke();
            }

            // The tail must not touch the raw source until the consumer has both
            // finished its in-flight read and stopped iterating. Awaiting only
            // one of the two barriers lets the tail call Next on the raw source
            // while the consumer is between Next and Current, which can skip or
            // duplicate the final usage event.
            StreamLifecycle.AwaitTailBarrier(context, Abort, ReaderDone);
            if (ReaderDoneFunc is not null)
            {
                StreamLifecycle.AwaitTailBarrier(context, Abort, ReaderDoneFunc());
            }

            result = new StreamTailResult(
                SourceEndReason.Canceled,
                new OperationCanceledException(context));

            if (!context.IsCancellationRequested)
            {
                var collected = new TaskCompletionSource<StreamTailResult>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                _ = Task.Factory.StartNew(
                    () =>
                    {
                        try
                        {
                            collected.TrySetResult(
                                StreamLifecycle.CollectTail(context, Source, OnEvent));
                        }
                        catch (Exception ex)
                        {
                            collected.TrySetResult(
                                new StreamTailResult(SourceEndReason.Error, ex));
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

                try
                {
                    collected.Task.Wait(context);
                    result = collected.Task.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    // Go: after the abort it waits for the collector to return,
                    // then overrides the result with the context's end.
                    Abort?.Invoke();
                    _ = collected.Task.GetAwaiter().GetResult();
                    result = new StreamTailResult(
                        SourceEndReason.Canceled,
                        new OperationCanceledException(context));
                }
            }

            if (Finalize is not null)
            {
                var end = result.End;
                if (result.Error is not null)
                {
                    end = SourceEndReason.Error;
                    if (StreamOutcome.IsContextCancellation(result.Error)
                        || StreamOutcome.HasTimeoutCause(result.Error))
                    {
                        end = SourceEndReason.Canceled;
                    }
                }

                if (result.Error is null
                    && end == SourceEndReason.CleanEof
                    && SourceEnd is not null
                    && SourceEnd() == SourceEndReason.Semantic)
                {
                    end = SourceEndReason.Semantic;
                }

                _ = Finalize(end);
            }
        }
        catch (Exception)
        {
            // The reference logs a tail-owner panic and still releases done.
        }
        finally
        {
            Done.TrySetResult();
        }
    }
}

/// <summary>Port of orchestrator.streamTailResult.</summary>
internal readonly record struct StreamTailResult(SourceEndReason End, Exception? Error);
