namespace AxonHub.Abstractions;

/// <summary>
/// Storage seam for the enabled-channel snapshot the load balancer selects
/// from. The reference keeps this snapshot in <c>enabledChannelsCache</c>
/// (internal/server/biz/channel.go), ordered by <c>ordering_weight DESC</c>.
/// </summary>
public interface IEnabledChannelProvider
{
    Task<ChannelDescriptor[]> ListEnabledAsync(CancellationToken cancellationToken = default);
}
