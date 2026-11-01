using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of the request-id half of middleware.WithLoggingTracing
/// (internal/server/middleware/logging.go). The middleware is global in the
/// reference: it mints an <c>ar-&lt;uuid&gt;</c> id for every request, echoes it
/// in the configured response header and publishes it on the request context so
/// handlers can put it in error bodies.
/// </summary>
/// <remarks>
/// The trace-id half of the middleware (with its Claude Code, Codex and OpenCode
/// header/body extraction and the tracing middleware that writes trace response
/// headers) stays unported (C088).
/// </remarks>
public sealed class RequestTracingMiddleware(RequestDelegate next, RequestTracingOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var requestId = RequestIds.Generate();

        // c.Header(requestHeader, requestID) — set before the handler runs, so
        // error responses carry it too.
        context.Response.Headers[options.RequestHeader] = requestId;
        context.Items[RequestIds.HttpContextItemKey] = requestId;

        await next(context).ConfigureAwait(false);
    }
}

/// <summary>Port of the tracing.Config fields the request-id middleware reads.</summary>
public sealed record RequestTracingOptions(string RequestHeader)
{
    /// <summary>middleware.WithLoggingTracing's fallback header name.</summary>
    public const string DefaultRequestHeader = "AH-Request-Id";

    public static RequestTracingOptions Default { get; } = new(DefaultRequestHeader);
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
