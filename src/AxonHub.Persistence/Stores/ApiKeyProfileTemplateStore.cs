using System.Text;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static AxonHub.Persistence.ConnectionSql;

namespace AxonHub.Persistence;

/// <summary>Ordering fields exposed by the reference APIKeyProfileTemplateOrder type.</summary>
public enum ApiKeyProfileTemplateSortField
{
    Id,
    CreatedAt,
    UpdatedAt,
}

/// <summary>
/// Projection of the template query/mutation privacy rules
/// (internal/scopes/rule_user_project_scope.go, rule_apikey_scope.go). A null
/// access filter is the owner/system-bypass path: no predicate is added.
/// </summary>
public sealed record ApiKeyProfileTemplateAccessFilter
{
    /// <summary>Renders <c>project_id = @p</c>.</summary>
    public long? ProjectId { get; init; }
}

/// <summary>
/// Subset of ProjectWhereInput applied inside the hasProjectWith EXISTS
/// subquery. Covers every predicate the port's shared ProjectWhereInput type
/// exposes.
/// </summary>
public sealed record ApiKeyProfileTemplateProjectFilter
{
    public long? Id { get; init; }

    public long? IdNEQ { get; init; }

    public IReadOnlyList<long>? IdIn { get; init; }

    public IReadOnlyList<long>? IdNotIn { get; init; }

    public string? Name { get; init; }

    public string? NameContainsFold { get; init; }

    public string? Status { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    public ApiKeyProfileTemplateProjectFilter? Not { get; init; }

    public IReadOnlyList<ApiKeyProfileTemplateProjectFilter>? And { get; init; }

    public IReadOnlyList<ApiKeyProfileTemplateProjectFilter>? Or { get; init; }
}

/// <summary>
/// Port of ent.APIKeyProfileTemplateWhereInput (ent.graphql:189-274). Every
/// generated scalar predicate is represented; the project edge supports
/// hasProject/hasProjectWith.
/// </summary>
public sealed record ApiKeyProfileTemplateListFilter
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

    public long? ProjectId { get; init; }

    public long? ProjectIdNEQ { get; init; }

    public IReadOnlyList<long>? ProjectIdIn { get; init; }

    public IReadOnlyList<long>? ProjectIdNotIn { get; init; }

    public bool? HasProject { get; init; }

    public IReadOnlyList<ApiKeyProfileTemplateProjectFilter>? HasProjectWith { get; init; }

    public ApiKeyProfileTemplateListFilter? Not { get; init; }

    public IReadOnlyList<ApiKeyProfileTemplateListFilter>? And { get; init; }

    public IReadOnlyList<ApiKeyProfileTemplateListFilter>? Or { get; init; }
}

/// <summary>Entgql pagination request for API key profile templates.</summary>
public sealed record ApiKeyProfileTemplateListQuery
{
    public ApiKeyProfileTemplateListFilter? Where { get; init; }

    public ApiKeyProfileTemplateSortField SortField { get; init; } =
        ApiKeyProfileTemplateSortField.Id;

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }

    /// <summary>Query privacy filter; null for an owner or system caller.</summary>
    public ApiKeyProfileTemplateAccessFilter? Access { get; init; }
}

/// <summary>
/// Field set of an ent <c>APIKeyProfileTemplate.UpdateOneID(...)</c> call. A
/// null value means the field is absent from the input and is not written.
/// </summary>
public sealed record ApiKeyProfileTemplateUpdate
{
    public required long Id { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Serialized objects.APIKeyProfile; null leaves the column alone.</summary>
    public string? Profile { get; init; }

    /// <summary>Mutation policy predicate (ent <c>WhereP</c>); null allows unconditionally.</summary>
    public long? ProjectWritePredicate { get; init; }
}

/// <summary>Persistence seam for the reference <c>api_key_profile_templates</c> table.</summary>
public interface IApiKeyProfileTemplateStore
{
    /// <summary>Joins the ambient transaction or opens one, like ent's RunInTransaction.</summary>
    Task<T> RunInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default);

