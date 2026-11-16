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
/// Locks the DataStorage GraphQL surface over /admin/graphql against the
/// queries and mutations the frontend sends (frontend/src/features/
/// data-storages): gid IDs, the DataStorageSettings JSON shape, the ported
/// where predicates, the entgql pagination rules and the reference mutation
/// semantics from internal/server/biz/data_storage.go at f272549f, including
/// the read/write data storage scope policy.
/// </summary>
public sealed class AdminDataStoragesEndpointTests : IAsyncLifetime
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private const string S3Settings =
        """{"dsn":null,"directory":null,"s3":{"bucketName":"bucket","endpoint":"https://s3.example.com","region":"us-east-1","accessKey":"ak","secretKey":"sk","pathStyle":true},"gcs":null,"webdav":null}""";

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeDataStorageStore _storages = new();

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
        builder.Services.AddSingleton<IDataStorageStore>(_storages);
        builder.Services.AddScoped<PermissionValidator>();
        builder.Services.AddScoped<DataStoragePrivacy>();
        builder.Services.AddScoped<DataStorageManagementService>();
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
    public async Task DataStoragesQueryMatchesTheFrontendShape()
    {
        var storage = _storages.Seed(record =>
        {
            record.Name = "direct-s3";
            record.Description = "object store";
            record.Type = "s3";
            record.Settings = S3Settings;
            record.CreatedAt = CreatedAt;
            record.UpdatedAt = UpdatedAt;
        });

        var data = await QueryDataAsync(
            """
            query DataStorages($first: Int) {
              dataStorages(first: $first) {
                totalCount
                pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                edges {
                  cursor
                  node {
                    id
                    createdAt
                    updatedAt
                    name
                    description
                    primary
                    type
                    status
                    settings {
                      dsn
                      directory
                      s3 { bucketName endpoint region pathStyle }
                      gcs { bucketName }
                      webdav { url username password insecure_skip_tls path }
                    }
                  }
                }
              }
            }
            """,
            new { first = 10 });

        var connection = data.GetProperty("dataStorages");
        Assert.Equal(1, connection.GetProperty("totalCount").GetInt32());
        var pageInfo = connection.GetProperty("pageInfo");
        Assert.False(pageInfo.GetProperty("hasNextPage").GetBoolean());
        Assert.False(pageInfo.GetProperty("hasPreviousPage").GetBoolean());
        Assert.False(string.IsNullOrEmpty(pageInfo.GetProperty("endCursor").GetString()));

        var edge = Assert.Single(connection.GetProperty("edges").EnumerateArray());
        Assert.False(string.IsNullOrEmpty(edge.GetProperty("cursor").GetString()));
        var node = edge.GetProperty("node");

        Assert.Equal("gid://axonhub/DataStorage/" + storage.Id, node.GetProperty("id").GetString());
        Assert.Equal("2026-01-02T03:04:05Z", node.GetProperty("createdAt").GetString());
        Assert.Equal("2026-02-03T04:05:06Z", node.GetProperty("updatedAt").GetString());
        Assert.Equal("direct-s3", node.GetProperty("name").GetString());
        Assert.Equal("object store", node.GetProperty("description").GetString());
        Assert.False(node.GetProperty("primary").GetBoolean());
        Assert.Equal("s3", node.GetProperty("type").GetString());
        Assert.Equal("active", node.GetProperty("status").GetString());

        var settings = node.GetProperty("settings");
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("dsn").ValueKind);
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("directory").ValueKind);
        var s3 = settings.GetProperty("s3");
        Assert.Equal("bucket", s3.GetProperty("bucketName").GetString());
        Assert.Equal("https://s3.example.com", s3.GetProperty("endpoint").GetString());
        Assert.Equal("us-east-1", s3.GetProperty("region").GetString());
        Assert.True(s3.GetProperty("pathStyle").GetBoolean());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("gcs").ValueKind);
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("webdav").ValueKind);

        // S3 credentials are never exposed by the output type.
        Assert.False(s3.TryGetProperty("accessKey", out _));
    }

    [Fact]
    public async Task WebDavAndGcsSettingsKeepTheReferenceShape()
    {
        _storages.Seed(record =>
        {
            record.Name = "dav";
            record.Type = "webdav";
            record.Settings =
                """{"dsn":null,"directory":null,"s3":null,"gcs":null,"webdav":{"url":"https://dav.example.com","username":"user","password":"secret","insecure_skip_tls":true,"path":"/backups"}}""";
        });
        _storages.Seed(record =>
        {
            record.Name = "gcs";
            record.Type = "gcs";
            record.Settings =
                """{"dsn":null,"directory":null,"s3":null,"gcs":{"bucketName":"gbucket","credential":"{}"},"webdav":null}""";
        });

        var data = await QueryDataAsync(
            """
            query DataStorages {
              dataStorages(first: 10, orderBy: { field: CREATED_AT, direction: ASC }) {
                edges {
                  node {
                    name
                    type
                    settings {
                      webdav { url username password insecure_skip_tls path }
                      gcs { bucketName }
                    }
                  }
                }
              }
            }
            """);

        var nodes = data.GetProperty("dataStorages").GetProperty("edges")
            .EnumerateArray()
            .Select(static edge => edge.GetProperty("node"))
            .ToArray();

        var dav = nodes.Single(static node => node.GetProperty("name").GetString() == "dav");
        Assert.Equal("webdav", dav.GetProperty("type").GetString());
        var webDav = dav.GetProperty("settings").GetProperty("webdav");
        Assert.Equal("https://dav.example.com", webDav.GetProperty("url").GetString());
        Assert.Equal("user", webDav.GetProperty("username").GetString());
        Assert.Equal("secret", webDav.GetProperty("password").GetString());
        Assert.True(webDav.GetProperty("insecure_skip_tls").GetBoolean());
        Assert.Equal("/backups", webDav.GetProperty("path").GetString());

        var gcs = nodes.Single(static node => node.GetProperty("name").GetString() == "gcs");
        Assert.Equal("gcs", gcs.GetProperty("type").GetString());
        // The GCS credential is not part of the output type.
        var gcsSettings = gcs.GetProperty("settings").GetProperty("gcs");
        Assert.Equal("gbucket", gcsSettings.GetProperty("bucketName").GetString());
        Assert.False(gcsSettings.TryGetProperty("credential", out _));
    }

    [Fact]
    public async Task DataStoragesWhereAndUpdatedAtOrderDriveKeysetPagination()
    {
        _storages.Seed(record =>
        {
            record.Name = "Alpha";
            record.Type = "s3";
            record.UpdatedAt = UpdatedAt.AddHours(-2);
        });
        _storages.Seed(record =>
        {
            record.Name = "Beta";
            record.Type = "s3";
            record.UpdatedAt = UpdatedAt;
        });
        _storages.Seed(record =>
        {
            record.Name = "Gamma";
            record.Type = "s3";
            record.Status = "archived";
            record.UpdatedAt = UpdatedAt.AddHours(-1);
        });
        _storages.Seed(record =>
        {
            record.Name = "Delta";
            record.Type = "fs";
            record.UpdatedAt = UpdatedAt.AddHours(-3);
        });

        var variables = new
        {
            where = new
            {
                nameContainsFold = "a",
                typeIn = new[] { "s3" },
                statusIn = new[] { "active" },
            },
            orderBy = new { field = "UPDATED_AT", direction = "DESC" },
        };
        var data = await QueryDataAsync(
            """
            query DataStorages(
              $where: DataStorageWhereInput,
              $orderBy: DataStorageOrder,
              $first: Int) {
              dataStorages(first: $first, where: $where, orderBy: $orderBy) {
                totalCount
                pageInfo { hasNextPage hasPreviousPage endCursor }
                edges { cursor node { id name } }
              }
            }
            """,
            new { variables.where, variables.orderBy, first = 1 });

        var connection = data.GetProperty("dataStorages");
        // totalCount counts the rows that pass the where input.
        Assert.Equal(2, connection.GetProperty("totalCount").GetInt32());
        Assert.True(connection.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());
        var firstEdge = Assert.Single(connection.GetProperty("edges").EnumerateArray());
        Assert.Equal("Beta", firstEdge.GetProperty("node").GetProperty("name").GetString());

        var query = Assert.Single(_storages.Queries);
        Assert.Equal("a", query.Where?.NameContainsFold);
        Assert.Equal(["s3"], query.Where?.TypeIn);
        Assert.Equal(["active"], query.Where?.StatusIn);
        Assert.Equal(DataStorageSortField.UpdatedAt, query.SortField);
        Assert.Equal(SortDirection.Desc, query.Direction);

        var after = await QueryDataAsync(
            """
            query DataStorages(
              $where: DataStorageWhereInput,
              $orderBy: DataStorageOrder,
              $first: Int,
              $after: Cursor) {
              dataStorages(first: $first, where: $where, orderBy: $orderBy, after: $after) {
                totalCount
                pageInfo { hasNextPage hasPreviousPage }
                edges { node { name } }
              }
            }
            """,
            new
            {
                variables.where,
                variables.orderBy,
                first = 1,
                after = firstEdge.GetProperty("cursor").GetString(),
            });

        var next = after.GetProperty("dataStorages");
        Assert.Equal(
            "Alpha",
            Assert.Single(next.GetProperty("edges").EnumerateArray())
                .GetProperty("node").GetProperty("name").GetString());
        Assert.False(next.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());
        Assert.True(next.GetProperty("pageInfo").GetProperty("hasPreviousPage").GetBoolean());
    }

    [Fact]
    public async Task CreatedAtOrderFallsBackToTheDefaultIdOrder()
    {
        var first = _storages.Seed(record => record.Name = "first");
        var second = _storages.Seed(record => record.Name = "second");

        var data = await QueryDataAsync(
            """
            query DataStorages {
              dataStorages(first: 10, orderBy: { field: CREATED_AT, direction: DESC }) {
                edges { node { id } }
              }
            }
            """);

        // DefaultDataStorageOrder is id; the resolver keeps the direction.
        var ids = data.GetProperty("dataStorages").GetProperty("edges").EnumerateArray()
            .Select(static edge => edge.GetProperty("node").GetProperty("id").GetString()!)
            .ToArray();
        Assert.Equal(
            [
                "gid://axonhub/DataStorage/" + second.Id,
                "gid://axonhub/DataStorage/" + first.Id,
            ],
            ids);
        Assert.Equal(DataStorageSortField.Id, Assert.Single(_storages.Queries).SortField);
    }

    [Fact]
    public async Task ExplicitFalsePrimaryIsAPredicate()
    {
        var primary = _storages.Seed(record =>
        {
            record.Name = "primary";
            record.Primary = true;
        });
        var plain = _storages.Seed(record =>
        {
            record.Name = "plain";
            record.Primary = false;
        });

        var data = await QueryDataAsync(
            """
            query DataStorages($where: DataStorageWhereInput) {
              dataStorages(first: 10, where: $where) {
                totalCount
                edges { node { id name } }
              }
            }
            """,
            new { where = new { primary = false } });

        var node = Assert.Single(
            data.GetProperty("dataStorages").GetProperty("edges").EnumerateArray())
            .GetProperty("node");
        Assert.Equal("gid://axonhub/DataStorage/" + plain.Id, node.GetProperty("id").GetString());
        Assert.Equal(false, Assert.Single(_storages.Queries).Where?.Primary);
        Assert.NotEqual(primary.Id, plain.Id);
    }

    [Fact]
    public async Task CreateDataStorageAppliesDefaultsAndSerializesEverySetting()
    {
        var data = await QueryDataAsync(
            """
            mutation Create($input: CreateDataStorageInput!) {
              createDataStorage(input: $input) {
                id
                name
                description
                primary
                type
                status
                settings {
                  dsn
                  directory
                  s3 { bucketName endpoint region pathStyle }
                  gcs { bucketName }
                  webdav { url username insecure_skip_tls path }
                }
              }
            }
            """,
            new
            {
                input = new
                {
                    name = "created",
                    description = "desc",
                    type = "s3",
                    // Accepted by the SDL but ignored: create always writes active.
                    status = "archived",
                    settings = new
                    {
                        dsn = "postgres://dsn",
                        directory = "/data",
                        s3 = new
                        {
                            bucketName = "bckt",
                            endpoint = "https://s3",
                            region = "eu",
                            accessKey = "ak",
                            secretKey = "sk",
                            pathStyle = true,
                        },
                        gcs = new { bucketName = "gb", credential = "cred" },
                        webdav = new
                        {
                            url = "https://dav",
                            username = "u",
                            password = "p",
                            insecure_skip_tls = true,
                            path = "/p",
                        },
                    },
                },
            });

        var created = data.GetProperty("createDataStorage");
        Assert.StartsWith("gid://axonhub/DataStorage/", created.GetProperty("id").GetString());
        Assert.Equal("created", created.GetProperty("name").GetString());
        Assert.Equal("desc", created.GetProperty("description").GetString());
        Assert.False(created.GetProperty("primary").GetBoolean());
        Assert.Equal("s3", created.GetProperty("type").GetString());
        Assert.Equal("active", created.GetProperty("status").GetString());

        var settings = created.GetProperty("settings");
        Assert.Equal("postgres://dsn", settings.GetProperty("dsn").GetString());
        Assert.Equal("/data", settings.GetProperty("directory").GetString());
        Assert.Equal("bckt", settings.GetProperty("s3").GetProperty("bucketName").GetString());
        Assert.True(settings.GetProperty("s3").GetProperty("pathStyle").GetBoolean());
        Assert.Equal("gb", settings.GetProperty("gcs").GetProperty("bucketName").GetString());
        Assert.Equal("https://dav", settings.GetProperty("webdav").GetProperty("url").GetString());

        var stored = Assert.Single(_storages.Rows);
        Assert.Equal(
            """{"dsn":"postgres://dsn","directory":"/data","s3":{"bucketName":"bckt","endpoint":"https://s3","region":"eu","accessKey":"ak","secretKey":"sk","pathStyle":true},"gcs":{"bucketName":"gb","credential":"cred"},"webdav":{"url":"https://dav","username":"u","password":"p","insecure_skip_tls":true,"path":"/p"}}""",
            stored.Settings);
        Assert.Equal("active", stored.Status);
        Assert.False(stored.Primary);
        Assert.NotEqual(default, stored.CreatedAt);
    }

    [Fact]
    public async Task CreateDuplicateNameReturnsTheCodedErrorExtensions()
    {
        _storages.Seed(record => record.Name = "dup");

        var errors = await QueryErrorsAsync(
            """
            mutation Create($input: CreateDataStorageInput!) {
              createDataStorage(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    name = "dup",
                    description = "",
                    type = "fs",
                    settings = new { directory = "/data" },
                },
            });

        var error = errors[0];
        Assert.Equal("data storage name 'dup' already exists", error.GetProperty("message").GetString());
        var extensions = error.GetProperty("extensions");
        Assert.Equal("DUPLICATE_NAME", extensions.GetProperty("code").GetString());
        Assert.Equal("data storage", extensions.GetProperty("resource").GetString());
        Assert.Equal("name", extensions.GetProperty("field").GetString());
        Assert.Equal("dup", extensions.GetProperty("value").GetString());

        Assert.Single(_storages.Rows);
    }

    [Fact]
    public async Task UpdateMergesSettingsAndPreservesStoredCredentials()
    {
        var storage = _storages.Seed(record =>
        {
            record.Name = "s3-storage";
            record.Description = "existing description";
            record.Type = "s3";
            record.Settings =
                """{"dsn":"keep-dsn","directory":"/old","s3":{"bucketName":"old-bucket","endpoint":"https://old","region":"old-region","accessKey":"old-ak","secretKey":"old-sk","pathStyle":true},"gcs":null,"webdav":null}""";
        });

        var data = await QueryDataAsync(
            """
            mutation Update($id: ID!, $input: UpdateDataStorageInput!) {
              updateDataStorage(id: $id, input: $input) {
                id
                name
                description
                status
                settings {
                  dsn
                  directory
                  s3 { bucketName endpoint region pathStyle }
                }
              }
            }
            """,
            new
            {
                id = Gid(storage.Id),
                input = new
                {
                    name = "renamed",
                    status = "archived",
                    settings = new
                    {
                        directory = "/new",
                        s3 = new { bucketName = "new-bucket" },
                    },
                },
            });

        var node = data.GetProperty("updateDataStorage");
        Assert.Equal("renamed", node.GetProperty("name").GetString());
        Assert.Equal("existing description", node.GetProperty("description").GetString());
        Assert.Equal("archived", node.GetProperty("status").GetString());
        Assert.Equal("keep-dsn", node.GetProperty("settings").GetProperty("dsn").GetString());
        Assert.Equal("/new", node.GetProperty("settings").GetProperty("directory").GetString());
        Assert.Equal(
            "new-bucket",
            node.GetProperty("settings").GetProperty("s3").GetProperty("bucketName").GetString());

        // mergeSettings: the provided S3 block wins (including its empty
        // endpoint/region and reset pathStyle) but the empty accessKey and
        // secretKey keep the stored credentials.
        Assert.Equal(
            """{"dsn":"keep-dsn","directory":"/new","s3":{"bucketName":"new-bucket","endpoint":"","region":"","accessKey":"old-ak","secretKey":"old-sk","pathStyle":false},"gcs":null,"webdav":null}""",
            storage.Settings);
        Assert.Equal("archived", storage.Status);
    }

    [Fact]
    public async Task UpdateRejectsADuplicateRenameAndAllowsKeepingItsOwnName()
    {
        var first = _storages.Seed(record => record.Name = "first");
        _storages.Seed(record => record.Name = "second");

        var errors = await QueryErrorsAsync(
            """
            mutation Update($id: ID!, $input: UpdateDataStorageInput!) {
              updateDataStorage(id: $id, input: $input) { id }
            }
            """,
            new { id = Gid(first.Id), input = new { name = "second" } });

        var error = errors[0];
        Assert.Equal("data storage name 'second' already exists", error.GetProperty("message").GetString());
        Assert.Equal(
            "DUPLICATE_NAME",
            error.GetProperty("extensions").GetProperty("code").GetString());
        Assert.Equal("first", first.Name);

        // Renaming to the current name skips the duplicate query entirely.
        var data = await QueryDataAsync(
            """
            mutation Update($id: ID!, $input: UpdateDataStorageInput!) {
              updateDataStorage(id: $id, input: $input) { name }
            }
            """,
            new { id = Gid(first.Id), input = new { name = "first" } });
        Assert.Equal("first", data.GetProperty("updateDataStorage").GetProperty("name").GetString());
    }

    [Fact]
    public async Task UpdateMissingDataStorageReturnsTheWrappedGetError()
    {
        var errors = await QueryErrorsAsync(
            """
            mutation Update($id: ID!, $input: UpdateDataStorageInput!) {
              updateDataStorage(id: $id, input: $input) { id }
            }
            """,
            new { id = Gid(404), input = new { name = "ghost" } });

        Assert.Equal(
            "failed to get data storage: ent: data storage not found",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_storages.Rows);
    }

    [Fact]
    public async Task NodeResolverReturnsTheDataStorage()
    {
        var storage = _storages.Seed(record =>
        {
            record.Name = "greeting";
            record.Type = "fs";
            record.Settings = """{"dsn":null,"directory":"/data","s3":null,"gcs":null,"webdav":null}""";
        });

        var data = await QueryDataAsync(
            """
            query Node($id: ID!) {
              node(id: $id) { id ... on DataStorage { name type settings { directory } } }
            }
            """,
            new { id = Gid(storage.Id) });

        var node = data.GetProperty("node");
        Assert.Equal(Gid(storage.Id), node.GetProperty("id").GetString());
        Assert.Equal("greeting", node.GetProperty("name").GetString());
        Assert.Equal("fs", node.GetProperty("type").GetString());
        Assert.Equal(
            "/data",
            node.GetProperty("settings").GetProperty("directory").GetString());

        var errors = await QueryErrorsAsync(
            """
            query Node($id: ID!) { node(id: $id) { id } }
            """,
            new { id = Gid(404) });
        Assert.Equal("ent: data storage not found", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ServiceAccountApiKeyWithoutTheScopeIsForbidden()
    {
        _storages.Seed(record => record.Name = "secret");
        // The gate's default key carries read_channels/write_requests only.
        _gate.AddServiceAccountKey();

        var errors = await QueryErrorsAsync(
            """
            query DataStorages { dataStorages(first: 10) { totalCount } }
            """,
            token: "sk-service-account");

        Assert.Equal("permission denied", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ServiceAccountApiKeyWithTheScopeReadsDataStorages()
    {
        _storages.Seed(record => record.Name = "visible");
        _gate.AddServiceAccountKey(
            configure: key => key.Scopes = """["read_data_storages"]""");

        var data = await QueryDataAsync(
            """
            query DataStorages { dataStorages(first: 10) { totalCount edges { node { name } } } }
            """,
            token: "sk-service-account");

        var connection = data.GetProperty("dataStorages");
        Assert.Equal(1, connection.GetProperty("totalCount").GetInt32());
        Assert.Equal(
            "visible",
            Assert.Single(connection.GetProperty("edges").EnumerateArray())
                .GetProperty("node").GetProperty("name").GetString());
    }

    [Fact]
    public async Task UserWithoutTheReadScopeIsDeniedWithTheDefaultRule()
    {
        var member = _gate.Users.Add(user =>
        {
            user.Id = 77;
            user.Email = "member@example.com";
            user.Status = "activated";
            user.IsOwner = false;
        });
        await _users.CreateAsync(
            new UserRecord
            {
                Id = member.Id,
                Email = member.Email,
                Status = "activated",
                IsOwner = false,
            },
            roleIds: null);
        var token = _gate.IssueJwt(member);

        var errors = await QueryErrorsAsync(
            """
            query DataStorages { dataStorages(first: 10) { totalCount } }
            """,
            token: token);

        Assert.Equal("permission denied", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task DataStoragesValidateFirstAndLast()
    {
        var errors = await QueryErrorsAsync(
            """
            query DataStorages { dataStorages(first: 1, last: 1) { edges { node { id } } } }
            """);

        Assert.Equal(
            "Passing both `first` and `last` to paginate a connection is not supported.",
            errors[0].GetProperty("message").GetString());

        // The resolver's validatePaginationArgs runs before ent's generated
        // Paginate calls validateFirstLast (ent.resolvers.go:400-412), so a
        // negative first surfaces the resolver wording.
        var negative = await QueryErrorsAsync(
            """
            query DataStorages { dataStorages(first: -1) { edges { node { id } } } }
            """);

        Assert.Equal("first must be greater than 0", negative[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task SchemaExposesEveryStorageWherePredicate()
    {
        var data = await QueryDataAsync(
            """
            { where: __type(name: "DataStorageWhereInput") { inputFields { name } } }
            """);

        string[] comparison = ["", "NEQ", "In", "NotIn", "GT", "GTE", "LT", "LTE"];
        string[] text =
        [
            .. comparison,
            "Contains",
            "HasPrefix",
            "HasSuffix",
            "EqualFold",
            "ContainsFold",
        ];

        var expected = new List<string> { "and", "not", "or" };
        expected.AddRange(comparison.Select(static suffix => "id" + suffix));
        expected.AddRange(comparison.Select(static suffix => "createdAt" + suffix));
        expected.AddRange(comparison.Select(static suffix => "updatedAt" + suffix));
        expected.AddRange(text.Select(static suffix => "name" + suffix));
        expected.AddRange(text.Select(static suffix => "description" + suffix));
        expected.AddRange(["primary", "primaryNEQ"]);
        expected.AddRange(["type", "typeNEQ", "typeIn", "typeNotIn"]);
        expected.AddRange(["status", "statusNEQ", "statusIn", "statusNotIn"]);
        expected.AddRange(
        [
            "hasRequests",
            "hasRequestsWith",
            "hasExecutions",
            "hasExecutionsWith",
        ]);

        var whereFields = data.GetProperty("where").GetProperty("inputFields")
            .EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expected.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            whereFields);
    }

    private static string Gid(long id) => "gid://axonhub/DataStorage/" + id;

    private async Task<JsonElement> QueryDataAsync(
        string query,
        object? variables = null,
        string? token = null)
    {
        var root = await ExecuteAsync(query, variables, token);
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
