using System.Text;
using AxonHub.Abstractions;
using AxonHub.Api.Http;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Persistence;
using AxonHub.Api.Settings;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Api.Tests;

/// <summary>
/// End-to-end wiring of the live preview middleware in the chat pipeline
/// (orchestrator.go:294-295): a streaming request under a live_preview storage
/// policy registers both buffers before the transport runs, mirrors the
/// provider events, and releases them when the client stream closes.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class LivePreviewOrchestratorTests(GrainClusterFixture fixture)
{
    private const long ChannelId = 6801;
    private const string Model = "live-preview";

    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        IRequestChainRecorder recorder,
        LiveStreamRegistry registry,
        ISystemSettingsAdminService settings)
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
            channelLimiterManager: fixture.ChannelLimiterManager,
            liveStreamRegistry: registry,
            systemSettings: settings);

    private static HttpRequestData ClientRequest(bool stream)
        => TestChannels.ChatRequest(Model, stream);

    private static SystemSettingsAdminService SettingsWithLivePreview(bool enabled)
    {
        var systems = new FakeSystemStore();
        systems.Add(
            StoragePolicyCodec.Key,
            StoragePolicyCodec.Serialize(StoragePolicy.Default with { LivePreview = enabled }));
        return new SystemSettingsAdminService(systems);
    }

    [Fact]
    public async Task StreamingRequestsRegisterAndReleasePreviewBuffers()
    {
        fixture.Channels.Set(TestChannels.OpenAi(ChannelId, Model));
        var registry = new LiveStreamRegistry();
        var recorder = new RecordingRequestChainRecorder();
        ChunkBuffer? requestBuffer = null;
        ChunkBuffer? executionBuffer = null;
        long requestId = 0;
        long executionId = 0;

        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) =>
            {
                // persistRequestExecution ran before the live preview middleware,
                // so both rows exist by the time the transport is called.
                var request = Assert.Single(recorder.Requests);
                var execution = Assert.Single(recorder.Executions);
                requestId = request.Id;
                executionId = execution.Id;
                requestBuffer = registry.GetRequestBuffer(request.Id);
                executionBuffer = registry.GetExecutionBuffer(execution.Id);
                Assert.NotNull(requestBuffer);
                Assert.NotNull(executionBuffer);
                Assert.False(requestBuffer.IsClosed);

                return new ListStream<StreamEvent>(ProviderEvents());
            },
        };
        var orchestrator = NewOrchestrator(
            executor,
            recorder,
            registry,
            SettingsWithLivePreview(enabled: true));

        var result = await orchestrator.ProcessAsync(ClientRequest(stream: true));
        Assert.NotNull(result.Stream);
        Drain(result.Stream!);

        // The buffers stay readable while the stream is open.
        Assert.NotNull(registry.GetRequestBuffer(requestId));
        Assert.NotNull(registry.GetExecutionBuffer(executionId));

        result.Stream.Dispose();

        Assert.True(requestBuffer!.IsClosed);
        Assert.True(executionBuffer!.IsClosed);
        Assert.Null(registry.GetRequestBuffer(requestId));
        Assert.Null(registry.GetExecutionBuffer(executionId));

        // Every provider event reached the execution buffer, terminal marker
        // included; the preview marshal filters it later.
        var executionChunks = executionBuffer.Slice();
        Assert.Equal(3, executionChunks.Count);
        Assert.Equal("[DONE]", Encoding.UTF8.GetString(executionChunks[^1].Data));

        // The inbound buffer mirrors the client-facing events instead; the
        // OpenAI transformer ends that stream with the same terminal marker.
        var requestChunks = requestBuffer.Slice();
        Assert.NotEmpty(requestChunks);
        Assert.Equal("[DONE]", Encoding.UTF8.GetString(requestChunks[^1].Data));
    }

    [Fact]
    public async Task DisabledPolicyKeepsTheRegistryEmpty()
    {
        fixture.Channels.Set(TestChannels.OpenAi(ChannelId, Model));
        var registry = new LiveStreamRegistry();
        var recorder = new RecordingRequestChainRecorder();
        var sawBuffer = true;

        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) =>
            {
                var request = Assert.Single(recorder.Requests);
                var execution = Assert.Single(recorder.Executions);
                sawBuffer = registry.GetRequestBuffer(request.Id) is not null
                    || registry.GetExecutionBuffer(execution.Id) is not null;
                return new ListStream<StreamEvent>(ProviderEvents());
            },
        };
        var orchestrator = NewOrchestrator(
            executor,
            recorder,
            registry,
            SettingsWithLivePreview(enabled: false));

        var result = await orchestrator.ProcessAsync(ClientRequest(stream: true));
        Drain(result.Stream!);
        result.Stream!.Dispose();

        Assert.False(sawBuffer);
    }

    [Fact]
    public async Task StoragePolicyReadFailureFallsBackToTheDefaultPolicy()
    {
        fixture.Channels.Set(TestChannels.OpenAi(ChannelId, Model));
        var registry = new LiveStreamRegistry();
        var recorder = new RecordingRequestChainRecorder();
        var systems = new FakeSystemStore();
        systems.GetFailuresByKey[StoragePolicyCodec.Key] = new InvalidOperationException("boom");
        var sawBuffer = true;

        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, _) =>
            {
                var request = Assert.Single(recorder.Requests);
                sawBuffer = registry.GetRequestBuffer(request.Id) is not null;
                return new ListStream<StreamEvent>(ProviderEvents());
            },
        };
        var orchestrator = NewOrchestrator(
            executor,
            recorder,
            registry,
            new SystemSettingsAdminService(systems));

        var result = await orchestrator.ProcessAsync(ClientRequest(stream: true));
        Drain(result.Stream!);
        result.Stream!.Dispose();

        Assert.False(sawBuffer);
    }

    [Fact]
    public async Task NonStreamRequestsNeverRegisterPreviewBuffers()
    {
        fixture.Channels.Set(TestChannels.OpenAi(ChannelId, Model));
        var registry = new LiveStreamRegistry();
        var recorder = new RecordingRequestChainRecorder();
        var sawBuffer = true;

        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) =>
            {
                var request = Assert.Single(recorder.Requests);
                sawBuffer = registry.GetRequestBuffer(request.Id) is not null;
                return TestChannels.Completion(Model);
            },
        };
        var orchestrator = NewOrchestrator(
            executor,
            recorder,
            registry,
            SettingsWithLivePreview(enabled: true));

        var result = await orchestrator.ProcessAsync(ClientRequest(stream: false));

        Assert.NotNull(result.Response);
        Assert.False(sawBuffer);
    }

    private static void Drain(IStream<StreamEvent> stream)
    {
        while (stream.Next())
        {
        }
    }

    private static StreamEvent[] ProviderEvents() =>
    [
        TestChannels.Data(
            "{\"id\":\"chatcmpl-live\",\"object\":\"chat.completion.chunk\",\"created\":1,"
            + "\"model\":\"" + Model + "\",\"choices\":[{\"index\":0,"
            + "\"delta\":{\"role\":\"assistant\",\"content\":\"he\"},\"finish_reason\":null}]}"),
        TestChannels.Data(
            "{\"id\":\"chatcmpl-live\",\"object\":\"chat.completion.chunk\",\"created\":1,"
            + "\"model\":\"" + Model + "\",\"choices\":[{\"index\":0,"
            + "\"delta\":{},\"finish_reason\":\"stop\"}],"
            + "\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":3}}"),
        TestChannels.Data("[DONE]"),
    ];
}
