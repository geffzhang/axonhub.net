using System.Text.Json;
using HotChocolate;
using HotChocolate.Types;
using AxonHub.Api.Management;
using AxonHub.Api.Traces;
using AxonHub.Persistence;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the TraceStatus enum (ent.graphql:6053-6058).</summary>
[GraphQLName("TraceStatus")]
public enum TraceStatusValue
{
    [GraphQLName("active")]
    Active,

    [GraphQLName("archived")]
    Archived,

    [GraphQLName("retained")]
    Retained,
}

/// <summary>Port of the ThreadStatus enum (ent.graphql:5905-5910).</summary>
[GraphQLName("ThreadStatus")]
public enum ThreadStatusValue
{
    [GraphQLName("active")]
    Active,

    [GraphQLName("archived")]
    Archived,

    [GraphQLName("retained")]
    Retained,
}

/// <summary>Port of the RequestStatus enum (ent.graphql:5083-5091).</summary>
[GraphQLName("RequestStatus")]
public enum RequestStatusValue
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

/// <summary>Port of the RequestSource enum (ent.graphql:5075-5081).</summary>
[GraphQLName("RequestSource")]
public enum RequestSourceValue
{
    [GraphQLName("api")]
    Api,

    [GraphQLName("playground")]
    Playground,

    [GraphQLName("test")]
    Test,
}

/// <summary>Port of TraceOrderField (ent.graphql:6033-6038).</summary>
[GraphQLName("TraceOrderField")]
public enum TraceOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,
}

/// <summary>Port of ThreadOrderField (ent.graphql:5885-5890).</summary>
[GraphQLName("ThreadOrderField")]
public enum ThreadOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,
}

/// <summary>Port of RequestOrderField (ent.graphql:5070-5073).</summary>
[GraphQLName("RequestOrderField")]
public enum RequestOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,
}

/// <summary>Port of TraceOrder (ent.graphql:6015-6029).</summary>
[GraphQLName("TraceOrder")]
[InputObjectType("TraceOrder")]
public sealed class TraceOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    public TraceOrderFieldValue Field { get; set; }
}

/// <summary>Port of ThreadOrder (ent.graphql:5873-5883).</summary>
[GraphQLName("ThreadOrder")]
[InputObjectType("ThreadOrder")]
public sealed class ThreadOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    public ThreadOrderFieldValue Field { get; set; }
}

/// <summary>Port of RequestOrder (ent.graphql:5057-5066).</summary>
[GraphQLName("RequestOrder")]
[InputObjectType("RequestOrder")]
public sealed class RequestOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    [GraphQLNonNullType]
    public RequestOrderFieldValue Field { get; set; }
}

/// <summary>Port of <c>TraceWhereInput</c> (ent.graphql:6060-6180).</summary>
[GraphQLName("TraceWhereInput")]
public sealed class TraceWhereInput
{
    public TraceWhereInput? Not { get; set; }

    public IReadOnlyList<TraceWhereInput>? And { get; set; }

    public IReadOnlyList<TraceWhereInput>? Or { get; set; }

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

    [GraphQLName("traceID")]
    public string? TraceId { get; set; }

    [GraphQLName("traceIDNEQ")]
    public string? TraceIdNEQ { get; set; }

    [GraphQLName("traceIDIn")]
    public IReadOnlyList<string>? TraceIdIn { get; set; }

    [GraphQLName("traceIDNotIn")]
    public IReadOnlyList<string>? TraceIdNotIn { get; set; }

    [GraphQLName("traceIDGT")]
    public string? TraceIdGT { get; set; }

    [GraphQLName("traceIDGTE")]
    public string? TraceIdGTE { get; set; }

    [GraphQLName("traceIDLT")]
    public string? TraceIdLT { get; set; }

    [GraphQLName("traceIDLTE")]
    public string? TraceIdLTE { get; set; }

    [GraphQLName("traceIDContains")]
    public string? TraceIdContains { get; set; }

    [GraphQLName("traceIDHasPrefix")]
    public string? TraceIdHasPrefix { get; set; }

    [GraphQLName("traceIDHasSuffix")]
    public string? TraceIdHasSuffix { get; set; }

    [GraphQLName("traceIDEqualFold")]
    public string? TraceIdEqualFold { get; set; }

    [GraphQLName("traceIDContainsFold")]
    public string? TraceIdContainsFold { get; set; }

    [GraphQLName("threadID")]
    [GraphQLType(typeof(IdType))]
    public string? ThreadId { get; set; }

