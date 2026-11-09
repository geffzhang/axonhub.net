using System.Text.Json;
using System.Text.Json.Nodes;
using HotChocolate;
using HotChocolate.Types;
using AxonHub.Api.Models;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Security;
using AxonHub.Api.Settings;

namespace AxonHub.Api.GraphQL;

/// <summary>Port of the gqlgen <c>TraceStickyMode</c> enum (system.graphql:132-135).</summary>
[GraphQLName("TraceStickyMode")]
public enum TraceStickyModeValue
{
    [GraphQLName("DISABLED")]
    Disabled,

    [GraphQLName("PREFER_PREVIOUS_CHANNEL")]
    PreferPreviousChannel,
}

/// <summary>Port of the gqlgen <c>APIKeyAutoDisableAction</c> enum (axonhub.graphql:230-235).</summary>
[GraphQLName("APIKeyAutoDisableAction")]
public enum ApiKeyAutoDisableActionValue
{
    [GraphQLName("temporary_disable")]
    TemporaryDisable,

    [GraphQLName("disable_until_cron")]
    DisableUntilCron,

    [GraphQLName("permanent_disable")]
    PermanentDisable,

    [GraphQLName("permanent_disable_delete")]
    PermanentDisableDelete,
}

/// <summary>Port of the gqlgen <c>FilterConditionType</c> enum (filter.graphql:3-6).</summary>
[GraphQLName("FilterConditionType")]
public enum FilterConditionTypeValue
{
    [GraphQLName("condition")]
    Condition,

    [GraphQLName("group")]
    Group,
}

/// <summary>Port of the <c>SystemStatus</c> type (system.graphql:15-17).</summary>
[GraphQLName("SystemStatus")]
public sealed class SystemStatusType
{
    public bool IsInitialized { get; init; }
}

/// <summary>Port of the <c>RetryPolicy</c> type (system.graphql:93-105).</summary>
[GraphQLName("RetryPolicy")]
public sealed class RetryPolicyType
{
    public int MaxChannelRetries { get; init; }

    public int MaxSingleChannelRetries { get; init; }

    public int RetryDelayMs { get; init; }

    public int StreamFirstEventTimeoutSeconds { get; init; }

    public int NonStreamResponseTimeoutSeconds { get; init; }

    public string LoadBalancerStrategy { get; init; } = string.Empty;

    public TraceStickyModeValue TraceStickyMode { get; init; }

    public bool Enabled { get; init; }

    public AutoDisableChannelType AutoDisableChannel { get; init; } = new();

    public bool EmptyResponseDetection { get; init; }

    public UpstreamErrorPolicyType UpstreamErrorPolicy { get; init; } = new();
}

/// <summary>Port of the <c>UpstreamErrorPolicy</c> type.</summary>
[GraphQLName("UpstreamErrorPolicy")]
public sealed class UpstreamErrorPolicyType
{
    public string Mode { get; init; } = string.Empty;

    public string CustomMessage { get; init; } = string.Empty;
}

/// <summary>Port of the <c>AutoDisableChannel</c> type (system.graphql:61-64).</summary>
[GraphQLName("AutoDisableChannel")]
public sealed class AutoDisableChannelType
{
    public bool Enabled { get; init; }

    public IReadOnlyList<ApiKeyAutoDisableRuleType> Rules { get; init; } = [];
}

/// <summary>Port of the <c>APIKeyAutoDisableRule</c> type (axonhub.graphql:243-251).</summary>
[GraphQLName("APIKeyAutoDisableRule")]
public sealed class ApiKeyAutoDisableRuleType
{
    /// <summary>Nullable list: Go keeps nil distinct from an empty slice.</summary>
    public IReadOnlyList<int>? StatusCodes { get; init; }

    /// <summary>Nullable list: Go keeps nil distinct from an empty slice.</summary>
    public IReadOnlyList<string>? KeywordPatterns { get; init; }

    public int Times { get; init; }

    public ApiKeyAutoDisableActionValue Action { get; init; }

    public int? DisableDurationMinutes { get; init; }

    public string? DisableUntilCron { get; init; }

    public string? DisableUntilTimezone { get; init; }
}

/// <summary>Port of the <c>SecuritySettings</c> type (system.graphql:237-240).</summary>
[GraphQLName("SecuritySettings")]
public sealed class SecuritySettingsType
{
    [GraphQLName("blockedIPs")]
    public IReadOnlyList<string> BlockedIps { get; init; } = [];

    [GraphQLName("showRequestLogIPBanIcon")]
    public bool ShowRequestLogIpBanIcon { get; init; }
}

/// <summary>Port of the <c>SystemModelSettings</c> type (system.graphql:259-267).</summary>
[GraphQLName("SystemModelSettings")]
public sealed class SystemModelSettingsType
{
    public bool FallbackToChannelsOnModelNotFound { get; init; }

    public bool QueryAllChannelModels { get; init; }

    [GraphQLName("defaultModelAPIIncludeAll")]
    public bool DefaultModelApiIncludeAll { get; init; }

    public bool AutoReasoningEffort { get; init; }

    public string ModelBlacklistRegex { get; init; } = string.Empty;

    public bool HideUnroutableModelsInList { get; init; }

    public IReadOnlyList<DeveloperModelSettingsType> DeveloperSettings { get; init; } = [];
}

/// <summary>Port of the <c>DeveloperModelSettings</c> type (system.graphql:276-279).</summary>
[GraphQLName("DeveloperModelSettings")]
public sealed class DeveloperModelSettingsType
{
    public string Developer { get; init; } = string.Empty;

    public IReadOnlyList<ModelAssociationType> Associations { get; init; } = [];
}

/// <summary>Port of the <c>ModelAssociation</c> type (model.graphql:46-57).</summary>
[GraphQLName("ModelAssociation")]
public sealed class ModelAssociationType
{
    public string Type { get; init; } = string.Empty;

    public int Priority { get; init; }

    public bool Disabled { get; init; }

    public ModelAssociationWhenType? When { get; init; }

    public ChannelModelAssociationType? ChannelModel { get; init; }

    public ChannelRegexAssociationType? ChannelRegex { get; init; }

    public RegexAssociationType? Regex { get; init; }

    public ModelIdAssociationType? ModelId { get; init; }

    public ChannelTagsModelAssociationType? ChannelTagsModel { get; init; }

    public ChannelTagsRegexAssociationType? ChannelTagsRegex { get; init; }
}

