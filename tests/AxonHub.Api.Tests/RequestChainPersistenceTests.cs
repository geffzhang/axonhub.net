using System.Text;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Auth;
using AxonHub.Api.Http;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Api.Tests;

/// <summary>
/// Behavioural tests for the request-chain persistence port
/// (internal/server/orchestrator/request.go and request_execution.go): the rows
/// and updates the middleware writes for a successful call, a retry, a final
/// failure and a streamed response.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class RequestChainPersistenceTests(GrainClusterFixture fixture)
{
    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        RetryPolicy policy,
        IRequestChainRecorder recorder)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            policy,
            TimeProvider.System,
            recorder);

    private static RetryPolicy FastPolicy(int maxSameChannelRetries = 0)
        => new()
        {
            Enabled = true,
            MaxChannelRetries = 3,
            MaxSingleChannelRetries = maxSameChannelRetries,
            RetryDelayMs = 0,
        };

    private static HttpRequestData ClientRequest(string model, bool stream = false)
    {
        var request = TestChannels.ChatRequest(model, stream);
        request.ClientIp = "10.0.0.7";
        request.UserAgent = "xunit";
        request.Headers!["Authorization"] = ["Bearer sk-client"];
        return request;
    }

    private static ChannelDescriptor MappedChannel(long id, string from, string to)
        => new()
        {
            Id = id,
            Name = "channel-" + id,
            OrderingWeight = 0,
            Type = "openai",
            BaseUrl = "http://upstream.test",
            Credentials = "{\"apiKey\":\"sk-mapped\"}",
            SupportedModels = "[\"" + to + "\"]",
            Settings = "{\"modelMappings\":[{\"from\":\"" + from + "\",\"to\":\"" + to + "\"}]}",
        };

    [Fact]
    public async Task NonStreamSuccessWritesRequestExecutionAndUsage()
    {
        fixture.Channels.Set(MappedChannel(5101, "client-persist", "upstream-persist"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("upstream-persist"),
        };
        var recorder = new RecordingRequestChainRecorder();
        var orchestrator = NewOrchestrator(executor, FastPolicy(), recorder);

        var result = await orchestrator.ProcessAsync(ClientRequest("client-persist"));

        Assert.Equal(200, result.Response!.StatusCode);

        var request = Assert.Single(recorder.Requests);
        Assert.Equal("api", request.Source);
        Assert.Equal("client-persist", request.ModelId);
        Assert.Equal("openai/chat_completions", request.Format);
        Assert.Equal("processing", request.Status);
        Assert.False(request.Stream);
        Assert.Equal(1L, request.ProjectId);
        Assert.Null(request.ApiKeyId);
        Assert.Equal("10.0.0.7", request.ClientIp);
        Assert.Equal("xunit", request.UserAgent);
        Assert.Equal("client-persist", BodyModel(request.RequestBody));
        Assert.Equal("******", MaskedHeader(request.RequestHeaders, "Authorization"));

        var execution = Assert.Single(recorder.Executions);
        Assert.Equal("upstream-persist", execution.ModelId);
        Assert.Equal("openai/chat_completions", execution.Format);
        Assert.Equal("processing", execution.Status);
        Assert.False(execution.Stream);
        Assert.Equal(1L, execution.ProjectId);
        Assert.Equal(5101L, execution.ChannelId);
        Assert.Equal(request.Id, execution.RequestId);
        Assert.Equal("http://upstream.test/v1/chat/completions", execution.RequestUrl);
        Assert.Equal("upstream-persist", BodyModel(execution.RequestBody));
        Assert.Equal("******", MaskedHeader(execution.RequestHeaders, "Authorization"));

        Assert.Equal(new long[] { 5101 }, recorder.RequestChannels);

        var executionResult = Assert.Single(recorder.ExecutionResults);
        Assert.Equal(execution.Id, executionResult.ExecutionId);
        Assert.Equal("completed", executionResult.Status);
        Assert.Equal("chatcmpl-1", executionResult.ExternalId);
        Assert.Equal("upstream-persist", executionResult.UpstreamModelId);
        Assert.NotNull(executionResult.ResponseBody);
        Assert.Null(executionResult.ErrorMessage);
        Assert.NotNull(executionResult.LatencyMs);

        // First token latency exists only for streams.
        Assert.Null(executionResult.FirstTokenLatencyMs);

        // Response headers travel through their own update (the transport
        // reports them before the body is transformed), never through the
        // finalized-row update.
        var executionHeaders = Assert.Single(recorder.ExecutionHeaders);
        Assert.Equal(execution.Id, executionHeaders.ExecutionId);
        Assert.Contains("\"Content-Type\"", executionHeaders.HeadersJson);

        var requestResult = Assert.Single(recorder.RequestResults);
        Assert.Equal(request.Id, requestResult.RequestId);
        Assert.Equal("completed", requestResult.Status);
        Assert.Equal("chatcmpl-1", requestResult.ExternalId);
        Assert.NotNull(requestResult.ResponseBody);
        Assert.NotNull(requestResult.LatencyMs);

        // The downstream response headers are written by the HTTP boundary
        // (middleware/response_headers.go), not by the orchestrator.
        Assert.Empty(recorder.RequestHeaders);

        var usage = Assert.Single(recorder.UsageLogs);
        Assert.Equal(0L, usage.ApiKeyId);
        Assert.Equal("upstream-persist", usage.ModelId);
        Assert.Equal(1L, usage.PromptTokens);
        Assert.Equal(1L, usage.CompletionTokens);
        Assert.Equal(2L, usage.TotalTokens);
        Assert.Equal("api", usage.Source);
        Assert.Equal("openai/chat_completions", usage.Format);
        Assert.Equal("[]", usage.CostItems);
        Assert.Null(usage.TotalCost);
        Assert.Equal(5101L, usage.ChannelId);
        Assert.Equal(request.Id, usage.RequestId);

        Assert.Empty(recorder.ExecutionStatuses);
        Assert.Empty(recorder.RequestStatuses);
    }

    [Fact]
    public async Task AuthenticatedIdentityFlowsIntoRequestExecutionAndUsageRows()
    {
        // WithAPIKeyConfig publishes the API key and its project on the request
        // context; the persistence middleware reads both back when it writes the
        // request row, every execution row and the usage log.
        fixture.Channels.Set(TestChannels.OpenAi(5501, "persist-identity"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("persist-identity"),
        };
        var recorder = new RecordingRequestChainRecorder();
        var orchestrator = NewOrchestrator(executor, FastPolicy(), recorder);
        var identity = new RequestIdentity(9, 77);

        var result = await orchestrator.ProcessAsync(
            ClientRequest("persist-identity"),
            identity,
            onRequestCreated: null);

        Assert.Equal(200, result.Response!.StatusCode);

        var request = Assert.Single(recorder.Requests);
        Assert.Equal(9L, request.ProjectId);
        Assert.Equal(77L, request.ApiKeyId);

        var execution = Assert.Single(recorder.Executions);
        Assert.Equal(9L, execution.ProjectId);

        var usage = Assert.Single(recorder.UsageLogs);
        Assert.Equal(77L, usage.ApiKeyId);
        Assert.Equal(9L, usage.ProjectId);
    }

    [Fact]
    public async Task RetryWritesOneExecutionPerAttempt()
    {
        fixture.Channels.Set(
            TestChannels.OpenAi(5201, "persist-retry"),
            TestChannels.OpenAi(5202, "persist-retry"));

        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call == 0
                ? throw TestChannels.HttpClient(500)
                : TestChannels.Completion("persist-retry"),
        };
        var recorder = new RecordingRequestChainRecorder();
        var orchestrator = NewOrchestrator(executor, FastPolicy(), recorder);

        var result = await orchestrator.ProcessAsync(ClientRequest("persist-retry"));

        Assert.Equal(200, result.Response!.StatusCode);
        Assert.Equal(2, recorder.Executions.Count);
        Assert.Equal(new long[] { 5201, 5202 }, recorder.Executions.Select(e => e.ChannelId!.Value).ToArray());
        Assert.Equal(new long[] { 5201, 5202 }, recorder.RequestChannels);

        var failure = Assert.Single(recorder.ExecutionStatuses);
        Assert.Equal(recorder.Executions[0].Id, failure.ExecutionId);
        Assert.Equal("failed", failure.Status);
        Assert.Equal(500L, failure.ResponseStatusCode);
        Assert.Equal("boom", failure.ErrorMessage);

        // The reference's error hook passes nil metrics.
        Assert.Null(failure.LatencyMs);
        Assert.Null(failure.FirstTokenLatencyMs);

        var completed = Assert.Single(recorder.ExecutionResults);
        Assert.Equal(recorder.Executions[1].Id, completed.ExecutionId);
        Assert.Equal("completed", completed.Status);
        Assert.Equal("chatcmpl-1", completed.ExternalId);

        var requestResult = Assert.Single(recorder.RequestResults);
        Assert.Equal("completed", requestResult.Status);

        var usage = Assert.Single(recorder.UsageLogs);
        Assert.Equal(5202L, usage.ChannelId);
        Assert.Empty(recorder.RequestStatuses);
    }

    [Fact]
    public async Task FinalFailureMarkedFailedWithoutUsage()
    {
        fixture.Channels.Set(TestChannels.OpenAi(5301, "persist-fail"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw TestChannels.HttpClient(503),
        };
        var recorder = new RecordingRequestChainRecorder();
        var orchestrator = NewOrchestrator(
            executor,
            new RetryPolicy { Enabled = false },
            recorder);

        await Assert.ThrowsAnyAsync<Exception>(
            () => orchestrator.ProcessAsync(ClientRequest("persist-fail")));

        var request = Assert.Single(recorder.Requests);
        var execution = Assert.Single(recorder.Executions);

        // Two updates: the raw-error hook, then the orchestrator's final
        // persistRequestExecutionFailure (nil metrics, empty upstream model).
        Assert.Equal(2, recorder.ExecutionStatuses.Count);
        Assert.All(recorder.ExecutionStatuses, update =>
        {
            Assert.Equal(execution.Id, update.ExecutionId);
            Assert.Equal("failed", update.Status);
            Assert.Equal(503L, update.ResponseStatusCode);
            Assert.Equal("boom", update.ErrorMessage);
            Assert.Null(update.LatencyMs);
            Assert.Null(update.FirstTokenLatencyMs);
        });

        var status = Assert.Single(recorder.RequestStatuses);
        Assert.Equal(request.Id, status.RequestId);
        Assert.Equal("failed", status.Status);

        Assert.Empty(recorder.RequestResults);
        Assert.Empty(recorder.ExecutionResults);
        Assert.Empty(recorder.UsageLogs);
    }

    [Fact]
    public async Task StreamSuccessFinalizesRequestAndExecutionOnDispose()
    {
        fixture.Channels.Set(TestChannels.OpenAi(5401, "persist-stream"));

        var chunks = new List<StreamEvent>
        {
            TestChannels.Data("{\"id\":\"chatcmpl-9\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"persist-stream\",\"choices\":[{\"index\":0,"
                + "\"delta\":{\"role\":\"assistant\",\"content\":\"he\"},\"finish_reason\":null}]}"),
            TestChannels.Data("{\"id\":\"chatcmpl-9\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"persist-stream\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"llo\"},"
                + "\"finish_reason\":null}]}"),
            TestChannels.Data("{\"id\":\"chatcmpl-9\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"persist-stream\",\"choices\":[{\"index\":0,\"delta\":{},"
                + "\"finish_reason\":\"stop\"}],"
                + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":3}}"),
            TestChannels.Data("[DONE]"),
        };
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(chunks),
        };
        var recorder = new RecordingRequestChainRecorder();
        var orchestrator = NewOrchestrator(executor, FastPolicy(), recorder);

        var result = await orchestrator.ProcessAsync(ClientRequest("persist-stream", stream: true));

        Assert.NotNull(result.Stream);
        var events = new List<StreamEvent>();
        while (result.Stream!.Next())
        {
            if (result.Stream.Current is { } streamEvent)
            {
                events.Add(streamEvent);
            }
        }

        result.Stream.Dispose();

        Assert.Equal("[DONE]", Encoding.UTF8.GetString(events[^1].Data));

        var request = Assert.Single(recorder.Requests);
        Assert.True(request.Stream);

        var execution = Assert.Single(recorder.Executions);
        Assert.True(execution.Stream);
        Assert.Equal("persist-stream", execution.ModelId);
        Assert.Equal(5401L, execution.ChannelId);

        var executionResult = Assert.Single(recorder.ExecutionResults);
        Assert.Equal("completed", executionResult.Status);
        Assert.Equal("persist-stream", executionResult.UpstreamModelId);
        Assert.NotNull(executionResult.LatencyMs);
        Assert.NotNull(executionResult.FirstTokenLatencyMs);

        // Close aggregates the buffered provider chunks into the finalized
        // execution row: external id and body come from the aggregated meta.
        Assert.Equal("chatcmpl-9", executionResult.ExternalId);
        Assert.Contains("\"chatcmpl-9\"", executionResult.ResponseBody);
        Assert.Contains("\"hello\"", executionResult.ResponseBody);

        var requestResult = Assert.Single(recorder.RequestResults);
        Assert.Equal("completed", requestResult.Status);
        Assert.Equal("chatcmpl-9", requestResult.ExternalId);
        Assert.Contains("\"chatcmpl-9\"", requestResult.ResponseBody);
        Assert.NotNull(requestResult.LatencyMs);
        Assert.NotNull(requestResult.FirstTokenLatencyMs);

        var usage = Assert.Single(recorder.UsageLogs);
        Assert.Equal(1L, usage.PromptTokens);
        Assert.Equal(2L, usage.CompletionTokens);
        Assert.Equal(3L, usage.TotalTokens);
        Assert.Equal(5401L, usage.ChannelId);
        Assert.Equal(request.Id, usage.RequestId);

        Assert.Empty(recorder.ExecutionStatuses);
        Assert.Empty(recorder.RequestStatuses);
    }

    private static string? BodyModel(string? body)
    {
        Assert.NotNull(body);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("model").GetString();
    }

    private static string? MaskedHeader(string? headersJson, string name)
    {
        Assert.NotNull(headersJson);
        using var document = JsonDocument.Parse(headersJson);
        return document.RootElement.GetProperty(name)[0].GetString();
    }
}
