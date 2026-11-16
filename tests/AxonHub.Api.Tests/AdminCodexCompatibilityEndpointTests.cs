using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Abstractions;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Settings;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Llm.Streams;
using AxonHub.Llm.Transformer.OpenAI.Codex;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the Codex compatibility surface over /admin/graphql
/// (system.graphql:409-440, system.resolvers.go:26-56 and 470-497): the exact
/// settings shape, the update round trip, the validation envelope, the catalog
/// channel list, the catalog test result folding and the read_settings /
/// write_settings / read_channels guards. The feature is post-baseline drift
/// followed at f272549f (§160).
/// </summary>
public sealed class AdminCodexCompatibilityEndpointTests : IAsyncLifetime
{
    private const string SettingsQuery = """
        query { codexCompatibilitySettings { enabled channelID } }
        """;

    private const string EnabledQuery = "query { codexCompatibilityEnabled }";

    private const string CatalogChannelsQuery = """
        query { codexCatalogChannels { id name status } }
        """;

    private const string UpdateMutation = """
        mutation {
          updateCodexCompatibilitySettings(input: { enabled: true, channelID: 7 })
        }
        """;

    private const string TestCatalogMutation = """
        mutation { testCodexCatalog(channelID: 7) { success modelCount upstreamStatus error } }
        """;

    private readonly FakeSystemStore _store = new();
    private readonly FakeChannelStore _channels = new();
    private readonly FakeChannelAdminStore _adminChannels = new();
    private readonly FakeEnabledChannelProvider _enabled = new();
    private readonly ScriptedCatalogExecutor _executor = new();
    private readonly FakeUserAdminStore _adminUsers = new();
    private readonly AdminGraphqlGateServices _gate = new(isOwner: false);

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // The gate user (id 9) is a non-owner carrying every scope these fields
        // check; individual tests narrow the list.
        await _adminUsers.CreateAsync(
            new UserRecord
            {
                Id = AdminGraphqlGateServices.UserId,
                Email = "gate-user@example.com",
                Status = "activated",
                Scopes = """["read_settings","write_settings","read_channels"]""",
            },
            roleIds: null);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IUserAdminStore>(_adminUsers);
        builder.Services.AddScoped<AuthorizationScope>();
        builder.Services.AddSingleton<IChannelStore>(_channels);
        builder.Services.AddSingleton<IChannelAdminStore>(_adminChannels);
        builder.Services.AddSingleton<IEnabledChannelProvider>(_enabled);
        builder.Services.AddSingleton<IExecutor>(_executor);
        builder.Services.AddSingleton<IProxyExecutorFactory>(new HttpProxyExecutorFactory());
        builder.Services.AddScoped(_ => new CodexCompatibilitySettingsService(
            _store,
            _channels,
            _.GetRequiredService<AuthorizationScope>()));
        builder.Services.AddScoped<ICodexCompatibilitySettingsService>(
            static services => services.GetRequiredService<CodexCompatibilitySettingsService>());
        builder.Services.AddSingleton<ICodexCatalogService>(static services => new CodexCatalogService(
            services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IExecutor>(),
            services.GetRequiredService<IProxyExecutorFactory>(),
            grainFactory: null!,
            timeProvider: TimeProvider.System));

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
    public async Task SettingsSurfaceHasTheExactShape()
    {
        var response = await PostAsync(SettingsQuery);

        Assert.Equal(
            """{"codexCompatibilitySettings":{"enabled":false,"channelID":null}}""",
            response.GetProperty("data").GetRawText());

        Assert.False((await QueryAsync(EnabledQuery)).GetProperty("codexCompatibilityEnabled").GetBoolean());
    }

    [Fact]
    public async Task EnabledNeedsNoScope()
    {
        (await _adminUsers.GetByIdAsync(AdminGraphqlGateServices.UserId))!.Scopes = "[]";

        Assert.False((await QueryAsync(EnabledQuery)).GetProperty("codexCompatibilityEnabled").GetBoolean());
    }

    [Fact]
    public async Task UpdateThenReadRoundTrips()
    {
        AddCodexChannel();

        Assert.True((await QueryAsync(UpdateMutation))
            .GetProperty("updateCodexCompatibilitySettings")
            .GetBoolean());

        var row = Assert.Single(_store.Rows);
        Assert.Equal(CodexCompatibilitySettingsService.SystemKey, row.Key);
        Assert.Equal("""{"enabled":true,"channelID":7}""", row.Value);

        var settings = (await QueryAsync(SettingsQuery)).GetProperty("codexCompatibilitySettings");
        Assert.True(settings.GetProperty("enabled").GetBoolean());
        Assert.Equal(7, settings.GetProperty("channelID").GetInt32());
        Assert.True((await QueryAsync(EnabledQuery))
            .GetProperty("codexCompatibilityEnabled")
            .GetBoolean());
    }

