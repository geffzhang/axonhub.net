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
/// Endpoint glue tests for the legacy completions route
/// (<c>POST /v1/completions</c>, routes.go: openaiGroup.POST("/completions")).
/// The reference's CompletionHandlers.ChatCompletion reuses the chat handler
/// with the completion inbound transformer, exactly like the port's Program.cs.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class CompletionsEndpointTests(GrainClusterFixture fixture)
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

    /// <summary>Program.cs: <c>orchestrator.WithInbound(new CompletionInboundTransformer())</c>.</summary>
    private static ChatCompletionOrchestrator WithCompletion(ChatCompletionOrchestrator orchestrator)
        => orchestrator.WithInbound(new CompletionInboundTransformer());

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

    private static DefaultHttpContext NewJsonContext(string body)
        => NewContext("/v1/completions", Encoding.UTF8.GetBytes(body), "application/json");

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

    [Fact]
    public async Task CompletionsForwardsJsonAndReturnsCompletionResponse()
    {
        // The openai channel type's built-in endpoints stop at chat (the reference's
        // openAIFullDefaultEndpoints has no openai/completion), so a completions
        // request only resolves when the channel opts in with a custom endpoint
        // (channel_endpoint.go buildNonDefaultEndpointOutbound).
        fixture.Channels.Set(TestChannels.OpenAi(
            4921,
            "gpt-3.5-turbo-instruct",
            endpoints: """[{"api_format":"openai/completions"}]"""));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => JsonUpstreamResponse(
                "{\"id\":\"cmpl-1\",\"object\":\"text_completion\",\"created\":1677652288,"
                + "\"model\":\"gpt-3.5-turbo-instruct\","
                + "\"choices\":[{\"text\":\"Hello there\",\"index\":0,\"finish_reason\":\"stop\"}],"
                + "\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2,\"total_tokens\":7}}"),
        };
        var context = NewJsonContext(
            "{\"model\":\"gpt-3.5-turbo-instruct\",\"prompt\":\"Say hello\",\"max_tokens\":16,\"stream\":false}");
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithCompletion(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using (var body = JsonDocument.Parse(ResponseBody(context)))
        {
            Assert.Equal("cmpl-1", body.RootElement.GetProperty("id").GetString());
            Assert.Equal("text_completion", body.RootElement.GetProperty("object").GetString());
            Assert.Equal(1677652288L, body.RootElement.GetProperty("created").GetInt64());
            Assert.Equal(
                "gpt-3.5-turbo-instruct",
                body.RootElement.GetProperty("model").GetString());

            var choices = body.RootElement.GetProperty("choices");
            Assert.Equal(1, choices.GetArrayLength());
            Assert.Equal("Hello there", choices[0].GetProperty("text").GetString());
            Assert.Equal(0, choices[0].GetProperty("index").GetInt32());
            Assert.Equal("stop", choices[0].GetProperty("finish_reason").GetString());

            var usage = body.RootElement.GetProperty("usage");
            Assert.Equal(5L, usage.GetProperty("prompt_tokens").GetInt64());
            Assert.Equal(2L, usage.GetProperty("completion_tokens").GetInt64());
            Assert.Equal(7L, usage.GetProperty("total_tokens").GetInt64());
        }

        var upstream = Assert.Single(executor.Requests);
        Assert.Equal("http://upstream.test/v1/completions", upstream.Url);
        Assert.Equal(RequestTypes.Completion, upstream.RequestType);
        Assert.Equal(ApiFormats.OpenAICompletion, upstream.ApiFormat);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(upstream.Headers, "Content-Type"));

        // PrepareHTTPTransportRequest folds the Auth config into the header.
        Assert.Equal(
            "Bearer sk-test",
            HttpClientUtils.HeaderGet(upstream.Headers, "Authorization"));

        // The outbound body is the legacy wire shape, not the chat messages body.
        using var upstreamBody = JsonDocument.Parse(upstream.Body!);
        Assert.Equal(
            "gpt-3.5-turbo-instruct",
            upstreamBody.RootElement.GetProperty("model").GetString());
        Assert.Equal("Say hello", upstreamBody.RootElement.GetProperty("prompt").GetString());
        Assert.Equal(16L, upstreamBody.RootElement.GetProperty("max_tokens").GetInt64());
        Assert.False(upstreamBody.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(upstreamBody.RootElement.TryGetProperty("messages", out _));
    }

    [Fact]
    public async Task CompletionsMissingPromptReturnsInvalidRequest()
    {
        fixture.Channels.Set();
        var context = NewJsonContext("{\"model\":\"gpt-3.5-turbo-instruct\"}");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithCompletion(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        using var body = JsonDocument.Parse(ResponseBody(context));
        var error = body.RootElement.GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("invalid request: prompt is required", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task CompletionsEmptyBodyReturnsBadRequest()
    {
        // api.ChatCompletion's empty-body guard runs before the inbound transform.
        fixture.Channels.Set();
        var context = NewContext("/v1/completions", [], "application/json");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithCompletion(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"type\":\"Bad Request\",\"message\":\"Request body is empty\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task CompletionsUnsupportedContentTypeReturnsInvalidRequest()
    {
        fixture.Channels.Set();
        var context = NewContext(
            "/v1/completions",
            Encoding.UTF8.GetBytes("{\"model\":\"m\",\"prompt\":\"p\"}"),
            "text/plain");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithCompletion(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        using var body = JsonDocument.Parse(ResponseBody(context));
        Assert.Equal(
            "invalid request: unsupported content type: text/plain",
            body.RootElement.GetProperty("error").GetProperty("message").GetString());
    }
}
