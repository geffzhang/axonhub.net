using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Http;
using AxonHub.Llm.Json;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.Ollama;

/// <summary>Port of ollama.Config (llm/transformer/ollama/outbound.go:18-30).</summary>
public sealed class OllamaOutboundConfig
{
    /// <summary>The Ollama base URL, required.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional key provider: local Ollama usually has no key, but a configured
    /// one is sent as a Bearer token.
    /// </summary>
    public IApiKeyProvider? ApiKeyProvider { get; set; }

    /// <summary>
    /// Optional custom path override (reference <c>EndpointPath</c>). When set it
    /// replaces <c>/api/chat</c> as is.
    /// </summary>
    public string EndpointPath { get; set; } = string.Empty;

    /// <summary>
    /// Port-only audit field: 1-based position of the key the provider serves in
    /// the channel's full key list, or null when the channel has a single key.
    /// </summary>
    public int? ChannelApiKeyIndex { get; set; }
}

/// <summary>
/// Port of the Ollama outbound transformer (llm/transformer/ollama):
/// NDJSON streaming, the native <c>/api/chat</c> wire shape, optional Bearer
/// auth and the <c>options</c> envelope.
/// </summary>
public sealed class OllamaOutboundTransformer : IOutboundTransformer, IApiKeyProvenance
{
    private readonly OllamaOutboundConfig _config;

    /// <summary>The key the latest TransformRequest used; null until it runs.</summary>
    private string? _selectedApiKey;

    /// <summary>Port of NewOutboundTransformerWithConfig.</summary>
    public OllamaOutboundTransformer(OllamaOutboundConfig config)
    {
        if (string.IsNullOrEmpty(config?.BaseUrl))
        {
            throw new InvalidOperationException("base URL is required for Ollama transformer");
        }

        _config = config;
    }

    public string ApiFormat => ApiFormats.OllamaChat;

    public string? SelectedApiKey => _selectedApiKey;

    public int? SelectedApiKeyIndex => _config.ChannelApiKeyIndex;

    /// <summary>Port of TransformRequest.</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        if (request is null)
        {
            throw new InvalidOperationException("request is nil");
        }

        if (string.IsNullOrEmpty(request.Model))
        {
            throw TransformerErrors.InvalidRequest("model is required");
        }

        if (request.Messages.Count == 0)
        {
            throw TransformerErrors.InvalidRequest("messages are required");
        }

        // Ollama defaults to streaming; the reference always pins the flag
        // (false when the request does not ask to stream).
        var stream = request.Stream ?? false;

        var ollamaReq = new OllamaChatRequest
        {
            Model = request.Model,
            Messages = new List<OllamaChatMessage>(request.Messages.Count),
            Stream = stream,
        };

        foreach (var message in request.Messages)
        {
            var chatMessage = new OllamaChatMessage
            {
                Role = message.Role ?? string.Empty,
                Content = GetContentString(message.Content),
                Images = GetImages(message.Content),
            };

            if (message.ToolCalls is { Count: > 0 } toolCalls)
            {
                chatMessage.ToolCalls = new List<OllamaToolCall>(toolCalls.Count);
                foreach (var toolCall in toolCalls)
                {
                    var rawArguments = toolCall.Function.Arguments;
                    if (string.IsNullOrEmpty(rawArguments))
                    {
                        rawArguments = "{}";
                    }

                    chatMessage.ToolCalls.Add(new OllamaToolCall
                    {
                        Function = new OllamaToolCallFunction
                        {
                            Name = toolCall.Function.Name,
                            Arguments = ParseArguments(rawArguments, toolCall.Function.Name),
                        },
                    });
                }
            }

            ollamaReq.Messages.Add(chatMessage);
        }

        foreach (var tool in request.Tools ?? [])
        {
            if (!string.Equals(tool.Type, ToolTypes.Function, StringComparison.Ordinal))
            {
                continue;
            }

            ollamaReq.Tools ??= [];
            ollamaReq.Tools.Add(new OllamaToolDefinition
            {
                Type = "function",
                Function = new OllamaToolFunction
                {
                    Name = tool.Function.Name,
                    Description = tool.Function.Description,
                    Parameters = NormalizeParameters(tool.Function.Parameters),
                },
            });
        }

        ollamaReq.Options = BuildOptions(request);

