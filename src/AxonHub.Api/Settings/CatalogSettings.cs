using System.Globalization;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Settings;

/// <summary>
/// Port of <c>biz.CatalogSettings</c> (catalog_settings.go:24-27): how the
/// built-in model catalog is refreshed.
/// </summary>
public sealed record CatalogSettings(string UpstreamUrl, int RefreshSeconds);

/// <summary>
/// Port of the catalog-settings constants, normalization and validation
/// (catalog_settings.go:14-77).
/// </summary>
public static class CatalogSettingsRules
{
    /// <summary>biz.SystemKeyCatalogSettings.</summary>
    public const string Key = "catalog_settings";

    /// <summary>biz.DefaultCatalogUpstreamURL.</summary>
    public const string DefaultUpstreamUrl =
        "https://raw.githubusercontent.com/ThinkInAIXYZ/PublicProviderConf/refs/heads/dev/dist/all.json";

    /// <summary>biz.DefaultCatalogRefreshSeconds.</summary>
    public const int DefaultRefreshSeconds = 3600;

    /// <summary>biz.minCatalogRefreshSeconds.</summary>
    public const int MinRefreshSeconds = 60;

    /// <summary>biz.maxCatalogRefreshSeconds.</summary>
    public const int MaxRefreshSeconds = 7 * 24 * 3600;

    public static CatalogSettings Default => new(string.Empty, DefaultRefreshSeconds);

    /// <summary>Port of <c>normalizeCatalogSettings</c>.</summary>
    public static CatalogSettings Normalize(CatalogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var upstreamUrl = settings.UpstreamUrl.Trim();
        var refreshSeconds = settings.RefreshSeconds;
        if (refreshSeconds <= 0)
        {
            refreshSeconds = DefaultRefreshSeconds;
        }

        if (refreshSeconds < MinRefreshSeconds)
        {
            refreshSeconds = MinRefreshSeconds;
        }

        if (refreshSeconds > MaxRefreshSeconds)
        {
            refreshSeconds = MaxRefreshSeconds;
        }

        return new CatalogSettings(upstreamUrl, refreshSeconds);
    }

    /// <summary>Port of <c>EffectiveUpstreamURL</c>.</summary>
    public static string EffectiveUpstreamUrl(CatalogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.UpstreamUrl.Trim().Length == 0
            ? DefaultUpstreamUrl
            : settings.UpstreamUrl;
    }

    /// <summary>
    /// Port of <c>validateCatalogSettings</c> (catalog_settings.go:44-63),
    /// including the accept/reject split and the wording of Go 1.26's
    /// <c>url.Parse</c> for the URL shapes the admin UI can submit.
    /// </summary>
    /// <remarks>
    /// <c>url.Parse</c> failures are part of the observable error text, so
    /// the port mirrors the reference parsing pipeline: fragment stripping,
    /// scheme scan, query cut, authority parse (host/port before userinfo),
    /// path escapes, then fragment escapes. Remaining approximations are
    /// recorded as C237: the <c>GODEBUG=urlstrictcolons=0</c> compatibility
    /// toggle is not honored, and quoting of non-ASCII or invalid-UTF-8
    /// bytes follows <see cref="GoNetip.Quote"/> rather than
    /// <c>strconv.Quote</c>'s rune-level escaping.
    /// </remarks>
    public static string? Validate(CatalogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var raw = settings.UpstreamUrl;
        if (raw.Length == 0)
        {
            return null;
        }

        // url.Parse cuts the fragment before parsing: parse errors quote the
        // fragment-stripped input, fragment escape errors quote the input.
        var hash = raw.IndexOf('#');
        var urlPart = hash >= 0 ? raw[..hash] : raw;
        var fragment = hash >= 0 ? raw[(hash + 1)..] : null;

        if (ContainsControlCharacter(urlPart))
        {
            return ParseError(urlPart, "net/url: invalid control character in URL");
        }

        string scheme;
        bool hostEmpty;
        if (urlPart == "*")
        {
            // net/url.Parse special-cases the asterisk-form request target.
            scheme = string.Empty;
            hostEmpty = true;
        }
        else
        {
            var parseError = ParseUrlPart(urlPart, out scheme, out hostEmpty);
            if (parseError is not null)
            {
                return ParseError(urlPart, parseError);
            }
        }

        if (!string.IsNullOrEmpty(fragment)
            && FindInvalidPercentEscape(fragment) is { } fragmentEscape)
        {
            // setFragment reports errors against the full input URL.
            return ParseError(raw, "invalid URL escape " + GoQuote(fragmentEscape));
        }

        if (scheme != "http" && scheme != "https")
        {
            return "catalog upstream url must be http or https";
        }

        if (hostEmpty)
        {
            return "catalog upstream url is missing a host";
        }

        return null;
    }

