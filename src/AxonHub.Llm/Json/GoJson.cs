using System.Text;
using System.Text.Json;

namespace AxonHub.Llm.Json;

/// <summary>
/// Go-compatible JSON emission.
/// </summary>
/// <remarks>
/// <para>
/// The reference implementation serializes every wire payload with
/// <c>encoding/json</c>. Two of its behaviours are not reproduced by
/// <see cref="System.Text.Json"/> out of the box and must be applied here so the
/// bytes match:
/// </para>
/// <list type="bullet">
/// <item>HTML escaping of <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> and the JS line
/// separators U+2028/U+2029.</item>
/// <item>Lowercase hex in <c>\u00xx</c> control-character escapes.</item>
/// </list>
/// <para>
/// Raw JSON fragments (<c>json.RawMessage</c> in Go) are carried as
/// <see cref="JsonElement"/>. Go re-compacts them on marshal (whitespace
/// removed, HTML chars escaped, every other escape and number literal preserved
/// verbatim); <see cref="CompactElement"/> performs the same pass.
/// </para>
/// </remarks>
public static class GoJson
{
    /// <summary>Go <c>json.Marshal</c> equivalent for a typed value.</summary>
    public static byte[] Marshal<T>(T value)
        => Escape(JsonSerializer.SerializeToUtf8Bytes(value, LlmJson.Options));

    /// <summary>Go <c>json.Marshal</c> equivalent, decoded as UTF-8.</summary>
    public static string MarshalToString<T>(T value) => Encoding.UTF8.GetString(Marshal(value));

    /// <summary>
    /// Go <c>json.Marshal</c> of a <c>json.RawMessage</c>: compact and escape.
    /// </summary>
    public static byte[] MarshalRaw(ReadOnlySpan<byte> raw) => Compact(raw);

    /// <summary>
    /// Normalizes a JSON fragment the way Go stores a <c>json.RawMessage</c>
    /// after a marshal round-trip.
    /// </summary>
    public static JsonElement CompactElement(JsonElement element)
    {
        var compacted = Compact(Encoding.UTF8.GetBytes(element.GetRawText()));
        using var doc = JsonDocument.Parse(compacted);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Port of Go's internal <c>compact</c> with <c>escapeHTML=true</c>: drop
    /// insignificant whitespace, escape HTML/line-separator characters, and
    /// leave string escapes and number literals untouched.
    /// </summary>
    public static byte[] Compact(ReadOnlySpan<byte> src)
    {
        var dst = new List<byte>(src.Length + 8);
        var inString = false;
        var escaped = false;

        for (var i = 0; i < src.Length; i++)
        {
            var b = src[i];

            if (inString)
            {
                if (escaped)
                {
                    dst.Add(b);
                    escaped = false;
                    continue;
                }

                switch (b)
                {
                    case (byte)'\\':
                        dst.Add(b);
                        escaped = true;
                        continue;
                    case (byte)'"':
                        dst.Add(b);
                        inString = false;
                        continue;
                    default:
                        AppendWithHtmlEscape(dst, src, ref i);
                        continue;
                }
            }

            switch (b)
            {
                case (byte)'"':
                    dst.Add(b);
                    inString = true;
                    continue;
                case (byte)' ':
                case (byte)'\t':
                case (byte)'\n':
                case (byte)'\r':
                    continue;
                default:
                    AppendWithHtmlEscape(dst, src, ref i);
                    continue;
            }
        }

        return dst.ToArray();
    }

    /// <summary>Go <c>encoding/json</c> escaping applied to already-serialized JSON.</summary>
    public static byte[] Escape(ReadOnlySpan<byte> src)
    {
        var dst = new List<byte>(src.Length + 16);
        var escaped = false;

        for (var i = 0; i < src.Length; i++)
        {
            var b = src[i];

            if (escaped)
            {
                dst.Add(b);
                escaped = false;
                continue;
            }

            if (b == (byte)'\\')
            {
                // Lowercase the hex of \u00XX control escapes; Go and
                // System.Text.Json differ only in hex case there.
                if (i + 5 < src.Length && src[i + 1] == (byte)'u'
                    && TryParseHex4(src.Slice(i + 2, 4), out var code)
                    && code < 0x20)
                {
                    dst.Add(b);
                    dst.Add((byte)'u');
                    for (var k = i + 2; k < i + 6; k++)
                    {
                        var h = src[k];
                        dst.Add(h >= (byte)'A' && h <= (byte)'F' ? (byte)(h + 32) : h);
                    }

                    i += 5;
                    continue;
                }

                dst.Add(b);
                escaped = true;
                continue;
            }

            AppendWithHtmlEscape(dst, src, ref i);
        }

        return dst.ToArray();
    }

    private static void AppendWithHtmlEscape(List<byte> dst, ReadOnlySpan<byte> src, ref int i)
    {
        var b = src[i];

        switch (b)
        {
            case (byte)'<':
                AppendAscii(dst, "\\u003c");
                return;
            case (byte)'>':
                AppendAscii(dst, "\\u003e");
                return;
            case (byte)'&':
                AppendAscii(dst, "\\u0026");
                return;
            case 0xE2 when i + 2 < src.Length && src[i + 1] == 0x80 && src[i + 2] == 0xA8:
                AppendAscii(dst, "\\u2028");
                i += 2;
                return;
            case 0xE2 when i + 2 < src.Length && src[i + 1] == 0x80 && src[i + 2] == 0xA9:
                AppendAscii(dst, "\\u2029");
                i += 2;
                return;
            default:
                dst.Add(b);
                return;
        }
    }

    private static void AppendAscii(List<byte> dst, string text)
    {
        foreach (var c in text)
        {
            dst.Add((byte)c);
        }
    }

    private static bool TryParseHex4(ReadOnlySpan<byte> span, out int value)
    {
        value = 0;
        foreach (var b in span)
        {
            int digit;
            if (b >= (byte)'0' && b <= (byte)'9')
            {
                digit = b - '0';
            }
            else if (b >= (byte)'a' && b <= (byte)'f')
            {
                digit = b - 'a' + 10;
            }
            else if (b >= (byte)'A' && b <= (byte)'F')
            {
                digit = b - 'A' + 10;
            }
            else
            {
                return false;
            }

            value = (value << 4) | digit;
        }

        return true;
    }
}
