using System.Text;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Management;

/// <summary>
/// Ports of <c>objects.ParseOverrideOperations</c>,
/// <c>objects.HeaderEntriesToOverrideOperations</c> and the two merge helpers
/// of the override-template service
/// (internal/objects/channel.go:625-665,
/// internal/server/biz/channel_override.go:89-116,
/// internal/server/biz/channel_override_template.go:315-398).
/// </summary>
public static class ChannelOverrideOperations
{
    public const string OpSet = "set";

    public const string OpSetIfAbsent = "set_if_absent";

    public const string OpDelete = "delete";

    public const string OpRename = "rename";

    public const string OpCopy = "copy";

    /// <summary>Legacy header value that turns a set into a delete.</summary>
    public const string ClearDirective = "__AXONHUB_CLEAR__";

    /// <summary>
    /// Port of <c>objects.ParseOverrideOperations</c>. Returns null for the
    /// empty/zero forms; a non-null result is never mutated by the caller.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The text is not valid JSON or does not decode into the operation shape;
    /// the message already carries the reference prefix
    /// (<c>invalid override operations: </c> / <c>invalid override parameters: </c>).
    /// </exception>
    public static IReadOnlyList<OverrideOperationDraft>? ParseOverrideOperations(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        raw = raw.Trim();
        if (raw.Length == 0 || raw == "{}" || raw == "[]")
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(raw);

        // Go validates the whole document before decoding; GoJsonSyntax
        // reproduces its SyntaxError text exactly.
        var syntaxError = GoJsonSyntax.Validate(bytes);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException(
                (raw[0] == '['
                    ? "invalid override operations: "
                    : "invalid override parameters: ")
                + syntaxError);
        }

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        if (raw[0] == '[')
        {
            return ParseOperationArray(root);
        }

        // json.Unmarshal("null", &map) leaves the map nil without an error;
        // the reference then returns an empty (non-nil) operation slice.
        if (root.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException(
                "invalid override parameters: json: cannot unmarshal "
                + GoKindName(root.ValueKind)
                + " into Go value of type map[string]interface {}");
        }

