using Microsoft.Extensions.Configuration;

namespace AxonHub.Api.Http;

/// <summary>
/// Port of internal/tracing.Config (tracing.go:12-52) and the
/// <c>server.trace.*</c> defaults (conf/conf.go:227-234). An instance is built
/// once at startup and shared by the logging and trace middlewares.
/// </summary>
/// <remarks>
/// The reference leaves the three header names empty when the config loader did
/// not fill them and falls back inside the middleware (<c>traceHeaderName</c>,
/// <c>WithLoggingTracing</c>). The port keeps the same zero-value semantics:
/// <see cref="TraceHeaderName"/>, <see cref="RequestHeaderName"/> and
/// <see cref="ThreadHeaderName"/> apply the defaults.
/// </remarks>
public sealed record TracingOptions
{
    public const string DefaultThreadHeader = "AH-Thread-Id";

    public const string DefaultTraceHeader = "AH-Trace-Id";

    public const string DefaultRequestHeader = "AH-Request-Id";

    /// <summary>Header name for the thread id. Defaults to AH-Thread-Id.</summary>
    public string ThreadHeader { get; init; } = string.Empty;

    /// <summary>Header name for the trace id. Defaults to AH-Trace-Id.</summary>
    public string TraceHeader { get; init; } = string.Empty;

    /// <summary>Header name for the request id. Defaults to AH-Request-Id.</summary>
    public string RequestHeader { get; init; } = string.Empty;

    /// <summary>Extra header names tried after the primary trace header.</summary>
    public IReadOnlyList<string> ExtraTraceHeaders { get; init; } = [];

    /// <summary>Response headers that echo the resolved trace id.</summary>
    public IReadOnlyList<string> ResponseTraceHeaders { get; init; } = [];

    /// <summary>Body field names tried when no trace header or client marker resolves an id.</summary>
    public IReadOnlyList<string> ExtraTraceBodyFields { get; init; } = [];

    /// <summary>Extract trace ids from Claude Code metadata. Defaults to true.</summary>
    public bool ClaudeCodeTraceEnabled { get; init; } = true;

    /// <summary>Extract trace ids from Codex session headers. Defaults to true.</summary>
    public bool CodexTraceEnabled { get; init; } = true;

    /// <summary>Extract trace ids from OpenCode session headers. Defaults to true.</summary>
    public bool OpenCodeTraceEnabled { get; init; } = true;

    public string ThreadHeaderName
        => string.IsNullOrEmpty(ThreadHeader) ? DefaultThreadHeader : ThreadHeader;

    public string TraceHeaderName
        => string.IsNullOrEmpty(TraceHeader) ? DefaultTraceHeader : TraceHeader;

    public string RequestHeaderName
        => string.IsNullOrEmpty(RequestHeader) ? DefaultRequestHeader : RequestHeader;

    public static TracingOptions Default { get; } = new();

    /// <summary>
    /// Binds <c>Server:Trace</c>. The reference's conf defaults are reproduced
    /// explicitly because the .NET binder would otherwise use false/empty.
    /// </summary>
    public static TracingOptions FromConfiguration(IConfigurationSection section)
    {
        ArgumentNullException.ThrowIfNull(section);

        return new TracingOptions
        {
            ThreadHeader = section["ThreadHeader"] ?? string.Empty,
            TraceHeader = section["TraceHeader"] ?? string.Empty,
            RequestHeader = section["RequestHeader"] ?? string.Empty,
            ExtraTraceHeaders = section.GetSection("ExtraTraceHeaders").Get<string[]>() ?? [],
            ResponseTraceHeaders = section.GetSection("ResponseTraceHeaders").Get<string[]>() ?? [],
            ExtraTraceBodyFields = section.GetSection("ExtraTraceBodyFields").Get<string[]>() ?? [],
            ClaudeCodeTraceEnabled = section.GetValue<bool?>("ClaudeCodeTraceEnabled") ?? true,
            CodexTraceEnabled = section.GetValue<bool?>("CodexTraceEnabled") ?? true,
            OpenCodeTraceEnabled = section.GetValue<bool?>("OpenCodeTraceEnabled") ?? true,
        };
    }
}
