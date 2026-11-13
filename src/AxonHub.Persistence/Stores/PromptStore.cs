using System.Text;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>Ordering fields exposed by the reference PromptOrder type.</summary>
public enum PromptSortField
{
    Id,
    UpdatedAt,
    Order,
}

/// <summary>
/// Port of ent.PromptWhereInput: every scalar predicate group and the project
/// edge predicates.
/// </summary>
public sealed record PromptListFilter
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

    public string? Role { get; init; }

    public string? RoleNEQ { get; init; }

    public IReadOnlyList<string>? RoleIn { get; init; }

    public IReadOnlyList<string>? RoleNotIn { get; init; }

    public string? RoleGT { get; init; }

    public string? RoleGTE { get; init; }

    public string? RoleLT { get; init; }

    public string? RoleLTE { get; init; }

    public string? RoleContains { get; init; }

    public string? RoleHasPrefix { get; init; }

    public string? RoleHasSuffix { get; init; }

    public string? RoleEqualFold { get; init; }

    public string? RoleContainsFold { get; init; }

    public string? Content { get; init; }

    public string? ContentNEQ { get; init; }

    public IReadOnlyList<string>? ContentIn { get; init; }

    public IReadOnlyList<string>? ContentNotIn { get; init; }

    public string? ContentGT { get; init; }

    public string? ContentGTE { get; init; }

    public string? ContentLT { get; init; }

    public string? ContentLTE { get; init; }

    public string? ContentContains { get; init; }

    public string? ContentHasPrefix { get; init; }

    public string? ContentHasSuffix { get; init; }

    public string? ContentEqualFold { get; init; }

    public string? ContentContainsFold { get; init; }

    public string? Status { get; init; }

    public string? StatusNEQ { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    public IReadOnlyList<string>? StatusNotIn { get; init; }

    public long? Order { get; init; }

    public long? OrderNEQ { get; init; }

    public IReadOnlyList<long>? OrderIn { get; init; }

    public IReadOnlyList<long>? OrderNotIn { get; init; }

    public long? OrderGT { get; init; }

    public long? OrderGTE { get; init; }

    public long? OrderLT { get; init; }

    public long? OrderLTE { get; init; }

    public bool? HasProject { get; init; }

    public IReadOnlyList<ProjectListFilter>? HasProjectWith { get; init; }

    public PromptListFilter? Not { get; init; }

    public IReadOnlyList<PromptListFilter>? And { get; init; }

    public IReadOnlyList<PromptListFilter>? Or { get; init; }
}

/// <summary>Entgql pagination request for prompts.</summary>
public sealed record PromptListQuery
{
    public PromptListFilter? Where { get; init; }

    /// <summary>
    /// The project predicate of the Prompt query privacy policy. Null means the
    /// policy allowed the query without a filter (owner or system scope with no
    /// project in context).
    /// </summary>
    public long? ProjectScope { get; init; }

    public PromptSortField SortField { get; init; } = PromptSortField.Id;

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }
}

/// <summary>
/// Field set of an ent <c>Prompt.Update()</c> call. The store renders the SET
/// list in the generated builder's fixed order (updated_at, name, description,
/// role, content, status, order, settings),
/// internal/ent/prompt_update.go:231-286.
/// </summary>
public sealed record PromptUpdate
{
    public required long Id { get; init; }

