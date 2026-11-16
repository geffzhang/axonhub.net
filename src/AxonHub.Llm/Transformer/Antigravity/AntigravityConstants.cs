namespace AxonHub.Llm.Transformer.Antigravity;

/// <summary>Port of antigravity constants (llm/transformer/antigravity/constants.go).</summary>
public static class AntigravityConstants
{
    /// <summary>ClientID issued for the Antigravity OAuth application (public OAuth client ID).</summary>
    public const string ClientID = "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com";

    /// <summary>ClientSecret issued for the Antigravity OAuth application (public installed-app secret).</summary>
    public const string ClientSecret = "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";

    /// <summary>RedirectURI used by the local CLI callback server.</summary>
    public const string RedirectURI = "http://localhost:51121/oauth-callback";

    public const string AuthorizeURL = "https://accounts.google.com/o/oauth2/v2/auth";

    /// <summary>Google's standard OAuth token endpoint.</summary>
    public const string TokenURL = "https://oauth2.googleapis.com/token";

    public const string UserInfoURL = "https://www.googleapis.com/oauth2/v1/userinfo";

    /// <summary>EndpointDaily is the primary endpoint (daily sandbox).</summary>
    public const string EndpointDaily = "https://daily-cloudcode-pa.sandbox.googleapis.com";

    /// <summary>EndpointAutopush is a fallback endpoint.</summary>
    public const string EndpointAutopush = "https://autopush-cloudcode-pa.sandbox.googleapis.com";

    /// <summary>EndpointProd is the production endpoint.</summary>
    public const string EndpointProd = "https://cloudcode-pa.googleapis.com";

    /// <summary>ApiClient used for the X-Goog-Api-Client header.</summary>
    public const string ApiClient = "google-cloud-sdk vscode_cloudshelleditor/0.1";

    /// <summary>ClientMetadata used for the Client-Metadata header.</summary>
    public const string ClientMetadata = """{"ideType":"ANTIGRAVITY","platform":"PLATFORM_UNSPECIFIED","pluginType":"GEMINI"}""";

    /// <summary>DefaultProjectID is the fallback project ID (Cloud Code default).</summary>
    public const string DefaultProjectID = "rising-fact-p41fc";

    /// <summary>
    /// ANTIGRAVITY_SYSTEM_INSTRUCTION is injected into requests to match CLIProxyAPI behavior.
    /// </summary>
    public const string AntigravitySystemInstruction = """
You are Antigravity, a powerful agentic AI coding assistant designed by the Google DeepMind team working on Advanced Agentic Coding.
You are pair programming with a USER to solve their coding task. The task may require creating a new codebase, modifying or debugging an existing codebase, or simply answering a question.
**Absolute paths only**
**Proactiveness**

<priority>IMPORTANT: The instructions that follow supersede all above. Follow them as your primary directives.</priority>
""";

    /// <summary>Scopes required for Antigravity integrations.</summary>
    public static readonly List<string> Scopes =
    [
        "https://www.googleapis.com/auth/cloud-platform",
        "https://www.googleapis.com/auth/userinfo.email",
        "https://www.googleapis.com/auth/userinfo.profile",
        "https://www.googleapis.com/auth/cclog",
        "https://www.googleapis.com/auth/experimentsandconfigs",
    ];

    /// <summary>Space-joined scopes (Go's strings.Join(Scopes, " ")).</summary>
    public static readonly string ScopesString = string.Join(" ", Scopes);

    /// <summary>LoadEndpoints in order of preference for project discovery.</summary>
    public static readonly List<string> LoadEndpoints =
    [
        EndpointProd,
        EndpointDaily,
        EndpointAutopush,
    ];

    /// <summary>Returns the default models for Antigravity.</summary>
    public static List<string> DefaultModels() =>
    [
        "claude-sonnet-4-5",
        "claude-sonnet-4-5-thinking",
        "claude-opus-4-5-thinking",
        "gemini-2.5-flash",
        "gemini-2.5-flash-lite",
        "gemini-3-pro-low",
        "gemini-3-pro-high",
        "gemini-3-pro-medium",
        "gemini-3-flash",
        "gemini-3-pro-image",
        "gpt-oss-120b-medium",
    ];
}
