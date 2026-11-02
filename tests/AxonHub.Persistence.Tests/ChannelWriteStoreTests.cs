using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Locks the channel lifecycle store against the reference's write semantics:
/// status changes clear the auto-disable markers, soft delete stores Unix
/// seconds, bulk status updates verify the exact id count inside one
/// transaction, bulk delete trusts the id list, and tag management applies the
/// load-then-guarded-update sequence of channel_bulk.go:365-432.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelWriteStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task UpdateStatusClearsAutoDisableMarkersAndStampsUpdatedAt()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var channel = await SeedChannelAsync(
            context,
            status: "disabled",
            errorMessage: "boom",
            autoDisabledAt: UpdatedAt,
            autoDisableExpiresAt: UpdatedAt.AddHours(1));

        var updated = await store.UpdateStatusAsync(channel.Id, "enabled");

        Assert.NotNull(updated);
        Assert.Equal("enabled", updated.Status);
        Assert.Null(updated.AutoDisabledAt);
        Assert.Null(updated.AutoDisableExpiresAt);

        // A manual status change does not touch error_message.
        Assert.Equal("boom", updated.ErrorMessage);
        Assert.True(updated.UpdatedAt > UpdatedAt);

        // UpdateOneID(...).Save persists even when column values did not change.
        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == channel.Id);
        Assert.Equal("enabled", reloaded.Status);
        Assert.Null(reloaded.AutoDisabledAt);
        Assert.Null(reloaded.AutoDisableExpiresAt);
        Assert.True(reloaded.UpdatedAt > UpdatedAt);
    }

    [Fact]
    public async Task UpdateStatusReturnsNullForMissingAndSoftDeletedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var deleted = await SeedChannelAsync(context, deletedAt: 1234);

        Assert.Null(await store.UpdateStatusAsync(424242, "enabled"));
        Assert.Null(await store.UpdateStatusAsync(deleted.Id, "enabled"));

        // The deleted row is untouched: the soft-delete predicate filters it out.
        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == deleted.Id);
        Assert.Equal("disabled", reloaded.Status);
        Assert.Equal(1234, reloaded.DeletedAt);
    }

    [Fact]
    public async Task DeleteSoftDeletesWithUnixSecondsAndHidesTheRow()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var channel = await SeedChannelAsync(context);

        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(await store.DeleteAsync(channel.Id));
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == channel.Id);
        Assert.InRange(reloaded.DeletedAt, before, after);
        Assert.True(reloaded.UpdatedAt > UpdatedAt);

        // The second delete finds no live row, mirroring DeleteOneID's not-found.
        Assert.False(await store.DeleteAsync(channel.Id));
    }

    [Fact]
    public async Task BulkUpdateVerifiesTheExactIdCountBeforeWritingAnything()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var alpha = await SeedChannelAsync(context, name: "Alpha");
        var deleted = await SeedChannelAsync(context, name: "Deleted", deletedAt: 77);

        // Duplicates fail the count check even though they name one live row.
        var duplicates = await Assert.ThrowsAsync<ExpectedRowsMismatchException>(
            () => store.BulkUpdateStatusAsync([alpha.Id, alpha.Id], "archived", false, "archive"));
        Assert.Equal(2, duplicates.Expected);
        Assert.Equal(1, duplicates.Found);
        Assert.Equal("expected to find 2 channels, but found 1", duplicates.Message);

        // A soft-deleted id is not counted either.
        var withDeleted = await Assert.ThrowsAsync<ExpectedRowsMismatchException>(
            () => store.BulkUpdateStatusAsync([alpha.Id, deleted.Id], "archived", false, "archive"));
        Assert.Equal(2, withDeleted.Expected);
        Assert.Equal(1, withDeleted.Found);

        // Nothing was written before the check failed.
        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == alpha.Id);
        Assert.Equal("disabled", reloaded.Status);
        Assert.Equal(UpdatedAt, reloaded.UpdatedAt);
    }

    [Fact]
    public async Task BulkUpdateWritesStatusAndOnlyRecoverClearsTheErrorMessage()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var alpha = await SeedChannelAsync(
            context,
            name: "Alpha",
            status: "enabled",
            errorMessage: "rate limited",
            autoDisabledAt: UpdatedAt,
            autoDisableExpiresAt: UpdatedAt.AddMinutes(5));
        var bravo = await SeedChannelAsync(context, name: "Bravo", status: "disabled");

        await store.BulkUpdateStatusAsync([alpha.Id, bravo.Id], "archived", false, "archive");

        await using (var verify = db.Factory.CreateDbContext())
        {
            var archived = await verify.Channels.SingleAsync(row => row.Id == alpha.Id);
            Assert.Equal("archived", archived.Status);
            Assert.Null(archived.AutoDisabledAt);
            Assert.Null(archived.AutoDisableExpiresAt);

            // archive/disable/enable keep the stored error message.
            Assert.Equal("rate limited", archived.ErrorMessage);
            Assert.True(archived.UpdatedAt > UpdatedAt);
        }

        // BulkRecover passes clearErrorMessage=true (channel_bulk.go:163-204).
        await store.BulkUpdateStatusAsync([alpha.Id, bravo.Id], "enabled", true, "recover");

        await using (var verify = db.Factory.CreateDbContext())
        {
            var recovered = await verify.Channels.SingleAsync(row => row.Id == alpha.Id);
            Assert.Equal("enabled", recovered.Status);
            Assert.Null(recovered.ErrorMessage);

            var plain = await verify.Channels.SingleAsync(row => row.Id == bravo.Id);
            Assert.Equal("enabled", plain.Status);
        }
    }

    [Fact]
    public async Task BulkDeleteSoftDeletesOnlyTheLiveMatchesWithoutACountCheck()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var alpha = await SeedChannelAsync(context, name: "Alpha");
        var bravo = await SeedChannelAsync(context, name: "Bravo");

        // Delete().Where(IDIn(...)) does not verify that every id matched.
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await store.BulkDeleteAsync([alpha.Id, 424242]);
        var after = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await using var verify = db.Factory.CreateDbContext();
        var deleted = await verify.Channels.SingleAsync(row => row.Id == alpha.Id);
        Assert.InRange(deleted.DeletedAt, before, after);

        var live = await verify.Channels.SingleAsync(row => row.Id == bravo.Id);
        Assert.Equal(0, live.DeletedAt);
        Assert.Equal(UpdatedAt, live.UpdatedAt);
    }

    [Fact]
    public async Task ManageTagsRemovesThenAppendsAndSkipsUnchangedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var alpha = await SeedChannelAsync(context, name: "Alpha", tags: """["a","b"]""");
        var bravo = await SeedChannelAsync(context, name: "Bravo", tags: """["x","b","c"]""");

        // remove [a], add [b, c]: b is already present, so the result is [b, c].
        await store.ManageTagsAsync([alpha.Id, bravo.Id], ["b", "c"], ["a"]);

        await using (var verify = db.Factory.CreateDbContext())
        {
            // jsonb normalizes the array text.
            var updated = await verify.Channels.SingleAsync(row => row.Id == alpha.Id);
            Assert.Equal("""["b", "c"]""", updated.Tags);
            Assert.True(updated.UpdatedAt > UpdatedAt);

            // Bravo had no delta: the reference skips the update entirely,
            // leaving updated_at untouched.
            var untouched = await verify.Channels.SingleAsync(row => row.Id == bravo.Id);
            Assert.Equal("""["x", "b", "c"]""", untouched.Tags);
            Assert.Equal(UpdatedAt, untouched.UpdatedAt);
        }
    }

    [Fact]
    public async Task ManageTagsRejectsAMissingChannelBeforeWriting()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var alpha = await SeedChannelAsync(context, name: "Alpha", tags: """["a"]""");

        var mismatch = await Assert.ThrowsAsync<ExpectedRowsMismatchException>(
            () => store.ManageTagsAsync([alpha.Id, 424242], ["b"], []));
        Assert.Equal(2, mismatch.Expected);
        Assert.Equal(1, mismatch.Found);
        Assert.Equal("expected to find 2 channels, but found 1", mismatch.Message);

        // The whole transaction rolls back before the first update.
        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == alpha.Id);
        Assert.Equal("""["a"]""", reloaded.Tags);
        Assert.Equal(UpdatedAt, reloaded.UpdatedAt);
    }

    [Fact]
    public async Task ManageTagsTreatsNullTagsAsAnEmptyArray()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var alpha = await SeedChannelAsync(context, name: "Alpha", tags: null);

        await store.ManageTagsAsync([alpha.Id], ["new"], []);

        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == alpha.Id);
        Assert.Equal("""["new"]""", reloaded.Tags);
    }

    [Fact]
    public async Task CreateInsertsTheRowAndRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var record = new ChannelRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            DeletedAt = 0,
            Type = "openai",
            BaseUrl = "https://api.example/v1",
            Name = "Created",
            Status = "disabled",
            Credentials = """{"apiKey":"k"}""",
            DisabledApiKeys = "[]",
            SupportedModels = """["m"]""",
            ManualModels = "null",
            AutoSyncSupportedModels = true,
            AutoSyncModelPattern = "gpt-*",
            Tags = """["t"]""",
            DefaultTestModel = "m",
            Policies = """{"stream":"unlimited"}""",
            Settings = "null",
            OrderingWeight = 0,
            Remark = "r",
            Endpoints = """[{"api_format":"openai/chat_completions","transport":"http"}]""",
        };

        var created = await store.CreateAsync(record);

        Assert.Same(record, created);
        Assert.True(created!.Id > 0);

        // jsonb normalizes key order and whitespace; the values round-trip.
        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == record.Id);
        Assert.Equal(CreatedAt, reloaded.CreatedAt);
        Assert.Equal(UpdatedAt, reloaded.UpdatedAt);
        Assert.Equal(0, reloaded.DeletedAt);
        Assert.Equal("openai", reloaded.Type);
        Assert.Equal("https://api.example/v1", reloaded.BaseUrl);
        Assert.Equal("Created", reloaded.Name);
        Assert.Equal("disabled", reloaded.Status);
        Assert.Equal("""{"apiKey": "k"}""", reloaded.Credentials);
        Assert.Equal("[]", reloaded.DisabledApiKeys);
        Assert.Equal("""["m"]""", reloaded.SupportedModels);
        Assert.Equal("null", reloaded.ManualModels);
        Assert.True(reloaded.AutoSyncSupportedModels);
        Assert.Equal("gpt-*", reloaded.AutoSyncModelPattern);
        Assert.Equal("""["t"]""", reloaded.Tags);
        Assert.Equal("m", reloaded.DefaultTestModel);
        Assert.Equal("""{"stream": "unlimited"}""", reloaded.Policies);
        Assert.Equal("null", reloaded.Settings);
        Assert.Equal(0, reloaded.OrderingWeight);
        Assert.Equal("r", reloaded.Remark);
        // jsonb sorts object keys by length, then bytewise.
        Assert.Equal(
            """[{"transport": "http", "api_format": "openai/chat_completions"}]""",
            reloaded.Endpoints);
    }

    [Fact]
    public async Task CreateReturnsNullWhenALiveChannelOwnsTheName()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        await SeedChannelAsync(context, name: "Taken");

        var attempted = CreateRecord("Taken");
        Assert.Null(await store.CreateAsync(attempted));

        await using var verify = db.Factory.CreateDbContext();
        Assert.Equal(1, await verify.Channels.CountAsync(row => row.Name == "Taken"));
    }

    [Fact]
    public async Task CreateAllowsAReusedNameAfterSoftDelete()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        await SeedChannelAsync(context, name: "Recycled", deletedAt: 1234);

        var created = await store.CreateAsync(CreateRecord("Recycled"));

        Assert.NotNull(created);
        await using var verify = db.Factory.CreateDbContext();
        Assert.Equal(2, await verify.Channels.CountAsync(row => row.Name == "Recycled"));
        Assert.Equal(1, await verify.Channels.CountAsync(row => row.Name == "Recycled" && row.DeletedAt == 0));
    }

    [Fact]
    public async Task CreateWrapsAnInsertFailureWithTheReferencePrefix()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var broken = CreateRecord("Broken");
        broken.Settings = "not-json";

        var failure = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.CreateAsync(broken));
        Assert.StartsWith("failed to create channel: ", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Broken", failure.Message, StringComparison.Ordinal);
    }

    private static ChannelRecord CreateRecord(string name) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Type = "openai",
        Name = name,
        Status = "disabled",
        Credentials = "{}",
        DisabledApiKeys = "[]",
        SupportedModels = "[]",
        ManualModels = "null",
        AutoSyncModelPattern = string.Empty,
        DefaultTestModel = string.Empty,
        Policies = """{"stream":"unlimited"}""",
        Settings = "null",
        Tags = "[]",
        Endpoints = "[]",
    };

    private static async Task<ChannelRecord> SeedChannelAsync(
        AxonHubDbContext context,
        string name = "Alpha",
        string status = "disabled",
        string? errorMessage = null,
        DateTime? autoDisabledAt = null,
        DateTime? autoDisableExpiresAt = null,
        string? tags = null,
        long deletedAt = 0)
    {
        var channel = new ChannelRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            DeletedAt = deletedAt,
            Type = "openai",
            Name = name,
            Status = status,
            Credentials = "{}",
            SupportedModels = "null",
            AutoSyncModelPattern = "",
            DefaultTestModel = "",
            Tags = tags,
            ErrorMessage = errorMessage,
            AutoDisabledAt = autoDisabledAt,
            AutoDisableExpiresAt = autoDisableExpiresAt,
        };

        context.Channels.Add(channel);
        await context.SaveChangesAsync();
        return channel;
    }
}
