using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;

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

/// <summary>Subset of ent.ChannelWhereInput used by the management frontend.</summary>
public sealed record ChannelListFilter
{
    public long? Id { get; init; }

    public IReadOnlyList<long>? IdIn { get; init; }

    public string? Type { get; init; }

    public IReadOnlyList<string>? TypeIn { get; init; }

    public string? Name { get; init; }

    public IReadOnlyList<string>? NameIn { get; init; }

    public string? NameContainsFold { get; init; }

    public string? Status { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool ErrorMessageIsNil { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool ErrorMessageNotNil { get; init; }

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
    /// Tags of every channel whose status is not archived, one row per channel
    /// in id order (the reference selects the tags column of each row).
    /// </summary>
    Task<IReadOnlyList<string?>> ListTagRowsAsync(
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

    public async Task<IReadOnlyList<string?>> ListTagRowsAsync(
        CancellationToken cancellationToken = default)
    {
        // Query().Select(channel.FieldTags).Where(status != archived).All().
        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<string?>(
            new CommandDefinition(
                "SELECT \"tags\" FROM \"channels\""
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

        if (filter.Id is not null)
        {
            predicates.Add(alias + ".\"id\" = " + args.Add(filter.Id.Value));
        }

        if (filter.IdIn is not null)
        {
            ConnectionSql.AppendIdIn(predicates, alias, "id", filter.IdIn, args);
        }

        if (filter.Type is not null)
        {
            predicates.Add(alias + ".\"type\" = " + args.Add(filter.Type));
        }

        if (filter.TypeIn is not null)
        {
            ConnectionSql.AppendStringIn(predicates, alias, "type", filter.TypeIn, args);
        }

        if (filter.Name is not null)
        {
            predicates.Add(alias + ".\"name\" = " + args.Add(filter.Name));
        }

        if (filter.NameIn is not null)
        {
            ConnectionSql.AppendStringIn(predicates, alias, "name", filter.NameIn, args);
        }

        if (filter.NameContainsFold is not null)
        {
            ConnectionSql.AppendStringFilter(
                predicates,
                alias,
                "name",
                "LIKE",
                filter.NameContainsFold,
                args);
        }

        if (filter.Status is not null)
        {
            predicates.Add(alias + ".\"status\" = " + args.Add(filter.Status));
        }

        if (filter.StatusIn is not null)
        {
            ConnectionSql.AppendStringIn(predicates, alias, "status", filter.StatusIn, args);
        }

        if (filter.ErrorMessageIsNil)
        {
            predicates.Add(alias + ".\"error_message\" IS NULL");
        }

        if (filter.ErrorMessageNotNil)
        {
            predicates.Add(alias + ".\"error_message\" IS NOT NULL");
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
