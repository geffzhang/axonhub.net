using System.Text;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

/// <summary>Ordering fields exposed by the reference ProjectOrder type.</summary>
public enum ProjectSortField
{
    Id,
    UpdatedAt,
}

/// <summary>Subset of ent.ProjectWhereInput used by the management frontend.</summary>
/// <summary>
/// Entgql <c>ProjectWhereInput</c> scalar predicates. The ten edge predicate
/// groups remain tracked by C165.
/// </summary>
public sealed record ProjectListFilter
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

    public string? Status { get; init; }

    public string? StatusNEQ { get; init; }

    public IReadOnlyList<string>? StatusIn { get; init; }

    public IReadOnlyList<string>? StatusNotIn { get; init; }

    public ProjectListFilter? Not { get; init; }

    public IReadOnlyList<ProjectListFilter>? And { get; init; }

    public IReadOnlyList<ProjectListFilter>? Or { get; init; }
}

/// <summary>Entgql pagination request for projects.</summary>
public sealed record ProjectListQuery
{
    public ProjectListFilter? Where { get; init; }

    public SortDirection Direction { get; init; } = SortDirection.Asc;

    public ProjectSortField SortField { get; init; } = ProjectSortField.Id;

    public ListCursor? After { get; init; }

    public ListCursor? Before { get; init; }

    public int? First { get; init; }

    public int? Last { get; init; }
}

/// <summary>
/// Port of the fields ProjectService.UpdateProject applies from
/// ent.UpdateProjectInput. <see cref="Status"/> and the join-table edge fields
/// the reference ignores are not part of this command.
/// </summary>
public sealed record ProjectUpdate
{
    public required long Id { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public bool ClearUsers { get; init; }

    public IReadOnlyList<long>? RemovedUserIds { get; init; }

    public IReadOnlyList<long>? AddedUserIds { get; init; }
}

/// <summary>Persistence seam for the reference <c>projects</c> table.</summary>
public interface IProjectStore
{
    Task<long> CreateAsync(ProjectRecord project, CancellationToken cancellationToken = default);

    Task<ProjectRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Port of Project.Query().Order(Asc(id)).First: the default project.</summary>
    Task<ProjectRecord?> GetFirstAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of ProjectService.CreateProject's duplicate check (live rows only).</summary>
    /// <param name="idScope">
    /// The <c>WhereID(ctx)</c> predicate the Project query policy installs; a
    /// project outside the scope cannot match.
    /// </param>
    Task<bool> NameExistsAsync(
        string name,
        CancellationToken cancellationToken = default,
        long? idScope = null);

