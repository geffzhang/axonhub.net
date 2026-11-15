using AxonHub.Persistence;

namespace AxonHub.Api.Storage;

/// <summary>
/// Caching decorator over <see cref="IDataStorageStore"/>: GetByIdAsync and
/// GetPrimaryAsync are served from <see cref="DataStorageCache"/> for 30
/// minutes; every write clears both the row and backend caches. The GC
/// worker's SkipSoftDelete reads bypass the cache so deleted rows remain
/// visible to cleanup.
/// </summary>
public sealed class CachingDataStorageStore(
    IDataStorageStore inner,
    DataStorageCache cache) : IDataStorageStore
{
    public async Task<long> CreateAsync(
        DataStorageRecord storage,
        CancellationToken cancellationToken = default)
    {
        var id = await inner.CreateAsync(storage, cancellationToken).ConfigureAwait(false);
        cache.Invalidate();
        return id;
    }

    public async Task<DataStorageRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        if (cache.TryGetRow(id, out var cached))
        {
            return cached;
        }

        var storage = await inner.GetByIdAsync(id, cancellationToken).ConfigureAwait(false);
        if (storage is not null)
        {
            cache.SetRow(id, storage);
        }

        return storage;
    }

    public Task<DataStorageRecord?> GetByIdIncludingDeletedAsync(
        long id,
        CancellationToken cancellationToken = default)
        => inner.GetByIdIncludingDeletedAsync(id, cancellationToken);

    public async Task<DataStorageRecord?> GetPrimaryAsync(
        CancellationToken cancellationToken = default)
    {
        if (cache.TryGetPrimary(out var cached))
        {
            return cached;
        }

        var storage = await inner.GetPrimaryAsync(cancellationToken).ConfigureAwait(false);
        if (storage is not null)
        {
            cache.SetPrimary(storage);
        }

        return storage;
    }

    public Task<DataStorageRecord?> GetPrimaryIncludingDeletedAsync(
        CancellationToken cancellationToken = default)
        => inner.GetPrimaryIncludingDeletedAsync(cancellationToken);

    public Task<bool> ExistsByNameAsync(
        string name,
        long? excludeId,
        CancellationToken cancellationToken = default)
        => inner.ExistsByNameAsync(name, excludeId, cancellationToken);

    public Task<ListPage<DataStorageRecord>> ListAsync(
        DataStorageListQuery query,
        CancellationToken cancellationToken = default)
        => inner.ListAsync(query, cancellationToken);

    public async Task<DataStorageRecord?> UpdateAsync(
        DataStorageUpdate update,
        CancellationToken cancellationToken = default)
    {
        var storage = await inner.UpdateAsync(update, cancellationToken).ConfigureAwait(false);
        cache.Invalidate();
        return storage;
    }
}
