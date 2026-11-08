using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Llm;

/// <summary>
/// Port of llm.ModerationInput (llm/moderation.go:8-66): the /v1/moderations
/// <c>input</c> field, a string, a string array, or an array of multimodal
/// parts. The codec reproduces Go's MarshalJSON/UnmarshalJSON, including the
/// decode precedence string → []string → parts and the <c>null</c> output for
/// an empty value.
/// </summary>
[JsonConverter(typeof(ModerationInputJsonConverter))]
public sealed class ModerationInput
{
    public string String { get; set; } = string.Empty;

    /// <summary>Non-null (possibly empty) after JSON <c>[]</c>, null when absent.</summary>
    public List<string>? StringArray { get; set; }

    public List<ModerationMultiModalPart>? Parts { get; set; }
}

/// <summary>Port of llm.ModerationMultiModalPart (llm/moderation.go:14-18).</summary>
public sealed class ModerationMultiModalPart
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>Go <c>string</c> + <c>omitempty</c>; empty decodes to null (C001).</summary>
    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; set; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ModerationImageUrlObject? ImageURL { get; set; }
}

/// <summary>Port of llm.ModerationImageURLObject (llm/moderation.go:21-23).</summary>
public sealed class ModerationImageUrlObject
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;
}

/// <summary>Port of ModerationInput.MarshalJSON/UnmarshalJSON.</summary>
public sealed class ModerationInputJsonConverter : JsonConverter<ModerationInput>
{
    public override bool HandleNull => true;

    public override ModerationInput Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        // Go always starts from the zero value.
        var input = new ModerationInput();

        if (root.ValueKind == JsonValueKind.Null)
        {
            // json.Unmarshal(null, &str) succeeds and leaves the zero string.
            return input;
        }

        if (root.ValueKind == JsonValueKind.String)
        {
            input.String = root.GetString() ?? string.Empty;
            return input;
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("invalid moderation input type");
        }

        // Try []string first so objects are never mistaken for strings.
        if (TryReadStringArray(root, out var strings))
        {
            input.StringArray = strings;
            return input;
        }

        if (TryReadParts(root, out var parts))
        {
            input.Parts = parts;
            return input;
        }

