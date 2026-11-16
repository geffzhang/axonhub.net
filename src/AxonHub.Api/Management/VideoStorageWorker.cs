using System.Globalization;
using System.Net;
using System.Net.Http;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Settings;
using AxonHub.Api.Storage;
using AxonHub.Llm;
using AxonHub.Llm.Json;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging;
using Path = System.IO.Path;

namespace AxonHub.Api.Management;

/// <summary>
/// Port of internal/server/video_storage/worker.go (f272549f): the periodic
/// scan that downloads generated videos into the configured external storage
/// and marks their request rows as saved. The Orleans reminder drives
/// <see cref="ScanAndSaveAsync"/> through
/// <see cref="AxonHub.Grains.Abstractions.IVideoStorageScanRunner"/>; the
/// scanned row set, the per-row gates, the download rules and the content
/// columns write follow the reference exactly.
/// </summary>
/// <remarks>
/// The reference uses <c>http.DefaultClient</c>; the port keeps one shared
/// client with Go's redirect limit (10) and transparent gzip, and applies the
/// same five-minute download / ten-minute scan timeouts through linked
/// cancellation tokens. Go's <c>io.LimitReader</c> truncates a download at
/// 512 MiB without an error; <see cref="LimitedReadStream"/> does the same.
/// </remarks>
public sealed class VideoStorageWorker(
    ISystemSettingsAdminService settings,
    IRequestStore requests,
    RequestPayloadStorage payloads,
    VideoService videos,
    IDataStorageBackendFactory backends,
    TimeProvider time,
    ILogger<VideoStorageWorker> logger,
    HttpClient? httpClient = null)
{
    /// <summary>worker.go:132-135: the scan limit when the stored value is non-positive.</summary>
    public const int DefaultScanLimit = 50;

    /// <summary>worker.go:193: the maximum number of bytes saved per video.</summary>
    public const long MaxDownloadBytes = 512L * 1024 * 1024;

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
    {
        // Go's http.DefaultClient follows at most ten redirects and asks for
        // gzip transparently.
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    });

    private static readonly Action<ILogger, long, Exception?> LogSaveFailed =
        LoggerMessage.Define<long>(
            LogLevel.Warning,
            new EventId(0, "VideoStorageSaveFailed"),
            "Failed to save video request request_id={RequestId}");

    private static readonly Action<ILogger, long, long, string, long, Exception?> LogSaved =
        LoggerMessage.Define<long, long, string, long>(
            LogLevel.Information,
            new EventId(0, "VideoStorageSaved"),
            "Saved video to storage request_id={RequestId} data_storage_id={DataStorageId} key={Key} size={Size}");

    private readonly HttpClient _http = httpClient ?? SharedHttpClient;

    /// <summary>Port of Worker.scanAndSave (worker.go:109-158).</summary>
    public async Task ScanAndSaveAsync(CancellationToken cancellationToken = default)
    {
        var videoSettings = await settings.GetVideoStorageSettingsAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!videoSettings.Enabled)
        {
            return;
        }

        if (videoSettings.DataStorageId == 0)
        {
            throw new InvalidOperationException(
                "video storage enabled but data_storage_id is not set");
        }

        DataStorageRecord storage;
        try
        {
            storage = await payloads
                .GetDataStorageByIdAsync(videoSettings.DataStorageId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to get data storage: " + ex.Message,
                ex);
        }

        if (storage.Primary || string.Equals(storage.Type, "database", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("video storage must be non-database storage");
        }

        var limit = videoSettings.ScanLimit;
        if (limit <= 0)
        {
            limit = DefaultScanLimit;
        }

        IReadOnlyList<RequestRecord> rows;
        try
        {
            rows = await requests
                .ListUnsavedVideoRequestsAsync(limit, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // worker.go:146-148 wraps the ent query failure.
            throw new InvalidOperationException(
                "failed to query video requests: " + ex.Message,
                ex);
        }

        foreach (var request in rows)
        {
            try
            {
                await ProcessOneAsync(storage, request, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The reference logs the per-row failure and continues; a
                // cancelled scan token surfaces as one warning per remaining
                // row, exactly like the Go loop.
                LogSaveFailed(logger, request.Id, ex);
            }
        }
    }

    /// <summary>Port of Worker.processOne (worker.go:160-222).</summary>
    private async Task ProcessOneAsync(
        DataStorageRecord storage,
        RequestRecord request,
        CancellationToken cancellationToken)
    {
        var videoUrl = ExtractVideoUrlFromResponseBody(request.ResponseBody);

        if (videoUrl.Trim().Length == 0)
        {
            var taskResponse = await videos.GetTaskAsync(request.Id, cancellationToken)
                .ConfigureAwait(false);

            var video = taskResponse.Video;
            if (video is null
                || !string.Equals(
                    video.Status.Trim().ToLowerInvariant(),
                    "succeeded",
                    StringComparison.Ordinal))
            {
                return;
            }

            videoUrl = video.VideoURL ?? string.Empty;
        }

        if (videoUrl.Trim().Length == 0)
        {
            return;
        }

        using var download = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        download.CancelAfter(DownloadTimeout);

        using var response = await OpenVideoStreamAsync(videoUrl, download.Token)
            .ConfigureAwait(false);

        // The reference saves with the scan context, not the download context
        // (worker.go:198): a slow body read is bounded by the download token,
        // the storage write by the scan.
        var filename = FilenameFromResponse(response, videoUrl);
        var storageKey = GenerateVideoKey(request.ProjectId, request.Id, filename);

        long bytesWritten;
        string savedKey;
        try
        {
            using var backend = backends.Create(storage);
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            (savedKey, bytesWritten) = await backend
                .WriteFromStreamAsync(
                    storageKey,
                    new LimitedReadStream(stream, MaxDownloadBytes),
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
                "failed to save video to storage: " + ex.Message,
                ex);
        }

        var now = time.GetUtcNow().UtcDateTime;
        bool updated;
        try
        {
            updated = await requests
                .MarkContentSavedAsync(request.Id, storage.Id, savedKey, now, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "failed to update request video saved status: " + ex.Message,
                ex);
        }

        if (!updated)
        {
            throw new InvalidOperationException(
                "failed to update request video saved status: ent: request not found");
        }

        LogSaved(logger, request.Id, storage.Id, savedKey, bytesWritten, null);
    }

    /// <summary>
    /// Port of extractVideoURLFromResponseBody (worker.go:233-244): an empty or
    /// malformed body yields no URL, like the ignored unmarshal error.
    /// </summary>
    private static string ExtractVideoUrlFromResponseBody(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        try
        {
            return LlmJson.Deserialize<VideoResponse>(raw)?.VideoURL ?? string.Empty;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException)
        {
            return string.Empty;
        }
    }

    /// <summary>Port of openVideoStream (worker.go:246-274).</summary>
    private async Task<HttpResponseMessage> OpenVideoStreamAsync(
        string videoUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(videoUrl, UriKind.RelativeOrAbsolute, out var parsed))
        {
            throw new InvalidOperationException(
                "invalid video URL: parse \"" + videoUrl + "\": invalid URI");
        }

        var scheme = parsed.IsAbsoluteUri ? parsed.Scheme : string.Empty;
        if (!string.Equals(scheme, "http", StringComparison.Ordinal)
            && !string.Equals(scheme, "https", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("invalid URL scheme: " + scheme);
        }

        HttpRequestMessage httpRequest;
        try
        {
            httpRequest = new HttpRequestMessage(HttpMethod.Get, videoUrl);
        }
        catch (Exception ex) when (ex is UriFormatException or FormatException or ArgumentException)
        {
            throw new InvalidOperationException("failed to create request: " + ex.Message, ex);
        }

        HttpResponseMessage response;
        using (httpRequest)
        {
            try
            {
                response = await _http
                    .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "failed to download video: context deadline exceeded");
            }
            catch (HttpRequestException ex)
            {
                throw new InvalidOperationException(
                    "failed to download video: " + ex.Message,
                    ex);
            }
        }

        if ((int)response.StatusCode != 200)
        {
            var statusCode = (int)response.StatusCode;
            response.Dispose();
            throw new InvalidOperationException(
                "failed to download video: HTTP "
                + statusCode.ToString(CultureInfo.InvariantCulture));
        }

        return response;
    }

    /// <summary>Port of filenameFromResponse (worker.go:276-298).</summary>
    private static string FilenameFromResponse(HttpResponseMessage response, string fallbackUrl)
    {
        if (response.Content.Headers.TryGetValues("Content-Disposition", out var values))
        {
            var contentDisposition = values.FirstOrDefault();
            if (!string.IsNullOrEmpty(contentDisposition))
            {
                var marker = contentDisposition.IndexOf("filename=", StringComparison.Ordinal);
                if (marker >= 0)
                {
                    var after = contentDisposition[(marker + "filename=".Length)..].Trim();
                    after = after.Trim('"');
                    if (after.Length > 0)
                    {
                        return after;
                    }
                }
            }
        }

        var url = fallbackUrl;
        var query = url.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            url = url[..query];
        }

        var name = GoFilepath.Base(url);
        if (name is "." or "/" or "")
        {
            return "video-"
                + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                + ".mp4";
        }

        return name;
    }

    /// <summary>Port of GenerateVideoKey (worker.go:224-231).</summary>
    public static string GenerateVideoKey(long projectId, long requestId, string filename)
    {
        var name = (filename ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            name = "video.mp4";
        }

        name = GoFilepath.Base(name);

        return "/" + projectId.ToString(CultureInfo.InvariantCulture)
            + "/requests/" + requestId.ToString(CultureInfo.InvariantCulture)
            + "/video/" + name;
    }
}

