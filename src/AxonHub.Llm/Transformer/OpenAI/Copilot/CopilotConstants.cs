namespace AxonHub.Llm.Transformer.OpenAI.Copilot;

/// <summary>
/// Port of llm/transformer/openai/copilot/outbound.go:28-49 (default base URL,
/// endpoint and the LiteLLM-style editor headers) and constants.go.
/// </summary>
public static class CopilotConstants
{
    public const string DefaultCopilotBaseUrl = "https://api.githubcopilot.com";

    public const string CopilotChatCompletionsEndpoint = "/chat/completions";

    public const string EditorVersionHeader = "Editor-Version";

    public const string EditorPluginVersionHeader = "Editor-Plugin-Version";

    public const string UserAgentHeader = "User-Agent";

    public const string OpenAiIntentHeader = "Openai-Intent";

    public const string CopilotIntegrationIdHeader = "Copilot-Integration-Id";

    public const string GitHubApiVersionHeader = "X-Github-Api-Version";

    public const string RequestIdHeader = "X-Request-Id";

    public const string VSCodeUserAgentLibHeader = "X-Vscode-User-Agent-Library-Version";

    public const string CopilotVisionRequestHeader = "Copilot-Vision-Request";

    public const string InitiatorHeader = "X-Initiator";

    /// <summary>The LiteLLM-style default editor version for Copilot quota tracking.</summary>
    public const string DefaultEditorVersion = "vscode/1.95.0";

    public const string DefaultEditorPluginVersion = "copilot-chat/0.26.7";

    public const string DefaultUserAgent = "GitHubCopilotChat/0.26.7";

    public const string DefaultOpenAiIntent = "conversation-edits";

    public const string DefaultCopilotIntegrationId = "vscode-chat";

    public const string DefaultGitHubApiVersion = "2025-04-01";

    public const string DefaultVSCodeUserAgentLib = "electron-fetch";

    /// <summary>
    /// Provider configuration URL (constants.go): model listings for GitHub
    /// Copilot in the PublicProviderConf.
    /// </summary>
    public const string ProviderConfUrl =
        "https://raw.githubusercontent.com/ThinkInAIXYZ/PublicProviderConf/dev/dist/github-copilot.json";

    /// <summary>The provider identifier in the PublicProviderConf.</summary>
    public const string ProviderId = "github-copilot";

    /// <summary>Port of defaultCopilotTokenEndpoint (token_exchanger.go:22).</summary>
    public const string DefaultCopilotTokenEndpoint = "https://api.github.com/copilot_internal/v2/token";

    /// <summary>Port of tokenExpiryBuffer (token_exchanger.go:23).</summary>
    public static readonly TimeSpan TokenExpiryBuffer = TimeSpan.FromMinutes(5);

    /// <summary>Port of tokenExchangeTimeout (token_exchanger.go:24).</summary>
    public static readonly TimeSpan TokenExchangeTimeout = TimeSpan.FromSeconds(30);

    // Device-flow configuration (token_provider.go:31-37): GitHub's device
    // authorization endpoints, the Copilot OAuth client id and its scope.
    public const string DeviceAuthUrl = "https://github.com/login/device/code";

    public const string OAuthTokenUrl = "https://github.com/login/oauth/access_token";

    public const string OAuthClientId = "Iv1.b507a08c87ecfe98";

    public static IReadOnlyList<string> DeviceFlowScopes => ["read:user"];
}
