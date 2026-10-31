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
    {
        var quoted = GoJson.Marshal(model);

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
                        "cannot set array element for non-numeric key 'model'");
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

                var isModelKey = reader.ValueTextEquals("model"u8);
                if (!reader.Read())
                {
                    throw new InvalidOperationException("json must be an object or array");
                }

                if (isModelKey && valueStart is null)
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
            var result = new byte[body.Length - (end - start) + quoted.Length];
            body[..start].CopyTo(result);
            quoted.CopyTo(result, start);
            body[end..].CopyTo(result.AsSpan(start + quoted.Length));
            return result;
        }

        if (objectEnd < 0)
        {
            throw new InvalidOperationException("json must be an object or array");
        }

        ReadOnlySpan<byte> keyBytes = "\"model\":"u8;
        var extra = keyBytes.Length + quoted.Length + (hasProperties ? 1 : 0);
        var appended = new byte[body.Length + extra];
        var position = objectEnd;

        body[..objectEnd].CopyTo(appended);
        if (hasProperties)
        {
            appended[position++] = (byte)',';
        }

        keyBytes.CopyTo(appended.AsSpan(position));
        position += keyBytes.Length;
        quoted.CopyTo(appended, position);
        position += quoted.Length;
        body[objectEnd..].CopyTo(appended.AsSpan(position));

        return appended;
    }
}
