using AxonHub.Api.Billing;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks objects.ModelPrice.Validate and the Equals family to the reference
/// messages and comparison rules (objects/price.go:48-664). The save mutation
/// relies on both: validation decides the GraphQL error text, equality decides
/// skip versus update and therefore whether a row gets a new reference id.
/// </summary>
public sealed class ModelPriceValidationTests
{
    // ---------------------------------------------------------------------
    // Validate
    // ---------------------------------------------------------------------

    [Fact]
    public void ValidateRejectsANullModelPrice()
    {
        AssertMessage("modelPrice is nil", () => ModelPriceValidation.ValidateModelPrice(null));
    }

    [Fact]
    public void ValidateAcceptsAPriceWithNoItems()
    {
        ModelPriceValidation.ValidateModelPrice(new ModelPrice());
        ModelPriceValidation.ValidateModelPrice(new ModelPrice { Items = [] });
        ModelPriceValidation.ValidateModelPrice(new ModelPrice { Items = null });
    }

    [Fact]
    public void ValidateRejectsANullItem()
    {
        AssertMessage(
            "items[0]: modelPriceItem is nil",
            () => ModelPriceValidation.ValidateModelPrice(
                new ModelPrice { Items = [null!] }));
    }

    [Fact]
    public void ValidateRejectsANullPricing()
    {
        AssertMessage(
            "items[0]: pricing: pricing is nil",
            () => ModelPriceValidation.ValidateModelPrice(
                new ModelPrice
                {
                    Items = [new ModelPriceItem { ItemCode = PriceItemCodes.PromptTokens, Pricing = null! }],
                }));
    }

    [Fact]
    public void ValidateRequiresFlatFee()
    {
        AssertMessage(
            "items[0]: pricing: flatFee is required",
            () => ModelPriceValidation.ValidateModelPrice(
                Price(new Pricing { Mode = PricingModes.FlatFee })));
    }

    [Fact]
    public void ValidateRequiresUsagePerUnit()
    {
        AssertMessage(
            "items[0]: pricing: usagePerUnit is required",
            () => ModelPriceValidation.ValidateModelPrice(
                Price(new Pricing { Mode = PricingModes.UsagePerUnit })));
    }

    [Theory]
    [InlineData(PricingModes.Tiered)]
    [InlineData(PricingModes.Volume)]
    public void ValidateRequiresUsageTieredForTieredAndVolumeModes(string mode)
    {
        AssertMessage(
            "items[0]: pricing: usageTiered is required",
            () => ModelPriceValidation.ValidateModelPrice(
                Price(new Pricing { Mode = mode })));
    }

    [Fact]
    public void ValidateRequiresAtLeastOneTier()
    {
        AssertMessage(
            "items[0]: pricing: tiers is required",
            () => ModelPriceValidation.ValidateModelPrice(
                Price(Tiered([]))));
    }

    [Fact]
    public void ValidateRequiresUpToOnEveryTierExceptTheLast()
    {
        AssertMessage(
            "items[0]: pricing: tiers[0].upTo is required",
            () => ModelPriceValidation.ValidateModelPrice(
                Price(Tiered([
                    new PriceTier { UpTo = null, PricePerUnit = 1m },
                    new PriceTier { UpTo = null, PricePerUnit = 2m },
                ]))));
    }

    [Fact]
    public void ValidateRejectsUpToOnTheLastTier()
    {
        AssertMessage(
            "items[0]: pricing: tiers[1].upTo must be null",
            () => ModelPriceValidation.ValidateModelPrice(
                Price(Tiered([
                    new PriceTier { UpTo = 1000, PricePerUnit = 1m },
                    new PriceTier { UpTo = 2000, PricePerUnit = 2m },
                ]))));
    }

    [Fact]
    public void ValidateRejectsUnknownPricingModes()
    {
        AssertMessage(
            "items[0]: pricing: unknown pricing mode: mystery",
            () => ModelPriceValidation.ValidateModelPrice(
                Price(new Pricing { Mode = "mystery", FlatFee = 1m })));
    }

