using System.Text;
using System.Text.Json;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Persistence;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Gemini;
using AxonHub.Llm.Transformer.OpenAI;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Endpoint glue tests for the native Gemini surface
/// (<c>POST /v1beta/models/{model}:generateContent</c> and
/// <c>:streamGenerateContent</c>, <c>GET /v1beta/models</c> — routes.go:266-289).
/// The reference's GeminiHandlers reuse the chat handler with the gemini inbound
/// transformer, exactly like the port's Program.cs.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class GeminiEndpointTests(GrainClusterFixture fixture)
{
    private static readonly byte[] GenerateContentBody = Encoding.UTF8.GetBytes(
        """{"contents":[{"parts":[{"text":"Say hello"}]}]}""");

    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        RecordingMetricsRecorder metrics)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            metrics,
            new StaticRetryPolicyProvider(
                new RetryPolicy
                {
                    Enabled = true,
                    MaxChannelRetries = 3,
                    MaxSingleChannelRetries = 0,
                    RetryDelayMs = 0,
                }),
            fixture.CandidateSelector,
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

    private static DefaultHttpContext NewContext(string path, byte[] body, string contentType, string? query = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.test");
        context.Request.Path = path;
        context.Request.QueryString = query is null ? QueryString.Empty : new QueryString(query);
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static DefaultHttpContext NewGetContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.test");
        context.Request.Path = new PathString(path);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ResponseBody(DefaultHttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static ModelService NewService(params ChannelRecord[] channels)
        => ModelTestSupport.Service(channels);

    private static IManagementIdentity DeniedIdentity()
        => ModelTestSupport.ApiKeyIdentity(
            ModelTestSupport.ApiKey(scopes: "write_requests"));

    /// <summary>OpenAI-shaped upstream chunks plus the [DONE] marker.</summary>
    private static List<StreamEvent> StreamChunks()
        =>
        [
            TestChannels.Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,"
                + "\"delta\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":null}]}"),
            TestChannels.Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"gpt-4o\",\"choices\":[{\"index\":0,\"delta\":{},"
                + "\"finish_reason\":\"stop\"}],"
                + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}"),
            TestChannels.Data("[DONE]"),
        ];

    [Fact]
    public async Task GenerateContentForwardsAndReturnsGeminiResponse()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4920, "gpt-4o"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("gpt-4o"),
        };
        var context = NewContext(
            "/v1beta/models/gpt-4o:generateContent",
            GenerateContentBody,
            "application/json");
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await GeminiEndpoint.GenerateContentAsync(context, orchestrator, new GeminiInboundTransformer());

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using var body = JsonDocument.Parse(ResponseBody(context));
        var candidate = body.RootElement.GetProperty("candidates")[0];
        Assert.Equal(
            "hello",
            candidate.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal("STOP", candidate.GetProperty("finishReason").GetString());
        Assert.Equal(0, candidate.GetProperty("index").GetInt32());
        Assert.Equal("gpt-4o", body.RootElement.GetProperty("modelVersion").GetString());
        Assert.Equal(
            1L,
            body.RootElement.GetProperty("usageMetadata").GetProperty("promptTokenCount").GetInt64());

        var upstream = Assert.Single(executor.Requests);
        Assert.Equal("http://upstream.test/v1/chat/completions", upstream.Url);
        using var upstreamBody = JsonDocument.Parse(upstream.Body!);
        Assert.Equal("gpt-4o", upstreamBody.RootElement.GetProperty("model").GetString());
        Assert.Equal(
            "Say hello",
            upstreamBody.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public async Task GenerateContentStreamWritesJsonArrayFraming()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4921, "gpt-4o"));
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(StreamChunks()),
        };
        var context = NewContext(
            "/v1beta/models/gpt-4o:streamGenerateContent",
            GenerateContentBody,
            "application/json");
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await GeminiEndpoint.GenerateContentAsync(context, orchestrator, new GeminiInboundTransformer());

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json; charset=UTF-8", context.Response.ContentType);

        // WriteGeminiStream: "[", comma-separated event Data, "]".
        var body = ResponseBody(context);
        Assert.StartsWith("[", body, StringComparison.Ordinal);
        Assert.EndsWith("]", body, StringComparison.Ordinal);
        Assert.DoesNotContain("[DONE]", body, StringComparison.Ordinal);

        using var events = JsonDocument.Parse(body);
        Assert.Equal(2, events.RootElement.GetArrayLength());
        Assert.Equal(
            "hi",
            events.RootElement[0].GetProperty("candidates")[0]
                .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(
            "STOP",
            events.RootElement[1].GetProperty("candidates")[0].GetProperty("finishReason").GetString());
    }

    [Fact]
    public async Task GenerateContentStreamWithAltSseWritesSseFraming()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4922, "gpt-4o"));
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(StreamChunks()),
        };
        var context = NewContext(
            "/v1beta/models/gpt-4o:streamGenerateContent",
            GenerateContentBody,
            "application/json",
            query: "?alt=sse");
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await GeminiEndpoint.GenerateContentAsync(context, orchestrator, new GeminiInboundTransformer());

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("text/event-stream;charset=utf-8", context.Response.ContentType);
        Assert.Equal("*", context.Response.Headers["Access-Control-Allow-Origin"].ToString());

        // WriteSSEStream: "data:{json}\n\n" per event; the STOP chunk marks the
        // stream terminal, so no trailing error event is written.
        var body = ResponseBody(context);
        Assert.StartsWith("data:{", body, StringComparison.Ordinal);
        Assert.EndsWith("\n\n", body, StringComparison.Ordinal);
        Assert.Contains("finishReason", body, StringComparison.Ordinal);
        Assert.DoesNotContain("event:error", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateContentUnknownActionReturnsNotFound()
    {
        // TransformRequest rejects the unknown action before orchestration.
        fixture.Channels.Set();
        var context = NewContext(
            "/v1beta/models/gpt-4o:unknownAction",
            GenerateContentBody,
            "application/json");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await GeminiEndpoint.GenerateContentAsync(context, orchestrator, new GeminiInboundTransformer());

        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal(
            """{"error":{"code":404,"message":"invalid request URL: invalid request path: /v1beta/models/gpt-4o:unknownAction","status":"NOT_FOUND"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task ListModelsReturnsTheGeminiShape()
    {
        var context = NewGetContext("/v1beta/models");

        await GeminiEndpoint.ListModelsAsync(
            context,
            NewService(ModelTestSupport.Channel(1, "openai", "gpt-4o")));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal(
            """{"models":[{"name":"models/gpt-4o","baseModelId":"gpt-4o","version":"gpt-4o-0","displayName":"gpt-4o","description":"gpt-4o","supportedGenerationMethods":["generateContent","streamGenerateContent"]}]}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task ListModelsFailureReturnsGeminiInternalError()
    {
        var context = NewGetContext("/v1beta/models");
        var (service, _, modelStore) = ModelTestSupport.ServiceWithStores(
            [ModelTestSupport.Channel(1, "openai", "gpt-4o")]);
        modelStore.ListAllFailure = new InvalidOperationException("channel query failed");

        await GeminiEndpoint.ListModelsAsync(context, service);

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal(
            """{"error":{"code":500,"message":"failed to list configured models: channel query failed","status":"internal_server_error"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task ListModelsPermissionDeniedReturnsGeminiPermissionError()
    {
        // entprivacy.Deny is the 403 permission_error branch (C178).
        var context = NewGetContext("/v1beta/models");
        var service = ModelTestSupport.Service(
            [ModelTestSupport.Channel(1, "openai", "gpt-4o")],
            identity: DeniedIdentity());

        await GeminiEndpoint.ListModelsAsync(context, service);

        Assert.Equal(403, context.Response.StatusCode);
        Assert.Equal(
            """{"error":{"code":403,"message":"failed to list configured models: ent/privacy: deny rule","status":"permission_error"}}""",
            ResponseBody(context));
    }
}
