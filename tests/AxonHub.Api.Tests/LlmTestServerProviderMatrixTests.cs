using System.Text;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Real-provider matrix over the local <c>LlmTestServer</c>
/// (E:\GitHub\LlmTestServer): inbound protocol × upstream protocol ×
/// non-stream/stream, plus embeddings, images, tool calls and channel retry.
/// The gateway, orchestrator, transformers and <see cref="HttpExecutor"/> run
/// for real; the server supplies deterministic provider wire responses.
/// </summary>
/// <remarks>
/// Opt-in like <see cref="LlmTestServerE2ETests"/>: start the server and set
/// <c>AXONHUB_E2E_LLM_TEST_SERVER_URL</c>.
/// </remarks>
[Collection(OrleansCollection.Name)]
public sealed class LlmTestServerProviderMatrixTests(GrainClusterFixture fixture)
{
    private const string ChatPath = "/v1/chat/completions";
    private const string AnthropicPath = "/v1/messages";
    private const string ResponsesPath = "/v1/responses";
    private const string EmbeddingsPath = "/v1/embeddings";
    private const string ImagesPath = "/v1/images/generations";

    private static string ServerUrl => LlmTestServerE2ETests.ServerUrl;
    private static string ApiKey => LlmTestServerE2ETests.ApiKey;

