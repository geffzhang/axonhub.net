using System.Globalization;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.OAuth;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.Antigravity;
using AxonHub.Llm.Transformer.Anthropic.ClaudeCode;
using AxonHub.Llm.Transformer.Bailian;
using AxonHub.Llm.Transformer.Cerebras;
using AxonHub.Llm.Transformer.Cline;
using AxonHub.Llm.Transformer.DeepSeek;
using AxonHub.Llm.Transformer.Doubao;
using AxonHub.Llm.Transformer.Fireworks;
using AxonHub.Llm.Transformer.Gemini;
using AxonHub.Llm.Transformer.Gemini.OpenAI;
using AxonHub.Llm.Transformer.Longcat;
using AxonHub.Llm.Transformer.Minimax;
using AxonHub.Llm.Transformer.Modelscope;
using AxonHub.Llm.Transformer.Moonshot;
using AxonHub.Llm.Transformer.Nanogpt;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenRouter;
using AxonHub.Llm.Transformer.Jina;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Llm.Transformer.OpenAI.Copilot;
using AxonHub.Llm.Transformer.OpenAI.Decisions;
using AxonHub.Llm.Transformer.Ollama;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Llm.Transformer.OpenCode;
using AxonHub.Llm.Transformer.TypeSafe;
using AxonHub.Llm.Transformer.Xai;
using AxonHub.Llm.Transformer.Xai.Subscription;
using AxonHub.Llm.Transformer.Zai;
using AxonHub.Llm.Transformer.Zenmux;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Builds the outbound transformer for one candidate, the job the reference does
/// once per channel while loading it (internal/server/biz/channel_llm.go:
/// <c>buildChannel</c> → the channel-type switch).
/// </summary>
/// <remarks>
/// Only the <c>openai</c>, <c>openai_responses</c>, <c>anthropic</c>,
/// <c>commandcode</c>, <c>commandcode_anthropic</c>, <c>claudecode</c>,
/// <c>opencode_go</c>, <c>opencode_go_anthropic</c>, <c>jina</c>,
/// <c>typesafe</c>, <c>codex</c>, <c>fenno</c>, <c>deepseek</c>, <c>fireworks</c>,
/// <c>moonshot</c>, <c>longcat</c>, <c>cline</c>, <c>xai</c>, <c>openrouter</c>,
/// <c>cerebras</c>, <c>nanogpt</c>, <c>nanogpt_responses</c>, <c>ollama</c>,
/// <c>ollama_anthropic</c>, <c>zai</c>, <c>zhipu</c>, <c>zai_anthropic</c>,
/// <c>zhipu_anthropic</c>, <c>xiaomi</c>, <c>xiaomi_anthropic</c>,
/// <c>minimax</c>, <c>minimax_anthropic</c>, <c>bailian</c>,
/// <c>bailian_responses</c>, <c>bailian_anthropic</c>,
/// <c>moonshot_coding</c>, <c>modelscope</c>, <c>doubao</c>,
/// <c>volcengine</c>, <c>doubao_anthropic</c>, <c>volcengine_anthropic</c>,
/// <c>zenmux</c>, <c>zenmux_responses</c>, <c>zenmux_anthropic</c>,
/// <c>zenmux_video</c>, <c>github_copilot</c>, <c>gemini_openai</c>,
/// <c>gemini</c>, <c>zenmux_gemini</c>, <c>gemini_vertex</c>,
/// <c>antigravity</c>, <c>atlascloud</c>, <c>vercel</c>, <c>deepinfra</c>,
/// <c>qiniu</c>, <c>ppio</c>, <c>siliconflow</c>, <c>aihubmix</c>,
/// <c>burncloud</c>, <c>github</c>, <c>evolink</c>, <c>groq</c>,
/// <c>qiniu_anthropic</c>, <c>aihubmix_anthropic</c>, <c>evolink_anthropic</c>,
/// <c>longcat_anthropic</c>, <c>moonshot_anthropic</c>, <c>deepseek_anthropic</c>,
/// <c>anthropic_aws</c>, <c>xai_responses</c> and <c>xai_subscription</c>
/// channel types are ported (the vertical slice); the
/// other provider types are registered as a scope cut. Key
/// selection follows getAPIKeyProvider: a single enabled key is static, several
/// enabled keys are picked by rendezvous hashing when the request carries a
/// trace id and at random otherwise (C013).
/// </remarks>
public static class ChannelOutboundFactory
{
    public const string OpenAiChannelType = "openai";

