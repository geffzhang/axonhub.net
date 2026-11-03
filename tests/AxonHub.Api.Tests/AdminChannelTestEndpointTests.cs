using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.Auth;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Llm;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Transformer;
using AxonHub.Llm.Transformer.OpenAI;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Orleans;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the three channel tester mutations against the reference resolvers
/// (axonhub.resolvers.go:305-372): the GraphQL payload shapes, the gid
/// round-trip and the resolver error prefixes (including the dedicated 404
/// mapping).
/// </summary>
[Collection(OrleansCollection.Name)]
public sealed class AdminChannelTestEndpointTests(GrainClusterFixture fixture) : IAsyncLifetime
{
    private const string TestChannelMutation =
        """
        mutation TestChannel($input: TestChannelInput!) {
          testChannel(input: $input) {
            latency success message error
          }
        }
        """;

    private const string TestChannelApiKeysMutation =
        """
        mutation TestChannelApiKeys($channelID: ID!, $modelID: String) {
          testChannelAPIKeys(channelID: $channelID, modelID: $modelID) {
            channelID total successCount failedCount
            results { keyPrefix success latency error disabled }
          }
        }
        """;

    private const string TestChannelApiKeyMutation =
        """
        mutation TestChannelApiKey($channelID: ID!, $key: String!, $modelID: String) {
          testChannelAPIKey(channelID: $channelID, key: $key, modelID: $modelID) {
            keyPrefix success latency error disabled
          }
        }
        """;

    private const string IntrospectionQuery =
        """
        query {
          input: __type(name: "TestChannelInput") {
            kind name
            inputFields { name type { kind name ofType { kind name } } }
          }
          payload: __type(name: "TestChannelPayload") {
            kind name
            fields { name type { kind name ofType { kind name } } }
          }
          keyResult: __type(name: "TestAPIKeyResult") {
            kind name
            fields { name type { kind name ofType { kind name } } }
          }
          keysPayload: __type(name: "TestChannelAPIKeysPayload") {
            kind name
            fields {
              name
              type { kind name ofType { kind name ofType { kind name ofType { kind name } } } }
            }
          }
        }
        """;

    private readonly FakeChannelWriteStore _channels = new();
    private readonly FakeSystemStore _systems = new();
    private readonly ScriptedExecutor _executor = new();
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
        builder.Services.AddSingleton<ISystemStore>(_systems);
        builder.Services.AddSingleton<IExecutor>(_executor);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IGrainFactory>(fixture.Cluster.GrainFactory);
        builder.Services.AddSingleton<IInboundTransformer>(new OpenAiInboundTransformer());
        builder.Services.AddSingleton<IChannelMetricsRecorder>(new RecordingMetricsRecorder());
        builder.Services.AddSingleton<IRetryPolicyProvider>(
            new StaticRetryPolicyProvider(new RetryPolicy
            {
                MaxChannelRetries = 0,
                MaxSingleChannelRetries = 0,
                RetryDelayMs = 0,
            }));
        builder.Services.AddScoped<IChannelTestPromptsProvider, SystemChannelSettingsService>();
        builder.Services.AddScoped<ChatCompletionOrchestrator>();
        builder.Services.AddScoped<ChannelTestService>();

        // The fixture's own systems store wins over the gate's default, so the
        // JWT secret must live there and the token must be signed against it.
        _systems.Add(ManagementAuthService.SecretKeyKey, AdminGraphqlGateServices.Secret);
        var jwt = AdminGraphqlGateServices.IssueJwtWith(_gate.Users, _systems, _gate.Owner);

