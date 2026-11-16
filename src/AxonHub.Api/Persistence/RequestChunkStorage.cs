using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Persistence;

/// <summary>
/// Port of the stream-event persistence helpers of biz.RequestService
/// (request.go:1017-1065, 1091-1111): filter terminal markers, summarize
/// binary payloads and emit the <c>{"last_event_id","event","data"}</c>
/// wrapper with Go's encoding/json behaviour.
/// </summary>
public static class RequestChunkStorage
{
    /// <summary>ExternalResponseChunksMarker (request.go:81).</summary>
    public const string ExternalResponseChunksMarker = "[{\"_ext\":1}]";

    private static ReadOnlySpan<byte> DoneStreamEventData => "[DONE]"u8;

    /// <summary>Port of isBinaryStreamChunk (request.go:1023-1031).</summary>
    public static bool IsBinaryStreamChunk(StreamEvent? chunk)
    {
        if (chunk is null)
        {
            return false;
        }

        var type = chunk.Type.Trim().ToLowerInvariant();
        return type.StartsWith("audio/", StringComparison.Ordinal)
            || type == "application/octet-stream";
    }

    /// <summary>Port of shouldSkipStoredStreamChunk (request.go:1033-1037).</summary>
    public static bool ShouldSkipStoredStreamChunk(StreamEvent? chunk)
        => chunk is null
            || (!IsBinaryStreamChunk(chunk) && chunk.Data.AsSpan().SequenceEqual(DoneStreamEventData))
            || chunk.Type == AuthTypes.BinaryStreamDoneEventType;

    /// <summary>
    /// Port of marshalStreamEventForStorage (request.go:1039-1065). Returns
    /// false when Go's json.Marshal would fail, which the reference treats as
    /// "skip this chunk".
    /// </summary>
    public static bool TryMarshalStreamEventForStorage(StreamEvent chunk, out byte[] json)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        json = [];

        byte[] data;
        if (IsBinaryStreamChunk(chunk))
        {
            // chunk.Size is set when the persistence layer replaced the raw
            // audio bytes with a length summary.
            var byteCount = chunk.Data.Length;
            if (byteCount == 0)
            {
                byteCount = chunk.Size;
            }

            data = GoJson.Marshal(new BinaryStreamChunkSummary
            {
                Object = "binary.stream_chunk",
                ContentType = chunk.Type.Trim(),
                Bytes = byteCount,
            });
        }
        else if (chunk.Data.Length == 0)
        {
            // json.RawMessage(nil) marshals to null.
            data = "null"u8.ToArray();
        }
        else
        {
            try
            {
                using var _ = JsonDocument.Parse(chunk.Data);
            }
            catch (JsonException)
            {
                // encoding/json rejects invalid RawMessage content.
                return false;
            }

            // RawMessage is compacted and HTML-escaped by the outer marshal.
            data = GoJson.MarshalRaw(chunk.Data);
        }

        var buffer = new List<byte>(data.Length + 64);
        buffer.Add((byte)'{');

        if (!string.IsNullOrEmpty(chunk.LastEventId))
        {
            AppendAscii(buffer, "\"last_event_id\":");
            buffer.AddRange(GoJson.Marshal(chunk.LastEventId));
            buffer.Add((byte)',');
        }

        AppendAscii(buffer, "\"event\":");
        buffer.AddRange(GoJson.Marshal(chunk.Type));
        AppendAscii(buffer, ",\"data\":");
        buffer.AddRange(data);
        buffer.Add((byte)'}');

        json = buffer.ToArray();
        return true;
    }

    /// <summary>
    /// Port of the SaveRequestChunks / SaveRequestExecutionChunks loop body:
    /// skip terminal markers, skip chunks that fail to marshal, and return the
    /// JSON array text (empty when nothing survives).
    /// </summary>
    public static string MarshalChunks(IEnumerable<StreamEvent> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        var parts = new List<byte[]>();
        foreach (var chunk in chunks)
        {
            if (ShouldSkipStoredStreamChunk(chunk))
            {
                continue;
            }

            if (TryMarshalStreamEventForStorage(chunk, out var json))
            {
                parts.Add(json);
            }
        }

        if (parts.Count == 0)
        {
            return "[]";
        }

        var buffer = new List<byte>();
        buffer.Add((byte)'[');
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0)
            {
                buffer.Add((byte)',');
            }

            buffer.AddRange(parts[i]);
        }

        buffer.Add((byte)']');

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void AppendAscii(List<byte> buffer, string text)
    {
        foreach (var c in text)
        {
            buffer.Add((byte)c);
        }
    }

    private sealed class BinaryStreamChunkSummary
    {
        [JsonPropertyName("object")]
        public string Object { get; init; } = string.Empty;

        [JsonPropertyName("content_type")]
        public string ContentType { get; init; } = string.Empty;

        [JsonPropertyName("bytes")]
        public int Bytes { get; init; }
    }
}
