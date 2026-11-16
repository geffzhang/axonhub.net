using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// Locks the channel lifecycle mutations over /admin/graphql against the
/// reference resolvers and biz.ChannelService (axonhub.resolvers.go:203-303,
/// channel.go:1205-1290, channel_bulk.go:163-462): status changes clear the
/// auto-disable markers, delete is a soft delete that drops the limiter, bulk
/// status mutations verify the id count, and tag mutations normalize before
/// delegating.
/// </summary>
public sealed class AdminChannelLifecycleEndpointTests : IAsyncLifetime
{
    private static readonly string[] NormalizedTagInput = [" a ", "", "a", "b"];
    private static readonly string[] WhitespaceTagInput = ["b ", " "];
    private static readonly string[] SingleTagA = ["a"];
    private static readonly string[] BlankTagInput = ["  "];
    private static readonly string[] DuplicatedNewTagInput = [" new ", "new"];
    private static readonly string[] OldTagInput = ["old"];

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
    public async Task UpdateChannelStatusClearsTheMarkersAndReturnsTheChannel()
    {
        var channel = SeedChannel(
            status: "disabled",
            errorMessage: "boom",
            autoDisabledAt: new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            autoDisableExpiresAt: new DateTime(2026, 1, 2, 4, 4, 5, DateTimeKind.Utc));

        var data = await QueryDataAsync(
            """
            mutation Update($id: ID!, $status: ChannelStatus!) {
              updateChannelStatus(id: $id, status: $status) {
                id name status errorMessage autoDisabledAt autoDisableExpiresAt
              }
            }
            """,
            new { id = Gid(channel.Id), status = "enabled" });

        var node = data.GetProperty("updateChannelStatus");
        Assert.Equal(Gid(channel.Id), node.GetProperty("id").GetString());
        Assert.Equal("enabled", node.GetProperty("status").GetString());
        Assert.Null(channel.AutoDisabledAt);
        Assert.Null(channel.AutoDisableExpiresAt);

        // The status change does not clear error_message (only bulk recover does).
        Assert.Equal("boom", node.GetProperty("errorMessage").GetString());
        Assert.Equal("boom", channel.ErrorMessage);

        var call = Assert.Single(_channels.StatusUpdates);
        Assert.Equal((channel.Id, "enabled"), call);

        // The mutation requests the debounced cache refresh (asyncReloadChannels);
        // it never blocks on a synchronous reload.
        Assert.Equal(1, _refresher.RequestCount);
        Assert.Equal(0, _refresher.ReloadCount);
    }

    [Fact]
    public async Task ChannelIdsIgnoreTheGidTypeSegmentLikeTheReference()
    {
        var channel = SeedChannel();

        var data = await QueryDataAsync(
            """
            mutation Update($id: ID!) {
              updateChannelStatus(id: $id, status: archived) { id status }
            }
            """,
            new { id = "gid://axonhub/User/" + channel.Id });

        Assert.Equal("archived", data.GetProperty("updateChannelStatus").GetProperty("status").GetString());
        Assert.Equal("archived", channel.Status);
    }

