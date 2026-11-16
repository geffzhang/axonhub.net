using System.Text;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>Ordering fields exposed by the reference PromptProtectionRuleOrder type.</summary>
public enum PromptProtectionRuleSortField
{
    Id,
    UpdatedAt,
    Name,
}

/// <summary>Port of ent.PromptProtectionRuleWhereInput (every predicate group).</summary>
public sealed record PromptProtectionRuleListFilter
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

    public string? Pattern { get; init; }

    public string? PatternNEQ { get; init; }

    public IReadOnlyList<string>? PatternIn { get; init; }

    public IReadOnlyList<string>? PatternNotIn { get; init; }

    public string? PatternGT { get; init; }

    public string? PatternGTE { get; init; }

    public string? PatternLT { get; init; }

    public string? PatternLTE { get; init; }

    public string? PatternContains { get; init; }

    public string? PatternHasPrefix { get; init; }

    public string? PatternHasSuffix { get; init; }

    public string? PatternEqualFold { get; init; }

    public string? PatternContainsFold { get; init; }

    public string? Status { get; init; }

    public string? StatusNEQ { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    public IReadOnlyList<string>? StatusNotIn { get; init; }

    public PromptProtectionRuleListFilter? Not { get; init; }

    public IReadOnlyList<PromptProtectionRuleListFilter>? And { get; init; }

    public IReadOnlyList<PromptProtectionRuleListFilter>? Or { get; init; }
}

/// <summary>Entgql pagination request for prompt protection rules.</summary>
public sealed record PromptProtectionRuleListQuery
{
    public PromptProtectionRuleListFilter? Where { get; init; }

    public PromptProtectionRuleSortField SortField { get; init; } = PromptProtectionRuleSortField.Id;

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }
}

