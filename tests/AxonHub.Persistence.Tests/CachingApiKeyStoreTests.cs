using AxonHub.Persistence;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// C026: the port caches GetByKeyAsync results like the reference's live API key
/// cache. A present key is read once until a write clears the cache; a missing
/// key is not cached.
/// </summary>
public sealed class CachingApiKeyStoreTests
{
    [Fact]
    public async Task ClusterReadsDoNotReuseAnotherReplicasRevokedKey()
    {
        var inner = new CountingApiKeyStore();
        var id = await inner.CreateAsync(new ApiKeyRecord { Key = "sk-old", Name = "one" });
        var first = new CachingApiKeyStore(inner, new ApiKeyCache(), bypassCache: true);
        var second = new CachingApiKeyStore(inner, new ApiKeyCache(), bypassCache: true);

        Assert.NotNull(await second.GetByKeyAsync("sk-old"));
        await first.UpdateAsync(new ApiKeyUpdate { Id = id, Key = "sk-new" });

        Assert.Null(await second.GetByKeyAsync("sk-old"));
        Assert.NotNull(await second.GetByKeyAsync("sk-new"));
    }

    [Fact]
    public async Task GetByKeyCachesUntilAWrite()
    {
        var inner = new CountingApiKeyStore();
        await inner.CreateAsync(new ApiKeyRecord { Key = "sk-one", Name = "one" });
        inner.GetByKeyCalls = 0;
        var store = new CachingApiKeyStore(inner, new ApiKeyCache());

        var first = await store.GetByKeyAsync("sk-one");
        var second = await store.GetByKeyAsync("sk-one");

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, inner.GetByKeyCalls);
    }

    [Fact]
    public async Task MissingKeyIsNotCached()
    {
        var inner = new CountingApiKeyStore();
        var store = new CachingApiKeyStore(inner, new ApiKeyCache());

        Assert.Null(await store.GetByKeyAsync("missing"));
        Assert.Null(await store.GetByKeyAsync("missing"));

        Assert.Equal(2, inner.GetByKeyCalls);
    }

    [Fact]
    public async Task CreateClearsTheCache()
    {
        var inner = new CountingApiKeyStore();
        await inner.CreateAsync(new ApiKeyRecord { Key = "sk-one", Name = "one" });
        inner.GetByKeyCalls = 0;
        var store = new CachingApiKeyStore(inner, new ApiKeyCache());

        await store.GetByKeyAsync("sk-one");
        Assert.Equal(1, inner.GetByKeyCalls);

        await store.CreateAsync(new ApiKeyRecord { Key = "sk-two", Name = "two" });

        Assert.Equal("sk-two", (await store.GetByKeyAsync("sk-two"))!.Key);
        Assert.Equal("sk-one", (await store.GetByKeyAsync("sk-one"))!.Key);
        Assert.Equal(3, inner.GetByKeyCalls);
    }

    [Fact]
    public async Task UpdateClearsTheRotatedKey()
    {
        var inner = new CountingApiKeyStore();
        var id = await inner.CreateAsync(new ApiKeyRecord { Key = "sk-old", Name = "one" });
        inner.GetByKeyCalls = 0;
        var store = new CachingApiKeyStore(inner, new ApiKeyCache());

        await store.GetByKeyAsync("sk-old");
        Assert.Equal(1, inner.GetByKeyCalls);

        await store.UpdateAsync(new ApiKeyUpdate { Id = id, Key = "sk-new" });

        Assert.Null(await store.GetByKeyAsync("sk-old"));
        Assert.Equal("sk-new", (await store.GetByKeyAsync("sk-new"))!.Key);
        Assert.Equal(3, inner.GetByKeyCalls);
    }

    [Fact]
    public async Task BulkStatusUpdateClearsTheCache()
    {
        var inner = new CountingApiKeyStore();
        var id = await inner.CreateAsync(new ApiKeyRecord { Key = "sk-one", Name = "one" });
        inner.GetByKeyCalls = 0;
        var store = new CachingApiKeyStore(inner, new ApiKeyCache());

        await store.GetByKeyAsync("sk-one");
        Assert.Equal(1, inner.GetByKeyCalls);

        await store.BulkUpdateStatusAsync([id], "disabled", null);

        await store.GetByKeyAsync("sk-one");
        Assert.Equal(2, inner.GetByKeyCalls);
    }

    private sealed class CountingApiKeyStore : IApiKeyStore
    {
        private readonly Dictionary<long, ApiKeyRecord> _rows = [];
        private readonly Dictionary<string, long> _byKey = new(StringComparer.Ordinal);
        private long _nextId = 1;

        public int GetByKeyCalls { get; set; }

        public Task<T> RunInTransactionAsync<T>(
            Func<CancellationToken, Task<T>> action,
            CancellationToken cancellationToken = default)
            => action(cancellationToken);

        public Task RunInTransactionAsync(
            Func<CancellationToken, Task> action,
            CancellationToken cancellationToken = default)
            => action(cancellationToken);

        public Task<long> CreateAsync(
            ApiKeyRecord apiKey,
            CancellationToken cancellationToken = default)
        {
            apiKey.Id = _nextId++;
            _rows[apiKey.Id] = apiKey;
            _byKey[apiKey.Key] = apiKey.Id;
            return Task.FromResult(apiKey.Id);
        }

        public Task<ApiKeyRecord?> GetByIdAsync(
            long id,
            ApiKeyAccessFilter? access = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_rows.GetValueOrDefault(id));

        public Task<ApiKeyRecord?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            GetByKeyCalls++;
            return Task.FromResult(
                _byKey.TryGetValue(key, out var id) ? _rows.GetValueOrDefault(id) : null);
        }

        public Task<int> CountAsync(
            ApiKeyListFilter filter,
            ApiKeyAccessFilter? access,
            bool includeDeleted = false,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_rows.Count);

        public Task<bool> ExistsAsync(
            ApiKeyListFilter filter,
            ApiKeyAccessFilter? access,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_rows.Count > 0);

        public Task<IReadOnlyList<ApiKeyRecord>> ListAllAsync(
            ApiKeyListFilter filter,
            ApiKeyAccessFilter? access,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ApiKeyRecord>>([.. _rows.Values]);

        public Task<ListPage<ApiKeyRecord>> ListAsync(
            ApiKeyListQuery query,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ListPage<ApiKeyRecord>(
                [.. _rows.Values],
                HasNextPage: false,
                HasPreviousPage: false,
                TotalCount: _rows.Count));

        public Task<ApiKeyRecord?> UpdateAsync(
            ApiKeyUpdate update,
            CancellationToken cancellationToken = default)
        {
            if (!_rows.TryGetValue(update.Id, out var row))
            {
                return Task.FromResult<ApiKeyRecord?>(null);
            }

            if (update.Key is { Length: > 0 } key)
            {
                _byKey.Remove(row.Key);
                row.Key = key;
                _byKey[key] = row.Id;
            }

            return Task.FromResult<ApiKeyRecord?>(row);
        }

        public Task<int> BulkUpdateStatusAsync(
            IReadOnlyList<long> ids,
            string status,
            long? projectWritePredicate,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ids.Count);

        public Task LockProjectForNameAsync(
            long projectId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
