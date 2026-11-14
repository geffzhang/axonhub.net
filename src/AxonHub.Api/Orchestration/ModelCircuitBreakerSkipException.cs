namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of orchestrator.errSkipCandidateByCircuitBreaker
/// (outbound.go:614): the model circuit is open and no probe lease is available,
/// so this attempt must bounce to the next candidate without counting a channel
/// failure.
/// </summary>
internal sealed class ModelCircuitBreakerSkipException(long channelId, string modelId)
    : Exception("skip candidate by circuit breaker")
{
    public long ChannelId { get; } = channelId;

    public string ModelId { get; } = modelId;
}
