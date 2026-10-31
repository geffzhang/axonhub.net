using AxonHub.Llm.Http;
using AxonHub.Llm.Json;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>Port of openai.PlatformType (llm/transformer/openai/outbound.go:22-28).</summary>
public enum OpenAiPlatformType
{
    OpenAi = 0,
    Google = 1,
}

/// <summary>Port of openai.Config.</summary>
public sealed class OpenAiOutboundConfig
{
    public OpenAiPlatformType PlatformType { get; set; } = OpenAiPlatformType.OpenAi;

    public string BaseUrl { get; set; } = string.Empty;

    public bool RawUrl { get; set; }

    public string EndpointPath { get; set; } = string.Empty;

    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>
    /// Port-only audit field: 1-based position of the key the provider serves in
    /// the channel's full key list, or null when the channel has a single key.
    /// Persisted on request_executions.channel_api_key_index.
    /// </summary>
    public int? ChannelApiKeyIndex { get; set; }

    /// <summary>Null models the reference's unset config: Content is used.</summary>
    public ReasoningField? ReasoningField { get; set; }
}

/// <summary>Port of the APIKeyProvider seam (llm/auth).</summary>
public interface IApiKeyProvider
{
    string Get();
}

/// <summary>Port of auth.StaticKeyProvider.</summary>
public sealed class StaticApiKeyProvider(string apiKey) : IApiKeyProvider
{
    public string Get() => apiKey;
}

/// <summary>
/// Audit seam for the request-execution rows: the channel credential an outbound
/// transformer actually put on the wire.
/// </summary>
public interface IApiKeyProvenance
{
    /// <summary>The key sent by the most recent <c>TransformRequest</c>, or null before it runs.</summary>
    string? SelectedApiKey { get; }

    /// <summary>1-based index over the channel's full key list, or null when unambiguous.</summary>
    int? SelectedApiKeyIndex { get; }
}

