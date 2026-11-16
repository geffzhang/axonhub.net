using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of middleware.WithLoggingTracing (internal/server/middleware/logging.go).
/// The middleware is global in the reference: it resolves a trace id from the
/// trace header (or mints an <c>at-&lt;uuid&gt;</c> one for log correlation),
/// mints an <c>ar-&lt;uuid&gt;</c> request id for every request, echoes the
/// request id in the configured response header and publishes both ids on the
/// request context so handlers can put them in error bodies.
/// </summary>
/// <remarks>
/// The generated trace id is only a logging correlation id. Persisted tracing
/// is opted into by <see cref="TraceMiddleware"/> through an explicit source
/// (header, client marker or configured body field), never by this fallback.
/// The reference also publishes an operation name (<c>METHOD path</c>) for its
/// logger; the port has no logging seam (C095).
/// </remarks>
public sealed class RequestTracingMiddleware(RequestDelegate next, TracingOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var traceId = context.Request.Headers.TryGetValue(options.TraceHeaderName, out var values)
            && values.Count > 0
                ? values[0] ?? string.Empty
                : string.Empty;
        if (traceId.Length == 0)
        {
            traceId = TraceContext.GenerateTraceId();
        }

        var requestId = RequestIds.Generate();

        // c.Header(requestHeader, requestID) — set before the handler runs, so
        // error responses carry it too.
        context.Response.Headers[options.RequestHeaderName] = requestId;
        context.Items[RequestIds.HttpContextItemKey] = requestId;
        TraceContext.SetTraceId(context, traceId);

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>The request id the reference keeps in <c>contexts</c>.</summary>
public static class RequestIds
{
    public const string HttpContextItemKey = "AxonHub.RequestId";

    /// <summary>Port of tracing.GenerateRequestID: <c>ar-{{uuid}}</c>.</summary>
    public static string Generate() => "ar-" + Guid.NewGuid().ToString("D");

    /// <summary>Port of contexts.GetRequestID: null when no middleware ran.</summary>
    public static string? FromHttpContext(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(HttpContextItemKey, out var item)
            && item is string requestId
                ? requestId
                : null;
    }
}
