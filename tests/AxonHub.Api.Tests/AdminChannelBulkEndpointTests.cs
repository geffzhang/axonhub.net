using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AxonHub.Api.GraphQL;
using AxonHub.Api.Management;
using AxonHub.Grains.Abstractions;
using AxonHub.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AxonHub.Api.Tests;

/// <summary>
/// Locks the bulk channel mutations against the reference resolvers and
/// biz.ChannelService (axonhub.resolvers.go:390-415; channel_bulk.go:23-45,
/// 242-340): ordering writes every weight in one transaction and prefixes
/// store failures, while auto-disable validates the action before the
/// transaction, normalizes write_rules with channel-level allowDelete, and
/// preserves stream plus the stored rules for off.
/// </summary>
public sealed class AdminChannelBulkEndpointTests : IAsyncLifetime
{
    private const string OrderingMutation =
        """
        mutation Order($input: BulkUpdateChannelOrderingInput!) {
          bulkUpdateChannelOrdering(input: $input) {
            success updated channels { id name orderingWeight }
          }
        }
        """;

    private const string AutoDisableMutation =
        """
        mutation AutoDisable($input: BulkUpdateChannelAutoDisableInput!) {
          bulkUpdateChannelAutoDisable(input: $input) {
            success
            updated
            channels {
              id
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
        """;

    private readonly FakeChannelWriteStore _channels = new();
    private readonly ChannelLimiterManager _limiters = new();
    private readonly FakeChannelModelPriceStore _prices = new();

    private readonly AdminGraphqlGateServices _gate = new();

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        _gate.Register(builder.Services);
        builder.Services.AddSingleton<IChannelWriteStore>(_channels);
        builder.Services.AddSingleton<IChannelModelPriceStore>(_prices);
        builder.Services.AddSingleton(_limiters);
        builder.Services.AddSingleton<ChannelLifecycleService>();

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
    public async Task BulkUpdateChannelOrderingWritesEveryWeightAndReturnsTheChannels()
    {
        var alpha = SeedChannel(name: "Alpha", orderingWeight: 1);
        var bravo = SeedChannel(name: "Bravo", orderingWeight: 2);

        var data = await QueryDataAsync(
            OrderingMutation,
            new
            {
                input = new
                {
                    channels = new object[]
                    {
                        new { id = Gid(alpha.Id), orderingWeight = 100 },
                        new { id = Gid(bravo.Id), orderingWeight = 50 },
                    },
                },
            });

        var payload = data.GetProperty("bulkUpdateChannelOrdering");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal(2, payload.GetProperty("updated").GetInt32());

        var nodes = payload.GetProperty("channels").EnumerateArray().ToArray();
        Assert.Equal(2, nodes.Length);
        Assert.Equal(100, nodes[0].GetProperty("orderingWeight").GetInt32());
        Assert.Equal(50, nodes[1].GetProperty("orderingWeight").GetInt32());

        Assert.Equal(100, alpha.OrderingWeight);
        Assert.Equal(50, bravo.OrderingWeight);

        var call = Assert.Single(_channels.OrderingCalls);
        Assert.Equal([(alpha.Id, 100), (bravo.Id, 50)], call);
    }

