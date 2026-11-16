using System.Globalization;
using System.Text;
using Dapper;

namespace AxonHub.Persistence;

/// <summary>Dimension values accepted by analyticsDimensionStats.</summary>
public enum AnalyticsDimension
{
    Channel,
    Model,
    ApiKey,
    User,
}

/// <summary>
/// Filter shared by the analytics overview/daily/dimension queries
/// (internal/server/gql/analytics_helpers.go buildAnalyticsWhere). API key IDs
/// arrive already resolved by the service: null means "no API key filter", an
/// empty list combined with <see cref="HasUserFilter"/> means "the user filter
/// matched no keys" and renders SQL FALSE.
/// </summary>
public sealed record AnalyticsQueryFilter
{
    public DateTime? StartTime { get; init; }

    public DateTime? EndTime { get; init; }

    public IReadOnlyList<long>? ProjectIds { get; init; }

    public IReadOnlyList<long>? ChannelIds { get; init; }

    public IReadOnlyList<string>? ModelIds { get; init; }

    public IReadOnlyList<long>? ApiKeyIds { get; init; }

    public bool HasUserFilter { get; init; }
}

/// <summary>Row scanned by the analytics overview aggregate.</summary>
public sealed class AnalyticsOverviewRow
{
    public long TotalRequests { get; init; }

    public long TotalInputTokens { get; init; }

    public long TotalCachedTokens { get; init; }

    public long TotalOutputTokens { get; init; }

    public long TotalTokens { get; init; }

    public double Cost { get; init; }
}

/// <summary>Row scanned by the analytics daily aggregate.</summary>
public sealed class AnalyticsDailyRow
{
    public string Date { get; init; } = string.Empty;

    public long RequestCount { get; init; }

    public long InputTokens { get; init; }

    public long CachedTokens { get; init; }

    public long OutputTokens { get; init; }

    public long TotalTokens { get; init; }

    public double Cost { get; init; }
}

/// <summary>
/// One group of a dimension breakdown. <see cref="Name"/> is null for the
/// apiKey dimension; the service fills it from <see cref="IAnalyticsStore.GetApiKeyNamesAsync"/>.
/// </summary>
public sealed class AnalyticsDimensionRow
{
    public string Id { get; init; } = string.Empty;

    public string? Name { get; set; }

    /// <summary>User name parts for the user dimension; the name is composed in the resolver layer like the reference.</summary>
    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    public string? Email { get; init; }

    public long RequestCount { get; init; }

    public long InputTokens { get; init; }

    public long CachedTokens { get; init; }

    public long OutputTokens { get; init; }

    public long TotalTokens { get; init; }

    public double Cost { get; init; }
}

/// <summary>Row scanned by the per-dimension performance aggregate.</summary>
public sealed class AnalyticsPerformanceRow
{
    public string Id { get; init; } = string.Empty;

    public double? TokensPerSecond { get; init; }

    public double? TtftMs { get; init; }
}

public interface IAnalyticsStore
{
    Task<DateTime?> GetEarliestCreatedAtAsync(
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default);

