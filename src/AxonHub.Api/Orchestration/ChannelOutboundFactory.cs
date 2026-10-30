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
/// provider types are registered as a scope cut. Key selection follows
/// getAPIKeyProvider: a single enabled key is static, several enabled keys are
/// chosen at random per attempt because the trace subsystem (and therefore
/// trace-sticky rendezvous selection) is not ported yet (C013).
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

        var enabledKeys = ChannelCredentials.GetEnabledApiKeys(
            candidate.Credentials,
            candidate.DisabledApiKeys,
            now);

        if (enabledKeys.Count == 0)
        {
            throw new InvalidOperationException("missing api key for channel " + candidate.ChannelName);
        }

        var provider = new ChannelApiKeyProvider(
            enabledKeys,
            ChannelCredentials.GetAllApiKeys(candidate.Credentials));

        return OpenAiOutboundTransformer.Create(
            candidate.BaseUrl ?? string.Empty,
            provider,
            provider.KeyIndex);
    }
}