    /// <summary>
    /// Port of the scheme/authority/path stages of Go's <c>parse</c>
    /// (url.go:428-508): returns the parse error text, the lowercased scheme
    /// and whether the parsed <c>Host</c> is empty.
    /// </summary>
    private static string? ParseUrlPart(
        string raw,
        out string scheme,
        out bool hostEmpty)
    {
        scheme = string.Empty;
        hostEmpty = true;

        if (!TryReadScheme(raw, out var rawScheme, out var rest))
        {
            return "missing protocol scheme";
        }

        scheme = rawScheme.ToLowerInvariant();

        // The query is cut before the authority is parsed and is never
        // unescaped by Parse; ForceQuery does not affect validation.
        var query = rest.IndexOf('?');
        if (query >= 0)
        {
            rest = rest[..query];
        }

        if (!rest.StartsWith('/'))
        {
            if (scheme.Length != 0)
            {
                // Rootless paths are opaque: Host stays empty and the path
                // is not unescaped.
                return null;
            }

            // RFC 3986 §4.1: the first path segment of a relative reference
            // cannot contain a colon.
            var slash = rest.IndexOf('/');
            var segment = slash >= 0 ? rest[..slash] : rest;
            if (segment.Contains(':'))
            {
                return "first path segment in URL cannot contain colon";
            }

            return DescribeEscapeError(rest);
        }

        if (rest.StartsWith("//", StringComparison.Ordinal)
            && (scheme.Length != 0
                || !rest.StartsWith("///", StringComparison.Ordinal)))
        {
            var afterSlashes = rest[2..];
            var slash = afterSlashes.IndexOf('/');
            var authority = slash >= 0 ? afterSlashes[..slash] : afterSlashes;
            var path = slash >= 0 ? afterSlashes[slash..] : string.Empty;

            var authorityError = ParseAuthority(scheme, authority, out hostEmpty);
            if (authorityError is not null)
            {
                return authorityError;
            }

            return DescribeEscapeError(path);
        }

        // OmitHost (scheme present, single leading slash) or a scheme-less
        // "///" rooted path: no authority is parsed.
        return DescribeEscapeError(rest);
    }

    /// <summary>Port of Go's <c>getScheme</c> (url.go:368-390).</summary>
    /// <returns>False for the <c>missing protocol scheme</c> error.</returns>
    private static bool TryReadScheme(
        string raw,
        out string scheme,
        out string rest)
    {
        scheme = string.Empty;
        rest = raw;

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (char.IsAsciiLetter(c))
            {
                continue;
            }

            if (char.IsAsciiDigit(c) || c is '+' or '-' or '.')
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

                scheme = raw[..i];
                rest = raw[(i + 1)..];
                return true;
            }

