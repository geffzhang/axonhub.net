namespace AxonHub.Llm.Transformer.Anthropic.ClaudeCode;

/// <summary>Port of llm/transformer/anthropic/claudecode (constants.go, outbound.go).</summary>
public static class ClaudeCodeConstants
{
    /// <summary>Go: <c>claudeCodeSystemMessage</c>.</summary>
    public const string SystemMessage = "You are Claude Code, Anthropic's official CLI for Claude.";

    /// <summary>Go: <c>toolPrefix</c>.</summary>
    public const string ToolPrefix = "proxy_";

    public const string AuthorizeUrl = "https://claude.ai/oauth/authorize";

    public const string TokenUrl = "https://api.anthropic.com/v1/oauth/token";

    public const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    public const string RedirectUri = "http://localhost:54545/callback";

    public const string Scopes = "org:create_api_key user:profile user:inference";

    /// <summary>Kept consistent with the Claude CLI.</summary>
    public const string UserAgent = "claude-cli/2.1.170 (external, cli)";

    /// <summary>Beta feature identifiers for the Claude Code API.</summary>
    public const string BetaHeader =
        "claude-code-20250219,context-1m-2025-08-07,interleaved-thinking-2025-05-14,"
        + "redact-thinking-2026-02-12,context-management-2025-06-27,"
        + "prompt-caching-scope-2026-01-05,mid-conversation-system-2026-04-07,effort-2025-11-24";

    public const string VersionHeader = "2023-06-01";

    /// <summary>Enables browser access capabilities.</summary>
    public const string BrowserAccessHeader = "true";

    /// <summary>Identifies the client application type.</summary>
    public const string AppHeader = "cli";

    /// <summary>The model used for quota checking requests.</summary>
    public const string QuotaCheckModel = "claude-haiku-4-5";

    public const string QuotaCheckHeader =
        "claude-code-20250219,interleaved-thinking-2025-05-14,redact-thinking-2026-02-12,"
        + "context-management-2025-06-27,prompt-caching-scope-2026-01-05,"
        + "mid-conversation-system-2026-04-07,effort-2025-11-24";

    /// <summary>
    /// Go: <c>claudeCodeHeaders</c> — every header set on Claude Code requests,
    /// in the reference's order.
    /// </summary>
    public static readonly (string Name, string Value)[] Headers =
    [
        ("Anthropic-Beta", BetaHeader),
        ("Anthropic-Version", VersionHeader),
        ("Anthropic-Dangerous-Direct-Browser-Access", BrowserAccessHeader),
        ("X-App", AppHeader),
        ("X-Stainless-Helper-Method", "stream"),
        ("X-Stainless-Retry-Count", "0"),
        ("X-Stainless-Runtime-Version", "v24.3.0"),
        ("X-Stainless-Package-Version", "0.94.0"),
        ("X-Stainless-Runtime", "node"),
        ("X-Stainless-Lang", "js"),
        ("X-Stainless-Arch", "arm64"),
        ("X-Stainless-Os", "MacOS"),
        ("X-Stainless-Timeout", "600"),
    ];

    /// <summary>
    /// Go: <c>PassthroughHeaders</c> — inbound headers forwarded upstream.
    /// Inbound values override the defaults above.
    /// </summary>
    public static readonly string[] PassthroughHeaders =
    [
        .. Headers.Select(static header => header.Name),
        "X-Claude-Code-Session-Id",
    ];

    /// <summary>Port of <c>DefaultModels</c>: a static list of Claude Code-capable model IDs.</summary>
    public static string[] DefaultModels() =>
    [
        "claude-haiku-4-5-20251001",
        "claude-sonnet-4-5-20250929",
        "claude-opus-4-5-20251101",
        "claude-opus-4-6",
        "claude-sonnet-4-6",
        "claude-opus-4-7",
        "claude-opus-4-8",
    ];
}
