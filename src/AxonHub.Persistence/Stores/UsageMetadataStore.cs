using Dapper;

namespace AxonHub.Persistence;

/// <summary>
/// Port of the numeric row scanned by <c>aggregateUsageMetadata</c>
/// (internal/server/biz/usage_metadata.go): the SUMs over the completed
/// requests of a trace or thread. SQL aggregates without GROUP BY always return
/// one row; NULL sums are coalesced to zero like the reference's null scans.
/// </summary>
public readonly record struct UsageMetadataRow(
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalTokens,
    long TotalCachedTokens,
    long TotalCachedWriteTokens,
    double TotalCost);

/// <summary>
/// Persistence seam for the usage metadata aggregates exposed by the Trace and
/// Thread GraphQL types.
/// </summary>
public interface IUsageMetadataStore
{
    /// <summary>
    /// Port of TraceService.UsageMetadata: usage_logs whose request belongs to
    /// the trace and completed.
    /// </summary>
    Task<UsageMetadataRow> AggregateForTraceAsync(
        long traceDbId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of ThreadService.UsageMetadata: usage_logs whose request belongs to
    /// a trace of the thread and completed.
    /// </summary>
    Task<UsageMetadataRow> AggregateForThreadAsync(
        long threadDbId,
        CancellationToken cancellationToken = default);
}

/// <summary>Read-only Dapper aggregate over <c>usage_logs</c>.</summary>
public sealed class UsageMetadataStore(AxonHubDbContext db) : IUsageMetadataStore
{
    private const string Aggregates = """
        COALESCE(SUM(u."prompt_tokens"), 0)::bigint AS "TotalInputTokens",
        COALESCE(SUM(u."completion_tokens"), 0)::bigint AS "TotalOutputTokens",
        COALESCE(SUM(u."total_tokens"), 0)::bigint AS "TotalTokens",
        COALESCE(SUM(u."prompt_cached_tokens"), 0)::bigint AS "TotalCachedTokens",
        COALESCE(SUM(u."prompt_write_cached_tokens"), 0)::bigint AS "TotalCachedWriteTokens",
        COALESCE(SUM(u."total_cost"), 0) AS "TotalCost"
        """;

    public Task<UsageMetadataRow> AggregateForTraceAsync(
        long traceDbId,
        CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT
            """ + " " + Aggregates + """

            FROM "usage_logs" u
            WHERE EXISTS (
                SELECT 1 FROM "requests" r
                WHERE r."id" = u."request_id"
                  AND r."trace_id" = @TraceId
                  AND r."status" = 'completed');
            """;

        return QuerySingleAsync(sql, new { TraceId = traceDbId }, cancellationToken);
    }

    public Task<UsageMetadataRow> AggregateForThreadAsync(
        long threadDbId,
        CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT
            """ + " " + Aggregates + """

            FROM "usage_logs" u
            WHERE EXISTS (
                SELECT 1 FROM "requests" r
                JOIN "traces" t ON t."id" = r."trace_id"
                WHERE r."id" = u."request_id"
                  AND r."status" = 'completed'
                  AND t."thread_id" = @ThreadId);
            """;

        return QuerySingleAsync(sql, new { ThreadId = threadDbId }, cancellationToken);
    }

    private async Task<UsageMetadataRow> QuerySingleAsync(
        string sql,
        object parameters,
        CancellationToken cancellationToken)
    {
        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleAsync<UsageMetadataRow>(
            new CommandDefinition(
                sql,
                parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