            // Invalid scheme character: the whole input is a relative URL.
            return true;
        }

        return true;
    }

    /// <summary>
    /// Port of Go's <c>parseAuthority</c> (url.go:511-546): the host is
    /// parsed before the userinfo is validated.
    /// </summary>
    private static string? ParseAuthority(
        string scheme,
        string authority,
        out bool hostEmpty)
    {
        var at = authority.LastIndexOf('@');
        var hostPort = at >= 0 ? authority[(at + 1)..] : authority;

        var hostError = ParseHost(scheme, hostPort);
        if (hostError is not null)
        {
            hostEmpty = false;
            return hostError;
        }

        // parseHost's result differs from hostPort only in its percent
        // decoding, which never removes the last character.
        hostEmpty = hostPort.Length == 0;

        if (at < 0)
        {
            return null;
        }

        var userinfo = authority[..at];
        if (!IsValidUserinfo(userinfo))
        {
            return "net/url: invalid userinfo";
        }

        var colon = userinfo.IndexOf(':');
        if (colon < 0)
        {
            return DescribeEscapeError(userinfo);
        }

        return DescribeEscapeError(userinfo[..colon])
            ?? DescribeEscapeError(userinfo[(colon + 1)..]);
    }

    /// <summary>Port of Go's <c>parseHost</c> (url.go:548-642).</summary>
    private static string? ParseHost(string scheme, string host)
    {
        var open = host.LastIndexOf('[');
        if (open > 0)
        {
            return "invalid IP-literal";
        }

        if (open == 0)
        {
            var close = host.LastIndexOf(']');
            if (close < 0)
            {
                return "missing ']' in host";
            }

            var colonPort = host[(close + 1)..];
            if (!IsValidOptionalPort(colonPort))
            {
                return "invalid port " + GoQuote(colonPort) + " after host";
            }

            var hostname = host[1..close];
            var hostnameError = UnescapeHostname(hostname, out var unescaped);
            if (hostnameError is not null)
            {
                return hostnameError;
            }

            if (!GoNetip.TryParseAddr(unescaped, out var addrError))
            {
                return "invalid host: " + addrError;
            }

            // RFC 3986 §3.2.2: only IPv6 addresses may be bracketed. netip
            // parses dotted IPv4, which the URL parser then rejects.
            return unescaped.Contains(':')
                ? null
                : "invalid IP-literal";
        }

        var firstColon = host.IndexOf(':');
        if (firstColon >= 0)
        {
            // RFC 3986 does not allow colons in the host subcomponent. Go
            // 1.26 enforces strictly for http/https (issue 75223) and falls
            // back to the last colon for other schemes.
            var colonIndex = firstColon;
            if (host.LastIndexOf(':') != firstColon
                && scheme != "http"
                && scheme != "https")
            {
                colonIndex = host.LastIndexOf(':');
            }

            var colonPort = host[colonIndex..];
            if (!IsValidOptionalPort(colonPort))
            {
                return "invalid port " + GoQuote(colonPort) + " after host";
            }
        }

        return UnescapeHost(host, out _);
    }

    /// <summary>
    /// Port of the bracketed-host branch of <c>parseHost</c>: RFC 6874 zone
    /// identifiers are introduced by the <c>%25</c> escape.
    /// </summary>
    private static string? UnescapeHostname(string hostname, out string unescaped)
    {
        var zoneIndex = hostname.IndexOf("%25", StringComparison.Ordinal);
        if (zoneIndex < 0)
        {
            return UnescapeHost(hostname, out unescaped);
        }

        var hostError = UnescapeHost(hostname[..zoneIndex], out var hostPart);
        if (hostError is not null)
        {
            unescaped = string.Empty;
            return hostError;
        }

        var zoneError = UnescapeZone(hostname[zoneIndex..], out var zonePart);
        if (zoneError is not null)
        {
            unescaped = string.Empty;
            return zoneError;
        }

        unescaped = hostPart + zonePart;
        return null;
    }

    private static string? UnescapeHost(string value, out string unescaped)
    {
        var builder = new StringBuilder(value.Length);
        var error = UnescapeHostOrZone(value, builder, zone: false);
        unescaped = error is null ? builder.ToString() : string.Empty;
        return error;
    }

    private static string? UnescapeZone(string value, out string unescaped)
    {
        var builder = new StringBuilder(value.Length);
        var error = UnescapeHostOrZone(value, builder, zone: true);
        unescaped = error is null ? builder.ToString() : string.Empty;
        return error;
    }

    /// <summary>
    /// Port of Go's <c>unescape</c> for <c>encodeHost</c>/<c>encodeZone</c>
    /// (url.go:105-152).
    /// </summary>
    private static string? UnescapeHostOrZone(
        string value,
        StringBuilder builder,
        bool zone)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c != '%')
            {
                // Non-ASCII bytes are accepted verbatim.
                if (c < 0x80 && !IsAllowedHostCharacter(c))
                {
                    return InvalidHostCharacter(c);
                }

                builder.Append(c);
                continue;
            }

            if (i + 2 >= value.Length
                || !char.IsAsciiHexDigit(value[i + 1])
                || !char.IsAsciiHexDigit(value[i + 2]))
            {
                return "invalid URL escape " + GoQuote(EscapeSequence(value, i));
            }

            var sequence = value.Substring(i, 3);
            var decoded = (HexValue(value[i + 1]) << 4) | HexValue(value[i + 2]);
            if (zone)
            {
                // Zone identifiers may %-encode bytes they could also spell
                // directly, plus the space Windows puts there.
                if (sequence != "%25"
                    && decoded != ' '
                    && (decoded >= 0x80 || !IsAllowedHostCharacter((char)decoded)))
                {
                    return "invalid URL escape " + GoQuote(sequence);
                }
            }
            else if (HexValue(value[i + 1]) < 8 && sequence != "%25")
            {
                // Hosts may %-encode only non-ASCII bytes, plus the RFC 6874
                // zone marker %25.
                return "invalid URL escape " + GoQuote(sequence);
            }

            builder.Append((char)decoded);
            i += 2;
        }

        return null;
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (var c in value)
        {
            if (c < ' ' || c == '\u007f')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Port of Go's <c>validOptionalPort</c> (url.go:761-774).</summary>
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

        foreach (var c in port.AsSpan(1))
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Port of Go's <c>validUserinfo</c> (url.go:1273-1304).</summary>
    private static bool IsValidUserinfo(string value)
    {
        foreach (var c in value)
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                continue;
            }

            if (c is '-' or '.' or '_' or ':' or '~' or '!' or '$' or '&' or '\''
                or '(' or ')' or '*' or '+' or ',' or ';' or '=' or '%' or '@')
            {
                continue;
            }

            return false;
        }

        return true;
    }

    /// <summary>
    /// Go's <c>shouldEscape(c, encodeHost|encodeZone)</c> for ASCII: the
    /// unreserved marks, the sub-delims and the host extras
    /// <c>:[]&lt;&gt;"</c>.
    /// </summary>
    private static bool IsAllowedHostCharacter(char c)
        => char.IsAsciiLetterOrDigit(c)
            || c is '-' or '.' or '_' or '~'
            || c is '!' or '$' or '&' or '\'' or '(' or ')' or '*' or '+' or ','
                or ';' or '=' or ':' or '[' or ']' or '<' or '>' or '"';

    private static string InvalidHostCharacter(char c)
        => "invalid character " + GoQuote(c.ToString()) + " in host name";

    private static string? DescribeEscapeError(string value)
        => FindInvalidPercentEscape(value) is { } escape
            ? "invalid URL escape " + GoQuote(escape)
            : null;

    private static string? FindInvalidPercentEscape(string raw)
    {
        for (var index = 0; index < raw.Length; index++)
        {
            if (raw[index] != '%')
            {
                continue;
            }

            if (index + 2 >= raw.Length
                || !char.IsAsciiHexDigit(raw[index + 1])
                || !char.IsAsciiHexDigit(raw[index + 2]))
            {
                return EscapeSequence(raw, index);
            }

            index += 2;
        }

        return null;
    }

    /// <summary>
    /// The three-byte window Go's <c>EscapeError</c> carries
    /// (url.go:113-118): from the <c>%</c> up to three bytes, truncated at
    /// the end of the input.
    /// </summary>
    private static string EscapeSequence(string value, int index)
        => value.Substring(index, Math.Min(3, value.Length - index));

    private static int HexValue(char c)
        => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            _ => c - 'A' + 10,
        };

    private static string ParseError(string raw, string detail)
        => "invalid catalog upstream url: parse " + GoQuote(raw) + ": " + detail;

    /// <summary>Go's <c>%q</c> (<c>strconv.Quote</c>).</summary>
    private static string GoQuote(string value) => GoNetip.Quote(value);
}

