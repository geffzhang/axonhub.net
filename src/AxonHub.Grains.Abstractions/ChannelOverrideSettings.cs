using System.Text.Json;
using AxonHub.Abstractions;
using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Request-rewrite half of the reference <c>objects.ChannelSettings</c>
/// (internal/objects/channel.go:231-260): structured body/header override
/// operations plus the legacy <c>overrideParameters</c> / <c>overrideHeaders</c>
/// forms, resolved with the same precedence as
/// <c>biz.Channel.GetBodyOverrideOperations</c> and
/// <c>GetHeaderOverrideOperations</c> (internal/server/biz/channel_override.go).
/// </summary>
[GenerateSerializer]
public sealed class ChannelOverrideSettings
{
    /// <summary>Operations applied to the outbound JSON body, in order.</summary>
    [Id(0)]
    public ChannelOverrideOperation[] BodyOperations { get; set; } = [];

    /// <summary>Operations applied to the outbound request headers, in order.</summary>
    [Id(1)]
    public ChannelOverrideOperation[] HeaderOperations { get; set; } = [];

    /// <summary>
    /// Reads the override fields from a stored <c>settings</c> jsonb value.
    /// The reference validates them when the channel is written; this build has
    /// no channel write API yet, so malformed values degrade to no overrides
    /// instead of failing the channel load.
    /// </summary>
    public static ChannelOverrideSettings Parse(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
        {
            return new ChannelOverrideSettings();
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(settingsJson);
        }
        catch (JsonException)
        {
            return new ChannelOverrideSettings();
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ChannelOverrideSettings();
            }

            return new ChannelOverrideSettings
            {
                BodyOperations = ParseBodyOperations(root),
                HeaderOperations = ParseHeaderOperations(root),
            };
        }
    }

    private static ChannelOverrideOperation[] ParseBodyOperations(JsonElement settings)
    {
        // New schema field takes precedence. Even an explicit empty array means
        // "no overrides", so it also suppresses the legacy field.
        if (TryGetProperty(settings, "bodyOverrideOperations", out var operations)
            && operations.ValueKind != JsonValueKind.Null)
        {
            return ParseOperationArray(operations) ?? [];
        }

        if (!TryGetProperty(settings, "overrideParameters", out var legacy)
            || legacy.ValueKind != JsonValueKind.String)
        {
            return [];
        }

        return ParseOverrideParameters(legacy.GetString() ?? string.Empty);
    }

    private static ChannelOverrideOperation[] ParseHeaderOperations(JsonElement settings)
    {
        if (TryGetProperty(settings, "headerOverrideOperations", out var operations)
            && operations.ValueKind != JsonValueKind.Null)
        {
            return ParseOperationArray(operations) ?? [];
        }

        if (!TryGetProperty(settings, "overrideHeaders", out var entries)
            || entries.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        return ParseHeaderEntries(entries) ?? [];
    }

    /// <summary>Port of <c>objects.ParseOverrideOperations</c>.</summary>
    private static ChannelOverrideOperation[] ParseOverrideParameters(string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0 || raw == "{}" || raw == "[]")
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (raw[0] == '[')
            {
                return ParseOperationArray(root) ?? [];
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var operations = new List<ChannelOverrideOperation>(root.GetPropertyCount());
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString() == ChannelOverrideOps.ClearDirective)
                {
                    operations.Add(new ChannelOverrideOperation
                    {
                        Op = ChannelOverrideOps.Delete,
                        Path = property.Name,
                    });
                }
                else
                {
                    // fmt.Sprintf("%v", value): JSON values decode into Go's
                    // any (numbers as float64) and are stringified with %v.
                    operations.Add(new ChannelOverrideOperation
                    {
                        Op = ChannelOverrideOps.Set,
                        Path = property.Name,
                        Value = GoSprint(property.Value),
                    });
                }
            }

            return [.. operations];
        }
        catch (JsonException)
        {
            // The reference logs the parse error and caches an empty slice.
            return [];
        }
    }

    private static ChannelOverrideOperation[]? ParseOperationArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var operations = new List<ChannelOverrideOperation>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var operation = new ChannelOverrideOperation();
            if (!TryReadString(item, "op", out var op)
                || !TryReadString(item, "path", out var path)
                || !TryReadString(item, "from", out var from)
                || !TryReadString(item, "to", out var to)
                || !TryReadString(item, "value", out var value)
                || !TryReadString(item, "condition", out var condition)
                || !TryReadIndex(item, out var index)
                || !TryReadSplat(item, out var splat)
                || !TryReadMatch(item, out var match))
            {
                return null;
            }

            operation.Op = op;
            operation.Path = path;
            operation.From = from;
            operation.To = to;
            operation.Value = value;
            operation.Condition = condition;
            operation.Index = index;
            operation.Splat = splat;
            operation.Match = match;
            operations.Add(operation);
        }

        return [.. operations];
    }

    private static ChannelOverrideOperation[]? ParseHeaderEntries(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var operations = new List<ChannelOverrideOperation>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryReadString(item, "key", out var key)
                || !TryReadString(item, "value", out var value))
            {
                return null;
            }

            operations.Add(value == ChannelOverrideOps.ClearDirective
                ? new ChannelOverrideOperation { Op = ChannelOverrideOps.Delete, Path = key }
                : new ChannelOverrideOperation
                {
                    Op = ChannelOverrideOps.Set,
                    Path = key,
                    Value = value,
                });
        }

        return [.. operations];
    }

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

    private static bool TryReadString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(element, name, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static bool TryReadIndex(JsonElement element, out long? value)
    {
        value = null;
        if (!TryGetProperty(element, "index", out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        // encoding/json only accepts an integer literal for *int; 1.5 and 1e2
        // both fail before the channel becomes usable.
        var raw = property.GetRawText();
        if (raw.AsSpan().IndexOfAny('.', 'e', 'E') >= 0
            || !property.TryGetInt64(out var parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadSplat(JsonElement element, out bool? value)
    {
        value = null;
        if (!TryGetProperty(element, "splat", out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.ValueKind == JsonValueKind.True;
        return true;
    }

    private static bool TryReadMatch(JsonElement element, out ChannelOverrideMatch? value)
    {
        value = null;
        if (!TryGetProperty(element, "match", out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Object
            || !TryReadString(property, "path", out var path)
            || !TryReadString(property, "eq", out var eq))
        {
            return false;
        }

        value = new ChannelOverrideMatch { Path = path, Eq = eq };
        return true;
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
}

/// <summary>Port of <c>objects.OverrideOperation</c>.</summary>
[GenerateSerializer]
public sealed class ChannelOverrideOperation
{
    [Id(0)]
    public string Op { get; set; } = string.Empty;

    [Id(1)]
    public string Path { get; set; } = string.Empty;

    [Id(2)]
    public string From { get; set; } = string.Empty;

    [Id(3)]
    public string To { get; set; } = string.Empty;

    [Id(4)]
    public string Value { get; set; } = string.Empty;

    [Id(5)]
    public string Condition { get; set; } = string.Empty;

    [Id(6)]
    public ChannelOverrideMatch? Match { get; set; }

    /// <summary>Target position for array_insert; negative values count from the end.</summary>
    [Id(7)]
    public long? Index { get; set; }

    /// <summary>Whether an array value is spread into the target array; defaults to true.</summary>
    [Id(8)]
    public bool? Splat { get; set; }
}

/// <summary>Port of <c>objects.OverrideMatch</c>.</summary>
[GenerateSerializer]
public sealed class ChannelOverrideMatch
{
    /// <summary>Path resolved relative to each array item.</summary>
    [Id(0)]
    public string Path { get; set; } = string.Empty;

    /// <summary>Value that removes the item when it matches.</summary>
    [Id(1)]
    public string Eq { get; set; } = string.Empty;
}

/// <summary>Port of the <c>objects.OverrideOp*</c> constants.</summary>
public static class ChannelOverrideOps
{
    public const string Set = "set";

    public const string SetIfAbsent = "set_if_absent";

    public const string Delete = "delete";

    public const string Rename = "rename";

    public const string Copy = "copy";

    public const string ArrayAppend = "array_append";

    public const string ArrayPrepend = "array_prepend";

    public const string ArrayInsert = "array_insert";

    public const string ArrayRemove = "array_remove";

    /// <summary>Legacy value that turns a header set into a delete.</summary>
    public const string ClearDirective = "__AXONHUB_CLEAR__";
}
