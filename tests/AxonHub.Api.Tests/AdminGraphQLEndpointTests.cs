using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the management GraphQL transport. The admin UI posts to /admin/graphql
/// with {query, variables, operationName} and reads data/errors
/// (frontend/src/gql/graphql.ts:56-161). The first ported field is
/// queryResolver.AllScopes (internal/server/gql/scopes.resolvers.go:15).
/// </summary>
public sealed class AdminGraphQLEndpointTests : IAsyncLifetime
{
    private static readonly (string Scope, string Description, string[] Levels)[] ReferenceCatalog =
    [
        ("read_dashboard", "View dashboard", ["system"]),
        ("read_settings", "View system settings", ["system"]),
        ("write_settings", "Manage system settings", ["system"]),
        ("read_channels", "View channel information", ["system"]),
        ("write_channels", "Manage channels/models (create, edit, delete)", ["system"]),
        ("read_data_storages", "View data storage information", ["system"]),
        ("write_data_storages", "Manage data storages (create, edit, delete)", ["system"]),
        ("read_users", "View user information", ["system", "project"]),
        ("write_users", "Manage users (create, edit, delete)", ["system", "project"]),
        ("read_roles", "View role information", ["system", "project"]),
        ("write_roles", "Manage roles (create, edit, delete)", ["system", "project"]),
        ("read_projects", "View project information", ["system"]),
        ("write_projects", "Manage projects (create, edit, delete)", ["system"]),
        ("read_api_keys", "View API keys", ["system", "project"]),
        ("write_api_keys", "Manage API keys (create, edit, delete)", ["system", "project"]),
        ("read_requests", "View request records", ["system", "project"]),
        ("write_requests", "Manage request records", ["system", "project"]),
        ("read_prompts", "View prompts", ["system", "project"]),
        ("write_prompts", "Manage prompts (create, edit, delete)", ["system", "project"]),
    ];

    private readonly AdminGraphqlGateServices _gate = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);

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
    public async Task AllScopesReturnsTheReferenceCatalogInOrder()
    {
        var data = await QueryAsync("{ allScopes { scope description levels } }");
        var scopes = data.GetProperty("allScopes").EnumerateArray().ToArray();

        Assert.Equal(ReferenceCatalog.Length, scopes.Length);
        for (var i = 0; i < ReferenceCatalog.Length; i++)
        {
            Assert.Equal(ReferenceCatalog[i].Scope, scopes[i].GetProperty("scope").GetString());
            Assert.Equal(ReferenceCatalog[i].Description, scopes[i].GetProperty("description").GetString());
            Assert.Equal(
                ReferenceCatalog[i].Levels,
                scopes[i].GetProperty("levels").EnumerateArray()
                    .Select(static level => level.GetString()!)
                    .ToArray());
        }
    }

    [Fact]
    public async Task ResponseUsesJsonMediaTypeForLegacyFrontend()
    {
        var payload = JsonSerializer.Serialize(new { query = "{ allScopes { scope } }" });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/graphql")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task EveryScopeAppliesAtTheSystemLevel()
    {
        var data = await QueryAsync("{ allScopes(level: \"system\") { scope } }");

        Assert.Equal(ReferenceCatalog.Length, data.GetProperty("allScopes").GetArrayLength());
    }

    [Fact]
    public async Task TheProjectLevelKeepsOnlyProjectScopesInCatalogOrder()
    {
        var data = await QueryAsync(
            "query AllScopes($level: String) { allScopes(level: $level) { scope } }",
            new { level = "project" },
            "AllScopes");

        var slugs = data.GetProperty("allScopes").EnumerateArray()
            .Select(static scope => scope.GetProperty("scope").GetString()!)
            .ToArray();

        Assert.Equal(
            [
                "read_users",
                "write_users",
                "read_roles",
                "write_roles",
                "read_api_keys",
                "write_api_keys",
                "read_requests",
                "write_requests",
                "read_prompts",
                "write_prompts",
            ],
            slugs);
    }

    [Fact]
    public async Task AnUnknownLevelYieldsAnEmptyList()
    {
        // scopes.ScopeLevel has no validation: an unknown value simply filters
        // every entry out (internal/scopes/scopes.go:205-220).
        var data = await QueryAsync("{ allScopes(level: \"workspace\") { scope } }");

        Assert.Empty(data.GetProperty("allScopes").EnumerateArray());
    }

    [Fact]
    public async Task ANullLevelReturnsEveryScope()
    {
        var data = await QueryAsync(
            "query AllScopes($level: String) { allScopes(level: $level) { scope } }",
            new { level = (string?)null });

        Assert.Equal(ReferenceCatalog.Length, data.GetProperty("allScopes").GetArrayLength());
    }

    [Fact]
    public async Task IntrospectionExposesTheScopeInfoType()
    {
        var data = await QueryAsync("{ __type(name: \"ScopeInfo\") { fields { name } } }");

        var fields = data.GetProperty("__type").GetProperty("fields").EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["description", "levels", "scope"], fields);
    }

    private async Task<JsonElement> QueryAsync(
        string query,
        object? variables = null,
        string? operationName = null)
    {
        var payload = JsonSerializer.Serialize(new { query, variables, operationName });
        using var response = await _client.PostAsync(
            "/admin/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        AdminGraphqlHttpResponseAssertions.AssertStatus(response, body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement.Clone();
        Assert.False(root.TryGetProperty("errors", out _), body);
        return root.GetProperty("data");
    }
}
