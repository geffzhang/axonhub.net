using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;

namespace AxonHub.Llm.Pipeline;

/// <summary>
/// Port of pipeline.Middleware (llm/pipeline/middleware.go).
/// </summary>
/// <remarks>
/// Execution concepts, as in the reference:
/// <list type="bullet">
/// <item><b>Request</b>: one <c>Process</c> call, i.e. one client request.</item>
/// <item><b>Attempt</b>: one outbound execution to a provider. A request may
/// contain several attempts because of same-channel retries and channel
/// switches.</item>
/// </list>
/// Inbound hooks run forward (first registered first); outbound, response and
/// stream hooks run in reverse (last registered first).
/// </remarks>
public interface IPipelineMiddleware
{
    /// <summary>Name of the middleware.</summary>
    string Name { get; }

    /// <summary>After inbound transformation, before any attempt. Once per request.</summary>
    LlmRequest TransformInboundLlmRequest(LlmRequest request);

    /// <summary>After the final response is transformed back to provider format. Once per successful non-stream request.</summary>
    HttpResponseData TransformInboundRawResponse(HttpResponseData response);

    /// <summary>After the final unified stream is transformed back to provider format. Once per successful stream request.</summary>
    IStream<StreamEvent> TransformInboundRawStream(IStream<StreamEvent> stream);

    /// <summary>After outbound transformation, before sending. Once per attempt.</summary>
    HttpRequestData TransformOutboundRawRequest(HttpRequestData request);

    /// <summary>Called when the provider request fails. Once per failed attempt.</summary>
    void OnOutboundRawError(Exception error);

    /// <summary>After a successful provider response is received. Once per successful non-stream attempt.</summary>
    HttpResponseData TransformOutboundRawResponse(HttpResponseData response);

    /// <summary>After the provider response is transformed to unified format. Once per successful non-stream attempt.</summary>
    LlmResponse TransformOutboundLlmResponse(LlmResponse response);

    /// <summary>After a provider stream is established. Once per successful stream attempt.</summary>
    IStream<StreamEvent> TransformOutboundRawStream(IStream<StreamEvent> stream);

    /// <summary>After the provider stream is transformed to unified format. Once per successful stream attempt.</summary>
    IStream<LlmResponse> TransformOutboundLlmStream(IStream<LlmResponse> stream);
}

/// <summary>
/// Port of pipeline.DummyMiddleware: a base whose hooks all pass through, so
/// implementers override only what they need.
/// </summary>
public class DummyMiddleware : IPipelineMiddleware
{
    public DummyMiddleware(string name = "")
    {
        Name = name;
    }

    public string Name { get; }

    public virtual LlmRequest TransformInboundLlmRequest(LlmRequest request) => request;

    public virtual HttpResponseData TransformInboundRawResponse(HttpResponseData response) => response;

    public virtual IStream<StreamEvent> TransformInboundRawStream(IStream<StreamEvent> stream) => stream;

    public virtual HttpRequestData TransformOutboundRawRequest(HttpRequestData request) => request;

    public virtual void OnOutboundRawError(Exception error)
    {
        // Do nothing.
    }

    public virtual HttpResponseData TransformOutboundRawResponse(HttpResponseData response) => response;

    public virtual LlmResponse TransformOutboundLlmResponse(LlmResponse response) => response;

    public virtual IStream<StreamEvent> TransformOutboundRawStream(IStream<StreamEvent> stream) => stream;

    public virtual IStream<LlmResponse> TransformOutboundLlmStream(IStream<LlmResponse> stream) => stream;
}
