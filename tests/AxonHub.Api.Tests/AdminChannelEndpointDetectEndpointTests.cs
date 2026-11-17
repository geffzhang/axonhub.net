using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the detectChannelEndpoints mutation against the reference resolver and
/// biz.ChannelService.DetectChannelEndpoints (axonhub.resolvers.go:199-201;
/// channel_endpoint_detect.go:72-240): the service loads the live row, builds
/// the channel's primary outbound once, probes chat completions, responses and
/// messages concurrently, and classifies each outcome from the upstream status
/// without persisting anything.
/// </summary>
public sealed class AdminChannelEndpointDetectEndpointTests : IAsyncLifetime
{
    private static readonly string[] InputFieldNames = ["channelID", "model"];

    private static readonly string[] DetectedFieldNames =
        ["apiFormat", "supported", "statusCode", "reason"];

    private static readonly string[] DetectableFormats =
    [
        ApiFormats.OpenAIChatCompletion,
        ApiFormats.OpenAIResponse,
        ApiFormats.AnthropicMessage,
    ];

    private const string DetectMutation =
        """
        mutation Detect($input: DetectChannelEndpointsInput!) {
          detectChannelEndpoints(input: $input) {
            endpoints { apiFormat supported statusCode reason }
          }
        }
        """;

    private readonly FakeChannelWriteStore _channels = new();
    private readonly DetectExecutor _executor = new();
    private readonly DetectExecutor _proxyExecutor = new();
    private readonly ScriptedProxyExecutorFactory _proxyFactory = new();

    private readonly AdminGraphqlGateServices _gate = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IChannelWriteStore>(_channels);
        builder.Services.AddSingleton<IExecutor>(_executor);
        _proxyFactory.Executor = _proxyExecutor;
        builder.Services.AddSingleton<IProxyExecutorFactory>(_proxyFactory);
        builder.Services.AddSingleton<ChannelProxyExecutors>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddScoped<ChannelEndpointDetectService>();

        _app = builder.Build();
        _app.MapAdminGraphql();
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _gate.Authorize(_client);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task DetectChannelEndpointsProbesTheThreeFormatsInTheReferenceOrder()
    {
        var channel = SeedChannel();

        _executor.OnDo = request => PathOf(request) switch
        {
            "/v1/chat/completions" => Ok(200),
            "/v1/responses" => throw TestChannels.HttpClient(404),
            "/v1/messages" => throw TestChannels.HttpClient(401),
            _ => throw new InvalidOperationException("unexpected probe " + request.Url),
        };

        var data = await QueryDataAsync(DetectMutation, Input(channel.Id));

        var endpoints = Endpoints(data);
        Assert.Equal(3, endpoints.Length);

        AssertEndpoint(endpoints[0], ApiFormats.OpenAIChatCompletion, true, 200, "supported");
        AssertEndpoint(endpoints[1], ApiFormats.OpenAIResponse, false, 404, "not_found");
        AssertEndpoint(endpoints[2], ApiFormats.AnthropicMessage, false, 401, "auth_error");

        var requests = _executor.Requests;
        Assert.Equal(3, requests.Length);

        // The primary OpenAI outbound serves chat; responses and messages fall
        // back to the generic transformers with the entity's raw base URL.
        var chat = RequestFor(requests, "/v1/chat/completions");
        Assert.Equal("http://upstream.test/v1/chat/completions", chat.Url);
        Assert.Equal(AuthTypes.Bearer, chat.Auth!.Type);
        Assert.Equal("sk-test", chat.Auth.ApiKey);

        var responses = RequestFor(requests, "/v1/responses");
        Assert.Equal("http://upstream.test/v1/responses", responses.Url);
        Assert.Equal(AuthTypes.Bearer, responses.Auth!.Type);
        Assert.Equal("sk-test", responses.Auth.ApiKey);

        var messages = RequestFor(requests, "/v1/messages");
        Assert.Equal("http://upstream.test/v1/messages", messages.Url);
        Assert.Equal(AuthTypes.ApiKey, messages.Auth!.Type);
        Assert.Equal("X-API-Key", messages.Auth.HeaderKey);
        Assert.Equal("sk-test", messages.Auth.ApiKey);

        // Detection is read-only: nothing is written back to the channel.
        Assert.Empty(_channels.UpdateCalls);
        Assert.Empty(_channels.SaveEndpointsCalls);
        Assert.Empty(_channels.CreatedChannels);
    }

