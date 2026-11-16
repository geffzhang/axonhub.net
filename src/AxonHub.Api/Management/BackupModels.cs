using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Api.Billing;
using AxonHub.Api.Models;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of backup.BackupOptions (internal/server/backup/types.go:214-223).
/// <see cref="IncludeProjects"/> has no GraphQL input field, so the management
/// API always leaves it false.
/// </summary>
public sealed record BackupOptions
{
    public bool IncludeSystemConfigs { get; init; }

    public bool IncludeProjects { get; init; }

    public bool IncludeChannels { get; init; }

    public bool IncludeModels { get; init; }

    public bool IncludeApiKeys { get; init; }

    public bool IncludeModelPrices { get; init; }

    public bool IncludeUsageStats { get; init; }

    public bool IncludeRequestLogs { get; init; }
}

/// <summary>Port of backup.ConflictStrategy (types.go:226-232).</summary>
public static class BackupConflictStrategy
{
    public const string Skip = "skip";

    public const string Overwrite = "overwrite";

    public const string Error = "error";
}

/// <summary>
/// Port of backup.RestoreOptions (types.go:234-245). The GraphQL input does not
/// expose <see cref="IncludeProjects"/> or <see cref="ProjectConflictStrategy"/>.
/// </summary>
public sealed record RestoreOptions
{
    public bool IncludeSystemConfigs { get; init; }

    public bool IncludeProjects { get; init; }

    public bool IncludeChannels { get; init; }

    public bool IncludeModels { get; init; }

    public bool IncludeApiKeys { get; init; }

    public bool IncludeModelPrices { get; init; }

    public bool IncludeUsageStats { get; init; }

    public bool IncludeRequestLogs { get; init; }

    public string ProjectConflictStrategy { get; init; } = string.Empty;

    public string ChannelConflictStrategy { get; init; } = string.Empty;

    public string ModelConflictStrategy { get; init; } = string.Empty;

    public string ModelPriceConflictStrategy { get; init; } = string.Empty;

    public string ApiKeyConflictStrategy { get; init; } = string.Empty;
}

/// <summary>Port of the backup version constants (types.go:208-213).</summary>
public static class BackupVersions
{
    public const string Current = "1.4";

    public const string V4 = "1.3";

    public const string V3 = "1.2";

    public const string V2 = "1.1";

    public const string V1 = "1.0";

    public static readonly IReadOnlyList<string> Accepted = [Current, V4, V3, V2, V1];
}

/// <summary>Port of biz.BackupFrequency (internal/server/biz/system.go:188-195).</summary>
public static class BackupFrequencies
{
    public const string Daily = "daily";

    public const string Weekly = "weekly";

    public const string Monthly = "monthly";
}

/// <summary>Port of backup.BackupSystemConfig (types.go:41-44).</summary>
public sealed record BackupSystemConfig(string Key, string Value);

/// <summary>
/// Port of biz.AutoBackupSettings (biz/system.go:197-219). The JSON shape is
/// pinned by <see cref="AutoBackupSettingsCodec"/>.
/// </summary>
public sealed record AutoBackupSettings
{
    public bool Enabled { get; init; }

    public string Frequency { get; init; } = BackupFrequencies.Daily;

    public int DataStorageId { get; init; }

    public bool IncludeSystemConfigs { get; init; }

    public bool IncludeChannels { get; init; }

    public bool IncludeModels { get; init; }

    public bool IncludeApiKeys { get; init; }

    public bool IncludeModelPrices { get; init; }

    public bool IncludeUsageStats { get; init; }

    public bool IncludeRequestLogs { get; init; }

    public int RetentionDays { get; init; }

    public DateTime? LastBackupAt { get; init; }

    public string LastBackupError { get; init; } = string.Empty;
}

/// <summary>
/// Port of biz/SystemService.AutoBackupSettings / SetAutoBackupSettings and the
/// autoBackupSettingsJSON decode struct (biz/system.go:1615-1676). Only the
/// three pointer fields fall back to the defaults when absent; every other
/// missing field keeps its Go zero value.
/// </summary>
public static class AutoBackupSettingsCodec
{
    public const string Key = "system_auto_backup_settings";

