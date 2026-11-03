using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Llm.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Management;

/// <summary>
/// Builds the ent channel row produced by createChannel's builder
/// (channel.go:617-631). Every column follows ent's presence rules:
/// <c>SetSettings(nil)</c>/<c>SetManualModels(nil)</c> store JSON <c>null</c>
/// (not the schema default), the module defaults write
/// <c>{"stream":"unlimited"}</c> for policies and <c>[]</c> for tags/endpoints,
/// and <c>orderingWeight</c> is never set.
/// </summary>
public static class ChannelWriteSerialization
{
    /// <summary>objects.ChannelPolicies zero value's JSON form (ent module default).</summary>
    public const string DefaultPoliciesJson = """{"stream":"unlimited"}""";

    /// <summary>
    /// ent's channel settings schema default
    /// (<c>&amp;objects.ChannelSettings{ModelMappings: []}</c>). Bulk import
    /// never sets the column, so this default is inserted.
    /// </summary>
    public const string DefaultSettingsJson = """{"modelMappings":[]}""";

    /// <summary>
    /// Decodes a stored settings column the way ent's JSON scanner does
    /// (Go's json.Unmarshal into <c>*objects.ChannelSettings</c>): JSON null is
    /// a nil pointer, and fields the Go struct marks <c>omitempty</c> are
    /// normalized back to null so a later marshal drops them again.
    /// </summary>
    /// <exception cref="System.Text.Json.JsonException">The stored JSON is malformed.</exception>
    public static ChannelSettingsDraft? ParseSettings(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return null;
        }

        var settings = LlmJson.Deserialize<ChannelSettingsDraft>(raw);
        if (settings is null)
        {
            return null;
        }