/// <summary>Port of the <c>ModelAssociationWhen</c> type.</summary>
[GraphQLName("ModelAssociationWhen")]
public sealed class ModelAssociationWhenType
{
    public bool Enabled { get; init; }

    public FilterConditionTypeDto? Condition { get; init; }
}

/// <summary>Port of the <c>FilterCondition</c> type (filter.graphql:8-15).</summary>
[GraphQLName("FilterCondition")]
public sealed class FilterConditionTypeDto
{
    public FilterConditionTypeValue Type { get; init; }

    public string? Logic { get; init; }

    public IReadOnlyList<FilterConditionTypeDto>? Conditions { get; init; }

    public string? Field { get; init; }

    public string? Operator { get; init; }

    [GraphQLType(typeof(AnyType))]
    public JsonElement? Value { get; init; }
}

/// <summary>Port of the <c>ChannelModelAssociation</c> type.</summary>
[GraphQLName("ChannelModelAssociation")]
public sealed class ChannelModelAssociationType
{
    public int ChannelId { get; init; }

    public string ModelId { get; init; } = string.Empty;
}

/// <summary>Port of the <c>ChannelRegexAssociation</c> type.</summary>
[GraphQLName("ChannelRegexAssociation")]
public sealed class ChannelRegexAssociationType
{
    public int ChannelId { get; init; }

    public string Pattern { get; init; } = string.Empty;
}

/// <summary>Port of the <c>RegexAssociation</c> type.</summary>
[GraphQLName("RegexAssociation")]
public sealed class RegexAssociationType
{
    public string Pattern { get; init; } = string.Empty;

    public IReadOnlyList<ExcludeAssociationType>? Exclude { get; init; }
}

/// <summary>Port of the <c>ModelIDAssociation</c> type.</summary>
[GraphQLName("ModelIDAssociation")]
public sealed class ModelIdAssociationType
{
    public string ModelId { get; init; } = string.Empty;

    public IReadOnlyList<ExcludeAssociationType>? Exclude { get; init; }
}

/// <summary>Port of the <c>ExcludeAssociation</c> type.</summary>
[GraphQLName("ExcludeAssociation")]
public sealed class ExcludeAssociationType
{
    public string? ChannelNamePattern { get; init; }

    public IReadOnlyList<int>? ChannelIds { get; init; }

    public IReadOnlyList<string>? ChannelTags { get; init; }
}

/// <summary>Port of the <c>ChannelTagsModelAssociation</c> type.</summary>
[GraphQLName("ChannelTagsModelAssociation")]
public sealed class ChannelTagsModelAssociationType
{
    public IReadOnlyList<string> ChannelTags { get; init; } = [];

    public string ModelId { get; init; } = string.Empty;
}

/// <summary>Port of the <c>ChannelTagsRegexAssociation</c> type.</summary>
[GraphQLName("ChannelTagsRegexAssociation")]
public sealed class ChannelTagsRegexAssociationType
{
    public IReadOnlyList<string> ChannelTags { get; init; } = [];

    public string Pattern { get; init; } = string.Empty;
}

/// <summary>Port of <c>UpdateRetryPolicyInput</c> (system.graphql:116-127).</summary>
public sealed class UpdateRetryPolicyInput
{
    public int? MaxChannelRetries { get; set; }

    public int? MaxSingleChannelRetries { get; set; }

    public int? RetryDelayMs { get; set; }

    public int? StreamFirstEventTimeoutSeconds { get; set; }

    public int? NonStreamResponseTimeoutSeconds { get; set; }

    public string? LoadBalancerStrategy { get; set; }

    public TraceStickyModeValue? TraceStickyMode { get; set; }

    public bool? Enabled { get; set; }

    public AutoDisableChannelInput? AutoDisableChannel { get; set; }

    public bool? EmptyResponseDetection { get; set; }

    public UpstreamErrorPolicyInput? UpstreamErrorPolicy { get; set; }
}

/// <summary>Port of <c>UpstreamErrorPolicyInput</c>.</summary>
public sealed class UpstreamErrorPolicyInput
{
    public string? Mode { get; set; }

    public string? CustomMessage { get; set; }
}

/// <summary>Port of <c>AutoDisableChannelInput</c> (system.graphql:109-112).</summary>
public sealed class AutoDisableChannelInput
{
    public bool? Enabled { get; set; }

    public IReadOnlyList<ApiKeyAutoDisableRuleInput>? Rules { get; set; }
}

/// <summary>Port of <c>APIKeyAutoDisableRuleInput</c> (axonhub.graphql:253-261).</summary>
[GraphQLName("APIKeyAutoDisableRuleInput")]
public sealed class ApiKeyAutoDisableRuleInput
{
    public IReadOnlyList<int>? StatusCodes { get; set; }

    public IReadOnlyList<string>? KeywordPatterns { get; set; }

    public int Times { get; set; }

    public ApiKeyAutoDisableActionValue Action { get; set; }

    public int? DisableDurationMinutes { get; set; }

    public string? DisableUntilCron { get; set; }

    public string? DisableUntilTimezone { get; set; }
}

/// <summary>Port of <c>UpdateSystemModelSettingsInput</c> (system.graphql:269-276).</summary>
public sealed class UpdateSystemModelSettingsInput
{
    public bool? FallbackToChannelsOnModelNotFound { get; set; }

    public bool? QueryAllChannelModels { get; set; }

    [GraphQLName("defaultModelAPIIncludeAll")]
    public bool? DefaultModelApiIncludeAll { get; set; }

    public bool? AutoReasoningEffort { get; set; }

    public string? ModelBlacklistRegex { get; set; }

    public bool? HideUnroutableModelsInList { get; set; }

    public IReadOnlyList<DeveloperModelSettingsInput>? DeveloperSettings { get; set; }
}

/// <summary>Port of <c>DeveloperModelSettingsInput</c> (system.graphql:281-284).</summary>
public sealed class DeveloperModelSettingsInput
{
    public string Developer { get; set; } = string.Empty;

    public IReadOnlyList<ModelAssociationInput> Associations { get; set; } = [];
}

/// <summary>Port of <c>ModelAssociationInput</c> (model.graphql:140-151).</summary>
public sealed class ModelAssociationInput
{
    public string Type { get; set; } = string.Empty;

    public int? Priority { get; set; }

    public bool? Disabled { get; set; }

    public ModelAssociationWhenInput? When { get; set; }

