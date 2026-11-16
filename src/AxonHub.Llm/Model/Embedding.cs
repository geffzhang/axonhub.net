using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Llm;

/// <summary>Port of the llm.EmbeddingInputType* constants (llm/embedding.go:8-13).</summary>
public static class EmbeddingInputTypes
{
    public const string String = "string";
    public const string StringArray = "string_array";
    public const string IntArray = "int_array";
    public const string IntArrayArray = "int_array_array";
}

/// <summary>
/// Port of llm.EmbeddingInput (llm/embedding.go:15-89): one of a string, a
/// string array, a token-id array, or an array of token-id arrays. The custom
/// codec mirrors Go's MarshalJSON/UnmarshalJSON exactly, including the order in
/// which the decoder tries the four shapes and the <c>null</c> output for an
/// empty value.
/// </summary>
[JsonConverter(typeof(EmbeddingInputJsonConverter))]
public sealed class EmbeddingInput
{
    public string String { get; set; } = string.Empty;

    /// <summary>Non-null (possibly empty) after JSON <c>[]</c>, null when absent.</summary>
    public List<string>? StringArray { get; set; }

    public List<long>? IntArray { get; set; }

    public List<List<long>>? IntArrayArray { get; set; }

    /// <summary>Port of EmbeddingInput.GetType; shadows object.GetType by design.</summary>
    public new string GetType()
    {
        if (String.Length > 0)
        {
            return EmbeddingInputTypes.String;
        }

        if (StringArray is { Count: > 0 })
        {
            return EmbeddingInputTypes.StringArray;
        }

        if (IntArray is { Count: > 0 })
        {
            return EmbeddingInputTypes.IntArray;
        }

        if (IntArrayArray is { Count: > 0 })
        {
            return EmbeddingInputTypes.IntArrayArray;
        }

        return string.Empty;
    }
}

/// <summary>
/// Port of EmbeddingInput.MarshalJSON/UnmarshalJSON. Go tries the four shapes in
/// order: string, []string, []int64, [][]int64; the first successful decode
/// wins, and a JSON <c>null</c> succeeds for every candidate (the zero value is
/// left in place). Anything else reports <c>invalid embedding input type</c>.
/// </summary>
public sealed class EmbeddingInputJsonConverter : JsonConverter<EmbeddingInput>
{
    public override bool HandleNull => true;

    public override EmbeddingInput Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var input = new EmbeddingInput();

        if (TryReadString(root, out var single))
        {
            input.String = single;
            return input;
        }

        if (TryReadStringArray(root, out var strings))
        {
            input.StringArray = strings;
            return input;
        }

        if (TryReadIntArray(root, out var ids))
        {
            input.IntArray = ids;
            return input;
        }

        if (TryReadIntArrayArray(root, out var batches))
        {
            input.IntArrayArray = batches;
            return input;
        }

        throw new JsonException("invalid embedding input type");
    }

    public override void Write(
        Utf8JsonWriter writer,
        EmbeddingInput value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.String.Length > 0)
        {
            writer.WriteStringValue(value.String);
            return;
        }

        if (value.StringArray is { Count: > 0 } strings)
        {
            writer.WriteStartArray();
            foreach (var item in strings)
            {
                writer.WriteStringValue(item);
            }

            writer.WriteEndArray();
            return;
        }

        if (value.IntArray is { Count: > 0 } ids)
        {
            writer.WriteStartArray();
            foreach (var item in ids)
            {
                writer.WriteNumberValue(item);
            }

            writer.WriteEndArray();
            return;
        }

        if (value.IntArrayArray is { Count: > 0 } batches)
        {
            writer.WriteStartArray();
            foreach (var batch in batches)
            {
                writer.WriteStartArray();
                foreach (var item in batch)
                {
                    writer.WriteNumberValue(item);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            return;
        }

        writer.WriteNullValue();
    }

    /// <summary>Decoding into <c>string</c>: a JSON string assigns, null is a no-op.</summary>
    private static bool TryReadString(JsonElement root, out string value)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Null:
                value = string.Empty;
                return true;
            case JsonValueKind.String:
                value = root.GetString() ?? string.Empty;
                return true;
            default:
                value = string.Empty;
                return false;
        }
    }

    private static bool TryReadStringArray(JsonElement root, out List<string>? value)
    {
        value = null;
        if (root.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

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
                    return false;
            }
        }

        value = items;
        return true;
    }

    private static bool TryReadIntArray(JsonElement root, out List<long>? value)
    {
        value = null;
        if (root.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var items = new List<long>();
        foreach (var element in root.EnumerateArray())
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Number when element.TryGetInt64(out var number):
                    items.Add(number);
                    break;
                case JsonValueKind.Null:
                    // Go decodes null into an int64 element as the zero value.
                    items.Add(0);
                    break;
                default:
                    return false;
            }
        }

        value = items;
        return true;
    }

    private static bool TryReadIntArrayArray(JsonElement root, out List<List<long>>? value)
    {
        value = null;
        if (root.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var items = new List<List<long>>();
        foreach (var element in root.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Null)
            {
                // Go decodes null into an inner slice as nil (length zero).
                items.Add([]);
                continue;
            }

            if (element.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var inner = new List<long>();
            foreach (var item in element.EnumerateArray())
            {
                switch (item.ValueKind)
                {
                    case JsonValueKind.Number when item.TryGetInt64(out var number):
                        inner.Add(number);
                        break;
                    case JsonValueKind.Null:
                        inner.Add(0);
                        break;
                    default:
                        return false;
                }
            }

            items.Add(inner);
        }

        value = items;
        return true;
    }
}

