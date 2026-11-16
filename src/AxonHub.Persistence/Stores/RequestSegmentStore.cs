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

    public long? ApiKeyId { get; init; }

    public long? ApiKeyIdNEQ { get; init; }

    public IReadOnlyList<long>? ApiKeyIdIn { get; init; }

    public IReadOnlyList<long>? ApiKeyIdNotIn { get; init; }

    public bool? ApiKeyIdIsNil { get; init; }

    public bool? ApiKeyIdNotNil { get; init; }

    public long? TraceId { get; init; }

    public long? TraceIdNEQ { get; init; }

    public IReadOnlyList<long>? TraceIdIn { get; init; }

    public IReadOnlyList<long>? TraceIdNotIn { get; init; }

    public bool? TraceIdIsNil { get; init; }

    public bool? TraceIdNotNil { get; init; }

    public long? DataStorageId { get; init; }

    public long? DataStorageIdNEQ { get; init; }

    public IReadOnlyList<long>? DataStorageIdIn { get; init; }

    public IReadOnlyList<long>? DataStorageIdNotIn { get; init; }

    public bool? DataStorageIdIsNil { get; init; }

    public bool? DataStorageIdNotNil { get; init; }

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

    public string? ModelIdGT { get; init; }

    public string? ModelIdGTE { get; init; }

    public string? ModelIdLT { get; init; }

    public string? ModelIdLTE { get; init; }

    public string? ModelIdContains { get; init; }

    public string? ModelIdContainsFold { get; init; }

    public string? ModelIdHasPrefix { get; init; }

    public string? ModelIdHasSuffix { get; init; }

    public string? ModelIdEqualFold { get; init; }

    public string? ReasoningEffort { get; init; }

    public string? ReasoningEffortNEQ { get; init; }

    public IReadOnlyList<string>? ReasoningEffortIn { get; init; }

    public IReadOnlyList<string>? ReasoningEffortNotIn { get; init; }

    public string? ReasoningEffortGT { get; init; }

    public string? ReasoningEffortGTE { get; init; }

    public string? ReasoningEffortLT { get; init; }

    public string? ReasoningEffortLTE { get; init; }

    public string? ReasoningEffortContains { get; init; }

    public string? ReasoningEffortContainsFold { get; init; }

    public string? ReasoningEffortHasPrefix { get; init; }

    public string? ReasoningEffortHasSuffix { get; init; }

    public string? ReasoningEffortEqualFold { get; init; }

    public bool? ReasoningEffortIsNil { get; init; }

    public bool? ReasoningEffortNotNil { get; init; }

    public string? Format { get; init; }

    public string? FormatNEQ { get; init; }

    public IReadOnlyList<string>? FormatIn { get; init; }

    public IReadOnlyList<string>? FormatNotIn { get; init; }

    public string? FormatGT { get; init; }

    public string? FormatGTE { get; init; }

    public string? FormatLT { get; init; }

    public string? FormatLTE { get; init; }

    public string? FormatContains { get; init; }

    public string? FormatContainsFold { get; init; }

    public string? FormatHasPrefix { get; init; }

    public string? FormatHasSuffix { get; init; }

    public string? FormatEqualFold { get; init; }

    public string? ExternalId { get; init; }

    public string? ExternalIdNEQ { get; init; }

    public IReadOnlyList<string>? ExternalIdIn { get; init; }

    public IReadOnlyList<string>? ExternalIdNotIn { get; init; }

    public string? ExternalIdGT { get; init; }

    public string? ExternalIdGTE { get; init; }

    public string? ExternalIdLT { get; init; }

    public string? ExternalIdLTE { get; init; }

    public string? ExternalIdContains { get; init; }

    public string? ExternalIdContainsFold { get; init; }

    public string? ExternalIdHasPrefix { get; init; }

    public string? ExternalIdHasSuffix { get; init; }

    public string? ExternalIdEqualFold { get; init; }

    public bool? ExternalIdIsNil { get; init; }

    public bool? ExternalIdNotNil { get; init; }

    public RequestListFilter? Not { get; init; }

    public IReadOnlyList<RequestListFilter>? And { get; init; }

    public IReadOnlyList<RequestListFilter>? Or { get; init; }

    public bool? Stream { get; init; }

    public bool? StreamNEQ { get; init; }

    public string? ClientIp { get; init; }

    public string? ClientIpNEQ { get; init; }

    public IReadOnlyList<string>? ClientIpIn { get; init; }

    public IReadOnlyList<string>? ClientIpNotIn { get; init; }

    public string? ClientIpGT { get; init; }

    public string? ClientIpGTE { get; init; }

    public string? ClientIpLT { get; init; }

    public string? ClientIpLTE { get; init; }

    public string? ClientIpContains { get; init; }

    public string? ClientIpHasPrefix { get; init; }

    public string? ClientIpHasSuffix { get; init; }

    public string? ClientIpEqualFold { get; init; }

    public string? ClientIpContainsFold { get; init; }

    public string? UserAgent { get; init; }

    public string? UserAgentNEQ { get; init; }

    public IReadOnlyList<string>? UserAgentIn { get; init; }

    public IReadOnlyList<string>? UserAgentNotIn { get; init; }

    public string? UserAgentGT { get; init; }

    public string? UserAgentGTE { get; init; }

    public string? UserAgentLT { get; init; }

    public string? UserAgentLTE { get; init; }

    public string? UserAgentContains { get; init; }

    public string? UserAgentHasPrefix { get; init; }

    public string? UserAgentHasSuffix { get; init; }

    public string? UserAgentEqualFold { get; init; }

    public string? UserAgentContainsFold { get; init; }

    public long? MetricsLatencyMs { get; init; }

    public long? MetricsLatencyMsNEQ { get; init; }

    public IReadOnlyList<long>? MetricsLatencyMsIn { get; init; }

    public IReadOnlyList<long>? MetricsLatencyMsNotIn { get; init; }

    public long? MetricsLatencyMsGT { get; init; }

    public long? MetricsLatencyMsGTE { get; init; }

    public long? MetricsLatencyMsLT { get; init; }

    public long? MetricsLatencyMsLTE { get; init; }

    public bool? MetricsLatencyMsIsNil { get; init; }

    public bool? MetricsLatencyMsNotNil { get; init; }

    public long? MetricsFirstTokenLatencyMs { get; init; }

    public long? MetricsFirstTokenLatencyMsNEQ { get; init; }

    public IReadOnlyList<long>? MetricsFirstTokenLatencyMsIn { get; init; }

    public IReadOnlyList<long>? MetricsFirstTokenLatencyMsNotIn { get; init; }

    public long? MetricsFirstTokenLatencyMsGT { get; init; }

    public long? MetricsFirstTokenLatencyMsGTE { get; init; }

    public long? MetricsFirstTokenLatencyMsLT { get; init; }

    public long? MetricsFirstTokenLatencyMsLTE { get; init; }

    public bool? MetricsFirstTokenLatencyMsIsNil { get; init; }

    public bool? MetricsFirstTokenLatencyMsNotNil { get; init; }

    public long? MetricsReasoningDurationMs { get; init; }

    public long? MetricsReasoningDurationMsNEQ { get; init; }

    public IReadOnlyList<long>? MetricsReasoningDurationMsIn { get; init; }

    public IReadOnlyList<long>? MetricsReasoningDurationMsNotIn { get; init; }

    public long? MetricsReasoningDurationMsGT { get; init; }

    public long? MetricsReasoningDurationMsGTE { get; init; }

    public long? MetricsReasoningDurationMsLT { get; init; }

    public long? MetricsReasoningDurationMsLTE { get; init; }

    public bool? MetricsReasoningDurationMsIsNil { get; init; }

    public bool? MetricsReasoningDurationMsNotNil { get; init; }

    public bool? ContentSaved { get; init; }

    public bool? ContentSavedNEQ { get; init; }

    public long? ContentStorageId { get; init; }

    public long? ContentStorageIdNEQ { get; init; }

    public IReadOnlyList<long>? ContentStorageIdIn { get; init; }

    public IReadOnlyList<long>? ContentStorageIdNotIn { get; init; }

    public long? ContentStorageIdGT { get; init; }

    public long? ContentStorageIdGTE { get; init; }

    public long? ContentStorageIdLT { get; init; }

    public long? ContentStorageIdLTE { get; init; }

    public bool? ContentStorageIdIsNil { get; init; }

    public bool? ContentStorageIdNotNil { get; init; }

    public string? ContentStorageKey { get; init; }

    public string? ContentStorageKeyNEQ { get; init; }

    public IReadOnlyList<string>? ContentStorageKeyIn { get; init; }

    public IReadOnlyList<string>? ContentStorageKeyNotIn { get; init; }

    public string? ContentStorageKeyGT { get; init; }

    public string? ContentStorageKeyGTE { get; init; }

    public string? ContentStorageKeyLT { get; init; }

    public string? ContentStorageKeyLTE { get; init; }

    public string? ContentStorageKeyContains { get; init; }

    public string? ContentStorageKeyHasPrefix { get; init; }

    public string? ContentStorageKeyHasSuffix { get; init; }

    public bool? ContentStorageKeyIsNil { get; init; }

    public bool? ContentStorageKeyNotNil { get; init; }

    public string? ContentStorageKeyEqualFold { get; init; }

    public string? ContentStorageKeyContainsFold { get; init; }

    public DateTime? ContentSavedAt { get; init; }

    public DateTime? ContentSavedAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? ContentSavedAtIn { get; init; }

    public IReadOnlyList<DateTime>? ContentSavedAtNotIn { get; init; }

    public DateTime? ContentSavedAtGT { get; init; }

    public DateTime? ContentSavedAtGTE { get; init; }

    public DateTime? ContentSavedAtLT { get; init; }

    public DateTime? ContentSavedAtLTE { get; init; }

    public bool? ContentSavedAtIsNil { get; init; }

    public bool? ContentSavedAtNotNil { get; init; }

    public bool? HasApiKey { get; init; }

    public IReadOnlyList<ApiKeyListFilter>? HasApiKeyWith { get; init; }

    public bool? HasProject { get; init; }

    public IReadOnlyList<ProjectListFilter>? HasProjectWith { get; init; }

    public bool? HasTrace { get; init; }

    public IReadOnlyList<TraceListFilter>? HasTraceWith { get; init; }

    public bool? HasDataStorage { get; init; }

    public IReadOnlyList<DataStorageListFilter>? HasDataStorageWith { get; init; }

    public bool? HasExecutions { get; init; }

    public IReadOnlyList<RequestExecutionListFilter>? HasExecutionsWith { get; init; }

    public bool? HasChannel { get; init; }

    public IReadOnlyList<ChannelListFilter>? HasChannelWith { get; init; }

    public bool? HasUsageLogs { get; init; }

    public IReadOnlyList<UsageLogListFilter>? HasUsageLogsWith { get; init; }
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

    /// <summary>
    /// Node lookup with the Request query policy's project and visibility
    /// predicates (entgql Noder -> Request.Query().Only).
    /// </summary>
    Task<RequestRecord?> GetByIdAsync(
        long id,
        long? projectScope,
        long? visibilityUserId,
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

    public async Task<RequestRecord?> GetByIdAsync(
        long id,
        long? projectScope,
        long? visibilityUserId,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT ").Append(Columns)
            .Append(" FROM \"requests\" m WHERE m.\"id\" = ").Append(args.Add(id));
        if (projectScope is not null)
        {
            sql.Append(" AND m.\"project_id\" = ").Append(args.Add(projectScope.Value));
        }

        AppendVisibility(sql, args, visibilityUserId);
        sql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<RequestRecord>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static void AppendNullableIdComparisons(
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

        AppendNullableIdComparisons(
            predicates, alias, "api_key_id", filter.ApiKeyId, filter.ApiKeyIdNEQ,
            filter.ApiKeyIdIn, filter.ApiKeyIdNotIn, filter.ApiKeyIdIsNil,
            filter.ApiKeyIdNotNil, args);
        AppendNullableIdComparisons(
            predicates, alias, "trace_id", filter.TraceId, filter.TraceIdNEQ,
            filter.TraceIdIn, filter.TraceIdNotIn, filter.TraceIdIsNil,
            filter.TraceIdNotNil, args);
        AppendNullableIdComparisons(
            predicates, alias, "data_storage_id", filter.DataStorageId,
            filter.DataStorageIdNEQ, filter.DataStorageIdIn, filter.DataStorageIdNotIn,
            filter.DataStorageIdIsNil, filter.DataStorageIdNotNil, args);
        AppendNullableIdComparisons(
            predicates, alias, "channel_id", filter.ChannelId, filter.ChannelIdNEQ,
            filter.ChannelIdIn, filter.ChannelIdNotIn, filter.ChannelIdIsNil,
            filter.ChannelIdNotNil, args);

        TraceAdminStore.AppendStringComparisons(
            predicates, filter.Source, filter.SourceNEQ, filter.SourceIn,
            filter.SourceNotIn, null, null, null, null, null, null, null, null,
            null, alias, "source", args);
        TraceAdminStore.AppendStringComparisons(
            predicates, filter.ModelId, filter.ModelIdNEQ, filter.ModelIdIn,
            filter.ModelIdNotIn, filter.ModelIdGT, filter.ModelIdGTE, filter.ModelIdLT,
            filter.ModelIdLTE, filter.ModelIdContains, filter.ModelIdContainsFold,
            filter.ModelIdHasPrefix, filter.ModelIdHasSuffix, filter.ModelIdEqualFold,
            alias, "model_id", args);
        TraceAdminStore.AppendStringComparisons(
            predicates, filter.ReasoningEffort, filter.ReasoningEffortNEQ,
            filter.ReasoningEffortIn, filter.ReasoningEffortNotIn,
            filter.ReasoningEffortGT, filter.ReasoningEffortGTE,
            filter.ReasoningEffortLT, filter.ReasoningEffortLTE,
            filter.ReasoningEffortContains, filter.ReasoningEffortContainsFold,
            filter.ReasoningEffortHasPrefix, filter.ReasoningEffortHasSuffix,
            filter.ReasoningEffortEqualFold, alias, "reasoning_effort", args);

        if (filter.ReasoningEffortIsNil == true)
        {
            predicates.Add(alias + ".\"reasoning_effort\" IS NULL");
        }

        if (filter.ReasoningEffortNotNil == true)
        {
            predicates.Add(alias + ".\"reasoning_effort\" IS NOT NULL");
        }

        TraceAdminStore.AppendStringComparisons(
            predicates, filter.Format, filter.FormatNEQ, filter.FormatIn,
            filter.FormatNotIn, filter.FormatGT, filter.FormatGTE, filter.FormatLT,
            filter.FormatLTE, filter.FormatContains, filter.FormatContainsFold,
            filter.FormatHasPrefix, filter.FormatHasSuffix, filter.FormatEqualFold,
            alias, "format", args);
        TraceAdminStore.AppendStringComparisons(
            predicates, filter.ExternalId, filter.ExternalIdNEQ, filter.ExternalIdIn,
            filter.ExternalIdNotIn, filter.ExternalIdGT, filter.ExternalIdGTE,
            filter.ExternalIdLT, filter.ExternalIdLTE, filter.ExternalIdContains,
            filter.ExternalIdContainsFold, filter.ExternalIdHasPrefix,
            filter.ExternalIdHasSuffix, filter.ExternalIdEqualFold,
            alias, "external_id", args);

        if (filter.ExternalIdIsNil == true)
        {
            predicates.Add(alias + ".\"external_id\" IS NULL");
        }

        if (filter.ExternalIdNotNil == true)
        {
            predicates.Add(alias + ".\"external_id\" IS NOT NULL");
        }

        ConnectionSql.AppendBoolPredicates(
            predicates, alias, "stream", filter.Stream, filter.StreamNEQ, args);

        ConnectionSql.AppendStringPredicates(
            predicates, alias, "client_ip", filter.ClientIp, filter.ClientIpNEQ, filter.ClientIpIn,
            filter.ClientIpNotIn, filter.ClientIpGT, filter.ClientIpGTE, filter.ClientIpLT, filter.ClientIpLTE,
            filter.ClientIpContains, filter.ClientIpHasPrefix, filter.ClientIpHasSuffix,
            filter.ClientIpEqualFold, filter.ClientIpContainsFold, args);

        ConnectionSql.AppendStringPredicates(
            predicates, alias, "user_agent", filter.UserAgent, filter.UserAgentNEQ, filter.UserAgentIn,
            filter.UserAgentNotIn, filter.UserAgentGT, filter.UserAgentGTE, filter.UserAgentLT, filter.UserAgentLTE,
            filter.UserAgentContains, filter.UserAgentHasPrefix, filter.UserAgentHasSuffix,
            filter.UserAgentEqualFold, filter.UserAgentContainsFold, args);

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "metrics_latency_ms", filter.MetricsLatencyMs, filter.MetricsLatencyMsNEQ, filter.MetricsLatencyMsIn,
            filter.MetricsLatencyMsNotIn, filter.MetricsLatencyMsGT, filter.MetricsLatencyMsGTE, filter.MetricsLatencyMsLT, filter.MetricsLatencyMsLTE,
            filter.MetricsLatencyMsIsNil, filter.MetricsLatencyMsNotNil, args);

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "metrics_first_token_latency_ms", filter.MetricsFirstTokenLatencyMs, filter.MetricsFirstTokenLatencyMsNEQ, filter.MetricsFirstTokenLatencyMsIn,
            filter.MetricsFirstTokenLatencyMsNotIn, filter.MetricsFirstTokenLatencyMsGT, filter.MetricsFirstTokenLatencyMsGTE, filter.MetricsFirstTokenLatencyMsLT, filter.MetricsFirstTokenLatencyMsLTE,
            filter.MetricsFirstTokenLatencyMsIsNil, filter.MetricsFirstTokenLatencyMsNotNil, args);

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "metrics_reasoning_duration_ms", filter.MetricsReasoningDurationMs, filter.MetricsReasoningDurationMsNEQ, filter.MetricsReasoningDurationMsIn,
            filter.MetricsReasoningDurationMsNotIn, filter.MetricsReasoningDurationMsGT, filter.MetricsReasoningDurationMsGTE, filter.MetricsReasoningDurationMsLT, filter.MetricsReasoningDurationMsLTE,
            filter.MetricsReasoningDurationMsIsNil, filter.MetricsReasoningDurationMsNotNil, args);

        ConnectionSql.AppendBoolPredicates(
            predicates, alias, "content_saved", filter.ContentSaved, filter.ContentSavedNEQ, args);

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "content_storage_id", filter.ContentStorageId, filter.ContentStorageIdNEQ, filter.ContentStorageIdIn,
            filter.ContentStorageIdNotIn, filter.ContentStorageIdGT, filter.ContentStorageIdGTE, filter.ContentStorageIdLT, filter.ContentStorageIdLTE,
            filter.ContentStorageIdIsNil, filter.ContentStorageIdNotNil, args);

        ConnectionSql.AppendStringPredicates(
            predicates, alias, "content_storage_key", filter.ContentStorageKey, filter.ContentStorageKeyNEQ, filter.ContentStorageKeyIn,
            filter.ContentStorageKeyNotIn, filter.ContentStorageKeyGT, filter.ContentStorageKeyGTE, filter.ContentStorageKeyLT, filter.ContentStorageKeyLTE,
            filter.ContentStorageKeyContains, filter.ContentStorageKeyHasPrefix, filter.ContentStorageKeyHasSuffix,
            filter.ContentStorageKeyEqualFold, filter.ContentStorageKeyContainsFold, args);

        if (filter.ContentStorageKeyIsNil == true)
        {
            predicates.Add(alias + ".\"content_storage_key\" IS NULL");
        }

        if (filter.ContentStorageKeyNotNil == true)
        {
            predicates.Add(alias + ".\"content_storage_key\" IS NOT NULL");
        }

        TraceAdminStore.AppendTimeComparisons(
            predicates, filter.ContentSavedAt, filter.ContentSavedAtNEQ, filter.ContentSavedAtIn, filter.ContentSavedAtNotIn,
            filter.ContentSavedAtGT, filter.ContentSavedAtGTE, filter.ContentSavedAtLT, filter.ContentSavedAtLTE, alias,
            "content_saved_at", args);

        if (filter.ContentSavedAtIsNil == true)
        {
            predicates.Add(alias + ".\"content_saved_at\" IS NULL");
        }

        if (filter.ContentSavedAtNotNil == true)
        {
            predicates.Add(alias + ".\"content_saved_at\" IS NOT NULL");
        }

        if (filter.HasApiKey is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"api_keys\" rqak WHERE rqak.\"id\" = " + alias + ".\"api_key_id\")";
            predicates.Add(filter.HasApiKey.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasApiKeyWith is { Count: > 0 } hasApiKeyFilters)
        {
            var withPredicates = hasApiKeyFilters
                .Select(nested => ApiKeyStore.BuildFilter(nested, "rqak", args))
                .Where(static nested => nested is not null)
                .ToArray();
            var withCondition = ConnectionSql.Combine(withPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"api_keys\" rqak WHERE rqak.\"id\" = " + alias + ".\"api_key_id\""
                + (withCondition is null ? string.Empty : " AND " + withCondition)
                + ")");
        }

        if (filter.HasProject is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"projects\" rqpr WHERE rqpr.\"id\" = " + alias + ".\"project_id\")";
            predicates.Add(filter.HasProject.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasProjectWith is { Count: > 0 } hasProjectFilters)
        {
            var withPredicates = hasProjectFilters
                .Select(nested => ProjectStore.BuildFilter(nested, "rqpr", args))
                .Where(static nested => nested is not null)
                .ToArray();
            var withCondition = ConnectionSql.Combine(withPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"projects\" rqpr WHERE rqpr.\"id\" = " + alias + ".\"project_id\""
                + (withCondition is null ? string.Empty : " AND " + withCondition)
                + ")");
        }

        if (filter.HasTrace is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"traces\" rqtr WHERE rqtr.\"id\" = " + alias + ".\"trace_id\")";
            predicates.Add(filter.HasTrace.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasTraceWith is { Count: > 0 } hasTraceFilters)
        {
            var withPredicates = hasTraceFilters
                .Select(nested => TraceAdminStore.BuildFilter(nested, "rqtr", args))
                .Where(static nested => nested is not null)
                .ToArray();
            var withCondition = ConnectionSql.Combine(withPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"traces\" rqtr WHERE rqtr.\"id\" = " + alias + ".\"trace_id\""
                + (withCondition is null ? string.Empty : " AND " + withCondition)
                + ")");
        }

        if (filter.HasDataStorage is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"data_storages\" rqds WHERE rqds.\"id\" = " + alias + ".\"data_storage_id\")";
            predicates.Add(filter.HasDataStorage.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasDataStorageWith is { Count: > 0 } hasDataStorageFilters)
        {
            var withPredicates = hasDataStorageFilters
                .Select(nested => DataStorageStore.BuildFilter(nested, "rqds", args))
                .Where(static nested => nested is not null)
                .ToArray();
            var withCondition = ConnectionSql.Combine(withPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"data_storages\" rqds WHERE rqds.\"id\" = " + alias + ".\"data_storage_id\""
                + (withCondition is null ? string.Empty : " AND " + withCondition)
                + ")");
        }

        if (filter.HasExecutions is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"request_executions\" rqex WHERE rqex.\"request_id\" = " + alias + ".\"id\")";
            predicates.Add(filter.HasExecutions.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasExecutionsWith is { Count: > 0 } hasExecutionsFilters)
        {
            var withPredicates = hasExecutionsFilters
                .Select(nested => RequestExecutionStore.BuildFilter(nested, "rqex", args))
                .Where(static nested => nested is not null)
                .ToArray();
            var withCondition = ConnectionSql.Combine(withPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"request_executions\" rqex WHERE rqex.\"request_id\" = " + alias + ".\"id\""
                + (withCondition is null ? string.Empty : " AND " + withCondition)
                + ")");
        }

        if (filter.HasChannel is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"channels\" rqch WHERE rqch.\"id\" = " + alias + ".\"channel_id\")";
            predicates.Add(filter.HasChannel.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasChannelWith is { Count: > 0 } hasChannelFilters)
        {
            var withPredicates = hasChannelFilters
                .Select(nested => ChannelAdminStore.BuildFilter(nested, "rqch", args))
                .Where(static nested => nested is not null)
                .ToArray();
            var withCondition = ConnectionSql.Combine(withPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"channels\" rqch WHERE rqch.\"id\" = " + alias + ".\"channel_id\""
                + (withCondition is null ? string.Empty : " AND " + withCondition)
                + ")");
        }

        if (filter.HasUsageLogs is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"usage_logs\" rqul WHERE rqul.\"request_id\" = " + alias + ".\"id\")";
            predicates.Add(filter.HasUsageLogs.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasUsageLogsWith is { Count: > 0 } hasUsageLogsFilters)
        {
            var withPredicates = hasUsageLogsFilters
                .Select(nested => UsageLogStore.BuildFilter(nested, "rqul", args))
                .Where(static nested => nested is not null)
                .ToArray();
            var withCondition = ConnectionSql.Combine(withPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"usage_logs\" rqul WHERE rqul.\"request_id\" = " + alias + ".\"id\""
                + (withCondition is null ? string.Empty : " AND " + withCondition)
                + ")");
        }

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
