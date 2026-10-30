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
}
