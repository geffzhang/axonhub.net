using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// Locks duplicateChannel, bulkCreateChannels and bulkImportChannels against
/// the reference resolvers and biz.ChannelService
/// (axonhub.resolvers.go:178-186, 374-388; channel_duplicate.go:17-84;
/// channel_bulk.go:67-161, 484-568): duplicate inherits endpoints and the
/// zenmux management key before the name check and copies prices inside the
/// create transaction, bulk create resolves the price plan once and numbers
/// the generated names around live rows, and bulk import keeps going after a
/// failed row with that row's raw error text.
/// </summary>
public sealed class AdminChannelDuplicateBulkEndpointTests : IAsyncLifetime
{
    private const string DuplicateMutation =
        """
        mutation Duplicate($sourceID: ID!, $input: CreateChannelInput!) {
          duplicateChannel(sourceID: $sourceID, input: $input) {
            id name status type baseURL supportedModels tags
            endpoints { apiFormat path baseURL transport }
          }
        }
        """;

    private const string BulkCreateMutation =
        """
        mutation Bulk($input: BulkCreateChannelsInput!) {
          bulkCreateChannels(input: $input) {
            id name status type baseURL supportedModels tags credentials { apiKey apiKeys }
          }
        }
        """;

    private const string BulkImportMutation =
        """
        mutation Import($input: BulkImportChannelsInput!) {
          bulkImportChannels(input: $input) {
            success created failed errors
            channels { id name type status supportedModels tags credentials { apiKey apiKeys } }
          }
        }
        """;

    private static readonly string[] SingleApiKey = ["sk-1"];
    private static readonly string[] TwoApiKeys = ["sk-1", "sk-2"];
    private static readonly string[] Gpt4oModels = ["gpt-4o"];
    private static readonly string[] TwoTags = ["a", "b"];

    private readonly FakeChannelWriteStore _channels = new();
    private readonly ChannelLimiterManager _limiters = new();
    private readonly FakeChannelModelPriceStore _prices = new();

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
    public async Task DuplicateChannelInheritsEndpointsAndTheZenmuxManagementKey()
    {
        var source = SeedChannel(
            "Zen Source",
            type: "zenmux",
            credentials: """{"managementApiKey":"mgmt-1"}""",
            endpoints: """[{"api_format":"openai/chat_completions","path":"/v1/chat"}]""");

        var data = await QueryDataAsync(DuplicateMutation, new
        {
            sourceID = Gid(source.Id),
            input = CreateInput("Copy", type: "zenmux"),
        });

        var node = data.GetProperty("duplicateChannel");
        Assert.Equal("Copy", node.GetProperty("name").GetString());
        Assert.Equal("zenmux", node.GetProperty("type").GetString());
        Assert.Equal("disabled", node.GetProperty("status").GetString());

        // The source's custom endpoints are inherited (channel_duplicate.go:27-29).
        var endpoint = Assert.Single(node.GetProperty("endpoints").EnumerateArray());
        Assert.Equal("openai/chat_completions", endpoint.GetProperty("apiFormat").GetString());
        Assert.Equal("/v1/chat", endpoint.GetProperty("path").GetString());

        var created = Assert.Single(_channels.CreatedChannels);
        Assert.Equal("Copy", created.Name);

        // Both sides are zenmux and the input key is empty, so the source
        // management key is copied (channel_duplicate.go:30-32).
        Assert.Equal("""{"managementApiKey":"mgmt-1"}""", created.Credentials);

        var copy = Assert.Single(_prices.CopyCalls);
        Assert.Equal((source.Id, created.Id), copy);
        var ensure = Assert.Single(_prices.EnsureCalls);
        Assert.Equal(created.Id, ensure.ChannelId);
    }