        byte[] body;
        try
        {
            body = GoJson.Marshal(ollamaReq);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidOperationException(
                "failed to marshal ollama request: " + ex.Message,
                ex);
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(headers, "Content-Type", "application/json");
        HttpClientUtils.HeaderSet(headers, "Accept", "application/json");

        AuthConfig? auth = null;
        var apiKey = _config.ApiKeyProvider?.Get() ?? string.Empty;
        _selectedApiKey = apiKey;
        if (apiKey.Length > 0)
        {
            auth = new AuthConfig
            {
                Type = AuthTypes.Bearer,
                ApiKey = apiKey,
            };
        }

        var path = _config.EndpointPath.Length > 0 ? _config.EndpointPath : "/api/chat";

        return new HttpRequestData
        {
            Method = "POST",
            Url = TrimSuffix(_config.BaseUrl, "/") + path,
            Headers = headers,
            Body = body,
            Auth = auth,
            ApiFormat = ApiFormats.OllamaChat,
        };
    }

    /// <summary>Port of TransformResponse.</summary>
    public LlmResponse TransformResponse(HttpResponseData? response)
    {
        if (response is null)
        {
            throw new InvalidOperationException("response is nil");
        }

        OllamaChatResponse ollamaResponse;
        try
        {
            ollamaResponse = LlmJson.Deserialize<OllamaChatResponse>(response.Body ?? [])
                ?? new OllamaChatResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal ollama response: " + ex.Message,
                ex);
        }

        var (content, reasoningContent, toolCalls) = ExtractMessage(ollamaResponse);

        var finishReason = ollamaResponse.DoneReason is { Length: > 0 } doneReason
            ? doneReason
            : "stop";
        if (toolCalls is { Count: > 0 })
        {
            finishReason = "tool_calls";
        }

