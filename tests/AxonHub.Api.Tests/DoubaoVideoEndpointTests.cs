using System.Text;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Endpoint glue tests for the Seedance task surface mounted under /doubao/v3
/// (internal/server/api/doubao.go:42-160, routes.go:253-258). The create path
/// reuses the shared chat handler (covered by <see cref="VideoServiceTests"/>
/// and the transformer tests); these lock the GET/DELETE rendering.
/// </summary>
public sealed class DoubaoVideoEndpointTests
{
    private static VideoService NewService(ScriptedExecutor? executor = null)
    {
        var requests = new FakeRequestStore();
        var executions = new FakeRequestExecutionStore();
        var channels = new FakeChannelStore();

        channels.Add(new ChannelRecord
        {
            Id = 1001,
            Name = "channel-1001",
            Type = "doubao",
            BaseUrl = "http://upstream.test",
            Status = "enabled",
            Credentials = "{\"apiKey\":\"sk-test\"}",
            SupportedModels = "[\"doubao-seedance-1-0-pro\"]",
        });

        requests.Add(new RequestRecord
        {
            Id = 1,
            ProjectId = 1,
            ExternalId = "task-1",
            ChannelId = 1001,
            Status = "processing",
            ModelId = "doubao-seedance-1-0-pro",
            Format = ApiFormats.SeedanceVideo,
        });

        return new VideoService(
            requests,
            executions,
            channels,
            executor ?? new ScriptedExecutor(),
            TimeProvider.System);
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

    private static HttpResponseData SeedanceTaskJson()
        => new()
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(
                "{\"id\":\"task-1\",\"model\":\"doubao-seedance-1-0-pro\",\"status\":\"succeeded\","
                + "\"content\":{\"video_url\":\"https://upstream.test/video.mp4\"},"
                + "\"created_at\":1700000000,\"updated_at\":1700000060,"
                + "\"seed\":7,\"resolution\":\"720p\",\"ratio\":\"16:9\",\"duration\":5,"
                + "\"framespersecond\":24}"),
        };

    [Fact]
    public async Task GetTaskRendersTheSeedanceTask()
    {
        var executor = new ScriptedExecutor { OnDo = (_, _, _) => SeedanceTaskJson() };
        var service = NewService(executor);
        var context = NewContext("GET", "/doubao/v3/contents/generations/tasks/task-1");

        await DoubaoVideoEndpoint.GetTaskAsync(context, "task-1", service);

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        // gin's c.Data forwards only the Content-Type; the transformer's
        // Cache-Control header is not surfaced on this route.
        Assert.Equal(string.Empty, context.Response.Headers["Cache-Control"].ToString());

        // Go struct declaration order of seedanceGetResponseInbound.
        Assert.Equal(
            """{"id":"task-1","model":"doubao-seedance-1-0-pro","status":"succeeded","content":{"video_url":"https://upstream.test/video.mp4"},"created_at":1700000000,"updated_at":1700000060,"seed":7,"resolution":"720p","ratio":"16:9","duration":5,"framespersecond":24}""",
            ResponseBody(context));

        Assert.Equal("GET", Assert.Single(executor.Requests).Method);
    }

    [Fact]
    public async Task GetTaskInvalidIdReturnsBadRequest()
    {
        var service = NewService();
        var context = NewContext("GET", "/doubao/v3/contents/generations/tasks/");

        await DoubaoVideoEndpoint.GetTaskAsync(context, null, service);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"type\":\"Bad Request\",\"message\":\"invalid id\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task GetTaskUnknownTaskReturnsInternalServerError()
    {
        var service = NewService();
        var context = NewContext("GET", "/doubao/v3/contents/generations/tasks/unknown");

        await DoubaoVideoEndpoint.GetTaskAsync(context, "unknown", service);

        Assert.Equal(500, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"type\":\"Internal Server Error\",\"message\":\"ent: request not found\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task DeleteTaskSendsTheSeedanceDeleteAndReturnsNoContent()
    {
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => new HttpResponseData
            {
                StatusCode = 200,
                Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
                {
                    ["Content-Type"] = ["application/json"],
                },
                Body = Encoding.UTF8.GetBytes("""{"id":"task-1","deleted":true}"""),
            },
        };
        var service = NewService(executor);
        var context = NewContext("DELETE", "/doubao/v3/contents/generations/tasks/task-1");

        await DoubaoVideoEndpoint.DeleteTaskAsync(context, "task-1", service);

        Assert.Equal(204, context.Response.StatusCode);
        Assert.Equal(string.Empty, ResponseBody(context));

        var upstream = Assert.Single(executor.Requests);
        Assert.Equal("DELETE", upstream.Method);
        Assert.Equal("http://upstream.test/v3/contents/generations/tasks/task-1", upstream.Url);
        Assert.Equal(ApiFormats.SeedanceVideo, upstream.ApiFormat);
    }

    [Fact]
    public async Task DeleteTaskInvalidIdReturnsBadRequest()
    {
        var service = NewService();
        var context = NewContext("DELETE", "/doubao/v3/contents/generations/tasks/");

        await DoubaoVideoEndpoint.DeleteTaskAsync(context, null, service);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(
            "{\"error\":{\"type\":\"Bad Request\",\"message\":\"invalid id\"}}",
            ResponseBody(context));
    }
}
