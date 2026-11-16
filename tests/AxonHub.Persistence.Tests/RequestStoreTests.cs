namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class RequestStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task CreateAndGetByIdRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        var request = NewRequest();
        var id = await store.CreateAsync(request);

        Assert.True(id > 0);

        request.Id = id;
        var actual = await store.GetByIdAsync(id);

        Assert.NotNull(actual);
        RecordAssert.Equivalent(request, actual);
    }

    [Fact]
    public async Task DefaultsMirrorGoZeroValues()
    {
        // A freshly constructed RequestRecord must persist the same values the Go
        // side writes for an unset field: nil JSONRawMessage -> "null", source and
        // format defaults, empty strings, project_id 1.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        var id = await store.CreateAsync(new RequestRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            ModelId = "gpt-4o",
            Status = "pending",
        });

        var actual = await store.GetByIdAsync(id);

        Assert.NotNull(actual);
        Assert.Equal("null", actual.RequestBody);
        Assert.Null(actual.RequestHeaders);
        Assert.Equal("api", actual.Source);
        Assert.Equal("openai/chat_completions", actual.Format);
        Assert.Equal(1L, actual.ProjectId);
        Assert.Equal(string.Empty, actual.ClientIp);
        Assert.Equal(string.Empty, actual.UserAgent);
        Assert.False(actual.Stream);
        Assert.False(actual.ContentSaved);
        Assert.Null(actual.MetricsLatencyMs);
    }

    [Fact]
    public async Task UpdateResultWritesFinalStatusAndMetrics()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        var id = await store.CreateAsync(NewRequest());
        var changedAt = UpdatedAt.AddMinutes(5);

        var updated = await store.UpdateResultAsync(
            id,
            "failed",
            externalId: "resp-2",
            responseBody: """{"error":"boom"}""",
            setResponseBody: true,
            latencyMs: 88,
            firstTokenLatencyMs: 9,
            reasoningDurationMs: 0,
            updatedAt: changedAt);

        Assert.True(updated);

        var actual = await store.GetByIdAsync(id);
        Assert.NotNull(actual);
        Assert.Equal("failed", actual.Status);
        Assert.Equal("resp-2", actual.ExternalId);
        RecordAssert.Json("""{"error":"boom"}""", actual.ResponseBody);
        Assert.Equal(88L, actual.MetricsLatencyMs);
        Assert.Equal(9L, actual.MetricsFirstTokenLatencyMs);
        Assert.Equal(0L, actual.MetricsReasoningDurationMs);
        Assert.Equal(changedAt, actual.UpdatedAt);

        // Finalization never clears data captured by the dedicated updates.
        RecordAssert.Json("""{"content-type":"application/json"}""", actual.ResponseHeaders);
        RecordAssert.Json("""[{"delta":"hi"}]""", actual.ResponseChunks);
    }

    [Fact]
    public async Task UpdateResultLeavesTheBodyUntouchedWhenNotSet()
    {
        // The storage policy can disable body storage and a failed external
        // write leaves the column alone: the store only writes the body when
        // the caller sets it.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        var id = await store.CreateAsync(NewRequest());
        var changedAt = UpdatedAt.AddMinutes(5);

        var updated = await store.UpdateResultAsync(
            id,
            "completed",
            externalId: "resp-3",
            responseBody: null,
            setResponseBody: false,
            latencyMs: 5,
            firstTokenLatencyMs: null,
            reasoningDurationMs: null,
            updatedAt: changedAt);

        Assert.True(updated);

        var actual = await store.GetByIdAsync(id);
        Assert.NotNull(actual);
        Assert.Equal("resp-3", actual.ExternalId);
        RecordAssert.Json("""{"id":"chatcmpl-1"}""", actual.ResponseBody);
        Assert.Equal(5L, actual.MetricsLatencyMs);
        Assert.Equal(changedAt, actual.UpdatedAt);
    }

    [Fact]
    public async Task UpdateResponseChunksWritesOnlyChunks()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        var id = await store.CreateAsync(NewRequest());
        var changedAt = UpdatedAt.AddMinutes(5);

        var updated = await store.UpdateResponseChunksAsync(
            id,
            """[{"delta":"yo"}]""",
            changedAt);

        Assert.True(updated);

        var actual = await store.GetByIdAsync(id);
        Assert.NotNull(actual);
        RecordAssert.Json("""[{"delta":"yo"}]""", actual.ResponseChunks);
        Assert.Equal("completed", actual.Status);
        RecordAssert.Json("""{"id":"chatcmpl-1"}""", actual.ResponseBody);
        Assert.Equal(changedAt, actual.UpdatedAt);
    }

    [Fact]
    public async Task UpdateResponseHeadersWritesOnlyHeaders()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        var id = await store.CreateAsync(NewRequest());
        var changedAt = UpdatedAt.AddMinutes(5);

        var updated = await store.UpdateResponseHeadersAsync(
            id,
            """{"content-type":["text/event-stream;charset=utf-8"]}""",
            changedAt);

        Assert.True(updated);

        var actual = await store.GetByIdAsync(id);
        Assert.NotNull(actual);
        RecordAssert.Json("""{"content-type":["text/event-stream;charset=utf-8"]}""", actual.ResponseHeaders);
        Assert.Equal("chatcmpl-1", actual.ExternalId);
        RecordAssert.Json("""{"id":"chatcmpl-1"}""", actual.ResponseBody);
        Assert.Equal(changedAt, actual.UpdatedAt);
    }

    [Fact]
    public async Task UpdateStatusOnlyLeavesResponseDataUntouched()
    {
        // Port of UpdateRequestStatus: a failure update must not clear the
        // external id or response body captured at finalization.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        var id = await store.CreateAsync(NewRequest());
        var changedAt = UpdatedAt.AddMinutes(5);

        var updated = await store.UpdateStatusAsync(id, "canceled", changedAt);

        Assert.True(updated);

        var actual = await store.GetByIdAsync(id);
        Assert.NotNull(actual);
        Assert.Equal("canceled", actual.Status);
        Assert.Equal("chatcmpl-1", actual.ExternalId);
        RecordAssert.Json("""{"id":"chatcmpl-1"}""", actual.ResponseBody);
        Assert.Equal(changedAt, actual.UpdatedAt);
    }

    [Fact]
    public async Task GetByExternalIdAndProjectScopesTheLookup()
    {
        // biz/video.go GetTaskByExternalID looks the provider task id up within
        // the API key's project only; the same id may exist in another project.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        var projectSeven = NewRequest();
        projectSeven.ExternalId = "video-task";
        projectSeven.ProjectId = 7;
        await store.CreateAsync(projectSeven);

        var projectNine = NewRequest();
        projectNine.ExternalId = "video-task";
        projectNine.ProjectId = 9;
        await store.CreateAsync(projectNine);

        var seven = await store.GetByExternalIdAndProjectAsync("video-task", 7);
        Assert.NotNull(seven);
        Assert.Equal(7L, seven.ProjectId);

        var nine = await store.GetByExternalIdAndProjectAsync("video-task", 9);
        Assert.NotNull(nine);
        Assert.Equal(9L, nine.ProjectId);

        Assert.Null(await store.GetByExternalIdAndProjectAsync("video-task", 8));
        Assert.Null(await store.GetByExternalIdAndProjectAsync("other-task", 7));
    }

    [Fact]
    public async Task FindLatestCompletedResponsesSessionScopesTheLookup()
    {
        // request.go:1442-1471: the continuation loader reads the newest
        // completed OpenAI Responses row for one API key (and project, when the
        // caller carries one). Failed rows, other formats and other owners
        // never satisfy a previous_response_id lookup.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        async Task<long> Add(
            string marker,
            DateTime createdAt,
            long apiKeyId = 7,
            long projectId = 9,
            string format = "openai/responses",
            string status = "completed",
            string externalId = "resp_shared")
            => await store.CreateAsync(new RequestRecord
            {
                CreatedAt = createdAt,
                UpdatedAt = createdAt,
                ModelId = "gpt-5",
                Format = format,
                Status = status,
                ExternalId = externalId,
                RequestBody = "{\"input\":\"" + marker + "\"}",
                ApiKeyId = apiKeyId,
                ProjectId = projectId,
            });

        await Add("older", CreatedAt);
        await Add("newer", CreatedAt.AddMinutes(1));
        await Add("other-key", CreatedAt.AddMinutes(2), apiKeyId: 8);
        await Add("failed", CreatedAt.AddMinutes(3), status: "failed");
        await Add("compact", CreatedAt.AddMinutes(4), format: "openai/responses_compact");
        var websocket = await Add(
            "websocket",
            CreatedAt.AddMinutes(5),
            format: "openai/responses-ws");

        var responsesOnly = await store.FindLatestCompletedResponsesSessionAsync(
            "resp_shared",
            7,
            ["openai/responses"],
            projectId: 9);
        Assert.NotNull(responsesOnly);
        RecordAssert.Json("""{"input":"newer"}""", responsesOnly.RequestBody);

        // Both Responses formats are accepted; the newest row wins.
        var bothFormats = await store.FindLatestCompletedResponsesSessionAsync(
            "resp_shared",
            7,
            ["openai/responses", "openai/responses-ws"],
            projectId: 9);
        Assert.NotNull(bothFormats);
        Assert.Equal(websocket, bothFormats.Id);

        // Without a project the lookup is keyed by API key alone.
        var unscoped = await store.FindLatestCompletedResponsesSessionAsync(
            "resp_shared",
            7,
            ["openai/responses"],
            projectId: null);
        Assert.NotNull(unscoped);
        RecordAssert.Json("""{"input":"newer"}""", unscoped.RequestBody);

        Assert.Null(await store.FindLatestCompletedResponsesSessionAsync(
            "resp_shared",
            7,
            ["openai/responses"],
            projectId: 10));
        Assert.Null(await store.FindLatestCompletedResponsesSessionAsync(
            "missing",
            7,
            ["openai/responses"],
            projectId: 9));
    }

    [Fact]
    public async Task HasCompletedResponsesOriginIgnoresTheOwner()
    {
        // request_responses_certification.go:12-18: the known check asks whether
        // any completed Responses row carries the provider id. It is not scoped
        // to an API key or project — the check decides whether a continuation
        // may be forwarded upstream without its snapshot, not what history the
        // caller may replay.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        async Task Add(
            string externalId,
            string format = "openai/responses",
            string status = "completed",
            long apiKeyId = 7,
            long projectId = 9)
            => await store.CreateAsync(new RequestRecord
            {
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                ModelId = "gpt-5",
                Format = format,
                Status = status,
                ExternalId = externalId,
                ApiKeyId = apiKeyId,
                ProjectId = projectId,
            });

        await Add("resp_shared", apiKeyId: 7, projectId: 9);
        await Add(
            "resp_websocket",
            format: "openai/responses-ws",
            apiKeyId: 8,
            projectId: 10);
        await Add("resp_failed", status: "failed");
        await Add("resp_compact", format: "openai/responses_compact");
        await Add("resp_chat", format: "openai/chat_completions");

        Assert.True(await store.HasCompletedResponsesOriginAsync("resp_shared"));
        Assert.True(await store.HasCompletedResponsesOriginAsync("resp_websocket"));
        Assert.False(await store.HasCompletedResponsesOriginAsync("resp_failed"));
        Assert.False(await store.HasCompletedResponsesOriginAsync("resp_compact"));
        Assert.False(await store.HasCompletedResponsesOriginAsync("resp_chat"));
        Assert.False(await store.HasCompletedResponsesOriginAsync("resp_missing"));
    }

    [Fact]
    public async Task UpdateStatusExternalIdAndResponseBodyRefreshesThePolledSnapshot()
    {
        // Port of UpdateRequestStatusExternalIDAndResponseBody: status and
        // external id are always written, together with the provider snapshot.
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        var id = await store.CreateAsync(NewRequest());
        var changedAt = UpdatedAt.AddMinutes(5);

        var updated = await store.UpdateStatusExternalIdAndResponseBodyAsync(
            id,
            "completed",
            "video-task",
            """{"id":"video-task","status":"succeeded"}""",
            setResponseBody: true,
            changedAt);

        Assert.True(updated);

        var actual = await store.GetByIdAsync(id);
        Assert.NotNull(actual);
        Assert.Equal("completed", actual.Status);
        Assert.Equal("video-task", actual.ExternalId);
        RecordAssert.Json("""{"id":"video-task","status":"succeeded"}""", actual.ResponseBody);
        Assert.Equal(changedAt, actual.UpdatedAt);
    }

    [Fact]
    public async Task SnapshotUpdateLeavesTheBodyUntouchedWhenNotSet()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        var id = await store.CreateAsync(NewRequest());
        var changedAt = UpdatedAt.AddMinutes(5);

        var updated = await store.UpdateStatusExternalIdAndResponseBodyAsync(
            id,
            "completed",
            "video-task",
            responseBody: null,
            setResponseBody: false,
            changedAt);

        Assert.True(updated);

        var actual = await store.GetByIdAsync(id);
        Assert.NotNull(actual);
        Assert.Equal("completed", actual.Status);
        Assert.Equal("video-task", actual.ExternalId);
        RecordAssert.Json("""{"id":"chatcmpl-1"}""", actual.ResponseBody);
        Assert.Equal(changedAt, actual.UpdatedAt);
    }

    [Fact]
    public async Task UpdateChannelRecordsTheSelectedChannel()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        var id = await store.CreateAsync(NewRequest());
        var changedAt = UpdatedAt.AddMinutes(5);

        var updated = await store.UpdateChannelAsync(id, 42, changedAt);

        Assert.True(updated);

        var actual = await store.GetByIdAsync(id);
        Assert.NotNull(actual);
        Assert.Equal(42L, actual.ChannelId);
        Assert.Equal(changedAt, actual.UpdatedAt);
    }

    [Fact]
    public async Task ListUnsavedVideoRequestsSelectsOnlyEligibleRowsOldestFirst()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        // worker.go:137-146: processing/completed, the two video formats, and
        // content_saved = false, ordered by id.
        var completedVideo = await store.CreateAsync(
            NewVideoRequest("completed", "openai/video"));
        var processingVideo = await store.CreateAsync(
            NewVideoRequest("processing", "seedance/video"));
        await store.CreateAsync(NewVideoRequest("completed", "openai/video", contentSaved: true));
        await store.CreateAsync(NewVideoRequest("failed", "openai/video"));
        await store.CreateAsync(NewVideoRequest("completed", "openai/chat_completions"));

        var rows = await store.ListUnsavedVideoRequestsAsync(50);
        Assert.Equal(new[] { completedVideo, processingVideo }, rows.Select(row => row.Id));

        var limited = await store.ListUnsavedVideoRequestsAsync(1);
        Assert.Equal(new[] { completedVideo }, limited.Select(row => row.Id));
    }

    [Fact]
    public async Task MarkContentSavedWritesTheContentColumnsAndUpdatedAt()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        var id = await store.CreateAsync(NewVideoRequest("completed", "openai/video"));
        var savedAt = UpdatedAt.AddMinutes(5);

        var updated = await store.MarkContentSavedAsync(id, 7, "/1/requests/9/video/clip.mp4", savedAt);

        Assert.True(updated);

        var actual = await store.GetByIdAsync(id);
        Assert.NotNull(actual);
        Assert.True(actual.ContentSaved);
        Assert.Equal(7L, actual.ContentStorageId);
        Assert.Equal("/1/requests/9/video/clip.mp4", actual.ContentStorageKey);
        Assert.Equal(savedAt, actual.ContentSavedAt);
        Assert.Equal(savedAt, actual.UpdatedAt);

        // The next scan no longer selects the row.
        Assert.Empty(await store.ListUnsavedVideoRequestsAsync(50));
    }

    [Fact]
    public async Task UpdatesReportMissingRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        Assert.False(await store.UpdateStatusAsync(12345, "failed", UpdatedAt));
        Assert.False(await store.UpdateResultAsync(
            12345, "failed", null, null, true, null, null, null, UpdatedAt));
        Assert.False(await store.UpdateResponseHeadersAsync(12345, "{}", UpdatedAt));
        Assert.False(await store.UpdateResponseChunksAsync(12345, "[]", UpdatedAt));
        Assert.False(await store.UpdateChannelAsync(12345, 1, UpdatedAt));
        Assert.False(await store.UpdateStatusExternalIdAndResponseBodyAsync(
            12345, "completed", "video-task", "{}", true, UpdatedAt));
        Assert.False(await store.MarkContentSavedAsync(
            12345, 7, "/9/requests/12345/video/video.mp4", UpdatedAt));
        Assert.Null(await store.GetByIdAsync(12345));
    }

    private static RequestRecord NewVideoRequest(
        string status,
        string format,
        bool contentSaved = false) => new()
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            ModelId = "sora-2",
            Format = format,
            Status = status,
            ContentSaved = contentSaved,
            ProjectId = 1,
        };

    private static RequestRecord NewRequest() => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        UserId = 11,
        Source = "playground",
        ModelId = "gpt-4o",
        ReasoningEffort = "high",
        Format = "openai/chat_completions",
        RequestHeaders = """{"authorization":"Bearer x"}""",
        RequestBody = """{"model":"gpt-4o"}""",
        ResponseHeaders = """{"content-type":"application/json"}""",
        ResponseBody = """{"id":"chatcmpl-1"}""",
        ResponseChunks = """[{"delta":"hi"}]""",
        ExternalId = "chatcmpl-1",
        Status = "completed",
        Stream = true,
        ClientIp = "127.0.0.1",
        UserAgent = "xunit",
        MetricsLatencyMs = 120,
        MetricsFirstTokenLatencyMs = 30,
        MetricsReasoningDurationMs = 40,
        ContentSaved = true,
        ContentStorageId = 5,
        ContentStorageKey = "requests/1",
        ContentSavedAt = CreatedAt.AddSeconds(10),
        ApiKeyId = 2,
        ChannelId = 3,
        DataStorageId = 4,
        ProjectId = 9,
        TraceId = 6,
    };
}
