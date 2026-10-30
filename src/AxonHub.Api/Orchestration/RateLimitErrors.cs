using System.Text;
using AxonHub.Grains.Abstractions;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of <c>orchestrator.ChannelQueueError</c>
/// (internal/server/orchestrator/channel_queue_error.go): a channel-level
/// admission failure that joins the typed limiter cause with a synthetic 429 so
/// the inbound transformer can render it.
/// </summary>
/// <remarks>
/// No Retry-After header is attached: the reference relies on its absence so the
/// rate-limit tracking middleware never mistakes a local rejection for an
/// upstream 429 and cools the channel down.
/// </remarks>
internal sealed class ChannelQueueError : Exception
{
    private ChannelQueueError(
        long channelId,
        string channelName,
        ChannelQueueException cause,
        HttpClientError httpError)
        : base(
            FormattableString.Invariant(
                $"channel {GoTextTemplate.GoQuote(channelName)} (id={channelId}) {cause.ReasonText}"),
            httpError)
    {
        ChannelId = channelId;
        ChannelName = channelName;
        Cause = cause;
        ReasonText = cause.ReasonText;
    }

    public long ChannelId { get; }

    public string ChannelName { get; }

    /// <summary>
    /// The typed limiter sentinel. Go's <c>Unwrap</c> returns both the cause and
    /// the synthetic transport error; .NET has a single
    /// <see cref="Exception.InnerException"/>, so the transport error occupies it
    /// (the inbound transformer looks it up there) and the cause is exposed here.
    /// </summary>
    public ChannelQueueException Cause { get; }

    /// <summary><c>queue_full</c> or <c>queue_timeout</c>.</summary>
    public string ReasonText { get; }

    /// <summary>
    /// Wraps a limiter sentinel error with channel context. Returns
    /// <see langword="null"/> when the cause is not a queue admission error
    /// (caller cancellation passes through unchanged).
    /// </summary>
    public static ChannelQueueError? From(long channelId, string? channelName, Exception? cause)
    {
        if (cause is null || channelName is null || GoErrors.As<ChannelQueueException>(cause) is not { } queue)
        {
            return null;
        }

        var reason = queue.ReasonText;
        var message = FormattableString.Invariant(
            $"channel {channelName} is at capacity ({reason}); please retry shortly");

        // Go: json.Marshal(map[string]any{"error": map[string]any{...}}) — map
        // keys are sorted, so the inner object is exactly code, message, type,
        // and strings use json.Marshal's HTML-safe escaping.
        var body = Encoding.UTF8.GetBytes(
            "{\"error\":{\"code\":" + GoJson.Quote("channel_" + reason)
            + ",\"message\":" + GoJson.Quote(message)
            + ",\"type\":" + GoJson.Quote("rate_limit_error") + "}}");

        var httpError = new HttpClientError(string.Empty, string.Empty, 429, "Too Many Requests", body);

        return new ChannelQueueError(channelId, channelName, queue, httpError);
    }
}

/// <summary>
/// Port of <c>orchestrator.LocalRPMExhaustedError</c>
/// (internal/server/orchestrator/rate_limit_admission_error.go): a local
/// per-channel RPM admission failure. The embedded synthetic 429 lets the
/// inbound transformer build a rate-limit-shaped client error while the typed
/// wrapper lets retry and cooldown code tell it apart from a provider 429.
/// </summary>
internal sealed class LocalRpmExhaustedError : Exception
{
    private LocalRpmExhaustedError(long channelId, string channelName, long limit, HttpClientError httpError)
        : base(
            FormattableString.Invariant(
                $"channel {GoTextTemplate.GoQuote(channelName)} (id={channelId}) local rpm exhausted"),
            httpError)
    {
        ChannelId = channelId;
        ChannelName = channelName;
        Limit = limit;
    }

    public long ChannelId { get; }

    public string ChannelName { get; }

    public long Limit { get; }

    public static LocalRpmExhaustedError Create(long channelId, string channelName, long limit)
    {
        var message = FormattableString.Invariant(
            $"channel {channelName} exhausted local RPM limit {limit}; please retry shortly");

        // Go: fmt.Appendf with a literal prefix and %q for the message.
        var body = Encoding.UTF8.GetBytes(
            "{\"error\":{\"code\":\"channel_rpm_exhausted\",\"message\":"
            + GoTextTemplate.GoQuote(message)
            + ",\"type\":\"rate_limit_error\"}}");

        var httpError = new HttpClientError(string.Empty, string.Empty, 429, "Too Many Requests", body);

        return new LocalRpmExhaustedError(channelId, channelName, limit, httpError);
    }
}
