using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using static AxonHub.Persistence.ConnectionSql;

namespace AxonHub.Persistence;

/// <summary>Ordering fields exposed by the reference RoleOrder type.</summary>
public enum RoleSortField
{
    Id,
    UpdatedAt,
}

/// <summary>
/// Port of <c>RoleWhereInput</c> (ent.graphql:5531-5615). All scalar field
/// predicates plus the <c>users</c>, <c>project</c> and <c>user_roles</c> edge
/// predicates are modeled.
/// </summary>
public sealed record RoleListFilter
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

    public string? Name { get; init; }

    public string? NameNEQ { get; init; }

    public IReadOnlyList<string>? NameIn { get; init; }

    public IReadOnlyList<string>? NameNotIn { get; init; }

    public string? NameGT { get; init; }

    public string? NameGTE { get; init; }

    public string? NameLT { get; init; }

    public string? NameLTE { get; init; }

    public string? NameContains { get; init; }

    public string? NameHasPrefix { get; init; }

    public string? NameHasSuffix { get; init; }

    public string? NameEqualFold { get; init; }

    public string? NameContainsFold { get; init; }

    public string? Level { get; init; }

    public string? LevelNEQ { get; init; }

    public IReadOnlyList<string>? LevelIn { get; init; }

    public IReadOnlyList<string>? LevelNotIn { get; init; }

    public long? ProjectId { get; init; }

    public long? ProjectIdNEQ { get; init; }

    public IReadOnlyList<long>? ProjectIdIn { get; init; }

    public IReadOnlyList<long>? ProjectIdNotIn { get; init; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool ProjectIdIsNil { get; init; }

    /// <summary>Ent reads this field as a plain bool: false is ignored.</summary>
    public bool ProjectIdNotNil { get; init; }

    /// <summary>
    /// Ent's <c>hasProject</c>: true → the role's <c>project_id</c> points at
    /// an existing project, false → NOT EXISTS. Generated sqlgraph does not
    /// re-apply the Project soft-delete predicate.
    /// </summary>
    public bool? HasProject { get; init; }

    public IReadOnlyList<ProjectListFilter>? HasProjectWith { get; init; }

    /// <summary>
    /// Ent's <c>hasUsers</c>: true → the role has a matching <c>user_roles</c>
    /// link, false → NOT EXISTS. Generated sqlgraph does not re-apply the User
    /// soft-delete predicate.
    /// </summary>
    public bool? HasUsers { get; init; }

    public IReadOnlyList<UserListFilter>? HasUsersWith { get; init; }

    /// <summary>
    /// Ent's <c>hasUserRoles</c>: true → the role has a <c>user_roles</c> row,
    /// false → NOT EXISTS.
    /// </summary>
    public bool? HasUserRoles { get; init; }

    public IReadOnlyList<UserRoleListFilter>? HasUserRolesWith { get; init; }

    public RoleListFilter? Not { get; init; }

    public IReadOnlyList<RoleListFilter>? And { get; init; }

    public IReadOnlyList<RoleListFilter>? Or { get; init; }
}

/// <summary>
/// Entgql pagination request for roles. <see cref="UserId"/> adds the
/// user_roles join used by the nested <c>User.roles</c> connection.
/// </summary>
public sealed record RoleListQuery
{
    public RoleListFilter? Where { get; init; }

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public RoleSortField SortField { get; init; } = RoleSortField.Id;

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }

    public long? UserId { get; init; }
}

/// <summary>Persistence seam for the reference <c>roles</c> and <c>user_roles</c> tables.</summary>
public interface IRoleStore
{
    Task<long> CreateAsync(RoleRecord role, CancellationToken cancellationToken = default);

