using System.Globalization;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Safeguards;

/// <summary>The shape of a JSON document root, mirroring Go's decoder outcomes.</summary>
internal enum SafeguardsRootKind
{
    /// <summary>The bytes are not valid JSON.</summary>
    Invalid,

    /// <summary>The document is the JSON literal <c>null</c>.</summary>
    Null,

    /// <summary>The document is an object (properties are available).</summary>
    Object,

    /// <summary>The document is valid JSON but not an object or null.</summary>
    Other,
}

/// <summary>
/// Go-compatible JSON fragments for the reviewer protocol: raw messages are
/// carried as UTF-8 bytes, map keys are emitted in ordinal order and every raw
/// value is compacted with HTML escaping exactly like <c>encoding/json</c>.
/// </summary>
internal static class SafeguardsJson
{
    /// <summary>
    /// Parses UTF-8 JSON, repairing ill-formed bytes and lone surrogate escapes
    /// inside string literals the way Go's decoder does (see
    /// <see cref="GoJsonUtf8"/>; residual difference C008).
    /// </summary>
    public static JsonDocument ParseDocument(ReadOnlySpan<byte> json)
    {
        var sanitized = GoJsonUtf8.SanitizeStringLiterals(json);
        return JsonDocument.Parse(sanitized ?? json.ToArray());
    }

