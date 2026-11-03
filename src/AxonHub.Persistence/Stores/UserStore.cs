using Dapper;

namespace AxonHub.Persistence;

/// <summary>Persistence seam for the reference <c>users</c> table.</summary>
public interface IUserStore
{
    Task<long> CreateAsync(UserRecord user, CancellationToken cancellationToken = default);

    Task<UserRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the AuthService.AuthenticateUser lookup:
    /// <c>User.Query().Where(EmailEQ(email)).Where(StatusEQ(activated)).Only()</c>.
    /// </summary>
    Task<UserRecord?> GetActivatedByEmailAsync(
        string email,
        CancellationToken cancellationToken = default);

    /// <summary>Port of User.Query().Where(IsOwnerEQ(true)).First: the owner user.</summary>
    Task<UserRecord?> FindOwnerAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>users</c> table. Writes go through EF Core;
/// reads use Dapper and honour the SoftDeleteMixin predicate (<c>deleted_at = 0</c>).
/// </summary>
public sealed class UserStore(AxonHubDbContext db) : IUserStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "email", "status",
        "prefer_language", "password", "first_name", "last_name", "avatar",
        "is_owner", "scopes"
        """;

    public async Task<long> CreateAsync(UserRecord user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        db.Users.Add(user);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<UserRecord?> GetActivatedByEmailAsync(
        string email,
        CancellationToken cancellationToken = default)
    {
        // Ent's Only() errors on zero rows; returning null lets the service
        // reproduce the same "invalid email or password" classification.
        var sql = "SELECT " + Columns
            + " FROM \"users\" WHERE \"email\" = @Email"
            + " AND \"status\" = 'activated' AND \"deleted_at\" = 0 LIMIT 1;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<UserRecord>(
            new CommandDefinition(
                sql,
                new { Email = email },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<UserRecord?> FindOwnerAsync(CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"users\" WHERE \"deleted_at\" = 0 AND \"is_owner\" = true"
            + " ORDER BY \"id\" LIMIT 1;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<UserRecord>(
            new CommandDefinition(
                sql,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
