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
    public void FallsBackToTheModelLibraryPriceWithoutAChannelPrice()
    {
        var source = new FakeChannelPricingSource();
        source.ModelCards["m-fallback"] = """{"cost":{"input":1,"output":2}}""";

        var cost = NewService(source).Compute(1, "m-fallback", UsageTokens(100, 200));

        // (100/1e6)*1 + (200/1e6)*2 = 0.0005.
        Assert.NotNull(cost);
        Assert.Equal(0.0005m, cost.Total);
        Assert.Equal(2, cost.Items.Count);
        Assert.Equal("model_library:m-fallback", cost.ReferenceId);
    }

    [Fact]
    public void ChannelPriceWinsOverTheModelLibraryPrice()
    {
        var source = new FakeChannelPricingSource();
        source.Prices.Add(PriceRow(
            1,
            "m-precedence",
            """
            {"items":[{"itemCode":"prompt_tokens","pricing":{"mode":"usage_per_unit","usagePerUnit":"1"}}]}
            """,
            "channel-ref"));
        source.ModelCards["m-precedence"] = """{"cost":{"input":100}}""";

        var cost = NewService(source).Compute(1, "m-precedence", UsageTokens(100, 0));

        Assert.NotNull(cost);
        Assert.Equal(0.0001m, cost.Total);
        Assert.Equal("channel-ref", cost.ReferenceId);
        Assert.Equal(0, source.ModelCardLookups);
    }

    [Fact]
    public void FallbackSkipsUnpriceableLibraryEntries()
    {
        var source = new FakeChannelPricingSource();
        source.ModelCards["m-no-cost"] = "{}";
        source.ModelCards["m-null-card"] = "null";

        foreach (var modelId in new[] { "m-no-cost", "m-null-card", "m-missing" })
        {
            Assert.Null(NewService(source).Compute(1, modelId, UsageTokens(100, 0)));
        }
    }

    [Fact]
    public void FallbackStripsEffortSuffixAndKeepsTheRequestedIdInTheReference()
    {
        var source = new FakeChannelPricingSource();
        source.ModelCards["m-suffix"] = """{"cost":{"input":1,"output":2}}""";

        var cost = NewService(source).Compute(1, "m-suffix-high", UsageTokens(100, 200));

        Assert.NotNull(cost);
        Assert.Equal(0.0005m, cost.Total);
        Assert.Equal("model_library:m-suffix-high", cost.ReferenceId);
        Assert.Equal(["m-suffix-high", "m-suffix"], source.LookedUpModelIds);
    }

    [Fact]
    public void FallbackPrefersAnExactLibraryEntryBeforeTheBaseModel()
    {
        var source = new FakeChannelPricingSource();
        source.ModelCards["m-exact"] = """{"cost":{"input":10}}""";
        source.ModelCards["m-exact-high"] = """{"cost":{"input":1}}""";

        var cost = NewService(source).Compute(1, "m-exact-high", UsageTokens(100, 0));

        // (100 / 1e6) * 1 = 0.0001; the base price would yield 0.001.
        Assert.NotNull(cost);
        Assert.Equal(0.0001m, cost.Total);
        Assert.Equal(["m-exact-high"], source.LookedUpModelIds);
    }

    [Theory]
    [InlineData("m", new[] { "m" })]
    [InlineData("m-high", new[] { "m-high", "m" })]
    [InlineData("m-medium", new[] { "m-medium", "m" })]
    [InlineData("m-low", new[] { "m-low", "m" })]
    [InlineData("m-high-", new[] { "m-high-" })]
    public void ModelLibraryPriceCandidatesStripOnlyCompleteSuffixes(
        string modelId,
        string[] expected)
    {
        Assert.Equal(expected, ModelLibraryPrice.Candidates(modelId));
    }

    [Fact]
    public void ModelLibraryLookupsAreMemoizedPerServiceInstance()
    {
        var source = new FakeChannelPricingSource();
        source.ModelCards["m-hit"] = """{"cost":{"input":1}}""";
        var service = NewService(source);

        var first = service.Compute(1, "m-hit", UsageTokens(100, 0));
        Assert.Equal(0.0001m, first!.Total);
        Assert.Equal(1, source.ModelCardLookups);

        // A price change within the same request reuses the memoized result.
        source.ModelCards["m-hit"] = """{"cost":{"input":9}}""";
        var second = service.Compute(1, "m-hit", UsageTokens(100, 0));
        Assert.Equal(0.0001m, second!.Total);
        Assert.Equal(1, source.ModelCardLookups);

        // A fresh request (new scoped instance) observes the new price.
        var fresh = NewService(source).Compute(1, "m-hit", UsageTokens(100, 0));
        Assert.Equal(0.0009m, fresh!.Total);
        Assert.Equal(2, source.ModelCardLookups);
    }

    [Fact]
    public void MissedModelLibraryLookupsAreMemoizedWithinTheRequest()
    {
        var source = new FakeChannelPricingSource();
        var service = NewService(source);

        Assert.Null(service.Compute(1, "m-absent", UsageTokens(100, 0)));
        Assert.Equal(1, source.ModelCardLookups);

        source.ModelCards["m-absent"] = """{"cost":{"input":1}}""";
        Assert.Null(service.Compute(1, "m-absent", UsageTokens(100, 0)));
        Assert.Equal(1, source.ModelCardLookups);

        var fresh = NewService(source).Compute(1, "m-absent", UsageTokens(100, 0));
        Assert.Equal(0.0001m, fresh!.Total);
        Assert.Equal(2, source.ModelCardLookups);
    }

    [Fact]
    public void FailedModelLibraryLookupsAreNotMemoized()
    {
        var source = new FakeChannelPricingSource();
        source.FailingModelCards.Add("m-error");
        source.ModelCards["m-error"] = """{"cost":{"input":1}}""";
        var service = NewService(source);

        Assert.Null(service.Compute(1, "m-error", UsageTokens(100, 0)));
        Assert.Equal(1, source.ModelCardLookups);

        // The usage-log write runs later with a healthy lookup; the failed
        // attempt must not pin a cached miss.
        source.FailingModelCards.Clear();
        var retried = service.Compute(1, "m-error", UsageTokens(100, 0));
        Assert.Equal(0.0001m, retried!.Total);
        Assert.Equal(2, source.ModelCardLookups);
    }

    [Fact]
    public void DisabledChannelsDoNotUseTheModelLibraryFallback()
    {
        var source = new FakeChannelPricingSource { ChannelEnabled = false };
        source.ModelCards["m-disabled"] = """{"cost":{"input":1}}""";

        Assert.Null(NewService(source).Compute(1, "m-disabled", UsageTokens(100, 0)));
        Assert.Equal(0, source.ModelCardLookups);
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
        source.ModelCards["good-model"] = """{"cost":{"input":1}}""";

        // preloadModelPrices drops the entire channel cache when one row fails
        // to decode, so even the good model has no price; the channel is not
        // treated as "no explicit price" and the library fallback stays inert.
        Assert.Null(NewService(source).Compute(1, "good-model", UsageTokens(1, 1)));
        Assert.Equal(0, source.ModelCardLookups);
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

        public Dictionary<string, string> ModelCards { get; } = new(StringComparer.Ordinal);

        public HashSet<string> FailingModelCards { get; } = new(StringComparer.Ordinal);

        public List<string> LookedUpModelIds { get; } = [];

        public bool ChannelEnabled { get; set; } = true;

        public int ModelCardLookups => LookedUpModelIds.Count;

        public IReadOnlyList<ChannelModelPriceRecord> ListEnabledChannelPrices(long channelId)
            => Prices.Where(price => price.ChannelId == channelId).ToList();

        public bool IsEnabledChannel(long channelId) => ChannelEnabled;

        public ModelLibraryModelCardLookup FindModelLibraryModelCard(string modelId)
        {
            LookedUpModelIds.Add(modelId);

            if (FailingModelCards.Contains(modelId))
            {
                return ModelLibraryModelCardLookup.Failure;
            }

            return ModelCards.TryGetValue(modelId, out var modelCard)
                ? ModelLibraryModelCardLookup.Hit(modelCard)
                : ModelLibraryModelCardLookup.Miss;
        }

        public string? GetSystemValue(string key)
            => Values.TryGetValue(key, out var value) ? value : null;
    }
}
