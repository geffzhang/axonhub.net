using System.Globalization;
using System.Text;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of the parts of Go's net/netip used by validateAllowedIPs
/// (biz/api_key.go:898-917): the parse error text is part of the observable
/// GraphQL error message, so the parsers reproduce netip's messages.
/// </summary>
/// <remarks>
/// Only the error surface is reproduced; the parsed address value is reduced
/// to the IPv4/IPv6 kind and IPv6 zone, which is all ParsePrefix needs.
/// </remarks>
public static class GoNetip
{
    public static bool TryParseAddr(string value, out string error)
    {
        var result = ParseAddr(value);
        error = result.Error ?? string.Empty;
        return result.Error is null;
    }

    public static bool TryParsePrefix(string value, out string error)
    {
        var result = ParsePrefix(value);
        error = result.Error ?? string.Empty;
        return result.Error is null;
    }

    /// <summary>Port of strconv.Quote for the ASCII inputs these errors carry.</summary>
    public static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\a':
                    builder.Append("\\a");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\v':
                    builder.Append("\\v");
                    break;
                default:
                    if (c < 0x20 || c == 0x7f)
                    {
                        builder.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.Append('"').ToString();
    }

    private readonly record struct AddrParseResult(bool Ok, bool IsV6, string Zone, string? Error);

    private readonly record struct PrefixParseResult(bool Ok, string? Error);

    /// <summary>Port of netip.ParsePrefix (netip.go:1368-1402).</summary>
    private static PrefixParseResult ParsePrefix(string value)
    {
        var slash = value.LastIndexOf('/');
        if (slash < 0)
        {
            return Fail("no '/'");
        }

        var addr = ParseAddr(value[..slash]);
        if (addr.Error is not null)
        {
            return Fail(addr.Error);
        }

        if (addr.IsV6 && addr.Zone.Length > 0)
        {
            return Fail("IPv6 zones cannot be present in a prefix");
        }

        var bitsText = value[(slash + 1)..];
        if (bitsText.Length > 1 && (bitsText[0] < '1' || bitsText[0] > '9'))
        {
            return Fail("bad bits after slash: " + Quote(bitsText));
        }

        // strconv.Atoi accepts a leading sign; the guard above only rejects
        // signs and leading zeroes for multi-character inputs.
        if (!long.TryParse(
                bitsText,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var bits))
        {
            return Fail("bad bits after slash: " + Quote(bitsText));
        }

        var maxBits = addr.IsV6 ? 128 : 32;
        if (bits < 0 || bits > maxBits)
        {
            return Fail("prefix length out of range");
        }

        return new PrefixParseResult(true, null);

        PrefixParseResult Fail(string message)
            => new(false, "netip.ParsePrefix(" + Quote(value) + "): " + message);
    }

    /// <summary>Port of netip.ParseAddr (netip.go:114-129).</summary>
    private static AddrParseResult ParseAddr(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            switch (value[i])
            {
                case '.':
                    return ParseIPv4(value);
                case ':':
                    return ParseIPv6(value);
                case '%':
                    // Assume an IPv6 address with a zone, but no address.
                    return AddrError(value, "missing IPv6 address", at: string.Empty);
            }
        }

