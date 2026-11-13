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
/// Port of <c>UserWhereInput</c> (ent.graphql:7506-7672). All scalar field
/// predicates are modeled; the seven edge predicates (hasProjects, hasAPIKeys,
/// hasRoles, hasChannelOverrideTemplates, hasOidcIdentities, hasProjectUsers,
/// hasUserRoles) remain a registered gap (C135).
/// </summary>
public sealed class UserWhereInput
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

    public string? EmailEqualFold { get; set; }

    public string? EmailContainsFold { get; set; }

    public UserStatusValue? Status { get; set; }

    public UserStatusValue? StatusNEQ { get; set; }

    public IReadOnlyList<UserStatusValue>? StatusIn { get; set; }

    public IReadOnlyList<UserStatusValue>? StatusNotIn { get; set; }

    public string? PreferLanguage { get; set; }

    public string? PreferLanguageNEQ { get; set; }

    public IReadOnlyList<string>? PreferLanguageIn { get; set; }

    public IReadOnlyList<string>? PreferLanguageNotIn { get; set; }

    public string? PreferLanguageGT { get; set; }

    public string? PreferLanguageGTE { get; set; }

    public string? PreferLanguageLT { get; set; }

    public string? PreferLanguageLTE { get; set; }

    public string? PreferLanguageContains { get; set; }

    public string? PreferLanguageHasPrefix { get; set; }

    public string? PreferLanguageHasSuffix { get; set; }

    public string? PreferLanguageEqualFold { get; set; }

    public string? PreferLanguageContainsFold { get; set; }

    public string? FirstName { get; set; }

    public string? FirstNameNEQ { get; set; }

    public IReadOnlyList<string>? FirstNameIn { get; set; }

    public IReadOnlyList<string>? FirstNameNotIn { get; set; }

    public string? FirstNameGT { get; set; }

    public string? FirstNameGTE { get; set; }

    public string? FirstNameLT { get; set; }

    public string? FirstNameLTE { get; set; }

    public string? FirstNameContains { get; set; }

    public string? FirstNameHasPrefix { get; set; }

    public string? FirstNameHasSuffix { get; set; }

    public string? FirstNameEqualFold { get; set; }

    public string? FirstNameContainsFold { get; set; }

    public string? LastName { get; set; }

    public string? LastNameNEQ { get; set; }

    public IReadOnlyList<string>? LastNameIn { get; set; }

    public IReadOnlyList<string>? LastNameNotIn { get; set; }

    public string? LastNameGT { get; set; }

    public string? LastNameGTE { get; set; }

    public string? LastNameLT { get; set; }

    public string? LastNameLTE { get; set; }

    public string? LastNameContains { get; set; }

    public string? LastNameHasPrefix { get; set; }

    public string? LastNameHasSuffix { get; set; }

    public string? LastNameEqualFold { get; set; }

    public string? LastNameContainsFold { get; set; }

    public string? Avatar { get; set; }

    public string? AvatarNEQ { get; set; }

    public IReadOnlyList<string>? AvatarIn { get; set; }

    public IReadOnlyList<string>? AvatarNotIn { get; set; }

    public string? AvatarGT { get; set; }

    public string? AvatarGTE { get; set; }

    public string? AvatarLT { get; set; }

    public string? AvatarLTE { get; set; }

    public string? AvatarContains { get; set; }

    public string? AvatarHasPrefix { get; set; }

    public string? AvatarHasSuffix { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? AvatarIsNil { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool? AvatarNotNil { get; set; }

    public string? AvatarEqualFold { get; set; }

    public string? AvatarContainsFold { get; set; }

    public bool? IsOwner { get; set; }

    public bool? IsOwnerNEQ { get; set; }

    public UserWhereInput? Not { get; set; }

    public IReadOnlyList<UserWhereInput>? And { get; set; }

    public IReadOnlyList<UserWhereInput>? Or { get; set; }
}

/// <summary>
/// Port of <c>RoleWhereInput</c> (ent.graphql:5531-5615). All scalar field
/// predicates are modeled; the three edge predicates (hasUsers, hasProject,
/// hasUserRoles) remain a registered gap (C135).
/// </summary>
public sealed class RoleWhereInput
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

    public RoleLevelValue? Level { get; set; }

    public RoleLevelValue? LevelNEQ { get; set; }

    public IReadOnlyList<RoleLevelValue>? LevelIn { get; set; }

    public IReadOnlyList<RoleLevelValue>? LevelNotIn { get; set; }

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

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    [GraphQLName("projectIDIsNil")]
    public bool? ProjectIDIsNil { get; set; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    [GraphQLName("projectIDNotNil")]
    public bool? ProjectIDNotNil { get; set; }

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
public sealed class UserType : INode
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
        [Service] RolePrivacy privacy,
        CancellationToken cancellationToken)
    {
        // entgql's generated edge resolver routes through RoleQuery.Paginate,
        // which validates first/last but allows both to be nil (unbounded).
        ManagementValidation.ValidateFirstLast(first, last);

        // The Role query policy installs WhereProjectID(ctx) before the edge
        // page runs (ent.graphql User.roles → obj.QueryRoles()).
        var projectScope = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        var filter = ManagementMapping.ToFilter(where);
        if (projectScope is not null)
        {
            var scope = new RoleListFilter { ProjectId = projectScope.Value };
            filter = filter is null
                ? scope
                : new RoleListFilter { And = [filter, scope] };
        }

        var sortField = ManagementMapping.ToRoleSortField(orderBy);
        var page = await roles.ListAsync(
                new RoleListQuery
                {
                    UserId = DbId,
                    Where = filter,
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
            EmailEqualFold = where.EmailEqualFold,
            EmailContainsFold = where.EmailContainsFold,
            Status = where.Status is null ? null : ToWire(where.Status.Value),
            StatusNEQ = where.StatusNEQ is null ? null : ToWire(where.StatusNEQ.Value),
            StatusIn = where.StatusIn?
                .Select(static status => ToWire(status))
                .ToList(),
            StatusNotIn = where.StatusNotIn?
                .Select(static status => ToWire(status))
                .ToList(),
            PreferLanguage = where.PreferLanguage,
            PreferLanguageNEQ = where.PreferLanguageNEQ,
            PreferLanguageIn = where.PreferLanguageIn,
            PreferLanguageNotIn = where.PreferLanguageNotIn,
            PreferLanguageGT = where.PreferLanguageGT,
            PreferLanguageGTE = where.PreferLanguageGTE,
            PreferLanguageLT = where.PreferLanguageLT,
            PreferLanguageLTE = where.PreferLanguageLTE,
            PreferLanguageContains = where.PreferLanguageContains,
            PreferLanguageHasPrefix = where.PreferLanguageHasPrefix,
            PreferLanguageHasSuffix = where.PreferLanguageHasSuffix,
            PreferLanguageEqualFold = where.PreferLanguageEqualFold,
            PreferLanguageContainsFold = where.PreferLanguageContainsFold,
            FirstName = where.FirstName,
            FirstNameNEQ = where.FirstNameNEQ,
            FirstNameIn = where.FirstNameIn,
            FirstNameNotIn = where.FirstNameNotIn,
            FirstNameGT = where.FirstNameGT,
            FirstNameGTE = where.FirstNameGTE,
            FirstNameLT = where.FirstNameLT,
            FirstNameLTE = where.FirstNameLTE,
            FirstNameContains = where.FirstNameContains,
            FirstNameHasPrefix = where.FirstNameHasPrefix,
            FirstNameHasSuffix = where.FirstNameHasSuffix,
            FirstNameEqualFold = where.FirstNameEqualFold,
            FirstNameContainsFold = where.FirstNameContainsFold,
            LastName = where.LastName,
            LastNameNEQ = where.LastNameNEQ,
            LastNameIn = where.LastNameIn,
            LastNameNotIn = where.LastNameNotIn,
            LastNameGT = where.LastNameGT,
            LastNameGTE = where.LastNameGTE,
            LastNameLT = where.LastNameLT,
            LastNameLTE = where.LastNameLTE,
            LastNameContains = where.LastNameContains,
            LastNameHasPrefix = where.LastNameHasPrefix,
            LastNameHasSuffix = where.LastNameHasSuffix,
            LastNameEqualFold = where.LastNameEqualFold,
            LastNameContainsFold = where.LastNameContainsFold,
            Avatar = where.Avatar,
            AvatarNEQ = where.AvatarNEQ,
            AvatarIn = where.AvatarIn,
            AvatarNotIn = where.AvatarNotIn,
            AvatarGT = where.AvatarGT,
            AvatarGTE = where.AvatarGTE,
            AvatarLT = where.AvatarLT,
            AvatarLTE = where.AvatarLTE,
            AvatarContains = where.AvatarContains,
            AvatarHasPrefix = where.AvatarHasPrefix,
            AvatarHasSuffix = where.AvatarHasSuffix,
            AvatarIsNil = where.AvatarIsNil == true,
            AvatarNotNil = where.AvatarNotNil == true,
            AvatarEqualFold = where.AvatarEqualFold,
            AvatarContainsFold = where.AvatarContainsFold,
            IsOwner = where.IsOwner,
            IsOwnerNEQ = where.IsOwnerNEQ,
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
            Level = where.Level is null ? null : ToWire(where.Level.Value),
            LevelNEQ = where.LevelNEQ is null ? null : ToWire(where.LevelNEQ.Value),
            LevelIn = where.LevelIn?
                .Select(static level => ToWire(level))
                .ToList(),
            LevelNotIn = where.LevelNotIn?
                .Select(static level => ToWire(level))
                .ToList(),
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
            ProjectIdIsNil = where.ProjectIDIsNil == true,
            ProjectIdNotNil = where.ProjectIDNotNil == true,
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

    private static string ToWire(UserStatusValue status)
        => status == UserStatusValue.Activated ? "activated" : "deactivated";

    private static string ToWire(RoleLevelValue level)
        => level == RoleLevelValue.System
            ? RoleManagementService.LevelSystem
            : RoleManagementService.LevelProject;

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
