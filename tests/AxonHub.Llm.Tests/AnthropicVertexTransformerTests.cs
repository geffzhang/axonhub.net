using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.Anthropic;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Llm.Vertex;

namespace AxonHub.Llm.Tests;

/// <summary>
/// Behaviour locks for the Vertex-wrapped Anthropic outbound
/// (llm/transformer/anthropic/vertex.go + outbound.go:88-120): the factory
/// builds the executor before base-URL normalization, the wrapper swaps in the
/// executor, and the resulting wire request is the Vertex rawPredict shape.
/// </summary>
public sealed class AnthropicVertexTransformerTests
{
    private const string CredentialsJson =
        """{"type":"authorized_user","client_id":"cid","client_secret":"secret","refresh_token":"rt"}""";

    [Fact]
    public void CreateWithConfig_returns_a_vertex_transformer()
    {
        var transformer = AnthropicOutboundTransformer.CreateWithConfig(VertexConfig());

        var vertex = Assert.IsType<VertexTransformer>(transformer);
        Assert.Equal(ApiFormats.AnthropicMessage, vertex.ApiFormat);
        Assert.IsType<VertexExecutor>(
            ((IChannelCustomizedExecutor)vertex).CustomizeExecutor(FakeExecutor.Instance));
    }

    [Fact]
    public void CreateWithConfig_wraps_credential_parse_errors()
    {
        var config = VertexConfig();
        config.JsonData = string.Empty;

        var error = Assert.Throws<InvalidOperationException>(
            () => AnthropicOutboundTransformer.CreateWithConfig(config));

        Assert.Equal(
            "failed to create vertex transformer: failed to parse credentials: unexpected end of JSON input",
            error.Message);
    }

    [Fact]
    public void CreateWithConfig_wraps_executor_validation_errors()
    {
        var config = VertexConfig();
        config.Region = string.Empty;

        var error = Assert.Throws<InvalidOperationException>(
            () => AnthropicOutboundTransformer.CreateWithConfig(config));

        Assert.Equal("failed to create vertex transformer: region must be provided", error.Message);
    }

    [Fact]
    public void CreateWithConfig_rejects_a_missing_config()
    {
        Assert.Throws<ArgumentNullException>(
            () => AnthropicOutboundTransformer.CreateWithConfig(null!));
    }

    [Fact]
    public void Wrapper_forwards_the_api_key_provenance()
    {
        var config = VertexConfig();
        config.ApiKeyProvider = new StaticApiKeyProvider("key-x");
        config.ChannelApiKeyIndex = 2;

        var transformer = new VertexTransformer(
            new AnthropicOutboundTransformer(config),
            VertexExecutor.FromJson(config.Region, config.ProjectId, config.JsonData));

        transformer.TransformRequest(SimpleRequest());

        Assert.Equal("key-x", transformer.SelectedApiKey);
        Assert.Equal(2, transformer.SelectedApiKeyIndex);
    }

    [Fact]
    public void Streaming_requests_use_streamRawPredict()
    {
        var request = SimpleRequest();
        request.Stream = true;

        var transformer = Transformer(
            new ScriptedHandler(_ => Json(HttpStatusCode.OK, "{}")),
            new ScriptedHandler(_ => Json(HttpStatusCode.OK, """{"access_token":"t","expires_in":3600}""")));

        var outbound = transformer.TransformRequest(request);

        Assert.EndsWith(":streamRawPredict", outbound.Url, StringComparison.Ordinal);
    }

    [Fact]
    public void Executing_a_vertex_request_rewrites_the_url_and_signs_it()
    {
        var captured = new List<(string Url, string Authorization, string Body)>();
        var apiHandler = new ScriptedHandler(request =>
        {
            captured.Add((
                request.RequestUri!.ToString(),
                Header(request, "Authorization"),
                BodyText(request)));
            return Json(HttpStatusCode.OK, "{}");
        });
        var tokenHandler = new ScriptedHandler(_ => Json(
            HttpStatusCode.OK,
            """{"access_token":"token-1","token_type":"Bearer","expires_in":3600}"""));

        var transformer = Transformer(apiHandler, tokenHandler);

        var outbound = transformer.TransformRequest(SimpleRequest());

        // The outbound leaves BaseUrl empty for Vertex, so its URL is the bare
        // Vertex path; the executor prepends the regional endpoint.
        Assert.Equal(
            "/v1/projects/proj/locations/us-central1/publishers/anthropic/models/claude-3-sonnet-20240229:rawPredict",
            outbound.Url);
        Assert.Equal("vertex-2023-10-16", HttpClientUtils.HeaderGet(outbound.Headers, "Anthropic-Version"));
        Assert.Null(outbound.Auth);

        var executor = ((IChannelCustomizedExecutor)transformer)
            .CustomizeExecutor(FakeExecutor.Instance);
        executor.Do(CancellationToken.None, outbound);

        var request = Assert.Single(captured);

        // The reference base URL ends with "/" and the path starts with "/", so
        // the wire URL carries a double slash.
        Assert.Equal(
            "https://us-central1-aiplatform.googleapis.com//v1/projects/proj/locations/us-central1/publishers/anthropic/models/claude-3-sonnet-20240229:rawPredict",
            request.Url);
        Assert.Equal("Bearer token-1", request.Authorization);

        using var body = JsonDocument.Parse(request.Body);

        // The executor adds anthropic_version; the outbound kept model in the
        // body because its "/v1/messages" rewrite branch is not reached by this
        // pipeline (the outbound already produced the Vertex path).
        Assert.Equal("vertex-2023-10-16", body.RootElement.GetProperty("anthropic_version").GetString());
        Assert.Equal("claude-3-sonnet-20240229", body.RootElement.GetProperty("model").GetString());
    }

    private static VertexTransformer Transformer(
        HttpMessageHandler apiHandler,
        HttpMessageHandler tokenHandler)
    {
        var config = VertexConfig();
        var executor = VertexExecutor.FromJson(
            config.Region,
            config.ProjectId,
            config.JsonData,
            new VertexExecutorOptions
            {
                Transport = apiHandler,
                TokenTransport = tokenHandler,
            });

        return new VertexTransformer(new AnthropicOutboundTransformer(config), executor);
    }

    private static AnthropicOutboundConfig VertexConfig() => new()
    {
        Type = AnthropicPlatformType.Vertex,
        Region = "us-central1",
        ProjectId = "proj",
        JsonData = CredentialsJson,
        BaseUrl = string.Empty,
    };

    private static LlmRequest SimpleRequest(string text = "Hello, Claude!")
        => new()
        {
            Model = "claude-3-sonnet-20240229",
            MaxTokens = 1024,
            Messages =
            [
                new Message { Role = "user", Content = MessageContent.FromText(text) },
            ],
        };

    private static string Header(HttpRequestMessage request, string name)
        => request.Headers.TryGetValues(name, out var values)
            ? string.Join(", ", values)
            : string.Empty;

    private static string BodyText(HttpRequestMessage request)
        => request.Content is null
            ? string.Empty
            : request.Content.ReadAsStringAsync().GetAwaiter().GetResult();

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class ScriptedHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class FakeExecutor : IExecutor
    {
        public static readonly FakeExecutor Instance = new();

        public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
            => throw new NotSupportedException();

        public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
            => throw new NotSupportedException();
    }
}
