using System.Buffers;
using System.Globalization;
using System.Text;
using AxonHub.Abstractions;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// A raw-preserving JSON document with the subset of gjson/sjson path semantics
/// the channel override operations rely on
/// (internal/server/orchestrator/override.go).
/// </summary>
/// <remarks>
/// The reference edits the serialized body in place with sjson, which splices
/// the original bytes and only rewrites the touched subtree. This port models
/// the document as a tree that keeps every scalar token and object key exactly
/// as it appeared, then re-serializes compactly. For the compact bodies the
/// transformers produce (json.Marshal output) both approaches emit identical
/// bytes; input whitespace inside untouched parts is not preserved (difference
/// C039).
/// <para>
/// Path syntax follows sjson: parts split on unescaped dots, a backslash
/// escapes the next character, and a leading colon on a part forces the part to
/// be treated as an object key. Wildcard paths (<c>* ? # @ |</c>) take the
/// reference's complex-path branch and are reported as unsupported.
/// </para>
/// </remarks>
internal static class OverrideJson
{
    public static bool IsValid(ReadOnlySpan<byte> utf8)
        => OverrideJsonParser.TryParse(utf8, out _);

    public static bool TryParse(ReadOnlySpan<byte> utf8, out OverrideJsonNode? node)
        => OverrideJsonParser.TryParse(utf8, out node);

    /// <summary>Resolves a path against the document (gjson.GetBytes).</summary>
    public static JsonLookup Get(ReadOnlySpan<byte> utf8, string path)
    {
        if (!OverrideJsonParser.TryParse(utf8, out var root) || root is null)
        {
            return default;
        }

        if (path.Length == 0)
        {
            return default;
        }

        if (!OverridePath.TryParse(path, out var parts))
        {
            // Wildcard paths need gjson's query engine, which is not ported.
            return default;
        }

        var node = Navigate(root, parts);
        return node is null ? default : new JsonLookup(node);
    }

    /// <summary>
    /// Port of sjson.SetBytes. On success <paramref name="result"/> is the new
    /// body; on failure the caller keeps the previous body.
    /// </summary>
    public static bool TrySet(ReadOnlySpan<byte> utf8, string path, GoValue value, out byte[] result, out string? error)
    {
        var node = OverrideJsonNode.FromGoValue(value);
        return TrySetNode(utf8, path, node, out result, out error);
    }

    /// <summary>Port of sjson.SetBytes with a pre-built JSON value.</summary>
    public static bool TrySetNode(
        ReadOnlySpan<byte> utf8,
        string path,
        OverrideJsonNode value,
        out byte[] result,
        out string? error)
    {
        result = [];
        error = null;

        if (path.Length == 0)
        {
            error = "path cannot be empty";
            return false;
        }

        if (!OverridePath.TryParse(path, out var parts))
        {
            error = "complex path is not supported";
            return false;
        }

        if (!OverrideJsonParser.TryParse(utf8, out var root) || root is null)
        {
            error = "body is not valid JSON";
            return false;
        }

        if (!TrySetIn(root, parts, 0, value, out var updated, out error))
        {
            return false;
        }

        result = Encoding.UTF8.GetBytes(updated.Raw);
        return true;
    }

    /// <summary>
    /// Port of sjson.DeleteBytes. A missing path is a successful no-op and keeps
    /// the original bytes.
    /// </summary>
    public static bool TryDelete(ReadOnlySpan<byte> utf8, string path, out byte[] result, out string? error)
    {
        result = [];
        error = null;

        if (path.Length == 0)
        {
            error = "path cannot be empty";
            return false;
        }

        if (!OverridePath.TryParse(path, out var parts))
        {
            error = "complex path is not supported";
            return false;
        }

        if (!OverrideJsonParser.TryParse(utf8, out var root) || root is null)
        {
            error = "body is not valid JSON";
            return false;
        }

        if (!TryDeleteIn(root, parts, 0, out var updated, out var changed))
        {
            // sjson never reports an error for a simple-path delete; treat any
            // structural failure like a no-op instead of losing the body.
            error = "delete failed";
            return false;
        }

        result = changed ? Encoding.UTF8.GetBytes(updated.Raw) : utf8.ToArray();
        return true;
    }

    private static OverrideJsonNode? Navigate(OverrideJsonNode root, List<OverridePathPart> parts)
    {
        var current = root;
        for (var index = 0; index < parts.Count; index++)
        {
            switch (current)
            {
                case OverrideJsonObject obj:
                    var member = obj.FindMember(parts[index].Part);
                    if (member is null)
                    {
                        return null;
                    }

                    current = member.Value;
                    break;

                case OverrideJsonArray array:
                    if (!TryParseArrayIndex(parts[index].Part, out var arrayIndex)
                        || arrayIndex < 0
                        || arrayIndex >= array.Items.Count)
                    {
                        return null;
                    }

                    current = array.Items[(int)arrayIndex];
                    break;

                default:
                    return null;
            }
        }

        return current;
    }

