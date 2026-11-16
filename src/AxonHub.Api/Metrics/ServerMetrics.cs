using System.Diagnostics.Metrics;

namespace AxonHub.Api.Metrics;

/// <summary>
/// Port of <c>metrics._Metrics</c> (internal/metrics/metrics.go): the nine
/// process instruments registered by <c>metrics.SetupMetrics</c> on the meter
/// named after <c>server.name</c> (default "AxonHub").
/// </summary>
/// <remarks>
/// <para>
/// Names, units and descriptions are copied verbatim; the reference's meter is
/// an OpenTelemetry meter and the port uses System.Diagnostics.Metrics, the
/// surface OpenTelemetry .NET collects. Only <see cref="RecordHttpRequest"/> is
/// wired by the reference's middleware; the GraphQL and chat helpers have no
/// call sites upstream and are ported as the same unreachable surface.
/// </para>
/// <para>
/// The meter is owned by this instance: it is created in the constructor and
/// disposed with the singleton at shutdown, which lets the host's OpenTelemetry
/// provider flush the final export (difference ledger C223).
/// </para>
/// </remarks>
public sealed class ServerMetrics : IDisposable
{
    private readonly Meter _meter;
    private readonly Counter<long> _httpRequestCount;
    private readonly Histogram<double> _httpRequestDuration;
    private readonly Counter<long> _graphqlRequestCount;
    private readonly Histogram<double> _graphqlRequestDuration;
    private readonly Counter<long> _chatRequestCount;
    private readonly Histogram<double> _chatRequestDuration;
    private readonly Counter<long> _chatTokenCount;
    private readonly Counter<long> _chatSuccessCount;
    private readonly Counter<long> _chatFailureCount;

    /// <summary>Registers the nine instruments on a new meter.</summary>
    public ServerMetrics(string meterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(meterName);

        _meter = new Meter(meterName);

        _httpRequestCount = _meter.CreateCounter<long>(
            "http_request_count",
            unit: "requests",
            description: "Number of HTTP requests");
        _httpRequestDuration = _meter.CreateHistogram<double>(
            "http_request_duration_seconds",
            unit: "seconds",
            description: "HTTP request duration in seconds");

        _graphqlRequestCount = _meter.CreateCounter<long>(
            "graphql_request_count",
            unit: "requests",
            description: "Number of GraphQL requests");
        _graphqlRequestDuration = _meter.CreateHistogram<double>(
            "graphql_request_duration_seconds",
            unit: "seconds",
            description: "GraphQL request duration in seconds");

        _chatRequestCount = _meter.CreateCounter<long>(
            "chat_request_count",
            unit: "requests",
            description: "Number of chat requests");
        _chatRequestDuration = _meter.CreateHistogram<double>(
            "chat_request_duration_seconds",
            unit: "seconds",
            description: "Chat request duration in seconds");
        _chatTokenCount = _meter.CreateCounter<long>(
            "chat_token_count",
            unit: "tokens",
            description: "Number of tokens in chat requests");
        _chatSuccessCount = _meter.CreateCounter<long>(
            "chat_success_count",
            unit: "requests",
            description: "Number of successful chat requests");
        _chatFailureCount = _meter.CreateCounter<long>(
            "chat_failure_count",
            unit: "requests",
            description: "Number of failed chat requests");
    }

    /// <summary>The meter the instruments live on (the reference's provider.Meter(name)).</summary>
    internal Meter Meter => _meter;

    /// <summary>Port of <c>_Metrics.RecordHTTPRequest</c>.</summary>
    public void RecordHttpRequest(string method, string path, int statusCode, double durationSeconds)
    {
        KeyValuePair<string, object?>[] tags =
        [
            new("method", method),
            new("path", path),
            new("status_code", statusCode),
        ];

        _httpRequestCount.Add(1, tags);
        _httpRequestDuration.Record(durationSeconds, tags);
    }

    /// <summary>Port of <c>_Metrics.RecordGraphQLRequest</c> (no upstream call sites).</summary>
    public void RecordGraphQLRequest(string operation, double durationSeconds)
    {
        KeyValuePair<string, object?>[] tags = [new("operation", operation)];

        _graphqlRequestCount.Add(1, tags);
        _graphqlRequestDuration.Record(durationSeconds, tags);
    }

    /// <summary>Port of <c>_Metrics.RecordChatRequest</c> (no upstream call sites).</summary>
    public void RecordChatRequest(
        string channelId,
        string channelName,
        double durationSeconds,
        bool success,
        long tokenCount)
    {
        KeyValuePair<string, object?>[] tags =
        [
            new("channel_id", channelId),
            new("channel_name", channelName),
        ];

        _chatRequestCount.Add(1, tags);
        _chatRequestDuration.Record(durationSeconds, tags);
        _chatTokenCount.Add(tokenCount, tags);

        if (success)
        {
            _chatSuccessCount.Add(1, tags);
        }
        else
        {
            _chatFailureCount.Add(1, tags);
        }
    }

    public void Dispose() => _meter.Dispose();
}
