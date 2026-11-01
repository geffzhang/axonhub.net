using System.Text;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Http;
using AxonHub.Api.Models;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Wire tests for the models surface (internal/server/api/openai.go:792-917 and
/// anthropic.go:85-138): exact bodies, field order and error envelopes, since
/// clients (and the SDKs) read these bytes.
/// </summary>
public sealed class ModelsEndpointTests
{
    private static readonly DateTime CreatedAt =
        new(2024, 4, 5, 19, 34, 58, DateTimeKind.Utc);

    private static ChannelDescriptor OpenAiChannel(
        long id = 1,
        string model = "gpt-4o",
        string? supportedModels = null)
        => TestChannels.OpenAi(id, model) with
        {
            CreatedAt = CreatedAt,
            SupportedModels = supportedModels ?? "[\"" + model + "\"]",
        };

    private static ModelService NewService(params ChannelDescriptor[] channels)
    {
        var provider = new FakeEnabledChannelProvider();
        provider.Set(channels);
        return new ModelService(provider, new StaticModelSettingsProvider(ModelSettings.Default));
    }

    private static DefaultHttpContext NewGetContext(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.test");
        context.Request.Path = new PathString(path);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static string ResponseBody(DefaultHttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static void AssertJsonResponse(DefaultHttpContext context, int statusCode)
    {
        Assert.Equal(statusCode, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
    }

    [Fact]
    public async Task ListReturnsTheFacadeShape()
    {
        var context = NewGetContext("/v1/models");

        await ModelsEndpoint.ListOpenAiModelsAsync(context, NewService(OpenAiChannel()));

        AssertJsonResponse(context, 200);
        Assert.Equal(
            """{"data":[{"id":"gpt-4o","object":"model","created":1712345698,"owned_by":"openai"}],"object":"list"}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task ListEmptyIsAnEmptyDataArray()
    {
        var context = NewGetContext("/v1/models");

        await ModelsEndpoint.ListOpenAiModelsAsync(context, NewService());

        AssertJsonResponse(context, 200);
        Assert.Equal("""{"data":[],"object":"list"}""", ResponseBody(context));
    }

    [Fact]
    public async Task ListHTMLEscapesLikeGo()
    {
        var context = NewGetContext("/v1/models");

        await ModelsEndpoint.ListOpenAiModelsAsync(
            context,
            NewService(OpenAiChannel(supportedModels: "[\"a<b>&c\"]")));

        var body = ResponseBody(context);

        using (var document = JsonDocument.Parse(body))
        {
            Assert.Equal(
                "a<b>&c",
                document.RootElement.GetProperty("data")[0].GetProperty("id").GetString());
        }

        // Go's encoding/json escapes <, > and & even inside strings, so the raw
        // characters never appear on the wire.
        Assert.DoesNotContain("<", body);
        Assert.DoesNotContain(">", body);
        Assert.DoesNotContain("&", body);
        Assert.Contains("u003c", body);
        Assert.Contains("u003e", body);
        Assert.Contains("u0026", body);
    }

    [Fact]
    public async Task ListTrailingSlashIsTheEmptyModelNotFound()
    {
        // gin routes "/v1/models/" to the catch-all, whose id trims to "".
        var context = NewGetContext("/v1/models/");

        await ModelsEndpoint.ListOpenAiModelsAsync(context, NewService(OpenAiChannel()));

        AssertJsonResponse(context, 404);
        Assert.Equal(
            """{"error":{"code":"model_not_found","message":"The model does not exist or you do not have access to it.","type":"invalid_request_error","param":"model"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task RetrieveReturnsTheModel()
    {
        var context = NewGetContext("/v1/models/gpt-4o");

        await ModelsEndpoint.RetrieveOpenAiModelAsync(context, "gpt-4o", NewService(OpenAiChannel()));

        AssertJsonResponse(context, 200);
        Assert.Equal(
            """{"id":"gpt-4o","object":"model","created":1712345698,"owned_by":"openai"}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task RetrieveSupportsSlashSeparatedModelIds()
    {
        // gin: "/v1/models/deepseek/chat" -> param "/deepseek/chat" -> trimmed.
        var context = NewGetContext("/v1/models/deepseek/chat");

        await ModelsEndpoint.RetrieveOpenAiModelAsync(
            context,
            "deepseek/chat",
            NewService(OpenAiChannel(model: "deepseek/chat")));

        AssertJsonResponse(context, 200);
        Assert.Equal(
            """{"id":"deepseek/chat","object":"model","created":1712345698,"owned_by":"openai"}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task RetrieveUnknownModelIsModelNotFound()
    {
        var context = NewGetContext("/v1/models/claude-3");

        await ModelsEndpoint.RetrieveOpenAiModelAsync(context, "claude-3", NewService(OpenAiChannel()));

        AssertJsonResponse(context, 404);
        Assert.Equal(
            """{"error":{"code":"model_not_found","message":"The model `claude-3` does not exist or you do not have access to it.","type":"invalid_request_error","param":"model"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task RetrieveEmptyModelIdHasNoBacktickedName()
    {
        var context = NewGetContext("/v1/models/");

        await ModelsEndpoint.RetrieveOpenAiModelAsync(context, string.Empty, NewService(OpenAiChannel()));

        AssertJsonResponse(context, 404);
        Assert.Equal(
            """{"error":{"code":"model_not_found","message":"The model does not exist or you do not have access to it.","type":"invalid_request_error","param":"model"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task RetrieveEchoesTheRequestId()
    {
        // middleware.WithLoggingTracing publishes the id on the request context;
        // the OpenAI error detail carries it as request_id.
        var context = NewGetContext("/v1/models/claude-3");
        context.Items[RequestIds.HttpContextItemKey] = "ar-1234";

        await ModelsEndpoint.RetrieveOpenAiModelAsync(context, "claude-3", NewService(OpenAiChannel()));

        Assert.Equal(
            """{"error":{"code":"model_not_found","message":"The model `claude-3` does not exist or you do not have access to it.","type":"invalid_request_error","param":"model","request_id":"ar-1234"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task ListInternalErrorUsesTheExceptionMessage()
    {
        // writeOpenAIInternalError: 500 / internal_server_error / server_error.
        var context = NewGetContext("/v1/models");
        var service = new ModelService(
            new FailedChannelProvider(new InvalidOperationException("channel query failed")),
            new StaticModelSettingsProvider(ModelSettings.Default));

        await ModelsEndpoint.ListOpenAiModelsAsync(context, service);

        AssertJsonResponse(context, 500);
        Assert.Equal(
            """{"error":{"code":"internal_server_error","message":"channel query failed","type":"server_error"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task AnthropicListReturnsThePaginatedShape()
    {
        var context = NewGetContext("/anthropic/v1/models");

        await ModelsEndpoint.ListAnthropicModelsAsync(context, NewService(OpenAiChannel()));

        AssertJsonResponse(context, 200);
        Assert.Equal(
            """{"data":[{"id":"gpt-4o","type":"model","display_name":"gpt-4o","created":"2024-04-05T19:34:58Z"}],"first_id":"gpt-4o","has_more":false,"last_id":"gpt-4o","object":"list"}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task AnthropicEmptyListUsesEmptyIdPagination()
    {
        var context = NewGetContext("/anthropic/v1/models");

        await ModelsEndpoint.ListAnthropicModelsAsync(context, NewService());

        AssertJsonResponse(context, 200);
        Assert.Equal(
            """{"data":[],"first_id":"","has_more":false,"last_id":"","object":"list"}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task AnthropicInternalErrorKeepsTheEmptyRequestId()
    {
        // anthropic.AnthropicError always writes request_id, even as "".
        var context = NewGetContext("/anthropic/v1/models");
        var service = new ModelService(
            new FailedChannelProvider(new InvalidOperationException("channel query failed")),
            new StaticModelSettingsProvider(ModelSettings.Default));

        await ModelsEndpoint.ListAnthropicModelsAsync(context, service);

        AssertJsonResponse(context, 500);
        Assert.Equal(
            """{"type":"internal_server_error","request_id":"","error":{"type":"internal_server_error","message":"channel query failed"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task AnthropicInternalErrorEchoesTheRequestId()
    {
        var context = NewGetContext("/anthropic/v1/models");
        context.Items[RequestIds.HttpContextItemKey] = "ar-1234";
        var service = new ModelService(
            new FailedChannelProvider(new InvalidOperationException("channel query failed")),
            new StaticModelSettingsProvider(ModelSettings.Default));

        await ModelsEndpoint.ListAnthropicModelsAsync(context, service);

        Assert.Equal(
            """{"type":"internal_server_error","request_id":"ar-1234","error":{"type":"internal_server_error","message":"channel query failed"}}""",
            ResponseBody(context));
    }

    private sealed class FailedChannelProvider(Exception error) : IEnabledChannelProvider
    {
        public Task<ChannelDescriptor[]> ListEnabledAsync(CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            return Task.FromException<ChannelDescriptor[]>(error);
        }
    }
}