    Task<RoleRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs <paramref name="action"/> in a transaction, joining the ambient one
    /// when the caller (RoleManagementService.DeleteRole/BulkDeleteRoles)
    /// already opened it. The reference's RoleService.RunInTransaction.
    /// </summary>
    Task<T> RunInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default);

    /// <inheritdoc cref="RunInTransactionAsync{T}"/>
    Task RunInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default);

    /// <summary>Port of RoleService.RoleNameExists (live rows only).</summary>
    Task<bool> NameExistsAsync(
        string name,
        long projectId,
        CancellationToken cancellationToken = default);

    Task<ListPage<RoleRecord>> ListAsync(
        RoleListQuery query,
        CancellationToken cancellationToken = default);

    /// <param name="projectScope">
    /// The <c>WhereP(project_id = ctx)</c> predicate the Role query policy
    /// installs; a row of another project is filtered out.
    /// </param>
    Task<IReadOnlyList<RoleRecord>> ListByIdsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default,
        long? projectScope = null);

    /// <param name="projectScope">
    /// The <c>WhereP(project_id = ctx)</c> predicate the Role mutation policy
    /// installs; a mismatch updates no row (ent NotFound).
    /// </param>
    Task<RoleRecord?> UpdateAsync(
        long id,
        string? name,
        IReadOnlyList<string>? scopes,
        CancellationToken cancellationToken = default,
        long? projectScope = null);

    /// <summary>Deletes the role's user_roles rows and soft-deletes the role.</summary>
    /// <param name="projectScope">
    /// The <c>WhereP(project_id = ctx)</c> predicate the Role mutation policy
    /// installs; a mismatch deletes nothing (ent NotFound).
    /// </param>
    Task<bool> DeleteAsync(
        long id,
        CancellationToken cancellationToken = default,
        long? projectScope = null);

    /// <summary>Deletes user_roles rows and soft-deletes every listed role.</summary>
    /// <param name="projectScope">
    /// The <c>WhereP(project_id = ctx)</c> predicate the Role mutation policy
    /// installs; rows of other projects are left untouched.
    /// </param>
    Task DeleteManyAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default,
        long? projectScope = null);
}

