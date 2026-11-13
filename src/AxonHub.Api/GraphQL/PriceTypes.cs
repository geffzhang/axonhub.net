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

/// <summary>Port of the ChannelModelPriceVersionStatus enum (ent.graphql:720-723).</summary>
[GraphQLName("ChannelModelPriceVersionStatus")]
public enum ChannelModelPriceVersionStatusValue
{
    [GraphQLName("active")]
    Active,

    [GraphQLName("archived")]
    Archived,
}

/// <summary>Port of objects.ModelPrice's ent GraphQL type (ent.graphql:574-590).</summary>
[GraphQLName("ChannelModelPrice")]
public sealed class ChannelModelPriceType : INode
{
    [GraphQLIgnore]
    public long DbId { get; init; }

    [GraphQLIgnore]
    public long DbChannelId { get; init; }

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

/// <summary>
/// Edge fields of ChannelModelPrice. The entgql SDL declares the edges after the
/// scalar columns, and the generated schema keeps that order; HotChocolate emits
/// CLR resolver methods before properties, so the edges live in an extension,
/// which is merged after the base fields.
/// </summary>
[ExtendObjectType(typeof(ChannelModelPriceType))]
public sealed class ChannelModelPriceTypeExtensions
{
    /// <summary>
    /// Port of the generated edge accessor (gql_edge.go:151-157):
    /// <c>QueryChannel().Only(ctx)</c> with the Channel query policy applied.
    /// </summary>
    [GraphQLName("channel")]
    public async Task<ChannelType> GetChannelAsync(
        [Parent] ChannelModelPriceType price,
        [Service] ChannelPrivacy privacy,
        [Service] IChannelAdminStore channels,
        CancellationToken cancellationToken)
    {
        await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

        var record = await channels.GetByIdAsync(price.DbChannelId, cancellationToken)
            .ConfigureAwait(false);
        return record is null
            ? throw new InvalidOperationException("ent: channel not found")
            : ChannelMapping.ToGraphQL(record);
    }

    /// <summary>
    /// Port of the generated edge accessor (gql_edge.go:159-169):
    /// <c>QueryVersions().All(ctx)</c> with the ChannelModelPriceVersion query
    /// policy applied.
    /// </summary>
    [GraphQLName("versions")]
    public async Task<IReadOnlyList<ChannelModelPriceVersionType>> GetVersionsAsync(
        [Parent] ChannelModelPriceType price,
        [Service] ChannelPrivacy privacy,
        [Service] IChannelModelPriceStore prices,
        CancellationToken cancellationToken)
    {
        await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

        var rows = await prices.ListVersionsAsync(price.DbId, cancellationToken)
            .ConfigureAwait(false);
        return [.. rows.Select(PriceMapping.ToGraphQL)];
    }
}

/// <summary>Port of the ChannelModelPriceVersion type (ent.graphql:667-705).</summary>
[GraphQLName("ChannelModelPriceVersion")]
public sealed class ChannelModelPriceVersionType : INode
{
    [GraphQLIgnore]
    public long DbId { get; init; }

    [GraphQLIgnore]
    public long DbChannelModelPriceId { get; init; }

    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt { get; init; }

    [GraphQLName("channelID")]
    public int ChannelID { get; init; }

    [GraphQLName("modelID")]
    public string ModelID { get; init; } = string.Empty;

    [GraphQLName("channelModelPriceID")]
    [GraphQLType(typeof(IdType))]
    public string ChannelModelPriceID { get; init; } = string.Empty;

    public ModelPriceType Price { get; init; } = new();

    public ChannelModelPriceVersionStatusValue Status { get; init; }