    private static bool TrySetIn(
        OverrideJsonNode current,
        List<OverridePathPart> parts,
        int depth,
        OverrideJsonNode value,
        out OverrideJsonNode updated,
        out string? error)
    {
        updated = current;
        error = null;

        var part = parts[depth];
        if (Find(current, part.Part) is { } found)
        {
            if (depth + 1 < parts.Count)
            {
                if (!TrySetIn(found.Value, parts, depth + 1, value, out var childUpdated, out error))
                {
                    return false;
                }

                updated = Replace(current, found, childUpdated);
                return true;
            }

            updated = Replace(current, found, value);
            return true;
        }

        // Port of appendRawPaths' "not found" branch.
        var numeric = OverridePath.IsNumeric(part);
        if (current is not (OverrideJsonObject or OverrideJsonArray))
        {
            current = numeric ? new OverrideJsonArray([]) : new OverrideJsonObject([]);
        }

        switch (current)
        {
            case OverrideJsonObject obj:
            {
                var childValue = depth + 1 < parts.Count
                    ? Build(parts, depth, value)
                    : value;
                obj.Members.Add(new OverrideJsonMember
                {
                    KeyRaw = GoJson.Quote(part.Part),
                    Key = part.Part,
                    Value = childValue,
                });
                updated = obj;
                return true;
            }

            case OverrideJsonArray array:
            {
                if (!numeric && !(!part.Force && part.Part == "-1"))
                {
                    error = $"cannot set array element for non-numeric key '{part.Part}'";
                    return false;
                }

                if (numeric)
                {
                    if (!TryParseArrayIndex(part.Part, out var target)
                        || target < 0
                        || target > int.MaxValue)
                    {
                        error = $"invalid array index '{part.Part}'";
                        return false;
                    }

                    // sjson pads with nulls up to the requested index; when the
                    // index is below the length (only reachable through a weird
                    // lookup) the value is appended.
                    while (array.Items.Count < target)
                    {
                        array.Items.Add(OverrideJsonScalar.Null);
                    }
                }

                array.Items.Add(depth + 1 < parts.Count ? Build(parts, depth, value) : value);
                updated = array;
                return true;
            }

            default:
                error = "json must be an object or array";
                return false;
        }
    }

    /// <summary>
    /// Port of sjson's appendBuild: materializes the missing remainder of a path,
    /// wrapping numeric parts in arrays padded with nulls and other parts in
    /// objects. <paramref name="depth"/> indexes the key that is about to be
    /// written by the caller.
    /// </summary>
    private static OverrideJsonNode Build(
        List<OverridePathPart> parts,
        int depth,
        OverrideJsonNode value)
    {
        if (depth + 1 >= parts.Count)
        {
            return value;
        }

        var next = parts[depth + 1];
        if (OverridePath.IsNumeric(next) || (!next.Force && next.Part == "-1"))
        {
            var count = OverridePath.IsNumeric(next) ? OverridePath.ParseNumeric(next) : 0;
            var array = new OverrideJsonArray([]);
            for (var index = 0; index < count; index++)
            {
                array.Items.Add(OverrideJsonScalar.Null);
            }

            array.Items.Add(Build(parts, depth + 1, value));
            return array;
        }

        var obj = new OverrideJsonObject([]);
        obj.Members.Add(new OverrideJsonMember
        {
            KeyRaw = GoJson.Quote(next.Part),
            Key = next.Part,
            Value = Build(parts, depth + 1, value),
        });
        return obj;
    }

    private static bool TryDeleteIn(
        OverrideJsonNode current,
        List<OverridePathPart> parts,
        int depth,
        out OverrideJsonNode updated,
        out bool changed)
    {
        updated = current;
        changed = false;

        var part = parts[depth];

        // sjson.Delete treats a literal "-1" on an array as the last element.
        if (!part.Force && part.Part == "-1" && current is OverrideJsonArray lastArray && lastArray.Items.Count > 0)
        {
            if (depth + 1 < parts.Count)
            {
                if (!TryDeleteIn(
                        lastArray.Items[^1],
                        parts,
                        depth + 1,
                        out var lastUpdated,
                        out changed))
                {
                    return false;
                }

                if (!changed)
                {
                    return true;
                }

                lastArray.Items[^1] = lastUpdated;
                updated = lastArray;
                return true;
            }

            lastArray.Items.RemoveAt(lastArray.Items.Count - 1);
            changed = true;
            return true;
        }

        if (Find(current, part.Part) is not { } found)
        {
            return true;
        }

        if (depth + 1 < parts.Count)
        {
            if (!TryDeleteIn(found.Value, parts, depth + 1, out var childUpdated, out changed))
            {
                return false;
            }

            if (!changed)
            {
                return true;
            }

            updated = Replace(current, found, childUpdated);
            return true;
        }

        switch (current)
        {
            case OverrideJsonObject obj:
                obj.Members.Remove(found.Member!);
                changed = true;
                return true;

            case OverrideJsonArray array:
                array.Items.RemoveAt(found.Index);
                changed = true;
                return true;

            default:
                return true;
        }
    }

