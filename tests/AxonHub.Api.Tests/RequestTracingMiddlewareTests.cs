using AxonHub.Api.Http;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks middleware.WithLoggingTracing (internal/server/middleware/logging.go):
/// an <c>ar-{uuid}</c> id per request written to the configured response header,
/// and a trace id taken from the trace header or minted as <c>at-{uuid}</c> for
/// log correlation (never opting a request into persisted tracing by itself).
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
            TracingOptions.Default);

        await middleware.InvokeAsync(context);

        var requestId = context.Response.Headers[TracingOptions.DefaultRequestHeader].ToString();
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
            new TracingOptions { RequestHeader = "X-Request-Id" });

        await middleware.InvokeAsync(context);

        Assert.False(string.IsNullOrEmpty(context.Response.Headers["X-Request-Id"].ToString()));
        Assert.False(context.Response.Headers.ContainsKey(TracingOptions.DefaultRequestHeader));
    }

    [Fact]
    public async Task EachRequestGetsItsOwnId()
    {
        var middleware = new RequestTracingMiddleware(
            static _ => Task.CompletedTask,
            TracingOptions.Default);

        var first = new DefaultHttpContext();
        var second = new DefaultHttpContext();
        await middleware.InvokeAsync(first);
        await middleware.InvokeAsync(second);

        Assert.NotEqual(
            first.Response.Headers[TracingOptions.DefaultRequestHeader].ToString(),
            second.Response.Headers[TracingOptions.DefaultRequestHeader].ToString());
    }

    [Fact]
    public async Task UsesTheTraceHeaderWhenPresent()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["AH-Trace-Id"] = "trace-from-header";
        string? seenInsidePipeline = null;

        var middleware = new RequestTracingMiddleware(
            inner =>
            {
                seenInsidePipeline = TraceContext.GetTraceId(inner);
                return Task.CompletedTask;
            },
            TracingOptions.Default);

        await middleware.InvokeAsync(context);

        Assert.Equal("trace-from-header", seenInsidePipeline);
    }

    [Fact]
    public async Task GeneratesAnAtGuidWhenTheTraceHeaderIsMissing()
    {
        var context = new DefaultHttpContext();
        string? seenInsidePipeline = null;

        var middleware = new RequestTracingMiddleware(
            inner =>
            {
                seenInsidePipeline = TraceContext.GetTraceId(inner);
                return Task.CompletedTask;
            },
            TracingOptions.Default);

        await middleware.InvokeAsync(context);

        Assert.NotNull(seenInsidePipeline);
        Assert.StartsWith("at-", seenInsidePipeline, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(seenInsidePipeline[3..], "D", out _));
    }

    [Fact]
    public async Task UsesTheConfiguredTraceHeaderName()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Trace-Id"] = "custom-trace";
        string? seenInsidePipeline = null;

        var middleware = new RequestTracingMiddleware(
            inner =>
            {
                seenInsidePipeline = TraceContext.GetTraceId(inner);
                return Task.CompletedTask;
            },
            new TracingOptions { TraceHeader = "X-Trace-Id" });

        await middleware.InvokeAsync(context);

        Assert.Equal("custom-trace", seenInsidePipeline);
    }

    [Fact]
    public void NoMiddlewareMeansNoRequestId()
    {
        Assert.Null(RequestIds.FromHttpContext(new DefaultHttpContext()));
    }
}
