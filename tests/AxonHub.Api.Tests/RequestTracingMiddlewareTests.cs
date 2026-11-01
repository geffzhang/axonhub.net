using AxonHub.Api.Http;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the request-id half of middleware.WithLoggingTracing
/// (internal/server/middleware/logging.go): an <c>ar-{uuid}</c> id per request,
/// written to the configured response header and published for handlers.
/// </summary>
public sealed class RequestTracingMiddlewareTests
{
    [Fact]
    public async Task GeneratesAnArGuidAndEchoesIt()
    {
        var context = new DefaultHttpContext();
        string? seenInsidePipeline = null;

        var middleware = new RequestTracingMiddleware(
            inner =>
            {
                seenInsidePipeline = RequestIds.FromHttpContext(inner);
                return Task.CompletedTask;
            },
            RequestTracingOptions.Default);

        await middleware.InvokeAsync(context);

        var requestId = context.Response.Headers[RequestTracingOptions.DefaultRequestHeader].ToString();
        Assert.StartsWith("ar-", requestId, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(requestId[3..], "D", out var uuid));
        Assert.NotEqual(Guid.Empty, uuid);

        // The handler sees the same id the response header carries.
        Assert.Equal(requestId, seenInsidePipeline);
    }

    [Fact]
    public async Task UsesTheConfiguredHeaderName()
    {
        var context = new DefaultHttpContext();
        var middleware = new RequestTracingMiddleware(
            static _ => Task.CompletedTask,
            new RequestTracingOptions("X-Request-Id"));

        await middleware.InvokeAsync(context);

        Assert.False(string.IsNullOrEmpty(context.Response.Headers["X-Request-Id"].ToString()));
        Assert.False(context.Response.Headers.ContainsKey(RequestTracingOptions.DefaultRequestHeader));
    }

    [Fact]
    public async Task EachRequestGetsItsOwnId()
    {
        var middleware = new RequestTracingMiddleware(
            static _ => Task.CompletedTask,
            RequestTracingOptions.Default);

        var first = new DefaultHttpContext();
        var second = new DefaultHttpContext();
        await middleware.InvokeAsync(first);
        await middleware.InvokeAsync(second);

        Assert.NotEqual(
            first.Response.Headers[RequestTracingOptions.DefaultRequestHeader].ToString(),
            second.Response.Headers[RequestTracingOptions.DefaultRequestHeader].ToString());
    }

    [Fact]
    public void NoMiddlewareMeansNoRequestId()
    {
        Assert.Null(RequestIds.FromHttpContext(new DefaultHttpContext()));
    }
}