    /// <summary>Port of defaultAutoBackupSettings (system_default.go:118-129).</summary>
    public static AutoBackupSettings Default { get; } = new()
    {
        Enabled = false,
        Frequency = BackupFrequencies.Daily,
        DataStorageId = 0,
        IncludeSystemConfigs = false,
        IncludeChannels = true,
        IncludeModels = true,
        IncludeApiKeys = false,
        IncludeModelPrices = true,
        IncludeUsageStats = false,
        IncludeRequestLogs = false,
        RetentionDays = 30,
        LastBackupAt = null,
        LastBackupError = string.Empty,
    };

    /// <exception cref="JsonException">The stored value is not valid JSON.</exception>
    public static AutoBackupSettings Deserialize(string value)
    {
        AutoBackupSettingsJson? stored;
        try
        {
            stored = JsonSerializer.Deserialize<AutoBackupSettingsJson>(value, LlmJson.Options);
        }
        catch (JsonException)
        {
            throw;
        }

        stored ??= new AutoBackupSettingsJson();

        return new AutoBackupSettings
        {
            Enabled = stored.Enabled,
            Frequency = stored.Frequency ?? string.Empty,
            DataStorageId = stored.DataStorageId,
            IncludeSystemConfigs = stored.IncludeSystemConfigs ?? Default.IncludeSystemConfigs,
            IncludeChannels = stored.IncludeChannels,
            IncludeModels = stored.IncludeModels,
            IncludeApiKeys = stored.IncludeApiKeys,
            IncludeModelPrices = stored.IncludeModelPrices,
            IncludeUsageStats = stored.IncludeUsageStats ?? Default.IncludeUsageStats,
            IncludeRequestLogs = stored.IncludeRequestLogs ?? Default.IncludeRequestLogs,
            RetentionDays = stored.RetentionDays,
            LastBackupAt = stored.LastBackupAt,
            LastBackupError = stored.LastBackupError ?? string.Empty,
        };
    }

    public static string Serialize(AutoBackupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("enabled", settings.Enabled);
            writer.WriteString("frequency", settings.Frequency);
            writer.WriteNumber("data_storage_id", settings.DataStorageId);
            writer.WriteBoolean("include_system_configs", settings.IncludeSystemConfigs);
            writer.WriteBoolean("include_channels", settings.IncludeChannels);
            writer.WriteBoolean("include_models", settings.IncludeModels);
            writer.WriteBoolean("include_api_keys", settings.IncludeApiKeys);
            writer.WriteBoolean("include_model_prices", settings.IncludeModelPrices);
            writer.WriteBoolean("include_usage_stats", settings.IncludeUsageStats);
            writer.WriteBoolean("include_request_logs", settings.IncludeRequestLogs);
            writer.WriteNumber("retention_days", settings.RetentionDays);

            if (settings.LastBackupAt is { } lastBackupAt)
            {
                writer.WriteString("last_backup_at", GoTime.Rfc3339Nano(lastBackupAt));
            }

            if (!string.IsNullOrEmpty(settings.LastBackupError))
            {
                writer.WriteString("last_backup_error", settings.LastBackupError);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(GoJson.Escape(buffer.ToArray()));
    }

    private sealed class AutoBackupSettingsJson
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; }

        [JsonPropertyName("frequency")]
        public string? Frequency { get; set; }

        [JsonPropertyName("data_storage_id")]
        public int DataStorageId { get; set; }

        [JsonPropertyName("include_system_configs")]
        public bool? IncludeSystemConfigs { get; set; }

        [JsonPropertyName("include_channels")]
        public bool IncludeChannels { get; set; }

        [JsonPropertyName("include_models")]
        public bool IncludeModels { get; set; }

        [JsonPropertyName("include_api_keys")]
        public bool IncludeApiKeys { get; set; }

        [JsonPropertyName("include_model_prices")]
        public bool IncludeModelPrices { get; set; }

        [JsonPropertyName("include_usage_stats")]
        public bool? IncludeUsageStats { get; set; }

        [JsonPropertyName("include_request_logs")]
        public bool? IncludeRequestLogs { get; set; }

        [JsonPropertyName("retention_days")]
        public int RetentionDays { get; set; }

        [JsonPropertyName("last_backup_at")]
        [JsonConverter(typeof(GoNullableDateTimeConverter))]
        public DateTime? LastBackupAt { get; set; }

