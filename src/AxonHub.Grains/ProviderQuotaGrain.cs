using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.Logging;

namespace AxonHub.Grains;

/// <summary>
/// One channel's quota actor (provider_quota.go:346-359, 586-618, 792-864):
/// it serializes the checks for the channel's quota group and keeps the last
/// cache entry the check produced, mirroring the reference's
/// <c>quotaCache</c> entry.
/// </summary>
/// <remarks>
/// <para>
/// The grain is invoked as the group leader (<c>group.channels[0]</c>); the
/// API-side runner performs the provider request and persistence, and returns
/// every member's resulting cache entry. The leader's entry stays here so the
/// channel's activation owns exactly the state the reference's process-local
/// cache held for it.
/// </para>
/// <para>
/// An unexpected runner failure is treated like the reference's group
/// goroutine (provider_quota.go:779-786): log it and let the run continue. The
/// previous cache entry is kept because the failed check produced no new
/// information; ordinary provider errors do not take this path (the runner
/// persists them as <c>unknown</c> status and returns normally).
/// </para>
/// </remarks>
public sealed class ProviderQuotaGrain(
    IProviderQuotaGroupRunner runner,
    ILogger<ProviderQuotaGrain> logger) : Grain, IProviderQuotaGrain
{
    private static readonly Action<ILogger, Exception?> LogGroupFailed =
        LoggerMessage.Define(
            LogLevel.Error,
            new EventId(0, "ProviderQuotaGroupFailed"),
            "quota check group failed");

    private ProviderQuotaStatusSnapshot? _status;

    public async Task<ProviderQuotaGroupCheckResult> CheckGroupAsync(
        ProviderQuotaCheckGroup group,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(group);

        ProviderQuotaGroupCheckResult result;
        try
        {
            result = await runner
                .RunGroupAsync(group, now, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LogGroupFailed(logger, ex);
            return new ProviderQuotaGroupCheckResult();
        }

        // The runner reports every member, with null for a channel whose status
        // was removed (credentials disappeared). Only a report replaces the
        // cached entry; an empty result means "no information".
        if (group.Members.Count > 0)
        {
            var leaderId = group.Members[0].Id;
            _status = result.Members.Find(member => member.ChannelId == leaderId)?.Status;
        }

        return result;
    }

    public Task<ProviderQuotaStatusSnapshot?> GetStatusAsync()
        => Task.FromResult(_status);

    public Task ApplyStatusAsync(ProviderQuotaStatusSnapshot status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _status = status;
        return Task.CompletedTask;
    }

    public Task ClearStatusAsync()
    {
        _status = null;
        return Task.CompletedTask;
    }
}
