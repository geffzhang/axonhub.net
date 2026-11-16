using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Http;

/// <summary>Downstream heartbeat payload selected by the API surface.</summary>
public enum SseHeartbeatFormat
{
    /// <summary>api/chat.go:702: <c>: keep-alive\n\n</c>.</summary>
    OpenAi,

    /// <summary>api/chat.go:704: <c>event: ping\ndata: {"type":"ping"}\n\n</c>.</summary>
    Anthropic,
}

/// <summary>
/// Port of api.SSEKeepAliveConfig (internal/server/api/chat.go:34-37) with the
/// reference defaults from conf/conf.go:245-246: disabled, 15 second interval.
/// </summary>
public sealed record SseKeepAliveSettings(bool Enabled, TimeSpan Interval)
{
    /// <summary>conf/conf.go:246: server.sse_keep_alive.interval defaults to "15s".</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(15);

    public static SseKeepAliveSettings Default { get; } = new(false, DefaultInterval);

    /// <summary>
    /// Reads <c>server.sse_keep_alive.*</c> from the <c>Server:SseKeepAlive</c>
    /// section first, then the reference's <c>AXONHUB_SERVER_SSE_KEEP_ALIVE_*</c>
    /// environment spelling, matching the other ported config surfaces.
    /// </summary>
    public static SseKeepAliveSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var enabledValue = configuration["Server:SseKeepAlive:Enabled"]
            ?? configuration["AXONHUB_SERVER_SSE_KEEP_ALIVE_ENABLED"];
        var enabled = bool.TryParse(enabledValue, out var parsed) ? parsed : false;

        var intervalValue = configuration["Server:SseKeepAlive:Interval"]
            ?? configuration["AXONHUB_SERVER_SSE_KEEP_ALIVE_INTERVAL"];
        var interval = string.IsNullOrEmpty(intervalValue)
            ? DefaultInterval
            : GoDurationParser.Parse(intervalValue);

        return new SseKeepAliveSettings(enabled, interval);
    }
}
