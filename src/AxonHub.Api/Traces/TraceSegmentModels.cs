using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Traces;

/// <summary>
/// Port of <c>biz.Segment</c> (internal/server/biz/trace.go:260-276).
/// </summary>
/// <remarks>
/// The JSON field names, declaration order and <c>omitempty</c> flags mirror the
/// Go struct: <c>RawRootSegment</c> serializes this model with
/// <c>encoding/json</c> semantics (<see cref="TraceSegmentJson"/>), and the
/// management frontend parses the resulting bytes.
/// </remarks>
public sealed class TraceSegment
{
    public long Id { get; set; }

    public long? ParentId { get; set; }

    public string Model { get; set; } = string.Empty;

    public List<TraceSegment>? Children { get; set; }

    public List<TraceSpan>? RequestSpans { get; set; }

    public List<TraceSpan>? ResponseSpans { get; set; }

    public TraceRequestMetadata? Metadata { get; set; }

    public DateTime StartTime { get; set; }

    public DateTime EndTime { get; set; }

    /// <summary>Duration in milliseconds.</summary>
    public long Duration { get; set; }

    /// <summary>Port of <c>Segment.FirstUserQuery</c> (trace.go:278-301).</summary>
    public string? FirstUserQuery()
    {
        foreach (var span in RequestSpans ?? [])
        {
            if (span.Type == "user_query" && span.Value?.UserQuery is { } userQuery)
            {
                return userQuery.Text;
            }
        }

        foreach (var child in Children ?? [])
        {
            if (child.FirstUserQuery() is { } query)
            {
                return query;
            }
        }

        return null;
    }

    /// <summary>Port of <c>Segment.FirstText</c> (trace.go:303-326).</summary>
    public string? FirstText()
    {
        foreach (var span in ResponseSpans ?? [])
        {
            if (span.Type == "text" && span.Value?.Text is { } text)
            {
                return text.Text;
            }
        }

        foreach (var child in Children ?? [])
        {
            if (child.FirstText() is { } text)
            {
                return text;
            }
        }

        return null;
    }
}

/// <summary>Port of <c>biz.Span</c> (trace.go:328-350).</summary>
public sealed class TraceSpan
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public DateTime StartTime { get; set; }

    public DateTime EndTime { get; set; }

    public TraceSpanValue? Value { get; set; }
}

/// <summary>Port of <c>biz.SpanValue</c> (trace.go:352-368).</summary>
public sealed class TraceSpanValue
{
    public TraceSpanSystemInstruction? SystemInstruction { get; set; }

    public TraceSpanUserQuery? UserQuery { get; set; }

    public TraceSpanUserImageUrl? UserImageURL { get; set; }

    public TraceSpanUserVideoUrl? UserVideoURL { get; set; }

    public TraceSpanUserInputAudio? UserInputAudio { get; set; }

    public TraceSpanText? Text { get; set; }

    public TraceSpanThinking? Thinking { get; set; }

    public TraceSpanImageUrl? ImageURL { get; set; }

    public TraceSpanVideoUrl? VideoURL { get; set; }

    public TraceSpanAudio? Audio { get; set; }

    public TraceSpanToolUse? ToolUse { get; set; }

    public TraceSpanToolResult? ToolResult { get; set; }

    public TraceSpanCompaction? Compaction { get; set; }
}

public sealed class TraceSpanSystemInstruction
{
    public string Instruction { get; set; } = string.Empty;
}

public sealed class TraceSpanUserQuery
{
    public string Text { get; set; } = string.Empty;
}

public sealed class TraceSpanUserImageUrl
{
    public string URL { get; set; } = string.Empty;
}

public sealed class TraceSpanUserVideoUrl
{
    public string URL { get; set; } = string.Empty;
}

public sealed class TraceSpanUserInputAudio
{
    public string Format { get; set; } = string.Empty;

    public string Data { get; set; } = string.Empty;
}

public sealed class TraceSpanThinking
{
    public string Thinking { get; set; } = string.Empty;
}

public sealed class TraceSpanText
{
    public string Text { get; set; } = string.Empty;
}

public sealed class TraceSpanImageUrl
{
    public string URL { get; set; } = string.Empty;
}

