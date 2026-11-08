using System.Net;
using System.Text;
using System.Text.Json;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Api.Orchestration;
using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the channel read surface over /admin/graphql against the queries the
/// migrated management frontend sends (frontend/src/gql/channels.ts): the
/// entgql connection contract, the queryChannels model branch, the aggregate
/// resolvers and the live field resolvers (credentials, settings, endpoints).
/// </summary>
public sealed class AdminChannelsEndpointTests : IAsyncLifetime
{
    private static readonly string[] EnabledStatuses = ["enabled"];
    private static readonly string[] AllChannelTypeNames = ["openai", "codex", "cloudflare"];

    private readonly FakeChannelAdminStore _channels = new();
    private readonly FakeChannelPriceReadStore _prices = new();
    private readonly FakeUserAdminStore _users = new();
    private readonly FakeRoleStore _roles = new();
    private readonly ChannelLimiterManager _limiters = new();
    private readonly FakeProviderQuotaStatusStore _quotaStatuses = new();
    private readonly ProviderQuotaSettingsCache _quotaSettingsCache = new();

    private readonly AdminGraphqlGateServices _gate = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IChannelAdminStore>(_channels);
        builder.Services.AddSingleton<IChannelModelPriceStore>(_prices);
        builder.Services.AddSingleton<IUserAdminStore>(_users);
        builder.Services.AddSingleton<IRoleStore>(_roles);
        builder.Services.AddScoped<ChannelPrivacy>();
        builder.Services.AddSingleton(_limiters);
        builder.Services.AddScoped<ChannelQueryService>();
        builder.Services.AddSingleton<IProviderQuotaStatusStore>(_quotaStatuses);
        builder.Services.AddSingleton(_quotaSettingsCache);
        builder.Services.AddScoped<
            IProviderQuotaCollectionSettingsService,
            ProviderQuotaCollectionSettingsService>();
        _users.RolesSource = _roles;

