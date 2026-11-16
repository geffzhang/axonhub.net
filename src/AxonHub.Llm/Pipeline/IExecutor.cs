using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Pipeline;

/// <summary>
/// Port of pipeline.Executor (llm/pipeline/executor.go): the transport seam.
/// </summary>
/// <remarks>
/// Everything below this interface is the network; everything above is
/// protocol. Tests substitute a scripted executor, so the retry and timeout
/// behaviour can be exercised without an upstream.
/// </remarks>
public interface IExecutor
{
    /// <summary>Executes a request and returns a complete response.</summary>
    HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request);

    /// <summary>Executes a request and returns a streaming response.</summary>
    IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request);
}

/// <summary>
/// Port of pipeline.Retryable: transformers that can switch to another channel.
/// </summary>
public interface IRetryable
{
    /// <summary>True if there are more channels to switch to. Only called while the attempt count allows it.</summary>
    bool HasMoreChannels();

    /// <summary>
    /// True when this failure is eligible for a channel switch. Existing
    /// retryable transformers keep the historical behavior by default.
    /// </summary>
    bool CanSwitchChannel(Exception error) => true;

    /// <summary>Switches to the next channel. Returns null on success, the failure otherwise.</summary>
    Exception? TryNextChannel(CancellationToken cancellationToken);
}

/// <summary>
/// Port of pipeline.ChannelRetryable: transformers that can retry the same
/// channel given a concrete error.
/// </summary>
public interface IChannelRetryable
{
    /// <summary>True if the current channel may be retried for this error.</summary>
    bool CanRetry(Exception error);

    /// <summary>Prepares the transformer for a retry. Returns null on success.</summary>
    Exception? TryPrepareForRetry(CancellationToken cancellationToken);
}

/// <summary>
/// Port of pipeline.ChannelCustomizedExecutor: channels that need their own
/// transport (e.g. AWS signing).
/// </summary>
public interface IChannelCustomizedExecutor
{
    IExecutor CustomizeExecutor(IExecutor executor);
}
