using System.Text.Json.Serialization;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Argument object for <see cref="ChannelLifecycleService.CreateAsync"/>: the
/// ent.CreateChannelInput of biz.ChannelService.createChannel
/// (channel.go:547-662) with the live settings/credentials extensions (C139).
/// </summary>
/// <remarks>
/// <c>orderingWeight</c> is deliberately absent: CreateChannel never calls
/// <c>SetOrderingWeight</c>, so ent's schema default (0) is stored even when the
/// client sends a value (ent.graphql:1617-1645; C139).
/// </remarks>
public sealed class CreateChannelCommand
{
    public string Type { get; init; } = string.Empty;

    /// <summary>Mutated by the xai_subscription rule (channel.go:556-560).</summary>
    public string? BaseUrl { get; set; }

    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Mutated by DuplicateChannel's zenmux management-key inheritance
    /// (channel_duplicate.go:30-32), so the initializer is settable.
    /// </summary>
    public ChannelCredentialsDraft Credentials { get; set; } = new();

    public IReadOnlyList<string> SupportedModels { get; init; } = [];

    public IReadOnlyList<string>? ManualModels { get; init; }

    public bool? AutoSyncSupportedModels { get; init; }

    public string? AutoSyncModelPattern { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    public string DefaultTestModel { get; init; } = string.Empty;

    /// <summary>Mutated by NormalizeAPIKeyAutoDisableRules (channel.go:731-757).</summary>
    public ChannelPoliciesDraft? Policies { get; set; }

    /// <summary>
    /// Mutated by the command-code cookie canonicalization and the settings
    /// normalizers (channel.go:531-600). A null settings pointer stores JSON
    /// <c>null</c> and suppresses ent's <c>{"modelMappings":[]}</c> default.
    /// </summary>
    public ChannelSettingsDraft? Settings { get; set; }

    public string? Remark { get; init; }

    /// <summary>Cleared by the xai_subscription rule (channel.go:556-560).</summary>
    public IReadOnlyList<ChannelEndpointDraft>? Endpoints { get; set; }
}

/// <summary>
/// Argument object for <see cref="ChannelLifecycleService.UpdateAsync"/>: the
/// ent.UpdateChannelInput of biz.ChannelService.UpdateChannel
/// (channel.go:849-1203). Every nullable member mirrors a Go pointer input, so
/// null means "leave the column untouched"; list members keep nil distinct from
/// an explicit empty slice. Fields the service never writes (status,
/// clearBaseURL, clear/append model/tag/policy/settings/endpoint, errorMessage)
/// are deliberately absent.
/// </summary>
public sealed class UpdateChannelCommand
{
    public string? Type { get; init; }

    /// <summary>Mutated by the xai_subscription rule (channel.go:896-909).</summary>
    public string? BaseUrl { get; set; }

    public string? Name { get; init; }

    /// <summary>Mutated by ValidateVertexApiKeys' zenmux key preservation.</summary>
    public ChannelCredentialsDraft? Credentials { get; init; }

    public IReadOnlyList<string>? SupportedModels { get; init; }

    public IReadOnlyList<string>? ManualModels { get; init; }

    public bool? AutoSyncSupportedModels { get; init; }

    public string? AutoSyncModelPattern { get; init; }

    public bool ClearAutoSyncModelPattern { get; init; }

    public IReadOnlyList<string>? Tags { get; init; }

    public string? DefaultTestModel { get; init; }

    /// <summary>Mutated by NormalizeAPIKeyAutoDisableRules (channel.go:731-757).</summary>
    public ChannelPoliciesDraft? Policies { get; init; }

    /// <summary>
    /// Mutated by the command-code cookie normalization, the stored-settings
    /// sync and the settings validators. An assigned value is always written.
    /// </summary>
    public ChannelSettingsDraft? Settings { get; set; }

    public int? OrderingWeight { get; init; }

    public string? Remark { get; init; }

