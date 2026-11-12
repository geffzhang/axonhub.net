using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Management;
using AxonHub.Api.Persistence;
using AxonHub.Api.Settings;
using AxonHub.Api.Storage;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the two request-detail REST routes of routes.go:145-152:
/// GET /admin/requests/:request_id/content (api/request_content.go) and
/// GET /admin/requests/:request_id/preview (api/request_live.go). Both sit on
/// the admin JWT gate and require X-Project-ID; the preview route tails a live
/// buffer when one is registered and serves the static-fetch fallback
/// otherwise.
/// </summary>
public sealed class AdminRequestContentEndpointTests : IAsyncLifetime, IDisposable
{
    private const string Secret = "test-secret";
    private const long ProjectId = 1;
    private const long OtherProjectId = 2;
    private const long RequestId = 42;
    private const string ContentKey = "/1/requests/42/video/video.mp4";

    private readonly FakeUserStore _users = new();
    private readonly FakeSystemStore _systems = new();
    private readonly FakeUserProfileStore _profiles = new();
    private readonly FakePasswordHasher _passwords = new();
    private readonly FakeOidcLoginOnlyProviders _oidc = new();
    private readonly FakeRequestStore _requests = new();
    private readonly FakeDataStorageStore _dataStorages = new();
    private readonly FakeDataStorageBackends _backends = new();
    private readonly LiveStreamRegistry _liveStreams = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private UserRecord _user = null!;
    private string? _tempDirectory;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IUserStore>(_users);
        builder.Services.AddSingleton<ISystemStore>(_systems);
        builder.Services.AddSingleton<IUserProfileStore>(_profiles);
        builder.Services.AddSingleton<IPasswordHasher>(_passwords);
        builder.Services.AddSingleton<IOidcLoginOnlyProviders>(_oidc);
        builder.Services.AddSingleton<IRequestStore>(_requests);
        builder.Services.AddSingleton<IDataStorageStore>(_dataStorages);
        builder.Services.AddSingleton<IDataStorageBackendFactory>(_backends);
        builder.Services.AddSingleton(_liveStreams);
        builder.Services.AddSingleton<ISystemSettingsAdminService>(
            new SystemSettingsAdminService(_systems));
        builder.Services.AddScoped<RequestPayloadStorage>();
        builder.Services.AddScoped<ManagementAuthService>();
        builder.Services.AddScoped<IManagementIdentity, HttpContextManagementIdentity>();

        _user = _users.Add(row =>
        {
            row.Id = 11;
            row.Email = "owner@example.com";
            row.IsOwner = true;
        });
        _systems.Add(ManagementAuthService.SecretKeyKey, Secret);

        _app = builder.Build();

        _app.UseWhen(
            static context => context.Request.Path.StartsWithSegments("/admin/requests"),
            static branch =>
            {
                branch.UseMiddleware<AdminJwtAuthMiddleware>();
                branch.UseMiddleware<ProjectIdMiddleware>();
            });

        _app.MapRequestDetailRoutes();

        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    public void Dispose()
    {
        if (_tempDirectory is not null && Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    // ---------------- admin gate and shared binding ----------------

    [Theory]
    [InlineData("/admin/requests/42/content")]
    [InlineData("/admin/requests/42/preview")]
    public async Task RoutesRequireTheJwtGate(string path)
    {
        using var response = await GetAsync(path, authenticated: false, projectId: null);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("API key is required", Message(body));
        Assert.Equal("Unauthorized", ErrorType(body));
    }

    [Theory]
    [InlineData("/admin/requests/42/content")]
    [InlineData("/admin/requests/42/preview")]
    public async Task RoutesRequireTheProjectHeader(string path)
    {
        using var response = await GetAsync(path, projectId: null);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Project ID not found in context", Message(body));
    }

    [Theory]
    [InlineData("/admin/requests/42/content")]
    [InlineData("/admin/requests/42/preview")]
    public async Task RoutesValidateTheProjectHeader(string path)
    {
        using var response = await GetAsync(path, projectId: "not-a-gid");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid project ID", Message(body));
    }

    [Theory]
    [InlineData("/admin/requests/abc/content")]
    [InlineData("/admin/requests/abc/preview")]
    public async Task RoutesUseTheGinUriBindingError(string path)
    {
        using var response = await GetAsync(path);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "Invalid request body: strconv.ParseInt: parsing \"abc\": invalid syntax",
            Message(body));
    }

    [Theory]
    [InlineData("/admin/requests/99999999999999999999/content")]
    [InlineData("/admin/requests/99999999999999999999/preview")]
    public async Task RoutesReportOutOfRangeRequestIds(string path)
    {
        using var response = await GetAsync(path);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "Invalid request body: strconv.ParseInt: parsing "
            + "\"99999999999999999999\": value out of range",
            Message(body));
    }

