using AxonHub.Api.Billing;
using AxonHub.Api.Management;
using AxonHub.Persistence;
using HotChocolate;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the PricingMode enum (price.graphql:2-7).</summary>
[GraphQLName("PricingMode")]
public enum PricingModeValue
{
    [GraphQLName("flat_fee")]
    FlatFee,

    [GraphQLName("usage_per_unit")]
    UsagePerUnit,

    [GraphQLName("usage_tiered")]
    UsageTiered,

    [GraphQLName("usage_volume")]
    UsageVolume,
}

/// <summary>Port of the PriceItemCode enum (price.graphql:9-14).</summary>
[GraphQLName("PriceItemCode")]
public enum PriceItemCodeValue
{
    [GraphQLName("prompt_tokens")]
    PromptTokens,

    [GraphQLName("completion_tokens")]
    CompletionTokens,

    [GraphQLName("prompt_cached_tokens")]
    PromptCachedTokens,

    [GraphQLName("prompt_write_cached_tokens")]
    PromptWriteCachedTokens,
}

/// <summary>Port of the PromptWriteCacheVariantCode enum (price.graphql:16-19).</summary>
[GraphQLName("PromptWriteCacheVariantCode")]
public enum PromptWriteCacheVariantCodeValue
{
    [GraphQLName("five_min")]
    FiveMin,

    [GraphQLName("one_hour")]
    OneHour,
}

/// <summary>Port of the ModelPrice output type (price.graphql:22-25).</summary>
[GraphQLName("ModelPrice")]
public sealed class ModelPriceType
{
    public IReadOnlyList<ModelPriceItemType> Items { get; init; } = [];

    public PriceScheduleType? Schedule { get; init; }
}

/// <summary>Port of the ModelPriceItem output type (price.graphql:27-31).</summary>
[GraphQLName("ModelPriceItem")]
public sealed class ModelPriceItemType
{
    public PriceItemCodeValue ItemCode { get; init; }

    public PricingType Pricing { get; init; } = new();

    public IReadOnlyList<PromptWriteCacheVariantType>? PromptWriteCacheVariants { get; init; }
}

/// <summary>Port of the PriceSchedule output type (price.graphql:33-37).</summary>
[GraphQLName("PriceSchedule")]
public sealed class PriceScheduleType
{
    public string Timezone { get; init; } = string.Empty;

    public IReadOnlyList<PriceOverrideType> Overrides { get; init; } = [];
}

/// <summary>Port of the PriceOverride output type (price.graphql:39-44).</summary>
[GraphQLName("PriceOverride")]
public sealed class PriceOverrideType
{
    public string Name { get; init; } = string.Empty;

    public int Priority { get; init; }

    public OverrideWhenType When { get; init; } = new();

    public IReadOnlyList<ModelPriceItemType> Items { get; init; } = [];
}

/// <summary>Port of the OverrideWhen output type (price.graphql:46-50).</summary>
[GraphQLName("OverrideWhen")]
public sealed class OverrideWhenType
{
    public DailyTimeRangeType? DailyTime { get; init; }

    public IReadOnlyList<int>? Weekdays { get; init; }

    public DateRangeType? DateRange { get; init; }
}

/// <summary>Port of the DailyTimeRange output type (price.graphql:52-55).</summary>
[GraphQLName("DailyTimeRange")]
public sealed class DailyTimeRangeType
{
    public string Start { get; init; } = string.Empty;

    public string End { get; init; } = string.Empty;
}

/// <summary>Port of the DateRange output type (price.graphql:57-60).</summary>
[GraphQLName("DateRange")]
public sealed class DateRangeType
{
    public string Start { get; init; } = string.Empty;

    public string End { get; init; } = string.Empty;
}

/// <summary>Port of the Pricing output type (price.graphql:62-67).</summary>
[GraphQLName("Pricing")]
public sealed class PricingType
{
    public PricingModeValue Mode { get; init; }

    [GraphQLType(typeof(GoDecimalType))]
    public decimal? FlatFee { get; init; }

    [GraphQLType(typeof(GoDecimalType))]
    public decimal? UsagePerUnit { get; init; }

    public TieredPricingType? UsageTiered { get; init; }
}

/// <summary>Port of the TieredPricing output type (price.graphql:69-71).</summary>
[GraphQLName("TieredPricing")]
public sealed class TieredPricingType
{
    public IReadOnlyList<PriceTierType> Tiers { get; init; } = [];
}

