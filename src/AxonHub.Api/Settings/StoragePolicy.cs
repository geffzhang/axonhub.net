namespace AxonHub.Api.Settings;

/// <summary>
/// Port of the biz.CleanupResource* constants (internal/server/biz/system.go:258-268).
/// </summary>
public static class CleanupResourceTypes
{
    /// <summary>Deletes request rows, executions, traces, and threads.</summary>
    public const string Requests = "requests";

    /// <summary>Deletes usage log rows.</summary>
    public const string UsageLogs = "usage_logs";

    /// <summary>Strips stored request bodies and headers only.</summary>
    public const string RequestBodies = "request_bodies";

    /// <summary>Strips stored response bodies only.</summary>
    public const string ResponseBodies = "response_bodies";

    /// <summary>Strips stored stream chunks only.</summary>
    public const string ResponseChunks = "response_chunks";
}

/// <summary>Port of biz.CleanupOption (internal/server/biz/system.go:246-250).</summary>
public sealed record CleanupOption(string ResourceType, bool Enabled, int CleanupDays);

/// <summary>
/// Port of biz.StoragePolicy (internal/server/biz/system.go:237-244). The
/// GraphQL <c>UpdateStoragePolicyInput</c> is decoded straight into this shape,
/// so an absent input field falls back to the Go zero value.
/// </summary>
public sealed record StoragePolicy
{
    public bool StoreChunks { get; init; }

    public bool LivePreview { get; init; }

    public bool StoreRequestBody { get; init; }

    public bool StoreResponseBody { get; init; }

    /// <summary>Null stands in for a JSON <c>null</c>/absent list.</summary>
    public IReadOnlyList<CleanupOption>? CleanupOptions { get; init; }

    /// <summary>Port of biz.defaultCleanupOptions (system_default.go:11-39).</summary>
    public static IReadOnlyList<CleanupOption> DefaultCleanupOptions() =>
    [
        new(CleanupResourceTypes.Requests, Enabled: false, CleanupDays: 3),
        new(CleanupResourceTypes.UsageLogs, Enabled: false, CleanupDays: 30),
        new(CleanupResourceTypes.RequestBodies, Enabled: false, CleanupDays: 7),
        new(CleanupResourceTypes.ResponseBodies, Enabled: false, CleanupDays: 7),
        new(CleanupResourceTypes.ResponseChunks, Enabled: false, CleanupDays: 3),
    ];

    /// <summary>Port of biz.defaultStoragePolicy (system_default.go:71-77).</summary>
    public static StoragePolicy Default { get; } = new()
    {
        StoreChunks = false,
        LivePreview = false,
        StoreRequestBody = true,
        StoreResponseBody = true,
        CleanupOptions = DefaultCleanupOptions(),
    };

    /// <summary>
    /// Port of biz.mergeCleanupOptions (system_default.go:41-69): keep existing
    /// entries in their first-seen order (a later duplicate wins the value) and
    /// append the defaults that are missing.
    /// </summary>
    public static IReadOnlyList<CleanupOption> MergeCleanupOptions(
        IReadOnlyList<CleanupOption>? existing)
    {
        var byType = new Dictionary<string, CleanupOption>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var option in existing ?? [])
        {
            if (!byType.ContainsKey(option.ResourceType))
            {
                order.Add(option.ResourceType);
            }

            byType[option.ResourceType] = option;
        }

        foreach (var fallback in DefaultCleanupOptions())
        {
            if (byType.ContainsKey(fallback.ResourceType))
            {
                continue;
            }

            byType[fallback.ResourceType] = fallback;
            order.Add(fallback.ResourceType);
        }

        return order.Select(static resourceType => byType[resourceType]).ToList();
    }
}
