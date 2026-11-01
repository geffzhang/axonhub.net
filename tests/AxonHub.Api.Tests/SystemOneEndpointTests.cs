using System.Text;
using System.Text.Json;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.TypeSafe;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Endpoint glue tests for TypeSafe System One
/// (<c>POST /v1/systemone</c> and <c>POST /typesafe/v1/systemone</c>,
/// routes.go:233/244). Both routes share one handler, like
/// api.TypeSafeHandlers.SystemOne over the shared chat handler, so these tests
/// drive ChatCompletionEndpoint with the SystemOne inbound transformer exactly
/// the way Program.cs registers it.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class SystemOneEndpointTests(GrainClusterFixture fixture)
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

    /// <summary>Program.cs: <c>orchestrator.WithInbound(new SystemOneInboundTransformer())</c>.</summary>
    private static ChatCompletionOrchestrator WithSystemOne(ChatCompletionOrchestrator orchestrator)
        => orchestrator.WithInbound(new SystemOneInboundTransformer());

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

    private static HttpClientError UpstreamError(int statusCode, string body)
        => new(
            "POST",
            "https://api.typesafe.ai/v1/systemone",
            statusCode,
            statusCode + " status",
            Encoding.UTF8.GetBytes(body));

    private const string RequestBody =
        """
        {
            "model": "jev-latest",
            "state": "The server CPU usage is 98% and memory is saturated.",
            "questions": {
                "urgent": {
                    "type": "noul",
                    "instructions": "Is this situation critical?"
                }
            }
        }
        """;

    [Fact]
    public async Task SystemOneForwardsStateAndReturnsAnswers()
    {
        // Port of TestChatCompletionOrchestrator_Process_SystemOne_Success.
        fixture.Channels.Set(TestChannels.TypeSafe(4941, "jev-latest"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => JsonUpstreamResponse(
                """
                {
                    "model": "jev-latest",
                    "answers": {"urgent": {"type": "noul", "noul": 0.96}},
                    "usage": {"input_tokens": 120, "output_tokens": 0}
                }
                """),
        };
        var context = NewJsonContext("/v1/systemone", RequestBody);
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithSystemOne(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using (var body = JsonDocument.Parse(ResponseBody(context)))
        {
            Assert.Equal("jev-latest", body.RootElement.GetProperty("model").GetString());

            var answer = body.RootElement.GetProperty("answers").GetProperty("urgent");
            Assert.Equal("noul", answer.GetProperty("type").GetString());
            Assert.Equal(0.96, answer.GetProperty("noul").GetDouble(), 12);

            var usage = body.RootElement.GetProperty("usage");
            Assert.Equal(120L, usage.GetProperty("input_tokens").GetInt64());
            Assert.Equal(0L, usage.GetProperty("output_tokens").GetInt64());
        }

        var upstream = Assert.Single(executor.Requests);
        Assert.Equal("https://api.typesafe.ai/v1/systemone", upstream.Url);
        Assert.Equal(RequestTypes.SystemOne, upstream.RequestType);
        Assert.Equal(ApiFormats.TypeSafeSystemOne, upstream.ApiFormat);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(upstream.Headers, "Content-Type"));
        Assert.Equal(
            "Bearer sk-typesafe-test-key",
            HttpClientUtils.HeaderGet(upstream.Headers, "Authorization"));

        // The outbound wire body keeps state and questions, and never streams.
        using var upstreamBody = JsonDocument.Parse(upstream.Body!);
        Assert.Equal("jev-latest", upstreamBody.RootElement.GetProperty("model").GetString());
        Assert.Equal(
            "The server CPU usage is 98% and memory is saturated.",
            upstreamBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(
            "noul",
            upstreamBody.RootElement.GetProperty("questions")
                .GetProperty("urgent").GetProperty("type").GetString());
        Assert.False(upstreamBody.RootElement.TryGetProperty("stream", out _));
    }

    [Fact]
    public async Task SystemOneNativeSurfaceUsesTheSameHandler()
    {
        // routes.go:244 mounts the native /typesafe/v1 group on the same handler.
        fixture.Channels.Set(TestChannels.TypeSafe(4942, "jev-latest"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => JsonUpstreamResponse(
                """{"model":"jev-latest","answers":{"urgent":{"type":"noul","noul":0.5}}}"""),
        };
        var context = NewJsonContext("/typesafe/v1/systemone", RequestBody);
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithSystemOne(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        using var body = JsonDocument.Parse(ResponseBody(context));
        Assert.Equal(
            0.5,
            body.RootElement.GetProperty("answers")
                .GetProperty("urgent").GetProperty("noul").GetDouble(),
            12);
    }

    [Fact]
    public async Task SystemOneEmptyResponseDetectionKeepsNonEmptyAnswers()
    {
        // Port of TestChatCompletionOrchestrator_Process_SystemOne_EmptyResponseDetection:
        // a systemone response with answers carries content, so it is committed.
        fixture.Channels.Set(TestChannels.TypeSafe(4943, "jev-latest"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => JsonUpstreamResponse(
                """
                {
                    "model": "jev-latest",
                    "answers": {"decision": {"type": "choice", "choice": "approve", "confidence": 0.99}}
                }
                """),
        };
        var context = NewJsonContext("/v1/systemone", RequestBody);
        var orchestrator = NewOrchestrator(
            executor,
            new RecordingMetricsRecorder(),
            new RetryPolicy
            {
                Enabled = true,
                MaxChannelRetries = 1,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
                EmptyResponseDetection = true,
            });

        await ChatCompletionEndpoint.HandleAsync(context, WithSystemOne(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(1, executor.Calls);
    }

    [Fact]
    public async Task SystemOneEmptyAnswersAreRetriedOnTheNextChannel()
    {
        // empty_response.go: SystemOne counts as content only when it has answers.
        fixture.Channels.Set(
            TestChannels.TypeSafe(4944, "jev-latest"),
            TestChannels.TypeSafe(4945, "jev-latest"));
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call == 0
                ? JsonUpstreamResponse("""{"model":"jev-latest","answers":{}}""")
                : JsonUpstreamResponse(
                    """{"model":"jev-latest","answers":{"urgent":{"type":"noul","noul":0.8}}}"""),
        };
        var context = NewJsonContext("/v1/systemone", RequestBody);
        var orchestrator = NewOrchestrator(
            executor,
            new RecordingMetricsRecorder(),
            new RetryPolicy
            {
                Enabled = true,
                MaxChannelRetries = 1,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
                EmptyResponseDetection = true,
            });

        await ChatCompletionEndpoint.HandleAsync(context, WithSystemOne(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(2, executor.Calls);
    }

    [Fact]
    public async Task SystemOneFailover429UsesTheNextChannelsKey()
    {
        // Port of TestChatCompletionOrchestrator_Process_SystemOne_Failover_429.
        fixture.Channels.Set(
            TestChannels.TypeSafe(4946, "jev-latest", apiKey: "sk-key-1"),
            TestChannels.TypeSafe(4947, "jev-latest", apiKey: "sk-key-2"));
        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call == 0
                ? throw UpstreamError(
                    429,
                    """{"error":{"message":"Rate limit exceeded","type":"rate_limit"}}""")
                : JsonUpstreamResponse(
                    """{"model":"jev-latest","answers":{"urgent":{"type":"noul","noul":1.0}}}"""),
        };
        var context = NewJsonContext("/v1/systemone", RequestBody);
        var orchestrator = NewOrchestrator(
            executor,
            new RecordingMetricsRecorder(),
            new RetryPolicy
            {
                Enabled = true,
                MaxChannelRetries = 3,
                MaxSingleChannelRetries = 2,
                RetryDelayMs = 0,
            });

        await ChatCompletionEndpoint.HandleAsync(context, WithSystemOne(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(2, executor.Calls);
        Assert.Equal(
            "Bearer sk-key-1",
            HttpClientUtils.HeaderGet(executor.Requests[0].Headers, "Authorization"));
        Assert.Equal(
            "Bearer sk-key-2",
            HttpClientUtils.HeaderGet(executor.Requests[1].Headers, "Authorization"));
    }

    [Fact]
    public async Task SystemOneUnauthorizedIsNotRetried()
    {
        // Port of TestChatCompletionOrchestrator_Process_SystemOne_NoRetry_401.
        fixture.Channels.Set(TestChannels.TypeSafe(4948, "jev-latest", apiKey: "sk-invalid-key"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw UpstreamError(
                401,
                """{"error":{"message":"Invalid API key","type":"authentication_error"}}"""),
        };
        var context = NewJsonContext("/v1/systemone", RequestBody);
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithSystemOne(orchestrator));

        Assert.Equal(401, context.Response.StatusCode);
        Assert.Equal(1, executor.Calls);

        using var body = JsonDocument.Parse(ResponseBody(context));
        var error = body.RootElement.GetProperty("error");
        Assert.Equal("Invalid API key", error.GetProperty("message").GetString());
        Assert.Equal("authentication_error", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task SystemOneMissingModelIsTransformedLikeTheReference()
    {
        // typesafe.SystemOneInboundTransformer.TransformError only special-cases
        // *llm.ResponseError; a rejected request therefore surfaces as the
        // reference's generic 500 api_error envelope, not a 400.
        fixture.Channels.Set();
        var context = NewJsonContext("/v1/systemone", """{"state":"s"}""");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithSystemOne(orchestrator));

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal(
            """{"error":{"message":"invalid request: model is required","type":"api_error"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task SystemOneMissingStateIsTransformedLikeTheReference()
    {
        fixture.Channels.Set();
        var context = NewJsonContext(
            "/v1/systemone",
            """{"model":"jev-latest","questions":{"q":{"type":"noul","instructions":"?"}}}""");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithSystemOne(orchestrator));

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal(
            """{"error":{"message":"invalid request: state is required","type":"api_error"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task SystemOneEmptyBodyReturnsBadRequest()
    {
        // api.ChatCompletion's empty-body guard runs before the inbound transform.
        fixture.Channels.Set();
        var context = NewJsonContext("/v1/systemone", string.Empty);
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithSystemOne(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"type\":\"Bad Request\",\"message\":\"Request body is empty\"}}",
            ResponseBody(context));
    }
}
