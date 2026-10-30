using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Port of the Anthropic-specific JSON helpers: transformer-metadata keys
/// (tool_blocks.go), content-block marshal quirks (model.go:409-428), data URL
/// parsing (internal/pkg/xurl), safe raw JSON (internal/pkg/xjson), thinking
/// signatures and the Read-tool argument sanitizer.
/// </summary>
internal static class AnthropicJson
{
    // TransformerMetadata keys (tool_blocks.go:14-36).
    public const string KeyThinkingType = "thinking_type";
    public const string KeyOutputConfigEffort = "output_config_effort";
    public const string KeyThinkingDisplay = "thinking_display";
    public const string KeyCacheControl = "anthropic_cache_control";
    public const string KeyResponseContent = "anthropic_response_content";
    public const string KeyAnthropicType = "anthropic_type";
    public const string KeyAnthropicCaller = "anthropic_caller";
    public const string KeyAnthropicToolResultContent = "anthropic_tool_result_content";
    public const string KeyAnthropicBlockIndex = "anthropic_block_index";

    /// <summary>Port of isAnthropicSpecialToolUseBlock.</summary>
    public static bool IsSpecialToolUseBlock(string? blockType)
        => !string.Equals(blockType, "tool_use", StringComparison.Ordinal)
           && (blockType?.EndsWith("_tool_use", StringComparison.Ordinal) ?? false);

    /// <summary>Port of isAnthropicSpecialToolResultBlock.</summary>
    public static bool IsSpecialToolResultBlock(string? blockType)
        => !string.Equals(blockType, "tool_result", StringComparison.Ordinal)
           && (blockType?.EndsWith("_tool_result", StringComparison.Ordinal) ?? false);

    /// <summary>
    /// Port of setAnthropicSpecialMeta. Returns the (possibly created) map so
    /// callers can assign it back to the property that owns it.
    /// </summary>
    public static Dictionary<string, JsonElement>? SetSpecialMeta(
        Dictionary<string, JsonElement>? metadata,
        string? blockType,
        JsonElement? caller)
    {
        if (!IsSpecialToolUseBlock(blockType) && !IsSpecialToolResultBlock(blockType))
        {
            return metadata;
        }

        metadata ??= [];
        metadata[KeyAnthropicType] = JsonSerializer.SerializeToElement(blockType!);
        if (caller is { } value)
        {
            metadata[KeyAnthropicCaller] = value;
        }

        return metadata;
    }

    /// <summary>Port of setAnthropicToolResultContent.</summary>
    public static Dictionary<string, JsonElement>? SetToolResultContent(
        Dictionary<string, JsonElement>? metadata,
        byte[]? content)
    {
        if (content is not { Length: > 0 })
        {
            return metadata;
        }

        metadata ??= [];
        metadata[KeyAnthropicToolResultContent] = RawElement(content);
        return metadata;
    }

