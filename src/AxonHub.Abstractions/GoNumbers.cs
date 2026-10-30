using System.Globalization;

namespace AxonHub.Abstractions;

/// <summary>
/// Go-compatible number formatting shared by the settings parser, the override
/// value renderer and the JSON marshaller.
/// </summary>
/// <remarks>
/// Ports of <c>strconv.FormatFloat</c> with precision -1 (shortest round-trip):
/// <c>'g'</c> is <c>fmt</c>'s default <c>%v</c>, <c>'f'</c> is what sjson uses
/// for float64 values, <c>'e'</c> is used by encoding/json for very small or
/// very large magnitudes, and <see cref="FormatJson"/> reproduces
/// <c>encoding/json</c>'s choice between the two.
/// </remarks>
public static class GoNumbers
{
    /// <summary>Go <c>fmt.Sprint(float64)</c> / <c>strconv.FormatFloat(v, 'g', -1, 64)</c>.</summary>
    public static string FormatG(double value)
    {
        if (!TryDecompose(value, out var negative, out var digits, out var exponent))
        {
            return FormatSpecial(value);
        }

        if (exponent < -4 || exponent >= 6)
        {
            return (negative ? "-" : string.Empty) + Scientific(digits, exponent);
        }

        return (negative ? "-" : string.Empty) + Fixed(digits, exponent);
    }

    /// <summary>Go <c>strconv.FormatFloat(v, 'f', -1, 64)</c>: never exponential.</summary>
    public static string FormatF(double value)
    {
        if (!TryDecompose(value, out var negative, out var digits, out var exponent))
        {
            return FormatSpecial(value);
        }

        return (negative ? "-" : string.Empty) + Fixed(digits, exponent);
    }

    /// <summary>Go <c>strconv.FormatFloat(v, 'e', -1, 64)</c>: always scientific.</summary>
    public static string FormatE(double value)
    {
        if (!TryDecompose(value, out var negative, out var digits, out var exponent))
        {
            return FormatSpecial(value);
        }

        return (negative ? "-" : string.Empty) + Scientific(digits, exponent);
    }

    /// <summary>
    /// Go <c>encoding/json</c>'s float encoding: fixed notation for magnitudes in
    /// [1e-6, 1e21), 'e' otherwise, and the negative exponent's leading zero
    /// dropped ("1e-7" rather than "1e-07").
    /// </summary>
    public static string FormatJson(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            // encoding/json rejects NaN and infinities.
            throw new ArgumentOutOfRangeException(nameof(value), "unsupported JSON number");
        }

        var abs = Math.Abs(value);
        if (abs != 0 && (abs < 1e-6 || abs >= 1e21))
        {
            var scientific = FormatE(value);
            var e = scientific.LastIndexOf('e');
            if (e >= 0 && e + 3 < scientific.Length && scientific[e + 1] == '-' && scientific[e + 2] == '0')
            {
                scientific = scientific.Remove(e + 2, 1);
            }

            return scientific;
        }

        return FormatF(value);
    }

    /// <summary>
    /// Splits a finite non-zero value into its shortest round-trip significant
    /// digits and the base-10 exponent of the first digit, e.g. 0.0012 becomes
    /// digits "12", exponent -3.
    /// </summary>
    private static bool TryDecompose(double value, out bool negative, out string digits, out int exponent)
    {
        negative = false;
        digits = string.Empty;
        exponent = 0;

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }

        if (value == 0)
        {
            return false;
        }

        negative = value < 0;

        // .NET Core 3.0+ "R" is the shortest round-tripping representation.
        var repr = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);

        var exponentPart = 0;
        var eIndex = repr.IndexOfAny(['E', 'e']);
        if (eIndex >= 0)
        {
            exponentPart = int.Parse(repr[(eIndex + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            repr = repr[..eIndex];
        }

        var dot = repr.IndexOf('.', StringComparison.Ordinal);
        var integerLength = dot >= 0 ? dot : repr.Length;
        var mantissa = dot >= 0 ? repr.Remove(dot, 1) : repr;

        var firstSignificant = 0;
        while (firstSignificant < mantissa.Length && mantissa[firstSignificant] == '0')
        {
            firstSignificant++;
        }

        if (firstSignificant == mantissa.Length)
        {
            return false;
        }

        digits = mantissa[firstSignificant..];
        exponent = integerLength - 1 - firstSignificant + exponentPart;
        return true;
    }

    private static string FormatSpecial(double value)
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

        // Zero; Go keeps the sign of negative zero.
        return double.IsNegative(value) ? "-0" : "0";
    }

    /// <summary>Fixed notation with the shortest digit string.</summary>
    private static string Fixed(string digits, int exponent)
    {
        if (exponent >= 0)
        {
            if (digits.Length <= exponent + 1)
            {
                return digits + new string('0', exponent + 1 - digits.Length);
            }

            return digits[..(exponent + 1)] + "." + digits[(exponent + 1)..];
        }

        return "0." + new string('0', -exponent - 1) + digits;
    }

    /// <summary>Scientific notation: one digit, optional fraction, signed two-digit exponent.</summary>
    private static string Scientific(string digits, int exponent)
    {
        var significant = digits.TrimEnd('0');
        if (significant.Length == 0)
        {
            significant = "0";
        }

        var fraction = significant.Length > 1 ? "." + significant[1..] : string.Empty;
        var exponentSign = exponent < 0 ? '-' : '+';
        var absoluteExponent = Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
        return $"{significant[0]}{fraction}e{exponentSign}{absoluteExponent}";
    }
}
