using HotChocolate;
using HotChocolate.Types;
using AxonHub.Persistence;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the gqlgen <c>SystemOrderField</c> enum (ent.graphql).</summary>
[GraphQLName("SystemOrderField")]
public enum SystemOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,
}

/// <summary>Port of <c>SystemOrder</c> (ent.graphql:5665-5674).</summary>
[GraphQLName("SystemOrder")]
[InputObjectType("SystemOrder")]
public sealed class SystemOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    public SystemOrderFieldValue Field { get; set; }
}

/// <summary>
/// Port of the subset of <c>SystemWhereInput</c> used by the root
/// <c>systems</c> query (id, key and value filters). The remaining entgql
/// scalar predicates are not modelled (C269).
/// </summary>
public sealed class SystemWhereInput
{
    [GraphQLType(typeof(IdType))]
    public string? Id { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdIn { get; set; }

    public string? Key { get; set; }

    public IReadOnlyList<string>? KeyIn { get; set; }

    public string? KeyHasPrefix { get; set; }

    public string? KeyContainsFold { get; set; }

    public string? ValueContainsFold { get; set; }

    public SystemWhereInput? Not { get; set; }

    public IReadOnlyList<SystemWhereInput>? And { get; set; }

    public IReadOnlyList<SystemWhereInput>? Or { get; set; }
}

/// <summary>Port of the entgql <c>System</c> type (ent.graphql:5616-5624).</summary>
[GraphQLName("System")]
public sealed class SystemType : INode
{
    [GraphQLIgnore]
    public long DbId { get; init; }

    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt { get; init; }

    public string Key { get; init; } = string.Empty;

