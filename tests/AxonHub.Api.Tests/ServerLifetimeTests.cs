using AxonHub.Api.Auth;
using AxonHub.Api.Lifecycle;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/lifecycle/lifecycle.go and of the server wiring that
/// lets a host shutdown cancel an acknowledged stream's execution
/// (server.go:36-39,101-109; stream_lifecycle.go:156-167).
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class ServerLifetimeTests(GrainClusterFixture fixture)
{
    [Fact]
    public void CauseIsNullBeforeShutdown()
    {
        using var lifetime = new ServerLifetime();

        Assert.Null(lifetime.Cause);
        Assert.False(lifetime.Token.IsCancellationRequested);
    }

    [Fact]
    public void ShutdownCancelsTokenWithCause()
    {
        using var lifetime = new ServerLifetime();
        var cause = new InvalidOperationException("test shutdown");

        lifetime.Shutdown(cause);

        Assert.True(lifetime.Token.IsCancellationRequested);
        Assert.Same(cause, lifetime.Cause);
    }

    [Fact]
    public void FirstShutdownWins()
    {
        using var lifetime = new ServerLifetime();
        var first = new InvalidOperationException("first");
        var second = new InvalidOperationException("second");

        lifetime.Shutdown(first);
        lifetime.Shutdown(second);

        Assert.Same(first, lifetime.Cause);
    }

    [Fact]
    public void DefaultShutdownCauseMatchesReference()
    {
        using var lifetime = new ServerLifetime();

        lifetime.Shutdown();

        Assert.True(lifetime.Token.IsCancellationRequested);
        Assert.Equal("server shutdown", lifetime.Cause?.Message);
    }

    [Fact]
    public async Task HostShutdownCancelsAcknowledgedStreamExecution()
    {
        const string model = "shutdown-stream";
        fixture.Channels.Set(TestChannels.OpenAi(7401, model));

        using var lifetime = new ServerLifetime();
        CancellationToken executionToken = default;
        var executor = new ScriptedExecutor
        {
            OnStream = (_, _, token) =>
            {
                executionToken = token;
                return new ListStream<StreamEvent>(
                [
                    TestChannels.Data(
                        "{\"id\":\"chat\",\"model\":\"shutdown-stream\",\"choices\":[{\"index\":0,"
                        + "\"delta\":{\"content\":\"hi\"}}]}"),
                ]);
            },
        };
        var recorder = new RecordingRequestChainRecorder();
        var orchestrator = NewOrchestrator(executor, recorder, lifetime);

        var result = await orchestrator.ProcessAsync(TestChannels.ChatRequest(model, stream: true));
        var stream = result.Stream;
        Assert.NotNull(stream);

        // A transport writer delivered a chunk and acknowledged delivery; only
        // then does the host begin shutting down.
        while (stream.Next())
        {
            _ = stream.Current;
        }

        stream.AcknowledgeDelivery();
        lifetime.Shutdown(new InvalidOperationException("test shutdown"));

        await AssertCanceledAsync(executionToken);

        await Task.Run(stream.Dispose).WaitAsync(TimeSpan.FromSeconds(5));

        // The stream had no provider terminal, so shutdown finalizes it through
        // the failure path: both rows report "failed" instead of staying in
        // processing.
        Assert.Single(recorder.Executions);
        Assert.Equal("failed", recorder.ExecutionStatuses[^1].Status);
        Assert.Equal("failed", recorder.RequestStatuses[^1].Status);
    }

    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        RecordingRequestChainRecorder recorder,
        ServerLifetime lifetime)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(new RetryPolicy { Enabled = false }),
            fixture.CandidateSelector,
            TimeProvider.System,
            recorder,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager,
            serverLifetime: lifetime);

    private static async Task AssertCanceledAsync(CancellationToken token)
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Delay(Timeout.InfiniteTimeSpan, token).WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
