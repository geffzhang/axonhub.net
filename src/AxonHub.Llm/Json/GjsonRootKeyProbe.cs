using System.Globalization;
using System.Text;

namespace AxonHub.Llm.Json;

/// <summary>
/// Port of the slice of gjson (github.com/tidwall/gjson v1.18.0, MIT license)
/// behind <c>gjson.GetBytes(body, key).Exists()</c> for a simple top-level key
/// without path separators.
/// </summary>
/// <remarks>
/// gjson does not validate JSON. <c>Get</c> finds the first <c>{</c> or
/// <c>[</c>, then scans object keys leniently: values may be truncated,
/// commas and colons may be missing, and stray text is skipped. A matching key
/// reports a hit as soon as anything that can open a value follows it. The
/// codex outbound relies on this to reject raw Responses bodies that carry a
/// token-limit field (difference C094).
/// </remarks>
internal static class GjsonRootKeyProbe
{
    /// <summary>
    /// Port of <c>gjson.GetBytes(body, key).Exists()</c> for a key such as
    /// <c>max_tokens</c>. A simple key never matches an array element, so an
    /// array root reports no hit.
    /// </summary>
    public static bool Exists(ReadOnlySpan<byte> body, ReadOnlySpan<byte> key)
    {
        // gjson.go:2212-2223 — Get scans for the first '{' or '[' and searches
        // only that container; an array root cannot match a simple object key.
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == (byte)'{')
            {
                return ParseObject(body, i + 1, key);
            }

