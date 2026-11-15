using AxonHub.Persistence;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// C029: the port caches system values like SystemService.Cache. A present key
/// is read once until a write through ISystemStore clears the cache; a missing
/// key is not cached.
/// </summary>
public sealed class CachingSystemStoreTests
{
    [Fact]
    public async Task ClusterReadsObserveWritesFromAnotherReplica()
    {
        var inner = new CountingSystemStore();
        await inner.SetAsync("setting", "old");
        var first = new CachingSystemStore(inner, new SystemValueCache(), bypassCache: true);
        var second = new CachingSystemStore(inner, new SystemValueCache(), bypassCache: true);

        Assert.Equal("old", (await second.GetByKeyAsync("setting"))!.Value);
        await first.SetAsync("setting", "new");
        Assert.Equal("new", (await second.GetByKeyAsync("setting"))!.Value);
        Assert.Equal(2, inner.GetCalls);
    }

    [Fact]
    public async Task GetByKeyCachesUntilAWrite()
    {
        var inner = new CountingSystemStore();
        await inner.SetAsync("k", "v");
        inner.GetCalls = 0;
        var store = new CachingSystemStore(inner, new SystemValueCache());

        var first = await store.GetByKeyAsync("k");
        var second = await store.GetByKeyAsync("k");

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, inner.GetCalls);
    }

    [Fact]
    public async Task MissingKeyIsNotCached()
    {
        var inner = new CountingSystemStore();
        var store = new CachingSystemStore(inner, new SystemValueCache());

        Assert.Null(await store.GetByKeyAsync("missing"));
        Assert.Null(await store.GetByKeyAsync("missing"));

        Assert.Equal(2, inner.GetCalls);
    }

    [Fact]
    public async Task WritesClearEveryCachedKey()
    {
        var inner = new CountingSystemStore();
        await inner.SetAsync("a", "old-a");
        await inner.SetAsync("b", "old-b");
        inner.GetCalls = 0;
        var store = new CachingSystemStore(inner, new SystemValueCache());

        await store.GetByKeyAsync("a");
        await store.GetByKeyAsync("b");
        Assert.Equal(2, inner.GetCalls);

        await store.SetAsync("a", "new-a");

        var a = await store.GetByKeyAsync("a");
        var b = await store.GetByKeyAsync("b");
        Assert.Equal("new-a", a!.Value);
        Assert.Equal("old-b", b!.Value);
        Assert.Equal(4, inner.GetCalls);
    }

    [Fact]
    public async Task CompareAndSetInvalidatesTheCache()
    {
        var inner = new CountingSystemStore();
        await inner.SetAsync("a", "old");
        var id = (await inner.GetByKeyAsync("a"))!.Id;
        inner.GetCalls = 0;
        var store = new CachingSystemStore(inner, new SystemValueCache());

        await store.GetByKeyAsync("a");
        Assert.Equal(1, inner.GetCalls);

        var updated = await store.UpdateValueIfUnchangedAsync(
            id,
            DateTime.UtcNow,
            "old",
            "new");
        Assert.Equal(1, updated);

        var row = await store.GetByKeyAsync("a");
        Assert.Equal("new", row!.Value);
        Assert.Equal(2, inner.GetCalls);
    }

    private sealed class CountingSystemStore : ISystemStore
    {
        private readonly Dictionary<string, SystemRecord> _rows = new(StringComparer.Ordinal);
        private long _nextId = 1;

        public int GetCalls { get; set; }

        public Task<long> CreateAsync(
            SystemRecord system,
            CancellationToken cancellationToken = default)
        {
            system.Id = _nextId++;
            _rows[system.Key] = system;
            return Task.FromResult(system.Id);
        }

        public Task<SystemRecord?> GetByIdAsync(
            long id,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_rows.Values.FirstOrDefault(row => row.Id == id));

        public Task<SystemRecord?> GetByKeyAsync(
            string key,
            CancellationToken cancellationToken = default)
        {
            GetCalls++;
            return Task.FromResult(_rows.GetValueOrDefault(key));
        }

        public Task<ListPage<SystemRecord>> ListAsync(
            SystemListQuery query,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ListPage<SystemRecord>(
                [.. _rows.Values],
                HasNextPage: false,
                HasPreviousPage: false,
                TotalCount: _rows.Count));

        public async Task<bool> TryCreateAsync(
            SystemRecord system,
            CancellationToken cancellationToken = default)
        {
            if (_rows.ContainsKey(system.Key))
            {
                return false;
            }

            await CreateAsync(system, cancellationToken).ConfigureAwait(false);
            return true;
        }

        public Task<int> UpdateValueIfUnchangedAsync(
            long id,
            DateTime expectedUpdatedAt,
            string expectedValue,
            string value,
            CancellationToken cancellationToken = default)
        {
            var row = _rows.Values.FirstOrDefault(candidate => candidate.Id == id);
            if (row is null || row.Value != expectedValue)
            {
                return Task.FromResult(0);
            }

            row.Value = value;
            row.UpdatedAt = DateTime.UtcNow;
            return Task.FromResult(1);
        }

        public Task SetAsync(
            string key,
            string value,
            CancellationToken cancellationToken = default)
        {
            if (!_rows.TryGetValue(key, out var row))
            {
                row = new SystemRecord
                {
                    Id = _nextId++,
                    Key = key,
                    CreatedAt = DateTime.UtcNow,
                };
                _rows[key] = row;
            }

            row.Value = value;
            row.UpdatedAt = DateTime.UtcNow;
            return Task.CompletedTask;
        }

        public Task CreateIfAbsentAsync(
            string key,
            string value,
            CancellationToken cancellationToken = default)
            => _rows.ContainsKey(key)
                ? Task.CompletedTask
                : SetAsync(key, value, cancellationToken);

        public Task RunInTransactionAsync(
            Func<CancellationToken, Task> action,
            CancellationToken cancellationToken = default)
            => action(cancellationToken);
    }
}
