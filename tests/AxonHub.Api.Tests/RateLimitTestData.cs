using System.Text;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Builders for the rate-limit middleware tests: a single-candidate outbound
/// transformer (the reference's <c>newTestOutbound</c>/<c>makeChannel</c>) and
/// synthetic transport errors.
/// </summary>
internal static class RateLimitTestData
{
    public static readonly DateTimeOffset Start = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>An outbound transformer whose current candidate is the given channel.</summary>
    public static PersistentOutboundTransformer Outbound(
        ChannelRateLimitSettings? settings,
        long channelId = 1,
        string channelName = "test-channel")
    {
        var candidates = new[]
        {
            new ChannelCandidate
            {
                ChannelId = channelId,
                ChannelName = channelName,
                ActualModel = "test-model",
                RateLimitSettings = settings,
            },
        };

        return new PersistentOutboundTransformer(
            candidates,
            TimeProvider.System,
            new RecordingMetricsRecorder());
    }

    /// <summary>An outbound transformer with no candidate at all (the reference's empty state).</summary>
    public static PersistentOutboundTransformer EmptyOutbound()
        => new([], TimeProvider.System, new RecordingMetricsRecorder());

    /// <summary>
    /// A synthetic transport error. <paramref name="retryAfter"/> is attached as
    /// the <c>Retry-After</c> header when non-null; no header at all otherwise.
    /// </summary>
    public static HttpClientError HttpError(int statusCode, string? retryAfter, string body = "{}")
    {
        var error = new HttpClientError(
            "POST",
            "http://upstream.test/v1/chat/completions",
            statusCode,
            statusCode + " status",
            Encoding.UTF8.GetBytes(body));

        if (retryAfter is not null)
        {
            error.Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Retry-After"] = [retryAfter],
            };
        }

        return error;
    }

    public static ChannelRateLimitSettings Rpm(long rpm)
        => new() { Rpm = rpm };

    public static ChannelRateLimitSettings Concurrency(long maxConcurrent, long? queueSize = null, long? queueTimeoutMs = null)
        => new()
        {
            MaxConcurrent = maxConcurrent,
            QueueSize = queueSize,
            QueueTimeoutMs = queueTimeoutMs,
        };
}
