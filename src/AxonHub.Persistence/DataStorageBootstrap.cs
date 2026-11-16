using System.Globalization;
using Dapper;
using Npgsql;

namespace AxonHub.Persistence;

/// <summary>
/// Port of the v0.4.0 data migration
/// (internal/ent/migrate/datamigrate/v0.4.0.go): a database without a primary
/// storage gets the seeded "Primary" database storage and the
/// <c>default_data_storage_id</c> system row pointing at it.
/// </summary>
/// <remarks>
/// The reference runs this through its version-tracked DataMigrator after the
/// system is initialized. The port has no data-migration bookkeeping table
/// (DatabaseMigrator note); the migration's own guard — a primary storage
/// already exists — makes it idempotent on every startup instead. The other
/// registered data migrations repair legacy rows and are no-ops on a database
/// the port creates (C020).
/// </remarks>
public static class DataStorageBootstrap
{
    /// <summary>
    /// Port of the guard at the top of datamigrate.Migrator.Run
    /// (internal/ent/migrate/datamigrate/migrator.go): no data migration runs
    /// until the system has been initialized. The port runs this check on
    /// every startup because it has no migration bookkeeping table.
    /// </summary>
    public static async Task<bool> IsSystemInitializedAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // SystemService.IsInitialized: System.Query().Where(KeyEQ).Only with
        // the SoftDeleteMixin predicate and strings.EqualFold(value, "true").
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (
                SELECT 1 FROM "systems"
                WHERE "key" = 'system_initialized'
                  AND "deleted_at" = 0
                  AND lower("value") = 'true'
            );
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>Port of V0_4_0.Migrate.</summary>
    public static async Task EnsurePrimaryDataStorageAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // datastorage.Primary(true).Exist: ent's generated query carries the
        // SoftDeleteMixin predicate.
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            """
            SELECT EXISTS (
                SELECT 1 FROM "data_storages"
                WHERE "primary" = TRUE AND "deleted_at" = 0
            );
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (exists)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // ent DataStorage.Create() with the migration's values; the TimeMixin
        // columns come from the table defaults, like every other insert.
        var storageId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO "data_storages"
                ("name", "description", "primary", "type", "settings", "status")
            VALUES ('Primary', 'Primary database storage', TRUE, 'database', '{}', 'active')
            RETURNING "id";
            """,
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        // System.Create() with biz.SystemKeyDefaultDataStorage and
        // fmt.Sprintf("%d", primaryDataStorage.ID).
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO "systems" ("key", "value")
            VALUES ('default_data_storage_id', @Value);
            """,
            new { Value = storageId.ToString(CultureInfo.InvariantCulture) },
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
