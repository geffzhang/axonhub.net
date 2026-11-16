using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Management;

/// <summary>
/// Runs one <c>ChannelProbeService.runProbe</c> pass in a fresh scope: the
/// grain turn has no request scope of its own. The reference's scheduler
/// callback wraps the pass in a system bypass
/// (<c>runProbePeriodically</c>, channel_probe_internal.go:8-12); the scoped
/// service reads the setting through the admin settings surface, which is the
/// system path in the port.
/// </summary>
public sealed class GrainChannelProbeRunRunner(IServiceScopeFactory scopeFactory)
    : IChannelProbeRunRunner
{
    public async Task RunProbeAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ChannelProbeService>();
        await service.RunProbeAsync(cancellationToken).ConfigureAwait(false);
    }
}
