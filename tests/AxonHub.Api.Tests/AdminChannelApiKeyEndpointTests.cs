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
/// Locks the channel API key management mutations against the reference
/// resolvers and biz.ChannelService API key bookkeeping
/// (axonhub.resolvers.go:418-462; channel_apikey.go:23-391): disable records are
/// keyed by credential reference, the channel is auto-disabled when its last
/// credential goes away, and enabling/deleting restores the channel when the
/// "All API keys disabled" marker no longer holds.
/// </summary>
public sealed class AdminChannelApiKeyEndpointTests : IAsyncLifetime
{
    private const string DisableMutation =
        """
        mutation Disable($channelID: ID!, $key: String!) {
          disableChannelAPIKey(channelID: $channelID, key: $key)
        }
        """;

    private const string EnableMutation =
        """
        mutation Enable($channelID: ID!, $key: String!) {
          enableChannelAPIKey(channelID: $channelID, key: $key)
        }
        """;

    private const string EnableAllMutation =
        """
        mutation EnableAll($channelID: ID!) {
          enableAllChannelAPIKeys(channelID: $channelID)
        }
        """;

    private const string EnableSelectedMutation =
        """
        mutation EnableSelected($channelID: ID!, $keys: [String!]!) {
          enableSelectedChannelAPIKeys(channelID: $channelID, keys: $keys)
        }
        """;

    private const string DeleteMutation =
        """
        mutation Delete($channelID: ID!, $keys: [String!]!) {
          deleteDisabledChannelAPIKeys(channelID: $channelID, keys: $keys) {
            success
            message
          }
        }
        """;

    private const string OAuthCredentials =
        """{"oauth":{"access_token":"tok","refresh_token":"refresh","expires_at":"0001-01-01T00:00:00Z"}}""";

    private static readonly string[] KeyA = ["a"];
    private static readonly string[] KeysAb = ["a", "b"];
    private static readonly string[] KeysAc = ["a", "c"];
    private static readonly string[] OAuthKeys = ["__oauth__"];

    private readonly FakeChannelWriteStore _channels = new();
    private readonly ChannelLimiterManager _limiters = new();
    private readonly FakeChannelModelPriceStore _prices = new();
    private readonly RecordingEnabledChannelsRefresher _refresher = new();

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
        builder.Services.AddSingleton<IEnabledChannelsCacheRefresher>(_refresher);
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
    public async Task DisableAppendsTheRecordAndKeepsTheChannelEnabled()
    {
        var channel = SeedChannel(
            credentials: """{"apiKey":"sk-one","apiKeys":["sk-two"]}""");

        var data = await QueryDataAsync(
            DisableMutation,
            new { channelID = Gid(channel.Id), key = "sk-one" });

        Assert.True(data.GetProperty("disableChannelAPIKey").GetBoolean());

        var record = Assert.Single(DisabledRecords(channel));
        Assert.Equal("sk-one", record.GetProperty("key").GetString());
        Assert.Equal(0, record.GetProperty("errorCode").GetInt32());
        Assert.Equal("Manually disabled by user", record.GetProperty("reason").GetString());
        Assert.Equal(DateTimeKind.Utc, record.GetProperty("disabledAt").GetDateTime().Kind);
        Assert.False(record.TryGetProperty("expiresAt", out _));

        // Another enabled key keeps the channel routable.
        Assert.Equal("enabled", channel.Status);
        Assert.Null(channel.ErrorMessage);
        Assert.Null(channel.AutoDisabledAt);

        // Only the asynchronous (debounced) refresh runs; the synchronous
        // reload is reserved for the exhausted path.
        Assert.Equal(1, _refresher.RequestCount);
        Assert.Equal(0, _refresher.ReloadCount);

        var call = Assert.Single(_channels.ApiKeyStateCalls);
        Assert.Null(call.CredentialsJson);
        Assert.Null(call.Status);
        Assert.False(call.ClearAutoDisableExpiresAt);
    }

