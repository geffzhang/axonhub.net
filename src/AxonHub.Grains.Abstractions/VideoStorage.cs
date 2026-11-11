using Orleans;

namespace AxonHub.Grains.Abstractions;

/// <summary>
/// Resolves the video-storage scan's fixed rate from
/// <c>system_video_storage_settings.scan_interval_minutes</c>
/// (video_storage/worker.go:61-70). The reference re-reads the setting on
/// registration and on every reschedule and falls back to one minute when the
/// stored value is non-positive.
/// </summary>
public interface IVideoStorageScheduleSource
{
    Task<TimeSpan> GetIntervalAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Runs one <c>video_storage.Worker.scanAndSave</c> pass
/// (internal/server/video_storage/worker.go:109-158).
/// </summary>
public interface IVideoStorageScanRunner
{
    Task ScanAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The reference's <c>"video-storage"</c> fixed-rate scheduler entry
/// (video_storage/worker.go:54-95) as an actor: one reminder carries the scan
/// cadence, and the settings mutation re-arms it through
/// <see cref="RescheduleAsync"/>.
/// </summary>
/// <remarks>
/// The reference registers a scheduler task with <c>FixRate</c> equal to the
/// stored interval; <c>Reschedule</c> cancels the old cron and re-creates it
/// with the new interval. The grain mirrors that with one renewable reminder:
/// the fire path re-arms the following occurrence before running the scan, so
/// a failed or slow scan never drops the schedule. The reference's pool
/// executor may start scans in parallel; grain turns serialize them, which only
/// removes duplicate downloads of the same rows.
/// </remarks>
public interface IVideoStorageGrain : IGrainWithStringKey
{
    /// <summary>The cluster-wide singleton key.</summary>
    public const string SingletonKey = "video-storage";

    /// <summary>The reminder that carries the scan cadence.</summary>
    public const string ReminderName = "video-storage";

    /// <summary>
    /// Registration: arms the pending scan when none is pending and returns
    /// the scheduled instant. Like the reference's <c>Register</c>, it never
    /// runs the scan.
    /// </summary>
    Task<DateTimeOffset> EnsureScheduledAsync(DateTimeOffset now);

    /// <summary>
    /// Port of <c>Worker.Reschedule</c> (worker.go:73-95): drops the pending
    /// scan and arms the next one from <paramref name="now"/> at the current
    /// stored interval.
    /// </summary>
    Task<DateTimeOffset> RescheduleAsync(DateTimeOffset now);

    /// <summary>
    /// The reminder body: arms the following scan first, then runs one pass.
    /// A stale tick for an occurrence this activation already consumed does not
    /// run again.
    /// </summary>
    Task<DateTimeOffset> FireScheduledAsync(DateTimeOffset now);
}
