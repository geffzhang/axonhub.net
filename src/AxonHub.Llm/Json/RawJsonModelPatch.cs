using System.Text;
using System.Text.Json;

namespace AxonHub.Llm.Json;

/// <summary>
/// Port of the only sjson call the alpha-search and decisions outbounds make:
/// <c>sjson.SetBytes(body, "model", value)</c> against a top-level object key.
/// </summary>
/// <remarks>
/// sjson splices the original bytes: an existing key keeps every other byte of
/// the body untouched and only replaces the value span; a missing key is
/// appended just before the closing brace (sjson's appendRawPaths). The raw
/// request bodies these transformers receive are already validated JSON
/// objects, so the failure branches mirror sjson's wording without being
/// reachable through the pipeline.
/// </remarks>
internal static class RawJsonModelPatch
{
    /// <summary>Replaces the top-level <c>model</c> string, or appends it.</summary>
    public static byte[] SetModel(ReadOnlySpan<byte> body, string model)
        => RawJsonPatch.SetTopLevelRaw(body, "model"u8, GoJson.Marshal(model));
}

/// <summary>
/// Port of <c>sjson.SetBytes</c> for a top-level key, byte-preserving like the
/// reference library (the Bailian outbound uses it to add
/// <c>enable_thinking</c>).
/// </summary>
internal static class RawJsonPatch
{
    /// <summary>
    /// Replaces the first top-level <paramref name="keyName"/> value with the
    /// raw <paramref name="valueJson"/> bytes, or appends the key when missing.
    /// </summary>
    public static byte[] SetTopLevelRaw(
        ReadOnlySpan<byte> body,
        ReadOnlySpan<byte> keyName,
        ReadOnlySpan<byte> valueJson)
    {
        int? valueStart = null;
        int? valueEnd = null;
        var objectEnd = -1;
        var hasProperties = false;

        try
        {
            // Go's encoding/json accepts nesting up to 10000 levels; the
            // reader's default of 64 would reject documents Go happily skips.
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = 10_000 });
            if (!reader.Read())
            {
                throw new InvalidOperationException("json must be an object or array");
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.StartArray:
                    throw new InvalidOperationException(
                        "cannot set array element for non-numeric key '"
                        + Encoding.UTF8.GetString(keyName) + "'");
                case JsonTokenType.StartObject:
                    break;
                default:
                    throw new InvalidOperationException("json must be an object or array");
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    objectEnd = checked((int)reader.BytesConsumed) - 1;
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new InvalidOperationException("json must be an object or array");
                }

                var isTargetKey = reader.ValueTextEquals(keyName);
                if (!reader.Read())
                {
                    throw new InvalidOperationException("json must be an object or array");
                }

                if (isTargetKey && valueStart is null)
                {
                    valueStart = checked((int)reader.TokenStartIndex);
                    reader.Skip();
                    valueEnd = checked((int)reader.BytesConsumed);

                    // gjson.Get returns the first matching key; splice without
                    // scanning the rest of the document, like sjson's
                    // optimistic path.
                    break;
                }

                hasProperties = true;
                reader.Skip();
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("json must be an object or array", ex);
        }

        if (valueStart is { } start && valueEnd is { } end)
        {
            var result = new byte[body.Length - (end - start) + valueJson.Length];
            body[..start].CopyTo(result);
            valueJson.CopyTo(result.AsSpan(start));
            body[end..].CopyTo(result.AsSpan(start + valueJson.Length));
            return result;
        }

        if (objectEnd < 0)
        {
            throw new InvalidOperationException("json must be an object or array");
        }

        // sjson writes `"key":value` verbatim.
        var keyJson = new byte[keyName.Length + 3];
        keyJson[0] = (byte)'"';
        keyName.CopyTo(keyJson.AsSpan(1));
        keyJson[^2] = (byte)'"';
        keyJson[^1] = (byte)':';

        var extra = keyJson.Length + valueJson.Length + (hasProperties ? 1 : 0);
        var appended = new byte[body.Length + extra];
        var position = objectEnd;

        body[..objectEnd].CopyTo(appended);
        if (hasProperties)
        {
            appended[position++] = (byte)',';
        }

        keyJson.CopyTo(appended, position);
        position += keyJson.Length;
        valueJson.CopyTo(appended.AsSpan(position));
        position += valueJson.Length;
        body[objectEnd..].CopyTo(appended.AsSpan(position));

        return appended;
    }
}

/// <summary>
/// Port of the <c>gjson.GetBytes</c> top-level lookups the Vertex executor makes
/// (<c>anthropic_version</c>, <c>model</c>, <c>stream</c>), plus
/// <c>sjson.DeleteBytes</c> for a top-level key.
/// </summary>
internal static class RawJsonGjson
{
    /// <summary>gjson <c>Result.Exists()</c>: present keys count even when null.</summary>
    public static bool HasTopLevelKey(ReadOnlySpan<byte> body, ReadOnlySpan<byte> keyName)
        => GetTopLevelValue(body, keyName).Exists;

    /// <summary>gjson <c>Result.String()</c>.</summary>
    public static string GetTopLevelString(ReadOnlySpan<byte> body, ReadOnlySpan<byte> keyName)
        => GetTopLevelValue(body, keyName).GjsonString();

    /// <summary>gjson <c>Result.Bool()</c>.</summary>
    public static bool GetTopLevelBool(ReadOnlySpan<byte> body, ReadOnlySpan<byte> keyName)
        => GetTopLevelValue(body, keyName).GjsonBool();