    [Fact]
    public async Task UpdateChannelStatusOnAMissingChannelKeepsTheReferenceMessage()
    {
        var errors = await QueryErrorsAsync(
            """
            mutation Update($id: ID!) {
              updateChannelStatus(id: $id, status: enabled) { id }
            }
            """,
            new { id = Gid(424242) });

        Assert.Equal(
            "failed to update channel status: ent: channel not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task DeleteChannelSoftDeletesAndForgetsTheLimiter()
    {
        var channel = SeedChannel();
        _limiters.GetOrCreate(
            channel.Id,
            channel.Name,
            new ChannelRateLimitSettings { MaxConcurrent = 1 });
        Assert.True(_limiters.TryGetStats(channel.Id, out _, out _));

        var data = await QueryDataAsync(
            """
            mutation Delete($id: ID!) { deleteChannel(id: $id) }
            """,
            new { id = Gid(channel.Id) });

        Assert.True(data.GetProperty("deleteChannel").GetBoolean());
        Assert.NotEqual(0, channel.DeletedAt);
        Assert.False(_limiters.TryGetStats(channel.Id, out _, out _));
    }

    [Fact]
    public async Task DeleteChannelOnAMissingRowKeepsTheReferenceMessage()
    {
        var errors = await QueryErrorsAsync(
            """
            mutation Delete($id: ID!) { deleteChannel(id: $id) }
            """,
            new { id = Gid(424242) });

        Assert.Equal(
            "failed to delete channel: ent: channel not found",
            errors[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task BulkStatusMutationsUseTheirActionWordsAndRecoverClearsErrors()
    {
        var channel = SeedChannel(status: "disabled", errorMessage: "rate limited");

        var archived = await QueryDataAsync(
            """
            mutation Archive($ids: [ID!]!) { bulkArchiveChannels(ids: $ids) }
            """,
            new { ids = new[] { Gid(channel.Id) } });
        Assert.True(archived.GetProperty("bulkArchiveChannels").GetBoolean());
        Assert.Equal("archived", channel.Status);
        Assert.Equal("rate limited", channel.ErrorMessage);

        await QueryDataAsync(
            """
            mutation Recover($ids: [ID!]!) { bulkRecoverChannels(ids: $ids) }
            """,
            new { ids = new[] { Gid(channel.Id) } });
        Assert.Equal("enabled", channel.Status);
        Assert.Null(channel.ErrorMessage);

        Assert.Equal(
            [(channel.Id, "archived", false, "archive"), (channel.Id, "enabled", true, "recover")],
            _channels.BulkStatusUpdates);

        await QueryDataAsync(
            """
            mutation Enable($ids: [ID!]!) { bulkEnableChannels(ids: $ids) }
            """,
            new { ids = new[] { Gid(channel.Id) } });
        await QueryDataAsync(
            """
            mutation Disable($ids: [ID!]!) { bulkDisableChannels(ids: $ids) }
            """,
            new { ids = new[] { Gid(channel.Id) } });

        Assert.Equal(
            [(channel.Id, "enabled", false, "enable"), (channel.Id, "disabled", false, "disable")],
            _channels.BulkStatusUpdates.Skip(2).ToArray());
    }

    [Fact]
    public async Task BulkStatusMismatchKeepsTheReferenceCountMessage()
    {
        var channel = SeedChannel();

        var errors = await QueryErrorsAsync(
            """
            mutation Archive($ids: [ID!]!) { bulkArchiveChannels(ids: $ids) }
            """,
            new { ids = new[] { Gid(channel.Id), Gid(channel.Id) } });

        Assert.Equal(
            "expected to find 2 channels, but found 1",
            errors[0].GetProperty("message").GetString());

        // The failed check leaves every row untouched.
        Assert.Equal("disabled", channel.Status);
    }

    [Fact]
    public async Task BulkDeleteToleratesMissingIdsAndForgetsEveryLimiter()
    {
        var channel = SeedChannel();
        _limiters.GetOrCreate(
            channel.Id,
            channel.Name,
            new ChannelRateLimitSettings { MaxConcurrent = 1 });

        var data = await QueryDataAsync(
            """
            mutation Delete($ids: [ID!]!) { bulkDeleteChannels(ids: $ids) }
            """,
            new { ids = new[] { Gid(channel.Id), Gid(424242) } });

        Assert.True(data.GetProperty("bulkDeleteChannels").GetBoolean());
        Assert.NotEqual(0, channel.DeletedAt);
        Assert.False(_limiters.TryGetStats(channel.Id, out _, out _));
        Assert.Equal([channel.Id, 424242L], Assert.Single(_channels.BulkDeletes));
    }

    [Fact]
    public async Task BulkTagMutationsNormalizeAndDeduplicateTheirInput()
    {
        var channel = SeedChannel(tags: """["keep"]""");

        var added = await QueryDataAsync(
            """
            mutation Add($ids: [ID!]!, $tags: [String!]!) {
              bulkAddChannelTags(ids: $ids, tags: $tags)
            }
            """,
            new { ids = new[] { Gid(channel.Id), Gid(channel.Id) }, tags = NormalizedTagInput });
        Assert.True(added.GetProperty("bulkAddChannelTags").GetBoolean());

        var addCall = Assert.Single(_channels.TagCalls);
        Assert.Equal([channel.Id], addCall.Ids);
        Assert.Equal(["a", "b"], addCall.AddTags);
        Assert.Empty(addCall.RemoveTags);
        Assert.Equal(["keep", "a", "b"], channel.TagsList());

        await QueryDataAsync(
            """
            mutation Remove($ids: [ID!]!, $tags: [String!]!) {
              bulkRemoveChannelTags(ids: $ids, tags: $tags)
            }
            """,
            new { ids = new[] { Gid(channel.Id) }, tags = WhitespaceTagInput });

        var removeCall = _channels.TagCalls[^1];
        Assert.Equal(["b"], removeCall.RemoveTags);
        Assert.Empty(removeCall.AddTags);
        Assert.Equal(["keep", "a"], channel.TagsList());
    }

    [Fact]
    public async Task BulkManageChannelTagsNoOpsOnEmptyIdsOrEmptyTagLists()
    {
        var channel = SeedChannel(tags: """["keep"]""");

        await QueryDataAsync(
            """
            mutation Manage($ids: [ID!]!, $add: [String!]!, $remove: [String!]!) {
              bulkManageChannelTags(ids: $ids, addTags: $add, removeTags: $remove)
            }
            """,
            new { ids = Array.Empty<string>(), add = SingleTagA, remove = Array.Empty<string>() });

        await QueryDataAsync(
            """
            mutation Manage($ids: [ID!]!, $add: [String!]!, $remove: [String!]!) {
              bulkManageChannelTags(ids: $ids, addTags: $add, removeTags: $remove)
            }
            """,
            new { ids = new[] { Gid(channel.Id) }, add = BlankTagInput, remove = Array.Empty<string>() });

        Assert.Empty(_channels.TagCalls);
        Assert.Equal(["keep"], channel.TagsList());
    }

    [Fact]
    public async Task BulkManageChannelTagsDelegatesBothLists()
    {
        var channel = SeedChannel(tags: """["old","keep"]""");

        var data = await QueryDataAsync(
            """
            mutation Manage($ids: [ID!]!, $add: [String!]!, $remove: [String!]!) {
              bulkManageChannelTags(ids: $ids, addTags: $add, removeTags: $remove)
            }
            """,
            new
            {
                ids = new[] { Gid(channel.Id) },
                add = DuplicatedNewTagInput,
                remove = OldTagInput,
            });

        Assert.True(data.GetProperty("bulkManageChannelTags").GetBoolean());
        var call = Assert.Single(_channels.TagCalls);
        Assert.Equal(["new"], call.AddTags);
        Assert.Equal(["old"], call.RemoveTags);
        Assert.Equal(["keep", "new"], channel.TagsList());
    }

    [Fact]
    public async Task LifecycleFailuresKeepTheReferencePrefixes()
    {
        _channels.StatusFailure = new InvalidOperationException("boom");
        _channels.DeleteFailure = new InvalidOperationException("boom");
        _channels.BulkStatusFailure =
            new StoreFailureException("failed to archive channels: boom", new InvalidOperationException("boom"));
        _channels.ManageTagsFailure =
            new StoreFailureException("failed to manage tags for channel 1: boom", new InvalidOperationException("boom"));

        var update = await QueryErrorsAsync(
            """
            mutation Update($id: ID!) { updateChannelStatus(id: $id, status: enabled) { id } }
            """,
            new { id = Gid(1) });
        Assert.Equal(
            "failed to update channel status: boom",
            update[0].GetProperty("message").GetString());

        var delete = await QueryErrorsAsync(
            """
            mutation Delete($id: ID!) { deleteChannel(id: $id) }
            """,
            new { id = Gid(1) });
        Assert.Equal("failed to delete channel: boom", delete[0].GetProperty("message").GetString());

        var archive = await QueryErrorsAsync(
            """
            mutation Archive($ids: [ID!]!) { bulkArchiveChannels(ids: $ids) }
            """,
            new { ids = new[] { Gid(1) } });
        Assert.Equal(
            "failed to archive channels: boom",
            archive[0].GetProperty("message").GetString());

        var tags = await QueryErrorsAsync(
            """
            mutation Add($ids: [ID!]!, $tags: [String!]!) {
              bulkAddChannelTags(ids: $ids, tags: $tags)
            }
            """,
            new { ids = new[] { Gid(1) }, tags = SingleTagA });
        Assert.Equal(
            "failed to manage tags for channel 1: boom",
            tags[0].GetProperty("message").GetString());
    }

    [Fact]
    public async Task EmptyBulkStatusAndDeleteCallsReturnTrueWithoutTouchingTheStore()
    {
        var archive = await QueryDataAsync(
            """
            mutation Archive($ids: [ID!]!) { bulkArchiveChannels(ids: $ids) }
            """,
            new { ids = Array.Empty<string>() });
        Assert.True(archive.GetProperty("bulkArchiveChannels").GetBoolean());

        var delete = await QueryDataAsync(
            """
            mutation Delete($ids: [ID!]!) { bulkDeleteChannels(ids: $ids) }
            """,
            new { ids = Array.Empty<string>() });
        Assert.True(delete.GetProperty("bulkDeleteChannels").GetBoolean());

        Assert.Empty(_channels.BulkStatusUpdates);
        Assert.Empty(_channels.BulkDeletes);
    }

    [Fact]
    public async Task IntrospectionExposesTheChannelLifecycleMutations()
    {
        var data = await QueryDataAsync(
            """
            {
              __schema {
                mutationType {
                  fields { name args { name type { kind ofType { name } } } }
                }
              }
            }
            """);

        var fields = data.GetProperty("__schema").GetProperty("mutationType")
            .GetProperty("fields").EnumerateArray().ToArray();
        var names = fields.Select(static field => field.GetProperty("name").GetString()!).ToArray();
        Assert.Contains("updateChannelStatus", names);
        Assert.Contains("deleteChannel", names);
        Assert.Contains("bulkArchiveChannels", names);
        Assert.Contains("bulkDisableChannels", names);
        Assert.Contains("bulkEnableChannels", names);
        Assert.Contains("bulkRecoverChannels", names);
        Assert.Contains("bulkDeleteChannels", names);
        Assert.Contains("bulkAddChannelTags", names);
        Assert.Contains("bulkRemoveChannelTags", names);
        Assert.Contains("bulkManageChannelTags", names);

        var update = fields.Single(static field => field.GetProperty("name").GetString() == "updateChannelStatus");
        var args = update.GetProperty("args").EnumerateArray().ToArray();
        var status = Assert.Single(args, static arg => arg.GetProperty("name").GetString() == "status");
        Assert.Equal("NON_NULL", status.GetProperty("type").GetProperty("kind").GetString());
        Assert.Equal("ChannelStatus", status.GetProperty("type").GetProperty("ofType").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ChannelMutationsDenyMembersWithoutWriteChannels()
    {
        var channel = SeedChannel();
        var member = await SeedMemberAsync(
            "reader@example.com",
            scopes: """["read_channels"]""");
        var token = _gate.IssueJwt(member);

        var update = await QueryErrorsAsync(
            """
            mutation Update($id: ID!) {
              updateChannelStatus(id: $id, status: archived) { id status }
            }
            """,
            new { id = Gid(channel.Id) },
            token);
        Assert.Equal("permission denied", update[0].GetProperty("message").GetString());

        var delete = await QueryErrorsAsync(
            "mutation Delete($id: ID!) { deleteChannel(id: $id) }",
            new { id = Gid(channel.Id) },
            token);
        Assert.Equal("permission denied", delete[0].GetProperty("message").GetString());

        var archive = await QueryErrorsAsync(
            "mutation Archive($ids: [ID!]!) { bulkArchiveChannels(ids: $ids) }",
            new { ids = new[] { Gid(channel.Id) } },
            token);
        Assert.Equal("permission denied", archive[0].GetProperty("message").GetString());

        var tags = await QueryErrorsAsync(
            """
            mutation Tags($ids: [ID!]!, $tags: [String!]!) {
              bulkAddChannelTags(ids: $ids, tags: $tags)
            }
            """,
            new { ids = new[] { Gid(channel.Id) }, tags = SingleTagA },
            token);
        Assert.Equal("permission denied", tags[0].GetProperty("message").GetString());

        var endpoints = await QueryErrorsAsync(
            """
            mutation Save($input: SaveChannelEndpointsInput!) {
              saveChannelEndpoints(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    channelID = Gid(channel.Id),
                    endpoints = Array.Empty<object>(),
                },
            },
            token);
        Assert.Equal("permission denied", endpoints[0].GetProperty("message").GetString());

        var updateChannel = await QueryErrorsAsync(
            """
            mutation UpdateChannel($id: ID!, $input: UpdateChannelInput!) {
              updateChannel(id: $id, input: $input) { id }
            }
            """,
            new { id = Gid(channel.Id), input = new { name = "Renamed" } },
            token);
        Assert.Equal(
            "permission denied",
            updateChannel[0].GetProperty("message").GetString());

        var create = await QueryErrorsAsync(
            """
            mutation Create($input: CreateChannelInput!) {
              createChannel(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    type = "openai",
                    name = "Unauthorized",
                    credentials = new { apiKey = "sk-na" },
                    supportedModels = Array.Empty<string>(),
                    defaultTestModel = string.Empty,
                },
            },
            token);
        Assert.Equal("permission denied", create[0].GetProperty("message").GetString());

        // The gate runs before every service call, so nothing was written.
        Assert.Equal("disabled", channel.Status);
        Assert.Equal(0, channel.DeletedAt);
        Assert.Empty(_channels.StatusUpdates);
        Assert.Empty(_channels.BulkStatusUpdates);
        Assert.Empty(_channels.TagCalls);
        Assert.Empty(_channels.SaveEndpointsCalls);
        Assert.Empty(_channels.UpdateCalls);
        Assert.Empty(_channels.CreatedChannels);
    }

    [Fact]
    public async Task ChannelMutationsAllowMembersHoldingWriteChannels()
    {
        var channel = SeedChannel();
        var member = await SeedMemberAsync(
            "writer@example.com",
            scopes: """["read_channels","write_channels"]""");

        var data = await QueryDataAsync(
            """
            mutation Update($id: ID!) {
              updateChannelStatus(id: $id, status: enabled) { id status }
            }
            """,
            new { id = Gid(channel.Id) },
            token: _gate.IssueJwt(member));

        Assert.Equal(
            "enabled",
            data.GetProperty("updateChannelStatus").GetProperty("status").GetString());
        Assert.Equal((channel.Id, "enabled"), Assert.Single(_channels.StatusUpdates));
    }

    [Fact]
    public async Task WriteOnlyMembersAreDeniedByTheInternalChannelReads()
    {
        var channel = SeedChannel();
        var member = await SeedMemberAsync(
            "write-only@example.com",
            scopes: """["write_channels"]""");
        var token = _gate.IssueJwt(member);

        // Every write below passes the resolver's write_channels mutation gate;
        // the deny comes from the Channel query policy that ent evaluates on the
        // service's internal reads (C014): the name/duplicate lookups, the xai
        // Exist query, the credential/settings reloads in update, and the bulk
        // and API-key reads.
        var create = await QueryErrorsAsync(
            """
            mutation Create($input: CreateChannelInput!) {
              createChannel(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    type = "openai",
                    name = "Write-only create",
                    credentials = new { apiKey = "sk-wo" },
                    supportedModels = Array.Empty<string>(),
                    defaultTestModel = string.Empty,
                },
            },
            token);
        Assert.Equal("permission denied", create[0].GetProperty("message").GetString());

        var updateChannel = """
            mutation UpdateChannel($id: ID!, $input: UpdateChannelInput!) {
              updateChannel(id: $id, input: $input) { id }
            }
            """;

        var nameUpdate = await QueryErrorsAsync(
            updateChannel,
            new { id = Gid(channel.Id), input = new { name = "Renamed" } },
            token);
        Assert.Equal("permission denied", nameUpdate[0].GetProperty("message").GetString());

        var baseUrlUpdate = await QueryErrorsAsync(
            updateChannel,
            new { id = Gid(channel.Id), input = new { baseURL = "https://example.test/v1" } },
            token);
        Assert.Equal("permission denied", baseUrlUpdate[0].GetProperty("message").GetString());

        var credentialUpdate = await QueryErrorsAsync(
            updateChannel,
            new { id = Gid(channel.Id), input = new { credentials = new { apiKey = "sk-new" } } },
            token);
        Assert.Equal(
            "permission denied",
            credentialUpdate[0].GetProperty("message").GetString());

        var bulkStatus = await QueryErrorsAsync(
            "mutation Archive($ids: [ID!]!) { bulkArchiveChannels(ids: $ids) }",
            new { ids = new[] { Gid(channel.Id) } },
            token);
        Assert.Equal("permission denied", bulkStatus[0].GetProperty("message").GetString());

        var tags = await QueryErrorsAsync(
            """
            mutation Tags($ids: [ID!]!, $tags: [String!]!) {
              bulkAddChannelTags(ids: $ids, tags: $tags)
            }
            """,
            new { ids = new[] { Gid(channel.Id) }, tags = SingleTagA },
            token);
        Assert.Equal("permission denied", tags[0].GetProperty("message").GetString());

        var autoDisable = await QueryErrorsAsync(
            """
            mutation AutoDisable($input: BulkUpdateChannelAutoDisableInput!) {
              bulkUpdateChannelAutoDisable(input: $input) { success }
            }
            """,
            new
            {
                input = new
                {
                    channelIDs = new[] { Gid(channel.Id) },
                    action = "off",
                },
            },
            token);
        Assert.Equal("permission denied", autoDisable[0].GetProperty("message").GetString());

        var endpoints = await QueryErrorsAsync(
            """
            mutation Save($input: SaveChannelEndpointsInput!) {
              saveChannelEndpoints(input: $input) { id }
            }
            """,
            new
            {
                input = new
                {
                    channelID = Gid(channel.Id),
                    endpoints = Array.Empty<object>(),
                },
            },
            token);
        Assert.Equal("permission denied", endpoints[0].GetProperty("message").GetString());

        var disableKey = await QueryErrorsAsync(
            """
            mutation Disable($channelID: ID!, $key: String!) {
              disableChannelAPIKey(channelID: $channelID, key: $key)
            }
            """,
            new { channelID = Gid(channel.Id), key = "sk-1" },
            token);
        Assert.Equal("permission denied", disableKey[0].GetProperty("message").GetString());

        // The internal reads are denied before any store write runs.
        Assert.Empty(_channels.CreatedChannels);
        Assert.Empty(_channels.NameChecks);
        Assert.Empty(_channels.UpdateCalls);
        Assert.Empty(_channels.BulkStatusUpdates);
        Assert.Empty(_channels.TagCalls);
        Assert.Empty(_channels.AutoDisableCalls);
        Assert.Empty(_channels.SaveEndpointsCalls);
        Assert.Empty(_channels.ApiKeyStateCalls);

        // The reads the reference wraps in authz.RunWithScopeDecision
        // (write_channels) are served from the entry snapshot, so a write-only
        // member still updates remark and the supported models.
        var remarkUpdate = await QueryDataAsync(
            """
            mutation UpdateChannel($id: ID!, $input: UpdateChannelInput!) {
              updateChannel(id: $id, input: $input) { id remark }
            }
            """,
            new { id = Gid(channel.Id), input = new { remark = "write-only note" } },
            token);
        Assert.Equal(
            "write-only note",
            remarkUpdate.GetProperty("updateChannel").GetProperty("remark").GetString());

        var modelsUpdate = await QueryDataAsync(
            """
            mutation UpdateChannel($id: ID!, $input: UpdateChannelInput!) {
              updateChannel(id: $id, input: $input) { id supportedModels }
            }
            """,
            new
            {
                id = Gid(channel.Id),
                input = new { supportedModels = new[] { "gpt-4o", "gpt-4o-mini" } },
            },
            token);
        Assert.Equal(
            new[] { "gpt-4o", "gpt-4o-mini" },
            modelsUpdate
                .GetProperty("updateChannel")
                .GetProperty("supportedModels")
                .EnumerateArray()
                .Select(static model => model.GetString())
                .ToArray());
        Assert.Equal(2, _channels.UpdateCalls.Count);
    }

    [Fact]
    public async Task ChannelMutationsAreRejectedForApiKeysBeforeThePolicy()
    {
        var channel = SeedChannel();
        _gate.AddServiceAccountKey(
            configure: key => key.Scopes = """["read_channels","write_channels"]""");

        // The admin surface's apiKeyReadOnly middleware rejects every mutation
        // for service-account keys, so the mutation policy's "no user in
        // context" deny is unreachable from /admin/graphql (C014).
        var errors = await QueryErrorsAsync(
            """
            mutation Update($id: ID!) {
              updateChannelStatus(id: $id, status: enabled) { id }
            }
            """,
            new { id = Gid(channel.Id) },
            token: "sk-service-account");

        Assert.Equal(
            "service account API keys are read-only",
            errors[0].GetProperty("message").GetString());
        Assert.Empty(_channels.StatusUpdates);
        Assert.Equal("disabled", channel.Status);
    }

    private static string Gid(long id) => "gid://axonhub/Channel/" + id;

    private ChannelRecord SeedChannel(
        string status = "disabled",
        string? errorMessage = null,
        DateTime? autoDisabledAt = null,
        DateTime? autoDisableExpiresAt = null,
        string? tags = null)
    {
        var record = new ChannelRecord
        {
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            Type = "openai",
            Name = "Channel " + Guid.NewGuid().ToString("N"),
            Status = status,
            Credentials = "{}",
            SupportedModels = "null",
            AutoSyncModelPattern = "",
            DefaultTestModel = "",
            ErrorMessage = errorMessage,
            AutoDisabledAt = autoDisabledAt,
            AutoDisableExpiresAt = autoDisableExpiresAt,
            Tags = tags,
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
        Assert.False(root.TryGetProperty("errors", out _), root.ToString());
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
        if (token is not null)
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Seeds a non-owner management user (id 77) in both the JWT store and the
    /// scopes store the privacy resolver reads.
    /// </summary>
    private async Task<UserRecord> SeedMemberAsync(string email, string? scopes)
    {
        var member = _gate.Users.Add(user =>
        {
            user.Id = 77;
            user.Email = email;
            user.Status = "activated";
            user.IsOwner = false;
        });

        await _gate.UserAdmins.CreateAsync(
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
}

/// <summary>
/// In-memory IChannelWriteStore mirroring the reference's checks: the bulk
/// status path compares the live row count to the id count, the tag path
/// applies remove-then-add, and every failure mode is injectable.
/// </summary>
internal sealed class FakeChannelWriteStore : IChannelWriteStore
{
    private readonly List<ChannelRecord> _rows = [];
    private long _nextId = 1;

    public List<(long Id, string Status)> StatusUpdates { get; } = [];

    public List<(long Id, string Status, bool ClearErrorMessage, string Action)> BulkStatusUpdates { get; } = [];

    public List<long[]> BulkDeletes { get; } = [];

    public List<(long[] Ids, string[] AddTags, string[] RemoveTags)> TagCalls { get; } = [];

    public List<(long Id, int OrderingWeight)[]> OrderingCalls { get; } = [];

    public List<(long[] Ids, string Mode, string? RulesJson)> AutoDisableCalls { get; } = [];

    public List<ChannelRecord> CreatedChannels { get; } = [];

    /// <summary>
    /// The live rows, exposed so the quota routing migration tests can build an
    /// <see cref="IChannelStore"/> read seam over the same table this store
    /// mutates.
    /// </summary>
    public IReadOnlyList<ChannelRecord> LiveRows => _rows;

    public List<(long Id, ChannelUpdateRecord Update)> UpdateCalls { get; } = [];

    public List<(long Id, string EndpointsJson, string? SettingsJson)> SaveEndpointsCalls { get; } = [];

    public List<ChannelApiKeyStateRecord> ApiKeyStateCalls { get; } = [];

    public List<(long Id, string Name)> NameChecks { get; } = [];

    public List<(long SourceId, string Name)> DuplicateCalls { get; } = [];

    public List<string> BulkCreateCalls { get; } = [];

    public List<long> ImportCreateCalls { get; } = [];

    /// <summary>Simulates a concurrent writer invalidating the version guard.</summary>
    public bool UpdateConflict { get; set; }

    /// <summary>
    /// Runs inside <see cref="UpdateAsync"/> after the row lookup but before
    /// the version guard, so a test can commit an interleaved write and make
    /// the merge loop re-read the new snapshot.
    /// </summary>
    public Action<ChannelRecord>? BeforeUpdate { get; set; }

    /// <summary>Counts every <see cref="UpdateAsync"/> call, conflicts included.</summary>
    public int UpdateAttempts { get; private set; }

    /// <summary>Simulates a concurrent writer invalidating the endpoint save guard.</summary>
    public bool SaveEndpointsConflict { get; set; }

    public Exception? GetForUpdateFailure { get; set; }

    public Exception? GetFailure { get; set; }

    /// <summary>Per-id read failures for the credential cleanup isolation test.</summary>
    public Dictionary<long, Exception> GetFailuresById { get; } = [];

    public Exception? GetSettingsVersionFailure { get; set; }

    public Exception? SaveEndpointsFailure { get; set; }

    public Exception? ApiKeyStateFailure { get; set; }

    /// <summary>Simulates the row disappearing between the read and the write.</summary>
    public bool ApiKeyStateRowMissing { get; set; }

    /// <summary>Fails the expired-credential cleanup's candidate query.</summary>
    public Exception? CleanupQueryFailure { get; set; }

    /// <summary>Fails the expired auto-disable candidate query.</summary>
    public Exception? ExpiredAutoDisableQueryFailure { get; set; }

    /// <summary>Fails every conditional auto-disable recovery.</summary>
    public Exception? ExpiredAutoDisableRecoverFailure { get; set; }

    public List<(long Id, DateTime Now)> ExpiredAutoDisableRecoveries { get; } = [];

    public Exception? NameCheckFailure { get; set; }

    public Exception? UpdateFailure { get; set; }

    /// <summary>
    /// Per-id update failures, mirroring the quota routing migration test's
    /// beforeChannelUpdate hook (fail one channel without failing the earlier
    /// successful writes).
    /// </summary>
    public Dictionary<long, Exception> UpdateFailuresById { get; } = [];

    public Exception? CreateFailure { get; set; }

    public Exception? BulkCreateFailure { get; set; }

    public Exception? ImportCreateFailure { get; set; }

    /// <summary>Simulates a duplicate live name at the import insert.</summary>
    public bool ImportNameConflict { get; set; }

    public Exception? StatusFailure { get; set; }

    public Exception? DeleteFailure { get; set; }

    public Exception? BulkStatusFailure { get; set; }

    public Exception? BulkDeleteFailure { get; set; }

    public Exception? ManageTagsFailure { get; set; }

    public Exception? BulkOrderingFailure { get; set; }

    public Exception? BulkAutoDisableFailure { get; set; }

    public void Seed(ChannelRecord record)
    {
        record.Id = _nextId++;
        _rows.Add(record);
    }

    public Task<ChannelRecord?> GetForUpdateAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        if (GetForUpdateFailure is not null)
        {
            throw GetForUpdateFailure;
        }

        // ChannelWriteStore loads the pre-update identity with AsNoTracking, so
        // the caller's snapshot is detached from the rows UpdateAsync mutates.
        // Handing out the live row here would erase the identity comparison in
        // ChannelLifecycleService.UpdateAsync before it runs.
        var row = _rows.SingleOrDefault(candidate =>
            candidate.Id == id && candidate.DeletedAt == 0);
        return Task.FromResult(row is null ? null : Detach(row));
    }

    private static ChannelRecord Detach(ChannelRecord row) => new()
    {
        Id = row.Id,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        DeletedAt = row.DeletedAt,
        Type = row.Type,
        BaseUrl = row.BaseUrl,
        Name = row.Name,
        Status = row.Status,
        Credentials = row.Credentials,
        DisabledApiKeys = row.DisabledApiKeys,
        SupportedModels = row.SupportedModels,
        ManualModels = row.ManualModels,
        AutoSyncSupportedModels = row.AutoSyncSupportedModels,
        AutoSyncModelPattern = row.AutoSyncModelPattern,
        Tags = row.Tags,
        DefaultTestModel = row.DefaultTestModel,
        Policies = row.Policies,
        Settings = row.Settings,
        OrderingWeight = row.OrderingWeight,
        ErrorMessage = row.ErrorMessage,
        AutoDisabledAt = row.AutoDisabledAt,
        AutoDisableExpiresAt = row.AutoDisableExpiresAt,
        Remark = row.Remark,
        Endpoints = row.Endpoints,
    };

    public Task<ChannelRecord?> GetAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        if (GetFailure is not null)
        {
            throw GetFailure;
        }

        if (GetFailuresById.TryGetValue(id, out var failure))
        {
            throw failure;
        }

        return Task.FromResult(
            _rows.SingleOrDefault(row => row.Id == id && row.DeletedAt == 0));
    }

    public Task<ChannelSettingsVersion?> GetSettingsVersionAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        if (GetSettingsVersionFailure is not null)
        {
            throw GetSettingsVersionFailure;
        }

        var row = _rows.SingleOrDefault(candidate =>
            candidate.Id == id && candidate.DeletedAt == 0);
        return Task.FromResult(
            row is null
                ? null
                : new ChannelSettingsVersion(row.Settings, row.UpdatedAt));
    }

    public Task<bool> NameExistsAsync(
        long id,
        string name,
        CancellationToken cancellationToken = default)
    {
        if (NameCheckFailure is not null)
        {
            throw NameCheckFailure;
        }

        NameChecks.Add((id, name));
        return Task.FromResult(
            _rows.Any(row => row.DeletedAt == 0 && row.Id != id && row.Name == name));
    }

    public async Task<ChannelRecord?> UpdateAsync(
        ChannelRecord snapshot,
        ChannelUpdateRecord update,
        Func<long, CancellationToken, Task>? afterUpdate = null,
        CancellationToken cancellationToken = default)
    {
        UpdateAttempts++;

        if (UpdateFailure is not null)
        {
            throw UpdateFailure;
        }

        if (UpdateFailuresById.TryGetValue(snapshot.Id, out var failure))
        {
            throw failure;
        }

        if (UpdateConflict)
        {
            return null;
        }

        var row = _rows.SingleOrDefault(candidate =>
            candidate.Id == snapshot.Id && candidate.DeletedAt == 0);
        if (row is null)
        {
            return null;
        }

        BeforeUpdate?.Invoke(row);

        if (update.ExpectedUpdatedAt is { } expected && row.UpdatedAt != expected)
        {
            return null;
        }

        UpdateCalls.Add((snapshot.Id, update));

        row.Type = update.Type ?? row.Type;
        row.BaseUrl = update.BaseUrl ?? row.BaseUrl;
        row.Name = update.Name ?? row.Name;
        row.DefaultTestModel = update.DefaultTestModel ?? row.DefaultTestModel;
        row.OrderingWeight = update.OrderingWeight ?? row.OrderingWeight;
        row.AutoSyncSupportedModels =
            update.AutoSyncSupportedModels ?? row.AutoSyncSupportedModels;
        row.SupportedModels = update.SupportedModelsJson ?? row.SupportedModels;
        row.ManualModels = update.ManualModelsJson ?? row.ManualModels;
        row.Tags = update.TagsJson ?? row.Tags;
        row.Settings = update.SettingsJson ?? row.Settings;
        row.Policies = update.PoliciesJson ?? row.Policies;
        row.Credentials = update.CredentialsJson ?? row.Credentials;
        row.Remark = update.ClearRemark ? null : update.Remark ?? row.Remark;
        row.AutoSyncModelPattern = update.ClearAutoSyncModelPattern
            ? null
            : update.AutoSyncModelPattern ?? row.AutoSyncModelPattern;
        row.Endpoints = update.EndpointsJson ?? row.Endpoints;
        if (update.ClearErrorMessage)
        {
            row.ErrorMessage = null;
        }

        row.UpdatedAt = DateTime.UtcNow;

        if (afterUpdate is not null)
        {
            await afterUpdate(row.Id, cancellationToken);
        }

        return row;
    }

    public Task<ChannelRecord?> SaveEndpointsAsync(
        ChannelRecord snapshot,
        string endpointsJson,
        string? settingsJson,
        CancellationToken cancellationToken = default)
    {
        if (SaveEndpointsFailure is not null)
        {
            throw SaveEndpointsFailure;
        }

        if (SaveEndpointsConflict)
        {
            return Task.FromResult<ChannelRecord?>(null);
        }

        var row = _rows.SingleOrDefault(candidate =>
            candidate.Id == snapshot.Id
            && candidate.DeletedAt == 0
            && candidate.UpdatedAt == snapshot.UpdatedAt);
        if (row is null)
        {
            return Task.FromResult<ChannelRecord?>(null);
        }

        SaveEndpointsCalls.Add((row.Id, endpointsJson, settingsJson));
        row.Endpoints = endpointsJson;
        row.Settings = settingsJson;
        row.UpdatedAt = DateTime.UtcNow;

        // ent returns the saved entity; the port projects the loaded snapshot.
        snapshot.Endpoints = endpointsJson;
        snapshot.Settings = settingsJson;
        snapshot.UpdatedAt = row.UpdatedAt;
        return Task.FromResult<ChannelRecord?>(snapshot);
    }

    public Task<bool> UpdateApiKeyStateAsync(
        ChannelApiKeyStateRecord state,
        CancellationToken cancellationToken = default)
    {
        if (ApiKeyStateFailure is not null)
        {
            throw ApiKeyStateFailure;
        }

        var row = _rows.SingleOrDefault(candidate =>
            candidate.Id == state.Id && candidate.DeletedAt == 0);
        if (row is null || ApiKeyStateRowMissing)
        {
            return Task.FromResult(false);
        }

        ApiKeyStateCalls.Add(state);
        row.DisabledApiKeys = state.DisabledApiKeysJson;
        row.Credentials = state.CredentialsJson ?? row.Credentials;
        row.Status = state.Status ?? row.Status;
        if (state.ClearErrorMessage)
        {
            row.ErrorMessage = null;
        }
        else if (state.ErrorMessage is not null)
        {
            row.ErrorMessage = state.ErrorMessage;
        }

        if (state.ClearAutoDisabledAt)
        {
            row.AutoDisabledAt = null;
        }
        else if (state.AutoDisabledAt is not null)
        {
            row.AutoDisabledAt = state.AutoDisabledAt;
        }

        if (state.ClearAutoDisableExpiresAt)
        {
            row.AutoDisableExpiresAt = null;
        }

        row.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<long>> ListCredentialCleanupChannelIdsAsync(
        CancellationToken cancellationToken = default)
    {
        if (CleanupQueryFailure is not null)
        {
            throw CleanupQueryFailure;
        }

        return Task.FromResult<IReadOnlyList<long>>(
            _rows.Where(row => row.DeletedAt == 0
                    && (row.Status == "enabled" || row.Status == "disabled"))
                .OrderBy(row => row.Id)
                .Select(row => row.Id)
                .ToList());
    }

    public Task<IReadOnlyList<long>> ListExpiredAutoDisabledChannelIdsAsync(
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        if (ExpiredAutoDisableQueryFailure is not null)
        {
            throw ExpiredAutoDisableQueryFailure;
        }

        return Task.FromResult<IReadOnlyList<long>>(
            _rows.Where(row => row.DeletedAt == 0
                    && row.Status == "disabled"
                    && row.AutoDisabledAt is not null
                    && row.AutoDisableExpiresAt is not null
                    && row.AutoDisableExpiresAt <= now)
                .OrderBy(row => row.Id)
                .Select(row => row.Id)
                .ToList());
    }

    public Task<bool> RecoverExpiredAutoDisabledChannelAsync(
        long id,
        DateTime now,
        CancellationToken cancellationToken = default)
    {
        if (ExpiredAutoDisableRecoverFailure is not null)
        {
            throw ExpiredAutoDisableRecoverFailure;
        }

        var row = _rows.SingleOrDefault(candidate =>
            candidate.Id == id
            && candidate.DeletedAt == 0
            && candidate.Status == "disabled"
            && candidate.AutoDisabledAt is not null
            && candidate.AutoDisableExpiresAt is not null
            && candidate.AutoDisableExpiresAt <= now);
        if (row is null)
        {
            return Task.FromResult(false);
        }

        ExpiredAutoDisableRecoveries.Add((id, now));
        row.Status = "enabled";
        row.ErrorMessage = null;
        row.AutoDisabledAt = null;
        row.AutoDisableExpiresAt = null;
        row.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(true);
    }

    public List<(long Id, string Reason, DateTime AutoDisabledAt, DateTime? ExpiresAt)> ChannelAutoDisableCalls { get; } = [];

    public Exception? AutoDisableFailure { get; set; }

    public Task<bool> AutoDisableAsync(
        long id,
        string reason,
        DateTime autoDisabledAt,
        DateTime? expiresAt,
        CancellationToken cancellationToken = default)
    {
        if (AutoDisableFailure is not null)
        {
            throw AutoDisableFailure;
        }

        var row = _rows.SingleOrDefault(candidate =>
            candidate.Id == id && candidate.Status == "enabled" && candidate.DeletedAt == 0);
        if (row is null)
        {
            return Task.FromResult(false);
        }

        ChannelAutoDisableCalls.Add((id, reason, autoDisabledAt, expiresAt));
        row.Status = "disabled";
        row.ErrorMessage = reason;
        row.AutoDisabledAt = autoDisabledAt;
        row.AutoDisableExpiresAt = expiresAt;
        row.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(true);
    }

    public async Task<ChannelRecord?> CreateAsync(
        ChannelRecord channel,
        Func<long, CancellationToken, Task>? afterInsert = null,
        CancellationToken cancellationToken = default)
    {
        if (CreateFailure is not null)
        {
            throw CreateFailure;
        }

        // ent's name check honours the SoftDeleteMixin predicate: only a live
        // row can own the name.
        if (_rows.Any(row => row.DeletedAt == 0 && row.Name == channel.Name))
        {
            return null;
        }

        channel.Id = _nextId++;
        _rows.Add(channel);
        CreatedChannels.Add(channel);

        if (afterInsert is not null)
        {
            try
            {
                await afterInsert(channel.Id, cancellationToken);
            }
            catch
            {
                // The real transaction rolls the insert back.
                _rows.Remove(channel);
                CreatedChannels.Remove(channel);
                throw;
            }
        }

        return channel;
    }

    public Task<IReadOnlyList<string>> ListLiveNamesAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(
            _rows.Where(row => row.DeletedAt == 0)
                .Select(row => row.Name)
                .ToList());

    public async Task<ChannelRecord?> DuplicateAsync(
        long sourceId,
        string name,
        Action<ChannelRecord> inherit,
        Func<ChannelRecord> buildRecord,
        Func<long, CancellationToken, Task>? afterInsert = null,
        CancellationToken cancellationToken = default)
    {
        if (GetFailure is not null)
        {
            throw new StoreFailureException(
                "failed to get source channel: " + GetFailure.Message,
                GetFailure);
        }

        var source = _rows.SingleOrDefault(row => row.Id == sourceId && row.DeletedAt == 0)
            ?? throw new StoreFailureException(
                "failed to get source channel: ent: channel not found",
                new InvalidOperationException("ent: channel not found"));

        DuplicateCalls.Add((sourceId, name));
        inherit(source);

        if (NameCheckFailure is not null)
        {
            throw new StoreFailureException(
                "failed to check channel name: " + NameCheckFailure.Message,
                NameCheckFailure);
        }

        if (_rows.Any(row => row.DeletedAt == 0 && row.Name == name))
        {
            return null;
        }

        var channel = buildRecord();
        if (CreateFailure is not null)
        {
            throw new StoreFailureException(
                "failed to create channel: " + CreateFailure.Message,
                CreateFailure);
        }

        channel.Id = _nextId++;
        _rows.Add(channel);
        CreatedChannels.Add(channel);

        if (afterInsert is not null)
        {
            try
            {
                await afterInsert(channel.Id, cancellationToken);
            }
            catch
            {
                _rows.Remove(channel);
                CreatedChannels.Remove(channel);
                throw;
            }
        }

        return channel;
    }

    public async Task<IReadOnlyList<ChannelRecord>> CreateManyAsync(
        IReadOnlyList<Func<ChannelRecord>> recordFactories,
        Func<CancellationToken, Task>? beforeInsert = null,
        Func<ChannelRecord, CancellationToken, Task>? afterEachInsert = null,
        CancellationToken cancellationToken = default)
    {
        if (BulkCreateFailure is not null)
        {
            throw BulkCreateFailure;
        }

        if (beforeInsert is not null)
        {
            await beforeInsert(cancellationToken);
        }

        var created = new List<ChannelRecord>(recordFactories.Count);
        try
        {
            foreach (var factory in recordFactories)
            {
                var channel = factory();
                BulkCreateCalls.Add(channel.Name);

                if (CreateFailure is not null)
                {
                    throw new StoreFailureException(
                        "failed to create channel '" + channel.Name + "': "
                            + "failed to create channel: " + CreateFailure.Message,
                        CreateFailure);
                }

                channel.Id = _nextId++;
                _rows.Add(channel);
                CreatedChannels.Add(channel);
                created.Add(channel);

                if (afterEachInsert is not null)
                {
                    await afterEachInsert(channel, cancellationToken);
                }
            }
        }
        catch
        {
            // The real transaction rolls the whole batch back.
            foreach (var channel in created)
            {
                _rows.Remove(channel);
                CreatedChannels.Remove(channel);
            }

            throw;
        }

        return created;
    }

    public async Task<ChannelRecord> ImportCreateAsync(
        ChannelRecord channel,
        Func<long, CancellationToken, Task>? afterInsert = null,
        CancellationToken cancellationToken = default)
    {
        if (ImportCreateFailure is not null)
        {
            throw new StoreFailureException(ImportCreateFailure.Message, ImportCreateFailure);
        }

        if (ImportNameConflict && _rows.Any(row => row.DeletedAt == 0 && row.Name == channel.Name))
        {
            throw new StoreFailureException(
                "ent: constraint failed: duplicate channel name",
                new InvalidOperationException("duplicate channel name"));
        }

        ImportCreateCalls.Add(0);
        channel.Id = _nextId++;
        _rows.Add(channel);
        CreatedChannels.Add(channel);

        if (afterInsert is not null)
        {
            try
            {
                await afterInsert(channel.Id, cancellationToken);
            }
            catch
            {
                _rows.Remove(channel);
                CreatedChannels.Remove(channel);
                throw;
            }
        }

        return channel;
    }

    public Task<ChannelRecord?> UpdateStatusAsync(
        long id,
        string status,
        CancellationToken cancellationToken = default)
    {
        if (StatusFailure is not null)
        {
            throw StatusFailure;
        }

        StatusUpdates.Add((id, status));

        var row = _rows.SingleOrDefault(candidate => candidate.Id == id && candidate.DeletedAt == 0);
        if (row is null)
        {
            return Task.FromResult<ChannelRecord?>(null);
        }

        row.Status = status;
        row.AutoDisabledAt = null;
        row.AutoDisableExpiresAt = null;
        row.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult<ChannelRecord?>(row);
    }

    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken = default)
    {
        if (DeleteFailure is not null)
        {
            throw DeleteFailure;
        }

        var row = _rows.SingleOrDefault(candidate => candidate.Id == id && candidate.DeletedAt == 0);
        if (row is null)
        {
            return Task.FromResult(false);
        }

        row.DeletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        row.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(true);
    }

    public Task BulkUpdateStatusAsync(
        IReadOnlyList<long> ids,
        string status,
        bool clearErrorMessage,
        string action,
        CancellationToken cancellationToken = default)
    {
        if (BulkStatusFailure is not null)
        {
            throw BulkStatusFailure;
        }

        BulkStatusUpdates.Add((ids[0], status, clearErrorMessage, action));

        var matches = _rows.Where(row => row.DeletedAt == 0 && ids.Contains(row.Id)).ToList();
        if (matches.Count != ids.Count)
        {
            throw new ExpectedRowsMismatchException(ids.Count, matches.Count);
        }

        foreach (var row in matches)
        {
            row.Status = status;
            row.AutoDisabledAt = null;
            row.AutoDisableExpiresAt = null;
            row.UpdatedAt = DateTime.UtcNow;
            if (clearErrorMessage)
            {
                row.ErrorMessage = null;
            }
        }

        return Task.CompletedTask;
    }

    public Task BulkDeleteAsync(
        IReadOnlyList<long> ids,
        CancellationToken cancellationToken = default)
    {
        if (BulkDeleteFailure is not null)
        {
            throw BulkDeleteFailure;
        }

        BulkDeletes.Add(ids.ToArray());

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var row in _rows.Where(row => row.DeletedAt == 0 && ids.Contains(row.Id)))
        {
            row.DeletedAt = now;
            row.UpdatedAt = DateTime.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task ManageTagsAsync(
        IReadOnlyList<long> ids,
        IReadOnlyList<string> addTags,
        IReadOnlyList<string> removeTags,
        CancellationToken cancellationToken = default)
    {
        if (ManageTagsFailure is not null)
        {
            throw ManageTagsFailure;
        }

        TagCalls.Add((ids.ToArray(), addTags.ToArray(), removeTags.ToArray()));

        var matches = _rows.Where(row => row.DeletedAt == 0 && ids.Contains(row.Id)).ToList();
        if (matches.Count != ids.Count)
        {
            throw new ExpectedRowsMismatchException(ids.Count, matches.Count);
        }

        foreach (var row in matches)
        {
            var tags = row.TagsList().Where(tag => !removeTags.Contains(tag)).ToList();
            tags.AddRange(addTags.Where(tag => !tags.Contains(tag)));
            row.Tags = JsonSerializer.Serialize(tags);
            row.UpdatedAt = DateTime.UtcNow;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChannelRecord>> BulkUpdateOrderingAsync(
        IReadOnlyList<ChannelOrderingItem> items,
        CancellationToken cancellationToken = default)
    {
        if (BulkOrderingFailure is not null)
        {
            throw BulkOrderingFailure;
        }

        OrderingCalls.Add(items.Select(static item => (item.Id, item.OrderingWeight)).ToArray());

        var updated = new List<ChannelRecord>(items.Count);
        foreach (var item in items)
        {
            var row = _rows.SingleOrDefault(candidate =>
                candidate.Id == item.Id && candidate.DeletedAt == 0);
            if (row is null)
            {
                throw new StoreFailureException(
                    $"failed to update channel {item.Id}: ent: channel not found",
                    new InvalidOperationException("ent: channel not found"));
            }

            row.OrderingWeight = item.OrderingWeight;
            row.UpdatedAt = DateTime.UtcNow;
            updated.Add(row);
        }

        return Task.FromResult<IReadOnlyList<ChannelRecord>>(updated);
    }

    public Task<IReadOnlyList<ChannelRecord>> BulkUpdateAutoDisableAsync(
        IReadOnlyList<long> ids,
        string mode,
        string? rulesJson,
        CancellationToken cancellationToken = default)
    {
        if (BulkAutoDisableFailure is not null)
        {
            throw BulkAutoDisableFailure;
        }

        AutoDisableCalls.Add((ids.ToArray(), mode, rulesJson));

        var matches = _rows.Where(row => row.DeletedAt == 0 && ids.Contains(row.Id)).ToList();
        if (matches.Count != ids.Count)
        {
            throw new ExpectedRowsMismatchException(ids.Count, matches.Count);
        }

        foreach (var row in matches)
        {
            var policies = string.IsNullOrWhiteSpace(row.Policies) || row.Policies == "null"
                ? new JsonObject()
                : JsonNode.Parse(row.Policies)!.AsObject();

            policies["apiKeyAutoDisableMode"] = mode;
            if (mode == "custom")
            {
                policies["apiKeyAutoDisableRules"] = JsonNode.Parse(rulesJson!);
            }
            else if (mode == "inherit")
            {
                policies.Remove("apiKeyAutoDisableRules");
            }

            row.Policies = policies.ToJsonString();
            row.UpdatedAt = DateTime.UtcNow;
        }

        return Task.FromResult<IReadOnlyList<ChannelRecord>>(matches);
    }
}

/// <summary>
/// In-memory IChannelModelPriceStore for the channel lifecycle hosts: the
/// auto-price seam records what CreateChannel/UpdateChannel asked for. The
/// pricing-management methods stay unused by these hosts.
/// </summary>
internal sealed class FakeChannelModelPriceStore : IChannelModelPriceStore
{
    public List<(long ChannelId, IReadOnlyList<string> ModelIds)> EnsureCalls { get; } = [];

    public List<IReadOnlyList<string>> ResolveCalls { get; } = [];

    public List<(long ChannelId, IReadOnlyList<ChannelModelPriceTemplate> Templates)> ApplyCalls { get; } = [];

    public List<(long SourceChannelId, long TargetChannelId)> CopyCalls { get; } = [];

    public StoreFailureException? EnsureFailure { get; set; }

    /// <summary>
    /// Whether the ensure call reports a price change. The model-sync tests use
    /// it to drive the sync's <c>changed</c> flag; the lifecycle tests keep the
    /// default false.
    /// </summary>
    public bool EnsureResult { get; set; }

    public StoreFailureException? ResolveFailure { get; set; }

    public StoreFailureException? ApplyFailure { get; set; }

    public StoreFailureException? CopyFailure { get; set; }

    public IReadOnlyList<ChannelModelPriceTemplate> Templates { get; set; } = [];

    public Task<bool> EnsureAutoPricesAsync(
        long channelId,
        IReadOnlyList<string> modelIds,
        ModelCardPriceJsonFactory modelCardToPriceJson,
        ChannelPriceReferenceIdFactory referenceIdFactory,
        CancellationToken cancellationToken = default)
    {
        if (EnsureFailure is not null)
        {
            throw EnsureFailure;
        }

        EnsureCalls.Add((channelId, modelIds.ToArray()));
        return Task.FromResult(EnsureResult);
    }

    public Task<IReadOnlyList<ChannelModelPriceTemplate>> ResolveAutoPriceTemplatesAsync(
        IReadOnlyList<string> modelIds,
        ModelCardPriceJsonFactory modelCardToPriceJson,
        CancellationToken cancellationToken = default)
    {
        ResolveCalls.Add(modelIds.ToArray());
        if (ResolveFailure is not null)
        {
            throw ResolveFailure;
        }

        return Task.FromResult(Templates);
    }

    public Task<bool> ApplyAutoPriceTemplatesAsync(
        long channelId,
        IReadOnlyList<ChannelModelPriceTemplate> templates,
        ChannelPriceReferenceIdFactory referenceIdFactory,
        CancellationToken cancellationToken = default)
    {
        ApplyCalls.Add((channelId, templates.ToArray()));
        if (ApplyFailure is not null)
        {
            throw ApplyFailure;
        }

        return Task.FromResult(templates.Count > 0);
    }

    public Task CopyChannelPricesAsync(
        long sourceChannelId,
        long targetChannelId,
        ChannelPriceReferenceIdFactory referenceIdFactory,
        CancellationToken cancellationToken = default)
    {
        CopyCalls.Add((sourceChannelId, targetChannelId));
        if (CopyFailure is not null)
        {
            throw CopyFailure;
        }

        return Task.CompletedTask;
    }

    public Task<long> CreateAsync(
        ChannelModelPriceRecord price,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the channel lifecycle never creates a price directly");

    public Task<ChannelModelPriceRecord?> GetByIdAsync(
        long id,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the channel lifecycle never reads a price by id");

    public Task<IReadOnlyList<ChannelModelPriceRecord>> ListByChannelAsync(
        long channelId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the channel lifecycle never lists prices");

    public Task<IReadOnlyList<ChannelModelPriceRecord>> ListEnabledChannelPricesAsync(
        long channelId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the channel lifecycle never reads enabled prices");

    public Task<IReadOnlyList<ChannelModelPriceRecord?>> ApplyAsync(
        long channelId,
        IReadOnlyList<ChannelModelPriceChange> changes,
        DateTime now,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the channel lifecycle never applies a price plan");

    public Task<IReadOnlyList<ChannelModelPriceVersionRecord>> ListVersionsAsync(
        long channelModelPriceId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("the channel lifecycle never lists price versions");
}

internal static class ChannelRecordTestExtensions
{
    public static List<string> TagsList(this ChannelRecord record)
        => string.IsNullOrWhiteSpace(record.Tags) || record.Tags == "null"
            ? []
            : JsonSerializer.Deserialize<List<string>>(record.Tags) ?? [];
}
