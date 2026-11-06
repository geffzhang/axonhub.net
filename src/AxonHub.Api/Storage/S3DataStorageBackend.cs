using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AxonHub.Api.Management;

namespace AxonHub.Api.Storage;

/// <summary>
/// Port of the native S3 ObjectStore (objectstore_s3.go) plus the afero-s3
/// listing used by cleanupOldBackups. Keys are normalized by stripping one
/// leading slash, exactly like normalizeObjectKey (objectstore.go:45-50).
/// </summary>
public sealed class S3DataStorageBackend : IDataStorageBackend
{
    private readonly IAmazonS3 _client;
    private readonly string _bucket;

    public S3DataStorageBackend(S3StorageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _bucket = settings.BucketName;
        _client = new AmazonS3Client(
            new BasicAWSCredentials(settings.AccessKey, settings.SecretKey),
            CreateConfig(settings));
    }

    public async Task WriteAsync(
        string key,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeObjectKey(key);

        try
        {
            await using var stream = new MemoryStream(data.ToArray(), writable: false);
            await _client.PutObjectAsync(
                    new PutObjectRequest
                    {
                        BucketName = _bucket,
                        Key = normalized,
                        InputStream = stream,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "s3 put object \"" + normalized + "\": " + ex.Message,
                ex);
        }
    }

    public async Task<(string Key, long BytesWritten)> WriteFromStreamAsync(
        string key,
        Stream source,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeObjectKey(key);
        var counting = new CountingStream(source);
        Stream upload = counting;
        MemoryStream? buffered = null;

        // manager.Uploader buffers non-seekable readers; PutObject needs a
        // length, so buffer the (usually small) backup stream the same way.
        if (!source.CanSeek)
        {
            buffered = new MemoryStream();
            await source.CopyToAsync(buffered, cancellationToken).ConfigureAwait(false);
            buffered.Position = 0;
            upload = buffered;
        }

        try
        {
            await _client.PutObjectAsync(
                    new PutObjectRequest
                    {
                        BucketName = _bucket,
                        Key = normalized,
                        InputStream = upload,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "s3 upload \"" + normalized + "\": " + ex.Message,
                ex);
        }
        finally
        {
            if (buffered is not null)
            {
                await buffered.DisposeAsync().ConfigureAwait(false);
            }
        }

        return (normalized, buffered?.Length ?? counting.BytesRead);
    }

    public async Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var normalized = NormalizeObjectKey(key);

        try
        {
            using var response = await _client.GetObjectAsync(
                    new GetObjectRequest
                    {
                        BucketName = _bucket,
                        Key = normalized,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            await using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
            return buffer.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AmazonS3Exception ex) when (IsNotFound(ex))
        {
            // objectstore_s3.go:90-92: fmt.Errorf("%w: %s", os.ErrNotExist, key).
            throw new FileNotFoundException("file does not exist: " + normalized, ex);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "s3 get object \"" + normalized + "\": " + ex.Message,
                ex);
        }
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        var normalized = NormalizeObjectKey(key);

        try
        {
            // DeleteObject is idempotent: a missing key still returns success.
            await _client.DeleteObjectAsync(
                    new DeleteObjectRequest
                    {
                        BucketName = _bucket,
                        Key = normalized,
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "s3 delete object \"" + normalized + "\": " + ex.Message,
                ex);
        }
    }

    public async Task<IReadOnlyList<DataStorageEntry>> ListAsync(
        CancellationToken cancellationToken)
    {
        var entries = new List<DataStorageEntry>();
        string? continuationToken = null;

        try
        {
            do
            {
                var response = await _client.ListObjectsV2Async(
                        new ListObjectsV2Request
                        {
                            BucketName = _bucket,
                            Delimiter = "/",
                            ContinuationToken = continuationToken,
                        },
                        cancellationToken)
                    .ConfigureAwait(false);

                foreach (var item in response.S3Objects)
                {
                    var modified = item.LastModified ?? DateTime.MinValue;
                    // afero.ReadDir reports base names; with Delimiter "/" the
                    // listed keys are immediate children anyway.
                    var name = item.Key.EndsWith('/') ? item.Key[..^1] : item.Key;
                    name = name[(name.LastIndexOf('/') + 1)..];
                    entries.Add(new DataStorageEntry(
                        name,
                        new DateTimeOffset(DateTime.SpecifyKind(
                            modified,
                            DateTimeKind.Utc))));
                }

                continuationToken = response.IsTruncated == true
                    ? response.NextContinuationToken
                    : null;
            }
            while (!string.IsNullOrEmpty(continuationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "s3 list objects: " + ex.Message,
                ex);
        }

        return entries;
    }

    /// <summary>objectstore.go:48-50: strip one leading slash.</summary>
    internal static string NormalizeObjectKey(string key)
        => key.StartsWith('/') ? key[1..] : key;

    public void Dispose() => _client.Dispose();

    private static AmazonS3Config CreateConfig(S3StorageSettings settings)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = settings.PathStyle,

            // aws-sdk-go-v2 is configured with an adaptive retryer capped at 3
            // attempts (data_storage.go:402-427).
            MaxErrorRetry = 3,
        };

        if (!string.IsNullOrEmpty(settings.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
            config.AuthenticationRegion = settings.Region;
        }

        if (!string.IsNullOrEmpty(settings.Endpoint))
        {
            config.ServiceURL = settings.Endpoint;
        }

        return config;
    }

    private static bool IsNotFound(AmazonS3Exception ex)
        => ex.StatusCode == HttpStatusCode.NotFound
            || ex.ErrorCode is "NoSuchKey" or "NotFound" or "404";
}
