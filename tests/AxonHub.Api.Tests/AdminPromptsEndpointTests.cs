using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Prompts;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the Prompt GraphQL surface over /admin/graphql against the queries and
/// mutations the frontend sends (frontend/src/features/prompts): gid IDs, the
/// PromptSettings JSON shape, the ported where predicates, the entgql
/// pagination rules and the reference mutation semantics from
/// internal/server/biz/prompt.go.
/// </summary>
public sealed class AdminPromptsEndpointTests : IAsyncLifetime
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private const string SettingsJson =
        """{"action":{"type":"append"},"conditions":[{"conditions":[{"type":"model_pattern","model_pattern":"gpt-*"}]}]}""";

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly FakePromptStore _prompts = new();

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
        builder.Services.AddSingleton<IPromptStore>(_prompts);
        builder.Services.AddScoped<PermissionValidator>();
        builder.Services.AddScoped<ApiKeyPrivacy>();
        builder.Services.AddScoped<PromptPrivacy>();
        builder.Services.AddScoped<PromptManagementService>();
        _users.RolesSource = _roles;

        // The gate's JWT user (id 9, owner) is the caller every fixture
        // client sends; the privacy policies see it as an owner.
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
    public async Task PromptsQueryMatchesTheFrontendShape()
    {
        var prompt = _prompts.Seed(record =>
        {
            record.Name = "greeting";
            record.Description = "draws a greeting";
            record.Role = "system";
            record.Content = "Hello!";
            record.Status = "enabled";
            record.Order = 3;
            record.Settings = SettingsJson;
            record.ProjectId = 1;
            record.CreatedAt = CreatedAt;
            record.UpdatedAt = UpdatedAt;
        });
        _gate.Projects.Add(project =>
        {
            project.Id = 1;
            project.Name = "project";
            project.Status = "active";
        });

        var data = await QueryDataAsync(
            """
            query Prompts($first: Int) {
              prompts(first: $first) {
                totalCount
                pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                edges {
                  cursor
                  node {
                    id
                    createdAt
                    updatedAt
                    projectID
                    name
                    description
                    role
                    content
                    status
                    order
                    settings {
                      action { type }
                      conditions { conditions { type modelId modelPattern apiKeyId } }
                    }
                    project { id name }
                  }
                }
              }
            }
            """,
            new { first = 10 });

        var connection = data.GetProperty("prompts");
        Assert.Equal(1, connection.GetProperty("totalCount").GetInt32());
        var pageInfo = connection.GetProperty("pageInfo");
        Assert.False(pageInfo.GetProperty("hasNextPage").GetBoolean());
        Assert.False(pageInfo.GetProperty("hasPreviousPage").GetBoolean());

        var edge = Assert.Single(connection.GetProperty("edges").EnumerateArray());
        Assert.False(string.IsNullOrEmpty(edge.GetProperty("cursor").GetString()));
        var node = edge.GetProperty("node");

        Assert.Equal("gid://axonhub/Prompt/" + prompt.Id, node.GetProperty("id").GetString());
        Assert.Equal("2026-01-02T03:04:05Z", node.GetProperty("createdAt").GetString());
        Assert.Equal("2026-02-03T04:05:06Z", node.GetProperty("updatedAt").GetString());
        Assert.Equal("gid://axonhub/Project/1", node.GetProperty("projectID").GetString());
        Assert.Equal("greeting", node.GetProperty("name").GetString());
        Assert.Equal("draws a greeting", node.GetProperty("description").GetString());
        Assert.Equal("system", node.GetProperty("role").GetString());
        Assert.Equal("Hello!", node.GetProperty("content").GetString());
        Assert.Equal("enabled", node.GetProperty("status").GetString());
        Assert.Equal(3, node.GetProperty("order").GetInt32());

        var settings = node.GetProperty("settings");
        Assert.Equal("append", settings.GetProperty("action").GetProperty("type").GetString());
        var condition = Assert.Single(
            Assert.Single(settings.GetProperty("conditions").EnumerateArray())
                .GetProperty("conditions").EnumerateArray());
        Assert.Equal("model_pattern", condition.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, condition.GetProperty("modelId").ValueKind);
        Assert.Equal("gpt-*", condition.GetProperty("modelPattern").GetString());
        Assert.Equal(JsonValueKind.Null, condition.GetProperty("apiKeyId").ValueKind);

        Assert.Equal(
            "gid://axonhub/Project/1",
            node.GetProperty("project").GetProperty("id").GetString());
    }

    [Fact]
    public async Task PromptsWhereAndOrderDriveKeysetPagination()
    {
        var first = _prompts.Seed(record =>
        {
            record.Name = "Alpha";
            record.Order = 2;
            record.Status = "enabled";
        });
        var second = _prompts.Seed(record =>
        {
            record.Name = "Beta";
            record.Order = 1;
            record.Status = "enabled";
        });
        _prompts.Seed(record =>
        {
            record.Name = "Gamma";
            record.Order = 3;
            record.Status = "disabled";
        });

        var variables = new
        {
            where = new { nameContainsFold = "a" },
            orderBy = new { field = "ORDER", direction = "ASC" },
        };
        var data = await QueryDataAsync(
            """
            query Prompts($where: PromptWhereInput, $orderBy: PromptOrder) {
              prompts(first: 2, where: $where, orderBy: $orderBy) {
                totalCount
                pageInfo { hasNextPage hasPreviousPage endCursor }
                edges { cursor node { id name } }
              }
            }
            """,
            variables);

        var connection = data.GetProperty("prompts");
        Assert.Equal(3, connection.GetProperty("totalCount").GetInt32());
        Assert.True(connection.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());

        var ids = connection.GetProperty("edges").EnumerateArray()
            .Select(static edge => edge.GetProperty("node").GetProperty("id").GetString()!)
            .ToArray();
        Assert.Equal(["gid://axonhub/Prompt/" + second.Id, "gid://axonhub/Prompt/" + first.Id], ids);

        var after = await QueryDataAsync(
            """
            query Prompts($where: PromptWhereInput, $orderBy: PromptOrder, $after: Cursor) {
              prompts(first: 2, where: $where, orderBy: $orderBy, after: $after) {
                totalCount
                pageInfo { hasNextPage hasPreviousPage }
                edges { node { name } }
              }
            }
            """,
            new
            {
                where = variables.where,
                orderBy = variables.orderBy,
                after = connection.GetProperty("pageInfo").GetProperty("endCursor").GetString(),
            });

        var next = Assert.Single(
            after.GetProperty("prompts").GetProperty("edges").EnumerateArray());
        Assert.Equal("Gamma", next.GetProperty("node").GetProperty("name").GetString());
        Assert.False(after.GetProperty("prompts").GetProperty("pageInfo")
            .GetProperty("hasNextPage").GetBoolean());
        Assert.True(after.GetProperty("prompts").GetProperty("pageInfo")
            .GetProperty("hasPreviousPage").GetBoolean());
    }

    [Fact]
    public async Task CreatedAtOrderFallsBackToTheDefaultIdOrder()
    {
        var first = _prompts.Seed(record => record.Name = "first");
        var second = _prompts.Seed(record => record.Name = "second");

        var data = await QueryDataAsync(
            """
            query Prompts {
              prompts(first: 10, orderBy: { field: CREATED_AT, direction: DESC }) {
                edges { cursor node { id } }
              }
            }
            """);

        // DefaultPromptOrder is id; DESC reverses it. The cursors carry no
        // sort value because the default field has none.
        var ids = data.GetProperty("prompts").GetProperty("edges").EnumerateArray()
            .Select(static edge => edge.GetProperty("node").GetProperty("id").GetString()!)
            .ToArray();
        Assert.Equal(["gid://axonhub/Prompt/" + second.Id, "gid://axonhub/Prompt/" + first.Id], ids);
    }

    [Fact]
    public async Task InvalidPaginationIsRejectedLikeTheReference()
    {
        var errors = await QueryErrorsAsync(
            """
            query Prompts { prompts(first: 10, last: 10) { totalCount } }
            """);

        Assert.Equal(
            "Passing both `first` and `last` to paginate a connection is not supported.",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task CreatePromptSerializesSettingsAndAppliesDefaults()
    {
        var data = await QueryDataAsync(
            """
            mutation Create($input: CreatePromptInput!) {
              createPrompt(input: $input) {
                id
                projectID
                name
                description
                role
                content
                status
                order
                settings { action { type } conditions { conditions { type apiKeyId } } }
              }
            }
            """,
            new
            {
                input = new
                {
                    name = "greeting",
                    role = "system",
                    content = "Hello",
                    settings = new
                    {
                        action = new { type = "prepend" },
                        conditions = new[]
                        {
                            new
                            {
                                conditions = new[]
                                {
                                    new { type = "api_key", apiKeyId = 7 },
                                },
                            },
                        },
                    },
                },
            });

        var created = data.GetProperty("createPrompt");
        Assert.StartsWith("gid://axonhub/Prompt/", created.GetProperty("id").GetString());
        Assert.Equal("gid://axonhub/Project/1", created.GetProperty("projectID").GetString());
        Assert.Equal("", created.GetProperty("description").GetString());
        Assert.Equal("disabled", created.GetProperty("status").GetString());
        Assert.Equal(0, created.GetProperty("order").GetInt32());

        var settings = created.GetProperty("settings");
        Assert.Equal("prepend", settings.GetProperty("action").GetProperty("type").GetString());
        var condition = Assert.Single(
            Assert.Single(settings.GetProperty("conditions").EnumerateArray())
                .GetProperty("conditions").EnumerateArray());
        Assert.Equal("api_key", condition.GetProperty("type").GetString());
        Assert.Equal(7, condition.GetProperty("apiKeyId").GetInt32());

        var stored = Assert.Single(_prompts.Rows);
        Assert.Equal(
            """{"action":{"type":"prepend"},"conditions":[{"conditions":[{"type":"api_key","api_key_id":7}]}]}""",
            stored.Settings);
        Assert.Equal("Hello", stored.Content);
    }

    [Fact]
    public async Task CreateDuplicateNameReturnsTheCodedErrorExtensions()
    {
        _prompts.Seed(record => record.Name = "dup");

        var errors = await QueryErrorsAsync(
            """
            mutation Create($input: CreatePromptInput!) {
              createPrompt(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    name = "dup",
                    role = "system",
                    content = "Hello",
                    settings = new { action = new { type = "append" } },
                },
            });

        var error = errors[0];
        Assert.Equal("prompt name 'dup' already exists", error.GetProperty("message").GetString());
        var extensions = error.GetProperty("extensions");
        Assert.Equal("DUPLICATE_NAME", extensions.GetProperty("code").GetString());
        Assert.Equal("prompt", extensions.GetProperty("resource").GetString());
        Assert.Equal("name", extensions.GetProperty("field").GetString());
        Assert.Equal("dup", extensions.GetProperty("value").GetString());

        Assert.Single(_prompts.Rows);
    }

    [Fact]
    public async Task UpdateStatusAndBulkMutationsChangeEveryRow()
    {
        var prompt = _prompts.Seed(record =>
        {
            record.Name = "before";
            record.Content = "before-content";
        });
        var other = _prompts.Seed(record => record.Name = "other");

        var updated = await QueryDataAsync(
            """
            mutation Update($id: ID!, $input: UpdatePromptInput!) {
              updatePrompt(id: $id, input: $input) {
                id name content description order
              }
            }
            """,
            new
            {
                id = Gid(prompt.Id),
                input = new
                {
                    name = "after",
                    content = "after-content",
                    description = "desc",
                    order = 9,
                },
            });

        var node = updated.GetProperty("updatePrompt");
        Assert.Equal("after", node.GetProperty("name").GetString());
        Assert.Equal("after-content", node.GetProperty("content").GetString());
        Assert.Equal("desc", node.GetProperty("description").GetString());
        Assert.Equal(9, node.GetProperty("order").GetInt32());
        Assert.Equal("after", prompt.Name);

        var status = await QueryDataAsync(
            """
            mutation Status($id: ID!, $status: PromptStatus!) {
              updatePromptStatus(id: $id, status: $status)
            }
            """,
            new { id = Gid(prompt.Id), status = "enabled" });
        Assert.True(status.GetProperty("updatePromptStatus").GetBoolean());
        Assert.Equal("enabled", prompt.Status);

        var disabled = await QueryDataAsync(
            """
            mutation Disable($ids: [ID!]!) { bulkDisablePrompts(ids: $ids) }
            """,
            new { ids = new[] { Gid(prompt.Id), Gid(other.Id) } });
        Assert.True(disabled.GetProperty("bulkDisablePrompts").GetBoolean());
        Assert.Equal("disabled", prompt.Status);
        Assert.Equal("disabled", other.Status);

        var enabled = await QueryDataAsync(
            """
            mutation Enable($ids: [ID!]!) { bulkEnablePrompts(ids: $ids) }
            """,
            new { ids = new[] { Gid(other.Id) } });
        Assert.True(enabled.GetProperty("bulkEnablePrompts").GetBoolean());
        Assert.Equal("enabled", other.Status);

        var deleted = await QueryDataAsync(
            """
            mutation Delete($ids: [ID!]!) { bulkDeletePrompts(ids: $ids) }
            """,
            new { ids = new[] { Gid(prompt.Id) } });
        Assert.True(deleted.GetProperty("bulkDeletePrompts").GetBoolean());
        Assert.NotEqual(0, prompt.DeletedAt);
        Assert.Equal(0, other.DeletedAt);
    }

    [Fact]
    public async Task DeletePromptReturnsBooleanAndReportsAlreadyDeletedRows()
    {
        var prompt = _prompts.Seed(record => record.Name = "gone");

        var data = await QueryDataAsync(
            """
            mutation Delete($id: ID!) { deletePrompt(id: $id) }
            """,
            new { id = Gid(prompt.Id) });

        Assert.True(data.GetProperty("deletePrompt").GetBoolean());
        Assert.NotEqual(0, prompt.DeletedAt);

        var errors = await QueryErrorsAsync(
            """
            mutation Delete($id: ID!) { deletePrompt(id: $id) }
            """,
            new { id = Gid(prompt.Id) });

        Assert.Equal(
            "prompt not found or not in project",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task NodeResolverReturnsThePrompt()
    {
        var prompt = _prompts.Seed(record => record.Name = "greeting");

        var data = await QueryDataAsync(
            """
            query Node($id: ID!) {
              node(id: $id) { id ... on Prompt { name status } }
            }
            """,
            new { id = Gid(prompt.Id) });

        var node = data.GetProperty("node");
        Assert.Equal(Gid(prompt.Id), node.GetProperty("id").GetString());
        Assert.Equal("greeting", node.GetProperty("name").GetString());
        Assert.Equal("disabled", node.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ServiceAccountApiKeyHasNoPromptAccess()
    {
        // Prompt has no API-key rule: the principal has no user in context.
        _prompts.Seed(record => record.Name = "greeting");
        _gate.AddServiceAccountKey();

        var errors = await QueryErrorsAsync(
            """
            query Prompts { prompts(first: 10) { totalCount } }
            """,
            token: "sk-service-account");

        // The presenter maps a privacy deny to the reference's FORBIDDEN error.
        Assert.Equal("permission denied", errors[0].GetProperty("message").GetString());
    }

    private static string Gid(long id) => "gid://axonhub/Prompt/" + id;

    private async Task<JsonElement> QueryDataAsync(string query, object? variables = null)
    {
        var root = await ExecuteAsync(query, variables, token: null);
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
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/admin/graphql")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query, variables }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            token ?? _gate.Jwt);

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
