using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;

namespace AxonHub.Api.Settings;

/// <summary>
/// Go-compatible JSON codec for the <c>storage_policy</c> system setting
/// (internal/server/biz/system.go:980-1039, system_default.go:11-77).
/// </summary>
public static class StoragePolicyCodec
{
    /// <summary>biz.SystemKeyStoragePolicy.</summary>
    public const string Key = "storage_policy";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        // Go's json.Unmarshal matches field names case-insensitively.
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Port of SystemService.StoragePolicy's decode path: missing booleans stay
    /// false, the two body flags default to true when their keys are absent,
    /// and the cleanup list is merged with the defaults.
    /// </summary>
    /// <exception cref="JsonException">The document cannot be decoded.</exception>
    public static StoragePolicy Deserialize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var stored = JsonSerializer.Deserialize<StoragePolicyJson>(value, ReadOptions);

        var policy = new StoragePolicy
        {
            StoreChunks = stored?.StoreChunks ?? false,
            LivePreview = stored?.LivePreview ?? false,
            StoreRequestBody = stored?.StoreRequestBody ?? false,
            StoreResponseBody = stored?.StoreResponseBody ?? false,
            CleanupOptions = stored?.CleanupOptions?
                .Select(static option => option is null
                    ? new CleanupOption(string.Empty, Enabled: false, CleanupDays: 0)
                    : new CleanupOption(
                        option.ResourceType ?? string.Empty,
                        option.Enabled ?? false,
                        option.CleanupDays ?? 0))
                .ToList(),
        };

        // Backward compatibility: a document written before these keys existed
        // keeps the old body-storing behaviour.
        if (!value.Contains("\"store_request_body\"", StringComparison.Ordinal))
        {
            policy = policy with { StoreRequestBody = true };
        }

        if (!value.Contains("\"store_response_body\"", StringComparison.Ordinal))
        {
            policy = policy with { StoreResponseBody = true };
        }

        return policy with
        {
            CleanupOptions = StoragePolicy.MergeCleanupOptions(policy.CleanupOptions),
        };
    }

    /// <summary>Port of json.Marshal(biz.StoragePolicy).</summary>
    public static string Serialize(StoragePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var document = new StoragePolicyJson
        {
            StoreChunks = policy.StoreChunks,
            LivePreview = policy.LivePreview,
            StoreRequestBody = policy.StoreRequestBody,
            StoreResponseBody = policy.StoreResponseBody,
            CleanupOptions = policy.CleanupOptions?
                .Select(static option => (CleanupOptionJson?)new CleanupOptionJson
                {
                    ResourceType = option.ResourceType,
                    Enabled = option.Enabled,
                    CleanupDays = option.CleanupDays,
                })
                .ToList(),
        };

        return GoJson.MarshalToString(document);
    }

    private sealed class StoragePolicyJson
    {
        [JsonPropertyName("store_chunks")]
        public bool? StoreChunks { get; set; }

        [JsonPropertyName("live_preview")]
        public bool? LivePreview { get; set; }

        [JsonPropertyName("store_request_body")]
        public bool? StoreRequestBody { get; set; }

        [JsonPropertyName("store_response_body")]
        public bool? StoreResponseBody { get; set; }

        [JsonPropertyName("cleanup_options")]
        public List<CleanupOptionJson?>? CleanupOptions { get; set; }
    }

    private sealed class CleanupOptionJson
    {
        [JsonPropertyName("resource_type")]
        public string? ResourceType { get; set; }

        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("cleanup_days")]
        public int? CleanupDays { get; set; }
    }
}