        return new LlmResponse
        {
            Id = "ollama-" + ollamaResponse.Model,
            Object = "chat.completion",
            Created = 0,
            Model = ollamaResponse.Model,
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Message = new Message
                    {
                        Role = "assistant",
                        Content = MessageContent.FromText(content),
                        ReasoningContent = reasoningContent,
                        ToolCalls = toolCalls,
                    },
                    FinishReason = finishReason,
                },
            ],
            Usage = new Usage
            {
                PromptTokens = ollamaResponse.PromptEvalCount,
                CompletionTokens = ollamaResponse.EvalCount,
                TotalTokens = ollamaResponse.PromptEvalCount + ollamaResponse.EvalCount,
            },
        };
    }

    /// <summary>Port of TransformStream (streams.MapErr over TransformStreamChunk).</summary>
    public IStream<LlmResponse> TransformStream(
        HttpRequestData? request,
        IStream<StreamEvent> stream)
        => new MapStream<StreamEvent, LlmResponse>(stream, TransformStreamChunk);

    /// <summary>Port of TransformStreamChunk (exposed for direct tests).</summary>
    public LlmResponse? TransformStreamChunk(StreamEvent? streamEvent)
    {
        if (streamEvent is null || streamEvent.Data.Length == 0)
        {
            return null;
        }

        OllamaChatResponse ollamaResponse;
        try
        {
            ollamaResponse = LlmJson.Deserialize<OllamaChatResponse>(streamEvent.Data)
                ?? new OllamaChatResponse();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal ollama stream chunk: " + ex.Message,
                ex);
        }

        var (content, reasoningContent, toolCalls) = ExtractMessage(ollamaResponse);

        string? finishReason;
        if (toolCalls is { Count: > 0 })
        {
            finishReason = "tool_calls";
        }
        else if (ollamaResponse.Done)
        {
            finishReason = ollamaResponse.DoneReason is { Length: > 0 } doneReason
                ? doneReason
                : "stop";
        }
        else
        {
            finishReason = null;
        }

        var response = new LlmResponse
        {
            Id = "ollama-" + ollamaResponse.Model,
            Object = "chat.completion.chunk",
            Model = ollamaResponse.Model,
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Delta = new Message
                    {
                        Role = "assistant",
                        Content = MessageContent.FromText(content),
                        ReasoningContent = reasoningContent,
                        ToolCalls = toolCalls,
                    },
                    FinishReason = finishReason,
                },
            ],
        };

        if (ollamaResponse.Done
            && (ollamaResponse.PromptEvalCount > 0 || ollamaResponse.EvalCount > 0))
        {
            response.Usage = new Usage
            {
                PromptTokens = ollamaResponse.PromptEvalCount,
                CompletionTokens = ollamaResponse.EvalCount,
                TotalTokens = ollamaResponse.PromptEvalCount + ollamaResponse.EvalCount,
            };
        }

        return response;
    }

    /// <summary>Port of TransformError: the raw body becomes the message.</summary>
    public ResponseError TransformError(HttpClientError? error)
    {
        // The reference returns a nil *llm.ResponseError; the port interface is
        // non-nullable, so the nil is carried as null! for callers that check.
        if (error is null)
        {
            return null!;
        }

        return new ResponseError
        {
            StatusCode = error.StatusCode,
            Detail = new ErrorDetail
            {
                Message = Encoding.UTF8.GetString(error.Body),
                Type = "ollama_error",
            },
        };
    }

    /// <summary>Port of AggregateStreamChunks.</summary>
    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
    {
        if (chunks.Count == 0)
        {
            throw new InvalidOperationException("no chunks to aggregate");
        }

        var fullContent = new StringBuilder();
        var fullThinking = new StringBuilder();
        var model = string.Empty;
        long promptEvalCount = 0;
        long evalCount = 0;
        var finishReason = string.Empty;
        List<OllamaToolCall>? lastToolCalls = null;

        foreach (var chunk in chunks)
        {
            OllamaChatResponse ollamaResponse;
            try
            {
                ollamaResponse = LlmJson.Deserialize<OllamaChatResponse>(chunk.Data)
                    ?? new OllamaChatResponse();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "failed to unmarshal ollama chunk: " + ex.Message,
                    ex);
            }

            if (ollamaResponse.Model.Length > 0)
            {
                model = ollamaResponse.Model;
            }

            if (ollamaResponse.Message is { } message)
            {
                fullContent.Append(message.Content);
                fullThinking.Append(message.Thinking);

                if (message.ToolCalls is { Count: > 0 })
                {
                    lastToolCalls = message.ToolCalls;
                }
            }

            if (ollamaResponse.PromptEvalCount > 0)
            {
                promptEvalCount = ollamaResponse.PromptEvalCount;
            }

            if (ollamaResponse.EvalCount > 0)
            {
                evalCount = ollamaResponse.EvalCount;
            }

            if (ollamaResponse.Done && ollamaResponse.DoneReason is { Length: > 0 })
            {
                finishReason = ollamaResponse.DoneReason;
            }
        }

        if (finishReason.Length == 0)
        {
            finishReason = "stop";
        }

        var content = fullContent.ToString();
        var reasoningContent = fullThinking.Length > 0 ? fullThinking.ToString() : null;
        var toolCalls = BuildToolCalls(lastToolCalls, model);
        if (toolCalls is { Count: > 0 })
        {
            finishReason = "tool_calls";
        }

        var aggregated = new LlmResponse
        {
            Id = "ollama-" + model,
            Object = "chat.completion",
            Created = 0,
            Model = model,
            Choices =
            [
                new Choice
                {
                    Index = 0,
                    Message = new Message
                    {
                        Role = "assistant",
                        Content = MessageContent.FromText(content),
                        ReasoningContent = reasoningContent,
                        ToolCalls = toolCalls,
                    },
                    FinishReason = finishReason,
                },
            ],
            Usage = new Usage
            {
                PromptTokens = promptEvalCount,
                CompletionTokens = evalCount,
                TotalTokens = promptEvalCount + evalCount,
            },
        };

        byte[] body;
        try
        {
            body = GoJson.Marshal(aggregated);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new InvalidOperationException(
                "failed to marshal aggregated response: " + ex.Message,
                ex);
        }

        meta = new ResponseMeta
        {
            Id = "ollama-" + model,
            Usage = new Usage
            {
                PromptTokens = promptEvalCount,
                CompletionTokens = evalCount,
                TotalTokens = promptEvalCount + evalCount,
            },
        };

        return body;
    }

    /// <summary>Port of getContentString.</summary>
    private static string GetContentString(MessageContent? content)
    {
        if (content?.Content is { } single)
        {
            return single;
        }

        if (content?.MultipleContent is { Count: > 0 } parts)
        {
            var builder = new StringBuilder();
            foreach (var part in parts)
            {
                if (part.Type == "text" && part.Text is { } text)
                {
                    builder.Append(text);
                }
            }

            return builder.ToString();
        }

        return string.Empty;
    }

    /// <summary>Port of getImages: data-URL prefixes are stripped to raw base64.</summary>
    private static List<string>? GetImages(MessageContent? content)
    {
        if (content?.MultipleContent is not { Count: > 0 } parts)
        {
            return null;
        }

        var images = new List<string>();
        foreach (var part in parts)
        {
            if (!string.Equals(part.Type, "image_url", StringComparison.Ordinal)
                || part.ImageUrl is null)
            {
                continue;
            }

            var image = part.ImageUrl.Url.Trim();
            if (image.Length == 0)
            {
                continue;
            }

            var comma = image.IndexOf(',', StringComparison.Ordinal);
            if (image.StartsWith("data:", StringComparison.Ordinal) && comma >= 0)
            {
                image = image[(comma + 1)..];
            }

            if (image.Length > 0)
            {
                images.Add(image);
            }
        }

        // Go: "images,omitempty" — an empty slice is omitted, nil is omitted.
        return images.Count > 0 ? images : null;
    }

    /// <summary>
    /// Port of the <c>json.RawMessage</c> parameter handling: a nil fragment
    /// becomes <c>{}</c>, anything else is compacted and HTML-escaped exactly
    /// as Go re-marshal does.
    /// </summary>
    private static JsonElement NormalizeParameters(JsonElement? parameters)
    {
        if (parameters is not { } value || value.ValueKind == JsonValueKind.Undefined)
        {
            return EmptyObject();
        }

        return GoJson.CompactElement(value);
    }

    /// <summary>Port of buildOptions.</summary>
    private static OllamaOptions? BuildOptions(LlmRequest request)
    {
        OllamaOptions? options = null;

        if (request.Temperature is { } temperature)
        {
            options ??= new OllamaOptions();
            options.Temperature = temperature;
        }

        if (request.TopP is { } topP)
        {
            options ??= new OllamaOptions();
            options.TopP = topP;
        }

        if (request.MaxTokens is { } maxTokens)
        {
            options ??= new OllamaOptions();
            options.NumPredict = (int)maxTokens;
        }

        if (request.Stop is { } stop)
        {
            List<string>? stops = null;
            if (stop.Single is { } single)
            {
                stops = [single];
            }
            else if (stop.Multiple is { Count: > 0 } multiple)
            {
                stops = multiple;
            }

            if (stops is { Count: > 0 })
            {
                options ??= new OllamaOptions();
                options.Stop = stops;
            }
        }

        return options;
    }

    /// <summary>
    /// Port of json.Unmarshal into map[string]any: objects and null are accepted,
    /// every other JSON value is a Go-shaped type error.
    /// </summary>
    private static JsonElement? ParseArguments(string rawArguments, string functionName)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawArguments);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to parse tool call arguments for " + functionName + ": " + ex.Message,
                ex);
        }

        using (document)
        {
            var root = document.RootElement;
            switch (root.ValueKind)
            {
                case JsonValueKind.Object:
                    return root.Clone();
                case JsonValueKind.Null:
                    return null;
                default:
                    throw new InvalidOperationException(
                        "failed to parse tool call arguments for " + functionName
                        + ": json: cannot unmarshal " + GoKindName(root.ValueKind)
                        + " into Go value of type map[string]interface {}");
            }
        }
    }

    private static string GoKindName(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.String => "string",
        JsonValueKind.True or JsonValueKind.False => "bool",
        JsonValueKind.Number => "number",
        _ => "value",
    };

    private static (string Content, string? ReasoningContent, List<ToolCall>? ToolCalls) ExtractMessage(
        OllamaChatResponse ollamaResponse)
    {
        var content = string.Empty;
        string? reasoningContent = null;
        List<ToolCall>? toolCalls = null;

        if (ollamaResponse.Message is { } message)
        {
            content = message.Content;
            if (message.Thinking.Length > 0)
            {
                reasoningContent = message.Thinking;
            }

            toolCalls = BuildToolCalls(message.ToolCalls, ollamaResponse.Model);
        }

        return (content, reasoningContent, toolCalls);
    }

    private static List<ToolCall>? BuildToolCalls(
        List<OllamaToolCall>? toolCalls,
        string model)
    {
        if (toolCalls is not { Count: > 0 })
        {
            return null;
        }

        var result = new List<ToolCall>(toolCalls.Count);
        for (var index = 0; index < toolCalls.Count; index++)
        {
            var toolCall = toolCalls[index];
            result.Add(new ToolCall
            {
                Id = "ollama-" + model + "-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Type = "function",
                Function = new FunctionCall
                {
                    Name = toolCall.Function.Name,
                    Arguments = MarshalArguments(toolCall.Function.Arguments),
                },
                Index = index,
            });
        }

        return result;
    }

    /// <summary>
    /// Port of json.Marshal(map[string]any): keys sorted, Go HTML escaping,
    /// number literals kept verbatim (the reference re-encodes float64; C097).
    /// A nil map marshals as null, which the callers turn into "{}".
    /// </summary>
    private static string MarshalArguments(JsonElement? arguments)
    {
        if (arguments is not { } value || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return "{}";
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            GoAnyJson.Write(writer, value);
        }

        return Encoding.UTF8.GetString(
            GoJson.Escape(buffer.GetBuffer().AsSpan(0, (int)buffer.Length)));
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static string TrimSuffix(string value, string suffix)
        => value.EndsWith(suffix, StringComparison.Ordinal) ? value[..^suffix.Length] : value;

    // ------------------------------------------------------------------
    // Wire models (field order and omitempty mirror outbound.go:44-110).
    // ------------------------------------------------------------------

    internal sealed class OllamaChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<OllamaChatMessage> Messages { get; set; } = [];

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("options")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OllamaOptions? Options { get; set; }

        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<OllamaToolDefinition>? Tools { get; set; }
    }

    internal sealed class OllamaChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("images")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Images { get; set; }

        [JsonPropertyName("thinking")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Thinking { get; set; }

        [JsonPropertyName("tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<OllamaToolCall>? ToolCalls { get; set; }
    }

    internal sealed class OllamaOptions
    {
        [JsonPropertyName("temperature")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? Temperature { get; set; }

        [JsonPropertyName("top_p")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? TopP { get; set; }

        [JsonPropertyName("top_k")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? TopK { get; set; }

        [JsonPropertyName("num_ctx")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? NumCtx { get; set; }

        [JsonPropertyName("num_predict")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? NumPredict { get; set; }

        [JsonPropertyName("stop")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<string>? Stop { get; set; }
    }

    internal sealed class OllamaToolDefinition
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("function")]
        public OllamaToolFunction Function { get; set; } = new();
    }

    internal sealed class OllamaToolFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("parameters")]
        public JsonElement Parameters { get; set; }
    }

    internal sealed class OllamaToolCall
    {
        [JsonPropertyName("function")]
        public OllamaToolCallFunction Function { get; set; } = new();
    }

    internal sealed class OllamaToolCallFunction
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Description { get; set; }

        [JsonPropertyName("arguments")]
        [JsonConverter(typeof(GoAnyJsonConverter))]
        public JsonElement? Arguments { get; set; }
    }

    /// <summary>
    /// Port of ollama.ChatResponse. Unknown fields are ignored like Go's
    /// decoder, and a JSON null leaves each field at its Go zero value.
    /// </summary>
    internal sealed class OllamaChatResponse
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("created_at")]
        public string? CreatedAt { get; set; }

        [JsonPropertyName("message")]
        public OllamaChatResponseMessage? Message { get; set; }

        [JsonPropertyName("done")]
        public bool Done { get; set; }

        [JsonPropertyName("done_reason")]
        public string? DoneReason { get; set; }

        [JsonPropertyName("total_duration")]
        public long TotalDuration { get; set; }

        [JsonPropertyName("load_duration")]
        public long LoadDuration { get; set; }

        [JsonPropertyName("prompt_eval_count")]
        public long PromptEvalCount { get; set; }

        [JsonPropertyName("prompt_eval_duration")]
        public long PromptEvalDuration { get; set; }

        [JsonPropertyName("eval_count")]
        public long EvalCount { get; set; }

        [JsonPropertyName("eval_duration")]
        public long EvalDuration { get; set; }
    }

    internal sealed class OllamaChatResponseMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("thinking")]
        public string Thinking { get; set; } = string.Empty;

        [JsonPropertyName("tool_calls")]
        public List<OllamaToolCall>? ToolCalls { get; set; }
    }
}
