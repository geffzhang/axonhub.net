using Microsoft.EntityFrameworkCore;

namespace AxonHub.Persistence.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ChannelAdminStoreTests(PostgresFixture fixture)
{
    private static readonly DateTime CreatedAt = new(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
    private static readonly DateTime UpdatedAt = new(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task ListAppliesFiltersKeysetPaginationAndOrdering()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelAdminStore(context);

        var alpha = await SeedChannelAsync(context, "openai", "Alpha", "enabled", weight: 3, tags: """["prod"]""");
        var bravo = await SeedChannelAsync(context, "anthropic", "Bravo", "disabled", weight: 1, tags: """["dev"]""");
        var charlie = await SeedChannelAsync(context, "openai", "Charlie", "enabled", weight: 2);
        await SeedChannelAsync(context, "openai", "Deleted", "enabled", weight: 0, deletedAt: 1234);

        var page = await store.ListAsync(new ChannelListQuery { First = 2 });
        Assert.Equal([alpha.Id, bravo.Id], page.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(page.HasNextPage);
        Assert.False(page.HasPreviousPage);
        Assert.Equal(3, page.TotalCount);

        var next = await store.ListAsync(new ChannelListQuery
        {
            After = new ListCursor(bravo.Id, null),
            First = 2,
        });
        Assert.Equal([charlie.Id], next.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(next.HasPreviousPage);

        var activated = await store.ListAsync(new ChannelListQuery
        {
            Where = new ChannelListFilter { StatusIn = ["enabled"] },
        });
        Assert.Equal([alpha.Id, charlie.Id], activated.Nodes.Select(static row => row.Id).ToArray());
        Assert.Equal(2, activated.TotalCount);

        var folded = await store.ListAsync(new ChannelListQuery
        {
            Where = new ChannelListFilter { NameContainsFold = "ALPH" },
        });
        Assert.Equal([alpha.Id], folded.Nodes.Select(static row => row.Id).ToArray());

        var tagged = await store.ListAsync(new ChannelListQuery { HasTag = "prod" });
        Assert.Equal([alpha.Id], tagged.Nodes.Select(static row => row.Id).ToArray());

        var noError = await store.ListAsync(new ChannelListQuery
        {
            Where = new ChannelListFilter { ErrorMessageIsNil = true },
        });
        Assert.Equal(3, noError.TotalCount);

        // ORDERING_WEIGHT DESC issues an integer keyset cursor.
        var byWeight = await store.ListAsync(new ChannelListQuery
        {
            SortField = ChannelSortField.OrderingWeight,
            Direction = SortDirection.Desc,
            First = 1,
        });
        Assert.Equal([alpha.Id], byWeight.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(byWeight.HasNextPage);

        var afterWeight = await store.ListAsync(new ChannelListQuery
        {
            SortField = ChannelSortField.OrderingWeight,
            Direction = SortDirection.Desc,
            After = new ListCursor(alpha.Id, alpha.OrderingWeight),
            First = 2,
        });
        Assert.Equal([charlie.Id, bravo.Id], afterWeight.Nodes.Select(static row => row.Id).ToArray());

        // last walks backwards and still returns the tail in ascending order.
        var tail = await store.ListAsync(new ChannelListQuery
        {
            SortField = ChannelSortField.Name,
            Direction = SortDirection.Asc,
            Last = 2,
        });
        Assert.Equal([bravo.Id, charlie.Id], tail.Nodes.Select(static row => row.Id).ToArray());
        Assert.True(tail.HasPreviousPage);
        Assert.False(tail.HasNextPage);
    }

    [Fact]
    public async Task ListAllOrdersOnlyWhenTheCallerSuppliedAnOrder()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelAdminStore(context);

        var alpha = await SeedChannelAsync(context, "openai", "Alpha", "enabled", weight: 2);
        var bravo = await SeedChannelAsync(context, "openai", "Bravo", "enabled", weight: 2);
        var charlie = await SeedChannelAsync(context, "openai", "Charlie", "enabled", weight: 1);

        var unordered = await store.ListAllAsync(new ChannelListQuery());
        Assert.Equal(
            [alpha.Id, bravo.Id, charlie.Id],
            unordered.Select(static row => row.Id).ToArray());

        var ordered = await store.ListAllAsync(new ChannelListQuery
        {
            SortField = ChannelSortField.OrderingWeight,
            Direction = SortDirection.Desc,
            Ordered = true,
        });

        // Equal weights fall back to id ASC; the orderBy field keeps its direction.
        Assert.Equal(
            [alpha.Id, bravo.Id, charlie.Id],
            ordered.Select(static row => row.Id).ToArray());

        var filtered = await store.ListAllAsync(new ChannelListQuery
        {
            Where = new ChannelListFilter { Name = "Bravo" },
            Ordered = true,
            SortField = ChannelSortField.Name,
        });
        Assert.Equal([bravo.Id], filtered.Select(static row => row.Id).ToArray());
    }

    [Fact]
    public async Task SummariesSelectEnabledAndDisabledOrderedByWeight()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelAdminStore(context);

        var enabled = await SeedChannelAsync(context, "openai", "Enabled", "enabled", weight: 9);
        var disabled = await SeedChannelAsync(context, "anthropic", "Disabled", "disabled", weight: 1);
        var archived = await SeedChannelAsync(context, "openai", "Archived", "archived", weight: 100);
        await SeedChannelAsync(context, "openai", "Gone", "enabled", weight: 50, deletedAt: 42);

        var live = await store.ListSummariesAsync(includeArchived: false);
        Assert.Equal([enabled.Id, disabled.Id], live.Select(static row => row.Id).ToArray());

        var includingArchived = await store.ListSummariesAsync(includeArchived: true);
        Assert.Equal(
            [archived.Id, enabled.Id, disabled.Id],
            includingArchived.Select(static row => row.Id).ToArray());

        // The project branch's system-bypass query is enabled-only.
        var enabledOnly = await store.ListEnabledSummariesAsync();
        Assert.Equal([enabled.Id], enabledOnly.Select(static row => row.Id).ToArray());
    }

    [Fact]
    public async Task TagRowsSkipArchivedRowsAndCountsApplyTheStatusPredicate()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelAdminStore(context);

        await SeedChannelAsync(context, "openai", "One", "enabled", tags: """["prod","eu"]""");
        await SeedChannelAsync(context, "openai", "Two", "disabled", tags: """["dev"]""");
        await SeedChannelAsync(context, "anthropic", "Three", "enabled");
        await SeedChannelAsync(context, "openai", "Archived", "archived", tags: """["gone"]""");
        await SeedChannelAsync(context, "openai", "Deleted", "enabled", tags: """["gone"]""", deletedAt: 9);

        // jsonb storage normalizes whitespace inside the array text.
        var tagRows = await store.ListTagSourcesAsync();
        Assert.Equal(3, tagRows.Count);
        Assert.Equal("""["prod", "eu"]""", tagRows[0].Tags);
        Assert.Equal("""["dev"]""", tagRows[1].Tags);
        Assert.Null(tagRows[2].Tags);

        var allLive = await store.CountByTypeAsync(statusIn: null);
        Assert.Equal(
            [("anthropic", 1), ("openai", 2)],
            allLive.Select(static row => (row.Type, row.Count)).ToArray());

        var enabledOnly = await store.CountByTypeAsync(statusIn: ["enabled"]);
        Assert.Equal(
            [("anthropic", 1), ("openai", 1)],
            enabledOnly.Select(static row => (row.Type, row.Count)).ToArray());

        // An explicitly empty statusIn falls back to "not archived".
        var emptyStatuses = await store.CountByTypeAsync(statusIn: []);
        Assert.Equal(3, emptyStatuses.Sum(static row => row.Count));
    }

    [Fact]
    public async Task ListAppliesIdTimeAndOrderingWeightPredicates()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelAdminStore(context);

        var t0 = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var t1 = new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2026, 3, 3, 0, 0, 0, DateTimeKind.Utc);

