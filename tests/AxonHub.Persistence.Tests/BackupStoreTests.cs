using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// The read half of internal/server/backup at b8bb3421: every
/// <see cref="BackupStore"/> page is an ent-style id cursor
/// (backup_ops.go:127-356) that filters the SoftDeleteMixin predicate and
/// resolves the eager-loaded project/channel/API key edges against live rows
/// only.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BackupStoreTests(PostgresFixture fixture)
{
    private const string EmptyPriceJson = """{"items":[]}""";

    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task SystemPageStreamsOnlyWhitelistedLiveRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupStore(context);

        var title = await AddAsync(context, NewSystem(SystemConfigBackupKeys.Title, "Axon"));
        await AddAsync(context, NewSystem("system_initialized", "true"));
        var retry = await AddAsync(
            context,
            NewSystem(SystemConfigBackupKeys.RetryPolicy, """{"enabled":true}"""));

        // Soft-deleted whitelisted key: the SoftDeleteMixin predicate hides it.
        var deleted = await AddAsync(
            context,
            NewSystem(SystemConfigBackupKeys.BrandLogo, "logo"));
        await SoftDeleteAsync(db, "systems", deleted.Id);

        var (rows, nextId) = await store.FetchSystemBatchAsync(0, 50, CancellationToken.None);

        Assert.Equal([title.Id, retry.Id], rows.Select(static row => row.Id).ToArray());
        Assert.Equal(
            [SystemConfigBackupKeys.Title, SystemConfigBackupKeys.RetryPolicy],
            rows.Select(static row => row.Key).ToArray());
        Assert.Equal(retry.Id, nextId);
    }

    [Fact]
    public async Task ProjectChannelAndModelPagesSkipSoftDeletedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupStore(context);

        var project = await AddAsync(context, NewProject("Default"));
        var goneProject = await AddAsync(context, NewProject("gone"));
        await SoftDeleteAsync(db, "projects", goneProject.Id);

        var channel = await AddAsync(context, NewChannel("gold"));
        var goneChannel = await AddAsync(context, NewChannel("gone"));
        await SoftDeleteAsync(db, "channels", goneChannel.Id);

        var model = await AddAsync(context, NewModel("gpt-4o"));
        var goneModel = await AddAsync(context, NewModel("gone"));
        await SoftDeleteAsync(db, "models", goneModel.Id);

        var (projects, projectCursor) =
            await store.FetchProjectBatchAsync(0, 50, CancellationToken.None);
        Assert.Equal(project.Id, Assert.Single(projects).Id);
        Assert.Equal(project.Id, projectCursor);

        var (channels, channelCursor) =
            await store.FetchChannelBatchAsync(0, 50, CancellationToken.None);
        Assert.Equal(channel.Id, Assert.Single(channels).Id);
        Assert.Equal(channel.Id, channelCursor);

        var (models, modelCursor) =
            await store.FetchModelBatchAsync(0, 50, CancellationToken.None);
        Assert.Equal(model.Id, Assert.Single(models).Id);
        Assert.Equal(model.Id, modelCursor);
    }

    [Fact]
    public async Task CursorPagesReturnTheLastRowIdUntilTheFeedIsEmpty()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupStore(context);

        var first = await AddAsync(context, NewProject("first"));
        var second = await AddAsync(context, NewProject("second"));
        var third = await AddAsync(context, NewProject("third"));

        var (page, cursor) = await store.FetchProjectBatchAsync(0, 2, CancellationToken.None);
        Assert.Equal([first.Id, second.Id], page.Select(static row => row.Id).ToArray());
        Assert.Equal(second.Id, cursor);

        (page, cursor) = await store.FetchProjectBatchAsync(cursor, 2, CancellationToken.None);
        Assert.Equal(third.Id, Assert.Single(page).Id);
        Assert.Equal(third.Id, cursor);

        (page, cursor) = await store.FetchProjectBatchAsync(cursor, 2, CancellationToken.None);
        Assert.Empty(page);
        Assert.Equal(0, cursor);
    }

    [Fact]
    public async Task PricePageCarriesTheLiveChannelNameOnly()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupStore(context);

        var live = await AddAsync(context, NewChannel("gold"));
        var deleted = await AddAsync(context, NewChannel("gone"));
        await SoftDeleteAsync(db, "channels", deleted.Id);

        var livePrice = await AddAsync(context, NewPrice(live.Id, "gpt-4o"));
        var orphanPrice = await AddAsync(context, NewPrice(deleted.Id, "gpt-4o"));

        var (rows, nextId) =
            await store.FetchChannelModelPriceBatchAsync(0, 50, CancellationToken.None);

        Assert.Equal(
            [livePrice.Id, orphanPrice.Id],
            rows.Select(static row => row.Price.Id).ToArray());
        Assert.Equal("gold", rows[0].ChannelName);
        Assert.Null(rows[1].ChannelName);
        Assert.Equal(orphanPrice.Id, nextId);
    }

    [Fact]
    public async Task ApiKeyPageResolvesTheLiveProjectEdge()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupStore(context);

        var live = await AddAsync(context, NewProject("Default"));
        var deleted = await AddAsync(context, NewProject("gone"));
        await SoftDeleteAsync(db, "projects", deleted.Id);

        var liveKey = await AddAsync(context, NewApiKey("sk-live", live.Id));
        var orphanKey = await AddAsync(context, NewApiKey("sk-gone", deleted.Id));

        var (rows, nextId) = await store.FetchApiKeyBatchAsync(0, 50, CancellationToken.None);

        Assert.Equal(
            [liveKey.Id, orphanKey.Id],
            rows.Select(static row => row.ApiKey.Id).ToArray());
        Assert.Equal("Default", rows[0].Project!.Name);
        Assert.Null(rows[1].Project);
        Assert.Equal(orphanKey.Id, nextId);
    }

    [Fact]
    public async Task UsageRequestPageResolvesLiveEdgesAndGatesTheApiKeyKey()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupStore(context);

        var liveProject = await AddAsync(context, NewProject("Default"));
        var deletedProject = await AddAsync(context, NewProject("gone"));
        var liveChannel = await AddAsync(context, NewChannel("gold"));
        var deletedChannel = await AddAsync(context, NewChannel("gone"));
        var liveKey = await AddAsync(context, NewApiKey("sk-live", liveProject.Id));
        var deletedKey = await AddAsync(context, NewApiKey("sk-gone", deletedProject.Id));

        var full = await AddAsync(
            context,
            NewRequest(liveProject.Id, liveChannel.Id, liveKey.Id, "gpt-4o"));
        var orphan = await AddAsync(
            context,
            NewRequest(deletedProject.Id, deletedChannel.Id, deletedKey.Id, "gone"));
        var bare = await AddAsync(context, NewRequest(liveProject.Id, null, null, "gpt-4o"));

        await SoftDeleteAsync(db, "projects", deletedProject.Id);
        await SoftDeleteAsync(db, "channels", deletedChannel.Id);
        await SoftDeleteAsync(db, "api_keys", deletedKey.Id);

        var (rows, nextId) = await store.FetchUsageRequestBatchAsync(
            0,
            50,
            includeApiKeys: true,
            CancellationToken.None);

        Assert.Equal(
            [full.Id, orphan.Id, bare.Id],
            rows.Select(static row => row.Request.Id).ToArray());
        Assert.Equal("Default", rows[0].ProjectName);
        Assert.Equal("gold", rows[0].ChannelName);
        Assert.Equal("sk-live", rows[0].ApiKeyKey);
        Assert.Null(rows[1].ProjectName);
        Assert.Null(rows[1].ChannelName);
        Assert.Null(rows[1].ApiKeyKey);
        Assert.Equal("Default", rows[2].ProjectName);
        Assert.Null(rows[2].ChannelName);
        Assert.Null(rows[2].ApiKeyKey);
        Assert.Equal(bare.Id, nextId);

        // includeAPIKeys=false never loads the key map (backup_ops.go:303-318).
        (rows, _) = await store.FetchUsageRequestBatchAsync(
            0,
            50,
            includeApiKeys: false,
            CancellationToken.None);

        Assert.All(rows, static row => Assert.Null(row.ApiKeyKey));
        Assert.Equal("Default", rows[0].ProjectName);
        Assert.Equal("gold", rows[0].ChannelName);
    }

    [Fact]
    public async Task UsageLogPageResolvesLiveProjectAndChannelNames()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupStore(context);

        var liveProject = await AddAsync(context, NewProject("Default"));
        var deletedProject = await AddAsync(context, NewProject("gone"));
        var liveChannel = await AddAsync(context, NewChannel("gold"));
        var deletedChannel = await AddAsync(context, NewChannel("gone"));

        var full = await AddAsync(
            context,
            NewLog(liveProject.Id, liveChannel.Id));
        var orphan = await AddAsync(
            context,
            NewLog(deletedProject.Id, deletedChannel.Id));
        var bare = await AddAsync(context, NewLog(liveProject.Id, null));

        await SoftDeleteAsync(db, "projects", deletedProject.Id);
        await SoftDeleteAsync(db, "channels", deletedChannel.Id);

        var (rows, nextId) = await store.FetchUsageLogBatchAsync(0, 50, CancellationToken.None);

        Assert.Equal(
            [full.Id, orphan.Id, bare.Id],
            rows.Select(static row => row.Log.Id).ToArray());
        Assert.Equal("Default", rows[0].ProjectName);
        Assert.Equal("gold", rows[0].ChannelName);
        Assert.Null(rows[1].ProjectName);
        Assert.Null(rows[1].ChannelName);
        Assert.Equal("Default", rows[2].ProjectName);
        Assert.Null(rows[2].ChannelName);
        Assert.Equal(bare.Id, nextId);
    }

    [Fact]
    public async Task ApiKeyKeyMapContainsLiveKeysOnly()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupStore(context);

        var first = await AddAsync(context, NewApiKey("sk-1", 1));
        var second = await AddAsync(context, NewApiKey("sk-2", 1));
        var deleted = await AddAsync(context, NewApiKey("sk-gone", 1));
        await SoftDeleteAsync(db, "api_keys", deleted.Id);

        var map = await store.FetchApiKeyKeyMapAsync(CancellationToken.None);

        Assert.Equal(2, map.Count);
        Assert.Equal("sk-1", map[first.Id]);
        Assert.Equal("sk-2", map[second.Id]);
        Assert.False(map.ContainsKey(deleted.Id));
    }

    // --------------------------------------------------------------- helpers

    private static async Task<T> AddAsync<T>(AxonHubDbContext context, T entity)
        where T : class
    {
        context.Add(entity);
        await context.SaveChangesAsync();
        return entity;
    }

    /// <summary>SoftDeleteMixin stores Unix seconds; every ent query filters 0.</summary>
    private static async Task SoftDeleteAsync(TestDatabase db, string table, long id)
    {
        await using var connection = await db.Factory.OpenAsync();
        await connection.ExecuteAsync(
            "UPDATE \"" + table + "\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
            new { DeletedAt = 1_700_000_000L, Id = id });
    }

    private static SystemRecord NewSystem(string key, string value) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Key = key,
        Value = value,
    };

    private static ProjectRecord NewProject(string name) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Name = name,
        Description = string.Empty,
        Status = "active",
    };

    private static ChannelRecord NewChannel(string name) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Type = "openai",
        Name = name,
        Status = "enabled",
    };

    private static ModelRecord NewModel(string modelId) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Developer = "openai",
        ModelId = modelId,
        Type = "chat",
        Name = modelId,
        Status = "enabled",
    };

    private static ChannelModelPriceRecord NewPrice(long channelId, string modelId) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        ChannelId = channelId,
        ModelId = modelId,
        Price = EmptyPriceJson,

        // channel_model_prices.reference_id is globally unique.
        ReferenceId = "ref-" + channelId + "-" + modelId,
    };

    private static ApiKeyRecord NewApiKey(string key, long projectId) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Key = key,
        Name = key,
        ProjectId = projectId,
    };

    private static RequestRecord NewRequest(
        long projectId,
        long? channelId,
        long? apiKeyId,
        string modelId) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        ProjectId = projectId,
        ChannelId = channelId,
        ApiKeyId = apiKeyId,
        Source = "api",
        ModelId = modelId,
        Format = "openai/chat_completions",
        Status = "completed",
        ClientIp = "127.0.0.1",
    };

    private static UsageLogRecord NewLog(long projectId, long? channelId) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        RequestId = 1,
        ProjectId = projectId,
        ChannelId = channelId,
        ModelId = "gpt-4o",
        PromptTokens = 1,
        CompletionTokens = 2,
        TotalTokens = 3,
    };
}
