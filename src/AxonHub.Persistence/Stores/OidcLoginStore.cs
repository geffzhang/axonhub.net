using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>An OIDC identity together with its (possibly soft-deleted) user edge.</summary>
public sealed record OidcIdentityWithUser(OidcIdentityRecord Identity, UserRecord? User);

/// <summary>
/// Wraps a failed step of <see cref="IOidcLoginStore.CreateUserWithIdentityAsync"/>
/// so the service can reproduce the reference's per-step error prefixes
/// (<c>failed to create user</c>, <c>failed to create identity for new user</c>).
/// </summary>
public sealed class OidcLoginStoreException(string stage, string message, Exception innerException)
    : Exception(message, innerException)
{
    /// <summary><c>user</c>, <c>identity</c> or <c>roles</c>.</summary>
    public string Stage { get; } = stage;
}

/// <summary>Port of the field and edge mutations of syncUserInfo's ent update.</summary>
public sealed record OidcSyncUpdate
{
    public required long UserId { get; init; }

    /// <summary>Set together with <see cref="LastName"/> when either is non-empty.</summary>
    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public string? Avatar { get; init; }

    public bool? IsOwner { get; init; }

    public IReadOnlyList<string>? Scopes { get; init; }

    public bool ClearScopes { get; init; }

    public bool ClearRoles { get; init; }

    public IReadOnlyList<long> AddRoleIds { get; init; } = [];
}

/// <summary>
/// Persistence seam for the OIDC login flow's user/identity reads and writes
/// (internal/server/biz/oidc.go:937-1300): identity lookup with the user edge,
/// email auto-linking, the JIT user+identity transaction, sync updates and the
/// role queries behind applyRoleMappings.
/// </summary>
public interface IOidcLoginStore
{
    Task<OidcIdentityWithUser?> GetIdentityWithUserAsync(
        string issuer,
        string subject,
        CancellationToken cancellationToken = default);

    Task TouchIdentityLastLoginAsync(
        long identityId,
        DateTime lastLoginAt,
        CancellationToken cancellationToken = default);

    Task<UserRecord?> GetUserByEmailAsync(
        string email,
        CancellationToken cancellationToken = default);

    Task<UserRecord?> GetUserByIdAsync(
        long id,
        CancellationToken cancellationToken = default);

