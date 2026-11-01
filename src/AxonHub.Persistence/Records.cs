namespace AxonHub.Persistence;

/// <summary>
/// Row of the reference <c>channels</c> table (internal/ent/migrate/schema.go).
/// JSON columns are kept as raw text so the shape stays byte-for-byte under the
/// caller's control, like objects.JSONRawMessage.
/// </summary>
public sealed class ChannelRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>SoftDeleteMixin: 0 means live, Unix seconds means deleted.</summary>
    public long DeletedAt { get; set; }

    public string Type { get; set; } = string.Empty;

    public string? BaseUrl { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = "disabled";

    public string Credentials { get; set; } = "{}";

    public string? DisabledApiKeys { get; set; }

    public string SupportedModels { get; set; } = "null";

    public string? ManualModels { get; set; }

    public bool AutoSyncSupportedModels { get; set; }

    public string? AutoSyncModelPattern { get; set; } = string.Empty;

    public string? Tags { get; set; }

    public string DefaultTestModel { get; set; } = string.Empty;

    public string? Policies { get; set; }

    public string? Settings { get; set; }

    public long OrderingWeight { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime? AutoDisabledAt { get; set; }

    public DateTime? AutoDisableExpiresAt { get; set; }

    public string? Remark { get; set; }

    public string? Endpoints { get; set; }
}

/// <summary>Row of the reference <c>channel_model_prices</c> table.</summary>
public sealed class ChannelModelPriceRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>SoftDeleteMixin: 0 means live.</summary>
    public long DeletedAt { get; set; }

    public string ModelId { get; set; } = string.Empty;

    /// <summary>objects.ModelPrice JSON; decimals are strings, as Go marshals them.</summary>
    public string Price { get; set; } = "null";

    public string ReferenceId { get; set; } = string.Empty;

    public long ChannelId { get; set; }
}

/// <summary>Row of the reference <c>requests</c> table.</summary>
public sealed class RequestRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public long? UserId { get; set; }

    public string Source { get; set; } = "api";

    public string ModelId { get; set; } = string.Empty;

    public string? ReasoningEffort { get; set; }

    public string Format { get; set; } = "openai/chat_completions";

    public string? RequestHeaders { get; set; }

    /// <summary>Go's zero JSONRawMessage marshals to <c>null</c>.</summary>
    public string RequestBody { get; set; } = "null";

    public string? ResponseHeaders { get; set; }

    public string? ResponseBody { get; set; }

    public string? ResponseChunks { get; set; }

    public string? ExternalId { get; set; }

    public string Status { get; set; } = string.Empty;

    public bool Stream { get; set; }

    public string ClientIp { get; set; } = string.Empty;

    public string UserAgent { get; set; } = string.Empty;

    public long? MetricsLatencyMs { get; set; }

    public long? MetricsFirstTokenLatencyMs { get; set; }

    public long? MetricsReasoningDurationMs { get; set; }

    public bool ContentSaved { get; set; }

    public long? ContentStorageId { get; set; }

    public string? ContentStorageKey { get; set; }

    public DateTime? ContentSavedAt { get; set; }

    public long? ApiKeyId { get; set; }

    public long? ChannelId { get; set; }

    public long? DataStorageId { get; set; }

    public long ProjectId { get; set; } = 1;

    public long? TraceId { get; set; }
}

/// <summary>Row of the reference <c>request_executions</c> table.</summary>
public sealed class RequestExecutionRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public long ProjectId { get; set; } = 1;

    public long? ChannelApiKeyIndex { get; set; }

    public string? ExternalId { get; set; }

    public string ModelId { get; set; } = string.Empty;

    public string? UpstreamModelId { get; set; }

    public string Format { get; set; } = "openai/chat_completions";

    public string? ReasoningEffort { get; set; }

    public string? ChannelApiKeySuffix { get; set; }

    public string RequestBody { get; set; } = "null";

    public string? ResponseHeaders { get; set; }

    public string? ResponseBody { get; set; }

    public string? ResponseChunks { get; set; }

    public string? ErrorMessage { get; set; }

    public long? ResponseStatusCode { get; set; }

    public string Status { get; set; } = string.Empty;

    public bool Stream { get; set; }

    public long? MetricsLatencyMs { get; set; }

    public long? MetricsFirstTokenLatencyMs { get; set; }

    public long? MetricsReasoningDurationMs { get; set; }

    public string? RequestHeaders { get; set; }

    public string? RequestUrl { get; set; }

    public bool PassThroughApplied { get; set; }

    public long? ChannelId { get; set; }

    public long? DataStorageId { get; set; }

    public long RequestId { get; set; }
}

