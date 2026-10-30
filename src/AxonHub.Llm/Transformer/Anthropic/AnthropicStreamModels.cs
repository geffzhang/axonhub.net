using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Port of anthropic.StreamEvent (llm/transformer/anthropic/model.go:448-467).
/// Field order matches the Go struct because Go marshals in declaration order
/// and the streamed bytes are compared verbatim.
/// </summary>
[JsonConverter(typeof(AnthropicStreamEventJsonConverter))]
public sealed class AnthropicStreamEvent
{
    /// <summary>Any of message_start, message_delta, message_stop,
    /// content_block_start, content_block_delta, content_block_stop.</summary>
    public string Type { get; set; } = string.Empty;

    public AnthropicStreamMessage? Message { get; set; }

    /// <summary>Go *int64: index 0 must still be written, so null is the only omission.</summary>
    public long? Index { get; set; }

    public AnthropicMessageContentBlock? ContentBlock { get; set; }

    public AnthropicStreamDelta? Delta { get; set; }

    public AnthropicUsage? Usage { get; set; }
}

/// <summary>
/// Port of StreamEvent's plain encoding/json shape. Hand-written because the
/// shared content-block converter decodes a JSON null to a zero block (Go does
/// that only for array elements), while this pointer field must stay null.
/// </summary>
public sealed class AnthropicStreamEventJsonConverter : JsonConverter<AnthropicStreamEvent>
{
    public override AnthropicStreamEvent Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("invalid stream event");
        }

        var streamEvent = new AnthropicStreamEvent();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return streamEvent;
            }

            // Go matches the tag exactly first, then case-insensitively; the
            // manual reader mirrors the fold (all tags are lowercase ASCII).
            var property = reader.GetString()?.ToLowerInvariant();
            reader.Read();

            switch (property)
            {
                case "type":
                    streamEvent.Type = ReadString(ref reader) ?? string.Empty;
                    break;
                case "message":
                    streamEvent.Message = reader.TokenType == JsonTokenType.Null
                        ? null
                        : JsonSerializer.Deserialize<AnthropicStreamMessage>(ref reader, options);
                    break;
                case "index":
                    streamEvent.Index = ReadInt64(ref reader);
                    break;
                case "content_block":
                    streamEvent.ContentBlock = reader.TokenType == JsonTokenType.Null
                        ? null
                        : JsonSerializer.Deserialize<AnthropicMessageContentBlock>(ref reader, options);
                    break;
                case "delta":
                    streamEvent.Delta = reader.TokenType == JsonTokenType.Null
                        ? null
                        : JsonSerializer.Deserialize<AnthropicStreamDelta>(ref reader, options);
                    break;
                case "usage":
                    streamEvent.Usage = reader.TokenType == JsonTokenType.Null
                        ? null
                        : JsonSerializer.Deserialize<AnthropicUsage>(ref reader, options);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("invalid stream event");
    }

    public override void Write(
        Utf8JsonWriter writer,
        AnthropicStreamEvent value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString("type", value.Type ?? string.Empty);

        if (value.Message is not null)
        {
            writer.WritePropertyName("message");
            JsonSerializer.Serialize(writer, value.Message, options);
        }

        if (value.Index is { } index)
        {
            writer.WriteNumber("index", index);
        }

        if (value.ContentBlock is not null)
        {
            writer.WritePropertyName("content_block");
            JsonSerializer.Serialize(writer, value.ContentBlock, options);
        }

        if (value.Delta is not null)
        {
            writer.WritePropertyName("delta");
            JsonSerializer.Serialize(writer, value.Delta, options);
        }

        if (value.Usage is not null)
        {
            writer.WritePropertyName("usage");
            JsonSerializer.Serialize(writer, value.Usage, options);
        }

        writer.WriteEndObject();
    }

    private static string? ReadString(ref Utf8JsonReader reader)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            _ => throw new JsonException("expected string"),
        };

    private static long? ReadInt64(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out var value))
        {
            throw new JsonException("expected integer");
        }

        return value;
    }
}

