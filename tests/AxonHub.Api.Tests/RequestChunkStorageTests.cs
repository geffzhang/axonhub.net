using System.Text;
using AxonHub.Api.Persistence;
using AxonHub.Llm.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Behavioural tests for the chunk persistence helpers ported from
/// biz.RequestService (request.go:1017-1111): which stream events are stored,
/// how binary payloads are summarized, and the exact wrapper JSON written to
/// the response_chunks columns.
/// </summary>
public sealed class RequestChunkStorageTests
{
    private static StreamEvent Chunk(string type, string data, string? lastEventId = null)
        => new()
        {
            Type = type,
            Data = Encoding.UTF8.GetBytes(data),
            LastEventId = lastEventId,
        };

    [Theory]
    [InlineData("audio/mpeg", true)]
    [InlineData("AUDIO/MPEG", true)]
    [InlineData(" audio/wav ", true)]
    [InlineData("application/octet-stream", true)]
    [InlineData("APPLICATION/OCTET-STREAM", true)]
    [InlineData("text/event-stream", false)]
    [InlineData("binary.done", false)]
    [InlineData("", false)]
    public void BinaryChunksAreDetectedLikeTheReference(string type, bool binary)
        => Assert.Equal(binary, RequestChunkStorage.IsBinaryStreamChunk(Chunk(type, "x")));

    [Fact]
    public void NullChunkIsNeitherBinaryNorStored()
    {
        Assert.False(RequestChunkStorage.IsBinaryStreamChunk(null));
        Assert.True(RequestChunkStorage.ShouldSkipStoredStreamChunk(null));
    }

    [Fact]
    public void DoneMarkerIsSkippedOnlyForTextChunks()
    {
        Assert.True(RequestChunkStorage.ShouldSkipStoredStreamChunk(Chunk(string.Empty, "[DONE]")));
        Assert.False(RequestChunkStorage.ShouldSkipStoredStreamChunk(Chunk("audio/mpeg", "[DONE]")));
        Assert.False(RequestChunkStorage.ShouldSkipStoredStreamChunk(Chunk(string.Empty, "[DONE] ")));
    }

    [Fact]
    public void BinaryDoneEventIsAlwaysSkipped()
    {
        var chunk = new StreamEvent
        {
            Type = AuthTypes.BinaryStreamDoneEventType,
            Data = Encoding.UTF8.GetBytes("payload"),
        };

        Assert.True(RequestChunkStorage.ShouldSkipStoredStreamChunk(chunk));
    }

    [Fact]
    public void TextChunkIsWrappedWithTypeAndRawData()
    {
        var chunk = Chunk("message", "{ \"a\" : 1 }");

        Assert.True(RequestChunkStorage.TryMarshalStreamEventForStorage(chunk, out var json));
        Assert.Equal("{\"event\":\"message\",\"data\":{\"a\":1}}", Encoding.UTF8.GetString(json));
    }

    [Fact]
    public void LastEventIdIsEmittedFirstAndOmittedWhenEmpty()
    {
        var withId = Chunk("message", "{}", "evt-7");
        Assert.True(RequestChunkStorage.TryMarshalStreamEventForStorage(withId, out var json));
        Assert.Equal(
            "{\"last_event_id\":\"evt-7\",\"event\":\"message\",\"data\":{}}",
            Encoding.UTF8.GetString(json));

        var withoutId = Chunk("message", "{}");
        Assert.True(RequestChunkStorage.TryMarshalStreamEventForStorage(withoutId, out var plain));
        Assert.Equal("{\"event\":\"message\",\"data\":{}}", Encoding.UTF8.GetString(plain));
    }

    [Fact]
    public void HtmlCharactersAreEscapedInTypeAndData()
    {
        var chunk = Chunk("a<b", "{\"x\":\"<&>\"}");

        Assert.True(RequestChunkStorage.TryMarshalStreamEventForStorage(chunk, out var json));
        Assert.Equal(
            "{\"event\":\"a\\u003cb\",\"data\":{\"x\":\"\\u003c\\u0026\\u003e\"}}",
            Encoding.UTF8.GetString(json));
    }

    [Fact]
    public void EmptyDataMarshalsAsNull()
    {
        var chunk = new StreamEvent { Type = "message" };

        Assert.True(RequestChunkStorage.TryMarshalStreamEventForStorage(chunk, out var json));
        Assert.Equal("{\"event\":\"message\",\"data\":null}", Encoding.UTF8.GetString(json));
    }

    [Fact]
    public void InvalidRawJsonIsSkipped()
    {
        var chunk = Chunk("message", "not-json");

        Assert.False(RequestChunkStorage.TryMarshalStreamEventForStorage(chunk, out var json));
        Assert.Empty(json);
    }

    [Fact]
    public void BinaryChunkIsSummarizedFromThePayloadLength()
    {
        var chunk = Chunk(" audio/mpeg ", "abcde");

        Assert.True(RequestChunkStorage.TryMarshalStreamEventForStorage(chunk, out var json));
        Assert.Equal(
            "{\"event\":\" audio/mpeg \",\"data\":"
            + "{\"object\":\"binary.stream_chunk\",\"content_type\":\"audio/mpeg\",\"bytes\":5}}",
            Encoding.UTF8.GetString(json));
    }

    [Fact]
    public void SummarizedBinaryChunkFallsBackToItsSize()
    {
        // The persistence layer replaces raw audio bytes with a size summary.
        var chunk = new StreamEvent { Type = "audio/mpeg", Size = 42 };

        Assert.True(RequestChunkStorage.TryMarshalStreamEventForStorage(chunk, out var json));
        Assert.Equal(
            "{\"event\":\"audio/mpeg\",\"data\":"
            + "{\"object\":\"binary.stream_chunk\",\"content_type\":\"audio/mpeg\",\"bytes\":42}}",
            Encoding.UTF8.GetString(json));
    }

    [Fact]
    public void MarshalChunksSkipsTerminalMarkersAndInvalidPayloads()
    {
        var chunks = new List<StreamEvent>
        {
            Chunk(string.Empty, "[DONE]"),
            Chunk("message", "{\"a\":1}"),
            new() { Type = AuthTypes.BinaryStreamDoneEventType, Data = Encoding.UTF8.GetBytes("x") },
            Chunk("message", "not-json"),
            Chunk("audio/mpeg", "[DONE]"),
        };

        var json = RequestChunkStorage.MarshalChunks(chunks);

        Assert.Equal(
            "[{\"event\":\"message\",\"data\":{\"a\":1}},"
            + "{\"event\":\"audio/mpeg\",\"data\":"
            + "{\"object\":\"binary.stream_chunk\",\"content_type\":\"audio/mpeg\",\"bytes\":6}}]",
            json);
    }

    [Fact]
    public void MarshalChunksReturnsAnEmptyArrayWhenNothingSurvives()
    {
        Assert.Equal("[]", RequestChunkStorage.MarshalChunks([]));
        Assert.Equal("[]", RequestChunkStorage.MarshalChunks([Chunk(string.Empty, "[DONE]")]));
    }

    [Fact]
    public void ExternalMarkerMatchesTheStoredForm()
        => Assert.Equal("[{\"_ext\":1}]", RequestChunkStorage.ExternalResponseChunksMarker);
}
