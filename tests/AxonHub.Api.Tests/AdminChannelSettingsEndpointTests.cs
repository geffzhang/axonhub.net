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
/// Locks the live <c>updateChannelSettings</c> mutation
/// (axonhub.graphql:331-352,1011; channel_settings.go:143-195): the patch is
/// merged onto the stored settings and written through UpdateChannel, so
/// untouched fields survive, explicit empty lists clear, the merged value goes
/// through the UpdateChannel validators, and a concurrent commit triggers a
/// re-read and re-merge instead of overwriting the other patch.
/// </summary>
public sealed class AdminChannelSettingsEndpointTests : IAsyncLifetime
{
    private const string PatchMutation =
        """
        mutation Patch($id: ID!, $input: UpdateChannelSettingsInput!) {
          updateChannelSettings(id: $id, input: $input) {
            id
            name
            settings {
              extraModelPrefix
              modelMappings { from to }
              autoTrimedModelPrefixes
              hideOriginalModels
              hideMappedModels
              lowercaseModelId
              retryableStatusCodes
              rateLimit { rpm }
              apiKeyStrategy
              quotaRoutingMode
              providerQuota { commandCode { authCookie } }
            }
          }
        }
        """;

    private readonly FakeChannelWriteStore _channels = new();
    private readonly ChannelLimiterManager _limiters = new();
    private readonly FakeChannelModelPriceStore _prices = new();
    private readonly RecordingQuotaInvalidator _quotaInvalidator = new();
    private readonly RecordingEnabledChannelsRefresher _refresher = new();

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
        builder.Services.AddSingleton<IEnabledChannelsCacheRefresher>(_refresher);
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
    public async Task UpdateChannelSettingsMergesThePatchIntoTheStoredSettings()
    {
        var channel = SeedChannel(
            settings: """{"extraModelPrefix":"deepseek","rateLimit":{"rpm":60},"modelMappings":[{"from":"alias","to":"gpt-4o"}]}""");

        var data = await QueryDataAsync(
            PatchMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { extraModelPrefix = "anthropic" },
            });

        var node = data.GetProperty("updateChannelSettings");
        Assert.Equal(Gid(channel.Id), node.GetProperty("id").GetString());
        Assert.Equal("Alpha", node.GetProperty("name").GetString());

        var settings = node.GetProperty("settings");
        Assert.Equal("anthropic", settings.GetProperty("extraModelPrefix").GetString());
        Assert.Equal(60, settings.GetProperty("rateLimit").GetProperty("rpm").GetInt32());
        var mapping = Assert.Single(settings.GetProperty("modelMappings").EnumerateArray());
        Assert.Equal("alias", mapping.GetProperty("from").GetString());
        Assert.Equal("gpt-4o", mapping.GetProperty("to").GetString());

        var call = Assert.Single(_channels.UpdateCalls);
        Assert.Equal(channel.Id, call.Id);

        // The merge is guarded on the version it read.
        Assert.Equal(SeedUpdatedAt, call.Update.ExpectedUpdatedAt!.Value);
        Assert.NotNull(call.Update.SettingsJson);
        Assert.Equal(1, _channels.UpdateAttempts);

