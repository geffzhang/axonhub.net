using System.Text.Json;
using AxonHub.Api.Billing;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks modelCardToChannelModelPrice (channel_price.go:58-88) and the Go wire
/// shape the auto-priced rows store: one usage_per_unit item per positive cost,
/// shortest-round-trip decimals serialized as strings, and no price at all when
/// every cost is unusable.
/// </summary>
public sealed class ModelCardPricingTests
{
    [Fact]
    public void CardCostsBecomeUsagePerUnitItemsInReferenceOrder()
    {
        var card = new ModelCard
        {
            Cost = new ModelCardCost
            {
                Input = 0.03,
                Output = 0.06,
                CacheRead = 0.01,
                CacheWrite = 0.02,
            },
        };

        var price = ModelCardPricing.ToPrice(card);

        Assert.NotNull(price);
        Assert.NotNull(price.Items);
        Assert.Equal(
            new[]
            {
                PriceItemCodes.PromptTokens,
                PriceItemCodes.CompletionTokens,
                PriceItemCodes.PromptCachedTokens,
                PriceItemCodes.PromptWriteCachedTokens,
            },
            price.Items.Select(item => item.ItemCode).ToArray());
        decimal?[] expectedCosts = [0.03m, 0.06m, 0.01m, 0.02m];
        Assert.Equal(
            expectedCosts,
            price.Items.Select(item => item.Pricing.UsagePerUnit).ToArray());
        Assert.All(
            price.Items,
            item => Assert.Equal(PricingModes.UsagePerUnit, item.Pricing.Mode));
        Assert.All(price.Items, item => Assert.Null(item.Pricing.FlatFee));
        Assert.All(price.Items, item => Assert.Null(item.PromptWriteCacheVariants));
        Assert.Null(price.Schedule);
    }

    [Fact]
    public void OnlyPositiveFiniteCostsBecomeItems()
    {
        var card = new ModelCard
        {
            Cost = new ModelCardCost
            {
                Input = 0,
                Output = -1,
                CacheRead = double.NaN,
                CacheWrite = double.PositiveInfinity,
            },
        };

        Assert.Null(ModelCardPricing.ToPrice(card));
        Assert.Null(ModelCardPricing.ToPrice(null));
        Assert.Null(ModelCardPricing.ToPrice(new ModelCard()));
        Assert.Null(ModelCardPricing.ToPrice(new ModelCard { Cost = new ModelCardCost() }));
    }

    [Fact]
    public void SerializedPriceMatchesTheGoWireShape()
    {
        var json = ModelCardPricing.ToPriceJson("""{"cost":{"input":0.03}}""");

        // shopspring decimals serialize as quoted strings with trailing zeros
        // trimmed; nil optional fields are omitted entirely.
        Assert.Equal(
            """{"items":[{"itemCode":"prompt_tokens","pricing":{"mode":"usage_per_unit","usagePerUnit":"0.03"}}]}""",
            json);
    }

    [Fact]
    public void TheShortestRoundTripFloatIsKept()
    {
        var json = ModelCardPricing.ToPriceJson(
            """{"cost":{"input":0.30000000000000004}}""");

        Assert.Equal(
            """{"items":[{"itemCode":"prompt_tokens","pricing":{"mode":"usage_per_unit","usagePerUnit":"0.30000000000000004"}}]}""",
            json);
    }

    [Fact]
    public void NullCardsAndNullCostsProduceNoPrice()
    {
        Assert.Null(ModelCardPricing.ToPriceJson("null"));
        Assert.Null(ModelCardPricing.ToPriceJson("{}"));
        Assert.Null(ModelCardPricing.ToPriceJson("""{"cost":null}"""));
        Assert.Null(ModelCardPricing.ToPriceJson("""{"cost":{"input":null}}"""));
    }

    [Fact]
    public void GoFieldMatchingIgnoresCaseAndUnknownFields()
    {
        var json = ModelCardPricing.ToPriceJson(
            """{"Cost":{"Input":1.5,"unknown":true},"extra":[1,2]}""");

        Assert.Equal(
            """{"items":[{"itemCode":"prompt_tokens","pricing":{"mode":"usage_per_unit","usagePerUnit":"1.5"}}]}""",
            json);
    }

    [Fact]
    public void OverflowingAndMistypedCostsAreDecodeErrors()
    {
        // Go's encoding/json rejects a number that overflows float64 instead of
        // producing +/-Inf, and rejects strings for a float64 field.
        Assert.Throws<JsonException>(
            () => ModelCardPricing.ToPriceJson("""{"cost":{"input":1e309}}"""));
        Assert.Throws<JsonException>(
            () => ModelCardPricing.ToPriceJson("""{"cost":{"input":"0.03"}}"""));
    }
}