    public bool ClearRemark { get; init; }

    public IReadOnlyList<ChannelEndpointDraft>? Endpoints { get; set; }

    public bool ClearErrorMessage { get; init; }
}

/// <summary>
/// Argument object for <see cref="ChannelLifecycleService.BulkUpdateAutoDisableAsync"/>:
/// the biz.BulkUpdateChannelAutoDisableInput of channel_bulk.go:235-240. The
/// action keeps its GraphQL wire value (<c>write_rules</c>/<c>inherit</c>/<c>off</c>)
/// because the service reproduces the reference's string switch, including its
/// unsupported-action branch.
/// </summary>
public sealed class BulkUpdateAutoDisableCommand
{
    public IReadOnlyList<long> ChannelIds { get; init; } = [];

    public string Action { get; init; } = string.Empty;

    /// <summary>
    /// Raw submitted rules; only <c>write_rules</c> reads them. They are
    /// normalized by normalizeAutoDisableRules with allowDelete=true before
    /// reaching the store.
    /// </summary>
    public IReadOnlyList<ApiKeyAutoDisableRuleDraft>? Rules { get; init; }
}

/// <summary>
/// Argument object for <see cref="ChannelLifecycleService.SaveEndpointsAsync"/>:
/// biz.SaveChannelEndpointsInput (channel.go:434-437). The endpoint list is the
/// decoder's non-nil slice; an explicitly empty GraphQL list stays empty.
/// </summary>
public sealed class SaveChannelEndpointsCommand
{
    public long ChannelId { get; init; }

    public IReadOnlyList<ChannelEndpointDraft> Endpoints { get; init; } = [];
}

/// <summary>
/// Argument object for <see cref="ChannelLifecycleService.BulkCreateChannelsAsync"/>:
/// biz.BulkCreateChannelsInput (channel_bulk.go:49-63). <c>orderingWeight</c> is
/// carried by the input but ignored, because createChannel never calls
/// <c>SetOrderingWeight</c> (C139).
/// </summary>
public sealed class BulkCreateChannelsCommand
{
    public string Type { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public IReadOnlyList<string>? Tags { get; init; }

    public string? BaseUrl { get; init; }

    public IReadOnlyList<string> ApiKeys { get; init; } = [];

    public IReadOnlyList<string> SupportedModels { get; init; } = [];

    public bool? AutoSyncSupportedModels { get; init; }

    public string DefaultTestModel { get; init; } = string.Empty;

    /// <summary>
    /// Shared by reference across every channel: createChannel mutates the
    /// pointer, so later channels observe the earlier normalization.
    /// </summary>
    public ChannelPoliciesDraft? Policies { get; init; }

    /// <summary>Shared by reference across every channel (see Policies).</summary>
    public ChannelSettingsDraft? Settings { get; init; }

    public int? OrderingWeight { get; init; }

    public string? Remark { get; init; }
}

/// <summary>
/// Port of biz.BulkImportChannelItem (channel_bulk.go:464-472). The two URL/key
/// members are nullable because GraphQL marks them nullable; the service treats
/// null and empty identically.
/// </summary>
public sealed class BulkImportChannelItem
{
    public string Type { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string? BaseUrl { get; init; }

    public string? ApiKey { get; init; }

    public IReadOnlyList<string> SupportedModels { get; init; } = [];

    public string DefaultTestModel { get; init; } = string.Empty;
}

/// <summary>
/// Port of biz.BulkImportChannelsResult (channel_bulk.go:474-481). Errors stays
/// null when every row succeeds (Go's nil slice marshals as GraphQL null);
/// Channels is the successfully created rows in input order.
/// </summary>
public sealed record BulkImportChannelsResult(
    bool Success,
    int Created,
    int Failed,
    IReadOnlyList<string>? Errors,
    IReadOnlyList<ChannelRecord> Channels);

/// <summary>
/// Port of biz.DeleteDisabledAPIKeysResult (channel_apikey.go:280-284). Message
/// is empty on success and <c>ONE_KEY_PRESERVED</c> when the last credential had
/// to be restored.
/// </summary>
public sealed record DeleteDisabledApiKeysResult(bool Success, string Message);

/// <summary>
/// Port of objects.ChannelSettings (live channel.go:155-288) with Go's JSON
/// tags, declaration order and <c>omitempty</c> behaviour. Nullable members are
/// omitted by <c>ChannelWriteSerialization</c>; non-nullable members are always
/// emitted, mirroring Go's struct marshalling.
/// </summary>
public sealed class ChannelSettingsDraft
{
    [JsonPropertyName("extraModelPrefix")]
    public string ExtraModelPrefix { get; set; } = string.Empty;

