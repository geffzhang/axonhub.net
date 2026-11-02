using HotChocolate;
using HotChocolate.Types;

namespace AxonHub.Api.GraphQL;

/// <summary>
/// Port of <c>CreateChannelInput</c> (ent.graphql:1616-1644). The frozen
/// reference input is extended with the live settings/credentials fields the
/// migrated management frontend sends (C139/C144).
/// </summary>
public sealed class CreateChannelInput
{
    public ChannelTypeValue Type { get; set; }

    [GraphQLName("baseURL")]
    public string? BaseUrl { get; set; }

    public string Name { get; set; } = string.Empty;

    public ChannelCredentialsInput Credentials { get; set; } = new();

    public IReadOnlyList<string> SupportedModels { get; set; } = [];

    public IReadOnlyList<string>? ManualModels { get; set; }

    public bool? AutoSyncSupportedModels { get; set; }

    public string? AutoSyncModelPattern { get; set; }

    public IReadOnlyList<string>? Tags { get; set; }

    public string DefaultTestModel { get; set; } = string.Empty;

    public ChannelPoliciesInput? Policies { get; set; }

    public ChannelSettingsInput? Settings { get; set; }

    public int? OrderingWeight { get; set; }

    public string? Remark { get; set; }

    public IReadOnlyList<ChannelEndpointInput>? Endpoints { get; set; }
}

/// <summary>
/// Port of <c>UpdateChannelInput</c> (ent.graphql:6185-6237). Fields the write
/// service never reads (status, clearBaseURL, clear/append
/// models/tags/policies/settings/endpoints, errorMessage) are still declared so
/// the introspected schema matches the reference.
/// </summary>
public sealed class UpdateChannelInput
{
    public ChannelTypeValue? Type { get; set; }

    [GraphQLName("baseURL")]
    public string? BaseUrl { get; set; }

    public bool? ClearBaseURL { get; set; }

    public string? Name { get; set; }

    public ChannelStatusValue? Status { get; set; }

    public ChannelCredentialsInput? Credentials { get; set; }

    public IReadOnlyList<string>? SupportedModels { get; set; }

    public IReadOnlyList<string>? AppendSupportedModels { get; set; }

    public IReadOnlyList<string>? ManualModels { get; set; }

    public IReadOnlyList<string>? AppendManualModels { get; set; }

    public bool? ClearManualModels { get; set; }

    public bool? AutoSyncSupportedModels { get; set; }

    public string? AutoSyncModelPattern { get; set; }

    public bool? ClearAutoSyncModelPattern { get; set; }

    public IReadOnlyList<string>? Tags { get; set; }

    public IReadOnlyList<string>? AppendTags { get; set; }

    public bool? ClearTags { get; set; }

    public string? DefaultTestModel { get; set; }

    public ChannelPoliciesInput? Policies { get; set; }

    public bool? ClearPolicies { get; set; }

    public ChannelSettingsInput? Settings { get; set; }

    public bool? ClearSettings { get; set; }

    public int? OrderingWeight { get; set; }

    public string? ErrorMessage { get; set; }

    public bool? ClearErrorMessage { get; set; }

    public string? Remark { get; set; }

    public bool? ClearRemark { get; set; }

    public IReadOnlyList<ChannelEndpointInput>? Endpoints { get; set; }

    public IReadOnlyList<ChannelEndpointInput>? AppendEndpoints { get; set; }

    public bool? ClearEndpoints { get; set; }
}

/// <summary>Port of <c>ChannelPoliciesInput</c> (axonhub.graphql:269-273).</summary>
public sealed class ChannelPoliciesInput
{
    public CapabilityPolicyValue? Stream { get; set; }

    public ApiKeyAutoDisableModeValue? ApiKeyAutoDisableMode { get; set; }

    public IReadOnlyList<ApiKeyAutoDisableRuleInput>? ApiKeyAutoDisableRules { get; set; }
}

/// <summary>Port of <c>ModelMappingInput</c> (axonhub.graphql:274-277).</summary>
public sealed class ModelMappingInput
{
    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;
}

