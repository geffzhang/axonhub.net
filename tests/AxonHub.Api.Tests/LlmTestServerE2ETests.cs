using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Abstractions;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Real-provider end-to-end slice: the inbound request, the orchestrator, the
/// outbound transformer and <see cref="HttpExecutor"/> all run for real, and
/// the only stub is the provider itself, which is the deterministic
/// <c>LlmTestServer</c> (E:\GitHub\LlmTestServer).
/// </summary>
/// <remarks>
/// Opt-in because a test run must not depend on an external process. Start the
/// server from its repository:
/// <code>
/// LlmTestServer__RequiredBearerToken=test-key dotnet run \
///   --project samples/ManagedCode.LlmTestServer.Service --urls http://localhost:8080
/// </code>
/// then set <c>AXONHUB_E2E_LLM_TEST_SERVER_URL=http://localhost:8080</c>
/// (and optionally <c>AXONHUB_E2E_LLM_TEST_SERVER_KEY</c>, default
/// <c>test-key</c>) before <c>dotnet test</c>.
/// </remarks>
[Collection(OrleansCollection.Name)]
public sealed class LlmTestServerE2ETests(GrainClusterFixture fixture)
{
    internal static string ServerUrl => (Environment.GetEnvironmentVariable(
        "AXONHUB_E2E_LLM_TEST_SERVER_URL") ?? string.Empty).TrimEnd('/');

    internal static string ApiKey
    {
        get
        {
            var key = Environment.GetEnvironmentVariable("AXONHUB_E2E_LLM_TEST_SERVER_KEY");
            return string.IsNullOrEmpty(key) ? "test-key" : key;
        }
    }

    private static readonly JsonSerializerOptions ConfigurationJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [LlmTestServerFact]
    public async Task NonStreamChatRoundTripsThroughRealHttpExecutor()
    {
        const string model = "axonhub-e2e-chat";
        const string content = "Hello from LlmTestServer.";
        await ConfigureAsync(model, content, streamChunks: null);

        fixture.Channels.Set(NewChannel(9901, model));
        using var http = NewHttpClient();
        var context = NewContext(
            "{\"model\":\"" + model + "\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}");
        var orchestrator = NewOrchestrator(new HttpExecutor(http));

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using var body = JsonDocument.Parse(ResponseBody(context));
        var root = body.RootElement;
        Assert.Equal("chat.completion", root.GetProperty("object").GetString());
        Assert.Equal(model, root.GetProperty("model").GetString());
        Assert.Equal(
            content,
            root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [LlmTestServerFact]
    public async Task StreamingChatRoundTripsEveryProviderChunk()
    {
        const string model = "axonhub-e2e-stream";
        const string content = "Hello from LlmTestServer.";
        await ConfigureAsync(model, content, ["Hello ", "from ", "LlmTestServer."]);

        fixture.Channels.Set(NewChannel(9902, model));
        using var http = NewHttpClient();
        var context = NewContext(
            "{\"model\":\"" + model + "\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],"
            + "\"stream\":true}");
        var orchestrator = NewOrchestrator(new HttpExecutor(http));

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        Assert.Equal("text/event-stream;charset=utf-8", context.Response.ContentType);

        var body = ResponseBody(context);
        Assert.EndsWith("data:[DONE]\n\n", body, StringComparison.Ordinal);
        Assert.Equal(content, Deltas(body));
    }

    [LlmTestServerFact]
    public async Task AnthropicInboundMapsOpenAiUpstreamIntoMessageEnvelope()
    {
        const string model = "axonhub-e2e-anthropic";
        const string content = "Hello from LlmTestServer.";
        await ConfigureAsync(model, content, streamChunks: null);

        fixture.Channels.Set(NewChannel(9903, model));
        using var http = NewHttpClient();
        var context = NewContext(
            "{\"model\":\"" + model + "\",\"max_tokens\":64,"
            + "\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}",
            path: "/v1/messages");
        var orchestrator = NewOrchestrator(new HttpExecutor(http))
            .WithInbound(new AnthropicInboundTransformer());

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);

        using var body = JsonDocument.Parse(ResponseBody(context));
        var root = body.RootElement;
        Assert.Equal("message", root.GetProperty("type").GetString());
        Assert.Equal("assistant", root.GetProperty("role").GetString());
        Assert.Equal(model, root.GetProperty("model").GetString());
        Assert.Equal("text", root.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal(content, root.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("end_turn", root.GetProperty("stop_reason").GetString());
    }

    private ChatCompletionOrchestrator NewOrchestrator(IExecutor executor)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(new RetryPolicy
            {
                MaxChannelRetries = 0,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
            }),
            fixture.CandidateSelector,
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

    private static ChannelDescriptor NewChannel(long id, string model)
        => TestChannels.OpenAi(
            id,
            model,
            apiKey: ApiKey,
            baseUrl: ServerUrl + "/openai/v1");

    private static HttpClient NewHttpClient()
        => new(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            // llm/httpclient/client.go:106: the reference client has no total
            // timeout; per-attempt timeouts are applied by the pipeline.
            Timeout = Timeout.InfiniteTimeSpan,
        };

    private static DefaultHttpContext NewContext(string body, string path = "/v1/chat/completions")
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

    /// <summary>Concatenates the OpenAI stream deltas carried in the SSE body.</summary>
    private static string Deltas(string sse)
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
            var delta = chunk.RootElement.GetProperty("choices")[0].GetProperty("delta");
            if (delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                text.Append(content.GetString());
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// Registers one model and one queued reply. Configuration replaces the
    /// runtime state, so every test starts from a fresh scenario queue.
    /// </summary>
    private static async Task ConfigureAsync(
        string model,
        string content,
        IReadOnlyList<string>? streamChunks)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        var configuration = new
        {
            requiredBearerToken = ApiKey,
            models = new[] { new { id = model, kind = "chat" } },
            chatScenarios = new[]
            {
                new
                {
                    id = "axonhub-e2e",
                    modelId = model,
                    responses = new[] { new { content, streamChunks } },
                },
            },
        };

        using var response = await http.PostAsJsonAsync(
            ServerUrl + "/admin-api/configure",
            configuration,
            ConfigurationJson);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.IsSuccessStatusCode,
            "LlmTestServer configure failed: " + (int)response.StatusCode + " " + body);
        Assert.Contains("configured", body, StringComparison.Ordinal);
    }
}

/// <summary>
/// Skips the real-provider tests unless <c>AXONHUB_E2E_LLM_TEST_SERVER_URL</c>
/// points at a running LlmTestServer.
/// </summary>
public sealed class LlmTestServerFactAttribute : FactAttribute
{
    public LlmTestServerFactAttribute()
    {
        if (LlmTestServerE2ETests.ServerUrl.Length == 0)
        {
            Skip = "set AXONHUB_E2E_LLM_TEST_SERVER_URL (e.g. http://localhost:8080) "
                + "and start E:\\GitHub\\LlmTestServer to run the real-provider E2E";
        }
    }
}