/// <summary>
/// Port of anthropic.StreamMessage (model.go:519-526). Unlike the other stream
/// types it has no custom marshaler: content is always written (null or []),
/// usage is omitted when absent.
/// </summary>
public sealed class AnthropicStreamMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public List<AnthropicMessageContentBlock>? Content { get; set; }

    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AnthropicUsage? Usage { get; set; }
}

/// <summary>
/// Port of anthropic.StreamDelta (model.go:470-516), including the custom
/// marshal: a thinking_delta carries only type and thinking.
/// </summary>
[JsonConverter(typeof(AnthropicStreamDeltaJsonConverter))]
public sealed class AnthropicStreamDelta
{
    public string? Type { get; set; }

    public string? Text { get; set; }

    public string? PartialJson { get; set; }

    public AnthropicTextCitation? Citation { get; set; }

    public string? Thinking { get; set; }

    public string? Signature { get; set; }

    public string? StopReason { get; set; }

    public string? StopSequence { get; set; }
}

public sealed class AnthropicStreamDeltaJsonConverter : JsonConverter<AnthropicStreamDelta>
{
    public override AnthropicStreamDelta Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("invalid stream delta");
        }

        var delta = new AnthropicStreamDelta();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return delta;
            }

            var property = reader.GetString()?.ToLowerInvariant();
            reader.Read();

            switch (property)
            {
                case "type":
                    delta.Type = ReadString(ref reader);
                    break;
                case "text":
                    delta.Text = ReadString(ref reader);
                    break;
                case "partial_json":
                    delta.PartialJson = ReadString(ref reader);
                    break;
                case "citation":
                    // The shared citation converter decodes null to a zero
                    // citation; this pointer field must stay null, as in Go.
                    delta.Citation = reader.TokenType == JsonTokenType.Null
                        ? null
                        : JsonSerializer.Deserialize<AnthropicTextCitation>(ref reader, options);
                    break;
                case "thinking":
                    delta.Thinking = ReadString(ref reader);
                    break;
                case "signature":
                    delta.Signature = ReadString(ref reader);
                    break;
                case "stop_reason":
                    delta.StopReason = ReadString(ref reader);
                    break;
                case "stop_sequence":
                    delta.StopSequence = ReadString(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("invalid stream delta");
    }

    public override void Write(
        Utf8JsonWriter writer,
        AnthropicStreamDelta value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();

        if (value.Type == "thinking_delta")
        {
            // model.go:500-516: the thinking variant shadows the alias and only
            // carries type and thinking.
            writer.WriteString("type", value.Type);

            if (value.Thinking is not null)
            {
                writer.WriteString("thinking", value.Thinking);
            }

            writer.WriteEndObject();
            return;
        }

        if (value.Type is not null)
        {
            writer.WriteString("type", value.Type);
        }

        if (value.Text is not null)
        {
            writer.WriteString("text", value.Text);
        }

        if (value.PartialJson is not null)
        {
            writer.WriteString("partial_json", value.PartialJson);
        }

        if (value.Citation is not null)
        {
            writer.WritePropertyName("citation");
            JsonSerializer.Serialize(writer, value.Citation, options);
        }

        if (value.Thinking is not null)
        {
            writer.WriteString("thinking", value.Thinking);
        }

        if (value.Signature is not null)
        {
            writer.WriteString("signature", value.Signature);
        }

        if (value.StopReason is not null)
        {
            writer.WriteString("stop_reason", value.StopReason);
        }

        if (value.StopSequence is not null)
        {
            writer.WriteString("stop_sequence", value.StopSequence);
        }

        writer.WriteEndObject();
    }

    private static string? ReadString(ref Utf8JsonReader reader)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            _ => throw new JsonException("expected string"),
        };
}
