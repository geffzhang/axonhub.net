using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Safeguards;
using AxonHub.Api.Settings;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the Claude Code safeguards settings surface over /admin/graphql
/// (system.graphql:542-573, system.resolvers.go:445-455 and 804-807): the
/// defaults, the update/reset round trip, the coded validation envelope and the
/// read_settings / write_settings guards. The feature is post-baseline drift
/// followed at f272549f (§159).
/// </summary>
public sealed class AdminClaudeCodeSafeguardsEndpointTests : IAsyncLifetime
{
    private const string SettingsQuery = """
        query {
          claudeCodeSafeguardsSettings {
            usingServerDefaults
            defaultPolicy
            config {
              classifierModel policy fallbackModels escalateOnDenial
              attemptTimeoutSeconds timeoutSeconds maxTokens transcriptCharBudget
            }
          }
        }
        """;

    private const string UpdateMutation = """
        mutation {
          updateClaudeCodeSafeguardsSettings(input: {
            classifierModel: "reviewer"
            policy: "Custom policy"
            fallbackModels: ["backup"]
            escalateOnDenial: true
            attemptTimeoutSeconds: 0.5
            timeoutSeconds: 12
            maxTokens: 1024
            transcriptCharBudget: 8000
          })
        }
        """;

    private readonly FakeSystemStore _store = new();
    private readonly FakeUserAdminStore _adminUsers = new();
    private readonly AdminGraphqlGateServices _gate = new(isOwner: false);

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // The gate user (id 9) is a non-owner carrying both settings scopes;
        // AuthorizationScope resolves them through IUserAdminStore.
        await _adminUsers.CreateAsync(
            new UserRecord
            {
                Id = AdminGraphqlGateServices.UserId,
                Email = "gate-user@example.com",
                Status = "activated",
                Scopes = """["read_settings","write_settings"]""",
            },
            roleIds: null);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IUserAdminStore>(_adminUsers);
        builder.Services.AddScoped<AuthorizationScope>();
        builder.Services.AddSingleton(new ClaudeCodeSafeguardsServerDefaults());
        builder.Services.AddScoped(_ => new ClaudeCodeSafeguardsSettingsService(
            _store,
            _.GetRequiredService<ClaudeCodeSafeguardsServerDefaults>(),
            _.GetRequiredService<AuthorizationScope>()));
        builder.Services.AddScoped<IClaudeCodeSafeguardsSettingsService>(
            static services => services.GetRequiredService<ClaudeCodeSafeguardsSettingsService>());

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
    public async Task QueryReturnsTheServerDefaults()
    {
        var data = await QueryAsync(SettingsQuery);

        var settings = data.GetProperty("claudeCodeSafeguardsSettings");
        Assert.True(settings.GetProperty("usingServerDefaults").GetBoolean());
        Assert.Equal(
            SafeguardsDefaults.DefaultPolicy,
            settings.GetProperty("defaultPolicy").GetString());

        var config = settings.GetProperty("config");
        Assert.Equal(string.Empty, config.GetProperty("classifierModel").GetString());
        Assert.Equal(string.Empty, config.GetProperty("policy").GetString());
        Assert.Empty(config.GetProperty("fallbackModels").EnumerateArray());
        Assert.False(config.GetProperty("escalateOnDenial").GetBoolean());
        Assert.Equal(0, config.GetProperty("attemptTimeoutSeconds").GetDouble());
        Assert.Equal(30, config.GetProperty("timeoutSeconds").GetDouble());
        Assert.Equal(4096, config.GetProperty("maxTokens").GetInt32());
        Assert.Equal(24000, config.GetProperty("transcriptCharBudget").GetInt32());
    }