    /// <summary>
    /// Decodes a document root. Struct-shaped callers pass an
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> comparer because Go's
    /// decoder matches struct fields case-insensitively; map-shaped callers use
    /// <see cref="StringComparer.Ordinal"/>.
    /// </summary>
    public static SafeguardsRootKind ReadRoot(
        ReadOnlySpan<byte> json,
        StringComparer comparer,
        out Dictionary<string, byte[]>? fields)
    {
        fields = null;
        if (json.IsEmpty)
        {
            return SafeguardsRootKind.Invalid;
        }

        try
        {
            using var document = ParseDocument(json);
            switch (document.RootElement.ValueKind)
            {
                case JsonValueKind.Null:
                    return SafeguardsRootKind.Null;
                case JsonValueKind.Object:
                    var result = new Dictionary<string, byte[]>(comparer);
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        result[property.Name] = Encoding.UTF8.GetBytes(property.Value.GetRawText());
                    }

                    fields = result;
                    return SafeguardsRootKind.Object;
                default:
                    return SafeguardsRootKind.Other;
            }
        }
        catch (JsonException)
        {
            return SafeguardsRootKind.Invalid;
        }
    }

    /// <summary>
    /// Port of <c>json.Unmarshal(raw, &amp;[]json.RawMessage)</c>: null yields
    /// a nil slice (success with <paramref name="items"/> null), a non-array is
    /// a decode error.
    /// </summary>
    public static bool TryParseArray(ReadOnlySpan<byte> json, out List<byte[]>? items)
    {
        items = null;
        if (json.IsEmpty)
        {
            return false;
        }

        try
        {
            using var document = ParseDocument(json);
            if (document.RootElement.ValueKind == JsonValueKind.Null)
            {
                return true;
            }

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var result = new List<byte[]>(document.RootElement.GetArrayLength());
            foreach (var item in document.RootElement.EnumerateArray())
            {
                result.Add(Encoding.UTF8.GetBytes(item.GetRawText()));
            }

            items = result;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Go <c>json.Unmarshal(raw, &amp;string)</c>: null is a no-op.</summary>
    public static bool TryString(byte[]? raw, out string value)
    {
        value = string.Empty;
        if (raw is null || raw.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = ParseDocument(raw);
            switch (document.RootElement.ValueKind)
            {
                case JsonValueKind.Null:
                    return true;
                case JsonValueKind.String:
                    value = document.RootElement.GetString() ?? string.Empty;
                    return true;
                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Go <c>json.Unmarshal(raw, &amp;*bool)</c>: null leaves the pointer nil.</summary>
    public static bool TryNullableBool(byte[]? raw, out bool? value)
    {
        value = null;
        if (raw is null || raw.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = ParseDocument(raw);
            switch (document.RootElement.ValueKind)
            {
                case JsonValueKind.Null:
                    return true;
                case JsonValueKind.True:
                    value = true;
                    return true;
                case JsonValueKind.False:
                    value = false;
                    return true;
                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Go <c>json.Unmarshal(raw, &amp;int)</c>: null is a no-op.</summary>
    public static bool TryInt64(byte[]? raw, out long value)
    {
        value = 0;
        if (raw is null || raw.Length == 0)
        {
            return false;
        }

        try
        {
            using var document = ParseDocument(raw);
            switch (document.RootElement.ValueKind)
            {
                case JsonValueKind.Null:
                    return true;
                case JsonValueKind.Number:
                    return document.RootElement.TryGetInt64(out value);
                default:
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Go struct-field lookup: the decoder accepts an exact tag or a
    /// case-insensitive match and the last matching property wins.
    /// </summary>
    public static bool TryFieldString(
        Dictionary<string, byte[]> fields,
        string name,
        out string value)
    {
        value = string.Empty;
        return !fields.TryGetValue(name, out var raw) || TryString(raw, out value);
    }

    /// <summary>Case-insensitive struct lookup of a nullable boolean field.</summary>
    public static bool TryFieldBool(
        Dictionary<string, byte[]> fields,
        string name,
        out bool? value)
    {
        value = null;
        return !fields.TryGetValue(name, out var raw) || TryNullableBool(raw, out value);
    }

    /// <summary>Case-insensitive struct lookup of an integer field.</summary>
    public static bool TryFieldInt64(
        Dictionary<string, byte[]> fields,
        string name,
        out long value)
    {
        value = 0;
        return !fields.TryGetValue(name, out var raw) || TryInt64(raw, out value);
    }

    /// <summary>
    /// Port of <c>decodeTool</c> (response.go:35-46): accepts only a complete
    /// <c>tool_use</c> block; any field-type error rejects the block.
    /// </summary>
    public static bool TryDecodeTool(byte[]? raw, out SafeguardsToolUse? tool)
    {
        tool = null;
        var kind = ReadRoot(raw, StringComparer.OrdinalIgnoreCase, out var fields);
        if (kind != SafeguardsRootKind.Object || fields is null)
        {
            return false;
        }

        if (!TryFieldString(fields, "type", out var type)
            || !TryFieldString(fields, "id", out var id)
            || !TryFieldString(fields, "name", out var name))
        {
            return false;
        }

        if (!string.Equals(type, "tool_use", StringComparison.Ordinal))
        {
            return false;
        }

        tool = new SafeguardsToolUse
        {
            Id = id,
            Name = name,
            Input = fields.TryGetValue("input", out var input) ? input : null,
        };
        return true;
    }

    /// <summary>
    /// Port of the per-tool verdict unmarshal (evaluator.go:269-275): a JSON
    /// object with an optional boolean <c>flagged</c> and optional string
    /// <c>explanation</c>; null decodes to the zero struct.
    /// </summary>
    public static bool TryDecodeVerdictValue(
        byte[] raw,
        out bool? flagged,
        out string explanation)
    {
        flagged = null;
        explanation = string.Empty;
        var kind = ReadRoot(raw, StringComparer.OrdinalIgnoreCase, out var fields);
        if (kind == SafeguardsRootKind.Null)
        {
            return true;
        }

        if (kind != SafeguardsRootKind.Object || fields is null)
        {
            return false;
        }

        return TryFieldBool(fields, "flagged", out flagged)
            && TryFieldString(fields, "explanation", out explanation);
    }

    /// <summary>
    /// Port of <c>mergeResults</c> (response.go:13-31): drops decodable
    /// upstream <c>dangerous_tool_use</c> entries, keeps every other decodable
    /// entry and appends the local results. A nil result list strips the
    /// upstream verdicts only.
    /// </summary>
    public static byte[] MergeResults(byte[]? raw, IReadOnlyList<SafeguardsResult>? results)
    {
        var kept = new List<byte[]>();
        if (raw is { Length: > 0 }
            && TryParseArray(raw, out var existing)
            && existing is not null)
        {
            foreach (var item in existing)
            {
                var kind = ReadRoot(item, StringComparer.OrdinalIgnoreCase, out var fields);
                switch (kind)
                {
                    case SafeguardsRootKind.Null:
                        // json.Unmarshal("null", &struct) is a no-op; Type "".
                        kept.Add(item);
                        continue;
                    case SafeguardsRootKind.Object when fields is not null:
                        if (!TryFieldString(fields, "type", out var type))
                        {
                            continue;
                        }

                        if (!string.Equals(type, SafeguardsLiterals.DangerousToolUse, StringComparison.Ordinal))
                        {
                            kept.Add(item);
                        }

                        continue;
                    default:
                        // A scalar or malformed value cannot decode into the
                        // entry struct and is dropped.
                        continue;
                }
            }
        }

        if (results is not null)
        {
            foreach (var result in results)
            {
                kept.Add(MarshalResult(result));
            }
        }

        return MarshalRawArray(kept);
    }

    /// <summary>
    /// Port of <c>stripUpstreamResults</c> (response.go:228-240): remove the
    /// upstream's dangerous-tool-use authority while retaining other extensions.
    /// Returns whether the field existed.
    /// </summary>
    public static bool StripUpstreamResults(Dictionary<string, byte[]> fields)
    {
        if (!fields.TryGetValue("safeguard_results", out var raw))
        {
            return false;
        }

        var merged = MergeResults(raw, null);
        if (merged.Length == 2 && merged[0] == (byte)'[' && merged[1] == (byte)']')
        {
            fields.Remove("safeguard_results");
        }
        else
        {
            fields["safeguard_results"] = merged;
        }

        return true;
    }

    /// <summary>
    /// Go <c>json.Marshal(map[string]json.RawMessage)</c>: ordinal key order,
    /// raw values compacted and HTML-escaped. A nil raw message is represented
    /// by the literal <c>null</c> bytes at the call site.
    /// </summary>
    public static byte[] MarshalRawMap(IEnumerable<KeyValuePair<string, byte[]>> fields)
    {
        var output = new List<byte> { (byte)'{' };
        var first = true;
        foreach (var pair in fields.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                output.Add((byte)',');
            }

            first = false;
            output.AddRange(GoJson.Marshal(pair.Key));
            output.Add((byte)':');
            AppendRaw(output, pair.Value);
        }

        output.Add((byte)'}');
        return [.. output];
    }

    /// <summary>Go <c>json.Marshal([]json.RawMessage)</c>.</summary>
    public static byte[] MarshalRawArray(IReadOnlyList<byte[]> items)
    {
        var output = new List<byte> { (byte)'[' };
        for (var i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                output.Add((byte)',');
            }

            AppendRaw(output, items[i]);
        }

        output.Add((byte)']');
        return [.. output];
    }

    /// <summary>Go <c>json.Marshal(map[string]string)</c> for one entry.</summary>
    public static string MarshalStringMap(string key, string value)
    {
        var output = new List<byte> { (byte)'{' };
        output.AddRange(GoJson.Marshal(key));
        output.Add((byte)':');
        output.AddRange(GoJson.Marshal(value));
        output.Add((byte)'}');
        return Encoding.UTF8.GetString(output.ToArray());
    }

    /// <summary>Port of the verdict struct's JSON shape (evaluator.go:84-89).</summary>
    public static byte[] MarshalVerdict(SafeguardsVerdict verdict)
    {
        var output = new List<byte> { (byte)'{' };
        AppendStringField(output, "type", verdict.Type, first: true);
        AppendStringField(output, "outcome", verdict.Outcome, first: false);
        AppendStringField(output, "explanation", verdict.Explanation, first: false);
        AppendStringField(output, "reason", verdict.Reason, first: false);
        output.Add((byte)'}');
        return [.. output];
    }

    /// <summary>
    /// Port of <c>json.Marshal(Result)</c>: declaration field order and a
    /// sorted <c>tool_uses</c> map.
    /// </summary>
    public static byte[] MarshalResult(SafeguardsResult result)
    {
        var toolUses = new List<byte> { (byte)'{' };
        var first = true;
        foreach (var pair in result.Status.ToolUses.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!first)
            {
                toolUses.Add((byte)',');
            }

            first = false;
            toolUses.AddRange(GoJson.Marshal(pair.Key));
            toolUses.Add((byte)':');
            toolUses.AddRange(MarshalVerdict(pair.Value));
        }

        toolUses.Add((byte)'}');

        var output = new List<byte> { (byte)'{' };
        AppendStringField(output, "type", result.Type, first: true);
        output.Add((byte)',');
        output.AddRange(GoJson.Marshal("status"));
        output.Add((byte)':');
        var status = new List<byte> { (byte)'{' };
        AppendStringField(status, "type", result.Status.Type, first: true);
        status.Add((byte)',');
        status.AddRange(GoJson.Marshal("tool_uses"));
        status.Add((byte)':');
        status.AddRange(toolUses);
        status.Add((byte)'}');
        output.AddRange(status);
        output.Add((byte)'}');
        return [.. output];
    }

    /// <summary>
    /// Port of <c>uniqueJSON</c> (evaluator.go:309-346): reject duplicate keys
    /// anywhere in a valid document.
    /// </summary>
    public static bool HasUniqueKeys(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty)
        {
            return false;
        }

        try
        {
            using var document = ParseDocument(json);
            return HasUniqueKeys(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Go <c>json.Valid</c>, the syntax gate used before key checks.</summary>
    public static bool IsValid(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty)
        {
            return false;
        }

        try
        {
            using var _ = ParseDocument(json);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Port of <c>bytes.TrimSpace</c>: trims the runes <c>unicode.IsSpace</c>
    /// reports from both ends of a UTF-8 fragment. Ill-formed bytes are not
    /// whitespace, so they terminate the scan like any other rune.
    /// </summary>
    public static ReadOnlySpan<byte> TrimSpace(ReadOnlySpan<byte> input)
    {
        var start = 0;
        while (start < input.Length)
        {
            if (Rune.DecodeFromUtf8(input[start..], out var rune, out var consumed)
                != System.Buffers.OperationStatus.Done
                || !IsGoSpace(rune.Value))
            {
                break;
            }

            start += consumed;
        }

        var end = input.Length;
        while (end > start)
        {
            var begin = end - 1;
            while (begin > start && (input[begin] & 0xC0) == 0x80)
            {
                begin--;
            }

            if (Rune.DecodeFromUtf8(input[begin..end], out var rune, out var consumed)
                != System.Buffers.OperationStatus.Done
                || begin + consumed != end
                || !IsGoSpace(rune.Value))
            {
                break;
            }

            end = begin;
        }

        return input[start..end];
    }

    /// <summary>Go unicode.IsSpace: White_Space plus the ASCII controls.</summary>
    private static bool IsGoSpace(int rune)
        => rune is '\t' or '\n' or '\v' or '\f' or '\r' or ' '
            or 0x85 or 0xA0 or 0x1680 or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000
            || rune is >= 0x2000 and <= 0x200A;

    /// <summary>Go <c>%q</c> for the ASCII-ish interpreter strings in errors.</summary>
    public static string GoQuote(string value)
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
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (rune.Value < 0x20 || rune.Value == 0x7f)
                    {
                        builder.Append("\\x")
                            .Append(rune.Value.ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(rune.ToString());
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static bool HasUniqueKeys(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name) || !HasUniqueKeys(property.Value))
                    {
                        return false;
                    }
                }

                return true;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (!HasUniqueKeys(item))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return true;
        }
    }

    private static void AppendRaw(List<byte> output, byte[]? raw)
    {
        if (raw is null)
        {
            output.AddRange("null"u8);
            return;
        }

        output.AddRange(GoJson.Compact(raw));
    }

    private static void AppendStringField(List<byte> output, string name, string value, bool first)
    {
        if (!first)
        {
            output.Add((byte)',');
        }

        output.AddRange(GoJson.Marshal(name));
        output.Add((byte)':');
        output.AddRange(GoJson.Marshal(value));
    }
}