/// <summary>
/// Store for the reference <c>roles</c> table. Writes go through EF Core; reads
/// use Dapper and honour the SoftDeleteMixin predicate (<c>deleted_at = 0</c>).
/// </summary>
public sealed class RoleStore(AxonHubDbContext db) : IRoleStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "name", "level",
        "scopes", "project_id"
        """;

    public async Task<long> CreateAsync(RoleRecord role, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(role);

        db.Roles.Add(role);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return role.Id;
    }

    public async Task<RoleRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"roles\" WHERE \"id\" = @Id AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<RoleRecord>(
            new CommandDefinition(
                sql,
                new { Id = id },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> NameExistsAsync(
        string name,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM "roles"
                WHERE "deleted_at" = 0 AND "name" = @Name
                  AND "project_id" = @ProjectId
            );
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                sql,
                new { Name = name, ProjectId = projectId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<ListPage<RoleRecord>> ListAsync(
        RoleListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var args = new SqlArguments();
        var where = BuildFilter(query.Where, "r", args);

        var from = query.UserId is null
            ? " FROM \"roles\" r"
            : " FROM \"roles\" r JOIN \"user_roles\" ur ON ur.\"role_id\" = r.\"id\"";

        var conditions = new StringBuilder(" WHERE r.\"deleted_at\" = 0");
        if (query.UserId is not null)
        {
            conditions.Append(" AND ur.\"user_id\" = ").Append(args.Add(query.UserId.Value));
        }

        if (where is not null)
        {
            conditions.Append(" AND ").Append(where);
        }

        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;

        var countSql = "SELECT COUNT(*) " + from + conditions + ";";

        var nodesSql = new StringBuilder("SELECT ");
        nodesSql.Append(string.Join(", ", Columns
            .Split(',')
            .Select(static column => "r." + column.Trim())));
        nodesSql.Append(from).Append(conditions);

        ConnectionSql.AppendCursor(
            nodesSql,
            "r",
            "id",
            query.SortField == RoleSortField.UpdatedAt ? "updated_at" : null,
            query.After,
            query.Before,
            direction,
            args);

        nodesSql.Append(" ORDER BY ")
            .Append(ConnectionSql.OrderBy(
                "r",
                "id",
                query.SortField == RoleSortField.UpdatedAt ? "updated_at" : null,
                direction));

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

        var nodes = (await connection.QueryAsync<RoleRecord>(
            new CommandDefinition(
                nodesSql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

        return BuildPage(nodes, query, totalCount);
    }

    public async Task<IReadOnlyList<RoleRecord>> ListByIdsAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default,
        long? projectScope = null)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var args = new SqlArguments();
        var predicates = new List<string?>();
        ConnectionSql.AppendIdIn(predicates, "r", "id", ids, args);
        if (projectScope is not null)
        {
            // Role query policy: WhereProjectID(ctx).
            predicates.Add("r.\"project_id\" = " + args.Add(projectScope.Value));
        }

        var sql = "SELECT " + Columns + " FROM \"roles\" r WHERE r.\"deleted_at\" = 0 AND "
            + ConnectionSql.Combine(predicates) + " ORDER BY r.\"id\" ASC;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<RoleRecord>(
            new CommandDefinition(
                sql,
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    public async Task<RoleRecord?> UpdateAsync(
        long id,
        string? name,
        IReadOnlyList<string>? scopes,
        CancellationToken cancellationToken = default,
        long? projectScope = null)
    {
        var lookup = db.Roles.Where(row => row.Id == id && row.DeletedAt == 0);
        if (projectScope is { } scope)
        {
            // Role mutation policy: WhereP(project_id = ctx).
            lookup = lookup.Where(row => row.ProjectId == scope);
        }

        var role = await lookup
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (role is null)
        {
            return null;
        }

        if (name is not null)
        {
            role.Name = name;
        }

        if (scopes is not null)
        {
            role.Scopes = JsonSerializer.Serialize(scopes);
        }

        role.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<T> RunInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    public Task RunInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        return RunInTransactionAsync(
            async token =>
            {
                await action(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public async Task<bool> DeleteAsync(
        long id,
        CancellationToken cancellationToken = default,
        long? projectScope = null)
    {
        return await RunInTransactionAsync(
            async token =>
            {
                var lookup = db.Roles.Where(row => row.Id == id && row.DeletedAt == 0);
                if (projectScope is { } scope)
                {
                    // Role mutation policy: WhereP(project_id = ctx).
                    lookup = lookup.Where(row => row.ProjectId == scope);
                }

                var role = await lookup
                    .SingleOrDefaultAsync(token)
                    .ConfigureAwait(false);
                if (role is null)
                {
                    return false;
                }

                try
                {
                    await db.UserRoles
                        .Where(row => row.RoleId == id)
                        .ExecuteDeleteAsync(token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new StoreFailureException(
                        "failed to delete user role relationships: " + ex.Message,
                        ex);
                }

                var now = DateTime.UtcNow;
                role.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                role.UpdatedAt = now;

                try
                {
                    await db.SaveChangesAsync(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new StoreFailureException("failed to delete role: " + ex.Message, ex);
                }

                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteManyAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default,
        long? projectScope = null)
    {
        if (ids.Count == 0)
        {
            return;
        }

        await RunInTransactionAsync(
            async token =>
            {
                // Ent's bulk delete selects the matched ids first (the Role
                // mutation policy's WhereP(project_id = ctx) included), clears
                // their edges, then updates the nodes.
                var lookup = db.Roles.Where(row => ids.Contains(row.Id) && row.DeletedAt == 0);
                if (projectScope is { } scope)
                {
                    lookup = lookup.Where(row => row.ProjectId == scope);
                }

                var matched = await lookup
                    .Select(row => row.Id)
                    .ToListAsync(token)
                    .ConfigureAwait(false);
                if (matched.Count == 0)
                {
                    return;
                }

                try
                {
                    await db.UserRoles
                        .Where(row => matched.Contains(row.RoleId))
                        .ExecuteDeleteAsync(token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new StoreFailureException(
                        "failed to delete user role relationships: " + ex.Message,
                        ex);
                }

                var now = DateTime.UtcNow;
                var deletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                try
                {
                    await db.Roles
                        .Where(row => matched.Contains(row.Id))
                        .ExecuteUpdateAsync(
                            setters => setters
                                .SetProperty(row => row.DeletedAt, deletedAt)
                                .SetProperty(row => row.UpdatedAt, now),
                            token)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new StoreFailureException("failed to delete roles: " + ex.Message, ex);
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static ListPage<RoleRecord> BuildPage(
        List<RoleRecord> nodes,
        RoleListQuery query,
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

        return new ListPage<RoleRecord>(nodes, hasNextPage, hasPreviousPage, totalCount);
    }

    internal static string? BuildFilter(
        RoleListFilter? filter,
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
            "name",
            filter.Name,
            filter.NameNEQ,
            filter.NameIn,
            filter.NameNotIn,
            filter.NameGT,
            filter.NameGTE,
            filter.NameLT,
            filter.NameLTE,
            filter.NameContains,
            filter.NameHasPrefix,
            filter.NameHasSuffix,
            filter.NameEqualFold,
            filter.NameContainsFold,
            args);

        AppendComparison(predicates, alias, "level", filter.Level, "=", args);
        AppendComparison(predicates, alias, "level", filter.LevelNEQ, "<>", args);
        if (filter.LevelIn is not null)
        {
            AppendStringIn(predicates, alias, "level", filter.LevelIn, args);
        }

        if (filter.LevelNotIn is not null)
        {
            AppendStringIn(predicates, alias, "level", filter.LevelNotIn, args, negate: true);
        }

        AppendComparison(predicates, alias, "project_id", filter.ProjectId, "=", args);
        AppendComparison(predicates, alias, "project_id", filter.ProjectIdNEQ, "<>", args);
        if (filter.ProjectIdIn is not null)
        {
            AppendIdIn(predicates, alias, "project_id", filter.ProjectIdIn, args);
        }

        if (filter.ProjectIdNotIn is not null)
        {
            AppendIdIn(predicates, alias, "project_id", filter.ProjectIdNotIn, args, negate: true);
        }

        if (filter.ProjectIdIsNil)
        {
            predicates.Add(alias + ".\"project_id\" IS NULL");
        }

        if (filter.ProjectIdNotNil)
        {
            predicates.Add(alias + ".\"project_id\" IS NOT NULL");
        }

        if (filter.HasProject is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"projects\" pj WHERE pj.\"id\" = "
                + alias + ".\"project_id\")";
            predicates.Add(filter.HasProject.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasProjectWith is { Count: > 0 } projectFilters)
        {
            // sqlgraph.HasNeighborsWith does not re-apply the Project
            // soft-delete predicate.
            var projectPredicates = projectFilters
                .Select(project => ProjectStore.BuildFilter(project, "pj", args))
                .Where(static project => project is not null)
                .ToArray();
            var projectCondition = ConnectionSql.Combine(projectPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"projects\" pj"
                + " WHERE pj.\"id\" = " + alias + ".\"project_id\""
                + (projectCondition is null ? string.Empty : " AND " + projectCondition)
                + ")");
        }

        if (filter.HasUsers is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"user_roles\" ur2 WHERE ur2.\"role_id\" = "
                + alias + ".\"id\")";
            predicates.Add(filter.HasUsers.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasUsersWith is { Count: > 0 } userFilters)
        {
            // Generated sqlgraph.HasNeighborsWith ANDs every predicate inside a
            // single EXISTS and does not re-apply the User soft-delete
            // predicate.
            var userPredicates = userFilters
                .Select(user => UserAdminStore.BuildFilter(user, "u2", args))
                .Where(static user => user is not null)
                .ToArray();
            var userCondition = ConnectionSql.Combine(userPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"user_roles\" ur2"
                + " JOIN \"users\" u2 ON u2.\"id\" = ur2.\"user_id\""
                + " WHERE ur2.\"role_id\" = " + alias + ".\"id\""
                + (userCondition is null ? string.Empty : " AND " + userCondition)
                + ")");
        }

        if (filter.HasUserRoles is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"user_roles\" ur2 WHERE ur2.\"role_id\" = "
                + alias + ".\"id\")";
            predicates.Add(filter.HasUserRoles.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasUserRolesWith is { Count: > 0 } userRoleFilters)
        {
            var userRolePredicates = userRoleFilters
                .Select(userRole => UserRoleFilterSql.Build(userRole, "ur2", args))
                .Where(static userRole => userRole is not null)
                .ToArray();
            var userRoleCondition = ConnectionSql.Combine(userRolePredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"user_roles\" ur2"
                + " WHERE ur2.\"role_id\" = " + alias + ".\"id\""
                + (userRoleCondition is null ? string.Empty : " AND " + userRoleCondition)
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