    private static Found? Find(OverrideJsonNode current, string key)
    {
        switch (current)
        {
            case OverrideJsonObject obj:
                var member = obj.FindMember(key);
                return member is null ? null : new Found(member.Value, member, -1);

            case OverrideJsonArray array:
                if (!TryParseArrayIndex(key, out var index) || index < 0 || index >= array.Items.Count)
                {
                    return null;
                }

                return new Found(array.Items[(int)index], null, (int)index);

            default:
                return null;
        }
    }

    private static OverrideJsonNode Replace(OverrideJsonNode current, Found found, OverrideJsonNode value)
    {
        switch (current)
        {
            case OverrideJsonObject obj:
                found.Member!.Value = value;
                return obj;

            case OverrideJsonArray array:
                array.Items[found.Index] = value;
                return array;

            default:
                return current;
        }
    }

    private static bool TryParseArrayIndex(string part, out long index)
    {
        index = 0;
        if (part.Length == 0)
        {
            return false;
        }

        foreach (var c in part)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return long.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private readonly record struct Found(OverrideJsonNode Value, OverrideJsonMember? Member, int Index);
}

/// <summary>The result of a path lookup, mirroring gjson.Result's reads.</summary>
internal readonly struct JsonLookup
{
    public JsonLookup(OverrideJsonNode node)
    {
        Node = node;
    }

    public OverrideJsonNode? Node { get; }

    public bool Exists => Node is not null;

    public bool IsArray => Node is OverrideJsonArray;

    /// <summary>gjson.Result.Type == String.</summary>
    public bool IsString => Node is OverrideJsonScalar { Kind: OverrideJsonKind.String };

    /// <summary>gjson.Result.Type == JSON (an object or an array).</summary>
    public bool IsJson => Node is OverrideJsonObject or OverrideJsonArray;

    /// <summary>
    /// gjson.Result.Type == Null: a missing path and an explicit JSON null are
    /// indistinguishable through gjson's type check.
    /// </summary>
    public bool IsNull => Node is null or OverrideJsonScalar { Kind: OverrideJsonKind.Null };

    /// <summary>gjson.Result.Int().</summary>
    public long IntValue => Node switch
    {
        OverrideJsonScalar { Kind: OverrideJsonKind.True } => 1,
        // gjson tries the float64 value first (safeInt), then the raw token.
        OverrideJsonScalar { Kind: OverrideJsonKind.Number } scalar
            => SafeInt(scalar.NumberValue, out var safe)
                ? safe
                : ParseIntPrefix(scalar.Text),
        OverrideJsonScalar { Kind: OverrideJsonKind.String } scalar
            => ParseIntPrefix(scalar.StringValue),
        _ => 0,
    };

    /// <summary>Port of gjson.safeInt: the float64 must be an exact integer.</summary>
    private static bool SafeInt(double value, out long result)
    {
        result = 0;
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }

        if (value < long.MinValue || value > long.MaxValue)
        {
            return false;
        }

        var truncated = (long)value;
        if ((double)truncated != value)
        {
            return false;
        }

        result = truncated;
        return true;
    }

    /// <summary>
    /// Port of gjson.parseInt: an optional leading minus, then digits; parsing
    /// stops at the first non-digit once at least one digit was read.
    /// </summary>
    private static long ParseIntPrefix(string text)
    {
        var index = 0;
        var negative = false;
        if (text.Length > 0 && text[0] == '-')
        {
            negative = true;
            index = 1;
        }

        if (index == text.Length)
        {
            return 0;
        }

        long value = 0;
        var digits = 0;
        for (; index < text.Length; index++)
        {
            var c = text[index];
            if (c is < '0' or > '9')
            {
                if (digits == 0)
                {
                    return 0;
                }

                break;
            }

            if (value > (long.MaxValue - (c - '0')) / 10)
            {
                return 0;
            }

            value = (value * 10) + (c - '0');
            digits++;
        }

        return negative ? -value : value;
    }

