using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Persistence;

namespace AxonHub.Api.Tests;

/// <summary>
/// Port of biz/video_test.go: the video task query/delete flow
/// (internal/server/biz/video.go). The reference fixture runs the service over
/// a real ent client; the port's service reads the plain store seams, so the
/// same scenarios run against in-memory stores and a scripted executor. The
/// store SQL itself is locked by AxonHub.Persistence.Tests.
/// </summary>
public sealed class VideoServiceTests
{
    private static readonly RequestIdentity ProjectOne = new(1, 10);

    private static readonly RequestIdentity ProjectTwo = new(2, 20);

    private sealed record Harness(
        VideoService Service,
        FakeRequestStore Requests,
        FakeRequestExecutionStore Executions,
        FakeChannelStore Channels,
        ScriptedExecutor Executor);

    private static Harness NewHarness()
    {
        var requests = new FakeRequestStore();
        var executions = new FakeRequestExecutionStore();
        var channels = new FakeChannelStore();
        var executor = new ScriptedExecutor();

        return new Harness(
            new VideoService(requests, executions, channels, executor, TimeProvider.System),
            requests,
            executions,
            channels,
            executor);
    }

    private static RequestRecord TaskRow(
        long id = 1,
        long projectId = 1,
        string? externalId = "task-1",
        long? channelId = 1001)
        => new()
        {
            Id = id,
            ProjectId = projectId,
            ExternalId = externalId,
            ChannelId = channelId,
            Status = "processing",
            ModelId = "sora-2",
            Format = ApiFormats.OpenAIVideo,
        };

    private static ChannelRecord OpenAiChannel(long id = 1001, string type = "openai")
        => new()
        {
            Id = id,
            Name = "channel-" + id,
            Type = type,
            BaseUrl = "http://upstream.test",
            Status = "enabled",
            Credentials = "{\"apiKey\":\"sk-test\"}",
            SupportedModels = "[\"sora-2\"]",
        };

