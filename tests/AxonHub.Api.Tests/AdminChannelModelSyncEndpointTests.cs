using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Llm.Http;
using AxonHub.Llm.Pipeline;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks syncChannelModels over /admin/graphql against the reference resolver
/// (axonhub.resolvers.go:710-729): the payload field names, the echoed
/// <c>gid://axonhub/Channel/&lt;id&gt;</c> value, the null manual_models
/// normalization and the error passthrough.
/// </summary>
public sealed class AdminChannelModelSyncEndpointTests : IAsyncLifetime
{
    private const string SyncChannelModelsMutation =
        """
        mutation SyncChannelModels($channelID: ID!, $pattern: String) {
          syncChannelModels(channelID: $channelID, pattern: $pattern) {
            channelID supportedModels manualModels
          }
        }
        """;

    private readonly FakeChannelModelSyncStore _channels = new();
    private readonly FakeChannelModelPriceStore _prices = new();
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
        builder.Services.AddSingleton<IChannelModelSyncStore>(_channels);
        builder.Services.AddSingleton<IChannelModelPriceStore>(_prices);
        builder.Services.AddSingleton<IExecutor>(_executor);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IChannelAutoSyncFrequencyProvider>(
            new FakeAutoSyncFrequencyProvider(AutoSyncFrequency.OneHour));
        builder.Services.AddSingleton<IChannelModelSyncReloadHook>(
            new RecordingModelSyncReloadHook());
        builder.Services.AddScoped<ChannelModelSyncService>();

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
    public async Task SyncChannelModelsReturnsTheMergedModelsAndEchoesTheGid()
    {
        SeedChannel(
            supportedModels: """["existing-model"]""",
            manualModels: """["manual-model"]""");
        _executor.OnDo = (_, request, _) =>
        {
            Assert.Equal("https://provider.test/v1/models", request.Url);
            return ModelFetchTestResponses.Json(
                """{"data":[{"id":"fetched-model"}]}""");
        };

        var data = await QueryDataAsync(
            SyncChannelModelsMutation,
            new { channelID = Gid(1), pattern = (string?)null });

        var payload = data.GetProperty("syncChannelModels");
        Assert.Equal(Gid(1), payload.GetProperty("channelID").GetString());
        Assert.Equal(
            ["manual-model", "fetched-model"],
            payload.GetProperty("supportedModels").EnumerateArray()
                .Select(static model => model.GetString()!).ToArray());
        Assert.Equal(
            ["manual-model"],
            payload.GetProperty("manualModels").EnumerateArray()
                .Select(static model => model.GetString()!).ToArray());
        Assert.Single(_prices.EnsureCalls);
    }

    [Fact]
    public async Task SyncChannelModelsEchoesTheCallersGidTypeSegment()
    {
        SeedChannel(supportedModels: """["existing-model"]""", manualModels: "[]");
        _executor.OnDo = (_, _, _) => ModelFetchTestResponses.Json(
            """{"data":[{"id":"existing-model"}]}""");

        var data = await QueryDataAsync(
            SyncChannelModelsMutation,
            new { channelID = "gid://axonhub/User/1", pattern = (string?)null });

        Assert.Equal(
            "gid://axonhub/User/1",
            data.GetProperty("syncChannelModels").GetProperty("channelID").GetString());
    }

    [Fact]
    public async Task SyncChannelModelsNormalizesANullManualModelsColumn()
    {
        SeedChannel(supportedModels: """["existing-model"]""", manualModels: "null");
        _executor.OnDo = (_, _, _) => ModelFetchTestResponses.Json(
            """{"data":[{"id":"existing-model"}]}""");

        var data = await QueryDataAsync(
            SyncChannelModelsMutation,
            new { channelID = Gid(1), pattern = (string?)null });

        var payload = data.GetProperty("syncChannelModels");
        Assert.Equal(
            ["existing-model"],
            payload.GetProperty("supportedModels").EnumerateArray()
                .Select(static model => model.GetString()!).ToArray());
        Assert.Empty(payload.GetProperty("manualModels").EnumerateArray());
    }

    [Fact]
    public async Task SyncChannelModelsPrefersThePatternOverTheChannelSetting()
    {
        SeedChannel(
            supportedModels: """["old-model"]""",
            manualModels: "[]",
            autoSyncModelPattern: "^keep-.*");
        _executor.OnDo = (_, _, _) => ModelFetchTestResponses.Json(
            """{"data":[{"id":"keep-1"},{"id":"override-1"},{"id":"other"}]}""");

        var data = await QueryDataAsync(
            SyncChannelModelsMutation,
            new { channelID = Gid(1), pattern = "^override-.*" });

        Assert.Equal(
            ["override-1"],
            data.GetProperty("syncChannelModels").GetProperty("supportedModels")
                .EnumerateArray().Select(static model => model.GetString()!).ToArray());
    }

    [Fact]
    public async Task SyncChannelModelsWrapsMissingChannelFailures()
    {
        var errors = await QueryErrorsAsync(
            SyncChannelModelsMutation,
            new { channelID = Gid(424242), pattern = (string?)null });

        Assert.Equal(
            "failed to get channel: ent: channel not found",
            errors[0].GetProperty("message").GetString());
    }

    private void SeedChannel(
        string supportedModels,
        string? manualModels,
        string autoSyncModelPattern = "")
        => _channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "Sync channel",
            Type = "openai",
            BaseUrl = "https://provider.test/v1",
            Status = "enabled",
            Credentials = """{"apiKey":"test-key"}""",
            SupportedModels = supportedModels,
            ManualModels = manualModels,
            DefaultTestModel = "existing-model",
            AutoSyncModelPattern = autoSyncModelPattern,
        });

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