    [Fact]
    public void ValidateWrapsVariantIndexAndPricing()
    {
        AssertMessage(
            "items[0]: promptWriteCacheVariants[0]: pricing: flatFee is required",
            () => ModelPriceValidation.ValidateModelPrice(
                new ModelPrice
                {
                    Items =
                    [
                        new ModelPriceItem
                        {
                            ItemCode = PriceItemCodes.PromptWriteCachedTokens,
                            Pricing = new Pricing
                            {
                                Mode = PricingModes.UsagePerUnit,
                                UsagePerUnit = 0.1m,
                            },
                            PromptWriteCacheVariants =
                            [
                                new PromptWriteCacheVariant
                                {
                                    VariantCode = PromptWriteCacheVariantCodes.FiveMin,
                                    Pricing = new Pricing { Mode = PricingModes.FlatFee },
                                },
                            ],
                        },
                    ],
                }));
    }

    [Fact]
    public void ValidateRejectsANullVariant()
    {
        AssertMessage(
            "items[0]: promptWriteCacheVariants[0]: promptWriteCacheVariant is nil",
            () => ModelPriceValidation.ValidateModelPrice(
                new ModelPrice
                {
                    Items =
                    [
                        new ModelPriceItem
                        {
                            ItemCode = PriceItemCodes.PromptWriteCachedTokens,
                            Pricing = new Pricing
                            {
                                Mode = PricingModes.UsagePerUnit,
                                UsagePerUnit = 0.1m,
                            },
                            PromptWriteCacheVariants = [null!],
                        },
                    ],
                }));
    }

    [Fact]
    public void ValidateRequiresScheduleTimezoneAndOverrides()
    {
        AssertMessage(
            "schedule: schedule timezone is required",
            () => ModelPriceValidation.ValidateModelPrice(
                WithSchedule(new PriceSchedule { Timezone = "", Overrides = [] })));

        AssertMessage(
            "schedule: schedule overrides is required",
            () => ModelPriceValidation.ValidateModelPrice(
                WithSchedule(new PriceSchedule { Timezone = "UTC", Overrides = [] })));
    }

    [Fact]
    public void ValidateRejectsUnknownTimezones()
    {
        AssertMessage(
            "schedule: invalid timezone: Mars/Olympus",
            () => ModelPriceValidation.ValidateModelPrice(
                WithSchedule(Schedule("Mars/Olympus", Override()))));
    }

    [Fact]
    public void ValidateAcceptsTheGoLocalTimezone()
    {
        // time.LoadLocation("Local") always resolves; the port keeps the same
        // special case because .NET has no such zone id.
        ModelPriceValidation.ValidateModelPrice(
            WithSchedule(Schedule("Local", Override())));
    }

    [Fact]
    public void ValidateRequiresWhenAndACondition()
    {
        AssertMessage(
            "schedule: overrides[0]: when: when is required",
            () => ModelPriceValidation.ValidateModelPrice(
                WithSchedule(Schedule(
                    "UTC",
                    new PriceOverride
                    {
                        Name = "override",
                        When = null!,
                        Items = [],
                    }))));

        AssertMessage(
            "schedule: overrides[0]: when: at least one condition (dailyTime, weekdays, or dateRange) is required",
            () => ModelPriceValidation.ValidateModelPrice(
                WithSchedule(Schedule("UTC", Override(when: new OverrideWhen())))));
    }

    [Fact]
    public void ValidateRequiresOverrideItems()
    {
        AssertMessage(
            "schedule: overrides[0]: items is required",
            () => ModelPriceValidation.ValidateModelPrice(
                WithSchedule(Schedule(
                    "UTC",
                    Override(
                        when: new OverrideWhen { Weekdays = [1] },
                        items: [])))));
    }

    [Fact]
    public void ValidateNestsOverrideItemErrors()
    {
        AssertMessage(
            "schedule: overrides[0]: items[0]: pricing: flatFee is required",
            () => ModelPriceValidation.ValidateModelPrice(
                WithSchedule(Schedule(
                    "UTC",
                    Override(
                        when: new OverrideWhen { Weekdays = [1] },
                        items:
                        [
                            new ModelPriceItem
                            {
                                ItemCode = PriceItemCodes.CompletionTokens,
                                Pricing = new Pricing { Mode = PricingModes.FlatFee },
                            },
                        ])))));
    }