    [Fact]
    public async Task DuplicateChannelKeepsCallerEndpointsAndCredentials()
    {
        var source = SeedChannel(
            "Zen Source",
            type: "zenmux",
            credentials: """{"managementApiKey":"mgmt-1"}""",
            endpoints: """[{"api_format":"openai/chat_completions","path":"/source"}]""");

        var data = await QueryDataAsync(DuplicateMutation, new
        {
            sourceID = Gid(source.Id),
            input = new
            {
                type = "zenmux",
                name = "Copy",
                credentials = new { managementApiKey = "mgmt-2" },
                supportedModels = Array.Empty<string>(),
                defaultTestModel = string.Empty,
                endpoints = new[] { new { apiFormat = "anthropic/messages", path = "/v1/messages" } },
            },
        });

        var endpoint = Assert.Single(
            data.GetProperty("duplicateChannel").GetProperty("endpoints").EnumerateArray());
        Assert.Equal("anthropic/messages", endpoint.GetProperty("apiFormat").GetString());
        Assert.Equal("/v1/messages", endpoint.GetProperty("path").GetString());

        var created = Assert.Single(_channels.CreatedChannels);
        Assert.Equal("""{"managementApiKey":"mgmt-2"}""", created.Credentials);
    }

    [Fact]
    public async Task DuplicateChannelChecksTheNameBeforeValidation()
    {
        var source = SeedChannel("Source");
        SeedChannel("Taken");

        var errors = await QueryErrorsAsync(DuplicateMutation, new
        {
            sourceID = Gid(source.Id),
            input = new
            {
                type = "openai",
                name = "Taken",
                credentials = new { },
                supportedModels = Array.Empty<string>(),
                defaultTestModel = string.Empty,
                settings = new { rateLimit = new { rpm = -1 } },
            },
        });

        // The live-name check runs before createChannel's validators
        // (channel_duplicate.go:34-45), so the duplicate wins over the
        // otherwise invalid settings.
        Assert.Equal("channel name 'Taken' already exists", errors[0].GetProperty("message").GetString());
        Assert.Equal(
            "DUPLICATE_NAME",
            errors[0].GetProperty("extensions").GetProperty("code").GetString());
        Assert.Empty(_channels.CreatedChannels);
        Assert.Empty(_prices.CopyCalls);
        Assert.Empty(_prices.EnsureCalls);
    }

    [Fact]
    public async Task DuplicateChannelSurfacesValidationErrorsAfterAFreeName()
    {
        var source = SeedChannel("Source");

        var errors = await QueryErrorsAsync(DuplicateMutation, new
        {
            sourceID = Gid(source.Id),
            input = new
            {
                type = "openai",
                name = "Copy",
                credentials = new { },
                supportedModels = Array.Empty<string>(),
                defaultTestModel = string.Empty,
                settings = new { rateLimit = new { rpm = -1 } },
            },
        });

        Assert.Equal(
            "invalid rate limit: rpm must be >= 0",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_channels.CreatedChannels);
        Assert.Empty(_prices.CopyCalls);
        Assert.Empty(_prices.EnsureCalls);
    }

    [Fact]
    public async Task DuplicateChannelOnAMissingSourceKeepsTheReferenceMessage()
    {
        var errors = await QueryErrorsAsync(DuplicateMutation, new
        {
            sourceID = Gid(424242),
            input = CreateInput("Copy"),
        });

        Assert.Equal(
            "failed to get source channel: ent: channel not found",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_channels.CreatedChannels);
    }

    [Fact]
    public async Task DuplicateChannelRollsBackTheInsertWhenThePriceCopyFails()
    {
        var source = SeedChannel("Source");
        _prices.CopyFailure = new StoreFailureException(
            "failed to query source channel model prices: boom",
            new InvalidOperationException("boom"));

        var errors = await QueryErrorsAsync(DuplicateMutation, new
        {
            sourceID = Gid(source.Id),
            input = CreateInput("Copy"),
        });

        Assert.Equal(
            "failed to query source channel model prices: boom",
            errors[0].GetProperty("message").GetString());

        // The whole transaction rolls back: neither the channel nor the ensure
        // (which follows the copy) happens.
        Assert.Empty(_channels.CreatedChannels);
        Assert.Empty(_prices.EnsureCalls);
    }

