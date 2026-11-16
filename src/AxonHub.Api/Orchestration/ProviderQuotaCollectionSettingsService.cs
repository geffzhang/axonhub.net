using System.Text.Json;
using System.Text.Json.Serialization;
using AxonHub.Llm.Json;
using AxonHub.Persistence;

namespace AxonHub.Api.Orchestration;

/// <summary>
/// Port of biz.ProviderQuotaCollectionSettings
/// (internal/server/biz/provider_quota_settings.go:30-33).
/// </summary>
public sealed class ProviderQuotaCollectionSettings
{
    /// <summary>Global collection switch.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Per-provider switches. A provider missing from the map is disabled, and
    /// the reference's <c>json.Marshal</c> sorts the keys, so
    /// <see cref="SortedDictionary{TKey,TValue}"/> preserves both semantics.
    /// </summary>
    public SortedDictionary<string, bool> Providers { get; init; } =
        new(StringComparer.Ordinal);
}

/// <summary>Port of biz.ProviderQuotaCollectionProvider (settings.go:35-38).</summary>
public sealed class ProviderQuotaCollectionProviderDraft
{
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }
}

/// <summary>
/// JSON shape of <c>provider_quota_collection_settings</c>. Go unmarshals a
/// missing <c>enabled</c> to false and a missing provider map to nil; both are
/// then folded through normalization.
/// </summary>
public sealed class ProviderQuotaCollectionSettingsDraft
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("providers")]
    public Dictionary<string, bool>? Providers { get; init; }
}

/// <summary>
/// Reads and writes the <c>provider_quota_collection_settings</c> system
/// setting (internal/server/biz/provider_quota_settings.go:12-174). Runtime
/// callers use the OrDefault accessor; the management mutation surfaces every
/// failure.
/// </summary>
public interface IProviderQuotaCollectionSettingsService
{
    /// <summary>Port of SystemService.ProviderQuotaCollectionSettings.</summary>
    Task<ProviderQuotaCollectionSettings> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.ProviderQuotaCollectionSettingsOrDefault.</summary>
    Task<ProviderQuotaCollectionSettings> GetOrDefaultAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.UpdateProviderQuotaCollectionSettings.</summary>
    Task UpdateAsync(
        bool? enabled,
        IReadOnlyList<ProviderQuotaCollectionProviderDraft> providers,
        CancellationToken cancellationToken = default);

    /// <summary>Port of SystemService.IsProviderQuotaCollectionEnabled.</summary>
    Task<bool> IsEnabledAsync(string providerType, CancellationToken cancellationToken = default);
}

