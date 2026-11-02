using System.Globalization;
using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.Antigravity;

/// <summary>
/// Port of antigravity JSON schema sanitization
/// (llm/transformer/antigravity/sanitizer.go): transforms unsupported JSON
/// Schema features into description hints while preserving semantic
/// information, then uppercases type values for the protobuf schema.
/// </summary>
/// <remarks>
/// The reference operates on Go <c>map[string]any</c> trees decoded from JSON
/// (numbers become float64). The port keeps the same shape as
/// <c>Dictionary&lt;string, object?&gt;</c>/<c>List&lt;object?&gt;</c> trees with
/// <see cref="double"/> numbers, and re-serializes with a Go-faithful writer
/// (sorted keys, Go float formatting, Go HTML escaping).
/// </remarks>
public static class AntigravitySanitizer
{
    /// <summary>Constraints that should be moved to description hints.</summary>
    private static readonly string[] UnsupportedConstraints =
    [
        "minLength", "maxLength", "exclusiveMinimum", "exclusiveMaximum",
        "pattern", "minItems", "maxItems", "format",
        "default", "examples",
    ];

    /// <summary>Keywords that should be removed after hint extraction.</summary>
    private static readonly string[] UnsupportedKeywords =
    [
        // Include all constraints that were moved to description.
        "minLength", "maxLength", "exclusiveMinimum", "exclusiveMaximum",
        "pattern", "minItems", "maxItems", "format",
        "default", "examples",
        // Other unsupported keywords.
        "$schema", "$defs", "definitions", "const", "$ref", "additionalProperties",
        "propertyNames", "title", "$id", "$comment",
    ];

    /// <summary>The name of the placeholder field.</summary>
    public const string EmptySchemaPlaceholderName = "_placeholder";

    /// <summary>The description of the placeholder field.</summary>
    public const string EmptySchemaPlaceholderDescription = "Placeholder. Always pass true.";

    /// <summary>
    /// Port of SanitizeJSONSchema: cleans a JSON schema for Antigravity API
    /// compatibility. Returns a deep copy; nil in, nil out.
    /// </summary>
    public static Dictionary<string, object?>? SanitizeJSONSchema(Dictionary<string, object?>? schema)
    {
        if (schema is null)
        {
            return null;
        }

        var result = DeepCopy(schema)!;

        // Phase 1: Convert and add hints.
        result = ConvertRefsToHints(result);
        result = ConvertConstToEnum(result);
        result = AddEnumHints(result);
        result = AddAdditionalPropertiesHints(result);
        result = MoveConstraintsToDescription(result);

        // Phase 2: Flatten complex structures.
        result = MergeAllOf(result);
        result = FlattenAnyOfOneOf(result);
        result = FlattenTypeArrays(result, null, "");

        // Phase 3: Cleanup.
        result = RemoveUnsupportedKeywords(result, false);
        result = CleanupRequiredFields(result);

        // Phase 4: Add placeholder for empty object schemas.
        result = AddEmptySchemaPlaceholder(result);

        return result;
    }

