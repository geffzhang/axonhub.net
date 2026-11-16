using System.Text;
using AxonHub.Api.Persistence;
using AxonHub.Llm.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks internal/pkg/chunkbuffer.Buffer: the append contract (null, closed,
/// capacity), the read snapshot the preview loop consumes, the close wake-up,
/// and SubscribeFromCurrent's atomic replay cutoff.
/// </summary>
public sealed class ChunkBufferTests
{
    [Fact]
    public void AppendRejectsNullAndClosedBuffers()
    {
        var buffer = new ChunkBuffer();

        Assert.False(buffer.Append(null));
        Assert.True(buffer.Append(Data("{\"a\":1}")));
        Assert.Equal(1, buffer.Length);

        buffer.Close();
        Assert.True(buffer.IsClosed);
        Assert.False(buffer.Append(Data("{\"b\":2}")));
        Assert.Equal(1, buffer.Length);
    }

    [Fact]
    public void AppendStopsAtTheReferenceCapacity()
    {
        var buffer = new ChunkBuffer();
        var chunk = Data("{}");

        for (var i = 0; i < ChunkBuffer.MaxChunkCapacity; i++)
        {
            Assert.True(buffer.Append(chunk));
        }

        // maxChunkCapacity (chunkbuffer.go:25) rejects the overflow append
        // instead of growing without bound.
        Assert.Equal(50_000, ChunkBuffer.MaxChunkCapacity);
        Assert.False(buffer.Append(chunk));
        Assert.Equal(ChunkBuffer.MaxChunkCapacity, buffer.Length);
    }

    [Fact]
    public void ReadReturnsTheChunkNextIndexAndTheClosedFlagAtReadTime()
    {
        var buffer = new ChunkBuffer();
        buffer.Append(Data("{\"a\":1}"));
        buffer.Append(Data("{\"b\":2}"));

        Assert.True(buffer.TryRead(0, out var chunk, out var next, out var closed));
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(chunk!.Data));
        Assert.Equal(1, next);
        Assert.False(closed);

        buffer.Close();

        Assert.True(buffer.TryRead(1, out chunk, out next, out closed));
        Assert.Equal("{\"b\":2}", Encoding.UTF8.GetString(chunk!.Data));
        Assert.Equal(2, next);
        Assert.True(closed);

        // Past the end the read reports the closed state without a chunk.
        Assert.False(buffer.TryRead(2, out chunk, out next, out closed));
        Assert.Null(chunk);
        Assert.Equal(2, next);
        Assert.True(closed);

        Assert.False(buffer.TryRead(-1, out chunk, out next, out _));
        Assert.Null(chunk);
        Assert.Equal(-1, next);

        Assert.Equal(2, buffer.Slice().Count);
    }

    [Fact]
    public void SliceIsASnapshotCopy()
    {
        var buffer = new ChunkBuffer();
        buffer.Append(Data("{\"a\":1}"));

        var slice = buffer.Slice();
        buffer.Append(Data("{\"b\":2}"));

        Assert.Single(slice);
        Assert.Equal(2, buffer.Length);
    }

    [Fact]
    public void CloseWakesEverySubscriber()
    {
        var buffer = new ChunkBuffer();
        var subscription = buffer.SubscribeFromCurrent();

        // A fresh subscription has no pending signal.
        Assert.False(subscription.Signal.Wait(0));
        Assert.False(subscription.Signal.Wait(0));

        buffer.Close();

        Assert.True(subscription.Signal.Wait(0));
        subscription.Unsubscribe();
    }

    [Fact]
    public void AppendsWakeSubscribersOncePerBurst()
    {
        var buffer = new ChunkBuffer();
        var subscription = buffer.SubscribeFromCurrent();

        buffer.Append(Data("{\"a\":1}"));
        buffer.Append(Data("{\"b\":2}"));

        // The one-slot signal coalesces a burst: consumers re-read the buffer,
        // so a second append before the wake-up is consumed is not lost.
        Assert.True(subscription.Signal.Wait(0));
        Assert.False(subscription.Signal.Wait(0));
        subscription.Unsubscribe();
    }

    [Fact]
    public void SubscribeFromCurrentCapturesTheReplayCutoff()
    {
        var buffer = new ChunkBuffer();
        buffer.Append(Data("{\"a\":1}"));
        buffer.Append(Data("{\"b\":2}"));

        var subscription = buffer.SubscribeFromCurrent();
        Assert.Equal(2, subscription.ReplayUntil);

        buffer.Append(Data("{\"c\":3}"));
        Assert.True(subscription.Signal.Wait(0));

        subscription.Unsubscribe();
    }

    [Fact]
    public void SubscribeFromAClosedBufferSignalsImmediately()
    {
        var buffer = new ChunkBuffer();
        buffer.Append(Data("{\"a\":1}"));
        buffer.Close();

        var subscription = buffer.SubscribeFromCurrent();

        Assert.Equal(1, subscription.ReplayUntil);
        Assert.True(subscription.Signal.Wait(0));

        // The closed branch returns a no-op unsubscribe (chunkbuffer.go:142).
        subscription.Unsubscribe();
        subscription.Unsubscribe();
    }

    [Fact]
    public void UnsubscribeIsIdempotentAndStopsFurtherSignalling()
    {
        var buffer = new ChunkBuffer();
        var subscription = buffer.SubscribeFromCurrent();

        subscription.Unsubscribe();
        subscription.Unsubscribe();

        // The subscriber was removed under the lock, so a later broadcast never
        // touches the disposed semaphore.
        buffer.Close();
        buffer.Append(Data("{}"));
    }

    [Fact]
    public void LastAppendedAtTracksSuccessfulAppends()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero));
        var buffer = new ChunkBuffer(clock);

        Assert.Equal(clock.GetUtcNow(), buffer.LastAppendedAt);

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.True(buffer.Append(Data("{}")));
        Assert.Equal(clock.GetUtcNow(), buffer.LastAppendedAt);

        // A rejected append does not refresh the idle clock.
        clock.Advance(TimeSpan.FromMinutes(2));
        buffer.Close();
        var rejected = buffer.Append(Data("{}"));
        Assert.False(rejected);
        Assert.Equal(clock.GetUtcNow() - TimeSpan.FromMinutes(2), buffer.LastAppendedAt);
    }

    private static StreamEvent Data(string payload)
        => new() { Type = "data", Data = Encoding.UTF8.GetBytes(payload) };
}
