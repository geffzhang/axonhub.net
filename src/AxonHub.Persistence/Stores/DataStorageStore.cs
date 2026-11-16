using System.Text;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>Ordering fields exposed by the reference DataStorageOrder type.</summary>
public enum DataStorageSortField
{
    Id,
    UpdatedAt,
}

/// <summary>
/// Port of ent.DataStorageWhereInput: every scalar predicate group and the
/// requests / executions edge predicates.
/// </summary>
public sealed record DataStorageListFilter
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

    public string? Description { get; init; }

    public string? DescriptionNEQ { get; init; }

    public IReadOnlyList<string>? DescriptionIn { get; init; }

    public IReadOnlyList<string>? DescriptionNotIn { get; init; }

    public string? DescriptionGT { get; init; }

    public string? DescriptionGTE { get; init; }

    public string? DescriptionLT { get; init; }

    public string? DescriptionLTE { get; init; }

    public string? DescriptionContains { get; init; }

    public string? DescriptionHasPrefix { get; init; }

    public string? DescriptionHasSuffix { get; init; }

    public string? DescriptionEqualFold { get; init; }

    public string? DescriptionContainsFold { get; init; }

    public bool? Primary { get; init; }

    public bool? PrimaryNEQ { get; init; }

    public string? Type { get; init; }

    public string? TypeNEQ { get; init; }

    public IReadOnlyList<string>? TypeIn { get; init; }

    public IReadOnlyList<string>? TypeNotIn { get; init; }

    public string? Status { get; init; }

    public string? StatusNEQ { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    public IReadOnlyList<string>? StatusNotIn { get; init; }

    public bool? HasRequests { get; init; }

    public IReadOnlyList<RequestListFilter>? HasRequestsWith { get; init; }

    public bool? HasExecutions { get; init; }

    public IReadOnlyList<RequestExecutionListFilter>? HasExecutionsWith { get; init; }

    public DataStorageListFilter? Not { get; init; }

    public IReadOnlyList<DataStorageListFilter>? And { get; init; }

    public IReadOnlyList<DataStorageListFilter>? Or { get; init; }
}

/// <summary>Entgql pagination request for data storages.</summary>
public sealed record DataStorageListQuery
{
    public DataStorageListFilter? Where { get; init; }

    public DataStorageSortField SortField { get; init; } = DataStorageSortField.Id;

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }
}

/// <summary>
/// Field set of an ent <c>DataStorage.UpdateOneID(id)</c> call. The store
/// renders the SET list in the generated builder's fixed order (updated_at,
/// name, description, settings, status); name/description/status are nullable
/// so an absent GraphQL field keeps its stored value, and settings carries the
/// already merged objects.DataStorageSettings JSON.
/// </summary>
public sealed record DataStorageUpdate
{
    public required long Id { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    /// <summary>objects.DataStorageSettings JSON (SetSettings).</summary>
    public string? Settings { get; init; }

    public string? Status { get; init; }
}

/// <summary>
/// Persistence seam for the reference <c>data_storages</c> table
/// (internal/ent/schema/data_storage.go).
/// </summary>
public interface IDataStorageStore
{
    Task<long> CreateAsync(DataStorageRecord storage, CancellationToken cancellationToken = default);

