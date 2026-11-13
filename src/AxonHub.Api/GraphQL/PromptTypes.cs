using HotChocolate;
using HotChocolate.Types;
using HotChocolate.Types.Relay;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Api.Prompts;
using AxonHub.Persistence;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the PromptActionType enum (prompt.graphql:1-4).</summary>
[GraphQLName("PromptActionType")]
public enum PromptActionTypeValue
{
    [GraphQLName("prepend")]
    Prepend,

    [GraphQLName("append")]
    Append,
}

/// <summary>Port of the PromptActivationConditionType enum (prompt.graphql:6-10).</summary>
[GraphQLName("PromptActivationConditionType")]
public enum PromptActivationConditionTypeValue
{
    [GraphQLName("model_id")]
    ModelId,

    [GraphQLName("model_pattern")]
    ModelPattern,

    [GraphQLName("api_key")]
    ApiKey,
}

/// <summary>Port of the PromptStatus enum (ent.graphql:3588-3592).</summary>
[GraphQLName("PromptStatus")]
public enum PromptStatusValue
{
    [GraphQLName("enabled")]
    Enabled,

    [GraphQLName("disabled")]
    Disabled,
}

/// <summary>Port of PromptOrderField (ent.graphql:3404-3408).</summary>
[GraphQLName("PromptOrderField")]
public enum PromptOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,

    [GraphQLName("ORDER")]
    Order,
}

/// <summary>Port of PromptOrder (ent.graphql:3391-3402).</summary>
[GraphQLName("PromptOrder")]
[InputObjectType("PromptOrder")]
public sealed class PromptOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    public PromptOrderFieldValue Field { get; set; } = PromptOrderFieldValue.CreatedAt;
}

/// <summary>Port of <c>PromptWhereInput</c> (axonhub.graphql).</summary>
[GraphQLName("PromptWhereInput")]
public sealed class PromptWhereInput
{
    public PromptWhereInput? Not { get; set; }

    public IReadOnlyList<PromptWhereInput>? And { get; set; }

    public IReadOnlyList<PromptWhereInput>? Or { get; set; }

    [GraphQLType(typeof(IdType))]
    public string? Id { get; set; }