/// <summary>Port of the PriceTier output type (price.graphql:73-76).</summary>
[GraphQLName("PriceTier")]
public sealed class PriceTierType
{
    public int? UpTo { get; init; }

    [GraphQLType(typeof(GoDecimalType))]
    public decimal PricePerUnit { get; init; }
}

/// <summary>Port of the PromptWriteCacheVariant output type (price.graphql:78-81).</summary>
[GraphQLName("PromptWriteCacheVariant")]
public sealed class PromptWriteCacheVariantType
{
    public PromptWriteCacheVariantCodeValue VariantCode { get; init; }

    public PricingType Pricing { get; init; } = new();
}

/// <summary>Port of objects.ModelPrice's ent GraphQL type (ent.graphql:574-590).</summary>
[GraphQLName("ChannelModelPrice")]
public sealed class ChannelModelPriceType
{
    [GraphQLIgnore]
    public long DbId { get; init; }

    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt { get; init; }

    [GraphQLName("channelID")]
    [GraphQLType(typeof(IdType))]
    public string ChannelID { get; init; } = string.Empty;

    [GraphQLName("modelID")]
    public string ModelID { get; init; } = string.Empty;

    public ModelPriceType Price { get; init; } = new();

    [GraphQLName("referenceID")]
    public string ReferenceID { get; init; } = string.Empty;
}

/// <summary>Input types of the price SDL (price.graphql:83-148).</summary>
public sealed class ModelPriceInput
{
    public IReadOnlyList<ModelPriceItemInput> Items { get; init; } = [];

    public PriceScheduleInput? Schedule { get; init; }
}

public sealed class ModelPriceItemInput
{
    public PriceItemCodeValue ItemCode { get; init; }

    public PricingInput Pricing { get; init; } = new();

    public IReadOnlyList<PromptWriteCacheVariantInput>? PromptWriteCacheVariants { get; init; }
}

public sealed class PriceScheduleInput
{
    public string Timezone { get; init; } = string.Empty;

    public IReadOnlyList<PriceOverrideInput> Overrides { get; init; } = [];
}

public sealed class PriceOverrideInput
{
    public string Name { get; init; } = string.Empty;

    public int Priority { get; init; }

    public OverrideWhenInput When { get; init; } = new();

    public IReadOnlyList<ModelPriceItemInput> Items { get; init; } = [];
}

public sealed class OverrideWhenInput
{
    public DailyTimeRangeInput? DailyTime { get; init; }

    public IReadOnlyList<int>? Weekdays { get; init; }

    public DateRangeInput? DateRange { get; init; }
}

public sealed class DailyTimeRangeInput
{
    public string Start { get; init; } = string.Empty;

    public string End { get; init; } = string.Empty;
}

public sealed class DateRangeInput
{
    public string Start { get; init; } = string.Empty;

    public string End { get; init; } = string.Empty;
}

public sealed class PricingInput
{
    public PricingModeValue Mode { get; init; }

    [GraphQLType(typeof(GoDecimalType))]
    public decimal? FlatFee { get; init; }

    [GraphQLType(typeof(GoDecimalType))]
    public decimal? UsagePerUnit { get; init; }

    public TieredPricingInput? UsageTiered { get; init; }
}

public sealed class TieredPricingInput
{
    public IReadOnlyList<PriceTierInput> Tiers { get; init; } = [];
}

public sealed class PriceTierInput
{
    public int? UpTo { get; init; }

    [GraphQLType(typeof(GoDecimalType))]
    public decimal PricePerUnit { get; init; }
}

public sealed class PromptWriteCacheVariantInput
{
    public PromptWriteCacheVariantCodeValue VariantCode { get; init; }

    public PricingInput Pricing { get; init; } = new();
}

/// <summary>Port of biz.SaveChannelModelPriceInput on the wire (price.graphql:145-148).</summary>
public sealed class SaveChannelModelPriceInput
{
    public string ModelId { get; init; } = string.Empty;

    public ModelPriceInput Price { get; init; } = new();
}

/// <summary>Mapping between the price SDL types and objects.ModelPrice.</summary>
public static class PriceMapping
{
    public static IReadOnlyList<Management.SaveChannelModelPriceInput> ToCommands(
        IReadOnlyList<SaveChannelModelPriceInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var commands = new List<Management.SaveChannelModelPriceInput>(inputs.Count);
        foreach (var input in inputs)
        {
            commands.Add(new Management.SaveChannelModelPriceInput(
                input.ModelId,
                ToDomain(input.Price)));
        }

        return commands;
    }

