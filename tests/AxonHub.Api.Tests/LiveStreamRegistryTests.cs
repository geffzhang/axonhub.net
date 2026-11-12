using System.Text;
using AxonHub.Api.Persistence;
using AxonHub.Llm.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks biz.LiveStreamRegistry (internal/server/biz/stream_preview.go): the
/// registry lookups, the preview marshal shape shared with SaveRequestChunks,
/// and the sweeper that closes closed or ten-minute-idle buffers.
/// </summary>
public sealed class LiveStreamRegistryTests
{
    [Fact]
    public void UnknownIdsHaveNoBufferAndNoChunks()
    {
        var registry = new LiveStreamRegistry();

        Assert.Null(registry.GetRequestBuffer(42));
        Assert.Null(registry.GetExecutionBuffer(7));
        Assert.Empty(registry.GetRequestChunks(42));
        Assert.Empty(registry.GetExecutionChunks(7));
    }

    [Fact]
    public void RegisteredBuffersAreReturnedUntilUnregistered()
    {
        var registry = new LiveStreamRegistry();
        var requestBuffer = registry.CreateBuffer();
        var executionBuffer = registry.CreateBuffer();

        registry.RegisterRequest(42, requestBuffer);
        registry.RegisterExecution(7, executionBuffer);

        Assert.Same(requestBuffer, registry.GetRequestBuffer(42));
        Assert.Same(executionBuffer, registry.GetExecutionBuffer(7));

        // Re-registering the same id replaces the reference (sync.Map.Store).
        var replacement = registry.CreateBuffer();
        registry.RegisterRequest(42, replacement);
        Assert.Same(replacement, registry.GetRequestBuffer(42));

        registry.UnregisterRequest(42);
        registry.UnregisterExecution(7);
        Assert.Null(registry.GetRequestBuffer(42));
        Assert.Null(registry.GetExecutionBuffer(7));
    }

    [Fact]
    public void PreviewChunksSkipTerminalMarkersAndBinaryDone()
    {
        var registry = new LiveStreamRegistry();
        var buffer = registry.CreateBuffer();
        buffer.Append(Data("data", "{\"a\":1}"));
        buffer.Append(Data("data", "[DONE]"));
        buffer.Append(new StreamEvent { Type = AuthTypes.BinaryStreamDoneEventType });
        buffer.Append(Data("data", "{\"b\":2}"));
        registry.RegisterRequest(42, buffer);

        Assert.Equal(
            new[] { "{\"event\":\"data\",\"data\":{\"a\":1}}", "{\"event\":\"data\",\"data\":{\"b\":2}}" },
            registry.GetRequestChunks(42));
    }

    [Fact]
    public void PreviewChunksSummarizeBinaryAudio()
    {
        var registry = new LiveStreamRegistry();
        var buffer = registry.CreateBuffer();
        buffer.Append(new StreamEvent
        {
            Type = "audio/mp3",
            Data = "abcdefgh"u8.ToArray(),
            Size = 8,
        });
        registry.RegisterExecution(7, buffer);

        var chunk = Assert.Single(registry.GetExecutionChunks(7));
        Assert.Equal(
            "{\"event\":\"audio/mp3\",\"data\":{\"object\":\"binary.stream_chunk\","
            + "\"content_type\":\"audio/mp3\",\"bytes\":8}}",
            chunk);
    }

    [Fact]
    public void PreviewChunksSkipInvalidJsonAndNullPayloadsMarshalAsNull()
    {
        var registry = new LiveStreamRegistry();
        var buffer = registry.CreateBuffer();
        buffer.Append(Data("data", "not-json"));
        buffer.Append(new StreamEvent { Type = "message" });
        registry.RegisterRequest(42, buffer);

        Assert.Equal(
            new[] { "{\"event\":\"message\",\"data\":null}" },
            registry.GetRequestChunks(42));
    }

    [Fact]
    public void PreviewChunksKeepTheLastEventId()
    {
        var registry = new LiveStreamRegistry();
        var buffer = registry.CreateBuffer();
        buffer.Append(new StreamEvent
        {
            LastEventId = "evt-1",
            Type = "data",
            Data = "{\"a\":1}"u8.ToArray(),
        });
        registry.RegisterRequest(42, buffer);

        Assert.Equal(
            new[] { "{\"last_event_id\":\"evt-1\",\"event\":\"data\",\"data\":{\"a\":1}}" },
            registry.GetRequestChunks(42));
    }

    [Fact]
    public void SweepRemovesClosedBuffers()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero));
        var registry = new LiveStreamRegistry(clock);
        var buffer = registry.CreateBuffer();
        registry.RegisterRequest(42, buffer);
        var executionBuffer = registry.CreateBuffer();
        registry.RegisterExecution(7, executionBuffer);

        Assert.Equal(0, registry.SweepStaleEntries(clock.GetUtcNow()));

        buffer.Close();

        Assert.Equal(1, registry.SweepStaleEntries(clock.GetUtcNow()));
        Assert.Null(registry.GetRequestBuffer(42));
        Assert.Same(executionBuffer, registry.GetExecutionBuffer(7));
    }

    [Fact]
    public void SweepClosesBuffersIdleForMoreThanTenMinutes()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero));
        var registry = new LiveStreamRegistry(clock);
        var buffer = registry.CreateBuffer();
        registry.RegisterRequest(42, buffer);

        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, registry.SweepStaleEntries(clock.GetUtcNow()));
        Assert.False(buffer.IsClosed);

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, registry.SweepStaleEntries(clock.GetUtcNow()));

        // The zombie is closed before it is dropped so a preview reader that
        // still holds the buffer sees the completed end (stream_preview.go:150).
        Assert.True(buffer.IsClosed);
        Assert.Null(registry.GetRequestBuffer(42));
    }

    [Fact]
    public void SweepKeepsBuffersThatKeepAppending()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero));
        var registry = new LiveStreamRegistry(clock);
        var buffer = registry.CreateBuffer();
        registry.RegisterRequest(42, buffer);

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.True(buffer.Append(Data("data", "{}")));

        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, registry.SweepStaleEntries(clock.GetUtcNow()));
        Assert.Same(buffer, registry.GetRequestBuffer(42));
    }

    [Fact]
    public void SweptBuffersStopProducingPreviewChunks()
    {
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero));
        var registry = new LiveStreamRegistry(clock);
        var buffer = registry.CreateBuffer();
        buffer.Append(Data("data", "{\"a\":1}"));
        registry.RegisterRequest(42, buffer);

        clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, registry.SweepStaleEntries(clock.GetUtcNow()));

        // After eviction the loaders fall back to persisted chunks.
        Assert.Empty(registry.GetRequestChunks(42));
    }

    private static StreamEvent Data(string type, string payload)
        => new() { Type = type, Data = Encoding.UTF8.GetBytes(payload) };
}
