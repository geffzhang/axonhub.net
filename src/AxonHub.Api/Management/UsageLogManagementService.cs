using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of the entgql UsageLog root connection and Node lookup
/// (internal/server/gql/ent.resolvers.go:572-586, 334-342). The reference runs
/// both queries through the UsageLog schema's query policy, which installs a
/// project predicate and, for a non-owner project member, the per-request
/// visibility predicate; the port applies the same decisions explicitly (C193).
/// </summary>
public sealed class UsageLogManagementService(
    IUsageLogStore usageLogs,
    TracePrivacy privacy)
{
    /// <summary>Ent's NotFoundError text for the UsageLog label.</summary>
    public const string UsageLogNotFound = "ent: usage_log not found";

    public async Task<ListPage<UsageLogRecord>> ListAsync(
        UsageLogListQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var access = await privacy.EvaluateUsageLogQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        return await usageLogs.ListAsync(
                query with
                {
                    ProjectScope = access?.ProjectId,
                    VisibilityUserId = access?.VisibilityUserId,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<UsageLogRecord> GetNodeAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var access = await privacy.EvaluateUsageLogQueryAsync(cancellationToken)
            .ConfigureAwait(false);

        var record = await usageLogs.GetByIdAsync(
                id,
                access?.ProjectId,
                access?.VisibilityUserId,
                cancellationToken)
            .ConfigureAwait(false);
        return record ?? throw new InvalidOperationException(UsageLogNotFound);
    }
}
