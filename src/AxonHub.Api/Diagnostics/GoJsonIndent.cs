using System.Text;

namespace AxonHub.Api.Diagnostics;

/// <summary>
/// Port of Go's <c>json.Indent</c> pass as used by <c>json.MarshalIndent</c>
/// (encoding/json/indent.go): two-space indentation, <c>: </c> after object
/// keys, <c>\n</c> line breaks, and empty objects/arrays kept on one line as
/// <c>{}</c> / <c>[]</c>.
/// </summary>
/// <remarks>
/// The input must be compact JSON without insignificant whitespace (the
/// <c>GoValue</c> writer never emits any). Blank lines are never produced, the
/// prefix is empty, and string literals pass through untouched — the exact
/// behaviour the reference's <c>marshalChannelModelCacheDiagnosticsExport</c>
/// depends on (channel_model_cache_diagnostics.go:352-359).
/// </remarks>
internal static class GoJsonIndent
{
    public static string Format(string compact, string indent)
    {
        ArgumentNullException.ThrowIfNull(compact);
        ArgumentNullException.ThrowIfNull(indent);

        var builder = new StringBuilder(compact.Length + (compact.Length / 4) + 16);
        var depth = 0;
        var needIndent = false;
        var inString = false;
        var escaped = false;

        foreach (var c in compact)
        {
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
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c is ' ' or '\t' or '\n' or '\r')
            {
                // json.Indent skips whitespace between tokens.
                continue;
            }

            // The check runs before the switch, so a nested object/array that
            // starts a value gets the newline and depth increment too
            // (encoding/json/indent.go:31-40). Without it an array of objects
            // would render as `[{` instead of `[\n  {`.
            if (needIndent && c is not '}' and not ']')
            {
                needIndent = false;
                depth++;
                NewLine(builder, indent, depth);
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    builder.Append(c);
                    break;
                case '{':
                case '[':
                    // Delay the indent so empty objects/arrays stay inline.
                    needIndent = true;
                    builder.Append(c);
                    break;
                case ',':
                    builder.Append(c);
                    NewLine(builder, indent, depth);
                    break;
                case ':':
                    builder.Append(c).Append(' ');
                    break;
                case '}':
                case ']':
                    if (needIndent)
                    {
                        // Suppress the indent in an empty object/array.
                        needIndent = false;
                    }
                    else
                    {
                        depth--;
                        NewLine(builder, indent, depth);
                    }

                    builder.Append(c);
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private static void NewLine(StringBuilder builder, string indent, int depth)
    {
        builder.Append('\n');
        for (var index = 0; index < depth; index++)
        {
            builder.Append(indent);
        }
    }
}
