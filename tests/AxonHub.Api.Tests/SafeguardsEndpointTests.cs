using System.Text;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Auth;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Api.Safeguards;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/api/safeguards_test.go, safeguards_terminal_test.go
/// and safeguards_concurrency_test.go: request preparation, the nine HTTP
/// delivery shapes, runtime settings reloads, terminal-tail completion and the
/// shared-channel concurrency handoff between the main request and its reviewer.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class SafeguardsEndpointTests(GrainClusterFixture fixture)
{
    private const string AnthropicNormal =
        """{"id":"msg","type":"message","role":"assistant","model":"gpt-test","content":[{"type":"tool_use","id":"tool1","name":"Bash","input":{"command":"pwd"}}],"stop_reason":"tool_use","usage":{"input_tokens":12,"output_tokens":34}}""";

    private const string AnthropicReview =
        """{"id":"review","type":"message","role":"assistant","model":"gpt-test","content":[{"type":"text","text":"{\"verdicts\":{\"tool1\":{\"flagged\":false}}}"}],"stop_reason":"end_turn","usage":{"input_tokens":12,"output_tokens":34}}""";

    private const string ReviewCompletion =
        """{"id":"review","object":"chat.completion","model":"gpt-test","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"{\"verdicts\":{\"tool1\":{\"flagged\":false}}}"}}],"usage":{"prompt_tokens":12,"completion_tokens":34,"total_tokens":46}}""";

    private const string RequestBody =
        """{"model":"gpt-test","max_tokens":128,"messages":[{"role":"user","content":"show cwd"}],"safeguards":[{"type":"dangerous_tool_use","classifier_context":{"trusted_directories":["/tmp"]}}],"stream":false}""";

    private static readonly string[] AnthropicEventTypes =
    [
        "message_start",
        "content_block_start",
        "content_block_delta",
        "content_block_stop",
        "message_delta",
        "message_stop",
    ];

    private static readonly string[] AnthropicEventData =
    [
        """{"type":"message_start","message":{"id":"msg","type":"message","role":"assistant","model":"gpt-test","content":[],"usage":{"input_tokens":12,"output_tokens":0}}}""",
        """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"tool1","name":"Bash","input":{}}}""",
        """{"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"command\":\"pwd\"}"}}""",
        """{"type":"content_block_stop","index":0}""",
        """{"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"input_tokens":12,"output_tokens":34}}""",
        """{"type":"message_stop"}""",
    ];

    private static readonly string[] ChatEventData =
    [
        """{"id":"chat","model":"gpt-test","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"tool1","type":"function","function":{"name":"Bash","arguments":"{\"command\":\"pwd\"}"}}]}}]}""",
        """{"id":"chat","model":"gpt-test","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":12,"completion_tokens":34,"total_tokens":46}}""",
        "[DONE]",
    ];

    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        IRequestChainRecorder? recorder = null,
        PassThroughSettings? passThrough = null,
        ChannelLimiterManager? channelLimiterManager = null)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(new RetryPolicy
            {
                Enabled = true,
                MaxChannelRetries = 3,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
            }),
            fixture.CandidateSelector,
            TimeProvider.System,
            recorder,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: channelLimiterManager ?? fixture.ChannelLimiterManager,
            passThroughSettings: passThrough is null
                ? null
                : new StaticPassThroughSettingsProvider(passThrough));

    private static ChatCompletionOrchestrator WithAnthropicInbound(ChatCompletionOrchestrator orchestrator)
        => orchestrator.WithInbound(new AnthropicInboundTransformer());

    private static ChannelDescriptor AnthropicChannel(long id, string model, string? settings = null)
        => new()
        {
            Id = id,
            Name = "channel-" + id,
            OrderingWeight = 0,
            Type = "anthropic",
            BaseUrl = "http://upstream.test",
            Credentials = "{\"apiKey\":\"sk-test\"}",
            SupportedModels = "[\"" + model + "\"]",
            Settings = settings,
        };

    private static HttpResponseData JsonResponse(string body) => new()
    {
        StatusCode = 200,
        Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
        },
        Body = Encoding.UTF8.GetBytes(body),
    };

    private static List<StreamEvent> AnthropicStream() =>
    [
        .. AnthropicEventData.Select(
            (data, index) => TestChannels.Data(data, AnthropicEventTypes[index])),
    ];

    private static List<StreamEvent> ChatStream() =>
        [.. ChatEventData.Select(static data => TestChannels.Data(data))];

    private static DefaultHttpContext NewContext(string body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.test");
        context.Request.Path = "/v1/messages";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ResponseBody(DefaultHttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static SafeguardsClassifierFunc VerdictClassifier(int[] calls)
        => (_, request) =>
        {
            calls[0]++;
            Assert.Equal(calls[0] == 1 ? "reviewer" : "backup", request.Model);
            Assert.Contains("\"command\":\"pwd\"", request.User, StringComparison.Ordinal);
            return Task.FromResult("""{"verdicts":{"tool1":{"flagged":false}}}""");
        };

    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.True(
            JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement),
            "expected " + expected + " but was " + actual);
    }

    [Fact]
    public void PrepareSafeguardsRequest()
    {
        const string originalBody =
            """{"model":"main","safeguards":[{"type":"dangerous_tool_use"},{"type":"other","setting":1}],"extra":true}""";
        var original = new HttpRequestData
        {
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Length"] = ["123"],
            },
            Body = Encoding.UTF8.GetBytes(originalBody),
        };

        var noReviewer = SafeguardsRequest.Prepare(original, new SafeguardsConfig(), call: null);
        Assert.Null(noReviewer.Evaluator);
        Assert.Same(original, noReviewer.Request);

        var prepared = SafeguardsRequest.Prepare(
            original,
            new SafeguardsConfig { ClassifierModel = "reviewer" },
            call: null);
        Assert.NotNull(prepared.Evaluator);
        Assert.NotSame(original, prepared.Request);
        AssertJsonEqual(
            """{"model":"main","safeguards":[{"type":"other","setting":1}],"extra":true}""",
            Encoding.UTF8.GetString(prepared.Request.Body!));

        // The prepared copy drops the obsolete length; the caller's request
        // keeps both its mutated-in-place body and header.
        Assert.Equal(string.Empty, HttpClientUtils.HeaderGet(prepared.Request.Headers, "Content-Length"));
        Assert.Equal("123", HttpClientUtils.HeaderGet(original.Headers, "Content-Length"));
        Assert.Contains("dangerous_tool_use", Encoding.UTF8.GetString(original.Body!), StringComparison.Ordinal);

        var malformed = new HttpRequestData
        {
            Body = Encoding.UTF8.GetBytes("""{"safeguards":"wrong"}"""),
        };
        Assert.Throws<SafeguardsDecodeException>(() => SafeguardsRequest.Prepare(
            malformed,
            new SafeguardsConfig { ClassifierModel = "reviewer" },
            call: null));
    }

    [Theory]
    [InlineData("normal", "anthropic", false, false, false, false, 4960)]
    [InlineData("passthrough", "anthropic", false, true, false, false, 4961)]
    [InlineData("stream", "anthropic", true, false, false, false, 4962)]
    [InlineData("stream_passthrough", "anthropic", true, true, false, false, 4963)]
    [InlineData("converted_stream", "openai", true, false, false, false, 4964)]
    [InlineData("timeout", "anthropic", false, false, false, true, 4965)]
    [InlineData("stream_timeout", "anthropic", true, false, false, true, 4966)]
    [InlineData("fallback_timeout", "anthropic", false, false, true, true, 4967)]
    [InlineData("stream_fallback_timeout", "anthropic", true, false, true, true, 4968)]
    public async Task SafeguardsHTTP(
        string name,
        string format,
        bool stream,
        bool passthrough,
        bool fallback,
        bool timeout,
        long channelId)
    {
        _ = name;
        var anthropic = string.Equals(format, "anthropic", StringComparison.Ordinal);
        fixture.Channels.Set(anthropic
            ? AnthropicChannel(channelId, "gpt-test")
            : TestChannels.OpenAi(channelId, "gpt-test"));

        var calls = new int[1];
        var reviewer = VerdictClassifier(calls);
        SafeguardsClassifierFunc timedReviewer = async (token, request) =>
        {
            if (timeout && calls[0] == 0)
            {
                // The first attempt waits out its attempt deadline.
                calls[0]++;
                Assert.Equal("reviewer", request.Model);
                Assert.Contains("\"command\":\"pwd\"", request.User, StringComparison.Ordinal);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            return await reviewer(token, request);
        };

        var executor = new ScriptedExecutor();
        if (stream)
        {
            var events = anthropic ? AnthropicStream() : ChatStream();
            executor.OnStream = (_, _, _) => new ListStream<StreamEvent>(events);
        }
        else
        {
            executor.OnDo = (_, _, _) => JsonResponse(AnthropicNormal);
        }

        var config = fallback
            ? new SafeguardsConfig
            {
                ClassifierModel = "reviewer",
                FallbackModels = ["backup"],
                AttemptTimeoutNanoseconds = 10_000_000,
                TimeoutNanoseconds = 1_000_000_000,
            }
            : new SafeguardsConfig
            {
                ClassifierModel = "reviewer",
                TimeoutNanoseconds = 10_000_000,
            };
        var recorder = new RecordingRequestChainRecorder();
        var orchestrator = WithAnthropicInbound(NewOrchestrator(
            executor,
            recorder,
            passthrough ? new PassThroughSettings(true, false) : null));
        var options = new SafeguardsEndpointOptions
        {
            Config = config,
            ClassifierFactory = (_, _, _) => timedReviewer,
        };

        var body = stream
            ? RequestBody.Replace("\"stream\":false", "\"stream\":true", StringComparison.Ordinal)
            : RequestBody;
        var context = NewContext(body);

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator, safeguards: options);

        var response = ResponseBody(context);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(fallback ? 2 : 1, calls[0]);
        Assert.Contains("\"safeguard_results\"", response, StringComparison.Ordinal);
        if (timeout && !fallback)
        {
            Assert.Contains("\"reason\":\"timeout\"", response, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("\"outcome\":\"not_flagged\"", response, StringComparison.Ordinal);
        }

        if (stream)
        {
            var reviewed = response.IndexOf("\"safeguard_results\"", StringComparison.Ordinal);
            var stopped = response.IndexOf("event:message_stop", StringComparison.Ordinal);
            Assert.True(stopped >= 0, "missing message_stop: " + response);
            Assert.True(reviewed < stopped, "review results were delivered after message_stop");
        }

        // The scripted reviewer stands in for the classifier pipeline, so only
        // the proxied call is persisted here (see StreamDeliveryE2E for both).
        Assert.Single(recorder.Requests);
        Assert.Equal("completed", Assert.Single(recorder.RequestResults).Status);
        Assert.Single(recorder.UsageLogs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TerminalTailRejectsIncompleteStream(bool noTail)
    {
        var calls = 0;
        var evaluator = SafeguardsEvaluator.Create(
            Encoding.UTF8.GetBytes("""{"safeguards":[{"type":"dangerous_tool_use"}]}"""),
            new SafeguardsConfig { ClassifierModel = "reviewer" },
            (_, _) =>
            {
                calls++;
                return Task.FromResult("""{"verdicts":{"tool1":{"flagged":false}}}""");
            });
        Assert.NotNull(evaluator);

        List<StreamEvent> events =
        [
            TestChannels.Data(
                """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"tool1","name":"Bash","input":{}}}"""),
            TestChannels.Data("""{"type":"content_block_stop","index":0}"""),
            TestChannels.Data("""{"type":"message_delta","delta":{"stop_reason":"tool_use"}}"""),
        ];
        if (!noTail)
        {
            events.Add(TestChannels.Data(
                """{"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"late","name":"Bash","input":{}}}"""));
        }

        var source = new ListStream<StreamEvent>(events);
        var reviewed = SafeguardsStream.Wrap(source, evaluator, CancellationToken.None, abortUpstream: null);
        while (reviewed.Next())
        {
            Assert.DoesNotContain(
                "not_flagged",
                Encoding.UTF8.GetString(reviewed.Current!.Data),
                StringComparison.Ordinal);
        }

        Assert.NotNull(reviewed.Err);
        Assert.Equal(0, calls);
    }

    /// <summary>
    /// The same handler must resolve settings anew for each request without
    /// sharing mutable configuration with in-flight reviews.
    /// </summary>
    [Fact]
    public async Task ClaudeCodeSafeguardsRuntimeSettings()
    {
        fixture.Channels.Set(AnthropicChannel(4970, "gpt-test"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => JsonResponse(AnthropicNormal),
        };

        var config = new SafeguardsConfig
        {
            ClassifierModel = "first",
            Policy = "First policy",
            TimeoutNanoseconds = 1_000_000_000,
        };
        Exception? loadError = null;
        var lookups = 0;
        var reviews = 0;
        var orchestrator = WithAnthropicInbound(NewOrchestrator(executor));
        var options = new SafeguardsEndpointOptions
        {
            Config = new SafeguardsConfig(),
            ConfigProvider = _ =>
            {
                lookups++;
                return loadError is null
                    ? Task.FromResult(config)
                    : Task.FromException<SafeguardsConfig>(loadError);
            },
            ClassifierFactory = (_, _, _) => (_, request) =>
            {
                reviews++;
                Assert.Equal(config.ClassifierModel, request.Model);
                Assert.Contains(config.Policy, request.System, StringComparison.Ordinal);
                return Task.FromResult("""{"verdicts":{"tool1":{"flagged":false}}}""");
            },
        };

        async Task<DefaultHttpContext> Send(bool withSafeguard)
        {
            var body = """{"model":"gpt-test","max_tokens":128,"messages":[{"role":"user","content":"show cwd"}]}""";
            if (withSafeguard)
            {
                body = body[..^1] + ""","safeguards":[{"type":"dangerous_tool_use"}]}""";
            }

            var context = NewContext(body);
            await ChatCompletionEndpoint.HandleAsync(context, orchestrator, safeguards: options);
            return context;
        }

        Assert.Equal(200, (await Send(true)).Response.StatusCode);

        config = new SafeguardsConfig
        {
            ClassifierModel = "second",
            Policy = "Second policy",
            TimeoutNanoseconds = 1_000_000_000,
        };
        Assert.Equal(200, (await Send(true)).Response.StatusCode);
        Assert.Equal(2, reviews);

        config = new SafeguardsConfig
        {
            ClassifierModel = string.Empty,
            Policy = "Second policy",
            TimeoutNanoseconds = 1_000_000_000,
        };
        Assert.Equal(200, (await Send(true)).Response.StatusCode);
        Assert.Equal(2, reviews);

        loadError = new InvalidOperationException("database unavailable");
        Assert.Equal(500, (await Send(true)).Response.StatusCode);
        Assert.Equal(2, reviews);
        Assert.Equal(4, lookups);

        Assert.Equal(200, (await Send(false)).Response.StatusCode);
        Assert.Equal(4, lookups);
    }

    /// <summary>
    /// Semantic completion does not cancel downstream delivery when an Anthropic
    /// provider delays transport EOF; the one-second abort bounds the tail drain.
    /// </summary>
    [Theory]
    [InlineData(false, 4971)]
    [InlineData(true, 4972)]
    public async Task CompletedUpstreamStaysOpen(bool passthrough, long channelId)
    {
        const string rateLimit = """{"rateLimit":{"maxConcurrent":1,"queueSize":1,"queueTimeoutMs":5000}}""";
        fixture.Channels.Set(AnthropicChannel(channelId, "gpt-test", rateLimit));

        var reviewCalls = new int[1];
        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = AnthropicStream();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) =>
            {
                Interlocked.Increment(ref reviewCalls[0]);
                return JsonResponse(AnthropicReview);
            },
            OnStream = (_, _, token) => new CompletedUpstreamStream(events, token, closed),
        };
        var recorder = new RecordingRequestChainRecorder();
        var orchestrator = WithAnthropicInbound(NewOrchestrator(
            executor,
            recorder,
            passthrough ? new PassThroughSettings(true, false) : null));
        var options = new SafeguardsEndpointOptions
        {
            Config = new SafeguardsConfig
            {
                ClassifierModel = "gpt-test",
                TimeoutNanoseconds = 500_000_000,
            },
            ClassifierFactory = static (parent, identity, request) =>
                SafeguardsClassifier.Create(parent, identity, request.SessionId, request.SessionScope),
        };

        var context = NewContext(RequestBody.Replace(
            "\"stream\":false",
            "\"stream\":true",
            StringComparison.Ordinal));

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator, safeguards: options);

        var response = ResponseBody(context);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref reviewCalls[0]));
        Assert.Contains("\"outcome\":\"not_flagged\"", response, StringComparison.Ordinal);
        Assert.Contains("event:message_stop", response, StringComparison.Ordinal);
        Assert.DoesNotContain("event:error", response, StringComparison.Ordinal);

        await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, recorder.Requests.Count);
        Assert.Equal(2, recorder.RequestResults.Count);
        Assert.All(recorder.RequestResults, static row => Assert.Equal("completed", row.Status));
        Assert.Equal(2, recorder.UsageLogs.Count);
    }

    /// <summary>
    /// A reviewer sharing a channel with <c>maxConcurrent=1</c> must not wait on
    /// its own main request's slot: the terminal-tail drain releases admission
    /// before the review starts.
    /// </summary>
    [Fact]
    public async Task SharedChannelSingleConcurrency()
    {
        const string rateLimit = """{"rateLimit":{"maxConcurrent":1,"queueSize":1,"queueTimeoutMs":5000}}""";
        fixture.Channels.Set(TestChannels.OpenAi(4973, "gpt-test", settings: rateLimit));

        var reviewCalls = new int[1];
        var streamEvents = ChatStream();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) =>
            {
                Interlocked.Increment(ref reviewCalls[0]);
                return JsonResponse(ReviewCompletion);
            },
            OnStream = (_, _, _) => new ListStream<StreamEvent>(streamEvents),
        };
        var recorder = new RecordingRequestChainRecorder();
        var orchestrator = WithAnthropicInbound(NewOrchestrator(executor, recorder));
        var options = new SafeguardsEndpointOptions
        {
            Config = new SafeguardsConfig
            {
                ClassifierModel = "gpt-test",
                TimeoutNanoseconds = 500_000_000,
            },
            ClassifierFactory = static (parent, identity, request) =>
                SafeguardsClassifier.Create(parent, identity, request.SessionId, request.SessionScope),
        };

        var context = NewContext(RequestBody.Replace(
            "\"stream\":false",
            "\"stream\":true",
            StringComparison.Ordinal));

        await ChatCompletionEndpoint.HandleAsync(context, orchestrator, safeguards: options);

        var response = ResponseBody(context);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(1, Volatile.Read(ref reviewCalls[0]));
        Assert.Contains("\"outcome\":\"not_flagged\"", response, StringComparison.Ordinal);

        Assert.Equal(2, recorder.Requests.Count);
        Assert.Equal(2, recorder.RequestResults.Count);
        Assert.All(recorder.RequestResults, static row => Assert.Equal("completed", row.Status));
        Assert.Equal(2, recorder.UsageLogs.Count);
    }

    /// <summary>
    /// Emits the completed Anthropic events, then parks the read like a provider
    /// that never sends transport EOF. The abort callback cancels the token and
    /// ends the drain; <paramref name="closed"/> reports the upstream teardown.
    /// </summary>
    private sealed class CompletedUpstreamStream(
        IReadOnlyList<StreamEvent> events,
        CancellationToken cancellationToken,
        TaskCompletionSource closed) : IStream<StreamEvent>
    {
        private static readonly TimeSpan SafetyTimeout = TimeSpan.FromSeconds(5);

        private int _index;
        private bool _blocked;

        public StreamEvent? Current { get; private set; }

        public Exception? Err => null;

        public bool Next()
        {
            if (_index < events.Count)
            {
                Current = events[_index];
                _index++;
                return true;
            }

            if (!_blocked)
            {
                _blocked = true;
                cancellationToken.WaitHandle.WaitOne(SafetyTimeout);
                closed.TrySetResult();
            }

            return false;
        }

        public void Dispose() => closed.TrySetResult();
    }
}