    /// <summary>gjson.Result.String().</summary>
    public string StringValue => Node switch
    {
        OverrideJsonScalar { Kind: OverrideJsonKind.String } scalar => scalar.StringValue,
        OverrideJsonScalar { Kind: OverrideJsonKind.Number } scalar => NumberString(scalar),
        OverrideJsonScalar { Kind: OverrideJsonKind.True } => "true",
        OverrideJsonScalar { Kind: OverrideJsonKind.False } => "false",
        OverrideJsonScalar { Kind: OverrideJsonKind.Null } => string.Empty,
        null => string.Empty,
        _ => Node.Raw,
    };

    /// <summary>gjson.Result.Value(): objects/arrays become maps/slices, numbers float64.</summary>
    public GoValue ToGoValue() => Node?.ToGoValue() ?? GoNullValue.Instance;

    private static string NumberString(OverrideJsonScalar scalar)
    {
        var raw = scalar.Text;
        var start = raw.Length > 0 && raw[0] == '-' ? 1 : 0;
        for (var index = start; index < raw.Length; index++)
        {
            if (raw[index] is < '0' or > '9')
            {
                return GoNumbers.FormatF(scalar.NumberValue);
            }
        }

        return raw;
    }
}

/// <summary>Raised when an override operation cannot be applied to a body.</summary>
internal sealed class OverrideOperationException(string message) : Exception(message);

// ---------------------------------------------------------------------------
// JSON tree
// ---------------------------------------------------------------------------

internal enum OverrideJsonKind
{
    Object,
    Array,
    String,
    Number,
    True,
    False,
    Null,
}

internal abstract class OverrideJsonNode
{
    public abstract OverrideJsonKind Kind { get; }

    public string Raw
    {
        get
        {
            var builder = new StringBuilder();
            Write(builder);
            return builder.ToString();
        }
    }

    public abstract GoValue ToGoValue();

    internal abstract void Write(StringBuilder builder);

    /// <summary>Builds a tree node from a GoValue the way sjson stringifies it.</summary>
    public static OverrideJsonNode FromGoValue(GoValue value)
    {
        switch (value)
        {
            case GoNullValue:
                return OverrideJsonScalar.Null;

            case GoBoolValue boolean:
                return OverrideJsonParser.ParseRaw(boolean.WriteJson())!;

            case GoStringValue text:
                return OverrideJsonParser.ParseRaw(SjsonQuoteString(text.Value))!;

            case GoIntValue integer:
                return OverrideJsonParser.ParseRaw(
                    integer.Value.ToString(CultureInfo.InvariantCulture))!;

            case GoFloatValue number:
                return OverrideJsonParser.ParseRaw(GoNumbers.FormatF(number.Value))!;

            default:
                return OverrideJsonParser.ParseRaw(value.WriteJson())!;
        }
    }

    /// <summary>
    /// Port of sjson's appendStringify: a string whose bytes are printable
    /// ASCII and contain neither quote nor backslash is spliced in verbatim
    /// (no HTML escaping), which is what sjson's fast path does; every other
    /// string goes through encoding/json.
    /// </summary>
    private static string SjsonQuoteString(string value)
    {
        foreach (var c in value)
        {
            if (c < ' ' || c > '\u007f' || c == '"' || c == '\\')
            {
                return GoJson.Quote(value);
            }
        }

        return "\"" + value + "\"";
    }
}

internal sealed class OverrideJsonScalar : OverrideJsonNode
{
    private OverrideJsonScalar(OverrideJsonKind kind, string text, string stringValue, double numberValue)
    {
        Kind = kind;
        Text = text;
        StringValue = stringValue;
        NumberValue = numberValue;
    }

    public static OverrideJsonScalar Null { get; } = new(OverrideJsonKind.Null, "null", string.Empty, 0);

    public override OverrideJsonKind Kind { get; }

    /// <summary>The exact source token (including quotes and escapes for strings).</summary>
    public string Text { get; }

    public string StringValue { get; }

    public double NumberValue { get; }

    public static OverrideJsonScalar String(string text, string stringValue)
        => new(OverrideJsonKind.String, text, stringValue, 0);

    public static OverrideJsonScalar Number(string text, double numberValue)
        => new(OverrideJsonKind.Number, text, string.Empty, numberValue);

    public static OverrideJsonScalar Boolean(bool value)
        => new(value ? OverrideJsonKind.True : OverrideJsonKind.False, value ? "true" : "false", string.Empty, 0);

