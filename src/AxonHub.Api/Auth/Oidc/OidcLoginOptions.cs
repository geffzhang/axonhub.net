using AxonHub.Llm.Transformer.Shared;
using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Auth.Oidc;

/// <summary>
/// Port of the <c>public_url</c> parameter of api.OIDCHandlers
/// (internal/server/api/oidc.go:88-106): every authorize/callback URL is built
/// from this configured value, never from the request's Host header.
/// </summary>
public sealed record OidcLoginOptions(string PublicUrl)
{
    /// <summary>biz.OIDCHandlers' <c>errOIDCPublicURLRequired</c>.</summary>
    public const string PublicUrlRequired =
        "server.public_url must be configured when OIDC is enabled";

    /// <summary><c>server.public_url</c> with the reference's AXONHUB_* env fallback.</summary>
    public static OidcLoginOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var publicUrl = configuration["Server:PublicUrl"];
        if (string.IsNullOrEmpty(publicUrl))
        {
            publicUrl = configuration["AXONHUB_SERVER_PUBLIC_URL"];
        }

        return new OidcLoginOptions(publicUrl ?? string.Empty);
    }

    /// <summary>
    /// Port of <c>OIDCHandlers.getBaseURL</c> (oidc.go:218-241): trims the
    /// configured value, validates it with Go's <c>url.Parse</c> rules, requires
    /// an http(s) scheme with a host, and drops one trailing slash.
    /// </summary>
    public bool TryGetBaseUrl(out string baseUrl, out string error)
    {
        baseUrl = string.Empty;
        error = string.Empty;

        var trimmed = GoStrings.TrimSpace(PublicUrl);
        if (trimmed.Length == 0)
        {
            error = PublicUrlRequired;
            return false;
        }

        var scheme = ParseProblem(trimmed);
        if (scheme is null)
        {
            baseUrl = trimmed.EndsWith('/') ? trimmed[..^1] : trimmed;
            return true;
        }

        error = scheme.StartsWith("scheme:", StringComparison.Ordinal)
            ? "server.public_url must use http or https"
            : "invalid server.public_url";
        return false;
    }

    /// <summary>
    /// Returns null when Go's <c>url.Parse</c> plus the reference's field checks
    /// accept the URL, <c>"scheme:&lt;scheme&gt;"</c> when only the scheme is
    /// unacceptable, and <c>"invalid"</c> otherwise.
    /// </summary>
    /// <remarks>
    /// This follows <c>net/url</c>'s absolute-URL path: fragment first, then
    /// scheme, then authority, then query/path; a URL without <c>//</c> after
    /// the scheme has no host and is rejected, and the reference's own checks
    /// reject userinfo, a query or a fragment. Unlike <see cref="Uri"/> it
    /// accepts any numeric port (Go does not range-check) and it rejects an
    /// invalid percent escape in any component (C171).
    /// </remarks>
    private static string? ParseProblem(string raw)
    {
        foreach (var character in raw)
        {
            if (character < 0x20 || character == 0x7f)
            {
                return "invalid";
            }
        }

        if (raw.Length == 0 || !IsSchemeStart(raw[0]))
        {
            return "invalid";
        }

        var index = 1;
        while (index < raw.Length && IsSchemeChar(raw[index]))
        {
            index++;
        }

        if (index >= raw.Length || raw[index] != ':')
        {
            return "invalid";
        }

        var scheme = raw[..index].ToLowerInvariant();
        index++;

        if (index + 1 >= raw.Length || raw[index] != '/' || raw[index + 1] != '/')
        {
            // No authority: Go leaves Host empty (the URL becomes opaque).
            return "invalid";
        }

        index += 2;
        var authorityStart = index;
        while (index < raw.Length && raw[index] != '/' && raw[index] != '?' && raw[index] != '#')
        {
            index++;
        }

        var authority = raw[authorityStart..index];

        // url.URL.User != nil for any userinfo, which the reference rejects.
        if (authority.Contains('@', StringComparison.Ordinal))
        {
            return "invalid";
        }

        if (!TryParseHost(authority, out var hostname) || hostname.Length == 0)
        {
            return "invalid";
        }

        var rest = raw[index..];

        string pathAndQuery;
        string fragment;
        var hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            pathAndQuery = rest[..hash];
            fragment = rest[(hash + 1)..];
        }
        else
        {
            pathAndQuery = rest;
            fragment = string.Empty;
        }

        var queryIndex = pathAndQuery.IndexOf('?');
        var path = queryIndex >= 0 ? pathAndQuery[..queryIndex] : pathAndQuery;
        var rawQuery = queryIndex >= 0 ? pathAndQuery[(queryIndex + 1)..] : string.Empty;

        if (!ValidEncoded(path) || !ValidEncoded(rawQuery) || !ValidEncoded(fragment))
        {
            return "invalid";
        }

        if (rawQuery.Length > 0 || fragment.Length > 0)
        {
            return "invalid";
        }

        return scheme is "http" or "https" ? null : "scheme:" + scheme;
    }

    private static bool TryParseHost(string hostPort, out string hostname)
    {
        hostname = string.Empty;

        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            hostname = hostPort[1..close];
            var port = hostPort[(close + 1)..];
            return IsOptionalPort(port) && ValidEncoded(hostname);
        }

        var colon = hostPort.LastIndexOf(':');
        if (colon >= 0)
        {
            if (!IsOptionalPort(hostPort[colon..]))
            {
                return false;
            }

            hostname = hostPort[..colon];
        }
        else
        {
            hostname = hostPort;
        }

        // Go rejects '%' in a non-IPv6 host outright, and whitespace in a host
        // name (url.Parse's "invalid character ... in host name").
        if (hostname.Contains('%', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in hostname)
        {
            if (character is ' ' or '\t' or '\n' or '\r')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Go's <c>validOptionalPort</c>: empty, or ':' followed by digits.</summary>
    private static bool IsOptionalPort(string value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        if (value[0] != ':')
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            if (!char.IsAsciiDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Go's <c>validEncoded</c>: every '%' starts a two-digit escape.</summary>
    private static bool ValidEncoded(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length
                || !char.IsAsciiHexDigit(value[index + 1])
                || !char.IsAsciiHexDigit(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    private static bool IsSchemeStart(char value)
        => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsSchemeChar(char value)
        => value is >= 'A' and <= 'Z'
            or >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '+'
            or '-'
            or '.';
}
