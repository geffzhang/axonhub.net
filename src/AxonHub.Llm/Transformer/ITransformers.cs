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

/// <summary>
/// Port of transformer.TransportRequestFinalizer
/// (llm/transformer/interfaces.go:69-73): transport-specific cleanup applied
/// after raw-request middlewares, so persistence and execution observe the same
/// provider request.
/// </summary>
public interface ITransportRequestFinalizer
{
    /// <summary>Applies the final transport-specific request cleanup.</summary>
    HttpRequestData? FinalizeTransportRequest(HttpRequestData? request);
}

/// <summary>
/// Port of the structural <c>interface{ Stop() }</c> the reference asserts on
/// outbound transformers when a channel is torn down
/// (internal/server/biz/channel.go:326-349): stopping a transformer releases
/// the WebSocket executors and connection pools it cached.
/// </summary>
public interface IStoppableOutbound
{
    /// <summary>Releases the connections the transformer owns.</summary>
    void Stop();
}

/// <summary>
/// Port of transformer.PassThroughBodyPolicy (llm/transformer/interfaces.go:65-68):
/// lets outbound transformers veto raw body pass-through.
/// </summary>
public interface IPassThroughBodyPolicy
{
    /// <summary>Whether the raw inbound body may replace the transformed body.</summary>
    bool AllowPassThroughBody(LlmRequest? llmRequest, HttpRequestData? providerRequest);
}

/// <summary>
/// Port of transformer.PassThroughResponsePolicy (llm/transformer/interfaces.go:69-75):
/// lets outbound transformers veto raw response pass-through. Transformers whose
/// raw provider payload is not the client-facing payload implement this to keep
/// pass-through from restoring the wrapped body.
/// </summary>
public interface IPassThroughResponsePolicy
{
    /// <summary>Whether the raw provider response may replace the transformed response.</summary>
    bool AllowPassThroughResponse(LlmRequest? llmRequest, HttpRequestData? providerRequest);
}

/// <summary>
/// Optional marker recognising the official Codex response-header contract.
/// The reference asserts it structurally inside
/// <c>PersistentOutboundTransformer.SupportsCodexResponseHeaders</c>
/// (internal/server/orchestrator/outbound.go:505-510); the port keeps the same
/// structural assertion through this interface.
/// </summary>
public interface ISupportsCodexResponseHeaders
{
    /// <summary>Whether the transformer forwards Codex session/turn response headers.</summary>
    bool SupportsCodexResponseHeaders();
}

/// <summary>
/// Port of transformer.VideoTaskOutbound (llm/transformer/interfaces.go:75-82):
/// the optional extension for outbound transformers that support video task
/// query/delete operations (async task model).
/// </summary>
public interface IVideoTaskOutbound
{
    /// <summary>Builds the provider GET request for one task.</summary>
    HttpRequestData BuildGetVideoTaskRequest(string providerTaskId);

    /// <summary>Parses the provider GET response into the unified response.</summary>
    LlmResponse ParseGetVideoTaskResponse(HttpResponseData? httpResp);

    /// <summary>Builds the provider DELETE request for one task.</summary>
    HttpRequestData BuildDeleteVideoTaskRequest(string providerTaskId);
}
