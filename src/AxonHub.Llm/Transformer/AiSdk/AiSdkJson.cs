using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.AiSdk;

/// <summary>
/// JSON helpers for the AI SDK data stream transformer. Go decodes <c>any</c>
/// fields into <c>map[string]any / []any / string / float64 / bool / nil</c>
/// and re-encodes them with <c>encoding/json</c>; these helpers reproduce that
/// round-trip (numbers become float64 text, object keys sort bytewise, strings
/// are HTML-escaped, duplicate keys keep the last value).
/// </summary>
public static class AiSdkJson
{
    /// <summary>
    /// Go <c>json.Unmarshal(data, &amp;v)</c> into an <c>any</c>: syntax errors and
    /// float64 range errors are reported with Go's wording, and JSON
    /// <c>null</c> maps to Go's nil (<see cref="JsonValueKind.Undefined"/>).
    /// </summary>
    public static bool TryParseAny(
        ReadOnlySpan<byte> data,
        out JsonElement value,
        out string? error)
    {
        error = GoJsonSyntax.Validate(data);
        if (error is not null)
        {
            value = default;
            return false;
        }

        JsonElement parsed;
        try
        {
            var sanitized = GoJsonUtf8.SanitizeStringLiterals(data);
            using var document = JsonDocument.Parse(sanitized ?? data.ToArray());
            parsed = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            value = default;
            error = ex.Message;
            return false;
        }

        if (parsed.ValueKind == JsonValueKind.Null)
        {
            value = default;
            return true;
        }

        error = ValidateGoNumbers(parsed);
        if (error is not null)
        {
            value = default;
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// Go's decoder converts every JSON number in an <c>any</c> to float64;
    /// a literal outside float64's range fails the whole decode.
    /// </summary>
    public static string? ValidateGoNumbers(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                return TryParseGoFloat(element.GetRawText(), out _)
                    ? null
                    : "json: cannot unmarshal number " + element.GetRawText()
                        + " into Go value of type float64";
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var error = ValidateGoNumbers(item);
                    if (error is not null)
                    {
                        return error;
                    }
                }

                return null;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var error = ValidateGoNumbers(property.Value);
                    if (error is not null)
                    {
                        return error;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>Go <c>strconv.ParseFloat(s, 64)</c> range semantics.</summary>
    public static bool TryParseGoFloat(string literal, out double value)
    {
        value = 0;
        if (!double.TryParse(
                literal,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        if (double.IsInfinity(parsed) || double.IsNaN(parsed))
        {
            return false;
        }

        // An underflow to zero is ErrRange in Go; a literal whose mantissa is
        // zero (0, -0, 0e10) is not an error.
        if (parsed == 0 && HasNonZeroDigit(literal))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>Go <c>json.Marshal(v)</c> for a value decoded into <c>any</c>.</summary>
    public static byte[] MarshalAny(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
        {
            WriteAny(writer, value);
        }

        return GoJson.Escape(buffer.ToArray());
    }

    /// <summary>Parses a JSON fragment into a detached element.</summary>
    public static JsonElement ElementFromBytes(ReadOnlySpan<byte> data)
    {
        using var document = JsonDocument.Parse(data.ToArray());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// aisdk's <c>anyToRaw</c> helper: nil for Go nil, raw string bytes for a
    /// string, otherwise the Go-marshaled value.
    /// </summary>
    public static byte[]? AnyToRaw(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                return Encoding.UTF8.GetBytes(value.GetString() ?? string.Empty);
            default:
                return MarshalAny(value);
        }
    }

    /// <summary>
    /// aisdk's <c>rawToString</c> helper: invalid JSON is returned verbatim, a
    /// JSON string yields its value, anything else is Go-marshaled back to text.
    /// </summary>
    public static string RawToString(byte[]? raw)
    {
        if (raw is null || raw.Length == 0)
        {
            return string.Empty;
        }

        var original = Encoding.UTF8.GetString(raw);
        if (!TryParseAny(raw, out var value, out _))
        {
            return original;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        return Encoding.UTF8.GetString(MarshalAny(value));
    }

    /// <summary>Go string ordering is byte-wise over UTF-8 (encoding/json map key sort).</summary>
    public sealed class GoUtf8StringComparer : IComparer<string>
    {
        public static GoUtf8StringComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var left = Encoding.UTF8.GetBytes(x);
            var right = Encoding.UTF8.GetBytes(y);
            var length = Math.Min(left.Length, right.Length);
            for (var i = 0; i < length; i++)
            {
                var diff = left[i] - right[i];
                if (diff != 0)
                {
                    return diff;
                }
            }

            return left.Length - right.Length;
        }
    }

    private static bool HasNonZeroDigit(string literal)
    {
        foreach (var c in literal)
        {
            if (c is 'e' or 'E')
            {
                break;
            }

            if (c is >= '1' and <= '9')
            {
                return true;
            }
        }

        return false;
    }

    private static void WriteAny(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                writer.WriteNullValue();
                return;
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                return;
            case JsonValueKind.Number:
                if (!TryParseGoFloat(value.GetRawText(), out var number))
                {
                    throw new JsonException(
                        "json: cannot unmarshal number " + value.GetRawText()
                        + " into Go value of type float64");
                }

                writer.WriteRawValue(GoJsonFloat.Format(number), skipInputValidation: true);
                return;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                return;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                return;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteAny(writer, item);
                }

                writer.WriteEndArray();
                return;
            case JsonValueKind.Object:
                WriteGoObject(writer, value);
                return;
            default:
                writer.WriteNullValue();
                return;
        }
    }

    private static void WriteGoObject(Utf8JsonWriter writer, JsonElement value)
    {
        // Go decodes objects into map[string]any: duplicate keys keep the last
        // value, and marshal sorts the surviving keys by UTF-8 bytes.
        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            properties[property.Name] = property.Value;
        }

        var names = properties.Keys.ToList();
        names.Sort(GoUtf8StringComparer.Instance);

        writer.WriteStartObject();
        foreach (var name in names)
        {
            writer.WritePropertyName(name);
            WriteAny(writer, properties[name]);
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// Models a Go <c>any</c> field: decodes into a detached <see cref="JsonElement"/>
/// with Go's float64 range check, and writes the value back through
/// <see cref="AiSdkJson.MarshalAny"/>.
/// </summary>
public sealed class GoAnyJsonConverter : JsonConverter<JsonElement>
{
    public override JsonElement Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return default;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var value = document.RootElement.Clone();

        var error = AiSdkJson.ValidateGoNumbers(value);
        if (error is not null)
        {
            throw new JsonException(error);
        }

        return value;
    }

    public override void Write(
        Utf8JsonWriter writer,
        JsonElement value,
        JsonSerializerOptions options)
    {
        if (value.ValueKind == JsonValueKind.Undefined)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteRawValue(AiSdkJson.MarshalAny(value), skipInputValidation: true);
    }
}

/// <summary>
/// Models a Go <c>json.RawMessage</c>: explicit JSON null becomes the four-byte
/// value <c>null</c> (non-nil, so omitempty keeps it), an absent property stays
/// <see langword="null"/> (omitted), and writing compacts like
/// <c>encoding/json</c>.
/// </summary>
public sealed class RawJsonBytesConverter : JsonConverter<byte[]>
{
    private static readonly byte[] NullLiteral = "null"u8.ToArray();

    /// <summary>An explicit JSON null must reach <see cref="Read"/> to become <c>null</c> bytes.</summary>
    public override bool HandleNull => true;

    public override byte[] Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return NullLiteral;
        }

        using var document = JsonDocument.ParseValue(ref reader);
        return Encoding.UTF8.GetBytes(document.RootElement.GetRawText());
    }

    public override void Write(
        Utf8JsonWriter writer,
        byte[] value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteRawValue(GoJson.Compact(value), skipInputValidation: true);
    }
}
