using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Vertex;

namespace AxonHub.Llm.Transformer.Anthropic;

/// <summary>
/// Port of anthropic.VertexTransformer (vertex.go): embeds the Anthropic
/// outbound and replaces the default transport with a Vertex AI executor that
/// signs requests with Google Cloud credentials.
/// </summary>
public sealed class VertexTransformer : IOutboundTransformer, IChannelCustomizedExecutor, IApiKeyProvenance
{
    private readonly IOutboundTransformer _outbound;
    private readonly VertexExecutor _executor;

    public VertexTransformer(IOutboundTransformer outbound, VertexExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(outbound);
        ArgumentNullException.ThrowIfNull(executor);

        _outbound = outbound;
        _executor = executor;
    }

    /// <summary>Port of CustomizeExecutor.</summary>
    public IExecutor CustomizeExecutor(IExecutor executor) => _executor;

    public string ApiFormat => _outbound.ApiFormat;

    // The embedded Outbound keeps its key audit state; forward it so the
    // execution row still records what the direct transformer selected.
    public string? SelectedApiKey => (_outbound as IApiKeyProvenance)?.SelectedApiKey;

    public int? SelectedApiKeyIndex => (_outbound as IApiKeyProvenance)?.SelectedApiKeyIndex;

    public HttpRequestData TransformRequest(LlmRequest? request)
        => _outbound.TransformRequest(request);

    public LlmResponse TransformResponse(HttpResponseData? response)
        => _outbound.TransformResponse(response);

    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
        => _outbound.TransformStream(request, stream);

    public ResponseError TransformError(HttpClientError? error)
        => _outbound.TransformError(error);

    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => _outbound.AggregateStreamChunks(request, chunks, out meta);
}