        throw new JsonException("invalid moderation input type");
    }

    public override void Write(
        Utf8JsonWriter writer,
        ModerationInput value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.Parts is { Count: > 0 } parts)
        {
            JsonSerializer.Serialize(writer, parts, options);
            return;
        }

        if (value.StringArray is { Count: > 0 } strings)
        {
            JsonSerializer.Serialize(writer, strings, options);
            return;
        }

        if (value.String.Length > 0)
        {
            writer.WriteStringValue(value.String);
            return;
        }

        writer.WriteNullValue();
    }

    private static bool TryReadStringArray(JsonElement root, out List<string>? value)
    {
        var items = new List<string>();
        foreach (var element in root.EnumerateArray())
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    items.Add(element.GetString() ?? string.Empty);
                    break;
                case JsonValueKind.Null:
                    // Go decodes null into a string element as the zero value.
                    items.Add(string.Empty);
                    break;
                default:
                    value = null;
                    return false;
            }
        }

        value = items;
        return true;
    }

    private static bool TryReadParts(JsonElement root, out List<ModerationMultiModalPart>? value)
    {
        var items = new List<ModerationMultiModalPart>();
        foreach (var element in root.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Null)
            {
                // Go decodes null into a struct element as the zero value.
                items.Add(new ModerationMultiModalPart());
                continue;
            }

            if (element.ValueKind != JsonValueKind.Object
                || !TryReadPart(element, out var part))
            {
                value = null;
                return false;
            }

            items.Add(part!);
        }

        value = items;
        return true;
    }

    private static bool TryReadPart(JsonElement element, out ModerationMultiModalPart? part)
    {
        var result = new ModerationMultiModalPart();

        foreach (var property in element.EnumerateObject())
        {
            switch (FoldField(property.Name))
            {
                case "type":
                    if (!TryReadString(property.Value, out var type))
                    {
                        part = null;
                        return false;
                    }

                    result.Type = type;
                    break;
                case "text":
                    if (!TryReadString(property.Value, out var text))
                    {
                        part = null;
                        return false;
                    }

                    // Go's omitempty drops an empty text on marshal.
                    result.Text = text.Length == 0 ? null : text;
                    break;
                case "image_url":
                    if (property.Value.ValueKind == JsonValueKind.Null)
                    {
                        result.ImageURL = null;
                        break;
                    }

                    if (property.Value.ValueKind != JsonValueKind.Object
                        || !TryReadImageUrl(property.Value, out var imageUrl))
                    {
                        part = null;
                        return false;
                    }

                    result.ImageURL = imageUrl;
                    break;
            }
        }

        part = result;
        return true;
    }

    private static bool TryReadImageUrl(JsonElement element, out ModerationImageUrlObject? imageUrl)
    {
        var result = new ModerationImageUrlObject();

        foreach (var property in element.EnumerateObject())
        {
            if (FoldField(property.Name) != "url")
            {
                continue;
            }

            if (!TryReadString(property.Value, out var url))
            {
                imageUrl = null;
                return false;
            }

            result.Url = url;
        }

        imageUrl = result;
        return true;
    }

    /// <summary>Decoding into <c>string</c>: a JSON string assigns, null is a no-op.</summary>
    private static bool TryReadString(JsonElement element, out string value)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                value = string.Empty;
                return true;
            case JsonValueKind.String:
                value = element.GetString() ?? string.Empty;
                return true;
            default:
                value = string.Empty;
                return false;
        }
    }

    private static string FoldField(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'A' and <= 'Z')
            {
                chars[i] = (char)(chars[i] + ('a' - 'A'));
            }
        }

        return new string(chars);
    }
}

/// <summary>
/// Port of llm.ModerationRequest (llm/moderation.go:69-71). Model lives on the
/// parent <see cref="LlmRequest"/>.
/// </summary>
public sealed class ModerationRequest
{
    [JsonPropertyName("input")]
    public ModerationInput Input { get; set; } = new();
}

/// <summary>Port of llm.ModerationClassification (llm/moderation.go:74-79).</summary>
[JsonConverter(typeof(ModerationClassificationJsonConverter))]
public sealed class ModerationClassification
{
    public bool Flagged { get; set; }

    /// <summary>Go <c>map[string]bool</c>: nil marshals as <c>null</c>, empty as <c>{}</c>.</summary>
    public Dictionary<string, bool>? Categories { get; set; }

    /// <summary>Go <c>map[string]float64</c>: nil marshals as <c>null</c>, empty as <c>{}</c>.</summary>
    public Dictionary<string, double>? CategoryScores { get; set; }

    /// <summary>Go <c>map[string][]string</c> + <c>omitempty</c>: nil and empty are omitted.</summary>
    public Dictionary<string, List<string>?>? CategoryAppliedInputTypes { get; set; }
}

/// <summary>
/// Port of llm.ModerationClassification marshaling: struct field order, sorted
/// map keys (Go's encoding/json), nil maps as <c>null</c>, and the
/// <c>omitempty</c> on <c>category_applied_input_types</c>.
/// </summary>
public sealed class ModerationClassificationJsonConverter : JsonConverter<ModerationClassification>
{
    public override bool HandleNull => true;

    public override ModerationClassification Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        // Go decodes null into a struct value as a no-op.
        if (root.ValueKind == JsonValueKind.Null)
        {
            return new ModerationClassification();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("invalid moderation classification");
        }

        var result = new ModerationClassification();

        foreach (var property in root.EnumerateObject())
        {
            switch (FoldField(property.Name))
            {
                case "flagged":
                    result.Flagged = ReadBool(property.Value, result.Flagged);
                    break;
                case "categories":
                    result.Categories = ReadBoolMap(property.Value);
                    break;
                case "category_scores":
                    result.CategoryScores = ReadDoubleMap(property.Value);
                    break;
                case "category_applied_input_types":
                    result.CategoryAppliedInputTypes = ReadStringArrayMap(property.Value);
                    break;
            }
        }

