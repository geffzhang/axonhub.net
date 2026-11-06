using AxonHub.Persistence;

namespace AxonHub.Api.Storage;

/// <summary>
/// One immediate child of a data storage root. <see cref="LastModified"/> is the
/// object/file modification time used by the backup retention sweep
/// (autobackup.go:155-205). Directory markers carry <see cref="DateTimeOffset.MinValue"/>.
/// </summary>
public sealed record DataStorageEntry(string Name, DateTimeOffset LastModified);

/// <summary>
/// The operation subset of <c>biz.DataStorageService</c> plus the afero
/// filesystem calls that autobackup makes (data_storage.go:504-733,
/// autobackup.go:88-205). The reference exposes one afero.Fs per storage; the
/// port models only the operations the backup slice performs, so every backend
/// can be implemented against its native protocol without emulating a whole
/// filesystem API.
/// </summary>
/// <remarks>
/// Implementations own their HTTP/S3 clients (the reference caches them for the
/// process lifetime; the port builds one per operation, C199), so callers
/// dispose a backend when the operation finishes.
/// </remarks>
public interface IDataStorageBackend : IDisposable
{
    /// <summary>SaveData for a byte payload (data_storage.go:531-584).</summary>
    Task WriteAsync(
        string key,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken);

    /// <summary>
    /// SaveDataFromReader (data_storage.go:589-647): returns the storage key
    /// and the number of bytes written.
    /// </summary>
    Task<(string Key, long BytesWritten)> WriteFromStreamAsync(
        string key,
        Stream source,
        CancellationToken cancellationToken);

    /// <summary>LoadData (data_storage.go:696-733).</summary>
    Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// DeleteData (data_storage.go:651-691). Missing keys are a no-op for the
    /// fs and S3 branches only: fs maps os.ErrNotExist to success and
    /// DeleteObject is idempotent, while the WebDAV and GCS adapters surface
    /// the server's 404.
    /// </summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>
    /// <c>afero.ReadDir(fs, "/")</c> as used by cleanupOldBackups
    /// (autobackup.go:156-170). Only immediate children are returned.
    /// </summary>
    Task<IReadOnlyList<DataStorageEntry>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Builds the backend for a stored data storage row, mirroring
/// <c>DataStorageService.buildFileSystem</c> (data_storage.go:151-197). The
/// reference caches filesystem/object-store instances per storage id; the port
/// constructs them per operation instead (C199).
/// </summary>
public interface IDataStorageBackendFactory
{
    /// <exception cref="InvalidOperationException">
    /// The storage type has no usable backend or its settings are missing, with
    /// the reference's error texts.
    /// </exception>
    IDataStorageBackend Create(DataStorageRecord storage);
}
