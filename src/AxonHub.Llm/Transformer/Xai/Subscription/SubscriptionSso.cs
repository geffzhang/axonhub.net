using System.Text;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Xai.Subscription;

/// <summary>Port of llm/transformer/xai/subscription/sso.go.</summary>
public static class SubscriptionSso
{
    public const int MaxSsoTokenLength = 16 << 10;

    /// <summary>
    /// Port of <c>NormalizeSSOToken</c>: trims, strips a <c>cookie:</c>
    /// prefix, extracts the first non-empty <c>sso</c>/<c>sso-rw</c> cookie
    /// from a cookie-string form, and otherwise treats the whole value as the
    /// token.
    /// </summary>
    public static string NormalizeSsoToken(string value)
    {
        value = GoStrings.TrimSpace(value);
        if (value.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
        {
            value = GoStrings.TrimSpace(value["cookie:".Length..]);
        }

        var sawCookie = false;
        foreach (var part in value.Split(';'))
        {
            var trimmed = GoStrings.TrimSpace(part);
            var cut = trimmed.IndexOf('=');
            if (cut < 0)
            {
                continue;
            }

            var name = GoStrings.TrimSpace(trimmed[..cut]);
            var token = trimmed[(cut + 1)..];
            switch (name.ToLowerInvariant())
            {
                case "sso":
                case "sso-rw":
                    sawCookie = true;
                    if (SanitizeSsoToken(token) is { Length: > 0 } sanitized)
                    {
                        return sanitized;
                    }

                    break;
            }
        }

        if (sawCookie)
        {
            return string.Empty;
        }

        var firstCut = value.IndexOf(';');
        if (firstCut >= 0)
        {
            value = GoStrings.TrimSpace(value[..firstCut]);
        }

        return SanitizeSsoToken(value);
    }

    /// <summary>Port of <c>sanitizeSSOToken</c>: strips CR/LF/NUL and caps the length.</summary>
    public static string SanitizeSsoToken(string value)
    {
        value = GoStrings.TrimSpace(value)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\x00", string.Empty, StringComparison.Ordinal);
        // Go's len() counts UTF-8 bytes.
        if (Encoding.UTF8.GetByteCount(value) > MaxSsoTokenLength)
        {
            return string.Empty;
        }

        return value;
    }
}