            if (body[i] == (byte)'[')
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Port of <c>gjson.GetBytes(body, key).String()</c> for a simple top-level
    /// key. The first occurrence wins. A string value is unescaped, an object
    /// or array keeps its raw JSON, literals become <c>true</c>/<c>false</c>,
    /// an integer number keeps its raw text while other numbers use Go's
    /// shortest plain notation, and a missing key, <c>null</c> or a malformed
    /// document yields the empty string.
    /// </summary>
    /// <remarks>
    /// Used by the Bedrock EventStream decoder, whose chunk payload starts as
    /// opaque bytes; like gjson it never validates the surrounding document.
    /// </remarks>
    public static string GetString(ReadOnlySpan<byte> body, ReadOnlySpan<byte> key)
    {
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] == (byte)'{')
            {
                return ParseObjectString(body, i + 1, key);
            }

            // gjson.Get searches only the first container; a simple key cannot
            // match an array root (gjson.go:2212-2223).
            if (body[i] == (byte)'[')
            {
                return string.Empty;
            }
        }

        return string.Empty;
    }

    // Port of parseObject (gjson.go:1189-1350) for a single-segment path,
    // returning Result.String() instead of Exists().
    private static string ParseObjectString(ReadOnlySpan<byte> body, int i, ReadOnlySpan<byte> key)
    {
        while (i < body.Length)
        {
            ReadOnlySpan<byte> candidate = default;
            var escaped = false;
            var closed = false;

            for (; i < body.Length; i++)
            {
                if (body[i] == (byte)'"')
                {
                    i = ScanKey(body, i + 1, out candidate, out escaped, out closed);
                    break;
                }

                if (body[i] == (byte)'}')
                {
                    return string.Empty;
                }
            }

            if (!closed)
            {
                return string.Empty;
            }

            var matched = escaped
                ? Unescape(candidate).AsSpan().SequenceEqual(key)
                : candidate.SequenceEqual(key);

            for (; i < body.Length; i++)
            {
                var isNumber = false;

                switch (body[i])
                {
                    default:
                        continue;
                    case (byte)'"':
                    {
                        var start = i + 1;
                        i = ParseString(body, i + 1, out var stringClosed);
                        if (!stringClosed)
                        {
                            return string.Empty;
                        }

                        if (matched)
                        {
                            var raw = body[start..(i - 1)];
                            return ContainsBackslash(raw)
                                ? Encoding.UTF8.GetString(Unescape(raw))
                                : Encoding.UTF8.GetString(raw);
                        }

                        break;
                    }

                    case (byte)'{':
                    case (byte)'[':
                    {
                        var start = i;
                        i = Squash(body, i);
                        if (matched)
                        {
                            return Encoding.UTF8.GetString(body[start..i]);
                        }

                        break;
                    }

                    case (byte)'n':
                        if (i + 1 < body.Length && body[i + 1] != (byte)'u')
                        {
                            isNumber = true;
                            break;
                        }

                        goto case (byte)'t';
                    case (byte)'t':
                    case (byte)'f':
                    {
                        var literal = body[i];
                        i = ParseLiteral(body, i);
                        if (matched)
                        {
                            // 'n' (null) carries the zero Result type, whose
                            // String() is the empty string.
                            return literal switch
                            {
                                (byte)'t' => "true",
                                (byte)'f' => "false",
                                _ => string.Empty,
                            };
                        }

                        break;
                    }

                    case (byte)'+':
                    case (byte)'-':
                    case >= (byte)'0' and <= (byte)'9':
                    case (byte)'i':
                    case (byte)'I':
                    case (byte)'N':
                        isNumber = true;
                        break;
                }

                if (isNumber)
                {
                    var start = i;
                    i = ParseNumber(body, i);
                    if (matched)
                    {
                        return NumberString(body[start..i]);
                    }
                }

                break;
            }
        }

        return string.Empty;
    }

    // Port of Result.String()'s Number branch (gjson.go:87-110): a raw integer
    // literal is returned verbatim, anything else goes through
    // strconv.FormatFloat(t.Num, 'f', -1, 64).
    private static string NumberString(ReadOnlySpan<byte> raw)
    {
        var i = raw.Length > 0 && raw[0] == (byte)'-' ? 1 : 0;
        var integer = raw.Length > i;
        for (var j = i; j < raw.Length; j++)
        {
            if (raw[j] < (byte)'0' || raw[j] > (byte)'9')
            {
                integer = false;
                break;
            }
        }

        if (integer)
        {
            return Encoding.UTF8.GetString(raw);
        }

        // ParseFloat's error is discarded by gjson; its zero value formats as
        // "0".
        return double.TryParse(
            Encoding.ASCII.GetString(raw),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? FormatGoFloat(value)
            : "0";
    }

    // Port of strconv.FormatFloat(value, 'f', -1, 64): the shortest round-trip
    // digits in plain decimal notation, never an exponent.
    private static string FormatGoFloat(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(value))
        {
            return "+Inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Inf";
        }

        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = text.IndexOfAny(['E', 'e']);
        if (exponentAt < 0)
        {
            return text;
        }

        var sign = text[0] == '-' ? "-" : string.Empty;
        var start = sign.Length;
        var mantissa = text[start..exponentAt];
        var exponent = int.Parse(text[(exponentAt + 1)..], CultureInfo.InvariantCulture);
        var digits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal);
        var point = (mantissa.Contains('.') ? mantissa.IndexOf('.') : mantissa.Length) + exponent;

        string body;
        if (point <= 0)
        {
            body = "0." + new string('0', -point) + digits;
        }
        else if (point >= digits.Length)
        {
            body = digits + new string('0', point - digits.Length);
        }
        else
        {
            body = digits[..point] + "." + digits[point..];
        }

        return sign + body;
    }

    private static bool ContainsBackslash(ReadOnlySpan<byte> data)
        => data.IndexOf((byte)'\\') >= 0;

    // Port of parseObject (gjson.go:1189-1350) for a single-segment path.
    private static bool ParseObject(ReadOnlySpan<byte> body, int i, ReadOnlySpan<byte> key)
    {
        while (i < body.Length)
        {
            ReadOnlySpan<byte> candidate = default;
            var escaped = false;
            var closed = false;

            // Scan for the next key string; a '}' before one ends the search.
            for (; i < body.Length; i++)
            {
                if (body[i] == (byte)'"')
                {
                    i = ScanKey(body, i + 1, out candidate, out escaped, out closed);
                    break;
                }

                if (body[i] == (byte)'}')
                {
                    return false;
                }
            }

            if (!closed)
            {
                return false;
            }

            var matched = escaped
                ? Unescape(candidate).AsSpan().SequenceEqual(key)
                : candidate.SequenceEqual(key);

            // Scan the value. Anything that cannot open a value is skipped,
            // including colons, commas and even a '}' when the key matched but
            // no value followed it (gjson.go:1265-1347).
            for (; i < body.Length; i++)
            {
                var isNumber = false;

                switch (body[i])
                {
                    default:
                        continue;
                    case (byte)'"':
                        i = ParseString(body, i + 1, out var stringClosed);
                        if (!stringClosed)
                        {
                            return false;
                        }

                        if (matched)
                        {
                            return true;
                        }

                        break;
                    case (byte)'{':
                    case (byte)'[':
                        i = Squash(body, i);
                        if (matched)
                        {
                            return true;
                        }

                        break;
                    case (byte)'n':
                        if (i + 1 < body.Length && body[i + 1] != (byte)'u')
                        {
                            isNumber = true;
                            break;
                        }

                        goto case (byte)'t';
                    case (byte)'t':
                    case (byte)'f':
                        i = ParseLiteral(body, i);
                        if (matched)
                        {
                            return true;
                        }

                        break;
                    case (byte)'+':
                    case (byte)'-':
                    case >= (byte)'0' and <= (byte)'9':
                    case (byte)'i':
                    case (byte)'I':
                    case (byte)'N':
                        isNumber = true;
                        break;
                }

                if (isNumber)
                {
                    i = ParseNumber(body, i);
                    if (matched)
                    {
                        return true;
                    }
                }

                break;
            }
        }

        return false;
    }

    // Port of parse_key_string inside parseObject (gjson.go:1198-1243).
    // i is the index after the opening quote; returns the index after the
    // closing quote (or the stop position when the key is unterminated).
    private static int ScanKey(
        ReadOnlySpan<byte> body,
        int i,
        out ReadOnlySpan<byte> key,
        out bool escaped,
        out bool closed)
    {
        var start = i;
        escaped = false;
        for (; i < body.Length; i++)
        {
            if (body[i] > (byte)'\\')
            {
                continue;
            }

            if (body[i] == (byte)'"')
            {
                key = body[start..i];
                closed = true;
                return i + 1;
            }

            if (body[i] == (byte)'\\')
            {
                i++;
                for (; i < body.Length; i++)
                {
                    if (body[i] > (byte)'\\')
                    {
                        continue;
                    }

                    if (body[i] == (byte)'"')
                    {
                        // gjson treats this quote as escaped when the run of
                        // backslashes before it has even length. The run scan
                        // stops at index 0, so a backslash at the start of the
                        // body is not counted (gjson.go:1222-1231).
                        if (body[i - 1] == (byte)'\\')
                        {
                            var run = 0;
                            for (var j = i - 2; j > 0; j--)
                            {
                                if (body[j] != (byte)'\\')
                                {
                                    break;
                                }

                                run++;
                            }

                            if (run % 2 == 0)
                            {
                                continue;
                            }
                        }

                        key = body[start..i];
                        escaped = true;
                        closed = true;
                        return i + 1;
                    }
                }

                break;
            }
        }

        key = body[start..];
        closed = false;
        return i;
    }

    // Port of parseString (gjson.go:688-724). i is the index after the opening
    // quote; returns the index after the closing quote, or the stop position
    // when the string is unterminated.
    private static int ParseString(ReadOnlySpan<byte> body, int i, out bool closed)
    {
        for (; i < body.Length; i++)
        {
            if (body[i] > (byte)'\\')
            {
                continue;
            }

            if (body[i] == (byte)'"')
            {
                closed = true;
                return i + 1;
            }

            if (body[i] == (byte)'\\')
            {
                i++;
                for (; i < body.Length; i++)
                {
                    if (body[i] > (byte)'\\')
                    {
                        continue;
                    }

                    if (body[i] == (byte)'"')
                    {
                        if (body[i - 1] == (byte)'\\')
                        {
                            var run = 0;
                            for (var j = i - 2; j > 0; j--)
                            {
                                if (body[j] != (byte)'\\')
                                {
                                    break;
                                }

                                run++;
                            }

                            if (run % 2 == 0)
                            {
                                continue;
                            }
                        }

                        closed = true;
                        return i + 1;
                    }
                }

                break;
            }
        }

        closed = false;
        return i;
    }

    // Port of parseSquash (gjson.go:1047-1187): skips a nested value and
    // returns the index just past its closing token, or the end of the body
    // when the value is unterminated.
    private static int Squash(ReadOnlySpan<byte> body, int i)
    {
        // The caller has already consumed the leading '{', '[' or '('.
        i++;
        var depth = 1;
        while (i < body.Length)
        {
            var kind = TokenKind(body[i]);
            if (kind == 0)
            {
                i++;
                continue;
            }

            if (kind == 2)
            {
                // Skip the string, honoring escaped quotes; the escape scan
                // only looks at backslashes inside this string.
                i++;
                var start = i;
                var closed = false;
                while (!closed && i < body.Length)
                {
                    if (body[i] != (byte)'"')
                    {
                        i++;
                        continue;
                    }

                    if (body[i - 1] == (byte)'\\')
                    {
                        var run = 0;
                        for (var j = i - 2; j > start - 1; j--)
                        {
                            if (body[j] != (byte)'\\')
                            {
                                break;
                            }

                            run++;
                        }

                        if (run % 2 == 0)
                        {
                            i++;
                            continue;
                        }
                    }

                    // Leave i on the quote; the outer increment moves past it.
                    closed = true;
                }
            }
            else
            {
                // '{', '[', '(' open one level; '}', ']', ')' close one.
                depth += kind - 2;
                if (depth == 0)
                {
                    return i + 1;
                }
            }

            i++;
        }

        return i;
    }

    private static byte TokenKind(byte value) => value switch
    {
        (byte)'"' => 2,
        (byte)'{' or (byte)'(' or (byte)'[' => 3,
        (byte)'}' or (byte)')' or (byte)']' => 1,
        _ => 0,
    };

    // Port of parseNumber (gjson.go:726-736). gjson accepts any run of bytes up
    // to whitespace or a structural delimiter without validating the number.
    private static int ParseNumber(ReadOnlySpan<byte> body, int i)
    {
        i++;
        for (; i < body.Length; i++)
        {
            if (body[i] <= (byte)' '
                || body[i] == (byte)','
                || body[i] == (byte)']'
                || body[i] == (byte)'}')
            {
                return i;
            }
        }

        return i;
    }

    // Port of parseLiteral (gjson.go:738-747): consumes lowercase letters
    // without checking that they spell a JSON literal.
    private static int ParseLiteral(ReadOnlySpan<byte> body, int i)
    {
        i++;
        for (; i < body.Length; i++)
        {
            if (body[i] < (byte)'a' || body[i] > (byte)'z')
            {
                return i;
            }
        }

        return i;
    }

    // Port of unescape (gjson.go:2247-2303). Only applied to object keys that
    // contained a backslash; JSON control bytes and unknown escapes truncate
    // the result, exactly like the reference.
    private static byte[] Unescape(ReadOnlySpan<byte> json)
    {
        var result = new List<byte>(json.Length);
        for (var i = 0; i < json.Length; i++)
        {
            switch (json[i])
            {
                default:
                    if (json[i] < (byte)' ')
                    {
                        return [.. result];
                    }

                    result.Add(json[i]);
                    break;
                case (byte)'\\':
                    i++;
                    if (i >= json.Length)
                    {
                        return [.. result];
                    }

                    switch (json[i])
                    {
                        default:
                            return [.. result];
                        case (byte)'\\':
                            result.Add((byte)'\\');
                            break;
                        case (byte)'/':
                            result.Add((byte)'/');
                            break;
                        case (byte)'b':
                            result.Add((byte)'\b');
                            break;
                        case (byte)'f':
                            result.Add((byte)'\f');
                            break;
                        case (byte)'n':
                            result.Add((byte)'\n');
                            break;
                        case (byte)'r':
                            result.Add((byte)'\r');
                            break;
                        case (byte)'t':
                            result.Add((byte)'\t');
                            break;
                        case (byte)'"':
                            result.Add((byte)'"');
                            break;
                        case (byte)'u':
                            if (i + 5 > json.Length)
                            {
                                return [.. result];
                            }

                            var rune = ParseHexRune(json[(i + 1)..]);
                            i += 5;
                            if (rune is >= 0xD800 and <= 0xDFFF)
                            {
                                if (json.Length - i >= 6
                                    && json[i] == (byte)'\\'
                                    && json[i + 1] == (byte)'u')
                                {
                                    rune = DecodeSurrogatePair(
                                        rune,
                                        ParseHexRune(json[(i + 2)..]));
                                    i += 6;
                                }
                                else
                                {
                                    // Go's utf8.EncodeRune replaces a lone
                                    // surrogate with U+FFFD.
                                    rune = 0xFFFD;
                                }
                            }

                            result.AddRange(Encoding.UTF8.GetBytes(char.ConvertFromUtf32(rune)));
                            i--; // the loop increment lands on the next byte
                            break;
                    }

                    break;
            }
        }

        return [.. result];
    }

    // Go's strconv.ParseUint with base 16: a syntax error yields 0 and the
    // error is discarded (gjson.go:2241-2244).
    private static int ParseHexRune(ReadOnlySpan<byte> json)
    {
        var value = 0;
        for (var i = 0; i < 4; i++)
        {
            var digit = HexDigit(json[i]);
            if (digit < 0)
            {
                return 0;
            }

            value = (value << 4) | digit;
        }

        return value;
    }

    private static int HexDigit(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
        _ => -1,
    };

    // Go's utf16.DecodeRune: an invalid pair yields the replacement character.
    private static int DecodeSurrogatePair(int high, int low)
        => high is >= 0xD800 and <= 0xDBFF && low is >= 0xDC00 and <= 0xDFFF
            ? 0x10000 + ((high - 0xD800) << 10) + (low - 0xDC00)
            : 0xFFFD;
}
