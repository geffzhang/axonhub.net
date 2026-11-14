using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/orchestrator/stream_lifecycle_test.go: the delivery
/// lifecycle must not promote a cancelled downstream, the accounting tail must
/// respect its event/payload budgets and both reader barriers, and a retry must
/// wait for the abandoned attempt's accounting.
/// </summary>
public sealed class StreamLifecycleTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task CancelBeforeAcknowledgementCannotPromote()
    {
        using var downstream = new CancellationTokenSource();
        var lifecycle = new StreamLifecycle(downstream.Token);

        downstream.Cancel();
        await WaitForCancellationAsync(lifecycle.ExecutionToken);

        var writes = 0;
        var error = await lifecycle.DeliverAsync(
            () =>
            {
                writes++;
                return Task.FromResult(new DeliveryWrite(Terminal: true, Error: null));
            },
            lifecycle.ExecutionToken);

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(0, writes);

        lifecycle.AcknowledgeDelivery();
        Assert.False(lifecycle.DeliveryAcknowledged);

        lifecycle.Close();
        Assert.True(lifecycle.ExecutionToken.IsCancellationRequested);
    }

    [Fact]
    public void SuccessfulTerminalWriteRemainsAcknowledgedAfterCancellation()
    {
        // Mirrors the reference subtest: with the watcher stopped, a direct
        // acknowledgement is still rejected while the downstream is cancelled.
        using var downstream = new CancellationTokenSource();
        var lifecycle = new StreamLifecycle(downstream.Token);

        lifecycle.StopWatcher();
        downstream.Cancel();
        lifecycle.AcknowledgeDelivery();

        Assert.False(lifecycle.DeliveryAcknowledged);

        lifecycle.Close();
    }

    [Fact]
    public async Task AcknowledgementBeforeLaterCancellationRemainsLatched()
    {
        using var downstream = new CancellationTokenSource();
        var lifecycle = new StreamLifecycle(downstream.Token);

        var error = await lifecycle.DeliverAsync(
            () => Task.FromResult(new DeliveryWrite(Terminal: true, Error: null)),
            lifecycle.ExecutionToken);
        Assert.Null(error);

        downstream.Cancel();

        Assert.True(lifecycle.DeliveryAcknowledged);

        // The acknowledged execution must survive a downstream loss.
        Assert.False(lifecycle.ExecutionToken.IsCancellationRequested);

        lifecycle.Close();

        // Close without a registered tail still releases the execution context.
        await WaitForCancellationAsync(lifecycle.ExecutionToken);
    }

    [Fact]
    public void TailRetainsOrderAndStopsAtEventBudget()
    {
        var events = Enumerable.Range(0, StreamLifecycle.TailEventLimit + 1)
            .Select(i => new StreamEvent { Data = [(byte)i] })
            .ToList();
        var source = new TailEventsStream(events);
        var seen = 0;

        var result = StreamLifecycle.CollectTail(
            CancellationToken.None,
            source,
            streamEvent =>
            {
                Assert.NotNull(streamEvent);
                seen++;
                return null;
            });

        Assert.Null(result.Error);
        Assert.Equal(StreamLifecycle.TailEventLimit, seen);
        Assert.Equal(StreamLifecycle.TailEventLimit, source.Index);
    }

    [Fact]
    public void TailEventBudgetReportsBudget()
    {
        var events = Enumerable.Range(0, StreamLifecycle.TailEventLimit + 1)
            .Select(i => new StreamEvent { Data = [(byte)i] })
            .ToList();

        var result = StreamLifecycle.CollectTail(
            CancellationToken.None,
            new TailEventsStream(events),
            onEvent: null);

        Assert.Equal(SourceEndReason.Budget, result.End);
    }

    [Fact]
    public void TailStopsBeforeExceedingPayloadBudget()
    {
        var source = new TailEventsStream(
            [new StreamEvent { Data = new byte[StreamLifecycle.TailPayloadLimit] }]);

        var result = StreamLifecycle.CollectTail(CancellationToken.None, source, onEvent: null);

        Assert.IsType<IOException>(result.Error);
        Assert.Equal(SourceEndReason.Budget, result.End);
    }

    [Fact]
    public async Task TailOwnerBudgetAbortsSourceBeforeFinalizing()
    {
        var source = new BudgetTailStream();
        var finalized = new TaskCompletionSource<SourceEndReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new StreamTailOwner
        {
            Source = source,
            ReaderDone = Task.CompletedTask,
            Abort = source.Dispose,
            Finalize = end =>
            {
                finalized.TrySetResult(end);
                return null;
            },
        };

        owner.Run(CancellationToken.None, new OperationCanceledException());
        await owner.Done.Task;

        Assert.Equal(StreamLifecycle.TailEventLimit, source.NextCalls);
        Assert.True(source.Closed.IsCompleted);
        Assert.Equal(SourceEndReason.Budget, await finalized.Task);
    }

    [Theory]
    [InlineData(false, SourceEndReason.CleanEof)]
    [InlineData(true, SourceEndReason.Semantic)]
    public async Task TailOwnerReportsSourceEndReasons(bool semantic, SourceEndReason expected)
    {
        var finalized = new TaskCompletionSource<SourceEndReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new StreamTailOwner
        {
            Source = new TailEventsStream([]),
            ReaderDone = Task.CompletedTask,
            SourceEnd = semantic ? () => SourceEndReason.Semantic : null,
            Finalize = end =>
            {
                finalized.TrySetResult(end);
                return null;
            },
        };

        owner.Run(CancellationToken.None, cause: null);
        await owner.Done.Task;

        Assert.Equal(expected, await finalized.Task);
    }

    [Fact]
    public async Task TailOwnerAbortsBlockedNextBeforeFinalizing()
    {
        using var tailCts = new CancellationTokenSource();
        var source = new BlockedNextStream();
        var finalized = new TaskCompletionSource<SourceEndReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var nextReturnedAtFinalize = false;
        var owner = new StreamTailOwner
        {
            Source = source,
            ReaderDone = Task.CompletedTask,
            Abort = source.Dispose,
            Finalize = end =>
            {
                nextReturnedAtFinalize = source.NextDone.IsCompleted;
                finalized.TrySetResult(end);
                return null;
            },
        };

        var run = Task.Run(() => owner.Run(tailCts.Token, cause: null));
        await source.Entered.WaitAsync(SafetyTimeout);

        tailCts.Cancel();

        await source.Closed.WaitAsync(SafetyTimeout);
        var end = await finalized.Task.WaitAsync(SafetyTimeout);
        await run.WaitAsync(SafetyTimeout);

        Assert.Equal(SourceEndReason.Canceled, end);
        Assert.True(source.NextDone.IsCompleted);
        Assert.True(nextReturnedAtFinalize);
        Assert.Equal(1, source.NextCalls);
    }

    [Fact]
    public async Task LifecycleTailOwnerFinalizesWithoutCancelingAcknowledgedExecution()
    {
        using var downstream = new CancellationTokenSource();
        var lifecycle = new StreamLifecycle(downstream.Token);
        var source = new BlockingTailStream();
        var finalized = new TaskCompletionSource<SourceEndReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        lifecycle.RegisterTailOwner(new StreamTailOwner
        {
            Source = source,
            ReaderDone = Task.CompletedTask,
            Abort = source.Dispose,
            Finalize = end =>
            {
                finalized.TrySetResult(end);
                return null;
            },
        });

        lifecycle.AcknowledgeDelivery();
        downstream.Cancel();

        await Task.Run(lifecycle.Close).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(SourceEndReason.Canceled, await finalized.Task.WaitAsync(SafetyTimeout));
        Assert.False(lifecycle.ExecutionToken.IsCancellationRequested);
        Assert.False(source.ConcurrentNext);
    }

    [Fact]
    public async Task RetryWaitsForPendingAccounting()
    {
        var lifecycle = new StreamLifecycle(CancellationToken.None);
        var accountingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAccounting = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tail = new StreamTailOwner
        {
            Source = new TailEventsStream([]),
            ReaderDone = Task.CompletedTask,
            Finalize = _ =>
            {
                accountingStarted.TrySetResult();
                releaseAccounting.Task.GetAwaiter().GetResult();
                return null;
            },
        };

        lifecycle.RegisterTailOwner(tail);
        lifecycle.StartTail(tail, cause: null);
        await accountingStarted.Task.WaitAsync(SafetyTimeout);

        var retry = Task.Run(lifecycle.WaitForAccounting);
        await Task.Delay(50);
        Assert.False(retry.IsCompleted);

        releaseAccounting.TrySetResult();

        await retry.WaitAsync(SafetyTimeout);
        lifecycle.Close();
    }

    [Fact]
    public async Task UnacknowledgedCloseWaitsForReaderBeforeFinalizing()
    {
        var lifecycle = new StreamLifecycle(CancellationToken.None);
        var source = new DelayedTailStream();
        var finalized = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var reader = Task.Run(() => source.Next());
        lifecycle.RegisterTailOwner(new StreamTailOwner
        {
            Source = source,
            ReaderDone = source.ReaderDone,
            Abort = source.Dispose,
            Finalize = _ =>
            {
                finalized.TrySetResult();
                return null;
            },
        });

        var close = Task.Run(lifecycle.Close);

        await source.CloseCalled.WaitAsync(SafetyTimeout);
        await Task.Delay(50);
        Assert.False(finalized.Task.IsCompleted);

        source.Release();

        await close.WaitAsync(TimeSpan.FromSeconds(10));
        await reader.WaitAsync(SafetyTimeout);
        Assert.True(finalized.Task.IsCompleted);
    }

    [Fact]
    public async Task LifecycleShutdownCancelsAcknowledgedExecution()
    {
        using var shutdown = new CancellationTokenSource();
        var lifecycle = new StreamLifecycle(
            CancellationToken.None,
            shutdown: shutdown.Token,
            shutdownCause: () => new InvalidOperationException("test shutdown"));

        lifecycle.AcknowledgeDelivery();
        shutdown.Cancel();

        await WaitForCancellationAsync(lifecycle.ExecutionToken);
        lifecycle.Close();
    }

    [Fact]
    public void TailOwnerWaitsForReader()
    {
        var readerDone = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new OrderedTailStream(readerDone.Task);
        var owner = new StreamTailOwner
        {
            Source = source,
            ReaderDone = readerDone.Task,
            Abort = () => readerDone.TrySetResult(),
            Finalize = _ => null,
        };

        owner.Run(CancellationToken.None, new OperationCanceledException());

        Assert.False(source.SawEarly);
    }

    [Fact]
    public async Task TailOwnerWaitsForConsumerQuiescence()
    {
        var consumerDone = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var activeReader = Task.CompletedTask;
        var source = new HandoffProbeStream();
        var finalized = new TaskCompletionSource<SourceEndReason>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new StreamTailOwner
        {
            Source = source,
            ReaderDone = consumerDone.Task,
            ReaderDoneFunc = () => activeReader,
            Abort = source.Dispose,
            Finalize = end =>
            {
                finalized.TrySetResult(end);
                return null;
            },
        };

        using var tailCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var run = Task.Run(() => owner.Run(tailCts.Token, cause: null));

        // The consumer still owns the source, so the tail must not read it.
        await Task.Delay(100);
        Assert.False(source.Entered.IsCompleted);

        consumerDone.TrySetResult();
        await source.Entered.WaitAsync(SafetyTimeout);

        source.Release();
        await finalized.Task.WaitAsync(SafetyTimeout);
        await run.WaitAsync(SafetyTimeout);

        Assert.False(source.ConcurrentNext);
    }

    private static async Task WaitForCancellationAsync(CancellationToken token)
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() => canceled.TrySetResult());
        if (token.IsCancellationRequested)
        {
            return;
        }

        await canceled.Task.WaitAsync(SafetyTimeout, CancellationToken.None);
    }

    private sealed class TailEventsStream(IReadOnlyList<StreamEvent> events) : IStream<StreamEvent>
    {
        private int _index;

        public int Index => _index;

        public StreamEvent? Current => _index > 0 ? events[_index - 1] : null;

        public Exception? Err => null;

        public bool Next()
        {
            if (_index == events.Count)
            {
                return false;
            }

            _index++;
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class BudgetTailStream : IStream<StreamEvent>
    {
        private readonly TaskCompletionSource _closed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int NextCalls { get; private set; }

        public Task Closed => _closed.Task;

        public StreamEvent? Current => new() { Data = [1] };

        public Exception? Err => null;

        public bool Next()
        {
            NextCalls++;
            return true;
        }

        public void Dispose() => _closed.TrySetResult();
    }

    private sealed class BlockedNextStream : IStream<StreamEvent>
    {
        private readonly TaskCompletionSource _closed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _nextDone =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _nextCalls;

        public int NextCalls => Volatile.Read(ref _nextCalls);

        public Task Closed => _closed.Task;

        public Task Entered => _entered.Task;

        public Task NextDone => _nextDone.Task;

        public StreamEvent? Current => null;

        public Exception? Err => new OperationCanceledException();

        public bool Next()
        {
            Interlocked.Increment(ref _nextCalls);
            _entered.TrySetResult();
            _closed.Task.GetAwaiter().GetResult();
            _nextDone.TrySetResult();
            return false;
        }

        public void Dispose() => _closed.TrySetResult();
    }

    private sealed class BlockingTailStream : IStream<StreamEvent>
    {
        private readonly TaskCompletionSource _closed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeNext;

        public bool ConcurrentNext { get; private set; }

        public StreamEvent? Current => null;

        public Exception? Err => new OperationCanceledException();

        public bool Next()
        {
            if (Interlocked.Increment(ref _activeNext) != 1)
            {
                ConcurrentNext = true;
            }

            try
            {
                _closed.Task.GetAwaiter().GetResult();
                return false;
            }
            finally
            {
                Interlocked.Decrement(ref _activeNext);
            }
        }

        public void Dispose() => _closed.TrySetResult();
    }

    private sealed class OrderedTailStream(Task readerDone) : IStream<StreamEvent>
    {
        public bool SawEarly { get; private set; }

        public StreamEvent? Current => null;

        public Exception? Err => null;

        public bool Next()
        {
            if (!readerDone.IsCompleted)
            {
                SawEarly = true;
            }

            return false;
        }

        public void Dispose()
        {
        }
    }

    private sealed class DelayedTailStream : IStream<StreamEvent>
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closeCalled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _readerDone =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CloseCalled => _closeCalled.Task;

        public Task ReaderDone => _readerDone.Task;

        public StreamEvent? Current => null;

        public Exception? Err => new OperationCanceledException();

        public bool Next()
        {
            try
            {
                _release.Task.GetAwaiter().GetResult();
                return false;
            }
            finally
            {
                _readerDone.TrySetResult();
            }
        }

        public void Release() => _release.TrySetResult();

        public void Dispose() => _closeCalled.TrySetResult();
    }

    private sealed class HandoffProbeStream : IStream<StreamEvent>
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeNext;

        public Task Entered => _entered.Task;

        public bool ConcurrentNext { get; private set; }

        public StreamEvent? Current => null;

        public Exception? Err => new OperationCanceledException();

        public bool Next()
        {
            if (Interlocked.Increment(ref _activeNext) != 1)
            {
                ConcurrentNext = true;
            }

            try
            {
                _entered.TrySetResult();
                _release.Task.GetAwaiter().GetResult();
                return false;
            }
            finally
            {
                Interlocked.Decrement(ref _activeNext);
            }
        }

        public void Release() => _release.TrySetResult();

        public void Dispose()
        {
        }
    }
}
