using System.Net;
using System.Text;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Settings;
using AxonHub.Api.Storage;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of internal/server/video_storage/worker_test.go (C234): the scan gates
/// (disabled, missing storage id, primary/database storage), the row query
/// limit, the response-body/provider URL lookup, the download rules, the
/// filename/key derivation and the content-columns write. The row SQL itself is
/// locked by AxonHub.Persistence.Tests.
/// </summary>
public sealed class VideoStorageWorkerTests
{
    private const long StorageId = 7;

    [Fact]
    public async Task DisabledSettingsSkipTheScan()
    {
        var harness = new Harness(enabled: false);
        harness.Requests.Add(VideoRow(id: 5, responseBody: VideoUrlBody("https://cdn.test/a.mp4")));

        await harness.Worker.ScanAndSaveAsync();

        Assert.Empty(harness.Http.Requests);
        Assert.Empty(harness.Backends.Created);
        Assert.Empty(harness.Requests.ContentSavedWrites);
    }

    [Fact]
    public async Task EnabledWithoutAStorageIdFailsBeforeScanning()
    {
        var harness = new Harness(enabled: true, dataStorageId: 0);
        harness.Requests.Add(VideoRow(id: 5, responseBody: VideoUrlBody("https://cdn.test/a.mp4")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Worker.ScanAndSaveAsync());

        Assert.Equal("video storage enabled but data_storage_id is not set", error.Message);
    }

    [Fact]
    public async Task AMissingStorageFailsWithTheReferenceWrap()
    {
        var harness = new Harness(enabled: true, dataStorageId: 99);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Worker.ScanAndSaveAsync());

        Assert.Equal(
            "failed to get data storage: failed to get data storage by ID 99: ent: data storage not found",
            error.Message);
    }

    [Fact]
    public async Task PrimaryAndDatabaseStoragesAreRejected()
    {
        var primary = new Harness();
        primary.Storages.Storages[StorageId].Primary = true;

        var primaryError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => primary.Worker.ScanAndSaveAsync());
        Assert.Equal("video storage must be non-database storage", primaryError.Message);

        var database = new Harness();
        database.Storages.Storages[StorageId].Type = "database";

