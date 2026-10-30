using AxonHub.Abstractions;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Builds the outbound transformer for one candidate, the job the reference does
/// once per channel while loading it (internal/server/biz/channel_llm.go:
/// <c>buildChannel</c> → the <c>channel.TypeOpenai</c> branch).
/// </summary>
/// <remarks>
/// Only the <c>openai</c> channel type is ported (the vertical slice); the other
/// provider types are registered as a scope cut. Multiple enabled API keys use
/// the first key because the reference's TraceStickyKeyProvider is deferred
/// (C013).
/// </remarks>
public static class ChannelOutboundFactory
{
    public const string OpenAiChannelType = "openai";

    public static OpenAiOutboundTransformer Create(ChannelCandidate candidate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!string.Equals(candidate.Type, OpenAiChannelType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "unsupported channel type: " + candidate.Type + " for channel " + candidate.ChannelName);
        }

        var apiKeys = ChannelCredentials.GetEnabledApiKeys(
            candidate.Credentials,
            candidate.DisabledApiKeys,
            now);

        if (apiKeys.Count == 0)
        {
            throw new InvalidOperationException("missing api key for channel " + candidate.ChannelName);
        }

        return OpenAiOutboundTransformer.Create(candidate.BaseUrl ?? string.Empty, apiKeys[0]);
    }
}