        var operations = new List<OverrideOperationDraft>(root.GetPropertyCount());
        foreach (var property in root.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && property.Value.GetString() == ClearDirective)
            {
                operations.Add(new OverrideOperationDraft
                {
                    Op = OpDelete,
                    Path = property.Name,
                });
            }
            else
            {
                // fmt.Sprintf("%v", value): JSON values decode into Go's any
                // (numbers as float64) and are stringified with %v.
                operations.Add(new OverrideOperationDraft
                {
                    Op = OpSet,
                    Path = property.Name,
                    Value = GoSprint(property.Value),
                });
            }
        }

        return operations;
    }

    /// <summary>
    /// Decodes a stored <c>[]objects.OverrideOperation</c> column. Ent scans a
    /// JSON <c>null</c> into a nil slice; an empty array and the empty forms
    /// ParseOverrideOperations already normalizes both yield no operations.
    /// </summary>
    public static IReadOnlyList<OverrideOperationDraft> ParseStoredOperations(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return ParseOverrideOperations(raw) ?? [];
    }

    /// <summary>Port of <c>objects.HeaderEntriesToOverrideOperations</c>.</summary>
    public static IReadOnlyList<OverrideOperationDraft>? HeaderEntriesToOperations(
        IReadOnlyList<HeaderEntryDraft> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (headers.Count == 0)
        {
            return null;
        }

        var operations = new List<OverrideOperationDraft>(headers.Count);
        foreach (var header in headers)
        {
            operations.Add(header.Value == ClearDirective
                ? new OverrideOperationDraft { Op = OpDelete, Path = header.Key }
                : new OverrideOperationDraft
                {
                    Op = OpSet,
                    Path = header.Key,
                    Value = header.Value,
                });
        }

        return operations;
    }

    /// <summary>
    /// Port of <c>biz.MergeOverrideHeaders</c>: rename/copy operations are
    /// always appended, other template operations replace the first existing
    /// set/delete with the same path (case-insensitive) or are appended.
    /// </summary>
    public static IReadOnlyList<OverrideOperationDraft> MergeHeaders(
        IReadOnlyList<OverrideOperationDraft>? existing,
        IReadOnlyList<OverrideOperationDraft>? template)
    {
        var result = new List<OverrideOperationDraft>(
            (existing?.Count ?? 0) + (template?.Count ?? 0));

        if (existing is not null)
        {
            result.AddRange(existing);
        }

        if (template is null)
        {
            return result;
        }

        foreach (var operation in template)
        {
            if (operation.Op is OpRename or OpCopy)
            {
                result.Add(operation);
                continue;
            }

            var index = result.FindIndex(item =>
                item.Op is OpSet or OpDelete
                && string.Equals(
                    item.Path ?? string.Empty,
                    operation.Path ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase));

            if (index < 0)
            {
                result.Add(operation);
            }
            else
            {
                result[index] = operation;
            }
        }

        return result;
    }

    /// <summary>
    /// Port of <c>biz.MergeOverrideOperations</c>: template set/set_if_absent/
    /// delete operations replace every existing operation with the same path
    /// (case-sensitive) at the first existing position; rename/copy/array_*
    /// operations are always appended.
    /// </summary>
    public static IReadOnlyList<OverrideOperationDraft> MergeBody(
        IReadOnlyList<OverrideOperationDraft>? existing,
        IReadOnlyList<OverrideOperationDraft>? template)
    {
        var result = new List<OverrideOperationDraft>(
            (existing?.Count ?? 0) + (template?.Count ?? 0));

        var templateOpsByPath = new Dictionary<string, List<OverrideOperationDraft>>(
            StringComparer.Ordinal);
        if (template is not null)
        {
            foreach (var operation in template)
            {
                if (IsReplacingBodyOperation(operation.Op))
                {
                    var path = operation.Path ?? string.Empty;
                    if (!templateOpsByPath.TryGetValue(path, out var operations))
                    {
                        operations = [];
                        templateOpsByPath[path] = operations;
                    }

                    operations.Add(operation);
                }
            }
        }

        var emittedTemplatePaths = new HashSet<string>(StringComparer.Ordinal);
        if (existing is not null)
        {
            foreach (var operation in existing)
            {
                if (IsReplacingBodyOperation(operation.Op)
                    && templateOpsByPath.TryGetValue(
                        operation.Path ?? string.Empty,
                        out var replacements))
                {
                    var path = operation.Path ?? string.Empty;
                    if (emittedTemplatePaths.Add(path))
                    {
                        result.AddRange(replacements);
                    }

                    continue;
                }

                result.Add(operation);
            }
        }

        if (template is not null)
        {
            foreach (var operation in template)
            {
                if (IsReplacingBodyOperation(operation.Op)
                    && emittedTemplatePaths.Contains(operation.Path ?? string.Empty))
                {
                    continue;
                }

                result.Add(operation);
            }
        }

        return result;
    }

    /// <summary>Port of <c>biz.isReplacingBodyOverrideOperation</c>.</summary>
    public static bool IsReplacingBodyOperation(string? op)
        => op is OpSet or OpSetIfAbsent or OpDelete;

    /// <summary>
    /// Port of <c>biz.getHeaderOverrideOperations</c>: the new field wins even
    /// when empty; otherwise the legacy header entries are converted.
    /// </summary>
    public static IReadOnlyList<OverrideOperationDraft>? GetHeaderOverrideOperations(
        ChannelSettingsDraft settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.HeaderOverrideOperations is not null)
        {
            return settings.HeaderOverrideOperations;
        }

        return settings.OverrideHeaders is { Count: > 0 } headers
            ? HeaderEntriesToOperations(headers)
            : null;
    }

    /// <summary>
    /// Port of <c>biz.getBodyOverrideOperations</c>: the new field wins even
    /// when empty; otherwise the legacy override parameters are parsed, and a
    /// parse error is swallowed (the reference logs and uses no operations).
    /// </summary>
    public static IReadOnlyList<OverrideOperationDraft>? GetBodyOverrideOperations(
        ChannelSettingsDraft settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (settings.BodyOverrideOperations is not null)
        {
            return settings.BodyOverrideOperations;
        }

        if (string.IsNullOrEmpty(settings.OverrideParameters))
        {
            return null;
        }

        try
        {
            return ParseOverrideOperations(settings.OverrideParameters);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException)
        {
            return null;
        }
    }

    private static List<OverrideOperationDraft> ParseOperationArray(JsonElement root)
    {
        var operations = new List<OverrideOperationDraft>(root.GetArrayLength());
        foreach (var item in root.EnumerateArray())
        {
            // Go decodes a JSON null array element into the zero struct.
            if (item.ValueKind == JsonValueKind.Null)
            {
                operations.Add(new OverrideOperationDraft());
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    "invalid override operations: json: cannot unmarshal "
                    + GoKindName(item.ValueKind)
                    + " into Go value of type objects.OverrideOperation");
            }

            operations.Add(ReadOperation(item));
        }

        return operations;
    }

    private static OverrideOperationDraft ReadOperation(JsonElement item)
    {
        var operation = new OverrideOperationDraft
        {
            Op = ReadString(item, "op", "OverrideOperation.op") ?? string.Empty,
            Path = ReadString(item, "path", "OverrideOperation.path"),
            From = ReadString(item, "from", "OverrideOperation.from"),
            To = ReadString(item, "to", "OverrideOperation.to"),
            Value = ReadString(item, "value", "OverrideOperation.value"),
            Condition = ReadString(item, "condition", "OverrideOperation.condition"),
        };

        if (TryGetProperty(item, "match", out var match) && match.ValueKind != JsonValueKind.Null)
        {
            if (match.ValueKind != JsonValueKind.Object)
            {
                throw Mismatch(match, "OverrideOperation.match", "objects.OverrideMatch");
            }

            operation.Match = new OverrideMatchDraft
            {
                Path = ReadString(match, "path", "OverrideMatch.path") ?? string.Empty,
                Eq = ReadString(match, "eq", "OverrideMatch.eq") ?? string.Empty,
            };
        }

        if (TryGetProperty(item, "index", out var index) && index.ValueKind != JsonValueKind.Null)
        {
            // encoding/json only accepts an integer literal for *int, and Go's
            // int is 64-bit on the reference platform.
            var raw = index.GetRawText();
            if (index.ValueKind != JsonValueKind.Number
                || raw.AsSpan().IndexOfAny('.', 'e', 'E') >= 0
                || !index.TryGetInt64(out var parsed))
            {
                throw Mismatch(index, "OverrideOperation.index", "int");
            }

            operation.Index = parsed;
        }

        if (TryGetProperty(item, "splat", out var splat) && splat.ValueKind != JsonValueKind.Null)
        {
            if (splat.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw Mismatch(splat, "OverrideOperation.splat", "bool");
            }

            operation.Splat = splat.ValueKind == JsonValueKind.True;
        }

        return operation;
    }

    private static string? ReadString(JsonElement element, string name, string goField)
    {
        if (!TryGetProperty(element, name, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            // Go's zero value is ""; the draft keeps null, which marshals with
            // the same omitempty behaviour.
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw Mismatch(property, goField, "string");
        }

        return property.GetString();
    }

    private static InvalidOperationException Mismatch(
        JsonElement value,
        string goField,
        string goType)
        => new(
            "invalid override operations: json: cannot unmarshal "
            + GoKindName(value.ValueKind)
            + " into Go struct field " + goField + " of type " + goType);

    /// <summary>
    /// Go <c>encoding/json</c> matches object keys to struct fields exactly
    /// first and case-insensitively second; a duplicate key keeps the last
    /// value.
    /// </summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }

        return found;
    }

    /// <summary>Port of <c>fmt.Sprintf("%v", value)</c> for values decoded from JSON.</summary>
    private static string GoSprint(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => GoNumbers.FormatG(value.GetDouble()),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "<nil>",
        JsonValueKind.Array => "[" + string.Join(
            " ",
            value.EnumerateArray().Select(GoSprint)) + "]",
        JsonValueKind.Object => "map[" + string.Join(
            " ",
            value.EnumerateObject()
                .OrderBy(static property => property.Name, StringComparer.Ordinal)
                .Select(static property => property.Name + ":" + GoSprint(property.Value))) + "]",
        _ => string.Empty,
    };

    private static string GoKindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Number => "number",
        JsonValueKind.Object => "object",
        _ => "null",
    };
}
