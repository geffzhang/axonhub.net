using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Nanogpt;

/// <summary>
/// Port of llm/transformer/nanogpt/xml_parser.go: a regex-based extractor for
/// the XML-ish tool-call syntax NanoGPT models emit in plain message content.
/// </summary>
public static class NanogptXmlToolCalls
{
    /// <summary>Maximum content length to prevent ReDoS attacks (100KB).</summary>
    public const int MaxXmlParseLength = 100000;

    // Go regexp's \s is the ASCII class; [^\s] must not use .NET's Unicode \s.
    private const string Ws = @"[ \t\n\f\r]";

    private static readonly Regex ToolCallPattern = new(
        @"<([a-zA-Z_][a-zA-Z0-9_-]*)" + Ws + @"*([^>]*)>([^<]*)</([a-zA-Z_][a-zA-Z0-9_-]*)>",
        RegexOptions.CultureInvariant);

    private static readonly Regex SelfClosingPattern = new(
        @"<([a-zA-Z_][a-zA-Z0-9_-]*)" + Ws + @"*([^>]*)/>",
        RegexOptions.CultureInvariant);

    private static readonly Regex AttrPattern = new(
        @"([a-zA-Z_][a-zA-Z0-9_-]*)" + Ws + @"*=" + Ws + @"*[""']([^""']*)[""']",
        RegexOptions.CultureInvariant);

    private static readonly Regex NormalizeTagPattern = new(
        @"([^ \t\n\f\r])/>",
        RegexOptions.CultureInvariant);

    private static readonly Regex NestedXmlPattern = new(
        @"<(Write|Read)[^>]*>" + Ws + @"*<file_path>([^<]*)</file_path>" + Ws
        + @"*<content>([\s\S]*?)</content>" + Ws + @"*</(Write|Read)>",
        RegexOptions.CultureInvariant);

    private static readonly Regex MismatchTagPattern = new(
        @"<(Write|Read|Write_FILE|Write_file|Read_FILE|Read_file)([^>]*)>([^<]*)</use_tool>",
        RegexOptions.CultureInvariant);

    private static readonly Regex UnclosedPattern = new(
        @"<(Write|Read|Write_FILE|Write_file|Read_FILE|Read_file)([^>]*)\n([\s\S]*?)</use_tool>",
        RegexOptions.CultureInvariant);

    // unicode.IsSpace (Go): Latin-1 spaces plus the Unicode White_Space set.
    private static readonly char[] GoWhitespace =
    [
        ' ', '\t', '\n', '\v', '\f', '\r', '\u0085', '\u00A0',
        '\u1680', '\u2000', '\u2001', '\u2002', '\u2003', '\u2004', '\u2005', '\u2006',
        '\u2007', '\u2008', '\u2009', '\u200A', '\u2028', '\u2029', '\u202F', '\u205F',
        '\u3000'
    ];

