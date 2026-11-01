using System.Globalization;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.Anthropic.ClaudeCode;
using AxonHub.Llm.Transformer.Bailian;
using AxonHub.Llm.Transformer.Cerebras;
using AxonHub.Llm.Transformer.Cline;
using AxonHub.Llm.Transformer.DeepSeek;
using AxonHub.Llm.Transformer.Fireworks;
using AxonHub.Llm.Transformer.Longcat;
using AxonHub.Llm.Transformer.Minimax;
using AxonHub.Llm.Transformer.Moonshot;
using AxonHub.Llm.Transformer.Nanogpt;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenRouter;
using AxonHub.Llm.Transformer.Jina;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Llm.Transformer.OpenAI.Decisions;
using AxonHub.Llm.Transformer.Ollama;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Llm.Transformer.OpenCode;
using AxonHub.Llm.Transformer.TypeSafe;
using AxonHub.Llm.Transformer.Xai;
using AxonHub.Llm.Transformer.Zai;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Builds the outbound transformer for one candidate, the job the reference does
/// once per channel while loading it (internal/server/biz/channel_llm.go:
/// <c>buildChannel</c> → the channel-type switch).
/// </summary>
/// <remarks>
/// Only the <c>openai</c>, <c>anthropic</c>, <c>claudecode</c>,
/// <c>opencode_go</c>, <c>opencode_go_anthropic</c>, <c>jina</c>,
/// <c>typesafe</c>, <c>codex</c>, <c>fenno</c>, <c>deepseek</c>, <c>fireworks</c>,
/// <c>moonshot</c>, <c>longcat</c>, <c>cline</c>, <c>xai</c>, <c>openrouter</c>,
/// <c>cerebras</c>, <c>nanogpt</c>, <c>nanogpt_responses</c>, <c>ollama</c>,
/// <c>ollama_anthropic</c>, <c>zai</c>, <c>zhipu</c>, <c>zai_anthropic</c>,
/// <c>zhipu_anthropic</c>, <c>xiaomi</c>, <c>xiaomi_anthropic</c>,
/// <c>minimax</c>, <c>minimax_anthropic</c>, <c>bailian</c>,
/// <c>bailian_responses</c>, <c>bailian_anthropic</c> and <c>moonshot_coding</c>
/// channel types are ported (the vertical slice); the
/// other provider types are registered as a scope cut. Key
/// selection follows getAPIKeyProvider: a single enabled key is static, several
/// enabled keys are picked by rendezvous hashing when the request carries a
/// trace id and at random otherwise (C013).
/// </remarks>
public static class ChannelOutboundFactory
{
    public const string OpenAiChannelType = "openai";

    public const string AnthropicChannelType = "anthropic";

    public const string JinaChannelType = "jina";

    public const string TypeSafeChannelType = "typesafe";

    public const string CodexChannelType = "codex";

    public const string FennoChannelType = "fenno";

    public const string ClaudeCodeChannelType = "claudecode";

    public const string OpenCodeGoChannelType = "opencode_go";

    public const string OpenCodeGoAnthropicChannelType = "opencode_go_anthropic";

    public const string DeepSeekChannelType = "deepseek";

    public const string FireworksChannelType = "fireworks";

    public const string MoonshotChannelType = "moonshot";

    public const string LongcatChannelType = "longcat";

    public const string ClineChannelType = "cline";

    public const string XaiChannelType = "xai";

    public const string OpenRouterChannelType = "openrouter";

    public const string CerebrasChannelType = "cerebras";

    public const string NanogptChannelType = "nanogpt";

    public const string NanogptResponsesChannelType = "nanogpt_responses";

    public const string OllamaChannelType = "ollama";

    public const string OllamaAnthropicChannelType = "ollama_anthropic";

    public const string ZaiChannelType = "zai";

    public const string ZhipuChannelType = "zhipu";

    public const string ZaiAnthropicChannelType = "zai_anthropic";

    public const string ZhipuAnthropicChannelType = "zhipu_anthropic";

    public const string XiaomiChannelType = "xiaomi";

    public const string XiaomiAnthropicChannelType = "xiaomi_anthropic";

    public const string MinimaxChannelType = "minimax";