    /// <summary>
    /// Port of <c>sjson.DeleteBytes</c> for a top-level key: splices out the
    /// key and value (and the comma on the appropriate side); a missing key or
    /// a non-object body is returned unchanged (the reference ignores the
    /// "no change" error).
    /// </summary>
    public static byte[] DeleteTopLevelKey(ReadOnlySpan<byte> body, ReadOnlySpan<byte> keyName)
    {
        var value = GetTopLevelValue(body, keyName);
        if (!value.Exists || value.Start <= 0)
        {
            return body.ToArray();
        }

        var prefixLength = DeleteTailItem(body[..value.Start], out var delNextComma);
        var skip = 0;
        if (delNextComma)
        {
            // appendRawPaths skips whitespace and the comma that follows the
            // value when the deleted key was the first property.
            var i = value.End;
            var j = 0;
            for (; i < body.Length; i++, j++)
            {
                if (body[i] <= ' ')
                {
                    continue;
                }

                if (body[i] == (byte)',')
                {
                    skip = j + 1;
                }

                break;
            }
        }

        var suffixStart = value.End + skip;
        var result = new byte[prefixLength + (body.Length - suffixStart)];
        body[..prefixLength].CopyTo(result);
        body[suffixStart..].CopyTo(result.AsSpan(prefixLength));
        return result;
    }

    /// <summary>Walks the top-level object and returns the first matching key.</summary>
    public static TopLevelValue GetTopLevelValue(ReadOnlySpan<byte> body, ReadOnlySpan<byte> keyName)
    {
        try
        {
            // Go's encoding/json accepts nesting up to 10000 levels; the
            // reader's default of 64 would reject documents Go happily skips.
            var reader = new Utf8JsonReader(body, new JsonReaderOptions { MaxDepth = 10_000 });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return TopLevelValue.Missing;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return TopLevelValue.Missing;
                }

                var isTargetKey = reader.ValueTextEquals(keyName);
                if (!reader.Read())
                {
                    return TopLevelValue.Missing;
                }

                var kind = reader.TokenType;
                var start = checked((int)reader.TokenStartIndex);
                var text = kind == JsonTokenType.String
                    ? reader.GetString() ?? string.Empty
                    : string.Empty;

                if (kind is JsonTokenType.StartObject or JsonTokenType.StartArray)
                {
                    reader.Skip();
                }

                var end = checked((int)reader.BytesConsumed);
                if (isTargetKey)
                {
                    if (kind != JsonTokenType.String)
                    {
                        text = kind == JsonTokenType.Null
                            ? string.Empty
                            : Encoding.UTF8.GetString(body[start..end]);
                    }

                    return new TopLevelValue
                    {
                        Exists = true,
                        Kind = kind,
                        Text = text,
                        Start = start,
                        End = end,
                    };
                }
            }
        }
        catch (JsonException)
        {
            // gjson is permissive; invalid input simply matches nothing here.
        }

        return TopLevelValue.Missing;
    }

    /// <summary>
    /// Port of sjson's <c>deleteTailItem</c>: removes the previous key or comma
    /// from the bytes before the deleted value.
    /// </summary>
    private static int DeleteTailItem(ReadOnlySpan<byte> buffer, out bool delNextComma)
    {
        delNextComma = false;
        for (var i = buffer.Length - 1; i >= 0; i--)
        {
            switch (buffer[i])
            {
                case (byte)'[':
                    delNextComma = true;
                    return buffer.Length;
                case (byte)',':
                    return i;
                case (byte)':':
                    i--;
                    for (; i >= 0; i--)
                    {
                        if (buffer[i] != (byte)'"')
                        {
                            continue;
                        }

                        i--;
                        for (; i >= 0; i--)
                        {
                            if (buffer[i] != (byte)'"')
                            {
                                continue;
                            }

                            i--;
                            if (i >= 0 && buffer[i] == (byte)'\\')
                            {
                                i--;
                                continue;
                            }

                            for (; i >= 0; i--)
                            {
                                switch (buffer[i])
                                {
                                    case (byte)'{':
                                        delNextComma = true;
                                        return i + 1;
                                    case (byte)',':
                                        return i;
                                }
                            }
                        }

                        goto done;
                    }

                    goto done;
            }
        }

    done:
        return buffer.Length;
    }
}

/// <summary>gjson Result projection for a top-level key.</summary>
internal readonly struct TopLevelValue
{
    public static readonly TopLevelValue Missing;

    public bool Exists { get; init; }

    public JsonTokenType Kind { get; init; }

    /// <summary>The decoded string for string values, the raw text otherwise.</summary>
    public string Text { get; init; }

    /// <summary>Byte offset of the value start (gjson <c>Result.Index</c>).</summary>
    public int Start { get; init; }

    /// <summary>Byte offset just past the value.</summary>
    public int End { get; init; }

    /// <summary>gjson <c>Result.String()</c>.</summary>
    public string GjsonString() => Exists ? Text : string.Empty;

    /// <summary>gjson <c>Result.Bool()</c>.</summary>
    public bool GjsonBool()
    {
        if (!Exists)
        {
            return false;
        }

        return Kind switch
        {
            JsonTokenType.True => true,
            JsonTokenType.String => GoCast.TryParseBool(Text, out var parsed) && parsed,
            JsonTokenType.Number => GoCast.TryParseFloat(Text, out var number) && number != 0,
            _ => false,
        };
    }
}
