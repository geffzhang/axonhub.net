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
/// Endpoint glue tests for the OpenAI embeddings and moderations routes
/// (<c>POST /v1/embeddings</c> and <c>POST /v1/moderations</c>,
/// routes.go:211-212). Both routes reuse the chat handler with a dedicated
/// inbound transformer, exactly like api.OpenAIHandlers.CreateEmbedding /
/// CreateModeration; the transformers themselves are ported one-for-one in
/// AxonHub.Llm.Tests.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class EmbeddingModerationEndpointTests(GrainClusterFixture fixture)
{
    private static readonly double[] SampleVector = [0.1, 0.2, 0.3];

    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        RecordingMetricsRecorder metrics,
        RetryPolicy? policy = null)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            metrics,
            new StaticRetryPolicyProvider(
                policy ?? new RetryPolicy
                {
                    Enabled = true,
                    MaxChannelRetries = 3,
                    MaxSingleChannelRetries = 0,
                    RetryDelayMs = 0,
                }),
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

    /// <summary>Program.cs: <c>orchestrator.WithInbound(new EmbeddingInboundTransformer())</c>.</summary>
    private static ChatCompletionOrchestrator WithEmbedding(ChatCompletionOrchestrator orchestrator)
        => orchestrator.WithInbound(new EmbeddingInboundTransformer());

    /// <summary>Program.cs: <c>orchestrator.WithInbound(new ModerationInboundTransformer())</c>.</summary>
    private static ChatCompletionOrchestrator WithModeration(ChatCompletionOrchestrator orchestrator)
        => orchestrator.WithInbound(new ModerationInboundTransformer());

    private static DefaultHttpContext NewContext(string path, byte[] body, string contentType)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.test");
        context.Request.Path = path;
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static DefaultHttpContext NewJsonContext(string path, string body)
        => NewContext(path, Encoding.UTF8.GetBytes(body), "application/json");

    private static string ResponseBody(DefaultHttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static HttpResponseData JsonUpstreamResponse(string json)
        => new()
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(json),
        };

    private static void AssertJsonError(string body, string expectedType, string expectedMessage)
    {
        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(expectedType, error.GetProperty("type").GetString());
        Assert.Equal(expectedMessage, error.GetProperty("message").GetString());
    }

    // ---------------------------------------------------------------- embeddings

    [Fact]
    public async Task EmbeddingsEmptyBodyReturnsBadRequest()
    {
        // api.ChatCompletion's empty-body guard runs before the inbound
        // transform, so CreateEmbedding reports the generic JSON error.
        fixture.Channels.Set();
        var context = NewContext("/v1/embeddings", [], "application/json");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithEmbedding(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal(
            "{\"error\":{\"type\":\"Bad Request\",\"message\":\"Request body is empty\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task EmbeddingsForwardsJsonAndReturnsEmbeddingsResponse()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4911, "text-embedding-3-small"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => JsonUpstreamResponse(
                "{\"object\":\"list\",\"data\":[{\"object\":\"embedding\","
                + "\"embedding\":[0.1,0.2,0.3],\"index\":0}],"
                + "\"model\":\"text-embedding-3-small\","
                + "\"usage\":{\"prompt_tokens\":4,\"total_tokens\":4}}"),
        };
        var context = NewJsonContext(
            "/v1/embeddings",
            "{\"model\":\"text-embedding-3-small\",\"input\":\"hello\"}");
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithEmbedding(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using (var body = JsonDocument.Parse(ResponseBody(context)))
        {
            Assert.Equal("list", body.RootElement.GetProperty("object").GetString());
            var data = body.RootElement.GetProperty("data");
            Assert.Equal(1, data.GetArrayLength());
            Assert.Equal("embedding", data[0].GetProperty("object").GetString());
            Assert.Equal(
                SampleVector,
                data[0].GetProperty("embedding").EnumerateArray().Select(static e => e.GetDouble()));
            Assert.Equal("text-embedding-3-small", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(4L, body.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt64());
            Assert.Equal(4L, body.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt64());
        }

        var upstream = Assert.Single(executor.Requests);
        Assert.Equal("http://upstream.test/v1/embeddings", upstream.Url);
        Assert.Equal(ApiFormats.OpenAIEmbedding, upstream.ApiFormat);
        Assert.Equal(RequestTypes.Embedding, upstream.RequestType);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(upstream.Headers, "Content-Type"));

        using var upstreamBody = JsonDocument.Parse(upstream.Body!);
        Assert.Equal("text-embedding-3-small", upstreamBody.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello", upstreamBody.RootElement.GetProperty("input").GetString());
    }

    [Fact]
    public async Task EmbeddingsMissingInputReturnsInvalidRequest()
    {
        fixture.Channels.Set();
        var context = NewJsonContext(
            "/v1/embeddings",
            "{\"model\":\"text-embedding-3-small\"}");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithEmbedding(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        AssertJsonError(
            ResponseBody(context),
            "invalid_request_error",
            "invalid request: input cannot be empty string");
    }

    [Fact]
    public async Task EmbeddingsCountsEmbeddingResponseAsContentWhenEmptyDetectionIsOn()
    {
        // empty_response.go checks resp.Embedding before the chat choices;
        // without that branch an embeddings response would be retried as "empty".
        fixture.Channels.Set(TestChannels.OpenAi(4912, "text-embedding-3-small"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => JsonUpstreamResponse(
                "{\"object\":\"list\",\"data\":[{\"object\":\"embedding\","
                + "\"embedding\":[0.1],\"index\":0}],"
                + "\"model\":\"text-embedding-3-small\"}"),
        };
        var context = NewJsonContext(
            "/v1/embeddings",
            "{\"model\":\"text-embedding-3-small\",\"input\":\"hello\"}");
        var orchestrator = NewOrchestrator(
            executor,
            new RecordingMetricsRecorder(),
            new RetryPolicy
            {
                Enabled = true,
                MaxChannelRetries = 3,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
                EmptyResponseDetection = true,
            });

        await ChatCompletionEndpoint.HandleAsync(context, WithEmbedding(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Single(executor.Requests);
    }

    // ---------------------------------------------------------------- moderations

    [Fact]
    public async Task ModerationsDefaultsModelAndReturnsModerationResponse()
    {
        // moderation_inbound.go defaults an omitted model to omni-moderation-latest
        // before the channel lookup and the outbound body.
        fixture.Channels.Set(TestChannels.OpenAi(4913, "omni-moderation-latest"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => JsonUpstreamResponse(
                "{\"id\":\"modr-1\",\"model\":\"omni-moderation-latest\","
                + "\"results\":[{\"flagged\":false,"
                + "\"categories\":{\"violence\":false},"
                + "\"category_scores\":{\"violence\":0.01}}]}"),
        };
        var context = NewJsonContext("/v1/moderations", "{\"input\":\"hello\"}");
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithModeration(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using (var body = JsonDocument.Parse(ResponseBody(context)))
        {
            Assert.Equal("modr-1", body.RootElement.GetProperty("id").GetString());
            Assert.Equal("omni-moderation-latest", body.RootElement.GetProperty("model").GetString());
            var results = body.RootElement.GetProperty("results");
            Assert.Equal(1, results.GetArrayLength());
            Assert.False(results[0].GetProperty("flagged").GetBoolean());
            Assert.False(results[0].GetProperty("categories").GetProperty("violence").GetBoolean());
            Assert.Equal(0.01, results[0].GetProperty("category_scores").GetProperty("violence").GetDouble());
        }

        var upstream = Assert.Single(executor.Requests);
        Assert.Equal("http://upstream.test/v1/moderations", upstream.Url);
        Assert.Equal(ApiFormats.OpenAIModeration, upstream.ApiFormat);
        Assert.Equal(RequestTypes.Moderation, upstream.RequestType);

        using var upstreamBody = JsonDocument.Parse(upstream.Body!);
        Assert.Equal(
            "omni-moderation-latest",
            upstreamBody.RootElement.GetProperty("model").GetString());
        Assert.Equal("hello", upstreamBody.RootElement.GetProperty("input").GetString());
    }

    [Fact]
    public async Task ModerationsInvalidInputReturnsInvalidRequest()
    {
        fixture.Channels.Set();
        var context = NewJsonContext("/v1/moderations", "{\"input\":\"\"}");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithModeration(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        AssertJsonError(
            ResponseBody(context),
            "invalid_request_error",
            "invalid request: input cannot be empty string");
    }

    [Fact]
    public async Task ModerationsCountsModerationResponseAsContentWhenEmptyDetectionIsOn()
    {
        // empty_response.go checks resp.Moderation first, before the chat choices.
        fixture.Channels.Set(TestChannels.OpenAi(4914, "omni-moderation-latest"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => JsonUpstreamResponse(
                "{\"id\":\"modr-2\",\"model\":\"omni-moderation-latest\","
                + "\"results\":[{\"flagged\":false,"
                + "\"categories\":{\"violence\":false},"
                + "\"category_scores\":{\"violence\":0.01}}]}"),
        };
        var context = NewJsonContext("/v1/moderations", "{\"input\":\"hello\"}");
        var orchestrator = NewOrchestrator(
            executor,
            new RecordingMetricsRecorder(),
            new RetryPolicy
            {
                Enabled = true,
                MaxChannelRetries = 3,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
                EmptyResponseDetection = true,
            });

        await ChatCompletionEndpoint.HandleAsync(context, WithModeration(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Single(executor.Requests);
    }
}
