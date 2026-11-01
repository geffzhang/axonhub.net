using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Http;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Streams;
using Microsoft.AspNetCore.Http;

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
    public static async Task HandleAsync(
        HttpContext context,
        ChatCompletionOrchestrator orchestrator,
        HttpRequestData? preReadRequest = null,
        StreamWriterDelegate? streamWriter = null)
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

        // The request row id is only known once the persistence middleware has
        // created it; response headers are written by the HTTP boundary after
        // the handler produces the response (middleware/response_headers.go).
        var requestId = 0L;

        // WithAPIKeyConfig publishes the authenticated identity on the request
        // context; direct calls (tests/internal handlers) fall back to Default.
        var identity = RequestIdentity.FromHttpContext(context);

        ChatCompletionResult result;
        try
        {
            result = await orchestrator.ProcessAsync(
                genericRequest,
                identity,
                id => requestId = id,
                cancellationToken);
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

        if (result.Response is { } response)
        {
            await WriteResponseAsync(context, response, cancellationToken);
            RecordResponseHeaders(context, orchestrator, requestId);
            return;
        }

        if (result.Stream is { } stream)
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";

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
                stream.Dispose();
                RecordResponseHeaders(context, orchestrator, requestId);
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
    internal static async Task WriteResponseAsync(
        HttpContext context,
        HttpResponseData response,
        CancellationToken cancellationToken)
    {
        var contentType = HttpClientUtils.HeaderGet(response.Headers, "Content-Type");
        if (contentType.Length == 0)
        {
            contentType = "application/json";
        }

        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = contentType;
        await WriteBodyAsync(context, response.Body ?? [], cancellationToken);
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