    public const string OpenaiResponsesChannelType = "openai_responses";

    public const string AnthropicChannelType = "anthropic";

    public const string CommandcodeChannelType = "commandcode";

    public const string CommandcodeAnthropicChannelType = "commandcode_anthropic";

    public const string AntigravityChannelType = "antigravity";

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

    public const string ModelscopeChannelType = "modelscope";

    public const string DoubaoChannelType = "doubao";

    public const string VolcengineChannelType = "volcengine";

    public const string DoubaoAnthropicChannelType = "doubao_anthropic";

    public const string VolcengineAnthropicChannelType = "volcengine_anthropic";

    public const string ZenmuxChannelType = "zenmux";

    public const string ZenmuxResponsesChannelType = "zenmux_responses";

    public const string ZenmuxAnthropicChannelType = "zenmux_anthropic";

    public const string ZenmuxVideoChannelType = "zenmux_video";

    public const string GithubCopilotChannelType = "github_copilot";

    public const string GeminiOpenAiChannelType = "gemini_openai";

    public const string GeminiChannelType = "gemini";

    public const string GeminiVertexChannelType = "gemini_vertex";

    public const string ZenmuxGeminiChannelType = "zenmux_gemini";

    // channel_llm.go:1277-1280: OpenAI-compatible alias channels sharing the
    // plain OpenAI outbound (PlatformOpenAI).
    public const string AtlascloudChannelType = "atlascloud";

    public const string VercelChannelType = "vercel";

    public const string DeepinfraChannelType = "deepinfra";

    public const string QiniuChannelType = "qiniu";

    public const string PpioChannelType = "ppio";

    public const string SiliconflowChannelType = "siliconflow";

    public const string AihubmixChannelType = "aihubmix";

    public const string BurncloudChannelType = "burncloud";

    public const string GithubChannelType = "github";

    public const string EvolinkChannelType = "evolink";

    public const string GroqChannelType = "groq";

    // channel_llm.go:898, 885, 990, 1016, 1056: Anthropic-format aliases
    // (qiniu/aihubmix/evolink share the plain direct platform).
    public const string QiniuAnthropicChannelType = "qiniu_anthropic";

    public const string AihubmixAnthropicChannelType = "aihubmix_anthropic";

    public const string EvolinkAnthropicChannelType = "evolink_anthropic";

    public const string LongcatAnthropicChannelType = "longcat_anthropic";

    public const string MoonshotAnthropicChannelType = "moonshot_anthropic";

    public const string DeepseekAnthropicChannelType = "deepseek_anthropic";

    public const string AnthropicAwsChannelType = "anthropic_aws";

    // channel_llm.go:677-680, 1069-1085: anthropic_gcp authenticates with GCP
    // credentials and has no API keys.
    public const string AnthropicGcpChannelType = "anthropic_gcp";

    // channel_llm.go:855-863: the xAI responses channel has exactly one
    // endpoint, the responses API (channel_endpoint.go:296).
    public const string XaiResponsesChannelType = "xai_responses";

    // channel_llm.go:867-884: xai_subscription is OAuth-only and speaks the
    // Responses protocol through the Grok CLI endpoint with a refreshing token
    // provider.
    public const string XaiSubscriptionChannelType = "xai_subscription";

    // channel_llm.go:56-58: the default base URLs applied when a ZenMux
    // channel has none stored (channel_llm.go:695-705).
    public const string ZenmuxOpenAiBaseUrl = "https://zenmux.ai/api/v1";

