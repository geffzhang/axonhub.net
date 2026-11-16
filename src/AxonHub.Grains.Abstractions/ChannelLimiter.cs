namespace AxonHub.Grains.Abstractions;

/// <summary>Why <see cref="ChannelLimiter.Acquire"/> refused to admit a request.</summary>
public enum ChannelQueueReason
{
    /// <summary>A bounded wait queue was already full at acquire time.</summary>
    QueueFull,

    /// <summary>The per-channel queue timeout elapsed while the request waited.</summary>
    QueueTimeout,
}

/// <summary>
/// Port of <c>orchestrator.ErrChannelQueueFull</c> /
/// <c>ErrChannelQueueTimeout</c> (internal/server/orchestrator/channel_limiter.go:10-17):
/// the typed admission failure raised by <see cref="ChannelLimiter"/>.
/// </summary>
public sealed class ChannelQueueException : Exception
{
    public ChannelQueueException(ChannelQueueReason reason)
        : base(reason == ChannelQueueReason.QueueFull
            ? "channel concurrency queue full"
            : "channel concurrency queue wait timeout")
    {
        Reason = reason;
    }

    public ChannelQueueReason Reason { get; }

    /// <summary>The reference sentinel text, e.g. <c>queue_full</c>.</summary>
    public string ReasonText => Reason == ChannelQueueReason.QueueFull ? "queue_full" : "queue_timeout";
}

/// <summary>
/// Port of <c>orchestrator.ChannelLimiter</c> (channel_limiter.go): per-channel
/// admission control as a hard blocking semaphore. At most <c>capacity</c>
/// requests are in flight; excess requests wait in a FIFO queue.
/// </summary>
/// <remarks>
/// <para>
/// Bounded queue (<c>queueSize &gt; 0</c>): up to <c>queueSize</c> requests wait
/// and further arrivals get <see cref="ChannelQueueReason.QueueFull"/>.
/// Unbounded queue (<c>queueSize &lt;= 0</c>): excess requests always wait and
/// are never rejected — this is the default when no queue size is configured.
/// </para>
/// <para>
/// Go blocks the calling goroutine; the port blocks the calling thread
/// (difference C042). The observable admission semantics are identical: a
/// waiter is granted its slot either by the fast path, by directly taking over
/// the slot of a releasing request, or never (queue full / timeout / caller
/// cancellation).
/// </para>
/// </remarks>
public sealed class ChannelLimiter
{
    private readonly int _capacity;
    private readonly int _queueSize;
    private readonly long _timeoutMs;
    private readonly LinkedList<Waiter> _waiters = new();
    private readonly object _lock = new();
    private int _inFlight;

    /// <summary>
    /// Creates a limiter. <paramref name="timeoutMs"/> &lt;= 0 means "no
    /// per-channel timeout" and the caller's token becomes the only deadline.
    /// The caller must ensure <paramref name="capacity"/> &gt; 0.
    /// </summary>
    public ChannelLimiter(int capacity, int queueSize, long timeoutMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
        _queueSize = queueSize;
        _timeoutMs = timeoutMs;
    }

    /// <summary>
    /// Requests a slot. Returning normally means a slot was granted and
    /// <see cref="Release"/> must be called exactly once; a thrown
    /// <see cref="ChannelQueueException"/> or <see cref="OperationCanceledException"/>
    /// means no slot was acquired and <see cref="Release"/> must not be called.
    /// </summary>
    public void Acquire(CancellationToken cancellationToken)
    {
        Waiter waiter;
        LinkedListNode<Waiter> node;

        lock (_lock)
        {
            // Fast path: a slot is free. Like Go, an already-cancelled token
            // still gets the slot — the fast path does not consult it.
            if (_inFlight < _capacity)
            {
                _inFlight++;
                return;
            }

            // A bounded queue rejects once full; an unbounded queue always waits.
            if (_queueSize > 0 && _waiters.Count >= _queueSize)
            {
                throw new ChannelQueueException(ChannelQueueReason.QueueFull);
            }

            waiter = new Waiter();
            node = _waiters.AddLast(waiter);
        }

        OperationCanceledException? canceled = null;
        var granted = false;
        try
        {
            var waitMilliseconds = _timeoutMs > 0
                ? (int)Math.Min(_timeoutMs, int.MaxValue)
                : Timeout.Infinite;

            granted = waiter.Signal.Wait(waitMilliseconds, cancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            canceled = ex;
        }

        if (!granted)
        {
            lock (_lock)
            {
                if (waiter.Granted)
                {
                    // The slot was handed over between the wait failing and us
                    // taking the lock: own it, then give it back so the next
                    // waiter (or inFlight) is correct.
                    ReleaseLocked();
                }
                else
                {
                    _waiters.Remove(node);
                }
            }

            waiter.Signal.Dispose();

            // Distinguish the per-channel queue timeout from caller-side
            // cancellation. Go: timeout only when the wait deadline fired and
            // the caller context was still live.
            if (canceled is null && !cancellationToken.IsCancellationRequested && _timeoutMs > 0)
            {
                throw new ChannelQueueException(ChannelQueueReason.QueueTimeout);
            }

            throw new OperationCanceledException(cancellationToken);
        }

        waiter.Signal.Dispose();
    }

    /// <summary>
    /// Returns one slot. Transfers it directly to the head waiter (FIFO
    /// fairness); otherwise decrements the in-flight count. Never goes below
    /// zero, so an unmatched release is a no-op instead of an error.
    /// </summary>
    public void Release()
    {
        lock (_lock)
        {
            ReleaseLocked();
        }
    }

    /// <summary>Current in-flight and waiting counts, for load-balancer scoring.</summary>
    public (int InFlight, int Waiting) Stats()
    {
        lock (_lock)
        {
            return (_inFlight, _waiters.Count);
        }
    }

    private void ReleaseLocked()
    {
        // Waiters exist in both queue modes, so the transfer is unconditional.
        if (_waiters.First is { } head)
        {
            _waiters.RemoveFirst();
            head.Value.Granted = true;
            head.Value.Signal.Set();
            return;
        }

        if (_inFlight > 0)
        {
            _inFlight--;
        }
    }

    private sealed class Waiter
    {
        /// <summary>Guarded by the limiter lock; set by a releasing owner.</summary>
        public bool Granted { get; set; }

        public ManualResetEventSlim Signal { get; } = new(false);
    }
}