    [GraphQLType(typeof(IdType))]
    public string? IdNEQ { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdIn { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdNotIn { get; set; }

    [GraphQLType(typeof(IdType))]
    public string? IdGT { get; set; }

    [GraphQLType(typeof(IdType))]
    public string? IdGTE { get; set; }

    [GraphQLType(typeof(IdType))]
    public string? IdLT { get; set; }

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

    public string? Name { get; set; }

    public string? NameNEQ { get; set; }

    public IReadOnlyList<string>? NameIn { get; set; }

    public IReadOnlyList<string>? NameNotIn { get; set; }

    public string? NameGT { get; set; }

    public string? NameGTE { get; set; }

    public string? NameLT { get; set; }

    public string? NameLTE { get; set; }

    public string? NameContains { get; set; }

    public string? NameHasPrefix { get; set; }

    public string? NameHasSuffix { get; set; }

    public string? NameEqualFold { get; set; }

    public string? NameContainsFold { get; set; }

    public string? Description { get; set; }

    public string? DescriptionNEQ { get; set; }

    public IReadOnlyList<string>? DescriptionIn { get; set; }

    public IReadOnlyList<string>? DescriptionNotIn { get; set; }

    public string? DescriptionGT { get; set; }

    public string? DescriptionGTE { get; set; }

    public string? DescriptionLT { get; set; }

    public string? DescriptionLTE { get; set; }

    public string? DescriptionContains { get; set; }

    public string? DescriptionHasPrefix { get; set; }

    public string? DescriptionHasSuffix { get; set; }

    public string? DescriptionEqualFold { get; set; }

    public string? DescriptionContainsFold { get; set; }

    public string? Role { get; set; }

    public string? RoleNEQ { get; set; }

    public IReadOnlyList<string>? RoleIn { get; set; }

    public IReadOnlyList<string>? RoleNotIn { get; set; }

    public string? RoleGT { get; set; }

    public string? RoleGTE { get; set; }

    public string? RoleLT { get; set; }

    public string? RoleLTE { get; set; }

    public string? RoleContains { get; set; }

    public string? RoleHasPrefix { get; set; }

    public string? RoleHasSuffix { get; set; }

    public string? RoleEqualFold { get; set; }

    public string? RoleContainsFold { get; set; }

    public string? Content { get; set; }

    public string? ContentNEQ { get; set; }

    public IReadOnlyList<string>? ContentIn { get; set; }

    public IReadOnlyList<string>? ContentNotIn { get; set; }

    public string? ContentGT { get; set; }

    public string? ContentGTE { get; set; }

    public string? ContentLT { get; set; }

    public string? ContentLTE { get; set; }

    public string? ContentContains { get; set; }

    public string? ContentHasPrefix { get; set; }

    public string? ContentHasSuffix { get; set; }

    public string? ContentEqualFold { get; set; }

    public string? ContentContainsFold { get; set; }

    public PromptStatusValue? Status { get; set; }

    public PromptStatusValue? StatusNEQ { get; set; }

    public IReadOnlyList<PromptStatusValue>? StatusIn { get; set; }

    public IReadOnlyList<PromptStatusValue>? StatusNotIn { get; set; }

    public int? Order { get; set; }

    public int? OrderNEQ { get; set; }

    public IReadOnlyList<int>? OrderIn { get; set; }

    public IReadOnlyList<int>? OrderNotIn { get; set; }

    public int? OrderGT { get; set; }

    public int? OrderGTE { get; set; }

    public int? OrderLT { get; set; }

    public int? OrderLTE { get; set; }

    public bool? HasProject { get; set; }

    public IReadOnlyList<ProjectWhereInput>? HasProjectWith { get; set; }
}

/// <summary>Port of the <c>PromptAction</c> type (prompt.graphql:12-14).</summary>
[GraphQLName("PromptAction")]
public sealed class PromptActionType
{
    public PromptActionTypeValue Type { get; init; }
}

/// <summary>Port of the <c>PromptActivationCondition</c> type (prompt.graphql:16-21).</summary>
[GraphQLName("PromptActivationCondition")]
public sealed class PromptActivationConditionType
{
    public PromptActivationConditionTypeValue Type { get; init; }

    public string? ModelId { get; init; }

    public string? ModelPattern { get; init; }

    /// <summary>Go's *int widens to the port's long in the domain model.</summary>
    public int? ApiKeyId { get; init; }
}

/// <summary>
/// Port of the <c>PromptActivationConditionComposite</c> type
/// (prompt.graphql:23-25).
/// </summary>
[GraphQLName("PromptActivationConditionComposite")]
public sealed class PromptActivationConditionCompositeType
{
    public IReadOnlyList<PromptActivationConditionType>? Conditions { get; init; }
}

/// <summary>Port of the <c>PromptSettings</c> type (prompt.graphql:27-30).</summary>
[GraphQLName("PromptSettings")]
public sealed class PromptSettingsType
{
    public PromptActionType Action { get; init; } = new();

    public IReadOnlyList<PromptActivationConditionCompositeType>? Conditions { get; init; }
}

/// <summary>Port of <c>PromptActionInput</c> (prompt.graphql:32-34).</summary>
[GraphQLName("PromptActionInput")]
public sealed class PromptActionInput
{
    public PromptActionTypeValue Type { get; set; }
}

/// <summary>Port of <c>PromptActivationConditionInput</c> (prompt.graphql:36-41).</summary>
[GraphQLName("PromptActivationConditionInput")]
public sealed class PromptActivationConditionInput
{
    public PromptActivationConditionTypeValue Type { get; set; }

    public string? ModelId { get; set; }

    public string? ModelPattern { get; set; }

    public int? ApiKeyId { get; set; }
}

/// <summary>
/// Port of <c>PromptActivationConditionCompositeInput</c>
/// (prompt.graphql:43-45).
/// </summary>
[GraphQLName("PromptActivationConditionCompositeInput")]
public sealed class PromptActivationConditionCompositeInput
{
    public IReadOnlyList<PromptActivationConditionInput>? Conditions { get; set; }
}

/// <summary>Port of <c>PromptSettingsInput</c> (prompt.graphql:47-50).</summary>
[GraphQLName("PromptSettingsInput")]
public sealed class PromptSettingsInput
{
    public PromptActionInput Action { get; set; } = new();

    public IReadOnlyList<PromptActivationConditionCompositeInput>? Conditions { get; set; }
}

/// <summary>Port of the ent <c>CreatePromptInput</c> (ent.graphql:1779-1805).</summary>
[GraphQLName("CreatePromptInput")]
public sealed class CreatePromptInput
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    public PromptStatusValue? Status { get; set; }

    public int? Order { get; set; }

    public PromptSettingsInput Settings { get; set; } = new();
}

/// <summary>Port of the ent <c>UpdatePromptInput</c> (ent.graphql:6370-6395).</summary>
[GraphQLName("UpdatePromptInput")]
public sealed class UpdatePromptInput
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public string? Role { get; set; }

