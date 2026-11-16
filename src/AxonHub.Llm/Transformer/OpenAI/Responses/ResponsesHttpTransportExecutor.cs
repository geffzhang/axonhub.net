using System.Globalization;
using System.Text;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.OpenAI.Responses;

/// <summary>
/// Port of responses/http_executor.go: the HTTP/SSE transport wrapper that strips
/// WebSocket-only request metadata before every attempt, and the shared
/// PrepareHTTPTransportRequest helper.
/// </summary>
internal sealed class HttpTransportExecutor(
    IExecutor inner,
    Func<HttpRequestData, HttpRequestData?> finalize) : IExecutor
{
    public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
        => inner.Do(cancellationToken, finalize(request)!);

    public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
        => inner.DoStream(cancellationToken, finalize(request)!);
}

/// <summary>Port of responses/http_executor.go helpers.</summary>
internal static class ResponsesHttpTransport
{
    /// <summary>
    /// Port of PrepareHTTPTransportRequest: removes WebSocket-v2-only request
    /// metadata before an HTTP/SSE attempt. Generic Responses HTTP requests
    /// retain a normal previous_response_id. Codex callers can force removal
    /// because their HTTP endpoint does not support that field.
    /// </summary>
    public static HttpRequestData? PrepareHttpTransportRequest(
        HttpRequestData? request,
        bool stripPreviousResponseId)
    {
        if (request is null)
        {
            return null;
        }

        var (headers, webSocketBetaRemoved) = WithoutWebSocketBeta(request.Headers);
        var body = request.Body;
        var bodyChanged = false;
        if (stripPreviousResponseId)
        {
            (body, bodyChanged) = WithoutPreviousResponseId(body);
        }

        if (!webSocketBetaRemoved && !bodyChanged)
        {
            return request;
        }

        // Go: `cloned := *request` — a shallow struct copy; only the fields
        // that changed are replaced.
        var cloned = request.ShallowClone();
        if (webSocketBetaRemoved)
        {
            cloned.Headers = headers;
        }

        if (bodyChanged)
        {
            cloned.Body = body;
        }

        return cloned;
    }

    /// <summary>Go: <c>withoutWebSocketBeta</c>.</summary>
    internal static (Dictionary<string, List<string>>? Headers, bool Changed) WithoutWebSocketBeta(
        Dictionary<string, List<string>>? headers)
    {
        if (headers is null)
        {
            return (null, false);
        }

        var canonical = HttpClientUtils.CanonicalHeaderKey("OpenAI-Beta");
        if (!headers.TryGetValue(canonical, out var values) || values.Count == 0)
        {
            return (headers, false);
        }

        var filtered = new List<string>(values.Count);
        var changed = false;
        foreach (var value in values)
        {
            var kept = new List<string>();
            foreach (var part in value.Split(','))
            {
                var trimmed = part.Trim();
                if (trimmed.StartsWith("responses_websockets=", StringComparison.OrdinalIgnoreCase))
                {
                    changed = true;
                    continue;
                }

                if (trimmed.Length > 0)
                {
                    kept.Add(trimmed);
                }
            }

            if (kept.Count > 0)
            {
                filtered.Add(string.Join(", ", kept));
            }
        }

        if (!changed)
        {
            return (headers, false);
        }

        var cloned = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (key, headerValues) in headers)
        {
            cloned[key] = [.. headerValues];
        }

        cloned.Remove(canonical);
        if (filtered.Count > 0)
        {
            cloned[canonical] = filtered;
        }

