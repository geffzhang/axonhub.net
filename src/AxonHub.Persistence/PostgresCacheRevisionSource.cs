using AxonHub.Abstractions;
using Dapper;
using Npgsql;

namespace AxonHub.Persistence;

public sealed class PostgresCacheRevisionSource(PostgresConnectionFactory factory) : ICacheRevisionSource
{
    public async Task<long> GetRevisionAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleAsync<long>(new CommandDefinition(
            "SELECT revision FROM axonhub_cache_revision WHERE id = 1",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public static async Task EnsureCreatedAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            SELECT pg_advisory_xact_lock(681524901);
            CREATE TABLE IF NOT EXISTS axonhub_cache_revision (
                id integer PRIMARY KEY CHECK (id = 1),
                revision bigint NOT NULL
            );
            INSERT INTO axonhub_cache_revision VALUES (1, 0) ON CONFLICT DO NOTHING;
            CREATE OR REPLACE FUNCTION axonhub_bump_cache_revision() RETURNS trigger
            LANGUAGE plpgsql AS $$
            BEGIN
                UPDATE axonhub_cache_revision SET revision = revision + 1 WHERE id = 1;
                RETURN NULL;
            END;
            $$;
            DROP TRIGGER IF EXISTS axonhub_cache_changed ON channels;
            CREATE TRIGGER axonhub_cache_changed
                AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON channels
                FOR EACH STATEMENT EXECUTE FUNCTION axonhub_bump_cache_revision();
            DROP TRIGGER IF EXISTS axonhub_cache_changed ON channel_model_prices;
            CREATE TRIGGER axonhub_cache_changed
                AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON channel_model_prices
                FOR EACH STATEMENT EXECUTE FUNCTION axonhub_bump_cache_revision();
            DROP TRIGGER IF EXISTS axonhub_cache_changed ON models;
            CREATE TRIGGER axonhub_cache_changed
                AFTER INSERT OR UPDATE OR DELETE OR TRUNCATE ON models
                FOR EACH STATEMENT EXECUTE FUNCTION axonhub_bump_cache_revision();
            """, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