/// <summary>
/// Go's <c>filepath.Base</c> for the running platform: separators follow
/// <see cref="Path.DirectorySeparatorChar"/> (with <c>/</c> also accepted on
/// Windows), trailing separators are trimmed, an empty path is <c>.</c> and an
/// all-separator path yields the separator itself.
/// </summary>
internal static class GoFilepath
{
    public static string Base(string path)
    {
        if (path.Length == 0)
        {
            return ".";
        }

        var end = path.Length;
        while (end > 0 && IsSeparator(path[end - 1]))
        {
            end--;
        }

        if (end == 0)
        {
            return Path.DirectorySeparatorChar.ToString();
        }

        var start = end;
        while (start > 0 && !IsSeparator(path[start - 1]))
        {
            start--;
        }

        return path[start..end];
    }

    private static bool IsSeparator(char value)
        => value == Path.DirectorySeparatorChar
            || (Path.DirectorySeparatorChar == '\\' && value == '/');
}

/// <summary>
/// Go's <c>io.LimitReader</c>: reads at most <paramref name="limit"/> bytes and
/// then reports end of stream without an error.
/// </summary>
internal sealed class LimitedReadStream(Stream inner, long limit) : Stream
{
    private long _remaining = limit;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
        _remaining -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = await inner
            .ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken)
            .ConfigureAwait(false);
        _remaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
