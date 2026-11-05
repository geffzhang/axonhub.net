using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of the entgql Request connection used by <c>Trace.requests</c>
/// (internal/ent/gql_edge.go:690-704). The reference runs the query through the
/// Request schema's query policy, which installs a project predicate and, for
/// a non-owner project member, the request-visibility predicate; the port
/// applies the same decisions explicitly (C189).
/// </summary>
public sealed class RequestManagementService(
    IRequestSegmentStore requests,
    TracePrivacy privacy)
{
    public async Task<ListPage<RequestRecord>> ListAsync(
        RequestListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var access = await privacy.EvaluateRequestQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        return await requests.ListAsync(
                query with
                {
                    ProjectScope = access?.ProjectId,
                    VisibilityUserId = access?.VisibilityUserId,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
