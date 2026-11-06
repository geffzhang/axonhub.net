using AxonHub.Api.Management;
using AxonHub.Persistence;
using HotChocolate;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the gqlgen <c>APIKeyProfileTemplateOrderField</c> enum.</summary>
[GraphQLName("APIKeyProfileTemplateOrderField")]
public enum ApiKeyProfileTemplateOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,
}

/// <summary>Port of <c>APIKeyProfileTemplateOrder</c> (ent.graphql:166-177).</summary>
[GraphQLName("APIKeyProfileTemplateOrder")]
[InputObjectType("APIKeyProfileTemplateOrder")]
public sealed class ApiKeyProfileTemplateOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    public ApiKeyProfileTemplateOrderFieldValue Field { get; set; } =
        ApiKeyProfileTemplateOrderFieldValue.CreatedAt;
}

/// <summary>
/// Port of <c>APIKeyProfileTemplateWhereInput</c> (ent.graphql:189-274). The
/// project edge supports the same subset as the shared
/// <see cref="ProjectWhereInput"/>.
/// </summary>
[GraphQLName("APIKeyProfileTemplateWhereInput")]
public sealed class ApiKeyProfileTemplateWhereInput
{
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

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string? ProjectID { get; set; }

    [GraphQLName("projectIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? ProjectIDNEQ { get; set; }

    [GraphQLName("projectIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ProjectIDIn { get; set; }

    [GraphQLName("projectIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ProjectIDNotIn { get; set; }

    public bool? HasProject { get; set; }

    public IReadOnlyList<ProjectWhereInput>? HasProjectWith { get; set; }

    public ApiKeyProfileTemplateWhereInput? Not { get; set; }

    public IReadOnlyList<ApiKeyProfileTemplateWhereInput>? And { get; set; }

    public IReadOnlyList<ApiKeyProfileTemplateWhereInput>? Or { get; set; }
}

/// <summary>
/// Port of <c>CreateAPIKeyProfileTemplateInput</c> (ent.graphql:1598-1610).
/// </summary>
[GraphQLName("CreateAPIKeyProfileTemplateInput")]
public sealed class CreateApiKeyProfileTemplateInput
{
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string ProjectID { get; set; } = string.Empty;
}

/// <summary>
/// Port of <c>UpdateAPIKeyProfileTemplateInput</c> (ent.graphql:6168-6180).
/// </summary>
[GraphQLName("UpdateAPIKeyProfileTemplateInput")]
public sealed class UpdateApiKeyProfileTemplateInput
{
    public string? Name { get; set; }

    public string? Description { get; set; }
}

/// <summary>
/// Port of <c>LoadApiKeyProfileTemplateInput</c> (axonhub.graphql:556-559). The
/// reference's Go binding type is LoadAPIKeyProfileTemplateInput while the SDL
/// name keeps the <c>Api</c> spelling.
/// </summary>
[GraphQLName("LoadApiKeyProfileTemplateInput")]
public sealed class LoadApiKeyProfileTemplateInput
{
    [GraphQLName("templateID")]
    [GraphQLType(typeof(IdType))]
    public string TemplateID { get; set; } = string.Empty;

    [GraphQLName("apiKeyID")]
    [GraphQLType(typeof(IdType))]
    public string ApiKeyID { get; set; } = string.Empty;
}

/// <summary>
/// Port of the <c>APIKeyProfileTemplate</c> object type (ent.graphql:116-137)
/// and its <c>linkedProfilesCount</c> extension (axonhub.graphql:551-554).
/// </summary>
[GraphQLName("APIKeyProfileTemplate")]
public sealed class ApiKeyProfileTemplateType : INode
{
    [GraphQLIgnore]
    public long DbId { get; init; }

    [GraphQLIgnore]
    public long ProjectDbId { get; init; }

    [GraphQLIgnore]
    public string? ProfileJson { get; init; }

    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string ProjectID { get; init; } = string.Empty;

    /// <summary>
    /// The stored objects.APIKeyProfile; SQL NULL and JSON <c>null</c> both
    /// surface as GraphQL null, like ent's scanned pointer.
    /// </summary>
    public ApiKeyProfileType? Profile { get; init; }

    /// <summary>
    /// Port of the entgql lazy Project edge (ent/gql_edge.go:48-54): the Project
    /// query policy applies, and a filtered-out project is NotFound.
    /// </summary>
    [GraphQLName("project")]
    public async Task<ProjectType> GetProjectAsync(
        [Service] ApiKeyPrivacy privacy,
        CancellationToken cancellationToken)
    {
        var project = await privacy.GetVisibleProjectAsync(ProjectDbId, cancellationToken)
            .ConfigureAwait(false);
        return project is null
            ? throw new InvalidOperationException("ent: project not found")
            : UserProfileMapping.ToGraphQL(project);
    }

    /// <summary>
    /// Port of aPIKeyProfileTemplateResolver.LinkedProfilesCount
    /// (axonhub.resolvers.go:29-32).
    /// </summary>
    [GraphQLName("linkedProfilesCount")]
    public Task<int> GetLinkedProfilesCountAsync(
        [Service] ApiKeyProfileTemplateService templates,
        CancellationToken cancellationToken)
        => templates.CountLinkedProfilesAsync(
            new ApiKeyProfileTemplateRecord
            {
                Id = DbId,
                Name = Name,
                ProjectId = ProjectDbId,
                Profile = ProfileJson,
            },
            cancellationToken);
}

/// <summary>Port of <c>APIKeyProfileTemplateEdge</c> (ent.graphql:155-164).</summary>
[GraphQLName("APIKeyProfileTemplateEdge")]
public sealed class ApiKeyProfileTemplateEdgeType
{
    public ApiKeyProfileTemplateType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>
/// Port of <c>APIKeyProfileTemplateConnection</c> (ent.graphql:138-152).
/// </summary>
[GraphQLName("APIKeyProfileTemplateConnection")]
public sealed class ApiKeyProfileTemplateConnectionType
{
    public IReadOnlyList<ApiKeyProfileTemplateEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>Domain ↔ GraphQL mapping for the profile template slice.</summary>
public static class ApiKeyProfileTemplateMapping
{
    public static ApiKeyProfileTemplateType ToGraphQL(
        ApiKeyProfileTemplateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var profile = ApiKeyProfilesCodec.DeserializeProfile(record.Profile);
        return new ApiKeyProfileTemplateType
        {
            DbId = record.Id,
            ProjectDbId = record.ProjectId,
            ProfileJson = record.Profile,
            Id = new GidValue("APIKeyProfileTemplate", record.Id).ToString(),
            CreatedAt = record.CreatedAt,
            UpdatedAt = record.UpdatedAt,
            Name = record.Name,
            Description = record.Description,
            ProjectID = new GidValue("Project", record.ProjectId).ToString(),
            Profile = profile is null ? null : ApiKeyMapping.ToGraphQL(profile),
        };
    }

    public static ApiKeyProfileTemplateListFilter? ToFilter(
        ApiKeyProfileTemplateWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new ApiKeyProfileTemplateListFilter
        {
            Id = where.Id is null ? null : GidValue.Parse(where.Id).Id,
            IdNEQ = where.IdNEQ is null ? null : GidValue.Parse(where.IdNEQ).Id,
            IdIn = where.IdIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
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
            ProjectId = where.ProjectID is null
                ? null
                : GidValue.Parse(where.ProjectID).Id,
            ProjectIdNEQ = where.ProjectIDNEQ is null
                ? null
                : GidValue.Parse(where.ProjectIDNEQ).Id,
            ProjectIdIn = where.ProjectIDIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            ProjectIdNotIn = where.ProjectIDNotIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            HasProject = where.HasProject,
            HasProjectWith = where.HasProjectWith?
                .Select(ToProjectFilter)
                .ToList(),
            Not = ToFilter(where.Not),
            And = where.And?
                .Select(ToFilter)
                .OfType<ApiKeyProfileTemplateListFilter>()
                .ToList(),
            Or = where.Or?
                .Select(ToFilter)
                .OfType<ApiKeyProfileTemplateListFilter>()
                .ToList(),
        };
    }

    /// <summary>
    /// CREATED_AT is remapped to ent.DefaultAPIKeyProfileTemplateOrder (id)
    /// before the query runs, so its edge cursor only carries the id.
    /// </summary>
    public static ApiKeyProfileTemplateSortField ToSortField(
        ApiKeyProfileTemplateOrder? orderBy) => orderBy?.Field switch
    {
        ApiKeyProfileTemplateOrderFieldValue.UpdatedAt =>
            ApiKeyProfileTemplateSortField.UpdatedAt,
        _ => ApiKeyProfileTemplateSortField.Id,
    };

    public static SortDirection ToSortDirection(ApiKeyProfileTemplateOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    public static ApiKeyProfileTemplateConnectionType BuildConnection(
        ListPage<ApiKeyProfileTemplateRecord> page,
        ApiKeyProfileTemplateSortField sortField)
    {
        ArgumentNullException.ThrowIfNull(page);

        var edges = page.Nodes
            .Select(node => new ApiKeyProfileTemplateEdgeType
            {
                Node = ToGraphQL(node),
                Cursor = new CursorValue(
                    node.Id,
                    sortField == ApiKeyProfileTemplateSortField.UpdatedAt
                        ? node.UpdatedAt
                        : null),
            })
            .ToList();

        return new ApiKeyProfileTemplateConnectionType
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

    private static ApiKeyProfileTemplateProjectFilter ToProjectFilter(
        ProjectWhereInput where) => new()
    {
        Id = where.Id is null ? null : GidValue.Parse(where.Id).Id,
        IdIn = where.IdIn?
            .Select(static id => GidValue.Parse(id).Id)
            .ToList(),
        Name = where.Name,
        NameContainsFold = where.NameContainsFold,
        Status = where.Status is null
            ? null
            : ProjectMapping.ToWireStatus(where.Status.Value),
        StatusIn = where.StatusIn?
            .Select(ProjectMapping.ToWireStatus)
            .ToList(),
        Not = where.Not is null ? null : ToProjectFilter(where.Not),
        And = where.And?
            .Select(ToProjectFilter)
            .ToList(),
        Or = where.Or?
            .Select(ToProjectFilter)
            .ToList(),
    };
}