        PruneGoOmitEmpty(settings);
        return settings;
    }

    /// <summary>
    /// Decodes stored settings for the override-template Apply path. The
    /// reference copies the ent-scanned settings and its
    /// getHeaderOverrideOperations / getBodyOverrideOperations helpers test the
    /// operation slices against nil, so an explicitly stored empty array wins
    /// over the legacy fields. <see cref="ParseSettings"/> prunes empty lists
    /// for the re-marshal omitempty behaviour, so their presence is restored
    /// here for the two operation fields only.
    /// </summary>
    /// <exception cref="System.Text.Json.JsonException">The stored JSON is malformed.</exception>
    public static ChannelSettingsDraft? ParseSettingsForOverrideApply(string? raw)
    {
        var settings = ParseSettings(raw);
        if (settings is null
            || string.IsNullOrWhiteSpace(raw)
            || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return settings;
        }

        using var document = JsonDocument.Parse(raw);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            if (IsEmptyArray(document.RootElement, "headerOverrideOperations"))
            {
                settings.HeaderOverrideOperations = [];
            }

            if (IsEmptyArray(document.RootElement, "bodyOverrideOperations"))
            {
                settings.BodyOverrideOperations = [];
            }
        }

        return settings;
    }

    private static bool IsEmptyArray(JsonElement element, string name)
        => element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Array
            && value.GetArrayLength() == 0;

    /// <summary>
    /// Decodes a stored credentials column into the write draft. Go unmarshals
    /// <c>null</c> into the value struct as a no-op, yielding the zero value.
    /// </summary>
    /// <exception cref="System.Text.Json.JsonException">The stored JSON is malformed.</exception>
    public static ChannelCredentialsDraft ParseCredentials(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return new ChannelCredentialsDraft();
        }

        var credentials = LlmJson.Deserialize<ChannelCredentialsDraft>(raw)
            ?? new ChannelCredentialsDraft();
        credentials.ApiKey = NullIfEmpty(credentials.ApiKey);
        credentials.ManagementApiKey = NullIfEmpty(credentials.ManagementApiKey);
        credentials.ApiKeys = credentials.ApiKeys is { Count: > 0 } ? credentials.ApiKeys : null;
        credentials.VertexApiKeys = credentials.VertexApiKeys is { Count: > 0 }
            ? credentials.VertexApiKeys
            : null;
        return credentials;
    }

    /// <summary>
    /// Decodes a stored endpoints column the way ent does: JSON null leaves the
    /// Go slice nil.
    /// </summary>
    /// <exception cref="System.Text.Json.JsonException">The stored JSON is malformed.</exception>
    public static IReadOnlyList<ChannelEndpointDraft>? ParseEndpoints(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return null;
        }

        var endpoints = LlmJson.Deserialize<List<ChannelEndpointDraft>>(raw);
        if (endpoints is null)
        {
            return null;
        }

        foreach (var endpoint in endpoints)
        {
            endpoint.Path = NullIfEmpty(endpoint.Path);
            endpoint.BaseUrl = NullIfEmpty(endpoint.BaseUrl);
            endpoint.Transport = NullIfEmpty(endpoint.Transport);
        }

        return endpoints;
    }

    /// <summary>
    /// Decodes the stored disabled_api_keys column the way ent's JSON scanner
    /// does (Go's json.Unmarshal into a <c>[]objects.DisabledAPIKey</c>): null
    /// and an absent column are the empty list. Go folds field names
    /// case-insensitively and treats <c>omitempty</c> strings as absent, so an
    /// empty reason is normalized back to null.
    /// </summary>
    /// <exception cref="System.Text.Json.JsonException">The stored JSON is malformed.</exception>
    public static List<DisabledApiKey> ParseDisabledApiKeys(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "null", StringComparison.Ordinal))
        {
            return [];
        }

        var keys = LlmJson.Deserialize<List<DisabledApiKey?>>(raw) ?? [];

        // JSON null elements decode into the zero Go struct rather than null;
        // System.Text.Json keeps them as null list entries.
        var result = new List<DisabledApiKey>(keys.Count);
        foreach (var key in keys)
        {
            var entry = key ?? new DisabledApiKey();
            entry.Reason = NullIfEmpty(entry.Reason);
            result.Add(entry);
        }

        return result;
    }

    /// <summary>
    /// Marshals disabled_api_keys with the reference's struct tags: key,
    /// disabledAt (RFC 3339 UTC), errorCode, then the omitted-when-empty reason
    /// and expiresAt. An empty list stores <c>[]</c>.
    /// </summary>
    public static string SerializeDisabledApiKeys(IReadOnlyList<DisabledApiKey> keys)
        => GoJson.MarshalToString(keys);

    private static void PruneGoOmitEmpty(ChannelSettingsDraft settings)
    {
        settings.BodyOverrideOperations = NonEmpty(settings.BodyOverrideOperations);
        settings.HeaderOverrideOperations = NonEmpty(settings.HeaderOverrideOperations);
        settings.RetryableStatusCodes = NonEmpty(settings.RetryableStatusCodes);
        settings.RetryableErrorPatterns = NonEmpty(settings.RetryableErrorPatterns);
        settings.ModelProtocols = NonEmpty(settings.ModelProtocols);
        settings.ApiKeyStrategy = NullIfEmpty(settings.ApiKeyStrategy);
        settings.QuotaRoutingMode = NullIfEmpty(settings.QuotaRoutingMode);

        // RetryableErrorPattern.Regex is a plain bool with omitempty: false is
        // dropped, true is kept.
        if (settings.RetryableErrorPatterns is { } patterns)
        {
            foreach (var pattern in patterns)
            {
                if (pattern.Regex is false)
                {
                    pattern.Regex = null;
                }
            }
        }

        if (settings.ProviderQuota is { } providerQuota)
        {
            if (providerQuota.CommandCode is { } commandCode)
            {
                commandCode.AuthCookie = NullIfEmpty(commandCode.AuthCookie);
            }

            if (providerQuota.Ollama is { } ollama)
            {
                ollama.AuthCookie = NullIfEmpty(ollama.AuthCookie);
            }
        }

        if (settings.Proxy is { } proxy)
        {
            proxy.Url = NullIfEmpty(proxy.Url);
            proxy.Username = NullIfEmpty(proxy.Username);
            proxy.Password = NullIfEmpty(proxy.Password);
            if (proxy.DisableConnectionReuse is false)
            {
                proxy.DisableConnectionReuse = null;
            }
        }

        settings.TransformOptions.ReasoningEffortMapping =
            NonEmpty(settings.TransformOptions.ReasoningEffortMapping);
    }

    private static IReadOnlyList<T>? NonEmpty<T>(IReadOnlyList<T>? values)
        => values is { Count: > 0 } ? values : null;

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrEmpty(value) ? null : value;

    public static ChannelRecord ToRecord(CreateChannelCommand command, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(command);

        return new ChannelRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = 0,
            Type = command.Type,
            BaseUrl = command.BaseUrl,
            Name = command.Name,
            Status = "disabled",
            Credentials = GoJson.MarshalToString(command.Credentials),
            DisabledApiKeys = "[]",
            SupportedModels = GoJson.MarshalToString(command.SupportedModels),

            // SetManualModels(nil) marks the column present with a nil value:
            // the stored JSON is null, suppressing the default [].
            ManualModels = command.ManualModels is null
                ? "null"
                : GoJson.MarshalToString(command.ManualModels),
            AutoSyncSupportedModels = command.AutoSyncSupportedModels ?? false,
            AutoSyncModelPattern = command.AutoSyncModelPattern ?? string.Empty,
            Tags = command.Tags is null ? "[]" : GoJson.MarshalToString(command.Tags),
            DefaultTestModel = command.DefaultTestModel,

            // SetPolicies only runs for a non-nil pointer; otherwise the ent
            // default applies.
            Policies = command.Policies is null
                ? DefaultPoliciesJson
                : GoJson.MarshalToString(command.Policies),

            // SetSettings(nil) stores JSON null, suppressing the schema
            // default {"modelMappings":[]}.
            Settings = command.Settings is null ? "null" : GoJson.MarshalToString(command.Settings),
            OrderingWeight = 0,
            Remark = command.Remark,
            Endpoints = command.Endpoints is null
                ? "[]"
                : GoJson.MarshalToString(command.Endpoints),
        };
    }

    /// <summary>
    /// Builds the row of one BulkImportChannels item
    /// (channel_bulk.go:520-527). The reference calls the ent builder directly
    /// and sets only type/name/baseURL/credentials/supportedModels/
    /// defaultTestModel, so every other column takes the schema default:
    /// status disabled, empty lists for disabled keys/manual models/tags/
    /// endpoints, policies <c>{"stream":"unlimited"}</c>, settings
    /// <c>{"modelMappings":[]}</c>, and the zero sync/ordering values.
    /// </summary>
    public static ChannelRecord ToImportRecord(
        BulkImportChannelItem item,
        DateTime now)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new ChannelRecord
        {
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = 0,
            Type = item.Type,
            BaseUrl = item.BaseUrl,
            Name = item.Name,
            Status = "disabled",
            Credentials = GoJson.MarshalToString(
                new ChannelCredentialsDraft { ApiKey = item.ApiKey }),
            DisabledApiKeys = "[]",
            SupportedModels = GoJson.MarshalToString(item.SupportedModels),
            ManualModels = "[]",
            AutoSyncSupportedModels = false,
            AutoSyncModelPattern = string.Empty,
            Tags = "[]",
            DefaultTestModel = item.DefaultTestModel,
            Policies = DefaultPoliciesJson,
            Settings = DefaultSettingsJson,
            OrderingWeight = 0,
            Endpoints = "[]",
        };
    }
}