    [Fact]
    public async Task DetectChannelEndpointsProbesThroughTheChannelProxyClient()
    {
        // channel_endpoint_detect.go:166 probes with ch.HTTPClient, which
        // buildChannelWithTransformer builds from settings.proxy
        // (channel_llm.go:121-127, 707). All three probes share that client.
        var channel = SeedChannel(
            settings: """
                {"proxy":{"type":"url","url":"http://proxy.test:8080","username":"u","password":"p"}}
                """);
        _proxyExecutor.OnDo = _ => Ok(200);

        var data = await QueryDataAsync(DetectMutation, Input(channel.Id));

        Assert.Equal(3, Endpoints(data).Length);
        Assert.Equal(3, _proxyExecutor.Requests.Length);
        Assert.Empty(_executor.Requests);

        var proxy = Assert.Single(_proxyFactory.Requests);
        Assert.Equal(ProxyType.Url, proxy.Type);
        Assert.Equal("http://proxy.test:8080", proxy.Url);
        Assert.Equal("u", proxy.Username);
        Assert.Equal("p", proxy.Password);
    }

    [Fact]
    public async Task DetectChannelEndpointsWithoutAChannelProxyKeepsTheSharedExecutor()
    {
        var channel = SeedChannel();
        _executor.OnDo = _ => Ok(200);

        var data = await QueryDataAsync(DetectMutation, Input(channel.Id));

        Assert.Equal(3, Endpoints(data).Length);
        Assert.Equal(3, _executor.Requests.Length);
        Assert.Empty(_proxyExecutor.Requests);
        Assert.Empty(_proxyFactory.Requests);
    }

    [Theory]
    [InlineData(404, false, "not_found")]
    [InlineData(405, false, "not_found")]
    [InlineData(400, true, "supported")]
    [InlineData(422, true, "supported")]
    [InlineData(401, false, "auth_error")]
    [InlineData(403, false, "auth_error")]
    [InlineData(429, true, "rate_limited")]
    [InlineData(500, false, "server_error")]
    [InlineData(503, false, "server_error")]
    public async Task DetectChannelEndpointsClassifiesTheUpstreamStatus(
        int statusCode,
        bool supported,
        string reason)
    {
        var channel = SeedChannel();
        _executor.OnDo = _ => throw TestChannels.HttpClient(statusCode);

        var data = await QueryDataAsync(DetectMutation, Input(channel.Id));

        var endpoints = Endpoints(data);
        Assert.Equal(3, endpoints.Length);
        foreach (var endpoint in endpoints)
        {
            AssertEndpoint(endpoint, endpoint.GetProperty("apiFormat").GetString()!, supported, statusCode, reason);
        }
    }

    [Fact]
    public async Task DetectChannelEndpointsKeepsZeroStatusForTransportFailures()
    {
        var channel = SeedChannel();
        _executor.OnDo = _ => throw new HttpRequestException("connection refused");

        var data = await QueryDataAsync(DetectMutation, Input(channel.Id));

        var endpoints = Endpoints(data);
        Assert.Equal(3, endpoints.Length);
        foreach (var endpoint in endpoints)
        {
            Assert.False(endpoint.GetProperty("supported").GetBoolean());
            Assert.Equal(0, endpoint.GetProperty("statusCode").GetInt32());
            Assert.Equal("unreachable", endpoint.GetProperty("reason").GetString());
        }
    }