    public static ModelPrice ToDomain(ModelPriceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return new ModelPrice
        {
            Items = input.Items.Select(ToDomain).ToList(),
            Schedule = input.Schedule is null ? null : ToDomain(input.Schedule),
        };
    }

    public static ChannelModelPriceType ToGraphQL(ChannelModelPriceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new ChannelModelPriceType
        {
            DbId = record.Id,
            Id = new GidValue("ChannelModelPrice", record.Id).ToString(),
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            ChannelID = new GidValue("Channel", record.ChannelId).ToString(),
            ModelID = record.ModelId,
            Price = ToGraphQL(ChannelModelPriceService.DeserializePrice(record.Price)),
            ReferenceID = record.ReferenceId,
        };
    }

    public static ModelPriceType ToGraphQL(ModelPrice price)
    {
        ArgumentNullException.ThrowIfNull(price);

        return new ModelPriceType
        {
            Items = (price.Items ?? []).Select(ToGraphQL).ToList(),
            Schedule = price.Schedule is null ? null : ToGraphQL(price.Schedule),
        };
    }

    private static ModelPriceItem ToDomain(ModelPriceItemInput input)
        => new()
        {
            ItemCode = ToWire(input.ItemCode),
            Pricing = ToDomain(input.Pricing),
            PromptWriteCacheVariants = input.PromptWriteCacheVariants?
                .Select(ToDomain)
                .ToList(),
        };

    private static PromptWriteCacheVariant ToDomain(PromptWriteCacheVariantInput input)
        => new()
        {
            VariantCode = ToWire(input.VariantCode),
            Pricing = ToDomain(input.Pricing),
        };

    private static Pricing ToDomain(PricingInput input)
        => new()
        {
            Mode = ToWire(input.Mode),
            FlatFee = input.FlatFee,
            UsagePerUnit = input.UsagePerUnit,
            UsageTiered = input.UsageTiered is null ? null : ToDomain(input.UsageTiered),
        };

    private static TieredPricing ToDomain(TieredPricingInput input)
        => new()
        {
            Tiers = input.Tiers.Select(static tier => new PriceTier
            {
                UpTo = tier.UpTo,
                PricePerUnit = tier.PricePerUnit,
            }).ToList(),
        };

    private static PriceSchedule ToDomain(PriceScheduleInput input)
        => new()
        {
            Timezone = input.Timezone,
            Overrides = input.Overrides.Select(ToDomain).ToList(),
        };

    private static PriceOverride ToDomain(PriceOverrideInput input)
        => new()
        {
            Name = input.Name,
            Priority = input.Priority,
            When = ToDomain(input.When),
            Items = input.Items.Select(ToDomain).ToList(),
        };

    private static OverrideWhen ToDomain(OverrideWhenInput input)
        => new()
        {
            DailyTime = input.DailyTime is null
                ? null
                : new DailyTimeRange
                {
                    Start = input.DailyTime.Start,
                    End = input.DailyTime.End,
                },
            Weekdays = input.Weekdays?.ToList(),
            DateRange = input.DateRange is null
                ? null
                : new DateRange
                {
                    Start = input.DateRange.Start,
                    End = input.DateRange.End,
                },
        };

    private static PricingType ToGraphQL(Pricing pricing)
        => new()
        {
            Mode = ModeFromWire(pricing.Mode),
            FlatFee = pricing.FlatFee,
            UsagePerUnit = pricing.UsagePerUnit,
            UsageTiered = pricing.UsageTiered is null
                ? null
                : new TieredPricingType
                {
                    Tiers = (pricing.UsageTiered.Tiers ?? [])
                        .Select(static tier => new PriceTierType
                        {
                            UpTo = tier.UpTo is null ? null : checked((int)tier.UpTo.Value),
                            PricePerUnit = tier.PricePerUnit,
                        })
                        .ToList(),
                },
        };

    private static ModelPriceItemType ToGraphQL(ModelPriceItem item)
        => new()
        {
            ItemCode = ItemCodeFromWire(item.ItemCode),
            Pricing = ToGraphQL(item.Pricing ?? new Pricing()),
            PromptWriteCacheVariants = item.PromptWriteCacheVariants?
                .Select(static variant => new PromptWriteCacheVariantType
                {
                    VariantCode = VariantCodeFromWire(variant.VariantCode),
                    Pricing = ToGraphQL(variant.Pricing ?? new Pricing()),
                })
                .ToList(),
        };

