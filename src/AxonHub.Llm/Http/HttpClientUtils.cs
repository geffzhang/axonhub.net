using System.Text;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Http;

/// <summary>
/// Port of llm/httpclient/utils.go and the request-building half of client.go.
/// Header handling mirrors Go's <c>net/http</c>: canonical keys, first-value
/// reads, and the same set of headers that are never forwarded upstream.
/// </summary>
public static class HttpClientUtils
{
    /// <summary>Headers the Go stdlib manages itself.</summary>
    private static readonly HashSet<string> LibManagedHeaders = new(StringComparer.Ordinal)
    {
        "Content-Length",
        "Transfer-Encoding",
        "Accept-Encoding",
        "Host",
    };

    private static readonly HashSet<string> BlockedHeaders = new(StringComparer.Ordinal)
    {
        "Content-Type",
        "Accept",
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Connection",
        "Te",
        "Trailer",
        "Upgrade",
        "X-Channel-Id",
        "X-Project-Id",
        "X-Real-Ip",
        "X-Forwarded-For",
        "X-Forwarded-Proto",
        "X-Forwarded-Host",
        "X-Forwarded-Port",
        "X-Forwarded-Server",
        "Accept-Language",
        "Dnt",
        "Origin",
        "Referer",
        "Sec-Fetch-Dest",
        "Sec-Fetch-Mode",
        "Sec-Fetch-Site",
        "Sec-Fetch-User",
        "Sec-Ch-Ua",
        "Sec-Ch-Ua-Mobile",
        "Sec-Ch-Ua-Platform",
        "User-Agent",
        "Ah-Trace-Id",
        "Ah-Thread-Id",
        "X-Initiator",
    };

