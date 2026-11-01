using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI;

namespace AxonHub.Llm.Transformer.OpenCode;

/// <summary>
/// Port of opencode.WithSessionHeader (llm/transformer/opencode/outbound.go:66-94):
/// decorates an outbound transformer with OpenCode Go session affinity. The
/// customized variant keeps <see cref="IChannelCustomizedExecutor"/> and
/// <see cref="ITransportRequestFinalizer"/> behaviour intact, mirroring the
/// reference's structural interface checks.
/// </summary>
public static class OpenCodeSessionHeader
{
    /// <summary>Port of WithSessionHeader.</summary>
    public static IOutboundTransformer? WithSessionHeader(IOutboundTransformer? outbound)
    {
        if (outbound is null)
        {
            return null;
        }

        var wrapped = new OpenCodeSessionHeaderOutbound(outbound);
        if (outbound is IChannelCustomizedExecutor customizer)
        {
            return new OpenCodeSessionHeaderCustomizedOutbound(wrapped, customizer);
        }

        return wrapped;
    }

    /// <summary>Port of setSessionHeader.</summary>
    public static void SetSessionHeader(LlmRequest? llmRequest, HttpRequestData? httpRequest)
    {
        if (httpRequest is null)
        {
            return;
        }

        httpRequest.Headers ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
        HttpClientUtils.HeaderSet(
            httpRequest.Headers,
            OpenCodeHeaders.SessionHeader,
            ResolveSessionId(llmRequest));
    }

    /// <summary>
    /// Port of resolveSessionID: the first OpenCode inbound header, then the
    /// shared context session (the port carries it on the raw request, C062),
    /// then a fresh UUID.
    /// </summary>
    public static string ResolveSessionId(LlmRequest? llmRequest)
    {
        if (llmRequest?.RawRequest is not null)
        {
            if (OpenCodeHeaders.GetSessionIdFromHeaders(llmRequest.RawRequest.Headers) is { Length: > 0 } headerSessionId)
            {
                return headerSessionId;
            }

            if (llmRequest.RawRequest.SessionId is { } sessionId
                && sessionId.Trim() is { Length: > 0 } trimmed)
            {
                return trimmed;
            }
        }

        return Guid.NewGuid().ToString("D");
    }
}

/// <summary>Port of opencode.sessionHeaderOutbound.</summary>
public class OpenCodeSessionHeaderOutbound : IOutboundTransformer, IApiKeyProvenance
{
    /// <summary>The decorated outbound (Go's embedded <c>Outbound</c>).</summary>
    protected IOutboundTransformer Inner { get; }

    public OpenCodeSessionHeaderOutbound(IOutboundTransformer inner)
    {
        Inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public string ApiFormat => Inner.ApiFormat;

    public string? SelectedApiKey => (Inner as IApiKeyProvenance)?.SelectedApiKey;

    public int? SelectedApiKeyIndex => (Inner as IApiKeyProvenance)?.SelectedApiKeyIndex;

    /// <summary>Port of (*sessionHeaderOutbound).TransformRequest.</summary>
    public HttpRequestData TransformRequest(LlmRequest? request)
    {
        var httpRequest = Inner.TransformRequest(request);
        OpenCodeSessionHeader.SetSessionHeader(request, httpRequest);
        return httpRequest;
    }

    public LlmResponse TransformResponse(HttpResponseData? response) => Inner.TransformResponse(response);

    public IStream<LlmResponse> TransformStream(HttpRequestData? request, IStream<StreamEvent> stream)
        => Inner.TransformStream(request, stream);

    public ResponseError TransformError(HttpClientError? error) => Inner.TransformError(error);

    public byte[] AggregateStreamChunks(
        HttpRequestData? request,
        IReadOnlyList<StreamEvent> chunks,
        out ResponseMeta meta)
        => Inner.AggregateStreamChunks(request, chunks, out meta);
}

/// <summary>Port of opencode.sessionHeaderCustomizedOutbound.</summary>
public sealed class OpenCodeSessionHeaderCustomizedOutbound :
    OpenCodeSessionHeaderOutbound,
    IChannelCustomizedExecutor,
    ITransportRequestFinalizer
{
    private readonly IChannelCustomizedExecutor _customizer;

    public OpenCodeSessionHeaderCustomizedOutbound(
        OpenCodeSessionHeaderOutbound inner,
        IChannelCustomizedExecutor customizer)
        : base(inner)
    {
        _customizer = customizer ?? throw new ArgumentNullException(nameof(customizer));
    }

    /// <summary>Port of CustomizeExecutor: forwards to the wrapped outbound.</summary>
    public IExecutor CustomizeExecutor(IExecutor executor) => _customizer.CustomizeExecutor(executor);

    /// <summary>Port of FinalizeTransportRequest: forwards when the wrapped outbound supports it.</summary>
    public HttpRequestData? FinalizeTransportRequest(HttpRequestData? request)
        => Inner is ITransportRequestFinalizer finalizer
            ? finalizer.FinalizeTransportRequest(request)
            : request;
}
