using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence;

/// <summary>Persistence seam for the reference <c>traces</c> table.</summary>
public interface ITraceStore
{
    /// <summary>
    /// Port of TraceService.GetOrCreateTrace (internal/server/biz/trace.go:
    /// 62-105): find the trace by (trace_id, project_id), create it when absent,
    /// and re-read after a concurrent create loses the unique-index race.
    /// </summary>
    Task<TraceRecord> GetOrCreateAsync(
        long projectId,
        string traceId,
        long? threadId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Store for the reference <c>traces</c> table. Writes go through EF Core;
/// reads use Dapper.
/// </summary>
/// <remarks>
/// The reference's Ent builder applies the TimeMixin values in the application
/// (created_at = updated_at = now), so the store stamps them here instead of
/// relying on the column defaults. A failed insert is detached from the context
/// so the scoped unit of work stays usable for the rest of the request.
/// </remarks>
public sealed class TraceStore(AxonHubDbContext db, TimeProvider? timeProvider = null) : ITraceStore
{
    private const string Columns = """
        "id", "created_at", "updated_at", "trace_id", "status", "project_id",
        "thread_id"
        """;

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<TraceRecord> GetOrCreateAsync(
        long projectId,
        string traceId,
        long? threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(traceId);

        // Trace.Query().Where(trace_id, project_id).Only(ctx)
        var existing = await FindAsync(projectId, traceId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var trace = new TraceRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            TraceId = traceId,
            Status = "active",
            ProjectId = projectId,
            ThreadId = threadId,
        };

        db.Traces.Add(trace);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return trace;
        }
        catch (DbUpdateException ex)
        {
            db.Entry(trace).State = EntityState.Detached;

            // ent.IsConstraintError: a concurrent request created the same
            // trace between the query and the insert. Re-run the same lookup
            // and return its result (including its not-found error).
            if (!IsConstraintViolation(ex))
            {
                throw new InvalidOperationException(
                    "failed to create trace: " + ex.GetBaseException().Message,
                    ex);
            }
        }

        return await FindAsync(projectId, traceId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("failed to create trace: trace not found");
    }

    private async Task<TraceRecord?> FindAsync(
        long projectId,
        string traceId,
        CancellationToken cancellationToken)
    {
        var sql = "SELECT " + Columns
            + " FROM \"traces\" WHERE \"trace_id\" = @TraceId AND \"project_id\" = @ProjectId;";

        var connection = await db.OpenDapperConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await connection.QuerySingleOrDefaultAsync<TraceRecord>(
            new CommandDefinition(
                sql,
                new { TraceId = traceId, ProjectId = projectId },
                transaction: db.CurrentDbTransaction,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// Ent classifies every PostgreSQL constraint failure (SQLSTATE class 23)
    /// as a constraint error; the only reachable one here is the unique index
    /// on trace_id.
    /// </summary>
    private static bool IsConstraintViolation(DbUpdateException exception)
        => exception.GetBaseException() is PostgresException { SqlState: { } sqlState }
            && sqlState.StartsWith("23", StringComparison.Ordinal);
}