    /// <summary>prompt.ProjectIDEQ: the reference always adds this predicate.</summary>
    public required long ProjectId { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Role { get; init; }

    public string? Content { get; init; }

    public string? Status { get; init; }

    public long? Order { get; init; }

    /// <summary>objects.PromptSettings JSON (SetSettings).</summary>
    public string? Settings { get; init; }
}

/// <summary>
/// Persistence seam for the reference <c>prompts</c> table
/// (internal/ent/schema/prompt.go).
/// </summary>
public interface IPromptStore
{
    Task<long> CreateAsync(PromptRecord prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Live, enabled prompts of one project. This is the query the reference's
    /// PromptService runs to fill its per-project cache (biz/prompt.go,
    /// loadPrompts); Ent's SoftDeleteMixin adds the deleted_at = 0 predicate
    /// and the query declares no ordering.
    /// </summary>
    Task<IReadOnlyList<PromptRecord>> ListEnabledByProjectAsync(
        long projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ent <c>Prompt.Query().Where(ID, [ProjectIDEQ]).Only(ctx)</c>: live rows;
    /// <paramref name="projectScope"/> is the project predicate produced by the
    /// query privacy policy (null when the policy added none).
    /// </summary>
    Task<PromptRecord?> GetByIdAsync(
        long id,
        long? projectScope,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ent <c>Prompt.Query().Where(ProjectIDEQ, NameEQ[, IDNEQ]).Exist(ctx)</c>
    /// (live rows).
    /// </summary>
    Task<bool> ExistsByNameAsync(
        long projectId,
        string name,
        long? excludeId,
        CancellationToken cancellationToken = default);

    Task<ListPage<PromptRecord>> ListAsync(
        PromptListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Aggregate update in ent's SET order (ent <c>Prompt.Update()</c> with the
    /// id and project predicates). Aggregate updates have no soft-delete
    /// predicate and return the affected row count.
    /// </summary>
    Task<int> UpdateAsync(PromptUpdate update, CancellationToken cancellationToken = default);

    /// <summary>Aggregate status update (no soft-delete predicate).</summary>
    Task<int> UpdateStatusAsync(
        long id,
        long projectId,
        string status,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-delete update (ent Delete through the SoftDeleteMixin hook):
    /// <c>UPDATE prompts SET updated_at = now, deleted_at = unix WHERE id = @id
    /// AND project_id = @project AND deleted_at = 0</c>. Returns the affected
    /// row count.
    /// </summary>
    Task<int> DeleteAsync(
        long id,
        long projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-delete update for many ids (ent <c>Prompt.Delete().Where(IDIn,
    /// ProjectIDEQ)</c>): no-op for an empty id list.
    /// </summary>
    Task<int> BulkDeleteAsync(
        IReadOnlyList<long> ids,
        long projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Status update for many ids (ent <c>Prompt.Update().Where(IDIn,
    /// ProjectIDEQ).SetStatus</c>): no soft-delete predicate; no-op for an
    /// empty id list.
    /// </summary>
    Task<int> BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        long projectId,
        string status,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>prompts</c> table. Writes go through EF Core and
/// Dapper; reads use Dapper and honour the SoftDeleteMixin predicate
/// (<c>deleted_at = 0</c>).
/// </summary>
public sealed class PromptStore(AxonHubDbContext db) : IPromptStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "name", "description",
        "role", "content", "status", "order", "settings", "project_id"
        """;

    public async Task<long> CreateAsync(PromptRecord prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        ApplyCreateDefaults(prompt);

        db.Prompts.Add(prompt);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return prompt.Id;
        }
        catch (DbUpdateException)
        {
            // Ent's client keeps no failed-entity state; detaching keeps a
            // later write on the same scoped context from retrying this row.
            db.Entry(prompt).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<IReadOnlyList<PromptRecord>> ListEnabledByProjectAsync(
        long projectId,
        CancellationToken cancellationToken = default)
    {
        // The reference query is
        //   client.Prompt.Query().
        //     Where(prompt.ProjectID(projectID), prompt.StatusEQ(prompt.StatusEnabled)).
        //     All(ctx)
        // and Ent's SoftDeleteMixin adds the deleted_at = 0 predicate. Ent adds
        // no ORDER BY, so neither does the port.
        var sql = "SELECT " + Columns
            + " FROM \"prompts\""
            + " WHERE \"project_id\" = @ProjectId"
            + " AND \"status\" = 'enabled' AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<PromptRecord>(
            new CommandDefinition(
                sql,
                new { ProjectId = projectId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<PromptRecord?> GetByIdAsync(
        long id,
        long? projectScope,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT ").Append(Columns)
            .Append(" FROM \"prompts\" WHERE \"id\" = ").Append(args.Add(id))
            .Append(" AND \"deleted_at\" = 0");
        if (projectScope is not null)
        {
            sql.Append(" AND \"project_id\" = ").Append(args.Add(projectScope.Value));
        }

        sql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<PromptRecord>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> ExistsByNameAsync(
        long projectId,
        string name,
        long? excludeId,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("SELECT EXISTS (SELECT 1 FROM \"prompts\" WHERE \"project_id\" = ")
            .Append(args.Add(projectId))
            .Append(" AND \"name\" = ").Append(args.Add(name))
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

    public async Task<ListPage<PromptRecord>> ListAsync(
        PromptListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var args = new SqlArguments();
        var where = BuildFilter(query.Where, "m", args);
        var conditions = new StringBuilder(" WHERE m.\"deleted_at\" = 0");
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
            PromptSortField.UpdatedAt => "updated_at",
            PromptSortField.Order => "order",
            _ => null,
        };
        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;
        const string from = " FROM \"prompts\" m";
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
        var nodes = (await connection.QueryAsync<PromptRecord>(
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

        return new ListPage<PromptRecord>(nodes, hasNextPage, hasPreviousPage, count);
    }

    public async Task<int> UpdateAsync(
        PromptUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var args = new SqlArguments();

        // Ent's generated sqlSave applies the fields in schema order:
        // updated_at, name, description, role, content, status, order, settings.
        var sets = new List<string>(8)
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

        if (update.Role is not null)
        {
            sets.Add("\"role\" = " + args.Add(update.Role));
        }

        if (update.Content is not null)
        {
            sets.Add("\"content\" = " + args.Add(update.Content));
        }

        if (update.Status is not null)
        {
            sets.Add("\"status\" = " + args.Add(update.Status));
        }

        if (update.Order is not null)
        {
            sets.Add("\"order\" = " + args.Add(update.Order.Value));
        }

        if (update.Settings is not null)
        {
            sets.Add("\"settings\" = " + JsonCast(args.Add(update.Settings)));
        }

        var sql = new StringBuilder("UPDATE \"prompts\" SET ")
            .Append(string.Join(", ", sets))
            .Append(" WHERE \"id\" = ").Append(args.Add(update.Id))
            .Append(" AND \"project_id\" = ").Append(args.Add(update.ProjectId))
            .Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> UpdateStatusAsync(
        long id,
        long projectId,
        string status,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();

        // Aggregate updates have no soft-delete predicate (the interceptor only
        // touches queries) and default updated_at before status.
        var sql = new StringBuilder("UPDATE \"prompts\" SET \"updated_at\" = ")
            .Append(args.Add(DateTime.UtcNow))
            .Append(", \"status\" = ").Append(args.Add(status))
            .Append(" WHERE \"id\" = ").Append(args.Add(id))
            .Append(" AND \"project_id\" = ").Append(args.Add(projectId))
            .Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> DeleteAsync(
        long id,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("UPDATE \"prompts\" SET \"updated_at\" = ")
            .Append(args.Add(DateTime.UtcNow))
            .Append(", \"deleted_at\" = ").Append(args.Add(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            .Append(" WHERE \"id\" = ").Append(args.Add(id))
            .Append(" AND \"project_id\" = ").Append(args.Add(projectId))
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
        long projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            // prompt.IDIn() with no ids renders an always-false predicate; the
            // statement still runs, affecting zero rows.
            return 0;
        }

        var args = new SqlArguments();
        var parameters = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            parameters[i] = args.Add(ids[i]);
        }

        var sql = new StringBuilder("UPDATE \"prompts\" SET \"updated_at\" = ")
            .Append(args.Add(DateTime.UtcNow))
            .Append(", \"deleted_at\" = ").Append(args.Add(DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            .Append(" WHERE \"id\" IN (").Append(string.Join(", ", parameters))
            .Append(") AND \"project_id\" = ").Append(args.Add(projectId))
            .Append(" AND \"deleted_at\" = 0;");

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        long projectId,
        string status,
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

        var sql = new StringBuilder("UPDATE \"prompts\" SET \"updated_at\" = ")
            .Append(args.Add(DateTime.UtcNow))
            .Append(", \"status\" = ").Append(args.Add(status))
            .Append(" WHERE \"id\" IN (").Append(string.Join(", ", parameters))
            .Append(") AND \"project_id\" = ").Append(args.Add(projectId))
            .Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static void ApplyCreateDefaults(PromptRecord prompt)
    {
        if (prompt.CreatedAt == default)
        {
            prompt.CreatedAt = DateTime.UtcNow;
        }

        if (prompt.UpdatedAt == default)
        {
            prompt.UpdatedAt = prompt.CreatedAt;
        }

        // Ent schema defaults: status=disabled; the management path always
        // supplies the other required fields.
        prompt.Status = string.IsNullOrEmpty(prompt.Status) ? "disabled" : prompt.Status;
    }

    private static string JsonCast(string parameter) => "CAST(" + parameter + " AS jsonb)";

    internal static string? BuildFilter(PromptListFilter? filter, string alias, SqlArguments args)
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

        ConnectionSql.AppendLongPredicates(
            predicates,
            alias,
            "project_id",
            filter.ProjectId,
            filter.ProjectIdNEQ,
            filter.ProjectIdIn,
            filter.ProjectIdNotIn,
            greaterThan: null,
            greaterThanOrEqual: null,
            lessThan: null,
            lessThanOrEqual: null,
            isNil: null,
            notNil: null,
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

        ConnectionSql.AppendStringPredicates(
            predicates,
            alias,
            "role",
            filter.Role,
            filter.RoleNEQ,
            filter.RoleIn,
            filter.RoleNotIn,
            filter.RoleGT,
            filter.RoleGTE,
            filter.RoleLT,
            filter.RoleLTE,
            filter.RoleContains,
            filter.RoleHasPrefix,
            filter.RoleHasSuffix,
            filter.RoleEqualFold,
            filter.RoleContainsFold,
            args);

        ConnectionSql.AppendStringPredicates(
            predicates,
            alias,
            "content",
            filter.Content,
            filter.ContentNEQ,
            filter.ContentIn,
            filter.ContentNotIn,
            filter.ContentGT,
            filter.ContentGTE,
            filter.ContentLT,
            filter.ContentLTE,
            filter.ContentContains,
            filter.ContentHasPrefix,
            filter.ContentHasSuffix,
            filter.ContentEqualFold,
            filter.ContentContainsFold,
            args);

        ConnectionSql.AppendComparison(predicates, alias, "status", filter.Status, "=", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "status", filter.StatusNEQ, "<>", args);
        ConnectionSql.AppendStringIn(predicates, alias, "status", filter.StatusIn, args);
        ConnectionSql.AppendStringIn(
            predicates, alias, "status", filter.StatusNotIn, args, negate: true);

        ConnectionSql.AppendLongPredicates(
            predicates,
            alias,
            "order",
            filter.Order,
            filter.OrderNEQ,
            filter.OrderIn,
            filter.OrderNotIn,
            filter.OrderGT,
            filter.OrderGTE,
            filter.OrderLT,
            filter.OrderLTE,
            isNil: null,
            notNil: null,
            args);

        if (filter.HasProject is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"projects\" pj2 WHERE pj2.\"id\" = "
                + alias + ".\"project_id\")";
            predicates.Add(filter.HasProject.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasProjectWith is { Count: > 0 } projectFilters)
        {
            // Generated sqlgraph.HasNeighborsWith ANDs every predicate inside a
            // single EXISTS and does not re-apply the target soft-delete predicate.
            var projectPredicates = projectFilters
                .Select(project => ProjectStore.BuildFilter(project, "pj2", args))
                .Where(static project => project is not null)
                .ToArray();
            var projectCondition = ConnectionSql.Combine(projectPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"projects\" pj2"
                + " WHERE pj2.\"id\" = " + alias + ".\"project_id\""
                + (projectCondition is null ? string.Empty : " AND " + projectCondition)
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
            predicates.Add(or.Length == 0 ? "FALSE" : "(" + string.Join(" OR ", or) + ")");
        }

        return ConnectionSql.Combine(predicates);
    }
}
