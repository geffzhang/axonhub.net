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

    [GraphQLName("modelIDGT")]
    public string? ModelIdGT { get; set; }

    [GraphQLName("modelIDGTE")]
    public string? ModelIdGTE { get; set; }

    [GraphQLName("modelIDLT")]
    public string? ModelIdLT { get; set; }

    [GraphQLName("modelIDLTE")]
    public string? ModelIdLTE { get; set; }

    [GraphQLName("upstreamModelID")]
    public string? UpstreamModelId { get; set; }

    [GraphQLName("upstreamModelIDNEQ")]
    public string? UpstreamModelIdNEQ { get; set; }

    [GraphQLName("upstreamModelIDIn")]
    public IReadOnlyList<string>? UpstreamModelIdIn { get; set; }

    [GraphQLName("upstreamModelIDNotIn")]
    public IReadOnlyList<string>? UpstreamModelIdNotIn { get; set; }

    [GraphQLName("upstreamModelIDGT")]
    public string? UpstreamModelIdGT { get; set; }

    [GraphQLName("upstreamModelIDGTE")]
    public string? UpstreamModelIdGTE { get; set; }

    [GraphQLName("upstreamModelIDLT")]
    public string? UpstreamModelIdLT { get; set; }

    [GraphQLName("upstreamModelIDLTE")]
    public string? UpstreamModelIdLTE { get; set; }

    [GraphQLName("upstreamModelIDContains")]
    public string? UpstreamModelIdContains { get; set; }

    [GraphQLName("upstreamModelIDHasPrefix")]
    public string? UpstreamModelIdHasPrefix { get; set; }

    [GraphQLName("upstreamModelIDHasSuffix")]
    public string? UpstreamModelIdHasSuffix { get; set; }

    [GraphQLName("upstreamModelIDIsNil")]
    public bool? UpstreamModelIdIsNil { get; set; }

    [GraphQLName("upstreamModelIDNotNil")]
    public bool? UpstreamModelIdNotNil { get; set; }

    [GraphQLName("upstreamModelIDEqualFold")]
    public string? UpstreamModelIdEqualFold { get; set; }

    [GraphQLName("upstreamModelIDContainsFold")]
    public string? UpstreamModelIdContainsFold { get; set; }

    [GraphQLName("formatGT")]
    public string? FormatGT { get; set; }

    [GraphQLName("formatGTE")]
    public string? FormatGTE { get; set; }

    [GraphQLName("formatLT")]
    public string? FormatLT { get; set; }

    [GraphQLName("formatLTE")]
    public string? FormatLTE { get; set; }

    [GraphQLName("formatContains")]
    public string? FormatContains { get; set; }

    [GraphQLName("formatHasPrefix")]
    public string? FormatHasPrefix { get; set; }

    [GraphQLName("formatHasSuffix")]
    public string? FormatHasSuffix { get; set; }

    [GraphQLName("formatEqualFold")]
    public string? FormatEqualFold { get; set; }

    [GraphQLName("formatContainsFold")]
    public string? FormatContainsFold { get; set; }

    [GraphQLName("reasoningEffort")]
    public string? ReasoningEffort { get; set; }

    [GraphQLName("reasoningEffortNEQ")]
    public string? ReasoningEffortNEQ { get; set; }

    [GraphQLName("reasoningEffortIn")]
    public IReadOnlyList<string>? ReasoningEffortIn { get; set; }

    [GraphQLName("reasoningEffortNotIn")]
    public IReadOnlyList<string>? ReasoningEffortNotIn { get; set; }

    [GraphQLName("reasoningEffortGT")]
    public string? ReasoningEffortGT { get; set; }

    [GraphQLName("reasoningEffortGTE")]
    public string? ReasoningEffortGTE { get; set; }

    [GraphQLName("reasoningEffortLT")]
    public string? ReasoningEffortLT { get; set; }

    [GraphQLName("reasoningEffortLTE")]
    public string? ReasoningEffortLTE { get; set; }

    [GraphQLName("reasoningEffortContains")]
    public string? ReasoningEffortContains { get; set; }

    [GraphQLName("reasoningEffortHasPrefix")]
    public string? ReasoningEffortHasPrefix { get; set; }

    [GraphQLName("reasoningEffortHasSuffix")]
    public string? ReasoningEffortHasSuffix { get; set; }

    [GraphQLName("reasoningEffortIsNil")]
    public bool? ReasoningEffortIsNil { get; set; }

    [GraphQLName("reasoningEffortNotNil")]
    public bool? ReasoningEffortNotNil { get; set; }

    [GraphQLName("reasoningEffortEqualFold")]
    public string? ReasoningEffortEqualFold { get; set; }

    [GraphQLName("reasoningEffortContainsFold")]
    public string? ReasoningEffortContainsFold { get; set; }

    [GraphQLName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [GraphQLName("errorMessageNEQ")]
    public string? ErrorMessageNEQ { get; set; }

    [GraphQLName("errorMessageIn")]
    public IReadOnlyList<string>? ErrorMessageIn { get; set; }

    [GraphQLName("errorMessageNotIn")]
    public IReadOnlyList<string>? ErrorMessageNotIn { get; set; }

    [GraphQLName("errorMessageGT")]
    public string? ErrorMessageGT { get; set; }

    [GraphQLName("errorMessageGTE")]
    public string? ErrorMessageGTE { get; set; }

    [GraphQLName("errorMessageLT")]
    public string? ErrorMessageLT { get; set; }

    [GraphQLName("errorMessageLTE")]
    public string? ErrorMessageLTE { get; set; }

    [GraphQLName("errorMessageContains")]
    public string? ErrorMessageContains { get; set; }

    [GraphQLName("errorMessageHasPrefix")]
    public string? ErrorMessageHasPrefix { get; set; }

    [GraphQLName("errorMessageHasSuffix")]
    public string? ErrorMessageHasSuffix { get; set; }

    [GraphQLName("errorMessageIsNil")]
    public bool? ErrorMessageIsNil { get; set; }

    [GraphQLName("errorMessageNotNil")]
    public bool? ErrorMessageNotNil { get; set; }

    [GraphQLName("errorMessageEqualFold")]
    public string? ErrorMessageEqualFold { get; set; }

    [GraphQLName("errorMessageContainsFold")]
    public string? ErrorMessageContainsFold { get; set; }

    [GraphQLName("responseStatusCode")]
    public int? ResponseStatusCode { get; set; }

    [GraphQLName("responseStatusCodeNEQ")]
    public int? ResponseStatusCodeNEQ { get; set; }

    [GraphQLName("responseStatusCodeIn")]
    public IReadOnlyList<int>? ResponseStatusCodeIn { get; set; }

    [GraphQLName("responseStatusCodeNotIn")]
    public IReadOnlyList<int>? ResponseStatusCodeNotIn { get; set; }

    [GraphQLName("responseStatusCodeGT")]
    public int? ResponseStatusCodeGT { get; set; }

    [GraphQLName("responseStatusCodeGTE")]
    public int? ResponseStatusCodeGTE { get; set; }

    [GraphQLName("responseStatusCodeLT")]
    public int? ResponseStatusCodeLT { get; set; }

    [GraphQLName("responseStatusCodeLTE")]
    public int? ResponseStatusCodeLTE { get; set; }

    [GraphQLName("responseStatusCodeIsNil")]
    public bool? ResponseStatusCodeIsNil { get; set; }

    [GraphQLName("responseStatusCodeNotNil")]
    public bool? ResponseStatusCodeNotNil { get; set; }

    [GraphQLName("stream")]
    public bool? Stream { get; set; }

    [GraphQLName("streamNEQ")]
    public bool? StreamNEQ { get; set; }

    [GraphQLName("metricsLatencyMs")]
    public int? MetricsLatencyMs { get; set; }

    [GraphQLName("metricsLatencyMsNEQ")]
    public int? MetricsLatencyMsNEQ { get; set; }

    [GraphQLName("metricsLatencyMsIn")]
    public IReadOnlyList<int>? MetricsLatencyMsIn { get; set; }

    [GraphQLName("metricsLatencyMsNotIn")]
    public IReadOnlyList<int>? MetricsLatencyMsNotIn { get; set; }

    [GraphQLName("metricsLatencyMsGT")]
    public int? MetricsLatencyMsGT { get; set; }

    [GraphQLName("metricsLatencyMsGTE")]
    public int? MetricsLatencyMsGTE { get; set; }

    [GraphQLName("metricsLatencyMsLT")]
    public int? MetricsLatencyMsLT { get; set; }

    [GraphQLName("metricsLatencyMsLTE")]
    public int? MetricsLatencyMsLTE { get; set; }

    [GraphQLName("metricsLatencyMsIsNil")]
    public bool? MetricsLatencyMsIsNil { get; set; }

    [GraphQLName("metricsLatencyMsNotNil")]
    public bool? MetricsLatencyMsNotNil { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMs")]
    public int? MetricsFirstTokenLatencyMs { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsNEQ")]
    public int? MetricsFirstTokenLatencyMsNEQ { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsIn")]
    public IReadOnlyList<int>? MetricsFirstTokenLatencyMsIn { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsNotIn")]
    public IReadOnlyList<int>? MetricsFirstTokenLatencyMsNotIn { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsGT")]
    public int? MetricsFirstTokenLatencyMsGT { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsGTE")]
    public int? MetricsFirstTokenLatencyMsGTE { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsLT")]
    public int? MetricsFirstTokenLatencyMsLT { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsLTE")]
    public int? MetricsFirstTokenLatencyMsLTE { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsIsNil")]
    public bool? MetricsFirstTokenLatencyMsIsNil { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsNotNil")]
    public bool? MetricsFirstTokenLatencyMsNotNil { get; set; }

    [GraphQLName("metricsReasoningDurationMs")]
    public int? MetricsReasoningDurationMs { get; set; }

    [GraphQLName("metricsReasoningDurationMsNEQ")]
    public int? MetricsReasoningDurationMsNEQ { get; set; }

    [GraphQLName("metricsReasoningDurationMsIn")]
    public IReadOnlyList<int>? MetricsReasoningDurationMsIn { get; set; }

    [GraphQLName("metricsReasoningDurationMsNotIn")]
    public IReadOnlyList<int>? MetricsReasoningDurationMsNotIn { get; set; }

    [GraphQLName("metricsReasoningDurationMsGT")]
    public int? MetricsReasoningDurationMsGT { get; set; }

    [GraphQLName("metricsReasoningDurationMsGTE")]
    public int? MetricsReasoningDurationMsGTE { get; set; }

    [GraphQLName("metricsReasoningDurationMsLT")]
    public int? MetricsReasoningDurationMsLT { get; set; }

    [GraphQLName("metricsReasoningDurationMsLTE")]
    public int? MetricsReasoningDurationMsLTE { get; set; }

    [GraphQLName("metricsReasoningDurationMsIsNil")]
    public bool? MetricsReasoningDurationMsIsNil { get; set; }

    [GraphQLName("metricsReasoningDurationMsNotNil")]
    public bool? MetricsReasoningDurationMsNotNil { get; set; }

    [GraphQLName("requestURL")]
    public string? RequestUrl { get; set; }

    [GraphQLName("requestURLNEQ")]
    public string? RequestUrlNEQ { get; set; }

    [GraphQLName("requestURLIn")]
    public IReadOnlyList<string>? RequestUrlIn { get; set; }

    [GraphQLName("requestURLNotIn")]
    public IReadOnlyList<string>? RequestUrlNotIn { get; set; }

    [GraphQLName("requestURLGT")]
    public string? RequestUrlGT { get; set; }

    [GraphQLName("requestURLGTE")]
    public string? RequestUrlGTE { get; set; }

    [GraphQLName("requestURLLT")]
    public string? RequestUrlLT { get; set; }

    [GraphQLName("requestURLLTE")]
    public string? RequestUrlLTE { get; set; }

    [GraphQLName("requestURLContains")]
    public string? RequestUrlContains { get; set; }

    [GraphQLName("requestURLHasPrefix")]
    public string? RequestUrlHasPrefix { get; set; }

    [GraphQLName("requestURLHasSuffix")]
    public string? RequestUrlHasSuffix { get; set; }

    [GraphQLName("requestURLIsNil")]
    public bool? RequestUrlIsNil { get; set; }

    [GraphQLName("requestURLNotNil")]
    public bool? RequestUrlNotNil { get; set; }

    [GraphQLName("requestURLEqualFold")]
    public string? RequestUrlEqualFold { get; set; }

    [GraphQLName("requestURLContainsFold")]
    public string? RequestUrlContainsFold { get; set; }

    [GraphQLName("passThroughApplied")]
    public bool? PassThroughApplied { get; set; }

    [GraphQLName("passThroughAppliedNEQ")]
    public bool? PassThroughAppliedNEQ { get; set; }

    [GraphQLName("hasRequest")]
    public bool? HasRequest { get; set; }

    [GraphQLName("hasRequestWith")]
    public IReadOnlyList<RequestWhereInput>? HasRequestWith { get; set; }

    [GraphQLName("hasChannel")]
    public bool? HasChannel { get; set; }

    [GraphQLName("hasChannelWith")]
    public IReadOnlyList<ChannelWhereInput>? HasChannelWith { get; set; }

    [GraphQLName("hasDataStorage")]
    public bool? HasDataStorage { get; set; }

    [GraphQLName("hasDataStorageWith")]
    public IReadOnlyList<DataStorageWhereInput>? HasDataStorageWith { get; set; }
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

    [GraphQLName("apiKeyIDGT")]
    public int? ApiKeyIdGT { get; set; }

    [GraphQLName("apiKeyIDGTE")]
    public int? ApiKeyIdGTE { get; set; }

    [GraphQLName("apiKeyIDLT")]
    public int? ApiKeyIdLT { get; set; }

    [GraphQLName("apiKeyIDLTE")]
    public int? ApiKeyIdLTE { get; set; }

    [GraphQLName("modelIDGT")]
    public string? ModelIdGT { get; set; }

    [GraphQLName("modelIDGTE")]
    public string? ModelIdGTE { get; set; }

    [GraphQLName("modelIDLT")]
    public string? ModelIdLT { get; set; }

    [GraphQLName("modelIDLTE")]
    public string? ModelIdLTE { get; set; }

    [GraphQLName("modelIDHasPrefix")]
    public string? ModelIdHasPrefix { get; set; }

    [GraphQLName("modelIDHasSuffix")]
    public string? ModelIdHasSuffix { get; set; }

    [GraphQLName("modelIDEqualFold")]
    public string? ModelIdEqualFold { get; set; }

    [GraphQLName("promptTokens")]
    public int? PromptTokens { get; set; }

    [GraphQLName("promptTokensNEQ")]
    public int? PromptTokensNEQ { get; set; }

    [GraphQLName("promptTokensIn")]
    public IReadOnlyList<int>? PromptTokensIn { get; set; }

    [GraphQLName("promptTokensNotIn")]
    public IReadOnlyList<int>? PromptTokensNotIn { get; set; }

    [GraphQLName("promptTokensGT")]
    public int? PromptTokensGT { get; set; }

    [GraphQLName("promptTokensGTE")]
    public int? PromptTokensGTE { get; set; }

    [GraphQLName("promptTokensLT")]
    public int? PromptTokensLT { get; set; }

    [GraphQLName("promptTokensLTE")]
    public int? PromptTokensLTE { get; set; }

    [GraphQLName("completionTokens")]
    public int? CompletionTokens { get; set; }

    [GraphQLName("completionTokensNEQ")]
    public int? CompletionTokensNEQ { get; set; }

    [GraphQLName("completionTokensIn")]
    public IReadOnlyList<int>? CompletionTokensIn { get; set; }

    [GraphQLName("completionTokensNotIn")]
    public IReadOnlyList<int>? CompletionTokensNotIn { get; set; }

    [GraphQLName("completionTokensGT")]
    public int? CompletionTokensGT { get; set; }

    [GraphQLName("completionTokensGTE")]
    public int? CompletionTokensGTE { get; set; }

    [GraphQLName("completionTokensLT")]
    public int? CompletionTokensLT { get; set; }

    [GraphQLName("completionTokensLTE")]
    public int? CompletionTokensLTE { get; set; }

    [GraphQLName("totalTokens")]
    public int? TotalTokens { get; set; }

    [GraphQLName("totalTokensNEQ")]
    public int? TotalTokensNEQ { get; set; }

    [GraphQLName("totalTokensIn")]
    public IReadOnlyList<int>? TotalTokensIn { get; set; }

    [GraphQLName("totalTokensNotIn")]
    public IReadOnlyList<int>? TotalTokensNotIn { get; set; }

    [GraphQLName("totalTokensGT")]
    public int? TotalTokensGT { get; set; }

    [GraphQLName("totalTokensGTE")]
    public int? TotalTokensGTE { get; set; }

    [GraphQLName("totalTokensLT")]
    public int? TotalTokensLT { get; set; }

    [GraphQLName("totalTokensLTE")]
    public int? TotalTokensLTE { get; set; }

    [GraphQLName("promptAudioTokens")]
    public int? PromptAudioTokens { get; set; }

    [GraphQLName("promptAudioTokensNEQ")]
    public int? PromptAudioTokensNEQ { get; set; }

    [GraphQLName("promptAudioTokensIn")]
    public IReadOnlyList<int>? PromptAudioTokensIn { get; set; }

    [GraphQLName("promptAudioTokensNotIn")]
    public IReadOnlyList<int>? PromptAudioTokensNotIn { get; set; }

    [GraphQLName("promptAudioTokensGT")]
    public int? PromptAudioTokensGT { get; set; }

    [GraphQLName("promptAudioTokensGTE")]
    public int? PromptAudioTokensGTE { get; set; }

    [GraphQLName("promptAudioTokensLT")]
    public int? PromptAudioTokensLT { get; set; }

    [GraphQLName("promptAudioTokensLTE")]
    public int? PromptAudioTokensLTE { get; set; }

    [GraphQLName("promptAudioTokensIsNil")]
    public bool? PromptAudioTokensIsNil { get; set; }

    [GraphQLName("promptAudioTokensNotNil")]
    public bool? PromptAudioTokensNotNil { get; set; }

    [GraphQLName("promptCachedTokens")]
    public int? PromptCachedTokens { get; set; }

    [GraphQLName("promptCachedTokensNEQ")]
    public int? PromptCachedTokensNEQ { get; set; }

    [GraphQLName("promptCachedTokensIn")]
    public IReadOnlyList<int>? PromptCachedTokensIn { get; set; }

    [GraphQLName("promptCachedTokensNotIn")]
    public IReadOnlyList<int>? PromptCachedTokensNotIn { get; set; }

    [GraphQLName("promptCachedTokensGT")]
    public int? PromptCachedTokensGT { get; set; }

    [GraphQLName("promptCachedTokensGTE")]
    public int? PromptCachedTokensGTE { get; set; }

    [GraphQLName("promptCachedTokensLT")]
    public int? PromptCachedTokensLT { get; set; }

    [GraphQLName("promptCachedTokensLTE")]
    public int? PromptCachedTokensLTE { get; set; }

    [GraphQLName("promptCachedTokensIsNil")]
    public bool? PromptCachedTokensIsNil { get; set; }

    [GraphQLName("promptCachedTokensNotNil")]
    public bool? PromptCachedTokensNotNil { get; set; }

    [GraphQLName("promptWriteCachedTokens")]
    public int? PromptWriteCachedTokens { get; set; }

    [GraphQLName("promptWriteCachedTokensNEQ")]
    public int? PromptWriteCachedTokensNEQ { get; set; }

    [GraphQLName("promptWriteCachedTokensIn")]
    public IReadOnlyList<int>? PromptWriteCachedTokensIn { get; set; }

    [GraphQLName("promptWriteCachedTokensNotIn")]
    public IReadOnlyList<int>? PromptWriteCachedTokensNotIn { get; set; }

    [GraphQLName("promptWriteCachedTokensGT")]
    public int? PromptWriteCachedTokensGT { get; set; }

    [GraphQLName("promptWriteCachedTokensGTE")]
    public int? PromptWriteCachedTokensGTE { get; set; }

    [GraphQLName("promptWriteCachedTokensLT")]
    public int? PromptWriteCachedTokensLT { get; set; }

    [GraphQLName("promptWriteCachedTokensLTE")]
    public int? PromptWriteCachedTokensLTE { get; set; }

    [GraphQLName("promptWriteCachedTokensIsNil")]
    public bool? PromptWriteCachedTokensIsNil { get; set; }

    [GraphQLName("promptWriteCachedTokensNotNil")]
    public bool? PromptWriteCachedTokensNotNil { get; set; }

    [GraphQLName("promptWriteCachedTokens5m")]
    public int? PromptWriteCachedTokens5m { get; set; }

    [GraphQLName("promptWriteCachedTokens5mNEQ")]
    public int? PromptWriteCachedTokens5mNEQ { get; set; }

    [GraphQLName("promptWriteCachedTokens5mIn")]
    public IReadOnlyList<int>? PromptWriteCachedTokens5mIn { get; set; }

    [GraphQLName("promptWriteCachedTokens5mNotIn")]
    public IReadOnlyList<int>? PromptWriteCachedTokens5mNotIn { get; set; }

    [GraphQLName("promptWriteCachedTokens5mGT")]
    public int? PromptWriteCachedTokens5mGT { get; set; }

    [GraphQLName("promptWriteCachedTokens5mGTE")]
    public int? PromptWriteCachedTokens5mGTE { get; set; }

    [GraphQLName("promptWriteCachedTokens5mLT")]
    public int? PromptWriteCachedTokens5mLT { get; set; }

    [GraphQLName("promptWriteCachedTokens5mLTE")]
    public int? PromptWriteCachedTokens5mLTE { get; set; }

    [GraphQLName("promptWriteCachedTokens5mIsNil")]
    public bool? PromptWriteCachedTokens5mIsNil { get; set; }

    [GraphQLName("promptWriteCachedTokens5mNotNil")]
    public bool? PromptWriteCachedTokens5mNotNil { get; set; }

    [GraphQLName("promptWriteCachedTokens1h")]
    public int? PromptWriteCachedTokens1h { get; set; }

    [GraphQLName("promptWriteCachedTokens1hNEQ")]
    public int? PromptWriteCachedTokens1hNEQ { get; set; }

    [GraphQLName("promptWriteCachedTokens1hIn")]
    public IReadOnlyList<int>? PromptWriteCachedTokens1hIn { get; set; }

    [GraphQLName("promptWriteCachedTokens1hNotIn")]
    public IReadOnlyList<int>? PromptWriteCachedTokens1hNotIn { get; set; }

    [GraphQLName("promptWriteCachedTokens1hGT")]
    public int? PromptWriteCachedTokens1hGT { get; set; }

    [GraphQLName("promptWriteCachedTokens1hGTE")]
    public int? PromptWriteCachedTokens1hGTE { get; set; }

    [GraphQLName("promptWriteCachedTokens1hLT")]
    public int? PromptWriteCachedTokens1hLT { get; set; }

    [GraphQLName("promptWriteCachedTokens1hLTE")]
    public int? PromptWriteCachedTokens1hLTE { get; set; }

    [GraphQLName("promptWriteCachedTokens1hIsNil")]
    public bool? PromptWriteCachedTokens1hIsNil { get; set; }

    [GraphQLName("promptWriteCachedTokens1hNotNil")]
    public bool? PromptWriteCachedTokens1hNotNil { get; set; }

    [GraphQLName("completionAudioTokens")]
    public int? CompletionAudioTokens { get; set; }

    [GraphQLName("completionAudioTokensNEQ")]
    public int? CompletionAudioTokensNEQ { get; set; }

    [GraphQLName("completionAudioTokensIn")]
    public IReadOnlyList<int>? CompletionAudioTokensIn { get; set; }

    [GraphQLName("completionAudioTokensNotIn")]
    public IReadOnlyList<int>? CompletionAudioTokensNotIn { get; set; }

    [GraphQLName("completionAudioTokensGT")]
    public int? CompletionAudioTokensGT { get; set; }

    [GraphQLName("completionAudioTokensGTE")]
    public int? CompletionAudioTokensGTE { get; set; }

    [GraphQLName("completionAudioTokensLT")]
    public int? CompletionAudioTokensLT { get; set; }

    [GraphQLName("completionAudioTokensLTE")]
    public int? CompletionAudioTokensLTE { get; set; }

    [GraphQLName("completionAudioTokensIsNil")]
    public bool? CompletionAudioTokensIsNil { get; set; }

    [GraphQLName("completionAudioTokensNotNil")]
    public bool? CompletionAudioTokensNotNil { get; set; }

    [GraphQLName("completionReasoningTokens")]
    public int? CompletionReasoningTokens { get; set; }

    [GraphQLName("completionReasoningTokensNEQ")]
    public int? CompletionReasoningTokensNEQ { get; set; }

    [GraphQLName("completionReasoningTokensIn")]
    public IReadOnlyList<int>? CompletionReasoningTokensIn { get; set; }

    [GraphQLName("completionReasoningTokensNotIn")]
    public IReadOnlyList<int>? CompletionReasoningTokensNotIn { get; set; }

    [GraphQLName("completionReasoningTokensGT")]
    public int? CompletionReasoningTokensGT { get; set; }

    [GraphQLName("completionReasoningTokensGTE")]
    public int? CompletionReasoningTokensGTE { get; set; }

    [GraphQLName("completionReasoningTokensLT")]
    public int? CompletionReasoningTokensLT { get; set; }

    [GraphQLName("completionReasoningTokensLTE")]
    public int? CompletionReasoningTokensLTE { get; set; }

    [GraphQLName("completionReasoningTokensIsNil")]
    public bool? CompletionReasoningTokensIsNil { get; set; }

    [GraphQLName("completionReasoningTokensNotNil")]
    public bool? CompletionReasoningTokensNotNil { get; set; }

    [GraphQLName("completionAcceptedPredictionTokens")]
    public int? CompletionAcceptedPredictionTokens { get; set; }

    [GraphQLName("completionAcceptedPredictionTokensNEQ")]
    public int? CompletionAcceptedPredictionTokensNEQ { get; set; }

    [GraphQLName("completionAcceptedPredictionTokensIn")]
    public IReadOnlyList<int>? CompletionAcceptedPredictionTokensIn { get; set; }

    [GraphQLName("completionAcceptedPredictionTokensNotIn")]
    public IReadOnlyList<int>? CompletionAcceptedPredictionTokensNotIn { get; set; }

    [GraphQLName("completionAcceptedPredictionTokensGT")]
    public int? CompletionAcceptedPredictionTokensGT { get; set; }

    [GraphQLName("completionAcceptedPredictionTokensGTE")]
    public int? CompletionAcceptedPredictionTokensGTE { get; set; }

    [GraphQLName("completionAcceptedPredictionTokensLT")]
    public int? CompletionAcceptedPredictionTokensLT { get; set; }

    [GraphQLName("completionAcceptedPredictionTokensLTE")]
    public int? CompletionAcceptedPredictionTokensLTE { get; set; }

    [GraphQLName("completionAcceptedPredictionTokensIsNil")]
    public bool? CompletionAcceptedPredictionTokensIsNil { get; set; }

    [GraphQLName("completionAcceptedPredictionTokensNotNil")]
    public bool? CompletionAcceptedPredictionTokensNotNil { get; set; }

    [GraphQLName("completionRejectedPredictionTokens")]
    public int? CompletionRejectedPredictionTokens { get; set; }

    [GraphQLName("completionRejectedPredictionTokensNEQ")]
    public int? CompletionRejectedPredictionTokensNEQ { get; set; }

    [GraphQLName("completionRejectedPredictionTokensIn")]
    public IReadOnlyList<int>? CompletionRejectedPredictionTokensIn { get; set; }

    [GraphQLName("completionRejectedPredictionTokensNotIn")]
    public IReadOnlyList<int>? CompletionRejectedPredictionTokensNotIn { get; set; }

    [GraphQLName("completionRejectedPredictionTokensGT")]
    public int? CompletionRejectedPredictionTokensGT { get; set; }

    [GraphQLName("completionRejectedPredictionTokensGTE")]
    public int? CompletionRejectedPredictionTokensGTE { get; set; }

    [GraphQLName("completionRejectedPredictionTokensLT")]
    public int? CompletionRejectedPredictionTokensLT { get; set; }

    [GraphQLName("completionRejectedPredictionTokensLTE")]
    public int? CompletionRejectedPredictionTokensLTE { get; set; }

    [GraphQLName("completionRejectedPredictionTokensIsNil")]
    public bool? CompletionRejectedPredictionTokensIsNil { get; set; }

    [GraphQLName("completionRejectedPredictionTokensNotNil")]
    public bool? CompletionRejectedPredictionTokensNotNil { get; set; }

    [GraphQLName("formatGT")]
    public string? FormatGT { get; set; }

    [GraphQLName("formatGTE")]
    public string? FormatGTE { get; set; }

    [GraphQLName("formatLT")]
    public string? FormatLT { get; set; }

    [GraphQLName("formatLTE")]
    public string? FormatLTE { get; set; }

    [GraphQLName("formatContains")]
    public string? FormatContains { get; set; }

    [GraphQLName("formatHasPrefix")]
    public string? FormatHasPrefix { get; set; }

    [GraphQLName("formatHasSuffix")]
    public string? FormatHasSuffix { get; set; }

    [GraphQLName("formatEqualFold")]
    public string? FormatEqualFold { get; set; }

    [GraphQLName("formatContainsFold")]
    public string? FormatContainsFold { get; set; }

    [GraphQLName("totalCost")]
    public double? TotalCost { get; set; }

    [GraphQLName("totalCostNEQ")]
    public double? TotalCostNEQ { get; set; }

    [GraphQLName("totalCostIn")]
    public IReadOnlyList<double>? TotalCostIn { get; set; }

    [GraphQLName("totalCostNotIn")]
    public IReadOnlyList<double>? TotalCostNotIn { get; set; }

    [GraphQLName("totalCostGT")]
    public double? TotalCostGT { get; set; }

    [GraphQLName("totalCostGTE")]
    public double? TotalCostGTE { get; set; }

    [GraphQLName("totalCostLT")]
    public double? TotalCostLT { get; set; }

    [GraphQLName("totalCostLTE")]
    public double? TotalCostLTE { get; set; }

    [GraphQLName("totalCostIsNil")]
    public bool? TotalCostIsNil { get; set; }

    [GraphQLName("totalCostNotNil")]
    public bool? TotalCostNotNil { get; set; }

    [GraphQLName("costPriceReferenceID")]
    public string? CostPriceReferenceId { get; set; }

    [GraphQLName("costPriceReferenceIDNEQ")]
    public string? CostPriceReferenceIdNEQ { get; set; }

    [GraphQLName("costPriceReferenceIDIn")]
    public IReadOnlyList<string>? CostPriceReferenceIdIn { get; set; }

    [GraphQLName("costPriceReferenceIDNotIn")]
    public IReadOnlyList<string>? CostPriceReferenceIdNotIn { get; set; }

    [GraphQLName("costPriceReferenceIDGT")]
    public string? CostPriceReferenceIdGT { get; set; }

    [GraphQLName("costPriceReferenceIDGTE")]
    public string? CostPriceReferenceIdGTE { get; set; }

    [GraphQLName("costPriceReferenceIDLT")]
    public string? CostPriceReferenceIdLT { get; set; }

    [GraphQLName("costPriceReferenceIDLTE")]
    public string? CostPriceReferenceIdLTE { get; set; }

    [GraphQLName("costPriceReferenceIDContains")]
    public string? CostPriceReferenceIdContains { get; set; }

    [GraphQLName("costPriceReferenceIDHasPrefix")]
    public string? CostPriceReferenceIdHasPrefix { get; set; }

    [GraphQLName("costPriceReferenceIDHasSuffix")]
    public string? CostPriceReferenceIdHasSuffix { get; set; }

    [GraphQLName("costPriceReferenceIDIsNil")]
    public bool? CostPriceReferenceIdIsNil { get; set; }

    [GraphQLName("costPriceReferenceIDNotNil")]
    public bool? CostPriceReferenceIdNotNil { get; set; }

    [GraphQLName("costPriceReferenceIDEqualFold")]
    public string? CostPriceReferenceIdEqualFold { get; set; }

    [GraphQLName("costPriceReferenceIDContainsFold")]
    public string? CostPriceReferenceIdContainsFold { get; set; }

    [GraphQLName("hasRequest")]
    public bool? HasRequest { get; set; }

    [GraphQLName("hasRequestWith")]
    public IReadOnlyList<RequestWhereInput>? HasRequestWith { get; set; }

    [GraphQLName("hasProject")]
    public bool? HasProject { get; set; }

    [GraphQLName("hasProjectWith")]
    public IReadOnlyList<ProjectWhereInput>? HasProjectWith { get; set; }

    [GraphQLName("hasChannel")]
    public bool? HasChannel { get; set; }

    [GraphQLName("hasChannelWith")]
    public IReadOnlyList<ChannelWhereInput>? HasChannelWith { get; set; }
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

    [GraphQLName("stream")]
    public bool? Stream { get; set; }

    [GraphQLName("streamNEQ")]
    public bool? StreamNEQ { get; set; }

    [GraphQLName("clientIP")]
    public string? ClientIp { get; set; }

    [GraphQLName("clientIPNEQ")]
    public string? ClientIpNEQ { get; set; }

    [GraphQLName("clientIPIn")]
    public IReadOnlyList<string>? ClientIpIn { get; set; }

    [GraphQLName("clientIPNotIn")]
    public IReadOnlyList<string>? ClientIpNotIn { get; set; }

    [GraphQLName("clientIPGT")]
    public string? ClientIpGT { get; set; }

    [GraphQLName("clientIPGTE")]
    public string? ClientIpGTE { get; set; }

    [GraphQLName("clientIPLT")]
    public string? ClientIpLT { get; set; }

    [GraphQLName("clientIPLTE")]
    public string? ClientIpLTE { get; set; }

    [GraphQLName("clientIPContains")]
    public string? ClientIpContains { get; set; }

    [GraphQLName("clientIPHasPrefix")]
    public string? ClientIpHasPrefix { get; set; }

    [GraphQLName("clientIPHasSuffix")]
    public string? ClientIpHasSuffix { get; set; }

    [GraphQLName("clientIPEqualFold")]
    public string? ClientIpEqualFold { get; set; }

    [GraphQLName("clientIPContainsFold")]
    public string? ClientIpContainsFold { get; set; }

    [GraphQLName("userAgent")]
    public string? UserAgent { get; set; }

    [GraphQLName("userAgentNEQ")]
    public string? UserAgentNEQ { get; set; }

    [GraphQLName("userAgentIn")]
    public IReadOnlyList<string>? UserAgentIn { get; set; }

    [GraphQLName("userAgentNotIn")]
    public IReadOnlyList<string>? UserAgentNotIn { get; set; }

    [GraphQLName("userAgentGT")]
    public string? UserAgentGT { get; set; }

    [GraphQLName("userAgentGTE")]
    public string? UserAgentGTE { get; set; }

    [GraphQLName("userAgentLT")]
    public string? UserAgentLT { get; set; }

    [GraphQLName("userAgentLTE")]
    public string? UserAgentLTE { get; set; }

    [GraphQLName("userAgentContains")]
    public string? UserAgentContains { get; set; }

    [GraphQLName("userAgentHasPrefix")]
    public string? UserAgentHasPrefix { get; set; }

    [GraphQLName("userAgentHasSuffix")]
    public string? UserAgentHasSuffix { get; set; }

    [GraphQLName("userAgentEqualFold")]
    public string? UserAgentEqualFold { get; set; }

    [GraphQLName("userAgentContainsFold")]
    public string? UserAgentContainsFold { get; set; }

    [GraphQLName("metricsLatencyMs")]
    public int? MetricsLatencyMs { get; set; }

    [GraphQLName("metricsLatencyMsNEQ")]
    public int? MetricsLatencyMsNEQ { get; set; }

    [GraphQLName("metricsLatencyMsIn")]
    public IReadOnlyList<int>? MetricsLatencyMsIn { get; set; }

    [GraphQLName("metricsLatencyMsNotIn")]
    public IReadOnlyList<int>? MetricsLatencyMsNotIn { get; set; }

    [GraphQLName("metricsLatencyMsGT")]
    public int? MetricsLatencyMsGT { get; set; }

    [GraphQLName("metricsLatencyMsGTE")]
    public int? MetricsLatencyMsGTE { get; set; }

    [GraphQLName("metricsLatencyMsLT")]
    public int? MetricsLatencyMsLT { get; set; }

    [GraphQLName("metricsLatencyMsLTE")]
    public int? MetricsLatencyMsLTE { get; set; }

    [GraphQLName("metricsLatencyMsIsNil")]
    public bool? MetricsLatencyMsIsNil { get; set; }

    [GraphQLName("metricsLatencyMsNotNil")]
    public bool? MetricsLatencyMsNotNil { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMs")]
    public int? MetricsFirstTokenLatencyMs { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsNEQ")]
    public int? MetricsFirstTokenLatencyMsNEQ { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsIn")]
    public IReadOnlyList<int>? MetricsFirstTokenLatencyMsIn { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsNotIn")]
    public IReadOnlyList<int>? MetricsFirstTokenLatencyMsNotIn { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsGT")]
    public int? MetricsFirstTokenLatencyMsGT { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsGTE")]
    public int? MetricsFirstTokenLatencyMsGTE { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsLT")]
    public int? MetricsFirstTokenLatencyMsLT { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsLTE")]
    public int? MetricsFirstTokenLatencyMsLTE { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsIsNil")]
    public bool? MetricsFirstTokenLatencyMsIsNil { get; set; }

    [GraphQLName("metricsFirstTokenLatencyMsNotNil")]
    public bool? MetricsFirstTokenLatencyMsNotNil { get; set; }

    [GraphQLName("metricsReasoningDurationMs")]
    public int? MetricsReasoningDurationMs { get; set; }

    [GraphQLName("metricsReasoningDurationMsNEQ")]
    public int? MetricsReasoningDurationMsNEQ { get; set; }

    [GraphQLName("metricsReasoningDurationMsIn")]
    public IReadOnlyList<int>? MetricsReasoningDurationMsIn { get; set; }

    [GraphQLName("metricsReasoningDurationMsNotIn")]
    public IReadOnlyList<int>? MetricsReasoningDurationMsNotIn { get; set; }

    [GraphQLName("metricsReasoningDurationMsGT")]
    public int? MetricsReasoningDurationMsGT { get; set; }

    [GraphQLName("metricsReasoningDurationMsGTE")]
    public int? MetricsReasoningDurationMsGTE { get; set; }

    [GraphQLName("metricsReasoningDurationMsLT")]
    public int? MetricsReasoningDurationMsLT { get; set; }

    [GraphQLName("metricsReasoningDurationMsLTE")]
    public int? MetricsReasoningDurationMsLTE { get; set; }

    [GraphQLName("metricsReasoningDurationMsIsNil")]
    public bool? MetricsReasoningDurationMsIsNil { get; set; }

    [GraphQLName("metricsReasoningDurationMsNotNil")]
    public bool? MetricsReasoningDurationMsNotNil { get; set; }

    [GraphQLName("contentSaved")]
    public bool? ContentSaved { get; set; }

    [GraphQLName("contentSavedNEQ")]
    public bool? ContentSavedNEQ { get; set; }

    [GraphQLName("contentStorageID")]
    public int? ContentStorageId { get; set; }

    [GraphQLName("contentStorageIDNEQ")]
    public int? ContentStorageIdNEQ { get; set; }

    [GraphQLName("contentStorageIDIn")]
    public IReadOnlyList<int>? ContentStorageIdIn { get; set; }

    [GraphQLName("contentStorageIDNotIn")]
    public IReadOnlyList<int>? ContentStorageIdNotIn { get; set; }

    [GraphQLName("contentStorageIDGT")]
    public int? ContentStorageIdGT { get; set; }

    [GraphQLName("contentStorageIDGTE")]
    public int? ContentStorageIdGTE { get; set; }

    [GraphQLName("contentStorageIDLT")]
    public int? ContentStorageIdLT { get; set; }

    [GraphQLName("contentStorageIDLTE")]
    public int? ContentStorageIdLTE { get; set; }

    [GraphQLName("contentStorageIDIsNil")]
    public bool? ContentStorageIdIsNil { get; set; }

    [GraphQLName("contentStorageIDNotNil")]
    public bool? ContentStorageIdNotNil { get; set; }

    [GraphQLName("contentStorageKey")]
    public string? ContentStorageKey { get; set; }

    [GraphQLName("contentStorageKeyNEQ")]
    public string? ContentStorageKeyNEQ { get; set; }

    [GraphQLName("contentStorageKeyIn")]
    public IReadOnlyList<string>? ContentStorageKeyIn { get; set; }

    [GraphQLName("contentStorageKeyNotIn")]
    public IReadOnlyList<string>? ContentStorageKeyNotIn { get; set; }

    [GraphQLName("contentStorageKeyGT")]
    public string? ContentStorageKeyGT { get; set; }

    [GraphQLName("contentStorageKeyGTE")]
    public string? ContentStorageKeyGTE { get; set; }

    [GraphQLName("contentStorageKeyLT")]
    public string? ContentStorageKeyLT { get; set; }

    [GraphQLName("contentStorageKeyLTE")]
    public string? ContentStorageKeyLTE { get; set; }

    [GraphQLName("contentStorageKeyContains")]
    public string? ContentStorageKeyContains { get; set; }

    [GraphQLName("contentStorageKeyHasPrefix")]
    public string? ContentStorageKeyHasPrefix { get; set; }

    [GraphQLName("contentStorageKeyHasSuffix")]
    public string? ContentStorageKeyHasSuffix { get; set; }

    [GraphQLName("contentStorageKeyIsNil")]
    public bool? ContentStorageKeyIsNil { get; set; }

    [GraphQLName("contentStorageKeyNotNil")]
    public bool? ContentStorageKeyNotNil { get; set; }

    [GraphQLName("contentStorageKeyEqualFold")]
    public string? ContentStorageKeyEqualFold { get; set; }

    [GraphQLName("contentStorageKeyContainsFold")]
    public string? ContentStorageKeyContainsFold { get; set; }

    [GraphQLName("contentSavedAt")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? ContentSavedAt { get; set; }

    [GraphQLName("contentSavedAtNEQ")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? ContentSavedAtNEQ { get; set; }

    [GraphQLName("contentSavedAtIn")]
    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? ContentSavedAtIn { get; set; }

    [GraphQLName("contentSavedAtNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? ContentSavedAtNotIn { get; set; }

    [GraphQLName("contentSavedAtGT")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? ContentSavedAtGT { get; set; }

    [GraphQLName("contentSavedAtGTE")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? ContentSavedAtGTE { get; set; }

    [GraphQLName("contentSavedAtLT")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? ContentSavedAtLT { get; set; }

    [GraphQLName("contentSavedAtLTE")]
    [GraphQLType(typeof(GoTimeType))]
    public DateTime? ContentSavedAtLTE { get; set; }

    [GraphQLName("contentSavedAtIsNil")]
    [GraphQLType(typeof(GoTimeType))]
    public bool? ContentSavedAtIsNil { get; set; }

    [GraphQLName("contentSavedAtNotNil")]
    [GraphQLType(typeof(GoTimeType))]
    public bool? ContentSavedAtNotNil { get; set; }

    [GraphQLName("hasAPIKey")]
    public bool? HasApiKey { get; set; }

    [GraphQLName("hasAPIKeyWith")]
    public IReadOnlyList<ApiKeyWhereInput>? HasApiKeyWith { get; set; }

    [GraphQLName("hasProject")]
    public bool? HasProject { get; set; }

    [GraphQLName("hasProjectWith")]
    public IReadOnlyList<ProjectWhereInput>? HasProjectWith { get; set; }

    [GraphQLName("hasTrace")]
    public bool? HasTrace { get; set; }

    [GraphQLName("hasTraceWith")]
    public IReadOnlyList<TraceWhereInput>? HasTraceWith { get; set; }

    [GraphQLName("hasDataStorage")]
    public bool? HasDataStorage { get; set; }

    [GraphQLName("hasDataStorageWith")]
    public IReadOnlyList<DataStorageWhereInput>? HasDataStorageWith { get; set; }

    [GraphQLName("hasExecutions")]
    public bool? HasExecutions { get; set; }

    [GraphQLName("hasExecutionsWith")]
    public IReadOnlyList<RequestExecutionWhereInput>? HasExecutionsWith { get; set; }

    [GraphQLName("hasChannel")]
    public bool? HasChannel { get; set; }

    [GraphQLName("hasChannelWith")]
    public IReadOnlyList<ChannelWhereInput>? HasChannelWith { get; set; }

    [GraphQLName("hasUsageLogs")]
    public bool? HasUsageLogs { get; set; }

    [GraphQLName("hasUsageLogsWith")]
    public IReadOnlyList<UsageLogWhereInput>? HasUsageLogsWith { get; set; }
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
public sealed class RequestExecutionType : INode
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

    /// <summary>Port of RequestService.LoadRequestExecutionRequestBody.</summary>
    [GraphQLName("requestBody")]
    [GraphQLType(typeof(JsonRawMessageType))]
    public async Task<JsonElement> GetRequestBodyAsync(
        [Service] RequestPayloadStorage payloads,
        CancellationToken cancellationToken)
        => RequestMapping.ParseJson(
            await payloads.LoadExecutionRequestBodyAsync(Record, cancellationToken)
                .ConfigureAwait(false));

    [GraphQLName("responseHeaders")]
    [GraphQLType(typeof(JsonRawMessageType))]
    public JsonElement? ResponseHeaders => RequestMapping.ParseJsonOrNull(Record.ResponseHeaders);

    /// <summary>Port of RequestService.LoadRequestExecutionResponseBody.</summary>
    [GraphQLName("responseBody")]
    [GraphQLType(typeof(JsonRawMessageType))]
    public async Task<JsonElement> GetResponseBodyAsync(
        [Service] RequestPayloadStorage payloads,
        CancellationToken cancellationToken)
        => RequestMapping.ParseJson(
            await payloads.LoadExecutionResponseBodyAsync(Record, cancellationToken)
                .ConfigureAwait(false));

    /// <summary>Port of RequestService.LoadRequestExecutionResponseChunks.</summary>
    [GraphQLName("responseChunks")]
    [GraphQLType(typeof(ListType<NonNullType<JsonRawMessageType>>))]
    public async Task<IReadOnlyList<JsonElement>> GetResponseChunksAsync(
        [Service] RequestPayloadStorage payloads,
        CancellationToken cancellationToken)
        => RequestMapping.ParseChunks(
            await payloads.LoadExecutionResponseChunksAsync(Record, cancellationToken)
                .ConfigureAwait(false));

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

    /// <summary>Port of the DataStorage edge (gql_edge.go:569-576): MaskNotFound.</summary>
    [GraphQLName("dataStorage")]
    public async Task<DataStorageType?> GetDataStorageAsync(
        [Service] RequestQueryService requests,
        CancellationToken cancellationToken)
    {
        var storage = await requests.GetVisibleDataStorageOrNullAsync(
                Record.DataStorageId,
                cancellationToken)
            .ConfigureAwait(false);
        return storage is null ? null : DataStorageMapping.ToGraphQL(storage);
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
public sealed class UsageLogType : INode
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
    public async Task<JsonElement> GetRequestBodyAsync(
        [Service] RequestPayloadStorage payloads,
        CancellationToken cancellationToken)
        => RequestMapping.ParseJson(
            await payloads.LoadRequestBodyAsync(Record, cancellationToken)
                .ConfigureAwait(false));

    [GraphQLName("responseHeaders")]
    [GraphQLType(typeof(JsonRawMessageType))]
    public JsonElement? ResponseHeaders => RequestMapping.ParseJsonOrNull(Record.ResponseHeaders);

    /// <summary>Port of RequestService.LoadResponseBody (ent.resolvers.go:660-671).</summary>
    [GraphQLName("responseBody")]
    [GraphQLType(typeof(JsonRawMessageType))]
    public async Task<JsonElement> GetResponseBodyAsync(
        [Service] RequestPayloadStorage payloads,
        CancellationToken cancellationToken)
        => RequestMapping.ParseJson(
            await payloads.LoadResponseBodyAsync(Record, cancellationToken)
                .ConfigureAwait(false));

    /// <summary>Port of RequestService.LoadResponseChunks (ent.resolvers.go:673-687).</summary>
    [GraphQLName("responseChunks")]
    [GraphQLType(typeof(ListType<NonNullType<JsonRawMessageType>>))]
    public async Task<IReadOnlyList<JsonElement>> GetResponseChunksAsync(
        [Service] RequestPayloadStorage payloads,
        CancellationToken cancellationToken)
        => RequestMapping.ParseChunks(
            await payloads.LoadResponseChunksAsync(Record, cancellationToken)
                .ConfigureAwait(false));

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

    /// <summary>Port of the DataStorage edge (gql_edge.go:495-502): MaskNotFound.</summary>
    [GraphQLName("dataStorage")]
    public async Task<DataStorageType?> GetDataStorageAsync(
        [Service] RequestQueryService requests,
        CancellationToken cancellationToken)
    {
        var storage = await requests.GetVisibleDataStorageOrNullAsync(
                Record.DataStorageId,
                cancellationToken)
            .ConfigureAwait(false);
        return storage is null ? null : DataStorageMapping.ToGraphQL(storage);
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

    /// <summary>
    /// Port of the root usageLogs resolver
    /// (internal/server/gql/ent.resolvers.go:578-580): CREATED_AT falls back to
    /// ent.DefaultUsageLogOrder's id column; the direction is preserved.
    /// </summary>
    public static UsageLogSortField ToRootUsageLogSortField(UsageLogOrder? orderBy)
        => orderBy?.Field == UsageLogOrderFieldValue.UpdatedAt
            ? UsageLogSortField.UpdatedAt
            : UsageLogSortField.Id;

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
            ExternalIdGT = where.ExternalIdGT,
            ExternalIdGTE = where.ExternalIdGTE,
            ExternalIdLT = where.ExternalIdLT,
            ExternalIdLTE = where.ExternalIdLTE,
            ExternalIdContains = where.ExternalIdContains,
            ExternalIdHasPrefix = where.ExternalIdHasPrefix,
            ExternalIdHasSuffix = where.ExternalIdHasSuffix,
            ExternalIdIsNil = where.ExternalIdIsNil,
            ExternalIdNotNil = where.ExternalIdNotNil,
            ExternalIdEqualFold = where.ExternalIdEqualFold,
            ExternalIdContainsFold = where.ExternalIdContainsFold,
            ModelIdGT = where.ModelIdGT,
            ModelIdGTE = where.ModelIdGTE,
            ModelIdLT = where.ModelIdLT,
            ModelIdLTE = where.ModelIdLTE,
            UpstreamModelId = where.UpstreamModelId,
            UpstreamModelIdNEQ = where.UpstreamModelIdNEQ,
            UpstreamModelIdIn = ToList(where.UpstreamModelIdIn),
            UpstreamModelIdNotIn = ToList(where.UpstreamModelIdNotIn),
            UpstreamModelIdGT = where.UpstreamModelIdGT,
            UpstreamModelIdGTE = where.UpstreamModelIdGTE,
            UpstreamModelIdLT = where.UpstreamModelIdLT,
            UpstreamModelIdLTE = where.UpstreamModelIdLTE,
            UpstreamModelIdContains = where.UpstreamModelIdContains,
            UpstreamModelIdHasPrefix = where.UpstreamModelIdHasPrefix,
            UpstreamModelIdHasSuffix = where.UpstreamModelIdHasSuffix,
            UpstreamModelIdIsNil = where.UpstreamModelIdIsNil,
            UpstreamModelIdNotNil = where.UpstreamModelIdNotNil,
            UpstreamModelIdEqualFold = where.UpstreamModelIdEqualFold,
            UpstreamModelIdContainsFold = where.UpstreamModelIdContainsFold,
            FormatGT = where.FormatGT,
            FormatGTE = where.FormatGTE,
            FormatLT = where.FormatLT,
            FormatLTE = where.FormatLTE,
            FormatContains = where.FormatContains,
            FormatHasPrefix = where.FormatHasPrefix,
            FormatHasSuffix = where.FormatHasSuffix,
            FormatEqualFold = where.FormatEqualFold,
            FormatContainsFold = where.FormatContainsFold,
            ReasoningEffort = where.ReasoningEffort,
            ReasoningEffortNEQ = where.ReasoningEffortNEQ,
            ReasoningEffortIn = ToList(where.ReasoningEffortIn),
            ReasoningEffortNotIn = ToList(where.ReasoningEffortNotIn),
            ReasoningEffortGT = where.ReasoningEffortGT,
            ReasoningEffortGTE = where.ReasoningEffortGTE,
            ReasoningEffortLT = where.ReasoningEffortLT,
            ReasoningEffortLTE = where.ReasoningEffortLTE,
            ReasoningEffortContains = where.ReasoningEffortContains,
            ReasoningEffortHasPrefix = where.ReasoningEffortHasPrefix,
            ReasoningEffortHasSuffix = where.ReasoningEffortHasSuffix,
            ReasoningEffortIsNil = where.ReasoningEffortIsNil,
            ReasoningEffortNotNil = where.ReasoningEffortNotNil,
            ReasoningEffortEqualFold = where.ReasoningEffortEqualFold,
            ReasoningEffortContainsFold = where.ReasoningEffortContainsFold,
            ErrorMessage = where.ErrorMessage,
            ErrorMessageNEQ = where.ErrorMessageNEQ,
            ErrorMessageIn = ToList(where.ErrorMessageIn),
            ErrorMessageNotIn = ToList(where.ErrorMessageNotIn),
            ErrorMessageGT = where.ErrorMessageGT,
            ErrorMessageGTE = where.ErrorMessageGTE,
            ErrorMessageLT = where.ErrorMessageLT,
            ErrorMessageLTE = where.ErrorMessageLTE,
            ErrorMessageContains = where.ErrorMessageContains,
            ErrorMessageHasPrefix = where.ErrorMessageHasPrefix,
            ErrorMessageHasSuffix = where.ErrorMessageHasSuffix,
            ErrorMessageIsNil = where.ErrorMessageIsNil,
            ErrorMessageNotNil = where.ErrorMessageNotNil,
            ErrorMessageEqualFold = where.ErrorMessageEqualFold,
            ErrorMessageContainsFold = where.ErrorMessageContainsFold,
            ResponseStatusCode = where.ResponseStatusCode,
            ResponseStatusCodeNEQ = where.ResponseStatusCodeNEQ,
            ResponseStatusCodeIn = ToLongList(where.ResponseStatusCodeIn),
            ResponseStatusCodeNotIn = ToLongList(where.ResponseStatusCodeNotIn),
            ResponseStatusCodeGT = where.ResponseStatusCodeGT,
            ResponseStatusCodeGTE = where.ResponseStatusCodeGTE,
            ResponseStatusCodeLT = where.ResponseStatusCodeLT,
            ResponseStatusCodeLTE = where.ResponseStatusCodeLTE,
            ResponseStatusCodeIsNil = where.ResponseStatusCodeIsNil,
            ResponseStatusCodeNotNil = where.ResponseStatusCodeNotNil,
            Stream = where.Stream,
            StreamNEQ = where.StreamNEQ,
            MetricsLatencyMs = where.MetricsLatencyMs,
            MetricsLatencyMsNEQ = where.MetricsLatencyMsNEQ,
            MetricsLatencyMsIn = ToLongList(where.MetricsLatencyMsIn),
            MetricsLatencyMsNotIn = ToLongList(where.MetricsLatencyMsNotIn),
            MetricsLatencyMsGT = where.MetricsLatencyMsGT,
            MetricsLatencyMsGTE = where.MetricsLatencyMsGTE,
            MetricsLatencyMsLT = where.MetricsLatencyMsLT,
            MetricsLatencyMsLTE = where.MetricsLatencyMsLTE,
            MetricsLatencyMsIsNil = where.MetricsLatencyMsIsNil,
            MetricsLatencyMsNotNil = where.MetricsLatencyMsNotNil,
            MetricsFirstTokenLatencyMs = where.MetricsFirstTokenLatencyMs,
            MetricsFirstTokenLatencyMsNEQ = where.MetricsFirstTokenLatencyMsNEQ,
            MetricsFirstTokenLatencyMsIn = ToLongList(where.MetricsFirstTokenLatencyMsIn),
            MetricsFirstTokenLatencyMsNotIn = ToLongList(where.MetricsFirstTokenLatencyMsNotIn),
            MetricsFirstTokenLatencyMsGT = where.MetricsFirstTokenLatencyMsGT,
            MetricsFirstTokenLatencyMsGTE = where.MetricsFirstTokenLatencyMsGTE,
            MetricsFirstTokenLatencyMsLT = where.MetricsFirstTokenLatencyMsLT,
            MetricsFirstTokenLatencyMsLTE = where.MetricsFirstTokenLatencyMsLTE,
            MetricsFirstTokenLatencyMsIsNil = where.MetricsFirstTokenLatencyMsIsNil,
            MetricsFirstTokenLatencyMsNotNil = where.MetricsFirstTokenLatencyMsNotNil,
            MetricsReasoningDurationMs = where.MetricsReasoningDurationMs,
            MetricsReasoningDurationMsNEQ = where.MetricsReasoningDurationMsNEQ,
            MetricsReasoningDurationMsIn = ToLongList(where.MetricsReasoningDurationMsIn),
            MetricsReasoningDurationMsNotIn = ToLongList(where.MetricsReasoningDurationMsNotIn),
            MetricsReasoningDurationMsGT = where.MetricsReasoningDurationMsGT,
            MetricsReasoningDurationMsGTE = where.MetricsReasoningDurationMsGTE,
            MetricsReasoningDurationMsLT = where.MetricsReasoningDurationMsLT,
            MetricsReasoningDurationMsLTE = where.MetricsReasoningDurationMsLTE,
            MetricsReasoningDurationMsIsNil = where.MetricsReasoningDurationMsIsNil,
            MetricsReasoningDurationMsNotNil = where.MetricsReasoningDurationMsNotNil,
            RequestUrl = where.RequestUrl,
            RequestUrlNEQ = where.RequestUrlNEQ,
            RequestUrlIn = ToList(where.RequestUrlIn),
            RequestUrlNotIn = ToList(where.RequestUrlNotIn),
            RequestUrlGT = where.RequestUrlGT,
            RequestUrlGTE = where.RequestUrlGTE,
            RequestUrlLT = where.RequestUrlLT,
            RequestUrlLTE = where.RequestUrlLTE,
            RequestUrlContains = where.RequestUrlContains,
            RequestUrlHasPrefix = where.RequestUrlHasPrefix,
            RequestUrlHasSuffix = where.RequestUrlHasSuffix,
            RequestUrlIsNil = where.RequestUrlIsNil,
            RequestUrlNotNil = where.RequestUrlNotNil,
            RequestUrlEqualFold = where.RequestUrlEqualFold,
            RequestUrlContainsFold = where.RequestUrlContainsFold,
            PassThroughApplied = where.PassThroughApplied,
            PassThroughAppliedNEQ = where.PassThroughAppliedNEQ,
            HasRequest = where.HasRequest,
            HasRequestWith = where.HasRequestWith?
                .Select(static nested => TraceMapping.ToFilter(nested))
                .OfType<RequestListFilter>()
                .ToList(),
            HasChannel = where.HasChannel,
            HasChannelWith = where.HasChannelWith?
                .Select(static nested => ChannelMapping.ToFilter(nested))
                .OfType<ChannelListFilter>()
                .ToList(),
            HasDataStorage = where.HasDataStorage,
            HasDataStorageWith = where.HasDataStorageWith?
                .Select(static nested => DataStorageMapping.ToFilter(nested))
                .OfType<DataStorageListFilter>()
                .ToList(),

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
            ApiKeyIdGT = where.ApiKeyIdGT,
            ApiKeyIdGTE = where.ApiKeyIdGTE,
            ApiKeyIdLT = where.ApiKeyIdLT,
            ApiKeyIdLTE = where.ApiKeyIdLTE,
            ModelIdGT = where.ModelIdGT,
            ModelIdGTE = where.ModelIdGTE,
            ModelIdLT = where.ModelIdLT,
            ModelIdLTE = where.ModelIdLTE,
            ModelIdHasPrefix = where.ModelIdHasPrefix,
            ModelIdHasSuffix = where.ModelIdHasSuffix,
            ModelIdEqualFold = where.ModelIdEqualFold,
            PromptTokens = where.PromptTokens,
            PromptTokensNEQ = where.PromptTokensNEQ,
            PromptTokensIn = ToLongList(where.PromptTokensIn),
            PromptTokensNotIn = ToLongList(where.PromptTokensNotIn),
            PromptTokensGT = where.PromptTokensGT,
            PromptTokensGTE = where.PromptTokensGTE,
            PromptTokensLT = where.PromptTokensLT,
            PromptTokensLTE = where.PromptTokensLTE,
            CompletionTokens = where.CompletionTokens,
            CompletionTokensNEQ = where.CompletionTokensNEQ,
            CompletionTokensIn = ToLongList(where.CompletionTokensIn),
            CompletionTokensNotIn = ToLongList(where.CompletionTokensNotIn),
            CompletionTokensGT = where.CompletionTokensGT,
            CompletionTokensGTE = where.CompletionTokensGTE,
            CompletionTokensLT = where.CompletionTokensLT,
            CompletionTokensLTE = where.CompletionTokensLTE,
            TotalTokens = where.TotalTokens,
            TotalTokensNEQ = where.TotalTokensNEQ,
            TotalTokensIn = ToLongList(where.TotalTokensIn),
            TotalTokensNotIn = ToLongList(where.TotalTokensNotIn),
            TotalTokensGT = where.TotalTokensGT,
            TotalTokensGTE = where.TotalTokensGTE,
            TotalTokensLT = where.TotalTokensLT,
            TotalTokensLTE = where.TotalTokensLTE,
            PromptAudioTokens = where.PromptAudioTokens,
            PromptAudioTokensNEQ = where.PromptAudioTokensNEQ,
            PromptAudioTokensIn = ToLongList(where.PromptAudioTokensIn),
            PromptAudioTokensNotIn = ToLongList(where.PromptAudioTokensNotIn),
            PromptAudioTokensGT = where.PromptAudioTokensGT,
            PromptAudioTokensGTE = where.PromptAudioTokensGTE,
            PromptAudioTokensLT = where.PromptAudioTokensLT,
            PromptAudioTokensLTE = where.PromptAudioTokensLTE,
            PromptAudioTokensIsNil = where.PromptAudioTokensIsNil,
            PromptAudioTokensNotNil = where.PromptAudioTokensNotNil,
            PromptCachedTokens = where.PromptCachedTokens,
            PromptCachedTokensNEQ = where.PromptCachedTokensNEQ,
            PromptCachedTokensIn = ToLongList(where.PromptCachedTokensIn),
            PromptCachedTokensNotIn = ToLongList(where.PromptCachedTokensNotIn),
            PromptCachedTokensGT = where.PromptCachedTokensGT,
            PromptCachedTokensGTE = where.PromptCachedTokensGTE,
            PromptCachedTokensLT = where.PromptCachedTokensLT,
            PromptCachedTokensLTE = where.PromptCachedTokensLTE,
            PromptCachedTokensIsNil = where.PromptCachedTokensIsNil,
            PromptCachedTokensNotNil = where.PromptCachedTokensNotNil,
            PromptWriteCachedTokens = where.PromptWriteCachedTokens,
            PromptWriteCachedTokensNEQ = where.PromptWriteCachedTokensNEQ,
            PromptWriteCachedTokensIn = ToLongList(where.PromptWriteCachedTokensIn),
            PromptWriteCachedTokensNotIn = ToLongList(where.PromptWriteCachedTokensNotIn),
            PromptWriteCachedTokensGT = where.PromptWriteCachedTokensGT,
            PromptWriteCachedTokensGTE = where.PromptWriteCachedTokensGTE,
            PromptWriteCachedTokensLT = where.PromptWriteCachedTokensLT,
            PromptWriteCachedTokensLTE = where.PromptWriteCachedTokensLTE,
            PromptWriteCachedTokensIsNil = where.PromptWriteCachedTokensIsNil,
            PromptWriteCachedTokensNotNil = where.PromptWriteCachedTokensNotNil,
            PromptWriteCachedTokens5m = where.PromptWriteCachedTokens5m,
            PromptWriteCachedTokens5mNEQ = where.PromptWriteCachedTokens5mNEQ,
            PromptWriteCachedTokens5mIn = ToLongList(where.PromptWriteCachedTokens5mIn),
            PromptWriteCachedTokens5mNotIn = ToLongList(where.PromptWriteCachedTokens5mNotIn),
            PromptWriteCachedTokens5mGT = where.PromptWriteCachedTokens5mGT,
            PromptWriteCachedTokens5mGTE = where.PromptWriteCachedTokens5mGTE,
            PromptWriteCachedTokens5mLT = where.PromptWriteCachedTokens5mLT,
            PromptWriteCachedTokens5mLTE = where.PromptWriteCachedTokens5mLTE,
            PromptWriteCachedTokens5mIsNil = where.PromptWriteCachedTokens5mIsNil,
            PromptWriteCachedTokens5mNotNil = where.PromptWriteCachedTokens5mNotNil,
            PromptWriteCachedTokens1h = where.PromptWriteCachedTokens1h,
            PromptWriteCachedTokens1hNEQ = where.PromptWriteCachedTokens1hNEQ,
            PromptWriteCachedTokens1hIn = ToLongList(where.PromptWriteCachedTokens1hIn),
            PromptWriteCachedTokens1hNotIn = ToLongList(where.PromptWriteCachedTokens1hNotIn),
            PromptWriteCachedTokens1hGT = where.PromptWriteCachedTokens1hGT,
            PromptWriteCachedTokens1hGTE = where.PromptWriteCachedTokens1hGTE,
            PromptWriteCachedTokens1hLT = where.PromptWriteCachedTokens1hLT,
            PromptWriteCachedTokens1hLTE = where.PromptWriteCachedTokens1hLTE,
            PromptWriteCachedTokens1hIsNil = where.PromptWriteCachedTokens1hIsNil,
            PromptWriteCachedTokens1hNotNil = where.PromptWriteCachedTokens1hNotNil,
            CompletionAudioTokens = where.CompletionAudioTokens,
            CompletionAudioTokensNEQ = where.CompletionAudioTokensNEQ,
            CompletionAudioTokensIn = ToLongList(where.CompletionAudioTokensIn),
            CompletionAudioTokensNotIn = ToLongList(where.CompletionAudioTokensNotIn),
            CompletionAudioTokensGT = where.CompletionAudioTokensGT,
            CompletionAudioTokensGTE = where.CompletionAudioTokensGTE,
            CompletionAudioTokensLT = where.CompletionAudioTokensLT,
            CompletionAudioTokensLTE = where.CompletionAudioTokensLTE,
            CompletionAudioTokensIsNil = where.CompletionAudioTokensIsNil,
            CompletionAudioTokensNotNil = where.CompletionAudioTokensNotNil,
            CompletionReasoningTokens = where.CompletionReasoningTokens,
            CompletionReasoningTokensNEQ = where.CompletionReasoningTokensNEQ,
            CompletionReasoningTokensIn = ToLongList(where.CompletionReasoningTokensIn),
            CompletionReasoningTokensNotIn = ToLongList(where.CompletionReasoningTokensNotIn),
            CompletionReasoningTokensGT = where.CompletionReasoningTokensGT,
            CompletionReasoningTokensGTE = where.CompletionReasoningTokensGTE,
            CompletionReasoningTokensLT = where.CompletionReasoningTokensLT,
            CompletionReasoningTokensLTE = where.CompletionReasoningTokensLTE,
            CompletionReasoningTokensIsNil = where.CompletionReasoningTokensIsNil,
            CompletionReasoningTokensNotNil = where.CompletionReasoningTokensNotNil,
            CompletionAcceptedPredictionTokens = where.CompletionAcceptedPredictionTokens,
            CompletionAcceptedPredictionTokensNEQ = where.CompletionAcceptedPredictionTokensNEQ,
            CompletionAcceptedPredictionTokensIn = ToLongList(where.CompletionAcceptedPredictionTokensIn),
            CompletionAcceptedPredictionTokensNotIn = ToLongList(where.CompletionAcceptedPredictionTokensNotIn),
            CompletionAcceptedPredictionTokensGT = where.CompletionAcceptedPredictionTokensGT,
            CompletionAcceptedPredictionTokensGTE = where.CompletionAcceptedPredictionTokensGTE,
            CompletionAcceptedPredictionTokensLT = where.CompletionAcceptedPredictionTokensLT,
            CompletionAcceptedPredictionTokensLTE = where.CompletionAcceptedPredictionTokensLTE,
            CompletionAcceptedPredictionTokensIsNil = where.CompletionAcceptedPredictionTokensIsNil,
            CompletionAcceptedPredictionTokensNotNil = where.CompletionAcceptedPredictionTokensNotNil,
            CompletionRejectedPredictionTokens = where.CompletionRejectedPredictionTokens,
            CompletionRejectedPredictionTokensNEQ = where.CompletionRejectedPredictionTokensNEQ,
            CompletionRejectedPredictionTokensIn = ToLongList(where.CompletionRejectedPredictionTokensIn),
            CompletionRejectedPredictionTokensNotIn = ToLongList(where.CompletionRejectedPredictionTokensNotIn),
            CompletionRejectedPredictionTokensGT = where.CompletionRejectedPredictionTokensGT,
            CompletionRejectedPredictionTokensGTE = where.CompletionRejectedPredictionTokensGTE,
            CompletionRejectedPredictionTokensLT = where.CompletionRejectedPredictionTokensLT,
            CompletionRejectedPredictionTokensLTE = where.CompletionRejectedPredictionTokensLTE,
            CompletionRejectedPredictionTokensIsNil = where.CompletionRejectedPredictionTokensIsNil,
            CompletionRejectedPredictionTokensNotNil = where.CompletionRejectedPredictionTokensNotNil,
            FormatGT = where.FormatGT,
            FormatGTE = where.FormatGTE,
            FormatLT = where.FormatLT,
            FormatLTE = where.FormatLTE,
            FormatContains = where.FormatContains,
            FormatHasPrefix = where.FormatHasPrefix,
            FormatHasSuffix = where.FormatHasSuffix,
            FormatEqualFold = where.FormatEqualFold,
            FormatContainsFold = where.FormatContainsFold,
            TotalCost = where.TotalCost,
            TotalCostNEQ = where.TotalCostNEQ,
            TotalCostIn = ToList(where.TotalCostIn),
            TotalCostNotIn = ToList(where.TotalCostNotIn),
            TotalCostGT = where.TotalCostGT,
            TotalCostGTE = where.TotalCostGTE,
            TotalCostLT = where.TotalCostLT,
            TotalCostLTE = where.TotalCostLTE,
            TotalCostIsNil = where.TotalCostIsNil,
            TotalCostNotNil = where.TotalCostNotNil,
            CostPriceReferenceId = where.CostPriceReferenceId,
            CostPriceReferenceIdNEQ = where.CostPriceReferenceIdNEQ,
            CostPriceReferenceIdIn = ToList(where.CostPriceReferenceIdIn),
            CostPriceReferenceIdNotIn = ToList(where.CostPriceReferenceIdNotIn),
            CostPriceReferenceIdGT = where.CostPriceReferenceIdGT,
            CostPriceReferenceIdGTE = where.CostPriceReferenceIdGTE,
            CostPriceReferenceIdLT = where.CostPriceReferenceIdLT,
            CostPriceReferenceIdLTE = where.CostPriceReferenceIdLTE,
            CostPriceReferenceIdContains = where.CostPriceReferenceIdContains,
            CostPriceReferenceIdHasPrefix = where.CostPriceReferenceIdHasPrefix,
            CostPriceReferenceIdHasSuffix = where.CostPriceReferenceIdHasSuffix,
            CostPriceReferenceIdIsNil = where.CostPriceReferenceIdIsNil,
            CostPriceReferenceIdNotNil = where.CostPriceReferenceIdNotNil,
            CostPriceReferenceIdEqualFold = where.CostPriceReferenceIdEqualFold,
            CostPriceReferenceIdContainsFold = where.CostPriceReferenceIdContainsFold,
            HasRequest = where.HasRequest,
            HasRequestWith = where.HasRequestWith?
                .Select(static nested => TraceMapping.ToFilter(nested))
                .OfType<RequestListFilter>()
                .ToList(),
            HasProject = where.HasProject,
            HasProjectWith = where.HasProjectWith?
                .Select(static nested => ProjectMapping.ToFilter(nested))
                .OfType<ProjectListFilter>()
                .ToList(),
            HasChannel = where.HasChannel,
            HasChannelWith = where.HasChannelWith?
                .Select(static nested => ChannelMapping.ToFilter(nested))
                .OfType<ChannelListFilter>()
                .ToList(),

            Not = ToFilter(where.Not),
            And = ToList(where.And?.Select(ToFilter).OfType<UsageLogListFilter>().ToList()),
            Or = ToList(where.Or?.Select(ToFilter).OfType<UsageLogListFilter>().ToList()),
        };
    }

    /// <summary>
    /// Parses the raw chunk texts returned by the storage loaders; the
    /// sanitize/external-marker collapsing happens in RequestPayloadStorage
    /// (request.go:143-151).
    /// </summary>
    public static IReadOnlyList<JsonElement> ParseChunks(IReadOnlyList<string> chunks)
        => chunks.Select(static chunk => ParseJson(chunk)).ToList();

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
