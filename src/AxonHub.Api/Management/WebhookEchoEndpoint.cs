using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of api.SystemHandlers.WebhookEcho (internal/server/api/system.go:103-130),
/// mounted at POST /openapi/webhook/echo on the openapi group. The response is
/// byte-for-byte the reference's <c>encoding/json</c> output: a fixed field
/// order, Go's HTML escaping, a compacted <c>json.RawMessage</c> body and the
/// encoder's trailing newline.
/// </summary>
public static class WebhookEchoEndpoint
{
    /// <summary>io.LimitReader(c.Request.Body, 1&lt;&lt;20): the reference reads at most 1 MiB.</summary>
    public const int MaxBodyBytes = 1 << 20;

    public static async Task HandleAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var cancellationToken = context.RequestAborted;

        byte[] body;
        try
        {
            body = await ReadLimitedAsync(context.Request.Body, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            await AbortResponse.WriteErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    "failed to read request body",
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";

        if (!IsValidJson(body))
        {
            // json.Encoder marshals the whole struct before writing anything;
            // json.RawMessage.MarshalJSON rejects an invalid payload, so the
            // failed Encode leaves a 200 with an empty body.
            return;
        }

        var payload = BuildResponse(context.Request, body);

        // Go appends the encoder's newline after the object.
        byte[] withNewline = [.. payload, (byte)'\n'];
        await context.Response.Body
            .WriteAsync(withNewline, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadLimitedAsync(
        Stream body,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxBodyBytes];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await body
                .ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total == buffer.Length ? buffer : buffer[..total];
    }

    /// <summary>
    /// Go's <c>json.Valid</c> gate used by <c>json.RawMessage.MarshalJSON</c>
    /// (encoding/json/stream.go:287-304): a single value, at most 10000 levels
    /// deep. System.Text.Json rejects the invalid UTF-8 Go's byte scanner
    /// accepts (C-range difference).
    /// </summary>
    private static bool IsValidJson(byte[] body)
    {
        try
        {
            using var document = JsonDocument.Parse(
                body,
                new JsonDocumentOptions { MaxDepth = 10000 });
            _ = document;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static byte[] BuildResponse(HttpRequest request, byte[] body)
    {
        var query = ParseGoQuery(request.QueryString.Value);
        var headers = CollectGoHeaders(request.Headers);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            buffer,
            new JsonWriterOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }))
        {
            writer.WriteStartObject();
            writer.WriteString("method", request.Method);
            writer.WriteString("path", request.Path.Value ?? "/");
            writer.WritePropertyName("query");
            WriteGoStringMap(writer, query);
            writer.WritePropertyName("headers");
            WriteGoStringMap(writer, headers);
            writer.WritePropertyName("body");
            writer.WriteRawValue(
                GoJson.Compact(body),
                skipInputValidation: true);
            writer.WriteEndObject();
        }

        // encoding/json escapes every string in the response, including the
        // compacted raw body, so one pass over the serialized document matches
        // MarshalJSON's escapeHTML=true behaviour.
        return GoJson.Escape(buffer.ToArray());
    }

    private static void WriteGoStringMap(
        Utf8JsonWriter writer,
        SortedDictionary<string, List<string>> values)
    {
        writer.WriteStartObject();
        foreach (var (key, list) in values)
        {
            writer.WritePropertyName(key);
            writer.WriteStartArray();
            foreach (var value in list)
            {
                writer.WriteStringValue(value);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Port of net/url.ParseQuery (net/url/url.go), the parser behind
    /// <c>c.Request.URL.Query()</c>: split on '&amp;', skip empty segments and
    /// segments containing ';', split at the first '=', and skip a pair whose
    /// key or value fails QueryUnescape. Values keep wire order.
    /// </summary>
    internal static SortedDictionary<string, List<string>> ParseGoQuery(string? rawQuery)
    {
        var result = new SortedDictionary<string, List<string>>(GoStringComparer.Instance);
        if (string.IsNullOrEmpty(rawQuery))
        {
            return result;
        }

        var query = rawQuery[0] == '?' ? rawQuery[1..] : rawQuery;
        while (query.Length > 0)
        {
            string segment;
            var ampersand = query.IndexOf('&');
            if (ampersand < 0)
            {
                segment = query;
                query = string.Empty;
            }
            else
            {
                segment = query[..ampersand];
                query = query[(ampersand + 1)..];
            }

            if (segment.Contains(';'))
            {
                // "invalid semicolon separator in query": the pair is skipped.
                continue;
            }

            if (segment.Length == 0)
            {
                continue;
            }

            var equals = segment.IndexOf('=');
            var rawKey = equals < 0 ? segment : segment[..equals];
            var rawValue = equals < 0 ? string.Empty : segment[(equals + 1)..];

            var key = GoQueryUnescape(rawKey);
            if (key is null)
            {
                continue;
            }

            var value = GoQueryUnescape(rawValue);
            if (value is null)
            {
                continue;
            }

            if (!result.TryGetValue(key, out var list))
            {
                list = [];
                result[key] = list;
            }

            list.Add(value);
        }

        return result;
    }

    /// <summary>Port of url.QueryUnescape: '+' is a space, %XX is decoded.</summary>
    internal static string? GoQueryUnescape(string value)
    {
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '+':
                    bytes.Add((byte)' ');
                    break;
                case '%':
                    if (i + 2 >= value.Length
                        || !TryHex(value[i + 1], out var high)
                        || !TryHex(value[i + 2], out var low))
                    {
                        return null;
                    }

                    bytes.Add((byte)((high << 4) | low));
                    i += 2;
                    break;
                default:
                    if (c > 0x7F)
                    {
                        // QueryUnescape copies raw bytes verbatim; a non-ASCII
                        // char in the .NET string is already decoded UTF-8, so
                        // re-encode it (surrogate pairs together).
                        if (char.IsHighSurrogate(c)
                            && i + 1 < value.Length
                            && char.IsLowSurrogate(value[i + 1]))
                        {
                            bytes.AddRange(Encoding.UTF8.GetBytes(value.Substring(i, 2)));
                            i++;
                        }
                        else
                        {
                            bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
                        }
                    }
                    else
                    {
                        bytes.Add((byte)c);
                    }

                    break;
            }
        }

        // Go carries arbitrary bytes in strings; encoding/json replaces
        // invalid UTF-8 with U+FFFD when marshaling.
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool TryHex(char c, out int value)
    {
        if (c is >= '0' and <= '9')
        {
            value = c - '0';
            return true;
        }

        if (c is >= 'a' and <= 'f')
        {
            value = c - 'a' + 10;
            return true;
        }

        if (c is >= 'A' and <= 'F')
        {
            value = c - 'A' + 10;
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Go's request Header map: Host and Transfer-Encoding never appear there
    /// (they live in Request.Host / Request.TransferEncoding), keys are
    /// canonicalized by textproto.CanonicalMIMEHeaderKey.
    /// </summary>
    private static SortedDictionary<string, List<string>> CollectGoHeaders(
        IHeaderDictionary headers)
    {
        var result = new SortedDictionary<string, List<string>>(GoStringComparer.Instance);

        foreach (var header in headers)
        {
            var name = CanonicalMimeHeaderKey(header.Key);
            if (string.Equals(name, "Host", StringComparison.Ordinal)
                || string.Equals(name, "Transfer-Encoding", StringComparison.Ordinal))
            {
                continue;
            }

            if (!result.TryGetValue(name, out var list))
            {
                list = [];
                result[name] = list;
            }

            foreach (var value in header.Value)
            {
                if (value is not null)
                {
                    list.Add(value);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Port of textproto.CanonicalMIMEHeaderKey: uppercase the first letter and
    /// every letter after '-', lowercase the rest; a key containing an invalid
    /// header-field byte is returned unchanged.
    /// </summary>
    internal static string CanonicalMimeHeaderKey(string value)
    {
        foreach (var c in value)
        {
            if (!IsTokenChar(c))
            {
                return value;
            }
        }

        var chars = value.ToCharArray();
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

    private static bool IsTokenChar(char c)
        => c is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*'
            or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    /// <summary>Go string ordering is byte-wise over UTF-8 (encoding/json map key sort).</summary>
    internal sealed class GoStringComparer : IComparer<string>
    {
        public static GoStringComparer Instance { get; } = new();

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var left = Encoding.UTF8.GetBytes(x);
            var right = Encoding.UTF8.GetBytes(y);
            var length = Math.Min(left.Length, right.Length);
            for (var i = 0; i < length; i++)
            {
                var diff = left[i] - right[i];
                if (diff != 0)
                {
                    return diff;
                }
            }

            return left.Length - right.Length;
        }
    }
}
