using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Persistence;
using RoutingPolicy = AxonHub.Api.Models.RoutingPolicy;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the trace/thread sticky half of orchestrator.LoadBalancedSelector
/// (candidates.go:738-905) and RequestService's previous-channel cache
/// (request.go:1733-1801) against candidates_sticky_test.go and
/// request_sticky_test.go.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class StickyChannelSelectionTests(GrainClusterFixture fixture)
{
    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        IModelCandidateSelector selector,
        RetryPolicy policy,
        IPreviousChannelProvider? previousChannels = null,
        IRequestChainRecorder? recorder = null)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(policy),
            selector,
            TimeProvider.System,
            recorder,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager,
            previousChannels: previousChannels);

    private static RetryPolicy Policy(
        int maxChannelRetries = 2,
        string traceStickyMode = RetryPolicy.TraceStickyPreferPreviousChannel)
        => new()
        {
            Enabled = maxChannelRetries > 0,
            MaxChannelRetries = maxChannelRetries,
            MaxSingleChannelRetries = 0,
            RetryDelayMs = 0,
            TraceStickyMode = traceStickyMode,
        };

    /// <summary>
    /// Builds the real DefaultSelector over an in-memory channel store, with one
    /// channel-model association per requested (channel, priority) pair.
    /// </summary>
    private ModelCandidateSelector Selector(
        string model,
        params (long ChannelId, int Priority)[] associations)
    {
        var models = new FakeModelStore();
        models.Seed(row =>
        {
            row.ModelId = model;
            row.Status = "enabled";
            row.Settings = ModelSettingsCodec.SerializeEntitySettings(new ModelEntitySettings
            {
                Associations = [.. associations.Select(association => new ModelAssociation
                {
                    Type = ModelAssociation.TypeChannelModel,
                    Priority = association.Priority,
                    ChannelModel = new ChannelModelAssociation(
                        (int)association.ChannelId,
                        model),
                })],
            });
        });

        var channels = new FakeChannelStore();
        foreach (var channelId in associations.Select(static a => a.ChannelId).Distinct())
        {
            channels.Add(new ChannelRecord
            {
                Id = channelId,
                Name = "channel-" + channelId,
                Type = "openai",
                Status = "enabled",
                BaseUrl = "http://channel-" + channelId + ".test",
                Credentials = "{\"apiKey\":\"sk-" + channelId + "\"}",
                SupportedModels = "[\"" + model + "\"]",
            });
        }

        return new ModelCandidateSelector(
            models,
            new StaticModelSettingsProvider(ModelSettings.Default),
            channels,
            fixture.Channels,
            TimeProvider.System,
            bypassModelPrivacy: true);
    }

    private static ScriptedExecutor FailingExecutor()
        => new() { OnDo = (_, _, _) => throw TestChannels.HttpClient(500) };

    [Fact]
    public async Task TraceStickyOverridesPriorityAndRemovesDuplicateFallback()
    {
        // candidates_sticky_test.go "trace overrides association priority and
        // removes duplicate fallback": channel 2 is pinned first, then the
        // remaining channels keep priority order (1 before 3), and the second
        // channel-2 association never reappears as a fallback.
        const string model = "sticky-priority-model";
        fixture.Channels.Set();
        var selector = Selector(
            model,
            (5401, 0),
            (5402, 2),
            (5403, 1),
            (5402, 4));
        var previous = new FakePreviousChannelProvider();
        previous.TraceChannels[7] = 5402;
        previous.ThreadChannels[9] = 5403;
        var executor = FailingExecutor();
        var orchestrator = NewOrchestrator(
            executor,
            selector,
            Policy(),
            previous);

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            new RequestIdentity(1, null) { TraceId = 7, ThreadId = 9 },
            onRequestCreated: null));

        Assert.Equal(
            [
                "http://channel-5402.test/v1/chat/completions",
                "http://channel-5401.test/v1/chat/completions",
                "http://channel-5403.test/v1/chat/completions",
            ],
            executor.Requests.Select(static request => request.Url));
    }

    [Fact]
    public async Task ThreadScopeIsUsedWhenTheTraceHasNoCachedChannel()
    {
        const string model = "sticky-thread-model";
        fixture.Channels.Set();
        var selector = Selector(model, (5404, 0), (5405, 1), (5406, 2));
        var previous = new FakePreviousChannelProvider();
        previous.ThreadChannels[9] = 5406;
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion(model),
        };
        var orchestrator = NewOrchestrator(executor, selector, Policy(), previous);

        var result = await orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            new RequestIdentity(1, null) { TraceId = 7, ThreadId = 9 },
            onRequestCreated: null);

        Assert.Equal(200, result.Response!.StatusCode);
        var request = Assert.Single(executor.Requests);
        Assert.Equal("http://channel-5406.test/v1/chat/completions", request.Url);
    }

    [Fact]
    public async Task DisabledModeKeepsTheNormalPriorityOrder()
    {
        const string model = "sticky-disabled-model";
        fixture.Channels.Set();
        var selector = Selector(model, (5407, 0), (5408, 2), (5409, 1));
        var previous = new FakePreviousChannelProvider();
        previous.TraceChannels[7] = 5408;
        var executor = FailingExecutor();
        var orchestrator = NewOrchestrator(
            executor,
            selector,
            Policy(traceStickyMode: RetryPolicy.TraceStickyDisabled),
            previous);

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            new RequestIdentity(1, null) { TraceId = 7, ThreadId = 9 },
            onRequestCreated: null));

        Assert.Equal(
            [
                "http://channel-5407.test/v1/chat/completions",
                "http://channel-5409.test/v1/chat/completions",
                "http://channel-5408.test/v1/chat/completions",
            ],
            executor.Requests.Select(static request => request.Url));
    }

    [Fact]
    public async Task StickyCandidateHasNoFallbackWhenCrossChannelRetriesAreDisabled()
    {
        const string model = "sticky-noretry-model";
        fixture.Channels.Set();
        var selector = Selector(model, (5410, 0), (5411, 1));
        var previous = new FakePreviousChannelProvider();
        previous.TraceChannels[7] = 5411;
        var executor = FailingExecutor();
        var orchestrator = NewOrchestrator(
            executor,
            selector,
            Policy(maxChannelRetries: 0),
            previous);

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            new RequestIdentity(1, null) { TraceId = 7 },
            onRequestCreated: null));

        var request = Assert.Single(executor.Requests);
        Assert.Equal("http://channel-5411.test/v1/chat/completions", request.Url);
    }

    [Fact]
    public async Task PersistenceCachesTheSelectedChannelForTraceAndThread()
    {
        const string model = "sticky-cache-model";
        fixture.Channels.Set();
        var selector = Selector(model, (5412, 0));
        var previous = new PreviousChannelCache(TimeProvider.System);
        var recorder = new RecordingRequestChainRecorder();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion(model),
        };
        var orchestrator = NewOrchestrator(
            executor,
            selector,
            Policy(),
            previous,
            recorder);

        _ = await orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            new RequestIdentity(1, null)
            {
                TraceId = 42,
                ThreadId = 77,
                TraceThreadId = 77,
            },
            onRequestCreated: null);

        Assert.Equal(5412, previous.GetPreviousChannelId(42));
        Assert.Equal(5412, previous.GetPreviousChannelIdByThread(77));
    }

    [Fact]
    public async Task PersistenceUsesTheTraceRowThreadForTheThreadScope()
    {
        // cachePreviousChannelForRequest writes the thread scope from the trace
        // row's thread id (request.go:1768-1774), not from the current request's
        // thread header.
        const string model = "sticky-trace-thread-model";
        fixture.Channels.Set();
        var selector = Selector(model, (5414, 0));
        var previous = new PreviousChannelCache(TimeProvider.System);
        var recorder = new RecordingRequestChainRecorder();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion(model),
        };
        var orchestrator = NewOrchestrator(
            executor,
            selector,
            Policy(),
            previous,
            recorder);

        _ = await orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            new RequestIdentity(1, null)
            {
                TraceId = 42,
                ThreadId = 77,
                TraceThreadId = 78,
            },
            onRequestCreated: null);

        Assert.Equal(5414, previous.GetPreviousChannelId(42));
        Assert.Equal(5414, previous.GetPreviousChannelIdByThread(78));
        Assert.Equal(0, previous.GetPreviousChannelIdByThread(77));
    }

    [Fact]
    public async Task PersistenceDoesNotCacheWithoutATrace()
    {
        const string model = "sticky-no-trace-model";
        fixture.Channels.Set();
        var selector = Selector(model, (5413, 0));
        var previous = new PreviousChannelCache(TimeProvider.System);
        var recorder = new RecordingRequestChainRecorder();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => TestChannels.Completion(model),
        };
        var orchestrator = NewOrchestrator(
            executor,
            selector,
            Policy(),
            previous,
            recorder);

        _ = await orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model),
            new RequestIdentity(1, null),
            onRequestCreated: null);

        Assert.Equal(0, previous.GetPreviousChannelId(42));
        Assert.Equal(0, previous.GetPreviousChannelIdByThread(77));
    }

    [Fact]
    public void ModelPolicyDisablesStickyWhenTheProfileDefaults()
    {
        // candidates_sticky_test.go "model disables sticky when profile
        // defaults": a non-default model policy survives a profile whose field
        // is the default marker.
        var systemPolicy = Policy(
            traceStickyMode: RetryPolicy.TraceStickyPreferPreviousChannel) with
        {
            LoadBalancerStrategy = RetryPolicy.AdaptiveStrategy,
        };
        var modelPolicy = new ModelRoutingPolicy(
            RoutingPolicy.Default,
            RetryPolicy.TraceStickyDisabled);
        var apiKey = ApiKey(loadBalanceStrategy: "default", traceStickyMode: "default");

        var effective = ChatCompletionOrchestrator.DeriveRoutingPolicy(
            systemPolicy,
            apiKey,
            modelPolicy);

        Assert.Equal(RetryPolicy.TraceStickyDisabled, effective.TraceStickyMode);
        Assert.Equal(RetryPolicy.AdaptiveStrategy, effective.LoadBalancerStrategy);
    }

    [Fact]
    public void ProfileOverridesTheModelRoutingPolicy()
    {
        // candidates_sticky_test.go "effective routing policy priority": the
        // active profile's values win over the model policy, which wins over
        // the retry policy.
        var systemPolicy = Policy(
            traceStickyMode: RetryPolicy.TraceStickyPreferPreviousChannel) with
        {
            LoadBalancerStrategy = RetryPolicy.AdaptiveStrategy,
        };
        var modelPolicy = new ModelRoutingPolicy(
            RetryPolicy.FailoverStrategy,
            RetryPolicy.TraceStickyDisabled);
        var apiKey = ApiKey(
            loadBalanceStrategy: RetryPolicy.RoundRobinStrategy,
            traceStickyMode: RetryPolicy.TraceStickyPreferPreviousChannel);

        var effective = ChatCompletionOrchestrator.DeriveRoutingPolicy(
            systemPolicy,
            apiKey,
            modelPolicy);

        Assert.Equal(RetryPolicy.RoundRobinStrategy, effective.LoadBalancerStrategy);
        Assert.Equal(RetryPolicy.TraceStickyPreferPreviousChannel, effective.TraceStickyMode);
    }

    private static ApiKeyRecord ApiKey(string loadBalanceStrategy, string traceStickyMode)
        => new()
        {
            Profiles = ApiKeyProfilesCodec.Serialize(new ApiKeyProfilesModel
            {
                ActiveProfile = "default",
                Profiles =
                [
                    new ApiKeyProfileModel
                    {
                        Name = "default",
                        LoadBalanceStrategy = loadBalanceStrategy,
                        TraceStickyMode = traceStickyMode,
                    },
                ],
            }),
        };
}

internal sealed class FakePreviousChannelProvider : IPreviousChannelProvider
{
    public Dictionary<long, long> TraceChannels { get; } = [];

    public Dictionary<long, long> ThreadChannels { get; } = [];

    public long GetPreviousChannelId(long traceId) => TraceChannels.GetValueOrDefault(traceId);

    public long GetPreviousChannelIdByThread(long threadId) => ThreadChannels.GetValueOrDefault(threadId);

    public void Remember(long traceId, long? threadId, long channelId)
    {
        TraceChannels[traceId] = channelId;
        if (threadId is > 0)
        {
            ThreadChannels[threadId.Value] = channelId;
        }
    }
}
