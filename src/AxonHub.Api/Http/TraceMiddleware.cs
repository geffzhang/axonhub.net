using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Llm.Transformer.Anthropic.ClaudeCode;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Llm.Transformer.OpenCode;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of middleware.WithTrace (internal/server/middleware/trace.go): resolves
/// a trace id from the primary/extra headers, OpenCode/Codex client markers,
/// Claude Code metadata or configured body fields; exposes it to the handler;
/// and gets or creates the persisted trace row.
/// </summary>
/// <remarks>
/// Failures while reading the body abort with the reference's 400 envelope.
/// Trace persistence failures are non-blocking: the resolved id and response
/// aliases stay visible, exactly like the reference's log-and-continue branch.
/// The reference logs those failures with slog; the port has no logging seam
/// (C095). <see cref="ThreadMiddleware"/> runs first and publishes the thread id
/// consumed here, so a request with the thread header stores <c>thread_id</c>.
/// </remarks>
public sealed class TraceMiddleware(RequestDelegate next, TracingOptions options)
{
    public async Task InvokeAsync(HttpContext context, ITraceStore traceStore)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(traceStore);

        var traceId = GetTraceIdFromHeader(context, options);

        if (traceId.Length == 0 && options.OpenCodeTraceEnabled)
        {
            traceId = OpenCodeHeaders.GetSessionIdFromHeaders(
                HttpRequestReader.CanonicalizeHeaders(context.Request.Headers));
        }

        if (traceId.Length == 0 && options.ClaudeCodeTraceEnabled)
        {
            try
            {
                traceId = await TryExtractTraceIdFromClaudeCodeRequestAsync(context)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteBadRequestAsync(context, "failed to read request body: " + ex.Message)
                    .ConfigureAwait(false);
                return;
            }
        }

        if (traceId.Length == 0 && options.CodexTraceEnabled)
        {
            traceId = CodexHeaders.GetSessionIdFromHeaders(
                HttpRequestReader.CanonicalizeHeaders(context.Request.Headers));
        }

        if (traceId.Length == 0 && options.ExtraTraceBodyFields.Count > 0)
        {
            try
            {
                traceId = await TryGetTraceIdFromBodyAsync(context, options.ExtraTraceBodyFields)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await WriteBadRequestAsync(context, "failed to read request body: " + ex.Message)
                    .ConfigureAwait(false);
                return;
            }
        }

        if (traceId.Length == 0)
        {
            // WithLoggingTracing may have minted a log-correlation id, but that
            // alone must not opt the request into persisted tracing.
            await next(context).ConfigureAwait(false);
            return;
        }

        // The trace middleware can resolve a more specific id than the logging
        // middleware; keep the request context in sync with the final value.
        TraceContext.SetTraceId(context, traceId);

        foreach (var header in options.ResponseTraceHeaders)
        {
            var name = header.Trim();
            if (name.Length > 0)
            {
                context.Response.Headers[name] = traceId;
            }
        }

        if (!RequestIdentity.TryFromHttpContext(context, out var identity))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        // Embeddings and other tool endpoints may carry a trace id header from
        // generic API clients but are not conversation turns: keep the resolved
        // id and response aliases, never persist a trace row.
        if (IsNonMessageEndpoint(context.Request.Path.Value ?? string.Empty))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        TraceRecord trace;
        try
        {
            trace = await traceStore
                .GetOrCreateAsync(
                    identity.ProjectId,
                    traceId,
                    ThreadContext.GetThreadId(context),
                    context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Bypass privacy policy so tokens without write_requests scope can
            // still trigger tracing; a persistence failure must not fail the
            // client call (the reference logs and continues).
            await next(context).ConfigureAwait(false);
            return;
        }

        TraceContext.SetTrace(context, trace);

        // The reference reads the trace back from the request context in
        // persistRequest; the port's context container is HttpContext.Items, so
        // the row id rides on the identity the endpoint already reads. The
        // trace row's own thread id is kept separately: the previous-channel
        // cache writes the thread scope from it (request.go:1768-1774), while
        // the selector reads the current request's thread first
        // (candidates.go:843-849).
        context.Items[RequestIdentity.HttpContextItemKey] = identity with
        {
            TraceId = trace.Id,
            TraceThreadId = trace.ThreadId,
        };

        // Let provider transformers use the trace id as the session id.
        TraceContext.SetSessionId(context, traceId);

        await next(context).ConfigureAwait(false);
    }

