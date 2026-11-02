using System.Text.Json;
using HotChocolate;
using HotChocolate.Types;
using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the gqlgen <c>UserStatus</c> enum (ent.graphql:7211-7214).</summary>
[GraphQLName("UserStatus")]
public enum UserStatusValue
{
    [GraphQLName("activated")]
    Activated,

    [GraphQLName("deactivated")]
    Deactivated,
}

/// <summary>Port of the gqlgen <c>RoleLevel</c> enum (ent.graphql:5503-5506).</summary>
[GraphQLName("RoleLevel")]
public enum RoleLevelValue
{
    [GraphQLName("system")]
    System,

    [GraphQLName("project")]
    Project,
}

/// <summary>Port of the gqlgen <c>OrderDirection</c> enum.</summary>
[GraphQLName("OrderDirection")]
public enum OrderDirectionValue
{
    [GraphQLName("ASC")]
    Asc,

    [GraphQLName("DESC")]
    Desc,
}

/// <summary>Port of the gqlgen <c>UserOrderField</c> enum.</summary>
[GraphQLName("UserOrderField")]
public enum UserOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,
}

/// <summary>Port of the gqlgen <c>RoleOrderField</c> enum.</summary>
[GraphQLName("RoleOrderField")]
public enum RoleOrderFieldValue
{
    [GraphQLName("CREATED_AT")]
    CreatedAt,

    [GraphQLName("UPDATED_AT")]
    UpdatedAt,
}

/// <summary>Port of <c>UserOrder</c> (ent.graphql:6553-6562).</summary>
[GraphQLName("UserOrder")]
[InputObjectType("UserOrder")]
public sealed class UserOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    public UserOrderFieldValue Field { get; set; } = UserOrderFieldValue.CreatedAt;
}

/// <summary>Port of <c>RoleOrder</c> (ent.graphql:5510-5519).</summary>
[GraphQLName("RoleOrder")]
[InputObjectType("RoleOrder")]
public sealed class RoleOrder
{
    [DefaultValue(OrderDirectionValue.Asc)]
    public OrderDirectionValue Direction { get; set; } = OrderDirectionValue.Asc;

    public RoleOrderFieldValue Field { get; set; } = RoleOrderFieldValue.CreatedAt;
}

/// <summary>
/// Port of the subset of <c>UserWhereInput</c> used by the management frontend
/// (firstNameContainsFold, emailContainsFold, statusIn, id, and/or/not).
/// </summary>
public sealed class UserWhereInput
{
    [GraphQLType(typeof(IdType))]
    public string? Id { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdIn { get; set; }

    public string? FirstNameContainsFold { get; set; }

    public string? EmailContainsFold { get; set; }

    public IReadOnlyList<UserStatusValue>? StatusIn { get; set; }

    public UserWhereInput? Not { get; set; }

    public IReadOnlyList<UserWhereInput>? And { get; set; }

    public IReadOnlyList<UserWhereInput>? Or { get; set; }
}

/// <summary>
/// Port of the subset of <c>RoleWhereInput</c> used by the management frontend
/// (nameContainsFold, level, projectID, id, and/or/not).
/// </summary>
public sealed class RoleWhereInput
{
    [GraphQLType(typeof(IdType))]
    public string? Id { get; set; }

    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? IdIn { get; set; }

    public string? NameContainsFold { get; set; }

    public RoleLevelValue? Level { get; set; }

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string? ProjectId { get; set; }

    public RoleWhereInput? Not { get; set; }

    public IReadOnlyList<RoleWhereInput>? And { get; set; }

    public IReadOnlyList<RoleWhereInput>? Or { get; set; }
}

/// <summary>Port of <c>CreateUserInput</c> (ent.graphql:2025-2046).</summary>
public sealed class CreateUserInput
{
    public string Email { get; set; } = string.Empty;

    public UserStatusValue? Status { get; set; }

    public string? PreferLanguage { get; set; }

    public string Password { get; set; } = string.Empty;

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? Avatar { get; set; }

    public bool? IsOwner { get; set; }

    public IReadOnlyList<string>? Scopes { get; set; }

