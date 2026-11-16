using System.Text;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of the delivery-gate behaviour added with f272549f: a completed
/// provider stream only counts as delivered once a transport acknowledged the
/// terminal event, the pass-through gate stops the producer before waiting on
/// the drain, and the API-level stream decorators keep forwarding the
/// capabilities of the stream they wrap.
/// </summary>
public sealed class StreamDeliveryTests
{
    private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void StreamLifecycleAcknowledgesOnlyOnce()
    {
        var lifecycle = new StreamLifecycle(CancellationToken.None);

        Assert.False(lifecycle.DeliveryAcknowledged);
        Assert.True(lifecycle.TryAcknowledgeDelivery());
        Assert.False(lifecycle.TryAcknowledgeDelivery());
        Assert.True(lifecycle.DeliveryAcknowledged);

        lifecycle.Close();

        Assert.False(lifecycle.TryAcknowledgeDelivery());
    }

    [Fact]
    public async Task StreamLifecycleAcknowledgesTerminalWritesOnly()
    {
        var lifecycle = new StreamLifecycle(CancellationToken.None);

        Assert.Null(await lifecycle.DeliverAsync(
            () => Task.FromResult(new DeliveryWrite(Terminal: false, Error: null)),
            CancellationToken.None));
        Assert.False(lifecycle.DeliveryAcknowledged);

        Assert.Null(await lifecycle.DeliverAsync(
            () => Task.FromResult(new DeliveryWrite(Terminal: true, Error: null)),
            CancellationToken.None));
        Assert.True(lifecycle.DeliveryAcknowledged);

        lifecycle.Close();
    }

    [Fact]
    public async Task StreamLifecycleRefusesWritesAfterClose()
    {
        var lifecycle = new StreamLifecycle(CancellationToken.None);
        lifecycle.Close();
        var writes = 0;

        var error = await lifecycle.DeliverAsync(
            () =>
            {
                writes++;
                return Task.FromResult(new DeliveryWrite(Terminal: true, Error: null));
            },
            CancellationToken.None);

        Assert.IsType<OperationCanceledException>(error);
        Assert.Equal(0, writes);
        Assert.False(lifecycle.DeliveryAcknowledged);
    }

    [Fact]
    public async Task StreamLifecycleReleasesFinalizationWaitersOnClose()
    {
        var lifecycle = new StreamLifecycle(CancellationToken.None);

        var waiter = Task.Run(lifecycle.WaitForFinalization);
        Assert.False(waiter.IsCompleted);

        lifecycle.Close();

        await waiter.WaitAsync(SafetyTimeout);
    }

    [Fact]
    public void StreamOutcomeRequiresAcknowledgementForCompletedDelivery()
    {
        var outcome = new StreamOutcome();
        outcome.ObserveTerminal(StreamTerminalState.Completed);

        Assert.Equal(StreamTerminalState.Completed, outcome.FinalDecision().State);

        var decision = outcome.DeliveryDecision();

        Assert.Equal(StreamTerminalState.Incomplete, decision.State);
        Assert.Same(StreamOutcome.ErrStreamIncomplete, decision.Cause);
    }

    [Fact]
    public void StreamOutcomeAcknowledgementCompletesTheDeliveryDecision()
    {
        var outcome = new StreamOutcome();
        outcome.ObserveTerminal(StreamTerminalState.Completed);
        outcome.ObserveDeliveryAcknowledgement();

        Assert.Equal(StreamTerminalState.Completed, outcome.DeliveryDecision().State);
    }

    [Fact]
    public void StreamOutcomeDeadlineBeforeTerminalOutranksTheTerminalEvent()
    {
        var outcome = new StreamOutcome();
        var deadline = new OperationCanceledException("deadline", new TimeoutException());

        outcome.ObserveContextError(deadline);
        outcome.ObserveTerminal(StreamTerminalState.Completed);
        outcome.ObserveDeliveryAcknowledgement();

        var decision = outcome.FinalDecision();

        Assert.Equal(StreamTerminalState.Failed, decision.State);
        Assert.Same(deadline, decision.Cause);
    }

    [Fact]
    public async Task PassThroughGateStopsTheProducerBeforeWaitingForTheDrain()
    {
        var state = new StreamLifecycle(CancellationToken.None);
        var order = new List<string>();
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new TrackingStream(order);
        var gate = new PassThroughDeliveryStream(
            inner,
            state,
            () =>
            {
                order.Add("close");
                drain.TrySetResult();
            },
            drain.Task);

        await Task.Run(gate.Dispose).WaitAsync(SafetyTimeout);
        state.Close();
        Assert.Equal(["close", "inner-dispose"], order);
    }

