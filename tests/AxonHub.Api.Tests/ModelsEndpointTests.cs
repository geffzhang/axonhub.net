using System.Text;
using System.Text.Json;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Http;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Persistence;
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

    private const string ExtendedModelCard = """
        {"reasoning":{"supported":true,"default":false},"toolCall":true,"temperature":false,"modalities":{"input":["text","image"],"output":["text"]},"vision":true,"cost":{"input":2.5,"output":10,"cacheRead":1.25,"cacheWrite":3.75},"limit":{"context":128000,"output":16384},"knowledge":"2024-04","releaseDate":"2024-05-13","lastUpdated":"2024-05-13"}
        """;

    private const string ExtendedModelJson =
        """{"id":"gpt-4o","object":"model","created":1712345698,"owned_by":"openai","name":"GPT-4o","description":"A test model","context_length":128000,"max_output_tokens":16384,"modalities":{"input":["text","image"],"output":["text"]},"capabilities":{"vision":true,"tool_call":true,"reasoning":true},"pricing":{"input":2.5,"output":10,"cache_read":1.25,"cache_write":3.75,"unit":"per_1m_tokens","currency":"USD"},"icon":"sparkles","type":"chat"}""";

    private sealed record Fixture(
        ModelService Service,
        StaticModelSettingsProvider Settings,
        FakeModelStore ModelStore);

    private static ChannelRecord OpenAiChannel(
        long id = 1,
        string model = "gpt-4o",
        string? supportedModels = null)
        => ModelTestSupport.Channel(
            id,
            "openai",
            model,
            supportedModels: supportedModels ?? "[\"" + model + "\"]",
            createdAt: CreatedAt);

    private static Fixture NewFixture(
        IReadOnlyList<ChannelRecord>? channels = null,
        IReadOnlyList<ModelRecord>? models = null,
        ModelSettings? settings = null,
        IManagementIdentity? identity = null)
    {
        settings ??= ModelSettings.Default;
        var (service, _, modelStore) = ModelTestSupport.ServiceWithStores(
            channels ?? [],
            models,
            settings,
            identity);
        return new Fixture(service, new StaticModelSettingsProvider(settings), modelStore);
    }

    /// <summary>
    /// The extended-metadata fixture: a channel serving gpt-4o plus an enabled
    /// configured row with a full model card associated with that channel.
    /// </summary>
    private static Fixture ExtendedFixture(ModelSettings? settings = null)
        => NewFixture(
            [OpenAiChannel()],
            [
                ModelTestSupport.ConfiguredModel(
                    "gpt-4o",
                    developer: "openai",
                    name: "GPT-4o",
                    icon: "sparkles",
                    remark: "A test model",
                    modelCard: ExtendedModelCard,
                    settings: ModelTestSupport.ChannelModelSettings(1, "gpt-4o")),
            ],
            settings);

    private static DefaultHttpContext NewGetContext(string path, string? query = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.test");
        context.Request.Path = new PathString(path);
        if (query is not null)
        {
            context.Request.QueryString = new QueryString(query);
        }

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

    private static Task ListAsync(DefaultHttpContext context, Fixture fixture)
        => ModelsEndpoint.ListOpenAiModelsAsync(
            context,
            fixture.Service,
            fixture.Settings,
            fixture.ModelStore);

    private static Task RetrieveAsync(
        DefaultHttpContext context, string? model, Fixture fixture)
        => ModelsEndpoint.RetrieveOpenAiModelAsync(
            context,
            model,
            fixture.Service,
            fixture.ModelStore);

    private static IManagementIdentity DeniedIdentity()
        => ModelTestSupport.ApiKeyIdentity(
            ModelTestSupport.ApiKey(scopes: "write_requests"));

    [Fact]
    public async Task ListReturnsTheFacadeShape()
    {
        var context = NewGetContext("/v1/models");

        await ListAsync(context, NewFixture([OpenAiChannel()]));

        AssertJsonResponse(context, 200);
        Assert.Equal(
            """{"data":[{"id":"gpt-4o","object":"model","created":1712345698,"owned_by":"openai"}],"object":"list"}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task ListEmptyIsAnEmptyDataArray()
    {
        var context = NewGetContext("/v1/models");

        await ListAsync(context, NewFixture());

        AssertJsonResponse(context, 200);
        Assert.Equal("""{"data":[],"object":"list"}""", ResponseBody(context));
    }

    [Fact]
    public async Task ListHTMLEscapesLikeGo()
    {
        var context = NewGetContext("/v1/models");

        await ListAsync(
            context,
            NewFixture([OpenAiChannel(supportedModels: "[\"a<b>&c\"]")]));

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

        await ListAsync(context, NewFixture([OpenAiChannel()]));

        AssertJsonResponse(context, 404);
        Assert.Equal(
            """{"error":{"code":"model_not_found","message":"The model does not exist or you do not have access to it.","type":"invalid_request_error","param":"model"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task RetrieveReturnsTheModel()
    {
        var context = NewGetContext("/v1/models/gpt-4o");

        await RetrieveAsync(context, "gpt-4o", NewFixture([OpenAiChannel()]));

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

        await RetrieveAsync(
            context,
            "deepseek/chat",
            NewFixture([OpenAiChannel(model: "deepseek/chat")]));

        AssertJsonResponse(context, 200);
        Assert.Equal(
            """{"id":"deepseek/chat","object":"model","created":1712345698,"owned_by":"openai"}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task RetrieveUnknownModelIsModelNotFound()
    {
        var context = NewGetContext("/v1/models/claude-3");

        await RetrieveAsync(context, "claude-3", NewFixture([OpenAiChannel()]));

        AssertJsonResponse(context, 404);
        Assert.Equal(
            """{"error":{"code":"model_not_found","message":"The model `claude-3` does not exist or you do not have access to it.","type":"invalid_request_error","param":"model"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task RetrieveEmptyModelIdHasNoBacktickedName()
    {
        var context = NewGetContext("/v1/models/");

        await RetrieveAsync(context, string.Empty, NewFixture([OpenAiChannel()]));

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

        await RetrieveAsync(context, "claude-3", NewFixture([OpenAiChannel()]));

        Assert.Equal(
            """{"error":{"code":"model_not_found","message":"The model `claude-3` does not exist or you do not have access to it.","type":"invalid_request_error","param":"model","request_id":"ar-1234"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task ListInternalErrorUsesTheExceptionMessage()
    {
        // writeOpenAIInternalError: 500 / internal_server_error / server_error.
        // The configured-model query failure is wrapped like the reference's
        // queryConfiguredModelFacades.
        var context = NewGetContext("/v1/models");
        var fixture = NewFixture([OpenAiChannel()]);
        fixture.ModelStore.ListAllFailure = new InvalidOperationException("channel query failed");

        await ListAsync(context, fixture);

        AssertJsonResponse(context, 500);
        Assert.Equal(
            """{"error":{"code":"internal_server_error","message":"failed to list configured models: channel query failed","type":"server_error"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task ListPermissionDeniedWithoutReadChannelsIs403()
    {
        var context = NewGetContext("/v1/models");

        await ListAsync(context, NewFixture([OpenAiChannel()], identity: DeniedIdentity()));

        AssertJsonResponse(context, 403);
        Assert.Equal(
            """{"error":{"code":"permission_denied","message":"insufficient permissions to access this resource","type":"authentication_error"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task RetrievePermissionDeniedWithoutReadChannelsIs403()
    {
        var context = NewGetContext("/v1/models/gpt-4o");

        await RetrieveAsync(
            context,
            "gpt-4o",
            NewFixture([OpenAiChannel()], identity: DeniedIdentity()));

        AssertJsonResponse(context, 403);
        Assert.Equal(
            """{"error":{"code":"permission_denied","message":"insufficient permissions to access this resource","type":"authentication_error"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task ListIncludeAllReturnsExtendedFields()
    {
        var context = NewGetContext("/v1/models", "?include=all");

        await ListAsync(context, ExtendedFixture());

        AssertJsonResponse(context, 200);
        Assert.Equal(
            """{"data":[""" + ExtendedModelJson + """],"object":"list"}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task ListIncludeNameOnlyOmitsOtherExtendedFields()
    {
        var context = NewGetContext("/v1/models", "?include=name");

        await ListAsync(context, ExtendedFixture());

        AssertJsonResponse(context, 200);
        Assert.Equal(
            """{"data":[{"id":"gpt-4o","object":"model","created":1712345698,"owned_by":"openai","name":"GPT-4o"}],"object":"list"}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task ListDefaultIncludeAllComesFromSystemSettings()
    {
        // DefaultModelApiIncludeAll=true makes a bare GET behave like
        // ?include=all; the reference resolves it before the model list.
        var settings = ModelSettings.Default with { DefaultModelApiIncludeAll = true };
        var context = NewGetContext("/v1/models");

        await ListAsync(context, ExtendedFixture(settings));

        Assert.Equal(
            """{"data":[""" + ExtendedModelJson + """],"object":"list"}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task ListIncludeAllWithoutConfiguredRowFallsBackToTheFacade()
    {
        var settings = ModelSettings.Default with { DefaultModelApiIncludeAll = true };
        var context = NewGetContext("/v1/models");

        await ListAsync(context, NewFixture([OpenAiChannel()], settings: settings));

        Assert.Equal(
            """{"data":[{"id":"gpt-4o","object":"model","created":1712345698,"owned_by":"openai"}],"object":"list"}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task ListIncludeAllMalformedModelCardIsInternalError()
    {
        // ent scans model_card while listing the configured rows, so a
        // malformed document fails the whole list before any conversion.
        var fixture = NewFixture(
            [OpenAiChannel()],
            [
                ModelTestSupport.ConfiguredModel(
                    "gpt-4o",
                    modelCard: "[1]",
                    settings: ModelTestSupport.ChannelModelSettings(1, "gpt-4o")),
            ]);
        var context = NewGetContext("/v1/models", "?include=all");

        await ListAsync(context, fixture);

        AssertJsonResponse(context, 500);
        Assert.Equal(
            """{"error":{"code":"internal_server_error","message":"failed to list configured models: unmarshal field model_card: json: cannot unmarshal array into Go value of type objects.ModelCard","type":"server_error"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task ListFullDataLookupFailureIsInternalError()
    {
        var fixture = ExtendedFixture();
        fixture.ModelStore.ListAllFailureFor = filter =>
            filter.ModelIdIn is not null ? new InvalidOperationException("db is down") : null;
        var context = NewGetContext("/v1/models", "?include=all");

        await ListAsync(context, fixture);

        AssertJsonResponse(context, 500);
        Assert.Equal(
            """{"error":{"code":"internal_server_error","message":"db is down","type":"server_error"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task RetrieveIncludeAllUsesTheConfiguredRow()
    {
        var context = NewGetContext("/v1/models/gpt-4o", "?include=all");

        await RetrieveAsync(context, "gpt-4o", ExtendedFixture());

        AssertJsonResponse(context, 200);
        Assert.Equal(ExtendedModelJson, ResponseBody(context));
    }

    [Fact]
    public async Task RetrieveIgnoresTheListDefaultIncludeAll()
    {
        // RetrieveModel hard-codes the false include default (openai.go:856);
        // only ListModels consults DefaultModelAPIIncludeAll. So even though
        // the extended row exists, the response is the visible facade, which
        // for a configured model is owned_by "configured" (model.go:796).
        var settings = ModelSettings.Default with { DefaultModelApiIncludeAll = true };
        var context = NewGetContext("/v1/models/gpt-4o");

        await RetrieveAsync(context, "gpt-4o", ExtendedFixture(settings));

        Assert.Equal(
            """{"id":"gpt-4o","object":"model","created":1712345698,"owned_by":"configured"}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task RetrieveIncludeAllWithoutConfiguredRowFallsBackToTheFacade()
    {
        var context = NewGetContext("/v1/models/gpt-4o", "?include=all");

        await RetrieveAsync(context, "gpt-4o", NewFixture([OpenAiChannel()]));

        Assert.Equal(
            """{"id":"gpt-4o","object":"model","created":1712345698,"owned_by":"openai"}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task RetrieveDuplicateModelIdIsNotSingular()
    {
        // ent's Only returns "ent: model not singular" when several live rows
        // share the model id (the unique index normally prevents it).
        var fixture = NewFixture(
            [OpenAiChannel()],
            [
                ModelTestSupport.ConfiguredModel(
                    "gpt-4o",
                    settings: ModelTestSupport.ChannelModelSettings(1, "gpt-4o")),
                ModelTestSupport.ConfiguredModel(
                    "gpt-4o",
                    settings: ModelTestSupport.ChannelModelSettings(1, "gpt-4o")),
            ]);
        var context = NewGetContext("/v1/models/gpt-4o", "?include=all");

        await RetrieveAsync(context, "gpt-4o", fixture);

        AssertJsonResponse(context, 500);
        Assert.Equal(
            """{"error":{"code":"internal_server_error","message":"ent: model not singular","type":"server_error"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task AnthropicListReturnsThePaginatedShape()
    {
        var context = NewGetContext("/anthropic/v1/models");

        await ModelsEndpoint.ListAnthropicModelsAsync(
            context, NewFixture([OpenAiChannel()]).Service);

        AssertJsonResponse(context, 200);
        Assert.Equal(
            """{"data":[{"id":"gpt-4o","type":"model","display_name":"gpt-4o","created":"2024-04-05T19:34:58Z"}],"first_id":"gpt-4o","has_more":false,"last_id":"gpt-4o","object":"list"}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task AnthropicEmptyListUsesEmptyIdPagination()
    {
        var context = NewGetContext("/anthropic/v1/models");

        await ModelsEndpoint.ListAnthropicModelsAsync(context, NewFixture().Service);

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
        var fixture = NewFixture([OpenAiChannel()]);
        fixture.ModelStore.ListAllFailure = new InvalidOperationException("channel query failed");

        await ModelsEndpoint.ListAnthropicModelsAsync(context, fixture.Service);

        AssertJsonResponse(context, 500);
        Assert.Equal(
            """{"type":"internal_server_error","request_id":"","error":{"type":"internal_server_error","message":"failed to list configured models: channel query failed"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task AnthropicInternalErrorEchoesTheRequestId()
    {
        var context = NewGetContext("/anthropic/v1/models");
        context.Items[RequestIds.HttpContextItemKey] = "ar-1234";
        var fixture = NewFixture([OpenAiChannel()]);
        fixture.ModelStore.ListAllFailure = new InvalidOperationException("channel query failed");

        await ModelsEndpoint.ListAnthropicModelsAsync(context, fixture.Service);

        Assert.Equal(
            """{"type":"internal_server_error","request_id":"ar-1234","error":{"type":"internal_server_error","message":"failed to list configured models: channel query failed"}}""",
            ResponseBody(context));
    }

    [Fact]
    public async Task AnthropicPermissionDeniedIsAPermissionError()
    {
        // entprivacy.Deny maps to 403 permission_error; the message is the
        // wrapped ListEnabledModels error.
        var context = NewGetContext("/anthropic/v1/models");
        var fixture = NewFixture([OpenAiChannel()], identity: DeniedIdentity());

        await ModelsEndpoint.ListAnthropicModelsAsync(context, fixture.Service);

        AssertJsonResponse(context, 403);
        Assert.Equal(
            """{"type":"permission_error","request_id":"","error":{"type":"permission_error","message":"failed to list configured models: ent/privacy: deny rule"}}""",
            ResponseBody(context));
    }
}