    public ChannelModelAssociationInput? ChannelModel { get; set; }

    public ChannelRegexAssociationInput? ChannelRegex { get; set; }

    public RegexAssociationInput? Regex { get; set; }

    public ModelIdAssociationInput? ModelId { get; set; }

    public ChannelTagsModelAssociationInput? ChannelTagsModel { get; set; }

    public ChannelTagsRegexAssociationInput? ChannelTagsRegex { get; set; }
}

/// <summary>Port of <c>ModelAssociationWhenInput</c>.</summary>
public sealed class ModelAssociationWhenInput
{
    public bool? Enabled { get; set; }

    public FilterConditionInput? Condition { get; set; }
}

/// <summary>Port of <c>FilterConditionInput</c> (filter.graphql:17-24).</summary>
public sealed class FilterConditionInput
{
    public FilterConditionTypeValue Type { get; set; }

    public string? Logic { get; set; }

    public IReadOnlyList<FilterConditionInput>? Conditions { get; set; }

    public string? Field { get; set; }

    public string? Operator { get; set; }

    [GraphQLType(typeof(AnyType))]
    public JsonElement? Value { get; set; }
}

/// <summary>Port of <c>ChannelModelAssociationInput</c>.</summary>
public sealed class ChannelModelAssociationInput
{
    public int ChannelId { get; set; }

    public string ModelId { get; set; } = string.Empty;
}

/// <summary>Port of <c>ChannelRegexAssociationInput</c>.</summary>
public sealed class ChannelRegexAssociationInput
{
    public int ChannelId { get; set; }

    public string Pattern { get; set; } = string.Empty;
}

/// <summary>Port of <c>RegexAssociationInput</c>.</summary>
public sealed class RegexAssociationInput
{
    public string Pattern { get; set; } = string.Empty;

    public IReadOnlyList<ExcludeAssociationInput>? Exclude { get; set; }
}

/// <summary>Port of <c>ModelIDAssociationInput</c>.</summary>
[GraphQLName("ModelIDAssociationInput")]
public sealed class ModelIdAssociationInput
{
    public string ModelId { get; set; } = string.Empty;

    public IReadOnlyList<ExcludeAssociationInput>? Exclude { get; set; }
}

/// <summary>Port of <c>ExcludeAssociationInput</c>.</summary>
public sealed class ExcludeAssociationInput
{
    public string? ChannelNamePattern { get; set; }

    public IReadOnlyList<int>? ChannelIds { get; set; }

    public IReadOnlyList<string>? ChannelTags { get; set; }
}

/// <summary>Port of <c>ChannelTagsModelAssociationInput</c>.</summary>
public sealed class ChannelTagsModelAssociationInput
{
    public IReadOnlyList<string> ChannelTags { get; set; } = [];

    public string ModelId { get; set; } = string.Empty;
}

/// <summary>Port of <c>ChannelTagsRegexAssociationInput</c>.</summary>
public sealed class ChannelTagsRegexAssociationInput
{
    public IReadOnlyList<string> ChannelTags { get; set; } = [];

    public string Pattern { get; set; } = string.Empty;
}

/// <summary>Port of <c>UpdateSecuritySettingsInput</c> (system.graphql:242-245).</summary>
public sealed class UpdateSecuritySettingsInput
{
    [GraphQLName("blockedIPs")]
    public IReadOnlyList<string>? BlockedIps { get; set; }

    [GraphQLName("showRequestLogIPBanIcon")]
    public bool? ShowRequestLogIpBanIcon { get; set; }
}

/// <summary>Port of the <c>StoragePolicy</c> type (system.graphql:27-33).</summary>
[GraphQLName("StoragePolicy")]
public sealed class StoragePolicyType
{
    public bool StoreChunks { get; init; }

    public bool LivePreview { get; init; }

    public bool StoreRequestBody { get; init; }

    public bool StoreResponseBody { get; init; }

    public IReadOnlyList<CleanupOptionType> CleanupOptions { get; init; } = [];
}

/// <summary>Port of the <c>CleanupOption</c> type (system.graphql:35-39).</summary>
[GraphQLName("CleanupOption")]
public sealed class CleanupOptionType
{
    public string ResourceType { get; init; } = string.Empty;

    public bool Enabled { get; init; }

    public int CleanupDays { get; init; }
}

/// <summary>
/// Port of <c>UpdateStoragePolicyInput</c> (system.graphql:47-53). The
/// reference decodes it into biz.StoragePolicy: absent fields become the Go
/// zero value, while an absent cleanupOptions stays a nil slice.
/// </summary>
public sealed class UpdateStoragePolicyInput
{
    public bool? StoreChunks { get; set; }

    public bool? LivePreview { get; set; }

    public bool? StoreRequestBody { get; set; }

    public bool? StoreResponseBody { get; set; }

    public IReadOnlyList<CleanupOptionInput>? CleanupOptions { get; set; }
}

/// <summary>Port of <c>CleanupOptionInput</c> (system.graphql:55-59).</summary>
public sealed class CleanupOptionInput
{
    public string ResourceType { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    public int CleanupDays { get; set; }
}

/// <summary>
/// Port of <c>UpdateDefaultDataStorageInput</c> (system.graphql:238-240).
/// </summary>
public sealed class UpdateDefaultDataStorageInput
{
    [GraphQLName("dataStorageID")]
    [GraphQLType(typeof(NonNullType<IdType>))]
    public string DataStorageId { get; set; } = string.Empty;
}

/// <summary>Port of <c>TriggerGcCleanupInput</c> (system.graphql:377-383).</summary>
public sealed class TriggerGcCleanupInput
{
    public int RequestsCleanupDays { get; set; }

    public int UsageLogsCleanupDays { get; set; }

    public int? RequestBodiesCleanupDays { get; set; }

    public int? ResponseBodiesCleanupDays { get; set; }

    public int? ResponseChunksCleanupDays { get; set; }
}

/// <summary>Port of the gqlgen <c>QuotaRoutingMode</c> enum (system.graphql:164-168).</summary>
[GraphQLName("QuotaRoutingMode")]
public enum QuotaRoutingModeValue
{
    [GraphQLName("IGNORE_QUOTA")]
    IgnoreQuota,

    [GraphQLName("REMOVE_ON_EXHAUSTED")]
    RemoveOnExhausted,

