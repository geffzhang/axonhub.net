using AxonHub.Abstractions;
using AxonHub.Api.Management;

namespace AxonHub.Api.Tests;

/// <summary>
/// Records the credential mutations the auto-disable engine drives
/// (<c>ChannelService.DisableAPIKey</c> / <c>DeleteDisabledAPIKeys</c>) and
/// scripts their failure modes, so every action branch is observable without a
/// channel store.
/// </summary>
internal sealed class FakeChannelCredentialMutations : IChannelCredentialMutations
{
    public sealed record DisableCall(
        long ChannelId,
        string Key,
        int ErrorCode,
        string Reason,
        DateTime? ExpiresAt);

    private readonly object _gate = new();

    public List<DisableCall> DisableCalls { get; } = [];

    public List<(long ChannelId, IReadOnlyList<string> Keys)> DeleteCalls { get; } = [];

    /// <summary>Completes when the first disable reaches the seam (queue tests).</summary>
    public TaskCompletionSource<DisableCall> DisableStarted { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Exception? DisableFailure { get; set; }

    public Exception? DeleteFailure { get; set; }

    public DeleteDisabledApiKeysResult DeleteResult { get; set; } = new(true, string.Empty);

    public Task DisableApiKeyAsync(
        long channelId,
        string key,
        int errorCode,
        string reason,
        DateTime? expiresAt = null,
        CancellationToken cancellationToken = default)
    {
        if (DisableFailure is not null)
        {
            return Task.FromException(DisableFailure);
        }

        var call = new DisableCall(channelId, key, errorCode, reason, expiresAt);
        lock (_gate)
        {
            DisableCalls.Add(call);
        }

        DisableStarted.TrySetResult(call);
        return Task.CompletedTask;
    }

    public Task<DeleteDisabledApiKeysResult> DeleteDisabledApiKeysAsync(
        long channelId,
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        if (DeleteFailure is not null)
        {
            return Task.FromException<DeleteDisabledApiKeysResult>(DeleteFailure);
        }

        lock (_gate)
        {
            DeleteCalls.Add((channelId, keys));
        }

        return Task.FromResult(DeleteResult);
    }
}

/// <summary>Captures the events the engine hands to the background sink.</summary>
internal sealed class FakeWebhookSink : IWebhookNotificationSink
{
    public List<ChannelAutoDisabledEvent> Events { get; } = [];

    public void PublishChannelAutoDisabled(ChannelAutoDisabledEvent notification)
        => Events.Add(notification);
}

/// <summary>
/// A provider whose read always fails, standing in for a storage-backed enabled
/// channel list that cannot be read during evaluation.
/// </summary>
internal sealed class FailingEnabledChannelProvider(Exception failure) : IEnabledChannelProvider
{
    public Task<ChannelDescriptor[]> ListEnabledAsync(
        CancellationToken cancellationToken = default)
        => Task.FromException<ChannelDescriptor[]>(failure);
}
