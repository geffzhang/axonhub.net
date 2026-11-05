using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence;

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
}

/// <summary>
/// Store for the reference <c>prompts</c> table. Writes go through EF Core;
/// reads use Dapper and honour the SoftDeleteMixin predicate
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
}
