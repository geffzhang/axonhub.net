using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer;
using PipelineType = AxonHub.Llm.Pipeline.Pipeline;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Port of llm/pipeline/response_timeout_test.go: an outbound transformer that
/// implements ResponseTimeoutProvider can override either pipeline timeout for
/// the current attempt, and a zero override disables it.
/// </summary>
public sealed class PipelineResponseTimeoutTests
{
    [Fact]
    public void NoProviderKeepsThePipelineDefaults()
    {
        var pipeline = new PipelineType(
            new MockExecutor(),
            new MockInbound(),
            new PlainOutbound(),
            new PipelineOptions
            {
                StreamFirstEventTimeout = TimeSpan.FromSeconds(30),
                NonStreamTimeout = TimeSpan.FromSeconds(20),
            });

        var (stream, nonStream) = pipeline.EffectiveResponseTimeouts();

        Assert.Equal(TimeSpan.FromSeconds(30), stream);
        Assert.Equal(TimeSpan.FromSeconds(20), nonStream);
    }

    [Fact]
    public void ProviderWithoutOverridesKeepsThePipelineDefaults()
    {
        var pipeline = Pipeline(
            new MockOutbound(),
            streamTimeout: TimeSpan.FromSeconds(30),
            nonStreamTimeout: TimeSpan.FromSeconds(20));

        var (stream, nonStream) = pipeline.EffectiveResponseTimeouts();

        Assert.Equal(TimeSpan.FromSeconds(30), stream);
        Assert.Equal(TimeSpan.FromSeconds(20), nonStream);
    }

    [Theory]
    [InlineData(5000, -1, 5000, 20000)]
    [InlineData(-1, 7000, 30000, 7000)]
    [InlineData(5000, 7000, 5000, 7000)]
    [InlineData(0, -1, 0, 20000)]
    public void ProviderOverridesEitherOrBothTimeouts(
        int streamOverrideMilliseconds,
        int nonStreamOverrideMilliseconds,
        int expectedStreamMilliseconds,
        int expectedNonStreamMilliseconds)
    {
        var outbound = new MockOutbound
        {
            StreamFirstEventTimeoutOverride = ToDuration(streamOverrideMilliseconds),
            NonStreamTimeoutOverride = ToDuration(nonStreamOverrideMilliseconds),
        };
        var pipeline = Pipeline(
            outbound,
            streamTimeout: TimeSpan.FromSeconds(30),
            nonStreamTimeout: TimeSpan.FromSeconds(20));

        var (stream, nonStream) = pipeline.EffectiveResponseTimeouts();

        Assert.Equal(TimeSpan.FromMilliseconds(expectedStreamMilliseconds), stream);
        Assert.Equal(TimeSpan.FromMilliseconds(expectedNonStreamMilliseconds), nonStream);
    }

    [Fact]
    public void NonStreamTimeoutUsesTheChannelOverride()
    {
        var executor = new MockExecutor
        {
            DoFn = (cancellationToken, _) =>
            {
                Task.Delay(150, cancellationToken).GetAwaiter().GetResult();
                return new HttpResponseData();
            },
        };
        var outbound = new MockOutbound
        {
            NonStreamTimeoutOverride = TimeSpan.FromMilliseconds(30),
        };
        var pipeline = Pipeline(
            executor,
            outbound,
            streamTimeout: TimeSpan.Zero,
            nonStreamTimeout: TimeSpan.Zero);

        var error = Assert.Throws<PipelineSentinelException>(
            () => pipeline.Process(new HttpRequestData()));

        Assert.Same(PipelineErrors.NonStreamResponseTimeout, error);
    }

    [Fact]
    public void AZeroOverrideDisablesTheGlobalTimeout()
    {
        var executor = new MockExecutor
        {
            DoFn = (cancellationToken, _) =>
            {
                Task.Delay(150, cancellationToken).GetAwaiter().GetResult();
                return new HttpResponseData();
            },
        };
        var outbound = new MockOutbound
        {
            NonStreamTimeoutOverride = TimeSpan.Zero,
        };
        var pipeline = Pipeline(
            executor,
            outbound,
            streamTimeout: TimeSpan.Zero,
            nonStreamTimeout: TimeSpan.FromMilliseconds(10));

        var result = pipeline.Process(new HttpRequestData());

        Assert.False(result.Stream);
        Assert.NotNull(result.Response);
    }

    private static PipelineType Pipeline(
        MockOutbound outbound,
        TimeSpan streamTimeout,
        TimeSpan nonStreamTimeout)
        => Pipeline(new MockExecutor(), outbound, streamTimeout, nonStreamTimeout);

    private static PipelineType Pipeline(
        MockExecutor executor,
        MockOutbound outbound,
        TimeSpan streamTimeout,
        TimeSpan nonStreamTimeout)
        => new(
            executor,
            new MockInbound(),
            outbound,
            new PipelineOptions
            {
                StreamFirstEventTimeout = streamTimeout,
                NonStreamTimeout = nonStreamTimeout,
            });

    private static TimeSpan? ToDuration(int milliseconds)
        => milliseconds < 0 ? null : TimeSpan.FromMilliseconds(milliseconds);

    /// <summary>An outbound transformer without the timeout-provider seam.</summary>
    private sealed class PlainOutbound : IOutboundTransformer
    {
        private readonly MockOutbound _inner = new();

        public string ApiFormat => _inner.ApiFormat;

        public HttpRequestData TransformRequest(LlmRequest? request)
            => _inner.TransformRequest(request);

        public LlmResponse TransformResponse(HttpResponseData? response)
            => _inner.TransformResponse(response);

        public IStream<LlmResponse> TransformStream(
            HttpRequestData? request,
            IStream<StreamEvent> stream)
            => _inner.TransformStream(request, stream);

        public ResponseError TransformError(HttpClientError? error)
            => _inner.TransformError(error);

        public byte[] AggregateStreamChunks(
            HttpRequestData? request,
            IReadOnlyList<StreamEvent> chunks,
            out ResponseMeta meta)
            => _inner.AggregateStreamChunks(request, chunks, out meta);
    }
}
