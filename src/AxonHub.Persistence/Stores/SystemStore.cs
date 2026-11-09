using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence;

/// <summary>Persistence seam for the reference <c>systems</c> table.</summary>
public interface ISystemStore
{
    Task<long> CreateAsync(SystemRecord system, CancellationToken cancellationToken = default);

    Task<SystemRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.getSystemValue's query (live rows only).</summary>
    Task<SystemRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the channel-settings loop's plain <c>System.Create()</c>: insert
    /// the row and report <c>false</c> when the unique index on <c>key</c>
    /// rejects the insert (ent.IsConstraintError), which the caller retries.
    /// </summary>
    Task<bool> TryCreateAsync(
        SystemRecord system,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the channel-settings compare-and-set update
    /// (<c>System.Update().Where(id, updated_at, value).SetValue(...).Save(ctx)</c>):
    /// the soft-delete predicate applies, <c>updated_at</c> advances, and the
    /// returned count is 0 when another writer changed the row first.
    /// </summary>
    Task<int> UpdateValueIfUnchangedAsync(
        long id,
        DateTime expectedUpdatedAt,
        string expectedValue,
        string value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>setSystemValue</c>: ent's <c>OnConflict(key).UpdateNewValues()</c>
    /// upsert. The conflicting row keeps its <c>id</c> and <c>created_at</c>;
    /// the new values overwrite the remaining columns (ent's update set excludes
    /// the primary key and the immutable creation timestamp).
    /// </summary>
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the quota routing migration's claim insert
    /// (<c>System.Create().OnConflict(sql.ConflictColumns(key), sql.ResolveWithIgnore())</c>):
    /// insert the row only when no row with that key exists. Because the unique
    /// index covers <c>key</c> alone, a soft-deleted row also conflicts and
    /// suppresses the insert.
    /// </summary>
    Task CreateIfAbsentAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>AbstractService.RunInTransaction</c>: reuse the enclosing
    /// logical transaction, otherwise open a new one, and commit or roll back
    /// only the transaction this call owns. The callback resolves the same
    /// scoped stores, which share <see cref="AxonHubDbContext"/>.
    /// </summary>
    Task RunInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>systems</c> table. Writes go through EF Core;
/// reads use Dapper and honour the SoftDeleteMixin predicate (<c>deleted_at = 0</c>).
/// </summary>
public sealed class SystemStore(AxonHubDbContext db) : ISystemStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "key", "value"
        """;

    public async Task<long> CreateAsync(SystemRecord system, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        db.Systems.Add(system);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return system.Id;
    }

    public async Task<SystemRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"systems\" WHERE \"id\" = @Id AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<SystemRecord>(
            new CommandDefinition(
                sql,
                new { Id = id },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<SystemRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        // Ent's System.Query().Where(KeyEQ(key)).Only applies the soft-delete
        // predicate, so a deleted row is indistinguishable from a missing one.
        var sql = "SELECT " + Columns
            + " FROM \"systems\" WHERE \"key\" = @Key AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<SystemRecord>(
            new CommandDefinition(
                sql,
                new { Key = key },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> TryCreateAsync(
        SystemRecord system,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        db.Systems.Add(system);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (IsConstraintViolation(ex))
        {
            // Ent keeps no failed-entity state; detaching keeps a later write
            // on the same scoped context from retrying this row.
            db.Entry(system).State = EntityState.Detached;
            return false;
        }
        catch (DbUpdateException)
        {
            db.Entry(system).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<int> UpdateValueIfUnchangedAsync(
        long id,
        DateTime expectedUpdatedAt,
        string expectedValue,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedValue);
        ArgumentNullException.ThrowIfNull(value);

        // Ent's UpdateSave adds the soft-delete predicate and bumps updated_at
        // through TimeMixin's UpdateDefault(xtime.UTCNow).
        const string sql = """
            UPDATE "systems"
            SET "value" = @Value, "updated_at" = CURRENT_TIMESTAMP
            WHERE "id" = @Id
              AND "updated_at" = @ExpectedUpdatedAt
              AND "value" = @ExpectedValue
              AND "deleted_at" = 0;
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                    Id = id,
                    ExpectedUpdatedAt = expectedUpdatedAt,
                    ExpectedValue = expectedValue,
                    Value = value,
                },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Ent classifies every PostgreSQL constraint failure (SQLSTATE class 23)
    /// as a constraint error; the only reachable one here is
    /// <c>systems_key_key</c>.
    /// </summary>
    private static bool IsConstraintViolation(DbUpdateException exception)
        => exception.GetBaseException() is PostgresException { SqlState: { } sqlState }
            && sqlState.StartsWith("23", StringComparison.Ordinal);

    public async Task SetAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        // Ent's generated upsert inserts every defaulted column and, on
        // conflict, updates all insert columns except id/created_at.
        const string sql = """
            INSERT INTO "systems" ("created_at", "updated_at", "deleted_at", "key", "value")
            VALUES (CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 0, @Key, @Value)
            ON CONFLICT ("key") DO UPDATE SET
                "updated_at" = EXCLUDED."updated_at",
                "deleted_at" = EXCLUDED."deleted_at",
                "key" = EXCLUDED."key",
                "value" = EXCLUDED."value";
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { Key = key, Value = value },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task CreateIfAbsentAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        // ent's OnConflict(key) + ResolveWithIgnore renders ON CONFLICT DO
        // NOTHING; PostgreSQL needs the conflict target the migration passes.
        const string sql = """
            INSERT INTO "systems" ("created_at", "updated_at", "deleted_at", "key", "value")
            VALUES (CURRENT_TIMESTAMP, CURRENT_TIMESTAMP, 0, @Key, @Value)
            ON CONFLICT ("key") DO NOTHING;
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { Key = key, Value = value },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task RunInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // ent rolls back only a failing callback; a failing commit surfaces
            // on its own and lets the ambient owner decide.
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            throw;
        }

        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
