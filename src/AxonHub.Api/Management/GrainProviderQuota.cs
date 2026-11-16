using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.Logging;

namespace AxonHub.Api.Management;

/// <summary>
/// The API-facing half of the provider quota coordinator
/// (provider_quota.go:620-708): every entry that the reference runs under
/// <c>svc.mu</c> goes through the singleton coordinator grain, whose turns
/// serialize the whole collection run, manual checks, invalidations and
/// post-reset refreshes (§150; C230).
/// </summary>
public interface IProviderQuotaCoordinatorClient
{
    /// <summary><c>ManualCheck</c> (provider_quota.go:620-623).</summary>
    Task RunManualAsync(CancellationToken cancellationToken = default);

    /// <summary>The post-reset refresh group (provider_quota.go:701-713).</summary>
    Task RefreshChannelAsync(
        ProviderQuotaRefreshRequest request,
        CancellationToken cancellationToken = default);

    /// <summary><c>InvalidateChannelQuota</c> (provider_quota.go:595-603).</summary>
    Task InvalidateAsync(long channelId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Production <see cref="IProviderQuotaCoordinatorClient"/>: forwards to the
/// singleton <see cref="IProviderQuotaCoordinatorGrain"/>.
/// </summary>
/// <remarks>
/// Grain calls are not cancellable; the token is accepted for the seam's shape
/// and ignored.
/// </remarks>
public sealed class GrainProviderQuotaCoordinator(IGrainFactory grainFactory)
    : IProviderQuotaCoordinatorClient
{
    private IProviderQuotaCoordinatorGrain Grain => grainFactory.GetGrain<IProviderQuotaCoordinatorGrain>(
        IProviderQuotaCoordinatorGrain.SingletonKey);

    public Task RunManualAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Grain.RunManualAsync();
    }

    public Task RefreshChannelAsync(
        ProviderQuotaRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Grain.RefreshChannelAsync(request);
    }

    public Task InvalidateAsync(long channelId, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Grain.InvalidateAsync(channelId);
    }
}

/// <summary>
/// Production <see cref="IProviderQuotaCoordinatorOperations"/>: the reference's
/// run bodies (provider_quota.go:719-790, 701-713, 605-618), executed inside
/// the coordinator grain's turn so they are mutually exclusive with every
/// other provider quota entry point.
/// </summary>
public sealed class GrainProviderQuotaOperations(
    ProviderQuotaService service,
    IGrainFactory grainFactory,
    ILogger<GrainProviderQuotaOperations> logger) : IProviderQuotaCoordinatorOperations
{
    private static readonly Action<ILogger, long, Exception?> LogClearFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(0, "ProviderQuotaGrainClearFailed"),
            "Failed to clear the provider quota grain state: channel_id={ChannelId}");

    public Task RunAsync(bool force, CancellationToken cancellationToken = default)
        => service.RunQuotaCheckAsync(force, cancellationToken);

    public Task RefreshChannelAsync(
        ProviderQuotaRefreshRequest request,
        CancellationToken cancellationToken = default)
        => service.RefreshChannelQuotaAsync(request, cancellationToken);

    public async Task InvalidateAsync(long channelId, CancellationToken cancellationToken = default)
    {
        try
        {
            // The cache entry is removed even when the hard delete fails
            // (provider_quota.go:605-618), so the grain state is cleared in a
            // finally as well.
            await service.InvalidateChannelQuotaCoreAsync(channelId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await grainFactory
                    .GetGrain<IProviderQuotaGrain>(channelId)
                    .ClearStatusAsync()
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The channel grain's copy is a mirror; a dispatch failure must
                // not turn the invalidation into an error.
                LogClearFailed(logger, channelId, ex);
            }
        }
    }
}

/// <summary>
/// Production <see cref="IQuotaGroupExecutor"/>: dispatches one due group to
/// the leader channel's <see cref="IProviderQuotaGrain"/> and mirrors the
/// result onto the member channels' grains, matching the reference's
/// <c>checkChannelQuota</c> fan-out (provider_quota.go:855-865).
/// </summary>
public sealed class GrainQuotaGroupExecutor(
    IGrainFactory grainFactory,
    ILogger<GrainQuotaGroupExecutor> logger) : IQuotaGroupExecutor
{
    private static readonly Action<ILogger, long, Exception?> LogMirrorFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(0, "ProviderQuotaGrainMirrorFailed"),
            "Failed to mirror the provider quota cache into a member grain: channel_id={ChannelId}");

    public async Task ExecuteAsync(
        ProviderQuotaCheckGroup group,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);
        _ = cancellationToken;

        if (group.Members.Count == 0)
        {
            return;
        }

        var leaderId = group.Members[0].Id;
        var result = await grainFactory
            .GetGrain<IProviderQuotaGrain>(leaderId)
            .CheckGroupAsync(group, now)
            .ConfigureAwait(false);

        foreach (var member in result.Members)
        {
            if (member.ChannelId == leaderId)
            {
                // The leader grain stored its own entry from the same result.
                continue;
            }

            try
            {
                var grain = grainFactory.GetGrain<IProviderQuotaGrain>(member.ChannelId);
                if (member.Status is null)
                {
                    await grain.ClearStatusAsync().ConfigureAwait(false);
                }
                else
                {
                    await grain.ApplyStatusAsync(member.Status).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                LogMirrorFailed(logger, member.ChannelId, ex);
            }
        }
    }
}

/// <summary>
/// Production <see cref="IProviderQuotaScheduleSource"/>: the next firing of
/// the reference's <c>provider-quota-check</c> task
/// (provider_quota.go:401-409). The check interval is the service's fixed
/// five-minute default (provider_quota.go:534-540), so the expression is
/// <c>*/5 * * * *</c>, evaluated in UTC.
/// </summary>
public sealed class GrainProviderQuotaScheduleSource : IProviderQuotaScheduleSource
{
    private readonly GcCronSchedule _schedule = new(
        ProviderQuotaService.IntervalToCronExpr(ProviderQuotaService.DefaultCheckInterval));

    public DateTimeOffset NextOccurrence(DateTimeOffset after) => _schedule.NextOccurrence(after);
}