    [Fact]
    public async Task BulkCreateNumbersNamesAroundLiveRowsAndDefaultsTagsToTheBaseName()
    {
        SeedChannel("Pool - (2)");
        _prices.Templates = [new ChannelModelPriceTemplate("gpt-4o", """{"prompt":1}""")];

        var data = await QueryDataAsync(BulkCreateMutation, new
        {
            input = new
            {
                type = "openai",
                name = "Pool",
                baseURL = "https://api.example/v1",
                apiKeys = TwoApiKeys,
                supportedModels = Gpt4oModels,
                defaultTestModel = "gpt-4o",
            },
        });

        var nodes = data.GetProperty("bulkCreateChannels").EnumerateArray().ToArray();
        Assert.Equal(
            ["Pool - (1)", "Pool - (3)"],
            nodes.Select(static node => node.GetProperty("name").GetString()!).ToArray());

        // An empty tag list falls back to the base name
        // (channel_bulk.go:95-98), and the second channel resumes the shared
        // counter after the skipped live name.
        var created = _channels.CreatedChannels.ToArray();
        Assert.Equal(2, created.Length);
        Assert.All(created, record => Assert.Equal("""["Pool"]""", record.Tags));
        Assert.Equal("""{"apiKeys":["sk-1"]}""", created[0].Credentials);
        Assert.Equal("""{"apiKeys":["sk-2"]}""", created[1].Credentials);

        // The price plan is resolved once and applied to every channel.
        var resolved = Assert.Single(_prices.ResolveCalls);
        Assert.Equal(["gpt-4o"], resolved);
        Assert.Equal(2, _prices.ApplyCalls.Count);
        Assert.All(
            _prices.ApplyCalls,
            call => Assert.Equal(_prices.Templates, call.Templates));
        Assert.Equal(created.Select(static record => record.Id), _prices.ApplyCalls.Select(static call => call.ChannelId));
    }

    [Fact]
    public async Task BulkCreateUsesProvidedTagsAndSharesSettingsAcrossTheBatch()
    {
        var data = await QueryDataAsync(BulkCreateMutation, new
        {
            input = new
            {
                type = "openai",
                name = "Pool",
                baseURL = "https://api.example/v1",
                apiKeys = TwoApiKeys,
                supportedModels = Array.Empty<string>(),
                autoSyncSupportedModels = true,
                defaultTestModel = string.Empty,
                tags = TwoTags,
                settings = new { modelMappings = new[] { new { from = "m", to = "n" } } },

                // createChannel never calls SetOrderingWeight, so the column
                // keeps ent's default 0 (C139).
                orderingWeight = 5,
            },
        });

        Assert.Equal(2, data.GetProperty("bulkCreateChannels").GetArrayLength());

        var created = _channels.CreatedChannels.ToArray();
        Assert.All(created, record => Assert.Equal("""["a","b"]""", record.Tags));
        Assert.All(created, record => Assert.True(record.AutoSyncSupportedModels));
        Assert.All(created, record => Assert.Equal(0, record.OrderingWeight));

        // The same settings instance is handed to every createChannel call; the
        // serialized rows therefore agree field for field.
        Assert.Equal(created[0].Settings, created[1].Settings);
        var settings = JsonNode.Parse(created[0].Settings!)!;
        Assert.Equal("m", settings["modelMappings"]![0]!["from"]!.GetValue<string>());
        Assert.Equal("n", settings["modelMappings"]![0]!["to"]!.GetValue<string>());
    }

    [Fact]
    public async Task BulkCreateOnAnEmptyApiKeyListKeepsTheReferenceMessage()
    {
        var errors = await QueryErrorsAsync(BulkCreateMutation, new
        {
            input = new
            {
                type = "openai",
                name = "Pool",
                baseURL = "https://api.example/v1",
                apiKeys = Array.Empty<string>(),
                supportedModels = Array.Empty<string>(),
                defaultTestModel = string.Empty,
            },
        });

        Assert.Equal("no API keys provided", errors[0].GetProperty("message").GetString());
        Assert.Empty(_channels.CreatedChannels);
        Assert.Empty(_prices.ResolveCalls);
    }

    [Fact]
    public async Task BulkCreateWithoutABaseUrlKeepsTheReferenceMessage()
    {
        var errors = await QueryErrorsAsync(BulkCreateMutation, new
        {
            input = new
            {
                type = "openai",
                name = "Pool",
                apiKeys = SingleApiKey,
                supportedModels = Array.Empty<string>(),
                defaultTestModel = string.Empty,
            },
        });

        Assert.Equal("base URL is required", errors[0].GetProperty("message").GetString());
        Assert.Empty(_channels.CreatedChannels);
    }

