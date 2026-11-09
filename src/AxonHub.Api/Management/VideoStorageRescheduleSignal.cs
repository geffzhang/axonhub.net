namespace AxonHub.Api.Management;

/// <summary>
/// The Reschedule side effect of updating the video storage settings
/// (video_storage.Worker.Reschedule, internal/server/video_storage/worker.go:73-95).
/// The reference cancels and re-creates the <c>video-storage</c> scan task and
/// swallows scheduling failures; the scan worker itself is not ported yet
/// (registered as a gap), so production leaves this signal unregistered and the
/// mutation stays a no-op after the settings write, like the reference's nil
/// scheduler.
/// </summary>
public interface IVideoStorageRescheduleSignal
{
    Task RescheduleAsync(CancellationToken cancellationToken = default);
}