    /// <summary>Port of getAnthropicType.</summary>
    public static string GetAnthropicType(Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is null
            || !metadata.TryGetValue(KeyAnthropicType, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return value.GetString() ?? string.Empty;
    }

    /// <summary>Port of getAnthropicCaller / asJSONRawMessage.</summary>
    public static JsonElement? GetAnthropicCaller(Dictionary<string, JsonElement>? metadata)
        => GetRawMetadata(metadata, KeyAnthropicCaller);

    /// <summary>Port of getAnthropicToolResultContent / asJSONRawMessage.</summary>
    public static JsonElement? GetAnthropicToolResultContent(Dictionary<string, JsonElement>? metadata)
        => GetRawMetadata(metadata, KeyAnthropicToolResultContent);

    private static JsonElement? GetRawMetadata(
        Dictionary<string, JsonElement>? metadata,
        string key)
    {
        if (metadata is null || !metadata.TryGetValue(key, out var value))
        {
            return null;
        }

        // Go's asJSONRawMessage marshals anything that is not already raw
        // bytes or a string; a JsonElement is exactly the stored JSON value.
        return value.ValueKind == JsonValueKind.Null ? null : value;
    }

    /// <summary>Port of setAnthropicBlockIndex.</summary>
    public static Dictionary<string, JsonElement>? SetBlockIndex(
        Dictionary<string, JsonElement>? metadata,
        int index)
    {
        metadata ??= [];
        metadata[KeyAnthropicBlockIndex] = JsonSerializer.SerializeToElement(index);
        return metadata;
    }

    /// <summary>Port of getAnthropicBlockIndex: -1 when absent or not numeric.</summary>
    public static int GetBlockIndex(Dictionary<string, JsonElement>? metadata)
    {
        if (metadata is null
            || !metadata.TryGetValue(KeyAnthropicBlockIndex, out var value))
        {
            return -1;
        }

        if (value.ValueKind != JsonValueKind.Number)
        {
            return -1;
        }

        if (value.TryGetInt64(out var l))
        {
            return (int)l;
        }

        return value.TryGetDouble(out var d) ? (int)d : -1;
    }

    /// <summary>Port of generateSignature: base64 of a random UUID string.</summary>
    public static string GenerateSignature()
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));

    /// <summary>Port of citationKey.</summary>
    public static string CitationKey(AnthropicTextCitation citation)
        => (citation.Type ?? string.Empty) + "\0" + (citation.Url ?? string.Empty) + "\0" + (citation.Title ?? string.Empty);

    /// <summary>Port of xurl.ParseDataURL.</summary>
    public static ParsedDataUrl? ParseDataUrl(string? url)
    {
        if (url is null || !url.StartsWith("data:", StringComparison.Ordinal))
        {
            return null;
        }

        var comma = url.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return null;
        }

        var header = url[..comma];
        var data = url[(comma + 1)..];

        var headerParts = header.Split(';');
        if (headerParts.Length == 0)
        {
            return null;
        }

        var mediaType = headerParts[0];
        if (mediaType.StartsWith("data:", StringComparison.Ordinal))
        {
            mediaType = mediaType["data:".Length..];
        }

        if (mediaType.Length == 0)
        {
            mediaType = "text/plain";
        }

        var isBase64 = false;
        for (var i = 1; i < headerParts.Length; i++)
        {
            if (headerParts[i].Trim() == "base64")
            {
                isBase64 = true;
                break;
            }
        }

        return new ParsedDataUrl(mediaType, data, isBase64);
    }

    /// <summary>Port of xurl.BuildDataURL.</summary>
    public static string BuildDataUrl(string mediaType, string data, bool isBase64)
    {
        if (mediaType.Length == 0)
        {
            mediaType = "text/plain";
        }

        return isBase64
            ? "data:" + mediaType + ";base64," + data
            : "data:" + mediaType + "," + data;
    }

    /// <summary>Port of xjson.SafeJSONRawMessage.</summary>
    public static byte[] SafeJsonRawMessage(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "{}"u8.ToArray();
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        if (IsValidJson(bytes))
        {
            return bytes;
        }

        var repaired = JsonRepair.Repair(value);
        return repaired is not null && IsValidJson(Encoding.UTF8.GetBytes(repaired))
            ? Encoding.UTF8.GetBytes(repaired)
            : "{}"u8.ToArray();
    }

    /// <summary>Builds a <see cref="JsonElement"/> from raw JSON bytes.</summary>
    public static JsonElement RawElement(ReadOnlySpan<byte> raw)
    {
        using var doc = JsonDocument.Parse(raw.ToArray());
        return doc.RootElement.Clone();
    }

    /// <summary>Parses bytes to a JsonElement, returning null for invalid JSON.</summary>
    public static JsonElement? TryRawElement(ReadOnlySpan<byte> raw)
    {
        try
        {
            return RawElement(raw);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Port of sanitizeReadToolInput.</summary>
    public static JsonElement SanitizeReadToolInput(string? name, JsonElement input)
    {
        if (name is null
            || !name.Equals("read", StringComparison.OrdinalIgnoreCase)
            || input.ValueKind != JsonValueKind.Object
            || !input.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.String
            || pages.GetString() != string.Empty)
        {
            return input;
        }

        var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in input.EnumerateObject())
        {
            // Go unmarshals into map[string]any (last duplicate key wins) and
            // deletes "pages" before re-marshalling.
            map[property.Name] = property.Value;
        }

        map.Remove("pages");

        var builder = new StringBuilder();
        AppendGoValue(builder, map);
        var element = TryRawElement(Encoding.UTF8.GetBytes(builder.ToString()));
        return element ?? input;
    }

    private static bool IsValidJson(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var _ = JsonDocument.Parse(bytes.ToArray());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Re-emits a JSON value the way Go marshals data that passed through
    /// <c>map[string]any</c>: object keys sorted, numbers formatted as float64.
    /// </summary>
    private static void AppendGoValue(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in value.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append(JsonSerializer.Serialize(property.Name, LlmJson.Options));
                    builder.Append(':');
                    AppendGoValue(builder, property.Value);
                }

                builder.Append('}');
                return;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    AppendGoValue(builder, item);
                }

                builder.Append(']');
                return;
            case JsonValueKind.Number:
                builder.Append(GoCast.GoFloatToString(value.GetDouble()));
                return;
            default:
                builder.Append(value.GetRawText());
                return;
        }
    }

    private static void AppendGoValue(StringBuilder builder, Dictionary<string, JsonElement> value)
    {
        builder.Append('{');
        var first = true;
        foreach (var property in value.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append(JsonSerializer.Serialize(property.Key, LlmJson.Options));
            builder.Append(':');
            AppendGoValue(builder, property.Value);
        }

        builder.Append('}');
    }
}

