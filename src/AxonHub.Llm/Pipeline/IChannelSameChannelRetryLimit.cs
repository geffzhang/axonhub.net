namespace AxonHub.Llm.Pipeline;

/// <summary>
/// Port of pipeline.ChannelSameChannelRetryLimit (pipeline.go:39-51): an
/// optional interface a channel-retryable outbound may implement to override
/// the global max same-channel retries for the current channel.
/// </summary>
public interface IChannelSameChannelRetryLimit
{
    /// <summary>
    /// Returns the channel's override. <c>HasOverride</c> is false when the
    /// channel sets none, so the pipeline keeps the global policy value.
    /// </summary>
    (int Limit, bool HasOverride) SameChannelRetryLimit();
}
