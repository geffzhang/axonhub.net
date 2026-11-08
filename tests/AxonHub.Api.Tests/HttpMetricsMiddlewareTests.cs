using AxonHub.Api.Http;
using AxonHub.Api.Metrics;
using AxonHub.Api.Static;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/middleware/metrics.go (difference ledger C223): the
/// middleware records method / path / status / duration with gin's FullPath()
/// semantics, and a request that panics is not recorded (the reference's
/// Recovery sits outside WithMetrics).
/// </summary>
public sealed class HttpMetricsMiddlewareTests
{
    [Fact]
    public async Task RecordsTheMatchedRouteInGinSyntax()
    {
        using var metrics = new ServerMetrics("AxonHub-test-http-middleware");
        using var capture = new MetricsCapture(metrics.Meter);

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.SetEndpoint(BuildEndpoint("/v1/videos/{id}"));

        var middleware = new HttpMetricsMiddleware(
            static nextContext =>
            {
                nextContext.Response.StatusCode = 201;
                return Task.CompletedTask;
            },
            metrics,
            new FixedElapsedClock(TimeSpan.FromSeconds(0.25)));

        await middleware.InvokeAsync(context);

        var count = Assert.Single(capture.Counters["http_request_count"]);
        Assert.Equal(1d, count.Value);
        Assert.Equal("POST", (string)count.Tags["method"]!);
        Assert.Equal("/v1/videos/:id", (string)count.Tags["path"]!);
        Assert.Equal(201, (int)count.Tags["status_code"]!);

        var duration = Assert.Single(capture.Histograms["http_request_duration_seconds"]);
        Assert.Equal(0.25, duration.Value, 9);
        Assert.Equal("/v1/videos/:id", (string)duration.Tags["path"]!);
    }

    [Fact]
    public async Task StaticFallbackRecordsTheEmptyPath()
    {
        using var metrics = new ServerMetrics("AxonHub-test-http-fallback");
        using var capture = new MetricsCapture(metrics.Meter);

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.SetEndpoint(new RouteEndpoint(
            static _ => Task.CompletedTask,
            RoutePatternFactory.Parse("{**path}"),
            int.MaxValue,
            "Fallback {**path}",
            new EndpointMetadataCollection(StaticFallbackMetadata.Instance)));

        var middleware = new HttpMetricsMiddleware(
            static _ => Task.CompletedTask,
            metrics,
            new FixedElapsedClock(TimeSpan.FromSeconds(1)));

        await middleware.InvokeAsync(context);

        var count = Assert.Single(capture.Counters["http_request_count"]);
        Assert.Equal(string.Empty, (string)count.Tags["path"]!);
        Assert.Equal(200, (int)count.Tags["status_code"]!);
        Assert.Equal(string.Empty, HttpMetricsMiddleware.RouteTemplate(new DefaultHttpContext()));
    }

    [Fact]
    public async Task PanickingHandlerIsNotRecorded()
    {
        using var metrics = new ServerMetrics("AxonHub-test-http-panic");
        using var capture = new MetricsCapture(metrics.Meter);

        RequestDelegate next = static _ => throw new InvalidOperationException("boom");
        var middleware = new HttpMetricsMiddleware(
            next,
            metrics,
            new FixedElapsedClock(TimeSpan.FromSeconds(1)));

        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.SetEndpoint(BuildEndpoint("/v1/chat/completions"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        Assert.Empty(capture.Counters);
        Assert.Empty(capture.Histograms);
    }

    [Theory]
    [InlineData("/health", "/health")]
    [InlineData("/v1/videos/{id}", "/v1/videos/:id")]
    [InlineData("/v1/models/{*model}", "/v1/models/*model")]
    [InlineData("/v1beta/models/{**action}", "/v1beta/models/*action")]
    [InlineData("/gemini/{gemini-api-version}/models/{**action}", "/gemini/:gemini-api-version/models/*action")]
    [InlineData("/admin/items/{id:int}", "/admin/items/:id")]
    public void ConvertsAspNetRoutePatternsToGinFullPath(string aspNetPattern, string ginPath)
    {
        Assert.Equal(ginPath, HttpMetricsMiddleware.ToGinRoutePath(aspNetPattern));
    }

    private static Endpoint BuildEndpoint(string pattern)
        => new RouteEndpoint(
            static _ => Task.CompletedTask,
            RoutePatternFactory.Parse(pattern),
            0,
            pattern);
}
