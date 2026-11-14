using System.Text.Json;
using AxonHub.Api.Http;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Api.Tests;

/// <summary>
/// Behavioural tests for the chat slice: candidate selection goes through the
/// real pool/channel grains, while the transport is scripted. Each test uses its
/// own model and channel IDs so the in-process cluster's grain state is isolated.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class ChatCompletionOrchestratorTests(GrainClusterFixture fixture)
{
    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        IChannelMetricsRecorder metrics,
        RetryPolicy policy,
        IRequestChainRecorder? recorder = null)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            metrics,
            new StaticRetryPolicyProvider(policy),
            fixture.CandidateSelector,
            TimeProvider.System,
            recorder,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

    private static RetryPolicy FastPolicy(int maxChannelRetries = 3, int maxSameChannelRetries = 2)
        => new()
        {
            Enabled = true,
            MaxChannelRetries = maxChannelRetries,
            MaxSingleChannelRetries = maxSameChannelRetries,
            RetryDelayMs = 0,
        };

    [Fact]
    public async Task NoCandidatesReturnsInvalidModelError()
    {
        fixture.Channels.Set();

        var executor = new ScriptedExecutor();
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder(), FastPolicy());

        var ex = await Assert.ThrowsAsync<TransformerException>(
            () => orchestrator.ProcessAsync(TestChannels.ChatRequest("missing-model")));

        Assert.Equal(TransformerErrorKind.InvalidModel, ex.Kind);
        Assert.Equal("model not found: missing-model", ex.Message);

        var clientError = orchestrator.Inbound.TransformError(ex);
        Assert.Equal(422, clientError.StatusCode);

        using var document = JsonDocument.Parse(clientError.Body!);
        Assert.Equal(
            "invalid_model_error",
            document.RootElement.GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(
            "model not found: missing-model",
            document.RootElement.GetProperty("error").GetProperty("message").GetString());
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task NonStreamSelectsChannelRewritesModelAndUsesChannelApiKey()
    {
        fixture.Channels.Set(new AxonHub.Abstractions.ChannelDescriptor
        {
            Id = 4101,
            Name = "mapping-channel",
            OrderingWeight = 0,
            Type = "openai",
            BaseUrl = "http://upstream.test",
            Credentials = "{\"apiKey\":\"sk-mapped\"}",
            SupportedModels = "[\"upstream-a\"]",
            Settings = "{\"modelMappings\":[{\"from\":\"client-a\",\"to\":\"upstream-a\"}]}",
        });

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("upstream-a", "hi there"),
        };
        var metrics = new RecordingMetricsRecorder();
        var orchestrator = NewOrchestrator(executor, metrics, FastPolicy());

        var result = await orchestrator.ProcessAsync(TestChannels.ChatRequest("client-a"));

        Assert.NotNull(result.Response);
        Assert.Equal(200, result.Response!.StatusCode);

        var request = Assert.Single(executor.Requests);
        Assert.Equal("http://upstream.test/v1/chat/completions", request.Url);
        Assert.Equal(
            "Bearer sk-mapped",
            HttpClientUtils.HeaderGet(request.Headers, "Authorization"));

        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("upstream-a", body.RootElement.GetProperty("model").GetString());

        using var response = JsonDocument.Parse(result.Response.Body!);
        Assert.Equal("hi there", response.RootElement
            .GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());

        Assert.Equal(new long[] { 4101 }, metrics.Successes);
        Assert.Empty(metrics.Failures);
    }

    [Fact]
    public async Task TraceIdSelectsTheSameChannelApiKeyAcrossRequests()
    {
        // channel_apikey_provider.go: with several enabled keys and a trace in
        // the context, TraceStickyKeyProvider picks by rendezvous hashing over
        // "traceID|key". WithTrace publishes the trace id as the session id, so
        // the wire request carries it to the outbound factory (C013).
        fixture.Channels.Set(new AxonHub.Abstractions.ChannelDescriptor
        {
            Id = 4102,
            Name = "sticky-key-channel",
            OrderingWeight = 0,
            Type = "openai",
            BaseUrl = "http://upstream.test",
            Credentials = "{\"apiKeys\":[\"sk-one\",\"sk-two\",\"sk-three\"]}",
            SupportedModels = "[\"sticky-model\"]",
        });

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("sticky-model", "ok"),
        };
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder(), FastPolicy());

        var first = TestChannels.ChatRequest("sticky-model");
        first.SessionId = "trace-sticky-key";
        await orchestrator.ProcessAsync(first);

        var second = TestChannels.ChatRequest("sticky-model");
        second.SessionId = "trace-sticky-key";
        await orchestrator.ProcessAsync(second);

        string[] keys = ["sk-one", "sk-two", "sk-three"];
        var expected = ChannelApiKeyProvider.RendezvousSelect(keys, "trace-sticky-key");

        Assert.Equal(2, executor.Requests.Count);
        Assert.All(
            executor.Requests,
            request => Assert.Equal(
                "Bearer " + expected,
                HttpClientUtils.HeaderGet(request.Headers, "Authorization")));
    }

    [Fact]
    public async Task NonStreamGrainRecorderUpdatesChannelMetrics()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4701, "grain-metrics-model"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("grain-metrics-model"),
        };
        var recorder = new GrainChannelMetricsRecorder(fixture.Cluster.GrainFactory);
        var orchestrator = NewOrchestrator(executor, recorder, FastPolicy());

        await orchestrator.ProcessAsync(TestChannels.ChatRequest("grain-metrics-model"));

        var snapshot = await fixture.Cluster.GrainFactory
            .GetGrain<AxonHub.Grains.Abstractions.IChannelGrain>(4701)
            .GetMetricsAsync(DateTimeOffset.UtcNow);

        // One initial selection (recorded by the pool grain) and one success
        // (recorded by the metrics middleware through the real recorder).
        Assert.Equal(1, snapshot.RequestCount);
        Assert.Equal(1, snapshot.SuccessCount);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
    }

    [Fact]
    public async Task NonStreamServerErrorSwitchesToNextChannel()
    {
        fixture.Channels.Set(
            TestChannels.OpenAi(4201, "failover-model"),
            TestChannels.OpenAi(4202, "failover-model"));

        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call == 0
                ? throw TestChannels.HttpClient(500)
                : TestChannels.Completion("failover-model"),
        };
        var metrics = new RecordingMetricsRecorder();
        var orchestrator = NewOrchestrator(executor, metrics, FastPolicy(maxSameChannelRetries: 0));

        var result = await orchestrator.ProcessAsync(TestChannels.ChatRequest("failover-model"));

        Assert.Equal(200, result.Response!.StatusCode);
        Assert.Equal(2, executor.Calls);
        Assert.Equal(new long[] { 4201 }, metrics.Failures);
        Assert.Equal(new long[] { 4202 }, metrics.Successes);

        // Only the retry selection goes through the recorder; the initial
        // selection is counted by the pool grain (TrackSelection).
        Assert.Equal(new long[] { 4202 }, metrics.Selections);
    }

    [Fact]
    public async Task NonStream429SkipsSameChannelRetry()
    {
        fixture.Channels.Set(
            TestChannels.OpenAi(4301, "rate-limit-model"),
            TestChannels.OpenAi(4302, "rate-limit-model"));

        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call == 0
                ? throw TestChannels.HttpClient(429)
                : TestChannels.Completion("rate-limit-model"),
        };
        var metrics = new RecordingMetricsRecorder();
        var orchestrator = NewOrchestrator(executor, metrics, FastPolicy(maxSameChannelRetries: 2));

        var result = await orchestrator.ProcessAsync(TestChannels.ChatRequest("rate-limit-model"));

        Assert.Equal(200, result.Response!.StatusCode);
        Assert.Equal(2, executor.Calls);
        Assert.Equal(new long[] { 4301 }, metrics.Failures);
        Assert.Equal(new long[] { 4302 }, metrics.Successes);
        Assert.Equal(new long[] { 4302 }, metrics.Selections);
    }

    [Fact]
    public async Task NonStreamRetryableErrorRetriesSameChannelUpToBudget()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4401, "same-channel-model"));

        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call < 2
                ? throw TestChannels.HttpClient(500)
                : TestChannels.Completion("same-channel-model"),
        };
        var metrics = new RecordingMetricsRecorder();
        var orchestrator = NewOrchestrator(executor, metrics, FastPolicy(maxSameChannelRetries: 2));

        var result = await orchestrator.ProcessAsync(TestChannels.ChatRequest("same-channel-model"));

        Assert.Equal(200, result.Response!.StatusCode);
        Assert.Equal(3, executor.Calls);
        Assert.All(executor.Requests, request => Assert.Equal("http://upstream.test/v1/chat/completions", request.Url));
        Assert.Equal(new long[] { 4401, 4401 }, metrics.Failures);
        Assert.Equal(new long[] { 4401 }, metrics.Successes);

        // One recorded selection per same-channel retry.
        Assert.Equal(new long[] { 4401, 4401 }, metrics.Selections);
    }

    [Fact]
    public async Task RetryableStatusCodesSettingRetriesConfigured400()
    {
        fixture.Channels.Set(TestChannels.OpenAi(
            4601,
            "configured-status-model",
            settings: "{\"retryableStatusCodes\":[400]}"));

        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call == 0
                ? throw TestChannels.HttpClient(400)
                : TestChannels.Completion("configured-status-model"),
        };
        var metrics = new RecordingMetricsRecorder();
        var orchestrator = NewOrchestrator(executor, metrics, FastPolicy(maxSameChannelRetries: 1));

        var result = await orchestrator.ProcessAsync(TestChannels.ChatRequest("configured-status-model"));

        Assert.Equal(200, result.Response!.StatusCode);
        Assert.Equal(2, executor.Calls);
        Assert.Equal(new long[] { 4601 }, metrics.Selections);
    }

    [Fact]
    public async Task RetryableErrorPatternsSettingRetriesMatchingText()
    {
        fixture.Channels.Set(TestChannels.OpenAi(
            4602,
            "configured-pattern-model",
            settings: "{\"retryableErrorPatterns\":[{\"pattern\":\"Console API returned 403\"}]}"));

        var executor = new ScriptedExecutor
        {
            OnDo = (call, _, _) => call == 0
                ? throw TestChannels.HttpClient(
                    403,
                    "{\"error\":{\"message\":\"Console API returned 403\",\"type\":\"upstream_error\"}}")
                : TestChannels.Completion("configured-pattern-model"),
        };
        var metrics = new RecordingMetricsRecorder();
        var orchestrator = NewOrchestrator(executor, metrics, FastPolicy(maxSameChannelRetries: 1));

        var result = await orchestrator.ProcessAsync(TestChannels.ChatRequest("configured-pattern-model"));

        Assert.Equal(200, result.Response!.StatusCode);
        Assert.Equal(2, executor.Calls);
        Assert.Equal(new long[] { 4602 }, metrics.Selections);
    }

    [Fact]
    public async Task Unconfigured400FailsWithoutSameChannelRetry()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4603, "plain-400-model"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw TestChannels.HttpClient(400),
        };
        var metrics = new RecordingMetricsRecorder();
        var orchestrator = NewOrchestrator(executor, metrics, FastPolicy(maxSameChannelRetries: 2));

        await Assert.ThrowsAnyAsync<Exception>(
            () => orchestrator.ProcessAsync(TestChannels.ChatRequest("plain-400-model")));

        Assert.Single(executor.Requests);
        Assert.Empty(metrics.Selections);
    }

    [Fact]
    public async Task NonStreamRetryableErrorWithoutRetryBudgetThrowsFirstError()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4501, "no-retry-model"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw TestChannels.HttpClient(500),
        };
        var metrics = new RecordingMetricsRecorder();
        var orchestrator = NewOrchestrator(executor, metrics, new RetryPolicy { Enabled = false });

        var ex = await Assert.ThrowsAnyAsync<Exception>(
            () => orchestrator.ProcessAsync(TestChannels.ChatRequest("no-retry-model")));

        Assert.Single(executor.Requests);
        Assert.Equal(500, AxonHub.Llm.Pipeline.GoErrors.As<ResponseError>(ex)!.StatusCode);
        Assert.Equal(new long[] { 4501 }, metrics.Failures);
        Assert.Empty(metrics.Successes);
    }

    [Fact]
    public async Task RetryPolicyMaxChannelRetriesExtendsTheCandidatePool()
    {
        // The pool must honor a policy larger than the reference default
        // (1 + 3); six channels and five channel retries exhaust all six.
        fixture.Channels.Set(
            TestChannels.OpenAi(5601, "wide-pool-model"),
            TestChannels.OpenAi(5602, "wide-pool-model"),
            TestChannels.OpenAi(5603, "wide-pool-model"),
            TestChannels.OpenAi(5604, "wide-pool-model"),
            TestChannels.OpenAi(5605, "wide-pool-model"),
            TestChannels.OpenAi(5606, "wide-pool-model"));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw TestChannels.HttpClient(500),
        };
        var metrics = new RecordingMetricsRecorder();
        var orchestrator = NewOrchestrator(
            executor,
            metrics,
            FastPolicy(maxChannelRetries: 5, maxSameChannelRetries: 0));

        await Assert.ThrowsAnyAsync<Exception>(
            () => orchestrator.ProcessAsync(TestChannels.ChatRequest("wide-pool-model")));

        Assert.Equal(6, executor.Calls);
        Assert.Equal(6, metrics.Failures.Count);
    }

    [Fact]
    public async Task LoadBalancerStrategyFromPolicySelectsTheChain()
    {
        fixture.Channels.Set(
            WeightedChannel(5701, "failover-strategy-model", "http://low.test", weight: 10),
            WeightedChannel(5702, "failover-strategy-model", "http://high.test", weight: 100),
            WeightedChannel(5711, "round-robin-strategy-model", "http://low.test", weight: 10),
            WeightedChannel(5712, "round-robin-strategy-model", "http://high.test", weight: 100));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("selected"),
        };
        var metrics = new RecordingMetricsRecorder();

        // failover = Weight + Random + RateLimitAware: the weight gap decides.
        var failover = NewOrchestrator(
            executor,
            metrics,
            FastPolicy() with { LoadBalancerStrategy = RetryPolicy.FailoverStrategy });
        await failover.ProcessAsync(TestChannels.ChatRequest("failover-strategy-model"));

        Assert.StartsWith("http://high.test", executor.Requests[^1].Url, StringComparison.Ordinal);

        // round-robin disables the weight tie-breaker: equal scores keep the
        // provider order, so the first enabled channel wins despite its weight.
        var roundRobin = NewOrchestrator(
            executor,
            metrics,
            FastPolicy() with { LoadBalancerStrategy = RetryPolicy.RoundRobinStrategy });
        await roundRobin.ProcessAsync(TestChannels.ChatRequest("round-robin-strategy-model"));

        Assert.StartsWith("http://low.test", executor.Requests[^1].Url, StringComparison.Ordinal);
    }

    private static AxonHub.Abstractions.ChannelDescriptor WeightedChannel(
        long id,
        string model,
        string baseUrl,
        long weight) => new()
        {
            Id = id,
            Name = "channel-" + id,
            OrderingWeight = weight,
            Type = "openai",
            BaseUrl = baseUrl,
            Credentials = "{\"apiKey\":\"sk-test\"}",
            SupportedModels = "[\"" + model + "\"]",
        };

    [Fact]
    public async Task NonStreamTimeoutSkipsSameChannelRetryAndSwitchesChannel()
    {
        fixture.Channels.Set(
            TestChannels.OpenAi(4901, "non-stream-timeout-model"),
            TestChannels.OpenAi(4902, "non-stream-timeout-model"));

        var executor = new ScriptedExecutor
        {
            // A stalled upstream honors nothing but the attempt deadline. The
            // pipeline must classify the cancellation as a response timeout and
            // skip the same-channel budget, because retrying cannot inform us.
            OnDo = (call, _, token) => call == 0
                ? WaitForCancellation(token)
                : TestChannels.Completion("non-stream-timeout-model"),
        };
        var metrics = new RecordingMetricsRecorder();
        var policy = FastPolicy() with
        {
            MaxSingleChannelRetries = 2,
            NonStreamResponseTimeoutSeconds = 1,
        };
        var orchestrator = NewOrchestrator(executor, metrics, policy);

        var result = await orchestrator.ProcessAsync(TestChannels.ChatRequest("non-stream-timeout-model"));

        Assert.Equal(200, result.Response!.StatusCode);
        Assert.Equal(2, executor.Calls);
        Assert.Equal(new long[] { 4902 }, metrics.Selections);
        Assert.Equal(new long[] { 4902 }, metrics.Successes);

        // The reference records a timed-out attempt as canceled, not failed:
        // performance.go checks errors.Is(err, context.Canceled) first.
        Assert.Empty(metrics.Failures);
    }

    [Fact]
    public async Task StreamFirstEventTimeoutSkipsSameChannelRetryAndRecordsFailure()
    {
        fixture.Channels.Set(
            TestChannels.OpenAi(4911, "first-event-timeout-model"),
            TestChannels.OpenAi(4912, "first-event-timeout-model"));

        var chunks = new List<StreamEvent>
        {
            TestChannels.Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"first-event-timeout-model\",\"choices\":[{\"index\":0,"
                + "\"delta\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":null}]}"),
            TestChannels.Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"first-event-timeout-model\",\"choices\":[{\"index\":0,\"delta\":{},"
                + "\"finish_reason\":\"stop\"}]}"),
            TestChannels.Data("[DONE]"),
        };
        var executor = new ScriptedExecutor
        {
            OnStream = (call, _, token) => call == 0
                ? new BlockingFirstEventStream(token)
                : new ListStream<StreamEvent>(chunks),
        };
        var metrics = new RecordingMetricsRecorder();
        var policy = FastPolicy() with { StreamFirstEventTimeoutSeconds = 1 };

        // The persistent-stream counterpart classifies the terminal event; the
        // metrics middleware only reports the record it marks.
        var orchestrator = NewOrchestrator(executor, metrics, policy, new RecordingRequestChainRecorder());

        var result = await orchestrator.ProcessAsync(
            TestChannels.ChatRequest("first-event-timeout-model", stream: true));

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

        Assert.Equal("[DONE]", System.Text.Encoding.UTF8.GetString(events[^1].Data));
        Assert.Equal(2, executor.Calls);
        Assert.Equal(new long[] { 4912 }, metrics.Selections);

        // A first-event timeout reaches the raw-error hook as itself, not as a
        // bare cancellation, so the stalled channel takes a failure mark.
        Assert.Equal(new long[] { 4911 }, metrics.Failures);
        Assert.Equal(new long[] { 4912 }, metrics.Successes);
    }

    private static HttpResponseData WaitForCancellation(CancellationToken token)
    {
        // The safety timeout only keeps a broken test from hanging the suite.
        _ = token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
        throw new OperationCanceledException(token);
    }

    [Fact]
    public async Task StreamPassesEventsThroughAndRecordsSuccessOnTerminal()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4601, "stream-model"));

        var chunks = new List<StreamEvent>
        {
            TestChannels.Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"stream-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"he\"},\"finish_reason\":null}]}"),
            TestChannels.Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"stream-model\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"llo\"},\"finish_reason\":null}]}"),
            TestChannels.Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"stream-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],"
                + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":3}}"),
            TestChannels.Data("[DONE]"),
        };

        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(chunks),
        };
        var metrics = new RecordingMetricsRecorder();
        var orchestrator = NewOrchestrator(
            executor,
            metrics,
            FastPolicy(),
            new RecordingRequestChainRecorder());

        var result = await orchestrator.ProcessAsync(TestChannels.ChatRequest("stream-model", stream: true));

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

        Assert.True(events.Count >= 2);
        Assert.Equal("[DONE]", System.Text.Encoding.UTF8.GetString(events[^1].Data));
        Assert.Equal(new long[] { 4601 }, metrics.Successes);
        Assert.Empty(metrics.Failures);
    }

    /// <summary>
    /// orchestrator.go:360-375 + api/chat.go:146-159 (C094): the forwardable
    /// response metadata the pipeline attaches to the inbound stream must
    /// survive the per-attempt teardown wrapper, otherwise
    /// writeForwardResponseHeaders has nothing to read and X-Reasoning-Included
    /// is silently dropped.
    /// </summary>
    [Fact]
    public async Task StreamKeepsForwardableResponseHeadersForTheEndpoint()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4603, "header-model"));

        var chunks = new List<StreamEvent>
        {
            TestChannels.Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"header-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":null}]}"),
            TestChannels.Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"header-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}"),
            TestChannels.Data("[DONE]"),
        };
        var upstreamHeaders = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            [ResponseHeaders.ReasoningIncludedHeader] = ["true"],
        };

        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => ResponseHeaders.WithResponseHeaders(
                new ListStream<StreamEvent>(chunks),
                upstreamHeaders),
        };
        var metrics = new RecordingMetricsRecorder();
        // The persistence middleware owns markStreamTerminal for chat streams,
        // and it is only installed when a recorder is present.
        var orchestrator = NewOrchestrator(
            executor,
            metrics,
            FastPolicy(),
            new RecordingRequestChainRecorder());

        var result = await orchestrator.ProcessAsync(TestChannels.ChatRequest("header-model", stream: true));

        Assert.NotNull(result.Stream);

        // The openai chat outbound does not advertise the codex contract; the
        // metadata itself is preserved regardless (C094 item 7).
        Assert.False(result.CodexResponseHeadersSupported);
        var attached = ResponseHeaders.GetResponseHeaders(result.Stream);
        Assert.NotNull(attached);
        Assert.True(attached.TryGetValue(ResponseHeaders.ReasoningIncludedHeader, out var values));
        Assert.Equal("true", Assert.Single(values));

        while (result.Stream!.Next())
        {
            _ = result.Stream.Current;
        }

        result.Stream.Dispose();
        Assert.Equal(new long[] { 4603 }, metrics.Successes);
    }

    /// <summary>
    /// A transport error that ends a stream without a terminal event is a
    /// persistence failure, but the reference's markPerformanceTerminal never
    /// runs for it: the channel keeps neither a success nor a failure (C017).
    /// </summary>
    [Fact]
    public async Task StreamTransportErrorWithoutTerminalEventDoesNotTouchChannelHealth()
    {
        fixture.Channels.Set(TestChannels.OpenAi(5101, "no-terminal-model"));

        var chunks = new List<StreamEvent>
        {
            TestChannels.Data("{\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"no-terminal-model\",\"choices\":[{\"index\":0,"
                + "\"delta\":{\"role\":\"assistant\",\"content\":\"par\"},\"finish_reason\":null}]}"),
        };

        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(
                chunks,
                new IOException("connection reset")),
        };
        var metrics = new RecordingMetricsRecorder();
        var orchestrator = NewOrchestrator(
            executor,
            metrics,
            FastPolicy(),
            new RecordingRequestChainRecorder());

        var result = await orchestrator.ProcessAsync(TestChannels.ChatRequest("no-terminal-model", stream: true));

        Assert.NotNull(result.Stream);
        while (result.Stream!.Next())
        {
            _ = result.Stream.Current;
        }

        result.Stream.Dispose();

        Assert.Empty(metrics.Successes);
        Assert.Empty(metrics.Failures);
    }

    /// <summary>
    /// No terminal event, but the buffered chunks aggregate into a complete
    /// response and the transport acknowledged delivery: the reference marks the
    /// attempt successful from OutboundPersistentStream.Close's
    /// aggregated-completion branch. The acknowledgement matters twice: an
    /// unacknowledged close cancels the execution, and with a canceled execution
    /// the reference only trusts a multi-choice aggregate after every expected
    /// choice finished (outbound.go:294-296) — here the provider sent one of the
    /// two choices.
    /// </summary>
    [Fact]
    public async Task AggregatedCompletionWithoutTerminalEventCountsSuccess()
    {
        fixture.Channels.Set(TestChannels.OpenAi(5102, "aggregate-model"));

        var chunks = new List<StreamEvent>
        {
            TestChannels.Data("{\"id\":\"chatcmpl-9\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"aggregate-model\",\"choices\":[{\"index\":0,"
                + "\"delta\":{\"role\":\"assistant\",\"content\":\"hi\"},\"finish_reason\":null}]}"),
            TestChannels.Data("{\"id\":\"chatcmpl-9\",\"object\":\"chat.completion.chunk\",\"created\":1,"
                + "\"model\":\"aggregate-model\",\"choices\":[{\"index\":0,\"delta\":{},"
                + "\"finish_reason\":\"stop\"}],"
                + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}"),
        };

        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) => new ListStream<StreamEvent>(chunks),
        };
        var metrics = new RecordingMetricsRecorder();
        var orchestrator = NewOrchestrator(
            executor,
            metrics,
            FastPolicy(),
            new RecordingRequestChainRecorder());

        var result = await orchestrator.ProcessAsync(
            TestChannels.ChatRequest("aggregate-model", stream: true, n: 2));

        Assert.NotNull(result.Stream);
        while (result.Stream!.Next())
        {
            _ = result.Stream.Current;
        }

        result.Stream.AcknowledgeDelivery();
        result.Stream.Dispose();

        Assert.Equal(new long[] { 5102 }, metrics.Successes);
        Assert.Empty(metrics.Failures);
    }

    /// <summary>
    /// Channel request rewriting runs after the outbound transform and before
    /// request-execution persistence (the reference registers
    /// persistRequestExecution last), so both the wire request and the stored
    /// execution carry the rewritten body and headers.
    /// </summary>
    [Fact]
    public async Task ChannelOverridesApplyToTheOutboundRequestAndTheStoredExecution()
    {
        fixture.Channels.Set(TestChannels.OpenAi(
            5201,
            "override-e2e-model",
            actualModel: "upstream-override-model",
            settings: """
            {
              "modelMappings":[{"from":"override-e2e-model","to":"upstream-override-model"}],
              "bodyOverrideOperations":[
                {"op":"set","path":"temperature","value":"0.1"},
                {"op":"delete","path":"model"},
                {"op":"set","path":"request_model","value":"{{.RequestModel}}"},
                {"op":"set","path":"upstream_model","value":"{{.Model}}"}
              ],
              "headerOverrideOperations":[
                {"op":"set","path":"X-Channel-Model","value":"{{.Model}}"},
                {"op":"delete","path":"X-Remove"}
              ]
            }
            """));

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion("upstream-override-model", "hi there"),
        };
        var recorder = new RecordingRequestChainRecorder();
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder(), FastPolicy(), recorder);

        var clientRequest = TestChannels.ChatRequest("override-e2e-model");
        clientRequest.Headers!["X-Remove"] = ["yes"];

        var result = await orchestrator.ProcessAsync(clientRequest);
        Assert.NotNull(result.Response);

        var sent = Assert.Single(executor.Requests);
        using (var sentBody = JsonDocument.Parse(sent.Body!))
        {
            var root = sentBody.RootElement;
            Assert.Equal(0.1, root.GetProperty("temperature").GetDouble());
            Assert.False(root.TryGetProperty("model", out _));
            Assert.Equal("override-e2e-model", root.GetProperty("request_model").GetString());
            Assert.Equal("upstream-override-model", root.GetProperty("upstream_model").GetString());
        }

        Assert.Equal("upstream-override-model", HttpClientUtils.HeaderGet(sent.Headers, "X-Channel-Model"));
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(sent.Headers, "X-Remove"));

        var execution = Assert.Single(recorder.Executions);
        using (var storedBody = JsonDocument.Parse(execution.RequestBody))
        {
            var root = storedBody.RootElement;
            Assert.Equal(0.1, root.GetProperty("temperature").GetDouble());
            Assert.False(root.TryGetProperty("model", out _));
            Assert.Equal("override-e2e-model", root.GetProperty("request_model").GetString());
        }

        using (var storedHeaders = JsonDocument.Parse(execution.RequestHeaders!))
        {
            var root = storedHeaders.RootElement;
            Assert.Equal(
                "upstream-override-model",
                root.GetProperty("X-Channel-Model")[0].GetString());
            Assert.False(root.TryGetProperty("X-Remove", out _));
        }
    }
}
