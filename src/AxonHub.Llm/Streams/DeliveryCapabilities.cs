using AxonHub.Llm.Http;

namespace AxonHub.Llm.Streams;

/// <summary>
/// Port of streams.DeliveryAcknowledger (llm/streams/stream.go): an optional
/// stream capability. A transport calls <see cref="AcknowledgeDelivery"/> only
/// after the semantic terminal boundary was written successfully; generation
/// and accounting remain separate concerns.
/// </summary>
public interface IDeliveryAcknowledger
{
    void AcknowledgeDelivery();

    bool DeliveryAcknowledged { get; }
}

/// <summary>
/// Port of streams.DeliveryBoundary: runs one downstream write under the
/// request chain's delivery gate. The write reports whether it delivered the
/// semantic terminal event and, as data, whether the transport failed.
/// </summary>
/// <remarks>
/// The reference's signature is <c>Deliver(context.Context, func() (bool, error)) error</c>.
/// .NET writes are asynchronous, so the callback and the result are awaitable;
/// the error stays data (<see cref="Exception"/>) instead of becoming a faulted
/// task. A callback that lets an exception escape aborts delivery without an
/// acknowledgement, exactly like a write failure in the reference.
/// </remarks>
public interface IDeliveryBoundary
{
    Task<Exception?> DeliverAsync(
        Func<Task<DeliveryWrite>> write,
        CancellationToken cancellationToken);
}

/// <summary>
/// Result of one gated downstream write: <see cref="Terminal"/> is true when the
/// event was the semantic terminal boundary, <see cref="Error"/> carries a
/// transport failure as data.
/// </summary>
public readonly record struct DeliveryWrite(bool Terminal, Exception? Error);

/// <summary>
/// Port of streams.FinalizationWaiter: waits for source-owned stream accounting
/// to finish. Stream disposal remains nonblocking for transport callers.
/// </summary>
public interface IFinalizationWaiter
{
    void WaitForFinalization();
}

/// <summary>
/// Port of the anonymous <c>interface{ ExpectedStreamChoices() int }</c> used by
/// the terminal trackers: a stream may tell its consumer how many choices the
/// request asked for, so a multi-choice stream is not declared incomplete after
/// the first choice finishes.
/// </summary>
public interface IExpectedStreamChoices
{
    int ExpectedStreamChoices { get; }
}

/// <summary>
/// Port of llm/pipeline/stream_capabilities.go and
/// internal/server/orchestrator/stream_delivery_capabilities.go: decorators that
/// implement only the base stream API keep transport acknowledgements,
/// finalization waits and the expected choice count connected to the source.
/// </summary>
public static class DeliveryCapabilities
{
    /// <summary>Port of the api.writeStreamDelivery gate (internal/server/api/stream_delivery.go).</summary>
    public static Task<Exception?> DeliverAsync(
        this IStream<StreamEvent> stream,
        Func<Task<DeliveryWrite>> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(write);

        if (stream is IDeliveryBoundary boundary)
        {
            return boundary.DeliverAsync(write, cancellationToken);
        }

        return DeliverWithoutBoundaryAsync(stream as IDeliveryAcknowledger, write);
    }

    /// <summary>Port of acknowledgeStreamDelivery.</summary>
    public static void AcknowledgeDelivery(this IStream<StreamEvent> stream)
        => (stream as IDeliveryAcknowledger)?.AcknowledgeDelivery();

    /// <summary>Port of streamExpectedChoices.</summary>
    public static int ExpectedStreamChoicesOf(this IStream<StreamEvent> stream)
        => stream is IExpectedStreamChoices choices ? choices.ExpectedStreamChoices : 0;

    /// <summary>
    /// Port of preserveStreamCapabilities[T, R] and
    /// preserveDeliveryCapabilities: the adapted decorator keeps the source's
    /// capabilities only when the source is a delivery acknowledger.
    /// </summary>
    public static IStream<TAdapted> Preserve<TSource, TAdapted>(
        IStream<TSource> source,
        IStream<TAdapted> adapted)
        where TSource : class
        where TAdapted : class
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(adapted);

        if (source is not IDeliveryAcknowledger observer)
        {
            return adapted;
        }

        // stream_capabilities.go keeps the method value, not the count read at
        // wrap time: the tracker can still be counting choices later.
        return new CapabilityStream<TAdapted>(
            adapted,
            observer,
            source as IDeliveryBoundary,
            source as IFinalizationWaiter,
            source is IExpectedStreamChoices choices ? () => choices.ExpectedStreamChoices : null);
    }

    /// <summary>
    /// Port of the fallback half of every Deliver implementation: without a
    /// boundary the write still acknowledges the delivered terminal event.
    /// A null observer accepts the acknowledgement as a no-op.
    /// </summary>
    public static async Task<Exception?> DeliverWithoutBoundaryAsync(
        IDeliveryAcknowledger? observer,
        Func<Task<DeliveryWrite>> write)
    {
        var result = await write().ConfigureAwait(false);
        if (result.Error is null && result.Terminal)
        {
            observer?.AcknowledgeDelivery();
        }

        return result.Error;
    }
}

/// <summary>
/// Port of pipeline.streamCapabilities / deliveryCapabilitiesStream: a
/// transparent stream decorator that forwards every optional capability to the
/// source that owns it.
/// </summary>
internal sealed class CapabilityStream<T>(
    IStream<T> adapted,
    IDeliveryAcknowledger observer,
    IDeliveryBoundary? boundary,
    IFinalizationWaiter? waiter,
    Func<int>? expectedChoices)
    : IStream<T>, IDeliveryAcknowledger, IDeliveryBoundary, IFinalizationWaiter, IExpectedStreamChoices
    where T : class
{
    public T? Current => adapted.Current;

    public Exception? Err => adapted.Err;

    public int ExpectedStreamChoices => expectedChoices?.Invoke() ?? 0;

    public bool DeliveryAcknowledged => observer.DeliveryAcknowledged;

    public bool Next() => adapted.Next();

    public void AcknowledgeDelivery() => observer.AcknowledgeDelivery();

    public Task<Exception?> DeliverAsync(
        Func<Task<DeliveryWrite>> write,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);

        if (boundary is not null)
        {
            return boundary.DeliverAsync(write, cancellationToken);
        }

        return DeliveryCapabilities.DeliverWithoutBoundaryAsync(observer, write);
    }

    public void WaitForFinalization() => waiter?.WaitForFinalization();

    public void Dispose() => adapted.Dispose();
}
