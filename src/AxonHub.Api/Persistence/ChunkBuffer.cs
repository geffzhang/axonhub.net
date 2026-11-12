using AxonHub.Llm.Http;

namespace AxonHub.Api.Persistence;

/// <summary>
/// Port of internal/pkg/chunkbuffer.Buffer: the single source of truth for the
/// chunks of one in-flight stream. The live preview registry and the
/// persistence wrappers read the same instance, so the preview never keeps a
/// second copy of the stream, and the final persistence write sees every chunk
/// the client received.
/// </summary>
/// <remarks>
/// The reference broadcasts with non-blocking sends on buffered
/// <c>chan struct{}</c> values; the port uses a one-slot
/// <see cref="SemaphoreSlim"/> per subscriber with the same coalescing
/// semantics (a burst of appends wakes a subscriber at most once).
/// </remarks>
public sealed class ChunkBuffer
{
    /// <summary>
    /// Port of maxChunkCapacity: appends beyond this are rejected so a runaway
    /// upstream cannot grow the buffer without bound.
    /// </summary>
    public const int MaxChunkCapacity = 50_000;

    private readonly object _sync = new();
    private readonly List<StreamEvent> _chunks = [];
    private readonly List<SemaphoreSlim> _subscribers = [];
    private readonly TimeProvider _timeProvider;
    private bool _closed;
    private DateTimeOffset _lastAppendedAt;

    public ChunkBuffer(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastAppendedAt = _timeProvider.GetUtcNow();
    }

    /// <summary>Port of Len.</summary>
    public int Length
    {
        get
        {
            lock (_sync)
            {
                return _chunks.Count;
            }
        }
    }

    /// <summary>Port of LastAppendedAt: the sweeper's idle signal.</summary>
    public DateTimeOffset LastAppendedAt
    {
        get
        {
            lock (_sync)
            {
                return _lastAppendedAt;
            }
        }
    }

    /// <summary>Port of IsClosed.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_sync)
            {
                return _closed;
            }
        }
    }

    /// <summary>
    /// Port of Append: false when the chunk is null, the buffer is closed, or
    /// the capacity limit was reached.
    /// </summary>
    public bool Append(StreamEvent? chunk)
    {
        if (chunk is null)
        {
            return false;
        }

        lock (_sync)
        {
            if (_closed || _chunks.Count >= MaxChunkCapacity)
            {
                return false;
            }

            _chunks.Add(chunk);
            _lastAppendedAt = _timeProvider.GetUtcNow();
            BroadcastLocked();
            return true;
        }
    }

    /// <summary>Port of Slice: a snapshot copy of the current chunks.</summary>
    public IReadOnlyList<StreamEvent> Slice()
    {
        lock (_sync)
        {
            return [.. _chunks];
        }
    }

    /// <summary>
    /// Port of Read: the chunk at <paramref name="index"/>, the next index, and
    /// whether the buffer was closed at the instant of the read.
    /// </summary>
    public bool TryRead(int index, out StreamEvent? chunk, out int nextIndex, out bool closed)
    {
        lock (_sync)
        {
            closed = _closed;
            if (index < 0 || index >= _chunks.Count)
            {
                chunk = null;
                nextIndex = index;
                return false;
            }

            chunk = _chunks[index];
            nextIndex = index + 1;
            return true;
        }
    }

    /// <summary>Port of Close: no further appends; every subscriber wakes up.</summary>
    public void Close()
    {
        lock (_sync)
        {
            _closed = true;
            BroadcastLocked();
        }
    }

    /// <summary>
    /// Port of SubscribeFromCurrent: registers a subscriber and atomically
    /// captures the current length as its replay cutoff. A closed buffer
    /// returns an already-signalled subscription, so the reader observes the
    /// closed state immediately.
    /// </summary>
    public BufferSubscription SubscribeFromCurrent()
    {
        var signal = new SemaphoreSlim(0, 1);
        int replayUntil;
        bool closed;

        lock (_sync)
        {
            replayUntil = _chunks.Count;
            closed = _closed;
            if (!closed)
            {
                _subscribers.Add(signal);
            }
        }

        Action unsubscribe = closed ? static () => { } : () => Unsubscribe(signal);
        if (closed)
        {
            signal.Release();
        }

        return new BufferSubscription(signal, replayUntil, unsubscribe);
    }

    private void BroadcastLocked()
    {
        foreach (var subscriber in _subscribers)
        {
            if (subscriber.CurrentCount == 0)
            {
                subscriber.Release();
            }
        }
    }

    private void Unsubscribe(SemaphoreSlim signal)
    {
        lock (_sync)
        {
            _subscribers.Remove(signal);
        }

        signal.Dispose();
    }
}

/// <summary>
/// The handle returned by <see cref="ChunkBuffer.SubscribeFromCurrent"/>: the
/// wake-up signal, the replay cutoff, and the unsubscribe action the preview
/// stream calls when it closes.
/// </summary>
public sealed class BufferSubscription
{
    private Action? _unsubscribe;

    internal BufferSubscription(
        SemaphoreSlim signal,
        int replayUntil,
        Action unsubscribe)
    {
        Signal = signal;
        ReplayUntil = replayUntil;
        _unsubscribe = unsubscribe;
    }

    /// <summary>Signalled whenever the buffer appends or closes.</summary>
    public SemaphoreSlim Signal { get; }

    /// <summary>
    /// The buffer length at subscription time: chunks with a lower index are
    /// replayed, later ones are incremental.
    /// </summary>
    public int ReplayUntil { get; }

    /// <summary>Removes the subscriber; safe to call more than once.</summary>
    public void Unsubscribe()
    {
        var unsubscribe = Interlocked.Exchange(ref _unsubscribe, null);
        unsubscribe?.Invoke();
    }
}
