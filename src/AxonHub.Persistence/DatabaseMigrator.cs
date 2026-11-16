using Dapper;
using Npgsql;

namespace AxonHub.Persistence;

/// <summary>
/// Applies the ported schema. The reference runs Ent's auto-migration on every
/// startup, diffing the live database against the generated schema. The port keeps
/// the same idempotent, startup-time behaviour with <c>CREATE ... IF NOT EXISTS</c>
/// and adds no bookkeeping table, so the database stays structurally identical to
/// the reference's.
/// </summary>
public static class DatabaseMigrator
{
    public static async Task EnsureCreatedAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(681524900)",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            DatabaseSchema.Initial,
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
