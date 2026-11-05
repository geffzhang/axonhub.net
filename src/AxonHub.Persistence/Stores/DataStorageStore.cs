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
/// Subset of ent.DataStorageWhereInput used by the management frontend
/// (nameContainsFold, typeIn, statusIn) plus the id/name/primary/type/status
/// equality and collection predicates and the and/or/not operators. The time,
/// description and string comparison variants and the requests/executions edge
/// predicates are not exposed in this slice (C196).
/// </summary>
public sealed record DataStorageListFilter
{
    public long? Id { get; init; }

    public long? IdNEQ { get; init; }

    public IReadOnlyList<long>? IdIn { get; init; }

    public IReadOnlyList<long>? IdNotIn { get; init; }

    public string? Name { get; init; }

    public string? NameNEQ { get; init; }

    public IReadOnlyList<string>? NameIn { get; init; }

    public IReadOnlyList<string>? NameNotIn { get; init; }

    public string? NameContainsFold { get; init; }

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

    private static string? BuildFilter(
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

        if (filter.Id is not null)
        {
            predicates.Add(alias + ".\"id\" = " + args.Add(filter.Id.Value));
        }

        if (filter.IdNEQ is not null)
        {
            predicates.Add(alias + ".\"id\" <> " + args.Add(filter.IdNEQ.Value));
        }

        AppendIdIn(predicates, alias, "id", filter.IdIn, args, negate: false);
        AppendIdIn(predicates, alias, "id", filter.IdNotIn, args, negate: true);

        if (filter.Name is not null)
        {
            predicates.Add(alias + ".\"name\" = " + args.Add(filter.Name));
        }

        if (filter.NameNEQ is not null)
        {
            predicates.Add(alias + ".\"name\" <> " + args.Add(filter.NameNEQ));
        }

        AppendStringIn(predicates, alias, "name", filter.NameIn, args, negate: false);
        AppendStringIn(predicates, alias, "name", filter.NameNotIn, args, negate: true);

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

        // GraphQL Boolean maps to *bool: both true and false install a predicate.
        if (filter.Primary is not null)
        {
            predicates.Add(alias + ".\"primary\" = " + args.Add(filter.Primary.Value));
        }

        if (filter.PrimaryNEQ is not null)
        {
            predicates.Add(alias + ".\"primary\" <> " + args.Add(filter.PrimaryNEQ.Value));
        }

        if (filter.Type is not null)
        {
            predicates.Add(alias + ".\"type\" = " + args.Add(filter.Type));
        }

        if (filter.TypeNEQ is not null)
        {
            predicates.Add(alias + ".\"type\" <> " + args.Add(filter.TypeNEQ));
        }

        AppendStringIn(predicates, alias, "type", filter.TypeIn, args, negate: false);
        AppendStringIn(predicates, alias, "type", filter.TypeNotIn, args, negate: true);

        if (filter.Status is not null)
        {
            predicates.Add(alias + ".\"status\" = " + args.Add(filter.Status));
        }

        if (filter.StatusNEQ is not null)
        {
            predicates.Add(alias + ".\"status\" <> " + args.Add(filter.StatusNEQ));
        }

        AppendStringIn(predicates, alias, "status", filter.StatusIn, args, negate: false);
        AppendStringIn(predicates, alias, "status", filter.StatusNotIn, args, negate: true);

        return ConnectionSql.Combine(predicates);
    }

    /// <summary>Ent skips an empty collection predicate instead of matching nothing.</summary>
    private static void AppendIdIn(
        List<string?> predicates,
        string alias,
        string column,
        IReadOnlyList<long>? values,
        SqlArguments args,
        bool negate)
    {
        if (values is null || values.Count == 0)
        {
            return;
        }

        var parameters = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            parameters[i] = args.Add(values[i]);
        }

        predicates.Add(
            alias + ".\"" + column + "\" " + (negate ? "NOT IN" : "IN")
            + " (" + string.Join(", ", parameters) + ")");
    }

    private static void AppendStringIn(
        List<string?> predicates,
        string alias,
        string column,
        IReadOnlyList<string>? values,
        SqlArguments args,
        bool negate)
    {
        if (values is null || values.Count == 0)
        {
            return;
        }

        var parameters = new string[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            parameters[i] = args.Add(values[i]);
        }

        predicates.Add(
            alias + ".\"" + column + "\" " + (negate ? "NOT IN" : "IN")
            + " (" + string.Join(", ", parameters) + ")");
    }

    /// <summary>
    /// Ent's generated P() rejects a nested where input without predicates:
    /// <c>"ent: empty predicate DataStorageWhereInput: field 'not'"</c>.
    /// </summary>
    private static InvalidOperationException EmptyWhere(string field)
        => new("ent: empty predicate DataStorageWhereInput: field '" + field + "'");
}