        return AddrError(value, "unable to parse IP", at: string.Empty);
    }

    /// <summary>Port of parseIPv4/parseIPv4Fields (netip.go:154-202).</summary>
    private static AddrParseResult ParseIPv4(string inValue)
    {
        var fields = new byte[4];
        var error = ParseIPv4Fields(inValue, 0, inValue.Length, fields);
        if (error is not null)
        {
            return new AddrParseResult(false, false, string.Empty, error);
        }

        return new AddrParseResult(true, false, string.Empty, null);
    }

    private static string? ParseIPv4Fields(string inValue, int off, int end, byte[] fields)
    {
        var s = inValue[off..end];
        var val = 0;
        var pos = 0;
        var digitLength = 0;

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is >= '0' and <= '9')
            {
                if (digitLength == 1 && val == 0)
                {
                    return AddrError(inValue, "IPv4 field has octet with leading zero", string.Empty).Error;
                }

                val = (val * 10) + (c - '0');
                digitLength++;
                if (val > 255)
                {
                    return AddrError(inValue, "IPv4 field has value >255", string.Empty).Error;
                }
            }
            else if (c == '.')
            {
                if (i == 0 || i == s.Length - 1 || s[i - 1] == '.')
                {
                    return AddrError(
                        inValue,
                        "IPv4 field must have at least one digit",
                        s[i..]).Error;
                }

                if (pos == 3)
                {
                    return AddrError(inValue, "IPv4 address too long", string.Empty).Error;
                }

                fields[pos] = (byte)val;
                pos++;
                val = 0;
                digitLength = 0;
            }
            else
            {
                return AddrError(inValue, "unexpected character", s[i..]).Error;
            }
        }

        if (pos < 3)
        {
            return AddrError(inValue, "IPv4 address too short", string.Empty).Error;
        }

        fields[3] = (byte)val;
        return null;
    }

    /// <summary>Port of parseIPv6 (netip.go:205-348).</summary>
    private static AddrParseResult ParseIPv6(string inValue)
    {
        var s = inValue;
        var zone = string.Empty;

        var percent = s.IndexOf('%');
        if (percent != -1)
        {
            zone = s[(percent + 1)..];
            s = s[..percent];
            if (zone.Length == 0)
            {
                return AddrError(inValue, "zone must be a non-empty string", string.Empty);
            }
        }

        var ip = new byte[16];
        var ellipsis = -1;

        if (s.Length >= 2 && s[0] == ':' && s[1] == ':')
        {
            ellipsis = 0;
            s = s[2..];
            if (s.Length == 0)
            {
                return new AddrParseResult(true, true, zone, null);
            }
        }

        var i = 0;
        while (i < 16)
        {
            var off = 0;
            uint acc = 0;
            for (; off < s.Length; off++)
            {
                var c = s[off];
                if (c is >= '0' and <= '9')
                {
                    acc = (acc << 4) + (uint)(c - '0');
                }
                else if (c is >= 'a' and <= 'f')
                {
                    acc = (acc << 4) + (uint)(c - 'a' + 10);
                }
                else if (c is >= 'A' and <= 'F')
                {
                    acc = (acc << 4) + (uint)(c - 'A' + 10);
                }
                else
                {
                    break;
                }

                if (off > 3)
                {
                    return AddrError(inValue, "each group must have 4 or less digits", s);
                }

                if (acc > ushort.MaxValue)
                {
                    return AddrError(inValue, "IPv6 field has value >=2^16", s);
                }
            }

            if (off == 0)
            {
                return AddrError(
                    inValue,
                    "each colon-separated field must have at least one digit",
                    s);
            }

            if (off < s.Length && s[off] == '.')
            {
                if (ellipsis < 0 && i != 12)
                {
                    return AddrError(
                        inValue,
                        "embedded IPv4 address must replace the final 2 fields of the address",
                        s);
                }

                if (i + 4 > 16)
                {
                    return AddrError(
                        inValue,
                        "too many hex fields to fit an embedded IPv4 at the end of the address",
                        s);
                }

                var error = ParseIPv4Fields(inValue, inValue.Length - s.Length - (zone.Length > 0 ? zone.Length + 1 : 0), inValue.Length - (zone.Length > 0 ? zone.Length + 1 : 0), ip[i..(i + 4)]);
                if (error is not null)
                {
                    return new AddrParseResult(false, false, string.Empty, error);
                }

                s = string.Empty;
                i += 4;
                break;
            }

            ip[i] = (byte)(acc >> 8);
            ip[i + 1] = (byte)acc;
            i += 2;

            s = s[off..];
            if (s.Length == 0)
            {
                break;
            }

            if (s[0] != ':')
            {
                return AddrError(inValue, "unexpected character, want colon", s);
            }

            if (s.Length == 1)
            {
                return AddrError(inValue, "colon must be followed by more characters", s);
            }

            s = s[1..];

            if (s[0] == ':')
            {
                if (ellipsis >= 0)
                {
                    return AddrError(inValue, "multiple :: in address", s);
                }

                ellipsis = i;
                s = s[1..];
                if (s.Length == 0)
                {
                    break;
                }
            }
        }

        if (s.Length != 0)
        {
            return AddrError(inValue, "trailing garbage after address", s);
        }

        if (i < 16)
        {
            if (ellipsis < 0)
            {
                return AddrError(inValue, "address string too short", string.Empty);
            }
        }
        else if (ellipsis >= 0)
        {
            return AddrError(inValue, "the :: must expand to at least one field of zeros", string.Empty);
        }

        return new AddrParseResult(true, true, zone, null);
    }

    private static AddrParseResult AddrError(string inValue, string message, string at)
    {
        var text = "ParseAddr(" + Quote(inValue) + "): " + message;
        if (at.Length > 0)
        {
            text += " (at " + Quote(at) + ")";
        }

        return new AddrParseResult(false, false, string.Empty, text);
    }
}