    Task<ListPage<ProjectRecord>> ListAsync(
        ProjectListQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of Project.UpdateOneID(id).Save: node update, user_projects edge
    /// clears and adds run in one transaction. Returns null when the project is
    /// missing (ent: project not found).
    /// </summary>
    Task<ProjectRecord?> UpdateAsync(
        ProjectUpdate update,
        CancellationToken cancellationToken = default);

    Task<ProjectRecord?> UpdateProfilesAsync(
        long id,
        string profilesJson,
        CancellationToken cancellationToken = default);

    Task<ProjectRecord?> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of UserProject.Create().SetIsOwner(true).SetScopes([]) used by
    /// CreateProject; the reference assigns the creator with default
    /// created_at/updated_at.
    /// </summary>
    Task AssignOwnerAsync(
        long userId,
        long projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Port of ProjectService.DeleteProject's transaction: hard-deletes
    /// user_projects, soft-deletes project roles and API keys, then the project.
    /// user_roles rows of the project roles are left untouched, like the
    /// reference. Returns false when the project is missing.
    /// </summary>
    /// <param name="idScope">
    /// The <c>WhereID(ctx)</c> predicate the Project query policy installs on
    /// the initial <c>Project.Get</c>; a project outside the scope reads as
    /// missing.
    /// </param>
    Task<bool> DeleteAsync(
        long id,
        CancellationToken cancellationToken = default,
        long? idScope = null);
}

/// <summary>
/// Store for the reference <c>projects</c> table. Writes go through EF Core;
/// reads use Dapper and honour the SoftDeleteMixin predicate (<c>deleted_at = 0</c>).
/// </summary>
public sealed class ProjectStore(AxonHubDbContext db) : IProjectStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "deleted_at", "name", "description",
        "status", "profiles"
        """;

    public async Task<long> CreateAsync(ProjectRecord project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        db.Projects.Add(project);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return project.Id;
    }

    public async Task<ProjectRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var sql = "SELECT " + Columns
            + " FROM \"projects\" WHERE \"id\" = @Id AND \"deleted_at\" = 0;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ProjectRecord>(
            new CommandDefinition(
                sql,
                new { Id = id },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<ProjectRecord?> GetFirstAsync(CancellationToken cancellationToken = default)
    {
        // Ent's Order(Asc(id)).First applies the soft-delete predicate; the port
        // keeps the same id ordering so "default project" is deterministic.
        var sql = "SELECT " + Columns
            + " FROM \"projects\" WHERE \"deleted_at\" = 0 ORDER BY \"id\" LIMIT 1;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ProjectRecord>(
            new CommandDefinition(
                sql,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> NameExistsAsync(
        string name,
        CancellationToken cancellationToken = default,
        long? idScope = null)
    {
        const string sql = """
            SELECT EXISTS (
                SELECT 1 FROM "projects"
                WHERE "deleted_at" = 0 AND "name" = @Name
                  AND (@IdScope IS NULL OR "id" = @IdScope)
            );
            """;

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(
                sql,
                new { Name = name, IdScope = idScope },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<ListPage<ProjectRecord>> ListAsync(
        ProjectListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var args = new SqlArguments();
        var where = BuildFilter(query.Where, "p", args);

        const string from = " FROM \"projects\" p";

        var conditions = new StringBuilder(" WHERE p.\"deleted_at\" = 0");
        if (where is not null)
        {
            conditions.Append(" AND ").Append(where);
        }

        var direction = query.Last is null
            ? query.Direction
            : query.Direction == SortDirection.Asc ? SortDirection.Desc : SortDirection.Asc;

        var countSql = "SELECT COUNT(*) " + from + conditions + ";";

        var nodesSql = new StringBuilder("SELECT ");
        nodesSql.Append(string.Join(", ", Columns
            .Split(',')
            .Select(static column => "p." + column.Trim())));
        nodesSql.Append(from).Append(conditions);

        ConnectionSql.AppendCursor(
            nodesSql,
            "p",
            "id",
            query.SortField == ProjectSortField.UpdatedAt ? "updated_at" : null,
            query.After,
            query.Before,
            direction,
            args);

        nodesSql.Append(" ORDER BY ")
            .Append(ConnectionSql.OrderBy(
                "p",
                "id",
                query.SortField == ProjectSortField.UpdatedAt ? "updated_at" : null,
                direction));

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

        var nodes = (await connection.QueryAsync<ProjectRecord>(
            new CommandDefinition(
                nodesSql.ToString(),
                args.Parameters,
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

        return BuildPage(nodes, query, totalCount);
    }

    public async Task<ProjectRecord?> UpdateAsync(
        ProjectUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Ent's UpdateOne load: the row must be live, or Save returns
        // NotFoundError ("ent: project not found").
        var project = await db.Projects.SingleOrDefaultAsync(
            row => row.Id == update.Id && row.DeletedAt == 0,
            cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            return null;
        }

        if (update.Name is not null)
        {
            project.Name = update.Name;
        }

        if (update.Description is not null)
        {
            project.Description = update.Description;
        }

        // ent's UpdateDefault bumps updated_at on every update, even when only
        // edges change.
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // sqlgraph order: update node, clear edges, then add edges.
        if (update.ClearUsers)
        {
            await db.UserProjects
                .Where(row => row.ProjectId == update.Id)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        else if (update.RemovedUserIds is { Count: > 0 })
        {
            await db.UserProjects
                .Where(row => row.ProjectId == update.Id
                    && update.RemovedUserIds.Contains(row.UserId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        if (update.AddedUserIds is { Count: > 0 })
        {
            var now = DateTime.UtcNow;
            foreach (var userId in update.AddedUserIds)
            {
                // Join-table edge creates use the UserProject defaults:
                // is_owner false, scopes [] (userproject.go:41,47).
                db.UserProjects.Add(new UserProjectRecord
                {
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsOwner = false,
                    Scopes = "[]",
                    UserId = userId,
                    ProjectId = update.Id,
                });
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex)
            {
                // The reference has no ON CONFLICT for this join table (it has
                // extra fields), so adding an existing member is a unique
                // violation that rolls back the whole update.
                throw new StoreFailureException(
                    "add m2m edge for table user_projects: " + ex.GetBaseException().Message,
                    ex);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return await GetByIdAsync(update.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectRecord?> UpdateProfilesAsync(
        long id,
        string profilesJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profilesJson);

        var project = await db.Projects.SingleOrDefaultAsync(
            row => row.Id == id && row.DeletedAt == 0,
            cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            return null;
        }

        project.Profiles = profilesJson;
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectRecord?> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);

        var project = await db.Projects.SingleOrDefaultAsync(
            row => row.Id == id && row.DeletedAt == 0,
            cancellationToken).ConfigureAwait(false);
        if (project is null)
        {
            return null;
        }

        project.Status = status;
        project.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return await GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task AssignOwnerAsync(
        long userId,
        long projectId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        db.UserProjects.Add(new UserProjectRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            IsOwner = true,
            Scopes = "[]",
            UserId = userId,
            ProjectId = projectId,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex)
        {
            throw new StoreFailureException(
                "failed to assign user to project: " + ex.GetBaseException().Message,
                ex);
        }
    }

    public async Task<bool> DeleteAsync(
        long id,
        CancellationToken cancellationToken = default,
        long? idScope = null)
    {
        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        ProjectRecord? project;
        try
        {
            var lookup = db.Projects.Where(row => row.Id == id && row.DeletedAt == 0);
            if (idScope is { } scope)
            {
                // Project query policy: WhereID(ctx).
                lookup = lookup.Where(row => row.Id == scope);
            }

            project = await lookup
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException("failed to get project: " + ex.Message, ex);
        }

        if (project is null)
        {
            // The caller renders "failed to get project: ent: project not found".
            return false;
        }

        try
        {
            await db.UserProjects
                .Where(row => row.ProjectId == id)
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException("failed to delete project users: " + ex.Message, ex);
        }

        var now = DateTime.UtcNow;
        var deletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Role.Delete() / APIKey.Delete() only soft-delete; their user_roles
        // links are intentionally not cleaned up here (unlike RoleService).
        try
        {
            await db.Roles
                .Where(row => row.ProjectId == project.Id && row.DeletedAt == 0)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.DeletedAt, deletedAt)
                        .SetProperty(row => row.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException("failed to delete project roles: " + ex.Message, ex);
        }

        try
        {
            await db.ApiKeys
                .Where(row => row.ProjectId == project.Id && row.DeletedAt == 0)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(row => row.DeletedAt, deletedAt)
                        .SetProperty(row => row.UpdatedAt, now),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException("failed to delete project API keys: " + ex.Message, ex);
        }

        try
        {
            project.DeletedAt = deletedAt;
            project.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StoreFailureException("failed to delete project: " + ex.Message, ex);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static ListPage<ProjectRecord> BuildPage(
        List<ProjectRecord> nodes,
        ProjectListQuery query,
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

        return new ListPage<ProjectRecord>(nodes, hasNextPage, hasPreviousPage, totalCount);
    }

    internal static string? BuildFilter(
        ProjectListFilter? filter,
        string alias,
        SqlArguments args)
    {
        if (filter is null)
        {
            return null;
        }

        var predicates = new List<string?>();

        ConnectionSql.AppendComparison(predicates, alias, "id", filter.Id, "=", args);
        ConnectionSql.AppendComparison(predicates, alias, "id", filter.IdNEQ, "<>", args);
        ConnectionSql.AppendIdIn(predicates, alias, "id", filter.IdIn, args);
        ConnectionSql.AppendIdIn(predicates, alias, "id", filter.IdNotIn, args, negate: true);
        ConnectionSql.AppendComparison(predicates, alias, "id", filter.IdGT, ">", args);
        ConnectionSql.AppendComparison(predicates, alias, "id", filter.IdGTE, ">=", args);
        ConnectionSql.AppendComparison(predicates, alias, "id", filter.IdLT, "<", args);
        ConnectionSql.AppendComparison(predicates, alias, "id", filter.IdLTE, "<=", args);

        ConnectionSql.AppendComparison(
            predicates, alias, "created_at", filter.CreatedAt, "=", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "created_at", filter.CreatedAtNEQ, "<>", args);
        ConnectionSql.AppendTimeIn(
            predicates, alias, "created_at", filter.CreatedAtIn, args);
        ConnectionSql.AppendTimeIn(
            predicates, alias, "created_at", filter.CreatedAtNotIn, args, negate: true);
        ConnectionSql.AppendComparison(
            predicates, alias, "created_at", filter.CreatedAtGT, ">", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "created_at", filter.CreatedAtGTE, ">=", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "created_at", filter.CreatedAtLT, "<", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "created_at", filter.CreatedAtLTE, "<=", args);

        ConnectionSql.AppendComparison(
            predicates, alias, "updated_at", filter.UpdatedAt, "=", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "updated_at", filter.UpdatedAtNEQ, "<>", args);
        ConnectionSql.AppendTimeIn(
            predicates, alias, "updated_at", filter.UpdatedAtIn, args);
        ConnectionSql.AppendTimeIn(
            predicates, alias, "updated_at", filter.UpdatedAtNotIn, args, negate: true);
        ConnectionSql.AppendComparison(
            predicates, alias, "updated_at", filter.UpdatedAtGT, ">", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "updated_at", filter.UpdatedAtGTE, ">=", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "updated_at", filter.UpdatedAtLT, "<", args);
        ConnectionSql.AppendComparison(
            predicates, alias, "updated_at", filter.UpdatedAtLTE, "<=", args);

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

        ConnectionSql.AppendComparison(predicates, alias, "status", filter.Status, "=", args);
        ConnectionSql.AppendComparison(predicates, alias, "status", filter.StatusNEQ, "<>", args);
        ConnectionSql.AppendStringIn(predicates, alias, "status", filter.StatusIn, args);
        ConnectionSql.AppendStringIn(
            predicates, alias, "status", filter.StatusNotIn, args, negate: true);

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
}