    [Fact]
    public async Task DetectChannelEndpointsUsesTheRequestedModelTrimmed()
    {
        var channel = SeedChannel(defaultTestModel: "gpt-default", supportedModels: """["gpt-supported"]""");
        _executor.OnDo = _ => Ok(200);

        await QueryDataAsync(
            DetectMutation,
            new { input = new { channelID = Gid(channel.Id), model = "  gpt-4.1  " } });

        var requests = _executor.Requests;
        Assert.Equal(3, requests.Length);
        Assert.All(requests, request => Assert.Equal("gpt-4.1", BodyModel(request)));
    }

    [Fact]
    public async Task DetectChannelEndpointsFallsBackToTheDefaultTestModel()
    {
        var channel = SeedChannel(defaultTestModel: "  gpt-default  ", supportedModels: """["gpt-supported"]""");
        _executor.OnDo = _ => Ok(200);

        // A blank requested model falls through like an absent pointer.
        await QueryDataAsync(
            DetectMutation,
            new { input = new { channelID = Gid(channel.Id), model = "   " } });

        var requests = _executor.Requests;
        Assert.Equal(3, requests.Length);
        Assert.All(requests, request => Assert.Equal("gpt-default", BodyModel(request)));
    }

    [Fact]
    public async Task DetectChannelEndpointsFallsBackToTheFirstNonEmptySupportedModel()
    {
        var channel = SeedChannel(
            defaultTestModel: string.Empty,
            supportedModels: """["", "  gpt-first  ", "gpt-second"]""");
        _executor.OnDo = _ => Ok(200);

        await QueryDataAsync(DetectMutation, Input(channel.Id));

        var requests = _executor.Requests;
        Assert.Equal(3, requests.Length);
        Assert.All(requests, request => Assert.Equal("gpt-first", BodyModel(request)));
    }

    [Fact]
    public async Task DetectChannelEndpointsSendsAnEmptyModelEvenWhenNoneIsConfigured()
    {
        var channel = SeedChannel(defaultTestModel: string.Empty, supportedModels: "null");
        _executor.OnDo = _ => Ok(200);

        var data = await QueryDataAsync(DetectMutation, Input(channel.Id));

        var endpoints = Endpoints(data);

        // The OpenAI chat and Anthropic outbounds reject an empty model while
        // building the probe request, so those probes never reach the upstream.
        AssertEndpoint(endpoints[0], ApiFormats.OpenAIChatCompletion, false, 0, "invalid_request");
        AssertEndpoint(endpoints[1], ApiFormats.OpenAIResponse, true, 200, "supported");
        AssertEndpoint(endpoints[2], ApiFormats.AnthropicMessage, false, 0, "invalid_request");

        var probe = Assert.Single(_executor.Requests);
        Assert.Equal("http://upstream.test/v1/responses", probe.Url);
        Assert.Equal(string.Empty, BodyModel(probe));
    }