    [GraphQLName("projectIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? ProjectIDs { get; set; }

    [GraphQLName("roleIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? RoleIDs { get; set; }
}

/// <summary>Port of <c>UpdateUserInput</c> (ent.graphql:6614-6642).</summary>
public sealed class UpdateUserInput
{
    public string? Email { get; set; }

    public UserStatusValue? Status { get; set; }

    public string? PreferLanguage { get; set; }

    public string? Password { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? Avatar { get; set; }

    public bool? ClearAvatar { get; set; }

    public bool? IsOwner { get; set; }

    public IReadOnlyList<string>? Scopes { get; set; }

    public IReadOnlyList<string>? AppendScopes { get; set; }

    public bool? ClearScopes { get; set; }

    [GraphQLName("addProjectIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? AddProjectIDs { get; set; }

    [GraphQLName("removeProjectIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? RemoveProjectIDs { get; set; }

    public bool? ClearProjects { get; set; }

    [GraphQLName("addRoleIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? AddRoleIDs { get; set; }

    [GraphQLName("removeRoleIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? RemoveRoleIDs { get; set; }

    public bool? ClearRoles { get; set; }
}

/// <summary>Port of <c>CreateRoleInput</c> (ent.graphql:1887-1899).</summary>
public sealed class CreateRoleInput
{
    public string Name { get; set; } = string.Empty;

    public RoleLevelValue? Level { get; set; }

    public IReadOnlyList<string>? Scopes { get; set; }

    [GraphQLName("userIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? UserIDs { get; set; }

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string? ProjectID { get; set; }
}

/// <summary>Port of <c>UpdateRoleInput</c> (ent.graphql:6481-6494).</summary>
public sealed class UpdateRoleInput
{
    public string? Name { get; set; }

    public IReadOnlyList<string>? Scopes { get; set; }

    public IReadOnlyList<string>? AppendScopes { get; set; }

    public bool? ClearScopes { get; set; }

    [GraphQLName("addUserIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? AddUserIDs { get; set; }

    [GraphQLName("removeUserIDs")]
    [GraphQLType(typeof(ListType<NonNullType<IdType>>))]
    public IReadOnlyList<string>? RemoveUserIDs { get; set; }

    public bool? ClearUsers { get; set; }

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string? ProjectID { get; set; }

    public bool? ClearProject { get; set; }
}

/// <summary>Port of the <c>User</c> object type (ent.graphql:7107-...).</summary>
[GraphQLName("User")]
public sealed class UserType
{
    [GraphQLIgnore]
    public long DbId { get; init; }

    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt { get; init; }

    public string Email { get; init; } = string.Empty;

    public UserStatusValue Status { get; init; }

    public string PreferLanguage { get; init; } = string.Empty;

    public string FirstName { get; init; } = string.Empty;

    public string LastName { get; init; } = string.Empty;

    public string? Avatar { get; init; }

    public bool IsOwner { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }

    /// <summary>Port of the entgql edge connection User.roles.</summary>
    [GraphQLName("roles")]
    public async Task<RoleConnectionType> GetRolesAsync(
        [GraphQLType(typeof(CursorType))] CursorValue? after,
        int? first,
        [GraphQLType(typeof(CursorType))] CursorValue? before,
        int? last,
        RoleOrder? orderBy,
        RoleWhereInput? where,
        [Service] IRoleStore roles,
        CancellationToken cancellationToken)
    {
        // entgql's generated edge resolver routes through RoleQuery.Paginate,
        // which validates first/last but allows both to be nil (unbounded).
        ManagementValidation.ValidateFirstLast(first, last);

        var sortField = ManagementMapping.ToRoleSortField(orderBy);
        var page = await roles.ListAsync(
                new RoleListQuery
                {
                    UserId = DbId,
                    Where = ManagementMapping.ToFilter(where),
                    SortField = sortField,
                    Direction = ManagementMapping.ToSortDirection(orderBy),
                    After = ManagementMapping.ToListCursor(after),
                    Before = ManagementMapping.ToListCursor(before),
                    First = first,
                    Last = last,
                },
                cancellationToken)
            .ConfigureAwait(false);

        return ManagementMapping.BuildConnection(page, sortField);
    }
}

/// <summary>Port of the <c>Role</c> object type (ent.graphql:5419-...).</summary>
[GraphQLName("Role")]
public sealed class RoleType
{
    [GraphQLIgnore]
    public long DbId { get; init; }

    [GraphQLType(typeof(IdType))]
    public string Id { get; init; } = string.Empty;

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CreatedAt { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime UpdatedAt { get; init; }

    public string Name { get; init; } = string.Empty;

    public RoleLevelValue Level { get; init; }

    [GraphQLName("projectID")]
    [GraphQLType(typeof(IdType))]
    public string? ProjectId { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }
}

/// <summary>Port of <c>UserEdge</c> (ent.graphql).</summary>
[GraphQLName("UserEdge")]
public sealed class UserEdgeType
{
    public UserType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of <c>RoleEdge</c> (ent.graphql:5490-5499).</summary>
[GraphQLName("RoleEdge")]
public sealed class RoleEdgeType
{
    public RoleType? Node { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue Cursor { get; init; }
}

/// <summary>Port of <c>UserConnection</c> (edges nullable, pageInfo non-null).</summary>
[GraphQLName("UserConnection")]
public sealed class UserConnectionType
{
    public IReadOnlyList<UserEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>Port of <c>RoleConnection</c> (ent.graphql:5473-5486).</summary>
[GraphQLName("RoleConnection")]
public sealed class RoleConnectionType
{
    public IReadOnlyList<RoleEdgeType?>? Edges { get; init; }

    public PageInfoType PageInfo { get; init; } = new();

    public int TotalCount { get; init; }
}

/// <summary>Port of the gqlgen <c>PageInfo</c> type.</summary>
[GraphQLName("PageInfo")]
public sealed class PageInfoType
{
    public bool HasNextPage { get; init; }

    public bool HasPreviousPage { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue? StartCursor { get; init; }

    [GraphQLType(typeof(CursorType))]
    public CursorValue? EndCursor { get; init; }
}

/// <summary>Port of gql's validatePaginationArgs (internal/server/gql/pagination.go).</summary>
public static class ManagementValidation
{
    public const int MaxPaginationLimit = 1000;

    public static void ValidatePaginationArgs(int? first, int? last)
    {
        var provided = false;

        if (first is not null)
        {
            provided = true;

            if (first <= 0)
            {
                throw new InvalidOperationException("first must be greater than 0");
            }

            if (first > MaxPaginationLimit)
            {
                throw new InvalidOperationException(
                    $"first cannot exceed {MaxPaginationLimit}");
            }
        }

        if (last is not null)
        {
            provided = true;

            if (last <= 0)
            {
                throw new InvalidOperationException("last must be greater than 0");
            }

            if (last > MaxPaginationLimit)
            {
                throw new InvalidOperationException(
                    $"last cannot exceed {MaxPaginationLimit}");
            }
        }

        if (!provided)
        {
            throw new InvalidOperationException("either first or last must be provided");
        }
    }

    /// <summary>Port of entgql's validateFirstLast.</summary>
    public static void ValidateFirstLast(int? first, int? last)
    {
        if (first is not null && last is not null)
        {
            throw new InvalidOperationException(
                "Passing both `first` and `last` to paginate a connection is not supported.");
        }

        if (first is < 0)
        {
            throw new InvalidOperationException(
                "`first` on a connection cannot be less than zero.");
        }

        if (last is < 0)
        {
            throw new InvalidOperationException(
                "`last` on a connection cannot be less than zero.");
        }
    }
}

/// <summary>Domain ↔ GraphQL mapping for the users/roles slice.</summary>
public static class ManagementMapping
{
    public static UserType ToGraphQL(UserRecord record) => new()
    {
        DbId = record.Id,
        Id = new GidValue("User", record.Id).ToString(),
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        Email = record.Email,
        Status = ToGraphQLStatus(record.Status),
        PreferLanguage = record.PreferLanguage,
        FirstName = record.FirstName,
        LastName = record.LastName,
        Avatar = record.Avatar,
        IsOwner = record.IsOwner,
        Scopes = ParseScopes(record.Scopes),
    };

    public static RoleType ToGraphQL(RoleRecord record) => new()
    {
        DbId = record.Id,
        Id = new GidValue("Role", record.Id).ToString(),
        CreatedAt = record.CreatedAt,
        UpdatedAt = record.UpdatedAt,
        Name = record.Name,
        Level = ToGraphQLLevel(record.Level),
        ProjectId = record.ProjectId is null
            ? null
            : new GidValue("Project", record.ProjectId.Value).ToString(),
        Scopes = ParseScopes(record.Scopes),
    };

    public static UserConnectionType BuildConnection(
        ListPage<UserRecord> page,
        UserSortField sortField)
    {
        var edges = page.Nodes
            .Select(node => new UserEdgeType
            {
                Node = ToGraphQL(node),
                Cursor = new CursorValue(
                    node.Id,
                    sortField == UserSortField.UpdatedAt ? node.UpdatedAt : (object?)null),
            })
            .ToList();

        return new UserConnectionType
        {
            Edges = edges,
            PageInfo = BuildPageInfo(
                edges.Select(static edge => edge.Cursor).ToList(),
                page.HasNextPage,
                page.HasPreviousPage),
            TotalCount = page.TotalCount,
        };
    }

    public static RoleConnectionType BuildConnection(
        ListPage<RoleRecord> page,
        RoleSortField sortField)
    {
        var edges = page.Nodes
            .Select(node => new RoleEdgeType
            {
                Node = ToGraphQL(node),
                Cursor = new CursorValue(
                    node.Id,
                    sortField == RoleSortField.UpdatedAt ? node.UpdatedAt : (object?)null),
            })
            .ToList();

        return new RoleConnectionType
        {
            Edges = edges,
            PageInfo = BuildPageInfo(
                edges.Select(static edge => edge.Cursor).ToList(),
                page.HasNextPage,
                page.HasPreviousPage),
            TotalCount = page.TotalCount,
        };
    }

    public static UserListFilter? ToFilter(UserWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new UserListFilter
        {
            Id = where.Id is null ? null : GidValue.Parse(where.Id).Id,
            IdIn = where.IdIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            FirstNameContainsFold = where.FirstNameContainsFold,
            EmailContainsFold = where.EmailContainsFold,
            StatusIn = where.StatusIn?
                .Select(static status => status == UserStatusValue.Activated
                    ? "activated"
                    : "deactivated")
                .ToList(),
            Not = ToFilter(where.Not),
            And = where.And?.Select(ToFilter).OfType<UserListFilter>().ToList(),
            Or = where.Or?.Select(ToFilter).OfType<UserListFilter>().ToList(),
        };
    }

    public static RoleListFilter? ToFilter(RoleWhereInput? where)
    {
        if (where is null)
        {
            return null;
        }

        return new RoleListFilter
        {
            Id = where.Id is null ? null : GidValue.Parse(where.Id).Id,
            IdIn = where.IdIn?.Select(static id => GidValue.Parse(id).Id).ToList(),
            NameContainsFold = where.NameContainsFold,
            Level = where.Level is null
                ? null
                : where.Level == RoleLevelValue.System
                    ? RoleManagementService.LevelSystem
                    : RoleManagementService.LevelProject,
            ProjectId = where.ProjectId is null
                ? null
                : GidValue.Parse(where.ProjectId).Id,
            Not = ToFilter(where.Not),
            And = where.And?.Select(ToFilter).OfType<RoleListFilter>().ToList(),
            Or = where.Or?.Select(ToFilter).OfType<RoleListFilter>().ToList(),
        };
    }

    public static ListCursor? ToListCursor(CursorValue? cursor)
        => cursor is null ? null : new ListCursor(cursor.Value.Id, cursor.Value.Value);

    public static UserSortField ToUserSortField(UserOrder? orderBy)
        // The reference resolver maps CREATED_AT to ent.DefaultUserOrder (id).
        => orderBy?.Field == UserOrderFieldValue.UpdatedAt
            ? UserSortField.UpdatedAt
            : UserSortField.Id;

    public static RoleSortField ToRoleSortField(RoleOrder? orderBy)
        => orderBy?.Field == RoleOrderFieldValue.UpdatedAt
            ? RoleSortField.UpdatedAt
            : RoleSortField.Id;

    public static SortDirection ToSortDirection(UserOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    public static SortDirection ToSortDirection(RoleOrder? orderBy)
        => orderBy?.Direction == OrderDirectionValue.Desc
            ? SortDirection.Desc
            : SortDirection.Asc;

    public static IReadOnlyList<long>? ParseGidList(IReadOnlyList<string>? values)
        => values?.Select(static value => GidValue.Parse(value).Id).ToList();

    public static IReadOnlyList<string>? ParseScopes(string? json)
    {
        if (json is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<List<string>>(json) ?? [];
    }

    public static UserStatusValue ToGraphQLStatus(string status) => status switch
    {
        "activated" => UserStatusValue.Activated,
        "deactivated" => UserStatusValue.Deactivated,
        _ => throw new GraphQLException(
            "Enum \"UserStatus\" cannot represent value: \"" + status + "\""),
    };

    public static RoleLevelValue ToGraphQLLevel(string level) => level switch
    {
        RoleManagementService.LevelSystem => RoleLevelValue.System,
        RoleManagementService.LevelProject => RoleLevelValue.Project,
        _ => throw new GraphQLException(
            "Enum \"RoleLevel\" cannot represent value: \"" + level + "\""),
    };

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
