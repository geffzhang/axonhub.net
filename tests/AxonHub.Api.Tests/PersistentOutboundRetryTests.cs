using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the multi-model retry slice of PersistentOutboundTransformer
/// (internal/server/orchestrator/outbound.go:468-856): CanRetry consults the
/// candidate's remaining models before the channel rules, 429 still exits
/// immediately, PrepareForRetry advances the model and rebuilds the transformer
/// with the refreshed per-model API format, NextChannel resets the model index,
/// refreshCandidateAPIFormat recomputes from the channel endpoints, and
/// applyModelDefaultMaxTokens only touches Anthropic rounds without a client cap.
/// </summary>
public sealed class PersistentOutboundRetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CanRetryWhileTheCandidateHasAnotherModelLeft()
    {
        var harness = new Harness(CandidateWithModels(901, "m1", "m2"));
        harness.Transformer.TransformRequest(Request("m1"));

        // The generic error matches no channel rule, but a model remains, and
        // outbound.go:807-812 checks that before the channel predicate.
        Assert.True(harness.Transformer.CanRetry(new InvalidOperationException("upstream hiccup")));

        Assert.Null(harness.Transformer.TryPrepareForRetry(CancellationToken.None));
        Assert.False(harness.Transformer.CanRetry(new InvalidOperationException("upstream hiccup")));
    }

    [Fact]
    public void RateLimitedAttemptsNeverRetryTheSameChannel()
    {
        var harness = new Harness(CandidateWithModels(902, "m1", "m2"));
        harness.Transformer.TransformRequest(Request("m1"));

        // 429 exits before the remaining-model branch even though a model is left.
        Assert.False(harness.Transformer.CanRetry(
            new HttpClientError("POST", "https://upstream.test/chat", 429, "429 Too Many Requests")));
    }

    [Fact]
    public void PrepareForRetryAdvancesToTheNextModel()
    {
        var candidate = CandidateWithModels(903, "m1", "m2");
        candidate.ModelApiFormats = ["openai/chat_completions", "anthropic/messages"];
        var harness = new Harness(candidate);

        var request = Request("m1");
        harness.Transformer.TransformRequest(request);
        Assert.Equal("openai/chat_completions", candidate.ApiFormat);

        Assert.Null(harness.Transformer.TryPrepareForRetry(CancellationToken.None));

        // outbound.go:838-856: the candidate protocol follows the new model.
        Assert.Equal("anthropic/messages", candidate.ApiFormat);
        Assert.Equal([903L], harness.Metrics.Selections);

        harness.Transformer.TransformRequest(request);

        Assert.Equal("m2", harness.Transformer.CurrentModel);
        Assert.Equal("m2", request.Model);
        Assert.Equal("m2", harness.Last.Requests[^1].Model);
    }

    [Fact]
    public void NextChannelResetsTheModelIndex()
    {
        var first = CandidateWithModels(911, "a1", "a2");
        var second = CandidateWithModels(912, "b1", "b2");
        var harness = new Harness(first, second);

        var request = Request("a1");
        harness.Transformer.TransformRequest(request);
        Assert.Null(harness.Transformer.TryPrepareForRetry(CancellationToken.None));

        Assert.Null(harness.Transformer.TryNextChannel(CancellationToken.None));
        harness.Transformer.TransformRequest(request);

        // NextChannel resets the model index, so the second candidate starts at
        // its first model instead of inheriting a2's position.
        Assert.Equal(912L, harness.Transformer.CurrentChannelId);
        Assert.Equal("b1", harness.Transformer.CurrentModel);
        Assert.Equal("b1", request.Model);
        Assert.Equal([911L, 912L], harness.Metrics.Selections);
    }

    [Fact]
    public void RefreshCandidateApiFormatRecomputesFromChannelEndpoints()
    {
        var candidate = CandidateWithModels(921, "alpha");
        candidate.ApiFormat = null;
        candidate.Type = "custom-endpoints";
        candidate.Settings =
            """{"modelProtocols":[{"model":"alpha","apiFormats":["anthropic/messages"]}]}""";
        candidate.Endpoints =
            """[{"api_format":"openai/chat_completions"},{"api_format":"anthropic/messages"}]""";

        var harness = new Harness(candidate);
        harness.Transformer.TransformRequest(Request("requested"));

        // No per-model table and no explicit format: refreshCandidateAPIFormat
        // resolves the entry's protocol against the channel endpoints using the
        // original client model (outbound.go:468-498).
        Assert.Equal("anthropic/messages", candidate.ApiFormat);
    }

    [Fact]
    public void AnthropicCandidatesGetTheModelCardOutputLimit()
    {
        var candidate = CandidateWithModels(931, "m1");
        candidate.ApiFormat = "anthropic/messages";
        candidate.DefaultMaxTokens = 4096;

        var harness = new Harness(candidate);
        var request = Request("m1");
        harness.Transformer.TransformRequest(request);

        var outbound = harness.Last.Requests[^1];
        Assert.Equal(4096L, outbound.TransformOptions?.DefaultMaxTokens);

        // ApplyModelDefaultMaxTokens works on a shallow clone: the caller's
        // request keeps its own TransformOptions.
        Assert.Null(request.TransformOptions);
        Assert.NotSame(request, outbound);
    }

    [Fact]
    public void ClientOutputCapsSuppressTheModelCardDefault()
    {
        var candidate = CandidateWithModels(932, "m1");
        candidate.ApiFormat = "anthropic/messages";
        candidate.DefaultMaxTokens = 4096;

        var harness = new Harness(candidate);
        var request = Request("m1");
        request.MaxTokens = 64;
        harness.Transformer.TransformRequest(request);

        Assert.Null(harness.Last.Requests[^1].TransformOptions);
    }

    [Fact]
    public void NonAnthropicFormatsSuppressTheModelCardDefault()
    {
        var candidate = CandidateWithModels(933, "m1");
        candidate.ApiFormat = "openai/chat_completions";
        candidate.DefaultMaxTokens = 4096;

        var harness = new Harness(candidate);
        harness.Transformer.TransformRequest(Request("m1"));

        Assert.Null(harness.Last.Requests[^1].TransformOptions);
    }

    [Fact]
    public void AnExplicitDefaultMaxTokensOptionWinsOverTheModelCard()
    {
        var candidate = CandidateWithModels(934, "m1");
        candidate.ApiFormat = "anthropic/messages";
        candidate.DefaultMaxTokens = 4096;

        var harness = new Harness(candidate);
        var request = Request("m1");
        var options = new TransformOptions { DefaultMaxTokens = 1 };
        request.TransformOptions = options;
        harness.Transformer.TransformRequest(request);

        Assert.Equal(1L, harness.Last.Requests[^1].TransformOptions?.DefaultMaxTokens);
        Assert.Same(options, request.TransformOptions);
        Assert.Equal(1L, options.DefaultMaxTokens);
    }

    // -- Helpers -------------------------------------------------------------

    private static LlmRequest Request(string model) => new()
    {
        Model = model,
        RequestType = "chat",
    };

    private static ChannelCandidate CandidateWithModels(long channelId, params string[] models)
    {
        var candidate = new ChannelCandidate
        {
            ChannelId = channelId,
            ChannelName = "channel-" + channelId,
            OrderingWeight = 0,
            ActualModel = models[0],
            Type = "openai",
            ApiFormat = "openai/chat_completions",
        };

        foreach (var model in models)
        {
            candidate.Models.Add(new ChannelCandidateModel
            {
                RequestModel = model,
                ActualModel = model,
            });
        }

        return candidate;
    }

    private sealed class Harness
    {
        public Harness(params ChannelCandidate[] candidates)
        {
            Transformer = new PersistentOutboundTransformer(
                candidates,
                new TestTimeProvider(Now),
                Metrics,
                (candidate, _, _) =>
                {
                    var outbound = new RecordingOutbound(
                        candidate.ApiFormat ?? "openai/chat_completions");
                    Created.Add(outbound);
                    return outbound;
                });
        }

        public RecordingMetricsRecorder Metrics { get; } = new();

        public List<RecordingOutbound> Created { get; } = [];

        public PersistentOutboundTransformer Transformer { get; }

        public RecordingOutbound Last => Created[^1];
    }

    private sealed class RecordingOutbound(string apiFormat) : IOutboundTransformer
    {
        public string ApiFormat { get; } = apiFormat;

        public List<LlmRequest> Requests { get; } = [];

        public HttpRequestData TransformRequest(LlmRequest? request)
        {
            Requests.Add(request!);
            return new HttpRequestData();
        }

        public LlmResponse TransformResponse(HttpResponseData? response)
            => throw new NotSupportedException("response path not used by these tests");

        public IStream<LlmResponse> TransformStream(
            HttpRequestData? request,
            IStream<StreamEvent> stream)
            => throw new NotSupportedException("stream path not used by these tests");

        public ResponseError TransformError(HttpClientError? error)
            => throw new NotSupportedException("error path not used by these tests");

        public byte[] AggregateStreamChunks(
            HttpRequestData? request,
            IReadOnlyList<StreamEvent> chunks,
            out ResponseMeta meta)
        {
            meta = new ResponseMeta();
            throw new NotSupportedException("aggregate path not used by these tests");
        }
    }
}
