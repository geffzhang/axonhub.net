using System.Text.Json;
using AxonHub.Api.Billing;
using AxonHub.Api.Management;
using AxonHub.Api.Traces;
using AxonHub.Persistence;
using HotChocolate;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the RequestExecutionStatus enum (ent.graphql:4763-4776).</summary>
[GraphQLName("RequestExecutionStatus")]
public enum RequestExecutionStatusValue
{
    [GraphQLName("pending")]
    Pending,

    [GraphQLName("processing")]
    Processing,

    [GraphQLName("completed")]
    Completed,

    [GraphQLName("failed")]
    Failed,

    [GraphQLName("canceled")]
    Canceled,
}

/// <summary>Port of the UsageLogSource enum (ent.graphql:6726-6730).</summary>
[GraphQLName("UsageLogSource")]
public enum UsageLogSourceValue
{
    [GraphQLName("api")]
    Api,

    [GraphQLName("playground")]
    Playground,

    [GraphQLName("test")]
    Test,
}

/// <summary>Port of RequestExecutionOrderField (ent.graphql:4742-4749).</summary>
[GraphQLName("RequestExecutionOrderField")]
public enum RequestExecutionOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,
}

/// <summary>Port of UsageLogOrderField (ent.graphql:6704-6711).</summary>
[GraphQLName("UsageLogOrderField")]
public enum UsageLogOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,
}

/// <summary>Port of RequestExecutionOrder (ent.graphql:4729-4740).</summary>
[GraphQLName("RequestExecutionOrder")]
[InputObjectType("RequestExecutionOrder")]
public sealed class RequestExecutionOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    public RequestExecutionOrderFieldValue Field { get; set; }
}

/// <summary>Port of UsageLogOrder (ent.graphql:6691-6702).</summary>
[GraphQLName("UsageLogOrder")]
[InputObjectType("UsageLogOrder")]
public sealed class UsageLogOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    public UsageLogOrderFieldValue Field { get; set; }
}

/// <summary>
/// Port of <c>RequestExecutionWhereInput</c> (ent.graphql:4774-5019). The
/// edge predicates and the string columns without a persistence filter are not
/// ported in this slice (C192).
/// </summary>
[GraphQLName("RequestExecutionWhereInput")]
public sealed class RequestExecutionWhereInput
{
    public RequestExecutionWhereInput? Not { get; set; }

    public IReadOnlyList<RequestExecutionWhereInput>? And { get; set; }

    public IReadOnlyList<RequestExecutionWhereInput>? Or { get; set; }

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

    [GraphQLName("projectID")]
    public int? ProjectId { get; set; }

    [GraphQLName("projectIDNEQ")]
    public int? ProjectIdNEQ { get; set; }

    [GraphQLName("projectIDIn")]
    public IReadOnlyList<int>? ProjectIdIn { get; set; }

    [GraphQLName("projectIDNotIn")]
    public IReadOnlyList<int>? ProjectIdNotIn { get; set; }

    [GraphQLName("projectIDGT")]
    public int? ProjectIdGT { get; set; }

    [GraphQLName("projectIDGTE")]
    public int? ProjectIdGTE { get; set; }

    [GraphQLName("projectIDLT")]
    public int? ProjectIdLT { get; set; }

    [GraphQLName("projectIDLTE")]
    public int? ProjectIdLTE { get; set; }

    [GraphQLName("requestID")]
    [GraphQLType(typeof(IdType))]
    public string? RequestId { get; set; }

