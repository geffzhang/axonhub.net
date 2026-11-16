using System.Text;
using AxonHub.Api.Auth;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the native-tool half of the selectCandidates middleware
/// (select_candidates.go:53-61): the Google filter for the Gemini contents
/// format and the Anthropic filter for the Anthropic Messages format, each only
/// when the request actually carries a native tool, with fallback to the full
/// candidate list when no channel type is compatible. Mirrors
/// candidates_google_test.go and candidates_anthropic_test.go.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class NativeToolSelectionTests(GrainClusterFixture fixture)
{
    private static ChannelCandidate Candidate(long channelId, string type, string model = "native-model")
        => new()
        {
            ChannelId = channelId,
            ChannelName = "channel-" + channelId,
            ActualModel = model,
            Type = type,
            BaseUrl = "http://channel-" + channelId + ".test",
            Credentials = "{\"apiKey\":\"sk-" + channelId + "\"}",
            ApiFormat = type is "gemini" or "gemini_vertex"
                ? ApiFormats.GeminiContents
                : ApiFormats.AnthropicMessage,
        };

    private static LlmRequest Request(
        string apiFormat,
        params string[] toolTypes)
        => new()
        {
            Model = "native-model",
            ApiFormat = apiFormat,
            Tools = [.. toolTypes.Select(type => new Tool { Type = type })],
        };

    private static long[] Ids(IEnumerable<ChannelCandidate> candidates)
        => [.. candidates.Select(candidate => candidate.ChannelId)];

    [Fact]
    public void GoogleNativeToolsKeepOnlyGeminiNativeChannels()
    {
        var candidates = new[]
        {
            Candidate(5701, "gemini"),
            Candidate(5702, "gemini_openai"),
            Candidate(5703, "gemini_vertex"),
        };

        var scoped = NativeToolFilters.ApplyToCandidates(
            candidates,
            Request(ApiFormats.GeminiContents, ToolTypes.GoogleSearch, ToolTypes.Function));

        Assert.Equal([5701L, 5703L], Ids(scoped));
    }

    [Fact]
    public void GoogleNativeToolsWithMultipleTypesKeepTheSameCompatibleSet()
    {
        var candidates = new[]
        {
            Candidate(5701, "gemini"),
            Candidate(5702, "gemini_openai"),
            Candidate(5703, "gemini_vertex"),
        };

        var scoped = NativeToolFilters.ApplyToCandidates(
            candidates,
            Request(
                ApiFormats.GeminiContents,
                ToolTypes.GoogleSearch,
                ToolTypes.GoogleUrlContext,
                ToolTypes.Function));

        Assert.Equal([5701L, 5703L], Ids(scoped));
    }

    [Fact]
    public void GoogleFilterIsInactiveWithoutGoogleNativeTools()
    {
        var candidates = new[]
        {
            Candidate(5701, "gemini"),
            Candidate(5702, "gemini_openai"),
        };

        var scoped = NativeToolFilters.ApplyToCandidates(
            candidates,
            Request(ApiFormats.GeminiContents, ToolTypes.Function));

        Assert.Equal([5701L, 5702L], Ids(scoped));
    }

    [Fact]
    public void GoogleFilterFallsBackToAllWhenNoChannelSupportsTheTools()
    {
        var candidates = new[] { Candidate(5702, "gemini_openai") };

        var scoped = NativeToolFilters.ApplyToCandidates(
            candidates,
            Request(ApiFormats.GeminiContents, ToolTypes.GoogleSearch));

        Assert.Equal([5702L], Ids(scoped));
    }

    [Fact]
    public void GoogleFilterIsNotInstalledForOtherApiFormats()
    {
        var candidates = new[]
        {
            Candidate(5701, "gemini"),
            Candidate(5702, "gemini_openai"),
        };

        var scoped = NativeToolFilters.ApplyToCandidates(
            candidates,
            Request(ApiFormats.OpenAIChatCompletion, ToolTypes.GoogleSearch));

        Assert.Equal([5701L, 5702L], Ids(scoped));
    }

    [Fact]
    public void AnthropicNativeWebSearchKeepsOnlyVerifiedNativeChannels()
    {
        var candidates = new[]
        {
            Candidate(5711, "anthropic"),
            Candidate(5712, "moonshot_anthropic"),
            Candidate(5713, "deepseek_anthropic"),
            Candidate(5714, "anthropic_aws"),
            Candidate(5715, "anthropic_gcp"),
            Candidate(5716, "claudecode"),
        };

        var scoped = NativeToolFilters.ApplyToCandidates(
            candidates,
            Request(ApiFormats.AnthropicMessage, ToolTypes.WebSearch));

        Assert.Equal([5711L, 5713L, 5714L, 5715L, 5716L], Ids(scoped));
    }

    [Fact]
    public void AnthropicFilterIsInactiveWithoutNativeTools()
    {
        var candidates = new[]
        {
            Candidate(5711, "anthropic"),
            Candidate(5712, "moonshot_anthropic"),
        };

        // A function named web_search is not native; the reference only looks
        // at the tool type.
        var request = new LlmRequest
        {
            Model = "native-model",
            ApiFormat = ApiFormats.AnthropicMessage,
            Tools = [new Tool { Type = ToolTypes.Function, Function = new Function { Name = "web_search" } }],
        };

        var scoped = NativeToolFilters.ApplyToCandidates(candidates, request);

        Assert.Equal([5711L, 5712L], Ids(scoped));
    }

    [Fact]
    public void AnthropicFilterFallsBackToAllWhenNoChannelSupportsTheTools()
    {
        var candidates = new[]
        {
            Candidate(5712, "moonshot_anthropic"),
            Candidate(5717, "zai_anthropic"),
        };

        var scoped = NativeToolFilters.ApplyToCandidates(
            candidates,
            Request(ApiFormats.AnthropicMessage, AnthropicToolFilter.ToolTypeWebSearch20250305));

        Assert.Equal([5712L, 5717L], Ids(scoped));
    }

    [Fact]
    public void AnthropicFilterIsNotInstalledForOtherApiFormats()
    {
        var candidates = new[]
        {
            Candidate(5711, "anthropic"),
            Candidate(5712, "moonshot_anthropic"),
        };

        var scoped = NativeToolFilters.ApplyToCandidates(
            candidates,
            Request(ApiFormats.OpenAIChatCompletion, ToolTypes.WebSearch));

        Assert.Equal([5711L, 5712L], Ids(scoped));
    }

    [Fact]
    public async Task OrchestratorRoutesAnthropicNativeToolsToCompatibleChannels()
    {
        const string model = "native-anthropic-model";
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
                MaxChannelRetries = 2,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
            }),
            new FixedSelector(
                Candidate(5721, "anthropic", model),
                Candidate(5722, "moonshot_anthropic", model)),
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager)
            .WithInbound(new AnthropicInboundTransformer());

        _ = await Record.ExceptionAsync(() => orchestrator.ProcessAsync(
            AnthropicRequest(model),
            RequestIdentity.Default,
            onRequestCreated: null));

        var request = Assert.Single(executor.Requests);
        Assert.StartsWith("http://channel-5721.test", request.Url);
    }

    private static HttpRequestData AnthropicRequest(string model)
    {
        var body = "{\"model\":\"" + model + "\",\"max_tokens\":16,"
            + "\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],"
            + "\"tools\":[{\"type\":\""
            + AnthropicToolFilter.ToolTypeWebSearch20250305
            + "\",\"name\":\"web_search\"}]}";

        return new HttpRequestData
        {
            Method = "POST",
            Url = "http://gateway.test/v1/messages",
            Path = "/v1/messages",
            Query = new Dictionary<string, List<string>>(StringComparer.Ordinal),
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(body),
            Auth = new AuthConfig(),
        };
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
