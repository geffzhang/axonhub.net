using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using static AxonHub.Persistence.ConnectionSql;

namespace AxonHub.Persistence;

/// <summary>Ordering fields exposed by the reference ChannelOrder type.</summary>
public enum ChannelSortField
{
    Id,
    UpdatedAt,
    Type,
    Name,
    Status,
    OrderingWeight,
}

/// <summary>Port of the scalar predicates of ent.ChannelWhereInput.</summary>
public sealed record ChannelListFilter
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

    public string? Type { get; init; }

    public string? TypeNEQ { get; init; }

    public IReadOnlyList<string>? TypeIn { get; init; }

    public IReadOnlyList<string>? TypeNotIn { get; init; }

    public string? BaseURL { get; init; }

    public string? BaseURLNEQ { get; init; }

    public IReadOnlyList<string>? BaseURLIn { get; init; }

    public IReadOnlyList<string>? BaseURLNotIn { get; init; }

    public string? BaseURLGT { get; init; }

    public string? BaseURLGTE { get; init; }

    public string? BaseURLLT { get; init; }

    public string? BaseURLLTE { get; init; }

    public string? BaseURLContains { get; init; }

    public string? BaseURLHasPrefix { get; init; }

    public string? BaseURLHasSuffix { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool BaseURLIsNil { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool BaseURLNotNil { get; init; }

    public string? BaseURLEqualFold { get; init; }

    public string? BaseURLContainsFold { get; init; }

    public string? Name { get; init; }

    public string? NameNEQ { get; init; }

    public IReadOnlyList<string>? NameIn { get; init; }

    public IReadOnlyList<string>? NameNotIn { get; init; }

    public string? NameGT { get; init; }

    public string? NameGTE { get; init; }

    public string? NameLT { get; init; }

    public string? NameLTE { get; init; }

    public string? NameContains { get; init; }

    public string? NameHasPrefix { get; init; }

    public string? NameHasSuffix { get; init; }

    public string? NameEqualFold { get; init; }

    public string? NameContainsFold { get; init; }

    public string? Status { get; init; }

    public string? StatusNEQ { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    public IReadOnlyList<string>? StatusNotIn { get; init; }

    public bool? AutoSyncSupportedModels { get; init; }

    public bool? AutoSyncSupportedModelsNEQ { get; init; }

    public string? AutoSyncModelPattern { get; init; }

    public string? AutoSyncModelPatternNEQ { get; init; }

    public IReadOnlyList<string>? AutoSyncModelPatternIn { get; init; }

    public IReadOnlyList<string>? AutoSyncModelPatternNotIn { get; init; }

    public string? AutoSyncModelPatternGT { get; init; }

    public string? AutoSyncModelPatternGTE { get; init; }

    public string? AutoSyncModelPatternLT { get; init; }

    public string? AutoSyncModelPatternLTE { get; init; }

    public string? AutoSyncModelPatternContains { get; init; }

    public string? AutoSyncModelPatternHasPrefix { get; init; }

    public string? AutoSyncModelPatternHasSuffix { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool AutoSyncModelPatternIsNil { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool AutoSyncModelPatternNotNil { get; init; }

    public string? AutoSyncModelPatternEqualFold { get; init; }

    public string? AutoSyncModelPatternContainsFold { get; init; }

    public string? DefaultTestModel { get; init; }

    public string? DefaultTestModelNEQ { get; init; }

    public IReadOnlyList<string>? DefaultTestModelIn { get; init; }

    public IReadOnlyList<string>? DefaultTestModelNotIn { get; init; }

    public string? DefaultTestModelGT { get; init; }

    public string? DefaultTestModelGTE { get; init; }

    public string? DefaultTestModelLT { get; init; }

    public string? DefaultTestModelLTE { get; init; }

    public string? DefaultTestModelContains { get; init; }

    public string? DefaultTestModelHasPrefix { get; init; }

    public string? DefaultTestModelHasSuffix { get; init; }

    public string? DefaultTestModelEqualFold { get; init; }

    public string? DefaultTestModelContainsFold { get; init; }

    public long? OrderingWeight { get; init; }

    public long? OrderingWeightNEQ { get; init; }

    public IReadOnlyList<long>? OrderingWeightIn { get; init; }

    public IReadOnlyList<long>? OrderingWeightNotIn { get; init; }

    public long? OrderingWeightGT { get; init; }

    public long? OrderingWeightGTE { get; init; }

    public long? OrderingWeightLT { get; init; }

    public long? OrderingWeightLTE { get; init; }

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

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool ErrorMessageIsNil { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool ErrorMessageNotNil { get; init; }

    public string? ErrorMessageEqualFold { get; init; }

    public string? ErrorMessageContainsFold { get; init; }

    public DateTime? AutoDisabledAt { get; init; }

    public DateTime? AutoDisabledAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? AutoDisabledAtIn { get; init; }

    public IReadOnlyList<DateTime>? AutoDisabledAtNotIn { get; init; }

    public DateTime? AutoDisabledAtGT { get; init; }

    public DateTime? AutoDisabledAtGTE { get; init; }

    public DateTime? AutoDisabledAtLT { get; init; }

    public DateTime? AutoDisabledAtLTE { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool AutoDisabledAtIsNil { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool AutoDisabledAtNotNil { get; init; }

    public DateTime? AutoDisableExpiresAt { get; init; }

    public DateTime? AutoDisableExpiresAtNEQ { get; init; }

    public IReadOnlyList<DateTime>? AutoDisableExpiresAtIn { get; init; }

    public IReadOnlyList<DateTime>? AutoDisableExpiresAtNotIn { get; init; }

    public DateTime? AutoDisableExpiresAtGT { get; init; }

    public DateTime? AutoDisableExpiresAtGTE { get; init; }

    public DateTime? AutoDisableExpiresAtLT { get; init; }

    public DateTime? AutoDisableExpiresAtLTE { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool AutoDisableExpiresAtIsNil { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool AutoDisableExpiresAtNotNil { get; init; }

    public string? Remark { get; init; }

    public string? RemarkNEQ { get; init; }

    public IReadOnlyList<string>? RemarkIn { get; init; }

    public IReadOnlyList<string>? RemarkNotIn { get; init; }

    public string? RemarkGT { get; init; }

    public string? RemarkGTE { get; init; }

    public string? RemarkLT { get; init; }

    public string? RemarkLTE { get; init; }

    public string? RemarkContains { get; init; }

    public string? RemarkHasPrefix { get; init; }

    public string? RemarkHasSuffix { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool RemarkIsNil { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool RemarkNotNil { get; init; }

    public string? RemarkEqualFold { get; init; }

    public string? RemarkContainsFold { get; init; }

    public ChannelListFilter? Not { get; init; }

    public IReadOnlyList<ChannelListFilter>? And { get; init; }

    public IReadOnlyList<ChannelListFilter>? Or { get; init; }
}

/// <summary>Entgql pagination request for channels.</summary>
public sealed record ChannelListQuery
{
    public ChannelListFilter? Where { get; init; }

    /// <summary>Port of <c>sqljson.ValueContains("tags", hasTag)</c>.</summary>
    public string? HasTag { get; init; }

    public ChannelSortField SortField { get; init; } = ChannelSortField.Id;

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    /// <summary>
    /// True when the caller supplied an orderBy input. The unpaginated model
    /// branch only orders when orderBy is non-nil (QueryChannelsInput.Model);
    /// the port appends an id tie-break to keep ties deterministic.
    /// </summary>
    public bool Ordered { get; init; }

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }
}

/// <summary>One row of CountChannelsByType.</summary>
public sealed record ChannelTypeCount(string Type, int Count);

/// <summary>Management read seam for the reference <c>channels</c> table.</summary>
public interface IChannelAdminStore
{
    /// <summary>
    /// Port of the entgql Node lookup for Channel: the live row with the given
    /// id, or null when neither the id nor the soft-delete predicate matches.
    /// </summary>
    Task<ChannelRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default);

    /// <summary>Port of entgql's ChannelQuery.Paginate.</summary>
    Task<ListPage<ChannelRecord>> ListAsync(
        ChannelListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The unpaginated model-filter branch: every row matching the filters,
    /// ordered only when the caller supplied an orderBy.
    /// </summary>
    Task<IReadOnlyList<ChannelRecord>> ListAllAsync(
        ChannelListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Port of queryResolver.AllChannelSummarys.</summary>
    Task<IReadOnlyList<ChannelRecord>> ListSummariesAsync(
        bool includeArchived,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the system-bypass branch of AllChannelSummarys: every enabled
    /// channel, ordered by ordering_weight desc.
    /// </summary>
    Task<IReadOnlyList<ChannelRecord>> ListEnabledSummariesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every channel whose status is not archived, one row per channel in id
    /// order (the reference selects the tags column of each row; the port
    /// loads the full row so the project profile can also filter on ids).
    /// </summary>
    Task<IReadOnlyList<ChannelRecord>> ListTagSourcesAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Port of queryResolver.CountChannelsByType's GroupBy(type).Aggregate(Count()).</summary>
    Task<IReadOnlyList<ChannelTypeCount>> CountByTypeAsync(
        IReadOnlyList<string>? statusIn,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the channel management reads. Dapper only: the reference queries
/// are read-only and Ent adds the SoftDeleteMixin predicate (<c>deleted_at = 0</c>)
/// to every one of them.
/// </summary>
public sealed class ChannelAdminStore(AxonHubDbContext db) : IChannelAdminStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "type", "base_url", "name",
        "status", "credentials", "disabled_api_keys", "supported_models", "manual_models",
        "auto_sync_supported_models", "auto_sync_model_pattern", "tags", "default_test_model",
        "policies", "settings", "ordering_weight", "error_message", "auto_disabled_at",
        "auto_disable_expires_at", "remark", "endpoints"
        """;

    public async Task<ChannelRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"channels\" WHERE \"id\" = @Id AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ChannelRecord>(
            new CommandDefinition(
                sql,
                new { Id = id },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<ListPage<ChannelRecord>> ListAsync(
        ChannelListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var args = new SqlArguments();
        var conditions = BuildConditions(query, args);

        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;

        var countSql = "SELECT COUNT(*) FROM \"channels\" c" + conditions + ";";

        var nodesSql = new StringBuilder("SELECT ");
        nodesSql.Append(string.Join(", ", Columns
            .Split(',')
            .Select(static column => "c." + column.Trim())));
        nodesSql.Append(" FROM \"channels\" c").Append(conditions);

        var valueColumn = SortValueColumn(query.SortField);

        ConnectionSql.AppendCursor(
            nodesSql,
            "c",
            "id",
            valueColumn,
            query.After,
            query.Before,
            direction,
            args);

        nodesSql.Append(" ORDER BY ")
            .Append(ConnectionSql.OrderBy("c", "id", valueColumn, direction));

        var limit = query.First is not null
            ? query.First + 1
            : query.Last is not null ? query.Last + 1 : (int?)null;
        if (limit is not null)
        {
            nodesSql.Append(" LIMIT ").Append(limit.Value);
        }

        nodesSql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);

        var totalCount = await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(
                countSql,
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Entgql returns an empty connection without touching the rows when the
        // requested page size is zero; only the count-based flags survive.
        if (query.First is 0 || query.Last is 0)
        {
            return new ListPage<ChannelRecord>(
                [],
                query.First is not null && totalCount > 0,
                query.Last is not null && totalCount > 0,
                totalCount);
        }

        var nodes = (await connection.QueryAsync<ChannelRecord>(
            new CommandDefinition(
                nodesSql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

        return BuildPage(nodes, query, totalCount);
    }

    public async Task<IReadOnlyList<ChannelRecord>> ListAllAsync(
        ChannelListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var args = new SqlArguments();
        var conditions = BuildConditions(query, args);

        var sql = new StringBuilder("SELECT ");
        sql.Append(string.Join(", ", Columns
            .Split(',')
            .Select(static column => "c." + column.Trim())));
        sql.Append(" FROM \"channels\" c").Append(conditions);

        var valueColumn = SortValueColumn(query.SortField);
        var dir = query.Direction == SortDirection.Asc ? "ASC" : "DESC";

        if (query.Ordered)
        {
            sql.Append(" ORDER BY c.\"")
                .Append(valueColumn ?? "id")
                .Append("\" ")
                .Append(dir);

            // The reference's ChannelOrder.ToOrderOption has no id tie-break;
            // id ASC only makes ties deterministic without changing the set.
            if (valueColumn is not null)
            {
                sql.Append(", c.\"id\" ASC");
            }
        }
        else
        {
            // Ent returns rows in table order; id ASC is the deterministic
            // equivalent the reference relies on in practice.
            sql.Append(" ORDER BY c.\"id\" ASC");
        }

        sql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ChannelRecord>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<ChannelRecord>> ListSummariesAsync(
        bool includeArchived,
        CancellationToken cancellationToken = default)
    {
        var statuses = includeArchived
            ? "'enabled', 'disabled', 'archived'"
            : "'enabled', 'disabled'";

        // Port of AllChannelSummarys' non-project branch:
        // StatusIn(...) + Order(ent.Desc(FieldOrderingWeight)). The reference
        // leaves equal weights unordered; id ASC keeps the port deterministic.
        var sql = "SELECT " + Columns
            + " FROM \"channels\" WHERE \"deleted_at\" = 0 AND \"status\" IN (" + statuses + ")"
            + " ORDER BY \"ordering_weight\" DESC, \"id\" ASC;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ChannelRecord>(
            new CommandDefinition(
                sql,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<ChannelRecord>> ListEnabledSummariesAsync(
        CancellationToken cancellationToken = default)
    {
        // authz.RunWithSystemBypass branch of AllChannelSummarys:
        // StatusEQ(enabled) + Order(ent.Desc(FieldOrderingWeight)).
        var sql = "SELECT " + Columns
            + " FROM \"channels\" WHERE \"deleted_at\" = 0 AND \"status\" = 'enabled'"
            + " ORDER BY \"ordering_weight\" DESC, \"id\" ASC;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ChannelRecord>(
            new CommandDefinition(
                sql,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<ChannelRecord>> ListTagSourcesAsync(
        CancellationToken cancellationToken = default)
    {
        // Query().Select(channel.FieldTags).Where(status != archived).All().
        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ChannelRecord>(
            new CommandDefinition(
                "SELECT " + Columns + " FROM \"channels\""
                    + " WHERE \"deleted_at\" = 0 AND \"status\" <> 'archived'"
                    + " ORDER BY \"id\";",
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<ChannelTypeCount>> CountByTypeAsync(
        IReadOnlyList<string>? statusIn,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var conditions = new StringBuilder(" WHERE c.\"deleted_at\" = 0");

        if (statusIn is { Count: > 0 })
        {
            var predicates = new List<string?>();
            ConnectionSql.AppendStringIn(predicates, "c", "status", statusIn, args);
            conditions.Append(" AND ").Append(ConnectionSql.Combine(predicates));
        }
        else
        {
            conditions.Append(" AND c.\"status\" <> 'archived'");
        }

        // The reference query has no ORDER BY; ordering by type makes the
        // response deterministic (PostgreSQL returns groups in hash order).
        var sql = "SELECT c.\"type\" AS \"type\", COUNT(*) AS \"count\""
            + " FROM \"channels\" c" + conditions
            + " GROUP BY c.\"type\" ORDER BY c.\"type\" ASC;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<CountRow>(
            new CommandDefinition(
                sql,
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);

        return [.. rows.Select(static row => new ChannelTypeCount(row.Type, (int)row.Count))];
    }

    private static string? SortValueColumn(ChannelSortField field) => field switch
    {
        ChannelSortField.UpdatedAt => "updated_at",
        ChannelSortField.Type => "type",
        ChannelSortField.Name => "name",
        ChannelSortField.Status => "status",
        ChannelSortField.OrderingWeight => "ordering_weight",
        _ => null,
    };

    private static string BuildConditions(ChannelListQuery query, SqlArguments args)
    {
        var conditions = new StringBuilder(" WHERE c.\"deleted_at\" = 0");

        var where = BuildFilter(query.Where, "c", args);
        if (where is not null)
        {
            conditions.Append(" AND ").Append(where);
        }

        if (!string.IsNullOrEmpty(query.HasTag))
        {
            // sqljson.ValueContains serializes the value as JSON and asks for
            // jsonb containment: tags @> '"tag"'.
            var parameter = args.Add(JsonSerializer.Serialize(query.HasTag));
            conditions.Append(" AND c.\"tags\" @> CAST(")
                .Append(parameter)
                .Append(" AS jsonb)");
        }

        return conditions.ToString();
    }

    private static ListPage<ChannelRecord> BuildPage(
        List<ChannelRecord> nodes,
        ChannelListQuery query,
        int totalCount)
    {
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

        return new ListPage<ChannelRecord>(nodes, hasNextPage, hasPreviousPage, totalCount);
    }

    private static string? BuildFilter(
        ChannelListFilter? filter,
        string alias,
        SqlArguments args)
    {
        if (filter is null)
        {
            return null;
        }

        var predicates = new List<string?>();

        AppendComparison(predicates, alias, "id", filter.Id, "=", args);
        AppendComparison(predicates, alias, "id", filter.IdNEQ, "<>", args);
        if (filter.IdIn is not null)
        {
            AppendIdIn(predicates, alias, "id", filter.IdIn, args);
        }

        if (filter.IdNotIn is not null)
        {
            AppendIdIn(predicates, alias, "id", filter.IdNotIn, args, negate: true);
        }

        AppendComparison(predicates, alias, "id", filter.IdGT, ">", args);
        AppendComparison(predicates, alias, "id", filter.IdGTE, ">=", args);
        AppendComparison(predicates, alias, "id", filter.IdLT, "<", args);
        AppendComparison(predicates, alias, "id", filter.IdLTE, "<=", args);

        AppendComparison(predicates, alias, "created_at", filter.CreatedAt, "=", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtNEQ, "<>", args);
        AppendTimeIn(predicates, alias, "created_at", filter.CreatedAtIn, args);
        AppendTimeIn(predicates, alias, "created_at", filter.CreatedAtNotIn, args, negate: true);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtGT, ">", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtGTE, ">=", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtLT, "<", args);
        AppendComparison(predicates, alias, "created_at", filter.CreatedAtLTE, "<=", args);

        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAt, "=", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtNEQ, "<>", args);
        AppendTimeIn(predicates, alias, "updated_at", filter.UpdatedAtIn, args);
        AppendTimeIn(predicates, alias, "updated_at", filter.UpdatedAtNotIn, args, negate: true);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtGT, ">", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtGTE, ">=", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtLT, "<", args);
        AppendComparison(predicates, alias, "updated_at", filter.UpdatedAtLTE, "<=", args);

        AppendComparison(predicates, alias, "type", filter.Type, "=", args);
        AppendComparison(predicates, alias, "type", filter.TypeNEQ, "<>", args);
        if (filter.TypeIn is not null)
        {
            AppendStringIn(predicates, alias, "type", filter.TypeIn, args);
        }

        if (filter.TypeNotIn is not null)
        {
            AppendStringIn(predicates, alias, "type", filter.TypeNotIn, args, negate: true);
        }

        AppendStringPredicates(
            predicates,
            alias,
            "base_url",
            filter.BaseURL,
            filter.BaseURLNEQ,
            filter.BaseURLIn,
            filter.BaseURLNotIn,
            filter.BaseURLGT,
            filter.BaseURLGTE,
            filter.BaseURLLT,
            filter.BaseURLLTE,
            filter.BaseURLContains,
            filter.BaseURLHasPrefix,
            filter.BaseURLHasSuffix,
            filter.BaseURLEqualFold,
            filter.BaseURLContainsFold,
            args);
        if (filter.BaseURLIsNil)
        {
            predicates.Add(alias + ".\"base_url\" IS NULL");
        }

        if (filter.BaseURLNotNil)
        {
            predicates.Add(alias + ".\"base_url\" IS NOT NULL");
        }

        AppendStringPredicates(
            predicates,
            alias,
            "name",
            filter.Name,
            filter.NameNEQ,
            filter.NameIn,
            filter.NameNotIn,
            filter.NameGT,
            filter.NameGTE,
            filter.NameLT,
            filter.NameLTE,
            filter.NameContains,
            filter.NameHasPrefix,
            filter.NameHasSuffix,
            filter.NameEqualFold,
            filter.NameContainsFold,
            args);

        AppendComparison(predicates, alias, "status", filter.Status, "=", args);
        AppendComparison(predicates, alias, "status", filter.StatusNEQ, "<>", args);
        if (filter.StatusIn is not null)
        {
            AppendStringIn(predicates, alias, "status", filter.StatusIn, args);
        }

        if (filter.StatusNotIn is not null)
        {
            AppendStringIn(predicates, alias, "status", filter.StatusNotIn, args, negate: true);
        }

        if (filter.AutoSyncSupportedModels is not null)
        {
            predicates.Add(
                alias + ".\"auto_sync_supported_models\" = "
                + args.Add(filter.AutoSyncSupportedModels.Value));
        }

        if (filter.AutoSyncSupportedModelsNEQ is not null)
        {
            predicates.Add(
                alias + ".\"auto_sync_supported_models\" <> "
                + args.Add(filter.AutoSyncSupportedModelsNEQ.Value));
        }

        AppendStringPredicates(
            predicates,
            alias,
            "auto_sync_model_pattern",
            filter.AutoSyncModelPattern,
            filter.AutoSyncModelPatternNEQ,
            filter.AutoSyncModelPatternIn,
            filter.AutoSyncModelPatternNotIn,
            filter.AutoSyncModelPatternGT,
            filter.AutoSyncModelPatternGTE,
            filter.AutoSyncModelPatternLT,
            filter.AutoSyncModelPatternLTE,
            filter.AutoSyncModelPatternContains,
            filter.AutoSyncModelPatternHasPrefix,
            filter.AutoSyncModelPatternHasSuffix,
            filter.AutoSyncModelPatternEqualFold,
            filter.AutoSyncModelPatternContainsFold,
            args);
        if (filter.AutoSyncModelPatternIsNil)
        {
            predicates.Add(alias + ".\"auto_sync_model_pattern\" IS NULL");
        }

        if (filter.AutoSyncModelPatternNotNil)
        {
            predicates.Add(alias + ".\"auto_sync_model_pattern\" IS NOT NULL");
        }

        AppendStringPredicates(
            predicates,
            alias,
            "default_test_model",
            filter.DefaultTestModel,
            filter.DefaultTestModelNEQ,
            filter.DefaultTestModelIn,
            filter.DefaultTestModelNotIn,
            filter.DefaultTestModelGT,
            filter.DefaultTestModelGTE,
            filter.DefaultTestModelLT,
            filter.DefaultTestModelLTE,
            filter.DefaultTestModelContains,
            filter.DefaultTestModelHasPrefix,
            filter.DefaultTestModelHasSuffix,
            filter.DefaultTestModelEqualFold,
            filter.DefaultTestModelContainsFold,
            args);

        AppendComparison(predicates, alias, "ordering_weight", filter.OrderingWeight, "=", args);
        AppendComparison(predicates, alias, "ordering_weight", filter.OrderingWeightNEQ, "<>", args);
        if (filter.OrderingWeightIn is not null)
        {
            AppendIdIn(predicates, alias, "ordering_weight", filter.OrderingWeightIn, args);
        }

        if (filter.OrderingWeightNotIn is not null)
        {
            AppendIdIn(
                predicates,
                alias,
                "ordering_weight",
                filter.OrderingWeightNotIn,
                args,
                negate: true);
        }

        AppendComparison(predicates, alias, "ordering_weight", filter.OrderingWeightGT, ">", args);
        AppendComparison(predicates, alias, "ordering_weight", filter.OrderingWeightGTE, ">=", args);
        AppendComparison(predicates, alias, "ordering_weight", filter.OrderingWeightLT, "<", args);
        AppendComparison(predicates, alias, "ordering_weight", filter.OrderingWeightLTE, "<=", args);

        AppendStringPredicates(
            predicates,
            alias,
            "error_message",
            filter.ErrorMessage,
            filter.ErrorMessageNEQ,
            filter.ErrorMessageIn,
            filter.ErrorMessageNotIn,
            filter.ErrorMessageGT,
            filter.ErrorMessageGTE,
            filter.ErrorMessageLT,
            filter.ErrorMessageLTE,
            filter.ErrorMessageContains,
            filter.ErrorMessageHasPrefix,
            filter.ErrorMessageHasSuffix,
            filter.ErrorMessageEqualFold,
            filter.ErrorMessageContainsFold,
            args);
        if (filter.ErrorMessageIsNil)
        {
            predicates.Add(alias + ".\"error_message\" IS NULL");
        }

        if (filter.ErrorMessageNotNil)
        {
            predicates.Add(alias + ".\"error_message\" IS NOT NULL");
        }

        AppendComparison(predicates, alias, "auto_disabled_at", filter.AutoDisabledAt, "=", args);
        AppendComparison(predicates, alias, "auto_disabled_at", filter.AutoDisabledAtNEQ, "<>", args);
        AppendTimeIn(predicates, alias, "auto_disabled_at", filter.AutoDisabledAtIn, args);
        AppendTimeIn(predicates, alias, "auto_disabled_at", filter.AutoDisabledAtNotIn, args, negate: true);
        AppendComparison(predicates, alias, "auto_disabled_at", filter.AutoDisabledAtGT, ">", args);
        AppendComparison(predicates, alias, "auto_disabled_at", filter.AutoDisabledAtGTE, ">=", args);
        AppendComparison(predicates, alias, "auto_disabled_at", filter.AutoDisabledAtLT, "<", args);
        AppendComparison(predicates, alias, "auto_disabled_at", filter.AutoDisabledAtLTE, "<=", args);
        if (filter.AutoDisabledAtIsNil)
        {
            predicates.Add(alias + ".\"auto_disabled_at\" IS NULL");
        }

        if (filter.AutoDisabledAtNotNil)
        {
            predicates.Add(alias + ".\"auto_disabled_at\" IS NOT NULL");
        }

        AppendComparison(
            predicates,
            alias,
            "auto_disable_expires_at",
            filter.AutoDisableExpiresAt,
            "=",
            args);
        AppendComparison(
            predicates,
            alias,
            "auto_disable_expires_at",
            filter.AutoDisableExpiresAtNEQ,
            "<>",
            args);
        AppendTimeIn(predicates, alias, "auto_disable_expires_at", filter.AutoDisableExpiresAtIn, args);
        AppendTimeIn(
            predicates,
            alias,
            "auto_disable_expires_at",
            filter.AutoDisableExpiresAtNotIn,
            args,
            negate: true);
        AppendComparison(predicates, alias, "auto_disable_expires_at", filter.AutoDisableExpiresAtGT, ">", args);
        AppendComparison(predicates, alias, "auto_disable_expires_at", filter.AutoDisableExpiresAtGTE, ">=", args);
        AppendComparison(predicates, alias, "auto_disable_expires_at", filter.AutoDisableExpiresAtLT, "<", args);
        AppendComparison(predicates, alias, "auto_disable_expires_at", filter.AutoDisableExpiresAtLTE, "<=", args);
        if (filter.AutoDisableExpiresAtIsNil)
        {
            predicates.Add(alias + ".\"auto_disable_expires_at\" IS NULL");
        }

        if (filter.AutoDisableExpiresAtNotNil)
        {
            predicates.Add(alias + ".\"auto_disable_expires_at\" IS NOT NULL");
        }

        AppendStringPredicates(
            predicates,
            alias,
            "remark",
            filter.Remark,
            filter.RemarkNEQ,
            filter.RemarkIn,
            filter.RemarkNotIn,
            filter.RemarkGT,
            filter.RemarkGTE,
            filter.RemarkLT,
            filter.RemarkLTE,
            filter.RemarkContains,
            filter.RemarkHasPrefix,
            filter.RemarkHasSuffix,
            filter.RemarkEqualFold,
            filter.RemarkContainsFold,
            args);
        if (filter.RemarkIsNil)
        {
            predicates.Add(alias + ".\"remark\" IS NULL");
        }

        if (filter.RemarkNotNil)
        {
            predicates.Add(alias + ".\"remark\" IS NOT NULL");
        }

        if (filter.Not is not null)
        {
            var not = BuildFilter(filter.Not, alias, args);
            if (not is not null)
            {
                predicates.Add("NOT (" + not + ")");
            }
        }

        if (filter.And is not null)
        {
            var and = filter.And
                .Select(nested => BuildFilter(nested, alias, args))
                .Where(static nested => nested is not null)
                .ToArray();
            if (and.Length > 0)
            {
                predicates.Add("(" + string.Join(" AND ", and) + ")");
            }
        }

        if (filter.Or is not null)
        {
            var or = filter.Or
                .Select(nested => BuildFilter(nested, alias, args))
                .Where(static nested => nested is not null)
                .ToArray();
            predicates.Add(or.Length == 0 ? "FALSE" : "(" + string.Join(" OR ", or) + ")");
        }

        return ConnectionSql.Combine(predicates);
    }

    private sealed record CountRow(string Type, long Count);
}