/// <summary>Port of llm/transformer/openai/outbound.go (chat path).</summary>
public sealed partial class OpenAiOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    private readonly OpenAiOutboundConfig _config;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    public OpenAiOutboundTransformer(OpenAiOutboundConfig config)
    {
        ValidateConfig(config);
        _config = config;

        if (_config.BaseUrl.EndsWith("##", StringComparison.Ordinal))
        {
            _config.RawUrl = true;
            _config.BaseUrl = _config.BaseUrl[..^2];
        }
        else if (!_config.RawUrl)
        {
            _config.BaseUrl = HttpClientUtils.NormalizeBaseUrl(
                _config.BaseUrl,
                _config.EndpointPath.Length > 0 ? string.Empty : "v1");
        }
    }

    /// <summary>Port of NewOutboundTransformer(baseURL, apiKey).</summary>
    public static OpenAiOutboundTransformer Create(string baseUrl, string apiKey)
        => Create(baseUrl, new StaticApiKeyProvider(apiKey));

    /// <summary>
    /// Builds a transformer with a key provider and its audit index
    /// (the reference's getAPIKeyProvider + channel_api_key_index row).
    /// </summary>
    public static OpenAiOutboundTransformer Create(
        string baseUrl,
        IApiKeyProvider apiKeyProvider,
        int? channelApiKeyIndex = null)
        => new(new OpenAiOutboundConfig
        {
            PlatformType = OpenAiPlatformType.OpenAi,
            BaseUrl = baseUrl,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = channelApiKeyIndex,
        });

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    public string ApiFormat => ApiFormats.OpenAIChatCompletion;

    public string BaseUrl => _config.BaseUrl;

    public OpenAiOutboundConfig Config => _config;

    private static void ValidateConfig(OpenAiOutboundConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.ApiKeyProvider is null)
        {
            throw new ArgumentException("invalid OpenAI transformer configuration: API key provider is required", nameof(config));
        }

        if (string.IsNullOrEmpty(config.BaseUrl))
        {
            throw new ArgumentException("invalid OpenAI transformer configuration: base URL is required", nameof(config));
        }

        if (config.PlatformType is not (OpenAiPlatformType.OpenAi or OpenAiPlatformType.Google))
        {
            throw new ArgumentException(
                "invalid OpenAI transformer configuration: unsupported platform type: " + config.PlatformType,
                nameof(config));
        }
    }

    /// <summary>Port of TransformRequest: unified request to the upstream HTTP request.</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("chat completion request is nil");
        }

        if (string.IsNullOrEmpty(request.Model))
        {
            throw new InvalidOperationException("model is required");
        }

        // Type-specific outbound paths; the remaining request types are out of
        // scope for this slice.
        switch (request.RequestType)
        {
            case RequestTypes.Embedding:
                return BuildEmbeddingApiRequest(request);
            case RequestTypes.Moderation:
                return BuildModerationApiRequest(request);
            case RequestTypes.AlphaSearch:
                return BuildAlphaSearchApiRequest(request);
            case RequestTypes.Image:
                return BuildImageGenerationApiRequest(request);
            case RequestTypes.Speech:
                return BuildSpeechRequest(request);
            case RequestTypes.Transcription:
                return BuildTranscriptionRequest(request);
            case RequestTypes.Translation:
                return BuildTranslationRequest(request);
            case null:
            case "":
            case RequestTypes.Chat:
                break;
            default:
                throw TransformerErrors.InvalidRequest(
                    request.RequestType + " is not supported by this build of the OpenAI outbound transformer");
        }

        if (request.Messages is null || request.Messages.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("messages are required");
        }

        ValidateChatDocumentParts(request.Messages);

        // reasoning_content is the standard field used by most providers; channels
        // that need to strip reasoning set ReasoningFieldNone explicitly.
        var reasoningField = _config.ReasoningField ?? Transformer.OpenAI.ReasoningField.Content;

        var oaiReq = request.RequestFromLlm(reasoningField);

        if (_config.PlatformType == OpenAiPlatformType.OpenAi)
        {
            OpenAiConvertFromLlm.StripUnsupportedToolCallExtraContent(oaiReq);
        }

        var body = GoJson.Marshal(oaiReq);

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        var authConfig = new AuthConfig
        {
            Type = AuthTypes.Bearer,
            ApiKey = apiKey,
        };

        var url = BuildFullRequestUrl();

        return new HttpRequestData
        {
            Method = "POST",
            Url = url,
            Headers = headers,
            Body = body,
            Auth = authConfig,
            ApiFormat = ApiFormats.OpenAIChatCompletion,
        };
    }

    /// <summary>Port of TransformResponse (chat path).</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("http response is nil");
        }

        // Alpha Search owns its error conversion; the reference dispatches here
        // before the generic status/empty-body checks so provider error details
        // survive into the client response.
        if (response.Request is not null
            && string.Equals(response.Request.ApiFormat, ApiFormats.OpenAIAlphaSearch, StringComparison.Ordinal))
        {
            return TransformAlphaSearchResponse(response);
        }

        if (response.StatusCode >= 400)
        {
            throw new InvalidOperationException("HTTP error " + response.StatusCode);
        }

        if (response.Body is null || response.Body.Length == 0)
        {
            throw new InvalidOperationException("response body is empty");
        }

        // Route to specialized transformers based on request APIFormat.
        if (response.Request is not null && response.Request.ApiFormat.Length > 0)
        {
            switch (response.Request.ApiFormat)
            {
                case ApiFormats.OpenAIImageGeneration:
                case ApiFormats.OpenAIImageEdit:
                case ApiFormats.OpenAIImageVariation:
                    return TransformImageGenerationResponse(response);
                case ApiFormats.OpenAIEmbedding:
                    return TransformEmbeddingResponse(response);
                case ApiFormats.OpenAIModeration:
                    return TransformModerationResponse(response);
                case ApiFormats.OpenAISpeech:
                    return TransformSpeechResponse(response);
                case ApiFormats.OpenAITranscription:
                case ApiFormats.OpenAITranslation:
                    return TransformTranscriptionResponse(response, response.Request.ApiFormat);
            }
        }

        // Go validates the whole document before decoding, so syntax errors are
        // always reported with encoding/json's own wording.
        var syntaxError = GoJsonSyntax.Validate(response.Body);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal chat completion response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(response.Body);
        if (rootKind is not null)
        {
            // null leaves the zero struct in place; other roots are a type error.
            return rootKind == "null"
                ? new LlmResponse()
                : throw new InvalidOperationException(
                    "failed to unmarshal chat completion response: "
                    + GoStructJson.TypeMismatch(rootKind, "openai.Response"));
        }

        OpenAiResponse? oaiResp;
        try
        {
            oaiResp = LlmJson.Deserialize<OpenAiResponse>(response.Body);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal chat completion response: " + OpenAiInboundTransformer.JsonErrorText(ex), ex);
        }

        return oaiResp?.ToLlmResponse()
            ?? throw new InvalidOperationException("failed to unmarshal chat completion response: unexpected null");
    }

    /// <summary>Port of buildFullRequestURL.</summary>
    public string BuildFullRequestUrl()
    {
        if (_config.RawUrl)
        {
            return _config.BaseUrl;
        }

        if (_config.EndpointPath.Length > 0)
        {
            return _config.BaseUrl + _config.EndpointPath;
        }

        return _config.BaseUrl + "/chat/completions";
    }

    /// <summary>Port of validateChatDocumentParts.</summary>
    public static void ValidateChatDocumentParts(List<Message> messages)
    {
        foreach (var message in messages)
        {
            if (message.Content?.MultipleContent is null)
            {
                continue;
            }

            foreach (var part in message.Content.MultipleContent)
            {
                if (part.Type != "document" || part.Document is null)
                {
                    continue;
                }

                if (string.IsNullOrEmpty(part.Document.FileId)
                    && !string.IsNullOrEmpty(part.Document.Url)
                    && !part.Document.Url.StartsWith("data:", StringComparison.Ordinal))
                {
                    throw TransformerErrors.InvalidRequest(
                        "OpenAI Chat file inputs require file_id or a data URL in file_data");
                }
            }
        }
    }
}