        // The gate's JWT user (id 9, owner) is the caller every fixture client
        // sends; the channel privacy rules see it as an owner.
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
    public async Task ChannelsQueryMatchesTheFrontendListShape()
    {
        var first = SeedChannel(
            type: "openai",
            name: "Alpha",
            status: "enabled",
            orderingWeight: 5,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            tags: """["tag-a","tag-b"]""");
        var second = SeedChannel(
            type: "anthropic",
            name: "Beta",
            status: "disabled",
            orderingWeight: 2,
            createdAt: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            updatedAt: new DateTime(2026, 1, 2, 3, 4, 5, 123, DateTimeKind.Utc));

        var data = await QueryDataAsync(
            """
            query Channels($first: Int, $orderBy: ChannelOrder) {
              channels(first: $first, orderBy: $orderBy) {
                edges {
                  cursor
                  node {
                    id createdAt updatedAt type baseURL name status
                    supportedModels manualModels autoSyncSupportedModels
                    autoSyncModelPattern tags defaultTestModel orderingWeight
                    errorMessage autoDisabledAt autoDisableExpiresAt remark
                  }
                }
                pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                totalCount
              }
            }
            """,
            new { first = 10, orderBy = new { field = "CREATED_AT", direction = "DESC" } });

        var connection = data.GetProperty("channels");
        var edges = connection.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(2, edges.Length);

        // CREATED_AT is remapped to ent's default order (id) by the reference
        // resolver, keeping the requested direction.
        var beta = edges[0].GetProperty("node");
        Assert.Equal("gid://axonhub/Channel/" + second.Id, beta.GetProperty("id").GetString());
        Assert.Equal("anthropic", beta.GetProperty("type").GetString());
        Assert.Equal("Beta", beta.GetProperty("name").GetString());
        Assert.Equal("disabled", beta.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, beta.GetProperty("baseURL").ValueKind);
        Assert.Empty(beta.GetProperty("supportedModels").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, beta.GetProperty("manualModels").ValueKind);
        Assert.False(beta.GetProperty("autoSyncSupportedModels").GetBoolean());
        Assert.Equal("", beta.GetProperty("autoSyncModelPattern").GetString());
        Assert.Equal(JsonValueKind.Null, beta.GetProperty("tags").ValueKind);
        Assert.Equal("", beta.GetProperty("defaultTestModel").GetString());
        Assert.Equal(2, beta.GetProperty("orderingWeight").GetInt32());
        Assert.Equal(JsonValueKind.Null, beta.GetProperty("errorMessage").ValueKind);
        Assert.Equal(JsonValueKind.Null, beta.GetProperty("autoDisabledAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, beta.GetProperty("autoDisableExpiresAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, beta.GetProperty("remark").ValueKind);

        // time.Format(time.RFC3339Nano) trims trailing fractional zeroes.
        Assert.Equal(
            "2026-01-02T03:04:05Z",
            beta.GetProperty("createdAt").GetString());
        Assert.Equal(
            "2026-01-02T03:04:05.123Z",
            beta.GetProperty("updatedAt").GetString());

        var alpha = edges[1].GetProperty("node");
        Assert.Equal(first.Id, CursorCodec.Decode(edges[1].GetProperty("cursor").GetString()!).Id);
        Assert.Equal(
            ["tag-a", "tag-b"],
            alpha.GetProperty("tags").EnumerateArray()
                .Select(static tag => tag.GetString()!).ToArray());

        var pageInfo = connection.GetProperty("pageInfo");
        Assert.False(pageInfo.GetProperty("hasNextPage").GetBoolean());
        Assert.False(pageInfo.GetProperty("hasPreviousPage").GetBoolean());
        Assert.Equal(second.Id, CursorCodec.Decode(pageInfo.GetProperty("startCursor").GetString()!).Id);
        Assert.Equal(first.Id, CursorCodec.Decode(pageInfo.GetProperty("endCursor").GetString()!).Id);
        Assert.Equal(2, connection.GetProperty("totalCount").GetInt32());

        // CREATED_AT became the default id order; the cursor carries only the id.
        var captured = Assert.Single(_channels.Queries);
        Assert.Equal(ChannelSortField.Id, captured.SortField);
        Assert.Equal(SortDirection.Desc, captured.Direction);
        Assert.Null(CursorCodec.Decode(edges[0].GetProperty("cursor").GetString()!).Value);
    }

    [Fact]
    public async Task ChannelsCursorPaginatesByValueOrderFields()
    {
        var alpha = SeedChannel(type: "openai", name: "alpha", orderingWeight: 3);
        SeedChannel(type: "openai", name: "bravo", orderingWeight: 1);
        SeedChannel(type: "openai", name: "charlie", orderingWeight: 2);

        var page = await QueryDataAsync(
            """
            query Channels($first: Int, $after: Cursor, $orderBy: ChannelOrder) {
              channels(first: $first, after: $after, orderBy: $orderBy) {
                edges { cursor node { id name orderingWeight } }
                pageInfo { hasNextPage hasPreviousPage }
                totalCount
              }
            }
            """,
            new
            {
                first = 2,
                after = (string?)null,
                orderBy = new { field = "NAME", direction = "ASC" },
            });

        var edges = page.GetProperty("channels").GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(2, edges.Length);
        Assert.Equal("alpha", edges[0].GetProperty("node").GetProperty("name").GetString());
        Assert.Equal("bravo", edges[1].GetProperty("node").GetProperty("name").GetString());
        Assert.True(page.GetProperty("channels").GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());

        // The NAME cursor payload carries the string value next to the id.
        var nameCursor = CursorCodec.Decode(edges[0].GetProperty("cursor").GetString()!);
        Assert.Equal(alpha.Id, nameCursor.Id);
        Assert.Equal("alpha", nameCursor.Value);

        var next = await QueryDataAsync(
            """
            query Channels($first: Int, $after: Cursor, $orderBy: ChannelOrder) {
              channels(first: $first, after: $after, orderBy: $orderBy) {
                edges { node { id } }
                pageInfo { hasNextPage hasPreviousPage }
              }
            }
            """,
            new
            {
                first = 2,
                after = edges[1].GetProperty("cursor").GetString(),
                orderBy = new { field = "NAME", direction = "ASC" },
            });

        var nextEdges = next.GetProperty("channels").GetProperty("edges").EnumerateArray().ToArray();
        Assert.Single(nextEdges);
        Assert.True(next.GetProperty("channels").GetProperty("pageInfo").GetProperty("hasPreviousPage").GetBoolean());

        // ORDERING_WEIGHT desc issues an integer cursor value.
        var byWeight = await QueryDataAsync(
            """
            query Channels($first: Int, $orderBy: ChannelOrder) {
              channels(first: $first, orderBy: $orderBy) {
                edges { cursor node { name orderingWeight } }
              }
            }
            """,
            new { first = 2, orderBy = new { field = "ORDERING_WEIGHT", direction = "DESC" } });

        var weightEdges = byWeight.GetProperty("channels").GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(3, weightEdges[0].GetProperty("node").GetProperty("orderingWeight").GetInt32());
        Assert.Equal(2, weightEdges[1].GetProperty("node").GetProperty("orderingWeight").GetInt32());
        var weightCursor = CursorCodec.Decode(weightEdges[0].GetProperty("cursor").GetString()!);
        Assert.Equal(3L, weightCursor.Value);
        Assert.Equal(alpha.Id, weightCursor.Id);

        // last returns the tail in ascending order with hasPreviousPage set.
        var tail = await QueryDataAsync(
            """
            query Channels($last: Int, $orderBy: ChannelOrder) {
              channels(last: $last, orderBy: $orderBy) {
                edges { node { name } }
                pageInfo { hasNextPage hasPreviousPage }
              }
            }
            """,
            new { last = 2, orderBy = new { field = "NAME", direction = "ASC" } });

        var tailEdges = tail.GetProperty("channels").GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(
            ["bravo", "charlie"],
            tailEdges.Select(static edge => edge.GetProperty("node").GetProperty("name").GetString()!).ToArray());
        Assert.True(tail.GetProperty("channels").GetProperty("pageInfo").GetProperty("hasPreviousPage").GetBoolean());
    }

    [Fact]
    public async Task ChannelsValidateFirstAndLast()
    {
        var errors = await QueryErrorsAsync(
            """
            query Channels { channels(first: 1, last: 1) { edges { node { id } } } }
            """);

        Assert.Equal(
            "Passing both `first` and `last` to paginate a connection is not supported.",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task QueryChannelsFiltersByWhereAndHasTag()
    {
        var enabled = SeedChannel(type: "openai", name: "Enabled One", status: "enabled", tags: """["prod"]""");
        SeedChannel(type: "anthropic", name: "Disabled One", status: "disabled", tags: """["prod"]""");
        SeedChannel(type: "openai", name: "Other Prod", status: "enabled", tags: """["prod"]""");
        SeedChannel(type: "openai", name: "No Tag", status: "enabled");

        var data = await QueryDataAsync(
            """
            query QueryChannels($input: QueryChannelInput!) {
              queryChannels(input: $input) {
                edges { node { id name status } }
                totalCount
              }
            }
            """,
            new
            {
                input = new
                {
                    first = 10,
                    hasTag = "prod",
                    where = new
                    {
                        typeIn = AllChannelTypeNames[..1],
                        status = "enabled",
                        nameContainsFold = "one",
                    },
                },
            });

        var nodes = data.GetProperty("queryChannels").GetProperty("edges").EnumerateArray()
            .Select(static edge => edge.GetProperty("node")).ToArray();
        Assert.Single(nodes);
        Assert.Equal("gid://axonhub/Channel/" + enabled.Id, nodes[0].GetProperty("id").GetString());
        Assert.Equal("Enabled One", nodes[0].GetProperty("name").GetString());

        var captured = _channels.Queries[^1];
        Assert.Equal("prod", captured.HasTag);
        Assert.Equal("openai", Assert.Single(captured.Where!.TypeIn!));
        Assert.Equal("enabled", captured.Where.Status);
        Assert.Equal("one", captured.Where.NameContainsFold);
        Assert.Null(captured.After);
        Assert.Null(captured.Before);
        Assert.Equal(10, captured.First);
        Assert.Equal(ChannelSortField.Id, captured.SortField);
        Assert.False(captured.Ordered);
    }

    [Fact]
    public async Task QueryChannelsModelBranchIgnoresPaginationAndValidation()
    {
        var supporting = SeedChannel(
            type: "openai",
            name: "Supports GPT",
            supportedModels: """["gpt-4o","o3"]""");
        SeedChannel(
            type: "openai",
            name: "Other Model",
            supportedModels: """["claude-3"]""");

        // The model branch calls ListAll and bypasses Paginate, so first/last are
        // ignored and even first+last together do not raise the validation error.
        var data = await QueryDataAsync(
            """
            query QueryChannels($input: QueryChannelInput!) {
              queryChannels(input: $input) {
                edges { node { id name allModelEntries { requestModel actualModel source } } }
                pageInfo { hasNextPage hasPreviousPage startCursor endCursor }
                totalCount
              }
            }
            """,
            new
            {
                input = new
                {
                    first = 1,
                    last = 1,
                    model = "gpt-4o",
                },
            });

        var connection = data.GetProperty("queryChannels");
        var nodes = connection.GetProperty("edges").EnumerateArray()
            .Select(static edge => edge.GetProperty("node")).ToArray();
        Assert.Single(nodes);
        Assert.Equal(
            "gid://axonhub/Channel/" + supporting.Id,
            nodes[0].GetProperty("id").GetString());
        Assert.Equal(1, connection.GetProperty("totalCount").GetInt32());
        Assert.False(connection.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());
        Assert.False(connection.GetProperty("pageInfo").GetProperty("hasPreviousPage").GetBoolean());

        // The in-memory connection still derives start/end cursors from the edges.
        Assert.Equal(
            supporting.Id,
            CursorCodec.Decode(connection.GetProperty("pageInfo").GetProperty("startCursor").GetString()!).Id);
        Assert.Equal(
            supporting.Id,
            CursorCodec.Decode(connection.GetProperty("pageInfo").GetProperty("endCursor").GetString()!).Id);

        var entries = nodes[0].GetProperty("allModelEntries").EnumerateArray().ToArray();
        Assert.Equal(["gpt-4o", "o3"], entries.Select(static e => e.GetProperty("requestModel").GetString()!).ToArray());
        Assert.All(entries, static entry => Assert.Equal("direct", entry.GetProperty("source").GetString()));

        var captured = _channels.AllQueries[^1];
        Assert.Null(captured.First);
        Assert.Null(captured.Last);
    }

    [Fact]
    public async Task AllChannelTagsFlattensRowsAndKeepsFirstOccurrence()
    {
        SeedChannel(type: "openai", name: "One", tags: """["b","a"]""");
        SeedChannel(type: "openai", name: "Two", tags: """["a","c"]""");
        SeedChannel(type: "openai", name: "Three", tags: "null");

        var data = await QueryDataAsync("""{ allChannelTags }""");

        Assert.Equal(
            ["b", "a", "c"],
            data.GetProperty("allChannelTags").EnumerateArray()
                .Select(static tag => tag.GetString()!).ToArray());
    }

    [Fact]
    public async Task CountChannelsByTypeMapsTheStatusEnumToTheColumn()
    {
        SeedChannel(type: "openai", name: "One", status: "enabled");
        SeedChannel(type: "openai", name: "Two", status: "disabled");
        SeedChannel(type: "anthropic", name: "Three", status: "enabled");
        SeedChannel(type: "anthropic", name: "Four", status: "archived");

        var data = await QueryDataAsync(
            """
            query Count($input: CountChannelsByTypeInput!) {
              countChannelsByType(input: $input) { type count }
            }
            """,
            new { input = new { statusIn = EnabledStatuses } });

        var rows = data.GetProperty("countChannelsByType").EnumerateArray().ToArray();
        Assert.Equal(
            [("anthropic", 1), ("openai", 1)],
            rows.Select(static row => (
                row.GetProperty("type").GetString()!,
                row.GetProperty("count").GetInt32())).ToArray());
        Assert.Equal(["enabled"], _channels.CountStatusIn![^1]);

        // No statusIn means "everything that is not archived".
        var all = await QueryDataAsync(
            """
            query Count($input: CountChannelsByTypeInput!) {
              countChannelsByType(input: $input) { type count }
            }
            """,
            new { input = new { } });

        Assert.Equal(
            [("anthropic", 1), ("openai", 2)],
            all.GetProperty("countChannelsByType").EnumerateArray()
                .Select(static row => (
                    row.GetProperty("type").GetString()!,
                    row.GetProperty("count").GetInt32())).ToArray());
    }

    [Fact]
    public async Task AllChannelSummarysFiltersArchivedAndOrdersByWeight()
    {
        var heavy = SeedChannel(type: "openai", name: "Heavy", status: "enabled", orderingWeight: 9);
        SeedChannel(type: "openai", name: "Archived", status: "archived", orderingWeight: 100);
        var light = SeedChannel(type: "openai", name: "Light", status: "disabled", orderingWeight: 1);

        var data = await QueryDataAsync(
            """
            { allChannelSummarys { id name status orderingWeight } }
            """);

        var nodes = data.GetProperty("allChannelSummarys").EnumerateArray().ToArray();
        Assert.Equal(
            ["Heavy", "Light"],
            nodes.Select(static node => node.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(
            "gid://axonhub/Channel/" + heavy.Id,
            nodes[0].GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/Channel/" + light.Id,
            nodes[1].GetProperty("id").GetString());
        Assert.Equal(9, nodes[0].GetProperty("orderingWeight").GetInt32());
        Assert.Equal("disabled", nodes[1].GetProperty("status").GetString());

        var withArchived = await QueryDataAsync(
            """
            { allChannelSummarys(includeArchived: true) { name status } }
            """);

        Assert.Contains(
            "archived",
            withArchived.GetProperty("allChannelSummarys").EnumerateArray()
                .Select(static node => node.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task ChannelPoliciesDefaultToUnlimitedAndExposeCustomRules()
    {
        SeedChannel(type: "openai", name: "Default");
        SeedChannel(
            type: "openai",
            name: "Custom",
            policies: """
                {
                  "stream": "forbid",
                  "apiKeyAutoDisableMode": "custom",
                  "apiKeyAutoDisableRules": [
                    {
                      "statusCodes": [429],
                      "keywordPatterns": ["rate limit"],
                      "times": 3,
                      "action": "temporary_disable",
                      "disableDurationMinutes": 10,
                      "disableUntilCron": "",
                      "disableUntilTimezone": ""
                    }
                  ]
                }
                """);

        var data = await QueryDataAsync(
            """
            {
              channels(first: 10, orderBy: {field: NAME, direction: ASC}) {
                edges {
                  node {
                    name
                    policies {
                      stream
                      apiKeyAutoDisableMode
                      apiKeyAutoDisableRules {
                        statusCodes keywordPatterns times action
                        disableDurationMinutes disableUntilCron disableUntilTimezone
                      }
                    }
                  }
                }
              }
            }
            """);

        var nodes = data.GetProperty("channels").GetProperty("edges").EnumerateArray()
            .Select(static edge => edge.GetProperty("node")).ToArray();

        // NAME ASC: "Custom" sorts before "Default".
        var custom = nodes[0].GetProperty("policies");
        Assert.Equal("forbid", custom.GetProperty("stream").GetString());
        Assert.Equal("custom", custom.GetProperty("apiKeyAutoDisableMode").GetString());
        var rule = Assert.Single(custom.GetProperty("apiKeyAutoDisableRules").EnumerateArray());
        Assert.Equal([429], rule.GetProperty("statusCodes").EnumerateArray()
            .Select(static code => code.GetInt32()).ToArray());
        Assert.Equal("rate limit", Assert.Single(rule.GetProperty("keywordPatterns").EnumerateArray()).GetString());
        Assert.Equal(3, rule.GetProperty("times").GetInt32());
        Assert.Equal("temporary_disable", rule.GetProperty("action").GetString());
        Assert.Equal(10, rule.GetProperty("disableDurationMinutes").GetInt32());

        var defaults = nodes[1].GetProperty("policies");
        Assert.Equal("unlimited", defaults.GetProperty("stream").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            defaults.GetProperty("apiKeyAutoDisableMode").ValueKind);
        Assert.Equal(JsonValueKind.Null, defaults.GetProperty("apiKeyAutoDisableRules").ValueKind);
    }

    [Fact]
    public async Task ChannelSettingsExposeTheLiveFrontendFields()
    {
        SeedChannel(
            type: "openai",
            name: "Configured",
            settings: """
                {
                  "extraModelPrefix": "px",
                  "modelMappings": [{"from": "alias", "to": "gpt-4o"}],
                  "autoTrimedModelPrefixes": ["vendor"],
                  "proxy": {
                    "type": "url",
                    "url": "http://proxy",
                    "username": "u",
                    "password": "p",
                    "disableConnectionReuse": true
                  },
                  "passThroughUserAgent": true,
                  "passThroughBody": false,
                  "rateLimit": {"rpm": 60, "maxConcurrent": 2, "queueSize": 5, "queueTimeoutMs": 1000},
                  "maxSingleChannelRetries": 3,
                  "responseTimeout": {
                    "mode": "custom",
                    "streamFirstEventTimeoutSeconds": 11,
                    "nonStreamResponseTimeoutSeconds": 22
                  },
                  "modelProtocols": [
                    {"model": "gpt-4o", "apiFormats": ["openai/chat_completions"], "enabled": true}
                  ],
                  "apiKeyStrategy": "round_robin",
                  "apiKeyRoundRobinSwitchAfter": 4,
                  "providerQuota": {
                    "commandCode": {"authCookie": "cc-cookie"},
                    "ollama": {"authCookie": "ollama-cookie"}
                  },
                  "quotaRoutingMode": "retired_mode"
                }
                """);

        var data = await QueryDataAsync(
            """
            {
              channels(first: 1) {
                edges {
                  node {
                    settings {
                      extraModelPrefix
                      modelMappings { from to }
                      autoTrimedModelPrefixes
                      hideOriginalModels hideMappedModels lowercaseModelId
                      proxy { type url username password disableConnectionReuse }
                      transformOptions {
                        forceArrayInstructions
                        forceArrayInputs
                        replaceDeveloperRoleWithSystem
                        reasoningEffortMapping { from to }
                      }
                      headerOverrideOperations { op path }
                      bodyOverrideOperations { op }
                      passThroughUserAgent passThroughBody
                      rateLimit { rpm tpm maxConcurrent queueSize queueTimeoutMs }
                      retryableStatusCodes
                      retryableErrorPatterns { pattern regex }
                      maxSingleChannelRetries
                      responseTimeout {
                        mode
                        streamFirstEventTimeoutSeconds
                        nonStreamResponseTimeoutSeconds
                      }
                      modelProtocols { model apiFormats enabled }
                      apiKeyStrategy
                      apiKeyRoundRobinSwitchAfter
                      providerQuota {
                        commandCode { authCookie }
                        ollama { authCookie }
                      }
                      quotaRoutingMode
                    }
                  }
                }
              }
            }
            """);

        var settings = data.GetProperty("channels").GetProperty("edges")[0]
            .GetProperty("node").GetProperty("settings");

        Assert.Equal("px", settings.GetProperty("extraModelPrefix").GetString());
        var mapping = Assert.Single(settings.GetProperty("modelMappings").EnumerateArray());
        Assert.Equal("alias", mapping.GetProperty("from").GetString());
        Assert.Equal("gpt-4o", mapping.GetProperty("to").GetString());
        Assert.Equal("vendor", Assert.Single(settings.GetProperty("autoTrimedModelPrefixes").EnumerateArray()).GetString());
        Assert.False(settings.GetProperty("hideOriginalModels").GetBoolean());
        Assert.False(settings.GetProperty("hideMappedModels").GetBoolean());
        Assert.False(settings.GetProperty("lowercaseModelId").GetBoolean());

        // ProxyType marshals the raw lowercase stored value (C139).
        var proxy = settings.GetProperty("proxy");
        Assert.Equal("url", proxy.GetProperty("type").GetString());
        Assert.Equal("http://proxy", proxy.GetProperty("url").GetString());
        Assert.Equal("u", proxy.GetProperty("username").GetString());
        Assert.Equal("p", proxy.GetProperty("password").GetString());
        Assert.True(proxy.GetProperty("disableConnectionReuse").GetBoolean());

        // The object exists whenever settings are present; absent arrays are null.
        var transform = settings.GetProperty("transformOptions");
        Assert.False(transform.GetProperty("forceArrayInstructions").GetBoolean());
        Assert.False(transform.GetProperty("forceArrayInputs").GetBoolean());
        Assert.False(transform.GetProperty("replaceDeveloperRoleWithSystem").GetBoolean());
        Assert.Equal(
            JsonValueKind.Null,
            transform.GetProperty("reasoningEffortMapping").ValueKind);

        Assert.Empty(settings.GetProperty("headerOverrideOperations").EnumerateArray());
        Assert.Empty(settings.GetProperty("bodyOverrideOperations").EnumerateArray());

        Assert.True(settings.GetProperty("passThroughUserAgent").GetBoolean());
        Assert.False(settings.GetProperty("passThroughBody").GetBoolean());

        var rateLimit = settings.GetProperty("rateLimit");
        Assert.Equal(60, rateLimit.GetProperty("rpm").GetInt32());
        Assert.Equal(JsonValueKind.Null, rateLimit.GetProperty("tpm").ValueKind);
        Assert.Equal(2, rateLimit.GetProperty("maxConcurrent").GetInt32());
        Assert.Equal(5, rateLimit.GetProperty("queueSize").GetInt32());
        Assert.Equal(1000, rateLimit.GetProperty("queueTimeoutMs").GetInt32());

        Assert.Equal(JsonValueKind.Null, settings.GetProperty("retryableStatusCodes").ValueKind);
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("retryableErrorPatterns").ValueKind);

        Assert.Equal(3, settings.GetProperty("maxSingleChannelRetries").GetInt32());

        var timeout = settings.GetProperty("responseTimeout");
        Assert.Equal("CUSTOM", timeout.GetProperty("mode").GetString());
        Assert.Equal(11, timeout.GetProperty("streamFirstEventTimeoutSeconds").GetInt32());
        Assert.Equal(22, timeout.GetProperty("nonStreamResponseTimeoutSeconds").GetInt32());

        var protocol = Assert.Single(settings.GetProperty("modelProtocols").EnumerateArray());
        Assert.Equal("gpt-4o", protocol.GetProperty("model").GetString());
        Assert.Equal("openai/chat_completions", Assert.Single(protocol.GetProperty("apiFormats").EnumerateArray()).GetString());
        Assert.True(protocol.GetProperty("enabled").GetBoolean());

        Assert.Equal("round_robin", settings.GetProperty("apiKeyStrategy").GetString());
        Assert.Equal(4, settings.GetProperty("apiKeyRoundRobinSwitchAfter").GetInt32());

        Assert.Equal(
            "cc-cookie",
            settings.GetProperty("providerQuota").GetProperty("commandCode").GetProperty("authCookie").GetString());
        Assert.Equal(
            "ollama-cookie",
            settings.GetProperty("providerQuota").GetProperty("ollama").GetProperty("authCookie").GetString());

        // Unknown stored values collapse to REMOVE_ON_EXHAUSTED like the reference.
        Assert.Equal("REMOVE_ON_EXHAUSTED", settings.GetProperty("quotaRoutingMode").GetString());
    }

    [Fact]
    public async Task ChannelCredentialsResolveTheLiveVertexAndOAuthRules()
    {
        SeedChannel(
            type: "gemini_vertex",
            name: "Vertex",
            credentials: """
                {
                  "apiKey": "legacy",
                  "apiKeys": ["k1", "k2"],
                  "vertexApiKeys": [
                    {"projectID": "p1", "apiKey": "v1", "region": "us-central1"}
                  ],
                  "gcp": {"region": "r", "projectID": "gp", "jsonData": "{}"}
                }
                """);
        SeedChannel(
            type: "openai",
            name: "Legacy OAuth",
            credentials: """{"apiKey": "{\"access_token\":\"tok\"}"}""");

        var data = await QueryDataAsync(
            """
            {
              channels(first: 10, orderBy: {field: NAME, direction: ASC}) {
                edges {
                  node {
                    name
                    credentials {
                      apiKey apiKeys
                      vertexApiKeys { projectID apiKey region }
                      gcp { region projectID jsonData }
                      oauth { accessToken tokenType }
                    }
                  }
                }
              }
            }
            """);

        var nodes = data.GetProperty("channels").GetProperty("edges").EnumerateArray()
            .Select(static edge => edge.GetProperty("node")).ToArray();

        // Legacy OAuth: the JSON blob stored in apiKey is recognized as OAuth,
        // so the whole credential is returned as the single apiKey value.
        var legacy = nodes[0].GetProperty("credentials");
        Assert.Equal("{\"access_token\":\"tok\"}", legacy.GetProperty("apiKey").GetString());
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("apiKeys").ValueKind);
        Assert.Equal(JsonValueKind.Null, legacy.GetProperty("oauth").ValueKind);

        var vertex = nodes[1].GetProperty("credentials");
        Assert.Equal("", vertex.GetProperty("apiKey").GetString());
        // Vertex keys are surfaced project-bound and removed from the flat list.
        Assert.Equal(
            ["legacy", "k1", "k2"],
            vertex.GetProperty("apiKeys").EnumerateArray()
                .Select(static key => key.GetString()!).ToArray());
        var vertexKey = Assert.Single(vertex.GetProperty("vertexApiKeys").EnumerateArray());
        Assert.Equal("p1", vertexKey.GetProperty("projectID").GetString());
        Assert.Equal("v1", vertexKey.GetProperty("apiKey").GetString());
        Assert.Equal("us-central1", vertexKey.GetProperty("region").GetString());
        Assert.Equal("gp", vertex.GetProperty("gcp").GetProperty("projectID").GetString());
        Assert.Equal("{}", vertex.GetProperty("gcp").GetProperty("jsonData").GetString());
    }

    [Fact]
    public async Task DisabledApiKeysAndLimiterStatsMatchTheLiveResolvers()
    {
        SeedChannel(
            type: "openai",
            name: "Limited",
            settings: """{"rateLimit":{"maxConcurrent":2,"queueSize":5}}""",
            disabledApiKeys: """
                [
                  {
                    "key": "dead-key",
                    "disabledAt": "2026-01-02T03:04:05Z",
                    "errorCode": 401,
                    "reason": "unauthorized"
                  }
                ]
                """);
        SeedChannel(type: "openai", name: "Unlimited");

        var data = await QueryDataAsync(
            """
            {
              channels(first: 10, orderBy: {field: NAME, direction: ASC}) {
                edges {
                  node {
                    name
                    disabledAPIKeys { key disabledAt errorCode reason expiresAt }
                    liveLimiterStats { inFlight waiting capacity queueSize }
                  }
                }
              }
            }
            """);

        var nodes = data.GetProperty("channels").GetProperty("edges").EnumerateArray()
            .Select(static edge => edge.GetProperty("node")).ToArray();

        var limited = nodes[0];
        var disabled = Assert.Single(limited.GetProperty("disabledAPIKeys").EnumerateArray());
        Assert.Equal("dead-key", disabled.GetProperty("key").GetString());
        Assert.Equal("2026-01-02T03:04:05Z", disabled.GetProperty("disabledAt").GetString());
        Assert.Equal(401, disabled.GetProperty("errorCode").GetInt32());
        Assert.Equal("unauthorized", disabled.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Null, disabled.GetProperty("expiresAt").ValueKind);

        var stats = limited.GetProperty("liveLimiterStats");
        Assert.Equal(0, stats.GetProperty("inFlight").GetInt32());
        Assert.Equal(0, stats.GetProperty("waiting").GetInt32());
        Assert.Equal(2, stats.GetProperty("capacity").GetInt32());
        Assert.Equal(5, stats.GetProperty("queueSize").GetInt32());

        var unlimited = nodes[1];
        Assert.Empty(unlimited.GetProperty("disabledAPIKeys").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, unlimited.GetProperty("liveLimiterStats").ValueKind);
    }

    [Fact]
    public async Task DefaultEndpointsExposeTheLiveTables()
    {
        SeedChannel(type: "codex", name: "Codex");
        SeedChannel(type: "cloudflare", name: "Cloudflare");

        var data = await QueryDataAsync(
            """
            {
              channels(first: 10, orderBy: {field: NAME, direction: ASC}) {
                edges {
                  node {
                    name
                    defaultEndpoints { apiFormat path baseURL transport }
                  }
                }
              }
            }
            """);

        var nodes = data.GetProperty("channels").GetProperty("edges").EnumerateArray()
            .Select(static edge => edge.GetProperty("node")).ToArray();

        // NAME ASC: "Cloudflare" sorts before "Codex".
        Assert.Equal(
            ["openai/chat_completions", "openai/embeddings"],
            nodes[0].GetProperty("defaultEndpoints").EnumerateArray()
                .Select(static endpoint => endpoint.GetProperty("apiFormat").GetString()!).ToArray());
        Assert.Equal(
            ["openai/responses", "openai/responses-ws", "openai/alpha_search",
             "openai/image_generation", "openai/image_edit"],
            nodes[1].GetProperty("defaultEndpoints").EnumerateArray()
                .Select(static endpoint => endpoint.GetProperty("apiFormat").GetString()!).ToArray());
        Assert.Equal(
            "",
            nodes[1].GetProperty("defaultEndpoints")[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task AllModelEntriesAreSortedByRequestModel()
    {
        SeedChannel(
            type: "openai",
            name: "Mapped",
            supportedModels: """["gpt-4o","claude-3"]""",
            settings: """{"modelMappings":[{"from":"alias","to":"gpt-4o"}]}""");

        var data = await QueryDataAsync(
            """
            {
              channels(first: 1) {
                edges {
                  node {
                    allModelEntries { requestModel actualModel source }
                  }
                }
              }
            }
            """);

        var entries = data.GetProperty("channels").GetProperty("edges")[0]
            .GetProperty("node").GetProperty("allModelEntries").EnumerateArray().ToArray();
        Assert.Equal(
            ["alias", "claude-3", "gpt-4o"],
            entries.Select(static entry => entry.GetProperty("requestModel").GetString()!).ToArray());
        Assert.Equal(
            ["mapping", "direct", "direct"],
            entries.Select(static entry => entry.GetProperty("source").GetString()!).ToArray());
        Assert.Equal("gpt-4o", entries[0].GetProperty("actualModel").GetString());
    }

    [Fact]
    public async Task ProviderQuotaStatusIsAbsentAndEnumsRejectUnknownStoredValues()
    {
        SeedChannel(type: "openai", name: "Plain", status: "enabled");

        var data = await QueryDataAsync(
            """
            {
              channels(first: 1) {
                edges { node { providerQuotaStatus { status ready } } }
              }
            }
            """);

        Assert.Equal(
            JsonValueKind.Null,
            data.GetProperty("channels").GetProperty("edges")[0]
                .GetProperty("node").GetProperty("providerQuotaStatus").ValueKind);

        SeedChannel(type: "wat", name: "Broken", status: "enabled");

        var errors = await QueryErrorsAsync(
            """
            { allChannelSummarys { id name } }
            """);

        Assert.Equal(
            "Enum \"ChannelType\" cannot represent value: \"wat\"",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ProviderQuotaStatusReturnsTheStoredRowShape()
    {
        var channel = SeedChannel(type: "codex", name: "Quota", status: "enabled");
        var row = _quotaStatuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = channel.Id,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            ProviderType = "codex",
            Status = ProviderQuotaStatuses.Warning,
            Ready = true,
            QuotaData = """
                {"email":"user@example.com","_limits":[{"type":"token","status":"warning","usageRatio":0.85,"ready":true,"window":"5h","nextResetAt":"2026-05-01T16:00:00Z"}],"_resets":{"supported":true,"resets":[{"id":"reset-1","status":"available"}]}}
                """,
            NextResetAt = new DateTime(2026, 5, 1, 16, 0, 0, DateTimeKind.Utc),
            NextCheckAt = new DateTime(2026, 5, 1, 12, 5, 0, DateTimeKind.Utc),
            AccountKey = "account-1",
        });

        var data = await QueryDataAsync(
            """
            {
              channels(first: 1) {
                edges {
                  node {
                    providerQuotaStatus {
                      id
                      channelID
                      providerType
                      status
                      ready
                      nextResetAt
                      nextCheckAt
                      accountKey
                      createdAt
                      updatedAt
                      quotaData
                      channel { id name }
                    }
                  }
                }
              }
            }
            """);

        var status = data.GetProperty("channels").GetProperty("edges")[0]
            .GetProperty("node").GetProperty("providerQuotaStatus");

        Assert.Equal(
            "gid://axonhub/ProviderQuotaStatus/" + row.Id,
            status.GetProperty("id").GetString());
        Assert.Equal(
            "gid://axonhub/Channel/" + channel.Id,
            status.GetProperty("channelID").GetString());
        Assert.Equal("codex", status.GetProperty("providerType").GetString());
        Assert.Equal("warning", status.GetProperty("status").GetString());
        Assert.True(status.GetProperty("ready").GetBoolean());
        Assert.Equal("2026-05-01T16:00:00Z", status.GetProperty("nextResetAt").GetString());
        Assert.Equal("2026-05-01T12:05:00Z", status.GetProperty("nextCheckAt").GetString());
        Assert.Equal("account-1", status.GetProperty("accountKey").GetString());
        Assert.Equal("2026-01-01T00:00:00Z", status.GetProperty("createdAt").GetString());
        Assert.Equal("2026-01-02T03:04:05Z", status.GetProperty("updatedAt").GetString());

        // gqlgen's Map scalar embeds the stored document as a JSON object.
        var quotaData = status.GetProperty("quotaData");
        Assert.Equal(JsonValueKind.Object, quotaData.ValueKind);
        Assert.Equal("user@example.com", quotaData.GetProperty("email").GetString());
        Assert.Equal(
            "reset-1",
            quotaData.GetProperty("_resets").GetProperty("resets")[0]
                .GetProperty("id").GetString());
        Assert.Equal(
            0.85,
            quotaData.GetProperty("_limits")[0].GetProperty("usageRatio").GetDouble());

        var embedded = status.GetProperty("channel");
        Assert.Equal("gid://axonhub/Channel/" + channel.Id, embedded.GetProperty("id").GetString());
        Assert.Equal("Quota", embedded.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ProviderQuotaStatusIsHiddenForRetiredOrDisabledProviders()
    {
        var disabled = SeedChannel(type: "codex", name: "Disabled", status: "enabled");
        var retired = SeedChannel(type: "codex", name: "Retired", status: "enabled");
        var empty = SeedChannel(type: "codex", name: "Empty", status: "enabled");
        _quotaStatuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = disabled.Id,
            ProviderType = "codex",
            Status = ProviderQuotaStatuses.Available,
        });
        _quotaStatuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = retired.Id,
            ProviderType = "wafer",
            Status = ProviderQuotaStatuses.Available,
        });
        _quotaStatuses.Add(new ProviderQuotaStatusRecord
        {
            ChannelId = empty.Id,
            ProviderType = string.Empty,
            Status = ProviderQuotaStatuses.Available,
        });
        _gate.Systems.Add(
            ProviderQuotaCollectionSettingsService.SystemKey,
            """{"enabled":true,"providers":{"codex":false}}""");

        var data = await QueryDataAsync(
            """
            {
              channels(first: 3) {
                edges { node { name providerQuotaStatus { status } } }
              }
            }
            """);

        var edges = data.GetProperty("channels").GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(
            ["Disabled", "Retired", "Empty"],
            edges.Select(static edge => edge.GetProperty("node").GetProperty("name").GetString()!)
                .ToArray());
        Assert.All(
            edges,
            static edge => Assert.Equal(
                JsonValueKind.Null,
                edge.GetProperty("node").GetProperty("providerQuotaStatus").ValueKind));
    }

    [Fact]
    public async Task MalformedJsonColumnsDegradeInsteadOfFailingTheQuery()
    {
        // Ent scans each JSON column into a Go struct and fails the whole query
        // on a malformed value; the port decodes each column independently and
        // degrades only that field (C141).
        var seeded = SeedChannel(
            type: "openai",
            name: "Broken",
            supportedModels: "{not-json",
            manualModels: "[\"ok\",",
            tags: "not-json",
            policies: "{oops",
            settings: "{oops",
            credentials: "{oops",
            disabledApiKeys: "{oops");

        var data = await QueryDataAsync(
            """
            {
              channels(first: 10) {
                edges {
                  node {
                    id name supportedModels manualModels tags
                    policies { stream }
                    settings { extraModelPrefix }
                    credentials { apiKey apiKeys }
                    disabledAPIKeys { key }
                    defaultEndpoints { apiFormat }
                  }
                }
              }
            }
            """);

        var node = data.GetProperty("channels").GetProperty("edges")[0]
            .GetProperty("node");
        Assert.Equal("gid://axonhub/Channel/" + seeded.Id, node.GetProperty("id").GetString());
        Assert.Equal("Broken", node.GetProperty("name").GetString());
        Assert.Empty(node.GetProperty("supportedModels").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, node.GetProperty("manualModels").ValueKind);
        Assert.Equal(JsonValueKind.Null, node.GetProperty("tags").ValueKind);
        Assert.Equal(JsonValueKind.Null, node.GetProperty("settings").ValueKind);

        // A malformed policies blob degrades to the zero policies object, whose
        // stream defaults to unlimited.
        Assert.Equal(
            "unlimited",
            node.GetProperty("policies").GetProperty("stream").GetString());

        var credentials = node.GetProperty("credentials");
        Assert.Equal("", credentials.GetProperty("apiKey").GetString());
        Assert.Equal(JsonValueKind.Null, credentials.GetProperty("apiKeys").ValueKind);
        Assert.Empty(node.GetProperty("disabledAPIKeys").EnumerateArray());

        // Resolvers derived from the channel type, not the JSON columns, still work.
        Assert.Equal(
            "openai/chat_completions",
            node.GetProperty("defaultEndpoints")[0].GetProperty("apiFormat").GetString());
    }

    [Fact]
    public async Task AggregateFailuresKeepTheReferenceErrorPrefixes()
    {
        _channels.SummaryFailure = new InvalidOperationException("boom");
        _channels.TagsFailure = new InvalidOperationException("boom");
        _channels.CountFailure = new InvalidOperationException("boom");

        var summaries = await QueryErrorsAsync("""{ allChannelSummarys { id } }""");
        Assert.Equal(
            "failed to query channels: boom",
            summaries[0].GetProperty("message").GetString());

        var tags = await QueryErrorsAsync("""{ allChannelTags }""");
        Assert.Equal(
            "failed to query channels: boom",
            tags[0].GetProperty("message").GetString());

        var counts = await QueryErrorsAsync(
            """
            { countChannelsByType(input: {}) { type count } }
            """);
        Assert.Equal(
            "failed to query channel type counts: boom",
            counts[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task IntrospectionExposesTheReferenceChannelEnumValues()
    {
        var data = await QueryDataAsync(
            """
            {
              channelType: __type(name: "ChannelType") { enumValues { name } }
              channelStatus: __type(name: "ChannelStatus") { enumValues { name } }
              orderField: __type(name: "ChannelOrderField") { enumValues { name } }
              proxyType: __type(name: "ProxyType") { enumValues { name } }
              quotaMode: __type(name: "ChannelQuotaRoutingMode") { enumValues { name } }
              timeoutMode: __type(name: "ChannelResponseTimeoutMode") { enumValues { name } }
            }
            """);

        var typeNames = data.GetProperty("channelType").GetProperty("enumValues").EnumerateArray()
            .Select(static value => value.GetProperty("name").GetString()!).ToArray();
        Assert.Contains("openai", typeNames);
        Assert.Contains("cloudflare", typeNames);
        Assert.Contains("gemini_vertex", typeNames);

        Assert.Equal(
            ["enabled", "disabled", "archived"],
            data.GetProperty("channelStatus").GetProperty("enumValues").EnumerateArray()
                .Select(static value => value.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(
            ["CREATED_AT", "UPDATED_AT", "TYPE", "NAME", "STATUS", "ORDERING_WEIGHT"],
            data.GetProperty("orderField").GetProperty("enumValues").EnumerateArray()
                .Select(static value => value.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(
            ["disabled", "environment", "url"],
            data.GetProperty("proxyType").GetProperty("enumValues").EnumerateArray()
                .Select(static value => value.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(
            ["INHERIT", "IGNORE_QUOTA", "REMOVE_ON_EXHAUSTED", "BACKPRESSURE"],
            data.GetProperty("quotaMode").GetProperty("enumValues").EnumerateArray()
                .Select(static value => value.GetProperty("name").GetString()!).ToArray());
        Assert.Equal(
            ["INHERIT", "CUSTOM"],
            data.GetProperty("timeoutMode").GetProperty("enumValues").EnumerateArray()
                .Select(static value => value.GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task NodeResolvesTheChannelWithTheEntNotFoundMessage()
    {
        var channel = SeedChannel(type: "openai", name: "Alpha");

        var data = await QueryDataAsync(
            """
            query ChannelNode($id: ID!) {
              node(id: $id) { id ... on Channel { id name type status } }
            }
            """,
            new { id = "gid://axonhub/Channel/" + channel.Id });

        var node = data.GetProperty("node");
        Assert.Equal("gid://axonhub/Channel/" + channel.Id, node.GetProperty("id").GetString());
        Assert.Equal("Alpha", node.GetProperty("name").GetString());
        Assert.Equal("openai", node.GetProperty("type").GetString());
        Assert.Equal("enabled", node.GetProperty("status").GetString());

        // gql_node.go:247-255: Channel.Query().Where(channel.ID(id)).Only(ctx)
        // surfaces ent's NotFoundError.
        var errors = await QueryErrorsAsync(
            """
            query ChannelNode($id: ID!) { node(id: $id) { id } }
            """,
            new { id = "gid://axonhub/Channel/404" });
        Assert.Equal(
            "ent: channel not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task NodeChannelModelPricesMatchesTheFrontendPriceDocument()
    {
        var channel = SeedChannel(type: "openai", name: "Alpha");

        // Go stores shopspring decimals as JSON strings; the rows are seeded
        // with ids 2 then 1 to prove the store's id order, plus a soft-deleted
        // row the query must skip (SoftDeleteMixin).
        _prices.SeedPrice(
            channel.Id,
            "model-b",
            """
            {"items":[{"itemCode":"completion_tokens","pricing":{"mode":"usage_tiered","usageTiered":{"tiers":[{"upTo":1000,"pricePerUnit":"0.5"},{"pricePerUnit":"0.25"}]}}}]}
            """,
            "ref-b",
            id: 2);
        _prices.SeedPrice(
            channel.Id,
            "model-a",
            """
            {
              "items": [
                {
                  "itemCode": "prompt_tokens",
                  "pricing": { "mode": "flat_fee", "flatFee": "1.5" },
                  "promptWriteCacheVariants": [
                    {
                      "variantCode": "one_hour",
                      "pricing": { "mode": "usage_per_unit", "usagePerUnit": "0.25" }
                    }
                  ]
                }
              ],
              "schedule": {
                "timezone": "Asia/Shanghai",
                "overrides": [
                  {
                    "name": "peak",
                    "priority": 10,
                    "when": {
                      "dailyTime": { "start": "08:00", "end": "18:00" },
                      "weekdays": [1, 2, 3, 4, 5],
                      "dateRange": { "start": "2026-01-01", "end": "2026-02-01" }
                    },
                    "items": [
                      {
                        "itemCode": "completion_tokens",
                        "pricing": {
                          "mode": "usage_tiered",
                          "usageTiered": {
                            "tiers": [
                              { "upTo": 1000, "pricePerUnit": "0.5" },
                              { "pricePerUnit": "0.25" }
                            ]
                          }
                        }
                      }
                    ]
                  }
                ]
              }
            }
            """,
            "ref-a",
            id: 1);
        _prices.SeedPrice(channel.Id, "model-gone", "null", "ref-gone", id: 3, deletedAt: 7);

        // The exact document frontend/src/features/channels/data/channels.ts
        // sends (GetChannelModelPrices).
        var data = await QueryDataAsync(
            """
            query GetChannelModelPrices($id: ID!) {
              node(id: $id) {
                ... on Channel {
                  id
                  channelModelPrices {
                    id
                    modelID
                    price {
                      items {
                        itemCode
                        pricing {
                          mode
                          flatFee
                          usagePerUnit
                          usageTiered {
                            tiers {
                              upTo
                              pricePerUnit
                            }
                          }
                        }
                        promptWriteCacheVariants {
                          variantCode
                          pricing {
                            mode
                            flatFee
                            usagePerUnit
                            usageTiered {
                              tiers {
                                upTo
                                pricePerUnit
                              }
                            }
                          }
                        }
                      }
                      schedule {
                        timezone
                        overrides {
                          name
                          priority
                          when {
                            dailyTime {
                              start
                              end
                            }
                            weekdays
                            dateRange {
                              start
                              end
                            }
                          }
                          items {
                            itemCode
                            pricing {
                              mode
                              flatFee
                              usagePerUnit
                              usageTiered {
                                tiers {
                                  upTo
                                  pricePerUnit
                                }
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
              }
            }
            """,
            new { id = "gid://axonhub/Channel/" + channel.Id });

        var node = data.GetProperty("node");
        Assert.Equal("gid://axonhub/Channel/" + channel.Id, node.GetProperty("id").GetString());

        // gql_edge.go:131-141 — QueryChannelModelPrices().All(ctx). The
        // reference leaves the order unspecified; the port's store orders by id.
        var prices = node.GetProperty("channelModelPrices").EnumerateArray().ToArray();
        Assert.Equal(2, prices.Length);

        var first = prices[0];
        Assert.Equal("gid://axonhub/ChannelModelPrice/1", first.GetProperty("id").GetString());
        Assert.Equal("model-a", first.GetProperty("modelID").GetString());

        var price = first.GetProperty("price");
        var item = Assert.Single(price.GetProperty("items").EnumerateArray());
        Assert.Equal("prompt_tokens", item.GetProperty("itemCode").GetString());
        var pricing = item.GetProperty("pricing");
        Assert.Equal("flat_fee", pricing.GetProperty("mode").GetString());
        Assert.Equal(1.5m, pricing.GetProperty("flatFee").GetDecimal());
        Assert.Equal(JsonValueKind.Null, pricing.GetProperty("usagePerUnit").ValueKind);
        Assert.Equal(JsonValueKind.Null, pricing.GetProperty("usageTiered").ValueKind);

        var variant = Assert.Single(
            item.GetProperty("promptWriteCacheVariants").EnumerateArray());
        Assert.Equal("one_hour", variant.GetProperty("variantCode").GetString());
        Assert.Equal("usage_per_unit", variant.GetProperty("pricing").GetProperty("mode").GetString());
        Assert.Equal(0.25m, variant.GetProperty("pricing").GetProperty("usagePerUnit").GetDecimal());

        var schedule = price.GetProperty("schedule");
        Assert.Equal("Asia/Shanghai", schedule.GetProperty("timezone").GetString());
        var priceOverride = Assert.Single(schedule.GetProperty("overrides").EnumerateArray());
        Assert.Equal("peak", priceOverride.GetProperty("name").GetString());
        Assert.Equal(10, priceOverride.GetProperty("priority").GetInt32());

        var when = priceOverride.GetProperty("when");
        Assert.Equal("08:00", when.GetProperty("dailyTime").GetProperty("start").GetString());
        Assert.Equal("18:00", when.GetProperty("dailyTime").GetProperty("end").GetString());
        Assert.Equal(
            [1, 2, 3, 4, 5],
            when.GetProperty("weekdays").EnumerateArray()
                .Select(static day => day.GetInt32()).ToArray());
        Assert.Equal("2026-01-01", when.GetProperty("dateRange").GetProperty("start").GetString());
        Assert.Equal("2026-02-01", when.GetProperty("dateRange").GetProperty("end").GetString());

        var overrideItem = Assert.Single(
            priceOverride.GetProperty("items").EnumerateArray());
        Assert.Equal("completion_tokens", overrideItem.GetProperty("itemCode").GetString());
        var overrideTiers = overrideItem.GetProperty("pricing")
            .GetProperty("usageTiered").GetProperty("tiers");
        Assert.Equal(2, overrideTiers.GetArrayLength());
        Assert.Equal(1000, overrideTiers[0].GetProperty("upTo").GetInt32());
        Assert.Equal(0.5m, overrideTiers[0].GetProperty("pricePerUnit").GetDecimal());
        Assert.Equal(JsonValueKind.Null, overrideTiers[1].GetProperty("upTo").ValueKind);
        Assert.Equal(0.25m, overrideTiers[1].GetProperty("pricePerUnit").GetDecimal());

        var second = prices[1];
        Assert.Equal("gid://axonhub/ChannelModelPrice/2", second.GetProperty("id").GetString());
        Assert.Equal("model-b", second.GetProperty("modelID").GetString());
        Assert.Equal(
            "usage_tiered",
            second.GetProperty("price").GetProperty("items")[0]
                .GetProperty("pricing").GetProperty("mode").GetString());
    }

    [Fact]
    public async Task ChannelModelPriceVersionsAndBackEdgesMatchTheReferenceSdl()
    {
        var channel = SeedChannel(type: "openai", name: "Alpha");
        var price = _prices.SeedPrice(
            channel.Id,
            "model-a",
            """{"items":[{"itemCode":"prompt_tokens","pricing":{"mode":"flat_fee","flatFee":"2"}}]}""",
            "ref-current",
            id: 1);
        _prices.SeedVersion(
            channel.Id,
            price.Id,
            "model-a",
            """{"items":[{"itemCode":"prompt_tokens","pricing":{"mode":"flat_fee","flatFee":"1.5"}}]}""",
            status: "archived",
            effectiveStartAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            effectiveEndAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            referenceId: "ref-old",
            id: 1);
        _prices.SeedVersion(
            channel.Id,
            price.Id,
            "model-a",
            """{"items":[{"itemCode":"prompt_tokens","pricing":{"mode":"flat_fee","flatFee":"2"}}]}""",
            status: "active",
            effectiveStartAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            effectiveEndAt: null,
            referenceId: "ref-current",
            id: 2);

        var data = await QueryDataAsync(
            """
            query PriceVersions($id: ID!) {
              node(id: $id) {
                ... on Channel {
                  channelModelPrices {
                    id
                    modelID
                    channel { id name }
                    versions {
                      id
                      createdAt
                      updatedAt
                      channelID
                      modelID
                      channelModelPriceID
                      price { items { itemCode pricing { mode flatFee usagePerUnit } } }
                      status
                      effectiveStartAt
                      effectiveEndAt
                      referenceID
                      channelModelPrice { id modelID }
                    }
                  }
                }
              }
            }
            """,
            new { id = "gid://axonhub/Channel/" + channel.Id });

        var priceNode = Assert.Single(
            data.GetProperty("node").GetProperty("channelModelPrices").EnumerateArray());

        // gql_edge.go:143-149 — ChannelModelPrice.Channel is QueryChannel().Only.
        var backChannel = priceNode.GetProperty("channel");
        Assert.Equal("gid://axonhub/Channel/" + channel.Id, backChannel.GetProperty("id").GetString());
        Assert.Equal("Alpha", backChannel.GetProperty("name").GetString());

        // gql_edge.go:159-169 — Versions is QueryVersions().All, ordered by id
        // in the port's store.
        var versions = priceNode.GetProperty("versions").EnumerateArray().ToArray();
        Assert.Equal(2, versions.Length);

        var archived = versions[0];
        Assert.Equal(
            "gid://axonhub/ChannelModelPriceVersion/1",
            archived.GetProperty("id").GetString());
        Assert.Equal("2026-01-01T00:00:00Z", archived.GetProperty("createdAt").GetString());
        Assert.Equal(1, archived.GetProperty("channelID").GetInt32());
        Assert.Equal("model-a", archived.GetProperty("modelID").GetString());
        Assert.Equal(
            "gid://axonhub/ChannelModelPrice/1",
            archived.GetProperty("channelModelPriceID").GetString());
        Assert.Equal(
            1.5m,
            archived.GetProperty("price").GetProperty("items")[0]
                .GetProperty("pricing").GetProperty("flatFee").GetDecimal());
        Assert.Equal("archived", archived.GetProperty("status").GetString());
        Assert.Equal(
            "2026-01-01T00:00:00Z",
            archived.GetProperty("effectiveStartAt").GetString());
        Assert.Equal(
            "2026-02-01T00:00:00Z",
            archived.GetProperty("effectiveEndAt").GetString());
        Assert.Equal("ref-old", archived.GetProperty("referenceID").GetString());

        // gql_edge.go:171-177 — ChannelModelPriceVersion.ChannelModelPrice.
        var priceBackEdge = archived.GetProperty("channelModelPrice");
        Assert.Equal("gid://axonhub/ChannelModelPrice/1", priceBackEdge.GetProperty("id").GetString());
        Assert.Equal("model-a", priceBackEdge.GetProperty("modelID").GetString());

        var active = versions[1];
        Assert.Equal(
            "gid://axonhub/ChannelModelPriceVersion/2",
            active.GetProperty("id").GetString());
        Assert.Equal("active", active.GetProperty("status").GetString());
        Assert.Equal(
            "2026-02-01T00:00:00Z",
            active.GetProperty("effectiveStartAt").GetString());
        Assert.Equal(JsonValueKind.Null, active.GetProperty("effectiveEndAt").ValueKind);
    }

    [Fact]
    public async Task ChannelModelPriceVersionFailsLikeTheGqlgenEnumMarshaler()
    {
        var channel = SeedChannel(type: "openai", name: "Alpha");
        var price = _prices.SeedPrice(channel.Id, "model-a", "null", "ref-a", id: 1);
        _prices.SeedVersion(
            channel.Id,
            price.Id,
            "model-a",
            "null",
            status: "bogus",
            id: 1);

        var errors = await QueryErrorsAsync(
            """
            query PriceVersions($id: ID!) {
              node(id: $id) {
                ... on Channel {
                  channelModelPrices { versions { status } }
                }
              }
            }
            """,
            new { id = "gid://axonhub/Channel/" + channel.Id });

        // gqlgen's enum marshaler text for an out-of-range stored value.
        Assert.Equal(
            "Enum \"ChannelModelPriceVersionStatus\" cannot represent value: \"bogus\"",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ChannelBackEdgesReportEntNotFoundWhenTheRowVanishes()
    {
        var channel = SeedChannel(type: "openai", name: "Alpha");
        var price = _prices.SeedPrice(channel.Id, "model-a", "null", "ref-a", id: 1);
        _prices.SeedVersion(
            channel.Id,
            price.Id,
            "model-a",
            "null",
            id: 1);

        // The node lookup succeeds and the edge lookup that follows sees the
        // row gone, modelling a concurrent soft-delete between the two ent
        // queries; Only then reports NotFound.
        _channels.VanishAfterFirstLookup.Add(channel.Id);
        var channelErrors = await QueryErrorsAsync(
            """
            query Prices($id: ID!) {
              node(id: $id) {
                ... on Channel { channelModelPrices { channel { id } } }
              }
            }
            """,
            new { id = "gid://axonhub/Channel/" + channel.Id });
        Assert.Equal(
            "ent: channel not found",
            channelErrors[0].GetProperty("message").GetString());

        _channels.VanishAfterFirstLookup.Clear();
        _prices.HideFromGetById.Add(price.Id);
        var priceErrors = await QueryErrorsAsync(
            """
            query Prices($id: ID!) {
              node(id: $id) {
                ... on Channel {
                  channelModelPrices { versions { channelModelPrice { id } } }
                }
              }
            }
            """,
            new { id = "gid://axonhub/Channel/" + channel.Id });
        Assert.Equal(
            "ent: channel_model_price not found",
            priceErrors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ChannelNodeRequiresTheReadChannelsScopeForServiceAccountKeys()
    {
        var channel = SeedChannel(type: "openai", name: "Alpha");

        // The gate's default key carries read_channels; strip it so the
        // APIKeyScopeQueryRule is the rule that denies.
        _gate.AddServiceAccountKey(
            configure: key => key.Scopes = """["read_models"]""");

        var errors = await QueryErrorsAsync(
            """
            query ChannelNode($id: ID!) { node(id: $id) { id } }
            """,
            new { id = "gid://axonhub/Channel/" + channel.Id },
            token: "sk-service-account");
        Assert.Equal("permission denied", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ChannelNodeAllowsAServiceAccountKeyWithTheReadChannelsScope()
    {
        var channel = SeedChannel(type: "openai", name: "Alpha");
        _prices.SeedPrice(channel.Id, "model-a", "null", "ref-a", id: 1);
        _gate.AddServiceAccountKey();

        // The nested price edge evaluates the ChannelModelPrice policy too;
        // rule 1 allows both queries with the same scope.
        var data = await QueryDataAsync(
            """
            query ChannelNode($id: ID!) {
              node(id: $id) {
                ... on Channel { id channelModelPrices { id } }
              }
            }
            """,
            new { id = "gid://axonhub/Channel/" + channel.Id },
            token: "sk-service-account");

        var node = data.GetProperty("node");
        Assert.Equal("gid://axonhub/Channel/" + channel.Id, node.GetProperty("id").GetString());
        Assert.Single(node.GetProperty("channelModelPrices").EnumerateArray());
    }

    [Fact]
    public async Task ChannelNodeDeniesUsersWithoutTheReadChannelsScope()
    {
        // OwnerRule skips a non-owner and UserReadScopeRule only skips when the
        // user has no scope; QueryPolicy then denies by default.
        var member = await SeedMemberAsync("member@example.com", scopes: null);

        var errors = await QueryErrorsAsync(
            """
            query ChannelNode($id: ID!) { node(id: $id) { id } }
            """,
            new { id = "gid://axonhub/Channel/1" },
            token: _gate.IssueJwt(member));
        Assert.Equal("permission denied", errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task ChannelNodeAllowsAUserWithTheReadChannelsScope()
    {
        var channel = SeedChannel(type: "openai", name: "Alpha");
        var member = await SeedMemberAsync(
            "scoped@example.com",
            scopes: """["read_channels"]""");

        var data = await QueryDataAsync(
            """
            query ChannelNode($id: ID!) { node(id: $id) { id } }
            """,
            new { id = "gid://axonhub/Channel/" + channel.Id },
            token: _gate.IssueJwt(member));

        Assert.Equal(
            "gid://axonhub/Channel/" + channel.Id,
            data.GetProperty("node").GetProperty("id").GetString());
    }

    private async Task<UserRecord> SeedMemberAsync(string email, string? scopes)
    {
        var member = _gate.Users.Add(user =>
        {
            user.Id = 77;
            user.Email = email;
            user.Status = "activated";
            user.IsOwner = false;
        });

        // The privacy rules read scopes through IUserAdminStore (ent scopes
        // edge), not the gate's auth store.
        await _users.CreateAsync(
            new UserRecord
            {
                Id = member.Id,
                Email = email,
                Status = "activated",
                IsOwner = false,
                Scopes = scopes,
            },
            roleIds: null);

        return member;
    }

    private ChannelRecord SeedChannel(
        string type,
        string name,
        string status = "enabled",
        long orderingWeight = 0,
        DateTime? createdAt = null,
        DateTime? updatedAt = null,
        string? supportedModels = null,
        string? manualModels = null,
        string? tags = null,
        string? settings = null,
        string? policies = null,
        string? credentials = null,
        string? disabledApiKeys = null)
    {
        var created = createdAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var record = new ChannelRecord
        {
            CreatedAt = created,
            UpdatedAt = updatedAt ?? created,
            Type = type,
            Name = name,
            Status = status,
            SupportedModels = supportedModels ?? "null",
            ManualModels = manualModels,
            AutoSyncModelPattern = "",
            Tags = tags,
            Settings = settings,
            Policies = policies,
            Credentials = credentials ?? "{}",
            DisabledApiKeys = disabledApiKeys,
            OrderingWeight = orderingWeight,
        };

        _channels.Seed(record);
        return record;
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
        using var request = new HttpRequestMessage(HttpMethod.Post, "/admin/graphql")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { query, variables }),
                Encoding.UTF8,
                "application/json"),
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                token ?? _gate.Jwt);

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }
}

/// <summary>
/// In-memory IChannelAdminStore with ent-like filtering, keyset paging and the
/// aggregate queries' status predicates.
/// </summary>
internal sealed class FakeChannelAdminStore : IChannelAdminStore
{
    private readonly List<ChannelRecord> _rows = [];
    private long _nextId = 1;

    public List<ChannelListQuery> Queries { get; } = [];

    public List<ChannelListQuery> AllQueries { get; } = [];

    /// <summary>Status lists captured by CountByTypeAsync, one entry per call.</summary>
    public List<IReadOnlyList<string>?> CountStatusIn { get; } = [];

    public Exception? SummaryFailure { get; set; }

    public Exception? TagsFailure { get; set; }

    public Exception? CountFailure { get; set; }

    /// <summary>
    /// Ids returned on their first GetByIdAsync lookup and missing on the
    /// next, modelling a concurrent soft-delete between an edge query and the
    /// back-reference it issues (ent's Only then reports NotFound).
    /// </summary>
    public HashSet<long> VanishAfterFirstLookup { get; } = [];

    private readonly HashSet<long> _servedById = [];

    public void Seed(ChannelRecord record)
    {
        record.Id = _nextId++;
        _rows.Add(record);
    }

    public Task<ChannelRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        var row = _rows.SingleOrDefault(candidate => candidate.Id == id && candidate.DeletedAt == 0);
        if (row is not null
            && VanishAfterFirstLookup.Contains(id)
            && !_servedById.Add(id))
        {
            return Task.FromResult<ChannelRecord?>(null);
        }

        return Task.FromResult(row);
    }

    public Task<ListPage<ChannelRecord>> ListAsync(
        ChannelListQuery query,
        CancellationToken cancellationToken = default)
    {
        Queries.Add(query);

        var filtered = _rows.Where(row => row.DeletedAt == 0 && Matches(query, row)).ToList();
        var page = FakePagination.Paginate(
            filtered,
            query.SortField != ChannelSortField.Id,
            row => row.Id,
            row => SortValue(row, query.SortField),
            query.Direction,
            query.After,
            query.Before,
            query.First,
            query.Last);

        return Task.FromResult(new ListPage<ChannelRecord>(
            page.Items, page.HasNextPage, page.HasPreviousPage, page.TotalCount));
    }

    public Task<IReadOnlyList<ChannelRecord>> ListAllAsync(
        ChannelListQuery query,
        CancellationToken cancellationToken = default)
    {
        AllQueries.Add(query);

        var filtered = _rows.Where(row => row.DeletedAt == 0 && Matches(query, row));
        IReadOnlyList<ChannelRecord> ordered = query.Ordered
            ? OrderByField(filtered, query)
            : filtered.OrderBy(static row => row.Id).ToList();

        return Task.FromResult(ordered);
    }

    public Task<IReadOnlyList<ChannelRecord>> ListSummariesAsync(
        bool includeArchived,
        CancellationToken cancellationToken = default)
    {
        if (SummaryFailure is not null)
        {
            throw SummaryFailure;
        }

        var statuses = includeArchived
            ? new[] { "enabled", "disabled", "archived" }
            : ["enabled", "disabled"];

        return Task.FromResult<IReadOnlyList<ChannelRecord>>(
            _rows.Where(row => row.DeletedAt == 0 && statuses.Contains(row.Status))
                .OrderByDescending(static row => row.OrderingWeight)
                .ThenBy(static row => row.Id)
                .ToList());
    }

    public Task<IReadOnlyList<string?>> ListTagRowsAsync(
        CancellationToken cancellationToken = default)
    {
        if (TagsFailure is not null)
        {
            throw TagsFailure;
        }

        return Task.FromResult<IReadOnlyList<string?>>(
            _rows.Where(row => row.DeletedAt == 0 && row.Status != "archived")
                .OrderBy(static row => row.Id)
                .Select(static row => row.Tags)
                .ToList());
    }

    public Task<IReadOnlyList<ChannelTypeCount>> CountByTypeAsync(
        IReadOnlyList<string>? statusIn,
        CancellationToken cancellationToken = default)
    {
        CountStatusIn.Add(statusIn);

        if (CountFailure is not null)
        {
            throw CountFailure;
        }

        var rows = _rows
            .Where(row => row.DeletedAt == 0)
            .Where(row => statusIn is { Count: > 0 }
                ? statusIn.Contains(row.Status)
                : row.Status != "archived")
            .GroupBy(static row => row.Type, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => new ChannelTypeCount(group.Key, group.Count()))
            .ToList();

        return Task.FromResult<IReadOnlyList<ChannelTypeCount>>(rows);
    }

    private static object? SortValue(ChannelRecord row, ChannelSortField field) => field switch
    {
        ChannelSortField.UpdatedAt => row.UpdatedAt,
        ChannelSortField.Type => row.Type,
        ChannelSortField.Name => row.Name,
        ChannelSortField.Status => row.Status,
        ChannelSortField.OrderingWeight => row.OrderingWeight,
        _ => null,
    };

    private static List<ChannelRecord> OrderByField(
        IEnumerable<ChannelRecord> rows,
        ChannelListQuery query)
        => query.Direction == SortDirection.Desc
            ? [.. rows.OrderByDescending(row => SortValue(row, query.SortField))
                .ThenBy(static row => row.Id)]
            : [.. rows.OrderBy(row => SortValue(row, query.SortField))
                .ThenBy(static row => row.Id)];

    private static bool Matches(ChannelListQuery query, ChannelRecord row)
    {
        if (!MatchesFilter(row, query.Where))
        {
            return false;
        }

        if (string.IsNullOrEmpty(query.HasTag))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(row.Tags) || row.Tags == "null")
        {
            return false;
        }

        try
        {
            var tags = JsonSerializer.Deserialize<string?[]>(row.Tags);
            return tags is not null
                && tags.Any(tag => string.Equals(tag, query.HasTag, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool MatchesFilter(ChannelRecord row, ChannelListFilter? filter)
    {
        if (filter is null)
        {
            return true;
        }

        if (filter.Id is not null && row.Id != filter.Id)
        {
            return false;
        }

        if (filter.IdIn is not null && !filter.IdIn.Contains(row.Id))
        {
            return false;
        }

        if (filter.Type is not null && row.Type != filter.Type)
        {
            return false;
        }

        if (filter.TypeIn is not null && !filter.TypeIn.Contains(row.Type))
        {
            return false;
        }

        if (filter.Name is not null && row.Name != filter.Name)
        {
            return false;
        }

        if (filter.NameIn is not null && !filter.NameIn.Contains(row.Name))
        {
            return false;
        }

        if (filter.NameContainsFold is not null
            && !row.Name.Contains(filter.NameContainsFold, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (filter.Status is not null && row.Status != filter.Status)
        {
            return false;
        }

        if (filter.StatusIn is not null && !filter.StatusIn.Contains(row.Status))
        {
            return false;
        }

        if (filter.ErrorMessageIsNil && row.ErrorMessage is not null)
        {
            return false;
        }

        if (filter.ErrorMessageNotNil && row.ErrorMessage is null)
        {
            return false;
        }

        if (filter.Not is not null && MatchesFilter(row, filter.Not))
        {
            return false;
        }

        if (filter.And is not null && !filter.And.All(inner => MatchesFilter(row, inner)))
        {
            return false;
        }

        if (filter.Or is not null && !filter.Or.Any(inner => MatchesFilter(row, inner)))
        {
            return false;
        }

        return true;
    }
}

/// <summary>
/// In-memory IChannelModelPriceStore for the channel read edges: prices and
/// versions are seeded directly and only the three read seams are implemented.
/// The write seams are unreachable from the management read surface.
/// </summary>
internal sealed class FakeChannelPriceReadStore : IChannelModelPriceStore
{
    private readonly List<ChannelModelPriceRecord> _prices = [];
    private readonly List<ChannelModelPriceVersionRecord> _versions = [];
    private long _nextPriceId = 1;
    private long _nextVersionId = 1;

    /// <summary>
    /// Ids hidden from GetByIdAsync only; the version back-edge is the first
    /// GetByIdAsync lookup for its parent price, so this models a concurrent
    /// soft-delete (ent's Only then reports NotFound).
    /// </summary>
    public HashSet<long> HideFromGetById { get; } = [];

    public ChannelModelPriceRecord SeedPrice(
        long channelId,
        string modelId,
        string priceJson,
        string referenceId,
        long? id = null,
        long deletedAt = 0,
        DateTime? createdAt = null,
        DateTime? updatedAt = null)
    {
        var created = createdAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var row = new ChannelModelPriceRecord
        {
            Id = id ?? _nextPriceId++,
            CreatedAt = created,
            UpdatedAt = updatedAt ?? created,
            DeletedAt = deletedAt,
            ModelId = modelId,
            Price = priceJson,
            ReferenceId = referenceId,
            ChannelId = channelId,
        };

        if (row.Id >= _nextPriceId)
        {
            _nextPriceId = row.Id + 1;
        }

        _prices.Add(row);
        return row;
    }

    public ChannelModelPriceVersionRecord SeedVersion(
        long channelId,
        long priceId,
        string modelId,
        string priceJson,
        string status = "active",
        DateTime? effectiveStartAt = null,
        DateTime? effectiveEndAt = null,
        string referenceId = "",
        long? id = null,
        DateTime? createdAt = null,
        DateTime? updatedAt = null)
    {
        var created = createdAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var row = new ChannelModelPriceVersionRecord
        {
            Id = id ?? _nextVersionId++,
            CreatedAt = created,
            UpdatedAt = updatedAt ?? created,
            ChannelId = channelId,
            ModelId = modelId,
            ChannelModelPriceId = priceId,
            Price = priceJson,
            Status = status,
            EffectiveStartAt = effectiveStartAt ?? created,
            EffectiveEndAt = effectiveEndAt,
            ReferenceId = referenceId,
        };

        if (row.Id >= _nextVersionId)
        {
            _nextVersionId = row.Id + 1;
        }

        _versions.Add(row);
        return row;
    }

    public Task<ChannelModelPriceRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
        => Task.FromResult(HideFromGetById.Contains(id)
            ? null
            : _prices.SingleOrDefault(row => row.Id == id && row.DeletedAt == 0));

    public Task<IReadOnlyList<ChannelModelPriceRecord>> ListByChannelAsync(
        long channelId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ChannelModelPriceRecord>>(
            _prices.Where(row => row.ChannelId == channelId && row.DeletedAt == 0)
                .OrderBy(static row => row.Id)
                .ToList());

    public Task<IReadOnlyList<ChannelModelPriceVersionRecord>> ListVersionsAsync(
        long channelModelPriceId,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ChannelModelPriceVersionRecord>>(
            _versions.Where(row => row.ChannelModelPriceId == channelModelPriceId)
                .OrderBy(static row => row.Id)
                .ToList());

    public Task<long> CreateAsync(
        ChannelModelPriceRecord price,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the channel read surface never creates a price");

    public Task<IReadOnlyList<ChannelModelPriceRecord>> ListEnabledChannelPricesAsync(
        long channelId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the channel read surface never preloads enabled prices");

    public Task<IReadOnlyList<ChannelModelPriceRecord?>> ApplyAsync(
        long channelId,
        IReadOnlyList<ChannelModelPriceChange> changes,
        DateTime now,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the channel read surface never applies a price plan");

    public Task<bool> EnsureAutoPricesAsync(
        long channelId,
        IReadOnlyList<string> modelIds,
        ModelCardPriceJsonFactory modelCardToPriceJson,
        ChannelPriceReferenceIdFactory referenceIdFactory,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the channel read surface never auto-prices");

    public Task<IReadOnlyList<ChannelModelPriceTemplate>> ResolveAutoPriceTemplatesAsync(
        IReadOnlyList<string> modelIds,
        ModelCardPriceJsonFactory modelCardToPriceJson,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the channel read surface never resolves price templates");

    public Task<bool> ApplyAutoPriceTemplatesAsync(
        long channelId,
        IReadOnlyList<ChannelModelPriceTemplate> templates,
        ChannelPriceReferenceIdFactory referenceIdFactory,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the channel read surface never applies price templates");

    public Task CopyChannelPricesAsync(
        long sourceChannelId,
        long targetChannelId,
        ChannelPriceReferenceIdFactory referenceIdFactory,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the channel read surface never copies prices");
}
