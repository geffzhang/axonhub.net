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

    public string? ExternalIdGT { get; init; }

    public string? ExternalIdGTE { get; init; }

    public string? ExternalIdLT { get; init; }

    public string? ExternalIdLTE { get; init; }

    public string? ExternalIdContains { get; init; }

    public string? ExternalIdHasPrefix { get; init; }

    public string? ExternalIdHasSuffix { get; init; }

    public bool? ExternalIdIsNil { get; init; }

    public bool? ExternalIdNotNil { get; init; }

    public string? ExternalIdEqualFold { get; init; }

    public string? ExternalIdContainsFold { get; init; }

    public string? ModelIdGT { get; init; }

    public string? ModelIdGTE { get; init; }

    public string? ModelIdLT { get; init; }

    public string? ModelIdLTE { get; init; }

    public string? UpstreamModelId { get; init; }

    public string? UpstreamModelIdNEQ { get; init; }

    public IReadOnlyList<string>? UpstreamModelIdIn { get; init; }

    public IReadOnlyList<string>? UpstreamModelIdNotIn { get; init; }

    public string? UpstreamModelIdGT { get; init; }

    public string? UpstreamModelIdGTE { get; init; }

    public string? UpstreamModelIdLT { get; init; }

    public string? UpstreamModelIdLTE { get; init; }

    public string? UpstreamModelIdContains { get; init; }

    public string? UpstreamModelIdHasPrefix { get; init; }

    public string? UpstreamModelIdHasSuffix { get; init; }

    public bool? UpstreamModelIdIsNil { get; init; }

    public bool? UpstreamModelIdNotNil { get; init; }

    public string? UpstreamModelIdEqualFold { get; init; }

    public string? UpstreamModelIdContainsFold { get; init; }

    public string? FormatGT { get; init; }

    public string? FormatGTE { get; init; }

    public string? FormatLT { get; init; }

    public string? FormatLTE { get; init; }

    public string? FormatContains { get; init; }

    public string? FormatHasPrefix { get; init; }

    public string? FormatHasSuffix { get; init; }

    public string? FormatEqualFold { get; init; }

    public string? FormatContainsFold { get; init; }

    public string? ReasoningEffort { get; init; }

    public string? ReasoningEffortNEQ { get; init; }

    public IReadOnlyList<string>? ReasoningEffortIn { get; init; }

    public IReadOnlyList<string>? ReasoningEffortNotIn { get; init; }

    public string? ReasoningEffortGT { get; init; }

    public string? ReasoningEffortGTE { get; init; }

    public string? ReasoningEffortLT { get; init; }

    public string? ReasoningEffortLTE { get; init; }

    public string? ReasoningEffortContains { get; init; }

    public string? ReasoningEffortHasPrefix { get; init; }

    public string? ReasoningEffortHasSuffix { get; init; }

    public bool? ReasoningEffortIsNil { get; init; }

    public bool? ReasoningEffortNotNil { get; init; }

    public string? ReasoningEffortEqualFold { get; init; }

    public string? ReasoningEffortContainsFold { get; init; }

    public string? ErrorMessage { get; init; }

    public string? ErrorMessageNEQ { get; init; }

    public IReadOnlyList<string>? ErrorMessageIn { get; init; }

    public IReadOnlyList<string>? ErrorMessageNotIn { get; init; }

    public string? ErrorMessageGT { get; init; }

    public string? ErrorMessageGTE { get; init; }

    public string? ErrorMessageLT { get; init; }

    public string? ErrorMessageLTE { get; init; }

    public string? ErrorMessageContains { get; init; }

    public string? ErrorMessageHasPrefix { get; init; }

    public string? ErrorMessageHasSuffix { get; init; }

    public bool? ErrorMessageIsNil { get; init; }

    public bool? ErrorMessageNotNil { get; init; }

    public string? ErrorMessageEqualFold { get; init; }

    public string? ErrorMessageContainsFold { get; init; }

    public long? ResponseStatusCode { get; init; }

    public long? ResponseStatusCodeNEQ { get; init; }

    public IReadOnlyList<long>? ResponseStatusCodeIn { get; init; }

    public IReadOnlyList<long>? ResponseStatusCodeNotIn { get; init; }

    public long? ResponseStatusCodeGT { get; init; }

    public long? ResponseStatusCodeGTE { get; init; }

    public long? ResponseStatusCodeLT { get; init; }

    public long? ResponseStatusCodeLTE { get; init; }

    public bool? ResponseStatusCodeIsNil { get; init; }

    public bool? ResponseStatusCodeNotNil { get; init; }

    public bool? Stream { get; init; }

    public bool? StreamNEQ { get; init; }

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

    public string? RequestUrl { get; init; }

    public string? RequestUrlNEQ { get; init; }

    public IReadOnlyList<string>? RequestUrlIn { get; init; }

    public IReadOnlyList<string>? RequestUrlNotIn { get; init; }

    public string? RequestUrlGT { get; init; }

    public string? RequestUrlGTE { get; init; }

    public string? RequestUrlLT { get; init; }

    public string? RequestUrlLTE { get; init; }

    public string? RequestUrlContains { get; init; }

    public string? RequestUrlHasPrefix { get; init; }

    public string? RequestUrlHasSuffix { get; init; }

    public bool? RequestUrlIsNil { get; init; }

    public bool? RequestUrlNotNil { get; init; }

    public string? RequestUrlEqualFold { get; init; }

    public string? RequestUrlContainsFold { get; init; }

    public bool? PassThroughApplied { get; init; }

    public bool? PassThroughAppliedNEQ { get; init; }

    public bool? HasRequest { get; init; }

    public IReadOnlyList<RequestListFilter>? HasRequestWith { get; init; }

    public bool? HasChannel { get; init; }

    public IReadOnlyList<ChannelListFilter>? HasChannelWith { get; init; }

    public bool? HasDataStorage { get; init; }

    public IReadOnlyList<DataStorageListFilter>? HasDataStorageWith { get; init; }
}

