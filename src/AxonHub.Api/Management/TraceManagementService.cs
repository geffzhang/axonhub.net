using System.Text.Json;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Traces;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>Port of biz.UsageMetadata (internal/server/biz/usage_metadata.go:14-21).</summary>
/// <remarks>
/// The reference fields are Go <c>int</c> (64-bit on its platforms) while the
/// SDL exposes them as GraphQL <c>Int</c>; the GraphQL mapping applies the
/// 32-bit cast the same way the other management types do (C190).
/// </remarks>
public sealed record UsageMetadata(
    long TotalInputTokens,
    long TotalOutputTokens,
    long TotalTokens,
    long TotalCachedTokens,
    long TotalCachedWriteTokens,
    decimal TotalCost)
{
    public static UsageMetadata FromRow(UsageMetadataRow row)
        => new(
            row.TotalInputTokens,
            row.TotalOutputTokens,
            row.TotalTokens,
            row.TotalCachedTokens,
            row.TotalCachedWriteTokens,
            // decimal.NewFromFloat(row.TotalCost.Float64) on a NULL scan keeps
            // decimal.Zero; the store coalesces NULL to 0 so the helper's
            // zero mapping matches.
            GoFloatDecimal.FromFloat(row.TotalCost));
}

/// <summary>The trace status strings of internal/ent/trace/trace.go.</summary>
public static class TraceStatuses
{
    public const string Active = "active";

    public const string Archived = "archived";

    public const string Retained = "retained";
}

/// <summary>The thread status strings of internal/ent/thread/thread.go.</summary>
public static class ThreadStatuses
{
    public const string Active = "active";

    public const string Archived = "archived";

    public const string Retained = "retained";
}

/// <summary>
/// Port of the management surface of <c>biz.TraceService</c>
/// (internal/server/biz/trace.go:117-256) plus the entgql Trace connection and
/// Node lookup. The reference runs every query and mutation through an
/// ent client whose privacy policy is installed in the context; the port
/// evaluates the same policy explicitly and turns its result into the store's
/// project predicate (C189).
/// </summary>
public sealed class TraceManagementService(
    ITraceAdminStore traces,
    IUsageMetadataStore usage,
    TraceSegmentService segments,
    TracePrivacy privacy)
{
    /// <summary>Ent's NotFoundError text for the Trace label.</summary>
    public const string TraceNotFound = "ent: trace not found";

    /// <summary>Port of the entgql Trace connection (ent.resolvers.go:557-570).</summary>
    public async Task<ListPage<TraceRecord>> ListAsync(
        TraceListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var projectScope = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        return await traces.ListAsync(
                query with { ProjectScope = projectScope },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Port of the entgql Node lookup for Trace (privacy applies).</summary>
    public async Task<TraceRecord> GetNodeAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var projectScope = await privacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        var record = await traces.GetByIdAsync(id, projectScope, cancellationToken)
            .ConfigureAwait(false);
        return record ?? throw new InvalidOperationException(TraceNotFound);
    }

    /// <summary>Port of TraceService.GetRootSegment (trace.go:448-555).</summary>
    public Task<TraceSegment?> GetRootSegmentAsync(
        long traceDbId,
        CancellationToken cancellationToken = default)
        => segments.GetRootSegmentAsync(traceDbId, cancellationToken);

    /// <summary>
    /// Port of the rawRootSegment resolver (axonhub.resolvers.go:1049-1062):
    /// <c>json.Marshal(segment)</c> with Go's encoding/json semantics. A nil
    /// segment (no completed request or a filtered request query) marshals to
    /// <c>null</c>.
    /// </summary>
    public async Task<JsonElement?> GetRawRootSegmentAsync(
        long traceDbId,
        CancellationToken cancellationToken = default)
    {
        var segment = await segments.GetRootSegmentAsync(traceDbId, cancellationToken)
            .ConfigureAwait(false);
        return TraceSegmentJson.SerializeToElement(segment);
    }

    /// <summary>Port of TraceService.GetFirstUserQuery (trace.go:149-164).</summary>
    public Task<string?> GetFirstUserQueryAsync(
        long traceDbId,
        CancellationToken cancellationToken = default)
        => segments.GetFirstUserQueryAsync(traceDbId, cancellationToken);

    /// <summary>Port of TraceService.GetFirstText (trace.go:166-178).</summary>
    public Task<string?> GetFirstTextAsync(
        long traceDbId,
        CancellationToken cancellationToken = default)
        => segments.GetFirstTextAsync(traceDbId, cancellationToken);

    /// <summary>Port of TraceService.UsageMetadata (trace.go:252-266).</summary>
    public async Task<UsageMetadata> GetUsageMetadataAsync(
        long traceDbId,
        CancellationToken cancellationToken = default)
    {
        UsageMetadataRow row;
        try
        {
            row = await usage.AggregateForTraceAsync(traceDbId, cancellationToken)
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

    /// <summary>Port of TraceService.Archive (trace.go:179-193).</summary>
    public Task ArchiveAsync(long id, CancellationToken cancellationToken = default)
        => TransitionAsync(
            id,
            TraceStatuses.Active,
            TraceStatuses.Archived,
            "archive",
            "active",
            cancellationToken);

    /// <summary>Port of TraceService.Unarchive (trace.go:195-209).</summary>
    public Task UnarchiveAsync(long id, CancellationToken cancellationToken = default)
        => TransitionAsync(
            id,
            TraceStatuses.Archived,
            TraceStatuses.Active,
            "unarchive",
            "archived",
            cancellationToken);

    /// <summary>Port of TraceService.Retain (trace.go:211-225).</summary>
    public Task RetainAsync(long id, CancellationToken cancellationToken = default)
        => TransitionAsync(
            id,
            TraceStatuses.Active,
            TraceStatuses.Retained,
            "retain",
            "active",
            cancellationToken);

    /// <summary>Port of TraceService.Unretain (trace.go:227-241).</summary>
    public Task UnretainAsync(long id, CancellationToken cancellationToken = default)
        => TransitionAsync(
            id,
            TraceStatuses.Retained,
            TraceStatuses.Active,
            "unretain",
            "retained",
            cancellationToken);

    /// <summary>
    /// <c>Trace.UpdateOneID(id).Where(status=from).SetStatus(to).Save(ctx)</c>.
    /// The mutation privacy check runs inside Save, so a deny is reported with
    /// the <c>failed to &lt;op&gt; trace: </c> prefix, while a row that does not
    /// match the status predicate is ent's NotFoundError and becomes the
    /// fixed <c>cannot ...</c> message.
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
            throw Wrap("failed to " + operation + " trace: ", ex);
        }

        int affected;
        try
        {
            affected = await traces.UpdateStatusAsync(
                    id,
                    projectScope,
                    fromStatus,
                    toStatus,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Wrap("failed to " + operation + " trace: ", ex);
        }

        if (affected == 0)
        {
            throw new InvalidOperationException(
                "cannot " + operation + " trace: current status is not "
                + fromStatusName + " or trace not found");
        }
    }

    private static InvalidOperationException Wrap(string prefix, Exception exception)
        => new(prefix + exception.Message, exception);
}
