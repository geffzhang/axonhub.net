namespace AxonHub.Llm.Transformer.OpenAI.Codex;

/// <summary>Port of llm/transformer/openai/codex/constants.go.</summary>
public static class CodexConstants
{
    /// <summary>Go: <c>defaultImageMainModel</c>.</summary>
    public const string DefaultImageMainModel = "gpt-6-luna";

    public const string AxonHubOriginator = "axonhub";

    public const string AuthorizeUrl = "https://auth.openai.com/oauth/authorize";

    public const string TokenUrl = "https://auth.openai.com/oauth/token";

    public const string ClientId = "app_EMoamEEZ73f0CkXaXp7hrann";

    public const string RedirectUri = "http://localhost:1455/auth/callback";

    public const string Scopes = "openid profile email offline_access";

    public const string DefaultVersion = "0.159.0";

    /// <summary>
    /// Go: <c>codexDefaultUserAgent</c>. The transformer owns the outbound UA;
    /// client-UA forwarding belongs solely to the orchestrator pass-through
    /// middleware and overwrites this value when the switch is enabled.
    /// </summary>
    public const string DefaultUserAgent = "codex_cli_rs/" + DefaultVersion;

    /// <summary>
    /// Go: <c>fabricatedBetaFeatures</c> — mirrors the X-Codex-Beta-Features
    /// value the current Codex CLI sends, used when a non-Codex inbound client
    /// omits the header.
    /// </summary>
    public const string FabricatedBetaFeatures = "remote_compaction_v2";

    /// <summary>
    /// Port of <c>DefaultModels</c>: a static list of Codex-capable model IDs.
    /// The ChatGPT Codex backend does not provide a stable public /models
    /// endpoint; CLIProxyAPI keeps a local registry, and AxonHub mirrors that
    /// approach to power "Fetch Models". A fresh array is returned on every
    /// call, matching the Go function's slice semantics.
    /// </summary>
    public static string[] DefaultModels() =>
    [
        "gpt-5.6-sol",
        "gpt-5.6-terra",
        "gpt-5.6-luna",
        "gpt-6-astra",
        "gpt-6-sol",
        "gpt-6-luna",
        "gpt-6.1-sol",
        "codex-auto-review",
    ];
}
