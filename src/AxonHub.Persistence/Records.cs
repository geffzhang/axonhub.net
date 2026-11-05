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

/// <summary>
/// Row of the reference <c>channel_model_price_versions</c> table
/// (internal/ent/schema/channel_model_price_versions.go). The version history
/// keeps the price that was active during a billing period; archiving stamps
/// <see cref="EffectiveEndAt"/> while a new active row carries the new price.
/// </summary>
public sealed class ChannelModelPriceVersionRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public long ChannelId { get; set; }

    public string ModelId { get; set; } = string.Empty;

    public long ChannelModelPriceId { get; set; }

    /// <summary>objects.ModelPrice JSON; decimals are strings, as Go marshals them.</summary>
    public string Price { get; set; } = "null";

    /// <summary>Ent enum channelmodelpriceversion.Status: active / archived.</summary>
    public string Status { get; set; } = "active";

    public DateTime EffectiveStartAt { get; set; }

    public DateTime? EffectiveEndAt { get; set; }

    public string ReferenceId { get; set; } = string.Empty;
}

/// <summary>
/// Row of the reference <c>channel_override_templates</c> table
/// (internal/ent/schema/channel_override_template.go). A template stores the
/// override operations a user can apply to many channels at once; the legacy
/// <see cref="OverrideParameters"/>/<see cref="OverrideHeaders"/> columns are
/// kept for rows written before the operation-list migration.
/// </summary>
public sealed class ChannelOverrideTemplateRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>SoftDeleteMixin: 0 means live.</summary>
    public long DeletedAt { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>Legacy objects.OverrideParameters JSON; ent default "{}".</summary>
    public string OverrideParameters { get; set; } = "{}";

    /// <summary>Legacy []objects.HeaderEntry JSON; ent default "[]".</summary>
    public string OverrideHeaders { get; set; } = "[]";

    /// <summary>[]objects.OverrideOperation JSON; nil marshals to JSON null.</summary>
    public string? HeaderOverrideOperations { get; set; }

    /// <summary>[]objects.OverrideOperation JSON; nil marshals to JSON null.</summary>
    public string? BodyOverrideOperations { get; set; }

    /// <summary>
    /// Owner user. The schema leaves it optional, but UserOwnedQueryRule /
    /// UserOwnedMutationRule only expose a row to the user it names (the owner
    /// account bypasses the restriction), so a NULL row is invisible to
    /// non-owners.
    /// </summary>
    public long? UserId { get; set; }
}

/// <summary>
/// Row of the reference <c>models</c> table (internal/ent/schema/model.go). The
/// model library supplies the automatic channel prices: each enabled or
/// disabled model's <see cref="ModelCard"/> carries the per-token costs.
/// </summary>
public sealed class ModelRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>SoftDeleteMixin: 0 means live.</summary>
    public long DeletedAt { get; set; }

    public string Developer { get; set; } = string.Empty;

    public string ModelId { get; set; } = string.Empty;

    /// <summary>Ent enum model.Type: chat / embedding / rerank / image_generation / video_generation.</summary>
    public string Type { get; set; } = "chat";

    public string Name { get; set; } = string.Empty;

    public string Icon { get; set; } = string.Empty;

    /// <summary>SQL keyword, always quoted by the generated DDL.</summary>
    public string Group { get; set; } = string.Empty;

    /// <summary>objects.ModelCard JSON; costs are float64 numbers.</summary>
    public string ModelCard { get; set; } = "{}";

    /// <summary>objects.ModelSettings JSON.</summary>
    public string Settings { get; set; } = "{}";

    /// <summary>Ent enum model.Status: enabled / disabled / archived.</summary>
    public string Status { get; set; } = "disabled";

    public string? Remark { get; set; }
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

/// <summary>Row of the reference <c>traces</c> table.</summary>
public sealed class TraceRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>Globally unique trace identifier configured by the client or a client marker.</summary>
    public string TraceId { get; set; } = string.Empty;

    /// <summary>Record status: active (default), archived (hidden), retained (protected from GC).</summary>
    public string Status { get; set; } = "active";

    /// <summary>Project that owns the trace.</summary>
    public long ProjectId { get; set; }

    /// <summary>Thread the trace belongs to; null when the request had no thread header.</summary>
    public long? ThreadId { get; set; }
}