    public override GoValue ToGoValue() => Kind switch
    {
        OverrideJsonKind.String => new GoStringValue(StringValue),
        OverrideJsonKind.Number => new GoFloatValue(NumberValue),
        OverrideJsonKind.True => new GoBoolValue(true),
        OverrideJsonKind.False => new GoBoolValue(false),
        _ => GoNullValue.Instance,
    };

    internal override void Write(StringBuilder builder) => builder.Append(Text);
}

internal sealed class OverrideJsonObject(List<OverrideJsonMember> members) : OverrideJsonNode
{
    public override OverrideJsonKind Kind => OverrideJsonKind.Object;

    public List<OverrideJsonMember> Members { get; } = members;

    /// <summary>gjson matches the first occurrence of a duplicate key.</summary>
    public OverrideJsonMember? FindMember(string key)
    {
        foreach (var member in Members)
        {
            if (string.Equals(member.Key, key, StringComparison.Ordinal))
            {
                return member;
            }
        }

        return null;
    }

    public override GoValue ToGoValue()
    {
        // json.Unmarshal into any keeps the last duplicate key.
        var values = new Dictionary<string, GoValue>(StringComparer.Ordinal);
        foreach (var member in Members)
        {
            values[member.Key] = member.Value.ToGoValue();
        }

        return new GoMapValue(values.Select(pair => new KeyValuePair<string, GoValue>(pair.Key, pair.Value)), sortKeys: true);
    }

    internal override void Write(StringBuilder builder)
    {
        builder.Append('{');
        for (var index = 0; index < Members.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(Members[index].KeyRaw).Append(':');
            Members[index].Value.Write(builder);
        }

        builder.Append('}');
    }
}

internal sealed class OverrideJsonArray(List<OverrideJsonNode> items) : OverrideJsonNode
{
    public override OverrideJsonKind Kind => OverrideJsonKind.Array;

    public List<OverrideJsonNode> Items { get; } = items;

    public override GoValue ToGoValue()
        => new GoSliceValue(Items.Select(item => item.ToGoValue()).ToList());

    internal override void Write(StringBuilder builder)
    {
        builder.Append('[');
        for (var index = 0; index < Items.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            Items[index].Write(builder);
        }

        builder.Append(']');
    }
}

internal sealed class OverrideJsonMember
{
    public required string KeyRaw { get; init; }

    public required string Key { get; init; }

    public required OverrideJsonNode Value { get; set; }
}

/// <summary>A parsed sjson path part.</summary>
internal sealed class OverridePathPart
{
    public required string Part { get; init; }

    public bool Force { get; init; }
}

internal static class OverridePath
{
    /// <summary>
    /// Port of sjson.parsePath's split loop. Returns false when an unescaped
    /// wildcard byte makes the path non-simple.
    /// </summary>
    public static bool TryParse(string path, out List<OverridePathPart> parts)
    {
        parts = [];
        var index = 0;
        var force = false;
        if (path.Length > 0 && path[0] == ':')
        {
            force = true;
            index = 1;
        }

        var part = new StringBuilder();
        while (index < path.Length)
        {
            var c = path[index];
            if (c is '|' or '#' or '@' or '*' or '?')
            {
                return false;
            }

            if (c == '.')
            {
                parts.Add(new OverridePathPart { Part = part.ToString(), Force = force });
                part.Clear();
                force = false;
                index++;
                if (index < path.Length && path[index] == ':')
                {
                    force = true;
                    index++;
                }

                continue;
            }

            if (c == '\\')
            {
                index++;
                if (index < path.Length)
                {
                    part.Append(path[index]);
                    index++;
                }

                continue;
            }

            part.Append(c);
            index++;
        }

        parts.Add(new OverridePathPart { Part = part.ToString(), Force = force });
        return true;
    }

