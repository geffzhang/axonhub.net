using Dapper;

namespace AxonHub.Persistence;

/// <summary>Persistence seam for the reference <c>systems</c> table.</summary>
public interface ISystemStore
{
    Task<long> CreateAsync(SystemRecord system, CancellationToken cancellationToken = default);

    Task<SystemRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.getSystemValue's query (live rows only).</summary>
    Task<SystemRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>setSystemValue</c>: ent's <c>OnConflict(key).UpdateNewValues()</c>
    /// upsert. The conflicting row keeps its <c>id</c> and <c>created_at</c>;
    /// the new values overwrite the remaining columns (ent's update set excludes
    /// the primary key and the immutable creation timestamp).
    /// </summary>
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);
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
}
