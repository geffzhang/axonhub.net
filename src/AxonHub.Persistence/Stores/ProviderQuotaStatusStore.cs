using Dapper;

namespace AxonHub.Persistence;

/// <summary>
/// Persistence seam for the reference <c>provider_quota_status</c> table
/// (internal/ent/schema/provider_quota_status.go).
/// </summary>
public interface IProviderQuotaStatusStore
{
    /// <summary>
    /// Live rows, loaded into the quota service cache at startup
    /// (<c>client.ProviderQuotaStatus.Query().All(ctx)</c>).
    /// </summary>
    Task<IReadOnlyList<ProviderQuotaStatusRecord>> ListAllAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Live row for a channel (<c>ProviderQuotaStatus.Query().Where(ChannelIDEQ(id))</c>);
    /// the GraphQL <c>Channel.providerQuotaStatus</c> field reads this row.
    /// </summary>
    Task<ProviderQuotaStatusRecord?> GetByChannelIdAsync(
        long channelId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the collection loop's upsert:
    /// <c>Create()...OnConflict(sql.ConflictColumns("channel_id")).UpdateNewValues()</c>.
    /// Ent's insert spec carries only the explicitly set fields, so the conflict
    /// update rewrites every inserted column and leaves <c>created_at</c>,
    /// <c>updated_at</c> and <c>deleted_at</c> untouched. When the quota data has
    /// no reset time the reference adds <c>ClearNextResetAt()</c>, which this
    /// renders as <c>next_reset_at = NULL</c>.
    /// </summary>
    Task UpsertAsync(ProviderQuotaStatusRecord status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the error path's plain
    /// <c>ProviderQuotaStatus.Create()...Exec(ctx)</c>: unlike
    /// <see cref="UpsertAsync"/>, a live row with the same channel id makes the
    /// insert fail (the reference relies on the unique edge constraint).
    /// </summary>
    Task CreateAsync(ProviderQuotaStatusRecord status, CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the provider-change / invalid-credentials error update:
    /// <c>UpdateOne(existing).SetProviderType(pt).SetAccountKey(...).
    /// SetStatus(unknown).SetReady(false).SetQuotaData(data).ClearNextResetAt().
    /// SetNextCheckAt(...)</c>. TimeMixin's <c>UpdateDefault(xtime.UTCNow)</c>
    /// also refreshes <c>updated_at</c>.
    /// </summary>
    Task ResetForProviderErrorAsync(
        long id,
        string providerType,
        string? accountKey,
        string quotaData,
        DateTime nextCheckAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the ordinary error update:
    /// <c>UpdateOne(existing).SetAccountKey(...).SetQuotaData(merged).
    /// SetNextCheckAt(...)</c> with TimeMixin refreshing <c>updated_at</c>.
    /// </summary>
    Task UpdateErrorAsync(
        long id,
        string? accountKey,
        string quotaData,
        DateTime nextCheckAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>ProviderQuotaStatus.Delete().
    /// Where(providerquotastatus.ChannelIDEQ(channelID)).Exec(SkipSoftDelete(ctx))</c>:
    /// a hard delete that also removes a soft-deleted row.
    /// </summary>
    Task<int> HardDeleteByChannelIdAsync(
        long channelId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>provider_quota_status</c> table. The collection
/// loop's upsert keeps Ent's conflict semantics, so all statements here are
/// hand-written Dapper SQL; reads honour the SoftDeleteMixin predicate
/// (<c>deleted_at = 0</c>).
/// </summary>
public sealed class ProviderQuotaStatusStore(AxonHubDbContext db) : IProviderQuotaStatusStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "provider_type",
        "status", "quota_data", "next_reset_at", "ready", "next_check_at",
        "account_key", "channel_id"
        """;

    public async Task<IReadOnlyList<ProviderQuotaStatusRecord>> ListAllAsync(
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"provider_quota_status\" WHERE \"deleted_at\" = 0 ORDER BY \"id\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ProviderQuotaStatusRecord>(
            new CommandDefinition(
                sql,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<ProviderQuotaStatusRecord?> GetByChannelIdAsync(
        long channelId,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"provider_quota_status\""
            + " WHERE \"channel_id\" = @ChannelId AND \"deleted_at\" = 0"
            + " ORDER BY \"id\" LIMIT 1;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ProviderQuotaStatusRecord>(
            new CommandDefinition(
                sql,
                new { ChannelId = channelId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task UpsertAsync(
        ProviderQuotaStatusRecord status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);

        // Ent omits created_at/updated_at/deleted_at from the insert spec (their
        // DB defaults apply) and UpdateNewValues rewrites exactly the inserted
        // columns on conflict.
        const string sql = """
            INSERT INTO "provider_quota_status"
                ("provider_type", "status", "quota_data", "next_reset_at", "ready",
                 "next_check_at", "account_key", "channel_id")
            VALUES
                (@ProviderType, @Status, CAST(@QuotaData AS jsonb), @NextResetAt, @Ready,
                 @NextCheckAt, @AccountKey, @ChannelId)
            ON CONFLICT ("channel_id") DO UPDATE SET
                "provider_type" = EXCLUDED."provider_type",
                "status" = EXCLUDED."status",
                "quota_data" = EXCLUDED."quota_data",
                "next_reset_at" = EXCLUDED."next_reset_at",
                "ready" = EXCLUDED."ready",
                "next_check_at" = EXCLUDED."next_check_at",
                "account_key" = EXCLUDED."account_key";
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                    status.ProviderType,
                    status.Status,
                    status.QuotaData,
                    status.NextResetAt,
                    status.Ready,
                    status.NextCheckAt,
                    status.AccountKey,
                    status.ChannelId,
                },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task CreateAsync(
        ProviderQuotaStatusRecord status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);

        const string sql = """
            INSERT INTO "provider_quota_status"
                ("provider_type", "status", "quota_data", "next_reset_at", "ready",
                 "next_check_at", "account_key", "channel_id")
            VALUES
                (@ProviderType, @Status, CAST(@QuotaData AS jsonb), @NextResetAt, @Ready,
                 @NextCheckAt, @AccountKey, @ChannelId);
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                    status.ProviderType,
                    status.Status,
                    status.QuotaData,
                    status.NextResetAt,
                    status.Ready,
                    status.NextCheckAt,
                    status.AccountKey,
                    status.ChannelId,
                },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task ResetForProviderErrorAsync(
        long id,
        string providerType,
        string? accountKey,
        string quotaData,
        DateTime nextCheckAt,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE "provider_quota_status" SET
                "updated_at" = @Now,
                "provider_type" = @ProviderType,
                "account_key" = @AccountKey,
                "status" = 'unknown',
                "ready" = false,
                "quota_data" = CAST(@QuotaData AS jsonb),
                "next_reset_at" = NULL,
                "next_check_at" = @NextCheckAt
            WHERE "id" = @Id;
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                    Id = id,
                    Now = DateTime.UtcNow,
                    ProviderType = providerType,
                    AccountKey = accountKey,
                    QuotaData = quotaData,
                    NextCheckAt = nextCheckAt,
                },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task UpdateErrorAsync(
        long id,
        string? accountKey,
        string quotaData,
        DateTime nextCheckAt,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            UPDATE "provider_quota_status" SET
                "updated_at" = @Now,
                "account_key" = @AccountKey,
                "quota_data" = CAST(@QuotaData AS jsonb),
                "next_check_at" = @NextCheckAt
            WHERE "id" = @Id;
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new
                {
                    Id = id,
                    Now = DateTime.UtcNow,
                    AccountKey = accountKey,
                    QuotaData = quotaData,
                    NextCheckAt = nextCheckAt,
                },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> HardDeleteByChannelIdAsync(
        long channelId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            DELETE FROM "provider_quota_status" WHERE "channel_id" = @ChannelId;
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql,
                new { ChannelId = channelId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
