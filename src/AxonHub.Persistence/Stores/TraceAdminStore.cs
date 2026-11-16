using System.Text;
using Dapper;

namespace AxonHub.Persistence;

/// <summary>Ordering fields exposed by the reference TraceOrder type.</summary>
public enum TraceSortField
{
    Id,
    CreatedAt,
    UpdatedAt,
}

/// <summary>
/// Port of ent.TraceWhereInput: the pointer comparisons the admin pages can
/// send (id, timestamps, projectID, traceID string predicates, threadID,
/// status), the and/or/not combinators and the project / thread / requests edge
/// predicates.
/// </summary>
public sealed record TraceListFilter
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

    public string? TraceId { get; init; }

    public string? TraceIdNEQ { get; init; }

    public IReadOnlyList<string>? TraceIdIn { get; init; }

    public IReadOnlyList<string>? TraceIdNotIn { get; init; }

    public string? TraceIdGT { get; init; }

    public string? TraceIdGTE { get; init; }

    public string? TraceIdLT { get; init; }

    public string? TraceIdLTE { get; init; }

    public string? TraceIdContains { get; init; }

    public string? TraceIdContainsFold { get; init; }

    public string? TraceIdPrefix { get; init; }

    public string? TraceIdSuffix { get; init; }

    public string? TraceIdEqualFold { get; init; }

    public long? ThreadId { get; init; }

    public long? ThreadIdNEQ { get; init; }

    public IReadOnlyList<long>? ThreadIdIn { get; init; }

    public IReadOnlyList<long>? ThreadIdNotIn { get; init; }

    public bool? ThreadIdIsNil { get; init; }

    public bool? ThreadIdNotNil { get; init; }

    public string? Status { get; init; }

    public string? StatusNEQ { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    public IReadOnlyList<string>? StatusNotIn { get; init; }

    public bool? HasProject { get; init; }

    public IReadOnlyList<ProjectListFilter>? HasProjectWith { get; init; }

    public bool? HasThread { get; init; }

    public IReadOnlyList<ThreadListFilter>? HasThreadWith { get; init; }

    public bool? HasRequests { get; init; }

    public IReadOnlyList<RequestListFilter>? HasRequestsWith { get; init; }

    public TraceListFilter? Not { get; init; }

    public IReadOnlyList<TraceListFilter>? And { get; init; }

    public IReadOnlyList<TraceListFilter>? Or { get; init; }
}

/// <summary>Entgql pagination request for traces.</summary>
public sealed record TraceListQuery
{
    public TraceListFilter? Where { get; init; }

    /// <summary>
    /// The project predicate of the Trace query privacy policy. Null means the
    /// policy allowed the query without a filter.
    /// </summary>
    public long? ProjectScope { get; init; }

    /// <summary>
    /// The implicit <c>trace.ThreadIDEQ</c> predicate of the Thread.traces edge
    /// (entgql adds it before the user's where).
    /// </summary>
    public long? ThreadScope { get; init; }

    public TraceSortField SortField { get; init; } = TraceSortField.Id;

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }
}

/// <summary>
/// Persistence seam for the trace/thread management surface: the Trace schema's
/// entgql queries and status mutations plus the thread-scoped lookups the
/// reference TraceService and ThreadService run.
/// </summary>
public interface ITraceAdminStore
{
    /// <summary>
    /// ent <c>Trace.Query().Where(ID, [ProjectIDEQ]).Only(ctx)</c>.
    /// </summary>
    Task<TraceRecord?> GetByIdAsync(
        long id,
        long? projectScope,
        CancellationToken cancellationToken = default);

