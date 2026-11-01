using System.Globalization;

namespace AxonHub.Llm.Json;

/// <summary>
/// Port of the two <c>time.Time</c> renderings the reference puts on the wire:
/// <c>MarshalJSON</c> (RFC 3339 with nanosecond precision, used by the Anthropic
/// models list) and <c>Unix()</c> (used by the OpenAI models list).
/// </summary>
/// <remarks>
/// PostgreSQL <c>timestamptz</c> reaches the port as a UTC
/// <see cref="DateTime"/> with 100ns ticks; Go's <c>time.Time</c> may carry
/// nanosecond precision, but no stored value can exceed microseconds, so the
/// seven-digit fraction below is equivalent for every value the port reads.
/// </remarks>
public static class GoTime
{
    /// <summary>
    /// Go <c>time.Time.MarshalJSON</c> with the <c>time.RFC3339Nano</c> layout:
    /// the fractional second keeps its significant digits (trailing zeros
    /// dropped, the dot omitted when the fraction is zero) and a UTC value ends
    /// in <c>Z</c>.
    /// </summary>
    public static string Rfc3339Nano(DateTime value)
    {
        var utc = ToUtc(value);

        var text = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var fraction = utc.Ticks % TimeSpan.TicksPerSecond;
        if (fraction != 0)
        {
            text += "." + fraction.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        }

        return text + "Z";
    }

    /// <summary>Go <c>time.Time.Unix()</c>: whole seconds since the Unix epoch.</summary>
    public static long UnixSeconds(DateTime value)
        => new DateTimeOffset(ToUtc(value)).ToUnixTimeSeconds();

    private static DateTime ToUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            // timestamptz values arrive as UTC; test doubles may omit the kind.
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}