    [JsonPropertyName("autoTrimedModelPrefixes")]
    public IReadOnlyList<string>? AutoTrimedModelPrefixes { get; set; }

    [JsonPropertyName("modelMappings")]
    public IReadOnlyList<ModelMappingDraft>? ModelMappings { get; set; }

    [JsonPropertyName("hideOriginalModels")]
    public bool HideOriginalModels { get; set; }

    [JsonPropertyName("hideMappedModels")]
    public bool HideMappedModels { get; set; }

    [JsonPropertyName("lowercaseModelId")]
    public bool LowercaseModelId { get; set; }

    [JsonPropertyName("overrideParameters")]
    public string OverrideParameters { get; set; } = string.Empty;

    [JsonPropertyName("bodyOverrideOperations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OverrideOperationDraft>? BodyOverrideOperations { get; set; }

    [JsonPropertyName("overrideHeaders")]
    public IReadOnlyList<HeaderEntryDraft>? OverrideHeaders { get; set; }

    [JsonPropertyName("headerOverrideOperations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OverrideOperationDraft>? HeaderOverrideOperations { get; set; }

    [JsonPropertyName("proxy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProxyConfigDraft? Proxy { get; set; }

    [JsonPropertyName("transformOptions")]
    public TransformOptionsDraft TransformOptions { get; set; } = new();

    [JsonPropertyName("passThroughUserAgent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PassThroughUserAgent { get; set; }

    [JsonPropertyName("passThroughBody")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? PassThroughBody { get; set; }

    [JsonPropertyName("rateLimit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChannelRateLimitDraft? RateLimit { get; set; }

    [JsonPropertyName("retryableStatusCodes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? RetryableStatusCodes { get; set; }

    [JsonPropertyName("retryableErrorPatterns")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<RetryableErrorPatternDraft>? RetryableErrorPatterns { get; set; }

    [JsonPropertyName("maxSingleChannelRetries")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxSingleChannelRetries { get; set; }

    [JsonPropertyName("responseTimeout")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChannelResponseTimeoutDraft? ResponseTimeout { get; set; }

    [JsonPropertyName("modelProtocols")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ModelProtocolDraft>? ModelProtocols { get; set; }

    [JsonPropertyName("apiKeyStrategy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKeyStrategy { get; set; }

    [JsonPropertyName("apiKeyRoundRobinSwitchAfter")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ApiKeyRoundRobinSwitchAfter { get; set; }

    [JsonPropertyName("providerQuota")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ChannelProviderQuotaSettingsDraft? ProviderQuota { get; set; }

    [JsonPropertyName("quotaRoutingMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? QuotaRoutingMode { get; set; }
}

/// <summary>Port of objects.ModelMapping (channel.go:35-41).</summary>
public sealed class ModelMappingDraft
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;
}

/// <summary>Port of objects.HeaderEntry (channel.go:43-46).</summary>
public sealed class HeaderEntryDraft
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

/// <summary>Port of objects.OverrideOperation (channel.go:82-107).</summary>
public sealed class OverrideOperationDraft
{
    [JsonPropertyName("op")]
    public string Op { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    [JsonPropertyName("from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? From { get; set; }

    [JsonPropertyName("to")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? To { get; set; }

    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Value { get; set; }

    [JsonPropertyName("condition")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Condition { get; set; }

    [JsonPropertyName("match")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OverrideMatchDraft? Match { get; set; }

    [JsonPropertyName("index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Index { get; set; }

    [JsonPropertyName("splat")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Splat { get; set; }
}

/// <summary>Port of objects.OverrideMatch (channel.go:72-79).</summary>
public sealed class OverrideMatchDraft
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("eq")]
    public string Eq { get; set; } = string.Empty;
}

/// <summary>Port of objects.TransformOptions (channel.go:116-137).</summary>
public sealed class TransformOptionsDraft
{
    [JsonPropertyName("forceArrayInstructions")]
    public bool ForceArrayInstructions { get; set; }

    [JsonPropertyName("forceArrayInputs")]
    public bool ForceArrayInputs { get; set; }

    [JsonPropertyName("replaceDeveloperRoleWithSystem")]
    public bool ReplaceDeveloperRoleWithSystem { get; set; }

    [JsonPropertyName("reasoningEffortMapping")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ReasoningEffortMappingDraft>? ReasoningEffortMapping { get; set; }
}

/// <summary>Port of llm.ReasoningEffortMapping (reasoning.go).</summary>
public sealed class ReasoningEffortMappingDraft
{
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;
}

/// <summary>Port of httpclient.ProxyConfig (proxy.go:11-17).</summary>
public sealed class ProxyConfigDraft
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("username")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Password { get; set; }

    [JsonPropertyName("disableConnectionReuse")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? DisableConnectionReuse { get; set; }
}

/// <summary>Port of objects.ChannelRateLimit (channel.go:271-289).</summary>
public sealed class ChannelRateLimitDraft
{
    [JsonPropertyName("rpm")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Rpm { get; set; }

    [JsonPropertyName("tpm")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Tpm { get; set; }

    [JsonPropertyName("maxConcurrent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? MaxConcurrent { get; set; }

    [JsonPropertyName("queueSize")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? QueueSize { get; set; }

    [JsonPropertyName("queueTimeoutMs")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? QueueTimeoutMs { get; set; }
}

/// <summary>Port of objects.RetryableErrorPattern (channel.go:263-266).</summary>
public sealed class RetryableErrorPatternDraft
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;

    [JsonPropertyName("regex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Regex { get; set; }
}

/// <summary>Port of objects.ModelProtocol (channel.go:144-152).</summary>
public sealed class ModelProtocolDraft
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("apiFormats")]
    public IReadOnlyList<string> ApiFormats { get; set; } = [];

    [JsonPropertyName("enabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Enabled { get; set; }
}

/// <summary>Port of objects.ChannelResponseTimeoutSettings (live channel.go:423-437).</summary>
public sealed class ChannelResponseTimeoutDraft
{
    [JsonPropertyName("mode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Mode { get; set; }

    [JsonPropertyName("streamFirstEventTimeoutSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? StreamFirstEventTimeoutSeconds { get; set; }

    [JsonPropertyName("nonStreamResponseTimeoutSeconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? NonStreamResponseTimeoutSeconds { get; set; }
}

/// <summary>Port of objects.ChannelProviderQuotaSettings (channel.go:206-213).</summary>
public sealed class ChannelProviderQuotaSettingsDraft
{
    [JsonPropertyName("commandCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CommandCodeQuotaSettingsDraft? CommandCode { get; set; }

    [JsonPropertyName("ollama")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OllamaQuotaSettingsDraft? Ollama { get; set; }
}

/// <summary>Port of objects.CommandCodeQuotaSettings (channel.go:225-227).</summary>
public sealed class CommandCodeQuotaSettingsDraft
{
    [JsonPropertyName("authCookie")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthCookie { get; set; }
}

/// <summary>Port of objects.OllamaQuotaSettings (channel.go:240-242).</summary>
public sealed class OllamaQuotaSettingsDraft
{
    [JsonPropertyName("authCookie")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AuthCookie { get; set; }
}

/// <summary>Port of objects.ChannelCredentials (live channel.go:475-501).</summary>
public sealed class ChannelCredentialsDraft
{
    [JsonPropertyName("apiKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKey { get; set; }

    [JsonPropertyName("oauth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OAuthCredentialsDraft? OAuth { get; set; }

    [JsonPropertyName("apiKeys")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ApiKeys { get; set; }

    [JsonPropertyName("vertexApiKeys")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<VertexApiKeyDraft>? VertexApiKeys { get; set; }

    [JsonPropertyName("managementApiKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ManagementApiKey { get; set; }

    [JsonPropertyName("azure")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AzureCredentialDraft? Azure { get; set; }

    [JsonPropertyName("gcp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GcpCredentialDraft? Gcp { get; set; }
}

/// <summary>Port of objects.AzureCredential (channel.go:478-481).</summary>
public sealed class AzureCredentialDraft
{
    [JsonPropertyName("apiVersion")]
    public string ApiVersion { get; set; } = string.Empty;
}

/// <summary>Port of oauth.OAuthCredentials (credentials.go:10-18).</summary>
public sealed class OAuthCredentialsDraft
{
    [JsonPropertyName("client_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ClientId { get; set; }

    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("id_token")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? IdToken { get; set; }

    /// <summary>Go time.Time: a zero value serializes as 0001-01-01T00:00:00Z.</summary>
    [JsonPropertyName("expires_at")]
    public string ExpiresAt { get; set; } = "0001-01-01T00:00:00Z";

    [JsonPropertyName("token_type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TokenType { get; set; }

    [JsonPropertyName("scopes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Scopes { get; set; }
}

/// <summary>Port of objects.VertexAPIKey (live channel.go:504-508).</summary>
public sealed class VertexApiKeyDraft
{
    [JsonPropertyName("projectID")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("apiKey")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;
}

/// <summary>Port of objects.GCPCredential (channel.go:483-487).</summary>
public sealed class GcpCredentialDraft
{
    [JsonPropertyName("region")]
    public string Region { get; set; } = string.Empty;

    [JsonPropertyName("projectID")]
    public string ProjectId { get; set; } = string.Empty;

    [JsonPropertyName("jsonData")]
    public string JsonData { get; set; } = string.Empty;
}

/// <summary>Port of objects.ChannelPolicies (channel.go:543-555).</summary>
public sealed class ChannelPoliciesDraft
{
    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Stream { get; set; }

    [JsonPropertyName("apiKeyAutoDisableMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKeyAutoDisableMode { get; set; }

    [JsonPropertyName("apiKeyAutoDisableRules")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ApiKeyAutoDisableRuleDraft>? ApiKeyAutoDisableRules { get; set; }
}

/// <summary>Port of objects.APIKeyAutoDisableRule (channel.go:605-620).</summary>
public sealed class ApiKeyAutoDisableRuleDraft
{
    [JsonPropertyName("statusCodes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<int>? StatusCodes { get; set; }

    [JsonPropertyName("keywordPatterns")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? KeywordPatterns { get; set; }

    [JsonPropertyName("times")]
    public int Times { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("disableDurationMinutes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? DisableDurationMinutes { get; set; }

    [JsonPropertyName("disableUntilCron")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisableUntilCron { get; set; }

    [JsonPropertyName("disableUntilTimezone")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisableUntilTimezone { get; set; }
}

/// <summary>Port of objects.ChannelEndpoint (channel.go:20-25).</summary>
public sealed class ChannelEndpointDraft
{
    [JsonPropertyName("api_format")]
    public string ApiFormat { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; set; }

    [JsonPropertyName("base_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("transport")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Transport { get; set; }
}
