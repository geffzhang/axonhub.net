using Dapper;
using static AxonHub.Persistence.ConnectionSql;

namespace AxonHub.Persistence;

/// <summary>
/// One grouped bucket of the probe-stats query
/// (qb.BuildProbeStatsQuery, throughput.go:317-368). Every metric is a SQL
/// aggregate; <see cref="EffectiveLatencyMs"/> is nullable because a completed
/// execution without a latency sample contributes NULL to that SUM, which the
/// reference's <c>rows.Scan</c> into <c>int64</c> rejects (C249).
/// </summary>
public sealed class ChannelProbeStatsRow
{
    public long ChannelId { get; init; }

    public long TotalCount { get; init; }

    public long SuccessCount { get; init; }

    public long TotalTokens { get; init; }

    /// <summary>
    /// Sum of effective latency for completed attempts. NULL when every
    /// completed attempt carried a NULL <c>metrics_latency_ms</c>.
    /// </summary>
    public long? EffectiveLatencyMs { get; init; }

    public long TotalFirstTokenLatency { get; init; }

    public long RequestCount { get; init; }

    public long StreamingRequestCount { get; init; }
}

/// <summary>
/// Port of the scalar and edge predicates of ent.ChannelProbeWhereInput
/// (ent.graphql). The table has no mixins, so the predicate never restricts
/// soft deletion.
/// </summary>
public sealed record ChannelProbeListFilter
{
    public long? Id { get; init; }

    public long? IdNEQ { get; init; }

    public IReadOnlyList<long>? IdIn { get; init; }

    public IReadOnlyList<long>? IdNotIn { get; init; }

    public long? IdGT { get; init; }

    public long? IdGTE { get; init; }

    public long? IdLT { get; init; }

    public long? IdLTE { get; init; }

    public long? ChannelId { get; init; }

    public long? ChannelIdNEQ { get; init; }

    public IReadOnlyList<long>? ChannelIdIn { get; init; }

    public IReadOnlyList<long>? ChannelIdNotIn { get; init; }

    public long? TotalRequestCount { get; init; }

    public long? TotalRequestCountNEQ { get; init; }

    public IReadOnlyList<long>? TotalRequestCountIn { get; init; }

    public IReadOnlyList<long>? TotalRequestCountNotIn { get; init; }

    public long? TotalRequestCountGT { get; init; }

    public long? TotalRequestCountGTE { get; init; }

    public long? TotalRequestCountLT { get; init; }

    public long? TotalRequestCountLTE { get; init; }

    public long? SuccessRequestCount { get; init; }

    public long? SuccessRequestCountNEQ { get; init; }

    public IReadOnlyList<long>? SuccessRequestCountIn { get; init; }

    public IReadOnlyList<long>? SuccessRequestCountNotIn { get; init; }

    public long? SuccessRequestCountGT { get; init; }

    public long? SuccessRequestCountGTE { get; init; }

    public long? SuccessRequestCountLT { get; init; }

    public long? SuccessRequestCountLTE { get; init; }

    public double? AvgTokensPerSecond { get; init; }

    public double? AvgTokensPerSecondNEQ { get; init; }

    public IReadOnlyList<double>? AvgTokensPerSecondIn { get; init; }

    public IReadOnlyList<double>? AvgTokensPerSecondNotIn { get; init; }

    public double? AvgTokensPerSecondGT { get; init; }

    public double? AvgTokensPerSecondGTE { get; init; }

    public double? AvgTokensPerSecondLT { get; init; }

