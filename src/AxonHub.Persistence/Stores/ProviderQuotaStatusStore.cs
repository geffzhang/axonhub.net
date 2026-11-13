using Dapper;
using static AxonHub.Persistence.ConnectionSql;

namespace AxonHub.Persistence;

/// <summary>
/// Port of the scalar and edge predicates of
/// ent.ProviderQuotaStatusWhereInput (ent.graphql). The filter itself never
/// restricts <c>deleted_at</c>, matching the generated sqlgraph predicates used
/// inside a nested edge filter.
/// </summary>
public sealed record ProviderQuotaStatusListFilter
{
    public long? Id { get; init; }

    public long? IdNEQ { get; init; }

    public IReadOnlyList<long>? IdIn { get; init; }

    public IReadOnlyList<long>? IdNotIn { get; init; }

    public long? IdGT { get; init; }

    public long? IdGTE { get; init; }

    public long? IdLT { get; init; }

    public long? IdLTE { get; init; }

    public DateTime? CreatedAt { get; init; }

    public DateTime? CreatedAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? CreatedAtIn { get; init; }

    public IReadOnlyList<DateTime>? CreatedAtNotIn { get; init; }

    public DateTime? CreatedAtGT { get; init; }

    public DateTime? CreatedAtGTE { get; init; }

    public DateTime? CreatedAtLT { get; init; }

    public DateTime? CreatedAtLTE { get; init; }

    public DateTime? UpdatedAt { get; init; }

    public DateTime? UpdatedAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? UpdatedAtIn { get; init; }

    public IReadOnlyList<DateTime>? UpdatedAtNotIn { get; init; }

    public DateTime? UpdatedAtGT { get; init; }

    public DateTime? UpdatedAtGTE { get; init; }

    public DateTime? UpdatedAtLT { get; init; }

    public DateTime? UpdatedAtLTE { get; init; }

    public long? ChannelId { get; init; }

    public long? ChannelIdNEQ { get; init; }

    public IReadOnlyList<long>? ChannelIdIn { get; init; }

    public IReadOnlyList<long>? ChannelIdNotIn { get; init; }

    public string? ProviderType { get; init; }

    public string? ProviderTypeNEQ { get; init; }

    public IReadOnlyList<string>? ProviderTypeIn { get; init; }

    public IReadOnlyList<string>? ProviderTypeNotIn { get; init; }

    public string? Status { get; init; }

