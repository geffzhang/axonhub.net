using System.Security.Cryptography;
using System.Text;

namespace AxonHub.Llm.Transformer.Shared;

/// <summary>Raised when a multipart body cannot be parsed.</summary>
public sealed class GoMultipartFormatException : Exception
{
    public GoMultipartFormatException(string message)
        : base(message)
    {
    }
}

/// <summary>One parsed part of a <c>multipart/form-data</c> body.</summary>
public sealed class GoMultipartPart
{
    /// <summary>Go: <c>Part.FormName()</c>, empty when the disposition is unusable.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Go: <c>Part.FileName()</c>; empty for plain form fields. The value is
    /// reduced to its last path segment, as Go's <c>filepath.Base</c> does.
    /// </summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>The first <c>Content-Type</c> header value, or null when absent.</summary>
    public string? ContentType { get; init; }

    /// <summary>The decoded part body.</summary>
    public byte[] Data { get; init; } = [];
}

/// <summary>
/// Port of the reading half of Go's <c>mime/multipart.Reader</c> — just enough
/// for the OpenAI images inbound transformer (multipart/form-data with CRLF
/// delimiters, header folding, quoted disposition parameters).
/// </summary>
public sealed class GoMultipartReader
{
    private readonly byte[] _body;
    private readonly byte[] _dashBoundary;
    private readonly byte[] _crlfBoundary;
    private int _pos = -1;
    private bool _finished;
    private string? _lastHeaderKey;

    public GoMultipartReader(byte[] body, string boundary)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrEmpty(boundary);

