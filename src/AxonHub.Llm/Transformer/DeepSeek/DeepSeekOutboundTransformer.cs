using System.Text.Json.Nodes;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.DeepSeek;

/// <summary>Port of deepseek.Config (llm/transformer/deepseek/outbound.go:19-22).</summary>
public sealed class DeepSeekOutboundConfig
{
    /// <summary>The base URL for the DeepSeek API, required.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Provides API keys for authentication, required.</summary>
    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>
    /// Port-only audit field: 1-based position of the key the provider serves in
    /// the channel's full key list, or null when the channel has a single key.
    /// </summary>
    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of the DeepSeek outbound transformer (llm/transformer/deepseek):
/// an OpenAI chat transformer that additionally emits DeepSeek's
/// <c>thinking</c> envelope and routes legacy /completions to the beta path.
/// </summary>
public sealed class DeepSeekOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    private readonly DeepSeekOutboundConfig _config;
    private readonly string _baseUrl;
    private readonly OpenAiOutboundTransformer _chat;
    private readonly CompletionOutboundTransformer _completion;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    /// <summary>Port of NewOutboundTransformerWithConfig.</summary>
    public DeepSeekOutboundTransformer(DeepSeekOutboundConfig config)
    {
        if (config is null)
        {
            // The reference passes nil into the OpenAI validator, whose error is
            // wrapped by the DeepSeek constructor.
            throw new InvalidOperationException(
                "invalid DeepSeek transformer configuration: config cannot be nil");
        }

        _config = config;

        try
        {
            _chat = new OpenAiOutboundTransformer(new OpenAiOutboundConfig
            {
                PlatformType = OpenAiPlatformType.OpenAi,
                BaseUrl = config.BaseUrl,
                ApiKeyProvider = config.ApiKeyProvider,
                ReasoningField = ReasoningField.Content,
                ChannelApiKeyIndex = config.ChannelApiKeyIndex,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "invalid DeepSeek transformer configuration: " + ex.Message,
                ex);
        }

        _baseUrl = HttpClientUtils.NormalizeBaseUrl(config.BaseUrl, "v1");

        try
        {
            // deepseek's legacy completion endpoint lives under /beta: the "#"
            // marker strips the /v1 normalization inside the constructor.
            _completion = new CompletionOutboundTransformer(new OpenAiOutboundConfig
            {
                BaseUrl = TrimSuffix(_baseUrl, "/v1") + "/beta#",
                ApiKeyProvider = config.ApiKeyProvider,
                ChannelApiKeyIndex = config.ChannelApiKeyIndex,
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidOperationException(
                "invalid DeepSeek completion transformer configuration: " + ex.Message,
                ex);
        }
    }

    public string ApiFormat => ApiFormats.OpenAIChatCompletion;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>Port of TransformRequest.</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("chat completion request is nil");
        }

        switch (request.RequestType)
        {
            case null or "" or RequestTypes.Chat:
                break;
            case RequestTypes.Completion:
                var completionRequest = _completion.TransformRequest(request);
                _selectedApiKey = _completion.SelectedApiKey;
                return completionRequest;
            case RequestTypes.Compact:
                throw TransformerErrors.InvalidRequest("compact is only supported by OpenAI Responses API");
            default:
                throw TransformerErrors.InvalidRequest(request.RequestType + " is not supported");
        }

        if (request.Messages.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("messages are required");
        }

        var oaiReq = request.RequestFromLlm(ReasoningField.Content)
            ?? throw new InvalidOperationException("chat completion request is nil");

        if (oaiReq.ResponseFormat is { Type: "json_schema" } responseFormat)
        {
            // DeepSeek only understands JSON mode, not a schema.
            responseFormat.Type = "json_object";
            responseFormat.JsonSchema = null;
        }

        var thinkingDisabled = request.ReasoningEffort == ReasoningEfforts.None;
        if (thinkingDisabled)
        {
            // DeepSeek only accepts high/low/medium/max/xhigh; "none" would be
            // rejected, so the effort is dropped and thinking disabled instead.
            oaiReq.ReasoningEffort = null;
        }
        else
        {
            foreach (var message in oaiReq.Messages ?? [])
            {
                if (message.Role == "assistant" && message.ReasoningContent is null)
                {
                    message.ReasoningContent = string.Empty;
                }
            }
        }

        // Go embeds openai.Request in deepseek.Request, so the thinking field is
        // marshalled after the OpenAI fields; appending it to the serialized
        // object reproduces that order.
        var body = JsonNode.Parse(GoJson.Marshal(oaiReq))!.AsObject();
        body["thinking"] = new JsonObject
        {
            ["type"] = thinkingDisabled ? "disabled" : "enabled",
        };

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        return new HttpRequestData
        {
            Method = "POST",
            Url = _baseUrl + "/chat/completions",
            Headers = headers,
            Body = GoJson.Marshal(body),
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
            ApiFormat = ApiFormats.OpenAIChatCompletion,
        };
    }

    /// <summary>Port of TransformResponse: completion responses use the completion transformer.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
        => response?.Request?.RequestType == RequestTypes.Completion
            ? _completion.TransformResponse(response)
            : _chat.TransformResponse(response);

    /// <summary>Port of TransformStream.</summary>
    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
        => request?.RequestType == RequestTypes.Completion
            ? _completion.TransformStream(request, stream)
            : _chat.TransformStream(request, stream);

    /// <summary>Port of TransformError: always the chat (OpenAI) error envelope.</summary>
    public ResponseError TransformError(HttpClientError? error) => _chat.TransformError(error);

    /// <summary>Port of AggregateStreamChunks.</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => request?.RequestType == RequestTypes.Completion
            ? _completion.AggregateStreamChunks(request, chunks, out meta)
            : _chat.AggregateStreamChunks(request, chunks, out meta);

    private static string TrimSuffix(string value, string suffix)
        => value.EndsWith(suffix, StringComparison.Ordinal) ? value[..^suffix.Length] : value;
}
