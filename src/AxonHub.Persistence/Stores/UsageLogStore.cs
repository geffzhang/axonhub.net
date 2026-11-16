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

    public long? ApiKeyIdGT { get; init; }

    public long? ApiKeyIdGTE { get; init; }

    public long? ApiKeyIdLT { get; init; }

    public long? ApiKeyIdLTE { get; init; }

    public string? ModelIdGT { get; init; }

    public string? ModelIdGTE { get; init; }

    public string? ModelIdLT { get; init; }

    public string? ModelIdLTE { get; init; }

    public string? ModelIdHasPrefix { get; init; }

    public string? ModelIdHasSuffix { get; init; }

    public string? ModelIdEqualFold { get; init; }

    public long? PromptTokens { get; init; }

    public long? PromptTokensNEQ { get; init; }

    public IReadOnlyList<long>? PromptTokensIn { get; init; }

    public IReadOnlyList<long>? PromptTokensNotIn { get; init; }

    public long? PromptTokensGT { get; init; }

    public long? PromptTokensGTE { get; init; }

    public long? PromptTokensLT { get; init; }

    public long? PromptTokensLTE { get; init; }

    public long? CompletionTokens { get; init; }

    public long? CompletionTokensNEQ { get; init; }

    public IReadOnlyList<long>? CompletionTokensIn { get; init; }

    public IReadOnlyList<long>? CompletionTokensNotIn { get; init; }

    public long? CompletionTokensGT { get; init; }

    public long? CompletionTokensGTE { get; init; }

    public long? CompletionTokensLT { get; init; }

    public long? CompletionTokensLTE { get; init; }

    public long? TotalTokens { get; init; }

    public long? TotalTokensNEQ { get; init; }

    public IReadOnlyList<long>? TotalTokensIn { get; init; }

    public IReadOnlyList<long>? TotalTokensNotIn { get; init; }

    public long? TotalTokensGT { get; init; }

    public long? TotalTokensGTE { get; init; }

    public long? TotalTokensLT { get; init; }

    public long? TotalTokensLTE { get; init; }

    public long? PromptAudioTokens { get; init; }

    public long? PromptAudioTokensNEQ { get; init; }

    public IReadOnlyList<long>? PromptAudioTokensIn { get; init; }

    public IReadOnlyList<long>? PromptAudioTokensNotIn { get; init; }

    public long? PromptAudioTokensGT { get; init; }

    public long? PromptAudioTokensGTE { get; init; }

    public long? PromptAudioTokensLT { get; init; }

    public long? PromptAudioTokensLTE { get; init; }

    public bool? PromptAudioTokensIsNil { get; init; }

    public bool? PromptAudioTokensNotNil { get; init; }

    public long? PromptCachedTokens { get; init; }

    public long? PromptCachedTokensNEQ { get; init; }

    public IReadOnlyList<long>? PromptCachedTokensIn { get; init; }

    public IReadOnlyList<long>? PromptCachedTokensNotIn { get; init; }

    public long? PromptCachedTokensGT { get; init; }

    public long? PromptCachedTokensGTE { get; init; }

    public long? PromptCachedTokensLT { get; init; }

    public long? PromptCachedTokensLTE { get; init; }

    public bool? PromptCachedTokensIsNil { get; init; }

    public bool? PromptCachedTokensNotNil { get; init; }

    public long? PromptWriteCachedTokens { get; init; }

    public long? PromptWriteCachedTokensNEQ { get; init; }

    public IReadOnlyList<long>? PromptWriteCachedTokensIn { get; init; }

    public IReadOnlyList<long>? PromptWriteCachedTokensNotIn { get; init; }

    public long? PromptWriteCachedTokensGT { get; init; }

    public long? PromptWriteCachedTokensGTE { get; init; }

    public long? PromptWriteCachedTokensLT { get; init; }

    public long? PromptWriteCachedTokensLTE { get; init; }

    public bool? PromptWriteCachedTokensIsNil { get; init; }

    public bool? PromptWriteCachedTokensNotNil { get; init; }

    public long? PromptWriteCachedTokens5m { get; init; }

    public long? PromptWriteCachedTokens5mNEQ { get; init; }

    public IReadOnlyList<long>? PromptWriteCachedTokens5mIn { get; init; }

    public IReadOnlyList<long>? PromptWriteCachedTokens5mNotIn { get; init; }

    public long? PromptWriteCachedTokens5mGT { get; init; }

    public long? PromptWriteCachedTokens5mGTE { get; init; }

    public long? PromptWriteCachedTokens5mLT { get; init; }

    public long? PromptWriteCachedTokens5mLTE { get; init; }

    public bool? PromptWriteCachedTokens5mIsNil { get; init; }

    public bool? PromptWriteCachedTokens5mNotNil { get; init; }

    public long? PromptWriteCachedTokens1h { get; init; }

    public long? PromptWriteCachedTokens1hNEQ { get; init; }

    public IReadOnlyList<long>? PromptWriteCachedTokens1hIn { get; init; }

    public IReadOnlyList<long>? PromptWriteCachedTokens1hNotIn { get; init; }

    public long? PromptWriteCachedTokens1hGT { get; init; }

    public long? PromptWriteCachedTokens1hGTE { get; init; }

    public long? PromptWriteCachedTokens1hLT { get; init; }

    public long? PromptWriteCachedTokens1hLTE { get; init; }

    public bool? PromptWriteCachedTokens1hIsNil { get; init; }

    public bool? PromptWriteCachedTokens1hNotNil { get; init; }

    public long? CompletionAudioTokens { get; init; }

    public long? CompletionAudioTokensNEQ { get; init; }

    public IReadOnlyList<long>? CompletionAudioTokensIn { get; init; }

    public IReadOnlyList<long>? CompletionAudioTokensNotIn { get; init; }

    public long? CompletionAudioTokensGT { get; init; }

    public long? CompletionAudioTokensGTE { get; init; }

    public long? CompletionAudioTokensLT { get; init; }

    public long? CompletionAudioTokensLTE { get; init; }

    public bool? CompletionAudioTokensIsNil { get; init; }

    public bool? CompletionAudioTokensNotNil { get; init; }

    public long? CompletionReasoningTokens { get; init; }

    public long? CompletionReasoningTokensNEQ { get; init; }

    public IReadOnlyList<long>? CompletionReasoningTokensIn { get; init; }

    public IReadOnlyList<long>? CompletionReasoningTokensNotIn { get; init; }

    public long? CompletionReasoningTokensGT { get; init; }

    public long? CompletionReasoningTokensGTE { get; init; }

    public long? CompletionReasoningTokensLT { get; init; }

    public long? CompletionReasoningTokensLTE { get; init; }

    public bool? CompletionReasoningTokensIsNil { get; init; }

    public bool? CompletionReasoningTokensNotNil { get; init; }

    public long? CompletionAcceptedPredictionTokens { get; init; }

    public long? CompletionAcceptedPredictionTokensNEQ { get; init; }

    public IReadOnlyList<long>? CompletionAcceptedPredictionTokensIn { get; init; }

    public IReadOnlyList<long>? CompletionAcceptedPredictionTokensNotIn { get; init; }

    public long? CompletionAcceptedPredictionTokensGT { get; init; }

    public long? CompletionAcceptedPredictionTokensGTE { get; init; }

    public long? CompletionAcceptedPredictionTokensLT { get; init; }

    public long? CompletionAcceptedPredictionTokensLTE { get; init; }

    public bool? CompletionAcceptedPredictionTokensIsNil { get; init; }

    public bool? CompletionAcceptedPredictionTokensNotNil { get; init; }

    public long? CompletionRejectedPredictionTokens { get; init; }

    public long? CompletionRejectedPredictionTokensNEQ { get; init; }

    public IReadOnlyList<long>? CompletionRejectedPredictionTokensIn { get; init; }

    public IReadOnlyList<long>? CompletionRejectedPredictionTokensNotIn { get; init; }

    public long? CompletionRejectedPredictionTokensGT { get; init; }

    public long? CompletionRejectedPredictionTokensGTE { get; init; }

    public long? CompletionRejectedPredictionTokensLT { get; init; }

    public long? CompletionRejectedPredictionTokensLTE { get; init; }

    public bool? CompletionRejectedPredictionTokensIsNil { get; init; }

    public bool? CompletionRejectedPredictionTokensNotNil { get; init; }

    public string? FormatGT { get; init; }

    public string? FormatGTE { get; init; }

    public string? FormatLT { get; init; }

    public string? FormatLTE { get; init; }

    public string? FormatContains { get; init; }

    public string? FormatHasPrefix { get; init; }

    public string? FormatHasSuffix { get; init; }

    public string? FormatEqualFold { get; init; }

    public string? FormatContainsFold { get; init; }

    public double? TotalCost { get; init; }

    public double? TotalCostNEQ { get; init; }

    public IReadOnlyList<double>? TotalCostIn { get; init; }

    public IReadOnlyList<double>? TotalCostNotIn { get; init; }

    public double? TotalCostGT { get; init; }

    public double? TotalCostGTE { get; init; }

    public double? TotalCostLT { get; init; }

    public double? TotalCostLTE { get; init; }

    public bool? TotalCostIsNil { get; init; }

    public bool? TotalCostNotNil { get; init; }

    public string? CostPriceReferenceId { get; init; }

    public string? CostPriceReferenceIdNEQ { get; init; }

    public IReadOnlyList<string>? CostPriceReferenceIdIn { get; init; }

    public IReadOnlyList<string>? CostPriceReferenceIdNotIn { get; init; }

    public string? CostPriceReferenceIdGT { get; init; }

    public string? CostPriceReferenceIdGTE { get; init; }

    public string? CostPriceReferenceIdLT { get; init; }

    public string? CostPriceReferenceIdLTE { get; init; }

    public string? CostPriceReferenceIdContains { get; init; }

    public string? CostPriceReferenceIdHasPrefix { get; init; }

    public string? CostPriceReferenceIdHasSuffix { get; init; }

    public bool? CostPriceReferenceIdIsNil { get; init; }

    public bool? CostPriceReferenceIdNotNil { get; init; }

    public string? CostPriceReferenceIdEqualFold { get; init; }

    public string? CostPriceReferenceIdContainsFold { get; init; }

    public bool? HasRequest { get; init; }

    public IReadOnlyList<RequestListFilter>? HasRequestWith { get; init; }

    public bool? HasProject { get; init; }

    public IReadOnlyList<ProjectListFilter>? HasProjectWith { get; init; }

    public bool? HasChannel { get; init; }

    public IReadOnlyList<ChannelListFilter>? HasChannelWith { get; init; }
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

    /// <summary>
    /// Port of ProviderQuotaService.channelCostSince
    /// (internal/server/biz/provider_quota_cost.go:87-108): the cost of the
    /// usage logs a channel produced in <c>[since, until)</c>, served by the
    /// <c>usage_logs_by_channel_id_created_at</c> index.
    /// </summary>
    Task<double> SumCostByChannelAsync(
        long channelId,
        DateTime since,
        DateTime until,
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
    /// (internal/scopes/rule_user_project_scope_requests.go:36-72). Shared with
    /// the analytics metadata query, which runs the same UsageLog query policy.
    /// </summary>
    internal static void AppendScopes(
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

    internal static string? BuildFilter(
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

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "api_key_id", filter.ApiKeyId, filter.ApiKeyIdNEQ, filter.ApiKeyIdIn,
            filter.ApiKeyIdNotIn, filter.ApiKeyIdGT, filter.ApiKeyIdGTE, filter.ApiKeyIdLT, filter.ApiKeyIdLTE,
            filter.ApiKeyIdIsNil, filter.ApiKeyIdNotNil, args);

        ConnectionSql.AppendStringPredicates(
            predicates, alias, "model_id", filter.ModelId, filter.ModelIdNEQ, filter.ModelIdIn,
            filter.ModelIdNotIn, filter.ModelIdGT, filter.ModelIdGTE, filter.ModelIdLT, filter.ModelIdLTE,
            filter.ModelIdContains, filter.ModelIdHasPrefix, filter.ModelIdHasSuffix,
            filter.ModelIdEqualFold, filter.ModelIdContainsFold, args);

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "prompt_tokens", filter.PromptTokens, filter.PromptTokensNEQ, filter.PromptTokensIn,
            filter.PromptTokensNotIn, filter.PromptTokensGT, filter.PromptTokensGTE, filter.PromptTokensLT, filter.PromptTokensLTE,
            null, null, args);

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "completion_tokens", filter.CompletionTokens, filter.CompletionTokensNEQ, filter.CompletionTokensIn,
            filter.CompletionTokensNotIn, filter.CompletionTokensGT, filter.CompletionTokensGTE, filter.CompletionTokensLT, filter.CompletionTokensLTE,
            null, null, args);

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "total_tokens", filter.TotalTokens, filter.TotalTokensNEQ, filter.TotalTokensIn,
            filter.TotalTokensNotIn, filter.TotalTokensGT, filter.TotalTokensGTE, filter.TotalTokensLT, filter.TotalTokensLTE,
            null, null, args);

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "prompt_audio_tokens", filter.PromptAudioTokens, filter.PromptAudioTokensNEQ, filter.PromptAudioTokensIn,
            filter.PromptAudioTokensNotIn, filter.PromptAudioTokensGT, filter.PromptAudioTokensGTE, filter.PromptAudioTokensLT, filter.PromptAudioTokensLTE,
            filter.PromptAudioTokensIsNil, filter.PromptAudioTokensNotNil, args);

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "prompt_cached_tokens", filter.PromptCachedTokens, filter.PromptCachedTokensNEQ, filter.PromptCachedTokensIn,
            filter.PromptCachedTokensNotIn, filter.PromptCachedTokensGT, filter.PromptCachedTokensGTE, filter.PromptCachedTokensLT, filter.PromptCachedTokensLTE,
            filter.PromptCachedTokensIsNil, filter.PromptCachedTokensNotNil, args);

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "prompt_write_cached_tokens", filter.PromptWriteCachedTokens, filter.PromptWriteCachedTokensNEQ, filter.PromptWriteCachedTokensIn,
            filter.PromptWriteCachedTokensNotIn, filter.PromptWriteCachedTokensGT, filter.PromptWriteCachedTokensGTE, filter.PromptWriteCachedTokensLT, filter.PromptWriteCachedTokensLTE,
            filter.PromptWriteCachedTokensIsNil, filter.PromptWriteCachedTokensNotNil, args);

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "prompt_write_cached_tokens_5m", filter.PromptWriteCachedTokens5m, filter.PromptWriteCachedTokens5mNEQ, filter.PromptWriteCachedTokens5mIn,
            filter.PromptWriteCachedTokens5mNotIn, filter.PromptWriteCachedTokens5mGT, filter.PromptWriteCachedTokens5mGTE, filter.PromptWriteCachedTokens5mLT, filter.PromptWriteCachedTokens5mLTE,
            filter.PromptWriteCachedTokens5mIsNil, filter.PromptWriteCachedTokens5mNotNil, args);

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "prompt_write_cached_tokens_1h", filter.PromptWriteCachedTokens1h, filter.PromptWriteCachedTokens1hNEQ, filter.PromptWriteCachedTokens1hIn,
            filter.PromptWriteCachedTokens1hNotIn, filter.PromptWriteCachedTokens1hGT, filter.PromptWriteCachedTokens1hGTE, filter.PromptWriteCachedTokens1hLT, filter.PromptWriteCachedTokens1hLTE,
            filter.PromptWriteCachedTokens1hIsNil, filter.PromptWriteCachedTokens1hNotNil, args);

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "completion_audio_tokens", filter.CompletionAudioTokens, filter.CompletionAudioTokensNEQ, filter.CompletionAudioTokensIn,
            filter.CompletionAudioTokensNotIn, filter.CompletionAudioTokensGT, filter.CompletionAudioTokensGTE, filter.CompletionAudioTokensLT, filter.CompletionAudioTokensLTE,
            filter.CompletionAudioTokensIsNil, filter.CompletionAudioTokensNotNil, args);

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "completion_reasoning_tokens", filter.CompletionReasoningTokens, filter.CompletionReasoningTokensNEQ, filter.CompletionReasoningTokensIn,
            filter.CompletionReasoningTokensNotIn, filter.CompletionReasoningTokensGT, filter.CompletionReasoningTokensGTE, filter.CompletionReasoningTokensLT, filter.CompletionReasoningTokensLTE,
            filter.CompletionReasoningTokensIsNil, filter.CompletionReasoningTokensNotNil, args);

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "completion_accepted_prediction_tokens", filter.CompletionAcceptedPredictionTokens, filter.CompletionAcceptedPredictionTokensNEQ, filter.CompletionAcceptedPredictionTokensIn,
            filter.CompletionAcceptedPredictionTokensNotIn, filter.CompletionAcceptedPredictionTokensGT, filter.CompletionAcceptedPredictionTokensGTE, filter.CompletionAcceptedPredictionTokensLT, filter.CompletionAcceptedPredictionTokensLTE,
            filter.CompletionAcceptedPredictionTokensIsNil, filter.CompletionAcceptedPredictionTokensNotNil, args);

        ConnectionSql.AppendLongPredicates(
            predicates, alias, "completion_rejected_prediction_tokens", filter.CompletionRejectedPredictionTokens, filter.CompletionRejectedPredictionTokensNEQ, filter.CompletionRejectedPredictionTokensIn,
            filter.CompletionRejectedPredictionTokensNotIn, filter.CompletionRejectedPredictionTokensGT, filter.CompletionRejectedPredictionTokensGTE, filter.CompletionRejectedPredictionTokensLT, filter.CompletionRejectedPredictionTokensLTE,
            filter.CompletionRejectedPredictionTokensIsNil, filter.CompletionRejectedPredictionTokensNotNil, args);

        ConnectionSql.AppendStringPredicates(
            predicates, alias, "format", filter.Format, filter.FormatNEQ, filter.FormatIn,
            filter.FormatNotIn, filter.FormatGT, filter.FormatGTE, filter.FormatLT, filter.FormatLTE,
            filter.FormatContains, filter.FormatHasPrefix, filter.FormatHasSuffix,
            filter.FormatEqualFold, filter.FormatContainsFold, args);

        ConnectionSql.AppendDoublePredicates(
            predicates, alias, "total_cost", filter.TotalCost, filter.TotalCostNEQ, filter.TotalCostIn,
            filter.TotalCostNotIn, filter.TotalCostGT, filter.TotalCostGTE, filter.TotalCostLT, filter.TotalCostLTE,
            filter.TotalCostIsNil, filter.TotalCostNotNil, args);

        ConnectionSql.AppendStringPredicates(
            predicates, alias, "cost_price_reference_id", filter.CostPriceReferenceId, filter.CostPriceReferenceIdNEQ, filter.CostPriceReferenceIdIn,
            filter.CostPriceReferenceIdNotIn, filter.CostPriceReferenceIdGT, filter.CostPriceReferenceIdGTE, filter.CostPriceReferenceIdLT, filter.CostPriceReferenceIdLTE,
            filter.CostPriceReferenceIdContains, filter.CostPriceReferenceIdHasPrefix, filter.CostPriceReferenceIdHasSuffix,
            filter.CostPriceReferenceIdEqualFold, filter.CostPriceReferenceIdContainsFold, args);

        if (filter.CostPriceReferenceIdIsNil == true)
        {
            predicates.Add(alias + ".\"cost_price_reference_id\" IS NULL");
        }

        if (filter.CostPriceReferenceIdNotNil == true)
        {
            predicates.Add(alias + ".\"cost_price_reference_id\" IS NOT NULL");
        }

        if (filter.HasRequest is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"requests\" ulrq WHERE ulrq.\"id\" = " + alias + ".\"request_id\")";
            predicates.Add(filter.HasRequest.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasRequestWith is { Count: > 0 } hasRequestFilters)
        {
            var withPredicates = hasRequestFilters
                .Select(nested => RequestSegmentStore.BuildFilter(nested, "ulrq", args))
                .Where(static nested => nested is not null)
                .ToArray();
            var withCondition = ConnectionSql.Combine(withPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"requests\" ulrq WHERE ulrq.\"id\" = " + alias + ".\"request_id\""
                + (withCondition is null ? string.Empty : " AND " + withCondition)
                + ")");
        }

        if (filter.HasProject is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"projects\" ulpr WHERE ulpr.\"id\" = " + alias + ".\"project_id\")";
            predicates.Add(filter.HasProject.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasProjectWith is { Count: > 0 } hasProjectFilters)
        {
            var withPredicates = hasProjectFilters
                .Select(nested => ProjectStore.BuildFilter(nested, "ulpr", args))
                .Where(static nested => nested is not null)
                .ToArray();
            var withCondition = ConnectionSql.Combine(withPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"projects\" ulpr WHERE ulpr.\"id\" = " + alias + ".\"project_id\""
                + (withCondition is null ? string.Empty : " AND " + withCondition)
                + ")");
        }

        if (filter.HasChannel is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"channels\" ulch WHERE ulch.\"id\" = " + alias + ".\"channel_id\")";
            predicates.Add(filter.HasChannel.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasChannelWith is { Count: > 0 } hasChannelFilters)
        {
            var withPredicates = hasChannelFilters
                .Select(nested => ChannelAdminStore.BuildFilter(nested, "ulch", args))
                .Where(static nested => nested is not null)
                .ToArray();
            var withCondition = ConnectionSql.Combine(withPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"channels\" ulch WHERE ulch.\"id\" = " + alias + ".\"channel_id\""
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

    public async Task<double> SumCostByChannelAsync(
        long channelId,
        DateTime since,
        DateTime until,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT COALESCE(SUM("total_cost"), 0)
            FROM "usage_logs"
            WHERE "channel_id" = @ChannelId
              AND "created_at" >= @Since
              AND "created_at" < @Until;
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<double>(
            new CommandDefinition(
                sql,
                new { ChannelId = channelId, Since = since, Until = until },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
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