    /// <summary>Same, for an action with no result.</summary>
    Task RunInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default);

    /// <summary>Port of <c>APIKeyProfileTemplate.Get</c> under the query privacy rule.</summary>
    Task<ApiKeyProfileTemplateRecord?> GetAsync(
        long id,
        ApiKeyProfileTemplateAccessFilter? access,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>Query().Where(NameEQ(name)).Only()</c>: null when no row
    /// matches, "not singular" when the privacy filter admits more than one.
    /// </summary>
    Task<ApiKeyProfileTemplateRecord?> GetByNameAsync(
        string name,
        ApiKeyProfileTemplateAccessFilter? access,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of <c>Query().Where(ProjectIDEQ(projectID)).All()</c>. Ordered by id
    /// because the reference has no ORDER BY (C163).
    /// </summary>
    Task<IReadOnlyList<ApiKeyProfileTemplateRecord>> ListForProjectAsync(
        long projectId,
        ApiKeyProfileTemplateAccessFilter? access,
        CancellationToken cancellationToken = default);

    /// <summary>Port of entgql's APIKeyProfileTemplateQuery.Paginate.</summary>
    Task<ListPage<ApiKeyProfileTemplateRecord>> ListAsync(
        ApiKeyProfileTemplateListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of the ent create builder: profile null writes SQL NULL, which
    /// suppresses the schema default.
    /// </summary>
    /// <exception cref="ApiKeyProfileTemplateNameConflictException">
    /// The (project_id, name, deleted_at) unique index rejected the insert.
    /// </exception>
    Task<ApiKeyProfileTemplateRecord> CreateAsync(
        ApiKeyProfileTemplateRecord template,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of UpdateOneID(...).Save: absent fields keep their value and the
    /// saved row is returned.
    /// </summary>
    /// <exception cref="ApiKeyProfileTemplateNameConflictException">
    /// A rename collided with the (project_id, name, deleted_at) unique index.
    /// </exception>
    Task<ApiKeyProfileTemplateRecord?> UpdateAsync(
        ApiKeyProfileTemplateUpdate update,
        CancellationToken cancellationToken = default);

    /// <summary>Soft delete: sets deleted_at (Unix seconds) and bumps updated_at.</summary>
    Task<bool> DeleteAsync(
        long id,
        ApiKeyProfileTemplateAccessFilter? access,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The reference maps every PostgreSQL SQLSTATE class 23 failure on template
/// insert/rename to <c>xerrors.DuplicateNameError("Template", name)</c>.
/// </summary>
public sealed class ApiKeyProfileTemplateNameConflictException : Exception
{
    public ApiKeyProfileTemplateNameConflictException(Exception innerException)
        : base("API key profile template name already exists", innerException)
    {
    }
}

/// <summary>
/// Store for the reference <c>api_key_profile_templates</c> table. Writes go
/// through EF Core (Dapper for the conditional ent-style update); reads use
/// Dapper and honour the SoftDeleteMixin predicate (<c>deleted_at = 0</c>).
/// </summary>
public sealed class ApiKeyProfileTemplateStore(AxonHubDbContext db) : IApiKeyProfileTemplateStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "name", "description",
        "profile", "project_id"
        """;

    public async Task<T> RunInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);

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

    public async Task<ApiKeyProfileTemplateRecord?> GetAsync(
        long id,
        ApiKeyProfileTemplateAccessFilter? access,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sql = new StringBuilder("SELECT ").Append(Columns)
                .Append(" FROM \"api_key_profile_templates\"")
                .Append(" WHERE \"id\" = @Id AND \"deleted_at\" = 0");
            if (access?.ProjectId is not null)
            {
                sql.Append(" AND \"project_id\" = @AccessProjectId");
            }

            sql.Append(';');

            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            return await connection.QuerySingleOrDefaultAsync<ApiKeyProfileTemplateRecord>(
                new CommandDefinition(
                    sql.ToString(),
                    new { Id = id, AccessProjectId = access?.ProjectId },
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.Message, ex);
        }
    }

    public async Task<ApiKeyProfileTemplateRecord?> GetByNameAsync(
        string name,
        ApiKeyProfileTemplateAccessFilter? access,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sql = new StringBuilder("SELECT ").Append(Columns)
                .Append(" FROM \"api_key_profile_templates\"")
                .Append(" WHERE \"name\" = @Name AND \"deleted_at\" = 0");
            if (access?.ProjectId is not null)
            {
                sql.Append(" AND \"project_id\" = @AccessProjectId");
            }

            sql.Append(" LIMIT 2;");

            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            var rows = (await connection.QueryAsync<ApiKeyProfileTemplateRecord>(
                new CommandDefinition(
                    sql.ToString(),
                    new { Name = name, AccessProjectId = access?.ProjectId },
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

            if (rows.Count > 1)
            {
                // Port of ent's NotSingularError.
                throw new StoreFailureException(
                    "ent: api_key_profile_template not singular",
                    new InvalidOperationException("ent: api_key_profile_template not singular"));
            }

            return rows.Count == 0 ? null : rows[0];
        }
        catch (StoreFailureException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.Message, ex);
        }
    }

    public async Task<IReadOnlyList<ApiKeyProfileTemplateRecord>> ListForProjectAsync(
        long projectId,
        ApiKeyProfileTemplateAccessFilter? access,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sql = new StringBuilder("SELECT ").Append(Columns)
                .Append(" FROM \"api_key_profile_templates\"")
                .Append(" WHERE \"deleted_at\" = 0 AND \"project_id\" = @ProjectId");
            if (access?.ProjectId is not null)
            {
                sql.Append(" AND \"project_id\" = @AccessProjectId");
            }

            sql.Append(" ORDER BY \"id\" ASC;");

            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            var rows = await connection.QueryAsync<ApiKeyProfileTemplateRecord>(
                new CommandDefinition(
                    sql.ToString(),
                    new { ProjectId = projectId, AccessProjectId = access?.ProjectId },
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            return rows.AsList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.Message, ex);
        }
    }

    public async Task<ListPage<ApiKeyProfileTemplateRecord>> ListAsync(
        ApiKeyProfileTemplateListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var args = new SqlArguments();
        var conditions = BuildConditions(query, args);

        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;

        var countSql = "SELECT COUNT(*) FROM \"api_key_profile_templates\" t"
            + conditions + ";";

        var nodesSql = new StringBuilder("SELECT ");
        nodesSql.Append(string.Join(", ", Columns
            .Split(',')
            .Select(static column => "t." + column.Trim())));
        nodesSql.Append(" FROM \"api_key_profile_templates\" t").Append(conditions);

        var valueColumn = SortValueColumn(query.SortField);

        ConnectionSql.AppendCursor(
            nodesSql,
            "t",
            "id",
            valueColumn,
            query.After,
            query.Before,
            direction,
            args);

        nodesSql.Append(" ORDER BY ")
            .Append(ConnectionSql.OrderBy("t", "id", valueColumn, direction));

        var limit = query.First is not null
            ? query.First + 1
            : query.Last is not null ? query.Last + 1 : (int?)null;
        if (limit is not null)
        {
            nodesSql.Append(" LIMIT ").Append(limit.Value);
        }

        nodesSql.Append(';');

        var connection = await db.OpenDapperConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

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
            return new ListPage<ApiKeyProfileTemplateRecord>(
                [],
                query.First is not null && totalCount > 0,
                query.Last is not null && totalCount > 0,
                totalCount);
        }

        var nodes = (await connection.QueryAsync<ApiKeyProfileTemplateRecord>(
            new CommandDefinition(
                nodesSql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

        return BuildPage(nodes, query, totalCount);
    }

    public async Task<ApiKeyProfileTemplateRecord> CreateAsync(
        ApiKeyProfileTemplateRecord template,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (template.CreatedAt == default)
        {
            template.CreatedAt = DateTime.UtcNow;
        }

        if (template.UpdatedAt == default)
        {
            template.UpdatedAt = template.CreatedAt;
        }

        db.ApiKeyProfileTemplates.Add(template);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return template;
        }
        catch (DbUpdateException ex)
        {
            db.Entry(template).State = EntityState.Detached;

            if (IsConstraintViolation(ex))
            {
                throw new ApiKeyProfileTemplateNameConflictException(ex);
            }

            throw new StoreFailureException(ex.GetBaseException().Message, ex);
        }
    }

    public async Task<ApiKeyProfileTemplateRecord?> UpdateAsync(
        ApiKeyProfileTemplateUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var args = new SqlArguments();
        var assignments = new List<string>
        {
            "\"updated_at\" = " + args.Add(DateTime.UtcNow),
        };

        if (update.Name is not null)
        {
            assignments.Add("\"name\" = " + args.Add(update.Name));
        }

        if (update.Description is not null)
        {
            assignments.Add("\"description\" = " + args.Add(update.Description));
        }

        if (update.Profile is not null)
        {
            assignments.Add("\"profile\" = CAST(" + args.Add(update.Profile) + " AS jsonb)");
        }

        var sql = "UPDATE \"api_key_profile_templates\" SET "
            + string.Join(", ", assignments)
            + " WHERE \"id\" = " + args.Add(update.Id)
            + " AND \"deleted_at\" = 0";
        if (update.ProjectWritePredicate is not null)
        {
            sql += " AND \"project_id\" = " + args.Add(update.ProjectWritePredicate.Value);
        }

        sql += " RETURNING " + Columns + ";";

        try
        {
            var connection = await db.OpenDapperConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            return await connection.QuerySingleOrDefaultAsync<ApiKeyProfileTemplateRecord>(
                new CommandDefinition(
                    sql,
                    args.Parameters,
                    transaction: db.CurrentDbTransaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (IsConstraintSqlState(ex.SqlState))
        {
            throw new ApiKeyProfileTemplateNameConflictException(ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.Message, ex);
        }
    }

    public async Task<bool> DeleteAsync(
        long id,
        ApiKeyProfileTemplateAccessFilter? access,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var deletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        try
        {
            // The soft-delete hook rewrites DELETE into an update: the time
            // mixin's UpdateDefault bumps updated_at as well.
            var query = db.ApiKeyProfileTemplates
                .Where(row => row.Id == id && row.DeletedAt == 0);
            if (access?.ProjectId is not null)
            {
                var scope = access.ProjectId.Value;
                query = query.Where(row => row.ProjectId == scope);
            }

            var affected = await query
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.DeletedAt, deletedAt)
                        .SetProperty(row => row.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);

            return affected > 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException(ex.Message, ex);
        }
    }

    private static string BuildConditions(
        ApiKeyProfileTemplateListQuery query,
        SqlArguments args)
    {
        var conditions = new StringBuilder(" WHERE t.\"deleted_at\" = 0");

        if (query.Access?.ProjectId is { } scope)
        {
            conditions.Append(" AND t.\"project_id\" = ").Append(args.Add(scope));
        }

        var where = BuildFilter(query.Where, "t", args);
        if (where is not null)
        {
            conditions.Append(" AND ").Append(where);
        }

        return conditions.ToString();
    }

    private static string? SortValueColumn(ApiKeyProfileTemplateSortField field) => field switch
    {
        ApiKeyProfileTemplateSortField.CreatedAt => "created_at",
        ApiKeyProfileTemplateSortField.UpdatedAt => "updated_at",
        _ => null,
    };

    private static ListPage<ApiKeyProfileTemplateRecord> BuildPage(
        List<ApiKeyProfileTemplateRecord> nodes,
        ApiKeyProfileTemplateListQuery query,
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

        return new ListPage<ApiKeyProfileTemplateRecord>(
            nodes,
            hasNextPage,
            hasPreviousPage,
            totalCount);
    }

    internal static string? BuildFilter(
        ApiKeyProfileTemplateListFilter? filter,
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
        AppendIdIn(predicates, alias, "id", filter.IdIn, args);
        AppendIdIn(predicates, alias, "id", filter.IdNotIn, args, negate: true);
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

        AppendStringPredicates(
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

        AppendComparison(predicates, alias, "project_id", filter.ProjectId, "=", args);
        AppendComparison(predicates, alias, "project_id", filter.ProjectIdNEQ, "<>", args);
        AppendIdIn(predicates, alias, "project_id", filter.ProjectIdIn, args);
        AppendIdIn(predicates, alias, "project_id", filter.ProjectIdNotIn, args, negate: true);

        if (filter.HasProject is not null)
        {
            var exists = "EXISTS (SELECT 1 FROM \"projects\" p"
                + " WHERE p.\"id\" = " + alias + ".\"project_id\")";
            predicates.Add(filter.HasProject.Value ? exists : "NOT (" + exists + ")");
        }

        if (filter.HasProjectWith is { Count: > 0 } projectFilters)
        {
            // Generated sqlgraph.HasNeighborsWith ANDs every predicate inside a
            // single EXISTS and does not re-apply the Project soft-delete
            // predicate.
            var projectPredicates = projectFilters
                .Select(project => BuildProjectFilter(project, args))
                .ToList();

            var projectCondition = ConnectionSql.Combine(projectPredicates);
            predicates.Add(
                "EXISTS (SELECT 1 FROM \"projects\" p WHERE p.\"id\" = "
                + alias + ".\"project_id\""
                + (projectCondition is null ? string.Empty : " AND " + projectCondition)
                + ")");
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

    /// <summary>
    /// Builds one ProjectWhereInput predicate against the <c>p</c> alias used by
    /// the hasProjectWith EXISTS subquery. Supports the subset of project
    /// predicates the shared GraphQL input exposes; nested Not/And/Or recurse.
    /// </summary>
    private static string? BuildProjectFilter(
        ApiKeyProfileTemplateProjectFilter? filter,
        SqlArguments args)
    {
        if (filter is null)
        {
            return null;
        }

        const string alias = "p";

        var predicates = new List<string?>();

        AppendComparison(predicates, alias, "id", filter.Id, "=", args);
        AppendComparison(predicates, alias, "id", filter.IdNEQ, "<>", args);
        AppendIdIn(predicates, alias, "id", filter.IdIn, args);
        AppendIdIn(predicates, alias, "id", filter.IdNotIn, args, negate: true);

        AppendComparison(predicates, alias, "name", filter.Name, "=", args);
        if (filter.NameContainsFold is not null)
        {
            // ent.ContainsFold: LOWER(col) LIKE LOWER('%value%').
            ConnectionSql.AppendStringFilter(
                predicates,
                alias,
                "name",
                "LIKE",
                filter.NameContainsFold,
                args);
        }

        AppendComparison(predicates, alias, "status", filter.Status, "=", args);
        AppendStringIn(predicates, alias, "status", filter.StatusIn, args);

        if (filter.Not is not null)
        {
            var not = BuildProjectFilter(filter.Not, args);
            if (not is not null)
            {
                predicates.Add("NOT (" + not + ")");
            }
        }

        if (filter.And is not null)
        {
            var and = filter.And
                .Select(nested => BuildProjectFilter(nested, args))
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
                .Select(nested => BuildProjectFilter(nested, args))
                .Where(static nested => nested is not null)
                .ToArray();
            predicates.Add(or.Length == 0 ? "FALSE" : "(" + string.Join(" OR ", or) + ")");
        }

        return ConnectionSql.Combine(predicates);
    }

    private static bool IsConstraintViolation(DbUpdateException exception)
        => exception.GetBaseException() is PostgresException { SqlState: { } sqlState }
            && IsConstraintSqlState(sqlState);

    private static bool IsConstraintSqlState(string? sqlState)
        => sqlState?.StartsWith("23", StringComparison.Ordinal) == true;
}
