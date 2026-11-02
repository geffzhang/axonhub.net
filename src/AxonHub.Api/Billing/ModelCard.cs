using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Api.Management;

namespace AxonHub.Api.Billing;

/// <summary>
/// Port of the pricing-relevant part of objects.ModelCard
/// (internal/objects/model.go:26-40). The auto-pricing path only reads the
/// per-token costs; the remaining card members are ignored like Go ignores
/// unknown JSON fields.
/// </summary>
public sealed class ModelCard
{
    [JsonPropertyName("cost")]
    public ModelCardCost? Cost { get; set; }
}

/// <summary>Port of objects.ModelCardCost (internal/objects/model.go:14-19).</summary>
public sealed class ModelCardCost
{
    [JsonPropertyName("input")]
    [JsonConverter(typeof(GoFloat64Converter))]
    public double? Input { get; set; }

    [JsonPropertyName("output")]
    [JsonConverter(typeof(GoFloat64Converter))]
    public double? Output { get; set; }

    [JsonPropertyName("cacheRead")]
    [JsonConverter(typeof(GoFloat64Converter))]
    public double? CacheRead { get; set; }

    [JsonPropertyName("cacheWrite")]
    [JsonConverter(typeof(GoFloat64Converter))]
    public double? CacheWrite { get; set; }
}

/// <summary>
/// Port of modelCardToChannelModelPrice (channel_price.go:58-88): a card turns
/// into up to four usage_per_unit items, skipping non-positive and non-finite
/// costs, and into no price at all when every cost is unusable.
/// </summary>
public static class ModelCardPricing
{
    /// <summary>
    /// Decodes <c>models.model_card</c> and returns the objects.ModelPrice JSON
    /// to store, or null when the card yields no items. A malformed card
    /// throws, like ent failing to scan the JSON column.
    /// </summary>
    public static string? ToPriceJson(string modelCardJson)
    {
        ArgumentNullException.ThrowIfNull(modelCardJson);

        // JSON null decodes into a nil *objects.ModelCard, which the reference
        // treats like "no priced items".
        var card = JsonSerializer.Deserialize<ModelCard>(modelCardJson, BillingJson.Options);
        var price = ToPrice(card);
        return price is null ? null : ChannelModelPriceService.SerializePrice(price);
    }

    public static ModelPrice? ToPrice(ModelCard? card)
    {
        if (card is null)
        {
            return null;
        }

        var items = new List<ModelPriceItem>(4);
        AddItem(items, PriceItemCodes.PromptTokens, card.Cost?.Input);
        AddItem(items, PriceItemCodes.CompletionTokens, card.Cost?.Output);
        AddItem(items, PriceItemCodes.PromptCachedTokens, card.Cost?.CacheRead);
        AddItem(items, PriceItemCodes.PromptWriteCachedTokens, card.Cost?.CacheWrite);

        return items.Count == 0 ? null : new ModelPrice { Items = items };
    }

    private static void AddItem(List<ModelPriceItem> items, string itemCode, double? cost)
    {
        if (cost is not { } value
            || value <= 0
            || double.IsNaN(value)
            || double.IsInfinity(value))
        {
            return;
        }

        items.Add(new ModelPriceItem
        {
            ItemCode = itemCode,
            Pricing = new Pricing
            {
                Mode = PricingModes.UsagePerUnit,
                UsagePerUnit = FromGoFloat(value),
            },
        });
    }

    /// <summary>
    /// shopspring's decimal.NewFromFloat keeps the shortest decimal that
    /// round-trips the float64; .NET's "R" format is the same shortest form and
    /// decimal.Parse accepts its exponent notation.
    /// </summary>
    private static decimal FromGoFloat(double value)
        => decimal.Parse(
            value.ToString("R", CultureInfo.InvariantCulture),
            NumberStyles.Float,
            CultureInfo.InvariantCulture);
}

/// <summary>
/// Reads a float64 the way Go's encoding/json does: JSON null leaves the zero
/// value (no-op), and a number that overflows float64 is a decode error rather
/// than System.Text.Json's Infinity.
/// </summary>
public sealed class GoFloat64Converter : JsonConverter<double?>
{
    public override double? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                var value = reader.GetDouble();
                if (!double.IsFinite(value))
                {
                    throw new JsonException(
                        "json: cannot unmarshal number into Go struct field "
                        + "ModelCard.cost of type float64");
                }

                return value;
            default:
                throw new JsonException(
                    "json: cannot unmarshal value into Go struct field "
                    + "ModelCard.cost of type float64");
        }
    }

    public override void Write(
        Utf8JsonWriter writer,
        double? value,
        JsonSerializerOptions options)
        => throw new NotSupportedException("model cards are only ever read");
}