    Task<long> CreateIdentityAsync(
        OidcIdentityRecord identity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of resolveUser's transaction: create the user (with the role ids
    /// resolved from the mapped names), then the identity row. A duplicate
    /// email/identity surfaces as the database exception, like ent's
    /// constraint error.
    /// </summary>
    Task<UserRecord> CreateUserWithIdentityAsync(
        UserRecord user,
        IReadOnlyList<string> roleNames,
        OidcIdentityRecord identity,
        CancellationToken cancellationToken = default);

    /// <summary>Returns null when the live user row no longer exists.</summary>
    Task<UserRecord?> UpdateUserForSyncAsync(
        OidcSyncUpdate update,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoleRecord>> ListRolesByNameAsync(
        IReadOnlyList<string> names,
        CancellationToken cancellationToken = default);

    Task<int> CountUserRolesAsync(
        long userId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the OIDC login flow. Writes go through EF Core and reads through
/// Dapper, honouring the SoftDeleteMixin predicate (<c>deleted_at = 0</c>).
/// </summary>
public sealed class OidcLoginStore(AxonHubDbContext db) : IOidcLoginStore
{
    private const string IdentityColumns = """
        "id", "created_at", "updated_at", "deleted_at", "issuer", "subject",
        "email", "idp_name", "last_login_at", "user_id"
        """;

    private const string UserColumns = """
        "id", "created_at", "updated_at", "deleted_at", "email", "status",
        "prefer_language", "password", "first_name", "last_name", "avatar",
        "is_owner", "scopes"
        """;

    public async Task<OidcIdentityWithUser?> GetIdentityWithUserAsync(
        string issuer,
        string subject,
        CancellationToken cancellationToken = default)
    {
        var identitySql = "SELECT " + IdentityColumns
            + " FROM \"oidc_identities\" WHERE \"issuer\" = @Issuer"
            + " AND \"subject\" = @Subject AND \"deleted_at\" = 0 LIMIT 1;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var identity = await connection.QuerySingleOrDefaultAsync<OidcIdentityRecord>(
            new CommandDefinition(
                identitySql,
                new { Issuer = issuer, Subject = subject },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (identity is null)
        {
            return null;
        }

        var userSql = "SELECT " + UserColumns
            + " FROM \"users\" WHERE \"id\" = @Id AND \"deleted_at\" = 0 LIMIT 1;";
        var user = await connection.QuerySingleOrDefaultAsync<UserRecord>(
            new CommandDefinition(
                userSql,
                new { Id = identity.UserId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new OidcIdentityWithUser(identity, user);
    }

    public async Task TouchIdentityLastLoginAsync(
        long identityId,
        DateTime lastLoginAt,
        CancellationToken cancellationToken = default)
    {
        var identity = await db.OidcIdentities
            .FirstOrDefaultAsync(
                row => row.Id == identityId && row.DeletedAt == 0,
                cancellationToken)
            .ConfigureAwait(false);
        if (identity is null)
        {
            return;
        }

        identity.LastLoginAt = lastLoginAt;
        identity.UpdatedAt = lastLoginAt;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<UserRecord?> GetUserByEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + UserColumns
            + " FROM \"users\" WHERE \"email\" = @Email AND \"deleted_at\" = 0 LIMIT 1;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<UserRecord>(
            new CommandDefinition(
                sql,
                new { Email = email },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<UserRecord?> GetUserByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + UserColumns
            + " FROM \"users\" WHERE \"id\" = @Id AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<UserRecord>(
            new CommandDefinition(
                sql,
                new { Id = id },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<long> CreateIdentityAsync(
        OidcIdentityRecord identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var now = DateTime.UtcNow;
        if (identity.CreatedAt == default)
        {
            identity.CreatedAt = now;
        }

        if (identity.UpdatedAt == default)
        {
            identity.UpdatedAt = now;
        }

        if (identity.LastLoginAt is null)
        {
            identity.LastLoginAt = now;
        }

        db.OidcIdentities.Add(identity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return identity.Id;
    }

    public async Task<UserRecord> CreateUserWithIdentityAsync(
        UserRecord user,
        IReadOnlyList<string> roleNames,
        OidcIdentityRecord identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(roleNames);
        ArgumentNullException.ThrowIfNull(identity);

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
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OidcLoginStoreException("user", ex.Message, ex);
        }

        if (roleNames.Count > 0)
        {
            var names = roleNames.Distinct(StringComparer.Ordinal).ToList();
            List<RoleRecord> roles;
            try
            {
                roles = await db.Roles
                    .Where(role => names.Contains(role.Name) && role.DeletedAt == 0)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // applyRoleMappings ignores a failed role query and continues.
                roles = [];
            }

            foreach (var roleId in roles.Select(static role => role.Id).Distinct())
            {
                db.UserRoles.Add(new UserRoleRecord
                {
                    CreatedAt = now,
                    UpdatedAt = now,
                    UserId = user.Id,
                    RoleId = roleId,
                });
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The reference adds the role edges to the same user mutation,
                // so their failure surfaces as the user save error.
                throw new OidcLoginStoreException("user", ex.Message, ex);
            }
        }

        identity.UserId = user.Id;
        if (identity.CreatedAt == default)
        {
            identity.CreatedAt = now;
        }

        if (identity.UpdatedAt == default)
        {
            identity.UpdatedAt = now;
        }

        if (identity.LastLoginAt is null)
        {
            identity.LastLoginAt = now;
        }

        db.OidcIdentities.Add(identity);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OidcLoginStoreException("identity", ex.Message, ex);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return user;
    }

    public async Task<UserRecord?> UpdateUserForSyncAsync(
        OidcSyncUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        var user = await db.Users
            .FirstOrDefaultAsync(
                row => row.Id == update.UserId && row.DeletedAt == 0,
                cancellationToken)
            .ConfigureAwait(false);
        if (user is null)
        {
            return null;
        }

        if (update.FirstName is not null || update.LastName is not null)
        {
            user.FirstName = update.FirstName ?? string.Empty;
            user.LastName = update.LastName ?? string.Empty;
        }

        if (update.Avatar is not null)
        {
            user.Avatar = update.Avatar;
        }

        if (update.IsOwner is bool isOwner)
        {
            user.IsOwner = isOwner;
        }

        if (update.Scopes is not null)
        {
            user.Scopes = JsonSerializer.Serialize(update.Scopes);
        }
        else if (update.ClearScopes)
        {
            user.Scopes = null;
        }

        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (update.ClearRoles)
        {
            await db.UserRoles
                .Where(link => link.UserId == user.Id)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (update.AddRoleIds.Count > 0)
        {
            var existing = await db.UserRoles
                .Where(link => link.UserId == user.Id)
                .Select(link => link.RoleId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            var now = DateTime.UtcNow;
            foreach (var roleId in update.AddRoleIds.Distinct())
            {
                if (existing.Contains(roleId))
                {
                    continue;
                }

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
        return user;
    }

    public async Task<IReadOnlyList<RoleRecord>> ListRolesByNameAsync(
        IReadOnlyList<string> names,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        if (names.Count == 0)
        {
            return [];
        }

        var wanted = names.Distinct(StringComparer.Ordinal).ToList();
        return await db.Roles
            .Where(role => wanted.Contains(role.Name) && role.DeletedAt == 0)
            .OrderBy(role => role.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<int> CountUserRolesAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT COUNT(DISTINCT ur."role_id") FROM "user_roles" ur
            JOIN "roles" r ON r."id" = ur."role_id" AND r."deleted_at" = 0
            WHERE ur."user_id" = @UserId;
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                sql,
                new { UserId = userId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
