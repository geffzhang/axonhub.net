namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class RequestExecutionStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task CreateAndListByRequestIdRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestExecutionStore(context);

        var first = NewExecution(101);
        first.Status = "failed";
        var firstId = await store.CreateAsync(first);
        first.Id = firstId;

        var second = NewExecution(101);
        second.ModelId = "gpt-4o-mini";
        second.ChannelId = 8;
        var secondId = await store.CreateAsync(second);
        second.Id = secondId;

        var otherRequest = NewExecution(202);
        await store.CreateAsync(otherRequest);

        var executions = await store.ListByRequestIdAsync(101);

        Assert.Equal(new[] { firstId, secondId }, executions.Select(execution => execution.Id).ToArray());
        RecordAssert.Equivalent(first, executions[0]);
        RecordAssert.Equivalent(second, executions[1]);
    }

    [Fact]
    public async Task ListByRequestIdReturnsEmptyForUnknownRequest()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestExecutionStore(context);

        var executions = await store.ListByRequestIdAsync(999);

        Assert.Empty(executions);
    }

    [Fact]
    public async Task UpdateResultWritesOutcomeAndKeepsHeadersAndChunks()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestExecutionStore(context);

        var execution = NewExecution(303);
        var id = await store.CreateAsync(execution);
        var changedAt = UpdatedAt.AddMinutes(1);

        var updated = await store.UpdateResultAsync(
            id,
            "completed",
            externalId: "resp-2",
            upstreamModelId: "gpt-4o-2026-02-01",
            responseBody: null,
            errorMessage: null,
            latencyMs: 55,
            firstTokenLatencyMs: 5,
            reasoningDurationMs: 6,
            updatedAt: changedAt);

        Assert.True(updated);

        var actual = (await store.ListByRequestIdAsync(303)).Single();
        Assert.Equal("completed", actual.Status);
        Assert.Equal("resp-2", actual.ExternalId);
        Assert.Equal("gpt-4o-2026-02-01", actual.UpstreamModelId);

        // UpdateRequestExecutionFinalized never touches response_status_code, so
        // the value captured at creation survives.
        Assert.Equal(200L, actual.ResponseStatusCode);
        Assert.Null(actual.ResponseBody);

        // Finalization passes an empty error message, and the store only writes
        // non-empty values, so a previously captured message survives.
        Assert.Equal("none", actual.ErrorMessage);

        // Headers and chunks go through their own updates, so finalization
        // cannot clear them.
        RecordAssert.Json("""{"content-type":"application/json"}""", actual.ResponseHeaders);
        RecordAssert.Json("""[{"delta":"hi"}]""", actual.ResponseChunks);
        Assert.Equal(55L, actual.MetricsLatencyMs);
        Assert.Equal(changedAt, actual.UpdatedAt);
    }

    [Fact]
    public async Task UpdateResponseHeadersWritesOnlyHeaders()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestExecutionStore(context);

        var id = await store.CreateAsync(NewExecution(606));
        var changedAt = UpdatedAt.AddMinutes(4);

        var updated = await store.UpdateResponseHeadersAsync(
            id,
            """{"content-type":["text/event-stream"]}""",
            changedAt);

        Assert.True(updated);

        var actual = (await store.ListByRequestIdAsync(606)).Single();
        RecordAssert.Json("""{"content-type":["text/event-stream"]}""", actual.ResponseHeaders);
        Assert.Equal("chatcmpl-1", actual.ExternalId);
        RecordAssert.Json("""{"id":"chatcmpl-1"}""", actual.ResponseBody);
        Assert.Equal(changedAt, actual.UpdatedAt);
    }

    [Fact]
    public async Task UpdateStatusWritesFailureDetailsAndKeepsFinalizedMetadata()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestExecutionStore(context);

        var execution = NewExecution(404);
        var id = await store.CreateAsync(execution);
        var changedAt = UpdatedAt.AddMinutes(2);

        var updated = await store.UpdateStatusAsync(
            id,
            "failed",
            responseStatusCode: 503,
            upstreamModelId: "gpt-4o-2026-03-01",
            errorMessage: "upstream unavailable",
            latencyMs: 77,
            firstTokenLatencyMs: null,
            reasoningDurationMs: null,
            updatedAt: changedAt);

        Assert.True(updated);

        var actual = (await store.ListByRequestIdAsync(404)).Single();
        Assert.Equal("failed", actual.Status);
        Assert.Equal(503L, actual.ResponseStatusCode);
        Assert.Equal("gpt-4o-2026-03-01", actual.UpstreamModelId);
        Assert.Equal("upstream unavailable", actual.ErrorMessage);
        Assert.Equal(77L, actual.MetricsLatencyMs);

        // Values not supplied by the caller are left as they were.
        Assert.Equal(30L, actual.MetricsFirstTokenLatencyMs);
        Assert.Equal(changedAt, actual.UpdatedAt);
    }

    [Fact]
    public async Task UpdateStatusCanCancelWithoutClearingResponseData()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestExecutionStore(context);

        var execution = NewExecution(505);
        var id = await store.CreateAsync(execution);

        await store.UpdateStatusAsync(
            id,
            "canceled",
            responseStatusCode: null,
            upstreamModelId: null,
            errorMessage: null,
            latencyMs: null,
            firstTokenLatencyMs: null,
            reasoningDurationMs: null,
            updatedAt: UpdatedAt.AddMinutes(3));

        var actual = (await store.ListByRequestIdAsync(505)).Single();
        Assert.Equal("canceled", actual.Status);
        Assert.Equal("chatcmpl-1", actual.ExternalId);
        Assert.Equal(200L, actual.ResponseStatusCode);
        RecordAssert.Json("""{"id":"chatcmpl-1"}""", actual.ResponseBody);
        Assert.Equal("none", actual.ErrorMessage);
    }

    [Fact]
    public async Task UpdateReportsMissingRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new RequestExecutionStore(context);

        Assert.False(await store.UpdateStatusAsync(
            12345, "failed", null, null, null, null, null, null, UpdatedAt));
        Assert.False(await store.UpdateResultAsync(
            12345, "failed", null, null, null, null, null, null, null, UpdatedAt));
        Assert.False(await store.UpdateResponseHeadersAsync(12345, "{}", UpdatedAt));
    }

    private static RequestExecutionRecord NewExecution(long requestId) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        ProjectId = 9,
        ChannelApiKeyIndex = 1,
        ExternalId = "chatcmpl-1",
        ModelId = "gpt-4o",
        UpstreamModelId = "gpt-4o-2026-01-01",
        Format = "openai/chat_completions",
        ReasoningEffort = "high",
        ChannelApiKeySuffix = "abcd",
        RequestBody = """{"model":"gpt-4o"}""",
        ResponseHeaders = """{"content-type":"application/json"}""",
        ResponseBody = """{"id":"chatcmpl-1"}""",
        ResponseChunks = """[{"delta":"hi"}]""",
        ErrorMessage = "none",
        ResponseStatusCode = 200,
        Status = "completed",
        Stream = true,
        MetricsLatencyMs = 120,
        MetricsFirstTokenLatencyMs = 30,
        MetricsReasoningDurationMs = 40,
        RequestHeaders = """{"authorization":"Bearer x"}""",
        RequestUrl = "https://api.example.com/v1/chat/completions",
        PassThroughApplied = true,
        ChannelId = 3,
        DataStorageId = 4,
        RequestId = requestId,
    };
}
