using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// End-to-end coverage of the orchestrator wiring (orchestrator.go:172-179,
/// 355-368): a Responses turn is remembered after the pipeline finishes and the
/// next previous_response_id is expanded before the inbound transformer runs.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class ResponsesSessionOrchestratorTests(GrainClusterFixture fixture)
{
    private const string Model = "responses-session-model";

    private static readonly RequestIdentity Identity = new(41, 51)
    {
        Project = new ProjectRecord { Id = 41 },
    };

    private static HttpRequestData ResponsesRequest(string body)
        => new()
        {
            Method = "POST",
            Url = "http://gateway.test/v1/responses",
            Path = "/v1/responses",
            Query = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(body),
            Auth = new AuthConfig(),
        };

    private static ChannelCandidate CandidateFor(string model, long channelId)
        => new()
        {
            ChannelId = channelId,
            ChannelName = "channel-" + channelId,
            ActualModel = model,
            Type = "openai",
            // Pin the chat transformer: a null ApiFormat would dispatch on the
            // inbound Responses format and proxy the body unchanged, which
            // would not exercise the chat -> Responses stream aggregation the
            // session recorder depends on.
            ApiFormat = ApiFormats.OpenAIChatCompletion,
            BaseUrl = "http://channel-" + channelId + ".test",
            Credentials = "{\"apiKey\":\"sk-" + channelId + "\"}",
        };

    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        ResponsesSessionStore store)
        => new(
            fixture.Cluster.GrainFactory,
            new ResponsesInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(new RetryPolicy
            {
                Enabled = false,
                MaxChannelRetries = 0,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
            }),
            new FixedSelector(CandidateFor(Model, 6201)),
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager,
            responsesSessions: store);

    private static string MessageText(JsonElement message)
    {
        var content = message.GetProperty("content");
        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        return string.Concat(content.EnumerateArray()
            .Select(part => part.TryGetProperty("text", out var text) ? text.GetString() : null));
    }

    [Fact]
    public async Task MergesAndRecordsAcrossRequests()
    {
        fixture.Channels.Set();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion(Model, "world"),
        };
        var store = new ResponsesSessionStore();
        var orchestrator = NewOrchestrator(executor, store);

        var firstRequest = ResponsesRequest(
            """{"model":"responses-session-model","input":"hello"}""");
        var first = await orchestrator.ProcessAsync(
            firstRequest,
            Identity,
            onRequestCreated: null);

        Assert.NotNull(first.Response);
        using var firstBody = JsonDocument.Parse(first.Response!.Body!);
        var responseId = firstBody.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(responseId));
        Assert.StartsWith("responses-", firstRequest.SessionId, StringComparison.Ordinal);

        var recorded = store.Lookup(responseId!, Identity.SessionScope);
        Assert.NotNull(recorded);
        Assert.Single(recorded!.Input);
        Assert.Single(recorded.Output);

        var secondRequest = ResponsesRequest(
            """{"model":"responses-session-model","input":"again","previous_response_id":"ID"}"""
                .Replace("ID", responseId!, StringComparison.Ordinal));
        var second = await orchestrator.ProcessAsync(
            secondRequest,
            Identity,
            onRequestCreated: null);

        Assert.NotNull(second.Response);
        Assert.Equal(2, executor.Requests.Count);

        using var upstreamBody = JsonDocument.Parse(executor.Requests[1].Body!);
        var messages = upstreamBody.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(3, messages.Count);
        Assert.Equal("user", messages[0].GetProperty("role").GetString());
        Assert.Equal("hello", MessageText(messages[0]));
        Assert.Equal("assistant", messages[1].GetProperty("role").GetString());
        Assert.Equal("world", MessageText(messages[1]));
        Assert.Equal("user", messages[2].GetProperty("role").GetString());
        Assert.Equal("again", MessageText(messages[2]));
    }

    [Fact]
    public async Task RecordsACompletedStream()
    {
        fixture.Channels.Set();
        StreamEvent[] chunks =
        [
            TestChannels.Data(
                """{"id":"chatcmpl-stream","object":"chat.completion.chunk","created":1,"model":"responses-session-model","choices":[{"index":0,"delta":{"role":"assistant","content":"hi"},"finish_reason":null}]}"""),
            TestChannels.Data(
                """{"id":"chatcmpl-stream","object":"chat.completion.chunk","created":1,"model":"responses-session-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}"""),
            TestChannels.Data("[DONE]"),
        ];
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new SliceStream<StreamEvent>(chunks),
        };
        var store = new ResponsesSessionStore();
        var orchestrator = NewOrchestrator(executor, store);

        var request = ResponsesRequest(
            """{"model":"responses-session-model","input":"hello","stream":true}""");
        var result = await orchestrator.ProcessAsync(
            request,
            Identity,
            onRequestCreated: null);

        Assert.NotNull(result.Stream);
        while (result.Stream!.Next())
        {
        }

        result.Stream.Dispose();

        var key = Assert.Single(store.ByResponse.Keys);
        Assert.Equal(Identity.SessionScope, key.Scope);
        Assert.Equal("chatcmpl-stream", key.ResponseId);

        var prepared = await store.PrepareAsync(
            Encoding.UTF8.GetBytes(
                """{"model":"responses-session-model","previous_response_id":"chatcmpl-stream","input":"again"}"""),
            request.SessionId,
            Identity.SessionScope,
            Identity);

        using var preparedBody = JsonDocument.Parse(prepared.Body!);
        var items = preparedBody.RootElement.GetProperty("input").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("hello", items[0].GetProperty("content").GetString());
        Assert.Equal("hi", items[1].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("again", items[2].GetProperty("content").GetString());
        Assert.False(preparedBody.RootElement.TryGetProperty("previous_response_id", out _));
    }

    private sealed class FixedSelector(params ChannelCandidate[] candidates) : IModelCandidateSelector
    {
        public Task<CandidateSelection> SelectAsync(
            LlmRequest request,
            RequestIdentity identity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new CandidateSelection(candidates, RoutingPolicy: null));
    }
}