    [Theory]
    [InlineData("/admin/requests/7/content")]
    [InlineData("/admin/requests/7/preview")]
    public async Task RoutesReturn404ForUnknownRequests(string path)
    {
        using var response = await GetAsync(path);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Request not found", Message(body));
    }

    [Theory]
    [InlineData("/admin/requests/42/content")]
    [InlineData("/admin/requests/42/preview")]
    public async Task RoutesHideRequestsOfAnotherProject(string path)
    {
        SeedRequest(row => row.ProjectId = OtherProjectId);

        using var response = await GetAsync(path);
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Request not found", Message(body));
    }

    [Fact]
    public async Task RequestLoadFailureIsA500()
    {
        _requests.GetByIdFailure = new InvalidOperationException("boom");

        using var response = await GetAsync("/admin/requests/42/content");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Failed to load request", Message(body));
    }

    // ---------------- GET /admin/requests/:id/content ----------------

    [Theory]
    [InlineData(false, 5L, ContentKey)]
    [InlineData(true, null, ContentKey)]
    [InlineData(true, 5L, null)]
    [InlineData(true, 5L, "   ")]
    public async Task ContentRequiresASavedNonEmptyKey(
        bool contentSaved,
        long? storageId,
        string? storageKey)
    {
        SeedRequest(row =>
        {
            row.ContentSaved = contentSaved;
            row.ContentStorageId = storageId;
            row.ContentStorageKey = storageKey;
        });

        using var response = await GetAsync("/admin/requests/42/content");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Content not found", Message(body));
    }

    [Fact]
    public async Task ContentKeyMustStayInsideTheRequestPrefix()
    {
        SeedRequest(row =>
        {
            row.ContentSaved = true;
            row.ContentStorageId = 5;
            row.ContentStorageKey = "/1/requests/43/video/video.mp4";
        });

        using var response = await GetAsync("/admin/requests/42/content");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Content not found", Message(body));
    }

    [Fact]
    public async Task ContentStorageMustExist()
    {
        SeedRequest(row =>
        {
            row.ContentSaved = true;
            row.ContentStorageId = 99;
            row.ContentStorageKey = ContentKey;
        });

        using var response = await GetAsync("/admin/requests/42/content");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Content storage not found", Message(body));
    }

    [Fact]
    public async Task ContentStorageLoadFailureIsA500()
    {
        SeedRequest(row =>
        {
            row.ContentSaved = true;
            row.ContentStorageId = 5;
            row.ContentStorageKey = ContentKey;
        });
        _dataStorages.GetByIdFailure = new InvalidOperationException("boom");

        using var response = await GetAsync("/admin/requests/42/content");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Failed to load content storage", Message(body));
    }

    [Theory]
    [InlineData(true, "fs")]
    [InlineData(false, "database")]
    public async Task PrimaryAndDatabaseStoragesAreNotFileBased(bool primary, string type)
    {
        SeedRequest(row =>
        {
            row.ContentSaved = true;
            row.ContentStorageId = 5;
            row.ContentStorageKey = ContentKey;
        });
        SeedStorage(
            type,
            new DataStorageSettings(),
            primary: primary);

        using var response = await GetAsync("/admin/requests/42/content");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Content storage is not file-based", Message(body));
    }

