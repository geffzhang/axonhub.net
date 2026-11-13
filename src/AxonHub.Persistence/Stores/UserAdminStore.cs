using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using static AxonHub.Persistence.ConnectionSql;

namespace AxonHub.Persistence;

/// <summary>Ordering fields exposed by the reference UserOrder type.</summary>
public enum UserSortField
{
    Id,
    CreatedAt,
    UpdatedAt,
}

/// <summary>
/// Port of <c>UserWhereInput</c> (ent.graphql:7506-7672). All scalar field
/// predicates plus the <c>projects</c>, <c>apiKeys</c>, <c>roles</c>,
/// <c>projectUsers</c> and <c>userRoles</c> edge predicates are modeled; the
/// remaining two edge predicates (channelOverrideTemplates, oidcIdentities)
/// are a registered gap (C135). <see cref="HasProjectUsersWithProjectId"/> is
/// the <c>hasProjectUsersWith</c> predicate the User query policy installs.
/// </summary>
public sealed record UserListFilter
{
    public long? Id { get; init; }

    public long? IdNEQ { get; init; }

    public IReadOnlyList<long>? IdIn { get; init; }

    public IReadOnlyList<long>? IdNotIn { get; init; }

    public long? IdGT { get; init; }

    public long? IdGTE { get; init; }

    public long? IdLT { get; init; }

    public long? IdLTE { get; init; }

    public DateTime? CreatedAt { get; init; }

    public DateTime? CreatedAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? CreatedAtIn { get; init; }

    public IReadOnlyList<DateTime>? CreatedAtNotIn { get; init; }

    public DateTime? CreatedAtGT { get; init; }

    public DateTime? CreatedAtGTE { get; init; }

    public DateTime? CreatedAtLT { get; init; }

    public DateTime? CreatedAtLTE { get; init; }

    public DateTime? UpdatedAt { get; init; }

    public DateTime? UpdatedAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? UpdatedAtIn { get; init; }

    public IReadOnlyList<DateTime>? UpdatedAtNotIn { get; init; }

    public DateTime? UpdatedAtGT { get; init; }

    public DateTime? UpdatedAtGTE { get; init; }

    public DateTime? UpdatedAtLT { get; init; }

    public DateTime? UpdatedAtLTE { get; init; }

    public string? Email { get; init; }

    public string? EmailNEQ { get; init; }

    public IReadOnlyList<string>? EmailIn { get; init; }

    public IReadOnlyList<string>? EmailNotIn { get; init; }

    public string? EmailGT { get; init; }

    public string? EmailGTE { get; init; }

    public string? EmailLT { get; init; }

    public string? EmailLTE { get; init; }

    public string? EmailContains { get; init; }

    public string? EmailHasPrefix { get; init; }

    public string? EmailHasSuffix { get; init; }

    public string? EmailEqualFold { get; init; }

    public string? EmailContainsFold { get; init; }

    public string? Status { get; init; }

