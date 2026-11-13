using System.Text.Json;
using HotChocolate;
using HotChocolate.Features;
using HotChocolate.Language;
using HotChocolate.Text.Json;
using HotChocolate.Types;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of the gqlgen <c>ChannelTagsMatchMode</c> enum (axonhub.graphql:531-535).
/// The reference binds the enum to <c>objects.ChannelTagsMatchMode</c>, a Go
/// string with no MarshalGQL: gqlgen therefore marshals with
/// graphql.MarshalString and parses with graphql.UnmarshalString, so invalid
/// names pass through to ValidateProjectProfiles and the empty value is
/// serialized as "". HotChocolate's enum serialization cannot emit "" (the
/// CoerceOutputValue/ValueToLiteral members are sealed), so the port exposes a
/// string-backed scalar of the same name (C175).
/// </summary>
public sealed class ChannelTagsMatchModeType : ScalarType<string, StringValueNode>
{
    public ChannelTagsMatchModeType()
        : base("ChannelTagsMatchMode", BindingBehavior.Explicit)
    {
    }

    protected override string OnCoerceInputLiteral(StringValueNode valueLiteral)
        => valueLiteral.Value;

    protected override string OnCoerceInputValue(
        JsonElement inputValue,
        IFeatureProvider context)
        => inputValue.GetString() ?? string.Empty;

    protected override void OnCoerceOutputValue(
        string runtimeValue,
        ResultElement resultValue)
        => resultValue.SetStringValue(runtimeValue, isEncoded: false);

    protected override StringValueNode OnValueToLiteral(string runtimeValue)
        => new(runtimeValue);
}

/// <summary>Port of the gqlgen <c>ProjectOrderField</c> enum (ent.graphql:3167-3183).</summary>
[GraphQLName("ProjectOrderField")]
public enum ProjectOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,
}

/// <summary>Port of <c>ProjectOrder</c> (ent.graphql:3160-3183).</summary>
[GraphQLName("ProjectOrder")]
[InputObjectType("ProjectOrder")]
public sealed class ProjectOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    public ProjectOrderFieldValue Field { get; set; } = ProjectOrderFieldValue.CreatedAt;
}

/// <summary>
/// Port of <c>ProjectWhereInput</c> (ent.graphql:3195-3313). All 54 scalar
/// field predicates are modeled; the ten edge predicate groups remain tracked
/// by C165.
/// </summary>
public sealed class ProjectWhereInput
{
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

    public ProjectStatusValue? Status { get; set; }

    public ProjectStatusValue? StatusNEQ { get; set; }

    public IReadOnlyList<ProjectStatusValue>? StatusIn { get; set; }

    public IReadOnlyList<ProjectStatusValue>? StatusNotIn { get; set; }

    public ProjectWhereInput? Not { get; set; }

    public IReadOnlyList<ProjectWhereInput>? And { get; set; }

    public IReadOnlyList<ProjectWhereInput>? Or { get; set; }
}

