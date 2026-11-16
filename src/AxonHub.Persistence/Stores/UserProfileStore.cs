using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>Port of the fields UserService.UpdateOwnProfile writes.</summary>
public sealed record OwnProfileUpdate
{
    public required long Id { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public string? PreferLanguage { get; init; }

    /// <summary>A null value means "not supplied"; empty string is written.</summary>
    public string? Avatar { get; init; }

    /// <summary>Already hashed by the service, exactly like UserService.</summary>
    public string? Password { get; init; }
}

/// <summary>
/// Persistence seam for the self-service queries of me.graphql: the user with
/// its roles, projects and OIDC identities, the myProjects lookup, and the
/// updateMyPassword / unlinkOIDCIdentity writes.
/// </summary>
public interface IUserProfileStore
{
    Task<UserRecord?> GetUserAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Port of User.Query().WithRoles() over the user_roles join.</summary>
    Task<IReadOnlyList<RoleRecord>> ListUserRolesAsync(
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>Port of User.Query().WithProjectUsers().</summary>
    Task<IReadOnlyList<UserProjectRecord>> ListUserProjectsAsync(
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>Port of User.Query().WithOidcIdentities().</summary>
    Task<IReadOnlyList<OidcIdentityRecord>> ListOidcIdentitiesAsync(
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>Port of myProjects: HasUsersWith(user) and status = active.</summary>
    Task<IReadOnlyList<ProjectRecord>> ListActiveProjectsAsync(
        long userId,
        CancellationToken cancellationToken = default);

    Task<UserRecord?> UpdateOwnProfileAsync(
        OwnProfileUpdate update,
        CancellationToken cancellationToken = default);

    Task<OidcIdentityRecord?> GetOidcIdentityAsync(
        long id,
        CancellationToken cancellationToken = default);

    Task<int> CountOidcIdentitiesAsync(
        long userId,
        CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes the identity; false when no live row matches.</summary>
    Task<bool> UnlinkOidcIdentityAsync(long id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>users</c>, <c>roles</c>, <c>user_projects</c> and
/// <c>oidc_identities</c> rows read by biz.UserService.GetUserByID, plus the two
/// self-service writes. Ent emits no ORDER BY for the eager-loaded edges; the
/// port pins each collection by primary key (C164).
/// </summary>
public sealed class UserProfileStore(AxonHubDbContext db) : IUserProfileStore
{
    private const string UserColumns = """
        "id", "created_at", "updated_at", "deleted_at", "email", "status",
        "prefer_language", "password", "first_name", "last_name", "avatar",
        "is_owner", "scopes"
        """;

    private const string RoleColumns = """
        "id", "created_at", "updated_at", "deleted_at", "name", "level",
        "scopes", "project_id"
        """;

    private const string UserProjectColumns = """
        "id", "created_at", "updated_at", "is_owner", "scopes", "user_id",
        "project_id"
        """;

    private const string OidcIdentityColumns = """
        "id", "created_at", "updated_at", "deleted_at", "issuer", "subject",
        "email", "idp_name", "last_login_at", "user_id"
        """;

    private const string ProjectColumns = """
        "id", "created_at", "updated_at", "deleted_at", "name", "description",
        "status", "profiles"
        """;

    public async Task<UserRecord?> GetUserAsync(
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

    public async Task<IReadOnlyList<RoleRecord>> ListUserRolesAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        // Ent's eager load joins roles to user_roles and applies the role
        // soft-delete predicate, so rows of deleted roles disappear.
        var sql = "SELECT " + string.Join(
                ", ",
                RoleColumns.Split(',').Select(static column => "r." + column.Trim()))
            + " FROM \"roles\" r"
            + " JOIN \"user_roles\" ur ON ur.\"role_id\" = r.\"id\""
            + " WHERE ur.\"user_id\" = @UserId AND r.\"deleted_at\" = 0"
            + " ORDER BY r.\"id\" ASC;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<RoleRecord>(
            new CommandDefinition(
                sql,
                new { UserId = userId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<UserProjectRecord>> ListUserProjectsAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + UserProjectColumns
            + " FROM \"user_projects\" WHERE \"user_id\" = @UserId ORDER BY \"id\" ASC;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<UserProjectRecord>(
            new CommandDefinition(
                sql,
                new { UserId = userId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<OidcIdentityRecord>> ListOidcIdentitiesAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + OidcIdentityColumns
            + " FROM \"oidc_identities\" WHERE \"user_id\" = @UserId AND \"deleted_at\" = 0"
            + " ORDER BY \"id\" ASC;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<OidcIdentityRecord>(
            new CommandDefinition(
                sql,
                new { UserId = userId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<ProjectRecord>> ListActiveProjectsAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        // project.HasUsersWith(user.IDEQ(id)) + project.StatusEQ(active).
        var sql = "SELECT " + string.Join(
                ", ",
                ProjectColumns.Split(',').Select(static column => "p." + column.Trim()))
            + " FROM \"projects\" p"
            + " WHERE p.\"deleted_at\" = 0"
            + " AND p.\"status\" = 'active'"
            + " AND EXISTS ("
            + " SELECT 1 FROM \"user_projects\" up"
            + " WHERE up.\"project_id\" = p.\"id\" AND up.\"user_id\" = @UserId)"
            + " ORDER BY p.\"id\" ASC;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ProjectRecord>(
            new CommandDefinition(
                sql,
                new { UserId = userId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<UserRecord?> UpdateOwnProfileAsync(
        OwnProfileUpdate update,
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

        // SetNillableAvatar: only a supplied value is written; the self-service
        // input cannot clear the avatar.
        if (update.Avatar is not null)
        {
            user.Avatar = update.Avatar;
        }

        if (update.Password is not null)
        {
            user.Password = update.Password;
        }

        user.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Read inside the transaction: an Npgsql transaction cannot be used once
        // it has been committed.
        var result = await GetUserAsync(update.Id, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return result;
    }

    public async Task<OidcIdentityRecord?> GetOidcIdentityAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + OidcIdentityColumns
            + " FROM \"oidc_identities\" WHERE \"id\" = @Id AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<OidcIdentityRecord>(
            new CommandDefinition(
                sql,
                new { Id = id },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> CountOidcIdentitiesAsync(
        long userId,
        CancellationToken cancellationToken = default)
    {
        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                "SELECT COUNT(*) FROM \"oidc_identities\""
                + " WHERE \"user_id\" = @UserId AND \"deleted_at\" = 0;",
                new { UserId = userId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> UnlinkOidcIdentityAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var identity = await db.OidcIdentities.SingleOrDefaultAsync(
            row => row.Id == id && row.DeletedAt == 0,
            cancellationToken).ConfigureAwait(false);
        if (identity is null)
        {
            return false;
        }

        var now = DateTime.UtcNow;
        identity.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        identity.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
