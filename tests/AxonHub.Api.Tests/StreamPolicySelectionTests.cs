using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Persistence;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.OpenAI.Decisions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the stream policy selector
/// (internal/server/orchestrator/candidates_stream_policy.go, mirroring
/// candidates_stream_policy_test.go): streaming requests drop <c>forbid</c>
/// channels, non-streaming requests prefer channels that do not
/// <c>require</c> streaming, auto-aggregatable request types keep a
/// require-only fallback, and Decisions raises the stream policy conflict.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class StreamPolicySelectionTests(GrainClusterFixture fixture)
{
    private const string Model = "stream-policy-model";

    private static ChannelCandidate Candidate(long channelId, string? streamPolicy)
        => new()
        {
            ChannelId = channelId,
            ChannelName = "channel-" + channelId,
            ActualModel = Model,
            Type = "openai",
            BaseUrl = "http://channel-" + channelId + ".test",
            Credentials = "{\"apiKey\":\"sk-" + channelId + "\"}",
            Policies = streamPolicy is null
                ? null
                : "{\"stream\":\"" + streamPolicy + "\"}",
        };

    private static IReadOnlyList<ChannelCandidate> Apply(
        IReadOnlyList<ChannelCandidate> candidates,
        bool? stream,
        string? requestType = null,
        string? apiFormat = null,
        string model = Model)
        => StreamPolicySelector.ApplyToCandidates(
            candidates,
            new LlmRequest
            {
                Model = model,
                Stream = stream,
                RequestType = requestType,
                ApiFormat = apiFormat,
            });

    private static long[] Ids(IEnumerable<ChannelCandidate> candidates)
        => [.. candidates.Select(candidate => candidate.ChannelId)];

    [Fact]
    public void StreamingRequestsDropForbidChannels()
    {
        var scoped = Apply(
            [
                Candidate(5801, "require"),
                Candidate(5802, "forbid"),
                Candidate(5803, "unlimited"),
                Candidate(5804, null),
            ],
            stream: true);

        Assert.Equal([5801L, 5803L, 5804L], Ids(scoped));
    }

    [Fact]
    public void StreamingRequestsWithOnlyForbiddenChannelsYieldNothing()
    {
        var scoped = Apply([Candidate(5802, "forbid")], stream: true);

        Assert.Empty(scoped);
    }

    [Fact]
    public void NonStreamingRequestsPreferChannelsThatDoNotRequireStreaming()
    {
        var scoped = Apply(
            [
                Candidate(5801, "require"),
                Candidate(5802, "forbid"),
                Candidate(5803, "unlimited"),
            ],
            stream: false);

        Assert.Equal([5802L, 5803L], Ids(scoped));
    }

    [Fact]
    public void NonStreamingRequireOnlyFallbackSurvivesAutoAggregatableChatRequests()
    {
        var scoped = Apply([Candidate(5801, "require")], stream: false);

        Assert.Equal([5801L], Ids(scoped));

        var withFormat = Apply(
            [Candidate(5801, "require")],
            stream: false,
            requestType: RequestTypes.Chat,
            apiFormat: ApiFormats.OpenAIChatCompletion);
        Assert.Equal([5801L], Ids(withFormat));
    }

    [Fact]
    public void NonStreamingRequireOnlyFallbackSurvivesCompletionRequests()
    {
        var scoped = Apply(
            [Candidate(5801, "require")],
            stream: false,
            requestType: RequestTypes.Completion,
            apiFormat: ApiFormats.OpenAICompletion);

        Assert.Equal([5801L], Ids(scoped));

        var withoutFormat = Apply(
            [Candidate(5801, "require")],
            stream: false,
            requestType: RequestTypes.Completion);
        Assert.Equal([5801L], Ids(withoutFormat));
    }

    [Theory]
    [InlineData(ApiFormats.AiSDKText)]
    [InlineData(ApiFormats.AiSDKDataStream)]
    public void NonStreamingRequireOnlyFallbackIsDroppedForAiSdkChatRequests(string apiFormat)
    {
        var scoped = Apply(
            [Candidate(5801, "require")],
            stream: false,
            requestType: RequestTypes.Chat,
            apiFormat: apiFormat);

        Assert.Empty(scoped);
    }

    [Theory]
    [InlineData(RequestTypes.Embedding, ApiFormats.OpenAIEmbedding)]
    [InlineData(RequestTypes.Compact, ApiFormats.OpenAIResponseCompact)]
    public void NonStreamingRequireOnlyFallbackIsDroppedForNonAggregatableRequests(
        string requestType,
        string apiFormat)
    {
        var scoped = Apply(
            [Candidate(5801, "require")],
            stream: false,
            requestType: requestType,
            apiFormat: apiFormat);

        Assert.Empty(scoped);
    }

    [Fact]
    public void NonStreamingMixedAiSdkCandidatesKeepOnlyNativeChannels()
    {
        var scoped = Apply(
            [
                Candidate(5801, "require"),
                Candidate(5802, "unlimited"),
            ],
            stream: false,
            requestType: RequestTypes.Chat,
            apiFormat: ApiFormats.AiSDKText);

        Assert.Equal([5802L], Ids(scoped));
    }

    [Fact]
    public void NonStreamingMixedUnsupportedCandidatesKeepOnlyNativeChannels()
    {
        var scoped = Apply(
            [
                Candidate(5801, "require"),
                Candidate(5802, "unlimited"),
            ],
            stream: false,
            requestType: RequestTypes.Embedding,
            apiFormat: ApiFormats.OpenAIEmbedding);

        Assert.Equal([5802L], Ids(scoped));
    }

    [Fact]
    public void DecisionsRequireOnlyCandidatesRaiseAStreamPolicyConflict()
    {
        var exception = Assert.Throws<StreamPolicyConflictException>(() => Apply(
            [Candidate(5801, "require")],
            stream: null,
            requestType: RequestTypes.Decisions,
            apiFormat: ApiFormats.OpenAIDecisions,
            model: "decisions-model"));

        Assert.Equal("decisions-model", exception.ModelName);
        Assert.Equal(
            "no eligible channel for model decisions-model: matching channels require streaming, "
            + "but Decisions requests are non-streaming",
            exception.Message);
    }

    [Fact]
    public void DecisionsMixedCandidatesKeepTheNativeChannel()
    {
        var scoped = Apply(
            [
                Candidate(5801, "require"),
                Candidate(5802, "unlimited"),
            ],
            stream: null,
            requestType: RequestTypes.Decisions,
            apiFormat: ApiFormats.OpenAIDecisions);

        Assert.Equal([5802L], Ids(scoped));
    }

    [Fact]
    public void NilStreamBehavesLikeNonStreaming()
    {
        var scoped = Apply([Candidate(5801, "require")], stream: null);

        Assert.Equal([5801L], Ids(scoped));
    }

    [Fact]
    public void EmptyPolicyIsUnlimitedInBothModes()
    {
        Assert.Equal(
            [5801L],
            Ids(Apply([Candidate(5801, "")], stream: true)));
        Assert.Equal(
            [5801L],
            Ids(Apply([Candidate(5801, "")], stream: false)));
    }

    [Fact]
    public void EmptyCandidateListStaysEmpty()
    {
        Assert.Empty(Apply([], stream: true));
        Assert.Empty(Apply([], stream: false));
    }

    [Fact]
    public async Task AssociationCandidatesCarryTheChannelStreamPolicy()
    {
        const string model = "stream-policy-plumbing-model";
        var models = new FakeModelStore();
        models.Seed(row =>
        {
            row.ModelId = model;
            row.Status = "enabled";
            row.Settings = ModelSettingsCodec.SerializeEntitySettings(new ModelEntitySettings
            {
                Associations =
                [
                    new ModelAssociation
                    {
                        Type = ModelAssociation.TypeChannelModel,
                        ChannelModel = new ChannelModelAssociation(5841, model),
                    },
                ],
            });
        });

        var channels = new FakeChannelStore();
        channels.Add(new ChannelRecord
        {
            Id = 5841,
            Name = "channel-5841",
            Type = "openai",
            Status = "enabled",
            BaseUrl = "http://channel-5841.test",
            Credentials = "{\"apiKey\":\"sk-5841\"}",
            SupportedModels = "[\"" + model + "\"]",
            Policies = "{\"stream\":\"forbid\"}",
        });

        var selector = new ModelCandidateSelector(
            models,
            new StaticModelSettingsProvider(ModelSettings.Default),
            channels,
            fixture.Channels,
            TimeProvider.System,
            bypassModelPrivacy: true);

        var selection = await selector.SelectAsync(
            new LlmRequest { Model = model },
            RequestIdentity.Default);

        var candidate = Assert.Single(selection.Candidates);
        Assert.Equal("{\"stream\":\"forbid\"}", candidate.Policies);
    }

    [Fact]
    public async Task OrchestratorDropsForbiddenChannelsForStreamingRequests()
    {
        const string model = "stream-policy-routing-model";
        fixture.Channels.Set();
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => throw TestChannels.HttpClient(500),
        };
        var orchestrator = new ChatCompletionOrchestrator(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(new RetryPolicy
            {
                Enabled = true,
                MaxChannelRetries = 1,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
            }),
            new FixedSelector(
                CandidateFor(model, 5821, "forbid"),
                CandidateFor(model, 5822, "unlimited")),
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            TestChannels.ChatRequest(model, stream: true),
            RequestIdentity.Default,
            onRequestCreated: null));

        var request = Assert.Single(executor.Requests);
        Assert.Equal("http://channel-5822.test/v1/chat/completions", request.Url);
    }

    [Fact]
    public async Task OrchestratorSurfacesTheDecisionsStreamPolicyConflict()
    {
        fixture.Channels.Set();
        var executor = new ScriptedExecutor();
        var orchestrator = new ChatCompletionOrchestrator(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            new RecordingMetricsRecorder(),
            new StaticRetryPolicyProvider(new RetryPolicy
            {
                Enabled = false,
                MaxChannelRetries = 0,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
            }),
            new FixedSelector(CandidateFor("decisions-model", 5831, "require")),
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager)
            .WithInbound(new DecisionsInboundTransformer());

        var exception = await Assert.ThrowsAsync<StreamPolicyConflictException>(() =>
            orchestrator.ProcessAsync(
                DecisionsRequest("decisions-model"),
                RequestIdentity.Default,
                onRequestCreated: null));

        Assert.Equal("decisions-model", exception.ModelName);
        Assert.Empty(executor.Requests);
    }

    private static ChannelCandidate CandidateFor(string model, long channelId, string? streamPolicy)
        => new()
        {
            ChannelId = channelId,
            ChannelName = "channel-" + channelId,
            ActualModel = model,
            Type = "openai",
            BaseUrl = "http://channel-" + channelId + ".test",
            Credentials = "{\"apiKey\":\"sk-" + channelId + "\"}",
            Policies = streamPolicy is null
                ? null
                : "{\"stream\":\"" + streamPolicy + "\"}",
        };

    private static HttpRequestData DecisionsRequest(string model)
        => new()
        {
            Method = "POST",
            Url = "http://gateway.test/v1/decisions",
            Path = "/v1/decisions",
            Query = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(
                "{\"model\":\"" + model + "\",\"input\":\"text\",\"questions\":[{}]}"),
            Auth = new AuthConfig(),
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
