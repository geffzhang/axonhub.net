using HotChocolate;
using HotChocolate.Types;
using AxonHub.Api.Management;
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
/// Port of <c>OIDCIdentityWhereInput</c> (ent.graphql): every scalar field
/// predicate group and the <c>user</c> edge predicates.
/// </summary>
[GraphQLName("OIDCIdentityWhereInput")]
[InputObjectType("OIDCIdentityWhereInput")]
public sealed class OidcIdentityWhereInput
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

    public string? Issuer { get; set; }

    public string? IssuerNEQ { get; set; }

    public IReadOnlyList<string>? IssuerIn { get; set; }

    public IReadOnlyList<string>? IssuerNotIn { get; set; }

    public string? IssuerGT { get; set; }

    public string? IssuerGTE { get; set; }

    public string? IssuerLT { get; set; }

    public string? IssuerLTE { get; set; }

    public string? IssuerContains { get; set; }

    public string? IssuerHasPrefix { get; set; }

    public string? IssuerHasSuffix { get; set; }

    public string? IssuerEqualFold { get; set; }

    public string? IssuerContainsFold { get; set; }

    public string? Subject { get; set; }

    public string? SubjectNEQ { get; set; }

    public IReadOnlyList<string>? SubjectIn { get; set; }

    public IReadOnlyList<string>? SubjectNotIn { get; set; }

    public string? SubjectGT { get; set; }

    public string? SubjectGTE { get; set; }

    public string? SubjectLT { get; set; }

    public string? SubjectLTE { get; set; }

    public string? SubjectContains { get; set; }

    public string? SubjectHasPrefix { get; set; }

    public string? SubjectHasSuffix { get; set; }

    public string? SubjectEqualFold { get; set; }

    public string? SubjectContainsFold { get; set; }

    public string? Email { get; set; }

    public string? EmailNEQ { get; set; }

    public IReadOnlyList<string>? EmailIn { get; set; }

    public IReadOnlyList<string>? EmailNotIn { get; set; }

    public string? EmailGT { get; set; }

    public string? EmailGTE { get; set; }

    public string? EmailLT { get; set; }

    public string? EmailLTE { get; set; }

    public string? EmailContains { get; set; }

    public string? EmailHasPrefix { get; set; }

    public string? EmailHasSuffix { get; set; }

    public bool? EmailIsNil { get; set; }

    public bool? EmailNotNil { get; set; }

    public string? EmailEqualFold { get; set; }

    public string? EmailContainsFold { get; set; }

    public string? IdpName { get; set; }

    public string? IdpNameNEQ { get; set; }

    public IReadOnlyList<string>? IdpNameIn { get; set; }

    public IReadOnlyList<string>? IdpNameNotIn { get; set; }

    public string? IdpNameGT { get; set; }

    public string? IdpNameGTE { get; set; }

    public string? IdpNameLT { get; set; }

    public string? IdpNameLTE { get; set; }

    public string? IdpNameContains { get; set; }

    public string? IdpNameHasPrefix { get; set; }

    public string? IdpNameHasSuffix { get; set; }

    public bool? IdpNameIsNil { get; set; }

    public bool? IdpNameNotNil { get; set; }

    public string? IdpNameEqualFold { get; set; }

    public string? IdpNameContainsFold { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? LastLoginAt { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? LastLoginAtNEQ { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? LastLoginAtIn { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<GoTimeType>>))]
    public IReadOnlyList<DateTime>? LastLoginAtNotIn { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? LastLoginAtGT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? LastLoginAtGTE { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? LastLoginAtLT { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? LastLoginAtLTE { get; set; }

    public bool? LastLoginAtIsNil { get; set; }

    public bool? LastLoginAtNotNil { get; set; }

    [GraphQLName("userID")]
    [GraphQLType(typeof(IdType))]
    public string? UserId { get; set; }

    [GraphQLName("userIDNEQ")]
    [GraphQLType(typeof(IdType))]
    public string? UserIdNEQ { get; set; }

    [GraphQLName("userIDIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? UserIdIn { get; set; }

    [GraphQLName("userIDNotIn")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? UserIdNotIn { get; set; }

    public bool? HasUser { get; set; }

    public IReadOnlyList<UserWhereInput>? HasUserWith { get; set; }

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
    /// resolver calls obj.User(ctx), which falls back to
    /// <c>QueryUser().Only(ctx)</c> — the User query policy runs, so a deny
    /// propagates and a filtered-out row is ent NotFound.
    /// </summary>
    public async Task<UserType> GetUserAsync(
        [Service] UserPrivacy privacy,
        CancellationToken cancellationToken)
    {
        var record = await privacy.GetVisibleUserOrThrowAsync(DbUserId, cancellationToken)
            .ConfigureAwait(false);

        return ManagementMapping.ToGraphQL(record);
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
            Issuer = where.Issuer,
            IssuerNEQ = where.IssuerNEQ,
            IssuerIn = where.IssuerIn,
            IssuerNotIn = where.IssuerNotIn,
            IssuerGT = where.IssuerGT,
            IssuerGTE = where.IssuerGTE,
            IssuerLT = where.IssuerLT,
            IssuerLTE = where.IssuerLTE,
            IssuerContains = where.IssuerContains,
            IssuerHasPrefix = where.IssuerHasPrefix,
            IssuerHasSuffix = where.IssuerHasSuffix,
            IssuerEqualFold = where.IssuerEqualFold,
            IssuerContainsFold = where.IssuerContainsFold,
            Subject = where.Subject,
            SubjectNEQ = where.SubjectNEQ,
            SubjectIn = where.SubjectIn,
            SubjectNotIn = where.SubjectNotIn,
            SubjectGT = where.SubjectGT,
            SubjectGTE = where.SubjectGTE,
            SubjectLT = where.SubjectLT,
            SubjectLTE = where.SubjectLTE,
            SubjectContains = where.SubjectContains,
            SubjectHasPrefix = where.SubjectHasPrefix,
            SubjectHasSuffix = where.SubjectHasSuffix,
            SubjectEqualFold = where.SubjectEqualFold,
            SubjectContainsFold = where.SubjectContainsFold,
            Email = where.Email,
            EmailNEQ = where.EmailNEQ,
            EmailIn = where.EmailIn,
            EmailNotIn = where.EmailNotIn,
            EmailGT = where.EmailGT,
            EmailGTE = where.EmailGTE,
            EmailLT = where.EmailLT,
            EmailLTE = where.EmailLTE,
            EmailContains = where.EmailContains,
            EmailHasPrefix = where.EmailHasPrefix,
            EmailHasSuffix = where.EmailHasSuffix,
            EmailIsNil = where.EmailIsNil,
            EmailNotNil = where.EmailNotNil,
            EmailEqualFold = where.EmailEqualFold,
            EmailContainsFold = where.EmailContainsFold,
            IdpName = where.IdpName,
            IdpNameNEQ = where.IdpNameNEQ,
            IdpNameIn = where.IdpNameIn,
            IdpNameNotIn = where.IdpNameNotIn,
            IdpNameGT = where.IdpNameGT,
            IdpNameGTE = where.IdpNameGTE,
            IdpNameLT = where.IdpNameLT,
            IdpNameLTE = where.IdpNameLTE,
            IdpNameContains = where.IdpNameContains,
            IdpNameHasPrefix = where.IdpNameHasPrefix,
            IdpNameHasSuffix = where.IdpNameHasSuffix,
            IdpNameIsNil = where.IdpNameIsNil,
            IdpNameNotNil = where.IdpNameNotNil,
            IdpNameEqualFold = where.IdpNameEqualFold,
            IdpNameContainsFold = where.IdpNameContainsFold,
            LastLoginAt = where.LastLoginAt,
            LastLoginAtNEQ = where.LastLoginAtNEQ,
            LastLoginAtIn = where.LastLoginAtIn,
            LastLoginAtNotIn = where.LastLoginAtNotIn,
            LastLoginAtGT = where.LastLoginAtGT,
            LastLoginAtGTE = where.LastLoginAtGTE,
            LastLoginAtLT = where.LastLoginAtLT,
            LastLoginAtLTE = where.LastLoginAtLTE,
            LastLoginAtIsNil = where.LastLoginAtIsNil,
            LastLoginAtNotNil = where.LastLoginAtNotNil,
            UserId = where.UserId is null ? null : GidValue.Parse(where.UserId).Id,
            UserIdNEQ = where.UserIdNEQ is null
                ? null
                : GidValue.Parse(where.UserIdNEQ).Id,
            UserIdIn = where.UserIdIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            UserIdNotIn = where.UserIdNotIn?
                .Select(static id => GidValue.Parse(id).Id)
                .ToList(),
            HasUser = where.HasUser,
            HasUserWith = where.HasUserWith?
                .Select(ManagementMapping.ToFilter)
                .OfType<UserListFilter>()
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