public sealed class TraceSpanVideoUrl
{
    public string URL { get; set; } = string.Empty;
}

public sealed class TraceSpanAudio
{
    public string Id { get; set; } = string.Empty;

    public string Format { get; set; } = string.Empty;

    public string Data { get; set; } = string.Empty;

    public string Transcript { get; set; } = string.Empty;
}

public sealed class TraceSpanToolUse
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    /// <summary>Go emits this field even when empty (no omitempty).</summary>
    public string Name { get; set; } = string.Empty;

    public string? Arguments { get; set; }
}

public sealed class TraceSpanToolResult
{
    public string ToolCallId { get; set; } = string.Empty;

    public bool IsError { get; set; }

    public string? Text { get; set; }
}

public sealed class TraceSpanCompaction
{
    public string Summary { get; set; } = string.Empty;
}

/// <summary>Port of <c>biz.RequestMetadata</c> (trace.go:417-425).</summary>
public sealed class TraceRequestMetadata
{
    public int? ItemCount { get; set; }

    public long? InputTokens { get; set; }

    public long? OutputTokens { get; set; }

    public long? TotalTokens { get; set; }

    public long? CachedTokens { get; set; }
}

/// <summary>
/// Go <c>encoding/json</c> emission for the trace segment tree.
/// </summary>
/// <remarks>
/// Field order follows the Go struct declaration order and every optional field
/// is omitted when nil/empty, exactly like <c>omitempty</c>. Timestamps use
/// <see cref="GoTime.Rfc3339Nano"/>; the final byte pass applies Go's HTML
/// escaping (<see cref="GoJson.Escape"/>), which the reference gets from
/// <c>json.Marshal</c>. A nil segment marshals to <c>null</c>.
/// </remarks>
public static class TraceSegmentJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
        SkipValidation = false,
    };

    public static byte[] SerializeToUtf8Bytes(TraceSegment? segment)
    {
        if (segment is null)
        {
            return "null"u8.ToArray();
        }

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            WriteSegment(writer, segment);
        }

        return GoJson.Escape(buffer.ToArray());
    }

    public static string Serialize(TraceSegment? segment)
        => Encoding.UTF8.GetString(SerializeToUtf8Bytes(segment));

    /// <summary>Parses the serialized tree for the GraphQL scalar.</summary>
    public static JsonElement? SerializeToElement(TraceSegment? segment)
    {
        using var document = JsonDocument.Parse(SerializeToUtf8Bytes(segment));
        return document.RootElement.Clone();
    }

    private static void WriteSegment(Utf8JsonWriter writer, TraceSegment segment)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", segment.Id);
        if (segment.ParentId is { } parentId)
        {
            writer.WriteNumber("parentId", parentId);
        }

        writer.WriteString("model", segment.Model);

        if (segment.Children is { Count: > 0 })
        {
            writer.WritePropertyName("children");
            writer.WriteStartArray();
            foreach (var child in segment.Children)
            {
                WriteSegment(writer, child);
            }

            writer.WriteEndArray();
        }

        WriteSpanList(writer, "requestSpans", segment.RequestSpans);
        WriteSpanList(writer, "responseSpans", segment.ResponseSpans);

        if (segment.Metadata is { } metadata)
        {
            writer.WritePropertyName("metadata");
            WriteMetadata(writer, metadata);
        }

        writer.WriteString("startTime", GoTime.Rfc3339Nano(segment.StartTime));
        writer.WriteString("endTime", GoTime.Rfc3339Nano(segment.EndTime));
        writer.WriteNumber("duration", segment.Duration);
        writer.WriteEndObject();
    }

    private static void WriteSpanList(
        Utf8JsonWriter writer,
        string name,
        List<TraceSpan>? spans)
    {
        if (spans is not { Count: > 0 })
        {
            return;
        }

        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var span in spans)
        {
            WriteSpan(writer, span);
        }

        writer.WriteEndArray();
    }

    private static void WriteSpan(Utf8JsonWriter writer, TraceSpan span)
    {
        writer.WriteStartObject();
        writer.WriteString("id", span.Id);
        writer.WriteString("type", span.Type);
        writer.WriteString("startTime", GoTime.Rfc3339Nano(span.StartTime));
        writer.WriteString("endTime", GoTime.Rfc3339Nano(span.EndTime));
        if (span.Value is { } value)
        {
            writer.WritePropertyName("value");
            WriteSpanValue(writer, value);
        }

        writer.WriteEndObject();
    }

    private static void WriteSpanValue(Utf8JsonWriter writer, TraceSpanValue value)
    {
        writer.WriteStartObject();

        if (value.SystemInstruction is { } systemInstruction)
        {
            writer.WritePropertyName("systemInstruction");
            writer.WriteStartObject();
            WriteNonEmpty(writer, "instruction", systemInstruction.Instruction);
            writer.WriteEndObject();
        }

        if (value.UserQuery is { } userQuery)
        {
            writer.WritePropertyName("userQuery");
            writer.WriteStartObject();
            WriteNonEmpty(writer, "text", userQuery.Text);
            writer.WriteEndObject();
        }

        if (value.UserImageURL is { } userImage)
        {
            writer.WritePropertyName("userImageUrl");
            writer.WriteStartObject();
            WriteNonEmpty(writer, "url", userImage.URL);
            writer.WriteEndObject();
        }

        if (value.UserVideoURL is { } userVideo)
        {
            writer.WritePropertyName("userVideoUrl");
            writer.WriteStartObject();
            WriteNonEmpty(writer, "url", userVideo.URL);
            writer.WriteEndObject();
        }

        if (value.UserInputAudio is { } userInputAudio)
        {
            writer.WritePropertyName("userInputAudio");
            writer.WriteStartObject();
            WriteNonEmpty(writer, "format", userInputAudio.Format);
            WriteNonEmpty(writer, "data", userInputAudio.Data);
            writer.WriteEndObject();
        }

        if (value.Text is { } text)
        {
            writer.WritePropertyName("text");
            writer.WriteStartObject();
            WriteNonEmpty(writer, "text", text.Text);
            writer.WriteEndObject();
        }

        if (value.Thinking is { } thinking)
        {
            writer.WritePropertyName("thinking");
            writer.WriteStartObject();
            WriteNonEmpty(writer, "thinking", thinking.Thinking);
            writer.WriteEndObject();
        }

        if (value.ImageURL is { } imageUrl)
        {
            writer.WritePropertyName("imageUrl");
            writer.WriteStartObject();
            WriteNonEmpty(writer, "url", imageUrl.URL);
            writer.WriteEndObject();
        }

        if (value.VideoURL is { } videoUrl)
        {
            writer.WritePropertyName("videoUrl");
            writer.WriteStartObject();
            WriteNonEmpty(writer, "url", videoUrl.URL);
            writer.WriteEndObject();
        }

        if (value.Audio is { } audio)
        {
            writer.WritePropertyName("audio");
            writer.WriteStartObject();
            WriteNonEmpty(writer, "id", audio.Id);
            WriteNonEmpty(writer, "format", audio.Format);
            WriteNonEmpty(writer, "data", audio.Data);
            WriteNonEmpty(writer, "transcript", audio.Transcript);
            writer.WriteEndObject();
        }

        if (value.ToolUse is { } toolUse)
        {
            writer.WritePropertyName("toolUse");
            writer.WriteStartObject();
            WriteNonEmpty(writer, "id", toolUse.Id);
            WriteNonEmpty(writer, "type", toolUse.Type);
            // No omitempty on the Go field.
            writer.WriteString("name", toolUse.Name);
            if (toolUse.Arguments is not null)
            {
                writer.WriteString("arguments", toolUse.Arguments);
            }

            writer.WriteEndObject();
        }

        if (value.ToolResult is { } toolResult)
        {
            writer.WritePropertyName("toolResult");
            writer.WriteStartObject();
            WriteNonEmpty(writer, "id", toolResult.ToolCallId);
            if (toolResult.IsError)
            {
                writer.WriteBoolean("error", true);
            }

            if (toolResult.Text is not null)
            {
                writer.WriteString("text", toolResult.Text);
            }

            writer.WriteEndObject();
        }

        if (value.Compaction is { } compaction)
        {
            writer.WritePropertyName("compaction");
            writer.WriteStartObject();
            WriteNonEmpty(writer, "summary", compaction.Summary);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteMetadata(Utf8JsonWriter writer, TraceRequestMetadata metadata)
    {
        writer.WriteStartObject();
        if (metadata.ItemCount is { } itemCount)
        {
            writer.WriteNumber("itemCount", itemCount);
        }

        if (metadata.InputTokens is { } inputTokens)
        {
            writer.WriteNumber("inputTokens", inputTokens);
        }

        if (metadata.OutputTokens is { } outputTokens)
        {
            writer.WriteNumber("outputTokens", outputTokens);
        }

        if (metadata.TotalTokens is { } totalTokens)
        {
            writer.WriteNumber("totalTokens", totalTokens);
        }

        if (metadata.CachedTokens is { } cachedTokens)
        {
            writer.WriteNumber("cachedTokens", cachedTokens);
        }

        writer.WriteEndObject();
    }

    private static void WriteNonEmpty(Utf8JsonWriter writer, string name, string value)
    {
        if (value.Length > 0)
        {
            writer.WriteString(name, value);
        }
    }
}

/// <summary>
/// Go <c>encoding/json</c> re-marshalling helpers used by the segment builder.
/// </summary>
public static class TraceJson
{
    /// <summary>
    /// Port of <c>normalizeJSON</c> (trace.go:1833-1846): decode into Go's
    /// <c>any</c> (numbers become float64, objects become maps with sorted
    /// keys) and re-encode. Invalid JSON and values that cannot be represented
    /// as float64 are returned unchanged.
    /// </summary>
    public static string NormalizeJson(string text)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return text;
        }

        using (document)
        {
            var buffer = new MemoryStream();
            var success = true;
            using (var writer = new Utf8JsonWriter(
                       buffer,
                       new JsonWriterOptions
                       {
                           Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                           Indented = false,
                       }))
            {
                success = TryWriteGoAny(writer, document.RootElement);
            }

            return success
                ? Encoding.UTF8.GetString(GoJson.Escape(buffer.ToArray()))
                : text;
        }
    }

    /// <summary>
    /// Port of the decisions answer re-marshal (trace.go:824-831): a JSON
    /// object decoded into <c>map[string]json.RawMessage</c> and marshalled
    /// back, which sorts the keys, keeps the last duplicate and compacts the
    /// raw values. A JSON null decodes into a nil map and marshals back as
    /// <c>null</c>; every other non-object kind fails to decode.
    /// </summary>
    public static string? RemarshalRawObject(JsonElement answer)
    {
        if (answer.ValueKind == JsonValueKind.Null)
        {
            return "null";
        }

        if (answer.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // A Go map keeps the last value for a duplicated key.
        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in answer.EnumerateObject())
        {
            members[property.Name] = property.Value.GetRawText();
        }

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                       Indented = false,
                   }))
        {
            writer.WriteStartObject();
            foreach (var name in members.Keys.OrderBy(static key => key, StringComparer.Ordinal))
            {
                writer.WritePropertyName(name);
                writer.WriteRawValue(
                    GoJson.Compact(Encoding.UTF8.GetBytes(members[name])),
                    skipInputValidation: false);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(GoJson.Escape(buffer.ToArray()));
    }

    private static bool TryWriteGoAny(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var members = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    members[property.Name] = property.Value;
                }

                writer.WriteStartObject();
                foreach (var name in members.Keys.OrderBy(static key => key, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(name);
                    if (!TryWriteGoAny(writer, members[name]))
                    {
                        return false;
                    }
                }

                writer.WriteEndObject();
                return true;
            }

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    if (!TryWriteGoAny(writer, item))
                    {
                        return false;
                    }
                }

                writer.WriteEndArray();
                return true;

            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                return true;

            case JsonValueKind.Number:
            {
                var raw = element.GetRawText();
                if (!double.TryParse(
                        raw,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var number)
                    || !double.IsFinite(number))
                {
                    // Go's Unmarshal into any fails for float64 overflow.
                    return false;
                }

                writer.WriteRawValue(GoJsonFloat.Format(number), skipInputValidation: true);
                return true;
            }

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return true;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return true;

            default:
                writer.WriteNullValue();
                return true;
        }
    }
}