    [GraphQLName("effectiveStartAt")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime EffectiveStartAt { get; init; }

    [GraphQLName("effectiveEndAt")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? EffectiveEndAt { get; init; }

    [GraphQLName("referenceID")]
    public string ReferenceID { get; init; } = string.Empty;
}

/// <summary>
/// Edge field of ChannelModelPriceVersion, kept in an extension so the
/// introspection field order matches the entgql SDL (see
/// <see cref="ChannelModelPriceTypeExtensions"/>).
/// </summary>
[ExtendObjectType(typeof(ChannelModelPriceVersionType))]
public sealed class ChannelModelPriceVersionTypeExtensions
{
    /// <summary>
    /// Port of the generated edge accessor (gql_edge.go:171-177):
    /// <c>QueryChannelModelPrice().Only(ctx)</c> with the ChannelModelPrice
    /// query policy applied.
    /// </summary>
    [GraphQLName("channelModelPrice")]
    public async Task<ChannelModelPriceType> GetChannelModelPriceAsync(
        [Parent] ChannelModelPriceVersionType version,
        [Service] ChannelPrivacy privacy,
        [Service] IChannelModelPriceStore prices,
        CancellationToken cancellationToken)
    {
        await privacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);

        var record = await prices.GetByIdAsync(version.DbChannelModelPriceId, cancellationToken)
            .ConfigureAwait(false);
        return record is null
            ? throw new InvalidOperationException("ent: channel_model_price not found")
            : PriceMapping.ToGraphQL(record);
    }
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

/// <summary>
/// Port of <c>ChannelModelPriceWhereInput</c> (ent.graphql). The filter itself
/// never restricts <c>deleted_at</c>, matching the generated sqlgraph
/// predicates used inside a nested edge filter.
/// </summary>
[GraphQLName("ChannelModelPriceWhereInput")]
public sealed class ChannelModelPriceWhereInput
{
    public ChannelModelPriceWhereInput? Not { get; set; }

    public IReadOnlyList<ChannelModelPriceWhereInput>? And { get; set; }

    public IReadOnlyList<ChannelModelPriceWhereInput>? Or { get; set; }

    [GraphQLName("id")]
    [GraphQLType(typeof(IdType))]
    public string? Id { get; set; }

    [GraphQLName("idNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? IdNEQ { get; set; }

    [GraphQLName("idIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdIn { get; set; }

    [GraphQLName("idNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdNotIn { get; set; }

    [GraphQLName("idGT")]
    [GraphQLType(typeof(IdType))]
    public string? IdGT { get; set; }

    [GraphQLName("idGTE")]
    [GraphQLType(typeof(IdType))]
    public string? IdGTE { get; set; }

    [GraphQLName("idLT")]
    [GraphQLType(typeof(IdType))]
    public string? IdLT { get; set; }

    [GraphQLName("idLTE")]
    [GraphQLType(typeof(IdType))]
    public string? IdLTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAt { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtNEQ { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? CreatedAtIn { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? CreatedAtNotIn { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtGT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtGTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtLT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtLTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAt { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtNEQ { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? UpdatedAtIn { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? UpdatedAtNotIn { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtGT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtGTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtLT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtLTE { get; set; }

    [GraphQLName("channelID")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelID { get; set; }

    [GraphQLName("channelIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelIDNEQ { get; set; }

    [GraphQLName("channelIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ChannelIDIn { get; set; }

    [GraphQLName("channelIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ChannelIDNotIn { get; set; }

    [GraphQLName("modelID")]
    public string? ModelID { get; set; }

    [GraphQLName("modelIDNEQ")]
    public string? ModelIDNEQ { get; set; }

    [GraphQLName("modelIDIn")]
    public IReadOnlyList<string>? ModelIDIn { get; set; }

    [GraphQLName("modelIDNotIn")]
    public IReadOnlyList<string>? ModelIDNotIn { get; set; }

    [GraphQLName("modelIDGT")]
    public string? ModelIDGT { get; set; }

    [GraphQLName("modelIDGTE")]
    public string? ModelIDGTE { get; set; }

    [GraphQLName("modelIDLT")]
    public string? ModelIDLT { get; set; }

    [GraphQLName("modelIDLTE")]
    public string? ModelIDLTE { get; set; }

    [GraphQLName("modelIDContains")]
    public string? ModelIDContains { get; set; }

    [GraphQLName("modelIDHasPrefix")]
    public string? ModelIDHasPrefix { get; set; }

    [GraphQLName("modelIDHasSuffix")]
    public string? ModelIDHasSuffix { get; set; }

    [GraphQLName("modelIDEqualFold")]
    public string? ModelIDEqualFold { get; set; }

    [GraphQLName("modelIDContainsFold")]
    public string? ModelIDContainsFold { get; set; }

    [GraphQLName("referenceID")]
    public string? ReferenceID { get; set; }

    [GraphQLName("referenceIDNEQ")]
    public string? ReferenceIDNEQ { get; set; }

    [GraphQLName("referenceIDIn")]
    public IReadOnlyList<string>? ReferenceIDIn { get; set; }

    [GraphQLName("referenceIDNotIn")]
    public IReadOnlyList<string>? ReferenceIDNotIn { get; set; }

    [GraphQLName("referenceIDGT")]
    public string? ReferenceIDGT { get; set; }

    [GraphQLName("referenceIDGTE")]
    public string? ReferenceIDGTE { get; set; }

    [GraphQLName("referenceIDLT")]
    public string? ReferenceIDLT { get; set; }

    [GraphQLName("referenceIDLTE")]
    public string? ReferenceIDLTE { get; set; }

    [GraphQLName("referenceIDContains")]
    public string? ReferenceIDContains { get; set; }

    [GraphQLName("referenceIDHasPrefix")]
    public string? ReferenceIDHasPrefix { get; set; }

    [GraphQLName("referenceIDHasSuffix")]
    public string? ReferenceIDHasSuffix { get; set; }

    [GraphQLName("referenceIDEqualFold")]
    public string? ReferenceIDEqualFold { get; set; }

    [GraphQLName("referenceIDContainsFold")]
    public string? ReferenceIDContainsFold { get; set; }

    public bool? HasChannel { get; set; }

    public IReadOnlyList<ChannelWhereInput>? HasChannelWith { get; set; }

    public bool? HasVersions { get; set; }

    public IReadOnlyList<ChannelModelPriceVersionWhereInput>? HasVersionsWith { get; set; }
}

/// <summary>
/// Port of <c>ChannelModelPriceVersionWhereInput</c> (ent.graphql).
/// </summary>
[GraphQLName("ChannelModelPriceVersionWhereInput")]
public sealed class ChannelModelPriceVersionWhereInput
{
    public ChannelModelPriceVersionWhereInput? Not { get; set; }

    public IReadOnlyList<ChannelModelPriceVersionWhereInput>? And { get; set; }

    public IReadOnlyList<ChannelModelPriceVersionWhereInput>? Or { get; set; }

    [GraphQLName("id")]
    [GraphQLType(typeof(IdType))]
    public string? Id { get; set; }

    [GraphQLName("idNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? IdNEQ { get; set; }

    [GraphQLName("idIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdIn { get; set; }

    [GraphQLName("idNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdNotIn { get; set; }

    [GraphQLName("idGT")]
    [GraphQLType(typeof(IdType))]
    public string? IdGT { get; set; }

    [GraphQLName("idGTE")]
    [GraphQLType(typeof(IdType))]
    public string? IdGTE { get; set; }

    [GraphQLName("idLT")]
    [GraphQLType(typeof(IdType))]
    public string? IdLT { get; set; }

    [GraphQLName("idLTE")]
    [GraphQLType(typeof(IdType))]
    public string? IdLTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAt { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtNEQ { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? CreatedAtIn { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? CreatedAtNotIn { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtGT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtGTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtLT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? CreatedAtLTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAt { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtNEQ { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? UpdatedAtIn { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? UpdatedAtNotIn { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtGT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtGTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtLT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? UpdatedAtLTE { get; set; }

    [GraphQLName("channelID")]
    public int? ChannelID { get; set; }

    [GraphQLName("channelIDNEQ")]
    public int? ChannelIDNEQ { get; set; }

    [GraphQLName("channelIDIn")]
    public IReadOnlyList<int>? ChannelIDIn { get; set; }

    [GraphQLName("channelIDNotIn")]
    public IReadOnlyList<int>? ChannelIDNotIn { get; set; }

    [GraphQLName("channelIDGT")]
    public int? ChannelIDGT { get; set; }

    [GraphQLName("channelIDGTE")]
    public int? ChannelIDGTE { get; set; }

    [GraphQLName("channelIDLT")]
    public int? ChannelIDLT { get; set; }

    [GraphQLName("channelIDLTE")]
    public int? ChannelIDLTE { get; set; }

    [GraphQLName("modelID")]
    public string? ModelID { get; set; }

    [GraphQLName("modelIDNEQ")]
    public string? ModelIDNEQ { get; set; }

    [GraphQLName("modelIDIn")]
    public IReadOnlyList<string>? ModelIDIn { get; set; }

    [GraphQLName("modelIDNotIn")]
    public IReadOnlyList<string>? ModelIDNotIn { get; set; }

    [GraphQLName("modelIDGT")]
    public string? ModelIDGT { get; set; }

    [GraphQLName("modelIDGTE")]
    public string? ModelIDGTE { get; set; }

    [GraphQLName("modelIDLT")]
    public string? ModelIDLT { get; set; }

    [GraphQLName("modelIDLTE")]
    public string? ModelIDLTE { get; set; }

    [GraphQLName("modelIDContains")]
    public string? ModelIDContains { get; set; }

    [GraphQLName("modelIDHasPrefix")]
    public string? ModelIDHasPrefix { get; set; }

    [GraphQLName("modelIDHasSuffix")]
    public string? ModelIDHasSuffix { get; set; }

    [GraphQLName("modelIDEqualFold")]
    public string? ModelIDEqualFold { get; set; }

    [GraphQLName("modelIDContainsFold")]
    public string? ModelIDContainsFold { get; set; }

    [GraphQLName("channelModelPriceID")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelModelPriceID { get; set; }

    [GraphQLName("channelModelPriceIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelModelPriceIDNEQ { get; set; }

    [GraphQLName("channelModelPriceIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ChannelModelPriceIDIn { get; set; }

    [GraphQLName("channelModelPriceIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ChannelModelPriceIDNotIn { get; set; }

    public ChannelModelPriceVersionStatusValue? Status { get; set; }

    public ChannelModelPriceVersionStatusValue? StatusNEQ { get; set; }

    public IReadOnlyList<ChannelModelPriceVersionStatusValue>? StatusIn { get; set; }

    public IReadOnlyList<ChannelModelPriceVersionStatusValue>? StatusNotIn { get; set; }

    [GraphQLName("effectiveStartAt")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? EffectiveStartAt { get; set; }

    [GraphQLName("effectiveStartAtNEQ")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? EffectiveStartAtNEQ { get; set; }

    [GraphQLName("effectiveStartAtIn")]
    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? EffectiveStartAtIn { get; set; }

    [GraphQLName("effectiveStartAtNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? EffectiveStartAtNotIn { get; set; }

    [GraphQLName("effectiveStartAtGT")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? EffectiveStartAtGT { get; set; }

    [GraphQLName("effectiveStartAtGTE")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? EffectiveStartAtGTE { get; set; }

    [GraphQLName("effectiveStartAtLT")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? EffectiveStartAtLT { get; set; }

    [GraphQLName("effectiveStartAtLTE")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? EffectiveStartAtLTE { get; set; }

    [GraphQLName("effectiveEndAt")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? EffectiveEndAt { get; set; }

    [GraphQLName("effectiveEndAtNEQ")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? EffectiveEndAtNEQ { get; set; }

    [GraphQLName("effectiveEndAtIn")]
    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? EffectiveEndAtIn { get; set; }

    [GraphQLName("effectiveEndAtNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? EffectiveEndAtNotIn { get; set; }

    [GraphQLName("effectiveEndAtGT")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? EffectiveEndAtGT { get; set; }

    [GraphQLName("effectiveEndAtGTE")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? EffectiveEndAtGTE { get; set; }

    [GraphQLName("effectiveEndAtLT")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? EffectiveEndAtLT { get; set; }

    [GraphQLName("effectiveEndAtLTE")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? EffectiveEndAtLTE { get; set; }

    [GraphQLName("effectiveEndAtIsNil")]
    public bool? EffectiveEndAtIsNil { get; set; }

    [GraphQLName("effectiveEndAtNotNil")]
    public bool? EffectiveEndAtNotNil { get; set; }

    [GraphQLName("referenceID")]
    public string? ReferenceID { get; set; }

    [GraphQLName("referenceIDNEQ")]
    public string? ReferenceIDNEQ { get; set; }

    [GraphQLName("referenceIDIn")]
    public IReadOnlyList<string>? ReferenceIDIn { get; set; }

    [GraphQLName("referenceIDNotIn")]
    public IReadOnlyList<string>? ReferenceIDNotIn { get; set; }

    [GraphQLName("referenceIDGT")]
    public string? ReferenceIDGT { get; set; }

    [GraphQLName("referenceIDGTE")]
    public string? ReferenceIDGTE { get; set; }

    [GraphQLName("referenceIDLT")]
    public string? ReferenceIDLT { get; set; }

    [GraphQLName("referenceIDLTE")]
    public string? ReferenceIDLTE { get; set; }

    [GraphQLName("referenceIDContains")]
    public string? ReferenceIDContains { get; set; }

    [GraphQLName("referenceIDHasPrefix")]
    public string? ReferenceIDHasPrefix { get; set; }

    [GraphQLName("referenceIDHasSuffix")]
    public string? ReferenceIDHasSuffix { get; set; }

    [GraphQLName("referenceIDEqualFold")]
    public string? ReferenceIDEqualFold { get; set; }

    [GraphQLName("referenceIDContainsFold")]
    public string? ReferenceIDContainsFold { get; set; }

    public bool? HasChannelModelPrice { get; set; }

    public IReadOnlyList<ChannelModelPriceWhereInput>? HasChannelModelPriceWith { get; set; }
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
            DbChannelId = record.ChannelId,
            Id = new GidValue("ChannelModelPrice", record.Id).ToString(),
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            ChannelID = new GidValue("Channel", record.ChannelId).ToString(),
            ModelID = record.ModelId,
            Price = ToGraphQL(ChannelModelPriceService.DeserializePrice(record.Price)),
            ReferenceID = record.ReferenceId,
        };
    }

    public static ChannelModelPriceVersionType ToGraphQL(
        ChannelModelPriceVersionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new ChannelModelPriceVersionType
        {
            DbId = record.Id,
            DbChannelModelPriceId = record.ChannelModelPriceId,
            Id = new GidValue("ChannelModelPriceVersion", record.Id).ToString(),
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            ChannelID = checked((int)record.ChannelId),
            ModelID = record.ModelId,
            ChannelModelPriceID = new GidValue(
                "ChannelModelPrice",
                record.ChannelModelPriceId).ToString(),
            Price = ToGraphQL(ChannelModelPriceService.DeserializePrice(record.Price)),
            Status = VersionStatusFromWire(record.Status),
            EffectiveStartAt = record.EffectiveStartAt,
            EffectiveEndAt = record.EffectiveEndAt,
            ReferenceID = record.ReferenceId,
        };
    }

    public static ChannelModelPriceListFilter? ToFilter(ChannelModelPriceWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new ChannelModelPriceListFilter
        {
            Id = where.Id is null ? null : GidValue.Parse(where.Id).Id,
            IdNEQ = where.IdNEQ is null ? null : GidValue.Parse(where.IdNEQ).Id,
            IdIn = where.IdIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            IdNotIn = where.IdNotIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            IdGT = where.IdGT is null ? null : GidValue.Parse(where.IdGT).Id,
            IdGTE = where.IdGTE is null ? null : GidValue.Parse(where.IdGTE).Id,
            IdLT = where.IdLT is null ? null : GidValue.Parse(where.IdLT).Id,
            IdLTE = where.IdLTE is null ? null : GidValue.Parse(where.IdLTE).Id,
            CreatedAt = where.CreatedAt,
            CreatedAtNEQ = where.CreatedAtNEQ,
            CreatedAtIn = where.CreatedAtIn,
            CreatedAtNotIn = where.CreatedAtNotIn,
            CreatedAtGT = where.CreatedAtGT,
            CreatedAtGTE = where.CreatedAtGTE,
            CreatedAtLT = where.CreatedAtLT,
            CreatedAtLTE = where.CreatedAtLTE,
            UpdatedAt = where.UpdatedAt,
            UpdatedAtNEQ = where.UpdatedAtNEQ,
            UpdatedAtIn = where.UpdatedAtIn,
            UpdatedAtNotIn = where.UpdatedAtNotIn,
            UpdatedAtGT = where.UpdatedAtGT,
            UpdatedAtGTE = where.UpdatedAtGTE,
            UpdatedAtLT = where.UpdatedAtLT,
            UpdatedAtLTE = where.UpdatedAtLTE,
            ChannelId = where.ChannelID is null ? null : GidValue.Parse(where.ChannelID).Id,
            ChannelIdNEQ = where.ChannelIDNEQ is null ? null : GidValue.Parse(where.ChannelIDNEQ).Id,
            ChannelIdIn = where.ChannelIDIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            ChannelIdNotIn = where.ChannelIDNotIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            ModelId = where.ModelID,
            ModelIdNEQ = where.ModelIDNEQ,
            ModelIdIn = where.ModelIDIn,
            ModelIdNotIn = where.ModelIDNotIn,
            ModelIdGT = where.ModelIDGT,
            ModelIdGTE = where.ModelIDGTE,
            ModelIdLT = where.ModelIDLT,
            ModelIdLTE = where.ModelIDLTE,
            ModelIdContains = where.ModelIDContains,
            ModelIdHasPrefix = where.ModelIDHasPrefix,
            ModelIdHasSuffix = where.ModelIDHasSuffix,
            ModelIdEqualFold = where.ModelIDEqualFold,
            ModelIdContainsFold = where.ModelIDContainsFold,
            ReferenceId = where.ReferenceID,
            ReferenceIdNEQ = where.ReferenceIDNEQ,
            ReferenceIdIn = where.ReferenceIDIn,
            ReferenceIdNotIn = where.ReferenceIDNotIn,
            ReferenceIdGT = where.ReferenceIDGT,
            ReferenceIdGTE = where.ReferenceIDGTE,
            ReferenceIdLT = where.ReferenceIDLT,
            ReferenceIdLTE = where.ReferenceIDLTE,
            ReferenceIdContains = where.ReferenceIDContains,
            ReferenceIdHasPrefix = where.ReferenceIDHasPrefix,
            ReferenceIdHasSuffix = where.ReferenceIDHasSuffix,
            ReferenceIdEqualFold = where.ReferenceIDEqualFold,
            ReferenceIdContainsFold = where.ReferenceIDContainsFold,
            HasChannel = where.HasChannel,
            HasChannelWith = where.HasChannelWith?
                .Select(static channel => ChannelMapping.ToFilter(channel))
                .OfType<ChannelListFilter>()
                .ToList(),
            HasVersions = where.HasVersions,
            HasVersionsWith = where.HasVersionsWith?
                .Select(ToFilter)
                .OfType<ChannelModelPriceVersionListFilter>()
                .ToList(),
            Not = ToFilter(where.Not),
            And = where.And?.Select(ToFilter).OfType<ChannelModelPriceListFilter>().ToList(),
            Or = where.Or?.Select(ToFilter).OfType<ChannelModelPriceListFilter>().ToList(),
        };
    }

    public static ChannelModelPriceVersionListFilter? ToFilter(
        ChannelModelPriceVersionWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new ChannelModelPriceVersionListFilter
        {
            Id = where.Id is null ? null : GidValue.Parse(where.Id).Id,
            IdNEQ = where.IdNEQ is null ? null : GidValue.Parse(where.IdNEQ).Id,
            IdIn = where.IdIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            IdNotIn = where.IdNotIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            IdGT = where.IdGT is null ? null : GidValue.Parse(where.IdGT).Id,
            IdGTE = where.IdGTE is null ? null : GidValue.Parse(where.IdGTE).Id,
            IdLT = where.IdLT is null ? null : GidValue.Parse(where.IdLT).Id,
            IdLTE = where.IdLTE is null ? null : GidValue.Parse(where.IdLTE).Id,
            CreatedAt = where.CreatedAt,
            CreatedAtNEQ = where.CreatedAtNEQ,
            CreatedAtIn = where.CreatedAtIn,
            CreatedAtNotIn = where.CreatedAtNotIn,
            CreatedAtGT = where.CreatedAtGT,
            CreatedAtGTE = where.CreatedAtGTE,
            CreatedAtLT = where.CreatedAtLT,
            CreatedAtLTE = where.CreatedAtLTE,
            UpdatedAt = where.UpdatedAt,
            UpdatedAtNEQ = where.UpdatedAtNEQ,
            UpdatedAtIn = where.UpdatedAtIn,
            UpdatedAtNotIn = where.UpdatedAtNotIn,
            UpdatedAtGT = where.UpdatedAtGT,
            UpdatedAtGTE = where.UpdatedAtGTE,
            UpdatedAtLT = where.UpdatedAtLT,
            UpdatedAtLTE = where.UpdatedAtLTE,
            ChannelId = where.ChannelID,
            ChannelIdNEQ = where.ChannelIDNEQ,
            ChannelIdIn = where.ChannelIDIn?.Select(static value => (long)value).ToList(),
            ChannelIdNotIn = where.ChannelIDNotIn?.Select(static value => (long)value).ToList(),
            ChannelIdGT = where.ChannelIDGT,
            ChannelIdGTE = where.ChannelIDGTE,
            ChannelIdLT = where.ChannelIDLT,
            ChannelIdLTE = where.ChannelIDLTE,
            ModelId = where.ModelID,
            ModelIdNEQ = where.ModelIDNEQ,
            ModelIdIn = where.ModelIDIn,
            ModelIdNotIn = where.ModelIDNotIn,
            ModelIdGT = where.ModelIDGT,
            ModelIdGTE = where.ModelIDGTE,
            ModelIdLT = where.ModelIDLT,
            ModelIdLTE = where.ModelIDLTE,
            ModelIdContains = where.ModelIDContains,
            ModelIdHasPrefix = where.ModelIDHasPrefix,
            ModelIdHasSuffix = where.ModelIDHasSuffix,
            ModelIdEqualFold = where.ModelIDEqualFold,
            ModelIdContainsFold = where.ModelIDContainsFold,
            ChannelModelPriceId = where.ChannelModelPriceID is null
                ? null
                : GidValue.Parse(where.ChannelModelPriceID).Id,
            ChannelModelPriceIdNEQ = where.ChannelModelPriceIDNEQ is null
                ? null
                : GidValue.Parse(where.ChannelModelPriceIDNEQ).Id,
            ChannelModelPriceIdIn = where.ChannelModelPriceIDIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            ChannelModelPriceIdNotIn = where.ChannelModelPriceIDNotIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            Status = where.Status is null ? null : ToWireVersionStatus(where.Status.Value),
            StatusNEQ = where.StatusNEQ is null ? null : ToWireVersionStatus(where.StatusNEQ.Value),
            StatusIn = where.StatusIn?
                .Select(static status => ToWireVersionStatus(status))
                .ToList(),
            StatusNotIn = where.StatusNotIn?
                .Select(static status => ToWireVersionStatus(status))
                .ToList(),
            EffectiveStartAt = where.EffectiveStartAt,
            EffectiveStartAtNEQ = where.EffectiveStartAtNEQ,
            EffectiveStartAtIn = where.EffectiveStartAtIn,
            EffectiveStartAtNotIn = where.EffectiveStartAtNotIn,
            EffectiveStartAtGT = where.EffectiveStartAtGT,
            EffectiveStartAtGTE = where.EffectiveStartAtGTE,
            EffectiveStartAtLT = where.EffectiveStartAtLT,
            EffectiveStartAtLTE = where.EffectiveStartAtLTE,
            EffectiveEndAt = where.EffectiveEndAt,
            EffectiveEndAtNEQ = where.EffectiveEndAtNEQ,
            EffectiveEndAtIn = where.EffectiveEndAtIn,
            EffectiveEndAtNotIn = where.EffectiveEndAtNotIn,
            EffectiveEndAtGT = where.EffectiveEndAtGT,
            EffectiveEndAtGTE = where.EffectiveEndAtGTE,
            EffectiveEndAtLT = where.EffectiveEndAtLT,
            EffectiveEndAtLTE = where.EffectiveEndAtLTE,
            EffectiveEndAtIsNil = where.EffectiveEndAtIsNil == true,
            EffectiveEndAtNotNil = where.EffectiveEndAtNotNil == true,
            ReferenceId = where.ReferenceID,
            ReferenceIdNEQ = where.ReferenceIDNEQ,
            ReferenceIdIn = where.ReferenceIDIn,
            ReferenceIdNotIn = where.ReferenceIDNotIn,
            ReferenceIdGT = where.ReferenceIDGT,
            ReferenceIdGTE = where.ReferenceIDGTE,
            ReferenceIdLT = where.ReferenceIDLT,
            ReferenceIdLTE = where.ReferenceIDLTE,
            ReferenceIdContains = where.ReferenceIDContains,
            ReferenceIdHasPrefix = where.ReferenceIDHasPrefix,
            ReferenceIdHasSuffix = where.ReferenceIDHasSuffix,
            ReferenceIdEqualFold = where.ReferenceIDEqualFold,
            ReferenceIdContainsFold = where.ReferenceIDContainsFold,
            HasChannelModelPrice = where.HasChannelModelPrice,
            HasChannelModelPriceWith = where.HasChannelModelPriceWith?
                .Select(ToFilter)
                .OfType<ChannelModelPriceListFilter>()
                .ToList(),
            Not = ToFilter(where.Not),
            And = where.And?.Select(ToFilter).OfType<ChannelModelPriceVersionListFilter>().ToList(),
            Or = where.Or?.Select(ToFilter).OfType<ChannelModelPriceVersionListFilter>().ToList(),
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

    /// <summary>
    /// Maps the stored version status to the GraphQL enum; an unknown stored
    /// value fails the field like gqlgen's enum marshaler does.
    /// </summary>
    private static ChannelModelPriceVersionStatusValue VersionStatusFromWire(string status)
        => status switch
        {
            "active" => ChannelModelPriceVersionStatusValue.Active,
            "archived" => ChannelModelPriceVersionStatusValue.Archived,
            _ => throw new InvalidOperationException(
                "Enum \"ChannelModelPriceVersionStatus\" cannot represent value: \""
                + status + "\""),
        };

    private static string ToWireVersionStatus(ChannelModelPriceVersionStatusValue status)
        => status switch
        {
            ChannelModelPriceVersionStatusValue.Active => "active",
            _ => "archived",
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