    [Fact]
    public async Task DisableRecordsTheDisableWhenAnotherKeyRemains()
    {
        var channel = SeedChannel(
            credentials: """{"apiKey":"sk-one","apiKeys":["sk-two"]}""");

        await QueryDataAsync(
            DisableMutation,
            new { channelID = Gid(channel.Id), key = "sk-one" });

        Assert.Equal("enabled", channel.Status);
        Assert.Equal("sk-one", Assert.Single(DisabledRecords(channel))
            .GetProperty("key").GetString());
    }

    [Fact]
    public async Task DisableOfTheLastKeyDisablesTheChannel()
    {
        var channel = SeedChannel(credentials: """{"apiKey":"sk-one"}""");

        await QueryDataAsync(
            DisableMutation,
            new { channelID = Gid(channel.Id), key = "sk-one" });

        Assert.Equal("disabled", channel.Status);
        Assert.Equal("All API keys disabled (last error: 0)", channel.ErrorMessage);
        Assert.NotNull(channel.AutoDisabledAt);
        Assert.Null(channel.AutoDisableExpiresAt);

        var call = Assert.Single(_channels.ApiKeyStateCalls);
        Assert.Equal("disabled", call.Status);
        Assert.Equal("All API keys disabled (last error: 0)", call.ErrorMessage);
        Assert.NotNull(call.AutoDisabledAt);
        Assert.True(call.ClearAutoDisableExpiresAt);

        // The exhausted path force-reloads synchronously so selection stops
        // using the channel before the mutation returns, then also requests the
        // debounced refresh (channel_apikey.go:107-141).
        Assert.Equal(1, _refresher.ReloadCount);
        Assert.Equal(1, _refresher.RequestCount);
    }

    [Fact]
    public async Task DisableUsesTheOAuthCredentialSentinel()
    {
        var channel = SeedChannel(credentials: OAuthCredentials);

        await QueryDataAsync(
            DisableMutation,
            new { channelID = Gid(channel.Id), key = "__oauth__" });

        var record = Assert.Single(DisabledRecords(channel));
        Assert.Equal("__oauth__", record.GetProperty("key").GetString());

        // The sentinel is the only credential identity, so the channel is
        // exhausted and disabled.
        Assert.Equal("disabled", channel.Status);
        Assert.Equal("All API keys disabled (last error: 0)", channel.ErrorMessage);
    }

    [Fact]
    public async Task DisableIgnoresAKeyThatIsNotOnTheChannel()
    {
        var channel = SeedChannel(credentials: """{"apiKey":"sk-one"}""");

        var data = await QueryDataAsync(
            DisableMutation,
            new { channelID = Gid(channel.Id), key = "sk-unknown" });

        Assert.True(data.GetProperty("disableChannelAPIKey").GetBoolean());
        Assert.Empty(DisabledRecords(channel));
        Assert.Empty(_channels.ApiKeyStateCalls);
    }

    [Fact]
    public async Task DisableIgnoresAnAlreadyDisabledKey()
    {
        var channel = SeedChannel(
            credentials: """{"apiKey":"sk-one"}""",
            disabledApiKeys:
                """[{"key":"sk-one","disabledAt":"2026-01-01T00:00:00Z","errorCode":0}]""");

        await QueryDataAsync(
            DisableMutation,
            new { channelID = Gid(channel.Id), key = "sk-one" });

        Assert.Single(DisabledRecords(channel));
        Assert.Empty(_channels.ApiKeyStateCalls);
    }

    [Fact]
    public async Task DisablePrunesExpiredRecordsBeforeAppending()
    {
        var channel = SeedChannel(
            credentials: """{"apiKeys":["sk-one","sk-two"]}""",
            disabledApiKeys:
                """[{"key":"sk-one","disabledAt":"2025-01-01T00:00:00Z","errorCode":429,"expiresAt":"2025-01-01T01:00:00Z"}]""");

        await QueryDataAsync(
            DisableMutation,
            new { channelID = Gid(channel.Id), key = "sk-one" });

        // The elapsed temporary disable disappears; the fresh record replaces it.
        var record = Assert.Single(DisabledRecords(channel));
        Assert.Equal("sk-one", record.GetProperty("key").GetString());
        Assert.False(record.TryGetProperty("expiresAt", out _));
        Assert.Equal("enabled", channel.Status);
    }