    public string? StatusNEQ { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    public IReadOnlyList<string>? StatusNotIn { get; init; }

    public string? PreferLanguage { get; init; }

    public string? PreferLanguageNEQ { get; init; }

    public IReadOnlyList<string>? PreferLanguageIn { get; init; }

    public IReadOnlyList<string>? PreferLanguageNotIn { get; init; }

    public string? PreferLanguageGT { get; init; }

    public string? PreferLanguageGTE { get; init; }

    public string? PreferLanguageLT { get; init; }

    public string? PreferLanguageLTE { get; init; }

    public string? PreferLanguageContains { get; init; }

    public string? PreferLanguageHasPrefix { get; init; }

    public string? PreferLanguageHasSuffix { get; init; }

    public string? PreferLanguageEqualFold { get; init; }

    public string? PreferLanguageContainsFold { get; init; }

    public string? FirstName { get; init; }

    public string? FirstNameNEQ { get; init; }

    public IReadOnlyList<string>? FirstNameIn { get; init; }

    public IReadOnlyList<string>? FirstNameNotIn { get; init; }

    public string? FirstNameGT { get; init; }

    public string? FirstNameGTE { get; init; }

    public string? FirstNameLT { get; init; }

    public string? FirstNameLTE { get; init; }

    public string? FirstNameContains { get; init; }

    public string? FirstNameHasPrefix { get; init; }

    public string? FirstNameHasSuffix { get; init; }

    public string? FirstNameEqualFold { get; init; }

    public string? FirstNameContainsFold { get; init; }

    public string? LastName { get; init; }

    public string? LastNameNEQ { get; init; }

    public IReadOnlyList<string>? LastNameIn { get; init; }

    public IReadOnlyList<string>? LastNameNotIn { get; init; }

    public string? LastNameGT { get; init; }

    public string? LastNameGTE { get; init; }

    public string? LastNameLT { get; init; }

    public string? LastNameLTE { get; init; }

    public string? LastNameContains { get; init; }

    public string? LastNameHasPrefix { get; init; }

    public string? LastNameHasSuffix { get; init; }

    public string? LastNameEqualFold { get; init; }

    public string? LastNameContainsFold { get; init; }

    public string? Avatar { get; init; }

    public string? AvatarNEQ { get; init; }

    public IReadOnlyList<string>? AvatarIn { get; init; }

    public IReadOnlyList<string>? AvatarNotIn { get; init; }

    public string? AvatarGT { get; init; }

    public string? AvatarGTE { get; init; }

    public string? AvatarLT { get; init; }

    public string? AvatarLTE { get; init; }

    public string? AvatarContains { get; init; }

    public string? AvatarHasPrefix { get; init; }

    public string? AvatarHasSuffix { get; init; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool AvatarIsNil { get; init; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool AvatarNotNil { get; init; }

    public string? AvatarEqualFold { get; init; }

    public string? AvatarContainsFold { get; init; }

    public bool? IsOwner { get; init; }

    public bool? IsOwnerNEQ { get; init; }

    /// <summary>
    /// Ent's <c>hasProjects</c>: true → the user has a <c>user_projects</c> row,
    /// false → NOT EXISTS. Generated sqlgraph does not re-apply the Project
    /// soft-delete predicate.
    /// </summary>
    public bool? HasProjects { get; init; }

    public IReadOnlyList<ProjectListFilter>? HasProjectsWith { get; init; }

    /// <summary>
    /// Ent's <c>hasAPIKeys</c>: true → the user has an <c>api_keys</c> row,
    /// false → NOT EXISTS. Generated sqlgraph does not re-apply the APIKey
    /// soft-delete predicate.
    /// </summary>
    public bool? HasAPIKeys { get; init; }

    public IReadOnlyList<ApiKeyListFilter>? HasAPIKeysWith { get; init; }

    /// <summary>
    /// Ent's <c>hasProjectUsers</c>: true → the user has a <c>user_projects</c>
    /// row, false → NOT EXISTS.
    /// </summary>
    public bool? HasProjectUsers { get; init; }

    public IReadOnlyList<UserProjectListFilter>? HasProjectUsersWith { get; init; }

    /// <summary>
    /// Ent's <c>hasRoles</c>: true → the user has a matching <c>user_roles</c>
    /// link, false → NOT EXISTS. Generated sqlgraph does not re-apply the Role
    /// soft-delete predicate.
    /// </summary>
    public bool? HasRoles { get; init; }

    public IReadOnlyList<RoleListFilter>? HasRolesWith { get; init; }

    /// <summary>
    /// Ent's <c>hasUserRoles</c>: true → the user has a <c>user_roles</c> row,
    /// false → NOT EXISTS.
    /// </summary>
    public bool? HasUserRoles { get; init; }

    public IReadOnlyList<UserRoleListFilter>? HasUserRolesWith { get; init; }

    /// <summary>
    /// Ent's <c>hasChannelOverrideTemplates</c>: true → the user has a
    /// <c>channel_override_templates</c> row, false → NOT EXISTS. Generated
    /// sqlgraph does not re-apply the template soft-delete predicate.
    /// </summary>
    public bool? HasChannelOverrideTemplates { get; init; }

    public IReadOnlyList<ChannelOverrideTemplateListFilter>? HasChannelOverrideTemplatesWith
    {
        get;
        init;
    }

    /// <summary>
    /// Ent's <c>hasOidcIdentities</c>: true → the user has an
    /// <c>oidc_identities</c> row, false → NOT EXISTS. Generated sqlgraph does
    /// not re-apply the identity soft-delete predicate.
    /// </summary>
    public bool? HasOidcIdentities { get; init; }

    public IReadOnlyList<OidcIdentityListFilter>? HasOidcIdentitiesWith { get; init; }

    /// <summary>
    /// Ent's <c>WhereHasProjectUsersWith(project_id = ?)</c> predicate the
    /// User query policy installs for project owners and project members
    /// (internal/scopes/rule_user_project_scope.go:91,124).
    /// </summary>
    public long? HasProjectUsersWithProjectId { get; init; }

    public UserListFilter? Not { get; init; }

    public IReadOnlyList<UserListFilter>? And { get; init; }

    public IReadOnlyList<UserListFilter>? Or { get; init; }
}

/// <summary>Entgql pagination request for users.</summary>
public sealed record UserListQuery
{
    public UserListFilter? Where { get; init; }

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public UserSortField SortField { get; init; } = UserSortField.Id;

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }
}

/// <summary>Port of the field and edge mutations of ent.UserUpdateOne.</summary>
public sealed record UserUpdate
{
    public required long Id { get; init; }

    public string? Email { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public string? PreferLanguage { get; init; }

    public string? Avatar { get; init; }

    public bool ClearAvatar { get; init; }

    public bool? IsOwner { get; init; }

    /// <summary>Already hashed by the service, exactly like UserService.UpdateUser.</summary>
    public string? Password { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }

    public IReadOnlyList<string>? AppendScopes { get; init; }

    public bool ClearScopes { get; init; }

    public IReadOnlyList<long>? AddRoleIds { get; init; }

    public IReadOnlyList<long>? RemoveRoleIds { get; init; }

    public bool ClearRoles { get; init; }
}

/// <summary>
/// A user loaded with the edges the permission validator reads: the reference
/// middleware loads the current user via UserService.GetUserByID
/// (WithRoles/WithProjectUsers) and CanEditUserPermissions loads its target
/// with the same edges (C161).
/// </summary>
public sealed record PermissionUser(
    UserRecord User,
    IReadOnlyList<RoleRecord> Roles,
    IReadOnlyList<UserProjectRecord> ProjectUsers);

/// <summary>
/// The predicates the UserProject query policy installs: at most one of
/// <see cref="ProjectId"/> (<c>project_id = ctx</c>) and <see cref="UserId"/>
/// (<c>user_id = me</c>), or neither when the query is unfiltered.
/// (internal/scopes/rule_user_project_scope.go, rule_user_owned.go).
/// </summary>
public sealed record UserProjectAccessFilter
{
    public long? ProjectId { get; init; }

    public long? UserId { get; init; }
}

/// <summary>
/// Management persistence seam for users and their relation tables. Kept apart
/// from <see cref="IUserStore"/> so the authentication slice's fake stays small.
/// </summary>
public interface IUserAdminStore
{
    Task<long> CreateAsync(
        UserRecord user,
        IReadOnlyList<long>? roleIds,
        CancellationToken cancellationToken = default);

    Task<UserRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Port of ent <c>User.Query().WithRoles().WithProjectUsers().Only()</c>.</summary>
    Task<PermissionUser?> GetPermissionUserAsync(
        long id,
        CancellationToken cancellationToken = default);

    Task<ListPage<UserRecord>> ListAsync(
        UserListQuery query,
        CancellationToken cancellationToken = default);

    Task<UserRecord?> UpdateAsync(
        UserUpdate update,
        CancellationToken cancellationToken = default);

    Task<UserRecord?> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes relations, archives personal keys and soft-deletes the user.</summary>
    Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of UserService.AddUserToProject's store steps (user.go:420-452):
    /// creates the user_project row in a transaction and, when roleIds is
    /// non-empty, adds the user_roles links. Step failures carry the reference's
    /// per-step wording ("failed to add user to project: %w", "failed to get
    /// user: %w", "failed to add roles to user: %w") as
    /// <see cref="StoreFailureException"/>; the service forwards them unchanged.
    /// </summary>
    /// <param name="beforeCreate">
    /// Evaluates the UserProject mutation policy at <c>mut.Save</c>
    /// (user.go:434). It runs inside the transaction before the insert, so a
    /// policy deny wins over a unique-index violation exactly as it does in the
    /// reference.
    /// </param>
    /// <param name="beforeUserRead">
    /// Evaluates the User query policy at <c>client.User.Get</c> (user.go:443).
    /// </param>
    /// <param name="beforeUserRoleUpdate">
    /// Evaluates the User mutation policy at
    /// <c>user.Update().AddRoleIDs(...).Exec</c> (user.go:447).
    /// </param>
    Task<UserProjectRecord> AddProjectUserAsync(
        long userId,
        long projectId,
        bool? isOwner,
        IReadOnlyList<string>? scopes,
        IReadOnlyList<long> roleIds,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? beforeCreate = null,
        Func<CancellationToken, Task>? beforeUserRead = null,
        Func<CancellationToken, Task>? beforeUserRoleUpdate = null);

    /// <summary>
    /// Port of UserService.RemoveUserFromProject's store steps (user.go:465-494):
    /// deletes the user_project rows; when none matched, returns without role
    /// cleanup. Otherwise removes the user_roles links of every live project
    /// role of the project the user still has.
    /// </summary>
    /// <param name="mutationProjectScope">
    /// The <c>WhereP(project_id = ctx)</c> predicate the UserProject mutation
    /// policy installs; a mismatch deletes nothing.
    /// </param>
    /// <param name="queryRoleScope">
    /// Evaluates the Role query policy at the reference's role lookup
    /// (user.go:480-485); the returned project predicate narrows the query.
    /// </param>
    /// <param name="beforeUserRoleRemoval">
    /// Evaluates the User mutation policy before
    /// <c>User.UpdateOneID(...).RemoveRoleIDs(...)</c> (user.go:489-492).
    /// </param>
    Task RemoveProjectUserAsync(
        long userId,
        long projectId,
        CancellationToken cancellationToken = default,
        long? mutationProjectScope = null,
        Func<CancellationToken, Task<long?>>? queryRoleScope = null,
        Func<CancellationToken, Task>? beforeUserRoleRemoval = null);

    /// <summary>
    /// Port of UserService.UpdateProjectUser's store steps (user.go:525-573):
    /// updates the user_project row (missing relationship raises the reference
    /// wording "failed to find user project relationship: %w"), then applies the
    /// role adds before the removals on the user.
    /// </summary>
    /// <param name="queryScope">
    /// The predicate the UserProject query policy installs on the relationship
    /// lookup (user.go:540-547).
    /// </param>
    /// <param name="beforeUpdate">
    /// Evaluates the UserProject mutation policy and returns the
    /// <c>WhereP(project_id = ctx)</c> predicate it installed on
    /// <c>mut.Save</c> (user.go:557); null means none.
    /// </param>
    /// <param name="beforeUserRead">Evaluates the User query policy.</param>
    /// <param name="beforeUserRoleUpdate">Evaluates the User mutation policy.</param>
    Task<UserProjectRecord> UpdateProjectUserAsync(
        long userId,
        long projectId,
        bool? isOwner,
        IReadOnlyList<string>? scopes,
        IReadOnlyList<long> addRoleIds,
        IReadOnlyList<long> removeRoleIds,
        CancellationToken cancellationToken = default,
        UserProjectAccessFilter? queryScope = null,
        Func<CancellationToken, Task<long?>>? beforeUpdate = null,
        Func<CancellationToken, Task>? beforeUserRead = null,
        Func<CancellationToken, Task>? beforeUserRoleUpdate = null);

    /// <summary>Port of <c>Project.QueryProjectUsers().All()</c> (no ordering).</summary>
    Task<IReadOnlyList<UserProjectRecord>> ListProjectUsersAsync(
        long projectId,
        CancellationToken cancellationToken = default,
        UserProjectAccessFilter? queryScope = null);

    /// <summary>Port of <c>User.QueryProjectUsers().All()</c> (no ordering).</summary>
    Task<IReadOnlyList<UserProjectRecord>> ListProjectUsersForUserAsync(
        long userId,
        CancellationToken cancellationToken = default,
        UserProjectAccessFilter? queryScope = null);

    /// <summary>
    /// Port of the entgql Node lookup for UserProject
    /// (<c>client.UserProject.Get(ctx, id)</c>): the UserProject query policy
    /// installs <c>WhereProjectID(ctx)</c> or <c>WhereUserID(ctx)</c>; a row the
    /// policy filters out is ent NotFound.
    /// </summary>
    Task<UserProjectRecord?> GetProjectUserByIdAsync(
        long id,
        CancellationToken cancellationToken = default,
        UserProjectAccessFilter? queryScope = null);
}

/// <summary>
/// Store for the reference <c>users</c>, <c>user_roles</c>, <c>user_projects</c>
/// and personal <c>api_keys</c> rows touched by UserService.
/// </summary>
public sealed class UserAdminStore(AxonHubDbContext db) : IUserAdminStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "email", "status",
        "prefer_language", "password", "first_name", "last_name", "avatar",
        "is_owner", "scopes"
        """;

    private const string UserProjectColumns = """
        "id", "created_at", "updated_at", "is_owner", "scopes", "user_id",
        "project_id"
        """;

    public async Task<long> CreateAsync(
        UserRecord user,
        IReadOnlyList<long>? roleIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;
        if (user.CreatedAt == default)
        {
            user.CreatedAt = now;
        }

        if (user.UpdatedAt == default)
        {
            user.UpdatedAt = now;
        }

        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (roleIds is not null && roleIds.Count > 0)
        {
            foreach (var roleId in roleIds.Distinct())
            {
                db.UserRoles.Add(new UserRoleRecord
                {
                    CreatedAt = now,
                    UpdatedAt = now,
                    UserId = user.Id,
                    RoleId = roleId,
                });
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return user.Id;
    }

    public async Task<UserRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"users\" WHERE \"id\" = @Id AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<UserRecord>(
            new CommandDefinition(
                sql,
                new { Id = id },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<PermissionUser?> GetPermissionUserAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var user = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        // Ent's WithRoles/WithProjectUsers edge loaders query the join tables
        // for live rows only (SoftDeleteMixin on the edges).
        var roleIds = await db.UserRoles
            .Where(row => row.UserId == id)
            .Select(row => row.RoleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var roles = roleIds.Count == 0
            ? []
            : await db.Roles
                .Where(row => row.DeletedAt == 0 && roleIds.Contains(row.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

        var projectUsers = await db.UserProjects
            .Where(row => row.UserId == id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PermissionUser(user, roles, projectUsers);
    }

    public async Task<ListPage<UserRecord>> ListAsync(
        UserListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var args = new SqlArguments();
        var where = BuildFilter(query.Where, "u", args);

        var conditions = new StringBuilder(" WHERE u.\"deleted_at\" = 0");
        if (where is not null)
        {
            conditions.Append(" AND ").Append(where);
        }

        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;

        var countSql = "SELECT COUNT(*) FROM \"users\" u" + conditions + ";";

        var nodesSql = new StringBuilder("SELECT ");
        nodesSql.Append(string.Join(", ", Columns
            .Split(',')
            .Select(static column => "u." + column.Trim())));
        nodesSql.Append(" FROM \"users\" u").Append(conditions);

        var sortColumn = query.SortField switch
        {
            UserSortField.CreatedAt => "created_at",
            UserSortField.UpdatedAt => "updated_at",
            _ => null,
        };
        ConnectionSql.AppendCursor(
            nodesSql,
            "u",
            "id",
            sortColumn,
            query.After,
            query.Before,
            direction,
            args);

        nodesSql.Append(" ORDER BY ")
            .Append(ConnectionSql.OrderBy("u", "id", sortColumn, direction));

        var limit = query.First is not null
            ? query.First + 1
            : query.Last is not null ? query.Last + 1 : (int?)null;
        if (limit is not null)
        {
            nodesSql.Append(" LIMIT ").Append(limit.Value);
        }

        nodesSql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);

        var totalCount = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                countSql,
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        var nodes = (await connection.QueryAsync<UserRecord>(
            new CommandDefinition(
                nodesSql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

        return BuildPage(nodes, query, totalCount);
    }

    public async Task<UserRecord?> UpdateAsync(
        UserUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var user = await db.Users.SingleOrDefaultAsync(
            row => row.Id == update.Id && row.DeletedAt == 0,
            cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        if (update.Email is not null)
        {
            user.Email = update.Email;
        }

        if (update.FirstName is not null)
        {
            user.FirstName = update.FirstName;
        }

        if (update.LastName is not null)
        {
            user.LastName = update.LastName;
        }

        if (update.PreferLanguage is not null)
        {
            user.PreferLanguage = update.PreferLanguage;
        }

        if (update.ClearAvatar)
        {
            user.Avatar = null;
        }
        else if (update.Avatar is not null)
        {
            user.Avatar = update.Avatar;
        }

        if (update.IsOwner is not null)
        {
            user.IsOwner = update.IsOwner.Value;
        }

        if (update.Password is not null)
        {
            user.Password = update.Password;
        }

        // Ent applies SetScopes, then AppendScopes, then ClearScopes.
        if (update.Scopes is not null)
        {
            user.Scopes = JsonSerializer.Serialize(update.Scopes);
        }

        if (update.AppendScopes is not null)
        {
            var scopes = ParseScopes(user.Scopes);
            scopes.AddRange(update.AppendScopes);
            user.Scopes = JsonSerializer.Serialize(scopes);
        }

        if (update.ClearScopes)
        {
            user.Scopes = null;
        }

        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Ent applies AddRoles, then RemoveRoles, then ClearRoles.
        if (update.AddRoleIds is not null && update.AddRoleIds.Count > 0)
        {
            var existing = await db.UserRoles
                .Where(row => row.UserId == update.Id)
                .Select(row => row.RoleId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var now = DateTime.UtcNow;
            foreach (var roleId in update.AddRoleIds.Distinct().Except(existing))
            {
                db.UserRoles.Add(new UserRoleRecord
                {
                    CreatedAt = now,
                    UpdatedAt = now,
                    UserId = update.Id,
                    RoleId = roleId,
                });
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (update.RemoveRoleIds is not null && update.RemoveRoleIds.Count > 0)
        {
            await db.UserRoles
                .Where(row => row.UserId == update.Id && update.RemoveRoleIds.Contains(row.RoleId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (update.ClearRoles)
        {
            await db.UserRoles
                .Where(row => row.UserId == update.Id)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        // Read inside the transaction: an Npgsql transaction cannot be used once
        // it has been committed.
        var result = await GetByIdAsync(update.Id, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task<UserRecord?> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var user = await db.Users.SingleOrDefaultAsync(
            row => row.Id == id && row.DeletedAt == 0,
            cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        var now = DateTime.UtcNow;
        user.Status = status;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (status == "deactivated")
        {
            // Reference UpdateUserStatus wraps this failure itself; the service
            // must not add its "failed to update user status: " prefix.
            try
            {
                await UpdatePersonalApiKeyStatusAsync(
                    id,
                    ["enabled"],
                    "disabled",
                    now,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StoreFailureException(
                    "failed to disable user personal API keys: " + ex.Message,
                    ex);
            }
        }

        var result = await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var user = await db.Users.SingleOrDefaultAsync(
            row => row.Id == id && row.DeletedAt == 0,
            cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return false;
        }

        await db.UserProjects
            .Where(row => row.UserId == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        await db.UserRoles
            .Where(row => row.UserId == id)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);

        var now = DateTime.UtcNow;
        try
        {
            await UpdatePersonalApiKeyStatusAsync(
                id,
                ["enabled", "disabled"],
                "archived",
                now,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(
                "failed to archive user personal API keys: " + ex.Message,
                ex);
        }

        user.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        user.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<UserProjectRecord> AddProjectUserAsync(
        long userId,
        long projectId,
        bool? isOwner,
        IReadOnlyList<string>? scopes,
        IReadOnlyList<long> roleIds,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? beforeCreate = null,
        Func<CancellationToken, Task>? beforeUserRead = null,
        Func<CancellationToken, Task>? beforeUserRoleUpdate = null)
    {
        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // UserProject mutation policy at mut.Save (user.go:434). It runs
        // before the insert so a policy deny precedes the unique-index
        // violation, matching the reference's in-transaction order.
        if (beforeCreate is not null)
        {
            try
            {
                await beforeCreate(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StoreFailureException(
                    "failed to add user to project: " + ex.Message,
                    ex);
            }
        }

        var now = DateTime.UtcNow;
        var row = new UserProjectRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            UserId = userId,
            ProjectId = projectId,
            // Ent's create default for is_owner is false (user.go:426-428).
            IsOwner = isOwner ?? false,
            Scopes = scopes is null ? null : JsonSerializer.Serialize(scopes),
        };

        db.UserProjects.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            throw new StoreFailureException(
                "failed to add user to project: " + ex.GetBaseException().Message,
                ex);
        }

        if (roleIds.Count > 0)
        {
            // User query policy at client.User.Get (user.go:443).
            if (beforeUserRead is not null)
            {
                try
                {
                    await beforeUserRead(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new StoreFailureException(
                        "failed to get user: " + ex.Message,
                        ex);
                }
            }

            var user = await db.Users
                .SingleOrDefaultAsync(
                    row => row.Id == userId && row.DeletedAt == 0,
                    cancellationToken)
                .ConfigureAwait(false);
            if (user is null)
            {
                throw new StoreFailureException(
                    "failed to get user: ent: user not found",
                    new InvalidOperationException("ent: user not found"));
            }

            // User mutation policy at user.Update().AddRoleIDs (user.go:447).
            if (beforeUserRoleUpdate is not null)
            {
                try
                {
                    await beforeUserRoleUpdate(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new StoreFailureException(
                        "failed to add roles to user: " + ex.Message,
                        ex);
                }
            }

            await AddUserRolesAsync(userId, roleIds, now, cancellationToken)
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return row;
    }

    public async Task RemoveProjectUserAsync(
        long userId,
        long projectId,
        CancellationToken cancellationToken = default,
        long? mutationProjectScope = null,
        Func<CancellationToken, Task<long?>>? queryRoleScope = null,
        Func<CancellationToken, Task>? beforeUserRoleRemoval = null)
    {
        int deleted;
        try
        {
            var query = db.UserProjects
                .Where(row => row.UserId == userId && row.ProjectId == projectId);
            if (mutationProjectScope is { } scope)
            {
                // UserProject mutation policy: WhereP(project_id = ctx).
                query = query.Where(row => row.ProjectId == scope);
            }

            deleted = await query
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new StoreFailureException(
                "failed to remove user from project: " + ex.GetBaseException().Message,
                ex);
        }

        if (deleted == 0)
        {
            // The reference returns success without role cleanup or cache
            // invalidation when no relationship matched (user.go:476-478).
            return;
        }

        // Reference (user.go:480-482): Role.Query().
        //   Where(ProjectIDEQ(projectID), HasUsersWith(IDEQ(userID))).IDs()
        // — the roles of this project the removed user still has. Links of
        // system roles (NULL project_id) never match ProjectIDEQ. The Role
        // query policy runs first and may install WhereP(project_id = ctx).
        long? roleScope = null;
        if (queryRoleScope is not null)
        {
            try
            {
                roleScope = await queryRoleScope(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StoreFailureException(
                    "failed to query user project roles: " + ex.Message,
                    ex);
            }
        }

        if (roleScope is not null && roleScope.Value != projectId)
        {
            // WhereProjectID(ctx) filters every row of this project out.
            return;
        }

        List<long> roleIds;
        try
        {
            roleIds = await db.UserRoles
                .Join(
                    db.Roles,
                    userRole => userRole.RoleId,
                    role => role.Id,
                    (userRole, role) => new { userRole.Id, userRole.UserId, role.ProjectId, role.DeletedAt })
                .Where(joined => joined.UserId == userId
                    && joined.DeletedAt == 0
                    && joined.ProjectId == projectId)
                .Select(joined => joined.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new StoreFailureException(
                "failed to query user project roles: " + ex.GetBaseException().Message,
                ex);
        }

        if (roleIds.Count > 0)
        {
            // User mutation policy on User.UpdateOneID(...).RemoveRoleIDs(...).
            if (beforeUserRoleRemoval is not null)
            {
                try
                {
                    await beforeUserRoleRemoval(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new StoreFailureException(
                        "failed to remove user project roles: " + ex.Message,
                        ex);
                }
            }

            try
            {
                await db.UserRoles
                    .Where(row => roleIds.Contains(row.Id))
                    .ExecuteDeleteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new StoreFailureException(
                    "failed to remove user project roles: " + ex.GetBaseException().Message,
                    ex);
            }
        }
    }

    public async Task<UserProjectRecord> UpdateProjectUserAsync(
        long userId,
        long projectId,
        bool? isOwner,
        IReadOnlyList<string>? scopes,
        IReadOnlyList<long> addRoleIds,
        IReadOnlyList<long> removeRoleIds,
        CancellationToken cancellationToken = default,
        UserProjectAccessFilter? queryScope = null,
        Func<CancellationToken, Task<long?>>? beforeUpdate = null,
        Func<CancellationToken, Task>? beforeUserRead = null,
        Func<CancellationToken, Task>? beforeUserRoleUpdate = null)
    {
        // UserProject query policy: WhereUserID(me) / WhereProjectID(ctx).
        var lookup = db.UserProjects
            .Where(row => row.UserId == userId && row.ProjectId == projectId);
        if (queryScope?.ProjectId is { } scopeProject)
        {
            lookup = lookup.Where(row => row.ProjectId == scopeProject);
        }

        if (queryScope?.UserId is { } scopeUser)
        {
            lookup = lookup.Where(row => row.UserId == scopeUser);
        }

        var row = await lookup
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            throw new StoreFailureException(
                "failed to find user project relationship: ent: user_project not found",
                new InvalidOperationException("ent: user_project not found"));
        }

        // UserProject mutation policy, evaluated at mut.Save (user.go:557).
        long? mutationScope = null;
        if (beforeUpdate is not null)
        {
            try
            {
                mutationScope = await beforeUpdate(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StoreFailureException(
                    "failed to update project user: " + ex.Message,
                    ex);
            }
        }

        if (mutationScope is { } scope && row.ProjectId != scope)
        {
            // WhereP(project_id = ctx) matched no row.
            throw new StoreFailureException(
                "failed to update project user: ent: user_project not found",
                new InvalidOperationException("ent: user_project not found"));
        }

        if (isOwner is not null)
        {
            row.IsOwner = isOwner.Value;
        }

        if (scopes is not null)
        {
            row.Scopes = JsonSerializer.Serialize(scopes);
        }

        row.UpdatedAt = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            throw new StoreFailureException(
                "failed to update project user: " + ex.GetBaseException().Message,
                ex);
        }

        // User query policy on client.User.Get (user.go:556).
        if (beforeUserRead is not null)
        {
            try
            {
                await beforeUserRead(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StoreFailureException(
                    "failed to get user: " + ex.Message,
                    ex);
            }
        }

        var user = await db.Users
            .SingleOrDefaultAsync(
                row => row.Id == userId && row.DeletedAt == 0,
                cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            throw new StoreFailureException(
                "failed to get user: ent: user not found",
                new InvalidOperationException("ent: user not found"));
        }

        // User mutation policy on user.Update() (user.go:562-572); the
        // reference always runs Exec, even with both lists empty.
        if (beforeUserRoleUpdate is not null)
        {
            try
            {
                await beforeUserRoleUpdate(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StoreFailureException(
                    "failed to update user roles: " + ex.Message,
                    ex);
            }
        }

        await UpdateUserRolesAsync(
                userId,
                addRoleIds,
                removeRoleIds,
                cancellationToken)
            .ConfigureAwait(false);

        return row;
    }

    public async Task<UserProjectRecord?> GetProjectUserByIdAsync(
        long id,
        CancellationToken cancellationToken = default,
        UserProjectAccessFilter? queryScope = null)
    {
        var sql = "SELECT " + UserProjectColumns
            + " FROM \"user_projects\" WHERE \"id\" = @Id";
        var parameters = new DynamicParameters();
        parameters.Add("Id", id);

        // UserProject query policy: WhereProjectID(ctx) / WhereUserID(me).
        if (queryScope?.ProjectId is { } scopeProject)
        {
            sql += " AND \"project_id\" = @ScopeProjectId";
            parameters.Add("ScopeProjectId", scopeProject);
        }

        if (queryScope?.UserId is { } scopeUser)
        {
            sql += " AND \"user_id\" = @ScopeUserId";
            parameters.Add("ScopeUserId", scopeUser);
        }

        sql += ";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<UserProjectRecord>(
            new CommandDefinition(
                sql,
                parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UserProjectRecord>> ListProjectUsersAsync(
        long projectId,
        CancellationToken cancellationToken = default,
        UserProjectAccessFilter? queryScope = null)
    {
        var sql = "SELECT " + UserProjectColumns
            + " FROM \"user_projects\" WHERE \"project_id\" = @ProjectId";
        var parameters = new DynamicParameters();
        parameters.Add("ProjectId", projectId);

        // UserProject query policy: WhereProjectID(ctx) / WhereUserID(me).
        if (queryScope?.ProjectId is { } scopeProject)
        {
            sql += " AND \"project_id\" = @ScopeProjectId";
            parameters.Add("ScopeProjectId", scopeProject);
        }

        if (queryScope?.UserId is { } scopeUser)
        {
            sql += " AND \"user_id\" = @ScopeUserId";
            parameters.Add("ScopeUserId", scopeUser);
        }

        sql += ";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<UserProjectRecord>(
            new CommandDefinition(
                sql,
                parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    public async Task<IReadOnlyList<UserProjectRecord>> ListProjectUsersForUserAsync(
        long userId,
        CancellationToken cancellationToken = default,
        UserProjectAccessFilter? queryScope = null)
    {
        var sql = "SELECT " + UserProjectColumns
            + " FROM \"user_projects\" WHERE \"user_id\" = @UserId";
        var parameters = new DynamicParameters();
        parameters.Add("UserId", userId);

        // UserProject query policy: WhereProjectID(ctx) / WhereUserID(me).
        if (queryScope?.ProjectId is { } scopeProject)
        {
            sql += " AND \"project_id\" = @ScopeProjectId";
            parameters.Add("ScopeProjectId", scopeProject);
        }

        if (queryScope?.UserId is { } scopeUser)
        {
            sql += " AND \"user_id\" = @ScopeUserId";
            parameters.Add("ScopeUserId", scopeUser);
        }

        sql += ";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<UserProjectRecord>(
            new CommandDefinition(
                sql,
                parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.AsList();
    }

    /// <summary>Port of <c>user.Update().AddRoleIDs(...)</c> for a live user.</summary>
    private async Task AddUserRolesAsync(
        long userId,
        IReadOnlyList<long> roleIds,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var existing = await db.UserRoles
            .Where(row => row.UserId == userId)
            .Select(row => row.RoleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var roleId in roleIds.Distinct().Except(existing))
        {
            db.UserRoles.Add(new UserRoleRecord
            {
                CreatedAt = now,
                UpdatedAt = now,
                UserId = userId,
                RoleId = roleId,
            });
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            throw new StoreFailureException(
                "failed to add roles to user: " + ex.GetBaseException().Message,
                ex);
        }
    }

    /// <summary>
    /// Port of <c>user.Update().AddRoleIDs(...).RemoveRoleIDs(...).Exec()</c>:
    /// ent applies the additions before the removals, so a role in both lists
    /// ends up removed.
    /// </summary>
    private async Task UpdateUserRolesAsync(
        long userId,
        IReadOnlyList<long> addRoleIds,
        IReadOnlyList<long> removeRoleIds,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var pending = false;

        if (addRoleIds.Count > 0)
        {
            var existing = await db.UserRoles
                .Where(row => row.UserId == userId)
                .Select(row => row.RoleId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var roleId in addRoleIds.Distinct().Except(existing))
            {
                db.UserRoles.Add(new UserRoleRecord
                {
                    CreatedAt = now,
                    UpdatedAt = now,
                    UserId = userId,
                    RoleId = roleId,
                });
                pending = true;
            }
        }

        if (removeRoleIds.Count > 0)
        {
            pending |= await db.UserRoles
                .Where(row => row.UserId == userId && removeRoleIds.Contains(row.RoleId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false) > 0;
        }

        if (!pending)
        {
            return;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            throw new StoreFailureException(
                "failed to update user roles: " + ex.GetBaseException().Message,
                ex);
        }
    }

    private async Task UpdatePersonalApiKeyStatusAsync(
        long userId,
        IReadOnlyList<string> fromStatuses,
        string toStatus,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var statuses = fromStatuses.ToArray();
        await db.ApiKeys
            .Where(row => row.UserId == userId
                && row.Type == "personal"
                && row.DeletedAt == 0
                && statuses.Contains(row.Status))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, toStatus)
                    .SetProperty(row => row.UpdatedAt, now),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static List<string> ParseScopes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ListPage<UserRecord> BuildPage(
        List<UserRecord> nodes,
        UserListQuery query,
        int totalCount)
    {
        var hasNextPage = query.Before is not null;
        var hasPreviousPage = query.After is not null;

        if (query.First is not null && query.First + 1 == nodes.Count)
        {
            hasNextPage = true;
            nodes.RemoveAt(nodes.Count - 1);
        }
        else if (query.Last is not null && query.Last + 1 == nodes.Count)
        {
            hasPreviousPage = true;
            nodes.RemoveAt(nodes.Count - 1);
        }

        if (query.Last is not null)
        {
            nodes.Reverse();
        }

        return new ListPage<UserRecord>(nodes, hasNextPage, hasPreviousPage, totalCount);
    }

    internal static string? BuildFilter(
        UserListFilter? filter,
        string alias,
        SqlArguments args)
    {
        if (filter is null)
        {
            return null;
        }

        var predicates = new List<string?>();

        AppendComparison(predicates, alias, "id", filter.Id, "=", args);
        AppendComparison(predicates, alias, "id", filter.IdNEQ, "<>", args);
        if (filter.IdIn is not null)
        {
            AppendIdIn(predicates, alias, "id", filter.IdIn, args);
        }

        if (filter.IdNotIn is not null)
        {
            AppendIdIn(predicates, alias, "id", filter.IdNotIn, args, negate: true);
        }

        AppendComparison(predicates, alias, "id", filter.IdGT, ">", args);
        AppendComparison(predicates, alias, "id", filter.IdGTE, ">=", args);
        AppendComparison(predicates, alias, "id", filter.IdLT, "<", args);
        AppendComparison(predicates, alias, "id", filter.IdLTE, "<=", args);

        AppendComparison(predicates, alias, "created_at", filter.CreatedAt, "=", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtNEQ, "<>", args);
        AppendTimeIn(predicates, alias, "created_at", filter.CreatedAtIn, args);
        AppendTimeIn(predicates, alias, "created_at", filter.CreatedAtNotIn, args, negate: true);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtGT, ">", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtGTE, ">=", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtLT, "<", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtLTE, "<=", args);

        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAt, "=", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtNEQ, "<>", args);
        AppendTimeIn(predicates, alias, "updated_at", filter.UpdatedAtIn, args);
        AppendTimeIn(predicates, alias, "updated_at", filter.UpdatedAtNotIn, args, negate: true);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtGT, ">", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtGTE, ">=", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtLT, "<", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtLTE, "<=", args);

        AppendStringPredicates(
            predicates,
            alias,
            "email",
            filter.Email,
            filter.EmailNEQ,
            filter.EmailIn,
            filter.EmailNotIn,
            filter.EmailGT,
            filter.EmailGTE,
            filter.EmailLT,
            filter.EmailLTE,
            filter.EmailContains,
            filter.EmailHasPrefix,
            filter.EmailHasSuffix,
            filter.EmailEqualFold,
            filter.EmailContainsFold,
            args);

        AppendComparison(predicates, alias, "status", filter.Status, "=", args);
        AppendComparison(predicates, alias, "status", filter.StatusNEQ, "<>", args);
        if (filter.StatusIn is not null)
        {
            AppendStringIn(predicates, alias, "status", filter.StatusIn, args);
        }

        if (filter.StatusNotIn is not null)
        {
            AppendStringIn(predicates, alias, "status", filter.StatusNotIn, args, negate: true);
        }

        AppendStringPredicates(
            predicates,
            alias,
            "prefer_language",
            filter.PreferLanguage,
            filter.PreferLanguageNEQ,
            filter.PreferLanguageIn,
            filter.PreferLanguageNotIn,
            filter.PreferLanguageGT,
            filter.PreferLanguageGTE,
            filter.PreferLanguageLT,
            filter.PreferLanguageLTE,
            filter.PreferLanguageContains,
            filter.PreferLanguageHasPrefix,
            filter.PreferLanguageHasSuffix,
            filter.PreferLanguageEqualFold,
            filter.PreferLanguageContainsFold,
            args);

        AppendStringPredicates(
            predicates,
            alias,
            "first_name",
            filter.FirstName,
            filter.FirstNameNEQ,
            filter.FirstNameIn,
            filter.FirstNameNotIn,
            filter.FirstNameGT,
            filter.FirstNameGTE,
            filter.FirstNameLT,
            filter.FirstNameLTE,
            filter.FirstNameContains,
            filter.FirstNameHasPrefix,
            filter.FirstNameHasSuffix,
            filter.FirstNameEqualFold,
            filter.FirstNameContainsFold,
            args);

        AppendStringPredicates(
            predicates,
            alias,
            "last_name",
            filter.LastName,
            filter.LastNameNEQ,
            filter.LastNameIn,
            filter.LastNameNotIn,
            filter.LastNameGT,
            filter.LastNameGTE,
            filter.LastNameLT,
            filter.LastNameLTE,
            filter.LastNameContains,
            filter.LastNameHasPrefix,
            filter.LastNameHasSuffix,
            filter.LastNameEqualFold,
            filter.LastNameContainsFold,
            args);

        AppendStringPredicates(
            predicates,
            alias,
            "avatar",
            filter.Avatar,
            filter.AvatarNEQ,
            filter.AvatarIn,
            filter.AvatarNotIn,
            filter.AvatarGT,
            filter.AvatarGTE,
            filter.AvatarLT,
            filter.AvatarLTE,
            filter.AvatarContains,
            filter.AvatarHasPrefix,
            filter.AvatarHasSuffix,
            filter.AvatarEqualFold,
            filter.AvatarContainsFold,
            args);
        if (filter.AvatarIsNil)
        {
            predicates.Add(alias + ".\"avatar\" IS NULL");
        }

        if (filter.AvatarNotNil)
        {
            predicates.Add(alias + ".\"avatar\" IS NOT NULL");
        }

        AppendComparison(predicates, alias, "is_owner", filter.IsOwner, "=", args);
        AppendComparison(predicates, alias, "is_owner", filter.IsOwnerNEQ, "<>", args);

        if (filter.HasProjects is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"user_projects\" up WHERE up.\"user_id\" = "
                + alias + ".\"id\")";
            predicates.Add(filter.HasProjects.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasProjectsWith is { Count: > 0 } projectFilters)
        {
            // Generated sqlgraph.HasNeighborsWith ANDs every predicate inside a
            // single EXISTS and does not re-apply the Project soft-delete
            // predicate.
            var projectPredicates = projectFilters
                .Select(project => ProjectStore.BuildFilter(project, "p2", args))
                .Where(static project => project is not null)
                .ToArray();
            var projectCondition = ConnectionSql.Combine(projectPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"user_projects\" up"
                + " JOIN \"projects\" p2 ON p2.\"id\" = up.\"project_id\""
                + " WHERE up.\"user_id\" = " + alias + ".\"id\""
                + (projectCondition is null ? string.Empty : " AND " + projectCondition)
                + ")");
        }

        if (filter.HasAPIKeys is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"api_keys\" ak WHERE ak.\"user_id\" = "
                + alias + ".\"id\")";
            predicates.Add(filter.HasAPIKeys.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasAPIKeysWith is { Count: > 0 } apiKeyFilters)
        {
            // sqlgraph.HasNeighborsWith does not re-apply the APIKey
            // soft-delete predicate.
            var apiKeyPredicates = apiKeyFilters
                .Select(apiKey => ApiKeyStore.BuildFilter(apiKey, "ak", args))
                .Where(static apiKey => apiKey is not null)
                .ToArray();
            var apiKeyCondition = ConnectionSql.Combine(apiKeyPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"api_keys\" ak"
                + " WHERE ak.\"user_id\" = " + alias + ".\"id\""
                + (apiKeyCondition is null ? string.Empty : " AND " + apiKeyCondition)
                + ")");
        }

        if (filter.HasProjectUsers is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"user_projects\" up WHERE up.\"user_id\" = "
                + alias + ".\"id\")";
            predicates.Add(filter.HasProjectUsers.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasProjectUsersWith is { Count: > 0 } userProjectFilters)
        {
            var userProjectPredicates = userProjectFilters
                .Select(userProject => UserProjectFilterSql.Build(userProject, "up", args))
                .Where(static userProject => userProject is not null)
                .ToArray();
            var userProjectCondition = ConnectionSql.Combine(userProjectPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"user_projects\" up"
                + " WHERE up.\"user_id\" = " + alias + ".\"id\""
                + (userProjectCondition is null ? string.Empty : " AND " + userProjectCondition)
                + ")");
        }

        if (filter.HasRoles is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"user_roles\" ur WHERE ur.\"user_id\" = "
                + alias + ".\"id\")";
            predicates.Add(filter.HasRoles.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasRolesWith is { Count: > 0 } roleFilters)
        {
            // Generated sqlgraph.HasNeighborsWith ANDs every predicate inside a
            // single EXISTS and does not re-apply the Role soft-delete
            // predicate.
            var rolePredicates = roleFilters
                .Select(role => RoleStore.BuildFilter(role, "r2", args))
                .Where(static role => role is not null)
                .ToArray();
            var roleCondition = ConnectionSql.Combine(rolePredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"user_roles\" ur"
                + " JOIN \"roles\" r2 ON r2.\"id\" = ur.\"role_id\""
                + " WHERE ur.\"user_id\" = " + alias + ".\"id\""
                + (roleCondition is null ? string.Empty : " AND " + roleCondition)
                + ")");
        }

        if (filter.HasUserRoles is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"user_roles\" ur WHERE ur.\"user_id\" = "
                + alias + ".\"id\")";
            predicates.Add(filter.HasUserRoles.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasUserRolesWith is { Count: > 0 } userRoleFilters)
        {
            var userRolePredicates = userRoleFilters
                .Select(userRole => UserRoleFilterSql.Build(userRole, "ur", args))
                .Where(static userRole => userRole is not null)
                .ToArray();
            var userRoleCondition = ConnectionSql.Combine(userRolePredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"user_roles\" ur"
                + " WHERE ur.\"user_id\" = " + alias + ".\"id\""
                + (userRoleCondition is null ? string.Empty : " AND " + userRoleCondition)
                + ")");
        }

        if (filter.HasChannelOverrideTemplates is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"channel_override_templates\" cot"
                + " WHERE cot.\"user_id\" = " + alias + ".\"id\")";
            predicates.Add(
                filter.HasChannelOverrideTemplates.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasChannelOverrideTemplatesWith is { Count: > 0 } templateFilters)
        {
            // Generated sqlgraph.HasNeighborsWith ANDs every predicate inside a
            // single EXISTS and does not re-apply the template soft delete.
            var templatePredicates = templateFilters
                .Select(template => ChannelOverrideTemplateStore.BuildFilter(
                    template,
                    "cot",
                    args))
                .Where(static template => template is not null)
                .ToArray();
            var templateCondition = ConnectionSql.Combine(templatePredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"channel_override_templates\" cot"
                + " WHERE cot.\"user_id\" = " + alias + ".\"id\""
                + (templateCondition is null ? string.Empty : " AND " + templateCondition)
                + ")");
        }

        if (filter.HasOidcIdentities is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"oidc_identities\" oi"
                + " WHERE oi.\"user_id\" = " + alias + ".\"id\")";
            predicates.Add(filter.HasOidcIdentities.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasOidcIdentitiesWith is { Count: > 0 } identityFilters)
        {
            // Generated sqlgraph.HasNeighborsWith ANDs every predicate inside a
            // single EXISTS and does not re-apply the identity soft delete.
            var identityPredicates = identityFilters
                .Select(identity => OidcIdentityAdminStore.BuildFilter(identity, "oi", args))
                .Where(static identity => identity is not null)
                .ToArray();
            var identityCondition = ConnectionSql.Combine(identityPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"oidc_identities\" oi"
                + " WHERE oi.\"user_id\" = " + alias + ".\"id\""
                + (identityCondition is null ? string.Empty : " AND " + identityCondition)
                + ")");
        }

        if (filter.HasProjectUsersWithProjectId is not null)
        {
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"user_projects\" up WHERE up.\"user_id\" = "
                + alias
                + ".\"id\" AND up.\"project_id\" = "
                + args.Add(filter.HasProjectUsersWithProjectId.Value)
                + ")");
        }

        if (filter.Not is not null)
        {
            var not = BuildFilter(filter.Not, alias, args);
            if (not is not null)
            {
                predicates.Add("NOT (" + not + ")");
            }
        }

        if (filter.And is not null)
        {
            var and = filter.And
                .Select(nested => BuildFilter(nested, alias, args))
                .Where(static nested => nested is not null)
                .ToArray();
            if (and.Length > 0)
            {
                predicates.Add("(" + string.Join(" AND ", and) + ")");
            }
        }

        if (filter.Or is not null)
        {
            var or = filter.Or
                .Select(nested => BuildFilter(nested, alias, args))
                .Where(static nested => nested is not null)
                .ToArray();
            predicates.Add(or.Length == 0 ? "FALSE" : "(" + string.Join(" OR ", or) + ")");
        }

        return ConnectionSql.Combine(predicates);
    }
}
