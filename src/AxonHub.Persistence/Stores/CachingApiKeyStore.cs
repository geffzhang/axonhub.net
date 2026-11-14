using System.Collections.Concurrent;

namespace AxonHub.Persistence;

/// <summary>
/// Process-wide cache for API key authentication lookups, the port of the
/// reference's live API key cache (C026): GetAPIKey reads by key value and the
/// write methods invalidate it.
/// </summary>
public sealed class ApiKeyCache
{
    private readonly ConcurrentDictionary<string, ApiKeyRecord> _byKey =
        new(StringComparer.Ordinal);

    internal bool TryGet(string key, out ApiKeyRecord record)
        => _byKey.TryGetValue(key, out record!);

    internal void Set(string key, ApiKeyRecord record) => _byKey[key] = record;

    internal void Clear() => _byKey.Clear();
}

/// <summary>
/// Caching decorator over <see cref="IApiKeyStore"/>: the authentication path's
/// <see cref="IApiKeyStore.GetByKeyAsync"/> is served from
/// <see cref="ApiKeyCache"/>; every write through the store clears the cache.
/// External direct database edits stay invisible until a write or restart,
/// matching the reference's in-process API key cache.
/// </summary>
public sealed class CachingApiKeyStore(IApiKeyStore inner, ApiKeyCache cache) : IApiKeyStore
{
    public Task<T> RunInTransactionAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken = default)
        => inner.RunInTransactionAsync(action, cancellationToken);

    public Task RunInTransactionAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken = default)
        => inner.RunInTransactionAsync(action, cancellationToken);

    public async Task<long> CreateAsync(
        ApiKeyRecord apiKey,
        CancellationToken cancellationToken = default)
    {
        var id = await inner.CreateAsync(apiKey, cancellationToken).ConfigureAwait(false);
        cache.Clear();
        return id;
    }

    public Task<ApiKeyRecord?> GetByIdAsync(
        long id,
        ApiKeyAccessFilter? access = null,
        CancellationToken cancellationToken = default)
        => inner.GetByIdAsync(id, access, cancellationToken);

    public async Task<ApiKeyRecord?> GetByKeyAsync(
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

    public Task<int> CountAsync(
        ApiKeyListFilter filter,
        ApiKeyAccessFilter? access,
        bool includeDeleted = false,
        CancellationToken cancellationToken = default)
        => inner.CountAsync(filter, access, includeDeleted, cancellationToken);

    public Task<bool> ExistsAsync(
        ApiKeyListFilter filter,
        ApiKeyAccessFilter? access,
        CancellationToken cancellationToken = default)
        => inner.ExistsAsync(filter, access, cancellationToken);

    public Task<IReadOnlyList<ApiKeyRecord>> ListAllAsync(
        ApiKeyListFilter filter,
        ApiKeyAccessFilter? access,
        CancellationToken cancellationToken = default)
        => inner.ListAllAsync(filter, access, cancellationToken);

    public Task<ListPage<ApiKeyRecord>> ListAsync(
        ApiKeyListQuery query,
        CancellationToken cancellationToken = default)
        => inner.ListAsync(query, cancellationToken);

    public async Task<ApiKeyRecord?> UpdateAsync(
        ApiKeyUpdate update,
        CancellationToken cancellationToken = default)
    {
        var record = await inner.UpdateAsync(update, cancellationToken).ConfigureAwait(false);
        cache.Clear();
        return record;
    }

    public async Task<int> BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        string status,
        long? projectWritePredicate,
        CancellationToken cancellationToken = default)
    {
        var affected = await inner
            .BulkUpdateStatusAsync(ids, status, projectWritePredicate, cancellationToken)
            .ConfigureAwait(false);
        cache.Clear();
        return affected;
    }

    public Task LockProjectForNameAsync(
        long projectId,
        CancellationToken cancellationToken = default)
        => inner.LockProjectForNameAsync(projectId, cancellationToken);
}