    /// <summary>
    /// Port of UppercaseSchemaTypes: converts all "type" field values to
    /// UPPERCASE to match the protobuf spec. Returns a new tree.
    /// </summary>
    public static Dictionary<string, object?>? UppercaseSchemaTypes(Dictionary<string, object?>? schema)
    {
        if (schema is null)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, value) in schema)
        {
            if (key == "type")
            {
                result[key] = value is string typeStr ? typeStr.ToUpperInvariant() : value;
            }
            else if (value is Dictionary<string, object?> subSchema)
            {
                result[key] = UppercaseSchemaTypes(subSchema);
            }
            else if (value is List<object?> subArray)
            {
                var newArray = new List<object?>(subArray.Count);
                foreach (var item in subArray)
                {
                    newArray.Add(item is Dictionary<string, object?> itemMap
                        ? UppercaseSchemaTypes(itemMap)
                        : item);
                }

                result[key] = newArray;
            }
            else
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>Port of deepCopy: JSON round-trip (numbers normalized to double).</summary>
    private static Dictionary<string, object?>? DeepCopy(Dictionary<string, object?>? src)
    {
        if (src is null)
        {
            return null;
        }

        return ToGoAny(JsonDocument.Parse(MarshalGoAny(src)).RootElement) as Dictionary<string, object?> ?? [];
    }

    // Phase 1 helpers.

    /// <summary>Port of appendDescriptionHint: appends a hint to the description.</summary>
    private static Dictionary<string, object?> AppendDescriptionHint(
        Dictionary<string, object?> schema,
        string hint)
    {
        if (schema.TryGetValue("description", out var existingObj)
            && existingObj is string existing
            && existing.Length > 0)
        {
            schema["description"] = existing + " (" + hint + ")";
        }
        else
        {
            schema["description"] = hint;
        }

        return schema;
    }

    /// <summary>Port of convertRefsToHints: $ref becomes an object with a "See:" hint.</summary>
    private static Dictionary<string, object?> ConvertRefsToHints(Dictionary<string, object?> schema)
    {
        if (schema.TryGetValue("$ref", out var refObj) && refObj is string refValue)
        {
            var parts = refValue.Split('/');
            var defName = parts[^1];
            var hint = "See: " + defName;

            var newSchema = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "object",
                ["description"] = schema.TryGetValue("description", out var descObj) && descObj is string desc
                    ? desc + " (" + hint + ")"
                    : hint,
            };

            return newSchema;
        }

        foreach (var (key, value) in schema.ToList())
        {
            if (value is Dictionary<string, object?> subSchema)
            {
                schema[key] = ConvertRefsToHints(subSchema);
            }
            else if (value is List<object?> subArray)
            {
                schema[key] = ProcessArray(subArray, ConvertRefsToHints);
            }
        }

        return schema;
    }

    /// <summary>Port of convertConstToEnum: a const without enum becomes a single-value enum.</summary>
    private static Dictionary<string, object?> ConvertConstToEnum(Dictionary<string, object?> schema)
    {
        if (schema.TryGetValue("const", out var constVal) && !schema.ContainsKey("enum"))
        {
            schema["enum"] = new List<object?> { constVal };
        }

        foreach (var (key, value) in schema.ToList())
        {
            if (key == "const")
            {
                continue;
            }

            if (value is Dictionary<string, object?> subSchema)
            {
                schema[key] = ConvertConstToEnum(subSchema);
            }
            else if (value is List<object?> subArray)
            {
                schema[key] = ProcessArray(subArray, ConvertConstToEnum);
            }
        }

        return schema;
    }

    /// <summary>Port of addEnumHints: 2-10 enum values become an "Allowed:" hint.</summary>
    private static Dictionary<string, object?> AddEnumHints(Dictionary<string, object?> schema)
    {
        if (schema.TryGetValue("enum", out var enumObj)
            && enumObj is List<object?> enumVals
            && enumVals.Count > 1
            && enumVals.Count <= 10)
        {
            var strVals = enumVals.Select(FormatGoValue).ToList();
            schema = AppendDescriptionHint(schema, "Allowed: " + string.Join(", ", strVals));
        }

        foreach (var (key, value) in schema.ToList())
        {
            if (key != "enum")
            {
                if (value is Dictionary<string, object?> subSchema)
                {
                    schema[key] = AddEnumHints(subSchema);
                }
                else if (value is List<object?> subArray)
                {
                    schema[key] = ProcessArray(subArray, AddEnumHints);
                }
            }
        }

        return schema;
    }

    /// <summary>Port of addAdditionalPropertiesHints: additionalProperties:false gets a hint.</summary>
    private static Dictionary<string, object?> AddAdditionalPropertiesHints(Dictionary<string, object?> schema)
    {
        if (schema.TryGetValue("additionalProperties", out var apObj) && apObj is bool ap && !ap)
        {
            schema = AppendDescriptionHint(schema, "No extra properties allowed");
        }

        foreach (var (key, value) in schema.ToList())
        {
            if (key != "additionalProperties")
            {
                if (value is Dictionary<string, object?> subSchema)
                {
                    schema[key] = AddAdditionalPropertiesHints(subSchema);
                }
                else if (value is List<object?> subArray)
                {
                    schema[key] = ProcessArray(subArray, AddAdditionalPropertiesHints);
                }
            }
        }

        return schema;
    }

    /// <summary>Port of moveConstraintsToDescription: primitive constraints become hints.</summary>
    private static Dictionary<string, object?> MoveConstraintsToDescription(Dictionary<string, object?> schema)
    {
        foreach (var constraint in UnsupportedConstraints)
        {
            if (schema.TryGetValue(constraint, out var val)
                && val is not Dictionary<string, object?>
                && val is not List<object?>)
            {
                schema = AppendDescriptionHint(schema, constraint + ": " + FormatGoValue(val));
            }
        }

        foreach (var (key, value) in schema.ToList())
        {
            if (value is Dictionary<string, object?> subSchema)
            {
                schema[key] = MoveConstraintsToDescription(subSchema);
            }
            else if (value is List<object?> subArray)
            {
                schema[key] = ProcessArray(subArray, MoveConstraintsToDescription);
            }
        }

        return schema;
    }

    // Phase 2 helpers.

    /// <summary>Port of mergeAllOf: merges allOf branches (properties override, required unique).</summary>
    private static Dictionary<string, object?> MergeAllOf(Dictionary<string, object?> schema)
    {
        if (schema.TryGetValue("allOf", out var allOfObj) && allOfObj is List<object?> allOf)
        {
            var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
            var mergedProps = new Dictionary<string, object?>(StringComparer.Ordinal);
            var mergedRequired = new List<string>();

            foreach (var item in allOf)
            {
                if (item is not Dictionary<string, object?> sub)
                {
                    continue;
                }

                // Merge properties.
                if (sub.TryGetValue("properties", out var propsObj)
                    && propsObj is Dictionary<string, object?> props)
                {
                    foreach (var (k, v) in props)
                    {
                        mergedProps[k] = v;
                    }
                }

                // Merge required.
                if (sub.TryGetValue("required", out var reqObj) && reqObj is List<object?> req)
                {
                    foreach (var r in req)
                    {
                        if (r is string rStr && !mergedRequired.Contains(rStr))
                        {
                            mergedRequired.Add(rStr);
                        }
                    }
                }

                // Copy other fields (first-wins).
                foreach (var (k, v) in sub)
                {
                    if (k != "properties" && k != "required" && !merged.ContainsKey(k))
                    {
                        merged[k] = v;
                    }
                }
            }

            // Apply merged content.
            if (mergedProps.Count > 0)
            {
                if (schema.TryGetValue("properties", out var existingPropsObj)
                    && existingPropsObj is Dictionary<string, object?> existingProps)
                {
                    foreach (var (k, v) in mergedProps)
                    {
                        existingProps[k] = v;
                    }
                }
                else
                {
                    schema["properties"] = mergedProps;
                }
            }

            if (mergedRequired.Count > 0)
            {
                var existingRequired = new List<string>();
                if (schema.TryGetValue("required", out var reqObj) && reqObj is List<object?> req)
                {
                    foreach (var r in req)
                    {
                        if (r is string rStr)
                        {
                            existingRequired.Add(rStr);
                        }
                    }
                }

                foreach (var r in mergedRequired)
                {
                    if (!existingRequired.Contains(r))
                    {
                        existingRequired.Add(r);
                    }
                }

                schema["required"] = existingRequired.Cast<object?>().ToList();
            }

            foreach (var (k, v) in merged)
            {
                if (k != "properties" && k != "required" && !schema.ContainsKey(k))
                {
                    schema[k] = v;
                }
            }

            schema.Remove("allOf");
        }

        foreach (var (key, value) in schema.ToList())
        {
            if (value is Dictionary<string, object?> subSchema)
            {
                schema[key] = MergeAllOf(subSchema);
            }
            else if (value is List<object?> subArray)
            {
                schema[key] = ProcessArray(subArray, MergeAllOf);
            }
        }

        return schema;
    }

    /// <summary>Port of flattenAnyOfOneOf: collapses unions to an enum or a best-scoring branch.</summary>
    private static Dictionary<string, object?> FlattenAnyOfOneOf(Dictionary<string, object?> schema)
    {
        foreach (var unionKey in new[] { "anyOf", "oneOf" })
        {
            if (schema.TryGetValue(unionKey, out var optionsObj)
                && optionsObj is List<object?> options
                && options.Count > 0)
            {
                var parentDesc = schema.TryGetValue("description", out var parentDescObj)
                    && parentDescObj is string pd
                    ? pd
                    : "";

                // Check for enum pattern.
                var mergedEnum = TryMergeEnumFromUnion(options);
                if (mergedEnum is not null)
                {
                    schema.Remove(unionKey);
                    schema["type"] = "string";
                    schema["enum"] = mergedEnum;
                    if (parentDesc.Length > 0)
                    {
                        schema["description"] = parentDesc;
                    }

                    continue;
                }

                // Flatten logic.
                var bestIdx = 0;
                var bestScore = -1;
                var allTypes = new List<string>();

                for (var i = 0; i < options.Count; i++)
                {
                    if (options[i] is Dictionary<string, object?> optMap)
                    {
                        var (score, typeName) = ScoreSchemaOption(optMap);
                        if (typeName.Length > 0)
                        {
                            allTypes.Add(typeName);
                        }

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestIdx = i;
                        }
                    }
                }

                if (bestIdx < options.Count && options[bestIdx] is Dictionary<string, object?> selectedMap)
                {
                    // Recursively flatten selected.
                    var selected = FlattenAnyOfOneOf(selectedMap);

                    schema.Remove(unionKey);

                    // Description merge.
                    var childDesc = selected.TryGetValue("description", out var childDescObj)
                        && childDescObj is string cd
                        ? cd
                        : "";
                    if (parentDesc.Length > 0)
                    {
                        if (childDesc.Length > 0 && childDesc != parentDesc)
                        {
                            selected["description"] = parentDesc + " (" + childDesc + ")";
                        }
                        else
                        {
                            selected["description"] = parentDesc;
                        }
                    }

                    // Type hints.
                    var uniqueTypes = allTypes.Distinct().ToList();
                    if (uniqueTypes.Count > 1)
                    {
                        selected = AppendDescriptionHint(selected, "Accepts: " + string.Join(" | ", uniqueTypes));
                    }

                    // Copy selected fields to schema.
                    foreach (var (k, v) in selected)
                    {
                        schema[k] = v;
                    }
                }
            }
        }

        foreach (var (key, value) in schema.ToList())
        {
            if (value is Dictionary<string, object?> subSchema)
            {
                schema[key] = FlattenAnyOfOneOf(subSchema);
            }
            else if (value is List<object?> subArray)
            {
                schema[key] = ProcessArray(subArray, FlattenAnyOfOneOf);
            }
        }

        return schema;
    }

    /// <summary>
    /// Port of flattenTypeArrays: collapses a type array to the first non-null
    /// type with "Accepts:"/"nullable" hints, and drops nullable fields from
    /// the root required array.
    /// </summary>
    private static Dictionary<string, object?> FlattenTypeArrays(
        Dictionary<string, object?> schema,
        Dictionary<string, List<string>>? nullableFields,
        string currentPath)
    {
        nullableFields ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (schema.TryGetValue("type", out var typesObj) && typesObj is List<object?> types)
        {
            var nonNullTypes = new List<string>();
            var hasNull = false;

            foreach (var t in types)
            {
                if (t is string tStr)
                {
                    if (tStr == "null")
                    {
                        hasNull = true;
                    }
                    else
                    {
                        nonNullTypes.Add(tStr);
                    }
                }
            }

            var firstType = nonNullTypes.Count > 0 ? nonNullTypes[0] : hasNull ? "null" : "string";
            schema["type"] = firstType;

            if (nonNullTypes.Count > 1)
            {
                schema = AppendDescriptionHint(schema, "Accepts: " + string.Join(" | ", nonNullTypes));
            }

            if (hasNull)
            {
                schema = AppendDescriptionHint(schema, "nullable");
            }
        }

        // Recursively process properties.
        if (schema.TryGetValue("properties", out var propsObj)
            && propsObj is Dictionary<string, object?> props)
        {
            foreach (var (key, val) in props.ToList())
            {
                if (val is Dictionary<string, object?> propSchema)
                {
                    var propPath = currentPath.Length == 0
                        ? "properties." + key
                        : currentPath + ".properties." + key;

                    var processed = FlattenTypeArrays(propSchema, nullableFields, propPath);
                    props[key] = processed;

                    if (processed.TryGetValue("description", out var descObj)
                        && descObj is string desc
                        && desc.Contains("nullable", StringComparison.Ordinal))
                    {
                        if (!nullableFields.TryGetValue(currentPath, out var list))
                        {
                            list = [];
                            nullableFields[currentPath] = list;
                        }

                        list.Add(key);
                    }
                }
            }
        }

        // Remove nullable fields from required array at root.
        if (currentPath.Length == 0
            && schema.TryGetValue("required", out var reqObj)
            && reqObj is List<object?> req)
        {
            var nullableAtRoot = nullableFields.TryGetValue("", out var atRoot) ? atRoot : [];

            var newReq = new List<object?>();
            foreach (var r in req)
            {
                if (r is string rStr && !nullableAtRoot.Contains(rStr))
                {
                    newReq.Add(rStr);
                }
            }

            if (newReq.Count == 0)
            {
                schema.Remove("required");
            }
            else
            {
                schema["required"] = newReq;
            }
        }

        // Recursion for other fields.
        foreach (var (key, value) in schema.ToList())
        {
            if (key != "properties")
            {
                if (value is Dictionary<string, object?> subSchema)
                {
                    schema[key] = FlattenTypeArrays(subSchema, nullableFields, currentPath + "." + key);
                }
                else if (value is List<object?> subArray)
                {
                    schema[key] = ProcessArray(subArray, m => FlattenTypeArrays(m, nullableFields, currentPath));
                }
            }
        }

        return schema;
    }

    // Phase 3 helpers.

    /// <summary>Port of removeUnsupportedKeywords: drops keywords outside property names.</summary>
    private static Dictionary<string, object?> RemoveUnsupportedKeywords(
        Dictionary<string, object?> schema,
        bool insideProperties)
    {
        foreach (var key in schema.Keys.ToList())
        {
            var value = schema[key];
            if (!insideProperties && UnsupportedKeywords.Contains(key))
            {
                schema.Remove(key);
                continue;
            }

            if (value is Dictionary<string, object?> subSchema)
            {
                if (key == "properties")
                {
                    var newProps = new Dictionary<string, object?>(StringComparer.Ordinal);
                    foreach (var (k, v) in subSchema)
                    {
                        newProps[k] = v is Dictionary<string, object?> vMap
                            ? RemoveUnsupportedKeywords(vMap, false)
                            : v;
                    }

                    schema[key] = newProps;
                }
                else
                {
                    schema[key] = RemoveUnsupportedKeywords(subSchema, false);
                }
            }
            else if (value is List<object?> subArray)
            {
                schema[key] = ProcessArray(subArray, m => RemoveUnsupportedKeywords(m, false));
            }
        }

        return schema;
    }

    /// <summary>Port of cleanupRequiredFields: drops required entries without a property.</summary>
    private static Dictionary<string, object?> CleanupRequiredFields(Dictionary<string, object?> schema)
    {
        if (schema.TryGetValue("required", out var reqObj)
            && reqObj is List<object?> req
            && schema.TryGetValue("properties", out var propsObj)
            && propsObj is Dictionary<string, object?> props)
        {
            var validReq = new List<object?>();
            foreach (var r in req)
            {
                if (r is string rStr && props.ContainsKey(rStr))
                {
                    validReq.Add(rStr);
                }
            }

            if (validReq.Count == 0)
            {
                schema.Remove("required");
            }
            else
            {
                schema["required"] = validReq;
            }
        }

        foreach (var (key, value) in schema.ToList())
        {
            if (value is Dictionary<string, object?> subSchema)
            {
                schema[key] = CleanupRequiredFields(subSchema);
            }
            else if (value is List<object?> subArray)
            {
                schema[key] = ProcessArray(subArray, CleanupRequiredFields);
            }
        }

        return schema;
    }

    // Phase 4 helpers.

    /// <summary>Port of addEmptySchemaPlaceholder: empty object schemas get a placeholder field.</summary>
    private static Dictionary<string, object?> AddEmptySchemaPlaceholder(Dictionary<string, object?> schema)
    {
        if (schema.TryGetValue("type", out var typeObj) && typeObj is string typeVal && typeVal == "object")
        {
            var hasProps = schema.TryGetValue("properties", out var propsObj)
                && propsObj is Dictionary<string, object?> props
                && props.Count > 0;
            if (!hasProps)
            {
                schema["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [EmptySchemaPlaceholderName] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "boolean",
                        ["description"] = EmptySchemaPlaceholderDescription,
                    },
                };
                schema["required"] = new List<object?> { EmptySchemaPlaceholderName };
            }
        }

        foreach (var (key, value) in schema.ToList())
        {
            if (value is Dictionary<string, object?> subSchema)
            {
                schema[key] = AddEmptySchemaPlaceholder(subSchema);
            }
            else if (value is List<object?> subArray)
            {
                schema[key] = ProcessArray(subArray, AddEmptySchemaPlaceholder);
            }
        }

        return schema;
    }

    // Utilities.

    /// <summary>Port of processArray: applies fn to map items, keeps everything else.</summary>
    private static List<object?> ProcessArray(
        List<object?> arr,
        Func<Dictionary<string, object?>, Dictionary<string, object?>> fn)
    {
        var newArr = new List<object?>(arr.Count);
        foreach (var item in arr)
        {
            newArr.Add(item is Dictionary<string, object?> itemMap ? fn(itemMap) : item);
        }

        return newArr;
    }

    /// <summary>Port of scoreSchemaOption: (score, typeName) for union branch ranking.</summary>
    private static (int Score, string TypeName) ScoreSchemaOption(Dictionary<string, object?> schema)
    {
        var typeName = schema.TryGetValue("type", out var typeObj) && typeObj is string t ? t : "";

        if (typeName == "object" || ValuePresent(schema, "properties"))
        {
            return (3, "object");
        }

        if (typeName == "array" || ValuePresent(schema, "items"))
        {
            return (2, "array");
        }

        if (typeName.Length > 0 && typeName != "null")
        {
            return (1, typeName);
        }

        if (typeName.Length == 0)
        {
            return (0, "null");
        }

        return (0, typeName);
    }

    /// <summary>
    /// Port of tryMergeEnumFromUnion: returns the merged enum when every union
    /// branch is a simple const/enum leaf, nil otherwise.
    /// </summary>
    private static List<object?>? TryMergeEnumFromUnion(List<object?> options)
    {
        var enumValues = new List<object?>();

        foreach (var opt in options)
        {
            if (opt is not Dictionary<string, object?> optMap)
            {
                return null;
            }

            if (optMap.TryGetValue("const", out var constVal))
            {
                enumValues.Add(constVal);
                continue;
            }

            if (optMap.TryGetValue("enum", out var enumsObj) && enumsObj is List<object?> enums)
            {
                if (enums.Count == 1)
                {
                    enumValues.Add(enums[0]);
                    continue;
                }

                if (enums.Count > 0)
                {
                    enumValues.AddRange(enums);
                    continue;
                }
            }

            // If complex structure, not simple enum.
            if (ValuePresent(optMap, "properties")
                || ValuePresent(optMap, "items")
                || ValuePresent(optMap, "anyOf")
                || ValuePresent(optMap, "oneOf")
                || ValuePresent(optMap, "allOf"))
            {
                return null;
            }

            // If type but no const/enum.
            if (optMap.ContainsKey("type") && !optMap.ContainsKey("const") && !optMap.ContainsKey("enum"))
            {
                return null;
            }
        }

        return enumValues.Count > 0 ? enumValues : null;
    }

    /// <summary>Go's <c>m["key"] != nil</c> presence check on the decoded tree.</summary>
    private static bool ValuePresent(Dictionary<string, object?> schema, string key)
        => schema.TryGetValue(key, out var value) && value is not null;

    /// <summary>Port of Go fmt %v for hint strings: null becomes "&lt;nil&gt;".</summary>
    private static string FormatGoValue(object? value)
        => value switch
        {
            null => "<nil>",
            string s => s,
            bool b => b ? "true" : "false",
            double d => GoJsonFloat.Format(d),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<nil>",
        };

    /// <summary>
    /// Port of decoding JSON into a Go-shaped any-tree (json.Unmarshal):
    /// objects become dictionaries, arrays become lists, numbers become double.
    /// </summary>
    internal static object? ToGoAny(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => ToGoAnyObject(element),
            JsonValueKind.Array => ToGoAnyArray(element),
            JsonValueKind.String => element.GetString()!,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

    private static Dictionary<string, object?> ToGoAnyObject(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = ToGoAny(property.Value);
        }

        return result;
    }

    private static List<object?> ToGoAnyArray(JsonElement element)
    {
        var result = new List<object?>();
        foreach (var item in element.EnumerateArray())
        {
            result.Add(ToGoAny(item));
        }

        return result;
    }

    /// <summary>
    /// Port of Go <c>json.Marshal</c> over an any-tree: sorted keys, Go float
    /// formatting, Go HTML escaping (via <see cref="GoJson.Escape"/>).
    /// </summary>
    internal static byte[] MarshalGoAny(object? value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteGoAny(writer, value);
        }

        return GoJson.Escape(stream.ToArray());
    }

    private static void WriteGoAny(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                return;
            case Dictionary<string, object?> obj:
                writer.WriteStartObject();
                foreach (var key in obj.Keys.OrderBy(k => k, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(key);
                    WriteGoAny(writer, obj[key]);
                }

                writer.WriteEndObject();
                return;
            case List<object?> list:
                writer.WriteStartArray();
                foreach (var item in list)
                {
                    WriteGoAny(writer, item);
                }

                writer.WriteEndArray();
                return;
            case string s:
                writer.WriteStringValue(s);
                return;
            case bool b:
                writer.WriteBooleanValue(b);
                return;
            default:
                // Go json.Unmarshal decodes all numbers as float64.
                writer.WriteRawValue(GoJsonFloat.Format(Convert.ToDouble(value, CultureInfo.InvariantCulture)), skipInputValidation: false);
                return;
        }
    }

    /// <summary>Decodes a JSON fragment into the Go-shaped tree (for tests and callers).</summary>
    internal static Dictionary<string, object?>? ParseSchema(string json)
        => ToGoAny(JsonDocument.Parse(json).RootElement) as Dictionary<string, object?>;

    /// <summary>Marshals a Go-shaped tree back into a JSON element (xjson.MustMarshal).</summary>
    internal static JsonElement MarshalToElement(Dictionary<string, object?>? schema)
    {
        using var doc = JsonDocument.Parse(MarshalGoAny(schema));
        return doc.RootElement.Clone();
    }
}