    [GraphQLName("threadIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? ThreadIdNEQ { get; set; }

    [GraphQLName("threadIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ThreadIdIn { get; set; }

    [GraphQLName("threadIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ThreadIdNotIn { get; set; }

    [GraphQLName("threadIDIsNil")]
    public bool? ThreadIdIsNil { get; set; }

    [GraphQLName("threadIDNotNil")]
    public bool? ThreadIdNotNil { get; set; }

    public TraceStatusValue? Status { get; set; }

    public TraceStatusValue? StatusNEQ { get; set; }

    public IReadOnlyList<TraceStatusValue>? StatusIn { get; set; }

    public IReadOnlyList<TraceStatusValue>? StatusNotIn { get; set; }

    public bool? HasProject { get; set; }

    public IReadOnlyList<ProjectWhereInput>? HasProjectWith { get; set; }

    public bool? HasThread { get; set; }

    public IReadOnlyList<ThreadWhereInput>? HasThreadWith { get; set; }

    public bool? HasRequests { get; set; }

    public IReadOnlyList<RequestWhereInput>? HasRequestsWith { get; set; }
}

/// <summary>Port of <c>ThreadWhereInput</c> (ent.graphql:5912-6000).</summary>
[GraphQLName("ThreadWhereInput")]
public sealed class ThreadWhereInput
{
    public ThreadWhereInput? Not { get; set; }

    public IReadOnlyList<ThreadWhereInput>? And { get; set; }

    public IReadOnlyList<ThreadWhereInput>? Or { get; set; }

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

    [GraphQLName("threadID")]
    public string? ThreadId { get; set; }

    [GraphQLName("threadIDNEQ")]
    public string? ThreadIdNEQ { get; set; }

    [GraphQLName("threadIDIn")]
    public IReadOnlyList<string>? ThreadIdIn { get; set; }

    [GraphQLName("threadIDNotIn")]
    public IReadOnlyList<string>? ThreadIdNotIn { get; set; }

    [GraphQLName("threadIDGT")]
    public string? ThreadIdGT { get; set; }

    [GraphQLName("threadIDGTE")]
    public string? ThreadIdGTE { get; set; }

    [GraphQLName("threadIDLT")]
    public string? ThreadIdLT { get; set; }

    [GraphQLName("threadIDLTE")]
    public string? ThreadIdLTE { get; set; }

    [GraphQLName("threadIDContains")]
    public string? ThreadIdContains { get; set; }

    [GraphQLName("threadIDHasPrefix")]
    public string? ThreadIdHasPrefix { get; set; }

    [GraphQLName("threadIDHasSuffix")]
    public string? ThreadIdHasSuffix { get; set; }

    [GraphQLName("threadIDEqualFold")]
    public string? ThreadIdEqualFold { get; set; }

    [GraphQLName("threadIDContainsFold")]
    public string? ThreadIdContainsFold { get; set; }

    public ThreadStatusValue? Status { get; set; }

    public ThreadStatusValue? StatusNEQ { get; set; }

    public IReadOnlyList<ThreadStatusValue>? StatusIn { get; set; }

    public IReadOnlyList<ThreadStatusValue>? StatusNotIn { get; set; }

    public bool? HasProject { get; set; }

    public IReadOnlyList<ProjectWhereInput>? HasProjectWith { get; set; }

    public bool? HasTraces { get; set; }

    public IReadOnlyList<TraceWhereInput>? HasTracesWith { get; set; }
}

/// <summary>
/// Port of the subset of <c>RequestWhereInput</c> the trace detail surface can
/// send (id, time, projectID and status predicates plus and/or/not). The
/// remaining entgql predicates are not ported in this slice (C165).
/// </summary>
[GraphQLName("RequestWhereInput")]
public sealed partial class RequestWhereInput
{
    public RequestWhereInput? Not { get; set; }

    public IReadOnlyList<RequestWhereInput>? And { get; set; }

    public IReadOnlyList<RequestWhereInput>? Or { get; set; }

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

    public RequestStatusValue? Status { get; set; }

    public RequestStatusValue? StatusNEQ { get; set; }

    public IReadOnlyList<RequestStatusValue>? StatusIn { get; set; }

    public IReadOnlyList<RequestStatusValue>? StatusNotIn { get; set; }
}

/// <summary>Port of the <c>Trace</c> object type (ent.graphql:5945-6002).</summary>
[GraphQLName("Trace")]
public sealed class TraceType : INode
{
    [GraphQLIgnore]
    public TraceRecord Record { get; init; } = null!;

    [GraphQLIgnore]
    public long DbId => Record.Id;

    [GraphQLType(typeof(IdType))]
    public string Id => new GidValue("Trace", Record.Id).ToString();

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt => Record.CreatedAt;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt => Record.UpdatedAt;

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string ProjectID => new GidValue("Project", Record.ProjectId).ToString();

    [GraphQLName("traceID")]
    public string TraceID => Record.TraceId;

    /// <summary>
    /// The reference column is a non-pointer int, so the resolver always
    /// returns a GUID and an absent thread serializes as Thread/0 (C190).
    /// </summary>
    [GraphQLName("threadID")]
    [GraphQLType(typeof(IdType))]
    public string? ThreadID => new GidValue("Thread", Record.ThreadId ?? 0).ToString();

    public TraceStatusValue Status => TraceMapping.ToGraphQLTraceStatus(Record.Status);

    /// <summary>Port of the entgql Project edge (gql_edge.go:652-658).</summary>
    [GraphQLName("project")]
    public async Task<ProjectType> GetProjectAsync(
        [Service] ApiKeyPrivacy privacy,
        CancellationToken cancellationToken)
    {
        var project = await privacy.GetVisibleProjectAsync(Record.ProjectId, cancellationToken)
            .ConfigureAwait(false);
        return project is null
            ? throw new InvalidOperationException("ent: project not found")
            : UserProfileMapping.ToGraphQL(project);
    }

    /// <summary>
    /// Port of the entgql Thread edge (gql_edge.go:660-668): the Thread query
    /// policy applies and <c>MaskNotFound</c> turns a missing row into null.
    /// </summary>
    [GraphQLName("thread")]
    public async Task<ThreadType?> GetThreadAsync(
        [Service] ThreadManagementService threads,
        CancellationToken cancellationToken)
    {
        var thread = await threads.GetTraceThreadAsync(Record.ThreadId ?? 0, cancellationToken)
            .ConfigureAwait(false);
        return thread is null ? null : TraceMapping.ToGraphQL(thread);
    }

    /// <summary>Port of the entgql Request connection (gql_edge.go:670-688).</summary>
    [GraphQLName("requests")]
    public async Task<RequestConnectionType> GetRequestsAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        RequestOrder? orderBy,
        RequestWhereInput? where,
        [Service] RequestManagementService requests,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        var sortField = TraceMapping.ToEdgeRequestSortField(orderBy);
        var page = await requests.ListAsync(
                new RequestListQuery
                {
                    Where = TraceMapping.ToFilter(where),
                    TraceScope = Record.Id,
                    SortField = sortField,
                    Direction = TraceMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return TraceMapping.BuildConnection(page, sortField);
    }

    /// <summary>Port of the rootSegment resolver (axonhub.resolvers.go:1044-1047).</summary>
    [GraphQLName("rootSegment")]
    public async Task<SegmentType?> GetRootSegmentAsync(
        [Service] TraceManagementService traces,
        CancellationToken cancellationToken)
    {
        var segment = await traces.GetRootSegmentAsync(Record.Id, cancellationToken)
            .ConfigureAwait(false);
        return segment is null ? null : TraceMapping.ToGraphQL(segment);
    }

    /// <summary>Port of the rawRootSegment resolver (axonhub.resolvers.go:1049-1062).</summary>
    [GraphQLName("rawRootSegment")]
    [GraphQLType(typeof(JsonRawMessageType))]
    public async Task<JsonElement?> GetRawRootSegmentAsync(
        [Service] TraceManagementService traces,
        CancellationToken cancellationToken)
        => await traces.GetRawRootSegmentAsync(Record.Id, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Port of the firstUserQuery resolver (axonhub.resolvers.go:1064-1067).</summary>
    [GraphQLName("firstUserQuery")]
    public async Task<string?> GetFirstUserQueryAsync(
        [Service] TraceManagementService traces,
        CancellationToken cancellationToken)
        => await traces.GetFirstUserQueryAsync(Record.Id, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Port of the firstText resolver (axonhub.resolvers.go:1069-1072).</summary>
    [GraphQLName("firstText")]
    public async Task<string?> GetFirstTextAsync(
        [Service] TraceManagementService traces,
        CancellationToken cancellationToken)
        => await traces.GetFirstTextAsync(Record.Id, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Port of the usageMetadata resolver (axonhub.resolvers.go:1074-1077).</summary>
    [GraphQLName("usageMetadata")]
    public async Task<UsageMetadataType?> GetUsageMetadataAsync(
        [Service] TraceManagementService traces,
        CancellationToken cancellationToken)
        => TraceMapping.ToGraphQL(
            await traces.GetUsageMetadataAsync(Record.Id, cancellationToken)
                .ConfigureAwait(false));
}

/// <summary>Port of the <c>Thread</c> object type (ent.graphql:5747-5800).</summary>
[GraphQLName("Thread")]
public sealed class ThreadType : INode
{
    [GraphQLIgnore]
    public ThreadRecord Record { get; init; } = null!;

    [GraphQLIgnore]
    public long DbId => Record.Id;

    [GraphQLType(typeof(IdType))]
    public string Id => new GidValue("Thread", Record.Id).ToString();

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt => Record.CreatedAt;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt => Record.UpdatedAt;

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string ProjectID => new GidValue("Project", Record.ProjectId).ToString();

    [GraphQLName("threadID")]
    public string ThreadID => Record.ThreadId;

    public ThreadStatusValue Status => TraceMapping.ToGraphQLThreadStatus(Record.Status);

    /// <summary>Port of the entgql Project edge (gql_edge.go:626-632).</summary>
    [GraphQLName("project")]
    public async Task<ProjectType> GetProjectAsync(
        [Service] ApiKeyPrivacy privacy,
        CancellationToken cancellationToken)
    {
        var project = await privacy.GetVisibleProjectAsync(Record.ProjectId, cancellationToken)
            .ConfigureAwait(false);
        return project is null
            ? throw new InvalidOperationException("ent: project not found")
            : UserProfileMapping.ToGraphQL(project);
    }

    /// <summary>Port of the entgql Trace connection (gql_edge.go:634-650).</summary>
    [GraphQLName("traces")]
    public async Task<TraceConnectionType> GetTracesAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        TraceOrder? orderBy,
        TraceWhereInput? where,
        [Service] TraceManagementService traces,
        CancellationToken cancellationToken)
    {
        ManagementValidation.ValidateFirstLast(first, last);

        var sortField = TraceMapping.ToEdgeTraceSortField(orderBy);
        var page = await traces.ListAsync(
                new TraceListQuery
                {
                    Where = TraceMapping.ToFilter(where),
                    ThreadScope = Record.Id,
                    SortField = sortField,
                    Direction = TraceMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return TraceMapping.BuildConnection(page, sortField);
    }

    /// <summary>Port of the firstUserQuery resolver (axonhub.resolvers.go:1009-1012).</summary>
    [GraphQLName("firstUserQuery")]
    public async Task<string?> GetFirstUserQueryAsync(
        [Service] ThreadManagementService threads,
        CancellationToken cancellationToken)
        => await threads.GetFirstUserQueryAsync(Record.Id, cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Port of the usageMetadata resolver (axonhub.resolvers.go:1014-1017).</summary>
    [GraphQLName("usageMetadata")]
    public async Task<UsageMetadataType?> GetUsageMetadataAsync(
        [Service] ThreadManagementService threads,
        CancellationToken cancellationToken)
        => TraceMapping.ToGraphQL(
            await threads.GetUsageMetadataAsync(Record.Id, cancellationToken)
                .ConfigureAwait(false));

    /// <summary>Port of the archivedTracesCount resolver (axonhub.resolvers.go:1019-1032).</summary>
    [GraphQLName("archivedTracesCount")]
    public async Task<int> GetArchivedTracesCountAsync(
        [Service] ThreadManagementService threads,
        CancellationToken cancellationToken)
        => checked((int)await threads.GetArchivedTracesCountAsync(
                Record.Id,
                cancellationToken)
            .ConfigureAwait(false));
}

/// <summary>Port of <c>TraceEdge</c> (ent.graphql:6006-6017).</summary>
[GraphQLName("TraceEdge")]
public sealed class TraceEdgeType
{
    public TraceType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of <c>TraceConnection</c> (ent.graphql:5989-6002).</summary>
[GraphQLName("TraceConnection")]
public sealed class TraceConnectionType
{
    public IReadOnlyList<TraceEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>Port of <c>ThreadEdge</c> (ent.graphql:5849-5860).</summary>
[GraphQLName("ThreadEdge")]
public sealed class ThreadEdgeType
{
    public ThreadType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of <c>ThreadConnection</c> (ent.graphql:5832-5845).</summary>
[GraphQLName("ThreadConnection")]
public sealed class ThreadConnectionType
{
    public IReadOnlyList<ThreadEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>Port of <c>RequestEdge</c> (ent.graphql:5044-5055).</summary>
[GraphQLName("RequestEdge")]
public sealed class RequestEdgeType
{
    public RequestType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of <c>RequestConnection</c> (ent.graphql:5027-5040).</summary>
[GraphQLName("RequestConnection")]
public sealed class RequestConnectionType
{
    public IReadOnlyList<RequestEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>
/// Port of the <c>Request</c> type (ent.graphql:4477-4606). The scalar fields
/// the trace/thread detail surface reaches live here; the body fields, edges
/// and connections are added by <c>RequestTypes.cs</c>.
/// </summary>
[GraphQLName("Request")]
public sealed partial class RequestType : INode
{
    [GraphQLIgnore]
    public RequestRecord Record { get; init; } = null!;

    [GraphQLIgnore]
    public long DbId => Record.Id;

    [GraphQLType(typeof(IdType))]
    public string Id => new GidValue("Request", Record.Id).ToString();

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt => Record.CreatedAt;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt => Record.UpdatedAt;

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string ProjectID => new GidValue("Project", Record.ProjectId).ToString();

    /// <summary>
    /// The reference column is a non-pointer int, so the resolver always
    /// returns a GUID and an absent key serializes as APIKey/0 (C190).
    /// </summary>
    [GraphQLName("apiKeyID")]
    [GraphQLType(typeof(IdType))]
    public string? APIKeyID => new GidValue("APIKey", Record.ApiKeyId ?? 0).ToString();

    /// <summary>Trace/0 when the request was not linked to a trace (C190).</summary>
    [GraphQLName("traceID")]
    [GraphQLType(typeof(IdType))]
    public string? TraceID => new GidValue("Trace", Record.TraceId ?? 0).ToString();

    /// <summary>Unlike the other ID resolvers, a zero id becomes null (ent.resolvers.go:637-647).</summary>
    [GraphQLName("dataStorageID")]
    [GraphQLType(typeof(IdType))]
    public string? DataStorageID => Record.DataStorageId is { } dataStorageId
        && dataStorageId != 0
            ? new GidValue("DataStorage", dataStorageId).ToString()
            : null;

    public RequestSourceValue Source => TraceMapping.ToGraphQLSource(Record.Source);

    [GraphQLName("modelID")]
    public string ModelID => Record.ModelId;

    public string? ReasoningEffort => Record.ReasoningEffort;

    public string Format => Record.Format;

    public RequestStatusValue Status => TraceMapping.ToGraphQLRequestStatus(Record.Status);

    public bool Stream => Record.Stream;

    [GraphQLName("clientIP")]
    public string ClientIP => Record.ClientIp;

    public string UserAgent => Record.UserAgent;

    public int? MetricsLatencyMs => TraceMapping.ToGraphQLInt(Record.MetricsLatencyMs);

    public int? MetricsFirstTokenLatencyMs
        => TraceMapping.ToGraphQLInt(Record.MetricsFirstTokenLatencyMs);

    public int? MetricsReasoningDurationMs
        => TraceMapping.ToGraphQLInt(Record.MetricsReasoningDurationMs);
}

/// <summary>Port of <c>Segment</c> (axonhub.graphql:909-921).</summary>
[GraphQLName("Segment")]
public sealed class SegmentType
{
    [GraphQLIgnore]
    public TraceSegment Segment { get; init; } = null!;

    [GraphQLType(typeof(IdType))]
    public string Id => new GidValue("Request", Segment.Id).ToString();

    [GraphQLType(typeof(IdType))]
    public string? ParentId => Segment.ParentId is { } parentId
        ? new GidValue("Request", parentId).ToString()
        : null;

    public string Model => Segment.Model;

    public IReadOnlyList<SegmentType>? Children
        => Segment.Children?.Select(TraceMapping.ToGraphQL).ToList();

    public IReadOnlyList<SpanType>? RequestSpans
        => Segment.RequestSpans?.Select(TraceMapping.ToGraphQL).ToList();

    public IReadOnlyList<SpanType>? ResponseSpans
        => Segment.ResponseSpans?.Select(TraceMapping.ToGraphQL).ToList();

    public RequestMetadataType? Metadata
        => Segment.Metadata is { } metadata
            ? TraceMapping.ToGraphQL(metadata)
            : null;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime StartTime => Segment.StartTime;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime EndTime => Segment.EndTime;

    /// <summary>The Go field is int64; the SDL exposes a GraphQL Int.</summary>
    public int Duration => checked((int)Segment.Duration);
}

/// <summary>Port of <c>RequestMetadata</c> (axonhub.graphql:923-929).</summary>
[GraphQLName("RequestMetadata")]
public sealed class RequestMetadataType
{
    public int? ItemCount => Metadata.ItemCount;

    public int? InputTokens => TraceMapping.ToGraphQLInt(Metadata.InputTokens);

    public int? OutputTokens => TraceMapping.ToGraphQLInt(Metadata.OutputTokens);

    public int? TotalTokens => TraceMapping.ToGraphQLInt(Metadata.TotalTokens);

    public int? CachedTokens => TraceMapping.ToGraphQLInt(Metadata.CachedTokens);

    [GraphQLIgnore]
    public TraceRequestMetadata Metadata { get; init; } = null!;
}

/// <summary>Port of <c>Span</c> (axonhub.graphql:931-937).</summary>
[GraphQLName("Span")]
public sealed class SpanType
{
    [GraphQLIgnore]
    public TraceSpan Span { get; init; } = null!;

    public string Id => Span.Id;

    public string Type => Span.Type;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime StartTime => Span.StartTime;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime EndTime => Span.EndTime;

    public SpanValueType? Value
        => Span.Value is { } value ? TraceMapping.ToGraphQL(value) : null;
}

/// <summary>Port of <c>SpanValue</c> (axonhub.graphql:939-948).</summary>
[GraphQLName("SpanValue")]
public sealed class SpanValueType
{
    [GraphQLIgnore]
    public TraceSpanValue Value { get; init; } = null!;

    public SpanUserQueryType? UserQuery
        => Value.UserQuery is { } value
            ? new SpanUserQueryType { Text = value.Text }
            : null;

    public SpanUserImageURLType? UserImageURL
        => Value.UserImageURL is { } value
            ? new SpanUserImageURLType { Url = value.URL }
            : null;

    public SpanTextType? Text
        => Value.Text is { } value
            ? new SpanTextType { Text = value.Text }
            : null;

    public SpanThinkingType? Thinking
        => Value.Thinking is { } value
            ? new SpanThinkingType { Thinking = value.Thinking }
            : null;

    public SpanImageURLType? ImageURL
        => Value.ImageURL is { } value
            ? new SpanImageURLType { Url = value.URL }
            : null;

    public SpanToolUseType? ToolUse
        => Value.ToolUse is { } value
            ? new SpanToolUseType
            {
                Id = value.Id,
                Name = value.Name,
                Arguments = value.Arguments,
            }
            : null;

    public SpanToolResultType? ToolResult
        => Value.ToolResult is { } value
            ? new SpanToolResultType
            {
                ToolCallID = value.ToolCallId,
                IsError = value.IsError,
                Text = value.Text,
            }
            : null;

    public SpanSystemInstructionType? SystemInstruction
        => Value.SystemInstruction is { } value
            ? new SpanSystemInstructionType { Instruction = value.Instruction }
            : null;
}

[GraphQLName("SpanUserQuery")]
public sealed class SpanUserQueryType
{
    public string? Text { get; init; }
}

[GraphQLName("SpanUserImageURL")]
public sealed class SpanUserImageURLType
{
    public string? Url { get; init; }
}

[GraphQLName("SpanThinking")]
public sealed class SpanThinkingType
{
    public string? Thinking { get; init; }
}

[GraphQLName("SpanText")]
public sealed class SpanTextType
{
    public string? Text { get; init; }
}

[GraphQLName("SpanImageURL")]
public sealed class SpanImageURLType
{
    public string? Url { get; init; }
}

[GraphQLName("SpanToolUse")]
public sealed class SpanToolUseType
{
    public string? Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string? Arguments { get; init; }
}

[GraphQLName("SpanToolResult")]
public sealed class SpanToolResultType
{
    [GraphQLName("toolCallID")]
    public string? ToolCallID { get; init; }

    public bool? IsError { get; init; }

    public string? Text { get; init; }
}

[GraphQLName("SpanSystemInstruction")]
public sealed class SpanSystemInstructionType
{
    public string? Instruction { get; init; }
}

/// <summary>Port of <c>UsageMetadata</c> (axonhub.graphql:822-829).</summary>
[GraphQLName("UsageMetadata")]
public sealed class UsageMetadataType
{
    [GraphQLIgnore]
    public UsageMetadata Record { get; init; } = null!;

    public int TotalInputTokens => checked((int)Record.TotalInputTokens);

    public int TotalOutputTokens => checked((int)Record.TotalOutputTokens);

    public int TotalTokens => checked((int)Record.TotalTokens);

    public int TotalCachedTokens => checked((int)Record.TotalCachedTokens);

    public int TotalCachedWriteTokens => checked((int)Record.TotalCachedWriteTokens);

    [GraphQLType(typeof(GoDecimalType))]
    public decimal TotalCost => Record.TotalCost;
}

/// <summary>Domain ↔ GraphQL mapping for the trace/thread slice.</summary>
public static class TraceMapping
{
    public static TraceType ToGraphQL(TraceRecord record) => new() { Record = record };

    public static ThreadType ToGraphQL(ThreadRecord record) => new() { Record = record };

    public static SegmentType ToGraphQL(TraceSegment segment) => new() { Segment = segment };

    public static SpanType ToGraphQL(TraceSpan span) => new() { Span = span };

    public static SpanValueType ToGraphQL(TraceSpanValue value) => new() { Value = value };

    public static RequestMetadataType ToGraphQL(TraceRequestMetadata metadata)
        => new() { Metadata = metadata };

    public static RequestType ToGraphQL(RequestRecord record) => new() { Record = record };

    public static UsageMetadataType ToGraphQL(UsageMetadata record) => new() { Record = record };

    /// <summary>
    /// entgql's Request order: the default is <c>id</c>; CREATED_AT and
    /// UPDATED_AT order by their columns with the id tie-breaker.
    /// </summary>
    public static RequestSortField ToEdgeRequestSortField(RequestOrder? orderBy)
        => orderBy?.Field switch
        {
            RequestOrderFieldValue.UpdatedAt => RequestSortField.UpdatedAt,
            RequestOrderFieldValue.CreatedAt => RequestSortField.CreatedAt,
            _ => RequestSortField.Id,
        };

    /// <summary>
    /// The root Traces resolver rewrites CREATED_AT to
    /// ent.DefaultTraceOrder's field (id) before paginating
    /// (ent.resolvers.go:559-563); the Thread.traces edge does not.
    /// </summary>
    public static TraceSortField ToRootTraceSortField(TraceOrder? orderBy)
        => orderBy?.Field == TraceOrderFieldValue.UpdatedAt
            ? TraceSortField.UpdatedAt
            : TraceSortField.Id;

    /// <summary>The Thread.traces edge keeps CREATED_AT as the created_at column.</summary>
    public static TraceSortField ToEdgeTraceSortField(TraceOrder? orderBy)
        => orderBy?.Field switch
        {
            TraceOrderFieldValue.UpdatedAt => TraceSortField.UpdatedAt,
            TraceOrderFieldValue.CreatedAt => TraceSortField.CreatedAt,
            _ => TraceSortField.Id,
        };

    /// <summary>
    /// The root Threads resolver rewrites CREATED_AT to
    /// ent.DefaultThreadOrder's field (id) (ent.resolvers.go:543-547).
    /// </summary>
    public static ThreadSortField ToRootThreadSortField(ThreadOrder? orderBy)
        => orderBy?.Field == ThreadOrderFieldValue.UpdatedAt
            ? ThreadSortField.UpdatedAt
            : ThreadSortField.Id;

    /// <summary>The Project.threads edge keeps CREATED_AT as the created_at column.</summary>
    public static ThreadSortField ToEdgeThreadSortField(ThreadOrder? orderBy)
        => orderBy?.Field switch
        {
            ThreadOrderFieldValue.UpdatedAt => ThreadSortField.UpdatedAt,
            ThreadOrderFieldValue.CreatedAt => ThreadSortField.CreatedAt,
            _ => ThreadSortField.Id,
        };

    /// <summary>
    /// The root Requests resolver rewrites CREATED_AT to
    /// ent.DefaultRequestOrder's field (id) (ent.resolvers.go:497-505).
    /// </summary>
    public static RequestSortField ToRootRequestSortField(RequestOrder? orderBy)
        => orderBy?.Field == RequestOrderFieldValue.UpdatedAt
            ? RequestSortField.UpdatedAt
            : RequestSortField.Id;

    public static SortDirection ToSortDirection(TraceOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    public static SortDirection ToSortDirection(ThreadOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    public static SortDirection ToSortDirection(RequestOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    public static TraceConnectionType BuildConnection(
        ListPage<TraceRecord> page,
        TraceSortField sortField)
    {
        var edges = page.Nodes
            .Select(node => new TraceEdgeType
            {
                Node = ToGraphQL(node),
                Cursor = new CursorValue(
                    node.Id,
                    sortField switch
                    {
                        TraceSortField.CreatedAt => node.CreatedAt,
                        TraceSortField.UpdatedAt => node.UpdatedAt,
                        _ => null,
                    }),
            })
            .ToList();

        return new TraceConnectionType
        {
            Edges = edges,
            PageInfo = BuildPageInfo(
                edges.Select(static edge => edge.Cursor).ToList(),
                page.HasNextPage,
                page.HasPreviousPage),
            TotalCount = page.TotalCount,
        };
    }

    public static ThreadConnectionType BuildConnection(
        ListPage<ThreadRecord> page,
        ThreadSortField sortField)
    {
        var edges = page.Nodes
            .Select(node => new ThreadEdgeType
            {
                Node = ToGraphQL(node),
                Cursor = new CursorValue(
                    node.Id,
                    sortField switch
                    {
                        ThreadSortField.CreatedAt => node.CreatedAt,
                        ThreadSortField.UpdatedAt => node.UpdatedAt,
                        _ => null,
                    }),
            })
            .ToList();

        return new ThreadConnectionType
        {
            Edges = edges,
            PageInfo = BuildPageInfo(
                edges.Select(static edge => edge.Cursor).ToList(),
                page.HasNextPage,
                page.HasPreviousPage),
            TotalCount = page.TotalCount,
        };
    }

    public static RequestConnectionType BuildConnection(
        ListPage<RequestRecord> page,
        RequestSortField sortField)
    {
        var edges = page.Nodes
            .Select(node => new RequestEdgeType
            {
                Node = ToGraphQL(node),
                Cursor = new CursorValue(
                    node.Id,
                    sortField switch
                    {
                        RequestSortField.CreatedAt => node.CreatedAt,
                        RequestSortField.UpdatedAt => node.UpdatedAt,
                        _ => null,
                    }),
            })
            .ToList();

        return new RequestConnectionType
        {
            Edges = edges,
            PageInfo = BuildPageInfo(
                edges.Select(static edge => edge.Cursor).ToList(),
                page.HasNextPage,
                page.HasPreviousPage),
            TotalCount = page.TotalCount,
        };
    }

    public static TraceListFilter? ToFilter(TraceWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new TraceListFilter
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
            ProjectId = ParseId(where.ProjectId),
            ProjectIdNEQ = ParseId(where.ProjectIdNEQ),
            ProjectIdIn = ParseIds(where.ProjectIdIn),
            ProjectIdNotIn = ParseIds(where.ProjectIdNotIn),
            TraceId = where.TraceId,
            TraceIdNEQ = where.TraceIdNEQ,
            TraceIdIn = ToList(where.TraceIdIn),
            TraceIdNotIn = ToList(where.TraceIdNotIn),
            TraceIdGT = where.TraceIdGT,
            TraceIdGTE = where.TraceIdGTE,
            TraceIdLT = where.TraceIdLT,
            TraceIdLTE = where.TraceIdLTE,
            TraceIdContains = where.TraceIdContains,
            TraceIdContainsFold = where.TraceIdContainsFold,
            TraceIdPrefix = where.TraceIdHasPrefix,
            TraceIdSuffix = where.TraceIdHasSuffix,
            TraceIdEqualFold = where.TraceIdEqualFold,
            ThreadId = ParseId(where.ThreadId),
            ThreadIdNEQ = ParseId(where.ThreadIdNEQ),
            ThreadIdIn = ParseIds(where.ThreadIdIn),
            ThreadIdNotIn = ParseIds(where.ThreadIdNotIn),
            ThreadIdIsNil = where.ThreadIdIsNil == true ? true : null,
            ThreadIdNotNil = where.ThreadIdNotNil == true ? true : null,
            Status = where.Status is null ? null : ToWireStatus(where.Status.Value),
            StatusNEQ = where.StatusNEQ is null
                ? null
                : ToWireStatus(where.StatusNEQ.Value),
            StatusIn = where.StatusIn is { Count: > 0 }
                ? where.StatusIn.Select(static status => ToWireStatus(status)).ToList()
                : null,
            StatusNotIn = where.StatusNotIn is { Count: > 0 }
                ? where.StatusNotIn.Select(static status => ToWireStatus(status)).ToList()
                : null,
            HasProject = where.HasProject,
            HasProjectWith = where.HasProjectWith?
                .Select(static nested => ProjectMapping.ToFilter(nested))
                .OfType<ProjectListFilter>()
                .ToList(),
            HasThread = where.HasThread,
            HasThreadWith = where.HasThreadWith?
                .Select(static nested => ToFilter(nested))
                .OfType<ThreadListFilter>()
                .ToList(),
            HasRequests = where.HasRequests,
            HasRequestsWith = where.HasRequestsWith?
                .Select(static nested => ToFilter(nested))
                .OfType<RequestListFilter>()
                .ToList(),
            Not = ToFilter(where.Not),
            And = ToList(where.And?.Select(ToFilter).OfType<TraceListFilter>().ToList()),
            Or = ToList(where.Or?.Select(ToFilter).OfType<TraceListFilter>().ToList()),
        };
    }

    public static ThreadListFilter? ToFilter(ThreadWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new ThreadListFilter
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
            ProjectId = ParseId(where.ProjectId),
            ProjectIdNEQ = ParseId(where.ProjectIdNEQ),
            ProjectIdIn = ParseIds(where.ProjectIdIn),
            ProjectIdNotIn = ParseIds(where.ProjectIdNotIn),
            ThreadId = where.ThreadId,
            ThreadIdNEQ = where.ThreadIdNEQ,
            ThreadIdIn = ToList(where.ThreadIdIn),
            ThreadIdNotIn = ToList(where.ThreadIdNotIn),
            ThreadIdGT = where.ThreadIdGT,
            ThreadIdGTE = where.ThreadIdGTE,
            ThreadIdLT = where.ThreadIdLT,
            ThreadIdLTE = where.ThreadIdLTE,
            ThreadIdContains = where.ThreadIdContains,
            ThreadIdContainsFold = where.ThreadIdContainsFold,
            ThreadIdPrefix = where.ThreadIdHasPrefix,
            ThreadIdSuffix = where.ThreadIdHasSuffix,
            ThreadIdEqualFold = where.ThreadIdEqualFold,
            Status = where.Status is null ? null : ToWireStatus(where.Status.Value),
            StatusNEQ = where.StatusNEQ is null
                ? null
                : ToWireStatus(where.StatusNEQ.Value),
            StatusIn = where.StatusIn is { Count: > 0 }
                ? where.StatusIn.Select(static status => ToWireStatus(status)).ToList()
                : null,
            StatusNotIn = where.StatusNotIn is { Count: > 0 }
                ? where.StatusNotIn.Select(static status => ToWireStatus(status)).ToList()
                : null,
            HasProject = where.HasProject,
            HasProjectWith = where.HasProjectWith?
                .Select(static nested => ProjectMapping.ToFilter(nested))
                .OfType<ProjectListFilter>()
                .ToList(),
            HasTraces = where.HasTraces,
            HasTracesWith = where.HasTracesWith?
                .Select(static nested => ToFilter(nested))
                .OfType<TraceListFilter>()
                .ToList(),
            Not = ToFilter(where.Not),
            And = ToList(where.And?.Select(ToFilter).OfType<ThreadListFilter>().ToList()),
            Or = ToList(where.Or?.Select(ToFilter).OfType<ThreadListFilter>().ToList()),
        };
    }

    public static RequestListFilter? ToFilter(RequestWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new RequestListFilter
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
            ProjectId = ParseId(where.ProjectId),
            ProjectIdNEQ = ParseId(where.ProjectIdNEQ),
            ProjectIdIn = ParseIds(where.ProjectIdIn),
            ProjectIdNotIn = ParseIds(where.ProjectIdNotIn),
            ApiKeyId = ParseId(where.ApiKeyId),
            ApiKeyIdNEQ = ParseId(where.ApiKeyIdNEQ),
            ApiKeyIdIn = ParseIds(where.ApiKeyIdIn),
            ApiKeyIdNotIn = ParseIds(where.ApiKeyIdNotIn),
            ApiKeyIdIsNil = where.ApiKeyIdIsNil == true ? true : null,
            ApiKeyIdNotNil = where.ApiKeyIdNotNil == true ? true : null,
            TraceId = ParseId(where.TraceId),
            TraceIdNEQ = ParseId(where.TraceIdNEQ),
            TraceIdIn = ParseIds(where.TraceIdIn),
            TraceIdNotIn = ParseIds(where.TraceIdNotIn),
            TraceIdIsNil = where.TraceIdIsNil == true ? true : null,
            TraceIdNotNil = where.TraceIdNotNil == true ? true : null,
            DataStorageId = ParseId(where.DataStorageId),
            DataStorageIdNEQ = ParseId(where.DataStorageIdNEQ),
            DataStorageIdIn = ParseIds(where.DataStorageIdIn),
            DataStorageIdNotIn = ParseIds(where.DataStorageIdNotIn),
            DataStorageIdIsNil = where.DataStorageIdIsNil == true ? true : null,
            DataStorageIdNotNil = where.DataStorageIdNotNil == true ? true : null,
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
            ModelIdGT = where.ModelIdGT,
            ModelIdGTE = where.ModelIdGTE,
            ModelIdLT = where.ModelIdLT,
            ModelIdLTE = where.ModelIdLTE,
            ModelIdContains = where.ModelIdContains,
            ModelIdContainsFold = where.ModelIdContainsFold,
            ModelIdHasPrefix = where.ModelIdHasPrefix,
            ModelIdHasSuffix = where.ModelIdHasSuffix,
            ModelIdEqualFold = where.ModelIdEqualFold,
            ReasoningEffort = where.ReasoningEffort,
            ReasoningEffortNEQ = where.ReasoningEffortNEQ,
            ReasoningEffortIn = ToList(where.ReasoningEffortIn),
            ReasoningEffortNotIn = ToList(where.ReasoningEffortNotIn),
            ReasoningEffortGT = where.ReasoningEffortGT,
            ReasoningEffortGTE = where.ReasoningEffortGTE,
            ReasoningEffortLT = where.ReasoningEffortLT,
            ReasoningEffortLTE = where.ReasoningEffortLTE,
            ReasoningEffortContains = where.ReasoningEffortContains,
            ReasoningEffortContainsFold = where.ReasoningEffortContainsFold,
            ReasoningEffortHasPrefix = where.ReasoningEffortHasPrefix,
            ReasoningEffortHasSuffix = where.ReasoningEffortHasSuffix,
            ReasoningEffortEqualFold = where.ReasoningEffortEqualFold,
            ReasoningEffortIsNil = where.ReasoningEffortIsNil == true ? true : null,
            ReasoningEffortNotNil = where.ReasoningEffortNotNil == true ? true : null,
            Format = where.Format,
            FormatNEQ = where.FormatNEQ,
            FormatIn = ToList(where.FormatIn),
            FormatNotIn = ToList(where.FormatNotIn),
            FormatGT = where.FormatGT,
            FormatGTE = where.FormatGTE,
            FormatLT = where.FormatLT,
            FormatLTE = where.FormatLTE,
            FormatContains = where.FormatContains,
            FormatContainsFold = where.FormatContainsFold,
            FormatHasPrefix = where.FormatHasPrefix,
            FormatHasSuffix = where.FormatHasSuffix,
            FormatEqualFold = where.FormatEqualFold,
            ExternalId = where.ExternalId,
            ExternalIdNEQ = where.ExternalIdNEQ,
            ExternalIdIn = ToList(where.ExternalIdIn),
            ExternalIdNotIn = ToList(where.ExternalIdNotIn),
            ExternalIdGT = where.ExternalIdGT,
            ExternalIdGTE = where.ExternalIdGTE,
            ExternalIdLT = where.ExternalIdLT,
            ExternalIdLTE = where.ExternalIdLTE,
            ExternalIdContains = where.ExternalIdContains,
            ExternalIdContainsFold = where.ExternalIdContainsFold,
            ExternalIdHasPrefix = where.ExternalIdHasPrefix,
            ExternalIdHasSuffix = where.ExternalIdHasSuffix,
            ExternalIdEqualFold = where.ExternalIdEqualFold,
            ExternalIdIsNil = where.ExternalIdIsNil == true ? true : null,
            ExternalIdNotNil = where.ExternalIdNotNil == true ? true : null,
            Status = where.Status is null ? null : ToWireStatus(where.Status.Value),
            StatusNEQ = where.StatusNEQ is null
                ? null
                : ToWireStatus(where.StatusNEQ.Value),
            StatusIn = where.StatusIn is { Count: > 0 }
                ? where.StatusIn.Select(static status => ToWireStatus(status)).ToList()
                : null,
            StatusNotIn = where.StatusNotIn is { Count: > 0 }
                ? where.StatusNotIn.Select(static status => ToWireStatus(status)).ToList()
                : null,
            Stream = where.Stream,
            StreamNEQ = where.StreamNEQ,
            ClientIp = where.ClientIp,
            ClientIpNEQ = where.ClientIpNEQ,
            ClientIpIn = ToList(where.ClientIpIn),
            ClientIpNotIn = ToList(where.ClientIpNotIn),
            ClientIpGT = where.ClientIpGT,
            ClientIpGTE = where.ClientIpGTE,
            ClientIpLT = where.ClientIpLT,
            ClientIpLTE = where.ClientIpLTE,
            ClientIpContains = where.ClientIpContains,
            ClientIpHasPrefix = where.ClientIpHasPrefix,
            ClientIpHasSuffix = where.ClientIpHasSuffix,
            ClientIpEqualFold = where.ClientIpEqualFold,
            ClientIpContainsFold = where.ClientIpContainsFold,
            UserAgent = where.UserAgent,
            UserAgentNEQ = where.UserAgentNEQ,
            UserAgentIn = ToList(where.UserAgentIn),
            UserAgentNotIn = ToList(where.UserAgentNotIn),
            UserAgentGT = where.UserAgentGT,
            UserAgentGTE = where.UserAgentGTE,
            UserAgentLT = where.UserAgentLT,
            UserAgentLTE = where.UserAgentLTE,
            UserAgentContains = where.UserAgentContains,
            UserAgentHasPrefix = where.UserAgentHasPrefix,
            UserAgentHasSuffix = where.UserAgentHasSuffix,
            UserAgentEqualFold = where.UserAgentEqualFold,
            UserAgentContainsFold = where.UserAgentContainsFold,
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
            ContentSaved = where.ContentSaved,
            ContentSavedNEQ = where.ContentSavedNEQ,
            ContentStorageId = where.ContentStorageId,
            ContentStorageIdNEQ = where.ContentStorageIdNEQ,
            ContentStorageIdIn = ToLongList(where.ContentStorageIdIn),
            ContentStorageIdNotIn = ToLongList(where.ContentStorageIdNotIn),
            ContentStorageIdGT = where.ContentStorageIdGT,
            ContentStorageIdGTE = where.ContentStorageIdGTE,
            ContentStorageIdLT = where.ContentStorageIdLT,
            ContentStorageIdLTE = where.ContentStorageIdLTE,
            ContentStorageIdIsNil = where.ContentStorageIdIsNil,
            ContentStorageIdNotNil = where.ContentStorageIdNotNil,
            ContentStorageKey = where.ContentStorageKey,
            ContentStorageKeyNEQ = where.ContentStorageKeyNEQ,
            ContentStorageKeyIn = ToList(where.ContentStorageKeyIn),
            ContentStorageKeyNotIn = ToList(where.ContentStorageKeyNotIn),
            ContentStorageKeyGT = where.ContentStorageKeyGT,
            ContentStorageKeyGTE = where.ContentStorageKeyGTE,
            ContentStorageKeyLT = where.ContentStorageKeyLT,
            ContentStorageKeyLTE = where.ContentStorageKeyLTE,
            ContentStorageKeyContains = where.ContentStorageKeyContains,
            ContentStorageKeyHasPrefix = where.ContentStorageKeyHasPrefix,
            ContentStorageKeyHasSuffix = where.ContentStorageKeyHasSuffix,
            ContentStorageKeyIsNil = where.ContentStorageKeyIsNil,
            ContentStorageKeyNotNil = where.ContentStorageKeyNotNil,
            ContentStorageKeyEqualFold = where.ContentStorageKeyEqualFold,
            ContentStorageKeyContainsFold = where.ContentStorageKeyContainsFold,
            ContentSavedAt = where.ContentSavedAt,
            ContentSavedAtNEQ = where.ContentSavedAtNEQ,
            ContentSavedAtIn = ToList(where.ContentSavedAtIn),
            ContentSavedAtNotIn = ToList(where.ContentSavedAtNotIn),
            ContentSavedAtGT = where.ContentSavedAtGT,
            ContentSavedAtGTE = where.ContentSavedAtGTE,
            ContentSavedAtLT = where.ContentSavedAtLT,
            ContentSavedAtLTE = where.ContentSavedAtLTE,
            ContentSavedAtIsNil = where.ContentSavedAtIsNil,
            ContentSavedAtNotNil = where.ContentSavedAtNotNil,
            HasApiKey = where.HasApiKey,
            HasApiKeyWith = where.HasApiKeyWith?
                .Select(static nested => ApiKeyMapping.ToFilter(nested))
                .OfType<ApiKeyListFilter>()
                .ToList(),
            HasProject = where.HasProject,
            HasProjectWith = where.HasProjectWith?
                .Select(static nested => ProjectMapping.ToFilter(nested))
                .OfType<ProjectListFilter>()
                .ToList(),
            HasTrace = where.HasTrace,
            HasTraceWith = where.HasTraceWith?
                .Select(static nested => TraceMapping.ToFilter(nested))
                .OfType<TraceListFilter>()
                .ToList(),
            HasDataStorage = where.HasDataStorage,
            HasDataStorageWith = where.HasDataStorageWith?
                .Select(static nested => DataStorageMapping.ToFilter(nested))
                .OfType<DataStorageListFilter>()
                .ToList(),
            HasExecutions = where.HasExecutions,
            HasExecutionsWith = where.HasExecutionsWith?
                .Select(static nested => RequestMapping.ToFilter(nested))
                .OfType<RequestExecutionListFilter>()
                .ToList(),
            HasChannel = where.HasChannel,
            HasChannelWith = where.HasChannelWith?
                .Select(static nested => ChannelMapping.ToFilter(nested))
                .OfType<ChannelListFilter>()
                .ToList(),
            HasUsageLogs = where.HasUsageLogs,
            HasUsageLogsWith = where.HasUsageLogsWith?
                .Select(static nested => RequestMapping.ToFilter(nested))
                .OfType<UsageLogListFilter>()
                .ToList(),

            Not = ToFilter(where.Not),
            And = ToList(where.And?.Select(ToFilter).OfType<RequestListFilter>().ToList()),
            Or = ToList(where.Or?.Select(ToFilter).OfType<RequestListFilter>().ToList()),
        };
    }

    public static TraceStatusValue ToGraphQLTraceStatus(string status) => status switch
    {
        TraceStatuses.Archived => TraceStatusValue.Archived,
        TraceStatuses.Retained => TraceStatusValue.Retained,
        _ => TraceStatusValue.Active,
    };

    public static ThreadStatusValue ToGraphQLThreadStatus(string status) => status switch
    {
        ThreadStatuses.Archived => ThreadStatusValue.Archived,
        ThreadStatuses.Retained => ThreadStatusValue.Retained,
        _ => ThreadStatusValue.Active,
    };

    public static RequestStatusValue ToGraphQLRequestStatus(string status) => status switch
    {
        "processing" => RequestStatusValue.Processing,
        "completed" => RequestStatusValue.Completed,
        "failed" => RequestStatusValue.Failed,
        "canceled" => RequestStatusValue.Canceled,
        _ => RequestStatusValue.Pending,
    };

    public static RequestSourceValue ToGraphQLSource(string source) => source switch
    {
        "playground" => RequestSourceValue.Playground,
        "test" => RequestSourceValue.Test,
        _ => RequestSourceValue.Api,
    };

    /// <summary>Port of the RequestSource wire strings (ent.graphql:86-101).</summary>
    public static string ToWireStatus(RequestSourceValue source) => source switch
    {
        RequestSourceValue.Playground => "playground",
        RequestSourceValue.Test => "test",
        _ => "api",
    };

    public static string ToWireStatus(TraceStatusValue status) => status switch
    {
        TraceStatusValue.Archived => TraceStatuses.Archived,
        TraceStatusValue.Retained => TraceStatuses.Retained,
        _ => TraceStatuses.Active,
    };

    public static string ToWireStatus(ThreadStatusValue status) => status switch
    {
        ThreadStatusValue.Archived => ThreadStatuses.Archived,
        ThreadStatusValue.Retained => ThreadStatuses.Retained,
        _ => ThreadStatuses.Active,
    };

    public static string ToWireStatus(RequestStatusValue status) => status switch
    {
        RequestStatusValue.Processing => "processing",
        RequestStatusValue.Completed => "completed",
        RequestStatusValue.Failed => "failed",
        RequestStatusValue.Canceled => "canceled",
        _ => "pending",
    };

    /// <summary>
    /// Go's int64 is exposed through the SDL's 32-bit Int; values outside the
    /// range fail the response instead of silently wrapping (C190).
    /// </summary>
    public static int? ToGraphQLInt(long? value)
        => value is null ? null : checked((int)value.Value);

    private static long? ParseId(string? id)
        => id is null ? null : GidValue.Parse(id).Id;

    /// <summary>
    /// entgql ignores an empty In/NotIn list (<c>len(...) &gt; 0</c>), so the
    /// mapping drops it before the store sees it (C189).
    /// </summary>
    private static IReadOnlyList<long>? ParseIds(IReadOnlyList<string>? ids)
    {
        if (ids is not { Count: > 0 })
        {
            return null;
        }

        return ids.Select(static id => GidValue.Parse(id).Id).ToList();
    }

    /// <summary>Drops empty lists, matching entgql's len guard (C189).</summary>
    private static IReadOnlyList<T>? ToList<T>(IReadOnlyList<T>? values)
        => values is { Count: > 0 } ? values : null;

    /// <summary>Widens the SDL's 32-bit Int lists to the store's int64 ids.</summary>
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
