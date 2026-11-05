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

/// <summary>Ordering fields of the entgql UsageLogOrder type.</summary>
public enum UsageLogSortField
{
    Id,
    CreatedAt,
    UpdatedAt,
}

/// <summary>
/// Subset of ent.UsageLogWhereInput used by the request detail page: id,
/// timestamps, request/api-key/project/channel, source, format, model id and
/// and/or/not. The edge predicates and the remaining scalar predicates are
/// not ported in this slice (C192).
/// </summary>
public sealed record UsageLogListFilter
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

    public long? RequestId { get; init; }

    public long? RequestIdNEQ { get; init; }

    public IReadOnlyList<long>? RequestIdIn { get; init; }

    public IReadOnlyList<long>? RequestIdNotIn { get; init; }

    public long? ApiKeyId { get; init; }

    public long? ApiKeyIdNEQ { get; init; }

    public IReadOnlyList<long>? ApiKeyIdIn { get; init; }

    public IReadOnlyList<long>? ApiKeyIdNotIn { get; init; }

    public bool? ApiKeyIdIsNil { get; init; }

    public bool? ApiKeyIdNotNil { get; init; }

    public long? ProjectId { get; init; }

    public long? ProjectIdNEQ { get; init; }

    public IReadOnlyList<long>? ProjectIdIn { get; init; }

    public IReadOnlyList<long>? ProjectIdNotIn { get; init; }

    public long? ChannelId { get; init; }

    public long? ChannelIdNEQ { get; init; }

    public IReadOnlyList<long>? ChannelIdIn { get; init; }

    public IReadOnlyList<long>? ChannelIdNotIn { get; init; }

    public bool? ChannelIdIsNil { get; init; }

    public bool? ChannelIdNotNil { get; init; }

    public string? Source { get; init; }

    public string? SourceNEQ { get; init; }

    public IReadOnlyList<string>? SourceIn { get; init; }

    public IReadOnlyList<string>? SourceNotIn { get; init; }

    public string? ModelId { get; init; }

    public string? ModelIdNEQ { get; init; }

    public IReadOnlyList<string>? ModelIdIn { get; init; }

    public IReadOnlyList<string>? ModelIdNotIn { get; init; }

    public string? ModelIdContains { get; init; }

    public string? ModelIdContainsFold { get; init; }

    public string? Format { get; init; }

    public string? FormatNEQ { get; init; }

    public IReadOnlyList<string>? FormatIn { get; init; }

    public IReadOnlyList<string>? FormatNotIn { get; init; }

    public UsageLogListFilter? Not { get; init; }

    public IReadOnlyList<UsageLogListFilter>? And { get; init; }

    public IReadOnlyList<UsageLogListFilter>? Or { get; init; }
}

/// <summary>Usage-log connection query, optionally scoped by its request.</summary>
public sealed record UsageLogListQuery
{
    public long? RequestScope { get; init; }

    /// <summary>
    /// The project predicate the UsageLog query policy installs
    /// (internal/scopes/rule_user_project_scope_requests.go:36-40).
    /// </summary>
    public long? ProjectScope { get; init; }

    /// <summary>
    /// The per-row visibility predicate of the UsageLogFilter branch
    /// (rule_user_project_scope_requests.go:63-72): a non-owner member sees
    /// usage logs whose request is a Playground row they created or an API
    /// request whose key is missing or not another user's personal key.
    /// </summary>
    public long? VisibilityUserId { get; init; }

    public UsageLogListFilter? Where { get; init; }

    public UsageLogSortField SortField { get; init; } = UsageLogSortField.Id;

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }
}