    [Fact]
    public async Task BulkUpdateChannelOrderingPrefixesAMissingRowWithTheResolverMessage()
    {
        var errors = await QueryErrorsAsync(
            OrderingMutation,
            new
            {
                input = new
                {
                    channels = new[] { new { id = Gid(424242), orderingWeight = 7 } },
                },
            });

        Assert.Equal(
            "failed to bulk update channel ordering: "
            + "failed to update channel 424242: ent: channel not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task BulkUpdateChannelOrderingAcceptsAnEmptyChannelList()
    {
        var data = await QueryDataAsync(
            OrderingMutation,
            new { input = new { channels = Array.Empty<object>() } });

        var payload = data.GetProperty("bulkUpdateChannelOrdering");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal(0, payload.GetProperty("updated").GetInt32());
        Assert.Empty(payload.GetProperty("channels").EnumerateArray());

        // The reference loop is a no-op but the store is still called once.
        var call = Assert.Single(_channels.OrderingCalls);
        Assert.Empty(call);
    }

    [Fact]
    public async Task BulkUpdateChannelAutoDisableWritesNormalizedRulesAndKeepsStream()
    {
        var channel = SeedChannel(
            name: "Alpha",
            policies: """{"stream":"require","apiKeyAutoDisableMode":"off"}""");

        int[] statusCodes = [503, 401, 401];
        string[] keywordPatterns = [" rate limit ", "", "rate limit"];

        var data = await QueryDataAsync(
            AutoDisableMutation,
            new
            {
                input = new
                {
                    channelIDs = new[] { Gid(channel.Id) },
                    action = "write_rules",
                    rules = new object[]
                    {
                        new
                        {
                            statusCodes,
                            keywordPatterns,
                            times = 2,
                            action = "permanent_disable",
                        },
                    },
                },
            });

        var payload = data.GetProperty("bulkUpdateChannelAutoDisable");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal(1, payload.GetProperty("updated").GetInt32());

        var call = Assert.Single(_channels.AutoDisableCalls);
        Assert.Equal([channel.Id], call.Ids);
        Assert.Equal("custom", call.Mode);

        // normalizeAutoDisableRules sorts/compacts status codes and trims and
        // deduplicates keyword patterns before the store sees them.
        Assert.Equal(
            """[{"statusCodes":[401,503],"keywordPatterns":["rate limit"],"times":2,"action":"permanent_disable"}]""",
            call.RulesJson);

        var policies = Assert.IsType<JsonObject>(JsonNode.Parse(channel.Policies!));
        Assert.Equal("require", (string?)policies["stream"]);
        Assert.Equal("custom", (string?)policies["apiKeyAutoDisableMode"]);
        Assert.Equal(
            """[{"statusCodes":[401,503],"keywordPatterns":["rate limit"],"times":2,"action":"permanent_disable"}]""",
            policies["apiKeyAutoDisableRules"]!.ToJsonString());

        var returned = payload.GetProperty("channels").EnumerateArray().Single();
        Assert.Equal("require", returned.GetProperty("policies").GetProperty("stream").GetString());
        Assert.Equal(
            "custom",
            returned.GetProperty("policies").GetProperty("apiKeyAutoDisableMode").GetString());
    }

    [Fact]
    public async Task BulkUpdateChannelAutoDisableAcceptsPermanentDeleteRules()
    {
        var channel = SeedChannel(name: "Alpha");

        var data = await QueryDataAsync(
            AutoDisableMutation,
            new
            {
                input = new
                {
                    channelIDs = new[] { Gid(channel.Id) },
                    action = "write_rules",
                    rules = new object[]
                    {
                        new { times = 1, action = "permanent_disable_delete" },
                    },
                },
            });

        Assert.True(
            data.GetProperty("bulkUpdateChannelAutoDisable").GetProperty("success").GetBoolean());
        Assert.Equal(
            """[{"times":1,"action":"permanent_disable_delete"}]""",
            Assert.Single(_channels.AutoDisableCalls).RulesJson);
    }

    [Fact]
    public async Task BulkUpdateChannelAutoDisableInheritClearsTheRules()
    {
        var channel = SeedChannel(
            name: "Alpha",
            policies:
                """{"stream":"unlimited","apiKeyAutoDisableMode":"custom","apiKeyAutoDisableRules":[{"times":3,"action":"permanent_disable"}]}""");

        await QueryDataAsync(
            AutoDisableMutation,
            new
            {
                input = new
                {
                    channelIDs = new[] { Gid(channel.Id) },
                    action = "inherit",
                    rules = new object[] { new { times = 1, action = "permanent_disable" } },
                },
            });

        var call = Assert.Single(_channels.AutoDisableCalls);
        Assert.Equal("inherit", call.Mode);
        Assert.Null(call.RulesJson);

        var policies = Assert.IsType<JsonObject>(JsonNode.Parse(channel.Policies!));
        Assert.Equal("unlimited", (string?)policies["stream"]);
        Assert.Equal("inherit", (string?)policies["apiKeyAutoDisableMode"]);
        Assert.False(policies.ContainsKey("apiKeyAutoDisableRules"));
    }

    [Fact]
    public async Task BulkUpdateChannelAutoDisableOffKeepsTheStoredRulesAndIgnoresTheInput()
    {
        const string storedRules =
            """{"statusCodes":[429],"times":3,"action":"permanent_disable"}""";
        var channel = SeedChannel(
            name: "Alpha",
            policies:
                $$"""{"stream":"unlimited","apiKeyAutoDisableMode":"custom","apiKeyAutoDisableRules":[{{storedRules}}]}""");

        var data = await QueryDataAsync(
            AutoDisableMutation,
            new
            {
                input = new
                {
                    channelIDs = new[] { Gid(channel.Id) },
                    action = "off",

                    // inherit/off ignore the submitted rules list entirely, so
                    // even an invalid rule never reaches the normalizer.
                    rules = new object[] { new { times = 0, action = "permanent_disable" } },
                },
            });

        Assert.True(
            data.GetProperty("bulkUpdateChannelAutoDisable").GetProperty("success").GetBoolean());

        var call = Assert.Single(_channels.AutoDisableCalls);
        Assert.Equal("off", call.Mode);
        Assert.Null(call.RulesJson);

        var policies = Assert.IsType<JsonObject>(JsonNode.Parse(channel.Policies!));
        Assert.Equal("off", (string?)policies["apiKeyAutoDisableMode"]);
        Assert.Equal(
            "[" + storedRules + "]",
            policies["apiKeyAutoDisableRules"]!.ToJsonString());
    }

    [Fact]
    public async Task BulkUpdateChannelAutoDisableRejectsAnEmptyIdList()
    {
        var errors = await QueryErrorsAsync(
            AutoDisableMutation,
            new
            {
                input = new
                {
                    channelIDs = Array.Empty<string>(),
                    action = "off",
                },
            });

        Assert.Equal(
            "channel IDs are required",
            errors[0].GetProperty("message").GetString());
        Assert.Equal(
            "VALIDATION_FAILED",
            errors[0].GetProperty("extensions").GetProperty("code").GetString());

        Assert.Empty(_channels.AutoDisableCalls);
    }

    [Fact]
    public async Task BulkUpdateChannelAutoDisableRequiresRulesForWriteRules()
    {
        var channel = SeedChannel(name: "Alpha");

        var missing = await QueryErrorsAsync(
            AutoDisableMutation,
            new
            {
                input = new
                {
                    channelIDs = new[] { Gid(channel.Id) },
                    action = "write_rules",
                },
            });
        Assert.Equal(
            "rules are required when writing auto-disable rules",
            missing[0].GetProperty("message").GetString());
        Assert.Equal(
            "VALIDATION_FAILED",
            missing[0].GetProperty("extensions").GetProperty("code").GetString());

        var empty = await QueryErrorsAsync(
            AutoDisableMutation,
            new
            {
                input = new
                {
                    channelIDs = new[] { Gid(channel.Id) },
                    action = "write_rules",
                    rules = Array.Empty<object>(),
                },
            });
        Assert.Equal(
            "rules are required when writing auto-disable rules",
            empty[0].GetProperty("message").GetString());

        Assert.Empty(_channels.AutoDisableCalls);
    }

    [Fact]
    public async Task BulkUpdateChannelAutoDisableRejectsAnInvalidRuleBeforeTheStore()
    {
        var channel = SeedChannel(name: "Alpha");

        var errors = await QueryErrorsAsync(
            AutoDisableMutation,
            new
            {
                input = new
                {
                    channelIDs = new[] { Gid(channel.Id) },
                    action = "write_rules",
                    rules = new object[] { new { times = 0, action = "permanent_disable" } },
                },
            });

        Assert.Equal(
            "API key rule 1 consecutive error count must be at least 1",
            errors[0].GetProperty("message").GetString());
        Assert.Equal(
            "VALIDATION_FAILED",
            errors[0].GetProperty("extensions").GetProperty("code").GetString());

        Assert.Empty(_channels.AutoDisableCalls);
        Assert.Equal("""{"stream":"unlimited"}""", channel.Policies);
    }

    [Fact]
    public async Task BulkUpdateAutoDisableServiceKeepsTheUnsupportedActionBranch()
    {
        // The GraphQL enum makes this branch unreachable from the wire, but the
        // service keeps the reference's %q error for parity (channel_bulk.go:274).
        var service = new ChannelLifecycleService(_channels, _limiters, _prices);

        var error = await Assert.ThrowsAsync<CodedException>(
            () => service.BulkUpdateAutoDisableAsync(new BulkUpdateAutoDisableCommand
            {
                ChannelIds = [1],
                Action = "explode",
            }));

        Assert.Equal("unsupported auto-disable action \"explode\"", error.Message);
        Assert.Equal(CodedException.ValidationFailed, error.Code);
        Assert.Empty(_channels.AutoDisableCalls);
    }

    [Fact]
    public async Task BulkUpdateChannelAutoDisableKeepsTheCountMismatchMessageWithoutAPrefix()
    {
        var channel = SeedChannel(name: "Alpha");

        var errors = await QueryErrorsAsync(
            AutoDisableMutation,
            new
            {
                input = new
                {
                    channelIDs = new[] { Gid(channel.Id), Gid(424242) },
                    action = "inherit",
                },
            });

        // The auto-disable resolver, unlike ordering, adds no prefix.
        Assert.Equal(
            "expected to find 2 channels, but found 1",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task IntrospectionExposesTheBulkChannelMutationsAndInputs()
    {
        var data = await QueryDataAsync(
            """
            {
              __schema { mutationType { fields { name } } }
              ordering: __type(name: "BulkUpdateChannelOrderingInput") {
                inputFields { name }
              }
              autoDisable: __type(name: "BulkUpdateChannelAutoDisableInput") {
                inputFields { name }
              }
              orderingItem: __type(name: "ChannelOrderingItem") {
                kind
                inputFields { name }
              }
              action: __type(name: "BulkAutoDisableAction") {
                enumValues { name }
              }
            }
            """);

        var mutations = data.GetProperty("__schema").GetProperty("mutationType")
            .GetProperty("fields").EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Contains("bulkUpdateChannelOrdering", mutations);
        Assert.Contains("bulkUpdateChannelAutoDisable", mutations);

        Assert.Equal(
            ["channels"],
            FieldNames(data.GetProperty("ordering")));
        Assert.Equal(
            ["channelIDs", "action", "rules"],
            FieldNames(data.GetProperty("autoDisable")));
        Assert.Equal("INPUT_OBJECT", data.GetProperty("orderingItem").GetProperty("kind").GetString());
        Assert.Equal(
            ["id", "orderingWeight"],
            FieldNames(data.GetProperty("orderingItem")));

        var actions = data.GetProperty("action").GetProperty("enumValues")
            .EnumerateArray()
            .Select(static value => value.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Equal(["write_rules", "inherit", "off"], actions);
    }

    private static string[] FieldNames(JsonElement type)
        => type.GetProperty("inputFields").EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .ToArray();

    private static string Gid(long id) => "gid://axonhub/Channel/" + id;

    private ChannelRecord SeedChannel(
        string name,
        string? policies = """{"stream":"unlimited"}""",
        long orderingWeight = 0)
    {
        var record = new ChannelRecord
        {
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Type = "openai",
            BaseUrl = "https://api.example/v1",
            Name = name,
            Status = "disabled",
            Credentials = "{}",
            SupportedModels = "null",
            AutoSyncModelPattern = string.Empty,
            DefaultTestModel = string.Empty,
            Tags = "[]",
            Policies = policies,
            Endpoints = "[]",
            OrderingWeight = orderingWeight,
        };

        _channels.Seed(record);
        return record;
    }

    private async Task<JsonElement> QueryDataAsync(string query, object? variables = null)
    {
        var root = await ExecuteAsync(query, variables);
        Assert.False(root.TryGetProperty("errors", out _), root.ToString());
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
