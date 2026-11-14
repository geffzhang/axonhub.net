using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI.Responses;
using AxonHub.Llm.Transformer.Shared;
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

    private static ChannelCandidate CandidateFor(
        string model,
        long channelId,
        string apiFormat)
        => new()
        {
            ChannelId = channelId,
            ChannelName = "channel-" + channelId,
            ActualModel = model,
            Type = "openai",
            // A null ApiFormat dispatches on the inbound Responses format and
            // proxies the body unchanged. The capture path
            // (responses_capture.go:99-165) only remembers raw provider
            // `response.completed` bodies, so the tests pin the Responses
            // transformer and script Responses-format provider payloads.
            ApiFormat = apiFormat,
            BaseUrl = "http://channel-" + channelId + ".test",
            Credentials = "{\"apiKey\":\"sk-" + channelId + "\"}",
            // responses_attempt.go:167-202 matches the owner by channel, format
            // and actual model, so the candidate must carry its model entry.
            Models =
            [
                new ChannelCandidateModel
                {
                    RequestModel = model,
                    ActualModel = model,
                    Source = "direct",
                },
            ],
        };

    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        ResponsesSessionStore store,
        string apiFormat = ApiFormats.OpenAIResponse,
        IReadOnlyList<ChannelCandidate>? candidates = null,
        int maxChannelRetries = 0)
        => new(
            fixture.Cluster.GrainFactory,
            new ResponsesInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            // select_candidates.go:80 truncates the ranked candidates to
            // 1 + MaxChannelRetries, so a multi-candidate continuation test has
            // to keep the channel retry budget open. Round-robin preserves the
            // selector order on equal request counts, which makes the ranked
            // list deterministic across the shared fixture.
            new StaticRetryPolicyProvider(new RetryPolicy
            {
                Enabled = maxChannelRetries > 0,
                MaxChannelRetries = maxChannelRetries,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
                LoadBalancerStrategy = RetryPolicy.RoundRobinStrategy,
            }),
            new FixedSelector(candidates is null
                ? [CandidateFor(Model, 6201, apiFormat)]
                : [.. candidates]),
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
            OnDo = (_, _, _) => ResponsesBody("resp_provider_1", "world"),
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
        Assert.StartsWith("responses-", firstRequest.SessionId, StringComparison.Ordinal);

        // The continuation records the provider's own response identity and
        // body (responses_capture.go:113-165), not the client-visible id.
        var recorded = store.Lookup("resp_provider_1", Identity.SessionScope);
        Assert.NotNull(recorded);
        Assert.Single(recorded!.Input);
        Assert.Single(recorded.Output);

        var secondRequest = ResponsesRequest(
            """{"model":"responses-session-model","input":"again","previous_response_id":"resp_provider_1"}""");
        var second = await orchestrator.ProcessAsync(
            secondRequest,
            Identity,
            onRequestCreated: null);

        Assert.NotNull(second.Response);
        Assert.Equal(2, executor.Requests.Count);

        using var upstreamBody = JsonDocument.Parse(executor.Requests[1].Body!);
        var items = upstreamBody.RootElement.GetProperty("input").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("user", items[0].GetProperty("role").GetString());
        Assert.Equal("hello", MessageText(items[0]));
        Assert.Equal("assistant", items[1].GetProperty("role").GetString());
        Assert.Equal("world", MessageText(items[1]));
        Assert.Equal("user", items[2].GetProperty("role").GetString());
        Assert.Equal("again", MessageText(items[2]));
    }

    [Fact]
    public async Task RecordsACompletedStream()
    {
        fixture.Channels.Set();
        StreamEvent[] chunks =
        [
            TestChannels.Data(
                """{"type":"response.created","response":{"id":"resp_stream","object":"response","created_at":1,"model":"responses-session-model","status":"in_progress","output":[]}}""",
                "response.created"),
            TestChannels.Data(
                """{"type":"response.output_item.done","output_index":0,"item":{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"hi","annotations":[]}]}}""",
                "response.output_item.done"),
            TestChannels.Data(
                """{"type":"response.completed","response":{"id":"resp_stream","object":"response","created_at":1,"model":"responses-session-model","status":"completed","output":[{"id":"msg_1","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"hi","annotations":[]}]}]}}""",
                "response.completed"),
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

        // The capture observer only accepts the raw provider `response.completed`
        // payload (responses_capture.go:36-56); a chat-completion stream has no
        // such event, so this test pins a Responses-format channel.
        var key = Assert.Single(store.ByResponse.Keys);
        Assert.Equal(Identity.SessionScope, key.Scope);
        Assert.Equal("resp_stream", key.ResponseId);

        var recorded = store.Lookup("resp_stream", Identity.SessionScope);
        Assert.NotNull(recorded);
        Assert.Single(recorded!.Input);
        Assert.Single(recorded.Output);
        Assert.True(recorded.Complete);

        var prepared = await store.PrepareAsync(
            Encoding.UTF8.GetBytes(
                """{"model":"responses-session-model","previous_response_id":"resp_stream","input":"again"}"""),
            request.SessionId,
            Identity.SessionScope,
            Identity);

        using var preparedBody = JsonDocument.Parse(prepared.Body!);
        var items = preparedBody.RootElement.GetProperty("input").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("user", items[0].GetProperty("role").GetString());
        Assert.Equal("hello", MessageText(items[0]));
        Assert.Equal("assistant", items[1].GetProperty("role").GetString());
        Assert.Equal("hi", MessageText(items[1]));
        Assert.Equal("user", items[2].GetProperty("role").GetString());
        Assert.Equal("again", MessageText(items[2]));
        Assert.False(preparedBody.RootElement.TryGetProperty("previous_response_id", out _));
    }

    [Fact]
    public async Task CompleteHistoryKeepsRankedCandidate()
    {
        fixture.Channels.Set();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ResponsesBody("resp_first", "world"),
        };
        var store = new ResponsesSessionStore();

        // The first turn lands on channel 6222 and records a complete snapshot.
        var firstOrchestrator = NewOrchestrator(
            executor,
            store,
            candidates: [CandidateFor(Model, 6222, ApiFormats.OpenAIResponse)]);
        var firstRequest = ResponsesRequest(
            """{"model":"responses-session-model","input":"hello"}""");
        var first = await firstOrchestrator.ProcessAsync(
            firstRequest,
            Identity,
            onRequestCreated: null);
        Assert.NotNull(first.Response);

        // A ranked-first channel that is not the owner still serves the replay:
        // a complete snapshot can be expanded and does not pin the origin.
        var secondOrchestrator = NewOrchestrator(
            executor,
            store,
            candidates:
            [
                CandidateFor(Model, 6221, ApiFormats.OpenAIResponse),
                CandidateFor(Model, 6222, ApiFormats.OpenAIResponse),
            ],
            maxChannelRetries: 1);
        var secondRequest = ResponsesRequest(
            """{"model":"responses-session-model","input":"again","previous_response_id":"resp_first"}""");
        var second = await secondOrchestrator.ProcessAsync(
            secondRequest,
            Identity,
            onRequestCreated: null);

        Assert.NotNull(second.Response);
        Assert.Equal(2, executor.Requests.Count);
        Assert.StartsWith("http://channel-6221.test", executor.Requests[1].Url, StringComparison.Ordinal);

        using var upstreamBody = JsonDocument.Parse(executor.Requests[1].Body!);
        var items = upstreamBody.RootElement.GetProperty("input").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal("hello", MessageText(items[0]));
        Assert.Equal("world", MessageText(items[1]));
        Assert.Equal("again", MessageText(items[2]));
    }

    [Fact]
    public async Task KnownWithoutRestorableSnapshotFailsBeforeDispatch()
    {
        fixture.Channels.Set();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ResponsesBody("resp_known", "world"),
        };

        // responses_attempt.go:101-105: persistence knows the provider id but
        // the store cannot restore it, so the turn is a client-facing recovery
        // rather than an upstream miss. The provider must never be called.
        var store = new ResponsesSessionStore(
            known: (_, _) => Task.FromResult(true));
        var orchestrator = NewOrchestrator(executor, store);

        var request = ResponsesRequest(
            """{"model":"responses-session-model","input":"delta","previous_response_id":"resp_persisted"}""");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => orchestrator.ProcessAsync(
            request,
            Identity,
            onRequestCreated: null));

        Assert.True(GoErrors.Is(error, ResponsesRecovery.Sentinel));
        var wire = Assert.IsType<HttpClientError>(GoErrors.As<HttpClientError>(error));
        Assert.Equal(400, wire.StatusCode);

        using var body = JsonDocument.Parse(wire.Body);
        Assert.Equal(
            "previous_response_not_found",
            body.RootElement.GetProperty("error").GetProperty("code").GetString());

        Assert.Empty(executor.Requests);
        Assert.Empty(store.ByResponse);
    }

    /// <summary>A raw Responses provider body with one completed message item.</summary>
    private static HttpResponseData ResponsesBody(string id, string text)
        => new()
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(
                "{\"id\":\"" + id + "\",\"object\":\"response\",\"created_at\":1,"
                + "\"model\":\"responses-session-model\",\"status\":\"completed\","
                + "\"output\":[{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\","
                + "\"status\":\"completed\",\"content\":[{\"type\":\"output_text\",\"text\":\""
                + text + "\",\"annotations\":[]}]}]}"),
        };

    private sealed class FixedSelector(params ChannelCandidate[] candidates) : IModelCandidateSelector
    {
        public Task<CandidateSelection> SelectAsync(
            LlmRequest request,
            RequestIdentity identity,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new CandidateSelection(candidates, RoutingPolicy: null));
    }
}