/// <summary>Row of the reference <c>projects</c> table.</summary>
public sealed class ProjectRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>SoftDeleteMixin: 0 means live, Unix seconds means deleted.</summary>
    public long DeletedAt { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Status { get; set; } = "active";

    /// <summary>ProjectProfiles JSON; Ent stores it as jsonb.</summary>
    public string? Profiles { get; set; }
}

/// <summary>Row of the reference <c>users</c> table.</summary>
public sealed class UserRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>SoftDeleteMixin: 0 means live, Unix seconds means deleted.</summary>
    public long DeletedAt { get; set; }

    public string Email { get; set; } = string.Empty;

    public string Status { get; set; } = "activated";

    public string PreferLanguage { get; set; } = "en";

    public string Password { get; set; } = string.Empty;

    public string FirstName { get; set; } = string.Empty;

    public string LastName { get; set; } = string.Empty;

    public string? Avatar { get; set; }

    public bool IsOwner { get; set; }

    /// <summary>JSON string array of system scopes.</summary>
    public string? Scopes { get; set; }
}

/// <summary>Row of the reference <c>api_keys</c> table.</summary>
public sealed class ApiKeyRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>SoftDeleteMixin: 0 means live, Unix seconds means deleted.</summary>
    public long DeletedAt { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>user, service_account, noauth, or personal.</summary>
    public string Type { get; set; } = "user";

    /// <summary>enabled, disabled, or archived.</summary>
    public string Status { get; set; } = "enabled";

    /// <summary>JSON string array; Ent's create default is read_channels/write_requests.</summary>
    public string? Scopes { get; set; }

    /// <summary>APIKeyProfiles JSON.</summary>
    public string? Profiles { get; set; }

    /// <summary>JSON string array of allowed CIDRs; empty means no restriction.</summary>
    public string? AllowedIps { get; set; }

    public long ProjectId { get; set; } = 1;

    public long? UserId { get; set; }
}

/// <summary>Row of the reference <c>systems</c> table (key/value settings).</summary>
public sealed class SystemRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>SoftDeleteMixin: 0 means live, Unix seconds means deleted.</summary>
    public long DeletedAt { get; set; }

    /// <summary>Settings key; Ent declares it unique.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Settings value; JSON for structured settings.</summary>
    public string Value { get; set; } = string.Empty;
}

/// <summary>Row of the reference <c>prompt_protection_rules</c> table.</summary>
public sealed class PromptProtectionRuleRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>SoftDeleteMixin: 0 means live, Unix seconds means deleted.</summary>
    public long DeletedAt { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Go regular expression (regexp2, .NET syntax).</summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>enabled, disabled, or archived.</summary>
    public string Status { get; set; } = "disabled";

    /// <summary>objects.PromptProtectionSettings JSON; Ent stores it as jsonb.</summary>
    public string Settings { get; set; } = "{}";
}

/// <summary>Row of the reference <c>usage_logs</c> table.</summary>
public sealed class UsageLogRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public long? ApiKeyId { get; set; }

    public string ModelId { get; set; } = string.Empty;

    public long PromptTokens { get; set; }

    public long CompletionTokens { get; set; }

    public long TotalTokens { get; set; }

    public long? PromptAudioTokens { get; set; }

    public long? PromptCachedTokens { get; set; }

    public long? PromptWriteCachedTokens { get; set; }

    public long? PromptWriteCachedTokens5m { get; set; }

    public long? PromptWriteCachedTokens1h { get; set; }

    public long? CompletionAudioTokens { get; set; }

    public long? CompletionReasoningTokens { get; set; }

    public long? CompletionAcceptedPredictionTokens { get; set; }

    public long? CompletionRejectedPredictionTokens { get; set; }

    public string Source { get; set; } = "api";

    public string Format { get; set; } = "openai/chat_completions";

    public double? TotalCost { get; set; }

    /// <summary>Go's default is a non-nil empty slice, which marshals to [].</summary>
    public string? CostItems { get; set; } = "[]";

    public string? CostPriceReferenceId { get; set; }

    public long? ChannelId { get; set; }

    public long ProjectId { get; set; } = 1;

    public long RequestId { get; set; }
}
