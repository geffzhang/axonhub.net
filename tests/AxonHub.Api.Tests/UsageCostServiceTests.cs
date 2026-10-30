using AxonHub.Api.Billing;
using AxonHub.Llm;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Behavioural tests for the stored-price lookup seam
/// (internal/server/biz/usage_log.go: computeUsageCost / InjectUsageCostEnabled)
/// and the JSON wire shapes of objects.ModelPrice / objects.CostItem.
/// </summary>
public sealed class UsageCostServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private const string TwoItemPrice = """
        {"items":[
          {"itemCode":"prompt_tokens","pricing":{"mode":"usage_per_unit","usagePerUnit":"0.03"}},
          {"itemCode":"completion_tokens","pricing":{"mode":"usage_per_unit","usagePerUnit":"0.06"}}
        ]}
        """;

    private static Usage UsageTokens(long prompt, long completion)
        => new() { PromptTokens = prompt, CompletionTokens = completion, TotalTokens = prompt + completion };

    private static ChannelModelPriceRecord PriceRow(
        long channelId,
        string modelId,
        string price,
        string referenceId = "ref-1")
        => new()
        {
            ChannelId = channelId,
            ModelId = modelId,
            Price = price,
            ReferenceId = referenceId,
        };

    private static UsageCostService NewService(FakeChannelPricingSource source)
        => new(source, new TestTimeProvider(Now));

    [Fact]
    public void ComputesCostFromStoredPrice()
    {
        var source = new FakeChannelPricingSource();
        source.Prices.Add(PriceRow(1, "gpt-4o", TwoItemPrice, "ref-abc"));

        var cost = NewService(source).Compute(1, "gpt-4o", UsageTokens(1000, 500));

        Assert.NotNull(cost);
        Assert.Equal(0.00006m, cost.Total);
        Assert.Equal(0.00006d, cost.TotalCost);
        Assert.Equal("ref-abc", cost.ReferenceId);
        Assert.Equal(2, cost.Items.Count);
    }

    [Fact]
    public void MissingRowsReturnNull()
    {
        var source = new FakeChannelPricingSource();

        Assert.Null(NewService(source).Compute(1, "gpt-4o", UsageTokens(1, 1)));
    }

    [Fact]
    public void UnknownModelReturnsNull()
    {
        var source = new FakeChannelPricingSource();
        source.Prices.Add(PriceRow(1, "gpt-4o", TwoItemPrice));

        Assert.Null(NewService(source).Compute(1, "gpt-4o-mini", UsageTokens(1, 1)));
    }

    [Fact]
    public void InvalidChannelIdReturnsNull()
    {
        var source = new FakeChannelPricingSource();
        source.Prices.Add(PriceRow(0, "gpt-4o", TwoItemPrice));

        Assert.Null(NewService(source).Compute(0, "gpt-4o", UsageTokens(1, 1)));
    }

    [Fact]
    public void MalformedPriceFailsTheWholeChannel()
    {
        var source = new FakeChannelPricingSource();
        source.Prices.Add(PriceRow(1, "good-model", TwoItemPrice));
        source.Prices.Add(PriceRow(1, "bad-model", "{not json"));

        // preloadModelPrices drops the entire channel cache when one row fails
        // to decode, so even the good model has no price.
        Assert.Null(NewService(source).Compute(1, "good-model", UsageTokens(1, 1)));
    }

    [Fact]
    public void JsonNullPriceDecodesAsEmptyPrice()
    {
        var source = new FakeChannelPricingSource();
        source.Prices.Add(PriceRow(1, "gpt-4o", "null", "ref-null"));

        var cost = NewService(source).Compute(1, "gpt-4o", UsageTokens(1000, 500));

        Assert.NotNull(cost);
        Assert.Empty(cost.Items);
        Assert.Equal(0m, cost.Total);
        Assert.Equal("ref-null", cost.ReferenceId);
    }

    [Fact]
    public void PriceDecimalsAcceptQuotedAndUnquotedForms()
    {
        var source = new FakeChannelPricingSource();
        source.Prices.Add(PriceRow(
            1,
            "gpt-4o",
            """
            {"Items":[{"ItemCode":"prompt_tokens","Pricing":{"Mode":"usage_per_unit","UsagePerUnit":0.03}}]}
            """));

        var cost = NewService(source).Compute(1, "gpt-4o", UsageTokens(1000, 500));

        Assert.NotNull(cost);
        Assert.Equal(0.00003m, cost.Total);
    }

    [Fact]
    public void InjectUsageCostEnabledRequiresExactTrueString()
    {
        var source = new FakeChannelPricingSource();
        var service = NewService(source);

        Assert.False(service.InjectUsageCostEnabled());

        source.Values[UsageCostService.InjectUsageCostKey] = "false";
        Assert.False(service.InjectUsageCostEnabled());

        source.Values[UsageCostService.InjectUsageCostKey] = "TRUE";
        Assert.False(service.InjectUsageCostEnabled());

        source.Values[UsageCostService.InjectUsageCostKey] = "true";
        Assert.True(service.InjectUsageCostEnabled());
    }

    [Fact]
    public void CostItemsSerializeLikeGoJson()
    {
        var usage = new Usage
        {
            PromptTokens = 1000,
            CompletionTokens = 500,
            TotalTokens = 1500,
            PromptTokensDetails = new PromptTokensDetails { CachedTokens = 300 },
        };
        var price = new ModelPrice
        {
            Items =
            [
                new ModelPriceItem
                {
                    ItemCode = PriceItemCodes.PromptTokens,
                    Pricing = new Pricing { Mode = PricingModes.UsagePerUnit, UsagePerUnit = 0.03m },
                },
                new ModelPriceItem
                {
                    ItemCode = PriceItemCodes.CompletionTokens,
                    Pricing = new Pricing { Mode = PricingModes.UsagePerUnit, UsagePerUnit = 0.06m },
                },
                new ModelPriceItem
                {
                    ItemCode = PriceItemCodes.PromptCachedTokens,
                    Pricing = new Pricing { Mode = PricingModes.UsagePerUnit, UsagePerUnit = 0.015m },
                },
            ],
        };

        var result = UsageCostCalculator.Compute(usage, price, Now);
        var json = BillingJson.SerializeCostItems(result.Items);

        Assert.Equal(
            "[{\"itemCode\":\"prompt_tokens\",\"quantity\":700,\"subtotal\":\"0.000021\"},"
            + "{\"itemCode\":\"completion_tokens\",\"quantity\":500,\"subtotal\":\"0.00003\"},"
            + "{\"itemCode\":\"prompt_cached_tokens\",\"quantity\":300,\"subtotal\":\"0.0000045\"}]",
            json);
    }

    [Fact]
    public void TierBreakdownAndVariantCodeSerializeWhenPresent()
    {
        var tiered = new CostItem
        {
            ItemCode = PriceItemCodes.PromptTokens,
            Quantity = 1500,
            Subtotal = 0.00002m,
            TierBreakdown =
            [
                new TierCost { UpTo = 1000, Units = 1000, Subtotal = 0.00001m },
                new TierCost { UpTo = null, Units = 500, Subtotal = 0.00001m },
            ],
        };
        var variant = new CostItem
        {
            ItemCode = PriceItemCodes.PromptWriteCachedTokens,
            PromptWriteCacheVariantCode = PromptWriteCacheVariantCodes.FiveMin,
            Quantity = 50,
            Subtotal = 0.0000015m,
        };

        Assert.Equal(
            "[{\"itemCode\":\"prompt_tokens\",\"quantity\":1500,"
            + "\"tierBreakdown\":[{\"upTo\":1000,\"units\":1000,\"subtotal\":\"0.00001\"},"
            + "{\"units\":500,\"subtotal\":\"0.00001\"}],\"subtotal\":\"0.00002\"},"
            + "{\"itemCode\":\"prompt_write_cached_tokens\",\"promptWriteCacheVariantCode\":\"five_min\","
            + "\"quantity\":50,\"subtotal\":\"0.0000015\"}]",
            BillingJson.SerializeCostItems([tiered, variant]));
    }

    [Fact]
    public void EmptyItemsSerializeAsJsonNull()
    {
        // Go's calculator starts from a nil slice, so an empty result marshals
        // as null rather than [].
        Assert.Equal("null", BillingJson.SerializeCostItems([]));
    }

    [Fact]
    public void GoDecimalFormatTrimsTrailingZeros()
    {
        Assert.Equal("0", GoDecimal.Format(0m));
        Assert.Equal("0.03", GoDecimal.Format(0.0300m));
        Assert.Equal("0.0000555", GoDecimal.Format(0.0000555m));
        Assert.Equal("12", GoDecimal.Format(12.000m));
    }

    private sealed class FakeChannelPricingSource : IChannelPricingSource
    {
        public List<ChannelModelPriceRecord> Prices { get; } = [];

        public Dictionary<string, string?> Values { get; } = new(StringComparer.Ordinal);

        public IReadOnlyList<ChannelModelPriceRecord> ListEnabledChannelPrices(long channelId)
            => Prices.Where(price => price.ChannelId == channelId).ToList();

        public string? GetSystemValue(string key)
            => Values.TryGetValue(key, out var value) ? value : null;
    }
}
