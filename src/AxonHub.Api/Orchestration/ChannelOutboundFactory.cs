using AxonHub.Abstractions;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Jina;
using AxonHub.Llm.Transformer.OpenAI.Decisions;
using AxonHub.Llm.Transformer.OpenAI.Responses;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Builds the outbound transformer for one candidate, the job the reference does
/// once per channel while loading it (internal/server/biz/channel_llm.go:
/// <c>buildChannel</c> → the channel-type switch).
/// </summary>
/// <remarks>
/// Only the <c>openai</c>, <c>anthropic</c> and <c>jina</c> channel types are
/// ported (the vertical slice); the other provider types are registered as a
/// scope cut. Key
/// selection follows getAPIKeyProvider: a single enabled key is static, several
/// enabled keys are chosen at random per attempt because the trace subsystem
/// (and therefore trace-sticky rendezvous selection) is not ported yet (C013).
/// </remarks>
public static class ChannelOutboundFactory
{
    public const string OpenAiChannelType = "openai";

    public const string AnthropicChannelType = "anthropic";

    public const string JinaChannelType = "jina";

    public static IOutboundTransformer Create(
        ChannelCandidate candidate,
        LlmRequest request,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(request);

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

        // The reference keys each channel's outbounds by API format (the channel
        // endpoint map). The slice's candidates carry one base URL, so dispatch
        // on the inbound API format instead: a Responses request to an openai
        // channel uses the Responses transformer, everything else the chat one.
        return candidate.Type switch
        {
            OpenAiChannelType when request.ApiFormat
                is ApiFormats.OpenAIResponse or ApiFormats.OpenAIResponseCompact
                => ResponsesOutboundTransformer.Create(
                    candidate.BaseUrl ?? string.Empty,
                    provider,
                    provider.KeyIndex),
            OpenAiChannelType when request.ApiFormat == ApiFormats.OpenAIDecisions
                => DecisionsOutboundTransformer.Create(
                    candidate.BaseUrl ?? string.Empty,
                    provider,
                    provider.KeyIndex),
            // channel_llm.go:445: the legacy /completions outbound shares the
            // openai channel's credentials but targets its own path.
            OpenAiChannelType when request.ApiFormat == ApiFormats.OpenAICompletion
                => CompletionOutboundTransformer.Create(
                    candidate.BaseUrl ?? string.Empty,
                    provider,
                    provider.KeyIndex),
            OpenAiChannelType => OpenAiOutboundTransformer.Create(
                candidate.BaseUrl ?? string.Empty,
                provider,
                provider.KeyIndex),
            AnthropicChannelType => AnthropicOutboundTransformer.Create(
                candidate.BaseUrl ?? string.Empty,
                provider,
                provider.KeyIndex),
            JinaChannelType => JinaOutboundTransformer.Create(
                candidate.BaseUrl ?? string.Empty,
                provider,
                provider.KeyIndex),
            _ => throw new InvalidOperationException(
                "unsupported channel type: " + candidate.Type + " for channel " + candidate.ChannelName),
        };
    }
}
