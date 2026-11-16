using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;

namespace AxonHub.Persistence;

/// <summary>Shared, expiring OIDC bytes addressed by opaque SHA-256 lookup keys.</summary>
public sealed class PostgresOidcStateStore(PostgresConnectionFactory connections)
{
    /// <summary>Creates the shared state table before any replica begins serving logins.</summary>
    public static async Task EnsureCreatedAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            SELECT pg_advisory_xact_lock(681524903);
            CREATE TABLE IF NOT EXISTS oidc_state_entries (
                lookup_key bytea PRIMARY KEY CHECK (octet_length(lookup_key) = 32),
                value bytea NOT NULL,
                expires_at timestamptz NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_oidc_state_entries_expires_at
                ON oidc_state_entries (expires_at);
            """,
            transaction: transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<byte[]?>(new CommandDefinition(
            """
            SELECT value FROM oidc_state_entries
            WHERE lookup_key = @Key AND expires_at >= statement_timestamp();
            """,
            new { Key = HashKey(key) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>DELETE RETURNING gives all replicas exactly one successful consumer.</summary>
    public async Task<byte[]?> ConsumeAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<byte[]?>(new CommandDefinition(
            """
            DELETE FROM oidc_state_entries WHERE lookup_key = @Key
            RETURNING CASE WHEN expires_at >= statement_timestamp() THEN value END;
            """,
            new { Key = HashKey(key) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task SetAsync(
        string key,
        byte[] value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM oidc_state_entries WHERE lookup_key IN (
                SELECT lookup_key FROM oidc_state_entries
                WHERE expires_at < statement_timestamp()
                ORDER BY expires_at LIMIT 100 FOR UPDATE SKIP LOCKED
            );
            INSERT INTO oidc_state_entries (lookup_key, value, expires_at)
            VALUES (@Key, @Value, statement_timestamp() + @Expiration)
            ON CONFLICT (lookup_key) DO UPDATE
            SET value = EXCLUDED.value, expires_at = EXCLUDED.expires_at;
            """,
            new { Key = HashKey(key), Value = value, Expiration = expiration },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var connection = await connections.OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM oidc_state_entries WHERE lookup_key = @Key;",
            new { Key = HashKey(key) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static byte[] HashKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }
}
