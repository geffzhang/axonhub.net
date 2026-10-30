using System.Text;
using System.Text.Json;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Endpoint glue tests for the Anthropic Messages routes
/// (<c>POST /v1/messages</c>, <c>POST /anthropic/v1/messages</c>). The handler
/// is shared with OpenAI Chat; only the inbound transformer differs, so these
/// tests drive the same glue through <see cref="ChatCompletionOrchestrator.WithInbound"/>
/// exactly as Program.cs wires it.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class AnthropicMessagesEndpointTests(GrainClusterFixture fixture)
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

    private static ChatCompletionOrchestrator WithAnthropicInbound(ChatCompletionOrchestrator orchestrator)
        => orchestrator.WithInbound(new AnthropicInboundTransformer());

    private static RetryPolicy NoRetryPolicy(UpstreamErrorPolicy upstreamErrorPolicy)
        => new()
        {
            MaxChannelRetries = 0,
            MaxSingleChannelRetries = 0,
            RetryDelayMs = 0,
            UpstreamErrorPolicy = upstreamErrorPolicy,
        };

    private static DefaultHttpContext NewContext(string body)
        => NewContext(Encoding.UTF8.GetBytes(body));

    private static DefaultHttpContext NewContext(byte[] body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.test");
        context.Request.Path = "/v1/messages";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ResponseBody(DefaultHttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static string RequestBody(string model, bool stream = false)
        => "{\"model\":\"" + model + "\",\"max_tokens\":64,"
            + "\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]"
            + (stream ? ",\"stream\":true}" : "}");

    [Fact]
    public async Task NonStreamSuccessReturnsAnthropicMessage()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4901, "anthropic-model"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("anthropic-model", "hi there"),
        };
        var context = NewContext(RequestBody("anthropic-model"));
        var orchestrator = WithAnthropicInbound(NewOrchestrator(executor, new RecordingMetricsRecorder()));

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using var body = JsonDocument.Parse(ResponseBody(context));
        var root = body.RootElement;
        Assert.Equal("chatcmpl-1", root.GetProperty("id").GetString());
        Assert.Equal("message", root.GetProperty("type").GetString());
        Assert.Equal("assistant", root.GetProperty("role").GetString());
        Assert.Equal("anthropic-model", root.GetProperty("model").GetString());

        var block = root.GetProperty("content")[0];
        Assert.Equal("text", block.GetProperty("type").GetString());
        Assert.Equal("hi there", block.GetProperty("text").GetString());

        Assert.Equal("end_turn", root.GetProperty("stop_reason").GetString());
        Assert.Equal(1, root.GetProperty("usage").GetProperty("input_tokens").GetInt64());
        Assert.Equal(1, root.GetProperty("usage").GetProperty("output_tokens").GetInt64());
    }

    [Fact]
    public async Task ValidationErrorReturnsAnthropicEnvelope()
    {
        fixture.Channels.Set();
        var context = NewContext("{\"model\":\"m\",\"max_tokens\":8,\"messages\":[]}");
        var orchestrator = WithAnthropicInbound(NewOrchestrator(
            new ScriptedExecutor(),
            new RecordingMetricsRecorder()));

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);

        using var body = JsonDocument.Parse(ResponseBody(context));
        var root = body.RootElement;
        Assert.Equal("invalid_request_error", root.GetProperty("type").GetString());
        Assert.Equal(string.Empty, root.GetProperty("request_id").GetString());
        Assert.Equal("invalid_request_error", root.GetProperty("error").GetProperty("type").GetString());
        // Go wraps the sentinel: fmt.Errorf("%w: messages are required", ErrInvalidRequest).
        Assert.Equal(
            "invalid request: messages are required",
            root.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task InvalidModelReturnsUnprocessableEntityWithAnthropicEnvelope()
    {
        fixture.Channels.Set();
        var context = NewContext(RequestBody("missing-anthropic-model"));
        var orchestrator = WithAnthropicInbound(NewOrchestrator(
            new ScriptedExecutor(),
            new RecordingMetricsRecorder()));

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        Assert.Equal(422, context.Response.StatusCode);

        using var body = JsonDocument.Parse(ResponseBody(context));
        var root = body.RootElement;
        Assert.Equal("invalid_model_error", root.GetProperty("type").GetString());

        // The reference emits an empty detail type for this branch.
        Assert.Equal(string.Empty, root.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(
            "model not found: missing-anthropic-model",
            root.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task StreamingRequestIsRejectedWithInvalidRequestEnvelope()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4903, "anthropic-stream-model"));
        var context = NewContext(RequestBody("anthropic-stream-model", stream: true));
        var orchestrator = WithAnthropicInbound(NewOrchestrator(
            new ScriptedExecutor(),
            new RecordingMetricsRecorder()));

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        Assert.Equal(400, context.Response.StatusCode);

        using var body = JsonDocument.Parse(ResponseBody(context));
        var root = body.RootElement;
        Assert.Equal("invalid_request_error", root.GetProperty("type").GetString());
        Assert.Equal(
            "invalid request: streaming is not supported by this port yet",
            root.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task EmptyBodyKeepsOpenAiStyleJsonError()
    {
        fixture.Channels.Set();
        var context = NewContext(string.Empty);
        var orchestrator = WithAnthropicInbound(NewOrchestrator(
            new ScriptedExecutor(),
            new RecordingMetricsRecorder()));

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        // api.JSONError runs before the transformer, so the envelope stays
        // OpenAI-shaped on every protocol route (reference chat.go:70-142).
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal(
            "{\"error\":{\"type\":\"Bad Request\",\"message\":\"Request body is empty\"}}",
            ResponseBody(context));
    }

    /// <summary>
    /// The pipeline converts an upstream HTTP failure through the outbound
    /// transformer first (lenient provider-body parse), so the Anthropic
    /// envelope carries the provider's detail, not the raw body.
    /// </summary>
    [Fact]
    public async Task UpstreamHttpErrorUsesParsedProviderDetail()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4904, "anthropic-upstream-error"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw TestChannels.HttpClient(
                429,
                "{\"error\":{\"message\":\"boom\",\"type\":\"rate_limit_error\"}}"),
        };
        var context = NewContext(RequestBody("anthropic-upstream-error"));
        var orchestrator = WithAnthropicInbound(NewOrchestrator(
            executor,
            new RecordingMetricsRecorder(),
            policy: NoRetryPolicy(new UpstreamErrorPolicy(RetryPolicy.UpstreamErrorModePassthrough, string.Empty))));

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        Assert.Equal(429, context.Response.StatusCode);

        using var body = JsonDocument.Parse(ResponseBody(context));
        var root = body.RootElement;
        Assert.Equal("rate_limit_error", root.GetProperty("type").GetString());
        Assert.Equal(string.Empty, root.GetProperty("request_id").GetString());
        Assert.Equal("rate_limit_error", root.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("boom", root.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task ResponseErrorUsesAnthropicErrorEnvelope()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4905, "anthropic-response-error"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw new ResponseError("upstream said no")
            {
                StatusCode = 503,
                Detail = new ErrorDetail
                {
                    Type = "overloaded_error",
                    Message = "upstream said no",
                    RequestId = "req-1",
                },
            },
        };
        var context = NewContext(RequestBody("anthropic-response-error"));
        var orchestrator = WithAnthropicInbound(NewOrchestrator(
            executor,
            new RecordingMetricsRecorder(),
            policy: NoRetryPolicy(new UpstreamErrorPolicy(RetryPolicy.UpstreamErrorModePassthrough, string.Empty))));

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        Assert.Equal(503, context.Response.StatusCode);

        using var body = JsonDocument.Parse(ResponseBody(context));
        var root = body.RootElement;
        Assert.Equal("overloaded_error", root.GetProperty("type").GetString());
        Assert.Equal("req-1", root.GetProperty("request_id").GetString());
        Assert.Equal("overloaded_error", root.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("upstream said no", root.GetProperty("error").GetProperty("message").GetString());
    }
}
