using System.Globalization;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer;

namespace AxonHub.Llm.Transformer.OpenAI.Decisions;

/// <summary>
/// The anonymous request struct from inbound.go:39-44 decoded with Go's
/// <c>json.Decoder</c> semantics (case-insensitive struct fields, last duplicate
/// key wins, <c>null</c> leaves the previous value in place).
/// </summary>
internal sealed class DecisionsWireRequest
{
    public string Model { get; set; } = string.Empty;

    public JsonElement? Input { get; set; }

    public List<JsonElement>? Questions { get; set; }

    public bool? Stream { get; set; }
}

/// <summary>
/// The anonymous response struct from outbound.go:120-126 decoded with Go's
/// <c>json.Unmarshal</c> semantics.
/// </summary>
internal sealed class DecisionsWireResponse
{
    public string Model { get; set; } = string.Empty;

    public List<JsonElement>? Answers { get; set; }

    /// <summary>Non-null when the provider sent a <c>usage</c> object.</summary>
    public DecisionsUsage? Usage { get; set; }
}

/// <summary>
/// Carries Go's decoder error text; callers add the operation prefix the
/// reference puts around <c>json.Decoder.Decode</c> / <c>json.Unmarshal</c>.
/// </summary>
internal sealed class DecisionsWireException(string message) : Exception(message);

/// <summary>
/// Port of the wire decode in llm/transformer/openai/decisions: Go's
/// <c>encoding/json</c> field matching and type-error wording, backed by
/// System.Text.Json only after Go's scanner has validated the bytes.
/// </summary>
internal static class DecisionsWire
{
    /// <summary>Go's <c>reflect.Type.String()</c> for the request wire struct.</summary>
    internal const string RequestStructType =
        "struct { Model string \"json:\\\"model\\\"\"; Input json.RawMessage \"json:\\\"input\\\"\"; "
        + "Questions []json.RawMessage \"json:\\\"questions\\\"\"; Stream *bool \"json:\\\"stream\\\"\" }";

    /// <summary>Go's <c>reflect.Type.String()</c> for the response wire struct.</summary>
    internal const string ResponseStructType =
        "struct { Model string \"json:\\\"model\\\"\"; Answers []json.RawMessage \"json:\\\"answers\\\"\"; "
        + "Usage *struct { InputTokens int64 \"json:\\\"input_tokens\\\"\" } \"json:\\\"usage\\\"\" }";

    /// <summary>Go's <c>reflect.Type.String()</c> for the nested usage struct.</summary>
    internal const string UsageStructType =
        "struct { InputTokens int64 \"json:\\\"input_tokens\\\"\" }";

    private static readonly JsonDocumentOptions ParseOptions = new() { MaxDepth = 10_000 };

    /// <summary>Decodes the four-field decisions request wire struct.</summary>
    public static DecisionsWireRequest DecodeRequest(byte[] body, int length)
    {
        using var document = Parse(body, length);
        var root = document.RootElement;

        switch (root.ValueKind)
        {
            case JsonValueKind.Null:
                return new DecisionsWireRequest();
            case JsonValueKind.Object:
                break;
            default:
                throw new DecisionsWireException(
                    "json: cannot unmarshal " + KindWord(root.ValueKind)
                    + " into Go value of type " + RequestStructType);
        }

        var wire = new DecisionsWireRequest();
        foreach (var property in root.EnumerateObject())
        {
            if (NameIs(property.Name, "model"))
            {
                wire.Model = ReadString(property.Value, wire.Model, ".model");
            }
            else if (NameIs(property.Name, "input"))
            {
                // json.RawMessage accepts every value verbatim; null becomes the
                // literal bytes "null", which the validator rejects.
                wire.Input = property.Value.Clone();
            }
            else if (NameIs(property.Name, "questions"))
            {
                wire.Questions = ReadRawArray(property.Value, "questions");
            }
            else if (NameIs(property.Name, "stream"))
            {
                wire.Stream = ReadBoolPointer(property.Value);
            }
        }

        return wire;
    }

    /// <summary>Decodes the response wire struct after <c>json.Valid</c> passed.</summary>
    public static DecisionsWireResponse DecodeResponse(byte[] body)
    {
        using var document = Parse(body, body.Length);
        var root = document.RootElement;

        switch (root.ValueKind)
        {
            case JsonValueKind.Null:
                return new DecisionsWireResponse();
            case JsonValueKind.Object:
                break;
            default:
                throw new DecisionsWireException(
                    "json: cannot unmarshal " + KindWord(root.ValueKind)
                    + " into Go value of type " + ResponseStructType);
        }

        var wire = new DecisionsWireResponse();
        foreach (var property in root.EnumerateObject())
        {
            if (NameIs(property.Name, "model"))
            {
                wire.Model = ReadString(property.Value, wire.Model, ".model");
            }
            else if (NameIs(property.Name, "answers"))
            {
                wire.Answers = ReadRawArray(property.Value, "answers");
            }
            else if (NameIs(property.Name, "usage"))
            {
                wire.Usage = ReadUsage(property.Value, wire.Usage);
            }
        }

        return wire;
    }