    Task<ListPage<TraceRecord>> ListAsync(
        TraceListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ent <c>Trace.UpdateOneID(id).Where(StatusEQ(from))
    /// .SetStatus(to).Save(ctx)</c> with the mutation policy's project
    /// predicate. Returns the affected row count.
    /// </summary>
    Task<int> UpdateStatusAsync(
        long id,
        long? projectScope,
        string fromStatus,
        string toStatus,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of TraceService.GetThreadFirstTrace: the oldest trace of a thread
    /// (created_at asc); no row yields null.
    /// </summary>
    Task<TraceRecord?> GetThreadFirstTraceAsync(
        long threadId,
        long? projectScope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the previous-trace lookup inside getPreviousTraceSpans: the
    /// newest trace of the same thread created before the current one.
    /// </summary>
    Task<TraceRecord?> GetPreviousTraceAsync(
        long threadId,
        long currentTraceId,
        DateTime currentCreatedAt,
        long? projectScope,
        CancellationToken cancellationToken = default);

    /// <summary>Port of Thread.ArchivedTracesCount.</summary>
    Task<long> CountArchivedByThreadAsync(
        long threadId,
        long? projectScope,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>traces</c> table's management surface. All
/// access goes through Dapper because the admin surface is read/status-write
/// only; the TimeMixin stamps <c>updated_at</c> on status updates.
/// </summary>
public sealed class TraceAdminStore(AxonHubDbContext db) : ITraceAdminStore
{
    internal const string Columns = """
        "id", "created_at", "updated_at", "trace_id", "status", "project_id",
        "thread_id"
        """;

    public async Task<TraceRecord?> GetByIdAsync(
        long id,
        long? projectScope,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT ").Append(Columns)
            .Append(" FROM \"traces\" WHERE \"id\" = ").Append(args.Add(id));
        if (projectScope is not null)
        {
            sql.Append(" AND \"project_id\" = ").Append(args.Add(projectScope.Value));
        }

        sql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<TraceRecord>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<ListPage<TraceRecord>> ListAsync(
        TraceListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var args = new SqlArguments();
        var where = BuildFilter(query.Where, "m", args);
        var conditions = new StringBuilder(" WHERE TRUE");
        if (query.ProjectScope is not null)
        {
            conditions.Append(" AND m.\"project_id\" = ").Append(args.Add(query.ProjectScope.Value));
        }

        if (query.ThreadScope is not null)
        {
            conditions.Append(" AND m.\"thread_id\" = ").Append(args.Add(query.ThreadScope.Value));
        }

        if (where is not null)
        {
            conditions.Append(" AND ").Append(where);
        }

        var sortColumn = query.SortField switch
        {
            TraceSortField.CreatedAt => "created_at",
            TraceSortField.UpdatedAt => "updated_at",
            _ => null,
        };
        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;
        const string from = " FROM \"traces\" m";
        var countSql = "SELECT COUNT(*) " + from + conditions + ";";
        var nodesSql = new StringBuilder("SELECT ");
        nodesSql.Append(string.Join(", ", Columns
            .Split(',')
            .Select(static column => "m." + column.Trim())));
        nodesSql.Append(from).Append(conditions);
        ConnectionSql.AppendCursor(
            nodesSql,
            "m",
            "id",
            sortColumn,
            query.After,
            query.Before,
            direction,
            args);
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
        var nodes = (await connection.QueryAsync<TraceRecord>(
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

        return new ListPage<TraceRecord>(nodes, hasNextPage, hasPreviousPage, count);
    }

    public async Task<int> UpdateStatusAsync(
        long id,
        long? projectScope,
        string fromStatus,
        string toStatus,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();

        // Generated UpdateOneID SET list: updated_at (TimeMixin), status.
        var sql = new StringBuilder("UPDATE \"traces\" SET \"updated_at\" = ")
            .Append(args.Add(DateTime.UtcNow))
            .Append(", \"status\" = ").Append(args.Add(toStatus))
            .Append(" WHERE \"id\" = ").Append(args.Add(id))
            .Append(" AND \"status\" = ").Append(args.Add(fromStatus));
        if (projectScope is not null)
        {
            sql.Append(" AND \"project_id\" = ").Append(args.Add(projectScope.Value));
        }

        sql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<TraceRecord?> GetThreadFirstTraceAsync(
        long threadId,
        long? projectScope,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT ").Append(Columns)
            .Append(" FROM \"traces\" WHERE \"thread_id\" = ").Append(args.Add(threadId));
        if (projectScope is not null)
        {
            sql.Append(" AND \"project_id\" = ").Append(args.Add(projectScope.Value));
        }

        sql.Append(" ORDER BY \"created_at\" ASC LIMIT 1;");

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<TraceRecord>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<TraceRecord?> GetPreviousTraceAsync(
        long threadId,
        long currentTraceId,
        DateTime currentCreatedAt,
        long? projectScope,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT ").Append(Columns)
            .Append(" FROM \"traces\" WHERE \"thread_id\" = ").Append(args.Add(threadId))
            .Append(" AND \"id\" <> ").Append(args.Add(currentTraceId))
            .Append(" AND \"created_at\" < ").Append(args.Add(currentCreatedAt));
        if (projectScope is not null)
        {
            sql.Append(" AND \"project_id\" = ").Append(args.Add(projectScope.Value));
        }

        sql.Append(" ORDER BY \"created_at\" DESC LIMIT 1;");

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<TraceRecord>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<long> CountArchivedByThreadAsync(
        long threadId,
        long? projectScope,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder(
                "SELECT COUNT(*)::bigint FROM \"traces\" WHERE \"thread_id\" = ")
            .Append(args.Add(threadId))
            .Append(" AND \"status\" = 'archived'");
        if (projectScope is not null)
        {
            sql.Append(" AND \"project_id\" = ").Append(args.Add(projectScope.Value));
        }

        sql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    internal static string? BuildFilter(
        TraceListFilter? filter,
        string alias,
        SqlArguments args)
    {
        if (filter is null)
        {
            return null;
        }

        var predicates = new List<string?>();
        AppendIdComparisons(predicates, filter, alias, args);
        AppendTimeComparisons(predicates, filter.CreatedAt, filter.CreatedAtNEQ,
            filter.CreatedAtIn, filter.CreatedAtNotIn, filter.CreatedAtGT,
            filter.CreatedAtGTE, filter.CreatedAtLT, filter.CreatedAtLTE,
            alias, "created_at", args);
        AppendTimeComparisons(predicates, filter.UpdatedAt, filter.UpdatedAtNEQ,
            filter.UpdatedAtIn, filter.UpdatedAtNotIn, filter.UpdatedAtGT,
            filter.UpdatedAtGTE, filter.UpdatedAtLT, filter.UpdatedAtLTE,
            alias, "updated_at", args);

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
            AppendIdNotIn(predicates, alias, "project_id", filter.ProjectIdNotIn, args);
        }

        AppendStringComparisons(predicates, filter.TraceId, filter.TraceIdNEQ,
            filter.TraceIdIn, filter.TraceIdNotIn,
            filter.TraceIdGT, filter.TraceIdGTE, filter.TraceIdLT, filter.TraceIdLTE,
            filter.TraceIdContains, filter.TraceIdContainsFold,
            filter.TraceIdPrefix, filter.TraceIdSuffix,
            filter.TraceIdEqualFold, alias, "trace_id", args);

        if (filter.ThreadId is not null)
        {
            predicates.Add(alias + ".\"thread_id\" = " + args.Add(filter.ThreadId.Value));
        }

        if (filter.ThreadIdNEQ is not null)
        {
            predicates.Add(alias + ".\"thread_id\" <> " + args.Add(filter.ThreadIdNEQ.Value));
        }

        if (filter.ThreadIdIn is not null)
        {
            ConnectionSql.AppendIdIn(predicates, alias, "thread_id", filter.ThreadIdIn, args);
        }

        if (filter.ThreadIdNotIn is not null)
        {
            AppendIdNotIn(predicates, alias, "thread_id", filter.ThreadIdNotIn, args);
        }

        if (filter.ThreadIdIsNil == true)
        {
            predicates.Add(alias + ".\"thread_id\" IS NULL");
        }

        if (filter.ThreadIdNotNil == true)
        {
            predicates.Add(alias + ".\"thread_id\" IS NOT NULL");
        }

        AppendStatusComparisons(predicates, filter.Status, filter.StatusNEQ,
            filter.StatusIn, filter.StatusNotIn, alias, args);

        if (filter.HasProject is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"projects\" pj2 WHERE pj2.\"id\" = "
                + alias + ".\"project_id\")";
            predicates.Add(filter.HasProject.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasProjectWith is { Count: > 0 } projectFilters)
        {
            // Generated sqlgraph.HasNeighborsWith ANDs every predicate inside a
            // single EXISTS and does not re-apply the target soft-delete predicate.
            var projectPredicates = projectFilters
                .Select(project => ProjectStore.BuildFilter(project, "pj2", args))
                .Where(static project => project is not null)
                .ToArray();
            var projectCondition = ConnectionSql.Combine(projectPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"projects\" pj2"
                + " WHERE pj2.\"id\" = " + alias + ".\"project_id\""
                + (projectCondition is null ? string.Empty : " AND " + projectCondition)
                + ")");
        }

        if (filter.HasThread is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"threads\" th2 WHERE th2.\"id\" = "
                + alias + ".\"thread_id\")";
            predicates.Add(filter.HasThread.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasThreadWith is { Count: > 0 } threadFilters)
        {
            var threadPredicates = threadFilters
                .Select(thread => ThreadAdminStore.BuildFilter(thread, "th2", args))
                .Where(static thread => thread is not null)
                .ToArray();
            var threadCondition = ConnectionSql.Combine(threadPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"threads\" th2"
                + " WHERE th2.\"id\" = " + alias + ".\"thread_id\""
                + (threadCondition is null ? string.Empty : " AND " + threadCondition)
                + ")");
        }

        if (filter.HasRequests is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"requests\" rq2 WHERE rq2.\"trace_id\" = "
                + alias + ".\"id\")";
            predicates.Add(filter.HasRequests.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasRequestsWith is { Count: > 0 } requestFilters)
        {
            var requestPredicates = requestFilters
                .Select(request => RequestSegmentStore.BuildFilter(request, "rq2", args))
                .Where(static request => request is not null)
                .ToArray();
            var requestCondition = ConnectionSql.Combine(requestPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"requests\" rq2"
                + " WHERE rq2.\"trace_id\" = " + alias + ".\"id\""
                + (requestCondition is null ? string.Empty : " AND " + requestCondition)
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
            // entgql only appends Or when len(i.Or) > 0; an empty list is
            // ignored rather than folded to FALSE.
            if (or.Length > 0)
            {
                predicates.Add("(" + string.Join(" OR ", or) + ")");
            }
        }

        return ConnectionSql.Combine(predicates);
    }

    private static void AppendIdComparisons(
        List<string?> predicates,
        TraceListFilter filter,
        string alias,
        SqlArguments args)
    {
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
            AppendIdNotIn(predicates, alias, "id", filter.IdNotIn, args);
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
    }

    internal static void AppendIdNotIn(
        List<string?> predicates,
        string alias,
        string column,
        IReadOnlyList<long> ids,
        SqlArguments args)
    {
        if (ids.Count == 0)
        {
            // Ent renders an empty NOT IN list as always true.
            predicates.Add("TRUE");
            return;
        }

        var parameters = ids.Select(value => args.Add(value)).ToArray();
        predicates.Add(
            alias + ".\"" + column + "\" NOT IN (" + string.Join(", ", parameters) + ")");
    }

    internal static void AppendTimeComparisons(
        List<string?> predicates,
        DateTime? value,
        DateTime? notEqual,
        IReadOnlyList<DateTime>? inList,
        IReadOnlyList<DateTime>? notInList,
        DateTime? greaterThan,
        DateTime? greaterThanOrEqual,
        DateTime? lessThan,
        DateTime? lessThanOrEqual,
        string alias,
        string column,
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
            predicates.Add(inList.Count == 0
                ? "FALSE"
                : name + " IN (" + string.Join(", ", inList.Select(v => args.Add(v))) + ")");
        }

        if (notInList is not null)
        {
            predicates.Add(notInList.Count == 0
                ? "TRUE"
                : name + " NOT IN ("
                    + string.Join(", ", notInList.Select(v => args.Add(v))) + ")");
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

    internal static void AppendStringComparisons(
        List<string?> predicates,
        string? value,
        string? notEqual,
        IReadOnlyList<string>? inList,
        IReadOnlyList<string>? notInList,
        string? greaterThan,
        string? greaterThanOrEqual,
        string? lessThan,
        string? lessThanOrEqual,
        string? contains,
        string? containsFold,
        string? prefix,
        string? suffix,
        string? equalFold,
        string alias,
        string column,
        SqlArguments args)
    {
        var name = alias + ".\"" + column + "\"";
        if (value is not null)
        {
            predicates.Add(name + " = " + args.Add(value));
        }

        if (notEqual is not null)
        {
            predicates.Add(name + " <> " + args.Add(notEqual));
        }

        if (inList is not null)
        {
            ConnectionSql.AppendStringIn(predicates, alias, column, inList, args);
        }

        if (notInList is not null)
        {
            predicates.Add(notInList.Count == 0
                ? "TRUE"
                : name + " NOT IN ("
                    + string.Join(", ", notInList.Select(v => args.Add(v))) + ")");
        }

        if (greaterThan is not null)
        {
            predicates.Add(name + " > " + args.Add(greaterThan));
        }

        if (greaterThanOrEqual is not null)
        {
            predicates.Add(name + " >= " + args.Add(greaterThanOrEqual));
        }

        if (lessThan is not null)
        {
            predicates.Add(name + " < " + args.Add(lessThan));
        }

        if (lessThanOrEqual is not null)
        {
            predicates.Add(name + " <= " + args.Add(lessThanOrEqual));
        }

        if (contains is not null)
        {
            predicates.Add(name + " LIKE " + args.Add("%" + contains + "%"));
        }

        if (containsFold is not null)
        {
            predicates.Add(
                "LOWER(" + name + ") LIKE LOWER(" + args.Add("%" + containsFold + "%") + ")");
        }

        if (prefix is not null)
        {
            predicates.Add(name + " LIKE " + args.Add(prefix + "%"));
        }

        if (suffix is not null)
        {
            predicates.Add(name + " LIKE " + args.Add("%" + suffix));
        }

        if (equalFold is not null)
        {
            predicates.Add(
                "LOWER(" + name + ") = LOWER(" + args.Add(equalFold) + ")");
        }
    }

    internal static void AppendStatusComparisons(
        List<string?> predicates,
        string? status,
        string? statusNotEqual,
        IReadOnlyList<string>? statusIn,
        IReadOnlyList<string>? statusNotIn,
        string alias,
        SqlArguments args)
    {
        var name = alias + ".\"status\"";
        if (status is not null)
        {
            predicates.Add(name + " = " + args.Add(status));
        }

        if (statusNotEqual is not null)
        {
            predicates.Add(name + " <> " + args.Add(statusNotEqual));
        }

        if (statusIn is not null)
        {
            ConnectionSql.AppendStringIn(predicates, alias, "status", statusIn, args);
        }

        if (statusNotIn is not null)
        {
            predicates.Add(statusNotIn.Count == 0
                ? "TRUE"
                : name + " NOT IN ("
                    + string.Join(", ", statusNotIn.Select(v => args.Add(v))) + ")");
        }
    }
}
