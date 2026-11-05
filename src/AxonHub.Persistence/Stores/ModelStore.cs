using System.Text;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>Ordering fields exposed by the reference ModelOrder type.</summary>
public enum ModelSortField
{
    Id,
    UpdatedAt,
    Name,
}

/// <summary>
/// Subset of ent.ModelWhereInput used by the management frontend. Every other
/// ent predicate (boolean operators over time fields, string prefix/suffix
/// variants, idNEQ, ...) is not ported in this slice (C177).
/// </summary>
public sealed record ModelListFilter
{
    public long? Id { get; init; }

    public IReadOnlyList<long>? IdIn { get; init; }

    public string? Developer { get; init; }

    public string? DeveloperContainsFold { get; init; }

    public string? ModelId { get; init; }

    /// <summary>ent <c>ModelIDIn</c> (model.ModelIDIn in the reference).</summary>
    public IReadOnlyList<string>? ModelIdIn { get; init; }

    public string? ModelIdContainsFold { get; init; }

    public string? Type { get; init; }

    public IReadOnlyList<string>? TypeIn { get; init; }

    public IReadOnlyList<string>? TypeNotIn { get; init; }

    public string? Name { get; init; }

    public string? NameContainsFold { get; init; }

    public string? Status { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    public IReadOnlyList<string>? StatusNotIn { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool RemarkIsNil { get; init; }

    /// <summary>Ent reads the GraphQL field as a plain bool, so only true applies.</summary>
    public bool RemarkNotNil { get; init; }

    public ModelListFilter? Not { get; init; }

    public IReadOnlyList<ModelListFilter>? And { get; init; }

    public IReadOnlyList<ModelListFilter>? Or { get; init; }
}

/// <summary>Entgql pagination request for models.</summary>
public sealed record ModelListQuery
{
    public ModelListFilter? Where { get; init; }

    public ModelSortField SortField { get; init; } = ModelSortField.Id;

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }
}

/// <summary>
/// Field set of an ent <c>Model.UpdateOneID(...)</c> call. The store renders
/// the SET list in the generated builder's fixed order (updated_at, then
/// developer/model_id/type/name/icon/group/model_card/settings/status/remark),
/// with a cleared remark emitted after the assignments exactly like ent's
/// <c>ClearField</c> (internal/ent/model_update.go:600-653).
/// </summary>
public sealed record ModelUpdate
{
    public required long Id { get; init; }

    public string? Developer { get; init; }

    public string? ModelId { get; init; }

    public string? Type { get; init; }

    public string? Name { get; init; }

    public string? Icon { get; init; }

    public string? Group { get; init; }

    /// <summary>objects.ModelCard JSON (SetModelCard).</summary>
    public string? ModelCard { get; init; }

    /// <summary>objects.ModelSettings JSON (SetSettings).</summary>
    public string? Settings { get; init; }

    public string? Status { get; init; }

    public string? Remark { get; init; }

    public bool ClearRemark { get; init; }
}

/// <summary>A (developer, model_id) pair used by the bulk-create existence check.</summary>
public readonly record struct ModelDeveloperModelId(string Developer, string ModelId);

/// <summary>Persistence seam for the reference <c>models</c> table.</summary>
public interface IModelStore
{
    /// <summary>
    /// Port of ent's RunInTransaction: joins the ambient transaction when one
    /// is open and commits/rolls back its own otherwise.
    /// </summary>
    Task<T> RunInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default);

    Task RunInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ent <c>Model.Create().Save()</c>: applies the schema defaults
    /// (type chat, status disabled) and inserts through EF Core.
    /// </summary>
    Task<long> CreateAsync(ModelRecord model, CancellationToken cancellationToken = default);

    /// <summary>ent <c>Model.CreateBulk(...).Save()</c>: one transaction.</summary>
    Task<IReadOnlyList<ModelRecord>> CreateBulkAsync(
        IReadOnlyList<ModelRecord> models,
        CancellationToken cancellationToken = default);

