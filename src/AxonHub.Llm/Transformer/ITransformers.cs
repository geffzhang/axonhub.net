using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Transformer;

/// <summary>
/// Port of transformer.Inbound (llm/transformer/interfaces.go:13-31): accepts
/// the client protocol, speaks the unified model inside.
/// </summary>
public interface IInboundTransformer
{
    /// <summary>Turns a client HTTP request into the unified request.</summary>
    LlmRequest TransformRequest(HttpRequestData? request);

    /// <summary>Turns a unified response into the client's HTTP response.</summary>
    HttpResponseData TransformResponse(LlmResponse? response);

    /// <summary>Turns a unified stream into the client's stream.</summary>
    IStream<StreamEvent> TransformStream(IStream<LlmResponse> stream);

    /// <summary>
    /// Aggregates stream chunks into a complete client-shaped body
    /// (used when a non-streaming client request had to be streamed upstream).
    /// </summary>
    byte[] AggregateStreamChunks(IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta);

    /// <summary>
    /// Turns an orchestrator failure into the client's error response
    /// (llm/transformer/interfaces.go: Inbound.TransformError).
    /// </summary>
    HttpResponseData TransformError(Exception? error);
}

/// <summary>
/// Port of transformer.Outbound (llm/transformer/interfaces.go:35-62): speaks
/// one provider protocol, consumes the unified model.
/// </summary>
public interface IOutboundTransformer
{
    /// <summary>The API format this transformer emits, e.g. <c>openai/chat_completions</c>.</summary>
    string ApiFormat { get; }

    /// <summary>Turns the unified request into a provider HTTP request.</summary>
    HttpRequestData TransformRequest(LlmRequest? request);

    /// <summary>Turns a provider HTTP response into the unified response.</summary>
    LlmResponse TransformResponse(HttpResponseData? response);

    /// <summary>
    /// Turns a provider stream into the unified stream. The request is carried
    /// because audio endpoints use dedicated SSE schemas (llm/transformer/openai/outbound.go:297).
    /// </summary>
    IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream);

    /// <summary>Turns a provider error into the unified error.</summary>
    ResponseError TransformError(HttpClientError? error);

    /// <summary>Aggregates provider stream chunks into a provider-shaped body.</summary>
    byte[] AggregateStreamChunks(HttpRequestData? request, IReadOnlyList<StreamEvent> chunks, out ResponseMeta meta);
}
