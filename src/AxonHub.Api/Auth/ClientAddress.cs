using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AxonHub.Api.Auth;

/// <summary>
/// Port of gin's Context.ClientIP + middleware.isAnyAllowedIP
/// (internal/server/middleware/ip_blocklist.go). The reference replaces gin's
/// unsafe "trust all proxies" default with an explicit network list
/// (server.go:SetTrustedProxies(config.TrustedProxies), default empty), so
/// forwarded client-IP headers are ignored unless the TCP peer is trusted.
/// </summary>
public static class ClientAddress
{
    /// <summary>Gin's RemoteIPHeaders default order.</summary>
    private static readonly string[] RemoteIpHeaders = ["X-Forwarded-For", "X-Real-IP"];

    /// <summary>Gin's ClientIP with the configured trusted proxies.</summary>
    public static string Resolve(HttpRequest request, IReadOnlyList<string> trustedProxies)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(trustedProxies);

        var remoteIp = request.HttpContext.Connection.RemoteIpAddress;
        if (remoteIp is null)
        {
            return string.Empty;
        }

        var remote = remoteIp.ToString();
        if (!IsGoParseable(remote))
        {
            return string.Empty;
        }

        // Gin only consults forwarded headers when the connection peer is a
        // configured proxy; otherwise the TCP address is the client address.
        if (!IsTrustedProxy(remoteIp, trustedProxies))
        {
            return remote;
        }

        foreach (var headerName in RemoteIpHeaders)
        {
            var header = JoinHeaderValues(request.Headers[headerName]);
            if (ValidateHeader(header, trustedProxies, out var ip))
            {
                return ip;
            }
        }

