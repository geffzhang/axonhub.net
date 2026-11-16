using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Llm.Transformer.Xai.Subscription;

/// <summary>Port of <c>BillingWindow</c> (billing.go:16-21).</summary>
public sealed class BillingWindow
{
    /// <summary>Go: <c>json:"usage_percent"</c> (no omitempty — zero is emitted).</summary>
    [JsonPropertyName("usage_percent")]
    public double UsagePercent { get; set; }

    /// <summary>Go: <c>json:"reset_at,omitempty"</c>; empty and null both omit (C001).</summary>
    [JsonPropertyName("reset_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResetAt { get; set; }

    /// <summary>Go: <c>json:"limit_usd,omitempty"</c>.</summary>
    [JsonPropertyName("limit_usd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double LimitUsd { get; set; }

    /// <summary>Go: <c>json:"used_usd,omitempty"</c>.</summary>
    [JsonPropertyName("used_usd")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double UsedUsd { get; set; }
}

/// <summary>Port of <c>BillingSummary</c> (billing.go:23-27).</summary>
public sealed class BillingSummary
{
    /// <summary>Go: <c>json:"plan,omitempty"</c>; empty and null both omit (C001).</summary>
    [JsonPropertyName("plan")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Plan { get; set; }

    [JsonPropertyName("weekly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BillingWindow? Weekly { get; set; }

    [JsonPropertyName("monthly")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BillingWindow? Monthly { get; set; }
}

/// <summary>
/// Port of <c>ParseBillingResponses</c> and its helpers (billing.go:29-151):
/// merges the weekly credit-usage payload and the monthly plan payload.
/// </summary>
/// <remarks>
/// Go's decode rules are reproduced rather than approximated: object keys are
/// matched exactly first and case-insensitively second (last duplicate wins),
/// a JSON <c>null</c> on a non-pointer field is a no-op, and
/// <c>billingAmount</c> first tries the <c>{"val": ...}</c> wrapper and then
/// the whole value.
/// </remarks>
public static class SubscriptionBilling
{
    private const double SuperGrokLimitCents = 15_000;
    private const double SuperGrokHeavyLimitCents = 150_000;

    /// <summary>
    /// Port of <c>ParseBillingResponses</c> (billing.go:69-102). Returns the
    /// merged summary; windows with no data are left null.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Neither payload carries a <c>config</c> object.
    /// </exception>
    /// <exception cref="JsonException">
    /// A payload is not a decodable document or a field has the wrong JSON
    /// type (the decoder's message text differs from Go — C093).
    /// </exception>
    public static BillingSummary ParseBillingResponses(byte[] weeklyBody, byte[] monthlyBody)
    {
        var weekly = ParseBillingPayload(weeklyBody);
        var monthly = ParseBillingPayload(monthlyBody);
        if (weekly.Config is null && monthly.Config is null)
        {
            throw new InvalidOperationException("xAI billing responses contain no config");
        }

        var result = new BillingSummary();
        if (weekly.Config is not null
            && (weekly.Config.CreditUsagePercent is not null || weekly.Config.CurrentPeriod is not null))
        {
            result.Weekly = new BillingWindow
            {
                UsagePercent = ValueOrZero(weekly.Config.CreditUsagePercent),
            };
            if (weekly.Config.CurrentPeriod is not null)
            {
                result.Weekly.ResetAt = EmptyToNull(GoStrings.TrimSpace(weekly.Config.CurrentPeriod.End));
            }
        }

        if (monthly.Config is not null
            && (monthly.Config.MonthlyLimit is not null
                || monthly.Config.Used is not null
                || GoStrings.TrimSpace(monthly.Config.BillingPeriodEnd).Length != 0))
        {
            var limitCents = ValueOrZero(monthly.Config.MonthlyLimit);
            var usedCents = ValueOrZero(monthly.Config.Used);
            result.Monthly = new BillingWindow
            {
                LimitUsd = limitCents / 100,
                UsedUsd = usedCents / 100,
                ResetAt = EmptyToNull(GoStrings.TrimSpace(monthly.Config.BillingPeriodEnd)),
            };
            if (limitCents > 0)
            {
                result.Monthly.UsagePercent = Math.Min(usedCents, limitCents) / limitCents * 100;
            }

            result.Plan = EmptyToNull(BillingPlan(limitCents));
        }

        return result;
    }

    /// <summary>Port of <c>billingPayload</c> / <c>billingConfig</c> / <c>billingPeriod</c>.</summary>
    private sealed class BillingPayload
    {
        public BillingConfig? Config { get; set; }
    }

    private sealed class BillingConfig
    {
        public BillingPeriod? CurrentPeriod { get; set; }

        public double? CreditUsagePercent { get; set; }

        public double? MonthlyLimit { get; set; }

        public double? Used { get; set; }

        public string BillingPeriodEnd { get; set; } = string.Empty;
    }

    private sealed class BillingPeriod
    {
        public string End { get; set; } = string.Empty;
    }

    private static BillingPayload ParseBillingPayload(byte[] data)
    {
        using var document = JsonDocument.Parse(GoJsonUtf8.SanitizeStringLiterals(data) ?? data);
        var root = document.RootElement;

        // Go: unmarshaling JSON null into a struct is a no-op.
        if (root.ValueKind == JsonValueKind.Null)
        {
            return new BillingPayload();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("cannot unmarshal non-object into billing payload");
        }

        var payload = new BillingPayload();
        if (!TryGetProperty(root, "config", out var configElement))
        {
            return payload;
        }

        // Go: a null config pointer stays nil; a non-object field errors.
        if (configElement.ValueKind == JsonValueKind.Null)
        {
            return payload;
        }

        if (configElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("cannot unmarshal config into billingConfig");
        }

        var config = new BillingConfig();

        if (TryGetProperty(configElement, "currentPeriod", out var currentPeriod)
            && currentPeriod.ValueKind != JsonValueKind.Null)
        {
            if (currentPeriod.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("cannot unmarshal currentPeriod into billingPeriod");
            }

            var period = new BillingPeriod();
            if (TryGetProperty(currentPeriod, "end", out var end))
            {
                // Go: null into a string field is a no-op, any other non-string errors.
                period.End = end.ValueKind switch
                {
                    JsonValueKind.Null => string.Empty,
                    JsonValueKind.String => end.GetString() ?? string.Empty,
                    _ => throw new JsonException("cannot unmarshal end into string"),
                };
            }

            config.CurrentPeriod = period;
        }

        if (TryGetProperty(configElement, "creditUsagePercent", out var credit)
            && credit.ValueKind != JsonValueKind.Null)
        {
            if (credit.ValueKind != JsonValueKind.Number
                || !GoCast.TryParseFloat(credit.GetRawText(), out var creditValue))
            {
                throw new JsonException("cannot unmarshal creditUsagePercent into float64");
            }

            config.CreditUsagePercent = creditValue;
        }

        if (TryGetProperty(configElement, "monthlyLimit", out var monthlyLimit))
        {
            config.MonthlyLimit = ParseBillingAmount(monthlyLimit);
        }

        if (TryGetProperty(configElement, "used", out var used))
        {
            config.Used = ParseBillingAmount(used);
        }

        if (TryGetProperty(configElement, "billingPeriodEnd", out var periodEnd))
        {
            config.BillingPeriodEnd = periodEnd.ValueKind switch
            {
                JsonValueKind.Null => string.Empty,
                JsonValueKind.String => periodEnd.GetString() ?? string.Empty,
                _ => throw new JsonException("cannot unmarshal billingPeriodEnd into string"),
            };
        }

        payload.Config = config;
        return payload;
    }

    /// <summary>Port of <c>billingAmount.UnmarshalJSON</c> (billing.go:49-67).</summary>
    private static double? ParseBillingAmount(JsonElement element)
    {
        // The wrapper attempt only succeeds for objects; a present "val"
        // (even null) wins over the whole value.
        if (element.ValueKind == JsonValueKind.Object && TryGetProperty(element, "val", out var wrapped))
        {
            return ParseBillingNumber(wrapped);
        }

        return ParseBillingNumber(element);
    }

    /// <summary>Port of <c>parseBillingNumber</c> (billing.go:112-133).</summary>
    private static double? ParseBillingNumber(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.Number:
                // Go: json.Number.Float64; overflow is an error, not ±Inf.
                if (!GoCast.TryParseFloat(element.GetRawText(), out var value))
                {
                    throw new JsonException("billing amount is not a valid number");
                }

                return value;
            case JsonValueKind.String:
                var text = element.GetString() ?? string.Empty;
                if (!GoCast.TryParseFloat(GoStrings.TrimSpace(text), out var parsed))
                {
                    throw new JsonException("billing amount is not a valid number");
                }

                return parsed;
            default:
                throw new JsonException("billing amount is not a valid number");
        }
    }

    /// <summary>
    /// Go <c>encoding/json</c> matches object keys to struct fields exactly
    /// first and case-insensitively second; a duplicate key keeps the last
    /// value.
    /// </summary>
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var found = false;
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                found = true;
            }
        }

        return found;
    }

    private static double ValueOrZero(double? value) => value ?? 0;

    /// <summary>Go's empty string is omitted on the wire; C# models that as null (C001).</summary>
    private static string? EmptyToNull(string value) => value.Length == 0 ? null : value;

    private static string BillingPlan(double limitCents) =>
        Math.Round(limitCents, MidpointRounding.AwayFromZero) switch
        {
            SuperGrokLimitCents => "SuperGrok",
            SuperGrokHeavyLimitCents => "SuperGrok Heavy",
            _ => string.Empty,
        };
}
