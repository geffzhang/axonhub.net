using System.Globalization;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Billing;
using AxonHub.Api.Models;
using AxonHub.Llm.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Streams a compact backup document exactly like backup.doBackupToWriter
/// (internal/server/backup/backup_ops.go:71-132): fixed root member order,
/// 500-row ID-cursor pages, Go field order/omitempty rules per entity, and a
/// final compact JSON object.
/// </summary>
public static class BackupJson
{
    public const int BatchSize = 500;

    public static async Task WriteAsync(
        Stream output,
        BackupOptions options,
        IBackupBatchSource source,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);

        var writer = new FragmentWriter(output);

        await writer.WriteAsync("{", cancellationToken).ConfigureAwait(false);
        await writer
            .FieldRawAsync("version", Quote(BackupVersions.Current), cancellationToken)
            .ConfigureAwait(false);
        await writer
            .FieldRawAsync("timestamp", Quote(FormatLocalRfc3339Nano(now)), cancellationToken)
            .ConfigureAwait(false);

        await StreamArrayFieldAsync(
                writer,
                "system_configs",
                options.IncludeSystemConfigs,
                omitEmpty: true,
                (lastId, ct) => source.FetchSystemBatchAsync(lastId, BatchSize, ct),
                static row => SystemElement(row),
                cancellationToken)
            .ConfigureAwait(false);

        await StreamArrayFieldAsync(
                writer,
                "projects",
                options.IncludeProjects,
                omitEmpty: true,
                (lastId, ct) => source.FetchProjectBatchAsync(lastId, BatchSize, ct),
                static row => ProjectElement(row),
                cancellationToken)
            .ConfigureAwait(false);

        await StreamArrayFieldAsync(
                writer,
                "channels",
                options.IncludeChannels,
                omitEmpty: false,
                (lastId, ct) => source.FetchChannelBatchAsync(lastId, BatchSize, ct),
                static row => ChannelElement(row),
                cancellationToken)
            .ConfigureAwait(false);

        await StreamArrayFieldAsync(
                writer,
                "models",
                options.IncludeModels,
                omitEmpty: false,
                (lastId, ct) => source.FetchModelBatchAsync(lastId, BatchSize, ct),
                static row => ModelElement(row),
                cancellationToken)
            .ConfigureAwait(false);

        await StreamArrayFieldAsync(
                writer,
                "channel_model_prices",
                options.IncludeModelPrices,
                omitEmpty: true,
                (lastId, ct) => source.FetchChannelModelPriceBatchAsync(lastId, BatchSize, ct),
                static row => ChannelModelPriceElement(row),
                cancellationToken)
            .ConfigureAwait(false);

        await StreamArrayFieldAsync(
                writer,
                "api_keys",
                options.IncludeApiKeys,
                omitEmpty: true,
                (lastId, ct) => source.FetchApiKeyBatchAsync(lastId, BatchSize, ct),
                static row => ApiKeyElement(row),
                cancellationToken)
            .ConfigureAwait(false);

