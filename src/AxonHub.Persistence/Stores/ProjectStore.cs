using Dapper;

namespace AxonHub.Persistence;

/// <summary>Persistence seam for the reference <c>projects</c> table.</summary>
public interface IProjectStore
{
    Task<long> CreateAsync(ProjectRecord project, CancellationToken cancellationToken = default);

    Task<ProjectRecord?> GetByIdAsync(long id, CancellationToken cancellationToken = default);

    /// <summary>Port of Project.Query().Order(Asc(id)).First: the default project.</summary>
    Task<ProjectRecord?> GetFirstAsync(CancellationToken cancellationToken = default);
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
}
