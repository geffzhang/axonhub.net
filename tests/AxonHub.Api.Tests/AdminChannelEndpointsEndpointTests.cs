using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the saveChannelEndpoints mutation against the reference resolver and
/// biz.ChannelService.SaveChannelEndpoints (axonhub.resolvers.go:194-197;
/// channel.go:1243-1275): the service reads the live row, validates the
/// endpoints against the channel type and the stored model-protocol overrides,
/// then writes only the endpoints column under the loaded updated_at guard.
/// </summary>
public sealed class AdminChannelEndpointsEndpointTests : IAsyncLifetime
{
    private const string SaveMutation =
        """
        mutation Save($input: SaveChannelEndpointsInput!) {
          saveChannelEndpoints(input: $input) {
            id
            endpoints { apiFormat path baseURL transport }
          }
        }
        """;

    private readonly FakeChannelWriteStore _channels = new();
    private readonly ChannelLimiterManager _limiters = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        builder.Services.AddSingleton<IChannelWriteStore>(_channels);
        builder.Services.AddSingleton(_limiters);
        builder.Services.AddSingleton<ChannelLifecycleService>();

        _app = builder.Build();
        _app.MapGraphQL("/admin/graphql");
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task SaveChannelEndpointsWritesTheJsonAndReturnsTheChannel()
    {
        var channel = SeedChannel(settings: """{"extraModelPrefix":"cfg"}""");

        var data = await QueryDataAsync(
            SaveMutation,
            new
            {
                input = new
                {
                    channelID = Gid(channel.Id),
                    endpoints = new object[]
                    {
                        new
                        {
                            apiFormat = "openai/chat_completions",
                            path = "/v1/chat/completions",
                            baseURL = "https://api.example/v1",
                            transport = "http",
                        },
                        new
                        {
                            apiFormat = "openai/responses",
                            path = "/v1/responses",
                        },
                    },
                },
            });

        var node = data.GetProperty("saveChannelEndpoints");
        Assert.Equal(Gid(channel.Id), node.GetProperty("id").GetString());

        var endpoints = node.GetProperty("endpoints").EnumerateArray().ToArray();
        Assert.Equal(2, endpoints.Length);
        Assert.Equal("openai/chat_completions", endpoints[0].GetProperty("apiFormat").GetString());
        Assert.Equal("/v1/chat/completions", endpoints[0].GetProperty("path").GetString());
        Assert.Equal("https://api.example/v1", endpoints[0].GetProperty("baseURL").GetString());
        Assert.Equal("http", endpoints[0].GetProperty("transport").GetString());
        Assert.Equal("openai/responses", endpoints[1].GetProperty("apiFormat").GetString());
        Assert.Equal("/v1/responses", endpoints[1].GetProperty("path").GetString());

        // Go's omitempty drops the empty transport/base_url members.
        const string expected =
            """[{"api_format":"openai/chat_completions","path":"/v1/chat/completions","base_url":"https://api.example/v1","transport":"http"},{"api_format":"openai/responses","path":"/v1/responses"}]""";
        var call = Assert.Single(_channels.SaveEndpointsCalls);
        Assert.Equal(channel.Id, call.Id);
        Assert.Equal(expected, call.EndpointsJson);
        Assert.Equal(expected, channel.Endpoints);

        // The save touches only the endpoints column.
        Assert.Equal("""{"extraModelPrefix":"cfg"}""", channel.Settings);
        Assert.Equal("Alpha", channel.Name);
    }

    [Fact]
    public async Task SaveChannelEndpointsAcceptsAnEmptyEndpointList()
    {
        var channel = SeedChannel();

        var data = await QueryDataAsync(
            SaveMutation,
            new
            {
                input = new
                {
                    channelID = Gid(channel.Id),
                    endpoints = Array.Empty<object>(),
                },
            });

        Assert.Empty(
            data.GetProperty("saveChannelEndpoints").GetProperty("endpoints").EnumerateArray());
        Assert.Equal("[]", Assert.Single(_channels.SaveEndpointsCalls).EndpointsJson);
        Assert.Equal("[]", channel.Endpoints);
    }