/// <summary>Row of the reference <c>threads</c> table.</summary>
public sealed class ThreadRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>Globally unique thread identifier supplied by the client.</summary>
    public string ThreadId { get; set; } = string.Empty;

    /// <summary>Record status: active (default), archived (hidden), retained (protected from GC).</summary>
    public string Status { get; set; } = "active";

    /// <summary>Project that owns the thread.</summary>
    public long ProjectId { get; set; }
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

/// <summary>Row of the reference <c>prompts</c> table.</summary>
public sealed class PromptRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>SoftDeleteMixin: 0 means live, Unix seconds means deleted.</summary>
    public long DeletedAt { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string Content { get; set; } = string.Empty;

    /// <summary>enabled or disabled.</summary>
    public string Status { get; set; } = "disabled";

    /// <summary>Insertion order; smaller values are inserted first.</summary>
    public long Order { get; set; }

    /// <summary>objects.PromptSettings JSON; Ent stores it as jsonb.</summary>
    public string Settings { get; set; } = "{}";

    public long ProjectId { get; set; } = 1;
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

/// <summary>Row of the reference <c>roles</c> table.</summary>
public sealed class RoleRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>SoftDeleteMixin: 0 means live, Unix seconds means deleted.</summary>
    public long DeletedAt { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>system or project.</summary>
    public string Level { get; set; } = "system";

    /// <summary>JSON string array of scopes.</summary>
    public string? Scopes { get; set; }

    /// <summary>Project ID; 0 for system roles (Ent's nullable column).</summary>
    public long? ProjectId { get; set; }

    /// <summary>
    /// Port of ent's generated <c>Role.IsSystemRole()</c>
    /// (internal/ent/extra.go:8-10): <c>ProjectID == nil || *ProjectID == 0</c>.
    /// The reference ignores the <c>level</c> string for this decision.
    /// </summary>
    public bool IsSystemRole => ProjectId is null or 0;
}

/// <summary>Row of the reference <c>user_roles</c> join table.</summary>
public sealed class UserRoleRecord
{
    public long Id { get; set; }

    /// <summary>Ent declares the timestamps nullable in this join table.</summary>
    public DateTime? CreatedAt { get; set; }

    public DateTime? UpdatedAt { get; set; }

    public long UserId { get; set; }

    public long RoleId { get; set; }
}

/// <summary>Row of the reference <c>user_projects</c> join table.</summary>
public sealed class UserProjectRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public bool IsOwner { get; set; }

    /// <summary>JSON string array of project-scoped scopes.</summary>
    public string? Scopes { get; set; }

    public long UserId { get; set; }

    public long ProjectId { get; set; }
}

/// <summary>Row of the reference <c>oidc_identities</c> table.</summary>
public sealed class OidcIdentityRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>SoftDeleteMixin: 0 means live, Unix seconds means deleted.</summary>
    public long DeletedAt { get; set; }

    public string Issuer { get; set; } = string.Empty;

    public string Subject { get; set; } = string.Empty;

    public string? Email { get; set; }

    public string? IdpName { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public long UserId { get; set; }
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

/// <summary>Row of the reference <c>data_storages</c> table.</summary>
public sealed class DataStorageRecord
{
    public long Id { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>SoftDeleteMixin: 0 means live, Unix seconds means deleted.</summary>
    public long DeletedAt { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>Immutable; only the seeded system database sets it.</summary>
    public bool Primary { get; set; }

    /// <summary>database, fs, s3, gcs, or webdav.</summary>
    public string Type { get; set; } = "database";

    /// <summary>objects.DataStorageSettings JSON; Ent stores it as jsonb.</summary>
    public string Settings { get; set; } = "{}";

    /// <summary>active or archived.</summary>
    public string Status { get; set; } = "active";
}