    public string? StatusNEQ { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    public IReadOnlyList<string>? StatusNotIn { get; init; }

    public DateTime? NextResetAt { get; init; }

    public DateTime? NextResetAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? NextResetAtIn { get; init; }

    public IReadOnlyList<DateTime>? NextResetAtNotIn { get; init; }

    public DateTime? NextResetAtGT { get; init; }

    public DateTime? NextResetAtGTE { get; init; }

    public DateTime? NextResetAtLT { get; init; }

    public DateTime? NextResetAtLTE { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool NextResetAtIsNil { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool NextResetAtNotNil { get; init; }

    public bool? Ready { get; init; }

    public bool? ReadyNEQ { get; init; }

    public DateTime? NextCheckAt { get; init; }

    public DateTime? NextCheckAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? NextCheckAtIn { get; init; }

    public IReadOnlyList<DateTime>? NextCheckAtNotIn { get; init; }

    public DateTime? NextCheckAtGT { get; init; }

    public DateTime? NextCheckAtGTE { get; init; }

    public DateTime? NextCheckAtLT { get; init; }

    public DateTime? NextCheckAtLTE { get; init; }

    public string? AccountKey { get; init; }

    public string? AccountKeyNEQ { get; init; }

    public IReadOnlyList<string>? AccountKeyIn { get; init; }

    public IReadOnlyList<string>? AccountKeyNotIn { get; init; }

    public string? AccountKeyGT { get; init; }

    public string? AccountKeyGTE { get; init; }

    public string? AccountKeyLT { get; init; }

    public string? AccountKeyLTE { get; init; }

    public string? AccountKeyContains { get; init; }

    public string? AccountKeyHasPrefix { get; init; }

    public string? AccountKeyHasSuffix { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool AccountKeyIsNil { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool AccountKeyNotNil { get; init; }

    public string? AccountKeyEqualFold { get; init; }

    public string? AccountKeyContainsFold { get; init; }

    /// <summary>
    /// Ent's <c>hasChannel</c>: true → an EXISTS over <c>channels</c>, false →
    /// NOT EXISTS. Generated sqlgraph does not re-apply the Channel soft-delete
    /// predicate.
    /// </summary>
    public bool? HasChannel { get; init; }

    public IReadOnlyList<ChannelListFilter>? HasChannelWith { get; init; }

    public ProviderQuotaStatusListFilter? Not { get; init; }

    public IReadOnlyList<ProviderQuotaStatusListFilter>? And { get; init; }

    public IReadOnlyList<ProviderQuotaStatusListFilter>? Or { get; init; }
}

/// <summary>SQL fragments for ent.ProviderQuotaStatusWhereInput.</summary>
internal static class ProviderQuotaStatusFilterSql
{
    internal static string? Build(
        ProviderQuotaStatusListFilter? filter,
        string alias,
        SqlArguments args)
    {
        if (filter is null)
        {
            return null;
        }

        var predicates = new List<string?>();

        AppendComparison(predicates, alias, "id", filter.Id, "=", args);
        AppendComparison(predicates, alias, "id", filter.IdNEQ, "<>", args);
        if (filter.IdIn is not null)
        {
            AppendIdIn(predicates, alias, "id", filter.IdIn, args);
        }

        if (filter.IdNotIn is not null)
        {
            AppendIdIn(predicates, alias, "id", filter.IdNotIn, args, negate: true);
        }

        AppendComparison(predicates, alias, "id", filter.IdGT, ">", args);
        AppendComparison(predicates, alias, "id", filter.IdGTE, ">=", args);
        AppendComparison(predicates, alias, "id", filter.IdLT, "<", args);
        AppendComparison(predicates, alias, "id", filter.IdLTE, "<=", args);

        AppendComparison(predicates, alias, "created_at", filter.CreatedAt, "=", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtNEQ, "<>", args);
        AppendTimeIn(predicates, alias, "created_at", filter.CreatedAtIn, args);
        AppendTimeIn(predicates, alias, "created_at", filter.CreatedAtNotIn, args, negate: true);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtGT, ">", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtGTE, ">=", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtLT, "<", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtLTE, "<=", args);

        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAt, "=", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtNEQ, "<>", args);
        AppendTimeIn(predicates, alias, "updated_at", filter.UpdatedAtIn, args);
        AppendTimeIn(predicates, alias, "updated_at", filter.UpdatedAtNotIn, args, negate: true);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtGT, ">", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtGTE, ">=", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtLT, "<", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtLTE, "<=", args);

        AppendComparison(predicates, alias, "channel_id", filter.ChannelId, "=", args);
        AppendComparison(predicates, alias, "channel_id", filter.ChannelIdNEQ, "<>", args);
        if (filter.ChannelIdIn is not null)
        {
            AppendIdIn(predicates, alias, "channel_id", filter.ChannelIdIn, args);
        }

        if (filter.ChannelIdNotIn is not null)
        {
            AppendIdIn(predicates, alias, "channel_id", filter.ChannelIdNotIn, args, negate: true);
        }

        AppendComparison(predicates, alias, "provider_type", filter.ProviderType, "=", args);
        AppendComparison(predicates, alias, "provider_type", filter.ProviderTypeNEQ, "<>", args);
        if (filter.ProviderTypeIn is not null)
        {
            AppendStringIn(predicates, alias, "provider_type", filter.ProviderTypeIn, args);
        }

        if (filter.ProviderTypeNotIn is not null)
        {
            AppendStringIn(predicates, alias, "provider_type", filter.ProviderTypeNotIn, args, negate: true);
        }

        AppendComparison(predicates, alias, "status", filter.Status, "=", args);
        AppendComparison(predicates, alias, "status", filter.StatusNEQ, "<>", args);
        if (filter.StatusIn is not null)
        {
            AppendStringIn(predicates, alias, "status", filter.StatusIn, args);
        }

        if (filter.StatusNotIn is not null)
        {
            AppendStringIn(predicates, alias, "status", filter.StatusNotIn, args, negate: true);
        }

        AppendComparison(predicates, alias, "next_reset_at", filter.NextResetAt, "=", args);
        AppendComparison(predicates, alias, "next_reset_at", filter.NextResetAtNEQ, "<>", args);
        AppendTimeIn(predicates, alias, "next_reset_at", filter.NextResetAtIn, args);
        AppendTimeIn(predicates, alias, "next_reset_at", filter.NextResetAtNotIn, args, negate: true);
        AppendComparison(predicates, alias, "next_reset_at", filter.NextResetAtGT, ">", args);
        AppendComparison(predicates, alias, "next_reset_at", filter.NextResetAtGTE, ">=", args);
        AppendComparison(predicates, alias, "next_reset_at", filter.NextResetAtLT, "<", args);
        AppendComparison(predicates, alias, "next_reset_at", filter.NextResetAtLTE, "<=", args);
        if (filter.NextResetAtIsNil)
        {
            predicates.Add(alias + ".\"next_reset_at\" IS NULL");
        }

        if (filter.NextResetAtNotNil)
        {
            predicates.Add(alias + ".\"next_reset_at\" IS NOT NULL");
        }

        AppendComparison(predicates, alias, "ready", filter.Ready, "=", args);
        AppendComparison(predicates, alias, "ready", filter.ReadyNEQ, "<>", args);

        AppendComparison(predicates, alias, "next_check_at", filter.NextCheckAt, "=", args);
        AppendComparison(predicates, alias, "next_check_at", filter.NextCheckAtNEQ, "<>", args);
        AppendTimeIn(predicates, alias, "next_check_at", filter.NextCheckAtIn, args);
        AppendTimeIn(predicates, alias, "next_check_at", filter.NextCheckAtNotIn, args, negate: true);
        AppendComparison(predicates, alias, "next_check_at", filter.NextCheckAtGT, ">", args);
        AppendComparison(predicates, alias, "next_check_at", filter.NextCheckAtGTE, ">=", args);
        AppendComparison(predicates, alias, "next_check_at", filter.NextCheckAtLT, "<", args);
        AppendComparison(predicates, alias, "next_check_at", filter.NextCheckAtLTE, "<=", args);

        AppendStringPredicates(
            predicates,
            alias,
            "account_key",
            filter.AccountKey,
            filter.AccountKeyNEQ,
            filter.AccountKeyIn,
            filter.AccountKeyNotIn,
            filter.AccountKeyGT,
            filter.AccountKeyGTE,
            filter.AccountKeyLT,
            filter.AccountKeyLTE,
            filter.AccountKeyContains,
            filter.AccountKeyHasPrefix,
            filter.AccountKeyHasSuffix,
            filter.AccountKeyEqualFold,
            filter.AccountKeyContainsFold,
            args);
        if (filter.AccountKeyIsNil)
        {
            predicates.Add(alias + ".\"account_key\" IS NULL");
        }

        if (filter.AccountKeyNotNil)
        {
            predicates.Add(alias + ".\"account_key\" IS NOT NULL");
        }

        if (filter.HasChannel is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"channels\" qch WHERE qch.\"id\" = "
                + alias + ".\"channel_id\")";
            predicates.Add(filter.HasChannel.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasChannelWith is { Count: > 0 } channelFilters)
        {
            var channelPredicates = channelFilters
                .Select(channel => ChannelAdminStore.BuildFilter(channel, "qchf", args))
                .Where(static channel => channel is not null)
                .ToArray();
            var channelCondition = Combine(channelPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"channels\" qchf WHERE qchf.\"id\" = "
                + alias + ".\"channel_id\""
                + (channelCondition is null ? string.Empty : " AND " + channelCondition)
                + ")");
        }

        if (filter.Not is not null)
        {
            var not = Build(filter.Not, alias, args);
            if (not is not null)
            {
                predicates.Add("NOT (" + not + ")");
            }
        }

        if (filter.And is not null)
        {
            var and = filter.And
                .Select(nested => Build(nested, alias, args))
                .Where(static nested => nested is not null)
                .ToArray();
            if (and.Length > 0)
            {
                predicates.Add("(" + string.Join(" AND ", and) + ")");
            }
        }

        if (filter.Or is not null)
        {
            var or = filter.Or
                .Select(nested => Build(nested, alias, args))
                .Where(static nested => nested is not null)
                .ToArray();
            predicates.Add(or.Length == 0 ? "FALSE" : "(" + string.Join(" OR ", or) + ")");
        }

        return Combine(predicates);
    }
}

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
