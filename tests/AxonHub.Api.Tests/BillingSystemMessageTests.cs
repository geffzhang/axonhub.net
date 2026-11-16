using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/orchestrator/claude_code.go:30-43 and
/// llm/transformer/anthropic/claudecode/utils_test.go: the billing system
/// messages the Claude CLI injects survive only on the official OAuth
/// <c>claudecode</c> channel; every other candidate strips them before the
/// provider transformer renders the body.
/// </summary>
public sealed class BillingSystemMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NonClaudeCodeCandidatesStripTheBillingMessage()
    {
        var harness = new Harness(Candidate("openai", "{\"apiKey\":\"sk-test\"}"));
        var request = RequestWithBilling();

        harness.Transformer.TransformRequest(request);

        var outbound = Assert.Single(harness.Created);
        var passed = Assert.Single(outbound.Requests);
        Assert.Equal(2, passed.Messages.Count);
        Assert.Equal("user", passed.Messages[0].Role);
        Assert.Equal("hello", passed.Messages[0].Content?.Content);
        Assert.Equal("system", passed.Messages[1].Role);
        Assert.Equal("be nice", passed.Messages[1].Content?.Content);

        // The shared pipeline request keeps its messages: retries and the next
        // candidate see the untouched request.
        Assert.Equal(3, request.Messages.Count);
        Assert.NotSame(request, passed);
    }

    [Fact]
    public void ClaudeCodeOAuthCandidatesKeepTheBillingMessage()
    {
        var harness = new Harness(Candidate(
            ChannelOutboundFactory.ClaudeCodeChannelType,
            "{\"oauth\":{\"access_token\":\"tok\"}}"));
        var request = RequestWithBilling();

        harness.Transformer.TransformRequest(request);

        var outbound = Assert.Single(harness.Created);
        var passed = Assert.Single(outbound.Requests);
        Assert.Same(request, passed);
        Assert.Equal(3, passed.Messages.Count);
        Assert.Equal("system", passed.Messages[0].Role);
        Assert.Equal(
            "x-anthropic-billing-header: cc_version=1.0.0",
            passed.Messages[0].Content?.Content?.Trim());
    }

    [Fact]
    public void ClaudeCodeApiKeyCandidatesStripTheBillingMessage()
    {
        var harness = new Harness(Candidate(
            ChannelOutboundFactory.ClaudeCodeChannelType,
            "{\"apiKey\":\"sk-test\"}"));
        var request = RequestWithBilling();

        harness.Transformer.TransformRequest(request);

        var outbound = Assert.Single(harness.Created);
        var passed = Assert.Single(outbound.Requests);
        Assert.Equal(2, passed.Messages.Count);
        Assert.Equal(3, request.Messages.Count);
    }

    [Fact]
    public void RequestsWithoutBillingMessagesPassThroughUnchanged()
    {
        var harness = new Harness(Candidate("openai", "{\"apiKey\":\"sk-test\"}"));
        var request = new LlmRequest
        {
            Model = "m1",
            RequestType = "chat",
            Messages =
            [
                new Message { Role = "system", Content = MessageContent.FromText("be nice") },
                new Message { Role = "user", Content = MessageContent.FromText("hello") },
            ],
        };

        harness.Transformer.TransformRequest(request);

        var outbound = Assert.Single(harness.Created);
        var passed = Assert.Single(outbound.Requests);
        Assert.Same(request, passed);
        Assert.Equal(2, passed.Messages.Count);
    }

    [Fact]
    public void PartBillingMessagesAreRemovedFromThePartList()
    {
        var harness = new Harness(Candidate("openai", "{\"apiKey\":\"sk-test\"}"));
        var request = new LlmRequest
        {
            Model = "m1",
            RequestType = "chat",
            Messages =
            [
                new Message
                {
                    Role = "system",
                    Content = MessageContent.FromParts(
                    [
                        new MessageContentPart
                        {
                            Type = "text",
                            Text = "x-anthropic-billing-header: cc_version=1.0.0",
                        },
                        new MessageContentPart { Type = "text", Text = "real instruction" },
                    ]),
                },
                new Message { Role = "user", Content = MessageContent.FromText("hello") },
            ],
        };

        harness.Transformer.TransformRequest(request);

        var outbound = Assert.Single(harness.Created);
        var passed = Assert.Single(outbound.Requests);
        Assert.Equal(2, passed.Messages.Count);
        Assert.Equal("system", passed.Messages[0].Role);
        var part = Assert.Single(passed.Messages[0].Content!.MultipleContent!);
        Assert.Equal("real instruction", part.Text);

        // The shared request keeps both parts.
        Assert.Equal(2, request.Messages[0].Content!.MultipleContent!.Count);
    }

    // -- Helpers -------------------------------------------------------------

    private static LlmRequest RequestWithBilling() => new()
    {
        Model = "m1",
        RequestType = "chat",
        Messages =
        [
            new Message
            {
                Role = "system",
                Content = MessageContent.FromText("x-anthropic-billing-header: cc_version=1.0.0"),
            },
            new Message { Role = "user", Content = MessageContent.FromText("hello") },
            new Message { Role = "system", Content = MessageContent.FromText("be nice") },
        ],
    };

    private static ChannelCandidate Candidate(string type, string credentials)
    {
        var candidate = new ChannelCandidate
        {
            ChannelId = 5301,
            ChannelName = "billing-channel",
            OrderingWeight = 0,
            ActualModel = "m1",
            Type = type,
            Credentials = credentials,
            ApiFormat = type == ChannelOutboundFactory.ClaudeCodeChannelType
                ? "anthropic/messages"
                : "openai/chat_completions",
        };

        candidate.Models.Add(new ChannelCandidateModel
        {
            RequestModel = "m1",
            ActualModel = "m1",
        });

        return candidate;
    }

    private sealed class Harness
    {
        public Harness(ChannelCandidate candidate)
        {
            Transformer = new PersistentOutboundTransformer(
                [candidate],
                new TestTimeProvider(Now),
                new RecordingMetricsRecorder(),
                (selected, _, _) =>
                {
                    var outbound = new RecordingOutbound(
                        selected.ApiFormat ?? "openai/chat_completions");
                    Created.Add(outbound);
                    return outbound;
                });
        }

        public List<RecordingOutbound> Created { get; } = [];

        public PersistentOutboundTransformer Transformer { get; }
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
