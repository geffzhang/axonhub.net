using AxonHub.Llm.Streams;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Per-request delivery state shared by the inbound persistent stream and the
/// pass-through delivery gate.
/// </summary>
/// <remarks>
/// Port of the parts of orchestrator.streamLifecycle
/// (internal/server/orchestrator/stream_lifecycle.go) that bound downstream
/// delivery: <c>Deliver</c>, <c>AcknowledgeDelivery</c>,
/// <c>DeliveryAcknowledged</c>, <c>WaitForFinalization</c> and the closed flag.
/// The reference also runs a separate execution context with tail accounting;
/// the port persists synchronously (C018), so this object only carries the
/// delivery facts. The execution-context split remains a registered residual
/// gap (C255).
/// </remarks>
public sealed class RequestDeliveryState
    : IDeliveryAcknowledger, IDeliveryBoundary, IFinalizationWaiter
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource _finalized =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _acknowledged;
    private bool _closed;

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

    /// <summary>Port of streamLifecycle.AcknowledgeDelivery, returning whether this call acknowledged.</summary>
    public bool TryAcknowledgeDelivery()
    {
        lock (_sync)
        {
            // The reference ignores the acknowledgement once the lifecycle is
            // closed or canceled.
            if (_closed)
            {
                return false;
            }

            var first = !_acknowledged;
            _acknowledged = true;
            return first;
        }
    }

    void IDeliveryAcknowledger.AcknowledgeDelivery() => TryAcknowledgeDelivery();

    /// <summary>
    /// Port of streamLifecycle.Deliver: refuse once closed or once the downstream
    /// context ended, otherwise write and acknowledge a terminal event.
    /// </summary>
    public async Task<Exception?> DeliverAsync(
        Func<Task<DeliveryWrite>> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        lock (_sync)
        {
            if (_closed)
            {
                return new OperationCanceledException("stream lifecycle closed");
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return new OperationCanceledException(cancellationToken);
        }

        var result = await write().ConfigureAwait(false);
        if (result.Error is null && result.Terminal)
        {
            TryAcknowledgeDelivery();
        }

        return result.Error;
    }

    /// <summary>
    /// Port of the end of streamLifecycle.Close: no further delivery is
    /// accepted and finalization waiters are released.
    /// </summary>
    public void MarkClosed()
    {
        lock (_sync)
        {
            _closed = true;
        }

        _finalized.TrySetResult();
    }

    /// <summary>Port of streamLifecycle.WaitForFinalization.</summary>
    public void WaitForFinalization() => _finalized.Task.GetAwaiter().GetResult();
}
