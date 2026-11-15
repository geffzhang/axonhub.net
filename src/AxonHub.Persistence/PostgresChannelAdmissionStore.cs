using Dapper;
using Npgsql;

namespace AxonHub.Persistence;

public enum AdmissionState
{
    Waiting,
    Granted,
    Full,
    Missing,
}

public sealed record ChannelAdmissionRecord
{
    public Guid Id { get; init; }
    public long ChannelId { get; init; }
    public string Owner { get; init; } = string.Empty;
    public bool Granted { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record ChannelAdmissionStats
{
    public long ChannelId { get; init; }
    public string ChannelName { get; init; } = string.Empty;
    public int InFlight { get; init; }
    public int Waiting { get; init; }
}

public sealed class PostgresChannelAdmissionStore(PostgresConnectionFactory factory)
{
    public static async Task EnsureCreatedAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            SELECT pg_advisory_xact_lock(681524902);
            CREATE TABLE IF NOT EXISTS axonhub_channel_gates (
                channel_id bigint PRIMARY KEY
            );
            CREATE TABLE IF NOT EXISTS axonhub_channel_admissions (
                id uuid PRIMARY KEY,
                channel_id bigint NOT NULL,
                ticket bigint GENERATED ALWAYS AS IDENTITY,
                owner text NOT NULL,
                granted boolean NOT NULL DEFAULT false,
                created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
                touched_at timestamptz NOT NULL DEFAULT clock_timestamp()
            );
            CREATE INDEX IF NOT EXISTS axonhub_admissions_channel_ticket
                ON axonhub_channel_admissions(channel_id, ticket);
            """, transaction: transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AdmissionState> JoinAsync(
        Guid id,
        long channelId,
        string owner,
        long capacity,
        long queueSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await LockAsync(connection, transaction, channelId, cancellationToken).ConfigureAwait(false);
        capacity = await ReadCapacityAsync(connection, transaction, channelId, capacity, cancellationToken)
            .ConfigureAwait(false);
        var existing = await connection.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
            "SELECT granted FROM axonhub_channel_admissions WHERE id = @id AND channel_id = @channelId",
            new { id, channelId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (existing is { } granted)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return granted ? AdmissionState.Granted : AdmissionState.Waiting;
        }

        var active = await CountAsync(connection, transaction, channelId, true, cancellationToken)
            .ConfigureAwait(false);
        var waiting = await CountAsync(connection, transaction, channelId, false, cancellationToken)
            .ConfigureAwait(false);
        var admit = active < capacity && waiting == 0;
        if (!admit && queueSize > 0 && waiting >= queueSize)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AdmissionState.Full;
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO axonhub_channel_admissions(id, channel_id, owner, granted)
            VALUES (@id, @channelId, @owner, @admit)
            """, new { id, channelId, owner, admit }, transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return admit ? AdmissionState.Granted : AdmissionState.Waiting;
    }

    public async Task<AdmissionState> PollAsync(
        Guid id,
        long channelId,
        long capacity,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await LockAsync(connection, transaction, channelId, cancellationToken).ConfigureAwait(false);
        capacity = await ReadCapacityAsync(connection, transaction, channelId, capacity, cancellationToken)
            .ConfigureAwait(false);
        var granted = await connection.QuerySingleOrDefaultAsync<bool?>(new CommandDefinition(
            "SELECT granted FROM axonhub_channel_admissions WHERE id = @id AND channel_id = @channelId",
            new { id, channelId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (granted is null)
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return AdmissionState.Missing;
        }

        if (!granted.Value)
        {
            var head = await connection.QuerySingleAsync<Guid>(new CommandDefinition("""
                SELECT id FROM axonhub_channel_admissions
                WHERE channel_id = @channelId AND NOT granted ORDER BY ticket LIMIT 1
                """, new { channelId }, transaction, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            var active = await CountAsync(connection, transaction, channelId, true, cancellationToken)
                .ConfigureAwait(false);
            granted = head == id && active < capacity;
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE axonhub_channel_admissions
                SET touched_at = clock_timestamp(), granted = @granted WHERE id = @id
                """, new { id, granted }, transaction, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return granted.Value ? AdmissionState.Granted : AdmissionState.Waiting;
    }

    public Task ReleaseAsync(Guid id, long channelId, CancellationToken cancellationToken = default)
        => RemoveAsync(id, channelId, waitingOnly: false, cancellationToken);

    public Task CancelWaitingAsync(Guid id, long channelId, CancellationToken cancellationToken = default)
        => RemoveAsync(id, channelId, waitingOnly: true, cancellationToken);

    public async Task<IReadOnlyList<ChannelAdmissionRecord>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<ChannelAdmissionRecord>(new CommandDefinition("""
            SELECT id, channel_id, owner, granted, created_at
            FROM axonhub_channel_admissions ORDER BY channel_id, ticket
            """, cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    public async Task<IReadOnlyList<ChannelAdmissionStats>> GetStatsAsync(
        long? channelId = null, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<ChannelAdmissionStats>(new CommandDefinition("""
            SELECT a.channel_id, COALESCE(c.name, '') AS channel_name,
                COUNT(*) FILTER (WHERE a.granted)::int AS in_flight,
                COUNT(*) FILTER (WHERE NOT a.granted)::int AS waiting
            FROM axonhub_channel_admissions a LEFT JOIN channels c ON c.id = a.channel_id
            WHERE (@channelId::bigint IS NULL OR a.channel_id = @channelId)
                AND (a.granted OR a.touched_at >= clock_timestamp() - interval '30 seconds')
            GROUP BY a.channel_id, c.name
            """, new { channelId }, cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    private async Task RemoveAsync(
        Guid id, long channelId, bool waitingOnly, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await LockAsync(connection, transaction, channelId, cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM axonhub_channel_admissions
            WHERE id = @id AND channel_id = @channelId AND (NOT @waitingOnly OR NOT granted)
            """, new { id, channelId, waitingOnly }, transaction, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> ReadCapacityAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long channelId,
        long fallback, CancellationToken cancellationToken)
    {
        var current = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition("""
            SELECT COALESCE((settings #>> '{rateLimit,maxConcurrent}')::bigint, 0)
            FROM channels WHERE id = @channelId AND deleted_at = 0
            """, new { channelId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return current is null ? fallback : current.Value > 0 ? current.Value : long.MaxValue;
    }

    private static async Task LockAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long channelId,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO axonhub_channel_gates(channel_id) VALUES (@channelId) ON CONFLICT DO NOTHING;
            SELECT channel_id FROM axonhub_channel_gates WHERE channel_id = @channelId FOR UPDATE;
            DELETE FROM axonhub_channel_admissions
            WHERE channel_id = @channelId AND NOT granted
                AND touched_at < clock_timestamp() - interval '30 seconds';
            """, new { channelId }, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static Task<long> CountAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long channelId,
        bool granted, CancellationToken cancellationToken)
        => connection.QuerySingleAsync<long>(new CommandDefinition("""
            SELECT COUNT(*) FROM axonhub_channel_admissions
            WHERE channel_id = @channelId AND granted = @granted
            """, new { channelId, granted }, transaction, cancellationToken: cancellationToken));
}
