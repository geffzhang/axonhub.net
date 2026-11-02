using System.Net;
using System.Text;
using System.Text.Json;
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

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAxonHubGraphQL();
        builder.Services.AddSingleton<IChannelWriteStore>(_channels);
        builder.Services.AddSingleton(_limiters);
        builder.Services.AddSingleton<ChannelLifecycleService>();

        _app = builder.Build();
        _app.MapGraphQL("/admin/graphql");
        await _app.StartAsync();
        _client = _app.GetTestClient();
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

    public List<ChannelRecord> CreatedChannels { get; } = [];

    public List<(long Id, ChannelUpdateRecord Update)> UpdateCalls { get; } = [];

    public List<(long Id, string Name)> NameChecks { get; } = [];

    /// <summary>Simulates a concurrent writer invalidating the version guard.</summary>
    public bool UpdateConflict { get; set; }

    public Exception? GetForUpdateFailure { get; set; }

    public Exception? NameCheckFailure { get; set; }

    public Exception? UpdateFailure { get; set; }

    public Exception? CreateFailure { get; set; }

    public Exception? StatusFailure { get; set; }

    public Exception? DeleteFailure { get; set; }

    public Exception? BulkStatusFailure { get; set; }

    public Exception? BulkDeleteFailure { get; set; }

    public Exception? ManageTagsFailure { get; set; }

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

        return Task.FromResult(
            _rows.SingleOrDefault(row => row.Id == id && row.DeletedAt == 0));
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

    public Task<ChannelRecord?> UpdateAsync(
        ChannelRecord snapshot,
        ChannelUpdateRecord update,
        CancellationToken cancellationToken = default)
    {
        if (UpdateFailure is not null)
        {
            throw UpdateFailure;
        }

        if (UpdateConflict)
        {
            return Task.FromResult<ChannelRecord?>(null);
        }

        var row = _rows.SingleOrDefault(candidate =>
            candidate.Id == snapshot.Id && candidate.DeletedAt == 0);
        if (row is null
            || (update.ExpectedUpdatedAt is { } expected && row.UpdatedAt != expected))
        {
            return Task.FromResult<ChannelRecord?>(null);
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
        return Task.FromResult<ChannelRecord?>(row);
    }

    public Task<ChannelRecord?> CreateAsync(
        ChannelRecord channel,
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
            return Task.FromResult<ChannelRecord?>(null);
        }

        channel.Id = _nextId++;
        _rows.Add(channel);
        CreatedChannels.Add(channel);
        return Task.FromResult<ChannelRecord?>(channel);
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
}

internal static class ChannelRecordTestExtensions
{
    public static List<string> TagsList(this ChannelRecord record)
        => string.IsNullOrWhiteSpace(record.Tags) || record.Tags == "null"
            ? []
            : JsonSerializer.Deserialize<List<string>>(record.Tags) ?? [];
}
