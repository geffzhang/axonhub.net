using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Ollama;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Ollama outbound transformer
/// (llm/transformer/ollama/outbound.go and outbound_test.go): the native
/// <c>/api/chat</c> wire shape, the optional Bearer key, NDJSON chunk mapping and
/// the aggregation used when a non-streaming client had to be streamed.
/// </summary>
public sealed class OllamaOutboundTransformerTests
{
    private static readonly string?[] StreamedContents = ["Hel", "lo"];

    // ------------------------------------------------------------------
    // Request conversion (reference outbound_test.go:14-176)
    // ------------------------------------------------------------------

    [Fact]
    public void TransformRequestPreservesImageURLParts()
    {
        // The reference keeps text parts concatenated and strips the data-URL
        // prefix from images, leaving raw base64.
        var request = new LlmRequest
        {
            Model = "qwen3.5:4b-q4_K_M",
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = MessageContent.FromParts(
                    [
                        new MessageContentPart { Type = "text", Text = "OCR this image." },
                        new MessageContentPart
                        {
                            Type = "image_url",
                            ImageUrl = new ImageUrl { Url = "data:image/png;base64,Zm9v" },
                        },
                    ]),
                },
            ],
        };

        var body = Body(Transformer().TransformRequest(request));
        var message = body.GetProperty("messages")[0];

        Assert.Equal("OCR this image.", message.GetProperty("content").GetString());
        Assert.Equal("Zm9v", message.GetProperty("images")[0].GetString());
    }

    [Fact]
    public void TransformRequestWithoutImagesOmitsTheImagesField()
    {
        // Go tag "images,omitempty": a nil or empty slice is never emitted.
        var request = new LlmRequest
        {
            Model = "qwen3",
            Messages =
            [
                new Message
                {
                    Role = "user",
                    Content = MessageContent.FromParts(
                        [new MessageContentPart { Type = "text", Text = "Hello" }]),
                },
            ],
        };

        var message = Body(Transformer().TransformRequest(request)).GetProperty("messages")[0];

        Assert.False(message.TryGetProperty("images", out _));
    }

    [Fact]
    public void TransformRequestWithTools()
    {
        var request = new LlmRequest
        {
            Model = "qwen3",
            Stream = false,
            Messages = [User("What is the weather in Paris?")],
            Tools =
            [
                new Tool
                {
                    Type = ToolTypes.Function,
                    Function = new Function
                    {
                        Name = "get_current_weather",
                        Description = "Get the current weather for a location",
                        Parameters = Json(
                            """{"type":"object","properties":{"location":{"type":"string"},"format":{"type":"string","enum":["celsius","fahrenheit"]}},"required":["location","format"]}"""),
                    },
                },
            ],
        };

        var tools = Body(Transformer().TransformRequest(request)).GetProperty("tools");

        Assert.Equal(1, tools.GetArrayLength());
        Assert.Equal("function", tools[0].GetProperty("type").GetString());
        Assert.Equal("get_current_weather", tools[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(
            """{"type":"object","properties":{"location":{"type":"string"},"format":{"type":"string","enum":["celsius","fahrenheit"]}},"required":["location","format"]}""",
            tools[0].GetProperty("function").GetProperty("parameters").GetRawText());
    }

    [Fact]
    public void TransformRequestWithToolMissingParametersSendsAnEmptySchema()
    {
        // The reference substitutes json.RawMessage("{}") for a nil schema.
        var request = new LlmRequest
        {
            Model = "qwen3",
            Messages = [User("Hello")],
            Tools = [new Tool { Type = ToolTypes.Function, Function = new Function { Name = "noop" } }],
        };

        var parameters = Body(Transformer().TransformRequest(request))
            .GetProperty("tools")[0].GetProperty("function").GetProperty("parameters");

        Assert.Equal("{}", parameters.GetRawText());
    }

    [Fact]
    public void TransformRequestSkipsNonFunctionTools()
    {
        var request = new LlmRequest
        {
            Model = "qwen3",
            Messages = [User("Hello")],
            Tools = [new Tool { Type = "web_search", Function = new Function { Name = "search" } }],
        };

        var body = Body(Transformer().TransformRequest(request));

        Assert.False(body.TryGetProperty("tools", out _));
    }

    [Fact]
    public void TransformRequestWithMessageToolCalls()
    {
        // Message tool calls become ollama functions with an object of arguments;
        // Go re-marshals map[string]any, so keys come out sorted.
        var request = new LlmRequest
        {
            Model = "qwen3",
            Messages =
            [
                new Message
                {
                    Role = "assistant",
                    Content = MessageContent.FromText(string.Empty),
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Id = "call-1",
                            Type = "function",
                            Function = new FunctionCall
                            {
                                Name = "get_current_weather",
                                Arguments = """{"location":"Paris","format":"celsius"}""",
                            },
                        },
                    ],
                },
                new Message { Role = "tool", Content = MessageContent.FromText("22°C"), ToolCallId = "call-1" },
            ],
        };

        var messages = Body(Transformer().TransformRequest(request)).GetProperty("messages");

        Assert.Equal(2, messages.GetArrayLength());
        Assert.Equal(1, messages[0].GetProperty("tool_calls").GetArrayLength());
        Assert.Equal(
            "get_current_weather",
            messages[0].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal(
            """{"format":"celsius","location":"Paris"}""",
            messages[0].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("arguments").GetRawText());
    }

    [Fact]
    public void TransformRequestWithMessageToolCallsEmptyArguments()
    {
        // rawArgs == "" is replaced by "{}" before parsing.
        var request = new LlmRequest
        {
            Model = "qwen3",
            Messages =
            [
                new Message
                {
                    Role = "assistant",
                    Content = MessageContent.FromText(string.Empty),
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Id = "call-1",
                            Type = "function",
                            Function = new FunctionCall { Name = "noop", Arguments = string.Empty },
                        },
                    ],
                },
            ],
        };

        var toolCall = Body(Transformer().TransformRequest(request))
            .GetProperty("messages")[0].GetProperty("tool_calls")[0];

        Assert.Equal("noop", toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{}", toolCall.GetProperty("function").GetProperty("arguments").GetRawText());
    }

    [Fact]
    public void TransformRequestRejectsMalformedToolArguments()
    {
        // json.Unmarshal into map[string]any fails on anything but an object.
        var request = new LlmRequest
        {
            Model = "qwen3",
            Messages =
            [
                new Message
                {
                    Role = "assistant",
                    ToolCalls =
                    [
                        new ToolCall
                        {
                            Type = "function",
                            Function = new FunctionCall { Name = "broken", Arguments = "[1,2]" },
                        },
                    ],
                },
            ],
        };

        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(request));

        Assert.StartsWith(
            "failed to parse tool call arguments for broken: ",
            error.Message,
            StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Request shape, options and auth
    // ------------------------------------------------------------------

    [Fact]
    public void TransformRequestAlwaysPinsTheStreamFlag()
    {
        // Ollama defaults to streaming; the reference always sends the flag,
        // false when the unified request did not ask to stream.
        var request = UserRequest();
        var body = Body(Transformer().TransformRequest(request));
        Assert.False(body.GetProperty("stream").GetBoolean());

        request.Stream = true;
        body = Body(Transformer().TransformRequest(request));
        Assert.True(body.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public void TransformRequestProducesTheReferenceBodyBytes()
    {
        var httpRequest = Transformer().TransformRequest(UserRequest());

        Assert.Equal("POST", httpRequest.Method);
        Assert.Equal(
            """{"model":"qwen3","messages":[{"role":"user","content":"Hello"}],"stream":false}""",
            Encoding.UTF8.GetString(httpRequest.Body!));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpRequest.Headers, "Content-Type"));
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(httpRequest.Headers, "Accept"));
        Assert.Equal(ApiFormats.OllamaChat, httpRequest.ApiFormat);
    }

    [Fact]
    public void TransformRequestMapsSamplingOptions()
    {
        var request = UserRequest();
        request.Temperature = 0.7;
        request.TopP = 0.9;
        request.MaxTokens = 256;
        request.Stop = new Stop { Multiple = ["STOP", "END"] };

        var options = Body(Transformer().TransformRequest(request)).GetProperty("options");

        Assert.Equal(0.7, options.GetProperty("temperature").GetDouble());
        Assert.Equal(0.9, options.GetProperty("top_p").GetDouble());
        Assert.Equal(256, options.GetProperty("num_predict").GetInt32());
        Assert.Equal("STOP", options.GetProperty("stop")[0].GetString());
        Assert.Equal("END", options.GetProperty("stop")[1].GetString());
    }

    [Fact]
    public void TransformRequestMapsASingleStopString()
    {
        var request = UserRequest();
        request.Stop = new Stop { Single = "STOP" };

        var options = Body(Transformer().TransformRequest(request)).GetProperty("options");

        Assert.Equal(1, options.GetProperty("stop").GetArrayLength());
        Assert.Equal("STOP", options.GetProperty("stop")[0].GetString());
    }

    [Fact]
    public void TransformRequestOmitsOptionsWhenNothingIsSet()
    {
        var body = Body(Transformer().TransformRequest(UserRequest()));

        Assert.False(body.TryGetProperty("options", out _));
    }

    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434/api/chat")]
    [InlineData("http://localhost:11434/", "http://localhost:11434/api/chat")]
    [InlineData("https://ollama.example/proxy", "https://ollama.example/proxy/api/chat")]
    public void TransformRequestBuildsTheDefaultEndpoint(string baseUrl, string expectedUrl)
    {
        Assert.Equal(expectedUrl, Transformer(baseUrl).TransformRequest(UserRequest()).Url);
    }

    [Fact]
    public void TransformRequestHonorsAnEndpointPathOverride()
    {
        // Config.EndpointPath replaces "/api/chat" as is.
        var httpRequest = Transformer(endpointPath: "/custom/chat")
            .TransformRequest(UserRequest());

        Assert.Equal("http://localhost:11434/custom/chat", httpRequest.Url);
    }

    [Fact]
    public void TransformRequestSendsABearerTokenOnlyWhenAKeyExists()
    {
        Assert.Null(Transformer().TransformRequest(UserRequest()).Auth);
        Assert.Null(Transformer(apiKey: string.Empty).TransformRequest(UserRequest()).Auth);

        var authorized = Transformer(apiKey: "ollama-key").TransformRequest(UserRequest());
        Assert.NotNull(authorized.Auth);
        Assert.Equal(AuthTypes.Bearer, authorized.Auth!.Type);
        Assert.Equal("ollama-key", authorized.Auth.ApiKey);
    }

    [Fact]
    public void TransformRequestExposesTheSelectedApiKey()
    {
        var transformer = new OllamaOutboundTransformer(new OllamaOutboundConfig
        {
            BaseUrl = "http://localhost:11434",
            ApiKeyProvider = new StaticApiKeyProvider("ollama-key"),
            ChannelApiKeyIndex = 2,
        });

        Assert.Null(transformer.SelectedApiKey);
        Assert.Equal(2, transformer.SelectedApiKeyIndex);

        transformer.TransformRequest(UserRequest());

        Assert.Equal("ollama-key", transformer.SelectedApiKey);
    }

    [Fact]
    public void ConstructorRequiresABaseUrl()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new OllamaOutboundTransformer(new OllamaOutboundConfig()));

        Assert.Equal("base URL is required for Ollama transformer", error.Message);
    }

    [Fact]
    public void TransformRequestRejectsInvalidRequests()
    {
        var nilRequest = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformRequest(null));
        Assert.Equal("request is nil", nilRequest.Message);

        var missingModel = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(new LlmRequest { Messages = [User("Hello")] }));
        Assert.Equal(TransformerErrorKind.InvalidRequest, missingModel.Kind);
        Assert.Equal("invalid request: model is required", missingModel.Message);

        var missingMessages = Assert.Throws<TransformerException>(
            () => Transformer().TransformRequest(new LlmRequest { Model = "qwen3" }));
        Assert.Equal("invalid request: messages are required", missingMessages.Message);
    }

    // ------------------------------------------------------------------
    // Response conversion (reference outbound_test.go:178-260)
    // ------------------------------------------------------------------

    [Fact]
    public void TransformResponseWithToolCalls()
    {
        var response = Transformer().TransformResponse(Response(
            """{"model":"qwen3","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"get_current_weather","arguments":{"location":"Paris","format":"celsius"}}}]},"done":true,"done_reason":"stop","prompt_eval_count":10,"eval_count":5}"""));

        Assert.Single(response.Choices);
        var toolCall = Assert.Single(response.Choices[0].Message!.ToolCalls!);
        Assert.Equal("get_current_weather", toolCall.Function.Name);
        Assert.Equal("""{"format":"celsius","location":"Paris"}""", toolCall.Function.Arguments);
    }

    [Fact]
    public void TransformResponseWithToolCallsSetsFinishReason()
    {
        var response = Transformer().TransformResponse(Response(
            """{"model":"qwen3","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"get_current_weather","arguments":{"location":"Paris","format":"celsius"}}}]},"done":true,"done_reason":"stop","prompt_eval_count":10,"eval_count":5}"""));

        Assert.Equal("tool_calls", response.Choices[0].FinishReason);
    }

    [Fact]
    public void TransformResponseMapsTextThinkingAndUsage()
    {
        var response = Transformer().TransformResponse(Response(
            """{"model":"qwen3","message":{"role":"assistant","content":"Hi","thinking":"hmm"},"done":true,"done_reason":"length","prompt_eval_count":10,"eval_count":5}"""));

        Assert.Equal("ollama-qwen3", response.Id);
        Assert.Equal("chat.completion", response.Object);
        Assert.Equal("qwen3", response.Model);
        var message = response.Choices[0].Message!;
        Assert.Equal("assistant", message.Role);
        Assert.Equal("Hi", message.Content!.Content);
        Assert.Equal("hmm", message.ReasoningContent);
        Assert.Equal("length", response.Choices[0].FinishReason);
        Assert.Equal(10, response.Usage!.PromptTokens);
        Assert.Equal(5, response.Usage.CompletionTokens);
        Assert.Equal(15, response.Usage.TotalTokens);
    }

    [Fact]
    public void TransformResponseDefaultsToStopWhenDoneReasonIsMissing()
    {
        var response = Transformer().TransformResponse(Response(
            """{"model":"qwen3","message":{"role":"assistant","content":"Hi"}}"""));

        var message = response.Choices[0].Message!;
        Assert.Equal("stop", response.Choices[0].FinishReason);
        Assert.Null(message.ReasoningContent);
        Assert.Null(message.ToolCalls);
        Assert.Equal(0, response.Usage!.TotalTokens);
    }

    [Fact]
    public void TransformResponseRejectsMalformedBodies()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(Response("{not json")));

        Assert.StartsWith("failed to unmarshal ollama response: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TransformResponseRejectsANilResponse()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformResponse(null));

        Assert.Equal("response is nil", error.Message);
    }

    // ------------------------------------------------------------------
    // Streaming (reference outbound_test.go:220-260)
    // ------------------------------------------------------------------

    [Fact]
    public void TransformStreamChunkWithToolCalls()
    {
        var response = Transformer().TransformStreamChunk(Event(
            """{"model":"qwen3","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"get_current_weather","arguments":{"location":"Paris","format":"celsius"}}}]},"done":true,"done_reason":"stop","prompt_eval_count":10,"eval_count":5}"""));

        Assert.NotNull(response);
        Assert.Single(response!.Choices);
        var toolCall = Assert.Single(response.Choices[0].Delta!.ToolCalls!);
        Assert.Equal("get_current_weather", toolCall.Function.Name);
        Assert.Equal("""{"format":"celsius","location":"Paris"}""", toolCall.Function.Arguments);
    }

    [Fact]
    public void TransformStreamChunkWithToolCallsSetsFinishReason()
    {
        var response = Transformer().TransformStreamChunk(Event(
            """{"model":"qwen3","message":{"role":"assistant","content":"","tool_calls":[{"function":{"name":"get_current_weather","arguments":{"location":"Paris","format":"celsius"}}}]},"done":true,"done_reason":"stop","prompt_eval_count":10,"eval_count":5}"""));

        Assert.Equal("tool_calls", response!.Choices[0].FinishReason);
    }

    [Fact]
    public void TransformStreamChunkMapsContentWithoutFinishReason()
    {
        var response = Transformer().TransformStreamChunk(Event(
            """{"model":"qwen3","message":{"role":"assistant","content":"Hel"},"done":false}"""));

        Assert.NotNull(response);
        Assert.Equal("ollama-qwen3", response!.Id);
        Assert.Equal("chat.completion.chunk", response.Object);
        var delta = response.Choices[0].Delta!;
        Assert.Equal("assistant", delta.Role);
        Assert.Equal("Hel", delta.Content!.Content);
        Assert.Null(response.Choices[0].FinishReason);
        Assert.Null(response.Usage);
    }

    [Fact]
    public void TransformStreamChunkAddsUsageOnTheDoneChunk()
    {
        var response = Transformer().TransformStreamChunk(Event(
            """{"model":"qwen3","message":{"role":"assistant","content":""},"done":true,"prompt_eval_count":7,"eval_count":2}"""));

        Assert.Equal("stop", response!.Choices[0].FinishReason);
        Assert.Equal(7, response.Usage!.PromptTokens);
        Assert.Equal(2, response.Usage.CompletionTokens);
        Assert.Equal(9, response.Usage.TotalTokens);
    }

    [Fact]
    public void TransformStreamChunkWithDoneAndNoCountsOmitsUsage()
    {
        // Reference condition: done && (prompt_eval_count > 0 || eval_count > 0).
        var response = Transformer().TransformStreamChunk(Event(
            """{"model":"qwen3","message":{"role":"assistant","content":""},"done":true}"""));

        Assert.Equal("stop", response!.Choices[0].FinishReason);
        Assert.Null(response.Usage);
    }

    [Fact]
    public void TransformStreamChunkDropsEmptyEvents()
    {
        var transformer = Transformer();

        Assert.Null(transformer.TransformStreamChunk(null));
        Assert.Null(transformer.TransformStreamChunk(new StreamEvent { Data = [] }));
    }

    [Fact]
    public void TransformStreamChunkReportsMalformedChunksThroughTheTerminalError()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().TransformStreamChunk(Event("{not json")));

        Assert.StartsWith(
            "failed to unmarshal ollama stream chunk: ",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TransformStreamSkipsEmptyLinesAndMapsTheRest()
    {
        var transformer = Transformer();
        var events = new ListStream<StreamEvent>(
        [
            new StreamEvent { Data = Encoding.UTF8.GetBytes("""{"model":"qwen3","message":{"content":"Hel"}}""") },
            new StreamEvent { Data = [] },
            new StreamEvent { Data = Encoding.UTF8.GetBytes("""{"model":"qwen3","message":{"content":"lo"},"done":true}""") },
        ]);

        using var stream = transformer.TransformStream(new HttpRequestData(), events);
        var contents = new List<string?>();
        while (stream.Next())
        {
            contents.Add(stream.Current!.Choices[0].Delta!.Content!.Content);
        }

        Assert.Null(stream.Err);
        Assert.Equal(StreamedContents, contents);
    }

    // ------------------------------------------------------------------
    // Errors and aggregation
    // ------------------------------------------------------------------

    [Fact]
    public void TransformErrorUsesTheRawBodyAsTheMessage()
    {
        var error = Transformer().TransformError(new HttpClientError(
            "POST",
            "http://localhost:11434/api/chat",
            500,
            "500 Internal Server Error",
            Encoding.UTF8.GetBytes("model not found")));

        Assert.Equal(500, error.StatusCode);
        Assert.Equal("model not found", error.Detail.Message);
        Assert.Equal("ollama_error", error.Detail.Type);
    }

    [Fact]
    public void TransformErrorOfNilIsNil()
    {
        Assert.Null(Transformer().TransformError(null));
    }

    [Fact]
    public void AggregateStreamChunksConcatenatesContentAndThinking()
    {
        var transformer = Transformer();

        var body = transformer.AggregateStreamChunks(
            new HttpRequestData(),
            [
                Event("""{"model":"qwen3","message":{"role":"assistant","content":"Hel"},"done":false}"""),
                Event("""{"model":"qwen3","message":{"role":"assistant","content":"lo","thinking":"t1"},"done":false}"""),
                Event("""{"model":"qwen3","message":{"role":"assistant","content":"!","thinking":"t2"},"done":true,"done_reason":"stop","prompt_eval_count":7,"eval_count":2}"""),
            ],
            out var meta);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("ollama-qwen3", root.GetProperty("id").GetString());
        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        var message = root.GetProperty("choices")[0].GetProperty("message");
        Assert.Equal("Hello!", message.GetProperty("content").GetString());
        Assert.Equal("t1t2", message.GetProperty("reasoning_content").GetString());
        Assert.Equal("stop", root.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal(7, root.GetProperty("usage").GetProperty("prompt_tokens").GetInt64());
        Assert.Equal(2, root.GetProperty("usage").GetProperty("completion_tokens").GetInt64());
        Assert.Equal(9, root.GetProperty("usage").GetProperty("total_tokens").GetInt64());

        Assert.Equal("ollama-qwen3", meta.Id);
        Assert.Equal(9, meta.Usage!.TotalTokens);
    }

    [Fact]
    public void AggregateStreamChunksKeepsTheLastToolCallsAndForcesToolCallsReason()
    {
        var body = Transformer().AggregateStreamChunks(
            new HttpRequestData(),
            [
                Event("""{"model":"qwen3","message":{"content":""},"done":false}"""),
                Event("""{"model":"qwen3","message":{"content":"","tool_calls":[{"function":{"name":"lookup","arguments":{"b":1,"a":2}}}]},"done":true,"done_reason":"stop"}"""),
            ],
            out _);

        using var document = JsonDocument.Parse(body);
        var choice = document.RootElement.GetProperty("choices")[0];
        var toolCall = choice.GetProperty("message").GetProperty("tool_calls")[0];

        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
        Assert.Equal("lookup", toolCall.GetProperty("function").GetProperty("name").GetString());
        // llm.FunctionCall.Arguments is a string, so the unified response carries
        // the marshalled object as a JSON string (encoder stays Go-compatible).
        Assert.Equal(
            """{"a":2,"b":1}""",
            toolCall.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal("ollama-qwen3-0", toolCall.GetProperty("id").GetString());
        Assert.Equal(0, toolCall.GetProperty("index").GetInt32());
    }

    [Fact]
    public void AggregateStreamChunksRejectsAnEmptyChunkList()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().AggregateStreamChunks(new HttpRequestData(), [], out _));

        Assert.Equal("no chunks to aggregate", error.Message);
    }

    [Fact]
    public void AggregateStreamChunksRejectsMalformedChunks()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => Transformer().AggregateStreamChunks(
                new HttpRequestData(),
                [Event("{not json")],
                out _));

        Assert.StartsWith("failed to unmarshal ollama chunk: ", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ApiFormatIsOllamaChat()
    {
        Assert.Equal(ApiFormats.OllamaChat, Transformer().ApiFormat);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static OllamaOutboundTransformer Transformer(
        string baseUrl = "http://localhost:11434",
        string? apiKey = null,
        string endpointPath = "")
        => new(new OllamaOutboundConfig
        {
            BaseUrl = baseUrl,
            ApiKeyProvider = apiKey is null ? null : new StaticApiKeyProvider(apiKey),
            EndpointPath = endpointPath,
        });

    private static LlmRequest UserRequest()
        => new() { Model = "qwen3", Messages = [User("Hello")] };

    private static Message User(string text)
        => new() { Role = "user", Content = MessageContent.FromText(text) };

    private static HttpResponseData Response(string raw)
        => new() { StatusCode = 200, Body = Encoding.UTF8.GetBytes(raw) };

    private static StreamEvent Event(string raw)
        => new() { Data = Encoding.UTF8.GetBytes(raw) };

    private static JsonElement Json(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private static JsonElement Body(HttpRequestData request)
    {
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.Clone();
    }

    /// <summary>Minimal in-memory stream for TransformStream tests.</summary>
    private sealed class ListStream<T>(IReadOnlyList<T> items) : IStream<T>
        where T : class
    {
        private int _index = -1;

        public T? Current => _index >= 0 && _index < items.Count ? items[_index] : null;

        public Exception? Err => null;

        public bool Next() => ++_index < items.Count;

        public void Dispose()
        {
        }
    }
}
