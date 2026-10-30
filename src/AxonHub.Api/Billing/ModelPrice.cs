using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxonHub.Api.Billing;

/// <summary>Port of objects.PricingMode (internal/objects/price.go).</summary>
public static class PricingModes
{
    public const string FlatFee = "flat_fee";

    public const string UsagePerUnit = "usage_per_unit";

    public const string Tiered = "usage_tiered";

    public const string Volume = "usage_volume";
}

/// <summary>Port of objects.PriceItemCode.</summary>
public static class PriceItemCodes
{
    public const string PromptTokens = "prompt_tokens";

    public const string CompletionTokens = "completion_tokens";

    public const string PromptCachedTokens = "prompt_cached_tokens";

    public const string PromptWriteCachedTokens = "prompt_write_cached_tokens";
}

/// <summary>Port of objects.PromptWriteCacheVariantCode.</summary>
public static class PromptWriteCacheVariantCodes
{
    public const string FiveMin = "five_min";

    public const string OneHour = "one_hour";
}

/// <summary>
/// Port of objects.Pricing. Decimals travel as strings produced by shopspring's
/// decimal.Decimal.MarshalJSON, with trailing zeros trimmed by String().
/// </summary>
public sealed class Pricing
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = string.Empty;

    [JsonPropertyName("flatFee")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(GoNullableDecimalConverter))]
    public decimal? FlatFee { get; set; }

    [JsonPropertyName("usagePerUnit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(GoNullableDecimalConverter))]
    public decimal? UsagePerUnit { get; set; }

    [JsonPropertyName("usageTiered")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TieredPricing? UsageTiered { get; set; }
}

/// <summary>Port of objects.TieredPricing.</summary>
public sealed class TieredPricing
{
    [JsonPropertyName("tiers")]
    public List<PriceTier>? Tiers { get; set; }
}

/// <summary>Port of objects.PriceTier.</summary>
public sealed class PriceTier
{
    /// <summary>Upper bound; null means no upper bound (must be the last tier).</summary>
    [JsonPropertyName("upTo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? UpTo { get; set; }

    [JsonPropertyName("pricePerUnit")]
    [JsonConverter(typeof(GoDecimalConverter))]
    public decimal PricePerUnit { get; set; }
}

/// <summary>Port of objects.PromptWriteCacheVariant.</summary>
public sealed class PromptWriteCacheVariant
{
    [JsonPropertyName("variantCode")]
    public string VariantCode { get; set; } = string.Empty;

    [JsonPropertyName("pricing")]
    public Pricing Pricing { get; set; } = new();
}

/// <summary>Port of objects.ModelPriceItem.</summary>
public sealed class ModelPriceItem
{
    [JsonPropertyName("itemCode")]
    public string ItemCode { get; set; } = string.Empty;

    [JsonPropertyName("pricing")]
    public Pricing Pricing { get; set; } = new();

    [JsonPropertyName("promptWriteCacheVariants")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PromptWriteCacheVariant>? PromptWriteCacheVariants { get; set; }

    /// <summary>
    /// Port of FindPromptWriteCacheVariantPricing: the first variant with the
    /// given code, falling back to the item pricing.
    /// </summary>
    public Pricing FindPromptWriteCacheVariantPricing(string variantCode)
    {
        foreach (var variant in PromptWriteCacheVariants ?? [])
        {
            if (variant.VariantCode == variantCode)
            {
                // JSON null decodes to a zero-value Pricing in Go, not to null.
                return variant.Pricing ?? new Pricing();
            }
        }

        return Pricing ?? new Pricing();
    }
}

/// <summary>Port of objects.ModelPrice.</summary>
public sealed class ModelPrice
{
    [JsonPropertyName("items")]
    public List<ModelPriceItem>? Items { get; set; }

    [JsonPropertyName("schedule")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PriceSchedule? Schedule { get; set; }
}

/// <summary>Port of objects.PriceSchedule.</summary>
public sealed class PriceSchedule
{
    /// <summary>IANA timezone name ("" means UTC, like time.LoadLocation).</summary>
    [JsonPropertyName("timezone")]
    public string Timezone { get; set; } = string.Empty;

    [JsonPropertyName("overrides")]
    public List<PriceOverride>? Overrides { get; set; }
}

/// <summary>Port of objects.PriceOverride.</summary>
public sealed class PriceOverride
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Lower values win; the first override wins ties.</summary>
    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("when")]
    public OverrideWhen When { get; set; } = new();

    [JsonPropertyName("items")]
    public List<ModelPriceItem>? Items { get; set; }
}

/// <summary>Port of objects.OverrideWhen.</summary>
public sealed class OverrideWhen
{
    [JsonPropertyName("dailyTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DailyTimeRange? DailyTime { get; set; }

    /// <summary>1 = Monday ... 7 = Sunday.</summary>
    [JsonPropertyName("weekdays")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<int>? Weekdays { get; set; }

    [JsonPropertyName("dateRange")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateRange? DateRange { get; set; }
}

/// <summary>Port of objects.DailyTimeRange.</summary>
public sealed class DailyTimeRange
{
    [JsonPropertyName("start")]
    public string Start { get; set; } = string.Empty;

    [JsonPropertyName("end")]
    public string End { get; set; } = string.Empty;
}

/// <summary>Port of objects.DateRange ("YYYY-MM-DD", both ends inclusive).</summary>
public sealed class DateRange
{
    [JsonPropertyName("start")]
    public string Start { get; set; } = string.Empty;

    [JsonPropertyName("end")]
    public string End { get; set; } = string.Empty;
}

/// <summary>Port of objects.TierCost.</summary>
public sealed class TierCost
{
    [JsonPropertyName("upTo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? UpTo { get; set; }

    [JsonPropertyName("units")]
    public long Units { get; set; }

    [JsonPropertyName("subtotal")]
    [JsonConverter(typeof(GoDecimalConverter))]
    public decimal Subtotal { get; set; }
}

/// <summary>Port of objects.CostItem.</summary>
public sealed class CostItem
{
    [JsonPropertyName("itemCode")]
    public string ItemCode { get; set; } = string.Empty;

    [JsonPropertyName("promptWriteCacheVariantCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PromptWriteCacheVariantCode { get; set; }

    [JsonPropertyName("quantity")]
    public long Quantity { get; set; }

    [JsonPropertyName("tierBreakdown")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TierCost>? TierBreakdown { get; set; }

    [JsonPropertyName("subtotal")]
    [JsonConverter(typeof(GoDecimalConverter))]
    public decimal Subtotal { get; set; }
}

/// <summary>JSON options for the reference price/cost wire shapes.</summary>
public static class BillingJson
{
    /// <summary>
    /// Go's encoding/json matches field names case-insensitively; unknown
    /// fields are ignored and missing fields keep their zero values.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Serializes cost items exactly like json.Marshal(items). Go's calculator
    /// starts from a nil slice, so a result with no items is nil and marshals as
    /// JSON null — the port keeps that empty-means-null shape.
    /// </summary>
    public static string SerializeCostItems(IReadOnlyList<CostItem> items)
        => items.Count == 0 ? "null" : JsonSerializer.Serialize(items, Options);
}

/// <summary>
/// shopspring/decimal serializes as a quoted string whose trailing zeros are
/// trimmed by String(); this is the write side of that shape.
/// </summary>
public static class GoDecimal
{
    public static string Format(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        if (text.Contains('.', StringComparison.Ordinal))
        {
            text = text.TrimEnd('0').TrimEnd('.');
        }

        return text.Length == 0 ? "0" : text;
    }

    public static decimal Parse(string? text)
    {
        if (text is null)
        {
            throw new JsonException("decimal value is null");
        }

        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new JsonException("invalid decimal value: " + text);
        }

        return value;
    }
}

/// <summary>Reads quoted or unquoted decimals; a JSON null is zero, like shopspring.</summary>
public sealed class GoDecimalConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return 0m;
            case JsonTokenType.Number:
                return reader.TryGetDecimal(out var number)
                    ? number
                    : throw new JsonException("invalid decimal value");
            case JsonTokenType.String:
                return GoDecimal.Parse(reader.GetString());
            default:
                throw new JsonException("unexpected token for decimal: " + reader.TokenType);
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(GoDecimal.Format(value));
    }
}

/// <summary>Reads quoted or unquoted decimals; a JSON null stays null.</summary>
public sealed class GoNullableDecimalConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                return reader.TryGetDecimal(out var number)
                    ? number
                    : throw new JsonException("invalid decimal value");
            case JsonTokenType.String:
                return GoDecimal.Parse(reader.GetString());
            default:
                throw new JsonException("unexpected token for decimal: " + reader.TokenType);
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(GoDecimal.Format(value.Value));
    }
}
