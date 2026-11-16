using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of the PersistentOutboundTransformer seams the Responses WebSocket
/// transport adds: <c>CustomizeExecutor</c> (outbound.go:880-909) and
/// <c>finalizeTransportRequest</c> (outbound.go:912-925). The Go test lives in
/// the responses package because the reference wires the seams there; the port
/// keeps the delegation on the orchestrator wrapper, so it is locked here.
/// </summary>
public sealed class TransportRequestFinalizerTests
{
    private static PersistentOutboundTransformer Outbound(IOutboundTransformer transformer)
    {
        var outbound = new PersistentOutboundTransformer(
            [
                new ChannelCandidate
                {
                    ChannelId = 1,
                    ChannelName = "test-channel",
                    ActualModel = "gpt-5",
                    Type = "openai",
                    BaseUrl = "https://api.example.com/v1",
                    Credentials = "{\"apiKey\":\"sk-test\"}",
                },
            ],
            TimeProvider.System,
            new RecordingMetricsRecorder(),
            (_, _, _) => transformer);
        outbound.TransformRequest(new LlmRequest { Model = "gpt-5" });
        return outbound;
    }

    [Fact]
    public void CustomizeExecutorDelegatesToCurrentTransformer()
    {
        var inner = new StubExecutor();
        var customized = new StubExecutor();
        var transformer = new SeamTransformer
        {
            CustomizedExecutor = customized,
        };

        var outbound = Outbound(transformer);
        var result = outbound.CustomizeExecutor(inner);

        Assert.Same(inner, transformer.ReceivedExecutor);
        Assert.Same(customized, result);
    }

    [Fact]
    public void CustomizeExecutorPassesThroughWithoutCustomization()
    {
        var inner = new StubExecutor();
        var outbound = Outbound(new PlainTransformer());

        Assert.Same(inner, outbound.CustomizeExecutor(inner));
    }

    [Fact]
    public void FinalizeTransportRequestDelegatesToCurrentTransformer()
    {
        var original = new HttpRequestData { Url = "https://api.example.com/v1/responses" };
        var finalized = new HttpRequestData { Url = "https://api.example.com/v1/responses?clean=1" };
        var transformer = new SeamTransformer
        {
            FinalizedRequest = finalized,
        };

        var outbound = Outbound(transformer);
        var result = outbound.FinalizeTransportRequest(original);

        Assert.Same(original, transformer.ReceivedFinalizeRequest);
        Assert.Same(finalized, result);
    }

    [Fact]
    public void FinalizeTransportRequestPassesThroughWithoutFinalizer()
    {
        var original = new HttpRequestData { Url = "https://api.example.com/v1/responses" };
        var outbound = Outbound(new PlainTransformer());

        Assert.Same(original, outbound.FinalizeTransportRequest(original));
    }

    [Fact]
    public void TransportRequestFinalizerMiddlewareDelegatesToOutbound()
    {
        var original = new HttpRequestData { Url = "https://api.example.com/v1/responses" };
        var finalized = new HttpRequestData { Url = "https://api.example.com/v1/responses?clean=1" };
        var outbound = Outbound(new SeamTransformer { FinalizedRequest = finalized });

        var result = new TransportRequestFinalizerMiddleware(outbound)
            .TransformOutboundRawRequest(original);

        Assert.Same(finalized, result);
    }

    private sealed class SeamTransformer : IOutboundTransformer, IChannelCustomizedExecutor,
        ITransportRequestFinalizer
    {
        public IExecutor? CustomizedExecutor { get; init; }

        public HttpRequestData? FinalizedRequest { get; init; }

        public IExecutor? ReceivedExecutor { get; private set; }

        public HttpRequestData? ReceivedFinalizeRequest { get; private set; }

        public string ApiFormat => ApiFormats.OpenAIResponse;

        public HttpRequestData TransformRequest(LlmRequest? request) => new();

        public LlmResponse TransformResponse(HttpResponseData? response)
            => throw new NotSupportedException();

        public IStream<LlmResponse> TransformStream(
            HttpRequestData? request,
            IStream<StreamEvent> stream)
            => throw new NotSupportedException();

        public ResponseError TransformError(HttpClientError? error)
            => throw new NotSupportedException();

        public byte[] AggregateStreamChunks(
            HttpRequestData? request,
            IReadOnlyList<StreamEvent> chunks,
            out ResponseMeta meta)
            => throw new NotSupportedException();

        public IExecutor CustomizeExecutor(IExecutor executor)
        {
            ReceivedExecutor = executor;
            return CustomizedExecutor ?? executor;
        }

        public HttpRequestData? FinalizeTransportRequest(HttpRequestData? request)
        {
            ReceivedFinalizeRequest = request;
            return FinalizedRequest;
        }
    }

    private sealed class PlainTransformer : IOutboundTransformer
    {
        public string ApiFormat => ApiFormats.OpenAIResponse;

        public HttpRequestData TransformRequest(LlmRequest? request) => new();

        public LlmResponse TransformResponse(HttpResponseData? response)
            => throw new NotSupportedException();

        public IStream<LlmResponse> TransformStream(
            HttpRequestData? request,
            IStream<StreamEvent> stream)
            => throw new NotSupportedException();

        public ResponseError TransformError(HttpClientError? error)
            => throw new NotSupportedException();

        public byte[] AggregateStreamChunks(
            HttpRequestData? request,
            IReadOnlyList<StreamEvent> chunks,
            out ResponseMeta meta)
            => throw new NotSupportedException();
    }

    private sealed class StubExecutor : IExecutor
    {
        public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
            => new() { StatusCode = 200 };

        public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
            => new SliceStream<StreamEvent>([]);
    }
}
