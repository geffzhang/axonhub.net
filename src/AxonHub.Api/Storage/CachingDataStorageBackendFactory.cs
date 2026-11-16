using AxonHub.Persistence;

namespace AxonHub.Api.Storage;

/// <summary>
/// Caching decorator over <see cref="IDataStorageBackendFactory"/>: backends
/// are reused per storage id for one minute (the reference refreshes its
/// filesystem/object-store cache every minute), and every write through
/// <see cref="CachingDataStorageStore"/> retires the cache immediately.
/// </summary>
public sealed class CachingDataStorageBackendFactory(
    IDataStorageBackendFactory inner,
    DataStorageCache cache) : IDataStorageBackendFactory
{
    public IDataStorageBackend Create(DataStorageRecord storage)
        => cache.AcquireBackend(storage.Id, () => inner.Create(storage));
}
