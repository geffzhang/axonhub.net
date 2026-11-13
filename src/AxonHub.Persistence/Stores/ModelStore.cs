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
/// Port of ent.ModelWhereInput: every scalar predicate group exposed by
/// entgql (there are no edge predicates on the Model type).
/// </summary>
public sealed record ModelListFilter
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

    public string? Developer { get; init; }

    public string? DeveloperNEQ { get; init; }

    public IReadOnlyList<string>? DeveloperIn { get; init; }

    public IReadOnlyList<string>? DeveloperNotIn { get; init; }

    public string? DeveloperGT { get; init; }

    public string? DeveloperGTE { get; init; }

    public string? DeveloperLT { get; init; }

    public string? DeveloperLTE { get; init; }

    public string? DeveloperContains { get; init; }

    public string? DeveloperHasPrefix { get; init; }

    public string? DeveloperHasSuffix { get; init; }

    public string? DeveloperEqualFold { get; init; }

    public string? DeveloperContainsFold { get; init; }

    public string? ModelId { get; init; }

    public string? ModelIdNEQ { get; init; }

    /// <summary>ent <c>ModelIDIn</c> (model.ModelIDIn in the reference).</summary>
    public IReadOnlyList<string>? ModelIdIn { get; init; }

    public IReadOnlyList<string>? ModelIdNotIn { get; init; }

    public string? ModelIdGT { get; init; }

    public string? ModelIdGTE { get; init; }

    public string? ModelIdLT { get; init; }

    public string? ModelIdLTE { get; init; }

    public string? ModelIdContains { get; init; }

    public string? ModelIdHasPrefix { get; init; }

    public string? ModelIdHasSuffix { get; init; }

    public string? ModelIdEqualFold { get; init; }

    public string? ModelIdContainsFold { get; init; }

    public string? Type { get; init; }

    public string? TypeNEQ { get; init; }

    public IReadOnlyList<string>? TypeIn { get; init; }

    public IReadOnlyList<string>? TypeNotIn { get; init; }

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

    public string? Icon { get; init; }

    public string? IconNEQ { get; init; }

    public IReadOnlyList<string>? IconIn { get; init; }

    public IReadOnlyList<string>? IconNotIn { get; init; }

    public string? IconGT { get; init; }

    public string? IconGTE { get; init; }

    public string? IconLT { get; init; }

    public string? IconLTE { get; init; }

    public string? IconContains { get; init; }

    public string? IconHasPrefix { get; init; }

    public string? IconHasSuffix { get; init; }

    public string? IconEqualFold { get; init; }

    public string? IconContainsFold { get; init; }

    public string? Group { get; init; }

    public string? GroupNEQ { get; init; }

    public IReadOnlyList<string>? GroupIn { get; init; }

    public IReadOnlyList<string>? GroupNotIn { get; init; }

    public string? GroupGT { get; init; }

    public string? GroupGTE { get; init; }

    public string? GroupLT { get; init; }

    public string? GroupLTE { get; init; }

    public string? GroupContains { get; init; }

    public string? GroupHasPrefix { get; init; }

    public string? GroupHasSuffix { get; init; }

    public string? GroupEqualFold { get; init; }

    public string? GroupContainsFold { get; init; }

    public string? Status { get; init; }

    public string? StatusNEQ { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    public IReadOnlyList<string>? StatusNotIn { get; init; }

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

    /// <summary>
    /// ent <c>Query().Where(model.ModelID(...), model.StatusEQ(status)).First(ctx)</c>
    /// (live rows), used by <c>biz.GetModelByModelID</c> on the routing path.
    /// </summary>
    Task<ModelRecord?> GetFirstByModelIdAndStatusAsync(
        string modelId,
        string status,
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

    public async Task<ModelRecord?> GetFirstByModelIdAndStatusAsync(
        string modelId,
        string status,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT ").Append(Columns)
            .Append(" FROM \"models\" WHERE \"model_id\" = ").Append(args.Add(modelId))
            .Append(" AND \"status\" = ").Append(args.Add(status))
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
            null,
            null,
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
            "developer",
            filter.Developer,
            filter.DeveloperNEQ,
            filter.DeveloperIn,
            filter.DeveloperNotIn,
            filter.DeveloperGT,
            filter.DeveloperGTE,
            filter.DeveloperLT,
            filter.DeveloperLTE,
            filter.DeveloperContains,
            filter.DeveloperHasPrefix,
            filter.DeveloperHasSuffix,
            filter.DeveloperEqualFold,
            filter.DeveloperContainsFold,
            args);
        ConnectionSql.AppendStringPredicates(
            predicates,
            alias,
            "model_id",
            filter.ModelId,
            filter.ModelIdNEQ,
            filter.ModelIdIn,
            filter.ModelIdNotIn,
            filter.ModelIdGT,
            filter.ModelIdGTE,
            filter.ModelIdLT,
            filter.ModelIdLTE,
            filter.ModelIdContains,
            filter.ModelIdHasPrefix,
            filter.ModelIdHasSuffix,
            filter.ModelIdEqualFold,
            filter.ModelIdContainsFold,
            args);
        ConnectionSql.AppendStringPredicates(
            predicates,
            alias,
            "type",
            filter.Type,
            filter.TypeNEQ,
            filter.TypeIn,
            filter.TypeNotIn,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
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
            "icon",
            filter.Icon,
            filter.IconNEQ,
            filter.IconIn,
            filter.IconNotIn,
            filter.IconGT,
            filter.IconGTE,
            filter.IconLT,
            filter.IconLTE,
            filter.IconContains,
            filter.IconHasPrefix,
            filter.IconHasSuffix,
            filter.IconEqualFold,
            filter.IconContainsFold,
            args);
        ConnectionSql.AppendStringPredicates(
            predicates,
            alias,
            "group",
            filter.Group,
            filter.GroupNEQ,
            filter.GroupIn,
            filter.GroupNotIn,
            filter.GroupGT,
            filter.GroupGTE,
            filter.GroupLT,
            filter.GroupLTE,
            filter.GroupContains,
            filter.GroupHasPrefix,
            filter.GroupHasSuffix,
            filter.GroupEqualFold,
            filter.GroupContainsFold,
            args);
        ConnectionSql.AppendStringPredicates(
            predicates,
            alias,
            "status",
            filter.Status,
            filter.StatusNEQ,
            filter.StatusIn,
            filter.StatusNotIn,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            args);
        ConnectionSql.AppendStringPredicates(
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
