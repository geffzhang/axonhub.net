using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Models;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the Model GraphQL surface over /admin/graphql against the queries and
/// mutations the frontend sends (frontend/src/features/models): gid IDs, the
/// ModelCard/ModelSettings JSON shape, associatedChannelCount, the ported
/// where predicates, and the reference mutation semantics from
/// internal/server/biz/model.go.
/// </summary>
public sealed class AdminModelsEndpointTests : IAsyncLifetime
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private const string CardJson =
        """{"reasoning":{"supported":true,"default":false},"reasoningEfforts":["low","high"],"toolCall":true,"temperature":true,"modalities":{"input":["text"],"output":["text"]},"vision":true,"cost":{"input":1.5,"output":2.5,"cacheRead":0.5,"cacheWrite":0.25},"limit":{"context":128000,"output":32000},"knowledge":"2025-01","releaseDate":"2025-01-01","lastUpdated":"2025-02-01"}""";

    private const string SettingsJson =
        """{"disableDeveloperSettingsInheritance":true,"associations":[{"type":"channel_model","priority":1,"disabled":false,"channelModel":{"channelId":1,"modelId":"gpt-4o"}}],"loadBalancerStrategy":"default","traceStickyMode":"default"}""";

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeModelStore _models = new();
    private readonly FakeChannelAdminStore _channels = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IUserAdminStore>(_users);
        builder.Services.AddSingleton<IRoleStore>(_roles);
        builder.Services.AddSingleton<IModelStore>(_models);
        builder.Services.AddSingleton<IChannelAdminStore>(_channels);
        builder.Services.AddSingleton<IModelSettingsProvider>(
            new StaticModelSettingsProvider(
                ModelSettings.Default with { QueryAllChannelModels = false }));
        builder.Services.AddScoped<PermissionValidator>();
        builder.Services.AddScoped<ModelPrivacy>();
        builder.Services.AddScoped<AuthorizationScope>();
        builder.Services.AddSingleton(
            new ModelFetcher(
                new ScriptedExecutor(),
                new FakeChannelModelCatalogStore(),
                TimeProvider.System));
        builder.Services.AddScoped<ModelManagementService>();
        _users.RolesSource = _roles;

        await _users.CreateAsync(
            new UserRecord
            {
                Id = AdminGraphqlGateServices.UserId,
                Email = "gate-user@example.com",
                Status = "activated",
                IsOwner = true,
            },
            roleIds: null);

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
    public async Task ModelsQueryMatchesTheFrontendShape()
    {
        var model = _models.Seed(record =>
        {
            record.Developer = "openai";
            record.ModelId = "gpt-4o";
            record.Type = "embedding";
            record.Name = "GPT-4o";
            record.Icon = "openai";
            record.Group = "default";
            record.ModelCard = CardJson;
            record.Settings = SettingsJson;
            record.Status = "enabled";
            record.Remark = "hello";
            record.CreatedAt = CreatedAt;
            record.UpdatedAt = UpdatedAt;
        });
        _channels.Seed(new ChannelRecord
        {
            Id = 1,
            Name = "channel-1",
            Type = "openai",
            BaseUrl = "https://provider.test/v1",
            Status = "enabled",
            Credentials = """{"apiKey":"test-key"}""",
            SupportedModels = """["gpt-4o"]""",
        });

        var data = await QueryDataAsync(
            """
            query Models($first: Int) {
              models(first: $first) {
                totalCount
                pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                edges {
                  cursor
                  node {
                    id
                    createdAt
                    updatedAt
                    developer
                    modelID
                    type
                    name
                    icon
                    group
                    status
                    remark
                    associatedChannelCount
                    modelCard {
                      reasoning { supported default }
                      reasoningEfforts
                      toolCall
                      temperature
                      modalities { input output }
                      vision
                      cost { input output cacheRead cacheWrite }
                      limit { context output }
                      knowledge
                      releaseDate
                      lastUpdated
                    }
                    settings {
                      disableDeveloperSettingsInheritance
                      loadBalancerStrategy
                      traceStickyMode
                      associations { type priority disabled }
                    }
                  }
                }
              }
            }
            """,
            new { first = 10 });

        var connection = data.GetProperty("models");
        Assert.Equal(1, connection.GetProperty("totalCount").GetInt32());
        var pageInfo = connection.GetProperty("pageInfo");
        Assert.False(pageInfo.GetProperty("hasNextPage").GetBoolean());
        Assert.False(pageInfo.GetProperty("hasPreviousPage").GetBoolean());

        var edge = Assert.Single(connection.GetProperty("edges").EnumerateArray());
        Assert.False(string.IsNullOrEmpty(edge.GetProperty("cursor").GetString()));
        var node = edge.GetProperty("node");

        Assert.Equal("gid://axonhub/Model/" + model.Id, node.GetProperty("id").GetString());
        Assert.Equal("2026-01-02T03:04:05Z", node.GetProperty("createdAt").GetString());
        Assert.Equal("2026-02-03T04:05:06Z", node.GetProperty("updatedAt").GetString());
        Assert.Equal("openai", node.GetProperty("developer").GetString());
        Assert.Equal("gpt-4o", node.GetProperty("modelID").GetString());
        Assert.Equal("embedding", node.GetProperty("type").GetString());
        Assert.Equal("GPT-4o", node.GetProperty("name").GetString());
        Assert.Equal("default", node.GetProperty("group").GetString());
        Assert.Equal("enabled", node.GetProperty("status").GetString());
        Assert.Equal("hello", node.GetProperty("remark").GetString());
        Assert.Equal(1, node.GetProperty("associatedChannelCount").GetInt32());

        var card = node.GetProperty("modelCard");
        Assert.True(card.GetProperty("reasoning").GetProperty("supported").GetBoolean());
        Assert.Equal(
            ["low", "high"],
            card.GetProperty("reasoningEfforts").EnumerateArray()
                .Select(static item => item.GetString()!).ToArray());
        Assert.True(card.GetProperty("toolCall").GetBoolean());
        Assert.True(card.GetProperty("vision").GetBoolean());
        Assert.Equal(
            ["text"],
            card.GetProperty("modalities").GetProperty("input").EnumerateArray()
                .Select(static item => item.GetString()!).ToArray());
        Assert.Equal(1.5, card.GetProperty("cost").GetProperty("input").GetDouble());
        Assert.Equal(0.25, card.GetProperty("cost").GetProperty("cacheWrite").GetDouble());
        Assert.Equal(128000, card.GetProperty("limit").GetProperty("context").GetInt32());
        Assert.Equal("2025-01", card.GetProperty("knowledge").GetString());

        var settings = node.GetProperty("settings");
        Assert.True(settings.GetProperty("disableDeveloperSettingsInheritance").GetBoolean());
        Assert.Equal("default", settings.GetProperty("loadBalancerStrategy").GetString());
        Assert.Equal("default", settings.GetProperty("traceStickyMode").GetString());
        var association = Assert.Single(settings.GetProperty("associations").EnumerateArray());
        Assert.Equal("channel_model", association.GetProperty("type").GetString());
        Assert.Equal(1, association.GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task ModelsWhereFilterCombinesOrBranches()
    {
        var alpha = _models.Seed(record =>
        {
            record.Developer = "openai";
            record.ModelId = "gpt-4o";
            record.Name = "Alpha";
        });
        var beta = _models.Seed(record =>
        {
            record.Developer = "openai";
            record.ModelId = "gpt-4o-mini";
            record.Name = "Beta";
        });
        _models.Seed(record =>
        {
            record.Developer = "anthropic";
            record.ModelId = "claude";
            record.Name = "Gamma";
        });

        var data = await QueryDataAsync(
            """
            query Models($where: ModelWhereInput) {
              models(first: 10, where: $where) { totalCount edges { node { id } } }
            }
            """,
            new
            {
                where = new
                {
                    or = new object[]
                    {
                        new { nameContainsFold = "ALPH" },
                        new { modelIDContainsFold = "MINI" },
                    },
                },
            });

        var ids = data.GetProperty("models").GetProperty("edges").EnumerateArray()
            .Select(static edge => edge.GetProperty("node").GetProperty("id").GetString()!)
            .ToArray();
        Assert.Equal(
            ["gid://axonhub/Model/" + alpha.Id, "gid://axonhub/Model/" + beta.Id],
            ids);

        // The port renders an empty or-list as an always-false predicate (C177).
        var empty = await QueryDataAsync(
            """
            query Models($where: ModelWhereInput) {
              models(first: 10, where: $where) { totalCount edges { node { id } } }
            }
            """,
            new { where = new { or = Array.Empty<object>() } });

        Assert.Equal(0, empty.GetProperty("models").GetProperty("totalCount").GetInt32());
        Assert.Empty(empty.GetProperty("models").GetProperty("edges").EnumerateArray());
    }

    [Fact]
    public async Task CreateModelAppliesEntDefaults()
    {
        var modalitiesInput = new[] { "text" };

        var data = await QueryDataAsync(
            """
            mutation Create($input: CreateModelInput!) {
              createModel(input: $input) {
                id
                modelID
                type
                status
                remark
                modelCard { reasoning { supported } vision cost { input } modalities { input } }
                settings { loadBalancerStrategy traceStickyMode associations { type } }
              }
            }
            """,
            new
            {
                input = new
                {
                    developer = "openai",
                    modelID = "gpt-4o",
                    name = "GPT-4o",
                    icon = "openai",
                    group = "default",
                    modelCard = new
                    {
                        reasoning = new { supported = true },
                        cost = new { input = 0.5 },
                        modalities = new { input = modalitiesInput },
                    },
                    settings = new
                    {
                        loadBalancerStrategy = "",
                        traceStickyMode = "system_default",
                        associations = Array.Empty<object>(),
                    },
                    remark = (string?)null,
                },
            });

        var model = data.GetProperty("createModel");
        Assert.StartsWith("gid://axonhub/Model/", model.GetProperty("id").GetString());
        Assert.Equal("gpt-4o", model.GetProperty("modelID").GetString());
        Assert.Equal("chat", model.GetProperty("type").GetString());
        Assert.Equal("disabled", model.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, model.GetProperty("remark").ValueKind);

        var card = model.GetProperty("modelCard");
        Assert.True(card.GetProperty("reasoning").GetProperty("supported").GetBoolean());
        Assert.False(card.GetProperty("vision").GetBoolean());
        Assert.Equal(0.5, card.GetProperty("cost").GetProperty("input").GetDouble());
        Assert.Equal(
            ["text"],
            card.GetProperty("modalities").GetProperty("input").EnumerateArray()
                .Select(static item => item.GetString()!).ToArray());

        var settings = model.GetProperty("settings");
        Assert.Equal("default", settings.GetProperty("loadBalancerStrategy").GetString());
        Assert.Equal("default", settings.GetProperty("traceStickyMode").GetString());
        Assert.Empty(settings.GetProperty("associations").EnumerateArray());

        Assert.Single(_models.Rows);
    }

    [Fact]
    public async Task UpdateModelAppliesFieldsAndClearsRemark()
    {
        var model = _models.Seed(record =>
        {
            record.Developer = "openai";
            record.ModelId = "gpt-4o";
            record.Name = "Before";
            record.Remark = "note";
        });

        var data = await QueryDataAsync(
            """
            mutation Update($id: ID!, $input: UpdateModelInput!) {
              updateModel(id: $id, input: $input) { id name remark status }
            }
            """,
            new
            {
                id = Gid(model.Id),
                input = new
                {
                    name = "After",
                    status = "enabled",
                    clearRemark = true,
                },
            });

        var updated = data.GetProperty("updateModel");
        Assert.Equal("After", updated.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("remark").ValueKind);
        Assert.Equal("enabled", updated.GetProperty("status").GetString());

        var missing = await QueryErrorsAsync(
            """
            mutation Update($id: ID!, $input: UpdateModelInput!) {
              updateModel(id: $id, input: $input) { id }
            }
            """,
            new { id = Gid(404), input = new { name = "nope" } });

        Assert.Equal(
            "failed to update model: ent: model not found",
            missing[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task UpdateModelStatusAndBulkMutationsChangeEveryRow()
    {
        var first = _models.Seed(record =>
        {
            record.Developer = "openai";
            record.ModelId = "gpt-4o";
            record.Name = "Alpha";
        });
        var second = _models.Seed(record =>
        {
            record.Developer = "openai";
            record.ModelId = "gpt-4o-mini";
            record.Name = "Beta";
        });

        var status = await QueryDataAsync(
            """
            mutation Status($id: ID!, $status: ModelStatus!) {
              updateModelStatus(id: $id, status: $status)
            }
            """,
            new { id = Gid(first.Id), status = "enabled" });
        Assert.True(status.GetProperty("updateModelStatus").GetBoolean());
        Assert.Equal("enabled", first.Status);

        var archived = await QueryDataAsync(
            """
            mutation Archive($ids: [ID!]!) { bulkArchiveModels(ids: $ids) }
            """,
            new { ids = new[] { Gid(first.Id), Gid(second.Id) } });
        Assert.True(archived.GetProperty("bulkArchiveModels").GetBoolean());
        Assert.Equal("archived", first.Status);
        Assert.Equal("archived", second.Status);

        var enabled = await QueryDataAsync(
            """
            mutation Enable($ids: [ID!]!) { bulkEnableModels(ids: $ids) }
            """,
            new { ids = new[] { Gid(second.Id) } });
        Assert.True(enabled.GetProperty("bulkEnableModels").GetBoolean());
        Assert.Equal("enabled", second.Status);

        var disabled = await QueryDataAsync(
            """
            mutation Disable($ids: [ID!]!) { bulkDisableModels(ids: $ids) }
            """,
            new { ids = new[] { Gid(second.Id) } });
        Assert.True(disabled.GetProperty("bulkDisableModels").GetBoolean());
        Assert.Equal("disabled", second.Status);

        var deleted = await QueryDataAsync(
            """
            mutation Delete($ids: [ID!]!) { bulkDeleteModels(ids: $ids) }
            """,
            new { ids = new[] { Gid(first.Id) } });
        Assert.True(deleted.GetProperty("bulkDeleteModels").GetBoolean());
        Assert.NotEqual(0, first.DeletedAt);
        Assert.Equal(0, second.DeletedAt);
    }

    [Fact]
    public async Task DeleteModelReturnsBooleanAndReportsNotFound()
    {
        var model = _models.Seed(record =>
        {
            record.Developer = "openai";
            record.ModelId = "gpt-4o";
            record.Name = "Alpha";
        });

        var data = await QueryDataAsync(
            """
            mutation Delete($id: ID!) { deleteModel(id: $id) }
            """,
            new { id = Gid(model.Id) });

        Assert.True(data.GetProperty("deleteModel").GetBoolean());
        Assert.NotEqual(0, model.DeletedAt);

        var errors = await QueryErrorsAsync(
            """
            mutation Delete($id: ID!) { deleteModel(id: $id) }
            """,
            new { id = Gid(model.Id) });

        Assert.Equal(
            "failed to delete model: ent: model not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task NodeResolverReturnsTheModel()
    {
        var model = _models.Seed(record =>
        {
            record.Developer = "openai";
            record.ModelId = "gpt-4o";
            record.Name = "Alpha";
        });

        var data = await QueryDataAsync(
            """
            query Node($id: ID!) {
              node(id: $id) { id ... on Model { modelID developer } }
            }
            """,
            new { id = Gid(model.Id) });

        var node = data.GetProperty("node");
        Assert.Equal(Gid(model.Id), node.GetProperty("id").GetString());
        Assert.Equal("gpt-4o", node.GetProperty("modelID").GetString());
        Assert.Equal("openai", node.GetProperty("developer").GetString());
    }

    [Fact]
    public async Task ModelsValidateFirstAndLast()
    {
        // The reference resolver does not call validatePaginationArgs, but
        // ent's generated ModelQuery.Paginate calls validateFirstLast before
        // paging (gql_pagination.go:2817-2824), so both checks surface here.
        var errors = await QueryErrorsAsync(
            """
            query Models { models(first: 1, last: 1) { edges { node { id } } } }
            """);

        Assert.Equal(
            "Passing both `first` and `last` to paginate a connection is not supported.",
            errors[0].GetProperty("message").GetString());

        var negative = await QueryErrorsAsync(
            """
            query Models { models(first: -1) { edges { node { id } } } }
            """);

        Assert.Equal(
            "`first` on a connection cannot be less than zero.",
            negative[0].GetProperty("message").GetString());
    }

    private static string Gid(long id) => "gid://axonhub/Model/" + id;

    private async Task<JsonElement> QueryDataAsync(string query, object? variables = null)
    {
        var root = await ExecuteAsync(query, variables);
        Assert.False(root.TryGetProperty("errors", out var errors), errors.ToString());
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
