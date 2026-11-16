using System.Globalization;

namespace AxonHub.Llm.Json;

/// <summary>
/// Port of Go <c>encoding/json</c>'s float64 formatting: the shortest
/// round-tripping digits, written in fixed notation unless the magnitude is
/// below 1e-6 or at least 1e21, in which case scientific notation is used and
/// a single-digit negative exponent loses its leading zero (e-06 becomes e-6).
/// </summary>
/// <remarks>
/// System.Text.Json writes the same shortest digits but switches notation at
/// different magnitudes and always pads the exponent (5E-06), so fields whose
/// bytes the reference tests lock (usage.cost) must be written through here.
/// </remarks>
public static class GoJsonFloat
{
    public static string Format(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException(
                "Go encoding/json cannot encode NaN or Infinity as a number", nameof(value));
        }

        var negative = value < 0 || double.IsNegative(value);
        var magnitude = Math.Abs(value);
        if (magnitude == 0)
        {
            return negative ? "-0" : "0";
        }

        var text = magnitude.ToString("R", CultureInfo.InvariantCulture);

        var parsedExponent = 0;
        var expIndex = text.IndexOfAny(['E', 'e']);
        if (expIndex >= 0)
        {
            parsedExponent = int.Parse(
                text[(expIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text[..expIndex];
        }

        var dot = text.IndexOf('.');
        var integerDigits = dot < 0 ? text.Length : dot;
        var digits = dot < 0 ? text : text.Remove(dot, 1);

        var leadingZeros = 0;
        while (leadingZeros < digits.Length - 1 && digits[leadingZeros] == '0')
        {
            leadingZeros++;
        }

        digits = digits[leadingZeros..];
        integerDigits -= leadingZeros;

        var trailingZeros = digits.Length;
        while (trailingZeros > 1 && digits[trailingZeros - 1] == '0')
        {
            trailingZeros--;
        }

        digits = digits[..trailingZeros];

        // Position of the leading digit in scientific form: value = d.[digits] × 10^decimalExponent.
        var decimalExponent = integerDigits - 1 + parsedExponent;

        var body = magnitude < 1e-6 || magnitude >= 1e21
            ? Scientific(digits, decimalExponent)
            : Fixed(digits, decimalExponent);

        return negative ? "-" + body : body;
    }

    private static string Fixed(string digits, int decimalExponent)
    {
        var pointPosition = decimalExponent + 1;
        if (pointPosition <= 0)
        {
            return "0." + new string('0', -pointPosition) + digits;
        }

        if (pointPosition >= digits.Length)
        {
            return digits + new string('0', pointPosition - digits.Length);
        }

        return digits[..pointPosition] + "." + digits[pointPosition..];
    }

    private static string Scientific(string digits, int decimalExponent)
    {
        var mantissa = digits.Length == 1 ? digits : digits[0] + "." + digits[1..];
        var sign = decimalExponent < 0 ? "-" : "+";
        var exponentDigits = Math.Abs(decimalExponent).ToString("D2", CultureInfo.InvariantCulture);

        // Go's encoding/json trims the leading zero only for negative
        // two-digit exponents (e-06 -> e-6); positive exponents are never
        // below 21 in the scientific branch, so they need no trimming.
        if (decimalExponent < 0 && exponentDigits.Length == 2 && exponentDigits[0] == '0')
        {
            exponentDigits = exponentDigits[1..];
        }

        return mantissa + "e" + sign + exponentDigits;
    }
}
