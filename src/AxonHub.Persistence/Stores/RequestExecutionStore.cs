using System.Text;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>Ordering fields of the entgql RequestExecutionOrder type.</summary>
public enum RequestExecutionSortField
{
    Id,
    CreatedAt,
    UpdatedAt,
}

/// <summary>
/// Subset of ent.RequestExecutionWhereInput used by the request detail page:
/// id, timestamps, project/request/channel/data-storage/status/model/format
/// and and/or/not. The edge predicates and the remaining string columns are
/// not ported in this slice (C192).
/// </summary>
public sealed record RequestExecutionListFilter
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

    public long? RequestId { get; init; }

    public long? RequestIdNEQ { get; init; }

    public IReadOnlyList<long>? RequestIdIn { get; init; }

    public IReadOnlyList<long>? RequestIdNotIn { get; init; }

    public long? ChannelId { get; init; }

    public long? ChannelIdNEQ { get; init; }

    public IReadOnlyList<long>? ChannelIdIn { get; init; }

    public IReadOnlyList<long>? ChannelIdNotIn { get; init; }

    public bool? ChannelIdIsNil { get; init; }

    public bool? ChannelIdNotNil { get; init; }

    public long? DataStorageId { get; init; }

    public long? DataStorageIdNEQ { get; init; }

    public IReadOnlyList<long>? DataStorageIdIn { get; init; }

    public IReadOnlyList<long>? DataStorageIdNotIn { get; init; }

    public bool? DataStorageIdIsNil { get; init; }

    public bool? DataStorageIdNotNil { get; init; }

    public string? Status { get; init; }

    public string? StatusNEQ { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    public IReadOnlyList<string>? StatusNotIn { get; init; }

    public string? ModelId { get; init; }

    public string? ModelIdNEQ { get; init; }

    public IReadOnlyList<string>? ModelIdIn { get; init; }

    public IReadOnlyList<string>? ModelIdNotIn { get; init; }

    public string? ModelIdContains { get; init; }

    public string? ModelIdContainsFold { get; init; }

    public string? ModelIdHasPrefix { get; init; }

    public string? ModelIdHasSuffix { get; init; }

    public string? ModelIdEqualFold { get; init; }

    public string? Format { get; init; }

    public string? FormatNEQ { get; init; }

    public IReadOnlyList<string>? FormatIn { get; init; }

    public IReadOnlyList<string>? FormatNotIn { get; init; }

    public string? ExternalId { get; init; }

    public string? ExternalIdNEQ { get; init; }

    public IReadOnlyList<string>? ExternalIdIn { get; init; }

    public IReadOnlyList<string>? ExternalIdNotIn { get; init; }

    public RequestExecutionListFilter? Not { get; init; }

    public IReadOnlyList<RequestExecutionListFilter>? And { get; init; }

    public IReadOnlyList<RequestExecutionListFilter>? Or { get; init; }
}

/// <summary>Execution connection query scoped by its parent request.</summary>
public sealed record RequestExecutionListQuery
{
    public long RequestScope { get; init; }

    public RequestExecutionListFilter? Where { get; init; }

    public RequestExecutionSortField SortField { get; init; } = RequestExecutionSortField.Id;

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }
}

/// <summary>Persistence seam for the reference <c>request_executions</c> table.</summary>
public interface IRequestExecutionStore
{
    Task<ListPage<RequestExecutionRecord>> ListAsync(
        RequestExecutionListQuery query,
        CancellationToken cancellationToken = default);

