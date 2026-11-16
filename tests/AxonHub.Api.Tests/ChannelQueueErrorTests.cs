using System.Text;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/orchestrator/channel_queue_error_test.go: the queue
/// admission error joins the typed limiter cause with a synthetic 429 so the
/// inbound transformer renders a rate-limit-shaped response, while carrying no
/// Retry-After header so the cooldown middleware never mistakes it for an
/// upstream rate limit.
/// </summary>
public sealed class ChannelQueueErrorTests
{
    [Fact]
    public void QueueFullWrapsTheSynthetic429()
    {
        var wrapped = ChannelQueueError.From(
            1,
            "kimi",
            new ChannelQueueException(ChannelQueueReason.QueueFull));

        Assert.NotNull(wrapped);
        Assert.Equal("queue_full", wrapped.ReasonText);
        Assert.Equal(ChannelQueueReason.QueueFull, wrapped.Cause.Reason);

        var httpError = Assert.IsType<HttpClientError>(wrapped.InnerException);
        Assert.Equal(429, httpError.StatusCode);
        Assert.True(httpError.Headers is null or { Count: 0 });

        var body = Encoding.UTF8.GetString(httpError.Body);
        Assert.Contains("rate_limit_error", body);
        Assert.Contains("channel_queue_full", body);
        Assert.Contains("kimi", body);
        Assert.Equal(
            "{\"error\":{\"code\":\"channel_queue_full\","
            + "\"message\":\"channel kimi is at capacity (queue_full); please retry shortly\","
            + "\"type\":\"rate_limit_error\"}}",
            body);
    }

    [Fact]
    public void QueueTimeoutWrapsTheSynthetic429()
    {
        var wrapped = ChannelQueueError.From(
            2,
            "openai",
            new ChannelQueueException(ChannelQueueReason.QueueTimeout));

        Assert.NotNull(wrapped);
        Assert.Equal("queue_timeout", wrapped.ReasonText);
        Assert.Equal(ChannelQueueReason.QueueTimeout, wrapped.Cause.Reason);

        var httpError = Assert.IsType<HttpClientError>(wrapped.InnerException);
        Assert.Equal(429, httpError.StatusCode);
        Assert.Contains("channel_queue_timeout", Encoding.UTF8.GetString(httpError.Body));
    }

    [Fact]
    public void NonQueueErrorsPassThrough()
    {
        // Non-queue errors are not wrapped.
        Assert.Null(ChannelQueueError.From(1, "x", new InvalidOperationException("other")));
        Assert.Null(ChannelQueueError.From(1, "x", null));
    }

    [Fact]
    public void QueueErrorLooksLike429ButHasNoRetryAfter()
    {
        var wrapped = ChannelQueueError.From(
            3,
            "anthropic",
            new ChannelQueueException(ChannelQueueReason.QueueFull));
        Assert.NotNull(wrapped);

        // The synthetic 429 makes the status look like a rate limit ...
        Assert.Equal(429, ChannelRetryPredicate.ExtractStatusCode(wrapped));

        // ... but the missing Retry-After keeps the cooldown path off, so a local
        // queue rejection can never cool the channel down.
        Assert.False(HttpClientErrors.HasRetryAfterHeader(wrapped));
    }
}