    Task<AnalyticsOverviewRow> GetOverviewAsync(
        AnalyticsQueryFilter filter,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Daily aggregate keyed by the local calendar date in <paramref name="timeZone"/>
    /// (the configured name Go passes to <c>AT TIME ZONE</c>).
    /// </summary>
    Task<IReadOnlyList<AnalyticsDailyRow>> GetDailyAsync(
        AnalyticsQueryFilter filter,
        string timeZone,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AnalyticsDimensionRow>> GetDimensionAsync(
        AnalyticsQueryFilter filter,
        AnalyticsDimension dimension,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, AnalyticsPerformanceRow>> GetPerformanceAsync(
        AnalyticsQueryFilter filter,
        AnalyticsDimension dimension,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<long, string>> GetApiKeyNamesAsync(
        IReadOnlyList<long> apiKeyIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<long>> ResolveApiKeyIdsByUserIdsAsync(
        IReadOnlyList<long> userIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Dapper port of the analytics aggregate queries
/// (internal/server/gql/analytics.resolvers.go and analytics_helpers.go). The
/// reference builds these queries through ent's raw selector modifiers; the
/// port renders the same SQL directly (C194).
/// </summary>
public sealed class AnalyticsStore(AxonHubDbContext db) : IAnalyticsStore
{
    private const string StatsColumns =
        """
        COUNT(m."id") AS request_count,
                COALESCE(SUM(m."prompt_tokens"), 0) AS input_tokens,
                COALESCE(SUM(m."prompt_cached_tokens"), 0) AS cached_tokens,
                COALESCE(SUM(m."completion_tokens"), 0) AS output_tokens,
                COALESCE(SUM(m."total_tokens"), 0) AS total_tokens,
                COALESCE(SUM(m."total_cost"), 0) AS cost
        """;

    /// <summary>
    /// The overview aggregate scans into <see cref="AnalyticsOverviewRow"/>, whose
    /// properties carry the Total* names of the GraphQL type, so it cannot share
    /// the request_count/input_tokens aliases used by the grouped queries.
    /// </summary>
    private const string OverviewColumns =
        """
        COUNT(m."id") AS total_requests,
                COALESCE(SUM(m."prompt_tokens"), 0) AS total_input_tokens,
                COALESCE(SUM(m."prompt_cached_tokens"), 0) AS total_cached_tokens,
                COALESCE(SUM(m."completion_tokens"), 0) AS total_output_tokens,
                COALESCE(SUM(m."total_tokens"), 0) AS total_tokens,
                COALESCE(SUM(m."total_cost"), 0) AS cost
        """;

    /// <summary>The clamp from biz.MinLatencyMs (internal/server/biz/performance.go).</summary>
    private const int MinLatencyMs = 10;

    public async Task<DateTime?> GetEarliestCreatedAtAsync(
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT m.\"created_at\" FROM \"usage_logs\" m WHERE TRUE");
        UsageLogStore.AppendScopes(sql, args, projectScope, visibilityUserId);
        sql.Append(" ORDER BY m.\"created_at\" ASC LIMIT 1;");

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QueryFirstOrDefaultAsync<DateTime?>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<AnalyticsOverviewRow> GetOverviewAsync(
        AnalyticsQueryFilter filter,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT ");
        sql.Append(OverviewColumns)
            .Append(" FROM \"usage_logs\" m WHERE TRUE");
        AppendFilter(sql, args, filter);
        sql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        // Same shape as the reference's slice scan: without GROUP BY the
        // aggregate always yields one row (zero counts when nothing matches).
        return await connection.QuerySingleOrDefaultAsync<AnalyticsOverviewRow>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)
            ?? new AnalyticsOverviewRow();
    }

    public async Task<IReadOnlyList<AnalyticsDailyRow>> GetDailyAsync(
        AnalyticsQueryFilter filter,
        string timeZone,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var zone = args.Add(timeZone);
        var dateExpression = "to_char(m.\"created_at\" AT TIME ZONE " + zone + "::text, 'YYYY-MM-DD')";

        var sql = new StringBuilder("SELECT ");
        sql.Append(dateExpression).Append(" AS \"date\", ")
            .Append(StatsColumns)
            .Append(" FROM \"usage_logs\" m WHERE TRUE");
        AppendFilter(sql, args, filter);
        sql.Append(" GROUP BY ").Append(dateExpression)
            .Append(" ORDER BY \"date\";");

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<AnalyticsDailyRow>(
                new CommandDefinition(
                    sql.ToString(),
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false)).AsList();
    }

    public async Task<IReadOnlyList<AnalyticsDimensionRow>> GetDimensionAsync(
        AnalyticsQueryFilter filter,
        AnalyticsDimension dimension,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT ");

        switch (dimension)
        {
            case AnalyticsDimension.Channel:
                sql.Append("m.\"channel_id\" AS channel_id, c.\"name\" AS name, ")
                    .Append(StatsColumns)
                    .Append(" FROM \"usage_logs\" m")
                    .Append(" JOIN \"channels\" c ON m.\"channel_id\" = c.\"id\"")
                    .Append(" WHERE c.\"deleted_at\" = 0");
                AppendFilter(sql, args, filter);
                sql.Append(" GROUP BY m.\"channel_id\", c.\"name\"")
                    .Append(" ORDER BY \"total_tokens\" DESC;");
                return MapChannelRows(await QueryAsync<ChannelDimensionRaw>(sql, args, cancellationToken));

            case AnalyticsDimension.Model:
                sql.Append("m.\"model_id\" AS id, m.\"model_id\" AS name, ")
                    .Append(StatsColumns)
                    .Append(" FROM \"usage_logs\" m WHERE TRUE");
                AppendFilter(sql, args, filter);
                sql.Append(" GROUP BY m.\"model_id\"")
                    .Append(" ORDER BY \"total_tokens\" DESC;");
                return MapModelRows(await QueryAsync<ModelDimensionRaw>(sql, args, cancellationToken));

            case AnalyticsDimension.ApiKey:
                sql.Append("m.\"api_key_id\" AS api_key_id, ")
                    .Append(StatsColumns)
                    .Append(" FROM \"usage_logs\" m WHERE m.\"api_key_id\" IS NOT NULL");
                AppendFilter(sql, args, filter);
                sql.Append(" GROUP BY m.\"api_key_id\"")
                    .Append(" ORDER BY \"total_tokens\" DESC;");
                return MapApiKeyRows(await QueryAsync<ApiKeyDimensionRaw>(sql, args, cancellationToken));

            case AnalyticsDimension.User:
                sql.Append("u.\"id\" AS user_id, u.\"first_name\" AS first_name, ")
                    .Append("u.\"last_name\" AS last_name, u.\"email\" AS email, ")
                    .Append(StatsColumns)
                    .Append(" FROM \"usage_logs\" m")
                    .Append(" JOIN \"api_keys\" k ON m.\"api_key_id\" = k.\"id\"")
                    .Append(" JOIN \"users\" u ON k.\"user_id\" = u.\"id\"")
                    .Append(" WHERE m.\"api_key_id\" IS NOT NULL AND k.\"deleted_at\" = 0");
                AppendFilter(sql, args, filter);
                sql.Append(" GROUP BY u.\"id\", u.\"first_name\", u.\"last_name\", u.\"email\"")
                    .Append(" ORDER BY \"total_tokens\" DESC;");
                return MapUserRows(await QueryAsync<UserDimensionRaw>(sql, args, cancellationToken));

            default:
                throw new ArgumentOutOfRangeException(nameof(dimension), dimension, null);
        }
    }

    public async Task<IReadOnlyDictionary<string, AnalyticsPerformanceRow>> GetPerformanceAsync(
        AnalyticsQueryFilter filter,
        AnalyticsDimension dimension,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();

        const string generationLatency =
            "CASE WHEN e.\"stream\" AND e.\"metrics_first_token_latency_ms\" IS NOT NULL"
            + " THEN e.\"metrics_latency_ms\" - e.\"metrics_first_token_latency_ms\""
            + " ELSE e.\"metrics_latency_ms\" END";
        var effectiveLatency =
            "CASE WHEN " + generationLatency + " < " + MinLatencyMs.ToString(CultureInfo.InvariantCulture)
            + " THEN " + MinLatencyMs.ToString(CultureInfo.InvariantCulture)
            + " ELSE " + generationLatency + " END";
        var throughput =
            "CASE WHEN SUM(" + effectiveLatency + ") > 0"
            + " THEN SUM(m.\"completion_tokens\") * 1000.0 / SUM(" + effectiveLatency + ")"
            + " ELSE NULL END::double precision";
        const string ttft =
            "AVG(CASE WHEN e.\"stream\" AND e.\"metrics_first_token_latency_ms\" IS NOT NULL"
            + " AND e.\"metrics_first_token_latency_ms\" > 0"
            + " THEN e.\"metrics_first_token_latency_ms\" END)::double precision";

        var sql = new StringBuilder("SELECT ");
        string dimensionColumn;
        switch (dimension)
        {
            case AnalyticsDimension.Channel:
                dimensionColumn = "m.\"channel_id\"";
                sql.Append(dimensionColumn).Append("::text AS id, ");
                break;

            case AnalyticsDimension.Model:
                dimensionColumn = "m.\"model_id\"";
                sql.Append(dimensionColumn).Append("::text AS id, ");
                break;

            case AnalyticsDimension.ApiKey:
                dimensionColumn = "m.\"api_key_id\"";
                sql.Append(dimensionColumn).Append("::text AS id, ");
                break;

            case AnalyticsDimension.User:
                dimensionColumn = "u.\"id\"";
                sql.Append(dimensionColumn).Append("::text AS id, ");
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(dimension), dimension, null);
        }

        sql.Append(throughput).Append(" AS tokens_per_second, ")
            .Append(ttft).Append(" AS ttft_ms")
            .Append(" FROM \"usage_logs\" m")
            .Append(" JOIN \"request_executions\" e ON m.\"request_id\" = e.\"request_id\"")
            .Append(" AND m.\"channel_id\" = e.\"channel_id\"");

        switch (dimension)
        {
            case AnalyticsDimension.ApiKey:
                sql.Append(" WHERE m.\"api_key_id\" IS NOT NULL");
                break;

            case AnalyticsDimension.User:
                sql.Append(" JOIN \"api_keys\" k ON m.\"api_key_id\" = k.\"id\"")
                    .Append(" JOIN \"users\" u ON k.\"user_id\" = u.\"id\"")
                    .Append(" WHERE k.\"deleted_at\" = 0");
                break;

            default:
                sql.Append(" WHERE TRUE");
                break;
        }

        // Only the newest completed attempt with a measured latency counts, so
        // retries cannot inflate the metrics.
        sql.Append(" AND e.\"id\" = (SELECT MAX(le.\"id\") FROM \"request_executions\" le")
            .Append(" WHERE le.\"request_id\" = m.\"request_id\"")
            .Append(" AND le.\"channel_id\" = m.\"channel_id\"")
            .Append(" AND le.\"status\" = 'completed'")
            .Append(" AND le.\"metrics_latency_ms\" > 0)");
        AppendFilter(sql, args, filter);
        sql.Append(" GROUP BY ").Append(dimensionColumn).Append(';');

        var rows = await QueryAsync<AnalyticsPerformanceRow>(sql, args, cancellationToken);
        var result = new Dictionary<string, AnalyticsPerformanceRow>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            result[row.Id] = row;
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<long, string>> GetApiKeyNamesAsync(
        IReadOnlyList<long> apiKeyIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(apiKeyIds);

        var result = new Dictionary<long, string>();
        if (apiKeyIds.Count == 0)
        {
            return result;
        }

        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT \"id\", \"name\" FROM \"api_keys\" WHERE TRUE");
        AppendInLongs(sql, args, "\"id\"", apiKeyIds);
        sql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ApiKeyNameRaw>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        foreach (var row in rows)
        {
            result[row.Id] = row.Name;
        }

        return result;
    }

    public async Task<IReadOnlyList<long>> ResolveApiKeyIdsByUserIdsAsync(
        IReadOnlyList<long> userIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);

        if (userIds.Count == 0)
        {
            return [];
        }

        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT \"id\" FROM \"api_keys\" WHERE TRUE");
        AppendInLongs(sql, args, "\"user_id\"", userIds);
        sql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<long>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    /// <summary>Port of buildAnalyticsWhere (analytics_helpers.go:24-68).</summary>
    private static void AppendFilter(
        StringBuilder sql,
        SqlArguments args,
        AnalyticsQueryFilter filter)
    {
        if (filter.StartTime is { } startTime)
        {
            sql.Append(" AND m.\"created_at\" >= ").Append(args.Add(startTime));
        }

        if (filter.EndTime is { } endTime)
        {
            sql.Append(" AND m.\"created_at\" <= ").Append(args.Add(endTime));
        }

        if (filter.ProjectIds is { Count: > 0 } projectIds)
        {
            AppendInLongs(sql, args, "m.\"project_id\"", projectIds);
        }

        if (filter.ChannelIds is { Count: > 0 } channelIds)
        {
            AppendInLongs(sql, args, "m.\"channel_id\"", channelIds);
        }

        if (filter.ModelIds is { Count: > 0 } modelIds)
        {
            sql.Append(" AND m.\"model_id\" IN (");
            for (var i = 0; i < modelIds.Count; i++)
            {
                if (i > 0)
                {
                    sql.Append(", ");
                }

                sql.Append(args.Add(modelIds[i]));
            }

            sql.Append(')');
        }

        if (filter.ApiKeyIds is { Count: > 0 } apiKeyIds)
        {
            AppendInLongs(sql, args, "m.\"api_key_id\"", apiKeyIds);
        }
        else if (filter.HasUserFilter)
        {
            sql.Append(" AND FALSE");
        }
    }

    private static void AppendInLongs(
        StringBuilder sql,
        SqlArguments args,
        string column,
        IReadOnlyList<long> values)
    {
        sql.Append(" AND ").Append(column).Append(" IN (");
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                sql.Append(", ");
            }

            sql.Append(args.Add(values[i]));
        }

        sql.Append(')');
    }

    private async Task<List<T>> QueryAsync<T>(
        StringBuilder sql,
        SqlArguments args,
        CancellationToken cancellationToken)
    {
        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return (await connection.QueryAsync<T>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();
    }

    private static List<AnalyticsDimensionRow> MapChannelRows(List<ChannelDimensionRaw> rows)
    {
        var results = new List<AnalyticsDimensionRow>(rows.Count);
        foreach (var row in rows)
        {
            results.Add(new AnalyticsDimensionRow
            {
                Id = row.ChannelId.ToString(CultureInfo.InvariantCulture),
                Name = row.Name,
                RequestCount = row.RequestCount,
                InputTokens = row.InputTokens,
                CachedTokens = row.CachedTokens,
                OutputTokens = row.OutputTokens,
                TotalTokens = row.TotalTokens,
                Cost = row.Cost,
            });
        }

        return results;
    }

    private static List<AnalyticsDimensionRow> MapModelRows(List<ModelDimensionRaw> rows)
    {
        var results = new List<AnalyticsDimensionRow>(rows.Count);
        foreach (var row in rows)
        {
            results.Add(new AnalyticsDimensionRow
            {
                Id = row.Id,
                Name = row.Name,
                RequestCount = row.RequestCount,
                InputTokens = row.InputTokens,
                CachedTokens = row.CachedTokens,
                OutputTokens = row.OutputTokens,
                TotalTokens = row.TotalTokens,
                Cost = row.Cost,
            });
        }

        return results;
    }

    private static List<AnalyticsDimensionRow> MapApiKeyRows(List<ApiKeyDimensionRaw> rows)
    {
        var results = new List<AnalyticsDimensionRow>(rows.Count);
        foreach (var row in rows)
        {
            results.Add(new AnalyticsDimensionRow
            {
                Id = row.ApiKeyId.ToString(CultureInfo.InvariantCulture),
                RequestCount = row.RequestCount,
                InputTokens = row.InputTokens,
                CachedTokens = row.CachedTokens,
                OutputTokens = row.OutputTokens,
                TotalTokens = row.TotalTokens,
                Cost = row.Cost,
            });
        }

        return results;
    }

    private static List<AnalyticsDimensionRow> MapUserRows(List<UserDimensionRaw> rows)
    {
        var results = new List<AnalyticsDimensionRow>(rows.Count);
        foreach (var row in rows)
        {
            // The reference composes "first last" and falls back to the email
            // in dimStatsToDimensionStats; the service applies the same rule
            // with Go's TrimSpace semantics.
            results.Add(new AnalyticsDimensionRow
            {
                Id = row.UserId.ToString(CultureInfo.InvariantCulture),
                FirstName = row.FirstName,
                LastName = row.LastName,
                Email = row.Email,
                RequestCount = row.RequestCount,
                InputTokens = row.InputTokens,
                CachedTokens = row.CachedTokens,
                OutputTokens = row.OutputTokens,
                TotalTokens = row.TotalTokens,
                Cost = row.Cost,
            });
        }

        return results;
    }

    private sealed class ApiKeyNameRaw
    {
        public long Id { get; init; }

        public string Name { get; init; } = string.Empty;
    }

    private sealed class ChannelDimensionRaw
    {
        public long ChannelId { get; init; }

        public string Name { get; init; } = string.Empty;

        public long RequestCount { get; init; }

        public long InputTokens { get; init; }

        public long CachedTokens { get; init; }

        public long OutputTokens { get; init; }

        public long TotalTokens { get; init; }

        public double Cost { get; init; }
    }

    private sealed class ModelDimensionRaw
    {
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public long RequestCount { get; init; }

        public long InputTokens { get; init; }

        public long CachedTokens { get; init; }

        public long OutputTokens { get; init; }

        public long TotalTokens { get; init; }

        public double Cost { get; init; }
    }

    private sealed class ApiKeyDimensionRaw
    {
        public long ApiKeyId { get; init; }

        public long RequestCount { get; init; }

        public long InputTokens { get; init; }

        public long CachedTokens { get; init; }

        public long OutputTokens { get; init; }

        public long TotalTokens { get; init; }

        public double Cost { get; init; }
    }

    private sealed class UserDimensionRaw
    {
        public long UserId { get; init; }

        public string FirstName { get; init; } = string.Empty;

        public string LastName { get; init; } = string.Empty;

        public string Email { get; init; } = string.Empty;

        public long RequestCount { get; init; }

        public long InputTokens { get; init; }

        public long CachedTokens { get; init; }

        public long OutputTokens { get; init; }

        public long TotalTokens { get; init; }

        public double Cost { get; init; }
    }
}
