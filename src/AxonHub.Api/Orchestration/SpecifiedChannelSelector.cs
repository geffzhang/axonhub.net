using AxonHub.Api.Auth;
using AxonHub.Llm;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of orchestrator.SpecifiedChannelSelector
/// (internal/server/orchestrator/candidates.go:1026-1078): every selection
/// resolves exactly the configured channel, re-read from the store per attempt.
/// The playground and the tester use it to pin the caller's channel id.
/// </summary>
public sealed class SpecifiedChannelSelector(
    IChannelWriteStore channels,
    long channelId,
    string? selectedApiKey = null) : IModelCandidateSelector
{
    public async Task<CandidateSelection> SelectAsync(
        LlmRequest request,
        RequestIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var candidate = await SpecifiedChannelCandidateFactory
            .BuildCandidateAsync(channels, channelId, request, selectedApiKey, cancellationToken)
            .ConfigureAwait(false);

        // The reference returns an empty list when the request needs an
        // explicit endpoint the channel cannot provide; the caller reports it
        // as the invalid-model error.
        return candidate is null
            ? new CandidateSelection([], RoutingPolicy: null)
            : new CandidateSelection([candidate], RoutingPolicy: null);
    }
}