/// <summary>
/// Port of llm.EmbeddingRequest (llm/embedding.go:94-123). Common fields such as
/// Model live on the parent <see cref="LlmRequest"/>, not here.
/// </summary>
public sealed class EmbeddingRequest
{
    [JsonPropertyName("input")]
    public EmbeddingInput Input { get; set; } = new();

    [JsonPropertyName("task")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Task { get; set; }

    [JsonPropertyName("encoding_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EncodingFormat { get; set; }

    [JsonPropertyName("dimensions")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Dimensions { get; set; }

    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; set; }
}

/// <summary>Port of llm.EmbeddingResponse (llm/embedding.go:126-137).</summary>
public sealed class EmbeddingResponse
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    [JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    /// <summary>Go's nil slice marshals as <c>null</c>; a fresh transform always sets [].</summary>
    [JsonPropertyName("data")]
    public List<EmbeddingData>? Data { get; set; }
}

/// <summary>Port of llm.EmbeddingData (llm/embedding.go:140-149).</summary>
public sealed class EmbeddingData
{
    [JsonPropertyName("object")]
    public string Object { get; set; } = string.Empty;

    [JsonPropertyName("embedding")]
    public Embedding Embedding { get; set; } = new();

    [JsonPropertyName("index")]
    public long Index { get; set; }
}

/// <summary>
/// Port of llm.Embedding (llm/embedding.go:151-183): the vector as a float list
/// or a base64 string. Go's field name is <c>Embedding</c>; C# forbids a member
/// with the enclosing type's name, so the vector is <see cref="Values"/>.
/// </summary>
[JsonConverter(typeof(EmbeddingJsonConverter))]
public sealed class Embedding
{
    public List<double>? Values { get; set; }

    public string Base64 { get; set; } = string.Empty;
}

/// <summary>
/// Port of Embedding.MarshalJSON/UnmarshalJSON: a non-empty float array wins,
/// then a non-empty base64 string, else <c>null</c>. Decoding tries string first
/// (a JSON <c>null</c> decodes to an empty base64 string), then float array,
/// else reports <c>invalid embedding type</c>.
/// </summary>
public sealed class EmbeddingJsonConverter : JsonConverter<Embedding>
{
    public override bool HandleNull => true;

    public override Embedding Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        switch (root.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.String:
                return new Embedding { Base64 = root.ValueKind == JsonValueKind.String ? root.GetString() ?? string.Empty : string.Empty };
            case JsonValueKind.Array:
                var values = new List<double>();
                foreach (var element in root.EnumerateArray())
                {
                    switch (element.ValueKind)
                    {
                        case JsonValueKind.Number when element.TryGetDouble(out var number):
                            values.Add(number);
                            break;
                        case JsonValueKind.Null:
                            // Go decodes null into a float64 element as the zero value.
                            values.Add(0);
                            break;
                        default:
                            throw new JsonException("invalid embedding type");
                    }
                }

                return new Embedding { Values = values };
            default:
                throw new JsonException("invalid embedding type");
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        Embedding value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        if (value.Values is { Count: > 0 } values)
        {
            writer.WriteStartArray();
            foreach (var item in values)
            {
                writer.WriteRawValue(GoJsonFloat.Format(item), skipInputValidation: true);
            }

            writer.WriteEndArray();
            return;
        }

        if (value.Base64.Length > 0)
        {
            writer.WriteStringValue(value.Base64);
            return;
        }

        writer.WriteNullValue();
    }
}
