namespace AxonHub.Llm.Pipeline;

/// <summary>
/// Port of the sentinel errors in llm/pipeline/empty_response.go. Each is a
/// shared instance so <see cref="GoErrors.Is"/> matches Go's identity check.
/// </summary>
public static class PipelineErrors
{
    /// <summary>
    /// The response contains no meaningful content. Go aliases this sentinel to
    /// <c>llm.ErrEmptyResponse</c> (#2657), so structured stream failures can
    /// carry it as their cause.
    /// </summary>
    public static readonly Exception EmptyResponse = LlmErrors.EmptyResponse;

    /// <summary>A streaming response did not produce the first event in time.</summary>
    public static readonly Exception StreamFirstEventTimeout = new PipelineSentinelException("stream first event timeout");

    /// <summary>A retryable stream buffered too much non-meaningful data before a safe commit point.</summary>
    public static readonly Exception PreCommitBufferExceeded = new PipelineSentinelException("pre-commit stream buffer limit exceeded");

    /// <summary>A non-streaming response did not complete in time.</summary>
    public static readonly Exception NonStreamResponseTimeout = new PipelineSentinelException("non-stream response timeout");

    /// <summary>An auto-upgraded streaming request produced no inbound chunks.</summary>
    public static readonly Exception EmptyStreamChunks = new PipelineSentinelException("empty stream chunks");

    /// <summary>Inbound chunk aggregation produced an empty body.</summary>
    public static readonly Exception EmptyAggregatedBody = new PipelineSentinelException("empty aggregated body");
}

/// <summary>A named pipeline failure; identity, not type, carries the meaning.</summary>
public sealed class PipelineSentinelException : Exception
{
    public PipelineSentinelException(string message)
        : base(message)
    {
    }
}
