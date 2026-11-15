using System.Data.Common;
using Dapper;

namespace AxonHub.Persistence;

/// <summary>
/// Creates the Orleans AdoNet membership and reminder tables used when
/// <c>OrleansCluster:Enabled</c> is on. The SQL is the Orleans
/// PostgreSQL AdoNet script (Apache-2.0) with idempotent
/// <c>CREATE TABLE IF NOT EXISTS</c>, <c>CREATE OR REPLACE FUNCTION</c> and
/// <c>ON CONFLICT</c> query upserts, so every replica can run it at startup.
/// </summary>
public static class OrleansSchema
{
    private const string ResourceName =
        "AxonHub.Persistence.Sql.Orleans-PostgreSQL.sql";

    /// <summary>
    /// Applies the shared schema in one transaction. Concurrent replicas may
    /// run this at the same time; PostgreSQL DDL serializes on the affected
    /// catalog objects and the query upserts converge.
    /// </summary>
    public static async Task EnsureCreatedAsync(
        DbConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Replicas start simultaneously. Serialize the idempotent DDL so
        // concurrent CREATE TABLE statements cannot race on the catalog.
        await connection.ExecuteAsync(new CommandDefinition(
            "SELECT pg_advisory_xact_lock(7364641);",
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            ReadScript(),
            transaction: transaction,
            commandTimeout: 120,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ReadScript()
    {
        var assembly = typeof(OrleansSchema).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                "missing embedded Orleans schema: " + ResourceName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