    [Fact]
    public async Task UpdateThenReadThenResetRoundTrips()
    {
        var update = await QueryAsync(UpdateMutation);
        Assert.True(update.GetProperty("updateClaudeCodeSafeguardsSettings").GetBoolean());

        var row = Assert.Single(_store.Rows);
        Assert.Equal(ClaudeCodeSafeguardsSettingsService.SystemKey, row.Key);
        Assert.Equal(
            """{"classifierModel":"reviewer","policy":"Custom policy","fallbackModels":["backup"],"escalateOnDenial":true,"attemptTimeoutSeconds":0.5,"timeoutSeconds":12,"maxTokens":1024,"transcriptCharBudget":8000}""",
            row.Value);

        var stored = (await QueryAsync(SettingsQuery))
            .GetProperty("claudeCodeSafeguardsSettings");
        Assert.False(stored.GetProperty("usingServerDefaults").GetBoolean());
        var config = stored.GetProperty("config");
        Assert.Equal("reviewer", config.GetProperty("classifierModel").GetString());
        Assert.Equal("Custom policy", config.GetProperty("policy").GetString());
        Assert.Equal("backup", config.GetProperty("fallbackModels")[0].GetString());
        Assert.True(config.GetProperty("escalateOnDenial").GetBoolean());
        Assert.Equal(0.5, config.GetProperty("attemptTimeoutSeconds").GetDouble());
        Assert.Equal(12, config.GetProperty("timeoutSeconds").GetDouble());
        Assert.Equal(1024, config.GetProperty("maxTokens").GetInt32());
        Assert.Equal(8000, config.GetProperty("transcriptCharBudget").GetInt32());

        var reset = await QueryAsync(
            "mutation { resetClaudeCodeSafeguardsSettings }");
        Assert.True(reset.GetProperty("resetClaudeCodeSafeguardsSettings").GetBoolean());
        Assert.Equal("null", Assert.Single(_store.Rows).Value);

        Assert.True((await QueryAsync(SettingsQuery))
            .GetProperty("claudeCodeSafeguardsSettings")
            .GetProperty("usingServerDefaults")
            .GetBoolean());
    }

    [Fact]
    public async Task ValidationFailuresUseTheCodedEnvelope()
    {
        var response = await PostAsync(
            """
            mutation {
              updateClaudeCodeSafeguardsSettings(input: {
                classifierModel: ""
                policy: ""
                fallbackModels: []
                escalateOnDenial: false
                attemptTimeoutSeconds: 0
                timeoutSeconds: 0
                maxTokens: 4096
                transcriptCharBudget: 24000
              })
            }
            """);

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Equal(
            "overall timeout must be greater than zero and at most 120 seconds",
            error.GetProperty("message").GetString());
        var extensions = error.GetProperty("extensions");
        Assert.Equal(CodedException.ValidationFailed, extensions.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, extensions.GetProperty("resource").ValueKind);
        Assert.Equal(JsonValueKind.Null, extensions.GetProperty("field").ValueKind);
        Assert.Equal(JsonValueKind.Null, extensions.GetProperty("value").ValueKind);
        Assert.Empty(_store.Rows);
    }

    [Fact]
    public async Task ReadRequiresTheReadSettingsScope()
    {
        (await _adminUsers.GetByIdAsync(AdminGraphqlGateServices.UserId))!.Scopes =
            """["write_settings"]""";

        var response = await PostAsync(SettingsQuery);

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Contains(
            "does not have required scope read_settings",
            error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task MutationsRequireTheWriteSettingsScope()
    {
        (await _adminUsers.GetByIdAsync(AdminGraphqlGateServices.UserId))!.Scopes =
            """["read_settings"]""";

        var response = await PostAsync(UpdateMutation);

        var error = Assert.Single(response.GetProperty("errors").EnumerateArray());
        Assert.Contains(
            "does not have required scope write_settings",
            error.GetProperty("message").GetString());

        var reset = await PostAsync(
            "mutation { resetClaudeCodeSafeguardsSettings }");
        Assert.Contains(
            "does not have required scope write_settings",
            Assert.Single(reset.GetProperty("errors").EnumerateArray())
                .GetProperty("message")
                .GetString());
        Assert.Empty(_store.Rows);
    }

    private async Task<JsonElement> QueryAsync(string query)
    {
        var response = await PostAsync(query);
        Assert.False(response.TryGetProperty("errors", out _), response.ToString());
        return response.GetProperty("data");
    }

    private async Task<JsonElement> PostAsync(string query)
    {
        var payload = JsonSerializer.Serialize(new { query });
        using var response = await _client.PostAsync(
            "/admin/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
