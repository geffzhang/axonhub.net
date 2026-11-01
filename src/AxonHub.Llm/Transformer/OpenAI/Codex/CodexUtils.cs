using System.Security.Cryptography;
using System.Text.Json;

namespace AxonHub.Llm.Transformer.OpenAI.Codex;

/// <summary>The fields of <c>net/url.URL</c> the Codex transformers inspect.</summary>
internal readonly record struct GoUrlParts(string Scheme, string Host, string Path);

/// <summary>
/// Port of llm/transformer/openai/codex/utils.go plus the Go URL/UUID helpers
/// the codex transformers rely on (there is no CLR equivalent of
/// <c>net/url.Parse</c>, and <see cref="Uri"/> normalizes differently).
/// </summary>
public static class CodexUtils
{
    /// <summary>
    /// Namespace bytes of <c>uuid.NameSpaceOID</c> (6ba7b812-9dad-11d1-80b4-00c04fd430c8).
    /// </summary>
    internal static readonly byte[] NameSpaceOid =
    [
        0x6b, 0xa7, 0xb8, 0x12, 0x9d, 0xad, 0x11, 0xd1,
        0x80, 0xb4, 0x00, 0xc0, 0x4f, 0xd4, 0x30, 0xc8,
    ];

    /// <summary>
    /// Port of <c>ExtractChatGPTAccountIDFromJWT</c>: golang-jwt's
    /// <c>ParseUnverified</c> with <c>jwt.MapClaims</c>, returning an empty
    /// string on any failure.
    /// </summary>
    public static string ExtractChatGptAccountIdFromJwt(string? tokenStr)
    {
        if (string.IsNullOrEmpty(tokenStr))
        {
            return string.Empty;
        }

        var parts = tokenStr.Split('.');
        if (parts.Length != 3)
        {
            return string.Empty;
        }

        // ParseUnverified decodes only the header and claims segments; the
        // signature segment is never inspected, so a malformed signature is
        // not an error.
        if (!TryDecodeSegment(parts[0], out var headerBytes))
        {
            return string.Empty;
        }

        try
        {
            using var header = JsonDocument.Parse(headerBytes);
            if (header.RootElement.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        if (!TryDecodeSegment(parts[1], out var claimsBytes))
        {
            return string.Empty;
        }

        JsonElement claims;
        try
        {
            using var document = JsonDocument.Parse(claimsBytes);
            claims = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        if (claims.ValueKind != JsonValueKind.Object
            || !claims.TryGetProperty("https://api.openai.com/auth", out var authClaims)
            || authClaims.ValueKind != JsonValueKind.Object
            || !authClaims.TryGetProperty("chatgpt_account_id", out var accountId)
            || accountId.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        return accountId.GetString() ?? string.Empty;
    }

    /// <summary>Port of <c>isCodexCLIVersion</c>; the reference has no callers yet.</summary>
    public static bool IsCodexCliVersion(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        var dot = false;
        foreach (var c in trimmed)
        {
            if (c == '.')
            {
                dot = true;
                continue;
            }

            if (c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' or '-' or '+' or '_')
            {
                continue;
            }

            return false;
        }

        return dot;
    }

    /// <summary>
    /// Port of <c>uuid.NewSHA1(space, name)</c> (google/uuid): SHA-1 over the
    /// namespace bytes followed by the name, with version 5 and RFC 4122
    /// variant bits, rendered lowercase.
    /// </summary>
    internal static string NewUuidV5(ReadOnlySpan<byte> nameSpace, ReadOnlySpan<byte> name)
    {
        Span<byte> data = stackalloc byte[nameSpace.Length + name.Length];
        nameSpace.CopyTo(data);
        name.CopyTo(data[nameSpace.Length..]);

        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
#pragma warning disable CA5350 // google/uuid.NewSHA1 mandates SHA-1 for name-based UUIDs.
        SHA1.HashData(data, hash);
#pragma warning restore CA5350

        // uuid.NewSHA1: set version 5 and the RFC 4122 variant, then format.
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        var uuidBytes = hash[..16].ToArray();
        return string.Create(36, uuidBytes, static (chars, uuid) =>
        {
            const string digits = "0123456789abcdef";
            var pos = 0;
            for (var i = 0; i < uuid.Length; i++)
            {
                if (i is 4 or 6 or 8 or 10)
                {
                    chars[pos++] = '-';
                }

                chars[pos++] = digits[uuid[i] >> 4];
                chars[pos++] = digits[uuid[i] & 0x0F];
            }
        });
    }

    /// <summary>
    /// Port of the parts of <c>net/url.Parse(rawURL)</c> the reference inspects:
    /// scheme (lowercased), host (still percent-encoded? no — host is decoded,
    /// as Go decodes it) and the decoded path. Returns false where Go's parse
    /// returns an error.
    /// </summary>
    internal static bool TryParseGoUrl(string rawUrl, out GoUrlParts parts)
    {
        parts = default;

        // url.Parse cuts the fragment at the first '#' before parsing.
        var hash = rawUrl.IndexOf('#');
        var withoutFragment = hash < 0 ? rawUrl : rawUrl[..hash];

        // net/url rejects ASCII control characters anywhere in the URL.
        foreach (var c in withoutFragment)
        {
            if (c < ' ' || c == '\u007f')
            {
                return false;
            }
        }

        if (!TrySplitScheme(withoutFragment, out var scheme, out var rest))
        {
            return false;
        }

        scheme = scheme.ToLowerInvariant();

        // The query is cut before the authority is parsed.
        var question = rest.IndexOf('?');
        if (question >= 0)
        {
            rest = rest[..question];
        }

        if (!rest.StartsWith('/'))
        {
            if (scheme.Length > 0)
            {
                // Rootless paths with a scheme are opaque: no host, no path.
                parts = new GoUrlParts(scheme, string.Empty, string.Empty);
                return true;
            }

            // RFC 3986 §3.3: the first path segment of a relative reference
            // cannot contain a colon.
            var firstSlash = rest.IndexOf('/');
            var firstSegment = firstSlash < 0 ? rest : rest[..firstSlash];
            if (firstSegment.Contains(':', StringComparison.Ordinal))
            {
                return false;
            }
        }

        var hasAuthority = (scheme.Length > 0 || !rest.StartsWith("///", StringComparison.Ordinal))
            && rest.StartsWith("//", StringComparison.Ordinal);

        var host = string.Empty;
        var pathPart = rest;
        if (hasAuthority)
        {
            var afterSlashes = rest[2..];
            var slash = afterSlashes.IndexOf('/');
            var authority = slash < 0 ? afterSlashes : afterSlashes[..slash];
            pathPart = slash < 0 ? string.Empty : afterSlashes[slash..];

            if (!TryParseAuthority(scheme, authority, out host))
            {
                return false;
            }
        }

        if (!TryUnescape(pathPart, out var path))
        {
            return false;
        }

        parts = new GoUrlParts(scheme, host, path);
        return true;
    }

    /// <summary>Port of <c>net/url.getScheme</c>; false means "missing protocol scheme".</summary>
    private static bool TrySplitScheme(string rawUrl, out string scheme, out string rest)
    {
        scheme = string.Empty;
        rest = rawUrl;

        for (var i = 0; i < rawUrl.Length; i++)
        {
            var c = rawUrl[i];
            if (IsAsciiAlpha(c))
            {
                continue;
            }

            if (c is >= '0' and <= '9' or '+' or '-' or '.')
            {
                if (i == 0)
                {
                    return true;
                }

                continue;
            }

            if (c == ':')
            {
                if (i == 0)
                {
                    return false;
                }

                scheme = rawUrl[..i];
                rest = rawUrl[(i + 1)..];
                return true;
            }

            // An invalid character means there is no scheme.
            return true;
        }

        return true;
    }

    /// <summary>Port of <c>net/url.parseAuthority</c>.</summary>
    private static bool TryParseAuthority(string scheme, string authority, out string host)
    {
        host = string.Empty;

        var at = authority.LastIndexOf('@');
        var hostPart = at < 0 ? authority : authority[(at + 1)..];

        if (!TryParseHost(scheme, hostPart, out host))
        {
            return false;
        }

        if (at >= 0 && !IsValidUserInfo(authority[..at]))
        {
            return false;
        }

        return true;
    }

    /// <summary>Port of <c>net/url.parseHost</c> (IPv6 literals included).</summary>
    private static bool TryParseHost(string scheme, string host, out string parsedHost)
    {
        parsedHost = string.Empty;

        if (host.Length == 0)
        {
            return true;
        }

        var openBracket = host.LastIndexOf('[');
        if (openBracket > 0)
        {
            return false;
        }

        if (openBracket == 0)
        {
            var closeBracket = host.LastIndexOf(']');
            if (closeBracket < 0)
            {
                return false;
            }

            if (!IsValidOptionalPort(host[(closeBracket + 1)..]))
            {
                return false;
            }

            return TryUnescape(host, out parsedHost, hostMode: true);
        }

        var firstColon = host.IndexOf(':', StringComparison.Ordinal);
        if (firstColon >= 0)
        {
            var lastColon = host.LastIndexOf(':');
            var colon = lastColon != firstColon && scheme is not ("http" or "https") ? lastColon : firstColon;
            if (!IsValidOptionalPort(host[colon..]))
            {
                return false;
            }
        }

        return TryUnescape(host, out parsedHost, hostMode: true);
    }

    /// <summary>Port of <c>net/url.validOptionalPort</c>.</summary>
    private static bool IsValidOptionalPort(string port)
    {
        if (port.Length == 0)
        {
            return true;
        }

        if (port[0] != ':')
        {
            return false;
        }

        for (var i = 1; i < port.Length; i++)
        {
            if (port[i] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Port of <c>net/url.validUserinfo</c>.</summary>
    private static bool IsValidUserInfo(string userInfo)
    {
        foreach (var c in userInfo)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
                or '-' or '.' or '_' or ':' or '~' or '!' or '$' or '&' or '\''
                or '(' or ')' or '*' or '+' or ',' or ';' or '=' or '%' or '@')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Port of <c>net/url.unescape</c> for paths (mode <c>encodePath</c>) and
    /// hosts (mode <c>encodeHost</c>). Percent-escaped bytes are materialized as
    /// single chars so the ASCII comparisons the reference performs stay exact.
    /// </summary>
    private static bool TryUnescape(string value, out string unescaped, bool hostMode = false)
    {
        unescaped = string.Empty;
        var builder = new System.Text.StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (c == '%')
            {
                if (i + 2 >= value.Length || !IsHex(value[i + 1]) || !IsHex(value[i + 2]))
                {
                    return false;
                }

                var decoded = (byte)((HexValue(value[i + 1]) << 4) | HexValue(value[i + 2]));

                // Hosts may only %-escape non-ASCII bytes (RFC 3986 §3.2.2),
                // except for %25 in IPv6 zone identifiers (RFC 6874).
                if (hostMode && decoded < 0x80 && !value.AsSpan(i, 3).SequenceEqual("%25"))
                {
                    return false;
                }

                builder.Append((char)decoded);
                i += 2;
                continue;
            }

            if (hostMode && c < 0x80 && ShouldEscapeHost(c))
            {
                return false;
            }

            builder.Append(c);
        }

        unescaped = builder.ToString();
        return true;
    }

    private static bool ShouldEscapeHost(char c)
        => c switch
        {
            >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' => false,
            '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ',' or ';' or '='
                or ':' or '[' or ']' or '<' or '>' or '"' => false,
            '-' or '_' or '.' or '~' => false,
            _ => true,
        };

    private static bool IsAsciiAlpha(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static int HexValue(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;

    /// <summary>
    /// Go's <c>base64.RawURLEncoding.DecodeString</c>: padding is rejected,
    /// <c>\r</c> and <c>\n</c> are ignored.
    /// </summary>
    private static bool TryDecodeSegment(string segment, out byte[] bytes)
    {
        bytes = [];

        var cleaned = new System.Text.StringBuilder(segment.Length);
        foreach (var c in segment)
        {
            if (c is '\r' or '\n')
            {
                continue;
            }

            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
            {
                return false;
            }

            cleaned.Append(c);
        }

        var raw = cleaned.ToString();
        if (raw.Length % 4 == 1)
        {
            return false;
        }

        var padded = raw.Length % 4 == 0
            ? raw
            : raw + new string('=', 4 - (raw.Length % 4));

        try
        {
            bytes = Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/'));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
