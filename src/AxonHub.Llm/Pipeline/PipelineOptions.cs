namespace AxonHub.Llm.Pipeline;

/// <summary>
/// Port of the pipeline.* fields configured through pipeline.Option
/// (llm/pipeline/pipeline.go:46-83). The reference's zero values are the
/// defaults: retries off, detection off, timeouts off.
/// </summary>
public sealed class PipelineOptions
{
    /// <summary>Maximum number of times to switch to another channel.</summary>
    public int MaxChannelRetries { get; set; }

    /// <summary>Maximum number of same-channel retries.</summary>
    public int MaxSameChannelRetries { get; set; }

    /// <summary>
    /// The global retry policy is switched off. A channel-level override only
    /// adjusts the count and must not switch retries back on.
    /// </summary>
    public bool RetriesDisabled { get; set; }

    /// <summary>Delay between retries.</summary>
    public TimeSpan RetryDelay { get; set; }

    /// <summary>
    /// Enables empty-response detection: the pipeline pre-reads the stream and
    /// fails the attempt when it ends without meaningful content, so the retry
    /// flow can handle it like any other failed attempt.
    /// </summary>
    public bool EmptyResponseDetection { get; set; }

    /// <summary>Maximum wait for the first streaming event.</summary>
    public TimeSpan StreamFirstEventTimeout { get; set; }

    /// <summary>Maximum wait for a complete non-streaming response.</summary>
    public TimeSpan NonStreamTimeout { get; set; }

    /// <summary>Pipeline decorators, in registration order.</summary>
    public List<IPipelineMiddleware> Middlewares { get; } = [];
}

/// <summary>
/// Port of pipeline.Result: the handler gets either a complete response or an
/// event stream, never both.
/// </summary>
public sealed class PipelineResult
{
    /// <summary>Whether the response is a stream.</summary>
    public bool Stream { get; init; }

    /// <summary>The final HTTP response, when <see cref="Stream"/> is false.</summary>
    public Http.HttpResponseData? Response { get; init; }

    /// <summary>The event stream, when <see cref="Stream"/> is true.</summary>
    public Streams.IStream<Http.StreamEvent>? EventStream { get; init; }
}
