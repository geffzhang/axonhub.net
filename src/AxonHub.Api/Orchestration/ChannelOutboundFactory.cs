using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Jina;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Llm.Transformer.OpenAI.Decisions;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Llm.Transformer.TypeSafe;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Builds the outbound transformer for one candidate, the job the reference does
/// once per channel while loading it (internal/server/biz/channel_llm.go:
/// <c>buildChannel</c> → the channel-type switch).
/// </summary>
/// <remarks>
/// Only the <c>openai</c>, <c>anthropic</c>, <c>jina</c> and <c>typesafe</c>
/// channel types are ported (the vertical slice); the other provider types are
/// registered as a scope cut. Key
/// selection follows getAPIKeyProvider: a single enabled key is static, several
/// enabled keys are chosen at random per attempt because the trace subsystem
/// (and therefore trace-sticky rendezvous selection) is not ported yet (C013).
/// </remarks>
public static class ChannelOutboundFactory
{
    public const string OpenAiChannelType = "openai";

    public const string AnthropicChannelType = "anthropic";

    public const string JinaChannelType = "jina";

    public const string TypeSafeChannelType = "typesafe";

    public const string CodexChannelType = "codex";

    public const string FennoChannelType = "fenno";

    /// <param name="tokenRefreshExecutor">
    /// HTTP executor used by an OAuth codex channel's token refresher. The
    /// reference passes the channel's proxy-aware client; the port's channels
    /// have no proxy configuration yet, so the API's shared executor is used.
    /// </param>
    public static IOutboundTransformer Create(
        ChannelCandidate candidate,
        LlmRequest request,
        DateTimeOffset now,
        IExecutor? tokenRefreshExecutor = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(request);

        var enabledKeys = ChannelCredentials.GetEnabledApiKeys(
            candidate.Credentials,
            candidate.DisabledApiKeys,
            now);

        var codexChannel = candidate.Type is CodexChannelType or FennoChannelType;

        // channel_llm.go:662-665: codex channels accept OAuth credentials in
        // place of api keys; every other channel type still needs a key.
        var oauth = codexChannel && ChannelCredentials.IsOAuth(candidate.Credentials);

        if (enabledKeys.Count == 0 && !oauth)
        {
            throw new InvalidOperationException(
                codexChannel
                    ? "missing credentials: oauth or api key required for channel " + candidate.ChannelName
                    : "missing api key for channel " + candidate.ChannelName);
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
            // channel_llm.go:454/465/497: codex responses, alpha search and the
            // image tool all run through the codex outbound. Fenno shares the
            // responses and image branches but not alpha search.
            CodexChannelType when request.ApiFormat is ApiFormats.OpenAIResponse
                or ApiFormats.OpenAIAlphaSearch
                or ApiFormats.OpenAIImageGeneration
                or ApiFormats.OpenAIImageEdit
                => CreateCodexOutbound(candidate, request, provider, tokenRefreshExecutor),
            FennoChannelType when request.ApiFormat is ApiFormats.OpenAIResponse
                or ApiFormats.OpenAIImageGeneration
                or ApiFormats.OpenAIImageEdit
                => CreateCodexOutbound(candidate, request, provider, tokenRefreshExecutor),
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
            // channel_endpoint.go:342: a typesafe channel has exactly one
            // endpoint (typesafe/systemone) and channel_llm.go routes every
            // request type through the same outbound.
            TypeSafeChannelType => TypeSafeOutboundTransformer.Create(
                candidate.BaseUrl ?? string.Empty,
                provider,
                provider.KeyIndex),
            _ => throw new InvalidOperationException(
                "unsupported channel type: " + candidate.Type + " for channel " + candidate.ChannelName),
        };
    }

    /// <summary>Port of <c>buildCodexOutbound</c> (channel_llm.go:296-387).</summary>
    private static CodexOutboundTransformer CreateCodexOutbound(
        ChannelCandidate candidate,
        LlmRequest request,
        ChannelApiKeyProvider apiKeyProvider,
        IExecutor? tokenRefreshExecutor)
    {
        var baseUrl = candidate.BaseUrl ?? string.Empty;

        return new CodexOutboundTransformer(new CodexOutboundParams
        {
            TokenProvider = CreateCodexTokenProvider(candidate, apiKeyProvider, tokenRefreshExecutor),
            BaseUrl = baseUrl,
            // channel_llm.go:263-274 (endpointTransport): an endpoint without an
            // explicit transport infers websocket from its ws/wss scheme. The
            // slice has no endpoint records, so the scheme is the only source.
            Transport = InferTransport(baseUrl),
            // The reference passes the alpha-search endpoint's path here; the
            // slice has no endpoint records, so the transformer's default
            // (/alpha/search) applies.
            AlphaSearchPath = string.Empty,
            ImageMainModel = candidate.DefaultTestModel,
            // The reference reads the trace session from the request context;
            // the port threads the captured session through the wire request
            // (C062). Both are null until the inbound layer captures one.
            SessionIdProvider = () => request.RawRequest?.SessionId,
            SessionScopeProvider = () => request.RawRequest?.SessionScope,
        });
    }

    /// <summary>
    /// Port of the credential half of buildCodexOutbound: an OAuth channel gets a
    /// refreshing token provider seeded from the channel's credentials, a plain
    /// key channel wraps the per-attempt key selection.
    /// </summary>
    private static ITokenGetter CreateCodexTokenProvider(
        ChannelCandidate candidate,
        ChannelApiKeyProvider apiKeyProvider,
        IExecutor? tokenRefreshExecutor)
    {
        if (!ChannelCredentials.IsOAuth(candidate.Credentials))
        {
            return new ApiKeyTokenProvider(_ => apiKeyProvider.Get());
        }

        // buildCodexOutbound re-marshals a typed `oauth` object (canonical field
        // order, empty fields omitted) and falls back to parsing the legacy
        // apiKey string as OAuth JSON.
        var oauthJson = ChannelCredentials.GetOAuthObjectJson(candidate.Credentials);
        var credentialsJson = oauthJson is not null
            ? OAuthCredentialsJson.ParseOAuthObject(oauthJson).ToJson()
            : ChannelCredentials.GetLegacyApiKey(candidate.Credentials);

        OAuthCredentials credentials;
        try
        {
            credentials = OAuthCredentialsJson.ParseCredentialsJson(credentialsJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to parse codex oauth credentials: " + ex.Message,
                ex);
        }

        return new TokenProvider(new TokenProviderParams
        {
            Credentials = credentials,
            HttpClient = tokenRefreshExecutor,
        });
    }

    private static string InferTransport(string baseUrl)
    {
        var normalized = baseUrl.Trim().ToLowerInvariant();
        return normalized.StartsWith("ws://", StringComparison.Ordinal)
            || normalized.StartsWith("wss://", StringComparison.Ordinal)
            ? ResponsesOutboundTransformer.TransportWebSocket
            : string.Empty;
    }
}
