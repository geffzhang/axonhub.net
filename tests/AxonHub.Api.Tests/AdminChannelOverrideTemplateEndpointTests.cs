using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the channel override template GraphQL surface over /admin/graphql
/// against the reference SDL and resolvers (ent.graphql:967-1043,
/// axonhub.graphql:905-930, axonhub.resolvers.go:644-708): gid IDs, the Time
/// and Cursor wire formats, legacy-field fallbacks, the apply/clear payloads
/// and the reference error strings.
/// </summary>
public sealed class AdminChannelOverrideTemplateEndpointTests : IAsyncLifetime
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly string[] Channel10Ids = ["gid://axonhub/Channel/10"];

    private readonly FakeChannelOverrideTemplateStore _templates = new();
    private readonly RecordingModelSyncReloadHook _reload = new();
    private readonly FakeUserStore _users = new();
    private readonly AdminGraphqlGateServices _gate = new(isOwner: false);

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IChannelOverrideTemplateStore>(_templates);
        builder.Services.AddSingleton<IChannelModelSyncReloadHook>(_reload);
        builder.Services.AddSingleton<ChannelOverrideTemplateService>();

        // The fixture's own user store wins over the gate's default, so the
        // gate user the JWT resolves to (id 9, non-owner - the template
        // viewer this suite locks) lives here. The template rows share the id
        // and the user{id email} resolver reads this row.
        builder.Services.AddSingleton<IUserStore>(_users);
        _users.Rows.Add(new UserRecord
        {
            Id = 9,
            Email = "owner@example.com",
            Status = "activated",
            PreferLanguage = "en",
            Password = "hashed",
            IsOwner = false,
        });

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
    public async Task TemplatesQueryReturnsTheFrontendFieldsAndLegacyFallbacks()
    {
        _templates.Templates.Add(Template(
            id: 1,
            name: "Operations",
            headerOverrideOperations:
                """[{"op":"set","path":"x","value":"v"},{"op":"rename","from":"a","to":"b"}]""",
            bodyOverrideOperations: """[{"op":"delete","path":"top_p"}]"""));
        _templates.Templates.Add(Template(
            id: 2,
            name: "Legacy",
            headerOverrideOperations: "null",
            bodyOverrideOperations: "null",
            overrideHeaders: """[{"key":"User-Agent","value":"AxonHub"}]""",
            overrideParameters: """{"temperature":0.7,"remove":"__AXONHUB_CLEAR__"}"""));

        var data = await QueryDataAsync(
            """
            query Templates($first: Int, $orderBy: ChannelOverrideTemplateOrder) {
              channelOverrideTemplates(first: $first, orderBy: $orderBy) {
                edges {
                  cursor
                  node {
                    id createdAt updatedAt userID name description
                    overrideParameters
                    overrideHeaders { key value }
                    headerOverrideOperations {
                      op path from to value condition
                      match { path eq }
                      index splat
                    }
                    bodyOverrideOperations { op path value }
                    user { id email }
                  }
                }
                pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                totalCount
              }
            }
            """,
            new { first = 10, orderBy = new { field = "CREATED_AT", direction = "ASC" } });

        var connection = data.GetProperty("channelOverrideTemplates");
        var edges = connection.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(2, edges.Length);
        Assert.Equal(2, connection.GetProperty("totalCount").GetInt32());

        Assert.Equal(
            "gid://axonhub/ChannelOverrideTemplate/1",
            edges[0].GetProperty("node").GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/ChannelOverrideTemplate/2",
            edges[1].GetProperty("node").GetProperty("id").GetString());

        var first = edges[0].GetProperty("node");
        Assert.Equal("2026-01-02T03:04:05Z", first.GetProperty("createdAt").GetString());
        Assert.Equal("gid://axonhub/User/9", first.GetProperty("userID").GetString());
        Assert.Equal("Operations", first.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("description").ValueKind);
        Assert.Equal("{}", first.GetProperty("overrideParameters").GetString());
        Assert.Empty(first.GetProperty("overrideHeaders").EnumerateArray().ToArray());

        var headerOps = first.GetProperty("headerOverrideOperations").EnumerateArray().ToArray();
        Assert.Equal(2, headerOps.Length);
        Assert.Equal("set", headerOps[0].GetProperty("op").GetString());
        Assert.Equal("x", headerOps[0].GetProperty("path").GetString());
        Assert.Equal("v", headerOps[0].GetProperty("value").GetString());
        Assert.Equal("rename", headerOps[1].GetProperty("op").GetString());
        Assert.Equal("a", headerOps[1].GetProperty("from").GetString());
        Assert.Equal("b", headerOps[1].GetProperty("to").GetString());

        var bodyOps = first.GetProperty("bodyOverrideOperations").EnumerateArray().ToArray();
        Assert.Equal("delete", Assert.Single(bodyOps).GetProperty("op").GetString());
        Assert.Equal("top_p", bodyOps[0].GetProperty("path").GetString());

        var user = first.GetProperty("user");
        Assert.Equal("gid://axonhub/User/9", user.GetProperty("id").GetString());
        Assert.Equal("owner@example.com", user.GetProperty("email").GetString());

        // The new fields are empty, so the legacy columns are converted.
        var legacy = edges[1].GetProperty("node");
        var legacyHeaders = legacy.GetProperty("headerOverrideOperations").EnumerateArray().ToArray();
        Assert.Equal(
            "User-Agent",
            Assert.Single(legacyHeaders).GetProperty("path").GetString());
        Assert.Equal("AxonHub", legacyHeaders[0].GetProperty("value").GetString());
        Assert.Equal("User-Agent", legacy.GetProperty("overrideHeaders")[0].GetProperty("key").GetString());

        var legacyBody = legacy.GetProperty("bodyOverrideOperations").EnumerateArray().ToArray();
        Assert.Equal(2, legacyBody.Length);
        Assert.Equal("set", legacyBody[0].GetProperty("op").GetString());
        Assert.Equal("temperature", legacyBody[0].GetProperty("path").GetString());
        Assert.Equal("0.7", legacyBody[0].GetProperty("value").GetString());
        Assert.Equal("delete", legacyBody[1].GetProperty("op").GetString());
        Assert.Equal("remove", legacyBody[1].GetProperty("path").GetString());

        var pageInfo = connection.GetProperty("pageInfo");
        Assert.False(pageInfo.GetProperty("hasNextPage").GetBoolean());
        Assert.False(pageInfo.GetProperty("hasPreviousPage").GetBoolean());
        Assert.Equal(1, CursorCodec.Decode(pageInfo.GetProperty("startCursor").GetString()!).Id);
        Assert.Equal(2, CursorCodec.Decode(pageInfo.GetProperty("endCursor").GetString()!).Id);
    }

    [Fact]
    public async Task TemplatesQueryOrdersByUpdatedAtAndFiltersNames()
    {
        _templates.Templates.Add(Template(
            id: 1,
            name: "Alpha",
            updatedAt: CreatedAt.AddMinutes(2)));
        _templates.Templates.Add(Template(
            id: 2,
            name: "Beta",
            updatedAt: CreatedAt.AddMinutes(1)));

        var data = await QueryDataAsync(
            """
            query Templates(
              $first: Int,
              $orderBy: ChannelOverrideTemplateOrder,
              $where: ChannelOverrideTemplateWhereInput) {
              channelOverrideTemplates(first: $first, orderBy: $orderBy, where: $where) {
                edges { cursor node { name } }
                pageInfo { hasNextPage hasPreviousPage }
                totalCount
              }
            }
            """,
            new
            {
                first = 1,
                orderBy = new { field = "UPDATED_AT", direction = "DESC" },
                where = new { nameContainsFold = "A" },
            });

        var connection = data.GetProperty("channelOverrideTemplates");
        var node = Assert.Single(connection.GetProperty("edges").EnumerateArray());
        Assert.Equal("Alpha", node.GetProperty("node").GetProperty("name").GetString());
        Assert.Equal(2, connection.GetProperty("totalCount").GetInt32());
        Assert.True(connection.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());

        var captured = Assert.Single(_templates.Queries);
        Assert.Equal(ChannelOverrideTemplateSortField.UpdatedAt, captured.SortField);
        Assert.Equal(SortDirection.Desc, captured.Direction);
        Assert.Equal("A", captured.Where?.NameContainsFold);

        // UpdatedAt ordering puts the value column in the cursor.
        var cursor = CursorCodec.Decode(node.GetProperty("cursor").GetString()!);
        Assert.Equal(1, cursor.Id);
        Assert.Equal(CreatedAt.AddMinutes(2), Assert.IsType<DateTime>(cursor.Value));
    }

    [Fact]
    public async Task TemplatesQueryRequiresFirstOrLast()
    {
        // entgql's validatePaginationArgs rejects an unbounded connection; the
        // frontend always sends first.
        var errors = await QueryErrorsAsync(
            "{ channelOverrideTemplates { edges { node { id } } } }");

        Assert.Equal(
            "either first or last must be provided",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task TemplatesQueryRejectsFirstAndLastTogether()
    {
        var errors = await QueryErrorsAsync(
            "{ channelOverrideTemplates(first: 1, last: 1) { edges { node { id } } } }");

        Assert.Equal(
            "Passing both `first` and `last` to paginate a connection is not supported.",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task SchemaExposesTheTemplateSurfaceAndWhereSubset()
    {
        var data = await QueryDataAsync(
            """
            {
              where: __type(name: "ChannelOverrideTemplateWhereInput") {
                inputFields { name }
              }
              schema: __schema {
                queryType { fields { name args { name } } }
                mutationType { fields { name } }
              }
            }
            """);

        // C162: only the predicates the management frontend sends.
        var whereFields = data.GetProperty("where").GetProperty("inputFields")
            .EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            ["and", "id", "idIn", "name", "nameContains", "nameContainsFold", "nameIn", "not", "or"],
            whereFields);

        var queryFields = data.GetProperty("schema").GetProperty("queryType")
            .GetProperty("fields").EnumerateArray()
            .ToDictionary(
                static field => field.GetProperty("name").GetString()!,
                static field => field.GetProperty("args")
                    .EnumerateArray()
                    .Select(static arg => arg.GetProperty("name").GetString())
                    .ToArray(),
                StringComparer.Ordinal);
        Assert.Equal(
            ["after", "before", "first", "last", "orderBy", "where"],
            queryFields["channelOverrideTemplates"].OrderBy(
                static name => name,
                StringComparer.Ordinal));

        var mutationFields = data.GetProperty("schema").GetProperty("mutationType")
            .GetProperty("fields").EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("createChannelOverrideTemplate", mutationFields);
        Assert.Contains("updateChannelOverrideTemplate", mutationFields);
        Assert.Contains("deleteChannelOverrideTemplate", mutationFields);
        Assert.Contains("applyChannelOverrideTemplate", mutationFields);
        Assert.Contains("clearChannelOverrideTemplates", mutationFields);
    }

    [Fact]
    public async Task TemplatesQueryWithoutAUserReturnsThePrivacyDenial()
    {
        // A service-account API key reaches the resolvers with no user in
        // context; the template privacy rule then denies the read.
        _gate.AddServiceAccountKey();
        using var client = _app.GetTestClient();
        AdminGraphqlGateServices.Authorize(client, "sk-service-account");

        var errors = await QueryErrorsAsync(
            client,
            "{ channelOverrideTemplates(first: 10) { edges { node { id } } } }");

        Assert.Equal(
            "no user in context: ent/privacy: deny rule",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task CreateMutationStoresAndReturnsTheTemplate()
    {
        var data = await MutateAsync(
            """
            mutation Create($input: CreateChannelOverrideTemplateInput!) {
              createChannelOverrideTemplate(input: $input) {
                id createdAt updatedAt userID name description
                overrideParameters
                overrideHeaders { key value }
                headerOverrideOperations { op path value }
                bodyOverrideOperations { op path value }
              }
            }
            """,
            new
            {
                input = new
                {
                    name = "Headers",
                    description = "desc",
                    headerOverrideOperations = new[]
                    {
                        new { op = "set", path = "x-key", value = "v" },
                    },
                    bodyOverrideOperations = new[]
                    {
                        new { op = "delete", path = "top_p" },
                    },
                },
            });

        var created = data.GetProperty("createChannelOverrideTemplate");
        Assert.Equal(
            "gid://axonhub/ChannelOverrideTemplate/" + _templates.Templates[0].Id,
            created.GetProperty("id").GetString());
        Assert.Equal("Headers", created.GetProperty("name").GetString());
        Assert.Equal("desc", created.GetProperty("description").GetString());
        Assert.Equal("gid://axonhub/User/9", created.GetProperty("userID").GetString());
        Assert.Equal("{}", created.GetProperty("overrideParameters").GetString());
        Assert.Empty(created.GetProperty("overrideHeaders").EnumerateArray().ToArray());

        var headerOps = Assert.Single(
            created.GetProperty("headerOverrideOperations").EnumerateArray());
        Assert.Equal("set", headerOps.GetProperty("op").GetString());
        Assert.Equal("x-key", headerOps.GetProperty("path").GetString());
        Assert.Equal("v", headerOps.GetProperty("value").GetString());
        var bodyOps = Assert.Single(
            created.GetProperty("bodyOverrideOperations").EnumerateArray());
        Assert.Equal("delete", bodyOps.GetProperty("op").GetString());
        Assert.Equal("top_p", bodyOps.GetProperty("path").GetString());

        // The stored columns carry the ent JSON shape.
        Assert.Equal(
            """[{"op":"set","path":"x-key","value":"v"}]""",
            _templates.Templates[0].HeaderOverrideOperations);
        Assert.Equal(
            """[{"op":"delete","path":"top_p"}]""",
            _templates.Templates[0].BodyOverrideOperations);
    }

    [Fact]
    public async Task CreateMutationWithoutAUserIsRefusedAsReadOnly()
    {
        // A service-account API key has no user in context, but the read-only
        // rule (apiKeyReadOnly) runs before the resolver chain, so the mutation
        // never reaches the "user not found in context" branch.
        _gate.AddServiceAccountKey();
        using var client = _app.GetTestClient();
        AdminGraphqlGateServices.Authorize(client, "sk-service-account");

        var errors = await MutateErrorsAsync(
            client,
            """
            mutation Create($input: CreateChannelOverrideTemplateInput!) {
              createChannelOverrideTemplate(input: $input) { id }
            }
            """,
            new { input = new { name = "Headers" } });

        Assert.Equal(
            "service account API keys are read-only",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_templates.Templates);
    }

    [Fact]
    public async Task CreateMutationDuplicateNameCarriesTheCodedExtensions()
    {
        _templates.Templates.Add(Template(id: 1, name: "Shared"));

        var errors = await MutateErrorsAsync(
            """
            mutation Create($input: CreateChannelOverrideTemplateInput!) {
              createChannelOverrideTemplate(input: $input) { id }
            }
            """,
            new { input = new { name = "Shared" } });

        var error = errors[0];
        Assert.Equal("Template name 'Shared' already exists", error.GetProperty("message").GetString());
        var extensions = error.GetProperty("extensions");
        Assert.Equal("DUPLICATE_NAME", extensions.GetProperty("code").GetString());
        Assert.Equal("Template", extensions.GetProperty("resource").GetString());
        Assert.Equal("name", extensions.GetProperty("field").GetString());
        Assert.Equal("Shared", extensions.GetProperty("value").GetString());
    }

    [Fact]
    public async Task UpdateMutationAppliesSetAppendAndClearDescription()
    {
        _templates.Templates.Add(Template(
            id: 1,
            name: "Original",
            description: "old",
            headerOverrideOperations: """[{"op":"set","path":"a","value":"1"}]"""));

        var data = await MutateAsync(
            """
            mutation Update($id: ID!, $input: UpdateChannelOverrideTemplateInput!) {
              updateChannelOverrideTemplate(id: $id, input: $input) {
                id name description
                headerOverrideOperations { op path value }
                bodyOverrideOperations { op path value }
              }
            }
            """,
            new
            {
                id = "gid://axonhub/ChannelOverrideTemplate/1",
                input = new
                {
                    name = "Renamed",
                    description = "ignored",
                    clearDescription = true,
                    appendHeaderOverrideOperations = new[]
                    {
                        new { op = "delete", path = "b" },
                    },
                    bodyOverrideOperations = new[]
                    {
                        new { op = "set", path = "c", value = "3" },
                    },
                },
            });

        var updated = data.GetProperty("updateChannelOverrideTemplate");
        Assert.Equal("Renamed", updated.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("description").ValueKind);

        var headers = updated.GetProperty("headerOverrideOperations").EnumerateArray().ToArray();
        Assert.Equal(2, headers.Length);
        Assert.Equal("a", headers[0].GetProperty("path").GetString());
        Assert.Equal("b", headers[1].GetProperty("path").GetString());
        Assert.Equal("delete", headers[1].GetProperty("op").GetString());

        var body = Assert.Single(updated.GetProperty("bodyOverrideOperations").EnumerateArray());
        Assert.Equal("c", body.GetProperty("path").GetString());
        Assert.Equal("3", body.GetProperty("value").GetString());
    }

    [Fact]
    public async Task DeleteMutationSoftDeletesAndReturnsTrue()
    {
        _templates.Templates.Add(Template(id: 1));

        var data = await MutateAsync(
            """
            mutation Delete($id: ID!) {
              deleteChannelOverrideTemplate(id: $id)
            }
            """,
            new { id = "gid://axonhub/ChannelOverrideTemplate/1" });

        Assert.True(data.GetProperty("deleteChannelOverrideTemplate").GetBoolean());
        Assert.NotEqual(0, _templates.Templates[0].DeletedAt);
    }

    [Fact]
    public async Task DeleteMutationOfAMissingRowReturnsNotFound()
    {
        var errors = await MutateErrorsAsync(
            """
            mutation Delete($id: ID!) {
              deleteChannelOverrideTemplate(id: $id)
            }
            """,
            new { id = "gid://axonhub/ChannelOverrideTemplate/1" });

        Assert.Equal(
            "failed to delete channel override template: "
            + "ent: channel_override_template not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ApplyMutationRewritesChannelSettingsAndReloads()
    {
        _templates.Templates.Add(Template(
            id: 1,
            headerOverrideOperations: """[{"op":"set","path":"x","value":"t"}]""",
            bodyOverrideOperations: "null"));
        _templates.Channels.Add(Channel(10, "Alpha",
            """{"modelMappings":[],"overrideParameters":"{\"a\":\"1\"}"}"""));

        var data = await MutateAsync(
            """
            mutation Apply($input: ApplyChannelOverrideTemplateInput!) {
              applyChannelOverrideTemplate(input: $input) {
                success
                updated
                channels { id name }
              }
            }
            """,
            new
            {
                input = new
                {
                    templateID = "gid://axonhub/ChannelOverrideTemplate/1",
                    channelIDs = Channel10Ids,
                    mode = "REPLACE",
                },
            });

        var payload = data.GetProperty("applyChannelOverrideTemplate");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal(1, payload.GetProperty("updated").GetInt32());
        var channel = Assert.Single(payload.GetProperty("channels").EnumerateArray());
        Assert.Equal("gid://axonhub/Channel/10", channel.GetProperty("id").GetString());
        Assert.Equal("Alpha", channel.GetProperty("name").GetString());
        Assert.Equal(1, _reload.Count);

        var settings = JsonDocument.Parse(Assert.Single(_templates.Writes).SettingsJson);
        Assert.Equal(
            """[{"op":"set","path":"x","value":"t"}]""",
            settings.RootElement.GetProperty("headerOverrideOperations").GetRawText());
        Assert.Equal("", settings.RootElement.GetProperty("overrideParameters").GetString());
    }

    [Fact]
    public async Task ApplyMutationErrorsCarryTheResolverPrefix()
    {
        _templates.Templates.Add(Template(id: 1));

        var errors = await MutateErrorsAsync(
            """
            mutation Apply($input: ApplyChannelOverrideTemplateInput!) {
              applyChannelOverrideTemplate(input: $input) { success }
            }
            """,
            new
            {
                input = new
                {
                    templateID = "gid://axonhub/ChannelOverrideTemplate/1",
                    channelIDs = Channel10Ids,
                },
            });

        Assert.Equal(
            "failed to apply template: some channels not found for provided IDs",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ClearMutationRemovesTheOverrideFields()
    {
        _templates.Channels.Add(Channel(10, "Alpha",
            """
            {
              "modelMappings": [],
              "headerOverrideOperations": [{"op":"set","path":"x","value":"1"}],
              "overrideHeaders": [{"key":"x","value":"1"}]
            }
            """));

        var data = await MutateAsync(
            """
            mutation Clear($input: ClearChannelOverrideTemplatesInput!) {
              clearChannelOverrideTemplates(input: $input) {
                success
                updated
                channels { id name }
              }
            }
            """,
            new
            {
                input = new
                {
                    channelIDs = Channel10Ids,
                },
            });

        var payload = data.GetProperty("clearChannelOverrideTemplates");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal(1, payload.GetProperty("updated").GetInt32());
        Assert.Equal(1, _reload.Count);

        var settings = JsonDocument.Parse(Assert.Single(_templates.Writes).SettingsJson);
        Assert.False(settings.RootElement.TryGetProperty("headerOverrideOperations", out _));
        Assert.Equal(
            JsonValueKind.Null,
            settings.RootElement.GetProperty("overrideHeaders").ValueKind);
        Assert.Equal("", settings.RootElement.GetProperty("overrideParameters").GetString());
    }

    [Fact]
    public async Task ClearMutationErrorsCarryTheResolverPrefix()
    {
        var errors = await MutateErrorsAsync(
            """
            mutation Clear($input: ClearChannelOverrideTemplatesInput!) {
              clearChannelOverrideTemplates(input: $input) { success }
            }
            """,
            new
            {
                input = new
                {
                    channelIDs = Channel10Ids,
                },
            });

        Assert.Equal(
            "failed to clear templates: some channels not found for provided IDs",
            errors[0].GetProperty("message").GetString());
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static ChannelOverrideTemplateRecord Template(
        long id,
        string name = "Template",
        DateTime? updatedAt = null,
        string? headerOverrideOperations = "null",
        string? bodyOverrideOperations = "null",
        string overrideHeaders = "[]",
        string overrideParameters = "{}",
        string? description = null) => new()
    {
        Id = id,
        CreatedAt = CreatedAt,
        UpdatedAt = updatedAt ?? CreatedAt,
        DeletedAt = 0,
        Name = name,
        Description = description,
        OverrideParameters = overrideParameters,
        OverrideHeaders = overrideHeaders,
        HeaderOverrideOperations = headerOverrideOperations,
        BodyOverrideOperations = bodyOverrideOperations,
        UserId = 9,
    };

    private static ChannelRecord Channel(long id, string name, string settings) => new()
    {
        Id = id,
        CreatedAt = CreatedAt,
        UpdatedAt = CreatedAt,
        DeletedAt = 0,
        Type = "openai",
        Name = name,
        Status = "enabled",
        Credentials = "{}",
        DisabledApiKeys = "[]",
        SupportedModels = "null",
        Tags = "[]",
        Policies = """{"stream":"unlimited"}""",
        Settings = settings,
        Endpoints = "[]",
    };

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null)
    {
        var root = await ExecuteAsync(query, variables);
        Assert.False(root.TryGetProperty("errors", out _), root.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(
        string query,
        object? variables = null)
        => await QueryErrorsAsync(_client, query, variables);

    private async Task<JsonElement[]> QueryErrorsAsync(
        HttpClient client,
        string query,
        object? variables = null)
    {
        var root = await ExecuteAsync(client, query, variables);
        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        return errors.EnumerateArray().Select(static error => error.Clone()).ToArray();
    }

    private async Task<JsonElement> MutateAsync(string query, object? variables)
        => await QueryDataAsync(query, variables);

    private async Task<JsonElement[]> MutateErrorsAsync(string query, object? variables)
        => await QueryErrorsAsync(_client, query, variables);

    private async Task<JsonElement[]> MutateErrorsAsync(
        HttpClient client,
        string query,
        object? variables)
        => await QueryErrorsAsync(client, query, variables);

    private async Task<JsonElement> ExecuteAsync(string query, object? variables)
        => await ExecuteAsync(_client, query, variables);

    private async Task<JsonElement> ExecuteAsync(
        HttpClient client,
        string query,
        object? variables)
    {
        var payload = JsonSerializer.Serialize(new { query, variables, operationName = (string?)null });
        using var response = await client.PostAsync(
            "/admin/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
