using AxonHub.Persistence;

namespace AxonHub.Api.Storage;

/// <summary>
/// Process-wide cache for the data storage slice (C196): rows live for 30
/// minutes like the reference's GetDataStorageByID / GetPrimaryDataStorage
/// cache, while backend clients are reused for one minute and then rebuilt,
/// approximating the reference's minute refresh of its filesystem/object-store
/// cache. Every write through <see cref="CachingDataStorageStore"/> invalidates
/// both halves.
/// </summary>
public sealed class DataStorageCache(TimeProvider timeProvider)
{
    private static readonly TimeSpan RowTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan BackendTtl = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<long, (DataStorageRecord Row, DateTimeOffset LoadedAt)> _rows = [];
    private readonly Dictionary<long, BackendEntry> _backends = [];
    private (DataStorageRecord Row, DateTimeOffset LoadedAt)? _primary;

    public bool TryGetRow(long id, out DataStorageRecord row)
    {
        lock (_gate)
        {
            if (_rows.TryGetValue(id, out var entry)
                && timeProvider.GetUtcNow() - entry.LoadedAt <= RowTtl)
            {
                row = entry.Row;
                return true;
            }

            _rows.Remove(id);
            row = null!;
            return false;
        }
    }

    public void SetRow(long id, DataStorageRecord row)
    {
        lock (_gate)
        {
            _rows[id] = (row, timeProvider.GetUtcNow());
        }
    }

    public bool TryGetPrimary(out DataStorageRecord? row)
    {
        lock (_gate)
        {
            if (_primary is { } entry
                && timeProvider.GetUtcNow() - entry.LoadedAt <= RowTtl)
            {
                row = entry.Row;
                return true;
            }

            _primary = null;
            row = null;
            return false;
        }
    }

    public void SetPrimary(DataStorageRecord row)
    {
        lock (_gate)
        {
            _primary = (row, timeProvider.GetUtcNow());
        }
    }

    /// <summary>Drops all cached rows and retires every backend client.</summary>
    public void Invalidate()
    {
        List<BackendEntry> dispose = [];
        lock (_gate)
        {
            _rows.Clear();
            _primary = null;

            foreach (var entry in _backends.Values)
            {
                entry.Retired = true;
                if (entry.Refs == 0)
                {
                    dispose.Add(entry);
                }
            }

            _backends.Clear();
        }

        foreach (var entry in dispose)
        {
            entry.Backend.Dispose();
        }
    }

    /// <summary>
    /// Returns a lease over the cached backend for <paramref name="id"/>, or
    /// creates one through <paramref name="create"/>. A lease never owns the
    /// cached backend; the cache disposes it when it is retired and unused.
    /// </summary>
    public IDataStorageBackend AcquireBackend(
        long id,
        Func<IDataStorageBackend> create)
    {
        BackendEntry? dispose = null;
        BackendEntry entry;
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            if (_backends.TryGetValue(id, out var existing)
                && now - existing.CreatedAt <= BackendTtl)
            {
                existing.Refs++;
                return new Lease(this, existing);
            }

            if (existing is not null)
            {
                existing.Retired = true;
                _backends.Remove(id);
                if (existing.Refs == 0)
                {
                    dispose = existing;
                }
            }

            entry = new BackendEntry(id, create(), now) { Refs = 1 };
            _backends[id] = entry;
        }

        dispose?.Backend.Dispose();
        return new Lease(this, entry);
    }

    private void Release(BackendEntry entry)
    {
        IDataStorageBackend? dispose = null;
        lock (_gate)
        {
            entry.Refs--;
            if (entry.Refs == 0
                && (entry.Retired
                    || !_backends.TryGetValue(entry.Id, out var current)
                    || !ReferenceEquals(current, entry)))
            {
                dispose = entry.Backend;
            }
        }

        dispose?.Dispose();
    }

    private sealed class BackendEntry(
        long id,
        IDataStorageBackend backend,
        DateTimeOffset createdAt)
    {
        public long Id { get; } = id;

        public IDataStorageBackend Backend { get; } = backend;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public int Refs { get; set; }

        public bool Retired { get; set; }
    }

    private sealed class Lease(DataStorageCache owner, BackendEntry entry)
        : IDataStorageBackend
    {
        private int _disposed;

        public Task WriteAsync(
            string key,
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken)
            => entry.Backend.WriteAsync(key, data, cancellationToken);

        public Task<(string Key, long BytesWritten)> WriteFromStreamAsync(
            string key,
            Stream source,
            CancellationToken cancellationToken)
            => entry.Backend.WriteFromStreamAsync(key, source, cancellationToken);

        public Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken)
            => entry.Backend.ReadAsync(key, cancellationToken);

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
            => entry.Backend.DeleteAsync(key, cancellationToken);

        public Task<IReadOnlyList<DataStorageEntry>> ListAsync(
            CancellationToken cancellationToken)
            => entry.Backend.ListAsync(cancellationToken);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(entry);
            }
        }
    }
}
