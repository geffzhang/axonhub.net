using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Http;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Safeguards;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AxonHub.Api.Endpoints;

/// <summary>
/// Port of api.ChatCompletionHandlers.ChatCompletion
/// (internal/server/api/chat.go:70-142) for <c>POST /v1/chat/completions</c>.
/// </summary>
public static class ChatCompletionEndpoint
{
    /// <summary>
    /// Port of api.StreamWriter: invoked for a streaming response. The
    /// reference swaps it in with WithStreamWriter; binary TTS routes
    /// <see cref="BinaryStreamWriter.WriteAsync"/> instead of SSE.
    /// </summary>
    public delegate Task StreamWriterDelegate(
        HttpContext context,
        IStream<StreamEvent> stream,
        int expectedChoices,
        CancellationToken cancellationToken,
        Func<Exception, CancellationToken, Task<Exception?>>? transformStreamError);

    /// <summary>
    /// Port of ChatCompletion + ChatCompletionWithRequest. The audio speech
    /// route reads the request once to decide between SSE and binary streaming,
    /// then hands the parsed request over via <paramref name="preReadRequest"/>
    /// and the chosen writer via <paramref name="streamWriter"/>.
    /// </summary>
    /// <param name="nilResultIsInternalError">
    /// api.DoubaoHandlers.CreateTask checks that the orchestrator produced a
    /// response and answers 500 <c>biz.ErrInternal</c> when it did not; the
    /// shared chat handler returns silently in that state. The Doubao route opts
    /// into the reference's stricter check.
    /// </param>
    public static async Task HandleAsync(
        HttpContext context,
        ChatCompletionOrchestrator orchestrator,
        HttpRequestData? preReadRequest = null,
        StreamWriterDelegate? streamWriter = null,
        bool nilResultIsInternalError = false,
        SafeguardsEndpointOptions? safeguards = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(orchestrator);

        var cancellationToken = context.RequestAborted;

        HttpRequestData genericRequest;
        if (preReadRequest is not null)
        {
            genericRequest = preReadRequest;
        }
        else
        {
            try
            {
                genericRequest = await HttpRequestReader.ReadAsync(context.Request, cancellationToken);
            }
            catch (Exception ex)
            {
                // ReadHTTPRequest failures go through the inbound transformer, which
                // maps an unknown error to 500 unexpected/internal_server_error.
                await WriteTransformedErrorAsync(context, orchestrator.Inbound.TransformError(ex), cancellationToken);
                return;
            }
        }

        // middleware.WithTrace publishes the resolved trace id as the shared
        // session id (shared.WithSessionID), which transformers read from the
        // request context. The port threads it through the wire request's
        // session seam instead (C062); the codex outbound's SessionIdProvider
        // and the Responses prompt-cache fallback read it from there.
        genericRequest.SessionId ??= TraceContext.GetSessionId(context);

        if (genericRequest.Body is null || genericRequest.Body.Length == 0)
        {
            await WriteJsonErrorAsync(context, 400, "Bad Request", "Request body is empty", cancellationToken);
            return;
        }

        // WithAPIKeyConfig publishes the authenticated identity on the request
        // context; direct calls (tests/internal handlers) fall back to Default.
        var identity = RequestIdentity.FromHttpContext(context);

        // api/chat.go:99-115: only Claude Code requests asking for server-side
        // safeguards need the settings lookup; prepare then removes the locally
        // handled safeguard from the upstream body.
        var evaluator = (SafeguardsEvaluator?)null;
        CancellationTokenSource? processCancellation = null;
        var processToken = cancellationToken;

        if (safeguards is not null)
        {
            var config = safeguards.Config;
            if (safeguards.ConfigProvider is not null
                && SafeguardsRequest.HasDangerousToolUse(genericRequest.Body))
            {
                try
                {
                    config = await safeguards.ConfigProvider(cancellationToken);
                }
                catch (Exception)
                {
                    await WriteJsonErrorAsync(
                        context,
                        500,
                        GoHttpStatus.Text(500),
                        "Unable to load Claude Code safeguards settings",
                        cancellationToken);
                    return;
                }
            }

            var classifier = safeguards.ClassifierFactory?.Invoke(orchestrator, identity, genericRequest);
            SafeguardsRequest.Prepared prepared;
            try
            {
                prepared = SafeguardsRequest.Prepare(genericRequest, config, classifier);
            }
            catch (SafeguardsDecodeException ex)
            {
                await WriteJsonErrorAsync(context, 400, GoHttpStatus.Text(400), ex.Message, cancellationToken);
                return;
            }

            evaluator = prepared.Evaluator;
            genericRequest = prepared.Request;

            if (evaluator is not null)
            {
                processCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                processToken = processCancellation.Token;
            }
        }

        // The request row id is only known once the persistence middleware has
        // created it; response headers are written by the HTTP boundary after
        // the handler produces the response (middleware/response_headers.go).
        var requestId = 0L;

        try
        {
            ChatCompletionResult result;
            try
            {
                result = await orchestrator.ProcessAsync(
                    genericRequest,
                    identity,
                    id => requestId = id,
                    processToken);
            }
            catch (Exception ex)
            {
                // api.transformOrchestratorError: classify, apply the upstream error
                // policy, then map through the inbound transformer.
                await WriteTransformedErrorAsync(
                    context,
                    await orchestrator.TransformErrorAsync(ex, cancellationToken),
                    cancellationToken);
                RecordResponseHeaders(context, orchestrator, requestId);
                return;
            }

            // api/chat.go:105: forward the small, explicit set of upstream headers
            // that are safe at the client boundary (codex responses only).
            WriteForwardResponseHeaders(context, genericRequest, result);

            if (result.Response is { } response)
            {
                CopyCodexTurnStateHeader(context.Response.Headers, response.Headers);

                // api/chat.go:144-148: the reviewer annotates (or refuses) the
                // complete response body before anything is written.
                var body = response.Body ?? [];
                if (evaluator is not null)
                {
                    try
                    {
                        body = evaluator.AnnotateResponse(cancellationToken, body);
                    }
                    catch (SafeguardsDecodeException ex)
                    {
                        await WriteJsonErrorAsync(
                            context,
                            502,
                            GoHttpStatus.Text(502),
                            ex.Message,
                            cancellationToken);
                        RecordResponseHeaders(context, orchestrator, requestId);
                        return;
                    }
                }

                await WriteResponseAsync(context, response, body, cancellationToken);
                RecordResponseHeaders(context, orchestrator, requestId);
                return;
            }

            if (result.Stream is { } stream)
            {
                context.Response.Headers["Access-Control-Allow-Origin"] = "*";

                // chat.go:154-165 closes and then waits on the orchestrator's
                // stream, not on the API-level wrappers, so finalization still
                // works when safeguards/priming wrap it.
                var resultStream = stream;

                // api/chat.go:169-170: review (and strip) each event before the
                // error-transforming and codex-priming wrappers see it.
                Action? abortUpstream = null;
                if (processCancellation is not null)
                {
                    abortUpstream = processCancellation.Cancel;
                }

                stream = SafeguardsStream.Wrap(
                    stream,
                    evaluator,
                    cancellationToken,
                    abortUpstream);

                // api/chat.go:134-136: the codex turn-state header travels on the
                // first upstream event's transport headers, and SSE headers must be
                // sent before the first flush, so prime it here.
                if (genericRequest.Path.EndsWith("/responses", StringComparison.Ordinal))
                {
                    stream = PrimeCodexTurnStateHeader(context.Response.Headers, stream);
                }

                var writer = streamWriter ?? new StreamWriterDelegate(SseStreamWriter.WriteAsync);
                try
                {
                    await writer(
                        context,
                        stream,
                        ExpectedStreamChoices.FromRequestBody(genericRequest.Body),
                        cancellationToken,
                        orchestrator.TransformStreamErrorAsync);
                }
                finally
                {
                    resultStream.Dispose();
                    if (resultStream is IFinalizationWaiter waiter)
                    {
                        // The reference blocks here until source accounting is
                        // done; the port's synchronous persistence already
                        // finalized by the time Dispose returned.
                        waiter.WaitForFinalization();
                    }

                    RecordResponseHeaders(context, orchestrator, requestId);
                }

                return;
            }

            if (nilResultIsInternalError)
            {
                // api/doubao.go:92-95: result.ChatCompletion == nil → 500 ErrInternal.
                await WriteJsonErrorAsync(
                    context,
                    500,
                    GoHttpStatus.Text(500),
                    "server internal error, please try again later",
                    cancellationToken);
            }
        }
        finally
        {
            // api/chat.go:117-122: the abort callback cancels the upstream
            // attempt once the response (or stream) has been handled.
            if (processCancellation is not null)
            {
                try
                {
                    processCancellation.Cancel();
                }
                catch (Exception)
                {
                    // Cancellation callbacks are pipeline-owned.
                }

                processCancellation.Dispose();
            }
        }
    }

