using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Diagnostics;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Orchestration;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the getCacheDiagnostics query and clearCache mutation over
/// /admin/graphql (system.graphql:459-509, system.resolvers.go:383-408,
/// 690-726): the owner gate on both fields, the payload shape, the default
/// CHANNEL_CACHE target and the reload error chain.
/// </summary>
public sealed class AdminDiagnosticsEndpointTests : IAsyncLifetime
{
    private static readonly DateTime UpdatedAt =
        new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private readonly FakeChannelAdminStore _channels = new();
    private readonly FakeModelStore _models = new();
    private readonly FakeEnabledChannelCacheSnapshotProvider _enabledCache = new();
    private readonly FakeDiagnosticsCandidateSelector _selector = new();
    private readonly DiagnosticsCacheRefresher _refresher = new();
    private readonly AdminGraphqlGateServices _gate = new();
    private readonly TestTimeProvider _time = new(new DateTimeOffset(2026, 2, 3, 4, 5, 6, TimeSpan.Zero));

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IChannelAdminStore>(_channels);
        builder.Services.AddSingleton<IModelStore>(_models);
        builder.Services.AddSingleton<IEnabledChannelCacheSnapshotProvider>(_enabledCache);
        builder.Services.AddSingleton<ICacheDiagnosticsCandidateSelector>(_selector);
        builder.Services.AddSingleton<IEnabledChannelsCacheRefresher>(_refresher);

        // Registered after the gate's TimeProvider.System so the export's two
        // clock reads are fixed.
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddScoped<CacheDiagnosticsService>();

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
    public async Task CacheDiagnosticsRequireOwnerAccess()
    {
        var member = _gate.Users.Add(user =>
        {
            user.Id = 77;
            user.Email = "member@example.com";
            user.Status = "activated";
            user.IsOwner = false;
        });
        var token = _gate.IssueJwt(member);

        var queryErrors = await QueryErrorsAsync(
            """
            query Diagnostics($input: GetCacheDiagnosticsInput) {
              getCacheDiagnostics(input: $input) { fileName }
            }
            """,
            new { input = new { targets = new[] { "CHANNEL_CACHE" } } },
            token);
        Assert.Equal(
            "permission denied: owner access required",
            queryErrors[0].GetProperty("message").GetString());

        var mutationErrors = await QueryErrorsAsync(
            """
            mutation Clear {
              clearCache(input: { targets: [CHANNEL_CACHE] }) { success }
            }
            """,
            variables: null,
            token);
        Assert.Equal(
            "permission denied: owner access required",
            mutationErrors[0].GetProperty("message").GetString());
        Assert.Equal(0, _refresher.ReloadCount);
    }

    [Fact]
    public async Task GetCacheDiagnosticsReturnsTheExportForTheOwner()
    {
        _channels.Seed(new ChannelRecord
        {
            Name = "Alpha",
            Type = "openai",
            Status = "enabled",
            SupportedModels = """["m"]""",
            Settings = "null",
            UpdatedAt = UpdatedAt,
        });
        _models.Seed(row =>
        {
            row.ModelId = "m";
            row.Status = "enabled";
            row.Settings = "{}";
            row.UpdatedAt = UpdatedAt;
        });

        var data = await QueryDataAsync(
            """
            query Diagnostics($input: GetCacheDiagnosticsInput) {
              getCacheDiagnostics(input: $input) {
                fileName
                content
                targets
              }
            }
            """,
            new { input = new { targets = new[] { "CHANNEL_CACHE" } } });

        var payload = data.GetProperty("getCacheDiagnostics");
        Assert.Equal(
            "axonhub-channel-model-cache-diagnostics-20260203T040506Z.json",
            payload.GetProperty("fileName").GetString());
        Assert.Equal(
            new[] { "CHANNEL_CACHE" },
            payload.GetProperty("targets").EnumerateArray()
                .Select(static target => target.GetString()!)
                .ToArray());

        using var content = JsonDocument.Parse(payload.GetProperty("content").GetString()!);
        var root = content.RootElement;
        Assert.Equal("2026-02-03T04:05:06Z", root.GetProperty("exportedAt").GetString());

        var channels = root.GetProperty("database").GetProperty("channels");
        Assert.Equal(1, channels.GetArrayLength());
        Assert.Equal("Alpha", channels[0].GetProperty("name").GetString());

        var models = root.GetProperty("database").GetProperty("models");
        Assert.Equal(1, models.GetArrayLength());
        Assert.Equal("m", models[0].GetProperty("modelId").GetString());

        var cache = root.GetProperty("cache").GetProperty("enabledChannelCache");
        Assert.Equal(0, cache.GetProperty("channelCount").GetInt32());
        Assert.Empty(cache.GetProperty("channelIDs").EnumerateArray());
    }

