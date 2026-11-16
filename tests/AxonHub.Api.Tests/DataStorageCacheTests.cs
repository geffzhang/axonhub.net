using AxonHub.Api.Storage;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// C196: the data storage slice reuses rows for 30 minutes and backend clients
/// for one minute, invalidating both on writes. The GC worker's
/// SkipSoftDelete reads bypass the cache.
/// </summary>
public sealed class DataStorageCacheTests
{
    [Fact]
    public async Task RowReadsAreCachedUntilAWrite()
    {
        var store = new FakeStore();
        var row = new DataStorageRecord { Id = 1, Name = "a", Primary = true };
        store.Rows[1] = row;
        store.PrimaryRow = row;
        var cache = new DataStorageCache(new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var cached = new CachingDataStorageStore(store, cache);

        Assert.Same(row, await cached.GetByIdAsync(1));
        Assert.Same(row, await cached.GetByIdAsync(1));
        Assert.Equal(1, store.GetByIdCalls);

        Assert.Same(row, await cached.GetPrimaryAsync());
        Assert.Same(row, await cached.GetPrimaryAsync());
        Assert.Equal(1, store.GetPrimaryCalls);

        await cached.CreateAsync(new DataStorageRecord { Name = "b" });

        await cached.GetByIdAsync(1);
        await cached.GetPrimaryAsync();
        Assert.Equal(2, store.GetByIdCalls);
        Assert.Equal(2, store.GetPrimaryCalls);
    }

    [Fact]
    public async Task MissingRowsAreNotCached()
    {
        var store = new FakeStore();
        var cached = new CachingDataStorageStore(
            store,
            new DataStorageCache(new ManualTimeProvider(DateTimeOffset.UnixEpoch)));

        Assert.Null(await cached.GetByIdAsync(42));
        Assert.Null(await cached.GetByIdAsync(42));

        Assert.Equal(2, store.GetByIdCalls);
    }

    [Fact]
    public async Task RowCacheExpiresAfterThirtyMinutes()
    {
        var store = new FakeStore();
        var row = new DataStorageRecord { Id = 1, Name = "a" };
        store.Rows[1] = row;
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var cached = new CachingDataStorageStore(store, new DataStorageCache(time));

        await cached.GetByIdAsync(1);
        time.Advance(TimeSpan.FromMinutes(31));
        await cached.GetByIdAsync(1);

        Assert.Equal(2, store.GetByIdCalls);
    }

    [Fact]
    public async Task BackendIsReusedUntilAWriteOrExpiry()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var cache = new DataStorageCache(time);
        var factory = new FakeFactory();
        var cachedFactory = new CachingDataStorageBackendFactory(factory, cache);
        var record = new DataStorageRecord { Id = 7, Name = "s" };

        using (cachedFactory.Create(record))
        {
        }

        using (cachedFactory.Create(record))
        {
        }

        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(0, factory.Backends[0].DisposeCount);

        var store = new FakeStore();
        var cachedStore = new CachingDataStorageStore(store, cache);
        await cachedStore.CreateAsync(new DataStorageRecord { Name = "new" });
        Assert.Equal(1, factory.Backends[0].DisposeCount);

        using (cachedFactory.Create(record))
        {
        }

        Assert.Equal(2, factory.CreateCount);

        time.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        using (cachedFactory.Create(record))
        {
        }

        Assert.Equal(3, factory.CreateCount);
        Assert.Equal(1, factory.Backends[1].DisposeCount);
    }

    [Fact]
    public void InvalidatedBackendIsDisposedWhenItsLeaseIsReleased()
    {
        var cache = new DataStorageCache(
            new ManualTimeProvider(DateTimeOffset.UnixEpoch));
        var factory = new FakeFactory();
        var cachedFactory = new CachingDataStorageBackendFactory(factory, cache);
        var record = new DataStorageRecord { Id = 3, Name = "s" };

        var lease = cachedFactory.Create(record);
        cache.Invalidate();
        using (cachedFactory.Create(record))
        {
        }

        Assert.Equal(0, factory.Backends[0].DisposeCount);
        lease.Dispose();
        Assert.Equal(1, factory.Backends[0].DisposeCount);
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeStore : IDataStorageStore
    {
        private long _nextId = 1;

        public Dictionary<long, DataStorageRecord> Rows { get; } = [];

        public DataStorageRecord? PrimaryRow { get; set; }

        public int GetByIdCalls { get; private set; }

        public int GetPrimaryCalls { get; private set; }

        public Task<long> CreateAsync(
            DataStorageRecord storage,
            CancellationToken cancellationToken = default)
        {
            storage.Id = _nextId++;
            Rows[storage.Id] = storage;
            if (storage.Primary)
            {
                PrimaryRow = storage;
            }

            return Task.FromResult(storage.Id);
        }

        public Task<DataStorageRecord?> GetByIdAsync(
            long id,
            CancellationToken cancellationToken = default)
        {
            GetByIdCalls++;
            return Task.FromResult(Rows.GetValueOrDefault(id));
        }

        public Task<DataStorageRecord?> GetByIdIncludingDeletedAsync(
            long id,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Rows.GetValueOrDefault(id));

        public Task<DataStorageRecord?> GetPrimaryAsync(
            CancellationToken cancellationToken = default)
        {
            GetPrimaryCalls++;
            return Task.FromResult(PrimaryRow);
        }

        public Task<DataStorageRecord?> GetPrimaryIncludingDeletedAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult(PrimaryRow);

        public Task<bool> ExistsByNameAsync(
            string name,
            long? excludeId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<ListPage<DataStorageRecord>> ListAsync(
            DataStorageListQuery query,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new ListPage<DataStorageRecord>(
                [],
                HasNextPage: false,
                HasPreviousPage: false,
                TotalCount: 0));

        public Task<DataStorageRecord?> UpdateAsync(
            DataStorageUpdate update,
            CancellationToken cancellationToken = default)
            => Task.FromResult<DataStorageRecord?>(null);
    }

    private sealed class FakeFactory : IDataStorageBackendFactory
    {
        public List<FakeBackend> Backends { get; } = [];

        public int CreateCount => Backends.Count;

        public IDataStorageBackend Create(DataStorageRecord storage)
        {
            var backend = new FakeBackend();
            Backends.Add(backend);
            return backend;
        }
    }

    private sealed class FakeBackend : IDataStorageBackend
    {
        public int DisposeCount { get; private set; }

        public Task WriteAsync(
            string key,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<(string Key, long BytesWritten)> WriteFromStreamAsync(
            string key,
            Stream source,
            CancellationToken cancellationToken)
            => Task.FromResult((key, 0L));

        public Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken)
            => Task.FromResult(Array.Empty<byte>());

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<DataStorageEntry>> ListAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DataStorageEntry>>([]);

        public void Dispose() => DisposeCount++;
    }
}
