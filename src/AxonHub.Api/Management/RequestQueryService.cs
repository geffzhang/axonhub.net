using AxonHub.Api.GraphQL;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Reads behind the requests page (root <c>requests</c>, the <c>node(id)</c>
/// Request branch and the Request detail edges). The reference runs every ent
/// query through the Request/APIKey/Channel/Project privacy policies; the port
/// evaluates the same decisions explicitly (C189, C192).
/// </summary>
public sealed class RequestQueryService(
    IRequestSegmentStore requests,
    IRequestExecutionStore executions,
    IUsageLogStore usageLogs,
    IApiKeyStore apiKeys,
    IChannelStore channels,
    ITraceAdminStore traces,
    IDataStorageStore dataStorages,
    ApiKeyPrivacy apiKeyPrivacy,
    ModelPrivacy channelPrivacy,
    TracePrivacy requestPrivacy,
    DataStoragePrivacy dataStoragePrivacy)
{
    /// <summary>Ent's NotFoundError text for the Request label.</summary>
    public const string RequestNotFound = "ent: request not found";

    /// <summary>Ent's NotFoundError text for the Project label.</summary>
    public const string ProjectNotFound = "ent: project not found";

    /// <summary>Port of the entgql Node lookup for Request (privacy applies).</summary>
    public async Task<RequestRecord> GetNodeAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var access = await requestPrivacy.EvaluateRequestQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        var record = await requests.GetByIdAsync(
                id,
                access?.ProjectId,
                access?.VisibilityUserId,
                cancellationToken)
            .ConfigureAwait(false);
        return record ?? throw new InvalidOperationException(RequestNotFound);
    }

    /// <summary>Port of the Request.executions connection (gql_edge.go:503-522).</summary>
    public Task<ListPage<RequestExecutionRecord>> ListExecutionsAsync(
        long requestId,
        RequestExecutionListQuery query,
        CancellationToken cancellationToken = default)
        => executions.ListAsync(query with { RequestScope = requestId }, cancellationToken);

    /// <summary>Port of the Request.usageLogs connection (gql_edge.go:532-551).</summary>
    public Task<ListPage<UsageLogRecord>> ListUsageLogsAsync(
        long requestId,
        UsageLogListQuery query,
        CancellationToken cancellationToken = default)
        => usageLogs.ListAsync(query with { RequestScope = requestId }, cancellationToken);

    /// <summary>
    /// Port of getNilableChannel (internal/server/gql/graphql.go:228-246): a
    /// missing channel, a deleted channel and a privacy deny all surface as
    /// null; any other failure is wrapped with <c>failed to load channel: </c>.
    /// </summary>
    public async Task<ChannelRecord?> GetVisibleChannelOrNullAsync(
        long? channelId,
        CancellationToken cancellationToken = default)
    {
        if (channelId is null or 0)
        {
            return null;
        }

        try
        {
            await channelPrivacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PrivacyDenyException)
        {
            return null;
        }

        try
        {
            return await channels.GetByIdAsync(channelId.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("failed to load channel: " + ex.Message, ex);
        }
    }

    /// <summary>
    /// Port of the APIKey edge query (gql_edge.go:471-477): the APIKey query
    /// policy runs, a deny propagates and MaskNotFound turns a missing row into
    /// null.
    /// </summary>
    public async Task<ApiKeyRecord?> GetVisibleApiKeyOrNullAsync(
        long? apiKeyId,
        CancellationToken cancellationToken = default)
    {
        var access = await apiKeyPrivacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (apiKeyId is null or 0)
        {
            return null;
        }

        return await apiKeys.GetByIdAsync(apiKeyId.Value, access, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Port of the Project edge query (gql_edge.go:479-485): the Project query
    /// policy runs; the port's existing helper collapses a deny and a missing
    /// row to null, and the non-null field reports <c>ent: project not found</c>
    /// (matching Trace.project).
    /// </summary>
    public async Task<ProjectRecord> GetVisibleProjectAsync(
        long projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await apiKeyPrivacy.GetVisibleProjectAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        return project ?? throw new InvalidOperationException(ProjectNotFound);
    }

    /// <summary>
    /// Port of the DataStorage edge query (gql_edge.go:495-502, 569-576): the
    /// DataStorage query policy runs before the SQL, so a deny propagates even
    /// when the foreign key is null; MaskNotFound then turns a missing or
    /// soft-deleted row into null.
    /// </summary>
    public async Task<DataStorageRecord?> GetVisibleDataStorageOrNullAsync(
        long? dataStorageId,
        CancellationToken cancellationToken = default)
    {
        await dataStoragePrivacy.EvaluateQueryAsync(cancellationToken).ConfigureAwait(false);
        if (dataStorageId is null or 0)
        {
            return null;
        }

        return await dataStorages.GetByIdAsync(dataStorageId.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Port of the Trace edge query (gql_edge.go:487-493): the Trace query
    /// policy runs, a deny propagates and MaskNotFound turns a missing row into
    /// null.
    /// </summary>
    public async Task<TraceRecord?> GetVisibleTraceOrNullAsync(
        long? traceId,
        CancellationToken cancellationToken = default)
    {
        var projectScope = await requestPrivacy.EvaluateQueryAsync(cancellationToken)
            .ConfigureAwait(false);
        if (traceId is null or 0)
        {
            return null;
        }

        return await traces.GetByIdAsync(traceId.Value, projectScope, cancellationToken)
            .ConfigureAwait(false);
    }
}
