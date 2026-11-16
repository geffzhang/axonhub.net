using System.Text;
using System.Text.Json;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Jina;
using AxonHub.Llm.Transformer.OpenAI;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Endpoint glue tests for the jina rerank and embedding routes
/// (<c>POST /v1/rerank</c>, <c>POST /jina/v1/rerank</c> and
/// <c>POST /jina/v1/embeddings</c>, routes.go:230,238-239). All three routes
/// reuse the chat handler with a dedicated inbound transformer, exactly like
/// api.JinaHandlers; the transformers themselves are ported one-for-one in
/// AxonHub.Llm.Tests.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class RerankEndpointTests(GrainClusterFixture fixture)
{
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
            fixture.CandidateSelector,
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

    /// <summary>Program.cs: <c>orchestrator.WithInbound(new JinaRerankInboundTransformer())</c>.</summary>
    private static ChatCompletionOrchestrator WithRerank(ChatCompletionOrchestrator orchestrator)
        => orchestrator.WithInbound(new JinaRerankInboundTransformer());

    /// <summary>Program.cs: <c>orchestrator.WithInbound(new JinaEmbeddingInboundTransformer())</c>.</summary>
    private static ChatCompletionOrchestrator WithJinaEmbedding(ChatCompletionOrchestrator orchestrator)
        => orchestrator.WithInbound(new JinaEmbeddingInboundTransformer());

    private static DefaultHttpContext NewJsonContext(string path, string body)
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

    // ------------------------------------------------------------------ rerank

    [Fact]
    public async Task RerankEmptyBodyReturnsBadRequest()
    {
        // api.ChatCompletion's empty-body guard runs before the inbound
        // transform, so Rerank reports the generic JSON error.
        fixture.Channels.Set();
        var context = NewJsonContext("/v1/rerank", string.Empty);
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithRerank(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"type\":\"Bad Request\",\"message\":\"Request body is empty\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task RerankValidationErrorReturnsInternalApiError()
    {
        // The reference rerank inbound TransformError only special-cases
        // ResponseError, so every validation failure surfaces as the generic
        // 500 api_error envelope (chat.go → transformOrchestratorError →
        // jina.TransformError).
        fixture.Channels.Set(TestChannels.Jina(4932, "jina-reranker-v2-base-multilingual"));
        var context = NewJsonContext(
            "/v1/rerank",
            "{\"query\":\"What is the capital of France?\",\"documents\":[\"Paris\"]}");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithRerank(orchestrator));

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"message\":\"Internal server error\",\"type\":\"api_error\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task RerankDecodeErrorReturnsInternalApiError()
    {
        fixture.Channels.Set(TestChannels.Jina(4932, "jina-reranker-v2-base-multilingual"));
        var context = NewJsonContext("/v1/rerank", "{not json");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithRerank(orchestrator));

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"message\":\"Internal server error\",\"type\":\"api_error\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task RerankForwardsToJinaChannelAndReturnsResults()
    {
        fixture.Channels.Set(TestChannels.Jina(4932, "jina-reranker-v2-base-multilingual"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => JsonUpstreamResponse(
                "{\"model\":\"jina-reranker-v2-base-multilingual\",\"object\":\"list\","
                + "\"results\":[{\"index\":0,\"relevance_score\":0.9,"
                + "\"document\":{\"text\":\"Paris\"}}],"
                + "\"usage\":{\"prompt_tokens\":5,\"total_tokens\":5}}"),
        };
        var context = NewJsonContext(
            "/v1/rerank",
            "{\"model\":\"jina-reranker-v2-base-multilingual\","
            + "\"query\":\"What is the capital of France?\",\"documents\":[\"Paris\",\"London\"]}");
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithRerank(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using (var body = JsonDocument.Parse(ResponseBody(context)))
        {
            Assert.Equal("list", body.RootElement.GetProperty("object").GetString());
            Assert.Equal(
                "jina-reranker-v2-base-multilingual",
                body.RootElement.GetProperty("model").GetString());
            var results = body.RootElement.GetProperty("results");
            Assert.Equal(1, results.GetArrayLength());
            Assert.Equal(0, results[0].GetProperty("index").GetInt32());
            Assert.Equal(0.9, results[0].GetProperty("relevance_score").GetDouble(), 9);
            Assert.Equal("Paris", results[0].GetProperty("document").GetProperty("text").GetString());
            Assert.Equal(5, body.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
            Assert.Equal(5, body.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        }

        var upstream = Assert.Single(executor.Requests);
        Assert.Equal("http://upstream.test/v1/rerank", upstream.Url);
        Assert.Equal(ApiFormats.JinaRerank, upstream.ApiFormat);
        Assert.Equal(RequestTypes.Rerank, upstream.RequestType);
        // The pipeline finalizes auth into the Authorization header before
        // dispatch (FinalizeAuthHeaders clears the Auth field).
        Assert.Equal("Bearer sk-test", HttpClientUtils.HeaderGet(upstream.Headers, "Authorization"));
    }

    [Fact]
    public async Task JinaRouteRerankUsesTheSameHandler()
    {
        fixture.Channels.Set(TestChannels.Jina(4932, "jina-reranker-v2-base-multilingual"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => JsonUpstreamResponse(
                "{\"model\":\"jina-reranker-v2-base-multilingual\",\"object\":\"list\","
                + "\"results\":[{\"index\":0,\"relevance_score\":0.9}],"
                + "\"usage\":{\"prompt_tokens\":5,\"total_tokens\":5}}"),
        };
        var context = NewJsonContext(
            "/jina/v1/rerank",
            "{\"model\":\"jina-reranker-v2-base-multilingual\",\"query\":\"q\",\"documents\":[\"d\"]}");
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithRerank(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("http://upstream.test/v1/rerank", Assert.Single(executor.Requests).Url);
    }

    // ------------------------------------------------------------ jina embeddings

    [Fact]
    public async Task JinaEmbeddingsForwardsToJinaChannelWithDefaultTask()
    {
        fixture.Channels.Set(TestChannels.Jina(4933, "jina-embeddings-v3"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => JsonUpstreamResponse(
                "{\"object\":\"list\",\"data\":[{\"object\":\"embedding\","
                + "\"embedding\":[0.1,0.2,0.3],\"index\":0}],"
                + "\"model\":\"jina-embeddings-v3\","
                + "\"usage\":{\"prompt_tokens\":4,\"total_tokens\":4}}"),
        };
        var context = NewJsonContext(
            "/jina/v1/embeddings",
            "{\"model\":\"jina-embeddings-v3\",\"input\":\"The quick brown fox\"}");
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithJinaEmbedding(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using (var body = JsonDocument.Parse(ResponseBody(context)))
        {
            Assert.Equal("list", body.RootElement.GetProperty("object").GetString());
            Assert.Equal(
                "jina-embeddings-v3",
                body.RootElement.GetProperty("model").GetString());
            var data = body.RootElement.GetProperty("data");
            Assert.Equal(1, data.GetArrayLength());
            Assert.Equal(0.1, data[0].GetProperty("embedding")[0].GetDouble(), 9);
            Assert.Equal(4L, body.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt64());
            Assert.Equal(4L, body.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt64());
        }

        var upstream = Assert.Single(executor.Requests);
        Assert.Equal("http://upstream.test/v1/embeddings", upstream.Url);
        Assert.Equal(ApiFormats.JinaEmbedding, upstream.ApiFormat);
        Assert.Equal(RequestTypes.Embedding, upstream.RequestType);
        Assert.Equal("Bearer sk-test", HttpClientUtils.HeaderGet(upstream.Headers, "Authorization"));

        using (var sent = JsonDocument.Parse(upstream.Body!))
        {
            Assert.Equal("jina-embeddings-v3", sent.RootElement.GetProperty("model").GetString());
            Assert.Equal("The quick brown fox", sent.RootElement.GetProperty("input").GetString());

            // The empty unified task defaults to text-matching on the wire.
            Assert.Equal("text-matching", sent.RootElement.GetProperty("task").GetString());
        }
    }

    [Fact]
    public async Task JinaEmbeddingsValidationErrorReturnsInternalApiError()
    {
        fixture.Channels.Set(TestChannels.Jina(4933, "jina-embeddings-v3"));
        var context = NewJsonContext("/jina/v1/embeddings", "{\"input\":\"test\"}");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithJinaEmbedding(orchestrator));

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"message\":\"Internal server error\",\"type\":\"api_error\"}}",
            ResponseBody(context));
    }
}
