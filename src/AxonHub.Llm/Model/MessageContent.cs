using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Llm;

/// <summary>
/// Port of llm.MessageContent.
/// Wire shape: a bare string, an array of content parts, or null.
/// A single text part collapses back to a bare string (llm/model.go:497-507).
/// </summary>
[JsonConverter(typeof(MessageContentJsonConverter))]
public sealed class MessageContent
{
    public string? Content { get; set; }

    public List<MessageContentPart>? MultipleContent { get; set; }

    /// <summary>Go's zero value: both representations absent.</summary>
    [JsonIgnore]
    public bool IsZero => Content is null && (MultipleContent is null || MultipleContent.Count == 0);

    public static MessageContent FromText(string text) => new() { Content = text };

    public static MessageContent FromParts(List<MessageContentPart> parts) => new() { MultipleContent = parts };

    /// <summary>Go: MessageContent.MarshalJSON.</summary>
    internal object? ToJsonValue()
    {
        if (MultipleContent is { Count: > 0 })
        {
            if (MultipleContent.Count == 1 && MultipleContent[0].Type == "text")
            {
                return MultipleContent[0].Text;
            }

            return MultipleContent;
        }

        return Content;
    }
}

public sealed class MessageContentJsonConverter : JsonConverter<MessageContent>
{
    public override MessageContent? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            return MessageContent.FromText(reader.GetString()!);
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var parts = JsonSerializer.Deserialize<List<MessageContentPart>>(ref reader, options);
            return MessageContent.FromParts(parts ?? []);
        }

        throw new JsonException("invalid content type");
    }

    public override void Write(Utf8JsonWriter writer, MessageContent value, JsonSerializerOptions options)
    {
        var json = value.ToJsonValue();
        if (json is null)
        {
            writer.WriteNullValue();
            return;
        }

        JsonSerializer.Serialize(writer, json, json.GetType(), options);
    }
}

/// <summary>Port of llm.MessageContentPart (chat-relevant parts).</summary>
public sealed class MessageContentPart
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImageUrl? ImageUrl { get; set; }

    [JsonPropertyName("video_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VideoURL? VideoUrl { get; set; }

    [JsonPropertyName("document")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DocumentUrl? Document { get; set; }

    [JsonPropertyName("input_audio")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InputAudio? InputAudio { get; set; }

    [JsonPropertyName("compact")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompactContent? Compact { get; set; }

    [JsonPropertyName("cache_control")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CacheControl? CacheControl { get; set; }

    [JsonPropertyName("transformer_metadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? TransformerMetadata { get; set; }
}

public sealed class ImageUrl
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("mime_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }

    [JsonPropertyName("detail")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; set; }
}

public sealed class DocumentUrl
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("file_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileId { get; set; }

    [JsonPropertyName("filename")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Filename { get; set; }

    [JsonPropertyName("mime_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }
}

public sealed class InputAudio
{
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public string Data { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("mime_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MimeType { get; set; }
}

public sealed class CompactContent
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("encrypted_content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncryptedContent { get; set; }

    [JsonPropertyName("created_by")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedBy { get; set; }
}

public sealed class CacheControl
{
    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    [JsonPropertyName("ttl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ttl { get; set; }
}