    [GraphQLName("BACKPRESSURE")]
    Backpressure,
}

/// <summary>Port of the <c>QuotaRoutingSettings</c> type (system.graphql:170-172).</summary>
[GraphQLName("QuotaRoutingSettings")]
public sealed class QuotaRoutingSettingsType
{
    public QuotaRoutingModeValue DefaultMode { get; init; }
}

/// <summary>Port of <c>UpdateQuotaRoutingSettingsInput</c> (system.graphql:174-176).</summary>
public sealed class UpdateQuotaRoutingSettingsInput
{
    public QuotaRoutingModeValue? DefaultMode { get; set; }
}

/// <summary>
/// Port of the <c>ProviderQuotaCollectionProvider</c> type (system.graphql:178-181).
/// </summary>
[GraphQLName("ProviderQuotaCollectionProvider")]
public sealed class ProviderQuotaCollectionProviderType
{
    public string Provider { get; init; } = string.Empty;

    public bool Enabled { get; init; }
}

/// <summary>
/// Port of the <c>ProviderQuotaCollectionSettings</c> type (system.graphql:183-186).
/// </summary>
[GraphQLName("ProviderQuotaCollectionSettings")]
public sealed class ProviderQuotaCollectionSettingsType
{
    public bool Enabled { get; init; }

    /// <summary>
    /// Always the full supported-provider list in reference order: the field
    /// resolver iterates <c>biz.SupportedProviderQuotaTypes()</c> and reads the
    /// map, so a provider missing from the stored settings reports false.
    /// </summary>
    public IReadOnlyList<ProviderQuotaCollectionProviderType> Providers { get; init; } = [];
}

/// <summary>
/// Port of <c>ProviderQuotaCollectionProviderInput</c> (system.graphql:188-191).
/// </summary>
public sealed class ProviderQuotaCollectionProviderInput
{
    public string Provider { get; set; } = string.Empty;

    public bool Enabled { get; set; }
}

/// <summary>
/// Port of <c>UpdateProviderQuotaCollectionSettingsInput</c>
/// (system.graphql:193-196). Both fields are optional: an absent
/// <c>enabled</c> keeps the stored switch and an absent <c>providers</c> list
/// changes no provider.
/// </summary>
public sealed class UpdateProviderQuotaCollectionSettingsInput
{
    public bool? Enabled { get; set; }

    public IReadOnlyList<ProviderQuotaCollectionProviderInput>? Providers { get; set; }
}

/// <summary>Port of <c>GcCleanupPreviewItem</c> (system.graphql:385-390).</summary>
[GraphQLName("GcCleanupPreviewItem")]
public sealed class GcCleanupPreviewItemType
{
    public string ResourceType { get; init; } = string.Empty;

    public int EstimatedCount { get; init; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime CutoffTime { get; init; }

    public int RetentionDays { get; init; }
}

/// <summary>Port of the gqlgen <c>ProbeFrequency</c> enum (system.graphql:271-276).</summary>
[GraphQLName("ProbeFrequency")]
public enum ProbeFrequencyValue
{
    [GraphQLName("ONE_MINUTE")]
    OneMinute,

    [GraphQLName("FIVE_MINUTES")]
    FiveMinutes,

    [GraphQLName("THIRTY_MINUTES")]
    ThirtyMinutes,

    [GraphQLName("ONE_HOUR")]
    OneHour,
}

/// <summary>Port of the gqlgen <c>AutoSyncFrequency</c> enum (system.graphql:278-282).</summary>
[GraphQLName("AutoSyncFrequency")]
public enum AutoSyncFrequencyValue
{
    [GraphQLName("ONE_HOUR")]
    OneHour,

    [GraphQLName("SIX_HOURS")]
    SixHours,

    [GraphQLName("ONE_DAY")]
    OneDay,
}

/// <summary>Port of the <c>ChannelProbeSetting</c> type (system.graphql:284-287).</summary>
[GraphQLName("ChannelProbeSetting")]
public sealed class ChannelProbeSettingType
{
    public bool Enabled { get; init; }

    public ProbeFrequencyValue Frequency { get; init; }
}

/// <summary>
/// Port of the <c>ChannelModelAutoSyncSetting</c> type (system.graphql:289-291).
/// </summary>
[GraphQLName("ChannelModelAutoSyncSetting")]
public sealed class ChannelModelAutoSyncSettingType
{
    public AutoSyncFrequencyValue Frequency { get; init; }
}

/// <summary>Port of the <c>SystemChannelSettings</c> type (system.graphql:293-298).</summary>
[GraphQLName("SystemChannelSettings")]
public sealed class SystemChannelSettingsType
{
    public ChannelProbeSettingType Probe { get; init; } = new();

    public ChannelModelAutoSyncSettingType AutoSync { get; init; } = new();

    public string TestSystemPrompt { get; init; } = string.Empty;

    public string TestUserPrompt { get; init; } = string.Empty;
}

/// <summary>Port of the <c>SystemGeneralSettings</c> type (system.graphql:316-319).</summary>
[GraphQLName("SystemGeneralSettings")]
public sealed class SystemGeneralSettingsType
{
    public string CurrencyCode { get; init; } = string.Empty;

    public string Timezone { get; init; } = string.Empty;
}

/// <summary>Port of the <c>BrandSettings</c> type (system.graphql:21-25).</summary>
[GraphQLName("BrandSettings")]
public sealed class BrandSettingsType
{
    /// <summary>Nullable in the SDL; the reference always returns a pointer.</summary>
    public string? BrandName { get; init; }

    /// <summary>Nullable in the SDL; the reference always returns a pointer.</summary>
    public string? BrandLogo { get; init; }

    /// <summary>Nullable in the SDL; the reference always returns a pointer.</summary>
    public string? Title { get; init; }
}

/// <summary>
/// Port of the <c>UserAgentPassThroughSettings</c> type (system.graphql:339-341).
/// </summary>
[GraphQLName("UserAgentPassThroughSettings")]
public sealed class UserAgentPassThroughSettingsType
{
    public bool Enabled { get; init; }
}

/// <summary>Port of the <c>PassThroughSettings</c> type (system.graphql:347-349).</summary>
[GraphQLName("PassThroughSettings")]
public sealed class PassThroughSettingsType
{
    public bool Enabled { get; init; }
}

/// <summary>
/// Port of the <c>UsageCostInjectionSettings</c> type (system.graphql:355-357).
/// </summary>
[GraphQLName("UsageCostInjectionSettings")]
public sealed class UsageCostInjectionSettingsType
{
    public bool Enabled { get; init; }
}

/// <summary>Port of <c>UpdateBrandSettingsInput</c> (system.graphql:41-45).</summary>
public sealed class UpdateBrandSettingsInput
{
    public string? BrandName { get; set; }