    [Fact]
    public async Task GetCacheDiagnosticsDefaultsToTheChannelCacheTarget()
    {
        var data = await QueryDataAsync(
            """
            query {
              getCacheDiagnostics {
                fileName
                targets
              }
            }
            """);

        var payload = data.GetProperty("getCacheDiagnostics");
        Assert.Equal(
            new[] { "CHANNEL_CACHE" },
            payload.GetProperty("targets").EnumerateArray()
                .Select(static target => target.GetString()!)
                .ToArray());

        // Both reads use the fixed clock.
        Assert.Equal(
            "axonhub-channel-model-cache-diagnostics-20260203T040506Z.json",
            payload.GetProperty("fileName").GetString());
    }

    [Fact]
    public async Task ClearCacheReloadsAndRepliesWithTheSuccessPayload()
    {
        var data = await QueryDataAsync(
            """
            mutation {
              clearCache(input: { targets: [CHANNEL_CACHE] }) {
                success
                message
                targets
              }
            }
            """);

        var payload = data.GetProperty("clearCache");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal("cache cleared successfully", payload.GetProperty("message").GetString());
        Assert.Equal(
            new[] { "CHANNEL_CACHE" },
            payload.GetProperty("targets").EnumerateArray()
                .Select(static target => target.GetString()!)
                .ToArray());
        Assert.Equal(1, _refresher.ReloadCount);
        Assert.Equal(0, _refresher.RequestCount);
    }

    [Fact]
    public async Task ClearCacheWithoutTargetsUsesTheDefault()
    {
        var data = await QueryDataAsync(
            """
            mutation {
              clearCache(input: {}) {
                success
                targets
              }
            }
            """);

        Assert.True(data.GetProperty("clearCache").GetProperty("success").GetBoolean());
        Assert.Equal(
            new[] { "CHANNEL_CACHE" },
            data.GetProperty("clearCache").GetProperty("targets").EnumerateArray()
                .Select(static target => target.GetString()!)
                .ToArray());
        Assert.Equal(1, _refresher.ReloadCount);
    }

    [Fact]
    public async Task ClearCacheFailureKeepsTheReloadChain()
    {
        _refresher.ReloadFailure = new InvalidOperationException("cache load failed");

        var errors = await QueryErrorsAsync(
            """
            mutation Clear {
              clearCache(input: { targets: [CHANNEL_CACHE] }) { success }
            }
            """);

        Assert.Equal(
            "failed to clear cache for CHANNEL_CACHE: "
            + "failed to reload enabled channels cache: cache load failed",
            errors[0].GetProperty("message").GetString());
        Assert.Equal(1, _refresher.ReloadCount);
    }

    // -- helpers ---------------------------------------------------------

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null,
        string? token = null)
    {
        var root = await ExecuteAsync(query, variables, token);
        Assert.False(root.TryGetProperty("errors", out var errors), errors.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(
        string query,
        object? variables = null,
        string? token = null)
    {
        var root = await ExecuteAsync(query, variables, token);
        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        return errors.EnumerateArray().Select(static error => error.Clone()).ToArray();
    }

    private async Task<JsonElement> ExecuteAsync(
        string query,
        object? variables,
        string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/graphql")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query, variables }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                token ?? _gate.Jwt);

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        AdminGraphqlHttpResponseAssertions.AssertStatus(response, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
