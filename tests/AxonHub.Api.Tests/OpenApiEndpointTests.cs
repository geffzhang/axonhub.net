using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AxonHub.Api.Auth;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Api.Security;
using AxonHub.Api.Static;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the OpenAPI surface added in slice §190 against the reference: the
/// second gqlgen schema at POST /openapi/v1/graphql (its own message-only error
/// presenter), the two GraphiQL pages, and POST /openapi/webhook/echo
/// (internal/server/routes.go:157-176, gql/openapi/graphql.go,
/// gql/openapi/openapi.resolvers.go, api/system.go:103-130).
/// </summary>
public sealed class OpenApiEndpointTests : IAsyncLifetime
{
    private const long ProjectId = 12;
    private const long OtherProjectId = 13;

    private const string FullScopes =
        """["read_api_keys","write_api_keys","read_channels","write_requests"]""";

    private const string ReadOnlyScopes =
        """["read_api_keys","read_channels","write_requests"]""";

    private const string WriteOnlyScopes = """["write_api_keys"]""";

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeApiKeyProfileTemplateStore _templates = new();
    private readonly FakeSecuritySettingsProvider _security = new();
    private readonly StubQuotaReader _quotas = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private ApiKeyRecord _serviceKey = null!;
    private ApiKeyRecord _readOnlyKey = null!;
    private ApiKeyRecord _writeOnlyKey = null!;
    private ApiKeyRecord _otherProjectKey = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubOpenApiGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IRoleStore>(_roles);
        builder.Services.AddSingleton<IApiKeyProfileTemplateStore>(_templates);
        builder.Services.AddSingleton<ISecuritySettingsProvider>(_security);
        builder.Services.AddSingleton<IApiKeyProfileQuotaReader>(_quotas);
        builder.Services.AddScoped<PermissionValidator>();
        builder.Services.AddScoped<ApiKeyPrivacy>();
        builder.Services.AddScoped<ApiKeyProfileTemplatePrivacy>();
        builder.Services.AddScoped<ApiKeyProfileTemplateService>();
        builder.Services.AddScoped<ApiKeyManagementService>();

        _gate.Projects.Add(project =>
        {
            project.Id = ProjectId;
            project.Name = "project";
            project.Status = "active";
        });
        _gate.Projects.Add(project =>
        {
            project.Id = OtherProjectId;
            project.Name = "other";
            project.Status = "active";
        });

        _serviceKey = AddServiceKey("sk-openapi", FullScopes, ProjectId, userId: 7);
        _readOnlyKey = AddServiceKey("sk-readonly", ReadOnlyScopes, ProjectId);
        _writeOnlyKey = AddServiceKey("sk-writeonly", WriteOnlyScopes, ProjectId);
        _otherProjectKey = AddServiceKey(
            "sk-other-project",
            FullScopes,
            OtherProjectId);