    public double? AvgTokensPerSecondLTE { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool AvgTokensPerSecondIsNil { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool AvgTokensPerSecondNotNil { get; init; }

    public double? AvgTimeToFirstTokenMs { get; init; }

    public double? AvgTimeToFirstTokenMsNEQ { get; init; }

    public IReadOnlyList<double>? AvgTimeToFirstTokenMsIn { get; init; }

    public IReadOnlyList<double>? AvgTimeToFirstTokenMsNotIn { get; init; }

    public double? AvgTimeToFirstTokenMsGT { get; init; }

    public double? AvgTimeToFirstTokenMsGTE { get; init; }

    public double? AvgTimeToFirstTokenMsLT { get; init; }

    public double? AvgTimeToFirstTokenMsLTE { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool AvgTimeToFirstTokenMsIsNil { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool AvgTimeToFirstTokenMsNotNil { get; init; }

    public long? Timestamp { get; init; }

    public long? TimestampNEQ { get; init; }

    public IReadOnlyList<long>? TimestampIn { get; init; }

    public IReadOnlyList<long>? TimestampNotIn { get; init; }

    public long? TimestampGT { get; init; }

    public long? TimestampGTE { get; init; }

    public long? TimestampLT { get; init; }

    public long? TimestampLTE { get; init; }

    /// <summary>
    /// Ent's <c>hasChannel</c>: true → an EXISTS over <c>channels</c>, false →
    /// NOT EXISTS. Generated sqlgraph does not re-apply the Channel soft-delete
    /// predicate.
    /// </summary>
    public bool? HasChannel { get; init; }

    public IReadOnlyList<ChannelListFilter>? HasChannelWith { get; init; }

    public ChannelProbeListFilter? Not { get; init; }

    public IReadOnlyList<ChannelProbeListFilter>? And { get; init; }

    public IReadOnlyList<ChannelProbeListFilter>? Or { get; init; }
}

/// <summary>
/// SQL fragments for ent.ChannelProbeWhereInput. Mirrors sqlgraph's
/// HasNeighbors / HasNeighborsWith shape: one EXISTS per edge predicate group.
/// </summary>
internal static class ChannelProbeFilterSql
{
    internal static string? Build(
        ChannelProbeListFilter? filter,
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

        AppendComparison(predicates, alias, "total_request_count", filter.TotalRequestCount, "=", args);
        AppendComparison(predicates, alias, "total_request_count", filter.TotalRequestCountNEQ, "<>", args);
        if (filter.TotalRequestCountIn is not null)
        {
            AppendIdIn(predicates, alias, "total_request_count", filter.TotalRequestCountIn, args);
        }

        if (filter.TotalRequestCountNotIn is not null)
        {
            AppendIdIn(
                predicates,
                alias,
                "total_request_count",
                filter.TotalRequestCountNotIn,
                args,
                negate: true);
        }

        AppendComparison(predicates, alias, "total_request_count", filter.TotalRequestCountGT, ">", args);
        AppendComparison(predicates, alias, "total_request_count", filter.TotalRequestCountGTE, ">=", args);
        AppendComparison(predicates, alias, "total_request_count", filter.TotalRequestCountLT, "<", args);
        AppendComparison(predicates, alias, "total_request_count", filter.TotalRequestCountLTE, "<=", args);

        AppendComparison(predicates, alias, "success_request_count", filter.SuccessRequestCount, "=", args);
        AppendComparison(predicates, alias, "success_request_count", filter.SuccessRequestCountNEQ, "<>", args);
        if (filter.SuccessRequestCountIn is not null)
        {
            AppendIdIn(predicates, alias, "success_request_count", filter.SuccessRequestCountIn, args);
        }

        if (filter.SuccessRequestCountNotIn is not null)
        {
            AppendIdIn(
                predicates,
                alias,
                "success_request_count",
                filter.SuccessRequestCountNotIn,
                args,
                negate: true);
        }

        AppendComparison(predicates, alias, "success_request_count", filter.SuccessRequestCountGT, ">", args);
        AppendComparison(predicates, alias, "success_request_count", filter.SuccessRequestCountGTE, ">=", args);
        AppendComparison(predicates, alias, "success_request_count", filter.SuccessRequestCountLT, "<", args);
        AppendComparison(predicates, alias, "success_request_count", filter.SuccessRequestCountLTE, "<=", args);

        AppendComparison(predicates, alias, "avg_tokens_per_second", filter.AvgTokensPerSecond, "=", args);
        AppendComparison(predicates, alias, "avg_tokens_per_second", filter.AvgTokensPerSecondNEQ, "<>", args);
        if (filter.AvgTokensPerSecondIn is not null)
        {
            AppendDoubleIn(predicates, alias, "avg_tokens_per_second", filter.AvgTokensPerSecondIn, args);
        }

        if (filter.AvgTokensPerSecondNotIn is not null)
        {
            AppendDoubleIn(
                predicates,
                alias,
                "avg_tokens_per_second",
                filter.AvgTokensPerSecondNotIn,
                args,
                negate: true);
        }

        AppendComparison(predicates, alias, "avg_tokens_per_second", filter.AvgTokensPerSecondGT, ">", args);
        AppendComparison(predicates, alias, "avg_tokens_per_second", filter.AvgTokensPerSecondGTE, ">=", args);
        AppendComparison(predicates, alias, "avg_tokens_per_second", filter.AvgTokensPerSecondLT, "<", args);
        AppendComparison(predicates, alias, "avg_tokens_per_second", filter.AvgTokensPerSecondLTE, "<=", args);
        if (filter.AvgTokensPerSecondIsNil)
        {
            predicates.Add(alias + ".\"avg_tokens_per_second\" IS NULL");
        }

        if (filter.AvgTokensPerSecondNotNil)
        {
            predicates.Add(alias + ".\"avg_tokens_per_second\" IS NOT NULL");
        }

        AppendComparison(
            predicates,
            alias,
            "avg_time_to_first_token_ms",
            filter.AvgTimeToFirstTokenMs,
            "=",
            args);
        AppendComparison(
            predicates,
            alias,
            "avg_time_to_first_token_ms",
            filter.AvgTimeToFirstTokenMsNEQ,
            "<>",
            args);
        if (filter.AvgTimeToFirstTokenMsIn is not null)
        {
            AppendDoubleIn(
                predicates,
                alias,
                "avg_time_to_first_token_ms",
                filter.AvgTimeToFirstTokenMsIn,
                args);
        }

        if (filter.AvgTimeToFirstTokenMsNotIn is not null)
        {
            AppendDoubleIn(
                predicates,
                alias,
                "avg_time_to_first_token_ms",
                filter.AvgTimeToFirstTokenMsNotIn,
                args,
                negate: true);
        }

        AppendComparison(predicates, alias, "avg_time_to_first_token_ms", filter.AvgTimeToFirstTokenMsGT, ">", args);
        AppendComparison(predicates, alias, "avg_time_to_first_token_ms", filter.AvgTimeToFirstTokenMsGTE, ">=", args);
        AppendComparison(predicates, alias, "avg_time_to_first_token_ms", filter.AvgTimeToFirstTokenMsLT, "<", args);
        AppendComparison(predicates, alias, "avg_time_to_first_token_ms", filter.AvgTimeToFirstTokenMsLTE, "<=", args);
        if (filter.AvgTimeToFirstTokenMsIsNil)
        {
            predicates.Add(alias + ".\"avg_time_to_first_token_ms\" IS NULL");
        }

        if (filter.AvgTimeToFirstTokenMsNotNil)
        {
            predicates.Add(alias + ".\"avg_time_to_first_token_ms\" IS NOT NULL");
        }

        AppendComparison(predicates, alias, "timestamp", filter.Timestamp, "=", args);
        AppendComparison(predicates, alias, "timestamp", filter.TimestampNEQ, "<>", args);
        if (filter.TimestampIn is not null)
        {
            AppendIdIn(predicates, alias, "timestamp", filter.TimestampIn, args);
        }

        if (filter.TimestampNotIn is not null)
        {
            AppendIdIn(predicates, alias, "timestamp", filter.TimestampNotIn, args, negate: true);
        }

        AppendComparison(predicates, alias, "timestamp", filter.TimestampGT, ">", args);
        AppendComparison(predicates, alias, "timestamp", filter.TimestampGTE, ">=", args);
        AppendComparison(predicates, alias, "timestamp", filter.TimestampLT, "<", args);
        AppendComparison(predicates, alias, "timestamp", filter.TimestampLTE, "<=", args);

        if (filter.HasChannel is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"channels\" chp WHERE chp.\"id\" = "
                + alias + ".\"channel_id\")";
            predicates.Add(filter.HasChannel.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasChannelWith is { Count: > 0 } channelFilters)
        {
            var channelPredicates = channelFilters
                .Select(channel => ChannelAdminStore.BuildFilter(channel, "chpc", args))
                .Where(static channel => channel is not null)
                .ToArray();
            var channelCondition = Combine(channelPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"channels\" chpc WHERE chpc.\"id\" = "
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
/// Persistence seam for the reference <c>channel_probes</c> table
/// (internal/server/biz/channel_probe.go): the batch read behind the
/// <c>channelProbeData</c> root, the aggregate scan behind the probe writer
/// (<c>computeAllChannelProbeStats</c>) and the bulk insert.
/// </summary>
public interface IChannelProbeStore
{
    /// <summary>
    /// Port of <c>client.ChannelProbe.Query().Where(ChannelIDIn(...),
    /// TimestampGTE(start), TimestampLTE(end)).Order(ent.Asc(Timestamp))</c>
    /// (channel_probe.go:375-382): inclusive bounds, ascending timestamp and no
    /// other ordering.
    /// </summary>
    Task<IReadOnlyList<ChannelProbeRecord>> QueryAsync(
        IReadOnlyList<long> channelIds,
        long startUnix,
        long endUnix,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>computeAllChannelProbeStats</c> (channel_probe.go:113-235):
    /// the PostgreSQL form of <c>qb.BuildProbeStatsQuery</c>
    /// (throughput.go:317-368). Attempt-based (completed and failed
    /// executions), usage tokens bounded by the same window start, one row per
    /// channel ordered by channel id. The caller must pass a non-empty
    /// <paramref name="channelIds"/>; the reference returns a nil map otherwise.
    /// </summary>
    Task<IReadOnlyList<ChannelProbeStatsRow>> ComputeStatsAsync(
        IReadOnlyList<long> channelIds,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>CreateBulk(probes...).Exec</c> (channel_probe.go:330):
    /// one insert per collected bucket. The reference has no uniqueness
    /// constraint, so duplicates are inserted like any other row.
    /// </summary>
    Task CreateRangeAsync(
        IReadOnlyList<ChannelProbeRecord> probes,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>channel_probes</c> table. Reads use Dapper like
/// the other read-side seams; the probe writer's inserts go through EF Core.
/// </summary>
public sealed class ChannelProbeStore(AxonHubDbContext db) : IChannelProbeStore
{
    private const string Columns = """
        "id", "total_request_count", "success_request_count",
        "avg_tokens_per_second", "avg_time_to_first_token_ms", "timestamp", "channel_id"
        """;

    public async Task<IReadOnlyList<ChannelProbeRecord>> QueryAsync(
        IReadOnlyList<long> channelIds,
        long startUnix,
        long endUnix,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelIds);
        if (channelIds.Count == 0)
        {
            return [];
        }

        var sql = "SELECT " + Columns
            + " FROM \"channel_probes\""
            + " WHERE \"channel_id\" = ANY(@ChannelIds)"
            + " AND \"timestamp\" >= @StartUnix"
            + " AND \"timestamp\" <= @EndUnix"
            + " ORDER BY \"timestamp\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ChannelProbeRecord>(
            new CommandDefinition(
                sql,
                new { ChannelIds = channelIds.ToArray(), StartUnix = startUnix, EndUnix = endUnix },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<ChannelProbeStatsRow>> ComputeStatsAsync(
        IReadOnlyList<long> channelIds,
        DateTime startUtc,
        DateTime endUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channelIds);
        if (channelIds.Count == 0)
        {
            return [];
        }

        // PostgreSQL form of qb.BuildProbeStatsQuery: $1 (the shared window
        // start) is bound as @Start, $2 as @End, the channel list as @ChannelIds.
        const string Sql =
            """
            SELECT
                se."channel_id" AS channel_id,
                COUNT(*) AS total_count,
                SUM(CASE WHEN se."status" = 'completed' THEN 1 ELSE 0 END) AS success_count,
                SUM(CASE WHEN se."status" = 'completed' THEN COALESCE(ul.total_completion_tokens, 0) ELSE 0 END) AS total_tokens,
                SUM(CASE WHEN se."status" = 'completed' THEN
                    CASE WHEN se."stream" AND se."metrics_first_token_latency_ms" IS NOT NULL
                         THEN CASE WHEN se."metrics_first_token_latency_ms" >= se."metrics_latency_ms"
                              THEN 0
                              ELSE se."metrics_latency_ms" - se."metrics_first_token_latency_ms" END
                         ELSE se."metrics_latency_ms" END
                    ELSE 0 END) AS effective_latency_ms,
                SUM(CASE WHEN se."status" = 'completed' AND se."stream" AND se."metrics_first_token_latency_ms" IS NOT NULL THEN se."metrics_first_token_latency_ms" ELSE 0 END) AS total_first_token_latency,
                COUNT(DISTINCT se."request_id") AS request_count,
                SUM(CASE WHEN se."status" = 'completed' AND se."stream" AND se."metrics_first_token_latency_ms" IS NOT NULL THEN 1 ELSE 0 END) AS streaming_request_count
            FROM "request_executions" se
            LEFT JOIN (
                SELECT
                    ul2."request_id",
                    ul2."channel_id",
                    SUM(COALESCE(ul2."completion_tokens", 0)) AS total_completion_tokens
                FROM "usage_logs" ul2
                WHERE ul2."created_at" >= @Start
                GROUP BY ul2."request_id", ul2."channel_id"
            ) ul ON se."status" = 'completed'
                AND se."request_id" = ul."request_id"
                AND se."channel_id" = ul."channel_id"
            WHERE se."created_at" >= @Start
                AND se."created_at" < @End
                AND se."status" IN ('completed', 'failed')
                AND se."channel_id" = ANY(@ChannelIds)
            GROUP BY se."channel_id"
            ORDER BY se."channel_id";
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var rows = await connection.QueryAsync<ChannelProbeStatsRow>(
                new CommandDefinition(
                    Sql,
                    new { Start = startUtc, End = endUtc, ChannelIds = channelIds.ToArray() },
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to query channel probe stats: " + ex.Message,
                ex);
        }
    }

    public async Task CreateRangeAsync(
        IReadOnlyList<ChannelProbeRecord> probes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probes);
        if (probes.Count == 0)
        {
            return;
        }

        db.ChannelProbes.AddRange(probes);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