/// <summary>
/// Store-backed implementation. The reference caches system values in an
/// in-process cache; the port reads PostgreSQL on demand (C029), so every
/// strict read behaves as a cache miss against the current row.
/// </summary>
public sealed class ProviderQuotaCollectionSettingsService(
    ISystemStore systems,
    ProviderQuotaSettingsCache cache)
    : IProviderQuotaCollectionSettingsService
{
    /// <summary>biz.SystemKeyProviderQuotaCollectionSettings.</summary>
    public const string SystemKey = "provider_quota_collection_settings";

    /// <summary>biz.supportedProviderQuotaTypes (settings.go:14-20), in order.</summary>
    public static IReadOnlyList<string> SupportedProviderQuotaTypes { get; } =
    [
        "claudecode", "codex", "antigravity", "github_copilot", "nanogpt", "cline",
        "xai_subscription", "synthetic", "neuralwatt", "apertis", "opencode_go",
        "kimi_code", "minimax", "zhipu", "zai", "charm_hyper", "zenmux", "commandcode",
        "ollama",
    ];

    private static readonly HashSet<string> SupportedProviderQuotaTypeSet =
        [.. SupportedProviderQuotaTypes];

    /// <summary>Port of defaultProviderQuotaCollectionSettings.</summary>
    public static ProviderQuotaCollectionSettings Default()
    {
        var providers = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var providerType in SupportedProviderQuotaTypes)
        {
            providers[providerType] = true;
        }

        return new ProviderQuotaCollectionSettings { Enabled = true, Providers = providers };
    }

    /// <summary>Port of IsSupportedProviderQuotaType.</summary>
    public static bool IsSupportedProviderQuotaType(string? providerType)
        => providerType is not null && SupportedProviderQuotaTypeSet.Contains(providerType);

    /// <summary>Port of normalizeProviderQuotaCollectionSettings.</summary>
    public static ProviderQuotaCollectionSettings Normalize(
        ProviderQuotaCollectionSettingsDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var normalized = Default();
        var providers = new SortedDictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (providerType, enabled) in normalized.Providers)
        {
            providers[providerType] = enabled;
        }

        // Old configurations predate providers added later; those keep their
        // default-on state so an upgrade never silently disables new support.
        if (draft.Providers is not null)
        {
            foreach (var (providerType, enabled) in draft.Providers)
            {
                if (SupportedProviderQuotaTypeSet.Contains(providerType))
                {
                    providers[providerType] = enabled;
                }
            }
        }

        return new ProviderQuotaCollectionSettings
        {
            Enabled = draft.Enabled,
            Providers = providers,
        };
    }

    public async Task<ProviderQuotaCollectionSettings> GetAsync(
        CancellationToken cancellationToken = default)
    {
        string? value;
        try
        {
            var row = await systems.GetByKeyAsync(SystemKey, cancellationToken)
                .ConfigureAwait(false);
            value = row?.Value;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to get provider quota collection settings: failed to get system value: "
                + ex.Message,
                ex);
        }

        if (value is null)
        {
            // ent.IsNotFound: the normalized defaults are returned (and, in the
            // reference, cached for the channel loop).
            var defaults = Default();
            cache.Update(defaults);
            return defaults;
        }

        ProviderQuotaCollectionSettingsDraft? draft;
        try
        {
            draft = LlmJson.Deserialize<ProviderQuotaCollectionSettingsDraft>(value);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "failed to unmarshal provider quota collection settings: " + ex.Message,
                ex);
        }

        var settings = Normalize(draft ?? new ProviderQuotaCollectionSettingsDraft());
        cache.Update(settings);
        return settings;
    }

    public async Task<ProviderQuotaCollectionSettings> GetOrDefaultAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // ProviderQuotaCollectionSettingsOrDefault logs and returns the
            // defaults on every read failure; the synchronous routing gate must
            // see the same value, so the snapshot follows.
            var defaults = Default();
            cache.Update(defaults);
            return defaults;
        }
    }

    public async Task UpdateAsync(
        bool? enabled,
        IReadOnlyList<ProviderQuotaCollectionProviderDraft> providers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);

        var settings = await GetAsync(cancellationToken).ConfigureAwait(false);
        var effectiveEnabled = enabled ?? settings.Enabled;
        var providerMap = new SortedDictionary<string, bool>(settings.Providers, StringComparer.Ordinal);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (!SupportedProviderQuotaTypeSet.Contains(provider.Provider))
            {
                throw new InvalidOperationException(
                    "unsupported provider quota type: " + GoCast.GoQuote(provider.Provider));
            }

            if (!seen.Add(provider.Provider))
            {
                throw new InvalidOperationException(
                    "duplicate provider quota type: " + GoCast.GoQuote(provider.Provider));
            }

            providerMap[provider.Provider] = provider.Enabled;
        }

        var updated = new ProviderQuotaCollectionSettings
        {
            Enabled = effectiveEnabled,
            Providers = providerMap,
        };

        // json.Marshal(ProviderQuotaCollectionSettings): struct field order is
        // enabled then providers; the map keys sort lexicographically.
        var json = AxonHub.Llm.Json.GoJson.MarshalToString(new StoredSettings
        {
            Enabled = updated.Enabled,
            Providers = updated.Providers,
        });

        try
        {
            await systems.SetAsync(SystemKey, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // setSystemValue's own prefix is the only wrapper here.
            throw new InvalidOperationException(
                "failed to create system setting: " + ex.Message,
                ex);
        }

        cache.Update(updated);
    }

    public async Task<bool> IsEnabledAsync(
        string providerType,
        CancellationToken cancellationToken = default)
    {
        if (!SupportedProviderQuotaTypeSet.Contains(providerType))
        {
            throw new InvalidOperationException(
                "unsupported provider quota type: " + GoCast.GoQuote(providerType));
        }

        var settings = await GetAsync(cancellationToken).ConfigureAwait(false);
        return settings.Enabled
            && settings.Providers.TryGetValue(providerType, out var enabled)
            && enabled;
    }

    /// <summary>
    /// Serialization shape for the system value. It is a separate type so the
    /// dictionary stays a <see cref="SortedDictionary{TKey,TValue}"/> (Go sorts
    /// map keys) without exposing the draft's nullable map on the wire.
    /// </summary>
    private sealed class StoredSettings
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; init; }

        [JsonPropertyName("providers")]
        public SortedDictionary<string, bool> Providers { get; init; } =
            new(StringComparer.Ordinal);
    }
}
