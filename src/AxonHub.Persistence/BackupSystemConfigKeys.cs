namespace AxonHub.Persistence;

/// <summary>
/// Port of systemConfigBackupKeys (internal/server/backup/backup_ops.go:104-125):
/// the portable subset of system settings. Deployment-bound keys (JWT secret,
/// data storage IDs and the auto-backup settings themselves) are deliberately
/// excluded. The ordered list mirrors the reference declaration, which is also
/// the order ent renders the <c>KeyIn</c> predicate.
/// </summary>
public static class SystemConfigBackupKeys
{
    public const string BrandName = "system_brand_name";

    public const string BrandLogo = "system_brand_logo";

    public const string Title = "system_title";

    public const string StoreChunks = "requests_store_chunks";

    public const string StoragePolicy = "storage_policy";

    public const string RetryPolicy = "retry_policy";

    public const string WebhookNotifierConfig = "webhook_notifier_config";

    public const string ModelSettings = "system_model_settings";

    public const string ChannelSettings = "system_channel_settings";

    public const string GeneralSettings = "system_general_settings";

    public const string UserAgentPassThrough = "system_user_agent_pass_through";

    public const string PassThrough = "system_pass_through";

    public const string InjectUsageCost = "system_inject_usage_cost";

    public const string QuotaEnforcementSettings = "quota_enforcement_settings";

    public const string QuotaRoutingSettings = "quota_routing_settings";

    public const string SecuritySettings = "security_settings";

    public const string ProxyPresets = "system_proxy_presets";

    /// <summary>The ordered key list of the reference.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        BrandName,
        BrandLogo,
        Title,
        StoreChunks,
        StoragePolicy,
        RetryPolicy,
        WebhookNotifierConfig,
        ModelSettings,
        ChannelSettings,
        GeneralSettings,
        UserAgentPassThrough,
        PassThrough,
        InjectUsageCost,
        QuotaEnforcementSettings,
        QuotaRoutingSettings,
        SecuritySettings,
        ProxyPresets,
    ];

    public static bool Contains(string? key)
        => key is not null && All.Contains(key, StringComparer.Ordinal);
}
