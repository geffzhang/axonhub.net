using System.Globalization;
using System.Text;
using Dapper;

namespace AxonHub.Persistence;

/// <summary>
/// Grouping dimension of the shared dashboard usage aggregate
/// (internal/server/gql/dashboard_usage_aggregates.go).
/// </summary>
public enum DashboardUsageDimension
{
    Channel,
    Model,
    ApiKey,
}

/// <summary>Row of the dashboard request-status grouping.</summary>
public sealed class DashboardStatusCountRow
{
    public string Status { get; init; } = string.Empty;

    public long Count { get; init; }
}

/// <summary>
/// One row of the shared dashboard usage aggregate scan. <see cref="Id"/> is the
/// channel or API key id and stays 0 for the model dimension; <see cref="Name"/>
/// is the channel name, API key name or model id.
/// </summary>
public sealed class DashboardUsageAggregateRow
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public long RequestCount { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long CachedTokens { get; init; }

    public long ReasoningTokens { get; init; }

    public double TotalCost { get; init; }
}

/// <summary>
/// Token sums for one time window (empty windows yield zeros). <see cref="RowCount"/>
/// lets the all-time token cache reproduce the reference's failure on an empty
/// window: its aggregate scans SQL NULL into an int, which errors when the
/// usage_logs table has no rows (TOKEN_STATS_ALL_TIME_AGG_FAILED).
/// </summary>
public sealed class DashboardTokenSumRow
{
    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long CachedTokens { get; init; }

    public long RowCount { get; init; }
}

/// <summary>Per-API-key token aggregate (apiKeyTokenUsageStats).</summary>
public sealed class DashboardApiKeyUsageRow
{
    public long ApiKeyId { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long CachedTokens { get; init; }

    public long ReasoningTokens { get; init; }
}

/// <summary>Per-API-key, per-model token aggregate used for the top-3 models.</summary>
public sealed class DashboardApiKeyModelUsageRow
{
    public long ApiKeyId { get; init; }

    public string ModelId { get; init; } = string.Empty;

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long CachedTokens { get; init; }

    public long ReasoningTokens { get; init; }

    public long TotalTokens { get; init; }
}

/// <summary>One local-day bucket of dailyRequestStats.</summary>
public sealed class DashboardDailyRequestRow
{
    public string Date { get; init; } = string.Empty;

    public long TotalCount { get; init; }

    public long TotalTokens { get; init; }

    public double TotalCost { get; init; }
}

/// <summary>Request count grouped by project (topRequestsProjects).</summary>
public sealed class DashboardProjectRequestRow
{
    public long ProjectId { get; init; }

    public long RequestCount { get; init; }
}

/// <summary>Success/failure counts grouped by channel (channelSuccessRates).</summary>
public sealed class DashboardChannelExecutionRow
{
    public long ChannelId { get; init; }

    public long SuccessCount { get; init; }

    public long FailedCount { get; init; }
}

/// <summary>The channel columns the dashboard reads for display.</summary>
public sealed class DashboardChannelRow
{
    public long Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public string Status { get; init; } = string.Empty;
}

/// <summary>Row of the PostgreSQL DISTINCT ON throughput query, channel variant.</summary>
public sealed class DashboardFastestChannelRow
{
    public long ChannelId { get; init; }

    public string ChannelName { get; init; } = string.Empty;

    public string ChannelType { get; init; } = string.Empty;

    public long TokensCount { get; init; }

    public long LatencyMs { get; init; }

    public long RequestCount { get; init; }

    public double Throughput { get; init; }
}

/// <summary>Row of the PostgreSQL DISTINCT ON throughput query, model variant.</summary>
public sealed class DashboardFastestModelRow
{
    public string ModelId { get; init; } = string.Empty;

    public string ModelName { get; init; } = string.Empty;

    public long TokensCount { get; init; }

    public long LatencyMs { get; init; }

    public long RequestCount { get; init; }

