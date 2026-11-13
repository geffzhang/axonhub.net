using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>Ordering fields exposed by the reference RoleOrder type.</summary>
public enum RoleSortField
{
    Id,
    UpdatedAt,
}

/// <summary>Subset of ent.RoleWhereInput used by the management frontend.</summary>
public sealed record RoleListFilter
{
    public long? Id { get; init; }

    public IReadOnlyList<long>? IdIn { get; init; }

    public string? NameContainsFold { get; init; }

    public string? Level { get; init; }

    public long? ProjectId { get; init; }

    public bool? ProjectIdIsNil { get; init; }

    public bool? ProjectIdNotNil { get; init; }

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

    private static string? BuildFilter(
        RoleListFilter? filter,
        string alias,
        SqlArguments args)
    {
        if (filter is null)
        {
            return null;
        }

        var predicates = new List<string?>();

        if (filter.Id is not null)
        {
            predicates.Add(alias + ".\"id\" = " + args.Add(filter.Id.Value));
        }

        if (filter.IdIn is not null)
        {
            ConnectionSql.AppendIdIn(predicates, alias, "id", filter.IdIn, args);
        }

        if (filter.NameContainsFold is not null)
        {
            ConnectionSql.AppendStringFilter(
                predicates,
                alias,
                "name",
                "LIKE",
                filter.NameContainsFold,
                args);
        }

        if (filter.Level is not null)
        {
            predicates.Add(alias + ".\"level\" = " + args.Add(filter.Level));
        }

        if (filter.ProjectId is not null)
        {
            predicates.Add(alias + ".\"project_id\" = " + args.Add(filter.ProjectId.Value));
        }

        if (filter.ProjectIdIsNil is not null)
        {
            predicates.Add(
                alias + ".\"project_id\" IS "
                + (filter.ProjectIdIsNil.Value ? "NULL" : "NOT NULL"));
        }

        if (filter.ProjectIdNotNil is not null)
        {
            predicates.Add(
                alias + ".\"project_id\" IS "
                + (filter.ProjectIdNotNil.Value ? "NOT NULL" : "NULL"));
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
