using Dapper;
using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// The write half of internal/server/backup at b8bb3421 (restore.go): the
/// single transaction that wraps a restore, the SoftDeleteMixin lookups and the
/// unconditional column writes of each restore builder.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class BackupRestoreStoreTests(PostgresFixture fixture)
{
    private const string EmptyPriceJson = """{"items":[]}""";

    private static readonly DateTime OldStamp = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);

    // ------------------------------------------------------------ transaction

    [Fact]
    public async Task CommittedWritesPersistAfterTheHandleIsDisposed()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupRestoreStore(context);

        await using (var transaction = await store.BeginTransactionAsync())
        {
            var created = await store.CreateChannelAsync(NewChannel("gold"));
            Assert.True(created.Id > 0);

            // Later reads observe earlier uncommitted writes (tx.Client()).
            Assert.NotNull(await store.FindLiveChannelByNameAsync("gold"));

            await transaction.CommitAsync();
        }

        Assert.NotNull(await store.FindLiveChannelByNameAsync("gold"));
    }

    [Fact]
    public async Task DisposingAnUncommittedHandleRollsTheWritesBack()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupRestoreStore(context);

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await store.CreateChannelAsync(NewChannel("gold"));
        }

        Assert.Null(await store.FindLiveChannelByNameAsync("gold"));
        Assert.Null(await store.FindLiveChannelByIdAsync(1));
    }

    [Fact]
    public async Task ExplicitRollbackDropsTheWrites()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupRestoreStore(context);

        await using (var transaction = await store.BeginTransactionAsync())
        {
            await store.CreateChannelAsync(NewChannel("gold"));
            await transaction.RollbackAsync();
        }

        Assert.Null(await store.FindLiveChannelByNameAsync("gold"));
    }

    [Fact]
    public async Task AnAmbientTransactionLeavesCommitAndRollbackToItsOwner()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupRestoreStore(context);

        var outer = await context.Database.BeginTransactionAsync();
        try
        {
            var first = await store.BeginTransactionAsync();
            var second = await store.BeginTransactionAsync();
            Assert.Same(first, second);

            await store.CreateChannelAsync(NewChannel("gold"));
            Assert.NotNull(await store.FindLiveChannelByNameAsync("gold"));

            // Commit on the ambient handle is a no-op: the outer transaction
            // must stay open until its owner completes it.
            await first.CommitAsync();
            Assert.NotNull(context.Database.CurrentTransaction);
            Assert.NotNull(await store.FindLiveChannelByNameAsync("gold"));
        }
        finally
        {
            await outer.RollbackAsync();
            await outer.DisposeAsync();
        }

        Assert.Null(await store.FindLiveChannelByNameAsync("gold"));
    }

    // --------------------------------------------------------------- channels

    [Fact]
    public async Task ChannelLookupsRespectTheSoftDeletePredicate()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupRestoreStore(context);

        var live = await store.CreateChannelAsync(NewChannel("gold"));
        var deleted = await store.CreateChannelAsync(NewChannel("gone"));
        await SoftDeleteAsync(db, "channels", deleted.Id);

        Assert.NotNull(await store.FindLiveChannelByNameAsync("gold"));
        Assert.Null(await store.FindLiveChannelByNameAsync("gone"));
        Assert.NotNull(await store.FindLiveChannelByIdAsync(live.Id));
        Assert.Null(await store.FindLiveChannelByIdAsync(deleted.Id));

        var names = await store.ListLiveChannelIdNamesAsync();
        var only = Assert.Single(names);
        Assert.Equal(live.Id, only.Id);
        Assert.Equal("gold", only.Name);

        var byName = await store.ListLiveChannelIdNamesByNamesAsync(["gold", "gone"]);
        Assert.Equal(live.Id, Assert.Single(byName).Id);
        Assert.Empty(await store.ListLiveChannelIdNamesByNamesAsync([]));
    }

    [Fact]
    public async Task ChannelUpdateWritesEveryColumnAndKeepsTheStoredBaseUrl()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupRestoreStore(context);

        var channel = NewChannel("gold");
        channel.BaseUrl = "https://old";
        channel.Remark = "old remark";
        var created = await store.CreateChannelAsync(channel);

        await store.UpdateChannelAsync(created.Id, new ChannelRestoreUpdate
        {
            BaseUrl = null,
            Status = "enabled",
            CredentialsJson = """{"apiKey":"sk-new"}""",
            SupportedModelsJson = "null",
            AutoSyncSupportedModels = true,
            AutoSyncModelPattern = "gpt-*",
            ManualModelsJson = "null",
            TagsJson = "null",
            DefaultTestModel = "gpt-4o",
            SettingsJson = "null",
            OrderingWeight = 7,
            Remark = null,
        });

        var row = (await store.FindLiveChannelByIdAsync(created.Id))!;
        Assert.Equal("https://old", row.BaseUrl);
        Assert.Null(row.Remark);
        Assert.Equal("enabled", row.Status);
        RecordAssert.Json("""{"apiKey":"sk-new"}""", row.Credentials);
        Assert.Equal("null", row.SupportedModels);
        Assert.True(row.AutoSyncSupportedModels);
        Assert.Equal("gpt-*", row.AutoSyncModelPattern);
        Assert.Equal("null", row.ManualModels);
        Assert.Equal("null", row.Tags);
        Assert.Equal("gpt-4o", row.DefaultTestModel);
        Assert.Equal("null", row.Settings);
        Assert.Equal(7, row.OrderingWeight);
        Assert.True(row.UpdatedAt > OldStamp);

        await store.UpdateChannelAsync(created.Id, new ChannelRestoreUpdate
        {
            BaseUrl = "https://new",
            Status = "disabled",
            CredentialsJson = "{}",
            SupportedModelsJson = "null",
            AutoSyncModelPattern = string.Empty,
            ManualModelsJson = "null",
            TagsJson = "null",
            DefaultTestModel = string.Empty,
            SettingsJson = "null",
            Remark = "kept verbatim",
        });

        row = (await store.FindLiveChannelByIdAsync(created.Id))!;
        Assert.Equal("https://new", row.BaseUrl);
        Assert.Equal("kept verbatim", row.Remark);
        Assert.Equal("disabled", row.Status);
    }

    [Fact]
    public async Task ChannelUpdatesReportTheEntNotFoundTextAndSkipTheSoftDeletedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupRestoreStore(context);

        var missing = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.UpdateChannelAsync(999, ChannelUpdate()));
        Assert.Equal("ent: channel not found", missing.Message);

        var settings = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.SetChannelSettingsAsync(999, "{}"));
        Assert.Equal("ent: channel not found", settings.Message);

        var channel = await store.CreateChannelAsync(NewChannel("gold"));
        await SoftDeleteAsync(db, "channels", channel.Id);

        await Assert.ThrowsAsync<StoreFailureException>(
            () => store.UpdateChannelAsync(channel.Id, ChannelUpdate()));
        await Assert.ThrowsAsync<StoreFailureException>(
            () => store.SetChannelSettingsAsync(channel.Id, "{}"));
    }

    [Fact]
    public async Task SetChannelSettingsAndUpdatedAtWriteTheExactValues()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupRestoreStore(context);

        var created = await store.CreateChannelAsync(NewChannel("gold"));

        await store.SetChannelSettingsAsync(
            created.Id,
            """{"quota_routing":{"enabled":true}}""");

        var row = (await store.FindLiveChannelByIdAsync(created.Id))!;
        RecordAssert.Json("""{"quota_routing":{"enabled":true}}""", row.Settings);
        Assert.True(row.UpdatedAt > OldStamp);

        // The explicit timestamp overrides the time mixin (restore.go:690-694).
        var exact = new DateTime(2025, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        await store.SetChannelUpdatedAtAsync(created.Id, exact);
        row = (await store.FindLiveChannelByIdAsync(created.Id))!;
        Assert.Equal(exact, row.UpdatedAt);

        // ent's Exec does not surface a missing row as an error.
        await store.SetChannelUpdatedAtAsync(999, exact);
    }

    // ------------------------------------------------- projects, models, keys

    [Fact]
    public async Task ProjectCreateFindAndUpdateMatchTheRestoreBuilder()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupRestoreStore(context);

        var created = await store.CreateProjectAsync(new ProjectRecord
        {
            CreatedAt = OldStamp,
            UpdatedAt = OldStamp,
            Name = "Default",
            Description = "old",
            Status = "active",
        });
        Assert.True(created.Id > 0);

        var found = await store.FindLiveProjectByNameAsync("Default");
        Assert.Equal(created.Id, found!.Id);
        Assert.Equal(created.Id, Assert.Single(await store.ListLiveProjectIdNamesAsync()).Id);

        await store.UpdateProjectAsync(created.Id, new ProjectRestoreUpdate
        {
            Name = "Default",
            Description = "new",
            Status = "disabled",
            ProfilesJson = "null",
        });

        found = (await store.FindLiveProjectByNameAsync("Default"))!;
        Assert.Equal("new", found.Description);
        Assert.Equal("disabled", found.Status);
        Assert.Equal("null", found.Profiles);
        Assert.True(found.UpdatedAt > OldStamp);

        await SoftDeleteAsync(db, "projects", created.Id);
        Assert.Null(await store.FindLiveProjectByNameAsync("Default"));
        Assert.Empty(await store.ListLiveProjectIdNamesAsync());

        var missing = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.UpdateProjectAsync(999, new ProjectRestoreUpdate
            {
                Name = "x",
                Description = string.Empty,
                Status = string.Empty,
                ProfilesJson = "null",
            }));
        Assert.Equal("ent: project not found", missing.Message);
    }

    [Fact]
    public async Task ModelLookupsMatchDeveloperAndModelId()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupRestoreStore(context);

        var created = await store.CreateModelAsync(NewModel("gpt-4o"));
        Assert.True(created.Id > 0);

        Assert.Equal(
            created.Id,
            (await store.FindLiveModelAsync("openai", "gpt-4o"))!.Id);
        Assert.Null(await store.FindLiveModelAsync("anthropic", "gpt-4o"));
        Assert.Null(await store.FindLiveModelAsync("openai", "missing"));

        await store.UpdateModelAsync(created.Id, new ModelRestoreUpdate
        {
            Name = "GPT-4o",
            Icon = "icon",
            Group = "group",
            ModelCardJson = "null",
            SettingsJson = "null",
            Status = "enabled",
            Remark = null,
        });

        var row = (await store.FindLiveModelAsync("openai", "gpt-4o"))!;
        Assert.Equal("GPT-4o", row.Name);
        Assert.Equal("icon", row.Icon);
        Assert.Equal("group", row.Group);
        Assert.Equal("null", row.ModelCard);
        Assert.Equal("null", row.Settings);
        Assert.Equal("enabled", row.Status);
        Assert.Null(row.Remark);
        Assert.True(row.UpdatedAt > OldStamp);

        await SoftDeleteAsync(db, "models", created.Id);
        Assert.Null(await store.FindLiveModelAsync("openai", "gpt-4o"));

        var missing = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.UpdateModelAsync(999, new ModelRestoreUpdate
            {
                Name = "x",
                Icon = string.Empty,
                Group = string.Empty,
                ModelCardJson = "null",
                SettingsJson = "null",
                Status = string.Empty,
            }));
        Assert.Equal("ent: model not found", missing.Message);
    }

    [Fact]
    public async Task ApiKeyCreateFindAndUpdateMatchTheRestoreBuilder()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupRestoreStore(context);

        var created = await store.CreateApiKeyAsync(NewApiKey("sk-1"));
        Assert.True(created.Id > 0);

        Assert.Equal(created.Id, (await store.FindLiveApiKeyByKeyAsync("sk-1"))!.Id);
        Assert.Null(await store.FindLiveApiKeyByKeyAsync("sk-2"));
        Assert.Equal(created.Id, Assert.Single(await store.ListLiveApiKeyIdKeysAsync()).Id);

        await store.UpdateApiKeyAsync(created.Id, new ApiKeyRestoreUpdate
        {
            Name = "renamed",
            Type = "service_account",
            Status = "disabled",
            ScopesJson = "null",
            ProfilesJson = "null",
        });

        var row = (await store.FindLiveApiKeyByKeyAsync("sk-1"))!;
        Assert.Equal("renamed", row.Name);
        Assert.Equal("service_account", row.Type);
        Assert.Equal("disabled", row.Status);
        Assert.Equal("null", row.Scopes);
        Assert.Equal("null", row.Profiles);
        Assert.True(row.UpdatedAt > OldStamp);

        await SoftDeleteAsync(db, "api_keys", created.Id);
        Assert.Null(await store.FindLiveApiKeyByKeyAsync("sk-1"));
        Assert.Empty(await store.ListLiveApiKeyIdKeysAsync());

        var missing = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.UpdateApiKeyAsync(999, new ApiKeyRestoreUpdate
            {
                Name = "x",
                Type = string.Empty,
                Status = string.Empty,
                ScopesJson = "null",
                ProfilesJson = "null",
            }));
        Assert.Equal("ent: api_key not found", missing.Message);
    }

    // ------------------------------------------------------ channel prices

    [Fact]
    public async Task ChannelModelPriceCreateFindAndUpdateMatchTheRestoreBuilder()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupRestoreStore(context);

        var channel = await store.CreateChannelAsync(NewChannel("gold"));
        var created = await store.CreateChannelModelPriceAsync(new ChannelModelPriceRecord
        {
            CreatedAt = OldStamp,
            UpdatedAt = OldStamp,
            ChannelId = channel.Id,
            ModelId = "gpt-4o",
            Price = EmptyPriceJson,
            ReferenceId = "ref-1",
        });
        Assert.True(created.Id > 0);

        var found = await store.FindLiveChannelModelPriceAsync(channel.Id, "gpt-4o");
        Assert.Equal(created.Id, found!.Id);
        Assert.Null(await store.FindLiveChannelModelPriceAsync(channel.Id, "missing"));
        Assert.Null(await store.FindLiveChannelModelPriceAsync(999, "gpt-4o"));

        await store.UpdateChannelModelPriceAsync(
            created.Id,
            """{"items":[{"itemCode":"prompt_tokens","pricing":{"mode":"flat_fee","flatFee":"0.9"}}]}""",
            "ref-2");

        found = (await store.FindLiveChannelModelPriceAsync(channel.Id, "gpt-4o"))!;
        Assert.Equal("ref-2", found.ReferenceId);
        RecordAssert.Json(
            """{"items":[{"itemCode":"prompt_tokens","pricing":{"mode":"flat_fee","flatFee":"0.9"}}]}""",
            found.Price);
        Assert.True(found.UpdatedAt > OldStamp);

        var missing = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.UpdateChannelModelPriceAsync(999, EmptyPriceJson, "ref"));
        Assert.Equal("ent: channel_model_price not found", missing.Message);
    }

    [Fact]
    public async Task ArchivingOnlyTouchesTheActiveVersionsOfThePrice()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupRestoreStore(context);

        var active = await CreateVersionAsync(store, priceId: 11, status: "active");
        var archived = await CreateVersionAsync(store, priceId: 11, status: "archived");
        var otherPrice = await CreateVersionAsync(store, priceId: 12, status: "active");

        var archiveAt = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        await store.ArchiveActiveChannelModelPriceVersionsAsync(11, archiveAt);

        var rows = await context.ChannelModelPriceVersions
            .AsNoTracking()
            .OrderBy(static version => version.Id)
            .ToListAsync();

        Assert.Equal([active.Id, archived.Id, otherPrice.Id],
            rows.Select(static version => version.Id).ToArray());
        Assert.Equal("archived", rows[0].Status);
        Assert.Equal(archiveAt, rows[0].EffectiveEndAt);
        Assert.True(rows[0].UpdatedAt > OldStamp);

        Assert.Equal("archived", rows[1].Status);
        Assert.Equal(OldStamp.AddDays(1), rows[1].EffectiveEndAt);
        Assert.Equal(OldStamp, rows[1].UpdatedAt);

        Assert.Equal("active", rows[2].Status);
        Assert.Null(rows[2].EffectiveEndAt);
    }

    // -------------------------------------------------------------- usage data

    [Fact]
    public async Task UsageRequestLookupsResolveTheLiveEdges()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupRestoreStore(context);

        var liveProject = await AddAsync(context, NewProject("Default"));
        var deletedProject = await AddAsync(context, NewProject("gone"));
        var liveChannel = await AddAsync(context, NewChannel("gold"));
        var deletedChannel = await AddAsync(context, NewChannel("gone"));
        var liveKey = await AddAsync(context, NewApiKey("sk-1", liveProject.Id));
        var deletedKey = await AddAsync(context, NewApiKey("sk-gone", deletedProject.Id));

        var full = await store.CreateUsageRequestAsync(
            NewRequest(liveProject.Id, liveChannel.Id, liveKey.Id));
        var orphan = await store.CreateUsageRequestAsync(
            NewRequest(deletedProject.Id, deletedChannel.Id, deletedKey.Id));
        Assert.True(full.Id > 0);

        await SoftDeleteAsync(db, "projects", deletedProject.Id);
        await SoftDeleteAsync(db, "channels", deletedChannel.Id);
        await SoftDeleteAsync(db, "api_keys", deletedKey.Id);

        var rows = await store.FindUsageRequestsByIdsAsync([full.Id, orphan.Id]);
        Assert.Equal([full.Id, orphan.Id], rows.Select(static row => row.Request.Id).ToArray());
        Assert.Equal("Default", rows[0].ProjectName);
        Assert.Equal("gold", rows[0].ChannelName);
        Assert.Equal("sk-1", rows[0].ApiKeyKey);
        Assert.Null(rows[1].ProjectName);
        Assert.Null(rows[1].ChannelName);
        Assert.Null(rows[1].ApiKeyKey);

        // The CreatedAtIn variant of existingUsageRequests.
        var byCreatedAt = await store.FindUsageRequestsByCreatedAtAsync([OldStamp]);
        Assert.Equal(2, byCreatedAt.Count);

        Assert.Empty(await store.FindUsageRequestsByIdsAsync([]));
        Assert.Empty(await store.FindUsageRequestsByCreatedAtAsync([]));
    }

    [Fact]
    public async Task UsageRequestLookupsSplitIntoFiveHundredIdBatches()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupRestoreStore(context);

        var request = await store.CreateUsageRequestAsync(NewRequest(1, null, null));

        var ids = new List<long> { request.Id };
        ids.AddRange(Enumerable.Range(1_000, 500).Select(static id => (long)id));

        var rows = await store.FindUsageRequestsByIdsAsync(ids);

        Assert.Equal(request.Id, Assert.Single(rows).Request.Id);
    }

    [Fact]
    public async Task UsageLogLookupsAndBulkInsertMatchTheReference()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new BackupRestoreStore(context);

        await store.CreateUsageLogsAsync([
            NewLog(requestId: 1),
            NewLog(requestId: 1),
            NewLog(requestId: 2),
        ]);

        var requestIds = await store.ListUsageLogRequestIdsAsync([1, 2, 3]);
        Assert.Equal([1, 2], requestIds.Order().ToArray());
        Assert.Empty(await store.ListUsageLogRequestIdsAsync([]));

        // The whole CreateBulk batch lands in one save.
        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(3, await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM \"usage_logs\";"));

        // An empty batch is a no-op.
        await store.CreateUsageLogsAsync([]);
    }

    // --------------------------------------------------------------- helpers

    private static async Task<ChannelModelPriceVersionRecord> CreateVersionAsync(
        BackupRestoreStore store,
        long priceId,
        string status)
    {
        var version = new ChannelModelPriceVersionRecord
        {
            CreatedAt = OldStamp,
            UpdatedAt = OldStamp,
            ChannelId = 5,
            ModelId = "gpt-4o",
            ChannelModelPriceId = priceId,
            Price = EmptyPriceJson,
            Status = status,
            EffectiveStartAt = OldStamp,
            EffectiveEndAt = status == "archived" ? OldStamp.AddDays(1) : null,

            // channel_model_price_versions.reference_id is globally unique.
            ReferenceId = "ref-" + priceId + "-" + status,
        };

        await store.CreateChannelModelPriceVersionAsync(version);
        return version;
    }

    private static ChannelRestoreUpdate ChannelUpdate() => new()
    {
        Status = "enabled",
        CredentialsJson = "{}",
        SupportedModelsJson = "null",
        AutoSyncModelPattern = string.Empty,
        ManualModelsJson = "null",
        TagsJson = "null",
        DefaultTestModel = string.Empty,
        SettingsJson = "null",
        Remark = null,
    };

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

    private static ChannelRecord NewChannel(string name) => new()
    {
        CreatedAt = OldStamp,
        UpdatedAt = OldStamp,
        Type = "openai",
        Name = name,
        Status = "enabled",
        SupportedModels = "null",
        Policies = """{"stream":"unlimited"}""",
    };

    private static ProjectRecord NewProject(string name) => new()
    {
        CreatedAt = OldStamp,
        UpdatedAt = OldStamp,
        Name = name,
        Description = string.Empty,
        Status = "active",
    };

    private static ModelRecord NewModel(string modelId) => new()
    {
        CreatedAt = OldStamp,
        UpdatedAt = OldStamp,
        Developer = "openai",
        ModelId = modelId,
        Type = "chat",
        Name = modelId,
        Status = "enabled",
    };

    private static ApiKeyRecord NewApiKey(string key, long projectId = 1) => new()
    {
        CreatedAt = OldStamp,
        UpdatedAt = OldStamp,
        Key = key,
        Name = key,
        ProjectId = projectId,
    };

    private static RequestRecord NewRequest(
        long projectId,
        long? channelId,
        long? apiKeyId) => new()
    {
        CreatedAt = OldStamp,
        UpdatedAt = OldStamp,
        ProjectId = projectId,
        ChannelId = channelId,
        ApiKeyId = apiKeyId,
        Source = "api",
        ModelId = "gpt-4o",
        Format = "openai/chat_completions",
        Status = "completed",
        ClientIp = "127.0.0.1",
    };

    private static UsageLogRecord NewLog(long requestId) => new()
    {
        CreatedAt = OldStamp,
        UpdatedAt = OldStamp,
        RequestId = requestId,
        ProjectId = 1,
        ModelId = "gpt-4o",
        PromptTokens = 1,
        CompletionTokens = 2,
        TotalTokens = 3,
    };
}
