using System.Text;
using System.Text.Json;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Endpoint glue tests: the HTTP status, content type and body the client sees,
/// with the real orchestrator behind it and a scripted upstream.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class ChatCompletionEndpointTests(GrainClusterFixture fixture)
{
    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        RecordingMetricsRecorder metrics,
        IRequestChainRecorder? recorder = null)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            metrics,
            new RetryPolicy { Enabled = true, MaxChannelRetries = 3, MaxSingleChannelRetries = 0, RetryDelayMs = 0 },
            TimeProvider.System,
            recorder);

    private static DefaultHttpContext NewContext(string body)
        => NewContext(Encoding.UTF8.GetBytes(body));

    private static DefaultHttpContext NewContext(byte[] body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.test");
        context.Request.Path = "/v1/chat/completions";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ResponseBody(DefaultHttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static string RequestBody(string model, bool stream = false)
        => "{\"model\":\"" + model + "\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]"
            + (stream ? ",\"stream\":true}" : "}");

    [Fact]
    public async Task EmptyBodyReturnsBadRequestJsonError()
    {
        fixture.Channels.Set();
        var context = NewContext(string.Empty);
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal(
            "{\"error\":{\"type\":\"Bad Request\",\"message\":\"Request body is empty\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task InvalidModelReturnsUnprocessableEntity()
    {
        fixture.Channels.Set();
        var context = NewContext(RequestBody("missing-http-model"));
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        Assert.Equal(422, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);

        using var body = JsonDocument.Parse(ResponseBody(context));
        Assert.Equal("invalid_model_error", body.RootElement
            .GetProperty("error").GetProperty("type").GetString());
        Assert.Equal("model not found: missing-http-model", body.RootElement
            .GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task FieldTypeErrorReturnsGoDecodeWording()
    {
        fixture.Channels.Set();
        var context = NewContext("{\"model\":123,\"messages\":[]}");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        Assert.Equal(400, context.Response.StatusCode);

        using var body = JsonDocument.Parse(ResponseBody(context));
        Assert.Equal("invalid_request_error", body.RootElement
            .GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(
            "invalid request: failed to decode openai request: "
            + "json: cannot unmarshal number into Go struct field Request.model of type string",
            body.RootElement.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task NonStreamSuccessForwardsStatusContentTypeAndBody()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4801, "endpoint-model"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("endpoint-model", "hi there"),
        };
        var context = NewContext(RequestBody("endpoint-model"));
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using var body = JsonDocument.Parse(ResponseBody(context));
        Assert.Equal("hi there", body.RootElement
            .GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task IllFormedUtf8InMessageIsRepairedBeforeForwarding()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4805, "utf8-model"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("utf8-model"),
        };
        var prefix = Encoding.UTF8.GetBytes(
            "{\"model\":\"utf8-model\",\"messages\":[{\"role\":\"user\",\"content\":\"caf");
        var suffix = Encoding.UTF8.GetBytes("\"}]}");
        var context = NewContext([.. prefix, 0xFF, .. suffix]);
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        Assert.Equal(200, context.Response.StatusCode);
        var outbound = Encoding.UTF8.GetString(Assert.Single(executor.Requests).Body ?? []);
        Assert.Contains("caf�\"", outbound, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NonStreamSuccessRecordsResponseHeaders()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4803, "endpoint-headers"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("endpoint-headers"),
        };
        var recorder = new RecordingRequestChainRecorder();
        var context = NewContext(RequestBody("endpoint-headers"));
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder(), recorder);

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        var request = Assert.Single(recorder.Requests);
        var headers = Assert.Single(recorder.RequestHeaders);
        Assert.Equal(request.Id, headers.RequestId);
        Assert.Contains("\"Content-Type\":[\"application/json\"]", headers.HeadersJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamingResponseWritesSseWithCorsHeader()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4802, "endpoint-stream-model"));
        var chunks = new List<StreamEvent>
        {
            TestChannels.Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"endpoint-stream-model\",\"choices\":[{\"index\":0,"
                + "\"delta\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":null}]}"),
            TestChannels.Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"endpoint-stream-model\",\"choices\":[{\"index\":0,\"delta\":{},"
                + "\"finish_reason\":\"stop\"}],"
                + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}"),
            TestChannels.Data("[DONE]"),
        };
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(chunks),
        };
        var context = NewContext(RequestBody("endpoint-stream-model", stream: true));
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("text/event-stream;charset=utf-8", context.Response.ContentType);
        Assert.Equal("*", context.Response.Headers["Access-Control-Allow-Origin"].ToString());
        Assert.EndsWith("data:[DONE]\n\n", ResponseBody(context), StringComparison.Ordinal);
        Assert.DoesNotContain("event:error", ResponseBody(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamingResponseRecordsResponseHeaders()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4804, "endpoint-stream-headers"));
        var chunks = new List<StreamEvent>
        {
            TestChannels.Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"endpoint-stream-headers\",\"choices\":[{\"index\":0,"
                + "\"delta\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":null}]}"),
            TestChannels.Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"endpoint-stream-headers\",\"choices\":[{\"index\":0,\"delta\":{},"
                + "\"finish_reason\":\"stop\"}]}"),
            TestChannels.Data("[DONE]"),
        };
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(chunks),
        };
        var recorder = new RecordingRequestChainRecorder();
        var context = NewContext(RequestBody("endpoint-stream-headers", stream: true));
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder(), recorder);

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator);

        var request = Assert.Single(recorder.Requests);
        var headers = Assert.Single(recorder.RequestHeaders);
        Assert.Equal(request.Id, headers.RequestId);
        Assert.Contains("text/event-stream", headers.HeadersJson, StringComparison.Ordinal);
        Assert.Contains("Access-Control-Allow-Origin", headers.HeadersJson, StringComparison.Ordinal);

        // The stream finalized both rows once the client-facing stream closed.
        var requestResult = Assert.Single(recorder.RequestResults);
        Assert.Equal("completed", requestResult.Status);
    }
}
