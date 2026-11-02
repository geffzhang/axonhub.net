using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Xai.Subscription;

/// <summary>Port of llm/transformer/xai/subscription/plan.go.</summary>
public static class SubscriptionPlan
{
    /// <summary>
    /// Port of <c>PlanFromAccessToken</c>: reads the provider-issued tier
    /// claim for display only. Authorization continues to rely on the token
    /// endpoint, not this unsigned payload.
    /// </summary>
    public static string PlanFromAccessToken(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            return string.Empty;
        }

        byte[] payload;
        try
        {
            payload = Base64Url.DecodeFromChars(parts[1]);
        }
        catch (FormatException)
        {
            return string.Empty;
        }

        try
        {
            // Go's json.Unmarshal repairs ill-formed UTF-8 inside string
            // literals instead of failing (GoJsonUtf8).
            var sanitized = GoJsonUtf8.SanitizeStringLiterals(payload);
            using var document = JsonDocument.Parse(sanitized ?? payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("tier", out var tier))
            {
                return string.Empty;
            }

            // json.Number first: only a valid JSON number literal reaches the
            // numeric map, and a number that fails ParseUint falls through to
            // the string unmarshal, which errors for numbers — so it yields "".
            if (tier.ValueKind == JsonValueKind.Number)
            {
                return ulong.TryParse(
                    tier.GetRawText(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var numericTier)
                    ? PlanFromTierNumber(numericTier)
                    : string.Empty;
            }

            if (tier.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            var text = tier.GetString() ?? string.Empty;
            if (ulong.TryParse(
                    GoStrings.TrimSpace(text),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsedTier))
            {
                return PlanFromTierNumber(parsedTier);
            }

            return PlanFromTierName(text);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string PlanFromTierNumber(ulong tier) => tier switch
    {
        0 => "Free",
        1 => "SuperGrok",
        2 => "X Basic",
        3 => "X Premium",
        4 => "X Premium Plus",
        5 => "SuperGrok Heavy",
        6 => "SuperGrok Lite",
        7 => "SuperGrok Plus",
        _ => string.Empty,
    };

    private static string PlanFromTierName(string raw)
    {
        var tier = GoStrings.TrimSpace(raw).ToLowerInvariant();
        tier = tier.Replace("-", "_", StringComparison.Ordinal);
        tier = string.Join('_', tier.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return tier switch
        {
            "free" or "grok_free" or "grokfree" or "free_tier" or "freetier" or "grok_basic" or "grokbasic"
                => "Free",
            "supergrok" or "grokpro" or "supergrok_pro" or "supergrokpro" or "paid" or "pro"
                => "SuperGrok",
            "supergrok_lite" or "supergroklite" => "SuperGrok Lite",
            "supergrok_heavy" or "supergrokheavy" => "SuperGrok Heavy",
            "supergrok_plus" or "supergrokplus" => "SuperGrok Plus",
            "x_basic" or "xbasic" or "basic" => "X Basic",
            "x_premium" or "xpremium" => "X Premium",
            "x_premium_plus" or "xpremiumplus" or "x_premium+" => "X Premium Plus",
            _ => string.Empty,
        };
    }
}