/// <summary>
/// Execution connection query. <see cref="RequestScope"/> is set by
/// Request.executions; the Channel.executions / DataStorage.executions edges
/// leave it null and carry the foreign key in <see cref="Where"/>.
/// </summary>
public sealed record RequestExecutionListQuery
{
    public long? RequestScope { get; init; }

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

    Task<RequestExecutionRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

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
    /// set here): status and external id are always written, the body only when
    /// <paramref name="setResponseBody"/> is set (the storage policy is checked,
    /// and an external write records its marker), upstream model / error
    /// message only when non-empty, metrics only when supplied; headers and
    /// chunks go through their own updates.
    /// </summary>
    Task<bool> UpdateResultAsync(
        long id,
        string status,
        string? externalId,
        string? upstreamModelId,
        string? responseBody,
        bool setResponseBody,
        string? errorMessage,
        long? latencyMs,
        long? firstTokenLatencyMs,
        long? reasoningDurationMs,
        DateTime updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of SaveRequestExecutionChunks' column write: the marshalled chunk
    /// array (or the external marker) with a fresh updated_at.
    /// </summary>
    Task<bool> UpdateResponseChunksAsync(
        long id,
        string responseChunks,
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
        var conditions = new StringBuilder(" WHERE TRUE");
        if (query.RequestScope is not null)
        {
            conditions.Append(" AND m.\"request_id\" = ")
                .Append(args.Add(query.RequestScope.Value));
        }

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

    internal static string? BuildFilter(
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

        ConnectionSql.AppendStringPredicates(
            predicates, alias, "external_id", filter.ExternalId, filter.ExternalIdNEQ, filter.ExternalIdIn,
            filter.ExternalIdNotIn, filter.ExternalIdGT, filter.ExternalIdGTE, filter.ExternalIdLT, filter.ExternalIdLTE,
            filter.ExternalIdContains, filter.ExternalIdHasPrefix, filter.ExternalIdHasSuffix,
            filter.ExternalIdEqualFold, filter.ExternalIdContainsFold, args);

        if (filter.ExternalIdIsNil == true)
        {
            predicates.Add(alias + ".\"external_id\" IS NULL");
        }

        if (filter.ExternalIdNotNil == true)
        {
            predicates.Add(alias + ".\"external_id\" IS NOT NULL");
        }

        ConnectionSql.AppendStringPredicates(
            predicates, alias, "model_id", filter.ModelId, filter.ModelIdNEQ, filter.ModelIdIn,
            filter.ModelIdNotIn, filter.ModelIdGT, filter.ModelIdGTE, filter.ModelIdLT, filter.ModelIdLTE,
            filter.ModelIdContains, filter.ModelIdHasPrefix, filter.ModelIdHasSuffix,
            filter.ModelIdEqualFold, filter.ModelIdContainsFold, args);

        ConnectionSql.AppendStringPredicates(
            predicates, alias, "upstream_model_id", filter.UpstreamModelId, filter.UpstreamModelIdNEQ, filter.UpstreamModelIdIn,
            filter.UpstreamModelIdNotIn, filter.UpstreamModelIdGT, filter.UpstreamModelIdGTE, filter.UpstreamModelIdLT, filter.UpstreamModelIdLTE,
            filter.UpstreamModelIdContains, filter.UpstreamModelIdHasPrefix, filter.UpstreamModelIdHasSuffix,
            filter.UpstreamModelIdEqualFold, filter.UpstreamModelIdContainsFold, args);

        if (filter.UpstreamModelIdIsNil == true)
        {
            predicates.Add(alias + ".\"upstream_model_id\" IS NULL");
        }

        if (filter.UpstreamModelIdNotNil == true)
        {
            predicates.Add(alias + ".\"upstream_model_id\" IS NOT NULL");
        }

        ConnectionSql.AppendStringPredicates(
            predicates, alias, "format", filter.Format, filter.FormatNEQ, filter.FormatIn,
            filter.FormatNotIn, filter.FormatGT, filter.FormatGTE, filter.FormatLT, filter.FormatLTE,
            filter.FormatContains, filter.FormatHasPrefix, filter.FormatHasSuffix,
            filter.FormatEqualFold, filter.FormatContainsFold, args);

        ConnectionSql.AppendStringPredicates(
            predicates, alias, "reasoning_effort", filter.ReasoningEffort, filter.ReasoningEffortNEQ, filter.ReasoningEffortIn,
            filter.ReasoningEffortNotIn, filter.ReasoningEffortGT, filter.ReasoningEffortGTE, filter.ReasoningEffortLT, filter.ReasoningEffortLTE,
            filter.ReasoningEffortContains, filter.ReasoningEffortHasPrefix, filter.ReasoningEffortHasSuffix,
            filter.ReasoningEffortEqualFold, filter.ReasoningEffortContainsFold, args);

        if (filter.ReasoningEffortIsNil == true)
        {
            predicates.Add(alias + ".\"reasoning_effort\" IS NULL");
        }

        if (filter.ReasoningEffortNotNil == true)
        {
            predicates.Add(alias + ".\"reasoning_effort\" IS NOT NULL");
        }

        ConnectionSql.AppendStringPredicates(
            predicates, alias, "error_message", filter.ErrorMessage, filter.ErrorMessageNEQ, filter.ErrorMessageIn,
            filter.ErrorMessageNotIn, filter.ErrorMessageGT, filter.ErrorMessageGTE, filter.ErrorMessageLT, filter.ErrorMessageLTE,
            filter.ErrorMessageContains, filter.ErrorMessageHasPrefix, filter.ErrorMessageHasSuffix,
            filter.ErrorMessageEqualFold, filter.ErrorMessageContainsFold, args);

        if (filter.ErrorMessageIsNil == true)
        {
            predicates.Add(alias + ".\"error_message\" IS NULL");
        }

        if (filter.ErrorMessageNotNil == true)
        {
            predicates.Add(alias + ".\"error_message\" IS NOT NULL");
        }

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "response_status_code", filter.ResponseStatusCode, filter.ResponseStatusCodeNEQ, filter.ResponseStatusCodeIn,
            filter.ResponseStatusCodeNotIn, filter.ResponseStatusCodeGT, filter.ResponseStatusCodeGTE, filter.ResponseStatusCodeLT, filter.ResponseStatusCodeLTE,
            filter.ResponseStatusCodeIsNil, filter.ResponseStatusCodeNotNil, args);

        ConnectionSql.AppendBoolPredicates(
            predicates, alias, "stream", filter.Stream, filter.StreamNEQ, args);

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

        ConnectionSql.AppendStringPredicates(
            predicates, alias, "request_url", filter.RequestUrl, filter.RequestUrlNEQ, filter.RequestUrlIn,
            filter.RequestUrlNotIn, filter.RequestUrlGT, filter.RequestUrlGTE, filter.RequestUrlLT, filter.RequestUrlLTE,
            filter.RequestUrlContains, filter.RequestUrlHasPrefix, filter.RequestUrlHasSuffix,
            filter.RequestUrlEqualFold, filter.RequestUrlContainsFold, args);

        if (filter.RequestUrlIsNil == true)
        {
            predicates.Add(alias + ".\"request_url\" IS NULL");
        }

        if (filter.RequestUrlNotNil == true)
        {
            predicates.Add(alias + ".\"request_url\" IS NOT NULL");
        }

        ConnectionSql.AppendBoolPredicates(
            predicates, alias, "pass_through_applied", filter.PassThroughApplied, filter.PassThroughAppliedNEQ, args);

        if (filter.HasRequest is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"requests\" exrq WHERE exrq.\"id\" = " + alias + ".\"request_id\")";
            predicates.Add(filter.HasRequest.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasRequestWith is { Count: > 0 } hasRequestFilters)
        {
            var withPredicates = hasRequestFilters
                .Select(nested => RequestSegmentStore.BuildFilter(nested, "exrq", args))
                .Where(static nested => nested is not null)
                .ToArray();
            var withCondition = ConnectionSql.Combine(withPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"requests\" exrq WHERE exrq.\"id\" = " + alias + ".\"request_id\""
                + (withCondition is null ? string.Empty : " AND " + withCondition)
                + ")");
        }

        if (filter.HasChannel is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"channels\" exch WHERE exch.\"id\" = " + alias + ".\"channel_id\")";
            predicates.Add(filter.HasChannel.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasChannelWith is { Count: > 0 } hasChannelFilters)
        {
            var withPredicates = hasChannelFilters
                .Select(nested => ChannelAdminStore.BuildFilter(nested, "exch", args))
                .Where(static nested => nested is not null)
                .ToArray();
            var withCondition = ConnectionSql.Combine(withPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"channels\" exch WHERE exch.\"id\" = " + alias + ".\"channel_id\""
                + (withCondition is null ? string.Empty : " AND " + withCondition)
                + ")");
        }

        if (filter.HasDataStorage is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"data_storages\" exds WHERE exds.\"id\" = " + alias + ".\"data_storage_id\")";
            predicates.Add(filter.HasDataStorage.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasDataStorageWith is { Count: > 0 } hasDataStorageFilters)
        {
            var withPredicates = hasDataStorageFilters
                .Select(nested => DataStorageStore.BuildFilter(nested, "exds", args))
                .Where(static nested => nested is not null)
                .ToArray();
            var withCondition = ConnectionSql.Combine(withPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"data_storages\" exds WHERE exds.\"id\" = " + alias + ".\"data_storage_id\""
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

    public async Task<RequestExecutionRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns + " FROM \"request_executions\" WHERE \"id\" = @Id;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<RequestExecutionRecord>(
            new CommandDefinition(
                sql,
                new { Id = id },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
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
        bool setResponseBody,
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

        // Ent only sets the column when the caller supplied the field: the
        // storage policy may disable it, and a failed external write leaves the
        // body untouched. Headers and chunks have their own updates.
        if (setResponseBody)
        {
            execution.ResponseBody = responseBody;
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

    public async Task<bool> UpdateResponseChunksAsync(
        long id,
        string responseChunks,
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

        execution.ResponseChunks = responseChunks;
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