    public string? BrandLogo { get; set; }

    public string? Title { get; set; }
}

/// <summary>
/// Port of <c>UpdateSystemGeneralSettingsInput</c> (system.graphql:321-324).
/// Absent strings decode to the Go zero value.
/// </summary>
public sealed class UpdateSystemGeneralSettingsInput
{
    public string? CurrencyCode { get; set; }

    public string? Timezone { get; set; }
}

/// <summary>
/// Port of <c>UpdateChannelProbeSettingInput</c> (system.graphql:300-303).
/// </summary>
public sealed class UpdateChannelProbeSettingInput
{
    public bool Enabled { get; set; }

    public ProbeFrequencyValue Frequency { get; set; }
}

/// <summary>
/// Port of <c>UpdateChannelModelAutoSyncSettingInput</c>
/// (system.graphql:305-307).
/// </summary>
public sealed class UpdateChannelModelAutoSyncSettingInput
{
    public AutoSyncFrequencyValue Frequency { get; set; }
}

/// <summary>
/// Port of <c>UpdateSystemChannelSettingsInput</c> (system.graphql:309-314).
/// Every field is optional; omitted fields leave the stored value unchanged.
/// </summary>
public sealed class UpdateSystemChannelSettingsInput
{
    public UpdateChannelProbeSettingInput? Probe { get; set; }

    public UpdateChannelModelAutoSyncSettingInput? AutoSync { get; set; }

    public string? TestSystemPrompt { get; set; }

    public string? TestUserPrompt { get; set; }
}

/// <summary>
/// Port of <c>UpdateUserAgentPassThroughSettingsInput</c>
/// (system.graphql:343-345).
/// </summary>
public sealed class UpdateUserAgentPassThroughSettingsInput
{
    public bool Enabled { get; set; }
}

/// <summary>Port of <c>UpdatePassThroughSettingsInput</c> (system.graphql:351-353).</summary>
public sealed class UpdatePassThroughSettingsInput
{
    public bool Enabled { get; set; }
}

/// <summary>
/// Port of <c>UpdateUsageCostInjectionSettingsInput</c> (system.graphql:359-361).
/// </summary>
public sealed class UpdateUsageCostInjectionSettingsInput
{
    public bool Enabled { get; set; }
}

/// <summary>Domain ↔ GraphQL mapping for the system-settings slice.</summary>
public static class SystemSettingsMapping
{
    public static SystemStatusType ToGraphQL(bool isInitialized)
        => new() { IsInitialized = isInitialized };

    /// <summary>
    /// The GraphQL list is non-null; GetStoragePolicy/SetStoragePolicy always
    /// leave a merged list, so the fallback only guards a hand-built policy.
    /// </summary>
    public static StoragePolicyType ToGraphQL(StoragePolicy policy) => new()
    {
        StoreChunks = policy.StoreChunks,
        LivePreview = policy.LivePreview,
        StoreRequestBody = policy.StoreRequestBody,
        StoreResponseBody = policy.StoreResponseBody,
        CleanupOptions = (policy.CleanupOptions ?? Array.Empty<CleanupOption>())
            .Select(static option => new CleanupOptionType
            {
                ResourceType = option.ResourceType,
                Enabled = option.Enabled,
                CleanupDays = option.CleanupDays,
            })
            .ToList(),
    };

    /// <summary>
    /// gqlgen decodes UpdateStoragePolicyInput straight into biz.StoragePolicy:
    /// an absent boolean is false and an absent cleanupOptions stays nil.
    /// </summary>
    public static StoragePolicy ToDomain(UpdateStoragePolicyInput input) => new()
    {
        StoreChunks = input.StoreChunks ?? false,
        LivePreview = input.LivePreview ?? false,
        StoreRequestBody = input.StoreRequestBody ?? false,
        StoreResponseBody = input.StoreResponseBody ?? false,
        CleanupOptions = input.CleanupOptions?
            .Select(static option => new CleanupOption(
                option.ResourceType,
                option.Enabled,
                option.CleanupDays))
            .ToList(),
    };

    /// <summary>Absent optional day fields decode to the Go zero value.</summary>
    public static GcCleanupRequest ToDomain(TriggerGcCleanupInput input) => new()
    {
        RequestsCleanupDays = input.RequestsCleanupDays,
        UsageLogsCleanupDays = input.UsageLogsCleanupDays,
        RequestBodiesCleanupDays = input.RequestBodiesCleanupDays ?? 0,
        ResponseBodiesCleanupDays = input.ResponseBodiesCleanupDays ?? 0,
        ResponseChunksCleanupDays = input.ResponseChunksCleanupDays ?? 0,
    };

    public static GcCleanupPreviewItemType ToGraphQL(GcCleanupPreviewItem item) => new()
    {
        ResourceType = item.ResourceType,
        EstimatedCount = item.EstimatedCount,
        CutoffTime = item.CutoffTime,
        RetentionDays = item.RetentionDays,
    };

    public static RetryPolicyType ToGraphQL(RetryPolicy policy) => new()
    {
        MaxChannelRetries = policy.MaxChannelRetries,
        MaxSingleChannelRetries = policy.MaxSingleChannelRetries,
        RetryDelayMs = policy.RetryDelayMs,
        StreamFirstEventTimeoutSeconds = policy.StreamFirstEventTimeoutSeconds,
        NonStreamResponseTimeoutSeconds = policy.NonStreamResponseTimeoutSeconds,
        LoadBalancerStrategy = policy.LoadBalancerStrategy,
        TraceStickyMode = ToGraphQL(policy.TraceStickyMode),
        Enabled = policy.Enabled,
        AutoDisableChannel = new AutoDisableChannelType
        {
            Enabled = policy.AutoDisableChannel.Enabled,
            Rules = policy.AutoDisableChannel.Rules.Select(ToGraphQL).ToList(),
        },
        EmptyResponseDetection = policy.EmptyResponseDetection,
        UpstreamErrorPolicy = new UpstreamErrorPolicyType
        {
            Mode = policy.UpstreamErrorPolicy.Mode,
            CustomMessage = policy.UpstreamErrorPolicy.CustomMessage,
        },
    };

