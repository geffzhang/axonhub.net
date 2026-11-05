using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Prompts;

/// <summary>
/// The gjson/sjson slice of orchestrator.injectDecisionsPrompts: the
/// <c>input.<i>.content.<j>.<key></c> lookups and the byte-preserving text
/// replacement the reference performs on the raw decisions body.
/// </summary>
/// <remarks>
/// The reference passes paths to gjson, which walks the original bytes;
/// <c>Result.String()</c> decodes JSON strings and returns the raw source text
/// for every other kind (a JSON null is the empty string). The helpers below
/// keep the same offsets so a later sjson-style splice only replaces the
/// value span.
/// </remarks>
internal static class DecisionsRawJson
{
    // Go's encoding/json accepts nesting up to 10000 levels; the reader's
    // default of 64 would reject documents Go happily skips.
    private const int MaxDepth = 10_000;

    /// <summary>A gjson result for one value: decoded text plus its byte span.</summary>
    public readonly struct Value
    {
        public static readonly Value Missing;

        public bool Exists { get; init; }

        public JsonTokenType Kind { get; init; }

        /// <summary>gjson <c>Result.String()</c>.</summary>
        public string Text { get; init; }

        /// <summary>Byte offset of the value start.</summary>
        public int Start { get; init; }

        /// <summary>Byte offset just past the value.</summary>
        public int End { get; init; }
    }