    [Fact]
    public async Task SaveChannelEndpointsRejectsDuplicateFormatsBeforeTheStore()
    {
        var channel = SeedChannel();

        var errors = await QueryErrorsAsync(
            SaveMutation,
            new
            {
                input = new
                {
                    channelID = Gid(channel.Id),
                    endpoints = new object[]
                    {
                        new
                        {
                            apiFormat = "openai/chat_completions",
                            path = "/v1/chat/completions",
                        },
                        new
                        {
                            apiFormat = "openai/chat_completions",
                            path = "/v2/chat/completions",
                        },
                    },
                },
            });

        Assert.Equal(
            "invalid endpoints: endpoint[1]: duplicate api_format \"openai/chat_completions\"",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_channels.SaveEndpointsCalls);
        Assert.Equal("[]", channel.Endpoints);
    }

    [Fact]
    public async Task SaveChannelEndpointsRejectsAFullUrlPath()
    {
        var channel = SeedChannel();

        var errors = await QueryErrorsAsync(
            SaveMutation,
            new
            {
                input = new
                {
                    channelID = Gid(channel.Id),
                    endpoints = new object[]
                    {
                        new
                        {
                            apiFormat = "openai/chat_completions",
                            path = "https://api.example/v1/chat/completions",
                        },
                    },
                },
            });

        Assert.Equal(
            "invalid endpoints: endpoint[0]: path must not be a full URL, "
            + "got \"https://api.example/v1/chat/completions\"",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_channels.SaveEndpointsCalls);
    }

    [Fact]
    public async Task SaveChannelEndpointsRejectsXaiSubscriptionChannels()
    {
        var channel = SeedChannel(type: "xai_subscription");

        var errors = await QueryErrorsAsync(
            SaveMutation,
            new
            {
                input = new
                {
                    channelID = Gid(channel.Id),
                    endpoints = new object[]
                    {
                        new { apiFormat = "openai/responses", path = "/v1/responses" },
                    },
                },
            });

        Assert.Equal(
            "xAI subscription channels do not support custom endpoints",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_channels.SaveEndpointsCalls);
    }

    [Fact]
    public async Task SaveChannelEndpointsValidatesTheStoredModelProtocolOverrides()
    {
        var channel = SeedChannel(
            settings:
                """{"modelProtocols":[{"model":"m","apiFormats":["anthropic/messages"],"enabled":true}]}""");

        var errors = await QueryErrorsAsync(
            SaveMutation,
            new
            {
                input = new
                {
                    channelID = Gid(channel.Id),
                    endpoints = new object[]
                    {
                        new { apiFormat = "openai/chat_completions", path = "/v1/chat/completions" },
                    },
                },
            });

        // The override referenced an api_format the new endpoint list removes.
        Assert.Equal(
            "invalid endpoints for configured model protocols: "
            + "modelProtocols[0] (m): api_format \"anthropic/messages\" "
            + "is not configured on this channel",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_channels.SaveEndpointsCalls);
    }