    public static ApiKeyAutoDisableRuleType ToGraphQL(AutoDisableRule rule) => new()
    {
        StatusCodes = rule.StatusCodes,
        KeywordPatterns = rule.KeywordPatterns,
        Times = rule.Times,
        Action = ToGraphQLAction(rule.Action),
        DisableDurationMinutes = rule.DisableDurationMinutes,
        DisableUntilCron = rule.DisableUntilCron,
        DisableUntilTimezone = rule.DisableUntilTimezone,
    };

    public static SecuritySettingsType ToGraphQL(SecuritySettings settings) => new()
    {
        BlockedIps = settings.BlockedIps,
        ShowRequestLogIpBanIcon = settings.ShowRequestLogIpBanIcon,
    };

    /// <summary>
    /// The stored wire values are the lowercase objects.QuotaRoutingMode
    /// constants; gqlgen renders them as the uppercase enum names.
    /// </summary>
    public static QuotaRoutingSettingsType ToGraphQL(QuotaRoutingSettings settings) => new()
    {
        DefaultMode = ToGraphQLMode(settings.DefaultMode),
    };

    /// <summary>
    /// The GraphQL enum values decode back to the lowercase wire constants
    /// (objects.QuotaRoutingMode).
    /// </summary>
    public static string ToDomain(QuotaRoutingModeValue mode) => mode switch
    {
        QuotaRoutingModeValue.IgnoreQuota => QuotaRoutingModes.IgnoreQuota,
        QuotaRoutingModeValue.RemoveOnExhausted => QuotaRoutingModes.RemoveOnExhausted,
        QuotaRoutingModeValue.Backpressure => QuotaRoutingModes.Backpressure,
        _ => throw new GraphQLException(
            "Enum \"QuotaRoutingMode\" cannot represent value: "
            + JsonSerializer.Serialize((int)mode)),
    };

    /// <summary>
    /// Port of providerQuotaCollectionSettingsResolver.Providers
    /// (system.resolvers.go:458-467): the list follows
    /// <c>SupportedProviderQuotaTypes()</c> order, and a provider missing from
    /// the stored map reports false.
    /// </summary>
    public static ProviderQuotaCollectionSettingsType ToGraphQL(
        ProviderQuotaCollectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var supported = ProviderQuotaCollectionSettingsService.SupportedProviderQuotaTypes;
        var providers = new List<ProviderQuotaCollectionProviderType>(supported.Count);
        foreach (var providerType in supported)
        {
            providers.Add(new ProviderQuotaCollectionProviderType
            {
                Provider = providerType,
                Enabled = settings.Providers.TryGetValue(providerType, out var enabled)
                    && enabled,
            });
        }

        return new ProviderQuotaCollectionSettingsType
        {
            Enabled = settings.Enabled,
            Providers = providers,
        };
    }

    private static QuotaRoutingModeValue ToGraphQLMode(string mode) => mode switch
    {
        QuotaRoutingModes.IgnoreQuota => QuotaRoutingModeValue.IgnoreQuota,
        QuotaRoutingModes.RemoveOnExhausted => QuotaRoutingModeValue.RemoveOnExhausted,
        QuotaRoutingModes.Backpressure => QuotaRoutingModeValue.Backpressure,
        _ => throw new GraphQLException(
            "Enum \"QuotaRoutingMode\" cannot represent value: "
            + JsonSerializer.Serialize(mode)),
    };

    public static SystemModelSettingsType ToGraphQL(ModelSettings settings) => new()
    {
        FallbackToChannelsOnModelNotFound = settings.FallbackToChannelsOnModelNotFound,
        QueryAllChannelModels = settings.QueryAllChannelModels,
        DefaultModelApiIncludeAll = settings.DefaultModelApiIncludeAll,
        AutoReasoningEffort = settings.AutoReasoningEffort,
        ModelBlacklistRegex = settings.ModelBlacklistRegex,
        HideUnroutableModelsInList = settings.HideUnroutableModelsInList,
        DeveloperSettings = settings.DeveloperSettings
            .Select(static developer => developer is null
                ? throw new GraphQLException(
                    "Cannot return null for non-nullable field "
                    + "SystemModelSettings.developerSettings.")
                : new DeveloperModelSettingsType
                {
                    Developer = developer.Developer,
                    Associations = developer.Associations
                        .Select(static association => association is null
                            ? throw new GraphQLException(
                                "Cannot return null for non-nullable field "
                                + "DeveloperModelSettings.associations.")
                            : ToGraphQL(association))
                        .ToList(),
                })
            .ToList(),
    };

    public static ModelAssociationType ToGraphQL(ModelAssociation association) => new()
    {
        Type = association.Type,
        Priority = association.Priority,
        Disabled = association.Disabled,
        When = association.When is null
            ? null
            : new ModelAssociationWhenType
            {
                Enabled = association.When.Enabled,
                Condition = association.When.Condition is null
                    ? null
                    : ToGraphQL(association.When.Condition),
            },
        ChannelModel = association.ChannelModel is null
            ? null
            : new ChannelModelAssociationType
            {
                ChannelId = association.ChannelModel.ChannelId,
                ModelId = association.ChannelModel.ModelId,
            },
        ChannelRegex = association.ChannelRegex is null
            ? null
            : new ChannelRegexAssociationType
            {
                ChannelId = association.ChannelRegex.ChannelId,
                Pattern = association.ChannelRegex.Pattern,
            },
        Regex = association.Regex is null
            ? null
            : new RegexAssociationType
            {
                Pattern = association.Regex.Pattern,
                Exclude = ToGraphQL(association.Regex.Exclude),
            },
        ModelId = association.ModelId is null
            ? null
            : new ModelIdAssociationType
            {
                ModelId = association.ModelId.ModelId,
                Exclude = ToGraphQL(association.ModelId.Exclude),
            },
        ChannelTagsModel = association.ChannelTagsModel is null
            ? null
            : new ChannelTagsModelAssociationType
            {
                ChannelTags = association.ChannelTagsModel.ChannelTags
                    ?? throw new GraphQLException(
                        "Cannot return null for non-nullable field "
                        + "ChannelTagsModelAssociation.channelTags."),
                ModelId = association.ChannelTagsModel.ModelId,
            },
        ChannelTagsRegex = association.ChannelTagsRegex is null
            ? null
            : new ChannelTagsRegexAssociationType
            {
                ChannelTags = association.ChannelTagsRegex.ChannelTags
                    ?? throw new GraphQLException(
                        "Cannot return null for non-nullable field "
                        + "ChannelTagsRegexAssociation.channelTags."),
                Pattern = association.ChannelTagsRegex.Pattern,
            },
    };