    [Fact]
    public async Task BulkCreateServiceRejectsAnUnknownWireTypeWithTheEntMessage()
    {
        // The GraphQL enum makes this branch unreachable from the wire, but the
        // service keeps the reference's TypeValidator text for parity
        // (channel_bulk.go:76-78).
        var service = new ChannelLifecycleService(_channels, _limiters, _prices);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BulkCreateChannelsAsync(new BulkCreateChannelsCommand
            {
                Type = "bogus",
                Name = "Pool",
                BaseUrl = "https://api.example/v1",
                ApiKeys = ["sk-1"],
            }));

        Assert.Equal(
            "invalid channel type 'bogus': channel: invalid enum value for type field: \"bogus\"",
            error.Message);
        Assert.Empty(_channels.CreatedChannels);
        Assert.Empty(_prices.ResolveCalls);
    }

    [Fact]
    public async Task BulkCreateResolvesTemplatesThenWrapsTheValidationErrorWithTheName()
    {
        var errors = await QueryErrorsAsync(BulkCreateMutation, new
        {
            input = new
            {
                type = "openai",
                name = "Pool",
                baseURL = "https://api.example/v1",
                apiKeys = SingleApiKey,
                supportedModels = Gpt4oModels,
                defaultTestModel = string.Empty,
                settings = new { rateLimit = new { rpm = -1 } },
            },
        });

        // resolveChannelModelPriceTemplates runs before the first createChannel
        // (channel_bulk.go:100-104); the validation error then carries the
        // generated name (channel_bulk.go:134-137).
        Assert.Equal(
            "failed to create channel 'Pool - (1)': invalid rate limit: rpm must be >= 0",
            errors[0].GetProperty("message").GetString());
        Assert.Single(_prices.ResolveCalls);
        Assert.Empty(_channels.CreatedChannels);
        Assert.Empty(_prices.ApplyCalls);
    }

    [Fact]
    public async Task BulkCreateRollsBackTheWholeBatchWhenApplyingTemplatesFails()
    {
        _prices.ApplyFailure = new StoreFailureException(
            "failed to auto-configure channel model price: model_id=gpt-4o: boom",
            new InvalidOperationException("boom"));

        var errors = await QueryErrorsAsync(BulkCreateMutation, new
        {
            input = new
            {
                type = "openai",
                name = "Pool",
                baseURL = "https://api.example/v1",
                apiKeys = TwoApiKeys,
                supportedModels = Gpt4oModels,
                defaultTestModel = string.Empty,
            },
        });

        Assert.Equal(
            "failed to auto-configure prices for channel 'Pool - (1)': "
                + "failed to auto-configure channel model price: model_id=gpt-4o: boom",
            errors[0].GetProperty("message").GetString());

        // The failed apply aborts the batch and the first insert rolls back.
        Assert.Empty(_channels.CreatedChannels);
        Assert.Single(_prices.ApplyCalls);
    }

    [Fact]
    public async Task BulkImportValidatesRowsInOrderAndStillCreatesTheValidOnes()
    {
        var data = await QueryDataAsync(BulkImportMutation, new
        {
            input = new
            {
                channels = new object[]
                {
                    new { type = "bogus", name = "Bad", baseURL = "https://api.example/v1", apiKey = "k", supportedModels = Array.Empty<string>(), defaultTestModel = string.Empty },
                    new { type = "openai", name = "NoUrl", baseURL = (string?)null, apiKey = "k", supportedModels = Array.Empty<string>(), defaultTestModel = string.Empty },
                    new { type = "openai", name = "NoKey", baseURL = "https://api.example/v1", apiKey = "", supportedModels = Array.Empty<string>(), defaultTestModel = string.Empty },
                    new { type = "openai", name = "Good", baseURL = "https://api.example/v1", apiKey = "sk-good", supportedModels = Gpt4oModels, defaultTestModel = "gpt-4o" },
                },
            },
        });

        var payload = data.GetProperty("bulkImportChannels");
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal(1, payload.GetProperty("created").GetInt32());
        Assert.Equal(3, payload.GetProperty("failed").GetInt32());
        Assert.Equal(
            [
                "Row 1: Invalid channel type 'bogus'",
                "Row 2 (NoUrl): Base URL is required",
                "Row 3 (NoKey): API Key is required",
            ],
            payload.GetProperty("errors").EnumerateArray()
                .Select(static error => error.GetString()!)
                .ToArray());

        var node = Assert.Single(payload.GetProperty("channels").EnumerateArray());
        Assert.Equal("Good", node.GetProperty("name").GetString());
        Assert.Equal("disabled", node.GetProperty("status").GetString());
        // The live credentials resolver keeps apiKey empty for non-OAuth
        // channels and surfaces the keys through apiKeys
        // (axonhub.resolvers.go:58-77).
        var credentials = node.GetProperty("credentials");
        Assert.Equal(string.Empty, credentials.GetProperty("apiKey").GetString());
        Assert.Equal(
            ["sk-good"],
            credentials.GetProperty("apiKeys").EnumerateArray()
                .Select(static key => key.GetString()!)
                .ToArray());

        // The raw ent builder sets only type/name/baseURL/credentials/models/
        // defaultTestModel; every other column takes the schema default
        // (channel_bulk.go:520-527).
        var created = Assert.Single(_channels.CreatedChannels);
        Assert.Equal("""{"apiKey":"sk-good"}""", created.Credentials);
        Assert.Equal("""["gpt-4o"]""", created.SupportedModels);
        Assert.Equal("disabled", created.Status);
        Assert.Equal("[]", created.DisabledApiKeys);
        Assert.Equal("[]", created.ManualModels);
        Assert.Equal("[]", created.Tags);
        Assert.False(created.AutoSyncSupportedModels);
        Assert.Equal(string.Empty, created.AutoSyncModelPattern);
        Assert.Equal("""{"stream":"unlimited"}""", created.Policies);
        Assert.Equal("""{"modelMappings":[]}""", created.Settings);
        Assert.Equal(0, created.OrderingWeight);
        Assert.Equal("[]", created.Endpoints);

        var ensure = Assert.Single(_prices.EnsureCalls);
        Assert.Equal(created.Id, ensure.ChannelId);
        Assert.Equal(["gpt-4o"], ensure.ModelIds);
    }

    [Fact]
    public async Task BulkImportKeepsGoingAfterARowInsertFailure()
    {
        SeedChannel("Dup");
        _channels.ImportNameConflict = true;

        var data = await QueryDataAsync(BulkImportMutation, new
        {
            input = new
            {
                channels = new object[]
                {
                    new { type = "openai", name = "Dup", baseURL = "https://api.example/v1", apiKey = "k", supportedModels = Array.Empty<string>(), defaultTestModel = string.Empty },
                    new { type = "openai", name = "Good", baseURL = "https://api.example/v1", apiKey = "k", supportedModels = Array.Empty<string>(), defaultTestModel = string.Empty },
                },
            },
        });

        var payload = data.GetProperty("bulkImportChannels");
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal(1, payload.GetProperty("created").GetInt32());
        Assert.Equal(1, payload.GetProperty("failed").GetInt32());

        // The failed row appends the raw insert error, no extra prefix
        // (channel_bulk.go:540-544).
        Assert.Equal(
            ["Row 1 (Dup): ent: constraint failed: duplicate channel name"],
            payload.GetProperty("errors").EnumerateArray()
                .Select(static error => error.GetString()!)
                .ToArray());

        Assert.Equal("Good", Assert.Single(_channels.CreatedChannels).Name);
    }

    [Fact]
    public async Task BulkImportReportsEnsureFailuresPerRowAndCreatesNothing()
    {
        _prices.EnsureFailure = new StoreFailureException(
            "failed to auto-configure channel model price: model_id=gpt-4o: boom",
            new InvalidOperationException("boom"));

        var data = await QueryDataAsync(BulkImportMutation, new
        {
            input = new
            {
                channels = new object[]
                {
                    new { type = "openai", name = "Good", baseURL = "https://api.example/v1", apiKey = "k", supportedModels = Gpt4oModels, defaultTestModel = string.Empty },
                },
            },
        });

        var payload = data.GetProperty("bulkImportChannels");
        Assert.False(payload.GetProperty("success").GetBoolean());
        Assert.Equal(0, payload.GetProperty("created").GetInt32());
        Assert.Equal(1, payload.GetProperty("failed").GetInt32());
        Assert.Equal(
            [
                "Row 1 (Good): failed to auto-configure channel model price: "
                    + "model_id=gpt-4o: boom",
            ],
            payload.GetProperty("errors").EnumerateArray()
                .Select(static error => error.GetString()!)
                .ToArray());

        // The row transaction rolled back.
        Assert.Empty(payload.GetProperty("channels").EnumerateArray().ToArray());
        Assert.Empty(_channels.CreatedChannels);
    }

    [Fact]
    public async Task BulkImportAllSuccessReportsNullErrors()
    {
        var data = await QueryDataAsync(BulkImportMutation, new
        {
            input = new
            {
                channels = new object[]
                {
                    new { type = "openai", name = "One", baseURL = "https://api.example/v1", apiKey = "k1", supportedModels = Array.Empty<string>(), defaultTestModel = string.Empty },
                    new { type = "anthropic", name = "Two", baseURL = "https://api.anthropic.com", apiKey = "k2", supportedModels = Array.Empty<string>(), defaultTestModel = string.Empty },
                },
            },
        });

        var payload = data.GetProperty("bulkImportChannels");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal(2, payload.GetProperty("created").GetInt32());
        Assert.Equal(0, payload.GetProperty("failed").GetInt32());

        // Go's nil slice marshals as null, while the non-null channels list is
        // [] or the created rows (axonhub.graphql:422-428).
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("errors").ValueKind);
        Assert.Equal(2, payload.GetProperty("channels").GetArrayLength());
        Assert.Equal(2, _channels.ImportCreateCalls.Count);
    }

    [Fact]
    public async Task IntrospectionExposesTheDuplicateAndBulkChannelMutations()
    {
        var data = await QueryDataAsync(
            """
            {
              __schema { mutationType { fields { name } } }
              duplicate: __type(name: "CreateChannelInput") { inputFields { name } }
              bulkCreate: __type(name: "BulkCreateChannelsInput") { inputFields { name } }
              bulkImport: __type(name: "BulkImportChannelsInput") { inputFields { name } }
              importItem: __type(name: "BulkImportChannelItem") { inputFields { name } }
              importResult: __type(name: "BulkImportChannelsResult") { fields { name } }
            }
            """);

        var mutations = data.GetProperty("__schema").GetProperty("mutationType")
            .GetProperty("fields").EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Contains("duplicateChannel", mutations);
        Assert.Contains("bulkCreateChannels", mutations);
        Assert.Contains("bulkImportChannels", mutations);

        Assert.Equal(
            [
                "type", "baseURL", "name", "credentials", "supportedModels",
                "manualModels", "autoSyncSupportedModels", "autoSyncModelPattern",
                "tags", "defaultTestModel", "policies", "settings",
                "orderingWeight", "remark", "endpoints",
            ],
            FieldNames(data.GetProperty("duplicate")));

        Assert.Equal(
            [
                "type", "name", "tags", "baseURL", "apiKeys", "supportedModels",
                "autoSyncSupportedModels", "defaultTestModel", "settings",
                "policies", "orderingWeight", "remark",
            ],
            FieldNames(data.GetProperty("bulkCreate")));

        Assert.Equal(["channels"], FieldNames(data.GetProperty("bulkImport")));
        Assert.Equal(
            ["type", "name", "baseURL", "apiKey", "supportedModels", "defaultTestModel"],
            FieldNames(data.GetProperty("importItem")));
        Assert.Equal(
            ["success", "created", "failed", "errors", "channels"],
            data.GetProperty("importResult").GetProperty("fields").EnumerateArray()
                .Select(static field => field.GetProperty("name").GetString()!)
                .ToArray());
    }

    private static string[] FieldNames(JsonElement type)
        => type.GetProperty("inputFields").EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .ToArray();

    private static string Gid(long id) => "gid://axonhub/Channel/" + id;

    private static object CreateInput(string name, string type = "openai") => new
    {
        type,
        name,
        credentials = new { },
        supportedModels = Array.Empty<string>(),
        defaultTestModel = string.Empty,
    };

    private ChannelRecord SeedChannel(
        string name,
        string type = "openai",
        string credentials = "{}",
        string? endpoints = "[]",
        long deletedAt = 0)
    {
        var record = new ChannelRecord
        {
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            DeletedAt = deletedAt,
            Type = type,
            BaseUrl = "https://api.example/v1",
            Name = name,
            Status = "disabled",
            Credentials = credentials,
            SupportedModels = "[]",
            AutoSyncModelPattern = string.Empty,
            DefaultTestModel = string.Empty,
            Tags = "[]",
            Policies = """{"stream":"unlimited"}""",
            Endpoints = endpoints,
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
        AdminGraphqlHttpResponseAssertions.AssertStatus(response, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