    Task<long> CreateAsync(RequestExecutionRecord execution, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RequestExecutionRecord>> ListByRequestIdAsync(
        long requestId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Newest first (biz/video.go loadTask: Order(ent.Desc(FieldID))), used by
    /// the video task flow to find the latest provider task association.
    /// </summary>
    Task<IReadOnlyList<RequestExecutionRecord>> ListByRequestIdDescAsync(
        long requestId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of UpdateRequestExecutionStatusWithMetrics: status, error details and
    /// metrics only. Response headers, body and external id captured earlier stay
    /// untouched, so a later status update cannot clear them.
    /// </summary>
    Task<bool> UpdateStatusAsync(
        long id,
        string status,
        long? responseStatusCode,
        string? upstreamModelId,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of UpdateRequestExecutionFinalized (response_status_code is never
    /// set here): status and external id are always written, the body is stored
    /// under the default storage policy, upstream model / error message only
    /// when non-empty, metrics only when supplied; headers and chunks go
    /// through their own updates.
    /// </summary>
    Task<bool> UpdateResultAsync(
        long id,
        string status,
        string? externalId,
        string? upstreamModelId,
        string? responseBody,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Port of UpdateRequestExecutionResponseHeaders: the upstream response headers only.</summary>
    Task<bool> UpdateResponseHeadersAsync(
        long id,
        string responseHeaders,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>request_executions</c> table. Writes go through
/// EF Core; reads use Dapper.
/// </summary>
public sealed class RequestExecutionStore(AxonHubDbContext db) : IRequestExecutionStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "project_id", "channel_api_key_index",
        "external_id", "model_id", "upstream_model_id", "format", "reasoning_effort",
        "channel_api_key_suffix", "request_body", "response_headers", "response_body",
        "response_chunks", "error_message", "response_status_code", "status", "stream",
        "metrics_latency_ms", "metrics_first_token_latency_ms",
        "metrics_reasoning_duration_ms", "request_headers", "request_url",
        "pass_through_applied", "channel_id", "data_storage_id", "request_id"
        """;

    public async Task<ListPage<RequestExecutionRecord>> ListAsync(
        RequestExecutionListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var args = new SqlArguments();
        var conditions = new StringBuilder(" WHERE m.\"request_id\" = ")
            .Append(args.Add(query.RequestScope));
        if (BuildFilter(query.Where, "m", args) is { } where)
        {
            conditions.Append(" AND ").Append(where);
        }

        var sortColumn = query.SortField switch
        {
            RequestExecutionSortField.CreatedAt => "created_at",
            RequestExecutionSortField.UpdatedAt => "updated_at",
            _ => null,
        };
        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;
        const string from = " FROM \"request_executions\" m";
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
        var nodes = (await connection.QueryAsync<RequestExecutionRecord>(
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

        return new ListPage<RequestExecutionRecord>(
            nodes, hasNextPage, hasPreviousPage, count);
    }

    private static string? BuildFilter(
        RequestExecutionListFilter? filter,
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
        AppendId(predicates, alias, "project_id", filter.ProjectId, filter.ProjectIdNEQ,
            filter.ProjectIdIn, filter.ProjectIdNotIn, null, null, null, null, args);
        AppendId(predicates, alias, "request_id", filter.RequestId, filter.RequestIdNEQ,
            filter.RequestIdIn, filter.RequestIdNotIn, null, null, null, null, args);
        AppendId(predicates, alias, "channel_id", filter.ChannelId, filter.ChannelIdNEQ,
            filter.ChannelIdIn, filter.ChannelIdNotIn, null, null, null, null, args);
        if (filter.ChannelIdIsNil == true)
        {
            predicates.Add(alias + ".\"channel_id\" IS NULL");
        }

        if (filter.ChannelIdNotNil == true)
        {
            predicates.Add(alias + ".\"channel_id\" IS NOT NULL");
        }

        AppendId(predicates, alias, "data_storage_id", filter.DataStorageId,
            filter.DataStorageIdNEQ, filter.DataStorageIdIn, filter.DataStorageIdNotIn,
            null, null, null, null, args);
        if (filter.DataStorageIdIsNil == true)
        {
            predicates.Add(alias + ".\"data_storage_id\" IS NULL");
        }

        if (filter.DataStorageIdNotNil == true)
        {
            predicates.Add(alias + ".\"data_storage_id\" IS NOT NULL");
        }

        TraceAdminStore.AppendStatusComparisons(
            predicates, filter.Status, filter.StatusNEQ, filter.StatusIn,
            filter.StatusNotIn, alias, args);
        TraceAdminStore.AppendStringComparisons(
            predicates, filter.ModelId, filter.ModelIdNEQ, filter.ModelIdIn,
            filter.ModelIdNotIn, null, null, null, null, filter.ModelIdContains,
            filter.ModelIdContainsFold, filter.ModelIdHasPrefix,
            filter.ModelIdHasSuffix, filter.ModelIdEqualFold, alias, "model_id", args);
        TraceAdminStore.AppendStringComparisons(
            predicates, filter.Format, filter.FormatNEQ, filter.FormatIn,
            filter.FormatNotIn, null, null, null, null, null, null, null, null,
            null, alias, "format", args);
        TraceAdminStore.AppendStringComparisons(
            predicates, filter.ExternalId, filter.ExternalIdNEQ, filter.ExternalIdIn,
            filter.ExternalIdNotIn, null, null, null, null, null, null, null, null,
            null, alias, "external_id", args);

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

    public async Task<long> CreateAsync(
        RequestExecutionRecord execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);

        db.RequestExecutions.Add(execution);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return execution.Id;
    }

    public async Task<IReadOnlyList<RequestExecutionRecord>> ListByRequestIdAsync(
        long requestId,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"request_executions\" WHERE \"request_id\" = @RequestId ORDER BY \"id\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<RequestExecutionRecord>(
            new CommandDefinition(
                sql,
                new { RequestId = requestId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<RequestExecutionRecord>> ListByRequestIdDescAsync(
        long requestId,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"request_executions\" WHERE \"request_id\" = @RequestId ORDER BY \"id\" DESC;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<RequestExecutionRecord>(
            new CommandDefinition(
                sql,
                new { RequestId = requestId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<bool> UpdateStatusAsync(
        long id,
        string status,
        long? responseStatusCode,
        string? upstreamModelId,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        var execution = await db.RequestExecutions
            .FirstOrDefaultAsync(execution => execution.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return false;
        }

        execution.Status = status;

        // Ent only writes these when the caller supplied a value; a later
        // status-only update must not clear metadata captured at finalization.
        if (!string.IsNullOrEmpty(upstreamModelId))
        {
            execution.UpstreamModelId = upstreamModelId;
        }

        if (!string.IsNullOrEmpty(errorMessage))
        {
            execution.ErrorMessage = errorMessage;
        }

        if (responseStatusCode is not null)
        {
            execution.ResponseStatusCode = responseStatusCode;
        }

        if (latencyMs is not null)
        {
            execution.MetricsLatencyMs = latencyMs;
        }

        if (firstTokenLatencyMs is not null)
        {
            execution.MetricsFirstTokenLatencyMs = firstTokenLatencyMs;
        }

        if (reasoningDurationMs is not null)
        {
            execution.MetricsReasoningDurationMs = reasoningDurationMs;
        }

        execution.UpdatedAt = updatedAt;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdateResultAsync(
        long id,
        string status,
        string? externalId,
        string? upstreamModelId,
        string? responseBody,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        var execution = await db.RequestExecutions
            .FirstOrDefaultAsync(execution => execution.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return false;
        }

        execution.Status = status;
        execution.ExternalId = externalId;

        if (!string.IsNullOrEmpty(upstreamModelId))
        {
            execution.UpstreamModelId = upstreamModelId;
        }

        if (!string.IsNullOrEmpty(errorMessage))
        {
            execution.ErrorMessage = errorMessage;
        }

        // Headers and chunks are written by their own updates (and chunks only
        // when the storage policy enables them, C020), so finalization cannot
        // clear them.
        execution.ResponseBody = responseBody;

        if (latencyMs is not null)
        {
            execution.MetricsLatencyMs = latencyMs;
        }

        if (firstTokenLatencyMs is not null)
        {
            execution.MetricsFirstTokenLatencyMs = firstTokenLatencyMs;
        }

        if (reasoningDurationMs is not null)
        {
            execution.MetricsReasoningDurationMs = reasoningDurationMs;
        }

        execution.UpdatedAt = updatedAt;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> UpdateResponseHeadersAsync(
        long id,
        string responseHeaders,
        DateTime updatedAt,
        CancellationToken cancellationToken = default)
    {
        var execution = await db.RequestExecutions
            .FirstOrDefaultAsync(execution => execution.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (execution is null)
        {
            return false;
        }

        execution.ResponseHeaders = responseHeaders;
        execution.UpdatedAt = updatedAt;

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }
}