/// <summary>Persistence seam for the reference <c>usage_logs</c> table.</summary>
public interface IUsageLogStore
{
    Task<ListPage<UsageLogRecord>> ListAsync(
        UsageLogListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the entgql Node lookup for UsageLog: the row must satisfy the
    /// query policy's project scope and optional visibility predicate.
    /// </summary>
    Task<UsageLogRecord?> GetByIdAsync(
        long id,
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default);

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

    public async Task<ListPage<UsageLogRecord>> ListAsync(
        UsageLogListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var args = new SqlArguments();
        var conditions = new StringBuilder(" WHERE TRUE");
        if (query.RequestScope is not null)
        {
            conditions.Append(" AND m.\"request_id\" = ")
                .Append(args.Add(query.RequestScope.Value));
        }

        AppendScopes(conditions, args, query.ProjectScope, query.VisibilityUserId);

        if (BuildFilter(query.Where, "m", args) is { } where)
        {
            conditions.Append(" AND ").Append(where);
        }

        var sortColumn = query.SortField switch
        {
            UsageLogSortField.CreatedAt => "created_at",
            UsageLogSortField.UpdatedAt => "updated_at",
            _ => null,
        };
        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;
        const string from = " FROM \"usage_logs\" m";
        var countSql = "SELECT COUNT(*) " + from + conditions + ";";
        var nodesSql = new StringBuilder("SELECT ");
        nodesSql.Append(string.Join(", ", Columns
            .Split(',')
            .Select(static column => "m." + column.Trim())));
        nodesSql.Append(from).Append(conditions);
        ConnectionSql.AppendCursor(
            nodesSql, "m", "id", sortColumn, query.After, query.Before, direction, args);
        nodesSql.Append(" ORDER BY ")
            .Append(ConnectionSql.OrderBy("m", "id", sortColumn, direction));

        var limit = query.First is not null
            ? query.First + 1
            : query.Last is not null ? query.Last + 1 : (int?)null;
        if (limit is not null)
        {
            nodesSql.Append(" LIMIT ").Append(limit.Value);
        }

        nodesSql.Append(';');
        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var count = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                countSql,
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        var nodes = (await connection.QueryAsync<UsageLogRecord>(
                new CommandDefinition(
                    nodesSql.ToString(),
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false)).AsList();

        var hasNextPage = query.Before is not null;
        var hasPreviousPage = query.After is not null;
        if (query.First is not null && query.First + 1 == nodes.Count)
        {
            hasNextPage = true;
            nodes.RemoveAt(nodes.Count - 1);
        }
        else if (query.Last is not null && query.Last + 1 == nodes.Count)
        {
            hasPreviousPage = true;
            nodes.RemoveAt(nodes.Count - 1);
        }

        if (query.Last is not null)
        {
            nodes.Reverse();
        }

        return new ListPage<UsageLogRecord>(nodes, hasNextPage, hasPreviousPage, count);
    }

    public async Task<UsageLogRecord?> GetByIdAsync(
        long id,
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT ");
        sql.Append(string.Join(", ", Columns
            .Split(',')
            .Select(static column => "m." + column.Trim())));
        sql.Append(" FROM \"usage_logs\" m WHERE m.\"id\" = ").Append(args.Add(id));
        AppendScopes(sql, args, projectScope, visibilityUserId);
        sql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<UsageLogRecord>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Port of the project predicate and the UsageLogFilter visibility branch
    /// of UserProjectScopeReadRequestsRule
    /// (internal/scopes/rule_user_project_scope_requests.go:36-72).
    /// </summary>
    private static void AppendScopes(
        StringBuilder sql,
        SqlArguments args,
        long? projectScope,
        long? visibilityUserId)
    {
        if (projectScope is not null)
        {
            sql.Append(" AND m.\"project_id\" = ").Append(args.Add(projectScope.Value));
        }

        if (visibilityUserId is null)
        {
            return;
        }

        var user = args.Add(visibilityUserId.Value);
        sql.Append(" AND EXISTS (SELECT 1 FROM \"requests\" r WHERE r.\"id\" = m.\"request_id\"")
            .Append(" AND ((r.\"source\" = 'playground' AND r.\"user_id\" = ").Append(user)
            .Append(") OR (r.\"source\" <> 'playground' AND (r.\"api_key_id\" IS NULL")
            .Append(" OR EXISTS (SELECT 1 FROM \"api_keys\" k WHERE k.\"id\" = r.\"api_key_id\"")
            .Append(" AND (k.\"type\" <> 'personal' OR k.\"user_id\" = ").Append(user)
            .Append("))))))");
    }

    private static string? BuildFilter(
        UsageLogListFilter? filter,
        string alias,
        SqlArguments args)
    {
        if (filter is null)
        {
            return null;
        }

        var predicates = new List<string?>();
        AppendId(predicates, alias, "id", filter.Id, filter.IdNEQ, filter.IdIn,
            filter.IdNotIn, filter.IdGT, filter.IdGTE, filter.IdLT, filter.IdLTE, args);
        TraceAdminStore.AppendTimeComparisons(
            predicates, filter.CreatedAt, filter.CreatedAtNEQ, filter.CreatedAtIn,
            filter.CreatedAtNotIn, filter.CreatedAtGT, filter.CreatedAtGTE,
            filter.CreatedAtLT, filter.CreatedAtLTE, alias, "created_at", args);
        TraceAdminStore.AppendTimeComparisons(
            predicates, filter.UpdatedAt, filter.UpdatedAtNEQ, filter.UpdatedAtIn,
            filter.UpdatedAtNotIn, filter.UpdatedAtGT, filter.UpdatedAtGTE,
            filter.UpdatedAtLT, filter.UpdatedAtLTE, alias, "updated_at", args);
        AppendId(predicates, alias, "request_id", filter.RequestId, filter.RequestIdNEQ,
            filter.RequestIdIn, filter.RequestIdNotIn, null, null, null, null, args);
        AppendNullableId(predicates, alias, "api_key_id", filter.ApiKeyId,
            filter.ApiKeyIdNEQ, filter.ApiKeyIdIn, filter.ApiKeyIdNotIn,
            filter.ApiKeyIdIsNil, filter.ApiKeyIdNotNil, args);
        AppendId(predicates, alias, "project_id", filter.ProjectId, filter.ProjectIdNEQ,
            filter.ProjectIdIn, filter.ProjectIdNotIn, null, null, null, null, args);
        AppendNullableId(predicates, alias, "channel_id", filter.ChannelId,
            filter.ChannelIdNEQ, filter.ChannelIdIn, filter.ChannelIdNotIn,
            filter.ChannelIdIsNil, filter.ChannelIdNotNil, args);
        TraceAdminStore.AppendStringComparisons(
            predicates, filter.Source, filter.SourceNEQ, filter.SourceIn,
            filter.SourceNotIn, null, null, null, null, null, null, null, null,
            null, alias, "source", args);
        TraceAdminStore.AppendStringComparisons(
            predicates, filter.ModelId, filter.ModelIdNEQ, filter.ModelIdIn,
            filter.ModelIdNotIn, null, null, null, null, filter.ModelIdContains,
            filter.ModelIdContainsFold, null, null, null, alias, "model_id", args);
        TraceAdminStore.AppendStringComparisons(
            predicates, filter.Format, filter.FormatNEQ, filter.FormatIn,
            filter.FormatNotIn, null, null, null, null, null, null, null, null,
            null, alias, "format", args);

        if (filter.Not is not null && BuildFilter(filter.Not, alias, args) is { } not)
        {
            predicates.Add("NOT (" + not + ")");
        }

        if (filter.And is not null)
        {
            var and = filter.And.Select(nested => BuildFilter(nested, alias, args))
                .Where(static nested => nested is not null).ToArray();
            if (and.Length > 0)
            {
                predicates.Add("(" + string.Join(" AND ", and) + ")");
            }
        }

        if (filter.Or is not null)
        {
            var or = filter.Or.Select(nested => BuildFilter(nested, alias, args))
                .Where(static nested => nested is not null).ToArray();
            if (or.Length > 0)
            {
                predicates.Add("(" + string.Join(" OR ", or) + ")");
            }
        }

        return ConnectionSql.Combine(predicates);
    }

    private static void AppendId(
        List<string?> predicates,
        string alias,
        string column,
        long? value,
        long? notEqual,
        IReadOnlyList<long>? inList,
        IReadOnlyList<long>? notInList,
        long? greaterThan,
        long? greaterThanOrEqual,
        long? lessThan,
        long? lessThanOrEqual,
        SqlArguments args)
    {
        var name = alias + ".\"" + column + "\"";
        if (value is not null)
        {
            predicates.Add(name + " = " + args.Add(value.Value));
        }

        if (notEqual is not null)
        {
            predicates.Add(name + " <> " + args.Add(notEqual.Value));
        }

        if (inList is not null)
        {
            ConnectionSql.AppendIdIn(predicates, alias, column, inList, args);
        }

        if (notInList is not null)
        {
            TraceAdminStore.AppendIdNotIn(predicates, alias, column, notInList, args);
        }

        if (greaterThan is not null)
        {
            predicates.Add(name + " > " + args.Add(greaterThan.Value));
        }

        if (greaterThanOrEqual is not null)
        {
            predicates.Add(name + " >= " + args.Add(greaterThanOrEqual.Value));
        }

        if (lessThan is not null)
        {
            predicates.Add(name + " < " + args.Add(lessThan.Value));
        }

        if (lessThanOrEqual is not null)
        {
            predicates.Add(name + " <= " + args.Add(lessThanOrEqual.Value));
        }
    }

    private static void AppendNullableId(
        List<string?> predicates,
        string alias,
        string column,
        long? value,
        long? notEqual,
        IReadOnlyList<long>? inList,
        IReadOnlyList<long>? notInList,
        bool? isNil,
        bool? notNil,
        SqlArguments args)
    {
        var name = alias + ".\"" + column + "\"";
        if (value is not null)
        {
            predicates.Add(name + " = " + args.Add(value.Value));
        }

        if (notEqual is not null)
        {
            predicates.Add(name + " <> " + args.Add(notEqual.Value));
        }

        if (inList is not null)
        {
            ConnectionSql.AppendIdIn(predicates, alias, column, inList, args);
        }

        if (notInList is not null)
        {
            TraceAdminStore.AppendIdNotIn(predicates, alias, column, notInList, args);
        }

        if (isNil == true)
        {
            predicates.Add(name + " IS NULL");
        }

        if (notNil == true)
        {
            predicates.Add(name + " IS NOT NULL");
        }
    }

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
