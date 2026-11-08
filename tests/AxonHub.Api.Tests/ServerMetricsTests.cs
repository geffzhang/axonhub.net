using System.Diagnostics.Metrics;
using AxonHub.Api.Metrics;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/metrics/metrics.go (difference ledger C223): the nine
/// instruments keep the reference names, units, descriptions and tags on
/// System.Diagnostics.Metrics.
/// </summary>
public sealed class ServerMetricsTests
{
    [Fact]
    public void InstrumentsMatchTheReferenceSurface()
    {
        using var metrics = new ServerMetrics("AxonHub-test-surface");
        using var capture = new MetricsCapture(metrics.Meter);

        Assert.Equal("AxonHub-test-surface", metrics.Meter.Name);
        Assert.Equal(9, capture.Instruments.Count);

        AssertInstrument<Counter<long>>(
            capture, "http_request_count", "requests", "Number of HTTP requests");
        AssertInstrument<Histogram<double>>(
            capture, "http_request_duration_seconds", "seconds", "HTTP request duration in seconds");
        AssertInstrument<Counter<long>>(
            capture, "graphql_request_count", "requests", "Number of GraphQL requests");
        AssertInstrument<Histogram<double>>(
            capture, "graphql_request_duration_seconds", "seconds", "GraphQL request duration in seconds");
        AssertInstrument<Counter<long>>(
            capture, "chat_request_count", "requests", "Number of chat requests");
        AssertInstrument<Histogram<double>>(
            capture, "chat_request_duration_seconds", "seconds", "Chat request duration in seconds");
        AssertInstrument<Counter<long>>(
            capture, "chat_token_count", "tokens", "Number of tokens in chat requests");
        AssertInstrument<Counter<long>>(
            capture, "chat_success_count", "requests", "Number of successful chat requests");
        AssertInstrument<Counter<long>>(
            capture, "chat_failure_count", "requests", "Number of failed chat requests");
    }

    [Fact]
    public void HttpRequestCarriesMethodPathAndStatusTags()
    {
        using var metrics = new ServerMetrics("AxonHub-test-http");
        using var capture = new MetricsCapture(metrics.Meter);

        metrics.RecordHttpRequest("POST", "/v1/chat/completions", statusCode: 200, durationSeconds: 0.25);

        var count = Assert.Single(capture.Counters["http_request_count"]);
        Assert.Equal(1d, count.Value);
        Assert.Equal("POST", (string)count.Tags["method"]!);
        Assert.Equal("/v1/chat/completions", (string)count.Tags["path"]!);
        Assert.Equal(200, (int)count.Tags["status_code"]!);

        var duration = Assert.Single(capture.Histograms["http_request_duration_seconds"]);
        Assert.Equal(0.25, duration.Value, 9);
        Assert.Equal("POST", (string)duration.Tags["method"]!);
        Assert.Equal(200, (int)duration.Tags["status_code"]!);
    }

    [Fact]
    public void GraphqlAndChatHelpersKeepTheReferenceTags()
    {
        using var metrics = new ServerMetrics("AxonHub-test-dead-surface");
        using var capture = new MetricsCapture(metrics.Meter);

        // The reference's GraphQL and chat helpers have no call sites; the
        // port keeps them callable for parity.
        metrics.RecordGraphQLRequest("ViewerQuery", 0.5);

        var graphqlCount = Assert.Single(capture.Counters["graphql_request_count"]);
        Assert.Equal("ViewerQuery", (string)graphqlCount.Tags["operation"]!);
        var graphqlDuration = Assert.Single(capture.Histograms["graphql_request_duration_seconds"]);
        Assert.Equal(0.5, graphqlDuration.Value, 9);

        metrics.RecordChatRequest("42", "kimi", durationSeconds: 1.5, success: true, tokenCount: 7);
        metrics.RecordChatRequest("42", "kimi", durationSeconds: 0.5, success: false, tokenCount: 3);

        var requests = capture.Counters["chat_request_count"];
        Assert.Equal(2, requests.Count);
        Assert.All(requests, point =>
        {
            Assert.Equal("42", (string)point.Tags["channel_id"]!);
            Assert.Equal("kimi", (string)point.Tags["channel_name"]!);
        });

        var tokens = capture.Counters["chat_token_count"];
        Assert.Equal(new[] { 7d, 3d }, tokens.Select(static point => point.Value));
        Assert.Single(capture.Counters["chat_success_count"]);
        Assert.Single(capture.Counters["chat_failure_count"]);
    }

    private static void AssertInstrument<T>(
        MetricsCapture capture,
        string name,
        string unit,
        string description)
        where T : Instrument
    {
        Assert.True(
            capture.Instruments.TryGetValue(name, out var instrument),
            $"instrument {name} was not published");
        Assert.IsAssignableFrom<T>(instrument);
        Assert.Equal(unit, instrument.Unit);
        Assert.Equal(description, instrument.Description);
    }
}