    public static FilterConditionTypeDto ToGraphQL(FilterCondition condition) => new()
    {
        Type = condition.Type is "" or FilterCondition.TypeCondition
            ? FilterConditionTypeValue.Condition
            : condition.Type == FilterCondition.TypeGroup
                ? FilterConditionTypeValue.Group
                : throw new GraphQLException(
                    "Enum \"FilterConditionType\" cannot represent value: "
                    + JsonSerializer.Serialize(condition.Type)),
        Logic = condition.Logic,
        Conditions = condition.Conditions?.Select(ToGraphQL).ToList(),
        Field = condition.Field,
        Operator = condition.Operator,
        Value = condition.Value is null
            ? null
            : JsonSerializer.SerializeToElement(condition.Value),
    };

    public static RetryPolicy ToDomain(UpdateRetryPolicyInput input)
    {
        var channel = input.AutoDisableChannel;

        return new RetryPolicy
        {
            Enabled = input.Enabled ?? false,
            MaxChannelRetries = input.MaxChannelRetries ?? 0,
            MaxSingleChannelRetries = input.MaxSingleChannelRetries ?? 0,
            RetryDelayMs = input.RetryDelayMs ?? 0,
            StreamFirstEventTimeoutSeconds = input.StreamFirstEventTimeoutSeconds ?? 0,
            NonStreamResponseTimeoutSeconds = input.NonStreamResponseTimeoutSeconds ?? 0,
            LoadBalancerStrategy = input.LoadBalancerStrategy ?? string.Empty,
            TraceStickyMode = input.TraceStickyMode is { } sticky ? ToDomain(sticky) : string.Empty,
            AutoDisableChannel = new AutoDisableChannel(
                channel?.Enabled ?? false,
                channel?.Rules?.Select(ToDomain).ToList() ?? []),
            EmptyResponseDetection = input.EmptyResponseDetection ?? false,
            UpstreamErrorPolicy = new UpstreamErrorPolicy(
                input.UpstreamErrorPolicy?.Mode ?? string.Empty,
                input.UpstreamErrorPolicy?.CustomMessage ?? string.Empty),
        };
    }

    public static AutoDisableRule ToDomain(ApiKeyAutoDisableRuleInput input)
        => new(
            input.StatusCodes?.ToList(),
            input.KeywordPatterns?.ToList(),
            input.Times,
            ToDomain(input.Action),
            input.DisableDurationMinutes,
            input.DisableUntilCron ?? string.Empty,
            input.DisableUntilTimezone ?? string.Empty);

    public static ModelSettings ToDomain(UpdateSystemModelSettingsInput input)
        => new()
        {
            FallbackToChannelsOnModelNotFound = input.FallbackToChannelsOnModelNotFound ?? false,
            QueryAllChannelModels = input.QueryAllChannelModels ?? false,
            DefaultModelApiIncludeAll = input.DefaultModelApiIncludeAll ?? false,
            AutoReasoningEffort = input.AutoReasoningEffort ?? false,
            ModelBlacklistRegex = input.ModelBlacklistRegex ?? string.Empty,
            HideUnroutableModelsInList = input.HideUnroutableModelsInList ?? false,
            DeveloperSettings = input.DeveloperSettings is null
                ? []
                : input.DeveloperSettings
                    .Select(static developer => (DeveloperModelSettings?)new DeveloperModelSettings(
                        developer.Developer,
                        developer.Associations.Select(ToDomain).ToList()))
                    .ToList(),
        };

    public static ModelAssociation ToDomain(ModelAssociationInput input) => new()
    {
        Type = input.Type,
        Priority = input.Priority ?? 0,
        Disabled = input.Disabled ?? false,
        When = input.When is null
            ? null
            : new ModelAssociationWhen(
                input.When.Enabled ?? false,
                input.When.Condition is null ? null : ToDomain(input.When.Condition)),
        ChannelModel = input.ChannelModel is null
            ? null
            : new ChannelModelAssociation(
                input.ChannelModel.ChannelId,
                input.ChannelModel.ModelId),
        ChannelRegex = input.ChannelRegex is null
            ? null
            : new ChannelRegexAssociation(
                input.ChannelRegex.ChannelId,
                input.ChannelRegex.Pattern),
        Regex = input.Regex is null
            ? null
            : new RegexAssociation(
                input.Regex.Pattern,
                ToDomain(input.Regex.Exclude)),
        ModelId = input.ModelId is null
            ? null
            : new ModelIdAssociation(
                input.ModelId.ModelId,
                ToDomain(input.ModelId.Exclude)),
        ChannelTagsModel = input.ChannelTagsModel is null
            ? null
            : new ChannelTagsModelAssociation(
                input.ChannelTagsModel.ChannelTags,
                input.ChannelTagsModel.ModelId),
        ChannelTagsRegex = input.ChannelTagsRegex is null
            ? null
            : new ChannelTagsRegexAssociation(
                input.ChannelTagsRegex.ChannelTags,
                input.ChannelTagsRegex.Pattern),
    };

    public static FilterCondition ToDomain(FilterConditionInput input)
        => new()
        {
            Type = input.Type == FilterConditionTypeValue.Group
                ? FilterCondition.TypeGroup
                : FilterCondition.TypeCondition,
            Logic = input.Logic ?? string.Empty,
            Conditions = input.Conditions?
                .Select(static child => ToDomain(child ?? throw new GraphQLException(
                    "Cannot return null for non-nullable field "
                    + "FilterConditionInput.conditions.")))
                .ToList(),
            Field = input.Field ?? string.Empty,
            Operator = input.Operator ?? string.Empty,
            Value = ModelSettingsCodec.NormalizeValue(input.Value),
        };

    private static List<ExcludeAssociation?>? ToDomain(
        IReadOnlyList<ExcludeAssociationInput>? excludes)
        => excludes?
            .Select(static exclude => (ExcludeAssociation?)new ExcludeAssociation(
                exclude.ChannelNamePattern ?? string.Empty,
                exclude.ChannelIds?.ToList(),
                exclude.ChannelTags?.ToList()))
            .ToList();

