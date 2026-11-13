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
/// Locks the prompt-protection-rule GraphQL surface over /admin/graphql against
/// the operations the unchanged frontend sends
/// (frontend/src/features/prompt-protection-rules/data/rules.ts): gid IDs, the
/// PromptProtectionSettings JSON shape, the ported where predicates, entgql
/// pagination, the reference mutation semantics from
/// internal/server/biz/prompt_protection_rule.go and
/// prompt_protection_preview.go, and the schema's read/write channels privacy
/// policy (internal/ent/schema/prompt_protection_rule.go:65-77).
/// </summary>
public sealed class AdminPromptProtectionRulesEndpointTests : IAsyncLifetime
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    private const string SettingsJson =
        """{"action":"mask","replacement":"[MASKED]","scopes":["user","assistant"]}""";

    private readonly AdminGraphqlGateServices _gate = new();
    private readonly FakeRoleStore _roles = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly FakePromptProtectionRuleStore _rules = new();

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
        builder.Services.AddSingleton<IPromptProtectionRuleStore>(_rules);
        builder.Services.AddScoped<PermissionValidator>();
        builder.Services.AddScoped<PromptProtectionRuleManagementService>();
        _users.RolesSource = _roles;

        // The gate's JWT user (id 9, owner) is the caller every fixture client
        // sends; the prompt-protection rule policy lets an owner through.
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
    public async Task PromptProtectionRulesQueryMatchesTheFrontendShape()
    {
        var rule = _rules.Seed(record =>
        {
            record.Name = "mask secrets";
            record.Description = "masks credentials";
            record.Pattern = "secret";
            record.Status = "enabled";
            record.Settings = SettingsJson;
            record.CreatedAt = CreatedAt;
            record.UpdatedAt = UpdatedAt;
        });

        var data = await QueryDataAsync(
            """
            query PromptProtectionRules($first: Int) {
              promptProtectionRules(first: $first) {
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
                    pattern
                    status
                    settings { action replacement scopes }
                  }
                }
              }
            }
            """,
            new { first = 10 });

        var connection = data.GetProperty("promptProtectionRules");
        Assert.Equal(1, connection.GetProperty("totalCount").GetInt32());
        var pageInfo = connection.GetProperty("pageInfo");
        Assert.False(pageInfo.GetProperty("hasNextPage").GetBoolean());
        Assert.False(pageInfo.GetProperty("hasPreviousPage").GetBoolean());

        var edge = Assert.Single(connection.GetProperty("edges").EnumerateArray());
        Assert.False(string.IsNullOrEmpty(edge.GetProperty("cursor").GetString()));
        var node = edge.GetProperty("node");

        Assert.Equal(
            "gid://axonhub/PromptProtectionRule/" + rule.Id,
            node.GetProperty("id").GetString());
        Assert.Equal("2026-01-02T03:04:05Z", node.GetProperty("createdAt").GetString());
        Assert.Equal("2026-02-03T04:05:06Z", node.GetProperty("updatedAt").GetString());
        Assert.Equal("mask secrets", node.GetProperty("name").GetString());
        Assert.Equal("masks credentials", node.GetProperty("description").GetString());
        Assert.Equal("secret", node.GetProperty("pattern").GetString());
        Assert.Equal("enabled", node.GetProperty("status").GetString());

        var settings = node.GetProperty("settings");
        Assert.Equal("mask", settings.GetProperty("action").GetString());
        Assert.Equal("[MASKED]", settings.GetProperty("replacement").GetString());
        Assert.Equal(
            ["user", "assistant"],
            settings.GetProperty("scopes").EnumerateArray()
                .Select(static scope => scope.GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task SettingsOutputUsesTheGoMarshallingOfAnEmptyReplacementAndNilScopes()
    {
        // A stored rule created before scopes existed: no replacement, no
        // scopes. gqlgen marshals the Go string as "" and the nil slice as [].
        var rule = _rules.Seed(record =>
        {
            record.Name = "reject";
            record.Pattern = "forbidden";
            record.Status = "disabled";
            record.Settings = """{"action":"reject"}""";
        });

        var data = await QueryDataAsync(
            """
            query PromptProtectionRules {
              promptProtectionRules(first: 1) {
                edges { node { id settings { action replacement scopes } } }
              }
            }
            """);

        var node = data.GetProperty("promptProtectionRules")
            .GetProperty("edges").EnumerateArray().Single()
            .GetProperty("node");
        Assert.Equal(
            "gid://axonhub/PromptProtectionRule/" + rule.Id,
            node.GetProperty("id").GetString());
        var settings = node.GetProperty("settings");
        Assert.Equal("reject", settings.GetProperty("action").GetString());
        Assert.Equal("", settings.GetProperty("replacement").GetString());
        Assert.Empty(settings.GetProperty("scopes").EnumerateArray());
    }

    [Fact]
    public async Task WhereAndNameOrderDriveKeysetPagination()
    {
        var alpha = _rules.Seed(record =>
        {
            record.Name = "Alpha";
            record.Status = "enabled";
            record.Settings = """{"action":"reject"}""";
        });
        var beta = _rules.Seed(record =>
        {
            record.Name = "Beta";
            record.Status = "enabled";
            record.Settings = """{"action":"reject"}""";
        });
        _rules.Seed(record =>
        {
            record.Name = "Gamma";
            record.Status = "disabled";
            record.Settings = """{"action":"reject"}""";
        });

        var variables = new
        {
            where = new { nameContainsFold = "a" },
            orderBy = new { field = "NAME", direction = "ASC" },
        };
        var data = await QueryDataAsync(
            """
            query Rules($where: PromptProtectionRuleWhereInput, $orderBy: PromptProtectionRuleOrder) {
              promptProtectionRules(first: 2, where: $where, orderBy: $orderBy) {
                totalCount
                pageInfo { hasNextPage hasPreviousPage endCursor }
                edges { cursor node { id name } }
              }
            }
            """,
            variables);

        var connection = data.GetProperty("promptProtectionRules");
        Assert.Equal(3, connection.GetProperty("totalCount").GetInt32());
        Assert.True(connection.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());

        var ids = connection.GetProperty("edges").EnumerateArray()
            .Select(static edge => edge.GetProperty("node").GetProperty("id").GetString()!)
            .ToArray();
        Assert.Equal(
            [
                "gid://axonhub/PromptProtectionRule/" + alpha.Id,
                "gid://axonhub/PromptProtectionRule/" + beta.Id,
            ],
            ids);

        var next = await QueryDataAsync(
            """
            query Rules($where: PromptProtectionRuleWhereInput, $orderBy: PromptProtectionRuleOrder, $after: Cursor) {
              promptProtectionRules(first: 2, where: $where, orderBy: $orderBy, after: $after) {
                totalCount
                pageInfo { hasNextPage hasPreviousPage }
                edges { node { name } }
              }
            }
            """,
            new
            {
                where = new { nameContainsFold = "a" },
                orderBy = new { field = "NAME", direction = "ASC" },
                after = connection.GetProperty("pageInfo").GetProperty("endCursor").GetString(),
            });

        var nextConnection = next.GetProperty("promptProtectionRules");
        Assert.Equal(3, nextConnection.GetProperty("totalCount").GetInt32());
        Assert.False(nextConnection.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());
        Assert.True(nextConnection.GetProperty("pageInfo").GetProperty("hasPreviousPage").GetBoolean());
        Assert.Equal(
            ["Gamma"],
            nextConnection.GetProperty("edges").EnumerateArray()
                .Select(static edge => edge.GetProperty("node").GetProperty("name").GetString()!)
                .ToArray());
    }

    [Fact]
    public async Task CreatedAtOrderFallsBackToTheDefaultIdOrder()
    {
        var first = _rules.Seed(record => record.Name = "first");
        var second = _rules.Seed(record => record.Name = "second");

        var data = await QueryDataAsync(
            """
            query Rules {
              promptProtectionRules(first: 10, orderBy: { field: CREATED_AT, direction: DESC }) {
                edges { cursor node { id } }
              }
            }
            """);

        // DefaultPromptProtectionRuleOrder is id; DESC reverses it. The
        // cursors carry no sort value because the default field has none.
        var ids = data.GetProperty("promptProtectionRules").GetProperty("edges").EnumerateArray()
            .Select(static edge => edge.GetProperty("node").GetProperty("id").GetString()!)
            .ToArray();
        Assert.Equal(
            [
                "gid://axonhub/PromptProtectionRule/" + second.Id,
                "gid://axonhub/PromptProtectionRule/" + first.Id,
            ],
            ids);
    }

    [Fact]
    public async Task InvalidPaginationIsRejectedLikeTheReference()
    {
        var errors = await QueryErrorsAsync(
            """
            query Rules { promptProtectionRules(first: 10, last: 10) { totalCount } }
            """);

        Assert.Equal(
            "Passing both `first` and `last` to paginate a connection is not supported.",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task CreateRuleSerializesSettingsAndAppliesDefaults()
    {
        var data = await QueryDataAsync(
            """
            mutation Create($input: CreatePromptProtectionRuleInput!) {
              createPromptProtectionRule(input: $input) {
                id
                name
                description
                pattern
                status
                settings { action replacement scopes }
              }
            }
            """,
            new
            {
                input = new
                {
                    name = "mask secrets",
                    pattern = "secret",
                    settings = new
                    {
                        action = "mask",
                        replacement = "[MASKED]",
                        scopes = new[] { "user", "assistant" },
                    },
                },
            });

        var created = data.GetProperty("createPromptProtectionRule");
        Assert.StartsWith(
            "gid://axonhub/PromptProtectionRule/",
            created.GetProperty("id").GetString());
        Assert.Equal("mask secrets", created.GetProperty("name").GetString());
        Assert.Equal("", created.GetProperty("description").GetString());
        Assert.Equal("secret", created.GetProperty("pattern").GetString());
        Assert.Equal("disabled", created.GetProperty("status").GetString());

        var settings = created.GetProperty("settings");
        Assert.Equal("mask", settings.GetProperty("action").GetString());
        Assert.Equal("[MASKED]", settings.GetProperty("replacement").GetString());
        Assert.Equal(
            ["user", "assistant"],
            settings.GetProperty("scopes").EnumerateArray()
                .Select(static scope => scope.GetString()!)
                .ToArray());

        // Go's json.Marshal with omitempty: action, then replacement, then
        // scopes, in struct field order.
        var stored = Assert.Single(_rules.Rows);
        Assert.Equal(
            """{"action":"mask","replacement":"[MASKED]","scopes":["user","assistant"]}""",
            stored.Settings);
    }

    [Fact]
    public async Task CreateOmitsEmptyOptionalFieldsFromTheStoredSettings()
    {
        await QueryDataAsync(
            """
            mutation Create($input: CreatePromptProtectionRuleInput!) {
              createPromptProtectionRule(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    name = "reject",
                    pattern = "forbidden",
                    settings = new { action = "reject" },
                },
            });

        var stored = Assert.Single(_rules.Rows);
        Assert.Equal("""{"action":"reject"}""", stored.Settings);
    }

    [Fact]
    public async Task CreateMaskWithoutReplacementIsRejected()
    {
        var errors = await QueryErrorsAsync(
            """
            mutation Create($input: CreatePromptProtectionRuleInput!) {
              createPromptProtectionRule(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    name = "mask",
                    pattern = "secret",
                    settings = new { action = "mask" },
                },
            });

        Assert.Equal(
            "replacement is required for mask action",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_rules.Rows);
    }

    [Fact]
    public async Task CreateWithAnInvalidRegexIsRejectedBeforeTheDuplicateCheck()
    {
        var errors = await QueryErrorsAsync(
            """
            mutation Create($input: CreatePromptProtectionRuleInput!) {
              createPromptProtectionRule(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    name = "broken",
                    pattern = "(",
                    settings = new { action = "reject" },
                },
            });

        Assert.StartsWith(
            "invalid regex pattern: ",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_rules.Rows);
    }

    [Fact]
    public async Task CreateDuplicateNameReturnsTheCodedErrorExtensions()
    {
        _rules.Seed(record =>
        {
            record.Name = "dup";
            record.Pattern = "secret";
            record.Settings = """{"action":"reject"}""";
        });

        var errors = await QueryErrorsAsync(
            """
            mutation Create($input: CreatePromptProtectionRuleInput!) {
              createPromptProtectionRule(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    name = "dup",
                    pattern = "secret",
                    settings = new { action = "reject" },
                },
            });

        var error = errors[0];
        Assert.Equal(
            "prompt protection rule name 'dup' already exists",
            error.GetProperty("message").GetString());
        var extensions = error.GetProperty("extensions");
        Assert.Equal("DUPLICATE_NAME", extensions.GetProperty("code").GetString());
        Assert.Equal(
            "prompt protection rule",
            extensions.GetProperty("resource").GetString());
        Assert.Equal("name", extensions.GetProperty("field").GetString());
        Assert.Equal("dup", extensions.GetProperty("value").GetString());

        Assert.Single(_rules.Rows);
    }

    [Fact]
    public async Task UpdateRuleValidatesTheEffectiveSettingsAndKeepsOmittedFields()
    {
        var rule = _rules.Seed(record =>
        {
            record.Name = "before";
            record.Description = "kept";
            record.Pattern = "secret";
            record.Status = "disabled";
            record.Settings = SettingsJson;
            record.UpdatedAt = UpdatedAt;
        });

        // Only the name changes: the effective pattern/settings still validate.
        var renamed = await QueryDataAsync(
            """
            mutation Update($id: ID!, $input: UpdatePromptProtectionRuleInput!) {
              updatePromptProtectionRule(id: $id, input: $input) {
                id name description pattern status
                settings { action replacement scopes }
              }
            }
            """,
            new
            {
                id = Gid(rule.Id),
                input = new { name = "after" },
            });

        var node = renamed.GetProperty("updatePromptProtectionRule");
        Assert.Equal("after", node.GetProperty("name").GetString());
        Assert.Equal("kept", node.GetProperty("description").GetString());
        Assert.Equal("secret", node.GetProperty("pattern").GetString());
        Assert.Equal("disabled", node.GetProperty("status").GetString());
        Assert.Equal("[MASKED]", node.GetProperty("settings").GetProperty("replacement").GetString());
        Assert.True(rule.UpdatedAt > UpdatedAt);

        // A settings-only update replaces the stored settings and keeps the
        // pattern.
        var switched = await QueryDataAsync(
            """
            mutation Update($id: ID!, $input: UpdatePromptProtectionRuleInput!) {
              updatePromptProtectionRule(id: $id, input: $input) {
                pattern
                settings { action replacement scopes }
              }
            }
            """,
            new
            {
                id = Gid(rule.Id),
                input = new
                {
                    settings = new { action = "reject" },
                },
            });

        var switchedNode = switched.GetProperty("updatePromptProtectionRule");
        Assert.Equal("secret", switchedNode.GetProperty("pattern").GetString());
        var settings = switchedNode.GetProperty("settings");
        Assert.Equal("reject", settings.GetProperty("action").GetString());
        Assert.Equal("", settings.GetProperty("replacement").GetString());
        Assert.Empty(settings.GetProperty("scopes").EnumerateArray());
        Assert.Equal("""{"action":"reject"}""", rule.Settings);
    }

    [Fact]
    public async Task UpdateWithAnInvalidEffectivePatternIsRejectedWithoutWriting()
    {
        var rule = _rules.Seed(record =>
        {
            record.Name = "kept";
            record.Pattern = "secret";
            record.Settings = """{"action":"reject"}""";
        });

        var errors = await QueryErrorsAsync(
            """
            mutation Update($id: ID!, $input: UpdatePromptProtectionRuleInput!) {
              updatePromptProtectionRule(id: $id, input: $input) { id }
            }
            """,
            new
            {
                id = Gid(rule.Id),
                input = new { pattern = "[" },
            });

        Assert.StartsWith(
            "invalid regex pattern: ",
            errors[0].GetProperty("message").GetString());
        Assert.Equal("kept", rule.Name);
        Assert.Equal("secret", rule.Pattern);
    }

    [Fact]
    public async Task MissingRuleReportsTheReferenceErrors()
    {
        var missing = Gid(9_999);

        var update = await QueryErrorsAsync(
            """
            mutation Update($id: ID!, $input: UpdatePromptProtectionRuleInput!) {
              updatePromptProtectionRule(id: $id, input: $input) { id }
            }
            """,
            new { id = missing, input = new { name = "x" } });
        Assert.Equal(
            "failed to query prompt protection rule: ent: prompt_protection_rule not found",
            update[0].GetProperty("message").GetString());

        var status = await QueryErrorsAsync(
            """
            mutation Status($id: ID!, $status: PromptProtectionRuleStatus!) {
              updatePromptProtectionRuleStatus(id: $id, status: $status)
            }
            """,
            new { id = missing, status = "enabled" });
        Assert.Equal(
            "failed to update prompt protection rule status: ent: prompt_protection_rule not found",
            status[0].GetProperty("message").GetString());

        var delete = await QueryErrorsAsync(
            """
            mutation Delete($id: ID!) { deletePromptProtectionRule(id: $id) }
            """,
            new { id = missing });
        Assert.Equal(
            "failed to delete prompt protection rule: ent: prompt_protection_rule not found",
            delete[0].GetProperty("message").GetString());

        Assert.Empty(_rules.Rows);
    }

    [Fact]
    public async Task StatusBulkAndDeleteMutationsChangeEveryRow()
    {
        var rule = _rules.Seed(record =>
        {
            record.Name = "rule";
            record.Pattern = "secret";
            record.Settings = """{"action":"reject"}""";
        });
        var other = _rules.Seed(record =>
        {
            record.Name = "other";
            record.Pattern = "secret";
            record.Settings = """{"action":"reject"}""";
        });

        var status = await QueryDataAsync(
            """
            mutation Status($id: ID!, $status: PromptProtectionRuleStatus!) {
              updatePromptProtectionRuleStatus(id: $id, status: $status)
            }
            """,
            new { id = Gid(rule.Id), status = "enabled" });
        Assert.True(status.GetProperty("updatePromptProtectionRuleStatus").GetBoolean());
        Assert.Equal("enabled", rule.Status);

        var disabled = await QueryDataAsync(
            """
            mutation Disable($ids: [ID!]!) {
              bulkDisablePromptProtectionRules(ids: $ids)
            }
            """,
            new { ids = new[] { Gid(rule.Id), Gid(other.Id) } });
        Assert.True(disabled.GetProperty("bulkDisablePromptProtectionRules").GetBoolean());
        Assert.Equal("disabled", rule.Status);
        Assert.Equal("disabled", other.Status);

        var enabled = await QueryDataAsync(
            """
            mutation Enable($ids: [ID!]!) {
              bulkEnablePromptProtectionRules(ids: $ids)
            }
            """,
            new { ids = new[] { Gid(other.Id) } });
        Assert.True(enabled.GetProperty("bulkEnablePromptProtectionRules").GetBoolean());
        Assert.Equal("enabled", other.Status);

        var deleted = await QueryDataAsync(
            """
            mutation Delete($ids: [ID!]!) {
              bulkDeletePromptProtectionRules(ids: $ids)
            }
            """,
            new { ids = new[] { Gid(rule.Id) } });
        Assert.True(deleted.GetProperty("bulkDeletePromptProtectionRules").GetBoolean());
        Assert.NotEqual(0, rule.DeletedAt);
        Assert.Equal(0, other.DeletedAt);

        var single = await QueryDataAsync(
            """
            mutation Delete($id: ID!) { deletePromptProtectionRule(id: $id) }
            """,
            new { id = Gid(other.Id) });
        Assert.True(single.GetProperty("deletePromptProtectionRule").GetBoolean());
        Assert.NotEqual(0, other.DeletedAt);

        var again = await QueryErrorsAsync(
            """
            mutation Delete($id: ID!) { deletePromptProtectionRule(id: $id) }
            """,
            new { id = Gid(other.Id) });
        Assert.Equal(
            "failed to delete prompt protection rule: ent: prompt_protection_rule not found",
            again[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task UpdateStatusWritesSoftDeletedRowsLikeTheReference()
    {
        var rule = _rules.Seed(record =>
        {
            record.Name = "deleted";
            record.Pattern = "secret";
            record.Settings = """{"action":"reject"}""";
        });
        rule.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // UpdateOneID has no deleted_at predicate, so the row is written even
        // though the live queries no longer see it.
        var status = await QueryDataAsync(
            """
            mutation Status($id: ID!, $status: PromptProtectionRuleStatus!) {
              updatePromptProtectionRuleStatus(id: $id, status: $status)
            }
            """,
            new { id = Gid(rule.Id), status = "archived" });

        Assert.True(status.GetProperty("updatePromptProtectionRuleStatus").GetBoolean());
        Assert.Equal("archived", rule.Status);
    }

    [Fact]
    public async Task PreviewRendersMaskAndRejectResults()
    {
        var mask = await QueryDataAsync(
            """
            mutation Preview($input: PromptProtectionRulePreviewInput!) {
              previewPromptProtectionRule(input: $input) { result hasMatch }
            }
            """,
            new
            {
                input = new
                {
                    pattern = "secret",
                    testText = "my secret and secret",
                    settings = new { action = "mask", replacement = "[x]" },
                },
            });

        var maskResult = mask.GetProperty("previewPromptProtectionRule");
        Assert.Equal("my [x] and [x]", maskResult.GetProperty("result").GetString());
        Assert.True(maskResult.GetProperty("hasMatch").GetBoolean());

        var reject = await QueryDataAsync(
            """
            mutation Preview($input: PromptProtectionRulePreviewInput!) {
              previewPromptProtectionRule(input: $input) { result hasMatch }
            }
            """,
            new
            {
                input = new
                {
                    pattern = "secret",
                    testText = "my secret",
                    settings = new { action = "reject" },
                },
            });

        var rejectResult = reject.GetProperty("previewPromptProtectionRule");
        Assert.Equal("reject", rejectResult.GetProperty("result").GetString());
        Assert.True(rejectResult.GetProperty("hasMatch").GetBoolean());

        var noMatch = await QueryDataAsync(
            """
            mutation Preview($input: PromptProtectionRulePreviewInput!) {
              previewPromptProtectionRule(input: $input) { result hasMatch }
            }
            """,
            new
            {
                input = new
                {
                    pattern = "secret",
                    testText = "nothing here",
                    settings = new { action = "mask", replacement = "[x]" },
                },
            });

        var noMatchResult = noMatch.GetProperty("previewPromptProtectionRule");
        Assert.Equal("nothing here", noMatchResult.GetProperty("result").GetString());
        Assert.False(noMatchResult.GetProperty("hasMatch").GetBoolean());
    }

    [Fact]
    public async Task PreviewValidatesThePatternAndSettings()
    {
        var invalidPattern = await QueryErrorsAsync(
            """
            mutation Preview($input: PromptProtectionRulePreviewInput!) {
              previewPromptProtectionRule(input: $input) { result }
            }
            """,
            new
            {
                input = new
                {
                    pattern = "(",
                    testText = "text",
                    settings = new { action = "reject" },
                },
            });

        Assert.StartsWith(
            "invalid regex pattern: ",
            invalidPattern[0].GetProperty("message").GetString());

        var missingReplacement = await QueryErrorsAsync(
            """
            mutation Preview($input: PromptProtectionRulePreviewInput!) {
              previewPromptProtectionRule(input: $input) { result }
            }
            """,
            new
            {
                input = new
                {
                    pattern = "secret",
                    testText = "text",
                    settings = new { action = "mask" },
                },
            });

        Assert.Equal(
            "replacement is required for mask action",
            missingReplacement[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task NodeQueryReportsPromptProtectionRuleAsAnUnknownType()
    {
        // PromptProtectionRule implements Node in the SDL, but the reference's
        // guidTypeToNodeType map does not register it, so node() fails before
        // loading the row (existing and missing ids alike).
        var rule = _rules.Seed(record =>
        {
            record.Name = "mask secrets";
            record.Pattern = "secret";
            record.Settings = """{"action":"reject"}""";
        });

        foreach (var id in new[] { Gid(rule.Id), Gid(9_999) })
        {
            var errors = await QueryErrorsAsync(
                """
                query Node($id: ID!) {
                  node(id: $id) { id ... on PromptProtectionRule { name } }
                }
                """,
                new { id });

            Assert.Equal(
                "unknown node type: PromptProtectionRule",
                errors[0].GetProperty("message").GetString());
        }
    }

    [Fact]
    public async Task RulesQueryRunsThroughTheReadChannelsPolicy()
    {
        _rules.Seed(record =>
        {
            record.Name = "rule";
            record.Pattern = "secret";
            record.Settings = """{"action":"reject"}""";
        });

        var readerToken = await SeedMemberTokenAsync(
            "reader@example.com",
            scopes: """["read_channels"]""",
            id: 71);
        var data = await QueryDataAsync(
            "query Rules { promptProtectionRules(first: 10) { totalCount } }",
            token: readerToken);
        Assert.Equal(
            1,
            data.GetProperty("promptProtectionRules").GetProperty("totalCount").GetInt32());

        // write_channels without read_channels: the query policy still denies.
        var writerToken = await SeedMemberTokenAsync(
            "writer@example.com",
            scopes: """["write_channels"]""",
            id: 72);
        var writerErrors = await QueryErrorsAsync(
            "query Rules { promptProtectionRules(first: 10) { totalCount } }",
            token: writerToken);
        Assert.Equal("permission denied", writerErrors[0].GetProperty("message").GetString());

        // No scopes at all: every rule skips and the policy denies by default.
        var plainToken = await SeedMemberTokenAsync(
            "plain@example.com",
            scopes: null,
            id: 73);
        var plainErrors = await QueryErrorsAsync(
            "query Rules { promptProtectionRules(first: 10) { totalCount } }",
            token: plainToken);
        Assert.Equal("permission denied", plainErrors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ServiceAccountKeysNeedReadChannelsForTheRulesQuery()
    {
        _rules.Seed(record =>
        {
            record.Name = "rule";
            record.Pattern = "secret";
            record.Settings = """{"action":"reject"}""";
        });

        // The store's default key scopes include read_channels.
        _gate.AddServiceAccountKey();
        var data = await QueryDataAsync(
            "query Rules { promptProtectionRules(first: 10) { totalCount } }",
            token: "sk-service-account");
        Assert.Equal(
            1,
            data.GetProperty("promptProtectionRules").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task ServiceAccountKeysWithoutReadChannelsAreDenied()
    {
        _rules.Seed(record =>
        {
            record.Name = "rule";
            record.Pattern = "secret";
            record.Settings = """{"action":"reject"}""";
        });

        _gate.AddServiceAccountKey(
            configure: key => key.Scopes = """["read_models"]""");
        var errors = await QueryErrorsAsync(
            "query Rules { promptProtectionRules(first: 10) { totalCount } }",
            token: "sk-service-account");
        Assert.Equal("permission denied", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task WriteOnlyMembersAreDeniedByTheInternalRuleReads()
    {
        var rule = _rules.Seed(record =>
        {
            record.Name = "existing";
            record.Pattern = "secret";
            record.Settings = """{"action":"reject"}""";
        });
        var token = await SeedMemberTokenAsync(
            "writer@example.com",
            scopes: """["write_channels"]""",
            id: 74);

        // CreateRule's name lookup runs through the query policy.
        var create = await QueryErrorsAsync(
            """
            mutation Create($input: CreatePromptProtectionRuleInput!) {
              createPromptProtectionRule(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    name = "fresh",
                    pattern = "secret",
                    settings = new { action = "reject" },
                },
            },
            token);
        Assert.Equal("permission denied", create[0].GetProperty("message").GetString());
        Assert.Single(_rules.Rows);

        // UpdateRule's re-read does too.
        var update = await QueryErrorsAsync(
            """
            mutation Update($id: ID!, $input: UpdatePromptProtectionRuleInput!) {
              updatePromptProtectionRule(id: $id, input: $input) { id }
            }
            """,
            new { id = Gid(rule.Id), input = new { name = "renamed" } },
            token);
        Assert.Equal("permission denied", update[0].GetProperty("message").GetString());
        Assert.Equal("existing", rule.Name);

        // Status, bulk status and delete have no internal read, so the write
        // policy allows a write-only member.
        var status = await QueryDataAsync(
            """
            mutation Status($id: ID!, $status: PromptProtectionRuleStatus!) {
              updatePromptProtectionRuleStatus(id: $id, status: $status)
            }
            """,
            new { id = Gid(rule.Id), status = "enabled" },
            token);
        Assert.True(status.GetProperty("updatePromptProtectionRuleStatus").GetBoolean());
        Assert.Equal("enabled", rule.Status);

        var deleted = await QueryDataAsync(
            """
            mutation Delete($ids: [ID!]!) {
              bulkDeletePromptProtectionRules(ids: $ids)
            }
            """,
            new { ids = new[] { Gid(rule.Id) } },
            token);
        Assert.True(deleted.GetProperty("bulkDeletePromptProtectionRules").GetBoolean());
        Assert.NotEqual(0, rule.DeletedAt);

        // An empty bulk list short-circuits before the empty ent mutation runs,
        // so even a scope-less member gets true instead of a deny.
        var plainToken = await SeedMemberTokenAsync(
            "plain@example.com",
            scopes: null,
            id: 75);
        var empty = await QueryDataAsync(
            "mutation Delete { bulkDeletePromptProtectionRules(ids: []) }",
            token: plainToken);
        Assert.True(empty.GetProperty("bulkDeletePromptProtectionRules").GetBoolean());
    }

    [Fact]
    public async Task ReadOnlyMembersCannotWriteRules()
    {
        var rule = _rules.Seed(record =>
        {
            record.Name = "existing";
            record.Pattern = "secret";
            record.Settings = """{"action":"reject"}""";
        });
        var token = await SeedMemberTokenAsync(
            "reader@example.com",
            scopes: """["read_channels"]""",
            id: 76);

        // The duplicate check runs before the write policy, so a read-only
        // member sees the coded duplicate error rather than permission denied.
        var duplicate = await QueryErrorsAsync(
            """
            mutation Create($input: CreatePromptProtectionRuleInput!) {
              createPromptProtectionRule(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    name = "existing",
                    pattern = "secret",
                    settings = new { action = "reject" },
                },
            },
            token);
        Assert.Equal(
            "prompt protection rule name 'existing' already exists",
            duplicate[0].GetProperty("message").GetString());
        Assert.Equal(
            "DUPLICATE_NAME",
            duplicate[0].GetProperty("extensions").GetProperty("code").GetString());

        var create = await QueryErrorsAsync(
            """
            mutation Create($input: CreatePromptProtectionRuleInput!) {
              createPromptProtectionRule(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    name = "fresh",
                    pattern = "secret",
                    settings = new { action = "reject" },
                },
            },
            token);
        Assert.Equal("permission denied", create[0].GetProperty("message").GetString());
        Assert.Single(_rules.Rows);

        var update = await QueryErrorsAsync(
            """
            mutation Update($id: ID!, $input: UpdatePromptProtectionRuleInput!) {
              updatePromptProtectionRule(id: $id, input: $input) { id }
            }
            """,
            new { id = Gid(rule.Id), input = new { name = "renamed" } },
            token);
        Assert.Equal("permission denied", update[0].GetProperty("message").GetString());
        Assert.Equal("existing", rule.Name);

        var delete = await QueryErrorsAsync(
            "mutation Delete($id: ID!) { deletePromptProtectionRule(id: $id) }",
            new { id = Gid(rule.Id) },
            token);
        Assert.Equal("permission denied", delete[0].GetProperty("message").GetString());
        Assert.Equal(0, rule.DeletedAt);

        // Preview evaluates no rules, so it carries no schema policy.
        var preview = await QueryDataAsync(
            """
            mutation Preview($input: PromptProtectionRulePreviewInput!) {
              previewPromptProtectionRule(input: $input) { result hasMatch }
            }
            """,
            new
            {
                input = new
                {
                    pattern = "secret",
                    testText = "my secret",
                    settings = new { action = "reject" },
                },
            },
            token);
        Assert.True(
            preview.GetProperty("previewPromptProtectionRule").GetProperty("hasMatch").GetBoolean());
    }

    [Fact]
    public async Task ValidationPrecedesTheRulePolicyOnCreateButNotOnUpdate()
    {
        var plainToken = await SeedMemberTokenAsync(
            "plain@example.com",
            scopes: null,
            id: 77);

        // CreateRule validates the pattern before its first read.
        var invalid = await QueryErrorsAsync(
            """
            mutation Create($input: CreatePromptProtectionRuleInput!) {
              createPromptProtectionRule(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    name = "broken",
                    pattern = "(",
                    settings = new { action = "reject" },
                },
            },
            plainToken);
        Assert.StartsWith(
            "invalid regex pattern: ",
            invalid[0].GetProperty("message").GetString());

        // UpdateRule reads the row first, so the query policy denies before the
        // effective settings are validated.
        var rule = _rules.Seed(record =>
        {
            record.Name = "kept";
            record.Pattern = "secret";
            record.Settings = """{"action":"reject"}""";
        });
        var update = await QueryErrorsAsync(
            """
            mutation Update($id: ID!, $input: UpdatePromptProtectionRuleInput!) {
              updatePromptProtectionRule(id: $id, input: $input) { id }
            }
            """,
            new { id = Gid(rule.Id), input = new { pattern = "[" } },
            plainToken);
        Assert.Equal("permission denied", update[0].GetProperty("message").GetString());
        Assert.Equal("secret", rule.Pattern);
    }

    [Fact]
    public async Task MembersWithBothScopesCanManageRules()
    {
        var token = await SeedMemberTokenAsync(
            "manager@example.com",
            scopes: """["read_channels","write_channels"]""",
            id: 78);

        var created = await QueryDataAsync(
            """
            mutation Create($input: CreatePromptProtectionRuleInput!) {
              createPromptProtectionRule(input: $input) { id name status }
            }
            """,
            new
            {
                input = new
                {
                    name = "managed",
                    pattern = "secret",
                    settings = new { action = "reject" },
                },
            },
            token);
        var node = created.GetProperty("createPromptProtectionRule");
        var id = node.GetProperty("id").GetString();
        Assert.Equal("managed", node.GetProperty("name").GetString());
        Assert.Equal("disabled", node.GetProperty("status").GetString());

        var updated = await QueryDataAsync(
            """
            mutation Update($id: ID!, $input: UpdatePromptProtectionRuleInput!) {
              updatePromptProtectionRule(id: $id, input: $input) { id name }
            }
            """,
            new { id, input = new { name = "renamed" } },
            token);
        Assert.Equal(
            "renamed",
            updated.GetProperty("updatePromptProtectionRule").GetProperty("name").GetString());

        var deleted = await QueryDataAsync(
            "mutation Delete($id: ID!) { deletePromptProtectionRule(id: $id) }",
            new { id },
            token);
        Assert.True(deleted.GetProperty("deletePromptProtectionRule").GetBoolean());
    }

    [Fact]
    public async Task SchemaExposesEveryRuleWherePredicate()
    {
        var data = await QueryDataAsync(
            """
            { where: __type(name: "PromptProtectionRuleWhereInput") { inputFields { name } } }
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
        expected.AddRange(text.Select(static suffix => "pattern" + suffix));
        expected.AddRange(["status", "statusNEQ", "statusIn", "statusNotIn"]);

        var whereFields = data.GetProperty("where").GetProperty("inputFields")
            .EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            expected.OrderBy(static name => name, StringComparer.Ordinal).ToArray(),
            whereFields);
    }

    private static string Gid(long id) => "gid://axonhub/PromptProtectionRule/" + id;

    /// <summary>
    /// Seeds a non-owner management user both in the JWT store and in the
    /// scopes store the rule policy reads.
    /// </summary>
    private async Task<string> SeedMemberTokenAsync(string email, string? scopes, long id)
    {
        var member = _gate.Users.Add(user =>
        {
            user.Id = id;
            user.Email = email;
            user.Status = "activated";
            user.IsOwner = false;
        });

        await _users.CreateAsync(
            new UserRecord
            {
                Id = id,
                Email = email,
                Status = "activated",
                IsOwner = false,
                Scopes = scopes,
            },
            roleIds: null);

        return _gate.IssueJwt(member);
    }

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
        request.Headers.Add("X-Project-ID", "gid://axonhub/Project/1");

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}
