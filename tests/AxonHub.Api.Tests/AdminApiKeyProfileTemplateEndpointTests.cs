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
/// Locks the API key profile template GraphQL surface over /admin/graphql
/// against the reference SDL and resolvers (ent.graphql:116-177,
/// ent.graphql:1598-1610, axonhub.graphql:551-559, axonhub.resolvers.go:29-32,
/// 731-749): gid IDs, the Time/Cursor wire formats, the profileJSON shape and
/// the create/update/delete/load payloads.
/// </summary>
public sealed class AdminApiKeyProfileTemplateEndpointTests : IAsyncLifetime
{
    private const string ProjectGid = "gid://axonhub/Project/12";

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeApiKeyProfileTemplateStore _templates = new();

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
        builder.Services.AddSingleton<IApiKeyProfileTemplateStore>(_templates);
        builder.Services.AddScoped<ApiKeyPrivacy>();
        builder.Services.AddScoped<ApiKeyProfileTemplatePrivacy>();
        builder.Services.AddScoped<ApiKeyProfileTemplateService>();
        _users.RolesSource = _roles;

        // The JWT user (id 9) is loaded through IUserAdminStore by the privacy
        // policies; it is a system owner, like the admin fixtures.
        await _users.CreateAsync(
            new UserRecord
            {
                Id = AdminGraphqlGateServices.UserId,
                Email = "gate-user@example.com",
                Status = "activated",
                IsOwner = true,
            },
            roleIds: null);