/// <summary>
/// Field set of an ent <c>PromptProtectionRule.UpdateOneID(id).SetInput()</c>
/// call. The store renders the SET list in the generated builder's fixed order
/// (updated_at, name, description, pattern, status, settings),
/// internal/ent/promptprotectionrule_update.go:437-460.
/// </summary>
public sealed record PromptProtectionRuleUpdate
{
    public required long Id { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? Pattern { get; init; }

    public string? Status { get; init; }

    /// <summary>objects.PromptProtectionSettings JSON (SetSettings).</summary>
    public string? Settings { get; init; }
}

/// <summary>
/// Persistence seam for the reference <c>prompt_protection_rules</c> table
/// (internal/ent/schema/prompt_protection_rule.go).
/// </summary>
public interface IPromptProtectionRuleStore
{
    Task<long> CreateAsync(
        PromptProtectionRuleRecord rule,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Live rules with status <c>enabled</c>, ordered by id. This is the query
    /// the reference rule service runs to fill its live rule cache
    /// (internal/server/biz/prompt_protection_rule.go, ListEnabled).
    /// </summary>
    Task<IReadOnlyList<PromptProtectionRuleRecord>> ListEnabledAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ent <c>PromptProtectionRule.Get(ctx, id)</c>: live rows only, because the
    /// SoftDeleteMixin interceptor adds the deleted_at = 0 predicate.
    /// </summary>
    Task<PromptProtectionRuleRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ent <c>PromptProtectionRule.Query().Where(NameEQ(name)).First(ctx)</c>
    /// as used by the create path's duplicate check (live rows only).
    /// </summary>
    Task<bool> ExistsByNameAsync(
        string name,
        CancellationToken cancellationToken = default);

    Task<ListPage<PromptProtectionRuleRecord>> ListAsync(
        PromptProtectionRuleListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ent <c>UpdateOneID(id).SetInput(input).Save</c>: aggregate updates have
    /// no soft-delete predicate, always set updated_at and return the updated
    /// row (null when no row matched, which Ent reports as NotFoundError).
    /// </summary>
    Task<PromptProtectionRuleRecord?> UpdateAsync(
        PromptProtectionRuleUpdate update,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// ent <c>UpdateOneID(id).SetStatus(status).Save</c>: no soft-delete
    /// predicate (a soft-deleted row is still updated) and updated_at is set.
    /// </summary>
    Task<PromptProtectionRuleRecord?> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-delete update (ent DeleteOneID through the SoftDeleteMixin hook):
    /// <c>UPDATE prompt_protection_rules SET updated_at = now,
    /// deleted_at = unix WHERE id = @id AND deleted_at = 0</c>. Returns the
    /// affected row count; zero means the DeleteOne reports NotFoundError.
    /// </summary>
    Task<int> DeleteAsync(
        long id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-delete update for many ids (ent <c>Delete().Where(IDIn(ids))</c>):
    /// no-op for an empty id list.
    /// </summary>
    Task<int> BulkDeleteAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Status update for many ids (ent <c>Update().Where(IDIn(ids))</c>):
    /// no soft-delete predicate; no-op for an empty id list.
    /// </summary>
    Task<int> BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        string status,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>prompt_protection_rules</c> table. Writes go
/// through EF Core and Dapper; reads use Dapper and honour the SoftDeleteMixin
/// predicate (<c>deleted_at = 0</c>) exactly where Ent's interceptors do.
/// </summary>
public sealed class PromptProtectionRuleStore(AxonHubDbContext db)
    : IPromptProtectionRuleStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "name", "description",
        "pattern", "status", "settings"
        """;

    public async Task<long> CreateAsync(
        PromptProtectionRuleRecord rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rule);

        ApplyCreateDefaults(rule);

        db.PromptProtectionRules.Add(rule);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return rule.Id;
        }
        catch (DbUpdateException)
        {
            // Ent's client keeps no failed-entity state; detaching keeps a
            // later write on the same scoped context from retrying this row.
            db.Entry(rule).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<IReadOnlyList<PromptProtectionRuleRecord>> ListEnabledAsync(
        CancellationToken cancellationToken = default)
    {
        // The reference query is
        //   client.PromptProtectionRule.Query().
        //     Where(promptprotectionrule.StatusEQ(objects.PromptProtectionRuleStatusEnabled)).
        //     Order(ent.Asc(promptprotectionrule.FieldID)).
        //     All(ctx)
        // and Ent's SoftDeleteMixin adds the deleted_at = 0 predicate.
        var sql = "SELECT " + Columns
            + " FROM \"prompt_protection_rules\""
            + " WHERE \"status\" = 'enabled' AND \"deleted_at\" = 0 ORDER BY \"id\";";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<PromptProtectionRuleRecord>(
            new CommandDefinition(
                sql,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.AsList();
    }

    public async Task<PromptProtectionRuleRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"prompt_protection_rules\""
            + " WHERE \"id\" = @Id AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<PromptProtectionRuleRecord>(
            new CommandDefinition(
                sql,
                new { Id = id },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> ExistsByNameAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM "prompt_protection_rules"
                WHERE "name" = @Name AND "deleted_at" = 0
            );
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                sql,
                new { Name = name },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<ListPage<PromptProtectionRuleRecord>> ListAsync(
        PromptProtectionRuleListQuery query,
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
            PromptProtectionRuleSortField.UpdatedAt => "updated_at",
            PromptProtectionRuleSortField.Name => "name",
            _ => null,
        };
        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;
        const string from = " FROM \"prompt_protection_rules\" m";
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
        var nodes = (await connection.QueryAsync<PromptProtectionRuleRecord>(
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

        return new ListPage<PromptProtectionRuleRecord>(
            nodes,
            hasNextPage,
            hasPreviousPage,
            count);
    }

    public async Task<PromptProtectionRuleRecord?> UpdateAsync(
        PromptProtectionRuleUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var args = new SqlArguments();

        // Ent's generated sqlSave applies the fields in schema order:
        // updated_at, deleted_at, name, description, pattern, status, settings.
        var sets = new List<string>(6)
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

        if (update.Pattern is not null)
        {
            sets.Add("\"pattern\" = " + args.Add(update.Pattern));
        }

        if (update.Status is not null)
        {
            sets.Add("\"status\" = " + args.Add(update.Status));
        }

        if (update.Settings is not null)
        {
            sets.Add("\"settings\" = " + JsonCast(args.Add(update.Settings)));
        }

        var sql = new StringBuilder("UPDATE \"prompt_protection_rules\" SET ")
            .Append(string.Join(", ", sets))
            .Append(" WHERE \"id\" = ").Append(args.Add(update.Id))
            .Append(" RETURNING ").Append(Columns).Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<PromptProtectionRuleRecord>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<PromptProtectionRuleRecord?> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();

        // Aggregate updates have no soft-delete predicate (the interceptor only
        // touches queries) and default updated_at before status.
        var sql = new StringBuilder("UPDATE \"prompt_protection_rules\" SET \"updated_at\" = ")
            .Append(args.Add(DateTime.UtcNow))
            .Append(", \"status\" = ").Append(args.Add(status))
            .Append(" WHERE \"id\" = ").Append(args.Add(id))
            .Append(" RETURNING ").Append(Columns).Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<PromptProtectionRuleRecord>(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<int> DeleteAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var args = new SqlArguments();
        var sql = new StringBuilder("UPDATE \"prompt_protection_rules\" SET \"updated_at\" = ")
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
            // The reference returns before issuing any statement for an empty
            // id list (prompt_protection_rule.go, BulkDeleteRules).
            return 0;
        }

        var args = new SqlArguments();
        var parameters = new string[ids.Count];
        for (var i = 0; i < ids.Count; i++)
        {
            parameters[i] = args.Add(ids[i]);
        }

        var sql = new StringBuilder("UPDATE \"prompt_protection_rules\" SET \"updated_at\" = ")
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

    public async Task<int> BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
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

        var sql = new StringBuilder("UPDATE \"prompt_protection_rules\" SET \"updated_at\" = ")
            .Append(args.Add(DateTime.UtcNow))
            .Append(", \"status\" = ").Append(args.Add(status))
            .Append(" WHERE \"id\" IN (").Append(string.Join(", ", parameters))
            .Append(");");

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteAsync(
            new CommandDefinition(
                sql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static void ApplyCreateDefaults(PromptProtectionRuleRecord rule)
    {
        if (rule.CreatedAt == default)
        {
            rule.CreatedAt = DateTime.UtcNow;
        }

        if (rule.UpdatedAt == default)
        {
            rule.UpdatedAt = rule.CreatedAt;
        }

        // Ent schema defaults: status=disabled. The management path supplies
        // every other required field.
        rule.Status = string.IsNullOrEmpty(rule.Status) ? "disabled" : rule.Status;
    }

    private static string JsonCast(string parameter) => "CAST(" + parameter + " AS jsonb)";

    private static string? BuildFilter(
        PromptProtectionRuleListFilter? filter,
        string alias,
        SqlArguments args)
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
            "pattern",
            filter.Pattern,
            filter.PatternNEQ,
            filter.PatternIn,
            filter.PatternNotIn,
            filter.PatternGT,
            filter.PatternGTE,
            filter.PatternLT,
            filter.PatternLTE,
            filter.PatternContains,
            filter.PatternHasPrefix,
            filter.PatternHasSuffix,
            filter.PatternEqualFold,
            filter.PatternContainsFold,
            args);

        ConnectionSql.AppendComparison(predicates, alias, "status", filter.Status, "=", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "status", filter.StatusNEQ, "<>", args);
        ConnectionSql.AppendStringIn(predicates, alias, "status", filter.StatusIn, args);
        ConnectionSql.AppendStringIn(
            predicates, alias, "status", filter.StatusNotIn, args, negate: true);

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