    public const string MinimaxAnthropicChannelType = "minimax_anthropic";

    public const string BailianChannelType = "bailian";

    public const string BailianResponsesChannelType = "bailian_responses";

    public const string BailianAnthropicChannelType = "bailian_anthropic";

    public const string MoonshotCodingChannelType = "moonshot_coding";

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

        var oauthCapableChannel = candidate.Type is CodexChannelType
            or FennoChannelType
            or ClaudeCodeChannelType;

        // channel_llm.go:662-665: codex and claudecode channels accept OAuth
        // credentials in place of api keys; every other channel type still
        // needs a key.
        var oauth = oauthCapableChannel && ChannelCredentials.IsOAuth(candidate.Credentials);

        // channel_llm.go:677-680: Ollama runs locally without a key; a stored
        // key is optional and disabled keys still mean no auth.
        var keylessChannel = candidate.Type is OllamaChannelType or OllamaAnthropicChannelType;

        if (enabledKeys.Count == 0 && !oauth && !keylessChannel)
        {
            throw new InvalidOperationException(
                oauthCapableChannel
                    ? "missing credentials: oauth or api key required for channel " + candidate.ChannelName
                    : "missing api key for channel " + candidate.ChannelName);
        }

        // WithTrace publishes the resolved trace id as the shared session id
        // (shared.WithSessionID), which the endpoint threads onto the wire
        // request; the reference provider reads it back from the request context
        // with contexts.GetTrace. No trace id means the untraced random branch.
        var provider = new ChannelApiKeyProvider(
            enabledKeys,
            ChannelCredentials.GetAllApiKeys(candidate.Credentials),
            request.RawRequest?.SessionId);

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
            // channel_llm.go:924-989: OAuth channels get a JSON-refreshing
            // Claude token provider; plain key channels rotate through the
            // per-attempt key provider.
            ClaudeCodeChannelType => CreateClaudeCodeOutbound(
                candidate,
                provider,
                tokenRefreshExecutor),
            // channel_llm.go:1183-1194: one outbound dispatching by model family.
            OpenCodeGoChannelType => new OpenCodeOutboundTransformer(new OpenCodeOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1170-1181: the anthropic outbound plus OpenCode
            // session affinity.
            OpenCodeGoAnthropicChannelType => OpenCodeSessionHeader.WithSessionHeader(
                AnthropicOutboundTransformer.Create(
                    candidate.BaseUrl ?? string.Empty,
                    provider,
                    provider.KeyIndex))!,
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
            // channel_llm.go:819-829: DeepSeek is OpenAI-compatible but owns the
            // /beta/completions route, json_schema downgrade and thinking field.
            DeepSeekChannelType => new DeepSeekOutboundTransformer(new DeepSeekOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:729-739: OpenAI-compatible; the provider package
            // supplies the default inference base URL and reasoning_content.
            FireworksChannelType => FireworksOutboundTransformer.Create(new FireworksOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:741-751: OpenAI-compatible with the Cline
            // {success,data,error,errors} envelope; the default endpoint set is
            // chat only (channel_endpoint.go:238).
            ClineChannelType => new ClineOutboundTransformer(new ClineOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:753-763: OpenRouter is OpenAI-compatible with a
            // top-level reasoning field and its own /images route; the default
            // endpoint set is chat plus speech/transcription/translation
            // (channel_endpoint.go:284-294).
            OpenRouterChannelType => new OpenRouterOutboundTransformer(new OpenRouterOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:764-774: Cerebras wraps the OpenRouter outbound and
            // strips the unsupported `store` field; the default endpoint set is
            // chat only (channel_endpoint.go:324).
            CerebrasChannelType => new CerebrasOutboundTransformer(new CerebrasOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:775-785: the OpenAI outbound plus top-level
            // reasoning and XML tool-call extraction; the default endpoint set
            // is the full OpenAI one (channel_endpoint.go:326).
            NanogptChannelType => new NanogptOutboundTransformer(new NanogptOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:789-799: the nanoGPT responses channel has exactly
            // one endpoint, the responses API, served by the shared Responses
            // outbound (channel_endpoint.go:327).
            NanogptResponsesChannelType => ResponsesOutboundTransformer.Create(
                candidate.BaseUrl ?? string.Empty,
                provider,
                provider.KeyIndex),
            // channel_llm.go:831-841: OpenAI-compatible with a custom request
            // path (chat only, json_schema downgraded to json_object).
            MoonshotChannelType => new MoonshotOutboundTransformer(new MoonshotOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:843-853: xAI is the OpenAI outbound with
            // reasoning_content and an empty-delta stream filter. The channel
            // also owns a peer responses endpoint (channel_endpoint.go:292-296),
            // served by the Responses transformer (channel_llm.go:437-455).
            XaiChannelType when request.ApiFormat is ApiFormats.OpenAIResponse
                or ApiFormats.OpenAIResponseCompact
                => ResponsesOutboundTransformer.Create(
                    candidate.BaseUrl ?? string.Empty,
                    provider,
                    provider.KeyIndex),
            XaiChannelType => new XaiOutboundTransformer(new XaiOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1121-1131: the OpenAI outbound plus the forced
            // array content required by LongCat models.
            LongcatChannelType => new LongcatOutboundTransformer(new LongcatOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1392-1405: the provider is only installed when the
            // channel has enabled keys, so a keyless or fully-disabled channel
            // sends no Authorization header at all.
            OllamaChannelType => new OllamaOutboundTransformer(new OllamaOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = enabledKeys.Count > 0 ? provider : null,
                ChannelApiKeyIndex = enabledKeys.Count > 0 ? provider.KeyIndex : null,
            }),
            // channel_llm.go:1406-1428: Anthropic Messages wire format against
            // Ollama, with Bearer auth instead of x-api-key.
            OllamaAnthropicChannelType => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.Ollama,
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = enabledKeys.Count > 0 ? provider : null,
                ChannelApiKeyIndex = enabledKeys.Count > 0 ? provider.KeyIndex : null,
            }),
            // channel_llm.go:582-589: zai/zhipu chat runs through the Zai
            // outbound (GLM thinking ladder, user_id rules) on the v4 route.
            ZaiChannelType or ZhipuChannelType => CreateZaiOutbound(candidate, request, provider, "v4"),
            // channel_llm.go:590-595: Xiaomi keeps the family request handling
            // on its v1 route (providerChatEndpointUsesFamilyVersion).
            XiaomiChannelType => CreateZaiOutbound(candidate, request, provider, "v1"),
            // channel_llm.go:1029-1052: the *Anthropic channel types serve
            // anthropic/messages through the anthropic outbound and, for a
            // chat-format endpoint, the family chat outbound.
            ZaiAnthropicChannelType when request.ApiFormat == ApiFormats.AnthropicMessage
                => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = AnthropicPlatformType.Zai,
                    BaseUrl = candidate.BaseUrl ?? string.Empty,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            ZaiAnthropicChannelType => CreateZaiOutbound(candidate, request, provider, "v4"),
            ZhipuAnthropicChannelType when request.ApiFormat == ApiFormats.AnthropicMessage
                => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = AnthropicPlatformType.Zhipu,
                    BaseUrl = candidate.BaseUrl ?? string.Empty,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            ZhipuAnthropicChannelType => CreateZaiOutbound(candidate, request, provider, "v4"),
            // channel_llm.go:898: xiaomi_anthropic and minimax_anthropic use the
            // plain Anthropic direct platform.
            XiaomiAnthropicChannelType when request.ApiFormat == ApiFormats.AnthropicMessage
                => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = AnthropicPlatformType.Direct,
                    BaseUrl = candidate.BaseUrl ?? string.Empty,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            XiaomiAnthropicChannelType => CreateZaiOutbound(candidate, request, provider, "v1"),
            // channel_llm.go:1303-1315: MiniMax dispatches images to its own
            // /image_generation API and delegates chat to the OpenAI outbound.
            MinimaxChannelType => new MinimaxOutboundTransformer(new MinimaxOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            MinimaxAnthropicChannelType when request.ApiFormat == ApiFormats.AnthropicMessage
                => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = AnthropicPlatformType.Direct,
                    BaseUrl = candidate.BaseUrl ?? string.Empty,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            MinimaxAnthropicChannelType => new MinimaxOutboundTransformer(new MinimaxOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1133-1143: DashScope's OpenAI-compatible outbound
            // with the tool-call merge, enable_thinking mapping and stream
            // filter; the default endpoint set is chat only
            // (channel_endpoint.go:313).
            BailianChannelType => new BailianOutboundTransformer(new BailianOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1145-1155: the Bailian responses channel has
            // exactly one endpoint (channel_endpoint.go:314), served by the
            // shared Responses outbound.
            BailianResponsesChannelType => ResponsesOutboundTransformer.Create(
                candidate.BaseUrl ?? string.Empty,
                provider,
                provider.KeyIndex),
            // channel_llm.go:1157-1167: bailian_anthropic and moonshot_coding
            // share the plain Anthropic direct outbound.
            BailianAnthropicChannelType or MoonshotCodingChannelType
                => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = AnthropicPlatformType.Direct,
                    BaseUrl = candidate.BaseUrl ?? string.Empty,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            _ => throw new InvalidOperationException(
                "unsupported channel type: " + candidate.Type + " for channel " + candidate.ChannelName),
        };
    }

    /// <summary>
    /// Port of the zai/xiaomi branch of <c>newProviderChatOutbound</c>
    /// (channel_llm.go:575-597). The request_id fallback reads the shared
    /// session id, which the endpoint threads onto the wire request (C062).
    /// </summary>
    private static ZaiOutboundTransformer CreateZaiOutbound(
        ChannelCandidate candidate,
        LlmRequest request,
        ChannelApiKeyProvider apiKeyProvider,
        string version)
    {
        return new ZaiOutboundTransformer(new ZaiOutboundConfig
        {
            BaseUrl = candidate.BaseUrl ?? string.Empty,
            Version = version,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
            SessionIdProvider = () => request.RawRequest?.SessionId,
        });
    }

    /// <summary>Port of the claudecode branch of buildChannelWithTransformer.</summary>
    private static ClaudeCodeOutboundTransformer CreateClaudeCodeOutbound(
        ChannelCandidate candidate,
        ChannelApiKeyProvider apiKeyProvider,
        IExecutor? tokenRefreshExecutor)
    {
        var baseUrl = candidate.BaseUrl ?? string.Empty;
        // strconv.Itoa(c.ID): the stable identity behind the deterministic
        // fake user id.
        var accountIdentity = candidate.ChannelId.ToString(CultureInfo.InvariantCulture);

        if (!ChannelCredentials.IsOAuth(candidate.Credentials))
        {
            // Non-OAuth: use the API key provider for multi-key rotation.
            return new ClaudeCodeOutboundTransformer(new ClaudeCodeParams
            {
                TokenProvider = new ApiKeyTokenProvider(_ => apiKeyProvider.Get()),
                BaseUrl = baseUrl,
                IsOfficial = false,
                AccountIdentity = accountIdentity,
            });
        }

        var credentials = ParseOAuthCredentials(candidate, "claudecode");

        return new ClaudeCodeOutboundTransformer(new ClaudeCodeParams
        {
            TokenProvider = ClaudeCodeTokenProvider.Create(new TokenProviderParams
            {
                Credentials = credentials,
                HttpClient = tokenRefreshExecutor,
            }),
            BaseUrl = baseUrl,
            IsOfficial = true,
            AccountIdentity = accountIdentity,
        });
    }

    /// <summary>
    /// Re-marshals the channel's typed <c>oauth</c> object (canonical field
    /// order, empty fields omitted), falling back to parsing the legacy apiKey
    /// string as OAuth JSON, then parses the credentials document.
    /// </summary>
    private static OAuthCredentials ParseOAuthCredentials(
        ChannelCandidate candidate,
        string channelType)
    {
        var oauthJson = ChannelCredentials.GetOAuthObjectJson(candidate.Credentials);
        var credentialsJson = oauthJson is not null
            ? OAuthCredentialsJson.ParseOAuthObject(oauthJson).ToJson()
            : ChannelCredentials.GetLegacyApiKey(candidate.Credentials);

        try
        {
            return OAuthCredentialsJson.ParseCredentialsJson(credentialsJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to parse " + channelType + " oauth credentials: " + ex.Message,
                ex);
        }
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

        var credentials = ParseOAuthCredentials(candidate, "codex");

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