    public string? Content { get; set; }

    public PromptStatusValue? Status { get; set; }

    public int? Order { get; set; }

    public PromptSettingsInput? Settings { get; set; }
}

/// <summary>Port of the <c>Prompt</c> object type (ent.graphql:3323-3360).</summary>
[GraphQLName("Prompt")]
public sealed class PromptType : INode
{
    [GraphQLIgnore]
    public PromptRecord Record { get; init; } = null!;

    [GraphQLIgnore]
    public long DbId => Record.Id;

    [GraphQLType(typeof(IdType))]
    public string Id => new GidValue("Prompt", Record.Id).ToString();

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt => Record.CreatedAt;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt => Record.UpdatedAt;

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string ProjectID => new GidValue("Project", Record.ProjectId).ToString();

    public string Name => Record.Name;

    public string Description => Record.Description;

    public string Role => Record.Role;

    public string Content => Record.Content;

    public PromptStatusValue Status => PromptMapping.ToGraphQLStatus(Record.Status);

    /// <summary>
    /// The SDL field is a GraphQL Int; the column is a Go int, which is
    /// 64-bit on the reference's platforms (C188).
    /// </summary>
    public int Order => (int)Record.Order;

    public PromptSettingsType Settings
        => PromptMapping.ToGraphQL(PromptSettings.FromJson(Record.Settings));

    /// <summary>
    /// Port of the entgql Project edge: the Project query policy applies, and
    /// a filtered-out project is reported as "ent: project not found".
    /// </summary>
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
}

/// <summary>Port of <c>PromptEdge</c> (ent.graphql:3378-3389).</summary>
[GraphQLName("PromptEdge")]
public sealed class PromptEdgeType
{
    public PromptType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of <c>PromptConnection</c> (ent.graphql:3361-3374).</summary>
[GraphQLName("PromptConnection")]
public sealed class PromptConnectionType
{
    public IReadOnlyList<PromptEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>Domain ↔ GraphQL mapping for the prompts slice.</summary>
public static class PromptMapping
{
    public static PromptType ToGraphQL(PromptRecord record) => new() { Record = record };

    public static PromptListFilter? ToFilter(PromptWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new PromptListFilter
        {
            Id = where.Id is null ? null : GidValue.Parse(where.Id).Id,
            IdNEQ = where.IdNEQ is null ? null : GidValue.Parse(where.IdNEQ).Id,
            IdIn = where.IdIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            IdNotIn = where.IdNotIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
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
            ProjectId = where.ProjectId is null
                ? null
                : GidValue.Parse(where.ProjectId).Id,
            ProjectIdNEQ = where.ProjectIdNEQ is null
                ? null
                : GidValue.Parse(where.ProjectIdNEQ).Id,
            ProjectIdIn = where.ProjectIdIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            ProjectIdNotIn = where.ProjectIdNotIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            Name = where.Name,
            NameNEQ = where.NameNEQ,
            NameIn = where.NameIn,
            NameNotIn = where.NameNotIn,
            NameGT = where.NameGT,
            NameGTE = where.NameGTE,
            NameLT = where.NameLT,
            NameLTE = where.NameLTE,
            NameContains = where.NameContains,
            NameHasPrefix = where.NameHasPrefix,
            NameHasSuffix = where.NameHasSuffix,
            NameEqualFold = where.NameEqualFold,
            NameContainsFold = where.NameContainsFold,
            Description = where.Description,
            DescriptionNEQ = where.DescriptionNEQ,
            DescriptionIn = where.DescriptionIn,
            DescriptionNotIn = where.DescriptionNotIn,
            DescriptionGT = where.DescriptionGT,
            DescriptionGTE = where.DescriptionGTE,
            DescriptionLT = where.DescriptionLT,
            DescriptionLTE = where.DescriptionLTE,
            DescriptionContains = where.DescriptionContains,
            DescriptionHasPrefix = where.DescriptionHasPrefix,
            DescriptionHasSuffix = where.DescriptionHasSuffix,
            DescriptionEqualFold = where.DescriptionEqualFold,
            DescriptionContainsFold = where.DescriptionContainsFold,
            Role = where.Role,
            RoleNEQ = where.RoleNEQ,
            RoleIn = where.RoleIn,
            RoleNotIn = where.RoleNotIn,
            RoleGT = where.RoleGT,
            RoleGTE = where.RoleGTE,
            RoleLT = where.RoleLT,
            RoleLTE = where.RoleLTE,
            RoleContains = where.RoleContains,
            RoleHasPrefix = where.RoleHasPrefix,
            RoleHasSuffix = where.RoleHasSuffix,
            RoleEqualFold = where.RoleEqualFold,
            RoleContainsFold = where.RoleContainsFold,
            Content = where.Content,
            ContentNEQ = where.ContentNEQ,
            ContentIn = where.ContentIn,
            ContentNotIn = where.ContentNotIn,
            ContentGT = where.ContentGT,
            ContentGTE = where.ContentGTE,
            ContentLT = where.ContentLT,
            ContentLTE = where.ContentLTE,
            ContentContains = where.ContentContains,
            ContentHasPrefix = where.ContentHasPrefix,
            ContentHasSuffix = where.ContentHasSuffix,
            ContentEqualFold = where.ContentEqualFold,
            ContentContainsFold = where.ContentContainsFold,
            Status = where.Status is null ? null : ToWireStatus(where.Status.Value),
            StatusNEQ = where.StatusNEQ is null ? null : ToWireStatus(where.StatusNEQ.Value),
            StatusIn = where.StatusIn?
                .Select(static status => ToWireStatus(status))
                .ToList(),
            StatusNotIn = where.StatusNotIn?
                .Select(static status => ToWireStatus(status))
                .ToList(),
            Order = where.Order,
            OrderNEQ = where.OrderNEQ,
            OrderIn = where.OrderIn?.Select(static order => (long)order).ToList(),
            OrderNotIn = where.OrderNotIn?.Select(static order => (long)order).ToList(),
            OrderGT = where.OrderGT,
            OrderGTE = where.OrderGTE,
            OrderLT = where.OrderLT,
            OrderLTE = where.OrderLTE,
            HasProject = where.HasProject,
            HasProjectWith = where.HasProjectWith?
                .Select(ProjectMapping.ToFilter)
                .OfType<ProjectListFilter>()
                .ToList(),
            Not = ToFilter(where.Not),
            And = where.And?.Select(ToFilter).OfType<PromptListFilter>().ToList(),
            Or = where.Or?.Select(ToFilter).OfType<PromptListFilter>().ToList(),
        };
    }

