using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of <c>finalizeTransportRequest</c>
/// (internal/server/orchestrator/outbound.go:912-925): after pass-through and
/// header overrides, let the current provider transformer remove fields the
/// selected transport cannot accept, so persistence and execution observe the
/// same provider request.
/// </summary>
internal sealed class TransportRequestFinalizerMiddleware(PersistentOutboundTransformer outbound)
    : DummyMiddleware("finalize_transport_request")
{
    public override HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
        // The outbound only yields null for a null request, which the pipeline
        // middleware contract does not admit; keep the incoming request then.
        => outbound.FinalizeTransportRequest(request) ?? request;
}
