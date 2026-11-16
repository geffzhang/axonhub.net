using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Api.Safeguards;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/api/safeguards_classifier.go and its Go tests: the
/// reviewer is an ordinary non-streaming chat completion with isolated request
/// bookkeeping, model access, quota and persistence, and it never re-enters the
/// Anthropic handler.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class SafeguardsClassifierTests(GrainClusterFixture fixture)
{
    private const string ReviewCompletion =
        """{"id":"review","object":"chat.completion","model":"gpt-test","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"verdict"}}],"usage":{"prompt_tokens":12,"completion_tokens":34,"total_tokens":46}}""";

    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        RecordingMetricsRecorder metrics,
        IRequestChainRecorder? recorder = null)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            metrics,
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
            channelLimiterManager: fixture.ChannelLimiterManager);

    private static ChatCompletionOrchestrator WithAnthropicInbound(ChatCompletionOrchestrator orchestrator)
        => orchestrator.WithInbound(new AnthropicInboundTransformer());

    private static HttpResponseData JsonResponse(string body) => new()
    {
        StatusCode = 200,
        Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["Content-Type"] = ["application/json"],
        },
        Body = Encoding.UTF8.GetBytes(body),
    };

    private static ChatCompletionResult Result(string body)
        => new() { Response = JsonResponse(body) };

    private static SafeguardsClassifierRequest ReviewRequest() => new()
    {
        Model = "gpt-test",
        System = "policy",
        User = "evidence",
        MaxTokens = 4096,
    };

    [Fact]
    public async Task ClassifierPipelineRecordsUsageAndEnforcesAccess()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4950, "gpt-test"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => JsonResponse(ReviewCompletion),
        };
        var recorder = new RecordingRequestChainRecorder();
        var parent = WithAnthropicInbound(NewOrchestrator(
            executor,
            new RecordingMetricsRecorder(),
            recorder));

        var identity = new RequestIdentity(1, 41)
        {
            ApiKey = new ApiKeyRecord { Id = 41 },
            RequestId = "outer-request",
        };
        var classify = SafeguardsClassifier.Create(parent, identity, sessionId: null, sessionScope: null);

        var text = await classify(CancellationToken.None, ReviewRequest());

        Assert.Equal("verdict", text);
        var requestRow = Assert.Single(recorder.Requests);
        Assert.Equal(41L, requestRow.ApiKeyId);
        Assert.Equal("gpt-test", requestRow.ModelId);
        Assert.NotEqual("outer-request", requestRow.ExternalId);
        Assert.Equal("completed", Assert.Single(recorder.RequestResults).Status);
        var usage = Assert.Single(recorder.UsageLogs);
        Assert.Equal(41L, usage.ApiKeyId);
        Assert.IsType<AnthropicInboundTransformer>(parent.Inbound);

        // checkApiKeyModelAccess runs before routing: a restricted profile can
        // never consume upstream tokens for another model.
        var restricted = ApiKeyProfilesCodec.Serialize(new ApiKeyProfilesModel
        {
            ActiveProfile = "restricted",
            Profiles = [new ApiKeyProfileModel { Name = "restricted", ModelIDs = ["other-model"] }],
        });
        var denied = identity with { ApiKey = new ApiKeyRecord { Id = 42, Profiles = restricted } };
        var deniedClassify = SafeguardsClassifier.Create(parent, denied, sessionId: null, sessionScope: null);

        await Assert.ThrowsAsync<SafeguardsClassifierException>(
            () => deniedClassify(CancellationToken.None, ReviewRequest()));
        Assert.Single(recorder.UsageLogs);
    }

    [Fact]
    public async Task ClassifierRequestIdentity()
    {
        var identity = new RequestIdentity(7, 42)
        {
            ApiKey = new ApiKeyRecord { Id = 42 },
            RequestId = "parent-request",
            TraceId = 5,
        };
        HttpRequestData? wireRequest = null;
        RequestIdentity? childIdentity = null;
        var classify = SafeguardsClassifier.Create(
            identity,
            sessionId: null,
            sessionScope: null,
            (request, classifierIdentity, _) =>
            {
                wireRequest = request;
                childIdentity = classifierIdentity;
                return Task.FromResult(Result(
                    """{"choices":[{"finish_reason":"stop","message":{"content":"verdict"}}]}"""));
            });

        var text = await classify(CancellationToken.None, new SafeguardsClassifierRequest
        {
            Model = "reviewer",
            System = "policy",
            User = "evidence",
            MaxTokens = 4096,
        });

        Assert.Equal("verdict", text);
        Assert.NotNull(wireRequest);
        Assert.NotNull(childIdentity);
        Assert.Equal(42L, childIdentity!.ApiKeyId);
        Assert.Equal(7L, childIdentity.ProjectId);
        Assert.Equal(5L, childIdentity.TraceId);
        Assert.NotEqual("parent-request", childIdentity.RequestId);
        Assert.StartsWith("ar-", childIdentity.RequestId, StringComparison.Ordinal);
        Assert.Equal(childIdentity.RequestId, wireRequest!.RequestId);
        Assert.Equal("POST", wireRequest.Method);
        Assert.Equal("/v1/chat/completions", wireRequest.Path);
        Assert.False(wireRequest.Headers!.ContainsKey("Authorization"));

        using var body = JsonDocument.Parse(wireRequest.Body!);
        var root = body.RootElement;
        Assert.Equal("reviewer", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(4096, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.TryGetProperty("safeguards", out _));

        // Contexts.ForkRequest isolates request bookkeeping only: the caller's
        // identity is a record copy and keeps its request id.
        Assert.Equal("parent-request", identity.RequestId);
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("""{"choices":[{"finish_reason":"length","message":{"content":"partial"}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":"verdict","refusal":"refused"}}]}""")]
    [InlineData("""{"choices":[{"finish_reason":"stop","message":{"content":" "}}]}""")]
    [InlineData("not JSON")]
    public async Task RejectsIncompleteOutput(string body)
    {
        var classify = SafeguardsClassifier.Create(
            RequestIdentity.Default,
            sessionId: null,
            sessionScope: null,
            (_, _, _) => Task.FromResult(Result(body)));

        await Assert.ThrowsAsync<SafeguardsClassifierException>(
            () => classify(CancellationToken.None, new SafeguardsClassifierRequest()));
    }

    [Fact]
    public async Task PropagatesDeadline()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var classify = SafeguardsClassifier.Create(
            RequestIdentity.Default,
            sessionId: null,
            sessionScope: null,
            (_, _, token) => Task.FromException<ChatCompletionResult>(
                new OperationCanceledException(token)));

        var error = await Assert.ThrowsAsync<SafeguardsClassifierException>(
            () => classify(cancellation.Token, new SafeguardsClassifierRequest()));
        Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
    }

    [Fact]
    public async Task FallbackUsesClassifierPipeline()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4951, "gpt-test"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => JsonResponse(
                """{"id":"review","object":"chat.completion","model":"gpt-test","choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"{\"verdicts\":{\"tool1\":{\"flagged\":true,\"explanation\":\"Unauthorized deletion\"}}}"}}],"usage":{"prompt_tokens":12,"completion_tokens":34,"total_tokens":46}}"""),
        };
        var recorder = new RecordingRequestChainRecorder();
        var classify = SafeguardsClassifier.Create(
            WithAnthropicInbound(NewOrchestrator(executor, new RecordingMetricsRecorder(), recorder)),
            new RequestIdentity(1, null),
            sessionId: null,
            sessionScope: null);

        var evaluator = SafeguardsEvaluator.Create(
            Encoding.UTF8.GetBytes("""{"safeguards":[{"type":"dangerous_tool_use"}]}"""),
            new SafeguardsConfig
            {
                ClassifierModel = "unavailable-reviewer",
                FallbackModels = ["gpt-test"],
            },
            classify);

        Assert.NotNull(evaluator);
        var results = evaluator!.Evaluate(
            CancellationToken.None,
            [
                new SafeguardsToolUse
                {
                    Id = "tool1",
                    Name = "Bash",
                    Input = Encoding.UTF8.GetBytes("""{"command":"rm -rf /tmp/data"}"""),
                },
            ]);

        Assert.Equal(
            "flagged",
            Assert.Single(results).Status.ToolUses["tool1"].Outcome);

        var usage = Assert.Single(recorder.UsageLogs);
        Assert.Equal("gpt-test", usage.ModelId);
        Assert.Equal(46L, usage.TotalTokens);
    }
}