    [Theory]
    [InlineData("9:00", "start: invalid time format \"9:00\", expected \"HH:mm\"")]
    [InlineData("a\"b", "start: invalid time format \"a\\\"b\", expected \"HH:mm\"")]
    [InlineData("aa:00", "start: hour must be between 0 and 23, got 539")]
    [InlineData("24:00", "start: hour must be between 0 and 23, got 24")]
    [InlineData("00:99", "start: minute must be between 0 and 59, got 99")]
    public void ValidateDailyTimeKeepsTheReferenceByteArithmetic(string start, string expected)
    {
        // validateDailyTime only checks the length and the colon; the hour and
        // minute come from byte subtraction without a digit check, so "aa:00"
        // is an hour-out-of-range error, not a format error. %q quoting is
        // kept for the format error.
        AssertMessage(
            "schedule: overrides[0]: when: dailyTime: " + expected,
            () => ModelPriceValidation.ValidateModelPrice(
                WithSchedule(Schedule(
                    "UTC",
                    Override(when: new OverrideWhen
                    {
                        DailyTime = new DailyTimeRange { Start = start, End = "08:00" },
                    })))));
    }

    [Fact]
    public void ValidateChecksTheEndTimeOfTheDailyRange()
    {
        AssertMessage(
            "schedule: overrides[0]: when: dailyTime: end: hour must be between 0 and 23, got 25",
            () => ModelPriceValidation.ValidateModelPrice(
                WithSchedule(Schedule(
                    "UTC",
                    Override(when: new OverrideWhen
                    {
                        DailyTime = new DailyTimeRange { Start = "00:00", End = "25:00" },
                    })))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void ValidateRejectsWeekdaysOutsideOneToSeven(int weekday)
    {
        AssertMessage(
            "schedule: overrides[0]: when: weekdays[0] must be between 1 and 7, got " + weekday,
            () => ModelPriceValidation.ValidateModelPrice(
                WithSchedule(Schedule(
                    "UTC",
                    Override(when: new OverrideWhen { Weekdays = [weekday] })))));
    }

    [Theory]
    [InlineData("", "08:00", "start is required")]
    [InlineData("2026-01-01", "", "end is required")]
    [InlineData("2026/01/01", "2026-12-31", "invalid start date format: 2026/01/01")]
    [InlineData("2026-01-01", "2026.12.31", "invalid end date format: 2026.12.31")]
    [InlineData("2026-02-30", "2026-12-31", "invalid start date format: 2026-02-30")]
    [InlineData("2026-12-31", "2026-01-01", "end date must be after or equal to start date")]
    public void ValidateDateRangesLikeTimeParse(string start, string end, string expected)
    {
        AssertMessage(
            "schedule: overrides[0]: when: dateRange: " + expected,
            () => ModelPriceValidation.ValidateModelPrice(
                WithSchedule(Schedule(
                    "UTC",
                    Override(when: new OverrideWhen
                    {
                        DateRange = new DateRange { Start = start, End = end },
                    })))));
    }

    [Fact]
    public void ValidateAcceptsASingleDayDateRange()
    {
        ModelPriceValidation.ValidateModelPrice(
            WithSchedule(Schedule(
                "UTC",
                Override(when: new OverrideWhen
                {
                    DateRange = new DateRange { Start = "2026-01-01", End = "2026-01-01" },
                }))));
    }

    [Fact]
    public void ValidateAcceptsAFullyPopulatedPrice()
    {
        ModelPriceValidation.ValidateModelPrice(
            new ModelPrice
            {
                Items =
                [
                    new ModelPriceItem
                    {
                        ItemCode = PriceItemCodes.CompletionTokens,
                        Pricing = Tiered([
                            new PriceTier { UpTo = 1000, PricePerUnit = 0.5m },
                            new PriceTier { PricePerUnit = 0.25m },
                        ]),
                        PromptWriteCacheVariants =
                        [
                            new PromptWriteCacheVariant
                            {
                                VariantCode = PromptWriteCacheVariantCodes.OneHour,
                                Pricing = new Pricing
                                {
                                    Mode = PricingModes.UsagePerUnit,
                                    UsagePerUnit = 0.2m,
                                },
                            },
                        ],
                    },
                ],
                Schedule = Schedule(
                    "Asia/Shanghai",
                    Override(
                        when: new OverrideWhen
                        {
                            DailyTime = new DailyTimeRange { Start = "22:00", End = "08:00" },
                            Weekdays = [1, 7],
                            DateRange = new DateRange { Start = "2026-01-01", End = "2026-12-31" },
                        })),
            });
    }

    // ---------------------------------------------------------------------
    // Equals
    // ---------------------------------------------------------------------

    [Fact]
    public void EqualsTreatsNilAndEmptyItemsAlike()
    {
        Assert.True(ModelPriceValidation.ModelPriceEquals(
            new ModelPrice { Items = null },
            new ModelPrice { Items = [] }));
        Assert.True(ModelPriceValidation.ModelPriceEquals(null, null));
        Assert.False(ModelPriceValidation.ModelPriceEquals(null, new ModelPrice()));
        Assert.False(ModelPriceValidation.ModelPriceEquals(new ModelPrice(), null));
    }

    [Fact]
    public void EqualsComparesDecimalsByValue()
    {
        Assert.True(ModelPriceValidation.ModelPriceEquals(
            Price(new Pricing { Mode = PricingModes.FlatFee, FlatFee = 1.50m }),
            Price(new Pricing { Mode = PricingModes.FlatFee, FlatFee = 1.5m })));
        Assert.False(ModelPriceValidation.ModelPriceEquals(
            Price(new Pricing { Mode = PricingModes.FlatFee, FlatFee = 1.5m }),
            Price(new Pricing { Mode = PricingModes.FlatFee, FlatFee = 1.6m })));
    }

    [Fact]
    public void EqualsDistinguishesAbsentFromZeroDecimals()
    {
        Assert.False(ModelPriceValidation.ModelPriceEquals(
            Price(new Pricing { Mode = PricingModes.FlatFee }),
            Price(new Pricing { Mode = PricingModes.FlatFee, FlatFee = 0m })));
    }

    [Fact]
    public void EqualsIsOrderSensitiveForItems()
    {
        var prompt = new ModelPriceItem
        {
            ItemCode = PriceItemCodes.PromptTokens,
            Pricing = new Pricing { Mode = PricingModes.FlatFee, FlatFee = 1m },
        };
        var completion = new ModelPriceItem
        {
            ItemCode = PriceItemCodes.CompletionTokens,
            Pricing = new Pricing { Mode = PricingModes.FlatFee, FlatFee = 2m },
        };

        Assert.True(ModelPriceValidation.ModelPriceEquals(
            new ModelPrice { Items = [prompt, completion] },
            new ModelPrice { Items = [prompt, completion] }));
        Assert.False(ModelPriceValidation.ModelPriceEquals(
            new ModelPrice { Items = [prompt, completion] },
            new ModelPrice { Items = [completion, prompt] }));
    }

    [Fact]
    public void EqualsComparesVariants()
    {
        var variant = new PromptWriteCacheVariant
        {
            VariantCode = PromptWriteCacheVariantCodes.FiveMin,
            Pricing = new Pricing { Mode = PricingModes.FlatFee, FlatFee = 3m },
        };

        Assert.True(ModelPriceValidation.ModelPriceEquals(
            VariantItem([variant]),
            VariantItem([variant])));

        // A different variant code changes structural equality even though the
        // item pricing is identical.
        Assert.False(ModelPriceValidation.ModelPriceEquals(
            VariantItem([variant]),
            VariantItem([
                new PromptWriteCacheVariant
                {
                    VariantCode = PromptWriteCacheVariantCodes.OneHour,
                    Pricing = variant.Pricing,
                },
            ])));

        Assert.False(ModelPriceValidation.ModelPriceEquals(
            VariantItem([variant]),
            VariantItem([])));
    }

    [Fact]
    public void EqualsComparesTheScheduleTree()
    {
        var withSchedule = WithSchedule(Schedule(
            "UTC",
            Override(
                "night",
                2,
                new OverrideWhen
                {
                    DailyTime = new DailyTimeRange { Start = "22:00", End = "08:00" },
                })));

        Assert.True(ModelPriceValidation.ModelPriceEquals(
            withSchedule,
            WithSchedule(Schedule(
                "UTC",
                Override(
                    "night",
                    2,
                    new OverrideWhen
                    {
                        DailyTime = new DailyTimeRange { Start = "22:00", End = "08:00" },
                    })))));

        Assert.False(ModelPriceValidation.ModelPriceEquals(new ModelPrice(), withSchedule));
        Assert.False(ModelPriceValidation.ModelPriceEquals(
            withSchedule,
            WithSchedule(Schedule(
                "Asia/Shanghai",
                Override(
                    "night",
                    2,
                    new OverrideWhen
                    {
                        DailyTime = new DailyTimeRange { Start = "22:00", End = "08:00" },
                    })))));
        Assert.False(ModelPriceValidation.ModelPriceEquals(
            withSchedule,
            WithSchedule(Schedule(
                "UTC",
                Override(
                    "night",
                    3,
                    new OverrideWhen
                    {
                        DailyTime = new DailyTimeRange { Start = "22:00", End = "08:00" },
                    })))));
        Assert.False(ModelPriceValidation.ModelPriceEquals(
            withSchedule,
            WithSchedule(Schedule("UTC", Override()))));
    }

    [Fact]
    public void EqualsTreatsNilAndEmptyVariantsAndOverridesAlike()
    {
        Assert.True(ModelPriceValidation.ModelPriceEquals(
            VariantItem(null),
            VariantItem([])));
        Assert.True(ModelPriceValidation.ModelPriceEquals(
            WithSchedule(new PriceSchedule { Timezone = "UTC", Overrides = null }),
            WithSchedule(new PriceSchedule { Timezone = "UTC", Overrides = [] })));
    }

    [Fact]
    public void EqualsIgnoresPayloadFieldsOfUnknownPricingModes()
    {
        // The reference switch only compares the payload of known modes; two
        // equal mode strings always compare equal otherwise.
        Assert.True(ModelPriceValidation.ModelPriceEquals(
            Price(new Pricing { Mode = "mystery", FlatFee = 1m }),
            Price(new Pricing { Mode = "mystery", UsagePerUnit = 9m })));
        Assert.False(ModelPriceValidation.ModelPriceEquals(
            Price(new Pricing { Mode = "mystery" }),
            Price(new Pricing { Mode = "other" })));
    }

    [Fact]
    public void EqualsHandlesNullNestedReceiversByReference()
    {
        Assert.True(ModelPriceValidation.ItemEquals(null, null));
        Assert.False(ModelPriceValidation.ItemEquals(null, VariantItem(null).Items![0]));
        Assert.False(ModelPriceValidation.ItemEquals(VariantItem(null).Items![0], null));
    }

    private static void AssertMessage(string expected, Action action)
    {
        var error = Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(expected, error.Message);
    }

    private static ModelPrice Price(Pricing pricing)
        => new()
        {
            Items =
            [
                new ModelPriceItem
                {
                    ItemCode = PriceItemCodes.PromptTokens,
                    Pricing = pricing,
                },
            ],
        };

    private static Pricing Tiered(List<PriceTier> tiers)
        => new()
        {
            Mode = PricingModes.Tiered,
            UsageTiered = new TieredPricing { Tiers = tiers },
        };

    private static ModelPrice WithSchedule(PriceSchedule schedule)
        => new()
        {
            Items =
            [
                new ModelPriceItem
                {
                    ItemCode = PriceItemCodes.PromptTokens,
                    Pricing = new Pricing
                    {
                        Mode = PricingModes.UsagePerUnit,
                        UsagePerUnit = 0.1m,
                    },
                },
            ],
            Schedule = schedule,
        };

    private static PriceSchedule Schedule(string timezone, params PriceOverride[] overrides)
        => new() { Timezone = timezone, Overrides = [.. overrides] };

    private static PriceOverride Override(
        string name = "override",
        int priority = 1,
        OverrideWhen? when = null,
        List<ModelPriceItem>? items = null)
    {
        when ??= new OverrideWhen { Weekdays = [1] };
        items ??=
        [
            new ModelPriceItem
            {
                ItemCode = PriceItemCodes.PromptTokens,
                Pricing = new Pricing
                {
                    Mode = PricingModes.UsagePerUnit,
                    UsagePerUnit = 0.05m,
                },
            },
        ];

        return new PriceOverride
        {
            Name = name,
            Priority = priority,
            When = when,
            Items = items,
        };
    }

    private static ModelPrice VariantItem(List<PromptWriteCacheVariant>? variants)
        => new()
        {
            Items =
            [
                new ModelPriceItem
                {
                    ItemCode = PriceItemCodes.PromptWriteCachedTokens,
                    Pricing = new Pricing
                    {
                        Mode = PricingModes.UsagePerUnit,
                        UsagePerUnit = 0.1m,
                    },
                    PromptWriteCacheVariants = variants,
                },
            ],
        };
}