    [LlmTestServerFact]
    public async Task AnthropicUpstreamRoundTripsAnthropicInboundNonStream()
    {
        const string model = "matrix-anthropic-in";
        const string content = "Anthropic upstream non-stream.";
        await ConfigureChatAsync(model, content);

        var context = await SendAsync(
            AnthropicPath,
            $$"""{"model":{{JsonSerializer.Serialize(model)}},"max_tokens":64,"messages":[{"role":"user","content":"hi"}]}""",
            new AnthropicInboundTransformer(),
            AnthropicChannel(9941, model));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var body = JsonDocument.Parse(ResponseBody(context));
        var root = body.RootElement;
        Assert.Equal("message", root.GetProperty("type").GetString());
        Assert.Equal("assistant", root.GetProperty("role").GetString());
        Assert.Equal(model, root.GetProperty("model").GetString());
        Assert.Equal("text", root.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal(content, root.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("end_turn", root.GetProperty("stop_reason").GetString());
        Assert.True(root.GetProperty("usage").GetProperty("input_tokens").GetInt64() >= 0);
        Assert.True(root.GetProperty("usage").GetProperty("output_tokens").GetInt64() >= 0);
    }

    [LlmTestServerFact]
    public async Task AnthropicUpstreamRoundTripsAnthropicInboundStream()
    {
        const string model = "matrix-anthropic-stream";
        const string content = "Anthropic upstream stream.";
        await ConfigureChatAsync(model, content, ["Anthropic ", "upstream ", "stream."]);

        var context = await SendAsync(
            AnthropicPath,
            $$"""{"model":{{JsonSerializer.Serialize(model)}},"max_tokens":64,"stream":true,"messages":[{"role":"user","content":"hi"}]}""",
            new AnthropicInboundTransformer(),
            AnthropicChannel(9942, model));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("text/event-stream;charset=utf-8", context.Response.ContentType);

        var (text, completed) = AnthropicStream(ResponseBody(context));
        Assert.Equal(content, text);
        Assert.True(completed, "Anthropic stream must end with message_stop");
    }

    [LlmTestServerFact]
    public async Task OpenAiInboundCrossesIntoTheAnthropicUpstream()
    {
        const string model = "matrix-anthropic-cross";
        const string content = "OpenAI inbound, Anthropic upstream.";
        await ConfigureChatAsync(model, content);

        var context = await SendAsync(
            ChatPath,
            $$"""{"model":{{JsonSerializer.Serialize(model)}},"max_tokens":64,"messages":[{"role":"user","content":"hi"}]}""",
            new OpenAiInboundTransformer(),
            AnthropicChannel(9943, model));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var body = JsonDocument.Parse(ResponseBody(context));
        Assert.Equal(
            content,
            body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.True(body.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt64() > 0);
    }

    [LlmTestServerFact]
    public async Task GeminiUpstreamRoundTripsOpenAiInboundNonStream()
    {
        const string model = "matrix-gemini-in";
        const string content = "Gemini upstream non-stream.";
        await ConfigureChatAsync(model, content);

        var context = await SendAsync(
            ChatPath,
            $$"""{"model":{{JsonSerializer.Serialize(model)}},"messages":[{"role":"user","content":"hi"}]}""",
            new OpenAiInboundTransformer(),
            GeminiChannel(9944, model));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var body = JsonDocument.Parse(ResponseBody(context));
        var root = body.RootElement;
        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        Assert.Equal(
            content,
            root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.True(root.GetProperty("usage").GetProperty("total_tokens").GetInt64() > 0);
    }

    [LlmTestServerFact]
    public async Task GeminiUpstreamRoundTripsOpenAiInboundStream()
    {
        const string model = "matrix-gemini-stream";
        const string content = "Gemini upstream stream.";
        await ConfigureChatAsync(model, content, ["Gemini ", "upstream ", "stream."]);

        var context = await SendAsync(
            ChatPath,
            $$"""{"model":{{JsonSerializer.Serialize(model)}},"stream":true,"messages":[{"role":"user","content":"hi"}]}""",
            new OpenAiInboundTransformer(),
            GeminiChannel(9945, model));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("text/event-stream;charset=utf-8", context.Response.ContentType);
        var body = ResponseBody(context);
        Assert.EndsWith("data:[DONE]\n\n", body, StringComparison.Ordinal);
        Assert.Equal(content, OpenAiDeltas(body));
    }

    [LlmTestServerFact]
    public async Task ResponsesUpstreamRoundTripsResponsesInboundNonStream()
    {
        const string model = "matrix-responses-in";
        const string content = "Responses upstream non-stream.";
        await ConfigureChatAsync(model, content);

        var context = await SendAsync(
            ResponsesPath,
            $$"""{"model":{{JsonSerializer.Serialize(model)}},"input":"hi"}""",
            new ResponsesInboundTransformer(),
            ResponsesChannel(9946, model));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var body = JsonDocument.Parse(ResponseBody(context));
        var root = body.RootElement;
        Assert.Equal("response", root.GetProperty("object").GetString());
        Assert.Equal("completed", root.GetProperty("status").GetString());
        Assert.Equal(content, ResponsesText(root));
        Assert.True(root.GetProperty("usage").GetProperty("total_tokens").GetInt64() > 0);
    }

    [LlmTestServerFact]
    public async Task ResponsesUpstreamRoundTripsResponsesInboundStream()
    {
        const string model = "matrix-responses-stream";
        const string content = "Responses upstream stream.";
        await ConfigureChatAsync(model, content, ["Responses ", "upstream ", "stream."]);

        var context = await SendAsync(
            ResponsesPath,
            $$"""{"model":{{JsonSerializer.Serialize(model)}},"stream":true,"input":"hi"}""",
            new ResponsesInboundTransformer(),
            ResponsesChannel(9947, model));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("text/event-stream;charset=utf-8", context.Response.ContentType);
        var (text, completed) = ResponsesStream(ResponseBody(context));
        Assert.Equal(content, text);
        Assert.True(completed, "Responses stream must emit response.completed");
    }

    [LlmTestServerFact]
    public async Task EmbeddingsEndpointRoundTripsThroughTheOpenAiUpstream()
    {
        const string model = "matrix-embedding";
        var vector = new[] { 0.125f, 0.25f, 0.5f };
        await ConfigureAsync(
            [new { id = model, kind = "embedding" }],
            [],
            new Dictionary<string, object?> { ["defaultEmbeddingVector"] = vector });

        var context = await SendAsync(
            EmbeddingsPath,
            $$"""{"model":{{JsonSerializer.Serialize(model)}},"input":"hello"}""",
            new EmbeddingInboundTransformer(),
            OpenAiChannel(9948, model));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var body = JsonDocument.Parse(ResponseBody(context));
        var root = body.RootElement;
        Assert.Equal("list", root.GetProperty("object").GetString());
        Assert.Equal(model, root.GetProperty("model").GetString());
        var embedding = root.GetProperty("data")[0].GetProperty("embedding");
        Assert.Equal(3, embedding.GetArrayLength());
        for (var i = 0; i < vector.Length; i++)
        {
            Assert.Equal(vector[i], embedding[i].GetSingle(), 6);
        }

        Assert.True(root.GetProperty("usage").GetProperty("prompt_tokens").GetInt64() > 0);
    }

    [LlmTestServerFact]
    public async Task ImageGenerationEndpointRoundTripsThroughTheOpenAiUpstream()
    {
        const string model = "matrix-image";
        await ConfigureAsync([new { id = model, kind = "image" }], []);

        var context = await SendAsync(
            ImagesPath,
            $$"""{"model":{{JsonSerializer.Serialize(model)}},"prompt":"a red fox","response_format":"b64_json"}""",
            ImageInboundTransformer.Generation(),
            OpenAiChannel(9949, model));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var body = JsonDocument.Parse(ResponseBody(context));
        var data = body.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        var bytes = Convert.FromBase64String(data[0].GetProperty("b64_json").GetString()!);
        Assert.True(bytes.Length > 8);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, bytes[..4]);
    }

    [LlmTestServerFact]
    public async Task OpenAiChatToolCallRoundTrips()
    {
        const string model = "matrix-tool";
        await ConfigureAsync(
            [new { id = model, kind = "chat" }],
            [
                new
                {
                    id = "matrix-tool-scenario",
                    modelId = model,
                    responses = new object[]
                    {
                        new
                        {
                            content = string.Empty,
                            toolCalls = new[]
                            {
                                new
                                {
                                    id = "call_weather",
                                    name = "weather",
                                    argumentsJson = """{"city":"Paris"}""",
                                },
                            },
                        },
                    },
                },
            ]);

        var requestBody = JsonSerializer.Serialize(new
        {
            model,
            messages = new[] { new { role = "user", content = "weather?" } },
            tools = new[]
            {
                new
                {
                    type = "function",
                    function = new
                    {
                        name = "weather",
                        description = "Weather",
                        parameters = new
                        {
                            type = "object",
                            properties = new { city = new { type = "string" } },
                            required = new[] { "city" },
                        },
                    },
                },
            },
        });

        var context = await SendAsync(
            ChatPath,
            requestBody,
            new OpenAiInboundTransformer(),
            OpenAiChannel(9950, model));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var body = JsonDocument.Parse(ResponseBody(context));
        var choice = body.RootElement.GetProperty("choices")[0];
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
        var call = choice.GetProperty("message").GetProperty("tool_calls")[0];
        Assert.Equal("call_weather", call.GetProperty("id").GetString());
        Assert.Equal("weather", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("Paris", JsonDocument
            .Parse(call.GetProperty("function").GetProperty("arguments").GetString()!)
            .RootElement.GetProperty("city").GetString());
    }

    [LlmTestServerFact]
    public async Task GroqUpstreamRoundTripsOpenAiInboundNonStream()
    {
        const string model = "matrix-groq-in";
        const string content = "Groq upstream non-stream.";
        await ConfigureChatAsync(model, content);

        var context = await SendAsync(
            ChatPath,
            $$"""{"model":{{JsonSerializer.Serialize(model)}},"messages":[{"role":"user","content":"hi"}]}""",
            new OpenAiInboundTransformer(),
            Channel(9953, "groq", model, ServerUrl + "/groq/openai/v1"));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var body = JsonDocument.Parse(ResponseBody(context));
        Assert.Equal(
            content,
            body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.True(body.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt64() > 0);
    }

    [LlmTestServerFact]
    public async Task OllamaUpstreamRoundTripsOpenAiInboundNonStream()
    {
        const string model = "matrix-ollama-in";
        const string content = "Ollama upstream non-stream.";
        await ConfigureChatAsync(model, content);

        var context = await SendAsync(
            ChatPath,
            $$"""{"model":{{JsonSerializer.Serialize(model)}},"messages":[{"role":"user","content":"hi"}]}""",
            new OpenAiInboundTransformer(),
            Channel(9954, "ollama", model, ServerUrl + "/ollama"));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var body = JsonDocument.Parse(ResponseBody(context));
        Assert.Equal(
            content,
            body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.True(body.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt64() > 0);
    }

    [LlmTestServerFact]
    public async Task DeepSeekUpstreamRoundTripsOpenAiInboundNonStream()
    {
        const string model = "matrix-deepseek-in";
        const string content = "DeepSeek upstream non-stream.";
        await ConfigureChatAsync(model, content);

        var context = await SendAsync(
            ChatPath,
            $$"""{"model":{{JsonSerializer.Serialize(model)}},"messages":[{"role":"user","content":"hi"}]}""",
            new OpenAiInboundTransformer(),
            Channel(9955, "deepseek", model, ServerUrl + "/deepseek/v1"));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var body = JsonDocument.Parse(ResponseBody(context));
        Assert.Equal(
            content,
            body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.True(body.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt64() > 0);
    }

    [LlmTestServerFact]
    public async Task OpenRouterUpstreamRoundTripsOpenAiInboundNonStream()
    {
        const string model = "matrix-openrouter-in";
        const string content = "OpenRouter upstream non-stream.";
        await ConfigureChatAsync(model, content);

        var context = await SendAsync(
            ChatPath,
            $$"""{"model":{{JsonSerializer.Serialize(model)}},"messages":[{"role":"user","content":"hi"}]}""",
            new OpenAiInboundTransformer(),
            Channel(9956, "openrouter", model, ServerUrl + "/openrouter/api/v1"));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var body = JsonDocument.Parse(ResponseBody(context));
        Assert.Equal(
            content,
            body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        Assert.True(body.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt64() > 0);
    }

    [LlmTestServerFact]
    public async Task AChannelThatReturns429IsReplacedByTheNextCandidate()
    {
        const string model = "matrix-retry";
        const string content = "Recovered on channel two.";
        await ConfigureAsync(
            [new { id = model, kind = "chat" }],
            [
                new
                {
                    id = "matrix-retry-scenario",
                    modelId = model,
                    responses = new object[]
                    {
                        new
                        {
                            error = new
                            {
                                statusCode = 429,
                                code = "rate_limit_exceeded",
                                message = "retry on another channel",
                            },
                        },
                        new { content },
                    },
                },
            ]);

        var context = await SendAsync(
            ChatPath,
            $$"""{"model":{{JsonSerializer.Serialize(model)}},"messages":[{"role":"user","content":"hi"}]}""",
            new OpenAiInboundTransformer(),
            new RetryPolicy
            {
                Enabled = true,
                MaxChannelRetries = 1,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
            },
            OpenAiChannel(9951, model),
            OpenAiChannel(9952, model));

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        using var body = JsonDocument.Parse(ResponseBody(context));
        Assert.Equal(
            content,
            body.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    // -- helpers ------------------------------------------------------------

    private async Task<DefaultHttpContext> SendAsync(
        string path,
        string body,
        IInboundTransformer inbound,
        params ChannelDescriptor[] channels)
        => await SendAsync(path, body, inbound, retryPolicy: null, channels);

    private async Task<DefaultHttpContext> SendAsync(
        string path,
        string body,
        IInboundTransformer inbound,
        RetryPolicy? retryPolicy,
        params ChannelDescriptor[] channels)
    {
        fixture.Channels.Set(channels);
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        var context = NewContext(body, path);
        var orchestrator = new ChatCompletionOrchestrator(
            fixture.Cluster.GrainFactory,
            inbound,
            new HttpExecutor(http),
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(retryPolicy ?? new RetryPolicy
            {
                MaxChannelRetries = 0,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
            }),
            fixture.CandidateSelector,
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);
        return context;
    }

    private static DefaultHttpContext NewContext(string body, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.test");
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ResponseBody(DefaultHttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static ChannelDescriptor OpenAiChannel(long id, string model)
        => Channel(id, "openai", model, ServerUrl + "/openai/v1");

    private static ChannelDescriptor AnthropicChannel(long id, string model)
        => Channel(id, "anthropic", model, ServerUrl + "/anthropic");

    private static ChannelDescriptor GeminiChannel(long id, string model)
        => Channel(id, "gemini", model, ServerUrl + "/gemini");

    private static ChannelDescriptor ResponsesChannel(long id, string model)
        => Channel(id, "openai_responses", model, ServerUrl + "/openai/v1");

    private static ChannelDescriptor Channel(long id, string type, string model, string baseUrl) => new()
    {
        Id = id,
        Name = "matrix-channel-" + id,
        OrderingWeight = 0,
        Type = type,
        BaseUrl = baseUrl,
        Credentials = "{\"apiKey\":" + JsonSerializer.Serialize(ApiKey) + "}",
        SupportedModels = "[" + JsonSerializer.Serialize(model) + "]",
    };

    private static async Task ConfigureChatAsync(
        string model,
        string content,
        IReadOnlyList<string>? streamChunks = null)
        => await ConfigureAsync(
            [new { id = model, kind = "chat" }],
            [
                new
                {
                    id = "matrix-" + model,
                    modelId = model,
                    responses = new[] { new { content, streamChunks } },
                },
            ]);

    private static async Task ConfigureAsync(
        object[] models,
        object[] chatScenarios,
        Dictionary<string, object?>? extras = null)
    {
        var configuration = new Dictionary<string, object?>
        {
            ["requiredBearerToken"] = ApiKey,
            ["models"] = models,
            ["chatScenarios"] = chatScenarios,
        };
        if (extras is not null)
        {
            foreach (var (key, value) in extras)
            {
                configuration[key] = value;
            }
        }

        await LlmTestServerE2ETests.AdminClient.ConfigureAsync(configuration);
    }

    private static string OpenAiDeltas(string sse)
    {
        var text = new StringBuilder();
        foreach (var raw in sse.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
            {
                continue;
            }

            using var chunk = JsonDocument.Parse(payload);
            if (!chunk.RootElement.TryGetProperty("choices", out var choices)
                || choices.GetArrayLength() == 0
                || !choices[0].TryGetProperty("delta", out var delta)
                || !delta.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            text.Append(content.GetString());
        }

        return text.ToString();
    }

    private static (string Text, bool Completed) AnthropicStream(string sse)
    {
        var text = new StringBuilder();
        var completed = false;
        foreach (var raw in sse.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
            {
                continue;
            }

            using var chunk = JsonDocument.Parse(payload);
            var type = chunk.RootElement.GetProperty("type").GetString();
            if (type == "message_stop")
            {
                completed = true;
            }
            else if (type == "content_block_delta"
                && chunk.RootElement.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("type", out var deltaType)
                && deltaType.GetString() == "text_delta"
                && delta.TryGetProperty("text", out var textValue))
            {
                text.Append(textValue.GetString());
            }
        }

        return (text.ToString(), completed);
    }

    private static (string Text, bool Completed) ResponsesStream(string sse)
    {
        var text = new StringBuilder();
        var completed = false;
        foreach (var raw in sse.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var payload = line["data:".Length..].Trim();
            if (payload.Length == 0 || payload == "[DONE]")
            {
                continue;
            }

            using var chunk = JsonDocument.Parse(payload);
            var type = chunk.RootElement.GetProperty("type").GetString();
            if (type == "response.completed")
            {
                completed = true;
            }
            else if (type == "response.output_text.delta"
                && chunk.RootElement.TryGetProperty("delta", out var delta))
            {
                text.Append(delta.GetString());
            }
        }

        return (text.ToString(), completed);
    }

    private static string ResponsesText(JsonElement response)
    {
        var text = new StringBuilder();
        foreach (var item in response.GetProperty("output").EnumerateArray())
        {
            if (item.GetProperty("type").GetString() != "message"
                || !item.TryGetProperty("content", out var contents))
            {
                continue;
            }

            foreach (var content in contents.EnumerateArray())
            {
                if (content.GetProperty("type").GetString() == "output_text"
                    && content.TryGetProperty("text", out var value))
                {
                    text.Append(value.GetString());
                }
            }
        }

        return text.ToString();
    }
}
