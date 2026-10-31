using System.Text;
using System.Text.Json;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Endpoint glue test for <c>POST /v1/videos</c> (routes.go:217;
/// api.OpenAIHandlers.CreateVideo in internal/server/api/openai.go:489-517).
/// The route reuses the shared chat handler with the video inbound transformer,
/// exactly like the images and rerank routes; the transformers themselves are
/// ported one-for-one in AxonHub.Llm.Tests.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class VideoEndpointTests(GrainClusterFixture fixture)
{
    private ChatCompletionOrchestrator NewOrchestrator(ScriptedExecutor executor)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(new RetryPolicy
            {
                Enabled = true,
                MaxChannelRetries = 3,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
            }),
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

    /// <summary>Program.cs: <c>orchestrator.WithInbound(new VideoInboundTransformer())</c>.</summary>
    private static ChatCompletionOrchestrator WithVideo(ChatCompletionOrchestrator orchestrator)
        => orchestrator.WithInbound(new VideoInboundTransformer());

    private static DefaultHttpContext NewJsonContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.test");
        context.Request.Path = "/v1/videos";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ResponseBody(DefaultHttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    [Fact]
    public async Task VideoEmptyBodyReturnsBadRequest()
    {
        fixture.Channels.Set();
        var context = NewJsonContext(string.Empty);
        var orchestrator = NewOrchestrator(new ScriptedExecutor());

        await ChatCompletionEndpoint.HandleAsync(context, WithVideo(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"type\":\"Bad Request\",\"message\":\"Request body is empty\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task VideoMissingModelReturnsInvalidRequestError()
    {
        fixture.Channels.Set(TestChannels.OpenAi(1001, "sora-2"));
        var context = NewJsonContext("{\"prompt\":\"a cat walking\"}");
        var orchestrator = NewOrchestrator(new ScriptedExecutor());

        await ChatCompletionEndpoint.HandleAsync(context, WithVideo(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"message\":\"invalid request: model is required\",\"type\":\"invalid_request_error\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task VideoMissingPromptReturnsInvalidRequestError()
    {
        fixture.Channels.Set(TestChannels.OpenAi(1001, "sora-2"));
        var context = NewJsonContext("{\"model\":\"sora-2\"}");
        var orchestrator = NewOrchestrator(new ScriptedExecutor());

        await ChatCompletionEndpoint.HandleAsync(context, WithVideo(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"message\":\"invalid request: prompt is required\",\"type\":\"invalid_request_error\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task VideoCreatesTaskViaOpenAiChannel()
    {
        fixture.Channels.Set(TestChannels.OpenAi(1001, "sora-2"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = 200,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Content-Type"] = ["application/json"],
                },
                Body = Encoding.UTF8.GetBytes(
                    "{\"id\":\"video_1\",\"object\":\"video\",\"status\":\"queued\","
                    + "\"created_at\":1700000000,\"progress\":0}"),
            },
        };
        var context = NewJsonContext("{\"model\":\"sora-2\",\"prompt\":\"a cat walking\"}");
        var orchestrator = NewOrchestrator(executor);

        await ChatCompletionEndpoint.HandleAsync(context, WithVideo(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using (var body = JsonDocument.Parse(ResponseBody(context)))
        {
            Assert.Equal("video_1", body.RootElement.GetProperty("id").GetString());
            Assert.Equal("video", body.RootElement.GetProperty("object").GetString());
            Assert.Equal("queued", body.RootElement.GetProperty("status").GetString());
            // The model and prompt come from the outbound request metadata; the
            // reference renders them on the wire object the same way.
            Assert.Equal("sora-2", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("a cat walking", body.RootElement.GetProperty("prompt").GetString());
        }

        var upstream = Assert.Single(executor.Requests);
        Assert.Equal("POST", upstream.Method);
        Assert.Equal("http://upstream.test/v1/videos", upstream.Url);
        Assert.Equal(ApiFormats.OpenAIVideo, upstream.ApiFormat);
        Assert.Equal(RequestTypes.Video, upstream.RequestType);
        Assert.Equal("Bearer sk-test", HttpClientUtils.HeaderGet(upstream.Headers, "Authorization"));
        Assert.Equal(
            "{\"model\":\"sora-2\",\"prompt\":\"a cat walking\"}",
            Encoding.UTF8.GetString(upstream.Body!));
    }
}