/// <summary>
/// Port of <c>ChannelSettingsInput</c> (axonhub.graphql:280-304), including the
/// live fields maxSingleChannelRetries, responseTimeout and the API key
/// strategy pair.
/// </summary>
public sealed class ChannelSettingsInput
{
    public string? ExtraModelPrefix { get; set; }

    public IReadOnlyList<ModelMappingInput>? ModelMappings { get; set; }

    public IReadOnlyList<string>? AutoTrimedModelPrefixes { get; set; }

    public bool? HideOriginalModels { get; set; }

    public bool? HideMappedModels { get; set; }

    public bool? LowercaseModelId { get; set; }

    public ProxyConfigInput? Proxy { get; set; }

    public TransformOptionsInput? TransformOptions { get; set; }

    public IReadOnlyList<OverrideOperationInput>? HeaderOverrideOperations { get; set; }

    public IReadOnlyList<OverrideOperationInput>? BodyOverrideOperations { get; set; }

    public bool? PassThroughUserAgent { get; set; }

    public bool? PassThroughBody { get; set; }

    public ChannelRateLimitInput? RateLimit { get; set; }

    public IReadOnlyList<int>? RetryableStatusCodes { get; set; }

    public IReadOnlyList<RetryableErrorPatternInput>? RetryableErrorPatterns { get; set; }

    public int? MaxSingleChannelRetries { get; set; }

    public ChannelResponseTimeoutInput? ResponseTimeout { get; set; }

    public IReadOnlyList<ModelProtocolInput>? ModelProtocols { get; set; }

    public string? ApiKeyStrategy { get; set; }

    public int? ApiKeyRoundRobinSwitchAfter { get; set; }

    public ChannelProviderQuotaSettingsInput? ProviderQuota { get; set; }

    public ChannelQuotaRoutingModeValue? QuotaRoutingMode { get; set; }
}

/// <summary>Port of <c>ReasoningEffortMappingInput</c> (axonhub.graphql:73-76).</summary>
public sealed class ReasoningEffortMappingInput
{
    public string From { get; set; } = string.Empty;

    public string To { get; set; } = string.Empty;
}

/// <summary>Port of <c>TransformOptionsInput</c> (axonhub.graphql:78-83).</summary>
public sealed class TransformOptionsInput
{
    public bool? ForceArrayInstructions { get; set; }

    public bool? ForceArrayInputs { get; set; }

    public bool? ReplaceDeveloperRoleWithSystem { get; set; }

    public IReadOnlyList<ReasoningEffortMappingInput>? ReasoningEffortMapping { get; set; }
}

/// <summary>Port of <c>ProxyConfigInput</c> (axonhub.graphql:136-142).</summary>
public sealed class ProxyConfigInput
{
    public ProxyTypeValue Type { get; set; }

    public string? Url { get; set; }

    public string? Username { get; set; }

    public string? Password { get; set; }

    public bool? DisableConnectionReuse { get; set; }
}

/// <summary>Port of <c>OverrideOperationInput</c> (axonhub.graphql:38-48).</summary>
public sealed class OverrideOperationInput
{
    public string Op { get; set; } = string.Empty;

    public string? Path { get; set; }

    public string? From { get; set; }

    public string? To { get; set; }

    public string? Value { get; set; }

    public string? Condition { get; set; }

    public OverrideMatchInput? Match { get; set; }

    public int? Index { get; set; }

    public bool? Splat { get; set; }
}

/// <summary>Port of <c>OverrideMatchInput</c> (axonhub.graphql:50-53).</summary>
public sealed class OverrideMatchInput
{
    public string Path { get; set; } = string.Empty;

    public string Eq { get; set; } = string.Empty;
}

/// <summary>Port of <c>RetryableErrorPatternInput</c> (axonhub.graphql:149-152).</summary>
public sealed class RetryableErrorPatternInput
{
    public string Pattern { get; set; } = string.Empty;

    public bool? Regex { get; set; }
}

