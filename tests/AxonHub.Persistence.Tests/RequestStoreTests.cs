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
    public async Task UpdatesReportMissingRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestStore(context);

        Assert.False(await store.UpdateStatusAsync(12345, "failed", UpdatedAt));
        Assert.False(await store.UpdateResultAsync(
            12345, "failed", null, null, null, null, null, UpdatedAt));
        Assert.False(await store.UpdateResponseHeadersAsync(12345, "{}", UpdatedAt));
        Assert.False(await store.UpdateChannelAsync(12345, 1, UpdatedAt));
        Assert.False(await store.UpdateStatusExternalIdAndResponseBodyAsync(
            12345, "completed", "video-task", "{}", UpdatedAt));
        Assert.Null(await store.GetByIdAsync(12345));
    }

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