    public const string ZenmuxAnthropicBaseUrl = "https://zenmux.ai/api/anthropic";

    public const string ZenmuxGeminiBaseUrl = "https://zenmux.ai/api/vertex-ai";

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
        //
        // channel_llm.go:666-669: github_copilot and xai_subscription are
        // OAuth-only; an api key without an oauth object is rejected with its
        // own message before the generic key check runs.
        var oauthRequiredChannel = candidate.Type
            is GithubCopilotChannelType or XaiSubscriptionChannelType;

        var isOAuth = ChannelCredentials.IsOAuth(candidate.Credentials);
        if (oauthRequiredChannel && !isOAuth)
        {
            throw new InvalidOperationException(
                "missing oauth credentials for channel " + candidate.ChannelName);
        }

        var oauth = (oauthCapableChannel || oauthRequiredChannel) && isOAuth;

        // channel_llm.go:677-682: Ollama runs locally without a key, and
        // anthropic_gcp uses the credentials' gcp object instead of API keys.
        var keylessChannel = candidate.Type is OllamaChannelType
            or OllamaAnthropicChannelType
            or AnthropicGcpChannelType;

        // channel_llm.go:670-674: antigravity consumes the single legacy APIKey
        // field (refreshToken|projectID) directly; enabled key lists do not
        // apply, so it leaves the generic key check to its own branch below.
        var antigravityChannel = candidate.Type == AntigravityChannelType;

        if (enabledKeys.Count == 0 && !oauth && !keylessChannel && !antigravityChannel)
        {
            throw new InvalidOperationException(
                oauthCapableChannel
                    ? "missing credentials: oauth or api key required for channel " + candidate.ChannelName
                    : "missing api key for channel " + candidate.ChannelName);
        }

        if (antigravityChannel
            && ChannelCredentials.GetLegacyApiKey(candidate.Credentials).Trim().Length == 0)
        {
            throw new InvalidOperationException(
                "missing api key for channel " + candidate.ChannelName);
        }

