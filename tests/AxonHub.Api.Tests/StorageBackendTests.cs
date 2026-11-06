using System.Text;
using AxonHub.Api.Management;
using AxonHub.Api.Storage;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// The fs backend (data_storage.go:550-729 over afero BasePathFs) and the
/// buildFileSystem error texts (data_storage.go:151-197).
/// </summary>
public sealed class StorageBackendTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "axonhub-storage-tests-" + Guid.NewGuid().ToString("N"));

    public StorageBackendTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort temp cleanup.
        }
    }

    // -------------------------------------------------------------- fs backend

    [Fact]
    public async Task WriteThenReadRoundTripsAndCreatesParentDirectories()
    {
        using var backend = new FsDataStorageBackend(_root);

        await backend.WriteAsync(
            "/backups/axonhub-backup-2026.json",
            Encoding.UTF8.GetBytes("{\"version\":\"1.4\"}"),
            CancellationToken.None);

        Assert.True(Directory.Exists(Path.Combine(_root, "backups")));
        var data = await backend.ReadAsync(
            "/backups/axonhub-backup-2026.json",
            CancellationToken.None);
        Assert.Equal("{\"version\":\"1.4\"}", Encoding.UTF8.GetString(data));
    }

    [Fact]
    public async Task WriteFromStreamReportsTheWrittenByteCountAndKey()
    {
        using var backend = new FsDataStorageBackend(_root);
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes("abcdef"));

        var (key, bytes) = await backend.WriteFromStreamAsync(
            "backup.json",
            source,
            CancellationToken.None);

        Assert.Equal("backup.json", key);
        Assert.Equal(6, bytes);
        Assert.Equal(
            "abcdef",
            await File.ReadAllTextAsync(Path.Combine(_root, "backup.json")));
    }

    [Fact]
    public async Task ListReturnsImmediateChildNamesAndModificationTimes()
    {
        using var backend = new FsDataStorageBackend(_root);
        var first = Path.Combine(_root, "axonhub-backup-a.json");
        await File.WriteAllTextAsync(first, "a");
        await File.WriteAllTextAsync(Path.Combine(_root, "axonhub-backup-b.json"), "b");
        File.SetLastWriteTimeUtc(first, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        var entries = await backend.ListAsync(CancellationToken.None);

        Assert.Equal(
            ["axonhub-backup-a.json", "axonhub-backup-b.json"],
            entries.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal));
        var a = Assert.Single(entries, entry => entry.Name == "axonhub-backup-a.json");
        Assert.Equal(
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            a.LastModified);
    }

    [Fact]
    public async Task DeleteRemovesFilesAndIgnoresMissingKeys()
    {
        using var backend = new FsDataStorageBackend(_root);
        var path = Path.Combine(_root, "gone.json");
        await File.WriteAllTextAsync(path, "x");

        await backend.DeleteAsync("gone.json", CancellationToken.None);
        Assert.False(File.Exists(path));

        // os.Remove returns ErrNotExist and DeleteData maps it to success.
        await backend.DeleteAsync("gone.json", CancellationToken.None);
    }

    [Fact]
    public async Task KeysCannotEscapeTheConfiguredDirectory()
    {
        using var backend = new FsDataStorageBackend(_root);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => backend.WriteAsync("../escape.json", [1], CancellationToken.None));

        Assert.Equal("invalid path: ../escape.json", ex.Message);
    }

    // ----------------------------------------------------------- factory texts

    [Fact]
    public void DatabaseStorageHasNoFileSystem()
    {
        var factory = new DataStorageBackendFactory();

        var ex = Assert.Throws<InvalidOperationException>(
            () => factory.Create(new DataStorageRecord { Type = "database" }));

        Assert.Equal(
            "database storage does not support file system operations",
            ex.Message);
    }

    [Fact]
    public void FsStorageRequiresADirectory()
    {
        var factory = new DataStorageBackendFactory();

        var ex = Assert.Throws<InvalidOperationException>(
            () => factory.Create(new DataStorageRecord
            {
                Type = "fs",
                Settings = """{"dsn":null,"directory":null,"s3":null,"gcs":null,"webdav":null}""",
            }));

        Assert.Equal("directory not configured for fs storage", ex.Message);
    }

    [Fact]
    public void EachBackendRequiresItsSettingsBlock()
    {
        var factory = new DataStorageBackendFactory();
        var settings = """{"dsn":null,"directory":null,"s3":null,"gcs":null,"webdav":null}""";

        Assert.Equal(
            "s3 settings not configured",
            Assert.Throws<InvalidOperationException>(
                () => factory.Create(new DataStorageRecord { Type = "s3", Settings = settings }))
                .Message);
        Assert.Equal(
            "gcs settings not configured",
            Assert.Throws<InvalidOperationException>(
                () => factory.Create(new DataStorageRecord { Type = "gcs", Settings = settings }))
                .Message);
        Assert.Equal(
            "webdav settings not configured",
            Assert.Throws<InvalidOperationException>(
                () => factory.Create(new DataStorageRecord { Type = "webdav", Settings = settings }))
                .Message);
    }

    [Fact]
    public void UnknownStorageTypesReportTheType()
    {
        var factory = new DataStorageBackendFactory();

        var ex = Assert.Throws<InvalidOperationException>(
            () => factory.Create(new DataStorageRecord { Type = "oss" }));

        Assert.Equal("unsupported storage type: oss", ex.Message);
    }

    [Fact]
    public void GcsCredentialFailuresAreWrappedLikeBuildFileSystem()
    {
        var factory = new DataStorageBackendFactory();

        var ex = Assert.Throws<InvalidOperationException>(
            () => factory.Create(new DataStorageRecord
            {
                Type = "gcs",
                Settings = """{"gcs":{"bucketName":"b","credential":"{\"type\":\"nope\"}"}}""",
            }));

        Assert.StartsWith(
            "failed to create gcs filesystem: failed to parse GCP credentials: ",
            ex.Message);
    }

    [Fact]
    public void FsBackendsAreCreatedForAConfiguredDirectory()
    {
        var factory = new DataStorageBackendFactory();

        using var backend = factory.Create(new DataStorageRecord
        {
            Type = "fs",
            Settings = """{"directory":"C:\\backups"}""",
        });

        Assert.IsType<FsDataStorageBackend>(backend);
    }

    [Fact]
    public void ObjectKeysStripExactlyOneLeadingSlash()
    {
        Assert.Equal("a/b.json", S3DataStorageBackend.NormalizeObjectKey("/a/b.json"));
        Assert.Equal("/a/b.json", S3DataStorageBackend.NormalizeObjectKey("//a/b.json"));
        Assert.Equal("a/b.json", S3DataStorageBackend.NormalizeObjectKey("a/b.json"));
    }
}