    private static PriceScheduleType ToGraphQL(PriceSchedule schedule)
        => new()
        {
            Timezone = schedule.Timezone,
            Overrides = (schedule.Overrides ?? []).Select(static priceOverride => new PriceOverrideType
            {
                Name = priceOverride.Name,
                Priority = priceOverride.Priority,
                When = ToGraphQL(priceOverride.When),
                Items = (priceOverride.Items ?? []).Select(ToGraphQL).ToList(),
            }).ToList(),
        };

    private static OverrideWhenType ToGraphQL(OverrideWhen when)
        => new()
        {
            DailyTime = when.DailyTime is null
                ? null
                : new DailyTimeRangeType
                {
                    Start = when.DailyTime.Start,
                    End = when.DailyTime.End,
                },
            Weekdays = when.Weekdays?.ToList(),
            DateRange = when.DateRange is null
                ? null
                : new DateRangeType
                {
                    Start = when.DateRange.Start,
                    End = when.DateRange.End,
                },
        };

    private static string ToWire(PricingModeValue mode) => mode switch
    {
        PricingModeValue.FlatFee => PricingModes.FlatFee,
        PricingModeValue.UsagePerUnit => PricingModes.UsagePerUnit,
        PricingModeValue.UsageTiered => PricingModes.Tiered,
        PricingModeValue.UsageVolume => PricingModes.Volume,
        _ => throw new InvalidOperationException("unknown pricing mode: " + mode),
    };

    private static PricingModeValue ModeFromWire(string mode) => mode switch
    {
        PricingModes.FlatFee => PricingModeValue.FlatFee,
        PricingModes.UsagePerUnit => PricingModeValue.UsagePerUnit,
        PricingModes.Tiered => PricingModeValue.UsageTiered,
        PricingModes.Volume => PricingModeValue.UsageVolume,
        _ => throw new InvalidOperationException("unknown pricing mode: " + mode),
    };

    private static string ToWire(PriceItemCodeValue code) => code switch
    {
        PriceItemCodeValue.PromptTokens => PriceItemCodes.PromptTokens,
        PriceItemCodeValue.CompletionTokens => PriceItemCodes.CompletionTokens,
        PriceItemCodeValue.PromptCachedTokens => PriceItemCodes.PromptCachedTokens,
        PriceItemCodeValue.PromptWriteCachedTokens => PriceItemCodes.PromptWriteCachedTokens,
        _ => throw new InvalidOperationException("unknown price item code: " + code),
    };

    /// <summary>
    /// Maps a stored cost item's wire code to the PriceItemCode enum. An
    /// unknown code fails the field like gqlgen's enum marshaler does.
    /// </summary>
    public static PriceItemCodeValue ToGraphQLItemCode(string code) => ItemCodeFromWire(code);

    private static PriceItemCodeValue ItemCodeFromWire(string code) => code switch
    {
        PriceItemCodes.PromptTokens => PriceItemCodeValue.PromptTokens,
        PriceItemCodes.CompletionTokens => PriceItemCodeValue.CompletionTokens,
        PriceItemCodes.PromptCachedTokens => PriceItemCodeValue.PromptCachedTokens,
        PriceItemCodes.PromptWriteCachedTokens => PriceItemCodeValue.PromptWriteCachedTokens,
        _ => throw new InvalidOperationException("unknown price item code: " + code),
    };

    private static string ToWire(PromptWriteCacheVariantCodeValue code) => code switch
    {
        PromptWriteCacheVariantCodeValue.FiveMin => PromptWriteCacheVariantCodes.FiveMin,
        PromptWriteCacheVariantCodeValue.OneHour => PromptWriteCacheVariantCodes.OneHour,
        _ => throw new InvalidOperationException(
            "unknown prompt write cache variant code: " + code),
    };

    private static PromptWriteCacheVariantCodeValue VariantCodeFromWire(string code) => code switch
    {
        PromptWriteCacheVariantCodes.FiveMin => PromptWriteCacheVariantCodeValue.FiveMin,
        PromptWriteCacheVariantCodes.OneHour => PromptWriteCacheVariantCodeValue.OneHour,
        _ => throw new InvalidOperationException(
            "unknown prompt write cache variant code: " + code),
    };
}
