using AxonHub.Api.Storage;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// In-memory <see cref="IDataStorageBackend"/>: objects are keyed by the exact
/// storage key and keep an explicit modification time so the retention sweep
/// is deterministic.
/// </summary>
internal sealed class FakeDataStorageBackend : IDataStorageBackend
{
    private readonly Dictionary<string, byte[]> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _modified = new(StringComparer.Ordinal);

    public List<string> Writes { get; } = [];

    public List<string> StreamWrites { get; } = [];

    public List<string> Deletes { get; } = [];

    public Exception? WriteFailure { get; set; }

    public Exception? WriteStreamFailure { get; set; }

    public Exception? ReadFailure { get; set; }

    public Exception? DeleteFailure { get; set; }

    public Exception? ListFailure { get; set; }

    public Func<string, Exception?>? DeleteFailureFor { get; set; }

    public IReadOnlyDictionary<string, byte[]> Objects => _objects;

    public void Add(string key, byte[] data, DateTimeOffset modified)
    {
        _objects[key] = data;
        _modified[key] = modified;
    }

    public Task WriteAsync(
        string key,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (WriteFailure is not null)
        {
            return Task.FromException(WriteFailure);
        }

        Writes.Add(key);
        _objects[key] = data.ToArray();
        _modified[key] = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    public async Task<(string Key, long BytesWritten)> WriteFromStreamAsync(
        string key,
        Stream source,
        CancellationToken cancellationToken)
    {
        if (WriteStreamFailure is not null)
        {
            throw WriteStreamFailure;
        }

        await using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        StreamWrites.Add(key);
        _objects[key] = buffer.ToArray();
        _modified[key] = DateTimeOffset.UtcNow;
        return (key, buffer.Length);
    }

    public Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (ReadFailure is not null)
        {
            return Task.FromException<byte[]>(ReadFailure);
        }

        return _objects.TryGetValue(key, out var data)
            ? Task.FromResult(data)
            : Task.FromException<byte[]>(new FileNotFoundException("file does not exist: " + key));
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (DeleteFailure is not null)
        {
            return Task.FromException(DeleteFailure);
        }

        var keyedFailure = DeleteFailureFor?.Invoke(key);
        if (keyedFailure is not null)
        {
            return Task.FromException(keyedFailure);
        }

        Deletes.Add(key);
        _objects.Remove(key);
        _modified.Remove(key);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DataStorageEntry>> ListAsync(
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (ListFailure is not null)
        {
            return Task.FromException<IReadOnlyList<DataStorageEntry>>(ListFailure);
        }

        var entries = _objects.Keys
            .Select(key => new DataStorageEntry(key, _modified[key]))
            .ToList();
        return Task.FromResult<IReadOnlyList<DataStorageEntry>>(entries);
    }

    public void Dispose()
    {
        // The double outlives one operation; retention reuses the same instance.
    }
}

/// <summary>
/// Factory double: hands every storage the same backend unless
/// <see cref="Failure"/> makes construction fail like buildFileSystem.
/// </summary>
internal sealed class FakeDataStorageBackends : IDataStorageBackendFactory
{
    public FakeDataStorageBackend Backend { get; } = new();

    public Exception? Failure { get; set; }

    public List<DataStorageRecord> Created { get; } = [];

    public DataStorageRecord? LastStorage => Created.Count == 0 ? null : Created[^1];

    public IDataStorageBackend Create(DataStorageRecord storage)
    {
        Created.Add(storage);
        if (Failure is not null)
        {
            throw Failure;
        }

        return Backend;
    }
}