    [Fact]
    public async Task PassThroughGateWaitsForTheDrainAfterDelivery()
    {
        var state = new StreamLifecycle(CancellationToken.None);
        state.AcknowledgeDelivery();

        var order = new List<string>();
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new TrackingStream(order);
        var gate = new PassThroughDeliveryStream(
            inner,
            state,
            () => order.Add("close"),
            drain.Task);
        drain.TrySetResult();

        await Task.Run(gate.Dispose).WaitAsync(SafetyTimeout);
        Assert.Equal(["close", "inner-dispose"], order);
    }

    [Fact]
    public async Task PassThroughGateClosesTheProducerAfterTheTailTimeout()
    {
        var state = new StreamLifecycle(CancellationToken.None);
        state.AcknowledgeDelivery();

        var order = new List<string>();
        var drain = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new TrackingStream(order);
        var gate = new PassThroughDeliveryStream(
            inner,
            state,
            () => order.Add("close"),
            drain.Task);

        await Task.Run(gate.Dispose).WaitAsync(SafetyTimeout);

        // streamTailTimeout: an acknowledged stream waits one second for the
        // drain, then closes the producer so a blocked reader cannot stall the
        // request teardown.
        state.Close();
        Assert.Equal(["close", "inner-dispose"], order);
    }

    [Fact]
    public async Task PassThroughGateIsIdempotentAndForwardsAcknowledgement()
    {
        var state = new StreamLifecycle(CancellationToken.None);
        var order = new List<string>();
        var gate = new PassThroughDeliveryStream(
            new TrackingStream(order),
            state,
            () => order.Add("close"),
            done: null);

        gate.AcknowledgeDelivery();
        Assert.True(gate.DeliveryAcknowledged);
        Assert.True(state.DeliveryAcknowledged);

        await Task.Run(gate.Dispose).WaitAsync(SafetyTimeout);
        await Task.Run(gate.Dispose).WaitAsync(SafetyTimeout);

        state.Close();
        Assert.Equal(["close", "inner-dispose"], order);
    }

    [Fact]
    public async Task PrimedStreamForwardsDeliveryCapabilities()
    {
        var first = Event("{\"type\":\"response.created\"}");
        var source = new TrackingCapabilityStream([first, Event("[DONE]")]);
        var primed = new ChatCompletionEndpoint.PrimedStream(source, first);

        Assert.True(primed.Next());
        Assert.Same(first, primed.Current);
        Assert.Equal(1, primed.ExpectedStreamChoices);

        primed.AcknowledgeDelivery();
        Assert.True(source.Acknowledged);
        Assert.True(primed.DeliveryAcknowledged);

        var error = await primed.DeliverAsync(
            () => Task.FromResult(new DeliveryWrite(Terminal: true, Error: null)),
            CancellationToken.None);
        Assert.Null(error);
        Assert.Equal(1, source.BoundaryCalls);

        primed.WaitForFinalization();
        Assert.Equal(1, source.WaiterCalls);
    }

    private static StreamEvent Event(string data)
        => new() { Data = Encoding.UTF8.GetBytes(data) };

    private sealed class TrackingStream(List<string> order) : IStream<StreamEvent>
    {
        public StreamEvent? Current => null;

        public Exception? Err => null;

        public bool Next() => false;

        public void Dispose() => order.Add("inner-dispose");
    }

    private sealed class TrackingCapabilityStream(IReadOnlyList<StreamEvent> events)
        : IStream<StreamEvent>, IDeliveryAcknowledger, IDeliveryBoundary, IFinalizationWaiter,
          IExpectedStreamChoices
    {
        private int _index;

        public bool Acknowledged { get; private set; }

        public int BoundaryCalls { get; private set; }

        public int WaiterCalls { get; private set; }

        public StreamEvent? Current { get; private set; }

        public Exception? Err => null;

        public int ExpectedStreamChoices => 1;

        public bool DeliveryAcknowledged => Acknowledged;

        public bool Next()
        {
            if (_index >= events.Count)
            {
                return false;
            }

            Current = events[_index];
            _index++;
            return true;
        }

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
}
