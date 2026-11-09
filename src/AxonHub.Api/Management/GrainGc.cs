using AxonHub.Grains.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Management;

/// <summary>
/// Feeds the parsed <c>gc.cron</c> (already validated at startup by
/// <see cref="GcCronSchedule"/>) to the singleton GC grain.
/// </summary>
public sealed class GrainGcScheduleSource(GcCronSchedule schedule) : IGcScheduleSource
{
    public DateTimeOffset NextOccurrence(DateTimeOffset after) => schedule.NextOccurrence(after);
}

/// <summary>
/// Runs <c>Worker.runAutomaticCleanup</c> (gc_internal.go:8-12) in a fresh
/// scope: the grain turn has no request scope of its own.
/// </summary>
public sealed class GrainGcCleanupRunner(IServiceScopeFactory scopeFactory) : IGcCleanupRunner
{
    public async Task RunAutomaticCleanupAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var cleanup = scope.ServiceProvider.GetRequiredService<IGcCleanupService>();
        await cleanup.RunAutomaticCleanupAsync(cancellationToken).ConfigureAwait(false);
    }
}
