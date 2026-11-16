using System.Globalization;
using System.Text;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Auth.Oidc;

/// <summary>
/// The wire encodings of golang.org/x/oauth2 and the Go standard library that
/// the reference's token client depends on: <c>url.Values.Encode</c>,
/// <c>url.ParseQuery</c>/<c>QueryUnescape</c>, <c>hex.EncodeToString</c>,
/// <c>strconv.Atoi</c> and the <c>time.Time.String</c> rendering used in the
/// expired-token error. Each helper keeps Go's observable text so the login
/// flow produces the reference's messages.
/// </summary>
public static class OidcProtocol
{
    /// <summary>Go <c>url.Values.Encode</c> for single-valued keys (alphabetical).</summary>
    public static string FormEncode(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var keys = values.Keys.ToList();
        keys.Sort(StringComparer.Ordinal);

        var builder = new StringBuilder();
        foreach (var key in keys)
        {
            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(QueryEscape(key)).Append('=').Append(QueryEscape(values[key]));
        }

        return builder.ToString();
    }

    /// <summary>Go <c>url.QueryEscape</c>: space becomes '+', unreserved bytes pass.</summary>
    public static string QueryEscape(string value)
        => AxonHub.Llm.Http.HttpClientUtils.QueryEscape(value);

    /// <summary>
    /// Go <c>url.ParseQuery</c>: splits on '&', skips empty segments and pairs
    /// whose key or value cannot be unescaped, and reports the first unescape
    /// error while keeping the values it did read. A ';' in a segment is an
    /// error in Go 1.17+ and skips that segment.
    /// </summary>
    public static Dictionary<string, List<string>> ParseQuery(string query, out string? error)
    {
        ArgumentNullException.ThrowIfNull(query);

        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        error = null;

        foreach (var segment in query.Split('&'))
        {
            if (segment.Length == 0)
            {
                continue;
            }

            if (segment.Contains(';'))
            {
                error ??= "invalid semicolon separator in query";
                continue;
            }

            var separator = segment.IndexOf('=');
            var rawKey = separator < 0 ? segment : segment[..separator];
            var rawValue = separator < 0 ? string.Empty : segment[(separator + 1)..];

            var key = QueryUnescape(rawKey, out var keyError);
            if (keyError is not null)
            {
                error ??= keyError;
                continue;
            }

            var value = QueryUnescape(rawValue, out var valueError);
            if (valueError is not null)
            {
                error ??= valueError;
                continue;
            }

            if (!values.TryGetValue(key, out var list))
            {
                list = [];
                values[key] = list;
            }

            list.Add(value);
        }

        return values;
    }

    /// <summary>Go <c>url.QueryUnescape</c>.</summary>
    public static string QueryUnescape(string value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(value);

        error = null;
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current == '%')
            {
                if (index + 2 >= value.Length)
                {
                    error = "invalid URL escape " + GoCast.GoQuote(value[index..]);
                    return builder.ToString();
                }

                var high = HexValue(value[index + 1]);
                var low = HexValue(value[index + 2]);
                if (high < 0 || low < 0)
                {
                    error = "invalid URL escape "
                        + GoCast.GoQuote(value.Substring(index, Math.Min(3, value.Length - index)));
                    return builder.ToString();
                }

                builder.Append((char)((high << 4) | low));
                index += 2;
            }
            else if (current == '+')
            {
                builder.Append(' ');
            }
            else
            {
                builder.Append(current);
            }
        }

        return builder.ToString();
    }

    /// <summary>Go <c>hex.EncodeToString</c>: lowercase hex.</summary>
    public static string HexLower(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToHexString(value).ToLowerInvariant();
    }

    /// <summary>
    /// Go <c>strconv.Atoi</c> (int is 64-bit on the reference's platforms):
    /// returns the parsed value, or the exact error text the consumer wraps.
    /// </summary>
    public static bool TryAtoi(string value, out long result, out string? error)
    {
        ArgumentNullException.ThrowIfNull(value);

        result = 0;
        error = null;

        var text = value;
        var negative = false;
        if (text.Length > 0 && (text[0] == '+' || text[0] == '-'))
        {
            negative = text[0] == '-';
            text = text[1..];
        }

        if (text.Length == 0 || !text.All(static character => character is >= '0' and <= '9'))
        {
            error = "strconv.Atoi: parsing " + GoCast.GoQuote(value) + ": invalid syntax";
            return false;
        }

        try
        {
            result = long.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
            if (negative)
            {
                result = -result;
            }
        }
        catch (OverflowException)
        {
            error = "strconv.Atoi: parsing " + GoCast.GoQuote(value) + ": value out of range";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Go <c>time.Time.String</c> for a UTC instant:
    /// <c>2006-01-02 15:04:05.999999999 -0700 MST</c> with trailing fraction
    /// digits trimmed.
    /// </summary>
    public static string GoTimeString(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var text = utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var fraction = utc.Ticks % TimeSpan.TicksPerSecond;
        if (fraction != 0)
        {
            text += "." + fraction.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        }

        return text + " +0000 UTC";
    }

    /// <summary>Go <c>fmt %q</c> of a <c>[]string</c>: <c>["a" "b"]</c>.</summary>
    public static string GoStringSlice(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return "[" + string.Join(" ", values.Select(GoCast.GoQuote)) + "]";
    }

    private static int HexValue(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'a' and <= 'f' => value - 'a' + 10,
        >= 'A' and <= 'F' => value - 'A' + 10,
        _ => -1,
    };
}
