using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the updateChannel mutation against the reference resolver and
/// biz.ChannelService.UpdateChannel (axonhub.resolvers.go:188-190,
/// channel.go:849-1203): only provided columns are written, clear flags store
/// nulls, the xai/Command Code rewrites run in reference order, name checks
/// honour soft deletes, the version guard only applies to identity updates,
/// and the live validator extensions keep their live positions (C144/C146).
/// </summary>
public sealed class AdminChannelUpdateEndpointTests : IAsyncLifetime
{
    private static readonly string[] SupportedModelsA = ["a"];
    private static readonly string[] SupportedModelsPriced = ["gpt-4o", "gpt-4o-mini"];
    private static readonly string[] SupportedModelsGpt4o = ["gpt-4o"];

    private const string UpdateMutation =
        """
        mutation Update($id: ID!, $input: UpdateChannelInput!) {
          updateChannel(id: $id, input: $input) {
            id name type status baseURL remark orderingWeight autoSyncModelPattern
          }
        }
        """;

    private readonly FakeChannelWriteStore _channels = new();
    private readonly ChannelLimiterManager _limiters = new();
    private readonly FakeChannelModelPriceStore _prices = new();
    private readonly RecordingQuotaInvalidator _quotaInvalidator = new();

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
        builder.Services.AddSingleton<IChannelModelPriceStore>(_prices);
        builder.Services.AddSingleton(_limiters);
        builder.Services.AddSingleton<IProviderQuotaInvalidator>(_quotaInvalidator);
        builder.Services.AddSingleton<ChannelLifecycleService>();

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
    public async Task UpdateChannelWritesOnlyTheProvidedColumns()
    {
        var channel = SeedChannel(
            name: "Alpha",
            remark: "old",
            settings: """{"extraModelPrefix":"p"}""");

        var data = await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new
                {
                    name = "Renamed",
                    remark = "new",
                    orderingWeight = 7,
                    status = "enabled",
                    errorMessage = "ignored",
                    clearSettings = true,
                },
            });

        var node = data.GetProperty("updateChannel");
        Assert.Equal("Renamed", node.GetProperty("name").GetString());
        Assert.Equal("new", node.GetProperty("remark").GetString());
        Assert.Equal(7, node.GetProperty("orderingWeight").GetInt32());

        // status and clearSettings are not read by the reference service.
        Assert.Equal("disabled", node.GetProperty("status").GetString());
        Assert.Equal("disabled", channel.Status);
        Assert.Equal("""{"extraModelPrefix":"p"}""", channel.Settings);

        var call = Assert.Single(_channels.UpdateCalls);
        Assert.Equal(channel.Id, call.Id);
        var update = call.Update;
        Assert.Equal("Renamed", update.Name);
        Assert.Equal("new", update.Remark);
        Assert.Equal(7, update.OrderingWeight);
        Assert.Null(update.Type);
        Assert.Null(update.BaseUrl);
        Assert.Null(update.SettingsJson);
        Assert.Null(update.SupportedModelsJson);
        Assert.Null(update.ManualModelsJson);
        Assert.Null(update.TagsJson);
        Assert.Null(update.EndpointsJson);
        Assert.Null(update.CredentialsJson);
        Assert.Null(update.PoliciesJson);

        // Only identity/endpoint/Command Code updates carry the version guard.
        Assert.Null(update.ExpectedUpdatedAt);
    }

    [Fact]
    public async Task UpdateChannelClearFlagsStoreNulls()
    {
        var channel = SeedChannel(
            name: "Alpha",
            remark: "old",
            autoSyncModelPattern: "gpt-*",
            errorMessage: "boom");

        var data = await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new
                {
                    clearRemark = true,
                    clearAutoSyncModelPattern = true,
                    clearErrorMessage = true,
                },
            });

        var node = data.GetProperty("updateChannel");
        Assert.Null(node.GetProperty("remark").GetString());
        Assert.Null(node.GetProperty("autoSyncModelPattern").GetString());

        var update = Assert.Single(_channels.UpdateCalls).Update;
        Assert.True(update.ClearRemark);
        Assert.True(update.ClearAutoSyncModelPattern);
        Assert.True(update.ClearErrorMessage);

        // ent's ClearField writes NULL for all three nullable columns.
        Assert.Null(channel.Remark);
        Assert.Null(channel.AutoSyncModelPattern);
        Assert.Null(channel.ErrorMessage);
    }

    [Fact]
    public async Task UpdateChannelRejectsADuplicateLiveName()
    {
        var channel = SeedChannel(name: "Alpha");
        SeedChannel(name: "Bravo");

        var errors = await QueryErrorsAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { name = "Bravo" },
            });

        Assert.Equal("channel name 'Bravo' already exists", errors[0].GetProperty("message").GetString());
        var extensions = errors[0].GetProperty("extensions");
        Assert.Equal("DUPLICATE_NAME", extensions.GetProperty("code").GetString());
        Assert.Equal("channel", extensions.GetProperty("resource").GetString());
        Assert.Equal("name", extensions.GetProperty("field").GetString());
        Assert.Equal("Bravo", extensions.GetProperty("value").GetString());

        Assert.Empty(_channels.UpdateCalls);
        Assert.Equal((channel.Id, "Bravo"), Assert.Single(_channels.NameChecks));
    }

    [Fact]
    public async Task UpdateChannelAllowsReusingASoftDeletedName()
    {
        var channel = SeedChannel(name: "Alpha");
        SeedChannel(name: "Recycled", deletedAt: 1234);

        var data = await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { name = "Recycled" },
            });

        Assert.Equal("Recycled", data.GetProperty("updateChannel").GetProperty("name").GetString());
        Assert.Equal("Recycled", channel.Name);
    }

    [Fact]
    public async Task UpdateChannelForcesTheXaiSubscriptionSurface()
    {
        var channel = SeedChannel(name: "Alpha");

        var data = await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new
                {
                    type = "xai_subscription",
                    baseURL = "https://caller.example/v1",
                    endpoints = new[] { new { apiFormat = "openai/chat_completions" } },
                },
            });

        Assert.Equal(
            ChannelValidation.XaiSubscriptionBaseUrl,
            data.GetProperty("updateChannel").GetProperty("baseURL").GetString());

        var update = Assert.Single(_channels.UpdateCalls).Update;
        Assert.Equal("xai_subscription", update.Type);
        Assert.Equal(ChannelValidation.XaiSubscriptionBaseUrl, update.BaseUrl);

        // input.Endpoints is replaced by an explicit empty slice.
        Assert.Equal("[]", update.EndpointsJson);
        Assert.NotNull(update.ExpectedUpdatedAt);
    }

    [Fact]
    public async Task UpdateChannelForcesTheXaiSurfaceWhenOnlyTheBaseUrlChanges()
    {
        var channel = SeedChannel(
            name: "Alpha",
            type: "xai_subscription",
            baseUrl: ChannelValidation.XaiSubscriptionBaseUrl);

        var data = await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { baseURL = "https://caller.example/v1" },
            });

        Assert.Equal(
            ChannelValidation.XaiSubscriptionBaseUrl,
            data.GetProperty("updateChannel").GetProperty("baseURL").GetString());

        var update = Assert.Single(_channels.UpdateCalls).Update;
        Assert.Null(update.Type);
        Assert.Equal(ChannelValidation.XaiSubscriptionBaseUrl, update.BaseUrl);
        Assert.Equal("[]", update.EndpointsJson);
    }

    [Fact]
    public async Task UpdateChannelNormalizesResponsesEndpointsAndModelProtocols()
    {
        var channel = SeedChannel(name: "Alpha", baseUrl: "wss://gateway.example/ws");

        await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new
                {
                    baseURL = "wss://gateway.example/ws",
                    endpoints = new[] { new { apiFormat = "openai/responses" } },
                    settings = new
                    {
                        modelProtocols = new[]
                        {
                            new
                            {
                                model = "m",
                                apiFormats = new[] { "openai/responses" },
                                enabled = true,
                            },
                        },
                    },
                },
            });

        var update = Assert.Single(_channels.UpdateCalls).Update;
        Assert.Equal("""[{"api_format":"openai/responses-ws"}]""", update.EndpointsJson);

        using var settings = JsonDocument.Parse(update.SettingsJson!);
        var protocol = settings.RootElement.GetProperty("modelProtocols")[0];
        Assert.Equal(
            "openai/responses-ws",
            protocol.GetProperty("apiFormats")[0].GetString());
    }

    [Fact]
    public async Task UpdateChannelCanonicalizesTheCommandCodeCookie()
    {
        var channel = SeedChannel(
            name: "Alpha",
            type: "commandcode",
            baseUrl: "https://api.commandcode.ai");

        var settings = await UpdateWithSettingsAsync(
            channel,
            new
            {
                providerQuota = new
                {
                    commandCode = new
                    {
                        authCookie =
                            "theme=dark; __secure-commandcode_prod_.session_token=abc; other=1",
                    },
                },
            });

        Assert.Equal(
            "__secure-commandcode_prod_.session_token=abc",
            settings.GetProperty("providerQuota")
                .GetProperty("commandCode")
                .GetProperty("authCookie")
                .GetString());
    }

    [Fact]
    public async Task UpdateChannelDropsABlankCommandCodeCookieButKeepsOllama()
    {
        var channel = SeedChannel(
            name: "Alpha",
            type: "commandcode",
            baseUrl: "https://api.commandcode.ai");

        var settings = await UpdateWithSettingsAsync(
            channel,
            new
            {
                providerQuota = new
                {
                    commandCode = new { authCookie = "   " },
                    ollama = new { authCookie = "ollama=1" },
                },
            });

        var quota = settings.GetProperty("providerQuota");
        Assert.False(quota.TryGetProperty("commandCode", out _));
        Assert.Equal("ollama=1", quota.GetProperty("ollama").GetProperty("authCookie").GetString());
    }

    [Fact]
    public async Task UpdateChannelClearsStoredQuotaSettingsOnANonCommandCodeTypeChange()
    {
        var channel = SeedChannel(
            name: "Alpha",
            type: "commandcode",
            baseUrl: "https://api.commandcode.ai",
            settings:
                """{"providerQuota":{"commandCode":{"authCookie":"__secure-commandcode_prod_.session_token=abc"}}}""");

        await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { type = "openai" },
            });

        var update = Assert.Single(_channels.UpdateCalls).Update;
        Assert.Equal("openai", update.Type);
        Assert.NotNull(update.ExpectedUpdatedAt);

        using var settings = JsonDocument.Parse(update.SettingsJson!);
        Assert.False(settings.RootElement.TryGetProperty("providerQuota", out _));
        Assert.Equal(
            string.Empty,
            settings.RootElement.GetProperty("extraModelPrefix").GetString());
    }

    [Fact]
    public async Task UpdateChannelInvalidatesTheQuotaStatusWhenTheProviderIdentityChanges()
    {
        var channel = SeedChannel(name: "Alpha");

        await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { baseURL = "https://api.other/v1" },
            });

        Assert.Equal([channel.Id], _quotaInvalidator.ChannelIds);
    }

    [Fact]
    public async Task UpdateChannelSkipsTheQuotaHookWhenTheIdentityIsUnchanged()
    {
        var channel = SeedChannel(name: "Alpha");

        // The identity guard runs (baseURL is provided) but compares equal.
        await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { baseURL = "https://api.example/v1" },
            });

        // A non-identity update never reaches the comparison at all.
        await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { remark = "note" },
            });

        Assert.Empty(_quotaInvalidator.ChannelIds);
    }

    [Fact]
    public async Task UpdateChannelInvalidatesWhenTheCommandCodeCookieIsCleared()
    {
        var channel = SeedChannel(
            name: "Alpha",
            type: "commandcode",
            baseUrl: "https://api.commandcode.ai",
            settings:
                """{"providerQuota":{"commandCode":{"authCookie":"__secure-commandcode_prod_.session_token=abc"}}}""");

        // A blank cookie and an omitted providerQuota block both clear it
        // (channel.go:923-925) and both invalidate without an identity change.
        await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new
                {
                    settings = new
                    {
                        providerQuota = new
                        {
                            commandCode = new { authCookie = "   " },
                        },
                    },
                },
            });

        await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { settings = new { extraModelPrefix = "x" } },
            });

        Assert.Equal([channel.Id, channel.Id], _quotaInvalidator.ChannelIds);
    }

    [Fact]
    public async Task UpdateChannelKeepsTheUpdateWhenTheQuotaHookFails()
    {
        var channel = SeedChannel(name: "Alpha");
        _quotaInvalidator.Error = new InvalidOperationException("invalidate boom");

        var data = await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { baseURL = "https://api.other/v1" },
            });

        Assert.Equal(
            "https://api.other/v1",
            data.GetProperty("updateChannel").GetProperty("baseURL").GetString());
        Assert.Equal([channel.Id], _quotaInvalidator.ChannelIds);
    }

    [Fact]
    public async Task UpdateChannelKeepsTheStoredZenmuxManagementKey()
    {
        var channel = SeedChannel(
            name: "Alpha",
            type: "zenmux",
            credentials: """{"managementApiKey":"mgmt-key"}""");

        await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { credentials = new { apiKey = "sk-new" } },
            });

        var update = Assert.Single(_channels.UpdateCalls).Update;
        using var credentials = JsonDocument.Parse(update.CredentialsJson!);
        Assert.Equal("sk-new", credentials.RootElement.GetProperty("apiKey").GetString());
        Assert.Equal(
            "mgmt-key",
            credentials.RootElement.GetProperty("managementApiKey").GetString());
    }

    [Fact]
    public async Task UpdateChannelPrunesModelProtocolsRemovedFromSupportedModels()
    {
        var channel = SeedChannel(
            name: "Alpha",
            supportedModels: """["a","b"]""",
            settings:
                """{"extraModelPrefix":"x","modelProtocols":[{"model":"a","apiFormats":["openai/chat_completions"]},{"model":"b","apiFormats":["openai/chat_completions"]}]}""");

        await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { supportedModels = SupportedModelsA },
            });

        var update = Assert.Single(_channels.UpdateCalls).Update;
        Assert.Equal("""["a"]""", update.SupportedModelsJson);

        using var settings = JsonDocument.Parse(update.SettingsJson!);
        Assert.Equal("x", settings.RootElement.GetProperty("extraModelPrefix").GetString());
        var protocols = settings.RootElement.GetProperty("modelProtocols");
        Assert.Equal(1, protocols.GetArrayLength());
        Assert.Equal("a", protocols[0].GetProperty("model").GetString());
    }

    [Fact]
    public async Task UpdateChannelEnsuresPricesOnlyWhenSupportedModelsIsProvided()
    {
        var channel = SeedChannel(name: "PriceUpdate");

        // Absent: the column is untouched and no ensure runs (input nil).
        await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { name = "Renamed" },
            });
        Assert.Empty(_prices.EnsureCalls);

        await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { supportedModels = SupportedModelsPriced },
            });
        var call = Assert.Single(_prices.EnsureCalls);
        Assert.Equal(channel.Id, call.ChannelId);
        Assert.Equal(SupportedModelsPriced, call.ModelIds);

        // An empty list is still "provided" and runs the ensure.
        await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { supportedModels = Array.Empty<string>() },
            });
        Assert.Equal(2, _prices.EnsureCalls.Count);
        Assert.Empty(_prices.EnsureCalls[1].ModelIds);
    }

    [Fact]
    public async Task UpdateChannelSurfacesAutoPricingFailuresUnwrapped()
    {
        var channel = SeedChannel(name: "PriceUpdateFailure");
        _prices.EnsureFailure = new StoreFailureException(
            "failed to auto-configure channel model price: model_id=gpt-4o: boom",
            new InvalidOperationException("boom"));

        var errors = await QueryErrorsAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { supportedModels = SupportedModelsGpt4o },
            });

        // UpdateChannel returns the ensure error unchanged.
        Assert.Equal(
            "failed to auto-configure channel model price: model_id=gpt-4o: boom",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task UpdateChannelChecksTheRetryBoundBeforeTheSettingsValidators()
    {
        var channel = SeedChannel(name: "Alpha");

        var errors = await QueryErrorsAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new
                {
                    settings = new
                    {
                        maxSingleChannelRetries = 70000,
                        bodyOverrideOperations = new[] { new { op = "set", path = "" } },
                    },
                },
            });

        // Live update validates the retry bound right after cookie
        // normalization, before the body-override validator (live channel.go:946).
        Assert.Equal(
            "invalid max single channel retries 70000: must be between 0 and 65535",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_channels.UpdateCalls);
    }

    [Fact]
    public async Task UpdateChannelValidatesVertexBindingsBeforeWriting()
    {
        var channel = SeedChannel(name: "Alpha");

        var errors = await QueryErrorsAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new
                {
                    credentials = new
                    {
                        vertexApiKeys = new[]
                        {
                            new { projectID = "p", apiKey = "k", region = "r" },
                        },
                    },
                },
            });

        Assert.Equal(
            "vertex api keys are only supported by gemini_vertex channels",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_channels.UpdateCalls);
    }

    [Fact]
    public async Task UpdateChannelReportsAConcurrentVersionChange()
    {
        var channel = SeedChannel(name: "Alpha");
        _channels.UpdateConflict = true;

        var errors = await QueryErrorsAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { type = "openai" },
            });

        Assert.Equal(
            "channel was updated concurrently; retry the operation",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task UpdateChannelKeepsTheIdentityLoadFailureMessage()
    {
        var errors = await QueryErrorsAsync(
            UpdateMutation,
            new
            {
                id = Gid(424242),
                input = new { name = "Nobody" },
            });

        Assert.Equal(
            "failed to load channel provider identity: ent: channel not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task IntrospectionExposesUpdateChannelAndTheReferenceInput()
    {
        var data = await QueryDataAsync(
            """
            {
              __schema { mutationType { fields { name } } }
              input: __type(name: "UpdateChannelInput") {
                inputFields { name }
              }
            }
            """);

        var mutations = data.GetProperty("__schema").GetProperty("mutationType")
            .GetProperty("fields").EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Contains("updateChannel", mutations);

        var fields = data.GetProperty("input").GetProperty("inputFields")
            .EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Contains("baseURL", fields);
        Assert.Contains("clearBaseURL", fields);
        Assert.Contains("status", fields);
        Assert.Contains("errorMessage", fields);
        Assert.Contains("clearSettings", fields);
        Assert.Contains("appendEndpoints", fields);
        Assert.Contains("clearEndpoints", fields);
    }

    private async Task<JsonElement> UpdateWithSettingsAsync(
        ChannelRecord channel,
        object settings)
    {
        await QueryDataAsync(
            UpdateMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { settings },
            });

        var update = Assert.Single(_channels.UpdateCalls).Update;
        using var document = JsonDocument.Parse(update.SettingsJson!);
        return document.RootElement.Clone();
    }

    private static string Gid(long id) => "gid://axonhub/Channel/" + id;

    private ChannelRecord SeedChannel(
        string name,
        string type = "openai",
        string? baseUrl = "https://api.example/v1",
        string credentials = "{}",
        string supportedModels = "null",
        string? settings = null,
        string endpoints = "[]",
        string? remark = null,
        string autoSyncModelPattern = "",
        string? errorMessage = null,
        long deletedAt = 0)
    {
        var record = new ChannelRecord
        {
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            DeletedAt = deletedAt,
            Type = type,
            BaseUrl = baseUrl,
            Name = name,
            Status = "disabled",
            Credentials = credentials,
            SupportedModels = supportedModels,
            AutoSyncModelPattern = autoSyncModelPattern,
            DefaultTestModel = string.Empty,
            Tags = "[]",
            Policies = """{"stream":"unlimited"}""",
            Settings = settings,
            Endpoints = endpoints,
            Remark = remark,
            ErrorMessage = errorMessage,
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