    [Fact]
    public async Task DisableRejectsAnEmptyKey()
    {
        var errors = await QueryErrorsAsync(
            DisableMutation,
            new { channelID = Gid(1), key = string.Empty });

        Assert.Equal(
            "failed to disable channel API key: api key cannot be empty",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_channels.ApiKeyStateCalls);
    }

    [Fact]
    public async Task DisableOnAMissingChannelKeepsBothPrefixes()
    {
        var errors = await QueryErrorsAsync(
            DisableMutation,
            new { channelID = Gid(424242), key = "sk-one" });

        Assert.Equal(
            "failed to disable channel API key: failed to get channel: ent: channel not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task DisableReportsMalformedDisabledKeysLikeAFailedRead()
    {
        var channel = SeedChannel(disabledApiKeys: "not-json");

        var errors = await QueryErrorsAsync(
            DisableMutation,
            new { channelID = Gid(channel.Id), key = "sk-one" });

        Assert.StartsWith(
            "failed to disable channel API key: failed to get channel: ",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_channels.ApiKeyStateCalls);
    }

    [Fact]
    public async Task EnableRemovesTheRecordAndRecoversTheChannel()
    {
        var channel = SeedChannel(
            status: "disabled",
            credentials: """{"apiKey":"sk-one"}""",
            disabledApiKeys:
                """[{"key":"sk-one","disabledAt":"2026-01-01T00:00:00Z","errorCode":429}]""",
            errorMessage: "All API keys disabled (last error: 429)",
            autoDisabledAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            autoDisableExpiresAt: new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc));

        var data = await QueryDataAsync(
            EnableMutation,
            new { channelID = Gid(channel.Id), key = "sk-one" });

        Assert.True(data.GetProperty("enableChannelAPIKey").GetBoolean());
        Assert.Empty(DisabledRecords(channel));
        Assert.Equal("enabled", channel.Status);
        Assert.Null(channel.ErrorMessage);
        Assert.Null(channel.AutoDisabledAt);
        Assert.Null(channel.AutoDisableExpiresAt);

        // Enabling never rewrites credentials.
        Assert.Null(Assert.Single(_channels.ApiKeyStateCalls).CredentialsJson);
    }

    [Fact]
    public async Task EnableRecoversTheChannelEvenWhenAnotherKeyRemainsDisabled()
    {
        var channel = SeedChannel(
            status: "disabled",
            credentials: """{"apiKeys":["sk-one","sk-two"]}""",
            disabledApiKeys:
                """[{"key":"sk-one","disabledAt":"2026-01-01T00:00:00Z","errorCode":429},{"key":"sk-two","disabledAt":"2026-01-01T00:00:00Z","errorCode":429}]""",
            errorMessage: "All API keys disabled (last error: 429)");

        await QueryDataAsync(
            EnableMutation,
            new { channelID = Gid(channel.Id), key = "sk-one" });

        Assert.Equal("sk-two", Assert.Single(DisabledRecords(channel))
            .GetProperty("key").GetString());

        // applyRecoveredChannelStatus only requires one usable credential; the
        // remaining disable record does not keep the channel offline.
        Assert.Equal("enabled", channel.Status);
        Assert.Null(channel.ErrorMessage);
    }

    [Fact]
    public async Task EnableDoesNotRecoverAChannelDisabledForAnotherReason()
    {
        var channel = SeedChannel(
            status: "disabled",
            credentials: """{"apiKey":"sk-one"}""",
            disabledApiKeys:
                """[{"key":"sk-one","disabledAt":"2026-01-01T00:00:00Z","errorCode":429}]""",
            errorMessage: "maintenance window",
            autoDisabledAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await QueryDataAsync(
            EnableMutation,
            new { channelID = Gid(channel.Id), key = "sk-one" });

        // The "All API keys disabled" marker is absent, so the status stays.
        Assert.Equal("disabled", channel.Status);
        Assert.Equal("maintenance window", channel.ErrorMessage);
        Assert.NotNull(channel.AutoDisabledAt);
    }

    [Fact]
    public async Task EnableIgnoresAKeyWithoutADisableRecord()
    {
        var channel = SeedChannel(credentials: """{"apiKey":"sk-one"}""");

        await QueryDataAsync(
            EnableMutation,
            new { channelID = Gid(channel.Id), key = "sk-one" });

        Assert.Empty(_channels.ApiKeyStateCalls);
    }

    [Fact]
    public async Task EnableAllClearsTheListAndRecoversTheChannel()
    {
        var channel = SeedChannel(
            status: "disabled",
            credentials: """{"apiKeys":["sk-one","sk-two"]}""",
            disabledApiKeys:
                """[{"key":"sk-one","disabledAt":"2026-01-01T00:00:00Z","errorCode":429},{"key":"sk-two","disabledAt":"2026-01-01T00:00:00Z","errorCode":429}]""",
            errorMessage: "All API keys disabled (last error: 429)",
            autoDisabledAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await QueryDataAsync(EnableAllMutation, new { channelID = Gid(channel.Id) });

        Assert.Empty(DisabledRecords(channel));
        Assert.Equal("enabled", channel.Status);
        Assert.Null(channel.ErrorMessage);
        Assert.Null(channel.AutoDisabledAt);
    }

    [Fact]
    public async Task EnableSelectedRemovesOnlyTheListedKeys()
    {
        var channel = SeedChannel(
            status: "disabled",
            credentials: """{"apiKeys":["a","b","c"]}""",
            disabledApiKeys:
                """[{"key":"a","disabledAt":"2026-01-01T00:00:00Z","errorCode":429},{"key":"b","disabledAt":"2026-01-01T00:00:00Z","errorCode":429},{"key":"c","disabledAt":"2026-01-01T00:00:00Z","errorCode":429}]""",
            errorMessage: "All API keys disabled (last error: 429)");

        var data = await QueryDataAsync(
            EnableSelectedMutation,
            new
            {
                channelID = Gid(channel.Id),
                keys = KeysAc,
            });

        Assert.True(data.GetProperty("enableSelectedChannelAPIKeys").GetBoolean());
        Assert.Equal("b", Assert.Single(DisabledRecords(channel))
            .GetProperty("key").GetString());

        // The remaining disable record does not block recovery: the reference
        // only requires one usable credential.
        Assert.Equal("enabled", channel.Status);
        Assert.Null(channel.ErrorMessage);
    }

    [Fact]
    public async Task EnableSelectedWithAnEmptyListSkipsTheRead()
    {
        _channels.GetFailure = new StoreFailureException(
            "failed to get channel: boom",
            new InvalidOperationException("boom"));

        var data = await QueryDataAsync(
            EnableSelectedMutation,
            new { channelID = Gid(1), keys = Array.Empty<string>() });

        Assert.True(data.GetProperty("enableSelectedChannelAPIKeys").GetBoolean());
        Assert.Empty(_channels.ApiKeyStateCalls);
    }

    [Fact]
    public async Task DeleteRemovesTheKeysAndPreservesTheLastCredential()
    {
        var channel = SeedChannel(
            credentials: """{"apiKeys":["a","b"]}""",
            disabledApiKeys:
                """[{"key":"a","disabledAt":"2026-01-01T00:00:00Z","errorCode":429},{"key":"b","disabledAt":"2026-01-01T00:00:00Z","errorCode":429}]""");

        var data = await QueryDataAsync(
            DeleteMutation,
            new
            {
                channelID = Gid(channel.Id),
                keys = KeysAb,
            });

        var payload = data.GetProperty("deleteDisabledChannelAPIKeys");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal("ONE_KEY_PRESERVED", payload.GetProperty("message").GetString());

        Assert.Empty(DisabledRecords(channel));

        // The first submitted key is restored as the only APIKeys entry.
        using var credentials = JsonDocument.Parse(channel.Credentials);
        var apiKeys = credentials.RootElement.GetProperty("apiKeys")
            .EnumerateArray().Select(static key => key.GetString()!).ToArray();
        Assert.Equal(["a"], apiKeys);
        Assert.False(credentials.RootElement.TryGetProperty("apiKey", out _));
    }

    [Fact]
    public async Task DeleteRemovesTheLegacyApiKeyAndKeepsTheRemainingList()
    {
        var channel = SeedChannel(
            credentials: """{"apiKey":"a","apiKeys":["b"]}""",
            disabledApiKeys:
                """[{"key":"a","disabledAt":"2026-01-01T00:00:00Z","errorCode":429}]""");

        var data = await QueryDataAsync(
            DeleteMutation,
            new
            {
                channelID = Gid(channel.Id),
                keys = KeyA,
            });

        var payload = data.GetProperty("deleteDisabledChannelAPIKeys");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal(string.Empty, payload.GetProperty("message").GetString());

        // Go clears empty strings through omitempty; only apiKeys survives.
        Assert.Equal("""{"apiKeys":["b"]}""", channel.Credentials);
        Assert.Empty(DisabledRecords(channel));
    }

    [Fact]
    public async Task DeleteRemovesTheMatchingVertexTripleAndKeepsTheOthers()
    {
        var channel = SeedChannel(
            credentials: """{"apiKeys":["a"],"vertexApiKeys":[{"projectID":"p1","apiKey":"va","region":"r1"},{"projectID":"p2","apiKey":"vb","region":"r2"}]}""",
            disabledApiKeys:
                """[{"key":"va","disabledAt":"2026-01-01T00:00:00Z","errorCode":429},{"key":"vb","disabledAt":"2026-01-01T00:00:00Z","errorCode":429}]""");

        var data = await QueryDataAsync(
            DeleteMutation,
            new
            {
                channelID = Gid(channel.Id),
                keys = new[] { "va" },
            });

        var payload = data.GetProperty("deleteDisabledChannelAPIKeys");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal(string.Empty, payload.GetProperty("message").GetString());
        Assert.Equal(
            """{"apiKeys":["a"],"vertexApiKeys":[{"projectID":"p2","apiKey":"vb","region":"r2"}]}""",
            channel.Credentials);
        Assert.Equal(
            "vb",
            Assert.Single(DisabledRecords(channel)).GetProperty("key").GetString());
    }

    [Fact]
    public async Task DeleteRestoresTheFirstRemovedVertexTripleWhenNoKeyRemains()
    {
        var channel = SeedChannel(
            credentials: """{"vertexApiKeys":[{"projectID":"p1","apiKey":"va","region":"r1"},{"projectID":"p2","apiKey":"vb","region":"r2"}]}""",
            disabledApiKeys:
                """[{"key":"va","disabledAt":"2026-01-01T00:00:00Z","errorCode":429},{"key":"vb","disabledAt":"2026-01-01T00:00:00Z","errorCode":429}]""");

        var data = await QueryDataAsync(
            DeleteMutation,
            new
            {
                channelID = Gid(channel.Id),
                keys = new[] { "va", "vb" },
            });

        var payload = data.GetProperty("deleteDisabledChannelAPIKeys");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal("ONE_KEY_PRESERVED", payload.GetProperty("message").GetString());

        // The first removed triple is restored in full: project and region
        // survive the preserve-last-key rule (channel_apikey.go:357-362).
        Assert.Equal(
            """{"vertexApiKeys":[{"projectID":"p1","apiKey":"va","region":"r1"}]}""",
            channel.Credentials);
        Assert.Empty(DisabledRecords(channel));
    }

    [Fact]
    public async Task DeleteKeepsAFlatKeyWithoutRestoringAVertexTriple()
    {
        var channel = SeedChannel(
            credentials: """{"apiKey":"flat","vertexApiKeys":[{"projectID":"p1","apiKey":"va","region":"r1"}]}""",
            disabledApiKeys:
                """[{"key":"va","disabledAt":"2026-01-01T00:00:00Z","errorCode":429}]""");

        var data = await QueryDataAsync(
            DeleteMutation,
            new
            {
                channelID = Gid(channel.Id),
                keys = new[] { "va" },
            });

        var payload = data.GetProperty("deleteDisabledChannelAPIKeys");
        Assert.Equal(string.Empty, payload.GetProperty("message").GetString());
        Assert.Equal("""{"apiKey":"flat"}""", channel.Credentials);
        Assert.Empty(DisabledRecords(channel));
    }

    [Fact]
    public async Task DisableOfTheLastVertexKeyDisablesTheChannel()
    {
        var channel = SeedChannel(
            credentials: """{"vertexApiKeys":[{"projectID":"p1","apiKey":"va","region":"r1"}]}""");

        await QueryDataAsync(
            DisableMutation,
            new { channelID = Gid(channel.Id), key = "va" });

        Assert.Equal("disabled", channel.Status);
        Assert.Equal("All API keys disabled (last error: 0)", channel.ErrorMessage);
        Assert.Equal(
            "va",
            Assert.Single(DisabledRecords(channel)).GetProperty("key").GetString());
    }

    [Fact]
    public async Task EnableOfAVertexKeyRecoversTheChannel()
    {
        var channel = SeedChannel(
            status: "disabled",
            credentials: """{"vertexApiKeys":[{"projectID":"p1","apiKey":"va","region":"r1"}]}""",
            disabledApiKeys:
                """[{"key":"va","disabledAt":"2026-01-01T00:00:00Z","errorCode":429}]""",
            errorMessage: "All API keys disabled (last error: 429)",
            autoDisabledAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        await QueryDataAsync(
            EnableMutation,
            new { channelID = Gid(channel.Id), key = "va" });

        Assert.Empty(DisabledRecords(channel));
        Assert.Equal("enabled", channel.Status);
        Assert.Null(channel.ErrorMessage);
        Assert.Null(channel.AutoDisabledAt);
    }

    [Fact]
    public async Task DeleteRecoversTheChannelWhenAnEnabledCredentialRemains()
    {
        var channel = SeedChannel(
            status: "disabled",
            credentials: """{"apiKeys":["a","b"]}""",
            disabledApiKeys:
                """[{"key":"a","disabledAt":"2026-01-01T00:00:00Z","errorCode":429}]""",
            errorMessage: "All API keys disabled (last error: 429)",
            autoDisabledAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            autoDisableExpiresAt: new DateTime(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc));

        await QueryDataAsync(
            DeleteMutation,
            new
            {
                channelID = Gid(channel.Id),
                keys = KeyA,
            });

        Assert.Equal("enabled", channel.Status);
        Assert.Null(channel.ErrorMessage);
        Assert.Null(channel.AutoDisabledAt);
        Assert.Null(channel.AutoDisableExpiresAt);
    }

    [Fact]
    public async Task DeleteRejectsOAuthChannels()
    {
        var channel = SeedChannel(credentials: OAuthCredentials);

        var errors = await QueryErrorsAsync(
            DeleteMutation,
            new
            {
                channelID = Gid(channel.Id),
                keys = OAuthKeys,
            });

        Assert.Equal(
            "failed to delete disabled channel API keys: "
            + "cannot delete API keys for OAuth channels",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_channels.ApiKeyStateCalls);
    }

    [Fact]
    public async Task DeleteWithAnEmptyListReturnsSuccessWithoutReading()
    {
        _channels.GetFailure = new StoreFailureException(
            "failed to get channel: boom",
            new InvalidOperationException("boom"));

        var data = await QueryDataAsync(
            DeleteMutation,
            new { channelID = Gid(1), keys = Array.Empty<string>() });

        var payload = data.GetProperty("deleteDisabledChannelAPIKeys");
        Assert.True(payload.GetProperty("success").GetBoolean());
        Assert.Equal(string.Empty, payload.GetProperty("message").GetString());
        Assert.Empty(_channels.ApiKeyStateCalls);
    }

    [Fact]
    public async Task ApiKeyMutationsForwardStoreFailuresWithTheReferencePrefixes()
    {
        var channel = SeedChannel(credentials: """{"apiKey":"sk-one"}""");
        _channels.ApiKeyStateFailure = new StoreFailureException(
            "boom",
            new InvalidOperationException("boom"));

        var errors = await QueryErrorsAsync(
            DisableMutation,
            new { channelID = Gid(channel.Id), key = "sk-one" });

        Assert.Equal(
            "failed to disable channel API key: failed to disable api key: boom",
            errors[0].GetProperty("message").GetString());

        _channels.GetFailure = new StoreFailureException(
            "failed to get channel: boom",
            new InvalidOperationException("boom"));

        var readErrors = await QueryErrorsAsync(
            EnableAllMutation,
            new { channelID = Gid(channel.Id) });

        Assert.Equal(
            "failed to enable all channel API keys: failed to get channel: boom",
            readErrors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task UpdateOnAMissingRowReportsTheOperationPrefix()
    {
        var channel = SeedChannel(credentials: """{"apiKey":"sk-one"}""");

        // The row disappears between the read and the write.
        _channels.ApiKeyStateRowMissing = true;

        var errors = await QueryErrorsAsync(
            DisableMutation,
            new { channelID = Gid(channel.Id), key = "sk-one" });

        Assert.Equal(
            "failed to disable channel API key: failed to disable api key: "
            + "ent: channel not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task IntrospectionExposesTheApiKeyMutations()
    {
        var data = await QueryDataAsync(
            """
            {
              __schema { mutationType { fields { name } } }
              payload: __type(name: "DeleteDisabledAPIKeysPayload") {
                fields { name type { kind name ofType { kind name } } }
              }
            }
            """);

        var mutations = data.GetProperty("__schema").GetProperty("mutationType")
            .GetProperty("fields").EnumerateArray()
            .Select(static field => field.GetProperty("name").GetString()!)
            .ToArray();
        Assert.Contains("disableChannelAPIKey", mutations);
        Assert.Contains("enableChannelAPIKey", mutations);
        Assert.Contains("enableAllChannelAPIKeys", mutations);
        Assert.Contains("enableSelectedChannelAPIKeys", mutations);
        Assert.Contains("deleteDisabledChannelAPIKeys", mutations);

        var fields = data.GetProperty("payload").GetProperty("fields").EnumerateArray()
            .ToDictionary(
                static field => field.GetProperty("name").GetString()!,
                static field => field.GetProperty("type"));
        Assert.Equal("NON_NULL", fields["success"].GetProperty("kind").GetString());
        Assert.Equal(
            "Boolean",
            fields["success"].GetProperty("ofType").GetProperty("name").GetString());

        // axonhub.graphql:932-935 declares message nullable.
        Assert.Equal("SCALAR", fields["message"].GetProperty("kind").GetString());
        Assert.Equal("String", fields["message"].GetProperty("name").GetString());
    }

    private static JsonElement[] DisabledRecords(ChannelRecord channel)
    {
        using var document = JsonDocument.Parse(channel.DisabledApiKeys!);
        return document.RootElement.EnumerateArray()
            .Select(static record => record.Clone())
            .ToArray();
    }

    private static string Gid(long id) => "gid://axonhub/Channel/" + id;

    private ChannelRecord SeedChannel(
        string status = "enabled",
        string credentials = "{}",
        string? disabledApiKeys = "[]",
        string? errorMessage = null,
        DateTime? autoDisabledAt = null,
        DateTime? autoDisableExpiresAt = null)
    {
        var record = new ChannelRecord
        {
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Type = "openai",
            BaseUrl = "https://api.example/v1",
            Name = "Alpha",
            Status = status,
            Credentials = credentials,
            DisabledApiKeys = disabledApiKeys,
            SupportedModels = "null",
            AutoSyncModelPattern = string.Empty,
            DefaultTestModel = string.Empty,
            Tags = "[]",
            Policies = """{"stream":"unlimited"}""",
            Settings = null,
            Endpoints = "[]",
            ErrorMessage = errorMessage,
            AutoDisabledAt = autoDisabledAt,
            AutoDisableExpiresAt = autoDisableExpiresAt,
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
