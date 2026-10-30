using System.Globalization;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Llm.Http;

/// <summary>
/// Port of the Retry-After helpers in llm/httpclient/errors.go:10-76: a 429
/// carrying a <c>Retry-After</c> header is the only upstream signal that cools a
/// channel down.
/// </summary>
public static class HttpClientErrors
{
    /// <summary>Cooldown when Retry-After is missing or unparsable.</summary>
    public const int DefaultRetryAfterSeconds = 60;

    /// <summary>Upper bound so a channel never disappears for long.</summary>
    public static readonly TimeSpan MaxRetryAfterDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// True when the error is a 429 with a non-empty <c>Retry-After</c> header.
    /// </summary>
    public static bool HasRetryAfterHeader(Exception? error)
    {
        var httpError = GoErrors.As<HttpClientError>(error);
        if (httpError is not { StatusCode: 429 } || httpError.Headers is null)
        {
            return false;
        }

        return HttpClientUtils.HeaderGet(httpError.Headers, "Retry-After").Length != 0;
    }

    /// <summary>
    /// Parses the Retry-After header of a 429 per RFC 7231 (delta-seconds or
    /// HTTP-date). Returns <see langword="false"/> when the error is not a 429.
    /// A missing or invalid value yields the 60-second default; the result is
    /// capped at five minutes. An already-elapsed HTTP-date yields zero, exactly
    /// like the reference.
    /// </summary>
    public static bool TryParseRetryAfter(Exception? error, DateTimeOffset now, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;

        var httpError = GoErrors.As<HttpClientError>(error);
        if (httpError is not { StatusCode: 429 })
        {
            return false;
        }

        var value = HttpClientUtils.HeaderGet(httpError.Headers, "Retry-After");
        if (value.Length == 0)
        {
            duration = TimeSpan.FromSeconds(DefaultRetryAfterSeconds);

            return true;
        }

        // 1. Delta-seconds (strconv.Atoi: optional sign, no surrounding space).
        if (long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds))
        {
            duration = ClampSeconds(seconds);

            return true;
        }

        // 2. HTTP-date (http.ParseTime: RFC1123, RFC850 and ANSI C layouts).
        if (TryParseHttpDate(value, out var target))
        {
            var remaining = target - now;
            duration = remaining <= TimeSpan.Zero
                ? TimeSpan.Zero
                : remaining > MaxRetryAfterDuration ? MaxRetryAfterDuration : remaining;

            return true;
        }

        // 3. Unparsable: default cooldown.
        duration = TimeSpan.FromSeconds(DefaultRetryAfterSeconds);

        return true;
    }

    private static readonly string[] HttpDateFormats =
    [
        "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
        "dddd, dd-MMM-yy HH:mm:ss 'GMT'",
        "ddd MMM d HH:mm:ss yyyy",
    ];

    private static bool TryParseHttpDate(string value, out DateTimeOffset parsed)
        => DateTimeOffset.TryParseExact(
            value,
            HttpDateFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);

    private static TimeSpan ClampSeconds(long seconds)
    {
        if (seconds <= 0)
        {
            return TimeSpan.Zero;
        }

        // Compare in seconds before constructing the TimeSpan so a huge value
        // cannot overflow (the reference caps at five minutes as well).
        return seconds >= (long)MaxRetryAfterDuration.TotalSeconds
            ? MaxRetryAfterDuration
            : TimeSpan.FromSeconds(seconds);
    }
}