/// <summary>
/// Go-shaped codec for the <c>catalog_settings</c> system value
/// (<c>json.Marshal(CatalogSettings)</c>: both fields always present, unknown
/// stored fields ignored, JSON null leaves the zero/default value).
/// </summary>
public static class CatalogSettingsCodec
{
    public static string Serialize(CatalogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("upstream_url", settings.UpstreamUrl);
            writer.WriteNumber("refresh_seconds", settings.RefreshSeconds);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(GoJson.Escape(buffer.ToArray()));
    }

    /// <summary>
    /// Port of <c>json.Unmarshal(value, &amp;settings)</c> where the caller
    /// pre-seeded the default values: absent and null fields keep the defaults.
    /// </summary>
    public static CatalogSettings Deserialize(string value)
    {
        using var document = JsonDocument.Parse(value);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Null)
        {
            return CatalogSettingsRules.Default;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(
                "json: cannot unmarshal " + root.ValueKind.ToString().ToLowerInvariant()
                + " into Go value of type biz.CatalogSettings");
        }

        var upstreamUrl = string.Empty;
        var refreshSeconds = CatalogSettingsRules.DefaultRefreshSeconds;

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "upstream_url", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    throw new JsonException(
                        "json: cannot unmarshal "
                        + property.Value.ValueKind.ToString().ToLowerInvariant()
                        + " into Go struct field CatalogSettings.upstream_url of type string");
                }

                upstreamUrl = property.Value.GetString() ?? string.Empty;
                continue;
            }

            if (string.Equals(property.Name, "refresh_seconds", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                {
                    continue;
                }

                if (property.Value.ValueKind != JsonValueKind.Number
                    || !long.TryParse(
                        property.Value.GetRawText(),
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out var parsed)
                    || parsed is < int.MinValue or > int.MaxValue)
                {
                    throw new JsonException(
                        "json: cannot unmarshal " + property.Value.GetRawText()
                        + " into Go struct field CatalogSettings.refresh_seconds of type int");
                }

                refreshSeconds = (int)parsed;
            }
        }

        return CatalogSettingsRules.Normalize(new CatalogSettings(upstreamUrl, refreshSeconds));
    }
}