        _app = builder.Build();
        MapOpenApi(_app);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    // ---------------------------------------------------------------------
    // Auth gate (WithIPBlocklist + WithOpenAPIAuth)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task MissingAuthorizationIsRejectedWithTheOpenApiEnvelope()
    {
        using var response = await PostGraphQlAsync(IntrospectionQuery, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            """{"error":{"type":"Unauthorized","message":"API key is required"}}""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task NonBearerAndBlankTokensAreRejectedWithTheExactMessages()
    {
        using (var request = new HttpRequestMessage(HttpMethod.Post, GraphQlPath))
        {
            request.Content = JsonContent(IntrospectionQuery);
            request.Headers.TryAddWithoutValidation("Authorization", "Token abc");
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(
                """{"error":{"type":"Unauthorized","message":"invalid token: Authorization header must start with 'Bearer '"}}""",
                await response.Content.ReadAsStringAsync());
        }

        // Trailing OWS is stripped by the HTTP parser on both sides (Go
        // net/http textproto and Kestrel both see "Bearer"), so this trips the
        // HasPrefix("Bearer ") check; the empty-token branch is unreachable
        // through a real header.
        using (var request = new HttpRequestMessage(HttpMethod.Post, GraphQlPath))
        {
            request.Content = JsonContent(IntrospectionQuery);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer    ");
            using var response = await _client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(
                """{"error":{"type":"Unauthorized","message":"invalid token: Authorization header must start with 'Bearer '"}}""",
                await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task UnknownDisabledAndUserKeysAreRejectedAsInvalidApiKey()
    {
        _gate.ApiKeys.Add(key =>
        {
            key.Key = "sk-disabled";
            key.Name = "disabled";
            key.Type = "service_account";
            key.Status = "disabled";
            key.ProjectId = ProjectId;
        });

        foreach (var token in new[] { "sk-missing", "sk-disabled", "sk-user" })
        {
            using var response = await PostGraphQlAsync(IntrospectionQuery, token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(
                """{"error":{"type":"Unauthorized","message":"Invalid API key"}}""",
                await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task KeysWithADisallowedIpAreForbidden()
    {
        var restricted = AddServiceKey(
            "sk-restricted",
            FullScopes,
            ProjectId,
            configure: key => key.AllowedIps = """["10.0.0.1"]""");

        using var response = await PostGraphQlAsync(IntrospectionQuery, restricted.Key);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(
            """{"error":{"type":"Forbidden","message":"IP address is not allowed for this API key"}}""",
            await response.Content.ReadAsStringAsync());
    }

    // ---------------------------------------------------------------------
    // Query.apiKey
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ApiKeyQueryProjectsTheOpenApiShapeByIdKeyAndName()
    {
        var target = _gate.ApiKeys.Add(key =>
        {
            key.Key = "sk-target";
            key.Name = "target";
            key.Type = "service_account";
            key.Status = "enabled";
            key.ProjectId = ProjectId;
            key.Scopes = FullScopes;
            key.Profiles = ApiKeyProfilesCodec.Serialize(new ApiKeyProfilesModel
            {
                ActiveProfile = "ops",
                Profiles =
                [
                    new ApiKeyProfileModel
                    {
                        Name = "ops",
                        ModelMappings = [new ApiKeyModelMapping { From = "a", To = "b" }],
                        ChannelIDs = [3, 5],
                        ChannelTags = ["t1"],
                        ChannelTagsMatchMode = "all",
                        ModelIDs = ["m1"],
                        Quota = new ApiKeyQuotaModel
                        {
                            Requests = 10,
                            TotalTokens = 1000,
                            Cost = 1.25m,
                            Period = new ApiKeyQuotaPeriodModel { Type = "all_time" },
                        },
                    },
                ],
            });
        });

        foreach (var variables in new object[]
                 {
                     new { key = "sk-target", id = (string?)null, name = (string?)null },
                     new
                     {
                         key = (string?)null,
                         id = new GidValue("APIKey", target.Id).ToString(),
                         name = (string?)null,
                     },
                     new { key = (string?)null, id = (string?)null, name = "target" },
                 })
        {
            var data = await QueryDataAsync(ApiKeyQuery, variables);
            var apiKey = data.GetProperty("apiKey");

            Assert.Equal(new GidValue("APIKey", target.Id).ToString(), apiKey.GetProperty("id").GetString());
            Assert.Equal("sk-target", apiKey.GetProperty("key").GetString());
            Assert.Equal("target", apiKey.GetProperty("name").GetString());
            Assert.Equal(
                ["read_api_keys", "write_api_keys", "read_channels", "write_requests"],
                apiKey.GetProperty("scopes").EnumerateArray()
                    .Select(static scope => scope.GetString()!)
                    .ToArray());

            var profiles = apiKey.GetProperty("profiles");
            Assert.Equal("ops", profiles.GetProperty("activeProfile").GetString());
            var profile = profiles.GetProperty("profiles").EnumerateArray().Single();
            Assert.Equal("ops", profile.GetProperty("name").GetString());
            Assert.Equal(
                """[{"from":"a","to":"b"}]""",
                profile.GetProperty("modelMappings").GetRawText());
            Assert.Equal("[3,5]", profile.GetProperty("channelIDs").GetRawText());
            Assert.Equal("""["t1"]""", profile.GetProperty("channelTags").GetRawText());
            Assert.Equal("all", profile.GetProperty("channelTagsMatchMode").GetString());
            Assert.Equal("""["m1"]""", profile.GetProperty("modelIDs").GetRawText());
            Assert.Equal(JsonValueKind.Null, profile.GetProperty("loadBalanceStrategy").ValueKind);
            Assert.Equal(JsonValueKind.Null, profile.GetProperty("traceStickyMode").ValueKind);

            var quota = profile.GetProperty("quota");
            Assert.Equal(10, quota.GetProperty("requests").GetInt32());
            Assert.Equal(1000, quota.GetProperty("totalTokens").GetInt32());
            Assert.Equal(1.25m, quota.GetProperty("cost").GetDecimal());
            Assert.Equal("all_time", quota.GetProperty("period").GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task ApiKeyQueryRequiresExactlyOneIdentifier()
    {
        Assert.Equal(
            "exactly one of api key id, key, or name must be provided",
            await ErrorMessageAsync(ApiKeyQuery, new { key = (string?)null, id = (string?)null, name = (string?)null }));

        Assert.Equal(
            "exactly one of api key id, key, or name must be provided",
            await ErrorMessageAsync(ApiKeyQuery, new { key = "sk-openapi", id = (string?)null, name = "target" }));
    }

    [Fact]
    public async Task ApiKeyQueryRejectsGuidsOfAnotherTypeBeforeLookingThemUp()
    {
        Assert.Equal(
            "invalid id: expected a APIKey GUID, got User",
            await ErrorMessageAsync(
                ApiKeyQuery,
                new { key = (string?)null, id = "gid://axonhub/User/1", name = (string?)null }));

        Assert.Equal(
            """strconv.Atoi: parsing "x": invalid syntax""",
            await ErrorMessageAsync(
                ApiKeyQuery,
                new { key = (string?)null, id = "gid://axonhub/APIKey/x", name = (string?)null }));
    }

    [Fact]
    public async Task ApiKeyQueryIsDeniedWithoutTheReadScope()
    {
        var error = await SingleErrorAsync(
            ApiKeyQuery,
            new
            {
                key = "sk-openapi",
                id = (string?)null,
                name = (string?)null,
            },
            // The caller may read neither the key nor the project it belongs
            // to, so ent privacy denies before the row is returned.
            _writeOnlyKey.Key);

        // gqlgen's default presenter reports err.Error(); the CodedError and
        // privacy-deny extensions the admin schema adds are absent here.
        Assert.Equal("ent/privacy: deny rule", ErrorMessage(error));
        Assert.False(error.TryGetProperty("extensions", out _), error.ToString());
    }

    [Fact]
    public async Task ApiKeyQueryHidesOtherProjectsAndMissingKeys()
    {
        _gate.ApiKeys.Add(key =>
        {
            key.Key = "sk-elsewhere";
            key.Name = "elsewhere";
            key.Type = "service_account";
            key.Status = "enabled";
            key.ProjectId = OtherProjectId;
        });

        Assert.Equal(
            "ent: api_key not found",
            await ErrorMessageAsync(
                ApiKeyQuery,
                new { key = "sk-elsewhere", id = (string?)null, name = (string?)null }));

        Assert.Equal(
            "ent: api_key not found",
            await ErrorMessageAsync(
                ApiKeyQuery,
                new { key = "sk-missing", id = (string?)null, name = (string?)null }));
    }

    [Fact]
    public async Task ApiKeyQueryReportsAmbiguousNamesWithTheGoMessage()
    {
        foreach (var key in new[] { "sk-dup-1", "sk-dup-2" })
        {
            _gate.ApiKeys.Add(row =>
            {
                row.Key = key;
                row.Name = "dup";
                row.Type = "user";
                row.Status = "enabled";
                row.ProjectId = ProjectId;
            });
        }

        Assert.Equal(
            """multiple API keys are named "dup" in this project; use id or key to identify the key""",
            await ErrorMessageAsync(
                ApiKeyQuery,
                new { key = (string?)null, id = (string?)null, name = "dup" }));
    }

    // ---------------------------------------------------------------------
    // Mutation.createLLMAPIKey
    // ---------------------------------------------------------------------

    [Fact]
    public async Task CreateLlmApiKeyTrimsAndInheritsTheOwnerProject()
    {
        var data = await QueryDataAsync(
            """
            mutation Create($name: String!) {
              createLLMAPIKey(name: $name) {
                id key name scopes
                profiles { activeProfile profiles { name } }
              }
            }
            """,
            new { name = "  agent key  " },
            _serviceKey.Key);

        var created = data.GetProperty("createLLMAPIKey");
        Assert.Equal("agent key", created.GetProperty("name").GetString());
        Assert.Equal(
            ["read_channels", "write_requests"],
            created.GetProperty("scopes").EnumerateArray()
                .Select(static scope => scope.GetString()!)
                .ToArray());
        Assert.Equal("", created.GetProperty("profiles").GetProperty("activeProfile").GetString());

        var key = created.GetProperty("key").GetString()!;
        Assert.StartsWith("ah-", key, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", key[3..]);

        var row = _gate.ApiKeys.Rows.Single(item => item.Key == key);
        Assert.Equal(ProjectId, row.ProjectId);
        Assert.Equal(7, row.UserId);
        Assert.Equal("user", row.Type);
        Assert.Equal(new GidValue("APIKey", row.Id).ToString(), created.GetProperty("id").GetString());
    }

    [Fact]
    public async Task CreateLlmApiKeyRejectsBlankNames()
    {
        Assert.Equal(
            "api key name is required",
            await ErrorMessageAsync(
                """mutation Create($name: String!) { createLLMAPIKey(name: $name) { id } }""",
                new { name = "   " }));
    }

    [Fact]
    public async Task CreateLlmApiKeyRejectsDuplicatesAndRollsTheInsertBack()
    {
        _gate.ApiKeys.Add(row =>
        {
            row.Key = "sk-existing";
            row.Name = "dup";
            row.Type = "user";
            row.Status = "enabled";
            row.ProjectId = ProjectId;
        });

        var before = _gate.ApiKeys.Rows.Count;

        var error = await SingleErrorAsync(
            """mutation Create($name: String!) { createLLMAPIKey(name: $name) { id } }""",
            new { name = "dup" });

        // The OpenAPI schema installs no gqlgen error presenter, so the
        // CodedError's code/resource/field/value extensions are lost.
        Assert.Equal("API Key name 'dup' already exists", ErrorMessage(error));
        Assert.False(error.TryGetProperty("extensions", out _), error.ToString());
        Assert.Equal(before, _gate.ApiKeys.Rows.Count);
    }

    [Fact]
    public async Task CreateLlmApiKeyRequiresTheWriteScope()
    {
        // biz.CreateLLMAPIKey wraps the ent Create/Save error
        // ("failed to create api key: %w"), so the privacy deny arrives inside
        // the server-side message rather than on its own.
        Assert.Equal(
            "failed to create api key: ent/privacy: deny rule",
            await ErrorMessageAsync(
                """mutation Create($name: String!) { createLLMAPIKey(name: $name) { id } }""",
                new { name = "new key" },
                _readOnlyKey.Key));
    }

    // ---------------------------------------------------------------------
    // Mutation.updateAPIKeyProfiles
    // ---------------------------------------------------------------------

    [Fact]
    public async Task UpdateApiKeyProfilesNormalizesNilModelMappingsToAnEmptyList()
    {
        var target = AddTargetKey("update-target", withProfiles: false);

        var data = await QueryDataAsync(
            """
            mutation Update(
                $id: ID,
                $name: String,
                $input: UpdateAPIKeyProfilesInput!
            ) {
              updateAPIKeyProfiles(id: $id, name: $name, input: $input) {
                id
                profiles {
                  activeProfile
                  profiles {
                    name
                    modelMappings { from to }
                    loadBalanceStrategy
                    traceStickyMode
                  }
                }
              }
            }
            """,
            new
            {
                id = (string?)null,
                name = "update-target",
                input = new
                {
                    activeProfile = "ops",
                    profiles = new[] { new { name = "ops" } },
                },
            },
            _serviceKey.Key);

        var profile = data.GetProperty("updateAPIKeyProfiles")
            .GetProperty("profiles")
            .GetProperty("profiles")
            .EnumerateArray()
            .Single();

        // The resolver coerces the omitted (nil) modelMappings to [] before
        // saving; nil would otherwise survive as JSON null.
        Assert.Equal("[]", profile.GetProperty("modelMappings").GetRawText());
        Assert.Equal("default", profile.GetProperty("loadBalanceStrategy").GetString());
        Assert.Equal("default", profile.GetProperty("traceStickyMode").GetString());

        Assert.Contains(
            "\"modelMappings\":[]",
            target.Profiles,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateApiKeyProfilesOnlyReachesKeysInTheCallersProject()
    {
        AddTargetKey("elsewhere", withProfiles: false, projectId: OtherProjectId);

        Assert.Equal(
            "ent: api_key not found",
            await ErrorMessageAsync(
                """
                mutation Update($id: ID, $name: String, $input: UpdateAPIKeyProfilesInput!) {
                  updateAPIKeyProfiles(id: $id, name: $name, input: $input) { id }
                }
                """,
                new
                {
                    id = (string?)null,
                    name = "elsewhere",
                    input = new { activeProfile = "", profiles = Array.Empty<object>() },
                }));
    }

    // ---------------------------------------------------------------------
    // Mutation.loadApiKeyProfileTemplate
    // ---------------------------------------------------------------------

    [Fact]
    public async Task LoadApiKeyProfileTemplateAppendsTheProfile()
    {
        var template = _templates.Add(row =>
        {
            row.Name = "Production";
            row.ProjectId = ProjectId;
            row.Profile = ApiKeyProfilesCodec.SerializeProfile(
                ApiKeyProfilesCodec.NormalizeAndValidateRoutingPolicy(new ApiKeyProfileModel
                {
                    Name = "prod-profile",
                    ModelMappings = [new ApiKeyModelMapping { From = "old", To = "new" }],
                }));
        });

        var target = _gate.ApiKeys.Add(row =>
        {
            row.Key = "sk-load";
            row.Name = "load-target";
            row.Type = "service_account";
            row.Status = "enabled";
            row.ProjectId = ProjectId;
            row.Profiles = ApiKeyProfilesCodec.Serialize(new ApiKeyProfilesModel
            {
                ActiveProfile = "main",
                Profiles = [new ApiKeyProfileModel { Name = "main" }],
            });
        });

        var data = await QueryDataAsync(
            """
            mutation Load($input: LoadApiKeyProfileTemplateInput!) {
              loadApiKeyProfileTemplate(input: $input) {
                id
                profiles {
                  activeProfile
                  profiles { name modelMappings { from to } }
                }
              }
            }
            """,
            new
            {
                input = new { templateName = "Production", apiKeyName = "load-target" },
            },
            _serviceKey.Key);

        var result = data.GetProperty("loadApiKeyProfileTemplate");
        Assert.Equal(new GidValue("APIKey", target.Id).ToString(), result.GetProperty("id").GetString());
        var profiles = result.GetProperty("profiles");
        Assert.Equal("main", profiles.GetProperty("activeProfile").GetString());
        var list = profiles.GetProperty("profiles").EnumerateArray().ToList();
        Assert.Equal(2, list.Count);
        Assert.Equal("main", list[0].GetProperty("name").GetString());
        Assert.Equal("prod-profile", list[1].GetProperty("name").GetString());
        Assert.Equal(
            """[{"from":"old","to":"new"}]""",
            list[1].GetProperty("modelMappings").GetRawText());

        // The template link is persisted even though the OpenAPI schema omits
        // templateID/templateName from APIKeyProfile.
        var stored = ApiKeyProfilesCodec.Deserialize(target.Profiles);
        Assert.Equal(2, stored.Profiles!.Count);
        Assert.Equal(template.Id, stored.Profiles[1].TemplateId);
        Assert.Equal("Production", stored.Profiles[1].TemplateName);
    }

    [Fact]
    public async Task LoadApiKeyProfileTemplateRequiresOneTemplateAndOneKey()
    {
        // The resolver looks the template up before the key, so the template
        // named in the second case must exist for the key check to be reached.
        _templates.Add(row =>
        {
            row.Name = "Production";
            row.ProjectId = ProjectId;
            row.Profile = ApiKeyProfilesCodec.SerializeProfile(
                ApiKeyProfilesCodec.NormalizeAndValidateRoutingPolicy(
                    new ApiKeyProfileModel { Name = "prod-profile" }));
        });

        Assert.Equal(
            "exactly one of template id or name must be provided",
            await ErrorMessageAsync(
                """
                mutation Load($input: LoadApiKeyProfileTemplateInput!) {
                  loadApiKeyProfileTemplate(input: $input) { id }
                }
                """,
                new { input = new { apiKeyName = "load-target" } }));

        Assert.Equal(
            "exactly one of api key id, key, or name must be provided",
            await ErrorMessageAsync(
                """
                mutation Load($input: LoadApiKeyProfileTemplateInput!) {
                  loadApiKeyProfileTemplate(input: $input) { id }
                }
                """,
                new { input = new { templateName = "Production" } }));
    }

    // ---------------------------------------------------------------------
    // Query.apiKeyQuotaUsages
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ApiKeyQuotaUsagesReturnsTheQuotaRows()
    {
        AddTargetKey("quota-target", withProfiles: false);
        _quotas.Handler = key => key.Name == "quota-target"
            ?
            [
                new ProfileQuotaUsage(
                    "ops",
                    new ApiKeyQuotaModel
                    {
                        Requests = 100,
                        TotalTokens = 5000,
                        Cost = 2.5m,
                        Period = new ApiKeyQuotaPeriodModel { Type = "all_time" },
                    },
                    new QuotaWindow(
                        new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                        null,
                        EndInclusive: false),
                    new QuotaUsage(7, 700, 0.75m)),
            ]
            : [];

        var data = await QueryDataAsync(
            """
            query Quota($apiKeyId: ID, $key: String, $name: String) {
              apiKeyQuotaUsages(apiKeyId: $apiKeyId, key: $key, name: $name) {
                profileName
                quota { requests totalTokens cost period { type } }
                window { start end }
                usage { requestCount totalTokens totalCost }
              }
            }
            """,
            new { apiKeyId = (string?)null, key = "sk-quota-target", name = (string?)null },
            _serviceKey.Key);

        var usage = data.GetProperty("apiKeyQuotaUsages").EnumerateArray().Single();
        Assert.Equal("ops", usage.GetProperty("profileName").GetString());
        Assert.Equal(100, usage.GetProperty("quota").GetProperty("requests").GetInt32());
        Assert.Equal(5000, usage.GetProperty("quota").GetProperty("totalTokens").GetInt32());
        Assert.Equal(2.5m, usage.GetProperty("quota").GetProperty("cost").GetDecimal());
        Assert.Equal(
            "all_time",
            usage.GetProperty("quota").GetProperty("period").GetProperty("type").GetString());
        Assert.Equal(
            "2026-01-01T00:00:00Z",
            usage.GetProperty("window").GetProperty("start").GetString());
        Assert.Equal(JsonValueKind.Null, usage.GetProperty("window").GetProperty("end").ValueKind);
        Assert.Equal(7, usage.GetProperty("usage").GetProperty("requestCount").GetInt32());
        Assert.Equal(700, usage.GetProperty("usage").GetProperty("totalTokens").GetInt32());
        Assert.Equal(0.75m, usage.GetProperty("usage").GetProperty("totalCost").GetDecimal());
    }

    [Fact]
    public async Task ApiKeyQuotaUsagesReturnsTheQuotaServiceErrorUnwrapped()
    {
        AddTargetKey("quota-error", withProfiles: false);
        _quotas.Failure = new InvalidOperationException("quota backend down");

        Assert.Equal(
            "quota backend down",
            await ErrorMessageAsync(
                """
                query Quota($key: String) {
                  apiKeyQuotaUsages(key: $key) { profileName }
                }
                """,
                new { key = "sk-quota-error" }));
    }

    // ---------------------------------------------------------------------
    // Schema shape
    // ---------------------------------------------------------------------

    [Fact]
    public async Task IntrospectionLocksTheOpenApiSchemaShape()
    {
        var data = await QueryDataAsync(
            """
            query Shape {
              apiKeyProfile: __type(name: "APIKeyProfile") { fields { name } }
              apiKey: __type(name: "APIKey") { fields { name } }
              profileInput: __type(name: "APIKeyProfileInput") { inputFields { name } }
              rootQuery: __type(name: "Query") { fields { name } }
              rootMutation: __type(name: "Mutation") { fields { name } }
            }
            """,
            variables: null,
            token: _serviceKey.Key);

        static string[] FieldNames(JsonElement type)
            => type.GetProperty("fields").EnumerateArray()
                .Select(static field => field.GetProperty("name").GetString()!)
                .ToArray();

        Assert.Equal(
            [
                "name", "modelMappings", "channelIDs", "channelTags",
                "channelTagsMatchMode", "modelIDs", "quota",
                "loadBalanceStrategy", "traceStickyMode",
            ],
            FieldNames(data.GetProperty("apiKeyProfile")));

        Assert.Equal(
            ["id", "key", "name", "scopes", "profiles"],
            FieldNames(data.GetProperty("apiKey")));

        Assert.Equal(
            [
                "apiKey", "apiKeyQuotaUsages",
            ],
            FieldNames(data.GetProperty("rootQuery")));

        Assert.Equal(
            [
                "createLLMAPIKey", "updateAPIKeyProfiles",
                "loadApiKeyProfileTemplate",
            ],
            FieldNames(data.GetProperty("rootMutation")));

        var inputNames = data.GetProperty("profileInput")
            .GetProperty("inputFields")
            .EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Contains("channelIDs", inputNames);
        Assert.Contains("modelIDs", inputNames);
        Assert.DoesNotContain("templateID", inputNames);
        Assert.DoesNotContain("templateName", inputNames);
    }

    // ---------------------------------------------------------------------
    // Routes: playground pages and non-POST behavior
    // ---------------------------------------------------------------------

    [Fact]
    public async Task OpenApiPlaygroundServesTheGqlgenPage()
    {
        using var response = await SendAsync(HttpMethod.Get, "/openapi/v1/playground", _serviceKey.Key);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PlaygroundPages.ContentType, response.Content.Headers.ContentType?.ToString());
        Assert.Equal(
            PlaygroundPages.LoadHtml(PlaygroundPages.OpenApiResource),
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AdminPlaygroundNeedsAUserJwtAndServesTheAdminPage()
    {
        using (var response = await SendAsync(HttpMethod.Get, "/admin/playground", _serviceKey.Key))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        using (var response = await SendAsync(HttpMethod.Get, "/admin/playground", _gate.Jwt))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(PlaygroundPages.ContentType, response.Content.Headers.ContentType?.ToString());
            Assert.Equal(
                PlaygroundPages.LoadHtml(PlaygroundPages.AdminResource),
                await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task NonPostGraphQlAndHeadPlaygroundFallThroughToTheStatic404()
    {
        using (var response = await SendAsync(HttpMethod.Get, GraphQlPath, _serviceKey.Key))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal(
                """{"error":{"type":"Not Found","message":"path not found: /openapi/v1/graphql"}}""",
                await response.Content.ReadAsStringAsync());
        }

        foreach (var path in new[] { "/openapi/v1/playground", "/admin/playground" })
        {
            using var response = await SendAsync(HttpMethod.Head, path, _serviceKey.Key);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    // ---------------------------------------------------------------------
    // Webhook echo
    // ---------------------------------------------------------------------

    [Fact]
    public async Task WebhookEchoRequiresAServiceAccountKey()
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/openapi/webhook/echo",
            token: null,
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(
            """{"error":{"type":"Unauthorized","message":"API key is required"}}""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WebhookEchoReplicatesTheGoEncodingJsonShape()
    {
        // bad=%zz lives in ParseGoQueryMirrorsNetUrlParseQuery: .NET's Uri
        // normalizes an invalid escape to %25zz before Kestrel sees it, so the
        // raw parser is the only place the Go behaviour is observable here.
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/openapi/webhook/echo?b=2&a=1&b=3&x&semi=a;b&plus=a+b")
        {
            Content = new StringContent(
                """{"z" : 1, "html":"<b>&"}""",
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceKey.Key);
        request.Headers.TryAddWithoutValidation("X-Custom", ["one", "two"]);

        using var response = await _client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.ToString());
        Assert.EndsWith("\n", raw, StringComparison.Ordinal);

        // struct field order, map keys sorted byte-wise, values in wire order.
        // ';' segments and pairs with invalid escapes are dropped.
        Assert.StartsWith(
            """{"method":"POST","path":"/openapi/webhook/echo","query":{"a":["1"],"b":["2","3"],"plus":["a b"],"x":[""]},"headers":{""",
            raw,
            StringComparison.Ordinal);
        Assert.Contains("\"X-Custom\":[\"one\",\"two\"]", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Host\":", raw, StringComparison.Ordinal);
        Assert.Contains("\"Content-Type\":[\"application/json; charset=utf-8\"]", raw, StringComparison.Ordinal);

        // json.RawMessage is compacted and the whole document gets Go's
        // HTML escaping, including inside the raw body. The escape prefix is
        // built from its char code so the source itself stays escape-free.
        var slash = ((char)92).ToString();
        Assert.Contains(
            """{"z":1,"html":"<b>&"}"""
                .Replace("<", slash + "u003c", StringComparison.Ordinal)
                .Replace(">", slash + "u003e", StringComparison.Ordinal)
                .Replace("&", slash + "u0026", StringComparison.Ordinal),
            raw,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebhookEchoWithInvalidJsonIsA200WithAnEmptyBody()
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/openapi/webhook/echo",
            _serviceKey.Key,
            new StringContent("not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WebhookEchoReadsAtMostOneMebibyte()
    {
        var prefix = """{"a":1}""";
        var body = prefix + new string(' ', (1 << 20) - prefix.Length) + "zzz";
        Assert.True(body.Length > 1 << 20);

        using var response = await SendAsync(
            HttpMethod.Post,
            "/openapi/webhook/echo",
            _serviceKey.Key,
            new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"body\":{\"a\":1}", raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// net/url.ParseQuery directly. The integration tests above cannot carry an
    /// invalid percent escape because .NET's <see cref="Uri"/> rewrites it to
    /// %25xx before the request reaches the server.
    /// </summary>
    [Fact]
    public void ParseGoQueryMirrorsNetUrlParseQuery()
    {
        var parsed = WebhookEchoEndpoint.ParseGoQuery(
            "?b=2&a=1&b=3&x&bad=%zz&semi=a;b&plus=a+b&esc=%2B&%E4%B8%AD=%41&%zz=1");

        // Invalid escapes drop the whole pair ('bad', key '%zz'); segments with
        // ';' are skipped; keys sort byte-wise (U+4E2D after ASCII).
        Assert.Equal(
            ["a", "b", "esc", "plus", "x", "中"],
            parsed.Keys.ToArray());
        Assert.Equal(["1"], parsed["a"]);
        Assert.Equal(["2", "3"], parsed["b"]);
        Assert.Equal(new[] { "+" }, parsed["esc"]);
        Assert.Equal(["a b"], parsed["plus"]);
        Assert.Equal([""], parsed["x"]);
        Assert.Equal(["A"], parsed["中"]);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private const string GraphQlPath = "/openapi/v1/graphql";

    private const string IntrospectionQuery = "query { __typename }";

    private const string ApiKeyQuery = """
        query Key($id: ID, $key: String, $name: String) {
          apiKey(id: $id, key: $key, name: $name) {
            id key name scopes
            profiles {
              activeProfile
              profiles {
                name
                modelMappings { from to }
                channelIDs channelTags channelTagsMatchMode modelIDs
                quota { requests totalTokens cost period { type } }
                loadBalanceStrategy traceStickyMode
              }
            }
          }
        }
        """;

    private ApiKeyRecord AddServiceKey(
        string key,
        string scopes,
        long projectId,
        long? userId = null,
        Action<ApiKeyRecord>? configure = null)
        => _gate.ApiKeys.Add(row =>
        {
            row.Key = key;
            row.Name = key;
            row.Type = "service_account";
            row.Status = "enabled";
            row.Scopes = scopes;
            row.ProjectId = projectId;
            row.UserId = userId;
            configure?.Invoke(row);
        });

    private ApiKeyRecord AddTargetKey(
        string name,
        bool withProfiles,
        long projectId = ProjectId)
        => _gate.ApiKeys.Add(row =>
        {
            row.Key = "sk-" + name;
            row.Name = name;
            row.Type = "service_account";
            row.Status = "enabled";
            row.ProjectId = projectId;
            if (!withProfiles)
            {
                row.Profiles = ApiKeyProfilesCodec.DefaultJson;
            }
        });

    private static StringContent JsonContent(string query, object? variables = null)
        => new(
            JsonSerializer.Serialize(new { query, variables }),
            Encoding.UTF8,
            "application/json");

    private async Task<HttpResponseMessage> PostGraphQlAsync(
        string query,
        string? token,
        object? variables = null)
        => await SendAsync(
            HttpMethod.Post,
            GraphQlPath,
            token,
            JsonContent(query, variables));

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? token,
        HttpContent? content = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _client.SendAsync(request);
    }

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables,
        string? token = null)
    {
        using var response = await PostGraphQlAsync(query, token ?? _serviceKey.Key, variables);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement.Clone();
        Assert.False(root.TryGetProperty("errors", out _), body);
        return root.GetProperty("data");
    }

    private async Task<JsonElement> SingleErrorAsync(
        string query,
        object? variables,
        string? token = null)
    {
        using var response = await PostGraphQlAsync(query, token ?? _serviceKey.Key, variables);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement.Clone();
        Assert.True(root.TryGetProperty("errors", out var errors), body);
        return errors.EnumerateArray().Single().Clone();
    }

    private async Task<string> ErrorMessageAsync(
        string query,
        object? variables,
        string? token = null)
        => ErrorMessage(await SingleErrorAsync(query, variables, token));

    private static string ErrorMessage(JsonElement error)
    {
        Assert.False(error.TryGetProperty("extensions", out var extensions), extensions.ToString());
        return error.GetProperty("message").GetString()!;
    }

    /// <summary>
    /// Mirrors the Program.cs openapi routing block: POST-only GraphQL, the
    /// non-POST static 404, the HEAD playground fallback, the openapi group
    /// gate, and the admin playground's JWT gate.
    /// </summary>
    private static void MapOpenApi(WebApplication app)
    {
        app.MapGraphQL(GraphQlPath, "openapi")
            .WithOptions(static options => options.EnableGetRequests = false)
            .WithMetadata(new HttpMethodMetadata([HttpMethods.Post]));
        app.MapGet("/admin/playground", PlaygroundPages.ServeAdminAsync);
        app.MapGet("/openapi/v1/playground", PlaygroundPages.ServeOpenApiAsync);
        app.MapPost("/openapi/webhook/echo", WebhookEchoEndpoint.HandleAsync);

        app.UseWhen(
            static context => context.Request.Path.Value == GraphQlPath
                && !HttpMethods.IsPost(context.Request.Method),
            static branch => branch.Run(context =>
                StaticHandler.WriteApiNotFoundAsync(
                    context,
                    context.Request.Path.Value ?? "/")));

        app.UseWhen(
            static context => HttpMethods.IsHead(context.Request.Method)
                && (context.Request.Path.Value == "/admin/playground"
                    || context.Request.Path.Value == "/openapi/v1/playground"),
            static branch => branch.Run(context =>
                StaticHandler.WriteApiNotFoundAsync(
                    context,
                    context.Request.Path.Value ?? "/")));

        app.UseWhen(
            static context => context.Request.Path.Value == GraphQlPath
                || context.Request.Path.Value == "/openapi/v1/playground"
                || context.Request.Path.Value == "/openapi/webhook/echo",
            static branch =>
            {
                branch.UseMiddleware<IpBlocklistMiddleware>();
                branch.UseMiddleware<OpenApiAuthMiddleware>();
            });

        app.UseWhen(
            static context => context.Request.Path.Value == "/admin/playground"
                && HttpMethods.IsGet(context.Request.Method),
            static branch =>
            {
                branch.UseMiddleware<AdminJwtAuthMiddleware>();
                branch.UseMiddleware<ProjectIdMiddleware>();
            });
    }

    private sealed class StubQuotaReader : IApiKeyProfileQuotaReader
    {
        public Func<ApiKeyRecord, IReadOnlyList<ProfileQuotaUsage>>? Handler { get; set; }

        public Exception? Failure { get; set; }

        public Task<IReadOnlyList<ProfileQuotaUsage>> ProfileQuotaUsagesAsync(
            ApiKeyRecord apiKey,
            CancellationToken cancellationToken = default)
        {
            _ = cancellationToken;
            if (Failure is not null)
            {
                return Task.FromException<IReadOnlyList<ProfileQuotaUsage>>(Failure);
            }

            return Task.FromResult(
                Handler?.Invoke(apiKey) ?? (IReadOnlyList<ProfileQuotaUsage>)[]);
        }
    }
}
