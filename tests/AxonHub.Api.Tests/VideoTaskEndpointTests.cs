using System.Text;
using System.Text.Json;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Endpoint glue tests for the video task query/delete routes
/// (<c>GET/DELETE /v1/videos/:id</c>, routes.go:217-219; api.OpenAIHandlers
/// GetVideo/DeleteVideo in internal/server/api/openai.go:519-578). The task
/// flow itself is covered by <see cref="VideoServiceTests"/>.
/// </summary>
public sealed class VideoTaskEndpointTests
{
    private static (VideoService Service, FakeRequestStore Requests, ScriptedExecutor Executor) NewService()
    {
        var requests = new FakeRequestStore();
        var executions = new FakeRequestExecutionStore();
        var channels = new FakeChannelStore();
        var executor = new ScriptedExecutor();

        channels.Add(new ChannelRecord
        {
            Id = 1001,
            Name = "channel-1001",
            Type = "openai",
            BaseUrl = "http://upstream.test",
            Status = "enabled",
            Credentials = "{\"apiKey\":\"sk-test\"}",
            SupportedModels = "[\"sora-2\"]",
        });

        requests.Add(new RequestRecord
        {
            Id = 1,
            ProjectId = 1,
            ExternalId = "task-1",
            ChannelId = 1001,
            Status = "processing",
            ModelId = "sora-2",
            Format = ApiFormats.OpenAIVideo,
        });

        return (new VideoService(requests, executions, channels, executor, TimeProvider.System),
            requests,
            executor);
    }

    private static DefaultHttpContext NewContext(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.test");
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ResponseBody(HttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static HttpResponseData VideoTaskJson()
        => new()
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(
                "{\"id\":\"task-1\",\"object\":\"video\",\"status\":\"completed\","
                + "\"model\":\"sora-2\",\"prompt\":\"a cat walking\",\"seconds\":\"4\","
                + "\"video_url\":\"https://upstream.test/video.mp4\",\"progress\":100,"
                + "\"created_at\":1700000000}"),
        };

    [Fact]
    public async Task GetVideoReturnsRenderedTask()
    {
        var (service, _, executor) = NewService();
        executor.OnDo = (_, _, _) => VideoTaskJson();
        var context = NewContext("GET", "/v1/videos/task-1");

        await VideoEndpoint.GetVideoAsync(context, "task-1", service);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using (var body = JsonDocument.Parse(ResponseBody(context)))
        {
            Assert.Equal("task-1", body.RootElement.GetProperty("id").GetString());
            Assert.Equal("video", body.RootElement.GetProperty("object").GetString());
            Assert.Equal("completed", body.RootElement.GetProperty("status").GetString());
            Assert.Equal("sora-2", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("a cat walking", body.RootElement.GetProperty("prompt").GetString());
            Assert.Equal("4", body.RootElement.GetProperty("seconds").GetString());
            Assert.Equal(
                "https://upstream.test/video.mp4",
                body.RootElement.GetProperty("video_url").GetString());
            Assert.Equal(100, body.RootElement.GetProperty("progress").GetInt32());
            Assert.Equal(1700000000L, body.RootElement.GetProperty("created_at").GetInt64());
        }

        Assert.Equal("GET", Assert.Single(executor.Requests).Method);
    }

    [Fact]
    public async Task GetVideoInvalidIdReturnsBadRequest()
    {
        var (service, _, _) = NewService();
        var context = NewContext("GET", "/v1/videos/");

        await VideoEndpoint.GetVideoAsync(context, null, service);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"type\":\"Bad Request\",\"message\":\"invalid id\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task GetVideoUnknownTaskReturnsInternalServerError()
    {
        var (service, _, _) = NewService();
        var context = NewContext("GET", "/v1/videos/unknown");

        await VideoEndpoint.GetVideoAsync(context, "unknown", service);

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"type\":\"Internal Server Error\",\"message\":\"ent: request not found\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task DeleteVideoSendsProviderDeleteAndReturnsNoContent()
    {
        var (service, requests, executor) = NewService();
        executor.OnDo = (_, _, _) => new HttpResponseData
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes("{\"id\":\"task-1\",\"deleted\":true}"),
        };
        var context = NewContext("DELETE", "/v1/videos/task-1");

        await VideoEndpoint.DeleteVideoAsync(context, "task-1", service);

        Assert.Equal(204, context.Response.StatusCode);
        Assert.Equal(string.Empty, ResponseBody(context));

        var upstream = Assert.Single(executor.Requests);
        Assert.Equal("DELETE", upstream.Method);
        Assert.Equal("http://upstream.test/v1/videos/task-1", upstream.Url);
        Assert.Equal("canceled", requests.Rows[0].Status);
    }

    [Fact]
    public async Task DeleteVideoInvalidIdReturnsBadRequest()
    {
        var (service, _, _) = NewService();
        var context = NewContext("DELETE", "/v1/videos/");

        await VideoEndpoint.DeleteVideoAsync(context, null, service);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"type\":\"Bad Request\",\"message\":\"invalid id\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task DeleteVideoUnknownTaskReturnsInternalServerError()
    {
        var (service, _, _) = NewService();
        var context = NewContext("DELETE", "/v1/videos/unknown");

        await VideoEndpoint.DeleteVideoAsync(context, "unknown", service);

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"type\":\"Internal Server Error\",\"message\":\"ent: request not found\"}}",
            ResponseBody(context));
    }
}