        [JsonPropertyName("last_backup_error")]
        public string? LastBackupError { get; set; }
    }
}

/// <summary>
/// Decoded backup document (backup.BackupData, types.go:11-24). Every element
/// list keeps null distinct from empty, and every timestamp keeps Go's
/// zero-on-null behaviour.
/// </summary>
public sealed class BackupData
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>time.Time; kept as text because restore never reads it.</summary>
    [JsonPropertyName("timestamp")]
    [JsonConverter(typeof(GoTimestampTextConverter))]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("system_configs")]
    public List<BackupSystemConfig?>? SystemConfigs { get; set; }

    [JsonPropertyName("projects")]
    public List<BackupProjectData?>? Projects { get; set; }

    [JsonPropertyName("channels")]
    public List<BackupChannelData?>? Channels { get; set; }

    [JsonPropertyName("models")]
    public List<BackupModelData?>? Models { get; set; }

    [JsonPropertyName("channel_model_prices")]
    public List<BackupChannelModelPriceData?>? ChannelModelPrices { get; set; }

    [JsonPropertyName("api_keys")]
    public List<BackupApiKeyData?>? ApiKeys { get; set; }

    [JsonPropertyName("usage_requests")]
    public List<BackupUsageRequestData?>? UsageRequests { get; set; }

    [JsonPropertyName("usage_logs")]
    public List<BackupUsageLogData?>? UsageLogs { get; set; }
}

/// <summary>Ent entity fields of ent.Project (project.go:19-36).</summary>
public sealed class BackupProjectData
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("created_at")]
    [JsonConverter(typeof(GoDateTimeConverter))]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    [JsonConverter(typeof(GoDateTimeConverter))]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("deleted_at")]
    public long DeletedAt { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("profiles")]
    public ProjectProfilesModel? Profiles { get; set; }
}

/// <summary>
/// Ent entity fields of ent.Channel plus the wrapper credentials object
/// (types.go:52-57). Policies/endpoints/disabled_api_keys are ignored by
/// restore and therefore not modelled.
/// </summary>
public sealed class BackupChannelData
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("created_at")]
    [JsonConverter(typeof(GoDateTimeConverter))]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    [JsonConverter(typeof(GoDateTimeConverter))]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("deleted_at")]
    public long DeletedAt { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("base_url")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("supported_models")]
    public List<string>? SupportedModels { get; set; }

    [JsonPropertyName("manual_models")]
    public List<string>? ManualModels { get; set; }

    [JsonPropertyName("auto_sync_supported_models")]
    public bool AutoSyncSupportedModels { get; set; }

    [JsonPropertyName("auto_sync_model_pattern")]
    public string AutoSyncModelPattern { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("default_test_model")]
    public string DefaultTestModel { get; set; } = string.Empty;

    [JsonPropertyName("settings")]
    public ChannelSettingsDraft? Settings { get; set; }

    [JsonPropertyName("ordering_weight")]
    public long OrderingWeight { get; set; }

    [JsonPropertyName("remark")]
    public string? Remark { get; set; }

    [JsonPropertyName("credentials")]
    public ChannelCredentialsDraft? Credentials { get; set; }
}

/// <summary>Ent entity fields of ent.Model (model.go:19-54).</summary>
public sealed class BackupModelData
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("created_at")]
    [JsonConverter(typeof(GoDateTimeConverter))]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    [JsonConverter(typeof(GoDateTimeConverter))]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("deleted_at")]
    public long DeletedAt { get; set; }

    [JsonPropertyName("developer")]
    public string Developer { get; set; } = string.Empty;

    [JsonPropertyName("model_id")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;

    [JsonPropertyName("group")]
    public string Group { get; set; } = string.Empty;

    /// <summary>Decoded with <see cref="ModelCardCodec"/>; kept raw here so Go's
    /// zero-value defaults apply to absent members.</summary>
    [JsonPropertyName("model_card")]
    public JsonElement? ModelCard { get; set; }

    /// <summary>Decoded with <see cref="ModelSettingsCodec"/>.</summary>
    [JsonPropertyName("settings")]
    public JsonElement? Settings { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("remark")]
    public string? Remark { get; set; }
}

