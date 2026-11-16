using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of the trace half of internal/contexts (trace.go, container fields) and
/// the id setters in internal/tracing (tracing.go:66-84). ASP.NET's
/// <see cref="HttpContext.Items"/> is the request-scoped container the reference
/// threads through <c>context.Context</c>.
/// </summary>
public static class TraceContext
{
    /// <summary>The trace id for log correlation (tracing.WithTraceID).</summary>
    public const string TraceIdItemKey = "AxonHub.TraceId";

    /// <summary>The session id handed to provider transformers (shared.WithSessionID).</summary>
    public const string SessionIdItemKey = "AxonHub.SessionId";

    /// <summary>The persisted trace entity (contexts.WithTrace).</summary>
    public const string TraceItemKey = "AxonHub.Trace";

    /// <summary>Port of tracing.GenerateTraceID: <c>at-{{uuid}}</c>.</summary>
    public static string GenerateTraceId() => "at-" + Guid.NewGuid().ToString("D");

    public static void SetTraceId(HttpContext context, string traceId)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[TraceIdItemKey] = traceId;
    }

    /// <summary>Port of tracing.GetTraceID: null when the middleware did not run.</summary>
    public static string? GetTraceId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(TraceIdItemKey, out var item) && item is string traceId
            ? traceId
            : null;
    }

    public static void SetSessionId(HttpContext context, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[SessionIdItemKey] = sessionId;
    }

    /// <summary>Port of shared.GetSessionID.</summary>
    public static string? GetSessionId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(SessionIdItemKey, out var item) && item is string sessionId
            ? sessionId
            : null;
    }

    public static void SetTrace(HttpContext context, TraceRecord trace)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(trace);
        context.Items[TraceItemKey] = trace;
    }

    /// <summary>Port of contexts.GetTrace.</summary>
    public static TraceRecord? GetTrace(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(TraceItemKey, out var item) ? item as TraceRecord : null;
    }
}

/// <summary>
/// Port of contexts.WithThread/GetThread for the thread id alone. The reference
/// stores the loaded <c>ent.Thread</c>; nothing downstream reads more than the
/// row id, so <see cref="ThreadMiddleware"/> publishes only that value.
/// </summary>
public static class ThreadContext
{
    public const string HttpContextItemKey = "AxonHub.ThreadId";

    public static void SetThreadId(HttpContext context, long threadId)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[HttpContextItemKey] = threadId;
    }

    public static long? GetThreadId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.Items.TryGetValue(HttpContextItemKey, out var item) && item is long threadId
            ? threadId
            : null;
    }
}
