using System.Text;
using System.Text.Json;
using AxonHub.Api.Endpoints;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Transformer.Shared;
using Microsoft.AspNetCore.Http;

namespace AxonHub.Api.Tests;

/// <summary>
/// Endpoint glue tests for the OpenAI images routes
/// (<c>POST /v1/images/generations</c> and <c>POST /v1/images/edits</c>,
/// routes.go:215-216). Both routes reuse the chat handler with an images
/// inbound transformer, exactly like api.OpenAIHandlers.CreateImage/CreateImageEdit
/// (internal/server/api/openai.go:477-483); the transformers themselves are
/// ported one-for-one in AxonHub.Llm.Tests.
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class ImageEndpointTests(GrainClusterFixture fixture)
{
    private ChatCompletionOrchestrator NewOrchestrator(
        ScriptedExecutor executor,
        RecordingMetricsRecorder metrics,
        RetryPolicy? policy = null)
        => new(
            fixture.Cluster.GrainFactory,
            new OpenAiInboundTransformer(),
            executor,
            metrics,
            new StaticRetryPolicyProvider(
                policy ?? new RetryPolicy
                {
                    Enabled = true,
                    MaxChannelRetries = 3,
                    MaxSingleChannelRetries = 0,
                    RetryDelayMs = 0,
                }),
            fixture.CandidateSelector,
            TimeProvider.System,
            rateLimitTracker: fixture.RateLimitTracker,
            channelLimiterManager: fixture.ChannelLimiterManager);

    /// <summary>Program.cs: <c>orchestrator.WithInbound(ImageInboundTransformer.Generation())</c>.</summary>
    private static ChatCompletionOrchestrator WithGeneration(ChatCompletionOrchestrator orchestrator)
        => orchestrator.WithInbound(ImageInboundTransformer.Generation());

    /// <summary>Program.cs: <c>orchestrator.WithInbound(ImageInboundTransformer.Edit())</c>.</summary>
    private static ChatCompletionOrchestrator WithEdit(ChatCompletionOrchestrator orchestrator)
        => orchestrator.WithInbound(ImageInboundTransformer.Edit());

    private static DefaultHttpContext NewContext(string path, byte[] body, string contentType)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("gateway.test");
        context.Request.Path = path;
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static DefaultHttpContext NewJsonContext(string path, string body)
        => NewContext(path, Encoding.UTF8.GetBytes(body), "application/json");

    private static string ResponseBody(DefaultHttpContext context)
        => Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray());

    private static HttpResponseData ImagesUpstreamResponse(long created = 1710000000)
        => new()
        {
            StatusCode = 200,
            Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["Content-Type"] = ["application/json"],
            },
            Body = Encoding.UTF8.GetBytes(
                "{\"created\":" + created.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"data\":[{\"url\":\"https://img.test/1.png\"}]}"),
        };

    private static readonly byte[] PngBytes =
    [
        0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A,
        0x00, 0x00, 0x00, 0x0D, (byte)'I', (byte)'H', (byte)'D', (byte)'R',
    ];

    private static void AssertJsonError(string body, string expectedType, string expectedMessage)
    {
        using var document = JsonDocument.Parse(body);
        var error = document.RootElement.GetProperty("error");
        Assert.Equal(expectedType, error.GetProperty("type").GetString());
        Assert.Equal(expectedMessage, error.GetProperty("message").GetString());
    }

    private static List<GoMultipartPart> ReadMultipart(HttpRequestData request)
    {
        var contentType = HttpClientUtils.HeaderGet(request.Headers, "Content-Type");
        Assert.True(
            GoMimeTypes.TryParseMediaType(contentType, out _, out var parameters),
            "upstream Content-Type should parse as a media type");
        Assert.True(parameters.TryGetValue("boundary", out var boundary), "boundary is required");

        var reader = new GoMultipartReader(request.Body!, boundary);
        var parts = new List<GoMultipartPart>();
        while (reader.NextPart() is { } part)
        {
            parts.Add(part);
        }

        return parts;
    }

    [Fact]
    public async Task GenerationEmptyBodyReturnsBadRequest()
    {
        // api.ChatCompletion's empty-body guard runs before the inbound
        // transform, so CreateImage reports the generic JSON error.
        fixture.Channels.Set();
        var context = NewContext("/v1/images/generations", [], "application/json");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithGeneration(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal(
            "{\"error\":{\"type\":\"Bad Request\",\"message\":\"Request body is empty\"}}",
            ResponseBody(context));
    }

    [Fact]
    public async Task GenerationForwardsJsonAndReturnsImagesResponse()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4901, "image-model"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ImagesUpstreamResponse(),
        };
        var context = NewJsonContext(
            "/v1/images/generations",
            "{\"model\":\"image-model\",\"prompt\":\"a red fox\",\"size\":\"1024x1024\",\"n\":1}");
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithGeneration(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using (var body = JsonDocument.Parse(ResponseBody(context)))
        {
            Assert.Equal(1710000000L, body.RootElement.GetProperty("created").GetInt64());
            var data = body.RootElement.GetProperty("data");
            Assert.Equal(1, data.GetArrayLength());
            Assert.Equal("https://img.test/1.png", data[0].GetProperty("url").GetString());
        }

        var upstream = Assert.Single(executor.Requests);
        Assert.Equal("http://upstream.test/v1/images/generations", upstream.Url);
        Assert.Equal(ApiFormats.OpenAIImageGeneration, upstream.ApiFormat);
        Assert.Equal("application/json", HttpClientUtils.HeaderGet(upstream.Headers, "Content-Type"));

        using var upstreamBody = JsonDocument.Parse(upstream.Body!);
        Assert.Equal("image-model", upstreamBody.RootElement.GetProperty("model").GetString());
        Assert.Equal("a red fox", upstreamBody.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("1024x1024", upstreamBody.RootElement.GetProperty("size").GetString());
        Assert.Equal(1L, upstreamBody.RootElement.GetProperty("n").GetInt64());
    }

    [Fact]
    public async Task GenerationDefaultsModelToDallE2()
    {
        // transformGenerationRequest: an omitted model becomes "dall-e-2"
        // before candidate selection and the outbound body.
        fixture.Channels.Set(TestChannels.OpenAi(4902, "dall-e-2"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ImagesUpstreamResponse(),
        };
        var context = NewJsonContext("/v1/images/generations", "{\"prompt\":\"a cat\"}");
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithGeneration(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        var upstream = Assert.Single(executor.Requests);
        using var upstreamBody = JsonDocument.Parse(upstream.Body!);
        Assert.Equal("dall-e-2", upstreamBody.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task GenerationCountsImageResponseAsContentWhenEmptyDetectionIsOn()
    {
        // empty_response.go checks resp.Image before the chat choices; without
        // that branch an images response would be retried as "empty".
        fixture.Channels.Set(TestChannels.OpenAi(4903, "image-model"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ImagesUpstreamResponse(),
        };
        var context = NewJsonContext("/v1/images/generations", "{\"model\":\"image-model\",\"prompt\":\"a cat\"}");
        var orchestrator = NewOrchestrator(
            executor,
            new RecordingMetricsRecorder(),
            new RetryPolicy
            {
                Enabled = true,
                MaxChannelRetries = 3,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
                EmptyResponseDetection = true,
            });

        await ChatCompletionEndpoint.HandleAsync(context, WithGeneration(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task EditMultipartForwardsModelFirstAndReturnsImagesResponse()
    {
        fixture.Channels.Set(TestChannels.OpenAi(4904, "gpt-image-1"));
        var executor = new ScriptedExecutor
        {
            OnDo = (_, _, _) => ImagesUpstreamResponse(),
        };

        var writer = new GoMultipartWriter();
        writer.WriteField("model", "gpt-image-1");
        writer.WriteField("prompt", "make it blue");
        writer.CreatePart("image", "fox.png", "image/png", PngBytes);
        writer.Close();

        var context = NewContext(
            "/v1/images/edits",
            writer.ToArray(),
            writer.FormDataContentType());
        var orchestrator = NewOrchestrator(executor, new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithEdit(orchestrator));

        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal("application/json", context.Response.ContentType);

        using (var body = JsonDocument.Parse(ResponseBody(context)))
        {
            Assert.Equal("https://img.test/1.png", body.RootElement
                .GetProperty("data")[0].GetProperty("url").GetString());
        }

        var upstream = Assert.Single(executor.Requests);
        Assert.Equal("http://upstream.test/v1/images/edits", upstream.Url);
        Assert.Equal(ApiFormats.OpenAIImageEdit, upstream.ApiFormat);

        var parts = ReadMultipart(upstream);

        // buildImageEditRequest writes model before the image parts.
        Assert.Equal("model", parts[0].Name);
        Assert.Equal("gpt-image-1", Encoding.UTF8.GetString(parts[0].Data));
        Assert.Contains(
            parts,
            part => part.Name == "prompt" && Encoding.UTF8.GetString(part.Data) == "make it blue");
        // The outbound names image parts from the sniffed content type
        // (image_1.png); the client filename is not forwarded.
        Assert.Contains(
            parts,
            part => part.FileName == "image_1.png"
                && part.ContentType == "image/png"
                && part.Data.SequenceEqual(PngBytes));
    }

    [Fact]
    public async Task EditMultipartWithoutImageReturnsInvalidRequest()
    {
        fixture.Channels.Set();
        var writer = new GoMultipartWriter();
        writer.WriteField("prompt", "make it blue");
        writer.Close();

        var context = NewContext(
            "/v1/images/edits",
            writer.ToArray(),
            writer.FormDataContentType());
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithEdit(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        AssertJsonError(
            ResponseBody(context),
            "invalid_request_error",
            "invalid request: at least one image is required for edits");
    }

    [Fact]
    public async Task GenerationWithoutPromptReturnsInvalidRequest()
    {
        fixture.Channels.Set();
        var context = NewJsonContext("/v1/images/generations", "{\"model\":\"dall-e-2\"}");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithGeneration(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        AssertJsonError(
            ResponseBody(context),
            "invalid_request_error",
            "invalid request: prompt is required");
    }

    [Fact]
    public async Task GenerationRequiresJsonContentType()
    {
        fixture.Channels.Set();
        var context = NewContext(
            "/v1/images/generations",
            Encoding.UTF8.GetBytes("{\"prompt\":\"a cat\"}"),
            "text/plain");
        var orchestrator = NewOrchestrator(new ScriptedExecutor(), new RecordingMetricsRecorder());

        await ChatCompletionEndpoint.HandleAsync(context, WithGeneration(orchestrator));

        Assert.Equal(400, context.Response.StatusCode);
        AssertJsonError(
            ResponseBody(context),
            "invalid_request_error",
            "invalid request: generations requires application/json");
    }
}
