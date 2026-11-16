using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer.OpenAI;

/// <summary>
/// Port of openai.CompletionOutboundTransformer
/// (llm/transformer/openai/completion_outbound.go): speaks the legacy
/// <c>/completions</c> protocol to an OpenAI-compatible upstream.
/// </summary>
public sealed class CompletionOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    private readonly OpenAiOutboundConfig _config;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    public CompletionOutboundTransformer(OpenAiOutboundConfig config)
    {
        if (config is null)
        {
            throw new InvalidOperationException("config cannot be nil");
        }

        if (config.ApiKeyProvider is null)
        {
            throw new InvalidOperationException("API key provider is required");
        }

        if (string.IsNullOrEmpty(config.BaseUrl))
        {
            throw new InvalidOperationException("base URL is required");
        }

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

    /// <summary>Port of NewCompletionOutboundTransformer(baseURL, apiKey).</summary>
    public static CompletionOutboundTransformer Create(string baseUrl, string apiKey)
        => Create(baseUrl, new StaticApiKeyProvider(apiKey));

    /// <summary>
    /// Builds a transformer with a key provider and its audit index
    /// (the reference's getAPIKeyProvider + channel_api_key_index row).
    /// </summary>
    public static CompletionOutboundTransformer Create(
        string baseUrl,
        IApiKeyProvider apiKeyProvider,
        int? channelApiKeyIndex = null)
        => new(new OpenAiOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = apiKeyProvider,
            ChannelApiKeyIndex = channelApiKeyIndex,
        });

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    public string ApiFormat => ApiFormats.OpenAICompletion;

    public string BaseUrl => _config.BaseUrl;

    /// <summary>Port of TransformRequest.</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("llm request is nil");
        }

        if (request.Completion is null)
        {
            throw new InvalidOperationException("completion request is nil in llm.Request");
        }

        if (string.IsNullOrEmpty(request.Model))
        {
            throw new InvalidOperationException("model is required");
        }

        var completion = request.Completion;

        var compReq = new OpenAiCompletionRequest
        {
            Model = request.Model,
            Prompt = completion.Prompt,
            Suffix = NullIfEmpty(completion.Suffix),
            MaxTokens = completion.MaxTokens,
            Temperature = completion.Temperature,
            TopP = completion.TopP,
            N = completion.N,
            Stream = request.Stream,
            StreamOptions = request.StreamOptions,
            Logprobs = completion.Logprobs,
            Echo = completion.Echo,
            PresencePenalty = completion.PresencePenalty,
            FrequencyPenalty = completion.FrequencyPenalty,
            BestOf = completion.BestOf,
            LogitBias = completion.LogitBias,
            Seed = completion.Seed,
            User = NullIfEmpty(completion.User),
        };

        if (completion.Stop is not null)
        {
            compReq.Stop = new Stop
            {
                Single = completion.Stop.Single,
                Multiple = completion.Stop.Multiple,
            };
        }

        var body = GoJson.Marshal(compReq);

        var apiKey = _config.ApiKeyProvider!.Get();
        _selectedApiKey = apiKey;

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        return new HttpRequestData
        {
            Method = "POST",
            Url = BuildFullRequestUrl(),
            Headers = headers,
            Body = body,
            Auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            },
            RequestType = RequestTypes.Completion,
            ApiFormat = ApiFormats.OpenAICompletion,
        };
    }

    /// <summary>Port of buildURL.</summary>
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

        return _config.BaseUrl + "/completions";
    }

    /// <summary>Port of TransformResponse.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("http response is nil");
        }

        if (response.StatusCode >= 400)
        {
            throw TransformError(new HttpClientError(
                "POST",
                string.Empty,
                response.StatusCode,
                GoHttpStatus.Text(response.StatusCode),
                response.Body));
        }

        if (response.Body is null || response.Body.Length == 0)
        {
            throw new InvalidOperationException("response body is empty");
        }

        var syntaxError = GoJsonSyntax.Validate(response.Body);
        if (syntaxError is not null)
        {
            throw new InvalidOperationException(
                "failed to unmarshal completion response: " + syntaxError);
        }

        var rootKind = GoStructJson.RootKind(response.Body);
        if (rootKind is not null)
        {
            // null leaves the zero struct in place; other roots are a type error.
            return rootKind == "null"
                ? CompletionResponseToLlm(new OpenAiCompletionResponse())
                : throw new InvalidOperationException(
                    "failed to unmarshal completion response: "
                    + GoStructJson.TypeMismatch(rootKind, "openai.CompletionResponse"));
        }

        OpenAiCompletionResponse compResp;
        try
        {
            compResp = LlmJson.Deserialize<OpenAiCompletionResponse>(response.Body)
                ?? new OpenAiCompletionResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal completion response: " + OpenAiInboundTransformer.JsonErrorText(ex),
                ex);
        }

        return CompletionResponseToLlm(compResp);
    }

    /// <summary>Port of completionResponseToLLM.</summary>
    private static LlmResponse CompletionResponseToLlm(OpenAiCompletionResponse compResp)
    {
        // encoding/json leaves a null slice nil and a null element in a struct
        // slice as its zero value; System.Text.Json yields null for both.
        var wireChoices = compResp.Choices ?? [];
        var llmChoices = new List<CompletionChoice>(wireChoices.Count);
        foreach (var choice in wireChoices)
        {
            var wireChoice = choice ?? new OpenAiCompletionChoice();
            llmChoices.Add(new CompletionChoice
            {
                // Go decodes a JSON null string as the zero value "".
                Text = wireChoice.Text ?? string.Empty,
                Index = wireChoice.Index,
                Logprobs = wireChoice.Logprobs,
                FinishReason = wireChoice.FinishReason,
            });
        }

        var llmResp = new LlmResponse
        {
            Id = compResp.Id ?? string.Empty,
            Object = compResp.Object ?? string.Empty,
            Created = compResp.Created,
            Model = compResp.Model ?? string.Empty,
            RequestType = RequestTypes.Completion,
            ApiFormat = ApiFormats.OpenAICompletion,
            Completion = new CompletionResponse
            {
                Choices = llmChoices,
            },
        };

        var usage = compResp.Usage ?? new OpenAiCompletionUsage();
        if (usage.PromptTokens > 0 || usage.TotalTokens > 0)
        {
            llmResp.Usage = new Usage
            {
                PromptTokens = usage.PromptTokens,
                CompletionTokens = usage.CompletionTokens,
                TotalTokens = usage.TotalTokens,
            };
        }

        return llmResp;
    }

    /// <summary>Port of TransformStream.</summary>
    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
        => new MapStream<StreamEvent, LlmResponse>(stream, TransformStreamChunk);

    /// <summary>Port of TransformStreamChunk. Returns null for empty events.</summary>
    public LlmResponse? TransformStreamChunk(StreamEvent? streamEvent)
    {
        if (streamEvent is null)
        {
            throw new InvalidOperationException("stream event is nil");
        }

        if (StartsWith(streamEvent.Data, "[DONE]"))
        {
            return LlmResponse.Done();
        }

        if (streamEvent.Data.Length == 0)
        {
            return null;
        }

        return TransformResponse(new HttpResponseData { Body = streamEvent.Data });
    }

    /// <summary>Port of AggregateStreamChunks.</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => CompletionAggregator.AggregateStreamChunks(chunks, out meta);

    /// <summary>Port of TransformError: TransformOpenAIError.</summary>
    public ResponseError TransformError(HttpClientError? error)
        => OpenAiOutboundTransformer.TransformOpenAiError(error);

    private static bool StartsWith(byte[] data, string prefix)
        => data.Length >= prefix.Length
            && Encoding.UTF8.GetString(data, 0, prefix.Length) == prefix;

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;
}