        var alpha = await SeedChannelAsync(
            context, "openai", "Alpha", "enabled", weight: 3, createdAt: t0, updatedAt: t0);
        var bravo = await SeedChannelAsync(
            context, "anthropic", "Bravo", "disabled", weight: 1, createdAt: t1, updatedAt: t1);
        var charlie = await SeedChannelAsync(
            context, "openai", "Charlie", "enabled", weight: 2, createdAt: t2, updatedAt: t2);

        async Task<long[]> QueryAsync(ChannelListFilter where)
            => (await store.ListAsync(new ChannelListQuery { Where = where }))
                .Nodes.Select(static row => row.Id).ToArray();

        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { Id = alpha.Id }));
        Assert.Equal([bravo.Id, charlie.Id], await QueryAsync(new ChannelListFilter { IdNEQ = alpha.Id }));
        Assert.Equal(
            [alpha.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { IdIn = [alpha.Id, charlie.Id] }));
        Assert.Equal(
            [charlie.Id],
            await QueryAsync(new ChannelListFilter { IdNotIn = [alpha.Id, bravo.Id] }));
        Assert.Equal([bravo.Id, charlie.Id], await QueryAsync(new ChannelListFilter { IdGT = alpha.Id }));
        Assert.Equal(
            [alpha.Id, bravo.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { IdGTE = alpha.Id }));
        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { IdLT = bravo.Id }));
        Assert.Equal([alpha.Id, bravo.Id], await QueryAsync(new ChannelListFilter { IdLTE = bravo.Id }));

        // ent.StringIn() with an empty list is false; NotIn() is true.
        Assert.Empty(await QueryAsync(new ChannelListFilter { IdIn = [] }));
        Assert.Equal(3, (await QueryAsync(new ChannelListFilter { IdNotIn = [] })).Length);

        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { CreatedAt = t0 }));
        Assert.Equal(
            [bravo.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { CreatedAtNEQ = t0 }));
        Assert.Equal(
            [alpha.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { CreatedAtIn = [t0, t2] }));
        Assert.Equal(
            [bravo.Id],
            await QueryAsync(new ChannelListFilter { CreatedAtNotIn = [t0, t2] }));
        Assert.Equal(
            [bravo.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { CreatedAtGT = t0 }));
        Assert.Equal(
            [bravo.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { CreatedAtGTE = t1 }));
        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { CreatedAtLT = t1 }));
        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { CreatedAtLTE = t0 }));

        // updated_at mirrors created_at in this fixture.
        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { UpdatedAt = t0 }));
        Assert.Equal(
            [bravo.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { UpdatedAtNEQ = t0 }));
        Assert.Equal(
            [alpha.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { UpdatedAtIn = [t0, t2] }));
        Assert.Equal(
            [bravo.Id],
            await QueryAsync(new ChannelListFilter { UpdatedAtNotIn = [t0, t2] }));
        Assert.Equal(
            [bravo.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { UpdatedAtGT = t0 }));
        Assert.Equal(
            [bravo.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { UpdatedAtGTE = t1 }));
        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { UpdatedAtLT = t1 }));
        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { UpdatedAtLTE = t0 }));

        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { OrderingWeight = 3 }));
        Assert.Equal(
            [bravo.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { OrderingWeightNEQ = 3 }));
        Assert.Equal(
            [bravo.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { OrderingWeightIn = [1, 2] }));
        Assert.Equal(
            [alpha.Id],
            await QueryAsync(new ChannelListFilter { OrderingWeightNotIn = [1, 2] }));
        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { OrderingWeightLT = 2 }));
        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { OrderingWeightLTE = 1 }));
        Assert.Equal(
            [alpha.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { OrderingWeightGT = 1 }));
        Assert.Equal(
            [alpha.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { OrderingWeightGTE = 2 }));
    }

    [Fact]
    public async Task ListAppliesNullableStringPredicates()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelAdminStore(context);

        var disabledAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var expiresAt = new DateTime(2026, 4, 2, 0, 0, 0, DateTimeKind.Utc);

        var alpha = await SeedChannelAsync(
            context,
            "openai",
            "Alpha",
            "enabled",
            baseUrl: "https://alpha.example/v1",
            autoSyncSupportedModels: true,
            autoSyncModelPattern: "^gpt-",
            defaultTestModel: "gpt-4o",
            remark: "primary");
        var bravo = await SeedChannelAsync(
            context,
            "anthropic",
            "Bravo",
            "disabled",
            baseUrl: "https://bravo.example/v1",
            autoSyncSupportedModels: true,
            autoSyncModelPattern: null,
            defaultTestModel: "claude-3",
            errorMessage: "boom",
            autoDisabledAt: disabledAt,
            autoDisableExpiresAt: expiresAt);
        var charlie = await SeedChannelAsync(
            context,
            "openai",
            "Charlie",
            "enabled",
            baseUrl: null,
            autoSyncModelPattern: "claude",
            defaultTestModel: "gpt-4o-mini",
            remark: "secondary");

        async Task<long[]> QueryAsync(ChannelListFilter where)
            => (await store.ListAsync(new ChannelListQuery { Where = where }))
                .Nodes.Select(static row => row.Id).ToArray();

        Assert.Equal(
            [bravo.Id],
            await QueryAsync(new ChannelListFilter { BaseURL = "https://bravo.example/v1" }));
        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { BaseURLNEQ = "https://bravo.example/v1" }));
        Assert.Equal(
            [alpha.Id],
            await QueryAsync(new ChannelListFilter { BaseURLIn = ["https://alpha.example/v1"] }));
        Assert.Equal(
            [bravo.Id],
            await QueryAsync(new ChannelListFilter { BaseURLNotIn = ["https://alpha.example/v1"] }));
        Assert.Equal(
            [bravo.Id],
            await QueryAsync(new ChannelListFilter { BaseURLGT = "https://b" }));
        Assert.Equal(
            [alpha.Id],
            await QueryAsync(new ChannelListFilter { BaseURLLTE = "https://b" }));
        Assert.Equal(
            [alpha.Id, bravo.Id],
            await QueryAsync(new ChannelListFilter { BaseURLContains = "example" }));
        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { BaseURLHasPrefix = "https://alpha" }));
        Assert.Equal(
            [alpha.Id, bravo.Id],
            await QueryAsync(new ChannelListFilter { BaseURLHasSuffix = "/v1" }));
        Assert.Equal(
            [alpha.Id],
            await QueryAsync(new ChannelListFilter { BaseURLEqualFold = "HTTPS://ALPHA.EXAMPLE/V1" }));
        Assert.Equal(
            [alpha.Id],
            await QueryAsync(new ChannelListFilter { BaseURLContainsFold = "ALPHA.EXAMPLE" }));
        Assert.Equal([charlie.Id], await QueryAsync(new ChannelListFilter { BaseURLIsNil = true }));
        Assert.Equal(
            [alpha.Id, bravo.Id],
            await QueryAsync(new ChannelListFilter { BaseURLNotNil = true }));

        Assert.Equal(
            [bravo.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { NameNEQ = "Alpha" }));
        Assert.Equal(
            [alpha.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { NameContains = "ha" }));
        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { NameHasPrefix = "Br" }));
        Assert.Equal([charlie.Id], await QueryAsync(new ChannelListFilter { NameHasSuffix = "lie" }));
        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { NameEqualFold = "alpha" }));
        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { NameContainsFold = "ALPH" }));

        Assert.Equal([charlie.Id], await QueryAsync(new ChannelListFilter { AutoSyncModelPattern = "claude" }));
        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { AutoSyncModelPatternNEQ = "claude" }));
        Assert.Equal(
            [charlie.Id],
            await QueryAsync(new ChannelListFilter { AutoSyncModelPatternIn = ["claude"] }));
        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { AutoSyncModelPatternContains = "gpt" }));
        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { AutoSyncModelPatternHasPrefix = "^" }));
        Assert.Equal([charlie.Id], await QueryAsync(new ChannelListFilter { AutoSyncModelPatternHasSuffix = "claude" }));
        Assert.Equal(
            [alpha.Id],
            await QueryAsync(new ChannelListFilter { AutoSyncModelPatternEqualFold = "^GPT-" }));
        Assert.Equal(
            [alpha.Id],
            await QueryAsync(new ChannelListFilter { AutoSyncModelPatternContainsFold = "GPT" }));
        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { AutoSyncModelPatternIsNil = true }));
        Assert.Equal(
            [alpha.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { AutoSyncModelPatternNotNil = true }));

        Assert.Equal(
            [alpha.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { DefaultTestModelContains = "gpt-4o" }));
        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { DefaultTestModelEqualFold = "CLAUDE-3" }));
        Assert.Equal(
            [bravo.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { DefaultTestModelNEQ = "gpt-4o" }));

        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { ErrorMessage = "boom" }));

        // SQL <> excludes NULL rows; ent's NotEQ behaves the same way, so the
        // two channels without an error message are not returned here.
        Assert.Empty(await QueryAsync(new ChannelListFilter { ErrorMessageNEQ = "boom" }));
        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { ErrorMessageContains = "oo" }));
        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { ErrorMessageEqualFold = "BOOM" }));
        Assert.Equal(
            [bravo.Id],
            await QueryAsync(new ChannelListFilter { ErrorMessageContainsFold = "OO" }));
        Assert.Equal(
            [alpha.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { ErrorMessageIsNil = true }));
        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { ErrorMessageNotNil = true }));

        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { AutoDisabledAt = disabledAt }));
        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { AutoDisabledAtGT = disabledAt.AddHours(-1) }));
        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { AutoDisabledAtLT = expiresAt }));
        Assert.Equal(
            [bravo.Id],
            await QueryAsync(new ChannelListFilter { AutoDisabledAtIn = [disabledAt] }));
        Assert.Equal(
            [alpha.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { AutoDisabledAtIsNil = true }));
        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { AutoDisabledAtNotNil = true }));

        Assert.Equal(
            [bravo.Id],
            await QueryAsync(new ChannelListFilter { AutoDisableExpiresAt = expiresAt }));
        Assert.Equal(
            [bravo.Id],
            await QueryAsync(new ChannelListFilter { AutoDisableExpiresAtGTE = expiresAt }));
        Assert.Equal(
            [alpha.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { AutoDisableExpiresAtIsNil = true }));
        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { AutoDisableExpiresAtNotNil = true }));

        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { Remark = "primary" }));
        Assert.Equal([charlie.Id], await QueryAsync(new ChannelListFilter { RemarkContains = "second" }));
        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { RemarkHasPrefix = "pri" }));
        Assert.Equal([charlie.Id], await QueryAsync(new ChannelListFilter { RemarkHasSuffix = "dary" }));
        Assert.Equal([alpha.Id], await QueryAsync(new ChannelListFilter { RemarkEqualFold = "PRIMARY" }));
        Assert.Equal([charlie.Id], await QueryAsync(new ChannelListFilter { RemarkContainsFold = "SECOND" }));
        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { RemarkIsNil = true }));
        Assert.Equal(
            [alpha.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { RemarkNotNil = true }));
    }

    [Fact]
    public async Task ListAppliesEnumBooleanAndNestedLogicPredicates()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelAdminStore(context);

        var alpha = await SeedChannelAsync(
            context, "openai", "Alpha", "enabled", weight: 3, autoSyncSupportedModels: true);
        var bravo = await SeedChannelAsync(
            context, "anthropic", "Bravo", "disabled", weight: 1, autoSyncSupportedModels: true);
        var charlie = await SeedChannelAsync(
            context, "openai", "Charlie", "enabled", weight: 2);

        async Task<long[]> QueryAsync(ChannelListFilter where)
            => (await store.ListAsync(new ChannelListQuery { Where = where }))
                .Nodes.Select(static row => row.Id).ToArray();

        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { TypeNEQ = "openai" }));
        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { TypeIn = ["anthropic"] }));
        Assert.Equal(
            [alpha.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { TypeNotIn = ["anthropic"] }));

        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { StatusNEQ = "enabled" }));
        Assert.Equal([bravo.Id], await QueryAsync(new ChannelListFilter { StatusIn = ["disabled"] }));
        Assert.Equal(
            [alpha.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter { StatusNotIn = ["disabled"] }));

        Assert.Equal(
            [alpha.Id, bravo.Id],
            await QueryAsync(new ChannelListFilter { AutoSyncSupportedModels = true }));
        Assert.Equal(
            [charlie.Id],
            await QueryAsync(new ChannelListFilter { AutoSyncSupportedModels = false }));
        Assert.Equal(
            [charlie.Id],
            await QueryAsync(new ChannelListFilter { AutoSyncSupportedModelsNEQ = true }));

        Assert.Equal(
            [alpha.Id, bravo.Id],
            await QueryAsync(new ChannelListFilter
            {
                Or =
                [
                    new ChannelListFilter { TypeIn = ["anthropic"] },
                    new ChannelListFilter { OrderingWeightGTE = 3 },
                ],
            }));
        Assert.Equal(
            [bravo.Id],
            await QueryAsync(new ChannelListFilter
            {
                Not = new ChannelListFilter { Status = "enabled" },
            }));
        Assert.Equal(
            [charlie.Id],
            await QueryAsync(new ChannelListFilter
            {
                And =
                [
                    new ChannelListFilter { Type = "openai" },
                    new ChannelListFilter { OrderingWeightLT = 3 },
                ],
            }));
    }

    [Fact]
    public async Task ListAppliesEdgePredicatesWithoutReapplyingTheTargetSoftDelete()
    {
        await using var db = new TestDatabase(fixture);
        await db.MigrateAsync();
        await using var context = db.Factory.CreateDbContext();
        var store = new ChannelAdminStore(context);

        var alpha = await SeedChannelAsync(context, "openai", "Alpha", "enabled");
        var bravo = await SeedChannelAsync(context, "anthropic", "Bravo", "disabled");
        var charlie = await SeedChannelAsync(context, "openai", "Charlie", "enabled");

        await SeedProbeAsync(context, alpha.Id, total: 10, success: 9, tps: 5.5, ttft: 120);
        await SeedProbeAsync(context, bravo.Id, total: 2, success: 1, tps: null, ttft: null);

        var alphaPrice = await SeedPriceAsync(context, alpha.Id, "gpt-4o", "alpha-gpt-4o");
        // Soft-deleted prices still satisfy ent's nested edge predicates: the
        // generated filter never re-applies the target's deleted_at = 0.
        await SeedPriceAsync(context, bravo.Id, "claude-3", "bravo-claude-3", deletedAt: 77);
        await SeedVersionAsync(
            context, alphaPrice.Id, alpha.Id, "gpt-4o", "alpha-gpt-4o-v1", status: "active");

        await SeedQuotaAsync(
            context, charlie.Id, providerType: "codex", status: "available", ready: true);

        async Task<long[]> QueryAsync(ChannelListFilter where)
            => (await store.ListAsync(new ChannelListQuery { Where = where }))
                .Nodes.Select(static row => row.Id).ToArray();

        Assert.Equal(
            [alpha.Id, bravo.Id],
            await QueryAsync(new ChannelListFilter { HasChannelProbes = true }));
        Assert.Equal(
            [charlie.Id],
            await QueryAsync(new ChannelListFilter { HasChannelProbes = false }));
        Assert.Equal(
            [alpha.Id],
            await QueryAsync(new ChannelListFilter
            {
                HasChannelProbesWith = [new ChannelProbeListFilter { TotalRequestCountGTE = 5 }],
            }));
        Assert.Equal(
            [bravo.Id],
            await QueryAsync(new ChannelListFilter
            {
                HasChannelProbesWith = [new ChannelProbeListFilter { AvgTokensPerSecondIsNil = true }],
            }));
        Assert.Equal(
            [bravo.Id],
            await QueryAsync(new ChannelListFilter
            {
                HasChannelProbesWith =
                [
                    new ChannelProbeListFilter
                    {
                        HasChannelWith = [new ChannelListFilter { Type = "anthropic" }],
                    },
                ],
            }));

        Assert.Equal(
            [alpha.Id, bravo.Id],
            await QueryAsync(new ChannelListFilter { HasChannelModelPrices = true }));
        Assert.Equal(
            [charlie.Id],
            await QueryAsync(new ChannelListFilter { HasChannelModelPrices = false }));
        Assert.Equal(
            [alpha.Id],
            await QueryAsync(new ChannelListFilter
            {
                HasChannelModelPricesWith = [new ChannelModelPriceListFilter { ModelIdContains = "gpt" }],
            }));
        Assert.Equal(
            [alpha.Id],
            await QueryAsync(new ChannelListFilter
            {
                HasChannelModelPricesWith = [new ChannelModelPriceListFilter { HasVersions = true }],
            }));
        Assert.Equal(
            [bravo.Id],
            await QueryAsync(new ChannelListFilter
            {
                HasChannelModelPricesWith = [new ChannelModelPriceListFilter { HasVersions = false }],
            }));
        Assert.Equal(
            [alpha.Id],
            await QueryAsync(new ChannelListFilter
            {
                HasChannelModelPricesWith =
                [
                    new ChannelModelPriceListFilter
                    {
                        HasVersionsWith =
                        [
                            new ChannelModelPriceVersionListFilter
                            {
                                Status = "active",
                                EffectiveEndAtIsNil = true,
                            },
                        ],
                    },
                ],
            }));

        Assert.Equal(
            [charlie.Id],
            await QueryAsync(new ChannelListFilter { HasProviderQuotaStatus = true }));
        Assert.Equal(
            [alpha.Id, bravo.Id],
            await QueryAsync(new ChannelListFilter { HasProviderQuotaStatus = false }));
        Assert.Equal(
            [charlie.Id],
            await QueryAsync(new ChannelListFilter
            {
                HasProviderQuotaStatusWith =
                [
                    new ProviderQuotaStatusListFilter
                    {
                        ProviderType = "codex",
                        Status = "available",
                        Ready = true,
                    },
                ],
            }));

        // Edges compose with the nested boolean logic exactly like scalars.
        Assert.Equal(
            [bravo.Id, charlie.Id],
            await QueryAsync(new ChannelListFilter
            {
                Or =
                [
                    new ChannelListFilter { Status = "disabled" },
                    new ChannelListFilter { HasProviderQuotaStatus = true },
                ],
            }));
        Assert.Equal(
            [charlie.Id],
            await QueryAsync(new ChannelListFilter
            {
                Not = new ChannelListFilter
                {
                    Or =
                    [
                        new ChannelListFilter { HasChannelProbes = true },
                        new ChannelListFilter { HasChannelModelPrices = true },
                    ],
                },
            }));
    }

    private static async Task<ChannelProbeRecord> SeedProbeAsync(
        AxonHubDbContext context,
        long channelId,
        long total,
        long success,
        double? tps,
        double? ttft)
    {
        var probe = new ChannelProbeRecord
        {
            ChannelId = channelId,
            TotalRequestCount = total,
            SuccessRequestCount = success,
            AvgTokensPerSecond = tps,
            AvgTimeToFirstTokenMs = ttft,
            Timestamp = 1_700_000_000,
        };

        context.ChannelProbes.Add(probe);
        await context.SaveChangesAsync();
        return probe;
    }

    private static async Task<ChannelModelPriceRecord> SeedPriceAsync(
        AxonHubDbContext context,
        long channelId,
        string modelId,
        string referenceId,
        long deletedAt = 0)
    {
        var price = new ChannelModelPriceRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            DeletedAt = deletedAt,
            ChannelId = channelId,
            ModelId = modelId,
            Price = "{}",
            ReferenceId = referenceId,
        };

        context.ChannelModelPrices.Add(price);
        await context.SaveChangesAsync();
        return price;
    }

    private static async Task<ChannelModelPriceVersionRecord> SeedVersionAsync(
        AxonHubDbContext context,
        long priceId,
        long channelId,
        string modelId,
        string referenceId,
        string status)
    {
        var version = new ChannelModelPriceVersionRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            ChannelId = channelId,
            ModelId = modelId,
            ChannelModelPriceId = priceId,
            Price = "{}",
            Status = status,
            EffectiveStartAt = CreatedAt,
            ReferenceId = referenceId,
        };

        context.ChannelModelPriceVersions.Add(version);
        await context.SaveChangesAsync();
        return version;
    }

    private static async Task<ProviderQuotaStatusRecord> SeedQuotaAsync(
        AxonHubDbContext context,
        long channelId,
        string providerType,
        string status,
        bool ready)
    {
        var quota = new ProviderQuotaStatusRecord
        {
            CreatedAt = CreatedAt,
            UpdatedAt = UpdatedAt,
            DeletedAt = 0,
            ChannelId = channelId,
            ProviderType = providerType,
            Status = status,
            QuotaData = "{}",
            Ready = ready,
            NextCheckAt = CreatedAt.AddHours(1),
            AccountKey = "account",
        };

        context.ProviderQuotaStatuses.Add(quota);
        await context.SaveChangesAsync();
        return quota;
    }

    private static async Task<ChannelRecord> SeedChannelAsync(
        AxonHubDbContext context,
        string type,
        string name,
        string status,
        long weight = 0,
        string? tags = null,
        long deletedAt = 0,
        string? baseUrl = null,
        bool autoSyncSupportedModels = false,
        string? autoSyncModelPattern = "",
        string? defaultTestModel = "",
        string? errorMessage = null,
        DateTime? autoDisabledAt = null,
        DateTime? autoDisableExpiresAt = null,
        string? remark = null,
        DateTime? createdAt = null,
        DateTime? updatedAt = null)
    {
        var channel = new ChannelRecord
        {
            CreatedAt = createdAt ?? CreatedAt,
            UpdatedAt = updatedAt ?? UpdatedAt,
            DeletedAt = deletedAt,
            Type = type,
            BaseUrl = baseUrl,
            Name = name,
            Status = status,
            Credentials = "{}",
            SupportedModels = "null",
            AutoSyncSupportedModels = autoSyncSupportedModels,
            AutoSyncModelPattern = autoSyncModelPattern,
            DefaultTestModel = defaultTestModel ?? string.Empty,
            Tags = tags,
            OrderingWeight = weight,
            ErrorMessage = errorMessage,
            AutoDisabledAt = autoDisabledAt,
            AutoDisableExpiresAt = autoDisableExpiresAt,
            Remark = remark,
        };

        context.Channels.Add(channel);
        await context.SaveChangesAsync();
        return channel;
    }
}