    private static TraceStickyModeValue ToGraphQL(string mode) => mode switch
    {
        RetryPolicy.TraceStickyDisabled => TraceStickyModeValue.Disabled,
        _ => TraceStickyModeValue.PreferPreviousChannel,
    };

    private static string ToDomain(TraceStickyModeValue mode) => mode switch
    {
        TraceStickyModeValue.Disabled => RetryPolicy.TraceStickyDisabled,
        _ => RetryPolicy.TraceStickyPreferPreviousChannel,
    };

    private static ApiKeyAutoDisableActionValue ToGraphQLAction(string action) => action switch
    {
        RetryPolicy.AutoDisableActionTemporary => ApiKeyAutoDisableActionValue.TemporaryDisable,
        RetryPolicy.AutoDisableActionUntilCron => ApiKeyAutoDisableActionValue.DisableUntilCron,
        RetryPolicy.AutoDisableActionPermanentDelete
            => ApiKeyAutoDisableActionValue.PermanentDisableDelete,
        _ => ApiKeyAutoDisableActionValue.PermanentDisable,
    };

    private static string ToDomain(ApiKeyAutoDisableActionValue action) => action switch
    {
        ApiKeyAutoDisableActionValue.TemporaryDisable => RetryPolicy.AutoDisableActionTemporary,
        ApiKeyAutoDisableActionValue.DisableUntilCron => RetryPolicy.AutoDisableActionUntilCron,
        ApiKeyAutoDisableActionValue.PermanentDisableDelete
            => RetryPolicy.AutoDisableActionPermanentDelete,
        _ => RetryPolicy.AutoDisableActionPermanent,
    };

    private static List<ExcludeAssociationType>? ToGraphQL(
        IReadOnlyList<ExcludeAssociation?>? excludes)
        => excludes?
            .Select(static exclude => exclude is null
                ? throw new GraphQLException(
                    "Cannot return null for non-nullable field ExcludeAssociation.")
                : new ExcludeAssociationType
                {
                    ChannelNamePattern = exclude.ChannelNamePattern,
                    ChannelIds = exclude.ChannelIds,
                    ChannelTags = exclude.ChannelTags,
                })
            .ToList();

    public static BrandSettingsType ToGraphQL(BrandSettings settings) => new()
    {
        BrandName = settings.BrandName,
        BrandLogo = settings.BrandLogo,
        Title = settings.Title,
    };

    public static SystemGeneralSettingsType ToGraphQL(SystemGeneralSettings settings) => new()
    {
        CurrencyCode = settings.CurrencyCode,
        Timezone = settings.Timezone,
    };

    public static SystemChannelSettingsType ToGraphQL(SystemChannelSettings settings) => new()
    {
        Probe = new ChannelProbeSettingType
        {
            Enabled = settings.Probe.Enabled,
            Frequency = ToGraphQLProbeFrequency(settings.Probe.Frequency),
        },
        AutoSync = new ChannelModelAutoSyncSettingType
        {
            Frequency = ToGraphQLAutoSyncFrequency(settings.AutoSync.Frequency),
        },
        TestSystemPrompt = settings.TestSystemPrompt,
        TestUserPrompt = settings.TestUserPrompt,
    };

    /// <summary>
    /// gqlgen decodes UpdateSystemGeneralSettingsInput straight into
    /// biz.SystemGeneralSettings: absent strings become "".
    /// </summary>
    public static SystemGeneralSettings ToDomain(UpdateSystemGeneralSettingsInput input)
        => new()
        {
            CurrencyCode = input.CurrencyCode ?? string.Empty,
            Timezone = input.Timezone ?? string.Empty,
        };

    public static SystemChannelSettingsPatch ToDomain(UpdateSystemChannelSettingsInput input)
        => new()
        {
            Probe = input.Probe is null
                ? null
                : new ChannelProbeSetting
                {
                    Enabled = input.Probe.Enabled,
                    Frequency = ToDomain(input.Probe.Frequency),
                },
            AutoSync = input.AutoSync is null
                ? null
                : new ChannelModelAutoSyncSetting
                {
                    Frequency = ToDomain(input.AutoSync.Frequency),
                },
            TestSystemPrompt = input.TestSystemPrompt,
            TestUserPrompt = input.TestUserPrompt,
        };

    /// <summary>
    /// Port of ProbeFrequency.MarshalGQL (system.go:626-644): an unknown stored
    /// value renders as ONE_MINUTE.
    /// </summary>
    private static ProbeFrequencyValue ToGraphQLProbeFrequency(string frequency)
        => frequency switch
        {
            ProbeFrequencies.OneMinute => ProbeFrequencyValue.OneMinute,
            ProbeFrequencies.FiveMinutes => ProbeFrequencyValue.FiveMinutes,
            ProbeFrequencies.ThirtyMinutes => ProbeFrequencyValue.ThirtyMinutes,
            ProbeFrequencies.OneHour => ProbeFrequencyValue.OneHour,
            _ => ProbeFrequencyValue.OneMinute,
        };

    private static string ToDomain(ProbeFrequencyValue frequency) => frequency switch
    {
        ProbeFrequencyValue.OneMinute => ProbeFrequencies.OneMinute,
        ProbeFrequencyValue.FiveMinutes => ProbeFrequencies.FiveMinutes,
        ProbeFrequencyValue.ThirtyMinutes => ProbeFrequencies.ThirtyMinutes,
        _ => ProbeFrequencies.OneHour,
    };

    /// <summary>
    /// Port of AutoSyncFrequency.MarshalGQL (system.go:515-530): an unknown
    /// stored value renders as ONE_HOUR.
    /// </summary>
    private static AutoSyncFrequencyValue ToGraphQLAutoSyncFrequency(string frequency)
        => frequency switch
        {
            AutoSyncFrequencies.OneHour => AutoSyncFrequencyValue.OneHour,
            AutoSyncFrequencies.SixHours => AutoSyncFrequencyValue.SixHours,
            AutoSyncFrequencies.OneDay => AutoSyncFrequencyValue.OneDay,
            _ => AutoSyncFrequencyValue.OneHour,
        };

    private static string ToDomain(AutoSyncFrequencyValue frequency) => frequency switch
    {
        AutoSyncFrequencyValue.OneHour => AutoSyncFrequencies.OneHour,
        AutoSyncFrequencyValue.SixHours => AutoSyncFrequencies.SixHours,
        _ => AutoSyncFrequencies.OneDay,
    };
}
