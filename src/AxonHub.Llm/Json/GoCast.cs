using System.Globalization;
using System.Text.Json;

namespace AxonHub.Llm.Json;

/// <summary>
/// Port of the subset of <c>github.com/spf13/cast</c> the reference uses on
/// values it decoded from JSON into <c>any</c>.
/// </summary>
public static class GoCast
{
    /// <summary>
    /// Port of <c>cast.ToString</c>. A value decoded by <c>encoding/json</c>
    /// into <c>any</c> can be a string, bool, float64, or nil; cast has no rule
    /// for objects/arrays and reports an error, which the reference ignores, so
    /// those (and JSON null) yield the empty string.
    /// </summary>
    public static string ToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => GoFloatToString(value.GetDouble()),
        _ => string.Empty,
    };

    /// <summary>
    /// Port of <c>strconv.FormatFloat(v, 'f', -1, 64)</c>: the shortest
    /// round-tripping decimal, always in fixed notation (never an exponent).
    /// </summary>
    public static string GoFloatToString(double value)
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

        // .NET's round-trip format ("R") also yields the shortest round-tripping
        // digits, but switches to exponent notation on small/large magnitudes.
        var text = value.ToString("R", CultureInfo.InvariantCulture);

        var expIndex = text.IndexOfAny(['E', 'e']);
        if (expIndex < 0)
        {
            return text;
        }

        return ExpandExponent(text, expIndex);
    }

    /// <summary>
    /// Port of <c>strconv.ParseFloat(s, 64)</c>. Returns false for both syntax
    /// errors and range errors, exactly as Go's ErrSyntax/ErrRange are treated
    /// as invalid by the reference's callers. Algorithm transcribed from
    /// <c>internal/strconv/atof.go</c> (Go 1.26): special() prefix rules,
    /// readFloat's underscore/mantissa/exponent scan, underscoreOK's state
    /// machine, and atofHex's bit-exact rounding; the decimal value is
    /// correctly rounded to nearest in both runtimes, so double.Parse
    /// reproduces atof64exact/Eisel-Lemire/floatBits results. Verified against
    /// probed ground truth (see AudioEndpointTests.ParseFloatFidelity).
    /// </summary>
    public static bool TryParseFloat(string s, out double value)
    {
        value = 0;

        if (TryParseSpecial(s, out var specialValue, out var specialLength))
        {
            if (specialLength != s.Length)
            {
                return false;
            }

            value = specialValue;
            return true;
        }

        if (!TryReadFloat(s, out var mantissa, out var exp, out var negative, out var trunc, out var hex, out var dp, out var consumed)
            || consumed != s.Length)
        {
            return false;
        }

        if (hex)
        {
            var (hexValue, rangeError) = AtofHex(mantissa, exp, negative, trunc);
            value = hexValue;
            return !rangeError;
        }

        // floatBits short-circuits an all-zero digit string to ±0 before any
        // dp bounds (probed: "0e10000" is a valid zero), so zero needs no
        // bounds check either. For non-zero values, dp > 310 overflows
        // (ErrRange) and dp < -330 underflows to ±0.
        if (mantissa == 0)
        {
            value = negative ? -0.0 : 0.0;
            return true;
        }

        if (dp > 310)
        {
            return false;
        }

        if (dp < -330)
        {
            value = negative ? -0.0 : 0.0;
            return true;
        }

        // Underscore placement was already validated by readFloat, so a plain
        // strip leaves a literal double.Parse handles.
        var literal = s.Replace("_", string.Empty);
        if (!double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return false;
        }

        // Go reports ErrRange (invalid) instead of returning ±Inf.
        if (double.IsInfinity(value))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Port of <c>strconv.ParseFloat(s, 64)</c> for callers that discard the
    /// error, as the reference's header helpers do
    /// (<c>v, _ := strconv.ParseFloat(s, 64)</c>): a syntax error yields 0, an
    /// overflow yields ±Inf and an underflow yields ±0. Go returns those
    /// values together with <c>ErrRange</c>; <see cref="TryParseFloat"/>
    /// keeps its "false for range errors" contract for the callers that treat
    /// them as invalid.
    /// </summary>
    public static double ParseFloatIgnoringError(string s)
    {
        if (TryParseSpecial(s, out var specialValue, out var specialLength))
        {
            return specialLength == s.Length ? specialValue : 0;
        }

        if (!TryReadFloat(s, out var mantissa, out var exp, out var negative, out var trunc, out var hex, out var dp, out var consumed)
            || consumed != s.Length)
        {
            return 0;
        }

        if (hex)
        {
            return AtofHex(mantissa, exp, negative, trunc).Value;
        }

        if (mantissa == 0)
        {
            return negative ? -0.0 : 0.0;
        }

        if (dp > 310)
        {
            // floatBits takes the overflow branch: ±Inf plus ErrRange.
            return negative ? double.NegativeInfinity : double.PositiveInfinity;
        }

        if (dp < -330)
        {
            return negative ? -0.0 : 0.0;
        }

        var literal = s.Replace("_", string.Empty);
        return double.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    /// <summary>
    /// Port of strconv.Quote: Go's exact escaping rules
    /// (\a\b\f\n\r\t\v, \u2028/\u2029, \u007f, \xNN for C0 controls).
    /// </summary>
    public static string GoQuote(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\a': sb.Append("\\a"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\v': sb.Append("\\v"); break;
                case '\u2028': sb.Append("\\u2028"); break;
                case '\u2029': sb.Append("\\u2029"); break;
                case '\u007f': sb.Append("\\u007f"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Port of strconv.special (atof.go:41): signed Inf/Infinity and unsigned
    /// NaN. Returns the prefix length WITHOUT the sign for NaN (so "+nan" and
    /// "-nan" are always syntax errors), exactly like the Go source.
    /// </summary>
    private static bool TryParseSpecial(string s, out double value, out int consumed)
    {
        value = 0;
        consumed = 0;
        if (s.Length == 0)
        {
            return false;
        }

        var rest = s;
        var signLength = 0;
        var sign = 1;
        var first = s[0];
        if (first is '+' or '-')
        {
            if (first == '-')
            {
                sign = -1;
            }

            signLength = 1;
            rest = s[1..];
            first = rest.Length > 0 ? rest[0] : '\0';
        }

        if (first is 'i' or 'I')
        {
            var n = CommonPrefixLenIgnoreCase(rest, "infinity");
            // Anything longer than "inf" is ok, but if we consume more than
            // just "inf", the caller will consume too little and the parse
            // will fail.
            if (n is > 3 and < 8)
            {
                n = 3;
            }

            if (n is 3 or 8)
            {
                value = double.PositiveInfinity * sign;
                consumed = signLength + n;
                return true;
            }
        }
        else if (first is 'n' or 'N')
        {
            if (CommonPrefixLenIgnoreCase(rest, "nan") == 3)
            {
                value = double.NaN;
                consumed = 3;
                return true;
            }
        }

        return false;
    }

    private static int CommonPrefixLenIgnoreCase(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            if (AsciiLower(a[i]) != AsciiLower(b[i]))
            {
                return i;
            }
        }

        return n;
    }

    private static char AsciiLower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;

    /// <summary>
    /// Port of strconv.readFloat (atof.go:171-306). Consumes the sign,
    /// optional 0x prefix, mantissa (digits, hex digits, one dot, underscores)
    /// and the e/p exponent; validates underscore placement via underscoreOK
    /// on the whole consumed prefix. dp is returned separately because the
    /// decimal overflow/underflow bounds in floatBits use it even when the
    /// mantissa is zero.
    /// </summary>
    private static bool TryReadFloat(
        string s,
        out ulong mantissa,
        out int exp,
        out bool negative,
        out bool trunc,
        out bool hex,
        out int dp,
        out int consumed)
    {
        mantissa = 0;
        exp = 0;
        negative = false;
        trunc = false;
        hex = false;
        dp = 0;
        consumed = 0;

        var i = 0;

        // Optional sign.
        if (i < s.Length && s[i] == '+')
        {
            i++;
        }
        else if (i < s.Length && s[i] == '-')
        {
            negative = true;
            i++;
        }

        // Digits.
        var baseValue = 10UL;
        var maxMantDigits = 19; // 10^19 fits in uint64
        var expChar = 'e';
        if (i + 2 < s.Length && s[i] == '0' && AsciiLower(s[i + 1]) == 'x')
        {
            baseValue = 16;
            maxMantDigits = 16; // 16^16 fits in uint64
            i += 2;
            expChar = 'p';
            hex = true;
        }

        var sawdot = false;
        var sawdigits = false;
        var nd = 0;
        var ndMant = 0;
        var underscores = false;

        while (i < s.Length)
        {
            var c = s[i];
            switch (c)
            {
                case '_':
                    underscores = true;
                    i++;
                    continue;
                case '.':
                    if (sawdot)
                    {
                        goto mantissaDone; // Go: break loop
                    }

                    sawdot = true;
                    dp = nd;
                    i++;
                    continue;
                default:
                    if (c is >= '0' and <= '9')
                    {
                        sawdigits = true;
                        if (c == '0' && nd == 0)
                        {
                            // Ignore leading zeros.
                            dp--;
                            i++;
                            continue;
                        }

                        nd++;
                        if (ndMant < maxMantDigits)
                        {
                            mantissa = mantissa * baseValue + (ulong)(c - '0');
                            ndMant++;
                        }
                        else if (c != '0')
                        {
                            trunc = true;
                        }

                        i++;
                        continue;
                    }

                    if (hex && AsciiLower(c) is >= 'a' and <= 'f')
                    {
                        sawdigits = true;
                        nd++;
                        if (ndMant < maxMantDigits)
                        {
                            mantissa = mantissa * 16 + (ulong)(AsciiLower(c) - 'a' + 10);
                            ndMant++;
                        }
                        else
                        {
                            trunc = true;
                        }

                        i++;
                        continue;
                    }

                    break;
            }

            break;
        }

    mantissaDone:
        if (!sawdigits)
        {
            return false;
        }

        if (!sawdot)
        {
            dp = nd;
        }

        if (hex)
        {
            dp *= 4;
            ndMant *= 4;
        }

        // Optional exponent (decimal 'e', or 'p' for hex, which requires it).
        if (i < s.Length && AsciiLower(s[i]) == expChar)
        {
            i++;
            if (i >= s.Length)
            {
                return false;
            }

            var eSign = 1;
            if (s[i] == '+')
            {
                i++;
            }
            else if (s[i] == '-')
            {
                eSign = -1;
                i++;
            }

            if (i >= s.Length || s[i] < '0' || s[i] > '9')
            {
                return false;
            }

            var e = 0;
            for (; i < s.Length && (s[i] is >= '0' and <= '9' || s[i] == '_'); i++)
            {
                if (s[i] == '_')
                {
                    underscores = true;
                    continue;
                }

                if (e < 10000)
                {
                    e = e * 10 + (s[i] - '0');
                }
            }

            dp += e * eSign;
        }
        else if (hex)
        {
            return false;
        }

        if (mantissa != 0)
        {
            exp = dp - ndMant;
        }

        if (underscores && !UnderscoreOk(s[..i]))
        {
            return false;
        }

        consumed = i;
        return true;
    }

    /// <summary>
    /// Port of strconv.underscoreOK (atoi.go:251-296): an underscore must sit
    /// between digits, or between a base prefix and a digit ("0x_1p2" is
    /// valid). The 0x/0b/0o prefix counts as a digit for this purpose.
    /// </summary>
    private static bool UnderscoreOk(string s)
    {
        // saw tracks the last character class: '^' beginning of number,
        // '0' digit or base prefix, '_' underscore, '!' none of the above.
        var saw = '^';
        var i = 0;

        // Optional sign.
        if (s.Length >= 1 && s[0] is '-' or '+')
        {
            s = s[1..];
        }

        // Optional base prefix.
        var hex = false;
        if (s.Length >= 2 && s[0] == '0' && AsciiLower(s[1]) is 'b' or 'o' or 'x')
        {
            i = 2;
            saw = '0'; // base prefix counts as a digit for "underscore as digit separator"
            hex = AsciiLower(s[1]) == 'x';
        }

        // Number proper.
        for (; i < s.Length; i++)
        {
            // Digits are always okay.
            if (s[i] is >= '0' and <= '9' || hex && AsciiLower(s[i]) is >= 'a' and <= 'f')
            {
                saw = '0';
                continue;
            }

            // Underscore must follow digit.
            if (s[i] == '_')
            {
                if (saw != '0')
                {
                    return false;
                }

                saw = '_';
                continue;
            }

            // Underscore must also be followed by digit.
            if (saw == '_')
            {
                return false;
            }

            // Saw non-digit, non-underscore.
            saw = '!';
        }

        return saw != '_';
    }

    /// <summary>
    /// Port of strconv.atofHex (atof.go:496-559) for float64. Reproduces Go's
    /// shift-and-round bit fiddling exactly, including the negative float64Bias
    /// (-1023) convention and the range-error signal for ±Inf.
    /// </summary>
    private static (double Value, bool RangeError) AtofHex(ulong mantissa, int exp, bool negative, bool trunc)
    {
        const int mantBits = 52; // float64MantBits
        const int expBits = 11; // float64ExpBits
        const int bias = -1023; // float64Bias
        var maxExp = (1 << expBits) + bias - 2;
        var minExp = bias + 1;
        exp += mantBits; // mantissa now implicitly divided by 2^mantbits.

        // Shift mantissa and exponent to bring representation into float
        // range: a leading 1-bit followed by mantbits other bits, plus two
        // more for rounding (the bottom one is a sticky bit).
        while (mantissa != 0 && mantissa >> (mantBits + 2) == 0)
        {
            mantissa <<= 1;
            exp--;
        }

        if (trunc)
        {
            mantissa |= 1;
        }

        while (mantissa >> (1 + mantBits + 2) != 0)
        {
            mantissa = mantissa >> 1 | mantissa & 1;
            exp++;
        }

        // If exponent is too negative, denormalize in hopes of making it
        // representable. (The -2 is for the rounding bits.)
        while (mantissa > 1 && exp < minExp - 2)
        {
            mantissa = mantissa >> 1 | mantissa & 1;
            exp++;
        }

        // Round using two bottom bits.
        var round = mantissa & 3;
        mantissa >>= 2;
        round |= mantissa & 1; // round to even (round up if mantissa is odd)
        exp += 2;
        if (round == 3)
        {
            mantissa++;
            if (mantissa == 1UL << (1 + mantBits))
            {
                mantissa >>= 1;
                exp++;
            }
        }

        if (mantissa >> mantBits == 0)
        {
            // Denormal or zero.
            exp = bias;
        }

        var rangeError = false;
        if (exp > maxExp)
        {
            // Infinity and range error.
            mantissa = 1UL << mantBits;
            exp = maxExp + 1;
            rangeError = true;
        }

        var bits = mantissa & ((1UL << mantBits) - 1);
        bits |= (ulong)((exp - bias) & ((1 << expBits) - 1)) << mantBits;
        if (negative)
        {
            bits |= 1UL << mantBits << expBits;
        }

        return (BitConverter.UInt64BitsToDouble(bits), rangeError);
    }

    private static string ExpandExponent(string text, int expIndex)
    {
        var negative = text[0] == '-';
        var mantissa = text[(negative ? 1 : 0)..expIndex];
        var exponent = int.Parse(text[(expIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

        var dot = mantissa.IndexOf('.');
        var integerDigits = dot < 0 ? mantissa.Length : dot;
        var digits = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        var pointPosition = integerDigits + exponent;

        string expanded;
        if (pointPosition <= 0)
        {
            expanded = "0." + new string('0', -pointPosition) + digits;
        }
        else if (pointPosition >= digits.Length)
        {
            expanded = digits + new string('0', pointPosition - digits.Length);
        }
        else
        {
            expanded = digits[..pointPosition] + "." + digits[pointPosition..];
        }

        return negative ? "-" + expanded : expanded;
    }

    /// <summary>
    /// Port of strconv.ParseBool: accepts exactly the Go table — 1, t, T,
    /// TRUE, true, True and 0, f, F, FALSE, false, False. Anything else
    /// (including surrounding whitespace) fails.
    /// </summary>
    public static bool TryParseBool(string s, out bool value)
    {
        switch (s)
        {
            case "1":
            case "t":
            case "T":
            case "TRUE":
            case "true":
            case "True":
                value = true;
                return true;
            case "0":
            case "f":
            case "F":
            case "FALSE":
            case "false":
            case "False":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    /// <summary>
    /// Port of strconv.ParseInt(s, 10, 64): an optional sign followed by ASCII
    /// digits only — no whitespace, no thousands separators, no underscores.
    /// Fails when empty, when any byte is not a digit, or on int64 overflow.
    /// </summary>
    public static bool TryParseInt64(string s, out long value)
    {
        value = 0;

        if (s.Length == 0)
        {
            return false;
        }

        var negative = false;
        var i = 0;
        switch (s[0])
        {
            case '+':
                i = 1;
                break;
            case '-':
                negative = true;
                i = 1;
                break;
        }

        if (i == s.Length)
        {
            return false;
        }

        var limit = negative ? 9223372036854775808UL : 9223372036854775807UL;
        ulong acc = 0;
        for (; i < s.Length; i++)
        {
            var c = s[i];
            if (c is < '0' or > '9')
            {
                return false;
            }

            acc = (acc * 10) + (uint)(c - '0');
            if (acc > limit)
            {
                return false;
            }
        }

        value = negative ? unchecked(-(long)acc) : (long)acc;
        return true;
    }
}