    private static readonly string[] BlockedHeaderPrefixes = ["Cf-", "Cdn-"];

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.Ordinal)
    {
        "Authorization",
        "Api-Key",
        "X-Api-Key",
        "X-Api-Secret",
        "X-Api-Token",
        "X-Goog-Api-Key",
        "X-Google-Api-Key",
        "Cookie",
        "Set-Cookie",
        "Proxy-Authorization",
        "Www-Authenticate",
    };

    private static readonly HashSet<string> MergeWithAppendHeaders = new(StringComparer.Ordinal);

    /// <summary>Port of textproto.CanonicalMIMEHeaderKey.</summary>
    public static string CanonicalHeaderKey(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return key;
        }

        // Quick check for canonical encoding; bail out on invalid bytes.
        var upper = true;
        for (var i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (!IsValidHeaderFieldByte(c))
            {
                return key;
            }

            if ((upper && c is >= 'a' and <= 'z') || (!upper && c is >= 'A' and <= 'Z'))
            {
                return CanonicalizeHeaderKey(key);
            }

            upper = c == '-';
        }

        return key;
    }

    private static bool IsValidHeaderFieldByte(char c)
        => c is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    private static string CanonicalizeHeaderKey(string key)
    {
        var chars = key.ToCharArray();
        var upper = true;
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (upper && c is >= 'a' and <= 'z')
            {
                chars[i] = (char)(c - 32);
            }
            else if (!upper && c is >= 'A' and <= 'Z')
            {
                chars[i] = (char)(c + 32);
            }

            upper = c == '-';
        }

        return new string(chars);
    }

    public static bool IsSensitiveHeader(string key)
        => SensitiveHeaders.Contains(CanonicalHeaderKey(key));

    /// <summary>Port of isBlockedHeader; the key must already be canonical.</summary>
    public static bool IsBlockedHeader(string key)
    {
        if (BlockedHeaders.Contains(key))
        {
            return true;
        }

        foreach (var prefix in BlockedHeaderPrefixes)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static void HeaderSet(Dictionary<string, List<string>> headers, string key, string value)
        => headers[CanonicalHeaderKey(key)] = [value];

    public static string HeaderGet(Dictionary<string, List<string>>? headers, string key)
    {
        if (headers is null)
        {
            return string.Empty;
        }

        return headers.TryGetValue(CanonicalHeaderKey(key), out var values) && values.Count > 0
            ? values[0]
            : string.Empty;
    }

    public static void HeaderAdd(Dictionary<string, List<string>> headers, string key, string value)
    {
        var canonical = CanonicalHeaderKey(key);
        if (!headers.TryGetValue(canonical, out var values))
        {
            values = [];
            headers[canonical] = values;
        }

        values.Add(value);
    }

    public static void HeaderDel(Dictionary<string, List<string>> headers, string key)
        => headers.Remove(CanonicalHeaderKey(key));

    /// <summary>Port of MergeInboundRequest.</summary>
    public static HttpRequestData MergeInboundRequest(HttpRequestData dest, HttpRequestData? src)
    {
        if (src is null || ((src.Headers is null || src.Headers.Count == 0) && (src.Query is null || src.Query.Count == 0)))
        {
            return dest;
        }

        dest.Headers = MergeHttpHeaders(dest.Headers, src.Headers);

        if (!dest.SkipInboundQueryMerge)
        {
            dest.Query = MergeHttpQuery(dest.Query, src.Query);
        }

        return dest;
    }

    /// <summary>
    /// Port of MergeHTTPQuery: source keys only fill gaps, they never overwrite.
    /// </summary>
    public static Dictionary<string, List<string>>? MergeHttpQuery(
        Dictionary<string, List<string>>? dest,
        Dictionary<string, List<string>>? src)
    {
        if (src is null || src.Count == 0)
        {
            return dest;
        }

        dest ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (key, values) in src)
        {
            if (!dest.ContainsKey(key))
            {
                dest[key] = values;
            }
        }

        return dest;
    }

    /// <summary>
    /// Port of MergeHTTPHeaders: blocked, sensitive and library-managed headers
    /// are never merged; everything else overwrites the destination.
    /// </summary>
    public static Dictionary<string, List<string>> MergeHttpHeaders(
        Dictionary<string, List<string>>? dest,
        Dictionary<string, List<string>>? src)
    {
        dest ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (src is null)
        {
            return dest;
        }

        foreach (var (key, values) in src)
        {
            if (IsSensitiveHeader(key) || LibManagedHeaders.Contains(key) || IsBlockedHeader(key))
            {
                continue;
            }

            if (MergeWithAppendHeaders.Contains(key) && dest.TryGetValue(key, out var existing))
            {
                var merged = new List<string>(existing);
                foreach (var value in values)
                {
                    if (!merged.Contains(value, StringComparer.Ordinal))
                    {
                        merged.Add(value);
                    }
                }

                dest[key] = merged;
            }
            else
            {
                dest[key] = values;
            }
        }

        return dest;
    }

    /// <summary>Port of FinalizeAuthHeaders: write auth into headers, clear the field.</summary>
    public static HttpRequestData FinalizeAuthHeaders(HttpRequestData req)
    {
        if (req.Auth is null)
        {
            return req;
        }

        try
        {
            ApplyAuth(req.Headers ??= new Dictionary<string, List<string>>(StringComparer.Ordinal), req.Auth);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException("failed to apply authentication: " + ex.Message, ex);
        }

        req.Auth = null;

        return req;
    }

    /// <summary>Port of applyAuth.</summary>
    public static void ApplyAuth(Dictionary<string, List<string>> headers, AuthConfig auth)
    {
        switch (auth.Type)
        {
            case AuthTypes.Bearer:
                if (string.IsNullOrEmpty(auth.ApiKey))
                {
                    throw new InvalidOperationException("bearer token is required");
                }

                HeaderSet(headers, "Authorization", "Bearer " + auth.ApiKey);
                break;

            case AuthTypes.ApiKey:
                if (string.IsNullOrEmpty(auth.HeaderKey))
                {
                    throw new InvalidOperationException("header key is required");
                }

                HeaderSet(headers, auth.HeaderKey, auth.ApiKey ?? string.Empty);
                break;

            default:
                throw new InvalidOperationException("unsupported auth type: " + auth.Type);
        }
    }

    /// <summary>
    /// Port of BuildHttpRequest (llm/httpclient/client.go:457-507): applies the
    /// default User-Agent, drops library-managed headers, folds in ContentType,
    /// applies auth and appends the query string.
    /// </summary>
    public static BuiltHttpRequest BuildHttpRequest(HttpRequestData request)
    {
        var headers = request.Headers ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(HeaderGet(headers, "User-Agent")))
        {
            HeaderSet(headers, "User-Agent", "axonhub/1.0");
        }

        foreach (var managed in LibManagedHeaders)
        {
            headers.Remove(managed);
        }

        if (!string.IsNullOrEmpty(request.ContentType))
        {
            HeaderSet(headers, "Content-Type", request.ContentType);
        }

        if (request.Auth is not null)
        {
            try
            {
                ApplyAuth(headers, request.Auth);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("failed to apply authentication: " + ex.Message, ex);
            }
        }

        var (baseUrl, rawQuery) = SplitUrl(request.Url);

        if (request.Query is { Count: > 0 })
        {
            if (rawQuery.Length > 0)
            {
                rawQuery += "&";
            }

            rawQuery += EncodeQuery(request.Query);
        }

        var url = rawQuery.Length > 0 ? baseUrl + "?" + rawQuery : baseUrl;

        return new BuiltHttpRequest
        {
            Method = request.Method,
            Url = url,
            Headers = headers,
            Body = request.Body ?? [],
        };
    }

    private static (string BaseUrl, string RawQuery) SplitUrl(string url)
    {
        var idx = url.IndexOf('?', StringComparison.Ordinal);
        return idx < 0 ? (url, string.Empty) : (url[..idx], url[(idx + 1)..]);
    }

    /// <summary>Port of url.Values.Encode: sorted keys, insertion-ordered values.</summary>
    public static string EncodeQuery(Dictionary<string, List<string>> values)
    {
        var keys = values.Keys.ToList();
        keys.Sort(StringComparer.Ordinal);

        var sb = new StringBuilder();
        foreach (var key in keys)
        {
            var escapedKey = QueryEscape(key);
            foreach (var value in values[key])
            {
                if (sb.Length > 0)
                {
                    sb.Append('&');
                }

                sb.Append(escapedKey).Append('=').Append(QueryEscape(value));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Port of <c>(url.URL).Query()</c> — the URL's query decoded by
    /// <c>url.ParseQuery</c>, with every parse error discarded (Go's Query
    /// ignores them). Pairs with an invalid escape or a semicolon separator are
    /// dropped, exactly like the reference's loop.
    /// </summary>
    public static Dictionary<string, List<string>> ParseQuery(string query)
        => ParseQuery(query, out _);

    /// <summary>
    /// Port of <c>url.ParseQuery</c> (net/url/url.go:1026-1075); the error is
    /// the first one the reference would have reported.
    /// </summary>
    public static Dictionary<string, List<string>> ParseQuery(string query, out string? error)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        error = null;

        while (query.Length > 0)
        {
            string part;
            var amp = query.IndexOf('&', StringComparison.Ordinal);
            if (amp < 0)
            {
                part = query;
                query = string.Empty;
            }
            else
            {
                part = query[..amp];
                query = query[(amp + 1)..];
            }

            if (part.Contains(';', StringComparison.Ordinal))
            {
                error ??= "invalid semicolon separator in query";
                continue;
            }

            if (part.Length == 0)
            {
                continue;
            }

            var equals = part.IndexOf('=', StringComparison.Ordinal);
            var rawKey = equals < 0 ? part : part[..equals];
            var rawValue = equals < 0 ? string.Empty : part[(equals + 1)..];

            if (!TryQueryUnescape(rawKey, out var key, out var keyError))
            {
                error ??= keyError;
                continue;
            }

            if (!TryQueryUnescape(rawValue, out var value, out var valueError))
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

    /// <summary>Port of <c>url.QueryUnescape</c> (net/url/url.go:131-165).</summary>
    public static bool TryQueryUnescape(string input, out string value, out string? error)
    {
        value = string.Empty;
        error = null;

        var bytes = Encoding.UTF8.GetBytes(input);
        var output = new byte[bytes.Length];
        var length = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            switch (bytes[index])
            {
                case (byte)'+':
                    output[length++] = (byte)' ';
                    break;
                case (byte)'%':
                    if (index + 2 >= bytes.Length
                        || !IsHexDigit(bytes[index + 1])
                        || !IsHexDigit(bytes[index + 2]))
                    {
                        var end = Math.Min(index + 3, bytes.Length);
                        error = "invalid URL escape "
                            + GoCast.GoQuote(Encoding.UTF8.GetString(bytes, index, end - index));
                        return false;
                    }

                    output[length++] = (byte)((HexDigitValue(bytes[index + 1]) << 4)
                        | HexDigitValue(bytes[index + 2]));
                    index += 2;
                    break;
                default:
                    output[length++] = bytes[index];
                    break;
            }
        }

        value = Encoding.UTF8.GetString(output, 0, length);
        return true;
    }

    private static bool IsHexDigit(byte value)
        => value is >= (byte)'0' and <= (byte)'9'
            or >= (byte)'a' and <= (byte)'f'
            or >= (byte)'A' and <= (byte)'F';

    private static int HexDigitValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0',
        >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        _ => value - 'A' + 10,
    };

    /// <summary>Port of url.QueryEscape.</summary>
    public static string QueryEscape(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b is (>= (byte)'A' and <= (byte)'Z')
                or (>= (byte)'a' and <= (byte)'z')
                or (>= (byte)'0' and <= (byte)'9')
                or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
            {
                sb.Append((char)b);
            }
            else if (b == (byte)' ')
            {
                sb.Append('+');
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    /// <summary>Port of transformer.NormalizeBaseURL (llm/transformer/url.go:16-44).</summary>
    public static string NormalizeBaseUrl(string url, string version)
    {
        if (string.IsNullOrEmpty(url))
        {
            return string.Empty;
        }

        if (url.EndsWith('#'))
        {
            return url[..^1].TrimEnd('/');
        }

        if (string.IsNullOrEmpty(version))
        {
            return url.TrimEnd('/');
        }

        if (url.EndsWith("/" + version, StringComparison.Ordinal))
        {
            return url.TrimEnd('/');
        }

        if (url.Contains("/" + version + "/", StringComparison.Ordinal))
        {
            return url.TrimEnd('/');
        }

        return url.TrimEnd('/') + "/" + version;
    }
}

/// <summary>The fully-built outbound request, before transport.</summary>
public sealed class BuiltHttpRequest
{
    public string Method { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public Dictionary<string, List<string>> Headers { get; init; } = new(StringComparer.Ordinal);

    public byte[] Body { get; init; } = [];
}
