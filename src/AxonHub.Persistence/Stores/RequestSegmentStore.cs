using System.Text;
using Dapper;

namespace AxonHub.Persistence;

/// <summary>Ordering fields of the entgql RequestOrder type.</summary>
public enum RequestSortField
{
    Id,
    CreatedAt,
    UpdatedAt,
}

/// <summary>
/// Subset of ent.RequestWhereInput used by the trace/thread detail pages
/// (id, time, projectID and status predicates plus and/or/not).
/// </summary>
public sealed record RequestListFilter
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

    public long? ProjectId { get; init; }

    public long? ProjectIdNEQ { get; init; }

    public IReadOnlyList<long>? ProjectIdIn { get; init; }

    public IReadOnlyList<long>? ProjectIdNotIn { get; init; }

    public string? Status { get; init; }

    public string? StatusNEQ { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    public IReadOnlyList<string>? StatusNotIn { get; init; }

    public RequestListFilter? Not { get; init; }

    public IReadOnlyList<RequestListFilter>? And { get; init; }

    public IReadOnlyList<RequestListFilter>? Or { get; init; }
}

/// <summary>
/// Request query for the entgql <c>Trace.requests</c> connection and the
/// segment/usage lookups of the trace services.
/// </summary>
public sealed record RequestListQuery
{
    public RequestListFilter? Where { get; init; }

    /// <summary>The query policy's <c>WhereProjectID</c> predicate.</summary>
    public long? ProjectScope { get; init; }

    /// <summary>
    /// The visibility predicate of UserProjectScopeReadRequestsRule for a
    /// non-owner project member: playground rows of the user and API requests
    /// whose key is not someone else's personal key.
    /// </summary>
    public long? VisibilityUserId { get; init; }

    /// <summary>The implicit <c>request.TraceIDEQ</c> predicate of Trace.requests.</summary>
    public long? TraceScope { get; init; }

    /// <summary>
    /// The implicit thread predicate of the thread-usage lookup
    /// (<c>request.HasTraceWith(trace.ThreadIDEQ)</c>).
    /// </summary>
    public long? ThreadScope { get; init; }

    public RequestSortField SortField { get; init; } = RequestSortField.Id;

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }
}

