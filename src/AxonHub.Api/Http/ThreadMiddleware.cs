using AxonHub.Api.Auth;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of middleware.WithThread (internal/server/middleware/thread.go): reads
/// the configured thread header, gets or creates the thread row for the
/// authenticated project, and publishes its database id for the trace
/// middleware.
/// </summary>
/// <remarks>
/// Thread tracking is best effort: a missing header, a request without an
/// authenticated project or a persistence failure all fall through to the next
/// middleware. The reference logs the failure with slog; the port has no
/// logging seam (C095).
/// </remarks>
public sealed class ThreadMiddleware(RequestDelegate next, TracingOptions options)
{
    public async Task InvokeAsync(HttpContext context, IThreadStore threadStore)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(threadStore);

        // gin's c.GetHeader returns the first value and never trims it.
        var threadId = context.Request.Headers.TryGetValue(options.ThreadHeaderName, out var values)
            && values.Count > 0
                ? values[0] ?? string.Empty
                : string.Empty;
        if (threadId.Length == 0)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!RequestIdentity.TryFromHttpContext(context, out var identity))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        try
        {
            var thread = await threadStore
                .GetOrCreateAsync(identity.ProjectId, threadId, context.RequestAborted)
                .ConfigureAwait(false);
            ThreadContext.SetThreadId(context, thread.Id);

            // Port of contexts.WithThread: the reference publishes the loaded
            // thread on the request context, where the trace sticky selector
            // reads it after the trace middleware.
            context.Items[RequestIdentity.HttpContextItemKey] =
                identity with { ThreadId = thread.Id };
        }
        catch (Exception)
        {
            // The reference logs and continues: thread tracking must never
            // block the request (thread.go:39-45).
        }

        await next(context).ConfigureAwait(false);
    }
}