        _gate.Projects.Add(project =>
        {
            project.Id = 12;
            project.Name = "project";
            project.Status = "active";
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
    public async Task TemplatesQueryReturnsTheTemplateFieldsAndLinkedProfileCount()
    {
        var first = _templates.Add(item =>
        {
            item.Name = "Operations";
            item.Description = "first template";
            item.ProjectId = 12;
            item.Profile = ApiKeyProfilesCodec.SerializeProfile(new ApiKeyProfileModel
            {
                Name = "ops",
                ModelMappings = [new ApiKeyModelMapping { From = "old", To = "new" }],
            });
        });
        _templates.Add(item =>
        {
            item.Name = "Empty";
            item.ProjectId = 12;
            item.Profile = null;
        });
        _templates.Add(item =>
        {
            item.Name = "Third";
            item.ProjectId = 12;
        });
        _templates.Add(item =>
        {
            item.Name = "Elsewhere";
            item.ProjectId = 99;
        });

        // One key links the first template, so linkedProfilesCount is 1 there
        // and 0 everywhere else.
        _gate.ApiKeys.Add(key =>
        {
            key.ProjectId = 12;
            key.Profiles = ApiKeyProfilesCodec.Serialize(new ApiKeyProfilesModel
            {
                ActiveProfile = "ops",
                Profiles =
                [
                    new ApiKeyProfileModel
                    {
                        Name = "ops",
                        TemplateId = first.Id,
                        TemplateName = first.Name,
                    },
                ],
            });
        });

        var data = await QueryDataAsync(
            """
            query Templates(
                $first: Int,
                $where: APIKeyProfileTemplateWhereInput,
                $orderBy: APIKeyProfileTemplateOrder,
                $nodeId: ID!
            ) {
              apiKeyProfileTemplates(first: $first, where: $where, orderBy: $orderBy) {
                edges {
                  cursor
                  node {
                    id createdAt updatedAt name description projectID
                    profile {
                      name templateID templateName loadBalanceStrategy traceStickyMode
                      modelMappings { from to }
                    }
                    project { id name }
                    linkedProfilesCount
                  }
                }
                pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                totalCount
              }
              node(id: $nodeId) {
                id
                ... on APIKeyProfileTemplate { name projectID }
              }
            }
            """,
            new
            {
                first = 10,
                where = new
                {
                    hasProject = true,
                    hasProjectWith = new[] { new { id = ProjectGid } },
                },
                orderBy = new { field = "CREATED_AT", direction = "ASC" },
                nodeId = "gid://axonhub/APIKeyProfileTemplate/1",
            });

        var connection = data.GetProperty("apiKeyProfileTemplates");
        var edges = connection.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(3, edges.Length);
        Assert.Equal(3, connection.GetProperty("totalCount").GetInt32());

        Assert.Equal(
            "gid://axonhub/APIKeyProfileTemplate/1",
            edges[0].GetProperty("node").GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/APIKeyProfileTemplate/2",
            edges[1].GetProperty("node").GetProperty("id").GetString());

        var node = edges[0].GetProperty("node");
        Assert.Equal("2026-01-02T03:04:05Z", node.GetProperty("createdAt").GetString());
        Assert.Equal("2026-01-02T03:04:05Z", node.GetProperty("updatedAt").GetString());
        Assert.Equal("Operations", node.GetProperty("name").GetString());
        Assert.Equal("first template", node.GetProperty("description").GetString());
        Assert.Equal(ProjectGid, node.GetProperty("projectID").GetString());

        var profile = node.GetProperty("profile");
        Assert.Equal("ops", profile.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("templateID").ValueKind);
        Assert.Equal(string.Empty, profile.GetProperty("templateName").GetString());
        Assert.Equal("default", profile.GetProperty("loadBalanceStrategy").GetString());
        Assert.Equal("default", profile.GetProperty("traceStickyMode").GetString());
        Assert.Equal(
            "old",
            Assert.Single(profile.GetProperty("modelMappings").EnumerateArray().ToArray())
                .GetProperty("from").GetString());

        Assert.Equal(1, node.GetProperty("linkedProfilesCount").GetInt32());
        Assert.Equal(
            ProjectGid,
            node.GetProperty("project").GetProperty("id").GetString());
        Assert.Equal(
            "project",
            node.GetProperty("project").GetProperty("name").GetString());

        // SQL NULL profile surfaces as GraphQL null.
        Assert.Equal(
            JsonValueKind.Null,
            edges[1].GetProperty("node").GetProperty("profile").ValueKind);
        Assert.Equal(0, edges[1].GetProperty("node").GetProperty("linkedProfilesCount").GetInt32());

        var pageInfo = connection.GetProperty("pageInfo");
        Assert.False(pageInfo.GetProperty("hasNextPage").GetBoolean());
        Assert.False(pageInfo.GetProperty("hasPreviousPage").GetBoolean());
        Assert.Equal(1, CursorCodec.Decode(pageInfo.GetProperty("startCursor").GetString()!).Id);
        Assert.Equal(3, CursorCodec.Decode(pageInfo.GetProperty("endCursor").GetString()!).Id);

        var resolved = data.GetProperty("node");
        Assert.Equal("Operations", resolved.GetProperty("name").GetString());
        Assert.Equal(ProjectGid, resolved.GetProperty("projectID").GetString());
    }

    [Fact]
    public async Task CreateUpdateDeleteMutationsRoundTripThroughGids()
    {
        var created = await MutateAsync(
            """
            mutation Create(
                $input: CreateAPIKeyProfileTemplateInput!,
                $profile: APIKeyProfileInput!
            ) {
              createApiKeyProfileTemplate(input: $input, profile: $profile) {
                id name description projectID
                profile {
                  name templateID templateName loadBalanceStrategy
                  modelMappings { from to }
                }
              }
            }
            """,
            new
            {
                input = new
                {
                    name = "Production",
                    description = "prod",
                    projectID = ProjectGid,
                },
                profile = new
                {
                    name = "ignored",
                    modelMappings = new[] { new { from = "a", to = "b" } },
                },
            });

        var node = created.GetProperty("createApiKeyProfileTemplate");
        Assert.Equal(
            "gid://axonhub/APIKeyProfileTemplate/1",
            node.GetProperty("id").GetString());
        Assert.Equal("Production", node.GetProperty("name").GetString());
        Assert.Equal("prod", node.GetProperty("description").GetString());
        Assert.Equal(ProjectGid, node.GetProperty("projectID").GetString());

        // CreateTemplate overwrites the embedded profile name with the input
        // name, clears the template link and normalizes the routing policy.
        var profile = node.GetProperty("profile");
        Assert.Equal("Production", profile.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, profile.GetProperty("templateID").ValueKind);
        Assert.Equal(string.Empty, profile.GetProperty("templateName").GetString());
        Assert.Equal("default", profile.GetProperty("loadBalanceStrategy").GetString());
        Assert.Equal(
            "b",
            Assert.Single(profile.GetProperty("modelMappings").EnumerateArray().ToArray())
                .GetProperty("to").GetString());

        var updated = await MutateAsync(
            """
            mutation Update(
                $id: ID!,
                $input: UpdateAPIKeyProfileTemplateInput!,
                $profile: APIKeyProfileInput
            ) {
              updateApiKeyProfileTemplate(id: $id, input: $input, profile: $profile) {
                id name description
                profile { name modelMappings { from to } }
              }
            }
            """,
            new
            {
                id = "gid://axonhub/APIKeyProfileTemplate/1",
                input = new { description = "changed" },
                profile = new
                {
                    name = "alias",
                    modelMappings = new[] { new { from = "c", to = "d" } },
                },
            });

        var updatedNode = updated.GetProperty("updateApiKeyProfileTemplate");
        Assert.Equal("Production", updatedNode.GetProperty("name").GetString());
        Assert.Equal("changed", updatedNode.GetProperty("description").GetString());
        Assert.Equal(
            "Production",
            updatedNode.GetProperty("profile").GetProperty("name").GetString());
        Assert.Equal(
            "d",
            Assert.Single(
                    updatedNode.GetProperty("profile")
                        .GetProperty("modelMappings")
                        .EnumerateArray()
                        .ToArray())
                .GetProperty("to").GetString());

        var duplicate = await MutateErrorsAsync(
            """
            mutation Create($input: CreateAPIKeyProfileTemplateInput!, $profile: APIKeyProfileInput!) {
              createApiKeyProfileTemplate(input: $input, profile: $profile) { id }
            }
            """,
            new
            {
                input = new
                {
                    name = "Production",
                    description = "",
                    projectID = ProjectGid,
                },
                profile = new { name = "x" },
            });

        var error = Assert.Single(duplicate);
        Assert.Equal("Template name 'Production' already exists", error.GetProperty("message").GetString());
        var extensions = error.GetProperty("extensions");
        Assert.Equal("DUPLICATE_NAME", extensions.GetProperty("code").GetString());
        Assert.Equal("Template", extensions.GetProperty("resource").GetString());
        Assert.Equal("name", extensions.GetProperty("field").GetString());
        Assert.Equal("Production", extensions.GetProperty("value").GetString());

        var deleted = await MutateAsync(
            """
            mutation Delete($id: ID!) {
              deleteApiKeyProfileTemplate(id: $id) { id name description }
            }
            """,
            new { id = "gid://axonhub/APIKeyProfileTemplate/1" });

        Assert.Equal(
            "Production",
            deleted.GetProperty("deleteApiKeyProfileTemplate").GetProperty("name").GetString());

        var remaining = await QueryDataAsync(
            """
            query Templates { apiKeyProfileTemplates(first: 10) { totalCount } }
            """);
        Assert.Equal(
            0,
            remaining.GetProperty("apiKeyProfileTemplates").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task LoadMutationAppendsTheTemplateProfileToTheKey()
    {
        var key = _gate.ApiKeys.Add(item =>
        {
            item.ProjectId = 12;
            item.Profiles = """{"activeProfile":"","profiles":null}""";
        });
        var template = _templates.Add(item =>
        {
            item.Name = "Load";
            item.ProjectId = 12;
            item.Profile = ApiKeyProfilesCodec.SerializeProfile(new ApiKeyProfileModel
            {
                Name = "Load profile",
                ModelMappings = [new ApiKeyModelMapping { From = "x", To = "y" }],
            });
        });

        var data = await MutateAsync(
            """
            mutation Load($input: LoadApiKeyProfileTemplateInput!) {
              loadApiKeyProfileTemplate(input: $input) {
                id
                profiles {
                  activeProfile
                  profiles { name templateID templateName modelMappings { from to } }
                }
              }
            }
            """,
            new
            {
                input = new
                {
                    templateID = new GidValue("APIKeyProfileTemplate", template.Id).ToString(),
                    apiKeyID = new GidValue("APIKey", key.Id).ToString(),
                },
            });

        var loaded = data.GetProperty("loadApiKeyProfileTemplate");
        Assert.Equal(
            new GidValue("APIKey", key.Id).ToString(),
            loaded.GetProperty("id").GetString());

        var profile = Assert.Single(
            loaded.GetProperty("profiles")
                .GetProperty("profiles")
                .EnumerateArray()
                .ToArray());
        Assert.Equal("Load profile", profile.GetProperty("name").GetString());
        Assert.Equal(
            checked((int)template.Id),
            profile.GetProperty("templateID").GetInt32());
        Assert.Equal("Load", profile.GetProperty("templateName").GetString());
        Assert.Equal(
            "y",
            Assert.Single(profile.GetProperty("modelMappings").EnumerateArray().ToArray())
                .GetProperty("to").GetString());
    }

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null)
    {
        var root = await ExecuteAsync(query, variables);
        Assert.False(root.TryGetProperty("errors", out _), root.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement> MutateAsync(string query, object? variables)
        => await QueryDataAsync(query, variables);

    private async Task<JsonElement[]> MutateErrorsAsync(string query, object? variables)
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
