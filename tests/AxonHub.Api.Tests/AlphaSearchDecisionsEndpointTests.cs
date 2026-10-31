using System.Text;
using System.Text.Json;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Decisions;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Endpoint glue tests for the OpenAI alpha search and decisions routes
/// (<c>POST /v1/alpha/search</c> and <c>POST /v1/decisions</c>,
/// routes.go:213-214). Both reuse the chat handler with a dedicated inbound
/// transformer, exactly like api.OpenAIHandlers.CreateAlphaSearch /
/// CreateDecisions; the transformers themselves are ported one-for-one in
/// AxonHub.Llm.Tests.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class AlphaSearchDecisionsEndpointTests(GrainClusterFixture fixture)
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
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

    private static RetryPolicy NoRetryPolicy()
        => new()
        {
            MaxChannelRetries = 0,
            MaxSingleChannelRetries = 0,
            RetryDelayMs = 0,
        };

    /// <summary>Program.cs: <c>orchestrator.WithInbound(new AlphaSearchInboundTransformer())</c>.</summary>
    private static ChatCompletionOrchestrator WithAlphaSearch(ChatCompletionOrchestrator orchestrator)
        => orchestrator.WithInbound(new AlphaSearchInboundTransformer());

    /// <summary>Program.cs: <c>orchestrator.WithInbound(new DecisionsInboundTransformer())</c>.</summary>
    private static ChatCompletionOrchestrator WithDecisions(ChatCompletionOrchestrator orchestrator)
        => orchestrator.WithInbound(new DecisionsInboundTransformer());

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
        => UpstreamResponse(200, json);

    private static HttpResponseData ErrorUpstreamResponse(int statusCode, string json)
        => UpstreamResponse(statusCode, json);

    private static HttpResponseData UpstreamResponse(int statusCode, string json)
        => new()
        {
            StatusCode = statusCode,
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

    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.True(
            JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement),
            "expected " + expected + " but got " + actual);
    }

    // --------------------------------------------------------------- alpha search

    [Fact]
    public async Task AlphaSearchForwardsRawBodyAndReturnsProviderBodyVerbatim()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4931, "search-model"));
        const string providerBody = """{"results":[{"title":"Go"}]}""";
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => JsonUpstreamResponse(providerBody),
        };
        const string clientBody =
            """{"model":"search-model","commands":{"search_query":[{"q":"golang"}]}}""";
        var context = NewJsonContext("/v1/alpha/search", clientBody);
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithAlphaSearch(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal(providerBody, ResponseBody(context));

        var upstream = Assert.Single(executor.Requests);
        Assert.Equal("http://upstream.test/v1/alpha/search", upstream.Url);
        Assert.Equal(ApiFormats.OpenAIAlphaSearch, upstream.ApiFormat);
        Assert.Equal(RequestTypes.AlphaSearch, upstream.RequestType);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(upstream.Headers, "Content-Type"));
        AssertJsonEqual(clientBody, Encoding.UTF8.GetString(upstream.Body!));
    }

    [Fact]
    public async Task AlphaSearchMissingModelReturnsInvalidRequest()
    {
        fixture.Channels.Set();
        var context = NewJsonContext("/v1/alpha/search", """{"commands":{}}""");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithAlphaSearch(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        AssertJsonError(
            ResponseBody(context),
            "invalid_request_error",
            "invalid request: model is required for alpha search routing");
    }

    [Fact]
    public async Task AlphaSearchProviderErrorBecomesAnEnvelope()
    {
        // transformAlphaSearchResponse returns openai.TransformError's
        // *llm.ResponseError, which (unlike the decisions outbound) carries no
        // Cause. The chat inbound mapper therefore cannot find an
        // httpclient.Error and re-marshals the detail envelope instead of
        // passing the provider bytes through.
        fixture.Channels.Set(TestChannels.OpenAi(4932, "search-model"));
        const string providerError =
            """{"error":{"message":"search rate limit","type":"rate_limit_error","code":"rate_limit"}}""";
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ErrorUpstreamResponse(429, providerError),
        };
        var context = NewJsonContext("/v1/alpha/search", """{"model":"search-model","commands":{}}""");
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder(), NoRetryPolicy());

        await ChatCompletionEndpoint.HandleAsync(context, WithAlphaSearch(orchestrator));

        Assert.Equal(429, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        AssertJsonEqual(providerError, ResponseBody(context));
    }

    // ----------------------------------------------------------------- decisions

    [Fact]
    public async Task DecisionsForwardsRawBodyAndReturnsProviderBodyVerbatim()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4933, "gpt-6-luna"));
        const string providerBody =
            """{"model":"gpt-6-luna","answers":[{"type":"choice","choice":"billing"}],"usage":{"input_tokens":120}}""";
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => JsonUpstreamResponse(providerBody),
        };
        const string clientBody =
            """{"model":"gpt-6-luna","input":"I was charged twice.","questions":[{"type":"choice"}]}""";
        var context = NewJsonContext("/v1/decisions", clientBody);
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithDecisions(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);
        Assert.Equal(providerBody, ResponseBody(context));

        var upstream = Assert.Single(executor.Requests);
        Assert.Equal("http://upstream.test/v1/decisions", upstream.Url);
        Assert.Equal(ApiFormats.OpenAIDecisions, upstream.ApiFormat);
        Assert.Equal(RequestTypes.Decisions, upstream.RequestType);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(upstream.Headers, "Content-Type"));
        AssertJsonEqual(clientBody, Encoding.UTF8.GetString(upstream.Body!));
    }

    [Fact]
    public async Task DecisionsMissingQuestionsReturnsInvalidRequest()
    {
        fixture.Channels.Set();
        var context = NewJsonContext(
            "/v1/decisions",
            """{"model":"gpt-6-luna","input":"text"}""");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithDecisions(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        AssertJsonError(
            ResponseBody(context),
            "invalid_request_error",
            "invalid request: questions are required");
    }

    [Fact]
    public async Task DecisionsProviderErrorPassesThroughVerbatim()
    {
        // decisions.OutboundTransformer.TransformError keeps the httpclient.Error
        // as the ResponseError's Cause, and the decisions inbound mapper checks
        // errors.As[httpclient.Error] first: the provider status and bytes reach
        // the client untouched.
        fixture.Channels.Set(TestChannels.OpenAi(4934, "gpt-6-luna"));
        const string providerError =
            """{"error":{"message":"bad request","type":"invalid_request_error","code":"bad_request"}}""";
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ErrorUpstreamResponse(400, providerError),
        };
        var context = NewJsonContext(
            "/v1/decisions",
            """{"model":"gpt-6-luna","input":"text","questions":[{}]}""");
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder(), NoRetryPolicy());

        await ChatCompletionEndpoint.HandleAsync(context, WithDecisions(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(providerError, ResponseBody(context));
    }
}
