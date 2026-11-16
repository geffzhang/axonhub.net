using Path = System.IO.Path;

namespace AxonHub.Api.Storage;

/// <summary>
/// Port of the fs branch of DataStorageService.SaveData / SaveDataFromReader /
/// DeleteData / LoadData (data_storage.go:550-729) over
/// <c>afero.NewBasePathFs(afero.NewOsFs(), Directory)</c>. Keys are rooted at
/// the configured directory; Go's <c>filepath.FromSlash</c> becomes
/// <see cref="Path.DirectorySeparatorChar"/>.
/// </summary>
public sealed class FsDataStorageBackend : IDataStorageBackend
{
    private readonly string _root;

    public FsDataStorageBackend(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);
        _root = Path.GetFullPath(directory);
    }

    public async Task WriteAsync(
        string key,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        var path = Resolve(key);
        var fileKey = ToFileKey(key);

        try
        {
            CreateParentDirectory(path);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw new InvalidOperationException(
                "failed to create directory: " + ex.Message + ", key: " + fileKey,
                ex);
        }

        try
        {
            await File.WriteAllBytesAsync(path, data.ToArray(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw new InvalidOperationException(
                "failed to write file: " + ex.Message + ", key: " + fileKey,
                ex);
        }
    }

    public async Task<(string Key, long BytesWritten)> WriteFromStreamAsync(
        string key,
        Stream source,
        CancellationToken cancellationToken)
    {
        var path = Resolve(key);
        var fileKey = ToFileKey(key);

        try
        {
            CreateParentDirectory(path);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw new InvalidOperationException(
                "failed to create directory: " + ex.Message + ", key: " + fileKey,
                ex);
        }

        FileStream file;
        try
        {
            file = new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw new InvalidOperationException(
                "failed to create file: " + ex.Message + ", key: " + fileKey,
                ex);
        }

        await using (file.ConfigureAwait(false))
        {
            try
            {
                await source.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                throw new InvalidOperationException(
                    "failed to write file: " + ex.Message + ", key: " + fileKey,
                    ex);
            }

            return (fileKey, file.Length);
        }
    }

    public async Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var path = Resolve(key);

        try
        {
            return await File.ReadAllBytesAsync(path, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw new InvalidOperationException(
                "failed to read file: " + ex.Message,
                ex);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Resolve(key);
        var fileKey = ToFileKey(key);

        try
        {
            if (Directory.Exists(path))
            {
                // os.Remove deletes an empty directory and fails on a non-empty one.
                Directory.Delete(path, recursive: false);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw new InvalidOperationException(
                "failed to remove file: " + ex.Message + ", key: " + fileKey,
                ex);
        }
    }

    public Task<IReadOnlyList<DataStorageEntry>> ListAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = new List<DataStorageEntry>();
        foreach (var path in Directory.EnumerateFileSystemEntries(_root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var modified = File.GetLastWriteTimeUtc(path);
            entries.Add(new DataStorageEntry(
                Path.GetFileName(path),
                new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc))));
        }

        return Task.FromResult<IReadOnlyList<DataStorageEntry>>(entries);
    }

    private string Resolve(string key)
    {
        // filepath.Join (what BasePathFs uses) collapses leading separators
        // instead of re-rooting the path, unlike Path.Combine.
        var relative = key
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_root, relative));

        // afero.BasePathFs refuses keys that escape the base directory.
        var rootWithSeparator = _root.EndsWith(Path.DirectorySeparatorChar)
            ? _root
            : _root + Path.DirectorySeparatorChar;
        if (!string.Equals(full, _root, StringComparison.Ordinal)
            && !full.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("invalid path: " + key);
        }

        return full;
    }

    private static string ToFileKey(string key)
        => key.Replace('/', Path.DirectorySeparatorChar);

    private static void CreateParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(parent))
        {
            Directory.CreateDirectory(parent);
        }
    }

    private static bool IsFileSystemFailure(Exception ex)
        => ex is IOException or UnauthorizedAccessException or NotSupportedException;

    public void Dispose()
    {
        // Afero's OsFs holds no disposable state.
    }
}
