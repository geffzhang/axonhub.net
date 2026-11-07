using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// Go <c>time.Time.MarshalJSON</c> for a value that keeps its offset:
    /// non-zero offsets render as <c>±hh:mm</c>, a zero offset as <c>Z</c>.
    /// </summary>
    public static string Rfc3339Nano(DateTimeOffset value)
    {
        var text = value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        var fraction = value.Ticks % TimeSpan.TicksPerSecond;
        if (fraction != 0)
        {
            text += "." + fraction.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        }

        return value.Offset == TimeSpan.Zero
            ? text + "Z"
            : text + value.ToString("zzz", CultureInfo.InvariantCulture);
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

/// <summary>
/// JSON form of a nullable <c>*time.Time</c> field: <c>null</c> stays null and
/// a present value renders (and is read) as Go's <c>time.Time</c>.
/// </summary>
public sealed class GoNullableRfc3339NanoConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Time.UnmarshalJSON: input is not a JSON string");
        }

        var text = reader.GetString() ?? string.Empty;
        return GoTimeParsing.TryParseRfc3339(text)
            ?? throw new JsonException("parsing time \"" + text + "\": cannot parse as RFC 3339");
    }

    public override void Write(
        Utf8JsonWriter writer,
        DateTimeOffset? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(GoTime.Rfc3339Nano(value.Value));
    }
}