    [Fact]
    public async Task ContentServesFsDirectoryFiles()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "axonhub-content-" + Guid.NewGuid().ToString("N"));
        var fullPath = Path.Combine(
            _tempDirectory,
            "1",
            "requests",
            "42",
            "video",
            "video.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, Encoding.UTF8.GetBytes("video-content"));

        SeedRequest(row =>
        {
            row.ContentSaved = true;
            row.ContentStorageId = 5;
            row.ContentStorageKey = ContentKey;
        });
        SeedStorage("fs", new DataStorageSettings { Directory = _tempDirectory });

        using var response = await GetAsync("/admin/requests/42/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "video-content",
            await response.Content.ReadAsStringAsync());
        Assert.Equal(
            "attachment; filename=\"video.mp4\"",
            response.Content.Headers.NonValidated["Content-Disposition"].ToString());
        Assert.Equal("video/mp4", response.Content.Headers.ContentType!.MediaType);

        // The fs fast path bypasses buildFileSystem in the reference too.
        Assert.Empty(_backends.Created);
    }

    [Fact]
    public async Task ContentFsDirectoryTraversalNeverLeavesTheDirectory()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "axonhub-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);

        SeedRequest(row =>
        {
            row.ContentSaved = true;
            row.ContentStorageId = 5;
            row.ContentStorageKey = "/1/requests/42/../../../../etc/passwd";
        });
        SeedStorage("fs", new DataStorageSettings { Directory = _tempDirectory });

        using var response = await GetAsync("/admin/requests/42/content");

        // filepath.Clean drops the leading ".." on Windows (the path is
        // rooted after FromSlash) and rejects it on Unix; both answer 404.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // ServeFile's Error, not the gin JSON envelope.
        Assert.Equal("404 page not found\n", await response.Content.ReadAsStringAsync());
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task FsDirectoryUsesRfc5987ForNonAsciiNames()
    {
        _tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "axonhub-content-" + Guid.NewGuid().ToString("N"));
        const string key = "/1/requests/42/video/视频.mp4";
        var fullPath = Path.Combine(_tempDirectory, "1", "requests", "42", "video", "视频.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, Encoding.UTF8.GetBytes("video-content"));

        SeedRequest(row =>
        {
            row.ContentSaved = true;
            row.ContentStorageId = 5;
            row.ContentStorageKey = key;
        });
        SeedStorage("fs", new DataStorageSettings { Directory = _tempDirectory });

        using var response = await GetAsync("/admin/requests/42/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "attachment; filename*=UTF-8''%E8%A7%86%E9%A2%91.mp4",
            response.Content.Headers.NonValidated["Content-Disposition"].ToString());
    }

    [Fact]
    public async Task ContentFsWithoutDirectoryReadsThroughTheBackend()
    {
        SeedRequest(row =>
        {
            row.ContentSaved = true;
            row.ContentStorageId = 5;
            row.ContentStorageKey = ContentKey;
        });
        SeedStorage("fs", new DataStorageSettings());

        var objectKey = AdminRequestEndpoints.FromSlash(ContentKey);
        _backends.Backend.Add(objectKey, Encoding.UTF8.GetBytes("video-content"), DateTimeOffset.UtcNow);

        using var response = await GetAsync("/admin/requests/42/content");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video-content", Encoding.UTF8.GetString(bytes));
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(
            "attachment; filename=\"video.mp4\"",
            response.Content.Headers.NonValidated["Content-Disposition"].ToString());
        Assert.Equal(
            "private, max-age=0, no-cache",
            response.Headers.NonValidated["Cache-Control"].ToString());
        Assert.Equal(bytes.Length, response.Content.Headers.ContentLength!.Value);
        Assert.Single(_backends.Created);
    }

    [Fact]
    public async Task S3PathStyleTrimsTheLeadingSlash()
    {
        SeedRequest(row =>
        {
            row.ContentSaved = true;
            row.ContentStorageId = 5;
            row.ContentStorageKey = ContentKey;
        });
        SeedStorage(
            "s3",
            new DataStorageSettings
            {
                S3 = new S3StorageSettings { PathStyle = true },
            });

        _backends.Backend.Add(
            "1/requests/42/video/video.mp4",
            Encoding.UTF8.GetBytes("video-content"),
            DateTimeOffset.UtcNow);

        using var response = await GetAsync("/admin/requests/42/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video-content", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task OtherStorageTypesKeepTheLeadingSlash()
    {
        SeedRequest(row =>
        {
            row.ContentSaved = true;
            row.ContentStorageId = 5;
            row.ContentStorageKey = ContentKey;
        });
        SeedStorage("gcs", new DataStorageSettings());

        _backends.Backend.Add(ContentKey, Encoding.UTF8.GetBytes("video-content"), DateTimeOffset.UtcNow);

        using var response = await GetAsync("/admin/requests/42/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("video-content", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MissingObjectsAre404ContentNotFound()
    {
        SeedRequest(row =>
        {
            row.ContentSaved = true;
            row.ContentStorageId = 5;
            row.ContentStorageKey = ContentKey;
        });
        SeedStorage("fs", new DataStorageSettings());

        using var response = await GetAsync("/admin/requests/42/content");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Content not found", Message(body));
    }

    [Fact]
    public async Task BackendReadFailuresAre404ContentNotFound()
    {
        SeedRequest(row =>
        {
            row.ContentSaved = true;
            row.ContentStorageId = 5;
            row.ContentStorageKey = ContentKey;
        });
        SeedStorage("fs", new DataStorageSettings());
        _backends.Backend.ReadFailure = new IOException("boom");

        using var response = await GetAsync("/admin/requests/42/content");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("Content not found", Message(body));
    }

    [Fact]
    public async Task BackendConstructionFailuresAreA500()
    {
        SeedRequest(row =>
        {
            row.ContentSaved = true;
            row.ContentStorageId = 5;
            row.ContentStorageKey = ContentKey;
        });
        SeedStorage("fs", new DataStorageSettings());
        _backends.Failure = new InvalidOperationException("directory not configured for fs storage");

        using var response = await GetAsync("/admin/requests/42/content");
        var body = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("Failed to open content storage", Message(body));
    }

    [Fact]
    public async Task ContentDispositionQuotesTheFilenameLikeGo()
    {
        SeedRequest(row =>
        {
            row.ContentSaved = true;
            row.ContentStorageId = 5;
            row.ContentStorageKey = "/1/requests/42/video/a\"b.mp4";
        });
        SeedStorage("fs", new DataStorageSettings());
        _backends.Backend.Add(
            AdminRequestEndpoints.FromSlash("/1/requests/42/video/a\"b.mp4"),
            Encoding.UTF8.GetBytes("video-content"),
            DateTimeOffset.UtcNow);

        using var response = await GetAsync("/admin/requests/42/content");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "attachment; filename=\"a\\\"b.mp4\"",
            response.Content.Headers.NonValidated["Content-Disposition"].ToString());
    }

    // ---------------- GET /admin/requests/:id/preview ----------------

    /// <summary>
    /// writeSSEStreamEnd's trailing event: requestPreviewStream does not
    /// implement ExpectedStreamChoices, so the terminal tracker never sees the
    /// preview.completed marker and the writer reports an incomplete stream
    /// (chat.go:298, orchestrator.ErrStreamIncomplete).
    /// </summary>
    private const string IncompleteStreamErrorEvent =
        "event:error\n"
        + "data:{\"error\":{\"code\":\"upstream_stream_interrupted\","
        + "\"message\":\"Upstream provider closed the connection before the response completed: "
        + "stream ended without terminal event or completed response\","
        + "\"type\":\"upstream_error\"},\"request_id\":\"\"}\n\n";

    [Fact]
    public async Task PreviewTailsAnActiveBufferAsReplayThenCompleted()
    {
        SeedRequest(row =>
        {
            row.Stream = true;
            row.Status = "processing";
        });
        var buffer = _liveStreams.CreateBuffer();
        buffer.Append(TestChannels.Data("{\"a\":1}", "data"));
        buffer.Append(TestChannels.Data("[DONE]", "data"));
        buffer.Append(TestChannels.Data("{\"b\":2}", "data"));
        _liveStreams.RegisterRequest(RequestId, buffer);
        buffer.Close();

        using var response = await GetAsync("/admin/requests/42/preview");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(
            "event:preview.replay\ndata:{\"a\":1}\n\n"
            + "event:preview.replay\ndata:{\"b\":2}\n\n"
            + "event:preview.completed\ndata:{\"status\":\"completed\"}\n\n"
            + IncompleteStreamErrorEvent,
            text);

        // The live branch never unregisters the buffer: the orchestrator's
        // stream close owns that.
        Assert.NotNull(_liveStreams.GetRequestBuffer(RequestId));
    }

    [Fact]
    public async Task PreviewStreamsChunksAppendedAfterTheSubscription()
    {
        SeedRequest(row =>
        {
            row.Stream = true;
            row.Status = "processing";
        });
        var buffer = _liveStreams.CreateBuffer();
        buffer.Append(TestChannels.Data("{\"first\":1}", "data"));
        _liveStreams.RegisterRequest(RequestId, buffer);

        using var request = NewRequest("/admin/requests/42/preview");
        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);

        // The handler flushed the SSE headers only after RequestPreviewStream
        // subscribed, so this append is incremental rather than replayed.
        buffer.Append(TestChannels.Data("{\"second\":2}", "data"));
        buffer.Close();

        var text = await response.Content.ReadAsStringAsync();

        Assert.Contains(
            "event:preview.replay\ndata:{\"first\":1}\n\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "event:preview.chunk\ndata:{\"second\":2}\n\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "event:preview.completed\ndata:{\"status\":\"completed\"}\n\n",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreviewCompletedRequestsIgnoreALiveBuffer()
    {
        SeedRequest(row =>
        {
            row.Stream = true;
            row.Status = "completed";
            row.ResponseChunks = "[{\"persisted\":true}]";
        });
        var buffer = _liveStreams.CreateBuffer();
        buffer.Append(TestChannels.Data("{\"live\":true}", "data"));
        _liveStreams.RegisterRequest(RequestId, buffer);

        using var response = await GetAsync("/admin/requests/42/preview");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "{\"mode\":\"static-fetch\",\"responseChunks\":[{\"persisted\":true}]}",
            text);
    }

    [Fact]
    public async Task PreviewServesPersistedChunksAsStaticFetch()
    {
        SeedRequest(row => row.ResponseChunks = "[{\"persisted\":true}]");

        using var response = await GetAsync("/admin/requests/42/preview");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(
            "{\"mode\":\"static-fetch\",\"responseChunks\":[{\"persisted\":true}]}",
            text);
    }

    [Fact]
    public async Task PreviewCompactsRawChunksLikeEncodingJson()
    {
        SeedRequest(row => row.ResponseChunks = "[ { \"a\" : 1 } , { \"b\" : [ 2 ] } ]");

        using var response = await GetAsync("/admin/requests/42/preview");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "{\"mode\":\"static-fetch\",\"responseChunks\":[{\"a\":1},{\"b\":[2]}]}",
            text);
    }

    [Fact]
    public async Task PreviewLoadsChunksFromExternalStorage()
    {
        var storage = SeedStorage("fs", new DataStorageSettings(), id: 5);
        SeedRequest(row =>
        {
            row.Stream = true;
            row.Status = "completed";
            row.DataStorageId = storage.Id;
        });
        _backends.Backend.Add(
            "/1/requests/42/response_chunks.json",
            Encoding.UTF8.GetBytes("[{\"from\":\"storage\"}]"),
            DateTimeOffset.UtcNow);

        using var response = await GetAsync("/admin/requests/42/preview");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "{\"mode\":\"static-fetch\",\"responseChunks\":[{\"from\":\"storage\"}]}",
            text);
    }

    [Fact]
    public async Task PreviewActiveStreamWithoutABufferIsEmptyStaticFetch()
    {
        SeedRequest(row =>
        {
            row.Stream = true;
            row.Status = "processing";
        });

        using var response = await GetAsync("/admin/requests/42/preview");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "{\"mode\":\"static-fetch\",\"responseChunks\":[]}",
            text);
    }

    [Fact]
    public async Task PreviewNonStreamRequestsAreEmptyStaticFetch()
    {
        SeedRequest(row =>
        {
            row.Stream = false;
            row.Status = "completed";
        });

        using var response = await GetAsync("/admin/requests/42/preview");
        var text = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            "{\"mode\":\"static-fetch\",\"responseChunks\":[]}",
            text);
    }

    // ---------------- helpers ----------------

    private RequestRecord SeedRequest(Action<RequestRecord>? configure = null)
    {
        var row = new RequestRecord
        {
            Id = RequestId,
            ProjectId = ProjectId,
            Status = "completed",
        };
        configure?.Invoke(row);
        return _requests.Add(row);
    }

    private DataStorageRecord SeedStorage(
        string type,
        DataStorageSettings settings,
        bool primary = false,
        long id = 5)
        => _dataStorages.Seed(row =>
        {
            row.Id = id;
            row.Type = type;
            row.Primary = primary;
            row.Settings = DataStorageSettingsCodec.Serialize(settings);
            row.Status = "active";
        });

    private async Task<HttpResponseMessage> GetAsync(
        string path,
        bool authenticated = true,
        string? projectId = "gid://axonhub/Project/1",
        string? token = null)
    {
        using var request = NewRequest(path, authenticated, projectId, token);
        return await _client.SendAsync(request);
    }

    private HttpRequestMessage NewRequest(
        string path,
        bool authenticated = true,
        string? projectId = "gid://axonhub/Project/1",
        string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (authenticated)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                token ?? IssueToken(_user.Id));
        }

        if (projectId is not null)
        {
            request.Headers.Add("X-Project-ID", projectId);
        }

        return request;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static string ErrorType(JsonElement body)
        => body.GetProperty("error").GetProperty("type").GetString()!;

    private static string Message(JsonElement body)
        => body.GetProperty("error").GetProperty("message").GetString()!;

    private static string IssueToken(long userId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return JwtTokenCodec.EncodeHs256(Secret, userId, now, now, now + 3600);
    }
}