        _body = body;
        _dashBoundary = Encoding.ASCII.GetBytes("--" + boundary);
        _crlfBoundary = Encoding.ASCII.GetBytes("\r\n--" + boundary);
    }

    /// <summary>
    /// Returns the next part, or <see langword="null"/> at the closing boundary.
    /// Throws <see cref="GoMultipartFormatException"/> on malformed input; the
    /// reference folds every such error into "failed to read multipart".
    /// </summary>
    public GoMultipartPart? NextPart()
    {
        if (_finished)
        {
            return null;
        }

        if (_pos < 0)
        {
            _pos = IndexOfFirstBoundary();
            if (_pos < 0)
            {
                // A body with no boundary at all is EOF for Go's reader, which
                // the reference treats as "no parts" rather than an error.
                _finished = true;
                return null;
            }
        }

        var after = _pos + _dashBoundary.Length;
        if (after + 1 < _body.Length && _body[after] == (byte)'-' && _body[after + 1] == (byte)'-')
        {
            _finished = true;
            return null;
        }

        // Go tolerates transport padding (spaces/tabs) between the boundary
        // and its terminating CRLF.
        while (after < _body.Length && (_body[after] == (byte)' ' || _body[after] == (byte)'\t'))
        {
            after++;
        }

        if (after + 1 >= _body.Length || _body[after] != (byte)'\r' || _body[after + 1] != (byte)'\n')
        {
            throw new GoMultipartFormatException("boundary is not followed by CRLF");
        }

        _pos = after + 2;
        _lastHeaderKey = null;
        var headers = ReadHeaders();

        var dataStart = _pos;
        var delimiter = IndexOfCrlfBoundary(dataStart);
        if (delimiter < 0)
        {
            throw new GoMultipartFormatException("missing terminating boundary");
        }

        var data = _body.AsSpan(dataStart, delimiter - dataStart).ToArray();
        _pos = delimiter + 2; // points at "--<boundary>"

        headers.TryGetValue("Content-Disposition", out var disposition);
        headers.TryGetValue("Content-Type", out var contentType);
        var (name, fileName) = ParseContentDisposition(disposition);

        return new GoMultipartPart
        {
            Name = name,
            FileName = fileName,
            ContentType = contentType,
            Data = data,
        };
    }

    private int IndexOfFirstBoundary()
    {
        for (var i = 0; i + _dashBoundary.Length <= _body.Length; i++)
        {
            if (!_body.AsSpan(i, _dashBoundary.Length).SequenceEqual(_dashBoundary))
            {
                continue;
            }

            // Go skips preamble lines: the boundary must start the body or follow CRLF.
            if (i == 0 || (i >= 2 && _body[i - 2] == (byte)'\r' && _body[i - 1] == (byte)'\n'))
            {
                return i;
            }
        }

        return -1;
    }

    private int IndexOfCrlfBoundary(int from)
    {
        for (var i = from; i + _crlfBoundary.Length <= _body.Length; i++)
        {
            if (_body.AsSpan(i, _crlfBoundary.Length).SequenceEqual(_crlfBoundary))
            {
                return i;
            }
        }

        return -1;
    }

    private Dictionary<string, string> ReadHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var lineEnd = IndexOfCrlf(_pos);
            if (lineEnd < 0)
            {
                throw new GoMultipartFormatException("unexpected end of part headers");
            }

            // Go keeps header bytes in a string; UTF-8 decoding round-trips the
            // valid case and, like Go's JSON encoder, repairs invalid bytes with
            // U+FFFD when the value is later marshaled (difference C074).
            var line = Encoding.UTF8.GetString(_body, _pos, lineEnd - _pos);
            _pos = lineEnd + 2;

            if (line.Length == 0)
            {
                return headers;
            }

            if (line[0] is ' ' or '\t')
            {
                // RFC 5322 folding: Go joins continuation lines with one space.
                if (_lastHeaderKey is null)
                {
                    throw new GoMultipartFormatException("continuation line without a header");
                }

                headers[_lastHeaderKey] = headers[_lastHeaderKey] + " " + line.Trim();
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new GoMultipartFormatException("malformed part header");
            }

            var key = line[..colon].Trim();
            headers[key] = line[(colon + 1)..].TrimStart(' ', '\t');
            _lastHeaderKey = key;
        }
    }

    private int IndexOfCrlf(int from)
    {
        for (var i = from; i + 1 < _body.Length; i++)
        {
            if (_body[i] == (byte)'\r' && _body[i + 1] == (byte)'\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static (string Name, string FileName) ParseContentDisposition(string? value)
    {
        // Go's Part.FormName/FileName treat an unparsable disposition as empty
        // parameters rather than a read error.
        if (!GoMimeTypes.TryParseMediaType(value, out var mediaType, out var parameters)
            || !string.Equals(mediaType, "form-data", StringComparison.Ordinal))
        {
            return (string.Empty, string.Empty);
        }

        var name = parameters.TryGetValue("name", out var nameValue) ? nameValue : string.Empty;
        var fileName = parameters.TryGetValue("filename", out var fileValue) ? fileValue : string.Empty;
        if (fileName.Length > 0)
        {
            // RFC 7578 §4.2: directory information must not be used.
            var slash = fileName.LastIndexOf('/');
            if (slash >= 0)
            {
                fileName = fileName[(slash + 1)..];
            }
        }

        return (name, fileName);
    }
}

/// <summary>
/// Port of the writing half of Go's <c>mime/multipart.Writer</c>: parts are
/// CRLF-delimited, header names are sorted, and disposition values use Go's
/// backslash/percent escaping. Byte-compatible with the reference so upstream
/// receives the same body shape.
/// </summary>
public sealed class GoMultipartWriter
{
    // A plain byte list rather than a MemoryStream: the writer is a value-like
    // helper with no unmanaged resources, and CA1001 would otherwise demand
    // IDisposable on every construction site.
    private readonly List<byte> _buffer = [];
    private readonly string _boundary;
    private bool _hasPart;

    public GoMultipartWriter()
    {
        // Go: 30 random bytes, lowercase hex (mime/multipart randomBoundary).
        _boundary = Convert.ToHexString(RandomNumberGenerator.GetBytes(30)).ToLowerInvariant();
    }

    public string Boundary => _boundary;

    /// <summary>Go: <c>Writer.FormDataContentType()</c>; the hex boundary needs no quoting.</summary>
    public string FormDataContentType() => "multipart/form-data; boundary=" + _boundary;

    /// <summary>Go: <c>Writer.WriteField</c> — a part with only Content-Disposition.</summary>
    public void WriteField(string name, string value)
        => CreatePart(name, fileName: null, contentType: null, Encoding.UTF8.GetBytes(value));

    /// <summary>
    /// Go: <c>Writer.CreatePart</c> with a Content-Disposition (and optional
    /// Content-Type) header. Header names are emitted in ordinal order, which
    /// puts Content-Disposition before Content-Type exactly like Go's
    /// <c>slices.Sorted(maps.Keys(header))</c>.
    /// </summary>
    public void CreatePart(string name, string? fileName, string? contentType, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        WriteAscii(_hasPart ? "\r\n--" : "--");
        WriteAscii(_boundary);
        WriteAscii("\r\n");

        var disposition = "Content-Disposition: form-data; name=\"" + EscapeQuotes(name) + "\"";
        if (fileName is not null)
        {
            disposition += "; filename=\"" + EscapeQuotes(fileName) + "\"";
        }

        WriteUtf8(disposition);
        WriteAscii("\r\n");

        if (contentType is not null)
        {
            WriteUtf8("Content-Type: " + contentType);
            WriteAscii("\r\n");
        }

        WriteAscii("\r\n");
        _buffer.AddRange(data);
        _hasPart = true;
    }

    /// <summary>
    /// Go: <c>Writer.Close</c> — terminates the final part's data and writes the
    /// closing boundary. Go writes <c>\r\n--{boundary}--\r\n</c> unconditionally
    /// (mime/multipart writer.go), even when no part was created.
    /// </summary>
    public void Close()
    {
        WriteAscii("\r\n--");
        WriteAscii(_boundary);
        WriteAscii("--\r\n");
    }

    /// <summary>The accumulated body; call after <see cref="Close"/>.</summary>
    public byte[] ToArray() => [.. _buffer];

    /// <summary>
    /// Go's <c>escapeQuotes</c>: backslash escapes for \ and ", percent escapes
    /// for CR/LF (multipart/writer.go).
    /// </summary>
    private static string EscapeQuotes(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\r':
                    builder.Append("%0D");
                    break;
                case '\n':
                    builder.Append("%0A");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    private void WriteAscii(string value) => _buffer.AddRange(Encoding.ASCII.GetBytes(value));

    private void WriteUtf8(string value) => _buffer.AddRange(Encoding.UTF8.GetBytes(value));
}

/// <summary>
/// Port of <c>mime.ParseMediaType</c> as used by the OpenAI images transformer:
/// lowercased media type plus quoted/unquoted parameters (no RFC 2231
/// continuation decoding, which the reference never feeds).
/// </summary>
public static class GoMimeTypes
{
    /// <summary>
    /// Parses a Content-Type/Content-Disposition value. Returns false where Go
    /// would return an error, so callers can keep the reference's "invalid
    /// content-type" branch.
    /// </summary>
    public static bool TryParseMediaType(
        string? value,
        out string mediaType,
        out Dictionary<string, string> parameters)
    {
        mediaType = string.Empty;
        parameters = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var semi = value.IndexOf(';');
        var basePart = (semi < 0 ? value : value[..semi]).Trim();
        if (!IsMediaType(basePart))
        {
            return false;
        }

        mediaType = basePart.ToLowerInvariant();

        var rest = semi < 0 ? string.Empty : value[(semi + 1)..];
        var pos = 0;
        while (pos < rest.Length)
        {
            while (pos < rest.Length && rest[pos] is ' ' or '\t')
            {
                pos++;
            }

            if (pos >= rest.Length)
            {
                break;
            }

            var keyStart = pos;
            while (pos < rest.Length && rest[pos] != '=' && rest[pos] != ';')
            {
                pos++;
            }

            if (pos >= rest.Length || rest[pos] != '=')
            {
                return false;
            }

            var key = rest[keyStart..pos].Trim();
            if (key.Length == 0)
            {
                return false;
            }

            pos++; // '='
            while (pos < rest.Length && rest[pos] is ' ' or '\t')
            {
                pos++;
            }

            string parameterValue;
            if (pos < rest.Length && rest[pos] == '"')
            {
                pos++;
                var builder = new StringBuilder();
                var closed = false;
                while (pos < rest.Length)
                {
                    var c = rest[pos++];
                    if (c == '\\' && pos < rest.Length)
                    {
                        builder.Append(rest[pos++]);
                        continue;
                    }

                    if (c == '"')
                    {
                        closed = true;
                        break;
                    }

                    builder.Append(c);
                }

                if (!closed)
                {
                    return false;
                }

                parameterValue = builder.ToString();
            }
            else
            {
                var valueStart = pos;
                while (pos < rest.Length && rest[pos] != ';')
                {
                    pos++;
                }

                parameterValue = rest[valueStart..pos].Trim();
                if (parameterValue.Length == 0)
                {
                    return false;
                }
            }

            parameters[key.ToLowerInvariant()] = parameterValue;

            while (pos < rest.Length && rest[pos] != ';')
            {
                pos++;
            }

            if (pos < rest.Length)
            {
                pos++;
            }
        }

        return true;
    }

    private static bool IsMediaType(string value)
    {
        var slash = value.IndexOf('/');
        return slash < 0
            ? IsToken(value)
            : IsToken(value[..slash]) && IsToken(value[(slash + 1)..]);
    }

    private static bool IsToken(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var c in value)
        {
            var ok = char.IsAsciiLetterOrDigit(c)
                || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