    public double Throughput { get; init; }
}

/// <summary>One daily model performance bucket (modelPerformanceStats).</summary>
public sealed class DashboardModelPerformanceRow
{
    public string Date { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public long TokensCount { get; init; }

    public long LatencyMs { get; init; }

    public double? FirstTokenMs { get; init; }

    public long RequestCount { get; init; }

    public double? Throughput { get; init; }
}

/// <summary>One daily channel performance bucket (channelPerformanceStats).</summary>
public sealed class DashboardChannelPerformanceRow
{
    public string Date { get; init; } = string.Empty;

    public long ChannelId { get; init; }

    public long TokensCount { get; init; }

    public long LatencyMs { get; init; }

    public double? FirstTokenMs { get; init; }

    public long RequestCount { get; init; }

    public double? Throughput { get; init; }
}

/// <summary>Usage aggregated by API key owner (usageStatsByUser).</summary>
public sealed class DashboardUserUsageRow
{
    public long UserId { get; init; }

    public string FirstName { get; init; } = string.Empty;

    public string LastName { get; init; } = string.Empty;

    public string Email { get; init; } = string.Empty;

    public long RequestCount { get; init; }

    public long TotalTokens { get; init; }

    public double TotalCost { get; init; }
}

/// <summary>
/// Persistence seam for the dashboard root fields
/// (internal/server/gql/dashboard.resolvers.go, dashboard_usage_aggregates.go,
/// qb/throughput.go and qb/throughput_daily.go).
/// </summary>
public interface IDashboardStore
{
    Task<IReadOnlyList<DashboardStatusCountRow>> GetRequestStatusCountsAsync(
        CancellationToken cancellationToken = default);

    Task<long> CountUsageLogsSinceAsync(
        DateTime since,
        CancellationToken cancellationToken = default);