        return (cloned, true);
    }

    /// <summary>
    /// Go: <c>withoutPreviousResponseID</c> — gjson existence check plus an
    /// sjson delete of the top-level member. The raw body is edited in place so
    /// every other byte (number lexemes, escapes, key order) is preserved.
    /// </summary>
    internal static (byte[]? Body, bool Changed) WithoutPreviousResponseId(byte[]? body)
    {
        if (body is not { Length: > 0 })
        {
            return (body, false);
        }

        // gjson inspects raw bytes, but sjson's delete fails on malformed JSON
        // and leaves the body untouched; validate once for both steps.
        if (GoJsonSyntax.Validate(body) is not null)
        {
            return (body, false);
        }

        if (!TryRemoveTopLevelMember(body, "previous_response_id", out var filtered))
        {
            return (body, false);
        }

        return (filtered, true);
    }

    private static bool TryRemoveTopLevelMember(byte[] body, string name, out byte[] result)
    {
        result = body;

        var span = body.AsSpan();
        var i = SkipWhitespace(span, 0);
        if (i >= span.Length || span[i] != (byte)'{')
        {
            return false;
        }

        i++;

        var keyStarts = new List<int>();
        var valueEnds = new List<int>();
        var commasBefore = new List<int>();
        var commasAfter = new List<int>();
        var commaBefore = -1;

        while (true)
        {
            i = SkipWhitespace(span, i);
            if (i >= span.Length)
            {
                return false;
            }

            if (span[i] == (byte)'}')
            {
                break;
            }

            var keyStart = i;
            if (span[i] != (byte)'"' || !TryReadString(span, ref i, out var key))
            {
                return false;
            }

            i = SkipWhitespace(span, i);
            if (i >= span.Length || span[i] != (byte)':')
            {
                return false;
            }

            i = SkipWhitespace(span, i + 1);
            if (!TrySkipValue(span, ref i))
            {
                return false;
            }

            var valueEnd = i;
            var next = SkipWhitespace(span, i);
            var commaAfter = -1;
            if (next < span.Length && span[next] == (byte)',')
            {
                commaAfter = next;
            }

            keyStarts.Add(keyStart);
            valueEnds.Add(valueEnd);
            commasBefore.Add(commaBefore);
            commasAfter.Add(commaAfter);

            if (commaAfter < 0)
            {
                break;
            }

            commaBefore = commaAfter;
            i = commaAfter + 1;
        }

        for (var member = 0; member < keyStarts.Count; member++)
        {
            if (!string.Equals(KeyAt(span, keyStarts[member]), name, StringComparison.Ordinal))
            {
                continue;
            }

            var start = keyStarts[member];
            var end = valueEnds[member];
            if (commasBefore[member] >= 0)
            {
                // Not the first member: drop the separating comma with it.
                start = commasBefore[member];
            }
            else if (commasAfter[member] >= 0)
            {
                // First of several: drop the following comma instead.
                end = commasAfter[member] + 1;
            }

            var remaining = new byte[body.Length - (end - start)];
            body.AsSpan(0, start).CopyTo(remaining);
            body.AsSpan(end).CopyTo(remaining.AsSpan(start));

            result = remaining;
            return true;
        }

        return false;
    }

    /// <summary>Decodes a validated string literal starting at the opening quote.</summary>
    private static bool TryReadString(ReadOnlySpan<byte> span, ref int index, out string value)
    {
        value = string.Empty;
        if (index >= span.Length || span[index] != (byte)'"')
        {
            return false;
        }

        var start = index + 1;
        var i = start;
        var escaped = false;
        while (i < span.Length)
        {
            var b = span[i];
            if (escaped)
            {
                escaped = false;
                i++;
                continue;
            }

            if (b == (byte)'\\')
            {
                escaped = true;
                i++;
                continue;
            }

            if (b == (byte)'"')
            {
                var raw = span[start..i];
                index = i + 1;
                return TryUnescape(raw, out value);
            }

            i++;
        }

        return false;
    }

    private static string KeyAt(ReadOnlySpan<byte> span, int keyStart)
        => TryReadString(span, ref keyStart, out var key) ? key : string.Empty;

    private static bool TryUnescape(ReadOnlySpan<byte> raw, out string value)
    {
        value = string.Empty;
        var escaped = false;
        foreach (var b in raw)
        {
            if (b == (byte)'\\')
            {
                escaped = true;
                break;
            }
        }

        if (!escaped)
        {
            value = Encoding.UTF8.GetString(raw);
            return true;
        }

        var builder = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            var b = raw[i];
            if (b != (byte)'\\')
            {
                builder.Append((char)b);
                continue;
            }

            if (++i >= raw.Length)
            {
                return false;
            }

            switch ((char)raw[i])
            {
                case '"':
                case '\\':
                case '/':
                    builder.Append((char)raw[i]);
                    break;
                case 'b':
                    builder.Append('\b');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case 'u':
                    if (i + 4 >= raw.Length
                        || !ushort.TryParse(
                            Encoding.ASCII.GetString(raw.Slice(i + 1, 4)),
                            NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture,
                            out var code))
                    {
                        return false;
                    }

                    builder.Append((char)code);
                    i += 4;
                    break;
                default:
                    return false;
            }
        }

        value = builder.ToString();
        return true;
    }

    /// <summary>Advances past one JSON value inside an object.</summary>
    private static bool TrySkipValue(ReadOnlySpan<byte> span, ref int index)
    {
        if (index >= span.Length)
        {
            return false;
        }

        var first = span[index];
        if (first == (byte)'"')
        {
            return TryReadString(span, ref index, out _);
        }

        if (first == (byte)'{' || first == (byte)'[')
        {
            var depth = 0;
            var inString = false;
            var escaped = false;
            while (index < span.Length)
            {
                var b = span[index];
                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (b == (byte)'\\')
                    {
                        escaped = true;
                    }
                    else if (b == (byte)'"')
                    {
                        inString = false;
                    }

                    index++;
                    continue;
                }

                if (b == (byte)'"')
                {
                    inString = true;
                }
                else if (b == (byte)'{' || b == (byte)'[')
                {
                    depth++;
                }
                else if (b == (byte)'}' || b == (byte)']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        index++;
                        return true;
                    }
                }

                index++;
            }

            return false;
        }

        var start = index;
        while (index < span.Length)
        {
            var b = span[index];
            if (b is (byte)',' or (byte)'}' or (byte)']'
                or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                break;
            }

            index++;
        }

        return index > start;
    }

    private static int SkipWhitespace(ReadOnlySpan<byte> span, int index)
    {
        while (index < span.Length && span[index] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            index++;
        }

        return index;
    }
}