    /// <summary>Port of MaybeHasXMLToolCalls.</summary>
    public static bool MaybeHasXmlToolCalls(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > MaxXmlParseLength)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            content = Encoding.UTF8.GetString(bytes, 0, MaxXmlParseLength);
        }

        return content.Contains('<', StringComparison.Ordinal)
            && content.Contains('>', StringComparison.Ordinal)
            && (ToolCallPattern.IsMatch(content)
                || SelfClosingPattern.IsMatch(content)
                || content.Contains("use_tool", StringComparison.Ordinal)
                || content.Contains("Write", StringComparison.Ordinal)
                || content.Contains("Bash", StringComparison.Ordinal)
                || content.Contains("Read", StringComparison.Ordinal));
    }

    /// <summary>
    /// Port of ParseXMLToolCalls. Returns null when no recognized tool tags are
    /// present; <paramref name="remaining"/> is then the (possibly normalized)
    /// content unchanged, matching the reference.
    /// </summary>
    public static List<ToolCall>? ParseXmlToolCalls(string content, out string remaining)
    {
        if (!MaybeHasXmlToolCalls(content))
        {
            remaining = content;
            return null;
        }

        content = NormalizeXml(content);

        var toolCalls = new List<ToolCall>();
        var remainingContent = new StringBuilder();
        var lastEnd = 0;

        var matches = new List<MatchInfo>();

        // Nested XML must be collected before the other patterns so its inner
        // elements do not shadow the whole call.
        foreach (Match match in NestedXmlPattern.Matches(content))
        {
            var tagName = match.Groups[1].Value;
            var filePath = match.Groups[2].Value;
            var innerContent = match.Groups[3].Value;
            var closingTag = match.Groups[4].Value;

            if (!string.Equals(tagName, closingTag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Synthetic attributes; escape quotes to prevent XML injection.
            var attrs = "file_path=\""
                + filePath.Replace("\"", "\\\"", StringComparison.Ordinal)
                + "\" content=\""
                + innerContent.Replace("\"", "\\\"", StringComparison.Ordinal)
                + "\"";

            matches.Add(new MatchInfo(
                match.Index,
                match.Index + match.Length,
                tagName,
                attrs,
                string.Empty));
        }

        foreach (Match match in ToolCallPattern.Matches(content))
        {
            var openingTag = match.Groups[1].Value;
            var closingTag = match.Groups[4].Value;

            if (!string.Equals(openingTag, closingTag, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            matches.Add(new MatchInfo(
                match.Index,
                match.Index + match.Length,
                openingTag,
                match.Groups[2].Value,
                match.Groups[3].Value));
        }

        foreach (Match match in SelfClosingPattern.Matches(content))
        {
            matches.Add(new MatchInfo(
                match.Index,
                match.Index + match.Length,
                match.Groups[1].Value,
                match.Groups[2].Value,
                string.Empty));
        }

        matches.Sort((left, right) => left.Start.CompareTo(right.Start));

        foreach (var match in matches)
        {
            var toolName = ExtractToolName(match.TagName, match.Attrs);
            if (toolName.Length == 0)
            {
                // Not a recognized tool pattern; keep the surrounding content.
                if (lastEnd < match.Start)
                {
                    remainingContent.Append(content, lastEnd, match.Start - lastEnd);
                }

                lastEnd = match.End;
                continue;
            }

            var args = ExtractToolArguments(toolName, match.Attrs, match.InnerContent);
            var id = GenerateToolCallId(toolName, args);

            toolCalls.Add(new ToolCall
            {
                Index = toolCalls.Count,
                Id = id,
                Type = "function",
                Function = new FunctionCall
                {
                    Name = toolName,
                    Arguments = args,
                },
            });

            if (lastEnd < match.Start)
            {
                remainingContent.Append(content, lastEnd, match.Start - lastEnd);
            }

            lastEnd = match.End;
        }

        if (lastEnd < content.Length)
        {
            remainingContent.Append(content, lastEnd, content.Length - lastEnd);
        }

        if (toolCalls.Count == 0)
        {
            remaining = content;
            return null;
        }

        remaining = GoTrimSpace(remainingContent.ToString());
        return toolCalls;
    }

    /// <summary>Port of normalizeXML.</summary>
    public static string NormalizeXml(string content)
    {
        content = UnclosedPattern.Replace(content, match =>
            "<" + match.Groups[1].Value + match.Groups[2].Value + ">\n"
            + match.Groups[3].Value + "</use_tool>");

        content = content.Replace("</use_use>", "</use_tool>", StringComparison.Ordinal);
        content = content.Replace("</Write_file>", "</Write>", StringComparison.Ordinal);
        content = content.Replace("</Write_FILE>", "</Write>", StringComparison.Ordinal);
        content = content.Replace("</Read_file>", "</Read>", StringComparison.Ordinal);
        content = content.Replace("</Read_FILE>", "</Read>", StringComparison.Ordinal);

        content = MismatchTagPattern.Replace(content, match =>
            "<" + match.Groups[1].Value + match.Groups[2].Value + ">"
            + match.Groups[3].Value + "</" + match.Groups[1].Value + ">");

        content = NormalizeTagPattern.Replace(content, "$1 />");

        return content;
    }

    /// <summary>Port of extractToolName.</summary>
    public static string ExtractToolName(string tagName, string attrs)
    {
        tagName = GoTrimSpace(tagName).ToLowerInvariant();

        if (tagName.StartsWith("write", StringComparison.Ordinal))
        {
            return "write";
        }

        if (tagName.StartsWith("read", StringComparison.Ordinal))
        {
            return "read";
        }

        if (tagName is "bash" or "python" or "search" or "glob")
        {
            return tagName;
        }

        if (tagName == "use_tool")
        {
            foreach (Match match in AttrPattern.Matches(attrs))
            {
                if (string.Equals(
                    match.Groups[1].Value,
                    "name",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return match.Groups[2].Value.ToLowerInvariant();
                }
            }
        }

        return string.Empty;
    }

    /// <summary>Port of extractToolArguments.</summary>
    public static string ExtractToolArguments(
        string toolName,
        string attrs,
        string innerContent)
    {
        var args = new Dictionary<string, GoAnyValue>(StringComparer.Ordinal);

        foreach (Match match in AttrPattern.Matches(attrs))
        {
            var key = match.Groups[1].Value;
            var value = match.Groups[2].Value;

            // Skip the name attribute for use_tool tags.
            if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase)
                && toolName.Length > 0)
            {
                continue;
            }

            args[key] = new GoAnyString(value);
        }

        innerContent = GoTrimSpace(innerContent);
        if (innerContent.Length > 0)
        {
            if (TryParseGoJsonAny(innerContent, out var jsonContent))
            {
                if (jsonContent is GoAnyObject jsonObject)
                {
                    foreach (var member in jsonObject.Members)
                    {
                        if (!args.ContainsKey(member.Key))
                        {
                            args[member.Key] = member.Value;
                        }
                    }
                }
                else
                {
                    args["content"] = jsonContent;
                }
            }
            else if (!args.ContainsKey("content"))
            {
                args["content"] = new GoAnyString(innerContent);
            }
            else
            {
                args["arg"] = new GoAnyString(innerContent);
            }
        }

        return args.Count == 0 ? "{}" : SerializeGoAnyObject(args);
    }

    /// <summary>Port of generateToolCallID (SHA-256 of name + arguments).</summary>
    public static string GenerateToolCallId(string name, string args)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var argsBytes = Encoding.UTF8.GetBytes(args);
        var input = new byte[nameBytes.Length + argsBytes.Length];
        nameBytes.CopyTo(input, 0);
        argsBytes.CopyTo(input, nameBytes.Length);

        var hash = SHA256.HashData(input);
        var hex = Convert.ToHexString(hash).ToLowerInvariant();

        return "nanogpt_" + hex[..16];
    }

    /// <summary>Port of ToOpenAIToolCalls; the reference returns nil for empty input.</summary>
    public static List<OpenAiToolCall>? ToOpenAiToolCalls(List<ToolCall> toolCalls)
    {
        if (toolCalls.Count == 0)
        {
            return null;
        }

        return toolCalls.Select(toolCall => new OpenAiToolCall
        {
            Id = toolCall.Id,
            Type = toolCall.Type,
            Index = toolCall.Index,
            Function = new OpenAiFunctionCall
            {
                Name = toolCall.Function.Name,
                Arguments = toolCall.Function.Arguments,
            },
        }).ToList();
    }

    /// <summary>Port of ToOpenAIMessageContent.</summary>
    public static OpenAiMessageContent ToOpenAiMessageContent(string content)
        => new() { Content = content };

    private static string GoTrimSpace(string value)
    {
        var start = 0;
        while (start < value.Length && IsGoSpace(value[start]))
        {
            start++;
        }

        var end = value.Length;
        while (end > start && IsGoSpace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static bool IsGoSpace(char c) => Array.IndexOf(GoWhitespace, c) >= 0;

    private static bool TryParseGoJsonAny(string text, out GoAnyValue value)
    {
        value = GoAnyNull.Instance;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            return TryConvertGoAny(document.RootElement, out value);
        }
    }

    private static bool TryConvertGoAny(JsonElement element, out GoAnyValue value)
    {
        value = GoAnyNull.Instance;

        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.True:
                value = new GoAnyBool(true);
                return true;
            case JsonValueKind.False:
                value = new GoAnyBool(false);
                return true;
            case JsonValueKind.String:
                value = new GoAnyString(element.GetString() ?? string.Empty);
                return true;
            case JsonValueKind.Number:
                double number;
                try
                {
                    number = element.GetDouble();
                }
                catch (Exception ex) when (
                    ex is FormatException or InvalidOperationException or OverflowException)
                {
                    return false;
                }

                if (!double.IsFinite(number))
                {
                    // encoding/json rejects numbers that overflow float64.
                    return false;
                }

                value = new GoAnyNumber(number);
                return true;
            case JsonValueKind.Array:
                var items = new List<GoAnyValue>();
                foreach (var item in element.EnumerateArray())
                {
                    if (!TryConvertGoAny(item, out var convertedItem))
                    {
                        return false;
                    }

                    items.Add(convertedItem);
                }

                value = new GoAnyArray(items);
                return true;
            case JsonValueKind.Object:
                var members = new Dictionary<string, GoAnyValue>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!TryConvertGoAny(property.Value, out var convertedValue))
                    {
                        return false;
                    }

                    // encoding/json lets the last duplicate key win.
                    members[property.Name] = convertedValue;
                }

                value = new GoAnyObject(members);
                return true;
            default:
                return false;
        }
    }

    private static string SerializeGoAnyObject(Dictionary<string, GoAnyValue> members)
    {
        var builder = new StringBuilder();
        WriteGoAnyObject(builder, members);
        return builder.ToString();
    }

    private static void WriteGoAnyValue(StringBuilder builder, GoAnyValue value)
    {
        switch (value)
        {
            case GoAnyNull:
                builder.Append("null");
                break;
            case GoAnyBool boolean:
                builder.Append(boolean.Value ? "true" : "false");
                break;
            case GoAnyNumber number:
                builder.Append(GoJsonFloat.Format(number.Value));
                break;
            case GoAnyString text:
                AppendGoString(builder, text.Value);
                break;
            case GoAnyArray array:
                builder.Append('[');
                for (var i = 0; i < array.Items.Count; i++)
                {
                    if (i > 0)
                    {
                        builder.Append(',');
                    }

                    WriteGoAnyValue(builder, array.Items[i]);
                }

                builder.Append(']');
                break;
            case GoAnyObject obj:
                WriteGoAnyObject(builder, obj.Members);
                break;
        }
    }

    private static void WriteGoAnyObject(
        StringBuilder builder,
        Dictionary<string, GoAnyValue> members)
    {
        // Go's encoding/json sorts map keys by their UTF-8 byte order.
        var keys = members.Keys.ToList();
        keys.Sort(CompareUtf8);

        builder.Append('{');
        for (var i = 0; i < keys.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            AppendGoString(builder, keys[i]);
            builder.Append(':');
            WriteGoAnyValue(builder, members[keys[i]]);
        }

        builder.Append('}');
    }

    private static void AppendGoString(StringBuilder builder, string value)
        => builder.Append(Encoding.UTF8.GetString(GoJson.Marshal(value)));

    private static int CompareUtf8(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);

        var length = Math.Min(leftBytes.Length, rightBytes.Length);
        for (var i = 0; i < length; i++)
        {
            var comparison = leftBytes[i].CompareTo(rightBytes[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftBytes.Length.CompareTo(rightBytes.Length);
    }

    private readonly record struct MatchInfo(
        int Start,
        int End,
        string TagName,
        string Attrs,
        string InnerContent);

    private abstract record GoAnyValue;

    private sealed record GoAnyNull : GoAnyValue
    {
        public static GoAnyNull Instance { get; } = new();
    }

    private sealed record GoAnyBool(bool Value) : GoAnyValue;

    private sealed record GoAnyNumber(double Value) : GoAnyValue;

    private sealed record GoAnyString(string Value) : GoAnyValue;

    private sealed record GoAnyArray(List<GoAnyValue> Items) : GoAnyValue;

    private sealed record GoAnyObject(Dictionary<string, GoAnyValue> Members) : GoAnyValue;
}