        return result;
    }

    public override void Write(
        Utf8JsonWriter writer,
        ModerationClassification value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();

        writer.WriteBoolean("flagged", value.Flagged);
        WriteBoolMap(writer, "categories", value.Categories);
        WriteDoubleMap(writer, "category_scores", value.CategoryScores);

        // Go's omitempty: nil and empty maps are both omitted.
        if (value.CategoryAppliedInputTypes is { Count: > 0 } applied)
        {
            WriteStringArrayMap(writer, "category_applied_input_types", applied);
        }

        writer.WriteEndObject();
    }

    private static bool ReadBool(JsonElement element, bool current)
        => element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => current,
            _ => throw new JsonException("invalid moderation classification"),
        };

    private static Dictionary<string, bool>? ReadBoolMap(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("invalid moderation classification");
        }

        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = ReadBool(property.Value, false);
        }

        return result;
    }

    private static Dictionary<string, double>? ReadDoubleMap(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("invalid moderation classification");
        }

        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Null:
                    // Go decodes null into a float64 map value as the zero value.
                    result[property.Name] = 0;
                    break;
                case JsonValueKind.Number when property.Value.TryGetDouble(out var number):
                    result[property.Name] = number;
                    break;
                default:
                    throw new JsonException("invalid moderation classification");
            }
        }

        return result;
    }

    private static Dictionary<string, List<string>?>? ReadStringArrayMap(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("invalid moderation classification");
        }

        var result = new Dictionary<string, List<string>?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                result[property.Name] = null;
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("invalid moderation classification");
            }

            var items = new List<string>();
            foreach (var item in property.Value.EnumerateArray())
            {
                switch (item.ValueKind)
                {
                    case JsonValueKind.String:
                        items.Add(item.GetString() ?? string.Empty);
                        break;
                    case JsonValueKind.Null:
                        items.Add(string.Empty);
                        break;
                    default:
                        throw new JsonException("invalid moderation classification");
                }
            }

            result[property.Name] = items;
        }

        return result;
    }

    private static void WriteBoolMap(
        Utf8JsonWriter writer,
        string name,
        Dictionary<string, bool>? map)
    {
        writer.WritePropertyName(name);
        if (map is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        foreach (var key in map.Keys.OrderBy(static key => key, StringComparer.Ordinal))
        {
            writer.WriteBoolean(key, map[key]);
        }

        writer.WriteEndObject();
    }

    private static void WriteDoubleMap(
        Utf8JsonWriter writer,
        string name,
        Dictionary<string, double>? map)
    {
        writer.WritePropertyName(name);
        if (map is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        foreach (var key in map.Keys.OrderBy(static key => key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(key);
            writer.WriteRawValue(GoJsonFloat.Format(map[key]), skipInputValidation: true);
        }

        writer.WriteEndObject();
    }

    private static void WriteStringArrayMap(
        Utf8JsonWriter writer,
        string name,
        Dictionary<string, List<string>?> map)
    {
        writer.WritePropertyName(name);
        writer.WriteStartObject();
        foreach (var key in map.Keys.OrderBy(static key => key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(key);

            if (map[key] is not { } values)
            {
                writer.WriteNullValue();
                continue;
            }

            writer.WriteStartArray();
            foreach (var value in values)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static string FoldField(string name)
    {
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'A' and <= 'Z')
            {
                chars[i] = (char)(chars[i] + ('a' - 'A'));
            }
        }

        return new string(chars);
    }
}

/// <summary>Port of llm.ModerationResponse (llm/moderation.go:82-84).</summary>
public sealed class ModerationResponse
{
    /// <summary>Go's nil slice marshals as <c>null</c>.</summary>
    [JsonPropertyName("results")]
    public List<ModerationClassification>? Results { get; set; }
}
