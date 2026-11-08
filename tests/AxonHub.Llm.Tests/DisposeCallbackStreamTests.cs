using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Locks the port-only teardown wrapper the orchestrator uses to stop an
/// attempt's outbound transformer when the returned stream is disposed (C072):
/// the callback runs exactly once, after the source is disposed, and even when
/// the source's Dispose throws.
/// </summary>
public sealed class DisposeCallbackStreamTests
{
    private static StreamEvent Event(string type) => new() { Type = type, Data = [1] };

    [Fact]
    public void DelegatesAndRunsTheCallbackOnceOnDispose()
    {
        var source = new SliceStream<StreamEvent>([Event("a"), Event("b")]);
        var calls = 0;
        var stream = new DisposeCallbackStream<StreamEvent>(source, () => calls++);

        Assert.True(stream.Next());
        Assert.Same(source.Current, stream.Current);
        Assert.Null(stream.Err);
        Assert.Equal(0, calls);

        stream.Dispose();
        Assert.Equal(1, calls);

        // Repeated disposal (e.g. using + explicit Dispose) is a no-op.
        stream.Dispose();
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RunsTheCallbackWhenTheSourceDisposeThrows()
    {
        var source = new ThrowingDisposeStream();
        var calls = 0;
        var stream = new DisposeCallbackStream<StreamEvent>(source, () => calls++);

        Assert.Throws<InvalidOperationException>(stream.Dispose);

        Assert.Equal(1, calls);
    }

    private sealed class ThrowingDisposeStream : IStream<StreamEvent>
    {
        public StreamEvent? Current => null;

        public Exception? Err => null;

        public bool Next() => false;

        public void Dispose() => throw new InvalidOperationException("dispose failed");
    }
}
