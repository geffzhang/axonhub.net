using System.Text;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Minimal port of the <c>kaptinlin/jsonrepair</c> subset AxonHub needs for
/// tool-call arguments: strip comments, normalize single-quoted strings,
/// quote bare keys and bare string values, and drop trailing commas.
/// </summary>
/// <remarks>
/// The full jsonrepair library also handles Python literals, unclosed
/// structures, ellipses and number normalization; those are registered as a
/// known difference (porting/differences.jsonl) rather than guessed at here.
/// </remarks>
internal static class JsonRepair
{
    /// <summary>Returns repaired JSON text, or null when no repair applies.</summary>
    public static string? Repair(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return null;
        }

        var text = QuoteBareTokens(NormalizeSingleQuotes(StripComments(input)));
        return IsValidJson(text) ? text : null;
    }

    private static bool IsValidJson(string text)
    {
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(text);
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string StripComments(string text)
    {
        var builder = new StringBuilder(text.Length);
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                builder.Append(c);
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"' || c == '\'')
                {
                    inString = false;
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                inString = true;
                builder.Append(c);
                continue;
            }

            if (c == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }

                    builder.Append('\n');
                    continue;
                }

                if (text[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
                    {
                        i++;
                    }

                    i++;
                    builder.Append(' ');
                    continue;
                }
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string NormalizeSingleQuotes(string text)
    {
        var builder = new StringBuilder(text.Length);
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (c == '"')
            {
                builder.Append(c);
                i++;
                while (i < text.Length)
                {
                    var ch = text[i];
                    builder.Append(ch);
                    i++;

                    if (ch == '\\' && i < text.Length)
                    {
                        builder.Append(text[i]);
                        i++;
                        continue;
                    }

                    if (ch == '"')
                    {
                        break;
                    }
                }

                continue;
            }

            if (c != '\'')
            {
                builder.Append(c);
                i++;
                continue;
            }

            builder.Append('"');
            i++;
            while (i < text.Length)
            {
                var ch = text[i];
                if (ch == '\\' && i + 1 < text.Length)
                {
                    var next = text[i + 1];
                    if (next == '\'')
                    {
                        builder.Append('\'');
                    }
                    else if (next == '"')
                    {
                        builder.Append("\\\"");
                    }
                    else
                    {
                        builder.Append(ch).Append(next);
                    }

                    i += 2;
                    continue;
                }

                if (ch == '"')
                {
                    builder.Append("\\\"");
                    i++;
                    continue;
                }

                if (ch == '\'')
                {
                    builder.Append('"');
                    i++;
                    break;
                }

                builder.Append(ch);
                i++;
            }
        }

        return builder.ToString();
    }

    private static string QuoteBareTokens(string text)
    {
        var builder = new StringBuilder(text.Length + 16);
        var containers = new Stack<char>();
        var expectKey = false;
        var expectValue = false;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (char.IsWhiteSpace(c))
            {
                builder.Append(c);
                i++;
                continue;
            }

            switch (c)
            {
                case '"':
                    CopyString(text, ref i, builder);
                    expectKey = false;
                    expectValue = false;
                    continue;
                case '{':
                    containers.Push('{');
                    builder.Append(c);
                    i++;
                    expectKey = true;
                    expectValue = false;
                    continue;
                case '[':
                    containers.Push('[');
                    builder.Append(c);
                    i++;
                    expectKey = false;
                    expectValue = true;
                    continue;
                case '}' or ']':
                    if (containers.Count > 0)
                    {
                        containers.Pop();
                    }

                    builder.Append(c);
                    i++;
                    expectKey = false;
                    expectValue = false;
                    continue;
                case ':':
                    builder.Append(c);
                    i++;
                    expectKey = false;
                    expectValue = true;
                    continue;
                case ',':
                    if (NextNonWhitespaceIsClose(text, i + 1))
                    {
                        // Trailing comma: jsonrepair drops it.
                        i++;
                        continue;
                    }

                    builder.Append(c);
                    i++;
                    expectKey = containers.Count > 0 && containers.Peek() == '{';
                    expectValue = !expectKey;
                    continue;
            }

            if (expectKey)
            {
                var keyStart = i;
                while (i < text.Length && text[i] != ':')
                {
                    i++;
                }

                var key = text[keyStart..i].Trim();
                builder.Append('"').Append(key.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
                expectKey = false;
                continue;
            }

            if (expectValue)
            {
                var valueStart = i;
                while (i < text.Length && text[i] is not (',' or '}' or ']'))
                {
                    i++;
                }

                var token = text[valueStart..i].Trim();
                if (token.Length == 0)
                {
                    continue;
                }

                if (token is "true" or "false" or "null" || IsNumber(token))
                {
                    builder.Append(token);
                }
                else
                {
                    builder.Append('"').Append(token.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
                }

                expectValue = false;
                continue;
            }

            // Undecorated token outside a key/value position: copy verbatim.
            builder.Append(c);
            i++;
        }

        return builder.ToString();
    }

    private static bool IsNumber(string token)
    {
        if (token.Length == 0)
        {
            return false;
        }

        var start = token[0] == '-' ? 1 : 0;
        if (start == token.Length)
        {
            return false;
        }

        var seenDigit = false;
        var seenDot = false;
        var seenExponent = false;

        foreach (var c in token.AsSpan(start))
        {
            if (char.IsAsciiDigit(c))
            {
                seenDigit = true;
            }
            else if (c == '.' && !seenDot && !seenExponent)
            {
                seenDot = true;
            }
            else if (c is 'e' or 'E' && seenDigit && !seenExponent)
            {
                seenExponent = true;
            }
            else if (c is '+' or '-' && seenExponent && token[^1] != c)
            {
                // exponent sign
            }
            else
            {
                return false;
            }
        }

        return seenDigit;
    }

    private static bool NextNonWhitespaceIsClose(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index < text.Length && text[index] is '}' or ']';
    }

    private static void CopyString(string text, ref int i, StringBuilder builder)
    {
        builder.Append(text[i]);
        i++;
        while (i < text.Length)
        {
            var ch = text[i];
            builder.Append(ch);
            i++;

            if (ch == '\\' && i < text.Length)
            {
                builder.Append(text[i]);
                i++;
                continue;
            }

            if (ch == '"')
            {
                return;
            }
        }
    }
}
