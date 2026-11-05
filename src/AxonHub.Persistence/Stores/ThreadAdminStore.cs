using System.Text;
using Dapper;

namespace AxonHub.Persistence;

/// <summary>Ordering fields exposed by the reference ThreadOrder type.</summary>
public enum ThreadSortField
{
    Id,
    CreatedAt,
    UpdatedAt,
}

/// <summary>
/// Subset of ent.ThreadWhereInput used by the management frontend plus the
/// pointer comparisons the admin pages can send (id, timestamps, projectID,
/// threadID string predicates, status and and/or/not). The edge predicates
/// (hasProjectWith, hasTracesWith) are not ported in this slice (C189).
/// </summary>
public sealed record ThreadListFilter
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

    public string? ThreadId { get; init; }

    public string? ThreadIdNEQ { get; init; }

    public IReadOnlyList<string>? ThreadIdIn { get; init; }

    public IReadOnlyList<string>? ThreadIdNotIn { get; init; }

    public string? ThreadIdGT { get; init; }

    public string? ThreadIdGTE { get; init; }

    public string? ThreadIdLT { get; init; }

    public string? ThreadIdLTE { get; init; }

    public string? ThreadIdContains { get; init; }

    public string? ThreadIdContainsFold { get; init; }

    public string? ThreadIdPrefix { get; init; }

    public string? ThreadIdSuffix { get; init; }

    public string? ThreadIdEqualFold { get; init; }

    public string? Status { get; init; }

    public string? StatusNEQ { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    public IReadOnlyList<string>? StatusNotIn { get; init; }

    public ThreadListFilter? Not { get; init; }

    public IReadOnlyList<ThreadListFilter>? And { get; init; }

    public IReadOnlyList<ThreadListFilter>? Or { get; init; }
}

/// <summary>Entgql pagination request for threads.</summary>
public sealed record ThreadListQuery
{
    public ThreadListFilter? Where { get; init; }

    public long? ProjectScope { get; init; }

    public ThreadSortField SortField { get; init; } = ThreadSortField.Id;

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }
}

/// <summary>Persistence seam for the thread management surface.</summary>
public interface IThreadAdminStore
{
    Task<ThreadRecord?> GetByIdAsync(
        long id,
        long? projectScope,
        CancellationToken cancellationToken = default);

    Task<ListPage<ThreadRecord>> ListAsync(
        ThreadListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of ThreadService.Archive/Unarchive/Retain/Unretain: only a row whose
    /// current status equals <paramref name="fromStatus"/> is updated, then the
    /// same transition cascades to that thread's traces of the same status.
    /// Runs in one transaction like <c>RunInTransaction</c>. The return value
    /// is the affected thread row count (0 skips the cascade).
    /// </summary>
    Task<int> UpdateStatusAndCascadeAsync(
        long id,
        long? projectScope,
        string fromStatus,
        string toStatus,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Marks a failure of the trace cascade inside
/// <see cref="ThreadAdminStore.UpdateStatusAndCascadeAsync"/> so the caller can
/// use the reference's distinct <c>failed to cascade &lt;op&gt; traces: </c>
/// wrap instead of the thread-update one.
/// </summary>
public sealed class TraceCascadeException(Exception innerException)
    : Exception(innerException.Message, innerException);

/// <summary>Store for the reference <c>threads</c> table's management surface.</summary>
public sealed class ThreadAdminStore(AxonHubDbContext db) : IThreadAdminStore
{
    internal const string Columns = """
        "id", "created_at", "updated_at", "thread_id", "status", "project_id"
        """;

    public async Task<ThreadRecord?> GetByIdAsync(
        long id,
        long? projectScope,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT ").Append(Columns)
            .Append(" FROM \"threads\" WHERE \"id\" = ").Append(args.Add(id));
        if (projectScope is not null)
        {
            sql.Append(" AND \"project_id\" = ").Append(args.Add(projectScope.Value));
        }

        sql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ThreadRecord>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<ListPage<ThreadRecord>> ListAsync(
        ThreadListQuery query,
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

        if (where is not null)
        {
            conditions.Append(" AND ").Append(where);
        }

        var sortColumn = query.SortField switch
        {
            ThreadSortField.CreatedAt => "created_at",
            ThreadSortField.UpdatedAt => "updated_at",
            _ => null,
        };
        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;
        const string from = " FROM \"threads\" m";
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
        var nodes = (await connection.QueryAsync<ThreadRecord>(
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

        return new ListPage<ThreadRecord>(nodes, hasNextPage, hasPreviousPage, count);
    }

    public async Task<int> UpdateStatusAndCascadeAsync(
        long id,
        long? projectScope,
        string fromStatus,
        string toStatus,
        CancellationToken cancellationToken = default)
    {
        // ent's RunInTransaction reuses an enclosing transaction and opens one
        // only for a standalone call.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            var args = new SqlArguments();
            var update = new StringBuilder("UPDATE \"threads\" SET \"updated_at\" = ")
                .Append(args.Add(DateTime.UtcNow))
                .Append(", \"status\" = ").Append(args.Add(toStatus))
                .Append(" WHERE \"id\" = ").Append(args.Add(id))
                .Append(" AND \"status\" = ").Append(args.Add(fromStatus));
            if (projectScope is not null)
            {
                update.Append(" AND \"project_id\" = ").Append(args.Add(projectScope.Value));
            }

            update.Append(';');

            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            var affected = await connection.ExecuteAsync(
                new CommandDefinition(
                    update.ToString(),
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

            if (affected > 0)
            {
                // tx.Trace.Update().Where(thread_id, status=from).SetStatus(to)
                var cascadeArgs = new SqlArguments();
                var cascade = new StringBuilder("UPDATE \"traces\" SET \"updated_at\" = ")
                    .Append(cascadeArgs.Add(DateTime.UtcNow))
                    .Append(", \"status\" = ").Append(cascadeArgs.Add(toStatus))
                    .Append(" WHERE \"thread_id\" = ").Append(cascadeArgs.Add(id))
                    .Append(" AND \"status\" = ").Append(cascadeArgs.Add(fromStatus));
                if (projectScope is not null)
                {
                    // The Trace mutation policy installs the same project
                    // predicate on the cascade Update (UserProjectScopeWriteRule).
                    cascade.Append(" AND \"project_id\" = ")
                        .Append(cascadeArgs.Add(projectScope.Value));
                }

                cascade.Append(';');

                try
                {
                    await connection.ExecuteAsync(
                        new CommandDefinition(
                            cascade.ToString(),
                            cascadeArgs.Parameters,
                            transaction: db.CurrentDbTransaction,
                            cancellationToken: cancellationToken)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new TraceCascadeException(ex);
                }
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return affected;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    internal static string? BuildFilter(
        ThreadListFilter? filter,
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

        TraceAdminStore.AppendStringComparisons(
            predicates, filter.ThreadId, filter.ThreadIdNEQ, filter.ThreadIdIn,
            filter.ThreadIdNotIn,
            filter.ThreadIdGT, filter.ThreadIdGTE, filter.ThreadIdLT, filter.ThreadIdLTE,
            filter.ThreadIdContains, filter.ThreadIdContainsFold,
            filter.ThreadIdPrefix, filter.ThreadIdSuffix, filter.ThreadIdEqualFold,
            alias, "thread_id", args);

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