    /// <summary>Port of traceHeaderName + getTraceIDFromHeader.</summary>
    public static string GetTraceIdFromHeader(HttpContext context, TracingOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        var traceId = GetHeader(context.Request.Headers, options.TraceHeaderName);
        if (traceId.Length > 0)
        {
            return traceId;
        }

        foreach (var header in options.ExtraTraceHeaders)
        {
            traceId = GetHeader(context.Request.Headers, header);
            if (traceId.Length > 0)
            {
                return traceId;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Port of tryExtractTraceIDFromClaudeCodeRequest: POST /v1/messages or
    /// /anthropic/v1/messages, trace id from <c>metadata.user_id</c>.
    /// </summary>
    public static async Task<string> TryExtractTraceIdFromClaudeCodeRequestAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return string.Empty;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        if (path != "/anthropic/v1/messages" && path != "/v1/messages")
        {
            return string.Empty;
        }

        var body = await ReadBodyAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
        if (body.Length == 0)
        {
            return string.Empty;
        }

        var userId = TryGetJsonPath(body, ["metadata", "user_id"], out var value) ? value : string.Empty;
        if (userId.Length == 0)
        {
            return string.Empty;
        }

        return ClaudeCodeUserID.ParseUserId(userId)?.SessionId ?? string.Empty;
    }

    /// <summary>Port of tryGetTraceIDFromBody: the first configured dot path with a non-empty string value.</summary>
    public static async Task<string> TryGetTraceIdFromBodyAsync(
        HttpContext context,
        IReadOnlyList<string> fields)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(fields);

        if (fields.Count == 0)
        {
            return string.Empty;
        }

        var body = await ReadBodyAsync(context.Request, context.RequestAborted).ConfigureAwait(false);
        if (body.Length == 0)
        {
            return string.Empty;
        }

        foreach (var field in fields)
        {
            var path = field.Split('.');
            if (TryGetJsonPath(body, path, out var value) && value.Length > 0)
            {
                return value;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Port of isNonMessageEndpoint: <c>/embeddings</c> suffixes and Gemini's
    /// <c>:embedContent</c>/<c>:batchEmbedContents</c> model actions.
    /// </summary>
    public static bool IsNonMessageEndpoint(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return path.EndsWith("/embeddings", StringComparison.Ordinal)
            || path.Contains(":embedContent", StringComparison.Ordinal)
            || path.Contains(":batchEmbedContents", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads the client body and rewinds it so the endpoint can read it again
    /// (the reference replaces <c>c.Request.Body</c> with a fresh reader).
    /// </summary>
    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        request.EnableBuffering();

        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        request.Body.Position = 0;
        return buffer.ToArray();
    }

    private static string GetHeader(IHeaderDictionary headers, string name)
        => headers.TryGetValue(name, out var values) && values.Count > 0
            ? values[0] ?? string.Empty
            : string.Empty;

    private static async Task WriteBadRequestAsync(HttpContext context, string message)
    {
        await AbortResponse
            .WriteErrorAsync(
                context,
                StatusCodes.Status400BadRequest,
                message,
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a gjson-style dot path on a JSON body. Malformed JSON is
    /// "absent" (gjson does not error); only object traversal is supported, which
    /// covers the configured field names (C095).
    /// </summary>
    private static bool TryGetJsonPath(byte[] body, IReadOnlyList<string> path, out string value)
    {
        value = string.Empty;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var element = document.RootElement;
            foreach (var segment in path)
            {
                if (element.ValueKind != JsonValueKind.Object
                    || !element.TryGetProperty(segment, out element))
                {
                    return false;
                }
            }

            value = GJsonString(element);
            return true;
        }
    }

    /// <summary>
    /// gjson's Result.String(): JSON strings verbatim, scalars as their literal
    /// text and containers as raw JSON (null is the empty string). Number
    /// normalization differs for exotic literals (C095).
    /// </summary>
    private static string GJsonString(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Object or JsonValueKind.Array => element.GetRawText(),
            _ => string.Empty,
        };
}
