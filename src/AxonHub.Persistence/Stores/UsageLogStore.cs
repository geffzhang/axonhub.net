using System.Text;
using Dapper;

namespace AxonHub.Persistence;

/// <summary>
/// Port of the numeric row scanned by <c>QuotaService.usageAgg</c>
/// (internal/server/biz/quota.go:281-284). The cost stays a float here because
/// the reference scans a float and converts it with decimal.NewFromFloat in the
/// business layer.
/// </summary>
public readonly record struct UsageAggregate(long TotalTokens, double TotalCost);

/// <summary>Persistence seam for the reference <c>usage_logs</c> table.</summary>
public interface IUsageLogStore
{
    Task<long> CreateAsync(UsageLogRecord usageLog, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UsageLogRecord>> ListByRequestIdAsync(
        long requestId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of QuotaService.requestCount (internal/server/biz/quota.go:258-279):
    /// rows of one API key in the window, optionally bounded at both ends.
    /// </summary>
    Task<long> CountByApiKeyAsync(
        long apiKeyId,
        DateTime? start,
        DateTime? end,
        bool endInclusive,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of QuotaService.usageAgg (internal/server/biz/quota.go:286-395): the
    /// COALESCE(SUM(...), 0) aggregate of one API key's rows in the window. The
    /// legacy rows without an api_key_id are excluded, as in the reference's
    /// disabled compatibility branch.
    /// </summary>
    Task<UsageAggregate> AggregateByApiKeyAsync(
        long apiKeyId,
        DateTime? start,
        DateTime? end,
        bool endInclusive,
        bool needTokens,
        bool needCost,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>usage_logs</c> table. Writes go through EF Core;
/// reads use Dapper.
/// </summary>
public sealed class UsageLogStore(AxonHubDbContext db) : IUsageLogStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "api_key_id", "model_id", "prompt_tokens",
        "completion_tokens", "total_tokens", "prompt_audio_tokens",
        "prompt_cached_tokens", "prompt_write_cached_tokens",
        "prompt_write_cached_tokens_5m", "prompt_write_cached_tokens_1h",
        "completion_audio_tokens", "completion_reasoning_tokens",
        "completion_accepted_prediction_tokens",
        "completion_rejected_prediction_tokens", "source", "format", "total_cost",
        "cost_items", "cost_price_reference_id", "channel_id", "project_id",
        "request_id"
        """;

    public async Task<long> CreateAsync(UsageLogRecord usageLog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usageLog);

        db.UsageLogs.Add(usageLog);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return usageLog.Id;
    }

    public async Task<IReadOnlyList<UsageLogRecord>> ListByRequestIdAsync(
        long requestId,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"usage_logs\" WHERE \"request_id\" = @RequestId ORDER BY \"id\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<UsageLogRecord>(
            new CommandDefinition(
                sql,
                new { RequestId = requestId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<long> CountByApiKeyAsync(
        long apiKeyId,
        DateTime? start,
        DateTime? end,
        bool endInclusive,
        CancellationToken cancellationToken = default)
    {
        var sql = new StringBuilder(
            "SELECT COUNT(*)::bigint FROM \"usage_logs\" WHERE \"api_key_id\" = @ApiKeyId");
        var parameters = new DynamicParameters();
        parameters.Add("ApiKeyId", apiKeyId);
        AppendWindow(sql, parameters, start, end, endInclusive);

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                sql.ToString(),
                parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<UsageAggregate> AggregateByApiKeyAsync(
        long apiKeyId,
        DateTime? start,
        DateTime? end,
        bool endInclusive,
        bool needTokens,
        bool needCost,
        CancellationToken cancellationToken = default)
    {
        if (!needTokens && !needCost)
        {
            return new UsageAggregate(0, 0);
        }

        var sql = new StringBuilder("SELECT ");
        sql.Append(needTokens
            ? "COALESCE(SUM(\"total_tokens\"), 0)::bigint AS total_tokens"
            : "0::bigint AS total_tokens");
        sql.Append(", ");
        sql.Append(needCost
            ? "COALESCE(SUM(\"total_cost\"), 0) AS total_cost"
            : "0::double precision AS total_cost");
        sql.Append(" FROM \"usage_logs\" WHERE \"api_key_id\" = @ApiKeyId");

        var parameters = new DynamicParameters();
        parameters.Add("ApiKeyId", apiKeyId);
        AppendWindow(sql, parameters, start, end, endInclusive);

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleAsync<UsageAggregateRow>(
            new CommandDefinition(
                sql.ToString(),
                parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return new UsageAggregate(row.TotalTokens, row.TotalCost);
    }

    private static void AppendWindow(
        StringBuilder sql,
        DynamicParameters parameters,
        DateTime? start,
        DateTime? end,
        bool endInclusive)
    {
        if (start is not null)
        {
            sql.Append(" AND \"created_at\" >= @Start");
            parameters.Add("Start", start.Value);
        }

        if (end is not null)
        {
            sql.Append(endInclusive
                ? " AND \"created_at\" <= @End"
                : " AND \"created_at\" < @End");
            parameters.Add("End", end.Value);
        }
    }

    private sealed class UsageAggregateRow
    {
        public long TotalTokens { get; set; }

        public double TotalCost { get; set; }
    }
}