/// <summary>Port of xurl.DataURL.</summary>
internal sealed record ParsedDataUrl(string MediaType, string Data, bool IsBase64);

/// <summary>
/// Port of MessageContentBlock.MarshalJSON/UnmarshalJSON: the thinking variant
/// emits <c>thinking</c> and <c>signature</c> unconditionally and last.
/// </summary>
public sealed class AnthropicContentBlockJsonConverter : JsonConverter<AnthropicMessageContentBlock>
{
    /// <summary>A JSON null element decodes to the zero block in Go.</summary>
    public override bool HandleNull => true;

    public override AnthropicMessageContentBlock Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new AnthropicMessageContentBlock();
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("invalid content block");
        }

        var block = new AnthropicMessageContentBlock();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return block;
            }

            // Go matches the tag exactly first, then case-insensitively; the
            // manual reader mirrors the fold (all tags are lowercase ASCII).
            var property = reader.GetString()?.ToLowerInvariant();
            reader.Read();

            switch (property)
            {
                case "type":
                    block.Type = ReadString(ref reader);
                    break;
                case "text":
                    block.Text = ReadNullableString(ref reader);
                    break;
                case "citations":
                    block.Citations = JsonSerializer.Deserialize<List<AnthropicTextCitation>>(ref reader, options);
                    break;
                case "thinking":
                    block.Thinking = ReadNullableString(ref reader);
                    break;
                case "signature":
                    block.Signature = ReadNullableString(ref reader);
                    break;
                case "data":
                    block.Data = ReadString(ref reader);
                    break;
                case "source":
                    block.Source = reader.TokenType == JsonTokenType.Null
                        ? null
                        : JsonSerializer.Deserialize<AnthropicImageSource>(ref reader, options);
                    break;
                case "id":
                    block.Id = ReadString(ref reader);
                    break;
                case "name":
                    block.Name = ReadNullableString(ref reader);
                    break;
                case "input":
                    block.Input = ReadRaw(ref reader);
                    break;
                case "cache_control":
                    block.CacheControl = reader.TokenType == JsonTokenType.Null
                        ? null
                        : JsonSerializer.Deserialize<AnthropicCacheControl>(ref reader, options);
                    break;
                case "tool_use_id":
                    block.ToolUseId = ReadNullableString(ref reader);
                    break;
                case "content":
                    // *MessageContent in Go: a JSON null leaves the pointer nil.
                    block.Content = reader.TokenType == JsonTokenType.Null
                        ? null
                        : JsonSerializer.Deserialize<AnthropicMessageContent>(ref reader, options);
                    break;
                case "is_error":
                    block.IsError = JsonSerializer.Deserialize<bool?>(ref reader, options);
                    break;
                case "url":
                    block.Url = ReadString(ref reader);
                    break;
                case "title":
                    block.Title = ReadString(ref reader);
                    break;
                case "encrypted_content":
                    block.EncryptedContent = ReadNullableString(ref reader);
                    break;
                case "page_age":
                    block.PageAge = ReadNullableString(ref reader);
                    break;
                case "caller":
                    block.Caller = ReadRaw(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("invalid content block");
    }

    public override void Write(
        Utf8JsonWriter writer,
        AnthropicMessageContentBlock value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString("type", value.Type ?? string.Empty);

        var thinkingBlock = value.Type == "thinking";
        WriteAliasFields(writer, value, options, includeThinking: !thinkingBlock);

        if (thinkingBlock)
        {
            // model.go:412-424: the outer struct shadows the alias fields, and
            // lo.FromPtr forces an empty string instead of an omission.
            writer.WriteString("thinking", value.Thinking ?? string.Empty);
            writer.WriteString("signature", value.Signature ?? string.Empty);
        }

        writer.WriteEndObject();
    }

    private static void WriteAliasFields(
        Utf8JsonWriter writer,
        AnthropicMessageContentBlock value,
        JsonSerializerOptions options,
        bool includeThinking)
    {
        if (value.Text is not null)
        {
            writer.WriteString("text", value.Text);
        }

        if (value.Citations is { Count: > 0 })
        {
            writer.WritePropertyName("citations");
            JsonSerializer.Serialize(writer, value.Citations, options);
        }

        if (includeThinking && value.Thinking is not null)
        {
            writer.WriteString("thinking", value.Thinking);
        }

        if (includeThinking && value.Signature is not null)
        {
            writer.WriteString("signature", value.Signature);
        }

        if (!string.IsNullOrEmpty(value.Data))
        {
            writer.WriteString("data", value.Data);
        }

        if (value.Source is not null)
        {
            WriteSource(writer, value.Source);
        }

        if (!string.IsNullOrEmpty(value.Id))
        {
            writer.WriteString("id", value.Id);
        }

        if (value.Name is not null)
        {
            writer.WriteString("name", value.Name);
        }

        if (value.Input is { } input)
        {
            writer.WritePropertyName("input");
            input.WriteTo(writer);
        }

        if (value.CacheControl is not null)
        {
            writer.WritePropertyName("cache_control");
            WriteCacheControl(writer, value.CacheControl);
        }

        if (value.ToolUseId is not null)
        {
            writer.WriteString("tool_use_id", value.ToolUseId);
        }

        if (value.Content is not null)
        {
            writer.WritePropertyName("content");
            JsonSerializer.Serialize(writer, value.Content, options);
        }

        if (value.IsError is { } isError)
        {
            writer.WriteBoolean("is_error", isError);
        }

        if (!string.IsNullOrEmpty(value.Url))
        {
            writer.WriteString("url", value.Url);
        }

        if (!string.IsNullOrEmpty(value.Title))
        {
            writer.WriteString("title", value.Title);
        }

        if (value.EncryptedContent is not null)
        {
            writer.WriteString("encrypted_content", value.EncryptedContent);
        }

        if (value.PageAge is not null)
        {
            writer.WriteString("page_age", value.PageAge);
        }

        if (value.Caller is { } caller)
        {
            writer.WritePropertyName("caller");
            caller.WriteTo(writer);
        }
    }

    private static void WriteSource(Utf8JsonWriter writer, AnthropicImageSource source)
    {
        writer.WriteStartObject();
        writer.WriteString("type", source.Type ?? string.Empty);
        writer.WriteString("media_type", source.MediaType ?? string.Empty);
        writer.WriteString("data", source.Data ?? string.Empty);
        if (!string.IsNullOrEmpty(source.Url))
        {
            writer.WriteString("url", source.Url);
        }

        writer.WriteEndObject();
    }

    private static void WriteCacheControl(Utf8JsonWriter writer, AnthropicCacheControl cacheControl)
    {
        writer.WriteStartObject();
        writer.WriteString("type", cacheControl.Type ?? string.Empty);
        if (!string.IsNullOrEmpty(cacheControl.Ttl))
        {
            writer.WriteString("ttl", cacheControl.Ttl);
        }

        writer.WriteEndObject();
    }

    private static string? ReadString(ref Utf8JsonReader reader)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString()!,
            JsonTokenType.Null => null,
            _ => throw new JsonException("expected string"),
        };

    private static string? ReadNullableString(ref Utf8JsonReader reader) => ReadString(ref reader);

    private static JsonElement ReadRaw(ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone();
    }
}