    [Fact]
    public async Task DetectChannelEndpointsOnAMissingChannelKeepsTheReferenceMessage()
    {
        var errors = await QueryErrorsAsync(DetectMutation, Input(424242));

        Assert.Equal(
            "channel not found: ent: channel not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task DetectChannelEndpointsWrapsAStoreFailureAsChannelNotFound()
    {
        _channels.GetFailure = new StoreFailureException(
            "failed to get channel: database is down",
            new InvalidOperationException("database is down"));

        var errors = await QueryErrorsAsync(DetectMutation, Input(7));

        Assert.Equal(
            "channel not found: failed to get channel: database is down",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task DetectChannelEndpointsFailsWhenThePrimaryOutboundCannotBeBuilt()
    {
        var channel = SeedChannel(credentials: "{}");

        var errors = await QueryErrorsAsync(DetectMutation, Input(channel.Id));

        // channel_endpoint_detect.go:89-92: the fallback rebuild fails with the
        // same credential error, wrapped by the caller with the channel name.
        Assert.Equal(
            "failed to build channel Alpha: missing api key for channel Alpha",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task DetectChannelEndpointsReusesTheOllamaAnthropicPrimaryForMessages()
    {
        var channel = SeedChannel(
            type: ChannelOutboundFactory.OllamaAnthropicChannelType,
            baseUrl: "http://localhost:11434",
            credentials: """{"apiKey":"ollama-key"}""");
        _executor.OnDo = _ => Ok(200);

        var data = await QueryDataAsync(DetectMutation, Input(channel.Id));

        Assert.Equal(3, Endpoints(data).Length);

        var requests = _executor.Requests;

        // The primary anthropic outbound keeps Ollama's Bearer scheme.
        var messages = RequestFor(requests, "/v1/messages");
        Assert.Equal("http://localhost:11434/v1/messages", messages.Url);
        Assert.Equal(AuthTypes.Bearer, messages.Auth!.Type);
        Assert.Equal("ollama-key", messages.Auth.ApiKey);

        // Chat and responses are generic for a channel with no such defaults.
        Assert.Equal(
            "http://localhost:11434/v1/chat/completions",
            RequestFor(requests, "/v1/chat/completions").Url);
        Assert.Equal(
            "http://localhost:11434/v1/responses",
            RequestFor(requests, "/v1/responses").Url);
    }

    [Fact]
    public async Task DetectChannelEndpointsProbesAnAnthropicOnlyTypeGenerically()
    {
        // opencode_go_anthropic has no default endpoint list, so even the
        // messages probe is built generically instead of reusing the primary.
        var channel = SeedChannel(
            type: ChannelOutboundFactory.OpenCodeGoAnthropicChannelType,
            baseUrl: "https://opencode.ai/zen/go");
        _executor.OnDo = _ => Ok(200);

        var data = await QueryDataAsync(DetectMutation, Input(channel.Id));

        Assert.Equal(3, Endpoints(data).Length);

        var requests = _executor.Requests;
        Assert.Equal(
            "https://opencode.ai/zen/go/v1/chat/completions",
            RequestFor(requests, "/v1/chat/completions").Url);
        Assert.Equal(
            "https://opencode.ai/zen/go/v1/responses",
            RequestFor(requests, "/v1/responses").Url);

        var messages = RequestFor(requests, "/v1/messages");
        Assert.Equal("https://opencode.ai/zen/go/v1/messages", messages.Url);
        Assert.Equal(AuthTypes.ApiKey, messages.Auth!.Type);
        Assert.Equal("X-API-Key", messages.Auth.HeaderKey);
    }

    [Fact]
    public async Task DetectChannelEndpointsKeepsTheCommandCodePlatformForAGenericMessagesProbe()
    {
        // channel_llm.go:521-541: a chat-format Command Code channel's generic
        // messages outbound is the Anthropic Command Code platform (Bearer),
        // not the plain Anthropic Direct outbound.
        var channel = SeedChannel(
            type: ChannelOutboundFactory.CommandcodeChannelType,
            baseUrl: "https://api.commandcode.test");
        _executor.OnDo = _ => Ok(200);

        await QueryDataAsync(DetectMutation, Input(channel.Id));

        var messages = RequestFor(_executor.Requests, "/v1/messages");
        Assert.Equal("https://api.commandcode.test/v1/messages", messages.Url);
        Assert.Equal(AuthTypes.Bearer, messages.Auth!.Type);
        Assert.Equal("sk-test", messages.Auth.ApiKey);
    }

    [Fact]
    public async Task DetectChannelEndpointsRefusesHttpsDowngradeRedirectsForCommandCodeChannels()
    {
        // channel_llm.go:708-713 hands Command Code channels a client that
        // refuses HTTPS-to-HTTP redirects; detection probes with ch.HTTPClient,
        // so the cleartext hop must never be sent.
        var handler = new HttpsDowngradeRedirectHandler();
        var executor = new HttpExecutor(handler);
        var service = new ChannelEndpointDetectService(
            _channels,
            executor,
            TimeProvider.System,
            channelProxyExecutors: new ChannelProxyExecutors(executor, _proxyFactory));
        var channel = SeedChannel(
            type: ChannelOutboundFactory.CommandcodeChannelType,
            baseUrl: "https://api.commandcode.test");

        var result = await service.DetectAsync(new DetectChannelEndpointsCommand(channel.Id, null));

        // A refused redirect is a transport failure to the detector: every
        // format stays unreachable with no status code.
        Assert.All(result.Endpoints, endpoint =>
        {
            Assert.False(endpoint.Supported);
            Assert.Equal(0, endpoint.StatusCode);
            Assert.Equal(ChannelEndpointDetectService.ReasonUnreachable, endpoint.Reason);
        });

        // One HTTPS request per probe; the HTTP hop is never attempted.
        Assert.Equal(3, handler.Requests.Length);
        Assert.All(handler.Requests, uri => Assert.Equal(Uri.UriSchemeHttps, uri.Scheme));
    }

    [Fact]
    public async Task DetectChannelEndpointsProbesATypeSafeChannelGenerically()
    {
        var channel = SeedChannel(
            type: ChannelOutboundFactory.TypeSafeChannelType,
            baseUrl: "https://api.typesafe.test",
            credentials: """{"apiKey":"typesafe-key"}""");
        _executor.OnDo = _ => Ok(200);

        var data = await QueryDataAsync(DetectMutation, Input(channel.Id));

        Assert.Equal(3, Endpoints(data).Length);

        var requests = _executor.Requests;
        Assert.Equal(3, requests.Length);
        Assert.Equal(
            "https://api.typesafe.test/v1/chat/completions",
            RequestFor(requests, "/v1/chat/completions").Url);
        Assert.Equal(
            "https://api.typesafe.test/v1/responses",
            RequestFor(requests, "/v1/responses").Url);
        Assert.Equal(
            "https://api.typesafe.test/v1/messages",
            RequestFor(requests, "/v1/messages").Url);
    }

    [Fact]
    public async Task DetectChannelEndpointsKeepsXaiChatPrimaryAndBuildsGenericResponses()
    {
        var channel = SeedChannel(
            type: ChannelOutboundFactory.XaiChannelType,
            baseUrl: "https://api.x.ai");
        _executor.OnDo = _ => Ok(200);

        await QueryDataAsync(DetectMutation, Input(channel.Id));

        var requests = _executor.Requests;

        // The primary xai transformer keeps the chat route; responses is the
        // reference's generic non-default outbound, not the xai chat one.
        Assert.Equal("https://api.x.ai/v1/chat/completions", RequestFor(requests, "/v1/chat/completions").Url);
        Assert.Equal("https://api.x.ai/v1/responses", RequestFor(requests, "/v1/responses").Url);
    }

    [Fact]
    public async Task DetectChannelEndpointsUsesTheFamilyChatOutboundForVersionedAnthropicTypes()
    {
        var channel = SeedChannel(
            type: ChannelOutboundFactory.ZaiAnthropicChannelType,
            baseUrl: "https://api.z.ai/api/paas/v4");
        _executor.OnDo = _ => Ok(200);

        await QueryDataAsync(DetectMutation, Input(channel.Id));

        var requests = _executor.Requests;

        // providerChatEndpointUsesFamilyVersion (channel_llm.go:610-628): the
        // /v4 base URL routes the chat probe through the Zai family outbound;
        // the generic OpenAI transformer would append /v1 instead.
        Assert.Equal(
            "https://api.z.ai/api/paas/v4/chat/completions",
            RequestFor(requests, "/chat/completions").Url);
    }

    [Fact]
    public async Task DetectChannelEndpointsKeepsTheFixedOrderWhenProbesFinishOutOfOrder()
    {
        var channel = SeedChannel();
        _executor.OnDo = request =>
        {
            // The chat probe is slowest, responses fastest: the payload order
            // must still follow the frozen detectable list.
            switch (PathOf(request))
            {
                case "/v1/chat/completions":
                    Thread.Sleep(150);
                    break;
                case "/v1/messages":
                    Thread.Sleep(60);
                    break;
            }

            return Ok(200);
        };

        var data = await QueryDataAsync(DetectMutation, Input(channel.Id));

        var endpoints = Endpoints(data);
        Assert.Equal(ApiFormats.OpenAIChatCompletion, endpoints[0].GetProperty("apiFormat").GetString());
        Assert.Equal(ApiFormats.OpenAIResponse, endpoints[1].GetProperty("apiFormat").GetString());
        Assert.Equal(ApiFormats.AnthropicMessage, endpoints[2].GetProperty("apiFormat").GetString());
    }

    [Fact]
    public async Task DetectChannelEndpointsOnAKeylessChannelDoesNotPanic()
    {
        // The reference's generic builders call getAPIKeyProvider, which panics
        // when a channel has no enabled keys (channel_llm.go:183). The port
        // builds empty-key transformers instead and lets the probe run (C150).
        var channel = SeedChannel(
            type: ChannelOutboundFactory.OllamaChannelType,
            baseUrl: "http://localhost:11434",
            credentials: "{}");
        _executor.OnDo = _ => Ok(200);

        var data = await QueryDataAsync(DetectMutation, Input(channel.Id));

        Assert.Equal(3, Endpoints(data).Length);
        Assert.Equal(3, _executor.Requests.Length);
    }

    [Fact]
    public async Task IntrospectionExposesTheFrozenDetectionTypes()
    {
        var data = await QueryDataAsync(
            """
            query {
              input: __type(name: "DetectChannelEndpointsInput") {
                inputFields { name type { kind name ofType { kind name } } }
              }
              detected: __type(name: "DetectedChannelEndpoint") {
                fields { name type { kind name ofType { kind name } } }
              }
              payload: __type(name: "DetectChannelEndpointsPayload") {
                fields { name type { kind name ofType { kind name } } }
              }
            }
            """,
            null);

        var inputFields = data.GetProperty("input").GetProperty("inputFields").EnumerateArray().ToArray();
        Assert.Equal(
            InputFieldNames,
            inputFields.Select(field => field.GetProperty("name").GetString()).ToArray());
        Assert.Equal("NON_NULL", inputFields[0].GetProperty("type").GetProperty("kind").GetString());
        Assert.Equal("ID", inputFields[0].GetProperty("type").GetProperty("ofType").GetProperty("name").GetString());
        Assert.Equal("SCALAR", inputFields[1].GetProperty("type").GetProperty("kind").GetString());

        var detected = data.GetProperty("detected").GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(
            DetectedFieldNames,
            detected.Select(field => field.GetProperty("name").GetString()).ToArray());

        // statusCode is Int (nullable) exactly like the frozen SDL; the Go
        // struct still emits its zero value instead of null.
        var statusCode = detected.Single(field => field.GetProperty("name").GetString() == "statusCode");
        Assert.Equal("SCALAR", statusCode.GetProperty("type").GetProperty("kind").GetString());
        Assert.Equal("Int", statusCode.GetProperty("type").GetProperty("name").GetString());

        var payload = data.GetProperty("payload").GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal("endpoints", Assert.Single(payload).GetProperty("name").GetString());
    }

    [Fact]
    public void DetectionConstantsMatchTheFrozenDetector()
    {
        Assert.Equal(
            DetectableFormats,
            ChannelEndpointDetectService.DetectableApiFormats);
        Assert.Equal(TimeSpan.FromSeconds(20), ChannelEndpointDetectService.ProbeTimeout);
    }

    private ChannelRecord SeedChannel(
        string type = ChannelOutboundFactory.OpenAiChannelType,
        string baseUrl = "http://upstream.test",
        string credentials = """{"apiKey":"sk-test"}""",
        string supportedModels = """["gpt-4o"]""",
        string defaultTestModel = "",
        string name = "Alpha",
        string? settings = null)
    {
        var record = new ChannelRecord
        {
            Type = type,
            BaseUrl = baseUrl,
            Name = name,
            Credentials = credentials,
            SupportedModels = supportedModels,
            DefaultTestModel = defaultTestModel,
            Settings = settings,
        };

        _channels.Seed(record);
        return record;
    }

    private static object Input(long channelId)
        => new { input = new { channelID = Gid(channelId) } };

    private static string Gid(long id) => "gid://axonhub/Channel/" + id;

    private static string PathOf(HttpRequestData request)
        => new Uri(request.Url!).AbsolutePath;

    private static HttpRequestData RequestFor(
        IReadOnlyList<HttpRequestData> requests,
        string pathSuffix)
        => Assert.Single(
            requests,
            request => PathOf(request).EndsWith(pathSuffix, StringComparison.Ordinal));

    private static string? BodyModel(HttpRequestData request)
    {
        Assert.NotNull(request.Body);
        using var document = JsonDocument.Parse(request.Body!);
        return document.RootElement.GetProperty("model").GetString();
    }

    private static HttpResponseData Ok(int statusCode) => new()
    {
        StatusCode = statusCode,
        Headers = new Dictionary<string, List<string>>(StringComparer.Ordinal),
        Body = [],
    };

    private static JsonElement[] Endpoints(JsonElement data)
        => data.GetProperty("detectChannelEndpoints")
            .GetProperty("endpoints")
            .EnumerateArray()
            .Select(static endpoint => endpoint.Clone())
            .ToArray();

    private static void AssertEndpoint(
        JsonElement endpoint,
        string apiFormat,
        bool supported,
        int statusCode,
        string reason)
    {
        Assert.Equal(apiFormat, endpoint.GetProperty("apiFormat").GetString());
        Assert.Equal(supported, endpoint.GetProperty("supported").GetBoolean());
        Assert.Equal(statusCode, endpoint.GetProperty("statusCode").GetInt32());
        Assert.Equal(reason, endpoint.GetProperty("reason").GetString());
    }

    private async Task<JsonElement> QueryDataAsync(string query, object? variables)
    {
        var root = await ExecuteAsync(query, variables);
        Assert.False(root.TryGetProperty("errors", out var errors), errors.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(string query, object? variables)
    {
        var root = await ExecuteAsync(query, variables);
        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        return errors.EnumerateArray().Select(static error => error.Clone()).ToArray();
    }

    private async Task<JsonElement> ExecuteAsync(string query, object? variables)
    {
        var payload = JsonSerializer.Serialize(new { query, variables });
        using var response = await _client.PostAsync(
            "/admin/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        AdminGraphqlHttpResponseAssertions.AssertStatus(response, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Thread-safe probe transport: the three detection probes run concurrently,
    /// so request recording and the scripted handler must tolerate overlap.
    /// </summary>
    private sealed class DetectExecutor : IExecutor
    {
        private readonly object _gate = new();
        private readonly List<HttpRequestData> _requests = [];

        public Func<HttpRequestData, HttpResponseData> OnDo { get; set; } = _ => Ok(200);

        public HttpRequestData[] Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
        {
            lock (_gate)
            {
                _requests.Add(request);
            }

            return OnDo(request);
        }

        public IStream<StreamEvent> DoStream(CancellationToken cancellationToken, HttpRequestData request)
            => throw new NotSupportedException("endpoint detection never streams");
    }

    /// <summary>
    /// Answers every request with an HTTPS-to-HTTP redirect, so the redirect
    /// policy installed by the detector decides whether the hop is sent.
    /// </summary>
    private sealed class HttpsDowngradeRedirectHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<Uri> _requests = [];

        public Uri[] Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _requests.Add(request.RequestUri!);
            }

            var response = new HttpResponseMessage(HttpStatusCode.Found)
            {
                Content = new StringContent(string.Empty),
            };
            response.Headers.Location = new Uri("http://api.commandcode.test/redirected");
            return Task.FromResult(response);
        }
    }
}