/// <summary>Port of <c>CreateProjectInput</c> (ent.graphql:1759-1773).</summary>
/// <remarks>
/// <c>status</c> and <c>userIDs</c> bind but are ignored by
/// ProjectService.CreateProject (project.go:53-157), like the reference.
/// </remarks>
public sealed class CreateProjectInput
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public ProjectStatusValue? Status { get; set; }

    [GraphQLName("userIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? UserIDs { get; set; }
}

/// <summary>Port of <c>UpdateProjectInput</c> (ent.graphql:6349-6365).</summary>
/// <remarks>
/// <c>status</c> binds but is ignored by ProjectService.UpdateProject
/// (project.go:160-188); status changes go through updateProjectStatus.
/// </remarks>
public sealed class UpdateProjectInput
{
    public string? Name { get; set; }

    public string? Description { get; set; }

    public ProjectStatusValue? Status { get; set; }

    [GraphQLName("addUserIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? AddUserIDs { get; set; }

    [GraphQLName("removeUserIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? RemoveUserIDs { get; set; }

    public bool? ClearUsers { get; set; }
}

/// <summary>Port of <c>UpdateProjectProfilesInput</c> (axonhub.graphql:497-500).</summary>
public sealed class UpdateProjectProfilesInput
{
    public string ActiveProfile { get; set; } = string.Empty;

    public IReadOnlyList<ProjectProfileInput>? Profiles { get; set; }
}

/// <summary>Port of <c>ProjectProfileInput</c> (axonhub.graphql:502-507).</summary>
public sealed class ProjectProfileInput
{
    public string Name { get; set; } = string.Empty;

    [GraphQLType(typeof(ListType<NonNullType<IntType>>))]
    public IReadOnlyList<int>? ChannelIDs { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<StringType>>))]
    public IReadOnlyList<string>? ChannelTags { get; set; }

    [GraphQLType(typeof(ChannelTagsMatchModeType))]
    public string? ChannelTagsMatchMode { get; set; }
}

/// <summary>Port of <c>ProjectProfiles</c> (axonhub.graphql:509-512).</summary>
[GraphQLName("ProjectProfiles")]
public sealed class ProjectProfilesType
{
    public string ActiveProfile { get; init; } = string.Empty;

    public IReadOnlyList<ProjectProfileType>? Profiles { get; init; }
}

/// <summary>Port of <c>ProjectProfile</c> (axonhub.graphql:514-519).</summary>
[GraphQLName("ProjectProfile")]
public sealed class ProjectProfileType
{
    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<int>? ChannelIDs { get; init; }

    public IReadOnlyList<string>? ChannelTags { get; init; }

    [GraphQLType(typeof(ChannelTagsMatchModeType))]
    public string? ChannelTagsMatchMode { get; init; }
}

/// <summary>Port of <c>ProjectEdge</c> (ent.graphql).</summary>
[GraphQLName("ProjectEdge")]
public sealed class ProjectEdgeType
{
    public ProjectType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of <c>ProjectConnection</c>.</summary>
[GraphQLName("ProjectConnection")]
public sealed class ProjectConnectionType
{
    public IReadOnlyList<ProjectEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>Domain ↔ GraphQL mapping for the projects slice.</summary>
public static class ProjectMapping
{
    public static ProjectProfilesType? ToGraphQL(string? profilesJson)
    {
        if (profilesJson is null)
        {
            return null;
        }

        var model = ProjectProfilesCodec.Deserialize(profilesJson);
        return new ProjectProfilesType
        {
            ActiveProfile = model.ActiveProfile,
            Profiles = model.Profiles?
                .Select(static profile => new ProjectProfileType
                {
                    Name = profile.Name,
                    ChannelIDs = profile.ChannelIDs,
                    ChannelTags = profile.ChannelTags,
                    ChannelTagsMatchMode = profile.ChannelTagsMatchMode,
                })
                .ToList(),
        };
    }

    public static ProjectListFilter? ToFilter(ProjectWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new ProjectListFilter
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
            Status = where.Status is null ? null : ToWireStatus(where.Status.Value),
            StatusNEQ = where.StatusNEQ is null ? null : ToWireStatus(where.StatusNEQ.Value),
            StatusIn = where.StatusIn?
                .Select(static status => ToWireStatus(status))
                .ToList(),
            StatusNotIn = where.StatusNotIn?
                .Select(static status => ToWireStatus(status))
                .ToList(),
            Not = ToFilter(where.Not),
            And = where.And?.Select(ToFilter).OfType<ProjectListFilter>().ToList(),
            Or = where.Or?.Select(ToFilter).OfType<ProjectListFilter>().ToList(),
        };
    }

    /// <summary>
    /// The reference resolver rewrites CREATED_AT to ent.DefaultProjectOrder's
    /// field, which is <c>id</c> (ent/gql_pagination.go).
    /// </summary>
    public static ProjectSortField ToSortField(ProjectOrder? orderBy)
        => orderBy?.Field == ProjectOrderFieldValue.UpdatedAt
            ? ProjectSortField.UpdatedAt
            : ProjectSortField.Id;

    public static SortDirection ToSortDirection(ProjectOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    public static string ToWireStatus(ProjectStatusValue status)
        => status == ProjectStatusValue.Archived ? "archived" : "active";

    public static ProjectConnectionType BuildConnection(
        ListPage<ProjectRecord> page,
        ProjectSortField sortField)
    {
        var edges = page.Nodes
            .Select(node => new ProjectEdgeType
            {
                Node = UserProfileMapping.ToGraphQL(node),
                Cursor = new CursorValue(
                    node.Id,
                    sortField == ProjectSortField.UpdatedAt ? node.UpdatedAt : (object?)null),
            })
            .ToList();

        return new ProjectConnectionType
        {
            Edges = edges,
            PageInfo = BuildPageInfo(
                edges.Select(static edge => edge.Cursor).ToList(),
                page.HasNextPage,
                page.HasPreviousPage),
            TotalCount = page.TotalCount,
        };
    }

    private static PageInfoType BuildPageInfo(
        List<CursorValue> cursors,
        bool hasNextPage,
        bool hasPreviousPage) => new()
        {
            HasNextPage = hasNextPage,
            HasPreviousPage = hasPreviousPage,
            StartCursor = cursors.Count > 0 ? cursors[0] : null,
            EndCursor = cursors.Count > 0 ? cursors[^1] : null,
        };
}