        _app = builder.Build();
        _app.MapAdminGraphql();
        await _app.StartAsync();
        _client = _app.GetTestClient();
        AdminGraphqlGateServices.Authorize(_client, jwt);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task TestChannelReturnsTheAssistantMessage()
    {
        SeedPrompts("system prompt", "user prompt");
        var channel = SeedChannel();
        _executor.OnDo = (_, _, _) => TestChannels.Completion("test-model", "hello");

        var data = await QueryDataAsync(
            TestChannelMutation,
            new { input = new { channelID = Gid(channel.Id), modelID = (string?)null } });

        var payload = data.GetProperty("testChannel");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal("hello", payload.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("error").ValueKind);
        Assert.True(payload.GetProperty("latency").GetDouble() >= 0);

        var request = Assert.Single(_executor.Requests);
        Assert.Equal("http://upstream.test/v1/chat/completions", request.Url);
        Assert.Equal(["Bearer test-key-1"], request.Headers!["Authorization"]);

        using var body = JsonDocument.Parse(request.Body!);
        var messages = body.RootElement.GetProperty("messages");
        Assert.Equal("system prompt", messages[0].GetProperty("content").GetString());
        Assert.Equal("user prompt", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task TestChannelForwardsTheModelOverride()
    {
        SeedPrompts();
        var channel = SeedChannel(supportedModels: """["test-model","other-model"]""");
        _executor.OnDo = (_, _, _) => TestChannels.Completion("other-model");

        await QueryDataAsync(
            TestChannelMutation,
            new { input = new { channelID = Gid(channel.Id), modelID = "other-model" } });

        using var body = JsonDocument.Parse(Assert.Single(_executor.Requests).Body!);
        Assert.Equal("other-model", body.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task TestChannelMapsA404ToTheDedicatedMessage()
    {
        _channels.GetFailure = TestChannels.HttpClient(404);

        var errors = await QueryErrorsAsync(
            TestChannelMutation,
            new { input = new { channelID = Gid(1), modelID = (string?)null } });

        Assert.Equal(
            "URL not found, please check if the URL is correct and try again",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task TestChannelWrapsEveryOtherFailure()
    {
        var errors = await QueryErrorsAsync(
            TestChannelMutation,
            new { input = new { channelID = Gid(424242), modelID = (string?)null } });

        Assert.Equal(
            "failed to test channel: channel not found: ent: channel not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task TestChannelAPIKeysReturnsEveryResultInOrder()
    {
        SeedPrompts();
        var channel = SeedChannel(
            credentials: """{"apiKeys":["test-key-1","test-key-2"]}""",
            disabledApiKeys: """[{"key":"test-key-2","disabledAt":"2026-01-01T00:00:00Z","errorCode":401,"reason":"auth"}]""");
        _executor.OnDo = (_, _, _) => TestChannels.Completion("test-model");

        var data = await QueryDataAsync(
            TestChannelApiKeysMutation,
            new { channelID = Gid(channel.Id), modelID = (string?)null });

        var payload = data.GetProperty("testChannelAPIKeys");
        Assert.Equal(Gid(channel.Id), payload.GetProperty("channelID").GetString());
        Assert.Equal(2, payload.GetProperty("total").GetInt32());
        Assert.Equal(2, payload.GetProperty("successCount").GetInt32());
        Assert.Equal(0, payload.GetProperty("failedCount").GetInt32());

        var results = payload.GetProperty("results").EnumerateArray().ToArray();
        Assert.Equal(2, results.Length);

        Assert.Equal("test****ey-1", results[0].GetProperty("keyPrefix").GetString());
        Assert.True(results[0].GetProperty("success").GetBoolean());
        Assert.False(results[0].GetProperty("disabled").GetBoolean());

        Assert.Equal("test****ey-2", results[1].GetProperty("keyPrefix").GetString());
        Assert.True(results[1].GetProperty("success").GetBoolean());
        Assert.True(results[1].GetProperty("disabled").GetBoolean());

        Assert.Equal(2, _executor.Calls);
    }

    [Fact]
    public async Task TestChannelAPIKeysWrapsItsFailures()
    {
        var channel = SeedChannel(credentials: "{}");

        var errors = await QueryErrorsAsync(
            TestChannelApiKeysMutation,
            new { channelID = Gid(channel.Id), modelID = (string?)null });

        Assert.Equal(
            "failed to test channel API keys: no API keys configured for channel",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task TestChannelAPIKeyReturnsOneResult()
    {
        SeedPrompts();
        var channel = SeedChannel();
        _executor.OnDo = (_, _, _) => TestChannels.Completion("test-model");

        var data = await QueryDataAsync(
            TestChannelApiKeyMutation,
            new { channelID = Gid(channel.Id), key = "test-key-1", modelID = (string?)null });

        var payload = data.GetProperty("testChannelAPIKey");
        Assert.Equal("test****ey-1", payload.GetProperty("keyPrefix").GetString());
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("error").ValueKind);
        Assert.False(payload.GetProperty("disabled").GetBoolean());

        Assert.Equal(
            ["Bearer test-key-1"],
            Assert.Single(_executor.Requests).Headers!["Authorization"]);
    }

    [Fact]
    public async Task TestChannelAPIKeyRejectsAKeyTheChannelDoesNotOwn()
    {
        var channel = SeedChannel();

        var errors = await QueryErrorsAsync(
            TestChannelApiKeyMutation,
            new { channelID = Gid(channel.Id), key = "other-key", modelID = (string?)null });

        Assert.Equal(
            "failed to test channel API key: the provided API key is not configured for this channel",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task TheTesterSchemaMatchesTheReferenceTypes()
    {
        var data = await QueryDataAsync(IntrospectionQuery, variables: null);

        var input = data.GetProperty("input");
        Assert.Equal("INPUT_OBJECT", input.GetProperty("kind").GetString());
        Assert.Equal("TestChannelInput", input.GetProperty("name").GetString());
        Assert.Equal(
            ["channelID", "modelID", "proxy"],
            input.GetProperty("inputFields").EnumerateArray()
                .Select(field => field.GetProperty("name").GetString()!)
                .ToArray());

        var channelId = InputField(input, "channelID").GetProperty("type");
        Assert.Equal("NON_NULL", channelId.GetProperty("kind").GetString());
        Assert.Equal("ID", channelId.GetProperty("ofType").GetProperty("name").GetString());

        var payload = data.GetProperty("payload");
        Assert.Equal("TestChannelPayload", payload.GetProperty("name").GetString());
        Assert.Equal(
            ["latency", "success", "message", "error"],
            payload.GetProperty("fields").EnumerateArray()
                .Select(field => field.GetProperty("name").GetString()!)
                .ToArray());

        var success = Field(payload, "success").GetProperty("type");
        Assert.Equal("NON_NULL", success.GetProperty("kind").GetString());
        Assert.Equal("Boolean", success.GetProperty("ofType").GetProperty("name").GetString());

        var latency = Field(payload, "latency").GetProperty("type");
        Assert.Equal("NON_NULL", latency.GetProperty("kind").GetString());
        Assert.Equal("Float", latency.GetProperty("ofType").GetProperty("name").GetString());

        var keyResult = data.GetProperty("keyResult");
        Assert.Equal("TestAPIKeyResult", keyResult.GetProperty("name").GetString());
        Assert.Equal(
            ["keyPrefix", "success", "latency", "error", "disabled"],
            keyResult.GetProperty("fields").EnumerateArray()
                .Select(field => field.GetProperty("name").GetString()!)
                .ToArray());

        var keysPayload = data.GetProperty("keysPayload");
        Assert.Equal("TestChannelAPIKeysPayload", keysPayload.GetProperty("name").GetString());
        Assert.Equal(
            ["channelID", "total", "successCount", "failedCount", "results"],
            keysPayload.GetProperty("fields").EnumerateArray()
                .Select(field => field.GetProperty("name").GetString()!)
                .ToArray());

        var results = Field(keysPayload, "results").GetProperty("type");
        Assert.Equal("NON_NULL", results.GetProperty("kind").GetString());
        Assert.Equal("LIST", results.GetProperty("ofType").GetProperty("kind").GetString());
        Assert.Equal(
            "NON_NULL",
            results.GetProperty("ofType").GetProperty("ofType").GetProperty("kind").GetString());
        Assert.Equal(
            "TestAPIKeyResult",
            results.GetProperty("ofType").GetProperty("ofType").GetProperty("ofType")
                .GetProperty("name").GetString());
    }

    // -- Helpers ------------------------------------------------------------

    private void SeedPrompts(
        string systemPrompt = "system prompt",
        string userPrompt = "user prompt")
        => _systems.Add(
            SystemChannelSettingsService.ChannelSettingsKey,
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["test_system_prompt"] = systemPrompt,
                ["test_user_prompt"] = userPrompt,
            }));

    private ChannelRecord SeedChannel(
        string type = "openai",
        string baseUrl = "http://upstream.test/v1",
        string credentials = """{"apiKey":"test-key-1"}""",
        string supportedModels = """["test-model"]""",
        string defaultTestModel = "test-model",
        string? disabledApiKeys = null)
    {
        var record = new ChannelRecord
        {
            Type = type,
            BaseUrl = baseUrl,
            Name = "Alpha",
            Status = "enabled",
            Credentials = credentials,
            SupportedModels = supportedModels,
            DefaultTestModel = defaultTestModel,
            DisabledApiKeys = disabledApiKeys,
        };

        _channels.Seed(record);
        return record;
    }

    private static JsonElement InputField(JsonElement type, string name)
        => type.GetProperty("inputFields").EnumerateArray()
            .Single(field => field.GetProperty("name").GetString() == name);

    private static JsonElement Field(JsonElement type, string name)
        => type.GetProperty("fields").EnumerateArray()
            .Single(field => field.GetProperty("name").GetString() == name);

    private static string Gid(long id) => "gid://axonhub/Channel/" + id;

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
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