    /// <summary>Port of sjson.atoui: empty parts are numeric index 0; forced parts never are.</summary>
    public static bool IsNumeric(OverridePathPart part)
    {
        if (part.Force)
        {
            return false;
        }

        foreach (var c in part.Part)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    public static long ParseNumeric(OverridePathPart part)
    {
        if (part.Part.Length == 0)
        {
            return 0;
        }

        return long.TryParse(part.Part, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new OverrideOperationException($"invalid numeric path part '{part.Part}'");
    }
}

// ---------------------------------------------------------------------------
// Parser
// ---------------------------------------------------------------------------

internal ref struct OverrideJsonParser
{
    private readonly ReadOnlySpan<byte> _json;
    private int _pos;

    private OverrideJsonParser(ReadOnlySpan<byte> json)
    {
        _json = json;
        _pos = 0;
    }

    public static bool TryParse(ReadOnlySpan<byte> utf8, out OverrideJsonNode? node)
    {
        var parser = new OverrideJsonParser(utf8);
        node = null;
        parser.SkipWhitespace();
        if (!parser.TryReadValue(out var value))
        {
            return false;
        }

        parser.SkipWhitespace();
        if (parser._pos != utf8.Length)
        {
            return false;
        }

        node = value;
        return true;
    }

    /// <summary>Parses a complete raw JSON token or document; used for marshalled values.</summary>
    public static OverrideJsonNode? ParseRaw(string json)
    {
        if (!TryParse(Encoding.UTF8.GetBytes(json), out var node))
        {
            throw new OverrideOperationException("marshalled value is not valid JSON");
        }

        return node;
    }

    private bool TryReadValue(out OverrideJsonNode? node)
    {
        node = null;
        if (_pos >= _json.Length)
        {
            return false;
        }

        switch (_json[_pos])
        {
            case (byte)'{':
                return TryReadObject(out node);

            case (byte)'[':
                return TryReadArray(out node);

            case (byte)'"':
                if (!TryReadStringToken(out var raw, out var decoded))
                {
                    return false;
                }

                node = OverrideJsonScalar.String(raw, decoded);
                return true;

            case (byte)'t':
                if (!TryConsume("true"))
                {
                    return false;
                }

                node = OverrideJsonScalar.Boolean(true);
                return true;

            case (byte)'f':
                if (!TryConsume("false"))
                {
                    return false;
                }

                node = OverrideJsonScalar.Boolean(false);
                return true;

            case (byte)'n':
                if (!TryConsume("null"))
                {
                    return false;
                }

                node = OverrideJsonScalar.Null;
                return true;

            default:
                return TryReadNumber(out node);
        }
    }

    private bool TryReadObject(out OverrideJsonNode? node)
    {
        node = null;
        _pos++; // '{'
        var members = new List<OverrideJsonMember>();
        SkipWhitespace();
        if (_pos < _json.Length && _json[_pos] == '}')
        {
            _pos++;
            node = new OverrideJsonObject(members);
            return true;
        }

        while (true)
        {
            SkipWhitespace();
            if (_pos >= _json.Length || _json[_pos] != '"' || !TryReadStringToken(out var keyRaw, out var key))
            {
                return false;
            }

            SkipWhitespace();
            if (_pos >= _json.Length || _json[_pos] != ':')
            {
                return false;
            }

            _pos++;
            SkipWhitespace();
            if (!TryReadValue(out var value) || value is null)
            {
                return false;
            }

            members.Add(new OverrideJsonMember { KeyRaw = keyRaw, Key = key, Value = value });
            SkipWhitespace();
            if (_pos >= _json.Length)
            {
                return false;
            }

            if (_json[_pos] == ',')
            {
                _pos++;
                continue;
            }

            if (_json[_pos] == '}')
            {
                _pos++;
                node = new OverrideJsonObject(members);
                return true;
            }

            return false;
        }
    }

    private bool TryReadArray(out OverrideJsonNode? node)
    {
        node = null;
        _pos++; // '['
        var items = new List<OverrideJsonNode>();
        SkipWhitespace();
        if (_pos < _json.Length && _json[_pos] == ']')
        {
            _pos++;
            node = new OverrideJsonArray(items);
            return true;
        }

        while (true)
        {
            SkipWhitespace();
            if (!TryReadValue(out var value) || value is null)
            {
                return false;
            }

            items.Add(value);
            SkipWhitespace();
            if (_pos >= _json.Length)
            {
                return false;
            }

            if (_json[_pos] == ',')
            {
                _pos++;
                continue;
            }

            if (_json[_pos] == ']')
            {
                _pos++;
                node = new OverrideJsonArray(items);
                return true;
            }

            return false;
        }
    }

    private bool TryReadNumber(out OverrideJsonNode? node)
    {
        node = null;
        var start = _pos;
        if (_pos < _json.Length && _json[_pos] == '-')
        {
            _pos++;
        }

        if (_pos >= _json.Length)
        {
            return false;
        }

        if (_json[_pos] == '0')
        {
            _pos++;
        }
        else if (_json[_pos] is >= (byte)'1' and <= (byte)'9')
        {
            _pos++;
            while (_pos < _json.Length && _json[_pos] is >= (byte)'0' and <= (byte)'9')
            {
                _pos++;
            }
        }
        else
        {
            return false;
        }

        if (_pos < _json.Length && _json[_pos] == '.')
        {
            _pos++;
            if (_pos >= _json.Length || _json[_pos] is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            while (_pos < _json.Length && _json[_pos] is >= (byte)'0' and <= (byte)'9')
            {
                _pos++;
            }
        }

        if (_pos < _json.Length && _json[_pos] is (byte)'e' or (byte)'E')
        {
            _pos++;
            if (_pos < _json.Length && _json[_pos] is (byte)'+' or (byte)'-')
            {
                _pos++;
            }

            if (_pos >= _json.Length || _json[_pos] is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            while (_pos < _json.Length && _json[_pos] is >= (byte)'0' and <= (byte)'9')
            {
                _pos++;
            }
        }

        var raw = Encoding.ASCII.GetString(_json[start.._pos]);
        node = OverrideJsonScalar.Number(
            raw,
            double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture));
        return true;
    }

    private bool TryReadStringToken(out string raw, out string decoded)
    {
        raw = string.Empty;
        decoded = string.Empty;
        if (_pos >= _json.Length || _json[_pos] != '"')
        {
            return false;
        }

        var start = _pos;
        _pos++;
        var builder = new StringBuilder();
        while (_pos < _json.Length)
        {
            var b = _json[_pos];
            if (b == '"')
            {
                _pos++;
                raw = Encoding.UTF8.GetString(_json[start.._pos]);
                decoded = builder.ToString();
                return true;
            }

            if (b < 0x20)
            {
                return false;
            }

            if (b == '\\')
            {
                _pos++;
                if (!TryReadEscape(builder))
                {
                    return false;
                }

                continue;
            }

            var status = Rune.DecodeFromUtf8(_json[_pos..], out var rune, out var consumed);
            if (status != OperationStatus.Done)
            {
                builder.Append('�');
                _pos++;
            }
            else
            {
                builder.Append(rune.ToString());
                _pos += consumed;
            }
        }

        return false;
    }

    private bool TryReadEscape(StringBuilder builder)
    {
        if (_pos >= _json.Length)
        {
            return false;
        }

        var c = (char)_json[_pos++];
        switch (c)
        {
            case '"':
            case '\\':
            case '/':
                builder.Append(c);
                return true;
            case 'b':
                builder.Append('\b');
                return true;
            case 'f':
                builder.Append('\f');
                return true;
            case 'n':
                builder.Append('\n');
                return true;
            case 'r':
                builder.Append('\r');
                return true;
            case 't':
                builder.Append('\t');
                return true;
            case 'u':
                if (!TryReadHex4(out var code))
                {
                    return false;
                }

                if (code is >= 0xD800 and <= 0xDBFF)
                {
                    // A high surrogate is only valid when followed by an
                    // escaped low surrogate; Go unquotes lone surrogates to
                    // U+FFFD.
                    if (_pos + 5 < _json.Length
                        && _json[_pos] == '\\'
                        && _json[_pos + 1] == 'u'
                        && IsHex(_json[_pos + 2])
                        && IsHex(_json[_pos + 3])
                        && IsHex(_json[_pos + 4])
                        && IsHex(_json[_pos + 5]))
                    {
                        var low = (char)ParseHex4(_json.Slice(_pos + 2, 4));
                        if (low is >= '\uDC00' and <= '\uDFFF')
                        {
                            _pos += 6;
                            builder.Append((char)code).Append(low);
                            return true;
                        }
                    }

                    builder.Append('�');
                    return true;
                }

                if (code is >= 0xDC00 and <= 0xDFFF)
                {
                    builder.Append('�');
                    return true;
                }

                builder.Append((char)code);
                return true;
            default:
                return false;
        }
    }

    private bool TryReadHex4(out int code)
    {
        code = 0;
        if (_pos + 4 > _json.Length)
        {
            return false;
        }

        for (var index = 0; index < 4; index++)
        {
            if (!IsHex(_json[_pos + index]))
            {
                return false;
            }
        }

        code = ParseHex4(_json.Slice(_pos, 4));
        _pos += 4;
        return true;
    }

    private static int ParseHex4(ReadOnlySpan<byte> span)
    {
        var value = 0;
        for (var index = 0; index < 4; index++)
        {
            var c = span[index];
            var digit = c switch
            {
                >= (byte)'0' and <= (byte)'9' => c - '0',
                >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                _ => c - 'A' + 10,
            };
            value = (value << 4) | digit;
        }

        return value;
    }

    private static bool IsHex(byte c)
        => c is (>= (byte)'0' and <= (byte)'9')
            or (>= (byte)'a' and <= (byte)'f')
            or (>= (byte)'A' and <= (byte)'F');

    private bool TryConsume(string literal)
    {
        if (_pos + literal.Length > _json.Length)
        {
            return false;
        }

        for (var index = 0; index < literal.Length; index++)
        {
            if (_json[_pos + index] != literal[index])
            {
                return false;
            }
        }

        _pos += literal.Length;
        return true;
    }

    private void SkipWhitespace()
    {
        while (_pos < _json.Length && _json[_pos] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
        {
            _pos++;
        }
    }
}

// ---------------------------------------------------------------------------
// Go values and encoding/json-compatible marshalling
// ---------------------------------------------------------------------------

internal abstract class GoValue
{
    public abstract string WriteJson();

    public override string ToString() => WriteJson();
}

internal sealed class GoNullValue : GoValue
{
    public static GoNullValue Instance { get; } = new();

    public override string WriteJson() => "null";
}

internal sealed class GoBoolValue(bool value) : GoValue
{
    public bool Value { get; } = value;

    public override string WriteJson() => Value ? "true" : "false";
}

internal sealed class GoStringValue(string value) : GoValue
{
    public string Value { get; } = value;

    public override string WriteJson() => GoJson.Quote(Value);
}

internal sealed class GoIntValue(long value) : GoValue
{
    public long Value { get; } = value;

    public override string WriteJson() => Value.ToString(CultureInfo.InvariantCulture);
}

internal sealed class GoFloatValue(double value) : GoValue
{
    public double Value { get; } = value;

    public override string WriteJson() => GoNumbers.FormatJson(Value);
}

internal sealed class GoSliceValue(List<GoValue> items) : GoValue
{
    public List<GoValue> Items { get; } = items;

    public override string WriteJson()
    {
        var builder = new StringBuilder();
        builder.Append('[');
        for (var index = 0; index < Items.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            builder.Append(Items[index].WriteJson());
        }

        builder.Append(']');
        return builder.ToString();
    }
}

/// <summary>
/// A Go map for marshalling. <c>encoding/json</c> sorts map keys but preserves
/// struct field order; both shapes use this type.
/// </summary>
internal sealed class GoMapValue : GoValue
{
    private readonly List<KeyValuePair<string, GoValue>> _members;
    private readonly bool _sortKeys;

    public GoMapValue(IEnumerable<KeyValuePair<string, GoValue>> members, bool sortKeys)
    {
        _members = [.. members];
        _sortKeys = sortKeys;
    }

    public bool TryGet(string key, out GoValue value)
    {
        for (var index = _members.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_members[index].Key, key, StringComparison.Ordinal))
            {
                value = _members[index].Value;
                return true;
            }
        }

        value = GoNullValue.Instance;
        return false;
    }

    public IEnumerable<KeyValuePair<string, GoValue>> Members => _members;

    public override string WriteJson()
    {
        IEnumerable<KeyValuePair<string, GoValue>> members = _members;
        if (_sortKeys)
        {
            members = members.OrderBy(static pair => pair.Key, StringComparer.Ordinal);
        }

        var builder = new StringBuilder();
        builder.Append('{');
        var first = true;
        foreach (var (key, value) in members)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append(GoJson.Quote(key)).Append(':').Append(value.WriteJson());
        }

        builder.Append('}');
        return builder.ToString();
    }
}

/// <summary>Go encoding/json string and value encoding.</summary>
internal static class GoJson
{
    private const string HexDigits = "0123456789abcdef";

