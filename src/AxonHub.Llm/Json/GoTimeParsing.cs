using System.Globalization;
using System.Text.RegularExpressions;

namespace AxonHub.Llm.Json;

/// <summary>
/// Port of <c>time.Parse(time.RFC3339, value)</c>. Go's parser accepts a
/// one-or-two-digit hour, an optional fraction (either <c>.</c> or <c>,</c>),
/// offsets up to ±24:60 and year 0000; the values <see cref="DateTimeOffset"/>
/// cannot represent return <c>null</c> (C123).
/// </summary>
public static class GoTimeParsing
{
    /// <summary>
    /// Go's <c>time.RFC3339</c> layout as accepted by <c>time.Parse</c>: four
    /// year digits, fixed-width month/day/minute/second, a one-or-two-digit
    /// hour (layout "15" is not zero padded), an optional fraction with any
    /// number of digits, then "Z" or a sign with fixed-width hour/minute.
    /// </summary>
    private static readonly Regex Rfc3339Regex = new(
        "^([0-9]{4})-([0-9]{2})-([0-9]{2})T([0-9]{1,2}):([0-9]{2}):([0-9]{2})"
        + "(?:[.,]([0-9]+))?(Z|([+-])([0-9]{2}):([0-9]{2}))$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>time.Parse(time.RFC3339, value)</c>, or <c>null</c> for a parse error
    /// or a value outside <see cref="DateTimeOffset"/>'s range.
    /// </summary>
    public static DateTimeOffset? TryParseRfc3339(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var match = Rfc3339Regex.Match(value);
        if (!match.Success)
        {
            return null;
        }

        var year = ParseDigits(match.Groups[1].Value);
        var month = ParseDigits(match.Groups[2].Value);
        var day = ParseDigits(match.Groups[3].Value);
        var hour = ParseDigits(match.Groups[4].Value);
        var minute = ParseDigits(match.Groups[5].Value);
        var second = ParseDigits(match.Groups[6].Value);

        // Go's time.Time supports year 0 (and negative years are not reachable
        // from the four-digit layout); DateTimeOffset starts at year 1 (C123).
        if (year < 1
            || month is < 1 or > 12
            || day < 1
            || day > DateTime.DaysInMonth(year, month)
            || hour > 23
            || minute > 59
            || second > 59)
        {
            return null;
        }

        var offsetSeconds = 0;
        if (match.Groups[8].Value != "Z")
        {
            var offsetHour = ParseDigits(match.Groups[10].Value);
            var offsetMinute = ParseDigits(match.Groups[11].Value);

            // The reference allows 24 hours and 60 minutes in an offset.
            if (offsetHour > 24 || offsetMinute > 60)
            {
                return null;
            }

            offsetSeconds = (offsetHour * 3600) + (offsetMinute * 60);
            if (match.Groups[9].Value == "-")
            {
                offsetSeconds = -offsetSeconds;
            }
        }

        // DateTimeOffset requires an offset within ±14:00.
        if (offsetSeconds < -14 * 3600 || offsetSeconds > 14 * 3600)
        {
            return null;
        }

        var fractionTicks = 0L;
        if (match.Groups[7].Success)
        {
            fractionTicks = FractionTicks(match.Groups[7].Value);
        }

        try
        {
            var parsed = new DateTimeOffset(
                year,
                month,
                day,
                hour,
                minute,
                second,
                TimeSpan.FromSeconds(offsetSeconds));
            return fractionTicks > 0 ? parsed.AddTicks(fractionTicks) : parsed;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static int ParseDigits(string digits)
        => int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>
    /// Go's parseNanoseconds: the first nine fraction digits scaled to
    /// nanoseconds; DateTimeOffset keeps 100ns ticks, so the remainder is
    /// truncated.
    /// </summary>
    private static long FractionTicks(string digits)
    {
        var length = Math.Min(digits.Length, 9);
        var nanoseconds = 0L;
        for (var i = 0; i < length; i++)
        {
            nanoseconds = (nanoseconds * 10) + (digits[i] - '0');
        }

        for (var i = length; i < 9; i++)
        {
            nanoseconds *= 10;
        }

        return nanoseconds / 100;
    }
}
