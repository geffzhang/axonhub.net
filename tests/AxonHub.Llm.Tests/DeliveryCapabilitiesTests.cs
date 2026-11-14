using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of the delivery-capability tests around pipeline.preserveStreamCapabilities
/// (llm/pipeline/stream_capabilities.go) and httpclient.responseHeadersStream:
/// decorators that implement only the base stream API must not hide the
/// transport acknowledgement, the delivery boundary, finalization or the
/// expected choice count of the stream they wrap.
/// </summary>
public sealed class DeliveryCapabilitiesTests
{
    [Fact]
    public async Task PreserveForwardsAcknowledgementBoundaryAndFinalization()
    {
        var source = new CapabilitySource();
        var adapted = new PassThroughStream<StreamEvent>(source);

        // Preserve keeps the reference's return type: a base stream whose
        // capabilities are only visible through the optional interfaces.
        var preserved = DeliveryCapabilities.Preserve(source, adapted);
        var observer = Assert.IsAssignableFrom<IDeliveryAcknowledger>(preserved);

        Assert.NotSame(adapted, preserved);
        Assert.IsAssignableFrom<IStream<StreamEvent>>(preserved);
        Assert.False(observer.DeliveryAcknowledged);

        preserved.AcknowledgeDelivery();
        Assert.True(source.Acknowledged);
        Assert.True(observer.DeliveryAcknowledged);

        var writes = 0;
        var error = await preserved.DeliverAsync(
            () =>
            {
                writes++;
                return Task.FromResult(new DeliveryWrite(Terminal: false, Error: null));
            },
            CancellationToken.None);

        Assert.Null(error);
        Assert.Equal(1, writes);
        Assert.Equal(1, source.BoundaryCalls);

        Assert.IsAssignableFrom<IFinalizationWaiter>(preserved).WaitForFinalization();
        Assert.Equal(1, source.WaiterCalls);
    }

    [Fact]
    public void PreserveKeepsExpectedChoicesDynamic()
    {
        // stream_capabilities.go stores the method value, not the count read at
        // wrap time, so a tracker that learns more choices later is visible.
        var source = new CapabilitySource { Expected = 1 };
        var preserved = DeliveryCapabilities.Preserve(
            source,
            new PassThroughStream<StreamEvent>(source));

        Assert.Equal(1, DeliveryCapabilities.ExpectedStreamChoicesOf(preserved));

        source.Expected = 3;

        Assert.Equal(3, DeliveryCapabilities.ExpectedStreamChoicesOf(preserved));
    }

    [Fact]
    public void PreserveLeavesPlainStreamsAlone()
    {
        var source = new ListStream<StreamEvent>([Event("hello")]);
        var adapted = new PassThroughStream<StreamEvent>(source);

        Assert.Same(adapted, DeliveryCapabilities.Preserve(source, adapted));
        Assert.Equal(0, DeliveryCapabilities.ExpectedStreamChoicesOf(adapted));
    }

    [Fact]
    public async Task DeliverWithoutBoundaryAcknowledgesOnlyTerminalSuccess()
    {
        var observer = new Acknowledger();

        Assert.Null(await DeliveryCapabilities.DeliverWithoutBoundaryAsync(
            observer,
            () => Task.FromResult(new DeliveryWrite(Terminal: true, Error: null))));
        Assert.Equal(1, observer.Count);

        Assert.Null(await DeliveryCapabilities.DeliverWithoutBoundaryAsync(
            observer,
            () => Task.FromResult(new DeliveryWrite(Terminal: false, Error: null))));
        Assert.Equal(1, observer.Count);

        var failure = new IOException("write failed");
        Assert.Same(failure, await DeliveryCapabilities.DeliverWithoutBoundaryAsync(
            observer,
            () => Task.FromResult(new DeliveryWrite(Terminal: true, Error: failure))));
        Assert.Equal(1, observer.Count);
    }

    [Fact]
    public async Task ResponseHeadersStreamForwardsDeliveryCapabilities()
    {
        var source = new CapabilitySource { Expected = 2 };
        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["X-Test"] = ["1"],
        };

        var wrapped = new ResponseHeadersStream(source, headers);

        Assert.Equal(2, wrapped.ExpectedStreamChoices);
        wrapped.AcknowledgeDelivery();
        Assert.True(source.Acknowledged);
        Assert.True(wrapped.DeliveryAcknowledged);

        _ = await wrapped.DeliverAsync(
            () => Task.FromResult(new DeliveryWrite(Terminal: true, Error: null)),
            CancellationToken.None);

        Assert.Equal(1, source.BoundaryCalls);
        wrapped.WaitForFinalization();
        Assert.Equal(1, source.WaiterCalls);
    }

    private static StreamEvent Event(string data) => new() { Data = System.Text.Encoding.UTF8.GetBytes(data) };

    private sealed class Acknowledger : IDeliveryAcknowledger
    {
        public int Count { get; private set; }

        public bool DeliveryAcknowledged => Count > 0;

        public void AcknowledgeDelivery() => Count++;
    }

    private sealed class CapabilitySource
        : IStream<StreamEvent>, IDeliveryAcknowledger, IDeliveryBoundary, IFinalizationWaiter,
          IExpectedStreamChoices
    {
        public bool Acknowledged { get; private set; }

        public int BoundaryCalls { get; private set; }

        public int WaiterCalls { get; private set; }

        public int Expected { get; set; }

        public StreamEvent? Current => null;

        public Exception? Err => null;

        public int ExpectedStreamChoices => Expected;

        public bool DeliveryAcknowledged => Acknowledged;

        public bool Next() => false;

        public void AcknowledgeDelivery() => Acknowledged = true;

        public async Task<Exception?> DeliverAsync(
            Func<Task<DeliveryWrite>> write,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(write);
            BoundaryCalls++;

            var result = await write();
            return result.Error;
        }

        public void WaitForFinalization() => WaiterCalls++;

        public void Dispose()
        {
            // Nothing to release.
        }
    }

    private sealed class PassThroughStream<T>(IStream<T> stream) : IStream<T>
        where T : class
    {
        public T? Current => stream.Current;

        public Exception? Err => stream.Err;

        public bool Next() => stream.Next();

        public void Dispose() => stream.Dispose();
    }

    private sealed class ListStream<T>(IReadOnlyList<T> items) : IStream<T>
        where T : class
    {
        private int _index;

        public T? Current { get; private set; }

        public Exception? Err => null;

        public bool Next()
        {
            if (_index >= items.Count)
            {
                return false;
            }

            Current = items[_index];
            _index++;
            return true;
        }

        public void Dispose()
        {
            // Nothing to release.
        }
    }
}