    /// <summary>Go's struct field lookup folds ASCII letters (encoding/json).</summary>
    private static bool NameIs(string name, string field) => string.Equals(name, field, StringComparison.OrdinalIgnoreCase);

    private static JsonDocument Parse(byte[] body, int length)
    {
        // Go repairs ill-formed UTF-8 inside string literals while unquoting;
        // see GoJsonUtf8 (difference C008).
        var sanitized = GoJsonUtf8.SanitizeStringLiterals(body.AsSpan(0, length));
        try
        {
            return sanitized is null
                ? JsonDocument.Parse(body.AsMemory(0, length), ParseOptions)
                : JsonDocument.Parse(sanitized, ParseOptions);
        }
        catch (JsonException ex)
        {
            // Defensive: the Go scanner accepted these bytes, so this is a
            // System.Text.Json-only rejection (depth/sanitizer residue).
            throw new DecisionsWireException(TrimPath(ex.Message));
        }
    }

    /// <summary>Strips STJ's position suffix so the text stays Go-shaped.</summary>
    private static string TrimPath(string message)
    {
        var pathIndex = message.IndexOf(" Path: ", StringComparison.Ordinal);
        return pathIndex > 0 ? message[..pathIndex] : message;
    }

    /// <summary>Decoding into <c>string</c>: null is a no-op, a string assigns.</summary>
    private static string ReadString(JsonElement value, string current, string field)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Null => current,
            JsonValueKind.String => value.GetString() ?? string.Empty,
            _ => throw new DecisionsWireException(
                "json: cannot unmarshal " + KindWord(value.ValueKind)
                + " into Go struct field " + field + " of type string"),
        };
    }

    /// <summary>Decoding into <c>[]json.RawMessage</c>.</summary>
    private static List<JsonElement>? ReadRawArray(JsonElement value, string field)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Array:
                return [.. value.EnumerateArray().Select(static element => element.Clone())];
            default:
                throw new DecisionsWireException(
                    "json: cannot unmarshal " + KindWord(value.ValueKind)
                    + " into Go struct field ." + field + " of type []json.RawMessage");
        }
    }

    /// <summary>Decoding into <c>*bool</c>.</summary>
    private static bool? ReadBoolPointer(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => throw new DecisionsWireException(
                "json: cannot unmarshal " + KindWord(value.ValueKind)
                + " into Go struct field .stream of type bool"),
        };
    }

    /// <summary>Decoding into <c>*struct{InputTokens int64}</c>.</summary>
    private static DecisionsUsage? ReadUsage(JsonElement value, DecisionsUsage? current)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Object:
                break;
            default:
                throw new DecisionsWireException(
                    "json: cannot unmarshal " + KindWord(value.ValueKind)
                    + " into Go struct field .usage of type " + UsageStructType);
        }

        // Duplicate "usage" keys decode into the same allocated struct, so a
        // later object only overwrites the fields it carries.
        current ??= new DecisionsUsage();
        foreach (var property in value.EnumerateObject())
        {
            if (NameIs(property.Name, "input_tokens"))
            {
                current.InputTokens = ReadInt64(property.Value, current.InputTokens);
            }
        }

        return current;
    }

    /// <summary>Decoding into <c>int64</c>: only an integer literal fits.</summary>
    private static long ReadInt64(JsonElement value, long current)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return current;
            case JsonValueKind.Number:
                var raw = value.GetRawText();
                if (long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                throw new DecisionsWireException(
                    "json: cannot unmarshal number " + raw
                    + " into Go struct field .usage.input_tokens of type int64");
            default:
                throw new DecisionsWireException(
                    "json: cannot unmarshal " + KindWord(value.ValueKind)
                    + " into Go struct field .usage.input_tokens of type int64");
        }
    }

    internal static string KindWord(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Null => "null",
        _ => "unknown",
    };
}