    /// <summary>Privacy-filtered live read (soft-delete predicate included).</summary>
    Task<ModelRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default);

    /// <summary>ent <c>Query().Where(model.ModelID(...)).First(ctx)</c> (live rows).</summary>
    Task<ModelRecord?> GetFirstByModelIdAsync(
        string modelId,
        CancellationToken cancellationToken = default);

    /// <summary>Port of the bulk-create existence check's OR-of-AND predicate.</summary>
    Task<IReadOnlyList<ModelRecord>> ListByDeveloperModelIdPairsAsync(
        IReadOnlyList<ModelDeveloperModelId> pairs,
        CancellationToken cancellationToken = default);

    /// <summary>ent <c>Query().Where(...).All(ctx)</c>: live rows, id ascending.</summary>
    Task<IReadOnlyList<ModelRecord>> ListAllAsync(
        ModelListFilter filter,
        CancellationToken cancellationToken = default);

    Task<ListPage<ModelRecord>> ListAsync(
        ModelListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Single-row update in ent's SET order, then a full re-read by id (no
    /// soft-delete predicate, no privacy — like the generated UpdateOneID).
    /// Returns null when no row matched (ent NotFound).
    /// </summary>
    Task<ModelRecord?> UpdateAsync(
        ModelUpdate update,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregate update (ent <c>Model.Update().Where(...).SetStatus().Save()</c>):
    /// sets updated_at and status, has no soft-delete predicate, and returns the
    /// affected row count.
    /// </summary>
    Task<int> BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        string status,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-delete update (ent DeleteOneID through the SoftDeleteMixin hook):
    /// <c>UPDATE models SET updated_at = now, deleted_at = unix WHERE id = @id
    /// AND deleted_at = 0</c>. Returns the affected row count.
    /// </summary>
    Task<int> DeleteAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-delete update for many ids (ent <c>Model.Delete().Where(IDIn)</c>):
    /// no-op for an empty id list.
    /// </summary>
    Task<int> BulkDeleteAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>models</c> table. Writes go through EF Core and
/// Dapper; reads use Dapper and honour the SoftDeleteMixin predicate
/// (<c>deleted_at = 0</c>).
/// </summary>
public sealed class ModelStore(AxonHubDbContext db) : IModelStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "developer", "model_id",
        "type", "name", "icon", "group", "model_card", "settings", "status", "remark"
        """;

    private const string DefaultModelCard =
        """{"reasoning":{"supported":false,"default":false},"reasoningEfforts":null,"toolCall":false,"temperature":false,"modalities":{"input":null,"output":null},"vision":false,"cost":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0},"limit":{"context":0,"output":0},"knowledge":"","releaseDate":"","lastUpdated":""}""";

    private const string DefaultSettings =
        """{"disableDeveloperSettingsInheritance":false,"associations":null,"loadBalancerStrategy":"default","traceStickyMode":"default"}""";

    public async Task<T> RunInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        // ent's RunInTransaction reuses the enclosing logical transaction and
        // only opens one for a standalone call.
        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false)
            : null;

        try
        {
            var result = await action(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
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

    public Task RunInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        return RunInTransactionAsync(
            async token =>
            {
                await action(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public async Task<long> CreateAsync(ModelRecord model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        ApplyCreateDefaults(model);

        db.Models.Add(model);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return model.Id;
    }

    public Task<IReadOnlyList<ModelRecord>> CreateBulkAsync(
        IReadOnlyList<ModelRecord> models,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);

        if (models.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<ModelRecord>>([]);
        }

        return RunInTransactionAsync(
            async token =>
            {
                foreach (var model in models)
                {
                    ApplyCreateDefaults(model);
                    db.Models.Add(model);
                }

                await db.SaveChangesAsync(token).ConfigureAwait(false);
                return (IReadOnlyList<ModelRecord>)models.ToList();
            },
            cancellationToken);
    }

    public async Task<ModelRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT ").Append(Columns)
            .Append(" FROM \"models\" WHERE \"id\" = ").Append(args.Add(id))
            .Append(" AND \"deleted_at\" = 0;");

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ModelRecord>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<ModelRecord?> GetFirstByModelIdAsync(
        string modelId,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT ").Append(Columns)
            .Append(" FROM \"models\" WHERE \"model_id\" = ").Append(args.Add(modelId))
            .Append(" AND \"deleted_at\" = 0 ORDER BY \"id\" ASC LIMIT 1;");

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ModelRecord>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ModelRecord>> ListByDeveloperModelIdPairsAsync(
        IReadOnlyList<ModelDeveloperModelId> pairs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        // sql.Or() over zero predicates renders an always-false condition, which
        // is what the reference's Where(func) closure produces for empty input.
        if (pairs.Count == 0)
        {
            return [];
        }

        var args = new SqlArguments();
        var predicates = new string[pairs.Count];
        for (var i = 0; i < pairs.Count; i++)
        {
            predicates[i] = "(\"developer\" = " + args.Add(pairs[i].Developer)
                + " AND \"model_id\" = " + args.Add(pairs[i].ModelId) + ")";
        }

        var sql = "SELECT " + Columns + " FROM \"models\" WHERE \"deleted_at\" = 0 AND ("
            + string.Join(" OR ", predicates) + ") ORDER BY \"id\" ASC;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ModelRecord>(
            new CommandDefinition(
                sql,
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<ModelRecord>> ListAllAsync(
        ModelListFilter filter,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var where = BuildFilter(filter, "m", args);
        var sql = new StringBuilder("SELECT ");
        sql.Append(string.Join(", ", Columns
            .Split(',')
            .Select(static column => "m." + column.Trim())));
        sql.Append(" FROM \"models\" m WHERE m.\"deleted_at\" = 0");
        if (where is not null)
        {
            sql.Append(" AND ").Append(where);
        }

        sql.Append(" ORDER BY m.\"id\" ASC;");

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ModelRecord>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<ListPage<ModelRecord>> ListAsync(
        ModelListQuery query,
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
            ModelSortField.UpdatedAt => "updated_at",
            ModelSortField.Name => "name",
            _ => null,
        };
        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;
        const string from = " FROM \"models\" m";
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
        var nodes = (await connection.QueryAsync<ModelRecord>(
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

        return new ListPage<ModelRecord>(nodes, hasNextPage, hasPreviousPage, count);
    }

    public async Task<ModelRecord?> UpdateAsync(
        ModelUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var args = new SqlArguments();

        // Ent's generated sqlSave applies the fields in schema order:
        // updated_at, developer, model_id, type, name, icon, group, model_card,
        // settings, status, remark; ClearField("remark") follows the SET list.
        var sets = new List<string>(11)
        {
            "\"updated_at\" = " + args.Add(DateTime.UtcNow),
        };
        if (update.Developer is not null)
        {
            sets.Add("\"developer\" = " + args.Add(update.Developer));
        }

        if (update.ModelId is not null)
        {
            sets.Add("\"model_id\" = " + args.Add(update.ModelId));
        }

        if (update.Type is not null)
        {
            sets.Add("\"type\" = " + args.Add(update.Type));
        }

        if (update.Name is not null)
        {
            sets.Add("\"name\" = " + args.Add(update.Name));
        }

        if (update.Icon is not null)
        {
            sets.Add("\"icon\" = " + args.Add(update.Icon));
        }

        if (update.Group is not null)
        {
            sets.Add("\"group\" = " + args.Add(update.Group));
        }

        if (update.ModelCard is not null)
        {
            sets.Add("\"model_card\" = " + JsonCast(args.Add(update.ModelCard)));
        }

        if (update.Settings is not null)
        {
            sets.Add("\"settings\" = " + JsonCast(args.Add(update.Settings)));
        }

        if (update.Status is not null)
        {
            sets.Add("\"status\" = " + args.Add(update.Status));
        }

        if (update.Remark is not null)
        {
            sets.Add("\"remark\" = " + args.Add(update.Remark));
        }

        if (update.ClearRemark)
        {
            sets.Add("\"remark\" = NULL");
        }

        var sql = new StringBuilder("UPDATE \"models\" SET ")
            .Append(string.Join(", ", sets))
            .Append(" WHERE \"id\" = ").Append(args.Add(update.Id))
            .Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        if (affected == 0)
        {
            return null;
        }

        // The generated UpdateOneID re-selects every column by id: no privacy
        // predicate and no soft-delete predicate.
        var reread = "SELECT " + Columns + " FROM \"models\" WHERE \"id\" = @Id;";
        return await connection.QuerySingleAsync<ModelRecord>(
            new CommandDefinition(
                reread,
                new { Id = update.Id },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        string status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            // model.IDIn() with no ids renders an always-false predicate; the
            // statement still runs, affecting zero rows.
            return 0;
        }

        var args = new SqlArguments();
        var parameters = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            parameters[i] = args.Add(ids[i]);
        }

        // Aggregate updates have no soft-delete predicate (the interceptor only
        // touches queries) and default updated_at before status.
        var sql = new StringBuilder("UPDATE \"models\" SET \"updated_at\" = ")
            .Append(args.Add(DateTime.UtcNow))
            .Append(", \"status\" = ").Append(args.Add(status))
            .Append(" WHERE \"id\" IN (").Append(string.Join(", ", parameters)).Append(");");

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("UPDATE \"models\" SET \"updated_at\" = ")
            .Append(args.Add(DateTime.UtcNow))
            .Append(", \"deleted_at\" = ").Append(args.Add(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            .Append(" WHERE \"id\" = ").Append(args.Add(id))
            .Append(" AND \"deleted_at\" = 0;");

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> BulkDeleteAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return 0;
        }

        var args = new SqlArguments();
        var parameters = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            parameters[i] = args.Add(ids[i]);
        }

        var sql = new StringBuilder("UPDATE \"models\" SET \"updated_at\" = ")
            .Append(args.Add(DateTime.UtcNow))
            .Append(", \"deleted_at\" = ").Append(args.Add(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            .Append(" WHERE \"id\" IN (").Append(string.Join(", ", parameters))
            .Append(") AND \"deleted_at\" = 0;");

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static void ApplyCreateDefaults(ModelRecord model)
    {
        if (model.CreatedAt == default)
        {
            model.CreatedAt = DateTime.UtcNow;
        }

        if (model.UpdatedAt == default)
        {
            model.UpdatedAt = model.CreatedAt;
        }

        // Ent schema defaults: type=chat, status=disabled. JSON fields are
        // required in the reference; the management path always supplies them,
        // and the zero values are written when a caller does not.
        model.Type = string.IsNullOrEmpty(model.Type) ? "chat" : model.Type;
        model.Status = string.IsNullOrEmpty(model.Status) ? "disabled" : model.Status;
        model.ModelCard = string.IsNullOrEmpty(model.ModelCard) ? DefaultModelCard : model.ModelCard;
        model.Settings = string.IsNullOrEmpty(model.Settings) ? DefaultSettings : model.Settings;
    }

    private static string JsonCast(string parameter) => "CAST(" + parameter + " AS jsonb)";

    private static string? BuildFilter(ModelListFilter? filter, string alias, SqlArguments args)
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

        if (filter.Developer is not null)
        {
            predicates.Add(alias + ".\"developer\" = " + args.Add(filter.Developer));
        }

        if (filter.DeveloperContainsFold is not null)
        {
            ConnectionSql.AppendStringFilter(
                predicates,
                alias,
                "developer",
                "LIKE",
                filter.DeveloperContainsFold,
                args);
        }

        if (filter.ModelId is not null)
        {
            predicates.Add(alias + ".\"model_id\" = " + args.Add(filter.ModelId));
        }

        if (filter.ModelIdIn is not null)
        {
            ConnectionSql.AppendStringIn(predicates, alias, "model_id", filter.ModelIdIn, args);
        }

        if (filter.ModelIdContainsFold is not null)
        {
            ConnectionSql.AppendStringFilter(
                predicates,
                alias,
                "model_id",
                "LIKE",
                filter.ModelIdContainsFold,
                args);
        }

        if (filter.Type is not null)
        {
            predicates.Add(alias + ".\"type\" = " + args.Add(filter.Type));
        }

        if (filter.TypeIn is not null)
        {
            ConnectionSql.AppendStringIn(predicates, alias, "type", filter.TypeIn, args);
        }

        if (filter.TypeNotIn is not null)
        {
            if (filter.TypeNotIn.Count == 0)
            {
                predicates.Add("TRUE");
            }
            else
            {
                var parameters = filter.TypeNotIn
                    .Select(value => args.Add(value))
                    .ToArray();
                predicates.Add(
                    alias + ".\"type\" NOT IN (" + string.Join(", ", parameters) + ")");
            }
        }

        if (filter.Name is not null)
        {
            predicates.Add(alias + ".\"name\" = " + args.Add(filter.Name));
        }

        if (filter.NameContainsFold is not null)
        {
            ConnectionSql.AppendStringFilter(predicates, alias, "name", "LIKE", filter.NameContainsFold, args);
        }

        if (filter.Status is not null)
        {
            predicates.Add(alias + ".\"status\" = " + args.Add(filter.Status));
        }

        if (filter.StatusIn is not null)
        {
            ConnectionSql.AppendStringIn(predicates, alias, "status", filter.StatusIn, args);
        }

        if (filter.StatusNotIn is not null)
        {
            if (filter.StatusNotIn.Count == 0)
            {
                predicates.Add("TRUE");
            }
            else
            {
                var parameters = filter.StatusNotIn
                    .Select(value => args.Add(value))
                    .ToArray();
                predicates.Add(
                    alias + ".\"status\" NOT IN (" + string.Join(", ", parameters) + ")");
            }
        }

        if (filter.RemarkIsNil)
        {
            predicates.Add(alias + ".\"remark\" IS NULL");
        }

        if (filter.RemarkNotNil)
        {
            predicates.Add(alias + ".\"remark\" IS NOT NULL");
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
            predicates.Add(or.Length == 0 ? "FALSE" : "(" + string.Join(" OR ", or) + ")");
        }

        return ConnectionSql.Combine(predicates);
    }
}