/// <summary>Port of <c>ModelProtocolInput</c> (axonhub.graphql:160-164).</summary>
public sealed class ModelProtocolInput
{
    public string Model { get; set; } = string.Empty;

    public IReadOnlyList<string> ApiFormats { get; set; } = [];

    public bool? Enabled { get; set; }
}

/// <summary>Port of <c>ChannelRateLimitInput</c> (axonhub.graphql:313-319).</summary>
public sealed class ChannelRateLimitInput
{
    public int? Rpm { get; set; }

    public int? Tpm { get; set; }

    public int? MaxConcurrent { get; set; }

    public int? QueueSize { get; set; }

    public int? QueueTimeoutMs { get; set; }
}

/// <summary>Port of the live <c>ChannelResponseTimeoutInput</c> (axonhub.graphql:240-244).</summary>
public sealed class ChannelResponseTimeoutInput
{
    public ChannelResponseTimeoutModeValue? Mode { get; set; }

    public int? StreamFirstEventTimeoutSeconds { get; set; }

    public int? NonStreamResponseTimeoutSeconds { get; set; }
}

/// <summary>Port of <c>CommandCodeQuotaSettingsInput</c> (axonhub.graphql:174-176).</summary>
public sealed class CommandCodeQuotaSettingsInput
{
    public string? AuthCookie { get; set; }
}

/// <summary>Port of <c>OllamaQuotaSettingsInput</c> (axonhub.graphql:182-184).</summary>
public sealed class OllamaQuotaSettingsInput
{
    public string? AuthCookie { get; set; }
}

/// <summary>Port of <c>ChannelProviderQuotaSettingsInput</c> (axonhub.graphql:191-194).</summary>
public sealed class ChannelProviderQuotaSettingsInput
{
    public CommandCodeQuotaSettingsInput? CommandCode { get; set; }

    public OllamaQuotaSettingsInput? Ollama { get; set; }
}

/// <summary>Port of <c>ChannelCredentialsInput</c> (axonhub.graphql:407-414, live).</summary>
public sealed class ChannelCredentialsInput
{
    public string? ApiKey { get; set; }

    public IReadOnlyList<string>? ApiKeys { get; set; }

    public IReadOnlyList<VertexApiKeyInput>? VertexApiKeys { get; set; }

    public GcpCredentialInput? Gcp { get; set; }

    [GraphQLName("oauth")]
    public OAuthCredentialsInput? OAuth { get; set; }

    public string? ManagementApiKey { get; set; }
}

/// <summary>Port of <c>OAuthCredentialsInput</c> (axonhub.graphql:416-423).</summary>
public sealed class OAuthCredentialsInput
{
    public string? AccessToken { get; set; }

    public string? RefreshToken { get; set; }

    public string? ClientID { get; set; }

    [GraphQLType(typeof(GoTimeType))]
    public DateTime? ExpiresAt { get; set; }

    public string? TokenType { get; set; }

    public IReadOnlyList<string>? Scopes { get; set; }
}

/// <summary>Port of <c>GCPCredentialInput</c> (axonhub.graphql:431-435).</summary>
[GraphQLName("GCPCredentialInput")]
public sealed class GcpCredentialInput
{
    public string Region { get; set; } = string.Empty;

    public string ProjectID { get; set; } = string.Empty;

    public string JsonData { get; set; } = string.Empty;
}

/// <summary>Port of the live <c>VertexAPIKeyInput</c> (axonhub.graphql:425-429).</summary>
[GraphQLName("VertexAPIKeyInput")]
public sealed class VertexApiKeyInput
{
    public string ProjectID { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;
}

/// <summary>Port of <c>ChannelEndpointInput</c> (axonhub.graphql:106-111).</summary>
public sealed class ChannelEndpointInput
{
    public string ApiFormat { get; set; } = string.Empty;

    public string? Path { get; set; }

    [GraphQLName("baseURL")]
    public string? BaseUrl { get; set; }

    public string? Transport { get; set; }
}
