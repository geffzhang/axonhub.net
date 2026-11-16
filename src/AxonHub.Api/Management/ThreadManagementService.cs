using AxonHub.Api.Traces;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of the management surface of <c>biz.ThreadService</c>
/// (internal/server/biz/thread.go:18-237) plus the entgql Thread connection
/// and Node lookup.
/// </summary>
/// <remarks>
/// The reference wraps each thread transition in <c>RunInTransaction</c>,
/// updates the thread under the mutation privacy predicate, and only when that
/// update matched does it cascade to the thread's traces of the same status.
/// <see cref="IThreadAdminStore.UpdateStatusAndCascadeAsync"/> performs the
/// same two statements in one database transaction; a cascade failure is
/// surfaced as <see cref="TraceCascadeException"/> so the two error prefixes
/// stay distinct.
/// </remarks>
public sealed class ThreadManagementService(
    IThreadAdminStore threads,
    ITraceAdminStore traces,
    IUsageMetadataStore usage,
    TraceSegmentService segments,
    TracePrivacy privacy)
{
    /// <summary>Ent's NotFoundError text for the Thread label.</summary>
    public const string ThreadNotFound = "ent: thread not found";

    /// <summary>The Trace query policy is a deny, so it is wrapped like any other query failure.</summary>
    private const string FirstTracePrefix = "failed to get first trace for thread: ";

    /// <summary>Port of the entgql Thread connection (ent.resolvers.go:541-554).</summary>
    public async Task<ListPage<ThreadRecord>> ListAsync(
        ThreadListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var projectScope = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        return await threads.ListAsync(
                query with { ProjectScope = projectScope },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of the entgql Node lookup for Thread (privacy applies).</summary>
    public async Task<ThreadRecord> GetNodeAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var projectScope = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        var record = await threads.GetByIdAsync(id, projectScope, cancellationToken)
            .ConfigureAwait(false);
        return record ?? throw new InvalidOperationException(ThreadNotFound);
    }

    /// <summary>
    /// Port of the <c>Trace.thread</c> edge (gql_edge.go): the Thread query
    /// policy applies and <c>MaskNotFound</c> turns a missing or
    /// privacy-filtered row into null.
    /// </summary>
    public async Task<ThreadRecord?> GetTraceThreadAsync(
        long threadId,
        CancellationToken cancellationToken = default)
    {
        var projectScope = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        return await threads.GetByIdAsync(threadId, projectScope, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of ThreadService.FirstUserQuery (thread.go:98-112).</summary>
    public async Task<string?> GetFirstUserQueryAsync(
        long threadDbId,
        CancellationToken cancellationToken = default)
    {
        var trace = await GetThreadFirstTraceAsync(threadDbId, cancellationToken)
            .ConfigureAwait(false);
        return trace is null
            ? null
            : await segments.GetFirstUserQueryAsync(trace.Id, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>Port of ThreadService.FirstText (thread.go:114-128).</summary>
    public async Task<string?> GetFirstTextAsync(
        long threadDbId,
        CancellationToken cancellationToken = default)
    {
        var trace = await GetThreadFirstTraceAsync(threadDbId, cancellationToken)
            .ConfigureAwait(false);
        return trace is null
            ? null
            : await segments.GetFirstTextAsync(trace.Id, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>Port of ThreadService.UsageMetadata (thread.go:219-231).</summary>
    public async Task<UsageMetadata> GetUsageMetadataAsync(
        long threadDbId,
        CancellationToken cancellationToken = default)
    {
        UsageMetadataRow row;
        try
        {
            row = await usage.AggregateForThreadAsync(threadDbId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to aggregate usage metadata: ", ex);
        }

        return UsageMetadata.FromRow(row);
    }

    /// <summary>
    /// Port of the archivedTracesCount resolver (axonhub.resolvers.go:1027-1041):
    /// a Trace query, so the Trace query policy applies and every failure -
    /// including a deny - carries the count prefix.
    /// </summary>
    public async Task<long> GetArchivedTracesCountAsync(
        long threadDbId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var projectScope = await privacy.EvaluateQueryAsync(cancellationToken)
                .ConfigureAwait(false);
            return await traces.CountArchivedByThreadAsync(
                    threadDbId,
                    projectScope,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to count archived traces: ", ex);
        }
    }

    /// <summary>Port of ThreadService.Archive (thread.go:130-152).</summary>
    public Task ArchiveAsync(long id, CancellationToken cancellationToken = default)
        => TransitionAsync(
            id,
            ThreadStatuses.Active,
            ThreadStatuses.Archived,
            "archive",
            "active",
            cancellationToken);

    /// <summary>Port of ThreadService.Unarchive (thread.go:154-176).</summary>
    public Task UnarchiveAsync(long id, CancellationToken cancellationToken = default)
        => TransitionAsync(
            id,
            ThreadStatuses.Archived,
            ThreadStatuses.Active,
            "unarchive",
            "archived",
            cancellationToken);

    /// <summary>Port of ThreadService.Retain (thread.go:178-198).</summary>
    public Task RetainAsync(long id, CancellationToken cancellationToken = default)
        => TransitionAsync(
            id,
            ThreadStatuses.Active,
            ThreadStatuses.Retained,
            "retain",
            "active",
            cancellationToken);

    /// <summary>Port of ThreadService.Unretain (thread.go:200-220).</summary>
    public Task UnretainAsync(long id, CancellationToken cancellationToken = default)
        => TransitionAsync(
            id,
            ThreadStatuses.Retained,
            ThreadStatuses.Active,
            "unretain",
            "retained",
            cancellationToken);

    /// <summary>
    /// Port of TraceService.GetThreadFirstTrace (trace.go:127-147): the oldest
    /// trace of the thread under the Trace query policy; no row yields null.
    /// </summary>
    private async Task<TraceRecord?> GetThreadFirstTraceAsync(
        long threadDbId,
        CancellationToken cancellationToken)
    {
        long? projectScope;
        try
        {
            projectScope = await privacy.EvaluateQueryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap(FirstTracePrefix, ex);
        }

        try
        {
            return await traces.GetThreadFirstTraceAsync(
                    threadDbId,
                    projectScope,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap(FirstTracePrefix, ex);
        }
    }

    /// <summary>
    /// The thread update runs inside the transaction with the mutation privacy
    /// predicate; a deny is reported as <c>failed to &lt;op&gt; thread: </c> and a
    /// status-predicate miss as the fixed <c>cannot ...</c> message. Only a
    /// matched thread update runs the cascade, whose failure keeps its own
    /// prefix.
    /// </summary>
    private async Task TransitionAsync(
        long id,
        string fromStatus,
        string toStatus,
        string operation,
        string fromStatusName,
        CancellationToken cancellationToken)
    {
        long? projectScope;
        try
        {
            projectScope = await privacy.EvaluateMutationAsync(
                    TraceMutationOp.UpdateOne,
                    mutationProjectId: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to " + operation + " thread: ", ex);
        }

        int affected;
        try
        {
            affected = await threads.UpdateStatusAndCascadeAsync(
                    id,
                    projectScope,
                    fromStatus,
                    toStatus,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TraceCascadeException ex)
        {
            throw Wrap(
                "failed to cascade " + operation + " traces: ",
                ex.InnerException ?? ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to " + operation + " thread: ", ex);
        }

        if (affected == 0)
        {
            throw new InvalidOperationException(
                "cannot " + operation + " thread: current status is not "
                + fromStatusName + " or thread not found");
        }
    }

    private static InvalidOperationException Wrap(string prefix, Exception exception)
        => new(prefix + exception.Message, exception);
}