        return remote;
    }

    /// <summary>
    /// Reference startup rejects invalid server.trusted_proxies entries by
    /// panicking; the port fails the host build the same way.
    /// </summary>
    public static void ValidateTrustedProxies(IReadOnlyList<string> trustedProxies)
    {
        ArgumentNullException.ThrowIfNull(trustedProxies);

        foreach (var entry in trustedProxies)
        {
            if (entry.Contains('/', StringComparison.Ordinal))
            {
                if (!TryParsePrefix(entry, out _, out _))
                {
                    throw new InvalidOperationException(
                        "invalid server.trusted_proxies entry: " + entry);
                }
            }
            else if (!TryParseAddress(entry, out _))
            {
                throw new InvalidOperationException(
                    "invalid server.trusted_proxies entry: " + entry);
            }
        }
    }

    private static bool IsTrustedProxy(IPAddress address, IReadOnlyList<string> trustedProxies)
    {
        foreach (var entry in trustedProxies)
        {
            var item = entry.Trim();
            if (item.Length == 0)
            {
                continue;
            }

            if (item.Contains('/', StringComparison.Ordinal))
            {
                if (TryParsePrefix(item, out var network, out var bits)
                    && PrefixContains(network, bits, address))
                {
                    return true;
                }

                continue;
            }

            if (TryParseAddress(item, out var trusted)
                && Unmap(trusted).Equals(Unmap(address)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Go's <c>strings.Join(header.Values, ",")</c>: every header line is joined
    /// into one comma-separated chain.
    /// </summary>
    private static string JoinHeaderValues(StringValues values)
    {
        var parts = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            parts[i] = values[i] ?? string.Empty;
        }

        return string.Join(",", parts);
    }

    /// <summary>
    /// Port of gin's validateHeader: iterate the list right-to-left; an invalid
    /// entry invalidates the header. The first entry that is not a trusted proxy
    /// (or the left-most entry) is the client.
    /// </summary>
    private static bool ValidateHeader(
        string header,
        IReadOnlyList<string> trustedProxies,
        out string clientIp)
    {
        clientIp = string.Empty;
        if (header.Length == 0)
        {
            return false;
        }

        var items = header.Split(',');
        for (var i = items.Length - 1; i >= 0; i--)
        {
            var candidate = items[i].Trim();
            if (!TryParseAddress(candidate, out var address))
            {
                return false;
            }

            if (i == 0 || !IsTrustedProxy(address, trustedProxies))
            {
                clientIp = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Port of middleware.isBlockedIP: true when any candidate matches.</summary>
    public static bool IsBlockedIp(IReadOnlyList<string> clientIps, IReadOnlyList<string> blockedIps)
    {
        // isBlockedIP and isAnyAllowedIP share isBlockedAddr in the reference.
        return IsAnyAllowedIp(clientIps, blockedIps);
    }

    /// <summary>Port of middleware.isAnyAllowedIP: true when any candidate matches.</summary>
    public static bool IsAnyAllowedIp(IReadOnlyList<string> clientIps, IReadOnlyList<string> allowedIps)
    {
        ArgumentNullException.ThrowIfNull(clientIps);
        ArgumentNullException.ThrowIfNull(allowedIps);

        foreach (var clientIp in clientIps)
        {
            if (!TryParseAddress(clientIp, out var address))
            {
                continue;
            }

            if (MatchesAny(address, allowedIps))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Port of isBlockedAddr, reused for the allowlist.</summary>
    private static bool MatchesAny(IPAddress clientAddress, IReadOnlyList<string> entries)
    {
        foreach (var raw in entries)
        {
            var item = raw.Trim();
            if (item.Length == 0)
            {
                continue;
            }

            if (item.Contains('/', StringComparison.Ordinal))
            {
                if (TryParsePrefix(item, out var network, out var bits)
                    && PrefixContains(network, bits, clientAddress))
                {
                    return true;
                }

                continue;
            }

            if (TryParseAddress(item, out var address)
                && Unmap(address).Equals(Unmap(clientAddress)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// netip.ParsePrefix + Prefix.Contains. Host bits are not masked away at
    /// parse time; Contains compares the masked bytes. An IPv4-mapped IPv6
    /// prefix with at least 96 bits is folded to its IPv4 form first.
    /// </summary>
    internal static bool TryParsePrefix(string text, out IPAddress network, out int bits)
    {
        network = IPAddress.None;
        bits = 0;

        var slash = text.LastIndexOf('/');
        if (slash <= 0 || slash == text.Length - 1)
        {
            return false;
        }

        var addressText = text[..slash];
        if (addressText.Contains('%', StringComparison.Ordinal))
        {
            // netip.ParsePrefix rejects zoned addresses.
            return false;
        }

        if (!TryParseAddress(addressText, out var address))
        {
            return false;
        }

        // netip.ParsePrefix accepts only 1-3 decimal digits for the bit count:
        // no sign, no whitespace and no leading zeroes ("+8"/"08" fail).
        var bitsText = text[(slash + 1)..];
        if (bitsText.Length > 3 || (bitsText.Length > 1 && bitsText[0] == '0'))
        {
            return false;
        }

        foreach (var c in bitsText)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        bits = int.Parse(bitsText, CultureInfo.InvariantCulture);

        var maxBits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (bits > maxBits)
        {
            return false;
        }

        // Only prefixes within the IPv4-mapped address space can be represented
        // as IPv4 prefixes without changing their range.
        if (address.IsIPv4MappedToIPv6 && bits >= 96)
        {
            address = address.MapToIPv4();
            bits -= 96;
        }

        network = address;
        return true;
    }

    /// <summary>
    /// Port of <c>prefix.Contains(clientAddr) || prefix.Contains(clientAddr.Unmap())</c>.
    /// The second form only matters for IPv4-mapped clients; a wider IPv6 prefix
    /// (bits &lt; 96) is never folded, so an IPv4 client cannot match it.
    /// </summary>
    private static bool PrefixContains(IPAddress network, int bits, IPAddress clientAddress)
    {
        if (ContainsAddress(network, bits, clientAddress))
        {
            return true;
        }

        return clientAddress.IsIPv4MappedToIPv6
            && ContainsAddress(network, bits, clientAddress.MapToIPv4());
    }

    private static bool ContainsAddress(IPAddress network, int bits, IPAddress candidate)
    {
        if (network.AddressFamily != candidate.AddressFamily)
        {
            return false;
        }

        var networkBytes = network.GetAddressBytes();
        var clientBytes = candidate.GetAddressBytes();
        if (networkBytes.Length != clientBytes.Length)
        {
            return false;
        }

        var fullBytes = bits / 8;
        for (var i = 0; i < fullBytes; i++)
        {
            if (networkBytes[i] != clientBytes[i])
            {
                return false;
            }
        }

        var remaining = bits % 8;
        if (remaining == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remaining));
        return (networkBytes[fullBytes] & mask) == (clientBytes[fullBytes] & mask);
    }

    private static IPAddress Unmap(IPAddress address)
        => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    /// <summary>
    /// netip.ParseAddr is stricter than IPAddress.TryParse: it rejects IPv4
    /// shorthand ("127.1") and leading zeros in IPv4 octets.
    /// </summary>
    internal static bool TryParseAddress(string text, out IPAddress address)
    {
        address = IPAddress.None;
        if (text.Length == 0 || !IsGoParseable(text))
        {
            return false;
        }

        return IPAddress.TryParse(text, out address!);
    }

    private static bool IsGoParseable(string text)
    {
        if (text.Contains(':', StringComparison.Ordinal))
        {
            return IPAddress.TryParse(text, out _);
        }

        var parts = text.Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        foreach (var part in parts)
        {
            if (part.Length == 0 || part.Length > 3 || (part.Length > 1 && part[0] == '0'))
            {
                return false;
            }

            foreach (var c in part)
            {
                if (c is < '0' or > '9')
                {
                    return false;
                }
            }

            if (!byte.TryParse(part, out _))
            {
                return false;
            }
        }

        return true;
    }
}
