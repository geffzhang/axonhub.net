namespace AxonHub.Llm.Transformer.Xai.Subscription;

/// <summary>Port of llm/transformer/xai/subscription/constants.go.</summary>
public static class SubscriptionConstants
{
    public const string AuthorizeUrl = "https://auth.x.ai/oauth2/authorize";
    public const string TokenUrl = "https://auth.x.ai/oauth2/token";
    public const string ClientId = "b1a00492-073a-47ea-816f-4c329264a828";
    public const string RedirectUri = "http://127.0.0.1:56121/callback";
    public const string Scopes = "openid profile email offline_access grok-cli:access api:access";
    public const string SsoScopes = Scopes + " conversations:read conversations:write";

    public const string DefaultBaseUrl = "https://cli-chat-proxy.grok.com/v1";
    public const string BillingWeeklyUrl = DefaultBaseUrl + "/billing?format=credits";
    public const string BillingMonthlyUrl = DefaultBaseUrl + "/billing";

    public const string CliClientVersion = "0.2.114";
    public const string CliClientVersionHeader = "x-grok-client-version";
    public const string CliClientIdentifier = "grok-shell";
    public const string CliClientIdentifierHeader = "x-grok-client-identifier";
    public const string CliTokenAuth = "xai-grok-cli";
    public const string CliTokenAuthHeader = "x-xai-token-auth";
    public const string CliUserAgent = "xai-grok-workspace/" + CliClientVersion;

    public const string SsoAccountsUrl = "https://accounts.x.ai/";
    public const string SsoDeviceUrl = "https://auth.x.ai/oauth2/device/code";
    public const string SsoVerifyUrl = "https://auth.x.ai/oauth2/device/verify";
    public const string SsoApproveUrl = "https://auth.x.ai/oauth2/device/approve";

    /// <summary>Port of <c>DefaultModels</c>.</summary>
    public static IReadOnlyList<string> DefaultModels() =>
    [
        "grok-4.6",
        "grok-4.5",
        "grok-4.3",
        "grok-3-mini",
        "grok-3-mini-fast",
        "grok-build-0.1",
        "grok-composer-2.5-fast",
        "grok-4.20-0309-reasoning",
        "grok-4.20-0309-non-reasoning",
        "grok-4.20-multi-agent-0309",
    ];
}