/// <summary>Persistence seam for the requests read by the trace/thread surface.</summary>
public interface IRequestSegmentStore
{
    /// <summary>
    /// Port of TraceService.GetRootSegment's request query: completed requests
    /// of a trace ordered by created_at ascending.
    /// </summary>
    Task<IReadOnlyList<RequestRecord>> ListCompletedByTraceAsync(
        long traceDbId,
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Port of RequestService.GetTraceFirstRequest.</summary>
    Task<RequestRecord?> GetFirstCompletedByTraceAsync(
        long traceDbId,
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of getPreviousTraceSpans' last-request lookup: the newest completed
    /// request of the previous trace.
    /// </summary>
    Task<RequestRecord?> GetLastCompletedByTraceAsync(
        long traceDbId,
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default);

    Task<ListPage<RequestRecord>> ListAsync(
        RequestListQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>Dapper store for the reference <c>requests</c> reads of this slice.</summary>
public sealed class RequestSegmentStore(AxonHubDbContext db) : IRequestSegmentStore
{
    internal const string Columns = """
        "id", "created_at", "updated_at", "user_id", "source", "model_id",
        "reasoning_effort", "format", "request_headers", "request_body",
        "response_headers", "response_body", "response_chunks", "external_id",
        "status", "stream", "client_ip", "user_agent", "metrics_latency_ms",
        "metrics_first_token_latency_ms", "metrics_reasoning_duration_ms",
        "content_saved", "content_storage_id", "content_storage_key",
        "content_saved_at", "api_key_id", "channel_id", "data_storage_id",
        "project_id", "trace_id"
        """;

    public async Task<IReadOnlyList<RequestRecord>> ListCompletedByTraceAsync(
        long traceDbId,
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default)
    {
        var (sql, args) = CompletedByTraceQuery(
            traceDbId, projectScope, visibilityUserId, orderDescending: false);
        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<RequestRecord>(
            new CommandDefinition(
                sql,
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public Task<RequestRecord?> GetFirstCompletedByTraceAsync(
        long traceDbId,
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default)
        => SingleCompletedByTraceAsync(
            traceDbId, projectScope, visibilityUserId, orderDescending: false, cancellationToken);

    public Task<RequestRecord?> GetLastCompletedByTraceAsync(
        long traceDbId,
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default)
        => SingleCompletedByTraceAsync(
            traceDbId, projectScope, visibilityUserId, orderDescending: true, cancellationToken);

    public async Task<ListPage<RequestRecord>> ListAsync(
        RequestListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var args = new SqlArguments();
        var conditions = new StringBuilder(" WHERE TRUE");
        AppendScopes(conditions, query, args);
        var where = BuildFilter(query.Where, "m", args);
        if (where is not null)
        {
            conditions.Append(" AND ").Append(where);
        }

        var sortColumn = query.SortField switch
        {
            RequestSortField.CreatedAt => "created_at",
            RequestSortField.UpdatedAt => "updated_at",
            _ => null,
        };
        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;
        const string from = " FROM \"requests\" m";
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
        var nodes = (await connection.QueryAsync<RequestRecord>(
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

        return new ListPage<RequestRecord>(nodes, hasNextPage, hasPreviousPage, count);
    }

    private async Task<RequestRecord?> SingleCompletedByTraceAsync(
        long traceDbId,
        long? projectScope,
        long? visibilityUserId,
        bool orderDescending,
        CancellationToken cancellationToken)
    {
        var (sql, args) = CompletedByTraceQuery(
            traceDbId, projectScope, visibilityUserId, orderDescending, limitOne: true);
        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<RequestRecord>(
            new CommandDefinition(
                sql,
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static (string Sql, SqlArguments Args) CompletedByTraceQuery(
        long traceDbId,
        long? projectScope,
        long? visibilityUserId,
        bool orderDescending,
        bool limitOne = false)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT ").Append(Columns)
            .Append(" FROM \"requests\" m WHERE m.\"trace_id\" = ").Append(args.Add(traceDbId))
            .Append(" AND m.\"status\" = 'completed'");
        if (projectScope is not null)
        {
            sql.Append(" AND m.\"project_id\" = ").Append(args.Add(projectScope.Value));
        }

        AppendVisibility(sql, args, visibilityUserId);
        sql.Append(orderDescending
            ? " ORDER BY m.\"created_at\" DESC"
            : " ORDER BY m.\"created_at\" ASC");
        if (limitOne)
        {
            // QuerySingleOrDefault returns one row; ent's First() emits LIMIT 1.
            sql.Append(" LIMIT 1");
        }

        sql.Append(';');
        return (sql.ToString(), args);
    }

    private static void AppendScopes(
        StringBuilder sql,
        RequestListQuery query,
        SqlArguments args)
    {
        if (query.ProjectScope is not null)
        {
            sql.Append(" AND m.\"project_id\" = ").Append(args.Add(query.ProjectScope.Value));
        }

        if (query.TraceScope is not null)
        {
            sql.Append(" AND m.\"trace_id\" = ").Append(args.Add(query.TraceScope.Value));
        }

        if (query.ThreadScope is not null)
        {
            sql.Append(" AND EXISTS (SELECT 1 FROM \"traces\" t WHERE t.\"id\" = m.\"trace_id\"")
                .Append(" AND t.\"thread_id\" = ").Append(args.Add(query.ThreadScope.Value))
                .Append(')');
        }

        AppendVisibility(sql, args, query.VisibilityUserId);
    }

    /// <summary>
    /// Port of the RequestFilter branch of UserProjectScopeReadRequestsRule:
    /// a non-owner project member sees playground rows they created and API
    /// requests whose key is missing or not another user's personal key.
    /// </summary>
    private static void AppendVisibility(
        StringBuilder sql,
        SqlArguments args,
        long? visibilityUserId)
    {
        if (visibilityUserId is null)
        {
            return;
        }

        var user = args.Add(visibilityUserId.Value);
        sql.Append(" AND ((m.\"source\" = 'playground' AND m.\"user_id\" = ").Append(user)
            .Append(") OR (m.\"source\" <> 'playground' AND (m.\"api_key_id\" IS NULL")
            .Append(" OR EXISTS (SELECT 1 FROM \"api_keys\" k WHERE k.\"id\" = m.\"api_key_id\"")
            .Append(" AND (k.\"type\" <> 'personal' OR k.\"user_id\" = ").Append(user)
            .Append(")))))");
    }

    internal static string? BuildFilter(
        RequestListFilter? filter,
        string alias,
        SqlArguments args)
    {
        if (filter is null)
        {
            return null;
        }

        var predicates = new List<string?>();
        if (filter.Id is not null)
        {
            predicates.Add(alias + ".\"id\" = " + args.Add(filter.Id.Value));
        }

        if (filter.IdNEQ is not null)
        {
            predicates.Add(alias + ".\"id\" <> " + args.Add(filter.IdNEQ.Value));
        }

        if (filter.IdIn is not null)
        {
            ConnectionSql.AppendIdIn(predicates, alias, "id", filter.IdIn, args);
        }

        if (filter.IdNotIn is not null)
        {
            TraceAdminStore.AppendIdNotIn(predicates, alias, "id", filter.IdNotIn, args);
        }

        if (filter.IdGT is not null)
        {
            predicates.Add(alias + ".\"id\" > " + args.Add(filter.IdGT.Value));
        }

        if (filter.IdGTE is not null)
        {
            predicates.Add(alias + ".\"id\" >= " + args.Add(filter.IdGTE.Value));
        }

        if (filter.IdLT is not null)
        {
            predicates.Add(alias + ".\"id\" < " + args.Add(filter.IdLT.Value));
        }

        if (filter.IdLTE is not null)
        {
            predicates.Add(alias + ".\"id\" <= " + args.Add(filter.IdLTE.Value));
        }

        TraceAdminStore.AppendTimeComparisons(
            predicates, filter.CreatedAt, filter.CreatedAtNEQ, filter.CreatedAtIn,
            filter.CreatedAtNotIn, filter.CreatedAtGT, filter.CreatedAtGTE,
            filter.CreatedAtLT, filter.CreatedAtLTE, alias, "created_at", args);
        TraceAdminStore.AppendTimeComparisons(
            predicates, filter.UpdatedAt, filter.UpdatedAtNEQ, filter.UpdatedAtIn,
            filter.UpdatedAtNotIn, filter.UpdatedAtGT, filter.UpdatedAtGTE,
            filter.UpdatedAtLT, filter.UpdatedAtLTE, alias, "updated_at", args);

        if (filter.ProjectId is not null)
        {
            predicates.Add(alias + ".\"project_id\" = " + args.Add(filter.ProjectId.Value));
        }

        if (filter.ProjectIdNEQ is not null)
        {
            predicates.Add(alias + ".\"project_id\" <> " + args.Add(filter.ProjectIdNEQ.Value));
        }

        if (filter.ProjectIdIn is not null)
        {
            ConnectionSql.AppendIdIn(predicates, alias, "project_id", filter.ProjectIdIn, args);
        }

        if (filter.ProjectIdNotIn is not null)
        {
            TraceAdminStore.AppendIdNotIn(
                predicates, alias, "project_id", filter.ProjectIdNotIn, args);
        }

        TraceAdminStore.AppendStatusComparisons(
            predicates, filter.Status, filter.StatusNEQ, filter.StatusIn,
            filter.StatusNotIn, alias, args);

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
            // entgql only appends Or when len(i.Or) > 0.
            if (or.Length > 0)
            {
                predicates.Add("(" + string.Join(" OR ", or) + ")");
            }
        }

        return ConnectionSql.Combine(predicates);
    }
}
