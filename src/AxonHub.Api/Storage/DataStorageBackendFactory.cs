using AxonHub.Api.Management;
using AxonHub.Persistence;

namespace AxonHub.Api.Storage;

/// <summary>
/// Port of DataStorageService.buildFileSystem + objectStoreFor
/// (data_storage.go:151-197, objectstore.go:59-85). Error texts follow the
/// reference's wrapping order: buildFileSystem's type/settings errors are the
/// bare messages, while client-construction failures are prefixed with
/// <c>failed to create &lt;type&gt; filesystem: </c>.
/// </summary>
public sealed class DataStorageBackendFactory : IDataStorageBackendFactory
{
    private readonly Func<GcsStorageSettings, GcsDataStorageBackend> _createGcs;
    private readonly Func<WebDavStorageSettings, HttpClient> _createWebDavClient;

    public DataStorageBackendFactory()
        : this(
            static settings => new GcsDataStorageBackend(settings),
            static settings => WebDavDataStorageBackend.CreateHttpClient(settings))
    {
    }

    /// <summary>Test seam: the reference's clients are bound to fixed endpoints.</summary>
    internal DataStorageBackendFactory(
        Func<GcsStorageSettings, GcsDataStorageBackend> createGcs,
        Func<WebDavStorageSettings, HttpClient> createWebDavClient)
    {
        _createGcs = createGcs;
        _createWebDavClient = createWebDavClient;
    }

    public IDataStorageBackend Create(DataStorageRecord storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        var settings = DataStorageSettingsCodec.Deserialize(storage.Settings);

        return storage.Type switch
        {
            "database" => throw new InvalidOperationException(
                "database storage does not support file system operations"),
            "fs" => CreateFs(settings),
            "s3" => CreateS3(settings),
            "gcs" => CreateGcs(settings),
            "webdav" => CreateWebDav(settings),
            _ => throw new InvalidOperationException(
                "unsupported storage type: " + storage.Type),
        };
    }

    private static IDataStorageBackend CreateFs(DataStorageSettings? settings)
    {
        if (settings?.Directory is null)
        {
            throw new InvalidOperationException(
                "directory not configured for fs storage");
        }

        return new FsDataStorageBackend(settings.Directory);
    }

    private static IDataStorageBackend CreateS3(DataStorageSettings? settings)
    {
        if (settings?.S3 is null)
        {
            throw new InvalidOperationException("s3 settings not configured");
        }

        try
        {
            return new S3DataStorageBackend(settings.S3);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to create s3 filesystem: " + ex.Message,
                ex);
        }
    }

    private IDataStorageBackend CreateGcs(DataStorageSettings? settings)
    {
        if (settings?.Gcs is null)
        {
            throw new InvalidOperationException("gcs settings not configured");
        }

        try
        {
            return _createGcs(settings.Gcs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to create gcs filesystem: " + ex.Message,
                ex);
        }
    }

    private IDataStorageBackend CreateWebDav(DataStorageSettings? settings)
    {
        if (settings?.WebDav is null)
        {
            throw new InvalidOperationException("webdav settings not configured");
        }

        try
        {
            return new WebDavDataStorageBackend(
                settings,
                _createWebDavClient(settings.WebDav));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                "failed to create webdav filesystem: " + ex.Message,
                ex);
        }
    }
}