    Task<long> CountUsageLogsBetweenAsync(
        DateTime since,
        DateTime until,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// One grouped row per channel, model or API key. <paramref name="since"/>
    /// null means all time (the reference's allTime window).
    /// </summary>
    Task<IReadOnlyList<DashboardUsageAggregateRow>> GetUsageAggregatesAsync(
        DashboardUsageDimension dimension,
        DateTime? since,
        CancellationToken cancellationToken = default);

    /// <summary>Token sums for the window; null <paramref name="since"/> is all time.</summary>
    Task<DashboardTokenSumRow> GetTokenSumsAsync(
        DateTime? since,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DashboardApiKeyUsageRow>> GetApiKeyTokenUsageAsync(
        IReadOnlyList<long> apiKeyIds,
        DateTime? createdAtGte,
        DateTime? createdAtLte,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DashboardApiKeyModelUsageRow>> GetApiKeyModelTokenUsageAsync(
        IReadOnlyList<long> apiKeyIds,
        DateTime? createdAtGte,
        DateTime? createdAtLte,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DashboardDailyRequestRow>> GetDailyRequestStatsAsync(
        DateTime startUtc,
        DateTime endUtc,
        string timeZone,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DashboardProjectRequestRow>> GetTopProjectRequestsAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DashboardChannelExecutionRow>> GetChannelExecutionStatsAsync(
        DateTime? since,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DashboardChannelRow>> GetChannelsByIdsAsync(
        IReadOnlyList<long> channelIds,
        bool includeDeleted,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DashboardFastestChannelRow>> GetFastestChannelsAsync(
        DateTime sinceUtc,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DashboardFastestModelRow>> GetFastestModelsAsync(
        DateTime sinceUtc,
        int limit,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DashboardModelPerformanceRow>> GetDailyModelPerformanceAsync(
        DateTime startUtc,
        string timeZone,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DashboardChannelPerformanceRow>> GetDailyChannelPerformanceAsync(
        DateTime startUtc,
        string timeZone,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DashboardUserUsageRow>> GetUsageByUserAsync(
        long projectId,
        DateTime? createdAtGte,
        DateTime? createdAtLte,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Dapper port of the dashboard aggregate queries. The reference builds the
/// grouped scans through ent selector modifiers and the throughput rankings
/// through qb/throughput.go; the port renders the same SQL directly. Only the
/// PostgreSQL branches are kept: the port targets PostgreSQL exclusively (C195).
/// </summary>
public sealed class DashboardStore(AxonHubDbContext db) : IDashboardStore
{
    /// <summary>
    /// The effective generation latency shared by the throughput builders:
    /// streaming requests with a TTFT count only the post-first-token time; a
    /// TTFT at or beyond the total latency yields zero.
    /// </summary>
    private const string EffectiveLatency =
        "CASE WHEN se.stream AND se.metrics_first_token_latency_ms IS NOT NULL"
        + " THEN CASE WHEN se.metrics_first_token_latency_ms >= se.metrics_latency_ms"
        + " THEN 0 ELSE se.metrics_latency_ms - se.metrics_first_token_latency_ms END"
        + " ELSE se.metrics_latency_ms END";

    private const string UsageAggregateColumns =
        """
        COUNT(m."id") AS request_count,
                COALESCE(SUM(m."prompt_tokens"), 0) AS input_tokens,
                COALESCE(SUM(m."completion_tokens"), 0) AS output_tokens,
                COALESCE(SUM(m."prompt_cached_tokens"), 0) AS cached_tokens,
                COALESCE(SUM(m."completion_reasoning_tokens"), 0) AS reasoning_tokens,
                COALESCE(SUM(m."total_cost"), 0) AS total_cost
        """;

    public async Task<IReadOnlyList<DashboardStatusCountRow>> GetRequestStatusCountsAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT r."status" AS status, COUNT(*) AS count
            FROM "requests" r
            GROUP BY r."status";
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<DashboardStatusCountRow>(
                new CommandDefinition(
                    sql,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    public Task<long> CountUsageLogsSinceAsync(
        DateTime since,
        CancellationToken cancellationToken = default)
        => CountUsageLogsAsync(
            " WHERE m.\"created_at\" >= @since",
            since,
            null,
            cancellationToken);

    public Task<long> CountUsageLogsBetweenAsync(
        DateTime since,
        DateTime until,
        CancellationToken cancellationToken = default)
        => CountUsageLogsAsync(
            " WHERE m.\"created_at\" >= @since AND m.\"created_at\" < @until",
            since,
            until,
            cancellationToken);

    private async Task<long> CountUsageLogsAsync(
        string where,
        DateTime since,
        DateTime? until,
        CancellationToken cancellationToken)
    {
        var sql = "SELECT COUNT(m.\"id\") FROM \"usage_logs\" m" + where + ";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                sql,
                new { since, until },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DashboardUsageAggregateRow>> GetUsageAggregatesAsync(
        DashboardUsageDimension dimension,
        DateTime? since,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT ");
        string? sincePredicate = since is null
            ? null
            : "m.\"created_at\" >= " + args.Add(since.Value);

        switch (dimension)
        {
            case DashboardUsageDimension.Channel:
                sql.Append("c.\"id\" AS id, c.\"name\" AS name, ")
                    .Append(UsageAggregateColumns)
                    .Append(" FROM \"usage_logs\" m")
                    .Append(" JOIN \"channels\" c ON m.\"channel_id\" = c.\"id\"")
                    .Append(" WHERE c.\"deleted_at\" = 0");
                if (sincePredicate is not null)
                {
                    sql.Append(" AND ").Append(sincePredicate);
                }

                sql.Append(" GROUP BY c.\"id\", c.\"name\";");
                break;

            case DashboardUsageDimension.Model:
                sql.Append("m.\"model_id\" AS name, ")
                    .Append(UsageAggregateColumns)
                    .Append(" FROM \"usage_logs\" m WHERE TRUE");
                if (sincePredicate is not null)
                {
                    sql.Append(" AND ").Append(sincePredicate);
                }

                sql.Append(" GROUP BY m.\"model_id\";");
                break;

            case DashboardUsageDimension.ApiKey:
                sql.Append("k.\"id\" AS id, k.\"name\" AS name, ")
                    .Append(UsageAggregateColumns)
                    .Append(" FROM \"usage_logs\" m")
                    .Append(" JOIN \"api_keys\" k ON m.\"api_key_id\" = k.\"id\"")
                    .Append(" WHERE m.\"api_key_id\" IS NOT NULL AND k.\"deleted_at\" = 0");
                if (sincePredicate is not null)
                {
                    sql.Append(" AND ").Append(sincePredicate);
                }

                sql.Append(" GROUP BY k.\"id\", k.\"name\";");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(dimension), dimension, null);
        }

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<DashboardUsageAggregateRow>(
                new CommandDefinition(
                    sql.ToString(),
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    public async Task<DashboardTokenSumRow> GetTokenSumsAsync(
        DateTime? since,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder(
            """
            SELECT COALESCE(SUM(m."prompt_tokens"), 0) AS input_tokens,
                   COALESCE(SUM(m."completion_tokens"), 0) AS output_tokens,
                   COALESCE(SUM(m."prompt_cached_tokens"), 0) AS cached_tokens,
                   COUNT(*) AS row_count
            FROM "usage_logs" m
            """);
        if (since is not null)
        {
            sql.Append(" WHERE m.\"created_at\" >= ").Append(args.Add(since.Value));
        }

        sql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleAsync<DashboardTokenSumRow>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DashboardApiKeyUsageRow>> GetApiKeyTokenUsageAsync(
        IReadOnlyList<long> apiKeyIds,
        DateTime? createdAtGte,
        DateTime? createdAtLte,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var predicates = new List<string?>();
        ConnectionSql.AppendIdIn(predicates, "m", "api_key_id", apiKeyIds, args);
        AppendTimeRange(predicates, args, createdAtGte, createdAtLte);
        var where = ConnectionSql.Combine(predicates);

        var sql =
            """
            SELECT m."api_key_id" AS api_key_id,
                   COALESCE(SUM(m."prompt_tokens"), 0) AS input_tokens,
                   COALESCE(SUM(m."completion_tokens"), 0) AS output_tokens,
                   COALESCE(SUM(m."prompt_cached_tokens"), 0) AS cached_tokens,
                   COALESCE(SUM(m."completion_reasoning_tokens"), 0) AS reasoning_tokens
            FROM "usage_logs" m
            """
            + (where is null ? string.Empty : " WHERE " + where)
            + " GROUP BY m.\"api_key_id\" ORDER BY m.\"api_key_id\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<DashboardApiKeyUsageRow>(
                new CommandDefinition(
                    sql,
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    public async Task<IReadOnlyList<DashboardApiKeyModelUsageRow>> GetApiKeyModelTokenUsageAsync(
        IReadOnlyList<long> apiKeyIds,
        DateTime? createdAtGte,
        DateTime? createdAtLte,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var predicates = new List<string?>();
        ConnectionSql.AppendIdIn(predicates, "m", "api_key_id", apiKeyIds, args);
        AppendTimeRange(predicates, args, createdAtGte, createdAtLte);
        var where = ConnectionSql.Combine(predicates);

        // Billable total is input + output; reasoning tokens are already part of
        // completion tokens, so they are not added again (dashboard_helpers.go).
        var sql =
            """
            SELECT m."api_key_id" AS api_key_id,
                   m."model_id" AS model_id,
                   COALESCE(SUM(m."prompt_tokens"), 0) AS input_tokens,
                   COALESCE(SUM(m."completion_tokens"), 0) AS output_tokens,
                   COALESCE(SUM(m."prompt_cached_tokens"), 0) AS cached_tokens,
                   COALESCE(SUM(m."completion_reasoning_tokens"), 0) AS reasoning_tokens,
                   COALESCE(SUM(m."prompt_tokens"), 0)
                     + COALESCE(SUM(m."completion_tokens"), 0) AS total_tokens
            FROM "usage_logs" m
            """
            + (where is null ? string.Empty : " WHERE " + where)
            + " GROUP BY m.\"api_key_id\", m.\"model_id\" ORDER BY total_tokens DESC;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<DashboardApiKeyModelUsageRow>(
                new CommandDefinition(
                    sql,
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    public async Task<IReadOnlyList<DashboardDailyRequestRow>> GetDailyRequestStatsAsync(
        DateTime startUtc,
        DateTime endUtc,
        string timeZone,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var zone = args.Add(timeZone);
        var dateExpression =
            "to_char(m.\"created_at\" AT TIME ZONE " + zone + "::text, 'YYYY-MM-DD')";

        var sql =
            "SELECT " + dateExpression + " AS \"date\", "
            + "COUNT(m.\"id\") AS total_count, "
            + "COALESCE(SUM(m.\"total_tokens\"), 0) AS total_tokens, "
            + "COALESCE(SUM(m.\"total_cost\"), 0) AS total_cost "
            + "FROM \"usage_logs\" m "
            + "WHERE m.\"created_at\" >= " + args.Add(startUtc)
            + " AND m.\"created_at\" < " + args.Add(endUtc)
            + " GROUP BY " + dateExpression
            + " ORDER BY \"date\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<DashboardDailyRequestRow>(
                new CommandDefinition(
                    sql,
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    public async Task<IReadOnlyList<DashboardProjectRequestRow>> GetTopProjectRequestsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql =
            """
            SELECT m."project_id" AS project_id, COUNT(*) AS request_count
            FROM "usage_logs" m
            GROUP BY m."project_id"
            ORDER BY request_count DESC
            LIMIT
            """
            + " " + args.Add(limit) + ";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<DashboardProjectRequestRow>(
                new CommandDefinition(
                    sql,
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    public async Task<IReadOnlyList<DashboardChannelExecutionRow>> GetChannelExecutionStatsAsync(
        DateTime? since,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder(
            """
            SELECT e."channel_id" AS channel_id,
                   SUM(CASE WHEN e."status" = 'completed' THEN 1 ELSE 0 END) AS success_count,
                   SUM(CASE WHEN e."status" = 'failed' THEN 1 ELSE 0 END) AS failed_count
            FROM "request_executions" e
            WHERE e."channel_id" IS NOT NULL
            """);
        if (since is not null)
        {
            sql.Append(" AND e.\"created_at\" >= ").Append(args.Add(since.Value));
        }

        sql.Append(" GROUP BY e.\"channel_id\";");

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<DashboardChannelExecutionRow>(
                new CommandDefinition(
                    sql.ToString(),
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    public async Task<IReadOnlyList<DashboardChannelRow>> GetChannelsByIdsAsync(
        IReadOnlyList<long> channelIds,
        bool includeDeleted,
        CancellationToken cancellationToken = default)
    {
        if (channelIds.Count == 0)
        {
            return [];
        }

        var args = new SqlArguments();
        var predicates = new List<string?>();
        ConnectionSql.AppendIdIn(predicates, "c", "id", channelIds, args);
        if (!includeDeleted)
        {
            predicates.Add("c.\"deleted_at\" = 0");
        }

        var sql =
            """
            SELECT c."id" AS id, c."name" AS name, c."type" AS type, c."status" AS status
            FROM "channels" c
            WHERE
            """
            + ConnectionSql.Combine(predicates) + ";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<DashboardChannelRow>(
                new CommandDefinition(
                    sql,
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    public async Task<IReadOnlyList<DashboardFastestChannelRow>> GetFastestChannelsAsync(
        DateTime sinceUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var sql = BuildThroughputQuery(byChannel: true, limit);

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<DashboardFastestChannelRow>(
                new CommandDefinition(
                    sql,
                    new { since = sinceUtc },
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    public async Task<IReadOnlyList<DashboardFastestModelRow>> GetFastestModelsAsync(
        DateTime sinceUtc,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var sql = BuildThroughputQuery(byChannel: false, limit);

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<DashboardFastestModelRow>(
                new CommandDefinition(
                    sql,
                    new { since = sinceUtc },
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    /// <summary>
    /// Port of qb.BuildThroughputQuery with ThroughputModeDistinctOn (the
    /// PostgreSQL branch the reference selects).
    /// </summary>
    private static string BuildThroughputQuery(bool byChannel, int limit)
    {
        var selectColumns = byChannel
            ? "se.channel_id, c.name as channel_name, c.type as channel_type,"
            : "r.model_id, COALESCE(m.name, r.model_id) as model_name,";
        var joinClause = byChannel
            ? "JOIN channels c ON se.channel_id = c.id"
            : "JOIN requests r ON se.request_id = r.id LEFT JOIN models m ON r.model_id = m.model_id";
        var groupBy = byChannel
            ? "se.channel_id, c.name, c.type"
            : "r.model_id, m.name";

        return $"""
            WITH successful_execs AS (
                SELECT DISTINCT ON (request_id)
                    request_id,
                    channel_id,
                    metrics_latency_ms,
                    metrics_first_token_latency_ms,
                    stream
                FROM request_executions
                WHERE status = 'completed' AND metrics_latency_ms > 0 AND created_at >= @since
                ORDER BY request_id, created_at DESC, id DESC
            )
            SELECT
                {selectColumns}
                SUM(ul.completion_tokens) as tokens_count,
                SUM(se.metrics_latency_ms) as latency_ms,
                COUNT(DISTINCT se.request_id) as request_count,
                (CASE
                    WHEN SUM({EffectiveLatency}) > 0
                    THEN SUM(ul.completion_tokens) * 1000.0 / SUM({EffectiveLatency})
                    ELSE 0
                END)::double precision as throughput
            FROM successful_execs se
            JOIN usage_logs ul ON se.request_id = ul.request_id
            {joinClause}
            GROUP BY {groupBy}
            ORDER BY throughput DESC
            LIMIT {limit.ToString(CultureInfo.InvariantCulture)}
            """;
    }

    public async Task<IReadOnlyList<DashboardModelPerformanceRow>> GetDailyModelPerformanceAsync(
        DateTime startUtc,
        string timeZone,
        CancellationToken cancellationToken = default)
    {
        var sql = BuildDailyPerformanceQuery(byChannel: false);

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<DashboardModelPerformanceRow>(
                new CommandDefinition(
                    sql,
                    new { start = startUtc, timeZone },
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    public async Task<IReadOnlyList<DashboardChannelPerformanceRow>> GetDailyChannelPerformanceAsync(
        DateTime startUtc,
        string timeZone,
        CancellationToken cancellationToken = default)
    {
        var sql = BuildDailyPerformanceQuery(byChannel: true);

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<DashboardChannelPerformanceRow>(
                new CommandDefinition(
                    sql,
                    new { start = startUtc, timeZone },
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    /// <summary>
    /// Port of qb.BuildDailyPerformanceStatsQuery with ThroughputModeRowNumber,
    /// the mode both call sites select (modelPerformanceStats and the execution
    /// fallback of channelPerformanceStats).
    /// </summary>
    private static string BuildDailyPerformanceQuery(bool byChannel)
    {
        var idColumn = byChannel ? "se.channel_id" : "se.model_id";
        var idAlias = byChannel ? "channel_id" : "model_id";
        var joinRequests = byChannel
            ? string.Empty
            : "    JOIN requests r ON se.request_id = r.id\n";

        var ttftExpression =
            "SUM(CASE WHEN se.metrics_first_token_latency_ms IS NOT NULL"
            + " AND se.metrics_first_token_latency_ms > 0"
            + " THEN se.metrics_first_token_latency_ms ELSE 0 END)"
            + " / NULLIF(SUM(CASE WHEN se.metrics_first_token_latency_ms IS NOT NULL"
            + " AND se.metrics_first_token_latency_ms > 0 THEN 1 ELSE 0 END), 0)";

        var throughputExpression =
            "(CASE WHEN SUM(" + EffectiveLatency + ") > 0"
            + " THEN SUM(ul.completion_tokens) * 1000.0 / SUM(" + EffectiveLatency + ")"
            + " ELSE 0 END)";

        return $"""
            WITH successful_execs AS (
                SELECT
                    se.request_id,
                    {idColumn},
                    se.metrics_latency_ms,
                    se.metrics_first_token_latency_ms,
                    se.stream,
                    to_char(se.created_at AT TIME ZONE @timeZone::text, 'YYYY-MM-DD') as exec_date,
                    ROW_NUMBER() OVER (PARTITION BY se.request_id ORDER BY se.created_at DESC, se.id DESC) as rn
                FROM request_executions se
            {joinRequests}    WHERE se.status = 'completed'
                    AND se.metrics_latency_ms > 0
                    AND se.created_at >= @start
            ),
            daily AS (
                SELECT
                    exec_date as date,
                    {idColumn} as {idAlias},
                    SUM(ul.completion_tokens) as tokens_count,
                    SUM(se.metrics_latency_ms) as latency_ms,
                    ({ttftExpression})::double precision as first_token_ms,
                    COUNT(DISTINCT se.request_id) as request_count,
                    {throughputExpression}::double precision as throughput
                FROM successful_execs se
                JOIN usage_logs ul ON se.request_id = ul.request_id
                WHERE se.rn = 1
                GROUP BY exec_date, {idColumn}
            )
            SELECT date, {idAlias}, tokens_count, latency_ms, first_token_ms, request_count, throughput
            FROM daily
            WHERE daily.throughput IS NOT NULL
                AND daily.throughput > 0
            ORDER BY date DESC, throughput DESC
            """;
    }

    public async Task<IReadOnlyList<DashboardUserUsageRow>> GetUsageByUserAsync(
        long projectId,
        DateTime? createdAtGte,
        DateTime? createdAtLte,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var predicates = new List<string?>
        {
            "m.\"api_key_id\" IS NOT NULL",
            "m.\"project_id\" = " + args.Add(projectId),
            "k.\"deleted_at\" = 0",
        };
        AppendTimeRange(predicates, args, createdAtGte, createdAtLte);
        var where = ConnectionSql.Combine(predicates);

        var sql =
            """
            SELECT u."id" AS user_id,
                   u."first_name" AS first_name,
                   u."last_name" AS last_name,
                   u."email" AS email,
                   COUNT(m."id") AS request_count,
                   COALESCE(SUM(m."total_tokens"), 0) AS total_tokens,
                   COALESCE(SUM(m."total_cost"), 0) AS total_cost
            FROM "usage_logs" m
            JOIN "api_keys" k ON m."api_key_id" = k."id"
            JOIN "users" u ON k."user_id" = u."id"
            WHERE
            """
            + where
            + """
             GROUP BY u."id", u."first_name", u."last_name", u."email"
            ORDER BY request_count DESC;
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<DashboardUserUsageRow>(
                new CommandDefinition(
                    sql,
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    private static void AppendTimeRange(
        List<string?> predicates,
        SqlArguments args,
        DateTime? createdAtGte,
        DateTime? createdAtLte)
    {
        if (createdAtGte is not null)
        {
            predicates.Add("m.\"created_at\" >= " + args.Add(createdAtGte.Value));
        }

        if (createdAtLte is not null)
        {
            predicates.Add("m.\"created_at\" <= " + args.Add(createdAtLte.Value));
        }
    }
}