        await StreamArrayFieldAsync(
                writer,
                "usage_requests",
                options.IncludeRequestLogs,
                omitEmpty: true,
                (lastId, ct) => source.FetchUsageRequestBatchAsync(
                    lastId,
                    BatchSize,
                    options.IncludeApiKeys,
                    ct),
                row => UsageRequestElement(row, options.IncludeApiKeys),
                cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyDictionary<long, string> apiKeyKeys =
            options.IncludeUsageStats && options.IncludeApiKeys
                ? await source.FetchApiKeyKeyMapAsync(cancellationToken).ConfigureAwait(false)
                : new Dictionary<long, string>();

        await StreamArrayFieldAsync(
                writer,
                "usage_logs",
                options.IncludeUsageStats,
                omitEmpty: true,
                (lastId, ct) => source.FetchUsageLogBatchAsync(lastId, BatchSize, ct),
                row => UsageLogElement(row, apiKeyKeys),
                cancellationToken)
            .ConfigureAwait(false);

        await writer.WriteAsync("}", cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task StreamArrayFieldAsync<T>(
        FragmentWriter writer,
        string name,
        bool on,
        bool omitEmpty,
        Func<long, CancellationToken, Task<(IReadOnlyList<T> Rows, long NextId)>> fetch,
        Func<T, string?> element,
        CancellationToken cancellationToken)
    {
        if (!on)
        {
            if (!omitEmpty)
            {
                await writer.FieldRawAsync(name, "null", cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        long lastId = 0;
        var opened = false;

        try
        {
            while (true)
            {
                var (rows, nextId) = await fetch(lastId, cancellationToken).ConfigureAwait(false);
                if (rows.Count == 0)
                {
                    break;
                }

                foreach (var row in rows)
                {
                    var json = element(row);
                    if (json is null)
                    {
                        continue;
                    }

                    if (!opened)
                    {
                        await writer.BeginArrayFieldAsync(name, cancellationToken).ConfigureAwait(false);
                        opened = true;
                    }
                    else
                    {
                        await writer.WriteAsync(",", cancellationToken).ConfigureAwait(false);
                    }

                    await writer.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                }

                lastId = nextId;
                if (rows.Count < BatchSize)
                {
                    break;
                }
            }
        }
        catch
        {
            // backup_ops.go closes the array on a mid-stream fetch/element
            // error before returning it (`_ = w.WriteString("]")`), ignoring
            // the close-write failure so the original error survives.
            if (opened)
            {
                try
                {
                    await writer.WriteAsync("]", CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Ignored like the reference.
                }
            }

            throw;
        }

        if (opened)
        {
            await writer.WriteAsync("]", cancellationToken).ConfigureAwait(false);
        }
        else if (!omitEmpty)
        {
            await writer.FieldRawAsync(name, "[]", cancellationToken).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- elements

    private static string? SystemElement(SystemRecord row)
        => new GoObjectBuilder()
            .Field("key", Quote(row.Key))
            .Field("value", Quote(row.Value))
            .Build();

    private static string? ProjectElement(ProjectRecord row)
    {
        var builder = EntityPrefix(row.Id, row.CreatedAt, row.UpdatedAt, row.DeletedAt)
            .StringField("name", row.Name)
            .StringField("description", row.Description)
            .StringField("status", row.Status);

        if (ProfilesJson(row.Profiles) is { } profiles)
        {
            builder.Raw("profiles", profiles);
        }

        return builder.Field("edges", "{}").Build();
    }

    private static string? ChannelElement(ChannelRecord row)
    {
        var builder = EntityPrefix(row.Id, row.CreatedAt, row.UpdatedAt, row.DeletedAt)
            .StringField("type", row.Type)
            .StringField("base_url", row.BaseUrl ?? string.Empty)
            .StringField("name", row.Name)
            .StringField("status", row.Status)
            .StringListField("supported_models", ParseStringList(row.SupportedModels))
            .StringListField("manual_models", ParseStringList(row.ManualModels))
            .BoolField("auto_sync_supported_models", row.AutoSyncSupportedModels)
            .StringField("auto_sync_model_pattern", row.AutoSyncModelPattern ?? string.Empty)
            .StringListField("tags", ParseStringList(row.Tags))
            .StringField("default_test_model", row.DefaultTestModel)
            .Field("policies", GoJson.MarshalToString(ParsePolicies(row.Policies)));

        if (ChannelWriteSerialization.ParseSettings(row.Settings) is { } settings)
        {
            builder.Field("settings", GoJson.MarshalToString(settings));
        }

        builder.NumberField("ordering_weight", row.OrderingWeight);

        if (row.ErrorMessage is not null)
        {
            builder.Field("error_message", Quote(row.ErrorMessage));
        }

        if (row.AutoDisabledAt is { } autoDisabledAt)
        {
            builder.Field("auto_disabled_at", Quote(GoTime.Rfc3339Nano(autoDisabledAt)));
        }

        if (row.AutoDisableExpiresAt is { } autoDisableExpiresAt)
        {
            builder.Field(
                "auto_disable_expires_at",
                Quote(GoTime.Rfc3339Nano(autoDisableExpiresAt)));
        }

        if (row.Remark is not null)
        {
            builder.Field("remark", Quote(row.Remark));
        }

        if (ChannelWriteSerialization.ParseEndpoints(row.Endpoints) is { Count: > 0 } endpoints)
        {
            builder.Field("endpoints", GoJson.MarshalToString(endpoints));
        }

        builder.Field("edges", "{}");
        builder.Field(
            "credentials",
            GoJson.MarshalToString(ChannelWriteSerialization.ParseCredentials(row.Credentials)));

        return builder.Build();
    }

    private static string? ModelElement(ModelRecord row)
    {
        var builder = EntityPrefix(row.Id, row.CreatedAt, row.UpdatedAt, row.DeletedAt)
            .StringField("developer", row.Developer)
            .StringField("model_id", row.ModelId)
            .StringField("type", row.Type)
            .StringField("name", row.Name)
            .StringField("icon", row.Icon)
            .StringField("group", row.Group);

        if (ModelCardJson(row.ModelCard) is { } modelCard)
        {
            builder.Raw("model_card", modelCard);
        }

        if (ModelSettingsJson(row.Settings) is { } settings)
        {
            builder.Raw("settings", settings);
        }

        builder.StringField("status", row.Status);

        if (row.Remark is not null)
        {
            builder.Field("remark", Quote(row.Remark));
        }

        return builder.Build();
    }

    private static string? ChannelModelPriceElement(BackupChannelModelPriceRow row)
    {
        // The reference skips the element when the channel edge is missing
        // (soft-deleted or absent channel).
        if (row.ChannelName is null)
        {
            return null;
        }

        return new GoObjectBuilder()
            .Field("channel_name", Quote(row.ChannelName))
            .Field("model_id", Quote(row.Price.ModelId))
            .Raw("price", ParsePriceJson(row.Price.Price))
            .Field("reference_id", Quote(row.Price.ReferenceId))
            .Build();
    }

    private static string? ApiKeyElement(BackupApiKeyRow row)
    {
        var key = row.ApiKey;
        var builder = EntityPrefix(key.Id, key.CreatedAt, key.UpdatedAt, key.DeletedAt)
            .NumberField("user_id", key.UserId ?? 0)
            .NumberField("project_id", key.ProjectId)
            .StringField("key", key.Key)
            .StringField("name", key.Name)
            .StringField("type", key.Type)
            .StringField("status", key.Status)
            .StringListField("scopes", ParseStringList(key.Scopes));

        if (ApiKeyProfilesJson(key.Profiles) is { } profiles)
        {
            builder.Raw("profiles", profiles);
        }

        builder.StringListField("allowed_ips", ParseStringList(key.AllowedIps));

        if (row.Project is { } project)
        {
            builder.Field("edges", new GoObjectBuilder()
                .Raw("project", ProjectElement(project)!)
                .Build());
        }
        else
        {
            builder.Field("edges", "{}");
        }

        builder.Field("project_name", Quote(row.Project?.Name ?? string.Empty));

        return builder.Build();
    }

    private static string? UsageRequestElement(BackupUsageRequestRow row, bool includeApiKeys)
    {
        var request = row.Request;
        var builder = new GoObjectBuilder()
            .NumberField("id", request.Id)
            .TimeFieldOmitZero("created_at", request.CreatedAt)
            .TimeFieldOmitZero("updated_at", request.UpdatedAt)
            .NumberField("project_id", request.ProjectId)
            .StringField("source", request.Source)
            .StringField("model_id", request.ModelId)
            .StringField("reasoning_effort", request.ReasoningEffort ?? string.Empty)
            .StringField("format", request.Format);

        if (RawMessage(request.RequestHeaders) is { } requestHeaders)
        {
            builder.Raw("request_headers", requestHeaders);
        }

        if (RawMessage(request.ResponseHeaders) is { } responseHeaders)
        {
            builder.Raw("response_headers", responseHeaders);
        }

        if (RawMessage(request.RequestBody) is { } requestBody)
        {
            builder.Raw("request_body", requestBody);
        }

        if (RawMessage(request.ResponseBody) is { } responseBody)
        {
            builder.Raw("response_body", responseBody);
        }

        if (RawMessageArray(request.ResponseChunks) is { } responseChunks)
        {
            builder.Raw("response_chunks", responseChunks);
        }

        builder
            .NumberField("channel_id", request.ChannelId ?? 0)
            .StringField("external_id", request.ExternalId ?? string.Empty)
            .StringField("status", request.Status)
            .BoolField("stream", request.Stream)
            .StringField("client_ip", request.ClientIp);

        if (request.MetricsLatencyMs is { } latency)
        {
            builder.Field("metrics_latency_ms", GoJson.MarshalToString(latency));
        }

        if (request.MetricsFirstTokenLatencyMs is { } firstToken)
        {
            builder.Field("metrics_first_token_latency_ms", GoJson.MarshalToString(firstToken));
        }

        if (request.MetricsReasoningDurationMs is { } reasoningDuration)
        {
            builder.Field(
                "metrics_reasoning_duration_ms",
                GoJson.MarshalToString(reasoningDuration));
        }

        builder.BoolField("content_saved", request.ContentSaved);

        if (request.ContentStorageId is { } contentStorageId)
        {
            builder.Field("content_storage_id", GoJson.MarshalToString(contentStorageId));
        }

        if (request.ContentStorageKey is not null)
        {
            builder.Field("content_storage_key", Quote(request.ContentStorageKey));
        }

        if (request.ContentSavedAt is { } contentSavedAt)
        {
            builder.Field("content_saved_at", Quote(GoTime.Rfc3339Nano(contentSavedAt)));
        }

        if (!string.IsNullOrEmpty(row.ProjectName))
        {
            builder.Field("project_name", Quote(row.ProjectName));
        }

        if (!string.IsNullOrEmpty(row.ChannelName))
        {
            builder.Field("channel_name", Quote(row.ChannelName));
        }

        if (includeApiKeys && !string.IsNullOrEmpty(row.ApiKeyKey))
        {
            builder.Field("api_key_key", Quote(row.ApiKeyKey));
        }

        return builder.Build();
    }

    private static string? UsageLogElement(
        BackupUsageLogRow row,
        IReadOnlyDictionary<long, string> apiKeyKeys)
    {
        var log = row.Log;
        var builder = new GoObjectBuilder()
            .NumberField("id", log.Id)
            .TimeFieldOmitZero("created_at", log.CreatedAt)
            .TimeFieldOmitZero("updated_at", log.UpdatedAt)
            .NumberField("request_id", log.RequestId)
            .NumberField("project_id", log.ProjectId)
            .NumberField("channel_id", log.ChannelId ?? 0)
            .StringField("model_id", log.ModelId)
            .NumberField("prompt_tokens", log.PromptTokens)
            .NumberField("completion_tokens", log.CompletionTokens)
            .NumberField("total_tokens", log.TotalTokens)
            .NumberField("prompt_audio_tokens", log.PromptAudioTokens ?? 0)
            .NumberField("prompt_cached_tokens", log.PromptCachedTokens ?? 0)
            .NumberField("prompt_write_cached_tokens", log.PromptWriteCachedTokens ?? 0)
            .NumberField("prompt_write_cached_tokens_5m", log.PromptWriteCachedTokens5m ?? 0)
            .NumberField("prompt_write_cached_tokens_1h", log.PromptWriteCachedTokens1h ?? 0)
            .NumberField("completion_audio_tokens", log.CompletionAudioTokens ?? 0)
            .NumberField("completion_reasoning_tokens", log.CompletionReasoningTokens ?? 0)
            .NumberField(
                "completion_accepted_prediction_tokens",
                log.CompletionAcceptedPredictionTokens ?? 0)
            .NumberField(
                "completion_rejected_prediction_tokens",
                log.CompletionRejectedPredictionTokens ?? 0)
            .StringField("source", log.Source)
            .StringField("format", log.Format);

        if (log.TotalCost is { } totalCost)
        {
            builder.Field("total_cost", GoJsonFloat.Format(totalCost));
        }

        if (CostItemsJson(log.CostItems) is { } costItems)
        {
            builder.Raw("cost_items", costItems);
        }

        builder.StringField("cost_price_reference_id", log.CostPriceReferenceId ?? string.Empty);

        if (!string.IsNullOrEmpty(row.ProjectName))
        {
            builder.Field("project_name", Quote(row.ProjectName));
        }

        if (!string.IsNullOrEmpty(row.ChannelName))
        {
            builder.Field("channel_name", Quote(row.ChannelName));
        }

        if (log.ApiKeyId is { } apiKeyId && apiKeyId != 0
            && apiKeyKeys.GetValueOrDefault(apiKeyId, string.Empty) is { Length: > 0 } apiKeyKey)
        {
            builder.Field("api_key_key", Quote(apiKeyKey));
        }

        return builder.Build();
    }

    // ----------------------------------------------------------------- helpers

    private static GoObjectBuilder EntityPrefix(
        long id,
        DateTime createdAt,
        DateTime updatedAt,
        long deletedAt)
    {
        var builder = new GoObjectBuilder();

        if (id != 0)
        {
            builder.Field("id", GoJson.MarshalToString(id));
        }

        // time.Time has no omitempty effect (value struct).
        builder
            .Field("created_at", Quote(GoTime.Rfc3339Nano(createdAt)))
            .Field("updated_at", Quote(GoTime.Rfc3339Nano(updatedAt)));

        if (deletedAt != 0)
        {
            builder.Field("deleted_at", GoJson.MarshalToString(deletedAt));
        }

        return builder;
    }

    private static string Quote(string value) => GoJson.MarshalToString(value);

    internal static string FormatLocalRfc3339Nano(DateTime value)
    {
        var local = value.Kind switch
        {
            DateTimeKind.Utc => value.ToLocalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Local),
            _ => value,
        };

        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        var text = local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        var fraction = local.Ticks % TimeSpan.TicksPerSecond;
        if (fraction != 0)
        {
            text += "." + fraction.ToString("D7", CultureInfo.InvariantCulture).TrimEnd('0');
        }

        if (offset == TimeSpan.Zero)
        {
            return text + "Z";
        }

        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var absolute = offset.Duration();
        return text + sign
            + absolute.Hours.ToString("D2", CultureInfo.InvariantCulture)
            + ":"
            + absolute.Minutes.ToString("D2", CultureInfo.InvariantCulture);
    }

    private static List<string>? ParseStringList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return null;
        }

        return JsonSerializer.Deserialize<List<string?>>(raw, LlmJson.Options)?
            .Select(static value => value ?? string.Empty)
            .ToList();
    }

    private static ChannelPoliciesDraft ParsePolicies(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return new ChannelPoliciesDraft();
        }

        var policies = LlmJson.Deserialize<ChannelPoliciesDraft>(raw) ?? new ChannelPoliciesDraft();
        policies.Stream = NullIfEmpty(policies.Stream);
        policies.ApiKeyAutoDisableMode = NullIfEmpty(policies.ApiKeyAutoDisableMode);
        if (policies.ApiKeyAutoDisableRules is { Count: 0 })
        {
            policies.ApiKeyAutoDisableRules = null;
        }

        return policies;
    }

    private static string? ProfilesJson(string? raw)
    {
        if (IsNullColumn(raw))
        {
            return null;
        }

        return ProjectProfilesCodec.Serialize(ProjectProfilesCodec.Deserialize(raw));
    }

    private static string? ApiKeyProfilesJson(string? raw)
    {
        if (IsNullColumn(raw))
        {
            return null;
        }

        return ApiKeyProfilesCodec.Serialize(ApiKeyProfilesCodec.Deserialize(raw));
    }

    private static string? ModelCardJson(string? raw)
    {
        if (IsNullColumn(raw))
        {
            return null;
        }

        return ModelCardCodec.Deserialize(raw) is { } card
            ? ModelCardCodec.Serialize(card)
            : null;
    }

    private static string? ModelSettingsJson(string? raw)
    {
        if (IsNullColumn(raw))
        {
            return null;
        }

        return ModelSettingsCodec.DeserializeEntitySettings(raw) is { } settings
            ? ModelSettingsCodec.SerializeEntitySettings(settings)
            : null;
    }

    private static string ParsePriceJson(string? raw)
    {
        if (IsNullColumn(raw))
        {
            return GoJson.MarshalToString(new ModelPrice());
        }

        var price = JsonSerializer.Deserialize<ModelPrice>(raw!, BillingJson.Options)
            ?? new ModelPrice();
        return GoJson.MarshalToString(price);
    }

    private static string? RawMessage(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        return Encoding.UTF8.GetString(GoJson.MarshalRaw(Encoding.UTF8.GetBytes(raw)));
    }

    private static string? RawMessageArray(string? raw)
    {
        if (IsNullColumn(raw))
        {
            return null;
        }

        var elements = JsonSerializer.Deserialize<List<JsonElement>>(raw!, LlmJson.Options);
        if (elements is not { Count: > 0 })
        {
            return null;
        }

        var builder = new StringBuilder("[");
        for (var i = 0; i < elements.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(RawMessage(elements[i].GetRawText()));
        }

        return builder.Append(']').ToString();
    }

    private static string? CostItemsJson(string? raw)
    {
        if (IsNullColumn(raw))
        {
            return null;
        }

        var items = JsonSerializer.Deserialize<List<CostItem>>(raw!, BillingJson.Options);
        return items is { Count: > 0 } ? GoJson.MarshalToString(items) : null;
    }

    private static bool IsNullColumn(string? raw)
        => string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal);

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>Incremental object writer with Go's comma placement.</summary>
    internal sealed class GoObjectBuilder
    {
        private readonly StringBuilder _builder = new("{");
        private bool _needComma;

        public GoObjectBuilder Field(string name, string rawJson)
        {
            if (_needComma)
            {
                _builder.Append(',');
            }

            _needComma = true;
            _builder.Append(Quote(name)).Append(':').Append(rawJson);
            return this;
        }

        public GoObjectBuilder Raw(string name, string rawJson) => Field(name, rawJson);

        /// <summary>Go string with omitempty.</summary>
        public GoObjectBuilder StringField(string name, string value)
            => value.Length == 0 ? this : Field(name, Quote(value));

        /// <summary>Go []string with omitempty (nil and empty are dropped).</summary>
        public GoObjectBuilder StringListField(string name, IReadOnlyList<string>? values)
            => values is { Count: > 0 } ? Field(name, GoJson.MarshalToString(values)) : this;

        /// <summary>Go int/bool with omitempty (zero is dropped).</summary>
        public GoObjectBuilder NumberField(string name, long value)
            => value == 0 ? this : Field(name, GoJson.MarshalToString(value));

        public GoObjectBuilder BoolField(string name, bool value)
            => value ? Field(name, "true") : this;

        /// <summary>Go time.Time without omitempty effect.</summary>
        public GoObjectBuilder TimeField(string name, DateTime value)
            => Field(name, Quote(GoTime.Rfc3339Nano(value)));

        /// <summary>Go time.Time with omitzero (the zero value is dropped).</summary>
        public GoObjectBuilder TimeFieldOmitZero(string name, DateTime value)
            => value == default ? this : Field(name, Quote(GoTime.Rfc3339Nano(value)));

        public string Build() => _builder.Append('}').ToString();
    }

    /// <summary>Comma-aware streaming writer of pre-escaped JSON fragments.</summary>
    internal sealed class FragmentWriter(Stream output)
    {
        private bool _needComma;

        public async Task WriteAsync(string fragment, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(fragment);
            await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        public async Task FieldRawAsync(
            string name,
            string rawJson,
            CancellationToken cancellationToken)
        {
            await WriteAsync((_needComma ? "," : string.Empty) + Quote(name) + ":", cancellationToken)
                .ConfigureAwait(false);
            _needComma = true;
            await WriteAsync(rawJson, cancellationToken).ConfigureAwait(false);
        }

        public async Task BeginArrayFieldAsync(string name, CancellationToken cancellationToken)
        {
            await WriteAsync((_needComma ? "," : string.Empty) + Quote(name) + ":[", cancellationToken)
                .ConfigureAwait(false);
            _needComma = true;
        }

        public Task FlushAsync(CancellationToken cancellationToken)
            => output.FlushAsync(cancellationToken);
    }
}
