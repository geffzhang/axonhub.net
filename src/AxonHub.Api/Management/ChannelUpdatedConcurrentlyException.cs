namespace AxonHub.Api.Management;

/// <summary>
/// Port of biz.ErrChannelUpdatedConcurrently (biz/errors.go:27): the guarded
/// channel update matched no row because the stored updated_at moved, or the
/// row vanished. Callers that merge patches onto a read snapshot detect this
/// type to re-read and re-merge (channel_settings.go:186-189).
/// </summary>
public sealed class ChannelUpdatedConcurrentlyException(
    string message = "channel was updated concurrently; retry the operation",
    Exception? innerException = null)
    : InvalidOperationException(message, innerException);
