using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence;

/// <summary>Persistence seam for the reference <c>threads</c> table.</summary>
public interface IThreadStore
{
    /// <summary>
    /// Port of ThreadService.GetOrCreateThread (internal/server/biz/thread.go:
    /// 32-76): find the thread by (thread_id, project_id), create it when
    /// absent, and re-read after a concurrent create loses the unique-index race.
    /// </summary>
    Task<ThreadRecord> GetOrCreateAsync(
        long projectId,
        string threadId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>threads</c> table. Writes go through EF Core;
/// reads use Dapper.
/// </summary>
/// <remarks>
/// The reference's Ent builder applies the TimeMixin values in the application
/// (created_at = updated_at = now), so the store stamps them here instead of
/// relying on the column defaults. A failed insert is detached from the context
/// so the scoped unit of work stays usable for the rest of the request.
/// </remarks>
public sealed class ThreadStore(AxonHubDbContext db, TimeProvider? timeProvider = null) : IThreadStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "thread_id", "status", "project_id"
        """;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ThreadRecord> GetOrCreateAsync(
        long projectId,
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(threadId);

        // Thread.Query().Where(thread_id, project_id).Only(ctx)
        var existing = await FindAsync(projectId, threadId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var thread = new ThreadRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            ThreadId = threadId,
            Status = "active",
            ProjectId = projectId,
        };

        db.Threads.Add(thread);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return thread;
        }
        catch (DbUpdateException ex)
        {
            db.Entry(thread).State = EntityState.Detached;

            // ent.IsConstraintError: a concurrent request created the same
            // thread between the query and the insert. Re-run the same lookup
            // and return its result (including its not-found error).
            if (!IsConstraintViolation(ex))
            {
                throw new InvalidOperationException(
                    "failed to create thread: " + ex.GetBaseException().Message,
                    ex);
            }
        }

        return await FindAsync(projectId, threadId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("failed to create thread: thread not found");
    }

    private async Task<ThreadRecord?> FindAsync(
        long projectId,
        string threadId,
        CancellationToken cancellationToken)
    {
        var sql = "SELECT " + Columns
            + " FROM \"threads\" WHERE \"thread_id\" = @ThreadId AND \"project_id\" = @ProjectId;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<ThreadRecord>(
            new CommandDefinition(
                sql,
                new { ThreadId = threadId, ProjectId = projectId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Ent classifies every PostgreSQL constraint failure (SQLSTATE class 23)
    /// as a constraint error; the only reachable one here is the unique index
    /// on thread_id.
    /// </summary>
    private static bool IsConstraintViolation(DbUpdateException exception)
        => exception.GetBaseException() is PostgresException { SqlState: { } sqlState }
            && sqlState.StartsWith("23", StringComparison.Ordinal);
}