    /// <summary>
    /// CREATED_AT is rewritten by the resolver to ent.DefaultPromptOrder's
    /// field, which is <c>id</c> (ent.resolvers.go:470-472). ORDER orders by
    /// the order column and UPDATED_AT by updated_at.
    /// </summary>
    public static PromptSortField ToSortField(PromptOrder? orderBy)
        => orderBy?.Field switch
        {
            PromptOrderFieldValue.UpdatedAt => PromptSortField.UpdatedAt,
            PromptOrderFieldValue.Order => PromptSortField.Order,
            _ => PromptSortField.Id,
        };

    public static SortDirection ToSortDirection(PromptOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    public static PromptConnectionType BuildConnection(
        ListPage<PromptRecord> page,
        PromptSortField sortField)
    {
        var edges = page.Nodes
            .Select(node => new PromptEdgeType
            {
                Node = ToGraphQL(node),
                Cursor = new CursorValue(
                    node.Id,
                    sortField switch
                    {
                        PromptSortField.UpdatedAt => node.UpdatedAt,
                        PromptSortField.Order => node.Order,
                        _ => null,
                    }),
            })
            .ToList();

        return new PromptConnectionType
        {
            Edges = edges,
            PageInfo = new PageInfoType
            {
                HasNextPage = page.HasNextPage,
                HasPreviousPage = page.HasPreviousPage,
                StartCursor = edges.Count > 0 ? edges[0].Cursor : null,
                EndCursor = edges.Count > 0 ? edges[^1].Cursor : null,
            },
            TotalCount = page.TotalCount,
        };
    }

    /// <summary>
    /// Port of gqlgen's PromptSettings marshalling: structs are never null, a
    /// nil optional list is null, and an unknown enum value is emitted raw
    /// (the reference does not validate enum output; the port fails the field
    /// like its other enum slices, C177 item 5 / C188).
    /// </summary>
    public static PromptSettingsType ToGraphQL(PromptSettings settings) => new()
    {
        Action = new PromptActionType
        {
            Type = ToGraphQLActionType(settings.Action?.Type),
        },
        Conditions = settings.Conditions?
            .Select(static composite => new PromptActivationConditionCompositeType
            {
                Conditions = composite?.Conditions?
                    .Select(static condition => new PromptActivationConditionType
                    {
                        Type = ToGraphQLConditionType(condition?.Type),
                        ModelId = condition?.ModelId,
                        ModelPattern = condition?.ModelPattern,
                        ApiKeyId = condition?.ApiKeyId is null
                            ? null
                            : (int)condition.ApiKeyId.Value,
                    })
                    .ToList(),
            })
            .ToList(),
    };

    public static string ToWireActionType(PromptActionTypeValue type) => type switch
    {
        PromptActionTypeValue.Prepend => PromptActionTypes.Prepend,
        PromptActionTypeValue.Append => PromptActionTypes.Append,
        _ => throw new GraphQLException(
            "Enum \"PromptActionType\" cannot represent value: \"" + type + "\""),
    };

    public static PromptActionTypeValue ToGraphQLActionType(string? type) => type switch
    {
        PromptActionTypes.Prepend => PromptActionTypeValue.Prepend,
        PromptActionTypes.Append => PromptActionTypeValue.Append,
        _ => throw new GraphQLException(
            "Enum \"PromptActionType\" cannot represent value: \"" + (type ?? string.Empty) + "\""),
    };

    public static string ToWireConditionType(PromptActivationConditionTypeValue type) => type switch
    {
        PromptActivationConditionTypeValue.ModelId => PromptActivationConditionTypes.ModelId,
        PromptActivationConditionTypeValue.ModelPattern => PromptActivationConditionTypes.ModelPattern,
        PromptActivationConditionTypeValue.ApiKey => PromptActivationConditionTypes.ApiKey,
        _ => throw new GraphQLException(
            "Enum \"PromptActivationConditionType\" cannot represent value: \"" + type + "\""),
    };

    public static PromptActivationConditionTypeValue ToGraphQLConditionType(string? type) => type switch
    {
        PromptActivationConditionTypes.ModelId => PromptActivationConditionTypeValue.ModelId,
        PromptActivationConditionTypes.ModelPattern => PromptActivationConditionTypeValue.ModelPattern,
        PromptActivationConditionTypes.ApiKey => PromptActivationConditionTypeValue.ApiKey,
        _ => throw new GraphQLException(
            "Enum \"PromptActivationConditionType\" cannot represent value: \""
            + (type ?? string.Empty) + "\""),
    };

    public static string ToWireStatus(PromptStatusValue status) => status switch
    {
        PromptStatusValue.Enabled => PromptStatuses.Enabled,
        PromptStatusValue.Disabled => PromptStatuses.Disabled,
        _ => throw new GraphQLException(
            "Enum \"PromptStatus\" cannot represent value: \"" + status + "\""),
    };

    public static PromptStatusValue ToGraphQLStatus(string status) => status switch
    {
        PromptStatuses.Enabled => PromptStatusValue.Enabled,
        PromptStatuses.Disabled => PromptStatusValue.Disabled,
        _ => throw new GraphQLException(
            "Enum \"PromptStatus\" cannot represent value: \"" + status + "\""),
    };

    /// <summary>
    /// Port of gqlgen's PromptSettingsInput → objects.PromptSettings binding:
    /// the required action struct and every condition struct decode to their
    /// zero value when absent.
    /// </summary>
    public static PromptSettings ToDomain(PromptSettingsInput input) => new()
    {
        Action = new PromptAction { Type = ToWireActionType(input.Action.Type) },
        Conditions = input.Conditions?
            .Select(static composite => new PromptActivationConditionComposite
            {
                Conditions = composite.Conditions?
                    .Select(static condition => new PromptActivationCondition
                    {
                        Type = ToWireConditionType(condition.Type),
                        ModelId = condition.ModelId,
                        ModelPattern = condition.ModelPattern,
                        ApiKeyId = condition.ApiKeyId is null
                            ? null
                            : condition.ApiKeyId.Value,
                    })
                    .ToList(),
            })
            .ToList(),
    };

    public static CreatePromptCommand ToCommand(CreatePromptInput input) => new()
    {
        Name = input.Name,
        Description = input.Description,
        Role = input.Role,
        Content = input.Content,
        Status = input.Status is null ? null : ToWireStatus(input.Status.Value),
        Order = input.Order,
        Settings = ToDomain(input.Settings),
    };

    public static UpdatePromptCommand ToCommand(string id, UpdatePromptInput input) => new()
    {
        Id = GidValue.Parse(id).Id,
        Name = input.Name,
        Description = input.Description,
        Role = input.Role,
        Content = input.Content,
        Status = input.Status is null ? null : ToWireStatus(input.Status.Value),
        Order = input.Order,
        Settings = input.Settings is null ? null : ToDomain(input.Settings),
    };
}