/// <summary>Port of backup.BackupChannelModelPrice (types.go:70-75).</summary>
public sealed class BackupChannelModelPriceData
{
    [JsonPropertyName("channel_name")]
    public string ChannelName { get; set; } = string.Empty;

    [JsonPropertyName("model_id")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("price")]
    public ModelPrice Price { get; set; } = new();

    [JsonPropertyName("reference_id")]
    public string ReferenceId { get; set; } = string.Empty;
}

/// <summary>Ent entity fields of ent.APIKey plus project_name (types.go:59-62).</summary>
public sealed class BackupApiKeyData
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("created_at")]
    [JsonConverter(typeof(GoDateTimeConverter))]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    [JsonConverter(typeof(GoDateTimeConverter))]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("deleted_at")]
    public long DeletedAt { get; set; }

    [JsonPropertyName("user_id")]
    public long UserId { get; set; }

    [JsonPropertyName("project_id")]
    public long ProjectId { get; set; }

    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("scopes")]
    public List<string>? Scopes { get; set; }

    [JsonPropertyName("profiles")]
    public ApiKeyProfilesModel? Profiles { get; set; }

    [JsonPropertyName("allowed_ips")]
    public List<string>? AllowedIps { get; set; }

    [JsonPropertyName("project_name")]
    public string ProjectName { get; set; } = string.Empty;
}

/// <summary>
/// Port of backup.BackupUsageRequest's custom wire shape (types.go:64-131).
/// Raw JSON members stay as <see cref="JsonElement"/> so both objects and
/// arrays survive the decode.
/// </summary>
public sealed class BackupUsageRequestData
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("created_at")]
    [JsonConverter(typeof(GoDateTimeConverter))]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    [JsonConverter(typeof(GoDateTimeConverter))]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("project_id")]
    public long ProjectId { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("model_id")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("reasoning_effort")]
    public string ReasoningEffort { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("request_headers")]
    public JsonElement? RequestHeaders { get; set; }

    [JsonPropertyName("response_headers")]
    public JsonElement? ResponseHeaders { get; set; }

    [JsonPropertyName("request_body")]
    public JsonElement? RequestBody { get; set; }

    [JsonPropertyName("response_body")]
    public JsonElement? ResponseBody { get; set; }

    [JsonPropertyName("response_chunks")]
    public List<JsonElement>? ResponseChunks { get; set; }

    [JsonPropertyName("channel_id")]
    public long ChannelId { get; set; }

    [JsonPropertyName("external_id")]
    public string ExternalId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("client_ip")]
    public string ClientIp { get; set; } = string.Empty;

    [JsonPropertyName("metrics_latency_ms")]
    public long? MetricsLatencyMs { get; set; }

    [JsonPropertyName("metrics_first_token_latency_ms")]
    public long? MetricsFirstTokenLatencyMs { get; set; }

    [JsonPropertyName("metrics_reasoning_duration_ms")]
    public long? MetricsReasoningDurationMs { get; set; }

    [JsonPropertyName("content_saved")]
    public bool ContentSaved { get; set; }

    [JsonPropertyName("content_storage_id")]
    public long? ContentStorageId { get; set; }

    [JsonPropertyName("content_storage_key")]
    public string? ContentStorageKey { get; set; }

    [JsonPropertyName("content_saved_at")]
    [JsonConverter(typeof(GoNullableDateTimeConverter))]
    public DateTime? ContentSavedAt { get; set; }

    [JsonPropertyName("project_name")]
    public string ProjectName { get; set; } = string.Empty;

    [JsonPropertyName("channel_name")]
    public string ChannelName { get; set; } = string.Empty;

    [JsonPropertyName("api_key_key")]
    public string ApiKeyKey { get; set; } = string.Empty;
}

