using System.Collections.Concurrent;

namespace AxonHub.Persistence;

/// <summary>
/// Process-wide cache for system values, the port of the reference's
/// <c>SystemService.Cache</c> (C029): <c>getSystemValue</c> reads from the
/// cache and <c>setSystemValue</c> invalidates the key. The port caches the
/// live row returned by <see cref="ISystemStore.GetByKeyAsync"/> and clears the
/// map after every write through the store.
/// </summary>
public sealed class SystemValueCache
{
    private readonly ConcurrentDictionary<string, SystemRecord> _values =
        new(StringComparer.Ordinal);

    internal bool TryGet(string key, out SystemRecord record)
        => _values.TryGetValue(key, out record!);

    internal void Set(string key, SystemRecord record) => _values[key] = record;

    internal void Clear() => _values.Clear();
}

/// <summary>
/// Caching decorator over <see cref="ISystemStore"/>. Reads of a present key
/// are served from <see cref="SystemValueCache"/>; every write clears the
/// cache, so all keys are re-read after a mutation. External direct database
/// edits stay invisible until a write or restart, matching the reference's
/// in-process cache.
/// </summary>
public sealed class CachingSystemStore(ISystemStore inner, SystemValueCache cache) : ISystemStore
{
    public async Task<long> CreateAsync(
        SystemRecord system,
        CancellationToken cancellationToken = default)
    {
        var id = await inner.CreateAsync(system, cancellationToken).ConfigureAwait(false);
        cache.Clear();
        return id;
    }

    public Task<SystemRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
        => inner.GetByIdAsync(id, cancellationToken);

    public async Task<SystemRecord?> GetByKeyAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        if (cache.TryGet(key, out var cached))
        {
            return cached;
        }

        var record = await inner.GetByKeyAsync(key, cancellationToken).ConfigureAwait(false);
        if (record is not null)
        {
            cache.Set(key, record);
        }

        return record;
    }

    public Task<ListPage<SystemRecord>> ListAsync(
        SystemListQuery query,
        CancellationToken cancellationToken = default)
        => inner.ListAsync(query, cancellationToken);

    public async Task<bool> TryCreateAsync(
        SystemRecord system,
        CancellationToken cancellationToken = default)
    {
        var created = await inner.TryCreateAsync(system, cancellationToken).ConfigureAwait(false);
        cache.Clear();
        return created;
    }

    public async Task<int> UpdateValueIfUnchangedAsync(
        long id,
        DateTime expectedUpdatedAt,
        string expectedValue,
        string value,
        CancellationToken cancellationToken = default)
    {
        var updated = await inner
            .UpdateValueIfUnchangedAsync(
                id,
                expectedUpdatedAt,
                expectedValue,
                value,
                cancellationToken)
            .ConfigureAwait(false);
        cache.Clear();
        return updated;
    }

    public async Task SetAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        await inner.SetAsync(key, value, cancellationToken).ConfigureAwait(false);
        cache.Clear();
    }

    public async Task CreateIfAbsentAsync(
        string key,
        string value,
        CancellationToken cancellationToken = default)
    {
        await inner.CreateIfAbsentAsync(key, value, cancellationToken).ConfigureAwait(false);
        cache.Clear();
    }

    public Task RunInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
        => inner.RunInTransactionAsync(action, cancellationToken);
}
