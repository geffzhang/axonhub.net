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
/// Locks the API key GraphQL surface over /admin/graphql against the queries
/// and mutations the frontend sends (frontend/src/features/api-keys): gid IDs,
/// the profile/quota JSON shape, Decimal marshaling, the entgql pagination
/// rules and the reference error strings from internal/server/biz/api_key.go.
/// </summary>
public sealed class AdminApiKeysEndpointTests : IAsyncLifetime
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly string[] NonPersonalTypes = ["service_account", "noauth"];
    private static readonly string[] ReadChannels = ["read_channels"];
    private static readonly string[] PrivateNetwork = ["10.0.0.0/8"];
    private static readonly string[] ReadUsers = ["read_users"];
    private static readonly int[] ChannelIds = [1, 2];
    private static readonly string[] ChannelTags = ["t1"];
    private static readonly string[] MissingId = ["gid://axonhub/APIKey/999"];

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeUserAdminStore _users = new();

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
        builder.Services.AddScoped<PermissionValidator>();
        builder.Services.AddScoped<ApiKeyPrivacy>();
        builder.Services.AddScoped<ApiKeyManagementService>();
        _users.RolesSource = _roles;

        // The JWT user (id 9) is loaded through IUserAdminStore by the project-
        // admin check and by the privacy policies.
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
    public async Task ApiKeysQueryMatchesTheFrontendShape()
    {
        await SeedAdminUserAsync(3, "creator@example.com");
        SeedProject();
        var key = SeedKey(configure: item =>
        {
            item.Key = "ah-service";
            item.Name = "Service key";
            item.Type = "service_account";
            item.Status = "enabled";
            item.Scopes = """["read_channels"]""";
            item.AllowedIps = """["127.0.0.1"]""";
            item.UserId = 3;
            item.Profiles =
                """{"activeProfile":"p","profiles":[{"name":"p","templateID":5,"templateName":"Tpl","modelMappings":[{"from":"a","to":"b"}],"quota":{"requests":5,"totalTokens":100,"cost":"1.5","period":{"type":"past_duration","pastDuration":{"value":2,"unit":"hour"}}},"loadBalanceStrategy":"default","traceStickyMode":"default","channelIDs":[1,2],"channelTags":["t1"],"channelTagsMatchMode":"all","modelIDs":["m1"]},{"name":"q"}]}""";
        });

        var data = await QueryDataAsync(
            ApiKeysQuery,
            new { first = 10 });

        var connection = data.GetProperty("apiKeys");
        var edge = Assert.Single(connection.GetProperty("edges").EnumerateArray());
        var node = edge.GetProperty("node");

        Assert.Equal("gid://axonhub/APIKey/" + key.Id, node.GetProperty("id").GetString());
        Assert.Equal(
            "2026-01-02T03:04:05Z",
            node.GetProperty("createdAt").GetString());
        Assert.Equal(
            "gid://axonhub/User/3",
            node.GetProperty("userID").GetString());
        Assert.Equal(
            "gid://axonhub/Project/1",
            node.GetProperty("projectID").GetString());
        Assert.Equal("ah-service", node.GetProperty("key").GetString());
        Assert.Equal("Service key", node.GetProperty("name").GetString());
        Assert.Equal("service_account", node.GetProperty("type").GetString());
        Assert.Equal("enabled", node.GetProperty("status").GetString());
        Assert.Equal(
            ["read_channels"],
            node.GetProperty("scopes").EnumerateArray()
                .Select(static item => item.GetString()!).ToArray());
        Assert.Equal(
            ["127.0.0.1"],
            node.GetProperty("allowedIps").EnumerateArray()
                .Select(static item => item.GetString()!).ToArray());

        var profiles = node.GetProperty("profiles");
        Assert.Equal("p", profiles.GetProperty("activeProfile").GetString());
        var profileNodes = profiles.GetProperty("profiles").EnumerateArray().ToArray();

        Assert.Equal(5, profileNodes[0].GetProperty("templateID").GetInt32());
        Assert.Equal("Tpl", profileNodes[0].GetProperty("templateName").GetString());
        var mapping = Assert.Single(
            profileNodes[0].GetProperty("modelMappings").EnumerateArray());
        Assert.Equal("a", mapping.GetProperty("from").GetString());
        Assert.Equal("b", mapping.GetProperty("to").GetString());
        Assert.Equal(
            [1, 2],
            profileNodes[0].GetProperty("channelIDs").EnumerateArray()
                .Select(static item => item.GetInt32()).ToArray());
        Assert.Equal(
            ["t1"],
            profileNodes[0].GetProperty("channelTags").EnumerateArray()
                .Select(static item => item.GetString()!).ToArray());
        Assert.Equal("all", profileNodes[0].GetProperty("channelTagsMatchMode").GetString());
        Assert.Equal(
            ["m1"],
            profileNodes[0].GetProperty("modelIDs").EnumerateArray()
                .Select(static item => item.GetString()!).ToArray());
        Assert.Equal("default", profileNodes[0].GetProperty("loadBalanceStrategy").GetString());
        Assert.Equal("default", profileNodes[0].GetProperty("traceStickyMode").GetString());

        var quota = profileNodes[0].GetProperty("quota");
        Assert.Equal(5, quota.GetProperty("requests").GetInt32());
        Assert.Equal(100, quota.GetProperty("totalTokens").GetInt32());
        // Decimal is marshaled with shopspring's trailing-zero-trimmed text
        // converted back to a JSON number.
        Assert.Equal("1.5", quota.GetProperty("cost").GetRawText());
        var period = quota.GetProperty("period");
        Assert.Equal("past_duration", period.GetProperty("type").GetString());
        Assert.Equal(2, period.GetProperty("pastDuration").GetProperty("value").GetInt32());
        Assert.Equal("hour", period.GetProperty("pastDuration").GetProperty("unit").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            period.GetProperty("calendarDuration").ValueKind);

        // gqlgen's optional string marshaller emits "" for empty strings and
        // omits nothing for absent optional fields.
        Assert.Equal(string.Empty, profileNodes[1].GetProperty("templateName").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            profileNodes[1].GetProperty("modelMappings").ValueKind);
        Assert.Equal(
            string.Empty,
            profileNodes[1].GetProperty("channelTagsMatchMode").GetString());
        Assert.Equal(JsonValueKind.Null, profileNodes[1].GetProperty("quota").ValueKind);

        // The reference graph resolves the lazy ent edges under the Project and
        // User privacy policies.
        Assert.Equal(
            "gid://axonhub/User/3",
            node.GetProperty("user").GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/Project/1",
            node.GetProperty("project").GetProperty("id").GetString());

        // CREATED_AT is rewritten to ent.DefaultAPIKeyOrder's id field, so the
        // cursor carries no value.
        var cursor = CursorCodec.Decode(edge.GetProperty("cursor").GetString()!);
        Assert.Equal(key.Id, cursor.Id);
        Assert.Null(cursor.Value);

        var pageInfo = connection.GetProperty("pageInfo");
        Assert.False(pageInfo.GetProperty("hasNextPage").GetBoolean());
        Assert.False(pageInfo.GetProperty("hasPreviousPage").GetBoolean());
        Assert.Equal(1, connection.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ApiKeysQueryFiltersAndOrdersByName()
    {
        SeedProject();
        var alpha = SeedKey(configure: item =>
        {
            item.Key = "ah-alpha";
            item.Name = "Alpha";
            item.Type = "user";
        });
        var beta = SeedKey(configure: item =>
        {
            item.Key = "ah-beta";
            item.Name = "Beta";
            item.Type = "service_account";
            item.CreatedAt = CreatedAt.AddSeconds(1);
            item.UpdatedAt = CreatedAt.AddSeconds(1);
        });
        var gamma = SeedKey(configure: item =>
        {
            item.Key = "ah-gamma";
            item.Name = "Gamma";
            item.Type = "noauth";
            item.CreatedAt = CreatedAt.AddSeconds(2);
            item.UpdatedAt = CreatedAt.AddSeconds(2);
        });

        var filtered = await QueryDataAsync(
            """
            query ApiKeys($where: APIKeyWhereInput) {
              apiKeys(first: 10, where: $where) { edges { node { name } } }
            }
            """,
            new { where = new { or = new[] { new { nameContainsFold = "ALPH" } } } });

        var names = filtered.GetProperty("apiKeys").GetProperty("edges")
            .EnumerateArray()
            .Select(static edge => edge.GetProperty("node").GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal(["Alpha"], names);

        // The frontend combines keyContainsFold with an exclusion list.
        var byKey = await QueryDataAsync(
            """
            query ApiKeys($where: APIKeyWhereInput) {
              apiKeys(first: 10, where: $where) { edges { node { name } } }
            }
            """,
            new
            {
                where = new
                {
                    keyContainsFold = "AH-",
                    typeNotIn = NonPersonalTypes,
                },
            });

        var byKeyNames = byKey.GetProperty("apiKeys").GetProperty("edges")
            .EnumerateArray()
            .Select(static edge => edge.GetProperty("node").GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal(["Alpha"], byKeyNames);

        var ordered = await QueryDataAsync(
            """
            query ApiKeys($orderBy: APIKeyOrder) {
              apiKeys(first: 10, orderBy: $orderBy) {
                edges { node { name } cursor }
              }
            }
            """,
            new { orderBy = new { field = "NAME", direction = "DESC" } });

        var orderedNames = ordered.GetProperty("apiKeys").GetProperty("edges")
            .EnumerateArray()
            .Select(static edge => edge.GetProperty("node").GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal(["Gamma", "Beta", "Alpha"], orderedNames);
        var firstEdge = ordered.GetProperty("apiKeys").GetProperty("edges")[0];
        var nameCursor = CursorCodec.Decode(firstEdge.GetProperty("cursor").GetString()!);
        Assert.Equal(gamma.Id, nameCursor.Id);
        Assert.Equal("Gamma", nameCursor.Value);

        // after: the NAME cursor paginates by (name, id).
        var after = await QueryDataAsync(
            """
            query ApiKeys($after: Cursor) {
              apiKeys(first: 1, after: $after, orderBy: { field: NAME, direction: DESC }) {
                edges { node { name } }
                pageInfo { hasNextPage hasPreviousPage }
              }
            }
            """,
            new { after = firstEdge.GetProperty("cursor").GetString() });

        var afterNames = after.GetProperty("apiKeys").GetProperty("edges")
            .EnumerateArray()
            .Select(static edge => edge.GetProperty("node").GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal(["Beta"], afterNames);
        Assert.True(
            after.GetProperty("apiKeys").GetProperty("pageInfo")
                .GetProperty("hasNextPage").GetBoolean());
        Assert.True(
            after.GetProperty("apiKeys").GetProperty("pageInfo")
                .GetProperty("hasPreviousPage").GetBoolean());

        Assert.Equal("Beta", beta.Name);
        Assert.Equal("Gamma", gamma.Name);
        Assert.NotNull(alpha);
    }

    [Fact]
    public async Task ApiKeyWithoutCreatorOutputsTheZeroUserGidAndNullUserEdge()
    {
        SeedProject();
        var key = SeedKey(configure: item => item.UserId = null);

        var data = await QueryDataAsync(
            """
            query Node($id: ID!) {
              node(id: $id) {
                ... on APIKey { id userID user { id } }
              }
            }
            """,
            new { id = "gid://axonhub/APIKey/" + key.Id });

        var node = data.GetProperty("node");
        Assert.Equal(
            "gid://axonhub/APIKey/" + key.Id,
            node.GetProperty("id").GetString());
        // ent's zero user id still marshals as a GUID.
        Assert.Equal("gid://axonhub/User/0", node.GetProperty("userID").GetString());
        Assert.Equal(JsonValueKind.Null, node.GetProperty("user").ValueKind);
    }

    [Fact]
    public async Task NodeLookupReturnsApiKeysAndReportsTheEntNotFoundError()
    {
        SeedProject();
        var key = SeedKey(configure: item => item.Name = "Lookup");

        var data = await QueryDataAsync(
            """
            query Node($id: ID!) {
              node(id: $id) { ... on APIKey { id name } }
            }
            """,
            new { id = "gid://axonhub/APIKey/" + key.Id });

        Assert.Equal("Lookup", data.GetProperty("node").GetProperty("name").GetString());

        var errors = await QueryErrorsAsync(
            """
            query Node($id: ID!) {
              node(id: $id) { ... on APIKey { id } }
            }
            """,
            new { id = "gid://axonhub/APIKey/999" });

        Assert.Equal("ent: api_key not found", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task CreateApiKeyUsesSchemaDefaultsAndTheConfiguredPrefix()
    {
        SeedProject();

        var data = await QueryDataAsync(
            """
            mutation Create($input: CreateAPIKeyInput!) {
              createAPIKey(input: $input) {
                id
                userID
                projectID
                key
                name
                type
                status
                scopes
                allowedIps
                profiles { activeProfile profiles { name } }
              }
            }
            """,
            new
            {
                input = new
                {
                    name = "Primary",
                    projectID = "gid://axonhub/Project/1",
                },
            });

        var node = data.GetProperty("createAPIKey");
        Assert.Equal(
            "gid://axonhub/User/" + AdminGraphqlGateServices.UserId,
            node.GetProperty("userID").GetString());
        Assert.Equal(
            "gid://axonhub/Project/1",
            node.GetProperty("projectID").GetString());
        var key = node.GetProperty("key").GetString()!;
        Assert.StartsWith("ah-", key, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", key[3..]);
        Assert.Equal("user", node.GetProperty("type").GetString());
        Assert.Equal("enabled", node.GetProperty("status").GetString());
        Assert.Equal(
            ["read_channels", "write_requests"],
            node.GetProperty("scopes").EnumerateArray()
                .Select(static item => item.GetString()!).ToArray());
        Assert.Empty(node.GetProperty("allowedIps").EnumerateArray());
        Assert.Equal(string.Empty, node.GetProperty("profiles").GetProperty("activeProfile").GetString());

        var stored = Assert.Single(_gate.ApiKeys.Rows);
        Assert.Equal("Primary", stored.Name);
        Assert.Equal(1, stored.ProjectId);
        Assert.Equal(AdminGraphqlGateServices.UserId, stored.UserId);
        Assert.Equal("""["read_channels","write_requests"]""", stored.Scopes);
        Assert.Equal("""{"activeProfile":"","profiles":null}""", stored.Profiles);
        Assert.Equal("[]", stored.AllowedIps);
    }

    [Fact]
    public async Task CreateServiceAccountApiKeyStoresScopesAndAllowedIps()
    {
        SeedProject();

        var data = await QueryDataAsync(
            """
            mutation Create($input: CreateAPIKeyInput!) {
              createAPIKey(input: $input) { type scopes allowedIps }
            }
            """,
            new
            {
                input = new
                {
                    name = "CI",
                    type = "service_account",
                    projectID = "gid://axonhub/Project/1",
                    scopes = ReadChannels,
                    allowedIps = PrivateNetwork,
                },
            });

        var node = data.GetProperty("createAPIKey");
        Assert.Equal("service_account", node.GetProperty("type").GetString());
        Assert.Equal(
            ["read_channels"],
            node.GetProperty("scopes").EnumerateArray()
                .Select(static item => item.GetString()!).ToArray());
        Assert.Equal(
            ["10.0.0.0/8"],
            node.GetProperty("allowedIps").EnumerateArray()
                .Select(static item => item.GetString()!).ToArray());

        var stored = Assert.Single(_gate.ApiKeys.Rows);
        Assert.Equal("""["read_channels"]""", stored.Scopes);
        Assert.Equal("""["10.0.0.0/8"]""", stored.AllowedIps);
    }

    [Fact]
    public async Task CreateDuplicateNameReturnsTheCodedErrorExtensions()
    {
        SeedProject();
        SeedKey(configure: item =>
        {
            item.Name = "dup";
            item.UserId = AdminGraphqlGateServices.UserId;
        });

        var errors = await QueryErrorsAsync(
            """
            mutation Create($input: CreateAPIKeyInput!) {
              createAPIKey(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    name = "dup",
                    projectID = "gid://axonhub/Project/1",
                },
            });

        var error = errors[0];
        Assert.Equal("API Key name 'dup' already exists", error.GetProperty("message").GetString());
        var extensions = error.GetProperty("extensions");
        Assert.Equal("DUPLICATE_NAME", extensions.GetProperty("code").GetString());
        Assert.Equal("API Key", extensions.GetProperty("resource").GetString());
        Assert.Equal("name", extensions.GetProperty("field").GetString());
        Assert.Equal("dup", extensions.GetProperty("value").GetString());

        // The insert is rolled back with the transaction.
        Assert.Single(_gate.ApiKeys.Rows);
    }

    [Fact]
    public async Task UpdateApiKeyRenamesUserKeysAndRejectsScopeChanges()
    {
        SeedProject();
        var key = SeedKey();

        var data = await QueryDataAsync(
            """
            mutation Update($id: ID!, $input: UpdateAPIKeyInput!) {
              updateAPIKey(id: $id, input: $input) { id name }
            }
            """,
            new
            {
                id = "gid://axonhub/APIKey/" + key.Id,
                input = new { name = "Renamed" },
            });

        Assert.Equal("Renamed", data.GetProperty("updateAPIKey").GetProperty("name").GetString());
        Assert.Equal("Renamed", key.Name);

        var errors = await QueryErrorsAsync(
            """
            mutation Update($id: ID!, $input: UpdateAPIKeyInput!) {
              updateAPIKey(id: $id, input: $input) { id }
            }
            """,
            new
            {
                id = "gid://axonhub/APIKey/" + key.Id,
                input = new { scopes = ReadUsers },
            });

        Assert.Equal(
            "user type API key cannot update scopes",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task UpdateStatusAndRotateChangeTheStoredKey()
    {
        SeedProject();
        var key = SeedKey(configure: item =>
        {
            item.Key = "ah-old";
            item.Type = "service_account";
            item.Scopes = """["read_channels"]""";
        });

        var status = await QueryDataAsync(
            """
            mutation UpdateStatus($id: ID!, $status: APIKeyStatus!) {
              updateAPIKeyStatus(id: $id, status: $status) { id status }
            }
            """,
            new { id = "gid://axonhub/APIKey/" + key.Id, status = "disabled" });

        Assert.Equal(
            "disabled",
            status.GetProperty("updateAPIKeyStatus").GetProperty("status").GetString());
        Assert.Equal("disabled", key.Status);

        var rotated = await QueryDataAsync(
            """
            mutation Rotate($id: ID!) {
              rotateAPIKey(id: $id) { id key }
            }
            """,
            new { id = "gid://axonhub/APIKey/" + key.Id });

        var rotatedKey = rotated.GetProperty("rotateAPIKey").GetProperty("key").GetString()!;
        Assert.NotEqual("ah-old", rotatedKey);
        Assert.Matches("^ah-[0-9a-f]{64}$", rotatedKey);
        Assert.Equal(rotatedKey, key.Key);
    }

    [Fact]
    public async Task UpdateApiKeyProfilesRoundTripsTheGoJsonShape()
    {
        SeedProject();
        var key = SeedKey();

        var data = await QueryDataAsync(
            """
            mutation UpdateProfiles($id: ID!, $input: UpdateAPIKeyProfilesInput!) {
              updateAPIKeyProfiles(id: $id, input: $input) {
                id
                profiles {
                  activeProfile
                  profiles {
                    name
                    channelIDs
                    channelTags
                    channelTagsMatchMode
                    loadBalanceStrategy
                    traceStickyMode
                    quota {
                      requests
                      totalTokens
                      cost
                      period {
                        type
                        pastDuration { value unit }
                        calendarDuration { unit }
                      }
                    }
                  }
                }
              }
            }
            """,
            new
            {
                id = "gid://axonhub/APIKey/" + key.Id,
                input = new
                {
                    activeProfile = "a",
                    profiles = new object[]
                    {
                        new
                        {
                            name = "a",
                            channelIDs = ChannelIds,
                            channelTags = ChannelTags,
                            channelTagsMatchMode = "none",
                            quota = new
                            {
                                requests = 10,
                                totalTokens = 1000,
                                cost = 2.5,
                                period = new
                                {
                                    type = "calendar_duration",
                                    calendarDuration = new { unit = "month" },
                                },
                            },
                            loadBalanceStrategy = "default",
                            traceStickyMode = "default",
                        },
                    },
                },
            });

        var profiles = data.GetProperty("updateAPIKeyProfiles").GetProperty("profiles");
        Assert.Equal("a", profiles.GetProperty("activeProfile").GetString());
        var profile = Assert.Single(profiles.GetProperty("profiles").EnumerateArray());
        Assert.Equal("none", profile.GetProperty("channelTagsMatchMode").GetString());
        var quota = profile.GetProperty("quota");
        Assert.Equal("2.5", quota.GetProperty("cost").GetRawText());
        var period = quota.GetProperty("period");
        Assert.Equal("calendar_duration", period.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, period.GetProperty("pastDuration").ValueKind);
        Assert.Equal("month", period.GetProperty("calendarDuration").GetProperty("unit").GetString());

        // Go's encoding/json shape with omitempty; routing policies are
        // normalized to "default".
        Assert.Equal(
            """{"activeProfile":"a","profiles":[{"name":"a","modelMappings":null,"quota":{"requests":10,"totalTokens":1000,"cost":"2.5","period":{"type":"calendar_duration","calendarDuration":{"unit":"month"}}},"loadBalanceStrategy":"default","traceStickyMode":"default","channelIDs":[1,2],"channelTags":["t1"],"channelTagsMatchMode":"none"}]}""",
            key.Profiles);
    }

    [Fact]
    public async Task BulkMutationsChangeStatusesAndReturnTrue()
    {
        SeedProject();
        var first = SeedKey(configure: item => item.Name = "first");
        var second = SeedKey(configure: item => item.Name = "second");

        var ids = new[]
        {
            "gid://axonhub/APIKey/" + first.Id,
            "gid://axonhub/APIKey/" + second.Id,
        };

        var disabled = await QueryDataAsync(
            """
            mutation Bulk($ids: [ID!]!) { bulkDisableAPIKeys(ids: $ids) }
            """,
            new { ids });
        Assert.True(disabled.GetProperty("bulkDisableAPIKeys").GetBoolean());
        Assert.Equal("disabled", first.Status);
        Assert.Equal("disabled", second.Status);

        var enabled = await QueryDataAsync(
            """
            mutation Bulk($ids: [ID!]!) { bulkEnableAPIKeys(ids: $ids) }
            """,
            new { ids });
        Assert.True(enabled.GetProperty("bulkEnableAPIKeys").GetBoolean());
        Assert.Equal("enabled", first.Status);
        Assert.Equal("enabled", second.Status);

        var archived = await QueryDataAsync(
            """
            mutation Bulk($ids: [ID!]!) { bulkArchiveAPIKeys(ids: $ids) }
            """,
            new { ids });
        Assert.True(archived.GetProperty("bulkArchiveAPIKeys").GetBoolean());
        Assert.Equal("archived", first.Status);
        Assert.Equal("archived", second.Status);
        Assert.Equal(0, first.DeletedAt);
        Assert.Equal(0, second.DeletedAt);

        var errors = await QueryErrorsAsync(
            """
            mutation Bulk($ids: [ID!]!) { bulkDisableAPIKeys(ids: $ids) }
            """,
            new
            {
                ids = MissingId,
            });
        Assert.Equal(
            "expected to find 1 API keys, but found 0",
            errors[0].GetProperty("message").GetString());
    }

    private const string ApiKeysQuery = """
        query ApiKeys($first: Int, $after: Cursor, $orderBy: APIKeyOrder, $where: APIKeyWhereInput) {
          apiKeys(first: $first, after: $after, orderBy: $orderBy, where: $where) {
            edges {
              node {
                id
                createdAt
                updatedAt
                userID
                projectID
                key
                name
                type
                status
                scopes
                allowedIps
                profiles {
                  activeProfile
                  profiles {
                    name
                    templateID
                    templateName
                    modelMappings { from to }
                    channelIDs
                    channelTags
                    channelTagsMatchMode
                    modelIDs
                    quota {
                      requests
                      totalTokens
                      cost
                      period {
                        type
                        pastDuration { value unit }
                        calendarDuration { unit }
                      }
                    }
                    loadBalanceStrategy
                    traceStickyMode
                  }
                }
                user { id }
                project { id }
              }
              cursor
            }
            pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
            totalCount
          }
        }
        """;

    private ProjectRecord SeedProject(long id = 1)
        => _gate.Projects.Add(project =>
        {
            project.Id = id;
            project.Name = "project";
            project.Status = "active";
        });

    private ApiKeyRecord SeedKey(
        long projectId = 1,
        Action<ApiKeyRecord>? configure = null)
        => _gate.ApiKeys.Add(key =>
        {
            key.ProjectId = projectId;
            key.UserId = AdminGraphqlGateServices.UserId;
            key.CreatedAt = CreatedAt;
            key.UpdatedAt = CreatedAt;
            configure?.Invoke(key);
        });

    private async Task SeedAdminUserAsync(long id, string email, bool isOwner = false)
    {
        await _users.CreateAsync(
            new UserRecord
            {
                Id = id,
                Email = email,
                Status = "activated",
                IsOwner = isOwner,
            },
            roleIds: null);
    }

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null,
        string? operationName = null)
    {
        var root = await ExecuteAsync(query, variables, operationName);
        Assert.False(root.TryGetProperty("errors", out _), root.ToString());
        return root.GetProperty("data");
    }

    private async Task<JsonElement[]> QueryErrorsAsync(
        string query,
        object? variables = null,
        string? operationName = null)
    {
        var root = await ExecuteAsync(query, variables, operationName);
        Assert.True(root.TryGetProperty("errors", out var errors), root.ToString());
        return errors.EnumerateArray().Select(static error => error.Clone()).ToArray();
    }

    private async Task<JsonElement> ExecuteAsync(
        string query,
        object? variables,
        string? operationName)
    {
        var payload = JsonSerializer.Serialize(new { query, variables, operationName });
        using var response = await _client.PostAsync(
            "/admin/graphql",
            new StringContent(payload, Encoding.UTF8, "application/json"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