    /// <summary>
    /// ent <c>DataStorage.Get(ctx, id)</c> through the SoftDeleteMixin: live
    /// rows only; null stands in for ent's NotFoundError.
    /// </summary>
    Task<DataStorageRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// ent <c>DataStorage.Get(ctx, id)</c> with <c>schematype.SkipSoftDelete</c>
    /// (the GC worker's context): soft-deleted rows stay visible so their
    /// external objects can still be removed (gc.go:117-118, 289-309).
    /// </summary>
    Task<DataStorageRecord?> GetByIdIncludingDeletedAsync(
        long id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ent <c>DataStorage.Query().Where(Primary(true)).First(ctx)</c> through
    /// the SoftDeleteMixin: live rows only, id ASC. Null stands in for ent's
    /// NotFoundError.
    /// </summary>
    Task<DataStorageRecord?> GetPrimaryAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// ent <c>DataStorage.Query().Where(Primary(true)).First(ctx)</c> under the
    /// GC worker's SkipSoftDelete context (gc_bodies.go:367-382): soft-deleted
    /// rows stay visible. Null stands in for ent's NotFoundError; the caller
    /// renders that text.
    /// </summary>
    Task<DataStorageRecord?> GetPrimaryIncludingDeletedAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ent <c>DataStorage.Query().Where(NameEQ[, IDNEQ]).Exist(ctx)</c> (live
    /// rows; the unique index itself has no deleted_at column).
    /// </summary>
    Task<bool> ExistsByNameAsync(
        string name,
        long? excludeId,
        CancellationToken cancellationToken = default);

    Task<ListPage<DataStorageRecord>> ListAsync(
        DataStorageListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ent <c>DataStorage.UpdateOneID(id).Set...().Save(ctx)</c>: always bumps
    /// updated_at, applies only the provided fields, and returns the stored
    /// row through RETURNING (null when the id no longer exists).
    /// </summary>
    Task<DataStorageRecord?> UpdateAsync(
        DataStorageUpdate update,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>data_storages</c> table. Reads use Dapper and
/// honour the SoftDeleteMixin predicate (<c>deleted_at = 0</c>).
/// </summary>
public sealed class DataStorageStore(AxonHubDbContext db) : IDataStorageStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "name", "description",
        "primary", "type", "settings", "status"
        """;

    public async Task<long> CreateAsync(
        DataStorageRecord storage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storage);

        ApplyCreateDefaults(storage);

        db.DataStorages.Add(storage);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return storage.Id;
        }
        catch (DbUpdateException)
        {
            // Ent's client keeps no failed-entity state; detaching keeps a
            // later write on the same scoped context from retrying this row.
            db.Entry(storage).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<DataStorageRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = "SELECT " + Columns + " FROM \"data_storages\" WHERE \"id\" = "
            + args.Add(id) + " AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<DataStorageRecord>(
            new CommandDefinition(
                sql,
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<DataStorageRecord?> GetByIdIncludingDeletedAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = "SELECT " + Columns + " FROM \"data_storages\" WHERE \"id\" = "
            + args.Add(id) + ";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<DataStorageRecord>(
            new CommandDefinition(
                sql,
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<DataStorageRecord?> GetPrimaryAsync(
        CancellationToken cancellationToken = default)
    {
        // ent's First() on multiple primary rows is unspecified; the seeded
        // invariant is at most one, and id ASC keeps the port deterministic.
        var sql = "SELECT " + Columns
            + " FROM \"data_storages\" WHERE \"primary\" = TRUE AND \"deleted_at\" = 0"
            + " ORDER BY \"id\" ASC LIMIT 1;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<DataStorageRecord>(
            new CommandDefinition(
                sql,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<DataStorageRecord?> GetPrimaryIncludingDeletedAsync(
        CancellationToken cancellationToken = default)
    {
        // The GC worker's SkipSoftDelete context: no deleted_at predicate.
        var sql = "SELECT " + Columns
            + " FROM \"data_storages\" WHERE \"primary\" = TRUE"
            + " ORDER BY \"id\" ASC LIMIT 1;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<DataStorageRecord>(
            new CommandDefinition(
                sql,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> ExistsByNameAsync(
        string name,
        long? excludeId,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT EXISTS (SELECT 1 FROM \"data_storages\" WHERE \"name\" = ")
            .Append(args.Add(name))
            .Append(" AND \"deleted_at\" = 0");
        if (excludeId is not null)
        {
            sql.Append(" AND \"id\" <> ").Append(args.Add(excludeId.Value));
        }

        sql.Append(");");

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<ListPage<DataStorageRecord>> ListAsync(
        DataStorageListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var args = new SqlArguments();
        var where = BuildFilter(query.Where, "m", args);
        var conditions = new StringBuilder(" WHERE m.\"deleted_at\" = 0");
        if (where is not null)
        {
            conditions.Append(" AND ").Append(where);
        }

        var sortColumn = query.SortField switch
        {
            DataStorageSortField.UpdatedAt => "updated_at",
            _ => null,
        };
        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;
        const string from = " FROM \"data_storages\" m";
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
        var nodes = (await connection.QueryAsync<DataStorageRecord>(
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

        return new ListPage<DataStorageRecord>(nodes, hasNextPage, hasPreviousPage, count);
    }

    public async Task<DataStorageRecord?> UpdateAsync(
        DataStorageUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var args = new SqlArguments();

        // Ent's generated sqlSave applies the fields in schema order:
        // updated_at, name, description, settings, status.
        var sets = new List<string>(5)
        {
            "\"updated_at\" = " + args.Add(DateTime.UtcNow),
        };
        if (update.Name is not null)
        {
            sets.Add("\"name\" = " + args.Add(update.Name));
        }

        if (update.Description is not null)
        {
            sets.Add("\"description\" = " + args.Add(update.Description));
        }

        if (update.Settings is not null)
        {
            sets.Add("\"settings\" = " + JsonCast(args.Add(update.Settings)));
        }

        if (update.Status is not null)
        {
            sets.Add("\"status\" = " + args.Add(update.Status));
        }

        var sql = new StringBuilder("UPDATE \"data_storages\" SET ")
            .Append(string.Join(", ", sets))
            .Append(" WHERE \"id\" = ").Append(args.Add(update.Id))
            .Append(" RETURNING ").Append(Columns).Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<DataStorageRecord>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static void ApplyCreateDefaults(DataStorageRecord storage)
    {
        if (storage.CreatedAt == default)
        {
            storage.CreatedAt = DateTime.UtcNow;
        }

        if (storage.UpdatedAt == default)
        {
            storage.UpdatedAt = storage.CreatedAt;
        }

        // Ent schema defaults: primary=false, status=active. The service always
        // supplies both; the defaults keep hand-built rows consistent.
        storage.Status = string.IsNullOrEmpty(storage.Status) ? "active" : storage.Status;
    }

    private static string JsonCast(string parameter) => "CAST(" + parameter + " AS jsonb)";

    internal static string? BuildFilter(
        DataStorageListFilter? filter,
        string alias,
        SqlArguments args)
    {
        if (filter is null)
        {
            return null;
        }

        var predicates = new List<string?>();
        if (filter.Not is not null)
        {
            var not = BuildFilter(filter.Not, alias, args)
                ?? throw EmptyWhere("not");
            predicates.Add("NOT (" + not + ")");
        }

        if (filter.Or is not null)
        {
            var or = new List<string>(filter.Or.Count);
            foreach (var nested in filter.Or)
            {
                or.Add(BuildFilter(nested, alias, args) ?? throw EmptyWhere("or"));
            }

            if (or.Count > 0)
            {
                predicates.Add("(" + string.Join(" OR ", or) + ")");
            }
        }

        if (filter.And is not null)
        {
            var and = new List<string>(filter.And.Count);
            foreach (var nested in filter.And)
            {
                and.Add(BuildFilter(nested, alias, args) ?? throw EmptyWhere("and"));
            }

            if (and.Count > 0)
            {
                predicates.Add("(" + string.Join(" AND ", and) + ")");
            }
        }

        ConnectionSql.AppendLongPredicates(
            predicates,
            alias,
            "id",
            filter.Id,
            filter.IdNEQ,
            filter.IdIn,
            filter.IdNotIn,
            filter.IdGT,
            filter.IdGTE,
            filter.IdLT,
            filter.IdLTE,
            isNil: null,
            notNil: null,
            args);

        ConnectionSql.AppendTimePredicates(
            predicates,
            alias,
            "created_at",
            filter.CreatedAt,
            filter.CreatedAtNEQ,
            filter.CreatedAtIn,
            filter.CreatedAtNotIn,
            filter.CreatedAtGT,
            filter.CreatedAtGTE,
            filter.CreatedAtLT,
            filter.CreatedAtLTE,
            args);

        ConnectionSql.AppendTimePredicates(
            predicates,
            alias,
            "updated_at",
            filter.UpdatedAt,
            filter.UpdatedAtNEQ,
            filter.UpdatedAtIn,
            filter.UpdatedAtNotIn,
            filter.UpdatedAtGT,
            filter.UpdatedAtGTE,
            filter.UpdatedAtLT,
            filter.UpdatedAtLTE,
            args);

        ConnectionSql.AppendStringPredicates(
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

        ConnectionSql.AppendStringPredicates(
            predicates,
            alias,
            "description",
            filter.Description,
            filter.DescriptionNEQ,
            filter.DescriptionIn,
            filter.DescriptionNotIn,
            filter.DescriptionGT,
            filter.DescriptionGTE,
            filter.DescriptionLT,
            filter.DescriptionLTE,
            filter.DescriptionContains,
            filter.DescriptionHasPrefix,
            filter.DescriptionHasSuffix,
            filter.DescriptionEqualFold,
            filter.DescriptionContainsFold,
            args);

        // GraphQL Boolean maps to *bool: both true and false install a predicate.
        ConnectionSql.AppendBoolPredicates(
            predicates, alias, "primary", filter.Primary, filter.PrimaryNEQ, args);

        ConnectionSql.AppendComparison(predicates, alias, "type", filter.Type, "=", args);
        ConnectionSql.AppendComparison(predicates, alias, "type", filter.TypeNEQ, "<>", args);
        ConnectionSql.AppendStringIn(predicates, alias, "type", filter.TypeIn, args);
        ConnectionSql.AppendStringIn(predicates, alias, "type", filter.TypeNotIn, args, negate: true);

        ConnectionSql.AppendComparison(predicates, alias, "status", filter.Status, "=", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "status", filter.StatusNEQ, "<>", args);
        ConnectionSql.AppendStringIn(predicates, alias, "status", filter.StatusIn, args);
        ConnectionSql.AppendStringIn(
            predicates, alias, "status", filter.StatusNotIn, args, negate: true);

        if (filter.HasRequests is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"requests\" rq2 WHERE rq2.\"data_storage_id\" = "
                + alias + ".\"id\")";
            predicates.Add(filter.HasRequests.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasRequestsWith is { Count: > 0 } requestFilters)
        {
            // Generated sqlgraph.HasNeighborsWith ANDs every predicate inside a
            // single EXISTS and does not re-apply the target soft-delete predicate.
            var requestPredicates = requestFilters
                .Select(request => RequestSegmentStore.BuildFilter(request, "rq2", args))
                .Where(static request => request is not null)
                .ToArray();
            var requestCondition = ConnectionSql.Combine(requestPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"requests\" rq2"
                + " WHERE rq2.\"data_storage_id\" = " + alias + ".\"id\""
                + (requestCondition is null ? string.Empty : " AND " + requestCondition)
                + ")");
        }

        if (filter.HasExecutions is not null)
        {
            var exists =
                "EXISTS (SELECT 1 FROM \"request_executions\" re2"
                + " WHERE re2.\"data_storage_id\" = " + alias + ".\"id\")";
            predicates.Add(filter.HasExecutions.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasExecutionsWith is { Count: > 0 } executionFilters)
        {
            var executionPredicates = executionFilters
                .Select(execution => RequestExecutionStore.BuildFilter(execution, "re2", args))
                .Where(static execution => execution is not null)
                .ToArray();
            var executionCondition = ConnectionSql.Combine(executionPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"request_executions\" re2"
                + " WHERE re2.\"data_storage_id\" = " + alias + ".\"id\""
                + (executionCondition is null ? string.Empty : " AND " + executionCondition)
                + ")");
        }

        return ConnectionSql.Combine(predicates);
    }

    /// <summary>
    /// Ent's generated P() rejects a nested where input without predicates:
    /// <c>"ent: empty predicate DataStorageWhereInput: field 'not'"</c>.
    /// </summary>
    private static InvalidOperationException EmptyWhere(string field)
        => new("ent: empty predicate DataStorageWhereInput: field '" + field + "'");
}
