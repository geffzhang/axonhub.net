using System.Globalization;
using System.Text;
using AxonHub.Api.Storage;
using AxonHub.Llm.Json;
using AxonHub.Llm.Transformer.Shared;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of api.RequestContentHandlers.DownloadRequestContent
/// (internal/server/api/request_content.go:40-122), mounted by routes.go:145
/// on the admin group. The reference streams the object straight from the
/// filesystem; the port's storage seam exposes byte reads, so the generic
/// branch materialises the object first (C273).
/// </summary>
public static class RequestContentHandlers
{
    public const string ContentNotFound = "Content not found";
    public const string ContentStorageNotFound = "Content storage not found";
    public const string FailedToLoadContentStorage = "Failed to load content storage";
    public const string FailedToOpenContentStorage = "Failed to open content storage";
    public const string ContentStorageNotFileBased = "Content storage is not file-based";

    private const string DatabaseStorageType = "database";
    private const string FsStorageType = "fs";
    private const string S3StorageType = "s3";

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>Port of DownloadRequestContent (request_content.go:40-122).</summary>
    public static async Task DownloadRequestContentAsync(
        HttpContext context,
        string? requestId,
        IManagementIdentity identity,
        IRequestStore requests,
        IDataStorageStore dataStorages,
        IDataStorageBackendFactory backends)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(dataStorages);
        ArgumentNullException.ThrowIfNull(backends);

        var cancellationToken = context.RequestAborted;

        var projectId = await AdminRequestEndpoints
            .RequireProjectIdAsync(context, identity)
            .ConfigureAwait(false);
        if (projectId is null)
        {
            return;
        }

        var request = await AdminRequestEndpoints
            .ResolveRequestAsync(context, requestId, projectId.Value, requests)
            .ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        var storageKey = request.ContentStorageKey is null
            ? null
            : GoStrings.TrimSpace(request.ContentStorageKey);
        if (!request.ContentSaved
            || request.ContentStorageId is null
            || storageKey is null
            || storageKey.Length == 0)
        {
            await ErrorAsync(context, StatusCodes.Status404NotFound, ContentNotFound)
                .ConfigureAwait(false);
            return;
        }

        var key = storageKey.StartsWith('/')
            ? storageKey
            : "/" + storageKey;
        var expectedPrefix = "/"
            + request.ProjectId.ToString(CultureInfo.InvariantCulture)
            + "/requests/"
            + request.Id.ToString(CultureInfo.InvariantCulture)
            + "/";
        if (!key.StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            await ErrorAsync(context, StatusCodes.Status404NotFound, ContentNotFound)
                .ConfigureAwait(false);
            return;
        }

        DataStorageRecord? storage;
        try
        {
            storage = await dataStorages
                .GetByIdAsync(request.ContentStorageId.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            await ErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    FailedToLoadContentStorage)
                .ConfigureAwait(false);
            return;
        }

        if (storage is null)
        {
            await ErrorAsync(
                    context,
                    StatusCodes.Status404NotFound,
                    ContentStorageNotFound)
                .ConfigureAwait(false);
            return;
        }

        if (storage.Primary
            || string.Equals(storage.Type, DatabaseStorageType, StringComparison.Ordinal))
        {
            await ErrorAsync(
                    context,
                    StatusCodes.Status400BadRequest,
                    ContentStorageNotFileBased)
                .ConfigureAwait(false);
            return;
        }

        var settings = DataStorageSettingsCodec.Deserialize(storage.Settings);