    /// <summary>Port of json.Marshal(string): HTML-safe escaping, UTF-8 output.</summary>
    public static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        WriteQuoted(builder, value);
        return builder.ToString();
    }

    public static void WriteQuoted(StringBuilder builder, string value)
    {
        builder.Append('"');
        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    continue;
                case '\\':
                    builder.Append("\\\\");
                    continue;
                case '\n':
                    builder.Append("\\n");
                    continue;
                case '\r':
                    builder.Append("\\r");
                    continue;
                case '\t':
                    builder.Append("\\t");
                    continue;
                case '<':
                    builder.Append("\\u003c");
                    continue;
                case '>':
                    builder.Append("\\u003e");
                    continue;
                case '&':
                    builder.Append("\\u0026");
                    continue;
                case '\u2028':
                    builder.Append("\\u2028");
                    continue;
                case '\u2029':
                    builder.Append("\\u2029");
                    continue;
            }

            if (c < 0x20)
            {
                builder.Append("\\u00").Append(HexDigits[c >> 4]).Append(HexDigits[c & 0xF]);
                continue;
            }

            if (char.IsHighSurrogate(c))
            {
                if (index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    builder.Append(c).Append(value[index + 1]);
                    index++;
                    continue;
                }

                builder.Append('�');
                continue;
            }

            if (char.IsLowSurrogate(c))
            {
                builder.Append('�');
                continue;
            }

            builder.Append(c);
        }

        builder.Append('"');
    }
}
