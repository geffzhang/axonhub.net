using AxonHub.Api.Billing;
using AxonHub.Llm;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/biz/cost_calc_test.go: cached/write-cached token
/// exclusion, all-cached edge case, and schedule matching (daily time,
/// cross-midnight, weekdays, date range, priority, timezone).
/// </summary>
public sealed class UsageCostCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private static ModelPriceItem Item(string code, string mode, decimal? perUnit)
        => new()
        {
            ItemCode = code,
            Pricing = new Pricing
            {
                Mode = mode,
                UsagePerUnit = perUnit,
            },
        };

    private static ModelPriceItem PerUnitItem(string code, decimal perUnit)
        => Item(code, PricingModes.UsagePerUnit, perUnit);

    private static Usage UsageWith(long prompt, long completion, PromptTokensDetails? details = null)
        => new()
        {
            PromptTokens = prompt,
            CompletionTokens = completion,
            TotalTokens = prompt + completion,
            PromptTokensDetails = details,
        };

    private static CostItem Find(UsageCostResult result, string itemCode)
        => Assert.Single(result.Items, item => item.ItemCode == itemCode);

    [Fact]
    public void CachedTokensAreExcludedFromPromptTokens()
    {
        var usage = UsageWith(1000, 500, new PromptTokensDetails { CachedTokens = 300 });
        var price = new ModelPrice
        {
            Items =
            [
                PerUnitItem(PriceItemCodes.PromptTokens, 0.03m),
                PerUnitItem(PriceItemCodes.CompletionTokens, 0.06m),
                PerUnitItem(PriceItemCodes.PromptCachedTokens, 0.015m),
            ],
        };

        var result = UsageCostCalculator.Compute(usage, price, Now);

        // 700/1M*0.03 + 300/1M*0.015 + 500/1M*0.06 = 0.0000555
        Assert.Equal(0.0000555m, result.Total);
        Assert.Equal(3, result.Items.Count);

        var input = Find(result, PriceItemCodes.PromptTokens);
        Assert.Equal(700, input.Quantity);
        Assert.Equal(0.000021m, input.Subtotal);

        var cached = Find(result, PriceItemCodes.PromptCachedTokens);
        Assert.Equal(300, cached.Quantity);
        Assert.Equal(0.0000045m, cached.Subtotal);

        var completion = Find(result, PriceItemCodes.CompletionTokens);
        Assert.Equal(500, completion.Quantity);
        Assert.Equal(0.00003m, completion.Subtotal);
    }

    [Fact]
    public void WithoutCachedTokensAllPromptTokensAreBillable()
    {
        var usage = UsageWith(1000, 500);
        var price = new ModelPrice
        {
            Items =
            [
                PerUnitItem(PriceItemCodes.PromptTokens, 0.03m),
                PerUnitItem(PriceItemCodes.CompletionTokens, 0.06m),
            ],
        };

        var result = UsageCostCalculator.Compute(usage, price, Now);

        Assert.Equal(0.00006m, result.Total);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal(1000, Find(result, PriceItemCodes.PromptTokens).Quantity);
    }

    [Fact]
    public void ZeroCachedTokensKeepsAllPromptTokensBillable()
    {
        var usage = UsageWith(1000, 500, new PromptTokensDetails { CachedTokens = 0 });
        var price = new ModelPrice
        {
            Items =
            [
                PerUnitItem(PriceItemCodes.PromptTokens, 0.03m),
                PerUnitItem(PriceItemCodes.CompletionTokens, 0.06m),
            ],
        };

        var result = UsageCostCalculator.Compute(usage, price, Now);

        Assert.Equal(0.00006m, result.Total);
        Assert.Equal(1000, Find(result, PriceItemCodes.PromptTokens).Quantity);
    }

    [Fact]
    public void WriteCachedTokensAreExcludedFromPromptTokens()
    {
        var usage = UsageWith(1000, 500, new PromptTokensDetails { WriteCachedTokens = 200 });
        var price = new ModelPrice
        {
            Items =
            [
                PerUnitItem(PriceItemCodes.PromptTokens, 0.03m),
                PerUnitItem(PriceItemCodes.CompletionTokens, 0.06m),
                PerUnitItem(PriceItemCodes.PromptWriteCachedTokens, 0.0375m),
            ],
        };

        var result = UsageCostCalculator.Compute(usage, price, Now);

        // 800/1M*0.03 + 200/1M*0.0375 + 500/1M*0.06 = 0.0000615
        Assert.Equal(0.0000615m, result.Total);
        Assert.Equal(3, result.Items.Count);

        var input = Find(result, PriceItemCodes.PromptTokens);
        Assert.Equal(800, input.Quantity);
        Assert.Equal(0.000024m, input.Subtotal);

        var writeCached = Find(result, PriceItemCodes.PromptWriteCachedTokens);
        Assert.Equal(200, writeCached.Quantity);
        Assert.Equal(0.0000075m, writeCached.Subtotal);

        Assert.Equal(0.00003m, Find(result, PriceItemCodes.CompletionTokens).Subtotal);
    }

    [Fact]
    public void CachedAndWriteCachedTokensAreBothExcluded()
    {
        var usage = UsageWith(
            1000,
            500,
            new PromptTokensDetails { CachedTokens = 300, WriteCachedTokens = 200 });
        var price = new ModelPrice
        {
            Items =
            [
                PerUnitItem(PriceItemCodes.PromptTokens, 0.03m),
                PerUnitItem(PriceItemCodes.CompletionTokens, 0.06m),
                PerUnitItem(PriceItemCodes.PromptCachedTokens, 0.015m),
                PerUnitItem(PriceItemCodes.PromptWriteCachedTokens, 0.0375m),
            ],
        };

        var result = UsageCostCalculator.Compute(usage, price, Now);

        // 500/1M*0.03 + 300/1M*0.015 + 200/1M*0.0375 + 500/1M*0.06 = 0.000057
        Assert.Equal(0.000057m, result.Total);
        Assert.Equal(4, result.Items.Count);
        Assert.Equal(500, Find(result, PriceItemCodes.PromptTokens).Quantity);
        Assert.Equal(0.0000045m, Find(result, PriceItemCodes.PromptCachedTokens).Subtotal);
        Assert.Equal(0.0000075m, Find(result, PriceItemCodes.PromptWriteCachedTokens).Subtotal);
    }

    [Fact]
    public void AllTokensCachedClampsPromptQuantityToZero()
    {
        var usage = UsageWith(1000, 500, new PromptTokensDetails { CachedTokens = 1000 });
        var price = new ModelPrice
        {
            Items =
            [
                PerUnitItem(PriceItemCodes.PromptTokens, 0.03m),
                PerUnitItem(PriceItemCodes.CompletionTokens, 0.06m),
                PerUnitItem(PriceItemCodes.PromptCachedTokens, 0.015m),
            ],
        };

        var result = UsageCostCalculator.Compute(usage, price, Now);

        // 0 + 1000/1M*0.015 + 500/1M*0.06 = 0.000045
        Assert.Equal(0.000045m, result.Total);

        var input = Find(result, PriceItemCodes.PromptTokens);
        Assert.Equal(0, input.Quantity);
        Assert.Equal(0m, input.Subtotal);
    }

    private static PromptWriteCacheVariant Variant(string code, decimal perUnit)
        => new()
        {
            VariantCode = code,
            Pricing = new Pricing { Mode = PricingModes.UsagePerUnit, UsagePerUnit = perUnit },
        };

    private static ModelPriceItem WriteCachedItem(
        decimal sharedPerUnit,
        params PromptWriteCacheVariant[] variants)
        => new()
        {
            ItemCode = PriceItemCodes.PromptWriteCachedTokens,
            Pricing = new Pricing { Mode = PricingModes.UsagePerUnit, UsagePerUnit = sharedPerUnit },
            PromptWriteCacheVariants = variants.Length == 0 ? null : [.. variants],
        };

    [Fact]
    public void FiveMinWriteCacheVariantReplacesTheSharedItem()
    {
        // internal/server/biz/usage_cost_test.go TestUsageCost_CacheVariant5Min:
        // prompt 100 (write-cached 50), 5m variant 50.
        var usage = UsageWith(
            100,
            0,
            new PromptTokensDetails { WriteCachedTokens = 50, WriteCached5MinTokens = 50 });
        var price = new ModelPrice
        {
            Items =
            [
                PerUnitItem(PriceItemCodes.PromptTokens, 0.01m),
                WriteCachedItem(0.04m, Variant(PromptWriteCacheVariantCodes.FiveMin, 0.03m)),
            ],
        };

        var result = UsageCostCalculator.Compute(usage, price, Now);

        // 50/1M*0.01 + 50/1M*0.03 = 0.000002
        Assert.Equal(0.000002m, result.Total);
        Assert.Equal(2, result.Items.Count);

        var input = Find(result, PriceItemCodes.PromptTokens);
        Assert.Equal(50, input.Quantity);
        Assert.Equal(0.0000005m, input.Subtotal);

        var writeCached = Find(result, PriceItemCodes.PromptWriteCachedTokens);
        Assert.Equal(PromptWriteCacheVariantCodes.FiveMin, writeCached.PromptWriteCacheVariantCode);
        Assert.Equal(50, writeCached.Quantity);
        Assert.Equal(0.0000015m, writeCached.Subtotal);
    }

    [Fact]
    public void OneHourWriteCacheVariantReplacesTheSharedItem()
    {
        // TestUsageCost_CacheVariant1Hour: prompt 100 (write-cached 80), 1h variant 80.
        var usage = UsageWith(
            100,
            0,
            new PromptTokensDetails { WriteCachedTokens = 80, WriteCached1HourTokens = 80 });
        var price = new ModelPrice
        {
            Items =
            [
                PerUnitItem(PriceItemCodes.PromptTokens, 0.01m),
                WriteCachedItem(0.04m, Variant(PromptWriteCacheVariantCodes.OneHour, 0.02m)),
            ],
        };

        var result = UsageCostCalculator.Compute(usage, price, Now);

        // 20/1M*0.01 + 80/1M*0.02 = 0.0000018
        Assert.Equal(0.0000018m, result.Total);
        Assert.Equal(2, result.Items.Count);

        var writeCached = Find(result, PriceItemCodes.PromptWriteCachedTokens);
        Assert.Equal(PromptWriteCacheVariantCodes.OneHour, writeCached.PromptWriteCacheVariantCode);
        Assert.Equal(80, writeCached.Quantity);
        Assert.Equal(0.0000016m, writeCached.Subtotal);
    }

    [Fact]
    public void BothWriteCacheVariantsProduceOneItemEach()
    {
        // TestUsageCost_CacheVariantBoth5MinAnd1Hour: 40 five-minute and 60
        // one-hour write-cached tokens; the shared write-cache item disappears.
        var usage = UsageWith(
            100,
            0,
            new PromptTokensDetails
            {
                WriteCachedTokens = 100,
                WriteCached5MinTokens = 40,
                WriteCached1HourTokens = 60,
            });
        var price = new ModelPrice
        {
            Items =
            [
                PerUnitItem(PriceItemCodes.PromptTokens, 0.01m),
                WriteCachedItem(
                    0.06m,
                    Variant(PromptWriteCacheVariantCodes.FiveMin, 0.05m),
                    Variant(PromptWriteCacheVariantCodes.OneHour, 0.03m)),
            ],
        };

        var result = UsageCostCalculator.Compute(usage, price, Now);

        // 40/1M*0.05 + 60/1M*0.03 = 0.0000038
        Assert.Equal(0.0000038m, result.Total);
        Assert.Equal(3, result.Items.Count);

        var input = Find(result, PriceItemCodes.PromptTokens);
        Assert.Equal(0, input.Quantity);
        Assert.Equal(0m, input.Subtotal);

        var items = result.Items
            .Where(item => item.ItemCode == PriceItemCodes.PromptWriteCachedTokens)
            .ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(PromptWriteCacheVariantCodes.FiveMin, items[0].PromptWriteCacheVariantCode);
        Assert.Equal(40, items[0].Quantity);
        Assert.Equal(0.000002m, items[0].Subtotal);
        Assert.Equal(PromptWriteCacheVariantCodes.OneHour, items[1].PromptWriteCacheVariantCode);
        Assert.Equal(60, items[1].Quantity);
        Assert.Equal(0.0000018m, items[1].Subtotal);
    }

    [Fact]
    public void SharedWriteCacheRateAppliesWhenNoVariantIsConfigured()
    {
        // TestUsageCost_CacheVariantFallbackToShared: no variant pricing, so the
        // item-level rate applies and the item carries no variant code.
        var usage = UsageWith(100, 0, new PromptTokensDetails { WriteCachedTokens = 70 });
        var price = new ModelPrice
        {
            Items =
            [
                PerUnitItem(PriceItemCodes.PromptTokens, 0.01m),
                WriteCachedItem(0.04m),
            ],
        };

        var result = UsageCostCalculator.Compute(usage, price, Now);

        // 30/1M*0.01 + 70/1M*0.04 = 0.0000031
        Assert.Equal(0.0000031m, result.Total);
        Assert.Equal(2, result.Items.Count);

        var writeCached = Find(result, PriceItemCodes.PromptWriteCachedTokens);
        Assert.Null(writeCached.PromptWriteCacheVariantCode);
        Assert.Equal(70, writeCached.Quantity);
        Assert.Equal(0.0000028m, writeCached.Subtotal);
    }

    private static ModelPrice SchedulePrice(PriceSchedule schedule)
        => new()
        {
            Items =
            [
                PerUnitItem(PriceItemCodes.PromptTokens, 0.03m),
                PerUnitItem(PriceItemCodes.CompletionTokens, 0.06m),
            ],
            Schedule = schedule,
        };

    private static PriceOverride NightOverride(string start, string end)
        => new()
        {
            Name = "Night Discount",
            Priority = 1,
            When = new OverrideWhen { DailyTime = new DailyTimeRange { Start = start, End = end } },
            Items =
            [
                PerUnitItem(PriceItemCodes.PromptTokens, 0.01m),
                PerUnitItem(PriceItemCodes.CompletionTokens, 0.02m),
            ],
        };

    [Fact]
    public void ScheduleOverrideMatchesUsesOverridePrices()
    {
        var price = SchedulePrice(new PriceSchedule
        {
            Timezone = "UTC",
            Overrides = [NightOverride("00:00", "08:00")],
        });

        var result = UsageCostCalculator.Compute(UsageWith(1000, 500), price, new DateTimeOffset(2026, 7, 21, 3, 0, 0, TimeSpan.Zero));

        Assert.Equal(0.00002m, result.Total);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public void ScheduleOverrideNoMatchUsesDefaultPrices()
    {
        var price = SchedulePrice(new PriceSchedule
        {
            Timezone = "UTC",
            Overrides = [NightOverride("00:00", "08:00")],
        });

        var result = UsageCostCalculator.Compute(UsageWith(1000, 500), price, new DateTimeOffset(2026, 7, 21, 14, 0, 0, TimeSpan.Zero));

        Assert.Equal(0.00006m, result.Total);
        Assert.Equal(2, result.Items.Count);
    }

    [Fact]
    public void ScheduleCrossMidnightRangeMatchesBothSides()
    {
        var price = SchedulePrice(new PriceSchedule
        {
            Timezone = "UTC",
            Overrides = [NightOverride("22:00", "06:00")],
        });
        var usage = UsageWith(1000, 500);

        Assert.Equal(
            0.00002m,
            UsageCostCalculator.Compute(usage, price, new DateTimeOffset(2026, 7, 21, 23, 0, 0, TimeSpan.Zero)).Total);
        Assert.Equal(
            0.00002m,
            UsageCostCalculator.Compute(usage, price, new DateTimeOffset(2026, 7, 22, 3, 0, 0, TimeSpan.Zero)).Total);
        Assert.Equal(
            0.00006m,
            UsageCostCalculator.Compute(usage, price, new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero)).Total);
    }

    [Fact]
    public void ScheduleWeekdaysMatchSaturdayAndSunday()
    {
        var price = SchedulePrice(new PriceSchedule
        {
            Timezone = "UTC",
            Overrides =
            [
                new PriceOverride
                {
                    Name = "Weekend Discount",
                    Priority = 1,
                    When = new OverrideWhen { Weekdays = [6, 7] },
                    Items =
                    [
                        PerUnitItem(PriceItemCodes.PromptTokens, 0.01m),
                        PerUnitItem(PriceItemCodes.CompletionTokens, 0.02m),
                    ],
                },
            ],
        });
        var usage = UsageWith(1000, 500);

        // 2026-07-25 is Saturday, 2026-07-26 is Sunday, 2026-07-21 is Tuesday.
        Assert.Equal(
            0.00002m,
            UsageCostCalculator.Compute(usage, price, new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero)).Total);
        Assert.Equal(
            0.00002m,
            UsageCostCalculator.Compute(usage, price, new DateTimeOffset(2026, 7, 26, 12, 0, 0, TimeSpan.Zero)).Total);
        Assert.Equal(
            0.00006m,
            UsageCostCalculator.Compute(usage, price, new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero)).Total);
    }

    [Fact]
    public void ScheduleDateRangeIsInclusive()
    {
        var price = SchedulePrice(new PriceSchedule
        {
            Timezone = "UTC",
            Overrides =
            [
                new PriceOverride
                {
                    Name = "Summer Promotion",
                    Priority = 1,
                    When = new OverrideWhen
                    {
                        DateRange = new DateRange { Start = "2026-07-01", End = "2026-07-31" },
                    },
                    Items =
                    [
                        PerUnitItem(PriceItemCodes.PromptTokens, 0.01m),
                        PerUnitItem(PriceItemCodes.CompletionTokens, 0.02m),
                    ],
                },
            ],
        });
        var usage = UsageWith(1000, 500);

        Assert.Equal(
            0.00002m,
            UsageCostCalculator.Compute(usage, price, new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)).Total);
        Assert.Equal(
            0.00006m,
            UsageCostCalculator.Compute(usage, price, new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero)).Total);
    }

    [Fact]
    public void ScheduleLowerPriorityNumberWins()
    {
        var price = SchedulePrice(new PriceSchedule
        {
            Timezone = "UTC",
            Overrides =
            [
                new PriceOverride
                {
                    Name = "Low Priority",
                    Priority = 10,
                    When = new OverrideWhen { DailyTime = new DailyTimeRange { Start = "00:00", End = "23:59" } },
                    Items =
                    [
                        PerUnitItem(PriceItemCodes.PromptTokens, 0.02m),
                        PerUnitItem(PriceItemCodes.CompletionTokens, 0.04m),
                    ],
                },
                new PriceOverride
                {
                    Name = "High Priority",
                    Priority = 1,
                    When = new OverrideWhen { DailyTime = new DailyTimeRange { Start = "10:00", End = "14:00" } },
                    Items =
                    [
                        PerUnitItem(PriceItemCodes.PromptTokens, 0.01m),
                        PerUnitItem(PriceItemCodes.CompletionTokens, 0.02m),
                    ],
                },
            ],
        });
        var usage = UsageWith(1000, 500);

        Assert.Equal(
            0.00002m,
            UsageCostCalculator.Compute(usage, price, new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero)).Total);
        Assert.Equal(
            0.00004m,
            UsageCostCalculator.Compute(usage, price, new DateTimeOffset(2026, 7, 21, 16, 0, 0, TimeSpan.Zero)).Total);
    }

    [Fact]
    public void ScheduleTimeZoneShiftsTheLocalClock()
    {
        var price = SchedulePrice(new PriceSchedule
        {
            Timezone = "Asia/Shanghai",
            Overrides = [NightOverride("00:00", "08:00")],
        });
        var usage = UsageWith(1000, 500);

        // 22:00 UTC is 06:00 in Shanghai; 10:00 UTC is 18:00.
        Assert.Equal(
            0.00002m,
            UsageCostCalculator.Compute(usage, price, new DateTimeOffset(2026, 7, 21, 22, 0, 0, TimeSpan.Zero)).Total);
        Assert.Equal(
            0.00006m,
            UsageCostCalculator.Compute(usage, price, new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero)).Total);
    }

    [Fact]
    public void UnknownTimeZoneFallsBackToUtc()
    {
        var price = SchedulePrice(new PriceSchedule
        {
            Timezone = "Not/AZone",
            Overrides = [NightOverride("00:00", "08:00")],
        });

        Assert.Equal(
            0.00002m,
            UsageCostCalculator.Compute(UsageWith(1000, 500), price, new DateTimeOffset(2026, 7, 21, 3, 0, 0, TimeSpan.Zero)).Total);
    }

    [Fact]
    public void ItemWithoutMatchingConditionsMatchesEveryTime()
    {
        var price = SchedulePrice(new PriceSchedule
        {
            Timezone = "UTC",
            Overrides =
            [
                new PriceOverride
                {
                    Name = "Always",
                    Priority = 1,
                    // No "when" conditions: the reference's zero OverrideWhen
                    // matches every instant.
                    Items =
                    [
                        PerUnitItem(PriceItemCodes.PromptTokens, 0.01m),
                        PerUnitItem(PriceItemCodes.CompletionTokens, 0.02m),
                    ],
                },
            ],
        });

        Assert.Equal(
            0.00002m,
            UsageCostCalculator.Compute(UsageWith(1000, 500), price, Now).Total);
    }

    [Fact]
    public void FlatFeeIsChargedOncePerItem()
    {
        var price = new ModelPrice
        {
            Items =
            [
                new ModelPriceItem
                {
                    ItemCode = PriceItemCodes.PromptTokens,
                    Pricing = new Pricing { Mode = PricingModes.FlatFee, FlatFee = 0.5m },
                },
            ],
        };

        var result = UsageCostCalculator.Compute(UsageWith(1000, 500), price, Now);

        Assert.Equal(0.5m, result.Total);
        var item = Assert.Single(result.Items);
        Assert.Equal(1000, item.Quantity);
        Assert.Equal(0.5m, item.Subtotal);
        Assert.Null(item.TierBreakdown);
    }

    [Fact]
    public void TieredBillsEachSegmentSeparately()
    {
        var price = new ModelPrice
        {
            Items =
            [
                new ModelPriceItem
                {
                    ItemCode = PriceItemCodes.PromptTokens,
                    Pricing = new Pricing
                    {
                        Mode = PricingModes.Tiered,
                        UsageTiered = new TieredPricing
                        {
                            Tiers =
                            [
                                new PriceTier { UpTo = 1000, PricePerUnit = 0.01m },
                                new PriceTier { UpTo = null, PricePerUnit = 0.02m },
                            ],
                        },
                    },
                },
            ],
        };

        var result = UsageCostCalculator.Compute(UsageWith(1500, 0), price, Now);

        // 1000/1M*0.01 + 500/1M*0.02 = 0.00002
        Assert.Equal(0.00002m, result.Total);
        var item = Assert.Single(result.Items);
        Assert.Equal(1500, item.Quantity);
        Assert.NotNull(item.TierBreakdown);
        Assert.Collection(
            item.TierBreakdown!,
            first =>
            {
                Assert.Equal(1000, first.UpTo);
                Assert.Equal(1000, first.Units);
                Assert.Equal(0.00001m, first.Subtotal);
            },
            second =>
            {
                Assert.Null(second.UpTo);
                Assert.Equal(500, second.Units);
                Assert.Equal(0.00001m, second.Subtotal);
            });
    }

    [Fact]
    public void VolumeBillsAllUnitsAtTheMatchedTierRate()
    {
        var tiers = new TieredPricing
        {
            Tiers =
            [
                new PriceTier { UpTo = 1000, PricePerUnit = 0.01m },
                new PriceTier { UpTo = null, PricePerUnit = 0.02m },
            ],
        };

        var price = new ModelPrice
        {
            Items =
            [
                new ModelPriceItem
                {
                    ItemCode = PriceItemCodes.PromptTokens,
                    Pricing = new Pricing { Mode = PricingModes.Volume, UsageTiered = tiers },
                },
            ],
        };

        var over = UsageCostCalculator.Compute(UsageWith(1500, 0), price, Now);
        Assert.Equal(0.00003m, over.Total);
        var overItem = Assert.Single(over.Items);
        var overBreakdown = Assert.Single(overItem.TierBreakdown!);
        Assert.Equal(1500, overBreakdown.Units);
        Assert.Equal(0.00003m, overBreakdown.Subtotal);

        var under = UsageCostCalculator.Compute(UsageWith(800, 0), price, Now);
        Assert.Equal(0.000008m, under.Total);
        var underItem = Assert.Single(under.Items);
        var underBreakdown = Assert.Single(underItem.TierBreakdown!);
        Assert.Equal(800, underBreakdown.Units);
    }

    [Fact]
    public void UnknownPricingModeProducesZeroSubtotal()
    {
        var price = new ModelPrice
        {
            Items =
            [
                new ModelPriceItem
                {
                    ItemCode = PriceItemCodes.PromptTokens,
                    Pricing = new Pricing { Mode = "made_up" },
                },
            ],
        };

        var result = UsageCostCalculator.Compute(UsageWith(1000, 500), price, Now);

        Assert.Equal(0m, result.Total);
        var item = Assert.Single(result.Items);
        Assert.Equal(0m, item.Subtotal);
        Assert.Null(item.TierBreakdown);
    }

    [Fact]
    public void MissingPricingPayloadProducesZeroSubtotal()
    {
        var price = new ModelPrice
        {
            Items =
            [
                new ModelPriceItem
                {
                    ItemCode = PriceItemCodes.PromptTokens,
                    Pricing = new Pricing { Mode = PricingModes.UsagePerUnit },
                },
            ],
        };

        var result = UsageCostCalculator.Compute(UsageWith(1000, 500), price, Now);

        Assert.Equal(0m, result.Total);
        var item = Assert.Single(result.Items);
        Assert.Equal(0m, item.Subtotal);
    }

    [Fact]
    public void NoItemsProducesEmptyResult()
    {
        var result = UsageCostCalculator.Compute(UsageWith(1000, 500), new ModelPrice(), Now);

        Assert.Empty(result.Items);
        Assert.Equal(0m, result.Total);
    }

    [Fact]
    public void UnknownItemCodeBillsZeroQuantity()
    {
        var price = new ModelPrice
        {
            Items = [PerUnitItem("mystery_tokens", 0.03m)],
        };

        var result = UsageCostCalculator.Compute(UsageWith(1000, 500), price, Now);

        Assert.Equal(0m, result.Total);
        var item = Assert.Single(result.Items);
        Assert.Equal(0, item.Quantity);
    }
}