        // The fs fast path bypasses the storage backend entirely
        // (request_content.go:91-104): it serves the file from the configured
        // directory with c.FileAttachment, which is http.ServeFile.
        if (string.Equals(storage.Type, FsStorageType, StringComparison.Ordinal)
            && settings?.Directory is not null)
        {
            if (!AdminRequestEndpoints.TryGetSafeRelativePath(key, out var relativePath))
            {
                await ErrorAsync(context, StatusCodes.Status404NotFound, ContentNotFound)
                    .ConfigureAwait(false);
                return;
            }

            string fullPath;
            try
            {
                fullPath = AdminRequestEndpoints.JoinStoragePath(settings.Directory, relativePath);
            }
            catch (Exception)
            {
                // filepath.Join never fails; ServeFile answers an unusable
                // path with its own 404.
                await ErrorAsync(context, StatusCodes.Status404NotFound, ContentNotFound)
                    .ConfigureAwait(false);
                return;
            }

            var filename = AdminRequestEndpoints.FilenameFromKey(key, request.Id);
            context.Response.Headers.ContentDisposition =
                AdminRequestEndpoints.FileAttachmentDisposition(filename);
            if (!File.Exists(fullPath))
            {
                // http.ServeFile answers a failed open with Error: the
                // FileAttachment header set above survives, the body is the
                // fixed text (net/http/fs.go toHTTPError).
                await WriteServeFileNotFoundAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            // ServeFile's range/conditional handling; the download name is
            // set by hand above so the reference's gin quoting is preserved.
            await Results.File(
                    fullPath,
                    MediaTypeFor(fullPath),
                    enableRangeProcessing: true)
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        IDataStorageBackend backend;
        try
        {
            backend = backends.Create(storage);
        }
        catch (Exception)
        {
            await ErrorAsync(
                    context,
                    StatusCodes.Status500InternalServerError,
                    FailedToOpenContentStorage)
                .ConfigureAwait(false);
            return;
        }

        using (backend)
        {
            var objectKey = key;
            if (string.Equals(storage.Type, FsStorageType, StringComparison.Ordinal))
            {
                objectKey = AdminRequestEndpoints.FromSlash(key);
            }
            else if (string.Equals(storage.Type, S3StorageType, StringComparison.Ordinal)
                && settings?.S3 is { PathStyle: true })
            {
                // strings.TrimPrefix(key, "/") removes one leading slash.
                objectKey = key.StartsWith('/') ? key[1..] : key;
            }

            byte[] data;
            try
            {
                data = await backend.ReadAsync(objectKey, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // fs.Open failures (including a missing object) are all a 404
                // in the reference; the backend seam reads the whole object.
                await ErrorAsync(context, StatusCodes.Status404NotFound, ContentNotFound)
                    .ConfigureAwait(false);
                return;
            }

            await WriteAttachmentAsync(
                    context,
                    data,
                    AdminRequestEndpoints.FilenameFromKey(objectKey, request.Id),
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>The two admin request-detail routes (routes.go:145-152).</summary>
    public static IEndpointRouteBuilder MapRequestDetailRoutes(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet(
            "/admin/requests/{requestId}/content",
            DownloadRequestContentAsync);
        endpoints.MapGet(
            "/admin/requests/{requestId}/preview",
            RequestPreviewHandlers.PreviewRequestAsync);
        return endpoints;
    }

    /// <summary>
    /// ServeFile's content type: mime.TypeByExtension, sniffed when the
    /// extension is unknown. The port only covers the extension table and
    /// falls back to the octet-stream default (C273).
    /// </summary>
    private static string MediaTypeFor(string fullPath)
        => ContentTypes.TryGetContentType(fullPath, out var contentType)
            ? contentType
            : "application/octet-stream";

    /// <summary>Port of http.Error on a failed ServeFile open.</summary>
    private static async Task WriteServeFileNotFoundAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        await context.Response.Body
            .WriteAsync(Encoding.UTF8.GetBytes("404 page not found\n"), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteAttachmentAsync(
        HttpContext context,
        byte[] data,
        string filename,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentLength = data.Length;

        // request_content.go:112-116, in the same order.
        context.Response.ContentType = "application/octet-stream";
        context.Response.Headers.ContentDisposition =
            "attachment; filename=" + GoCast.GoQuote(filename);
        context.Response.Headers.CacheControl = "private, max-age=0, no-cache";

        await context.Response.Body.WriteAsync(data, cancellationToken)
            .ConfigureAwait(false);
    }

    private static Task ErrorAsync(
        HttpContext context,
        int statusCode,
        string message)
        => AdminRequestEndpoints.WriteErrorAsync(context, statusCode, message);
}