/// <summary>
/// Port of backup.BackupUsageLog's custom wire shape (types.go:133-201).
/// </summary>
public sealed class BackupUsageLogData
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("created_at")]
    [JsonConverter(typeof(GoDateTimeConverter))]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    [JsonConverter(typeof(GoDateTimeConverter))]
    public DateTime UpdatedAt { get; set; }

    [JsonPropertyName("request_id")]
    public long RequestId { get; set; }

    [JsonPropertyName("project_id")]
    public long ProjectId { get; set; }

    [JsonPropertyName("channel_id")]
    public long ChannelId { get; set; }

    [JsonPropertyName("model_id")]
    public string ModelId { get; set; } = string.Empty;

    [JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public long CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; set; }

    [JsonPropertyName("prompt_audio_tokens")]
    public long PromptAudioTokens { get; set; }

    [JsonPropertyName("prompt_cached_tokens")]
    public long PromptCachedTokens { get; set; }

    [JsonPropertyName("prompt_write_cached_tokens")]
    public long PromptWriteCachedTokens { get; set; }

    [JsonPropertyName("prompt_write_cached_tokens_5m")]
    public long PromptWriteCachedTokens5m { get; set; }

    [JsonPropertyName("prompt_write_cached_tokens_1h")]
    public long PromptWriteCachedTokens1h { get; set; }

    [JsonPropertyName("completion_audio_tokens")]
    public long CompletionAudioTokens { get; set; }

    [JsonPropertyName("completion_reasoning_tokens")]
    public long CompletionReasoningTokens { get; set; }

    [JsonPropertyName("completion_accepted_prediction_tokens")]
    public long CompletionAcceptedPredictionTokens { get; set; }

    [JsonPropertyName("completion_rejected_prediction_tokens")]
    public long CompletionRejectedPredictionTokens { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    [JsonPropertyName("total_cost")]
    public double? TotalCost { get; set; }

    [JsonPropertyName("cost_items")]
    public List<CostItem>? CostItems { get; set; }

    [JsonPropertyName("cost_price_reference_id")]
    public string CostPriceReferenceId { get; set; } = string.Empty;

    [JsonPropertyName("project_name")]
    public string ProjectName { get; set; } = string.Empty;

    [JsonPropertyName("channel_name")]
    public string ChannelName { get; set; } = string.Empty;

    [JsonPropertyName("api_key_key")]
    public string ApiKeyKey { get; set; } = string.Empty;
}

/// <summary>
/// Parses Go's <c>time.Time</c> JSON form. JSON null leaves the Go zero time,
/// matching <c>time.Time.UnmarshalJSON</c>.
/// </summary>
public sealed class GoDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return default;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Time.UnmarshalJSON: input is not a JSON string");
        }

        return GoBackupTime.Parse(reader.GetString() ?? string.Empty);
    }

    public override void Write(
        Utf8JsonWriter writer,
        DateTime value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(GoTime.Rfc3339Nano(value));
}

/// <summary>Nullable form of <see cref="GoDateTimeConverter"/>.</summary>
public sealed class GoNullableDateTimeConverter : JsonConverter<DateTime?>
{
    public override DateTime? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Time.UnmarshalJSON: input is not a JSON string");
        }

        return GoBackupTime.Parse(reader.GetString() ?? string.Empty);
    }

    public override void Write(
        Utf8JsonWriter writer,
        DateTime? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(GoTime.Rfc3339Nano(value.Value));
    }
}

/// <summary>
/// Reads the root <c>timestamp</c> without interpreting it (restore ignores
/// it); Go accepts any RFC 3339 value here, including a nine-digit fraction.
/// </summary>
public sealed class GoTimestampTextConverter : JsonConverter<string>
{
    public override string Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => string.Empty,
            JsonTokenType.String => reader.GetString() ?? string.Empty,
            _ => throw new JsonException("Time.UnmarshalJSON: input is not a JSON string"),
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        string value,
        JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

/// <summary>RFC 3339 / RFC 3339Nano parsing shared by the backup DTOs.</summary>
public static class GoBackupTime
{
    public static DateTime Parse(string text)
    {
        var normalized = NormalizeFraction(text);

        if (!DateTimeOffset.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            throw new JsonException(
                "parsing time " + GoJson.MarshalToString(text)
                + " as \"2006-01-02T15:04:05.999999999Z07:00\": cannot parse");
        }

        return parsed.UtcDateTime;
    }

    /// <summary>
    /// .NET parses at most seven fractional digits; Go accepts up to nine.
    /// Values stored in PostgreSQL never exceed six, so trimming is lossless
    /// for every value the reference can have written.
    /// </summary>
    private static string NormalizeFraction(string text)
    {
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot < 0)
        {
            return text;
        }

        var end = dot + 1;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        var digits = end - dot - 1;
        if (digits <= 7)
        {
            return text;
        }

        return text[..(dot + 8)] + text[end..];
    }
}
