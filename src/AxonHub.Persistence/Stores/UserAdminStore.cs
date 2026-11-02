using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>Ordering fields exposed by the reference UserOrder type.</summary>
public enum UserSortField
{
    Id,
    UpdatedAt,
}

/// <summary>Subset of ent.UserWhereInput used by the management frontend.</summary>
public sealed record UserListFilter
{
    public long? Id { get; init; }

    public IReadOnlyList<long>? IdIn { get; init; }

    public string? FirstNameContainsFold { get; init; }

    public string? EmailContainsFold { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

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

        ConnectionSql.AppendCursor(
            nodesSql,
            "u",
            "id",
            query.SortField == UserSortField.UpdatedAt ? "updated_at" : null,
            query.After,
            query.Before,
            direction,
            args);

        nodesSql.Append(" ORDER BY ")
            .Append(ConnectionSql.OrderBy(
                "u",
                "id",
                query.SortField == UserSortField.UpdatedAt ? "updated_at" : null,
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

    private static string? BuildFilter(
        UserListFilter? filter,
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

        if (filter.FirstNameContainsFold is not null)
        {
            ConnectionSql.AppendStringFilter(
                predicates,
                alias,
                "first_name",
                "LIKE",
                filter.FirstNameContainsFold,
                args);
        }

        if (filter.EmailContainsFold is not null)
        {
            ConnectionSql.AppendStringFilter(
                predicates,
                alias,
                "email",
                "LIKE",
                filter.EmailContainsFold,
                args);
        }

        if (filter.StatusIn is not null)
        {
            ConnectionSql.AppendStringIn(predicates, alias, "status", filter.StatusIn, args);
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
