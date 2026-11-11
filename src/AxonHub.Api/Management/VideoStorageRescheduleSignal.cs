namespace AxonHub.Api.Management;

/// <summary>
/// The Reschedule side effect of updating the video storage settings
/// (video_storage.Worker.Reschedule, internal/server/video_storage/worker.go:73-95).
/// The reference cancels and re-creates the <c>video-storage</c> scan task and
/// swallows scheduling failures. Production registers
/// <see cref="GrainVideoStorageRescheduleSignal"/>, which re-arms the
/// video-storage grain; hosts that do not register a signal keep the
/// mutation's no-op behaviour after the settings write.
/// </summary>
public interface IVideoStorageRescheduleSignal
{
    Task RescheduleAsync(CancellationToken cancellationToken = default);
}