        // Only settings are written; identity columns stay untouched.
        Assert.Null(call.Update.Name);
        Assert.Null(call.Update.Type);
        Assert.Null(call.Update.BaseUrl);
        Assert.Equal(1, _refresher.RequestCount);
        Assert.Empty(_quotaInvalidator.ChannelIds);
    }

    [Fact]
    public async Task AnEmptyPatchStillWritesTheStoredSettings()
    {
        var channel = SeedChannel(
            settings: """{"extraModelPrefix":"deepseek","rateLimit":{"rpm":60}}""");

        await QueryDataAsync(
            PatchMutation,
            new { id = Gid(channel.Id), input = new { } });

        var call = Assert.Single(_channels.UpdateCalls);

        // The reference always sets the merged value: the Go struct's
        // non-omitempty fields and the zero TransformOptions are re-emitted.
        Assert.Equal(
            """{"extraModelPrefix":"deepseek","autoTrimedModelPrefixes":null,"modelMappings":null,"hideOriginalModels":false,"hideMappedModels":false,"lowercaseModelId":false,"overrideParameters":"","overrideHeaders":null,"transformOptions":{"forceArrayInstructions":false,"forceArrayInputs":false,"replaceDeveloperRoleWithSystem":false},"rateLimit":{"rpm":60}}""",
            call.Update.SettingsJson);
    }

    [Fact]
    public async Task AbsentListsKeepTheStoredValueAndAnEmptyListClears()
    {
        var channel = SeedChannel(
            settings: """{"autoTrimedModelPrefixes":["openai"],"retryableStatusCodes":[429,500]}""");

        var data = await QueryDataAsync(
            PatchMutation,
            new
            {
                id = Gid(channel.Id),
                // autoTrimedModelPrefixes is not omitempty in Go: the explicit
                // empty list is stored as [] and reads back as [].
                input = new { autoTrimedModelPrefixes = Array.Empty<string>() },
            });

        var settings = data.GetProperty("updateChannelSettings").GetProperty("settings");
        Assert.Equal(0, settings.GetProperty("autoTrimedModelPrefixes").GetArrayLength());
        Assert.Equal(
            new[] { 429, 500 },
            settings.GetProperty("retryableStatusCodes")
                .EnumerateArray()
                .Select(static code => code.GetInt32())
                .ToArray());

        var stored = ParseStoredSettings(channel);
        Assert.Equal(0, stored.GetProperty("autoTrimedModelPrefixes").GetArrayLength());
        Assert.Equal(2, stored.GetProperty("retryableStatusCodes").GetArrayLength());
    }

    [Fact]
    public async Task ClearingAnOmitEmptyListDropsItFromTheStoredJson()
    {
        var channel = SeedChannel(settings: """{"retryableStatusCodes":[429]}""");

        var data = await QueryDataAsync(
            PatchMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { retryableStatusCodes = Array.Empty<int>() },
            });

        // Go's omitempty drops the cleared slice on marshal, so the saved row
        // reads back as null instead of [].
        var settings = data.GetProperty("updateChannelSettings").GetProperty("settings");
        Assert.Equal(
            JsonValueKind.Null,
            settings.GetProperty("retryableStatusCodes").ValueKind);

        var stored = ParseStoredSettings(channel);
        Assert.False(stored.TryGetProperty("retryableStatusCodes", out _));
    }

    [Fact]
    public async Task AConcurrentCommitIsRereadAndBothPatchesSurvive()
    {
        var channel = SeedChannel(
            settings: """{"extraModelPrefix":"deepseek","rateLimit":{"rpm":60}}""");

        // Another client commits a different patch after our merge read but
        // before the guarded write lands.
        var interleaved = false;
        _channels.BeforeUpdate = row =>
        {
            if (interleaved)
            {
                return;
            }

            interleaved = true;
            row.Settings =
                """{"extraModelPrefix":"deepseek","rateLimit":{"rpm":60},"hideMappedModels":true}""";
            row.UpdatedAt = row.UpdatedAt.AddTicks(1);
        };

        var data = await QueryDataAsync(
            PatchMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { hideOriginalModels = true },
            });

        var settings = data.GetProperty("updateChannelSettings").GetProperty("settings");
        Assert.True(settings.GetProperty("hideOriginalModels").GetBoolean());
        Assert.True(settings.GetProperty("hideMappedModels").GetBoolean());
        Assert.Equal("deepseek", settings.GetProperty("extraModelPrefix").GetString());
        Assert.Equal(60, settings.GetProperty("rateLimit").GetProperty("rpm").GetInt32());

        Assert.Equal(2, _channels.UpdateAttempts);
        var call = Assert.Single(_channels.UpdateCalls);
        Assert.Equal(SeedUpdatedAt.AddTicks(1), call.Update.ExpectedUpdatedAt!.Value);
    }

    [Fact]
    public async Task AConflictedMergeRetriesAtMostEightTimes()
    {
        var channel = SeedChannel(settings: """{"extraModelPrefix":"deepseek"}""");
        _channels.UpdateConflict = true;

        var errors = await QueryErrorsAsync(
            PatchMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { hideOriginalModels = true },
            });

        Assert.Equal(
            "channel was updated concurrently; retry the operation",
            errors[0].GetProperty("message").GetString());
        Assert.Equal(8, _channels.UpdateAttempts);
        Assert.Empty(_channels.UpdateCalls);
    }

    [Fact]
    public async Task TheMergeRunsThroughTheUpdateChannelValidators()
    {
        var channel = SeedChannel(settings: """{"extraModelPrefix":"deepseek"}""");

        var errors = await QueryErrorsAsync(
            PatchMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { rateLimit = new { rpm = -1 } },
            });

        Assert.Equal(
            "invalid rate limit: rpm must be >= 0",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_channels.UpdateCalls);
    }

    [Fact]
    public async Task ACommandCodePatchPreservesTheStoredProviderQuota()
    {
        var channel = SeedChannel(
            type: "commandcode",
            settings: """{"providerQuota":{"commandCode":{"authCookie":"commandcode_prod_.session_token=abc123"}}}""");

        var data = await QueryDataAsync(
            PatchMutation,
            new
            {
                id = Gid(channel.Id),
                input = new { hideOriginalModels = true },
            });

        var settings = data.GetProperty("updateChannelSettings").GetProperty("settings");
        Assert.True(settings.GetProperty("hideOriginalModels").GetBoolean());
        Assert.Equal(
            "commandcode_prod_.session_token=abc123",
            settings.GetProperty("providerQuota")
                .GetProperty("commandCode")
                .GetProperty("authCookie")
                .GetString());

        // The merged block keeps a non-blank cookie, so no quota invalidation.
        Assert.Empty(_quotaInvalidator.ChannelIds);
    }

    [Fact]
    public async Task APresentEmptyApiKeyStrategyIsStoredAsAbsent()
    {
        // Port difference C250: Go keeps the non-nil *string (stored as ""),
        // while the port collapses a present empty value to absent on both the
        // full update and the merge.
        var channel = SeedChannel(settings: """{"apiKeyStrategy":"round_robin"}""");

        var data = await QueryDataAsync(
            PatchMutation,
            new { id = Gid(channel.Id), input = new { apiKeyStrategy = string.Empty } });

        var settings = data.GetProperty("updateChannelSettings").GetProperty("settings");
        Assert.Equal(
            JsonValueKind.Null,
            settings.GetProperty("apiKeyStrategy").ValueKind);

        var stored = ParseStoredSettings(channel);
        Assert.False(stored.TryGetProperty("apiKeyStrategy", out _));
    }

    [Fact]
    public async Task SettingQuotaRoutingModeToInheritClearsTheStoredMode()
    {
        var channel = SeedChannel(settings: """{"quotaRoutingMode":"ignore_quota"}""");

        var data = await QueryDataAsync(
            PatchMutation,
            new { id = Gid(channel.Id), input = new { quotaRoutingMode = "INHERIT" } });

        var settings = data.GetProperty("updateChannelSettings").GetProperty("settings");
        Assert.Equal("INHERIT", settings.GetProperty("quotaRoutingMode").GetString());

        var stored = ParseStoredSettings(channel);
        Assert.False(stored.TryGetProperty("quotaRoutingMode", out _));
    }

    [Fact]
    public async Task AMissingChannelKeepsTheReferenceMessage()
    {
        var errors = await QueryErrorsAsync(
            PatchMutation,
            new { id = Gid(424242), input = new { hideOriginalModels = true } });

        Assert.Equal(
            "failed to load channel settings: ent: channel not found",
            errors[0].GetProperty("message").GetString());
        Assert.Equal(0, _channels.UpdateAttempts);
    }

    [Fact]
    public async Task ASettingsReadFailureKeepsTheReferencePrefix()
    {
        _channels.GetSettingsVersionFailure = new StoreFailureException(
            "failed to load channel settings: boom",
            new InvalidOperationException("boom"));

        var errors = await QueryErrorsAsync(
            PatchMutation,
            new { id = Gid(1), input = new { hideOriginalModels = true } });

        Assert.Equal(
            "failed to load channel settings: boom",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task MalformedStoredSettingsFailTheRead()
    {
        var channel = SeedChannel(settings: "{");

        var errors = await QueryErrorsAsync(
            PatchMutation,
            new { id = Gid(channel.Id), input = new { hideOriginalModels = true } });

        Assert.StartsWith(
            "failed to load channel settings: ",
            errors[0].GetProperty("message").GetString());
        Assert.Equal(0, _channels.UpdateAttempts);
    }

    [Fact]
    public async Task MalformedGuidsAreRejectedBeforeTheService()
    {
        var errors = await QueryErrorsAsync(
            PatchMutation,
            new { id = "3", input = new { hideOriginalModels = true } });

        Assert.Equal(
            "guid must start with gid://axonhub/",
            errors[0].GetProperty("message").GetString());
        Assert.Equal(0, _channels.UpdateAttempts);
    }

    [Fact]
    public async Task TheInputGuidTypeIsNotValidated()
    {
        var channel = SeedChannel(settings: """{"extraModelPrefix":"deepseek"}""");

        var data = await QueryDataAsync(
            PatchMutation,
            new
            {
                id = "gid://axonhub/Whatever/" + channel.Id,
                input = new { hideOriginalModels = true },
            });

        Assert.Equal(
            Gid(channel.Id),
            data.GetProperty("updateChannelSettings").GetProperty("id").GetString());
    }

    [Fact]
    public async Task IntrospectionExposesTheMutationAndItsReferenceInput()
    {
        var data = await QueryDataAsync(
            """
            {
              __schema { mutationType { fields { name } } }
              input: __type(name: "UpdateChannelSettingsInput") {
                inputFields { name }
              }
            }
            """);

        var mutations = data.GetProperty("__schema").GetProperty("mutationType")
            .GetProperty("fields").EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Contains("updateChannelSettings", mutations);

        var fields = data.GetProperty("input").GetProperty("inputFields")
            .EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[]
            {
                "apiKeyRoundRobinSwitchAfter",
                "apiKeyStrategy",
                "autoTrimedModelPrefixes",
                "bodyOverrideOperations",
                "extraModelPrefix",
                "headerOverrideOperations",
                "hideMappedModels",
                "hideOriginalModels",
                "lowercaseModelId",
                "modelMappings",
                "modelProtocols",
                "passThroughBody",
                "passThroughUserAgent",
                "providerQuota",
                "proxy",
                "quotaRoutingMode",
                "rateLimit",
                "retryableErrorPatterns",
                "retryableStatusCodes",
                "transformOptions",
            },
            fields);
    }

    private static readonly DateTime SeedUpdatedAt =
        new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    private static string Gid(long id) => "gid://axonhub/Channel/" + id;

    private static JsonElement ParseStoredSettings(ChannelRecord channel)
    {
        using var document = JsonDocument.Parse(channel.Settings!);
        return document.RootElement.Clone();
    }

    private ChannelRecord SeedChannel(
        string type = "openai",
        string? baseUrl = "https://api.example/v1",
        string? settings = null)
    {
        var record = new ChannelRecord
        {
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = SeedUpdatedAt,
            DeletedAt = 0,
            Type = type,
            BaseUrl = baseUrl,
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
        return [.. errors.EnumerateArray().Select(static error => error.Clone())];
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
