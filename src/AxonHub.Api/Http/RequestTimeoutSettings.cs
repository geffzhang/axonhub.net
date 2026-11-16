using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of conf's <c>server.request_timeout</c> and
/// <c>server.llm_request_timeout</c> (conf/conf.go:223-224). Both default to Go
/// duration strings and are parsed with <c>time.ParseDuration</c> by the
/// customized decode hook (conf/conf.go:190-194).
/// </summary>
public sealed record RequestTimeoutSettings(TimeSpan RequestTimeout, TimeSpan LlmRequestTimeout)
{
    /// <summary>conf/conf.go:223: server.request_timeout defaults to "30s".</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>conf/conf.go:224: server.llm_request_timeout defaults to "600s".</summary>
    public static readonly TimeSpan DefaultLlmRequestTimeout = TimeSpan.FromSeconds(600);

    /// <summary>
    /// Reads both keys from the port's configuration convention: the
    /// <c>Server:</c> section (config.yml / appsettings) first, then the
    /// reference's <c>AXONHUB_SERVER_*</c> environment spelling. A present but
    /// empty value is duration zero, matching the decode hook's TrimSpace branch.
    /// </summary>
    public static RequestTimeoutSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new RequestTimeoutSettings(
            ReadDuration(
                configuration,
                "Server:RequestTimeout",
                "AXONHUB_SERVER_REQUEST_TIMEOUT",
                DefaultRequestTimeout),
            ReadDuration(
                configuration,
                "Server:LlmRequestTimeout",
                "AXONHUB_SERVER_LLM_REQUEST_TIMEOUT",
                DefaultLlmRequestTimeout));
    }

    private static TimeSpan ReadDuration(
        IConfiguration configuration,
        string key,
        string environmentKey,
        TimeSpan fallback)
    {
        var value = configuration[key];
        if (value is null)
        {
            value = configuration[environmentKey];
        }

        if (value is null)
        {
            return fallback;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return TimeSpan.Zero;
        }

        return GoDurationParser.Parse(value);
    }
}

/// <summary>
/// Port of the <c>time.Duration</c> branch of Go's <c>time.ParseDuration</c>,
/// used by the configuration decode hook. The parser mirrors Go's grammar
/// (optional sign followed by one or more decimal number + unit pairs,
/// <c>"0"</c> as a special case) and its two error family texts.
/// </summary>
internal static class GoDurationParser
{
    private const long Nanosecond = 1;
    private const long Microsecond = 1_000;
    private const long Millisecond = 1_000_000;
    private const long Second = 1_000_000_000;
    private const long Minute = 60 * Second;
    private const long Hour = 60 * Minute;

    private static readonly Dictionary<string, long> Units = new(StringComparer.Ordinal)
    {
        ["ns"] = Nanosecond,
        ["us"] = Microsecond,
        ["µs"] = Microsecond, // U+00B5 micro sign.
        ["μs"] = Microsecond, // U+03BC Greek small letter mu.
        ["ms"] = Millisecond,
        ["s"] = Second,
        ["m"] = Minute,
        ["h"] = Hour,
    };

    public static TimeSpan Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var original = text;
        var index = 0;
        var negative = false;

        if (index < text.Length && (text[index] == '-' || text[index] == '+'))
        {
            negative = text[index] == '-';
            index++;
        }

        // Go special-cases exactly "0" after the sign.
        if (index < text.Length && text[index] == '0' && index + 1 == text.Length)
        {
            return TimeSpan.Zero;
        }

        if (index == text.Length)
        {
            throw InvalidDuration(original);
        }

        Int128 total = 0;

        while (index < text.Length)
        {
            if (text[index] != '.' && (text[index] < '0' || text[index] > '9'))
            {
                throw InvalidDuration(original);
            }

            var beforeStart = index;
            var whole = LeadingInt(text, ref index, original);
            var hasWhole = index != beforeStart;

            Int128 fractionValue = 0;
            Int128 fractionScale = 1;
            var hasFraction = false;
            if (index < text.Length && text[index] == '.')
            {
                index++;
                var fractionStart = index;
                LeadingFraction(text, ref index, out fractionValue, out fractionScale);
                hasFraction = index != fractionStart;
            }

            if (!hasWhole && !hasFraction)
            {
                throw InvalidDuration(original);
            }

            var unitStart = index;
            while (index < text.Length && text[index] != '.' && (text[index] < '0' || text[index] > '9'))
            {
                index++;
            }

            if (unitStart == index)
            {
                throw new FormatException(
                    "time: missing unit in duration " + GoQuote(original));
            }

            var unitText = text[unitStart..index];
            if (!Units.TryGetValue(unitText, out var unit))
            {
                throw new FormatException(
                    "time: unknown unit " + GoQuote(unitText)
                    + " in duration " + GoQuote(original));
            }

            // Go checks v > 1<<63/unit before multiplying into int64.
            if (whole > (Int128)long.MaxValue / unit)
            {
                throw InvalidDuration(original);
            }

            var part = whole * unit;
            if (hasFraction)
            {
                // float64 mirrors Go's nanosecond-accurate fractional units.
                part += (Int128)((double)fractionValue * unit / (double)fractionScale);
            }

            total += part;
            if (total > long.MaxValue)
            {
                throw InvalidDuration(original);
            }
        }

        if (negative)
        {
            total = -total;
        }

        // Go durations are int64 nanoseconds; TimeSpan ticks are 100ns. The
        // configuration values are whole seconds; a sub-tick remainder has no
        // observable timeout effect.
        return TimeSpan.FromTicks((long)(total / 100));
    }

    private static Int128 LeadingInt(string text, ref int index, string original)
    {
        Int128 value = 0;
        while (index < text.Length && text[index] >= '0' && text[index] <= '9')
        {
            var digit = text[index] - '0';
            if (value > (Int128.MaxValue - digit) / 10)
            {
                throw InvalidDuration(original);
            }

            value = value * 10 + digit;
            index++;
        }

        // Go's leadingInt caps at 1<<63 and reports overflow; any value that
        // large fails the unit multiplication below, so clamp here.
        return value;
    }

    private static void LeadingFraction(
        string text,
        ref int index,
        out Int128 value,
        out Int128 scale)
    {
        value = 0;
        scale = 1;
        var overflow = false;
        while (index < text.Length && text[index] >= '0' && text[index] <= '9')
        {
            if (!overflow)
            {
                var digit = text[index] - '0';
                if (value > (Int128)(long.MaxValue / 10))
                {
                    overflow = true;
                }
                else
                {
                    value = value * 10 + digit;
                    scale *= 10;
                }
            }

            index++;
        }
    }

    private static FormatException InvalidDuration(string value)
        => new("time: invalid duration " + GoQuote(value));

    /// <summary>Port of Go's <c>strconv.Quote</c> for the characters that can
    /// appear in a duration; printable Unicode is left untouched.</summary>
    private static string GoQuote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (Rune.IsControl(rune) || rune.Value == 0x7f)
                    {
                        builder.Append("\\x");
                        builder.Append(rune.Value.ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(rune.ToString());
                    }

                    break;
            }
        }

        builder.Append('"');
        return builder.ToString();
    }
}
