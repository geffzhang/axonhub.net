using System.Text.Json;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;

namespace AxonHub.Api.Tests;

/// <summary>
/// Difference C094 (6), Api side: the pipeline carries the caller's token on the
/// unified request; the persistent outbound transformer must hand it to the
/// per-attempt request even when the channel transform options rebuild it.
/// </summary>
public sealed class PersistentOutboundCancellationTests
{
    private static ChannelCandidate Candidate(string? settings = null) => new()
    {
        ChannelId = 9101,
        ChannelName = "persistent-cancel",
        ActualModel = "upstream-model",
        Type = "openai",
        ApiFormat = ApiFormats.OpenAIChatCompletion,
        Settings = settings,
        Credentials = """{"api_key":"test-key"}""",
    };

    private static PersistentOutboundTransformer Transformer(
        ChannelCandidate candidate,
        RecordingOutbound recorder)
        => new(
            [candidate],
            TimeProvider.System,
            new RecordingMetricsRecorder(),
            (_, _, _) => recorder);

    [Fact]
    public void TheTransformStageTokenReachesTheProviderTransformer()
    {
        using var cts = new CancellationTokenSource();
        var recorder = new RecordingOutbound(ApiFormats.OpenAIChatCompletion);
        var transformer = Transformer(Candidate(), recorder);

        var request = new LlmRequest
        {
            Model = "client-model",
            RequestCancellationToken = cts.Token,
        };
        transformer.TransformRequest(request);

        var forwarded = Assert.IsType<LlmRequest>(recorder.LastRequest);
        Assert.Same(request, forwarded);
        Assert.Equal(cts.Token, forwarded.RequestCancellationToken);
    }

    [Fact]
    public void ARequestCloneFromChannelTransformOptionsKeepsTheToken()
    {
        using var cts = new CancellationTokenSource();
        var settings = JsonSerializer.Serialize(new ChannelSettingsDraft
        {
            TransformOptions = new TransformOptionsDraft { ForceArrayInputs = true },
        });
        var recorder = new RecordingOutbound(ApiFormats.OpenAIChatCompletion);
        var transformer = Transformer(Candidate(settings), recorder);

        var request = new LlmRequest
        {
            Model = "client-model",
            Messages = [new Message { Role = "user", Content = MessageContent.FromText("Hello") }],
            RequestCancellationToken = cts.Token,
        };
        transformer.TransformRequest(request);

        var forwarded = Assert.IsType<LlmRequest>(recorder.LastRequest);
        Assert.NotSame(request, forwarded); // applyTransformOptions cloned it
        Assert.Equal(cts.Token, forwarded.RequestCancellationToken);
    }

    private sealed class RecordingOutbound(string apiFormat) : IOutboundTransformer
    {
        public string ApiFormat { get; } = apiFormat;

        public LlmRequest? LastRequest { get; private set; }

        public HttpRequestData TransformRequest(LlmRequest? request)
        {
            LastRequest = request;
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