    [GraphQLName("requestIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? RequestIdNEQ { get; set; }

    [GraphQLName("requestIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? RequestIdIn { get; set; }

    [GraphQLName("requestIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? RequestIdNotIn { get; set; }

    [GraphQLName("channelID")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelId { get; set; }

    [GraphQLName("channelIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelIdNEQ { get; set; }

    [GraphQLName("channelIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ChannelIdIn { get; set; }

    [GraphQLName("channelIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ChannelIdNotIn { get; set; }

    [GraphQLName("channelIDIsNil")]
    public bool? ChannelIdIsNil { get; set; }

    [GraphQLName("channelIDNotNil")]
    public bool? ChannelIdNotNil { get; set; }

    [GraphQLName("dataStorageID")]
    [GraphQLType(typeof(IdType))]
    public string? DataStorageId { get; set; }

    [GraphQLName("dataStorageIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? DataStorageIdNEQ { get; set; }

    [GraphQLName("dataStorageIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? DataStorageIdIn { get; set; }

    [GraphQLName("dataStorageIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? DataStorageIdNotIn { get; set; }

    [GraphQLName("dataStorageIDIsNil")]
    public bool? DataStorageIdIsNil { get; set; }

    [GraphQLName("dataStorageIDNotNil")]
    public bool? DataStorageIdNotNil { get; set; }

    public RequestExecutionStatusValue? Status { get; set; }

    public RequestExecutionStatusValue? StatusNEQ { get; set; }

    public IReadOnlyList<RequestExecutionStatusValue>? StatusIn { get; set; }

    public IReadOnlyList<RequestExecutionStatusValue>? StatusNotIn { get; set; }

    [GraphQLName("modelID")]
    public string? ModelId { get; set; }

    [GraphQLName("modelIDNEQ")]
    public string? ModelIdNEQ { get; set; }

    [GraphQLName("modelIDIn")]
    public IReadOnlyList<string>? ModelIdIn { get; set; }

    [GraphQLName("modelIDNotIn")]
    public IReadOnlyList<string>? ModelIdNotIn { get; set; }

    [GraphQLName("modelIDContains")]
    public string? ModelIdContains { get; set; }

    [GraphQLName("modelIDContainsFold")]
    public string? ModelIdContainsFold { get; set; }

    [GraphQLName("modelIDHasPrefix")]
    public string? ModelIdHasPrefix { get; set; }

    [GraphQLName("modelIDHasSuffix")]
    public string? ModelIdHasSuffix { get; set; }

    [GraphQLName("modelIDEqualFold")]
    public string? ModelIdEqualFold { get; set; }

    public string? Format { get; set; }

    public string? FormatNEQ { get; set; }

    public IReadOnlyList<string>? FormatIn { get; set; }

    public IReadOnlyList<string>? FormatNotIn { get; set; }

    [GraphQLName("externalID")]
    public string? ExternalId { get; set; }

    [GraphQLName("externalIDNEQ")]
    public string? ExternalIdNEQ { get; set; }

    [GraphQLName("externalIDIn")]
    public IReadOnlyList<string>? ExternalIdIn { get; set; }

    [GraphQLName("externalIDNotIn")]
    public IReadOnlyList<string>? ExternalIdNotIn { get; set; }
}

/// <summary>
/// Port of <c>UsageLogWhereInput</c> (ent.graphql:6733-...). The request edge
/// predicates and the remaining scalar columns are not ported in this slice
/// (C192).
/// </summary>
[GraphQLName("UsageLogWhereInput")]
public sealed class UsageLogWhereInput
{
    public UsageLogWhereInput? Not { get; set; }

    public IReadOnlyList<UsageLogWhereInput>? And { get; set; }

    public IReadOnlyList<UsageLogWhereInput>? Or { get; set; }

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

    [GraphQLName("requestID")]
    [GraphQLType(typeof(IdType))]
    public string? RequestId { get; set; }

    [GraphQLName("requestIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? RequestIdNEQ { get; set; }

    [GraphQLName("requestIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? RequestIdIn { get; set; }

    [GraphQLName("requestIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? RequestIdNotIn { get; set; }

    [GraphQLName("apiKeyID")]
    public int? ApiKeyId { get; set; }

    [GraphQLName("apiKeyIDNEQ")]
    public int? ApiKeyIdNEQ { get; set; }

    [GraphQLName("apiKeyIDIn")]
    public IReadOnlyList<int>? ApiKeyIdIn { get; set; }

    [GraphQLName("apiKeyIDNotIn")]
    public IReadOnlyList<int>? ApiKeyIdNotIn { get; set; }

    [GraphQLName("apiKeyIDIsNil")]
    public bool? ApiKeyIdIsNil { get; set; }

    [GraphQLName("apiKeyIDNotNil")]
    public bool? ApiKeyIdNotNil { get; set; }

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string? ProjectId { get; set; }

    [GraphQLName("projectIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? ProjectIdNEQ { get; set; }

    [GraphQLName("projectIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ProjectIdIn { get; set; }

    [GraphQLName("projectIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ProjectIdNotIn { get; set; }

    [GraphQLName("channelID")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelId { get; set; }

    [GraphQLName("channelIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelIdNEQ { get; set; }

    [GraphQLName("channelIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ChannelIdIn { get; set; }

    [GraphQLName("channelIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ChannelIdNotIn { get; set; }

    [GraphQLName("channelIDIsNil")]
    public bool? ChannelIdIsNil { get; set; }

    [GraphQLName("channelIDNotNil")]
    public bool? ChannelIdNotNil { get; set; }

    public UsageLogSourceValue? Source { get; set; }

    public UsageLogSourceValue? SourceNEQ { get; set; }

    public IReadOnlyList<UsageLogSourceValue>? SourceIn { get; set; }

    public IReadOnlyList<UsageLogSourceValue>? SourceNotIn { get; set; }

    [GraphQLName("modelID")]
    public string? ModelId { get; set; }

    [GraphQLName("modelIDNEQ")]
    public string? ModelIdNEQ { get; set; }

    [GraphQLName("modelIDIn")]
    public IReadOnlyList<string>? ModelIdIn { get; set; }

    [GraphQLName("modelIDNotIn")]
    public IReadOnlyList<string>? ModelIdNotIn { get; set; }

    [GraphQLName("modelIDContains")]
    public string? ModelIdContains { get; set; }

    [GraphQLName("modelIDContainsFold")]
    public string? ModelIdContainsFold { get; set; }

    public string? Format { get; set; }

    public string? FormatNEQ { get; set; }

    public IReadOnlyList<string>? FormatIn { get; set; }

    public IReadOnlyList<string>? FormatNotIn { get; set; }
}

/// <summary>
/// The remaining scalar predicates of <c>RequestWhereInput</c>
/// (ent.graphql:5096-...): the APIKey/trace/data-storage/channel edges, source,
/// model, reasoning effort, format and external id. The <c>clientIP</c>,
/// <c>userAgent</c>, <c>stream</c> and edge predicates are not ported in this
/// slice (C192).
/// </summary>
public sealed partial class RequestWhereInput
{
    [GraphQLName("apiKeyID")]
    [GraphQLType(typeof(IdType))]
    public string? ApiKeyId { get; set; }

    [GraphQLName("apiKeyIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? ApiKeyIdNEQ { get; set; }

    [GraphQLName("apiKeyIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ApiKeyIdIn { get; set; }

    [GraphQLName("apiKeyIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ApiKeyIdNotIn { get; set; }

    [GraphQLName("apiKeyIDIsNil")]
    public bool? ApiKeyIdIsNil { get; set; }

    [GraphQLName("apiKeyIDNotNil")]
    public bool? ApiKeyIdNotNil { get; set; }

    [GraphQLName("traceID")]
    [GraphQLType(typeof(IdType))]
    public string? TraceId { get; set; }

    [GraphQLName("traceIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? TraceIdNEQ { get; set; }

    [GraphQLName("traceIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? TraceIdIn { get; set; }

    [GraphQLName("traceIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? TraceIdNotIn { get; set; }

    [GraphQLName("traceIDIsNil")]
    public bool? TraceIdIsNil { get; set; }

    [GraphQLName("traceIDNotNil")]
    public bool? TraceIdNotNil { get; set; }

    [GraphQLName("dataStorageID")]
    [GraphQLType(typeof(IdType))]
    public string? DataStorageId { get; set; }

    [GraphQLName("dataStorageIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? DataStorageIdNEQ { get; set; }

    [GraphQLName("dataStorageIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? DataStorageIdIn { get; set; }

    [GraphQLName("dataStorageIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? DataStorageIdNotIn { get; set; }

    [GraphQLName("dataStorageIDIsNil")]
    public bool? DataStorageIdIsNil { get; set; }

    [GraphQLName("dataStorageIDNotNil")]
    public bool? DataStorageIdNotNil { get; set; }

    public RequestSourceValue? Source { get; set; }

    public RequestSourceValue? SourceNEQ { get; set; }

    public IReadOnlyList<RequestSourceValue>? SourceIn { get; set; }

    public IReadOnlyList<RequestSourceValue>? SourceNotIn { get; set; }

    [GraphQLName("modelID")]
    public string? ModelId { get; set; }

    [GraphQLName("modelIDNEQ")]
    public string? ModelIdNEQ { get; set; }

    [GraphQLName("modelIDIn")]
    public IReadOnlyList<string>? ModelIdIn { get; set; }

    [GraphQLName("modelIDNotIn")]
    public IReadOnlyList<string>? ModelIdNotIn { get; set; }

    [GraphQLName("modelIDGT")]
    public string? ModelIdGT { get; set; }

    [GraphQLName("modelIDGTE")]
    public string? ModelIdGTE { get; set; }

    [GraphQLName("modelIDLT")]
    public string? ModelIdLT { get; set; }

    [GraphQLName("modelIDLTE")]
    public string? ModelIdLTE { get; set; }

    [GraphQLName("modelIDContains")]
    public string? ModelIdContains { get; set; }

    [GraphQLName("modelIDHasPrefix")]
    public string? ModelIdHasPrefix { get; set; }

    [GraphQLName("modelIDHasSuffix")]
    public string? ModelIdHasSuffix { get; set; }

    [GraphQLName("modelIDEqualFold")]
    public string? ModelIdEqualFold { get; set; }

    [GraphQLName("modelIDContainsFold")]
    public string? ModelIdContainsFold { get; set; }

    public string? ReasoningEffort { get; set; }

    public string? ReasoningEffortNEQ { get; set; }

    public IReadOnlyList<string>? ReasoningEffortIn { get; set; }

    public IReadOnlyList<string>? ReasoningEffortNotIn { get; set; }

    public string? ReasoningEffortGT { get; set; }

    public string? ReasoningEffortGTE { get; set; }

    public string? ReasoningEffortLT { get; set; }

    public string? ReasoningEffortLTE { get; set; }

    public string? ReasoningEffortContains { get; set; }

    public string? ReasoningEffortHasPrefix { get; set; }

    public string? ReasoningEffortHasSuffix { get; set; }

    public bool? ReasoningEffortIsNil { get; set; }

    public bool? ReasoningEffortNotNil { get; set; }

    public string? ReasoningEffortEqualFold { get; set; }

    public string? ReasoningEffortContainsFold { get; set; }

    public string? Format { get; set; }

    public string? FormatNEQ { get; set; }

    public IReadOnlyList<string>? FormatIn { get; set; }

    public IReadOnlyList<string>? FormatNotIn { get; set; }

    public string? FormatGT { get; set; }

    public string? FormatGTE { get; set; }

    public string? FormatLT { get; set; }

    public string? FormatLTE { get; set; }

    public string? FormatContains { get; set; }

    public string? FormatHasPrefix { get; set; }

    public string? FormatHasSuffix { get; set; }

    public string? FormatEqualFold { get; set; }

    public string? FormatContainsFold { get; set; }

    [GraphQLName("channelID")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelId { get; set; }

    [GraphQLName("channelIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelIdNEQ { get; set; }

    [GraphQLName("channelIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ChannelIdIn { get; set; }

    [GraphQLName("channelIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ChannelIdNotIn { get; set; }

    [GraphQLName("channelIDIsNil")]
    public bool? ChannelIdIsNil { get; set; }

    [GraphQLName("channelIDNotNil")]
    public bool? ChannelIdNotNil { get; set; }

    [GraphQLName("externalID")]
    public string? ExternalId { get; set; }

    [GraphQLName("externalIDNEQ")]
    public string? ExternalIdNEQ { get; set; }

    [GraphQLName("externalIDIn")]
    public IReadOnlyList<string>? ExternalIdIn { get; set; }

    [GraphQLName("externalIDNotIn")]
    public IReadOnlyList<string>? ExternalIdNotIn { get; set; }

    [GraphQLName("externalIDGT")]
    public string? ExternalIdGT { get; set; }

    [GraphQLName("externalIDGTE")]
    public string? ExternalIdGTE { get; set; }

    [GraphQLName("externalIDLT")]
    public string? ExternalIdLT { get; set; }

    [GraphQLName("externalIDLTE")]
    public string? ExternalIdLTE { get; set; }

    [GraphQLName("externalIDContains")]
    public string? ExternalIdContains { get; set; }

    [GraphQLName("externalIDHasPrefix")]
    public string? ExternalIdHasPrefix { get; set; }

    [GraphQLName("externalIDHasSuffix")]
    public string? ExternalIdHasSuffix { get; set; }

    [GraphQLName("externalIDIsNil")]
    public bool? ExternalIdIsNil { get; set; }

    [GraphQLName("externalIDNotNil")]
    public bool? ExternalIdNotNil { get; set; }

    [GraphQLName("externalIDEqualFold")]
    public string? ExternalIdEqualFold { get; set; }

    [GraphQLName("externalIDContainsFold")]
    public string? ExternalIdContainsFold { get; set; }
}

/// <summary>Port of TierCost (cost.graphql:1-5).</summary>
[GraphQLName("TierCost")]
public sealed class TierCostType
{
    public int? UpTo { get; init; }

    public int Units { get; init; }

    [GraphQLType(typeof(GoDecimalType))]
    public decimal Subtotal { get; init; }
}

/// <summary>Port of CostItem (cost.graphql:7-12).</summary>
[GraphQLName("CostItem")]
public sealed class CostItemType
{
    public PriceItemCodeValue ItemCode { get; init; }

    public int Quantity { get; init; }

    public IReadOnlyList<TierCostType>? TierBreakdown { get; init; }

    [GraphQLType(typeof(GoDecimalType))]
    public decimal Subtotal { get; init; }
}

/// <summary>Port of the <c>RequestExecution</c> type (ent.graphql:4641-4711).</summary>
[GraphQLName("RequestExecution")]
public sealed class RequestExecutionType
{
    [GraphQLIgnore]
    public RequestExecutionRecord Record { get; init; } = null!;

    [GraphQLType(typeof(IdType))]
    public string Id => new GidValue("RequestExecution", Record.Id).ToString();

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt => Record.CreatedAt;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt => Record.UpdatedAt;

    [GraphQLName("projectID")]
    public int ProjectID => checked((int)Record.ProjectId);

    [GraphQLName("requestID")]
    [GraphQLType(typeof(IdType))]
    public string RequestID => new GidValue("Request", Record.RequestId).ToString();

    /// <summary>The column is an optional int, so this always serializes (C190).</summary>
    [GraphQLName("channelID")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelID => new GidValue("Channel", Record.ChannelId ?? 0).ToString();

    /// <summary>
    /// Masked unless the caller may read the channel
    /// (ent.resolvers.go:702-720).
    /// </summary>
    [GraphQLName("channelAPIKeyIndex")]
    public async Task<int?> GetChannelAPIKeyIndexAsync(
        [Service] RequestQueryService requests,
        CancellationToken cancellationToken)
    {
        if (Record.ChannelApiKeyIndex is null || (Record.ChannelId ?? 0) == 0)
        {
            return null;
        }

        var channel = await requests.GetVisibleChannelOrNullAsync(
                Record.ChannelId,
                cancellationToken)
            .ConfigureAwait(false);
        return channel is null ? null : checked((int)Record.ChannelApiKeyIndex.Value);
    }

    [GraphQLName("dataStorageID")]
    [GraphQLType(typeof(IdType))]
    public string? DataStorageID => Record.DataStorageId is { } dataStorageId
        && dataStorageId != 0
            ? new GidValue("DataStorage", dataStorageId).ToString()
            : null;

    [GraphQLName("externalID")]
    public string? ExternalID => Record.ExternalId;

    [GraphQLName("modelID")]
    public string ModelID => Record.ModelId;

    [GraphQLName("upstreamModelID")]
    public string? UpstreamModelID => Record.UpstreamModelId;

    public string Format => Record.Format;

    public string? ReasoningEffort => Record.ReasoningEffort;

    /// <summary>Masked unless the caller may read the channel (ent.resolvers.go:765-780).</summary>
    [GraphQLName("channelAPIKeySuffix")]
    public async Task<string?> GetChannelAPIKeySuffixAsync(
        [Service] RequestQueryService requests,
        CancellationToken cancellationToken)
    {
        if (Record.ChannelApiKeySuffix is null || (Record.ChannelId ?? 0) == 0)
        {
            return null;
        }

        var channel = await requests.GetVisibleChannelOrNullAsync(
                Record.ChannelId,
                cancellationToken)
            .ConfigureAwait(false);
        return channel is null ? null : Record.ChannelApiKeySuffix;
    }

    [GraphQLName("requestBody")]
    [GraphQLType(typeof(JsonRawMessageType))]
    public JsonElement RequestBody
        => RequestMapping.ParseJson(TraceSegmentService.LoadRequestBody(Record));

    [GraphQLName("responseHeaders")]
    [GraphQLType(typeof(JsonRawMessageType))]
    public JsonElement? ResponseHeaders => RequestMapping.ParseJsonOrNull(Record.ResponseHeaders);

    [GraphQLName("responseBody")]
    [GraphQLType(typeof(JsonRawMessageType))]
    public JsonElement ResponseBody
        => RequestMapping.ParseJson(TraceSegmentService.LoadResponseBody(Record));

    [GraphQLName("responseChunks")]
    [GraphQLType(typeof(ListType<NonNullType<JsonRawMessageType>>))]
    public IReadOnlyList<JsonElement> ResponseChunks
        => RequestMapping.LoadResponseChunks(
            Record.ResponseChunks,
            Record.Stream,
            Record.Status);

    [GraphQLName("errorMessage")]
    public string? ErrorMessage => Record.ErrorMessage;

    [GraphQLName("responseStatusCode")]
    public int? ResponseStatusCode => RequestMapping.ToGraphQLInt(Record.ResponseStatusCode);

    public RequestExecutionStatusValue Status
        => RequestMapping.ToGraphQLExecutionStatus(Record.Status);

    public bool Stream => Record.Stream;

    public int? MetricsLatencyMs => RequestMapping.ToGraphQLInt(Record.MetricsLatencyMs);

    public int? MetricsFirstTokenLatencyMs
        => RequestMapping.ToGraphQLInt(Record.MetricsFirstTokenLatencyMs);

    public int? MetricsReasoningDurationMs
        => RequestMapping.ToGraphQLInt(Record.MetricsReasoningDurationMs);

    [GraphQLName("requestHeaders")]
    [GraphQLType(typeof(JsonRawMessageType))]
    public JsonElement? RequestHeaders => RequestMapping.ParseJsonOrNull(Record.RequestHeaders);

    [GraphQLName("requestURL")]
    public string? RequestURL => Record.RequestUrl;

    [GraphQLName("passThroughApplied")]
    public bool PassThroughApplied => Record.PassThroughApplied;

    /// <summary>Port of the entgql Request edge (gql_edge.go:553-559).</summary>
    [GraphQLName("request")]
    public async Task<RequestType> GetRequestAsync(
        [Service] RequestQueryService requests,
        CancellationToken cancellationToken)
    {
        var request = await requests.GetNodeAsync(Record.RequestId, cancellationToken)
            .ConfigureAwait(false);
        return TraceMapping.ToGraphQL(request);
    }

    /// <summary>Port of getNilableChannel (ent.resolvers.go:816-819).</summary>
    [GraphQLName("channel")]
    public async Task<ChannelType?> GetChannelAsync(
        [Service] RequestQueryService requests,
        CancellationToken cancellationToken)
    {
        var channel = await requests.GetVisibleChannelOrNullAsync(
                Record.ChannelId,
                cancellationToken)
            .ConfigureAwait(false);
        return channel is null ? null : ChannelMapping.ToGraphQL(channel);
    }
}

/// <summary>Port of <c>RequestExecutionEdge</c> (ent.graphql:4713-4724).</summary>
[GraphQLName("RequestExecutionEdge")]
public sealed class RequestExecutionEdgeType
{
    public RequestExecutionType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of <c>RequestExecutionConnection</c> (ent.graphql:4696-4709).</summary>
[GraphQLName("RequestExecutionConnection")]
public sealed class RequestExecutionConnectionType
{
    public IReadOnlyList<RequestExecutionEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>Port of the <c>UsageLog</c> type (ent.graphql:6643-6718).</summary>
[GraphQLName("UsageLog")]
public sealed class UsageLogType
{
    [GraphQLIgnore]
    public UsageLogRecord Record { get; init; } = null!;

    [GraphQLType(typeof(IdType))]
    public string Id => new GidValue("UsageLog", Record.Id).ToString();

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt => Record.CreatedAt;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt => Record.UpdatedAt;

    [GraphQLName("requestID")]
    [GraphQLType(typeof(IdType))]
    public string RequestID => new GidValue("Request", Record.RequestId).ToString();

    /// <summary>The ent field is an optional int, so null stays null.</summary>
    [GraphQLName("apiKeyID")]
    public int? APIKeyID => Record.ApiKeyId is { } apiKeyId ? checked((int)apiKeyId) : null;

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string ProjectID => new GidValue("Project", Record.ProjectId).ToString();

    /// <summary>The resolver always returns a GUID and serializes Channel/0 (C190).</summary>
    [GraphQLName("channelID")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelID => new GidValue("Channel", Record.ChannelId ?? 0).ToString();

    [GraphQLName("modelID")]
    public string ModelID => Record.ModelId;

    public int PromptTokens => checked((int)Record.PromptTokens);

    public int CompletionTokens => checked((int)Record.CompletionTokens);

    public int TotalTokens => checked((int)Record.TotalTokens);

    public int? PromptAudioTokens => RequestMapping.ToGraphQLInt(Record.PromptAudioTokens);

    public int? PromptCachedTokens => RequestMapping.ToGraphQLInt(Record.PromptCachedTokens);

    public int? PromptWriteCachedTokens
        => RequestMapping.ToGraphQLInt(Record.PromptWriteCachedTokens);

    public int? PromptWriteCachedTokens5m
        => RequestMapping.ToGraphQLInt(Record.PromptWriteCachedTokens5m);

    public int? PromptWriteCachedTokens1h
        => RequestMapping.ToGraphQLInt(Record.PromptWriteCachedTokens1h);

    public int? CompletionAudioTokens
        => RequestMapping.ToGraphQLInt(Record.CompletionAudioTokens);

    public int? CompletionReasoningTokens
        => RequestMapping.ToGraphQLInt(Record.CompletionReasoningTokens);

    public int? CompletionAcceptedPredictionTokens
        => RequestMapping.ToGraphQLInt(Record.CompletionAcceptedPredictionTokens);

    public int? CompletionRejectedPredictionTokens
        => RequestMapping.ToGraphQLInt(Record.CompletionRejectedPredictionTokens);

    public UsageLogSourceValue Source => RequestMapping.ToGraphQLUsageLogSource(Record.Source);

    public string Format => Record.Format;

    public double? TotalCost => Record.TotalCost;

    [GraphQLName("costItems")]
    public IReadOnlyList<CostItemType>? CostItems => RequestMapping.ParseCostItems(Record.CostItems);

    [GraphQLName("costPriceReferenceID")]
    public string? CostPriceReferenceID => Record.CostPriceReferenceId;

    /// <summary>Port of the entgql Request edge (gql_edge.go:693-699).</summary>
    [GraphQLName("request")]
    public async Task<RequestType> GetRequestAsync(
        [Service] RequestQueryService requests,
        CancellationToken cancellationToken)
    {
        var request = await requests.GetNodeAsync(Record.RequestId, cancellationToken)
            .ConfigureAwait(false);
        return TraceMapping.ToGraphQL(request);
    }

    /// <summary>Port of the entgql Project edge (gql_edge.go:701-707).</summary>
    [GraphQLName("project")]
    public async Task<ProjectType> GetProjectAsync(
        [Service] RequestQueryService requests,
        CancellationToken cancellationToken)
    {
        var project = await requests.GetVisibleProjectAsync(Record.ProjectId, cancellationToken)
            .ConfigureAwait(false);
        return UserProfileMapping.ToGraphQL(project);
    }

    /// <summary>Port of getNilableChannel (ent.resolvers.go:930-933).</summary>
    [GraphQLName("channel")]
    public async Task<ChannelType?> GetChannelAsync(
        [Service] RequestQueryService requests,
        CancellationToken cancellationToken)
    {
        var channel = await requests.GetVisibleChannelOrNullAsync(
                Record.ChannelId,
                cancellationToken)
            .ConfigureAwait(false);
        return channel is null ? null : ChannelMapping.ToGraphQL(channel);
    }
}

/// <summary>Port of <c>UsageLogEdge</c> (ent.graphql:6720-6731).</summary>
[GraphQLName("UsageLogEdge")]
public sealed class UsageLogEdgeType
{
    public UsageLogType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of <c>UsageLogConnection</c> (ent.graphql:6766-6779).</summary>
[GraphQLName("UsageLogConnection")]
public sealed class UsageLogConnectionType
{
    public IReadOnlyList<UsageLogEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>
/// Port of the <c>Request</c> fields that belong to the requests slice: bodies,
/// channel/external IDs, content storage columns, edges and connections
/// (ent.graphql:4516-4606).
/// </summary>
public sealed partial class RequestType
{
    [GraphQLName("requestHeaders")]
    [GraphQLType(typeof(JsonRawMessageType))]
    public JsonElement? RequestHeaders => RequestMapping.ParseJsonOrNull(Record.RequestHeaders);

    /// <summary>Port of RequestService.LoadRequestBody (ent.resolvers.go:649-658).</summary>
    [GraphQLName("requestBody")]
    [GraphQLType(typeof(JsonRawMessageType))]
    public JsonElement RequestBody
        => RequestMapping.ParseJson(TraceSegmentService.LoadRequestBody(Record));

    [GraphQLName("responseHeaders")]
    [GraphQLType(typeof(JsonRawMessageType))]
    public JsonElement? ResponseHeaders => RequestMapping.ParseJsonOrNull(Record.ResponseHeaders);

    /// <summary>Port of RequestService.LoadResponseBody (ent.resolvers.go:660-671).</summary>
    [GraphQLName("responseBody")]
    [GraphQLType(typeof(JsonRawMessageType))]
    public JsonElement ResponseBody
        => RequestMapping.ParseJson(TraceSegmentService.LoadResponseBody(Record));

    /// <summary>Port of RequestService.LoadResponseChunks (ent.resolvers.go:673-687).</summary>
    [GraphQLName("responseChunks")]
    [GraphQLType(typeof(ListType<NonNullType<JsonRawMessageType>>))]
    public IReadOnlyList<JsonElement> ResponseChunks
        => RequestMapping.LoadResponseChunks(Record.ResponseChunks, Record.Stream, Record.Status);

    /// <summary>The resolver returns nil when the column is zero (ent.resolvers.go:689-699).</summary>
    [GraphQLName("channelID")]
    [GraphQLType(typeof(IdType))]
    public string? ChannelID => Record.ChannelId is { } channelId && channelId != 0
        ? new GidValue("Channel", channelId).ToString()
        : null;

    [GraphQLName("externalID")]
    public string? ExternalID => Record.ExternalId;

    public bool ContentSaved => Record.ContentSaved;

    [GraphQLName("contentStorageID")]
    public int? ContentStorageID => RequestMapping.ToGraphQLInt(Record.ContentStorageId);

    [GraphQLName("contentStorageKey")]
    public string? ContentStorageKey => Record.ContentStorageKey;

    [GraphQLName("contentSavedAt")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? ContentSavedAt => Record.ContentSavedAt;

    /// <summary>Port of the APIKey edge (gql_edge.go:471-477).</summary>
    [GraphQLName("apiKey")]
    public async Task<ApiKeyType?> GetAPIKeyAsync(
        [Service] RequestQueryService requests,
        CancellationToken cancellationToken)
    {
        var apiKey = await requests.GetVisibleApiKeyOrNullAsync(Record.ApiKeyId, cancellationToken)
            .ConfigureAwait(false);
        return apiKey is null ? null : ApiKeyMapping.ToGraphQL(apiKey);
    }

    /// <summary>Port of the Project edge (gql_edge.go:479-485).</summary>
    [GraphQLName("project")]
    public async Task<ProjectType> GetProjectAsync(
        [Service] RequestQueryService requests,
        CancellationToken cancellationToken)
    {
        var project = await requests.GetVisibleProjectAsync(Record.ProjectId, cancellationToken)
            .ConfigureAwait(false);
        return UserProfileMapping.ToGraphQL(project);
    }

    /// <summary>Port of the Trace edge (gql_edge.go:487-493): MaskNotFound.</summary>
    [GraphQLName("trace")]
    public async Task<TraceType?> GetTraceAsync(
        [Service] RequestQueryService requests,
        CancellationToken cancellationToken)
    {
        var trace = await requests.GetVisibleTraceOrNullAsync(Record.TraceId, cancellationToken)
            .ConfigureAwait(false);
        return trace is null ? null : TraceMapping.ToGraphQL(trace);
    }

    /// <summary>Port of the Request.executions connection (gql_edge.go:503-522).</summary>
    [GraphQLName("executions")]
    public async Task<RequestExecutionConnectionType> GetExecutionsAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        RequestExecutionOrder? orderBy,
        RequestExecutionWhereInput? where,
        [Service] RequestQueryService requests,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        var sortField = RequestMapping.ToEdgeExecutionSortField(orderBy);
        var page = await requests.ListExecutionsAsync(
                Record.Id,
                new RequestExecutionListQuery
                {
                    Where = RequestMapping.ToFilter(where),
                    SortField = sortField,
                    Direction = RequestMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return RequestMapping.BuildConnection(page, sortField);
    }

    /// <summary>Port of the Request.usageLogs connection (gql_edge.go:532-551).</summary>
    [GraphQLName("usageLogs")]
    public async Task<UsageLogConnectionType> GetUsageLogsAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        UsageLogOrder? orderBy,
        UsageLogWhereInput? where,
        [Service] RequestQueryService requests,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        var sortField = RequestMapping.ToEdgeUsageLogSortField(orderBy);
        var page = await requests.ListUsageLogsAsync(
                Record.Id,
                new UsageLogListQuery
                {
                    Where = RequestMapping.ToFilter(where),
                    SortField = sortField,
                    Direction = RequestMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return RequestMapping.BuildConnection(page, sortField);
    }
}

/// <summary>
/// Domain ↔ GraphQL mapping for the requests slice: ordering, filters,
/// connections, body/chunk loading and the execution/usage-log enums.
/// </summary>
public static class RequestMapping
{
    private const string ExternalResponseChunksMarker = "{\"_ext\":1}";

    public static RequestExecutionType ToGraphQL(RequestExecutionRecord record)
        => new() { Record = record };

    public static UsageLogType ToGraphQL(UsageLogRecord record) => new() { Record = record };

    public static RequestExecutionSortField ToEdgeExecutionSortField(
        RequestExecutionOrder? orderBy)
        => orderBy?.Field switch
        {
            RequestExecutionOrderFieldValue.UpdatedAt => RequestExecutionSortField.UpdatedAt,
            RequestExecutionOrderFieldValue.CreatedAt => RequestExecutionSortField.CreatedAt,
            _ => RequestExecutionSortField.Id,
        };

    public static UsageLogSortField ToEdgeUsageLogSortField(UsageLogOrder? orderBy)
        => orderBy?.Field switch
        {
            UsageLogOrderFieldValue.UpdatedAt => UsageLogSortField.UpdatedAt,
            UsageLogOrderFieldValue.CreatedAt => UsageLogSortField.CreatedAt,
            _ => UsageLogSortField.Id,
        };

    public static SortDirection ToSortDirection(RequestExecutionOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    public static SortDirection ToSortDirection(UsageLogOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    public static RequestExecutionConnectionType BuildConnection(
        ListPage<RequestExecutionRecord> page,
        RequestExecutionSortField sortField)
    {
        var edges = page.Nodes
            .Select(node => new RequestExecutionEdgeType
            {
                Node = ToGraphQL(node),
                Cursor = new CursorValue(
                    node.Id,
                    sortField switch
                    {
                        RequestExecutionSortField.CreatedAt => node.CreatedAt,
                        RequestExecutionSortField.UpdatedAt => node.UpdatedAt,
                        _ => null,
                    }),
            })
            .ToList();

        return new RequestExecutionConnectionType
        {
            Edges = edges,
            PageInfo = BuildPageInfo(
                edges.Select(static edge => edge.Cursor).ToList(),
                page.HasNextPage,
                page.HasPreviousPage),
            TotalCount = page.TotalCount,
        };
    }

    public static UsageLogConnectionType BuildConnection(
        ListPage<UsageLogRecord> page,
        UsageLogSortField sortField)
    {
        var edges = page.Nodes
            .Select(node => new UsageLogEdgeType
            {
                Node = ToGraphQL(node),
                Cursor = new CursorValue(
                    node.Id,
                    sortField switch
                    {
                        UsageLogSortField.CreatedAt => node.CreatedAt,
                        UsageLogSortField.UpdatedAt => node.UpdatedAt,
                        _ => null,
                    }),
            })
            .ToList();

        return new UsageLogConnectionType
        {
            Edges = edges,
            PageInfo = BuildPageInfo(
                edges.Select(static edge => edge.Cursor).ToList(),
                page.HasNextPage,
                page.HasPreviousPage),
            TotalCount = page.TotalCount,
        };
    }

    public static RequestExecutionListFilter? ToFilter(RequestExecutionWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new RequestExecutionListFilter
        {
            Id = ParseId(where.Id),
            IdNEQ = ParseId(where.IdNEQ),
            IdIn = ParseIds(where.IdIn),
            IdNotIn = ParseIds(where.IdNotIn),
            IdGT = ParseId(where.IdGT),
            IdGTE = ParseId(where.IdGTE),
            IdLT = ParseId(where.IdLT),
            IdLTE = ParseId(where.IdLTE),
            CreatedAt = where.CreatedAt,
            CreatedAtNEQ = where.CreatedAtNEQ,
            CreatedAtIn = ToList(where.CreatedAtIn),
            CreatedAtNotIn = ToList(where.CreatedAtNotIn),
            CreatedAtGT = where.CreatedAtGT,
            CreatedAtGTE = where.CreatedAtGTE,
            CreatedAtLT = where.CreatedAtLT,
            CreatedAtLTE = where.CreatedAtLTE,
            UpdatedAt = where.UpdatedAt,
            UpdatedAtNEQ = where.UpdatedAtNEQ,
            UpdatedAtIn = ToList(where.UpdatedAtIn),
            UpdatedAtNotIn = ToList(where.UpdatedAtNotIn),
            UpdatedAtGT = where.UpdatedAtGT,
            UpdatedAtGTE = where.UpdatedAtGTE,
            UpdatedAtLT = where.UpdatedAtLT,
            UpdatedAtLTE = where.UpdatedAtLTE,
            ProjectId = where.ProjectId,
            ProjectIdNEQ = where.ProjectIdNEQ,
            ProjectIdIn = ToLongList(where.ProjectIdIn),
            ProjectIdNotIn = ToLongList(where.ProjectIdNotIn),
            RequestId = ParseId(where.RequestId),
            RequestIdNEQ = ParseId(where.RequestIdNEQ),
            RequestIdIn = ParseIds(where.RequestIdIn),
            RequestIdNotIn = ParseIds(where.RequestIdNotIn),
            ChannelId = ParseId(where.ChannelId),
            ChannelIdNEQ = ParseId(where.ChannelIdNEQ),
            ChannelIdIn = ParseIds(where.ChannelIdIn),
            ChannelIdNotIn = ParseIds(where.ChannelIdNotIn),
            ChannelIdIsNil = where.ChannelIdIsNil == true ? true : null,
            ChannelIdNotNil = where.ChannelIdNotNil == true ? true : null,
            DataStorageId = ParseId(where.DataStorageId),
            DataStorageIdNEQ = ParseId(where.DataStorageIdNEQ),
            DataStorageIdIn = ParseIds(where.DataStorageIdIn),
            DataStorageIdNotIn = ParseIds(where.DataStorageIdNotIn),
            DataStorageIdIsNil = where.DataStorageIdIsNil == true ? true : null,
            DataStorageIdNotNil = where.DataStorageIdNotNil == true ? true : null,
            Status = where.Status is null
                ? null
                : ToWireStatus(where.Status.Value),
            StatusNEQ = where.StatusNEQ is null
                ? null
                : ToWireStatus(where.StatusNEQ.Value),
            StatusIn = where.StatusIn is { Count: > 0 }
                ? where.StatusIn.Select(static status => ToWireStatus(status)).ToList()
                : null,
            StatusNotIn = where.StatusNotIn is { Count: > 0 }
                ? where.StatusNotIn.Select(static status => ToWireStatus(status)).ToList()
                : null,
            ModelId = where.ModelId,
            ModelIdNEQ = where.ModelIdNEQ,
            ModelIdIn = ToList(where.ModelIdIn),
            ModelIdNotIn = ToList(where.ModelIdNotIn),
            ModelIdContains = where.ModelIdContains,
            ModelIdContainsFold = where.ModelIdContainsFold,
            ModelIdHasPrefix = where.ModelIdHasPrefix,
            ModelIdHasSuffix = where.ModelIdHasSuffix,
            ModelIdEqualFold = where.ModelIdEqualFold,
            Format = where.Format,
            FormatNEQ = where.FormatNEQ,
            FormatIn = ToList(where.FormatIn),
            FormatNotIn = ToList(where.FormatNotIn),
            ExternalId = where.ExternalId,
            ExternalIdNEQ = where.ExternalIdNEQ,
            ExternalIdIn = ToList(where.ExternalIdIn),
            ExternalIdNotIn = ToList(where.ExternalIdNotIn),
            Not = ToFilter(where.Not),
            And = ToList(
                where.And?.Select(ToFilter).OfType<RequestExecutionListFilter>().ToList()),
            Or = ToList(
                where.Or?.Select(ToFilter).OfType<RequestExecutionListFilter>().ToList()),
        };
    }

    public static UsageLogListFilter? ToFilter(UsageLogWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new UsageLogListFilter
        {
            Id = ParseId(where.Id),
            IdNEQ = ParseId(where.IdNEQ),
            IdIn = ParseIds(where.IdIn),
            IdNotIn = ParseIds(where.IdNotIn),
            IdGT = ParseId(where.IdGT),
            IdGTE = ParseId(where.IdGTE),
            IdLT = ParseId(where.IdLT),
            IdLTE = ParseId(where.IdLTE),
            CreatedAt = where.CreatedAt,
            CreatedAtNEQ = where.CreatedAtNEQ,
            CreatedAtIn = ToList(where.CreatedAtIn),
            CreatedAtNotIn = ToList(where.CreatedAtNotIn),
            CreatedAtGT = where.CreatedAtGT,
            CreatedAtGTE = where.CreatedAtGTE,
            CreatedAtLT = where.CreatedAtLT,
            CreatedAtLTE = where.CreatedAtLTE,
            UpdatedAt = where.UpdatedAt,
            UpdatedAtNEQ = where.UpdatedAtNEQ,
            UpdatedAtIn = ToList(where.UpdatedAtIn),
            UpdatedAtNotIn = ToList(where.UpdatedAtNotIn),
            UpdatedAtGT = where.UpdatedAtGT,
            UpdatedAtGTE = where.UpdatedAtGTE,
            UpdatedAtLT = where.UpdatedAtLT,
            UpdatedAtLTE = where.UpdatedAtLTE,
            RequestId = ParseId(where.RequestId),
            RequestIdNEQ = ParseId(where.RequestIdNEQ),
            RequestIdIn = ParseIds(where.RequestIdIn),
            RequestIdNotIn = ParseIds(where.RequestIdNotIn),
            ApiKeyId = where.ApiKeyId,
            ApiKeyIdNEQ = where.ApiKeyIdNEQ,
            ApiKeyIdIn = ToLongList(where.ApiKeyIdIn),
            ApiKeyIdNotIn = ToLongList(where.ApiKeyIdNotIn),
            ApiKeyIdIsNil = where.ApiKeyIdIsNil == true ? true : null,
            ApiKeyIdNotNil = where.ApiKeyIdNotNil == true ? true : null,
            ProjectId = ParseId(where.ProjectId),
            ProjectIdNEQ = ParseId(where.ProjectIdNEQ),
            ProjectIdIn = ParseIds(where.ProjectIdIn),
            ProjectIdNotIn = ParseIds(where.ProjectIdNotIn),
            ChannelId = ParseId(where.ChannelId),
            ChannelIdNEQ = ParseId(where.ChannelIdNEQ),
            ChannelIdIn = ParseIds(where.ChannelIdIn),
            ChannelIdNotIn = ParseIds(where.ChannelIdNotIn),
            ChannelIdIsNil = where.ChannelIdIsNil == true ? true : null,
            ChannelIdNotNil = where.ChannelIdNotNil == true ? true : null,
            Source = where.Source is null ? null : ToWireStatus(where.Source.Value),
            SourceNEQ = where.SourceNEQ is null ? null : ToWireStatus(where.SourceNEQ.Value),
            SourceIn = where.SourceIn is { Count: > 0 }
                ? where.SourceIn.Select(static source => ToWireStatus(source)).ToList()
                : null,
            SourceNotIn = where.SourceNotIn is { Count: > 0 }
                ? where.SourceNotIn.Select(static source => ToWireStatus(source)).ToList()
                : null,
            ModelId = where.ModelId,
            ModelIdNEQ = where.ModelIdNEQ,
            ModelIdIn = ToList(where.ModelIdIn),
            ModelIdNotIn = ToList(where.ModelIdNotIn),
            ModelIdContains = where.ModelIdContains,
            ModelIdContainsFold = where.ModelIdContainsFold,
            Format = where.Format,
            FormatNEQ = where.FormatNEQ,
            FormatIn = ToList(where.FormatIn),
            FormatNotIn = ToList(where.FormatNotIn),
            Not = ToFilter(where.Not),
            And = ToList(where.And?.Select(ToFilter).OfType<UsageLogListFilter>().ToList()),
            Or = ToList(where.Or?.Select(ToFilter).OfType<UsageLogListFilter>().ToList()),
        };
    }

    /// <summary>
    /// Port of sanitizeLoadedResponseChunks (request.go:143-151): the external
    /// marker collapses to an empty list.
    /// </summary>
    public static IReadOnlyList<JsonElement> LoadResponseChunks(
        string? chunksJson,
        bool stream,
        string status)
    {
        if (!stream || !IsFinishedStatus(status))
        {
            return [];
        }

        var chunks = ParseChunks(chunksJson);
        return chunks.Count == 1
            && TraceSegmentService.IsExternalResponseBodyMarker(chunks[0].GetRawText())
                ? []
                : chunks;
    }

    public static CostItemType ToGraphQL(CostItem item) => new()
    {
        ItemCode = PriceMapping.ToGraphQLItemCode(item.ItemCode),
        Quantity = checked((int)item.Quantity),
        TierBreakdown = item.TierBreakdown?.Select(ToGraphQL).ToList(),
        Subtotal = item.Subtotal,
    };

    public static TierCostType ToGraphQL(TierCost cost) => new()
    {
        UpTo = cost.UpTo is { } upTo ? checked((int)upTo) : null,
        Units = checked((int)cost.Units),
        Subtotal = cost.Subtotal,
    };

    public static IReadOnlyList<CostItemType>? ParseCostItems(string? json)
    {
        if (json is null)
        {
            return null;
        }

        var items = JsonSerializer.Deserialize<List<CostItem>>(json, BillingJson.Options);
        return items?.Select(ToGraphQL).ToList();
    }

    /// <summary>
    /// Go's JSONRawMessage keeps the stored bytes; a null column serializes as
    /// JSON null and invalid bytes fail the field like encoding/json does.
    /// </summary>
    public static JsonElement? ParseJsonOrNull(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Parses a body that the loader guarantees to be valid JSON.</summary>
    public static JsonElement ParseJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IReadOnlyList<JsonElement> ParseChunks(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return document.RootElement.EnumerateArray()
            .Select(static chunk => chunk.Clone())
            .ToList();
    }

    /// <summary>
    /// Go's int64 is exposed through the SDL's 32-bit Int; values outside the
    /// range fail the response instead of silently wrapping (C190).
    /// </summary>
    public static int? ToGraphQLInt(long? value)
        => value is null ? null : checked((int)value.Value);

    public static RequestExecutionStatusValue ToGraphQLExecutionStatus(string status)
        => status switch
        {
            "processing" => RequestExecutionStatusValue.Processing,
            "completed" => RequestExecutionStatusValue.Completed,
            "failed" => RequestExecutionStatusValue.Failed,
            "canceled" => RequestExecutionStatusValue.Canceled,
            _ => RequestExecutionStatusValue.Pending,
        };

    public static UsageLogSourceValue ToGraphQLUsageLogSource(string source)
        => source switch
        {
            "playground" => UsageLogSourceValue.Playground,
            "test" => UsageLogSourceValue.Test,
            _ => UsageLogSourceValue.Api,
        };

    public static string ToWireStatus(RequestExecutionStatusValue status) => status switch
    {
        RequestExecutionStatusValue.Processing => "processing",
        RequestExecutionStatusValue.Completed => "completed",
        RequestExecutionStatusValue.Failed => "failed",
        RequestExecutionStatusValue.Canceled => "canceled",
        _ => "pending",
    };

    public static string ToWireStatus(UsageLogSourceValue source) => source switch
    {
        UsageLogSourceValue.Playground => "playground",
        UsageLogSourceValue.Test => "test",
        _ => "api",
    };

    private static bool IsFinishedStatus(string status)
        => status is "completed" or "failed" or "canceled";

    private static long? ParseId(string? id)
        => id is null ? null : GidValue.Parse(id).Id;

    private static IReadOnlyList<long>? ParseIds(IReadOnlyList<string>? ids)
    {
        if (ids is not { Count: > 0 })
        {
            return null;
        }

        return ids.Select(static id => GidValue.Parse(id).Id).ToList();
    }

    private static IReadOnlyList<T>? ToList<T>(IReadOnlyList<T>? values)
        => values is { Count: > 0 } ? values : null;

    /// <summary>
    /// The SDL declares RequestExecution.projectID as Int while the store keys
    /// on long, so the list is widened element by element.
    /// </summary>
    private static IReadOnlyList<long>? ToLongList(IReadOnlyList<int>? values)
        => values is { Count: > 0 } ? values.Select(static value => (long)value).ToList() : null;

    private static PageInfoType BuildPageInfo(
        IReadOnlyList<CursorValue> cursors,
        bool hasNextPage,
        bool hasPreviousPage)
        => new()
        {
            HasNextPage = hasNextPage,
            HasPreviousPage = hasPreviousPage,
            StartCursor = cursors.Count > 0 ? cursors[0] : null,
            EndCursor = cursors.Count > 0 ? cursors[^1] : null,
        };
}
