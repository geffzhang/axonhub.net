using Dapper;

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
