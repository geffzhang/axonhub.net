using System.Text.Json;
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

    [Fact]
    public async Task GetForUpdateReturnsOnlyLiveRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var live = await SeedChannelAsync(context, name: "Alpha");
        var deleted = await SeedChannelAsync(context, name: "Gone", deletedAt: 77);

        var loaded = await store.GetForUpdateAsync(live.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Alpha", loaded.Name);
        Assert.Equal("openai", loaded.Type);
        Assert.Equal(UpdatedAt, loaded.UpdatedAt);

        // The identity read applies the SoftDeleteMixin predicate.
        Assert.Null(await store.GetForUpdateAsync(424242));
        Assert.Null(await store.GetForUpdateAsync(deleted.Id));
    }

    [Fact]
    public async Task NameExistsExcludesTheChannelItselfAndSoftDeletedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var alpha = await SeedChannelAsync(context, name: "Alpha");
        await SeedChannelAsync(context, name: "Bravo");
        await SeedChannelAsync(context, name: "Recycled", deletedAt: 1234);

        // Name(...) AND IDNEQ(id): the row being updated never collides with itself.
        Assert.False(await store.NameExistsAsync(alpha.Id, "Alpha"));
        Assert.True(await store.NameExistsAsync(alpha.Id, "Bravo"));
        Assert.False(await store.NameExistsAsync(alpha.Id, "Recycled"));
        Assert.False(await store.NameExistsAsync(alpha.Id, "Unused"));
    }

    [Fact]
    public async Task UpdateWritesOnlyProvidedColumnsAndStampsUpdatedAt()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var channel = await SeedChannelAsync(
            context,
            name: "Alpha",
            baseUrl: "https://api.example/v1",
            supportedModels: """["a"]""",
            settings: """{"extraModelPrefix":"p"}""",
            credentials: """{"apiKey":"k"}""",
            remark: "old",
            autoSyncModelPattern: "gpt-*",
            endpoints: """[{"api_format":"openai/chat_completions"}]""",
            orderingWeight: 3,
            defaultTestModel: "m",
            errorMessage: "boom");

        // The service loads the snapshot separately; jsonb normalizes the text
        // on that read, so project the same way here.
        var snapshot = await store.GetForUpdateAsync(channel.Id);
        Assert.NotNull(snapshot);

        var updated = await store.UpdateAsync(
            snapshot,
            new ChannelUpdateRecord
            {
                Name = "Renamed",
                Remark = "new",
                OrderingWeight = 7,
                ClearErrorMessage = true,
            });

        Assert.NotNull(updated);
        Assert.Equal(channel.Id, updated.Id);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal("new", updated.Remark);
        Assert.Equal(7, updated.OrderingWeight);
        Assert.Null(updated.ErrorMessage);
        Assert.True(updated.UpdatedAt > UpdatedAt);

        // Untouched columns keep the loaded snapshot.
        Assert.Equal("openai", updated.Type);
        Assert.Equal("https://api.example/v1", updated.BaseUrl);
        Assert.Equal("m", updated.DefaultTestModel);
        Assert.Equal("""["a"]""", updated.SupportedModels);
        Assert.Equal("""{"extraModelPrefix": "p"}""", updated.Settings);
        Assert.Equal("""{"apiKey": "k"}""", updated.Credentials);
        Assert.Equal("gpt-*", updated.AutoSyncModelPattern);
        Assert.Equal(0, updated.DeletedAt);

        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == channel.Id);
        Assert.Equal("Renamed", reloaded.Name);
        Assert.Equal("new", reloaded.Remark);
        Assert.Equal(7, reloaded.OrderingWeight);
        Assert.Null(reloaded.ErrorMessage);
        Assert.True(reloaded.UpdatedAt > UpdatedAt);

        // Not-set columns are literally unchanged in the row.
        Assert.Equal("openai", reloaded.Type);
        Assert.Equal("https://api.example/v1", reloaded.BaseUrl);
        Assert.Equal("m", reloaded.DefaultTestModel);
        Assert.Equal("""["a"]""", reloaded.SupportedModels);
        Assert.Equal("""{"extraModelPrefix": "p"}""", reloaded.Settings);
        Assert.Equal("""{"apiKey": "k"}""", reloaded.Credentials);
        Assert.Equal("gpt-*", reloaded.AutoSyncModelPattern);
        Assert.Equal(
            """[{"api_format": "openai/chat_completions"}]""",
            reloaded.Endpoints);
    }

    [Fact]
    public async Task UpdateClearFlagsWriteNulls()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var channel = await SeedChannelAsync(
            context,
            name: "Alpha",
            settings: """{"extraModelPrefix":"p"}""",
            remark: "old",
            autoSyncModelPattern: "gpt-*",
            errorMessage: "boom");

        var updated = await store.UpdateAsync(
            channel,
            new ChannelUpdateRecord
            {
                ClearRemark = true,
                ClearAutoSyncModelPattern = true,
                ClearErrorMessage = true,
                SettingsJson = "null",
            });

        Assert.NotNull(updated);
        Assert.Null(updated.Remark);
        Assert.Null(updated.AutoSyncModelPattern);
        Assert.Null(updated.ErrorMessage);

        // ent's ClearField stores NULL; SetSettings(nil) stores JSON null.
        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == channel.Id);
        Assert.Null(reloaded.Remark);
        Assert.Null(reloaded.AutoSyncModelPattern);
        Assert.Null(reloaded.ErrorMessage);
        Assert.Equal("null", reloaded.Settings);
    }

    [Fact]
    public async Task UpdateHonoursTheExpectedVersionGuard()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var channel = await SeedChannelAsync(context, name: "Alpha");

        // Identity updates match only while updated_at is the loaded value.
        var stale = await store.UpdateAsync(
            channel,
            new ChannelUpdateRecord
            {
                Name = "Stale",
                ExpectedUpdatedAt = UpdatedAt.AddMinutes(-1),
            });
        Assert.Null(stale);

        await using (var verify = db.Factory.CreateDbContext())
        {
            var untouched = await verify.Channels.SingleAsync(row => row.Id == channel.Id);
            Assert.Equal("Alpha", untouched.Name);
            Assert.Equal(UpdatedAt, untouched.UpdatedAt);
        }

        var fresh = await store.UpdateAsync(
            channel,
            new ChannelUpdateRecord
            {
                Name = "Fresh",
                ExpectedUpdatedAt = UpdatedAt,
            });
        Assert.NotNull(fresh);

        await using (var verify = db.Factory.CreateDbContext())
        {
            var reloaded = await verify.Channels.SingleAsync(row => row.Id == channel.Id);
            Assert.Equal("Fresh", reloaded.Name);
            Assert.True(reloaded.UpdatedAt > UpdatedAt);
        }
    }

    [Fact]
    public async Task UnguardedUpdateIgnoresAStaleLoadedVersion()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var channel = await SeedChannelAsync(context, name: "Alpha");

        // A non-identity update bumps updated_at behind the loaded snapshot.
        await store.UpdateStatusAsync(channel.Id, "enabled");

        // Remark-only updates carry no version guard, like the reference.
        var updated = await store.UpdateAsync(
            channel,
            new ChannelUpdateRecord { Remark = "new" });
        Assert.NotNull(updated);

        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == channel.Id);
        Assert.Equal("new", reloaded.Remark);
        Assert.Equal("Alpha", reloaded.Name);
        Assert.Equal("enabled", reloaded.Status);
    }

    [Fact]
    public async Task UpdateReturnsNullForMissingAndSoftDeletedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var deleted = await SeedChannelAsync(context, name: "Gone", deletedAt: 55);

        Assert.Null(await store.UpdateAsync(
            new ChannelRecord { Id = 424242 },
            new ChannelUpdateRecord { Name = "Nobody" }));
        Assert.Null(await store.UpdateAsync(
            deleted,
            new ChannelUpdateRecord { Name = "Resurrected" }));

        await using var verify = db.Factory.CreateDbContext();
        var untouched = await verify.Channels.SingleAsync(row => row.Id == deleted.Id);
        Assert.Equal("Gone", untouched.Name);
        Assert.Equal(55, untouched.DeletedAt);
        Assert.Equal(UpdatedAt, untouched.UpdatedAt);
    }

    [Fact]
    public async Task UpdateWrapsAUniqueNameViolationWithTheReferencePrefix()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        await SeedChannelAsync(context, name: "Alpha");
        var bravo = await SeedChannelAsync(context, name: "Bravo");

        // The service checks the live name first; the (name, deleted_at)
        // unique index still guards the store against the create/update race.
        var failure = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.UpdateAsync(bravo, new ChannelUpdateRecord { Name = "Alpha" }));
        Assert.StartsWith("failed to update channel: ", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BulkUpdateOrderingWritesEveryWeightAndReturnsTheSavedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var alpha = await SeedChannelAsync(context, name: "Alpha", orderingWeight: 1);
        var bravo = await SeedChannelAsync(context, name: "Bravo", orderingWeight: 2);

        var updated = await store.BulkUpdateOrderingAsync(
        [
            new ChannelOrderingItem(alpha.Id, 100),
            new ChannelOrderingItem(bravo.Id, 50),
        ]);

        // ent returns one saved entity per input, in input order.
        Assert.Equal(2, updated.Count);
        Assert.Equal(alpha.Id, updated[0].Id);
        Assert.Equal(100, updated[0].OrderingWeight);
        Assert.Equal(bravo.Id, updated[1].Id);
        Assert.Equal(50, updated[1].OrderingWeight);

        await using var verify = db.Factory.CreateDbContext();
        var reloadedAlpha = await verify.Channels.SingleAsync(row => row.Id == alpha.Id);
        Assert.Equal(100, reloadedAlpha.OrderingWeight);
        Assert.True(reloadedAlpha.UpdatedAt > UpdatedAt);

        var reloadedBravo = await verify.Channels.SingleAsync(row => row.Id == bravo.Id);
        Assert.Equal(50, reloadedBravo.OrderingWeight);
        Assert.True(reloadedBravo.UpdatedAt > UpdatedAt);
    }

    [Fact]
    public async Task BulkUpdateOrderingRollsBackTheBatchWhenARowIsMissing()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var alpha = await SeedChannelAsync(context, name: "Alpha", orderingWeight: 1);
        var deleted = await SeedChannelAsync(
            context,
            name: "Gone",
            deletedAt: 77,
            orderingWeight: 9);

        // UpdateOneID(...).Save reports a missing row, and the GraphQL
        // Transactioner rolls back the updates that already ran.
        var missing = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.BulkUpdateOrderingAsync(
            [
                new ChannelOrderingItem(alpha.Id, 100),
                new ChannelOrderingItem(424242, 7),
            ]));
        Assert.Equal("failed to update channel 424242: ent: channel not found", missing.Message);

        // The SoftDeleteMixin predicate hides deleted rows from UpdateOneID.
        var hidden = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.BulkUpdateOrderingAsync([new ChannelOrderingItem(deleted.Id, 5)]));
        Assert.Equal($"failed to update channel {deleted.Id}: ent: channel not found", hidden.Message);

        await using var verify = db.Factory.CreateDbContext();
        var reloadedAlpha = await verify.Channels.SingleAsync(row => row.Id == alpha.Id);
        Assert.Equal(1, reloadedAlpha.OrderingWeight);
        Assert.Equal(UpdatedAt, reloadedAlpha.UpdatedAt);

        var reloadedDeleted = await verify.Channels.SingleAsync(row => row.Id == deleted.Id);
        Assert.Equal(9, reloadedDeleted.OrderingWeight);
        Assert.Equal(77, reloadedDeleted.DeletedAt);
    }

    [Fact]
    public async Task BulkUpdateAutoDisableWriteRulesKeepsStreamAndWritesNormalizedRules()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var channel = await SeedChannelAsync(
            context,
            policies: """{"stream":"require","apiKeyAutoDisableMode":"off"}""");

        var updated = await store.BulkUpdateAutoDisableAsync(
            [channel.Id],
            "custom",
            """[{"statusCodes":[401],"times":2,"action":"permanent_disable"}]""");

        var returned = Assert.Single(updated);
        var returnedPolicies = ParsePolicyJson(returned.Policies);
        Assert.Equal("require", returnedPolicies.GetProperty("stream").GetString());
        Assert.Equal("custom", returnedPolicies.GetProperty("apiKeyAutoDisableMode").GetString());
        var returnedRules = returnedPolicies.GetProperty("apiKeyAutoDisableRules");
        Assert.Equal(401, returnedRules[0].GetProperty("statusCodes")[0].GetInt32());
        Assert.Equal(2, returnedRules[0].GetProperty("times").GetInt32());
        Assert.Equal("permanent_disable", returnedRules[0].GetProperty("action").GetString());

        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == channel.Id);
        var stored = ParsePolicyJson(reloaded.Policies);
        Assert.Equal("require", stored.GetProperty("stream").GetString());
        Assert.Equal("custom", stored.GetProperty("apiKeyAutoDisableMode").GetString());
        Assert.Equal(
            "permanent_disable",
            stored.GetProperty("apiKeyAutoDisableRules")[0].GetProperty("action").GetString());
        Assert.True(reloaded.UpdatedAt > UpdatedAt);
    }

    [Fact]
    public async Task BulkUpdateAutoDisableInheritClearsTheRulesButKeepsStream()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var channel = await SeedChannelAsync(
            context,
            policies:
                """{"stream":"unlimited","apiKeyAutoDisableMode":"custom","apiKeyAutoDisableRules":[{"times":3,"action":"permanent_disable"}]}""");

        await store.BulkUpdateAutoDisableAsync([channel.Id], "inherit", null);

        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == channel.Id);
        var stored = ParsePolicyJson(reloaded.Policies);
        Assert.Equal("unlimited", stored.GetProperty("stream").GetString());
        Assert.Equal("inherit", stored.GetProperty("apiKeyAutoDisableMode").GetString());
        Assert.False(stored.TryGetProperty("apiKeyAutoDisableRules", out _));
    }

    [Fact]
    public async Task BulkUpdateAutoDisableOffKeepsTheStoredRules()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var channel = await SeedChannelAsync(
            context,
            policies:
                """{"stream":"unlimited","apiKeyAutoDisableMode":"custom","apiKeyAutoDisableRules":[{"statusCodes":[429],"times":3,"action":"permanent_disable"}]}""");

        // off ignores the submitted rules: the store receives none and the
        // channel keeps its own.
        await store.BulkUpdateAutoDisableAsync([channel.Id], "off", null);

        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == channel.Id);
        var stored = ParsePolicyJson(reloaded.Policies);
        Assert.Equal("off", stored.GetProperty("apiKeyAutoDisableMode").GetString());
        var rules = stored.GetProperty("apiKeyAutoDisableRules");
        Assert.Equal(429, rules[0].GetProperty("statusCodes")[0].GetInt32());
        Assert.Equal(3, rules[0].GetProperty("times").GetInt32());
    }

    [Fact]
    public async Task BulkUpdateAutoDisableTreatsNullPoliciesAsAnEmptyObject()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var channel = await SeedChannelAsync(context, policies: null);

        await store.BulkUpdateAutoDisableAsync(
            [channel.Id],
            "custom",
            """[{"times":1,"action":"permanent_disable"}]""");

        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == channel.Id);
        var stored = ParsePolicyJson(reloaded.Policies);
        Assert.Equal("custom", stored.GetProperty("apiKeyAutoDisableMode").GetString());
        Assert.Equal(
            1,
            stored.GetProperty("apiKeyAutoDisableRules")[0].GetProperty("times").GetInt32());
        Assert.False(stored.TryGetProperty("stream", out _));
    }

    [Fact]
    public async Task BulkUpdateAutoDisableVerifiesTheExactIdCountBeforeWriting()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var alpha = await SeedChannelAsync(context, name: "Alpha");
        var deleted = await SeedChannelAsync(context, name: "Gone", deletedAt: 77);

        // Duplicates and soft-deleted ids both fail the live row-count check.
        var duplicates = await Assert.ThrowsAsync<ExpectedRowsMismatchException>(
            () => store.BulkUpdateAutoDisableAsync([alpha.Id, alpha.Id], "inherit", null));
        Assert.Equal("expected to find 2 channels, but found 1", duplicates.Message);

        var withDeleted = await Assert.ThrowsAsync<ExpectedRowsMismatchException>(
            () => store.BulkUpdateAutoDisableAsync([alpha.Id, deleted.Id], "inherit", null));
        Assert.Equal("expected to find 2 channels, but found 1", withDeleted.Message);

        // The failed check leaves every row untouched.
        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == alpha.Id);
        Assert.Equal("""{"stream": "unlimited"}""", reloaded.Policies);
        Assert.Equal(UpdatedAt, reloaded.UpdatedAt);
    }

    [Fact]
    public async Task GetReturnsOnlyLiveChannelRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var live = await SeedChannelAsync(context, name: "Live");
        var deleted = await SeedChannelAsync(context, name: "Gone", deletedAt: 77);

        var loaded = await store.GetAsync(live.Id);
        Assert.NotNull(loaded);
        Assert.Equal("Live", loaded!.Name);
        Assert.Equal(live.UpdatedAt, loaded.UpdatedAt);

        Assert.Null(await store.GetAsync(deleted.Id));
        Assert.Null(await store.GetAsync(424242));
    }

    [Fact]
    public async Task SaveEndpointsWritesTheJsonAndStampsUpdatedAt()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var channel = await SeedChannelAsync(
            context,
            name: "Alpha",
            settings: """{"extraModelPrefix":"cfg"}""",
            endpoints: "[]");

        var snapshot = await store.GetAsync(channel.Id);
        Assert.NotNull(snapshot);

        const string json =
            """[{"api_format":"openai/chat_completions"},{"api_format":"openai/embedding","path":"/custom"}]""";

        var saved = await store.SaveEndpointsAsync(snapshot!, json);
        Assert.NotNull(saved);
        Assert.Equal(json, saved!.Endpoints);
        Assert.True(saved.UpdatedAt > channel.UpdatedAt);

        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == channel.Id);

        using var document = JsonDocument.Parse(reloaded.Endpoints!);
        var endpoints = document.RootElement;
        Assert.Equal(2, endpoints.GetArrayLength());
        Assert.Equal(
            "openai/chat_completions",
            endpoints[0].GetProperty("api_format").GetString());
        Assert.Equal("/custom", endpoints[1].GetProperty("path").GetString());

        // SaveChannelEndpoints writes only the endpoints column; jsonb
        // re-renders the untouched settings with its own spacing.
        using var settingsDocument = JsonDocument.Parse(reloaded.Settings!);
        Assert.Equal(
            "cfg",
            settingsDocument.RootElement.GetProperty("extraModelPrefix").GetString());
        Assert.True(reloaded.UpdatedAt > channel.UpdatedAt);
    }

    [Fact]
    public async Task SaveEndpointsHonoursTheVersionGuardAndMissingRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var channel = await SeedChannelAsync(context, name: "Alpha", endpoints: "[]");
        var deleted = await SeedChannelAsync(context, name: "Gone", deletedAt: 77);

        var snapshot = await store.GetAsync(channel.Id);
        Assert.NotNull(snapshot);
        snapshot!.UpdatedAt = snapshot.UpdatedAt.AddSeconds(-1);

        Assert.Null(await store.SaveEndpointsAsync(snapshot, """[{"api_format":"openai/chat_completions"}]"""));

        // A vanished or soft-deleted id never matches the guarded UPDATE.
        var liveSnapshot = await store.GetAsync(channel.Id);
        Assert.NotNull(liveSnapshot);
        Assert.Null(await store.SaveEndpointsAsync(
            new ChannelRecord
            {
                Id = 424242,
                UpdatedAt = liveSnapshot!.UpdatedAt,
                DeletedAt = 0,
            },
            "[]"));
        Assert.Null(await store.SaveEndpointsAsync(
            new ChannelRecord
            {
                Id = deleted.Id,
                UpdatedAt = deleted.UpdatedAt,
                DeletedAt = deleted.DeletedAt,
            },
            "[]"));

        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == channel.Id);
        Assert.Equal("[]", reloaded.Endpoints);
        Assert.Equal(UpdatedAt, reloaded.UpdatedAt);
    }

    [Fact]
    public async Task SaveEndpointsAcceptsAnEmptyArray()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelWriteStore(context);

        var channel = await SeedChannelAsync(
            context,
            name: "Alpha",
            endpoints: """[{"api_format":"openai/chat_completions"}]""");

        var snapshot = await store.GetAsync(channel.Id);
        var saved = await store.SaveEndpointsAsync(snapshot!, "[]");

        Assert.NotNull(saved);
        Assert.Equal("[]", saved!.Endpoints);

        await using var verify = db.Factory.CreateDbContext();
        var reloaded = await verify.Channels.SingleAsync(row => row.Id == channel.Id);
        Assert.Equal("[]", reloaded.Endpoints);
    }

    private static JsonElement ParsePolicyJson(string? json)
    {
        using var document = JsonDocument.Parse(json!);
        return document.RootElement.Clone();
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
        long deletedAt = 0,
        string type = "openai",
        string? baseUrl = null,
        string credentials = "{}",
        string supportedModels = "null",
        string? settings = null,
        string? policies = """{"stream":"unlimited"}""",
        string? remark = null,
        string autoSyncModelPattern = "",
        string? endpoints = "[]",
        long orderingWeight = 0,
        string defaultTestModel = "")
    {
        var channel = new ChannelRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            DeletedAt = deletedAt,
            Type = type,
            BaseUrl = baseUrl,
            Name = name,
            Status = status,
            Credentials = credentials,
            SupportedModels = supportedModels,
            AutoSyncModelPattern = autoSyncModelPattern,
            DefaultTestModel = defaultTestModel,
            Tags = tags,
            Policies = policies,
            Settings = settings,
            OrderingWeight = orderingWeight,
            Endpoints = endpoints,
            Remark = remark,
            ErrorMessage = errorMessage,
            AutoDisabledAt = autoDisabledAt,
            AutoDisableExpiresAt = autoDisableExpiresAt,
        };

        context.Channels.Add(channel);
        await context.SaveChangesAsync();
        return channel;
    }
}