    private static HttpResponseData VideoTaskJson(
        string id = "task-1",
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
                "{\"id\":\"" + id + "\",\"object\":\"video\",\"status\":\"" + status + "\","
                + "\"model\":\"sora-2\",\"prompt\":\"a cat walking\",\"seconds\":\"4\","
                + "\"video_url\":\"" + videoUrl + "\",\"progress\":100,\"created_at\":1700000000}"),
        };

    [Fact]
    public async Task ExternalIdLookupRespectsProjectScope()
    {
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel());
        harness.Executor.OnDo = (_, _, _) => VideoTaskJson();
        harness.Requests.Add(TaskRow(id: 1, projectId: 1, externalId: "project-one-task"));
        harness.Requests.Add(TaskRow(id: 2, projectId: 2, externalId: "project-two-task"));

        var first = await harness.Service.GetTaskByExternalIdAsync("project-one-task", ProjectOne);
        Assert.Equal("task-1", first.Video!.ID);

        var second = await harness.Service.GetTaskByExternalIdAsync("project-two-task", ProjectTwo);
        Assert.Equal("task-1", second.Video!.ID);

        var getError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.GetTaskByExternalIdAsync("project-two-task", ProjectOne));
        Assert.Equal("ent: request not found", getError.Message);

        var deleteError = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.DeleteTaskByExternalIdAsync("project-two-task", ProjectOne));
        Assert.Equal("ent: request not found", deleteError.Message);

        // Only the two in-scope lookups reached the provider.
        Assert.Equal(2, harness.Executor.Requests.Count);
    }

    [Fact]
    public async Task GetTaskFetchesProviderTaskAndPersistsSnapshot()
    {
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel());
        harness.Requests.Add(TaskRow(externalId: "task-1"));
        harness.Executor.OnDo = (_, _, _) => VideoTaskJson();

        var response = await harness.Service.GetTaskByExternalIdAsync("task-1", ProjectOne);

        // ParseGetVideoTaskResponse normalizes "completed" to the unified "succeeded".
        Assert.Equal(RequestTypes.Video, response.RequestType);
        var video = response.Video!;
        Assert.Equal("task-1", video.ID);
        Assert.Equal("succeeded", video.Status);
        Assert.Equal("https://upstream.test/video.mp4", video.VideoURL);

        var upstream = Assert.Single(harness.Executor.Requests);
        Assert.Equal("GET", upstream.Method);
        Assert.Equal("http://upstream.test/v1/videos/task-1", upstream.Url);
        Assert.Equal(ApiFormats.OpenAIVideo, upstream.ApiFormat);
        Assert.Equal(RequestTypes.Video, upstream.RequestType);
        var auth = upstream.Auth!;
        Assert.Equal(AuthTypes.Bearer, auth.Type);
        Assert.Equal("sk-test", auth.ApiKey);

        // The polled snapshot lands on the request row: status mapped to the
        // request status vocabulary, external id and body refreshed.
        var snapshot = Assert.Single(harness.Requests.Snapshots);
        Assert.Equal(1, snapshot.Id);
        Assert.Equal("completed", snapshot.Status);
        Assert.Equal("task-1", snapshot.ExternalId);
        Assert.NotEqual(default, snapshot.UpdatedAt);

        using (var body = JsonDocument.Parse(snapshot.ResponseBody))
        {
            Assert.Equal("task-1", body.RootElement.GetProperty("id").GetString());
            Assert.Equal("succeeded", body.RootElement.GetProperty("status").GetString());
            Assert.Equal(
                "https://upstream.test/video.mp4",
                body.RootElement.GetProperty("video_url").GetString());
        }

        Assert.Equal("completed", harness.Requests.Rows[0].Status);
    }

    [Fact]
    public async Task LoadTaskUsesLatestAssociatedExecutionFormatAcrossRetries()
    {
        // The newest execution with both a format and a provider task id wins;
        // an older chat-completion attempt must not be chosen even though it
        // carries an external id.
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel());
        harness.Requests.Add(TaskRow());
        harness.Executions.Add(requestId: 1, format: ApiFormats.OpenAIChatCompletion, externalId: "old-task");
        harness.Executions.Add(requestId: 1, format: ApiFormats.OpenAIVideo, externalId: "task-1");

        var (task, outbound) = await harness.Service.LoadTaskAsync(1);

        Assert.Equal(ApiFormats.OpenAIVideo, task.Format);
        Assert.Equal("task-1", task.ExternalId);
        Assert.IsType<OpenAiOutboundTransformer>(outbound);
    }

    [Fact]
    public async Task LoadTaskSkipsNewerExecutionWithoutProviderTaskAssociation()
    {
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel());
        harness.Requests.Add(TaskRow());
        harness.Executions.Add(requestId: 1, format: ApiFormats.OpenAIVideo, externalId: "task-1");
        harness.Executions.Add(requestId: 1, format: ApiFormats.SeedanceVideo, externalId: null);

        var (_, outbound) = await harness.Service.LoadTaskAsync(1);

        Assert.IsType<OpenAiOutboundTransformer>(outbound);
    }

    [Fact]
    public async Task LoadTaskRejectsAssociatedNonVideoExecutionFormat()
    {
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel());
        harness.Requests.Add(TaskRow());
        harness.Executions.Add(requestId: 1, format: ApiFormats.OpenAIChatCompletion, externalId: "task-1");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.LoadTaskAsync(1));

        Assert.Equal(
            "server internal error, please try again later: persisted execution format "
            + "\"openai/chat_completions\" does not support video task operations",
            error.Message);
    }

    [Fact]
    public async Task LoadTaskFallsBackToChannelTypeDefaultWithoutPersistedFormat()
    {
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel());
        harness.Requests.Add(TaskRow());
        harness.Executions.Add(requestId: 1, format: string.Empty, externalId: "task-1");

        var (_, outbound) = await harness.Service.LoadTaskAsync(1);

        Assert.IsType<OpenAiOutboundTransformer>(outbound);
    }

    [Fact]
    public async Task LoadTaskRejectsDoubaoFallbackInThisBuild()
    {
        // The reference falls back to seedance/video and resolves the doubao
        // outbound; the doubao video transformer is the deferred scope cut
        // (C010), so the port reports the unsupported-channel error instead.
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel(type: "doubao"));
        harness.Requests.Add(TaskRow());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.LoadTaskAsync(1));

        Assert.Equal(
            "server internal error, please try again later: channel does not support video task operations",
            error.Message);
    }

    [Fact]
    public async Task DeleteTaskSendsProviderDeleteAndCancelsLocally()
    {
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel());
        harness.Requests.Add(TaskRow());
        harness.Executor.OnDo = (_, _, _) => new HttpResponseData
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes("{\"id\":\"task-1\",\"deleted\":true}"),
        };

        await harness.Service.DeleteTaskByExternalIdAsync("task-1", ProjectOne);

        var upstream = Assert.Single(harness.Executor.Requests);
        Assert.Equal("DELETE", upstream.Method);
        Assert.Equal("http://upstream.test/v1/videos/task-1", upstream.Url);

        var update = Assert.Single(harness.Requests.StatusUpdates);
        Assert.Equal(1, update.Id);
        Assert.Equal("canceled", update.Status);
        Assert.Equal("canceled", harness.Requests.Rows[0].Status);
    }

    [Fact]
    public async Task GetTaskReportsMissingProviderTaskId()
    {
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel());
        harness.Requests.Add(TaskRow(externalId: null));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.GetTaskAsync(1));

        Assert.Equal("server internal error, please try again later: missing external_id for task", error.Message);
    }

    [Fact]
    public async Task GetTaskReportsMissingChannelId()
    {
        var harness = NewHarness();
        harness.Requests.Add(TaskRow(channelId: null));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.GetTaskAsync(1));

        Assert.Equal("server internal error, please try again later: missing channel_id for task", error.Message);
    }

    [Fact]
    public async Task GetTaskReportsUnknownChannel()
    {
        var harness = NewHarness();
        harness.Requests.Add(TaskRow(channelId: 4242));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.GetTaskAsync(1));

        Assert.Equal("channel not found: ent: channel not found", error.Message);
    }

    [Fact]
    public async Task GetTaskReportsUnknownRequest()
    {
        var harness = NewHarness();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.GetTaskAsync(42));

        Assert.Equal("ent: request not found", error.Message);
    }

    [Fact]
    public async Task GetTaskWrapsExecutionQueryFailure()
    {
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel());
        harness.Requests.Add(TaskRow());
        harness.Executions.ListError = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.GetTaskAsync(1));

        Assert.Equal("failed to query request executions: boom", error.Message);
    }

    [Fact]
    public async Task GetTaskSnapshotWriteFailureIsNonFatal()
    {
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel());
        harness.Requests.Add(TaskRow());
        harness.Requests.ThrowOnSnapshot = true;
        harness.Executor.OnDo = (_, _, _) => VideoTaskJson();

        var response = await harness.Service.GetTaskAsync(1);

        Assert.Equal("task-1", response.Video!.ID);
    }

    [Theory]
    [InlineData("succeeded", "completed")]
    [InlineData("Succeeded", "completed")]
    [InlineData(" succeeded ", "completed")]
    [InlineData("failed", "failed")]
    [InlineData("FAILED", "failed")]
    [InlineData("queued", "processing")]
    [InlineData("running", "processing")]
    [InlineData("in_progress", "processing")]
    [InlineData("", "processing")]
    public void MapVideoStatusToRequestStatusMatchesTheReference(
        string status,
        string expected)
        => Assert.Equal(expected, VideoService.MapVideoStatusToRequestStatus(status));
}