/// <summary>Port of TextCitation's plain encoding/json shape.</summary>
public sealed class AnthropicTextCitationJsonConverter : JsonConverter<AnthropicTextCitation>
{
    /// <summary>A JSON null element decodes to the zero citation in Go.</summary>
    public override bool HandleNull => true;

    public override AnthropicTextCitation Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new AnthropicTextCitation();
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("invalid citation");
        }

        var citation = new AnthropicTextCitation();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                return citation;
            }

            // Go folds tag names case-insensitively; see the block converter.
            var property = reader.GetString()?.ToLowerInvariant();
            reader.Read();

            switch (property)
            {
                case "type":
                    citation.Type = ReadCitationString(ref reader);
                    break;
                case "url":
                    citation.Url = ReadCitationString(ref reader);
                    break;
                case "title":
                    citation.Title = ReadCitationString(ref reader);
                    break;
                case "encrypted_index":
                    citation.EncryptedIndex = ReadCitationString(ref reader);
                    break;
                case "cited_text":
                    citation.CitedText = ReadCitationString(ref reader);
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        throw new JsonException("invalid citation");
    }

    public override void Write(
        Utf8JsonWriter writer,
        AnthropicTextCitation value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        if (!string.IsNullOrEmpty(value.Type))
        {
            writer.WriteString("type", value.Type);
        }

        if (!string.IsNullOrEmpty(value.Url))
        {
            writer.WriteString("url", value.Url);
        }

        if (!string.IsNullOrEmpty(value.Title))
        {
            writer.WriteString("title", value.Title);
        }

        if (value.EncryptedIndex is not null)
        {
            writer.WriteString("encrypted_index", value.EncryptedIndex);
        }

        if (value.CitedText is not null)
        {
            writer.WriteString("cited_text", value.CitedText);
        }

        writer.WriteEndObject();
    }

    private static string? ReadCitationString(ref Utf8JsonReader reader)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString()!,
            JsonTokenType.Null => null,
            _ => throw new JsonException("expected string"),
        };
}
