using System.Text.Json.Nodes;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AxonHub.Persistence.Tests;

/// <summary>
/// Behavioural tests for the ported <c>channel_model_prices</c> store and the
/// synchronous pricing seam (internal/server/biz/channel_price.go:
/// preloadModelPrices and the enabled-channel lookup in computeUsageCost).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ChannelModelPriceStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    private static readonly string[] AutoPricedModels = ["gpt-4o", "gpt-4o-mini"];
    private static readonly string[] AutoPriceReferences = ["auto-ref-1", "auto-ref-2"];

    private const string PriceJson =
        """{"items":[{"itemCode":"prompt_tokens","pricing":{"mode":"usage_per_unit","usagePerUnit":"0.03"}}]}""";

    [Fact]
    public async Task CreateAndListByChannelRoundTripsEveryColumn()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-roundtrip"));
        var price = NewPrice(channelId, "gpt-4o", "ref-roundtrip");
        var id = await store.CreateAsync(price);

        Assert.True(id > 0);

        price.Id = id;
        var actual = Assert.Single(await store.ListByChannelAsync(channelId));

        RecordAssert.Equivalent(price, actual);
    }

    [Fact]
    public async Task ListByChannelSkipsSoftDeletedRows()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-deleted"));
        var liveId = await store.CreateAsync(NewPrice(channelId, "gpt-4o", "ref-live"));
        var deletedId = await store.CreateAsync(NewPrice(channelId, "gpt-4o-mini", "ref-deleted"));

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"channel_model_prices\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = deletedId });
        }

        var rows = await store.ListByChannelAsync(channelId);

        Assert.Equal(new[] { liveId }, rows.Select(row => row.Id).ToArray());
    }

    [Fact]
    public async Task EnabledPricesRequireALiveEnabledChannel()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var enabledId = await channels.CreateAsync(NewChannel("price-enabled"));
        var enabledPrice = await store.CreateAsync(NewPrice(enabledId, "gpt-4o", "ref-enabled"));

        var disabled = NewChannel("price-disabled");
        disabled.Status = "disabled";
        var disabledId = await channels.CreateAsync(disabled);
        await store.CreateAsync(NewPrice(disabledId, "gpt-4o", "ref-disabled"));

        var deletedChannel = NewChannel("price-deleted-channel");
        var deletedChannelId = await channels.CreateAsync(deletedChannel);
        await store.CreateAsync(NewPrice(deletedChannelId, "gpt-4o", "ref-deleted-channel"));

        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"channels\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = deletedChannelId });
        }

        Assert.Equal(
            new[] { enabledPrice },
            (await store.ListEnabledChannelPricesAsync(enabledId)).Select(row => row.Id).ToArray());
        Assert.Empty(await store.ListEnabledChannelPricesAsync(disabledId));
        Assert.Empty(await store.ListEnabledChannelPricesAsync(deletedChannelId));
        Assert.Empty(await store.ListEnabledChannelPricesAsync(999_999));
    }

    [Fact]
    public async Task ReferenceIdIsGloballyUnique()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var firstChannel = await channels.CreateAsync(NewChannel("price-ref-1"));
        var secondChannel = await channels.CreateAsync(NewChannel("price-ref-2"));

        await store.CreateAsync(NewPrice(firstChannel, "gpt-4o", "ref-shared"));

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => store.CreateAsync(NewPrice(secondChannel, "gpt-4o", "ref-shared")));
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
    }

    [Fact]
    public async Task LiveModelPricePerChannelIsUnique()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-unique-model"));

        await store.CreateAsync(NewPrice(channelId, "gpt-4o", "ref-1"));

        var error = await Assert.ThrowsAsync<DbUpdateException>(
            () => store.CreateAsync(NewPrice(channelId, "gpt-4o", "ref-2")));
        var postgres = Assert.IsType<PostgresException>(error.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
    }

    [Fact]
    public async Task SyncSeamReadsPricesAndSystemValues()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        long channelId;

        await using (var context = db.Factory.CreateDbContext())
        {
            var channels = new ChannelStore(context);
            var store = new ChannelModelPriceStore(context);
            var systems = new SystemStore(context);

            channelId = await channels.CreateAsync(NewChannel("price-seam"));
            await store.CreateAsync(NewPrice(channelId, "gpt-4o", "ref-seam"));
            await systems.CreateAsync(new SystemRecord
            {
                CreatedAt = CreatedAt,
                UpdatedAt = UpdatedAt,
                Key = "system_inject_usage_cost",
                Value = "true",
            });
        }

        var source = new PostgresChannelPricingSource(db.Factory);

        var price = Assert.Single(source.ListEnabledChannelPrices(channelId));
        Assert.Equal("ref-seam", price.ReferenceId);
        Assert.Equal("true", source.GetSystemValue("system_inject_usage_cost"));
        Assert.Null(source.GetSystemValue("system_missing"));
    }

    [Fact]
    public async Task ApplyCreateWritesTheRowAndAnActiveVersion()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-apply-create"));
        var now = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        var results = await store.ApplyAsync(
            channelId,
            [
                new ChannelModelPriceChange(
                    ChannelModelPriceChangeType.Create,
                    "gpt-4o",
                    null,
                    PriceJson,
                    "ref-created",
                    null),
            ],
            now);

        var created = Assert.IsType<ChannelModelPriceRecord>(Assert.Single(results));
        Assert.True(created.Id > 0);
        Assert.Equal(channelId, created.ChannelId);
        Assert.Equal("gpt-4o", created.ModelId);
        Assert.Equal(PriceJson, created.Price);
        Assert.Equal("ref-created", created.ReferenceId);
        Assert.Equal(0, created.DeletedAt);

        RecordAssert.Equivalent(created, Assert.Single(await store.ListByChannelAsync(channelId)));

        var version = Assert.Single(await store.ListVersionsAsync(created.Id));
        Assert.Equal(channelId, version.ChannelId);
        Assert.Equal("gpt-4o", version.ModelId);
        Assert.Equal(created.Id, version.ChannelModelPriceId);
        AssertJsonEquivalent(PriceJson, version.Price);
        Assert.Equal("active", version.Status);
        Assert.Equal("ref-created", version.ReferenceId);
        Assert.Equal(now, version.EffectiveStartAt);
        Assert.Null(version.EffectiveEndAt);

        // The reference forces the channel row's updated_at so its reload
        // cache hook fires (channel_price.go:503-505).
        Assert.Equal(now, await ChannelUpdatedAtAsync(context, channelId));
    }

    [Fact]
    public async Task ApplyUpdateArchivesTheActiveVersionAndIssuesANewOne()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-apply-update"));
        var firstNow = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var secondNow = firstNow.AddHours(1);

        var created = Assert.IsType<ChannelModelPriceRecord>(Assert.Single(await store.ApplyAsync(
            channelId,
            [
                new ChannelModelPriceChange(
                    ChannelModelPriceChangeType.Create,
                    "gpt-4o",
                    null,
                    PriceJson,
                    "ref-old",
                    null),
            ],
            firstNow)));

        var newPriceJson = PriceJson.Replace("0.03", "0.04", StringComparison.Ordinal);
        var results = await store.ApplyAsync(
            channelId,
            [
                new ChannelModelPriceChange(
                    ChannelModelPriceChangeType.Update,
                    "gpt-4o",
                    created.Id,
                    newPriceJson,
                    "ref-new",
                    created),
            ],
            secondNow);

        var updated = Assert.IsType<ChannelModelPriceRecord>(Assert.Single(results));
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("ref-new", updated.ReferenceId);
        Assert.Equal(newPriceJson, updated.Price);
        Assert.Equal(secondNow, updated.UpdatedAt);

        var versions = await store.ListVersionsAsync(created.Id);
        Assert.Equal(2, versions.Count);
        Assert.Equal("archived", versions[0].Status);
        Assert.Equal("ref-old", versions[0].ReferenceId);
        Assert.Equal(secondNow, versions[0].EffectiveEndAt);
        Assert.Equal("active", versions[1].Status);
        Assert.Equal("ref-new", versions[1].ReferenceId);
        AssertJsonEquivalent(newPriceJson, versions[1].Price);
        Assert.Equal(secondNow, versions[1].EffectiveStartAt);
        Assert.Null(versions[1].EffectiveEndAt);

        Assert.Equal(secondNow, await ChannelUpdatedAtAsync(context, channelId));
    }

    [Fact]
    public async Task ApplySkipReturnsTheExistingRowWithoutWriting()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-apply-skip"));
        var firstNow = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var secondNow = firstNow.AddHours(1);

        var created = Assert.IsType<ChannelModelPriceRecord>(Assert.Single(await store.ApplyAsync(
            channelId,
            [
                new ChannelModelPriceChange(
                    ChannelModelPriceChangeType.Create,
                    "gpt-4o",
                    null,
                    PriceJson,
                    "ref-kept",
                    null),
            ],
            firstNow)));

        var results = await store.ApplyAsync(
            channelId,
            [
                new ChannelModelPriceChange(
                    ChannelModelPriceChangeType.Skip,
                    "gpt-4o",
                    created.Id,
                    null,
                    null,
                    created),
            ],
            secondNow);

        var skipped = Assert.IsType<ChannelModelPriceRecord>(Assert.Single(results));
        Assert.Same(created, skipped);
        Assert.Equal("ref-kept", skipped.ReferenceId);
        Assert.Equal(firstNow, skipped.UpdatedAt);

        var version = Assert.Single(await store.ListVersionsAsync(created.Id));
        Assert.Equal("active", version.Status);
        Assert.Equal(firstNow, version.EffectiveStartAt);
        Assert.Null(version.EffectiveEndAt);

        // The channel reload stamp still happens for a skip-only plan.
        Assert.Equal(secondNow, await ChannelUpdatedAtAsync(context, channelId));
    }

    [Fact]
    public async Task ApplyDeleteSoftDeletesTheRowAndArchivesItsVersion()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-apply-delete"));
        var firstNow = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var secondNow = firstNow.AddHours(1);

        var created = Assert.IsType<ChannelModelPriceRecord>(Assert.Single(await store.ApplyAsync(
            channelId,
            [
                new ChannelModelPriceChange(
                    ChannelModelPriceChangeType.Create,
                    "gpt-4o",
                    null,
                    PriceJson,
                    "ref-deleted",
                    null),
            ],
            firstNow)));

        var results = await store.ApplyAsync(
            channelId,
            [
                new ChannelModelPriceChange(
                    ChannelModelPriceChangeType.Delete,
                    "gpt-4o",
                    created.Id,
                    null,
                    null,
                    created),
            ],
            secondNow);

        Assert.Null(Assert.Single(results));
        Assert.Empty(await store.ListByChannelAsync(channelId));

        var version = Assert.Single(await store.ListVersionsAsync(created.Id));
        Assert.Equal("archived", version.Status);
        Assert.Equal(secondNow, version.EffectiveEndAt);

        await using var connection = await db.Factory.OpenAsync();
        var deletedAt = await connection.ExecuteScalarAsync<long>(
            "SELECT \"deleted_at\" FROM \"channel_model_prices\" WHERE \"id\" = @Id;",
            new { Id = created.Id });
        Assert.True(deletedAt > 0);
    }

    [Fact]
    public async Task ApplyMixedPlanWritesInOrderAndReturnsResultsInPlanOrder()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-apply-mixed"));
        var firstNow = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
        var secondNow = firstNow.AddHours(1);

        var created = (await store.ApplyAsync(
            channelId,
            [
                new ChannelModelPriceChange(
                    ChannelModelPriceChangeType.Create,
                    "gpt-4o",
                    null,
                    PriceJson,
                    "ref-a",
                    null),
                new ChannelModelPriceChange(
                    ChannelModelPriceChangeType.Create,
                    "gpt-4o-mini",
                    null,
                    PriceJson,
                    "ref-b",
                    null),
            ],
            firstNow)).Select(row => row!).OrderBy(row => row.Id).ToArray();

        var results = await store.ApplyAsync(
            channelId,
            [
                new ChannelModelPriceChange(
                    ChannelModelPriceChangeType.Update,
                    "gpt-4o",
                    created[0].Id,
                    PriceJson,
                    "ref-a2",
                    created[0]),
                new ChannelModelPriceChange(
                    ChannelModelPriceChangeType.Skip,
                    "gpt-4o-mini",
                    created[1].Id,
                    null,
                    null,
                    created[1]),
                new ChannelModelPriceChange(
                    ChannelModelPriceChangeType.Create,
                    "gpt-4.1",
                    null,
                    PriceJson,
                    "ref-c",
                    null),
            ],
            secondNow);

        Assert.Equal(3, results.Count);
        Assert.Equal(created[0].Id, results[0]!.Id);
        Assert.Equal("ref-a2", results[0]!.ReferenceId);
        Assert.Same(created[1], results[1]);
        Assert.Equal("gpt-4.1", results[2]!.ModelId);
        Assert.Equal("ref-c", results[2]!.ReferenceId);
    }

    [Fact]
    public async Task ApplyForAMissingChannelRollsBackThePlan()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelModelPriceStore(context);
        var now = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        var error = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.ApplyAsync(
                999_999,
                [
                    new ChannelModelPriceChange(
                        ChannelModelPriceChangeType.Create,
                        "gpt-4o",
                        null,
                        PriceJson,
                        "ref-orphan",
                        null),
                ],
                now));

        Assert.Equal("ent: channel not found", error.Message);

        // The create happened inside the store transaction, so the rollback
        // removes both the price row and its version.
        Assert.Empty(await store.ListByChannelAsync(999_999));
        await using var connection = await db.Factory.OpenAsync();
        var versions = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM \"channel_model_price_versions\";");
        Assert.Equal(0, versions);
    }

    [Fact]
    public async Task ApplyUpdateAndDeleteForAMissingRowKeepTheReferenceErrors()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);
        var now = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        var channelId = await channels.CreateAsync(NewChannel("price-apply-missing"));

        var updateError = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.ApplyAsync(
                channelId,
                [
                    new ChannelModelPriceChange(
                        ChannelModelPriceChangeType.Update,
                        "gpt-4o",
                        999_999,
                        PriceJson,
                        "ref-missing",
                        null),
                ],
                now));
        Assert.Equal(
            "failed to update channel model price: ent: channel model price not found",
            updateError.Message);

        var deleteError = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.ApplyAsync(
                channelId,
                [
                    new ChannelModelPriceChange(
                        ChannelModelPriceChangeType.Delete,
                        "gpt-4o",
                        999_999,
                        null,
                        null,
                        null),
                ],
                now));
        Assert.Equal(
            "failed to delete channel model price: ent: channel model price not found",
            deleteError.Message);
    }

    [Fact]
    public async Task EnsureAutoPricesFillsMissingPricesFromTheModelLibrary()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-ensure"));
        var channelUpdatedAt = await ChannelUpdatedAtAsync(context, channelId);

        SeedModel(context, "gpt-4o", "enabled", """{"priced":true}""");
        SeedModel(context, "gpt-4o-mini", "disabled", """{"priced":true}""");
        SeedModel(context, "archived-model", "archived", """{"priced":true}""");
        SeedModel(
            context,
            "deleted-model",
            "enabled",
            """{"priced":true}""",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        SeedModel(context, "unpriced-model", "enabled", """{"priced":false}""");
        await context.SaveChangesAsync();

        var nextReference = 0;
        var created = await store.EnsureAutoPricesAsync(
            channelId,
            [
                "gpt-4o",
                "gpt-4o-mini",
                "archived-model",
                "deleted-model",
                "unpriced-model",
                "missing-model",
                "gpt-4o",
                "",
            ],
            card => JsonNode.Parse(card)?["priced"]?.GetValue<bool>() == true
                ? PriceJson
                : null,
            () => "auto-ref-" + (++nextReference));

        Assert.True(created);
        Assert.Equal(2, nextReference);

        // Only enabled/disabled live models with a usable card are priced, in
        // first-occurrence order; duplicates and blank ids are dropped.
        var rows = await store.ListByChannelAsync(channelId);
        Assert.Equal(AutoPricedModels, rows.Select(row => row.ModelId).ToArray());
        Assert.Equal(AutoPriceReferences, rows.Select(row => row.ReferenceId).ToArray());

        foreach (var row in rows)
        {
            Assert.Equal(channelId, row.ChannelId);

            var version = Assert.Single(await store.ListVersionsAsync(row.Id));
            Assert.Equal(channelId, version.ChannelId);
            Assert.Equal(row.ModelId, version.ModelId);
            Assert.Equal(row.Id, version.ChannelModelPriceId);
            Assert.Equal("active", version.Status);
            Assert.Equal(row.ReferenceId, version.ReferenceId);
            AssertJsonEquivalent(PriceJson, version.Price);
            Assert.Null(version.EffectiveEndAt);
        }

        // ensureChannelModelPrices never touches the channel row.
        Assert.Equal(channelUpdatedAt, await ChannelUpdatedAtAsync(context, channelId));
    }

    [Fact]
    public async Task EnsureAutoPricesKeepsExistingPricesAndVersionHistory()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-ensure-idempotent"));
        SeedModel(context, "gpt-4o", "enabled", """{"priced":true}""");
        SeedModel(context, "gpt-4.1", "enabled", """{"priced":true}""");
        await context.SaveChangesAsync();

        var nextReference = 0;
        string NextReference() => "auto-ref-" + (++nextReference);

        Assert.True(await store.EnsureAutoPricesAsync(
            channelId, ["gpt-4o"], _ => PriceJson, NextReference));

        var manualId = await store.CreateAsync(NewPrice(channelId, "gpt-4.1", "manual-ref"));

        var again = await store.EnsureAutoPricesAsync(
            channelId,
            ["gpt-4o", "gpt-4.1", "gpt-4o"],
            _ => PriceJson,
            NextReference);

        Assert.False(again);
        Assert.Equal(1, nextReference);

        var rows = await store.ListByChannelAsync(channelId);
        Assert.Equal(2, rows.Count);
        Assert.Equal("auto-ref-1", rows.Single(row => row.ModelId == "gpt-4o").ReferenceId);
        var untouched = rows.Single(row => row.ModelId == "gpt-4.1");
        Assert.Equal(manualId, untouched.Id);
        Assert.Equal("manual-ref", untouched.ReferenceId);

        var versions = await store.ListVersionsAsync(manualId);
        Assert.Empty(versions);
        Assert.Single(await store.ListVersionsAsync(rows.Single(row => row.ModelId == "gpt-4o").Id));
    }

    [Fact]
    public async Task EnsureAutoPricesJoinsTheCallersTransaction()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-ensure-rollback"));
        SeedModel(context, "gpt-4o", "enabled", """{"priced":true}""");
        await context.SaveChangesAsync();

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            Assert.True(await store.EnsureAutoPricesAsync(
                channelId, ["gpt-4o"], _ => PriceJson, () => "auto-ref-rollback"));

            // The rows are visible inside the caller's transaction.
            Assert.Single(await store.ListByChannelAsync(channelId));

            await transaction.RollbackAsync();
        }

        Assert.Empty(await store.ListByChannelAsync(channelId));
        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM \"channel_model_price_versions\";"));
    }

    [Fact]
    public async Task EnsureAutoPricesWrapsCreationFailuresWithTheModelPrefix()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-ensure-duplicate-ref"));
        SeedModel(context, "model-a", "enabled", """{"priced":true}""");
        SeedModel(context, "model-b", "enabled", """{"priced":true}""");
        await context.SaveChangesAsync();

        // The candidate reference id is globally unique, so the second insert
        // fails and the whole standalone ensure transaction rolls back.
        var error = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.EnsureAutoPricesAsync(
                channelId,
                ["model-a", "model-b"],
                _ => PriceJson,
                () => "colliding-ref"));

        Assert.StartsWith(
            "failed to auto-configure channel model price: model_id=model-b: "
                + "failed to create channel model price: ",
            error.Message,
            StringComparison.Ordinal);

        Assert.Empty(await store.ListByChannelAsync(channelId));
        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM \"channel_model_price_versions\";"));
    }

    [Fact]
    public async Task EnsureAutoPricesWrapsModelCardFailuresWithTheLibraryPrefix()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-ensure-bad-card"));
        SeedModel(context, "gpt-4o", "enabled", """{"priced":true}""");
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.EnsureAutoPricesAsync(
                channelId,
                ["gpt-4o"],
                _ => throw new InvalidOperationException("bad model card"),
                () => "auto-ref"));

        // ent decodes model_card while scanning the query rows, so the failure
        // carries the library query prefix and nothing is written.
        Assert.Equal("failed to query model prices from model library: bad model card", error.Message);
        Assert.Empty(await store.ListByChannelAsync(channelId));
    }

    [Fact]
    public async Task ResolveAutoPriceTemplatesFiltersTheLibraryAndKeepsInputOrder()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelModelPriceStore(context);

        SeedModel(context, "gpt-4o", "enabled", """{"priced":true}""");
        SeedModel(context, "gpt-4o-mini", "disabled", """{"priced":true}""");
        SeedModel(context, "archived-model", "archived", """{"priced":true}""");
        SeedModel(
            context,
            "deleted-model",
            "enabled",
            """{"priced":true}""",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        SeedModel(context, "unpriced-model", "enabled", """{"priced":false}""");
        await context.SaveChangesAsync();

        var converted = new List<string>();
        var templates = await store.ResolveAutoPriceTemplatesAsync(
            [
                "gpt-4o",
                "",
                "gpt-4o-mini",
                "gpt-4o",
                "archived-model",
                "deleted-model",
                "unpriced-model",
                "missing-model",
            ],
            card =>
            {
                converted.Add(card);
                return JsonNode.Parse(card)?["priced"]?.GetValue<bool>() == true
                    ? PriceJson
                    : null;
            });

        // Only enabled/disabled live models with a usable card become
        // templates, in first-occurrence order (channel_price.go:142-191).
        Assert.Equal(AutoPricedModels, templates.Select(template => template.ModelId).ToArray());
        Assert.All(templates, template => Assert.Equal(PriceJson, template.PriceJson));

        // The card conversion runs once per library hit (the unpriced card
        // included), not per input entry.
        Assert.Equal(3, converted.Count);

        // Resolving templates is a read-only step.
        Assert.Equal(0, await context.ChannelModelPrices.CountAsync());
        Assert.Equal(0, await context.ChannelModelPriceVersions.CountAsync());
    }

    [Fact]
    public async Task ResolveAutoPriceTemplatesWrapsCardFailuresWithTheLibraryPrefix()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelModelPriceStore(context);

        SeedModel(context, "gpt-4o", "enabled", """{"priced":true}""");
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<StoreFailureException>(
            () => store.ResolveAutoPriceTemplatesAsync(
                ["gpt-4o"],
                _ => throw new InvalidOperationException("bad card")));

        Assert.Equal("failed to query model prices from model library: bad card", error.Message);
    }

    [Fact]
    public async Task ApplyAutoPriceTemplatesCreatesFreshRowsAndVersionsWithOneTimestamp()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-apply-templates"));

        var nextReference = 0;
        var changed = await store.ApplyAutoPriceTemplatesAsync(
            channelId,
            [
                new ChannelModelPriceTemplate("gpt-4o", PriceJson),
                new ChannelModelPriceTemplate("gpt-4o-mini", PriceJson),
            ],
            () => "tpl-ref-" + (++nextReference));

        Assert.True(changed);
        Assert.Equal(2, nextReference);

        var rows = await store.ListByChannelAsync(channelId);
        Assert.Equal(AutoPricedModels, rows.Select(row => row.ModelId).ToArray());
        Assert.Equal(["tpl-ref-1", "tpl-ref-2"], rows.Select(row => row.ReferenceId).ToArray());

        // applyChannelModelPriceTemplates stamps one now for the whole plan.
        var now = rows[0].CreatedAt;
        Assert.All(rows, row => Assert.Equal(now, row.CreatedAt));
        Assert.All(rows, row => Assert.Equal(now, row.UpdatedAt));

        foreach (var row in rows)
        {
            var version = Assert.Single(await store.ListVersionsAsync(row.Id));
            Assert.Equal("active", version.Status);
            Assert.Equal(row.ReferenceId, version.ReferenceId);
            Assert.Equal(now, version.EffectiveStartAt);
            Assert.Null(version.EffectiveEndAt);
            AssertJsonEquivalent(row.Price, version.Price);
        }
    }

    [Fact]
    public async Task ApplyAutoPriceTemplatesKeepsExistingRowsAndTheirVersions()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-apply-templates-idempotent"));

        var nextReference = 0;
        string NextReference() => "tpl-ref-" + (++nextReference);
        ChannelModelPriceTemplate[] templates = [new("gpt-4o", PriceJson)];

        Assert.True(await store.ApplyAutoPriceTemplatesAsync(channelId, templates, NextReference));
        var existing = Assert.Single(await store.ListByChannelAsync(channelId));

        Assert.False(await store.ApplyAutoPriceTemplatesAsync(channelId, templates, NextReference));

        // generateReferenceID still runs per template before the upsert; the
        // conflict simply creates nothing (channel_price.go:123-139).
        Assert.Equal(2, nextReference);
        var row = Assert.Single(await store.ListByChannelAsync(channelId));
        Assert.Equal(existing.Id, row.Id);
        Assert.Equal("tpl-ref-1", row.ReferenceId);
        Assert.Single(await store.ListVersionsAsync(row.Id));
    }

    [Fact]
    public async Task ApplyAutoPriceTemplatesWrapsFailuresAndJoinsTheCallerTransaction()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var channelId = await channels.CreateAsync(NewChannel("price-apply-templates-failure"));

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            // A constant reference id collides on the second insert.
            var error = await Assert.ThrowsAsync<StoreFailureException>(
                () => store.ApplyAutoPriceTemplatesAsync(
                    channelId,
                    [
                        new ChannelModelPriceTemplate("model-a", PriceJson),
                        new ChannelModelPriceTemplate("model-b", PriceJson),
                    ],
                    () => "same-ref"));

            Assert.StartsWith(
                "failed to auto-configure channel model price: model_id=model-b: "
                    + "failed to create channel model price: 23505: "
                    + "duplicate key value violates unique constraint",
                error.Message,
                StringComparison.Ordinal);

            await transaction.RollbackAsync();
        }

        Assert.Empty(await store.ListByChannelAsync(channelId));
        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM \"channel_model_price_versions\";"));
    }

    [Fact]
    public async Task CopyChannelPricesCopiesLiveSourceRowsWithFreshReferences()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var sourceId = await channels.CreateAsync(NewChannel("price-copy-source"));
        var targetId = await channels.CreateAsync(NewChannel("price-copy-target"));

        await store.CreateAsync(NewPrice(sourceId, "gpt-4o", "src-ref-1"));
        await store.CreateAsync(NewPrice(sourceId, "gpt-4o-mini", "src-ref-2"));
        var deletedId = await store.CreateAsync(NewPrice(sourceId, "gone-model", "src-ref-deleted"));
        await using (var connection = await db.Factory.OpenAsync())
        {
            await connection.ExecuteAsync(
                "UPDATE \"channel_model_prices\" SET \"deleted_at\" = @DeletedAt WHERE \"id\" = @Id;",
                new { DeletedAt = 1_700_000_000L, Id = deletedId });
        }

        var nextReference = 0;
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await store.CopyChannelPricesAsync(
                sourceId,
                targetId,
                () => "copy-ref-" + (++nextReference));
            await transaction.CommitAsync();
        }

        Assert.Equal(2, nextReference);

        var rows = await store.ListByChannelAsync(targetId);
        Assert.Equal(AutoPricedModels, rows.Select(row => row.ModelId).ToArray());
        Assert.Equal(["copy-ref-1", "copy-ref-2"], rows.Select(row => row.ReferenceId).ToArray());

        // DuplicateChannel stamps one time.Now() for every copied row
        // (channel_duplicate.go:59).
        var now = rows[0].CreatedAt;
        Assert.All(rows, row => Assert.Equal(now, row.CreatedAt));
        Assert.All(rows, row => Assert.Equal(now, row.UpdatedAt));

        foreach (var row in rows)
        {
            AssertJsonEquivalent(PriceJson, row.Price);
            var version = Assert.Single(await store.ListVersionsAsync(row.Id));
            Assert.Equal("active", version.Status);
            Assert.Equal(row.ReferenceId, version.ReferenceId);
            Assert.Equal(now, version.EffectiveStartAt);
        }

        // Soft-deleted source rows are not copied, and the source is untouched.
        Assert.Equal(2, (await store.ListByChannelAsync(sourceId)).Count);
    }

    [Fact]
    public async Task CopyChannelPricesWrapsFailuresAndLeavesTheTargetEmptyOnRollback()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var channels = new ChannelStore(context);
        var store = new ChannelModelPriceStore(context);

        var sourceId = await channels.CreateAsync(NewChannel("price-copy-failure-source"));
        var targetId = await channels.CreateAsync(NewChannel("price-copy-failure-target"));

        await store.CreateAsync(NewPrice(sourceId, "gpt-4o", "src-ref-a"));
        await store.CreateAsync(NewPrice(sourceId, "gpt-4o-mini", "src-ref-b"));

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var error = await Assert.ThrowsAsync<StoreFailureException>(
                () => store.CopyChannelPricesAsync(sourceId, targetId, () => "same-ref"));

            Assert.StartsWith(
                "failed to copy channel model price: failed to create channel model price: "
                    + "23505: duplicate key value violates unique constraint",
                error.Message,
                StringComparison.Ordinal);

            await transaction.RollbackAsync();
        }

        Assert.Empty(await store.ListByChannelAsync(targetId));
        await using var connection = await db.Factory.OpenAsync();
        Assert.Equal(
            0,
            await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM \"channel_model_price_versions\";"));
    }

    /// <summary>
    /// jsonb round-trips rewrite whitespace and key order, so stored prices are
    /// compared structurally (the reference only ever decodes them).
    /// </summary>
    private static void AssertJsonEquivalent(string expected, string actual)
    {
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)),
            "JSON differs: " + actual);
    }

    private static async Task<DateTime> ChannelUpdatedAtAsync(
        AxonHubDbContext context,
        long channelId)
    {
        await context.Entry(context.Channels.Local.Single(row => row.Id == channelId))
            .ReloadAsync();
        return context.Channels.Local.Single(row => row.Id == channelId).UpdatedAt;
    }

    private static ChannelRecord NewChannel(string name) => new()
    {
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
        Type = "openai",
        BaseUrl = "https://api.example.com/v1",
        Name = name,
        Status = "enabled",
        Credentials = """{"api_key":"sk-test"}""",
        DisabledApiKeys = "[]",
        SupportedModels = """["gpt-4o"]""",
        ManualModels = "[]",
        Tags = "[]",
        Settings = "{}",
        Endpoints = """["chat"]""",
    };

    private static ModelRecord SeedModel(
        AxonHubDbContext context,
        string modelId,
        string status,
        string modelCard,
        long deletedAt = 0)
    {
        var model = new ModelRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            DeletedAt = deletedAt,
            Developer = "test-developer",
            ModelId = modelId,
            Type = "chat",
            Name = modelId + "-name",
            Icon = "TestIcon",
            Group = "test-group",
            ModelCard = modelCard,
            Settings = "{}",
            Status = status,
        };

        context.Models.Add(model);
        return model;
    }

    private static ChannelModelPriceRecord NewPrice(long channelId, string modelId, string referenceId)
        => new()
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            ModelId = modelId,
            Price = PriceJson,
            ReferenceId = referenceId,
            ChannelId = channelId,
        };
}