/// <summary>
/// Port of validateDecisionsInput / validateDecisionsInputItem /
/// validateDecisionsMessageContent (inbound.go:83-171). Each method returns the
/// Go error detail or <see langword="null"/> when the value is accepted.
/// </summary>
internal static class DecisionsValidation
{
    public static string? ValidateInput(JsonElement? input)
    {
        if (input is null)
        {
            return "input is required";
        }

        var value = input.Value;
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                return "input is required";
            case JsonValueKind.String:
                return (value.GetString() ?? string.Empty).Length == 0
                    ? "input string cannot be empty"
                    : null;
            case JsonValueKind.Array:
                var items = value.EnumerateArray().ToList();
                if (items.Count == 0)
                {
                    return "input array cannot be empty";
                }

                for (var i = 0; i < items.Count; i++)
                {
                    var error = ValidateInputItem(items[i]);
                    if (error is not null)
                    {
                        return "input[" + i.ToString(CultureInfo.InvariantCulture) + "]: " + error;
                    }
                }

                return null;
            default:
                return "input must be a string or supported message structure";
        }
    }

    private static string? ValidateInputItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            // json.Unmarshal into map[string]json.RawMessage fails (or leaves the
            // map nil for a JSON null); the reference discards the reason.
            return "item must be an object";
        }

        // The item is a Go map: keys match exactly (case-sensitive) and a later
        // duplicate key overwrites the earlier one.
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in item.EnumerateObject())
        {
            fields[property.Name] = property.Value;
        }

        if (!fields.TryGetValue("type", out var typeValue))
        {
            return "type is required";
        }

        string itemType;
        switch (typeValue.ValueKind)
        {
            case JsonValueKind.String:
                itemType = typeValue.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Null:
                // json.Unmarshal(null, &string) leaves the empty string; the
                // switch then reports it as unsupported.
                itemType = string.Empty;
                break;
            default:
                return "type is required";
        }

        return itemType switch
        {
            "message" => ValidateMessageContent(
                fields.TryGetValue("content", out var content) ? content : null),
            "input_text" => ValidateNonEmptyString(
                fields.TryGetValue("text", out var text) ? text : null,
                "text"),
            "input_image" => ValidateNonEmptyString(
                fields.TryGetValue("image_url", out var imageUrl) ? imageUrl : null,
                "image_url"),
            _ => "unsupported item type " + GoQuote(itemType),
        };
    }

    private static string? ValidateMessageContent(JsonElement? content)
    {
        if (content is null || content.Value.ValueKind == JsonValueKind.Null)
        {
            return "message content is required";
        }

        var value = content.Value;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return ValidateNonEmptyString(value, "content");
            case JsonValueKind.Array:
                var parts = value.EnumerateArray().ToList();
                if (parts.Count == 0)
                {
                    return "message content array cannot be empty";
                }

                for (var i = 0; i < parts.Count; i++)
                {
                    var error = ValidateInputItem(parts[i]);
                    if (error is not null)
                    {
                        return "content[" + i.ToString(CultureInfo.InvariantCulture) + "]: " + error;
                    }
                }

                return null;
            default:
                return "message content must be a string or content array";
        }
    }

    private static string? ValidateNonEmptyString(JsonElement? value, string field)
    {
        if (value is { ValueKind: JsonValueKind.String }
            && (value.Value.GetString() ?? string.Empty).Length > 0)
        {
            return null;
        }

        return field + " must be a non-empty string";
    }

    /// <summary>Go <c>strconv.Quote</c> for the <c>%q</c> in the unsupported-type error.</summary>
    private static string GoQuote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\a':
                    builder.Append("\\a");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\v':
                    builder.Append("\\v");
                    break;
                default:
                    if (IsGoPrint(rune))
                    {
                        builder.Append(rune.ToString());
                    }
                    else if (rune.Value is < 0x20 or 0x7F)
                    {
                        builder.Append("\\x").Append(
                            rune.Value.ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else if (rune.Value < 0x10000)
                    {
                        builder.Append("\\u").Append(
                            rune.Value.ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append("\\U").Append(
                            rune.Value.ToString("x8", CultureInfo.InvariantCulture));
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    /// <summary>Port of Go's <c>unicode.IsPrint</c> categories.</summary>
    private static bool IsGoPrint(Rune rune)
    {
        if (rune.Value == ' ')
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.UppercaseLetter or
            UnicodeCategory.LowercaseLetter or
            UnicodeCategory.TitlecaseLetter or
            UnicodeCategory.ModifierLetter or
            UnicodeCategory.OtherLetter or
            UnicodeCategory.NonSpacingMark or
            UnicodeCategory.SpacingCombiningMark or
            UnicodeCategory.EnclosingMark or
            UnicodeCategory.DecimalDigitNumber or
            UnicodeCategory.LetterNumber or
            UnicodeCategory.OtherNumber or
            UnicodeCategory.ConnectorPunctuation or
            UnicodeCategory.DashPunctuation or
            UnicodeCategory.OpenPunctuation or
            UnicodeCategory.ClosePunctuation or
            UnicodeCategory.InitialQuotePunctuation or
            UnicodeCategory.FinalQuotePunctuation or
            UnicodeCategory.OtherPunctuation or
            UnicodeCategory.MathSymbol or
            UnicodeCategory.CurrencySymbol or
            UnicodeCategory.ModifierSymbol or
            UnicodeCategory.OtherSymbol;
    }
}