        // channel_llm.go:640-647, 705-711: both Command Code channel types
        // require an HTTPS base URL. The reference also hands them an
        // HTTPS-downgrade-rejecting client; that needs the per-channel client
        // seam the port does not have yet (C124).
        var commandCodeChannel = candidate.Type is CommandcodeChannelType or CommandcodeAnthropicChannelType;
        if (commandCodeChannel)
        {
            var parsed = Uri.TryCreate(
                (candidate.BaseUrl ?? string.Empty).Trim(),
                UriKind.Absolute,
                out var commandCodeUri);
            if (!parsed || commandCodeUri!.Scheme != Uri.UriSchemeHttps || commandCodeUri.Host.Length == 0)
            {
                throw new InvalidOperationException("Command Code base URL must use HTTPS");
            }
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
            // channel_llm.go:1195-1204: the commandcode channel's primary
            // outbound is the plain OpenAI chat outbound; the default endpoint
            // set is chat only (channel_endpoint.go:339). A custom
            // Anthropic-format endpoint would be served by the Command Code
            // anthropic outbound with Bearer auth (channel_llm.go:527-529),
            // but the slice has no endpoint records (C082). Credential
            // validation shares the generic enabled-keys check
            // (channel_llm.go:683-688 behaves like the default case once the
            // key-test override is absent; the port has no key-test flow,
            // C014). The reference's error wrap is unreachable after the
            // credential check.
            CommandcodeChannelType => OpenAiOutboundTransformer.Create(
                candidate.BaseUrl ?? string.Empty,
                provider,
                provider.KeyIndex),
            // channel_llm.go:911-918: commandcode_anthropic speaks Anthropic
            // Messages through the Command Code platform — Bearer auth instead
            // of X-API-Key, with Direct-like signature decoding and thinking
            // support (outbound.go:251, outbound_convert.go:96, thinking.go).
            // The default endpoint set is a single messages endpoint
            // (channel_endpoint.go:340).
            CommandcodeAnthropicChannelType => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.CommandCode,
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:855-863: the xAI responses channel has exactly
            // one endpoint, the responses API (channel_endpoint.go:296),
            // served by the shared Responses outbound.
            XaiResponsesChannelType => ResponsesOutboundTransformer.Create(
                candidate.BaseUrl ?? string.Empty,
                provider,
                provider.KeyIndex),
            // channel_llm.go:867-884: xai_subscription wraps the Responses
            // outbound with a refreshing OAuth token provider pinned to the
            // Grok CLI endpoints. The reference also registers the provider's
            // auto-refresh and an OnRefreshed persistence callback; the port's
            // channel grains run neither yet (C094/C127).
            XaiSubscriptionChannelType => CreateXaiSubscriptionOutbound(
                candidate,
                tokenRefreshExecutor),
            // channel_llm.go:885-893: longcat_anthropic speaks Anthropic
            // Messages through the LongCat platform (Bearer auth).
            LongcatAnthropicChannelType => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.LongCat,
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:898-909: qiniu/aihubmix/evolink anthropic variants
            // share the plain Anthropic direct platform (X-API-Key).
            QiniuAnthropicChannelType or AihubmixAnthropicChannelType or EvolinkAnthropicChannelType
                => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = AnthropicPlatformType.Direct,
                    BaseUrl = candidate.BaseUrl ?? string.Empty,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            // channel_llm.go:990-998: deepseek_anthropic uses the DeepSeek
            // platform (output_config support, no adaptive thinking).
            DeepseekAnthropicChannelType => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.DeepSeek,
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1016-1024: moonshot_anthropic uses the Moonshot
            // platform.
            MoonshotAnthropicChannelType => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.Moonshot,
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1056-1064: anthropic_aws speaks Anthropic Messages
            // through the Bedrock platform (Bearer auth, /model/{model}/invoke
            // wire envelope; the Vertex sibling stays a gap, C056).
            AnthropicAwsChannelType => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.Bedrock,
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1069-1085: anthropic_gcp authenticates with the
            // channel's GCP credentials and rewrites requests for Vertex AI.
            AnthropicGcpChannelType => CreateAnthropicGcpOutbound(candidate),
            // channel_llm.go:1277-1280: the OpenAI-compatible alias channels
            // share the plain OpenAI outbound (PlatformOpenAI). Their default
            // endpoint sets — openAICompatibleDefaultEndpoints
            // (channel_endpoint.go:189-197), qiniu's chat-only (:236) and
            // groq's chat + audio (:333-337) — are all formats the OpenAI
            // outbound serves internally, so no ApiFormat branching is needed;
            // formats outside the set error in the reference's endpoint
            // lookup (dispatch model difference C094).
            AtlascloudChannelType or VercelChannelType or DeepinfraChannelType
                or QiniuChannelType or PpioChannelType or SiliconflowChannelType
                or AihubmixChannelType or BurncloudChannelType or GithubChannelType
                or EvolinkChannelType or GroqChannelType
                => OpenAiOutboundTransformer.Create(
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
            // channel_llm.go:1313-1322: the openai_responses channel has exactly
            // one default endpoint, the responses API (channel_endpoint.go:233),
            // served by the shared Responses outbound whatever the inbound
            // format. The transport comes from the base URL's ws/wss scheme:
            // primaryEndpointTransport falls through to endpointTransport on
            // the channel base URL (channel_llm.go:276-292). The slice has no
            // endpoint records, so the scheme is the only source (C082). The
            // constructor only fails on a missing key provider, which the key
            // check above rules out, so the reference's error wrap is
            // unreachable here too.
            OpenaiResponsesChannelType => new ResponsesOutboundTransformer(new ResponsesOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
                Transport = InferTransport(candidate.BaseUrl ?? string.Empty),
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
            // channel_llm.go:1096-1108: chat plus the asynchronous image task
            // protocol (channel_endpoint.go:309-312). The reference passes the
            // channel's proxy-aware HTTP client; the port has no per-channel
            // proxy configuration yet, so the transformer's default client is
            // used (C104).
            ModelscopeChannelType => new ModelscopeOutboundTransformer(new ModelscopeOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:720-728: doubao/volcengine install the Doubao
            // outbound as the channel's primary transformer; every default
            // endpoint (chat plus seedance/video, channel_endpoint.go:271-275)
            // shares it.
            DoubaoChannelType or VolcengineChannelType
                => CreateDoubaoOutbound(candidate, provider),
            // channel_llm.go:1003-1015: doubao_anthropic speaks Anthropic
            // Messages through the Doubao platform; the family chat outbound
            // serves the other endpoints (channel_llm.go:598-604).
            DoubaoAnthropicChannelType when request.ApiFormat == ApiFormats.AnthropicMessage
                => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = AnthropicPlatformType.Doubao,
                    BaseUrl = candidate.BaseUrl ?? string.Empty,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            DoubaoAnthropicChannelType => CreateDoubaoOutbound(candidate, provider),
            // channel_llm.go:898: volcengine_anthropic uses the plain Anthropic
            // direct platform for Messages.
            VolcengineAnthropicChannelType when request.ApiFormat == ApiFormats.AnthropicMessage
                => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
                {
                    Type = AnthropicPlatformType.Direct,
                    BaseUrl = candidate.BaseUrl ?? string.Empty,
                    ApiKeyProvider = provider,
                    ChannelApiKeyIndex = provider.KeyIndex,
                }),
            VolcengineAnthropicChannelType => CreateDoubaoOutbound(candidate, provider),
            // channel_llm.go:511-520: a zenmux/video endpoint is built for
            // every ZenMux channel type, but only when the channel lists it
            // (channel_endpoint.go). The slice has no endpoint records, so the
            // persisted-format lookup reaches the native transformer directly
            // (C082).
            ZenmuxChannelType or ZenmuxResponsesChannelType
                or ZenmuxAnthropicChannelType or ZenmuxVideoChannelType
                when request.ApiFormat == ApiFormats.ZenmuxVideo
                => CreateZenmuxOutbound(candidate, provider),
            // channel_llm.go:1269-1279: a plain ZenMux channel's primary
            // outbound is the shared OpenAI one; a responses-format request is
            // served by the Responses outbound, like the openai channel type.
            ZenmuxChannelType when request.ApiFormat
                is ApiFormats.OpenAIResponse or ApiFormats.OpenAIResponseCompact
                => ResponsesOutboundTransformer.Create(
                    ZenmuxBaseUrl(candidate),
                    provider,
                    provider.KeyIndex),
            ZenmuxChannelType => OpenAiOutboundTransformer.Create(
                ZenmuxBaseUrl(candidate),
                provider,
                provider.KeyIndex),
            // channel_llm.go:789-799: zenmux_responses has exactly one
            // endpoint, the responses API (channel_endpoint.go:279-281).
            ZenmuxResponsesChannelType => ResponsesOutboundTransformer.Create(
                ZenmuxBaseUrl(candidate),
                provider,
                provider.KeyIndex),
            // channel_llm.go:898: zenmux_anthropic is the plain Anthropic
            // direct platform on the ZenMux Anthropic base URL.
            ZenmuxAnthropicChannelType => new AnthropicOutboundTransformer(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.Direct,
                BaseUrl = ZenmuxBaseUrl(candidate),
                ApiKeyProvider = provider,
                ChannelApiKeyIndex = provider.KeyIndex,
            }),
            // channel_llm.go:1293-1302: the zenmux_video channel's primary
            // outbound is the native transformer, which also serves chat
            // through its embedded OpenAI outbound.
            ZenmuxVideoChannelType => CreateZenmuxOutbound(candidate, provider),
            // channel_llm.go:1218-1276: github_copilot's transformer is
            // OAuth-driven and routes GPT-5+/Grok models to the Responses API
            // internally, so no ApiFormat branching is needed here
            // (channel_endpoint.go:322).
            GithubCopilotChannelType
                => CreateGithubCopilotOutbound(candidate, tokenRefreshExecutor),
            // channel_llm.go:1109-1118: Gemini's OpenAI-compatible endpoint;
            // the default endpoint set is chat only (channel_endpoint.go:253).
            GeminiOpenAiChannelType => CreateGeminiOpenAiOutbound(candidate, provider),
            // channel_llm.go:1327-1335: gemini and zenmux_gemini share the
            // native Gemini transformer; zenmux_gemini falls back to the
            // ZenMux Vertex base URL when the channel stores none
            // (channel_llm.go:695-705). The constructor cannot fail, so no
            // error wrapping is needed.
            GeminiChannelType or ZenmuxGeminiChannelType => new GeminiOutboundTransformer(new GeminiOutboundConfig
            {
                BaseUrl = candidate.Type == ZenmuxGeminiChannelType
                    ? ZenmuxBaseUrl(candidate)
                    : candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
            }),
            // channel_llm.go:1336-1346: the gemini_vertex channel pins the
            // Vertex platform (URL shape + function ID clearing).
            GeminiVertexChannelType => new GeminiOutboundTransformer(new GeminiOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = provider,
                PlatformType = GeminiConstants.PlatformVertex,
            }),
            // channel_llm.go:1375-1391: antigravity wraps the native Gemini
            // outbound in the project envelope and authenticates exclusively
            // through OAuth tokens minted from the legacy refreshToken|projectID
            // key (channel_endpoint.go:325: a single Gemini-contents endpoint).
            AntigravityChannelType => CreateAntigravityOutbound(candidate, tokenRefreshExecutor),
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

    /// <summary>
    /// Port of the doubao/volcengine branch of <c>newProviderChatOutbound</c>
    /// (channel_llm.go:598-604). The default chat endpoint passes an empty
    /// endpoint path, so the transformer normalizes the base URL to the Ark v3
    /// route. The reference hands the channel's proxy-aware HTTP client to the
    /// transformer; the port has no per-channel proxy configuration yet (C105).
    /// </summary>
    private static DoubaoOutboundTransformer CreateDoubaoOutbound(
        ChannelCandidate candidate,
        ChannelApiKeyProvider apiKeyProvider)
        => new(new DoubaoOutboundConfig
        {
            BaseUrl = candidate.BaseUrl ?? string.Empty,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
        });

    /// <summary>
    /// Port of the empty-base-URL defaulting in <c>buildChannelWithTransformer</c>
    /// (channel_llm.go:695-705). Only the ported ZenMux types carry a default.
    /// </summary>
    private static string ZenmuxBaseUrl(ChannelCandidate candidate)
    {
        var baseUrl = candidate.BaseUrl ?? string.Empty;
        if (baseUrl.Length != 0)
        {
            return baseUrl;
        }

        return candidate.Type switch
        {
            ZenmuxAnthropicChannelType => ZenmuxAnthropicBaseUrl,
            ZenmuxGeminiChannelType => ZenmuxGeminiBaseUrl,
            _ => ZenmuxOpenAiBaseUrl,
        };
    }

    /// <summary>
    /// Port of the ZenMux branches of <c>buildChannelWithTransformer</c>
    /// (channel_llm.go:1293-1302) and the zenmux/video case of
    /// <c>newProviderChatOutbound</c> (channel_llm.go:511-520). The endpoint
    /// path defaults to /videos; the slice has no endpoint records, so a
    /// custom path is not modelled yet (C082).
    /// </summary>
    private static ZenmuxOutboundTransformer CreateZenmuxOutbound(
        ChannelCandidate candidate,
        ChannelApiKeyProvider apiKeyProvider)
        => new(new ZenmuxOutboundConfig
        {
            BaseUrl = ZenmuxBaseUrl(candidate),
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
        });

    /// <summary>
    /// Port of the gemini_openai branch of <c>buildChannelWithTransformer</c>
    /// (channel_llm.go:1109-1118). The reference wraps the constructor error
    /// with <c>failed to create outbound transformer</c>, including the
    /// missing-base-URL validation.
    /// </summary>
    private static GeminiOpenAiOutboundTransformer CreateGeminiOpenAiOutbound(
        ChannelCandidate candidate,
        ChannelApiKeyProvider apiKeyProvider)
    {
        try
        {
            return new GeminiOpenAiOutboundTransformer(new GeminiOpenAiOutboundConfig
            {
                BaseUrl = candidate.BaseUrl ?? string.Empty,
                ApiKeyProvider = apiKeyProvider,
                ChannelApiKeyIndex = apiKeyProvider.KeyIndex,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create outbound transformer: " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Port of the anthropic_gcp branch of <c>buildChannelWithTransformer</c>
    /// (channel_llm.go:1069-1085): the reference wraps the constructor error
    /// with <c>failed to create outbound transformer</c>, and reports missing
    /// GCP credentials before calling it.
    /// </summary>
    private static IOutboundTransformer CreateAnthropicGcpOutbound(ChannelCandidate candidate)
    {
        var gcp = ChannelCredentials.GetGcpCredential(candidate.Credentials);
        if (gcp is null)
        {
            throw new InvalidOperationException(
                "GCP credentials are required for anthropic_vertex channel");
        }

        try
        {
            return AnthropicOutboundTransformer.CreateWithConfig(new AnthropicOutboundConfig
            {
                Type = AnthropicPlatformType.Vertex,
                Region = gcp.Region,
                ProjectId = gcp.ProjectId,
                JsonData = gcp.JsonData,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create outbound transformer: " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Port of the antigravity branch of <c>buildChannelWithTransformer</c>
    /// (channel_llm.go:1375-1391). The transformer consumes the single legacy
    /// APIKey field (refreshToken|projectID) directly. The reference registers
    /// <c>onTokenRefreshed</c> and <c>setupAutoRefresh</c> on the channel; the
    /// port's channels do not run refresh loops or write refreshed tokens back
    /// (C094).
    /// </summary>
    private static AntigravityTransformer CreateAntigravityOutbound(
        ChannelCandidate candidate,
        IExecutor? tokenRefreshExecutor)
    {
        try
        {
            return AntigravityTransformer.Create(
                new AntigravityConfig
                {
                    BaseURL = candidate.BaseUrl ?? string.Empty,
                    APIKey = ChannelCredentials.GetLegacyApiKey(candidate.Credentials),
                },
                new AntigravityTransformerOptions
                {
                    HttpClient = tokenRefreshExecutor,
                });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create antigravity outbound transformer: " + ex.Message,
                ex);
        }
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

    /// <summary>
    /// Port of the github_copilot branch of <c>buildChannelWithTransformer</c>
    /// (channel_llm.go:1218-1276). The typed <c>oauth</c> object is re-marshalled
    /// without <c>id_token</c>, parsed, and wrapped in the device-flow token
    /// provider that mints Copilot tokens. The reference also registers the
    /// provider's auto-refresh (channel_llm.go:1270-1275); the port's channel
    /// grains do not run refresh loops yet (C094).
    /// </summary>
    private static CopilotOutboundTransformer CreateGithubCopilotOutbound(
        ChannelCandidate candidate,
        IExecutor? tokenRefreshExecutor)
    {
        if (!ChannelCredentials.IsOAuth(candidate.Credentials))
        {
            throw new InvalidOperationException(
                "missing oauth credentials for channel " + candidate.ChannelName);
        }

        var credentialsJson = ChannelCredentials.GetLegacyApiKey(candidate.Credentials).Trim();
        if (credentialsJson.Length == 0)
        {
            throw new InvalidOperationException(
                "github_copilot channel " + candidate.ChannelName + " has no credentials");
        }

        var oauthJson = ChannelCredentials.GetOAuthObjectJson(candidate.Credentials);
        if (oauthJson is not null)
        {
            try
            {
                // Same shape as the reference's re-marshal: id_token is not
                // part of the Copilot credential document.
                var oauth = OAuthCredentialsJson.ParseOAuthObject(oauthJson);
                credentialsJson = new OAuthCredentials
                {
                    AccessToken = oauth.AccessToken,
                    RefreshToken = oauth.RefreshToken,
                    ClientId = oauth.ClientId,
                    ExpiresAt = oauth.ExpiresAt,
                    TokenType = oauth.TokenType,
                    Scopes = oauth.Scopes,
                }.ToJson();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "failed to encode github_copilot oauth credentials for channel "
                    + candidate.ChannelName + ": " + ex.Message,
                    ex);
            }
        }

        OAuthCredentials credentials;
        try
        {
            credentials = OAuthCredentialsJson.ParseCredentialsJson(credentialsJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "github_copilot channel " + candidate.ChannelName
                + " has invalid credentials: " + ex.Message,
                ex);
        }

        CopilotTokenProvider tokenProvider;
        try
        {
            tokenProvider = new CopilotTokenProvider(new CopilotTokenProviderParams
            {
                Credentials = credentials,
                HttpClient = tokenRefreshExecutor,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create CopilotTokenProvider: " + ex.Message,
                ex);
        }

        try
        {
            return new CopilotOutboundTransformer(new CopilotOutboundParams
            {
                TokenProvider = tokenProvider,
                BaseUrl = candidate.BaseUrl ?? string.Empty,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create github_copilot outbound transformer: " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Port of the xai_subscription branch of <c>buildChannelWithTransformer</c>
    /// (channel_llm.go:867-884): resolve the OAuth credentials, build the
    /// refreshing token provider over the xAI endpoints, and wrap the Responses
    /// outbound. The reference also registers the provider's auto-refresh and
    /// the <c>OnRefreshed</c> persistence callback; the port's channel grains
    /// run neither yet (C094/C127).
    /// </summary>
    private static SubscriptionOutboundTransformer CreateXaiSubscriptionOutbound(
        ChannelCandidate candidate,
        IExecutor? tokenRefreshExecutor)
    {
        OAuthCredentials credentials;
        try
        {
            credentials = ResolveOAuthCredentials(candidate);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "xAI subscription channel " + candidate.ChannelName
                + " has invalid credentials: " + ex.Message,
                ex);
        }

        var tokens = SubscriptionToken.NewTokenProvider(new SubscriptionTokenProviderParams
        {
            Credentials = credentials,
            HttpClient = tokenRefreshExecutor,
        });

        try
        {
            return SubscriptionOutboundTransformer.NewOutboundTransformer(tokens);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "failed to create xAI subscription outbound transformer: " + ex.Message,
                ex);
        }
    }

    /// <summary>
    /// Port of <c>(*ChannelCredentials).ResolveOAuthCredentials</c>
    /// (internal/objects/channel.go:460-468): a typed <c>oauth</c> object with a
    /// non-blank access token wins; otherwise the legacy <c>apiKey</c> string is
    /// parsed as the credentials document.
    /// </summary>
    private static OAuthCredentials ResolveOAuthCredentials(ChannelCandidate candidate)
    {
        var oauthJson = ChannelCredentials.GetOAuthObjectJson(candidate.Credentials);
        if (oauthJson is not null)
        {
            var oauth = OAuthCredentialsJson.ParseOAuthObject(oauthJson);
            if (oauth.AccessToken.Trim().Length > 0)
            {
                return oauth;
            }
        }

        return OAuthCredentialsJson.ParseCredentialsJson(
            ChannelCredentials.GetLegacyApiKey(candidate.Credentials));
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
