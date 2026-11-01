using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.Doubao;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Zenmux;
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
    public async Task LoadTaskResolvesDoubaoSeedanceOutboundWithoutPersistedFormat()
    {
        // biz/video.go falls back to seedance/video for doubao channels and
        // looks that key up in the doubao channel's outbound map
        // (channel_endpoint.go:271-275), which carries the Doubao transformer.
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel(type: "doubao"));
        harness.Requests.Add(TaskRow());

        var (_, outbound) = await harness.Service.LoadTaskAsync(1);

        var doubao = Assert.IsType<DoubaoOutboundTransformer>(outbound);
        var request = doubao.BuildGetVideoTaskRequest("task-1");
        Assert.Equal("GET", request.Method);
        Assert.Equal("http://upstream.test/v3/contents/generations/tasks/task-1", request.Url);
        Assert.Equal(ApiFormats.SeedanceVideo, request.ApiFormat);
        Assert.Equal(RequestTypes.Video, request.RequestType);
    }

    [Fact]
    public async Task LoadTaskUsesLatestZenmuxExecutionFormatAcrossRetries()
    {
        // video_test.go:153-164: the latest associated execution format wins
        // while the public request format stays openai/video.
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel(type: "zenmux"));
        harness.Requests.Add(TaskRow());
        harness.Executions.Add(requestId: 1, format: ApiFormats.OpenAIVideo, externalId: "openai-task");
        harness.Executions.Add(requestId: 1, format: ApiFormats.ZenmuxVideo, externalId: "native-task");

        var (task, outbound) = await harness.Service.LoadTaskAsync(1);

        Assert.Equal(ApiFormats.OpenAIVideo, task.Format);
        var zenmux = Assert.IsType<ZenmuxOutboundTransformer>(outbound);
        var request = zenmux.BuildGetVideoTaskRequest(task.ExternalId!);
        Assert.Equal("http://upstream.test/v1/videos/task-1", request.Url);
        Assert.Equal(ApiFormats.ZenmuxVideo, request.ApiFormat);
    }

    [Fact]
    public async Task LoadTaskSkipsNewerZenmuxExecutionWithoutProviderTaskId()
    {
        // video_test.go:166-176: the newest execution has no external id, so
        // the previous native task association remains authoritative.
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel(type: "zenmux"));
        harness.Requests.Add(TaskRow());
        harness.Executions.Add(requestId: 1, format: ApiFormats.ZenmuxVideo, externalId: "native-task");
        harness.Executions.Add(requestId: 1, format: ApiFormats.OpenAIVideo, externalId: string.Empty);

        var (_, outbound) = await harness.Service.LoadTaskAsync(1);

        Assert.IsType<ZenmuxOutboundTransformer>(outbound);
    }

    [Fact]
    public async Task LoadTaskUsesPersistedOpenAiVideoFormatOnZenmuxChannel()
    {
        // video_test.go:178-188: a persisted openai/video execution selects the
        // shared OpenAI outbound even on a ZenMux channel.
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel(type: "zenmux"));
        harness.Requests.Add(TaskRow());
        harness.Executions.Add(requestId: 1, format: ApiFormats.ZenmuxVideo, externalId: "old-native-task");
        harness.Executions.Add(requestId: 1, format: ApiFormats.OpenAIVideo, externalId: "openai-task");

        var (_, outbound) = await harness.Service.LoadTaskAsync(1);

        var openai = Assert.IsType<OpenAiOutboundTransformer>(outbound);
        var request = openai.BuildGetVideoTaskRequest("openai-task");
        Assert.Equal("http://upstream.test/v1/videos/openai-task", request.Url);
    }

    [Fact]
    public async Task LoadTaskRejectsAssociatedNonVideoExecutionFormatOnZenmuxChannel()
    {
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel(type: "zenmux"));
        harness.Requests.Add(TaskRow());
        harness.Executions.Add(requestId: 1, format: ApiFormats.OpenAIChatCompletion, externalId: "native-task");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.LoadTaskAsync(1));

        Assert.Equal(
            "server internal error, please try again later: persisted execution format "
            + "\"openai/chat_completions\" does not support video task operations",
            error.Message);
    }

    [Fact]
    public async Task LoadTaskFallsBackToOpenAiVideoOnZenmuxChannelWithoutPersistedFormat()
    {
        // video_test.go:201-224: ZenMux's legacy fallback is openai/video.
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel(type: "zenmux"));
        harness.Requests.Add(TaskRow());
        harness.Executions.Add(requestId: 1, format: string.Empty, externalId: "legacy-task");

        var (_, outbound) = await harness.Service.LoadTaskAsync(1);

        Assert.IsType<OpenAiOutboundTransformer>(outbound);
    }

    [Fact]
    public async Task DeleteTaskCancelsZenmuxTaskLocallyWithoutUpstreamDelete()
    {
        // video_test.go:226-246: ZenMux has no native delete, so the task is
        // cancelled locally and no upstream request is issued.
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel(type: "zenmux"));
        harness.Requests.Add(TaskRow());
        harness.Executions.Add(requestId: 1, format: ApiFormats.ZenmuxVideo, externalId: "task-1");

        await harness.Service.DeleteTaskAsync(1);

        Assert.Empty(harness.Executor.Requests);

        var update = Assert.Single(harness.Requests.StatusUpdates);
        Assert.Equal(1, update.Id);
        Assert.Equal("canceled", update.Status);
        Assert.Equal("canceled", harness.Requests.Rows[0].Status);
    }

    [Fact]
    public async Task DeleteTaskReportsZenmuxLocalCancelFailure()
    {
        // biz/video.go:126-131: unlike the best-effort cancel after an upstream
        // delete, the local ZenMux cancel is fatal.
        var harness = NewHarness();
        harness.Channels.Add(OpenAiChannel(type: "zenmux"));
        harness.Requests.Add(TaskRow());
        harness.Requests.ThrowOnStatusUpdate = true;
        harness.Executions.Add(requestId: 1, format: ApiFormats.ZenmuxVideo, externalId: "task-1");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.Service.DeleteTaskAsync(1));

        Assert.Equal(
            "failed to cancel video task locally: status update failed",
            error.Message);
        Assert.Empty(harness.Executor.Requests);
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