    /// <summary>
    /// Snapshots the response headers once the handler has written the response.
    /// ASP.NET freezes headers after the first write, so this matches the
    /// reference's capture at first Write/WriteHeaderNow.
    /// </summary>
    private static void RecordResponseHeaders(
        HttpContext context,
        ChatCompletionOrchestrator orchestrator,
        long requestId)
    {
        if (requestId == 0)
        {
            return;
        }

        var headers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var header in context.Response.Headers)
        {
            var values = new List<string>(header.Value.Count);
            foreach (var value in header.Value)
            {
                if (value is not null)
                {
                    values.Add(value);
                }
            }

            headers[header.Key] = values;
        }

        orchestrator.RecordResponseHeaders(requestId, headers);
    }

    /// <summary>gin: <c>c.Data(resp.StatusCode, contentType, resp.Body)</c>.</summary>
    internal static Task WriteResponseAsync(
        HttpContext context,
        HttpResponseData response,
        CancellationToken cancellationToken)
        => WriteResponseAsync(context, response, response.Body ?? [], cancellationToken);

    /// <summary>
    /// gin: <c>c.Data(resp.StatusCode, contentType, body)</c>; the body may be
    /// replaced by the safeguards reviewer before it is written.
    /// </summary>
    internal static async Task WriteResponseAsync(
        HttpContext context,
        HttpResponseData response,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var contentType = HttpClientUtils.HeaderGet(response.Headers, "Content-Type");
        if (contentType.Length == 0)
        {
            contentType = "application/json";
        }

        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = contentType;
        await WriteBodyAsync(context, body, cancellationToken);
    }

    /// <summary>Port of api.writeForwardResponseHeaders (chat.go:146-159).</summary>
    internal static void WriteForwardResponseHeaders(
        HttpContext context,
        HttpRequestData? request,
        ChatCompletionResult result)
    {
        if (!result.CodexResponseHeadersSupported
            || request is null
            || !request.Path.EndsWith("/responses", StringComparison.Ordinal)
            || CodexHeaders.GetSessionIdFromHeaders(request.Headers).Length == 0)
        {
            return;
        }

        var headers = result.Response is not null
            ? result.Response.Headers
            : ResponseHeaders.GetResponseHeaders(result.Stream);

        // httpclient.MergeForwardResponseHeaders copies only the explicit safe
        // set; the downstream writer has no stale value to remove.
        if (ResponseHeaders.MergeForwardResponseHeaders(null, headers) is not { } forwarded)
        {
            return;
        }

        foreach (var (name, values) in forwarded)
        {
            context.Response.Headers[name] = new StringValues([.. values]);
        }
    }

    /// <summary>Port of api.copyCodexTurnStateHeader (chat.go:161-169).</summary>
    internal static void CopyCodexTurnStateHeader(
        IHeaderDictionary dst,
        Dictionary<string, List<string>>? src)
    {
        if (src is null)
        {
            return;
        }

        var value = HttpClientUtils.HeaderGet(src, CodexHeaders.TurnStateHeader);
        if (value.Length > 0)
        {
            dst[CodexHeaders.TurnStateHeader] = value;
        }
    }

    /// <summary>
    /// Port of api.primeCodexTurnStateHeader (chat.go:181-192): pull the first
    /// event so its transport headers can be copied before SSE headers are sent,
    /// then hand the event back through <see cref="PrimedStream"/>.
    /// </summary>
    internal static IStream<StreamEvent> PrimeCodexTurnStateHeader(
        IHeaderDictionary dst,
        IStream<StreamEvent> stream)
    {
        if (!stream.Next())
        {
            return stream;
        }

        var first = stream.Current;
        if (first is not null)
        {
            CopyCodexTurnStateHeader(dst, first.Headers);
        }

        return new PrimedStream(stream, first);
    }

    /// <summary>
    /// Port of api.primedStream (chat.go:171-251 plus the capability methods in
    /// stream_delivery.go:27-70): priming must not hide the source's delivery
    /// capabilities, because the SSE writer gates and acknowledges through them.
    /// </summary>
    internal sealed class PrimedStream(IStream<StreamEvent> stream, StreamEvent? first)
        : IStream<StreamEvent>, IDeliveryAcknowledger, IDeliveryBoundary, IFinalizationWaiter, IExpectedStreamChoices
    {
        private bool _pending = true;

        public StreamEvent? Current
        {
            get
            {
                if (_pending)
                {
                    _pending = false;
                    return first;
                }

                return stream.Current;
            }
        }

        public Exception? Err => stream.Err;

        public int ExpectedStreamChoices => DeliveryCapabilities.ExpectedStreamChoicesOf(stream);

        public bool DeliveryAcknowledged
            => stream is IDeliveryAcknowledger observer && observer.DeliveryAcknowledged;

        public bool Next() => _pending || stream.Next();

        public void AcknowledgeDelivery()
            => (stream as IDeliveryAcknowledger)?.AcknowledgeDelivery();

        public Task<Exception?> DeliverAsync(
            Func<Task<DeliveryWrite>> write,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(write);

            if (stream is IDeliveryBoundary boundary)
            {
                return boundary.DeliverAsync(write, cancellationToken);
            }

            return DeliveryCapabilities.DeliverWithoutBoundaryAsync(
                stream as IDeliveryAcknowledger,
                write);
        }

        public void WaitForFinalization()
            => (stream as IFinalizationWaiter)?.WaitForFinalization();

        public void Dispose() => stream.Dispose();
    }

    /// <summary>gin: <c>c.JSON(status, json.RawMessage(httpErr.Body))</c>.</summary>
    internal static async Task WriteTransformedErrorAsync(
        HttpContext context,
        HttpResponseData errorResponse,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = errorResponse.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await WriteBodyAsync(context, errorResponse.Body ?? [], cancellationToken);
    }

    /// <summary>api.JSONError: <c>{"error":{"type":statusText,"message":err}}</c>.</summary>
    internal static async Task WriteJsonErrorAsync(
        HttpContext context,
        int statusCode,
        string type,
        string message,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("error");
            writer.WriteStartObject();
            writer.WriteString("type", type);
            writer.WriteString("message", message);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        await WriteBodyAsync(context, buffer.ToArray(), cancellationToken);
    }

    private static async Task WriteBodyAsync(
        HttpContext context,
        byte[] body,
        CancellationToken cancellationToken)
    {
        try
        {
            if (body.Length > 0)
            {
                await context.Response.Body.WriteAsync(body, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // Reference logs the failed write; the client is already gone.
        }
    }
}
