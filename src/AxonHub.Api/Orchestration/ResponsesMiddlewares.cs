using System.Text;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Shared;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of applyResponsesContinuationBody (responses_effective_body.go:15-49):
/// writes the continuation's effective <c>input</c> (and the surviving
/// <c>previous_response_id</c>) into the provider body. The merge keeps the
/// already-protected, already-transformed body when prompt protection cannot
/// prove the replay is safe.
/// </summary>
internal sealed class ResponsesContinuationBodyMiddleware(PersistentOutboundTransformer outbound)
    : DummyMiddleware("apply_responses_continuation_body")
{
    public override HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var continuation = outbound.ResponsesContinuation;
        if (continuation is null
            || continuation.Previous.Length == 0
            || !ChannelTransformOptions.IsResponsesFormat(request.ApiFormat))
        {
            return request;
        }

        var llmRequest = outbound.CurrentLlmRequest;

        // A merge/patch failure keeps the already-transformed protected body.
        if (!PassThrough.TryMergeRequestBodyWithPromptProtection(
                continuation.Effective,
                llmRequest?.ApiFormat,
                outbound.CurrentModel,
                outbound.PromptProtectionMaskRules,
                out var effective,
                out _))
        {
            return request;
        }

        if (outbound.PromptProtectionBodyCheck is { } bodyCheck
            && bodyCheck.ValidateExpanded(effective) is not null)
        {
            return request;
        }

        var payload = OverrideJson.Get(effective, "input");
        var body = request.Body ?? [];

        if (payload.Exists)
        {
            body = SetRaw(body, "input", payload.Node!.Raw);
        }

        var previous = OverrideJson.Get(effective, "previous_response_id");
        if (previous.Exists)
        {
            body = SetRaw(body, "previous_response_id", previous.Node!.Raw);
        }
        else if (!OverrideJson.TryDelete(body, "previous_response_id", out body, out var deleteError))
        {
            throw new InvalidOperationException(
                "delete previous_response_id from continuation body: " + deleteError);
        }

        request.Body = body;
        return request;
    }

    /// <summary>Port of <c>sjson.SetRawBytes</c> on a raw provider body.</summary>
    private static byte[] SetRaw(byte[] body, string path, string raw)
    {
        if (!OverrideJson.TryParse(Encoding.UTF8.GetBytes(raw), out var node) || node is null)
        {
            throw new InvalidOperationException(
                "set " + path + " in continuation body: invalid JSON value");
        }

        if (!OverrideJson.TrySetNode(body, path, node, out var result, out var error))
        {
            throw new InvalidOperationException(
                "set " + path + " in continuation body: " + error);
        }

        return result;
    }
}

/// <summary>
/// Port of verifyResponsesAttempt (responses_attempt.go:304-345): runs on the
/// final provider request, pins the continuation to the origin that will carry
/// it, refuses an unforwardable <c>previous_response_id</c>, and snapshots the
/// final body for the next turn.
/// </summary>
internal sealed class ResponsesVerifyMiddleware(PersistentOutboundTransformer outbound)
    : DummyMiddleware("verify_responses_continuation")
{
    public override HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        outbound.ResponsesContinuation?.Verify(
            request,
            (int)outbound.CurrentChannelId,
            outbound.CurrentModel,
            outbound.ForwardsResponsesId());

        return request;
    }
}

/// <summary>
/// Port of captureResponsesProvider (responses_capture.go:77-111): remembers the
/// final provider response (or its metadata-only projection) so the completed
/// turn can be continued by the next request.
/// </summary>
internal sealed class CaptureResponsesProviderMiddleware(PersistentOutboundTransformer outbound)
    : DummyMiddleware("responses_provider_completion")
{
    public override HttpResponseData TransformOutboundRawResponse(HttpResponseData response)
    {
        ArgumentNullException.ThrowIfNull(response);

        outbound.ResponsesContinuation?.Capture.CaptureResponse(response.Body);
        return response;
    }

    public override IStream<StreamEvent> TransformOutboundRawStream(IStream<StreamEvent> stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var continuation = outbound.ResponsesContinuation;
        if (continuation is null)
        {
            return stream;
        }

        return ResponseHeaders.WithResponseHeaders(
            new ResponsesProviderStream(stream, continuation.Capture),
            ResponseHeaders.GetResponseHeaders(stream));
    }
}

/// <summary>
/// Port of withoutResponsesRecoveryPenalty (responses_health.go:12-24): a
/// continuation that cannot be repaired is a client-facing recovery signal, not
/// a channel failure, so the wrapped health middleware never sees it. The
/// reference also releases a model-circuit-breaker probe lease here; the port
/// has no circuit breaker yet (C036).
/// </summary>
internal sealed class WithoutResponsesRecoveryPenaltyMiddleware(IPipelineMiddleware inner)
    : IPipelineMiddleware
{
    public string Name => inner.Name;

    public LlmRequest TransformInboundLlmRequest(LlmRequest request)
        => inner.TransformInboundLlmRequest(request);

    public HttpResponseData TransformInboundRawResponse(HttpResponseData response)
        => inner.TransformInboundRawResponse(response);

    public IStream<StreamEvent> TransformInboundRawStream(IStream<StreamEvent> stream)
        => inner.TransformInboundRawStream(stream);

    public HttpRequestData TransformOutboundRawRequest(HttpRequestData request)
        => inner.TransformOutboundRawRequest(request);

    public void OnOutboundRawError(Exception error)
    {
        if (ResponsesRecovery.IsRecoveryError(error))
        {
            return;
        }

        inner.OnOutboundRawError(error);
    }

    public HttpResponseData TransformOutboundRawResponse(HttpResponseData response)
        => inner.TransformOutboundRawResponse(response);

    public LlmResponse TransformOutboundLlmResponse(LlmResponse response)
        => inner.TransformOutboundLlmResponse(response);

    public IStream<StreamEvent> TransformOutboundRawStream(IStream<StreamEvent> stream)
        => inner.TransformOutboundRawStream(stream);

    public IStream<LlmResponse> TransformOutboundLlmStream(IStream<LlmResponse> stream)
        => inner.TransformOutboundLlmStream(stream);
}