    [Fact]
    public async Task SaveChannelEndpointsOnAMissingChannelKeepsTheReferenceMessage()
    {
        var errors = await QueryErrorsAsync(
            SaveMutation,
            new
            {
                input = new
                {
                    channelID = Gid(424242),
                    endpoints = new object[]
                    {
                        new { apiFormat = "openai/chat_completions", path = "/v1/chat/completions" },
                    },
                },
            });

        Assert.Equal(
            "failed to get channel: ent: channel not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task SaveChannelEndpointsReportsAConcurrentUpdate()
    {
        var channel = SeedChannel();
        _channels.SaveEndpointsConflict = true;

        var errors = await QueryErrorsAsync(
            SaveMutation,
            new
            {
                input = new
                {
                    channelID = Gid(channel.Id),
                    endpoints = new object[]
                    {
                        new { apiFormat = "openai/chat_completions", path = "/v1/chat/completions" },
                    },
                },
            });

        Assert.Equal(
            "channel was updated concurrently; retry the operation",
            errors[0].GetProperty("message").GetString());
        Assert.Equal("[]", channel.Endpoints);
    }

    [Fact]
    public async Task SaveChannelEndpointsForwardsStoreFailures()
    {
        var channel = SeedChannel();
        _channels.GetFailure = new StoreFailureException(
            "failed to get channel: boom",
            new InvalidOperationException("boom"));
        _channels.SaveEndpointsFailure = new StoreFailureException(
            "failed to update channel endpoints: boom",
            new InvalidOperationException("boom"));

        var readErrors = await QueryErrorsAsync(
            SaveMutation,
            new
            {
                input = new
                {
                    channelID = Gid(channel.Id),
                    endpoints = new object[]
                    {
                        new { apiFormat = "openai/chat_completions", path = "/v1/chat/completions" },
                    },
                },
            });
        Assert.Equal(
            "failed to get channel: boom",
            readErrors[0].GetProperty("message").GetString());

        _channels.GetFailure = null;
        var saveErrors = await QueryErrorsAsync(
            SaveMutation,
            new
            {
                input = new
                {
                    channelID = Gid(channel.Id),
                    endpoints = new object[]
                    {
                        new { apiFormat = "openai/chat_completions", path = "/v1/chat/completions" },
                    },
                },
            });
        Assert.Equal(
            "failed to update channel endpoints: boom",
            saveErrors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task IntrospectionExposesTheSaveChannelEndpointsInputs()
    {
        var data = await QueryDataAsync(
            """
            {
              __schema { mutationType { fields { name } } }
              save: __type(name: "SaveChannelEndpointsInput") { inputFields { name } }
              endpoint: __type(name: "ChannelEndpointInput") { inputFields { name } }
              channel: __type(name: "Channel") {
                fields { name type { kind ofType { kind name ofType { name } } } }
              }
            }
            """);

        var mutations = data.GetProperty("__schema").GetProperty("mutationType")
            .GetProperty("fields").EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Contains("saveChannelEndpoints", mutations);

        Assert.Equal(
            ["channelID", "endpoints"],
            FieldNames(data.GetProperty("save")));
        Assert.Equal(
            ["apiFormat", "path", "baseURL", "transport"],
            FieldNames(data.GetProperty("endpoint")));

        // ent.graphql:446 — endpoints: [ChannelEndpoint!] (nullable list of
        // non-null elements).
        var endpoints = data.GetProperty("channel").GetProperty("fields").EnumerateArray()
            .Single(static field => field.GetProperty("name").GetString() == "endpoints");
        var listType = endpoints.GetProperty("type");
        Assert.Equal("LIST", listType.GetProperty("kind").GetString());
        var elementType = listType.GetProperty("ofType");
        Assert.Equal("NON_NULL", elementType.GetProperty("kind").GetString());
        Assert.Equal(
            "ChannelEndpoint",
            elementType.GetProperty("ofType").GetProperty("name").GetString());
    }

    private static string[] FieldNames(JsonElement type)
        => type.GetProperty("inputFields").EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .ToArray();

    private static string Gid(long id) => "gid://axonhub/Channel/" + id;

    private ChannelRecord SeedChannel(
        string type = "openai",
        string? settings = null)
    {
        var record = new ChannelRecord
        {
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Type = type,
            BaseUrl = "https://api.example/v1",
            Name = "Alpha",
            Status = "disabled",
            Credentials = "{}",
            SupportedModels = "null",
            AutoSyncModelPattern = string.Empty,
            DefaultTestModel = string.Empty,
            Tags = "[]",
            Policies = """{"stream":"unlimited"}""",
            Settings = settings,
            Endpoints = "[]",
        };

        _channels.Seed(record);
        return record;
    }

    private async Task<JsonElement> QueryDataAsync(string query, object? variables = null)
    {
        var root = await ExecuteAsync(query, variables);
        Assert.False(root.TryGetProperty("errors", out _), root.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(string query, object? variables = null)
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
