namespace AxonHub.Api.Orchestration;

/// <summary>
/// In-process snapshot of <c>provider_quota_collection_settings</c>, used by the
/// synchronous routing gate (<see cref="IProviderQuotaStatusProvider"/> has no
/// async shape in the reference) and refreshed by every settings read/write.
/// </summary>
/// <remarks>
/// The reference calls <c>SystemService.ProviderQuotaCollectionSettingsOrDefault</c>
/// from <c>GetQuotaStatus</c>, which reads the system row through the ent query
/// path (the reference's <c>SystemService.Cache</c> fronts it). The port's
/// <see cref="ProviderQuotaCollectionSettingsService"/> reads PostgreSQL on
/// demand, so the synchronous caller sees the last successfully read or written
/// snapshot instead. A stale snapshot can only delay the routing gate's view of
/// a settings change until the next read; the collection loop always reads
/// live. See C029/C182.
/// </remarks>
public sealed class ProviderQuotaSettingsCache
{
    private ProviderQuotaCollectionSettings _settings =
        ProviderQuotaCollectionSettingsService.Default();

    /// <summary>The last known settings; defaults until the first read.</summary>
    public ProviderQuotaCollectionSettings Current => Volatile.Read(ref _settings);

    /// <summary>Replaces the snapshot; called after every settings read/write.</summary>
    public void Update(ProviderQuotaCollectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _settings, settings);
    }
}