    /// <summary>gjson <c>GetBytes(body, "input")</c>.</summary>
    public static Value GetTopLevel(ReadOnlySpan<byte> body, ReadOnlySpan<byte> key)
    {
        try
        {
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = MaxDepth });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return Value.Missing;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return Value.Missing;
                }

                var isTargetKey = reader.ValueTextEquals(key);
                if (!reader.Read())
                {
                    return Value.Missing;
                }

                var kind = reader.TokenType;
                var start = checked((int)reader.TokenStartIndex);
                var text = kind == JsonTokenType.String ? reader.GetString() ?? string.Empty : string.Empty;

                if (kind is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }

                var end = checked((int)reader.BytesConsumed);
                if (!isTargetKey)
                {
                    continue;
                }

                if (kind != JsonTokenType.String)
                {
                    text = kind == JsonTokenType.Null
                        ? string.Empty
                        : Encoding.UTF8.GetString(body[start..end]);
                }

                return new Value
                {
                    Exists = true,
                    Kind = kind,
                    Text = text,
                    Start = start,
                    End = end,
                };
            }
        }
        catch (JsonException)
        {
            // gjson is permissive; invalid input simply matches nothing here.
        }

        return Value.Missing;
    }

    /// <summary>
    /// gjson <c>input.#</c>: the number of array elements, or 0 when the value
    /// is missing or not an array (gjson returns 0 for strings, objects and
    /// numbers as well).
    /// </summary>
    public static int TopLevelArrayLength(ReadOnlySpan<byte> body, ReadOnlySpan<byte> key)
    {
        var value = GetTopLevel(body, key);
        return value.Exists && value.Kind == JsonTokenType.StartArray
            ? CountArray(body[value.Start..value.End])
            : 0;
    }

    /// <summary>gjson <c>input.&lt;i&gt;.&lt;key&gt;.String()</c>.</summary>
    public static string GetInputItemString(ReadOnlySpan<byte> body, int itemIndex, ReadOnlySpan<byte> key)
    {
        if (!TryGetInputItem(body, itemIndex, out var item))
        {
            return string.Empty;
        }

        TryGetProperty(item, key, out _, out _, out _, out var text);
        return text;
    }

    /// <summary>gjson <c>input.&lt;i&gt;.&lt;key&gt;.#</c>.</summary>
    public static int GetInputItemArrayLength(ReadOnlySpan<byte> body, int itemIndex, ReadOnlySpan<byte> key)
    {
        if (!TryGetInputItem(body, itemIndex, out var item))
        {
            return 0;
        }

        if (!TryGetProperty(item, key, out var start, out var end, out var kind, out _)
            || kind != JsonTokenType.StartArray)
        {
            return 0;
        }

        return CountArray(item[start..end]);
    }

    /// <summary>gjson <c>input.&lt;i&gt;.content.&lt;j&gt;.&lt;key&gt;.String()</c>.</summary>
    public static string GetContentPartString(
        ReadOnlySpan<byte> body,
        int itemIndex,
        int partIndex,
        ReadOnlySpan<byte> key)
    {
        if (!TryGetContentPart(body, itemIndex, partIndex, out var part))
        {
            return string.Empty;
        }

        TryGetProperty(part, key, out _, out _, out _, out var text);
        return text;
    }

    /// <summary>
    /// sjson <c>SetBytes(body, "input.&lt;i&gt;.content.&lt;j&gt;.&lt;key&gt;", value)</c>;
    /// null when the path does not resolve (unreachable for a validated
    /// decisions body).
    /// </summary>
    public static byte[]? SetContentPartString(
        ReadOnlySpan<byte> body,
        int itemIndex,
        int partIndex,
        ReadOnlySpan<byte> key,
        string value)
    {
        var input = GetTopLevel(body, "input"u8);
        if (!input.Exists || input.Kind != JsonTokenType.StartArray)
        {
            return null;
        }

        var inputSpan = body[input.Start..input.End];
        if (!TryGetArrayElement(inputSpan, itemIndex, out var itemStart, out var itemEnd))
        {
            return null;
        }

        var item = inputSpan[itemStart..itemEnd];
        if (!TryGetProperty(item, "content"u8, out var contentStart, out var contentEnd, out var contentKind, out _)
            || contentKind != JsonTokenType.StartArray)
        {
            return null;
        }

        var content = item[contentStart..contentEnd];
        if (!TryGetArrayElement(content, partIndex, out var partStart, out var partEnd))
        {
            return null;
        }

        var part = content[partStart..partEnd];
        var patched = RawJsonStringSet.SetKey(part, Encoding.UTF8.GetString(key), value);

        var absolute = input.Start + itemStart + contentStart + partStart;
        var result = new byte[body.Length - part.Length + patched.Length];
        body[..absolute].CopyTo(result);
        patched.CopyTo(result.AsSpan(absolute));
        body[(absolute + part.Length)..].CopyTo(result.AsSpan(absolute + patched.Length));
        return result;
    }

    private static bool TryGetInputItem(ReadOnlySpan<byte> body, int itemIndex, out ReadOnlySpan<byte> item)
    {
        item = default;

        var input = GetTopLevel(body, "input"u8);
        if (!input.Exists || input.Kind != JsonTokenType.StartArray)
        {
            return false;
        }

        var inputSpan = body[input.Start..input.End];
        if (!TryGetArrayElement(inputSpan, itemIndex, out var start, out var end))
        {
            return false;
        }

        item = inputSpan[start..end];
        return true;
    }

    private static bool TryGetContentPart(
        ReadOnlySpan<byte> body,
        int itemIndex,
        int partIndex,
        out ReadOnlySpan<byte> part)
    {
        part = default;

        if (!TryGetInputItem(body, itemIndex, out var item))
        {
            return false;
        }

        if (!TryGetProperty(item, "content"u8, out var contentStart, out var contentEnd, out var kind, out _)
            || kind != JsonTokenType.StartArray)
        {
            return false;
        }

        var content = item[contentStart..contentEnd];
        if (!TryGetArrayElement(content, partIndex, out var start, out var end))
        {
            return false;
        }

        part = content[start..end];
        return true;
    }

    private static int CountArray(ReadOnlySpan<byte> array)
    {
        var count = 0;

        try
        {
            var reader = new Utf8JsonReader(array, new JsonReaderOptions { MaxDepth = MaxDepth });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                return 0;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    break;
                }

                count++;
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            return 0;
        }

        return count;
    }

    private static bool TryGetArrayElement(ReadOnlySpan<byte> array, int index, out int start, out int end)
    {
        start = 0;
        end = 0;
        if (index < 0)
        {
            return false;
        }

        try
        {
            var reader = new Utf8JsonReader(array, new JsonReaderOptions { MaxDepth = MaxDepth });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
            {
                return false;
            }

            var current = -1;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                {
                    break;
                }

                current++;
                var elementStart = checked((int)reader.TokenStartIndex);
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }

                var elementEnd = checked((int)reader.BytesConsumed);
                if (current == index)
                {
                    start = elementStart;
                    end = elementEnd;
                    return true;
                }
            }
        }
        catch (JsonException)
        {
            // gjson is permissive; an invalid array matches nothing.
        }

        return false;
    }

    /// <summary>
    /// gjson's first-match object property lookup. Returns the value span and
    /// <c>Result.String()</c> for it.
    /// </summary>
    private static bool TryGetProperty(
        ReadOnlySpan<byte> obj,
        ReadOnlySpan<byte> key,
        out int start,
        out int end,
        out JsonTokenType kind,
        out string text)
    {
        start = 0;
        end = 0;
        kind = default;
        text = string.Empty;

        try
        {
            var reader = new Utf8JsonReader(obj, new JsonReaderOptions { MaxDepth = MaxDepth });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return false;
                }

                var isTargetKey = reader.ValueTextEquals(key);
                if (!reader.Read())
                {
                    return false;
                }

                var valueKind = reader.TokenType;
                var valueStart = checked((int)reader.TokenStartIndex);
                var valueText = valueKind == JsonTokenType.String
                    ? reader.GetString() ?? string.Empty
                    : string.Empty;

                if (valueKind is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }

                var valueEnd = checked((int)reader.BytesConsumed);
                if (!isTargetKey)
                {
                    continue;
                }

                if (valueKind != JsonTokenType.String)
                {
                    valueText = valueKind == JsonTokenType.Null
                        ? string.Empty
                        : Encoding.UTF8.GetString(obj[valueStart..valueEnd]);
                }

                start = valueStart;
                end = valueEnd;
                kind = valueKind;
                text = valueText;
                return true;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }
}
