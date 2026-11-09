using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// The parsed <c>gc.cron</c> expression in UTC
/// (<c>scheduler.TaskSpec{Name:"gc", CronExpr:gc.cron, Timezone:"UTC"}</c>,
/// gc.go:77-84). The host keeps the parser and the startup validation; the
/// grain only asks for the next instant.
/// </summary>
public interface IGcScheduleSource
{
    /// <summary>The first instant strictly after <paramref name="after"/> matching the cron.</summary>
    DateTimeOffset NextOccurrence(DateTimeOffset after);
}

/// <summary>Runs <c>Worker.runAutomaticCleanup</c> (gc_internal.go:8-12).</summary>
public interface IGcCleanupRunner
{
    Task RunAutomaticCleanupAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The reference's <c>"gc"</c> cron entry as an actor
/// (gc.Worker.RegisterScheduledTasks, gc.go:77-84): one pending Orleans
/// reminder per cron occurrence.
/// </summary>
public interface IGcGrain : IGrainWithStringKey
{
    /// <summary>The cluster-wide singleton key.</summary>
    public const string SingletonKey = "gc";

    /// <summary>The reminder that carries the pending occurrence.</summary>
    public const string ReminderName = "gc";

    /// <summary>
    /// Registration: arms the pending occurrence when none is pending and
    /// returns the scheduled instant. Like the reference's <c>Register</c>,
    /// it never runs the cleanup.
    /// </summary>
    Task<DateTimeOffset> EnsureScheduledAsync(DateTimeOffset now);

    /// <summary>
    /// The reminder body: arms the following occurrence first (so a failed
    /// run never loses the schedule), then runs the automatic cleanup. A stale
    /// tick for an occurrence this activation already consumed does not run
    /// again.
    /// </summary>
    Task<DateTimeOffset> FireScheduledAsync(DateTimeOffset now);
}