    [Fact]
    public async Task UpdateValidationUsesTheCodedEnvelope()
    {
        var response = await PostAsync(
            """
            mutation {
              updateCodexCompatibilitySettings(input: { enabled: true })
            }
            """);

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "a Codex channel is required when enabled",
            error.GetProperty("message").GetString());
        Assert.Equal(
            CodedException.ValidationFailed,
            error.GetProperty("extensions").GetProperty("code").GetString());
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task UpdateRequiresTheWriteSettingsScope()
    {
        (await _adminUsers.GetByIdAsync(AdminGraphqlGateServices.UserId))!.Scopes =
            """["read_settings","read_channels"]""";

        var response = await PostAsync(UpdateMutation);

        Assert.Contains(
            "does not have required scope write_settings",
            Assert.Single(response.GetProperty("errors").EnumerateArray())
                .GetProperty("message")
                .GetString());
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task CatalogChannelsReturnEveryCodexRowIncludingArchived()
    {
        _adminChannels.Seed(Channel(1, "codex-main", "codex", "enabled"));
        _adminChannels.Seed(Channel(2, "openai-main", "openai", "enabled"));
        _adminChannels.Seed(Channel(3, "codex-old", "codex", "archived"));

        var data = await QueryAsync(CatalogChannelsQuery);

        var rows = data.GetProperty("codexCatalogChannels");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal(1, rows[0].GetProperty("id").GetInt32());
        Assert.Equal("codex-main", rows[0].GetProperty("name").GetString());
        Assert.Equal("enabled", rows[0].GetProperty("status").GetString());
        Assert.Equal(3, rows[1].GetProperty("id").GetInt32());
        Assert.Equal("codex-old", rows[1].GetProperty("name").GetString());
        Assert.Equal("archived", rows[1].GetProperty("status").GetString());

        var query = Assert.Single(_adminChannels.AllQueries);
        Assert.Equal("codex", query.Where?.Type);
    }

    [Fact]
    public async Task CatalogChannelsRequireBothScopes()
    {
        (await _adminUsers.GetByIdAsync(AdminGraphqlGateServices.UserId))!.Scopes =
            """["read_channels"]""";

        var missingSettings = await PostAsync(CatalogChannelsQuery);
        Assert.Contains(
            "does not have required scope read_settings",
            Assert.Single(missingSettings.GetProperty("errors").EnumerateArray())
                .GetProperty("message")
                .GetString());

        (await _adminUsers.GetByIdAsync(AdminGraphqlGateServices.UserId))!.Scopes =
            """["read_settings"]""";

        var missingChannels = await PostAsync(CatalogChannelsQuery);
        Assert.Contains(
            "does not have required scope read_channels",
            Assert.Single(missingChannels.GetProperty("errors").EnumerateArray())
                .GetProperty("message")
                .GetString());
    }

    [Fact]
    public async Task TestCatalogReportsTheUpstreamStatusWithoutLeakingTheBody()
    {
        AddCodexChannel();
        _executor.Respond = static _ => new HttpResponseData
        {
            StatusCode = 404,
            Body = Encoding.UTF8.GetBytes("""{"error":"private-secret"}"""),
        };

        var body = await PostRawAsync(TestCatalogMutation);

        using var document = JsonDocument.Parse(body);
        var result = document.RootElement.GetProperty("data").GetProperty("testCodexCatalog");
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(0, result.GetProperty("modelCount").GetInt32());
        Assert.Equal(404, result.GetProperty("upstreamStatus").GetInt32());
        Assert.Equal(
            "Codex catalog upstream HTTP 404",
            result.GetProperty("error").GetString());
        Assert.DoesNotContain("private-secret", body, StringComparison.Ordinal);

        var request = Assert.Single(_executor.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Equal(
            "https://chatgpt.com/backend-api/codex/models?client_version="
            + CodexConstants.DefaultVersion,
            request.Url);
        Assert.Equal("Bearer sk-test", request.Headers!["Authorization"][0]);
        Assert.Equal(CodexConstants.DefaultVersion, request.Headers["Version"][0]);
        Assert.Equal("application/json", request.Headers["Accept"][0]);
    }

    [Fact]
    public async Task TestCatalogCountsTheModelsOnSuccess()
    {
        AddCodexChannel();
        _executor.Respond = static _ => new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes(
                """{"models":[{"slug":"gpt-5-codex"},{"slug":"gpt-5"}]}"""),
        };

        var result = (await QueryAsync(TestCatalogMutation)).GetProperty("testCodexCatalog");

        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(2, result.GetProperty("modelCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("upstreamStatus").ValueKind);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("error").ValueKind);
    }

    [Fact]
    public async Task TestCatalogRejectsAnInvalidCatalogBody()
    {
        AddCodexChannel();
        _executor.Respond = static _ => new HttpResponseData
        {
            StatusCode = 200,
            Body = Encoding.UTF8.GetBytes("""{"models":[{"slug":""}]}"""),
        };

        var result = (await QueryAsync(TestCatalogMutation)).GetProperty("testCodexCatalog");

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("invalid Codex model catalog", result.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("upstreamStatus").ValueKind);
    }

    [Fact]
    public async Task TestCatalogOnANonCodexChannelReportsTheSourceError()
    {
        // The resolver precheck only asks whether the row exists; the catalog
        // service repeats the lookup and rejects a non-codex type with 503.
        _channels.Add(new ChannelRecord
        {
            Id = 7,
            Type = "openai",
            Name = "openai-main",
            Status = "enabled",
        });

        var result = (await QueryAsync(TestCatalogMutation)).GetProperty("testCodexCatalog");

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("Codex catalog source is unavailable", result.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("upstreamStatus").ValueKind);
        Assert.Empty(_executor.Requests);
    }

    [Fact]
    public async Task TestCatalogWithoutAnEnabledKeyReportsTheCredentialError()
    {
        // The reference calls getAPIKeyProvider, which panics when zero keys are
        // enabled and at most one is configured; singleflight re-panics and the
        // process exits (C246). The port folds it into the credential error.
        var record = AddCodexChannel();
        record.DisabledApiKeys = """[{"key":"sk-test"}]""";

        var result = (await QueryAsync(TestCatalogMutation)).GetProperty("testCodexCatalog");

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(
            "Codex catalog credentials are unavailable",
            result.GetProperty("error").GetString());
        Assert.Empty(_executor.Requests);
    }

    [Fact]
    public async Task TestCatalogWithAMismatchedCachedProviderReportsTheProviderError()
    {
        // GetEnabledChannel returns a snapshot whose cached outbound is not a
        // codex transformer; the reference type assertion fails with 502.
        AddCodexChannel();
        _enabled.Set(new ChannelDescriptor
        {
            Id = 7,
            Name = "codex-main",
            OrderingWeight = 0,
            Type = "openai",
            Credentials = """{"apiKey":"sk-test"}""",
        });

        var result = (await QueryAsync(TestCatalogMutation)).GetProperty("testCodexCatalog");

        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("Codex catalog provider is unavailable", result.GetProperty("error").GetString());
        Assert.Empty(_executor.Requests);
    }

    [Fact]
    public async Task TestCatalogPrecheckIsAPlainError()
    {
        var response = await PostAsync(
            "mutation { testCodexCatalog(channelID: 999) { success } }");

        Assert.Equal(
            "catalog channel is unavailable or inaccessible",
            Assert.Single(response.GetProperty("errors").EnumerateArray())
                .GetProperty("message")
                .GetString());
    }

    [Fact]
    public async Task TestCatalogRequiresBothScopes()
    {
        AddCodexChannel();
        (await _adminUsers.GetByIdAsync(AdminGraphqlGateServices.UserId))!.Scopes =
            """["read_channels"]""";

        var missingSettings = await PostAsync(TestCatalogMutation);
        Assert.Contains(
            "does not have required scope write_settings",
            Assert.Single(missingSettings.GetProperty("errors").EnumerateArray())
                .GetProperty("message")
                .GetString());

        (await _adminUsers.GetByIdAsync(AdminGraphqlGateServices.UserId))!.Scopes =
            """["write_settings"]""";

        var missingChannels = await PostAsync(TestCatalogMutation);
        Assert.Contains(
            "does not have required scope read_channels",
            Assert.Single(missingChannels.GetProperty("errors").EnumerateArray())
                .GetProperty("message")
                .GetString());

        Assert.Empty(_executor.Requests);
    }

    private ChannelRecord AddCodexChannel()
    {
        var record = new ChannelRecord
        {
            Id = 7,
            Type = "codex",
            Name = "codex-main",
            Status = "enabled",
            Credentials = """{"apiKey":"sk-test"}""",
        };
        _channels.Add(record);
        return record;
    }

    private static ChannelRecord Channel(
        long id,
        string name,
        string type,
        string status) => new()
        {
            Id = id,
            Name = name,
            Type = type,
            Status = status,
        };

    private async Task<JsonElement> QueryAsync(string query)
    {
        var response = await PostAsync(query);
        Assert.False(response.TryGetProperty("errors", out _), response.ToString());
        return response.GetProperty("data");
    }

    private async Task<JsonElement> PostAsync(string query)
    {
        var body = await PostRawAsync(query);
        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    private async Task<string> PostRawAsync(string query)
    {
        var payload = JsonSerializer.Serialize(new { query });
        using var response = await _client.PostAsync(
            "/admin/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return body;
    }

    private sealed class ScriptedCatalogExecutor : IExecutor
    {
        public List<HttpRequestData> Requests { get; } = [];

        public Func<HttpRequestData, HttpResponseData> Respond { get; set; } =
            static _ => new HttpResponseData { StatusCode = 500 };

        public HttpResponseData Do(CancellationToken cancellationToken, HttpRequestData request)
        {
            Requests.Add(request);
            return Respond(request);
        }

        public IStream<StreamEvent> DoStream(
            CancellationToken cancellationToken,
            HttpRequestData request)
            => throw new NotSupportedException();
    }
}