        var databaseError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.Worker.ScanAndSaveAsync());
        Assert.Equal("video storage must be non-database storage", databaseError.Message);
    }

    [Fact]
    public async Task AQueryFailureIsWrappedLikeTheReference()
    {
        var harness = new Harness();
        harness.Requests.ListUnsavedVideoRequestsFailure =
            new InvalidOperationException("connection refused");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Worker.ScanAndSaveAsync());

        Assert.Equal(
            "failed to query video requests: connection refused",
            error.Message);
    }

    [Fact]
    public async Task AResponseBodyUrlIsDownloadedAndTheRowIsMarked()
    {
        var harness = new Harness();
        harness.Requests.Add(
            VideoRow(id: 5, responseBody: VideoUrlBody("https://cdn.test/media/clip.mp4?token=abc")));
        harness.Http.OnSend = _ => BinaryResponse([1, 2, 3, 4]);

        await harness.Worker.ScanAndSaveAsync();

        // The response body URL wins; the provider task is never queried.
        Assert.Equal(new[] { "https://cdn.test/media/clip.mp4?token=abc" }, harness.Http.Requests);
        Assert.Equal(0, harness.Executor.Calls);

        var write = Assert.Single(harness.Requests.ContentSavedWrites);
        Assert.Equal(5, write.Id);
        Assert.Equal(StorageId, write.DataStorageId);
        Assert.Equal("/1/requests/5/video/clip.mp4", write.StorageKey);

        var backend = Assert.Single(harness.Backends.Created);
        var saved = Assert.Single(backend.Writes);
        Assert.Equal("/1/requests/5/video/clip.mp4", saved.Key);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, saved.Data);
        Assert.True(Assert.Single(harness.Requests.Rows).ContentSaved);
    }

    [Fact]
    public async Task TheContentDispositionFilenameWinsOverTheUrl()
    {
        var harness = new Harness();
        harness.Requests.Add(
            VideoRow(id: 5, responseBody: VideoUrlBody("https://cdn.test/download?id=1")));
        harness.Http.OnSend = _ => BinaryResponse(
            [1],
            "attachment; filename=\"clip final.mp4\"");

        await harness.Worker.ScanAndSaveAsync();

        var write = Assert.Single(harness.Requests.ContentSavedWrites);
        Assert.Equal("/1/requests/5/video/clip final.mp4", write.StorageKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    public async Task AnEmptyOrMalformedBodyFallsBackToTheProviderTask(string body)
    {
        var harness = new Harness();
        harness.Channels.Add(OpenAiChannel());
        harness.Executor.OnDo = (_, _, _) => VideoTaskJson();
        harness.Requests.Add(VideoRow(id: 5, externalId: "task-1", channelId: 1001, responseBody: body));
        harness.Http.OnSend = _ => BinaryResponse([7]);

        await harness.Worker.ScanAndSaveAsync();

        Assert.Equal(1, harness.Executor.Calls);
        Assert.Equal(new[] { "https://upstream.test/video.mp4" }, harness.Http.Requests);
        var write = Assert.Single(harness.Requests.ContentSavedWrites);
        Assert.Equal("/1/requests/5/video/video.mp4", write.StorageKey);
    }

    [Fact]
    public async Task ANonSucceededProviderTaskIsNotDownloaded()
    {
        var harness = new Harness();
        harness.Channels.Add(OpenAiChannel());
        harness.Executor.OnDo = (_, _, _) => VideoTaskJson(status: "in_progress");
        harness.Requests.Add(VideoRow(id: 5, externalId: "task-1", channelId: 1001, responseBody: ""));

        await harness.Worker.ScanAndSaveAsync();

        Assert.Empty(harness.Http.Requests);
        Assert.Empty(harness.Requests.ContentSavedWrites);
        Assert.Empty(harness.Backends.Created);
    }

    [Fact]
    public async Task AProviderTaskWithoutAVideoUrlIsNotDownloaded()
    {
        var harness = new Harness();
        harness.Channels.Add(OpenAiChannel());
        harness.Executor.OnDo = (_, _, _) => VideoTaskJson(videoUrl: string.Empty);
        harness.Requests.Add(VideoRow(id: 5, externalId: "task-1", channelId: 1001, responseBody: ""));

        await harness.Worker.ScanAndSaveAsync();

        Assert.Empty(harness.Http.Requests);
        Assert.Empty(harness.Requests.ContentSavedWrites);
    }

    [Fact]
    public async Task ANonHttpUrlFailsOnlyItsRow()
    {
        var harness = new Harness();
        harness.Requests.Add(VideoRow(id: 5, responseBody: VideoUrlBody("file:///etc/passwd")));
        harness.Requests.Add(VideoRow(id: 6, responseBody: VideoUrlBody("https://cdn.test/ok.mp4")));
        harness.Http.OnSend = _ => BinaryResponse([1]);

        await harness.Worker.ScanAndSaveAsync();

        Assert.Equal(new[] { "https://cdn.test/ok.mp4" }, harness.Http.Requests);
        var write = Assert.Single(harness.Requests.ContentSavedWrites);
        Assert.Equal(6, write.Id);
    }

    [Fact]
    public async Task ANonOkStatusFailsOnlyItsRow()
    {
        var harness = new Harness();
        harness.Requests.Add(VideoRow(id: 5, responseBody: VideoUrlBody("https://cdn.test/fail.mp4")));
        harness.Requests.Add(VideoRow(id: 6, responseBody: VideoUrlBody("https://cdn.test/ok.mp4")));
        harness.Http.OnSend = request => request.RequestUri!.AbsolutePath.Contains("fail")
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : BinaryResponse([1]);

        await harness.Worker.ScanAndSaveAsync();

        Assert.Equal(2, harness.Http.Requests.Count);
        var write = Assert.Single(harness.Requests.ContentSavedWrites);
        Assert.Equal(6, write.Id);
    }

    [Fact]
    public async Task AFailedContentUpdateDoesNotStopTheScan()
    {
        var harness = new Harness();
        harness.Requests.Add(VideoRow(id: 5, responseBody: VideoUrlBody("https://cdn.test/a.mp4")));
        harness.Requests.Add(VideoRow(id: 6, responseBody: VideoUrlBody("https://cdn.test/b.mp4")));
        harness.Http.OnSend = _ => BinaryResponse([1]);
        harness.Requests.MarkContentSavedFailure =
            new InvalidOperationException("database is down");

        await harness.Worker.ScanAndSaveAsync();

        Assert.Equal(2, harness.Http.Requests.Count);
        Assert.Empty(harness.Requests.ContentSavedWrites);
    }

    [Fact]
    public async Task TheScanLimitBoundsTheRowsOfOnePass()
    {
        var harness = new Harness(scanLimit: 1);
        harness.Requests.Add(VideoRow(id: 5, responseBody: VideoUrlBody("https://cdn.test/first.mp4")));
        harness.Requests.Add(VideoRow(id: 6, responseBody: VideoUrlBody("https://cdn.test/second.mp4")));
        harness.Http.OnSend = _ => BinaryResponse([1]);

        await harness.Worker.ScanAndSaveAsync();

        Assert.Equal(new[] { "https://cdn.test/first.mp4" }, harness.Http.Requests);
        Assert.Equal(5, Assert.Single(harness.Requests.ContentSavedWrites).Id);
    }

    [Fact]
    public async Task ANonPositiveScanLimitFallsBackToFifty()
    {
        var settings = new FakeGcSettingsService
        {
            VideoStorage = new VideoStorageSettings
            {
                Enabled = true,
                DataStorageId = (int)StorageId,
                ScanIntervalMinutes = 1,
                ScanLimit = 0,
            },
        };

        var harness = Harness.With(settings);
        for (var id = 1; id <= 60; id++)
        {
            harness.Requests.Add(VideoRow(id: id, responseBody: VideoUrlBody("https://cdn.test/" + id + ".mp4")));
        }

        harness.Http.OnSend = _ => BinaryResponse([1]);

        await harness.Worker.ScanAndSaveAsync();

        Assert.Equal(VideoStorageWorker.DefaultScanLimit, harness.Http.Requests.Count);
    }

    [Fact]
    public async Task AUrlWithoutAFileComponentFallsBackToATimestampedName()
    {
        var harness = new Harness();
        harness.Requests.Add(VideoRow(id: 5, responseBody: VideoUrlBody("https://cdn.test/.")));
        harness.Http.OnSend = _ => BinaryResponse([1]);

        await harness.Worker.ScanAndSaveAsync();

        var write = Assert.Single(harness.Requests.ContentSavedWrites);
        Assert.Matches(@"^/1/requests/5/video/video-\d+\.mp4$", write.StorageKey);
    }

    [Theory]
    [InlineData(" clip.mp4 ", "/2/requests/9/video/clip.mp4")]
    [InlineData("dir/sub/clip.mp4", "/2/requests/9/video/clip.mp4")]
    [InlineData("/clip.mp4", "/2/requests/9/video/clip.mp4")]
    [InlineData("", "/2/requests/9/video/video.mp4")]
    public void GenerateVideoKeyNormalizesTheFilename(string filename, string expected)
    {
        Assert.Equal(expected, VideoStorageWorker.GenerateVideoKey(2, 9, filename));
    }

    private static RequestRecord VideoRow(
        long id,
        string? responseBody = null,
        string? externalId = null,
        long? channelId = null)
        => new()
        {
            Id = id,
            ProjectId = 1,
            Status = "completed",
            Format = ApiFormats.OpenAIVideo,
            ResponseBody = responseBody,
            ExternalId = externalId,
            ChannelId = channelId,
        };

    private static string VideoUrlBody(string url)
        => "{\"video_url\":\"" + url + "\"}";

    private static ChannelRecord OpenAiChannel()
        => new()
        {
            Id = 1001,
            Name = "channel-1001",
            Type = "openai",
            BaseUrl = "http://upstream.test",
            Status = "enabled",
            Credentials = "{\"apiKey\":\"sk-test\"}",
            SupportedModels = "[\"sora-2\"]",
        };

    private static HttpResponseData VideoTaskJson(
        string status = "completed",
        string videoUrl = "https://upstream.test/video.mp4")
        => new()
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(
                "{\"id\":\"task-1\",\"object\":\"video\",\"status\":\"" + status + "\","
                + "\"model\":\"sora-2\",\"prompt\":\"a cat walking\",\"seconds\":\"4\","
                + "\"video_url\":\"" + videoUrl + "\",\"progress\":100,\"created_at\":1700000000}"),
        };

    private static HttpResponseMessage BinaryResponse(
        byte[] body,
        string? contentDisposition = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };

        if (contentDisposition is not null)
        {
            response.Content.Headers.TryAddWithoutValidation(
                "Content-Disposition",
                contentDisposition);
        }

        return response;
    }

    private sealed class Harness
    {
        private Harness(ISystemSettingsAdminService settings, bool registerStorage)
        {
            if (registerStorage)
            {
                Storages.Storages[StorageId] = new DataStorageRecord
                {
                    Id = StorageId,
                    Name = "videos",
                    Type = "fs",
                    Settings = "{\"path\":\"/tmp/videos\"}",
                };
            }

            var payloads = new RequestPayloadStorage(Storages, settings, Backends);
            var videos = new VideoService(
                Requests,
                Executions,
                Channels,
                Executor,
                TimeProvider.System);
            Worker = new VideoStorageWorker(
                settings,
                Requests,
                payloads,
                videos,
                Backends,
                TimeProvider.System,
                NullLogger<VideoStorageWorker>.Instance,
                new HttpClient(Http));
        }

        public FakeRequestStore Requests { get; } = new();

        public FakeRequestExecutionStore Executions { get; } = new();

        public FakeChannelStore Channels { get; } = new();

        public FakeGcDataStorageStore Storages { get; } = new();

        public CapturingVideoStorageBackendFactory Backends { get; } = new();

        public ScriptedExecutor Executor { get; } = new();

        public ScriptedVideoHttpHandler Http { get; } = new();

        public VideoStorageWorker Worker { get; }

        public Harness(bool enabled = true, int dataStorageId = (int)StorageId, int scanLimit = 50)
            : this(CreateSettings(enabled, dataStorageId, scanLimit), registerStorage: true)
        {
        }

        public static Harness With(ISystemSettingsAdminService settings)
            => new(settings, registerStorage: true);

        private static SystemSettingsAdminService CreateSettings(
            bool enabled,
            int dataStorageId,
            int scanLimit)
        {
            var systems = new FakeSystemStore();
            systems.Add(
                SystemSettingsAdminService.VideoStorageSettingsKey,
                SystemSettingsCodec.SerializeVideoStorage(new VideoStorageSettings
                {
                    Enabled = enabled,
                    DataStorageId = dataStorageId,
                    ScanIntervalMinutes = 1,
                    ScanLimit = scanLimit,
                }));

            return new SystemSettingsAdminService(systems);
        }
    }

    private sealed class ScriptedVideoHttpHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? OnSend { get; set; }

        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            Requests.Add(request.RequestUri?.ToString() ?? string.Empty);
            if (OnSend is not { } handler)
            {
                throw new InvalidOperationException("OnSend is not configured");
            }

            return Task.FromResult(handler(request));
        }
    }

    private sealed class CapturingVideoStorageBackendFactory : IDataStorageBackendFactory
    {
        public List<CapturingVideoStorageBackend> Created { get; } = [];

        public IDataStorageBackend Create(DataStorageRecord storage)
        {
            var backend = new CapturingVideoStorageBackend(storage);
            Created.Add(backend);
            return backend;
        }
    }

    private sealed class CapturingVideoStorageBackend(DataStorageRecord storage) : IDataStorageBackend
    {
        public DataStorageRecord Storage { get; } = storage;

        public List<(string Key, byte[] Data)> Writes { get; } = [];

        public Task WriteAsync(string key, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public async Task<(string Key, long BytesWritten)> WriteFromStreamAsync(
            string key,
            Stream source,
            CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            var data = buffer.ToArray();
            Writes.Add((key, data));
            return (key, data.Length);
        }

        public Task<byte[]> ReadAsync(string key, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DeleteAsync(string key, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DataStorageEntry>> ListAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
