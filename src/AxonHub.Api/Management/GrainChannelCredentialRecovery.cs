using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Management;

/// <summary>
/// Runs one <c>ChannelService.cleanupExpiredDisabledAPIKeys</c> pass in a fresh
/// scope: the grain turn has no request scope of its own. The reference's
/// scheduler callback wraps the pass in a system bypass
/// (<c>authz.WithSystemBypass(ctx, "channel-cleanup-expired-disabled-api-keys")</c>,
/// channel_apikey.go:398); <see cref="ChannelLifecycleService.CleanupExpiredDisabledApiKeysAsync"/>
/// performs the internal reads without the Channel query policy, which is that
/// bypass in the port.
/// </summary>
public sealed class GrainChannelCredentialRecoveryRunner(IServiceScopeFactory scopeFactory)
    : IChannelCredentialRecoveryRunner
{
    public async Task RunRecoveryAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ChannelLifecycleService>();
        await service.CleanupExpiredDisabledApiKeysAsync(cancellationToken).ConfigureAwait(false);
    }
}