    public string Value { get; init; } = string.Empty;
}

/// <summary>Port of <c>SystemEdge</c> (ent.graphql:5638-5649).</summary>
[GraphQLName("SystemEdge")]
public sealed class SystemEdgeType
{
    public SystemType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of <c>SystemConnection</c> (ent.graphql:5626-5636).</summary>
[GraphQLName("SystemConnection")]
public sealed class SystemConnectionType
{
    public IReadOnlyList<SystemEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>Port of the gqlgen <c>OIDCIdentityOrderField</c> enum (ent.graphql).</summary>
[GraphQLName("OIDCIdentityOrderField")]
public enum OidcIdentityOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,
}

/// <summary>Port of <c>OIDCIdentityOrder</c> (ent.graphql:2648-2657).</summary>
[GraphQLName("OIDCIdentityOrder")]
[InputObjectType("OIDCIdentityOrder")]
public sealed class OidcIdentityOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    public OidcIdentityOrderFieldValue Field { get; set; }
}

/// <summary>
/// Port of the subset of <c>OIDCIdentityWhereInput</c> used by the root
/// <c>oidcIdentities</c> query. The remaining entgql scalar predicates and the
/// <c>hasUser</c> edge predicates are not modelled (C269).
/// </summary>
[GraphQLName("OIDCIdentityWhereInput")]
[InputObjectType("OIDCIdentityWhereInput")]
public sealed class OidcIdentityWhereInput
{
    [GraphQLType(typeof(IdType))]
    public string? Id { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdIn { get; set; }

    public string? Issuer { get; set; }

    public IReadOnlyList<string>? IssuerIn { get; set; }

    public string? Subject { get; set; }

    public IReadOnlyList<string>? SubjectIn { get; set; }

    public string? EmailContainsFold { get; set; }

    public string? IdpNameContainsFold { get; set; }

    [GraphQLName("userID")]
    [GraphQLType(typeof(IdType))]
    public string? UserId { get; set; }

    [GraphQLName("userIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? UserIdIn { get; set; }

    public OidcIdentityWhereInput? Not { get; set; }

    public IReadOnlyList<OidcIdentityWhereInput>? And { get; set; }

    public IReadOnlyList<OidcIdentityWhereInput>? Or { get; set; }
}

/// <summary>Port of the entgql <c>OIDCIdentity</c> type (ent.graphql:2586-2614).</summary>
[GraphQLName("OIDCIdentity")]
public sealed class OidcIdentityType : INode
{
    [GraphQLIgnore]
    public long DbId { get; init; }

    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt { get; init; }

    public string Issuer { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string? Email { get; init; }

    public string? IdpName { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? LastLoginAt { get; init; }

    [GraphQLName("userID")]
    [GraphQLType(typeof(IdType))]
    public string UserId { get; init; } = string.Empty;

    [GraphQLIgnore]
    public long DbUserId { get; init; }

    /// <summary>
    /// Port of the entgql required edge OIDCIdentity.user: the generated
    /// resolver calls obj.User(ctx), which loads the user whose id is the
    /// identity's user_id edge.
    /// </summary>
    public async Task<UserType> GetUserAsync(
        [Service] IUserAdminStore users,
        CancellationToken cancellationToken)
    {
        var record = await users.GetByIdAsync(DbUserId, cancellationToken)
            .ConfigureAwait(false);

        return record is null
            ? throw new InvalidOperationException("ent: user not found")
            : ManagementMapping.ToGraphQL(record);
    }
}

/// <summary>Port of <c>OIDCIdentityEdge</c> (ent.graphql:2626-2637).</summary>
[GraphQLName("OIDCIdentityEdge")]
public sealed class OidcIdentityEdgeType
{
    public OidcIdentityType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of <c>OIDCIdentityConnection</c> (ent.graphql:2616-2624).</summary>
[GraphQLName("OIDCIdentityConnection")]
public sealed class OidcIdentityConnectionType
{
    public IReadOnlyList<OidcIdentityEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>Domain ↔ GraphQL mapping for the systems/oidcIdentities slice.</summary>
public static class SystemAdminMapping
{
    public static SystemType ToGraphQL(SystemRecord record) => new()
    {
        DbId = record.Id,
        Id = new GidValue("System", record.Id).ToString(),
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        Key = record.Key,
        Value = record.Value,
    };

    public static OidcIdentityType ToGraphQL(OidcIdentityRecord record) => new()
    {
        DbId = record.Id,
        Id = new GidValue("OIDCIdentity", record.Id).ToString(),
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        Issuer = record.Issuer,
        Subject = record.Subject,
        Email = record.Email,
        IdpName = record.IdpName,
        LastLoginAt = record.LastLoginAt,
        UserId = new GidValue("User", record.UserId).ToString(),
        DbUserId = record.UserId,
    };

    public static SystemConnectionType BuildConnection(
        ListPage<SystemRecord> page,
        SystemSortField sortField)
    {
        ArgumentNullException.ThrowIfNull(page);

        var cursors = page.Nodes
            .Select(node => new CursorValue(
                node.Id,
                sortField switch
                {
                    SystemSortField.CreatedAt => node.CreatedAt,
                    SystemSortField.UpdatedAt => node.UpdatedAt,
                    _ => null,
                }))
            .ToList();

        return new SystemConnectionType
        {
            Edges = page.Nodes
                .Select((node, index) => new SystemEdgeType
                {
                    Node = ToGraphQL(node),
                    Cursor = cursors[index],
                })
                .ToList(),
            PageInfo = BuildPageInfo(cursors, page.HasNextPage, page.HasPreviousPage),
            TotalCount = page.TotalCount,
        };
    }

    public static OidcIdentityConnectionType BuildConnection(
        ListPage<OidcIdentityRecord> page,
        OidcIdentitySortField sortField)
    {
        ArgumentNullException.ThrowIfNull(page);

        var cursors = page.Nodes
            .Select(node => new CursorValue(
                node.Id,
                sortField switch
                {
                    OidcIdentitySortField.CreatedAt => node.CreatedAt,
                    OidcIdentitySortField.UpdatedAt => node.UpdatedAt,
                    _ => null,
                }))
            .ToList();

        return new OidcIdentityConnectionType
        {
            Edges = page.Nodes
                .Select((node, index) => new OidcIdentityEdgeType
                {
                    Node = ToGraphQL(node),
                    Cursor = cursors[index],
                })
                .ToList(),
            PageInfo = BuildPageInfo(cursors, page.HasNextPage, page.HasPreviousPage),
            TotalCount = page.TotalCount,
        };
    }

    public static SystemListFilter? ToFilter(SystemWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new SystemListFilter
        {
            Id = where.Id is null ? null : GidValue.Parse(where.Id).Id,
            IdIn = where.IdIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            Key = where.Key,
            KeyIn = where.KeyIn,
            KeyHasPrefix = where.KeyHasPrefix,
            KeyContainsFold = where.KeyContainsFold,
            ValueContainsFold = where.ValueContainsFold,
            Not = ToFilter(where.Not),
            And = where.And?.Select(ToFilter).OfType<SystemListFilter>().ToList(),
            Or = where.Or?.Select(ToFilter).OfType<SystemListFilter>().ToList(),
        };
    }

    public static OidcIdentityListFilter? ToFilter(OidcIdentityWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new OidcIdentityListFilter
        {
            Id = where.Id is null ? null : GidValue.Parse(where.Id).Id,
            IdIn = where.IdIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            Issuer = where.Issuer,
            IssuerIn = where.IssuerIn,
            Subject = where.Subject,
            SubjectIn = where.SubjectIn,
            EmailContainsFold = where.EmailContainsFold,
            IdpNameContainsFold = where.IdpNameContainsFold,
            UserId = where.UserId is null ? null : GidValue.Parse(where.UserId).Id,
            UserIdIn = where.UserIdIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            Not = ToFilter(where.Not),
            And = where.And?.Select(ToFilter).OfType<OidcIdentityListFilter>().ToList(),
            Or = where.Or?.Select(ToFilter).OfType<OidcIdentityListFilter>().ToList(),
        };
    }

    /// <summary>
    /// The reference resolver passes the order straight to Paginate (there is
    /// no CREATED_AT → default rewrite like users/roles); a missing orderBy
    /// means the default id order.
    /// </summary>
    public static SystemSortField ToSortField(SystemOrder? orderBy)
        => orderBy?.Field switch
        {
            SystemOrderFieldValue.CreatedAt => SystemSortField.CreatedAt,
            SystemOrderFieldValue.UpdatedAt => SystemSortField.UpdatedAt,
            _ => SystemSortField.Id,
        };

    public static OidcIdentitySortField ToSortField(OidcIdentityOrder? orderBy)
        => orderBy?.Field switch
        {
            OidcIdentityOrderFieldValue.CreatedAt => OidcIdentitySortField.CreatedAt,
            OidcIdentityOrderFieldValue.UpdatedAt => OidcIdentitySortField.UpdatedAt,
            _ => OidcIdentitySortField.Id,
        };

    public static SortDirection ToSortDirection(SystemOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    public static SortDirection ToSortDirection(OidcIdentityOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

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
