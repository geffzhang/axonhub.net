using System.Text.Json;
using System.Text.Json.Nodes;

namespace AxonHub.Llm.Json;

/// <summary>
/// Port of llm/internal/pkg/xjson/schema.go (Transform +
/// transformSchemaRecursive): walk every subschema of a JSON Schema document
/// and apply a mutation, mirroring the reference's jsonschema-go round-trip.
/// </summary>
/// <remarks>
/// The walk visits exactly the keywords transformSchemaRecursive covers:
/// items, additionalItems, contains, not, if, then, else, propertyNames,
/// unevaluatedProperties, unevaluatedItems, contentSchema; the arrays
/// prefixItems, items (legacy array form), allOf, anyOf, oneOf; and the maps
/// $defs, definitions, dependentSchemas, properties, patternProperties and
/// the object values of dependencies. <c>additionalProperties</c> and unknown
/// keywords are deliberately NOT walked, matching the reference.
///
/// Known divergence (C114): the reference re-marshals through the
/// jsonschema-go struct, which normalizes the document (empty known-keyword
/// containers are dropped, numbers are canonicalized, and a few ill-typed
/// values fail the whole transform). The port preserves the original shape
/// except for the empty-schema collapses (<c>{}</c> to <c>true</c>,
/// <c>{"not":{}}</c> to <c>false</c>), which are cheap to replicate.
/// </remarks>
public static class XjsonSchema
{
    private static readonly string[] SingleSchemaKeywords =
    [
        "items", "additionalItems", "contains", "not", "if", "then", "else",
        "propertyNames", "unevaluatedProperties", "unevaluatedItems", "contentSchema",
    ];

    private static readonly string[] SchemaArrayKeywords =
        ["prefixItems", "allOf", "anyOf", "oneOf"];

    private static readonly string[] SchemaMapKeywords =
        ["$defs", "definitions", "dependentSchemas", "properties", "patternProperties"];

    /// <summary>
    /// Port of xjson.Transform: parse the schema (an object or a boolean),
    /// apply <paramref name="transform"/> to every subschema, and return the
    /// re-serialized document. Invalid input throws <see cref="JsonException"/>,
    /// like the reference returning an error.
    /// </summary>
    public static JsonElement Transform(JsonElement rawSchema, Action<JsonObject> transform)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(rawSchema.GetRawText());
        }
        catch (JsonException)
        {
            throw;
        }

        // Go: json.Unmarshal into jsonschema.Schema fails for any document
        // that is neither an object nor a boolean ("null" included).
        if (node is JsonObject root)
        {
            var result = TransformSchema(root, transform);
            return JsonSerializer.SerializeToElement(result, LlmJson.Options);
        }

        if (node is JsonValue v && v.TryGetValue<bool>(out _))
        {
            return JsonSerializer.SerializeToElement(node, LlmJson.Options);
        }

        throw new JsonException("invalid schema document");
    }

    /// <summary>
    /// Applies <paramref name="transform"/> to the schema (pre-order, like
    /// the reference), recurses into every visited subschema, and returns the
    /// node to store in place of the schema — which may be a collapsed
    /// boolean schema.
    /// </summary>
    private static JsonNode TransformSchema(JsonObject schema, Action<JsonObject> transform)
    {
        transform(schema);

        foreach (var keyword in SingleSchemaKeywords)
        {
            if (!schema.TryGetPropertyValue(keyword, out var value))
            {
                continue;
            }

            var replaced = TransformSchemaNode(value, transform);
            if (replaced is null)
            {
                // Go: a null *Schema pointer is omitted on re-marshal.
                schema.Remove(keyword);
            }
            else if (!ReferenceEquals(replaced, value))
            {
                schema[keyword] = replaced;
            }
        }

        // "items" in its legacy array form lives under the same key.
        if (schema.TryGetPropertyValue("items", out var items) && items is JsonArray itemsArray)
        {
            for (var i = 0; i < itemsArray.Count; i++)
            {
                itemsArray[i] = TransformSchemaNode(itemsArray[i], transform);
            }
        }

        foreach (var keyword in SchemaArrayKeywords)
        {
            if (schema.TryGetPropertyValue(keyword, out var value) && value is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = TransformSchemaNode(array[i], transform);
                }
            }
        }

        foreach (var keyword in SchemaMapKeywords)
        {
            if (schema.TryGetPropertyValue(keyword, out var value) && value is JsonObject map)
            {
                foreach (var (key, sub) in map)
                {
                    // Go: a nil value in a schema map marshals back as null.
                    if (sub is null)
                    {
                        continue;
                    }

                    map[key] = TransformSchemaNode(sub, transform);
                }
            }
        }

        // "dependencies": the reference walks the object values (the legacy
        // schema-valued form) and re-emits string arrays untouched.
        if (schema.TryGetPropertyValue("dependencies", out var deps) && deps is JsonObject depMap)
        {
            foreach (var (key, sub) in depMap)
            {
                if (sub is not JsonObject)
                {
                    continue;
                }

                depMap[key] = TransformSchemaNode(sub, transform);
            }
        }

        // Go's Schema.MarshalJSON: an empty schema is emitted as true, and
        // {"not": {}} as false, recursively at every level. A boolean under
        // "not" always produces the falseSchema shape, so both bools
        // collapse: true is the empty schema, false becomes {not: {}}.
        if (schema.Count == 0)
        {
            return JsonValue.Create(true)!;
        }

        if (schema.Count == 1
            && schema.TryGetPropertyValue("not", out var not)
            && not is JsonValue notValue
            && notValue.TryGetValue<bool>(out _))
        {
            return JsonValue.Create(false)!;
        }

        return schema;
    }

    /// <summary>
    /// Recurses into one subschema value. Boolean schemas survive as-is
    /// (Go accepts them); a scalar in a schema position fails the whole
    /// transform, like Go's unmarshal error.
    /// </summary>
    private static JsonNode? TransformSchemaNode(JsonNode? node, Action<JsonObject> transform)
    {
        switch (node)
        {
            case JsonObject sub:
                return TransformSchema(sub, transform);
            case JsonValue v when v.TryGetValue<bool>(out _):
                return node;
            case null:
                return null;
            default:
                throw new JsonException("invalid schema element");
        }
    }
}
